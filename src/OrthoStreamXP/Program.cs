using System.Globalization;
using OrthoStreamXP.Gui;

// The installer's hooks first: on install, update or uninstall Velopack runs them and ends the process here.
// A downloaded update is not installed at start (Velopack's default): only when the person chooses it on the Home page.
Velopack.VelopackApp.Build()
    .SetAutoApplyOnStartup(false)
    .OnBeforeUninstallFastCallback(_ => OrthoStreamXP.Setup.Installation.BeforeUninstall())
    .Run();

// The log, the settings file and the window's English text all use one number and date format, whatever
// the PC's regional settings (WPF needs real culture data, so invariant globalization is not an option).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

// What earlier versions kept beside the program, or in the cache, moves to the app's folder before anything reads it.
OrthoStreamXP.Setup.LegacyNames.MoveSettings(AppContext.BaseDirectory, OrthoStreamXP.Setup.AppPaths.Settings);
OrthoStreamXP.Setup.SettingsDocument settings = OrthoStreamXP.Setup.SettingsDocument.Load(OrthoStreamXP.Setup.AppPaths.Settings);
OrthoStreamXP.Setup.LegacyNames.MoveCache(settings.Get("cache"), OrthoStreamXP.Setup.AppPaths.Logs, OrthoStreamXP.Setup.AppPaths.History);
Directory.CreateDirectory(OrthoStreamXP.Setup.AppPaths.History);
// The regions 1.1.1 installed as OSXP_* take their new names before the window lists them.
OrthoStreamXP.Setup.LegacyPackageNames.Move(settings.Get("base-packages"), settings.Get("cache"), settings.Get("xplane"));

return GuiHost.Run();
