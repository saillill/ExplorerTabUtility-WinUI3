using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using ExplorerTabUtility.Models;

namespace ExplorerTabUtility.Tests;

/// <summary>
/// Covers the invariant behind the upstream #137 port: the window table is consulted through a
/// <em>liveness</em> question ("are there windows?"), never through raw <see cref="DualKeyDictionary{TKey,TKey2,TValue}.Count"/>
/// ("how many are tracked?").
/// <para>
/// The two answers diverge exactly when a window was destroyed without its unhook running — a shell crash,
/// a forced close, a shell restart after resume. Every decision that read <c>Count</c> then believed in
/// windows that no longer existed; <c>OpenTabNavigateWithSelection</c> in particular took its
/// "reuse an existing tab" branch against a ghost and dropped the navigation entirely.
/// </para>
/// <para>
/// These tests exercise the table arithmetic that the fix relies on. The liveness probe itself
/// (<c>WinApi.IsWindow</c>) needs a real desktop session, so it is modelled here as a predicate over the
/// recorded window identity — which is the part the fix actually changed.
/// </para>
/// </summary>
public class LiveWindowTrackingTests
{
    /// <summary>Stand-in for the tracked window; the fix keys everything off its HWND.</summary>
    private sealed record FakeWindow(nint HWND);

    /// <summary>Mirrors <c>ExplorerWatcher.GetLiveWindowCount</c>: prune the dead, then count.</summary>
    private static int GetLiveWindowCount(
        DualKeyDictionary<FakeWindow, nint?, string> table,
        HashSet<nint> liveHandles)
    {
        foreach (var window in table.Keys.ToArray())
            if (!liveHandles.Contains(window.HWND))
                table.Remove(window);

        return table.Count;
    }

    private static DualKeyDictionary<FakeWindow, nint?, string> BuildTable(params (nint Hwnd, string Location)[] windows)
    {
        var table = new DualKeyDictionary<FakeWindow, nint?, string>();
        foreach (var (hwnd, location) in windows)
            table.Add(new FakeWindow(hwnd), location);

        return table;
    }

    [Fact]
    public void LiveCount_CountsWindowsThatStillExist()
    {
        var table = BuildTable((0x100, @"C:\a"), (0x200, @"C:\b"));
        var live = new HashSet<nint> { 0x100, 0x200 };

        Assert.Equal(2, GetLiveWindowCount(table, live));
    }

    [Fact]
    public void LiveCount_DropsEntriesWhoseHandleIsGone()
    {
        var table = BuildTable((0x100, @"C:\a"), (0x200, @"C:\b"), (0x300, @"C:\c"));
        var live = new HashSet<nint> { 0x200 };

        Assert.Equal(1, GetLiveWindowCount(table, live));

        // The dead entries are evicted, not merely excluded from the count: a later call must not have to
        // rediscover them, and the stale secondary mapping must not survive either.
        Assert.Single(table);
        Assert.Single(table.Keys);
        Assert.Equal(0x200, table.Keys.Single().HWND);
    }

    [Fact]
    public void LiveCount_IsZeroWhenEveryTrackedWindowIsGone()
    {
        var table = BuildTable((0x100, @"C:\a"), (0x200, @"C:\b"));

        // This is the case the old code got wrong. `Count` still reports 2, so the "reuse an existing tab"
        // branch was taken and, finding no matching tab, the navigation was dropped — the window-path
        // fallback below it was never reached.
        Assert.Equal(2, table.Count);
        Assert.Equal(0, GetLiveWindowCount(table, []));

        Assert.Empty(table);
    }

    [Fact]
    public void LiveCount_PreservesTheReuseDecisionWhenOtherWindowsAreStillOpen()
    {
        // The counterpart to the test above, and the reason the fix cannot simply treat "no match found"
        // as "open a window": with another window genuinely open, a missing tab is a real answer and the
        // caller must still take the reuse path.
        var table = BuildTable((0x100, @"C:\a"), (0x200, @"C:\b"));
        var live = new HashSet<nint> { 0x200 };

        Assert.Equal(1, GetLiveWindowCount(table, live));
    }

    [Fact]
    public void LiveCount_IsIdempotent()
    {
        var table = BuildTable((0x100, @"C:\a"), (0x200, @"C:\b"), (0x300, @"C:\c"));
        var live = new HashSet<nint> { 0x300 };

        var first = GetLiveWindowCount(table, live);
        var second = GetLiveWindowCount(table, live);

        Assert.Equal(first, second);
        Assert.Equal(1, first);
    }

    [Fact]
    public void LiveCount_KeepsTheSecondaryKeyLookupWorkingForLiveWindows()
    {
        // Removing a window must not take another window's tab-handle mapping with it. The two-key table
        // resolves the tab handle back to the window, and tab reuse depends on that lookup staying valid.
        var windowA = new FakeWindow(0x100);
        var windowB = new FakeWindow(0x200);

        var table = new DualKeyDictionary<FakeWindow, nint?, string>();
        table.Add(windowA, @"C:\a", 0xA0);
        table.Add(windowB, @"C:\b", 0xB0);

        Assert.Equal(1, GetLiveWindowCount(table, [0x200]));

        Assert.True(table.TryGetByOptional(0xB0, out var primary, out var value));
        Assert.Same(windowB, primary);
        Assert.Equal(@"C:\b", value);

        // ... and the evicted window's mapping is gone, so a recycled handle cannot resolve to a dead entry.
        Assert.False(table.TryGetByOptional(0xA0, out _, out _));
    }

    [Fact]
    public void LiveCount_ReportsZeroForAnEmptyTable()
    {
        var table = new DualKeyDictionary<FakeWindow, nint?, string>();

        Assert.Equal(0, GetLiveWindowCount(table, []));
    }
}
