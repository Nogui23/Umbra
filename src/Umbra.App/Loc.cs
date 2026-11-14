using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using Umbra.Core;

namespace Umbra.App;

/// <summary>Binding source for translated texts in XAML: its indexer returns the text in the current language.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    Loc() => L.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));

    public string this[string text] => L.T(text);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// XAML: <c>Text="{local:T 'Ajustes'}"</c>. The Spanish text is the key; the value follows the language live, so
/// switching language updates the screen without rebuilding it.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension(string text) : MarkupExtension
{
    [ConstructorArgument("text")]
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding { Source = Loc.I, Path = new System.Windows.PropertyPath("[(0)]", Text), Mode = BindingMode.OneWay }
            .ProvideValue(serviceProvider);
}
