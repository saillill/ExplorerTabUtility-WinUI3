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
/// </summary>
public class ProfileManager
{
    // Saved state (persistent)
    private readonly List<HotKeyProfile> _savedProfiles = [];

    // Temporary state (for editing)
    private readonly List<HotKeyProfile> _tempProfiles = [];

    private readonly IProfilesHost _profilesHost;
    private readonly ProfileCardFactory _cardFactory;
    private readonly ProfileCardCallbacks _callbacks;

    public event Action? KeybindingsHookStarted;
    public event Action? KeybindingsHookStopped;

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

            _savedProfiles.Clear();
            _savedProfiles.AddRange(profiles);

            // Create temporary copies
            _tempProfiles.Clear();
            foreach (var p in _savedProfiles)
            {
                _tempProfiles.Add(p.Clone());
            }
        }
        catch
        {
            // Invalid JSON: fall back to the default profiles so a later save
            // does not silently wipe the user hotkey list.
            try
            {
                var defaults = JsonSerializer.Deserialize<List<HotKeyProfile>>(Constants.DefaultHotKeyProfiles);
                if (defaults != null)
                {
                    _savedProfiles.Clear();
                    _savedProfiles.AddRange(defaults);
                    _tempProfiles.Clear();
                    foreach (var p in _savedProfiles)
                    {
                        _tempProfiles.Add(p.Clone());
                    }
                }
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

    public void AddProfile(HotKeyProfile? profile = null)
    {
        var newProfile = profile?.Clone() ?? new HotKeyProfile();
        _tempProfiles.Add(newProfile);
        _profilesHost.Add(_cardFactory(newProfile, _callbacks));
    }

    private void Remove(HotKeyProfile profile)
    {
        _tempProfiles.Remove(profile);

        var card = FindCardByProfile(profile);
        if (card != null)
            _profilesHost.Remove(card);
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
        // Find and update in saved profiles (for tray menu)
        var savedProfile = _savedProfiles.First(p => p.Id == profile.Id);
        savedProfile.IsEnabled = enabled;

        // Find and update in temp profiles (for panel)
        var tempProfile = _tempProfiles.FirstOrDefault(p => p.Id == profile.Id);
        if (tempProfile == null) return;

        tempProfile.IsEnabled = enabled;
        var card = FindCardByProfile(tempProfile);
        if (card != null) card.IsEnabled = enabled;

        // Persist immediately: tray toggles happen while the window may not be open.
        SaveProfiles();
    }

    public IReadOnlyList<HotKeyProfile> GetProfiles() => _savedProfiles.AsReadOnly();
    public IEnumerable<HotKeyProfile> GetKeyboardProfiles() => _savedProfiles.Where(p => !p.IsMouse);
    public IEnumerable<HotKeyProfile> GetMouseProfiles() => _savedProfiles.Where(p => p.IsMouse);

    public void SaveProfiles()
    {
        // Nothing is pruned here. Saving now happens on every keystroke, and a profile can only be
        // completed after its row exists — so any "delete what looks incomplete" rule here deletes
        // the row the user is working on. Cleanup moved to PruneUntouchedProfiles, which runs only
        // when the settings window is dismissed.

        // Update saved profiles
        _savedProfiles.Clear();
        foreach (var profile in _tempProfiles)
        {
            _savedProfiles.Add(profile.Clone());
        }

        // Save to settings
        SettingsManager.HotKeyProfiles = JsonSerializer.Serialize(_savedProfiles);
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
        var untouched = _tempProfiles
            .Where(profile => (profile.HotKeys == null || profile.HotKeys.Length == 0)
                              && string.IsNullOrWhiteSpace(profile.Name))
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

            _tempProfiles.Clear();
            foreach (var profile in importedList)
            {
                _tempProfiles.Add(profile.Clone());
            }

            RefreshPanel();
        }
        catch
        {
            // Invalid JSON or deserialization error
        }
    }

    public string ExportProfiles() => JsonSerializer.Serialize(_savedProfiles);

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
