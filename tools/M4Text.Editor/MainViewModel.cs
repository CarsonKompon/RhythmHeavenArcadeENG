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

    // User-hidden junk slots, keyed by "file:offsetHex" so the choice survives a
    // reload/rescan. Persisted next to the .dec files; never affects the ROM bytes.
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);

    private ICollectionView _entriesView;
    public ICollectionView EntriesView { get => _entriesView; private set => Set(ref _entriesView, value); }

    public string[] EncodingFilters { get; } = { "All", "English (ascii)", "Japanese (utf8)" };
    public string[] SortModes { get; } = { "File + offset", "Bytes left (asc)", "Encoding", "Modified first" };
    public PadMode[] PadModes { get; } = { PadMode.Auto, PadMode.Null, PadMode.Space };

    // Populated from the loaded file set; "All" plus each ROM file (ic8, ic9, …).
    public ObservableCollection<string> FileFilters { get; } = new() { "All" };

    // --- Layout / References tab ---------------------------------------------
    // Every place an on-screen string is referenced by a 32-bit pointer, together
    // with the surrounding command-record fields (candidate X/Y/scale). Read-only
    // for now: this surfaces the display-list so layout can be inspected before we
    // commit to editing/relocation semantics (which need Flycast validation).
    public ObservableCollection<ReferenceRow> References { get; } = new();

    // The decrypted code image (ic8) and its ROM↔RAM map are cached after the first
    // scan so re-filtering the references list doesn't re-read the 64 MB file.
    private byte[]? _codeImage;
    private RomMemoryMap? _codeMap;

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
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); RefreshEntriesView(); };

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
        ExportRomCommand = new RelayCommand(async () => await ExportRomAsync(), () => _service is not null && (ModifiedCount > 0 || _service.ModifiedFiles.Count > 0) && !IsBusy);
        RevertSelectedCommand = new RelayCommand(RevertSelected, () => SelectedEntry?.IsModified == true);

        ScanReferencesCommand = new RelayCommand(async () => await LoadReferencesAsync(reread: true), () => !IsBusy);
        SaveFieldEditsCommand = new RelayCommand(SaveFieldEdits, () => _service is not null && !IsBusy);
        SaveReferenceTextCommand = new RelayCommand(SaveReferenceText, () => _service is not null && SelectedReference is not null && !IsBusy);

        SaveChangesCommand = new RelayCommand(SaveChanges, () => _service is not null && !IsBusy);
        LoadChangesCommand = new RelayCommand(LoadChanges, () => _service is not null && !IsBusy);

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
    public RelayCommand ScanReferencesCommand { get; }
    public RelayCommand SaveFieldEditsCommand { get; }
    public RelayCommand SaveReferenceTextCommand { get; }
    public RelayCommand SaveChangesCommand { get; }
    public RelayCommand LoadChangesCommand { get; }
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
    public string EncodingFilter { get => _encodingFilter; set { if (Set(ref _encodingFilter, value)) RefreshEntriesView(); } }

    private string _fileFilter = "All";
    public string FileFilter { get => _fileFilter; set { if (Set(ref _fileFilter, value)) RefreshEntriesView(); } }

    private bool _showModifiedOnly;
    public bool ShowModifiedOnly { get => _showModifiedOnly; set { if (Set(ref _showModifiedOnly, value)) RefreshEntriesView(); } }

    private bool _showHidden;
    public bool ShowHidden { get => _showHidden; set { if (Set(ref _showHidden, value)) RefreshEntriesView(); } }

    private string _sortMode = "File + offset";
    public string SortMode { get => _sortMode; set { if (Set(ref _sortMode, value)) ApplySort(); } }

    private PadMode _padMode = PadMode.Auto;
    public PadMode SelectedPadMode { get => _padMode; set => Set(ref _padMode, value); }

    private TextEntry? _selectedEntry;
    public TextEntry? SelectedEntry { get => _selectedEntry; set => Set(ref _selectedEntry, value); }

    // References-tab region + filter (hex ROM offsets). Defaults cover the rhytngk
    // script block; the user can point the scan at any mapped region.
    private string _refFrom = "0x230000";
    public string RefFrom { get => _refFrom; set => Set(ref _refFrom, value); }

    private string _refTo = "0x260000";
    public string RefTo { get => _refTo; set => Set(ref _refTo, value); }

    private string _refFilterText = string.Empty;
    public string RefFilterText { get => _refFilterText; set => Set(ref _refFilterText, value); }

    private int _referenceCount;
    public int ReferenceCount { get => _referenceCount; private set => Set(ref _referenceCount, value); }

    // Master-row selection drives the field detail grid in the Layout tab.
    private ReferenceRow? _selectedReference;
    public ReferenceRow? SelectedReference { get => _selectedReference; set => Set(ref _selectedReference, value); }

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
        // A (re)load may point at a different work folder, so drop the cached code image.
        _codeImage = null;
        _codeMap = null;
        try
        {
            string work = WorkFolder, pic = PicPath;
            int minA = MinAscii, minJ = MinJapanese;

            // A "Rebuild Index" on an already-loaded folder must NOT throw away work.
            // Reuse the live service (preserving in-memory References-tab byte patches)
            // and carry unsaved Strings-tab edits across the rescan by (file, offset).
            bool rescanInPlace = forceRescan && _service is not null;
            var pending = rescanInPlace
                ? _all.Where(e => e.IsModified)
                       .ToDictionary(e => (e.File, e.Offset), e => e.Edited)
                : new Dictionary<(string, long), string>();

            // Read/decrypt and index off the UI thread so the window stays responsive.
            var existing = _service;
            var (service, entries) = await Task.Run(() =>
            {
                var s = rescanInPlace ? existing! : RomTextService.Load(work, pic);
                return (s, s.GetEntries(minA, minJ, forceRescan));
            });

            // Re-apply preserved edits onto the freshly scanned slots.
            if (pending.Count > 0)
                foreach (var e in entries)
                    if (pending.TryGetValue((e.File, e.Offset), out var edited))
                        e.Edited = edited;

            foreach (var e in _all) e.PropertyChanged -= OnEntryChanged;
            foreach (var e in entries) e.PropertyChanged += OnEntryChanged;
            _service = service;
            _all = entries;
            UpdateFileFilters();

            // Restore the persisted hide-list onto the freshly loaded entries.
            LoadHidden();
            ApplyHidden(_all);

            // Rebuild the view from the new list (avoids mutating a live/deferred view).
            EntriesView = BuildView(_all);
            RaiseCounts();
            OnPropertyChanged(nameof(HiddenCount));
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

    // Scans the decrypted code image for pointers into the chosen text region and
    // projects each into a browsable row. Cached image/map keep re-filters cheap.
    private async Task LoadReferencesAsync(bool reread)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Scanning text references…";
        try
        {
            string work = WorkFolder;
            string filter = RefFilterText;
            long from = ParseHex(RefFrom, 0x230000);
            long to = ParseHex(RefTo, 0x260000);

            var rows = await Task.Run(() =>
            {
                if (reread || _codeImage is null || _codeMap is null)
                {
                    // Prefer the service's live in-memory buffer so field edits (and
                    // text edits) are reflected immediately; fall back to disk.
                    _codeImage = _service?.GetFileBytes("ic8");
                    if (_codeImage is null)
                    {
                        string path = Path.Combine(work, "ic8.dec");
                        if (!File.Exists(path)) return new List<ReferenceRow>();
                        _codeImage = File.ReadAllBytes(path);
                    }
                    _codeMap = RomMemoryMap.Parse(_codeImage);
                }

                string? contains = string.IsNullOrWhiteSpace(filter) ? null : filter;
                // Scan the FULL reference set (unfiltered) so slot bounds — which
                // depend on the next referenced string — are always correct. The text
                // filter is applied only to what we display, never to slot sizing.
                var list = TextReferenceScanner
                    .Scan(_codeImage, _codeMap, from, to, minLen: 2, contains: null, context: 6)
                    .Select(r => new ReferenceRow(r))
                    .ToList();

                // Slot-aware sizing: a record string owns bytes up to the next
                // *referenced* string. Unreferenced continuation lines in between are
                // absorbed, so the whole multi-line message becomes one editable slot.
                var starts = list.Select(r => r.StringOffset).Distinct().OrderBy(x => x).ToList();
                foreach (var r in list)
                {
                    long? next = starts.Where(o => o > r.StringOffset).Cast<long?>().FirstOrDefault();
                    // With no following reference we can't bound the slot safely, so
                    // fall back to the current single string (no expansion offered).
                    long end = next ?? (r.StringOffset + System.Text.Encoding.UTF8.GetByteCount(r.Text) + 1);
                    r.InitSlot(end, JoinSlotMessage(_codeImage, r.StringOffset, end));
                }

                // Apply the display filter after slot sizing (matches the full message).
                if (contains is not null)
                    list = list
                        .Where(r => r.EditText.Contains(contains, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                return list;
            });

            References.Clear();
            foreach (var r in rows) References.Add(r);
            ReferenceCount = rows.Count;
            Status = $"Found {rows.Count:N0} text reference(s) in [0x{from:x}, 0x{to:x}).";
        }
        catch (Exception ex)
        {
            Status = $"Reference scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Decodes a record's full slot [start,end) into an editable multi-line message:
    // NUL bytes (terminator + inter-fragment padding) are dropped so previously
    // fragmented lines rejoin, while embedded '\n' line breaks are preserved.
    private static string JoinSlotMessage(byte[] image, long start, long end)
    {
        int s = (int)start, e = (int)Math.Min(end, image.Length);
        if (e <= s) return string.Empty;
        var bytes = new List<byte>(e - s);
        for (int i = s; i < e; i++)
            if (image[i] != 0) bytes.Add(image[i]); // NUL never appears inside UTF-8/text
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    // Writes the selected record's edited multi-line message back into the whole
    // slot: message bytes, then NUL terminator + padding to the next referenced
    // string. Flows through SaveDec/Export like other edits.
    private void SaveReferenceText()
    {
        if (_service is null || SelectedReference is not { } r) return;
        if (!r.CanExpand) { Status = "This reference has no bounded slot to write into."; return; }
        if (r.IsOverLimit) { Status = $"Text is {r.ByteCount - r.MaxBytes} byte(s) over the {r.MaxBytes}-byte slot."; return; }
        try
        {
            int slotLen = (int)(r.SlotEnd - r.StringOffset);
            var slot = new byte[slotLen]; // zero-filled -> terminator + padding for free
            byte[] msg = System.Text.Encoding.UTF8.GetBytes(ReferenceRow.Normalize(r.EditText));
            Array.Copy(msg, 0, slot, 0, msg.Length);

            _service.PatchBytes("ic8", r.StringOffset, slot);
            if (_codeImage is not null && r.StringOffset + slotLen <= _codeImage.Length)
                Array.Copy(slot, 0, _codeImage, (int)r.StringOffset, slotLen);

            // The old scanner exposed the now-absorbed continuation lines as their
            // own Strings-tab slots. Neutralize any that overlap this slot so a later
            // ApplyEdits (Save .dec/Export) can't re-truncate the message we just wrote.
            foreach (var te in _all.Where(e =>
                         e.File.Equals("ic8", StringComparison.OrdinalIgnoreCase) &&
                         e.Offset >= r.StringOffset && e.Offset < r.SlotEnd))
                te.Edited = te.Original;

            r.CommitText();
            OnPropertyChanged(nameof(ModifiedCount));
            Status = $"Saved message ({msg.Length}/{r.MaxBytes} bytes) at 0x{r.StringOffset:x}. Use Save .dec or Export to persist.";
        }
        catch (Exception ex)
        {
            Status = $"Text save failed: {ex.Message}";
        }
    }

    // Applies every modified record-field edit across all scanned references into
    // the service's in-memory ic8 buffer (and the cached code image), so they flow
    // through SaveDec/Export like text edits. Non-destructive to unrelated bytes.
    private void SaveFieldEdits()
    {
        if (_service is null) return;
        var dirty = References
            .SelectMany(r => r.FieldRows)
            .Where(f => f.IsEditable && f.IsModified)
            .ToList();
        if (dirty.Count == 0) { Status = "No record-field edits to apply."; return; }
        try
        {
            foreach (var f in dirty)
            {
                _service.PatchDword("ic8", f.Rom, f.Value);
                // Keep the cached scan image consistent so a re-scan re-reads the new value.
                if (_codeImage is not null && f.Rom + 4 <= _codeImage.Length)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                        _codeImage.AsSpan((int)f.Rom, 4), f.Value);
            }
            OnPropertyChanged(nameof(ModifiedCount));
            Status = $"Applied {dirty.Count} record-field edit(s) to ic8 (in memory). Use Save .dec or Export to persist.";
        }
        catch (Exception ex)
        {
            Status = $"Field edit failed: {ex.Message}";
        }
    }

    private static long ParseHex(string s, long fallback)
    {
        s = s?.Trim() ?? string.Empty;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out long v) ? v : fallback;
    }

    private static string SafeDir(string? path)
        => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path! : Environment.CurrentDirectory;

    private bool FilterPredicate(object obj)
    {
        if (obj is not TextEntry e) return false;
        if (e.IsHidden && !ShowHidden) return false;
        if (ShowModifiedOnly && !e.IsModified) return false;
        if (FileFilter != "All" && !string.Equals(e.File, FileFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (EncodingFilter.StartsWith("English") && e.Encoding != "ascii") return false;
        if (EncodingFilter.StartsWith("Japanese") && e.Encoding != "utf8") return false;
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            string f = FilterText.Trim();
            // A query starting with "0x" searches by offset (hex) instead of text, so
            // you can jump straight to a slot. Matches on the hex digits as a substring
            // (e.g. "0x24ed" finds 0x0024ed68), case-insensitively.
            if (f.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string digits = f[2..];
                if (digits.Length == 0) return true; // bare "0x" matches everything
                string offHex = e.Offset.ToString("x");
                return offHex.Contains(digits, StringComparison.OrdinalIgnoreCase);
            }

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

    // Raised after the Strings view is re-filtered so the view can keep the current
    // selection visible (e.g. after clearing the search box the previously selected
    // row is scrolled back into view instead of the list jumping to the top).
    public event Action? SelectionShouldScrollIntoView;

    // Re-applies the filter/sort while preserving the current selection, then asks
    // the view to bring it back into view. Refresh() keeps SelectedEntry as long as
    // the item still passes the (now broader/narrower) filter.
    private void RefreshEntriesView()
    {
        var keep = SelectedEntry;
        EntriesView.Refresh();
        if (keep is not null && FilterPredicate(keep))
        {
            SelectedEntry = keep;
            SelectionShouldScrollIntoView?.Invoke();
        }
    }

    // ---- Hide / unhide junk slots -------------------------------------------

    private static string HiddenKey(TextEntry e) => $"{e.File}:{e.Offset:x}";

    public int HiddenCount => _all.Count(e => e.IsHidden);

    // Marks the given entries hidden (or visible) and persists the change. Hidden
    // rows drop out of the list unless "Show hidden" is on, so garbage offsets can be
    // set aside without deleting anything.
    public void SetEntriesHidden(IEnumerable<TextEntry> entries, bool hidden)
    {
        bool changed = false;
        foreach (var e in entries)
        {
            if (e.IsHidden == hidden) continue;
            e.IsHidden = hidden;
            if (hidden) _hidden.Add(HiddenKey(e)); else _hidden.Remove(HiddenKey(e));
            changed = true;
        }
        if (!changed) return;
        SaveHidden();
        OnPropertyChanged(nameof(HiddenCount));
        RefreshEntriesView();
    }

    private string HiddenPath => Path.Combine(WorkFolder, ".m4text-hidden.json");

    private void LoadHidden()
    {
        _hidden.Clear();
        try
        {
            if (File.Exists(HiddenPath) &&
                System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(HiddenPath)) is { } keys)
                foreach (var k in keys) _hidden.Add(k);
        }
        catch { /* a corrupt/absent hide-list just means nothing is hidden */ }
    }

    private void SaveHidden()
    {
        try
        {
            if (!Directory.Exists(WorkFolder)) return;
            File.WriteAllText(HiddenPath, System.Text.Json.JsonSerializer.Serialize(_hidden.OrderBy(k => k)));
        }
        catch { /* non-fatal: hiding is a convenience, not data */ }
    }

    // Applies the persisted hide-list to freshly loaded entries.
    private void ApplyHidden(IEnumerable<TextEntry> entries)
    {
        foreach (var e in entries)
            e.IsHidden = _hidden.Contains(HiddenKey(e));
    }

    // ---- Portable changes file (ROM-free, human-editable JSON) ---------------

    private static readonly System.Text.Json.JsonSerializerOptions PatchJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep 日本語 readable
    };

    // Writes only the edits (and hide-list) to JSON so they can be committed to a repo
    // without any ROM data. Each edit records file+offset+encoding, the pristine
    // Original (so a collaborator's ROM can be validated on load) and the new text.
    private void SaveChanges()
    {
        if (_service is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Save changes (ROM-free JSON)",
            Filter = "M4Text changes (*.m4text.json)|*.m4text.json|JSON (*.json)|*.json",
            FileName = "changes.m4text.json",
            InitialDirectory = SafeDir(WorkFolder),
        };
        if (dlg.ShowDialog() != true) return;

        var patch = new M4TextPatch
        {
            Edits = _all.Where(e => e.IsModified)
                .OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Offset)
                .Select(e => new PatchEdit
                {
                    File = e.File,
                    Offset = $"0x{e.Offset:x}",
                    Encoding = e.Encoding,
                    Original = e.Original,
                    Text = e.Edited,
                })
                .ToList(),
            Hidden = _hidden.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        try
        {
            File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(patch, PatchJson));
            Status = $"Saved {patch.Edits.Count} edit(s) and {patch.Hidden.Count} hidden slot(s) to {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            Status = $"Save changes failed: {ex.Message}";
        }
    }

    // Applies a changes file onto the currently loaded ROM. Edits are matched by
    // file+offset; the stored Original is compared against this ROM so version drift
    // is reported rather than silently mis-applied.
    private void LoadChanges()
    {
        if (_service is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Load changes (ROM-free JSON)",
            Filter = "M4Text changes (*.m4text.json)|*.m4text.json|JSON (*.json)|*.json|All files|*.*",
            InitialDirectory = SafeDir(WorkFolder),
        };
        if (dlg.ShowDialog() != true) return;

        M4TextPatch? patch;
        try
        {
            patch = System.Text.Json.JsonSerializer.Deserialize<M4TextPatch>(File.ReadAllText(dlg.FileName), PatchJson);
        }
        catch (Exception ex)
        {
            Status = $"Load changes failed: {ex.Message}";
            return;
        }
        if (patch is null) { Status = "Load changes failed: not a valid changes file."; return; }

        var byKey = _all.GroupBy(e => (e.File, e.Offset)).ToDictionary(g => g.Key, g => g.First());
        int applied = 0, missing = 0, mismatched = 0;
        foreach (var ed in patch.Edits ?? new())
        {
            if (!TryParseHex(ed.Offset, out long off)) { missing++; continue; }
            if (!byKey.TryGetValue((ed.File, off), out var entry)) { missing++; continue; }
            // Warn (but still apply) when this ROM's slot differs from the one the edit
            // was authored against — usually a different game/region/revision.
            if (!string.IsNullOrEmpty(ed.Original) && !string.Equals(ed.Original, entry.Original, StringComparison.Ordinal))
                mismatched++;
            entry.Edited = ed.Text ?? entry.Original;
            applied++;
        }

        // Merge the hide-list from the file into the current one.
        if (patch.Hidden is { Count: > 0 })
        {
            foreach (var k in patch.Hidden) _hidden.Add(k);
            ApplyHidden(_all);
            SaveHidden();
        }

        RaiseCounts();
        OnPropertyChanged(nameof(HiddenCount));
        RefreshEntriesView();
        Status = $"Applied {applied} edit(s) from {Path.GetFileName(dlg.FileName)}"
               + (missing > 0 ? $", {missing} not found" : "")
               + (mismatched > 0 ? $", {mismatched} differ from this ROM (check version)" : "") + ".";
    }

    private static bool TryParseHex(string? s, out long value)
    {
        value = 0;
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
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

/// <summary>
/// ROM-free, human-editable changes file. Holds only the edits (and the hide-list)
/// so translation work can be committed to a repository without any original,
/// decrypted, or modified ROM bytes. Collaborators clone the repo, supply their own
/// ROM, and load this file to reproduce and extend the edits.
/// </summary>
public sealed class M4TextPatch
{
    public string Format { get; set; } = "m4text-changes";
    public int Version { get; set; } = 1;
    public List<PatchEdit> Edits { get; set; } = new();
    public List<string> Hidden { get; set; } = new();
}

/// <summary>One edited slot. Offset is hex text (e.g. "0x24ed68"); Original is the
/// pristine slot text, kept only to validate against a collaborator's ROM.</summary>
public sealed class PatchEdit
{
    public string File { get; set; } = "";
    public string Offset { get; set; } = "";
    public string Encoding { get; set; } = "";
    public string Original { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
/// A browsable row for the Layout / References tab: one on-screen string plus a
/// compact view of its command-record fields (the candidate X/Y/scale/opcode
/// values that surround the text pointer in the display list).
/// </summary>
public sealed class ReferenceRow : INotifyPropertyChanged
{
    public ReferenceRow(TextReference r)
    {
        Text = r.Text;
        PointerOffset = r.PointerRom;
        StringOffset = r.StringRom;
        StringRam = r.StringRam;
        // Detail rows for the surrounding record words. Pointer/textPtr words are
        // shown read-only; plain integers are editable so layout params (candidate
        // X/Y/width/height) can be tuned in place. RelOffset==0 is the text pointer.
        FieldRows = r.Context
            .Select(f => new FieldRow(f, isTextPointer: f.RelOffset == 0))
            .ToList();
        // Compact one-line dump of the surrounding words (skip the pointer itself),
        // e.g. "int3 · fn:0006f0a4 · int256" — enough to eyeball the layout params.
        Fields = string.Join("  ·  ", r.Context
            .Where(f => f.RelOffset != 0)
            .Select(f => f.TargetRom is long t ? $"fn:{t:x8}" : $"int{(int)f.Value}"));
    }

    public string Text { get; }
    public long PointerOffset { get; }
    public long StringOffset { get; }
    public long StringRam { get; }
    public string Fields { get; }

    /// <summary>Editable/inspectable words of this record for the detail grid.</summary>
    public IReadOnlyList<FieldRow> FieldRows { get; }

    // --- Slot-aware multi-line message editing --------------------------------
    // A record string owns the bytes from its pointer target up to the next
    // *referenced* string; within it the message uses embedded '\n' line breaks
    // and is NUL-terminated/padded. These are filled in by the ViewModel once the
    // full reference set (and code image) are known.

    /// <summary>Exclusive ROM end of this string's slot (next referenced string).</summary>
    public long SlotEnd { get; private set; }

    /// <summary>Max message bytes (slot length minus one reserved terminator).</summary>
    public int MaxBytes { get; private set; }

    private string _originalText = string.Empty;
    private string _editText = string.Empty;

    /// <summary>The full multi-line message (real newlines), NULs collapsed away.</summary>
    public string EditText
    {
        get => _editText;
        set
        {
            if (Set(ref _editText, value)) RaiseTextStats();
        }
    }

    // UTF-8 byte length of the message as it will be written (game uses LF only).
    public int ByteCount => System.Text.Encoding.UTF8.GetByteCount(Normalize(_editText));
    public int RemainingBytes => MaxBytes - ByteCount;
    public bool IsOverLimit => ByteCount > MaxBytes;
    public bool IsTextModified => !string.Equals(Normalize(_editText), Normalize(_originalText), StringComparison.Ordinal);
    public bool CanExpand => SlotEnd > StringOffset;

    /// <summary>Byte budget summary for the UI, e.g. "65 / 115 bytes".</summary>
    public string ByteSummary => $"{ByteCount} / {MaxBytes} bytes";

    /// <summary>Populates the slot bounds and the joined current message text.</summary>
    public void InitSlot(long slotEnd, string fullText)
    {
        SlotEnd = slotEnd;
        MaxBytes = Math.Max(0, (int)(slotEnd - StringOffset) - 1);
        _originalText = fullText;
        _editText = fullText;
        OnPropertyChanged(nameof(EditText));
        OnPropertyChanged(nameof(MaxBytes));
        OnPropertyChanged(nameof(CanExpand));
        RaiseTextStats();
    }

    /// <summary>Marks the current edit as the new baseline after a successful save.</summary>
    public void CommitText()
    {
        _originalText = _editText;
        RaiseTextStats();
    }

    // Normalizes editor input to what the game stores: LF-only line breaks.
    public static string Normalize(string s) => (s ?? string.Empty).Replace("\r", string.Empty);

    private void RaiseTextStats()
    {
        OnPropertyChanged(nameof(ByteCount));
        OnPropertyChanged(nameof(RemainingBytes));
        OnPropertyChanged(nameof(IsOverLimit));
        OnPropertyChanged(nameof(IsTextModified));
        OnPropertyChanged(nameof(ByteSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name!);
        return true;
    }
}

/// <summary>
/// One 32-bit word of a display-list record. Pointer and text-pointer words are
/// read-only; integer words expose an editable <see cref="ValueText"/> (decimal
/// or 0x-hex) whose change is tracked for write-back into the .dec.
/// </summary>
public sealed class FieldRow : INotifyPropertyChanged
{
    private readonly uint _original;
    private uint _value;

    public FieldRow(RecordField f, bool isTextPointer)
    {
        Rom = f.Rom;
        RelOffset = f.RelOffset;
        IsPointer = f.TargetRom is not null;
        IsTextPointer = isTextPointer;
        _original = f.Value;
        _value = f.Value;
        // Pointer/text words are structural — never edited from this grid.
        IsEditable = !IsPointer && !IsTextPointer;
        Kind = isTextPointer ? "text" : f.TargetRom is long t ? $"fn:{t:x8}" : "int";
    }

    public long Rom { get; }
    public int RelOffset { get; }
    public bool IsPointer { get; }
    public bool IsTextPointer { get; }
    public bool IsEditable { get; }
    public string Kind { get; }

    // "+12", "-8", etc. — signed word position relative to the text pointer.
    public string Rel => RelOffset >= 0 ? $"+{RelOffset}" : RelOffset.ToString();
    public string RomHex => $"0x{Rom:x6}";
    public uint Value => _value;
    public bool IsModified => _value != _original;

    /// <summary>Editable text form: accepts decimal or 0x-prefixed hex.</summary>
    public string ValueText
    {
        get => _value.ToString();
        set
        {
            if (!IsEditable) return;
            string s = (value ?? string.Empty).Trim();
            bool ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint v)
                : uint.TryParse(s, out v);
            if (!ok || v == _value) return;
            _value = v;
            OnPropertyChanged(nameof(ValueText));
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(IsModified));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
