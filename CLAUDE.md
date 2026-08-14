# CLAUDE.md — Video Editor

AI geliştirme rehberi. Her oturumda önce bu dosyayı, sonra `VEGAS_EDITOR_REFERENCE.md`'yi oku.
(Guidance for AI sessions working on this repo. Read this first, then `VEGAS_EDITOR_REFERENCE.md`.)

## What this is

A Windows desktop **non-destructive video/audio editor** inspired by Sony VEGAS Pro:
Media Library → drag & drop → Track/Event timeline → preview → effects → MP4 export.
Source media files are NEVER modified; the project (`.veproj`) stores references + edit state only.

## Environment rules (critical — do not break these)

- **.NET 10** everywhere. Libraries target `net10.0`; the WPF App targets `net10.0-windows`.
- **Zero NuGet packages.** No `PackageReference` anywhere. The cloud dev sandbox has no NuGet
  access, and the app must stay lightweight. Tests use a custom zero-dependency runner
  (`tests/Tests/TestRunner.cs`), not xUnit.
- `EnableWindowsTargeting` lives ONLY in `App.csproj` (putting it in Directory.Build.props breaks
  offline restore of the class libraries).
- Domain / Application / ProjectIO / MediaEngine / Tests compile and run on Linux;
  **the WPF App compiles only on the user's Windows machine** (`run.bat`). When editing App code
  without being able to compile it, be extra careful with WPF API usage.
- **FFmpeg is an external process**, not a library. Everything media-related must degrade
  gracefully when ffmpeg/ffprobe are missing (`FFmpegLocator.IsAvailable`).
- Repo root on the user's machine: `C:\Projects\VideoEditor`. `run.bat` / `test.bat` / `build.bat`
  at the root. `build.bat` publishes a self-contained `build\VideoEditor.exe`.

## Project structure

```
development/
  src/
    Domain/        Pure model. No dependencies. Project, Track, TimelineEvent, MediaItem,
                   TimeRange (export range), VolumeLimits, Effects/ (EffectDefinition,
                   EffectStep, EffectTarget, IEffectCatalog). Time unit: double seconds.
    Application/   Use cases. Commands (IEditorCommand + undo/redo), ProjectService,
                   Effects/ (EffectCatalog + BuiltInEffects + UserEffectLibrary).
                   Depends only on Domain.
    ProjectIO/     Persistence. JsonProjectSerializer (.veproj), VefxSerializer (.vefx).
    MediaEngine/   FFmpeg integration. Depends only on Domain. Probe, thumbnails, waveform
                   peaks, frame extraction, FrameCompositor, video kernels (CPU "shaders"),
                   AudioFilterGraphBuilder (ffmpeg filters), ExportService. Fully async.
    App/           WPF (MVVM). ViewModels + Services (TimelineVisualsService,
                   MediaEnrichmentService) + XAML. No FFmpeg calls in views/viewmodels —
                   always go through MediaEngine services.
  tests/Tests/     Zero-dependency test suite (43 tests). Run: test.bat / dotnet run.
user/              User-editable assets (effects/*.vefx, templates, fonts, exports…).
                   NEVER deleted by updates.
cache/             Regenerable artifacts (thumbnails, waveform peaks, preview, proxy).
                   Deleting it must never break a project.
projects/          Default location for .veproj files.
```

Dependency direction (never reverse it):
`App → {Application, ProjectIO, MediaEngine} → Domain`. MediaEngine must not reference Application.

## Architecture rules

1. **Command pattern for every edit.** Any change to the project model goes through an
   `IEditorCommand` executed by `UndoRedoService`. Multi-part operations (linked A/V pairs,
   import+place) are wrapped in one `CompositeCommand`. For simple value changes use the generic
   `SetValueCommand<T>` — do not add one-off command classes for single properties.
2. **Slider pattern:** live-drag writes the model directly (for instant preview) and notifies;
   ONE undoable command is issued on mouse release (`BeginEdit`/`EndEdit`). See
   `EffectParameterViewModel`, `TrackViewModel.VolumePercent`.
3. **Preview == export.** Both run through `FrameCompositor` + `VideoEffectPipeline`. Never add
   preview-only composition logic that export doesn't share.
4. **Async everything heavy.** FFmpeg runs via `ProcessRunner` (async, cancellable). The UI
   thread never waits on media work. UI callbacks are marshalled through the Dispatcher
   (see `TimelineVisualsService`).
5. **Caching:** cache keys come from `CachePaths.KeyFor(path, variantParts…)` and include the
   source file's mtime, so entries self-invalidate. Cache writes are best-effort.
6. **Linked audio/video:** dropping a video with audio creates a video event + an audio event
   cross-linked via `LinkedEventId`; move/delete operate on both (composite commands).
   Splitting linked pairs must split both (SplitEventCommand handles rate-aware source mapping).

## Effect system (the core extension point)

An **EffectDefinition** is pure data: id, name, category, `EffectTarget` flags
(video/audio/image), parameter definitions (min/max/default) and a list of **EffectSteps**.
A step = a **kernel** name + args, where an arg is a literal (`"3"`) or a parameter reference
(`"$strength"`). Built-in effects and imported `.vefx` files use the exact same shape —
downstream code never distinguishes them.

- **Video kernels** (`MediaEngine/Effects/*Kernels.cs`) are CPU pixel routines (`IVideoKernel`,
  BGRA in-place): grayscale, sepia, temperature (warm/cold), brightness, contrast, saturation,
  invert, blur, vignette. Register new ones in `VideoEffectPipeline.CreateDefaultKernels()`.
- **Audio kernels** map to FFmpeg filters in `AudioFilterGraphBuilder`: `pitch`
  (asetrate+aresample+atempo — used by Helium & Deep Voice), `echo` (aecho), `gain` (volume).
- **EffectInstance** (on events/tracks) stores only `Type` (= definition id) + parameter values +
  Enabled. `EffectCatalog` resolves ids; user `.vefx` files can override built-in ids.
- **.vefx format** (`VefxSerializer`, format v1): JSON
  `{ "formatVersion": 1, "effect": { id, name, category, description, targets, parameters, steps } }`.
  Files live in `user/effects/` and load at startup; import copies the file there.
  Examples: `user/effects/vhs-look.vefx`, `robot-voice.vefx`, `dream-look.vefx`.
- Adding a new effect: data-only composite → just author a `.vefx`. New processing capability →
  add a kernel (+ tests) and expose it via a built-in definition or `.vefx`.

## Feature state (2026-08-14)

Done: project model + .veproj; undo/redo commands; timeline UI (zoom, scroll-sync, selection,
drag-move with snap); Explorer/library drag & drop; linked A/V import; ffprobe enrichment (real
durations); library thumbnails; event film strips + audio waveforms; playhead + click/drag scrub;
preview monitor (ffmpeg frame compose at ≤640px) with Space play (video-only, no audio playback);
effect system + Effects panel (drag onto clips, parameter sliders, enable/remove); .vefx
import/export; event & track volume 0–200%; yellow export range bars (I/O keys + draggable);
MP4 H.264+AAC export of range or full project with progress/cancel. 43 tests green.

Not done yet (next steps, roughly in order):
1. Trim (drag event edges) + slip; 2. Split at playhead (S/X) — command exists, UI missing;
3. T = unlink A/V; 4. Audio playback in preview (needs a WAV pipeline or WASAPI interop — no NuGet);
5. Fade handles UI; 6. Text events; 7. Keyframe UI; 8. Track FX UI (model supports it);
9. Proxy/preview cache; 10. Customizable shortcuts; 11. IDialogService (get MessageBox out of VMs);
12. Timeline virtualization for 1000+ events.

## Coding conventions

- C# latest, nullable enabled, implicit usings. File-scoped namespaces. 4-space indent.
- One public type per file; file name = type name. Folders = namespaces
  (`VideoEditor.MediaEngine.Effects` ↔ `src/MediaEngine/Effects/`).
- Naming: `_camelCase` private fields, `PascalCase` members, `camelCase` locals/params.
  Constants `PascalCase`. Async methods end in `Async`.
- Prefer `sealed` for kernel/leaf classes; records for immutable data (`MediaInfo`, `RawFrame`).
- Every public type/member that isn't self-evident gets a short `<summary>` explaining *why*,
  not *what*. Comments explain intent and non-obvious decisions only.
- Culture: all number formatting/parsing that reaches FFmpeg or files uses
  `CultureInfo.InvariantCulture` (`0.###`).
- XAML: styles live in `App.xaml` (dark theme). Reuse `ToolButton`, `FlatSlider`, `FlatCheckBox`,
  `SideTabControl`, etc. XML comments must not contain `--`.
- Segoe MDL2 glyphs in C# as `"\uE767"` escapes (never raw PUA characters).

## Clean code principles (enforced in review)

- **SOLID:** single responsibility per class (locator ≠ runner ≠ probe ≠ compositor);
  depend on abstractions across layers (`IEffectCatalog`, `IProjectSerializer`,
  `IEffectFileReader` let outer layers plug into inner ones); open/closed via kernels + .vefx.
- **DRY:** shared logic gets one home (`VolumeLimits`, `SetValueCommand<T>`, `KernelArgs`,
  `CachePaths.KeyFor`). If you copy-paste a third time, extract.
- Small units: methods ≲ 40 lines, classes ≲ 300 lines; split view models before they bloat
  (MainViewModel delegates to PreviewViewModel / EffectsPanelViewModel).
- No global mutable state; services are constructed in one place (MainViewModel ctor for now).
- Fail soft in cosmetic paths (thumbnails/waveforms swallow errors), fail loud in data paths
  (project IO and export throw with friendly messages; raw ffmpeg stderr only in details/logs).
- Guard clauses over nesting; early returns preferred.
- Tests for every non-UI behavior change; keep `test.bat` green — the suite must run without
  ffmpeg too (integration tests self-skip).

## Workflow for changes

1. Read `claude/DURUM.md` (project memory) and this file.
2. Plan smallest clean change; touch only affected modules; don't rewrite working systems.
3. Libraries + tests compile/run on Linux (`dotnet run --project development/tests/Tests/Tests.csproj`).
4. WPF changes: write carefully, user compiles with `run.bat` and pastes errors back.
5. Update tests, this file's "Feature state", and `claude/DURUM.md` when a feature lands.
