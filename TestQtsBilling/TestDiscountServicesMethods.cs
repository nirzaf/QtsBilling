using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestDiscountServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void AddDiscount_ValidDiscount_ReturnsTrue()
    {
        // Arrange
        var discount = new Faker<Discount>()
            .RuleFor(d => d.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(d => d.Name, f => f.Commerce.ProductName())
            .RuleFor(d => d.BusinessId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.BrandId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.CategoryId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.LocationId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.Priority, f => f.Random.Int(1, 10))
            .RuleFor(d => d.DiscountType, f => f.Commerce.ProductAdjective())
            .RuleFor(d => d.DiscountAmount, f => f.Finance.Amount())
            .RuleFor(d => d.StartsAt, f => f.Date.Past())
            .RuleFor(d => d.EndsAt, f => f.Date.Future())
            .RuleFor(d => d.IsActive, f => f.Random.Bool())
            .RuleFor(d => d.ApplicableInSpg, f => f.Random.Bool())
            .RuleFor(d => d.ApplicableInCg, f => f.Random.Bool())
            .RuleFor(d => d.CreatedAt, f => f.Date.Past())
            .RuleFor(d => d.UpdatedAt, f => f.Date.Recent())
            .Generate();

        var service = new DiscountService();

        // Act
        var result = service.AddDiscount(discount);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        bool isDeleted = DeleteDiscount_ExistingDiscount_Returns_True(discount.Id);
        Assert.True(isDeleted);
    }

    [Fact]
    public void UpdateDiscount_ValidDiscount_ReturnsTrue()
    {
        // Arrange
        var discount = new Faker<Discount>()
            .RuleFor(d => d.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(d => d.Name, f => f.Commerce.ProductName())
            .RuleFor(d => d.BusinessId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.BrandId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.CategoryId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.LocationId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.Priority, f => f.Random.Int(1, 10))
            .RuleFor(d => d.DiscountType, f => f.Commerce.ProductAdjective())
            .RuleFor(d => d.DiscountAmount, f => f.Finance.Amount())
            .RuleFor(d => d.StartsAt, f => f.Date.Past())
            .RuleFor(d => d.EndsAt, f => f.Date.Future())
            .RuleFor(d => d.IsActive, f => f.Random.Bool())
            .RuleFor(d => d.ApplicableInSpg, f => f.Random.Bool())
            .RuleFor(d => d.ApplicableInCg, f => f.Random.Bool())
            .RuleFor(d => d.CreatedAt, f => f.Date.Past())
            .RuleFor(d => d.UpdatedAt, f => f.Date.Recent())
            .Generate();

        var service = new DiscountService();
        service.AddDiscount(discount);

        // Act
        discount.Name = "Updated Discount Name";
        var result = service.UpdateDiscount(discount);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        bool isDeleted = DeleteDiscount_ExistingDiscount_Returns_True(discount.Id);
        Assert.True(isDeleted);
    }

    [Fact]
    public void DeleteDiscount_ExistingDiscount_ReturnsTrue()
    {
        // Arrange
        var discount = new Faker<Discount>()
            .RuleFor(d => d.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(d => d.Name, f => f.Commerce.ProductName())
            .RuleFor(d => d.BusinessId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.BrandId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.CategoryId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.LocationId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.Priority, f => f.Random.Int(1, 10))
            .RuleFor(d => d.DiscountType, f => f.Commerce.ProductAdjective())
            .RuleFor(d => d.DiscountAmount, f => f.Finance.Amount())
            .RuleFor(d => d.StartsAt, f => f.Date.Past())
            .RuleFor(d => d.EndsAt, f => f.Date.Future())
            .RuleFor(d => d.IsActive, f => f.Random.Bool())
            .RuleFor(d => d.ApplicableInSpg, f => f.Random.Bool())
            .RuleFor(d => d.ApplicableInCg, f => f.Random.Bool())
            .RuleFor(d => d.CreatedAt, f => f.Date.Past())
            .RuleFor(d => d.UpdatedAt, f => f.Date.Recent())
            .Generate();

        var service = new DiscountService();
        service.AddDiscount(discount);

        // Act
        var result = service.DeleteDiscount(discount.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetAllDiscounts_ReturnsListOfDiscounts()
    {
        // Arrange
        var service = new DiscountService();

        // Act
        var result = service.GetAllDiscounts();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<Discount>>(result);
    }

    [Fact]
    public void GetDiscountById_ValidId_ReturnsDiscount()
    {
        // Arrange
        var discount = new Faker<Discount>()
            .RuleFor(d => d.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(d => d.Name, f => f.Commerce.ProductName())
            .RuleFor(d => d.BusinessId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.BrandId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.CategoryId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.LocationId, f => f.Random.Int(1, 100))
            .RuleFor(d => d.Priority, f => f.Random.Int(1, 10))
            .RuleFor(d => d.DiscountType, f => f.Commerce.ProductAdjective())
            .RuleFor(d => d.DiscountAmount, f => f.Finance.Amount())
            .RuleFor(d => d.StartsAt, f => f.Date.Past())
            .RuleFor(d => d.EndsAt, f => f.Date.Future())
            .RuleFor(d => d.IsActive, f => f.Random.Bool())
            .RuleFor(d => d.ApplicableInSpg, f => f.Random.Bool())
            .RuleFor(d => d.ApplicableInCg, f => f.Random.Bool())
            .RuleFor(d => d.CreatedAt, f => f.Date.Past())
            .RuleFor(d => d.UpdatedAt, f => f.Date.Recent())
            .Generate();

        var service = new DiscountService();
        service.AddDiscount(discount);

        // Act
        var result = service.GetDiscountById(discount.Id);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(discount.Id, result.Id);

        // Clean up
        bool isDeleted = DeleteDiscount_ExistingDiscount_Returns_True(discount.Id);
        Assert.True(isDeleted);
    }
    
    private bool DeleteDiscount_ExistingDiscount_Returns_True(string? id)
    {
        DiscountService service = new();
        var result = service.DeleteDiscount(id);
        output.LogResponse("Discount with id " + id + " deleted successfully");
        return result;
    }
}
