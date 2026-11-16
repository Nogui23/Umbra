using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Umbra.Core;

namespace Umbra.App;

/// <summary>
/// «¿Quién está viendo?»: the profiles as big circles. Choosing one makes it the profile in use; in «Gestionar
/// perfiles» mode choosing one opens it to edit, and a new one can be added.
/// </summary>
public partial class ProfilesView : UserControl, IRefreshable
{
    readonly AppState S = AppState.I;
    readonly Action<Profile>? _onPicked;
    bool _manage;

    /// <param name="manage">Opens in edit mode.</param>
    /// <param name="onPicked">What to do once a profile is chosen (it is already the one in use).</param>
    public ProfilesView(bool manage, Action<Profile>? onPicked)
    {
        _manage = manage;
        _onPicked = onPicked;
        InitializeComponent();
        Refresh();
        Loaded += (_, _) =>
        {
            // Known only once it is on screen (it is put on top of the previous one after being built).
            BackButton.Visibility = MainWindow.Instance.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
            Motion.Enter(Page, 40, 22, 520);
        };
    }

    public void Refresh()
    {
        BackButton.Visibility = MainWindow.Instance.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = L.T(_manage ? "Gestionar perfiles" : "¿Quién está viendo?");
        SubText.Text = L.T(_manage ? "Elige un perfil para cambiar su nombre, su foto o su color." : "Cada perfil tiene sus favoritos y su «Seguir viendo».");
        ManageText.Text = L.T(_manage ? "Listo" : "Gestionar perfiles");
        ManageIcon.Text = _manage ? "" : "";
        // Opened only to manage (from the top bar): «Listo» goes back.
        ManageButton.Visibility = Visibility.Visible;

        Tiles.Children.Clear();
        foreach (var profile in S.Profiles.Items) Tiles.Children.Add(Tile(profile));
        if (_manage) Tiles.Children.Add(AddTile());
    }

    Button Tile(Profile profile)
    {
        var avatar = new ProfileAvatar { Profile = profile, Size = 128, HorizontalAlignment = HorizontalAlignment.Center };
        var circle = new Grid { Width = 128, Height = 128, HorizontalAlignment = HorizontalAlignment.Center };
        circle.Children.Add(avatar);
        if (_manage)
        {
            // A pencil over the circle says it opens to edit.
            var edit = new Border { Width = 128, Height = 128, CornerRadius = new CornerRadius(64), Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)) };
            edit.Child = new TextBlock { Text = "", FontSize = 30, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            ((TextBlock)edit.Child).SetResourceReference(StyleProperty, "Icon");
            circle.Children.Add(edit);
        }
        else if (profile.Id == S.Profile.Id && S.Profiles.Items.Count > 1)
        {
            circle.Children.Add(new Border
            {
                Width = 136, Height = 136, Margin = new Thickness(-4), CornerRadius = new CornerRadius(68),
                BorderBrush = Brushes.White, BorderThickness = new Thickness(2), IsHitTestVisible = false,
            });
        }
        var name = new TextBlock
        {
            Text = profile.Name,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var content = new StackPanel { Width = 150 };
        content.Children.Add(circle);
        content.Children.Add(name);
        var button = new Button
        {
            Content = content,
            Style = (Style)FindResource("CardButton"),
            Margin = new Thickness(14),
            Tag = profile,
            ToolTip = profile.Name,
        };
        button.Click += (_, _) => Pick(profile);
        return button;
    }

    Button AddTile()
    {
        var plus = new Border { Width = 128, Height = 128, CornerRadius = new CornerRadius(64), HorizontalAlignment = HorizontalAlignment.Center };
        plus.SetResourceReference(Border.BackgroundProperty, "Surface2");
        var glyph = new TextBlock { Text = "", FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(StyleProperty, "Icon");
        plus.Child = glyph;
        var content = new StackPanel { Width = 150 };
        content.Children.Add(plus);
        content.Children.Add(new TextBlock
        {
            Text = L.T("Añadir perfil"), FontSize = 16, FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 16, 0, 0),
        });
        var button = new Button { Content = content, Style = (Style)FindResource("CardButton"), Margin = new Thickness(14) };
        button.Click += (_, _) => MainWindow.Instance.Push(new ProfileEditView(null));
        return button;
    }

    void Pick(Profile profile)
    {
        if (_manage)
        {
            MainWindow.Instance.Push(new ProfileEditView(profile));
            return;
        }
        if (profile.Id != S.Profile.Id) S.SwitchProfile(profile);
        _onPicked?.Invoke(profile);
    }

    void Manage_Click(object sender, RoutedEventArgs e)
    {
        // Opened to manage from inside the app: «Listo» goes back to where it was.
        if (_manage && _onPicked is null && MainWindow.Instance.CanGoBack)
        {
            MainWindow.Instance.GoBack();
            return;
        }
        _manage = !_manage;
        Refresh();
    }

    void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.GoBack();
}
