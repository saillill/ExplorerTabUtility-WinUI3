using System;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using ExplorerTabUtility.Abstractions;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Managers;

/// <summary>
/// Owns the hotkey-profile list — the persistent copy and the editing copy — and keeps the
/// UI in sync through <see cref="IProfilesHost"/>.
/// <para>
/// The UI toolkit dependency was removed by replacing the old
/// <c>System.Windows.Controls.Panel</c> field with <see cref="IProfilesHost"/> plus a
/// <see cref="ProfileCardFactory"/> that the shell supplies.
/// </para>
/// <para>
/// <b>Threading contract.</b> The hook threads only ever read the immutable snapshot published by
/// <see cref="GetProfilesSnapshot"/>; the UI thread builds a brand-new array and swaps it in with a
/// single atomic write. Neither side shares a mutable collection, so a keystroke in the settings
/// window can no longer tear the list the low-level keyboard hook is walking (AUD-02). <b>Never</b>
/// mutate a published snapshot in place.
/// </para>
/// </summary>
public class ProfileManager
{
    // Published snapshot: read by the hook threads, replaced atomically by the UI thread. Volatile so
    // a hook thread always observes the most recent array reference.
    private volatile IReadOnlyList<HotKeyProfile> _profilesSnapshot = [];

    // Temporary state (for editing). Touched only on the UI thread.
    private readonly List<HotKeyProfile> _tempProfiles = [];

    private readonly IProfilesHost _profilesHost;
    private readonly ProfileCardFactory _cardFactory;
    private readonly ProfileCardCallbacks _callbacks;

    public event Action? KeybindingsHookStarted;
    public event Action? KeybindingsHookStopped;

    /// <summary>
    /// Raised (on the UI thread) whenever the profile list or any profile's user-visible state
    /// changes — add, remove, rename, enable toggle, import. Shells use it to rebuild derived
    /// views such as the tray profile menus; without it the tray showed a stale list for the
    /// whole session.
    /// </summary>
    public event Action? ProfilesChanged;

    public ProfileManager(IProfilesHost profilesHost, ProfileCardFactory cardFactory)
    {
        _profilesHost = profilesHost ?? throw new ArgumentNullException(nameof(profilesHost));
        _cardFactory = cardFactory ?? throw new ArgumentNullException(nameof(cardFactory));

        _callbacks = new ProfileCardCallbacks(
            Remove,
            () => KeybindingsHookStarted?.Invoke(),
            () => KeybindingsHookStopped?.Invoke(),
            SaveProfiles);

        LoadSavedProfiles();
        RefreshPanel();
    }

    /// <summary>Cards currently displayed. Lets shells refresh localization or drop focus.</summary>
    public IReadOnlyList<IProfileCardView> Cards => _profilesHost.Cards;

    private void LoadSavedProfiles()
    {
        try
        {
            var profiles = JsonSerializer.Deserialize<List<HotKeyProfile>>(SettingsManager.HotKeyProfiles);
            if (profiles == null) return;

            MigrateLegacyNames(profiles);

            ReplaceTempProfiles(profiles);
        }
        catch
        {
            // Invalid JSON: fall back to the default profiles so a later save
            // does not silently wipe the user hotkey list.
            try
            {
                var defaults = JsonSerializer.Deserialize<List<HotKeyProfile>>(Constants.DefaultHotKeyProfiles);
                if (defaults != null)
                    ReplaceTempProfiles(defaults);
            }
            catch
            {
                // Ignore - defaults are static and should always parse.
            }
        }
    }

    /// <summary>
    /// Translates profile names the WPF build generated automatically.
    /// <para>
    /// That build named new profiles after the enum's Chinese <c>[Description]</c>, so an upgraded
    /// settings file carries names like "显示/隐藏". Only exact matches of those generated names are
    /// rewritten — anything the user typed is left alone.
    /// </para>
    /// </summary>
    private static void MigrateLegacyNames(List<HotKeyProfile> profiles)
    {
        var renamed = false;

        foreach (var profile in profiles)
        {
            var english = HotKeyActionCatalog.MigrateLegacyProfileName(profile.Name);
            if (english is null) continue;

            profile.Name = english;
            renamed = true;
        }

        // Write back immediately so the rename survives even if nothing else triggers a save.
        if (renamed)
            SettingsManager.HotKeyProfiles = JsonSerializer.Serialize(profiles);
    }

    /// <summary>Resets the editing list from <paramref name="profiles"/> and republishes the snapshot.</summary>
    private void ReplaceTempProfiles(IEnumerable<HotKeyProfile> profiles)
    {
        _tempProfiles.Clear();
        foreach (var profile in profiles)
            _tempProfiles.Add(profile.Clone());

        PublishSnapshot(_tempProfiles);
    }

    /// <summary>
    /// Builds a fresh snapshot from <paramref name="profiles"/> and swaps it in atomically.
    /// <para>
    /// The array is fully materialised (and cloned) <b>before</b> the write, so a hook thread sees
    /// either the whole previous snapshot or the whole new one — never a half-cleared list. The clones
    /// keep later in-place edits of <see cref="_tempProfiles"/> from being observed by the hook thread.
    /// </para>
    /// </summary>
    private void PublishSnapshot(IEnumerable<HotKeyProfile> profiles)
    {
        var snapshot = profiles.Select(profile => profile.Clone()).ToArray();

        // A plain assignment to a volatile field is itself a release barrier — semantically identical to
        // Volatile.Write(ref _profilesSnapshot, snapshot), but without the CS0420 "reference to a volatile
        // field will not be treated as volatile" warning that passing it by ref produces.
        _profilesSnapshot = snapshot;
    }

    public void AddProfile(HotKeyProfile? profile = null)
    {
        // A new row starts with the localized placeholder name rather than an empty one: an empty
        // name makes the collapsed row fall back to showing the hotkey, which reads as though the
        // row had already been filled in. The name is never taken from the mapped action either —
        // that is what the old WPF build did, and why upgraded files carry names like "显示/隐藏"
        // (see MigrateLegacyNames).
        var newProfile = profile?.Clone() ??
                         new HotKeyProfile { Name = LocalizationService.DefaultProfileName };
        _tempProfiles.Add(newProfile);
        _profilesHost.Add(_cardFactory(newProfile, _callbacks));
        ProfilesChanged?.Invoke();
    }

    private void Remove(HotKeyProfile profile)
    {
        _tempProfiles.Remove(profile);

        var card = FindCardByProfile(profile);
        if (card != null)
            _profilesHost.Remove(card);

        // Publish the removal at once so the hook threads stop matching a deleted profile.
        PublishSnapshot(_tempProfiles);
        ProfilesChanged?.Invoke();
    }

    private void RefreshPanel()
    {
        _profilesHost.Clear();

        foreach (var profile in _tempProfiles)
        {
            _profilesHost.Add(_cardFactory(profile, _callbacks));
        }
    }

    /// <summary>Re-reads every card's localized strings after a language switch.</summary>
    public void RefreshLocalization()
    {
        foreach (var card in _profilesHost.Cards)
            card.RefreshLocalization();
    }

    /// <summary>Moves focus out of any in-progress hotkey capture (e.g. when the window deactivates).</summary>
    public void EndHotKeyCapture()
    {
        foreach (var card in _profilesHost.Cards)
            card.EndHotKeyCapture();
    }

    public void SetProfileEnabledFromTray(HotKeyProfile profile, bool enabled)
    {
        // Only the editing copy is written. The published snapshot is immutable by contract (see the
        // class remarks): a bool store into one of its elements "works" because it is atomic, but it is
        // the one mutation the contract forbids, and copying the pattern elsewhere is how the hook
        // threads end up reading half-updated state. SaveProfiles below republishes a fresh snapshot, so
        // the change still reaches the hooks immediately.
        //
        // FirstOrDefault, not First: a profile removed from the panel between the tray menu being built
        // and the click would otherwise throw InvalidOperationException (AUD-02).
        var tempProfile = _tempProfiles.FirstOrDefault(p => p.Id == profile.Id);
        if (tempProfile == null) return;

        tempProfile.IsEnabled = enabled;

        var card = FindCardByProfile(tempProfile);
        if (card != null) card.IsEnabled = enabled;

        // Persist immediately: tray toggles happen while the window may not be open.
        SaveProfiles();
    }

    /// <summary>
    /// The published snapshot. Safe to read, hold or enumerate from any thread: it is replaced
    /// atomically and never mutated in place (AUD-02).
    /// </summary>
    public IReadOnlyList<HotKeyProfile> GetProfiles() => _profilesSnapshot;

    /// <summary>Explicit-name alias of <see cref="GetProfiles"/>, used by the hook constructors.</summary>
    public IReadOnlyList<HotKeyProfile> GetProfilesSnapshot() => _profilesSnapshot;

    public IEnumerable<HotKeyProfile> GetKeyboardProfiles() => _profilesSnapshot.Where(p => !p.IsMouse);
    public IEnumerable<HotKeyProfile> GetMouseProfiles() => _profilesSnapshot.Where(p => p.IsMouse);

    public void SaveProfiles()
    {
        // Nothing is pruned here. Saving now happens on every keystroke, and a profile can only be
        // completed after its row exists — so any "delete what looks incomplete" rule here deletes
        // the row the user is working on. Cleanup moved to PruneUntouchedProfiles, which runs only
        // when the settings window is dismissed.

        // Publish a fresh snapshot from the editing list. This replaces the old in-place
        // Clear()+Add() that raced the hook threads (AUD-02).
        PublishSnapshot(_tempProfiles);

        // Save to settings
        SettingsManager.HotKeyProfiles = JsonSerializer.Serialize(_profilesSnapshot);

        ProfilesChanged?.Invoke();
    }

    /// <summary>
    /// Drops rows that are still completely blank — no hotkeys and no typed name.
    /// <para>
    /// Only safe when the user is not mid-edit, so it is called when the settings window is
    /// dismissed rather than on every save.
    /// </para>
    /// </summary>
    public void PruneUntouchedProfiles()
    {
        // "Untouched" now also covers a row whose name is still the placeholder it was created
        // with — a new row is no longer nameless, so the old blank-name test alone would keep every
        // abandoned row forever.
        var localization = LocalizationService.Instance;
        var untouched = _tempProfiles
            .Where(profile => (profile.HotKeys == null || profile.HotKeys.Length == 0)
                              && localization.IsDefaultProfileName(profile.Name))
            .ToList();

        if (untouched.Count == 0) return;

        untouched.ForEach(Remove);
        SaveProfiles();
    }

    public void ImportProfiles(string jsonString)
    {
        try
        {
            var importedList = JsonSerializer.Deserialize<List<HotKeyProfile>>(jsonString);
            if (importedList == null) return;

            ReplaceTempProfiles(importedList);

            RefreshPanel();
            ProfilesChanged?.Invoke();
        }
        catch
        {
            // Invalid JSON or deserialization error
        }
    }

    public string ExportProfiles() => JsonSerializer.Serialize(_profilesSnapshot);

    private IProfileCardView? FindCardByProfile(HotKeyProfile profile)
    {
        var cards = _profilesHost.Cards;

        foreach (var card in cards)
        {
            if (ReferenceEquals(card.Profile, profile))
                return card;
        }

        // Clone() preserves Id, so fall back to identity-by-Id for callers that hold a copy.
        foreach (var card in cards)
        {
            if (card.Profile.Id == profile.Id)
                return card;
        }

        return null;
    }
}
