using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace D2Vpn.Ui.ViewModels;

/// <summary>
/// The minimum a WPF binding needs to notice a property changed.
/// </summary>
/// <remarks>
/// Hand-written rather than taken from a MVVM package. There are two view models in this
/// application and they need one interface between them; a dependency that generates it would be
/// more to explain than the twenty lines below.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for the calling property.</summary>
    protected void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    /// <summary>Raises <see cref="PropertyChanged"/> for several properties at once.</summary>
    protected void RaiseAll(params string[] properties)
    {
        foreach (var property in properties)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    /// <summary>Sets a field and raises if it changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(property);
        return true;
    }
}
