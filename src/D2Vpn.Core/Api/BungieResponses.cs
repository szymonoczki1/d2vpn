using System.Globalization;
using System.Text.Json;

namespace D2Vpn.Core.Api;

/// <summary>A Destiny account, as the API addresses it.</summary>
/// <param name="MembershipType">The platform code. Not the one you logged in with - see remarks.</param>
/// <param name="MembershipId">The account id, which is a number too large for an int.</param>
/// <param name="DisplayName">The name as the API spells it, for showing back.</param>
/// <remarks>
/// With cross save on, several platform memberships point at one account and only one of them
/// carries the data. Asking the wrong one returns a valid, empty profile - a failure that looks
/// exactly like a private one. <see cref="BungieResponses.ReadPlayers"/> picks the right one.
/// </remarks>
public readonly record struct BungiePlayer(int MembershipType, string MembershipId, string DisplayName);

/// <summary>What the API says the character is doing.</summary>
/// <param name="StartedAt">When the server says the current activity began.</param>
/// <param name="ActivityHash">The activity, or 0 when in orbit or a social space.</param>
public readonly record struct ActivitySnapshot(DateTimeOffset StartedAt, long ActivityHash)
{
    /// <summary>True when the character is in something rather than in orbit.</summary>
    public bool InActivity => ActivityHash != 0;
}

/// <summary>
/// Why a call did not produce an answer.
/// </summary>
/// <remarks>
/// Separated from the happy path because each one needs a different thing said to the person.
/// A private profile is not a bug to report, a bad key is not a network problem, and a name with
/// no match is a typo - telling them apart is most of the value of having the API at all.
/// </remarks>
public enum BungieFailure
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>The key was missing, malformed or rejected.</summary>
    Key,

    /// <summary>The name matched no Destiny account.</summary>
    NoSuchPlayer,

    /// <summary>The account is there, but its activity is not visible anonymously.</summary>
    Private,

    /// <summary>Asked for too much too quickly.</summary>
    Throttled,

    /// <summary>Everything else: no network, Bungie down, maintenance.</summary>
    Unavailable
}

/// <summary>
/// Reading Bungie's JSON, kept apart from the HTTP that fetches it.
/// </summary>
/// <remarks>
/// So the shapes can be tested against recorded responses without a key, a network or an account.
/// Every one of these has a case that only shows up against the live service - cross save, a
/// private profile, three characters with different last-played times - and a recorded body is the
/// only way to have those covered at all.
/// </remarks>
public static class BungieResponses
{
    /// <summary>Bungie's own code for success.</summary>
    private const int Success = 1;

    /// <summary>
    /// Reads the error envelope every response is wrapped in.
    /// </summary>
    /// <remarks>
    /// HTTP 200 with an error inside is the normal way this API reports failure, so the status
    /// code alone decides nothing.
    /// </remarks>
    public static BungieFailure ReadEnvelope(JsonElement root)
    {
        if (!root.TryGetProperty("ErrorCode", out var code))
        {
            return BungieFailure.Unavailable;
        }

        return code.GetInt32() switch
        {
            Success => BungieFailure.None,

            // 2101/2102 are the key being absent or not recognised; 5 is the platform being
            // switched off for maintenance, which is a different thing from being down.
            2101 or 2102 => BungieFailure.Key,
            51 or 52 => BungieFailure.Throttled,
            _ => BungieFailure.Unavailable
        };
    }

    /// <summary>
    /// Picks the membership that actually holds the data.
    /// </summary>
    /// <remarks>
    /// <c>crossSaveOverride</c> names the platform the account's data lives on, and is 0 when
    /// cross save is off. Any other value means every other membership in the list is a shell: it
    /// resolves, it returns 200, and its profile is empty. Choosing by position in the list
    /// happens to work until the person has played on two platforms.
    /// </remarks>
    public static BungiePlayer? ReadPlayers(JsonElement root)
    {
        if (!root.TryGetProperty("Response", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        BungiePlayer? fallback = null;

        foreach (var entry in list.EnumerateArray())
        {
            var type = entry.TryGetProperty("membershipType", out var t) ? t.GetInt32() : 0;
            var id = entry.TryGetProperty("membershipId", out var i) ? i.GetString() : null;

            if (id is null)
            {
                continue;
            }

            var name = entry.TryGetProperty("bungieGlobalDisplayName", out var n)
                ? n.GetString() ?? string.Empty
                : string.Empty;

            var code = entry.TryGetProperty("bungieGlobalDisplayNameCode", out var c)
                && c.ValueKind == JsonValueKind.Number
                    ? c.GetInt32()
                    : 0;

            var over = entry.TryGetProperty("crossSaveOverride", out var o) ? o.GetInt32() : 0;
            var player = new BungiePlayer(
                type, id, code > 0 ? $"{name}#{code:0000}" : name);

            // The one the override names is the real one, whatever order they came in.
            if (over == type)
            {
                return player;
            }

            // Cross save off: this membership holds its own data, but a later entry may still be
            // an override that outranks it.
            fallback ??= over == 0 ? player : null;
        }

        return fallback;
    }

    /// <summary>
    /// Reads the character activities component.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The character with the most recent start is the one being played. There is no "active
    /// character" in the response - all three are returned, each with whatever it was last doing -
    /// so the most recent is the only thing that distinguishes the one at the keyboard.
    /// </para>
    /// <para>
    /// A missing <c>data</c> is the privacy case, and it is not an error: the request succeeded
    /// and the component was withheld. That is what the <c>privacy</c> field beside it says, and
    /// it is the single most likely reason this half does nothing for somebody.
    /// </para>
    /// </remarks>
    public static (ActivitySnapshot? Snapshot, BungieFailure Failure) ReadActivity(JsonElement root)
    {
        if (!root.TryGetProperty("Response", out var response)
            || !response.TryGetProperty("characterActivities", out var activities))
        {
            return (null, BungieFailure.Private);
        }

        if (!activities.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            return (null, BungieFailure.Private);
        }

        ActivitySnapshot? latest = null;

        foreach (var character in data.EnumerateObject())
        {
            if (!character.Value.TryGetProperty("dateActivityStarted", out var started)
                || started.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!DateTimeOffset.TryParse(
                    started.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var at))
            {
                continue;
            }

            var hash = character.Value.TryGetProperty("currentActivityHash", out var h)
                && h.ValueKind == JsonValueKind.Number
                    ? h.GetInt64()
                    : 0;

            if (latest is null || at > latest.Value.StartedAt)
            {
                latest = new ActivitySnapshot(at, hash);
            }
        }

        // Visible, but with nothing in it. A profile that has never played rather than one being
        // withheld - no snapshot, and no failure to report either.
        return (latest, BungieFailure.None);
    }

    /// <summary>
    /// Splits <c>Name#1234</c> into its two halves.
    /// </summary>
    /// <remarks>
    /// The code is a fixed four digits and is part of the identity, not decoration: two people can
    /// hold the same name. Leading zeros matter, which is why it is parsed as a number only after
    /// the length is known to be right.
    /// </remarks>
    public static (string Name, int Code)? ParseBungieName(string? full)
    {
        if (string.IsNullOrWhiteSpace(full))
        {
            return null;
        }

        var at = full.LastIndexOf('#');

        if (at <= 0 || at == full.Length - 1)
        {
            return null;
        }

        var name = full[..at].Trim();
        var digits = full[(at + 1)..].Trim();

        if (name.Length == 0
            || digits.Length is < 1 or > 4
            || !digits.All(char.IsAsciiDigit)
            || !int.TryParse(digits, CultureInfo.InvariantCulture, out var code))
        {
            return null;
        }

        return (name, code);
    }
}
