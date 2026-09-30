using System;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Navigation;
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
        }
        catch (Exception ex)
        {
            StartupLog.Fail("PreferencesPage.OnNavigatedTo", ex);
        }
    }

    private void Localize()
    {
        HeaderTitle.Text = LocalizationService.Get("TabPreferences");
        HeaderSubtitle.Text = LocalizationService.Get("PreferencesHeader");

        CardTheme.Header = LocalizationService.Get("Theme");
        CardLanguage.Header = LocalizationService.Get("Language");
        SetToggleCard(CardSaveClosedHistory, "SaveClosedHistory", "SaveClosedHistoryTooltip");
        SetToggleCard(CardRestorePreviousWindows, "RestorePreviousWindows", "RestorePreviousWindowsTooltip");
        // 这一行的标题与说明完全由 ApplyTrayIconHeader / ApplyTrayIconRequirement 管理
        // （可用态：纯文本标题、无说明；不可用态：禁用色标题 + ⓘ 原因），所以这里只设标题，
        // 不再引用 HideTrayIconTooltip。其余设置卡仍走 SetToggleCard。
        CardHideTrayIcon.Header = LocalizationService.Get("HideTrayIcon");

        CardStartup.Header = LocalizationService.Get("AddToStartup");
        SetToggleCard(CardHideWindowOnStartup, "HideWindowOnStartup", "HideWindowOnStartupTooltip");

        // Every boolean row shows the state as text to the left of the switch (the native
        // ToggleSwitch placement). Set from our own resources so the label follows the in-app
        // language rather than whatever language the WinAppSDK resource package resolved to.
        LocalizeToggleLabels(SwSaveClosedHistory, SwRestorePreviousWindows, SwHideTrayIcon,
                             SwStartup, SwHideWindowOnStartup);

        // SetToggleCard above restored the plain description; re-apply the disabled-state warning so a
        // language switch does not drop it.
        UpdateTrayIconCardState();
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
        // once the window is at the 540 epx floor. The star column gives the text a real width
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

    /// <summary>
    /// Paints the row's own title for the row's current availability.
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
    private void ApplyTrayIconHeader(bool available)
    {
        var title = LocalizationService.Get("HideTrayIcon");

        CardHideTrayIcon.Header = new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.Wrap,
            Style = ResolveStyle(available ? "EnabledSettingHeaderTextStyle" : "DisabledSettingHeaderTextStyle")
        };
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

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var desired = SwStartup.IsOn;
        if (desired == RegistryManager.IsStartupEnabled) return;

        RegistryManager.ToggleStartup();

        _loading = true;
        SwStartup.IsOn = RegistryManager.IsStartupEnabled;
        _loading = false;
    }
}
