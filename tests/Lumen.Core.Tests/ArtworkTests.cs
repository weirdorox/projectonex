using Lumen.Core.Artwork;

namespace Lumen.Core.Tests;

public class ArtworkCacheTests
{
    private sealed class FakeFetcher : IArtworkFetcher
    {
        public int Calls;
        public Func<Uri, byte[]?> Respond { get; set; } = _ => [1, 2, 3];
        public TaskCompletionSource? Gate { get; set; }

        public async Task<byte[]?> FetchAsync(Uri uri, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Gate is not null) await Gate.Task;
            return Respond(uri);
        }
    }

    private const string Url = "https://cdn.example.com/steam/apps/620/library_600x900.jpg";

    [Fact]
    public async Task Downloads_once_then_serves_from_disk()
    {
        using var dir = new TempDir();
        var fetcher = new FakeFetcher();
        var cache = new ArtworkCache(dir.Path, fetcher);

        Assert.Null(cache.TryGetLocal(Url));
        var path = await cache.ResolveAsync(Url);
        Assert.NotNull(path);
        Assert.EndsWith(".jpg", path);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path!));

        Assert.Equal(path, cache.TryGetLocal(Url));
        Assert.Equal(path, await new ArtworkCache(dir.Path, fetcher).ResolveAsync(Url)); // survives restart
        Assert.Equal(1, fetcher.Calls);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_download()
    {
        using var dir = new TempDir();
        var fetcher = new FakeFetcher { Gate = new TaskCompletionSource() };
        var cache = new ArtworkCache(dir.Path, fetcher);

        var a = cache.ResolveAsync(Url);
        var b = cache.ResolveAsync(Url);
        fetcher.Gate.SetResult();
        var results = await Task.WhenAll(a, b);

        Assert.Equal(1, fetcher.Calls);
        Assert.Equal(results[0], results[1]);
    }

    [Fact]
    public async Task Failures_are_not_retried_in_the_same_session()
    {
        using var dir = new TempDir();
        var fetcher = new FakeFetcher { Respond = _ => null };
        var cache = new ArtworkCache(dir.Path, fetcher);

        Assert.Null(await cache.ResolveAsync(Url));
        Assert.Null(await cache.ResolveAsync(Url));
        Assert.Equal(1, fetcher.Calls);
    }

    [Fact]
    public async Task Exceptions_become_null()
    {
        using var dir = new TempDir();
        var cache = new ArtworkCache(dir.Path, new FakeFetcher { Respond = _ => throw new HttpRequestException("offline") });
        Assert.Null(await cache.ResolveAsync(Url));
    }

    [Fact]
    public async Task Local_paths_pass_through()
    {
        using var dir = new TempDir();
        var file = dir.Write("cover.png", "x");
        var fetcher = new FakeFetcher();
        var cache = new ArtworkCache(Path.Combine(dir.Path, "cache"), fetcher);

        Assert.Equal(file, await cache.ResolveAsync(file));
        Assert.Null(await cache.ResolveAsync(Path.Combine(dir.Path, "missing.png")));
        Assert.Null(await cache.ResolveAsync(null));
        Assert.Equal(0, fetcher.Calls);
    }

    [Fact]
    public void File_names_are_stable_and_keep_extension()
    {
        Assert.Equal(ArtworkCache.FileNameFor(Url), ArtworkCache.FileNameFor(Url));
        Assert.NotEqual(ArtworkCache.FileNameFor(Url), ArtworkCache.FileNameFor(Url + "?v=2"));
        Assert.EndsWith(".png", ArtworkCache.FileNameFor("https://x/y/logo.png"));
        Assert.EndsWith(".img", ArtworkCache.FileNameFor("https://x/y/z"));
    }

    [Fact]
    public void Trim_evicts_oldest_until_under_budget()
    {
        using var dir = new TempDir();
        var old = dir.WriteBytes("a.jpg", 600);
        var mid = dir.WriteBytes("b.jpg", 600);
        var newest = dir.WriteBytes("c.jpg", 600);
        dir.Write("partial.jpg.tmp", "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        File.SetLastAccessTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(mid, DateTime.UtcNow.AddDays(-2));
        File.SetLastAccessTimeUtc(mid, DateTime.UtcNow.AddDays(-2));

        var cache = new ArtworkCache(dir.Path, new FakeFetcher(), maxBytes: 1000);
        Assert.Equal(2, cache.Trim());
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(mid));
        Assert.True(File.Exists(newest));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }
}

public class ArtworkPaletteTests
{
    private static byte[] Image(int w, int h, Func<int, int, RgbColor> pixel)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = pixel(x, y);
            var i = (y * w + x) * 4;
            data[i] = c.B; data[i + 1] = c.G; data[i + 2] = c.R; data[i + 3] = 255;
        }
        return data;
    }

    [Fact]
    public void Solid_colour_is_returned()
    {
        var c = ArtworkPalette.Dominant(Image(8, 8, (_, _) => new RgbColor(200, 40, 40)), 8, 8);
        Assert.InRange(c.R, 195, 205);
        Assert.InRange(c.G, 35, 45);
    }

    [Fact]
    public void Vivid_accent_beats_large_grey_area()
    {
        // 80% grey, 20% orange: the orange is what a designer would pick.
        var c = ArtworkPalette.Dominant(Image(10, 10, (x, _) => x < 8 ? new RgbColor(120, 120, 120) : new RgbColor(255, 140, 60)), 10, 10);
        Assert.True(c.R > 200 && c.G is > 100 and < 180 && c.B < 100, $"got {c}");
    }

    [Fact]
    public void Greyscale_falls_back_to_average()
    {
        var c = ArtworkPalette.Dominant(Image(4, 4, (x, _) => x < 2 ? new RgbColor(100, 100, 100) : new RgbColor(200, 200, 200)), 4, 4);
        Assert.Equal(new RgbColor(150, 150, 150), c);
    }

    [Fact]
    public void Empty_or_transparent_input_returns_ink()
    {
        Assert.Equal(RgbColor.Ink, ArtworkPalette.Dominant([], 0, 0));
        var transparent = new byte[16]; // alpha 0
        Assert.Equal(RgbColor.Ink, ArtworkPalette.Dominant(transparent, 2, 2));
    }

    [Fact]
    public void Backdrop_colour_is_dark_and_keeps_hue()
    {
        var bright = new RgbColor(255, 200, 0);
        var tint = bright.ForBackdrop();
        var (h1, _, _) = ArtworkPalette.ToHsv(bright);
        var (h2, _, v2) = ArtworkPalette.ToHsv(tint);
        Assert.InRange(v2, 0.21, 0.43);
        Assert.InRange(Math.Abs(h1 - h2), 0, 3);
    }

    [Fact]
    public void Hsv_round_trips()
    {
        foreach (var c in new[] { new RgbColor(255, 0, 0), new RgbColor(12, 200, 99), new RgbColor(80, 80, 200) })
        {
            var (h, s, v) = ArtworkPalette.ToHsv(c);
            var back = ArtworkPalette.FromHsv(h, s, v);
            Assert.InRange(Math.Abs(back.R - c.R), 0, 1);
            Assert.InRange(Math.Abs(back.G - c.G), 0, 1);
            Assert.InRange(Math.Abs(back.B - c.B), 0, 1);
        }
    }
}
