using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Builder.Models;
using Builder.ViewModels;
using MaterialDesignThemes.Wpf;

namespace Builder;

public partial class MainWindow : Window
{
    // 行をまたいで ANSI エスケープ状態を維持するインスタンス
    private readonly AnsiState _ansiState = new();
    private const string ProjectDragFormat = "Builder.ProjectEntryId";

    public MainWindow()
    {
        InitializeComponent();

        // FlowDocument のデフォルト余白をゼロに
        OutputLogBox.Document.PagePadding = new Thickness(0);

        if (DataContext is MainViewModel vm)
        {
            vm.LineAppended += AppendAnsiLine;
            vm.LogReset += ResetLog;
            ResetLog(vm.OutputLog);
        }
    }

    private Paragraph EnsureParagraph()
    {
        var doc = OutputLogBox.Document;
        if (doc.Blocks.LastBlock is Paragraph p) return p;
        var para = new Paragraph { Margin = new Thickness(0) };
        doc.Blocks.Add(para);
        return para;
    }

    private void AppendAnsiLine(string line)
    {
        line = line.TrimEnd('\r');
        var para = EnsureParagraph();
        AppendLineToParagraph(para, line);
        para.Inlines.Add(new LineBreak());
        OutputLogBox.ScrollToEnd();
    }

    private void ResetLog(string fullLog)
    {
        _ansiState.Reset();  // 状態リセット（プロジェクト切替・クリア時）

        var doc = OutputLogBox.Document;
        doc.Blocks.Clear();

        if (string.IsNullOrEmpty(fullLog)) return;

        var para = new Paragraph { Margin = new Thickness(0) };
        doc.Blocks.Add(para);

        var lines = fullLog.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            AppendLineToParagraph(para, line);
            if (i < lines.Length - 1)
                para.Inlines.Add(new LineBreak());
        }

        OutputLogBox.ScrollToEnd();
    }

    /// <summary>
    /// 1行分のテキストを Paragraph に追加する。
    /// - ANSI コードを含む行、または前の行からアクティブな色が引き継がれている場合:
    ///   AnsiState でパースして色付きスパンを追加する。
    /// - それ以外: キーワードパターンでフォールバック色付けを行う。
    /// </summary>
    private void AppendLineToParagraph(Paragraph para, string line)
    {
        bool hasAnsiCodes = line.Contains('\x1b');
        bool hadActiveState = _ansiState.HasActiveState;

        // 状態を更新しながらパース（ANSI なし・状態なしの場合も呼んでおく）
        var spans = _ansiState.Parse(line);

        if (!hasAnsiCodes && !hadActiveState)
        {
            // ANSI コードなし・引き継ぎ状態なし → キーワードフォールバック
            var fallback = AnsiParser.GetFallbackColor(line);
            var run = new Run(line);
            if (fallback.HasValue)
                run.Foreground = new SolidColorBrush(fallback.Value);
            para.Inlines.Add(run);
            return;
        }

        // ANSI あり or 前行から色を引き継いでいる → スパンごとに色を適用
        foreach (var span in spans)
        {
            if (span.Text.Length == 0) continue;
            var run = new Run(span.Text);
            if (span.Foreground.HasValue)
                run.Foreground = new SolidColorBrush(span.Foreground.Value);
            if (span.Background.HasValue)
                run.Background = new SolidColorBrush(span.Background.Value);
            if (span.Bold)
                run.FontWeight = FontWeights.Bold;
            para.Inlines.Add(run);
        }
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

    private void ProjectListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ProjectEntry project)
        {
            if (DataContext is MainViewModel vm)
                vm.SelectedProject = project;
        }
    }

    private void ProjectList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var item = FindAncestor<ListBoxItem>(source);
        if (item == null) return;

        if (item.DataContext is not ProjectEntry project) return;
        if (!System.IO.Directory.Exists(project.FolderPath)) return;

        item.IsSelected = true;
        ShellContextMenu.Prepare(project.FolderPath, this,
            copyRepoPathAction: () =>
            {
                if (DataContext is MainViewModel vm && vm.CopyRepoPathCommand.CanExecute(null))
                    vm.CopyRepoPathCommand.Execute(null);
            },
            setGroupAction: () =>
            {
                if (DataContext is MainViewModel vm)
                    vm.SetGroupCommand.Execute(null);
            });
    }

    private void ProjectList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var screenPoint = PointToScreen(e.GetPosition(this));
        ShellContextMenu.ShowPrepared(screenPoint);
        e.Handled = true;
    }

    // --- Drag & Drop for project reordering ---

    private Point _dragStartPoint;
    private string? _dragProjectId;
    private bool _isDragging;

    private void ProjectList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
        _dragProjectId = null;

        if (sender is not ListBox listBox) return;
        if (e.OriginalSource is not DependencyObject source) return;

        var item = FindAncestor<ListBoxItem>(source);
        if (item == null) return;
        if (item.DataContext is not ProjectEntry project) return;

        _dragProjectId = project.Id;
    }

    private void ProjectList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (string.IsNullOrEmpty(_dragProjectId)) return;

        var diff = e.GetPosition(null) - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (_isDragging) return;
        if (sender is not ListBox listBox) return;
        if (DataContext is not MainViewModel vm) return;
        if (!vm.Projects.Any(p => string.Equals(p.Id, _dragProjectId, StringComparison.Ordinal))) return;

        try
        {
            _isDragging = true;
            var dragData = new DataObject();
            dragData.SetData(ProjectDragFormat, _dragProjectId);
            DragDrop.DoDragDrop(listBox, dragData, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ProjectList_PreviewMouseMove: {ex.Message}");
        }
        finally
        {
            _isDragging = false;
            _dragProjectId = null;
        }
    }

    private void ProjectList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ProjectDragFormat))
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
        if (!e.Data.GetDataPresent(ProjectDragFormat)) return;
        if (DataContext is not MainViewModel vm) return;
        if (e.Data.GetData(ProjectDragFormat) is not string droppedProjectId ||
            string.IsNullOrWhiteSpace(droppedProjectId))
            return;
        var droppedData = vm.Projects.FirstOrDefault(p => string.Equals(p.Id, droppedProjectId, StringComparison.Ordinal));
        if (droppedData == null) return;

        ListBoxItem? targetItem = null;
        if (e.OriginalSource is DependencyObject source)
            targetItem = FindAncestor<ListBoxItem>(source);

        var oldIndex = vm.Projects.IndexOf(droppedData);
        if (oldIndex < 0) return;

        int newIndex;
        if (targetItem != null)
        {
            if (targetItem.DataContext is not ProjectEntry target) return;
            newIndex = vm.Projects.IndexOf(target);
            if (newIndex < 0) return;
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

    private void GroupTabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        var sv = FindVisualChild<ScrollViewer>(listBox);
        if (sv == null) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T t) return t;
            // Run/Span etc. (TextBlock inlines) are DependencyObject but not Visual.
            // VisualTreeHelper.GetParent throws for non-Visual elements, so fall back
            // to LogicalTreeHelper which correctly walks up to the containing TextBlock.
            current = (current is Visual or Visual3D)
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

}
