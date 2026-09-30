using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using ExplorerTabUtility.Abstractions;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// System tray icon and menu.
/// <para>
/// WinUI 3 has no tray control, so this uses <c>H.NotifyIcon</c> — the same ecosystem as
/// <c>H.Hooks</c>, and the only maintained replacement for the WPF-only
/// <c>Hardcodet.NotifyIcon.Wpf</c>. Every menu entry is a native
/// <see cref="MenuFlyoutItem"/> / <see cref="ToggleMenuFlyoutItem"/> /
/// <see cref="MenuFlyoutSubItem"/>; no menu visual is defined here.
/// </para>
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly ProfileManager _profileManager;
    private readonly HookManager? _hookManager;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly TaskbarIcon _trayIcon = new();
    private readonly MenuFlyout _menu = new();

    private readonly ToggleMenuFlyoutItem _windowHookItem = new();
    private readonly ToggleMenuFlyoutItem _reuseTabsItem = new();
    private readonly ToggleMenuFlyoutItem _startupItem = new();
    private readonly MenuFlyoutSubItem _keyboardMenu = new();
    private readonly MenuFlyoutSubItem _mouseMenu = new();

    private bool _savedReuseTabsState;
    private bool _disposed;

    public TrayIconService(ProfileManager profileManager, HookManager? hookManager, IUiDispatcher uiDispatcher)
    {
        _profileManager = profileManager;
        _hookManager = hookManager;
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));

        BuildMenu();

        _trayIcon.ToolTipText = Constants.NotifyIconText;

        // WinUI names it ContextFlyout (TaskbarIcon has no ContextMenu property). The flyout is a
        // plain MenuFlyout, so the menu visual is the system's, not ours.
        _trayIcon.ContextFlyout = _menu;

        // PopupMenu is H.NotifyIcon's default and the most compatible mode: it delegates to a real
        // Win32 tray menu. "SecondWindow" (a WinUI-rendered overlay) is documented as preview-stage
        // and behaves differently for unpackaged apps.
        // IMPORTANT (BUG-01): in this mode the library rebuilds the MenuFlyout as a native Win32 popup
        // and only executes each item's Command — the XAML Click event is never raised, so every menu
        // entry in BuildMenu must be wired via Command, never via Click.
        _trayIcon.ContextMenuMode = ContextMenuMode.PopupMenu;

        _trayIcon.DoubleClickCommand = new RelayCommand(() => _uiDispatcher.TryPost(() => ShowRequested?.Invoke()));

        // Unpackaged apps have no package-relative icon; load it straight off disk.
        try
        {
            StartupLog.Step($"Tray: icon path {App.IconPath}, exists={System.IO.File.Exists(App.IconPath)}");
            _trayIcon.IconSource = new BitmapImage(new Uri(App.IconPath));
        }
        catch (Exception ex)
        {
            // A missing icon must not stop the hooks from starting.
            StartupLog.Fail("Tray: IconSource", ex);
        }

        StartupLog.Step("Tray: ForceCreate");
        _trayIcon.ForceCreate();
        StartupLog.Step($"Tray: created, IsCreated={_trayIcon.IsCreated}");

        // Events cannot be subscribed through ?. — guard explicitly.
        if (_hookManager is { } hooks)
        {
            hooks.OnShellInitialized += OnShellInitialized;
            hooks.OnWindowHookToggled += OnWindowHookToggled;
            hooks.OnReuseTabsToggled += OnReuseTabsToggled;
        }

        // Rebuild the profile submenus on every profile change (add / remove / rename / enable
        // toggle / import). Without this the menu showed the startup snapshot for the whole
        // session — the settings page and the tray disagreed about names and check states.
        _profileManager.ProfilesChanged += OnProfilesChanged;
    }

    private void OnProfilesChanged() => RefreshProfileMenus();

    /// <summary>Raised when the user asks for the settings window (double-click, or "Open settings").</summary>
    public event Action? ShowRequested;

    /// <summary>
    /// Raised when the user picks "Exit". The window owns the real shutdown so it can mark the
    /// close as intentional before tearing the app down.
    /// </summary>
    public event Action? ExitRequested;

    public bool IsVisible
    {
        get => _trayIcon.Visibility == Visibility.Visible;
        set => RunOnUi(() => _trayIcon.Visibility = value ? Visibility.Visible : Visibility.Collapsed);
    }

    /// <summary>
    /// Marshals to the UI thread when needed. Every member of <see cref="TaskbarIcon"/> and of the
    /// <see cref="MenuFlyout"/> it renders is a WinUI XAML object, and touching one from a
    /// background thread throws a COMException that terminates the process.
    /// </summary>
    private void RunOnUi(Action action)
    {
        if (_uiDispatcher.HasThreadAccess) action();
        else _uiDispatcher.TryPost(action);
    }

    private void BuildMenu()
    {
        _keyboardMenu.Text = LocalizationService.Get("KeyboardShortcut");
        _mouseMenu.Text = LocalizationService.Get("MouseShortcut");

        _windowHookItem.Text = LocalizationService.Get("WindowIntercept");
        _windowHookItem.IsChecked = SettingsManager.IsWindowHookActive;
        // BUG-01: ContextMenuMode.PopupMenu rebuilds this MenuFlyout into a native Win32 popup and only
        // executes each item's Command — it never raises the XAML Click event. Menu behaviour must be a
        // Command, not a Click handler. WinUI also no longer auto-flips IsChecked on click, so the
        // toggle is done explicitly here before the existing logic runs.
        _windowHookItem.Command = new RelayCommand(() => RunOnUi(() =>
        {
            _windowHookItem.IsChecked = !_windowHookItem.IsChecked;
            ToggleWindowHook();
        }));

        _reuseTabsItem.Text = LocalizationService.Get("TabReuse");
        _reuseTabsItem.IsChecked = SettingsManager.ReuseTabs;
        _reuseTabsItem.Command = new RelayCommand(() => RunOnUi(() =>
        {
            _reuseTabsItem.IsChecked = !_reuseTabsItem.IsChecked;
            ToggleReuseTabs();
        }));

        _startupItem.Text = LocalizationService.Get("AddToStartup");
        _startupItem.IsChecked = RegistryManager.IsStartupEnabled;
        // ToggleStartup re-reads the registry, so it does not depend on a pre-flipped IsChecked.
        _startupItem.Command = new RelayCommand(() => RunOnUi(ToggleStartup));

        var settingsItem = new MenuFlyoutItem { Text = LocalizationService.Get("Settings") };
        // Post rather than invoke inline: the click arrives while the Win32 tray popup still owns
        // the foreground, and the window cannot be activated until that popup has finished closing.
        // (Command, not Click — see BUG-01 note above.)
        settingsItem.Command = new RelayCommand(() => _uiDispatcher.TryPost(() => ShowRequested?.Invoke()));

        var exitItem = new MenuFlyoutItem { Text = LocalizationService.Get("Exit") };
        exitItem.Command = new RelayCommand(() => ExitRequested?.Invoke());

        _menu.Items.Add(_keyboardMenu);
        _menu.Items.Add(_mouseMenu);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(_windowHookItem);
        _menu.Items.Add(_reuseTabsItem);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(exitItem);

        RefreshProfileMenus();
    }

    /// <summary>Rebuilds the per-profile check lists under the keyboard / mouse submenus.</summary>
    public void RefreshProfileMenus()
    {
        RunOnUi(() =>
        {
            PopulateProfiles(_keyboardMenu, _profileManager.GetKeyboardProfiles());
            PopulateProfiles(_mouseMenu, _profileManager.GetMouseProfiles());
        });
    }

    private void PopulateProfiles(MenuFlyoutSubItem parent, IEnumerable<HotKeyProfile> profiles)
    {
        parent.Items.Clear();

        foreach (var profile in profiles)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = string.IsNullOrWhiteSpace(profile.Name) ? profile.Id.ToString()[..8] : profile.Name,
                IsChecked = profile.IsEnabled,
                Tag = profile
            };

            // Command, not Click: PopupMenu mode only executes the Command (BUG-01). The native popup
            // does not flip IsChecked, so toggle it explicitly to mirror WinUI's click behaviour.
            item.Command = new RelayCommand(() => RunOnUi(() =>
            {
                item.IsChecked = !item.IsChecked;
                _profileManager.SetProfileEnabledFromTray(profile, item.IsChecked);
                SyncParentFromChildren(parent);
            }));

            parent.Items.Add(item);
        }

        SyncParentFromChildren(parent);
    }

    private static void SyncParentFromChildren(MenuFlyoutSubItem parent)
    {
        foreach (var item in parent.Items)
        {
            if (item is ToggleMenuFlyoutItem toggle)
                toggle.IsEnabled = parent.IsEnabled;
        }
    }

    private void OnShellInitialized()
    {
        // Explorer restarted: the shell may have dropped our icon. Re-register it.
        RunOnUi(() =>
        {
            if (_trayIcon.Visibility != Visibility.Visible) return;

            try
            {
                _trayIcon.ForceCreate();
            }
            catch
            {
                // Best-effort; the icon is recreated on the next shell restart.
            }
        });
    }

    private void OnWindowHookToggled()
    {
        if (_windowHookItem.IsChecked)
        {
            // Reuse-tabs implies the window hook; remember the old state so it can be restored.
            _savedReuseTabsState = SettingsManager.ReuseTabs;

            _windowHookItem.IsChecked = false;
            ApplyWindowHook(false);

            // Match the menu path (ToggleWindowHook): reuse-tabs must actually stop too. Leaving
            // it running meant windows kept folding into tabs even though the user had just turned
            // interception "off" — the fold condition is (_isForcingTabs || _reuseTabs).
            if (_reuseTabsItem.IsChecked)
            {
                _reuseTabsItem.IsChecked = false;
                ApplyReuseTabs(false);
            }
            return;
        }

        _windowHookItem.IsChecked = true;
        ApplyWindowHook(true);

        if (!_savedReuseTabsState) return;

        _reuseTabsItem.IsChecked = true;
        ApplyReuseTabs(true);
    }

    private void OnReuseTabsToggled()
    {
        _reuseTabsItem.IsChecked = !_reuseTabsItem.IsChecked;
        ApplyReuseTabs(_reuseTabsItem.IsChecked);
    }

    private void ToggleWindowHook()
    {
        ApplyWindowHook(_windowHookItem.IsChecked);

        if (_windowHookItem.IsChecked || !_reuseTabsItem.IsChecked) return;

        _reuseTabsItem.IsChecked = false;
        ApplyReuseTabs(false);
    }

    private void ApplyWindowHook(bool enabled)
    {
        SettingsManager.IsWindowHookActive = enabled;

        if (enabled) _hookManager?.StartWindowHook();
        else _hookManager?.StopWindowHook();
    }

    private void ToggleReuseTabs()
    {
        ApplyReuseTabs(_reuseTabsItem.IsChecked);

        if (!_reuseTabsItem.IsChecked || _windowHookItem.IsChecked) return;

        _windowHookItem.IsChecked = true;
        ApplyWindowHook(true);
    }

    private void ApplyReuseTabs(bool enabled)
    {
        SettingsManager.ReuseTabs = enabled;
        _hookManager?.SetReuseTabs(enabled);
    }

    private void ToggleStartup()
    {
        RegistryManager.ToggleStartup();
        _startupItem.IsChecked = RegistryManager.IsStartupEnabled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _profileManager.ProfilesChanged -= OnProfilesChanged;
        _trayIcon.Dispose();
    }
}
