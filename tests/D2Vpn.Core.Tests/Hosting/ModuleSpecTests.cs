using D2Vpn.Core.Hosting;
using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Hosting;

/// <summary>
/// Spec parsing, which is also a contract: a macro binds to the canonical name this produces.
/// </summary>
public class ModuleSpecTests
{
    [Theory]
    [InlineData("3074", "3074-ul", HoldDirection.Uplink, false)]
    [InlineData("3074-ul", "3074-ul", HoldDirection.Uplink, false)]
    [InlineData("3074-dl", "3074-dl", HoldDirection.Downlink, false)]
    [InlineData("3074-full", "3074-full", HoldDirection.Both, false)]
    [InlineData("3074-ul-slow", "3074-ul-slow", HoldDirection.Uplink, true)]
    [InlineData("27k-dl", "27k-dl", HoldDirection.Downlink, false)]
    [InlineData("27k-full", "27k-full", HoldDirection.Both, false)]
    public void Udp_specs_parse_to_a_canonical_name(
        string text, string name, HoldDirection direction, bool slow)
    {
        var spec = ModuleSpec.Parse(text);

        Assert.Equal(name, spec.Name);
        Assert.Equal(direction, spec.Direction);
        Assert.Equal(slow, spec.Slow);
    }

    [Theory]
    [InlineData("30k", "30k", false)]
    [InlineData("30k-slow", "30k-slow", true)]
    [InlineData("7500", "7500", false)]
    public void Tcp_specs_carry_no_direction_in_their_name(string text, string name, bool slow)
    {
        var spec = ModuleSpec.Parse(text);

        Assert.Equal(name, spec.Name);
        Assert.Equal(slow, spec.Slow);
        Assert.Equal(PortProtocol.Tcp, spec.Group.Protocol);
    }

    [Theory]
    [InlineData("up")]
    [InlineData("uplink")]
    public void Direction_aliases_are_accepted(string alias)
    {
        Assert.Equal("3074-ul", ModuleSpec.Parse($"3074-{alias}").Name);
    }

    [Fact]
    public void Parsing_is_case_insensitive()
    {
        Assert.Equal("27k-full", ModuleSpec.Parse("27K-FULL").Name);
    }

    [Fact]
    public void A_direction_on_a_tcp_group_is_rejected()
    {
        // Saying one is a misunderstanding of what the module does, and quietly ignoring it would
        // leave the operator believing they had an uplink hold on a port that has none.
        var ex = Assert.Throws<ArgumentException>(() => ModuleSpec.Parse("30k-ul"));

        Assert.Contains("takes no direction", ex.Message);
    }

    [Fact]
    public void An_unknown_group_lists_the_known_ones()
    {
        var ex = Assert.Throws<ArgumentException>(() => ModuleSpec.Parse("8080"));

        Assert.Contains("3074", ex.Message);
        Assert.Contains("27k", ex.Message);
    }

    [Fact]
    public void An_unknown_suffix_says_what_is_allowed()
    {
        var ex = Assert.Throws<ArgumentException>(() => ModuleSpec.Parse("3074-sideways"));

        Assert.Contains("ul, dl, full, or slow", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_spec_is_rejected(string text)
    {
        Assert.Throws<ArgumentException>(() => ModuleSpec.Parse(text));
    }

    [Theory]
    [InlineData("rc")]
    [InlineData("reconnect")]
    public void Reconnect_explains_that_it_is_a_capability(string text)
    {
        var ex = Assert.Throws<ArgumentException>(() => ModuleSpec.Parse(text));

        Assert.Contains("reset 30k", ex.Message);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("gamepause")]
    public void Pause_explains_that_it_is_its_own_command(string text)
    {
        var ex = Assert.Throws<ArgumentException>(() => ModuleSpec.Parse(text));

        Assert.Contains("d2vpn pause", ex.Message);
    }

    [Fact]
    public void Two_modules_on_one_group_conflict()
    {
        var specs = new[] { ModuleSpec.Parse("3074-ul"), ModuleSpec.Parse("3074-dl") };

        var ex = Assert.Throws<ArgumentException>(() => ModuleSpec.EnsureNoConflicts(specs));

        Assert.Contains("3074-ul", ex.Message);
        Assert.Contains("3074-full", ex.Message);
    }

    [Fact]
    public void A_tcp_conflict_does_not_suggest_a_full_variant()
    {
        // 30k is inbound-only and has no combined form, so pointing at one would send the operator
        // after a module that does not exist.
        var specs = new[] { ModuleSpec.Parse("30k"), ModuleSpec.Parse("30k-slow") };

        var ex = Assert.Throws<ArgumentException>(() => ModuleSpec.EnsureNoConflicts(specs));

        Assert.DoesNotContain("-full", ex.Message);
    }

    [Fact]
    public void Modules_on_different_groups_do_not_conflict()
    {
        var specs = new[]
        {
            ModuleSpec.Parse("3074-ul"),
            ModuleSpec.Parse("27k-ul"),
            ModuleSpec.Parse("30k"),
            ModuleSpec.Parse("7500")
        };

        ModuleSpec.EnsureNoConflicts(specs);
    }

    [Fact]
    public void A_combination_with_no_measured_defaults_is_refused()
    {
        // 30k is only calibrated inbound, and the TCP path rejects a direction outright. The
        // guard exists for a UDP group that gains a direction before it gains defaults.
        foreach (var group in DestinyPorts.All.Where(g => g.Protocol == PortProtocol.Udp))
        {
            foreach (var direction in new[]
                     { HoldDirection.Uplink, HoldDirection.Downlink, HoldDirection.Both })
            {
                var suffix = direction switch
                {
                    HoldDirection.Uplink => "ul",
                    HoldDirection.Downlink => "dl",
                    _ => "full"
                };

                var expected = MeasuredDefaults.For(group, direction) is not null;
                var parsed = Record.Exception(
                    () => ModuleSpec.Parse($"{group.Name}-{suffix}")) is null;

                Assert.Equal(expected, parsed);
            }
        }
    }
}
