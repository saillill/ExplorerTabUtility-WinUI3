using System;
using System.Runtime.InteropServices;
using ExplorerTabUtility.Abstractions;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// The platform's own message box: a real top-level window, movable and independent of the app window.
/// <para>
/// That independence is why the "restore previous windows?" prompt uses it (see
/// <see cref="DialogHost.Standalone"/>). The question arrives from a background thread
/// while the app window may be hidden in the tray, and a dialog that lives inside that window would have
/// to drag it on screen first — the app window appearing out of nowhere to ask a question about
/// <em>File Explorer</em>'s windows. File Explorer's own prompts are separate windows for the same
/// reason.
/// </para>
/// <para>
/// Also used where a <c>ContentDialog</c> is impossible because no <c>XamlRoot</c> exists yet — the
/// "already running" notices shown before the app window is created.
/// </para>
/// <para>
/// Its look and theme belong to the system: a Win32 message box cannot follow the theme this app applies
/// to its own window. That is the price of a dialog that exists independently of it, and the reason
/// everything else stays in-window.
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
