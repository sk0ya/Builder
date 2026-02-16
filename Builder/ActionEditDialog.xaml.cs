using System.Windows;

namespace Builder;

public partial class ActionEditDialog : Window
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

    public ActionEditDialog()
    {
        InitializeComponent();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ActionName))
        {
            MessageBox.Show("名前を入力してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
