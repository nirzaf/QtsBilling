namespace QtsBilling.Ui.Layout;

/// <summary>
/// A navigation entry contributed by the host. <paramref name="Group"/> places a new module in the shared
/// grouping; an entry whose address the shell catalogue already knows simply marks that row as shipped by this
/// host, so the desktop point of sale does not appear twice (specification task 13).
/// </summary>
public sealed record NavItem(string Title, string Href, string Icon, string Group = ShellNavigation.GroupOverview);

/// <summary>
/// Lets a host add its own entries to the shared navigation (the desktop client adds the point of sale,
/// the browser client adds nothing). The shared library never decides which host shows what.
/// </summary>
public interface INavContributor
{
    IReadOnlyList<NavItem> Items { get; }
}
