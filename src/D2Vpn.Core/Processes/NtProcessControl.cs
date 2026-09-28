using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace D2Vpn.Core.Processes;

/// <summary>
/// Suspends a process through <c>ntdll</c>'s whole-process primitives.
/// </summary>
/// <remarks>
/// <para>
/// <c>NtSuspendProcess</c> rather than walking a thread snapshot and calling
/// <c>SuspendThread</c> on each. The thread-by-thread approach races: a thread created between
/// taking the snapshot and finishing the walk is never suspended and keeps running inside an
/// otherwise frozen process, which is worse than not pausing at all. The whole-process call has
/// no such gap, and resuming is symmetrical rather than a matter of remembering exactly which
/// threads were suspended and how deep each suspend count went.
/// </para>
/// <para>
/// Both are undocumented exports. They have been present and stable since Windows XP and are what
/// every process-suspend tool uses, but they are not a contract Microsoft offers, so a missing
/// export is handled rather than assumed away.
/// </para>
/// <para>
/// The handle asks for <c>PROCESS_SUSPEND_RESUME</c> alone. .NET's <see cref="Process.Handle"/>
/// requests full access, which an anti-cheat is far more likely to refuse - and refusing is the
/// expected outcome here, not an error to be surprised by.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NtProcessControl : IProcessControl
{
    private const int ProcessSuspendResume = 0x0800;

    private readonly SafeProcessHandle _handle;
    private readonly Process _process;

    private NtProcessControl(Process process, SafeProcessHandle handle)
    {
        _process = process;
        _handle = handle;
        Name = process.ProcessName;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool IsAlive
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>The process id, for display.</summary>
    public int Id => _process.Id;

    /// <summary>
    /// Opens the named process for suspending, or throws explaining why it could not.
    /// </summary>
    /// <param name="processName">Process name without the extension, such as <c>destiny2</c>.</param>
    /// <exception cref="InvalidOperationException">
    /// The process is not running, or the handle was refused.
    /// </exception>
    public static NtProcessControl Open(string processName)
    {
        var matches = Process.GetProcessesByName(processName);

        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                $"no process named '{processName}' is running - start the game first");
        }

        // More than one match is not expected for a game, but picking the first silently would
        // hide a second copy that the pause then appears not to affect.
        var process = matches[0];

        foreach (var extra in matches.Skip(1))
        {
            extra.Dispose();
        }

        var handle = OpenProcess(ProcessSuspendResume, false, process.Id);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            process.Dispose();

            throw new InvalidOperationException(
                $"could not open '{processName}' (pid {process.Id}) for suspending - "
                + $"Win32 error {error}. Run as administrator; if that does not help, the "
                + "game's anti-cheat is refusing the handle and this cannot work.");
        }

        return new NtProcessControl(process, handle);
    }

    /// <inheritdoc/>
    public void Suspend() => Invoke(NtSuspendProcess, "suspend");

    /// <inheritdoc/>
    public void Resume() => Invoke(NtResumeProcess, "resume");

    private void Invoke(Func<SafeProcessHandle, int> call, string verb)
    {
        var status = call(_handle);

        // NTSTATUS, not a Win32 error: anything negative is a failure.
        if (status < 0)
        {
            throw new InvalidOperationException(
                $"could not {verb} {Name}: NTSTATUS 0x{status:X8}");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _handle.Dispose();
        _process.Dispose();
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(SafeProcessHandle process);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        int desiredAccess, bool inheritHandle, int processId);
}
