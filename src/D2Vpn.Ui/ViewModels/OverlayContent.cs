using System.Collections.ObjectModel;

namespace D2Vpn.Ui.ViewModels;

/// <summary>
/// Whatever one overlay panel draws.
/// </summary>
/// <remarks>
/// Both panels are a list of rows - a name and a figure - so the window hosts either without
/// knowing which it has. That is also what lets the timer be stacked into the module panel when the
/// two are attached: the same rows, one list.
/// </remarks>
public abstract class OverlayContent : ObservableObject
{
    /// <summary>The rows, in display order.</summary>
    public ObservableCollection<OverlayLine> Lines { get; } = [];

    /// <summary>True when there is anything to draw.</summary>
    public bool HasLines => Lines.Count > 0;

    /// <summary>Rebuilds the rows if what they would say has changed.</summary>
    public abstract void Refresh();
}
