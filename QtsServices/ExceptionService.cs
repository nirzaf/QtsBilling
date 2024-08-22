using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;


public class ExceptionService
{
    private readonly IMongoCollection<Log> _logCollection;

    public ExceptionService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");
        
        var filter = new BsonDocument("name", "Logs");
        var collections = database.ListCollections(new ListCollectionsOptions { Filter = filter }).ToList();
        if (collections.Count == 0)
        {
            database.CreateCollection("Logs");
        }

        _logCollection = database.GetCollection<Log>("Logs");
    }

    public void CreateLog(Exception exception)
    {
        try
        {
            var log = new Log
            {
                Id = ObjectId.GenerateNewId().ToString(),
                Message = exception.Message,
                StackTrace = exception.StackTrace,
                CreatedAt = DateTime.Now
            };

            _logCollection.InsertOne(log); 
        }
        catch (Exception ex)
        {
            _logCollection.InsertOne(new Log 
            {
                Id = ObjectId.GenerateNewId().ToString(),
                Message = ex.Message,
                StackTrace = ex.StackTrace,
                CreatedAt = DateTime.Now
            });
        }
    }
}