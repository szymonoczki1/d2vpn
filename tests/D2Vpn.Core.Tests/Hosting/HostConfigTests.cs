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
    public void Switch_states_reach_the_tuning_that_configures_a_module()
    {
        // They were written to the file and then ignored on the way back in: nothing carried
        // Enabled through to the module, so a module turned off came back on.
        var config = HostConfig.Parse("""
        {
          "modules": [
            { "name": "30k", "enabled": false, "resync": false, "buffer": false }
          ]
        }
        """);

        var tuning = config.Resolve()[0].Tuning;

        Assert.False(tuning.Enabled);
        Assert.False(tuning.Resync);
        Assert.False(tuning.Buffer);
    }

    [Fact]
    public void Switch_states_default_to_on_when_a_config_does_not_mention_them()
    {
        var tuning = HostConfig.Parse("""{ "modules": [ { "name": "30k" } ] }""")
            .Resolve()[0].Tuning;

        Assert.True(tuning.Enabled);
        Assert.True(tuning.Resync);
        Assert.True(tuning.Buffer);
    }

    [Fact]
    public void Pause_separates_being_hosted_from_being_enabled()
    {
        // Two different questions that would otherwise share one flag: a switch in the window that
        // unhosted its own control could not be switched back on from the window.
        var config = HostConfig.Parse("""
        {
          "pause": { "enabled": true, "moduleEnabled": false, "bind": "f10" }
        }
        """);

        Assert.True(config.Pause.Enabled);
        Assert.False(config.Pause.ModuleEnabled);

        // Hosted and enabled is the default, so an older config keeps working.
        var older = HostConfig.Parse("""{ "pause": { "enabled": true } }""");

        Assert.True(older.Pause.ModuleEnabled);
    }

    [Fact]
    public void Switch_states_survive_a_round_trip()
    {
        var original = HostConfig.Parse("""
        {
          "modules": [ { "name": "30k", "enabled": false, "resync": false } ],
          "pause": { "enabled": true, "moduleEnabled": false }
        }
        """);

        var again = HostConfig.Parse(original.ToJson());

        Assert.False(again.Modules[0].Enabled);
        Assert.False(again.Modules[0].Resync);
        Assert.False(again.Pause.ModuleEnabled);
    }

    [Fact]
    public void The_timer_waits_round_trip_and_default_sensibly()
    {
        var config = HostConfig.Parse("""
        {
          "modules": [ { "name": "30k" } ],
          "timer": { "hideAfterS": 20, "endAfterS": 60 }
        }
        """);

        Assert.True(config.Timer.Enabled);
        Assert.Equal(20, config.Timer.HideAfterS);
        Assert.Equal(60, config.Timer.EndAfterS);

        var defaults = HostConfig.Parse("""{ "modules": [ { "name": "30k" } ] }""").Timer;

        Assert.Equal(15, defaults.HideAfterS);
        Assert.Equal(45, defaults.EndAfterS);
    }

    [Fact]
    public void Hiding_after_the_end_is_rejected()
    {
        // It would mean the timer was never hidden at all, only stopped.
        var ex = Assert.Throws<ArgumentException>(() => HostConfig.Parse("""
        {
          "modules": [ { "name": "30k" } ],
          "timer": { "hideAfterS": 60, "endAfterS": 30 }
        }
        """));

        Assert.Contains("before it was ever hidden", ex.Message);
    }

    [Fact]
    public void A_contradictory_timer_is_repaired_rather_than_fatal_for_the_window()
    {
        var path = Write("""
        {
          "modules": [ { "name": "30k" } ],
          "timer": { "hideAfterS": 60, "endAfterS": 30 }
        }
        """);

        try
        {
            var config = HostConfig.LoadForgiving(path, out _);

            Assert.True(config.Timer.EndAfterS >= config.Timer.HideAfterS);
        }
        finally
        {
            File.Delete(path);
        }
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

    [Fact]
    public void The_overlay_panels_start_in_different_corners()
    {
        // Two panels in the same corner would be on top of each other on first run, which reads as
        // the overlay being broken rather than as something to go and move.
        var config = HostConfig.Parse("""
        { "modules": [ { "name": "30k" } ] }
        """);

        Assert.True(config.Overlay.Modules.Show);
        Assert.True(config.Overlay.Timer.Show);
        Assert.False(config.Overlay.TimerAttached);
        Assert.NotEqual(config.Overlay.Modules.Corner, config.Overlay.Timer.Corner);
    }

    [Fact]
    public void An_overlay_position_written_before_there_were_panels_is_carried_across()
    {
        // The overlay had one corner and one pair of margins before it had two panels. Without the
        // migration an existing file would quietly revert to the default corner on the first load
        // after an update - a setting silently lost, which is worse than one that fails loudly.
        var config = HostConfig.Parse("""
        {
          "modules": [ { "name": "30k" } ],
          "overlay": { "corner": "BottomLeft", "marginX": 64, "marginY": 90 }
        }
        """);

        Assert.Equal(OverlayCorner.BottomLeft, config.Overlay.Modules.Corner);
        Assert.Equal(64, config.Overlay.Modules.MarginX);
        Assert.Equal(90, config.Overlay.Modules.MarginY);

        // Cleared, so the move happens once and the old shape is not written back.
        Assert.Null(config.Overlay.Corner);
        Assert.Null(config.Overlay.MarginX);
        Assert.Null(config.Overlay.MarginY);

        // The timer keeps its own default rather than being dragged to the same corner.
        Assert.Equal(OverlayCorner.TopLeft, config.Overlay.Timer.Corner);
    }

    [Fact]
    public void An_overlay_size_written_before_there_were_panels_goes_to_both()
    {
        // Unlike the position, which described the module panel alone. Size and transparency
        // applied to everything drawn, so splitting them must not quietly halve the timer.
        var config = HostConfig.Parse("""
        {
          "modules": [ { "name": "30k" } ],
          "overlay": { "scale": 1.6, "opacity": 0.3 }
        }
        """);

        Assert.Equal(1.6, config.Overlay.Modules.Scale);
        Assert.Equal(1.6, config.Overlay.Timer.Scale);
        Assert.Equal(0.3, config.Overlay.Modules.Opacity);
        Assert.Equal(0.3, config.Overlay.Timer.Opacity);

        Assert.Null(config.Overlay.Scale);
        Assert.Null(config.Overlay.Opacity);
    }

    [Fact]
    public void The_panels_can_be_sized_separately()
    {
        var config = HostConfig.Parse("""
        {
          "modules": [ { "name": "30k" } ],
          "overlay": {
            "modules": { "scale": 0.8 },
            "timer": { "scale": 2.5, "opacity": 0 }
          }
        }
        """);

        Assert.Equal(0.8, config.Overlay.Modules.Scale);
        Assert.Equal(2.5, config.Overlay.Timer.Scale);
        Assert.Equal(0, config.Overlay.Timer.Opacity);

        // Untouched keeps the default rather than inheriting the other panel's.
        Assert.Equal(0.72, config.Overlay.Modules.Opacity);
    }

    [Fact]
    public void A_panel_scale_outside_the_range_is_refused()
    {
        Assert.Throws<ArgumentException>(() => HostConfig.Parse("""
        {
          "modules": [ { "name": "30k" } ],
          "overlay": { "timer": { "scale": 40 } }
        }
        """));
    }

    [Fact]
    public void A_config_written_with_panels_is_left_alone()
    {
        var config = HostConfig.Parse("""
        {
          "modules": [ { "name": "30k" } ],
          "overlay": {
            "modules": { "corner": "TopLeft", "marginX": 10, "marginY": 10 },
            "timer": { "corner": "BottomRight", "marginX": 20, "marginY": 20 },
            "timerAttached": true
          }
        }
        """);

        Assert.Equal(OverlayCorner.TopLeft, config.Overlay.Modules.Corner);
        Assert.Equal(OverlayCorner.BottomRight, config.Overlay.Timer.Corner);
        Assert.Equal(20, config.Overlay.Timer.MarginX);
        Assert.True(config.Overlay.TimerAttached);
    }

    [Fact]
    public void A_macro_preset_is_enabled_unless_it_says_otherwise()
    {
        // Per preset, not per module: with each one separately bindable, a module-wide switch
        // would turn off five things at once and say nothing about which.
        var config = HostConfig.Parse("""
        {
          "modules": [ { "name": "30k" } ],
          "macro": {
            "enabled": true,
            "presets": [
              { "name": "on by default" },
              { "name": "turned off", "enabled": false }
            ]
          }
        }
        """);

        Assert.True(config.Macro.Presets[0].Enabled);
        Assert.False(config.Macro.Presets[1].Enabled);
    }

    [Fact]
    public void A_disabled_preset_survives_being_written_back()
    {
        // The whole point: a preset switched off in the window has to still be off next launch.
        var original = HostConfig.Parse("""
        {
          "modules": [ { "name": "30k" } ],
          "macro": {
            "enabled": true,
            "presets": [ { "name": "two swap", "bind": "f8", "enabled": false } ]
          }
        }
        """);

        var reloaded = HostConfig.Parse(original.ToJson());

        Assert.False(reloaded.Macro.Presets[0].Enabled);
        Assert.Equal("f8", reloaded.Macro.Presets[0].Bind);
    }

    [Fact]
    public void A_macro_preset_holds_peer_traffic_unless_told_otherwise()
    {
        var config = HostConfig.Parse("""
        {
          "modules": [ { "name": "30k" } ],
          "macro": {
            "enabled": true,
            "presets": [
              { "name": "peers" },
              { "name": "players", "uplink": "27k-ul" }
            ]
          }
        }
        """);

        Assert.Equal("3074-ul", config.Macro.Presets[0].Uplink);
        Assert.Equal("27k-ul", config.Macro.Presets[1].Uplink);

        // Each preset carries its own, so one macro can hide behind peers and another behind
        // players in the same session.
        Assert.Equal("27k-ul", config.Macro.Presets[1].ToSettings(config.Macro.Grid).Uplink);
    }

    [Fact]
    public void A_macro_preset_naming_no_uplink_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => HostConfig.Parse("""
        {
          "modules": [ { "name": "30k" } ],
          "macro": {
            "enabled": true,
            "presets": [ { "name": "nameless", "uplink": "" } ]
          }
        }
        """));

        // The message lists what it could have been, since the valid answers are a short list.
        Assert.Contains("3074-ul", error.Message, StringComparison.Ordinal);
        Assert.Contains("27k-ul", error.Message, StringComparison.Ordinal);
    }
}
