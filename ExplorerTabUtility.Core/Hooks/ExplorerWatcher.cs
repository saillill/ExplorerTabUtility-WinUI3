using System;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
    /// <summary>Set while the shell is being rebuilt after explorer.exe died.</summary>
    private bool _shellTornDown;

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

        if (_windowEntryDict.Count > 0)
        {
            OpenNewTab(windowHandle, normalizedPath);
            return;
        }

        await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
    }
    public void OpenNewTab(nint windowHandle, string location)
    {
        _ = OpenTabNavigateWithSelection(new WindowRecord(location, windowHandle), windowHandle);
    }
    public async Task DuplicateActiveTab(nint windowHandle, bool asTab)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        if (window == null) return;

        var location = _windowEntryDict[window].Value.Location ?? GetLocation(window);
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

        var location = _windowEntryDict[window].Value.Location ?? GetLocation(window);
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
        if (!_isForcingTabs || idObject != 0 || idChild != 0) return;

        // Check if the hWnd was processed by OnShellWindowRegistered
        if (_processedHWnds.TryRemove(hWnd, out _)) return;

        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass")) return;

        if (_windowEntryDict.Count < 2 || Helper.IsCtrlShiftDown()) return;
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

            // Check if this is a single tab window and there are other windows
            var shouldReopenAsTab = (_isForcingTabs || _reuseTabs) &&
                                    _windowEntryDict.Count > 1 &&
                                    hWnd != _mainWindowHandle &&
                                    Helper.GetAllExplorerTabs(hWnd).Take(2).Count() == 1;

            if (shouldReopenAsTab)
                Helper.HideWindow(hWnd);
            else
                PreventWindowHiding(hWnd);

            // Remove this location from closed list (race condition fix: OnQuit may add it async)
            lock (_closedWindowsLock)
                _closedWindows.RemoveAll(w => string.Equals(w.Location, location, StringComparison.OrdinalIgnoreCase));

            // Check if it is a detached tab
            var isRecentlyClosed = TryGetRecentlyClosedWindow(location, out var closedWindow);
            if (isRecentlyClosed)
                SelectItems(window, closedWindow!.SelectedItems);

            shouldReopenAsTab = shouldReopenAsTab && !isRecentlyClosed;

            if (shouldReopenAsTab)
            {
                showAgain = false;

                _ = OpenTabNavigateWithSelection(new WindowRecord(location, hWnd, GetSelectedItems(window)), _mainWindowHandle);

                window.Quit();
                lock (_closedWindowsLock)
                    _closedWindows.RemoveAll(w => string.Equals(w.Location, location, StringComparison.OrdinalIgnoreCase));
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            // OnQuit might fire after ShellWindowRegistered in case of reattached tab (and there were selected files)
            if (!isRecentlyClosed)
            {
                isRecentlyClosed = await Helper.DoUntilNotDefaultAsync(() => TryGetRecentlyClosedWindow(location, out closedWindow), 700, 50);
                if (isRecentlyClosed)
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
            if (showAgain)
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
            var windowRecord = new WindowRecord(location, new IntPtr(window.HWND), name: locationName);
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
            var result = await RunInStaThread(() => _dialogService.Show(
                LocalizationService.Get("RestoreWindowsPrompt"),
                Constants.AppName,
                DialogButton.YesNo,
                DialogIcon.Question));

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

                _ = OpenTabNavigateWithSelection(record);
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
    private async Task OpenTabNavigateWithSelection(WindowRecord windowToOpen, nint windowHandle = 0, bool isDuplicate = false, bool forceTabReuse = false)
    {
        await _toOpenWindowsLock.WaitAsync();
        try
        {
            if ((_reuseTabs || forceTabReuse) && !isDuplicate && _windowEntryDict.Count > 0)
            {
                var existingTab = SearchForTab(windowToOpen.Location);
                if (existingTab != 0)
                {
                    windowHandle = WinApi.GetParent(existingTab);
                    await SelectTabByHandle(windowHandle, existingTab);
                    WinApi.RestoreWindowToForeground(windowHandle);
                    return;
                }
            }

            // Get the main window
            var mainWindowHWnd = Helper.IsFileExplorerWindow(windowHandle)
                ? windowHandle
                : GetMainWindowHWnd(windowToOpen.Handle);

            if (mainWindowHWnd == 0)
            {
                await OpenNewWindowWithSelection(windowToOpen, lockToOpenWindows: false);
                return;
            }

            // Store the current tabs
            var currentTabs = Helper.GetAllExplorerTabs(mainWindowHWnd).ToArray();

            // Request to open a new tab
            await RequestToOpenNewTab(mainWindowHWnd, lockToOpenWindows: false);

            // Wait for the new tab
            var newTabHandle = await Helper.ListenForNewExplorerTabAsync(mainWindowHWnd, currentTabs, 2_000);
            if (newTabHandle == 0) return;

            // Get the window object
            var window = await Helper.DoUntilNotDefaultAsync(() => GetWindowByTabHandle(newTabHandle), 2_000, 50);
            if (window == null) return;

            var tcs = new TaskCompletionSource<bool>();
            Action<object?, object?> navigateHandler = null!;
            navigateHandler = (_, _) =>
            {
                window.NavigateComplete2 -= navigateHandler;
                tcs.TrySetResult(true);
                SelectItems(window, windowToOpen.SelectedItems);
            };

            window.NavigateComplete2 += navigateHandler;
            try
            {
                Navigate(window, windowToOpen.Location);
            }
            catch
            {
                window.NavigateComplete2 -= navigateHandler;
                tcs.TrySetResult(false);
            }

            WinApi.RestoreWindowToForeground(mainWindowHWnd);

            var timeoutTask = Task.Delay(5000);
            await Task.WhenAny(tcs.Task, timeoutTask);
        }
        finally
        {
            _toOpenWindowsLock.Release();
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
            if (_mainExplorerProcessId != 0) return;

            _mainExplorerProcessId = process.Id;
            InitializeShellObjects();
            OnShellInitialized?.Invoke();
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
                // offering back. A plain app shutdown must NOT set this: there the shell keeps its
                // windows, and re-offering them would duplicate them.
                _shellTornDown = true;

                DisposeShellObjects();
                StartExplorerProcessCheck();
                return;
            }
        }

        // Other explorer.exe processes
        lock (_windowEntryDictLock)
        {
            if (_windowEntryDict.Count == 0) return;
            var crashCount = 0;
            for (var i = _windowEntryDict.Count - 1; i >= 0; i--)
            {
                var (window, info) = _windowEntryDict.ElementAt<WindowEntry>(i);
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
        var hasOpen = false;
        var count = _shellWindows.Count;
        for (var i = 0; i < count; i++)
        {
            try
            {
                var window = _shellWindows.Item(i);
                if (window is null) continue;
                hasOpen = true;

                var windowInfo = new WindowInfo(PreExistingWindowTimestamp);

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

        // Restore flags are only meaningful when the shell was rebuilt (explorer.exe restart) or
        // when no window survived at all. Clearing them whenever *any* window happened to be open
        // disabled the feature in its main scenario: after an Explorer restart the shell recreates a
        // window before this initialisation runs, so "a window exists" says nothing about whether the
        // user's previous windows survived.
        if (hasOpen && !_shellTornDown)
            lock (_closedWindowsLock)
                foreach (var window in _closedWindows) window.Restore = false;

        _shellTornDown = false;
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
