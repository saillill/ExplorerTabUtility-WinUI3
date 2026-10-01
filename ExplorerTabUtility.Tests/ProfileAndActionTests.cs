using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Models;
using H.Hooks;
using Xunit;

namespace ExplorerTabUtility.Tests;

/// <summary>
/// The editor hides rows an action never reads and the menu strips profiles an action cannot serve,
/// so these three predicates have to agree with <c>HookManager</c>'s dispatch. A drift here is silent:
/// the setting simply stops being honoured (or a hidden row keeps being written).
/// </summary>
public class HotKeyActionCatalogTests
{
    public static TheoryData<HotKeyAction> AllActions()
    {
        var data = new TheoryData<HotKeyAction>();
        foreach (var action in Enum.GetValues<HotKeyAction>()) data.Add(action);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void Only_open_reads_the_path_field(HotKeyAction action)
        => Assert.Equal(action is HotKeyAction.Open, HotKeyActionCatalog.UsesPath(action));

    [Theory]
    [MemberData(nameof(AllActions))]
    public void Only_open_and_the_snap_actions_read_the_delay_field(HotKeyAction action)
    {
        var expected = action is HotKeyAction.Open
                              or HotKeyAction.SnapRight or HotKeyAction.SnapLeft
                              or HotKeyAction.SnapUp or HotKeyAction.SnapDown;

        Assert.Equal(expected, HotKeyActionCatalog.UsesDelay(action));
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void Only_the_three_location_actions_read_the_as_tab_field(HotKeyAction action)
    {
        var expected = action is HotKeyAction.Open
                              or HotKeyAction.Duplicate
                              or HotKeyAction.ReopenClosed;

        Assert.Equal(expected, HotKeyActionCatalog.UsesAsTab(action));
    }

    [Fact]
    public void Every_action_has_a_localized_display_name()
    {
        // Pinned culture: in the neutral (English) file a label may legitimately equal the enum member
        // name ("Duplicate"), so "label != enum name" only holds for a translated culture.
        using var culture = CultureScope.Use("zh-CN");

        var displays = new List<string>();

        foreach (var action in Enum.GetValues<HotKeyAction>())
        {
            var display = HotKeyActionCatalog.GetActionDisplay(action);

            Assert.False(string.IsNullOrWhiteSpace(display));
            // GetActionDisplay falls back to the enum name when the catalogue has no key for an action,
            // which is how a newly added action would silently ship as "SnapDiagonal".
            Assert.NotEqual(action.ToString(), display);

            displays.Add(display);
        }

        // …and two actions sharing one catalogue key would show up as a duplicate label.
        Assert.Equal(displays.Count, displays.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Global_scope_drops_the_actions_that_only_work_inside_explorer()
    {
        var global = HotKeyActionCatalog.GetAllowedActions(HotkeyScope.Global);
        var explorer = HotKeyActionCatalog.GetAllowedActions(HotkeyScope.FileExplorer);

        Assert.Equal(Enum.GetValues<HotKeyAction>().Length, explorer.Length);
        Assert.True(global.Length < explorer.Length);
        Assert.DoesNotContain(HotKeyAction.NavigateBack, global);
        Assert.DoesNotContain(HotKeyAction.NavigateForward, global);
        Assert.DoesNotContain(HotKeyAction.NavigateUp, global);
        Assert.DoesNotContain(HotKeyAction.SetTargetWindow, global);
        Assert.DoesNotContain(HotKeyAction.DetachTab, global);
        // Global actions are offered whichever window has focus, so the ones that act on an existing
        // Explorer window must not be among them.
        Assert.Contains(HotKeyAction.Open, global);
        Assert.Contains(HotKeyAction.ToggleVisibility, global);
        Assert.Contains(HotKeyAction.TabSearch, global);
    }

    [Theory]
    [InlineData("显示/隐藏", "ToggleVisibility")]
    [InlineData("标签搜索", "TabSearch")]
    [InlineData("分离标签页", "DetachTab")]
    [InlineData("贴靠至右侧", "SnapRight")]
    public void Legacy_wpf_profile_names_are_translated(string legacyName, string expected)
        => Assert.Equal(expected, HotKeyActionCatalog.MigrateLegacyProfileName(legacyName));

    [Theory]
    [InlineData("ReopenClosed")]   // already the English name
    [InlineData("My own name")]    // typed by the user
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Names_that_were_not_generated_are_left_alone(string? name)
        => Assert.Null(HotKeyActionCatalog.MigrateLegacyProfileName(name));
}

/// <summary>
/// Serialisation of the profile list. Two rules here have already caused real, silent data loss:
/// a private <c>Id</c> setter (every load minted new ids, so tray/edit lookups drifted) and numeric
/// <c>Action</c> values in older files (which must map through the legacy table, not through the enum).
/// </summary>
public class HotKeyProfileSerializationTests
{
    [Fact]
    public void Id_survives_a_round_trip()
    {
        var profile = new HotKeyProfile { Name = "Round trip", HotKeys = [Key.Ctrl, Key.D] };
        var id = profile.Id;

        var json = JsonSerializer.Serialize(profile);
        var restored = JsonSerializer.Deserialize<HotKeyProfile>(json)!;

        Assert.Equal(id, restored.Id);
        Assert.Equal(profile.Name, restored.Name);
        Assert.Equal([Key.Ctrl, Key.D], restored.HotKeys!);
    }

    [Fact]
    public void Action_is_written_as_a_name_so_reordering_cannot_change_its_meaning()
    {
        var json = JsonSerializer.Serialize(new HotKeyProfile { Action = HotKeyAction.SetTargetWindow });

        Assert.Contains("\"SetTargetWindow\"", json);
    }

    [Theory]
    // Legacy numeric values, from the table in HotKeyActionJsonConverter.
    [InlineData(0, HotKeyAction.Open)]
    [InlineData(1, HotKeyAction.Duplicate)]
    [InlineData(2, HotKeyAction.ReopenClosed)]
    [InlineData(6, HotKeyAction.ToggleVisibility)]
    [InlineData(9, HotKeyAction.DetachTab)]
    [InlineData(14, HotKeyAction.TabSearch)]
    // Not in the table and not a defined enum value: falls back to Open rather than throwing.
    [InlineData(99, HotKeyAction.Open)]
    public void Legacy_numeric_actions_map_through_the_compatibility_table(int stored, HotKeyAction expected)
    {
        var restored = JsonSerializer.Deserialize<HotKeyProfile>($"{{\"Action\":{stored}}}")!;

        Assert.Equal(expected, restored.Action);
    }

    [Fact]
    public void The_built_in_first_run_profiles_still_parse_into_the_intended_actions()
    {
        var profiles = JsonSerializer.Deserialize<List<HotKeyProfile>>(Constants.DefaultHotKeyProfiles)!;

        Assert.Equal(3, profiles.Count);
        Assert.Equal(
            [HotKeyAction.Open, HotKeyAction.Duplicate, HotKeyAction.ReopenClosed],
            profiles.Select(p => p.Action).ToArray());
        Assert.All(profiles, p => Assert.NotNull(p.HotKeys));
        Assert.All(profiles, p => Assert.NotEqual(Guid.Empty, p.Id));
    }

    [Fact]
    public void A_window_record_keeps_its_selected_items_and_age_stamp()
    {
        var record = new WindowRecord(@"C:\Temp", new IntPtr(0x1234), ["a.txt", "b.txt"], name: "Temp");
        var json = JsonSerializer.Serialize(record);

        var restored = JsonSerializer.Deserialize<WindowRecord>(json)!;

        Assert.Equal(record.Location, restored.Location);
        Assert.Equal(record.Handle, restored.Handle);
        Assert.Equal(record.Name, restored.Name);
        Assert.Equal(["a.txt", "b.txt"], restored.SelectedItems!);
        // TickCount64, not TickCount: the 32-bit value wraps negative after ~24.85 days of uptime and
        // the "recently closed" age check then matched records of any age.
        Assert.True(restored.CreatedAt > 0);
    }

    [Fact]
    public void Profile_clone_is_deep_enough_for_the_publish_snapshot()
    {
        var profile = new HotKeyProfile { Name = "Original", HotKeys = [Key.Ctrl, Key.D], Delay = 250 };
        var clone = profile.Clone();

        clone.Name = "Changed";
        clone.HotKeys![0] = Key.Alt;
        clone.Delay = 999;

        Assert.Equal("Original", profile.Name);
        Assert.Equal(Key.Ctrl, profile.HotKeys![0]);
        Assert.Equal(250, profile.Delay);
    }
}
