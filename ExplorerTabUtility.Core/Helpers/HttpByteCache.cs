using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace ExplorerTabUtility.Helpers;

/// <summary>
/// Session-level byte cache for HTTP(S) downloads, keyed by URL.
/// <para>
/// The About page downloads supporter avatars and the sponsors SVG from the network every time it is
/// opened. Those payloads do not change within a run, so the raw bytes are cached and a second open
/// performs no network I/O at all. Concurrent callers asking for the same URL share a single download.
/// </para>
/// <para>
/// The cache is bounded: once more than <see cref="MaxEntries"/> distinct URLs have been stored the
/// oldest insertions are dropped, so it cannot grow without limit. Failures are never memoized — the
/// caller still receives the exception, so the About page keeps its existing graceful fallback.
/// </para>
/// <para>
/// This is intentionally memory-only. A disk cache under <c>%APPDATA%</c> would survive restarts but
/// needs freshness/TTL and cleanup rules (avatars can change, the sponsors file is regenerated), and
/// that complexity is not worth it for data that is re-fetched at most once per launch.
/// </para>
/// </summary>
public static class HttpByteCache
{
    /// <summary>Upper bound on cached URLs (a handful of avatars plus the sponsors SVG).</summary>
    private const int MaxEntries = 64;

    /// <summary>Shared client: one connection pool instead of a brand-new <c>HttpClient</c> per image.</summary>
    private static readonly System.Net.Http.HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Lazy<Task<...>> (ExecutionAndPublication) so the matching concurrent misses await one download.
    private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> InsertionOrder = new();

    /// <summary>
    /// Returns the bytes for <paramref name="url"/>, downloading them at most once per session.
    /// The first caller performs the request; later callers return the cached bytes. Any exception the
    /// download throws (for example an unsupported scheme) propagates and is not cached.
    /// <para>
    /// Only <c>https</c> is accepted. The URL is not always a constant this app chose: the About page
    /// feeds the sponsors SVG's <c>&lt;image href&gt;</c> straight in, so anything that can influence
    /// that file could otherwise aim this app at an arbitrary <c>http://</c> or <c>file://</c> target —
    /// an intranet probe, with the response decoded as an image.
    /// </para>
    /// </summary>
    public static async Task<byte[]> GetBytesAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) throw new ArgumentException("URL must be non-empty.", nameof(url));

        if (!IsAllowedUrl(url))
            throw new ArgumentException(
                $"Only https URLs are fetched (got '{Describe(url)}').", nameof(url));

        if (Cache.TryGetValue(url, out var cached))
            return await cached.Value.ConfigureAwait(false);

        var candidate = new Lazy<Task<byte[]>>(() => DownloadAsync(url));
        var entry = Cache.GetOrAdd(url, candidate);

        try
        {
            var bytes = await entry.Value.ConfigureAwait(false);

            // Only the caller that actually inserted the entry records its order / trims the cache.
            if (ReferenceEquals(entry, candidate))
            {
                InsertionOrder.Enqueue(url);
                TrimToCapacity();
            }

            return bytes;
        }
        catch
        {
            // Never memoize a failure: a transient network error must not stick for the whole session.
            if (ReferenceEquals(entry, candidate))
                Cache.TryRemove(url, out _);

            throw;
        }
    }

    private static Task<byte[]> DownloadAsync(string url) => Client.GetByteArrayAsync(url);

    /// <summary>True only for an absolute <c>https</c> URL.</summary>
    private static bool IsAllowedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>A short, log-safe rendering of a rejected URL (data: payloads are tens of KB).</summary>
    private static string Describe(string url) => url.Length <= 64 ? url : url[..64] + "…";

    private static void TrimToCapacity()
    {
        // Approximate FIFO eviction. Re-inserting a previously evicted key leaves a stale queue entry,
        // which can evict one extra live entry — harmless for a cache (it simply re-downloads later).
        while (InsertionOrder.Count > MaxEntries && InsertionOrder.TryDequeue(out var oldest))
            Cache.TryRemove(oldest, out _);
    }
}
