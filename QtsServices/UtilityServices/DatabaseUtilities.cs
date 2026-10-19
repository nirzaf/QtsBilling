using MongoDB.Driver;

namespace QtsServices.UtilityServices;

public static class DatabaseUtilities
{
    public static IMongoCollection<T> GetOrCreateCollection<T>(this IMongoDatabase database, string collectionName)
    {
        if (!database.ListCollectionNames().ToList().Contains(collectionName))
        {
            database.CreateCollection(collectionName);
        }
        return database.GetCollection<T>(collectionName);
    }
}