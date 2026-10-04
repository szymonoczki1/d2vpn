using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using D2Vpn.Core.Hosting;
using D2Vpn.Core.Processes;
using D2Vpn.Ui.ViewModels;

namespace D2Vpn.Ui;

/// <summary>
/// A small panel over the game, saying what is held and for how long.
/// </summary>
/// <remarks>
/// <para>
/// Visible only while the watched process is in front, so it is invisible on the desktop and while
/// anything else has focus. It cannot be clicked, cannot take focus and does not appear in Alt+Tab
/// - all three through extended window styles applied once the handle exists, because a window
/// that swallowed a click meant for the game would be worse than no overlay at all.
/// </para>
/// <para>
/// <b>Exclusive fullscreen will not show it.</b> Nothing outside the game can draw there: that
/// process owns the display and a composited window is not in the chain. Borderless windowed
/// works, and is what most people run for alt-tabbing anyway.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly OverlayViewModel _model;
    private readonly ForegroundWatcher _watcher;
    private OverlayConfig _config;

    /// <summary>Creates the overlay over a model, following a process.</summary>
    public OverlayWindow(OverlayViewModel model, OverlayConfig config)
    {
        _model = model;
        _config = config;

        InitializeComponent();
        DataContext = model;

        Panel.Background = new SolidColorBrush((Color)FindResource("RailColor"))
        {
            Opacity = config.Opacity
        };

        Root.LayoutTransform = new ScaleTransform(config.Scale, config.Scale);

        _model.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(Reposition);

        _watcher = new ForegroundWatcher(config.Process);
        _watcher.Changed += (inFront, bounds) =>
            Dispatcher.BeginInvoke(() => OnForegroundChanged(inFront, bounds));
    }

    /// <summary>Where the game window is, as last seen.</summary>
    private ScreenRect _gameBounds;

    private bool _gameInFront;

    /// <summary>Starts watching and showing.</summary>
    public void Begin() => _watcher.Start();

    /// <inheritdoc/>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);

        // Transparent makes it click-through, NoActivate stops it ever taking focus, and
        // ToolWindow keeps it out of Alt+Tab. Layered is required for Transparent to apply.
        SetWindowLong(
            handle,
            GwlExStyle,
            style | WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate);
    }

    private void OnForegroundChanged(bool inFront, ScreenRect bounds)
    {
        _gameInFront = inFront;
        _gameBounds = bounds;
        Reposition();
    }

    /// <summary>
    /// Moves the panel to its corner of the game window, and shows or hides it.
    /// </summary>
    /// <remarks>
    /// Positioned against the game's own rectangle rather than a screen corner, so it is right
    /// whichever monitor the game is on and whether it is windowed or not.
    /// </remarks>
    private void Reposition()
    {
        var wanted = _config.Enabled
            && _gameInFront
            && _gameBounds.IsReal
            && (!_config.HideWhenIdle || _model.HasLines);

        if (!wanted)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        // Win32 gives physical pixels and WPF positions in device-independent units, so on a
        // scaled display the two differ - without this the panel drifts further off the corner the
        // higher the scaling is.
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        var topLeft = transform.Transform(new Point(_gameBounds.Left, _gameBounds.Top));
        var size = transform.Transform(new Point(_gameBounds.Width, _gameBounds.Height));

        var right = _config.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
        var bottom = _config.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;

        Left = right
            ? topLeft.X + size.X - ActualWidth - _config.MarginX
            : topLeft.X + _config.MarginX;

        Top = bottom
            ? topLeft.Y + size.Y - ActualHeight - _config.MarginY
            : topLeft.Y + _config.MarginY;
    }

    /// <summary>Applies changed settings without restarting.</summary>
    public void Apply(OverlayConfig config)
    {
        _config = config;

        Panel.Background = new SolidColorBrush((Color)FindResource("RailColor"))
        {
            Opacity = config.Opacity
        };

        Root.LayoutTransform = new ScaleTransform(config.Scale, config.Scale);
        Reposition();
    }

    /// <inheritdoc/>
    protected override void OnClosed(EventArgs e)
    {
        _watcher.Dispose();
        base.OnClosed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
