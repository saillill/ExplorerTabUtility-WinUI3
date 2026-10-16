using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
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
public sealed record UpdateRelease(
    Version Version,
    string Tag,
    string Notes,
    string? InstallerUrl,
    string? PortableUrl)
{
    public string PageUrl => "https://github.com/saillill/ExplorerTabUtility-WinUI3/releases/tag/" + Tag;
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateRelease? Release)
{
    public static UpdateCheckResult Failed() => new(UpdateCheckStatus.Failed, null);
    public static UpdateCheckResult UpToDate() => new(UpdateCheckStatus.UpToDate, null);
    public static UpdateCheckResult Available(UpdateRelease release) => new(UpdateCheckStatus.UpdateAvailable, release);
}

/// <summary>
/// Asks GitHub for the newest release and fetches the file an update would use.
/// </summary>
/// <remarks>
/// <para>
/// The repository is public, so the check is a plain HTTPS GET of the "latest release" endpoint — no token,
/// no telemetry, and nothing about the machine is sent beyond what any web request carries.
/// </para>
/// <para>
/// <see cref="IsInstalledBuild"/> is what decides which file is wanted: an installed copy is replaced by the
/// Setup (which keeps the settings, see <c>installers/installer.iss</c>), while an unzipped copy cannot
/// replace its own running files and is only offered the archive.
/// </para>
/// </remarks>
public static class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/saillill/ExplorerTabUtility-WinUI3/releases/latest";

    /// <summary>
    /// The GitHub API answers only requests that identify themselves, and a client with no User-Agent is
    /// rejected outright.
    /// </summary>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ExplorerTabUtility");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// True when this copy sits under <c>%LOCALAPPDATA%\Programs</c>, which is where the installer puts it
    /// (<c>{userpf}</c> in the .iss). An unzipped copy is anywhere else, and the difference decides whether
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

    /// <summary>The architecture-suffixed asset name for this build, e.g. <c>Setup_x64</c>.</summary>
    private static string ArchitectureSuffix =>
        RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "arm64"
            : "x64";

    public static async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(LatestReleaseApi, ct);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return UpdateCheckResult.Failed();

            // Tags are "v1.0.1"; anything that does not parse is not a version this app can compare against.
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var published)) return UpdateCheckResult.Failed();
            if (published <= current) return UpdateCheckResult.UpToDate();

            var notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? "" : "";

            string? installer = null;
            string? portable = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;

                    if (name.EndsWith($"Setup_{ArchitectureSuffix}.exe", StringComparison.OrdinalIgnoreCase))
                        installer = url;
                    else if (name.EndsWith($"Portable_{ArchitectureSuffix}.zip", StringComparison.OrdinalIgnoreCase))
                        portable = url;
                }
            }

            return UpdateCheckResult.Available(
                new UpdateRelease(published, tag!, notes, installer, portable));
        }
        catch (Exception ex)
        {
            // No network, a rate-limited API, a shape that changed: all of them are "could not check".
            Debug.WriteLine($"UpdateService.CheckAsync failed: {ex.Message}");
            return UpdateCheckResult.Failed();
        }
    }

    /// <summary>
    /// Downloads a release asset to <paramref name="destination"/>, reporting 0–1 as it goes.
    /// </summary>
    /// <remarks>
    /// Streamed rather than buffered: the Setup is ~23 MB and there is no reason to hold it in memory while
    /// the user watches a progress bar. The length is reported against the advertised one, and a body that
    /// ends short leaves a partial file deleted rather than handed to the installer.
    /// </remarks>
    public static async Task<bool> DownloadAsync(
        string url, string destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
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
}
