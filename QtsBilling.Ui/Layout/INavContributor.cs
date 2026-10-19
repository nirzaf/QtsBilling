namespace QtsBilling.Ui.Layout;

/// <summary>A navigation entry contributed by the host.</summary>
public sealed record NavItem(string Title, string Href, string Icon);

/// <summary>
/// Lets a host add its own entries to the shared navigation (the desktop client adds the point of sale,
/// the browser client adds nothing). The shared library never decides which host shows what.
/// </summary>
public interface INavContributor
{
    IReadOnlyList<NavItem> Items { get; }
}
