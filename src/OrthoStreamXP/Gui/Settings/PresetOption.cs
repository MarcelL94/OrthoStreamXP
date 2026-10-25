using System.Globalization;
using System.Windows.Input;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Gui;

/// <summary>A quality preset as a card on the Settings page and in the setup.</summary>
internal sealed class PresetOption(QualityPreset preset, Action<QualityPreset> choose) : ObservableObject
{
    private bool _isSelected;

    public QualityPreset Preset { get; } = preset;
    public string Title => Preset.Name;
    public string Summary => Preset.Summary;
    /// <summary>The preset the evidence recommends for this PC (<see cref="Tuning.PresetAdvisor"/>).</summary>
    public bool IsRecommended => Preset == PresetMemory.Recommended.Preset;

    /// <summary>Why it is recommended, under its badge.</summary>
    public string? RecommendedReason => IsRecommended ? PresetMemory.Recommended.Reason : null;

    public string Details =>
        $"Zoom {Preset.Zl16Zoom} in most areas, {Preset.Zl18Zoom} near airports · about {Preset.MetresPerPixel:0.#} m per pixel · a new area loads in {Preset.FirstVisit}";

    public string BestFor => $"Best for: {Preset.BestFor}";

    /// <summary>The photo textures' graphics memory at this preset, as flown on this PC (or on the test flight).</summary>
    public string MemoryText
    {
        get
        {
            long bytes = PresetMemory.Estimate.BytesAt(Preset.Size);
            string source = PresetMemory.Estimate == Tuning.GraphicsMemory.TestFlight ? "on a test flight" : "on your last long flight";
            return $"Photo textures take about {CacheInventory.FormatBytes(bytes)} of graphics memory at the most ({source}).";
        }
    }

    /// <summary>
    /// A warning when the photo textures and X-Plane's own needs (about 5 GB) would be more than the graphics card has.
    /// </summary>
    public string? MemoryWarning
    {
        get
        {
            if (PresetMemory.Card is not { } card) return null;
            long bytes = PresetMemory.Estimate.BytesAt(Preset.Size);
            return bytes + Tuning.GraphicsMemory.XPlaneOwnBytes <= card ? null
                : $"Your graphics card has {CacheInventory.FormatBytes(card)}. With X-Plane's own needs (about 5 GB), {Preset.Name} may run it short: "
                  + "X-Plane then stutters, or lowers the detail of its textures. A smaller preset is safer.";
        }
    }

    /// <summary>The figures read again (a flight measured them since).</summary>
    public void RefreshMemory()
    {
        Raise(nameof(MemoryText));
        Raise(nameof(MemoryWarning));
        Raise(nameof(IsRecommended));
        Raise(nameof(RecommendedReason));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value) choose(Preset);
        }
    }

    /// <summary>Marks it chosen without choosing it again (when loading).</summary>
    public void Show(bool selected)
    {
        _isSelected = selected;
        Raise(nameof(IsSelected));
    }
}
