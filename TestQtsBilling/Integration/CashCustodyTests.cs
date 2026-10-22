using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Sales;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Register custody and the close rules a drawer must obey (specification task 19, T19.01 - T19.03). The drawer is
/// its movements, unresolved money must be named and handed to a person before a drawer may go, a counted
/// difference is posted rather than absorbed, and every command is replayable under one key without moving twice.
/// Setup the shipped browser paths cannot reach - a terminal that has never pushed, cash taken and never confirmed,
/// an operation left awaiting review - is written the way the ledger writes it, and every assertion is made on the
/// stored rows rather than on a response code.
/// </summary>
public sealed class CashCustodyTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture = fixture;

    [Fact]
    public async Task T19_01_a_sale_racing_a_close_is_either_counted_or_refused_and_never_lost()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var registerId = await CreateRegisterAsync(owner, $"T19 race {Guid.NewGuid().ToString("N")[..8]}");
        var sessionId = await OpenDrawerAsync(owner, registerId, 50m);

        // Both commands are in flight at once; each is awaited rather than blocked on, so a deadlock in either
        // path shows up as a failing test rather than a hung run.
        var saleTask = PostSaleAsync(owner, registerId, "race" + Guid.NewGuid().ToString("N")[..8]);
        var closeTask = CloseDrawerAsync(owner, registerId, sessionId, 50m);

        var saleResponse = await saleTask;
        var closeResponse = await closeTask;

        // The close always lands: the drawer is judged against whatever the movements had become, and a sale that
        // arrived after the session closed is refused rather than written into a closed drawer.
        var closeBody = await closeResponse.Content.ReadAsStringAsync();
        Assert.True(closeResponse.StatusCode == HttpStatusCode.OK, $"the close was refused: {(int)closeResponse.StatusCode} {closeBody}");

        var saleSucceeded = saleResponse.StatusCode == HttpStatusCode.Created;

        if (!saleSucceeded)
        {
            var refusal = await saleResponse.Content.ReadAsStringAsync();

            // The sale lost the race and is refused by the drawer's own state, with the code the POS already
            // understands - never accepted into a closed session uncounted.
            Assert.Equal(HttpStatusCode.Conflict, saleResponse.StatusCode);
            Assert.Contains(ApiErrorCodes.CashRegisterNotOpen, refusal, StringComparison.Ordinal);
        }

        var stored = await _fixture.WithFreshContextAsync(async db =>
        {
            var session = await db.CashRegisterSessions.AsNoTracking().SingleAsync(row => row.Id == sessionId);
            var movements = await db.CashRegisterMovements.AsNoTracking()
                .Where(movement => movement.CashRegisterSessionId == sessionId)
                .Select(movement => new { movement.MovementType, movement.Amount })
                .ToListAsync();

            var expected = movements
                .Where(movement => movement.MovementType != CashMovementType.Closing)
                .Sum(movement => DrawerEffect(movement.MovementType, movement.Amount));

            return (
                session.ExpectedCash,
                session.CountedCash,
                session.Variance,
                SaleCash: movements.Where(m => m.MovementType == CashMovementType.SalePayment).Sum(m => m.Amount),
                ExpectedFromMovements: expected,
                SalePayments: movements.Count(m => m.MovementType == CashMovementType.SalePayment));
        });

        // Whichever command held the session lock first, the drawer's stored expected cash is exactly the movements
        // it now holds, and a sale that arrived after the close is refused rather than counted nowhere.
        Assert.Equal(saleSucceeded ? 165m : 50m, stored.ExpectedCash);

        Assert.Equal(stored.ExpectedFromMovements, stored.ExpectedCash);
        Assert.Equal(stored.CountedCash - stored.ExpectedCash, stored.Variance);

        if (saleSucceeded)
        {
            Assert.Equal(1, stored.SalePayments);
            Assert.Equal(115m, stored.SaleCash);
        }
        else
        {
            Assert.Equal(0, stored.SalePayments);
            Assert.Equal(0m, stored.SaleCash);
        }
    }

    [Fact]
    public async Task T19_02_unconfirmed_cash_and_a_silent_terminal_hold_the_drawer_open_until_each_is_carried_forward()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var cashier = _fixture.Users.Cashier;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registerId = await CreateRegisterAsync(owner, $"T19 custody {suffix}");
        var sessionId = await OpenDrawerAsync(owner, registerId, 20m);
        var locationId = (await _fixture.WithFreshContextAsync(db => db.CashRegisters.AsNoTracking()
            .SingleAsync(row => row.Id == registerId))).LocationId;

        // A sale posted normally, so the drawer holds real cash.
        await PostSaleAsync(owner, registerId, "good" + suffix);

        var cashPaymentId = Guid.CreateVersion7();
        var deviceId = Guid.CreateVersion7();
        var operationRowId = Guid.CreateVersion7();
        var reviewSaleId = Guid.CreateVersion7();

        // Three kinds of unresolved money: cash this drawer took that the server never confirmed, an operation a
        // terminal pushed that sits awaiting review against a posted sale, and a terminal of this branch that has
        // never reported at all.
        await _fixture.WithFreshContextAsync(async db =>
        {
            db.Payments.Add(new Payment
            {
                Id = cashPaymentId,
                BusinessId = _fixture.BusinessA.BusinessId,
                Method = PaymentMethod.Cash,
                Status = QtsBilling.Persistence.Postgres.PaymentStatus.Pending,
                Amount = 30m,
                UnallocatedAmount = 30m,
                Currency = "QAR",
                CashReceived = 30m,
                Reference = $"offline receipt {suffix}",
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                CreatedByUserId = cashier.UserId,
                CashRegisterSessionId = sessionId,
            });

            db.DeviceRegistrations.Add(new DeviceRegistration
            {
                Id = deviceId,
                BusinessId = _fixture.BusinessA.BusinessId,
                LocationId = locationId,
                Name = $"T19 silent terminal {suffix}",
                Platform = "windows",
                Status = DeviceStatus.Active,
                SchemaVersion = 1,
                EnrolledAtUtc = DateTimeOffset.UtcNow.AddHours(-2),
            });

            db.Sales.Add(new Sale
            {
                Id = reviewSaleId,
                BusinessId = _fixture.BusinessA.BusinessId,
                LocationId = locationId,
                SaleNumber = $"T19REVIEW{suffix}",
                Status = SaleStatus.NeedsReview,
                Currency = "QAR",
                SubtotalAmount = 20m,
                TaxAmount = 5m,
                TotalAmount = 25m,
                PaymentStatus = QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed,
                PostedAtUtc = DateTimeOffset.UtcNow,
                PostedByUserId = cashier.UserId,
            });

            db.ProcessedOperations.Add(new ProcessedOperation
            {
                Id = operationRowId,
                BusinessId = _fixture.BusinessA.BusinessId,
                DeviceId = deviceId,
                OperationId = Guid.NewGuid(),
                OperationType = "cash-sale",
                PayloadHash = new string('a', 64),
                Outcome = SyncOutcome.NeedsReview,
                ServerSaleId = reviewSaleId,
                ProcessedAtUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync();
        });

        var custody = await GetCustodyAsync(owner, registerId, sessionId);

        Assert.False(custody.Closable);
        Assert.Equal(3, custody.Unresolved.Count);
        Assert.Contains("carried forward", custody.BlockReason, StringComparison.Ordinal);
        Assert.Contains(custody.Unresolved, item => item.Kind == CashCustodySourceState.DeviceCustody
            && item.DeviceLastSeenAtUtc is null
            && item.Explanation.Contains("never reported", StringComparison.Ordinal));
        Assert.Contains(custody.Unresolved, item => item.Kind == CashCustodySourceState.DeviceOperation && item.Amount == 25m);
        Assert.Contains(custody.Devices, device => device.ContactedSinceSessionOpened == false);

        // Closing over it is refused, and the refusal is the custody rule with its own code.
        var blocked = await CloseDrawerAsync(owner, registerId, sessionId, 135m);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var blockedBody = await blocked.Content.ReadAsStringAsync();
        Assert.Contains(ApiErrorCodes.ApprovalRequired, blockedBody, StringComparison.Ordinal);
        Assert.Contains("unresolved", blockedBody, StringComparison.OrdinalIgnoreCase);

        // The drawer and its movements are untouched by the refusal.
        Assert.Equal(135m, (await GetCustodyAsync(owner, registerId, sessionId)).ExpectedCash);

        // An understated carry-forward is refused with the figure the server can see, so a hand-over cannot
        // quietly shrink what the business is owed.
        var understated = await PostJsonAsync(owner, TransferRoute(registerId, sessionId),
            new CreateCustodyTransferRequest(cashier.UserId, "understated attempt",
                [
                    new CustodyTransferItemRequest(CashCustodySourceState.ProvisionalPayment, cashPaymentId, 30m),
                    new CustodyTransferItemRequest(CashCustodySourceState.DeviceOperation, operationRowId, 10m),
                    new CustodyTransferItemRequest(CashCustodySourceState.DeviceCustody, deviceId, 0m),
                ]),
            "t19-under-" + suffix);
        Assert.Equal(HttpStatusCode.Conflict, understated.StatusCode);
        var underBody = await understated.Content.ReadAsStringAsync();
        Assert.Contains("understates", underBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("25", underBody, StringComparison.Ordinal);

        Assert.Equal(0, await ResolutionCountAsync(sessionId));

        // Handing money to oneself is refused: the carrying and the receiving must be two people.
        var toOneself = await PostJsonAsync(owner, TransferRoute(registerId, sessionId),
            new CreateCustodyTransferRequest(_fixture.Users.OwnerA.UserId, "handing it to myself",
                [new CustodyTransferItemRequest(CashCustodySourceState.ProvisionalPayment, cashPaymentId, 30m)]),
            "t19-self-" + suffix);
        Assert.Equal(HttpStatusCode.BadRequest, toOneself.StatusCode);
        Assert.Contains("receivingCustodianUserId", await toOneself.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A complete, honestly-stated carry-forward names all three and gives them to a named custodian.
        var carried = await PostJsonAsync(owner, TransferRoute(registerId, sessionId),
            new CreateCustodyTransferRequest(cashier.UserId, "night handover at the counter",
                [
                    new CustodyTransferItemRequest(CashCustodySourceState.ProvisionalPayment, cashPaymentId, 30m),
                    new CustodyTransferItemRequest(CashCustodySourceState.DeviceOperation, operationRowId, 25m),
                    new CustodyTransferItemRequest(CashCustodySourceState.DeviceCustody, deviceId, 60m),
                ]),
            "t19-carry-" + suffix);
        Assert.Equal(HttpStatusCode.Created, carried.StatusCode);

        var afterCarry = await GetCustodyAsync(owner, registerId, sessionId);

        Assert.True(afterCarry.Closable, afterCarry.BlockReason);
        Assert.Equal(3, afterCarry.CarryForwards.Count);
        Assert.StartsWith("CF-", afterCarry.CarryForwards[0].CarryForwardNumber, StringComparison.Ordinal);
        Assert.All(afterCarry.CarryForwards, forward => Assert.Equal(cashier.Email, forward.ReceivingCustodianName));
        Assert.Equal(3, await ResolutionCountAsync(sessionId));

        // Carried money never entered the expected cash, and carrying it changed no drawer arithmetic.
        Assert.Equal(135m, afterCarry.ExpectedCash);
        Assert.Equal(30m, afterCarry.ProvisionalCash);

        // Closing now counts a drawer that is short by 5.00, and that difference is posted to the ledger.
        var closed = await CloseDrawerAsync(owner, registerId, sessionId, 130m);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var session = await _fixture.WithFreshContextAsync(db => db.CashRegisterSessions.AsNoTracking()
            .SingleAsync(row => row.Id == sessionId));

        Assert.Equal(CashRegisterSessionStatus.Closed, session.Status);
        Assert.Equal(130m, session.CountedCash);
        Assert.Equal(135m, session.ExpectedCash);
        Assert.Equal(-5m, session.Variance);

        var entry = await _fixture.WithFreshContextAsync(db => db.JournalEntries.AsNoTracking()
            .SingleOrDefaultAsync(journal => journal.BusinessId == _fixture.BusinessA.BusinessId
                && journal.SourceType == JournalSourceType.CashOverShort && journal.SourceId == sessionId));

        Assert.NotNull(entry);
        Assert.Equal(5m, entry!.TotalDebit);
        Assert.Equal(5m, entry.TotalCredit);

        var lines = await _fixture.WithFreshContextAsync(async db => await db.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntryId == entry.Id)
            .OrderBy(line => line.LineNo)
            .Select(line => new { line.AccountId, Code = db.Accounts.Where(a => a.Id == line.AccountId).Select(a => a.Code).FirstOrDefault(), line.DebitAmount, line.CreditAmount })
            .ToListAsync());

        Assert.Equal(2, lines.Count);

        // The short side is the business's own cash-over/short account and the cash account gives the difference
        // back - a real pairing, never a balancing plug.
        var shortLine = Assert.Single(lines, line => line.Code == "5300");
        var cashLine = Assert.Single(lines, line => line.Code == "1000");
        Assert.Equal(5m, shortLine.DebitAmount);
        Assert.Equal(5m, cashLine.CreditAmount);

        // The carry-forward rows survive the close as the record of who answers for what.
        var resolutions = await _fixture.WithFreshContextAsync(async db => await db.CashCustodyResolutions.AsNoTracking()
            .Where(row => row.CashRegisterSessionId == sessionId)
            .OrderBy(row => row.SourceReference)
            .Select(row => new { row.CarryForwardNumber, row.SourceKind, row.Amount, row.ReceivingCustodianUserId, row.AuthorisedByUserId })
            .ToListAsync());

        Assert.All(resolutions, row => Assert.Equal(resolutions[0].CarryForwardNumber, row.CarryForwardNumber));
        Assert.All(resolutions, row => Assert.Equal(cashier.UserId, row.ReceivingCustodianUserId));
        Assert.All(resolutions, row => Assert.Equal(_fixture.Users.OwnerA.UserId, row.AuthorisedByUserId));
        Assert.Equal(115m, resolutions.Sum(row => row.Amount));
    }

    [Fact]
    public async Task T19_03_a_replayed_command_moves_the_drawer_once_and_a_changed_body_under_the_key_conflicts()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registerId = await CreateRegisterAsync(owner, $"T19 replay {suffix}");

        // A drawer command without a key is refused rather than run unguarded.
        var noKey = await owner.PostAsJsonAsync(
            $"/api/v1/pos/registers/{registerId:D}/sessions",
            new OpenCashRegisterSessionRequest(10m, "no key attempt"));
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Contains("Idempotency-Key", await noKey.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var openKey = "t19-open-" + suffix;
        var firstOpen = await PostJsonAsync(owner, $"/api/v1/pos/registers/{registerId:D}/sessions",
            new OpenCashRegisterSessionRequest(10m, "T19 replay float"), openKey);
        var secondOpen = await PostJsonAsync(owner, $"/api/v1/pos/registers/{registerId:D}/sessions",
            new OpenCashRegisterSessionRequest(10m, "T19 replay float"), openKey);

        Assert.Equal(HttpStatusCode.Created, firstOpen.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondOpen.StatusCode);

        var firstSessionId = JsonDocument.Parse(await firstOpen.Content.ReadAsStringAsync())
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();
        var secondSessionId = JsonDocument.Parse(await secondOpen.Content.ReadAsStringAsync())
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        Assert.Equal(firstSessionId, secondSessionId);

        var sessionRows = await _fixture.WithFreshContextAsync(db => db.CashRegisterSessions.AsNoTracking()
            .CountAsync(row => row.CashRegisterId == registerId));
        Assert.Equal(1, sessionRows);

        // A paid-in replayed with the same key moves the drawer once and answers with the same movement row.
        var movementKey = "t19-paidin-" + suffix;
        var movementRequest = new CashMovementRequest("cashIn", 7.5m, "Float correction replay test");
        var movementRoute = $"/api/v1/pos/registers/{registerId:D}/sessions/{secondSessionId:D}/movements";
        var firstMovement = await PostJsonAsync(owner, movementRoute, movementRequest, movementKey);
        var replayMovement = await PostJsonAsync(owner, movementRoute, movementRequest, movementKey);

        Assert.Equal(HttpStatusCode.Created, firstMovement.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replayMovement.StatusCode);
        Assert.Equal(
            JsonDocument.Parse(await firstMovement.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid(),
            JsonDocument.Parse(await replayMovement.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());

        var paidInRows = await _fixture.WithFreshContextAsync(db => db.CashRegisterMovements.AsNoTracking()
            .CountAsync(movement => movement.CashRegisterSessionId == secondSessionId
                && movement.MovementType == CashMovementType.CashIn));
        Assert.Equal(1, paidInRows);

        Assert.Equal(17.5m, (await GetCustodyAsync(owner, registerId, secondSessionId)).ExpectedCash);

        // The same key with a different body is a conflict, and nothing moves.
        var changed = await PostJsonAsync(owner, movementRoute,
            new CashMovementRequest("cashIn", 9m, "Float correction replay test"), movementKey);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Contains(ApiErrorCodes.SameKeyDifferentPayload, await changed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(17.5m, (await GetCustodyAsync(owner, registerId, secondSessionId)).ExpectedCash);

        // A close replayed with its key leaves one closed session, one closing movement and one over/short journal.
        var closeKey = "t19-close-" + suffix;
        var closeRequest = new CloseCashRegisterSessionRequest(15m, "T19 replay close");
        var closeRoute = $"/api/v1/pos/registers/{registerId:D}/sessions/{secondSessionId:D}/close";
        var firstClose = await PostJsonAsync(owner, closeRoute, closeRequest, closeKey);
        var replayClose = await PostJsonAsync(owner, closeRoute, closeRequest, closeKey);

        Assert.Equal(HttpStatusCode.OK, firstClose.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayClose.StatusCode);

        var afterClose = await _fixture.WithFreshContextAsync(async db =>
        {
            var session = await db.CashRegisterSessions.AsNoTracking().SingleAsync(row => row.Id == secondSessionId);

            return (
                session.Status,
                session.Variance,
                Closing: await db.CashRegisterMovements.AsNoTracking()
                    .CountAsync(movement => movement.CashRegisterSessionId == secondSessionId
                        && movement.MovementType == CashMovementType.Closing),
                Journals: await db.JournalEntries.AsNoTracking()
                    .CountAsync(journal => journal.BusinessId == _fixture.BusinessA.BusinessId
                        && journal.SourceType == JournalSourceType.CashOverShort
                        && journal.SourceId == secondSessionId));
        });

        Assert.Equal(CashRegisterSessionStatus.Closed, afterClose.Status);
        Assert.Equal(-2.5m, afterClose.Variance);
        Assert.Equal(1, afterClose.Closing);
        Assert.Equal(1, afterClose.Journals);
    }

    [Fact]
    public async Task T19_04_a_carry_forward_that_names_nothing_or_invents_an_obligation_is_refused_before_anything_is_written()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registerId = await CreateRegisterAsync(owner, $"T19 refusal {suffix}");
        var sessionId = await OpenDrawerAsync(owner, registerId, 15m);

        var custody = await GetCustodyAsync(owner, registerId, sessionId);
        Assert.True(custody.Closable);
        Assert.Empty(custody.Unresolved);

        var empty = await PostJsonAsync(owner, TransferRoute(registerId, sessionId),
            new CreateCustodyTransferRequest(_fixture.Users.Cashier.UserId, "nothing to hand over", []),
            "t19-empty-" + suffix);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("names nothing", await empty.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var noReason = await PostJsonAsync(owner, TransferRoute(registerId, sessionId),
            new CreateCustodyTransferRequest(_fixture.Users.Cashier.UserId, "  ",
                [new CustodyTransferItemRequest(CashCustodySourceState.DeviceCustody, Guid.NewGuid(), 5m)]),
            "t19-noreason-" + suffix);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var stranger = await PostJsonAsync(owner, TransferRoute(registerId, sessionId),
            new CreateCustodyTransferRequest(Guid.NewGuid(), "carrying to a person who holds no position here",
                [new CustodyTransferItemRequest(CashCustodySourceState.DeviceCustody, Guid.NewGuid(), 5m)]),
            "t19-stranger-" + suffix);
        Assert.Equal(HttpStatusCode.BadRequest, stranger.StatusCode);
        Assert.Contains("receivingCustodianUserId", await stranger.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // An obligation the drawer does not have cannot be carried either.
        var invented = await PostJsonAsync(owner, TransferRoute(registerId, sessionId),
            new CreateCustodyTransferRequest(_fixture.Users.Cashier.UserId, "inventing an obligation",
                [new CustodyTransferItemRequest(CashCustodySourceState.DeviceCustody, Guid.NewGuid(), 100m)]),
            "t19-invented-" + suffix);
        Assert.Equal(HttpStatusCode.BadRequest, invented.StatusCode);
        Assert.Contains("not an unresolved obligation", await invented.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Nothing was written by any of the four refusals, and the drawer is still open and still closable.
        Assert.Equal(0, await ResolutionCountAsync(sessionId));

        var stillOpen = await GetCustodyAsync(owner, registerId, sessionId);
        Assert.True(stillOpen.Closable);
        Assert.Equal(15m, stillOpen.ExpectedCash);

        // The cashiers' own drawer commands answer 403 for a viewer, so the custody rule is not the only guard.
        using var viewer = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Viewer);
        var forbidden = await viewer.GetAsync($"/api/v1/pos/registers/{registerId}/sessions/{sessionId}/custody");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private static string TransferRoute(Guid registerId, Guid sessionId)
        => $"/api/v1/pos/registers/{registerId:D}/sessions/{sessionId:D}/custody-transfers";

    /// <summary>The drawer effect of one movement kind, the same rule the server's derivation uses.</summary>
    private static decimal DrawerEffect(CashMovementType type, decimal amount) => type switch
    {
        CashMovementType.CashOut or CashMovementType.Refund => -amount,
        CashMovementType.Closing => 0m,
        _ => amount,
    };

    private async Task<int> ResolutionCountAsync(Guid sessionId)
        => await _fixture.WithFreshContextAsync(db => db.CashCustodyResolutions.AsNoTracking()
            .CountAsync(row => row.CashRegisterSessionId == sessionId));

    private async Task<Guid> CreateRegisterAsync(HttpClient client, string name)
    {
        var response = await PostJsonAsync(client, "/api/v1/pos/registers",
            new CreateCashRegisterRequest(_fixture.BusinessA.LocationId, name),
            "t19-register-" + Guid.NewGuid().ToString("N"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"the register could not be created: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> OpenDrawerAsync(HttpClient client, Guid registerId, decimal floatAmount)
    {
        var response = await PostJsonAsync(client,
            $"/api/v1/pos/registers/{registerId:D}/sessions",
            new OpenCashRegisterSessionRequest(floatAmount, "T19 test float"),
            "t19-open-" + Guid.NewGuid().ToString("N"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"the drawer could not be opened: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("session").GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> CloseDrawerAsync(
        HttpClient client, Guid registerId, Guid sessionId, decimal counted)
        => await PostJsonAsync(client,
            $"/api/v1/pos/registers/{registerId:D}/sessions/{sessionId:D}/close",
            new CloseCashRegisterSessionRequest(counted, "T19 counted at close"),
            "t19-close-" + Guid.NewGuid().ToString("N"));

    private async Task<DrawerCustodyDto> GetCustodyAsync(HttpClient client, Guid registerId, Guid sessionId)
    {
        var response = await client.GetAsync($"/api/v1/pos/registers/{registerId:D}/sessions/{sessionId:D}/custody");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the custody position could not be read: {(int)response.StatusCode} {body}");

        return await response.Content.ReadFromJsonAsync<DrawerCustodyDto>(ContractJson.Options)
            ?? throw new InvalidOperationException("the custody position answered without a body");
    }

    private async Task<HttpResponseMessage> PostSaleAsync(HttpClient client, Guid registerId, string reference)
    {
        var request = new SaleCaptureRequest(
            _fixture.BusinessA.LocationId,
            CustomerId: null,
            Currency: "QAR",
            Lines: [new SaleLineRequest(_fixture.BusinessA.VariantId, 1m, 100m, 0m, 0.15m, null)],
            Payment: new PaymentCaptureRequest("Cash", 115m, 115m, reference),
            Note: "task 19 custody test",
            RegisterId: registerId);

        return await PostJsonAsync(client, "/api/v1/pos/sales", request, "t19-sale-" + reference);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client, string route, object payload, string idempotencyKey)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(payload, options: ContractJson.Options),
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);

        return await client.SendAsync(message);
    }
}
