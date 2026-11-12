namespace OrthoStreamXP.Gui;

/// <summary>
/// The SimBrief prefetch's explanations, shared by the setup's SimBrief step and the Settings page's SimBrief tab so
/// both say the same.
/// </summary>
internal static class SimBriefText
{
    /// <summary>What the download limit (<c>simbrief-download-limit</c>) does, in one line.</summary>
    public const string LimitHelp =
        "The most the SimBrief prefetch may download per streaming session. When it is reached, nothing breaks: the rest loads as usual while you fly. Your own flying never counts against it.";

    /// <summary>Why there is a limit at all, and how to choose it.</summary>
    public const string LimitWhy =
        "Why a limit? The photos come from free map services (Bing, Esri). They are made for looking at maps, not for bulk downloads, and a program that downloads too much can be slowed down or blocked, which would hit every OrthoStreamXP user, not only you. " +
        "Building scenery ahead means many photos before you need any of them, so the prefetch gets a budget. It also protects a metered or slow connection.\n" +
        "Which value? 1000 MB (the default) is meant for a typical flight at High or Ultra. " +
        "Choose less (300-500 MB) on a metered or mobile connection. Choose more (2000-3000 MB) with Extra, which downloads about four times as much near airports. " +
        "If the log says \"the download budget ... is used up\" on your flights, raise it a step. No limit is not recommended.";

    /// <summary>The limits offered in the setup; a value set by hand in the file is added to them.</summary>
    public static IReadOnlyList<Choice> LimitChoices(int? current)
    {
        List<Choice> choices =
        [
            new("300 MB: a metered or mobile connection", "300"),
            new("500 MB: a slow connection", "500"),
            new($"{MountSettings.DefaultSimBriefDownloadLimitMb} MB: a typical flight (recommended)", MountSettings.DefaultSimBriefDownloadLimitMb.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("3000 MB: Extra quality", "3000"),
            new("No limit (not recommended)", "0"),
        ];
        string? own = current?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (own is not null && choices.All(c => c.Value != own)) choices.Insert(0, new Choice($"{own} MB (set in the settings file)", own));
        return choices;
    }
}
