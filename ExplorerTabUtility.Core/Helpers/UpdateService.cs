using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerTabUtility.Helpers;

/// <summary>How a check ended. "No update" and "the check failed" are different answers to the user.</summary>
public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed
}

/// <summary>A published release, with the asset this architecture would need.</summary>
public sealed record UpdateRelease(Version Version, string Tag, string? InstallerUrl, string? PortableUrl)
{
    public string PageUrl => "https://github.com/saillill/ExplorerTabUtility-WinUI3/releases/tag/" + Tag;
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateRelease? Release, string? Reason = null)
{
    public static UpdateCheckResult Failed(string reason) => new(UpdateCheckStatus.Failed, null, reason);
    public static UpdateCheckResult UpToDate() => new(UpdateCheckStatus.UpToDate, null);
    public static UpdateCheckResult Available(UpdateRelease release) => new(UpdateCheckStatus.UpdateAvailable, release);
}

/// <summary>
/// Asks GitHub for the newest release and fetches the file an update would use.
/// </summary>
/// <remarks>
/// <para>
/// The version is read from the redirect <c>github.com/…/releases/latest</c> answers — deliberately not from
/// <c>api.github.com</c>: the API is a separate host that is frequently unreachable from this app's users
/// (it was the whole of a reported "检查更新失败" — the ordinary github.com host answered fine at the same
/// moment), it is rate-limited per address, and it needs a JSON shape to stay put. The redirect costs one
/// request to a host the app already trusts, and the asset names are built from the tag with the naming the
/// release script has always used.
/// </para>
/// <para>
/// <see cref="IsInstalledBuild"/> decides which file is wanted: an installed copy is replaced by the Setup
/// (which keeps the settings — see <c>installers/installer.iss</c>), while an unzipped copy cannot replace
/// its own running files and is only offered the archive.
/// </para>
/// </remarks>
public static class UpdateService
{
    private const string LatestReleasePage =
        "https://github.com/saillill/ExplorerTabUtility-WinUI3/releases/latest";

    private const string DownloadBase =
        "https://github.com/saillill/ExplorerTabUtility-WinUI3/releases/download/";

    /// <summary>The architecture-suffixed asset name for this build, e.g. <c>Setup_x64</c>.</summary>
    private static string ArchitectureSuffix =>
        RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "arm64"
            : "x64";

    /// <summary>
    /// True when this copy sits under <c>%LOCALAPPDATA%\Programs</c>, which is where the installer puts it
    /// (<c>{userpf}</c> in the .iss). An unzipped copy is anywhere else, and that difference decides whether
    /// an update can replace it in place.
    /// </summary>
    public static bool IsInstalledBuild
    {
        get
        {
            var programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

            return AppContext.BaseDirectory.StartsWith(programs, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The releases page, for a caller that wants to send the user there instead of downloading.</summary>
    public static string ReleasesUrl => "https://github.com/saillill/ExplorerTabUtility-WinUI3/releases";

    public static async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            var tag = await FindLatestTagAsync(ct);
            if (string.IsNullOrWhiteSpace(tag)) return UpdateCheckResult.Failed("no release tag in the redirect");

            // Tags are "v1.0.1"; anything else is not a version this app can compare against.
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var published))
                return UpdateCheckResult.Failed($"unrecognised tag '{tag}'");

            if (published <= current) return UpdateCheckResult.UpToDate();

            var prefix = DownloadBase + tag + "/ExplorerTabUtility_" + tag + "_";

            return UpdateCheckResult.Available(new UpdateRelease(
                published,
                tag,
                prefix + "Setup_" + ArchitectureSuffix + ".exe",
                prefix + "Portable_" + ArchitectureSuffix + ".zip"));
        }
        catch (Exception ex)
        {
            // No network, a blocked host, a shape that changed: all of them are "could not check", and the
            // reason is carried out so the caller can put it in the log rather than leave the user guessing.
            Debug.WriteLine($"UpdateService.CheckAsync failed: {ex.Message}");
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// The tag of the newest release, taken from the redirect <c>/releases/latest</c> answers with.
    /// </summary>
    /// <remarks>
    /// Redirects are not followed: the tag is in the <c>Location</c> header, and following it would download
    /// the release page itself for no reason.
    /// </remarks>
    private static async Task<string?> FindLatestTagAsync(CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ExplorerTabUtility");

        using var response = await client.GetAsync(LatestReleasePage, ct);
        var location = response.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(location)) return null;

        const string marker = "/releases/tag/";
        var index = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : location[(index + marker.Length)..].Trim('/');
    }

    /// <summary>
    /// Downloads a release asset to <paramref name="destination"/>, reporting 0–1 as it goes.
    /// </summary>
    /// <remarks>
    /// Streamed rather than buffered: the Setup is ~23 MB and there is no reason to hold it in memory while
    /// the user watches a progress bar. The length is checked against the advertised one, and a body that
    /// ends short leaves a partial file deleted rather than handed to the installer.
    /// </remarks>
    public static async Task<bool> DownloadAsync(
        string url, string destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ExplorerTabUtility");

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0L;
            var buffer = new byte[81920];
            long written = 0;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(destination))
            {
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;

                    if (total > 0) progress?.Report((double)written / total);
                }
            }

            if (total > 0 && written != total)
            {
                Debug.WriteLine($"UpdateService.DownloadAsync: got {written} of {total} bytes");
                TryDelete(destination);
                return false;
            }

            progress?.Report(1);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateService.DownloadAsync failed: {ex.Message}");
            TryDelete(destination);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A leftover in %TEMP% is not worth failing the update over.
        }
    }

    // ---- The automatic pass ------------------------------------------------------------------------

    /// <summary>What the last check found, whoever ran it — so the settings row can show it.</summary>
    public static UpdateCheckResult? LastCheck { get; private set; }

    /// <summary>
    /// The pass the app runs by itself at startup, if the user left "check automatically" on.
    /// </summary>
    /// <remarks>
    /// With "install automatically" also on, a found update is taken without asking: downloaded, handed to the
    /// installer, and the app comes back by itself. That is why the switch is off by default — it closes the
    /// app while the user is working — but it is what the switch promises when it is on.
    /// </remarks>
    public static async Task RunStartupCheckAsync(Version current, CancellationToken ct = default)
    {
        if (!Managers.SettingsManager.AutoCheckForUpdates) return;

        LastCheck = await CheckAsync(current, ct);
        if (LastCheck.Status != UpdateCheckStatus.UpdateAvailable) return;
        if (!Managers.SettingsManager.AutoUpdate) return;

        var release = LastCheck.Release!;
        var installed = IsInstalledBuild;
        var url = installed ? release.InstallerUrl : release.PortableUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        var destination = Path.Combine(
            Path.GetTempPath(),
            "ExplorerTabUtility_" + release.Tag + (installed ? "_Setup.exe" : "_Portable.zip"));

        if (!await DownloadAsync(url, destination, null, ct)) return;

        if (installed) LaunchInstaller(destination);
        else Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + destination + "\""));
    }

    /// <summary>
    /// Runs a downloaded Setup, and brings the app back when it is done.
    /// </summary>
    /// <remarks>
    /// Detached through <c>cmd</c> deliberately. The installer ends this process while it works —
    /// <c>PrepareToInstall</c> in installer.iss runs taskkill, because this app hides to the tray instead of
    /// exiting and Inno's own Restart Manager handshake would wait forever for a close that never comes — so
    /// nothing inside this process can start the app again afterwards. <c>/wait</c> holds the helper until
    /// the install has finished; the second <c>start</c> then opens the freshly installed copy. Silent rather
    /// than interactive because the user has already agreed, by pressing the button or by switching the
    /// automatic update on.
    /// </remarks>
    public static void LaunchInstaller(string setupPath)
    {
        var exePath = Environment.ProcessPath
                      ?? Path.Combine(AppContext.BaseDirectory, "ExplorerTabUtility.exe");

        var command = "/c start \"\" /wait \"" + setupPath + "\" /SILENT /SUPPRESSMSGBOXES /NORESTART /SP-"
                      + " & start \"\" \"" + exePath + "\"";

        Process.Start(new ProcessStartInfo("cmd.exe", command) { CreateNoWindow = true });
    }
}
