# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OrthoStreamXP: a Windows-only WPF app (.NET 10, `net10.0-windows`) that mounts a read-only WinFsp virtual file system over the user's existing X-Plane 12 scenery packages and builds photo-scenery DDS textures on demand from online aerial imagery (Bing, Esri, Geoportal PL) as X-Plane reads them. Users must start it before X-Plane. See `README.md` for the user-facing description.

## Commands

Requires the .NET 10 SDK and WinFsp **with its Developer part** (the build references `%ProgramFiles(x86)%\WinFsp\bin\winfsp-msil.dll`; override with `-p:WinFspDir=...`).

```powershell
dotnet build OrthoStreamXP.slnx
dotnet test --solution OrthoStreamXP.slnx                       # Microsoft.Testing.Platform runner (global.json), xUnit v3 style
dotnet test --solution OrthoStreamXP.slnx --filter-method "*Name*"   # single test (MTP filter syntax; verify against installed runner)
.\build-installer.ps1 [-Version x.y.z]                          # publish + Velopack pack -> artifacts\releases (MSI, portable zip)
```

- `TreatWarningsAsErrors` and `AnalysisMode=Recommended` are on (`Directory.Build.props`): warnings fail the build.
- `MountSmokeTests` performs a real WinFsp mount with synthetic imagery; it is skipped locally if WinFsp is missing, but always runs when the `CI` env var is set. CI (`.github/workflows/ci.yml`) runs on Windows and installs WinFsp.
- Tests use `FakeTileServer` / `TempTree` helpers instead of the network.

## Branching

Don't commit anything to the repo

## Architecture

The program is the WPF window only (`Program.cs` runs the Velopack install/update/uninstall hooks, forces the Invariant culture for logs/settings, then starts `GuiHost`); there is no command-line interface and arguments are ignored. Settings live in `orthostreamxp.ini`: `SettingsOptions.Schema` (a `SettingsSchema`) declares every known setting, reads the file's `name = value` lines into `SettingValues`, and `MountSettings.FromValues` validates them (a bad one is a `SettingsException`).

Source layout under `src/OrthoStreamXP/`. Sub-folders are for navigation only: **namespaces stay at the top-level folder** (`OrthoStreamXP.Scenery`, `OrthoStreamXP.Gui`, ...; the root-level folders `Configuration`, `Mounting` and `Infrastructure` use `OrthoStreamXP`), so moving a file never needs `using`/XAML changes.

- `Configuration/` — `SettingsOptions`/`SettingsSchema` (the known settings and the ini reader), `SettingValues` (typed access), `SettingsException`; `MountSettings` turns the values into a mount's settings.
- `Mounting/` — `MountSession`: each scenery package gets its own `PackageMount` (overlay + build scheduler + load-ahead), sharing one downloader/encoder and the aircraft-position listener. `MountStatus` is the live snapshot for the UI.
- `Infrastructure/` — `Log`, `AtomicFile`.
- `Gui/` — `App`, `MainWindow`, `GuiHost`, `MountController` (drives a `MountSession` from the window) at the root; `Views/` (XAML + code-behind: Home, Setup, Settings, Cache, Log, About), `ViewModels/`, `Settings/` and `Home/` (the types those pages show), `Mvvm/` (`ObservableObject`, `RelayCommand`, converters), `Map/` (cities, flight charts).
- `Scenery/` — the runtime streaming pipeline:
  - `FileSystem/`: `SceneryFileSystem` (WinFsp `FileSystemBase`) → `SceneryOverlay` (resolves a path to a backing file, a streamed photo texture, or an in-memory terrain definition); `SceneryPacks`, `TerrainDefinitions`.
  - `Streaming/`: `TextureStreamer` and its read/stand-in/build types, `TextureScheduler`, `TextureCatalog`, `ReadLog`.
  - `Paging/`: `PagingModel`, `PagingMeasurements`, `PagingCalibration`.
  - `Prefetch/`: `LoadAheadPrefetcher`, `PrefetchPlanner`, `MipWarmer`.
  - `Aircraft/`: `AircraftPositionListener` (X-Plane UDP), `ViewTracker`, `SceneryLoads`.
  - `Colour/`: `ColourMatcher` (evens patchwork), `SeasonClock` (seasonal tint by simulated date/location).
- `Textures/` — imagery to DDS: `TextureBuilder` (fetch → stitch → resize → mipmaps → BC1 encode → atomic write; own encoder, SkiaSharp is decode-only), `TextureQuality`, `Images` at the root; `Tiles/` (`TileSource`/`TileStore`: download + cache), `Dds/`, `Scheduling/` (priority gates), `Seasons/`, `Colour/` (`ColourMatch`, `Exposure`, `ColourTable` = the shared BC1 lookup-table recolouring, `PhotoColours` = match → season → exposure as X-Plane reads, also used by the Settings preview).
- `Setup/` — `Settings/` (`SettingsDocument`, ini under `%LOCALAPPDATA%\OrthoStreamXP`, `QualityPreset`, `ConnectionTest`), `Cache/` (inventory, areas, eviction), `Installation/` (`AppPaths`, `AppUpdates`, `LegacyNames` migrating older settings/cache locations, `SystemCheck`, Velopack hooks).
- `Tuning/` — flight reports, facts and the preset/setting recommendations.
- Logs go to `%LOCALAPPDATA%\OrthoStreamXP\logs`.


## C# / .NET Engineering Guidelines

### General principles

- Inspect the existing solution and conventions before making changes.
- Prefer object-oriented design, composition, and explicit dependencies.
- Leverage the rule one class - one responsibility. The classes should be small and easy to understand.
- Avoid static state and static service classes unless justified by the framework or an established project convention and performance.
- Follow SOLID where it improves maintainability; avoid unnecessary abstractions and over engineering.
- Make the smallest change that correctly solves the task.
- Do not introduce dependencies without explaining why they are needed.

### C# standards

- Follow the project's TargetFramework and LangVersion.
- Prefer modern, idiomatic C# supported by the project.
- Enable and respect nullable reference types where configured.
- Use async/await for asynchronous I/O.
- Propagate CancellationToken through cancellable async operations.
- Dispose resources correctly; use IDisposable or IAsyncDisposable
  when appropriate.
- Handle exceptions intentionally. Do not silently swallow exceptions.
- Avoid unnecessary allocations, reflection, and LINQ complexity in performance-sensitive code.

### Architecture

- Prefer constructor injection for external dependencies.
- Define clear interfaces at genuine architectural boundaries.
- Keep public APIs small, explicit, and backward-compatible where required.
- Follow existing naming, folder structure, and dependency direction.
- Do not refactor unrelated code as part of a feature or bug fix.

### Testing

- Add or update tests for new behavior and bug fixes.
- Cover edge cases and failure paths where relevant.
- Follow the test framework already used by the solution.
- Never change a test merely to hide a genuine regression.
- Do not claim tests passed unless they were actually executed.

### Workflow

1. Inspect the relevant code and existing tests.
2. Summarize the intended change and any important trade-offs.
3. Implement the smallest maintainable solution.
4. Build the affected projects.
5. Run relevant tests.
6. Review the diff for regressions, unnecessary changes, and security issues.
7. Report changed files, validation results, and any remaining risks.

### Tool usage

- Use the repository's existing SDK, NuGet configuration, and build tools.
- Prefer existing project scripts and documented commands.
- Do not modify generated files unless explicitly required.
- Ask before destructive operations, dependency upgrades, or broad
  architectural changes.
- Never expose secrets, tokens, or credentials in logs or source code.

