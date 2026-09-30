using System;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using H.Hooks;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Hooks;

public sealed class Mouse : IHook
{
    private int _lastClickTime;
    private Key _lastClickKey;
    private readonly LowLevelMouseHook _lowLevelMouseHook;
    private readonly Func<IReadOnlyList<HotKeyProfile>> _hotkeyProfilesProvider;
    public bool IsHookActive => _lowLevelMouseHook.IsStarted;
    public event Action<HotKeyEventArgs>? OnHotKeyProfileTriggered;

    public Mouse(Func<IReadOnlyList<HotKeyProfile>> hotkeyProfilesProvider)
    {
        // A provider, not a collection: the hook thread must fetch the *current* immutable snapshot on
        // every event rather than capture a reference the UI thread may later replace or mutate (AUD-02).
        _hotkeyProfilesProvider = hotkeyProfilesProvider ?? throw new ArgumentNullException(nameof(hotkeyProfilesProvider));
        // Handling = true so a matched profile can swallow the click via e.IsHandled below.
        // (H.Hooks only suppresses an event when BOTH are set.)
        _lowLevelMouseHook = new LowLevelMouseHook { AddKeyboardKeys = true, Handling = true };
        _lowLevelMouseHook.Down += LowLevelMouseHook_Down;
    }

    public void StartHook() => _lowLevelMouseHook.Start();
    public void StopHook() => _lowLevelMouseHook.Stop();

    private void LowLevelMouseHook_Down(object? sender, MouseEventArgs e)
    {
        var handler = OnHotKeyProfileTriggered;
        if (handler == null) return;

        // Fetch the snapshot ONCE for the whole callback (see Keyboard for the rationale) (AUD-02).
        IReadOnlyList<HotKeyProfile> hotkeyProfiles;
        try
        {
            hotkeyProfiles = _hotkeyProfilesProvider();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Mouse profile provider failed: {ex}");
            return;
        }

        var isDoubleClick = IsDoubleClick(e.CurrentKey);

        bool? isFileExplorerForeground = null;
        nint handle = 0;

        // This body runs inside a WH_MOUSE_LL native callback where a stray exception is undefined
        // behaviour — contain the loop so nothing can escape it (AUD-02).
        try
        {
            foreach (var profile in hotkeyProfiles)
            {
                // Skip disabled, empty or non mouse
                if (!profile.IsMouse || !profile.IsEnabled || profile.HotKeys is null || profile.HotKeys.Length == 0)
                    continue;

                // Skip if it requires double-click and it is not
                if (profile.IsDoubleClick && !isDoubleClick) continue;

                // Skip if keys do not match
                if (!e.Keys.Are(profile.HotKeys)) continue;

                // Let's see if we need to check File Explorer
                if (profile.Scope == HotkeyScope.FileExplorer)
                {
                    // Check if File Explorer is foreground (only once)
                    isFileExplorerForeground ??= Helper.IsFileExplorerForeground(out handle);

                    if (isFileExplorerForeground == false)
                    {
                        handle = 0; // Reset handle if not File Explorer
                        continue;
                    }
                }

                // Set handled value. Only the X-buttons are ever swallowed: they carry a shell-default
                // action (back/forward), so without suppression a profile bound to XButton1 would fire
                // BOTH our action and Explorer's own navigation (a double navigation). LMB/RMB/MMB are
                // never swallowed — suppressing those would break normal clicking everywhere.
                e.IsHandled = profile.IsHandled &&
                              profile.HotKeys.Any(k => k is Key.MouseXButton1 or Key.MouseXButton2);

                // Queue the hotkey trigger in a separate thread.
                ThreadPool.QueueUserWorkItem(static s => s.Handler.Invoke(new HotKeyEventArgs(s.Profile, s.Handle, s.Position)),
                    new State(handler, profile, handle, new PixelPoint(e.Position.X, e.Position.Y)), false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Mouse hook iteration failed: {ex}");
        }
    }

    private bool IsDoubleClick(Key currentKey)
    {
        var isDoubleClick = false;

        var now = Environment.TickCount;
        if (now - _lastClickTime < 500 && _lastClickKey == currentKey)
            isDoubleClick = true;

        _lastClickTime = now;
        _lastClickKey = currentKey;
        return isDoubleClick;
    }

    public void Dispose()
    {
        StopHook();
        _lowLevelMouseHook.Dispose();
    }

    private readonly struct State(Action<HotKeyEventArgs> handler, HotKeyProfile profile, nint handle, PixelPoint position)
    {
        public readonly Action<HotKeyEventArgs> Handler = handler;
        public readonly HotKeyProfile Profile = profile;
        public readonly nint Handle = handle;
        public readonly PixelPoint Position = position;
    }
}
