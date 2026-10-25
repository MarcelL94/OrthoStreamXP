using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

/// <summary>
/// The Colours tab's before and after pictures (<see cref="ColourPreview"/>): a photo from the cache, made again off the UI
/// thread whenever a colour setting changes; a change made meanwhile replaces the picture being made.
/// </summary>
internal sealed class ColourPreviewViewModel : ObservableObject, IDisposable
{
    private readonly Func<string?> _cache;
    private readonly Func<ColourAdjustments> _settings;
    private ColourPreview? _preview;
    private string? _previewCache;
    private IReadOnlyList<PreviewPhoto> _photos = [];
    private int _photo;
    private int _version;
    private bool _shown;
    private ImageSource? _before, _after;
    private string _place = "", _notes = "", _status = "";

    /// <param name="cache">The cache folder the settings name now.</param>
    /// <param name="settings">The colour settings the page shows now.</param>
    public ColourPreviewViewModel(Func<string?> cache, Func<ColourAdjustments> settings)
    {
        _cache = cache;
        _settings = settings;
        NextPhoto = new RelayCommand(() =>
        {
            _photo = (_photo + 1) % Math.Max(1, _photos.Count);
            Refresh();
        }, () => _photos.Count > 1);
    }

    public ICommand NextPhoto { get; }

    public ImageSource? Before
    {
        get => _before;
        private set => Set(ref _before, value);
    }

    public ImageSource? After
    {
        get => _after;
        private set => Set(ref _after, value);
    }

    /// <summary>Where the photo is.</summary>
    public string Place
    {
        get => _place;
        private set => Set(ref _place, value);
    }

    /// <summary>What the settings do to it.</summary>
    public string Notes
    {
        get => _notes;
        private set => Set(ref _notes, value);
    }

    /// <summary>Why there is no picture (no photo in the cache yet, ...), or empty.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value)) Raise(nameof(HasPicture));
        }
    }

    public bool HasPicture => Status.Length == 0 && After is not null;

    /// <summary>The Colours tab is shown: the photos are looked for (again if the cache folder changed) and the preview made.</summary>
    public void Show()
    {
        _shown = true;
        Refresh();
    }

    /// <summary>The settings changed: the preview is made again, if it is in use.</summary>
    public void Refresh()
    {
        if (!_shown) return;
        int version = ++_version;
        string? cache = _cache();
        ColourAdjustments settings = _settings();
        if (string.IsNullOrWhiteSpace(cache))
        {
            Show(null, "Choose a cache folder: the preview uses a photo from it.");
            return;
        }
        if (!string.Equals(cache, _previewCache, StringComparison.OrdinalIgnoreCase))
        {
            _preview?.Dispose();
            _preview = new ColourPreview(cache, AppPaths.History);
            _previewCache = cache;
            _photos = [];
            _photo = 0;
        }
        ColourPreview preview = _preview!;
        IReadOnlyList<PreviewPhoto> photos = _photos;
        int index = _photo;
        _ = RenderAsync(version, preview, photos, index, settings);
    }

    private async Task RenderAsync(int version, ColourPreview preview, IReadOnlyList<PreviewPhoto> photos, int index, ColourAdjustments settings)
    {
        (IReadOnlyList<PreviewPhoto> Photos, PreviewPicture? Picture, string? Error) result;
        try
        {
            result = await Task.Run(() =>
            {
                IReadOnlyList<PreviewPhoto> found = photos.Count > 0 ? photos : preview.FindPhotos();
                if (found.Count == 0) return (found, (PreviewPicture?)null, (string?)null);
                // A photo that cannot be shown (being replaced, not DXT1) gives its turn to the next.
                for (int tries = 0; tries < Math.Min(found.Count, 5); tries++)
                    if (preview.Render(found[(index + tries) % found.Count], settings) is { } picture) return (found, picture, null);
                return (found, null, (string?)null);
            }).ConfigureAwait(true);
        }
        // ObjectDisposedException: the cache folder changed while this picture was being made; a newer one replaces it.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
            result = (photos, null, $"The preview could not read the cache: {ex.Message}");
        }
        if (version != _version) return; // the settings changed meanwhile: a newer picture is on its way
        _photos = result.Photos;
        RelayCommand.Refresh();
        if (result.Error is not null) Show(null, result.Error);
        else if (result.Photos.Count == 0) Show(null, "No photos in the cache yet: fly once with OrthoStreamXP and the preview shows one of them.");
        else if (result.Picture is null) Show(null, "The photos in the cache cannot be shown just now. Try another one.");
        else Show(result.Picture, "");
    }

    private void Show(PreviewPicture? picture, string status)
    {
        Before = picture is null ? null : Bitmap(picture.Before, picture.Size);
        After = picture is null ? null : Bitmap(picture.After, picture.Size);
        Place = picture?.Place ?? "";
        Notes = picture is null ? "" : string.Join(" · ", picture.Notes);
        Status = status;
        Raise(nameof(HasPicture));
    }

    private static BitmapSource Bitmap(byte[] rgb, int size)
    {
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Rgb24, null, rgb, size * 3);
        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose() => _preview?.Dispose();
}
