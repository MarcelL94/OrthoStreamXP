# Review: `feature/simbrief-prefetch`

Static review only: not built or tested (the project needs Windows + WinFsp). Line numbers refer to `1f287f3`; the code
is unchanged up to `e59478f` (later commits only touch `CLAUDE.md` and this file).

- **Round 1** (2026-11-11 morning): first pass over the SimBrief client, the departure prefetch, the ground pre-warm and
  `MountSession`.
- **Round 2** (2026-11-11 midday): every changed file, including the GUI, the tests and the `MipWarmer` /
  `TextureStreamer` internals the new code relies on. Round 1's findings are kept, re-checked and re-ranked below.
- **Appendix** (2026-11-11): ideas for lowering OrthoStreamXP's CPU load, unrelated to SimBrief (last section, P1-P4).

Context: a feature proposal has been posted upstream (flysimdev/OrthoStreamXP). It describes this branch as work in
progress and asks whether to send one PR or several. This file is the to-do list until that PR.

## Overall

The design is sound and fits the codebase. One OFP fetch feeds both:

- the **departure prefetch** at mount start: the 3×4 DSF tiles X-Plane loads, each texture to the mip it will want from
  the gate;
- the **ground pre-warm** while parked: arrival, alternate and climb to TOC, at the lowest priority, cancelled on the
  takeoff roll.

Strong points:

- Reading the DSF `TERT` table (`SceneryArea`/`DsfTerrain`) gives exactly the textures X-Plane will ask for, without
  listing the huge terrain folder. It is defensive against truncated or compressed files.
- SimBrief failures never throw into the mount. The parser copes with string or number coordinates, an alternate given
  as an array, and a navlog with a single fix.
- `ClimbRoute` is careful: it skips the departure fix, cuts the climb at TOC, at the end of the CLB stage, or at
  200 NM / 25 min, interpolates the cut leg, and wraps tile longitudes.
- There are good, readable unit tests for parsing, the ground watch, the climb, the scenery area and the warmer's
  ranking.

The main risks are not in the new classes themselves. They are in how a **much bigger request queue** behaves inside
the existing `MipWarmer` (H1-H4).

## Findings at a glance

| ID | Severity | Area | Short |
|---|---|---|---|
| H1 | High | MipWarmer | Waiting ground requests swallow X-Plane's own load-ahead requests |
| H2 | High (verify) | MipWarmer | Every pass ranks and probes the whole queue, now ~10k+ entries |
| H3 | Medium-High (verify) | MipWarmer | Ground heavy builds hold heavy slots while waiting at the lowest download priority |
| H4 | Medium-High | Departure prefetch | Departure requests never expire, even after takeoff |
| H5 | Medium-High | Departure prefetch | A stale OFP prefetches the wrong airport |
| M1 | Medium | MountSession | The OFP is fetched once, at mount start only |
| M2 | Medium | GroundPrewarm | Fire-and-forget queue task, not awaited on stop |
| M3 | Medium | MipWarmer | A base step that keeps failing never leaves the queue and inflates the build counter |
| M4 | Low-Medium | GroundWatch | The camera's speed (outside view) can stop the pre-warm |
| L* | Low | various | See "Minor" |
| D* | – | Docs/UX | Help texts, user guide, README and release notes |
| T* | – | Tests | Gaps that match the findings above |

## High

### H1. Waiting ground requests swallow X-Plane's own load-ahead requests (priority inversion)

- `Scenery/Prefetch/MipWarmer.cs:101` (`OnEntryAdded` → `Add`, line 224) and `:108` (`OnMoved`) both use
  `_waiting.TryAdd`.
- Suppose a ground request (`Ground = true`, priority `FetchPriority.Background + 1 + phase` = 401-403) is waiting for a
  texture that X-Plane now actually loads. The load-ahead request is then silently dropped. The texture stays at the
  lowest priority, with the ground request's mip and distance.
- On the takeoff roll `CancelGround()` (`:149`) removes it entirely, and it only comes back with the next `OnMoved`.
- **Fix:** in `Add` and `OnMoved`, replace a waiting request whose `existing.Ground` is true. A tiny helper
  `AddOrOutrank(Request)` used by all three call sites keeps the rule in one place.
- **Test:** queue a ground request for a texture, raise `EntryAdded` for it, and assert that its target priority is
  load-ahead's and that `CancelGround()` keeps it.

### H2. Every pass ranks and probes the whole queue, now ~10k+ entries (verify by measuring)

- `StartWhatFits` (`MipWarmer.cs:281`) copies all of `_waiting` (`:284`), calls `Target()` on every entry and sorts the
  lot (`:286`) on **every** wake: each finished build, plus once a second.
- `Target()` → `_streamer.RealFrom(t)` (`:242`, and again in `StartWhatFits`) → `TextureStreamer.Probe`. For a texture
  with nothing yet (`RealFrom > BaseMip`), `Probe` calls `StoreHasRegion` (`TextureStreamer.cs:344`). That is up to 16
  `File.Exists` calls (`TileStore.Contains`), and it happens **per waiting texture, per pass**.
- Before this branch the queue held what X-Plane had loaded, a few thousand entries. Now the departure (3,554 textures
  at EDDV) plus the arrival, the alternate and the climb corridor (3 tiles wide, up to 200 NM) can come to **~10-15k
  waiting entries**, almost all of them without data. That is easily 10⁵+ file checks per pass, with many passes per
  second while 64 base builds finish.
- Symptoms to look for: high CPU or disk activity on the warmer thread while parked, and load-ahead reacting late after
  takeoff.
- **Measure first:** log the duration of `StartWhatFits` and `_waiting.Count` every N passes, during a parked ground
  pre-warm.
- **Fix options (one or more):**
  - Keep ground requests in a separate queue that `StartWhatFits` looks at only when the main queue had nothing it could
    start. They are the lowest priority anyway, so no sort is needed beyond their own order.
  - Release ground requests into `_waiting` in batches (e.g. 500 at a time, refilled when the batch drains).
  - Stop the loop over `ranked` once nothing more fits (the `break` exists, but the full sort and all the probes run
    before the first item comes out). Ranking with a `PriorityQueue`, or a partial sort, avoids probing what cannot
    start anyway.

### H3. Ground heavy builds hold heavy slots while waiting at the lowest download priority (verify)

- `_running[kind]` is taken when a build starts and released only when it completes (`MipWarmer.cs:314` ff.).
- The arrival's central textures want full detail (`MipFor` at a small distance → mip 0). They are **Heavy** builds,
  queued at priority 401+.
- Their tile downloads come last in the downloader. So once they have started, they can hold the `heavy` places for a
  long time, while any load-ahead `NearWarm` heavy build waits (`Full(BuildKind.Heavy)`). That can happen at the gate
  (view change, nearby tile paging), and above all **right after takeoff**: `CancelGround()` only drops what is still
  waiting, not the builds already running.
- **Fix options:**
  - Let ground builds use at most part of the heavy places (e.g. `heavy / 2`), with their own counter.
  - Cancel running ground builds on the takeoff roll. This needs a `CancellationToken` through `Warm()` for byRead=false
    builds.
  - Or limit the ground pre-warm to `BuildKind.Small`/`Base` levels for the arrival and alternate, and let load-ahead do
    full detail during the approach.
- **Check in a log:** builds still running at the "aircraft is rolling" line, and the heavy queue wait right after it.

### H4. Departure requests never expire, even after takeoff

- The departure requests carry a fixed mip and priority (`PrefetchPlaced` `:171`, `Target` `:248`: `if (mip is int
  fixedMip) return ...`), chosen for the gate. Near ones get `NearWarm`.
- On a slow connection, departure textures still waiting at takeoff are then fetched at gate detail, with near-aircraft
  priority, after the aircraft has left. That competes with load-ahead for the climb.
- **Fix:** mark departure requests (like `Ground`) and drop or downgrade them on the takeoff roll. `GroundPrewarm` already
  knows the moment. Alternatively, once a real camera position exists, let `Target()` ignore the fixed mip and fall back
  to the normal distance-based choice.

### H5. A stale OFP prefetches the wrong airport

- `Scenery/Flight/DeparturePrefetch.cs:35`: the `StalePlanKm` check only works when `aircraft()` has a position. At mount
  start X-Plane is usually not running yet, so it is null and the check is skipped.
- Result: yesterday's OFP (different departure) queues thousands of textures at the wrong place, using bandwidth, build
  capacity and queue size (see H2).
- **Fix (either or both):**
  - Parse `params.time_generated` (Unix seconds) in `SimBriefClient.Parse`, and ignore OFPs older than a few hours (or
    log loudly).
  - When the first real (non-inferred) position arrives more than `StalePlanKm` from the origin, cancel the departure
    requests. This reuses the marking from H4.

## Medium

### M1. The OFP is fetched once, at mount start only

- `Mounting/MountSession.cs:226`: `client.LatestPlanAsync(user, stop)` runs once.
- If the pilot starts OrthoStreamXP first and generates the OFP afterwards (very common), neither feature runs for that
  flight. A second flight in the same session also keeps the old plan (`GroundWatch` goes to `Done` and never
  restarts).
- **Fix:** fetch the OFP again when `GroundWatch` reports `Start` (parked for 30 s), at least when the cached plan's
  origin is far from the parked aircraft or the plan is old. It costs one HTTP call per flight. Reset the watch when a
  new plan with a different origin arrives.

### M2. The ground pre-warm task is fire-and-forget

- `Scenery/Flight/GroundPrewarm.cs:79`: `_ = Task.Run(...)` is not tracked and not awaited in
  `MountSession.StopAsync` (unlike `_departure`).
- Only `IOException` / `UnauthorizedAccessException` are caught. Stopping while it is queuing can touch a disposed
  `MipWarmer`: `Wake()` on a disposed `SemaphoreSlim` throws `ObjectDisposedException`. Any other exception goes
  unobserved and is never logged.
- **Fix:** keep the task (e.g. `GroundPrewarm.Running`), await it in `StopAsync` next to `_departure`, pass the session's
  stop token into `Queue`, and log unexpected exceptions.

### M3. A base step that keeps failing never leaves the queue and inflates the build counter

- New in this branch: a request asking for a finer level than the base keeps its entry while the base tile is fetched
  first (`baseStep`, `MipWarmer.cs:312`).
- If that base build fails, `TextureStreamer.StartBuild` returns `Task.FromResult(false)` for 15 s (`RetryAfter`), so
  `build.IsCompleted`. The request stays (it is a base step), and the next pass tries again. Before this branch a
  failed warm was removed.
- Effects:
  - A texture whose base tile can never be fetched (provider gap, ocean, 404) stays in `_waiting` for the whole session
    and is re-ranked and re-probed every pass (H2).
  - `Interlocked.Increment(ref _queued)` (`:314`) runs on every attempt, so "N builds started so far" in the progress log
    over-counts.
- **Fix:** count base-step attempts per request and drop it (or fall back to the asked level directly) after 1-2
  failures. Increment `_queued` only when a build really started (`!build.IsCompleted`).

### M4. The camera's speed can stop the pre-warm (outside view)

- `GroundPrewarm.cs:76` passes the camera's motion as the ground speed. Moving the camera fast in the outside view
  (> `RollingKt` = 40 kt) can stop the pre-warm before the real roll. It never restarts, because the watch goes to
  `Done`.
- **Fix:** if X-Plane's UDP data has ground speed or an on-ground flag (or the aircraft position separate from the
  camera), use that. Otherwise require the speed to stay above `RollingKt` for a few seconds, and/or allow the
  `Running` → `Waiting` way back while the camera stays near the departure.

## Minor

- **L1. Duplicate log line.** If the aircraft starts rolling while `Queue` is still running, `Cancel()` runs twice
  (`GroundPrewarm.cs:89` and the `Stop` branch), so "the aircraft is rolling for takeoff" is logged twice.
- **L2. Duplicate constants.** `DeparturePrefetch.StalePlanKm` (`DeparturePrefetch.cs:28`) and
  `GroundWatch.NearDepartureKm` (`GroundPrewarm.cs:14`) are both 50 km for the same idea. Use one constant on
  `FlightPlan`.
- **L3. Magic number.** `1.943844` (m/s → kt) at `GroundPrewarm.cs:76`: use a named constant or an existing helper.
- **L4. All-digit usernames.** `SimBriefClient.cs:31` sends an all-digit username as `userid` (Pilot ID). Probably fine,
  but say so in the setup and ini help.
- **L5. Fully qualified type.** `System.Collections.Concurrent.ConcurrentDictionary` in `MipWarmer.cs`: add a `using`.
  The same goes for `System.Globalization` / `System.Text` in `MountSession.ReadNumber`/`WriteNumber`.
- **L6. User-Agent.** It is `orthostreamxp` (`MountSession.cs:221`). Include the version (`OrthoStreamXP/1.2.1`) so
  SimBrief can tell clients apart, and set the same agent on the Setup test's `HttpClient` (it uses `MeshHttp`).
- **L7. Timeout vs OFP size.** The fetcher's JSON includes the whole OFP text (often 1-2 MB). `Timeout` = 10 s
  (`SimBriefClient.cs:25`) covers headers **and** body (`ReadAsStringAsync`, `:52`), which may be tight on a slow
  line. Consider 20-30 s, or `HttpCompletionOption.ResponseHeadersRead` with a separate read timeout.
- **L8. Every texture of the arrival, alternate and climb is registered.** `PrefetchPlaced` calls
  `_streamer.Get(texture)`, which `GetOrAdd`s a `StreamedTexture` (`TextureStreamer.cs:105`) for every texture there
  (tens of thousands). They live for the session. That is probably fine; just be aware of it.
- **L9. The fallback search scales badly with the radius.** `AirportTextures.Near` (`:32`) walks every cell of the box
  at all 8 zoom levels (12-19), with one `EnumerateFiles` per cell inside the circle. At ZL19 and the maximum 50 km that
  is tens of thousands of directory lookups. The radius field is gone from the UI, so only the ini can raise it. Either
  cap the radius lower or look only at the zooms the package actually uses.
- **L10. The remembered paging k can come from a zoomed view.** `MountSession.cs:177` writes k on every
  screen/field-of-view change. If the pilot ends a session zoomed in, the next start's first guess uses that field of
  view. Ignore fields of view far from the session's median, or write only at stop.
- **L11. The test button is fire-and-forget.** `SimBriefTestViewModel` runs `_ = RunAsync()` (`:23`). `FetchAsync`
  doesn't throw for expected failures, so this is OK. A `try/catch` that logs and sets `Status` would still make it
  robust against an unexpected exception (it would vanish silently otherwise).
- **L12. Auto-start skips the switch.** With "Start when OrthoStreamXP opens" on, the mount starts before the Home
  switch can be turned off (`CanChangeDeparturePrefetch` is only true while stopped). Acceptable, but mention it in the
  tooltip.

## Docs and UX consistency

- **D1. Help texts describe only the departure.** The Settings field (`SettingsViewModel.cs:66`), the ini help
  (`SettingsOptions.cs:42`), the Setup step (`SetupView.xaml:285`) and `orthostreamxp.example.ini` all describe the
  departure prefetch only. The Home tooltip mentions arrival, alternate and climb. Describe both phases the same way
  everywhere, in one sentence.
- **D2. Wording style.** The Home switch reads "SimBrief Prefetch enabled" (`HomeView.xaml:112`). The other switches use
  sentence case and describe the action ("Start when OrthoStreamXP opens"). Something like "Build my SimBrief flight's
  scenery ahead" would match.
- **D3. User-facing docs are missing.** There is nothing in the user guide (`docs/`, mkdocs), in README's feature list,
  or in release notes. Upstream will want at least a docs page and a release-notes entry.
- **D4. Privacy note.** Say in the docs that the username goes to simbrief.com (HTTPS) once per streaming start, and
  that nothing else is sent.

## Test gaps

- **T1.** H1: a ground request should not block an `EntryAdded` / `OnMoved` request, and `CancelGround` should keep the
  load-ahead one.
- **T2.** H4/H5: departure requests are dropped on takeoff, and when the first real position is far away.
- **T3.** M3: a base step whose base build fails is dropped (or falls back) after N attempts, and `Queued` counts only
  real starts.
- **T4.** M2/L1: stopping during `Queue` doesn't throw, and "rolling" is logged once. With an injectable scheduler
  instead of `Task.Run`, that is testable.
- **T5.** `SimBriefClient`: the timeout path (a handler that never answers), a non-JSON body (an HTML error page), and
  `params.time_generated` once H5 is in.
- **T6.** `DsfTerrain`: a truncated file, and an atom length beyond the end. Today only uncompressed vs. compressed is
  covered.
- **T7.** A small benchmark or a log-based check for H2: queue 15k ground requests on a fake store and time
  `StartWhatFits`.

## Branch scope

These changes are unrelated to SimBrief and should be separate PRs, or at least be called out in the PR description:

- Remembering the paging model's k across starts (`PagingModel.Remember`, `MountSession.ReadNumber`/`WriteNumber`). The
  static file I/O inside `MountSession` also goes against the "one class, one responsibility" guideline: move it into a
  small `PagingScreenMemory` (or similar) class.
- The `-NoUpdates` / `NO_APP_UPDATES` build switch (`build-installer.ps1`, `OrthoStreamXP.csproj`, `AppUpdates.cs`).
- The log line for the first listing of a package folder (`SceneryFileSystem.cs`).
- The `TextTrimming` tweak on the setup step labels (`SetupView.xaml`), needed because there are now 7 steps. It is fine
  to keep, but say why.

`MountSession.StartDeparturePrefetch` itself (wiring, the HTTP client, the targets and the plan task, about 50 lines)
would read better as its own small `SimBriefSession` / `FlightPlanPrefetch` class that `MountSession` creates and
disposes.

## Before the upstream PR (checklist)

- [ ] Wait for the maintainer's answer to the feature proposal (one PR, or departure first and ground pre-warm second).
- [ ] Rebase onto upstream **`develop`** (the README says PRs go to `develop`). The branch is based on `1f5fb20`,
      `develop` is 4 commits further (CI and a help text), and none of those touch the changed files.
- [ ] Remove `review-simbrief-prefetch.md` and the fork-only `CLAUDE.md` branching change from the PR branch (upstream
      still has "Don't commit anything to the repo").
- [ ] Fix H1, H4 and H5 at least. Measure H2 and H3 in a flight log, and fix them if they show.
- [ ] Split out or call out the unrelated changes (Branch scope).
- [ ] D1-D4: help texts, user guide page and release-notes entry.
- [ ] Build with `TreatWarningsAsErrors` and run the full test suite on Windows, including `MountSmokeTests` with WinFsp.
      Note the results in the PR.
- [ ] In the PR description, include the numbers from the test flights (native-speed first load, ~98 % of the arrival
      preloaded) with the route and a log excerpt.

## What to log in the next test flight

These answer H2, H3 and M4 without guessing:

- `_waiting.Count` and the duration of `StartWhatFits`, every 100 passes, while the ground pre-warm runs.
- At the "rolling for takeoff" line: the heavy, small and base builds still running, and how many of them are ground
  builds.
- The first 2 minutes after takeoff: the wait times of X-Plane's near reads (`NearRead`), compared with a flight without
  the ground pre-warm.
- Whether a camera move in the outside view ever triggered `GroundDecision.Stop` before the real roll.

## Unrelated to SimBrief: lowering OrthoStreamXP's CPU load

X-Plane is mostly limited by its main thread. OrthoStreamXP's builds only cost frames when they saturate the CPU or land
on X-Plane's cores. Moving the work to the GPU (ComputeSharp / DirectX 12 compute) is possible, but it competes with
X-Plane's Vulkan rendering for GPU time and VRAM (12 GB on an RTX 5070), so it can make stutters worse. These four
steps come first, in this order. Consider a GPU path, opt-in with CPU fallback, only if step 1 still shows encoding
dominating after steps 2-4.

Today: there is no SIMD anywhere (`System.Runtime.Intrinsics` is unused), no lowered thread priority, and the work runs
in `Parallel.For` on thread-pool threads (`Images.cs:104/123/135`, `Dds.cs:214`). `encoder-workers` (default 4) caps the
parallelism.

### P1. Profile where a build's CPU time goes

- `Dds.Encode` already returns `mipmapS` and `bc1S` (`TextureBuilder.cs:45`). Add the time spent decoding
  (`Images.DecodeTile`, called from `TileSource.cs:307/347`), stitching, resizing (`ResizeTo`/`BoxHalf`) and colour
  work (`PhotoColours`).
- Sum them per session by stage and texture size, and put them in the flight report or the summary log line ("CPU per
  4096 px build: decode x ms, resize y, mips z, BC1 w, colour v").
- Run one flight with this, plus a short sampling profile (dotnet-trace / PerfView) during a cold load. The result
  decides which of P2-P4 (or a GPU path) is worth it.

### P2. Run the build workers at BelowNormal priority

- The goal: Windows always gives X-Plane's threads the core first, and OrthoStreamXP takes what is left.
- **Do not lower the whole process** (`ProcessPriorityClass.BelowNormal`). The WinFsp callbacks that answer X-Plane's
  reads run in this process, and their latency is what X-Plane waits for.
- Instead, run decode, resize, mips, BC1 and colour on dedicated threads with `ThreadPriority.BelowNormal`: a small
  custom `TaskScheduler` with `encoder-workers` threads, passed as `ParallelOptions.TaskScheduler` to the
  `Parallel.For` calls and used for `TileSource`'s decodes. Thread-pool threads should not have their priority changed.
- It is cheap to try: compare frame times in a cold-load flight with and without it.

### P3. SIMD for BC1 and the mip filter (AVX2 via `Vector256`)

- BC1 (`Dds.cs`, `DdsBlocks.cs`): the block min/max or endpoint search, the colour-index selection and the RGB → 565
  packing are classic SIMD work. Processing 2-4 blocks per vector or vectorising within the block often gives 3-8× on
  this code.
- `Images.BoxHalf` / `BoxHalfInto` (2×2 averages over RGB rows) and `ResizeBicubic` (the horizontal and vertical passes)
  vectorise well too.
- Keep the scalar path as the fallback (`Vector256.IsHardwareAccelerated`). Add tests that compare the output byte for
  byte, or within a small tolerance, with the scalar version on a few real tiles.

### P4. Decode coarse tiles at reduced size

- The ability already exists: `Images.Decode(encoded, scale)` uses libjpeg-turbo's DCT scaling via
  `SKCodec.GetScaledDimensions` (1/2, 1/4, 1/8). Today only `CacheAreas` (the cache map) uses it.
- The build path decodes every tile at full 256 px and then halves it in a loop: `TextureBuilder.CopyTileAsync`
  (`:201`, `BoxHalf` at `:208`) whenever `tilePx < 256` (smaller textures, `level-change` < 0, stand-ins). Decoding
  directly at `tilePx / 256` skips most of the decode **and** the halving.
- This needs `TileSource.GetTileAsync` to accept a decode scale. Its RAM cache holds decoded full-size tiles, so cache
  the scaled result under its own key, or decode scaled only when the full-size tile isn't cached anyway.
- Check the result visually: DCT scaling filters slightly differently from `BoxHalf`, which matters for colour
  matching across seams.
