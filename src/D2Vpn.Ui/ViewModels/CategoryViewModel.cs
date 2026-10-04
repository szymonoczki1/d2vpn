using System.Collections.ObjectModel;

using D2Vpn.Core.Hosting;

namespace D2Vpn.Ui.ViewModels;

/// <summary>
/// One section of the rail, holding the modules that belong to it.
/// </summary>
/// <remarks>
/// <para>
/// A flat list of every module runs out of room. There are already twelve, the sidebar is nearly
/// full at that, and the things still to come - a macro manager, whatever follows it - are not
/// ports and would not belong in the same list even if there were space.
/// </para>
/// <para>
/// So the far-left rail picks a category and the sidebar shows only that category's modules. Each
/// list stays short however many categories there are, which is the property a flat list and a
/// list of section headers both lack: both of those grow downwards, and downwards is the direction
/// that has already run out.
/// </para>
/// <para>
/// Adding one is a row in <see cref="MainViewModel.BuildCategories"/> and a path in
/// <c>Theme/Icons.xaml</c>. That is the whole extension point.
/// </para>
/// </remarks>
public sealed class CategoryViewModel : ObservableObject
{
    /// <summary>Creates a category.</summary>
    /// <param name="name">Shown on the rail and as the sidebar heading.</param>
    /// <param name="icon">Resource key of the geometry drawn on the rail.</param>
    /// <param name="emptyMessage">Shown when the category has nothing in it.</param>
    public CategoryViewModel(string name, string icon, string emptyMessage)
    {
        Name = name;
        Icon = icon;
        EmptyMessage = emptyMessage;
    }

    /// <summary>The category's name.</summary>
    public string Name { get; }

    /// <summary>The geometry resource drawn on the rail.</summary>
    public string Icon { get; }

    /// <summary>What to say when this category holds nothing.</summary>
    public string EmptyMessage { get; }

    /// <summary>What this category lists, in order.</summary>
    public ObservableCollection<SidebarItem> Items { get; } = [];

    /// <summary>True when there is nothing to list.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>How many of this category's entries are on, for the rail badge.</summary>
    public int ActiveCount => Items.Count(m => m.IsActive);

    /// <summary>True when any of them is on.</summary>
    public bool HasActive => ActiveCount > 0;

    /// <summary>What was last looked at here, so switching categories does not lose your place.</summary>
    public SidebarItem? Selected { get; set; }

    /// <summary>Re-reads what the modules report.</summary>
    public void Refresh() => RaiseAll(nameof(ActiveCount), nameof(HasActive), nameof(IsEmpty));

    /// <summary>The name, so a rail entry announces itself usefully. See SidebarItem.ToString.</summary>
    public override string ToString() => Name;
}
