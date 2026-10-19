using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;

public class InvoiceService
{
    private readonly IMongoCollection<Invoice> _invoicesCollection;

    public InvoiceService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _invoicesCollection = database.GetCollection<Invoice>("Invoices");
    }

    public bool CreateInvoice(Invoice invoice)
    {
        try
        {
            invoice.Id = ObjectId.GenerateNewId().ToString();
            _invoicesCollection.InsertOne(invoice);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool UpdateInvoice(Invoice invoice)
    {
        try
        {
            var filter = Builders<Invoice>.Filter.Eq(i => i.Id, invoice.Id);
            var update = Builders<Invoice>.Update
                .Set(i => i.CustomerId, invoice.CustomerId)
                .Set(i => i.Date, invoice.Date)
                .Set(i => i.Products, invoice.Products)
                .Set(i => i.TotalAmount, invoice.TotalAmount)
                .Set(i => i.Status, invoice.Status);

            var result = _invoicesCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteInvoice(string invoiceId)
    {
        try
        {
            var filter = Builders<Invoice>.Filter.Eq(i => i.Id, invoiceId);
            var result = _invoicesCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public Invoice GetInvoiceById(string invoiceId)
    {
        return _invoicesCollection.Find(i => i.Id == invoiceId).FirstOrDefault();
    }

    public List<Invoice> GetAllInvoices()
    {
        return _invoicesCollection.Find(new BsonDocument()).ToList();
    }
}
