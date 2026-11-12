namespace OrthoStreamXP.Gui;

/// <summary>A downloaded new version, as the update window shows it before the person installs it.</summary>
internal sealed record UpdateOffer(string Version, string? CurrentVersion, IReadOnlyList<NoteBlock> Notes, string ReleasePage);
