using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Umbra.Core;

namespace Umbra.App;

/// <summary>
/// In-app modal dialog (message, confirmation or PIN). It is laid over the main window with a dimmed backdrop, so it
/// reads as part of the app instead of a separate system window.
/// </summary>
public partial class Sheet : Window
{
    bool _result;
    bool _closing;

    Sheet(string glyph, string title, string message, string ok, string? cancel, bool pin)
    {
        InitializeComponent();
        Glyph.Text = glyph;
        TitleText.Text = L.T(title);
        MessageText.Text = L.T(message);
        MessageText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Content = L.T(ok);
        if (cancel is null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            CancelColumn.Width = new GridLength(0);
            OkButton.Margin = new Thickness(0);
        }
        else
        {
            CancelButton.Content = L.T(cancel);
        }
        if (pin) PinBox.Visibility = Visibility.Visible;

        Loaded += (_, _) =>
        {
            Motion.Fade(Backdrop, 1, 180);
            Motion.Pop(CardHost, 0.93, 340);
            if (pin) PinBox.Focus();
            else OkButton.Focus();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Finish(false);
                e.Handled = true;
            }
        };
    }

    void Ok_Click(object sender, RoutedEventArgs e) => Finish(true);

    void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false);

    void Finish(bool result)
    {
        if (_closing) return;
        _closing = true;
        _result = result;
        Motion.Fade(Backdrop, 0, 130, Close);
    }

    bool Run()
    {
        var owner = Application.Current.MainWindow;
        if (owner is { IsLoaded: true, Content: FrameworkElement root } && PresentationSource.FromVisual(owner) is { CompositionTarget: { } target })
        {
            Owner = owner;
            var origin = target.TransformFromDevice.Transform(root.PointToScreen(new Point(0, 0)));
            Left = origin.X;
            Top = origin.Y;
            Width = root.ActualWidth;
            Height = root.ActualHeight;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 620;
            Height = 460;
        }
        ShowDialog();
        return _result;
    }

    // ----- Entry points -----

    public static void Info(string title, string message = "", string glyph = "")
    {
        if (App.Quiet)
        {
            Log.Info($"[diálogo] {title}: {message}");
            return;
        }
        new Sheet(glyph, title, message, "Entendido", null, pin: false).Run();
    }

    public static void Warn(string title, string message = "") => Info(title, message, "");

    public static bool Confirm(string title, string message, string ok = "Aceptar", string cancel = "Cancelar", string glyph = "")
    {
        if (App.Quiet) return false;
        return new Sheet(glyph, title, message, ok, cancel, pin: false).Run();
    }

    /// <summary>Asks for a PIN; null when cancelled.</summary>
    public static string? AskPin(string title, string message = "")
    {
        if (App.Quiet) return null;
        var sheet = new Sheet("", title, message, "Aceptar", "Cancelar", pin: true);
        return sheet.Run() ? sheet.PinBox.Password : null;
    }
}
