using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace D2Vpn.Core.Input;

/// <summary>
/// Clicks and keystrokes, sent the way Windows sends real ones.
/// </summary>
/// <remarks>
/// <para>
/// <c>SendInput</c> puts events into the system input queue, which is the same queue a mouse and
/// keyboard feed. Nothing is attached to the game: no window is subclassed, no message hook is
/// installed, no memory is read or written, and no message is posted to a game window. This is the
/// same mechanism AutoHotkey uses underneath, so choosing one over the other changes how it is
/// packaged rather than what it does.
/// </para>
/// <para>
/// A click is a down and an up as two events in one call. Sending them in separate calls lets
/// another process's input interleave between them, which arrives as a drag.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SyntheticInput : IInputSink
{
    /// <inheritdoc/>
    public void MoveTo(int x, int y)
    {
        var input = MouseInput(MouseEventAbsolute | MouseEventVirtualDesk | MouseEventMove, x, y);
        Send(input);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Moved and clicked in one call, for the same reason the two halves of the click are: an
    /// event arriving between the move and the press lands the click wherever that event left the
    /// pointer.
    /// </remarks>
    public void ClickAt(int x, int y)
    {
        Span<Input> inputs =
        [
            MouseInput(MouseEventAbsolute | MouseEventVirtualDesk | MouseEventMove, x, y),
            MouseInput(MouseEventLeftDown, x, y),
            MouseInput(MouseEventLeftUp, x, y)
        ];

        Send(inputs);
    }

    /// <inheritdoc/>
    public void PressKey(ushort virtualKey)
    {
        Span<Input> inputs =
        [
            KeyInput(virtualKey, up: false),
            KeyInput(virtualKey, up: true)
        ];

        Send(inputs);
    }

    /// <summary>
    /// Turns a screen pixel into the 0-65535 coordinate <c>SendInput</c> takes.
    /// </summary>
    /// <remarks>
    /// Against the virtual desktop rather than the primary monitor, since that is what
    /// <c>MOUSEEVENTF_VIRTUALDESK</c> means and the two differ the moment there is a second screen
    /// - above or to the left of the primary, the virtual origin is negative.
    /// </remarks>
    private static (int X, int Y) ToAbsolute(int x, int y)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);

        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        return (
            (int)(((long)(x - left) * 65535) / (width - 1)),
            (int)(((long)(y - top) * 65535) / (height - 1)));
    }

    private static Input MouseInput(uint flags, int x, int y)
    {
        var (ax, ay) = ToAbsolute(x, y);

        return new Input
        {
            Type = InputMouse,
            Union = new InputUnion
            {
                Mouse = new MouseInputData
                {
                    Dx = ax,
                    Dy = ay,
                    MouseData = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static Input KeyInput(ushort key, bool up) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = key,

                // Left and right arrows are extended keys. Without the flag some applications read
                // them as the numeric keypad equivalents, which are a different key.
                Scan = 0,
                Flags = (up ? KeyEventKeyUp : 0u) | (IsExtended(key) ? KeyEventExtended : 0u),
                Time = 0,
                ExtraInfo = IntPtr.Zero
            }
        }
    };

    /// <summary>The keys that need the extended flag to be read as themselves.</summary>
    private static bool IsExtended(ushort key) => key is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E;

    private static void Send(Span<Input> inputs)
    {
        var sent = SendInput((uint)inputs.Length, ref inputs[0], Marshal.SizeOf<Input>());

        if (sent != inputs.Length)
        {
            // Blocked, which on Windows means one thing: something at a higher integrity level has
            // the foreground. A game running as administrator while this does not would do it.
            throw new InvalidOperationException(
                "Windows refused the input. This usually means the foreground window belongs to a "
                + "process running at a higher integrity level than d2vpn.");
        }
    }

    private static void Send(Input input)
    {
        Span<Input> one = [input];
        Send(one);
    }

    // ---- Win32 ----

    private const int InputMouse = 0;
    private const int InputKeyboard = 1;

    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;

    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInputData Mouse;

        [FieldOffset(0)]
        public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;

        // The keyboard struct is smaller than the mouse one, and the union is sized by the larger.
        // Without this the padding is left uninitialised, which SendInput rejects on some builds.
        private readonly int _pad1;
        private readonly int _pad2;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ref Input inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
