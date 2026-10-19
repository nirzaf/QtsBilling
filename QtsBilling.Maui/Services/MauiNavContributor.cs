using MudBlazor;
using QtsBilling.Ui.Layout;

namespace QtsBilling.Maui.Services;

/// <summary>
/// The desktop host adds the point of sale to the shared navigation. The browser host contributes
/// nothing, so the shared navigation stays honest about what each host can do.
/// </summary>
public sealed class MauiNavContributor : INavContributor
{
    public IReadOnlyList<NavItem> Items { get; } =
    [
        new NavItem("Point of sale", "pos", Icons.Material.Filled.PointOfSale),
    ];
}
