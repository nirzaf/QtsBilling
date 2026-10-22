using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Invoices;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Contracts;

/// <summary>
/// The authenticated-printing contract of specification task 24 (T24.02, T24.04). A document must reach a printer
/// with the session that already holds it, and a document that cannot be authorised must produce nothing at all -
/// not a page, not a file, not a URL a token rode in. These checks exercise the real host, the real typed client
/// and the real print route, because every one of the failures this task retires lives in the seam between them.
/// </summary>
public sealed class DocumentOutputTests : IClassFixture<PostgresApiFixture>
{
    private static readonly string[] HostStartups =
    [
        @"QtsWebApp/QtsWebApp/Program.cs",
        @"QtsWebApp/QtsWebApp.Client/Program.cs",
        @"QtsBilling.Maui/MauiProgram.cs",
    ];

    private readonly PostgresApiFixture _fixture;

    public DocumentOutputTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public void T24_04_a_print_request_carries_content_and_can_never_carry_a_location()
    {
        // The shape of the contract is the first line of defence: nothing here can express "fetch this and print
        // it", which is the only way a token ends up in a URL, a history entry or a shared link.
        foreach (var location in new[]
        {
            "https://other-host.example/invoice?token=abc",
            "http://127.0.0.1:5000/api/v1/invoices/00000000-0000-0000-0000-000000000000/print",
            "//example.test/document",
            "file:///C:/Users/operator/Desktop/invoice.html",
        })
        {
            var refused = Assert.Throws<ArgumentException>(() => new DocumentOutputRequest(location, "Invoice X"));
            Assert.Contains("location", refused.Message, StringComparison.OrdinalIgnoreCase);
        }

        // An error page, an empty body or a JSON problem is not a document and never reaches a print facility.
        Assert.Throws<ArgumentException>(() => new DocumentOutputRequest("{\"title\":\"Invoice\"}", "Invoice X"));
        Assert.Throws<ArgumentException>(() => new DocumentOutputRequest("   ", "Invoice X"));
        Assert.Throws<ArgumentException>(() => new DocumentOutputRequest("<!DOCTYPE html><div>not a page</div>", "Invoice X"));
        Assert.Throws<ArgumentException>(() => new DocumentOutputRequest(new string('x', DocumentOutputRequest.MaxContentLength + 1), "Invoice X"));

        // A document name must be safe wherever a host decides to use it, and it is never allowed to smuggle a path.
        // The rule is fixed rather than read from the host's file-name rules, because a request composed on a Linux
        // server is printed on the device the operator stands at: the same name must be refused on every OS.
        foreach (var unsafeTitle in new[]
        {
            @"C:\shared\invoice",
            "../elsewhere",
            "..",
            "Invoice: final",
            "INV|1",
            "<script>INV</script>",
            "INV\"1\"",
            "INV*1",
            "INV?1",
            "Invoice\tINV-1",
            "share\\invoice",
            new string('n', 200),
        })
        {
            Assert.Throws<ArgumentException>(() => new DocumentOutputRequest("<html><body>x</body></html>", unsafeTitle));
        }

        // An ordinary name an operator would recognise still passes, so the rule protects the printer without
        // refusing the document.
        foreach (var safeTitle in new[] { "Invoice INV-1", "Receipt GRN-2026-0001 (copy)", "Purchase order PO-0007" })
        {
            Assert.Equal(safeTitle, new DocumentOutputRequest("<html><body>x</body></html>", safeTitle).Title);
        }

        var document = new DocumentOutputRequest("<!DOCTYPE html><html><body><p>INV-1</p></body></html>", "Invoice INV-1");
        Assert.Equal("Invoice INV-1", document.Title);
        Assert.Contains("INV-1", document.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T24_02_the_document_reaches_the_printer_through_the_authenticated_client_and_no_url_at_all()
    {
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoices = new InvoiceApiClient(owner);
        var invoiceId = await CreateAndIssueAsync(invoices, "printed snapshot");

        var document = await invoices.GetPrintDocumentAsync(invoiceId, "Invoice of printed snapshot");

        // The server's own page: self-contained, carrying the stored figures and no reference to a public place.
        Assert.StartsWith("<!DOCTYPE html", document.Content, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\">", document.Content, StringComparison.Ordinal);
        Assert.Contains("ISSUED", document.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", document.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("token", document.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", document.Content, StringComparison.Ordinal);

        // Every href the page carries is an in-document anchor, never a location that would have to be fetched.
        var schemes = System.Text.RegularExpressions.Regex.Matches(document.Content, "href=\"([^\":]*)://");
        Assert.True(schemes.Count == 0, $"the printable page reaches out to {schemes.Count} external location(s)");

        // The name the operator was given is the name the host prints under, and the request holds no URL.
        Assert.Equal("Invoice of printed snapshot", document.Title);
        Assert.False(document.Content.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase));

        // Every host that renders the shared invoice screens must resolve the print contract, or its operator
        // gets a button that throws (finding F25). The device host is proven by its bearer client: the snapshot
        // arrives over the same authenticated HTTP path as every other device call.
        var root = TestQtsBilling.Ui.InvoiceEditorTests.RepositoryRoot();
        foreach (var startup in HostStartups)
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, startup));
            Assert.Contains(nameof(IDocumentOutput), source, StringComparison.Ordinal);
        }

        var maui = await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Maui/MauiProgram.cs"));
        var bearerGate = maui.IndexOf($"AddHttpClient<IInvoiceApiClient, InvoiceApiClient>", StringComparison.Ordinal);
        Assert.True(bearerGate >= 0, "the device host does not build its invoice client at all");
        Assert.True(
            maui.IndexOf("AddHttpMessageHandler<SessionAuthHandler>", bearerGate, StringComparison.Ordinal) > bearerGate,
            "the device's invoice client is not the bearer-authenticated one, so the printed snapshot would not be authorised");

        using var scope = _fixture.Factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService(typeof(IDocumentOutput)));
    }

    [Fact]
    public async Task T24_04_a_foreign_or_expired_document_yields_nothing_to_print_or_display()
    {
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoices = new InvoiceApiClient(owner);
        var invoiceId = await CreateAndIssueAsync(invoices, "belonging to business A");

        // 1. A document this business does not have: refused, and no request object exists to hand anywhere.
        var stranger = Guid.CreateVersion7();
        var missing = await Assert.ThrowsAsync<ApiException>(() => invoices.GetPrintDocumentAsync(stranger, "Invoice nobody has"));
        Assert.True(missing.IsNotFound);
        Assert.Equal(ApiErrorCodes.NotFound, missing.Code);

        // 2. A document of another business the same user may not act in: existence is not disclosed.
        var ownerB = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB);
        var foreign = await Assert.ThrowsAsync<ApiException>(
            () => new InvoiceApiClient(ownerB).GetPrintDocumentAsync(invoiceId, "Invoice of business A"));
        Assert.True(foreign.IsNotFound, $"a foreign document must read as missing, not as printable: {foreign.Code}");

        // 3. An expired or absent session: the route answers 401 before a byte of the document, and a token
        //    offered in the query string is not a way around it - which is exactly why no host may compose one.
        var anonymous = _fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var unauthenticated = await anonymous.GetAsync($"/api/v1/invoices/{invoiceId:D}/print");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var withQueryToken = await anonymous.GetAsync($"/api/v1/invoices/{invoiceId:D}/print?token=not-a-session");
        Assert.Equal(HttpStatusCode.Unauthorized, withQueryToken.StatusCode);
        var body = await withQueryToken.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html lang=", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ISSUED", body, StringComparison.Ordinal);

        // 4. A role that may read the ledger but not this document's branch: refused, with nothing to print.
        var viewer = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Viewer);
        var forbidden = await Assert.ThrowsAsync<ApiException>(
            () => new InvoiceApiClient(viewer).GetPrintDocumentAsync(invoiceId, "Invoice a viewer may not print"));
        Assert.True(forbidden.IsForbidden || forbidden.IsNotFound, $"unexpected {(int)forbidden.StatusCode} {forbidden.Code}");
    }

    [Fact]
    public async Task T24_02_no_host_writes_a_file_navigates_or_reaches_the_network_to_print()
    {
        var root = TestQtsBilling.Ui.InvoiceEditorTests.RepositoryRoot();

        var adapters = new[]
        {
            @"QtsWebApp/QtsWebApp.Client/Services/WebDocumentOutput.cs",
            @"QtsBilling.Maui/Services/NativeDocumentOutput.cs",
            @"QtsWebApp/QtsWebApp/wwwroot/document-output.js",
            @"QtsBilling.Maui/wwwroot/document-output.js",
        };

        foreach (var relative in adapters)
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, relative));

            // No navigation, no fetch, no file, no token: the four ways a print path becomes a leak.
            Assert.DoesNotContain("location.href", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("window.open", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fetch(", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("XMLHttpRequest", source, StringComparison.Ordinal);
            Assert.DoesNotContain("access_token", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer", source, StringComparison.Ordinal);
            Assert.DoesNotContain("File.Write", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WriteAllText", source, StringComparison.Ordinal);
            Assert.DoesNotContain("http://", source, StringComparison.Ordinal);
            Assert.DoesNotContain("https://", source, StringComparison.Ordinal);
        }

        // Each host declares its own local facility, and the shared library asks for no print dependency.
        var web = await File.ReadAllTextAsync(Path.Combine(root, @"QtsWebApp/QtsWebApp.Client/Services/WebDocumentOutput.cs"));
        var native = await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Maui/Services/NativeDocumentOutput.cs"));
        Assert.Contains("qtsDocuments.print", web, StringComparison.Ordinal);
        Assert.Contains("qtsDocuments.print", native, StringComparison.Ordinal);
        Assert.Contains("DocumentOutputRequest", native, StringComparison.Ordinal);

        Assert.False(typeof(QtsBilling.Ui.Pages.Invoices.InvoiceActions).Assembly
            .GetReferencedAssemblies()
            .Any(name => name.Name!.Contains("Pdf", StringComparison.OrdinalIgnoreCase)
                || name.Name!.Contains("Reporting", StringComparison.OrdinalIgnoreCase)
                || name.Name!.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)),
            "the shared UI reached for a renderer or a database to print a document");

        // The host pages load their own copy of the facility, and nothing else (specification task 10).
        var app = await File.ReadAllTextAsync(Path.Combine(root, @"QtsWebApp/QtsWebApp/Components/App.razor"));
        var index = await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Maui/wwwroot/index.html"));
        Assert.Contains("document-output.js", app, StringComparison.Ordinal);
        Assert.Contains("document-output.js", index, StringComparison.Ordinal);
    }

    [Fact]
    public void T24_01_the_printed_page_is_read_at_the_business_scale_and_never_at_an_assumed_two()
    {
        // The renderer is proven against a document that carries a settlement scale of two and one of four, so the
        // figures an operator puts on paper are the figures their own business settles in (finding F21) - while a
        // business that never chose a scale is shown stored precision rather than an invented one.
        var settled = Document(twoDecimals: true);
        var htmlTwo = QtsWebApp.Features.Invoices.InvoicePrintRenderer.RenderHtml(settled);
        Assert.Contains(">115.00</td>", htmlTwo, StringComparison.Ordinal);
        Assert.Contains("settlement scale (2 decimal places)", htmlTwo, StringComparison.Ordinal);
        Assert.DoesNotContain("115.0000", htmlTwo, StringComparison.Ordinal);

        var fine = Document(twoDecimals: false);
        var htmlFour = QtsWebApp.Features.Invoices.InvoicePrintRenderer.RenderHtml(fine);
        Assert.Contains(">115.0000</td>", htmlFour, StringComparison.Ordinal);
        Assert.Contains("settlement scale (4 decimal places)", htmlFour, StringComparison.Ordinal);

        var unknown = Document(twoDecimals: true) with { Presentation = null };
        var htmlUnknown = QtsWebApp.Features.Invoices.InvoicePrintRenderer.RenderHtml(unknown);
        Assert.Contains(">115.0000</td>", htmlUnknown, StringComparison.Ordinal);
        Assert.Contains("no settlement scale configured yet", htmlUnknown, StringComparison.Ordinal);

        // A line rate is never rounded to the settlement scale: a rate that moves on paper is a different rate.
        Assert.Contains(">100.0000</td>", htmlTwo, StringComparison.Ordinal);

        // An uncleared tender is named on paper as what it is, beside the money that actually cleared.
        Assert.Contains("Recorded, awaiting clearance", htmlTwo, StringComparison.Ordinal);
        Assert.Contains("Pending", htmlTwo, StringComparison.Ordinal);
    }

    private static InvoiceDetailDto Document(bool twoDecimals) => new(
        new InvoiceSummaryDto(
            Guid.CreateVersion7(),
            "INV-F21",
            InvoiceState.PartiallyPaid,
            null,
            "Acme Ltd",
            new DateOnly(2026, 3, 5),
            new DateOnly(2026, 4, 4),
            "USD",
            SubtotalAmount: 100m,
            DiscountAmount: 0m,
            TaxAmount: 15m,
            TotalAmount: 115m,
            PaidAmount: 90m,
            CreditedAmount: 0m,
            BalanceDue: 25m,
            Note: null,
            IssuedAtUtc: new DateTimeOffset(2026, 3, 5, 2, 30, 0, TimeSpan.Zero),
            VoidedAtUtc: null,
            Version: 2,
            RecognitionMode: InvoiceRecognitionMode.DirectGoods),
        [new InvoiceLineDto(Guid.CreateVersion7(), 1, null, "Consulting", "SKU-1", 1m, 100m, 0m, 0.15m, 15m, 115m, "Exclusive", 100m)],
        [new InvoicePaymentDto(Guid.CreateVersion7(), "Cheque", "Pending", 25m, "USD", "CHQ-1", new DateTimeOffset(2026, 3, 6, 3, 0, 0, TimeSpan.Zero))],
        CreditNote: null,
        CreditNotes: null,
        LocationId: Guid.CreateVersion7(),
        PartySnapshot: new InvoicePartySnapshotDto("Fixture Business", null, null, "Acme Ltd", null, null, null, 30),
        Presentation: new InvoicePresentationDto(twoDecimals ? 2 : 4, "Pacific/Auckland", new DateOnly(2026, 3, 6)),
        Source: new InvoiceSourceDto("sale", Guid.CreateVersion7(), "S-1001"),
        AwaitingClearanceAmount: 25m);

    private async Task<Guid> CreateAndIssueAsync(InvoiceApiClient invoices, string note)
    {
        // The same document shape the recognition tests prove: a direct-goods draft on the fixture branch, for a
        // customer of this business, issued through the ordinary route - so the printable snapshot is a real
        // committed fact rather than a fixture built for the screen.
        var suffix = Guid.NewGuid().ToString("N")[..10];
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var customer = await owner.PostAsJsonAsync("/api/v1/customers", new
        {
            name = $"Printed document customer {suffix}",
            email = $"print-{suffix}@fixture.test",
            phone = $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
        });
        var customerBody = await customer.Content.ReadAsStringAsync();
        Assert.True(customer.IsSuccessStatusCode, $"customer create failed with {(int)customer.StatusCode}: {customerBody}");
        var customerId = JsonDocument.Parse(customerBody).RootElement.GetProperty("id").GetGuid();

        var created = await invoices.CreateAsync(new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            note,
            [new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 1m, 40m, 0m, 0.15m)],
            InvoiceRecognitionMode.DirectGoods));

        var issued = await invoices.IssueAsync(created.Invoice.Id);
        Assert.NotEqual(InvoiceState.Draft, issued.Invoice.Status);

        return issued.Invoice.Id;
    }
}
