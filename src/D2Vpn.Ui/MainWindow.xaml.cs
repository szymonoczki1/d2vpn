using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using D2Vpn.Core.Hosting;
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
    private readonly DispatcherTimer _live;

    /// <summary>Creates the window over a prepared model.</summary>
    public MainWindow(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;

        // Two ticks, because the two kinds of reading want different rates.
        //
        // The timers count in tenths, and sampling at the same interval they display makes them
        // skip and repeat as the two drift against each other - so they are read four times per
        // tenth and look continuous. The on states ride along, which is also what makes a hold
        // started by a key or over the pipe appear here more or less at once.
        _live = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };

        _live.Tick += (_, _) => _model.TickLive();
        _live.Start();

        // Everything else - counters, switch states - at a rate suited to reading rather than to
        // animating. Metrics builds a list on every read, and a packet count does not become more
        // useful for being sampled forty times a second.
        _refresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };

        _refresh.Tick += (_, _) => _model.Refresh();
        _refresh.Start();
    }

    /// <summary>Drags the window, and maximises on a double click as a caption bar does.</summary>
    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximised();
            return;
        }

        // Guarded: DragMove throws if the button is already up by the time it runs, which happens
        // on a fast click.
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void OnMinimise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e) => ToggleMaximised();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximised() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private readonly List<OverlayWindow> _overlays = [];

    /// <summary>
    /// Takes ownership of an overlay panel, so it closes with this window.
    /// </summary>
    /// <remarks>
    /// Owner rather than a second top-level window: without it the application would keep running
    /// with an invisible overlay after the main window closed, since something would still be open.
    /// A list because there are two panels - the modules and the instance timer - which can sit in
    /// different corners, and each is its own window so each can be placed on its own.
    /// </remarks>
    public void AttachOverlay(OverlayWindow overlay)
    {
        _overlays.Add(overlay);
        overlay.Owner = this;
        overlay.Begin();
    }

    // ---- Overlay position preview -------------------------------------------------------

    private bool _draggingChip;

    /// <summary>
    /// How many reference pixels one preview pixel stands for.
    /// </summary>
    /// <remarks>
    /// The preview is 384x216 standing in for a 1920x1080 window, so exactly five. Margins are
    /// stored in that reference window's pixels rather than as a fraction, because the overlay
    /// anchors to a corner and a fixed inset from a corner is what stays put when the real window
    /// is a different size.
    /// </remarks>
    private const double PreviewScale = OverlaySettingsViewModel.ReferenceWidth / 384.0;

    /// <summary>
    /// One of the preview's chips, by the panel it stands for.
    /// </summary>
    /// <remarks>
    /// Found by tag rather than by index. There are two, and which is declared first is a drawing
    /// order decision that should not also decide which one a drag moves.
    /// </remarks>
    private static Border? Chip(Canvas canvas, OverlayPanelKind kind) =>
        canvas.Children.OfType<Border>()
            .FirstOrDefault(b => Equals(b.Tag, kind.ToString()));

    /// <summary>Puts both chips where their own settings say.</summary>
    /// <remarks>
    /// Both, every time, rather than only the one being edited: they are two things on one screen,
    /// and the point of showing them together is seeing whether they overlap.
    /// </remarks>
    private void OnPreviewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Canvas canvas
            || _model.Selected is not OverlaySettingsViewModel settings)
        {
            return;
        }

        foreach (var kind in Panels)
        {
            if (Chip(canvas, kind) is not { } chip)
            {
                continue;
            }

            var panel = settings.PanelFor(kind);
            var right = panel.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
            var bottom = panel.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;

            Canvas.SetLeft(chip, right
                ? canvas.Width - chip.Width - (panel.MarginX / PreviewScale)
                : panel.MarginX / PreviewScale);

            Canvas.SetTop(chip, bottom
                ? canvas.Height - chip.Height - (panel.MarginY / PreviewScale)
                : panel.MarginY / PreviewScale);
        }
    }

    private static readonly OverlayPanelKind[] Panels =
        [OverlayPanelKind.Modules, OverlayPanelKind.Timer];

    /// <summary>
    /// Starts a drag, on whichever chip was grabbed.
    /// </summary>
    /// <remarks>
    /// Clicking a chip selects its panel as well as picking it up, so the obvious gesture - grab
    /// the thing you want to move - works without having to find the right button first. A click on
    /// bare canvas moves the panel already selected, which is what keeps placing by single clicks
    /// working.
    /// </remarks>
    private void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas
            || _model.Selected is not OverlaySettingsViewModel settings)
        {
            return;
        }

        var at = e.GetPosition(canvas);

        if (Grabbed(canvas, settings, at) is { } grabbed)
        {
            settings.Editing = grabbed;
        }

        _draggingChip = true;
        canvas.CaptureMouse();
        MoveChip(canvas, at);
    }

    /// <summary>Which chip the pointer is over, or null for bare canvas.</summary>
    /// <remarks>
    /// The timer's chip can only be grabbed while it has a place of its own. Attached, it is drawn
    /// as a row of the module panel and has no position to drag. Tested before the module chip
    /// because it is the smaller of the two and is drawn over it.
    /// </remarks>
    private static OverlayPanelKind? Grabbed(
        Canvas canvas, OverlaySettingsViewModel settings, Point at)
    {
        foreach (var kind in Panels.Reverse())
        {
            if (kind == OverlayPanelKind.Timer && !settings.TimerHasOwnPlace)
            {
                continue;
            }

            if (Chip(canvas, kind) is not { } chip)
            {
                continue;
            }

            var left = Canvas.GetLeft(chip);
            var top = Canvas.GetTop(chip);

            if (at.X >= left && at.X <= left + chip.Width
                && at.Y >= top && at.Y <= top + chip.Height)
            {
                return kind;
            }
        }

        return null;
    }

    private void OnPreviewMove(object sender, MouseEventArgs e)
    {
        if (_draggingChip && sender is Canvas canvas)
        {
            MoveChip(canvas, e.GetPosition(canvas));
        }
    }

    private void OnPreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas)
        {
            return;
        }

        _draggingChip = false;
        canvas.ReleaseMouseCapture();
        Commit(canvas);
    }

    /// <summary>Centres the selected panel's chip on the pointer, kept inside the preview.</summary>
    private void MoveChip(Canvas canvas, Point at)
    {
        if (_model.Selected is not OverlaySettingsViewModel settings
            || Chip(canvas, settings.Editing) is not { } chip)
        {
            return;
        }

        Canvas.SetLeft(chip, Math.Clamp(at.X - (chip.Width / 2), 0, canvas.Width - chip.Width));
        Canvas.SetTop(chip, Math.Clamp(at.Y - (chip.Height / 2), 0, canvas.Height - chip.Height));
    }

    /// <summary>Turns where the chip ended up into a corner and a pair of margins.</summary>
    private void Commit(Canvas canvas)
    {
        if (_model.Selected is not OverlaySettingsViewModel settings
            || Chip(canvas, settings.Editing) is not { } chip)
        {
            return;
        }

        settings.PlaceAt(
            Canvas.GetLeft(chip) * PreviewScale,
            Canvas.GetTop(chip) * PreviewScale,
            chip.Width * PreviewScale,
            chip.Height * PreviewScale);
    }

    /// <summary>The arrows beside the numbers, for placing it more precisely than a drag can.</summary>
    private void OnNudge(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }
            || _model.Selected is not OverlaySettingsViewModel settings)
        {
            return;
        }

        // Ten at a time: one pixel per click is not a useful unit at this size, and the drag
        // already covers coarse placement.
        switch (tag)
        {
            case "x-": settings.NudgeX(-10); break;
            case "x+": settings.NudgeX(10); break;
            case "y-": settings.NudgeY(-10); break;
            case "y+": settings.NudgeY(10); break;
        }

        SyncPreview();
    }

    /// <summary>Puts the chip back in step with the numbers after they change on their own.</summary>
    private void SyncPreview()
    {
        if (FindPreviewCanvas(this) is { } canvas)
        {
            OnPreviewLoaded(canvas, new RoutedEventArgs());
        }
    }

    private static Canvas? FindPreviewCanvas(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is Canvas canvas)
            {
                return canvas;
            }

            if (FindPreviewCanvas(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Opens the folder the API key goes in, creating it if this is the first time.
    /// </summary>
    /// <remarks>
    /// Rather than a file picker. The key is pasted into a text file that may not exist yet, which
    /// is a thing to do in Explorer, not something to choose - and the path is long enough that
    /// typing it from the label would be its own small ordeal.
    /// </remarks>
    private void OnOpenKeyFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ConfigLocation.Folder);

            Process.Start(new ProcessStartInfo
            {
                FileName = ConfigLocation.Folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _model.Flash($"could not open {ConfigLocation.Folder}: {ex.Message}");
        }
    }

    /// <summary>
    /// The arrows on the macro panel.
    /// </summary>
    /// <remarks>
    /// Step sizes differ per setting because the units do. A click gap is tuned in single
    /// milliseconds, a run length in whole seconds, and a screen delay somewhere between - one step
    /// for all three would make two of them tedious and the third imprecise.
    /// </remarks>
    private void OnMacroNudge(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }
            || _model.Selected is not ModuleViewModel { Macro: { } macro })
        {
            return;
        }

        var up = tag.EndsWith('+');
        var what = tag[..^1];

        var step = what switch
        {
            "duration" => 1000,
            "open" or "page" or "close" or "uplink" or "end-delay" => 20,
            "end" => 1,
            _ => 5
        };

        macro.Nudge(what, up ? step : -step);
    }

    /// <summary>The arrows on the timer page.</summary>
    private void OnTimerNudge(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }
            || _model.Selected is not TimerSettingsViewModel timer)
        {
            return;
        }

        // Five at a time. These are waits on a heartbeat arriving every three seconds, so a second
        // either way is below the resolution of what they are measuring.
        switch (tag)
        {
            case "hide-": timer.NudgeHide(-5); break;
            case "hide+": timer.NudgeHide(5); break;
            case "end-": timer.NudgeEnd(-5); break;
            case "end+": timer.NudgeEnd(5); break;
        }
    }

    private void OnReleaseAll(object sender, RoutedEventArgs e) => _model.ReleaseAll();

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_model.SelectedModule is not { SupportsReset: true } module)
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
            || _model.SelectedModule is not { } module)
        {
            return;
        }

        // Clicking a second key while one is already waiting would otherwise leave two handlers
        // attached, and the first button showing a prompt it never comes back from.
        CancelCapture();

        _capture = new Capture(button, slot, module, OnCaptured);
        PreviewKeyDown += _capture.Handler;
        Focus();
    }

    private void OnCaptured(Capture capture, KeyEventArgs args)
    {
        PreviewKeyDown -= capture.Handler;
        _capture = null;
        capture.Restore();

        // Esc clears the bind. Otherwise a module can only ever gain keys, never lose them.
        int? code = args.Key == Key.Escape
            ? null
            : KeyInterop.VirtualKeyFromKey(args.Key == Key.System ? args.SystemKey : args.Key);

        if (_model.Rebind(capture.Module, capture.Slot, code) is { } problem)
        {
            MessageBox.Show(
                this, problem, "Key already in use",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelCapture()
    {
        if (_capture is not { } capture)
        {
            return;
        }

        PreviewKeyDown -= capture.Handler;
        _capture = null;
        capture.Restore();
    }

    /// <summary>
    /// One key-capture in progress, which knows how to put the button back as it found it.
    /// </summary>
    /// <remarks>
    /// The prompt cannot simply be assigned to <see cref="ContentControl.Content"/>: that is a
    /// local value, and setting one <b>removes the binding</b>. Writing the old text back
    /// afterwards then leaves a literal string where a binding used to be, so that button shows the
    /// key it had when it was first clicked and never updates again - which is exactly what
    /// happened: the sidebar, still bound, showed the new key while the panel showed the old one.
    /// So the binding is detached and put back rather than overwritten.
    /// </remarks>
    private sealed class Capture
    {
        private readonly Button _button;
        private readonly Binding? _binding;
        private readonly object? _original;

        public Capture(
            Button button, BindSlot slot, ModuleViewModel module,
            Action<Capture, KeyEventArgs> onCaptured)
        {
            _button = button;
            _binding = BindingOperations.GetBinding(button, ContentControl.ContentProperty);
            _original = button.Content;

            Slot = slot;
            Module = module;
            Handler = (_, args) =>
            {
                args.Handled = true;
                onCaptured(this, args);
            };

            button.Content = "press a key…  (Esc to clear)";
        }

        public BindSlot Slot { get; }

        public ModuleViewModel Module { get; }

        public KeyEventHandler Handler { get; }

        /// <summary>Puts the binding back, so the button tracks the module again.</summary>
        public void Restore()
        {
            if (_binding is not null)
            {
                BindingOperations.SetBinding(_button, ContentControl.ContentProperty, _binding);
            }
            else
            {
                _button.Content = _original;
            }
        }
    }

    private Capture? _capture;

    /// <inheritdoc/>
    protected override void OnClosed(EventArgs e)
    {
        // Releasing before the handles close is what stops a crash or a quick Alt+F4 leaving the
        // game held. Dispose does it in order: keys, then holds, then handles.
        CancelCapture();
        _live.Stop();
        _refresh.Stop();
        foreach (var overlay in _overlays)
        {
            overlay.Close();
        }

        _model.Dispose();
        base.OnClosed(e);
    }
}
