using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using Microsoft.Win32;

namespace M4Text.Editor;

public sealed class MainViewModel : INotifyPropertyChanged
{
    // Convenient defaults for this workspace; both are user-overridable in the UI.
    private const string DefaultWork = @"E:\PROJECTS\Github\TengokuArcade\tools\M4Text\work";
    private const string DefaultPic = @"E:\PROJECTS\Github\TengokuArcade\Original ROM\rhytngk\317-0503-jpn.ic3";

    private RomTextService? _service;
    private List<TextEntry> _all = new();

    private ICollectionView _entriesView;
    public ICollectionView EntriesView { get => _entriesView; private set => Set(ref _entriesView, value); }

    public string[] EncodingFilters { get; } = { "All", "English (ascii)", "Japanese (utf8)" };
    public string[] SortModes { get; } = { "File + offset", "Bytes left (asc)", "Encoding", "Modified first" };
    public PadMode[] PadModes { get; } = { PadMode.Auto, PadMode.Null, PadMode.Space };

    // Populated from the loaded file set; "All" plus each ROM file (ic8, ic9, …).
    public ObservableCollection<string> FileFilters { get; } = new() { "All" };

    // Coalesces rapid keystrokes so the (large) view is only re-filtered once the
    // user pauses — keeps the search box responsive over thousands of rows.
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce;

    // Coalesces the global Modified/Over-limit recompute (an O(n) scan over every
    // entry) so editing a cell stays smooth; the status-bar counts settle shortly
    // after the last keystroke instead of rescanning on each one.
    private readonly System.Windows.Threading.DispatcherTimer _countsDebounce;

    public MainViewModel()
    {
        _entriesView = BuildView(_all);

        _searchDebounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); EntriesView.Refresh(); };

        _countsDebounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _countsDebounce.Tick += (_, _) => { _countsDebounce.Stop(); RaiseCounts(); };

        LoadCommand = new RelayCommand(async () => await LoadAsync(), () => !IsBusy);
        RebuildIndexCommand = new RelayCommand(async () => await LoadAsync(forceRescan: true), () => !IsBusy);
        BrowseWorkCommand = new RelayCommand(BrowseWork, () => !IsBusy);
        BrowsePicCommand = new RelayCommand(BrowsePic, () => !IsBusy);
        SaveDecCommand = new RelayCommand(async () => await SaveDecAsync(), () => _service is not null && !IsBusy);
        ExportRomCommand = new RelayCommand(async () => await ExportRomAsync(), () => _service is not null && ModifiedCount > 0 && !IsBusy);
        RevertSelectedCommand = new RelayCommand(RevertSelected, () => SelectedEntry?.IsModified == true);

        // Auto-load on startup so a returning user lands straight on their data
        // (index makes this fast; empty folders bootstrap via decrypt).
        if (Directory.Exists(WorkFolder)) _ = LoadAsync();
    }

    public RelayCommand LoadCommand { get; }
    public RelayCommand RebuildIndexCommand { get; }
    public RelayCommand BrowseWorkCommand { get; }
    public RelayCommand BrowsePicCommand { get; }
    public RelayCommand SaveDecCommand { get; }
    public RelayCommand ExportRomCommand { get; }
    public RelayCommand RevertSelectedCommand { get; }

    private string _workFolder = DefaultWork;
    public string WorkFolder { get => _workFolder; set => Set(ref _workFolder, value); }

    private string _picPath = DefaultPic;
    public string PicPath { get => _picPath; set => Set(ref _picPath, value); }

    private int _minAscii = 4;
    public int MinAscii { get => _minAscii; set => Set(ref _minAscii, value); }

    private int _minJapanese = 2;
    public int MinJapanese { get => _minJapanese; set => Set(ref _minJapanese, value); }

    private string _filterText = string.Empty;
    public string FilterText { get => _filterText; set { if (Set(ref _filterText, value)) { _searchDebounce.Stop(); _searchDebounce.Start(); } } }

    private string _encodingFilter = "All";
    public string EncodingFilter { get => _encodingFilter; set { if (Set(ref _encodingFilter, value)) EntriesView.Refresh(); } }

    private string _fileFilter = "All";
    public string FileFilter { get => _fileFilter; set { if (Set(ref _fileFilter, value)) EntriesView.Refresh(); } }

    private bool _showModifiedOnly;
    public bool ShowModifiedOnly { get => _showModifiedOnly; set { if (Set(ref _showModifiedOnly, value)) EntriesView.Refresh(); } }

    private string _sortMode = "File + offset";
    public string SortMode { get => _sortMode; set { if (Set(ref _sortMode, value)) ApplySort(); } }

    private PadMode _padMode = PadMode.Auto;
    public PadMode SelectedPadMode { get => _padMode; set => Set(ref _padMode, value); }

    private TextEntry? _selectedEntry;
    public TextEntry? SelectedEntry { get => _selectedEntry; set => Set(ref _selectedEntry, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }

    public int ModifiedCount => _all.Count(e => e.IsModified);
    public int OverLimitCount => _all.Count(e => e.IsOverLimit);

    private string _status = "Open a work folder to begin.";
    public string Status { get => _status; set => Set(ref _status, value); }

    private async Task LoadAsync(bool forceRescan = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = forceRescan ? $"Rebuilding index for {WorkFolder}…" : $"Loading {WorkFolder}…";
        try
        {
            string work = WorkFolder, pic = PicPath;
            int minA = MinAscii, minJ = MinJapanese;
            // Read/decrypt and index off the UI thread so the window stays responsive.
            var (service, entries) = await Task.Run(() =>
            {
                var s = RomTextService.Load(work, pic);
                return (s, s.GetEntries(minA, minJ, forceRescan));
            });

            foreach (var e in _all) e.PropertyChanged -= OnEntryChanged;
            foreach (var e in entries) e.PropertyChanged += OnEntryChanged;
            _service = service;
            _all = entries;
            UpdateFileFilters();

            // Rebuild the view from the new list (avoids mutating a live/deferred view).
            EntriesView = BuildView(_all);
            RaiseCounts();
            Status = $"Loaded {_all.Count:N0} strings from {_service.FileNames.Count} file(s) in {WorkFolder}.";
        }
        catch (Exception ex)
        {
            Status = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Refreshes the File combo to "All" + the loaded file names, preserving the
    // current selection when it still exists.
    private void UpdateFileFilters()
    {
        string current = FileFilter;
        FileFilters.Clear();
        FileFilters.Add("All");
        if (_service is not null)
            foreach (var f in _service.FileNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                FileFilters.Add(f);
        FileFilter = FileFilters.Contains(current) ? current : "All";
    }

    // Per-row properties (Used/Left/highlight) update live via their own bindings;
    // only the global counts are deferred, so typing never rescans the full list.
    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        _countsDebounce.Stop();
        _countsDebounce.Start();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(ModifiedCount));
        OnPropertyChanged(nameof(OverLimitCount));
    }

    private void BrowseWork()
    {
        var dlg = new OpenFolderDialog { Title = "Select work folder (contains *.dec)", InitialDirectory = SafeDir(WorkFolder) };
        if (dlg.ShowDialog() == true) { WorkFolder = dlg.FolderName; _ = LoadAsync(); }
    }

    private void BrowsePic()
    {
        var dlg = new OpenFileDialog { Title = "Select PIC key file", Filter = "PIC dump (*.ic3)|*.ic3|All files|*.*", InitialDirectory = SafeDir(Path.GetDirectoryName(PicPath)) };
        if (dlg.ShowDialog() == true) PicPath = dlg.FileName;
    }

    private async Task SaveDecAsync()
    {
        if (IsBusy || _service is null) return;
        IsBusy = true;
        try
        {
            int n = _service.ApplyEdits(_all, SelectedPadMode);
            await Task.Run(() => _service.SaveDec());
            Status = $"Saved {n} edited slot(s) to decrypted files in {WorkFolder}.";
        }
        catch (Exception ex)
        {
            Status = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportRomAsync()
    {
        if (IsBusy || _service is null) return;
        try
        {
            _service.ApplyEdits(_all, SelectedPadMode);
            var dlg = new OpenFolderDialog { Title = "Select output folder for re-encrypted ROM", InitialDirectory = SafeDir(WorkFolder) };
            if (dlg.ShowDialog() != true) return;
            string outFolder = dlg.FolderName, pic = PicPath;

            IsBusy = true;
            Status = "Re-encrypting modified ROM file(s)…";
            var written = await Task.Run(() => _service.ExportEncrypted(pic, outFolder));
            Status = written.Count == 0
                ? "Nothing to export (no modified files)."
                : $"Exported {written.Count} ROM file(s): {string.Join(", ", written.Select(Path.GetFileName))}";
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RevertSelected()
    {
        if (SelectedEntry is { } e) e.Edited = e.Original;
    }

    private static string SafeDir(string? path)
        => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path! : Environment.CurrentDirectory;

    private bool FilterPredicate(object obj)
    {
        if (obj is not TextEntry e) return false;
        if (ShowModifiedOnly && !e.IsModified) return false;
        if (FileFilter != "All" && !string.Equals(e.File, FileFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (EncodingFilter.StartsWith("English") && e.Encoding != "ascii") return false;
        if (EncodingFilter.StartsWith("Japanese") && e.Encoding != "utf8") return false;
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            string f = FilterText;
            bool match = e.Original.Contains(f, StringComparison.OrdinalIgnoreCase)
                      || e.Edited.Contains(f, StringComparison.OrdinalIgnoreCase);
            if (!match) return false;
        }
        return true;
    }

    // Creates a fresh view over the given list with the current filter and sort.
    private ICollectionView BuildView(IList<TextEntry> source)
    {
        var view = new ListCollectionView((System.Collections.IList)source) { Filter = FilterPredicate };
        ApplySortTo(view);
        return view;
    }

    private void ApplySort() => ApplySortTo(EntriesView);

    private void ApplySortTo(ICollectionView view)
    {
        view.SortDescriptions.Clear();
        switch (SortMode)
        {
            case "Bytes left (asc)":
                view.SortDescriptions.Add(new SortDescription(nameof(TextEntry.RemainingBytes), ListSortDirection.Ascending));
                break;
            case "Encoding":
                view.SortDescriptions.Add(new SortDescription(nameof(TextEntry.Encoding), ListSortDirection.Ascending));
                view.SortDescriptions.Add(new SortDescription(nameof(TextEntry.Offset), ListSortDirection.Ascending));
                break;
            case "Modified first":
                view.SortDescriptions.Add(new SortDescription(nameof(TextEntry.IsModified), ListSortDirection.Descending));
                view.SortDescriptions.Add(new SortDescription(nameof(TextEntry.Offset), ListSortDirection.Ascending));
                break;
            default:
                view.SortDescriptions.Add(new SortDescription(nameof(TextEntry.File), ListSortDirection.Ascending));
                view.SortDescriptions.Add(new SortDescription(nameof(TextEntry.Offset), ListSortDirection.Ascending));
                break;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
