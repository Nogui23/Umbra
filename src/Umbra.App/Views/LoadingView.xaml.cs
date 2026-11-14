using System.Windows.Controls;

namespace Umbra.App;

public partial class LoadingView : UserControl
{
    public LoadingView(string accountName)
    {
        InitializeComponent();
        AccountText.Text = accountName;
        InitialText.Text = InitialConverter.Of(accountName);
    }

    public void SetStatus(string text) => StatusText.Text = text;
}
