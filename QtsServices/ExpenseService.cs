using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;

public class ExpenseService
{
    private readonly IMongoCollection<Expense> _expensesCollection;

    public ExpenseService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _expensesCollection = database.GetCollection<Expense>("Expenses");
    }

    public bool AddExpense(Expense expense)
    {
        try
        {
            expense.Id = ObjectId.GenerateNewId().ToString();
            _expensesCollection.InsertOne(expense);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool UpdateExpense(Expense expense)
    {
        try
        {
            var filter = Builders<Expense>.Filter.Eq(e => e.Id, expense.Id);
            var update = Builders<Expense>.Update
                .Set(e => e.Name, expense.Name)
                .Set(e => e.Amount, expense.Amount)
                .Set(e => e.Date, expense.Date)
                .Set(e => e.Description, expense.Description);

            var result = _expensesCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteExpense(string expenseId)
    {
        try
        {
            var filter = Builders<Expense>.Filter.Eq(e => e.Id, expenseId);
            var result = _expensesCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public List<Expense> GetAllExpenses()
    {
        return _expensesCollection.Find(new BsonDocument()).ToList();
    }

    public Expense GetExpenseById(string expenseId)
    {
        return _expensesCollection.Find(e => e.Id == expenseId).FirstOrDefault();
    }
}
