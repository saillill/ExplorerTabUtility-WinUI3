using System;
using System.Diagnostics;

namespace ExplorerTabUtility.Models;

/// <summary>
/// Per-window bookkeeping held alongside each tracked Explorer window.
/// <para>
/// Handler types are plain delegates rather than imported COM event delegates, so this model
/// no longer references <c>SHDocVw</c>.
/// </para>
/// </summary>
public class WindowInfo
{
    public WindowInfo() : this(Stopwatch.GetTimestamp())
    {
    }

    /// <summary>
    /// Creates a record with an explicit creation timestamp.
    /// <para>
    /// Windows that already existed when the watcher started are <b>not</b> freshly created; stamping
    /// them "<see cref="Stopwatch.GetTimestamp"/> now" made <c>SearchForTab</c>'s 2-second
    /// reuse-suppression window treat them as new and skip them, so the first hotkey press after
    /// launch opened a duplicate tab instead of reusing (AUD-27).
    /// </para>
    /// </summary>
    public WindowInfo(long createdAt) => CreatedAt = createdAt;

    public long CreatedAt { get; }
    public string? Location { get; set; }
    public string? Name { get; set; }

    /// <summary>Runs when the Explorer window quits; records it in the closed-windows list.</summary>
    public Action? OnQuitHandler { get; set; }

    /// <summary>Runs on <c>NavigateComplete2</c>; keeps <see cref="Location"/> up to date.</summary>
    public Action<object?, object?>? OnNavigateHandler { get; set; }
}