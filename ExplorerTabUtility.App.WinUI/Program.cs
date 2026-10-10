using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ExplorerTabUtility.App.Services;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.App;

/// <summary>
/// Explicit entry point (<c>DISABLE_XAML_GENERATED_MAIN</c>).
/// <para>
/// Owning Main lets us decide single-instance before any XAML object exists — the same ordering
/// the WPF build had — and keeps the "already running" path off the XAML stack, where a
/// <c>ContentDialog</c> would be impossible (no <c>XamlRoot</c> yet).
/// </para>
/// <para>
/// Everything is logged to <c>%APPDATA%\ExplorerTabUtility\startup.log</c>: an unpackaged WinUI
/// app that throws here dies with no dialog and no console output.
/// </para>
/// </summary>
public static class Program
{
    private static Mutex? _singleInstanceMutex;

    /// <summary>
    /// Owned by the first instance so a later launch can ask it to surface itself.
    /// </summary>
    internal static EventWaitHandle? ShowRequestEvent { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            // Runs before anything can show a localized string. The single-instance notice below
            // happens before the XAML application exists, so applying the language only in
            // App.OnLaunched was too late — the notice came out in the system language.
            LocalizationService.ApplySavedLanguage();

            _singleInstanceMutex = new Mutex(true, Constants.MutexId, out var createdNew);
            StartupLog.Step($"mutex acquired, createdNew={createdNew}");

            if (!createdNew)
            {
                // Hand the request to the running instance, which owns a window and can therefore
                // show a real WinUI ContentDialog. This process has no XamlRoot at all, so it must
                // not try to show anything itself.
                if (TrySignalRunningInstance())
                {
                    StartupLog.Step("second instance: signalled the running instance");
                    return;
                }

                // Fallback only: the running instance could not be reached.
                NativeMessageBox.Show(
                    LocalizationService.Get("AlreadyRunning"),
                    Constants.AppName,
                    icon: NativeMessageBox.Icon.Information);
                return;
            }

            ShowRequestEvent = new EventWaitHandle(
                false, EventResetMode.AutoReset, Constants.ShowRequestEventName);

            // Only the instance that actually runs the app truncates the log. Doing it before the
            // mutex check meant every extra launch wiped the running instance's diagnostics.
            StartupLog.Reset();

            StartupLog.Step("InitializeComWrappers");
            WinRT.ComWrappersSupport.InitializeComWrappers();

            StartupLog.Step("Application.Start");
            Application.Start(callbackParams =>
            {
                try
                {
                    var queue = DispatcherQueue.GetForCurrentThread();
                    StartupLog.Step($"dispatcher queue acquired: {queue is not null}");
                    SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));

                    StartupLog.Step("constructing App");
                    _ = new App();
                }
                catch (Exception ex)
                {
                    StartupLog.Fail("Application.Start callback", ex);
                    throw;
                }

                GC.KeepAlive(callbackParams);
            });

            StartupLog.Step("Application.Start returned");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("Program.Main", ex);

            // Intentional native box: XAML may not be usable at all at this point, so there is no
            // XamlRoot to host a ContentDialog on.
            NativeMessageBox.Show(
                $"{ex.GetType().Name}: {ex.Message}\n\n{StartupLog.FilePath}",
                $"{Constants.AppName} - startup failed",
                icon: NativeMessageBox.Icon.Error);
        }
    }

    /// <summary>Asks the already-running instance to show itself. False when it cannot be reached.</summary>
    private static bool TrySignalRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(Constants.ShowRequestEventName, out var handle))
                return false;

            using (handle)
            {
                handle.Set();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
