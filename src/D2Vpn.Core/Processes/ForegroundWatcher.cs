using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace D2Vpn.Core.Processes;

/// <summary>Where a window is, in physical pixels.</summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    /// <summary>True when the rectangle has area.</summary>
    public bool IsReal => Width > 0 && Height > 0;
}

/// <summary>
/// Watches which application is in front, so an overlay can follow one of them.
/// </summary>
/// <remarks>
/// <para>
/// Polled rather than hooked. A foreground-change hook needs either a global hook, which an
/// anti-cheat has every reason to object to, or a window and a message loop, which a watcher
/// should not require. Asking four times a second costs one syscall and is imperceptible for
/// something a person does with Alt+Tab.
/// </para>
/// <para>
/// It reports the window's rectangle as well as whether it is in front, because an overlay that
/// sits in a fixed screen corner is wrong the moment the game is not fullscreen on that monitor.
/// Following the window itself is right in every arrangement.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ForegroundWatcher : IDisposable
{
    private readonly string _processName;
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _interval;

    private Task? _loop;
    private bool _disposed;

    /// <summary>Watches for a process by name.</summary>
    /// <param name="processName">Process name without the extension, such as <c>destiny2</c>.</param>
    /// <param name="interval">How often to look. 250 ms by default.</param>
    public ForegroundWatcher(string processName, TimeSpan? interval = null)
    {
        _processName = processName;
        _interval = interval ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>True while the watched process owns the foreground window.</summary>
    public bool IsInFront { get; private set; }

    /// <summary>Where that window is, when it is in front.</summary>
    public ScreenRect Bounds { get; private set; }

    /// <summary>
    /// Raised when the answer changes, with whether it is in front and where.
    /// </summary>
    /// <remarks>
    /// Also raised when the window moves or resizes while staying in front, so an overlay
    /// following it does not have to poll separately.
    /// </remarks>
    public event Action<bool, ScreenRect>? Changed;

    /// <summary>Starts watching. Safe to call once.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                Poll();

                try
                {
                    await Task.Delay(_interval, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    private void Poll()
    {
        var front = false;
        var bounds = default(ScreenRect);

        try
        {
            var handle = GetForegroundWindow();

            if (handle != IntPtr.Zero)
            {
                _ = GetWindowThreadProcessId(handle, out var pid);

                // Compared by id rather than by name on the window, because several processes can
                // share a name and only the one actually in front should count.
                front = IsWatched(pid);

                if (front && GetWindowRect(handle, out var rect))
                {
                    bounds = new ScreenRect(
                        rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                }
            }
        }
        catch (Exception)
        {
            // A window that vanished between the two calls, or a process that exited. Treating it
            // as "not in front" hides the overlay, which is the safe way to be wrong.
            front = false;
        }

        if (front == IsInFront && bounds == Bounds)
        {
            return;
        }

        IsInFront = front;
        Bounds = bounds;
        Changed?.Invoke(front, bounds);
    }

    private bool IsWatched(uint pid)
    {
        if (pid == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);

            return string.Equals(
                process.ProcessName, _processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // Gone between the handle and the lookup.
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Cancellation on the way out.
        }

        _stop.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
