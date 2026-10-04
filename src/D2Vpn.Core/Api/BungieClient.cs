using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace D2Vpn.Core.Api;

/// <summary>
/// The Bungie API, as much of it as the instance timer needs.
/// </summary>
/// <remarks>
/// <para>
/// Two calls: resolve a Bungie name to an account once, then ask what that account is doing. No
/// OAuth - both endpoints are public, which was checked against Bungie's own OpenAPI document
/// rather than assumed, and the activity component is then gated on the account's privacy
/// settings rather than on a token.
/// </para>
/// <para>
/// Nothing here throws for a failed call. The API is a correction to a timer that already works
/// without it, so every failure is a reason to carry on with the traffic figure - not something to
/// interrupt a session over.
/// </para>
/// </remarks>
public sealed class BungieClient : IDisposable
{
    /// <summary>Every path hangs off this.</summary>
    public const string Root = "https://www.bungie.net/Platform";

    private readonly HttpClient _http;
    private readonly bool _owned;

    /// <summary>Creates a client over its own connection.</summary>
    /// <param name="key">The API key, from bungie.net/developer.</param>
    public BungieClient(string key)
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, key, owned: true)
    {
    }

    /// <summary>Creates a client over a connection someone else owns, for tests.</summary>
    public BungieClient(HttpClient http, string key, bool owned = false)
    {
        _http = http;
        _owned = owned;

        _http.DefaultRequestHeaders.Remove("X-API-Key");
        _http.DefaultRequestHeaders.Add("X-API-Key", key.Trim());
    }

    /// <summary>The last thing that went wrong, for a front end to show.</summary>
    public BungieFailure LastFailure { get; private set; }

    /// <summary>
    /// Resolves <c>Name#1234</c> to the account that holds the data.
    /// </summary>
    /// <remarks>
    /// Membership type -1 is "search them all", which is what makes one call enough whatever
    /// platform the person plays on.
    /// </remarks>
    public async Task<BungiePlayer?> FindPlayerAsync(string bungieName, CancellationToken token)
    {
        if (BungieResponses.ParseBungieName(bungieName) is not { } parsed)
        {
            LastFailure = BungieFailure.NoSuchPlayer;
            return null;
        }

        var (name, code) = parsed;

        var body = new Dictionary<string, object>
        {
            ["displayName"] = name,
            ["displayNameCode"] = code
        };

        using var root = await PostAsync(
            $"{Root}/Destiny2/SearchDestinyPlayerByBungieName/-1/", body, token)
            .ConfigureAwait(false);

        if (root is null)
        {
            return null;
        }

        var player = BungieResponses.ReadPlayers(root.RootElement);

        if (player is null)
        {
            LastFailure = BungieFailure.NoSuchPlayer;
        }

        return player;
    }

    /// <summary>Asks what that account is doing now.</summary>
    public async Task<ActivitySnapshot?> GetCurrentActivityAsync(
        BungiePlayer player, CancellationToken token)
    {
        // 204 is CharacterActivities, which is the only component with a start time on it.
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{Root}/Destiny2/{player.MembershipType}/Profile/{player.MembershipId}/?components=204");

        using var root = await GetAsync(url, token).ConfigureAwait(false);

        if (root is null)
        {
            return null;
        }

        var (snapshot, failure) = BungieResponses.ReadActivity(root.RootElement);
        LastFailure = failure;

        return snapshot;
    }

    private Task<JsonDocument?> GetAsync(string url, CancellationToken token) =>
        SendAsync(() => _http.GetAsync(url, token), token);

    private Task<JsonDocument?> PostAsync(
        string url, object body, CancellationToken token) =>
        SendAsync(() => _http.PostAsJsonAsync(url, body, token), token);

    /// <summary>
    /// Sends, and turns anything that went wrong into a failure rather than an exception.
    /// </summary>
    /// <remarks>
    /// 200 with an error code inside is this API's normal way of saying no, so the status line is
    /// checked for the few cases it does carry - a rejected key, a throttle - and the envelope
    /// decides the rest.
    /// </remarks>
    private async Task<JsonDocument?> SendAsync(
        Func<Task<HttpResponseMessage>> send, CancellationToken token)
    {
        LastFailure = BungieFailure.None;

        try
        {
            using var response = await send().ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                LastFailure = BungieFailure.Key;
                return null;
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                LastFailure = BungieFailure.Throttled;
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var document = JsonDocument.Parse(json);
            var envelope = BungieResponses.ReadEnvelope(document.RootElement);

            if (envelope != BungieFailure.None)
            {
                LastFailure = envelope;
                document.Dispose();
                return null;
            }

            return document;
        }
        catch (OperationCanceledException)
        {
            // Shutting down, or the per-request timeout. Neither is worth reporting as a fault.
            LastFailure = BungieFailure.Unavailable;
            return null;
        }
        catch (Exception)
        {
            // No network, DNS gone, Bungie down, a body that is not JSON. All the same answer:
            // there is no correction this time, and the traffic timer carries on alone.
            LastFailure = BungieFailure.Unavailable;
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_owned)
        {
            _http.Dispose();
        }
    }
}
