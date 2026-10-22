using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Sales;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Sales;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The register workbench (specification task 19, recipe steps 1 to 4): the screen ships as a routed page in the
/// shared library, every host can resolve what it injects, and the sentences it shows about custody and about a
/// counted difference say what the server actually enforces. The drawer arithmetic itself is proven against the
/// real host in <see cref="Integration.CashCustodyTests"/>; nothing here restates it as a mock.
/// </summary>
public sealed class RegisterWorkbenchTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture = fixture;

    [Fact]
    public void T19_05_the_register_workbench_is_a_routed_shipped_screen_and_every_host_registers_what_it_injects()
    {
        var root = InvoiceEditorTests.RepositoryRoot();
        var page = typeof(Registers);

        var template = Assert.Single(page.GetCustomAttributes<RouteAttribute>()).Template.Trim('/');
        Assert.Equal("sales/registers", template);

        var row = Assert.Single(ShellNavigation.Routes, candidate => candidate.Href == "sales/registers");
        Assert.Equal(ShellAvailability.Available, row.Availability);
        Assert.True(ShellNavigation.IsNavigable(row), "the register screen is offered but not navigable");
        Assert.Equal(Permissions.RegistersManage, row.RequiredPermission);

        var injected = page
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface && type.Assembly == typeof(ICashRegisterApiClient).Assembly)
            .Distinct()
            .ToArray();

        Assert.Contains(typeof(ICashRegisterApiClient), injected);
        Assert.Contains(typeof(IInventoryApiClient), injected);
        Assert.Contains(typeof(IAccountApiClient), injected);

        using var scope = _fixture.Factory.Services.CreateScope();

        var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the register screen but registers none for: {string.Join(", ", missing)}");

        foreach (var startup in new[] { @"QtsWebApp\QtsWebApp.Client\Program.cs", @"QtsBilling.Maui\MauiProgram.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, startup));
            var unnamed = injected.Select(type => type.Name).Where(name => !source.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(unnamed.Length == 0, $"{startup} routes the register screen but never registers: {string.Join(", ", unnamed)}");
        }
    }

    [Fact]
    public void T19_06_the_screen_says_what_a_counted_difference_does_and_what_it_never_does()
    {
        var matching = RegisterRules.VarianceSentence(100m, 100m, "QAR");
        Assert.Contains("no journal is produced", matching, StringComparison.Ordinal);

        var over = RegisterRules.VarianceSentence(105m, 100m, "QAR");
        Assert.Contains("Over by 5.00 QAR", over, StringComparison.Ordinal);
        Assert.Contains("cash-over/short account", over, StringComparison.Ordinal);

        // A short drawer is named as a shortage, and the sentence states the rule that rejected offline sales are
        // never used to make the difference disappear (specification D07, task 19, recipe step 4).
        var shortBy = RegisterRules.VarianceSentence(97.5m, 100m, "QAR");
        Assert.Contains("Short by 2.50 QAR", shortBy, StringComparison.Ordinal);
        Assert.Contains("never netted out", shortBy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T19_07_the_close_is_offered_only_when_the_servers_custody_answer_says_it_may_be()
    {
        var clean = Custody(closable: true, block: null);
        Assert.True(RegisterRules.CanAttemptClose(clean));

        var held = Custody(closable: false, block: "2 unresolved item(s) on this drawer have not been carried forward to a named custodian.");
        Assert.False(RegisterRules.CanAttemptClose(held));
        Assert.Contains("carried forward to a named custodian", RegisterRules.BlockSentence(held), StringComparison.Ordinal);

        // A refusal with no sentence from the server is still not a silent go-ahead.
        var mute = Custody(closable: false, block: null);
        Assert.Equal("This drawer still holds unresolved cash.", RegisterRules.BlockSentence(mute));
    }

    [Fact]
    public void T19_08_only_money_the_server_cannot_see_needs_a_declared_figure_and_keys_stay_inside_the_servers_bound()
    {
        var payment = new UnresolvedCashItemDto(
            CashCustodySourceState.ProvisionalPayment, Guid.NewGuid(), "payment against sale S-1", 30m, "QAR", null, null, null, "unconfirmed");
        var operation = new UnresolvedCashItemDto(
            CashCustodySourceState.DeviceOperation, Guid.NewGuid(), "operation from Till 2", 25m, "QAR", Guid.NewGuid(), "Till 2", null, "awaiting review");
        var terminal = new UnresolvedCashItemDto(
            CashCustodySourceState.DeviceCustody, Guid.NewGuid(), "terminal Till 3", 0m, "QAR", Guid.NewGuid(), "Till 3", null, "never reported");

        // A payment the server already holds a figure for is carried as stated; the two kinds whose amount lives on
        // the terminal have to be declared by the person receiving them (specification D07).
        Assert.False(RegisterRules.NeedsDeclaredAmount(payment));
        Assert.True(RegisterRules.NeedsDeclaredAmount(operation));
        Assert.True(RegisterRules.NeedsDeclaredAmount(terminal));
        Assert.Equal(25m, RegisterRules.MinimumCarried(operation));
        Assert.Equal(0m, RegisterRules.MinimumCarried(terminal));

        Assert.Equal("30.00 QAR", RegisterRules.MoneyText(30m, "QAR"));
        Assert.Equal("2.50", RegisterRules.Money(2.5m));

        // A sub-cent difference is shown rather than rounded away, because a rounded-away difference is exactly how
        // a drawer stops agreeing with its movements.
        Assert.Equal("0.0050", RegisterRules.Money(0.005m));

        // The command key is one per intention, distinct between intentions, and short enough for the header the
        // server validates (1-64 characters).
        var keys = Enumerable.Range(0, 50).Select(_ => RegisterRules.NewCommandKey("drawer-close")).ToArray();
        Assert.Equal(50, keys.Distinct().Count());
        Assert.All(keys, key => Assert.True(key.Length is > 0 and <= 64, $"the key {key} is {key.Length} characters"));
        Assert.Contains("drawer-close", keys[0], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static DrawerCustodyDto Custody(bool closable, string? block) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "QAR",
        100m,
        0m,
        closable ? [] : [new UnresolvedCashItemDto(CashCustodySourceState.DeviceCustody, Guid.NewGuid(), "terminal Till 3", 0m, "QAR", null, null, null, "never reported")],
        closable ? [] : [new DeviceCustodyEvidenceDto(Guid.NewGuid(), "Till 3", "Active", null, false, 0, 0m, "never reported")],
        [],
        closable ? 0m : 0m,
        closable,
        block);
}
