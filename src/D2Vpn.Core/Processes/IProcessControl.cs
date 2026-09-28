namespace D2Vpn.Core.Processes;

/// <summary>
/// Suspends and resumes one running process.
/// </summary>
/// <remarks>
/// Split from <see cref="GamePauseSession"/> so the policy - when to pause, when to force a
/// resume, what to count - can be tested without suspending anything real. The only production
/// implementation is <see cref="NtProcessControl"/>.
/// </remarks>
public interface IProcessControl : IDisposable
{
    /// <summary>The process name, for display.</summary>
    string Name { get; }

    /// <summary>False once the process has exited.</summary>
    bool IsAlive { get; }

    /// <summary>Stops every thread in the process.</summary>
    void Suspend();

    /// <summary>Lets the process run again.</summary>
    void Resume();
}
