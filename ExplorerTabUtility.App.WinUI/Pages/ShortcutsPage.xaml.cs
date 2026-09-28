using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
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

            LogGeometryOnce();

            Localize();

            }
        catch (Exception ex)
        {
            StartupLog.Fail("ShortcutsPage.OnNavigatedTo", ex);
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

        var picker = new FileOpenPicker();
        InitializeWithWindow(picker);

        picker.FileTypeFilter.Add(".json");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var json = await File.ReadAllTextAsync(file.Path);
        _services.ProfileManager.ImportProfiles(json);
        _services?.Tray?.RefreshProfileMenus();
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;

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
