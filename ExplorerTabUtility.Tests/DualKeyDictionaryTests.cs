using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExplorerTabUtility.Models;
using Xunit;

namespace ExplorerTabUtility.Tests;

/// <summary>
/// The table <c>ExplorerWatcher</c> keeps its live windows in, touched from several threads: the COM
/// event thread registers windows, the STA scheduler resolves tab handles, the shell-restart path tears
/// everything down, and the crash path walks and mutates the table at the same time. The class promises
/// internal synchronisation and snapshot enumeration — both asserted here, because their absence shows
/// up as a mid-loop <see cref="InvalidOperationException"/> inside a hook callback rather than as an
/// obvious failure.
/// </summary>
public class DualKeyDictionaryTests
{
    [Fact]
    public void An_entry_is_reachable_by_both_keys()
    {
        var dict = new DualKeyDictionary<string, int?, string>();

        Assert.True(dict.TryAdd("primary", "value", 7));

        Assert.True(dict.TryGetByPrimary("primary", out var byPrimary, out var optionalKey));
        Assert.Equal("value", byPrimary);
        Assert.Equal(7, optionalKey);

        Assert.True(dict.TryGetByOptional(7, out var primaryKey, out var byOptional));
        Assert.Equal("primary", primaryKey);
        Assert.Equal("value", byOptional);

        Assert.Single(dict);
        Assert.True(dict.ContainsPrimary("primary"));
        Assert.True(dict.ContainsOptional(7));
    }

    [Fact]
    public void A_duplicate_primary_key_is_refused_by_TryAdd_and_throws_from_Add()
    {
        var dict = new DualKeyDictionary<string, int?, string>();
        Assert.True(dict.TryAdd("a", "first"));

        Assert.False(dict.TryAdd("a", "second"));
        Assert.Equal("first", dict["a"].Value);

        Assert.Throws<ArgumentException>(() => dict.Add("a", "third"));
    }

    [Fact]
    public void Indexing_a_missing_primary_key_throws()
    {
        var dict = new DualKeyDictionary<string, int?, string>();

        Assert.Throws<KeyNotFoundException>(() => { _ = dict["missing"]; });
    }

    [Fact]
    public void Updating_the_optional_key_moves_the_secondary_mapping()
    {
        var dict = new DualKeyDictionary<string, int?, string>();
        dict.TryAdd("w", "value", 1);

        dict.UpdateOptionalKey("w", 2);

        Assert.False(dict.ContainsOptional(1));
        Assert.True(dict.ContainsOptional(2));
        Assert.True(dict.TryGetByOptional(2, out var primary, out _));
        Assert.Equal("w", primary);

        // Passing null detaches the secondary key entirely (the window-handle cache case).
        dict.UpdateOptionalKey("w", null);
        Assert.False(dict.ContainsOptional(2));
        Assert.True(dict.ContainsPrimary("w"));
    }

    [Fact]
    public void Updating_onto_an_optional_key_owned_by_another_entry_is_refused()
    {
        var dict = new DualKeyDictionary<string, int?, string>();
        dict.TryAdd("a", "value-a", 1);
        dict.TryAdd("b", "value-b", 2);

        Assert.Throws<ArgumentException>(() => dict.UpdateOptionalKey("b", 1));
    }

    [Fact]
    public void Claiming_an_optional_key_takes_it_away_from_the_previous_owner()
    {
        var dict = new DualKeyDictionary<string, int?, string>();
        dict.TryAdd("old", "value-old", 5);

        // The indexer overwrites, which is what makes "which window owns this tab handle now" a single
        // well-defined answer instead of two entries fighting over it.
        dict["new"] = new DualKeyEntry<string, int?, string>("new", "value-new", 5);

        Assert.True(dict.TryGetByOptional(5, out var primary, out var value));
        Assert.Equal("new", primary);
        Assert.Equal("value-new", value);

        Assert.True(dict.TryGetByPrimary("old", out var oldValue, out var oldOptionalKey));
        Assert.Equal("value-old", oldValue);
        Assert.Null(oldOptionalKey);
    }

    [Fact]
    public void Removal_clears_both_mappings()
    {
        var dict = new DualKeyDictionary<string, int?, string>();
        dict.TryAdd("a", "value-a", 1);

        Assert.True(dict.RemoveByPrimary("a"));
        Assert.Empty(dict);
        Assert.False(dict.ContainsOptional(1));

        dict.TryAdd("b", "value-b", 2);
        Assert.True(dict.RemoveByOptional(2));
        Assert.Empty(dict);
        Assert.False(dict.ContainsPrimary("b"));
    }

    [Fact]
    public void Enumerating_while_removing_does_not_throw()
    {
        var dict = new DualKeyDictionary<string, int?, string>();
        for (var i = 0; i < 20; i++) dict.TryAdd($"w{i}", $"value{i}", i);

        var seen = 0;

        // Exactly the crash path in ExplorerWatcher.OnExplorerProcessTerminated: walk the table and
        // remove as you go. Snapshot enumeration is what makes that legal.
        foreach (var (primaryKey, value) in dict)
        {
            seen++;
            dict.RemoveByPrimary(primaryKey);
        }

        Assert.Equal(20, seen);
        Assert.Empty(dict);
    }

    [Fact]
    public void Keys_and_Values_are_snapshots_not_live_views()
    {
        var dict = new DualKeyDictionary<string, int?, string>();
        dict.TryAdd("a", "value-a", 1);

        var keys = dict.Keys;
        var values = dict.Values;
        var optionalKeys = dict.OptionalKeys;

        dict.TryAdd("b", "value-b", 2);

        Assert.Single(keys);
        Assert.Single(values);
        Assert.Single(optionalKeys);
        Assert.Equal(2, dict.Count);
    }

    [Fact]
    public async Task Concurrent_readers_enumerators_and_writers_do_not_interfere()
    {
        var dict = new DualKeyDictionary<string, int?, string>();
        var failures = 0;

        void Guard(Action body)
        {
            try { body(); }
            catch { Interlocked.Increment(ref failures); }
        }

        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            var keyPrefix = worker * 10_000;

            for (var i = 0; i < 300; i++)
            {
                var key = $"w{keyPrefix + i}";

                Guard(() => dict.TryAdd(key, key, keyPrefix + i));
                Guard(() => { if (dict.ContainsPrimary(key)) dict.UpdateOptionalKey(key, keyPrefix + i + 1); });
                Guard(() => _ = dict.Count);
                Guard(() => dict.RemoveByPrimary(key));
            }
        })).ToArray();

        // Enumerating while the others add and remove is the case that used to throw.
        var enumerator = Task.Run(() =>
        {
            for (var i = 0; i < 600; i++)
                Guard(() => { foreach (var _ in dict) { } });
        });

        await Task.WhenAll(workers.Append(enumerator));

        Assert.Equal(0, failures);
    }
}

/// <summary>
/// The persisted window size. Its unit contract (outer window size, effective pixels) is documented on
/// the type; asserted here is the part the restore path branches on.
/// </summary>
public class WindowSizeTests
{
    [Fact]
    public void Default_is_a_usable_size()
    {
        var size = WindowSize.Default;

        Assert.True(size.IsValid);
        Assert.True(size.Width > 0 && size.Height > 0);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 600, false)]
    [InlineData(1130, 0, false)]
    [InlineData(-5, 600, false)]
    [InlineData(1130, 600, true)]
    public void IsValid_rejects_anything_a_window_cannot_be(double width, double height, bool expected)
        => Assert.Equal(expected, new WindowSize(width, height).IsValid);
}
