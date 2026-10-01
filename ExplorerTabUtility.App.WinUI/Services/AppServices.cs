using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ExplorerTabUtility.Managers;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// Composition root. Builds every Core manager with the WinUI implementations of the four
/// abstractions (<c>IDialogService</c>, <c>IUiDispatcher</c>, <c>IProfilesHost</c>,
/// <c>ProfileCardFactory</c>) so the Core library never sees the UI toolkit.
/// <para>
/// Each optional subsystem is built behind its own guard: a failure in the tray icon or the
/// hook engine must not stop the settings window from opening. Failures are recorded to
/// <see cref="StartupLog"/> instead of silently killing the process.
/// </para>
/// </summary>
public sealed class AppServices : IDisposable
{
    private bool _disposed;

    public AppServices(DispatcherQueue queue, Func<XamlRoot?> xamlRootProvider)
    {
        StartupLog.Step("AppServices: dispatcher");
        Dispatcher = new WinUiDispatcher(queue);

        StartupLog.Step("AppServices: dialog service");
        // The window is created after this service, so the callback resolves it lazily; before it exists
        // there is nothing to show anyway (the service falls back to a native message box).
        Dialogs = new ContentDialogService(
            Dispatcher,
            xamlRootProvider,
            ensureWindowVisible: () => App.MainWindowInstance?.EnsureWindowVisible());

        StartupLog.Step("AppServices: profiles host");
        Profiles = new ProfilesHost();

        StartupLog.Step("AppServices: profile manager");
        ProfileManager = new ProfileManager(
            Profiles,
            (profile, callbacks) => new ProfileCardView(profile, callbacks));
        StartupLog.Step($"AppServices: profile manager ok ({ProfileManager.Cards.Count} cards)");

        // Optional: the app is still usable (settings only) if the hook engine cannot start.
        try
        {
            StartupLog.Step("AppServices: hook manager");
            HookManager = new HookManager(ProfileManager, Dialogs, Dispatcher);
            StartupLog.Step("AppServices: hook manager ok");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("AppServices: hook manager", ex);
        }

        // Optional: a missing tray icon must not prevent the window from appearing.
        try
        {
            StartupLog.Step("AppServices: tray icon");
            Tray = new TrayIconService(ProfileManager, HookManager, Dispatcher);
            StartupLog.Step("AppServices: tray icon ok");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("AppServices: tray icon", ex);
        }
    }

    public WinUiDispatcher Dispatcher { get; }
    public ContentDialogService Dialogs { get; }
    public ProfilesHost Profiles { get; }
    public ProfileManager ProfileManager { get; }

    /// <summary>Null when the hook engine failed to construct.</summary>
    public HookManager? HookManager { get; }

    /// <summary>Null when the tray icon failed to construct.</summary>
    public TrayIconService? Tray { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Same teardown order as the WPF build: flush settings, drop the tray, then release hooks.
        Safe("dispose: settings", SettingsManager.ForceSave);
        Safe("dispose: tray", () => Tray?.Dispose());
        Safe("dispose: hooks", () => HookManager?.Dispose());
        Safe("dispose: dispatcher", Dispatcher.Dispose);
    }

    private static void Safe(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            StartupLog.Fail($"AppServices {what}", ex);
        }
    }
}
