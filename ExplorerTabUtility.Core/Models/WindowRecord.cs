using System;
using System.Text.Json.Serialization;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Models;

public class WindowRecord(string location, nint handle = 0, string[]? selectedItems = null, string name = "", bool restore = false)
{
    [JsonConverter(typeof(IntPtrConverter))]
    public nint Handle { get; set; } = handle;
    public string Name { get; set; } = name;
    public string Location { get; set; } = location;
    public string[]? SelectedItems { get; set; } = selectedItems;

    /// <summary>
    /// Creation stamp, compared against <see cref="Environment.TickCount64"/> by
    /// <c>ExplorerWatcher.TryGetRecentlyClosedWindow</c>. The 32-bit <c>Environment.TickCount</c>
    /// wraps negative after ~24.85 days of uptime, which made the age check match records of ANY
    /// age. Persisted copies are meaningless against a later boot's clock and are zeroed on load.
    /// </summary>
    public long CreatedAt { get; set; } = Environment.TickCount64;
    public bool Restore { get; set; } = restore;

    /// <summary>
    /// Whether this record came from a tab that left a window behind — i.e. a tab pulled out of a window
    /// that is still open, rather than a window that was closed.
    /// </summary>
    /// <remarks>
    /// The two look identical from the location alone: a window disappears and a window at the same location
    /// appears moments later. They are not the same event, though — one is the user saying "this folder
    /// belongs in its own window", the other is a close and a reopen (or the app restoring the windows it
    /// remembered). Only the first is worth writing down, so the count of tabs still in the window at the
    /// moment the tab quit is recorded here and read back by <c>ExplorerWatcher</c>.
    /// </remarks>
    public bool WasDetachedTab { get; set; }

    [JsonConstructor]
    private WindowRecord() : this(string.Empty)
    {
    }
}