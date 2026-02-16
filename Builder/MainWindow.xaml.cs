using System.Windows;
using Builder.ViewModels;

namespace Builder;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnSettingsFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SaveSettingsCommand.Execute(null);
        }
    }
}
