using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using ExplorerTabUtility.Abstractions;
using ExplorerTabUtility.App.Services;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;

namespace ExplorerTabUtility.App.Pages;

/// <summary>
/// Hotkey list page. Native toolbar + a vertical stack of <c>SettingsExpander</c> cards.
/// </summary>
public sealed partial class ShortcutsPage : Page
{
    private AppServices? _services;

    public ShortcutsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            // Fall back to the shell's instance: a re-navigation for a language switch used to pass
            // null, which left this page silently skipping its whole setup.
            _services = e.Parameter as AppServices ?? App.MainWindowInstance?.Services;
            StartupLog.Step($"ShortcutsPage.OnNavigatedTo (services={_services is not null})");

            if (_services is null) return;

            // Attach is idempotent, so re-navigating with the cached page instance is a no-op.
            _services.Profiles.Attach(ProfilesPanel);

            StartupLog.Step($"ShortcutsPage: profiles attached ({_services.Profiles.Cards.Count} cards)");

            PlayEntrance();

            LogGeometryOnce();

            Localize();

            }
        catch (Exception ex)
        {
            StartupLog.Fail("ShortcutsPage.OnNavigatedTo", ex);
        }
    }

    /// <summary>
    /// Plays this page's entrance: the heading and the toolbar, then the cards that follow them.
    /// </summary>
    /// <remarks>
    /// The cards are already in the panel from the previous visit, so "attach" is a no-op and no entrance
    /// would play — the list is only ever *given* its items once, on the first visit. Entering the page is
    /// itself the moment the content arrives, so the page hands both halves over again: its own blocks here,
    /// and the cards through the host, which is what replays them. On the first visit the panel is still
    /// empty and that half does nothing; the delayed add that follows queues its own entrance.
    /// </remarks>
    internal void PlayEntrance()
    {
        // The card list takes the position its own blocks reached, so the heading leads the cards instead of
        // racing them — one wave over the page, not two over its halves.
        var cardWaveIndex = ListEntrance.ReplayColumn(ContentColumn, ProfilesPanel);

        _services?.Profiles.Replay(cardWaveIndex);
    }

    /// <summary>
    /// Ends a hotkey capture when this page is left.
    /// </summary>
    /// <remarks>
    /// The capture is the one piece of state here that reaches outside the window: it installs a global
    /// keyboard and mouse hook that swallows the chords it records, and it *suspends the app's own hotkeys*
    /// for as long as it is running (<c>HookManager.KeybindingStarted</c> stops both hooks and
    /// <c>KeybindingStopped</c> starts them again). The window only ended it on deactivation, which does not
    /// happen when the user simply picks another page — and then a profile that is no longer on screen kept
    /// the keyboard, every bindable key pressed anywhere was written into it, and the app's hotkeys stayed
    /// dead until the window happened to lose activation. Navigating away is leaving the thing being edited,
    /// so it ends the edit.
    /// </remarks>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        try
        {
            _services?.ProfileManager.EndHotKeyCapture();
        }
        catch (Exception ex)
        {
            StartupLog.Fail("ShortcutsPage.OnNavigatedFrom", ex);
        }
    }

    private bool _geometryLogged;

    /// <summary>
    /// One-shot layout report. Used to diagnose the content column being pushed off the right edge;
    /// a screenshot alone cannot say which element is at fault.
    /// </summary>
    private async void LogGeometryOnce()
    {
        if (_geometryLogged) return;
        _geometryLogged = true;

        await Task.Delay(700);

        try
        {
            var column = ContentColumn;
            var origin = column.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));

            var scroller = column.Parent as ScrollViewer;
            var window = App.MainWindowInstance;

            StartupLog.Step(
                $"geom: window={window?.AppWindow.Size.Width ?? 0}x{window?.AppWindow.Size.Height ?? 0} " +
                $"svW={scroller?.ActualWidth ?? 0:F0} svViewport={scroller?.ViewportWidth ?? 0:F0} " +
                $"colX={origin.X:F0} colW={column.ActualWidth:F0}");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("ShortcutsPage.LogGeometryOnce", ex);
        }
    }

    private void Localize()
    {
        HeaderTitle.Text = LocalizationService.Get("TabShortcuts");
        HeaderSubtitle.Text = LocalizationService.Get("ShortcutsHeader");

        BtnNewProfile.Content = LocalizationService.Get("BtnNewProfile");
        BtnImport.Content = LocalizationService.Get("BtnImport");
        BtnExport.Content = LocalizationService.Get("BtnExport");

        ToolTipService.SetToolTip(BtnNewProfile, LocalizationService.Get("BtnNewProfile"));
        ToolTipService.SetToolTip(BtnImport, LocalizationService.Get("BtnImport"));
        ToolTipService.SetToolTip(BtnExport, LocalizationService.Get("BtnExport"));
    }

    private void OnNewProfileClick(object sender, RoutedEventArgs e)
        => _services?.ProfileManager.AddProfile();

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;

        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow(picker);

            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var json = await File.ReadAllTextAsync(file.Path);
            _services.ProfileManager.ImportProfiles(json);
            _services.Tray?.RefreshProfileMenus();
        }
        catch (Exception ex)
        {
            // An async void handler with no local catch is invisible to the user and, on the wrong
            // thread, fatal — mirror MainWindow's pattern and report the failure (AUD-12).
            StartupLog.Fail("ShortcutsPage.OnImportClick", ex);
            await ShowErrorAsync(LocalizationService.Get("ImportFailed"));
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;

        try
        {
            var picker = new FileSavePicker();
            InitializeWithWindow(picker);

            picker.SuggestedFileName = Constants.HotKeyProfilesFileName;
            picker.FileTypeChoices.Add("JSON", new[] { ".json" });

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            await File.WriteAllTextAsync(
                file.Path,
                _services.ProfileManager.ExportProfiles(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            StartupLog.Fail("ShortcutsPage.OnExportClick", ex);
            await ShowErrorAsync(LocalizationService.Get("ExportFailed"));
        }
    }

    /// <summary>Shows a non-fatal error to the user; never rethrows (the caller is an async void).</summary>
    private async Task ShowErrorAsync(string message)
    {
        if (_services is null) return;

        try
        {
            await _services.Dialogs.ShowAsync(
                message,
                LocalizationService.Get("AppTitle"),
                DialogButton.OK,
                DialogIcon.Error);
        }
        catch (Exception ex)
        {
            // Another ContentDialog may already be on screen; the service serializes them, but a
            // failure here must still not escape this async void.
            StartupLog.Fail("ShortcutsPage.ShowErrorAsync", ex);
        }
    }

    /// <summary>
    /// Unpackaged WinUI apps must hand the picker an owner window, otherwise the call fails with
    /// "no window to attach to".
    /// </summary>
    private static void InitializeWithWindow(object picker)
    {
        var window = App.MainWindowInstance;
        if (window is null) return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
