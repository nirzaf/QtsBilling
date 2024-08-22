using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestAccountingServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void AddAccount_ValidAccount_ReturnsTrue()
    {
        // Arrange
        var account = new Faker<Account>()
            .RuleFor(a => a.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(a => a.Name, f => f.Company.CompanyName())
            .RuleFor(a => a.Type, f => f.PickRandom<AccountType>())
            .RuleFor(a => a.Balance, f => f.Finance.Amount())
            .Generate();

        var service = new AccountingService();

        // Act
        var result = service.AddAccount(account);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        service.DeleteAccount(account.Id);
    }

    [Fact]
    public void RecordTransaction_ValidTransaction_ReturnsTrue()
    {
        // Arrange
        var transaction = new Faker<Transaction>()
            .RuleFor(t => t.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(t => t.Date, f => f.Date.Past())
            .RuleFor(t => t.Description, f => f.Lorem.Sentence())
            .RuleFor(t => t.Entries, f =>
            [
                new TransactionEntry
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    AccountId = ObjectId.GenerateNewId().ToString(),
                    Amount = f.Finance.Amount(),
                    Type = f.PickRandom<EntryType>(),
                }
            ])
            .Generate();

        var service = new AccountingService();

        // Act
        var result = service.RecordTransaction(transaction);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        service.DeleteTransaction(transaction.Id);
    }

    [Fact]
    public void GetAccounts_ReturnsListOfAccounts()
    {
        // Arrange
        var service = new AccountingService();

        // Act
        var result = service.GetAccounts();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<Account>>(result);
    }

    [Fact]
    public void GetTransactions_ReturnsListOfTransactions()
    {
        // Arrange
        var service = new AccountingService();

        // Act
        var result = service.GetTransactions();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<Transaction>>(result);
    }

    [Fact]
    public void GetTransactionsByAccountId_ValidAccountId_ReturnsListOfTransactions()
    {
        // Arrange
        var accountId = ObjectId.GenerateNewId().ToString();
        var transaction = new Faker<Transaction>()
            .RuleFor(t => t.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(t => t.Date, f => f.Date.Past())
            .RuleFor(t => t.Description, f => f.Lorem.Sentence())
            .RuleFor(t => t.Entries, f =>
            [
                new TransactionEntry
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    AccountId = accountId,
                    Amount = f.Finance.Amount(),
                    Type = f.PickRandom<EntryType>()
                }
            ])
            .Generate();

        var service = new AccountingService();
        service.RecordTransaction(transaction);

        // Act
        var result = service.GetTransactionsByAccountId(accountId);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<Transaction>>(result);

        // Clean up
        service.DeleteTransaction(transaction.Id);
    }

    [Fact]
    public void GetTransactionsByDateRange_ValidDateRange_ReturnsListOfTransactions()
    {
        // Arrange
        var startDate = DateTime.Now.AddMonths(-1);
        var endDate = DateTime.Now;
        var transaction = new Faker<Transaction>()
            .RuleFor(t => t.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(t => t.Date, f => f.Date.Between(startDate, endDate))
            .RuleFor(t => t.Description, f => f.Lorem.Sentence())
            .RuleFor(t => t.Entries, f =>
            [
                new()
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    AccountId = ObjectId.GenerateNewId().ToString(),
                    Amount = f.Finance.Amount(),
                    Type = f.PickRandom<EntryType>()
                }
            ])
            .Generate();

        var service = new AccountingService();
        service.RecordTransaction(transaction);

        // Act
        var result = service.GetTransactionsByDateRange(startDate, endDate);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<Transaction>>(result);

        // Clean up
        service.DeleteTransaction(transaction.Id);
    }

    [Fact]
    public void GetAccountBalance_ValidAccountId_ReturnsBalance()
    {
        // Arrange
        var account = new Faker<Account>()
            .RuleFor(a => a.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(a => a.Name, f => f.Company.CompanyName())
            .RuleFor(a => a.Type, f => f.PickRandom<AccountType>())
            .RuleFor(a => a.Balance, f => f.Finance.Amount())
            .Generate();

        var service = new AccountingService();
        service.AddAccount(account);

        // Act
        var result = service.GetAccountBalance(account.Id);
        output.LogResponse(result);

        // Assert
        Assert.Equal(account.Balance, result);

        // Clean up
        service.DeleteAccount(account.Id);
    }

    [Fact]
    public void UpdateAccountBalance_ValidAccountId_ReturnsTrue()
    {
        // Arrange
        var account = new Faker<Account>()
            .RuleFor(a => a.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(a => a.Name, f => f.Company.CompanyName())
            .RuleFor(a => a.Type, f => f.PickRandom<AccountType>())
            .RuleFor(a => a.Balance, f => f.Finance.Amount())
            .Generate();

        var service = new AccountingService();
        service.AddAccount(account);

        var newBalance = new Faker().Finance.Amount();

        // Act
        var result = service.UpdateAccountBalance(account.Id, newBalance);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        service.DeleteAccount(account.Id);
    }

    [Fact]
    public void DeleteAccount_ValidAccountId_ReturnsTrue()
    {
        // Arrange
        var account = new Faker<Account>()
            .RuleFor(a => a.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(a => a.Name, f => f.Company.CompanyName())
            .RuleFor(a => a.Type, f => f.PickRandom<AccountType>())
            .RuleFor(a => a.Balance, f => f.Finance.Amount())
            .Generate();

        var service = new AccountingService();
        service.AddAccount(account);

        // Act
        var result = service.DeleteAccount(account.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void DeleteTransaction_ValidTransactionId_ReturnsTrue()
    {
        // Arrange
        var transaction = new Faker<Transaction>()
            .RuleFor(t => t.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(t => t.Date, f => f.Date.Past())
            .RuleFor(t => t.Description, f => f.Lorem.Sentence())
            .RuleFor(t => t.Entries, f =>
            [
                new TransactionEntry
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    AccountId = ObjectId.GenerateNewId().ToString(),
                    Amount = f.Finance.Amount(),
                    Type = f.PickRandom<EntryType>()
                }
            ])
            .Generate();

        var service = new AccountingService();
        service.RecordTransaction(transaction);

        // Act
        var result = service.DeleteTransaction(transaction.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void SearchAccounts_ValidSearchText_ReturnsListOfAccounts()
    {
        // Arrange
        var account = new Faker<Account>()
            .RuleFor(a => a.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(a => a.Name, f => f.Company.CompanyName())
            .RuleFor(a => a.Type, f => f.PickRandom<AccountType>())
            .RuleFor(a => a.Balance, f => f.Finance.Amount())
            .Generate();

        var service = new AccountingService();
        service.AddAccount(account);

        // Act
        var result = service.SearchAccounts(account.Name);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<Account>>(result);

        // Clean up
        service.DeleteAccount(account.Id);
    }

    [Fact]
    public void SearchTransactions_ValidSearchText_ReturnsListOfTransactions()
    {
        // Arrange
        var transaction = new Faker<Transaction>()
            .RuleFor(t => t.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(t => t.Date, f => f.Date.Past())
            .RuleFor(t => t.Description, f => f.Lorem.Sentence())
            .RuleFor(t => t.Entries, f =>
            [
                new TransactionEntry
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    AccountId = ObjectId.GenerateNewId().ToString(),
                    Amount = f.Finance.Amount(),
                    Type = f.PickRandom<EntryType>()
                }
            ])
            .Generate();

        var service = new AccountingService();
        service.RecordTransaction(transaction);

        // Act
        var result = service.SearchTransactions(transaction.Description);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<List<Transaction>>(result);

        // Clean up
        service.DeleteTransaction(transaction.Id);
    }
}
