using System.Runtime.InteropServices;
using System.Runtime.Versioning;


namespace D2Vpn.Core.Input;

/// <summary>
/// Watches for key presses globally and raises an event on each press.
/// </summary>
/// <remarks>
/// In Core rather than in a front end because two of them need it: the CLI host and the window
/// both bind the same keys from the same config file.
/// </remarks>
/// <remarks>
/// <para>
/// Polls <c>GetAsyncKeyState</c> rather than installing a hook. A low-level keyboard hook needs a
/// message pump, which a console application does not have, and it would also let us swallow the
/// keypress - neither of which is wanted here. Polling every few milliseconds is imperceptible for
/// a hotkey and keeps this to one P/Invoke with no window and no message loop.
/// </para>
/// <para>
/// The key is <em>not</em> suppressed: it reaches the game as well. That matters for CapsLock,
/// which the existing AutoHotkey scripts already send, and which would otherwise start toggling
/// caps state. Suppression would need a hook, and is left for if it turns out to be wanted.
/// </para>
/// <para>
/// Presses are edge-triggered. Holding a key down raises one event, not a stream of them.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HotkeyWatcher : IDisposable
{
    private readonly Dictionary<int, Action> _bindings = [];
    private readonly Dictionary<int, bool> _wasDown = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _interval;
    private Task? _loop;

    /// <summary>Raised when a bound key is pressed, with the key's name.</summary>
    public event Action<string>? KeyPressed;

    /// <summary>Creates a watcher polling at <paramref name="interval"/>.</summary>
    /// <remarks>
    /// Default 1 ms. Some uses are frame-precise - a bullet is detached from its shooter by
    /// holding uplink, swapping weapon and firing inside about two seconds - and at 60 fps an 8 ms
    /// poll is half a frame of slop at each end of a sequence that short. One P/Invoke per key per
    /// millisecond is not measurable next to a game.
    /// </remarks>
    public HotkeyWatcher(TimeSpan? interval = null) =>
        _interval = interval ?? TimeSpan.FromMilliseconds(1);

    /// <summary>Binds an action to a virtual-key code.</summary>
    public void Bind(int virtualKey, Action onPressed)
    {
        _bindings[virtualKey] = onPressed;
        _wasDown[virtualKey] = IsDown(virtualKey);
    }

    /// <summary>Starts watching. Safe to call once.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                foreach (var (key, action) in _bindings)
                {
                    var down = IsDown(key);
                    var wasDown = _wasDown[key];
                    _wasDown[key] = down;

                    if (!down || wasDown)
                    {
                        continue;
                    }

                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        // A misbehaving binding must not stop the watcher, which would leave every
                        // other hotkey silently dead.
                        Console.Error.WriteLine($"hotkey {KeyName.Describe(key)}: {ex.Message}");
                    }

                    KeyPressed?.Invoke(KeyName.Describe(key));
                }

                try
                {
                    await Task.Delay(_interval, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    /// <summary>True while the key is physically down.</summary>
    /// <remarks>
    /// The high bit is the pressed state. The low bit is the toggle state, which is why CapsLock
    /// needs the mask: without it a latched CapsLock would read as permanently held.
    /// </remarks>
    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <inheritdoc/>
    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Cancellation on the way out; nothing to salvage.
        }

        _cts.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
