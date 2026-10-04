using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

using D2Vpn.Core.Hosting;
using D2Vpn.Ui.ViewModels;

namespace D2Vpn.Ui;

/// <summary>
/// Starts the window, after finding or creating a config for it.
/// </summary>
/// <remarks>
/// The process is elevated by its manifest, because it opens the WinDivert driver itself. There is
/// no unelevated mode worth having: a window that cannot open a handle can only report that.
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class App : Application
{
    /// <inheritdoc/>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Numbers here are counters and durations read at a glance, and a locale whose group
        // separator is a non-breaking space has already caused trouble once in this project.
        //
        // Both forms are needed. DefaultThreadCurrentCulture only reaches threads created after it
        // is set, so on its own the UI thread kept the system locale and a pause of two and a half
        // seconds was rendered "2,4s".
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        var path = ConfigLocation.Resolve(
            e.Args.FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase)));

        HostConfig config;
        IReadOnlyList<string> warnings;

        try
        {
            // First run writes the template rather than demanding the CLI be used first. The window
            // has to be able to stand alone, since that is the whole point of shipping one.
            ConfigLocation.EnsureExists(path);
            // Forgiving, because this window is the tool for fixing a config and must not be
            // stopped from opening by the config needing fixing. A duplicate key used to be fatal,
            // which left the only thing that could resolve it unable to start.
            config = HostConfig.LoadForgiving(path, out warnings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not load the configuration.\n\n{path}\n\n{ex.Message}",
                "d2vpn", MessageBoxButton.OK, MessageBoxImage.Error);

            Shutdown(1);
            return;
        }

        var model = new MainViewModel(path, config);

        // Shown either way. A failure to open a handle - another limiter still running is the usual
        // cause - belongs in the status bar where it can be read, not in a dialog that closes the
        // application.
        model.Start();

        // After Start so it is not immediately replaced by the hosting summary, and in the footer
        // rather than a dialog: these are things already dealt with, not decisions to make.
        //
        // Skipped when startup failed, because the footer is then showing why nothing is hosted -
        // which matters more than a key that was unbound, and which has nowhere else to appear.
        if (warnings.Count > 0 && !model.Failed)
        {
            model.Flash(warnings.Count == 1
                ? warnings[0]
                : $"fixed {warnings.Count} config problems: {string.Join("; ", warnings)}");
        }

        var main = new MainWindow(model);

        MainWindow = main;
        MainWindow.Show();

        // After Show, because a window cannot own another until it has been shown itself. Only when
        // asked for, and only when there is something to report: an overlay over a session that
        // hosts nothing would be an empty panel following the game around.
        if (config.Overlay.Enabled && model.Modules.Count > 0)
        {
            var overlay = new OverlayViewModel(model.Modules, config.Overlay);

            model.OverlayTick += overlay.Refresh;
            main.AttachOverlay(new OverlayWindow(overlay, config.Overlay));
        }
    }
}

/// <summary>
/// True to <see cref="Visibility.Visible"/>, false to <see cref="Visibility.Collapsed"/>.
/// </summary>
/// <remarks>
/// Collapsed rather than Hidden, so a section a module does not have takes no space instead of
/// leaving a gap - 7500 has no keepalive to switch, and pause has neither switch.
/// </remarks>
/// <summary>Looks a geometry up by resource key.</summary>
/// <remarks>
/// A category names its icon with a string so it stays a plain object with no WPF types on it,
/// which is what lets the categories be built in the view model rather than in XAML.
/// </remarks>
[ValueConversion(typeof(string), typeof(Geometry))]
public sealed class KeyToGeometryConverter : IValueConverter
{
    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current.TryFindResource(key) : null;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Dims an element when the bound value is false.</summary>
/// <remarks>
/// For a disabled module in the sidebar: it stays listed and selectable, because you still need to
/// reach its settings, but it should not read as one of the live ones.
/// </remarks>
[ValueConversion(typeof(bool), typeof(double))]
public sealed class BoolToOpacityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is false ? 0.4 : 1.0;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only when the bound value is null.</summary>
/// <remarks>For the empty state: there is nothing to configure until a module is selected.</remarks>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only when the bound value is not null.</summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc/>
    public object ConvertBack(
        object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
