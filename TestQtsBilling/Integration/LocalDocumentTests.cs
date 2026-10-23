using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Documents;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Documents;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Local document evidence (specification task 39, cases T39.01 - T39.04). Every claim here is checked against the
/// real server, a real PostgreSQL database and the real directory this instance writes its evidence into: the bytes
/// an owner attaches to an issued invoice come back identical through the authorised read and are refused to anyone
/// else, a file that lies about its type or could run is refused before a write is attempted, a crash between
/// staging and metadata commit leaves no referenced evidence behind and removes only what is provably nobody's, and
/// replacing a version keeps the version it replaced readable.
/// </summary>
public sealed class LocalDocumentTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public LocalDocumentTests(PostgresApiFixture fixture) => _fixture = fixture;

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nQTS local evidence of a delivery\n%%EOF\n");
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static readonly byte[] Html = Encoding.ASCII.GetBytes("<html><body><script>alert(1)</script></body></html>");
    private static readonly byte[] Svg = Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script/></svg>");

    [Fact]
    public async Task T39_01_only_the_authorised_tenant_reads_back_the_bytes_and_the_digest_it_attached()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var documents = new DocumentApiClient(http);
        var invoiceId = await CreateIssuedInvoiceAsync(http);
        var directory = EvidenceFiles();
        var filesBefore = Directory.GetFiles(directory);

        var attached = await documents.AttachAsync(new AttachDocumentEvidenceRequest(
            DocumentAggregates.Invoice, invoiceId, "supplier delivery note", "application/pdf", Pdf, null));

        Assert.Equal(1, attached.Version);
        Assert.True(attached.Current, "the first version of a document's evidence is the one held now");
        Assert.Equal("Linked", attached.Status);
        Assert.Equal("application/pdf", attached.ContentType);
        Assert.Equal("pdf", attached.FileExtension);
        Assert.Equal(Pdf.LongLength, attached.SizeBytes);
        Assert.Equal(Sha(Pdf), attached.Sha256);
        Assert.Equal(invoiceId, attached.AggregateId);
        Assert.Equal(_fixture.BusinessA.BusinessId, attached.BusinessId);
        Assert.NotNull(attached.RetainUntilUtc);
        Assert.True(attached.RetainUntilUtc > DateTimeOffset.UtcNow.AddDays(300), "evidence is undertaken to be kept, not held until convenient");

        var downloaded = await documents.DownloadAsync(attached.Id);
        Assert.Equal(Pdf, downloaded.Content);
        Assert.Equal(attached.Sha256, downloaded.Sha256);
        Assert.Equal("supplier-delivery-note-v1.pdf", downloaded.FileName);
        Assert.Equal(Path.GetFileName(downloaded.FileName), downloaded.FileName);

        // The bytes live on this machine, in a private directory, under a key that carries none of the operator's
        // text. The name a person gave the file is display data, never a place on the disk.
        var filesAfter = Directory.GetFiles(directory);
        Assert.Equal(filesBefore.Length + 1, filesAfter.Length);

        var key = Assert.Single(filesAfter.Except(filesBefore));
        Assert.Matches("^[0-9a-f]{32}$", Path.GetFileName(key));
        Assert.Equal(Pdf.LongLength, new FileInfo(key).Length);

        // Another business cannot list it, download it, or attach to the document it does not have.
        var other = new DocumentApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB));
        var crossRead = await Assert.ThrowsAsync<ApiException>(() => other.ListEvidenceAsync(DocumentAggregates.Invoice, invoiceId));
        Assert.True(crossRead.IsNotFound, $"another business's read of this invoice answered {crossRead.StatusCode}");

        var crossDownload = await Assert.ThrowsAsync<ApiException>(() => other.DownloadAsync(attached.Id));
        Assert.True(crossDownload.IsNotFound, "a foreign business was answered with something other than nothing");

        var crossAttach = await Assert.ThrowsAsync<ApiException>(() => other.AttachAsync(new AttachDocumentEvidenceRequest(
            DocumentAggregates.Invoice, invoiceId, "not mine", "application/pdf", Pdf, null)));
        Assert.True(crossAttach.IsNotFound, $"attaching to another business's invoice answered {crossAttach.StatusCode}");

        // A role that may not manage the aggregate is refused by the route, whatever the screen offers.
        var cashier = new DocumentApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Cashier));
        var refused = await Assert.ThrowsAsync<ApiException>(() => cashier.AttachAsync(new AttachDocumentEvidenceRequest(
            DocumentAggregates.Invoice, invoiceId, "at the counter", "application/pdf", Pdf, null)));
        Assert.True(refused.IsForbidden, $"a cashier's attach answered {refused.StatusCode} instead of a refusal");
        Assert.Contains(QtsBilling.Contracts.Identity.Permissions.InvoicesManage, refused.Message, StringComparison.Ordinal);

        // The same bytes on the same document are one piece of evidence, not a second copy of it.
        var again = await documents.AttachAsync(new AttachDocumentEvidenceRequest(
            DocumentAggregates.Invoice, invoiceId, "supplier delivery note again", "application/pdf", Pdf, null));
        Assert.Equal(attached.Id, again.Id);
        Assert.Single(await documents.ListEvidenceAsync(DocumentAggregates.Invoice, invoiceId));
        Assert.Equal(filesAfter.Length, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public async Task T39_02_a_file_that_lies_about_itself_is_refused_before_anything_is_written()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var documents = new DocumentApiClient(http);
        var invoiceId = await CreateIssuedInvoiceAsync(http);

        var attached = await documents.AttachAsync(new AttachDocumentEvidenceRequest(
            DocumentAggregates.Invoice, invoiceId, "the accepted scan", "application/pdf", Pdf, null));
        var directory = EvidenceFiles();
        var filesBefore = Directory.GetFiles(directory).Length;

        // A claimed type the leading bytes contradict is the oldest trick in the book, and it is refused by comparing
        // the two rather than believing either.
        var spoof = await Assert.ThrowsAsync<ApiException>(() => documents.AttachAsync(new AttachDocumentEvidenceRequest(
            DocumentAggregates.Invoice, invoiceId, "png wearing a pdf name", "application/pdf", Png, null)));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (HttpStatusCode)spoof.StatusCode);
        Assert.Contains("leading", spoof.Message, StringComparison.Ordinal);

        // Active content is refused outright: nothing that could run or render in a page belongs in a document's file.
        foreach (var (label, bytes) in new[] { ("html", Html), ("svg", Svg) })
        {
            var active = await Assert.ThrowsAsync<ApiException>(() => documents.AttachAsync(new AttachDocumentEvidenceRequest(
                DocumentAggregates.Invoice, invoiceId, $"a {label} document", null, bytes, null)));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (HttpStatusCode)active.StatusCode);
            Assert.Equal(ApiErrorCodes.ValidationFailed, active.Code);
            Assert.Contains("run or render", active.Message, StringComparison.Ordinal);
        }

        // Bytes that are none of the accepted kinds are refused by naming what is accepted - not by their name.
        var unknown = await Assert.ThrowsAsync<ApiException>(() => documents.AttachAsync(new AttachDocumentEvidenceRequest(
            DocumentAggregates.Invoice, invoiceId, "evidence.pdf", null, Encoding.ASCII.GetBytes("just some text"), null)));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (HttpStatusCode)unknown.StatusCode);
        Assert.Contains("application/pdf", unknown.Message, StringComparison.Ordinal);

        // Larger than this instance will hold.
        var oversized = new byte[DocumentEvidenceRules.MaxFileBytes + 1];
        Png.CopyTo(oversized, 0);

        var tooBig = await Assert.ThrowsAsync<ApiException>(() => documents.AttachAsync(new AttachDocumentEvidenceRequest(
            DocumentAggregates.Invoice, invoiceId, "a whole folder scan", "image/png", oversized, null)));
        Assert.Equal(HttpStatusCode.BadRequest, (HttpStatusCode)tooBig.StatusCode);
        Assert.Contains("larger", tooBig.Message, StringComparison.Ordinal);
        Assert.Contains(
            "at most",
            string.Join(' ', tooBig.Errors.TryGetValue("content", out var sizeReasons) ? sizeReasons : []),
            StringComparison.Ordinal);

        // No bytes at all is not evidence.
        var empty = await Assert.ThrowsAsync<ApiException>(() => documents.AttachAsync(new AttachDocumentEvidenceRequest(
            DocumentAggregates.Invoice, invoiceId, "nothing", "application/pdf", [], null)));
        Assert.Equal(HttpStatusCode.BadRequest, (HttpStatusCode)empty.StatusCode);

        // A title that is a path stays a title: the download name is composed bare, and the disk holds a random key.
        var smuggled = await documents.AttachAsync(new AttachDocumentEvidenceRequest(
            DocumentAggregates.Invoice, invoiceId, "../../webroot/evil.pdf", null, Jpeg, null));
        var kept = await documents.DownloadAsync(smuggled.Id);

        Assert.Equal(Path.GetFileName(kept.FileName), kept.FileName);
        Assert.DoesNotContain("..", kept.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("/", kept.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", kept.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.pdf", Path.GetFileName(kept.FileName), StringComparison.Ordinal);
        Assert.StartsWith("webroot-evil-pdf", Path.GetFileName(kept.FileName), StringComparison.Ordinal);

        var webRoot = Path.Combine(
            _fixture.Factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "wwwroot");
        Assert.Empty(Directory.GetFiles(webRoot, "*evil*", SearchOption.AllDirectories));
        Assert.All(Directory.GetFiles(directory), path => Assert.Matches("^[0-9a-f]{32}$", Path.GetFileName(path)));

        // Every refusal above wrote nothing, and none of them left a file behind: only the two accepted pieces of
        // evidence are on the disk, and the aggregate holds both of them as its own versions.
        Assert.Equal(filesBefore + 1, Directory.GetFiles(directory).Length);
        Assert.Equal(2, (await documents.ListEvidenceAsync(DocumentAggregates.Invoice, invoiceId)).Count);
        Assert.Equal(2, (await documents.ListEvidenceAsync(DocumentAggregates.Invoice, invoiceId, includeHistory: false)).Count);
    }

    [Fact]
    public async Task T39_03_a_crash_between_staging_and_the_record_loses_no_referenced_evidence()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var documents = new DocumentApiClient(http);
        var operations = new QtsBilling.Contracts.Diagnostics.OperationsApiClient(http);
        var invoiceId = await CreateIssuedInvoiceAsync(http);

        var attached = await documents.AttachAsync(new AttachDocumentEvidenceRequest(
            DocumentAggregates.Invoice, invoiceId, "proof this cleanup must not touch", "image/jpeg", Jpeg, null));

        var directory = EvidenceFiles();

        // What a crash leaves behind: a file the instance wrote and never got to record. One is old enough to be
        // judged, one is still inside the grace in which a slow request may still be about to record it.
        var abandoned = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(abandoned, "bytes no record ever claimed");
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-2));

        var stillStaging = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(stillStaging, "a request that has not finished yet");

        var cleanup = await documents.CleanOrphanedEvidenceAsync();

        Assert.Contains(Path.GetFileName(abandoned), cleanup.RemovedKeys);
        Assert.False(File.Exists(abandoned), "an unlinked file older than the grace survived the cleanup");
        Assert.True(File.Exists(stillStaging), "a file inside the grace was judged without being given the chance");
        Assert.Equal(0, cleanup.RemovedKeys.Count(key => key == Path.GetFileName(stillStaging)));
        Assert.True(cleanup.TooYoungToJudge >= 1, "the young file was not counted as too young to judge");
        Assert.True(cleanup.ReferencedKept >= 1, "the cleanup examined a directory it found nothing referenced in");
        Assert.Equal(cleanup.RemovedKeys.Count, cleanup.Examined - cleanup.ReferencedKept - cleanup.TooYoungToJudge);

        // The evidence that a record does point at is untouched, and still reads back byte for byte.
        var reread = await documents.DownloadAsync(attached.Id);
        Assert.Equal(Jpeg, reread.Content);
        Assert.Equal(attached.Sha256, reread.Sha256);

        // The pass is itself a fact: what was examined and what was removed, with a correlation.
        var entries = await operations.ListAuditEventsAsync(entityType: "document_evidence", action: "orphans_removed");
        var entry = Assert.Single(entries.Items);
        Assert.False(string.IsNullOrWhiteSpace(entry.CorrelationId));
        Assert.Contains("removed", entry.Summary ?? string.Empty, StringComparison.Ordinal);

        // And a second pass has nothing left to remove.
        var again = await documents.CleanOrphanedEvidenceAsync();
        Assert.Empty(again.RemovedKeys);
    }

    [Fact]
    public async Task T39_04_replacing_evidence_adds_a_version_and_keeps_the_one_it_replaced()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var documents = new DocumentApiClient(http);
        var operations = new QtsBilling.Contracts.Diagnostics.OperationsApiClient(http);
        var invoiceId = await CreateIssuedInvoiceAsync(http, "evidence history invoice");

        var first = await documents.AttachAsync(new AttachDocumentEvidenceRequest(
            DocumentAggregates.Invoice, invoiceId, "the scan the supplier gave us", "application/pdf", Pdf, null));

        var second = await documents.ReplaceAsync(first.Id, new ReplaceDocumentEvidenceRequest(
            "a clearer scan", "image/jpeg", Jpeg, "the supplier sent a better copy of the same note"));

        Assert.Equal(2, second.Version);
        Assert.True(second.Current);
        Assert.Equal(Sha(Jpeg), second.Sha256);

        var history = await documents.ListEvidenceAsync(DocumentAggregates.Invoice, invoiceId);
        Assert.Equal(2, history.Count);

        var superseded = history.Single(row => row.Id == first.Id);
        Assert.False(superseded.Current);
        Assert.Equal("Superseded", superseded.Status);
        Assert.Equal(second.Id, superseded.ReplacedById);
        Assert.NotNull(superseded.RetainUntilUtc);

        // The document was issued while the first version was the evidence for it, so that version stays readable -
        // and a replacement never deletes anything to make room.
        var keptOld = await documents.DownloadAsync(first.Id);
        Assert.Equal(Pdf, keptOld.Content);
        Assert.Equal(first.Sha256, keptOld.Sha256);
        Assert.Equal("the-scan-the-supplier-gave-us-v1.pdf", keptOld.FileName);

        var keptNew = await documents.DownloadAsync(second.Id);
        Assert.Equal(Jpeg, keptNew.Content);
        Assert.Equal("a-clearer-scan-v2.jpg", keptNew.FileName);

        // Only the current version may be replaced, and saying so is not optional: a second attempt on the version
        // that already gave way is a conflict, not a second copy.
        var twice = await Assert.ThrowsAsync<ApiException>(() => documents.ReplaceAsync(
            first.Id, new ReplaceDocumentEvidenceRequest("again", "image/jpeg", Jpeg, null)));
        Assert.True(twice.IsConflict, $"replacing a superseded version answered {twice.StatusCode}");

        // Both acts are in the feed with their own correlation, and the replacement names what it gave way to.
        var replaced = await operations.ListAuditEventsAsync(entityType: "document_evidence", action: "replaced");
        var entry = Assert.Single(replaced.Items, row => row.EntityId == first.Id);
        Assert.False(string.IsNullOrWhiteSpace(entry.CorrelationId));
        Assert.Contains(second.Sha256, entry.Summary ?? string.Empty, StringComparison.Ordinal);

        var attached = await operations.ListAuditEventsAsync(entityType: "document_evidence", action: "attached");
        Assert.True(attached.Items.Count >= 2, $"the two attaches wrote {attached.Items.Count} facts");

        // The inventory a backup carries names both versions and their digests.
        var inventory = await documents.GetInventoryAsync();
        Assert.True(inventory.Versions >= 2);
        Assert.Contains(inventory.Items, row => row.Sha256 == first.Sha256);
        Assert.Contains(inventory.Items, row => row.Sha256 == second.Sha256);
        Assert.True(inventory.TotalBytes >= first.SizeBytes + second.SizeBytes);
        Assert.All(inventory.Items, row => Assert.Equal(_fixture.BusinessA.BusinessId, row.BusinessId));
    }

    // ------------------------------------------------------------------ helpers

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private string EvidenceFiles()
    {
        var services = _fixture.Factory.Services;
        var environment = services.GetRequiredService<IWebHostEnvironment>();
        var configuration = services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();

        Assert.True(DocumentEndpoints.TryPrivateDirectory(environment, configuration, out var directory, out var sentence), sentence);
        Directory.CreateDirectory(directory);

        // The whole point of the directory is that no URL reaches it.
        Assert.False(directory.StartsWith(
            Path.GetFullPath(Path.Combine(environment.ContentRootPath, "wwwroot")), StringComparison.OrdinalIgnoreCase),
            $"evidence is being written inside webroot: {directory}");

        return directory;
    }

    private async Task<Guid> CreateIssuedInvoiceAsync(HttpClient http, string note = "evidence invoice")
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var customerId = await CreateCustomerAsync(http, note);
        var request = new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            today,
            today.AddDays(30),
            note,
            [new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 1m, 25m, 0m, 0m)],
            InvoiceRecognitionMode.DirectGoods);

        using var create = await http.PostAsJsonAsync("/api/v1/invoices", request).ConfigureAwait(false);
        var createdBody = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"invoice create failed: {(int)create.StatusCode} {createdBody}");

        Guid invoiceId;
        using (var document = JsonDocument.Parse(createdBody))
        {
            invoiceId = document.RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
        }

        using var issue = await http.PostAsync($"/api/v1/invoices/{invoiceId:D}/issue", content: null).ConfigureAwait(false);
        var issueBody = await issue.Content.ReadAsStringAsync();
        Assert.True(issue.IsSuccessStatusCode, $"invoice issue failed: {(int)issue.StatusCode} {issueBody}");

        return invoiceId;
    }

    /// <summary>A customer of the signed-in business, because an invoice tracks its receivable per customer.</summary>
    private static async Task<Guid> CreateCustomerAsync(HttpClient http, string label)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var created = await http.PostAsJsonAsync("/api/v1/customers", new
        {
            name = $"{label} {suffix}",
            email = $"evidence-{suffix}@fixture.test",
            phone = $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
        }).ConfigureAwait(false);

        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.IsSuccessStatusCode, $"customer create failed: {(int)created.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetGuid();
    }
}
