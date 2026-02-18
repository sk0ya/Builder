using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Builder.Models;
using Builder.ViewModels;
using MaterialDesignThemes.Wpf;

namespace Builder;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OutputLogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        OutputLogTextBox.ScrollToEnd();
    }

    private void OnSettingsFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SaveSettingsCommand.Execute(null);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeIcon.Kind = PackIconKind.WindowMaximize;
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeIcon.Kind = PackIconKind.WindowRestore;
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.OpenSettingsCommand.Execute(null);
        }
    }

    // --- Drag & Drop for project reordering ---

    private Point _dragStartPoint;
    private bool _isDragging;

    private void ProjectList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
    }

    private void ProjectList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var diff = e.GetPosition(null) - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (_isDragging) return;

        var listBox = (ListBox)sender;
        var item = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (item == null) return;

        var data = (ProjectEntry)listBox.ItemContainerGenerator.ItemFromContainer(item);
        if (data == null) return;

        _isDragging = true;
        DragDrop.DoDragDrop(item, data, DragDropEffects.Move);
        _isDragging = false;
    }

    private void ProjectList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ProjectEntry)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ProjectList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ProjectEntry))) return;

        var droppedData = (ProjectEntry)e.Data.GetData(typeof(ProjectEntry))!;
        var targetItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);

        if (DataContext is not MainViewModel vm) return;

        var oldIndex = vm.Projects.IndexOf(droppedData);
        if (oldIndex < 0) return;

        int newIndex;
        if (targetItem != null)
        {
            var target = (ProjectEntry)ProjectListBox.ItemContainerGenerator.ItemFromContainer(targetItem);
            newIndex = vm.Projects.IndexOf(target);
        }
        else
        {
            newIndex = vm.Projects.Count - 1;
        }

        if (oldIndex == newIndex) return;

        vm.Projects.Move(oldIndex, newIndex);
        vm.SelectedProject = droppedData;
        vm.SaveSettingsCommand.Execute(null);
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

}
