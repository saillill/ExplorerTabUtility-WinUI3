using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;
using Xunit;

namespace ExplorerTabUtility.Tests;

/// <summary>
/// The threading contract that <c>Fody</c>/<c>ConfigureAwait.Fody</c> imposes on the Core assembly.
/// <para>
/// It is invisible in the source (the weaver rewrites every <c>await</c> in IL), it is load-bearing in
/// both directions, and until now it was only written down in a comment: Core's continuations must
/// <b>not</b> come back to the caller's context, while the App shell's must stay on the UI thread
/// (<c>AboutPage</c> caches <c>ImageSource</c> objects on that assumption). Both halves are asserted so
/// that removing or broadening the weaver fails loudly instead of subtly changing where work runs.
/// </para>
/// </summary>
public class ThreadingContractTests
{
    [Fact]
    public void Core_await_continuations_do_not_capture_the_callers_synchronization_context()
    {
        var recorder = new RecordingSynchronizationContext();

        // A dedicated background thread with the recording context installed: the body blocks on the
        // task, which is safe only while the continuations are detached from the context. Without the
        // weaver they are posted to the recorder, nobody pumps it, the thread never returns and the
        // Join below fails — which is exactly the signal we want, phrased as a readable failure.
        var worker = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(recorder);
            try
            {
                // Any Core async path will do; this one awaits Task.Delay repeatedly while its predicate
                // stays false, so it reaches a continuation either way.
                Helper.DoUntilConditionAsync(() => 0, _ => false, timeMs: 150, sleepMs: 10)
                      .GetAwaiter()
                      .GetResult();
            }
            catch (Exception ex)
            {
                recorder.RecordFailure(ex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        })
        {
            IsBackground = true,
            Name = "threading-contract-probe"
        };

        worker.Start();

        var finished = worker.Join(TimeSpan.FromSeconds(15));

        Assert.True(finished,
            "the Core call never completed: its continuations were captured by the caller's " +
            "SynchronizationContext, i.e. ConfigureAwait.Fody is no longer weaving " +
            "ExplorerTabUtility.Core. See docs/ARCHITECTURE.md, 'threading delivery model'.");
        Assert.Null(recorder.Failure);
        Assert.Equal(0, recorder.PostCount);
    }

    [Fact]
    public void Only_the_core_project_is_woven()
    {
        var root = FindRepositoryRoot();
        if (root is null) return;   // a repo-layout assertion; meaningless outside a checkout

        var coreProject = Path.Combine(root, "ExplorerTabUtility.Core", "ExplorerTabUtility.Core.csproj");
        var appProject = Path.Combine(root, "ExplorerTabUtility.App.WinUI", "ExplorerTabUtility.App.WinUI.csproj");

        var core = File.ReadAllText(coreProject);
        var app = File.ReadAllText(appProject);

        Assert.Contains("ConfigureAwait.Fody", core, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "ExplorerTabUtility.Core", "FodyWeavers.xml")),
            "FodyWeavers.xml is what makes the ConfigureAwait weaver run; without it Core's awaits keep " +
            "capturing the caller's context.");

        // The App shell must NOT be woven: its continuations have to stay on the UI thread.
        Assert.DoesNotContain("Fody", app, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public Exception? Failure { get; private set; }

        // Recording without executing: a posted continuation is the failure signal, and running it would
        // hide that by making the probe complete anyway.
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _postCount);

        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _postCount);

        public void RecordFailure(Exception ex) => Failure = ex;
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExplorerTabUtility.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}

/// <summary>
/// The settings file is the only copy of a user's hotkey list, and the write path is the code that
/// decides whether it survives a crash mid-save. It is exercised here against a temporary directory
/// instead of the real one.
/// <para>
/// Touching <c>SettingsManager</c> at all runs its static constructor, which reads
/// <c>%APPDATA%\ExplorerTabUtility\settings.json</c> and registers a process-exit flush. Neither can
/// write anything: the directory already exists, the read is a read, and the flush is a no-op while
/// nothing has been changed through the public setters — which this test class never does.
/// </para>
/// </summary>
public class SettingsWriteTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("etu-settings-tests-").FullName;

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void A_first_write_creates_the_file_without_a_backup()
    {
        SettingsManager.WriteAtomic(SettingsPath, new AppSettings { ThemeMode = 1 });

        Assert.True(File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".bak"));
        Assert.False(File.Exists(SettingsPath + ".tmp"), "the temporary file must not be left behind");
        Assert.Equal(1, Read(SettingsPath).ThemeMode);
    }

    [Fact]
    public void A_second_write_keeps_the_previous_content_as_the_backup()
    {
        SettingsManager.WriteAtomic(SettingsPath, new AppSettings { ThemeMode = 1, ReuseTabs = true });
        SettingsManager.WriteAtomic(SettingsPath, new AppSettings { ThemeMode = 2, ReuseTabs = false });

        var current = Read(SettingsPath);
        var backup = Read(SettingsPath + ".bak");

        Assert.Equal(2, current.ThemeMode);
        Assert.False(current.ReuseTabs);

        // The backup is the point: if the new file is ever unreadable, the old one is still there and
        // the loader recovers from it.
        Assert.Equal(1, backup.ThemeMode);
        Assert.True(backup.ReuseTabs);
    }

    [Fact]
    public void A_write_leaves_no_temporary_file_behind()
    {
        for (var i = 0; i < 3; i++)
            SettingsManager.WriteAtomic(SettingsPath, new AppSettings { ThemeMode = i });

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.Equal(2, Read(SettingsPath).ThemeMode);
    }

    [Fact]
    public void The_written_file_is_the_json_the_loader_expects()
    {
        var settings = new AppSettings
        {
            WindowHook = true,
            IsTrayIconHidden = true,
            ThemeMode = 2,
            Language = "zh-Hant",
            HotKeyProfiles = "[]",
            FormSize = new WindowSize(1300, 800)
        };

        SettingsManager.WriteAtomic(SettingsPath, settings);

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"ThemeMode\":2", text, StringComparison.Ordinal);
        Assert.Contains("zh-Hant", text, StringComparison.Ordinal);
        Assert.Contains("\"Width\":1300", text, StringComparison.Ordinal);
        // WindowSize.IsValid is derived, marked [JsonIgnore]; persisting it would put a field in the file
        // that the loader then has to ignore.
        Assert.DoesNotContain("IsValid", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_one_time_migration_flags_survive_a_write_and_read()
    {
        SettingsManager.WriteAtomic(
            SettingsPath,
            new AppSettings { FormSizeMigrated = true, LanguageMigrated = true });

        var restored = Read(SettingsPath);

        // Both flags gate one-time corrections that DISCARD stored state (a window size, a pinned
        // language). If a flag failed to persist, the correction would run on every launch and keep
        // throwing away whatever the user had just chosen.
        Assert.True(restored.FormSizeMigrated);
        Assert.True(restored.LanguageMigrated);
    }

    private static AppSettings Read(string path) =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { /* best effort: a temp directory left behind is harmless */ }
    }
}
