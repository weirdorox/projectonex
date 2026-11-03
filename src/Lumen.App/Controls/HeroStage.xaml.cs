using System.Numerics;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.Core.Artwork;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Lumen.App.Controls;

/// <summary>
/// Cross-fading backdrop. New artwork is decoded into the hidden layer and only revealed once
/// <see cref="BitmapImage.ImageOpened"/> fires, so there is never a blank or half-loaded frame.
/// The outgoing layer releases its bitmap after the fade to keep memory flat.
/// </summary>
public sealed partial class HeroStage : UserControl
{
    private const int FadeMs = 450;
    private const int DecodeWidth = 1280;

    private Image _front;
    private Image _back;
    private string? _key;
    private int _generation;

    public HeroStage()
    {
        InitializeComponent();
        _front = LayerA;
        _back = LayerB;
        TintLayer.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(700) };
    }

    /// <summary>Shows a local image path (null clears the stage).</summary>
    public void Show(string? localPath)
    {
        if (localPath == _key) return;
        _key = localPath;
        var generation = ++_generation;

        var bitmap = localPath is null ? null : GameTileViewModel.CreateBitmap(localPath, DecodeWidth);
        if (bitmap is null)
        {
            _front.OpacityTransition = Motion.Fade(FadeMs);
            _front.Opacity = 0;
            return;
        }

        var incoming = _back;
        incoming.OpacityTransition = null;
        incoming.Opacity = 0;
        incoming.ScaleTransition = null;
        incoming.Scale = Vector3.One;
        bitmap.ImageOpened += (_, _) =>
        {
            if (generation == _generation) Reveal(incoming, generation);
        };
        incoming.Source = bitmap;
    }

    public void SetTint(RgbColor? color)
    {
        var c = color?.ForBackdrop() ?? RgbColor.Ink;
        TintLayer.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, c.R, c.G, c.B));
    }

    private void Reveal(Image incoming, int generation)
    {
        var outgoing = incoming == _front ? _back : _front;

        incoming.OpacityTransition = Motion.Fade(FadeMs);
        outgoing.OpacityTransition = Motion.Fade(FadeMs);
        incoming.Opacity = 1;
        outgoing.Opacity = 0;
        _front = incoming;
        _back = outgoing;

        // Slow "Ken Burns" drift on the visible layer.
        if (Motion.Enabled)
        {
            incoming.CenterPoint = new Vector3((float)ActualWidth / 2f, (float)ActualHeight / 3f, 0f);
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (generation != _generation) return;
                incoming.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromSeconds(16) };
                incoming.Scale = new Vector3(1.07f, 1.07f, 1f);
            });
        }

        // Release the old bitmap once it has faded out.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(FadeMs + 100);
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            if (outgoing == _back && outgoing.Opacity == 0) outgoing.Source = null;
        };
        timer.Start();
    }
}
