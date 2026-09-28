namespace ExplorerTabUtility.Models;

public class HotKeyEventArgs(HotKeyProfile profile, nint foregroundWindow, PixelPoint? mousePosition = null)
{
    public HotKeyProfile Profile { get; } = profile;
    public nint ForegroundWindow { get; } = foregroundWindow;
    public PixelPoint? MousePosition { get; } = mousePosition;
}