using System;
using System.Collections.Generic;
using System.Threading;
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
    private readonly MenuFlyoutItem _settingsItem = new();
    private readonly MenuFlyoutItem _exitItem = new();

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

        // ForceCreate always puts the icon in the notification area, so the saved preference has to
        // be applied on top of it. Without this the setting only affected the session in which it was
        // changed — every restart brought the icon back even though "hide tray icon" was on.
        IsVisible = !SettingsManager.IsTrayIconHidden;
        StartupLog.Step($"Tray: visible={IsVisible} (hidden setting={SettingsManager.IsTrayIconHidden})");

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

    /// <summary>Set while a coalesced profile-menu rebuild is already queued for the UI thread.</summary>
    private int _profileMenuRebuildQueued;

    /// <summary>
    /// Rebuilds the profile submenus once per burst of changes.
    /// </summary>
    /// <remarks>
    /// Profiles are persisted on <em>every keystroke</em> (the settings window saves as you type), so
    /// this fires per character typed into a name or path field. Rebuilding means clearing both submenus
    /// and creating one <see cref="ToggleMenuFlyoutItem"/> per profile, i.e. real XAML work per keystroke
    /// once a profile list is long enough to matter. Coalescing to a single rebuild per message-loop turn
    /// keeps the menu correct without doing that work once per character.
    /// </remarks>
    private void OnProfilesChanged()
    {
        // Cleared before the rebuild, so a change that arrives during the rebuild queues another pass
        // and the menu still ends up showing the newest state.
        if (Interlocked.Exchange(ref _profileMenuRebuildQueued, 1) == 1) return;

        _uiDispatcher.TryPost(() =>
        {
            Interlocked.Exchange(ref _profileMenuRebuildQueued, 0);
            RefreshProfileMenus();
        });
    }

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
        set => RunOnUi(() => SetVisible(value));
    }

    /// <summary>
    /// Applies the tray icon's visibility, recovering when the shell refuses the change.
    /// <para>
    /// H.NotifyIcon only raises <c>UpdateState failed</c> while it still believes the icon is created,
    /// so that failure means the shell has no copy of it while the element's <c>Visibility</c> property
    /// has already moved on. Left alone the setting and the tray disagree — the icon stays hidden with
    /// "hide tray icon" off, and a later click happens to fix it (the log shows five toggles in a row
    /// failing). The exception surfaced through the dependency-property callback as an
    /// <c>Application.UnhandledException</c>, so it never reached a handler that could react.
    /// </para>
    /// </summary>
    private void SetVisible(bool visible)
    {
        try
        {
            _trayIcon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        catch (Exception ex)
        {
            StartupLog.Fail($"Tray: set visibility to {visible}", ex);
        }

        Recreate(visible);
    }

    /// <summary>
    /// Drops the shell's copy of the icon, creates it again and re-applies <paramref name="visible"/>.
    /// <para>
    /// This is the library's own recovery for a lost icon (it runs the same two calls when the taskbar
    /// is recreated): <c>Create</c> returns early while <c>IsCreated</c> is still true, so the stale
    /// registration has to be dropped first. That also means the old <c>ForceCreate()</c> call did
    /// nothing here.
    /// </para>
    /// <para>
    /// The state is applied through the inner <c>TrayIcon</c> rather than the element's property: the
    /// element already holds the value that was asked for, and setting a property to the value it
    /// holds raises no change callback.
    /// </para>
    /// </summary>
    private void Recreate(bool visible)
    {
        try
        {
            _ = _trayIcon.TrayIcon.TryRemove();
            _trayIcon.TrayIcon.Create();

            if (visible) _trayIcon.TrayIcon.Show();
            else _trayIcon.TrayIcon.Hide();

            StartupLog.Step($"Tray: recreated (visible={visible})");
        }
        catch (Exception ex)
        {
            StartupLog.Fail($"Tray: recreate (visible={visible})", ex);
        }
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
        ApplyMenuText();

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

        _reuseTabsItem.IsChecked = SettingsManager.ReuseTabs;
        _reuseTabsItem.Command = new RelayCommand(() => RunOnUi(() =>
        {
            _reuseTabsItem.IsChecked = !_reuseTabsItem.IsChecked;
            ToggleReuseTabs();
        }));

        _startupItem.IsChecked = RegistryManager.IsStartupEnabled;
        // ToggleStartup re-reads the registry, so it does not depend on a pre-flipped IsChecked.
        _startupItem.Command = new RelayCommand(() => RunOnUi(ToggleStartup));

        // Post rather than invoke inline: the click arrives while the Win32 tray popup still owns
        // the foreground, and the window cannot be activated until that popup has finished closing.
        // (Command, not Click — see BUG-01 note above.)
        _settingsItem.Command = new RelayCommand(() => _uiDispatcher.TryPost(() => ShowRequested?.Invoke()));

        // Same reason as the settings item above: this command runs while the native tray popup is
        // still finishing, and exiting tears down the tray icon, the hooks and the message loop. Post
        // it so the teardown never overlaps the popup's own shutdown — every other command in this
        // menu already goes through the dispatcher, and this was the single exception.
        _exitItem.Command = new RelayCommand(() => _uiDispatcher.TryPost(() => ExitRequested?.Invoke()));

        _menu.Items.Add(_keyboardMenu);
        _menu.Items.Add(_mouseMenu);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(_windowHookItem);
        _menu.Items.Add(_reuseTabsItem);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(_exitItem);

        RefreshProfileMenus();
    }

    /// <summary>
    /// Reads every string the menu shows.
    /// <para>
    /// Kept apart from <see cref="BuildMenu"/>, which appends to the flyout and therefore may only run
    /// once, while a language change has to re-read the texts of items that already exist. Without that
    /// the window follows the new language and the tray menu keeps the old one until the next start —
    /// which is what "switching the language needs a restart" turned out to be.
    /// </para>
    /// </summary>
    private void ApplyMenuText()
    {
        _keyboardMenu.Text = LocalizationService.Get("KeyboardShortcut");
        _mouseMenu.Text = LocalizationService.Get("MouseShortcut");
        _windowHookItem.Text = LocalizationService.Get("WindowIntercept");
        _reuseTabsItem.Text = LocalizationService.Get("TabReuse");
        _startupItem.Text = LocalizationService.Get("AddToStartup");
        _settingsItem.Text = LocalizationService.Get("Settings");
        _exitItem.Text = LocalizationService.Get("Exit");
    }

    /// <summary>
    /// Re-reads the tray menu after a language change. The per-profile entries are rebuilt as well:
    /// they are produced by the same pass and would otherwise stay in the previous language too.
    /// </summary>
    public void RefreshLocalization()
    {
        RunOnUi(ApplyMenuText);
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
        // Explorer restarted: whatever the shell had for us is gone. The library handles the taskbar
        // re-creating itself, but not every shell restart reaches that path, and the icon's state has to
        // be re-applied for a hidden icon too — ForceCreate() only re-showed a visible one, and returned
        // early besides (see Recreate).
        RunOnUi(() => Recreate(_trayIcon.Visibility == Visibility.Visible));
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
