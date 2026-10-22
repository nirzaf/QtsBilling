using Microsoft.JSInterop;
using QtsBilling.Contracts.Abstractions;

namespace QtsWebApp.Client.Services;

/// <summary>
/// Browser document output (specification task 24, recipe step 3): the invoice snapshot has already been fetched
/// by <see cref="QtsBilling.Contracts.Invoices.IInvoiceApiClient"/>, which carries this host's session, so all
/// that is left is to hand the committed page to the browser's own print dialogue.
/// </summary>
/// <remarks>
/// There is deliberately no navigation in this path. A link to <c>/api/v1/invoices/{id}/print</c> works only
/// while the browser's cookie is attached, and the moment the same page is hosted by a device that
/// authenticates with a bearer token, the only way to make that link work is to put the token in the URL - into
/// history, into the address bar, into server logs and into anything the operator shares. The contract cannot
/// express that (a print request holds content, never a location), and this adapter holds no URL either.
/// Nothing is written to disk: a saved PDF is the browser's own choice in the browser's own folder, so no
/// world-readable temporary file of a customer's money detail is ever left behind.
/// <c>document-output.js</c> is a host-owned asset loaded by <c>App.razor</c> (specification task 10).
/// </remarks>
public sealed class WebDocumentOutput(IJSRuntime js) : IDocumentOutput
{
    /// <summary>The browser is the printer's owner: every supported browser has a print dialogue.</summary>
    public bool CanPresentDocuments => true;

    public async Task<bool> PresentAsync(DocumentOutputRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await js.InvokeAsync<bool>(
                "qtsDocuments.print",
                cancellationToken,
                request.Title,
                request.Content);
        }
        catch (JSException)
        {
            // The page could not present it - a blocked dialogue, or a browser with no print facility.
            return false;
        }
        catch (InvalidOperationException)
        {
            // A static server render has no client to talk to. The screen keeps the document on display and
            // says so instead of pretending a print happened.
            return false;
        }
    }
}
