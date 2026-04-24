using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Builder.Models;
using MaterialDesignThemes.Wpf;

namespace Builder;

public class GroupAssignment
{
    public ProjectEntry Project { get; }
    public string Group { get; set; }

    public GroupAssignment(ProjectEntry p)
    {
        Project = p;
        Group = p.Group;
    }
}

public partial class GroupManagementDialog : UserControl
{
    public ObservableCollection<GroupAssignment> Items { get; } = [];
    public List<string> Groups { get; }

    public GroupManagementDialog(IEnumerable<ProjectEntry> projects, IEnumerable<string> existingGroups)
    {
        InitializeComponent();
        DataContext = this;
        Groups = [.. existingGroups];
        foreach (var p in projects)
            Items.Add(new GroupAssignment(p));
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogHost.CloseDialogCommand.Execute(true, this);
    }
}
