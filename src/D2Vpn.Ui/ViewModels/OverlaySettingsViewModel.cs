using D2Vpn.Core.Hosting;

namespace D2Vpn.Ui.ViewModels;

/// <summary>
/// The overlay's settings, as a page in the sidebar.
/// </summary>
/// <remarks>
/// <para>
/// Every change is applied to the live overlay at once and written to the config, so there is no
/// apply button and nothing to lose by closing the window. Dragging the preview is the same thing
/// as typing the numbers - both end up as a corner and a pair of margins.
/// </para>
/// <para>
/// A corner and margins rather than an absolute position, because the overlay has to stay put when
/// the game window moves or resizes. A point would be right once and wrong afterwards.
/// </para>
/// </remarks>
public sealed class OverlaySettingsViewModel : SidebarItem
{
    /// <summary>The window size the preview stands in for, and that margins are measured against.</summary>
    public const double ReferenceWidth = 1920;

    /// <summary>The height of that reference window.</summary>
    public const double ReferenceHeight = 1080;

    private readonly Action<OverlayConfig> _apply;
    private OverlayConfig _config;

    /// <summary>Creates the page over the current settings.</summary>
    /// <param name="config">What the overlay is using now.</param>
    /// <param name="apply">Called with every change, to save it and update the live overlay.</param>
    public OverlaySettingsViewModel(OverlayConfig config, Action<OverlayConfig> apply)
    {
        _config = config;
        _apply = apply;
    }

    /// <inheritdoc/>
    public override string Name => "Overlay";

    /// <inheritdoc/>
    public override string SidebarDetail => IsEnabledSetting ? "on" : "off";

    /// <summary>The settings as they stand.</summary>
    public OverlayConfig Config => _config;

    /// <summary>Whether the overlay is shown at all.</summary>
    public bool IsEnabledSetting
    {
        get => _config.Enabled;
        set => Change(_config with { Enabled = value });
    }

    /// <summary>The process the overlay follows.</summary>
    public string Process
    {
        get => _config.Process;
        set => Change(_config with { Process = string.IsNullOrWhiteSpace(value) ? "destiny2" : value });
    }

    /// <summary>Show how long each module has been held.</summary>
    public bool ShowTimers
    {
        get => _config.ShowTimers;
        set => Change(_config with { ShowTimers = value });
    }

    /// <summary>Also list modules that are hosted but idle.</summary>
    public bool ShowIdle
    {
        get => _config.ShowIdle;
        set => Change(_config with { ShowIdle = value });
    }

    /// <summary>Disappear entirely when nothing is held.</summary>
    public bool HideWhenIdle
    {
        get => _config.HideWhenIdle;
        set => Change(_config with { HideWhenIdle = value });
    }

    /// <summary>Text size multiplier.</summary>
    public double Scale
    {
        get => _config.Scale;
        set => Change(_config with { Scale = Math.Round(Math.Clamp(value, 0.5, 4), 2) });
    }

    /// <summary>How solid the panel behind the text is.</summary>
    public double Opacity
    {
        get => _config.Opacity;
        set => Change(_config with { Opacity = Math.Round(Math.Clamp(value, 0, 1), 2) });
    }

    /// <summary>
    /// Which panel the position controls are editing.
    /// </summary>
    /// <remarks>
    /// Both are placed from the one preview, because that preview is the screen - two things that
    /// appear on it, positioned from two different pages, is how they end up overlapping with
    /// nowhere showing why.
    /// </remarks>
    public OverlayPanelKind Editing
    {
        get => _editing;
        set
        {
            if (!Set(ref _editing, value))
            {
                return;
            }

            RaiseAll(
                nameof(MarginX), nameof(MarginY), nameof(Corner), nameof(CornerLabel),
                nameof(EditingModules), nameof(EditingTimer));
        }
    }

    private OverlayPanelKind _editing = OverlayPanelKind.Modules;

    /// <summary>
    /// True while the module panel is the one being placed.
    /// </summary>
    /// <remarks>
    /// Settable, so a radio button can bind to it two ways and keep its own selected look. Ignores
    /// being set to false: that arrives as the other button takes the selection, and acting on it
    /// would mean a moment with neither panel being edited.
    /// </remarks>
    public bool EditingModules
    {
        get => _editing == OverlayPanelKind.Modules;
        set
        {
            if (value)
            {
                Editing = OverlayPanelKind.Modules;
            }
        }
    }

    /// <inheritdoc cref="EditingModules"/>
    public bool EditingTimer
    {
        get => _editing == OverlayPanelKind.Timer;
        set
        {
            if (value)
            {
                Editing = OverlayPanelKind.Timer;
            }
        }
    }

    /// <summary>The panel currently being placed.</summary>
    private OverlayPanel Panel =>
        _editing == OverlayPanelKind.Timer ? _config.Timer : _config.Modules;

    /// <summary>
    /// One named panel's placement, whichever is being edited.
    /// </summary>
    /// <remarks>
    /// The preview draws both chips at once, each from its own settings, so it needs the panel it
    /// is not editing as well. Everything else here goes through <see cref="Panel"/>.
    /// </remarks>
    public OverlayPanel PanelFor(OverlayPanelKind kind) =>
        kind == OverlayPanelKind.Timer ? _config.Timer : _config.Modules;

    /// <summary>Whether the module panel is drawn.</summary>
    public bool ShowModules
    {
        get => _config.Modules.Show;
        set => Change(_config with { Modules = _config.Modules with { Show = value } });
    }

    /// <summary>Whether the timer panel is drawn.</summary>
    public bool ShowTimerPanel
    {
        get => _config.Timer.Show;
        set => Change(_config with { Timer = _config.Timer with { Show = value } });
    }

    /// <summary>
    /// Draw the timer as a row of the module panel rather than as a panel of its own.
    /// </summary>
    /// <remarks>
    /// Attached, the timer has no position of its own, so the preview stops offering one.
    /// </remarks>
    public bool TimerAttached
    {
        get => _config.TimerAttached;
        set
        {
            Change(_config with { TimerAttached = value });

            if (value && _editing == OverlayPanelKind.Timer)
            {
                Editing = OverlayPanelKind.Modules;
            }

            RaiseAll(nameof(TimerAttached), nameof(TimerHasOwnPlace));
        }
    }

    /// <summary>True when the timer is a panel in its own right, and so has somewhere to be.</summary>
    public bool TimerHasOwnPlace => !_config.TimerAttached && _config.Timer.Show;

    /// <summary>Distance from the nearer vertical edge, in pixels.</summary>
    public int MarginX
    {
        get => Panel.MarginX;
        set => ChangePanel(Panel with { MarginX = Math.Clamp(value, 0, 1200) });
    }

    /// <summary>Distance from the nearer horizontal edge, in pixels.</summary>
    public int MarginY
    {
        get => Panel.MarginY;
        set => ChangePanel(Panel with { MarginY = Math.Clamp(value, 0, 800) });
    }

    /// <summary>Which corner it is anchored to.</summary>
    public OverlayCorner Corner
    {
        get => Panel.Corner;
        set => ChangePanel(Panel with { Corner = value });
    }

    /// <summary>Puts the edited panel back into the configuration.</summary>
    private void ChangePanel(OverlayPanel panel) =>
        Change(_editing == OverlayPanelKind.Timer
            ? _config with { Timer = panel }
            : _config with { Modules = panel });

    /// <summary>The corner in words, for the preview caption.</summary>
    public string CornerLabel => Panel.Corner switch
    {
        OverlayCorner.TopLeft => "anchored top left",
        OverlayCorner.TopRight => "anchored top right",
        OverlayCorner.BottomLeft => "anchored bottom left",
        _ => "anchored bottom right"
    };

    /// <summary>Nudges the horizontal margin.</summary>
    public void NudgeX(int by) => MarginX += by;

    /// <summary>Nudges the vertical margin.</summary>
    public void NudgeY(int by) => MarginY += by;

    /// <summary>
    /// Places the overlay from a point dropped on the preview, in reference-window pixels.
    /// </summary>
    /// <remarks>
    /// The quadrant the point lands in chooses the corner, and the distance to that corner's two
    /// edges becomes the margins. So a drag produces exactly what typing the numbers would, and the
    /// result still holds when the game window changes size.
    /// </remarks>
    /// <param name="x">Left edge of the dropped panel, in reference pixels.</param>
    /// <param name="y">Top edge of the dropped panel, in reference pixels.</param>
    /// <param name="width">The panel's width in reference pixels.</param>
    /// <param name="height">The panel's height in reference pixels.</param>
    public void PlaceAt(double x, double y, double width, double height)
    {
        var centreX = x + (width / 2);
        var centreY = y + (height / 2);

        var right = centreX > ReferenceWidth / 2;
        var bottom = centreY > ReferenceHeight / 2;

        var corner = (right, bottom) switch
        {
            (false, false) => OverlayCorner.TopLeft,
            (true, false) => OverlayCorner.TopRight,
            (false, true) => OverlayCorner.BottomLeft,
            _ => OverlayCorner.BottomRight
        };

        var marginX = right ? ReferenceWidth - (x + width) : x;
        var marginY = bottom ? ReferenceHeight - (y + height) : y;

        ChangePanel(Panel with
        {
            Corner = corner,
            MarginX = (int)Math.Clamp(Math.Round(marginX), 0, 1200),
            MarginY = (int)Math.Clamp(Math.Round(marginY), 0, 800)
        });
    }

    private void Change(OverlayConfig updated)
    {
        if (updated == _config)
        {
            return;
        }

        _config = updated;
        _apply(updated);

        RaiseAll(
            nameof(Config), nameof(IsEnabledSetting), nameof(Process), nameof(ShowTimers),
            nameof(ShowIdle), nameof(HideWhenIdle), nameof(Scale), nameof(Opacity),
            nameof(MarginX), nameof(MarginY), nameof(Corner), nameof(CornerLabel),
            nameof(ShowModules), nameof(ShowTimerPanel), nameof(TimerAttached),
            nameof(TimerHasOwnPlace), nameof(SidebarDetail));
    }
}
