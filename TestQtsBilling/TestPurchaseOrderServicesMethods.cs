using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestPurchaseOrderServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void create_purchase_order()
    {
        // Arrange
        var purchaseOrder = new Faker<PurchaseOrder>()
            .RuleFor(po => po.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(po => po.SupplierId, f => ObjectId.GenerateNewId().ToString())
            .RuleFor(po => po.OrderDate, f => f.Date.Past())
            .RuleFor(po => po.Items, f =>
            [
                new Product
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    Name = f.Commerce.ProductName(),
                    BusinessId = f.Random.Int(1, 100),
                    Type = f.PickRandom<string>("single", "variable", "modifier", "combo"),
                    UnitId = f.Random.Int(1, 100),
                    SubUnitIds = f.Random.Word(),
                    BrandId = f.Random.Int(1, 100),
                    CategoryId = f.Random.Int(1, 100),
                    SubCategoryId = f.Random.Int(1, 100),
                    Tax = f.Random.Int(1, 100),
                    TaxType = f.PickRandom<string>("inclusive", "exclusive"),
                    EnableStock = f.Random.Bool(),
                    AlertQuantity = f.Random.Int(1, 100),
                    Sku = f.Random.Word(),
                    BarcodeType = f.PickRandom<string>("C39", "C128", "EAN13", "EAN8", "UPCA", "UPCE"),
                    ExpiryPeriod = f.Random.Int(1, 100),
                    ExpiryPeriodType = f.PickRandom<string>("days", "months"),
                    EnableSrNo = f.Random.Bool(),
                    Weight = f.Random.Word(),
                    ProductCustomField1 = f.Random.Word(),
                    ProductCustomField2 = f.Random.Word(),
                    ProductCustomField3 = f.Random.Word(),
                    ProductCustomField4 = f.Random.Word(),
                    Image = f.Image.PicsumUrl(),
                    ProductDescription = f.Lorem.Sentence(),
                    Quantity = f.Random.Int(1, 100),
                    CreatedBy = f.Random.Int(1, 100),
                    WarrantyId = f.Random.Int(1, 100),
                    IsInactive = f.Random.Bool(),
                    NotForSelling = f.Random.Bool(),
                    CreatedAt = f.Date.Past(),
                    UpdatedAt = f.Date.Recent()
                }
            ])
            .RuleFor(po => po.TotalAmount, f => f.Finance.Amount())
            .Generate();

        var service = new PurchaseOrderService();

        // Act
        var result = service.CreatePurchaseOrder(purchaseOrder);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        bool isDeleted = DeletePurchaseOrder_ExistingPurchaseOrder_ReturnsTrue(purchaseOrder.Id!);
        Assert.True(isDeleted);
    }

    [Fact]
    public void update_purchase_order()
    {
        // Arrange
        var purchaseOrder = new Faker<PurchaseOrder>()
            .RuleFor(po => po.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(po => po.SupplierId, f => ObjectId.GenerateNewId().ToString())
            .RuleFor(po => po.OrderDate, f => f.Date.Past())
            .RuleFor(po => po.Items, f =>
            [
                new()
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    Name = f.Commerce.ProductName(),
                    BusinessId = f.Random.Int(1, 100),
                    Type = f.PickRandom<string>("single", "variable", "modifier", "combo"),
                    UnitId = f.Random.Int(1, 100),
                    SubUnitIds = f.Random.Word(),
                    BrandId = f.Random.Int(1, 100),
                    CategoryId = f.Random.Int(1, 100),
                    SubCategoryId = f.Random.Int(1, 100),
                    Tax = f.Random.Int(1, 100),
                    TaxType = f.PickRandom<string>("inclusive", "exclusive"),
                    EnableStock = f.Random.Bool(),
                    AlertQuantity = f.Random.Int(1, 100),
                    Sku = f.Random.Word(),
                    BarcodeType = f.PickRandom<string>("C39", "C128", "EAN13", "EAN8", "UPCA", "UPCE"),
                    ExpiryPeriod = f.Random.Int(1, 100),
                    ExpiryPeriodType = f.PickRandom<string>("days", "months"),
                    EnableSrNo = f.Random.Bool(),
                    Weight = f.Random.Word(),
                    ProductCustomField1 = f.Random.Word(),
                    ProductCustomField2 = f.Random.Word(),
                    ProductCustomField3 = f.Random.Word(),
                    ProductCustomField4 = f.Random.Word(),
                    Image = f.Image.PicsumUrl(),
                    ProductDescription = f.Lorem.Sentence(),
                    Quantity = f.Random.Int(1, 100),
                    CreatedBy = f.Random.Int(1, 100),
                    WarrantyId = f.Random.Int(1, 100),
                    IsInactive = f.Random.Bool(),
                    NotForSelling = f.Random.Bool(),
                    CreatedAt = f.Date.Past(),
                    UpdatedAt = f.Date.Recent()
                }
            ])
            .RuleFor(po => po.TotalAmount, f => f.Finance.Amount())
            .Generate();

        var service = new PurchaseOrderService();
        service.CreatePurchaseOrder(purchaseOrder);

        // Act
        purchaseOrder.SupplierId = ObjectId.GenerateNewId().ToString();
        purchaseOrder.OrderDate = new Faker().Date.Past();
        purchaseOrder.Items =
        [
            new Product
            {
                Id = ObjectId.GenerateNewId().ToString(),
                Name = new Faker().Commerce.ProductName(),
                Quantity = new Faker().Random.Int(1, 10),
                BusinessId = new Faker().Random.Int(1, 100),
                Type = new Faker().PickRandom<string>("single", "variable", "modifier", "combo"),
                UnitId = new Faker().Random.Int(1, 100),
                SubUnitIds = new Faker().Random.Word(),
                BrandId = new Faker().Random.Int(1, 100),
                CategoryId = new Faker().Random.Int(1, 100),
                SubCategoryId = new Faker().Random.Int(1, 100),
                Tax = new Faker().Random.Int(1, 100),
                TaxType = new Faker().PickRandom<string>("inclusive", "exclusive"),
                EnableStock = new Faker().Random.Bool(),
                AlertQuantity = new Faker().Random.Int(1, 100),
                Sku = new Faker().Random.Word(),
                BarcodeType = new Faker().PickRandom<string>("C39", "C128", "EAN13", "EAN8", "UPCA", "UPCE"),
                ExpiryPeriod = new Faker().Random.Int(1, 100),
                ExpiryPeriodType = new Faker().PickRandom<string>("days", "months"),
                EnableSrNo = new Faker().Random.Bool(),
                Weight = new Faker().Random.Word(),
                ProductCustomField1 = new Faker().Random.Word(),
                ProductCustomField2 = new Faker().Random.Word(),
                ProductCustomField3 = new Faker().Random.Word(),
                ProductCustomField4 = new Faker().Random.Word(),
                Image = new Faker().Image.PicsumUrl(),
                ProductDescription = new Faker().Lorem.Sentence(),
            }
        ];
        purchaseOrder.TotalAmount = new Faker().Finance.Amount();
        var result = service.UpdatePurchaseOrder(purchaseOrder);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        bool isDeleted = DeletePurchaseOrder_ExistingPurchaseOrder_ReturnsTrue(purchaseOrder.Id!);
        Assert.True(isDeleted);
    }

    [Fact]
    public void delete_purchase_order()
    {
        // Arrange
        var purchaseOrder = new Faker<PurchaseOrder>()
            .RuleFor(po => po.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(po => po.SupplierId, f => ObjectId.GenerateNewId().ToString())
            .RuleFor(po => po.OrderDate, f => f.Date.Past())
            .RuleFor(po => po.Items, f =>
            [
                new Product
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    BusinessId = f.Random.Int(1, 100),
                    Type = f.PickRandom<string>("single", "variable", "modifier", "combo"),
                    UnitId = f.Random.Int(1, 100),
                    SubUnitIds = f.Random.Word(),
                    BrandId = f.Random.Int(1, 100),
                    CategoryId = f.Random.Int(1, 100),
                    SubCategoryId = f.Random.Int(1, 100),
                    Tax = f.Random.Int(1, 100),
                    TaxType = f.PickRandom<string>("inclusive", "exclusive"),
                    EnableStock = f.Random.Bool(),
                    AlertQuantity = f.Random.Int(1, 100),
                    Sku = f.Random.Word(),
                    BarcodeType = f.PickRandom<string>("C39", "C128", "EAN13", "EAN8", "UPCA", "UPCE"),
                    ExpiryPeriod = f.Random.Int(1, 100),
                    ExpiryPeriodType = f.PickRandom<string>("days", "months"),
                    EnableSrNo = f.Random.Bool(),
                    Weight = f.Random.Word(),
                    ProductCustomField1 = f.Random.Word(),
                    ProductCustomField2 = f.Random.Word(),
                    ProductCustomField3 = f.Random.Word(),
                    ProductCustomField4 = f.Random.Word(),
                    Image = f.Image.PicsumUrl(),
                    ProductDescription = f.Lorem.Sentence(),
                    Quantity = f.Random.Int(1, 100),
                    CreatedBy = f.Random.Int(1, 100),
                    WarrantyId = f.Random.Int(1, 100),
                    IsInactive = f.Random.Bool(),
                    NotForSelling = f.Random.Bool(),
                    CreatedAt = f.Date.Past(),
                    UpdatedAt = f.Date.Recent()
                }
            ])
            .RuleFor(po => po.TotalAmount, f => f.Finance.Amount())
            .Generate();

        var service = new PurchaseOrderService();
        service.CreatePurchaseOrder(purchaseOrder);

        // Act
        var result = service.DeletePurchaseOrder(purchaseOrder.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void get_all_purchase_orders()
    {
        // Arrange
        var purchaseOrder1 = new Faker<PurchaseOrder>()
            .RuleFor(po => po.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(po => po.SupplierId, f => ObjectId.GenerateNewId().ToString())
            .RuleFor(po => po.OrderDate, f => f.Date.Past())
            .RuleFor(po => po.Items, f =>
            [
                new Product
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    BusinessId = f.Random.Int(1, 100),
                    Type = f.PickRandom<string>("single", "variable", "modifier", "combo"),
                    UnitId = f.Random.Int(1, 100),
                    SubUnitIds = f.Random.Word(),
                    BrandId = f.Random.Int(1, 100),
                    CategoryId = f.Random.Int(1, 100),
                    SubCategoryId = f.Random.Int(1, 100),
                    Tax = f.Random.Int(1, 100),
                    TaxType = f.PickRandom<string>("inclusive", "exclusive"),
                    EnableStock = f.Random.Bool(),
                    AlertQuantity = f.Random.Int(1, 100),
                    Sku = f.Random.Word(),
                    BarcodeType = f.PickRandom<string>("C39", "C128", "EAN13", "EAN8", "UPCA", "UPCE"),
                    ExpiryPeriod = f.Random.Int(1, 100),
                    ExpiryPeriodType = f.PickRandom<string>("days", "months"),
                    EnableSrNo = f.Random.Bool(),
                    Weight = f.Random.Word(),
                    ProductCustomField1 = f.Random.Word(),
                    ProductCustomField2 = f.Random.Word(),
                    ProductCustomField3 = f.Random.Word(),
                    ProductCustomField4 = f.Random.Word()
                }
            ])
            .RuleFor(po => po.TotalAmount, f => f.Finance.Amount())
            .Generate();

        var purchaseOrder2 = new Faker<PurchaseOrder>()
            .RuleFor(po => po.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(po => po.SupplierId, f => ObjectId.GenerateNewId().ToString())
            .RuleFor(po => po.OrderDate, f => f.Date.Past())
            .RuleFor(po => po.Items, f =>
            [
                new Product
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    BusinessId = f.Random.Int(1, 100),
                    Type = f.PickRandom<string>("single", "variable", "modifier", "combo"),
                    UnitId = f.Random.Int(1, 100),
                    SubUnitIds = f.Random.Word(),
                    BrandId = f.Random.Int(1, 100),
                    CategoryId = f.Random.Int(1, 100),
                    SubCategoryId = f.Random.Int(1, 100),
                    Tax = f.Random.Int(1, 100),
                    TaxType = f.PickRandom<string>("inclusive", "exclusive"),
                    EnableStock = f.Random.Bool(),
                    AlertQuantity = f.Random.Int(1, 100),
                    Sku = f.Random.Word(),
                    BarcodeType = f.PickRandom<string>("C39", "C128", "EAN13", "EAN8", "UPCA", "UPCE"),
                    ExpiryPeriod = f.Random.Int(1, 100),
                    ExpiryPeriodType = f.PickRandom<string>("days", "months"),
                    EnableSrNo = f.Random.Bool(),
                    Weight = f.Random.Word(),
                    ProductCustomField1 = f.Random.Word(),
                    ProductCustomField2 = f.Random.Word(),
                    ProductCustomField3 = f.Random.Word(),
                    ProductCustomField4 = f.Random.Word()
                }
            ])
            .RuleFor(po => po.TotalAmount, f => f.Finance.Amount())
            .Generate();

        var service = new PurchaseOrderService();
        service.CreatePurchaseOrder(purchaseOrder1);
        service.CreatePurchaseOrder(purchaseOrder2);

        // Act
        var result = service.GetAllPurchaseOrders();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Count >= 2);

        // Clean up
        bool isDeleted1 = DeletePurchaseOrder_ExistingPurchaseOrder_ReturnsTrue(purchaseOrder1.Id!);
        bool isDeleted2 = DeletePurchaseOrder_ExistingPurchaseOrder_ReturnsTrue(purchaseOrder2.Id!);
        Assert.True(isDeleted1);
        Assert.True(isDeleted2);
    }

    [Fact]
    public void get_purchase_order_by_id()
    {
        // Arrange
        var purchaseOrder = new Faker<PurchaseOrder>()
            .RuleFor(po => po.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(po => po.SupplierId, f => ObjectId.GenerateNewId().ToString())
            .RuleFor(po => po.OrderDate, f => f.Date.Past())
            .RuleFor(po => po.Items, f =>
            [
                new Product
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    BusinessId = f.Random.Int(1, 100),
                    Type = f.PickRandom<string>("single", "variable", "modifier", "combo"),
                    UnitId = f.Random.Int(1, 100),
                    SubUnitIds = f.Random.Word(),
                    BrandId = f.Random.Int(1, 100),
                    CategoryId = f.Random.Int(1, 100),
                    SubCategoryId = f.Random.Int(1, 100),
                    Tax = f.Random.Int(1, 100),
                    TaxType = f.PickRandom<string>("inclusive", "exclusive"),
                    EnableStock = f.Random.Bool(),
                    AlertQuantity = f.Random.Int(1, 100),
                    Sku = f.Random.Word(),
                    BarcodeType = f.PickRandom<string>("C39", "C128", "EAN13", "EAN8", "UPCA", "UPCE"),
                    ExpiryPeriod = f.Random.Int(1, 100),
                    ExpiryPeriodType = f.PickRandom<string>("days", "months"),
                    EnableSrNo = f.Random.Bool(),
                    Weight = f.Random.Word(),
                    ProductCustomField1 = f.Random.Word(),
                    ProductCustomField2 = f.Random.Word(),
                    ProductCustomField3 = f.Random.Word(),
                    ProductCustomField4 = f.Random.Word()
                }
            ])
            .RuleFor(po => po.TotalAmount, f => f.Finance.Amount())
            .Generate();

        var service = new PurchaseOrderService();
        service.CreatePurchaseOrder(purchaseOrder);

        // Act
        var result = service.GetPurchaseOrderById(purchaseOrder.Id);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(purchaseOrder.Id, result.Id);

        // Clean up
        bool isDeleted = DeletePurchaseOrder_ExistingPurchaseOrder_ReturnsTrue(purchaseOrder.Id!);
        Assert.True(isDeleted);
    }

    private bool DeletePurchaseOrder_ExistingPurchaseOrder_ReturnsTrue(string id)
    {
        PurchaseOrderService service = new();
        var result = service.DeletePurchaseOrder(id);
        output.LogResponse("PurchaseOrder with id " + id + " deleted successfully");
        return result;
    }
}