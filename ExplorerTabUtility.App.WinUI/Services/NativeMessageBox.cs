using System;
using System.Runtime.InteropServices;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// Win32 <c>MessageBox</c>, used only where a <c>ContentDialog</c> is impossible because no
/// <c>XamlRoot</c> exists yet — i.e. the "already running" notice shown before the app window
/// is created. Everywhere else goes through <see cref="ContentDialogService"/>.
/// </summary>
internal static class NativeMessageBox
{
    internal enum Icon : uint
    {
        None = 0x00000000,
        Error = 0x00000010,
        Question = 0x00000020,
        Warning = 0x00000030,
        Information = 0x00000040
    }

    private const uint MB_OK = 0x00000000;
    private const uint MB_SETFOREGROUND = 0x00010000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    public static void Show(string text, string caption, Icon icon = Icon.None)
    {
        // Duplicate instance: no window of our own to parent to, so pass NULL and force foreground.
        MessageBoxW(0, text, caption, MB_OK | (uint)icon | MB_SETFOREGROUND);
    }
}
