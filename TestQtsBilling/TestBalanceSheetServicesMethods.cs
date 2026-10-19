using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestBalanceSheetServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void create_balance_sheet()
    {
        // Arrange
        var balanceSheet = new Faker<BalanceSheet>()
            .RuleFor(b => b.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(b => b.Assets, f => f.Finance.Amount())
            .RuleFor(b => b.Liabilities, f => f.Finance.Amount())
            .RuleFor(b => b.Equity, f => f.Finance.Amount())
            .Generate();

        var service = new BalanceSheetService();

        // Act
        var result = service.CreateBalanceSheet(balanceSheet);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        bool isDeleted = DeleteBalanceSheet_ExistingBalanceSheet_ReturnsTrue(balanceSheet.Id!);
        Assert.True(isDeleted);
    }

    [Fact]
    public void update_balance_sheet()
    {
        // Arrange
        var balanceSheet = new Faker<BalanceSheet>()
            .RuleFor(b => b.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(b => b.Assets, f => f.Finance.Amount())
            .RuleFor(b => b.Liabilities, f => f.Finance.Amount())
            .RuleFor(b => b.Equity, f => f.Finance.Amount())
            .Generate();

        var service = new BalanceSheetService();
        service.CreateBalanceSheet(balanceSheet);

        // Act
        balanceSheet.Assets = new Faker().Finance.Amount();
        balanceSheet.Liabilities = new Faker().Finance.Amount();
        balanceSheet.Equity = new Faker().Finance.Amount();
        var result = service.UpdateBalanceSheet(balanceSheet);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        bool isDeleted = DeleteBalanceSheet_ExistingBalanceSheet_ReturnsTrue(balanceSheet.Id!);
        Assert.True(isDeleted);
    }

    [Fact]
    public void delete_balance_sheet()
    {
        // Arrange
        var balanceSheet = new Faker<BalanceSheet>()
            .RuleFor(b => b.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(b => b.Assets, f => f.Finance.Amount())
            .RuleFor(b => b.Liabilities, f => f.Finance.Amount())
            .RuleFor(b => b.Equity, f => f.Finance.Amount())
            .Generate();

        var service = new BalanceSheetService();
        service.CreateBalanceSheet(balanceSheet);

        // Act
        var result = service.DeleteBalanceSheet(balanceSheet.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void get_all_balance_sheets()
    {
        // Arrange
        var balanceSheet1 = new Faker<BalanceSheet>()
            .RuleFor(b => b.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(b => b.Assets, f => f.Finance.Amount())
            .RuleFor(b => b.Liabilities, f => f.Finance.Amount())
            .RuleFor(b => b.Equity, f => f.Finance.Amount())
            .Generate();

        var balanceSheet2 = new Faker<BalanceSheet>()
            .RuleFor(b => b.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(b => b.Assets, f => f.Finance.Amount())
            .RuleFor(b => b.Liabilities, f => f.Finance.Amount())
            .RuleFor(b => b.Equity, f => f.Finance.Amount())
            .Generate();

        var service = new BalanceSheetService();
        service.CreateBalanceSheet(balanceSheet1);
        service.CreateBalanceSheet(balanceSheet2);

        // Act
        var result = service.GetAllBalanceSheets();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Count >= 2);

        // Clean up
        bool isDeleted1 = DeleteBalanceSheet_ExistingBalanceSheet_ReturnsTrue(balanceSheet1.Id!);
        bool isDeleted2 = DeleteBalanceSheet_ExistingBalanceSheet_ReturnsTrue(balanceSheet2.Id!);
        Assert.True(isDeleted1);
        Assert.True(isDeleted2);
    }

    [Fact]
    public void get_balance_sheet_by_id()
    {
        // Arrange
        var balanceSheet = new Faker<BalanceSheet>()
            .RuleFor(b => b.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(b => b.Assets, f => f.Finance.Amount())
            .RuleFor(b => b.Liabilities, f => f.Finance.Amount())
            .RuleFor(b => b.Equity, f => f.Finance.Amount())
            .Generate();

        var service = new BalanceSheetService();
        service.CreateBalanceSheet(balanceSheet);

        // Act
        var result = service.GetBalanceSheetById(balanceSheet.Id);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(balanceSheet.Id, result.Id);

        // Clean up
        bool isDeleted = DeleteBalanceSheet_ExistingBalanceSheet_ReturnsTrue(balanceSheet.Id!);
        Assert.True(isDeleted);
    }

    private bool DeleteBalanceSheet_ExistingBalanceSheet_ReturnsTrue(string? id)
    {
        BalanceSheetService service = new();
        var result = service.DeleteBalanceSheet(id);
        output.LogResponse("BalanceSheet with id " + id + " deleted successfully");
        return result;
    }
}
