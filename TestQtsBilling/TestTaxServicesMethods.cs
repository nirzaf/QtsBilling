using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestTaxServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void AddTax_ValidTax_ReturnsTrue()
    {
        // Arrange
        var tax = new Faker<Tax>()
            .RuleFor(t => t.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(t => t.Name, f => f.Commerce.ProductName())
            .RuleFor(t => t.Rate, f => f.Random.Double(0, 100))
            .RuleFor(t => t.Description, f => f.Lorem.Sentence())
            .Generate();

        var service = new TaxService();

        // Act
        var result = service.AddTax(tax);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        bool isDeleted = DeleteTax_ExistingTax_Returns_True(tax.Id);
        Assert.True(isDeleted);
    }

    [Fact]
    public void UpdateTax_ExistingTax_ReturnsTrue()
    {
        // Arrange
        var tax = new Faker<Tax>()
            .RuleFor(t => t.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(t => t.Name, f => f.Commerce.ProductName())
            .RuleFor(t => t.Rate, f => f.Random.Double(0, 100))
            .RuleFor(t => t.Description, f => f.Lorem.Sentence())
            .Generate();

        var service = new TaxService();
        service.AddTax(tax);

        // Act
        tax.Name = "Updated Tax Name";
        var result = service.UpdateTax(tax);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        bool isDeleted = DeleteTax_ExistingTax_Returns_True(tax.Id);
        Assert.True(isDeleted);
    }

    [Fact]
    public void DeleteTax_ExistingTax_ReturnsTrue()
    {
        // Arrange
        var tax = new Faker<Tax>()
            .RuleFor(t => t.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(t => t.Name, f => f.Commerce.ProductName())
            .RuleFor(t => t.Rate, f => f.Random.Double(0, 100))
            .RuleFor(t => t.Description, f => f.Lorem.Sentence())
            .Generate();

        var service = new TaxService();
        service.AddTax(tax);

        // Act
        var result = service.DeleteTax(tax.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetAllTaxes_ReturnsListOfTaxes()
    {
        // Arrange
        var service = new TaxService();

        // Act
        var result = service.GetAllTaxes();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<Tax>>(result);
    }

    [Fact]
    public void GetTaxById_ExistingTax_ReturnsTax()
    {
        // Arrange
        var tax = new Faker<Tax>()
            .RuleFor(t => t.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(t => t.Name, f => f.Commerce.ProductName())
            .RuleFor(t => t.Rate, f => f.Random.Double(0, 100))
            .RuleFor(t => t.Description, f => f.Lorem.Sentence())
            .Generate();

        var service = new TaxService();
        service.AddTax(tax);

        // Act
        var result = service.GetTaxById(tax.Id);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(tax.Id, result.Id);

        // Clean up
        bool isDeleted = DeleteTax_ExistingTax_Returns_True(tax.Id);
        Assert.True(isDeleted);
    }

    private bool DeleteTax_ExistingTax_Returns_True(string id)
    {
        TaxService service = new();
        var result = service.DeleteTax(id);
        output.LogResponse("Tax with id " + id + " deleted successfully");
        return result;
    }
}