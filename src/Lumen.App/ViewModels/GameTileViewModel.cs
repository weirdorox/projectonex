using System.ComponentModel;
using System.Runtime.CompilerServices;
using Lumen.App.Services;
using Lumen.Core.Artwork;
using Lumen.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Lumen.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>
/// One game tile. Instances are cached per game id by <see cref="Services.LibraryStore"/> and shared
/// between pages, so artwork is decoded once. Must be created on the UI thread.
/// </summary>
public sealed class GameTileViewModel : ObservableObject
{
    private const int DecodeWidth = 360; // ~2x tile width: sharp at 200% scaling, small in memory

    private GameEntry _game;
    private ImageSource? _cover;
    private string? _coverKey;

    public GameTileViewModel(GameEntry game)
    {
        _game = game;
        PlaceholderBrush = MakePlaceholderBrush(game.Title);
        RefreshCover();
    }

    public GameEntry Game => _game;
    public string Id => _game.Id;
    public string Title => _game.Title;
    public string SourceLabel => _game.Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic Games",
        GameSource.Gog => "GOG",
        GameSource.Xbox => "Xbox",
        GameSource.Folder => "Folder",
        _ => "Manual",
    };

    public string Subtitle
    {
        get
        {
            var parts = new List<string> { SourceLabel };
            if (_game.LastPlayed is { } lp) parts.Add("Played " + Humanize(lp));
            if (_game.PlaytimeSeconds >= 60) parts.Add($"{TimeSpan.FromSeconds(_game.PlaytimeSeconds).TotalHours:0.#} h");
            return string.Join("  ·  ", parts);
        }
    }

    public ImageSource? Cover => _cover;
    public Visibility CoverVisibility => _cover is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PlaceholderVisibility => _cover is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FavoriteVisibility => _game.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
    public Brush PlaceholderBrush { get; }
    public string Initials => MakeInitials(_game.Title);

    /// <summary>Wide artwork for the home backdrop (falls back to the cover). May be a remote URI.</summary>
    public string? HeroPath => _game.Artwork.Hero ?? _game.EffectiveCover;

    /// <summary>Transparent title logo (local files only, e.g. Steam's logo.png).</summary>
    public string? LogoPath =>
        _game.Artwork.Logo is { } logo && !ArtworkCache.IsRemote(logo) && File.Exists(logo) ? logo : null;

    public void Update(GameEntry game)
    {
        if (game == _game) return;
        _game = game;
        RefreshCover();
        OnPropertyChanged(string.Empty); // refresh all bindings
    }

    private void RefreshCover()
    {
        var key = _game.EffectiveCover;
        if (key == _coverKey) return;
        _coverKey = key;

        // Local file or already-cached remote art: show immediately.
        var service = ArtworkService.Instance;
        var local = service?.Cache.TryGetLocal(key) ?? (ArtworkCache.IsRemote(key) ? null : key);
        _cover = CreateBitmap(local, DecodeWidth);

        // Remote art not cached yet: keep the placeholder, download once in the background.
        if (_cover is null && service is not null && ArtworkCache.IsRemote(key))
            _ = LoadRemoteCoverAsync(service, key!);
    }

    private async Task LoadRemoteCoverAsync(ArtworkService service, string key)
    {
        var path = await service.Cache.ResolveAsync(key).ConfigureAwait(false);
        if (path is null) return;
        service.Ui.TryEnqueue(() =>
        {
            if (_coverKey != key) return; // game changed meanwhile
            _cover = CreateBitmap(path, DecodeWidth);
            OnPropertyChanged(nameof(Cover));
            OnPropertyChanged(nameof(CoverVisibility));
            OnPropertyChanged(nameof(PlaceholderVisibility));
        });
    }

    public static BitmapImage? CreateBitmap(string? pathOrUri, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(pathOrUri)) return null;
        try
        {
            Uri uri;
            if (pathOrUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                pathOrUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                pathOrUri.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase))
            {
                uri = new Uri(pathOrUri);
            }
            else
            {
                if (!File.Exists(pathOrUri)) return null;
                uri = new Uri(Path.GetFullPath(pathOrUri));
            }
            return new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical, UriSource = uri };
        }
        catch
        {
            return null;
        }
    }

    private static string MakeInitials(string title)
    {
        var words = title.Split(new[] { ' ', ':', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0]))
            .Take(2)
            .Select(w => char.ToUpperInvariant(w[0]));
        var s = new string(words.ToArray());
        return s.Length == 0 ? "?" : s;
    }

    /// <summary>Deterministic two-tone gradient per title so placeholder tiles are distinguishable.</summary>
    private static Brush MakePlaceholderBrush(string title)
    {
        var hash = 17;
        foreach (var c in title) hash = unchecked(hash * 31 + c);
        var hue = (uint)hash % 360;
        var a = FromHsv(hue, 0.55, 0.42);
        var b = FromHsv((hue + 40) % 360, 0.65, 0.18);
        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = a, Offset = 0 },
                new GradientStop { Color = b, Offset = 1 },
            },
        };
    }

    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60d => (c, x, 0d),
            < 120d => (x, c, 0d),
            < 180d => (0d, c, x),
            < 240d => (0d, x, c),
            < 300d => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return ColorHelper.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    internal static string Humanize(DateTimeOffset when)
    {
        var d = DateTimeOffset.Now - when;
        if (d < TimeSpan.FromMinutes(2)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} h ago";
        if (d < TimeSpan.FromDays(2)) return "yesterday";
        if (d < TimeSpan.FromDays(30)) return $"{(int)d.TotalDays} days ago";
        return when.LocalDateTime.ToString("d MMM yyyy");
    }
}
