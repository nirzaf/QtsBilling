using Microsoft.JSInterop;
using QtsBilling.Contracts.Abstractions;

namespace QtsBilling.Maui.Services;

/// <summary>
/// Device file output (specification task 33, recipe step 3). The report bytes reached the device through
/// <see cref="QtsBilling.Contracts.Reports.IReportApiClient"/>, whose <see cref="HttpClient"/> carries the bearer
/// session like every other device call, so this adapter is handed content and a bare file name and never a URL:
/// the WebView performs the download itself and Windows puts the file where the user's downloads go.
/// </summary>
/// <remarks>
/// What this path structurally cannot do: open a bearer route in the address bar (the request holds content, not a
/// location, so there is no URL for a token to ride in), upload a customer's figures to a service, or require a
/// spreadsheet or BI product on the machine. A refusal is returned rather than thrown, because an export that did
/// not happen has to be said, not hidden behind a button that looked busy. <c>file-output.js</c> is this host's own
/// asset, declared in <c>wwwroot/index.html</c> (task 10). The physical proof that the file lands on a real device
/// belongs to the Windows acceptance sheet of task 38; what is owned here is the contract and the absence of every
/// leak path.
/// </remarks>
public sealed class NativeFileOutput(IJSRuntime js) : ILocalFileOutput
{
    /// <summary>The Windows WebView has a download facility, so a device can keep a report locally.</summary>
    public bool CanSaveFiles => true;

    public async Task<bool> SaveAsync(StoredFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        try
        {
            return await js.InvokeAsync<bool>(
                "qtsFiles.save",
                cancellationToken,
                file.FileName,
                Convert.ToBase64String(file.Content));
        }
        catch (JSException)
        {
            // The bridge refused the name or the content; the statement stays on screen.
            return false;
        }
        catch (InvalidOperationException)
        {
            // No client to talk to - the pre-render path, which cannot place a file.
            return false;
        }
    }
}
