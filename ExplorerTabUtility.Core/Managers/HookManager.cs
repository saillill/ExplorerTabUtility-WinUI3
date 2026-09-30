using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using ExplorerTabUtility.Abstractions;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Hooks;
using ExplorerTabUtility.WinAPI;

namespace ExplorerTabUtility.Managers;

/// <summary>
/// Owns the three hook implementations and turns triggered hotkey profiles into actions.
/// <para>
/// UI interaction is routed through abstractions: work is marshalled with
/// <see cref="IUiDispatcher"/> instead of <c>SynchronizationContext</c>, session shutdown comes
/// from <see cref="IUiDispatcher.SessionEnding"/> instead of
/// <c>Application.Current.SessionEnding</c>, and the tab-search picker is requested via
/// <see cref="OnTabSearchRequested"/> so the shell owns the actual window.
/// </para>
/// </summary>
public sealed class HookManager
{
    private readonly Mouse _mouseHook = null!;
    private readonly Keyboard _keyboardHook = null!;
    private readonly ExplorerWatcher _windowHook = null!;
    private readonly IUiDispatcher _uiDispatcher;
    private bool _disposed;

    public event Action? OnVisibilityToggled;
    public event Action? OnWindowHookToggled;
    public event Action? OnReuseTabsToggled;
    public event Action? OnShellInitialized;

    /// <summary>
    /// Raised on the UI thread when the tab-search hotkey fires. The UI shell shows its own picker.
    /// </summary>
    public event Action? OnTabSearchRequested;

    /// <summary>The window watcher, exposed so shells can drive tab search / switching.</summary>
    public ExplorerWatcher WindowHook => _windowHook;

    public HookManager(ProfileManager profileManager, IDialogService dialogService, IUiDispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));

        try
        {
            _windowHook = new ExplorerWatcher(dialogService);
            // Pass the snapshot *provider*, not a materialised list: the hook threads then always read the
            // most recent immutable snapshot the UI thread published (AUD-02).
            _mouseHook = new Mouse(profileManager.GetProfilesSnapshot);
            _keyboardHook = new Keyboard(profileManager.GetProfilesSnapshot);

            profileManager.KeybindingsHookStarted += KeybindingStarted;
            profileManager.KeybindingsHookStopped += KeybindingStopped;
            _keyboardHook.OnHotKeyProfileTriggered += OnHotKeyProfileTriggered;
            _mouseHook.OnHotKeyProfileTriggered += OnHotKeyProfileTriggered;

            // ExplorerWatcher raises this from a System.Threading.Timer callback, i.e. a thread-pool
            // thread. WinUI 3 XAML objects are strictly single-threaded, so every UI-facing event must
            // be marshalled — touching a control off-thread throws a COMException that kills the
            // process (it arrives on a pool thread, so it cannot even be marked handled).
            // TryPost: this is a UI notification, not shutdown-critical work (see IUiDispatcher).
            _windowHook.OnShellInitialized += () => _uiDispatcher.TryPost(() => OnShellInitialized?.Invoke());

            _uiDispatcher.SessionEnding += (_, _) => Dispose();
        }
        catch
        {
            // Roll back whatever was already constructed: a half-built manager that escapes the
            // AppServices guard would leave an ExplorerWatcher (process watcher, 1s shell-discovery
            // timer, later the global COM/WinEvent hooks) running with no owner to ever dispose it.
            SafeDispose(() => _keyboardHook?.Dispose());
            SafeDispose(() => _mouseHook?.Dispose());
            SafeDispose(() => _windowHook?.Dispose());
            throw;
        }
    }

    public void StartMouseHook() => ChangeHookStatus(_mouseHook, true);
    public void StopMouseHook() => ChangeHookStatus(_mouseHook, false);
    public void StartKeyboardHook() => ChangeHookStatus(_keyboardHook, true);
    public void StopKeyboardHook() => ChangeHookStatus(_keyboardHook, false);
    public void StartWindowHook() => ChangeHookStatus(_windowHook, true);
    public void StopWindowHook() => ChangeHookStatus(_windowHook, false);
    public void SetReuseTabs(bool value) => _windowHook.SetReuseTabs(value);

    private async void OnHotKeyProfileTriggered(HotKeyEventArgs e)
    {
        // An async void handler with no guard takes the entire process down if any action throws
        // (an invalid path, a COM failure, a denied window operation...). Failures are contained and
        // reported instead.
        try
        {
            await HandleHotKeyProfileTriggeredAsync(e);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HotKey action failed: {ex}");
        }
    }

    private async Task HandleHotKeyProfileTriggeredAsync(HotKeyEventArgs e)
    {
        switch (e.Profile.Action)
        {
            case HotKeyAction.Open:
                await _windowHook.Open(e.Profile.Path, e.Profile.IsAsTab, e.ForegroundWindow, e.Profile.Delay);
                break;

            case HotKeyAction.Duplicate:
                await _windowHook.DuplicateActiveTab(e.ForegroundWindow, e.Profile.IsAsTab);
                break;

            case HotKeyAction.ReopenClosed:
                await _windowHook.ReopenClosedTab(e.Profile.IsAsTab, e.ForegroundWindow);
                break;

            case HotKeyAction.DetachTab:
                await _windowHook.DetachCurrentTab(e.ForegroundWindow);
                break;

            case HotKeyAction.SetTargetWindow:
                _windowHook.SetTargetWindow(e.ForegroundWindow);
                break;

            case HotKeyAction.NavigateBack:
                NavigateBackForward(e.ForegroundWindow, e.MousePosition, isForward: false);
                break;

            case HotKeyAction.NavigateUp:
                NavigateUp(e.ForegroundWindow, e.MousePosition);
                break;

            case HotKeyAction.NavigateForward:
                NavigateBackForward(e.ForegroundWindow, e.MousePosition, isForward: true);
                break;

            case HotKeyAction.ToggleReuseTabs:
                _uiDispatcher.TryPost(() => OnReuseTabsToggled?.Invoke());
                break;

            case HotKeyAction.ToggleWinHook:
                _uiDispatcher.TryPost(() => OnWindowHookToggled?.Invoke());
                break;

            case HotKeyAction.ToggleVisibility:
                _uiDispatcher.TryPost(() => OnVisibilityToggled?.Invoke());
                break;

            case HotKeyAction.TabSearch:
                _uiDispatcher.TryPost(() => OnTabSearchRequested?.Invoke());
                break;

            case HotKeyAction.SnapRight:
            case HotKeyAction.SnapLeft:
            case HotKeyAction.SnapUp:
            case HotKeyAction.SnapDown:
                await SnapForegroundWindow(e.Profile.Action, e.Profile.Delay);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(e.Profile.Action),
                    e.Profile.Action,
                    @"Invalid profile action");
        }
    }

    private void KeybindingStarted()
    {
        StopMouseHook();
        StopKeyboardHook();
    }

    private void KeybindingStopped()
    {
        if (SettingsManager.IsMouseHookActive) StartMouseHook();
        if (SettingsManager.IsKeyboardHookActive) StartKeyboardHook();
    }

    private void NavigateBackForward(nint foregroundWindow, PixelPoint? mousePosition, bool isForward)
    {
        if (foregroundWindow == 0) return;

        if (mousePosition is not { } position)
            _windowHook.NavigateBackForward(foregroundWindow, isForward);
        else if (Helper.IsExplorerEmptySpace(position))
            KeyboardSimulator.ModifiedKeyStroke(VirtualKey.Alt, isForward ? VirtualKey.Right : VirtualKey.Left);
    }

    private void NavigateUp(nint foregroundWindow, PixelPoint? mousePosition)
    {
        if (foregroundWindow == 0) return;

        if (mousePosition is not { } position)
            KeyboardSimulator.ModifiedKeyStroke(VirtualKey.Alt, VirtualKey.Up);
        else if (Helper.IsExplorerEmptySpace(position))
            KeyboardSimulator.ModifiedKeyStroke(VirtualKey.Alt, VirtualKey.Up);
    }

    private async Task SnapForegroundWindow(HotKeyAction direction, int delay = 0)
    {
        var snapKey = GetSnapKey(direction);
        if (snapKey == VirtualKey.None) return;

        if (delay > 0)
            await Task.Delay(delay);

        var inputs = new List<INPUT>();
        // Remove any currently pressed modifiers (Ctrl, Shift, Alt, Win)
        inputs.AddUpEventsForCurrentlyPressedModifiers();

        // Press Windows key
        inputs.AddKeyDown(VirtualKey.LWin);

        // For up and down press Alt key
        if (snapKey is VirtualKey.Up or VirtualKey.Down)
            inputs.AddKeyDown(VirtualKey.Alt);

        // Press the snap key
        inputs.AddKeyPress(snapKey);

        // For up and down release Alt key
        if (snapKey is VirtualKey.Up or VirtualKey.Down)
            inputs.AddKeyUp(VirtualKey.Alt);

        // Release Windows key
        inputs.AddKeyUp(VirtualKey.LWin);

        // Re-add any currently pressed modifiers
        inputs.AddDownEventsForCurrentlyPressedModifiers();

        KeyboardSimulator.SendInputs(inputs.ToArray());
    }

    private static VirtualKey GetSnapKey(HotKeyAction direction)
    {
        return direction switch
        {
            HotKeyAction.SnapRight => VirtualKey.Right,
            HotKeyAction.SnapLeft => VirtualKey.Left,
            HotKeyAction.SnapUp => VirtualKey.Up,
            HotKeyAction.SnapDown => VirtualKey.Down,
            _ => VirtualKey.None
        };
    }

    private static void ChangeHookStatus(IHook hook, bool isActive)
    {
        if (hook.IsHookActive == isActive) return;

        if (isActive)
            hook.StartHook();
        else
            hook.StopHook();
    }

    public void Dispose()
    {
        // Idempotent and fault-tolerant: SessionEnding (a SystemEvents thread) and the UI-exit path both
        // reach here, and the SessionEnding lambda has no try/catch of its own — an exception escaping
        // it during logoff would surface as a shutdown error (AUD-04).
        if (_disposed) return;
        _disposed = true;

        SafeDispose(_keyboardHook.Dispose);
        SafeDispose(_mouseHook.Dispose);
        SafeDispose(_windowHook.Dispose);
    }

    private static void SafeDispose(Action dispose)
    {
        try { dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"HookManager.Dispose failed: {ex}"); }
    }
}
