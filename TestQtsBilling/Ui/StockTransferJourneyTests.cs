using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Inventory;
using QtsBilling.Persistence.Postgres.Entities;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The stock workbench as a shipped screen (specification task 29): dispatch takes the stock out of the source
/// and parks its value in transit exactly once, receipt at the far end is the only thing that stocks the
/// destination and can only happen once, a membership that does not hold both ends is refused the transfer
/// without a single foreign quantity disclosed, and the two screens are wired into every host with rules that
/// never offer to repair a movement-backed figure.
/// </summary>
public sealed class StockTransferJourneyTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public StockTransferJourneyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T29_01_dispatch_reduces_the_source_and_parks_its_value_in_transit_exactly_once()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var inventory = new InventoryApiClient(http);
        var destinationId = await CreateDestinationAsync();

        var before = await SourceBalanceAsync(inventory);
        var summaryBefore = await inventory.GetStockSummaryAsync();

        // The draft the operator names: two branches, one line, and no value - nothing has moved yet.
        var draft = await inventory.CreateTransferAsync(
            new CreateStockTransferRequest(
                _fixture.BusinessA.LocationId,
                destinationId,
                [new StockTransferLineRequest(_fixture.BusinessA.VariantId, 10m)],
                "Replenish the second branch"),
            StockRules.NewKey("transfer", Guid.NewGuid().ToString("N")[..8]));

        Assert.Equal(StockTransferState.Draft, draft.Status);
        Assert.Null(draft.TotalValue);
        Assert.Null(draft.Lines.Single().ValueAtDispatch);
        Assert.Equal(10m, draft.TotalQuantity);
        Assert.Equal(_fixture.BusinessA.LocationId, draft.SourceLocationId);
        Assert.Equal(destinationId, draft.DestinationLocationId);

        // The list the screen reads names the route and prices nothing: a draft has no posted valuation, so
        // the queue prints no money figure for it (whatever zero the server may carry in the raw total).
        var listed = Assert.Single(await inventory.GetTransfersAsync(), row => row.Id == draft.Id);
        Assert.Equal(StockTransferState.Draft, listed.Status);
        Assert.Equal("-", StockRules.QueueValueText(listed));
        Assert.Equal(draft.DestinationLocationName, listed.DestinationLocationName);

        // Dispatch: the source loses the stock, the value is priced at the cost the source held, and the
        // in-transit figure appears - exactly the dispatched quantity, counted once.
        var dispatched = await inventory.DispatchTransferAsync(draft.Id);
        Assert.Equal(StockTransferState.Dispatched, dispatched.Status);
        Assert.Equal(600m, dispatched.TotalValue);
        Assert.Equal(600m, dispatched.Lines.Single().ValueAtDispatch);
        Assert.Equal(60m, dispatched.Lines.Single().UnitCostAtDispatch);
        Assert.True(dispatched.DispatchJournalEntryId is { });

        // A second operator session repeats the same command: the transfer stays what it is, nothing moves twice.
        var secondSession = new InventoryApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA));
        var replayed = await secondSession.DispatchTransferAsync(draft.Id);
        Assert.Equal(StockTransferState.Dispatched, replayed.Status);
        Assert.Equal(600m, replayed.TotalValue);
        Assert.Equal(dispatched.DispatchedAtUtc, replayed.DispatchedAtUtc);

        // The source is down by exactly the line quantity and its value by exactly the valuation.
        var after = await SourceBalanceAsync(inventory);
        Assert.Equal(10m, before.OnHand - after.OnHand);
        var summaryAfter = await inventory.GetStockSummaryAsync();
        Assert.Equal(600m, summaryBefore.TotalValue - summaryAfter.TotalValue);

        // The destination has not been stocked: receipt is the only arrival, and it has not happened.
        var atDestination = await inventory.GetBalancesAsync(
            search: null, cursor: null, pageSize: 50, locationId: destinationId, variantId: _fixture.BusinessA.VariantId);
        Assert.Empty(atDestination.Items);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var source = await db.InventoryBalances.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.LocationId == _fixture.BusinessA.LocationId && row.VariantId == _fixture.BusinessA.VariantId);
            Assert.Equal(after.OnHand, source.OnHand);

            var movements = await db.InventoryMovements.AsNoTracking()
                .Where(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.ReferenceType == "stock_transfer" && row.ReferenceId == draft.Id)
                .ToListAsync();
            Assert.Single(movements);
            var movement = movements[0];
            Assert.Equal(MovementType.TransferOut, movement.MovementType);
            Assert.Equal(-10m, movement.QuantityDelta);
            Assert.Equal(-600m, movement.ValueDelta);
        });
    }

    [Fact]
    public async Task T29_02_receipt_stocks_the_destination_once_whatever_is_replayed()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var inventory = new InventoryApiClient(http);
        var destinationId = await CreateDestinationAsync();

        var destinationBefore = await BalanceAtAsync(inventory, destinationId);
        var summaryBefore = await inventory.GetStockSummaryAsync();
        var transitBefore = await DispatchedValueAsync(inventory);

        var draft = await inventory.CreateTransferAsync(
            new CreateStockTransferRequest(
                _fixture.BusinessA.LocationId,
                destinationId,
                [new StockTransferLineRequest(_fixture.BusinessA.VariantId, 10m)],
                null),
            StockRules.NewKey("transfer", Guid.NewGuid().ToString("N")[..8]));

        // Receipt before dispatch is refused: an arrival cannot be claimed for stock that never left.
        var tooEarly = await CaptureAsync(() => inventory.ReceiveTransferAsync(draft.Id));
        Assert.Equal(ApiErrorCodes.Conflict, tooEarly.Code);
        Assert.Contains("lifecycle state", tooEarly.Message, StringComparison.OrdinalIgnoreCase);

        await inventory.DispatchTransferAsync(draft.Id);

        // In transit between the two commands: the value left the stock summary exactly once and sits between
        // branches, counted once, not yet stocked anywhere.
        var whileInTransit = await inventory.GetTransfersAsync();
        Assert.Equal(transitBefore + 600m, await DispatchedValueAsync(inventory));
        Assert.Equal(600m, whileInTransit.Single(row => row.Id == draft.Id).TotalValue);
        var summaryWhileInTransit = await inventory.GetStockSummaryAsync();
        Assert.Equal(600m, summaryBefore.TotalValue - summaryWhileInTransit.TotalValue);
        Assert.Equal(0m, await BalanceAtAsync(inventory, destinationId));

        // Receipt, then the same receipt again, then the same receipt from a second operator session: the
        // destination gains the stock once and the in-transit value clears once.
        var received = await inventory.ReceiveTransferAsync(draft.Id);
        Assert.Equal(StockTransferState.Received, received.Status);
        Assert.Equal(600m, received.TotalValue);
        Assert.True(received.ReceiveJournalEntryId is { });

        var secondOperator = new InventoryApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA));
        var replayed = await secondOperator.ReceiveTransferAsync(draft.Id);
        Assert.Equal(StockTransferState.Received, replayed.Status);
        Assert.Equal(received.ReceivedAtUtc, replayed.ReceivedAtUtc);

        var destinationAfter = await BalanceAtAsync(inventory, destinationId);
        Assert.Equal(10m, destinationAfter - destinationBefore);

        var summaryAfter = await inventory.GetStockSummaryAsync();
        Assert.Equal(summaryBefore.TotalValue, summaryAfter.TotalValue);
        Assert.Equal(transitBefore, await DispatchedValueAsync(inventory));

        var afterReceipt = await inventory.GetTransfersAsync();
        Assert.Equal(StockTransferState.Received, Assert.Single(afterReceipt, row => row.Id == draft.Id).Status);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var movements = await db.InventoryMovements.AsNoTracking()
                .Where(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.ReferenceType == "stock_transfer" && row.ReferenceId == draft.Id)
                .ToListAsync();
            Assert.Equal(2, movements.Count);
            var inbound = Assert.Single(movements, row => row.MovementType == MovementType.TransferIn);
            Assert.Equal(10m, inbound.QuantityDelta);
            Assert.Equal(600m, inbound.ValueDelta);
            Assert.Equal(destinationId, inbound.LocationId);
        });
    }

    [Fact]
    public async Task T29_03_a_membership_holding_one_end_is_refused_the_transfer_and_sees_no_foreign_stock()
    {
        var ownerHttp = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var inventory = new InventoryApiClient(ownerHttp);
        var destinationId = await CreateDestinationAsync();
        var destinationName = await _fixture.WithFreshContextAsync(async db =>
            (await db.BusinessLocations.AsNoTracking().SingleAsync(row => row.Id == destinationId)).Name);

        var draft = await inventory.CreateTransferAsync(
            new CreateStockTransferRequest(
                _fixture.BusinessA.LocationId,
                destinationId,
                [new StockTransferLineRequest(_fixture.BusinessA.VariantId, 10m)],
                null),
            StockRules.NewKey("transfer", Guid.NewGuid().ToString("N")[..8]));
        await inventory.DispatchTransferAsync(draft.Id);

        var membershipId = _fixture.Users.OwnerA.MembershipId;
        try
        {
            // The same operator, now holding only the source branch. The membership is saved on its own before
            // the grant: writing the grant bumps the membership's version through the authorization trigger,
            // so the two writes must never share one save.
            await _fixture.WithFreshContextAsync(async db =>
            {
                var membership = await db.BusinessMemberships.SingleAsync(row => row.Id == membershipId);
                membership.AllLocations = false;
                await db.SaveChangesAsync();
                return true;
            });
            await _fixture.WithFreshContextAsync(async db =>
            {
                if (!await db.BusinessMembershipLocations.AnyAsync(grant => grant.MembershipId == membershipId
                        && grant.LocationId == _fixture.BusinessA.LocationId))
                {
                    db.BusinessMembershipLocations.Add(new BusinessMembershipLocation
                    {
                        BusinessId = _fixture.BusinessA.BusinessId,
                        MembershipId = membershipId,
                        LocationId = _fixture.BusinessA.LocationId,
                    });
                    await db.SaveChangesAsync();
                }
                return true;
            });

            var scoped = new InventoryApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA));

            // The transfer list never mentions a branch pair the reader does not hold: no foreign route, no
            // foreign quantity, not even the transfer's existence.
            var list = await scoped.GetTransfersAsync();
            Assert.DoesNotContain(list, row => row.Id == draft.Id);
            Assert.DoesNotContain(list, row => row.DestinationLocationId == destinationId);

            // Reading it directly is refused, and the refusal does not name what the reader may not see.
            var refusedRead = await CaptureAsync(() => scoped.GetTransferAsync(draft.Id));
            Assert.Equal(403, refusedRead.StatusCode);
            Assert.Equal(ApiErrorCodes.Forbidden, refusedRead.Code);
            Assert.DoesNotContain(destinationName, refusedRead.Message, StringComparison.OrdinalIgnoreCase);

            // Creating, dispatching or receiving across the boundary is refused whole: nothing moves.
            var refusedCreate = await CaptureAsync(() => scoped.CreateTransferAsync(
                new CreateStockTransferRequest(
                    _fixture.BusinessA.LocationId,
                    destinationId,
                    [new StockTransferLineRequest(_fixture.BusinessA.VariantId, 1m)],
                    null),
                StockRules.NewKey("transfer", Guid.NewGuid().ToString("N")[..8])));
            Assert.Equal(ApiErrorCodes.Forbidden, refusedCreate.Code);

            var refusedDispatch = await CaptureAsync(() => scoped.DispatchTransferAsync(draft.Id));
            Assert.Equal(ApiErrorCodes.Forbidden, refusedDispatch.Code);

            // Narrowing the membership raised its authorization revision, so every session issued before the
            // change has lost its authority (spec D02/F07), and this membership is itself the restricted one.
            // A Cashier outside the restriction - a fresh sign-in carrying the current revision - reads the
            // transfer back exactly as it was: dispatched once, unmoved by the refusals.
            var verifier = new InventoryApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Cashier));
            var stillThere = await verifier.GetTransferAsync(draft.Id);
            Assert.Equal(StockTransferState.Dispatched, stillThere.Status);
            Assert.Equal(600m, stillThere.TotalValue);

            // It is in transit, not received: the destination still holds nothing of it.
            Assert.Equal(0m, await BalanceAtAsync(verifier, destinationId));

            // Their own branch's stock is still readable - the boundary is the transfer, not a blanket lockout.
            var own = await scoped.GetBalancesAsync(
                search: null, cursor: null, pageSize: 50, locationId: _fixture.BusinessA.LocationId, variantId: _fixture.BusinessA.VariantId);
            Assert.Single(own.Items);
            Assert.True(own.Items[0].OnHand > 0m);
        }
        finally
        {
            // Restore in the same two-step order: the membership first, then the grants it no longer needs.
            await _fixture.WithFreshContextAsync(async db =>
            {
                var membership = await db.BusinessMemberships.SingleAsync(row => row.Id == membershipId);
                membership.AllLocations = true;
                await db.SaveChangesAsync();
                return true;
            });
            await _fixture.WithFreshContextAsync(async db =>
            {
                db.BusinessMembershipLocations.RemoveRange(
                    await db.BusinessMembershipLocations.Where(grant => grant.MembershipId == membershipId).ToListAsync());
                await db.SaveChangesAsync();
                return true;
            });
        }

        // Restoring the grant raised the revision once more, so the proof is a brand-new sign-in: the very
        // same transfer reads back - the refusal was the scope, not the record.
        var afterRestore = new InventoryApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA));
        var restored = await afterRestore.GetTransferAsync(draft.Id);
        Assert.Equal(StockTransferState.Dispatched, restored.Status);
    }

    [Fact]
    public async Task T29_the_stock_and_transfer_screens_are_wired_into_every_host_that_can_show_them()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        Assert.Contains("inventory/stock", typeof(Stock)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Select(route => route.Template.Trim('/')));
        Assert.Contains("inventory/transfers", typeof(Transfers)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Select(route => route.Template.Trim('/')));
        Assert.Contains("inventory/transfers/{Id:guid}", typeof(Transfers)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Select(route => route.Template.Trim('/')));

        foreach (var href in new[] { "inventory/stock", "inventory/transfers" })
        {
            var row = Assert.Single(ShellNavigation.Routes, route => route.Href == href);
            Assert.Equal(ShellAvailability.Available, row.Availability);
            Assert.Equal(Permissions.ProductsRead, row.RequiredPermission);
            Assert.Null(row.ArrivalNote);
        }

        foreach (var page in new[] { typeof(Stock), typeof(Transfers) })
        {
            var injected = page
                .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
                .Select(property => property.PropertyType)
                .Where(type => type.IsInterface)
                .Distinct()
                .ToArray();

            Assert.Contains(typeof(IInventoryApiClient), injected);
            Assert.Contains(typeof(IAccountApiClient), injected);

            using var scope = _fixture.Factory.Services.CreateScope();
            var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
            Assert.True(missing.Length == 0, $"the server host renders {page.Name} but registers none for: {string.Join(", ", missing)}");
        }

        foreach (var startup in new[] { "QtsWebApp/QtsWebApp.Client/Program.cs", "QtsBilling.Maui/MauiProgram.cs" })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, startup));
            Assert.Contains("IInventoryApiClient", source, StringComparison.Ordinal);
            Assert.Contains("InventoryApiClient", source, StringComparison.Ordinal);
        }

        // The shipped markup asks for no key, reaches for no public resource, and hardcodes no scale or clock.
        var markup = string.Concat(
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Inventory/Stock.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Inventory/Stock.razor.cs")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Inventory/Transfers.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Inventory/Transfers.razor.cs")));

        foreach (var forbidden in new[]
        {
            "Transfer id", "Variant id", "Journal entry id", "access_token",
            "http://", "https://", "ToString(\"N2\")", "DateTime.Today", "Guid.Parse",
        })
        {
            Assert.DoesNotContain(forbidden, markup, StringComparison.Ordinal);
        }

        // The custody, provisional, reconciliation and discrepancy rules all appear in what ships.
        Assert.Contains("inventory-in-transit account", markup, StringComparison.Ordinal);
        Assert.Contains("never mixed into on-hand", markup, StringComparison.Ordinal);
        Assert.Contains("custody, not a copy", markup, StringComparison.Ordinal);
        Assert.Contains("never repairs a movement-backed", markup, StringComparison.Ordinal);
        Assert.Contains("reviewed adjustment", markup, StringComparison.Ordinal);

        // Rules and words the screens speak: states and movements in the operator's language, a refusal that
        // names the action clearing it, and a key that is bounded and distinct per intention.
        Assert.Equal("draft, not yet dispatched", StockRules.StateText(StockTransferState.Draft));
        Assert.Equal("dispatched, in transit", StockRules.StateText(StockTransferState.Dispatched));
        Assert.Equal("transfer received", StockRules.MovementText(StockMovementKind.TransferIn));
        Assert.Contains("refused whole", StockRules.RefusalSentence(
            new ApiException(409, ApiErrorCodes.InsufficientStock, "short"), "the dispatch"), StringComparison.Ordinal);
        Assert.Contains("both locations", StockRules.RefusalSentence(
            new ApiException(403, ApiErrorCodes.Forbidden, "blocked"), "this transfer"), StringComparison.Ordinal);

        // Only a refusal that proves nothing was written releases the held draft; an unknown answer keeps it.
        Assert.True(StockRules.CommittedNothing(ApiErrorCodes.InsufficientStock));
        Assert.True(StockRules.CommittedNothing(ApiErrorCodes.SameKeyDifferentPayload));
        Assert.False(StockRules.CommittedNothing(ApiErrorCodes.InternalError));
        Assert.False(StockRules.CommittedNothing(code: null));

        var key = StockRules.NewKey("transfer", Guid.NewGuid().ToString("N")[..8]);
        Assert.True(key.Length <= 64);
        Assert.StartsWith("transfer-", key, StringComparison.Ordinal);
        Assert.NotEqual(key, StockRules.NewKey("transfer", Guid.NewGuid().ToString("N")[..8]));
    }

    private async Task<decimal> BalanceAtAsync(InventoryApiClient inventory, Guid locationId)
    {
        var page = await inventory.GetBalancesAsync(
            search: null, cursor: null, pageSize: 50, locationId: locationId, variantId: _fixture.BusinessA.VariantId);
        return page.Items.Sum(row => row.OnHand);
    }

    /// <summary>The value the transfers screen counts as between branches: dispatched, valued, not yet received.</summary>
    private static async Task<decimal> DispatchedValueAsync(InventoryApiClient inventory)
        => (await inventory.GetTransfersAsync())
            .Where(row => row.Status == StockTransferState.Dispatched)
            .Sum(row => row.TotalValue ?? 0m);

    private async Task<InventoryBalanceDto> SourceBalanceAsync(InventoryApiClient inventory)
    {
        var page = await inventory.GetBalancesAsync(
            search: null, cursor: null, pageSize: 50, locationId: _fixture.BusinessA.LocationId, variantId: _fixture.BusinessA.VariantId);
        return Assert.Single(page.Items);
    }

    private async Task<Guid> CreateDestinationAsync()
    {
        var destinationId = Guid.CreateVersion7();
        await _fixture.WithFreshContextAsync(async db =>
        {
            db.BusinessLocations.Add(new BusinessLocation
            {
                Id = destinationId,
                BusinessId = _fixture.BusinessA.BusinessId,
                Name = $"Branch {destinationId:N}",
                Code = $"BR-{destinationId:N}"[..20],
                IsActive = true,
            });
            var membership = await db.BusinessMemberships.SingleAsync(row => row.Id == _fixture.Users.OwnerA.MembershipId);
            membership.AllLocations = true;
            await db.SaveChangesAsync();
        });
        return destinationId;
    }

    private static async Task<ApiException> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("the request should have been refused, but it succeeded");
    }
}
