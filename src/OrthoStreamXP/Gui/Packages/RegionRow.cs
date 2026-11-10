using System.Diagnostics;
using System.Windows.Input;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

/// <summary>One region of the mesh on the Packages page: whether it is installed, its download, and its buttons.</summary>
internal sealed class RegionRow : ObservableObject
{
    private readonly PackagesViewModel _owner;
    private InstalledRegion? _installed;
    private long _partial;
    private bool _busy, _confirming, _deleting;
    private InstallProgress? _progress;
    private string? _error;
    private (long At, long Bytes)? _rateFrom;
    private double _bytesPerSecond;

    public RegionRow(PackagesViewModel owner, MeshRegion region)
    {
        _owner = owner;
        Region = region;
        Install = new RelayCommand(() => _ = _owner.InstallAsync(this), () => _owner.CanInstall(this));
        Stop = new RelayCommand(_owner.StopInstall, () => Busy);
        AskDelete = new RelayCommand(() => Confirming = true, () => _owner.CanDelete(this));
        CancelDelete = new RelayCommand(() => Confirming = false);
        Delete = new RelayCommand(() => _ = _owner.DeleteAsync(this), () => _owner.CanDelete(this));
    }

    public MeshRegion Region { get; }
    public ICommand Install { get; }
    public ICommand Stop { get; }
    public ICommand AskDelete { get; }
    public ICommand CancelDelete { get; }
    public ICommand Delete { get; }

    public string Name => Region.Name;

    public string Details =>
        $"{CacheInventory.FormatBytes(Region.MeshSize)} mesh" +
        (Region.OverlaysSize > 0 ? $" + {CacheInventory.FormatBytes(Region.OverlaysSize)} roads, forests and buildings" : "") +
        $" · installed as {Region.PackageName}";

    public InstalledRegion? Installed
    {
        get => _installed;
        set
        {
            _installed = value;
            RaiseState();
        }
    }

    /// <summary>Bytes an earlier, stopped install already has.</summary>
    public long Partial
    {
        get => _partial;
        set
        {
            _partial = value;
            RaiseState();
        }
    }

    public bool IsInstalled => Installed is not null;

    public bool UpToDate => Installed is { } i && i.Release == Region.Release;

    public bool Busy
    {
        get => _busy;
        set
        {
            if (!Set(ref _busy, value)) return;
            _rateFrom = null;
            _bytesPerSecond = 0;
            if (!value) _progress = null;
            RaiseState();
        }
    }

    public bool Confirming
    {
        get => _confirming;
        set => Set(ref _confirming, value);
    }

    public bool Deleting
    {
        get => _deleting;
        set
        {
            if (Set(ref _deleting, value)) Confirming = false;
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            Set(ref _error, value);
            Raise(nameof(HasError));
        }
    }

    public bool HasError => Error is not null;

    /// <summary>The install button's word: Install, Resume or Update; hidden when the region is up to date.</summary>
    public string InstallLabel => IsInstalled ? "Update" : Partial > 0 ? "Resume" : "Install";

    public bool ShowInstall => !Busy && !UpToDate;

    public string DeleteQuestion =>
        $"Delete the {Name} mesh ({Region.PackageName}) and its roads, forests and buildings? Downloading it again takes {CacheInventory.FormatBytes(Region.DownloadSize(true))}.";

    public string Status
    {
        get
        {
            if (Busy)
            {
                if (_progress is not { } p) return "Starting…";
                string rate = _bytesPerSecond > 0 ? $" · {_bytesPerSecond / (1 << 20):F1} MB/s" : "";
                return $"{p.Stage}: {CacheInventory.FormatBytes(p.Done)} of {CacheInventory.FormatBytes(p.Total)}{rate}";
            }
            if (Installed is { } installed)
                return UpToDate
                    ? $"Installed · release {installed.Release}{(installed.Overlays ? "" : ", without roads, forests and buildings")}"
                    : $"Update available: release {installed.Release} → {Region.Release}";
            return Partial > 0 ? $"Stopped with {CacheInventory.FormatBytes(Partial)} downloaded: Resume goes on from there" : "Not installed";
        }
    }

    /// <summary>0 to 100.</summary>
    public double Percent => _progress is { Total: > 0 } p ? 100.0 * p.Done / p.Total : 0;

    public void Report(InstallProgress progress)
    {
        _progress = progress;
        long now = Stopwatch.GetTimestamp();
        if (_rateFrom is not { } from) _rateFrom = (now, progress.Done);
        else if (Stopwatch.GetElapsedTime(from.At, now) is { TotalSeconds: >= 3 } span)
        {
            _bytesPerSecond = Math.Max(0, (progress.Done - from.Bytes) / span.TotalSeconds);
            _rateFrom = (now, progress.Done);
        }
        Raise(nameof(Status));
        Raise(nameof(Percent));
    }

    private void RaiseState()
    {
        Raise(nameof(IsInstalled));
        Raise(nameof(UpToDate));
        Raise(nameof(InstallLabel));
        Raise(nameof(ShowInstall));
        Raise(nameof(Status));
        Raise(nameof(Percent));
        RelayCommand.Refresh();
    }
}
