using D2Vpn.Core.Network;

namespace D2Vpn.Core.Api;

/// <summary>
/// Asks the API when the current activity really started, and corrects the traffic timer.
/// </summary>
/// <remarks>
/// <para>
/// The traffic half already knows <em>that</em> the instance changed, the moment it changes. What
/// it cannot know is when the activity began on the server, because it only sees the connection,
/// which is a second or two later. That one number is all this is for.
/// </para>
/// <para>
/// So it polls on demand rather than continuously: a change starts a short burst of attempts, and
/// the first one that lands stops it until the next change. An activity that is twenty minutes
/// long costs a handful of calls, not eighty. The API's current activity also lags by a few
/// seconds after entering somewhere - it still describes the last place - which is why the burst
/// retries instead of accepting the first answer.
/// </para>
/// <para>
/// Nothing here is required for the timer to work. Every failure leaves the traffic figure
/// standing, which is the whole reason this is a correction and not a source.
/// </para>
/// </remarks>
public sealed class BungieActivityWatcher : IDisposable
{
    private readonly InstanceWatcher _watcher;
    private readonly BungieClient _client;
    private readonly string _bungieName;
    private readonly TimeSpan _interval;
    private readonly int _attempts;

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _gate = new();

    private BungiePlayer? _player;
    private Task? _loop;

    /// <summary>Creates a corrector over a watcher.</summary>
    /// <param name="watcher">The traffic timer to correct.</param>
    /// <param name="client">An authenticated client. Disposed with this.</param>
    /// <param name="bungieName">The account to follow, as <c>Name#1234</c>.</param>
    /// <param name="interval">How long to wait between attempts within one burst.</param>
    /// <param name="attempts">How many attempts a burst makes before giving up on an instance.</param>
    public BungieActivityWatcher(
        InstanceWatcher watcher,
        BungieClient client,
        string bungieName,
        TimeSpan? interval = null,
        int attempts = 6)
    {
        _watcher = watcher;
        _client = client;
        _bungieName = bungieName;

        // Bungie asks for roughly a request a second across an application, and this is one of
        // several things that might be talking to it. Fifteen seconds between attempts is what the
        // prior art settled on for the same call.
        _interval = interval ?? TimeSpan.FromSeconds(15);
        _attempts = attempts;
    }

    /// <summary>What went wrong last, for a front end to show.</summary>
    public BungieFailure LastFailure { get; private set; }

    /// <summary>The account being followed, once it has been resolved.</summary>
    public BungiePlayer? Player
    {
        get
        {
            lock (_gate)
            {
                return _player;
            }
        }
    }

    /// <summary>True once a name has resolved to an account.</summary>
    public bool IsResolved => Player is not null;

    /// <summary>Starts following the watcher.</summary>
    public void Start()
    {
        _watcher.Changed += OnInstanceChanged;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>A change means there is a new start time worth asking about.</summary>
    private void OnInstanceChanged(InstanceChange change)
    {
        lock (_gate)
        {
            _pending = _attempts;
        }

        // Released rather than awaited: this runs on the packet pump, and nothing on that thread
        // may wait on a web request.
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already awake with work queued. Two changes in quick succession need one wake.
        }
    }

    private int _pending;

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!token.IsCancellationRequested && Take())
            {
                if (await TryCorrectAsync(token).ConfigureAwait(false))
                {
                    // Got the server's own time. Nothing more to learn until the next change.
                    lock (_gate)
                    {
                        _pending = 0;
                    }

                    break;
                }

                try
                {
                    await Task.Delay(_interval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Takes one attempt off the burst, if there is one left.</summary>
    private bool Take()
    {
        lock (_gate)
        {
            if (_pending <= 0)
            {
                return false;
            }

            _pending--;
            return true;
        }
    }

    /// <summary>One attempt: resolve the account if needed, then ask and correct.</summary>
    private async Task<bool> TryCorrectAsync(CancellationToken token)
    {
        // Resolved once and kept. The name does not change, and this costs a call.
        if (Player is null)
        {
            var found = await _client.FindPlayerAsync(_bungieName, token).ConfigureAwait(false);
            LastFailure = _client.LastFailure;

            if (found is null)
            {
                return false;
            }

            lock (_gate)
            {
                _player = found;
            }
        }

        var snapshot = await _client
            .GetCurrentActivityAsync(Player!.Value, token)
            .ConfigureAwait(false);

        LastFailure = _client.LastFailure;

        // In orbit, the start time is the last activity's. Correcting from it would put the timer
        // back into something already over.
        if (snapshot is not { InActivity: true } activity)
        {
            return false;
        }

        return _watcher.Correct(activity.StartedAt);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _watcher.Changed -= OnInstanceChanged;

        _stop.Cancel();

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Shutting down. A web request that will not come back is not worth holding the
            // window closed for.
        }

        _stop.Dispose();
        _wake.Dispose();
        _client.Dispose();
    }
}
