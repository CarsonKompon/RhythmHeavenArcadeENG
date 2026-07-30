using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ModdingTool.App.Services;
using ModdingTool.App.ViewModels;
using Microsoft.Win32;

namespace ModdingTool.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    private Point dragStart;
    private TextureItemViewModel[] dragItems = [];
    private TextureItemViewModel? inspectorBeforeDrag;
    private OutputGroupViewModel? contextOutputGroup;
    private string? pendingOriginalFolder;
    private string? pendingOutputFolder;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = await AppSettingsStore.LoadAsync();
        viewModel.SetHideSeenOriginals(settings.HideSeenOriginals);
        viewModel.SetHideTodoOriginals(settings.HideTodoOriginals);
        viewModel.LeftPaneSource = settings.LeftPaneSource ?? PaneSource.Originals;
        viewModel.RightPaneSource = settings.RightPaneSource ?? PaneSource.Output;
        viewModel.LeftHideGroups = settings.LeftHideGroups ?? false;
        viewModel.RightHideGroups = settings.RightHideGroups ?? settings.HideGroups;
        viewModel.LeftOnlyUnfinished = settings.LeftOnlyUnfinished;
        viewModel.RightOnlyUnfinished = settings.RightOnlyUnfinished;
        if (Directory.Exists(settings.OriginalFolder) && Directory.Exists(settings.OutputFolder))
        {
            pendingOriginalFolder = settings.OriginalFolder;
            pendingOutputFolder = settings.OutputFolder;
            await TryOpenFoldersAsync();
        }

        await Dispatcher.InvokeAsync(ApplyViewLayout, DispatcherPriority.Loaded);
    }

    private async void ChooseOriginalFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the original texture folder" };
        if (dialog.ShowDialog(this) == true)
        {
            pendingOriginalFolder = dialog.FolderName;
            await TryOpenFoldersAsync();
        }
    }

    private async void ChooseOutputFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the modified / live output folder" };
        if (dialog.ShowDialog(this) == true)
        {
            pendingOutputFolder = dialog.FolderName;
            await TryOpenFoldersAsync();
        }
    }

    private async Task TryOpenFoldersAsync()
    {
        if (pendingOriginalFolder is not null && pendingOutputFolder is not null)
        {
            await viewModel.OpenFoldersAsync(pendingOriginalFolder, pendingOutputFolder);
            await AppSettingsStore.SaveAsync(GetCurrentSettings());
        }
    }

    private AppSettings GetCurrentSettings() => new(
        pendingOriginalFolder,
        pendingOutputFolder,
        viewModel.HideSeenOriginals,
        viewModel.HideTodoOriginals,
        viewModel.RightHideGroups,
        viewModel.LeftPaneSource,
        viewModel.RightPaneSource,
        viewModel.LeftHideGroups,
        viewModel.RightHideGroups,
        viewModel.LeftOnlyUnfinished,
        viewModel.RightOnlyUnfinished);

    private void ToggleView(object sender, RoutedEventArgs e)
    {
        viewModel.ToggleView();
        ApplyViewLayout();
    }

    private void ApplyViewLayout()
    {
        var panelName = viewModel.IsGridView ? "TextureGridPanel" : "TextureListPanel";
        var panel = (ItemsPanelTemplate)FindResource(panelName);
        var templateName = viewModel.IsGridView ? "TextureTemplate" : "TextureListTemplate";
        var template = (DataTemplate)FindResource(templateName);
        var itemStyleName = viewModel.IsGridView ? "TextureGridItemStyle" : "TextureListItemStyle";
        var itemStyle = (Style)FindResource(itemStyleName);
        foreach (var listBox in AllPaneLists)
        {
            listBox.ItemsPanel = panel;
            listBox.ItemContainerStyle = itemStyle;
            listBox.HorizontalContentAlignment = viewModel.IsGridView
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Stretch;
            listBox.ItemTemplate = template;
            listBox.Items.Refresh();
            listBox.InvalidateMeasure();
        }
    }

    private void PreviousOriginalPage(object sender, RoutedEventArgs e)
    {
        ScrollOriginalsToTop();
        viewModel.ShowPreviousOriginals();
    }

    private void NextOriginalPage(object sender, RoutedEventArgs e)
    {
        ScrollOriginalsToTop();
        viewModel.ShowNextOriginals();
    }

    private void ScrollOriginalsToTop()
    {
        foreach (var listBox in new[] { LeftOriginalList, RightOriginalList })
        {
            FindVisualChild<ScrollViewer>(listBox)?.ScrollToTop();
        }
    }

    private async void ThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TextureItemViewModel item })
        {
            await item.EnsureThumbnailAsync();
        }
    }

    private void TextureListMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(null);
        if (sender is ListBox listBox &&
            FindItem(e.OriginalSource as DependencyObject) is { DataContext: TextureItemViewModel item })
        {
            inspectorBeforeDrag = item.IsOutput ? viewModel.SelectedTexture : null;
            dragItems = listBox.SelectedItems.Contains(item)
                ? listBox.SelectedItems.Cast<TextureItemViewModel>().ToArray()
                : [item];
        }
    }

    private void TextureListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not ListBox listBox)
        {
            return;
        }

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (FindItem(e.OriginalSource as DependencyObject) is { DataContext: TextureItemViewModel item })
        {
            if (item.IsOutput && inspectorBeforeDrag is not null)
            {
                viewModel.SelectedTexture = inspectorBeforeDrag;
                viewModel.SelectedOutput = inspectorBeforeDrag.IsOutput ? inspectorBeforeDrag : null;
            }

            DragDrop.DoDragDrop(listBox, new DataObject(typeof(TextureItemViewModel[]), dragItems), DragDropEffects.Copy | DragDropEffects.Move);
        }
    }

    private void TextureSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.SelectedItem is not TextureItemViewModel item)
        {
            return;
        }

        viewModel.SelectedTexture = item;
        viewModel.SelectedOutput = item.IsOutput ? item : null;
    }

    private void PaneListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox) return;

        contextOutputGroup = FindVisualParent<GroupItem>(e.OriginalSource as DependencyObject)?.DataContext is
            System.Windows.Data.CollectionViewGroup { Name: OutputGroupViewModel group }
                ? group
                : null;

        if (FindItem(e.OriginalSource as DependencyObject) is not { DataContext: TextureItemViewModel item } container)
        {
            return;
        }

        if (!container.IsSelected)
        {
            listBox.SelectedItems.Clear();
            container.IsSelected = true;
        }

        viewModel.SelectedTexture = item;
        viewModel.SelectedOutput = item.IsOutput ? item : null;
    }

    private async void MarkOriginalsSeen(object sender, RoutedEventArgs e) =>
        await viewModel.SetSeenAsync(GetContextSelection(sender), true);

    private async void MarkOriginalsUnseen(object sender, RoutedEventArgs e) =>
        await viewModel.SetSeenAsync(GetContextSelection(sender), false);

    private async void MarkOriginalsTodo(object sender, RoutedEventArgs e) =>
        await viewModel.SetTodoAsync(GetContextSelection(sender), true);

    private async void ClearOriginalsTodo(object sender, RoutedEventArgs e) =>
        await viewModel.SetTodoAsync(GetContextSelection(sender), false);

    private async void MarkOutputsUnfinished(object sender, RoutedEventArgs e) =>
        await viewModel.SetUnfinishedAsync(GetContextSelection(sender), true);

    private async void ClearOutputsUnfinished(object sender, RoutedEventArgs e) =>
        await viewModel.SetUnfinishedAsync(GetContextSelection(sender), false);

    private void HideSeenChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            viewModel.SetHideSeenOriginals(checkBox.IsChecked == true);
        }
    }

    private void HideTodoChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            viewModel.SetHideTodoOriginals(checkBox.IsChecked == true);
        }
    }

    private async void CreateGroupFromTexture(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedOutput is not { } item || PromptForGroupName() is not { } name)
        {
            return;
        }

        await viewModel.CreateGroupAsync(item, name);
    }

    private async void RenameGroupFromMenu(object sender, RoutedEventArgs e)
    {
        var group = contextOutputGroup ?? viewModel.SelectedOutput?.OutputGroup;
        if (group?.Id is not { } groupId || PromptForGroupName(group.Name) is not { } name)
        {
            return;
        }

        await viewModel.RenameGroupAsync(groupId, name);
    }

    private async void RemoveFromGroup(object sender, RoutedEventArgs e)
    {
        var items = GetContextSelection(sender)
            .Where(item => item.OutputGroup?.Id is not null)
            .ToArray();
        await viewModel.UngroupItemsAsync(items);
    }

    private async void MoveTextureUp(object sender, RoutedEventArgs e) => await viewModel.MoveSelectedItemAsync(-1);

    private async void MoveTextureDown(object sender, RoutedEventArgs e) => await viewModel.MoveSelectedItemAsync(1);

    private void OpenOutputImage(object sender, RoutedEventArgs e)
    {
        if (GetContextSelection(sender).FirstOrDefault() is { } item) viewModel.OpenInEditor(item);
    }

    private void OpenOutputOriginalImage(object sender, RoutedEventArgs e)
    {
        if (GetContextSelection(sender).FirstOrDefault() is { } item) viewModel.OpenOriginalInEditor(item);
    }

    private async void OutputGroupExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander { DataContext: System.Windows.Data.CollectionViewGroup { Name: OutputGroupViewModel group } })
        {
            await viewModel.SetGroupCollapsedAsync(group.Id, false);
        }
    }

    private async void OutputGroupCollapsed(object sender, RoutedEventArgs e)
    {
        if (sender is Expander { DataContext: System.Windows.Data.CollectionViewGroup { Name: OutputGroupViewModel group } })
        {
            await viewModel.SetGroupCollapsedAsync(group.Id, true);
        }
    }

    private void TextureDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindItem(e.OriginalSource as DependencyObject) is { DataContext: TextureItemViewModel item })
        {
            viewModel.OpenInEditor(item);
        }
    }

    private void PaneDragOver(object sender, DragEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            AutoScroll(listBox, e);
            e.Effects = IsOutputPane(listBox) && e.Data.GetDataPresent(typeof(TextureItemViewModel[]))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }
    }

    private async void OutputDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox || !IsOutputPane(listBox)) return;

        if (e.Data.GetData(typeof(TextureItemViewModel[])) is not TextureItemViewModel[] draggedItems || draggedItems.Length == 0)
        {
            return;
        }

        var targetContainer = FindItem(e.OriginalSource as DependencyObject);
        var targetGroup = FindVisualParent<GroupItem>(e.OriginalSource as DependencyObject)?.DataContext is
            System.Windows.Data.CollectionViewGroup { Name: OutputGroupViewModel group }
                ? group
                : null;

        if (!draggedItems[0].IsOutput)
        {
            if (await viewModel.CopyOriginalsAsync(draggedItems) && targetGroup?.Id is { } destinationGroupId)
            {
                await viewModel.AddFilesToGroupAsync(draggedItems.Select(item => item.FileName).ToArray(), destinationGroupId);
            }
            return;
        }

        if (targetContainer is null && targetGroup?.Id is null)
        {
            await viewModel.UngroupItemsAsync(draggedItems);
            return;
        }

        if (targetContainer is null && targetGroup?.Id is { } groupId)
        {
            await viewModel.AddItemsToGroupAsync(draggedItems, groupId);
            return;
        }

        if (targetContainer is { DataContext: TextureItemViewModel target })
        {
            if (draggedItems.All(item => item == target))
            {
                return;
            }

            string? newGroupName = null;
            if (target.GroupName == "Ungrouped")
            {
                newGroupName = PromptForGroupName();
                if (newGroupName is null)
                {
                    return;
                }
            }

            await viewModel.GroupAsync(draggedItems, target, newGroupName);
        }
    }

    private void CopyImageDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(TextureItemViewModel[])) is TextureItemViewModel[] { Length: 1 } items && items[0].IsOutput
            ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void CopyImageDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TextureItemViewModel[])) is TextureItemViewModel[] { Length: 1 } items && items[0].IsOutput)
        {
            await viewModel.AssignCopyAsync(items[0]);
        }
    }

    private async void SaveBrightness(object sender, RoutedEventArgs e) => await viewModel.ApplyBrightnessAsync();

    private async void ClearCopy(object sender, RoutedEventArgs e) => await viewModel.ClearCopyAsync();

    private async void RenameGroup(object sender, RoutedEventArgs e) =>
        await viewModel.RenameSelectedGroupAsync(GroupNameBox.Text);

    private async void Ungroup(object sender, RoutedEventArgs e) => await viewModel.UngroupSelectedAsync();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        AppSettingsStore.Save(GetCurrentSettings());
        viewModel.Dispose();
    }

    private string? PromptForGroupName(string initialName = "New group")
    {
        var input = new TextBox { Margin = new Thickness(0, 8, 0, 14), Padding = new Thickness(7), Text = initialName };
        var dialog = new Window
        {
            Title = "Create texture group",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = System.Windows.Media.Brushes.White,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Width = 320,
                Children =
                {
                    new TextBlock { Text = "Group name", FontWeight = FontWeights.SemiBold },
                    input
                }
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var create = new Button { Content = "Create", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        create.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(input.Text))
            {
                dialog.DialogResult = true;
            }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        ((StackPanel)dialog.Content).Children.Add(buttons);
        input.SelectAll();
        input.Focus();
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private static void AutoScroll(ListBox listBox, DragEventArgs e)
    {
        const double edgeSize = 56;
        var position = e.GetPosition(listBox);
        if (FindVisualChild<ScrollViewer>(listBox) is not { } scrollViewer)
        {
            return;
        }

        if (position.Y < edgeSize)
        {
            scrollViewer.LineUp();
        }
        else if (position.Y > listBox.ActualHeight - edgeSize)
        {
            scrollViewer.LineDown();
        }
    }

    private static ListBoxItem? FindItem(DependencyObject? source)
    {
        while (source is not null and not ListBoxItem)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return source as ListBoxItem;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private ListBox[] AllPaneLists => [LeftOriginalList, LeftOutputList, RightOriginalList, RightOutputList];

    private bool IsOutputPane(ListBox listBox) => listBox == LeftOutputList || listBox == RightOutputList;

    private static TextureItemViewModel[] GetContextSelection(object sender)
    {
        if (sender is MenuItem menuItem &&
            ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu { PlacementTarget: ListBox listBox })
        {
            return listBox.SelectedItems.Cast<TextureItemViewModel>().ToArray();
        }

        return [];
    }
}