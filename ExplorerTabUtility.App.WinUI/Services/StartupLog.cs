using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

    /// <summary>
    /// Append-only failure log. Separate from <see cref="LogPath"/> because that one is truncated on
    /// every start, so a report of "it misbehaved yesterday" was unrecoverable.
    /// </summary>
    private static readonly string ErrorLogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ExplorerTabUtility",
        "error.log");

    /// <summary>Rotate rather than grow without bound; a resident tray host can run for weeks.</summary>
    private const long ErrorLogMaxBytes = 512 * 1024;

    /// <summary>How many times one failure site may repeat before it is called out as persistent.</summary>
    private const int RepeatWarningThreshold = 5;

    /// <summary>Failure site → number of times it has failed this session.</summary>
    private static readonly Dictionary<string, int> FailureCounts = new(StringComparer.Ordinal);

    /// <summary>Absolute path of the append-only failure log.</summary>
    public static string ErrorFilePath => ErrorLogPath;

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
    {
        Write($"FAIL  {where}{Environment.NewLine}      {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}      {ex.StackTrace}");

        WriteErrorLog(
            $"{where}{Environment.NewLine}" +
            $"  {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}" +
            $"  {ex.StackTrace}");

        // Repetition is the signal that separates "a one-off callback hiccup" from "the app is running
        // degraded". The exception itself is already marked handled by AttachGlobalHandlers, so without
        // this the only trace of a persistently broken code path was one more identical line in a log the
        // user never opens.
        lock (Gate)
        {
            var key = $"{where}|{ex.GetType().FullName}";
            FailureCounts.TryGetValue(key, out var count);
            FailureCounts[key] = ++count;

            if (count == RepeatWarningThreshold)
            {
                Write($"WARN  {where} has now failed {count} times with {ex.GetType().Name} — " +
                      $"the app is still running but that code path is not working. " +
                      $"Details: {ErrorLogPath}");
            }
        }
    }

    private static void WriteErrorLog(string block)
    {
        try
        {
            lock (Gate)
            {
                var directory = System.IO.Path.GetDirectoryName(ErrorLogPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var info = new FileInfo(ErrorLogPath);
                if (info.Exists && info.Length > ErrorLogMaxBytes)
                    File.Delete(ErrorLogPath);

                File.AppendAllText(
                    ErrorLogPath,
                    $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  pid {Environment.ProcessId}{Environment.NewLine}" +
                    $"{block}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Never let logging fail the caller.
        }
    }

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
        Write($"build    {BuildStamp}");
        Write($"os       {Environment.OSVersion}");
        Write($"runtime  {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
    }

    /// <summary>
    /// Version of the running assembly, plus the source revision it was built from when the build could
    /// read one (see the <c>SourceRevisionId</c> target in <c>Directory.Build.props</c>).
    /// <para>
    /// This is the answer to "which build produced this log?" — previously the only way to tell a
    /// deployed binary from the repository it claimed to come from was to hash files by hand.
    /// </para>
    /// </summary>
    private static string BuildStamp
    {
        get
        {
            var assembly = typeof(StartupLog).Assembly;

            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString()
                   ?? "unknown";
        }
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
