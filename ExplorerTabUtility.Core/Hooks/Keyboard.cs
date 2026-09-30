using System;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using H.Hooks;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Hooks;

public sealed class Keyboard : IHook
{
    private readonly LowLevelKeyboardHook _lowLevelKeyboardHook;
    private readonly Func<IReadOnlyList<HotKeyProfile>> _hotkeyProfilesProvider;
    public bool IsHookActive => _lowLevelKeyboardHook.IsStarted;
    public event Action<HotKeyEventArgs>? OnHotKeyProfileTriggered;

    public Keyboard(Func<IReadOnlyList<HotKeyProfile>> hotkeyProfilesProvider)
    {
        // A provider, not a collection: the hook thread must fetch the *current* immutable snapshot on
        // every event rather than capture a reference the UI thread may later replace or mutate (AUD-02).
        _hotkeyProfilesProvider = hotkeyProfilesProvider ?? throw new ArgumentNullException(nameof(hotkeyProfilesProvider));
        _lowLevelKeyboardHook = new LowLevelKeyboardHook { Handling = true };
        _lowLevelKeyboardHook.Down += LowLevelKeyboardHook_Down;
    }

    public void StartHook() => _lowLevelKeyboardHook.Start();
    public void StopHook() => _lowLevelKeyboardHook.Stop();

    private void LowLevelKeyboardHook_Down(object? sender, KeyboardEventArgs e)
    {
        var handler = OnHotKeyProfileTriggered;
        if (handler == null) return;

        // Fetch the snapshot ONCE for the whole callback. The provider returns the array the UI thread
        // swapped in atomically, so this loop can never observe a half-cleared list (AUD-02).
        IReadOnlyList<HotKeyProfile> hotkeyProfiles;
        try
        {
            hotkeyProfiles = _hotkeyProfilesProvider();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Keyboard profile provider failed: {ex}");
            return;
        }

        bool? isFileExplorerForeground = null;
        nint handle = 0;

        // This body runs inside a WH_KEYBOARD_LL native callback where a stray exception is undefined
        // behaviour — contain the loop so nothing can escape it (AUD-02).
        try
        {
            foreach (var profile in hotkeyProfiles)
            {
                // Skip disabled, empty or mouse
                if (!profile.IsEnabled || profile.IsMouse || profile.HotKeys is null || profile.HotKeys.Length == 0)
                    continue;

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

                // Set handled value.
                e.IsHandled = profile.IsHandled;

                // Queue the hotkey trigger in a separate thread.
                ThreadPool.QueueUserWorkItem(static s => s.Handler.Invoke(new HotKeyEventArgs(s.Profile, s.Handle)),
                    new State(handler, profile, handle), false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Keyboard hook iteration failed: {ex}");
        }
    }

    public void Dispose()
    {
        StopHook();
        _lowLevelKeyboardHook.Dispose();
    }

    private readonly struct State(Action<HotKeyEventArgs> handler, HotKeyProfile profile, nint handle)
    {
        public readonly Action<HotKeyEventArgs> Handler = handler;
        public readonly HotKeyProfile Profile = profile;
        public readonly nint Handle = handle;
    }
}
