using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Umbra.App;

/// <summary>An image that fades in when a remote picture finishes downloading instead of popping in.</summary>
public sealed class FadeImage : Image
{
    BitmapSource? _pending;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != SourceProperty) return;
        Detach();
        BeginAnimation(OpacityProperty, null);
        if (e.NewValue is BitmapSource { IsDownloading: true } bitmap)
        {
            Opacity = 0;
            _pending = bitmap;
            bitmap.DownloadCompleted += OnDownloaded;
        }
        else
        {
            Opacity = 1;
        }
    }

    void OnDownloaded(object? sender, EventArgs e)
    {
        Detach();
        Motion.Fade(this, 1, 320);
    }

    void Detach()
    {
        if (_pending is null) return;
        _pending.DownloadCompleted -= OnDownloaded;
        _pending = null;
    }
}

/// <summary>A border whose ends are always full half-circles, whatever its size.</summary>
public sealed class Pill : Border
{
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        CornerRadius = new CornerRadius(Math.Min(sizeInfo.NewSize.Width, sizeInfo.NewSize.Height) / 2);
    }
}

/// <summary>Sizes its child to a fixed height/width ratio of the available width and clips it to rounded corners.</summary>
public sealed class AspectBox : Decorator
{
    /// <summary>Height divided by width (1.5 for posters, 0.5625 for 16:9).</summary>
    public double Ratio { get; set; } = 1.5;
    public double CornerRadius { get; set; }

    protected override Size MeasureOverride(Size constraint)
    {
        var width = double.IsInfinity(constraint.Width) ? 160 : constraint.Width;
        var size = new Size(width, Math.Round(width * Ratio));
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        var clip = new RectangleGeometry(new Rect(arrangeSize), CornerRadius, CornerRadius);
        clip.Freeze();
        Clip = clip;
        return arrangeSize;
    }
}

/// <summary>Activity indicator: a ring with a rotating arc.</summary>
public sealed class Spinner : FrameworkElement
{
    static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));

    readonly RotateTransform _rotation = new();

    public Brush Stroke { get; set; } = Brushes.White;

    public Spinner()
    {
        Width = Height = 30;
        IsHitTestVisible = false;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _rotation;
        IsVisibleChanged += (_, _) => Update();
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    void Update()
    {
        if (IsVisible && IsLoaded)
        {
            _rotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(850)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var thickness = Math.Max(2, size * 0.1);
        var radius = (size - thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(TrackBrush, thickness), center, radius, radius);

        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius, center.Y);
        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
    }
}
