using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Identity;
using QtsBilling.Ui.Layout;

namespace QtsBilling.Ui.Pages.Catalog;

/// <summary>
/// The four catalog reference lists (specification task 16, recipe steps 1 and 4). Every rule that matters - unique
/// name, an acyclic category hierarchy, a unit's decimal behaviour, deactivation instead of deletion - belongs to the
/// server; this screen asks for the same values the server checks and shows its refusal instead of a green tick.
/// </summary>
public partial class MasterData
{
    [Inject] private IMasterDataApiClient MasterDataApi { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private static readonly MasterDataResource[] Kinds =
    [
        MasterDataResource.Units,
        MasterDataResource.Categories,
        MasterDataResource.Brands,
        MasterDataResource.ProductGroups,
    ];

    private readonly Dictionary<MasterDataResource, IReadOnlyList<MasterDataDto>> _lists = new();

    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _problem;
    private bool _canRead;
    private bool _canWrite;
    private bool _busy;
    private bool _started;
    private int _tabIndex;

    private bool _creating;
    private MasterDataDto? _editing;
    private string _name = string.Empty;
    private string? _shortName;
    private bool _allowDecimals = true;
    private Guid? _parentId;

    private MasterDataResource Tab => Kinds[Math.Clamp(_tabIndex, 0, Kinds.Length - 1)];

    private string TabName => TabNameOf(Tab);

    private bool CanSave => _name.Trim().Length > 0
        && (Tab != MasterDataResource.Units || !string.IsNullOrWhiteSpace(_shortName));

    private IEnumerable<MasterDataDto> ParentCandidates =>
        _lists.TryGetValue(MasterDataResource.Categories, out var categories)
            ? [.. categories.Where(candidate => candidate.IsActive != false && candidate.Id != _editing?.Id)]
            : [];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task LoadAsync()
    {
        _busy = true;
        _state = ShellStateKind.Loading;
        _message = null;
        StateHasChanged();

        try
        {
            var session = await Accounts.GetSessionAsync().ConfigureAwait(false);

            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                return;
            }

            _canRead = session.Permissions.Contains(Permissions.ProductsRead, StringComparer.Ordinal);
            _canWrite = session.Permissions.Contains(Permissions.ProductsWrite, StringComparer.Ordinal);

            if (!_canRead)
            {
                _state = ShellStateKind.Forbidden;
                _message = $"Seeing the catalog needs the '{Permissions.ProductsRead}' authority.";
                return;
            }

            foreach (var kind in Kinds)
            {
                // These are the closed sets a product chooses from; the server returns each complete, so a
                // reference that exists is a reference the screen can offer.
                _lists[kind] = await MasterDataApi.ListAsync(kind, includeInactive: true).ConfigureAwait(false);
            }

            _state = ShellStateKind.Content;
        }
        catch (ApiException exception)
        {
            _state = exception.StatusCode switch
            {
                401 => ShellStateKind.Unauthenticated,
                403 => ShellStateKind.Forbidden,
                409 => ShellStateKind.Conflict,
                _ => ShellStateKind.Failure,
            };
            _message = Describe(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private static int IndexOf(MasterDataResource resource) => Array.IndexOf(Kinds, resource);

    private void SelectTab(int index) => _tabIndex = Math.Clamp(index, 0, Kinds.Length - 1);

    private static string TabNameOf(MasterDataResource resource) => resource switch
    {
        MasterDataResource.Units => "Units",
        MasterDataResource.Categories => "Categories",
        MasterDataResource.Brands => "Brands",
        _ => "Product groups",
    };

    private IReadOnlyList<MasterDataDto> RowsOf(MasterDataResource resource)
        => _lists.TryGetValue(resource, out var rows) ? rows : [];

    private string ParentName(MasterDataDto row)
    {
        if (row.ParentId is not { } parentId)
        {
            return "Top level";
        }

        var parent = RowsOf(MasterDataResource.Categories).FirstOrDefault(candidate => candidate.Id == parentId);

        return parent?.Name ?? "a category that is no longer listed";
    }

    // ------------------------------------------------------------------ form

    private void BeginCreate()
    {
        _problem = null;
        _creating = true;
        _editing = null;
        _name = string.Empty;
        _shortName = null;
        _allowDecimals = true;
        _parentId = null;
    }

    private void BeginEdit(MasterDataDto row)
    {
        _problem = null;
        _creating = false;
        _editing = row;
        _name = row.Name;
        _shortName = row.ShortName;
        _allowDecimals = row.AllowDecimals ?? true;
        _parentId = row.ParentId;
    }

    private void Cancel()
    {
        _creating = false;
        _editing = null;
    }

    private async Task SaveAsync()
    {
        if (!CanSave || _busy)
        {
            return;
        }

        _busy = true;
        _problem = null;

        var request = new SaveMasterDataRequest(
            _name,
            Tab == MasterDataResource.Units ? _shortName : null,
            Tab == MasterDataResource.Units ? _allowDecimals : null,
            Tab == MasterDataResource.Categories ? _parentId : null);

        try
        {
            if (_creating)
            {
                await MasterDataApi.CreateAsync(Tab, request).ConfigureAwait(false);
                Snackbar.Add($"{TabName} entry added.", Severity.Success);
            }
            else if (_editing is { } row)
            {
                await MasterDataApi.UpdateAsync(Tab, row.Id, request).ConfigureAwait(false);
                Snackbar.Add($"{TabName} entry saved.", Severity.Success);
            }

            Cancel();
            await LoadAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            // The typed values stay in the form: a refusal about a name or a hierarchy is the operator's next
            // attempt, not something to re-type from memory (task 16, T16.02).
            _problem = Describe(exception);

            if (exception.IsConflict)
            {
                await LoadAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task DeactivateAsync(MasterDataDto row)
    {
        if (_busy)
        {
            return;
        }

        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Take out of service",
            $"Products that already use this {TabNameOf(Tab).ToLowerInvariant()} keep resolving it. Taking it out of service "
                + "only stops it being chosen for anything new.",
            yesText: "Take out of service",
            cancelText: "Cancel");

        if (confirmed != true)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            await MasterDataApi.DeactivateAsync(Tab, row.Id).ConfigureAwait(false);
            Snackbar.Add("Taken out of service. Nothing that used it was changed.", Severity.Success);
            await LoadAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = Describe(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private static string Describe(ApiException exception)
    {
        if (exception.Errors.Count == 0)
        {
            return exception.Message;
        }

        var lines = exception.Errors.Select(pair => $"{pair.Key}: {string.Join(" ", pair.Value)}");

        return string.Join(" ", [exception.Message, .. lines]);
    }
}
