namespace D2Vpn.Ui.ViewModels;

/// <summary>
/// The module rows and the instance timer drawn as one panel.
/// </summary>
/// <remarks>
/// <para>
/// This is what attaching the timer means: one panel in one corner with the timer as its top row,
/// rather than two panels to place separately. Detached, this is the module rows alone and the
/// timer is drawn by a window of its own.
/// </para>
/// <para>
/// A wrapper rather than folding the timer into <see cref="OverlayViewModel"/>, because the timer
/// still has to be drawable on its own - and a module list that knew about instance traffic would
/// be the two of them stuck together whether or not anyone had asked for that.
/// </para>
/// </remarks>
public sealed class JoinedOverlayContent : OverlayContent
{
    private readonly OverlayContent _modules;
    private readonly OverlayContent _timer;
    private bool _attached;
    private bool _stale = true;

    /// <summary>Joins the two, drawing the timer only while attached.</summary>
    public JoinedOverlayContent(OverlayContent modules, OverlayContent timer, bool attached)
    {
        _modules = modules;
        _timer = timer;
        _attached = attached;

        // Each part rebuilds its rows only when what they say has changed, and announces it when
        // it does. So this rebuilds when a part did and does nothing on the ticks where nothing
        // moved - which at ten a second is nearly all of them.
        _modules.PropertyChanged += (_, _) => _stale = true;

        // Only while attached. Detached, the timer's row is not in this panel, so its ticking is
        // not a reason to rebuild a list it does not appear in - or to re-anchor the window.
        _timer.PropertyChanged += (_, _) => _stale |= _attached;
    }

    /// <summary>Whether the timer is drawn here rather than as a panel of its own.</summary>
    public bool Attached
    {
        get => _attached;
        set
        {
            if (_attached == value)
            {
                return;
            }

            _attached = value;
            _stale = true;
            Refresh();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Refreshes both parts whether or not the timer is attached, because the timer's own window is
    /// drawing it in the detached case and this is the one tick either of them gets.
    /// </remarks>
    public override void Refresh()
    {
        _modules.Refresh();
        _timer.Refresh();

        if (!_stale)
        {
            return;
        }

        _stale = false;
        Lines.Clear();

        // Timer first: it is the headline figure and always one row, so a hold appearing or
        // disappearing underneath does not move it.
        if (_attached)
        {
            foreach (var line in _timer.Lines)
            {
                Lines.Add(line);
            }
        }

        foreach (var line in _modules.Lines)
        {
            Lines.Add(line);
        }

        Raise(nameof(HasLines));
        Raise(nameof(Lines));
    }
}
