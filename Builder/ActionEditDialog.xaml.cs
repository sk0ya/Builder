using System.Windows;
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;

namespace Builder;

public partial class ActionEditDialog : UserControl
{
    public string ActionName
    {
        get => NameBox.Text;
        set => NameBox.Text = value;
    }

    public string Script
    {
        get => ScriptBox.Text;
        set => ScriptBox.Text = value;
    }

    public bool LaunchOnly
    {
        get => LaunchOnlyCheck.IsChecked == true;
        set => LaunchOnlyCheck.IsChecked = value;
    }

    public ActionEditDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ActionName))
        {
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogHost.CloseDialogCommand.Execute(true, this);
    }
}
