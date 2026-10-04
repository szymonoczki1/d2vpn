namespace D2Vpn.Ui.ViewModels;

/// <summary>
/// Anything the sidebar can list and the detail pane can show.
/// </summary>
/// <remarks>
/// <para>
/// The sidebar used to hold modules and nothing else, which stopped working the moment a category
/// needed a settings page in it. Rather than teach the sidebar about two kinds of row, both kinds
/// present the same handful of properties and the detail pane picks its layout from the runtime
/// type through a <c>DataTemplate</c> - which is what WPF does well and what keeps the window from
/// growing a switch on what it is showing.
/// </para>
/// <para>
/// A settings page simply has nothing to report for most of these, which is the point: the sidebar
/// renders it without knowing what it is.
/// </para>
/// </remarks>
public abstract class SidebarItem : ObservableObject
{
    /// <summary>What the sidebar shows, and how the detail pane titles itself.</summary>
    public abstract string Name { get; }

    /// <summary>The second line in the sidebar - a keybind, a timer, or a hint.</summary>
    public abstract string SidebarDetail { get; }

    /// <summary>Whether to show the holding dot. False for anything that is not a module.</summary>
    /// <remarks>
    /// Settable on the base so the sidebar can bind two-way without knowing what it has. Anything
    /// that cannot be switched on simply ignores the set, which is what a settings page does.
    /// </remarks>
    public virtual bool IsActive
    {
        get => false;
        set { }
    }

    /// <summary>Whether to render at full strength. A disabled module is dimmed.</summary>
    public virtual bool IsEnabled
    {
        get => true;
        set { }
    }

    /// <summary>Whether the second line is a running timer, which is shown in red.</summary>
    public virtual bool IsTiming => false;

    /// <summary>
    /// The name, so a list row announces itself as something a person would recognise.
    /// </summary>
    /// <remarks>
    /// WPF falls back to <c>ToString</c> for a list item's accessible name, and without this every
    /// row in the sidebar announced itself to a screen reader as its own class name.
    /// </remarks>
    public override string ToString() => Name;
}
