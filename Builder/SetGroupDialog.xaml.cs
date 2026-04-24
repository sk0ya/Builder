using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;

namespace Builder;

public partial class SetGroupDialog : UserControl
{
    public string GroupName
    {
        get => GroupCombo.Text;
        set => GroupCombo.Text = value;
    }

    public SetGroupDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => GroupCombo.Focus();
    }

    public void SetExistingGroups(IEnumerable<string> groups)
    {
        GroupCombo.ItemsSource = groups;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogHost.CloseDialogCommand.Execute(true, this);
    }
}
