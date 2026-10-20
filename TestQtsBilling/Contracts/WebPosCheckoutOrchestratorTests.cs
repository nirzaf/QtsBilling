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
            Mock.Of<IProductApiClient>(), Mock.Of<IInventoryApiClient>(), registers.Object, sales.Object);
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
            Mock.Of<IProductApiClient>(), Mock.Of<IInventoryApiClient>(), registers.Object, sales.Object);
        var draft = new LocalCashSaleDraft(Guid.CreateVersion7(), 10m,
        [new LocalSaleLineDraft(0, Guid.CreateVersion7(), 1m, 10m, 0m, 0m, 0m, 1)]);

        var exception = await Assert.ThrowsAsync<ApiException>(
            () => orchestrator.CaptureCashSaleAsync(locationId, registerId, draft));

        Assert.Equal(ApiErrorCodes.CashRegisterNotOpen, exception.Code);
        sales.Verify(client => client.PostSaleAsync(It.IsAny<SaleCaptureRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
