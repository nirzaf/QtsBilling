using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestProductGroupServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void AddProductGroup_ValidProductGroup_ReturnsTrue()
    {
        // Arrange
        var productGroup = new Faker<ProductGroup>()
            .RuleFor(pg => pg.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(pg => pg.Name, f => f.Commerce.ProductName())
            .RuleFor(pg => pg.Description, f => f.Commerce.ProductDescription())
            .RuleFor(pg => pg.Image, f => f.Image.PicsumUrl())
            .Generate();

        var service = new ProductGroupService();

        // Act
        var result = service.AddProductGroup(productGroup);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        bool isDeleted = DeleteProductGroup_ExistingProductGroup_Returns_True(productGroup.Id!);
        Assert.True(isDeleted);
    }

    [Fact]
    public void UpdateProductGroup_ValidProductGroup_ReturnsTrue()
    {
        // Arrange
        var productGroup = new Faker<ProductGroup>()
            .RuleFor(pg => pg.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(pg => pg.Name, f => f.Commerce.ProductName())
            .RuleFor(pg => pg.Description, f => f.Commerce.ProductDescription())
            .RuleFor(pg => pg.Image, f => f.Image.PicsumUrl())
            .Generate();

        var service = new ProductGroupService();
        service.AddProductGroup(productGroup);

        var updatedProductGroup = new Faker<ProductGroup>()
            .RuleFor(pg => pg.Id, _ => productGroup.Id)
            .RuleFor(pg => pg.Name, f => f.Commerce.ProductName())
            .RuleFor(pg => pg.Description, f => f.Commerce.ProductDescription())
            .RuleFor(pg => pg.Image, f => f.Image.PicsumUrl())
            .Generate();

        // Act
        var result = service.UpdateProductGroup(updatedProductGroup);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        bool isDeleted = DeleteProductGroup_ExistingProductGroup_Returns_True(productGroup.Id!);
        Assert.True(isDeleted);
    }

    [Fact]
    public void DeleteProductGroup_ExistingProductGroup_ReturnsTrue()
    {
        // Arrange
        var productGroup = new Faker<ProductGroup>()
            .RuleFor(pg => pg.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(pg => pg.Name, f => f.Commerce.ProductName())
            .RuleFor(pg => pg.Description, f => f.Commerce.ProductDescription())
            .RuleFor(pg => pg.Image, f => f.Image.PicsumUrl())
            .Generate();

        var service = new ProductGroupService();
        service.AddProductGroup(productGroup);

        // Act
        var result = service.DeleteProductGroup(productGroup.Id!);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetAllProductGroups_ReturnsListOfProductGroups()
    {
        // Arrange
        var service = new ProductGroupService();

        // Act
        var result = service.GetAllProductGroups();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<ProductGroup>>(result);
    }

    [Fact]
    public void GetProductGroupById_ExistingProductGroup_ReturnsProductGroup()
    {
        // Arrange
        var productGroup = new Faker<ProductGroup>()
            .RuleFor(pg => pg.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(pg => pg.Name, f => f.Commerce.ProductName())
            .RuleFor(pg => pg.Description, f => f.Commerce.ProductDescription())
            .RuleFor(pg => pg.Image, f => f.Image.PicsumUrl())
            .Generate();

        var service = new ProductGroupService();
        service.AddProductGroup(productGroup);

        // Act
        var result = service.GetProductGroupById(productGroup.Id!);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(productGroup.Id, result.Id);

        // Clean up
        bool isDeleted = DeleteProductGroup_ExistingProductGroup_Returns_True(productGroup.Id!);
        Assert.True(isDeleted);
    }
    
    private bool DeleteProductGroup_ExistingProductGroup_Returns_True(string id)
    {
        ProductGroupService service = new();
        var result = service.DeleteProductGroup(id);
        output.LogResponse("ProductGroup with id " + id + " deleted successfully");
        return result;
    }
}
