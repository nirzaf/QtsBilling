using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;

public class ProductGroupService
{
    private readonly IMongoCollection<ProductGroup> _productGroupsCollection;

    public ProductGroupService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _productGroupsCollection = database.GetCollection<ProductGroup>("ProductGroups");
    }

    public bool AddProductGroup(ProductGroup productGroup)
    {
        try
        {
            productGroup.Id = ObjectId.GenerateNewId().ToString();
            _productGroupsCollection.InsertOne(productGroup);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool UpdateProductGroup(ProductGroup productGroup)
    {
        try
        {
            var filter = Builders<ProductGroup>.Filter.Eq(pg => pg.Id, productGroup.Id);
            var update = Builders<ProductGroup>.Update
                .Set(pg => pg.Name, productGroup.Name)
                .Set(pg => pg.Description, productGroup.Description)
                .Set(pg => pg.Image, productGroup.Image);

            var result = _productGroupsCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteProductGroup(string productGroupId)
    {
        try
        {
            var filter = Builders<ProductGroup>.Filter.Eq(pg => pg.Id, productGroupId);
            var result = _productGroupsCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<ProductGroup> GetAllProductGroups()
    {
        return _productGroupsCollection.Find(new BsonDocument()).ToList();
    }

    public ProductGroup GetProductGroupById(string productGroupId)
    {
        return _productGroupsCollection.Find(pg => pg.Id == productGroupId).FirstOrDefault();
    }
}
