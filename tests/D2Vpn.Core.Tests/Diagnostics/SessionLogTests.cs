using System.Globalization;
using D2Vpn.Core.Diagnostics;

namespace D2Vpn.Core.Tests.Diagnostics;

/// <summary>
/// The log exists so a run can be lined up against a packet capture and the attempts that worked
/// compared against the ones that did not. That makes ordering, timestamps and durability the
/// things worth testing.
/// </summary>
public class SessionLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"d2vpn-{Guid.NewGuid():N}.tsv");

    [Fact]
    public void Events_are_written_in_order_with_offsets()
    {
        using (var log = new SessionLog(_path))
        {
            log.Write("hold");
            log.Write("release");
            log.Write("worked", "attempt 1");
        }

        var lines = File.ReadAllLines(_path);

        Assert.Equal("wall_clock\toffset_s\tevent\tdetail", lines[0]);
        Assert.Contains("start", lines[1]);
        Assert.Contains("hold", lines[2]);
        Assert.Contains("release", lines[3]);
        Assert.Contains("worked", lines[4]);
        Assert.Contains("attempt 1", lines[4]);
        Assert.Contains("end", lines[5]);
    }

    [Fact]
    public void Offsets_are_relative_to_the_start()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-10);

        using (var log = new SessionLog(_path, started))
        {
            log.Write("hold");
        }

        // Parsed invariant, as written. Under a locale using a comma as the decimal
        // separator, a current-culture parse rejects "10.005" outright.
        var offset = double.Parse(
            File.ReadAllLines(_path)[2].Split('\t')[1], CultureInfo.InvariantCulture);

        // Roughly ten seconds in, since the log was told it started ten seconds ago.
        Assert.InRange(offset, 9.0, 11.0);
    }

    [Fact]
    public void Events_are_readable_before_the_log_is_closed()
    {
        // A session worth investigating is one that might end badly - a disconnect, a crash, a
        // forced exit - and a buffered log would lose exactly the run worth having.
        using var log = new SessionLog(_path);
        log.Write("hold");

        using var reader = new StreamReader(
            new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

        Assert.Contains("hold", reader.ReadToEnd());
    }

    [Fact]
    public void Writing_from_several_threads_does_not_interleave_lines()
    {
        using (var log = new SessionLog(_path))
        {
            Parallel.For(0, 200, i => log.Write("mark", $"n{i}"));
        }

        var lines = File.ReadAllLines(_path);

        // Header, start, 200 marks, end - and every line intact.
        Assert.Equal(203, lines.Length);
        Assert.All(lines, line => Assert.Equal(3, line.Count(c => c == '\t')));
    }

    [Fact]
    public void The_event_count_matches_what_was_written()
    {
        using var log = new SessionLog(_path);
        log.Write("hold");
        log.Write("release");

        Assert.Equal(3, log.Count);   // start plus two
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
