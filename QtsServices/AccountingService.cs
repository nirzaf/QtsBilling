using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;

public class AccountingService
{
    private readonly IMongoCollection<Account> _accountsCollection;
    private readonly IMongoCollection<Transaction> _transactionsCollection;

    public AccountingService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _accountsCollection = database.GetCollection<Account>("Accounts");
        _transactionsCollection = database.GetCollection<Transaction>("Transactions");
    }

    public bool AddAccount(Account account)
    {
        try
        {
            account.Id = ObjectId.GenerateNewId().ToString();
            _accountsCollection.InsertOne(account);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool RecordTransaction(Transaction transaction)
    {
        try
        {
            transaction.Id = ObjectId.GenerateNewId().ToString();
            _transactionsCollection.InsertOne(transaction);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<Account> GetAccounts()
    {
        return _accountsCollection.Find(new BsonDocument()).ToList();
    }

    public List<Transaction> GetTransactions()
    {
        return _transactionsCollection.Find(new BsonDocument()).ToList();
    }

    public List<Transaction> GetTransactionsByAccountId(string accountId)
    {
        var filter = Builders<Transaction>.Filter.ElemMatch(t => t.Entries, e => e.AccountId == accountId);
        return _transactionsCollection.Find(filter).ToList();
    }

    public List<Transaction> GetTransactionsByDateRange(DateTime startDate, DateTime endDate)
    {
        var filter = Builders<Transaction>.Filter.And(
            Builders<Transaction>.Filter.Gte(t => t.Date, startDate),
            Builders<Transaction>.Filter.Lte(t => t.Date, endDate)
        );
        return _transactionsCollection.Find(filter).ToList();
    }

    public decimal GetAccountBalance(string? accountId)
    {
        var account = _accountsCollection.Find(a => a.Id == accountId).FirstOrDefault();
        return account?.Balance ?? 0;
    }

    public bool UpdateAccountBalance(string? accountId, decimal newBalance)
    {
        try
        {
            var filter = Builders<Account>.Filter.Eq(a => a.Id, accountId);
            var update = Builders<Account>.Update.Set(a => a.Balance, newBalance);
            var result = _accountsCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteAccount(string? accountId)
    {
        try
        {
            var filter = Builders<Account>.Filter.Eq(a => a.Id, accountId);
            var result = _accountsCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteTransaction(string transactionId)
    {
        try
        {
            var filter = Builders<Transaction>.Filter.Eq(t => t.Id, transactionId);
            var result = _transactionsCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<Account> SearchAccounts(string? searchText)
    {
        try
        {
            if (string.IsNullOrEmpty(searchText))
            {
                return _accountsCollection.Find(new BsonDocument()).ToList();
            }

            var lowerCaseSearchText = searchText.ToLower();
            var filter = Builders<Account>.Filter.Regex(a => a.Name, new BsonRegularExpression(lowerCaseSearchText, "i"));
            return _accountsCollection.Find(filter).ToList();
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            return [];
        }
    }

    public List<Transaction> SearchTransactions(string? searchText)
    {
        try
        {
            if (string.IsNullOrEmpty(searchText))
            {
                return _transactionsCollection.Find(new BsonDocument()).ToList();
            }

            var lowerCaseSearchText = searchText.ToLower();
            var filter = Builders<Transaction>.Filter.Regex(t => t.Description, new BsonRegularExpression(lowerCaseSearchText, "i"));
            return _transactionsCollection.Find(filter).ToList();
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            return [];
        }
    }
}
