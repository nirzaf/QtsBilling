using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;

public class CustomerService
{
    private readonly IMongoCollection<Customer> _customersCollection;

    public CustomerService()
    {
        var mongoClient = GetMongoClient(GlobalConstants.GetLocalConnectionString());
        var database = mongoClient.GetDatabase("qtsBilling");

        _customersCollection = database.GetCollection<Customer>("Customers");
        EnsureUniquePhoneNumber();
    }

    public MongoClient GetMongoClient(string mongoConnectionString)
    {
       return new MongoClient(mongoConnectionString);
    }

    public string? GetCustomerIdByPhone(string? phone)
    {
        try
        {
            var filter = Builders<Customer>.Filter.Eq(c => c.Phone, phone);
            var customer = _customersCollection.Find(filter).FirstOrDefault();
            return customer?.Id ?? null;
        }
        catch
        {
            return null;
        }
    }

    public Customer? GetCustomerById(string? id)
    {
        try
        {
            return _customersCollection.Find(c => c.Id == id).FirstOrDefault();
        }
        catch
        {
            return new Customer { Id = String.Empty };
        }
    }
    
    //async GetCustomerById(string? id)
    public async Task<Customer?> GetCustomerByIdAsync(string? id)
    {
        try
        {
            return await _customersCollection.Find(c => c.Id == id).FirstOrDefaultAsync();
        }
        catch
        {
            return new Customer { Id = string.Empty };
        }
    }

    public bool DeleteCustomer(string? id)
    {
        try
        {
            var filter = Builders<Customer>.Filter.Eq(c => c.Id, id);
            var result = _customersCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }
    
    public async Task<bool> DeleteCustomerAsync(string? id)
    {
        try
        {
            var filter = Builders<Customer>.Filter.Eq(c => c.Id, id);
            DeleteResult? result = await _customersCollection.DeleteOneAsync(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    private void EnsureUniquePhoneNumber()
    {
        try
        {
            var indexKeysDefinition = Builders<Customer>.IndexKeys.Ascending(c => c.Phone);
            var indexOptions = new CreateIndexOptions { Unique = true };
            var indexModel = new CreateIndexModel<Customer>(indexKeysDefinition, indexOptions);
            _customersCollection.Indexes.CreateOneAsync(indexModel);
        }
        catch (Exception ex)
        {
            ExceptionService es = new();
            es.CreateLog(ex);
        }
    }

    public bool AddCustomer(Customer customer)
    {
        try
        {
            customer.Id = ObjectId.GenerateNewId().ToString();
            _customersCollection.InsertOne(customer);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService es = new();
            es.CreateLog(ex);
            return false;
        }
    }
    
    public async Task<bool> AddCustomerAsync(Customer customer)
    {
        try
        {
            customer.Id = ObjectId.GenerateNewId().ToString();
            await _customersCollection.InsertOneAsync(customer);
            return true;
        }
        catch (MongoWriteException mwx)
        {
            ExceptionService es = new();
            es.CreateLog(mwx);
            return false;
        }
        catch (MongoException mex)
        {
            ExceptionService es = new();
            es.CreateLog(mex);
            return false;
        }
        catch (Exception ex)
        {
            ExceptionService es = new();
            es.CreateLog(ex);
            return false;
        }
    }

    public bool UpdateCustomer(Customer customer, string? id)
    {
        try
        {
            var filter = Builders<Customer>.Filter.Eq(c => c.Id, id);
            var update = Builders<Customer>.Update
                .Set(c => c.Name, customer.Name)
                .Set(c => c.Phone, customer.Phone)
                .Set(c => c.Address, customer.Address)
                .Set(c => c.Email, customer.Email);

            var result = _customersCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService es = new();
            es.CreateLog(ex);
            return false;
        }
    }
    
    public async Task<bool> UpdateCustomerAsync(Customer customer, string? id)
    {
        try
        {
            var filter = Builders<Customer>.Filter.Eq(c => c.Id, id);
            var update = Builders<Customer>.Update
                .Set(c => c.Name, customer.Name)
                .Set(c => c.Phone, customer.Phone)
                .Set(c => c.Address, customer.Address)
                .Set(c => c.Email, customer.Email);
            var result = await _customersCollection.UpdateOneAsync(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService es = new();
            es.CreateLog(ex);
            return false;
        }
    }

    public List<Customer> SearchCustomers(string? searchText)
    {
        try
        {
            if (string.IsNullOrEmpty(searchText))
            {
                return _customersCollection.Find(new BsonDocument()).ToList(); 
            }

            var lowerCaseSearchText = searchText.ToLower();
            var filter = Builders<Customer>.Filter.Regex(c => c.Name, new BsonRegularExpression(lowerCaseSearchText, "i"))
                         | Builders<Customer>.Filter.Regex(c => c.Phone, new BsonRegularExpression(lowerCaseSearchText, "i"))
                         | Builders<Customer>.Filter.Regex(c => c.Email, new BsonRegularExpression(lowerCaseSearchText, "i"))
                         | Builders<Customer>.Filter.Regex(c => c.Address, new BsonRegularExpression(lowerCaseSearchText, "i"));
            
            return _customersCollection.Find(filter).ToList();
        }
        catch (Exception ex)
        {
            ExceptionService es = new();
            es.CreateLog(ex);
            return [];
        }
    }
    
    // async search
    public async Task<List<Customer>> SearchCustomersAsync(string? searchText)
    {
        try
        {
            if (string.IsNullOrEmpty(searchText))
            {
                return await _customersCollection.Find(new BsonDocument()).ToListAsync(); 
            }
            
            var lowerCaseSearchText = searchText.ToLower();
            var filter = Builders<Customer>.Filter.Regex(c => c.Name, new BsonRegularExpression(lowerCaseSearchText, "i"))
                         | Builders<Customer>.Filter.Regex(c => c.Phone, new BsonRegularExpression(lowerCaseSearchText, "i"))
                         | Builders<Customer>.Filter.Regex(c => c.Email, new BsonRegularExpression(lowerCaseSearchText, "i"))
                         | Builders<Customer>.Filter.Regex(c => c.Address, new BsonRegularExpression(lowerCaseSearchText, "i"));
            
            return await _customersCollection.Find(filter).ToListAsync();
        }
        catch (Exception ex)
        {
            ExceptionService es = new();
            es.CreateLog(ex);
            return [];
        }
    }

    public List<Customer> GetCustomers()
    {
        try
        {
            return _customersCollection.Find(new BsonDocument()).ToList();
        }
        catch (Exception ex)
        {
            ExceptionService es = new();
            es.CreateLog(ex);
            return [];
        }
    }
    
    public async Task<List<Customer>> GetCustomersAsync()
    {
        try
        {
            return await _customersCollection.Find(new BsonDocument()).ToListAsync();
        }
        catch (Exception ex)
        {
            ExceptionService es = new();
            es.CreateLog(ex);
            return [];
        }
    }
    
    public async Task<List<Customer>> PagedCustomersAsync(int page, int pageSize)
    {
        try
        {
            var filter = Builders<Customer>.Filter.Empty;
            var skip = (page - 1) * pageSize;
            var limit = pageSize;
            
            return await _customersCollection.Find(filter).Skip(skip).Limit(limit).ToListAsync();
        }
        catch (Exception ex)
        {
            ExceptionService es = new();
            es.CreateLog(ex);
            return [];
        }
    }
}