using D2Vpn.Core.Hosting;

namespace D2Vpn.Core.Tests.Hosting;

/// <summary>
/// The config file, which is the one place binds live.
/// </summary>
/// <remarks>
/// Validation carries most of these tests. A config is hand-edited, and the failures worth catching
/// are the silent ones - a key bound to two things, where whichever registered second simply
/// replaces the first, and a module name typo that would otherwise surface as a toggle that never
/// appears.
/// </remarks>
public class HostConfigTests
{
    [Fact]
    public void A_minimal_config_takes_the_measured_defaults()
    {
        var config = HostConfig.Parse("""
        { "modules": [ { "name": "3074-ul" } ] }
        """);

        Assert.Equal("d2vpn", config.Pipe);
        Assert.Equal(1, config.PollMs);
        Assert.False(config.Pause.Enabled);

        var (spec, tuning) = config.Resolve()[0];

        Assert.Equal("3074-ul", spec.Name);
        Assert.True(tuning.Resync);
        Assert.True(tuning.Buffer);

        // Null means "leave the calibration alone", so a corrected measurement still reaches a
        // session that did not deliberately override it.
        Assert.Null(tuning.SlowMs);
        Assert.Null(tuning.ResyncCooldownS);
        Assert.Null(tuning.BufferPackets);
        Assert.Null(tuning.BufferAgeS);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        // The file is hand-edited and the template is mostly comments, so the reader has to accept
        // both even though a writer cannot emit them.
        var config = HostConfig.Parse("""
        {
          // which pipe
          "pipe": "custom",
          "modules": [
            { "name": "30k", "bind": "f6" },
          ],
        }
        """);

        Assert.Equal("custom", config.Pipe);
        Assert.Single(config.Modules);
    }

    [Fact]
    public void Every_tunable_round_trips()
    {
        var config = HostConfig.Parse("""
        {
          "modules": [
            {
              "name": "30k-slow",
              "bind": "f7",
              "resync": false,
              "buffer": true,
              "slowMs": 8000,
              "resyncCooldownS": 9,
              "bufferPackets": 512,
              "bufferAgeS": 45
            }
          ]
        }
        """);

        var tuning = config.Resolve()[0].Tuning;

        Assert.False(tuning.Resync);
        Assert.Equal(8000, tuning.SlowMs);
        Assert.Equal(9, tuning.ResyncCooldownS);
        Assert.Equal(512, tuning.BufferPackets);
        Assert.Equal(45, tuning.BufferAgeS);
    }

    [Fact]
    public void Names_are_canonicalised_through_the_spec()
    {
        var config = HostConfig.Parse("""
        { "modules": [ { "name": "3074" }, { "name": "27K-FULL" } ] }
        """);

        Assert.Equal(["3074-ul", "27k-full"], config.Resolve().Select(m => m.Spec.Name));
    }

    [Fact]
    public void Several_modules_on_one_group_are_accepted()
    {
        var config = HostConfig.Parse("""
        {
          "modules": [
            { "name": "3074-ul" }, { "name": "3074-dl" },
            { "name": "3074-ul-slow" }, { "name": "3074-dl-slow" }
          ]
        }
        """);

        Assert.Equal(4, config.Resolve().Count);
    }

    [Fact]
    public void A_module_can_be_hosted_with_no_bind_at_all()
    {
        // Four ways of saying "no key": omitted, null, empty, whitespace. A module without one is
        // still hosted and still reachable over the pipe - it just has no key, which is what a
        // macro-driven setup wants.
        var config = HostConfig.Parse("""
        {
          "modules": [
            { "name": "3074-ul", "bind": "capslock" },
            { "name": "3074-dl" },
            { "name": "27k-ul", "bind": null },
            { "name": "27k-dl", "bind": "" },
            { "name": "30k", "bind": "   " }
          ],
          "releaseBind": ""
        }
        """);

        Assert.Equal(5, config.Resolve().Count);
        Assert.Equal("capslock", config.Modules[0].Bind);
        Assert.Null(config.Modules[1].Bind);
    }

    [Fact]
    public void Several_unbound_modules_do_not_collide_with_each_other()
    {
        // The clash check keys on the bind, so without filtering the blanks out first, two unbound
        // modules would read as two things on the same key.
        HostConfig.Parse("""
        {
          "modules": [
            { "name": "3074-ul", "bind": "" },
            { "name": "3074-dl", "bind": "" },
            { "name": "30k" },
            { "name": "7500" }
          ],
          "releaseBind": "",
          "pause": { "enabled": true, "bind": "" }
        }
        """);
    }

    [Fact]
    public void One_key_bound_to_two_things_is_rejected()
    {
        // The silent failure this check exists for: whichever binding registered second would
        // simply replace the first, with nothing to say so.
        var ex = Assert.Throws<ArgumentException>(() => HostConfig.Parse("""
        {
          "modules": [
            { "name": "3074-ul", "bind": "f1" },
            { "name": "30k", "bind": "f1" }
          ]
        }
        """));

        Assert.Contains("f1", ex.Message);
        Assert.Contains("3074-ul", ex.Message);
    }

    [Fact]
    public void A_key_clashing_with_release_or_pause_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => HostConfig.Parse("""
        {
          "modules": [ { "name": "3074-ul", "bind": "f12" } ],
          "releaseBind": "f12"
        }
        """));

        Assert.Throws<ArgumentException>(() => HostConfig.Parse("""
        {
          "modules": [ { "name": "3074-ul", "bind": "f10" } ],
          "pause": { "enabled": true, "bind": "f10" }
        }
        """));
    }

    [Fact]
    public void A_pause_bind_only_clashes_when_pause_is_enabled()
    {
        // A disabled pause is not hosted, so its key is free.
        var config = HostConfig.Parse("""
        {
          "modules": [ { "name": "3074-ul", "bind": "f10" } ],
          "pause": { "enabled": false, "bind": "f10" }
        }
        """);

        Assert.Single(config.Modules);
    }

    [Fact]
    public void A_reset_bind_naming_an_unhosted_module_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => HostConfig.Parse("""
        {
          "modules": [ { "name": "3074-ul" } ],
          "resetBinds": [ { "module": "30k", "bind": "f9" } ]
        }
        """));

        Assert.Contains("30k", ex.Message);
    }

    [Fact]
    public void A_config_that_hosts_nothing_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => HostConfig.Parse("""{ "modules": [] }"""));

        Assert.Contains("hosts nothing", ex.Message);
    }

    [Fact]
    public void Pause_alone_is_enough_to_host()
    {
        var config = HostConfig.Parse("""{ "pause": { "enabled": true, "bind": "f10" } }""");

        Assert.Empty(config.Modules);
        Assert.True(config.Pause.Enabled);
    }

    [Fact]
    public void A_bad_module_name_is_reported_at_load()
    {
        // Parsing every name up front turns a typo into one clear error instead of a module that
        // silently never appears.
        var ex = Assert.Throws<ArgumentException>(() => HostConfig.Parse("""
        { "modules": [ { "name": "3074-sideways" } ] }
        """));

        Assert.Contains("ul, dl, full, or slow", ex.Message);
    }

    [Theory]
    [InlineData("""{ "modules": [ { "name": "3074-ul", "slowMs": 0 } ] }""")]
    [InlineData("""{ "modules": [ { "name": "3074-ul", "bufferPackets": 0 } ] }""")]
    [InlineData("""{ "pollMs": 0, "modules": [ { "name": "3074-ul" } ] }""")]
    [InlineData("""{ "pollMs": 5000, "modules": [ { "name": "3074-ul" } ] }""")]
    public void Nonsense_numbers_are_rejected(string json)
    {
        Assert.Throws<ArgumentException>(() => HostConfig.Parse(json));
    }

    [Fact]
    public void Broken_json_says_where()
    {
        var ex = Assert.Throws<ArgumentException>(() => HostConfig.Parse("{ not json"));

        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void The_shipped_template_is_valid_and_hosts_everything()
    {
        // It is the first thing anyone runs, and it is mostly comments - so a stray comma in it
        // would be a broken first experience. This is the guard.
        var config = HostConfig.Parse(ConfigTemplate.Text);

        var names = config.Resolve().Select(m => m.Spec.Name).ToArray();

        Assert.Equal(
            [
                "3074-ul", "3074-dl", "3074-ul-slow", "3074-dl-slow",
                "27k-ul", "27k-dl", "7500", "30k", "30k-slow"
            ],
            names);

        Assert.True(config.Pause.Enabled);
        Assert.Equal("f12", config.ReleaseBind);
        Assert.Single(config.ResetBinds);

        // Every toggle reachable by key, which is what the file is for. Validation already proved
        // no two of them collide.
        Assert.All(config.Modules, m => Assert.False(string.IsNullOrWhiteSpace(m.Bind)));
        Assert.False(string.IsNullOrWhiteSpace(config.Pause.Bind));
    }

    [Fact]
    public void The_template_records_the_measured_30k_slow_depth()
    {
        // 8 s rather than the reference's stated 6 s, measured better here. The template carrying it
        // is the point of having a config at all.
        var slow = HostConfig.Parse(ConfigTemplate.Text)
            .Modules.Single(m => m.Name == "30k-slow");

        Assert.Equal(8000, slow.SlowMs);
    }

    [Fact]
    public void A_config_survives_a_round_trip_through_json()
    {
        var original = HostConfig.Parse("""
        {
          "pipe": "p",
          "modules": [ { "name": "30k-slow", "bind": "f7", "slowMs": 8000 } ],
          "releaseBind": "f12"
        }
        """);

        var again = HostConfig.Parse(original.ToJson());

        Assert.Equal(original.Pipe, again.Pipe);
        Assert.Equal("f12", again.ReleaseBind);
        Assert.Equal(8000, again.Resolve()[0].Tuning.SlowMs);
    }
}
