using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// Paints the system-drawn caption buttons for a theme.
/// <para>
/// Windows colours them from <em>its own</em> app theme. This app can override that theme, and both
/// windows extend the client area over the caption, so a light window with a light caption is the result
/// unless the buttons are told otherwise. Shared by the main window and the dialog window so the two
/// cannot drift apart.
/// </para>
/// </summary>
internal static class CaptionColors
{
    public static void Apply(AppWindow window, bool dark)
    {
        // Fluent caption colours: full-strength glyph, dimmed when the window is inactive, and the
        // standard subtle overlays for hover/pressed.
        var foreground = dark ? Colors.White : Colors.Black;
        var inactive = Color.FromArgb(0x66, dark ? (byte)0xFF : (byte)0x00,
                                                 dark ? (byte)0xFF : (byte)0x00,
                                                 dark ? (byte)0xFF : (byte)0x00);
        var hover = dark
            ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
        var pressed = dark
            ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x17, 0x00, 0x00, 0x00);

        var bar = window.TitleBar;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = inactive;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonPressedForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = pressed;
    }
}
