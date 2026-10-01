using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace ExplorerTabUtility.Models;

/// <summary>
/// A pixel coordinate, laid out exactly like Win32 <c>POINT</c> so it can be passed straight to
/// P/Invoke. Replaces <c>System.Drawing.Point</c> in the Core library, which removes the
/// System.Drawing dependency the WinUI shell does not otherwise need.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct PixelPoint(int x, int y)
{
    public readonly int X = x;
    public readonly int Y = y;

    public override string ToString() => $"({X}, {Y})";
}

/// <summary>
/// Persisted settings-window size. Replaces <c>System.Windows.Size</c>.
/// <para>
/// <b>Unit contract: the OUTER window size, in effective pixels.</b> That is the WPF
/// <c>Window.ActualWidth/Height</c> semantics this type inherits, and it is what
/// <c>AppWindow.Size</c>/<c>Resize</c> deal in once divided/multiplied by the rasterization scale.
/// It is deliberately <b>not</b> the XAML client area: writing <c>FrameworkElement.ActualWidth</c>
/// here (one frame border smaller in width, and one frame border plus the 32 epx title bar smaller in
/// height) made every launch restore a window smaller than the one that was saved.
/// </para>
/// <para>
/// Deliberately keeps the <c>Width</c>/<c>Height</c> member names and floating-point shape of
/// the original so existing <c>settings.json</c> files deserialize unchanged.
/// </para>
/// </summary>
public readonly record struct WindowSize(double Width, double Height)
{
    /// <summary>
    /// First-run window size, in effective pixels (outer window size — see the type remarks).
    /// <para>
    /// Wide enough to show the navigation pane <b>and</b> the settings column at once: the pane is
    /// 240 epx and the settings column wants at least ~620, so anything under ~900 would make the
    /// window open with the pane already collapsed by
    /// <c>MainWindow.UpdatePaneForWidth</c>. Matches the fallback used when a stored size is
    /// unusable, so both paths agree.
    /// </para>
    /// </summary>
    public static WindowSize Default => new(1130, 600);

    /// <summary>Derived, not persisted — without this it is written into settings.json.</summary>
    [JsonIgnore]
    public bool IsValid => Width > 0 && Height > 0;
}
