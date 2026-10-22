using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Expenses;
using QtsBilling.Contracts.Identity;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Expenses;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The expense workbench as a shipped screen (specification task 28): the rules state what posting and
/// reversal mean, every host that renders the pages can resolve what they inject, the markup asks for no
/// identifier a person could not know, and the reads and writes it relies on answer the way the operator
/// needs. The ledger arithmetic itself belongs to the expense posting tests; this file proves the screen
/// is reachable, honest, and cannot invent an expense.
/// </summary>
public sealed class ExpenseJourneyTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public ExpenseJourneyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public void T28_the_rules_keep_a_posting_a_reversal_and_a_key_apart()
    {
        // Posting is one balanced entry; LocalCore never pretends to take a card payment.
        Assert.Contains("one balanced ledger entry", ExpenseRules.PostingRule, StringComparison.Ordinal);
        Assert.Contains("never calls a payment", ExpenseRules.PostingRule, StringComparison.Ordinal);
        Assert.Contains("never stores a card number", ExpenseRules.PostingRule, StringComparison.Ordinal);

        // A reversal is a reasoned mirror that can happen once, and a closed period is not rewritten.
        Assert.Contains("mirrors the original", ExpenseRules.ReversalRule, StringComparison.Ordinal);
        Assert.Contains("only once", ExpenseRules.ReversalRule, StringComparison.Ordinal);
        Assert.Contains("closed period is not rewritten", ExpenseRules.ReversalRule, StringComparison.Ordinal);

        // Tax is entered as percent and converted once, and the preview is only an estimate.
        Assert.Contains("percentage", ExpenseRules.TaxRule, StringComparison.Ordinal);
        Assert.Contains("fraction exactly once", ExpenseRules.TaxRule, StringComparison.Ordinal);
        Assert.Contains("estimate until the server confirms", ExpenseRules.TaxRule, StringComparison.Ordinal);

        // Paid-via options are the ones the server accepts; card still settles through the bank.
        Assert.Equal(["cash", "cheque", "bankTransfer", "card"], ExpenseRules.PaidViaOptions);
        Assert.Equal("Cash", ExpenseRules.PaidViaText("cash"));
        Assert.Equal("Card (settled through the bank)", ExpenseRules.PaidViaText("card"));
        Assert.Equal("1000 cash", ExpenseRules.CreditAccountText("cash"));
        Assert.Equal("1010 bank", ExpenseRules.CreditAccountText("bankTransfer"));
        Assert.Equal("1010 bank", ExpenseRules.CreditAccountText("card"));

        // The preview converts percent once and keeps the paid total honest for both tax modes.
        Assert.Equal((100m, 15m, 115m), ExpenseRules.Preview(100m, 15m, "Exclusive"));
        Assert.Equal((86.9565m, 13.0435m, 100m), ExpenseRules.Preview(100m, 15m, "Inclusive"));
        Assert.Equal((100m, 0m, 100m), ExpenseRules.Preview(100m, 15m, "Exempt"));
        Assert.Equal((100m, 0m, 100m), ExpenseRules.Preview(100m, 0m, "Exclusive"));

        // A posted expense with a journal can be reversed; an already reversed one cannot.
        var posted = Expense(Guid.CreateVersion7(), reversedAt: null);
        Assert.True(ExpenseRules.CanReverse(posted));
        var reversed = Expense(Guid.CreateVersion7(), reversedAt: DateTimeOffset.UtcNow);
        Assert.False(ExpenseRules.CanReverse(reversed));
        Assert.True(ExpenseRules.IsReversed(reversed));

        // Money is read through the register's own scale.
        Assert.Equal("690.00", ExpenseRules.Money(690m));

        // Refusals name the rule and the answer, not just the status.
        string Sentence(string code, string message = "refused") => ExpenseRules.RefusalSentence(
            new ApiException(409, code, message, new Dictionary<string, string[]>()), "this reversal");

        Assert.Contains("already reversed", Sentence(ApiErrorCodes.AlreadyReversed), StringComparison.Ordinal);
        Assert.Contains("Nothing new was posted", Sentence(ApiErrorCodes.AlreadyReversed), StringComparison.Ordinal);
        Assert.Contains("Start a new intention", Sentence(ApiErrorCodes.SameKeyDifferentPayload), StringComparison.Ordinal);
        Assert.Contains("returned the stored expense", Sentence(ApiErrorCodes.IdempotencyKeyReused), StringComparison.Ordinal);
        Assert.Contains("not in this business", Sentence(ApiErrorCodes.NotFound), StringComparison.Ordinal);
        Assert.Contains("open period", Sentence(ApiErrorCodes.AccountingPeriodClosed), StringComparison.Ordinal);
        Assert.Contains("Read the expense back", Sentence(ApiErrorCodes.OptimisticConcurrencyFailure), StringComparison.Ordinal);

        // Only codes that prove nothing was written release the intention; an unknown answer keeps key and body.
        Assert.True(ExpenseRules.CommittedNothing(ApiErrorCodes.AlreadyReversed));
        Assert.True(ExpenseRules.CommittedNothing(ApiErrorCodes.AccountingPeriodClosed));
        Assert.False(ExpenseRules.CommittedNothing(ApiErrorCodes.InternalError));
        Assert.False(ExpenseRules.CommittedNothing(code: null));

        // One key per intention, bounded by the header the server validates, distinct per attempt.
        var expenseId = Guid.CreateVersion7();
        var key = ExpenseRules.NewKey("post", expenseId.ToString("N")[..8]);
        Assert.True(key.Length <= ExpenseRules.MaxKeyLength);
        Assert.StartsWith("post-", key, StringComparison.Ordinal);
        Assert.NotEqual(key, ExpenseRules.NewKey("post", expenseId.ToString("N")[..8]));
        Assert.NotEqual(key, ExpenseRules.NewKey("reverse", expenseId.ToString("N")[..8]));
    }

    [Fact]
    public async Task T28_the_expense_screens_are_wired_into_every_host_that_can_show_them()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        Assert.Contains("expenses", typeof(Expenses)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Select(route => route.Template.Trim('/')));
        Assert.Contains("expenses/new", typeof(ExpenseEditor)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Select(route => route.Template.Trim('/')));
        Assert.Contains("expenses/{Id:guid}", typeof(ExpenseEditor)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Select(route => route.Template.Trim('/')));

        var row = Assert.Single(ShellNavigation.Routes, route => route.Href == "expenses");
        Assert.Equal(ShellAvailability.Available, row.Availability);
        Assert.Equal(Permissions.ExpensesManage, row.RequiredPermission);
        Assert.Null(row.ArrivalNote);

        foreach (var page in new[] { typeof(Expenses), typeof(ExpenseEditor) })
        {
            var injected = page
                .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
                .Select(property => property.PropertyType)
                .Where(type => type.IsInterface)
                .Distinct()
                .ToArray();

            Assert.Contains(typeof(IExpenseApiClient), injected);

            using var scope = _fixture.Factory.Services.CreateScope();
            var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
            Assert.True(missing.Length == 0, $"the server host renders {page.Name} but registers none for: {string.Join(", ", missing)}");
        }

        foreach (var startup in new[] { "QtsWebApp/QtsWebApp.Client/Program.cs", "QtsBilling.Maui/MauiProgram.cs" })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, startup));
            Assert.Contains("IExpenseApiClient", source, StringComparison.Ordinal);
            Assert.Contains("ExpenseApiClient", source, StringComparison.Ordinal);
        }

        // The shipped markup asks for no key, reaches for no public resource, and hardcodes no scale or clock.
        var markup = string.Concat(
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Expenses/Expenses.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Expenses/Expenses.razor.cs")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Expenses/ExpenseEditor.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Expenses/ExpenseEditor.razor.cs")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Expenses/ExpenseRules.cs")));

        foreach (var forbidden in new[]
        {
            "Expense id", "Category id", "Journal entry id", "access_token",
            "http://", "https://", "ToString(\"N2\")", "DateTime.Today", "Guid.Parse",
        })
        {
            Assert.DoesNotContain(forbidden, markup, StringComparison.Ordinal);
        }

        Assert.Contains("one balanced ledger entry", markup, StringComparison.Ordinal);
        Assert.Contains("mirrors the original", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T28_the_expense_screen_names_documents_and_cannot_reach_another_business_expense()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var expenses = new ExpenseApiClient(http);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // Cash and bank accounts exist before money can leave; the fixture starts a business with no chart.
        await _fixture.WithFreshContextAsync(db =>
            FixtureAccounting.EnsurePayablesAsync(db, _fixture.BusinessA.BusinessId));

        // A category the operator created, read back the way the list filters by it.
        var category = await expenses.CreateCategoryAsync(
            new SaveExpenseCategoryRequest($"Office supplies {suffix}", AccountId: null));
        Assert.Equal($"Office supplies {suffix}", category.Name);
        Assert.True(category.IsActive);

        var categories = await expenses.ListCategoriesAsync(includeInactive: true);
        Assert.Contains(categories, row => row.Id == category.Id && row.Name == category.Name);

        // The expense the operator posts: keyed once, named by description and category, never by identifier.
        var key = ExpenseRules.NewKey("post", suffix);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var posted = await expenses.CreateExpenseAsync(new SaveExpenseRequest(
            category.Id,
            _fixture.BusinessA.LocationId,
            SupplierId: null,
            $"Courier run {suffix}",
            115m,
            today,
            "cash",
            $"REF-{suffix}",
            TaxRate: 0.15m,
            TaxAmount: 15m,
            TaxMode: "Exclusive"), key);

        Assert.Equal($"Courier run {suffix}", posted.Description);
        Assert.Equal("Office supplies " + suffix, posted.ExpenseCategoryName);
        Assert.Equal("Cash", posted.PaidVia);
        Assert.True(posted.JournalEntryId is { });
        Assert.True(posted.JournalEntryNumber is { Length: > 0 });
        Assert.Null(posted.ReversedAtUtc);
        Assert.Equal(0.15m, posted.TaxRate);
        Assert.Equal(15m, posted.TaxAmount);

        // Re-sending the same key with the same body returns the stored expense instead of posting twice.
        var replayed = await expenses.CreateExpenseAsync(new SaveExpenseRequest(
            category.Id,
            _fixture.BusinessA.LocationId,
            SupplierId: null,
            $"Courier run {suffix}",
            115m,
            today,
            "cash",
            $"REF-{suffix}",
            TaxRate: 0.15m,
            TaxAmount: 15m,
            TaxMode: "Exclusive"), key);
        Assert.Equal(posted.Id, replayed.Id);

        // The same key with a different body is refused as a different intention, not silently rewritten.
        var rewritten = await CaptureAsync(() => expenses.CreateExpenseAsync(new SaveExpenseRequest(
            category.Id,
            _fixture.BusinessA.LocationId,
            SupplierId: null,
            $"Courier run {suffix} (edited)",
            130m,
            today,
            "cash",
            $"REF-{suffix}",
            TaxRate: 0.15m,
            TaxAmount: 16.96m,
            TaxMode: "Exclusive"), key));
        Assert.Equal(ApiErrorCodes.SameKeyDifferentPayload, rewritten.Code);

        // The list the screen offers names the category, the branch currency and the journal numbers.
        var page = await expenses.ListExpensesAsync(search: $"Courier run {suffix}");
        var listed = Assert.Single(page.Items, row => row.Id == posted.Id);
        Assert.Equal(posted.Description, listed.Description);
        Assert.Equal("Office supplies " + suffix, listed.ExpenseCategoryName);
        Assert.Equal(posted.JournalEntryNumber, listed.JournalEntryNumber);
        Assert.True(page.PageSize is >= 1);

        // A reversal mirrors the original with its own reasoned answer, and the original stays on the record.
        var reversed = await expenses.ReverseExpenseAsync(
            posted.Id,
            new ExpenseActionRequest("wrong amount on the receipt", "Courier charged the wrong branch"),
            ExpenseRules.NewKey("reverse", suffix));
        Assert.Equal(posted.Id, reversed.Id);
        Assert.Equal(posted.Description, reversed.Description);
        Assert.True(reversed.ReversedAtUtc is { });
        Assert.True(reversed.ReversalEntryNumber is { Length: > 0 });
        Assert.Equal(posted.JournalEntryNumber, reversed.JournalEntryNumber);

        // Each expense can be reversed only once - the second reason is refused as already reversed.
        var again = await CaptureAsync(() => expenses.ReverseExpenseAsync(
            posted.Id,
            new ExpenseActionRequest("a second reason", Note: null),
            ExpenseRules.NewKey("reverse2", suffix)));
        Assert.Equal(ApiErrorCodes.AlreadyReversed, again.Code);
        Assert.Contains("one-time transition", again.Message, StringComparison.OrdinalIgnoreCase);

        // Another business cannot reach this expense by guessing its identifier.
        var otherBusiness = new ExpenseApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB));
        var foreign = await CaptureAsync(() => otherBusiness.GetExpenseAsync(posted.Id));
        Assert.Equal(404, foreign.StatusCode);
        var foreignWrite = await CaptureAsync(() => otherBusiness.ReverseExpenseAsync(
            posted.Id,
            new ExpenseActionRequest("not theirs", Note: null),
            ExpenseRules.NewKey("reverse3", suffix)));
        Assert.Equal(404, foreignWrite.StatusCode);

        // A role that may not hold expense authority is refused the read, and the expense is exactly as it was.
        var viewer = new ExpenseApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Viewer));
        var refused = await CaptureAsync(() => viewer.ListExpensesAsync());
        Assert.True(refused.StatusCode is 401 or 403, $"the expense read answered {refused.StatusCode}, expected a refusal");
        Assert.True((await expenses.GetExpenseAsync(posted.Id)).ReversedAtUtc is { });
    }

    private static ExpenseDto Expense(Guid id, DateTimeOffset? reversedAt) => new(
        id,
        ExpenseCategoryId: null,
        ExpenseCategoryName: null,
        LocationId: null,
        SupplierId: null,
        Description: "posted",
        Amount: 10m,
        Currency: "QAR",
        OccurredOn: new DateOnly(2026, 1, 15),
        PaidVia: "cash",
        Reference: null,
        JournalEntryId: Guid.CreateVersion7(),
        JournalEntryNumber: "JE-1",
        ReversedAtUtc: reversedAt,
        ReversalEntryId: null,
        ReversalEntryNumber: null,
        CreatedAtUtc: DateTimeOffset.UnixEpoch);

    private static async Task<ApiException> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("the request should have been refused, but it succeeded");
    }
}
