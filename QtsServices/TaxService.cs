using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;

public class TaxService
{
    private readonly IMongoCollection<Tax> _taxesCollection;

    public TaxService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _taxesCollection = database.GetCollection<Tax>("Taxes");
    }

    public bool AddTax(Tax tax)
    {
        try
        {
            tax.Id = ObjectId.GenerateNewId().ToString();
            _taxesCollection.InsertOne(tax);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool UpdateTax(Tax tax)
    {
        try
        {
            var filter = Builders<Tax>.Filter.Eq(t => t.Id, tax.Id);
            var update = Builders<Tax>.Update
                .Set(t => t.Name, tax.Name)
                .Set(t => t.Rate, tax.Rate)
                .Set(t => t.Description, tax.Description);

            var result = _taxesCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteTax(string taxId)
    {
        try
        {
            var filter = Builders<Tax>.Filter.Eq(t => t.Id, taxId);
            var result = _taxesCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<Tax> GetAllTaxes()
    {
        return _taxesCollection.Find(new BsonDocument()).ToList();
    }

    public Tax GetTaxById(string taxId)
    {
        return _taxesCollection.Find(t => t.Id == taxId).FirstOrDefault();
    }
}
