using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Umbra.Core;

namespace Umbra.App;

/// <summary>A rounded search field with an icon, placeholder text and a clear button.</summary>
public sealed class SearchBox : UserControl
{
    readonly TextBox _box;
    readonly TextBlock _placeholder;
    readonly TextBlock _icon;
    readonly Button _clear;

    public SearchBox()
    {
        var muted = (Brush)Application.Current.Resources["MutedBrush"];
        var icons = (FontFamily)Application.Current.Resources["IconFont"];
        _box = new TextBox { Padding = new Thickness(40, 0, 38, 0) };
        _placeholder = new TextBlock
        {
            Margin = new Thickness(42, 0, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Foreground = muted,
            FontSize = 14.5,
        };
        _icon = new TextBlock
        {
            Text = "",
            FontFamily = icons,
            FontSize = 14,
            Margin = new Thickness(15, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Foreground = muted,
        };
        _clear = new Button
        {
            Content = "",
            Style = (Style)Application.Current.Resources["IconButton"],
            Width = 28,
            Height = 28,
            FontSize = 10,
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = muted,
            Focusable = false,
            Visibility = Visibility.Collapsed,
            ToolTip = L.T("Borrar"),
        };
        _clear.Click += (_, _) =>
        {
            _box.Clear();
            FocusBox();
        };

        var grid = new Grid();
        grid.Children.Add(_box);
        grid.Children.Add(_placeholder);
        grid.Children.Add(_icon);
        grid.Children.Add(_clear);
        Content = grid;
        Focusable = false;

        _box.TextChanged += (_, _) =>
        {
            var empty = _box.Text.Length == 0;
            _placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            _clear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            TextChanged?.Invoke(this, EventArgs.Empty);
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _box.Text.Length > 0)
            {
                _box.Clear();
                e.Handled = true;
            }
        };
    }

    public event EventHandler? TextChanged;

    public string Text
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    public string Placeholder
    {
        get => _placeholder.Text;
        set => _placeholder.Text = L.T(value);
    }

    /// <summary>Bigger field for the search screen.</summary>
    public bool Large
    {
        get => _box.MinHeight > 44;
        set
        {
            _box.MinHeight = value ? 54 : 44;
            _box.FontSize = _placeholder.FontSize = value ? 17 : 14.5;
            _box.Padding = new Thickness(value ? 50 : 40, 0, 38, 0);
            _placeholder.Margin = new Thickness(value ? 52 : 42, 0, 0, 1);
            _icon.Margin = new Thickness(value ? 20 : 15, 0, 0, 0);
            _icon.FontSize = value ? 16 : 14;
        }
    }

    public void FocusBox()
    {
        _box.Focus();
        Keyboard.Focus(_box);
    }
}
