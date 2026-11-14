using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Umbra.Core;

namespace Umbra.App;

/// <summary>Segmented control with a thumb that slides to the selected option.</summary>
public sealed class Segmented : Border
{
    readonly Border _thumb;
    readonly TranslateTransform _thumbMove = new();
    readonly UniformGrid _host = new() { Rows = 1 };
    int _selected;
    bool _placed;

    public Segmented()
    {
        Height = 42;
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(3);
        SetResourceReference(BackgroundProperty, "Surface1");
        _thumb = new Border
        {
            CornerRadius = new CornerRadius(9),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = _thumbMove,
        };
        _thumb.SetResourceReference(BackgroundProperty, "Surface3");
        var grid = new Grid();
        grid.Children.Add(_thumb);
        grid.Children.Add(_host);
        Child = grid;
        _host.SizeChanged += (_, _) => Place(animate: false);
    }

    public event EventHandler? SelectionChanged;

    /// <summary>Option labels separated by «|».</summary>
    public string Items
    {
        set
        {
            _host.Children.Clear();
            var labels = value.Split('|');
            for (var i = 0; i < labels.Length; i++)
            {
                var index = i;
                var option = new RadioButton
                {
                    Content = L.T(labels[i]),
                    Style = (Style)Application.Current.Resources["SegmentItem"],
                    IsChecked = i == _selected,
                };
                option.Checked += (_, _) => Select(index, raise: true);
                _host.Children.Add(option);
            }
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set => Select(value, raise: false);
    }

    void Select(int index, bool raise)
    {
        if (index < 0 || index >= _host.Children.Count) return;
        var changed = index != _selected;
        _selected = index;
        if (_host.Children[index] is RadioButton { IsChecked: not true } option) option.IsChecked = true;
        Place(animate: true);
        if (changed && raise) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    void Place(bool animate)
    {
        var count = _host.Children.Count;
        if (count == 0 || _host.ActualWidth <= 0) return;
        var width = _host.ActualWidth / count;
        var x = width * _selected;
        _thumb.Width = width;
        if (animate && _placed && Motion.Enabled)
        {
            _thumbMove.BeginAnimation(TranslateTransform.XProperty, Motion.Tween(_thumbMove.X, x, 320, 0, Motion.OutStrong));
        }
        else
        {
            _thumbMove.BeginAnimation(TranslateTransform.XProperty, null);
            _thumbMove.X = x;
        }
        _placed = true;
    }
}

/// <summary>Pull-down button: shows the current option and opens a menu with the rest.</summary>
public sealed class Picker : Button
{
    readonly List<(string Label, string Tag)> _options = new();
    readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    int _selected = -1;

    public Picker()
    {
        SetResourceReference(StyleProperty, "SubtleButton");
        Padding = new Thickness(16, 9, 13, 9);
        FontSize = 13.5;
        var chevron = new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
            FontSize = 9,
            Margin = new Thickness(10, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["MutedBrush"],
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_label);
        row.Children.Add(chevron);
        Content = row;
    }

    /// <summary>Raised when the user picks another option.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>«Label=tag» pairs separated by «|».</summary>
    public string Options
    {
        set => SetOptions(value.Split('|').Select(pair =>
        {
            var cut = pair.LastIndexOf('=');
            return cut < 0 ? (pair, pair) : (pair[..cut], pair[(cut + 1)..]);
        }));
    }

    public void SetOptions(IEnumerable<(string Label, string Tag)> options)
    {
        _options.Clear();
        _options.AddRange(options.Select(o => (L.T(o.Label), o.Tag)));
        Show(_options.Count > 0 ? 0 : -1);
    }

    public string SelectedTag => _selected >= 0 ? _options[_selected].Tag : "";
    public int SelectedIndex => _selected;

    /// <summary>Selects the option with this tag (or the fallback, or the first one) without raising the event.</summary>
    public void Select(string tag, string? fallback = null)
    {
        var index = _options.FindIndex(o => o.Tag == tag);
        if (index < 0 && fallback is not null) index = _options.FindIndex(o => o.Tag == fallback);
        Show(index < 0 && _options.Count > 0 ? 0 : index);
    }

    void Show(int index)
    {
        _selected = index;
        _label.Text = index >= 0 ? _options[index].Label : "";
    }

    protected override void OnClick()
    {
        base.OnClick();
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Bottom, VerticalOffset = -6, HorizontalOffset = -14 };
        for (var i = 0; i < _options.Count; i++)
        {
            var index = i;
            var item = new MenuItem { Header = _options[i].Label, IsChecked = i == _selected };
            item.Click += (_, _) =>
            {
                if (index == _selected) return;
                Show(index);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
