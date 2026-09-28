using System;
using System.Linq;
using Microsoft.Win32;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Managers;

public static class RegistryManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ExplorerAdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private static readonly string? ExecutablePath = Helper.GetExecutablePath();

    /// <summary>
    /// Marker written after the executable path in the Run entry, so a launch caused by Windows
    /// sign-in can be told apart from a launch the user performed.
    /// </summary>
    public const string StartupArgument = "--startup";

    public static bool IsStartupEnabled => IsInStartup() && IsStartupApprovedEnabled();

    /// <summary>True when this process was started by the sign-in Run entry.</summary>
    public static bool WasStartedAtSignIn =>
        Environment.GetCommandLineArgs().Any(
            a => string.Equals(a, StartupArgument, StringComparison.OrdinalIgnoreCase));

    public static void ToggleStartup()
    {
        if (IsStartupEnabled)
            RemoveFromStartup();
        else
            AddToStartup();
    }

    private static bool IsInStartup()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath)) return false;

        // Check if the application exists in the Run registry key and has the correct executable location
        using var key = OpenCurrentUserKey(RunKeyPath, false);
        var value = key?.GetValue(Constants.AppName) as string;
        return string.Equals(ExtractExecutable(value), ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStartupApprovedEnabled()
    {
        using var key = OpenCurrentUserKey(StartupApprovedKeyPath, false);
        var value = key?.GetValue(Constants.AppName) as byte[];
        // Check first byte parity (even = enabled, odd = disabled), null and empty also mean enabled.
        return value == null || value.Length == 0 || value[0] % 2 == 0;
    }

    private static void AddToStartup()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath)) return;

        // Add to Run registry key, with the marker argument so a sign-in launch is identifiable.
        using var runKey = OpenCurrentUserKey(RunKeyPath, true);
        runKey?.SetValue(Constants.AppName, $"\"{ExecutablePath}\" {StartupArgument}");

        // Create enabled entry in StartupApproved
        var enabledData = new byte[12];
        enabledData[0] = 0x02; // Even value for enabled

        using var approvedKey = OpenCurrentUserKey(StartupApprovedKeyPath, true);
        approvedKey?.SetValue(Constants.AppName, enabledData, RegistryValueKind.Binary);
    }

    private static void RemoveFromStartup()
    {
        // Remove from Run registry key
        using var runKey = OpenCurrentUserKey(RunKeyPath, true);
        runKey?.DeleteValue(Constants.AppName, false);

        // Remove from StartupApproved
        using var approvedKey = OpenCurrentUserKey(StartupApprovedKeyPath, true);
        approvedKey?.DeleteValue(Constants.AppName, false);
    }

    /// <summary>
    /// Returns just the executable path from a Run value, tolerating a surrounding quote pair and a
    /// trailing argument. Entries written by earlier builds hold the bare path.
    /// </summary>
    private static string? ExtractExecutable(string? runValue)
    {
        if (string.IsNullOrWhiteSpace(runValue)) return null;

        var value = runValue.Trim();
        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            var closing = value.IndexOf('"', 1);
            return closing > 0 ? value[1..closing] : value.Trim('"');
        }

        var space = value.IndexOf(' ');
        return space > 0 ? value[..space] : value;
    }

    /// <summary>
    /// Rewrites an existing entry so it carries <see cref="StartupArgument"/>. Without this an
    /// installation created before the marker existed would never be recognised as a sign-in launch.
    /// </summary>
    public static void UpgradeStartupEntry()
    {
        if (!IsStartupEnabled || string.IsNullOrWhiteSpace(ExecutablePath)) return;

        using var key = OpenCurrentUserKey(RunKeyPath, false);
        if (key?.GetValue(Constants.AppName) is string value &&
            value.Contains(StartupArgument, StringComparison.OrdinalIgnoreCase))
            return;

        AddToStartup();
    }

    public static int GetDefaultExplorerLaunchId()
    {
        using var key = OpenCurrentUserKey(ExplorerAdvancedKeyPath, false);
        if (key == null) return 1;
        return key.GetValue("LaunchTo") as int? ?? 1;
    }

    private static RegistryKey? OpenCurrentUserKey(string name, bool writable) => Registry.CurrentUser.OpenSubKey(name, writable);
}