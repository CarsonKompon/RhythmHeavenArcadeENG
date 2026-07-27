using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using ModdingTool.Core.Models;
using ModdingTool.Core.Services;

namespace ModdingTool.App.ViewModels;

public enum PaneSource
{
    Originals,
    Output
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int OriginalPageSize = 100;
    private static readonly string[] GroupColors =
    [
        "#E8F0E6", "#F3E8D8", "#E2ECF3", "#F1E3E6", "#E9E5F2", "#E3EFEC"
    ];

    private readonly TextureWorkspace workspace = new();
    private readonly Dictionary<string, DateTime> pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> scannedOriginalFileNames = [];
    private IReadOnlyList<string> originalFileNames = [];
    private FileSystemWatcher? originalWatcher;
    private FileSystemWatcher? outputWatcher;

    [ObservableProperty] private string? originalFolder;
    [ObservableProperty] private string? outputFolder;
    [ObservableProperty] private TextureItemViewModel? selectedOutput;
    [ObservableProperty] private TextureItemViewModel? selectedTexture;
    [ObservableProperty] private bool hideSeenOriginals;
    [ObservableProperty] private bool hideTodoOriginals;
    [ObservableProperty] private PaneSource leftPaneSource = PaneSource.Originals;
    [ObservableProperty] private PaneSource rightPaneSource = PaneSource.Output;
    [ObservableProperty] private bool leftHideGroups;
    [ObservableProperty] private bool rightHideGroups;
    [ObservableProperty] private bool leftOnlyUnfinished;
    [ObservableProperty] private bool rightOnlyUnfinished;
    [ObservableProperty] private bool isGridView = true;
    [ObservableProperty] private string status = "Choose original and modified texture folders to begin.";
    [ObservableProperty] private int originalPage = 1;
    [ObservableProperty] private int originalPageCount = 1;
    [ObservableProperty] private int originalCount;

    public MainViewModel()
    {
        LeftOutputView = CreateOutputView(item => item is TextureItemViewModel texture &&
            (!LeftHideGroups || texture.OutputGroup?.Id is null) &&
            (!LeftOnlyUnfinished || texture.IsUnfinished));
        RightOutputView = CreateOutputView(item => item is TextureItemViewModel texture &&
            (!RightHideGroups || texture.OutputGroup?.Id is null) &&
            (!RightOnlyUnfinished || texture.IsUnfinished));
    }

    public bool CanShowPreviousOriginals => OriginalPage > 1;
    public bool CanShowNextOriginals => OriginalPage < OriginalPageCount;
    public bool IsLeftOriginals => LeftPaneSource == PaneSource.Originals;
    public bool IsLeftOutput => LeftPaneSource == PaneSource.Output;
    public bool IsRightOriginals => RightPaneSource == PaneSource.Originals;
    public bool IsRightOutput => RightPaneSource == PaneSource.Output;
    public ObservableCollection<TextureItemViewModel> Originals { get; } = [];
    public ObservableCollection<TextureItemViewModel> Outputs { get; } = [];
    public ICollectionView LeftOutputView { get; }
    public ICollectionView RightOutputView { get; }

    public async Task OpenFoldersAsync(string originalFolder, string outputFolder)
    {
        OriginalFolder = originalFolder;
        OutputFolder = outputFolder;
        await RunAsync(async () =>
        {
            await workspace.OpenAsync(originalFolder, outputFolder);
            foreach (var group in workspace.Project.Groups)
            {
                group.IsCollapsed = true;
            }
            RefreshCollections();
            BindWatchers();
            Status = $"Loaded {OriginalCount:N0} originals and {Outputs.Count:N0} modified textures.";
        });
    }

    public void ShowPreviousOriginals()
    {
        if (!CanShowPreviousOriginals) return;
        OriginalPage--;
        RefreshOriginalPage();
    }

    public void ShowNextOriginals()
    {
        if (!CanShowNextOriginals) return;
        OriginalPage++;
        RefreshOriginalPage();
    }

    public async Task CopyOriginalAsync(TextureItemViewModel item) => await CopyOriginalsAsync([item]);

    public async Task<bool> CopyOriginalsAsync(IReadOnlyList<TextureItemViewModel> items)
    {
        if (OutputFolder is null || items.Count == 0) return false;
        var existingCount = items.Count(item => File.Exists(Path.Combine(OutputFolder, item.FileName)));
        if (existingCount > 0 && MessageBox.Show(
                $"Replace {existingCount:N0} existing modified texture(s)?",
                "Replace texture", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return false;
        }

        await RunAsync(async () =>
        {
            if (outputWatcher is not null) outputWatcher.EnableRaisingEvents = false;
            try
            {
                foreach (var item in items) await workspace.CopyOriginalAsync(item.FileName, true);
                RefreshCollections(items[0].FileName);
                Status = $"Copied {items.Count:N0} texture(s).";
            }
            finally
            {
                if (outputWatcher is not null) outputWatcher.EnableRaisingEvents = true;
            }
        });
        return true;
    }

    public async Task SetSeenAsync(IReadOnlyList<TextureItemViewModel> items, bool value) =>
        await SetOriginalFlagAsync(items, value, false);

    public async Task SetTodoAsync(IReadOnlyList<TextureItemViewModel> items, bool value) =>
        await SetOriginalFlagAsync(items, value, true);

    public async Task SetUnfinishedAsync(IReadOnlyList<TextureItemViewModel> items, bool value)
    {
        var outputs = items.Where(item => item.IsOutput).ToArray();
        if (outputs.Length == 0) return;
        foreach (var item in outputs)
        {
            GetEntry(item.FileName).IsUnfinished = value;
            item.IsUnfinished = value;
        }

        if (LeftOnlyUnfinished) LeftOutputView.Refresh();
        if (RightOnlyUnfinished) RightOutputView.Refresh();
        Status = $"Marked {outputs.Length:N0} modified texture(s) as {(value ? "unfinished" : "finished")}.";
        await workspace.SaveAsync();
    }

    public void SetHideSeenOriginals(bool hide)
    {
        HideSeenOriginals = hide;
        ApplyOriginalFilter();
    }

    public void SetHideTodoOriginals(bool hide)
    {
        HideTodoOriginals = hide;
        ApplyOriginalFilter();
    }

    public async Task AssignCopyAsync(TextureItemViewModel source)
    {
        if (SelectedOutput is null) return;
        await RunAsync(async () =>
        {
            var selectedFileName = SelectedOutput.FileName;
            await workspace.AssignCopyAsync(selectedFileName, source.FileName);
            RefreshCollections(selectedFileName);
            Status = $"{selectedFileName} now copies {source.FileName}.";
        });
    }

    public async Task ApplyBrightnessAsync()
    {
        if (SelectedOutput is null || !SelectedOutput.HasCopySource) return;
        var selectedFileName = SelectedOutput.FileName;
        var brightness = SelectedOutput.Brightness;
        await RunAsync(async () =>
        {
            await workspace.SetBrightnessAsync(selectedFileName, brightness);
            RefreshCollections(selectedFileName);
            Status = $"Saved {brightness}% brightness to {selectedFileName}.";
        });
    }

    public async Task ClearCopyAsync()
    {
        if (SelectedOutput is null) return;
        var selectedFileName = SelectedOutput.FileName;
        await RunAsync(async () =>
        {
            await workspace.ClearCopyAsync(selectedFileName);
            RefreshCollections(selectedFileName);
            Status = $"Cleared Copy Image from {selectedFileName}.";
        });
    }

    public async Task GroupAsync(IReadOnlyList<TextureItemViewModel> draggedItems, TextureItemViewModel target, string? newGroupName = null)
    {
        var items = draggedItems.Where(item => item.IsOutput && item != target).ToArray();
        if (items.Length == 0) return;
        var targetEntry = GetEntry(target.FileName);
        var group = targetEntry.GroupId is { } targetGroupId
            ? workspace.Project.Groups.FirstOrDefault(candidate => candidate.Id == targetGroupId)
            : null;
        if (group is null)
        {
            group = new TextureGroup
            {
                Name = string.IsNullOrWhiteSpace(newGroupName) ? $"Group {workspace.Project.Groups.Count + 1}" : newGroupName.Trim(),
                Order = workspace.Project.Groups.Count,
                IsCollapsed = true
            };
            workspace.Project.Groups.Add(group);
            AssignToGroup(targetEntry, group.Id, 0);
        }

        var nextOrder = workspace.Project.Textures.Values.Count(entry => entry.GroupId == group.Id);
        foreach (var item in items)
        {
            var entry = GetEntry(item.FileName);
            AssignToGroup(entry, group.Id, nextOrder++);
        }

        await workspace.SaveAsync();
        RefreshCollections(items[0].FileName);
        Status = $"Grouped {items.Length:N0} texture(s) with {target.FileName}.";
    }

    public async Task CreateGroupAsync(TextureItemViewModel item, string name)
    {
        if (!item.IsOutput || string.IsNullOrWhiteSpace(name)) return;
        var group = new TextureGroup { Name = name.Trim(), Order = workspace.Project.Groups.Count, IsCollapsed = true };
        workspace.Project.Groups.Add(group);
        var entry = GetEntry(item.FileName);
        AssignToGroup(entry, group.Id, 0);
        await workspace.SaveAsync();
        RefreshCollections(item.FileName);
        Status = $"Created group {group.Name}.";
    }

    public async Task AddItemsToGroupAsync(IReadOnlyList<TextureItemViewModel> items, Guid groupId)
    {
        await AddFilesToGroupAsync(items.Where(item => item.IsOutput).Select(item => item.FileName).ToArray(), groupId);
    }

    public async Task AddFilesToGroupAsync(IReadOnlyList<string> fileNames, Guid groupId)
    {
        var group = workspace.Project.Groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null || fileNames.Count == 0) return;

        var affectedGroups = new HashSet<Guid>();
        var nextOrder = workspace.Project.Textures.Values.Count(entry => entry.GroupId == groupId);
        foreach (var fileName in fileNames)
        {
            var entry = GetEntry(fileName);
            if (entry.GroupId == groupId) continue;
            if (entry.GroupId is { } oldGroupId) affectedGroups.Add(oldGroupId);
            AssignToGroup(entry, groupId, nextOrder++);
        }

        foreach (var oldGroupId in affectedGroups)
        {
            if (workspace.Project.Textures.Values.All(entry => entry.GroupId != oldGroupId))
            {
                workspace.Project.Groups.RemoveAll(candidate => candidate.Id == oldGroupId);
            }
        }

        await workspace.SaveAsync();
        RefreshCollections(fileNames[0]);
        Status = $"Added {fileNames.Count:N0} texture(s) to {group.Name}.";
    }

    public async Task SetGroupCollapsedAsync(Guid? groupId, bool collapsed)
    {
        var group = workspace.Project.Groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null || group.IsCollapsed == collapsed) return;
        group.IsCollapsed = collapsed;
        await workspace.SaveAsync();
    }

    partial void OnLeftHideGroupsChanged(bool value)
    {
        LeftOutputView.Refresh();
    }

    partial void OnRightHideGroupsChanged(bool value)
    {
        RightOutputView.Refresh();
    }

    partial void OnLeftOnlyUnfinishedChanged(bool value)
    {
        LeftOutputView.Refresh();
    }

    partial void OnRightOnlyUnfinishedChanged(bool value)
    {
        RightOutputView.Refresh();
    }

    partial void OnLeftPaneSourceChanged(PaneSource value)
    {
        OnPropertyChanged(nameof(IsLeftOriginals));
        OnPropertyChanged(nameof(IsLeftOutput));
    }

    partial void OnRightPaneSourceChanged(PaneSource value)
    {
        OnPropertyChanged(nameof(IsRightOriginals));
        OnPropertyChanged(nameof(IsRightOutput));
    }

    public async Task MoveSelectedItemAsync(int direction)
    {
        if (SelectedOutput is null) return;
        var selectedFileName = SelectedOutput.FileName;
        var selectedEntry = GetEntry(selectedFileName);
        var peers = Outputs.Where(item => GetEntry(item.FileName).GroupId == selectedEntry.GroupId)
            .OrderBy(item => GetEntry(item.FileName).Order)
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var index = peers.FindIndex(item => item.FileName.Equals(selectedFileName, StringComparison.OrdinalIgnoreCase));
        var targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= peers.Count) return;
        (peers[index], peers[targetIndex]) = (peers[targetIndex], peers[index]);
        for (var order = 0; order < peers.Count; order++) GetEntry(peers[order].FileName).Order = order;
        await workspace.SaveAsync();
        RefreshCollections(selectedFileName);
    }

    public async Task RenameSelectedGroupAsync(string name)
    {
        if (SelectedOutput is null || GetEntry(SelectedOutput.FileName).GroupId is not { } groupId) return;
        await RenameGroupAsync(groupId, name);
    }

    public async Task RenameGroupAsync(Guid groupId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var group = workspace.Project.Groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null) return;
        group.Name = name.Trim();
        await workspace.SaveAsync();
        RefreshCollections(SelectedOutput?.FileName);
        Status = $"Renamed group to {group.Name}.";
    }

    public async Task UngroupSelectedAsync()
    {
        if (SelectedOutput is null) return;
        var entry = GetEntry(SelectedOutput.FileName);
        var oldGroupId = entry.GroupId;
        entry.GroupId = null;
        if (oldGroupId is not null && workspace.Project.Textures.Values.Count(value => value.GroupId == oldGroupId) < 2)
        {
            foreach (var remaining in workspace.Project.Textures.Values.Where(value => value.GroupId == oldGroupId)) remaining.GroupId = null;
            workspace.Project.Groups.RemoveAll(group => group.Id == oldGroupId);
        }
        await workspace.SaveAsync();
        RefreshCollections(SelectedOutput.FileName);
        Status = $"Ungrouped {SelectedOutput?.FileName}.";
    }

    public async Task UngroupItemsAsync(IReadOnlyList<TextureItemViewModel> items)
    {
        var outputItems = items.Where(item => item.IsOutput).ToArray();
        if (outputItems.Length == 0) return;
        var affectedGroups = new HashSet<Guid>();
        foreach (var item in outputItems)
        {
            var entry = GetEntry(item.FileName);
            if (entry.GroupId is { } groupId) affectedGroups.Add(groupId);
            entry.GroupId = null;
        }

        foreach (var groupId in affectedGroups)
        {
            if (workspace.Project.Textures.Values.All(entry => entry.GroupId != groupId))
                workspace.Project.Groups.RemoveAll(group => group.Id == groupId);
        }

        await workspace.SaveAsync();
        RefreshCollections(outputItems[0].FileName);
        Status = $"Removed {outputItems.Length:N0} texture(s) from their group.";
    }

    public void OpenInEditor(TextureItemViewModel item)
    {
        var path = item.FullPath;
        if (item.IsOutput && item.HasCopySource && OutputFolder is not null)
            path = Path.Combine(OutputFolder, workspace.Resolve(item.FileName).BaseFileName);
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Status = $"Opened {Path.GetFileName(path)} in the default image editor.";
        }
        catch (Exception exception)
        {
            Status = $"Could not open the image: {exception.Message}";
        }
    }

    public void ToggleView() => IsGridView = !IsGridView;

    public void Dispose()
    {
        originalWatcher?.Dispose();
        outputWatcher?.Dispose();
    }

    private async Task SetOriginalFlagAsync(IReadOnlyList<TextureItemViewModel> items, bool value, bool todoFlag)
    {
        var originals = items.Where(item => !item.IsOutput).ToArray();
        if (originals.Length == 0) return;
        foreach (var item in originals)
        {
            var entry = GetEntry(item.FileName);
            if (todoFlag)
            {
                entry.IsTodo = value;
                item.IsTodo = value;
            }
            else
            {
                entry.IsSeen = value;
                item.IsSeen = value;
            }
        }
        if (value && (todoFlag ? HideTodoOriginals : HideSeenOriginals)) RemoveHiddenFromCurrentPage(originals);
        var flagName = todoFlag ? "TODO" : "seen";
        Status = $"Marked {originals.Length:N0} original texture(s) as {(value ? flagName : $"not {flagName}")}.";
        await workspace.SaveAsync();
    }

    private TextureEntry GetEntry(string fileName)
    {
        if (!workspace.Project.Textures.TryGetValue(fileName, out var entry))
        {
            entry = new TextureEntry { Order = workspace.Project.Textures.Count };
            workspace.Project.Textures[fileName] = entry;
        }
        return entry;
    }

    private static void AssignToGroup(TextureEntry entry, Guid groupId, int order)
    {
        entry.GroupId = groupId;
        entry.Order = order;
        if (entry.IsTodo) entry.IsUnfinished = true;
    }

    private void RefreshCollections(string? selectedFileName = null)
    {
        var selectedName = selectedFileName ?? SelectedOutput?.FileName;
        scannedOriginalFileNames = workspace.ScanOriginals();
        ApplyOriginalFilter();

        var presentations = workspace.Project.Groups.ToDictionary(
            group => group.Id,
            group => new OutputGroupViewModel(
                group.Id,
                group.Name,
                GroupColors[(int)((uint)group.Id.GetHashCode() % GroupColors.Length)],
                !group.IsCollapsed,
                group.Order));
        var ungrouped = new OutputGroupViewModel(null, "Ungrouped", "#F1F1ED", true, int.MaxValue);

        Outputs.Clear();
        foreach (var fileName in workspace.ScanOutputs()
                     .OrderBy(fileName => GetEntry(fileName).GroupId is null)
                     .ThenBy(fileName => GetEntry(fileName).GroupId is { } groupId && presentations.TryGetValue(groupId, out var group)
                         ? group.Name
                         : string.Empty, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(fileName => GetEntry(fileName).Order)
                     .ThenBy(fileName => fileName, StringComparer.OrdinalIgnoreCase))
        {
            var entry = GetEntry(fileName);
            var item = new TextureItemViewModel(fileName, OutputFolder!, true)
            {
                CopySourceFileName = entry.CopySourceFileName,
                Brightness = entry.Brightness,
                IsUnfinished = entry.IsUnfinished,
                OutputGroup = entry.GroupId is { } groupId && presentations.TryGetValue(groupId, out var group) ? group : ungrouped
            };
            item.GroupName = item.OutputGroup.Name;
            item.HasError = item.HasCopySource && !File.Exists(Path.Combine(OutputFolder!, workspace.Resolve(fileName).BaseFileName));
            Outputs.Add(item);
        }

        SelectedOutput = Outputs.FirstOrDefault(item => string.Equals(item.FileName, selectedName, StringComparison.OrdinalIgnoreCase));
        if (SelectedOutput is not null) SelectedTexture = SelectedOutput;
    }

    private ICollectionView CreateOutputView(Predicate<object> filter)
    {
        var source = new CollectionViewSource { Source = Outputs };
        var view = source.View;
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TextureItemViewModel.OutputGroup)));
        view.Filter = filter;
        return view;
    }

    private void ApplyOriginalFilter()
    {
        originalFileNames = scannedOriginalFileNames
            .Where(fileName => !HideSeenOriginals || !GetEntry(fileName).IsSeen)
            .Where(fileName => !HideTodoOriginals || !GetEntry(fileName).IsTodo)
            .ToArray();
        OriginalCount = originalFileNames.Count;
        OriginalPageCount = Math.Max(1, (int)Math.Ceiling(OriginalCount / (double)OriginalPageSize));
        OriginalPage = Math.Min(OriginalPage, OriginalPageCount);
        RefreshOriginalPage();
    }

    private void RefreshOriginalPage()
    {
        Originals.Clear();
        foreach (var fileName in GetCurrentOriginalPage()) Originals.Add(CreateOriginalItem(fileName));
        NotifyOriginalPageState();
    }

    private void RemoveHiddenFromCurrentPage(IReadOnlyCollection<TextureItemViewModel> hiddenItems)
    {
        var hiddenNames = hiddenItems.Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previousPage = OriginalPage;
        originalFileNames = scannedOriginalFileNames
            .Where(fileName => !HideSeenOriginals || !GetEntry(fileName).IsSeen)
            .Where(fileName => !HideTodoOriginals || !GetEntry(fileName).IsTodo)
            .ToArray();
        OriginalCount = originalFileNames.Count;
        OriginalPageCount = Math.Max(1, (int)Math.Ceiling(OriginalCount / (double)OriginalPageSize));
        OriginalPage = Math.Min(OriginalPage, OriginalPageCount);
        if (OriginalPage != previousPage)
        {
            RefreshOriginalPage();
            return;
        }
        foreach (var item in Originals.Where(item => hiddenNames.Contains(item.FileName)).ToArray()) Originals.Remove(item);
        var visibleNames = Originals.Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in GetCurrentOriginalPage().Where(fileName => !visibleNames.Contains(fileName))) Originals.Add(CreateOriginalItem(fileName));
        NotifyOriginalPageState();
    }

    private IEnumerable<string> GetCurrentOriginalPage() => originalFileNames
        .Skip((OriginalPage - 1) * OriginalPageSize)
        .Take(OriginalPageSize);

    private TextureItemViewModel CreateOriginalItem(string fileName)
    {
        var entry = GetEntry(fileName);
        return new TextureItemViewModel(fileName, OriginalFolder!, false) { IsSeen = entry.IsSeen, IsTodo = entry.IsTodo };
    }

    private void NotifyOriginalPageState()
    {
        OnPropertyChanged(nameof(CanShowPreviousOriginals));
        OnPropertyChanged(nameof(CanShowNextOriginals));
    }

    private void BindWatchers()
    {
        originalWatcher?.Dispose();
        outputWatcher?.Dispose();
        originalWatcher = CreateWatcher(OriginalFolder!, false);
        outputWatcher = CreateWatcher(OutputFolder!, true);
    }

    private FileSystemWatcher CreateWatcher(string folder, bool isOutput)
    {
        var watcher = new FileSystemWatcher(folder, "*.png")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        watcher.Changed += (_, args) => QueueChange(args.FullPath, isOutput);
        watcher.Created += (_, args) => QueueChange(args.FullPath, isOutput);
        watcher.Deleted += (_, args) => QueueChange(args.FullPath, isOutput);
        watcher.Renamed += (_, args) => QueueChange(args.FullPath, isOutput);
        return watcher;
    }

    private void QueueChange(string path, bool isOutput)
    {
        lock (pendingChanges) pendingChanges[path] = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            lock (pendingChanges)
            {
                if (!pendingChanges.Remove(path, out var timestamp) || DateTime.UtcNow - timestamp < TimeSpan.FromMilliseconds(250)) return;
            }
            if (isOutput && File.Exists(path))
            {
                try { await workspace.RefreshSourceAsync(Path.GetFileName(path)); }
                catch { }
            }
            await Application.Current.Dispatcher.InvokeAsync(() => RefreshCollections());
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { Status = exception.Message; }
    }
}
