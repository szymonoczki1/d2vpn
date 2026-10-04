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

    private OverlayWindow? _overlay;

    /// <summary>
    /// Takes ownership of the overlay, so it closes with this window.
    /// </summary>
    /// <remarks>
    /// Owner rather than a second top-level window: without it the application would keep running
    /// with an invisible overlay after the main window closed, since something would still be open.
    /// </remarks>
    public void AttachOverlay(OverlayWindow overlay)
    {
        _overlay = overlay;
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

    private static Border? Chip(object sender) =>
        sender is Canvas canvas && canvas.Children.Count > 0
            ? canvas.Children[0] as Border
            : null;

    /// <summary>Puts the chip where the current settings say, when the page first appears.</summary>
    private void OnPreviewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Canvas canvas
            || Chip(canvas) is not { } chip
            || _model.Selected is not OverlaySettingsViewModel settings)
        {
            return;
        }

        var right = settings.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
        var bottom = settings.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;

        Canvas.SetLeft(chip, right
            ? canvas.Width - chip.Width - (settings.MarginX / PreviewScale)
            : settings.MarginX / PreviewScale);

        Canvas.SetTop(chip, bottom
            ? canvas.Height - chip.Height - (settings.MarginY / PreviewScale)
            : settings.MarginY / PreviewScale);
    }

    private void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas)
        {
            return;
        }

        _draggingChip = true;
        canvas.CaptureMouse();
        MoveChip(canvas, e.GetPosition(canvas));
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

    /// <summary>Centres the chip on the pointer, kept inside the preview.</summary>
    private static void MoveChip(Canvas canvas, Point at)
    {
        if (Chip(canvas) is not { } chip)
        {
            return;
        }

        Canvas.SetLeft(chip, Math.Clamp(at.X - (chip.Width / 2), 0, canvas.Width - chip.Width));
        Canvas.SetTop(chip, Math.Clamp(at.Y - (chip.Height / 2), 0, canvas.Height - chip.Height));
    }

    /// <summary>Turns where the chip ended up into a corner and a pair of margins.</summary>
    private void Commit(Canvas canvas)
    {
        if (Chip(canvas) is not { } chip
            || _model.Selected is not OverlaySettingsViewModel settings)
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
        _overlay?.Close();
        _model.Dispose();
        base.OnClosed(e);
    }
}
