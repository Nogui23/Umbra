using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Umbra.Core;

namespace Umbra.App;

public sealed record PosterRow(IReadOnlyList<PosterItem> Items, int Columns);

/// <summary>
/// Virtualised, responsive poster grid: the number of columns follows the width and the posters stretch to fill it.
/// Items are grouped into rows shown in a virtualising list, so catalogues with tens of thousands of titles stay fast.
/// </summary>
public sealed class PosterGrid : ListBox
{
    const double MinTileWidth = 176;
    const double ScrollGutter = 14;
    List<PosterItem> _items = new();
    int _columns;

    public PosterGrid()
    {
        SetResourceReference(StyleProperty, typeof(ListBox));
        Focusable = false;

        var container = new Style(typeof(ListBoxItem));
        container.Setters.Add(new Setter(FocusableProperty, false));
        container.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(ListBoxItem))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)),
        }));
        ItemContainerStyle = container;

        var row = new FrameworkElementFactory(typeof(ItemsControl));
        row.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(PosterRow.Items)));
        row.SetValue(ItemsControl.ItemTemplateProperty, Application.Current.FindResource("PosterTemplate"));
        row.SetValue(MarginProperty, new Thickness(0, 0, ScrollGutter, 0));
        row.SetValue(FocusableProperty, false);
        var panel = new FrameworkElementFactory(typeof(UniformGrid));
        panel.SetValue(UniformGrid.RowsProperty, 1);
        panel.SetBinding(UniformGrid.ColumnsProperty, new Binding(nameof(PosterRow.Columns)));
        row.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(panel));
        ItemTemplate = new DataTemplate { VisualTree = row };

        SizeChanged += (_, _) =>
        {
            if (Columns() != _columns) Rebuild(keepPosition: true);
        };
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnTileClick));
    }

    public event Action<PosterItem>? ItemClicked;

    public void SetItems(IEnumerable<PosterItem> items)
    {
        _items = items.ToList();
        Rebuild(keepPosition: false);
    }

    int Columns() => Math.Max(2, (int)((ActualWidth - ScrollGutter) / MinTileWidth));

    void Rebuild(bool keepPosition)
    {
        _columns = Columns();
        var rows = new List<PosterRow>((_items.Count + _columns - 1) / _columns);
        for (var i = 0; i < _items.Count; i += _columns)
            rows.Add(new PosterRow(_items.GetRange(i, Math.Min(_columns, _items.Count - i)), _columns));
        ItemsSource = rows;
        if (!keepPosition && rows.Count > 0) ScrollIntoView(rows[0]);
    }

    void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: PosterItem item }) ItemClicked?.Invoke(item);
    }
}
