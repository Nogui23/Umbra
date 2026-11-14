using System.ComponentModel;
using System.Windows.Media;

namespace Umbra.App;

/// <summary>
/// Responsive scale of the interface. Screens are designed on a 1600×900 canvas and the whole shell is scaled to the
/// window, so the app looks the same from a 1280×720 laptop to a 4K television; very wide or very tall windows get
/// more canvas instead of more zoom, and <see cref="Compact"/> tells the screens when that canvas is narrow.
/// </summary>
public sealed class Ui : INotifyPropertyChanged
{
    public static Ui I { get; } = new();

    const double DesignWidth = 1600, DesignHeight = 900;
    const double CompactBelow = 1340;

    double _pixelWidth, _pixelHeight;

    public double Scale { get; private set; } = 1;

    /// <summary>Scale as a transform, for LayoutTransform bindings (shell, player controls, menus and tooltips).</summary>
    public Transform Transform { get; private set; } = Transform.Identity;

    /// <summary>Size of the canvas in design units.</summary>
    public double Width { get; private set; } = DesignWidth;
    public double Height { get; private set; } = DesignHeight;

    public bool Compact => Width < CompactBelow;

    /// <summary>Raised after the canvas size, the scale or the compact state changed.</summary>
    public event Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Resize(double pixelWidth, double pixelHeight)
    {
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        Recompute();
    }

    public void Recompute()
    {
        if (_pixelWidth <= 0 || _pixelHeight <= 0) return;
        var zoom = Math.Clamp(AppState.I.Settings.UiZoom, 70, 160) / 100.0;
        var fit = Math.Clamp(Math.Min(_pixelWidth / DesignWidth, _pixelHeight / DesignHeight), 0.8, 1.75);
        var scale = Math.Round(fit * zoom * 50) / 50;
        Width = _pixelWidth / scale;
        Height = _pixelHeight / scale;
        if (Math.Abs(scale - Scale) > 0.0001)
        {
            Scale = scale;
            var transform = new ScaleTransform(scale, scale);
            transform.Freeze();
            Transform = transform;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scale)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Transform)));
        }
        Changed?.Invoke();
    }
}
