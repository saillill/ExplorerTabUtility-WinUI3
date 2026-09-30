using System;
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
    public static string HotKeyProfiles
    { get => Settings.HotKeyProfiles; set { Settings.HotKeyProfiles = value; DebounceSave(); } }
    public static WindowSize FormSize
    { get => Settings.FormSize; set { Settings.FormSize = value; DebounceSave(); } }
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

    private static void WriteAtomic(string path, AppSettings s)
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
}
