using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;


public class TestPosServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void process_sale_valid_sale()
    {
        // Arrange
        var sale = new Faker<Sale>()
            .RuleFor(s => s.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(s => s.CustomerId, f => f.Person.Random.Guid().ToString())
            .RuleFor(s => s.Date, f => f.Date.Past())
            .RuleFor(s => s.Items, f =>
            [
                new()
                {
                    ProductId = ObjectId.GenerateNewId().ToString(),
                    Quantity = f.Random.Int(1, 10),
                    UnitPrice = f.Random.Decimal(1, 100),
                    Subtotal = f.Random.Decimal(1, 100)
                }
            ])
            .RuleFor(s => s.TotalAmount, f => f.Random.Decimal(1, 1000))
            .Generate();

        var service = new PosService();

        // Act
        var result = service.ProcessSale(sale);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void apply_discount_valid_discount()
    {
        // Arrange
        var totalAmount = 100m;
        var discountPercentage = 10m;
        var service = new PosService();

        // Act
        var result = service.ApplyDiscount(totalAmount, discountPercentage);
        output.LogResponse(result);

        // Assert
        Assert.Equal(90m, result);
    }

    [Fact]
    public void calculate_tax_valid_tax()
    {
        // Arrange
        var totalAmount = 100m;
        var taxPercentage = 10m;
        var service = new PosService();

        // Act
        var result = service.CalculateTax(totalAmount, taxPercentage);
        output.LogResponse(result);

        // Assert
        Assert.Equal(110m, result);
    }

    [Fact]
    public void handle_payment_valid_payment()
    {
        // Arrange
        var sale = new Faker<Sale>()
            .RuleFor(s => s.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(s => s.CustomerId, f => f.Person.Random.Guid().ToString())
            .RuleFor(s => s.Date, f => f.Date.Past())
            .RuleFor(s => s.Items, f =>
            [
                new()
                {
                    ProductId = ObjectId.GenerateNewId().ToString(),
                    Quantity = f.Random.Int(1, 10),
                    UnitPrice = f.Random.Decimal(1, 100),
                    Subtotal = f.Random.Decimal(1, 100)
                }
            ])
            .RuleFor(s => s.TotalAmount, f => f.Random.Decimal(1, 1000))
            .Generate();

        var service = new PosService();

        // Act
        var result = service.HandlePayment(sale);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void generate_receipt_valid_sale()
    {
        // Arrange
        var sale = new Faker<Sale>()
            .RuleFor(s => s.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(s => s.CustomerId, f => f.Person.Random.Guid().ToString())
            .RuleFor(s => s.Date, f => f.Date.Past())
            .RuleFor(s => s.Items, f =>
            [
                new SaleItem
                {
                    ProductId = ObjectId.GenerateNewId().ToString(),
                    Quantity = f.Random.Int(1, 10),
                    UnitPrice = f.Random.Decimal(1, 100),
                    Subtotal = f.Random.Decimal(1, 100)
                }
            ])
            .RuleFor(s => s.TotalAmount, f => f.Random.Decimal(1, 1000))
            .Generate();

        var service = new PosService();

        // Act
        service.GenerateReceipt(sale);
        output.LogResponse("Receipt generated successfully");

        // Assert
        Assert.True(true);
    }
}