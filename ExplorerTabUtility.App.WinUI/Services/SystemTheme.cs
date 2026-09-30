using System;
using Windows.UI.ViewManagement;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// Single source of truth for the Windows <b>app</b> theme — the theme that "Follow system"
/// resolves to.
/// <para>
/// WinUI 3 desktop does <b>not</b> make <see cref="Microsoft.UI.Xaml.Application.RequestedTheme"/>
/// follow the OS: unlike UWP it starts at <c>Light</c> and stays there. Treating the default as
/// "already tracks Windows" is what made "Follow system" come up light on a dark system, so every
/// caller that needs the OS theme now resolves it through <see cref="IsDark"/> and there is exactly
/// one place that knows how to ask.
/// </para>
/// <para>
/// The value comes from <see cref="UISettings.GetColorValue"/>: <c>UIColorType.Background</c> is
/// white under the light app theme and black under the dark one. Measured on this machine, it tracks
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme</c> (the
/// <em>app</em> theme), <b>not</b> <c>SystemUsesLightTheme</c>: with <c>AppsUseLightTheme=0</c> and
/// <c>SystemUsesLightTheme=1</c> the background still reports black. That is the setting the picker
/// means by "Follow system", so this is the correct signal.
/// </para>
/// <para>
/// <see cref="Changed"/> is raised when the OS colours change (a theme switch included). It is
/// raised on a background thread, so subscribers must marshal before touching XAML.
/// </para>
/// </summary>
internal static class SystemTheme
{
    /// <summary>
    /// The shared settings object. Created once, on the first caller's thread — the <see cref="App"/>
    /// constructor is the earliest and runs on the UI thread, and construction works off it too.
    /// It is owned by this type for the life of the process, so it never outlives anything it refers to.
    /// </summary>
    private static readonly UISettings Settings = new();

    static SystemTheme()
    {
        // Subscribed once, here, rather than per consumer: the static guard makes a double
        // subscription impossible, and the handler captures nothing but the static event field, so
        // it holds no reference to any window or page. There is consequently nothing to unsubscribe.
        Settings.ColorValuesChanged += (_, _) => Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>True when the Windows app theme is the dark one.</summary>
    public static bool IsDark()
    {
        // White background = light app theme, black = dark. Averaging the channels keeps a
        // high-contrast theme (which reports its own background) on the correct side.
        var background = Settings.GetColorValue(UIColorType.Background);
        return (background.R + background.G + background.B) / 3 < 128;
    }

    /// <summary>
    /// Raised on a background thread when the OS colours change. A theme switch is one of those
    /// changes; subscribers must hop to the UI thread before touching XAML.
    /// </summary>
    public static event EventHandler? Changed;
}
