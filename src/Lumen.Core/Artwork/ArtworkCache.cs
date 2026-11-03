using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Lumen.Core.Diagnostics;

namespace Lumen.Core.Artwork;

/// <summary>Downloads one artwork file. Returns null for "not available" (404, too large, offline).</summary>
public interface IArtworkFetcher
{
    Task<byte[]?> FetchAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>
/// Disk cache for remote artwork (e.g. Steam CDN images). Local files pass straight through.
///
/// - File names are a SHA-256 of the URI, so they are stable and safe.
/// - Writes are atomic (temp file + move); a half-written file is never served.
/// - Concurrent requests for the same URI share one download.
/// - Failed URIs are remembered for the session so a missing image is not re-requested on every
///   focus change.
/// - <see cref="Trim"/> keeps the folder under a size budget, evicting least recently used files.
/// </summary>
public sealed class ArtworkCache
{
    private readonly string _directory;
    private readonly IArtworkFetcher _fetcher;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.Ordinal);

    public ArtworkCache(string directory, IArtworkFetcher fetcher, long maxBytes = 400L * 1024 * 1024)
    {
        _directory = directory;
        _fetcher = fetcher;
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }

    public static bool IsRemote(string? pathOrUri) =>
        pathOrUri is not null &&
        (pathOrUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         pathOrUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    /// <summary>Cache file name for a URI: 32 hex chars + the URI's image extension.</summary>
    public static string FileNameFor(string uri)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(uri));
        var hex = Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        var ext = ".img";
        try
        {
            var e = Path.GetExtension(new Uri(uri).AbsolutePath).ToLowerInvariant();
            if (e is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp") ext = e;
        }
        catch (UriFormatException) { /* keep .img */ }
        return hex + ext;
    }

    /// <summary>
    /// Synchronous fast path: a local file that exists, or a remote URI already cached on disk.
    /// Returns null when the image would need a download (or does not exist).
    /// </summary>
    public string? TryGetLocal(string? pathOrUri)
    {
        if (string.IsNullOrWhiteSpace(pathOrUri)) return null;
        if (!IsRemote(pathOrUri)) return File.Exists(pathOrUri) ? pathOrUri : null;

        var cached = Path.Combine(_directory, FileNameFor(pathOrUri));
        if (!File.Exists(cached)) return null;
        Touch(cached);
        return cached;
    }

    /// <summary>Resolves to a local file path, downloading remote images once. Null when unavailable.</summary>
    public Task<string?> ResolveAsync(string? pathOrUri, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pathOrUri)) return Task.FromResult<string?>(null);
        var local = TryGetLocal(pathOrUri);
        if (local is not null || !IsRemote(pathOrUri)) return Task.FromResult(local);
        if (_failed.ContainsKey(pathOrUri)) return Task.FromResult<string?>(null);

        var lazy = _inflight.GetOrAdd(pathOrUri, uri => new Lazy<Task<string?>>(() => DownloadAsync(uri, cancellationToken)));
        return lazy.Value;
    }

    private async Task<string?> DownloadAsync(string uri, CancellationToken ct)
    {
        try
        {
            var bytes = await _fetcher.FetchAsync(new Uri(uri), ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                _failed.TryAdd(uri, 0);
                return null;
            }

            Directory.CreateDirectory(_directory);
            var target = Path.Combine(_directory, FileNameFor(uri));
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (OperationCanceledException)
        {
            return null; // not a failure - may be retried later
        }
        catch (Exception ex)
        {
            Log.Debug($"Artwork download failed for {uri}: {ex.Message}");
            _failed.TryAdd(uri, 0);
            return null;
        }
        finally
        {
            _inflight.TryRemove(uri, out _);
        }
    }

    /// <summary>Deletes least-recently-used files until the cache is below <see cref="MaxBytes"/>. Returns files removed.</summary>
    public int Trim()
    {
        if (!Directory.Exists(_directory)) return 0;
        var files = new DirectoryInfo(_directory).GetFiles()
            .Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc)
            .ToList();
        var total = files.Sum(f => f.Length);
        var removed = 0;
        foreach (var f in files)
        {
            if (total <= MaxBytes) break;
            try
            {
                total -= f.Length;
                f.Delete();
                removed++;
            }
            catch { /* in use - skip */ }
        }

        // Leftover temp files from an interrupted download.
        foreach (var tmp in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
        if (removed > 0) Log.Info($"Artwork cache trimmed: {removed} files removed.");
        return removed;
    }

    private static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { /* best effort */ }
    }
}
