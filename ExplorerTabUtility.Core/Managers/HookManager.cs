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
    /// Raised after the interception hook has been started or stopped — from the tray menu, the settings
    /// page or a hotkey. Whoever shows the switch reads the current state when it hears this, rather than
    /// trying to remember what it last set.
    /// </summary>
    public event Action? WindowInterceptionChanged;

    /// <summary>Raised after the tab-reuse switch has been applied, from wherever it was flipped.</summary>
    public event Action? ReuseTabsChanged;

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

    /// <summary>
    /// Applies the window-interception switch, whoever asked for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interception and tab reuse are two switches over one behaviour: a new single-tab window is folded
    /// while <em>either</em> is on (<c>ExplorerWatcher</c> tests <c>_isForcingTabs || _reuseTabs</c>). So
    /// switching interception off has to stop reuse as well — otherwise the switch reads "off" and every
    /// window is still being folded, which is exactly how this looked from the outside — and switching it
    /// back on restores the reuse setting the user had.
    /// </para>
    /// <para>
    /// The rule lives here rather than in the menu that happened to own it, because the tray menu, the
    /// settings page and the hotkey action are three ways to flip one switch and each could previously
    /// disagree with the others about what "off" means.
    /// </para>
    /// </remarks>
    public void SetWindowInterception(bool enabled)
    {
        if (enabled)
        {
            SettingsManager.IsWindowHookActive = true;
            ChangeHookStatus(_windowHook, true);

            if (_reuseTabsBeforeInterceptionStopped)
            {
                SettingsManager.ReuseTabs = true;
                SetReuseTabs(true);
            }
        }
        else
        {
            _reuseTabsBeforeInterceptionStopped = SettingsManager.ReuseTabs;

            SettingsManager.IsWindowHookActive = false;
            ChangeHookStatus(_windowHook, false);

            if (SettingsManager.ReuseTabs) SetReuseTabs(false);
        }

        WindowInterceptionChanged?.Invoke();
    }

    /// <summary>Applies the tab-reuse switch, whoever asked for it.</summary>
    /// <remarks>
    /// Reuse is a request for the interception that does the folding, so switching it on switches
    /// interception on: without that hook running there is nothing to fold a window with, and a switch that
    /// changes nothing is worse than no switch. The reverse is deliberate in
    /// <see cref="SetWindowInterception"/> — interception off implies reuse off — and is not mirrored here:
    /// switching reuse off leaves interception alone, because the user asked for one thing, not both.
    /// </remarks>
    public void SetReuseTabs(bool enabled)
    {
        SettingsManager.ReuseTabs = enabled;
        _windowHook.SetReuseTabs(enabled);
        ReuseTabsChanged?.Invoke();

        if (enabled && !SettingsManager.IsWindowHookActive) SetWindowInterception(true);
    }

    /// <summary>
    /// The reuse setting to restore when interception is switched back on. Remembered here because
    /// interception turning reuse off is this class's own doing, not the user's choice.
    /// </summary>
    private bool _reuseTabsBeforeInterceptionStopped;

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
