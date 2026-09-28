using System;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using ExplorerTabUtility.Abstractions;
using ExplorerTabUtility.App.Pages;
using ExplorerTabUtility.App.Services;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.WinAPI;

namespace ExplorerTabUtility.App;

/// <summary>
/// The settings window. System title bar, Mica backdrop, <see cref="NavigationView"/> navigation.
/// <para>
/// Nothing here draws chrome: window rounding, shadow, title-bar buttons and their hover states
/// are all provided by the OS. The WPF build's ~120 lines of custom chrome, drag/maximise
/// workarounds and title-bar animations are simply gone.
/// </para>
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;
    private bool _exiting;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        StartupLog.Step("MainWindow: InitializeComponent ok");

        Title = LocalizationService.Get("AppTitle");

        ApplyWindowChrome();
        StartupLog.Step("MainWindow: chrome applied");

        ApplySavedTheme();
        StartupLog.Step($"MainWindow: theme mode = {SettingsManager.ThemeMode}");

        LocalizeNavigation();
        WireServices();
        StartupLog.Step("MainWindow: services wired");

        // Navigate only once the NavigationView is loaded. Navigating from the constructor runs
        // before the frame is realised, which leaves the first page blank and — because
        // CurrentSourcePageType is already recorded — makes the guard below skip every later
        // attempt to select that same item. That is why the shortcuts page never appeared.
        Nav.Loaded += OnNavLoaded;
    }

    private bool _initialNavigationDone;

    private void OnNavLoaded(object sender, RoutedEventArgs e)
    {
        // XamlRoot is available from here on, which the DPI-aware sizing needs.
        ApplyInitialSize();

        // Responsive pane: the NavigationView keeps its pane open at any width in "Left" mode, so
        // the collapse has to be driven from here. Subscribed after ApplyInitialSize so the first
        // run of the handler already sees the restored size.
        Nav.SizeChanged += OnNavSizeChanged;
        UpdatePaneForWidth(Nav.ActualWidth);

        if (_initialNavigationDone) return;
        _initialNavigationDone = true;

        Nav.SelectedItem = NavShortcuts;
        StartupLog.Step("MainWindow: initial navigation requested");
    }

    /// <summary>
    /// Width below which the pane is collapsed automatically.
    /// <para>
    /// The window itself cannot go below 820 epx (see <see cref="ApplyInitialSize"/>), which leaves
    /// only 820 − 240 = 580 epx for the settings column — narrow enough that the value boxes and the
    /// folder pickers start to truncate. 900 gives the column roughly 660 epx, which is the point
    /// where the rows stop being squeezed.
    /// </para>
    /// </summary>
    private const double PaneCollapseWidth = 900;

    /// <summary>True when the pane was closed by <see cref="UpdatePaneForWidth"/> rather than by the user.</summary>
    private bool _paneAutoCollapsed;

    private void OnNavSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePaneForWidth(e.NewSize.Width);

    /// <summary>
    /// Keeps the pane from eating the settings column on a narrow window, and puts it back when there
    /// is room again.
    /// <para>
    /// A manual toggle always wins: if the user closed the pane themselves the width never re-opens
    /// it, and if they opened it on a narrow window it stays open until the width crosses the
    /// threshold again.
    /// </para>
    /// </summary>
    private void UpdatePaneForWidth(double width)
    {
        if (width <= 0) return;

        if (width < PaneCollapseWidth)
        {
            if (!Nav.IsPaneOpen) return;
            _paneAutoCollapsed = true;
            Nav.IsPaneOpen = false;
            return;
        }

        if (!_paneAutoCollapsed) return;
        _paneAutoCollapsed = false;
        Nav.IsPaneOpen = true;
    }

    private void ApplyWindowChrome()
    {
        try
        {
            AppWindow.SetIcon(App.IconPath);
        }
        catch
        {
            // Missing icon file is not fatal.
        }

        // Mica is the Windows 11 system material. On Windows 10 it is unsupported and the window
        // keeps the default opaque background — no fallback drawing needed.
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();

        ApplyCustomTitleBar();
    }

    /// <summary>
    /// Replaces the stock title bar row with the app's own, so the pane toggle, the app name and the
    /// system-drawn minimise/maximise/close buttons sit on one row.
    /// <para>
    /// <c>ExtendsContentIntoTitleBar</c> hands the top strip of the window to XAML, while
    /// <c>AppWindow.TitleBar</c> keeps the caption buttons — those are never redrawn here, which is
    /// what keeps this consistent with "no custom chrome".
    /// </para>
    /// </summary>
    private void ApplyCustomTitleBar()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBar);

            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

            TitleText.Text = LocalizationService.Get("AppTitle");

            ToolTipService.SetToolTip(BtnPaneToggle, LocalizationService.Get("TogglePane"));
            AutomationProperties.SetName(BtnPaneToggle, LocalizationService.Get("TogglePane"));

            StartupLog.Step("MainWindow: custom title bar applied");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("MainWindow.ApplyCustomTitleBar", ex);
        }
    }

    /// <summary>
    /// Applies the saved theme to the XAML root.
    /// <para>
    /// Nothing did this at launch before: the theme was only ever applied inside the preferences
    /// picker's change handler, so a saved Dark preference was ignored on every start — the window
    /// came up in the system theme while the picker still read "Dark". Both paths now go through
    /// this single method.
    /// </para>
    /// <para>
    /// Set on the root element rather than <c>Application.RequestedTheme</c>, which may only be
    /// assigned before the first window is created.
    /// </para>
    /// </summary>
    public void ApplySavedTheme()
    {
        if (Content is not FrameworkElement root) return;

        root.RequestedTheme = SettingsManager.ThemeMode switch
        {
            1 => ElementTheme.Dark,
            2 => ElementTheme.Light,
            _ => ElementTheme.Default
        };

        ApplyTitleBarColors();
    }

    /// <summary>
    /// Repaints the system-drawn caption buttons for the theme that is actually in effect.
    /// <para>
    /// Windows colours them from <em>its own</em> app theme. This app can override that theme, and
    /// <see cref="ApplyCustomTitleBar"/> makes the button backgrounds transparent — so a light window
    /// on a dark system (or vice versa) ends up with glyphs that do not contrast with the bar.
    /// Setting the foreground explicitly is the documented way to keep them legible; the buttons
    /// themselves are still drawn by the system.
    /// </para>
    /// </summary>
    private void ApplyTitleBarColors()
    {
        var dark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;

        // Fluent caption colours: full-strength glyph, dimmed when the window is inactive, and the
        // standard subtle overlays for hover/pressed.
        var foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var inactive = Windows.UI.Color.FromArgb(0x66, dark ? (byte)0xFF : (byte)0x00,
                                                       dark ? (byte)0xFF : (byte)0x00,
                                                       dark ? (byte)0xFF : (byte)0x00);
        var hover = dark
            ? Windows.UI.Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
        var pressed = dark
            ? Windows.UI.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x17, 0x00, 0x00, 0x00);

        var bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = inactive;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonPressedForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = pressed;
    }

    /// <summary>
    /// Re-localizes the shell after a language change: the navigation labels plus the current page,
    /// whose strings are applied in its own <c>Localize</c> pass during <c>OnNavigatedTo</c>.
    /// <para>
    /// The page is re-navigated <b>with the services instance</b>. Pages take their <c>_services</c>
    /// from the navigation parameter, so navigating with <c>null</c> left them without it — and a
    /// page that bails out on a null parameter keeps whichever strings were applied before the
    /// re-navigation. That is how the "Hide tray icon" row ended up showing its plain tooltip
    /// instead of the disabled-state badge after a language switch.
    /// </para>
    /// </summary>
    public void ReloadForLanguage()
    {
        LocalizeNavigation();

        var pageType = ContentFrame.CurrentSourcePageType;
        if (pageType is null) return;

        ContentFrame.Navigate(pageType, _services, new SuppressNavigationTransitionInfo());
    }

    /// <summary>
    /// The shared services, so a page can recover them when it is navigated to without a parameter.
    /// </summary>
    internal AppServices Services => _services;

    /// <summary>
    /// Answers a second launch.
    /// <para>
    /// The second process owns no window and no <c>XamlRoot</c>, so it cannot present anything
    /// itself — it signals <see cref="Program.ShowRequestEvent"/> and this instance, which does have
    /// a window, shows a real WinUI dialog. The previous build popped a raw Win32 message box from
    /// the second process instead.
    /// </para>
    /// </summary>
    private void ListenForSecondInstance()
    {
        var handle = Program.ShowRequestEvent;
        if (handle is null) return;

        _ = Task.Run(() =>
        {
            try
            {
                while (handle.WaitOne())
                    _services.Dispatcher.Post(OnSecondInstanceRequested);
            }
            catch (Exception ex)
            {
                StartupLog.Fail("MainWindow.ListenForSecondInstance", ex);
            }
        });
    }

    private async void OnSecondInstanceRequested()
    {
        try
        {
            // The window may be hidden in the tray; bring it back so the message is actually seen.
            if (!AppWindow.IsVisible) ShowFromTray();

            await _services.Dialogs.ShowAsync(
                LocalizationService.Get("AlreadyRunning"),
                LocalizationService.Get("AppTitle"),
                DialogButton.OK,
                DialogIcon.Information);
        }
        catch (Exception ex)
        {
            StartupLog.Fail("MainWindow.OnSecondInstanceRequested", ex);
        }
    }

    private void OnPaneToggleClick(object sender, RoutedEventArgs e)
    {
        Nav.IsPaneOpen = !Nav.IsPaneOpen;

        // An explicit toggle overrides the responsive rule — otherwise the next resize would
        // immediately undo what the user just asked for.
        _paneAutoCollapsed = false;
    }

    /// <summary>
    /// Restores the saved window size.
    /// <para>
    /// <c>FormSize</c> is stored in <b>effective</b> (XAML) pixels, matching the original WPF
    /// build's <c>System.Windows.Size</c> semantics and keeping existing settings files valid.
    /// <c>AppWindow</c> works in <b>physical</b> pixels, so the value is scaled here — and the size
    /// is read back from <see cref="FrameworkElement.ActualWidth"/> on save, not from
    /// <c>AppWindow.Size</c>. Mixing the two made the window drift in size on high-DPI displays
    /// (a 175% display multiplied the stored value by 1.75 on every run).
    /// </para>
    /// <para>
    /// Runs from <c>Nav.Loaded</c>: <c>XamlRoot</c> (and therefore the rasterization scale) is not
    /// available in the constructor.
    /// </para>
    /// </summary>
    private void ApplyInitialSize()
    {
        var scale = Nav.XamlRoot?.RasterizationScale ?? 1.0;
        var size = SettingsManager.FormSize;

        var logicalWidth = size.IsValid ? size.Width : 1130;
        var logicalHeight = size.IsValid ? size.Height : 600;

        var targetWidth = (int)Math.Round(logicalWidth * scale);
        var targetHeight = (int)Math.Round(logicalHeight * scale);

        AppWindow.Resize(new SizeInt32(targetWidth, targetHeight));

        StartupLog.Step(
            $"size: form={logicalWidth:F0}x{logicalHeight:F0} scale={scale:F3} " +
            $"target={targetWidth}x{targetHeight} actual={AppWindow.Size.Width}x{AppWindow.Size.Height}");

        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;

        // WinUI's Window has no MinWidth/MinHeight; the presenter does.
        presenter.PreferredMinimumWidth = (int)Math.Round(820 * scale);
        presenter.PreferredMinimumHeight = (int)Math.Round(520 * scale);
    }

    private void LocalizeNavigation()
    {

        NavShortcuts.Content = LocalizationService.Get("TabShortcuts");
        NavPreferences.Content = LocalizationService.Get("TabPreferences");
        NavAbout.Content = LocalizationService.Get("TabAbout");
    }

    private void WireServices()
    {
        ListenForSecondInstance();

        if (_services.HookManager is { } hooks)
        {
            hooks.OnVisibilityToggled += ToggleVisibility;
            StartHooks(hooks);
        }

        if (_services.Tray is { } tray)
        {
            tray.ShowRequested += ShowFromTray;
            tray.ExitRequested += ExitApplication;
        }

        AppWindow.Closing += OnClosing;
    }

    /// <summary>
    /// Applies the saved hook state at startup — the WPF build's <c>StartHooks()</c>.
    /// This was missing, so hooks only ever ran after being toggled from the tray.
    /// </summary>
    private static void StartHooks(HookManager hooks)
    {
        try
        {
            StartupLog.Step(
                $"hooks: window={SettingsManager.IsWindowHookActive} " +
                $"keyboard={SettingsManager.IsKeyboardHookActive} " +
                $"mouse={SettingsManager.IsMouseHookActive} " +
                $"reuseTabs={SettingsManager.ReuseTabs}");

            if (SettingsManager.IsWindowHookActive) hooks.StartWindowHook();
            if (SettingsManager.IsMouseHookActive) hooks.StartMouseHook();
            if (SettingsManager.IsKeyboardHookActive) hooks.StartKeyboardHook();
            hooks.SetReuseTabs(SettingsManager.ReuseTabs);

            StartupLog.Step("hooks: started");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("MainWindow.StartHooks", ex);
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;

        var pageType = tag switch
        {
            "Shortcuts" => typeof(ShortcutsPage),
            "Preferences" => typeof(PreferencesPage),
            _ => typeof(AboutPage)
        };

        // Guard on the actually-realised page, not on CurrentSourcePageType: the latter is set as
        // soon as navigation is requested, so it can claim a page is showing while the frame is
        // still empty.
        if (ContentFrame.Content is FrameworkElement currentPage && currentPage.GetType() == pageType)
        {
            StartupLog.Step($"nav: {tag} already realised, skipping");
            return;
        }

        StartupLog.Step($"nav: {tag}");
        ContentFrame.Navigate(pageType, _services);
        StartupLog.Step($"nav: {tag} done, content={ContentFrame.Content?.GetType().Name ?? "null"}");
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;

        // Without a tray icon there would be no way back to a hidden window, so fall through and
        // let the window actually close instead of stranding the process.
        if (_services.Tray is null)
        {
            StartupLog.Step("OnClosing: no tray icon, allowing real close");
            _exiting = true;
            return;
        }

        // Close means "hide to tray" — the process is a resident hook host.
        //
        // IMPORTANT: this handler is strictly synchronous. Awaiting async UI work here (a
        // ContentDialog, for instance) is a documented source of 0xC0000005 access violations in
        // WinUI 3 because the native window can be torn down before the await resumes.
        args.Cancel = true;
        HideToTray();
    }

    /// <summary>Saves the window size, then hides to the tray. Profiles are already persisted on edit.</summary>
    public void HideToTray()
    {
        // Effective pixels, matching the FormSize contract — never AppWindow.Size (physical).
        if (Nav.ActualWidth > 0 && Nav.ActualHeight > 0)
            SettingsManager.FormSize = new WindowSize(Nav.ActualWidth, Nav.ActualHeight);

        // The user is done editing: now it is safe to discard rows that were never filled in.
        // Profiles themselves are already persisted on every change.
        _services.ProfileManager.PruneUntouchedProfiles();

        AppWindow.Hide();
    }

    public void ShowFromTray()
    {
        AppWindow.Show();

        // A tray popup menu owns the foreground while its click handler runs, so the OS silently
        // ignores a plain Activate(). RestoreWindowToForeground applies the same
        // SetForegroundWindow + synthetic-key workaround the Core uses for Explorer windows.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinApi.RestoreWindowToForeground(hwnd);

        Activate();
        StartupLog.Step($"ShowFromTray: visible={AppWindow.IsVisible}");
    }

    private void ToggleVisibility()
    {
        if (AppWindow.IsVisible) HideToTray();
        else ShowFromTray();
    }

    private void ExitApplication()
    {
        // Let the close handler know this is a real exit, not a hide.
        _exiting = true;

        _services.Dispose();
        Application.Current.Exit();
    }
}
