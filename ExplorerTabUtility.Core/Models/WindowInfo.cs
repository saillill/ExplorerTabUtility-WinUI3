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
    public long CreatedAt { get; } = Stopwatch.GetTimestamp();
    public string? Location { get; set; }
    public string? Name { get; set; }

    /// <summary>Runs when the Explorer window quits; records it in the closed-windows list.</summary>
    public Action? OnQuitHandler { get; set; }

    /// <summary>Runs on <c>NavigateComplete2</c>; keeps <see cref="Location"/> up to date.</summary>
    public Action<object?, object?>? OnNavigateHandler { get; set; }
}