using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Documents;
using QtsBilling.Ui.Components;
using QtsBilling.Ui.Pages.Expenses;
using QtsBilling.Ui.Pages.Invoices;
using QtsWebApp.Features.Documents;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The evidence surface as a shipped part of the document screens (specification task 39, recipe step 4, proven from
/// the operator's side): the panel exists in the shared library and is placed on the invoice and expense screens that
/// own a document, every host that renders it can resolve what it injects, the bytes travel only over the host's own
/// authenticated client, and a saved copy is a bare file name on a machine - never a URL, never an active document,
/// never a remote converter. The tenant and integrity proofs stay with <see cref="LocalDocumentTests"/>; this file
/// proves the screen is reachable, honest about what it can do, and unable to reach anything off this machine.
/// </summary>
public sealed class DocumentJourneyTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public DocumentJourneyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T39_the_evidence_panel_is_part_of_the_document_screens_and_every_host_can_resolve_it()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        // 1. The component is in the shared library, addressed by the screens that own a document.
        var panel = typeof(DocumentEvidence);
        var parameters = panel.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetCustomAttribute<ParameterAttribute>() is not null)
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(nameof(DocumentEvidence.AggregateType), parameters);
        Assert.Contains(nameof(DocumentEvidence.AggregateId), parameters);
        Assert.Contains(nameof(DocumentEvidence.MayManage), parameters);

        var injected = panel.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface)
            .Distinct()
            .ToArray();

        Assert.Contains(typeof(IDocumentApiClient), injected);
        Assert.Contains(typeof(ILocalFileOutput), injected);

        using var scope = _fixture.Factory.Services.CreateScope();
        var missing = injected.Where(type => scope.ServiceProvider.GetService(type) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the evidence panel but registers none for: {string.Join(", ", missing)}");

        // 2. Both document screens place it, and no host that can render them fails to name the client.
        var invoicePage = await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui/Pages/Invoices/InvoiceActions.razor"));
        Assert.Contains(panel.Name, invoicePage, StringComparison.Ordinal);

        // Each screen names its aggregate through the contract's own constant, never a hand-typed string - so a page
        // cannot invent an aggregate the server does not know.
        Assert.Contains($"{nameof(DocumentAggregates)}.{nameof(DocumentAggregates.Invoice)}", invoicePage, StringComparison.Ordinal);

        var expensePage = await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui/Pages/Expenses/ExpenseEditor.razor"));
        Assert.Contains(panel.Name, expensePage, StringComparison.Ordinal);
        Assert.Contains($"{nameof(DocumentAggregates)}.{nameof(DocumentAggregates.Expense)}", expensePage, StringComparison.Ordinal);

        // The procurement side of the same story: the supplier's own file belongs to the order it arrived against.
        var purchasePage = await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui/Pages/Purchasing/PurchaseOrderEditor.razor"));
        Assert.Contains(panel.Name, purchasePage, StringComparison.Ordinal);
        Assert.Contains($"{nameof(DocumentAggregates)}.{nameof(DocumentAggregates.PurchaseOrder)}", purchasePage, StringComparison.Ordinal);

        foreach (var startup in new[]
        {
            "QtsWebApp/QtsWebApp/Program.cs",
            "QtsWebApp/QtsWebApp.Client/Program.cs",
            "QtsBilling.Maui/MauiProgram.cs",
        })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, startup));
            Assert.Contains(nameof(IDocumentApiClient), source, StringComparison.Ordinal);
            Assert.Contains(nameof(DocumentApiClient), source, StringComparison.Ordinal);
        }

        var server = await File.ReadAllTextAsync(Path.Combine(root, "QtsWebApp/QtsWebApp/Program.cs"));
        Assert.Contains("MapDocumentEndpoints()", server, StringComparison.Ordinal);

        // 3. The panel is reachable through the typed contract the server publishes - no invented paths.
        var routes = server.Contains("MapDocumentEndpoints", StringComparison.Ordinal)
            ? typeof(DocumentEndpoints).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == nameof(DocumentEndpoints.MapDocumentEndpoints))
                .ToArray()
            : [];

        Assert.Single(routes);
        Assert.Equal(
            [DocumentAggregates.Invoice, DocumentAggregates.Expense, DocumentAggregates.PurchaseOrder, DocumentAggregates.PurchaseReceipt],
            DocumentAggregates.Allowed);
    }

    [Fact]
    public async Task T39_a_local_copy_is_a_bare_name_on_a_machine_and_never_a_thing_the_browser_runs()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        // The host bridge is a local-only asset of each host, and it names the formats this instance holds and no
        // others: a report export and a piece of evidence. An active format is absent on purpose.
        foreach (var asset in new[]
        {
            @"QtsWebApp/QtsWebApp/wwwroot/file-output.js",
            @"QtsBilling.Maui/wwwroot/file-output.js",
        })
        {
            var script = await File.ReadAllTextAsync(Path.Combine(root, asset), Encoding.UTF8);

            Assert.Contains("window.qtsFiles", script, StringComparison.Ordinal);
            Assert.Contains("save: function", script, StringComparison.Ordinal);
            Assert.Contains("application/pdf", script, StringComparison.Ordinal);
            Assert.Contains("image/png", script, StringComparison.Ordinal);
            Assert.Contains("hasOwnProperty", script, StringComparison.Ordinal);

            // Nothing in the bridge reaches out: no fetch, no XHR, no remote URL, no navigation.
            foreach (var forbidden in new[] { "fetch(", "XMLHttpRequest", "https://", "http://", "window.location", "form.submit" })
            {
                Assert.DoesNotContain(forbidden, script, StringComparison.Ordinal);
            }
        }

        // The contract guards the same list, so a screen cannot smuggle an executable past the bridge.
        var held = new StoredFile("delivery-note-v1.pdf", [0x25, 0x50, 0x44, 0x46]);
        Assert.Equal(4, held.Bytes);

        Assert.True(StoredFile.HasAllowedExtension("scan.jpeg"));
        Assert.True(StoredFile.HasAllowedExtension("statement.csv"));
        Assert.False(StoredFile.HasAllowedExtension("page.html"));
        Assert.False(StoredFile.HasAllowedExtension("drawing.svg"));
        Assert.False(StoredFile.HasAllowedExtension("program.exe"));
        Assert.False(StoredFile.HasAllowedExtension("no-extension"));

        Assert.Throws<ArgumentException>(() => new StoredFile("../elsewhere.pdf", [0x25]));
        Assert.Throws<ArgumentException>(() => new StoredFile("page.html", [0x3C]));
        Assert.Throws<ArgumentException>(() => new StoredFile(new string('a', 130) + ".pdf", [0x25]));
    }

    [Fact]
    public void T39_the_screen_and_the_server_quote_the_same_limits_and_the_same_words()
    {
        // A rule a screen states is a bound the server enforces, so the two cannot drift.
        Assert.True(DocumentEndpoints.RetentionDays > 365, "evidence a posted document was issued with is kept for years, not days");
        Assert.True(DocumentEvidenceRules.MaxVersionsPerAggregate > 1);
        Assert.True(DocumentEvidenceRules.MaxFileBytes == DocumentEndpoints.MaxHeldBytes,
            "the screen's size bound and the server's are the same figure");
        Assert.Contains("bytes", DocumentEvidenceRules.SizeSentence(6_000_000), StringComparison.Ordinal);
        Assert.Contains("application/pdf", DocumentEvidenceRules.AcceptedContentTypes);
        Assert.Contains("image/png", DocumentEvidenceRules.AcceptedContentTypes);

        // The type gate is the same function the routes call, on the shapes the routes refuse.
        Assert.Equal("application/pdf", DocumentEndpoints.NegotiateContentType([0x25, 0x50, 0x44, 0x46, 0x2D]));
        Assert.Equal("image/jpeg", DocumentEndpoints.NegotiateContentType([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/png", DocumentEndpoints.NegotiateContentType(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Null(DocumentEndpoints.NegotiateContentType(Encoding.ASCII.GetBytes("<html><body>x</body></html>")));
        Assert.Null(DocumentEndpoints.NegotiateContentType(Encoding.ASCII.GetBytes("<svg xmlns=\"x\"></svg>")));
        Assert.Null(DocumentEndpoints.NegotiateContentType([0x4D, 0x5A, 0x90, 0x00]));
        Assert.Null(DocumentEndpoints.NegotiateContentType([]));

        // A file name is composed here, from characters a file name may hold - never from the caller's text as given.
        Assert.Equal("supplier-note-pdf-v2.pdf", DocumentEvidenceRules.ComposeFileName("../../supplier note.PDF", 2, "pdf"));
        Assert.Equal("evidence-v1.png", DocumentEvidenceRules.ComposeFileName("///", 1, "png"));
        Assert.Equal("scan-v3.jpg", DocumentEvidenceRules.ComposeFileName("scan", 3, ".JPG"));
        Assert.Throws<ArgumentException>(() => DocumentEvidenceRules.ComposeFileName("a name", 1, "../exe"));
        Assert.Throws<ArgumentException>(() => DocumentEvidenceRules.ComposeFileName("a name", 1, "html"));

        // A version's place in the history is said in words an operator can act on.
        var current = Evidence("current", true, null);
        var superseded = Evidence("superseded", false, Guid.NewGuid());

        Assert.Contains("held now", DocumentEvidenceRules.VersionWords(current), StringComparison.Ordinal);
        Assert.Contains("replaced, and still readable", DocumentEvidenceRules.VersionWords(superseded), StringComparison.Ordinal);
    }

    [Fact]
    public async Task T39_the_panel_says_what_it_holds_and_offers_no_way_to_edit_a_fact()
    {
        var root = InvoiceEditorTests.RepositoryRoot();
        var markup = await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui/Components/DocumentEvidence.razor"));
        var code = await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui/Components/DocumentEvidence.razor.cs"));

        // It states the rules before it asks for anything, and it names the state it is in.
        Assert.Contains("DocumentEvidenceRules.StorageRule", markup, StringComparison.Ordinal);
        Assert.Contains("DocumentEvidenceRules.AggregateRule", markup, StringComparison.Ordinal);
        Assert.Contains("data-qts-state=\"read-only\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-qts-state=\"empty\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-qts-state=\"refused\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-qts-action=\"retry-evidence\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-qts-action=\"keep-evidence\"", markup, StringComparison.Ordinal);

        // The operator chooses the file and then agrees to place it; nothing is written on the way past a picker.
        Assert.Contains("InputFile", markup, StringComparison.Ordinal);
        Assert.Contains(".pdf,.jpg,.jpeg,.png", markup, StringComparison.Ordinal);
        Assert.Contains("Keep it here", markup, StringComparison.Ordinal);
        Assert.Contains("Not this one", markup, StringComparison.Ordinal);

        // No URL, no inline view, no remote converter, and no edit of a held fact anywhere in the panel.
        foreach (var forbidden in new[] { "http://", "https://", "window.open", "<img src=", "<iframe", "fetch(" })
        {
            Assert.DoesNotContain(forbidden, markup, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }

        // The panel never deletes: a wrong version is answered by the version that replaces it, and a host with
        // nowhere to put a copy says so instead of offering a button that lies.
        Assert.DoesNotContain("DeleteAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveAsync", code, StringComparison.Ordinal);
        Assert.Contains("Files.CanSaveFiles", markup, StringComparison.Ordinal);
        Assert.Contains("This device refused the file", code, StringComparison.Ordinal);

        // A refusal is kept as an answer, not swallowed: the message came from the server's own detail.
        Assert.Contains("exception.Message", code, StringComparison.Ordinal);
        Assert.Contains("The instance did not answer", code, StringComparison.Ordinal);
    }

    private static DocumentEvidenceDto Evidence(string title, bool current, Guid? replacedById)
        => new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            DocumentAggregates.Invoice,
            Guid.CreateVersion7(),
            title,
            "application/pdf",
            "pdf",
            1_024,
            new string('a', 64),
            1,
            null,
            replacedById,
            current ? "Linked" : "Superseded",
            current,
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            null,
            DateTimeOffset.UtcNow.AddDays(30));
}
