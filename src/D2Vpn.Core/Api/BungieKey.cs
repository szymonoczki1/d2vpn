using D2Vpn.Core.Hosting;

namespace D2Vpn.Core.Api;

/// <summary>
/// Where the Bungie API key is kept, and why it is not in the config file.
/// </summary>
/// <remarks>
/// <para>
/// Its own file next to the config rather than a field inside it. The config is the thing people
/// paste into a thread when asking why a module will not start, and it is what a zip would most
/// plausibly ship with a sensible default set up - a key in there leaves by both routes without
/// anybody deciding to send it. One file to keep back is easier to remember than one field to
/// redact.
/// </para>
/// <para>
/// A key is free, instant and per person, from <c>bungie.net/developer</c>. There is deliberately
/// no fallback key compiled in: the prior art ships one in its source, and a shipped key is one
/// revocation away from breaking for everyone at once, with every user sharing its rate limit.
/// </para>
/// </remarks>
public static class BungieKey
{
    /// <summary>The file the key is read from.</summary>
    public static string Path => System.IO.Path.Combine(ConfigLocation.Folder, "bungie.key");

    /// <summary>The key, or null if there is none to read.</summary>
    /// <remarks>
    /// Trimmed, because a key copied from a web page arrives with a newline on it more often than
    /// not, and a header value with a stray newline fails in a way that does not mention newlines.
    /// </remarks>
    public static string? Read()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            var key = File.ReadAllText(Path).Trim();
            return key.Length == 0 ? null : key;
        }
        catch
        {
            // Unreadable is the same as absent as far as anything downstream is concerned: the
            // API half does not run, and the traffic timer carries on by itself.
            return null;
        }
    }

    /// <summary>Writes the key, creating the folder if needed.</summary>
    public static void Write(string key)
    {
        Directory.CreateDirectory(ConfigLocation.Folder);
        File.WriteAllText(Path, key.Trim());
    }

    /// <summary>
    /// A key with its middle removed, for showing on screen.
    /// </summary>
    /// <remarks>
    /// So the window can say which key it is using without putting the key itself somewhere a
    /// screenshot would carry it. Short input is replaced outright rather than partly shown -
    /// masking four of eight characters is not masking.
    /// </remarks>
    public static string Mask(string? key) => key is null or { Length: 0 }
        ? "none"
        : key.Length < 12
            ? new string('*', key.Length)
            : $"{key[..4]}{new string('*', 8)}{key[^4..]}";
}
