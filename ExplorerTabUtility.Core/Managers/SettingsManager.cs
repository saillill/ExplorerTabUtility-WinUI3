using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Managers;

public static class SettingsManager
{
    private static readonly AppSettings Settings;
    private static readonly object SaveLock = new();
    private static Timer? DebounceTimer;
    private static volatile bool IsDirty;
    private const int DebounceMs = 500;

    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Constants.AppName,
        Constants.SettingsFileName);

    private static readonly string BackupFilePath = SettingsFilePath + ".bak";

    static SettingsManager()
    {
        // Last-resort flush: the normal exit path flushes through AppServices.Dispose, but the
        // window also closes for real when the tray icon failed to create, and that path disposes
        // nothing — so the final debounced change would be lost.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ForceSave();

        var directory = Path.GetDirectoryName(SettingsFilePath);
        Directory.CreateDirectory(directory!);

        if (!File.Exists(SettingsFilePath))
        {
            // Recover from backup if the main file is missing (e.g., interrupted write),
            // otherwise the user would silently lose all settings.
            if (File.Exists(BackupFilePath) && TryLoad(BackupFilePath, out var missingBackup))
            {
                Settings = missingBackup;
                try { WriteAtomic(SettingsFilePath, missingBackup); } catch { }
                return;
            }
            Settings = new AppSettings();
            return;
        }
        if (TryLoad(SettingsFilePath, out var loaded))
        {
            Settings = loaded;
            return;
        }
        if (File.Exists(BackupFilePath) && TryLoad(BackupFilePath, out var backup))
        {
            Settings = backup;
            try { WriteAtomic(SettingsFilePath, backup); } catch { }
            return;
        }
        Settings = new AppSettings();
    }

    private static bool TryLoad(string path, out AppSettings s)
    {
        s = null!;
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return false;
            var r = JsonSerializer.Deserialize<AppSettings>(json);
            if (r == null) return false;
            s = r; return true;
        }
        catch { return false; }
    }

    public static bool IsMouseHookActive
    { get => Settings.MouseHook; set { Settings.MouseHook = value; DebounceSave(); } }
    public static bool IsKeyboardHookActive
    { get => Settings.KeyboardHook; set { Settings.KeyboardHook = value; DebounceSave(); } }
    public static bool IsWindowHookActive
    { get => Settings.WindowHook; set { Settings.WindowHook = value; DebounceSave(); } }
    public static bool ReuseTabs
    { get => Settings.ReuseTabs; set { Settings.ReuseTabs = value; DebounceSave(); } }

    /// <summary>Whether a check runs by itself at startup — see <see cref="AppSettings.AutoCheckForUpdates"/>.</summary>
    public static bool AutoCheckForUpdates
    { get => Settings.AutoCheckForUpdates; set { Settings.AutoCheckForUpdates = value; DebounceSave(); } }

    /// <summary>
    /// Folders that always open in their own window — see <see cref="AppSettings.NoMergeFolders"/>.
    /// <para>
    /// Replaced wholesale rather than mutated: the list is saved by the same debounced writer as every
    /// other setting, and a caller that edited the live instance would be changing state the writer may
    /// already be reading.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> NoMergeFolders
    {
        get => Settings.NoMergeFolders;
        set { Settings.NoMergeFolders = [.. value]; DebounceSave(); }
    }
    public static string HotKeyProfiles
    { get => Settings.HotKeyProfiles; set { Settings.HotKeyProfiles = value; DebounceSave(); } }
    public static WindowSize FormSize
    { get => Settings.FormSize; set { Settings.FormSize = value; DebounceSave(); } }

    /// <summary>
    /// Set once the stored <see cref="FormSize"/> has been re-evaluated against the settled unit
    /// contract. Without this the one-time discard in <c>MainWindow.ApplyInitialSize</c> would repeat on
    /// every launch — throwing away a size the user chose on purpose.
    /// </summary>
    public static bool FormSizeMigrated
    { get => Settings.FormSizeMigrated; set { Settings.FormSizeMigrated = value; DebounceSave(); } }
    public static bool IsFirstRun
    { get => Settings.IsFirstRun; set { Settings.IsFirstRun = value; DebounceSave(); } }
    public static bool IsTrayIconHidden
    { get => Settings.IsTrayIconHidden; set { Settings.IsTrayIconHidden = value; DebounceSave(); } }
    public static int ThemeMode
    {
        get => Settings.ThemeMode;
        set { Settings.ThemeMode = value; DebounceSave(); }
    }

    public static bool SaveClosedHistory
    { get => Settings.SaveClosedWindows; set { Settings.SaveClosedWindows = value; DebounceSave(); } }
    public static bool RestorePreviousWindows
    { get => Settings.RestorePreviousWindows; set { Settings.RestorePreviousWindows = value; DebounceSave(); } }

    public static bool HideWindowOnStartup
    { get => Settings.HideWindowOnStartup; set { Settings.HideWindowOnStartup = value; DebounceSave(); } }
    public static WindowRecord[]? ClosedWindows
    { get => Settings.ClosedWindows; set { Settings.ClosedWindows = value; DebounceSave(); } }
    public static string Language
    { get => Settings.Language; set { Settings.Language = value; DebounceSave(); } }

    /// <summary>
    /// Set once the legacy language value has been re-evaluated. Without this the correction in
    /// <c>LocalizationService.ApplySavedLanguage</c> would repeat on every launch and an explicit
    /// "English" could never stick.
    /// </summary>
    public static bool LanguageMigrated
    { get => Settings.LanguageMigrated; set { Settings.LanguageMigrated = value; DebounceSave(); } }

    private static void DebounceSave()
    {
        // Everything below shares SaveLock with ForceSave: the read/modify/Change of DebounceTimer is
        // not atomic on its own, and ForceSave may Dispose the timer from the ProcessExit thread at any
        // moment. A single lock removes the lost-update (two orphan timers) and the
        // change-after-dispose races (AUD-15).
        lock (SaveLock)
        {
            IsDirty = true;

            try
            {
                if (DebounceTimer == null)
                    DebounceTimer = new Timer(_ => FlushIfDirty(), null, DebounceMs, Timeout.Infinite);
                else
                    DebounceTimer.Change(DebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // ForceSave disposed the timer between our read and our Change. Rebuild it so the
                // pending change still flushes instead of throwing up into the caller (AUD-15).
                DebounceTimer = new Timer(_ => FlushIfDirty(), null, DebounceMs, Timeout.Infinite);
            }
        }
    }

    private static void FlushIfDirty()
    {
        lock (SaveLock)
        {
            if (!IsDirty) return;
            IsDirty = false;
            DoSave();
        }
    }

    public static void ForceSave()
    {
        // Same lock as DebounceSave. ForceSave is invoked from the UI thread and from
        // AppDomain.ProcessExit, so it can run concurrently with a setter's debounce (AUD-15).
        lock (SaveLock)
        {
            DebounceTimer?.Dispose();
            DebounceTimer = null;

            if (!IsDirty) return;
            IsDirty = false;
            DoSave();
        }
    }

    private static void DoSave()
    {
        try { WriteAtomic(SettingsFilePath, Settings); }
        catch (Exception ex)
        {
            // A persistent IO failure (disk full, permissions) used to vanish without a trace and
            // the user's settings were silently lost. Leave at least a diagnostic breadcrumb.
            System.Diagnostics.Debug.WriteLine($"SettingsManager save failed: {ex}");
        }
    }

    /// <summary>
    /// Writes <paramref name="s"/> to <paramref name="path"/> as atomically as the file system allows,
    /// keeping the previous content in <c>.bak</c>.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than private so the unit tests can exercise it against a temporary
    /// directory: this is the code that decides whether a user's settings survive a crash mid-save, and
    /// its failure mode (a missing settings file) is silent. See
    /// <c>ExplorerTabUtility.Tests.SettingsWriteTests</c>.
    /// </remarks>
    internal static void WriteAtomic(string path, AppSettings s)
    {
        var tmp = path + ".tmp";
        var bak = path + ".bak";
        var json = JsonSerializer.Serialize(s);
        File.WriteAllText(tmp, json);

        if (File.Exists(path))
        {
            // File.Replace is atomic on NTFS: tmp becomes the settings file and the old
            // file is moved to bak. If it fails, fall back to copy + delete + move.
            try
            {
                File.Replace(tmp, path, bak);
                return;
            }
            catch
            {
                try { File.Copy(path, bak, overwrite: true); } catch { }
            }
        }

        try { File.Delete(path); } catch (FileNotFoundException) { }
        File.Move(tmp, path);
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
    }

}
internal class AppSettings
{
    public bool MouseHook { get; set; }
    public bool KeyboardHook { get; set; } = true;
    public bool WindowHook { get; set; } = true;
    public bool ReuseTabs { get; set; } = true;

    /// <summary>
    /// Install a found update without asking — and look for one in the background at startup, which is part
    /// of the same promise. Off unless the user asks for it: taking an update closes this app and reopens it,
    /// which is not something to do behind someone's back.
    /// </summary>
    public bool AutoCheckForUpdates { get; set; } = true;
    public WindowSize FormSize { get; set; } = WindowSize.Default;
    public bool IsFirstRun { get; set; } = true;
    public bool IsTrayIconHidden { get; set; }
    public int ThemeMode { get; set; }
    public string HotKeyProfiles { get; set; } = Constants.DefaultHotKeyProfiles;
    public bool SaveClosedWindows { get; set; }
    public bool RestorePreviousWindows { get; set; }
    public bool HideWindowOnStartup { get; set; }
    public WindowRecord[]? ClosedWindows { get; set; }
    public string Language { get; set; } = "";
    public bool LanguageMigrated { get; set; }

    /// <summary>
    /// Folders that always open in their own window, never folded into a tab.
    /// <para>
    /// Compared by the shell's own path identity rather than as text, so a marked folder is recognised
    /// through the spellings Explorer actually hands over — a trailing separator, a different case, a
    /// non-canonical form. A list that only matched byte-for-byte would look like a feature that does
    /// not work.
    /// </para>
    /// </summary>
    public List<string> NoMergeFolders { get; set; } = [];

    /// <summary>
    /// Set once the stored <see cref="FormSize"/> has been trusted as belonging to the current unit
    /// contract (outer window size, effective pixels). See <c>MainWindow.ApplyInitialSize</c>.
    /// </summary>
    public bool FormSizeMigrated { get; set; }
}
