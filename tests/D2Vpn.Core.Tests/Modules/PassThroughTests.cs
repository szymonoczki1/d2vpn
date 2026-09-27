using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// Reconnect watches and injects rather than blocking, and the connection has to be healthy right
/// up to the moment it is killed - so this module must never withhold anything.
/// </summary>
public class PassThroughTests
{
    private static PassThroughModule Module() =>
        new("Reconnect", DestinyPorts.Instance);

    [Fact]
    public void Everything_passes()
    {
        var module = Module();

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.TcpData(30000, 500, 1000)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.TcpAck(30000, 1000)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.TcpReset(30000)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 500)));
    }

    [Fact]
    public void It_is_never_active_however_it_is_set()
    {
        // There is no hold here, so there is no state for IsActive to describe. Anything reading
        // it - a status line, the buffer's release detection - should see a module at rest.
        var module = Module();

        Assert.False(module.IsActive);

        module.IsActive = true;

        Assert.False(module.IsActive);
    }

    [Fact]
    public void Resetting_it_changes_nothing()
    {
        var module = Module();
        module.Reset();

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.TcpData(30000, 500, 1000)));
    }
}
