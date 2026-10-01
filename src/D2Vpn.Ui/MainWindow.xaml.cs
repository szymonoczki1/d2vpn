using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using D2Vpn.Ui.ViewModels;

namespace D2Vpn.Ui;

/// <summary>
/// The window. A sidebar of modules, and a panel for the selected one.
/// </summary>
/// <remarks>
/// Code-behind rather than commands and a router, because there are five interactions and a
/// command infrastructure for five interactions is ceremony. Everything that decides anything lives
/// in <see cref="MainViewModel"/>; this file is the bridge from clicks and keystrokes to it.
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private readonly DispatcherTimer _refresh;

    /// <summary>Creates the window over a prepared model.</summary>
    public MainWindow(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;

        // Counters are read from the live modules rather than pushed, so something has to ask. Four
        // times a second is enough to look live and cheap enough not to matter next to a game.
        _refresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };

        _refresh.Tick += (_, _) => _model.Refresh();
        _refresh.Start();
    }

    /// <summary>
    /// Asks Windows for a dark title bar, so the system chrome matches the window.
    /// </summary>
    /// <remarks>
    /// WPF does not style the non-client area, so without this a near-black window sits under a
    /// white caption bar. The attribute number changed between Windows 10 builds, hence both;
    /// a build that knows neither simply keeps the light one.
    /// </remarks>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var on = 1;

        foreach (var attribute in (int[])[20, 19])
        {
            if (DwmSetWindowAttribute(handle, attribute, ref on, sizeof(int)) == 0)
            {
                return;
            }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);

    private void OnReleaseAll(object sender, RoutedEventArgs e) => _model.ReleaseAll();

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is not { SupportsReset: true } module)
        {
            return;
        }

        module.RequestReset();
        _model.Refresh();
    }

    /// <summary>
    /// Captures the next keystroke and binds it to whichever slot the clicked button names.
    /// </summary>
    /// <remarks>
    /// A modal capture rather than a text box, because a key is a key and typing its name invites
    /// spellings the parser will not take. The button says what it is waiting for, so the mode is
    /// never ambiguous.
    /// </remarks>
    private void OnRebind(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.Tag is not string tag
            || !Enum.TryParse<BindSlot>(tag, out var slot)
            || _model.Selected is not { } module)
        {
            return;
        }

        var original = button.Content;
        button.Content = "press a key…  (Esc to clear)";

        void Finish(KeyEventArgs args)
        {
            PreviewKeyDown -= Capture;
            button.Content = original;

            // Esc clears the bind. Otherwise the module can only ever gain keys, never lose them.
            int? code = args.Key == Key.Escape
                ? null
                : KeyInterop.VirtualKeyFromKey(
                    args.Key == Key.System ? args.SystemKey : args.Key);

            if (_model.Rebind(module, slot, code) is { } problem)
            {
                MessageBox.Show(
                    this, problem, "Key already in use",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void Capture(object s, KeyEventArgs args)
        {
            args.Handled = true;
            Finish(args);
        }

        PreviewKeyDown += Capture;
        Focus();
    }

    /// <inheritdoc/>
    protected override void OnClosed(EventArgs e)
    {
        // Releasing before the handles close is what stops a crash or a quick Alt+F4 leaving the
        // game held. Dispose does it in order: keys, then holds, then handles.
        _refresh.Stop();
        _model.Dispose();
        base.OnClosed(e);
    }
}
