using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Customers;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Sales;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The sales history and the controlled partial refund as an operator works them (specification task 21,
/// T21.01 - T21.03 plus the shipping cases). Every call is the typed contract the shared screens inject, against
/// the real host, so the cumulative figures, the racing refund, the authority boundary and the bounded search are
/// proven on the server's own records rather than on a mocked response.
/// </summary>
public sealed class SalesRefundJourneyTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture = fixture;

    [Fact]
    public async Task T21_01_refunding_the_remainder_closes_the_cumulative_cash_tax_and_cost_exactly()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var sales = new SaleApiClient(owner);
        var registers = new CashRegisterApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var register = await registers.CreateRegisterAsync(
            new CreateCashRegisterRequest(_fixture.BusinessA.LocationId, $"T21 cumulative {suffix}"));
        var registerId = register.Id;
        await OpenDrawerAsync(registers, registerId, 1_000m);

        var posted = await sales.PostSaleAsync(CashSale(4m, registerId), "t21-01-sale-" + suffix);
        Assert.Equal(460m, posted.TotalAmount);

        var detail = await sales.GetSaleAsync(posted.SaleId);
        var line = Assert.Single(detail.Lines);
        Assert.Equal(4m, line.Quantity);
        Assert.Equal(60m, detail.TaxAmount);

        // 1. One unit back: the server's own figure, and its position reports what is left.
        var first = await sales.RefundSaleAsync(
            posted.SaleId,
            new RefundSaleRequest("goods returned in the box", null, registerId, [new RefundLineRequest(line.Id, 1m)]),
            "t21-01-first-" + suffix);
        Assert.Equal(115m, first.Amount);
        Assert.Equal("Restock", Assert.Single(first.Lines).Disposition);

        var afterOne = await sales.GetRefundPositionAsync(posted.SaleId);
        var positionLine = Assert.Single(afterOne.Lines);
        Assert.Equal(1m, positionLine.RefundedQuantity);
        Assert.Equal(3m, positionLine.RemainingQuantity);
        Assert.Equal(345m, positionLine.RemainingAmount);
        Assert.Equal(345m, afterOne.RemainingRefundableAmount);
        Assert.Equal(115m, afterOne.RefundedAmount);
        Assert.True(afterOne.RefundableAtRegister, afterOne.RefusalReason);

        // The cost the screen is shown is the cost the sale took out at - the server's snapshot, never a reprice.
        var unitCost = Assert.Single((await _fixture.WithFreshContextAsync(async db => await db.SaleLines.AsNoTracking()
            .Where(row => row.SaleId == posted.SaleId).Select(row => row.UnitCostSnapshot).ToListAsync())));
        Assert.Equal(unitCost, positionLine.UnitCost);

        // 2. More than the remaining quantity is not a second refund - it is refused, and nothing is committed.
        var excess = await Assert.ThrowsAsync<ApiException>(() => sales.RefundSaleAsync(
            posted.SaleId,
            new RefundSaleRequest("too much", null, registerId, [new RefundLineRequest(line.Id, 4m)]),
            "t21-01-excess-" + suffix));
        Assert.Equal(400, excess.StatusCode);
        Assert.Contains("remaining refundable quantity", excess.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, await _fixture.WithFreshContextAsync(db => db.Refunds.AsNoTracking()
            .CountAsync(refund => refund.SaleId == posted.SaleId)));

        // 3. The remainder closes the sale: the cumulative money, tax and quantity equal the original exactly,
        //    including the rounding residual only the last part carries.
        var second = await sales.RefundSaleAsync(
            posted.SaleId,
            new RefundSaleRequest("the rest of the box", null, registerId, [new RefundLineRequest(line.Id, 3m)]),
            "t21-01-rest-" + suffix);
        Assert.Equal(345m, second.Amount);

        var history = await sales.GetRefundsAsync(posted.SaleId);
        Assert.Equal(2, history.Count);
        Assert.Equal(460m, history.Sum(refund => refund.Amount));
        Assert.Equal(4m, history.Sum(refund => refund.Lines.Sum(row => row.Quantity)));
        Assert.Equal(60m, history.Sum(refund => refund.Lines.Sum(row => row.TaxAmount)));
        Assert.Equal(4m * unitCost, history.Sum(refund => refund.Lines.Sum(row => row.Quantity * row.UnitCost)));

        // The sale is closed for further refunds, and the server says so with its own code.
        var closed = await sales.GetRefundPositionAsync(posted.SaleId);
        Assert.False(closed.RefundableAtRegister);
        Assert.Equal(ApiErrorCodes.AlreadyRefunded, closed.RefusalCode);
        Assert.Equal(0m, closed.RemainingRefundableAmount);
        Assert.Equal(0m, Assert.Single(closed.Lines).RemainingQuantity);

        var effects = await _fixture.WithFreshContextAsync(async db =>
        {
            var sale = await db.Sales.AsNoTracking().SingleAsync(row => row.Id == posted.SaleId);
            var payment = await db.Payments.AsNoTracking().SingleAsync(row => row.SaleId == posted.SaleId);

            return (
                SaleStatus: sale.Status,
                PaymentStatus: payment.Status,
                RefundRows: await db.Refunds.AsNoTracking().CountAsync(refund => refund.SaleId == posted.SaleId),
                RefundCash: await db.CashRegisterMovements.AsNoTracking()
                    .Where(movement => movement.SaleId == posted.SaleId && movement.MovementType == CashMovementType.Refund)
                    .SumAsync(movement => movement.Amount),
                ReversalQuantity: await db.InventoryMovements.AsNoTracking()
                    .Where(movement => movement.BusinessId == _fixture.BusinessA.BusinessId
                        && movement.MovementType == MovementType.SaleReversal
                        && db.RefundLines.Any(row => row.Id == movement.ReferenceId
                            && db.Refunds.Any(refund => refund.SaleId == posted.SaleId && refund.Id == row.RefundId)))
                    .SumAsync(movement => movement.QuantityDelta),
                RefundJournals: await db.JournalEntries.AsNoTracking().CountAsync(entry => entry.SourceType == JournalSourceType.Refund
                    && db.Refunds.Any(refund => refund.SaleId == posted.SaleId && refund.Id == entry.SourceId)));
        });

        Assert.Equal(SaleStatus.Reversed, effects.SaleStatus);
        Assert.Equal(QtsBilling.Persistence.Postgres.PaymentStatus.Refunded, effects.PaymentStatus);
        Assert.Equal(2, effects.RefundRows);
        Assert.Equal(460m, effects.RefundCash);
        Assert.Equal(4m, effects.ReversalQuantity);
        Assert.Equal(2, effects.RefundJournals);

        // The drawer gave back exactly the money it took, once each: 1 000 float + 460 in - 460 out.
        var position2 = await registers.GetCurrentSessionAsync(registerId);
        Assert.Equal(1_000m, position2.ExpectedCash);

        // And the money the two refunds paid out is the drawer's own two movement rows, never a re-derived guess.
        Assert.Equal([115m, 345m], history.Select(refund => refund.Amount).OrderBy(amount => amount).ToArray());
    }

    [Fact]
    public async Task T21_02_two_operators_refunding_the_same_remaining_line_commit_one_payout_only()
    {
        using var firstOperator = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        using var secondOperator = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var registers = new CashRegisterApiClient(firstOperator);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var register = await registers.CreateRegisterAsync(
            new CreateCashRegisterRequest(_fixture.BusinessA.LocationId, $"T21 race {suffix}"));
        var registerId = register.Id;
        await OpenDrawerAsync(registers, registerId, 1_000m);

        var posted = await new SaleApiClient(firstOperator).PostSaleAsync(CashSale(2m, registerId), "t21-02-sale-" + suffix);
        var saleLineId = await _fixture.WithFreshContextAsync(async db => await db.SaleLines.AsNoTracking()
            .Where(row => row.SaleId == posted.SaleId).Select(row => row.Id).SingleAsync());

        // Both operators ask for the whole remaining quantity of the same line at the same time, each with their
        // own key - precisely the double payout the cumulative protection exists to stop.
        async Task<(bool Committed, string Detail)> TryRefundAsync(HttpClient client, string key)
        {
            try
            {
                var refund = await new SaleApiClient(client).RefundSaleAsync(
                    posted.SaleId,
                    new RefundSaleRequest("returned by the customer", null, registerId, [new RefundLineRequest(saleLineId, 2m)]),
                    key);

                return (true, refund.RefundNumber);
            }
            catch (ApiException exception)
            {
                return (false, $"{exception.StatusCode} {exception.Code}");
            }
        }

        var firstTask = TryRefundAsync(firstOperator, "t21-02-race-a-" + suffix);
        var secondTask = TryRefundAsync(secondOperator, "t21-02-race-b-" + suffix);
        await Task.WhenAll(firstTask, secondTask);

        var first = await firstTask;
        var second = await secondTask;

        Assert.Single(new[] { first, second }.Where(outcome => outcome.Committed).ToList());
        Assert.Single(new[] { first, second }.Where(outcome => !outcome.Committed).ToList());

        // One refund row, one drawer movement, one journal - the money left the till once.
        var effects = await _fixture.WithFreshContextAsync(async db => (
            Refunds: await db.Refunds.AsNoTracking().CountAsync(refund => refund.SaleId == posted.SaleId),
            RefundCash: await db.CashRegisterMovements.AsNoTracking()
                .Where(movement => movement.SaleId == posted.SaleId && movement.MovementType == CashMovementType.Refund)
                .SumAsync(movement => movement.Amount),
            Journals: await db.JournalEntries.AsNoTracking().CountAsync(entry => entry.SourceType == JournalSourceType.Refund
                && db.Refunds.Any(refund => refund.SaleId == posted.SaleId && refund.Id == entry.SourceId)),
            Reversals: await db.InventoryMovements.AsNoTracking().CountAsync(movement => movement.MovementType == MovementType.SaleReversal
                && db.RefundLines.Any(row => row.Id == movement.ReferenceId
                    && db.Refunds.Any(refund => refund.SaleId == posted.SaleId && refund.Id == row.RefundId)))));

        Assert.Equal(1, effects.Refunds);
        Assert.Equal(230m, effects.RefundCash);
        Assert.Equal(1, effects.Journals);
        Assert.Equal(1, effects.Reversals);

        // A retry of the winner's own key answers the same refund instead of paying it out again (specification D06).
        var winner = first.Committed ? firstOperator : secondOperator;
        var winnerKey = first.Committed ? "t21-02-race-a-" + suffix : "t21-02-race-b-" + suffix;
        var replay = await new SaleApiClient(winner).RefundSaleAsync(
            posted.SaleId,
            new RefundSaleRequest("returned by the customer", null, registerId, [new RefundLineRequest(saleLineId, 2m)]),
            winnerKey);

        Assert.Equal(230m, replay.Amount);
        Assert.Equal(1, await _fixture.WithFreshContextAsync(db => db.Refunds.AsNoTracking()
            .CountAsync(refund => refund.SaleId == posted.SaleId)));

        // The same key with a different body is a conflict, not a second refund.
        var changed = await Assert.ThrowsAsync<ApiException>(() => new SaleApiClient(winner).RefundSaleAsync(
            posted.SaleId,
            new RefundSaleRequest("a different story", null, registerId, [new RefundLineRequest(saleLineId, 2m)]),
            winnerKey));
        Assert.Equal(409, changed.StatusCode);
        Assert.Equal(ApiErrorCodes.SameKeyDifferentPayload, changed.Code);
    }

    [Fact]
    public async Task T21_03_a_cashier_without_the_refund_authority_reads_the_history_and_is_refused_the_payout()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        using var cashier = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Cashier);
        var ownerView = new SaleApiClient(owner);
        var registers = new CashRegisterApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var register = await registers.CreateRegisterAsync(
            new CreateCashRegisterRequest(_fixture.BusinessA.LocationId, $"T21 authority {suffix}"));
        var registerId = register.Id;
        await OpenDrawerAsync(registers, registerId, 500m);

        var posted = await ownerView.PostSaleAsync(CashSale(1m, registerId), "t21-03-sale-" + suffix);
        var line = Assert.Single((await ownerView.GetSaleAsync(posted.SaleId)).Lines);

        // The counter genuinely cannot refund: the authority the refund route asks for is not in its session.
        var cashierPermissions = (await new AccountApiClient(cashier).GetSessionAsync())!.Permissions;
        Assert.DoesNotContain(Permissions.SalesRefund, cashierPermissions);
        Assert.Contains(Permissions.SalesPost, cashierPermissions);

        // A manager's part refund first, so the refusal below is about authority and not about an empty sale.
        var settled = await ownerView.RefundSaleAsync(
            posted.SaleId,
            new RefundSaleRequest("half back, manager", null, registerId, [new RefundLineRequest(line.Id, 0.5m)]),
            "t21-03-manager-" + suffix);
        Assert.Equal(57.5m, settled.Amount);

        var cashierView = new SaleApiClient(cashier);

        // Everything the history screen reads stays readable with the sale authority.
        var page = await cashierView.GetSalesAsync(search: posted.SaleNumber, pageSize: 50);
        Assert.Contains(page.Items, row => row.SaleId == posted.SaleId);

        var detail = await cashierView.GetSaleAsync(posted.SaleId);
        Assert.Equal(posted.SaleNumber, detail.SaleNumber);

        var position = await cashierView.GetRefundPositionAsync(posted.SaleId);
        Assert.Equal(0.5m, Assert.Single(position.Lines).RefundedQuantity);
        Assert.Equal(57.5m, position.RemainingRefundableAmount);

        var history = await cashierView.GetRefundsAsync(posted.SaleId);
        Assert.Single(history);

        // The payout itself is refused, and it commits nothing.
        var refusal = await Assert.ThrowsAsync<ApiException>(() => cashierView.RefundSaleAsync(
            posted.SaleId,
            new RefundSaleRequest("cashier trying the same return", null, registerId, [new RefundLineRequest(line.Id, 0.5m)]),
            "t21-03-cashier-" + suffix));
        Assert.Equal(403, refusal.StatusCode);

        Assert.Single(await _fixture.WithFreshContextAsync(db => db.Refunds.AsNoTracking()
            .Where(refund => refund.SaleId == posted.SaleId).ToListAsync()));

        // The screen says what is missing rather than offering a button that would be refused.
        var note = SalesRules.RefundAuthorityNote(false);
        Assert.Contains(Permissions.SalesRefund, note, StringComparison.Ordinal);
        Assert.Contains("stay readable", note, StringComparison.Ordinal);
        Assert.Equal(string.Empty, SalesRules.RefundAuthorityNote(true));

        // It was the authority and nothing else: the same request from the manager posts.
        var afterAllowance = await ownerView.RefundSaleAsync(
            posted.SaleId,
            new RefundSaleRequest("the rest back", null, registerId, [new RefundLineRequest(line.Id, 0.5m)]),
            "t21-03-manager-rest-" + suffix);
        Assert.Equal(57.5m, afterAllowance.Amount);
    }

    [Fact]
    public async Task T21_04_the_history_is_bounded_by_number_customer_date_and_page_and_names_unconfirmed_money()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var sales = new SaleApiClient(owner);
        var customers = new CustomerApiClient(owner);
        var registers = new CashRegisterApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var register = await registers.CreateRegisterAsync(
            new CreateCashRegisterRequest(_fixture.BusinessA.LocationId, $"T21 history {suffix}"));
        var registerId = register.Id;
        await OpenDrawerAsync(registers, registerId, 1_000m);

        var customer = await customers.CreateCustomerAsync(new SaveCustomerRequest(
            $"Counter {suffix}", $"counter-{suffix}@t21.test", null, null));

        var counterSale = await sales.PostSaleAsync(CashSale(1m, registerId, customer.Id), "t21-04-customer-" + suffix);
        var walkInSale = await sales.PostSaleAsync(CashSale(1m, registerId), "t21-04-walkin-" + suffix);

        // Search by the number the till roll shows, executed over the server rather than filtered in the browser.
        var byNumber = await sales.GetSalesAsync(search: counterSale.SaleNumber);
        Assert.Contains(byNumber.Items, row => row.SaleId == counterSale.SaleId);
        Assert.DoesNotContain(byNumber.Items, row => row.SaleId == walkInSale.SaleId);

        // Search by the customer record the operator chose by name; the row carries that name back.
        var byCustomer = await sales.GetSalesAsync(customerId: customer.Id);
        Assert.NotEmpty(byCustomer.Items);
        Assert.All(byCustomer.Items, row =>
        {
            Assert.Equal(customer.Id, row.CustomerId);
            Assert.Equal(customer.Name, row.CustomerName);
        });

        Assert.Contains(byCustomer.Items, row => row.SaleId == counterSale.SaleId);
        Assert.DoesNotContain(byCustomer.Items, row => row.SaleId == walkInSale.SaleId);

        // The date window is the server's own instant comparison, bounded to what was asked for.
        var future = await sales.GetSalesAsync(from: DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.DoesNotContain(future.Items, row => row.SaleId == counterSale.SaleId);

        var untilNow = await sales.GetSalesAsync(to: DateTimeOffset.UtcNow);
        Assert.Contains(untilNow.Items, row => row.SaleId == counterSale.SaleId);

        // Paging walks the whole list without repeats or misses, however many pages it takes.
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await sales.GetSalesAsync(cursor: cursor, pageSize: 2);
            pages++;
            seen.AddRange(page.Items.Select(row => row.SaleId));
            cursor = page.NextCursor;
        }
        while (cursor is not null && pages < 40);

        Assert.True(pages >= 2, $"the page size of two never had to move on: {pages} page(s)");
        Assert.Equal(seen.Count, seen.Distinct().Count());

        var total = await _fixture.WithFreshContextAsync(db => db.Sales.AsNoTracking()
            .CountAsync(sale => sale.BusinessId == _fixture.BusinessA.BusinessId));
        Assert.Equal(total, seen.Count);

        // A payment the server confirmed can never be relabelled as uncollected - the database itself refuses it -
        // so no row in this history can present provisional device money as a settled server sale
        // (specification task 21, recipe step 1; D07's "posted money is immutable").
        var rewind = await Assert.ThrowsAsync<DbUpdateException>(async () => await _fixture.WithFreshContextAsync(async db =>
        {
            var payment = await db.Payments.SingleAsync(row => row.SaleId == walkInSale.SaleId);
            payment.Status = QtsBilling.Persistence.Postgres.PaymentStatus.Pending;
            await db.SaveChangesAsync();
        }));
        Assert.Contains("cannot be rewound to pending", rewind.InnerException?.Message ?? rewind.Message, StringComparison.OrdinalIgnoreCase);

        var stillSettled = Assert.Single((await sales.GetSalesAsync(search: walkInSale.SaleNumber)).Items);
        Assert.Equal(QtsBilling.Contracts.Sales.PaymentStatus.Confirmed, stillSettled.PaymentStatus);
        Assert.False(SalesRules.IsUncollected(stillSettled));

        // What an unconfirmed payment does look like, stated by the screen's own rule: not cash in a till, and no
        // payout is offered for it.
        var pending = stillSettled with { PaymentStatus = QtsBilling.Contracts.Sales.PaymentStatus.Pending };
        Assert.True(SalesRules.IsUncollected(pending));
        Assert.Contains("not cash in a till", SalesRules.StateText(pending), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task T21_05_the_sales_screens_ship_as_shared_pages_wired_in_every_host()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        Assert.Contains(typeof(Sales).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>(), route => route.Template.Trim('/') == "sales");
        Assert.Contains(typeof(SaleDetail).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>(), route => route.Template.Trim('/') == "sales/{SaleId:guid}");

        var historyRow = ShellNavigation.Routes.Single(route => route.Href == "sales");
        Assert.Equal(ShellAvailability.Available, historyRow.Availability);
        Assert.Equal(Permissions.SalesPost, historyRow.RequiredPermission);

        // Every contract the two screens inject has to resolve in the server host and be named in the browser and
        // terminal startups, or a navigation would hand the operator a dead form.
        var injected = new[]
        {
            typeof(ISaleApiClient),
            typeof(ICustomerApiClient),
            typeof(IInventoryApiClient),
            typeof(QtsBilling.Contracts.Administration.IBusinessSetupApiClient),
            typeof(IAccountApiClient),
        };

        using var scope = _fixture.Factory.Services.CreateScope();
        var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the sales screens but registers none for: {string.Join(", ", missing)}");

        foreach (var startup in new[] { @"QtsWebApp\QtsWebApp.Client\Program.cs", @"QtsBilling.Maui\MauiProgram.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, startup));
            var unnamed = injected.Select(type => type.Name).Where(name => !source.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(unnamed.Length == 0, $"{startup} does not name: {string.Join(", ", unnamed)}");
        }

        // The refund command is keyed, and a key the server can hold fits its header bound.
        var key = SalesRules.NewRefundKey("S-2026-0001");
        Assert.True(key.Length <= 64, $"the refund key is {key.Length} characters");
        Assert.StartsWith("refund-S-2026-0001-", key, StringComparison.Ordinal);

        // The screen never asks a human for an identifier, never reaches for a public resource, and never puts a
        // token in a URL: the reprint is read from the same contract as everything else (finding F20's rule).
        var markup = string.Concat(
            await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui\Pages\Sales\Sales.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui\Pages\Sales\Sales.razor.cs")),
            await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui\Pages\Sales\SaleDetail.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui\Pages\Sales\SaleDetail.razor.cs")));

        Assert.DoesNotContain("Sale id", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Refund id", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", markup, StringComparison.Ordinal);

        // Both pages state whose figures are theirs: the server's position is the limit, the screen's is a preview.
        Assert.Contains("What the server still allows back", markup, StringComparison.Ordinal);
        Assert.Contains("Preview of this screen", markup, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private SaleCaptureRequest CashSale(decimal quantity, Guid registerId, Guid? customerId = null)
        => new(
            _fixture.BusinessA.LocationId,
            customerId,
            "QAR",
            [new SaleLineRequest(_fixture.BusinessA.VariantId, quantity, 100m, 0m, 0.15m, null)
            {
                TaxMode = QtsModels.ValueObjects.CommercialTaxMode.Exclusive,
            }],
            new PaymentCaptureRequest("Cash", quantity * 115m, quantity * 115m, null),
            "task 21 refund journey",
            registerId);

    private async Task<CashSessionDetailDto> OpenDrawerAsync(CashRegisterApiClient registers, Guid registerId, decimal floatAmount)
        => await registers.OpenSessionAsync(
            registerId,
            new OpenCashRegisterSessionRequest(floatAmount, "T21 test float"),
            "t21-open-" + Guid.NewGuid().ToString("N"));
}
