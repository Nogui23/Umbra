using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Umbra.Core;

namespace Umbra.App;

/// <summary>Name, photo and colour of a profile; with null, a new one.</summary>
public partial class ProfileEditView : UserControl
{
    readonly AppState S = AppState.I;
    readonly Profile? _profile;
    string _color;
    byte[]? _photo;          // chosen now, saved with «Guardar»
    bool _photoChanged, _removePhoto;

    public ProfileEditView(Profile? profile)
    {
        _profile = profile;
        InitializeComponent();
        TitleText.Text = L.T(profile is null ? "Nuevo perfil" : "Editar perfil");
        _color = profile?.Color ?? ProfileStore.Colors.FirstOrDefault(c => S.Profiles.Items.All(p => p.Color != c)) ?? ProfileStore.Colors[0];
        NameBox.Text = profile?.Name ?? "";
        Avatar.Profile = profile;
        DeleteButton.Visibility = profile is not null && S.Profiles.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BuildSwatches();
        UpdatePreview();
        Loaded += (_, _) =>
        {
            Motion.Enter(Panel, 40, 20, 480);
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    bool HasPhoto => !_removePhoto && (_photo is not null || _profile is { HasPhoto: true });

    void BuildSwatches()
    {
        Swatches.Children.Clear();
        foreach (var hex in ProfileStore.Colors)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var dot = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = new SolidColorBrush(color),
                BorderBrush = Brushes.White, BorderThickness = new Thickness(hex == _color ? 3 : 0),
            };
            var button = new Button
            {
                Content = dot, Style = (Style)FindResource("CardButton"), Margin = new Thickness(0, 0, 10, 10),
                ToolTip = hex, Cursor = System.Windows.Input.Cursors.Hand,
            };
            button.Click += (_, _) =>
            {
                _color = hex;
                BuildSwatches();
                UpdatePreview();
            };
            Swatches.Children.Add(button);
        }
    }

    void UpdatePreview()
    {
        Avatar.NameOverride = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "?";
        Avatar.ColorOverride = _color;
        // Without the profile the circle only shows the name and colour being chosen (its old photo is gone).
        Avatar.Profile = _removePhoto ? null : _profile;
        Avatar.Render();
        PhotoText.Text = L.T(HasPhoto ? "Cambiar foto" : "Subir foto");
        RemovePhotoButton.Visibility = HasPhoto ? Visibility.Visible : Visibility.Collapsed;
    }

    void Name_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) UpdatePreview();
    }

    void Photo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("Elige una foto"),
            Filter = L.T("Imágenes (*.jpg;*.jpeg;*.png;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        UsePhoto(dialog.FileName);
    }

    /// <summary>Takes a picture as the photo (shown now, saved with «Guardar»).</summary>
    internal void UsePhoto(string file)
    {
        try
        {
            var (png, image) = ProfileAvatar.MakePhoto(file);
            _photo = png;
            _photoChanged = true;
            _removePhoto = false;
            Avatar.Preview = image;
            UpdatePreview();
            HideError();
        }
        catch (SourceException ex)
        {
            ShowError(ex.Message);
        }
    }

    void RemovePhoto_Click(object sender, RoutedEventArgs e)
    {
        _photo = null;
        _photoChanged = true;
        _removePhoto = true;
        Avatar.Preview = null;
        UpdatePreview();
    }

    void ShowError(string message)
    {
        ErrorText.Text = L.T(message);
        ErrorBox.Visibility = Visibility.Visible;
        Motion.Shake(ErrorBox);
    }

    void HideError() => ErrorBox.Visibility = Visibility.Collapsed;

    internal void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Escribe un nombre para el perfil.");
            return;
        }
        // Names tell profiles apart (also when a backup is imported).
        if (S.Profiles.Items.Any(p => p != _profile && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            ShowError("Ya hay un perfil con ese nombre.");
            return;
        }
        var profile = _profile ?? S.Profiles.Add(name);
        profile.Name = name;
        profile.Color = _color;
        try
        {
            if (_photoChanged) S.Profiles.SetPhoto(profile, _removePhoto ? null : _photo);
            else S.Profiles.Save();
        }
        catch (SourceException ex)
        {
            ShowError(ex.Message);
            return;
        }
        S.ProfilesEdited();
        MainWindow.Instance.GoBack();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        if (!Sheet.Confirm(L.T("¿Eliminar el perfil «{0}»?", _profile.Name),
                "Se borrarán sus favoritos, su progreso y su «Seguir viendo» en todas las listas. Las descargas no se tocan.", "Eliminar")) return;
        S.Profiles.Remove(_profile);
        S.ProfilesEdited();
        MainWindow.Instance.GoBack();
    }

    void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.GoBack();
}
