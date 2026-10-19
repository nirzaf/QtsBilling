using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;

public class ProductCategoryService
{
    private readonly IMongoCollection<ProductCategory> _productCategoriesCollection;

    public ProductCategoryService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _productCategoriesCollection = database.GetCollection<ProductCategory>("ProductCategories");
    }

    public bool AddProductCategory(ProductCategory productCategory)
    {
        try
        {
            productCategory.Id = ObjectId.GenerateNewId().ToString();
            _productCategoriesCollection.InsertOne(productCategory);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool UpdateProductCategory(ProductCategory productCategory)
    {
        try
        {
            var filter = Builders<ProductCategory>.Filter.Eq(pc => pc.Id, productCategory.Id);
            var update = Builders<ProductCategory>.Update
                .Set(pc => pc.Name, productCategory.Name)
                .Set(pc => pc.Description, productCategory.Description)
                .Set(pc => pc.Image, productCategory.Image)
                .Set(pc => pc.ProductGroupId, productCategory.ProductGroupId)
                .Set(pc => pc.ProductGroup, productCategory.ProductGroup);

            var result = _productCategoriesCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteProductCategory(string productCategoryId)
    {
        try
        {
            var filter = Builders<ProductCategory>.Filter.Eq(pc => pc.Id, productCategoryId);
            var result = _productCategoriesCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<ProductCategory> GetAllProductCategories()
    {
        return _productCategoriesCollection.Find(new BsonDocument()).ToList();
    }

    public ProductCategory GetProductCategoryById(string productCategoryId)
    {
        return _productCategoriesCollection.Find(pc => pc.Id == productCategoryId).FirstOrDefault();
    }
}
