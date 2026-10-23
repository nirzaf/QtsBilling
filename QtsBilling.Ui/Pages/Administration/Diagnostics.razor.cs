using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Diagnostics;
using QtsBilling.Contracts.Identity;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Administration;

/// <summary>
/// The instance's own operational picture (specification task 34, recipe steps 2 and 3): what answered, how much room
/// is left, when a backup was last proved, which terminals are quiet or refused, and when the signing material expires -
/// all of it read from evidence this instance holds, with an act attached to every warning. The support bundle is
/// written locally, on request, with a reason; nothing here uploads anything or clears a fact to silence a warning.
/// </summary>
public partial class Diagnostics : IAsyncDisposable
{
    [Inject] private IOperationsApiClient Operations { get; set; } = default!;
    [Inject] private IAccountApiClient AccountsApi { get; set; } = default!;
    [Inject] private ILocalFileOutput Files { get; set; } = default!;

    private readonly HeldIntention<string> _bundle = new("the support bundle");

    private DiagnosticsDto? _diagnostics;
    private SupportBundleDto? _lastBundle;
    private string? _reason;
    private string? _error;
    private string? _notice;
    private string? _message;
    private ShellStateKind _state = ShellStateKind.Loading;
    private bool _busy;
    private bool _started;
    private int _loadVersion;
    private CancellationTokenSource? _pending;

    private bool CanRead => !_busy;

    private bool CanBundle => _diagnostics is not null && DiagnosticsRules.CanRequestBundle(_reason, _busy) && !_bundle.Outstanding;

    private bool CanSaveBundle => _lastBundle is not null && Files.CanSaveFiles;

    protected override void OnInitialized() => _state = ShellStateKind.Loading;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await LoadAsync().ConfigureAwait(false);
        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        return default;
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        _busy = true;
        _error = null;
        _state = ShellStateKind.Loading;
        _message = null;

        try
        {
            var session = await AccountsApi.GetSessionAsync(_pending.Token).ConfigureAwait(false);
            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                _message = "Sign in to read the instance's diagnostics.";
                return;
            }

            if (!session.Permissions.Contains(Permissions.AuditView, StringComparer.Ordinal))
            {
                _state = ShellStateKind.Forbidden;
                _message = $"Reading diagnostics needs the '{Permissions.AuditView}' authority, which this account does not hold here.";
                return;
            }

            _diagnostics = await Operations.GetDiagnosticsAsync(_pending.Token).ConfigureAwait(false);
            _state = ShellStateKind.Content;
            _message = $"Read at {_diagnostics.Store.ServerTimeUtc.ToUniversalTime():yyyy-MM-dd HH:mm} UTC. "
                + $"{_diagnostics.Alerts.Count} warning(s) need an answer; {_diagnostics.AuditEventsLast24Hours} audit entries were written in the last 24 hours.";
        }
        catch (ApiException exception)
        {
            _state = exception.StatusCode is 401 or 403 ? ShellStateKind.Forbidden : ShellStateKind.Failure;
            _message = DiagnosticsRules.RefusalSentence(exception, "the diagnostics");
            _error = _message;
        }
        catch (OperationCanceledException)
        {
            // A superseded read is not a failure.
        }
        catch (HttpRequestException)
        {
            _state = ShellStateKind.Offline;
            _message = "The business server could not be reached, so no diagnostic was read and none was guessed.";
        }
        finally
        {
            if (version == _loadVersion)
            {
                _busy = false;
            }
        }
    }

    /// <summary>
    /// Asks this instance to write a bundle for the reason given. A lost answer keeps the intention held, so the same
    /// request is not repeated into a stack of bundles nobody asked for; a refusal that proves nothing was written lets
    /// the operator ask again.
    /// </summary>
    private async Task RequestBundleAsync()
    {
        var reason = _reason?.Trim() ?? string.Empty;
        if (!DiagnosticsRules.CanRequestBundle(reason, _busy))
        {
            _error = "Say why the bundle is being taken out - five characters at least, and it is written into the file.";
            return;
        }

        _bundle.Body = reason;
        _busy = true;
        _error = null;
        _notice = null;

        try
        {
            var bundle = await Operations.CreateSupportBundleAsync(reason).ConfigureAwait(false);
            _lastBundle = bundle;
            _notice = $"{bundle.FileName} ({bundle.Bytes} bytes) was written on the server's own disk for the reason given. "
                + $"It carries {string.Join(", ", bundle.IncludedSections)} and refuses {string.Join(", ", bundle.ExcludedCategories)}.";
            _bundle.Release();
            _reason = null;
        }
        catch (ApiException exception)
        {
            _error = DiagnosticsRules.RefusalSentence(exception, "the support bundle");
            if (DiagnosticsRules.CommittedNothing(exception.Code))
            {
                _bundle.Release();
            }
        }
        catch (HttpRequestException)
        {
            _error = "The server could not be reached, so no bundle was written and none is claimed.";
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Carries an already-generated bundle to this machine through the same local file contract the reports use. A
    /// device that cannot place a file says so; the bundle stays where the server wrote it either way.
    /// </summary>
    private async Task CarryBundleAsync()
    {
        if (_lastBundle is not { } saved)
        {
            return;
        }

        var text = $"{{\"bundle\":\"{saved.FileName}\",\"generatedAtUtc\":\"{saved.GeneratedAtUtc:O}\",\"reason\":\"{saved.Reason.Replace("\"", "'")}\"}}";
        if (await Files.SaveAsync(new StoredFile(saved.FileName.EndsWith(".json", StringComparison.Ordinal) ? saved.FileName : $"{saved.FileName}.json",
                System.Text.Encoding.UTF8.GetBytes(text))).ConfigureAwait(false))
        {
            _notice = $"{saved.FileName} was handed to this machine as well. Nothing was uploaded anywhere.";
        }
        else
        {
            _error = "This device would not take the file. The bundle stays on the server's own disk, and no copy was claimed.";
        }

        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    private IEnumerable<DiagnosticsAlertDto> Critical =>
        _diagnostics?.Alerts.Where(alert => DiagnosticsRules.IsCritical(alert.Severity)) ?? [];

    private IEnumerable<DiagnosticsAlertDto> Warnings =>
        _diagnostics?.Alerts.Where(alert => !DiagnosticsRules.IsCritical(alert.Severity)) ?? [];
}
