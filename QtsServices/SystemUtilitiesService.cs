using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;

public class SystemUtilitiesService
{
    private readonly IMongoDatabase _localDatabase;
    private readonly IMongoDatabase _cloudDatabase;
    private readonly IMongoCollection<Log> _logCollection;
    private readonly IMongoCollection<SystemConfiguration> _configCollection;
    private readonly IMongoCollection<Backup> _backupCollection;

    public SystemUtilitiesService()
    {
        var localConnectionString = GlobalConstants.GetLocalConnectionString();
        var cloudConnectionString = GlobalConstants.GetRemoteConnectionString();

        _localDatabase = new MongoClient(localConnectionString).GetDatabase("qtsBilling");
        _cloudDatabase = new MongoClient(cloudConnectionString).GetDatabase("qtsBilling");

        // Assuming 'qtsBilling' is your cloud database name
        var database = new MongoClient(cloudConnectionString).GetDatabase("qtsBilling");
        _logCollection = database.GetCollection<Log>("Logs");
        _configCollection = database.GetCollection<SystemConfiguration>("SystemConfigurations");
        _backupCollection = database.GetCollection<Backup>("Backups");
    }

    public void SyncAllCollections()
    {
        var collections = _localDatabase.ListCollectionNames().ToList();

        foreach (var collectionName in collections)
        {
            var localCollection = _localDatabase.GetCollection<BsonDocument>(collectionName);
            var cloudCollection = _cloudDatabase.GetCollection<BsonDocument>(collectionName);

            var localData = localCollection.Find(FilterDefinition<BsonDocument>.Empty).ToList();

            foreach (var document in localData)
            {
                var filter = Builders<BsonDocument>.Filter.Eq("_id", document["_id"]);
                cloudCollection.ReplaceOne(filter, document, new ReplaceOptions { IsUpsert = true });
            }
        }
    }

    public void SyncLocalWithCloud()
    {
        var collections = _localDatabase.ListCollectionNames().ToList();

        foreach (var collectionName in collections)
        {
            var localCollection = _localDatabase.GetCollection<BsonDocument>(collectionName);
            var cloudCollection = _cloudDatabase.GetCollection<BsonDocument>(collectionName);

            var localData = localCollection.Find(FilterDefinition<BsonDocument>.Empty).ToList();

            foreach (var document in localData)
            {
                var filter = Builders<BsonDocument>.Filter.Eq("_id", document["_id"]);
                cloudCollection.ReplaceOne(filter, document, new ReplaceOptions { IsUpsert = true });
            }
        }
    }

    public void SyncRemoteWithLocal()
    {
        var collections = _cloudDatabase.ListCollectionNames().ToList();

        foreach (var collectionName in collections)
        {
            var localCollection = _localDatabase.GetCollection<BsonDocument>(collectionName);
            var cloudCollection = _cloudDatabase.GetCollection<BsonDocument>(collectionName);

            var cloudData = cloudCollection.Find(FilterDefinition<BsonDocument>.Empty).ToList();

            foreach (var document in cloudData)
            {
                var filter = Builders<BsonDocument>.Filter.Eq("_id", document["_id"]);
                localCollection.ReplaceOne(filter, document, new ReplaceOptions { IsUpsert = true });
            }
        }
    }

    public async Task SyncBidirectionally()
    {
        var collections = (await _localDatabase.ListCollectionNamesAsync()).ToList();

        foreach (var collectionName in collections)
        {
            var localCollection = _localDatabase.GetCollection<BsonDocument>(collectionName);
            var remoteCollection = _cloudDatabase.GetCollection<BsonDocument>(collectionName);

            // Fetch data from local database
            var localData = await localCollection.Find(new BsonDocument()).ToListAsync();

            // Insert data into remote database
            await remoteCollection.InsertManyAsync(localData);

            // Fetch data from remote database
            var remoteData = await remoteCollection.Find(new BsonDocument()).ToListAsync();

            // Insert data into local database
            await localCollection.InsertManyAsync(remoteData);
        }
    }

    public void AuditLog(string message)
    {
        try
        {
            _logCollection.InsertOne(new Log { Message = message, CreatedAt = DateTime.Now });
        }
        catch (Exception ex)
        {
            // Consider logging the exception to a more persistent location.
            // Throwing an exception here might hide the original error.
            // Perhaps log and rethrow, or handle differently based on context.
            new ExceptionService().CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool UpdateSystemConfiguration(SystemConfiguration config)
    {
        try
        {
            var filter = Builders<SystemConfiguration>.Filter.Eq(c => c.Id, config.Id);
            var update = Builders<SystemConfiguration>.Update
                .Set(c => c.TaxRate, config.TaxRate)
                .Set(c => c.DefaultSettings, config.DefaultSettings);

            var result = _configCollection.UpdateOne(filter, update, new UpdateOptions { IsUpsert = true });
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            // Same exception handling considerations as in AuditLog.
            new ExceptionService().CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool BackupData(Backup backup)
    {
        try
        {
            backup.Id = ObjectId.GenerateNewId().ToString();
            _backupCollection.InsertOne(backup);
            return true;
        }
        catch (Exception ex)
        {
            // Same exception handling considerations.
            new ExceptionService().CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool RestoreData(string? backupId)
    {
        try
        {
            var filter = Builders<Backup>.Filter.Eq(b => b.Id, backupId);
            var backup = _backupCollection.Find(filter).FirstOrDefault();

            if (backup != null)
            {
                // TODO: Implement restore logic here 
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            // Same exception handling considerations.
            new ExceptionService().CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<T> Search<T>(string searchText, IMongoCollection<T> collection)
    {
        try
        {
            var filter = Builders<T>.Filter.Text(searchText);
            return collection.Find(filter).ToList();
        }
        catch (Exception ex)
        {
            // Same exception handling considerations.
            new ExceptionService().CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }
}
