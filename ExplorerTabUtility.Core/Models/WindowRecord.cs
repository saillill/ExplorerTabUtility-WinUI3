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

    [JsonConstructor]
    private WindowRecord() : this(string.Empty)
    {
    }
}