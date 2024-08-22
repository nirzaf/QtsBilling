using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;

public class PosService
{
    private readonly IMongoCollection<Sale> _salesCollection;
    private readonly IMongoCollection<Product> _productsCollection;

    public PosService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _salesCollection = database.GetCollection<Sale>("Sales");
        _productsCollection = database.GetCollection<Product>("Products");
    }

    public bool ProcessSale(Sale sale)
    {
        try
        {
            sale.Id = ObjectId.GenerateNewId().ToString();
            _salesCollection.InsertOne(sale);
            UpdateInventory(sale.Items);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    private void UpdateInventory(List<SaleItem> items)
    {
        foreach (var item in items)
        {
            var filter = Builders<Product>.Filter.Eq(p => p.Id, item.ProductId);
            var update = Builders<Product>.Update.Inc(p => p.Quantity, -item.Quantity);
            _productsCollection.UpdateOne(filter, update);
        }
    }

    public decimal ApplyDiscount(decimal totalAmount, decimal discountPercentage)
    {
        return totalAmount - (totalAmount * discountPercentage / 100);
    }

    public decimal CalculateTax(decimal totalAmount, decimal taxPercentage)
    {
        return totalAmount + (totalAmount * taxPercentage / 100);
    }

    public bool HandlePayment(Sale sale)
    {
        try
        {
            // TODO: Handle payment logic here (e.g., Cash, Credit Card)
            // For simplicity, we assume payment is always successful
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public void GenerateReceipt(Sale sale)
    {
        // TODO: Generate and print receipt logic here
        // For simplicity, we assume receipt is generated and printed successfully
    }
}
