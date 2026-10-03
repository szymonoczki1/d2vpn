using System.Runtime.Versioning;

using D2Vpn.Core.Processes;

namespace D2Vpn.Core.Hosting;

/// <summary>
/// Game pause as something the host can toggle, alongside the traffic modules.
/// </summary>
/// <remarks>
/// <para>
/// A controllable that opens no packet handle, which is why <see cref="IControllable"/> is separate
/// from <see cref="IModuleRunner"/>. Its <see cref="HandleKey"/> is null, so it never collides with
/// anything.
/// </para>
/// <para>
/// It still needs to run: the safety timeout that stops a pause outliving the operator's attention
/// is driven by <see cref="GamePauseSession.Tick"/>, and nothing else is running while the game is
/// suspended. <see cref="RunAsync"/> is that clock.
/// </para>
/// <para>
/// Releasing is the whole point. <see cref="ModuleHost.ReleaseAll"/> sets
/// <see cref="IsActive"/> false here like any other module, so the panic button unfreezes the game,
/// and disposal resumes it as well.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class GamePauseRunner : IModuleRunner, IControllable
{
    private readonly GamePauseSession _session;

    /// <summary>Wraps a pause session under a control-surface name.</summary>
    public GamePauseRunner(GamePauseSession session, string name = "pause")
    {
        _session = session;
        Name = name;
    }

    /// <inheritdoc cref="IControllable.Name"/>
    public string Name { get; }

    /// <inheritdoc/>
    public IReadOnlyList<IControllable> Controls => [this];

    /// <summary>Null: this opens no packet handle, so it collides with nothing.</summary>
    public string? HandleKey => null;

    /// <inheritdoc/>
    public ModuleKind Kind => ModuleKind.Process;

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc/>
    public bool IsActive
    {
        get => _session.IsPaused;
        set
        {
            if (value)
            {
                _session.Pause(DateTimeOffset.UtcNow);
            }
            else
            {
                _session.Release(DateTimeOffset.UtcNow, ResumeReason.Toggle);
            }
        }
    }

    /// <inheritdoc/>
    public DateTimeOffset? ActiveSince => _session.PausedAt;

    /// <inheritdoc/>
    public TimeSpan TotalActive => _session.TotalPaused + _session.Elapsed(DateTimeOffset.UtcNow);

    /// <summary>False: there is no connection here to reset.</summary>
    public bool SupportsReset => false;

    /// <summary>Null: neither buffering nor resync means anything to a suspended process.</summary>
    public IModuleOptions? Options => null;

    /// <summary>
    /// A reading, with every packet counter zero because this module handles none.
    /// </summary>
    /// <remarks>
    /// The pause count used to be reported here as packets seen, there being no other field for it,
    /// which put a number under the wrong word in every front end. It lives in
    /// <see cref="Metrics"/> now.
    /// </remarks>
    public ModuleStatus Status => new(
        Name, _session.IsPaused, IsEnabled, 0, 0, 0, 0, 0,
        _session.Elapsed(DateTimeOffset.UtcNow).TotalSeconds,
        TotalActive.TotalSeconds);

    /// <inheritdoc/>
    public IReadOnlyList<ModuleMetric> Metrics =>
    [
        new("pauses", $"{_session.Pauses}"),
        // The pause in progress counts towards both. The session banks a pause only when it is
        // released, so reading those raw showed "total held 0.0s" beside a timer reading 15.1s.
        new("total held", ModuleMetrics.Seconds(TotalActive)),
        new("longest", ModuleMetrics.Seconds(
            _session.Elapsed(DateTimeOffset.UtcNow) > _session.LongestPause
                ? _session.Elapsed(DateTimeOffset.UtcNow)
                : _session.LongestPause)),
        new("safety stops", $"{_session.SafetyStops}")
    ];

    /// <inheritdoc/>
    public void RequestReset() =>
        throw new InvalidOperationException(
            $"'{Name}' suspends the game process - there is no connection to reset");

    /// <inheritdoc/>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // The safety-timeout clock. Nothing else runs while the game is suspended, so if this loop
        // is not ticking, a pause the operator walks away from never ends.
        while (!cancellationToken.IsCancellationRequested)
        {
            _session.Tick(DateTimeOffset.UtcNow);

            try
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _session.Dispose();
}
