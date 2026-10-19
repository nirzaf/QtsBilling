using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestExpenseServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void AddExpense_ValidExpense_ReturnsTrue()
    {
        // Arrange
        var expense = new Faker<Expense>()
            .RuleFor(e => e.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(e => e.Name, f => f.Commerce.ProductName())
            .RuleFor(e => e.Amount, f => f.Finance.Amount())
            .RuleFor(e => e.Date, f => f.Date.Past())
            .RuleFor(e => e.Description, f => f.Lorem.Sentence())
            .Generate();

        var service = new ExpenseService();

        // Act
        var result = service.AddExpense(expense);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        var isDeleted = DeleteExpense_ExistingExpense_Returns_True(expense.Id);
        Assert.True(isDeleted);
    }

    [Fact]
    public void UpdateExpense_ValidExpense_ReturnsTrue()
    {
        // Arrange
        var expense = new Faker<Expense>()
            .RuleFor(e => e.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(e => e.Name, f => f.Commerce.ProductName())
            .RuleFor(e => e.Amount, f => f.Finance.Amount())
            .RuleFor(e => e.Date, f => f.Date.Past())
            .RuleFor(e => e.Description, f => f.Lorem.Sentence())
            .Generate();

        var service = new ExpenseService();
        service.AddExpense(expense);

        var updatedExpense = new Faker<Expense>()
            .RuleFor(e => e.Id, expense.Id)
            .RuleFor(e => e.Name, f => f.Commerce.ProductName())
            .RuleFor(e => e.Amount, f => f.Finance.Amount())
            .RuleFor(e => e.Date, f => f.Date.Past())
            .RuleFor(e => e.Description, f => f.Lorem.Sentence())
            .Generate();

        // Act
        var result = service.UpdateExpense(updatedExpense);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        var isDeleted = DeleteExpense_ExistingExpense_Returns_True(expense.Id);
        Assert.True(isDeleted);
    }

    [Fact]
    public void DeleteExpense_ExistingExpense_ReturnsTrue()
    {
        // Arrange
        var expense = new Faker<Expense>()
            .RuleFor(e => e.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(e => e.Name, f => f.Commerce.ProductName())
            .RuleFor(e => e.Amount, f => f.Finance.Amount())
            .RuleFor(e => e.Date, f => f.Date.Past())
            .RuleFor(e => e.Description, f => f.Lorem.Sentence())
            .Generate();

        var service = new ExpenseService();
        service.AddExpense(expense);

        // Act
        var result = service.DeleteExpense(expense.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetAllExpenses_ReturnsListOfExpenses()
    {
        // Arrange
        var service = new ExpenseService();

        // Act
        var result = service.GetAllExpenses();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<Expense>>(result);
    }

    [Fact]
    public void GetExpenseById_ExistingExpense_ReturnsExpense()
    {
        // Arrange
        var expense = new Faker<Expense>()
            .RuleFor(e => e.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(e => e.Name, f => f.Commerce.ProductName())
            .RuleFor(e => e.Amount, f => f.Finance.Amount())
            .RuleFor(e => e.Date, f => f.Date.Past())
            .RuleFor(e => e.Description, f => f.Lorem.Sentence())
            .Generate();

        var service = new ExpenseService();
        service.AddExpense(expense);

        // Act
        var result = service.GetExpenseById(expense.Id);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expense.Id, result.Id);

        // Clean up
        var isDeleted = DeleteExpense_ExistingExpense_Returns_True(expense.Id);
        Assert.True(isDeleted);
    }

    private bool DeleteExpense_ExistingExpense_Returns_True(string id)
    {
        ExpenseService service = new();
        var result = service.DeleteExpense(id);
        output.LogResponse("Expense with id " + id + " deleted successfully");
        return result;
    }
}