using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ModdingTool.Core.Models;
using ModdingTool.Core.Services;

namespace ModdingTool.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int OriginalPageSize = 100;
    private readonly TextureWorkspace workspace = new();
    private readonly Dictionary<string, DateTime> pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> scannedOriginalFileNames = [];
    private IReadOnlyList<string> originalFileNames = [];
    private FileSystemWatcher? originalWatcher;
    private FileSystemWatcher? outputWatcher;

    [ObservableProperty]
    private string? originalFolder;

    [ObservableProperty]
    private string? outputFolder;

    [ObservableProperty]
    private TextureItemViewModel? selectedOutput;

    [ObservableProperty]
    private TextureItemViewModel? selectedTexture;

    [ObservableProperty]
    private bool hideSeenOriginals;

    [ObservableProperty]
    private bool isGridView = true;

    [ObservableProperty]
    private string status = "Choose original and modified texture folders to begin.";

    [ObservableProperty]
    private int originalPage = 1;

    [ObservableProperty]
    private int originalPageCount = 1;

    [ObservableProperty]
    private int originalCount;

    public bool CanShowPreviousOriginals => OriginalPage > 1;

    public bool CanShowNextOriginals => OriginalPage < OriginalPageCount;

    public ObservableCollection<TextureItemViewModel> Originals { get; } = [];

    public ObservableCollection<TextureItemViewModel> Outputs { get; } = [];

    public async Task OpenFoldersAsync(string originalFolder, string outputFolder)
    {
        OriginalFolder = originalFolder;
        OutputFolder = outputFolder;
        await RunAsync(async () =>
        {
            await workspace.OpenAsync(originalFolder, outputFolder);
            RefreshCollections();
            BindWatchers();
            Status = $"Loaded {OriginalCount:N0} originals and {Outputs.Count:N0} modified textures.";
        });
    }

    public void ShowPreviousOriginals()
    {
        if (!CanShowPreviousOriginals)
        {
            return;
        }

        OriginalPage--;
        RefreshOriginalPage();
    }

    public void ShowNextOriginals()
    {
        if (!CanShowNextOriginals)
        {
            return;
        }

        OriginalPage++;
        RefreshOriginalPage();
    }

    public async Task CopyOriginalAsync(TextureItemViewModel item)
    {
        await CopyOriginalsAsync([item]);
    }

    public async Task CopyOriginalsAsync(IReadOnlyList<TextureItemViewModel> items)
    {
        if (OutputFolder is null || items.Count == 0)
        {
            return;
        }

        var existingCount = items.Count(item => File.Exists(Path.Combine(OutputFolder, item.FileName)));
        if (existingCount > 0 &&
            MessageBox.Show(
                $"Replace {existingCount:N0} existing modified texture(s)?",
                "Replace texture",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (outputWatcher is not null)
            {
                outputWatcher.EnableRaisingEvents = false;
            }

            try
            {
                foreach (var item in items)
                {
                    await workspace.CopyOriginalAsync(item.FileName, true);
                }

                RefreshCollections(items[0].FileName);
                Status = $"Copied {items.Count:N0} texture(s).";
            }
            finally
            {
                if (outputWatcher is not null)
                {
                    outputWatcher.EnableRaisingEvents = true;
                }
            }
        });
    }

    public async Task SetSeenAsync(IReadOnlyList<TextureItemViewModel> items, bool isSeen)
    {
        if (items.Count == 0)
        {
            return;
        }

        var originals = items.Where(item => !item.IsOutput).ToArray();
        foreach (var item in originals)
        {
            GetEntry(item.FileName).IsSeen = isSeen;
            item.IsSeen = isSeen;
        }

        if (HideSeenOriginals && isSeen)
        {
            RemoveSeenFromCurrentPage(originals);
        }
        Status = $"Marked {items.Count:N0} original texture(s) as {(isSeen ? "seen" : "unseen")}.";
        await workspace.SaveAsync();
    }

    public void SetHideSeenOriginals(bool hideSeen)
    {
        HideSeenOriginals = hideSeen;
        ApplyOriginalFilter();
    }

    public async Task AssignCopyAsync(TextureItemViewModel source)
    {
        if (SelectedOutput is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await workspace.AssignCopyAsync(SelectedOutput.FileName, source.FileName);
            RefreshCollections(SelectedOutput.FileName);
            Status = $"{SelectedOutput?.FileName} now copies {source.FileName}.";
        });
    }

    public async Task ApplyBrightnessAsync()
    {
        if (SelectedOutput is null || !SelectedOutput.HasCopySource)
        {
            return;
        }

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
        if (SelectedOutput is null)
        {
            return;
        }

        var selectedFileName = SelectedOutput.FileName;
        await RunAsync(async () =>
        {
            await workspace.ClearCopyAsync(selectedFileName);
            RefreshCollections(selectedFileName);
            Status = $"Cleared Copy Image from {selectedFileName}.";
        });
    }

    public async Task GroupAsync(TextureItemViewModel dragged, TextureItemViewModel target, string? newGroupName = null)
    {
        await GroupAsync([dragged], target, newGroupName);
    }

    public async Task GroupAsync(
        IReadOnlyList<TextureItemViewModel> draggedItems,
        TextureItemViewModel target,
        string? newGroupName = null)
    {
        var items = draggedItems.Where(item => item.IsOutput && item != target).ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var targetEntry = GetEntry(target.FileName);
        var group = targetEntry.GroupId is { } targetGroupId
            ? workspace.Project.Groups.FirstOrDefault(candidate => candidate.Id == targetGroupId)
            : null;

        if (group is null)
        {
            group = new TextureGroup
            {
                Name = string.IsNullOrWhiteSpace(newGroupName)
                    ? $"Group {workspace.Project.Groups.Count + 1}"
                    : newGroupName.Trim(),
                Order = workspace.Project.Groups.Count
            };
            workspace.Project.Groups.Add(group);
            targetEntry.GroupId = group.Id;
            targetEntry.Order = 0;
        }

        var nextOrder = workspace.Project.Textures.Values.Count(entry => entry.GroupId == group.Id);
        foreach (var item in items)
        {
            var draggedEntry = GetEntry(item.FileName);
            draggedEntry.GroupId = group.Id;
            draggedEntry.Order = nextOrder++;
        }

        await workspace.SaveAsync();
        RefreshCollections(items[0].FileName);
        Status = $"Grouped {items.Length:N0} texture(s) with {target.FileName}.";
    }

    public async Task RenameSelectedGroupAsync(string name)
    {
        if (SelectedOutput is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var entry = GetEntry(SelectedOutput.FileName);
        var group = workspace.Project.Groups.FirstOrDefault(candidate => candidate.Id == entry.GroupId);
        if (group is null)
        {
            return;
        }

        group.Name = name.Trim();
        await workspace.SaveAsync();
        RefreshCollections(SelectedOutput.FileName);
        Status = $"Renamed group to {group.Name}.";
    }

    public async Task UngroupSelectedAsync()
    {
        if (SelectedOutput is null)
        {
            return;
        }

        var entry = GetEntry(SelectedOutput.FileName);
        var oldGroupId = entry.GroupId;
        entry.GroupId = null;
        if (oldGroupId is not null && workspace.Project.Textures.Values.Count(value => value.GroupId == oldGroupId) < 2)
        {
            foreach (var remaining in workspace.Project.Textures.Values.Where(value => value.GroupId == oldGroupId))
            {
                remaining.GroupId = null;
            }

            workspace.Project.Groups.RemoveAll(group => group.Id == oldGroupId);
        }

        await workspace.SaveAsync();
        RefreshCollections(SelectedOutput.FileName);
        Status = $"Ungrouped {SelectedOutput?.FileName}.";
    }

    public void OpenInEditor(TextureItemViewModel item)
    {
        var path = item.FullPath;
        if (item.IsOutput && item.HasCopySource && OutputFolder is not null)
        {
            path = Path.Combine(OutputFolder, workspace.Resolve(item.FileName).BaseFileName);
        }

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

    private TextureEntry GetEntry(string fileName)
    {
        if (!workspace.Project.Textures.TryGetValue(fileName, out var entry))
        {
            entry = new TextureEntry { Order = workspace.Project.Textures.Count };
            workspace.Project.Textures[fileName] = entry;
        }

        return entry;
    }

    private void RefreshCollections(string? selectedFileName = null)
    {
        var selectedName = selectedFileName ?? SelectedOutput?.FileName;
        scannedOriginalFileNames = workspace.ScanOriginals();
        ApplyOriginalFilter();

        Outputs.Clear();
        var groupNames = workspace.Project.Groups.ToDictionary(group => group.Id, group => group.Name);
        foreach (var fileName in workspace.ScanOutputs()
                     .OrderBy(fileName => GetEntry(fileName).GroupId is null ? 1 : 0)
                     .ThenBy(fileName => GetEntry(fileName).GroupId)
                     .ThenBy(fileName => GetEntry(fileName).Order)
                     .ThenBy(fileName => fileName, StringComparer.OrdinalIgnoreCase))
        {
            var item = new TextureItemViewModel(fileName, OutputFolder!, true);
            var entry = GetEntry(fileName);
            item.CopySourceFileName = entry.CopySourceFileName;
            item.Brightness = entry.Brightness;
            item.GroupName = entry.GroupId is { } groupId && groupNames.TryGetValue(groupId, out var groupName)
                ? groupName
                : "Ungrouped";
            item.HasError = item.HasError ||
                            (item.HasCopySource && !File.Exists(Path.Combine(OutputFolder!, workspace.Resolve(fileName).BaseFileName)));
            Outputs.Add(item);
        }

        SelectedOutput = Outputs.FirstOrDefault(item =>
            string.Equals(item.FileName, selectedName, StringComparison.OrdinalIgnoreCase));
        if (SelectedOutput is not null)
        {
            SelectedTexture = SelectedOutput;
        }
    }

    private void ApplyOriginalFilter()
    {
        originalFileNames = HideSeenOriginals
            ? scannedOriginalFileNames.Where(fileName => !GetEntry(fileName).IsSeen).ToArray()
            : scannedOriginalFileNames;
        OriginalCount = originalFileNames.Count;
        OriginalPageCount = Math.Max(1, (int)Math.Ceiling(OriginalCount / (double)OriginalPageSize));
        OriginalPage = Math.Min(OriginalPage, OriginalPageCount);
        RefreshOriginalPage();
    }

    private void RefreshOriginalPage()
    {
        Originals.Clear();
        foreach (var fileName in originalFileNames
                     .Skip((OriginalPage - 1) * OriginalPageSize)
                     .Take(OriginalPageSize))
        {
            Originals.Add(new TextureItemViewModel(fileName, OriginalFolder!, false)
            {
                IsSeen = GetEntry(fileName).IsSeen
            });
        }

        OnPropertyChanged(nameof(CanShowPreviousOriginals));
        OnPropertyChanged(nameof(CanShowNextOriginals));
    }

    private void RemoveSeenFromCurrentPage(IReadOnlyCollection<TextureItemViewModel> seenItems)
    {
        var seenNames = seenItems.Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previousPage = OriginalPage;
        originalFileNames = scannedOriginalFileNames
            .Where(fileName => !GetEntry(fileName).IsSeen)
            .ToArray();
        OriginalCount = originalFileNames.Count;
        OriginalPageCount = Math.Max(1, (int)Math.Ceiling(OriginalCount / (double)OriginalPageSize));
        OriginalPage = Math.Min(OriginalPage, OriginalPageCount);

        if (OriginalPage != previousPage)
        {
            RefreshOriginalPage();
            return;
        }

        foreach (var item in Originals.Where(item => seenNames.Contains(item.FileName)).ToArray())
        {
            Originals.Remove(item);
        }

        var desiredPage = originalFileNames
            .Skip((OriginalPage - 1) * OriginalPageSize)
            .Take(OriginalPageSize);
        var visibleNames = Originals.Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in desiredPage.Where(fileName => !visibleNames.Contains(fileName)))
        {
            Originals.Add(new TextureItemViewModel(fileName, OriginalFolder!, false)
            {
                IsSeen = GetEntry(fileName).IsSeen
            });
        }

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
        lock (pendingChanges)
        {
            pendingChanges[path] = DateTime.UtcNow;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            lock (pendingChanges)
            {
                if (!pendingChanges.Remove(path, out var timestamp) || DateTime.UtcNow - timestamp < TimeSpan.FromMilliseconds(250))
                {
                    return;
                }
            }

            if (isOutput && File.Exists(path))
            {
                try
                {
                    await workspace.RefreshSourceAsync(Path.GetFileName(path));
                }
                catch
                {
                    // The next filesystem event retries after an editor finishes its atomic save.
                }
            }

            await Application.Current.Dispatcher.InvokeAsync(() => RefreshCollections());
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }
}