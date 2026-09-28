using System;
using System.Linq;
using System.ComponentModel;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace ExplorerTabUtility.Helpers;

/// <summary>
/// One language the UI ships translations for.
/// <para>
/// <see cref="NativeName"/> is the endonym and is deliberately <b>not</b> looked up in the resource
/// files: a language picker has to be readable to someone who cannot read the current UI language.
/// </para>
/// </summary>
public sealed record SupportedLanguage(string Code, string NativeName);

public class LocalizationService : INotifyPropertyChanged
{
    private static LocalizationService? _instance;
    public static LocalizationService Instance => _instance ??= new LocalizationService();

    /// <summary>
    /// Every satellite <c>Resources.&lt;code&gt;.resx</c>, in picker order.
    /// <para>
    /// Ordered by how widely each language is used: the two Chinese variants first (they are this
    /// app's main audience and belong next to each other), then the rest by number of speakers —
    /// English, Spanish, French, Russian, German, Japanese, Korean.
    /// </para>
    /// <para>
    /// The neutral <c>Resources.resx</c> is English, so "en" has no satellite of its own — it is
    /// also the fallback for any culture that is not listed here.
    /// </para>
    /// <para>
    /// Adding a language means adding the <c>.resx</c>, this entry, and the code in the
    /// <c>SatelliteResourceLanguages</c> property of both project files — otherwise the satellite
    /// assembly is built but never copied to the publish output.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<SupportedLanguage> SupportedLanguages =
    [
        new("zh-CN",   "简体中文"),
        new("zh-Hant", "繁體中文"),
        new("en",      "English"),
        new("es",      "Español"),
        new("fr",      "Français"),
        new("ru",      "Русский"),
        new("de",      "Deutsch"),
        new("ja",      "日本語"),
        new("ko",      "한국어"),
    ];

    /// <summary>Language used when the requested culture has no translation.</summary>
    public const string FallbackLanguage = "en";

    private readonly ResourceManager _resourceManager;

    private LocalizationService()
    {
        _resourceManager = new ResourceManager("ExplorerTabUtility.Properties.Resources", typeof(LocalizationService).Assembly);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public static event Action? LanguageChanged;

    public string this[string key]
    {
        get
        {
            try { return _resourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key; }
            catch (MissingManifestResourceException) { return key; }
        }
    }

    public static string Get(string key) => Instance[key];

    /// <summary>
    /// Reads a string for an explicit culture rather than the active one.
    /// Used by the legacy-name migration, which must produce English names regardless of the UI
    /// language the user happens to be running.
    /// </summary>
    public static string Get(string key, string cultureName)
    {
        try
        {
            return Instance._resourceManager.GetString(key, new CultureInfo(cultureName)) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    /// <summary>
    /// The language the UI is actually showing right now, as a code from
    /// <see cref="SupportedLanguages"/>. Reflects the effective culture rather than the stored
    /// setting, so an unset setting (follow the system) reports what the system resolved to.
    /// </summary>
    public string Language
    {
        get => ResolveSupported(CultureInfo.CurrentUICulture.Name);
        set
        {
            var culture = CreateCulture(ResolveSupported(value));
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            OnPropertyChanged("Item[]");
            OnPropertyChanged("");
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>
    /// Maps an arbitrary culture name onto a supported language by walking its parent chain, so
    /// <c>zh-TW</c>/<c>zh-HK</c> land on <c>zh-Hant</c> and <c>de-AT</c> lands on <c>de</c>.
    /// Anything unmatched falls back to <see cref="FallbackLanguage"/>.
    /// </summary>
    public static string ResolveSupported(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName)) return FallbackLanguage;

        CultureInfo culture;
        try { culture = new CultureInfo(cultureName); }
        catch (CultureNotFoundException) { return FallbackLanguage; }

        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            var match = SupportedLanguages.FirstOrDefault(
                l => string.Equals(l.Code, current.Name, StringComparison.OrdinalIgnoreCase));

            if (match is not null) return match.Code;

            // Parent of the invariant culture is itself; stop before spinning.
            if (string.Equals(current.Parent.Name, current.Name, StringComparison.OrdinalIgnoreCase))
                break;
        }

        // Chinese is the one family where the script decides the language rather than the region
        // alone; keep this so a Traditional user without an exact culture match still gets 繁體.
        if (culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
            return culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                   culture.Name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                   culture.Name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                   culture.Name.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase)
                ? "zh-Hant"
                : "zh-CN";

        return FallbackLanguage;
    }

    /// <summary>
    /// Builds the culture, tolerating a name this runtime does not know: an unusable culture would
    /// otherwise throw on the UI thread. The invariant culture still resolves resources, via the
    /// neutral (English) file.
    /// </summary>
    private static CultureInfo CreateCulture(string code)
    {
        try { return new CultureInfo(code); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    public void SetLanguage(string lang) => Language = lang;

    /// <summary>
    /// Applies the language stored in settings; leaves the system culture in place when unset.
    /// <para>
    /// Must be callable before any UI exists: the "already running" notice is shown before the XAML
    /// application is created, and it used to come out in the system language even when the user had
    /// chosen another one.
    /// </para>
    /// </summary>
    public static void ApplySavedLanguage()
    {
        var saved = Managers.SettingsManager.Language;
        if (string.IsNullOrWhiteSpace(saved)) return;

        Instance.Language = saved;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
