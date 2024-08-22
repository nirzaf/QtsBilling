using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestInventoryManagementServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void AddProduct_ValidProduct_ReturnsTrue()
    {
        // Arrange
        var product = new Faker<Product>()
            .RuleFor(p => p.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(p => p.Name, f => f.Commerce.ProductName())
            .RuleFor(p => p.Type, f => f.Commerce.ProductMaterial())
            .RuleFor(p => p.Quantity, f => f.Random.Int(1, 100))
            .Generate();

        var service = new InventoryManagementService();

        // Act
        var result = service.AddProduct(product);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        service.DeleteProduct(product.Id);
    }

    [Fact]
    public void EditProduct_ValidProduct_ReturnsTrue()
    {
        // Arrange
        var product = new Faker<Product>()
            .RuleFor(p => p.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(p => p.Name, f => f.Commerce.ProductName())
            .RuleFor(p => p.Type, f => f.Commerce.ProductMaterial())
            .RuleFor(p => p.Quantity, f => f.Random.Int(1, 100))
            .Generate();

        var service = new InventoryManagementService();
        service.AddProduct(product);

        // Act
        product.Name = "Updated Product Name";
        var result = service.EditProduct(product);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        service.DeleteProduct(product.Id);
    }

    [Fact]
    public void DeleteProduct_ValidProductId_ReturnsTrue()
    {
        // Arrange
        var product = new Faker<Product>()
            .RuleFor(p => p.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(p => p.Name, f => f.Commerce.ProductName())
            .RuleFor(p => p.Type, f => f.Commerce.ProductMaterial())
            .RuleFor(p => p.Quantity, f => f.Random.Int(1, 100))
            .Generate();

        var service = new InventoryManagementService();
        service.AddProduct(product);

        // Act
        var result = service.DeleteProduct(product.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetAllProducts_ReturnsListOfProducts()
    {
        // Arrange
        var service = new InventoryManagementService();

        // Act
        var result = service.GetAllProducts();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<Product>>(result);
    }

    [Fact]
    public void UpdateInventory_ValidProductIdAndQuantity_ReturnsTrue()
    {
        // Arrange
        var product = new Faker<Product>()
            .RuleFor(p => p.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(p => p.Name, f => f.Commerce.ProductName())
            .RuleFor(p => p.Type, f => f.Commerce.ProductMaterial())
            .RuleFor(p => p.Quantity, f => f.Random.Int(1, 100))
            .Generate();

        var service = new InventoryManagementService();
        service.AddProduct(product);

        // Act
        var result = service.UpdateInventory(product.Id, 50);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        service.DeleteProduct(product.Id);
    }

    [Fact]
    public void GetLowStockProducts_ValidThreshold_ReturnsListOfProducts()
    {
        // Arrange
        var service = new InventoryManagementService();

        // Act
        var result = service.GetLowStockProducts(10);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<Inventory>>(result);
    }

    [Fact]
    public void AddSupplier_ValidSupplier_ReturnsTrue()
    {
        // Arrange
        var supplier = new Faker<Supplier>()
            .RuleFor(s => s.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(s => s.Name, f => f.Company.CompanyName())
            .RuleFor(s => s.ContactInfo, f => f.Phone.PhoneNumber())
            .RuleFor(s => s.Address, f => f.Address.FullAddress())
            .Generate();

        var service = new InventoryManagementService();

        // Act
        var result = service.AddSupplier(supplier);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        service.DeleteSupplier(supplier.Id);
    }

    [Fact]
    public void EditSupplier_ValidSupplier_ReturnsTrue()
    {
        // Arrange
        var supplier = new Faker<Supplier>()
            .RuleFor(s => s.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(s => s.Name, f => f.Company.CompanyName())
            .RuleFor(s => s.ContactInfo, f => f.Phone.PhoneNumber())
            .RuleFor(s => s.Address, f => f.Address.FullAddress())
            .Generate();

        var service = new InventoryManagementService();
        service.AddSupplier(supplier);

        // Act
        supplier.Name = "Updated Supplier Name";
        var result = service.EditSupplier(supplier);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        service.DeleteSupplier(supplier.Id);
    }

    [Fact]
    public void DeleteSupplier_ValidSupplierId_ReturnsTrue()
    {
        // Arrange
        var supplier = new Faker<Supplier>()
            .RuleFor(s => s.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(s => s.Name, f => f.Company.CompanyName())
            .RuleFor(s => s.ContactInfo, f => f.Phone.PhoneNumber())
            .RuleFor(s => s.Address, f => f.Address.FullAddress())
            .Generate();

        var service = new InventoryManagementService();
        service.AddSupplier(supplier);

        // Act
        var result = service.DeleteSupplier(supplier.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetAllSuppliers_ReturnsListOfSuppliers()
    {
        // Arrange
        var service = new InventoryManagementService();

        // Act
        var result = service.GetAllSuppliers();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<Supplier>>(result);
    }

    [Fact]
    public void HandleProductReturn_InvalidProductIdAndQuantity_Returns_False()
    {
        // Arrange
        var product = new Faker<Product>()
            .RuleFor(p => p.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(p => p.Name, f => f.Commerce.ProductName())
            .RuleFor(p => p.Type, f => f.Commerce.ProductMaterial())
            .RuleFor(p => p.Quantity, f => f.Random.Int(1, 100))
            .Generate();

        var service = new InventoryManagementService();
        service.AddProduct(product);

        // Act
        var result = service.HandleProductReturn(product.Id, 10);
        output.LogResponse(result);

        // Assert
        Assert.False(result);

        // Clean up
        service.DeleteProduct(product.Id);
    }
}
