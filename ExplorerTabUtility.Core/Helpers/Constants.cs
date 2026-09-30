namespace ExplorerTabUtility.Helpers;

/// <summary>
/// Shared application constants. Public so UI shells can use the mutex id, app name and the
/// default profile JSON without duplicating them.
/// </summary>
public static class Constants
{
    public const string AppName = "ExplorerTabUtility";
    public const string MutexId = $"__{AppName}Hook__Mutex";

    /// <summary>
    /// Named event a second launch uses to tell the running instance to surface itself.
    /// The second process has no window and no XamlRoot, so it cannot show a dialog of its own.
    /// </summary>
    public const string ShowRequestEventName = $"__{AppName}ShowRequest__Event";
    public const string NotifyIconText = "Explorer Tab Utility: Force new windows to tabs.";
    public const string SettingsFileName = "settings.json";
    public const string HotKeyProfilesFileName = "HotKeyProfiles.json";

    /// <summary>
    /// Built-in profiles shipped to first-run users. The numeric <c>Action</c> values are resolved
    /// through the LEGACY mapping in <see cref="HotKeyActionJsonConverter"/> — if the
    /// <c>HotKeyAction</c> enum is ever reordered again, both sides must be updated together.
    /// </summary>
    public const string DefaultHotKeyProfiles = "[{\"Name\":\"Home\",\"HotKeys\":[91,69],\"Scope\":0,\"Action\":0,\"Path\":\"\",\"IsHandled\":true,\"IsEnabled\":true,\"Delay\":0},{\"Name\":\"Duplicate\",\"HotKeys\":[17,68],\"Scope\":1,\"Action\":1,\"Path\":null,\"IsHandled\":true,\"IsEnabled\":true,\"Delay\":0},{\"Name\":\"ReopenClosed\",\"HotKeys\":[16,17,84],\"Scope\":1,\"Action\":2,\"Path\":null,\"IsHandled\":true,\"IsEnabled\":true,\"Delay\":0}]";
}