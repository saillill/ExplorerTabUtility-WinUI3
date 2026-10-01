using System;
using System.Linq;
using ExplorerTabUtility.Helpers;
using H.Hooks;
using Xunit;

namespace ExplorerTabUtility.Tests;

/// <summary>
/// <see cref="Helper.NormalizeLocation"/> is the funnel every user-supplied location goes through —
/// hotkey profiles, the default Explorer location, and the strings read back out of a live window.
/// A regression here does not throw, it just sends Explorer somewhere wrong, so each rule is pinned.
/// </summary>
public class LocationNormalizationTests
{
    [Theory]
    // Web URLs are returned verbatim: the back-slash rewrite below would destroy them.
    [InlineData("https://github.com/w4po/ExplorerTabUtility", "https://github.com/w4po/ExplorerTabUtility")]
    [InlineData("ftp://host/share", "ftp://host/share")]
    [InlineData("  https://host/x  ", "https://host/x")]
    // A scheme-less string containing "://" is NOT a URL (a scheme must start with a letter), so it
    // still goes through path normalisation — both slashes become backslashes.
    [InlineData("1://x", @"1:\\x")]
    public void NonFileUrls_are_returned_verbatim_but_trimmed(string input, string expected)
        => Assert.Equal(expected, Helper.NormalizeLocation(input));

    [Theory]
    [InlineData(@"C:\Users\Docs", @"C:\Users\Docs")]
    [InlineData("C:/Users/Docs", @"C:\Users\Docs")]
    [InlineData(@"C:\a/b\c", @"C:\a\b\c")]
    [InlineData(@"C:\Users\Docs\", @"C:\Users\Docs")]
    [InlineData(@"C:\Users\Docs/", @"C:\Users\Docs")]
    [InlineData("  \"C:\\Program Files\"  ", @"C:\Program Files")]
    [InlineData("'C:\\Temp'", @"C:\Temp")]
    public void Paths_are_separator_normalised_and_edge_trimmed(string input, string expected)
        => Assert.Equal(expected, Helper.NormalizeLocation(input));

    [Theory]
    // A bare "C:" means "current directory on C", not the root — the trailing separator must survive
    // so the meaning does not depend on the process working directory.
    [InlineData("C:", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    // A leading "\\" is the UNC marker and "\\?\" the extended-length prefix: trimming the FRONT
    // destroys them, so only the end may be trimmed.
    [InlineData(@"\\server\share\", @"\\server\share")]
    [InlineData(@"\\?\C:\very\long\", @"\\?\C:\very\long")]
    public void Roots_and_unc_prefixes_survive(string input, string expected)
        => Assert.Equal(expected, Helper.NormalizeLocation(input));

    [Theory]
    // Both spellings normalise to the same canonical form Windows itself uses for a shell folder,
    // so a profile saved from the picker and one typed by hand compare equal later.
    [InlineData("{A8CDFF1C-4878-43be-B5FD-F8091C1C60D0}", "shell:::{A8CDFF1C-4878-43be-B5FD-F8091C1C60D0}")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    [InlineData("  {20D04FE0-3AEA-1069-A2D8-08002B30309D}  ", "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    public void Clsid_paths_get_their_shell_prefix(string input, string expected)
        => Assert.Equal(expected, Helper.NormalizeLocation(input));

    [Fact]
    public void Environment_variables_are_expanded()
    {
        var result = Helper.NormalizeLocation("%TEMP%");

        Assert.DoesNotContain('%', result);
        Assert.True(System.IO.Path.IsPathRooted(result), $"expected a rooted path, got '{result}'");
    }

    [Fact]
    public void The_file_url_shape_the_shell_hands_back_is_preserved()
    {
        // GetLocation() reads "file:///C:/…" out of a window and this exact shape is what the
        // Navigate2/ParseDisplayName round trip tolerates — asserted so a future "tidy-up" of the
        // back-slash rewriting cannot silently break navigation to the default location.
        Assert.Equal(
            @"file:\\\C:\Users\x\Downloads",
            Helper.NormalizeLocation("file:///C:/Users/x/Downloads"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_input_is_returned_untouched(string input)
        => Assert.Equal(input, Helper.NormalizeLocation(input));
}

/// <summary>
/// The hotkey text shown in the collapsed profile row and in the tray-free parts of the UI.
/// Only the mappings this app defines are asserted — the rest comes from H.Hooks' own enum.
/// </summary>
public class HotKeyDisplayTests
{
    [Theory]
    [InlineData(Key.MouseLeft, "LMB")]
    [InlineData(Key.MouseRight, "RMB")]
    [InlineData(Key.MouseMiddle, "MMB")]
    [InlineData(Key.MouseXButton1, "X1")]
    [InlineData(Key.MouseXButton2, "X2")]
    public void Mouse_buttons_use_the_short_labels(Key key, string expected)
        => Assert.Equal(expected, key.ToDisplayString());

    [Fact]
    public void Keys_are_joined_with_a_plus_and_double_click_gets_a_suffix()
    {
        Key[] keys = [Key.Ctrl, Key.D];

        Assert.Contains(" + ", keys.HotKeysToString());
        Assert.EndsWith("_DBL", keys.HotKeysToString(isDoubleClick: true));
        Assert.DoesNotContain("_DBL", keys.HotKeysToString(isDoubleClick: false));
    }
}

/// <summary>
/// The About page feeds third-party SVG content straight into the downloader, so the scheme check is
/// the only thing standing between that content and an arbitrary request. It must reject before any
/// network work happens — hence "throws" rather than "returns null".
/// </summary>
public class HttpByteCacheUrlTests
{
    [Theory]
    [InlineData("http://example.com/a.png")]
    [InlineData("file:///C:/a.png")]
    [InlineData("ftp://example.com/a.png")]
    [InlineData("/relative/a.png")]
    [InlineData("not a url")]
    public async System.Threading.Tasks.Task NonHttps_urls_are_rejected(string url)
        => await Assert.ThrowsAsync<ArgumentException>(() => HttpByteCache.GetBytesAsync(url));

    [Fact]
    public async System.Threading.Tasks.Task Empty_url_is_rejected()
        => await Assert.ThrowsAsync<ArgumentException>(() => HttpByteCache.GetBytesAsync(string.Empty));
}
