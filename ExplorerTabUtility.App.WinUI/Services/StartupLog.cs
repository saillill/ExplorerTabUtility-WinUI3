using System;
using System.IO;
using System.Text;
using System.Threading;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// Minimal append-only startup/crash log.
/// <para>
/// An unpackaged WinUI app that throws during <c>OnLaunched</c> dies with no dialog, no console
/// output and no Windows Error Reporting entry — the user just sees "nothing happened".
/// This writes the reason to disk so failures are diagnosable.
/// </para>
/// </summary>
internal static class StartupLog
{
    private static readonly object Gate = new();

    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ExplorerTabUtility",
        "startup.log");

    /// <summary>Absolute path of the log file. Named FilePath, not Path, to avoid shadowing System.IO.Path.</summary>
    public static string FilePath => LogPath;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var directory = System.IO.Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never be the reason the app fails.
        }
    }

    public static void Step(string step) => Write($"STEP  {step}");

    public static void Fail(string where, Exception ex)
        => Write($"FAIL  {where}{Environment.NewLine}      {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}      {ex.StackTrace}");

    /// <summary>Truncates the log. Called once at process start so each run is self-contained.</summary>
    public static void Reset()
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(LogPath))
                    File.Delete(LogPath);
            }
        }
        catch
        {
            // Non-fatal.
        }

        Write($"===== ExplorerTabUtility start (pid {Environment.ProcessId}) =====");
        Write($"exe      {Environment.ProcessPath}");
        Write($"os       {Environment.OSVersion}");
        Write($"runtime  {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
    }

    /// <summary>Hooks every unhandled-exception source the runtime offers.</summary>
    public static void AttachGlobalHandlers(Microsoft.UI.Xaml.Application app)
    {
        app.UnhandledException += (_, e) =>
        {
            Fail("Application.UnhandledException", e.Exception);
            // Mark handled so a recoverable XAML callback failure does not kill the tray host.
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fail("AppDomain.UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Fail("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }
}
