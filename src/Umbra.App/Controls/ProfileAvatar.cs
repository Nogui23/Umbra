using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umbra.Core;

namespace Umbra.App;

/// <summary>The circle of a profile: its photo, or its initial on its colour.</summary>
public sealed class ProfileAvatar : Border
{
    readonly TextBlock _initial = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
    };

    public ProfileAvatar()
    {
        Child = _initial;
        _initial.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        Render();
    }

    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(
        nameof(Profile), typeof(Profile), typeof(ProfileAvatar), new PropertyMetadata(null, (d, _) => ((ProfileAvatar)d).Render()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(ProfileAvatar), new PropertyMetadata(40.0, (d, _) => ((ProfileAvatar)d).Render()));

    public Profile? Profile
    {
        get => (Profile?)GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>A photo chosen but not saved yet (in the profile editor); null shows the profile's own.</summary>
    public ImageSource? Preview
    {
        get => _preview;
        set { _preview = value; Render(); }
    }
    ImageSource? _preview;

    /// <summary>Shown when there is no picture: the name being typed, the colour being chosen.</summary>
    public string? NameOverride { get; set; }
    public string? ColorOverride { get; set; }

    public void Render()
    {
        Width = Height = Size;
        CornerRadius = new CornerRadius(Size / 2);
        _initial.FontSize = Math.Max(10, Size * 0.42);
        var profile = Profile;
        var photo = _preview ?? (profile is { HasPhoto: true } ? LoadPhoto(profile) : null);
        if (photo is not null)
        {
            Background = new ImageBrush(photo) { Stretch = Stretch.UniformToFill };
            _initial.Text = "";
            return;
        }
        Background = Brush(ColorOverride ?? profile?.Color ?? ProfileStore.Colors[0]);
        _initial.Text = InitialConverter.Of(NameOverride ?? profile?.Name ?? "");
    }

    static Brush Brush(string hex)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Gray;
        }
    }

    /// <summary>Reads the photo from disk every time (it may have just been replaced).</summary>
    public static BitmapSource? LoadPhoto(Profile profile)
    {
        var path = ProfileStore.PhotoPath(profile);
        if (!File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
            // Saved at 256: a file that claims to be far bigger is never decoded at full size.
            bitmap.DecodePixelWidth = 256;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo leer la foto del perfil", ex);
            return null;
        }
    }

    /// <summary>
    /// Turns any picture into the photo of a profile: the centre square, 256×256, as PNG. Throws a readable error
    /// when the file is not a picture.
    /// </summary>
    public static (byte[] Png, BitmapSource Image) MakePhoto(string file)
    {
        try
        {
            if (new FileInfo(file).Length > 40L << 20) throw new SourceException("La imagen es demasiado grande (más de 40 MB).");
            var source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new Uri(file);
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            source.DecodePixelWidth = 768; // enough for a 256 square, and quick with photos from a phone
            source.EndInit();
            var side = Math.Min(source.PixelWidth, source.PixelHeight);
            var square = new CroppedBitmap(source, new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
            var scale = 256.0 / side;
            BitmapSource result = new TransformedBitmap(square, new ScaleTransform(scale, scale));
            result = new FormatConvertedBitmap(result, PixelFormats.Bgra32, null, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            result.Freeze();
            return (stream.ToArray(), result);
        }
        catch (SourceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo usar la imagen como foto de perfil", ex);
            throw new SourceException("No se ha podido abrir la imagen. Prueba con una JPG o PNG.");
        }
    }
}
