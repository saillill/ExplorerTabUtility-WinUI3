using System;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ExplorerTabUtility.App.Services;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;

namespace ExplorerTabUtility.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        // The application theme has to be set before the first window exists. Overriding the theme
        // on the root element alone is not enough: elements then *render* dark, but
        // Application.Current.Resources[...] still resolves against the system theme, so brushes
        // fetched in code (and {ThemeResource} lookups at page level) disagree with the UI.
        ApplyApplicationTheme();

        StartupLog.Step("App ctor: InitializeComponent");
        InitializeComponent();
        StartupLog.Step("App ctor: InitializeComponent ok");

        StartupLog.AttachGlobalHandlers(this);
        StartupLog.Step("App ctor: global handlers attached");
    }

    private static void ApplyApplicationTheme()
    {
        Current.RequestedTheme = SettingsManager.ThemeMode switch
        {
            1 => ApplicationTheme.Dark,
            2 => ApplicationTheme.Light,
            // "Follow system": resolve the OS app theme explicitly. WinUI 3 desktop does NOT
            // track the OS by default — unlike UWP, Application.RequestedTheme starts at Light
            // and stays there — so keeping the default here is what made "Follow system" come
            // up light on a dark system. SystemTheme is the one place that knows how to ask.
            _ => SystemTheme.IsDark() ? ApplicationTheme.Dark : ApplicationTheme.Light
        };
    }

    internal AppServices? Services { get; private set; }

    /// <summary>
    /// The active window. Needed by pages that must hand an owner HWND to a WinRT API
    /// (file pickers in an unpackaged app).
    /// </summary>
    internal static MainWindow? MainWindowInstance { get; private set; }

    /// <summary>
    /// Icon on disk. Unpackaged apps have no <c>ms-appx</c> resolution outside the install
    /// directory, so the tray loads it by absolute path.
    /// </summary>
    internal static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Icon.ico");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            StartupLog.Step("OnLaunched: begin");

            // The saved UI language is applied in Program.Main, before the XAML application exists —
            // it has to be, because the single-instance notice is shown even earlier. Logged here for
            // diagnosis only.
            StartupLog.Step($"OnLaunched: ui language = {LocalizationService.Instance.Language}");

            var queue = DispatcherQueue.GetForCurrentThread();

            StartupLog.Step("OnLaunched: AppServices");
            Services = new AppServices(queue, () => _window?.Content?.XamlRoot);

            StartupLog.Step("OnLaunched: MainWindow");
            _window = new MainWindow(Services);
            MainWindowInstance = _window;
            StartupLog.Step("OnLaunched: MainWindow constructed");

            // The show/exit commands are wired inside MainWindow.WireServices so they are
            // subscribed exactly once — subscribing here as well made every tray command run twice.
            _window.Activate();
            StartupLog.Step("OnLaunched: Activate called");

            // Launched by the Windows sign-in entry: start in the tray when the user asked for it.
            // The window is activated first so the tray icon, XamlRoot and backdrop are all live;
            // HideToTray then removes it from the taskbar and the screen.
            if (RegistryManager.WasStartedAtSignIn && SettingsManager.HideWindowOnStartup)
            {
                StartupLog.Step("OnLaunched: sign-in launch, hiding window to tray");
                _window.HideToTray();
            }
            else
            {
                RegistryManager.UpgradeStartupEntry();
            }

            // The WPF build always showed the window on first run; keep that behaviour so a fresh
            // install is discoverable even when it is set to start minimised later on.
            StartupLog.Step($"OnLaunched: window visible = {_window.AppWindow.IsVisible}");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("OnLaunched", ex);

            NativeMessageBox.Show(
                $"{ex.GetType().Name}: {ex.Message}\n\n{StartupLog.FilePath}",
                "Explorer Tab Utility - startup failed",
                icon: NativeMessageBox.Icon.Error);

            // Do NOT stay in the message loop: a windowless process still owns the single-instance
            // mutex and the show-request event, so every later launch would "successfully" signal
            // this zombie and exit silently — the app could never be opened again until the process
            // was killed by hand. Exit and let the OS release both handles.
            Current.Exit();
        }
    }
}
