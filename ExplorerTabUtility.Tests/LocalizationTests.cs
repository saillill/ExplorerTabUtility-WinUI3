using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Resources;
using ExplorerTabUtility.Helpers;
using Xunit;

namespace ExplorerTabUtility.Tests;

/// <summary>
/// Culture resolution and resource completeness. The second test is the one that matters most in
/// practice: nine resource files that must stay in lock-step is exactly the kind of invariant that a
/// hand-run script catches once and then forgets.
/// </summary>
public class LocalizationTests
{
    [Theory]
    // Exact matches.
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("en", "en")]
    [InlineData("ja", "ja")]
    // Region variants walk up to the language we ship.
    [InlineData("de-AT", "de")]
    [InlineData("fr-CA", "fr")]
    [InlineData("en-GB", "en")]
    [InlineData("es-MX", "es")]
    // Chinese is the one family where the script decides, not the region.
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    [InlineData("zh-SG", "zh-CN")]
    // Anything unsupported falls back to English rather than to the invariant culture's resources.
    [InlineData("xx-YY", LocalizationService.FallbackLanguage)]
    [InlineData("", LocalizationService.FallbackLanguage)]
    [InlineData(null, LocalizationService.FallbackLanguage)]
    public void ResolveSupported_maps_a_culture_onto_a_shipped_language(string? input, string expected)
        => Assert.Equal(expected, LocalizationService.ResolveSupported(input));

    [Fact]
    public void Every_supported_language_ships_the_same_resource_keys()
    {
        var assembly = typeof(LocalizationService).Assembly;
        var manager = new ResourceManager("ExplorerTabUtility.Properties.Resources", assembly);

        var neutralKeys = ReadKeys(manager, CultureInfo.InvariantCulture)
                          ?? throw new Xunit.Sdk.XunitException("the neutral (English) resource set is missing");

        Assert.NotEmpty(neutralKeys);

        foreach (var language in LocalizationService.SupportedLanguages)
        {
            var culture = new CultureInfo(language.Code);

            // English IS the neutral file — LocalizationService documents that it deliberately has no
            // satellite of its own — so only the other languages are expected to resolve a resource set
            // of their own here (tryParents: false, which is what makes a missing satellite visible as
            // null rather than silently falling back to English).
            var isNeutralLanguage = string.Equals(
                language.Code, LocalizationService.FallbackLanguage, StringComparison.OrdinalIgnoreCase);

            var keys = isNeutralLanguage ? neutralKeys : ReadKeys(manager, culture);

            Assert.True(keys is not null,
                $"no resource set for '{language.Code}' — the .resx or its satellite assembly is missing " +
                "(check SatelliteResourceLanguages in Directory.Build.props)");

            var missing = neutralKeys.Except(keys!).OrderBy(k => k).ToArray();
            var extra = keys!.Except(neutralKeys).OrderBy(k => k).ToArray();

            Assert.True(missing.Length == 0, $"'{language.Code}' is missing keys: {string.Join(", ", missing)}");
            Assert.True(extra.Length == 0, $"'{language.Code}' has keys the neutral file does not: {string.Join(", ", extra)}");

            if (isNeutralLanguage) continue;

            // A satellite that exists but is a copy of the English file would pass the two checks above
            // and still ship an untranslated UI, so the labels that must differ are compared directly.
            foreach (var key in new[] { "ToggleOn", "ToggleOff", "DefaultProfileName" })
                Assert.NotEqual(manager.GetString(key, CultureInfo.InvariantCulture), manager.GetString(key, culture));

            foreach (var key in neutralKeys)
            {
                var value = manager.GetString(key, culture);
                Assert.False(string.IsNullOrWhiteSpace(value), $"'{language.Code}' has an empty value for '{key}'");
            }
        }
    }

    [Fact]
    public void A_key_that_is_not_translated_falls_back_to_its_own_name_rather_than_throwing()
        => Assert.Equal("NoSuchKeyExists", LocalizationService.Get("NoSuchKeyExists"));

    [Theory]
    // The placeholder a brand-new profile starts with, in three scripts, plus the blank case.
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("New shortcut", true)]
    [InlineData("新建快捷键", true)]
    [InlineData("新增快速鍵", true)]
    [InlineData("新しいショートカット", true)]
    [InlineData("Neues Tastenkürzel", true)]
    // Anything the user typed is NOT a placeholder — it must survive PruneUntouchedProfiles.
    [InlineData("My shortcut", false)]
    [InlineData("New shortcut 2", false)]
    public void IsDefaultProfileName_only_matches_the_generated_placeholders(string name, bool expected)
        => Assert.Equal(expected, LocalizationService.Instance.IsDefaultProfileName(name));

    private static HashSet<string>? ReadKeys(ResourceManager manager, CultureInfo culture)
    {
        // tryParents: false is the point of the check — a language whose satellite is missing comes
        // back as null instead of silently resolving to English.
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        if (set is null) return null;

        return set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet();
    }
}
