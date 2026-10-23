using Microsoft.JSInterop;
using QtsBilling.Contracts.Abstractions;

namespace QtsWebApp.Client.Services;

/// <summary>
/// Local file output for the browser (specification task 33, recipe step 3). The report bytes were fetched by the
/// authenticated client, so this adapter receives content and a bare file name and hands them to the browser's own
/// download: the operator chooses the folder, nothing is uploaded, and no URL carrying a bearer route is ever
/// composed. <c>file-output.js</c> is a host-owned asset loaded by <c>App.razor</c>.
/// </summary>
/// <remarks>
/// A refusal is returned rather than thrown. A static server render has no client to talk to, and a browser that
/// blocks the download leaves the statement on screen with the truth said about it - the screen never claims a file
/// exists when none was placed.
/// </remarks>
public sealed class WebFileOutput(IJSRuntime js) : ILocalFileOutput
{
    /// <summary>Every supported browser has a download facility; only the static prerender lacks one.</summary>
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
            // The bridge refused it - a blocked download, or a name the browser will not accept.
            return false;
        }
        catch (InvalidOperationException)
        {
            // A static server render has no client to talk to.
            return false;
        }
    }
}
