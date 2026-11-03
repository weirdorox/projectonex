using System.Collections.Concurrent;
using System.Net;
using Lumen.Core;
using Lumen.Core.Artwork;
using Lumen.Core.Diagnostics;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Lumen.App.Services;

/// <summary>
/// App-wide artwork access: disk cache for remote images, bounded parallel downloads, and accent
/// colour extraction from a 24×24 thumbnail decoded off the UI thread.
/// </summary>
public sealed class ArtworkService
{
    private const int ThumbSize = 24;
    private readonly ConcurrentDictionary<string, RgbColor> _accents = new(StringComparer.OrdinalIgnoreCase);

    private ArtworkService(ArtworkCache cache, DispatcherQueue ui)
    {
        Cache = cache;
        Ui = ui;
    }

    public static ArtworkService? Instance { get; private set; }

    public ArtworkCache Cache { get; }
    public DispatcherQueue Ui { get; }

    public static ArtworkService Initialize(AppPaths paths, DispatcherQueue ui)
    {
        Instance = new ArtworkService(new ArtworkCache(paths.ArtworkCache, new HttpArtworkFetcher()), ui);
        _ = Task.Run(() =>
        {
            try { Instance.Cache.Trim(); }
            catch (Exception ex) { Log.Warn("Artwork cache trim failed.", ex); }
        });
        return Instance;
    }

    /// <summary>Dominant colour of a local image, cached per path. Null if it cannot be decoded.</summary>
    public async Task<RgbColor?> GetAccentAsync(string? localPath)
    {
        if (string.IsNullOrEmpty(localPath) || ArtworkCache.IsRemote(localPath)) return null;
        if (_accents.TryGetValue(localPath, out var known)) return known;
        try
        {
            var color = await Task.Run(async () =>
            {
                var file = await StorageFile.GetFileFromPathAsync(localPath);
                using var stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var transform = new BitmapTransform
                {
                    ScaledWidth = ThumbSize,
                    ScaledHeight = ThumbSize,
                    InterpolationMode = BitmapInterpolationMode.Linear,
                };
                var pixels = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                return ArtworkPalette.Dominant(pixels.DetachPixelData(), ThumbSize, ThumbSize);
            });
            _accents[localPath] = color;
            return color;
        }
        catch (Exception ex)
        {
            Log.Debug($"Accent extraction failed for {localPath}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>HTTP fetcher with a small parallelism cap and size limit, so a big library doesn't flood the network.</summary>
public sealed class HttpArtworkFetcher : IArtworkFetcher
{
    private const long MaxBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(4);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Lumen/0.2 (handheld game launcher)");
        return c;
    }

    public async Task<byte[]?> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is > MaxBytes) return null;
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !media.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length > MaxBytes ? null : bytes;
        }
        finally
        {
            Gate.Release();
        }
    }
}
