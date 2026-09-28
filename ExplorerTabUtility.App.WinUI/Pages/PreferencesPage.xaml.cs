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
                // the picker's order.
                var languages = LocalizationService.SupportedLanguages
                    .Select(l => new DisplayItem<string>(l.NativeName, l.Code))
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
        SetToggleCard(CardHideTrayIcon, "HideTrayIcon", "HideTrayIconTooltip");

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

    /// <summary>Index of the language currently in effect, falling back to the first entry.</summary>
    private static int FindLanguageIndex(DisplayItem<string>[] languages)
    {
        var current = LocalizationService.Instance.Language;
        var index = Array.FindIndex(languages, item => item.Value == current);
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
        if (canToggle)
        {
            CardHideTrayIcon.Description = LocalizationService.Get("HideTrayIconTooltip");
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

        var description = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            // The badge's filled disc makes this row look tighter than a plain text description, so
            // give the block a little more air under the header.
            Margin = new Thickness(0, DescriptionTopGap, 0, 0)
        };
        description.Children.Add(badge);
        description.Children.Add(text);

        CardHideTrayIcon.Description = description;
        ToolTipService.SetToolTip(CardHideTrayIcon, reason);
    }


    /// <summary>Fetches an application-level style (its {ThemeResource} setters follow the theme).</summary>
    private static Style? ResolveStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var found) && found is Style style ? style : null;

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CbTheme.SelectedItem is not DisplayItem<int> item) return;

        // No-op writes are skipped: a regenerated item list can re-raise SelectionChanged with the
        // same or a reset item, and persisting that would silently change the user's setting.
        if (SettingsManager.ThemeMode == item.Value) return;

        SettingsManager.ThemeMode = item.Value;

        // The window owns the one place that maps ThemeMode to an ElementTheme, so the picker and
        // the startup path can never drift apart.
        App.MainWindowInstance?.ApplySavedTheme();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CbLanguage.SelectedItem is not DisplayItem<string> item) return;

        // Compare against the language that is actually in effect, not the raw setting. With the
        // setting unset ("" = follow the system) the two differ, so a SelectionChanged the user did
        // not cause — the picker regenerating its items raises one — would pass the old guard,
        // silently pin the language to a concrete value and reload the whole shell for nothing.
        if (LocalizationService.Instance.Language == item.Value) return;

        SettingsManager.Language = item.Value;
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
