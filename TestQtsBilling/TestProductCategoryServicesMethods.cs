using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestProductCategoryServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void AddProductCategory_ValidProductCategory_ReturnsTrue()
    {
        // Arrange
        var productCategory = new Faker<ProductCategory>()
            .RuleFor(pc => pc.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(pc => pc.Name, f => f.Commerce.Categories(1)[0])
            .RuleFor(pc => pc.Description, f => f.Lorem.Sentence())
            .RuleFor(pc => pc.Image, f => f.Image.PicsumUrl())
            .RuleFor(pc => pc.ProductGroupId, f => f.Random.Int())
            .Generate();

        var service = new ProductCategoryService();

        // Act
        var result = service.AddProductCategory(productCategory);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        service.DeleteProductCategory(productCategory.Id);
    }

    [Fact]
    public void UpdateProductCategory_ValidProductCategory_ReturnsTrue()
    {
        // Arrange
        var productCategory = new Faker<ProductCategory>()
            .RuleFor(pc => pc.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(pc => pc.Name, f => f.Commerce.Categories(1)[0])
            .RuleFor(pc => pc.Description, f => f.Lorem.Sentence())
            .RuleFor(pc => pc.Image, f => f.Image.PicsumUrl())
            .RuleFor(pc => pc.ProductGroupId, f => f.Random.Int())
            .Generate();

        var service = new ProductCategoryService();
        service.AddProductCategory(productCategory);

        // Act
        productCategory.Name = new Faker().Commerce.Categories(1)[0];
        productCategory.Description = new Faker().Lorem.Sentence();
        productCategory.Image = new Faker().Image.PicsumUrl();
        productCategory.ProductGroupId = new Faker().Random.Int();
        var result = service.UpdateProductCategory(productCategory);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        service.DeleteProductCategory(productCategory.Id);
    }

    [Fact]
    public void DeleteProductCategory_ValidProductCategoryId_ReturnsTrue()
    {
        // Arrange
        var productCategory = new Faker<ProductCategory>()
            .RuleFor(pc => pc.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(pc => pc.Name, f => f.Commerce.Categories(1)[0])
            .RuleFor(pc => pc.Description, f => f.Lorem.Sentence())
            .RuleFor(pc => pc.Image, f => f.Image.PicsumUrl())
            .RuleFor(pc => pc.ProductGroupId, f => f.Random.Int())
            .Generate();

        var service = new ProductCategoryService();
        service.AddProductCategory(productCategory);

        // Act
        var result = service.DeleteProductCategory(productCategory.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetAllProductCategories_ReturnsListOfProductCategories()
    {
        // Arrange
        var productCategory1 = new Faker<ProductCategory>()
            .RuleFor(pc => pc.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(pc => pc.Name, f => f.Commerce.Categories(1)[0])
            .RuleFor(pc => pc.Description, f => f.Lorem.Sentence())
            .RuleFor(pc => pc.Image, f => f.Image.PicsumUrl())
            .RuleFor(pc => pc.ProductGroupId, f => f.Random.Int())
            .Generate();

        var productCategory2 = new Faker<ProductCategory>()
            .RuleFor(pc => pc.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(pc => pc.Name, f => f.Commerce.Categories(1)[0])
            .RuleFor(pc => pc.Description, f => f.Lorem.Sentence())
            .RuleFor(pc => pc.Image, f => f.Image.PicsumUrl())
            .RuleFor(pc => pc.ProductGroupId, f => f.Random.Int())
            .Generate();

        var service = new ProductCategoryService();
        service.AddProductCategory(productCategory1);
        service.AddProductCategory(productCategory2);

        // Act
        var result = service.GetAllProductCategories();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Count >= 2);

        // Clean up
        service.DeleteProductCategory(productCategory1.Id);
        service.DeleteProductCategory(productCategory2.Id);
    }

    [Fact]
    public void GetProductCategoryById_ValidProductCategoryId_ReturnsProductCategory()
    {
        // Arrange
        var productCategory = new Faker<ProductCategory>()
            .RuleFor(pc => pc.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(pc => pc.Name, f => f.Commerce.Categories(1)[0])
            .RuleFor(pc => pc.Description, f => f.Lorem.Sentence())
            .RuleFor(pc => pc.Image, f => f.Image.PicsumUrl())
            .RuleFor(pc => pc.ProductGroupId, f => f.Random.Int())
            .Generate();

        var service = new ProductCategoryService();
        service.AddProductCategory(productCategory);

        // Act
        var result = service.GetProductCategoryById(productCategory.Id);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(productCategory.Id, result.Id);

        // Clean up
        service.DeleteProductCategory(productCategory.Id);
    }
}
