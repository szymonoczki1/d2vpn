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
    public void Switch_binds_are_read_and_counted_in_the_clash_check()
    {
        // The whole reason this test exists: a switch bind is a key like any other, so leaving it
        // out of the clash check would let it silently replace a hold binding.
        var config = HostConfig.Parse("""
        {
          "modules": [
            {
              "name": "3074-ul", "bind": "capslock",
              "resyncBind": "numpad1", "bufferBind": "numpad2"
            }
          ]
        }
        """);

        Assert.Equal("numpad1", config.Modules[0].ResyncBind);
        Assert.Equal("numpad2", config.Modules[0].BufferBind);

        var ex = Assert.Throws<ArgumentException>(() => HostConfig.Parse("""
        {
          "modules": [
            { "name": "3074-ul", "bind": "f1", "resyncBind": "f1" }
          ]
        }
        """));

        Assert.Contains("f1", ex.Message);
        Assert.Contains("resync", ex.Message);
    }

    [Fact]
    public void A_switch_bind_clashing_across_modules_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => HostConfig.Parse("""
        {
          "modules": [
            { "name": "3074-ul", "bufferBind": "f3" },
            { "name": "30k", "resyncBind": "f3" }
          ]
        }
        """));
    }

    [Fact]
    public void Switch_binds_are_optional()
    {
        var config = HostConfig.Parse("""
        { "modules": [ { "name": "3074-ul", "bind": "capslock" } ] }
        """);

        Assert.Null(config.Modules[0].ResyncBind);
        Assert.Null(config.Modules[0].BufferBind);
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
    public void A_module_can_be_configured_as_disabled()
    {
        var config = HostConfig.Parse("""
        {
          "modules": [
            { "name": "3074-ul" },
            { "name": "30k", "enabled": false }
          ]
        }
        """);

        Assert.True(config.Modules[0].Enabled);
        Assert.False(config.Modules[1].Enabled);
    }

    [Fact]
    public void A_duplicate_key_is_repaired_rather_than_fatal()
    {
        // The window is the tool for fixing a config, so it must not be stopped from opening by
        // the config needing fixing - a duplicate key once left the only thing that could resolve
        // it unable to start. The first use keeps the key; later ones are cleared.
        var path = Write("""
        {
          "modules": [
            { "name": "3074-ul-slow", "bind": "f2" },
            { "name": "3074-dl-slow", "bind": "f2" },
            { "name": "30k", "bind": "f6" }
          ]
        }
        """);

        try
        {
            Assert.Throws<ArgumentException>(() => HostConfig.Load(path));

            var config = HostConfig.LoadForgiving(path, out var warnings);

            Assert.Equal(3, config.Modules.Count);
            Assert.Equal("f2", config.Modules[0].Bind);
            Assert.Null(config.Modules[1].Bind);
            Assert.Equal("f6", config.Modules[2].Bind);

            Assert.Single(warnings);
            Assert.Contains("f2", warnings[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_unparseable_module_is_dropped_and_the_rest_are_kept()
    {
        var path = Write("""
        {
          "modules": [
            { "name": "3074-sideways", "bind": "f1" },
            { "name": "30k", "bind": "f6" }
          ]
        }
        """);

        try
        {
            var config = HostConfig.LoadForgiving(path, out var warnings);

            Assert.Single(config.Modules);
            Assert.Equal("30k", config.Modules[0].Name);
            Assert.Contains(warnings, w => w.Contains("3074-sideways"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_reconnect_key_for_an_unhosted_module_is_dropped()
    {
        var path = Write("""
        {
          "modules": [ { "name": "3074-ul" } ],
          "resetBinds": [ { "module": "30k", "bind": "f9" } ]
        }
        """);

        try
        {
            var config = HostConfig.LoadForgiving(path, out var warnings);

            Assert.Empty(config.ResetBinds);
            Assert.Contains(warnings, w => w.Contains("30k"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Repair_still_refuses_a_config_with_nothing_left()
    {
        // Removing things is all repair does, so a config whose every module was unparseable has
        // nothing to host and is a real failure rather than something to carry on from.
        var path = Write("""{ "modules": [ { "name": "nonsense" } ] }""");

        try
        {
            Assert.Throws<ArgumentException>(
                () => HostConfig.LoadForgiving(path, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_clean_config_is_repaired_to_itself_with_no_warnings()
    {
        var path = Write(ConfigTemplate.Text);

        try
        {
            var config = HostConfig.LoadForgiving(path, out var warnings);

            Assert.Empty(warnings);
            Assert.Equal(9, config.Modules.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Write(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"d2vpn-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void The_config_lives_in_appdata_unless_a_path_is_given()
    {
        // Both front ends used to pick their own: the window wrote beside its executable and the
        // CLI in the working directory, so a bind saved by one was invisible to the other. A build
        // directory is also wiped by a clean, and is under Program Files once installed - a
        // per-machine path for a per-user setting.
        var fallback = ConfigLocation.Resolve(null);

        Assert.Equal(ConfigLocation.Default, fallback);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            fallback,
            StringComparison.OrdinalIgnoreCase);

        Assert.EndsWith("d2vpn.json", fallback, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_falls_back_rather_than_resolving_to_nothing(string requested)
    {
        Assert.Equal(ConfigLocation.Default, ConfigLocation.Resolve(requested));
    }

    [Fact]
    public void An_explicit_path_is_made_absolute_and_kept()
    {
        var resolved = ConfigLocation.Resolve("somewhere/else.json");

        Assert.NotEqual(ConfigLocation.Default, resolved);
        Assert.EndsWith("else.json", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.True(Path.IsPathRooted(resolved));
    }

    [Fact]
    public void A_written_config_uses_the_same_casing_as_the_template()
    {
        // The reader does not care, but a file the window rewrites should not come back looking
        // different from the one the operator was given.
        var json = HostConfig.Parse(ConfigTemplate.Text).ToJson();

        Assert.Contains("\"pipe\"", json);
        Assert.Contains("\"pollMs\"", json);
        Assert.Contains("\"resetBinds\"", json);
        Assert.DoesNotContain("\"Pipe\"", json);
        Assert.DoesNotContain("\"PollMs\"", json);
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
