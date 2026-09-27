using D2Vpn.Core.Interception;
using WindivertDotnet;

namespace D2Vpn.Core.Tests.Interception;

/// <summary>
/// A held TCP segment acknowledges what its sender had received when it was built, which is stale
/// by the time it is replayed. Three stale acknowledgements are the fast-retransmit signal, so
/// getting this wrong makes this machine re-send data the peer already has.
/// </summary>
/// <remarks>
/// Written against real packet bytes and read back through the parser, because the whole job is
/// mutating a header in place - asserting on anything else would test the intent rather than the
/// bytes that go out.
/// </remarks>
public class TcpAckRewriterTests
{
    private static PacketInfo Describe(WinDivertPacket packet, WinDivertAddress addr) =>
        WinDivertPacketSource.Describe(packet, addr)!.Value;

    [Fact]
    public void A_stale_acknowledgement_is_brought_forward()
    {
        var rewriter = new TcpAckRewriter();
        using var addr = RawPackets.Address(outbound: false);

        // The connection has moved on since this segment was captured.
        using var current = RawPackets.Tcp(acknowledgement: 5000);
        rewriter.Observe(Describe(current, addr));

        using var held = RawPackets.Tcp(acknowledgement: 1000);
        rewriter.Rewrite(held, addr);

        Assert.Equal(5000u, Describe(held, addr).AckNumber);
        Assert.Equal(1, rewriter.Rewritten);
    }

    [Fact]
    public void A_segment_already_current_is_left_alone()
    {
        var rewriter = new TcpAckRewriter();
        using var addr = RawPackets.Address(outbound: false);

        using var current = RawPackets.Tcp(acknowledgement: 1000);
        rewriter.Observe(Describe(current, addr));

        using var held = RawPackets.Tcp(acknowledgement: 1000);
        rewriter.Rewrite(held, addr);

        Assert.Equal(1000u, Describe(held, addr).AckNumber);
        Assert.Equal(0, rewriter.Rewritten);
        Assert.Equal(1, rewriter.LeftAlone);
    }

    [Fact]
    public void A_segment_newer_than_anything_seen_is_left_alone()
    {
        // Never move an acknowledgement backwards: that would claim the peer un-received data.
        var rewriter = new TcpAckRewriter();
        using var addr = RawPackets.Address(outbound: false);

        using var current = RawPackets.Tcp(acknowledgement: 1000);
        rewriter.Observe(Describe(current, addr));

        using var held = RawPackets.Tcp(acknowledgement: 9000);
        rewriter.Rewrite(held, addr);

        Assert.Equal(9000u, Describe(held, addr).AckNumber);
        Assert.Equal(0, rewriter.Rewritten);
    }

    [Fact]
    public void The_sequence_number_is_never_touched()
    {
        // It identifies the data being delivered, which is the entire point of replaying it - the
        // receiver is still waiting for exactly that number.
        var rewriter = new TcpAckRewriter();
        using var addr = RawPackets.Address(outbound: false);

        using var current = RawPackets.Tcp(acknowledgement: 5000);
        rewriter.Observe(Describe(current, addr));

        using var held = RawPackets.Tcp(sequence: 777, acknowledgement: 1000);
        rewriter.Rewrite(held, addr);

        Assert.Equal(777u, Describe(held, addr).SequenceNumber);
        Assert.Equal(5000u, Describe(held, addr).AckNumber);
    }

    [Fact]
    public void An_unknown_connection_is_left_alone()
    {
        var rewriter = new TcpAckRewriter();
        using var addr = RawPackets.Address(outbound: false);

        using var held = RawPackets.Tcp(acknowledgement: 1000);
        rewriter.Rewrite(held, addr);

        Assert.Equal(1000u, Describe(held, addr).AckNumber);
        Assert.Equal(1, rewriter.LeftAlone);
    }

    [Fact]
    public void Outbound_acknowledgements_are_not_tracked()
    {
        // An inbound segment's acknowledgement field is written by the peer, so only the peer's own
        // direction says what it would put there now. Tracking ours would rewrite a held segment to
        // claim the peer acknowledged something it never sent.
        var rewriter = new TcpAckRewriter();
        using var outbound = RawPackets.Address(outbound: true);
        using var inbound = RawPackets.Address(outbound: false);

        using var ours = RawPackets.Tcp(acknowledgement: 5000);
        rewriter.Observe(Describe(ours, outbound));

        using var held = RawPackets.Tcp(acknowledgement: 1000);
        rewriter.Rewrite(held, inbound);

        Assert.Equal(1000u, Describe(held, inbound).AckNumber);
        Assert.Equal(0, rewriter.Rewritten);
    }

    [Fact]
    public void Connections_are_tracked_separately()
    {
        // Two connections carry unrelated sequence spaces, so one's acknowledgement number says
        // nothing about the other's and must never be written into it.
        var rewriter = new TcpAckRewriter();
        using var addr = RawPackets.Address(outbound: false);

        using var other = RawPackets.Tcp(acknowledgement: 9000, localPort: 51999);
        rewriter.Observe(Describe(other, addr));

        using var held = RawPackets.Tcp(acknowledgement: 1000);
        rewriter.Rewrite(held, addr);

        Assert.Equal(1000u, Describe(held, addr).AckNumber);
        Assert.Equal(1, rewriter.LeftAlone);
    }

    [Fact]
    public void The_newest_wins_even_when_an_older_one_arrives_after_it()
    {
        // Out-of-order delivery is ordinary, so the newest value has to survive an older arrival.
        var rewriter = new TcpAckRewriter();
        using var addr = RawPackets.Address(outbound: false);

        using var newest = RawPackets.Tcp(acknowledgement: 5000);
        using var older = RawPackets.Tcp(acknowledgement: 4000);
        rewriter.Observe(Describe(newest, addr));
        rewriter.Observe(Describe(older, addr));

        using var held = RawPackets.Tcp(acknowledgement: 1000);
        rewriter.Rewrite(held, addr);

        Assert.Equal(5000u, Describe(held, addr).AckNumber);
    }

    [Fact]
    public void A_value_just_past_the_wrap_counts_as_newer()
    {
        // Sequence numbers are 32 bits and wrap. A plain greater-than is wrong here: 5 is
        // numerically tiny and genuinely newer than 0xFFFFFFFB. This fails once every 4 GB of a
        // connection, which is exactly the kind of bug that never reproduces.
        var rewriter = new TcpAckRewriter();
        using var addr = RawPackets.Address(outbound: false);

        using var beforeWrap = RawPackets.Tcp(acknowledgement: uint.MaxValue - 5);
        using var afterWrap = RawPackets.Tcp(acknowledgement: 5);
        rewriter.Observe(Describe(beforeWrap, addr));
        rewriter.Observe(Describe(afterWrap, addr));

        using var held = RawPackets.Tcp(acknowledgement: uint.MaxValue - 100);
        rewriter.Rewrite(held, addr);

        Assert.Equal(5u, Describe(held, addr).AckNumber);
    }

    [Fact]
    public void A_value_just_before_the_wrap_does_not_displace_one_past_it()
    {
        var rewriter = new TcpAckRewriter();
        using var addr = RawPackets.Address(outbound: false);

        using var afterWrap = RawPackets.Tcp(acknowledgement: 5);
        using var beforeWrap = RawPackets.Tcp(acknowledgement: uint.MaxValue - 5);
        rewriter.Observe(Describe(afterWrap, addr));
        rewriter.Observe(Describe(beforeWrap, addr));

        using var held = RawPackets.Tcp(acknowledgement: uint.MaxValue - 100);
        rewriter.Rewrite(held, addr);

        Assert.Equal(5u, Describe(held, addr).AckNumber);
    }

    [Fact]
    public void Udp_is_left_alone_entirely()
    {
        // The rewriter is wired for TCP groups only, but a packet with no acknowledgement field
        // must not be mangled if it ever reaches here.
        var rewriter = new TcpAckRewriter();
        using var addr = RawPackets.Address(outbound: false);

        using var packet = RawPackets.Udp();
        rewriter.Rewrite(packet, addr);

        var info = Describe(packet, addr);
        Assert.Null(info.AckNumber);
        Assert.Equal(0, rewriter.Rewritten);
    }

    [Fact]
    public void Clearing_forgets_every_connection()
    {
        var rewriter = new TcpAckRewriter();
        using var addr = RawPackets.Address(outbound: false);

        using var current = RawPackets.Tcp(acknowledgement: 5000);
        rewriter.Observe(Describe(current, addr));
        rewriter.Clear();

        using var held = RawPackets.Tcp(acknowledgement: 1000);
        rewriter.Rewrite(held, addr);

        Assert.Equal(1000u, Describe(held, addr).AckNumber);
    }
}
