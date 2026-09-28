using System;
using System.Collections.Generic;
using ExplorerTabUtility.Models;

namespace ExplorerTabUtility.Abstractions;

/// <summary>
/// One hotkey-profile card in the shortcuts list, as seen by the Core library.
/// <para>
/// The WPF shell implements this over <c>HotKeyProfileControl</c>; the WinUI shell implements
/// it over a <c>SettingsExpander</c>. Only the members <c>ProfileManager</c> and the shells'
/// language-refresh / focus-fallback paths actually need are exposed.
/// </para>
/// </summary>
public interface IProfileCardView
{
    HotKeyProfile Profile { get; }

    bool IsEnabled { get; set; }

    /// <summary>Re-reads all localized strings after a language switch.</summary>
    void RefreshLocalization();

    /// <summary>
    /// Moves focus off the hotkey-capture input. Called when the settings window deactivates,
    /// so an in-progress capture does not swallow the keys the user pressed elsewhere.
    /// </summary>
    void EndHotKeyCapture();
}

/// <summary>Callbacks a shell must wire into every card it creates.</summary>
/// <param name="Remove">Asks <c>ProfileManager</c> to delete the profile.</param>
/// <param name="CaptureStarted">Global hooks must be suspended while capturing keys.</param>
/// <param name="CaptureStopped">Capture finished; global hooks may resume.</param>
public sealed record ProfileCardCallbacks(
    Action<HotKeyProfile> Remove,
    Action CaptureStarted,
    Action CaptureStopped,
    Action Save);

/// <summary>Creates the platform-specific card control for a profile.</summary>
public delegate IProfileCardView ProfileCardFactory(HotKeyProfile profile, ProfileCardCallbacks callbacks);

/// <summary>
/// The container that hosts profile cards. Replaces the Core library's former dependency on
/// <c>System.Windows.Controls.Panel</c>.
/// </summary>
public interface IProfilesHost
{
    void Clear();
    void Add(IProfileCardView card);
    void Remove(IProfileCardView card);

    /// <summary>Cards currently displayed, in display order.</summary>
    IReadOnlyList<IProfileCardView> Cards { get; }
}
