using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;

public class PurchaseOrderService
{
    private readonly IMongoCollection<PurchaseOrder> _purchaseOrdersCollection;

    public PurchaseOrderService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _purchaseOrdersCollection = database.GetCollection<PurchaseOrder>("PurchaseOrders");
    }

    public bool CreatePurchaseOrder(PurchaseOrder purchaseOrder)
    {
        try
        {
            purchaseOrder.Id = ObjectId.GenerateNewId().ToString();
            _purchaseOrdersCollection.InsertOne(purchaseOrder);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool UpdatePurchaseOrder(PurchaseOrder purchaseOrder)
    {
        try
        {
            var filter = Builders<PurchaseOrder>.Filter.Eq(po => po.Id, purchaseOrder.Id);
            var update = Builders<PurchaseOrder>.Update
                .Set(po => po.SupplierId, purchaseOrder.SupplierId)
                .Set(po => po.OrderDate, purchaseOrder.OrderDate)
                .Set(po => po.Items, purchaseOrder.Items)
                .Set(po => po.TotalAmount, purchaseOrder.TotalAmount);

            var result = _purchaseOrdersCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeletePurchaseOrder(string purchaseOrderId)
    {
        try
        {
            var filter = Builders<PurchaseOrder>.Filter.Eq(po => po.Id, purchaseOrderId);
            var result = _purchaseOrdersCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<PurchaseOrder> GetAllPurchaseOrders()
    {
        return _purchaseOrdersCollection.Find(new BsonDocument()).ToList();
    }

    public PurchaseOrder GetPurchaseOrderById(string purchaseOrderId)
    {
        return _purchaseOrdersCollection.Find(po => po.Id == purchaseOrderId).FirstOrDefault();
    }
}
