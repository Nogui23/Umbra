using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Umbra.Core;

namespace Umbra.App;

/// <summary>Movies or series catalogue: categories on the left, a responsive poster grid on the right.</summary>
public partial class BrowserView : UserControl, IRefreshable
{
    readonly AppState S = AppState.I;
    readonly MediaKind _kind;
    readonly DispatcherTimer _filterTimer;
    readonly List<Category> _virtual = new();
    Category? _category;
    bool _reverting;

    public BrowserView(MediaKind kind)
    {
        InitializeComponent();
        _kind = kind;
        PageTitle.Text = L.T(kind == MediaKind.Movie ? "Películas" : "Series");
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyFilter(); };
        Filter.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
        Sort.SelectionChanged += (_, _) => ApplyFilter();
        Posters.ItemClicked += OpenItem;
        Loaded += (_, _) =>
        {
            Ui.I.Changed += ApplyLayout;
            ApplyLayout();
        };
        Unloaded += (_, _) => Ui.I.Changed -= ApplyLayout;
        BuildCategories();
    }

    void ApplyLayout()
    {
        var compact = Ui.I.Compact;
        CategoryColumn.Width = new GridLength(compact ? 216 : 270);
        Layout.Margin = compact ? new Thickness(26, 10, 14, 0) : new Thickness(42, 10, 28, 0);
        Filter.Width = compact ? 200 : 260;
    }

    IReadOnlyList<PosterItem> AllItems =>
        _kind == MediaKind.Movie ? S.Library!.Movies : S.Library!.Series;

    void BuildCategories()
    {
        var lib = S.Library!;
        var isMovie = _kind == MediaKind.Movie;
        _virtual.Add(new Category { Id = "@all", Name = L.T(isMovie ? "Todas las películas" : "Todas las series"), Kind = _kind, Icon = "" });
        _virtual.Add(new Category { Id = "@fav", Name = L.T("Favoritas"), Kind = _kind, Icon = "" });
        if (isMovie) _virtual.Add(new Category { Id = "@resume", Name = L.T("Seguir viendo"), Kind = _kind, Icon = "" });
        _virtual.Add(new Category { Id = "@new", Name = L.T(isMovie ? "Añadidas recientemente" : "Actualizadas recientemente"), Kind = _kind, Icon = "" });
        UpdateVirtualCounts();
        Categories.ItemsSource = _virtual.Concat(isMovie ? lib.MovieCategories : lib.SeriesCategories).ToList();
        Categories.SelectedIndex = 0;
    }

    void UpdateVirtualCounts()
    {
        foreach (var c in _virtual) c.Count = ItemsFor(c).Count();
    }

    public void Refresh()
    {
        UpdateVirtualCounts();
        if (_category?.Id is "@fav" or "@resume") ApplyFilter();
    }

    IEnumerable<PosterItem> ItemsFor(Category category)
    {
        var visible = AllItems.Where(i => !S.IsHidden(_kind, i.CategoryId));
        return category.Id switch
        {
            "@all" => visible,
            "@fav" => visible.Where(i => i.IsFavorite),
            "@resume" => visible.Where(i => S.ResumeOf(AppState.Key(MediaKind.Movie, i.Id)) > 0),
            "@new" => visible.Where(i => i.Added > 0).OrderByDescending(i => i.Added).Take(300),
            _ => AllItems.Where(i => i.CategoryId == category.Id),
        };
    }

    void Categories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_reverting || Categories.SelectedItem is not Category category) return;
        if (!category.IsVirtual && !S.TryUnlock(category))
        {
            _reverting = true;
            Categories.SelectedItem = _category;
            _reverting = false;
            return;
        }
        _category = category;
        ApplyFilter();
        if (IsLoaded) Motion.Enter(Posters, 0, 12, 340);
    }

    void ApplyFilter()
    {
        if (_category is null) return;
        var items = ItemsFor(_category);
        var query = Filter.Text.Trim();
        if (query.Length > 0) items = items.Where(i => Text.Contains(i.Name, query));
        items = Sort.SelectedTag switch
        {
            "1" => items.OrderBy(i => i.Name, StringComparer.Create(CultureInfo.GetCultureInfo("es-ES"), true)),
            "2" => items.OrderByDescending(i => i.Added),
            "3" => items.OrderByDescending(i => i.Rating),
            _ => items,
        };
        var list = items.ToList();
        Posters.SetItems(list);
        var noun = _kind == MediaKind.Movie ? "película" : "serie";
        CountText.Text = list.Count == 1
            ? L.T("{0} · 1 " + noun, _category.Name)
            : L.T("{0} · {1:N0} " + (_kind == MediaKind.Movie ? "películas" : "series"), _category.Name, list.Count);
        EmptyPanel.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = L.T(query.Length > 0 ? "No hay resultados para la búsqueda."
            : _category.Id switch
            {
                "@fav" => "Aún no tienes favoritas.\nAbre una ficha y pulsa «Favorita».",
                "@resume" => "Aquí aparecerán las películas que dejes a medias.",
                _ => "No hay contenido en esta categoría.",
            });
    }

    void OpenItem(PosterItem item)
    {
        UIElement view = item switch
        {
            Movie m => new MovieDetailView(m),
            SeriesItem s => new SeriesDetailView(s),
            _ => throw new InvalidOperationException(),
        };
        MainWindow.Instance.Push(view);
    }

    void Categories_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = e.OriginalSource is DependencyObject d ? ItemsControl.ContainerFromElement(Categories, d) as ListBoxItem : null;
        if (item?.DataContext is not Category { IsVirtual: false } category)
        {
            e.Handled = true;
            return;
        }
        LockMenuItem.Tag = category;
        LockMenuItem.Header = L.T(category.IsLocked ? "Desbloquear «{0}»" : "Bloquear «{0}» con PIN", category.Name);
    }

    void LockMenu_Click(object sender, RoutedEventArgs e)
    {
        LiveView.ToggleLock(LockMenuItem.Tag as Category);
        UpdateVirtualCounts();
        ApplyFilter();
    }
}
