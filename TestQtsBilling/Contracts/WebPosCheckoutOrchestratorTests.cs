using Microsoft.JSInterop;
using Moq;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sales;
using QtsModels.ValueObjects;
using QtsWebApp.Client.Services;
using Xunit;

namespace TestQtsBilling.Contracts;

/// <summary>
/// The browser checkout orchestration (specification task 20): the exact key and body a sale was sent with must
/// outlive the page that sent them, and a lost answer must be reported as an unknown outcome rather than as a
/// refusal or a success. The fake storage below stands in for the browser's own, so nothing here touches a network.
/// </summary>
public sealed class WebPosCheckoutOrchestratorTests
{
    [Fact]
    public async Task Capture_posts_the_shared_inclusive_amount_to_the_selected_open_drawer()
    {
        var locationId = Guid.CreateVersion7();
        var registerId = Guid.CreateVersion7();
        var operationId = Guid.CreateVersion7();
        var session = new CashRegisterSessionDto(
            Guid.CreateVersion7(), registerId, "USD", CashRegisterSessionState.Open, 50m,
            DateTimeOffset.UtcNow, Guid.CreateVersion7(), null, null, null, null, null, null);
        var registers = new Mock<ICashRegisterApiClient>(MockBehavior.Strict);
        registers.Setup(client => client.GetRegistersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CashRegisterDto(registerId, locationId, "Front till", true, session)]);

        SaleCaptureRequest? captured = null;
        string? capturedKey = null;
        var sales = new Mock<ISaleApiClient>(MockBehavior.Strict);
        var posted = new SaleCaptureResult(Guid.CreateVersion7(), "S-1002", "USD", 115m, 15m, DateTimeOffset.UtcNow, false);
        sales.Setup(client => client.PostSaleAsync(It.IsAny<SaleCaptureRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<SaleCaptureRequest, string, CancellationToken>((request, key, _) =>
            {
                captured = request;
                capturedKey = key;
            })
            .ReturnsAsync(posted);

        var orchestrator = new WebPosCheckoutOrchestrator(
            Mock.Of<IProductApiClient>(), Mock.Of<IInventoryApiClient>(), registers.Object, sales.Object, new FakeBrowserStorage());
        var draft = new LocalCashSaleDraft(operationId, 120m,
        [
            new LocalSaleLineDraft(0, Guid.CreateVersion7(), 1m, 115m, 0.15m, 15m, 0m, 7)
            {
                TaxMode = CommercialTaxMode.Inclusive,
                PricingPolicyVersion = "3",
            },
        ]);

        var result = await orchestrator.CaptureCashSaleAsync(locationId, registerId, draft);

        Assert.Same(posted, result);
        Assert.NotNull(captured);
        Assert.Equal(locationId, captured!.LocationId);
        Assert.Equal(registerId, captured.RegisterId);
        Assert.Equal("USD", captured.Currency);
        Assert.Equal(115m, captured.Payment.Amount);
        Assert.Equal(120m, captured.Payment.CashReceived);
        Assert.Equal(CommercialTaxMode.Inclusive, captured.Lines[0].TaxMode);
        Assert.Equal("3", captured.Lines[0].PricingPolicyVersion);
        Assert.Equal(operationId.ToString("N"), capturedKey);
        sales.VerifyAll();
        registers.VerifyAll();
    }

    [Fact]
    public async Task Capture_refuses_a_closed_drawer_before_posting()
    {
        var locationId = Guid.CreateVersion7();
        var registerId = Guid.CreateVersion7();
        var registers = new Mock<ICashRegisterApiClient>(MockBehavior.Strict);
        registers.Setup(client => client.GetRegistersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CashRegisterDto(registerId, locationId, "Front till", true, null)]);
        var sales = new Mock<ISaleApiClient>(MockBehavior.Strict);
        var orchestrator = new WebPosCheckoutOrchestrator(
            Mock.Of<IProductApiClient>(), Mock.Of<IInventoryApiClient>(), registers.Object, sales.Object, new FakeBrowserStorage());
        var draft = new LocalCashSaleDraft(Guid.CreateVersion7(), 10m,
        [new LocalSaleLineDraft(0, Guid.CreateVersion7(), 1m, 10m, 0m, 0m, 0m, 1)]);

        var exception = await Assert.ThrowsAsync<ApiException>(
            () => orchestrator.CaptureCashSaleAsync(locationId, registerId, draft));

        Assert.Equal(ApiErrorCodes.CashRegisterNotOpen, exception.Code);
        sales.Verify(client => client.PostSaleAsync(It.IsAny<SaleCaptureRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_sale_that_never_heard_back_is_reported_as_unknown_and_keeps_its_exact_key_and_body()
    {
        var locationId = Guid.CreateVersion7();
        var registerId = Guid.CreateVersion7();
        var operationId = Guid.CreateVersion7();
        var storage = new FakeBrowserStorage();
        var session = new CashRegisterSessionDto(
            Guid.CreateVersion7(), registerId, "QAR", CashRegisterSessionState.Open, 0m,
            DateTimeOffset.UtcNow, Guid.CreateVersion7(), null, null, null, null, null, null);
        var registers = new Mock<ICashRegisterApiClient>(MockBehavior.Strict);
        registers.Setup(client => client.GetRegistersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CashRegisterDto(registerId, locationId, "Front till", true, session)]);

        var sales = new Mock<ISaleApiClient>(MockBehavior.Strict);
        sales.Setup(client => client.PostSaleAsync(It.IsAny<SaleCaptureRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var orchestrator = new WebPosCheckoutOrchestrator(
            Mock.Of<IProductApiClient>(), Mock.Of<IInventoryApiClient>(), registers.Object, sales.Object, storage);
        var draft = new LocalCashSaleDraft(operationId, 20m,
        [new LocalSaleLineDraft(0, Guid.CreateVersion7(), 1m, 20m, 0m, 0m, 0m, 4)]);

        // What the checkout screen does before it sends: keep the envelope of the attempt it is about to make.
        await orchestrator.SavePendingAttemptAsync(
            new PendingCheckoutAttempt(operationId, locationId, registerId, draft, DateTimeOffset.UtcNow));

        var exception = await Assert.ThrowsAsync<ApiException>(
            () => orchestrator.CaptureCashSaleAsync(locationId, registerId, draft));

        // Not a refusal and not a success: the outcome is unknown, and the message says what the operator may do.
        Assert.Equal(ApiErrorCodes.TransientRetry, exception.Code);
        Assert.Equal(408, exception.StatusCode);
        Assert.Contains("unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(operationId.ToString("N"), exception.Message, StringComparison.Ordinal);

        // The envelope survives the failure untouched, so the next load of the page has the same attempt to resolve
        // with the same key and the same single line - the state in which no new sale may be started.
        var stored = await orchestrator.ReadPendingAttemptAsync();

        Assert.NotNull(stored);
        Assert.Equal(operationId, stored!.OperationId);
        Assert.Equal(registerId, stored.RegisterId);
        Assert.Equal(locationId, stored.LocationId);
        Assert.Equal(draft.CashReceived, stored.Draft.CashReceived);
        Assert.Single(stored.Draft.Lines);
        Assert.Equal(draft.Lines[0].VariantId, stored.Draft.Lines[0].VariantId);
        Assert.Equal(draft.Lines[0].UnitPrice, stored.Draft.Lines[0].UnitPrice);
        Assert.Equal(draft.Lines[0].ExpectedCatalogRevision, stored.Draft.Lines[0].ExpectedCatalogRevision);
    }

    [Fact]
    public async Task The_pending_attempt_outlives_the_page_that_saved_it_and_clears_only_when_the_sale_is_resolved()
    {
        var locationId = Guid.CreateVersion7();
        var registerId = Guid.CreateVersion7();
        var operationId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var saleId = Guid.CreateVersion7();
        var storage = new FakeBrowserStorage();
        var draft = new LocalCashSaleDraft(operationId, 30m,
        [
            new LocalSaleLineDraft(0, variantId, 2m, 11.5m, 0.15m, 3.45m, 1.55m, 9)
            {
                TaxMode = CommercialTaxMode.Inclusive,
                PricingPolicyVersion = "v9",
            },
        ]);

        // A save through one orchestrator instance is readable by another: that is what a refresh or a restart is.
        var writer = new WebPosCheckoutOrchestrator(
            Mock.Of<IProductApiClient>(), Mock.Of<IInventoryApiClient>(),
            Mock.Of<ICashRegisterApiClient>(), Mock.Of<ISaleApiClient>(), storage);
        await writer.SavePendingAttemptAsync(new PendingCheckoutAttempt(operationId, locationId, registerId, draft, DateTimeOffset.UtcNow));

        var reloaded = new WebPosCheckoutOrchestrator(
            Mock.Of<IProductApiClient>(), Mock.Of<IInventoryApiClient>(),
            Mock.Of<ICashRegisterApiClient>(), Mock.Of<ISaleApiClient>(), storage);
        var pending = await reloaded.ReadPendingAttemptAsync();

        Assert.NotNull(pending);
        Assert.Equal(operationId, pending!.OperationId);
        Assert.Equal(locationId, pending.LocationId);
        Assert.Equal(registerId, pending.RegisterId);
        Assert.Equal(draft.CashReceived, pending.Draft.CashReceived);

        // The line is read back field by field: a serialised list is the same basket, not the same array instance.
        var restored = Assert.Single(pending.Draft.Lines);
        Assert.Equal(draft.Lines[0].VariantId, restored.VariantId);
        Assert.Equal(draft.Lines[0].Quantity, restored.Quantity);
        Assert.Equal(draft.Lines[0].UnitPrice, restored.UnitPrice);
        Assert.Equal(draft.Lines[0].TaxRate, restored.TaxRate);
        Assert.Equal(draft.Lines[0].Tax, restored.Tax);
        Assert.Equal(1.55m, restored.Discount);
        Assert.Equal(9L, restored.ExpectedCatalogRevision);
        Assert.Equal(CommercialTaxMode.Inclusive, restored.TaxMode);
        Assert.Equal("v9", restored.PricingPolicyVersion);

        // What is written is only the attempt: no token, no cookie, no card material.
        var raw = storage.Values[WebPosCheckoutOrchestrator.PendingAttemptKey]!;
        Assert.Contains(operationId.ToString("D"), raw, StringComparison.Ordinal);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pan", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", raw, StringComparison.OrdinalIgnoreCase);

        await reloaded.ClearPendingAttemptAsync();
        Assert.Null(await reloaded.ReadPendingAttemptAsync());

        // A resolved sale answers from the same key and body, and the receipt shown afterwards is the server's.
        var session = new CashRegisterSessionDto(
            Guid.CreateVersion7(), registerId, "QAR", CashRegisterSessionState.Open, 0m,
            DateTimeOffset.UtcNow, Guid.CreateVersion7(), null, null, null, null, null, null);
        var registers = new Mock<ICashRegisterApiClient>(MockBehavior.Strict);
        registers.Setup(client => client.GetRegistersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CashRegisterDto(registerId, locationId, "Front till", true, session)]);

        var keys = new List<string>();
        var bodies = new List<SaleCaptureRequest>();
        var sales = new Mock<ISaleApiClient>(MockBehavior.Strict);
        sales.Setup(client => client.PostSaleAsync(It.IsAny<SaleCaptureRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<SaleCaptureRequest, string, CancellationToken>((request, key, _) =>
            {
                keys.Add(key);
                bodies.Add(request);
            })
            .ReturnsAsync(new SaleCaptureResult(saleId, "S-2001", "QAR", 23m, 3m, DateTimeOffset.UtcNow, false));
        var receipt = new SaleReceiptDto(
            saleId, "S-2001", "Posted", null, "Trader", null, "Main", null, "QAR", DateTimeOffset.UtcNow, "2026-10-22 09:00",
            "cashier@test", "Front till", session.Id, [], 20m, 1.55m, 3m, 23m,
            [new SaleReceiptPaymentDto("Cash", "Confirmed", 23m, 30m, 7m, null)]);
        sales.Setup(client => client.GetReceiptAsync(saleId, It.IsAny<CancellationToken>())).ReturnsAsync(receipt);

        var checkout = new WebPosCheckoutOrchestrator(
            Mock.Of<IProductApiClient>(), Mock.Of<IInventoryApiClient>(), registers.Object, sales.Object, storage);

        // The retry that resolves the lost attempt sends the same key and the same lines the first attempt did -
        // which is what lets the server answer with its stored receipt instead of a second sale.
        var first = await checkout.CaptureCashSaleAsync(locationId, registerId, draft);
        var retry = await checkout.CaptureCashSaleAsync(locationId, registerId, draft);

        Assert.Equal(2, keys.Count);
        Assert.All(keys, key => Assert.Equal(operationId.ToString("N"), key));
        Assert.Equal(first.SaleId, retry.SaleId);
        Assert.Equal(bodies[0].Lines.Count, bodies[1].Lines.Count);
        Assert.Equal(bodies[0].LocationId, bodies[1].LocationId);
        Assert.Equal(bodies[0].RegisterId, bodies[1].RegisterId);
        Assert.Equal(bodies[0].Payment.Amount, bodies[1].Payment.Amount);
        Assert.Equal(bodies[0].Payment.CashReceived, bodies[1].Payment.CashReceived);
        Assert.Equal(bodies[0].Lines[0].VariantId, bodies[1].Lines[0].VariantId);
        Assert.Equal(bodies[0].Lines[0].Quantity, bodies[1].Lines[0].Quantity);
        Assert.Equal(bodies[0].Lines[0].UnitPrice, bodies[1].Lines[0].UnitPrice);
        Assert.Equal(bodies[0].Lines[0].DiscountAmount, bodies[1].Lines[0].DiscountAmount);
        Assert.Equal(bodies[0].Lines[0].TaxMode, bodies[1].Lines[0].TaxMode);
        Assert.Equal(bodies[0].Lines[0].ObservedCatalogRevision, bodies[1].Lines[0].ObservedCatalogRevision);
        Assert.Equal(bodies[0].Lines[0].PricingPolicyVersion, bodies[1].Lines[0].PricingPolicyVersion);

        var serverReceipt = await checkout.GetPostedReceiptAsync(first.SaleId);
        Assert.Equal("Posted", serverReceipt.ReceiptState);
        Assert.Equal(23m, serverReceipt.TotalAmount);
        Assert.Equal(7m, Assert.Single(serverReceipt.Payments).Change);

        await checkout.ClearPendingAttemptAsync();
        Assert.Null(await checkout.ReadPendingAttemptAsync());
    }

    [Fact]
    public async Task A_damaged_pending_record_is_reported_and_kept_instead_of_being_silently_dropped()
    {
        var storage = new FakeBrowserStorage();
        storage.Seed(WebPosCheckoutOrchestrator.PendingAttemptKey, """{"operationId":"not-a-guid","draft":null}""");

        var checkout = new WebPosCheckoutOrchestrator(
            Mock.Of<IProductApiClient>(), Mock.Of<IInventoryApiClient>(),
            Mock.Of<ICashRegisterApiClient>(), Mock.Of<ISaleApiClient>(), storage);

        // "No pending attempt" would be a lie here, and a lie that lets the cashier ring a second sale for the same
        // cash: an unreadable record is reported instead (specification task 20, recipe step 2).
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => checkout.ReadPendingAttemptAsync());
        Assert.Contains("may already be posted", exception.Message, StringComparison.Ordinal);

        // Nothing is thrown away on the way: the figure the operator has to reconcile is still on the machine.
        Assert.NotNull(storage.Values[WebPosCheckoutOrchestrator.PendingAttemptKey]);
    }

    /// <summary>The browser's local storage, reduced to what the orchestrator actually asks for.</summary>
    private sealed class FakeBrowserStorage : IJSRuntime
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string?> Values => _values;

        /// <summary>Seeds a value the way a previous page visit left it, damaged or not.</summary>
        public void Seed(string key, string value) => _values[key] = value;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var key = args is { Length: > 0 } ? args[0] as string : null;

            object? result = identifier switch
            {
                "localStorage.getItem" when key is not null => _values.TryGetValue(key, out var stored) ? stored : null,
                "localStorage.setItem" when key is not null && args is { Length: > 1 } => Record(key, args[1] as string),
                "localStorage.removeItem" when key is not null => Forget(key),
                _ => null,
            };

            return ValueTask.FromResult((TValue)result!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        private object? Record(string key, string? value)
        {
            _values[key] = value;
            return null;
        }

        private object? Forget(string key)
        {
            _values.Remove(key);
            return null;
        }
    }
}
