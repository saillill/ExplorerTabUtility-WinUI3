using System;
using System.Runtime.InteropServices;
using ExplorerTabUtility.Abstractions;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// The platform's own message box: a real top-level window, drawn by the system.
/// <para>
/// Used only where a <c>ContentDialog</c> is impossible because no <c>XamlRoot</c> exists yet — the
/// "already running" notices and the startup-failure report, all of which run before the app window is
/// created. Everything the user sees afterwards goes through <see cref="ContentDialogService"/>, which can
/// follow the app's theme; this cannot, which is why it is the exception rather than the rule.
/// </para>
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
    private const uint MB_OKCANCEL = 0x00000001;
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_SETFOREGROUND = 0x00010000;

    private const int IDOK = 1;
    private const int IDCANCEL = 2;
    private const int IDYES = 6;
    private const int IDNO = 7;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    public static DialogResult Show(
        string text,
        string caption,
        DialogButton buttons = DialogButton.OK,
        Icon icon = Icon.None)
    {
        // No window of our own to parent to, so pass NULL and force foreground: NULL keeps the box on
        // the desktop, owned by nobody, and MB_SETFOREGROUND is what stops it from opening behind
        // whatever the user is looking at.
        var type = buttons switch
        {
            DialogButton.OKCancel => MB_OKCANCEL,
            DialogButton.YesNo => MB_YESNO,
            _ => MB_OK
        };
        type |= (uint)icon | MB_SETFOREGROUND;

        return MessageBoxW(0, text, caption, type) switch
        {
            IDYES => DialogResult.Yes,
            IDNO => DialogResult.No,
            IDCANCEL => DialogResult.Cancel,
            _ => DialogResult.OK
        };
    }
}
