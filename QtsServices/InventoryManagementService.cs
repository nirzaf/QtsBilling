using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;
using QtsServices.UtilityServices;

namespace QtsServices;

public class InventoryManagementService
{
    private readonly IMongoCollection<Product> _productsCollection;
    private readonly IMongoCollection<Inventory> _inventoryCollection;
    private readonly IMongoCollection<Supplier> _suppliersCollection;

    public InventoryManagementService()
    {
        var mongoConnectionString = GlobalConstants.GetLocalConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _productsCollection = database.GetOrCreateCollection<Product>("Products");
        _inventoryCollection = database.GetOrCreateCollection<Inventory>("Inventory");
        _suppliersCollection = database.GetOrCreateCollection<Supplier>("Suppliers");
    }

    public bool AddProduct(Product product)
    {
        try
        {
            product.Id = ObjectId.GenerateNewId().ToString();
            _productsCollection.InsertOne(product);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool EditProduct(Product product)
    {
        try
        {
            var filter = Builders<Product>.Filter.Eq(p => p.Id, product.Id);
            var update = Builders<Product>.Update
                .Set(p => p.Name, product.Name)
                .Set(p => p.Type, product.Type)
                .Set(p => p.UnitId, product.UnitId)
                .Set(p => p.SubUnitIds, product.SubUnitIds)
                .Set(p => p.BrandId, product.BrandId)
                .Set(p => p.CategoryId, product.CategoryId)
                .Set(p => p.SubCategoryId, product.SubCategoryId)
                .Set(p => p.Tax, product.Tax)
                .Set(p => p.TaxType, product.TaxType)
                .Set(p => p.EnableStock, product.EnableStock)
                .Set(p => p.AlertQuantity, product.AlertQuantity)
                .Set(p => p.Sku, product.Sku)
                .Set(p => p.BarcodeType, product.BarcodeType)
                .Set(p => p.ExpiryPeriod, product.ExpiryPeriod)
                .Set(p => p.ExpiryPeriodType, product.ExpiryPeriodType)
                .Set(p => p.EnableSrNo, product.EnableSrNo)
                .Set(p => p.Weight, product.Weight)
                .Set(p => p.ProductCustomField1, product.ProductCustomField1)
                .Set(p => p.ProductCustomField2, product.ProductCustomField2)
                .Set(p => p.ProductCustomField3, product.ProductCustomField3)
                .Set(p => p.ProductCustomField4, product.ProductCustomField4)
                .Set(p => p.Image, product.Image)
                .Set(p => p.ProductDescription, product.ProductDescription)
                .Set(p => p.Quantity, product.Quantity);

            var result = _productsCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteProduct(string productId)
    {
        try
        {
            var filter = Builders<Product>.Filter.Eq(p => p.Id, productId);
            var result = _productsCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<Product> GetAllProducts()
    {
        try
        {
            var products = _productsCollection.Find(new BsonDocument()).ToList();
            return products.Count > 0 ? products : [];
        }
        catch (Exception e)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(e);
            return [];
        }
    }

    public bool UpdateInventory(string productId, int quantity)
    {
        try
        {
            var filter = Builders<Inventory>.Filter.Eq(i => i.ProductId, productId);
            var update = Builders<Inventory>.Update
                .Set(i => i.Quantity, quantity)
                .Set(i => i.LastUpdated, DateTime.Now);

            var result = _inventoryCollection.UpdateOne(filter, update, new UpdateOptions { IsUpsert = true });
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<Inventory> GetLowStockProducts(int threshold)
    {
        var filter = Builders<Inventory>.Filter.Lt(i => i.Quantity, threshold);
        return _inventoryCollection.Find(filter).ToList();
    }

    public bool AddSupplier(Supplier supplier)
    {
        try
        {
            supplier.Id = ObjectId.GenerateNewId().ToString();
            _suppliersCollection.InsertOne(supplier);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool EditSupplier(Supplier supplier)
    {
        try
        {
            var filter = Builders<Supplier>.Filter.Eq(s => s.Id, supplier.Id);
            var update = Builders<Supplier>.Update
                .Set(s => s.Name, supplier.Name)
                .Set(s => s.ContactInfo, supplier.ContactInfo)
                .Set(s => s.Address, supplier.Address);

            var result = _suppliersCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteSupplier(string supplierId)
    {
        try
        {
            var filter = Builders<Supplier>.Filter.Eq(s => s.Id, supplierId);
            var result = _suppliersCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<Supplier> GetAllSuppliers()
    {
        return _suppliersCollection.Find(new BsonDocument()).ToList();
    }

    public bool HandleProductReturn(string productId, int quantity)
    {
        try
        {
            var filter = Builders<Inventory>.Filter.Eq(i => i.ProductId, productId);
            var update = Builders<Inventory>.Update
                .Inc(i => i.Quantity, quantity)
                .Set(i => i.LastUpdated, DateTime.Now);

            var result = _inventoryCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }
}