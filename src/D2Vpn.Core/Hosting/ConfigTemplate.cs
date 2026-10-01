namespace D2Vpn.Core.Hosting;

/// <summary>
/// The commented config file <c>d2vpn config init</c> writes.
/// </summary>
/// <remarks>
/// A string rather than a serialised <c>HostConfig</c>, because the comments are most of the value
/// and a JSON writer cannot emit them. The reader allows comments and trailing commas for exactly
/// this reason.
/// </remarks>
public static class ConfigTemplate
{
    /// <summary>The template text.</summary>
    public const string Text = """
    {
      // d2vpn host configuration.
      //
      // Comments and trailing commas are allowed. Anything omitted keeps the value measured in
      // docs/protocol.md - so only write a number here if you have deliberately changed it, and a
      // corrected calibration will still reach you.
      //
      // Run it with:   d2vpn host --config d2vpn.json
      // Keys are polled in this process; macros can also drive the pipe directly.

      "pipe": "d2vpn",
      "pollMs": 1,

      // Every module you want hosted. Several on one port group is fine: they share that group's
      // packet handle as slots behind it, each with its own buffer and its own release.
      //
      // Names: 3074-ul 3074-dl 3074-full (+ -slow), 27k-ul 27k-dl 27k-full (+ -slow),
      //        30k (+ -slow), 7500. TCP groups take no direction.
      //
      // resync - the resync window on 3074/27k, the keepalive on 30k. Changeable while running.
      // buffer - whether withheld packets are kept for replay. A slow module cannot turn it off,
      //          since a packet cannot arrive late unless it was kept.
      // bind   - leave it out, or set it to "" or null, for a module with no key. It is still
      //          hosted and still reachable over the pipe. Delete the whole entry to not host it.
      "modules": [
        { "name": "3074-ul",      "bind": "capslock", "resync": true, "buffer": true },
        { "name": "3074-dl",      "bind": "mouse4",   "resync": true, "buffer": true },

        // Slow is a rate cap on UDP: one packet per peer per slowMs.
        { "name": "3074-ul-slow", "bind": "f1", "slowMs": 1000 },
        { "name": "3074-dl-slow", "bind": "f2", "slowMs": 1000 },

        { "name": "27k-ul", "bind": "f3", "resync": true, "buffer": true },
        { "name": "27k-dl", "bind": "f4", "resync": true, "buffer": true },

        { "name": "7500", "bind": "f5", "buffer": true },

        // 30k keepalive is the 73-byte heartbeat. Without it a hold dies in about twenty seconds.
        { "name": "30k", "bind": "f6", "resync": true, "buffer": true },

        // The reference states 6 s; 8 s measured better here. This is the kind of thing the file
        // exists to record.
        { "name": "30k-slow", "bind": "f7", "resync": true, "slowMs": 8000 }
      ],

      // Suspends the game process. Nothing is sent, received, drawn or simulated while it is held.
      // safetyTimeoutS ends a pause automatically - on by default, unlike every traffic hold,
      // because a suspend that goes wrong leaves a process that will never run again. 0 for none.
      "pause": {
        "enabled": true,
        "bind": "f10",
        "process": "destiny2",
        "safetyTimeoutS": 30
      },

      // Releases every module at once, including pause. Worth binding somewhere easy to hit.
      "releaseBind": "f12",

      // Reconnect. It is a capability of the TCP modules rather than a module of its own, because
      // the sampler it needs has to share the hold's packet handle.
      "resetBinds": [
        { "module": "30k", "bind": "f9" }
      ]
    }
    """;
}
