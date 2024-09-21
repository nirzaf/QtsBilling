using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;


public class DiscountService
{
    private readonly IMongoCollection<Discount> _discountsCollection;

    public DiscountService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _discountsCollection = database.GetCollection<Discount>("Discounts");
    }

    public bool AddDiscount(Discount discount)
    {
        try
        {
            discount.Id = ObjectId.GenerateNewId().ToString();
            _discountsCollection.InsertOne(discount);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool UpdateDiscount(Discount discount)
    {
        try
        {
            var filter = Builders<Discount>.Filter.Eq(d => d.Id, discount.Id);
            var update = Builders<Discount>.Update
                .Set(d => d.Name, discount.Name)
                .Set(d => d.BusinessId, discount.BusinessId)
                .Set(d => d.BrandId, discount.BrandId)
                .Set(d => d.CategoryId, discount.CategoryId)
                .Set(d => d.LocationId, discount.LocationId)
                .Set(d => d.Priority, discount.Priority)
                .Set(d => d.DiscountType, discount.DiscountType)
                .Set(d => d.DiscountAmount, discount.DiscountAmount)
                .Set(d => d.StartsAt, discount.StartsAt)
                .Set(d => d.EndsAt, discount.EndsAt)
                .Set(d => d.IsActive, discount.IsActive)
                .Set(d => d.ApplicableInSpg, discount.ApplicableInSpg)
                .Set(d => d.ApplicableInCg, discount.ApplicableInCg)
                .Set(d => d.CreatedAt, discount.CreatedAt)
                .Set(d => d.UpdatedAt, discount.UpdatedAt);

            var result = _discountsCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteDiscount(string? discountId)
    {
        try
        {
            var filter = Builders<Discount>.Filter.Eq(d => d.Id, discountId);
            var result = _discountsCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<Discount> GetAllDiscounts()
    {
        return _discountsCollection.Find(new BsonDocument()).ToList();
    }

    public Discount GetDiscountById(string? discountId)
    {
        return _discountsCollection.Find(d => d.Id == discountId).FirstOrDefault();
    }
}
