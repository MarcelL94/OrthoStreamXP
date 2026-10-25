namespace OrthoStreamXP.Tests;

/// <summary>Settings as a test writes them: the lines of a settings file ("name = value"), read the way the program reads one.</summary>
internal static class TestSettings
{
    public static SettingValues Of(params string[] lines) => SettingsOptions.Schema.Read(lines, "test.ini");
}
