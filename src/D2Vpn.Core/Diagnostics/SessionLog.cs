using System.Globalization;

namespace D2Vpn.Core.Diagnostics;

/// <summary>
/// What happened and when, written to a file so it can be lined up against a packet capture.
/// </summary>
/// <remarks>
/// <para>
/// For the case where a trick works sometimes and there is no visible pattern. One unlabelled run
/// cannot show why: the traffic looks much the same whether the attempt succeeded or not, and
/// without knowing which attempts were which there is nothing to compare. Marking each outcome as
/// it happens turns a recording into labelled trials, and then the successes and the failures can
/// be diffed.
/// </para>
/// <para>
/// Timestamps are absolute so they align with a capture taken alongside, and relative so a run can
/// be read on its own.
/// </para>
/// </remarks>
public sealed class SessionLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly DateTimeOffset _started;
    private readonly Lock _gate = new();

    /// <summary>Where the log is being written.</summary>
    public string Path { get; }

    /// <summary>Events recorded so far.</summary>
    public int Count { get; private set; }

    /// <summary>Opens a log, replacing any file already there.</summary>
    public SessionLog(string path, DateTimeOffset? started = null)
    {
        Path = path;
        _started = started ?? DateTimeOffset.UtcNow;
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
        _writer.WriteLine("wall_clock\toffset_s\tevent\tdetail");
        Write("start", _started.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>Records an event.</summary>
    /// <remarks>
    /// Flushed on every write. A session being investigated is one that might end badly - a
    /// disconnect, a crash, a forced exit - and a buffered log would lose exactly the run worth
    /// having.
    /// </remarks>
    public void Write(string name, string detail = "")
    {
        var now = DateTimeOffset.UtcNow;
        var offset = (now - _started).TotalSeconds;

        lock (_gate)
        {
            _writer.WriteLine(
                $"{now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}\t"
                + $"{offset.ToString("F3", CultureInfo.InvariantCulture)}\t{name}\t{detail}");

            Count++;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Write("end");
        _writer.Dispose();
    }
}
