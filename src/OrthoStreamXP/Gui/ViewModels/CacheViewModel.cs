using System.Globalization;
using System.Windows.Input;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

/// <summary>One folder of the cache on the Cache page, with its own delete confirmation.</summary>
internal sealed class CacheItem : ObservableObject
{
    private readonly CacheViewModel _owner;
    private bool _confirming, _deleting;

    public CacheItem(CacheViewModel owner, CacheEntry entry, bool inUse)
    {
        _owner = owner;
        Entry = entry;
        InUse = inUse;
        Open = new RelayCommand(() => Shell.Open(entry.Path));
        AskDelete = new RelayCommand(() => Confirming = true, () => !InUse && !Deleting);
        CancelDelete = new RelayCommand(() => Confirming = false);
        Delete = new RelayCommand(() => _ = _owner.DeleteAsync(this), () => !InUse && !Deleting);
    }

    public CacheEntry Entry { get; }
    public bool InUse { get; }
    public ICommand Open { get; }
    public ICommand AskDelete { get; }
    public ICommand CancelDelete { get; }
    public ICommand Delete { get; }

    public string Glyph => Entry.Kind switch
    {
        CacheEntryKind.Base => "", // World
        CacheEntryKind.Logs => "",
        CacheEntryKind.Other => "",
        _ => "",
    };

    public string Title
    {
        get
        {
            if (Entry.Kind == CacheEntryKind.Logs) return "Logs";
            if (Entry.Kind == CacheEntryKind.Base) return "Low-zoom base";
            if (Entry.Setup is not { } setup) return $"{Entry.Name} (older layout)";
            if (QualityPreset.ForLevel(setup.Level) is { } preset) return preset.Name;
            return $"Level {setup.Level.ToString("+0;−0;0", CultureInfo.InvariantCulture)}";
        }
    }

    public string Details
    {
        get
        {
            string used = Entry.LastWriteUtc is { } last ? $" · last changed {last.ToLocalTime():d MMM yyyy}" : "";
            return Entry.Kind switch
            {
                CacheEntryKind.Logs => $"The log files of earlier runs{used}",
                CacheEntryKind.Base => $"Coarse pictures far scenery is drawn from at once, for every setup · {Entry.Files.ToString("N0", CultureInfo.CurrentCulture)} tiles{used}",
                CacheEntryKind.Other => "From before each setup got its own folder: OrthoStreamXP no longer uses it. Delete it, or move it into the setup folder it was built with.",
                _ => $"Most areas {Entry.Setup!.Zl16Size} px, airports {Entry.Setup.Zl18Size} px · {Entry.Files.ToString("N0", CultureInfo.CurrentCulture)} files{used}",
            };
        }
    }

    public string Size => CacheInventory.FormatBytes(Entry.Bytes);

    public string DeleteQuestion => Entry.Kind switch
    {
        CacheEntryKind.Logs => $"Delete {Size} of logs?",
        CacheEntryKind.Base => $"Delete {Size}? At the start of a flight, far scenery is a plain colour until these are downloaded again.",
        _ => $"Delete {Size}? These textures are downloaded again when you next fly there.",
    };

    public bool Confirming
    {
        get => _confirming;
        private set => Set(ref _confirming, value);
    }

    public bool Deleting
    {
        get => _deleting;
        set
        {
            Set(ref _deleting, value);
            if (value) Confirming = false;
            RelayCommand.Refresh();
        }
    }
}

/// <summary>The Cache page: what each setup takes on disk, and deleting what is no longer wanted.</summary>
internal sealed class CacheViewModel : ObservableObject, IPage
{
    private readonly MainViewModel _main;
    private IReadOnlyList<CacheItem> _items = [];
    private bool _scanning;
    private string _root = "", _summary = "";
    private string? _error;
    private int _scanNumber;

    public CacheViewModel(MainViewModel main)
    {
        _main = main;
        Map = new CacheMapViewModel(this);
        Refresh = new RelayCommand(() => _ = ScanAsync(), () => !Scanning);
        OpenRoot = new RelayCommand(() => Shell.Open(Root), () => Directory.Exists(Root));
        ChangeFolder = new RelayCommand(() =>
        {
            _main.Settings.Tab = SettingsViewModel.FoldersTab;
            _main.Show(_main.Settings);
        });
    }

    public ICommand Refresh { get; }
    public ICommand OpenRoot { get; }
    public ICommand ChangeFolder { get; }

    public CacheMapViewModel Map { get; }

    /// <summary>OrthoStreamXP is running: areas are not deleted while X-Plane may read them.</summary>
    public bool InUse => _main.Mount.State != MountState.Stopped;

    public string? InUseNote => InUse ? "Stop OrthoStreamXP to delete areas: X-Plane may be reading them." : null;

    /// <summary>The cache limit's choices (cache-limit in orthostreamxp.ini, in GB).</summary>
    public IReadOnlyList<Choice> LimitChoices { get; } =
    [
        new Choice("No limit", "0"),
        new Choice("25 GB", "25"),
        new Choice("50 GB", "50"),
        new Choice("100 GB", "100"),
        new Choice("200 GB", "200"),
        new Choice("500 GB", "500"),
        new Choice("1 TB", "1024"),
    ];

    public Choice? Limit
    {
        get
        {
            string value = SettingsDocument.Load(SettingsDocument.DefaultPath).Get("cache-limit") ?? "0";
            return LimitChoices.FirstOrDefault(c => c.Value == value) ?? new Choice($"{value} GB", value);
        }
        set
        {
            if (value is null) return;
            try
            {
                SettingsDocument settings = SettingsDocument.Load(SettingsDocument.DefaultPath);
                settings.Set("cache-limit", value.Value == "0" ? null : value.Value);
                settings.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = $"The cache limit could not be saved: {ex.Message}";
            }
            Raise();
            Raise(nameof(LimitText));
        }
    }

    public string LimitText
    {
        get
        {
            string used = $"The photo textures take {CacheInventory.FormatBytes(_areaBytes)} now.";
            return Limit?.Value is null or "0"
                ? $"{used} The cache keeps growing as you fly new places."
                : $"{used} When they take more, OrthoStreamXP removes the areas flown longest ago as it starts or stops, down to 90% of the limit. The low-zoom base and the logs stay.";
        }
    }

    private long _areaBytes;

    internal void ShowError(string message) => Error = message;

    internal Task RescanAsync() => ScanAsync();

    public string Root
    {
        get => _root;
        private set => Set(ref _root, value);
    }

    public IReadOnlyList<CacheItem> Items
    {
        get => _items;
        private set
        {
            Set(ref _items, value);
            Raise(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => !Scanning && Items.Count == 0;

    public bool Scanning
    {
        get => _scanning;
        private set
        {
            Set(ref _scanning, value);
            Raise(nameof(IsEmpty));
            RelayCommand.Refresh();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public void OnShown() => _ = ScanAsync();

    private async Task ScanAsync()
    {
        // A newer scan (the page shown again, a delete) replaces this one's result.
        int number = ++_scanNumber;
        Root = SettingsDocument.Load(SettingsDocument.DefaultPath).Get("cache") ?? "";
        Scanning = true;
        Summary = "Measuring…";
        Error = null;
        try
        {
            string root = Root;
            var (entries, areas) = root.Length == 0 ? ([], []) : await Task.Run(() =>
                (CacheInventory.Scan(root), CacheAreas.Scan(root, AreaUsage.Load(root)))).ConfigureAwait(true);
            if (number != _scanNumber) return;
            _areaBytes = areas.Sum(a => a.Bytes);
            Map.Show(areas, root);
            foreach (string name in new[] { nameof(Limit), nameof(LimitText), nameof(InUse), nameof(InUseNote) }) Raise(name);
            // By name: the same folder can be written with a short (8.3) or a long path.
            string? inUse = _main.Mount.Running?.Textures.FolderName;
            bool running = _main.Mount.IsRunning;
            Items = [.. entries.Select(e => new CacheItem(this, e,
                (running && e.Name == inUse) || (running && e.Kind is CacheEntryKind.Logs or CacheEntryKind.Base)))];
            long total = entries.Sum(e => e.Bytes);
            string free = SystemCheck.FreeBytes(root) is { } bytes ? $" · {CacheInventory.FormatBytes(bytes)} free on this drive" : "";
            Summary = root.Length == 0 ? "No cache folder is set yet." : $"{CacheInventory.FormatBytes(total)} in use{free}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"The cache folder could not be read: {ex.Message}";
        }
        finally
        {
            if (number == _scanNumber) Scanning = false;
        }
    }

    internal async Task DeleteAsync(CacheItem item)
    {
        item.Deleting = true;
        try
        {
            await Task.Run(() => Directory.Delete(item.Entry.Path, recursive: true)).ConfigureAwait(true);
            Log.Info($"Deleted {item.Entry.Path} ({item.Size}) from the cache");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"{item.Title} could not be deleted completely: {ex.Message}";
        }
        await ScanAsync().ConfigureAwait(true);
    }
}
