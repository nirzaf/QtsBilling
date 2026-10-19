using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;

public class JournalEntryService
{
    private readonly IMongoCollection<Transaction> _journalEntriesCollection;

    public JournalEntryService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _journalEntriesCollection = database.GetCollection<Transaction>("JournalEntries");
    }

    public bool CreateJournalEntry(Transaction journalEntry)
    {
        try
        {
            journalEntry.Id = ObjectId.GenerateNewId().ToString();
            _journalEntriesCollection.InsertOne(journalEntry);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool UpdateJournalEntry(Transaction journalEntry)
    {
        try
        {
            var filter = Builders<Transaction>.Filter.Eq(j => j.Id, journalEntry.Id);
            var update = Builders<Transaction>.Update
                .Set(j => j.Date, journalEntry.Date)
                .Set(j => j.Description, journalEntry.Description)
                .Set(j => j.Entries, journalEntry.Entries);

            var result = _journalEntriesCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteJournalEntry(string journalEntryId)
    {
        try
        {
            var filter = Builders<Transaction>.Filter.Eq(j => j.Id, journalEntryId);
            var result = _journalEntriesCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<Transaction> GetAllJournalEntries()
    {
        return _journalEntriesCollection.Find(new BsonDocument()).ToList();
    }

    public Transaction GetJournalEntryById(string journalEntryId)
    {
        return _journalEntriesCollection.Find(j => j.Id == journalEntryId).FirstOrDefault();
    }
}
