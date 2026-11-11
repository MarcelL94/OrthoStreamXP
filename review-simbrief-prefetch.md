# Review: `feature/simbrief-prefetch`

Reviewed at commit `1f287f3` (15 commits on top of `1f5fb20`, ~1,800 lines added). Static review only: not built or
tested (the project needs Windows + WinFsp). Line numbers refer to `1f287f3`.

Overall the design is sound: one OFP fetch feeds both the departure prefetch at mount start (the 3×4 DSF tiles,
each texture to the mip X-Plane wants from the gate) and the ground pre-warm (arrival, alternate, climb) that is
cancelled on the takeoff roll. Reading the DSF TERT table for the exact texture list is a good approach.

## Problems worth fixing

### 1. Ground pre-warm requests block real load-ahead requests (priority inversion)

- `Scenery/Prefetch/MipWarmer.cs:101` (`OnEntryAdded` → `Add`, line 224) and `:108` (`OnMoved`) both use
  `_waiting.TryAdd`.
- If a ground request (`Ground = true`, priority `FetchPriority.Background + 1 + phase`) is already waiting for a
  texture that X-Plane now actually loads, the load-ahead request is silently dropped. The texture stays at the
  lowest priority, with the ground request's mip/distance.
- On the takeoff roll `CancelGround()` (`:149`) removes it entirely; it only comes back with the next `OnMoved`.
- **Suggested fix:** in `Add` and `OnMoved`, replace a waiting request when `existing.Ground` is true
  (instead of `TryAdd`).

### 2. A stale OFP prefetches the wrong airport

- `Scenery/Flight/DeparturePrefetch.cs:35`: the `StalePlanKm` check only works when `aircraft()` has a position.
  At mount start X-Plane is usually not running yet, so it is null and the check is skipped.
- Result: yesterday's OFP (different departure) queues thousands of textures (e.g. 3,554 at EDDV) at the wrong
  place, using bandwidth and build capacity.
- **Suggested fixes (either or both):**
  - Parse `params.time_generated` (Unix seconds) in `SimBriefClient.Parse` and ignore OFPs older than a few hours.
  - When the first real (non-inferred) position arrives and it is > `StalePlanKm` from the origin, cancel the
    departure requests (like `CancelGround`, with their own flag).

### 3. The flight plan is fetched only once, at mount start

- `Mounting/MountSession.cs:226`: `client.LatestPlanAsync(user, stop)` runs once.
- If the user starts OrthoStreamXP first and generates the OFP afterwards, neither the departure prefetch nor the
  ground pre-warm runs for that flight. A second flight in the same session also keeps the old plan
  (`GroundWatch` goes to `Done` and never restarts).
- **Suggested fix:** re-fetch the OFP when `GroundWatch` reports `Start` (the aircraft has been parked 30 s), or at
  least when the cached plan's origin is far from the parked aircraft. That is one cheap HTTP call per flight.

### 4. The ground pre-warm task is fire-and-forget

- `Scenery/Flight/GroundPrewarm.cs:79`: `_ = Task.Run(...)`, not tracked and not awaited in
  `MountSession.StopAsync` (unlike `_departure`).
- Only `IOException` / `UnauthorizedAccessException` are caught. Stopping while it is queuing can touch a disposed
  `MipWarmer` (its `SemaphoreSlim` → `ObjectDisposedException`), and any other exception goes unobserved and is
  never logged.
- **Suggested fix:** keep the task (e.g. `GroundPrewarm.Running`), await it in `StopAsync` next to `_departure`,
  pass the session's stop token into `Queue`, and log unexpected exceptions.

## Minor

- **The camera's speed drives the stop.** `GroundPrewarm.cs:76` passes the camera's motion as the ground speed.
  Moving the camera fast in the outside view (> `RollingKt` = 40 kt) can stop the pre-warm before the real roll.
- **Duplicate log line.** If the aircraft starts rolling while `Queue` is still running, `Cancel()` runs twice
  (`:89` and the `Stop` branch), so "the aircraft is rolling for takeoff" is logged twice.
- **Duplicate constants.** `DeparturePrefetch.StalePlanKm` (`DeparturePrefetch.cs:28`) and
  `GroundWatch.NearDepartureKm` (`GroundPrewarm.cs:14`) are both 50 km for the same idea. Use one constant.
- **Magic number.** `1.943844` (m/s → kt) at `GroundPrewarm.cs:76`: use a named constant or an existing helper.
- **All-digit usernames.** `SimBriefClient.cs:31` sends an all-digit username as `userid` (Pilot ID). Probably
  fine, but say so in the setup / ini help text.
- **Fully qualified type.** `System.Collections.Concurrent.ConcurrentDictionary` in `MipWarmer.cs`: add a `using`.

## Branch scope

These changes are unrelated to SimBrief and would read better as their own PRs:

- Remembering the paging model's k across starts (`PagingModel.Remember`, `MountSession.ReadNumber`/`WriteNumber`
  at `MountSession.cs:257`/`:270`). The static file I/O inside `MountSession` also goes against the "one class, one
  responsibility" guideline in CLAUDE.md: move it into a small `PagingScreenMemory` (or similar) class.
- The `-NoUpdates` / `NO_APP_UPDATES` build switch (`build-installer.ps1`, `OrthoStreamXP.csproj`, `AppUpdates.cs`).
- The log line for the first listing of a package folder (`SceneryFileSystem.cs`).
