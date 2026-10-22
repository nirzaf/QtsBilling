using Microsoft.JSInterop;
using QtsBilling.Contracts.Abstractions;

namespace QtsBilling.Maui.Services;

/// <summary>
/// Device document output (specification task 24, recipe step 3, T24.02). The invoice snapshot reached the device
/// through <see cref="QtsBilling.Contracts.Invoices.IInvoiceApiClient"/>, whose <see cref="HttpClient"/> is
/// fitted with <see cref="SessionAuthHandler"/> - the same bearer path every other device call uses. This
/// adapter hands the returned page to the WebView's print facility, which on Windows opens the operating
/// system's own printer list, "Microsoft Print to PDF" included.
/// </summary>
/// <remarks>
/// What this path structurally cannot do:
/// <list type="bullet">
///   <item>navigate to a bearer-protected route, because a print request carries content and not a location - so
///   there is no URL for a token to ride in, and none is ever composed. The old shared screen offered a link to
///   <c>/api/v1/invoices/{id}/print</c>; on a device with no cookie that link could only be made to work by
///   appending the access token, which is the leak task 24 recipe step 3 forbids and the reason this adapter exists;</item>
///   <item>write a temporary file, so there is no copy of a customer's money detail left anywhere a second
///   account on the machine could read it. The bytes exist only in the page's own memory for as long as the
///   dialogue is open, and the frame holding them is removed afterwards;</item>
///   <item>reach a network service, because the content is already in hand and the script performs no fetch -
///   an offline device prints the documents it has, and nothing here asks the internet for a renderer.</item>
/// </list>
/// A refusal is returned, never thrown: an operator who cannot print still keeps the document on screen. The
/// physical proof on a real device (paper, driver list, PDF) belongs to the Windows acceptance sheet of task 38;
/// what is proven here is the contract, the build, and the absence of every leak path.
/// <c>document-output.js</c> is this host's own asset, declared in <c>wwwroot/index.html</c> (task 10).
/// </remarks>
public sealed class NativeDocumentOutput(IJSRuntime js) : IDocumentOutput
{
    /// <summary>The device hosts a WebView with the operating system's print dialogue behind it.</summary>
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
            // No print facility answered - the device states that instead of filing a blank page.
            return false;
        }
        catch (InvalidOperationException)
        {
            // The WebView is not attached (the app is backgrounded or the handler was replaced).
            return false;
        }
    }
}
