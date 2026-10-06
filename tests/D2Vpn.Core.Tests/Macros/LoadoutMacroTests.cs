using D2Vpn.Core.Input;
using D2Vpn.Core.Macros;

namespace D2Vpn.Core.Tests.Macros;

/// <summary>
/// The loadout swap, against a recording input sink.
/// </summary>
/// <remarks>
/// The whole sequence is assertable without a game, a screen, or a pointer that moves while
/// somebody is using the machine - which also means a change to the order or the finishing click
/// is caught here rather than in the one place it matters, mid-activity.
/// </remarks>
public class LoadoutMacroTests
{
    private const ushort F1 = 0x70;
    private const ushort Left = 0x25;

    /// <summary>
    /// Writes down what it was asked to do, in order, and keeps a clock that only moves when the
    /// macro waits.
    /// </summary>
    /// <remarks>
    /// The clock is the point. With a no-op wait and a real stopwatch the duration is decided by
    /// how fast the machine runs the loop, which is a test that passes on one box and not another.
    /// Here a one-millisecond run is exactly one click, every time.
    /// </remarks>
    private sealed class Recorder : IInputSink, IMacroClock
    {
        public List<string> Events { get; } = [];

        public List<TimeSpan> Waits { get; } = [];

        public void MoveTo(int x, int y) => Events.Add($"move {x},{y}");

        public void ClickAt(int x, int y) => Events.Add($"click {x},{y}");

        public void PressKey(ushort key) => Events.Add($"key {key:X2}");

        public TimeSpan Elapsed { get; private set; }

        public void Restart() => Elapsed = TimeSpan.Zero;

        public bool Wait(TimeSpan duration, CancellationToken token)
        {
            Waits.Add(duration);
            Elapsed += duration;
            return !token.IsCancellationRequested;
        }
    }

    private static readonly LoadoutGrid Grid = new();

    private static MacroPlan Plan(
        IReadOnlyList<int> order,
        int? endOn = null,
        TimeSpan? duration = null,
        bool closeAfter = false,
        TimeSpan? settle = null,
        int endClicks = 1) =>
        new(
            Grid,
            order,
            endOn,
            duration ?? TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(120),
            closeAfter,
            settle ?? TimeSpan.FromMilliseconds(50),
            endClicks);

    // ---- the grid ----

    [Theory]
    [InlineData(1, 117, 385)]
    [InlineData(2, 213, 385)]
    [InlineData(4, 405, 385)]
    [InlineData(5, 117, 481)]
    [InlineData(20, 405, 769)]
    public void A_slot_is_where_it_was_measured(int slot, int x, int y)
    {
        // Slots 1, 2 and 5 are the measured ones; 4 and 20 are the row ends, which is where an
        // off-by-one in the wrapping would show first.
        Assert.Equal((x, y), Grid.PointFor(slot));
    }

    [Fact]
    public void The_grid_holds_twenty()
    {
        Assert.Equal(20, Grid.Count);
        Assert.True(Grid.Holds(20));
        Assert.False(Grid.Holds(21));
        Assert.False(Grid.Holds(0));
    }

    [Fact]
    public void A_slot_off_the_grid_is_refused_rather_than_clicked_somewhere()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Grid.PointFor(21));
    }

    // ---- the sequence ----

    [Fact]
    public void It_opens_the_character_screen_before_clicking_anything()
    {
        // F1, then left. A click sent before the screen is up goes to whatever was underneath it.
        var sink = new Recorder();
        new LoadoutMacro(sink, sink).Run(Plan([1], endOn: null), CancellationToken.None);

        Assert.Equal($"key {F1:X2}", sink.Events[0]);
        Assert.Equal($"key {Left:X2}", sink.Events[1]);
        Assert.StartsWith("click", sink.Events[2], StringComparison.Ordinal);
    }

    [Fact]
    public void It_waits_for_the_screen_and_then_for_the_page()
    {
        var sink = new Recorder();
        new LoadoutMacro(sink, sink).Run(Plan([1]), CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(400), sink.Waits[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(120), sink.Waits[1]);
    }

    [Fact]
    public void It_cycles_the_order_given()
    {
        // Long enough to come round twice, so the order is shown repeating rather than just being
        // walked once.
        var sink = new Recorder();
        var macro = new LoadoutMacro(sink, sink);

        // 30 ms between clicks and 50 ms of duration is two clicks; the third and fourth come
        // from the order repeating on a longer run.
        macro.Run(
            Plan([5, 2, 9], endOn: null, duration: TimeSpan.FromMilliseconds(200)),
            CancellationToken.None);

        var clicks = sink.Events.Where(e => e.StartsWith("click", StringComparison.Ordinal))
            .Take(4)
            .ToList();

        Assert.Equal("click 117,481", clicks[0]);
        Assert.Equal("click 213,385", clicks[1]);
        Assert.Equal("click 117,577", clicks[2]);
        Assert.Equal("click 117,481", clicks[3]);
    }

    [Fact]
    public void It_finishes_on_the_loadout_asked_for()
    {
        // The reason this matters: stopping mid-cycle leaves whichever loadout the clock landed
        // on, which for a swap done to desync is the difference between ending armed and ending
        // on whatever was third in the list.
        var sink = new Recorder();
        var macro = new LoadoutMacro(sink, sink);

        macro.Run(
            Plan([5, 2, 9], endOn: 1, duration: TimeSpan.FromMilliseconds(200)),
            CancellationToken.None);

        Assert.Equal("click 117,385", sink.Events[^1]);
    }

    [Fact]
    public void Stopping_early_still_finishes_on_the_right_loadout()
    {
        // A stop asks the cycling to end, not the loadout to be left wherever it got to.
        var sink = new Recorder();
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        new LoadoutMacro(sink, sink).Run(Plan([5, 2], endOn: 3), stop.Token);

        Assert.Equal("click 309,385", sink.Events[^1]);
    }

    [Fact]
    public void No_finishing_loadout_means_it_stops_where_it_is()
    {
        var sink = new Recorder();
        var macro = new LoadoutMacro(sink, sink);

        macro.Run(
            Plan([5, 2], endOn: null, duration: TimeSpan.FromMilliseconds(1)),
            CancellationToken.None);

        Assert.Equal("click 117,481", sink.Events[^1]);
        Assert.Equal(1, macro.Swaps);
    }

    // ---- what it refuses ----

    [Fact]
    public void It_closes_the_character_screen_when_asked()
    {
        // The same key that opened it, after the finishing click - so the game has been given the
        // swap before the screen it was made on goes away.
        var sink = new Recorder();

        new LoadoutMacro(sink, sink).Run(
            Plan([5, 2], endOn: 1, closeAfter: true), CancellationToken.None);

        Assert.Equal($"key {F1:X2}", sink.Events[^1]);
        Assert.Equal("click 117,385", sink.Events[^2]);
    }

    [Fact]
    public void The_finishing_loadout_is_clicked_as_many_times_as_asked()
    {
        // Redundancy rather than patience: waiting longer before the click that decides the
        // outcome does not help if the click itself is what goes missing.
        var sink = new Recorder();

        new LoadoutMacro(sink, sink).Run(
            Plan([5, 2], endOn: 1, endClicks: 3, settle: TimeSpan.FromMilliseconds(40)),
            CancellationToken.None);

        Assert.Equal(
            ["click 117,385", "click 117,385", "click 117,385"],
            sink.Events.TakeLast(3));
    }

    [Fact]
    public void Each_finishing_click_gets_a_settle_of_its_own()
    {
        // What makes a repeat a retry. Three clicks in the same frame are one event, so the gap
        // between them is the whole point of there being three.
        var sink = new Recorder();

        new LoadoutMacro(sink, sink).Run(
            Plan(
                [5, 2],
                endOn: 1,
                closeAfter: true,
                endClicks: 2,
                settle: TimeSpan.FromMilliseconds(40)),
            CancellationToken.None);

        // Settle, click, settle, click, settle, close.
        Assert.Equal(
            [TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40),
             TimeSpan.FromMilliseconds(40)],
            sink.Waits.TakeLast(3));

        Assert.Equal($"key {F1:X2}", sink.Events[^1]);
        Assert.Equal("click 117,385", sink.Events[^2]);
        Assert.Equal("click 117,385", sink.Events[^3]);
    }

    [Fact]
    public void The_screen_stays_up_for_its_own_settle_not_a_click_gap()
    {
        // The bug this exists for: a loadout applies over several frames, so closing straight
        // after the click lost the last swap - the one that decides what you are left wearing.
        // The click gap can be single milliseconds precisely because those swaps do not have to
        // survive the screen going away.
        var sink = new Recorder();

        new LoadoutMacro(sink, sink).Run(
            Plan([5, 2], endOn: 1, closeAfter: true, settle: TimeSpan.FromMilliseconds(80)),
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(80), sink.Waits[^1]);
        Assert.Equal($"key {F1:X2}", sink.Events[^1]);
    }

    [Fact]
    public void It_closes_the_screen_on_a_run_that_was_stopped()
    {
        // An inventory left open sends the next key press to a menu rather than to the game, which
        // is a worse place to be than before the macro ran.
        var sink = new Recorder();
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        new LoadoutMacro(sink, sink).Run(
            Plan([5, 2], endOn: 1, closeAfter: true), stop.Token);

        Assert.Equal($"key {F1:X2}", sink.Events[^1]);
    }

    [Fact]
    public void It_leaves_the_screen_open_when_not_asked()
    {
        var sink = new Recorder();

        new LoadoutMacro(sink, sink).Run(
            Plan([5, 2], endOn: 1, closeAfter: false), CancellationToken.None);

        Assert.StartsWith("click", sink.Events[^1], StringComparison.Ordinal);

        // Exactly one F1, the one that opened it.
        Assert.Single(sink.Events, e => e == $"key {F1:X2}");
    }

    [Fact]
    public void An_empty_order_is_refused()
    {
        var sink = new Recorder();

        var error = Assert.Throws<ArgumentException>(() =>
            new LoadoutMacro(sink, sink).Run(Plan([]), CancellationToken.None));

        Assert.Contains("no loadouts", error.Message, StringComparison.Ordinal);

        // And nothing was sent. A refused plan must not leave the character screen open.
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void A_slot_not_on_the_grid_is_refused_by_name()
    {
        var sink = new Recorder();

        var error = Assert.Throws<ArgumentException>(() =>
            new LoadoutMacro(sink, sink).Run(Plan([1, 44]), CancellationToken.None));

        Assert.Contains("44", error.Message, StringComparison.Ordinal);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void A_finishing_slot_not_on_the_grid_is_refused()
    {
        var sink = new Recorder();

        Assert.Throws<ArgumentException>(() =>
            new LoadoutMacro(sink, sink).Run(Plan([1], endOn: 99), CancellationToken.None));
    }

    [Fact]
    public void A_macro_with_no_duration_is_refused()
    {
        var sink = new Recorder();

        var error = Assert.Throws<ArgumentException>(() =>
            new LoadoutMacro(sink, sink)
                .Run(Plan([1], duration: TimeSpan.Zero), CancellationToken.None));

        Assert.Contains("duration", error.Message, StringComparison.Ordinal);
    }

    // ---- a re-measured screen ----

    [Fact]
    public void Re_measuring_the_grid_moves_every_slot()
    {
        // Why a corner and a pitch rather than twenty pairs: a different resolution is two numbers
        // to change, not forty.
        var grid = Grid with { OriginX = 200, OriginY = 500, Pitch = 128 };

        Assert.Equal((200, 500), grid.PointFor(1));
        Assert.Equal((328, 500), grid.PointFor(2));
        Assert.Equal((200, 628), grid.PointFor(5));
    }
}
