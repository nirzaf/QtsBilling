using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;

public class BalanceSheetService
{
    private readonly IMongoCollection<BalanceSheet> _balanceSheetsCollection;

    public BalanceSheetService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _balanceSheetsCollection = database.GetCollection<BalanceSheet>("BalanceSheets");
    }

    public bool CreateBalanceSheet(BalanceSheet balanceSheet)
    {
        try
        {
            balanceSheet.Id = ObjectId.GenerateNewId().ToString();
            _balanceSheetsCollection.InsertOne(balanceSheet);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool UpdateBalanceSheet(BalanceSheet balanceSheet)
    {
        try
        {
            var filter = Builders<BalanceSheet>.Filter.Eq(b => b.Id, balanceSheet.Id);
            var update = Builders<BalanceSheet>.Update
                .Set(b => b.Assets, balanceSheet.Assets)
                .Set(b => b.Liabilities, balanceSheet.Liabilities)
                .Set(b => b.Equity, balanceSheet.Equity);

            var result = _balanceSheetsCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteBalanceSheet(string? balanceSheetId)
    {
        try
        {
            var filter = Builders<BalanceSheet>.Filter.Eq(b => b.Id, balanceSheetId);
            var result = _balanceSheetsCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<BalanceSheet> GetAllBalanceSheets()
    {
        return _balanceSheetsCollection.Find(new BsonDocument()).ToList();
    }

    public BalanceSheet GetBalanceSheetById(string? balanceSheetId)
    {
        return _balanceSheetsCollection.Find(b => b.Id == balanceSheetId).FirstOrDefault();
    }
}
