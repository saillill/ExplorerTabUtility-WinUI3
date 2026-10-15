using System;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ExplorerTabUtility.Abstractions;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Interop;
using ExplorerTabUtility.Interop.Com;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.WinAPI;

namespace ExplorerTabUtility.Hooks;

using WindowEntry = DualKeyEntry<ExplorerWindow, nint?, WindowInfo>;

/// <summary>
/// Watches Explorer windows and folds new windows into tabs.
/// <para>
/// The Shell COM surface this class drives is now provided by
/// <see cref="Interop.Com"/>: <see cref="ShellWindows"/> and <see cref="ExplorerWindow"/> are
/// hand-written dispatch wrappers, so the project no longer needs a type-library import
/// (tlbimp), a Visual Studio install, or the <c>net481</c> target.
/// </para>
/// <para>
/// The only remaining UI dependency was the "restore previous windows?" prompt; it now goes
/// through <see cref="IDialogService"/>.
/// </para>
/// </summary>
public class ExplorerWatcher : IHook
{
    private static bool _instanceRunning;

    private ShellWindows _shellWindows = null!;
    private ShellPathComparer _shellPathComparer = null!;
    private StaTaskScheduler _staTaskScheduler = null!;
    private nint _mainWindowHandle;
    private readonly ConcurrentDictionary<nint, byte> _processedHWnds = new();
    private readonly DualKeyDictionary<ExplorerWindow, nint?, WindowInfo> _windowEntryDict = [];
    private readonly List<WindowRecord> _closedWindows = new();

    /// <summary>The restore offer runs at most once per session.</summary>
    private bool _restorePrompted;
    private readonly object _windowEntryDictLock = new(), _closedWindowsLock = new(), _processLock = new();
    private readonly SemaphoreSlim _toOpenWindowsLock = new(1);
    private readonly ProcessWatcher _processWatcher;
    private readonly IDialogService _dialogService;
    private int _mainExplorerProcessId;
    private Timer? _explorerCheckTimer;

    /// <summary>
    /// Set once <see cref="Dispose"/> runs. Reads from the timer callback and the Shell COM event
    /// threads, so it must be volatile. Guards <see cref="InitializeShellObjects"/> against being
    /// resurrected by a timer tick that fired after the watcher was disposed.
    /// </summary>
    private volatile bool _disposed;

    /// <summary>True between a successful <see cref="InitializeShellObjects"/> and its teardown.</summary>
    private bool _shellInitialized;

    /// <summary>Guards the resume rebuild against a second <c>PowerModeChanged</c> overlapping it.</summary>
    private int _resumeRebuildInProgress;

    /// <summary>Set by <see cref="OnPowerModeChanged"/>; read by the STA thread that rebuilds the shell.</summary>
    private volatile bool _shellRebuildRequested;

    private nint _eventObjectShowHookId;
    private WinEventDelegate? _eventObjectShowHookCallback;
    private Action<int>? _windowRegisteredHandler;

    private string _defaultLocation = null!;
    private bool _reuseTabs = true;
    // Toggled from the UI thread, read from the WinEvent hook thread; volatile keeps the hook from
    // acting on a stale value (AUD-23).
    private volatile bool _isForcingTabs;
    public bool IsHookActive => _isForcingTabs;
    public event Action? OnShellInitialized;

    public ExplorerWatcher(IDialogService dialogService)
    {
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

        if (_instanceRunning)
            throw new InvalidOperationException("Only one instance of ExplorerWatcher is allowed at a time.");
        _instanceRunning = true;

        _processWatcher = new ProcessWatcher("explorer");
        _processWatcher.ProcessTerminated += OnExplorerProcessTerminated;
        StartExplorerProcessCheck();

        // Power events are the only notification that the shell COM objects below have gone stale without
        // our process learning about it any other way; see OnPowerModeChanged (upstream #137).
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void StartHook()
    {
        if (_isForcingTabs) return;
        _isForcingTabs = true;
    }

    public void StopHook()
    {
        if (!_isForcingTabs) return;
        _isForcingTabs = false;
    }
    public void SetReuseTabs(bool reuseTabs) => _reuseTabs = reuseTabs;

    public void ClearClosedWindows()
    {
        lock (_closedWindowsLock)
            _closedWindows.Clear();
    }

    /// <summary>
    /// Number of tracked windows that still exist, pruning entries whose HWND Explorer has already
    /// destroyed. Callers must use this for liveness tests, never <c>_windowEntryDict.Count</c>.
    /// </summary>
    /// <remarks>
    /// <c>Count</c> answers "how many windows have we ever tracked and not yet unhooked", which is not
    /// "how many windows are on screen". Explorer can destroy a window without the unhook running first —
    /// a crash, a forced close, a shell restart after resume — leaving a dead entry behind. Because
    /// <c>CabinetWClass</c> handles are recycled, that dead entry can even compare equal to a live window
    /// later. Every decision of the form "are there other windows?" was reading <c>Count</c> and so could
    /// believe in windows that no longer exist: <c>OpenTabNavigateWithSelection</c> then takes the
    /// "reuse an existing tab" branch against a ghost and drops the user's navigation entirely (upstream
    /// #137). Pruning here makes the count a fact instead of an estimate.
    /// </remarks>
    private int GetLiveWindowCount()
    {
        ExplorerWindow[] candidates;
        lock (_windowEntryDictLock)
            candidates = _windowEntryDict.Keys.ToArray();

        foreach (var window in candidates)
            if (!IsWindowAlive(window))
                RemoveWindowEntry(window);

        lock (_windowEntryDictLock)
            return _windowEntryDict.Count;
    }

    /// <summary>
    /// Whether an <see cref="ExplorerWindow"/> still refers to a live top-level window.
    /// </summary>
    /// <remarks>
    /// The HWND test alone is not enough: <c>IsWindow</c> is satisfied by any window that has taken over
    /// the recycled handle, including one that is not an Explorer window at all, so the class name is
    /// checked too. Reading <c>window.HWND</c> goes through COM and is wrapped because the object itself
    /// can already be detached, in which case it is equally dead to us.
    /// </remarks>
    private static bool IsWindowAlive(ExplorerWindow window)
    {
        nint hWnd;
        try
        {
            hWnd = new IntPtr(window.HWND);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"IsWindowAlive: reading HWND failed, treating as dead: {ex.Message}");
            return false;
        }

        return hWnd != 0 && WinApi.IsWindow(hWnd) && WinApi.IsWindowHasClassName(hWnd, "CabinetWClass");
    }

    /// <summary>
    /// Drops a window from the tracking table, tolerating the not-present case.
    /// </summary>
    /// <remarks>
    /// <see cref="DualKeyDictionary{TKey,TKey2,TValue}"/> has a two-key identity, so evicting through one
    /// key alone removes it from <c>Keys</c> while a stale secondary mapping can survive; both the HWND and
    /// the tab-handle key are therefore removed when they are known. Removal is deliberately silent when
    /// the entry is already gone — pruning races with the normal unhook path and must never throw.
    /// </remarks>
    private void RemoveWindowEntry(ExplorerWindow window)
    {
        try
        {
            if (_windowEntryDict.TryGetValue(window, out WindowEntry entry) && entry.OptionalKey is { } tabHandle and > 0)
                _windowEntryDict.Remove(tabHandle);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RemoveWindowEntry: secondary-key removal failed: {ex.Message}");
        }

        lock (_windowEntryDictLock)
            _windowEntryDict.Remove(window);
    }

    public IReadOnlyCollection<WindowRecord> GetWindows()
    {
        // Take a snapshot under the lock, then read each window's location / selection OUTSIDE it.
        // Those reads are blocking COM calls (LocationURL, SelectedItems) against Explorer windows
        // that may be busy or unresponsive; doing them while holding the dictionary lock stalls every
        // other watcher operation behind a single slow window (AUD-01).
        WindowEntry[] entries;
        lock (_windowEntryDictLock)
            entries = _windowEntryDict.ToArray<WindowEntry>();

        var result = new List<WindowRecord>();

        // Add open windows
        foreach (var (window, windowInfo, tabHandle) in entries)
        {
            try
            {
                result.Add(new WindowRecord(GetLocation(window), new IntPtr(window.HWND), GetSelectedItems(window), window.LocationName));
            }
            catch
            {
                // The window may have been destroyed between the snapshot and this read; skip it.
            }
        }

        // Add closed windows in reverse order (last closed on top)
        lock (_closedWindowsLock)
            result.AddRange(_closedWindows.AsEnumerable().Reverse());

        return result.GroupBy(w => w.Location).Select(g => g.First()).ToList();
    }

    public async Task SwitchTo(string location, nint windowHandle = 0, string[]? selectedItems = null, bool asTab = true, bool duplicate = false)
    {
        var windowToOpen = new WindowRecord(location, windowHandle, selectedItems);
        if (!asTab)
        {
            await OpenNewWindowWithSelection(windowToOpen);
            return;
        }

        await OpenTabNavigateWithSelection(windowToOpen, windowHandle, duplicate, true);
    }

    public nint SearchForTab(string targetPath)
    {
        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer.GetPidlFromPath(targetPath);
            if (targetPidl == 0) return 0;

            foreach (var (window, windowInfo, tabHandle) in _windowEntryDict)
            {
                // Make sure it is not the newly created window
                if (!Helper.IsTimeUp(windowInfo.CreatedAt, 2_000) || !tabHandle.HasValue || tabHandle.Value == 0)
                    continue;

                var comparePath = windowInfo.Location ?? GetLocation(window);

                if (_shellPathComparer.IsEquivalent(targetPath, comparePath, targetPidl))
                    return tabHandle.Value;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (targetPidl != 0)
                Marshal.FreeCoTaskMem(targetPidl);
        }
    }
    public async Task SelectTabByHandle(nint windowHandle, nint tabHandle)
    {
        var tabs = Helper.GetAllExplorerTabs(windowHandle).ToArray();
        if (tabs.Length == 0) return;

        var activeTab = tabs[0];
        for (var i = 0; i < tabs.Length; i++)
        {
            if (activeTab == tabHandle) break;

            SelectTabByIndex(windowHandle, i);

            // ReSharper disable once AccessToModifiedClosure
            activeTab = await Helper.DoUntilConditionAsync(
                () => WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null),
                h => h != activeTab);
        }
    }
    public void SelectLastTab(nint windowHandle)
    {
        var count = Helper.GetAllExplorerTabs(windowHandle).Count();

        // No tabs (mid switch/close): count - 1 would be -1 and lParam 0 is a meaningless index.
        if (count <= 0) return;

        SelectTabByIndex(windowHandle, count - 1);
    }
    public void SelectTabByIndex(nint windowHandle, int index)
    {
        // Send 0xA221 magic command (CTRL + 1...n)
        WinApi.SendMessage(windowHandle, WinApi.WM_COMMAND, 0xA221, index + 1);
    }
    public async Task RequestToOpenNewTab(nint windowHandle, bool bringToFront = false, bool lockToOpenWindows = true)
    {
        if (bringToFront && windowHandle == 0)
            windowHandle = GetMainWindowHWnd(0);

        if (windowHandle == 0)
        {
            await OpenNewWindowWithSelection(new WindowRecord(string.Empty), lockToOpenWindows);
            return;
        }

        var tabHandle = WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
        if (tabHandle == 0) return;

        // Send 0xA21B magic command (CTRL + T)
        WinApi.PostMessage(tabHandle, WinApi.WM_COMMAND, 0xA21B, 0);

        if (bringToFront)
            WinApi.RestoreWindowToForeground(windowHandle);
    }
    public async Task Open(string? location, bool asTab, nint windowHandle, int delay = 0)
    {
        if (delay > 0)
            await Task.Delay(delay);

        var normalizedPath = Helper.NormalizeLocation(location ?? string.Empty);

        if (normalizedPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
            System.IO.File.Exists(normalizedPath))
        {
            try
            {
                Helper.BypassWinForegroundRestrictions();
                Process.Start(new ProcessStartInfo(normalizedPath) { UseShellExecute = true });
                return;
            }
            catch
            {
                //
            }
        }

        if (!asTab)
        {
            await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
            return;
        }

        if (string.IsNullOrWhiteSpace(normalizedPath) && !_reuseTabs)
        {
            await RequestToOpenNewTab(windowHandle, bringToFront: true);
            return;
        }

        // Liveness, not bookkeeping: a leftover entry for a destroyed window must not route this down
        // the "there is a window to put the tab in" path (upstream #137).
        if (GetLiveWindowCount() > 0)
        {
            OpenNewTab(windowHandle, normalizedPath);
            return;
        }

        await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
    }
    public void OpenNewTab(nint windowHandle, string location)
    {
        // Nothing depends on the outcome here: the target window is the caller's own, it is not hidden and
        // nothing is destroyed, so a failure just means the tab did not open. The task is still observed
        // (a discarded faulted task would otherwise surface as an unobserved-exception event).
        _ = OpenTabNavigateWithSelection(new WindowRecord(location, windowHandle), windowHandle)
            .ContinueWith(t => Debug.WriteLine($"OpenNewTab: failed for {location}: {t.Exception?.GetBaseException().Message}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
    public async Task DuplicateActiveTab(nint windowHandle, bool asTab)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        if (window == null) return;

        var location = GetKnownLocation(window);
        var selectedItems = GetSelectedItems(window);
        var windowRecord = new WindowRecord(location, windowHandle, selectedItems);

        if (!asTab)
        {
            await OpenNewWindowWithSelection(windowRecord);
            return;
        }

        await OpenTabNavigateWithSelection(windowRecord, windowHandle, isDuplicate: true);
    }
    public async Task ReopenClosedTab(bool asTab, nint windowHandle = 0)
    {
        WindowRecord? closedWindow;
        lock (_closedWindowsLock)
        {
            // Walk from the newest record: default-location entries (Home / This PC) are not worth
            // reopening, but merely skipping them with LastOrDefault left them at the tail of the list
            // forever, shadowing the real history behind them (AUD-29). Drop them as we pass over them.
            closedWindow = null;
            for (var i = _closedWindows.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_closedWindows[i].Location, _defaultLocation, StringComparison.OrdinalIgnoreCase))
                {
                    _closedWindows.RemoveAt(i);
                    continue;
                }

                closedWindow = _closedWindows[i];
                _closedWindows.RemoveAt(i);
                break;
            }

            if (closedWindow == null) return;
        }

        if (!asTab)
        {
            closedWindow.CreatedAt = Environment.TickCount64;
            await OpenNewWindowWithSelection(closedWindow);
            return;
        }

        await OpenTabNavigateWithSelection(closedWindow, windowHandle);
    }
    public async Task DetachCurrentTab(nint windowHandle)
    {
        if (Helper.GetAllExplorerTabs(windowHandle).Take(2).Count() < 2)
            return;

        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        if (window == null) return;

        var location = GetKnownLocation(window);
        var selectedItems = GetSelectedItems(window);
        var windowRecord = new WindowRecord(location, windowHandle, selectedItems);

        // Send 0xA021 magic command (CTRL + W)
        WinApi.SendMessage(activeTabHandle, WinApi.WM_COMMAND, 0xA021, 1);

        await OpenNewWindowWithSelection(windowRecord);
    }
    public void SetTargetWindow(nint windowHandle)
    {
        if (Helper.IsFileExplorerWindow(windowHandle))
            _mainWindowHandle = windowHandle;
    }
    public void NavigateBackForward(nint windowHandle, bool isForward)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        try
        {
            if (isForward) window?.GoForward();
            else window?.GoBack();
        }
        catch
        {
            // Will throw if there is no further history
        }
    }

    private void PreventWindowHiding(nint hWnd)
    {
        if (_processedHWnds.TryAdd(hWnd, 0))
        {
            // Schedule removal after a short delay
            _ = Task.Delay(7_000).ContinueWith(t => _processedHWnds.TryRemove(hWnd, out _), TaskScheduler.Default);
        }
    }
    private void OnWindowShown(nint hWinEventHook, uint eventType, nint hWnd, int idObject, int idChild, uint dwEventThread, uint dWmsEventTime)
    {
        // Hiding a window only ever serves the fold, so it is the fold's own switch that decides. Gating this
        // on interception instead is how a window could be hidden with nothing left to convert it into —
        // one way an Explorer window ends up invisible while still counting as open.
        if (!_reuseTabs || idObject != 0 || idChild != 0) return;

        // Check if the hWnd was processed by OnShellWindowRegistered
        if (_processedHWnds.TryRemove(hWnd, out _)) return;

        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass")) return;

        if (GetLiveWindowCount() < 2 || Helper.IsCtrlShiftDown()) return;
        Helper.HideWindow(hWnd);

        // Mirror OnShellWindowRegistered: schedule the delayed cache eviction afterwards. The two
        // "hide a window" entry points must have symmetric cleanup — without it here the entry stays
        // in Helper.HiddenWindows forever, the window (alpha 0) can never be recovered, and the cache
        // grows without bound over a long session (AUD-10).
        _ = Task.Delay(3000).ContinueWith(t => Helper.HiddenWindows.TryRemove(hWnd, out _), TaskScheduler.Default);
    }
    private ExplorerWindow? GetRecentlyCreatedWindow(out WindowInfo? windowInfo)
    {
        // When a new window is registered, it's typically the last in the collection
        var count = _shellWindows.Count;
        for (var i = count - 1; i >= 0; i--)
        {
            var window = _shellWindows.Item(i);
            if (window is null) continue;

            // Blocking COM property-bag reads stay OUTSIDE _windowEntryDictLock — the same rule
            // GetWindows follows (AUD-01): a hung window must not stall every other watcher
            // operation while the lock is held.
            if (window.GetProperty("seenBefore") is not null) continue;

            lock (_windowEntryDictLock)
            {
                if (_windowEntryDict.Keys.Contains(window)) continue;

                windowInfo = new WindowInfo();

                // TryAdd, not Add: the Contains check above and this insert are not atomic, and
                // InitializeShellObjects inserts without taking _windowEntryDictLock. Losing that race
                // must skip the window, not throw ArgumentException out of a COM event callback (AUD-01).
                if (!_windowEntryDict.TryAdd(window, windowInfo)) continue;

                if (_windowEntryDict.Count == 1)
                    _mainWindowHandle = new IntPtr(window.HWND);
            }

            // Mark only after winning the add: the property bag is the cross-thread guard for the
            // lock-free pre-check above. A thread that lost the TryAdd race must NOT mark the window,
            // otherwise the winner's window could be skipped by a later scan before it is in the dict.
            window.PutProperty("seenBefore", true);

            // Outside the lock: MaybeRestore takes _closedWindowsLock, and PersistWindows takes the
            // two locks in the opposite order.
            MaybeRestorePreviousWindows();
            return window;
        }

        windowInfo = null;
        return null;
    }
    private async void OnShellWindowRegistered(int cookie)
    {
        var showAgain = true;
        nint hWnd = 0;
        try
        {
            var shouldOpenAsWindow = Helper.IsCtrlShiftDown();

            WindowInfo windowInfo = null!;
            var window = await Helper.DoUntilNotDefaultAsync(() => GetRecentlyCreatedWindow(out windowInfo!), 2_500, 70);
            if (window == null) return;

            _ = GetTabHandle(window);

            hWnd = new IntPtr(window.HWND);

            if (shouldOpenAsWindow)
            {
                PreventWindowHiding(hWnd);
                HookWindowEvents(window, windowInfo);
                return;
            }

            var location = GetLocation(window);

            //Control Panel
            if (location.StartsWith("shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}"))
            {
                PreventWindowHiding(hWnd);
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            // A tab the user pulled out of another window has to survive, and that question has to be
            // asked here — before the window is hidden and before the fold is decided.
            //
            // The order is the whole point, and it used to be the other way round: every record for this
            // location was dropped first ("race condition fix: OnQuit may add it async") and the lookup
            // ran afterwards, so it could only ever match a record that arrived in between — never the one
            // that was already there. Detaching does the opposite: the tab goes away first and the window
            // appears second, so the record was always already there, always deleted unread, and the window
            // the user had just pulled out was folded straight back into a tab. It was also hidden before
            // this point and had to be brought back by the finally block, so even a window that survived
            // flickered on its way out of the fold.
            // The record also has to be the right *kind* of record. "A location was released and came back"
            // is equally true of a window that was closed and reopened, or of the app restoring the windows
            // it remembered — and only a tab pulled out of a window that kept its other tabs is a statement
            // about where the folder belongs. Without this test the app wrote folders down that nobody had
            // asked it to keep, which is how a restored window put an entry in the user's list.
            var isDetachedTab = TryGetRecentlyClosedWindow(location, out var closedWindow) &&
                                closedWindow!.WasDetachedTab;
            if (isDetachedTab)
            {
                SelectItems(window, closedWindow!.SelectedItems);

                // A folder the user pulled out of a window is a folder they want in its own window, and that
                // is a decision about the folder rather than about this one window — so it is written down
                // where the user can see and undo it (the list on the settings page, and the tray item).
                // Without this the exemption lasted two seconds and the folder was folded again the next time
                // it was opened, which reads as "the app forgot what I just told it".
                MarkNoMergeFolder(location);
            }

            // A folder the user has marked as one that always opens on its own is exempt by name,
            // instead of by the timing the detached-tab test above depends on.
            var isMarkedFolder = IsNoMergeFolder(location);

            // Check if this is a single tab window and there are other windows
            var shouldReopenAsTab = !isDetachedTab &&
                                    !isMarkedFolder &&
                                    _reuseTabs &&
                                    GetLiveWindowCount() > 1 &&
                                    hWnd != _mainWindowHandle &&
                                    Helper.GetAllExplorerTabs(hWnd).Take(2).Count() == 1;

            if (shouldReopenAsTab)
                Helper.HideWindow(hWnd);
            else
                PreventWindowHiding(hWnd);

            // Drop whatever is left for this location. OnQuit can still arrive after this point, which is
            // what the second lookup below is for; a record that is still here now belongs to something
            // the shell is already done with, and the lookup above has consumed the one that mattered.
            lock (_closedWindowsLock)
                _closedWindows.RemoveAll(w => string.Equals(w.Location, location, StringComparison.OrdinalIgnoreCase));

            if (shouldReopenAsTab)
            {
                // Await the conversion before deciding the window's fate (upstream #137). This used to be
                // fire-and-forget with an unconditional `window.Quit()` right after, so any failure inside —
                // no new tab within the 2s budget, the window not appearing in the table, Navigate refused,
                // no NavigateComplete2 within 5s — destroyed a window that had already been hidden by
                // `Helper.HideWindow(hWnd)` above. The tab was never created and the window was gone: the
                // user's folder simply disappeared, with no way to get it back.
                var converted = await OpenTabNavigateWithSelection(new WindowRecord(location, hWnd, GetSelectedItems(window)), _mainWindowHandle);

                if (converted)
                {
                    showAgain = false;

                    window.Quit();
                    lock (_closedWindowsLock)
                        _closedWindows.RemoveAll(w => string.Equals(w.Location, location, StringComparison.OrdinalIgnoreCase));
                    RemoveWindowAndUnhookEvents(window, windowInfo);
                    return;
                }

                // Conversion failed. This must be the state the normal "not a tab-conversion" path ends in,
                // which is `showAgain` still true: the window was already hidden above, so the finally block
                // is the only thing that brings it back, and leaving `showAgain` false here would turn "the
                // tab was not created" into "and the window is gone too" — the very outcome being fixed.
                // `window.Quit()` is deliberately not called and the window stays hooked, so it is still a
                // fully functional Explorer window rather than one the watcher has stopped looking after.
                Debug.WriteLine($"OnShellWindowRegistered: could not convert {location} to a tab; restoring the window");
                return;
            }

            // OnQuit might fire after ShellWindowRegistered in case of reattached tab (and there were selected files).
            // This is the late half of the detached-tab question asked above — the record can land after the
            // window did. It only restores the selection now: whether the window was folded was already
            // decided, and a late record cannot fold a window that is already open on screen.
            if (!isDetachedTab)
            {
                isDetachedTab = await Helper.DoUntilNotDefaultAsync(() => TryGetRecentlyClosedWindow(location, out closedWindow), 700, 50);
                if (isDetachedTab)
                    SelectItems(window, closedWindow!.SelectedItems);
            }

            HookWindowEvents(window, windowInfo);
        }
        catch (Exception ex)
        {
            // `async void` COM event receiver: it runs on Explorer's own thread, so an exception that
            // escapes is a process-killer (Application.UnhandledException cannot see it). Log it
            // instead of silently swallowing (AUD-12).
            Debug.WriteLine($"OnShellWindowRegistered failed: {ex}");
        }
        finally
        {
            // Only a window that was actually hidden has anything to restore. `Helper.ShowWindow`
            // signals "nothing to do" with `false`, which is also the value DoUntilNotDefaultAsync reads
            // as "keep polling" — so on every path where the window was never hidden (a plain new window,
            // a Ctrl+Shift window, the Control Panel, or an early return where hWnd is still 0) the loop
            // below used to spin for its full 1.5 s before doing nothing.
            if (showAgain && Helper.HiddenWindows.ContainsKey(hWnd))
            {
                // The finally body itself is guarded: the await below can throw, and an exception
                // escaping a `finally` in an async void COM callback terminates the process (AUD-12).
                try
                {
                    await Helper.DoUntilNotDefaultAsync(() => Helper.ShowWindow(hWnd, removeCache: false), 1_500, 200);

                    Helper.UpdateWindowLayered(hWnd, remove: true);

                    // OnWindowShown might fire after ShellWindowRegistered and hide it again, keep the cache, wait a bit, then remove it.
                    _ = Task.Delay(3000).ContinueWith(t => Helper.HiddenWindows.TryRemove(hWnd, out _), TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OnShellWindowRegistered cleanup failed: {ex}");
                }
            }
        }
    }
    private void HookWindowEvents(ExplorerWindow window, WindowInfo windowInfo)
    {
        // Create strongly-typed handlers so we can remove them later
        windowInfo.OnQuitHandler = () =>
        {
            var location = windowInfo.Location ?? GetLocation(window);
            var locationName = windowInfo.Name ?? window.LocationName;
            var windowRecord = new WindowRecord(location, new IntPtr(window.HWND), name: locationName)
            {
                // A tab pulled out of a window leaves its window with the other tabs; a window that was
                // closed leaves nothing behind. Read *now*, while the window is still there to be asked.
                WasDetachedTab = Helper.GetAllExplorerTabs(new IntPtr(window.HWND)).Any()
            };
            lock (_closedWindowsLock) _closedWindows.Add(windowRecord);

            // Home, This PC, etc (compared the same case-insensitive way as every other
            // location comparison in this class)
            if (string.Equals(location, _defaultLocation, StringComparison.OrdinalIgnoreCase))
            {
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            windowRecord.SelectedItems = GetSelectedItems(window);
            RemoveWindowAndUnhookEvents(window, windowInfo);
        };

        if (SettingsManager.RestorePreviousWindows)
            windowInfo.OnNavigateHandler = (_, _) =>
            {
                windowInfo.Location = GetLocation(window);
                windowInfo.Name = window.LocationName;
            };

        try
        {
            // Subscribe
            window.OnQuit += windowInfo.OnQuitHandler;
            if (SettingsManager.RestorePreviousWindows)
            {
                windowInfo.Location = GetLocation(window);
                windowInfo.Name = window.LocationName;
                window.NavigateComplete2 += windowInfo.OnNavigateHandler;
            }

            // Make sure the window is still alive (User might have closed it immediately after opening it)
            _ = window.HWND;
        }
        catch
        {
            lock (_windowEntryDictLock)
                _windowEntryDict.Remove(window);

            // The window died mid-subscription. ExplorerWindow has no finalizer, so without an explicit
            // Dispose here the advised connection point and its COM reference leak forever (AUD-05).
            window.Dispose();
        }
    }
    private void RemoveWindowAndUnhookEvents(ExplorerWindow window, WindowInfo windowInfo, bool useLock = true)
    {
        // Unsubscribe
        if (windowInfo.OnQuitHandler != null) window.OnQuit -= windowInfo.OnQuitHandler;
        if (windowInfo.OnNavigateHandler != null) window.NavigateComplete2 -= windowInfo.OnNavigateHandler;

        // Remove from dictionary
        if (useLock)
        {
            lock (_windowEntryDictLock)
                _windowEntryDict.Remove(window);
        }
        else
            _windowEntryDict.Remove(window);

        // Finally, release the COM reference for this Explorer window
        window.Dispose();
    }

    /// <summary>
    /// Offers to reopen the recorded windows, at most once per session.
    /// <para>
    /// The previous trigger required <c>_windowEntryDict.Count == 1</c>, which never matched after an
    /// Explorer restart: <see cref="InitializeShellObjects"/> already hooks the window the shell
    /// recreates, so the count entered this method at 1 and became 2.
    /// </para>
    /// </summary>
    private void MaybeRestorePreviousWindows()
    {
        if (_restorePrompted) return;
        if (!SettingsManager.RestorePreviousWindows) return;

        lock (_closedWindowsLock)
        {
            if (_closedWindows.All(record => !record.Restore)) return;
        }

        _restorePrompted = true;
        _ = RestorePreviousWindows();
    }

    private async Task RestorePreviousWindows()
    {
        // Called as `_ = RestorePreviousWindows()`: the returned task is never awaited, so an escaped
        // exception would only show up as an unobserved-task event. Contain it here (AUD-14).
        try
        {
            // Standalone, not in-window: this asks about File Explorer's windows, and it arrives while
            // the app window may be hidden in the tray. Dragging that window on screen just to ask the
            // question is the wrong trade, so the prompt is a window of its own (the platform's message
            // box) — movable, and shown without surfacing anything.
            var result = await RunInStaThread(() => _dialogService.Show(
                LocalizationService.Get("RestoreWindowsPrompt"),
                Constants.AppName,
                DialogButton.YesNo,
                DialogIcon.Question,
                host: DialogHost.Standalone));

            // Snapshot under the lock, then iterate outside it: OnQuit and OnExplorerProcessTerminated
            // append to _closedWindows from other threads, and the previous unguarded foreach threw
            // InvalidOperationException mid-enumeration (AUD-14).
            WindowRecord[] toRestore;
            lock (_closedWindowsLock)
                toRestore = _closedWindows.Where(record => record.Restore).ToArray();

            foreach (var record in toRestore)
            {
                record.Restore = false;

                if (result != DialogResult.Yes) continue;

                // Deliberately not awaited: each restore takes the tab lock in turn and the window has to
                // stay responsive while they run. Observed for faults so a failing restore is logged rather
                // than swallowed (a bare `_ =` on a task that throws raises UnobservedTaskException).
                _ = OpenTabNavigateWithSelection(record)
                    .ContinueWith(t => Debug.WriteLine($"RestorePreviousWindows: failed for {record.Location}: {t.Exception?.GetBaseException().Message}"),
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RestorePreviousWindows failed: {ex}");
        }
    }
    private async Task OpenNewWindowWithSelection(WindowRecord windowToOpen, bool duplicate = true, bool lockToOpenWindows = true)
    {
        if (lockToOpenWindows)
            await _toOpenWindowsLock.WaitAsync();

        try
        {
            lock (_closedWindowsLock)
                _closedWindows.Add(windowToOpen);

            var hasSelection = windowToOpen.SelectedItems?.Length > 0;

            nint[]? currentWindows = null;
            if (hasSelection)
                currentWindows = Helper.GetAllExplorerWindows().ToArray();

            Helper.BypassWinForegroundRestrictions();

            var location = string.IsNullOrWhiteSpace(windowToOpen.Location) ? _defaultLocation : windowToOpen.Location;
            await RunInStaThread(() =>
            {
                // Equivalent of the old Shell32 SHELLEXECUTE with the open/opennewwindow verb,
                // but without pulling in the Shell type library.
                try
                {
                    Process.Start(new ProcessStartInfo(location)
                    {
                        UseShellExecute = true,
                        Verb = duplicate ? "opennewwindow" : "open"
                    });
                }
                catch
                {
                    // Shell refused the verb (e.g. virtual folder); nothing else to try.
                }
            });

            if (!hasSelection) return;

            var newWindowHandle = await Helper.ListenForNewExplorerWindowAsync(currentWindows ?? []);
            if (newWindowHandle == 0) return;

            var window = _windowEntryDict.Keys.FirstOrDefault(w => w.HWND == newWindowHandle);
            if (window == null) return;

            SelectItems(window, windowToOpen.SelectedItems);
        }
        finally
        {
            if (lockToOpenWindows)
                _toOpenWindowsLock.Release();
        }
    }
    /// <summary>
    /// Opens <paramref name="windowToOpen"/>'s location as a tab, restoring its selection once navigation
    /// completes. Returns whether the conversion succeeded, which decides whether the caller may destroy
    /// the window it came from (upstream #137).
    /// </summary>
    private async Task<bool> OpenTabNavigateWithSelection(WindowRecord windowToOpen, nint windowHandle = 0, bool isDuplicate = false, bool forceTabReuse = false)
    {
        await _toOpenWindowsLock.WaitAsync();
        try
        {
            // Reusing an existing tab is only a shortcut if the tab is still there. `_windowEntryDict.Count`
            // says "we once tracked windows" and reads > 0 for a session whose windows are all gone, so this
            // branch used to look for (and fail to find) a tab, and then still skip the window path below —
            // the navigation was dropped on the floor (upstream #137). The live count removes that outcome:
            // with no windows left, it is 0 and the "open a window" fallback below runs instead.
            if ((_reuseTabs || forceTabReuse) && !isDuplicate && GetLiveWindowCount() > 0)
            {
                var existingTab = SearchForTab(windowToOpen.Location);
                if (existingTab != 0)
                {
                    windowHandle = WinApi.GetParent(existingTab);
                    await SelectTabByHandle(windowHandle, existingTab);
                    WinApi.RestoreWindowToForeground(windowHandle);
                    return true;
                }
            }

            // Get the main window
            var mainWindowHWnd = Helper.IsFileExplorerWindow(windowHandle)
                ? windowHandle
                : GetMainWindowHWnd(windowToOpen.Handle);

            if (mainWindowHWnd == 0)
            {
                await OpenNewWindowWithSelection(windowToOpen, lockToOpenWindows: false);
                return true;
            }

            // Store the current tabs
            var currentTabs = Helper.GetAllExplorerTabs(mainWindowHWnd).ToArray();

            // Request to open a new tab
            await RequestToOpenNewTab(mainWindowHWnd, lockToOpenWindows: false);

            // Wait for the new tab
            var newTabHandle = await Helper.ListenForNewExplorerTabAsync(mainWindowHWnd, currentTabs, 2_000);
            if (newTabHandle == 0) return false;

            // Get the window object
            var window = await Helper.DoUntilNotDefaultAsync(() => GetWindowByTabHandle(newTabHandle), 2_000, 50);
            if (window == null) return false;

            var navigated = await NavigateAndSelectAsync(window, windowToOpen);

            WinApi.RestoreWindowToForeground(mainWindowHWnd);

            return navigated;
        }
        finally
        {
            _toOpenWindowsLock.Release();
        }
    }

    /// <summary>
    /// Navigates <paramref name="window"/> to <paramref name="windowToOpen"/>'s location and applies its
    /// selection, reporting whether the navigation completed.
    /// </summary>
    /// <remarks>
    /// Two things this must not do, both of them #142:
    ///
    /// <para>It must not leave the <c>NavigateComplete2</c> handler attached. The event source outlives the
    /// call, so a handler that is only unsubscribed from inside itself stays attached forever whenever
    /// navigation never completes — exactly the case the timeout below exists for. The next navigation to
    /// that window would then re-enter a handler whose <c>TaskCompletionSource</c> has already finished, and
    /// this method's own locals are kept alive by the subscription it never dropped.</para>
    ///
    /// <para>It must not resume the await inline on Explorer's COM callback stack. The continuation runs
    /// <c>RequestToOpenNewTab</c>/<c>SelectItems</c> and releases <c>_toOpenWindowsLock</c>; a plain
    /// <c>TaskCompletionSource</c> runs all of that on the thread that raised the event (Explorer's), where a
    /// blocking call stalls the shell that just raised it and can deadlock against the lock the callback
    /// path may already hold. <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> forces the
    /// continuation onto the pool.</para>
    ///
    /// <para>The item selection is also taken off the callback stack for the same reason — it is a stream of
    /// blocking COM calls (<c>SelectItem</c> per item), and doing that inside a shell event handler blocks
    /// the shell's own message pump.</para>
    /// </remarks>
    private static async Task<bool> NavigateAndSelectAsync(ExplorerWindow window, WindowRecord windowToOpen)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void NavigateHandler(object? _, object? __) => tcs.TrySetResult(true);

        try
        {
            window.NavigateComplete2 += NavigateHandler;

            try
            {
                Navigate(window, windowToOpen.Location);
            }
            catch (Exception ex)
            {
                // A refused Navigate never raises NavigateComplete2, so without this the await below would
                // always burn the full timeout before reporting the same failure.
                Debug.WriteLine($"NavigateAndSelectAsync: Navigate failed: {ex.Message}");
                return false;
            }

            // Task.WhenAny rather than WhenAll: a navigation that never reports completion must not wedge
            // the caller, and the tab it was aimed at already exists either way.
            if (await Task.WhenAny(tcs.Task, Task.Delay(5_000)) != tcs.Task)
            {
                Debug.WriteLine($"NavigateAndSelectAsync: no NavigateComplete2 for {windowToOpen.Location} within 5s");
                return false;
            }
        }
        finally
        {
            // Unconditional: this is the fix. The handler above is a closure over `window`, so leaving it
            // attached pins an Explorer COM object for the life of that window's process.
            try
            {
                window.NavigateComplete2 -= NavigateHandler;
            }
            catch (Exception ex)
            {
                // The window can be gone by now; that detaches the handler just as well.
                Debug.WriteLine($"NavigateAndSelectAsync: unsubscribing NavigateComplete2 failed: {ex.Message}");
            }
        }

        // Off the COM callback stack (see remarks): the navigation is done, the selection is not urgent.
        SelectItems(window, windowToOpen.SelectedItems);

        return true;
    }
    /// <summary>
    /// Whether the user has marked this folder as one that always opens in its own window.
    /// </summary>
    /// <remarks>
    /// The comparison is the shell's own path identity, not text: a marked folder arrives through several
    /// spellings (a trailing separator, a different case, a non-canonical form) and a list that matched
    /// only byte-for-byte would read as a feature that does not work.
    /// </remarks>
    private bool IsNoMergeFolder(string location)
    {
        var marked = SettingsManager.NoMergeFolders;
        if (marked.Count == 0) return false;

        foreach (var folder in marked)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            if (_shellPathComparer.IsEquivalent(location, folder)) return true;
        }

        return false;
    }

    /// <summary>
    /// Writes a folder down as one that always opens in its own window.
    /// </summary>
    /// <remarks>
    /// Called when the user pulls a tab out of a window — see the detached-tab branch in
    /// <see cref="OnShellWindowRegistered"/>. The entry is deliberately visible to the user (the settings
    /// page list, and the tray item that toggles it) because the app is making a lasting decision on their
    /// behalf; something it can do only if they can see it and take it back.
    /// </remarks>
    /// <returns>True when a folder was written down.</returns>
    private bool MarkNoMergeFolder(string location)
    {
        try
        {
            // The window's location is the shell's URL form; the list stores paths, because the user reads
            // it and the tray compares against it. A location with no path of its own (a shell namespace)
            // has nothing to write down and is skipped.
            var path = Helper.ToFolderPath(location);
            if (path is null) return false;
            if (IsNoMergeFolder(path)) return false;

            SettingsManager.NoMergeFolders = [.. SettingsManager.NoMergeFolders, path];
            Debug.WriteLine($"MarkNoMergeFolder: {path}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MarkNoMergeFolder failed: {ex}");
            return false;
        }
    }

    private bool TryGetRecentlyClosedWindow(string location, out WindowRecord? closedWindow, int maxAge = 2_000)
    {
        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer.GetPidlFromPath(location);
            lock (_closedWindowsLock)
            {
                for (var i = _closedWindows.Count - 1; i >= 0; i--)
                {
                    var record = _closedWindows[i];
                    if (Environment.TickCount64 - record.CreatedAt > maxAge) break;
                    if (!_shellPathComparer.IsEquivalent(location, record.Location, targetPidl)) continue;
                    _closedWindows.RemoveAt(i);
                    closedWindow = record;
                    return true;
                }
            }
            closedWindow = null;
            return false;
        }
        finally
        {
            if (targetPidl != 0)
                Marshal.FreeCoTaskMem(targetPidl);
        }
    }
    private nint GetMainWindowHWnd(nint otherThan)
    {
        if (Helper.IsFileExplorerWindow(_mainWindowHandle))
            return _mainWindowHandle;

        var allWindows = WinApi.FindAllWindowsEx("CabinetWClass");

        // Get another handle other than the newly created one. (In case if it is still alive.)
        _mainWindowHandle = allWindows
            .Where(h => h != otherThan)
            .Reverse() // To get the last one in the z-index (the oldest)
            .OrderByDescending(h => WinApi.FindAllWindowsEx("ShellTabWindowClass", h).Count()) // The one with the most tabs first
            .FirstOrDefault();

        if (_mainWindowHandle != 0) return _mainWindowHandle;

        return Helper.IsFileExplorerWindow(otherThan) ? otherThan : 0;
    }
    private Task<nint> GetTabHandle(ExplorerWindow window)
    {
        if (_windowEntryDict.TryGetValue(window, out WindowEntry entry) && entry.OptionalKey is { } handle and > 0)
            return Task.FromResult(handle);

        // Schedule the operation on STA
        return RunInStaThread(() =>
        {
            if (!window.TryGetTabHandle(out var hWnd) || hWnd == 0)
                return 0;

            _windowEntryDict.UpdateOptionalKey(window, hWnd);
            return hWnd;
        });
    }
    private static nint GetActiveTabHandle(nint windowHandle)
    {
        // Active tab always at the top of the z-index
        return WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
    }
    private ExplorerWindow? GetWindowByTabHandle(nint tabHandle)
    {
        if (tabHandle == 0) return null;
        return _windowEntryDict.TryGetValue(tabHandle, out ExplorerWindow? foundWindow) ? foundWindow : null;
    }
    private static string[]? GetSelectedItems(ExplorerWindow window) => window.GetSelectedItemNames();
    private static void SelectItems(ExplorerWindow window, string[]? names) => window.SelectItemsByName(names);

    /// <summary>
    /// The folder the app currently treats as the user's folder: the active tab of the window it means by
    /// "the window in use".
    /// </summary>
    /// <remarks>
    /// Asked for by the tray menu — "never merge this folder" needs a folder, and the menu's own click
    /// arrives while the native tray popup owns the foreground, so the foreground window is the popup rather
    /// than Explorer. The main window is the app's existing answer to "which window does the user mean"
    /// (a hotkey can point it at another one with <see cref="SetTargetWindow"/>). Returns null when there is
    /// no Explorer window to read, which the caller shows as a disabled item.
    /// </remarks>
    /// <returns>The folder's path — not the window's location, which is the shell's URL form.</returns>
    public string? GetCurrentLocation()
    {
        try
        {
            var hWnd = Helper.IsFileExplorerWindow(_mainWindowHandle) ? _mainWindowHandle : GetMainWindowHWnd(0);
            if (hWnd == 0) return null;

            var tabHandle = GetActiveTabHandle(hWnd);
            var window = GetWindowByTabHandle(tabHandle);
            if (window is null) return null;

            return Helper.ToFolderPath(GetLocation(window));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetCurrentLocation failed: {ex}");
            return null;
        }
    }
    /// <summary>
    /// The window's location: the cached one when the table still knows the window, a live read otherwise.
    /// </summary>
    /// <remarks>
    /// Used instead of the dictionary's indexer, which throws <see cref="KeyNotFoundException"/> for a
    /// window that has gone. The entry can disappear between resolving a tab handle and reading it — the
    /// window is closed on another thread, or the crash path tears the whole table down — and because
    /// every caller of this sits inside a hotkey handler that catches broadly, the indexer turned that
    /// race into "the hotkey did nothing, with no trace". Falling back to a live read is both truthful
    /// and what the caller wants.
    /// </remarks>
    private string GetKnownLocation(ExplorerWindow window) =>
        _windowEntryDict.TryGetByPrimary(window, out var info, out _) && info.Location is { } cached
            ? cached
            : GetLocation(window);

    private static string GetLocation(ExplorerWindow window)
    {
        var path = window.LocationURL;
        if (!string.IsNullOrWhiteSpace(path)) return Helper.NormalizeLocation(path);

        // Recycle Bin, This PC, etc
        path = window.GetDocumentFolderSelfPath() ?? string.Empty;
        return Helper.NormalizeLocation(path);
    }

    /// <summary>
    /// Navigates a tab to <paramref name="path"/>.
    /// <para>
    /// A literal <c>#</c> makes Explorer treat the remainder as a URL fragment, so it is
    /// percent-escaped. (The original worked around this by building a <c>Shell32.Folder</c>
    /// through <c>Shell.NameSpace</c>, which is no longer available.)
    /// </para>
    /// </summary>
    private static void Navigate(ExplorerWindow window, string path)
    {
        var target = path.Contains('#') ? path.Replace("#", "%23") : path;
        window.Navigate2(target);
    }
    private Task RunInStaThread(Action action, TaskCreationOptions tco = default, CancellationToken ct = default)
    {
        return Task.Factory.StartNew(action, ct, tco, _staTaskScheduler);
    }
    private Task<T?> RunInStaThread<T>(Func<T?> action, TaskCreationOptions tco = default, CancellationToken ct = default)
    {
        return Task.Factory.StartNew(action, ct, tco, _staTaskScheduler);
    }

    private void StartExplorerProcessCheck() => _explorerCheckTimer = new Timer(CheckForMainExplorer, null, 0, 1000);
    private void CheckForMainExplorer(object? state)
    {
        // A timer tick can already be in flight when Dispose() runs; without this the callback would
        // re-initialise the shell objects on a disposed watcher and leak the freshly created global
        // hooks / COM subscriptions (AUD-04).
        if (_disposed) return;

        // The returned Process holds an open process handle (StartTime was read); release it once
        // the id is captured.
        using var process = Helper.GetMainExplorerProcess();
        if (process == null) return;

        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = null;

        lock (_processLock)
        {
            // `_shellRebuildRequested` overrides the id check: after a resume the shell may have come back
            // under the same pid, in which case the id is non-zero and this would otherwise conclude the
            // shell was never re-created and leave the stale COM objects in place (upstream #137).
            if (_mainExplorerProcessId != 0 && !_shellRebuildRequested) return;

            _mainExplorerProcessId = process.Id;
            _shellRebuildRequested = false;
            InitializeShellObjects();
            OnShellInitialized?.Invoke();
        }
    }

    /// <summary>
    /// Rebuilds the Shell COM objects after the machine wakes from sleep (upstream #137).
    /// </summary>
    /// <remarks>
    /// A modern standby cycle can take the shell down and bring it back without <c>explorer.exe</c> ever
    /// terminating, and possibly without a new process id — so neither <see cref="OnExplorerProcessTerminated"/>
    /// nor the <c>_mainExplorerProcessId</c> check notices. Every <c>ShellWindows</c> / <c>ShellFolderView</c>
    /// interface obtained before that point is then a stale marshaller: calls to it fail or, worse, succeed
    /// against an object the shell no longer uses, and no window registered afterwards reaches the
    /// <c>WindowRegistered</c> handler because the connection point died with the old object. The visible
    /// result is a tray app that is still running and still claims its hook is active, but silently stops
    /// managing tabs until it is restarted.
    ///
    /// <para>Only <c>Resume</c> is handled. <c>StatusChange</c> fires on every battery/AC transition and
    /// <c>Suspend</c> fires while the machine is going down, where tearing the shell down would discard
    /// state for no benefit.</para>
    ///
    /// <para>No wait for the shell to settle is needed before the rebuild: the work here is only the release,
    /// and the re-creation is retried by the discovery loop once a second until the shell answers.</para>
    /// </remarks>
    private void OnPowerModeChanged(object? s, PowerModeChangedEventArgs e)
    {
        if (_disposed || e.Mode != PowerModes.Resume) return;

        // One rebuild at a time: Windows can deliver several power notifications in a row across a single
        // resume, and a second teardown running against a shell that is already being re-bound would leave
        // the watcher without its global hooks.
        if (Interlocked.CompareExchange(ref _resumeRebuildInProgress, 1, 0) != 0) return;

        // The shell COM objects we hold are dropped here — on the SystemEvents thread, without waiting for
        // anything — and the rebuild is left to the same discovery loop the startup path uses. That loop
        // already knows how to create the objects, hook every window it finds and tolerate a shell that is
        // not ready yet (it re-tries every second); re-implementing any of that would be a second code path
        // to keep correct. `_shellRebuildRequested` is what tells it this tick is a rebuild and not a no-op.
        _shellRebuildRequested = true;

        try
        {
            lock (_processLock)
            {
                if (_disposed)
                {
                    _shellRebuildRequested = false;
                    return;
                }

                ReleaseShellObjectsForRebuild();
                _mainExplorerProcessId = 0;
                StartExplorerProcessCheck();
            }
        }
        catch (Exception ex)
        {
            _shellRebuildRequested = false;
            Debug.WriteLine($"OnPowerModeChanged: releasing the shell objects failed: {ex}");
        }
        finally
        {
            // Only guards against overlap, not against the rebuild itself: `CheckForMainExplorer` is what
            // clears `_shellRebuildRequested`, and it does so under `_processLock`.
            Interlocked.Exchange(ref _resumeRebuildInProgress, 0);
        }
    }

    private void OnExplorerProcessTerminated(object? s, ProcessEventArgs e)
    {
        // The ProcessWatcher is disposed alongside this watcher, but a termination event can
        // already be queued (posted to the captured sync context) when Dispose runs. Without this
        // guard the handler would call StartExplorerProcessCheck on a disposed instance and create
        // a timer that is never released again.
        if (_disposed) return;

        // Main explorer.exe process (_shellWindows must be restarted)
        lock (_processLock)
        {
            // Re-check inside the lock: Dispose may have run between the guard above and here.
            if (_disposed) return;

            if (e.ProcessId == _mainExplorerProcessId)
            {
                _mainExplorerProcessId = 0;

                // The shell died under us, so the windows recorded now are genuinely lost and worth
                // offering back; DisposeShellObjects persists them with Restore set, and the prompt on
                // the next start decides whether the user wants them.
                DisposeShellObjects();
                StartExplorerProcessCheck();
                return;
            }
        }

        // Other explorer.exe processes
        lock (_windowEntryDictLock)
        {
            // Nothing tracked at all: skip the per-entry COM probes entirely. This is a bookkeeping
            // check (are there entries to examine?), not a liveness one, so `Count` is the right read
            // here — the loop below is exactly what turns a stale entry into a removal.
            if (_windowEntryDict.Count == 0) return;

            // Snapshot first, then walk the copy. The dictionary's enumerator copies under its own lock
            // on every use, so indexing it live builds a *different, shrinking* collection on each
            // iteration: the old `ElementAt(i)` loop only stayed in range because the removal it performs
            // as it goes makes the next index land exactly on the new last element. Any other remover
            // (the OnQuit handler removes too) breaks that coincidence and the index goes out of range —
            // inside a hook callback, where the exception is swallowed and the crash bookkeeping is
            // simply lost.
            var entries = _windowEntryDict.ToArray<WindowEntry>();

            var crashCount = 0;
            for (var i = entries.Length - 1; i >= 0; i--)
            {
                var (window, info) = entries[i];
                try
                {
                    _ = window.HWND;
                }
                catch
                {
                    if (info.OnNavigateHandler != null)
                    {
                        crashCount++;
                        lock (_closedWindowsLock)
                            _closedWindows.Add(new WindowRecord(info.Location!, name: info.Name!));
                    }

                    RemoveWindowAndUnhookEvents(window, info, useLock: false);
                }
            }
            if (!SettingsManager.RestorePreviousWindows || _windowEntryDict.Count > 0) return;
            lock (_closedWindowsLock)
            {
                // Clamp: crashCount can exceed the number of records held, and the old loop then
                // walked off the front of the list inside a hook callback.
                var markCount = Math.Min(crashCount, _closedWindows.Count);
                for (var i = 1; i <= markCount; i++)
                    _closedWindows[_closedWindows.Count - i].Restore = true;
            }
        }
    }

    /// <summary>
    /// A "created long ago" <see cref="Stopwatch"/> timestamp for windows that were already open when
    /// the watcher started. <see cref="SearchForTab"/> suppresses reuse for windows younger than 2s, so
    /// stamping such windows "now" made the first hotkey press after launch open a duplicate (AUD-27).
    /// </summary>
    private static long PreExistingWindowTimestamp => Stopwatch.GetTimestamp() - 60L * Stopwatch.Frequency;

    private void InitializeShellObjects()
    {
        // Idempotence + post-dispose guards. The shell is initialized from a timer callback and rebuilt
        // after an Explorer restart; without these the method could run twice over (duplicate global
        // hooks) or resurrect a disposed watcher (AUD-04).
        if (_disposed) return;
        if (_shellInitialized) return;

        // Windows already on screen are "pre-existing" only when the watcher is starting up. After a
        // post-resume rebuild they are being re-hooked within seconds of the shell coming back, and stamping
        // them an hour old would let SearchForTab reuse them immediately — the stale-entry guard is exactly
        // wrong there. `_shellRebuildRequested` is the tell, and it is read once here so the flag is this
        // method's answer to "fresh start or rebuild?" for the whole loop below.
        var isFirstInitialization = !_shellRebuildRequested;

        _shellPathComparer = new ShellPathComparer();
        _staTaskScheduler = new StaTaskScheduler();
        _shellWindows = new ShellWindows();

        _defaultLocation = Helper.GetDefaultExplorerLocation(_shellPathComparer);

        if (SettingsManager.ClosedWindows != null)
            lock (_closedWindowsLock)
            {
                // A persisted record carries a TickCount64 from a previous session (older builds:
                // a 32-bit TickCount) that is meaningless against the current boot's clock. Stamp
                // them "ancient" so TryGetRecentlyClosedWindow can never mistake restored history
                // for a just-detached tab.
                foreach (var record in SettingsManager.ClosedWindows)
                    record.CreatedAt = 0;

                _closedWindows.AddRange(SettingsManager.ClosedWindows);
            }

        // Hook the global "WindowRegistered" event
        _windowRegisteredHandler = OnShellWindowRegistered;
        _shellWindows.WindowRegistered += _windowRegisteredHandler;

        // Hook the global "OBJECT_SHOW" event
        _eventObjectShowHookCallback = OnWindowShown;
        _eventObjectShowHookId = WinApi.SetWinEventHook(WinApi.EVENT_OBJECT_SHOW, WinApi.EVENT_OBJECT_SHOW, 0, _eventObjectShowHookCallback, 0, 0, 0);

        // If a previous instance died (or the shell was torn down) between Helper.HideWindow and
        // the matching ShowWindow, an Explorer window can be stranded fully transparent. Anything
        // still recorded as hidden gets one restore attempt; dead handles simply no-op.
        foreach (var hWnd in Helper.HiddenWindows.Keys)
        {
            Helper.ShowWindow(hWnd, removeCache: true);
            Helper.UpdateWindowLayered(hWnd, remove: true);
        }

        // Hook the event handlers for already-open windows
        var count = _shellWindows.Count;
        for (var i = 0; i < count; i++)
        {
            try
            {
                var window = _shellWindows.Item(i);
                if (window is null) continue;

                // Pre-existing on a fresh start (see isFirstInitialization above), newly created otherwise.
                var windowInfo = new WindowInfo(isFirstInitialization ? PreExistingWindowTimestamp : Stopwatch.GetTimestamp());

                // TryAdd, not Add: this runs on a thread-pool Timer callback, and a duplicate primary
                // key used to throw ArgumentException that escaped the callback and killed the process
                // (the "resident tray app just disappears" symptom) (AUD-01).
                if (!_windowEntryDict.TryAdd(window, windowInfo)) continue;

                window.PutProperty("seenBefore", true);

                _ = GetTabHandle(window);
                HookWindowEvents(window, windowInfo);
            }
            catch (Exception ex)
            {
                // Keep a bad window from aborting the whole rebuild (and thus the process).
                Debug.WriteLine($"InitializeShellObjects: window #{i} failed: {ex}");
            }
        }

        // Recorded windows stay on offer until they have been offered once — whatever happens to be
        // open right now is not consulted.
        //
        // The rule used to be "clear every Restore flag when a window is open and the shell was not
        // torn down", on the grounds that reopening windows the shell still has would duplicate them.
        // That is true, but it is a question for the user, and it is the question RestoreWindowsPrompt
        // already asks. Clearing the flags instead meant the feature did nothing in its most ordinary
        // case: starting the app while Explorer windows are already open, where the settings toggle and
        // the tooltip promised a restore that could never happen.
        //
        // Repeating the prompt is not a risk: RestorePreviousWindows marks every record it offers as
        // consumed whether the answer is yes or no, and MaybeRestorePreviousWindows runs at most once
        // per session.
        _shellInitialized = true;
    }
    private void DisposeShellObjects()
    {
        // Nothing was initialised (e.g. no main Explorer was ever found): every field below is still
        // null! and releasing it would NRE on the SessionEnding path. Also makes this idempotent, so a
        // second Dispose (SessionEnding arrives on a SystemEvents thread and again on UI exit) is a
        // harmless no-op instead of an ObjectDisposedException (AUD-04).
        if (!_shellInitialized) return;

        PersistWindows();

        // Unhook global event
        if (_windowRegisteredHandler != null)
        {
            _shellWindows.WindowRegistered -= _windowRegisteredHandler;
            _windowRegisteredHandler = null;
        }
        if (_eventObjectShowHookCallback != null)
        {
            WinApi.UnhookWinEvent(_eventObjectShowHookId);
            _eventObjectShowHookCallback = null;
        }

        // Unsubscribe from each Explorer window's events and release its COM reference
        foreach (var (window, windowInfo) in _windowEntryDict)
        {
            if (windowInfo.OnQuitHandler != null) window.OnQuit -= windowInfo.OnQuitHandler;
            if (windowInfo.OnNavigateHandler != null) window.NavigateComplete2 -= windowInfo.OnNavigateHandler;

            window.Dispose();
        }
        _windowEntryDict.Clear();

        // Release the ShellWindows COM object (also unadvises event sinks)
        _shellWindows.Dispose();

        _shellPathComparer.Dispose();
        _staTaskScheduler.Dispose();

        _shellInitialized = false;
    }

    /// <summary>
    /// Drops the Shell COM objects and the per-window subscriptions, leaving the watcher ready to be
    /// re-initialised. Used only by the post-resume rebuild.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="DisposeShellObjects"/> because that one persists the tracked windows as
    /// "lost, offer to restore" on the way out. That is right for a dying shell and wrong for a resume: the
    /// windows are about to be re-hooked by <see cref="InitializeShellObjects"/>, so persisting them would
    /// schedule a restore prompt for windows the user can still see.
    ///
    /// <para>Only the shell-owned objects are released. <c>_staTaskScheduler</c> and
    /// <c>_shellPathComparer</c> are ours, and <c>_defaultLocation</c> is a string; keeping them means the
    /// rebuild does not depend on the shell being ready enough to answer a default-location query during
    /// it.</para>
    /// </remarks>
    private void ReleaseShellObjectsForRebuild()
    {
        if (!_shellInitialized) return;

        if (_windowRegisteredHandler != null)
        {
            _shellWindows.WindowRegistered -= _windowRegisteredHandler;
            _windowRegisteredHandler = null;
        }

        if (_eventObjectShowHookCallback != null)
        {
            WinApi.UnhookWinEvent(_eventObjectShowHookId);
            _eventObjectShowHookCallback = null;
        }

        foreach (var (window, windowInfo) in _windowEntryDict)
        {
            try
            {
                if (windowInfo.OnQuitHandler != null) window.OnQuit -= windowInfo.OnQuitHandler;
                if (windowInfo.OnNavigateHandler != null) window.NavigateComplete2 -= windowInfo.OnNavigateHandler;
            }
            catch (Exception ex)
            {
                // A window whose connection point is already gone cannot be unsubscribed; the object is
                // released by the Dispose below either way.
                Debug.WriteLine($"ReleaseShellObjectsForRebuild: unhooking a window failed: {ex.Message}");
            }

            window.Dispose();
        }

        _windowEntryDict.Clear();

        try
        {
            _shellWindows.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ReleaseShellObjectsForRebuild: releasing ShellWindows failed: {ex.Message}");
        }

        _shellInitialized = false;
    }

    private void PersistWindows()
    {
        var store = new List<WindowRecord>();
        lock (_closedWindowsLock)
        {
            if (SettingsManager.SaveClosedHistory) store.AddRange(_closedWindows);
            _closedWindows.Clear();
        }

        // Save currently open windows (explorer crash / system restart, logoff / AppExit)
        if (SettingsManager.RestorePreviousWindows)
            lock (_windowEntryDictLock)
            {
                store.AddRange(_windowEntryDict.Values
                    .Where(w => w.OnNavigateHandler != null)
                    .Select(w => new WindowRecord(w.Location!, name: w.Name!, restore: true)));
            }

        // DistinctBy location
        var distinctItems = store
            .GroupBy(w => w.Location)
            .Select(g => g.Last())
            .ToArray();

        // TakeLast 100
        SettingsManager.ClosedWindows = distinctItems.Skip(Math.Max(0, distinctItems.Length - 100)).ToArray();
    }

    public void Dispose()
    {
        // Idempotent: SessionEnding (a SystemEvents thread) and the UI-exit path both call this.
        if (_disposed) return;
        _disposed = true;

        // Before anything else: the resume handler is the one remaining reference that can drive a full
        // shell rebuild on a disposed watcher (AUD-04's rule, same as the timer below).
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        // Stop the shell-discovery timer first. It is the only remaining reference that could
        // re-initialise the shell objects (and therefore re-create global hooks) on an instance that
        // has already been disposed — a real leak of WinEvent hooks and COM subscriptions (AUD-04).
        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = null;

        DisposeShellObjects();
        _instanceRunning = false;

        // Unsubscribe BEFORE disposing the watcher: a termination event queued behind the dispose
        // would otherwise reach OnExplorerProcessTerminated and resurrect the explorer-check timer.
        _processWatcher.ProcessTerminated -= OnExplorerProcessTerminated;
        _processWatcher.Dispose();
        GC.SuppressFinalize(this);
    }
}
