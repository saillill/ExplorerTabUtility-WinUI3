using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using ExplorerTabUtility.App.Services;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;

namespace ExplorerTabUtility.App.Pages;

/// <summary>
/// Preferences page, laid out with the Windows settings-page pattern: one
/// <c>SettingsCard</c> per setting with a native <c>ToggleSwitch</c> or <c>ComboBox</c>.
/// </summary>
public sealed partial class PreferencesPage : Page
{
    private AppServices? _services;

    /// <summary>
    /// Edge length of the composed info badge. Sized to the CJK caption's ink height (~11 epx) so
    /// it reads as an inline marker instead of a button next to the sentence.
    /// </summary>
    private const double BadgeSize = 11;

    /// <summary>Leading gap between the badge/sentence block and the card header above it.</summary>
    private const double DescriptionTopGap = 4;
    private bool _loading;

    /// <summary>
    /// Set once the <see cref="CardHideTrayIcon"/> IsEnabled callback has been registered. The page is
    /// cached (<c>NavigationCacheMode=Required</c>) and <c>OnNavigatedTo</c> runs on every navigation, so
    /// this guard keeps the handler from being attached more than once.
    /// </summary>
    private bool _trayIconHeaderHooked;

    public PreferencesPage()
    {
        InitializeComponent();

        // Bound once, here, so the drawer's contents are whatever the collection holds at any moment —
        // including folders added while the page is open.
        CardNoMergeFolders.ItemsSource = _noMergeRows;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            _services = e.Parameter as AppServices ?? App.MainWindowInstance?.Services;
            StartupLog.Step($"PreferencesPage.OnNavigatedTo (services={_services is not null})");

            Localize();

            _loading = true;
            try
            {
                var themes = new[]
                {
                    new DisplayItem<int>(LocalizationService.Get("ThemeFollowSystem"), 0),
                    new DisplayItem<int>(LocalizationService.Get("ThemeDark"), 1),
                    new DisplayItem<int>(LocalizationService.Get("ThemeLight"), 2)
                };

                CbTheme.ItemsSource = themes;
                CbTheme.DisplayMemberPath = nameof(DisplayItem<int>.Display);
                CbTheme.SelectedIndex = Math.Clamp(SettingsManager.ThemeMode, 0, themes.Length - 1);

                // Native names come from the service (they must not be translated), and the order is
                // the picker's order. "Follow system" leads it: without that entry there was no way
                // back to auto-detection once a concrete language had been stored.
                var languages = new[]
                    {
                        new DisplayItem<string>(
                            LocalizationService.Get(LocalizationService.FollowSystemLanguageKey),
                            LocalizationService.FollowSystemLanguage)
                    }
                    .Concat(LocalizationService.SupportedLanguages
                        .Select(l => new DisplayItem<string>(l.NativeName, l.Code)))
                    .ToArray();

                CbLanguage.ItemsSource = languages;
                CbLanguage.DisplayMemberPath = nameof(DisplayItem<string>.Display);
                // Reflect the language that is ACTUALLY in effect, not the raw setting. An unset
                // setting means "follow the system", which resolves to a culture that has to be
                // mapped back to one of the supported languages.
                CbLanguage.SelectedIndex = FindLanguageIndex(languages);
                SwSaveClosedHistory.IsOn = SettingsManager.SaveClosedHistory;
                SwRestorePreviousWindows.IsOn = SettingsManager.RestorePreviousWindows;
                SwHideTrayIcon.IsOn = SettingsManager.IsTrayIconHidden;
                SwStartup.IsOn = RegistryManager.IsStartupEnabled;
                SwHideWindowOnStartup.IsOn = SettingsManager.HideWindowOnStartup;
                SwAutoCheckUpdates.IsOn = SettingsManager.AutoCheckForUpdates;

                // The fold switch is the hook engine's state, not the page's: the tray menu and the hotkeys
                // move the same setting, so the page reads it and then keeps reading it (see
                // WatchHookSwitches) instead of owning a copy.
                SwMergeNewWindows.IsOn = SettingsManager.ReuseTabs;
                CardMergeNewWindows.IsEnabled = _services?.HookManager is not null;

                // Configure the dropdowns while the guard is still up: applying the item container
                // style makes a ComboBox regenerate its items and raise SelectionChanged, and the
                // handlers would otherwise write the reset selection back to settings — which is how
                // a saved "Dark" silently turned into "Follow system".
                ComboBoxAssist.Configure(CbTheme);
                ComboBoxAssist.Configure(CbLanguage);
            }
            finally
            {
                _loading = false;
            }

            // Attach the callback exactly once. Driving the colour swap from the IsEnabled change itself
            // means it can never be left stale by a navigation-timed re-evaluation: whichever code flips
            // IsEnabled, and whenever, the header follows immediately — in both directions.
            if (!_trayIconHeaderHooked)
            {
                _trayIconHeaderHooked = true;
                CardHideTrayIcon.RegisterPropertyChangedCallback(
                    Control.IsEnabledProperty, (_, _) => ApplyTrayIconHeader(CardHideTrayIcon.IsEnabled));
            }

            UpdateTrayIconCardState();
            ApplyHideWindowOnStartupRequirement(RegistryManager.IsStartupEnabled);

            WatchHookSwitches();

            PlayEntrance();
        }
        catch (Exception ex)
        {
            StartupLog.Fail("PreferencesPage.OnNavigatedTo", ex);
        }
    }

    /// <summary>
    /// Plays this page's entrance: the heading, then the setting rows in order, as one wave.
    /// </summary>
    /// <remarks>
    /// The whole column is the wave. A page is a stack of blocks and this one has no list of its own, so
    /// every row is a position in it — the same behavior the shortcuts page's cards use, which is what makes
    /// arriving on this page read like arriving on that one.
    /// </remarks>
    internal void PlayEntrance() => ListEntrance.ReplayColumn(ContentColumn);

    private void Localize()
    {
        HeaderTitle.Text = LocalizationService.Get("TabPreferences");
        HeaderSubtitle.Text = LocalizationService.Get("PreferencesHeader");

        CardTheme.Header = LocalizationService.Get("Theme");
        CardLanguage.Header = LocalizationService.Get("Language");
        SetToggleCard(CardSaveClosedHistory, "SaveClosedHistory", "SaveClosedHistoryTooltip");
        SetToggleCard(CardRestorePreviousWindows, "RestorePreviousWindows", "RestorePreviousWindowsTooltip");
        // 这一行的标题与说明完全由 ApplyTrayIconHeader / ApplyTrayIconRequirement 管理
        // （可用态：纯文本标题、无说明；不可用态：禁用色标题 + ⓘ 原因），所以这里只设标题。
        // 说明文案不再走 SetToggleCard，原先给它用的 HideTrayIconTooltip 资源键已从 9 个
        // resx 中一并删除，避免留下"代码不引用但语言文件仍在维护"的死键。
        CardHideTrayIcon.Header = LocalizationService.Get("HideTrayIcon");

        CardStartup.Header = LocalizationService.Get("AddToStartup");
        // 这一行的标题与说明由 ApplyHideWindowOnStartupRequirement 管理：它只在已注册开机自启时起作用，
        // 未注册时整行不显示。
        ApplyHideWindowOnStartupRequirement(RegistryManager.IsStartupEnabled);

        // 检查更新：标题与按钮固定，说明行随状态（当前版本 / 已是最新 / 发现新版本 / 失败）重建。
        CardUpdate.Header = LocalizationService.Get("AutoCheckForUpdates");
        BtnUpdate.Content = LocalizationService.Get(_updateRelease is null ? "CheckNow" : "DownloadAndInstall");
        ShowUpdateDescription();

        // One switch, named after what the user sees happen. The two switches this replaced ("tab reuse" and
        // "window interception") were two names for one behaviour — folding happens while either is on — so
        // their descriptions were bound to read alike; the fix is not better wording but one switch.
        SetToggleCard(CardMergeNewWindows, "MergeNewWindows", "MergeNewWindowsTooltip");

        CardNoMergeFolders.Header = LocalizationService.Get("NoMergeFolders");
        CardNoMergeFolders.Description = LocalizationService.Get("NoMergeFoldersTooltip");
        AddNoMergeFolderText.Text = LocalizationService.Get("AddFolder");

        // Every boolean row shows the state as text to the left of the switch (the native
        // ToggleSwitch placement). Set from our own resources so the label follows the in-app
        // language rather than whatever language the WinAppSDK resource package resolved to.
        LocalizeToggleLabels(SwSaveClosedHistory, SwRestorePreviousWindows, SwHideTrayIcon,
                             SwStartup, SwHideWindowOnStartup, SwMergeNewWindows,
                             SwAutoCheckUpdates);

        // SetToggleCard above restored the plain description; re-apply the disabled-state warning so a
        // language switch does not drop it.
        UpdateTrayIconCardState();

        // The rows carry their own accessible names, which are language-dependent like everything else.
        RebuildNoMergeFolderList();
    }

    private static void LocalizeToggleLabels(params ToggleSwitch[] switches)
    {
        var on = LocalizationService.Get("ToggleOn");
        var off = LocalizationService.Get("ToggleOff");

        foreach (var toggle in switches)
        {
            toggle.OnContent = on;
            toggle.OffContent = off;
        }
    }

    /// <summary>
    /// Index of the picker entry matching the stored setting, falling back to "follow system".
    /// <para>
    /// Matches on the <b>setting</b>, not on the language in effect: an unset setting still resolves
    /// to a concrete culture for display purposes, and indexing by that would show 简体中文 while
    /// the app is actually auto-detecting. Same distinction the language change handler relies on.
    /// </para>
    /// </summary>
    private static int FindLanguageIndex(DisplayItem<string>[] languages)
    {
        var saved = SettingsManager.Language;
        var index = Array.FindIndex(languages, item => item.Value == saved);
        return index < 0 ? 0 : index;
    }

    private static void SetToggleCard(SettingsCard card, string headerKey, string descriptionKey)
    {
        card.Header = LocalizationService.Get(headerKey);
        card.Description = LocalizationService.Get(descriptionKey);
    }

    /// <summary>
    /// Hiding the tray icon is only safe when a hotkey can bring the window back. Same guard the
    /// WPF build applied.
    /// </summary>
    private void UpdateTrayIconCardState()
    {
        if (_services is null) return;

        var canToggle = _services.ProfileManager
            .GetProfiles()
            .Any(p => p is { IsEnabled: true, Action: HotKeyAction.ToggleVisibility } &&
                      (p.IsMouse ? SettingsManager.IsMouseHookActive : SettingsManager.IsKeyboardHookActive));

        CardHideTrayIcon.IsEnabled = canToggle;
        ApplyTrayIconRequirement(canToggle);

        if (canToggle) return;

        SwHideTrayIcon.IsOn = false;
        SettingsManager.IsTrayIconHidden = false;

        if (_services.Tray is { } tray)
            tray.IsVisible = true;
    }

    /// <summary>
    /// Explains why the row is unavailable.
    /// <para>
    /// The WPF build kept the check box clickable and answered with a message box after the fact.
    /// Here the row is disabled up front and its description becomes an exclamation mark plus the
    /// reason, so the restriction is visible before the user tries it.
    /// </para>
    /// </summary>
    private void ApplyTrayIconRequirement(bool canToggle)
    {
        // The header is rebuilt here as well (not only from the IsEnabled callback), because a language
        // switch re-runs Localize() -> UpdateTrayIconCardState() and the title text has to be re-localised.
        ApplyTrayIconHeader(canToggle);

        if (canToggle)
        {
            // 标题「隐藏托盘图标」已经说明了用途，启用态不再挂一行冗余描述。
            // null! —— SettingsCard.Description 是非空引用类型，但「没有描述」正是把内容清空。
            CardHideTrayIcon.Description = null!;
            ToolTipService.SetToolTip(CardHideTrayIcon, null);
            return;
        }

        var reason = LocalizationService.Get("HideTrayIconUnavailable");

        // Official settings-page treatment: a solid accent badge, then the sentence in the secondary
        // text colour. Both are semantic brushes, so light/dark/high contrast all work.
        //
        // The badge is composed from an ellipse and the letter "i" because Segoe Fluent Icons ships
        // only the *outline* info glyph (E946) — Windows' own settings pages use a solid one. Two
        // primitives as a container also keeps the icon's optical centre on the text's centre line,
        // which a FontIcon cannot do (its glyph sits above the centre of its line box).
        var badge = new Grid
        {
            Width = BadgeSize,
            Height = BadgeSize,
            VerticalAlignment = VerticalAlignment.Center
        };

        badge.Children.Add(new Ellipse
        {
            Width = BadgeSize,
            Height = BadgeSize,
            // 徽章与说明文字保持"可读"，不随行的禁用态一起变灰 —— 它们承载的是
            // "为什么用不了"这条信息，变灰就等于把提示藏起来了。
            Style = ResolveStyle("AccentBadgeFillStyle")
        });

        badge.Children.Add(new TextBlock
        {
            Text = "i",
            FontSize = 8,
            FontWeight = FontWeights.SemiBold,
            Style = ResolveStyle("OnAccentTextStyle"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        var text = new TextBlock
        {
            Text = reason,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            // Primary text colour, not the secondary/disabled one: the sentence is the whole point of
            // the badge and has to stay readable in both themes.
            Style = ResolveStyle("PrimaryCaptionTextStyle")
        };

        // Grid, not a horizontal StackPanel: a StackPanel measures the TextBlock with infinite
        // width, so its TextWrapping.Wrap never engages and the sentence CLIPS at the card edge
        // once the window is at the content-column floor (MainWindow.ContentMinWidth = 620 epx).
        // The star column gives the text a real width
        // constraint so it wraps instead.
        var description = new Grid
        {
            ColumnSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            // The badge's filled disc makes this row look tighter than a plain text description, so
            // give the block a little more air under the header.
            Margin = new Thickness(0, DescriptionTopGap, 0, 0)
        };
        description.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        description.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(badge, 0);
        Grid.SetColumn(text, 1);
        description.Children.Add(badge);
        description.Children.Add(text);

        CardHideTrayIcon.Description = description;
        ToolTipService.SetToolTip(CardHideTrayIcon, reason);
    }

    /// <summary>Paints the tray-icon row's title for its current availability.</summary>
    private void ApplyTrayIconHeader(bool available) => ApplyRequirementHeader(CardHideTrayIcon, "HideTrayIcon", available);

    /// <summary>
    /// Paints a row's own title for the row's current availability.
    /// <para>
    /// The title is always given an explicit foreground rather than inheriting one from the card's
    /// header presenter. The toolkit template hands the presenter the disabled brush from its
    /// <c>Disabled</c> visual state, and that hold is NOT released when <c>IsEnabled</c> is set back to
    /// true while the cached page is being re-attached (measured: the presenter sat on the disabled
    /// brush while <c>IsEnabled</c> was already true). A plain-string header inherits that stuck grey,
    /// which is exactly why the title stayed grey after the row became available again. An explicit
    /// style beats inheritance, so the colour is right in both directions. The styles use
    /// <c>{ThemeResource}</c> so the brush follows the theme actually in effect (a brush fetched in
    /// code would resolve against the application theme instead).
    /// </para>
    /// <para>
    /// Driven from an IsEnabled-changed callback as well as from the page's navigation / localisation
    /// pass: the header follows the row's state immediately, however and whenever that changes.
    /// </para>
    /// </summary>
    private static void ApplyRequirementHeader(SettingsCard card, string headerKey, bool available)
    {
        card.Header = new TextBlock
        {
            Text = LocalizationService.Get(headerKey),
            TextWrapping = TextWrapping.Wrap,
            Style = ResolveStyle(available ? "EnabledSettingHeaderTextStyle" : "DisabledSettingHeaderTextStyle")
        };
    }

    /// <summary>
    /// Shows the "don't show the window at startup" row only while the app is registered to start with
    /// Windows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The setting is read on the sign-in launch and on no other (<c>App.OnLaunched</c> checks it together
    /// with <c>RegistryManager.WasStartedAtSignIn</c>), so with no startup entry it controls nothing.
    /// </para>
    /// <para>
    /// Hidden rather than disabled: this is the platform's own treatment for a setting whose parent switch
    /// is off, and it is the rule this window already follows — a profile's path or delay row appears with
    /// the action that reads it and not before. A greyed row with a reason was tried first and asks the user
    /// to work out which switch to reach for; a row that is simply not there does not.
    /// </para>
    /// <para>
    /// The stored value is left alone, so registering startup later brings the user's own choice back
    /// rather than a default.
    /// </para>
    /// </remarks>
    private void ApplyHideWindowOnStartupRequirement(bool startupRegistered)
    {
        CardHideWindowOnStartup.Visibility = startupRegistered ? Visibility.Visible : Visibility.Collapsed;
        SetToggleCard(CardHideWindowOnStartup, "HideWindowOnStartup", "HideWindowOnStartupTooltip");
    }

    // ---- Updates ---------------------------------------------------------------------------------

    /// <summary>The version of this build, read the way the about page reads it.</summary>
    private static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 1);

    /// <summary>The release the last check found, or null while the build is current.</summary>
    private UpdateRelease? _updateRelease;

    /// <summary>Set while a check or a download is in flight, so the button cannot start a second one.</summary>
    private bool _updateBusy;

    /// <summary>A message that replaces the version line until the next check (a failure, or a hand-over).</summary>
    private string? _updateMessage;

    private void OnAutoCheckUpdatesToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsManager.AutoCheckForUpdates = SwAutoCheckUpdates.IsOn;
    }

    private async void OnUpdateButtonClick(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;

        // One button, two meanings: it checks, then it installs what the check found.
        if (_updateRelease is { } release)
        {
            await DownloadAndInstallAsync(release);
            return;
        }

        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        _updateBusy = true;
        _updateMessage = null;
        BtnUpdate.IsEnabled = false;

        try
        {
            var result = await UpdateService.CheckAsync(CurrentVersion);

            _updateRelease = result.Status == UpdateCheckStatus.UpdateAvailable ? result.Release : null;
            if (result.Status == UpdateCheckStatus.Failed)
            {
                // The reason goes to the log, not to the user: "could not check" is what they need to see, and
                // what went wrong is what makes it fixable.
                _updateMessage = LocalizationService.Get("UpdateCheckFailed");
                StartupLog.Step($"update check failed: {result.Reason}");
            }
        }
        finally
        {
            _updateBusy = false;
            BtnUpdate.IsEnabled = true;
            BtnUpdate.Content = LocalizationService.Get(_updateRelease is null ? "CheckNow" : "DownloadAndInstall");
            ShowUpdateDescription();
        }
    }

    /// <summary>
    /// The row's second line: the version in use, then what the last check made of it.
    /// </summary>
    private void ShowUpdateDescription()
    {
        // What the startup pass found counts too, so the row is not blank about an answer the app already has.
        var known = UpdateService.LastCheck;
        if (known is { Status: UpdateCheckStatus.UpdateAvailable, Release: { } startupRelease } &&
            _updateRelease is null && _updateMessage is null)
        {
            _updateRelease = startupRelease;
            BtnUpdate.Content = LocalizationService.Get("DownloadAndInstall");
        }

        var state = _updateMessage;

        if (state is null && _updateRelease is { } release)
            state = LocalizationService.Get("UpdateAvailable") + " " + release.Version.ToString(3);

        CardUpdate.Description = LocalizationService.Get("CurrentVersion") + " " + CurrentVersion.ToString(3)
                                 + "  ·  " + (state ?? LocalizationService.Get("UpToDate"));
    }

    private async Task DownloadAndInstallAsync(UpdateRelease release)
    {
        // Which file is wanted depends on how this copy was put on the machine: the installer can replace an
        // installed copy in place, while an unzipped one cannot replace its own running files.
        var installed = UpdateService.IsInstalledBuild;
        var url = installed ? release.InstallerUrl : release.PortableUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            _updateMessage = LocalizationService.Get("UpdateCheckFailed");
            ShowUpdateDescription();
            return;
        }

        var destination = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ExplorerTabUtility_" + release.Tag + (installed ? "_Setup.exe" : "_Portable.zip"));

        _updateBusy = true;
        BtnUpdate.IsEnabled = false;
        BtnUpdate.Visibility = Visibility.Collapsed;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;

        try
        {
            var progress = new Progress<double>(value =>
            {
                UpdateProgress.Value = value * 100;
                if (value < 1) return;

                CardUpdate.Description = LocalizationService.Get("CurrentVersion") + " "
                                         + CurrentVersion.ToString(3) + "  ·  "
                                         + LocalizationService.Get(
                                             installed ? "UpdateInstalling" : "UpdateDownloadedPortable");
            });

            var downloading = LocalizationService.Get("CurrentVersion") + " " + CurrentVersion.ToString(3)
                              + "  ·  " + LocalizationService.Get("Downloading");
            CardUpdate.Description = downloading;

            if (!await UpdateService.DownloadAsync(url, destination, progress))
            {
                _updateMessage = LocalizationService.Get("UpdateCheckFailed");
                ShowUpdateDescription();
                return;
            }

            if (installed) UpdateService.LaunchInstaller(destination);
            else Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + destination + "\""));
        }
        catch (Exception ex)
        {
            StartupLog.Fail("PreferencesPage.DownloadAndInstallAsync", ex);
        }
        finally
        {
            _updateBusy = false;
            BtnUpdate.IsEnabled = true;
            BtnUpdate.Visibility = Visibility.Visible;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Runs the downloaded Setup, and brings the app back when it is done.
    /// </summary>
    /// <remarks>
    /// Detached through <c>cmd</c> deliberately. The installer ends this process while it works —
    /// <c>PrepareToInstall</c> in installer.iss runs taskkill, because this app hides to the tray instead of
    /// exiting and Inno's own Restart Manager handshake would wait forever for a close that never comes — so
    /// nothing running inside this process can start the app again afterwards. <c>/wait</c> holds the helper
    /// until the installer has finished; the second <c>start</c> then opens the freshly installed copy.
    /// Silent rather than interactive because the user has already agreed by pressing the button.
    /// </remarks>
    private static void StartInstaller(string setupPath)
    {
        var exePath = Environment.ProcessPath
                      ?? System.IO.Path.Combine(AppContext.BaseDirectory, "ExplorerTabUtility.exe");

        var command = "/c start \"\" /wait \"" + setupPath + "\" /SILENT /SUPPRESSMSGBOXES /NORESTART /SP-"
                      + " & start \"\" \"" + exePath + "\"";

        Process.Start(new ProcessStartInfo("cmd.exe", command) { CreateNoWindow = true });
    }

    /// <summary>Fetches an application-level style (its {ThemeResource} setters follow the theme).</summary>
    private static Style? ResolveStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var found) && found is Style style ? style : null;

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CbTheme.SelectedItem is not DisplayItem<int> item) return;

        // Persist only when the value actually changed: a regenerated item list can re-raise
        // SelectionChanged with the same or a reset item, and writing that back would silently
        // change the user's setting.
        if (SettingsManager.ThemeMode != item.Value)
            SettingsManager.ThemeMode = item.Value;

        // Re-apply even when the value did NOT change. "Follow system" depends on external state
        // (the OS theme), so re-selecting it has to re-resolve that state — the old early-return
        // made picking "Follow system" while it was already the setting a complete no-op, which is
        // why the window stayed in the wrong theme until something else forced a repaint.
        // The window owns the one place that maps ThemeMode to an ElementTheme, so the picker and
        // the startup path can never drift apart.
        App.MainWindowInstance?.ApplySavedTheme();

        // The tray menu is a native popup and does not follow the window, so it needs the same answer
        // applied to it — otherwise it keeps the theme it was built with until the next start.
        _services?.Tray?.ApplyTheme();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CbLanguage.SelectedItem is not DisplayItem<string> item) return;

        // Compare against the stored setting, which is what the picker now indexes by — comparing
        // against the language in effect used to swallow a deliberate "pick Japanese while
        // following the system" (the effective culture already matched, so nothing was pinned),
        // and later, comparing against a setting the picker could not express is what silently
        // pinned "en" onto installs nobody had touched. With a real "follow system" entry the two
        // never disagree: the selection IS the setting, so any change is a real user choice.
        if (SettingsManager.Language == item.Value) return;

        SettingsManager.Language = item.Value;

        // An empty selection re-runs auto-detection inside SetLanguage rather than being ignored.
        LocalizationService.Instance.SetLanguage(item.Value);

        // Refresh visible strings, then rebuild the shell so the navigation labels and every page
        // pick up the new culture (the previous version only refreshed this page).
        Localize();
        _services?.ProfileManager.RefreshLocalization();
        // The tray menu is built once at startup and is not part of the window tree, so it does not
        // follow ReloadForLanguage: without this its entries kept the language they were built in.
        _services?.Tray?.RefreshLocalization();
        App.MainWindowInstance?.ReloadForLanguage();
    }

    private void OnSaveClosedHistoryToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsManager.SaveClosedHistory = SwSaveClosedHistory.IsOn;
    }

    private void OnRestorePreviousWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsManager.RestorePreviousWindows = SwRestorePreviousWindows.IsOn;
    }

    private void OnHideTrayIconToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _services is null) return;

        SettingsManager.IsTrayIconHidden = SwHideTrayIcon.IsOn;

        if (_services.Tray is { } tray)
            tray.IsVisible = !SwHideTrayIcon.IsOn;
    }

    private void OnHideWindowOnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsManager.HideWindowOnStartup = SwHideWindowOnStartup.IsOn;
    }

    // ---- Folding new windows into tabs -----------------------------------------------------------

    /// <summary>Set once the hook-state events have been subscribed; this page is cached.</summary>
    private bool _hookSwitchesWatched;

    /// <summary>
    /// Applies the fold switch through the hook engine, which owns the rules around it: folding needs the
    /// interception that performs it, so switching this on switches that on, and switching it off stops the
    /// folding everywhere — including the interception that was only ever hiding windows in order to fold
    /// them.
    /// </summary>
    private void OnMergeNewWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _services?.HookManager?.SetReuseTabs(SwMergeNewWindows.IsOn);
    }

    /// <summary>
    /// Re-reads the switch when it is moved, wherever it was moved from.
    /// </summary>
    /// <remarks>
    /// The tray menu and the two hotkey actions move the same setting. The page keeps no copy of its own: it
    /// reads the settings back, which the switch implementations keep authoritative, so the two surfaces
    /// cannot end up showing different states — which is what happened while this rule lived in the menu.
    /// </remarks>
    private void WatchHookSwitches()
    {
        if (_hookSwitchesWatched) return;
        if (_services?.HookManager is not { } hooks) return;

        _hookSwitchesWatched = true;
        hooks.WindowInterceptionChanged += RefreshHookSwitches;
        hooks.ReuseTabsChanged += RefreshHookSwitches;
    }

    private void RefreshHookSwitches() => _services?.Dispatcher.TryPost(() =>
    {
        _loading = true;
        try
        {
            SwMergeNewWindows.IsOn = SettingsManager.ReuseTabs;
        }
        finally
        {
            _loading = false;
        }
    });

    /// <summary>
    /// Opens a folder picker and adds the chosen folder to the exempt list.
    /// </summary>
    private async void OnAddNoMergeFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            // Required: a picker with no filter throws on PickSingleFolderAsync.
            picker.FileTypeFilter.Add("*");

            // Start at "This PC", not at the folder the picker last used. The picker never lets you commit
            // the folder it opened *in* — its button stays disabled until the selection moves — so starting
            // where the user left off put the folder they most likely wanted out of reach, and the only way
            // forward was to walk into some other folder first. From This PC every folder is a step in.
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;

            var window = App.MainWindowInstance;
            if (window is null) return;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            var current = SettingsManager.NoMergeFolders;
            if (current.Any(item => string.Equals(item, folder.Path, StringComparison.OrdinalIgnoreCase))) return;

            SettingsManager.NoMergeFolders = [.. current, folder.Path];
            RebuildNoMergeFolderList();
        }
        catch (Exception ex)
        {
            StartupLog.Fail("PreferencesPage.OnAddNoMergeFolderClick", ex);
        }
    }

    private void OnRemoveNoMergeFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string folder }) return;

        SettingsManager.NoMergeFolders = SettingsManager.NoMergeFolders
            .Where(item => !string.Equals(item, folder, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        RebuildNoMergeFolderList();
    }

    /// <summary>
    /// The rows inside the no-merge drawer.
    /// </summary>
    /// <remarks>
    /// Observable, and handed to the expander as its <c>ItemsSource</c>, because the list changes while the
    /// page is open. Adding to the expander's own <c>Items</c> works for rows that exist before it is loaded
    /// and does nothing for rows added afterwards — which is exactly the case the add button creates, and
    /// what made the button look like it did nothing: the folder was being saved, and the row that should
    /// have shown it was never drawn.
    /// </remarks>
    private readonly ObservableCollection<object> _noMergeRows = [];

    /// <summary>
    /// Rebuilds the exempt-folder rows: one <see cref="SettingsCard"/> per folder, the path on its leading
    /// side and the button that takes it out on the trailing side — the layout Windows uses for a list of
    /// locations. Built in code because the paths are data. An empty list gets one muted row saying so, so
    /// that opening the drawer never shows a blank area.
    /// </summary>
    private void RebuildNoMergeFolderList()
    {
        _noMergeRows.Clear();

        // An empty list still says so. Expanding to a blank area reads as a control that failed to load, and
        // this list is empty on every fresh install — it is the first thing anyone sees of it.
        if (SettingsManager.NoMergeFolders.Count == 0)
        {
            _noMergeRows.Add(new SettingsCard
            {
                // Styled rather than coloured: the style's {ThemeResource} setter follows the theme that is
                // actually in effect, which a brush fetched here would not — the same reason the profile
                // cards apply a style instead of a colour.
                Header = new TextBlock
                {
                    Text = LocalizationService.Get("NoMergeFoldersEmpty"),
                    Style = ResolveStyle("SecondaryCaptionTextBlockStyle"),
                    TextWrapping = TextWrapping.Wrap
                },
                HorizontalAlignment = HorizontalAlignment.Stretch
            });

            return;
        }

        var removeLabel = LocalizationService.Get("NoMergeCurrentFolder");

        foreach (var folder in SettingsManager.NoMergeFolders)
        {
            var remove = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
                Tag = folder
            };
            AutomationProperties.SetName(remove, removeLabel);
            remove.Click += OnRemoveNoMergeFolderClick;

            _noMergeRows.Add(new SettingsCard
            {
                Header = folder,
                Content = remove,
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
        }
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var desired = SwStartup.IsOn;
        if (desired == RegistryManager.IsStartupEnabled) return;

        RegistryManager.ToggleStartup();

        _loading = true;
        SwStartup.IsOn = RegistryManager.IsStartupEnabled;
        _loading = false;

        // The startup-hide row only means something while the app is registered to start with Windows.
        ApplyHideWindowOnStartupRequirement(RegistryManager.IsStartupEnabled);
    }
}
