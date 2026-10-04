using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

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

    private readonly OverlayContent _model;
    private readonly ForegroundWatcher _watcher;
    private readonly OverlayPanelKind _kind;
    private OverlayConfig _config;

    /// <summary>Creates one panel of the overlay, following a process.</summary>
    /// <param name="model">What this panel shows - modules, or the instance timer.</param>
    /// <param name="config">The overlay settings, shared across panels.</param>
    /// <param name="kind">Which panel this is, and so which position it takes.</param>
    public OverlayWindow(OverlayContent model, OverlayConfig config, OverlayPanelKind kind)
    {
        _model = model;
        _config = config;
        _kind = kind;

        InitializeComponent();
        DataContext = model;

        Dress();

        _model.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(Reposition);
        SizeChanged += OnSizeChanged;

        _watcher = new ForegroundWatcher(config.Process);
        _watcher.Changed += (inFront, bounds) =>
            Dispatcher.BeginInvoke(() => OnForegroundChanged(inFront, bounds));
    }

    /// <summary>This panel's own position, from the shared settings.</summary>
    private OverlayPanel Panel =>
        _kind == OverlayPanelKind.Timer ? _config.Timer : _config.Modules;

    /// <summary>
    /// Whether this panel is wanted at all.
    /// </summary>
    /// <remarks>
    /// Attaching the timer is what takes the timer's own window away. The row itself does not
    /// disappear - it moves into the module panel, which draws it instead - so this is the one
    /// place that has to know, and both windows are built either way so the setting takes effect
    /// without a restart.
    /// </remarks>
    private bool PanelWanted => _kind == OverlayPanelKind.Timer
        ? _config.Timer.Show && !_config.TimerAttached
        : _config.Modules.Show;

    /// <summary>
    /// Whether this panel has anything worth drawing.
    /// </summary>
    /// <remarks>
    /// The module panel may deliberately be left on screen with nothing in it - that is what
    /// turning off "disappear entirely when nothing is held" asks for, and it keeps the thing
    /// visible so you can see where it is. The timer has no equivalent: outside an activity there
    /// is no figure, so what would be left is an empty box over the game.
    /// </remarks>
    private bool HasSomethingToSay => _kind == OverlayPanelKind.Timer
        ? _model.HasLines
        : !_config.HideWhenIdle || _model.HasLines;

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
            && PanelWanted
            && _gameInFront
            && _gameBounds.IsReal
            && HasSomethingToSay;

        if (!wanted)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (IsVisible)
        {
            Place();
            return;
        }

        // Shown transparent, placed on the next layout pass, and only then faded in.
        //
        // The right and bottom anchors subtract the panel's own size, and that size is not known
        // until it has laid out - measuring it by hand returns whatever it last was, which after a
        // spell hidden is wrong. So the first frames were drawn against a stale size and corrected
        // once layout caught up, which is the jump; the top left anchor never reads the size, which
        // is why that one corner always looked right.
        Opacity = 0;
        Show();

        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                Place();
                Opacity = 1;
            });
    }

    /// <summary>Puts the panel in its corner of the game window, using its laid-out size.</summary>
    private void Place()
    {
        // Win32 gives physical pixels and WPF positions in device-independent units, so on a
        // scaled display the two differ - without this the panel drifts further off the corner the
        // higher the scaling is.
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        var topLeft = transform.Transform(new Point(_gameBounds.Left, _gameBounds.Top));
        var size = transform.Transform(new Point(_gameBounds.Width, _gameBounds.Height));

        var panel = Panel;
        var right = panel.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
        var bottom = panel.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;

        Left = right
            ? topLeft.X + size.X - ActualWidth - panel.MarginX
            : topLeft.X + panel.MarginX;

        Top = bottom
            ? topLeft.Y + size.Y - ActualHeight - panel.MarginY
            : topLeft.Y + panel.MarginY;
    }

    /// <summary>
    /// Re-anchors when the panel's own size changes.
    /// </summary>
    /// <remarks>
    /// The window sizes to its contents, so a module appearing, or a timer passing from 9.9s to
    /// 10.0s, changes its width. Anchored right or bottom, the position depends on that size, so
    /// without this the panel would creep every time the text grew by a character.
    /// </remarks>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsVisible)
        {
            Place();
        }
    }

    /// <summary>Applies changed settings without restarting.</summary>
    public void Apply(OverlayConfig config)
    {
        _config = config;

        Dress();
        Reposition();
    }

    /// <summary>Applies this panel's own size and transparency.</summary>
    /// <remarks>
    /// The whole panel, not only the text inside it: scaling the contents alone left the padding
    /// and the corner radius at full size, so a small overlay looked like a large one with small
    /// writing in it.
    /// </remarks>
    private void Dress()
    {
        var panel = Panel;

        Backdrop.Background = new SolidColorBrush((Color)FindResource("RailColor"))
        {
            Opacity = panel.Opacity
        };

        Backdrop.LayoutTransform = new ScaleTransform(panel.Scale, panel.Scale);
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
