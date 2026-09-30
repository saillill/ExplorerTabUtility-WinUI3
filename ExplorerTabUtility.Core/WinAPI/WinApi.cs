// ReSharper disable IdentifierTypo
// ReSharper disable InconsistentNaming

using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ExplorerTabUtility.Interop;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Models;

namespace ExplorerTabUtility.WinAPI;

public static class WinApi
{
    public const int EVENT_OBJECT_SHOW = 0x8002;

    public const int WM_COMMAND = 0x111; // Send a command

    public const int SW_SHOWNOACTIVATE = 4; // Show window but not activated

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;

    /// <summary>Puts the window at the top of the Z order without activating it.</summary>
    public static readonly nint HWND_TOPMOST = new(-1);

    /// <summary>Removes the topmost promotion, putting the window back in the normal band.</summary>
    public static readonly nint HWND_NOTOPMOST = new(-2);

    /// <summary>Restores a minimized or maximized window to its normal size and activates it.</summary>
    public const int SW_RESTORE = 9;

    public const int GWL_EXSTYLE = -20; // Extended window style.
    public const int WS_EX_LAYERED = 0x80000; // Layered window.
    public const int LWA_ALPHA = 0x2; // Determine the opacity of a layered window

    public const uint SIGDN_URL = 0x80068000;

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hModWinEventProc, WinEventDelegate lPfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    public static extern nint GetParent(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint FindWindowEx(nint parentHandle, nint childAfter, string className, string? windowTitle);

    [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "MapVirtualKeyW")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint handle, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern uint RealGetWindowClass(nint hwnd, StringBuilder pszType, uint cchType);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern nint SendMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool PostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);
    
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("shell32.dll")]
    public static extern int SHGetDesktopFolder(out nint ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetNameFromIDList(nint pidl, uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);

    [DllImport("oleacc.dll")]
    public static extern nint AccessibleObjectFromPoint(PixelPoint pt, [Out, MarshalAs(UnmanagedType.Interface)] out IAccessible? accObj, [Out] out object ChildID);

    public static IEnumerable<nint> FindAllWindowsEx(string className, nint parent = 0, string? windowTitle = null)
    {
        nint handle = 0;
        do
        {
            handle = FindWindowEx(parent, handle, className, windowTitle);

            if (handle == 0) continue;

            yield return handle;

        } while (handle != 0);
    }

    /// <summary>
    /// Restores the specified window to the foreground even if it was minimized.
    /// </summary>
    /// <param name="window">The handle to the window that needs to be restored to the foreground.</param>
    /// <remarks>
    /// <para>
    /// This is the call that does the real work of surfacing a window: it activates it and, when the
    /// OS refuses because the caller is a background process, it clears the foreground lock via
    /// <see cref="Helper.BypassWinForegroundRestrictions"/> and retries. It does not change the
    /// window's Z-order band, which it does not need to — a fullscreen non-topmost window (a
    /// borderless/maximized game, for instance) is still below an ordinary foreground window.
    /// </para>
    /// <para>
    /// Call it for "bring this window forward the way clicking its taskbar button would". Reach for
    /// <see cref="ForceToTop"/> only when that is provably not enough.
    /// </para>
    /// </remarks>
    public static void RestoreWindowToForeground(nint window)
    {
        //If Minimized
        if (IsIconic(window))
        {
            // Show the window but don't activate it, SetForegroundWindow is going to activate it. 
            ShowWindow(window, SW_SHOWNOACTIVATE);
        }

        if (SetForegroundWindow(window)) return;

        // Background processes are not allowed to steal the foreground; the OS silently refuses and
        // only blinks the taskbar button. Simulate a key press to clear that lock, then retry.
        Helper.BypassWinForegroundRestrictions();

        SetForegroundWindow(window);
    }

    /// <summary>
    /// Makes a best effort to get the window in front of everything covering it, including a
    /// fullscreen window. <b>Not a guarantee</b> — see the remarks for the measured failure rate.
    /// </summary>
    /// <param name="window">The handle to the window that should end up on top.</param>
    /// <returns>
    /// True when the window ended up in the foreground; false when the OS still refused after the
    /// fallback. False does not necessarily mean the window is invisible — the Z-order promotion may
    /// still have worked.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Measured behaviour — the Z-order promotion is what makes the window visible.</b> Verified
    /// end-to-end against two fullscreen titles, one borderless (<c>LaunchUnrealUWindowsClient</c>,
    /// <c>WS_POPUP</c>, no caption, 3840x2160) and one a Vulkan renderer that actively reclaims the top
    /// of the Z order. With the promotion applied the window ranked #1 in Z order, all 9 occlusion
    /// sample points resolved to it, and its dialog was plainly visible on screen. The Vulkan title
    /// exposed one residual failure mode: in 1 of 5 runs the window stayed behind the game with
    /// <c>WS_EX_TOPMOST</c> still set, i.e. the promotion itself succeeded and the renderer won the
    /// race anyway. That is not retryable from here — there is nothing left to fail — so callers get
    /// visible-or-not, with no way to force it.
    /// </para>
    /// <para>
    /// An earlier controlled A/B/C experiment (Notepad against the same game, so the result is
    /// independent of this app) is consistent with this once read correctly: <c>SetForegroundWindow</c>
    /// alone changed nothing on screen — it does not touch Z-order, only foreground ownership — and
    /// the case that appeared to fail was one where the foreground could not be taken, not one where
    /// the promotion was ineffective. Repeating the sequence "working" the second time is the same
    /// race described above, not the promotion being unreliable.
    /// </para>
    /// <para>
    /// <b>Taking the foreground is a separate concern and routinely fails.</b> When a fullscreen game
    /// holds the foreground, <c>GetForegroundWindow()</c> still returns the game after this method
    /// runs, so the <c>false</c> return is expected and common. It does <b>not</b> mean the window is
    /// hidden — visibility comes from the Z-order promotion, which is independent of foreground
    /// ownership. Callers must read a <c>false</c> return as "keyboard focus not acquired", never as
    /// "window invisible", and must not surface it to the user as a failure.
    /// </para>
    /// <para>
    /// The one case that genuinely cannot be fixed in user space is <b>true exclusive fullscreen</b>,
    /// where the game owns the display outright and no ordinary window can appear over it. The title
    /// this was measured against is borderless fullscreen, which is not that. Do not document this
    /// method as "guaranteed on top" anywhere: it is best effort, and the exclusive case is beyond it.
    /// </para>
    /// <para>
    /// Order matters and follows the measurement: restore, then promote the Z-order band, then take
    /// the foreground. The foreground step goes through <see cref="RestoreWindowToForeground"/> so it
    /// keeps the foreground-lock bypass the rest of the app relies on.
    /// </para>
    /// <para>
    /// ⚠️ The <c>HWND_TOPMOST</c> promotion is <b>sticky</b> — it is a persistent style, not a
    /// one-shot raise, and measurement confirmed the bit stays set afterwards. That is why
    /// <see cref="ReleaseTop"/> exists: leaving it on would make the window permanently outrank the
    /// user's Explorer windows and browser. The caller must pair every call with a release, and must
    /// not release on a timer while a dialog is still open — dropping the promotion hands the top of
    /// the Z order straight back to a fullscreen renderer (measured: the notice disappeared at ~2.5s).
    /// Bind the promotion's lifetime to whatever the window was raised for, not to a duration.
    /// </para>
    /// </remarks>
    public static bool ForceToTop(nint window)
    {
        // Restore first: a minimized window cannot be raised meaningfully.
        if (IsIconic(window)) ShowWindow(window, SW_RESTORE);

        // Promote the Z-order band. SWP_NOACTIVATE keeps this independent of the foreground step
        // below; SWP_NOMOVE|SWP_NOSIZE keeps the geometry untouched — only the Z-order changes.
        SetWindowPos(
            window,
            HWND_TOPMOST,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        RestoreWindowToForeground(window);

        return GetForegroundWindow() == window;
    }

    /// <summary>
    /// Drops the <c>HWND_TOPMOST</c> promotion applied by <see cref="ForceToTop"/>.
    /// </summary>
    /// <param name="window">The handle whose topmost promotion should be removed.</param>
    /// <remarks>
    /// Must follow every <see cref="ForceToTop"/>, otherwise the window keeps outranking all
    /// non-topmost windows for the rest of the session — including the user's own Explorer windows,
    /// which is the usual complaint about stray topmost windows. Uses <c>SWP_NOACTIVATE</c> so the
    /// retreat does not itself steal focus, and does not move or resize the window.
    /// </remarks>
    public static void ReleaseTop(nint window)
    {
        SetWindowPos(
            window,
            HWND_NOTOPMOST,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static string GetWindowClassName(nint hWnd, int maxClassNameLength = 254)
    {
        if (hWnd == 0) return string.Empty;

        var className = new StringBuilder(maxClassNameLength + 1);
        RealGetWindowClass(hWnd, className, (uint)className.Capacity);

        return className.ToString();
    }
    public static bool IsWindowHasClassName(nint hWnd, string className, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var currentClassName = GetWindowClassName(hWnd, className.Length);

        return string.Equals(currentClassName, className, comparison);
    }
    
    public static string? GetProcessPath(int pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        var procHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (procHandle == 0) return null;
        
        try
        {
            var capacity = 260;
            var sb = new StringBuilder(capacity);
            return QueryFullProcessImageName(procHandle, 0, sb, ref capacity) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(procHandle);
        }
    }
}