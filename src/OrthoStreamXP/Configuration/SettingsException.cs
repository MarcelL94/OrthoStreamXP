namespace OrthoStreamXP;

/// <summary>A setting the program cannot use: a line the settings file cannot read, or a value out of range.</summary>
internal sealed class SettingsException(string message) : Exception(message);
