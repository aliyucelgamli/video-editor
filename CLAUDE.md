# CLAUDE.md — Video Editor

Guidance for AI sessions working on this repository. Read this first, then
`vefx.md` (effect authoring) and `TODO.md` (prioritized backlog). The whole
repository — code, comments, docs — is written in English.

## What this is

A Windows desktop **non-destructive video/audio editor** inspired by Sony VEGAS Pro:
Media Library → drag & drop → Track/Event timeline → preview → effects → MP4 export.
Source media files are NEVER modified; the project file (`.veproj`) stores references
plus edit state only.

## Environment rules (critical — do not break these)

- **.NET 10** everywhere. Libraries target `net10.0`; the WPF App targets `net10.0-windows`.
- **Zero NuGet packages.** No `PackageReference` anywhere. The cloud dev sandbox has no NuGet
  access, and the app must stay lightweight. Tests use a custom zero-dependency runner
  (`tests/Tests/TestRunner.cs`), not xUnit.
- `EnableWindowsTargeting` lives ONLY in `App.csproj` (putting it in Directory.Build.props
  breaks offline restore of the class libraries).
- Domain / Application / ProjectIO / MediaEngine / Tests compile and run on Linux;
  **the WPF App compiles only on the user's Windows machine** (`run.bat`). When editing App
  code without being able to compile it, be extra careful with WPF API usage.
- **FFmpeg is an external process**, not a library. Every media feature must degrade
  gracefully when ffmpeg/ffprobe are missing (`FFmpegLocator.IsAvailable`).
- Repo root on the user's machine: `C:\Projects\VideoEditor`. Root scripts:
  - `run.bat` — incremental build first (skips fast when nothing changed, shows compiler
    errors), then starts the app with `--no-build`.
  - `test.bat` — runs the test suite. `build.bat` — publishes a self-contained
    `build\VideoEditor.exe`.

## Project structure

```
development/
  src/
    Domain/        Pure model, no dependencies. Project, Track, TimelineEvent, MediaItem,
                   TimeRange (export range), VolumeLimits, Effects/ (EffectDefinition,
                   EffectStep, EffectTarget, IEffectCatalog). Time unit: double seconds.
    Application/   Use cases. Commands (IEditorCommand + undo/redo), ProjectService,
                   Effects/ (EffectCatalog, BuiltInEffects, UserEffectLibrary).
                   Depends only on Domain.
    ProjectIO/     Persistence. JsonProjectSerializer (.veproj), VefxSerializer (.vefx).
    MediaEngine/   FFmpeg integration; depends only on Domain. Probe, thumbnails, waveform
                   peaks, frame extraction, FrameCompositor, video kernels (CPU "shaders",
                   including time-varying ones like glitch), AudioFilterGraphBuilder
                   (ffmpeg filters), ExportService. Fully async.
    App/           WPF (MVVM). ViewModels + Services (TimelineVisualsService,
                   MediaEnrichmentService) + XAML. No FFmpeg calls in views/viewmodels —
                   always go through MediaEngine services.
  tests/Tests/     Zero-dependency test suite (46 tests). Run: test.bat / dotnet run.
examples/          Tiny test assets (1080p clips, 64×64 images) for drag & drop testing.
user/              User-editable assets (effects/*.vefx, templates, fonts, exports…).
                   NEVER deleted by updates.
cache/             Regenerable artifacts (thumbnails, waveform peaks, preview, proxy).
                   Deleting it must never break a project.
projects/          Default location for .veproj files.
```

Dependency direction (never reverse it):
`App → {Application, ProjectIO, MediaEngine} → Domain`. MediaEngine must not reference
Application.

## Architecture rules

1. **Command pattern for every edit.** Any change to the project model goes through an
   `IEditorCommand` executed by `UndoRedoService`. Multi-part operations (linked A/V pairs,
   import + place) are wrapped in one `CompositeCommand`. For simple value changes use the
   generic `SetValueCommand<T>` — do not add one-off command classes per property.
2. **Slider pattern:** live-drag writes the model directly (instant preview) and notifies;
   ONE undoable command is issued on mouse release (`BeginEdit`/`EndEdit`). See
   `EffectParameterViewModel`, `TrackViewModel.VolumePercent`.
3. **Preview == export.** Both run through `FrameCompositor` + `VideoEffectPipeline`.
   Never add preview-only composition logic export doesn't share. Time-varying kernels get
   the hidden `__time` argument injected by the pipeline and must be deterministic
   (same time + args ⇒ same pixels).
4. **Async everything heavy.** FFmpeg runs via `ProcessRunner` (async, cancellable). The UI
   thread never waits on media work; UI callbacks are marshalled through the Dispatcher.
   Intentional fire-and-forget calls are discarded explicitly (`_ = …`) with a comment —
   the build must stay warning-free.
5. **Caching:** cache keys come from `CachePaths.KeyFor(path, variantParts…)` and include
   the source file's mtime, so entries self-invalidate. Cache writes are best-effort.
6. **Linked audio/video:** dropping a video with audio creates a video event + an audio
   event cross-linked via `LinkedEventId`; move/delete/stretch operate on both via
   composite commands.
7. **Timeline interactions** (code-behind `MainWindow.xaml.cs`, math in
   `Application/Editing`, model changes in `MainViewModel`): drag = move (with snap),
   **plain edge drag = trim** (rate fixed, source range follows — `EdgeTrim`),
   **Shift + edge drag = time stretch** (duration changes,
   `PlaybackRate = sourceSpan / duration`, source range untouched),
   **Alt + drag = slip** (source slides, position fixed), corner grips = eased fades,
   fx button (bottom-right of every clip) and right-click menu = attach effects /
   remove effects / delete. Effects can also be dragged from the panel or double-clicked.

## Effect system (the core extension point)

See **`vefx.md`** for the authoring guide and full kernel catalog. Summary:

- An `EffectDefinition` is pure data: id, targets (video/audio/image flags), parameters
  (min/max/default sliders) and **steps** — kernel calls whose args are literals or
  `"$parameter"` references. Built-ins and imported `.vefx` files share this shape.
- **Video kernels** (`MediaEngine/Effects/`): grayscale, sepia, temperature, brightness,
  contrast, saturation, invert, blur, vignette, glitch (time-varying). New kernels are
  registered in `VideoEffectPipeline.CreateDefaultKernels()` and documented in `vefx.md`.
- **Audio kernels** map to FFmpeg filters in `AudioFilterGraphBuilder`: pitch (helium /
  deep voice), echo, gain.
- `.vefx` files live in `user/effects/`, load at startup, import via panel button or
  drag & drop, and may override built-in ids.

## Feature state (2026-08-15, round 14)

Done: project model + .veproj; undo/redo commands; timeline (zoom, scroll-sync, selection,
drag-move with snap, **Shift+edge time stretch**); Explorer/library drag & drop; linked A/V
import; ffprobe enrichment; library thumbnails; film strips + waveforms on events; playhead +
scrub; preview monitor (Space play, video-only); effect system + Effects panel + **fx button
and right-click menu on clips**; `.vefx` import/export + `vefx.md` guide; time-varying
kernel support + glitch; event & track volume 0–200%; yellow export range bars (I/O keys,
draggable); multi-format export (MP4 H.264/H.265, WebM VP9, MP3, WAV) with a
**streaming export pipeline** (`SequentialCompositor`: one long-lived ffmpeg decoder per
event instead of one process per frame, double-buffered pipe into the encoder — ~8×
faster CPU-only, more with a GPU); **GPU encoder auto-detection** (`HardwareEncoders`:
NVENC/Quick Sync/AMF, verified at runtime with a tiny test encode, cached; toggle in the
export dialog); **export progress window** (percent, elapsed/ETA, cancel; on completion
shows the output path with Play / Open folder / Close); **visual transform editor**
(`TransformEditorWindow`, opened by a clip's size button or "Size && Position…" menu:
Unity-style stage gizmo — corner drag scales aspect-locked, edge drag stretches one axis,
inner drag moves with center snapping, **Ctrl+drag snaps to the frame's edges/corners and
center lines with alignment guides**, Esc cancels a drag; numeric panel on the right;
one undo step per session; math in `Application/Editing/TransformGizmo`, unit-tested);
**split at playhead** (S/X keys + context menu — selected clip incl. linked partner,
or every clip under the playhead); **eased fades with corner grips** (hold a clip's
top corner and drag inward; the eased opacity envelope is drawn on the clip; easing
per fade via right-click — Linear/Sine/Quad/Cubic/Back families in `Domain/Easing`,
audio maps to afade curves); **automatic crossfades** (same-track overlaps fade
out/in across the overlap, video and audio identically — `Domain/Crossfade` +
`FrameCompositor.EffectiveFadeFactor` + `AudioMixPlanner`); **plain edge trim + Alt slip**
(`EdgeTrim` math, `TrimEventCommand`/`SlipEventCommand`, media-bound clamping, linked
partners follow); **text (title) events** (`TextStyle` on the event, WPF rasterizer →
`TextRasterCache`, compositor/export layer them like stills, Text toolbar button +
edit dialog, transform gizmo works on titles, pre-rendered at export size before
rendering); **export presets** (YouTube/TikTok/Instagram/Discord one-click in the
export dialog); **dark native title bars** on every window (`App/Ui/DarkTitleBar`);
shared utils (`App/Ui`: ChildWindowSlot, FrameBitmaps, TimeText; `FfmpegFormat`,
`FrameSizes`); run.bat build-first flow. 69 tests green.

Not done yet: see **`TODO.md`** at the repo root — the prioritized backlog. It is a
living list: items are ordered by importance and DELETED when they land (no archive;
history lives in git and `claude/DURUM.md`).

## Coding conventions

Based on the Microsoft C# coding conventions, adapted to this codebase:

- C# latest, nullable enabled, implicit usings, file-scoped namespaces, 4-space indent.
- One public type per file; file name = type name. Folders = namespaces
  (`VideoEditor.MediaEngine.Effects` ↔ `src/MediaEngine/Effects/`).
- Naming: `_camelCase` private fields, `PascalCase` types/members/constants,
  `camelCase` locals and parameters, `Async` suffix on async methods, `I` prefix on
  interfaces. No abbreviations (`definition`, not `def`) except well-known ones (`evt`
  is used for `TimelineEvent` to avoid the `event` keyword).
- Prefer: expression-bodied members for one-liners; pattern matching (`is { } x`,
  switch expressions) over null checks and if-chains; `sealed` for leaf classes;
  records for immutable data (`MediaInfo`, `RawFrame`, `ProcessResult`).
- Culture: every number that reaches FFmpeg, file formats or logs uses
  `CultureInfo.InvariantCulture` (`0.###`).
- XAML: shared styles in `App.xaml` (dark theme) — reuse `ToolButton`, `FlatSlider`,
  `FlatCheckBox`, `SideTabControl`, `FlatProgressBar`. XML comments must not contain `--`.
- Segoe MDL2 glyphs in C# as `"\uE767"` escapes (never raw PUA characters).
- Warnings are errors in spirit: the build must be warning-clean. Un-awaited calls that
  are intentionally fire-and-forget use `_ =` discards with a short comment.

## Clean code principles (enforced in review)

- **SOLID**
  - *Single responsibility:* one reason to change per class — locator ≠ runner ≠ probe ≠
    compositor ≠ exporter; view models delegate (Main → Preview/EffectsPanel).
  - *Open/closed:* extend via kernels + `.vefx` + commands, don't modify working systems.
  - *Liskov:* kernels and commands are interchangeable through their interfaces; no
    type-checking consumers.
  - *Interface segregation:* small contracts (`IEditorCommand`, `IVideoKernel`,
    `IEffectFileReader`) instead of god-interfaces.
  - *Dependency inversion:* outer layers depend on Domain abstractions
    (`IEffectCatalog`, `IProjectSerializer`); Domain depends on nothing.
- **DRY:** shared logic gets one home (`VolumeLimits`, `SetValueCommand<T>`,
  `KernelArgs`, `CachePaths.KeyFor`, `ContentTypeOf`). Extract on the second copy-paste.
- **Small units:** methods ≲ 40 lines, classes ≲ 300 lines; guard clauses and early
  returns over nesting; no boolean-parameter traps (use enums like `EventDragMode`).
- **Meaningful names over comments;** comments explain *why* and non-obvious decisions,
  never restate code.
- **Error strategy:** fail soft in cosmetic paths (thumbnails/waveforms swallow and log),
  fail loud in data paths (project IO and export throw with a friendly message; raw
  ffmpeg stderr only in details/logs, `logs/app.log`).
- **No global mutable state.** Services are constructed in one composition root
  (MainViewModel ctor for now — extract a bootstrapper when it grows).
- **Tests with every behavior change.** Non-UI logic must be covered; the suite runs
  without ffmpeg too (integration tests self-skip). Keep `test.bat` green.

## Workflow for changes

1. Read `claude/DURUM.md` (project memory), this file, and `vefx.md` when effects are involved.
2. Track work as small steps; implement one step at a time — never a big-bang rewrite.
3. Libraries + tests compile/run on Linux
   (`dotnet run --project development/tests/Tests/Tests.csproj`).
4. WPF changes: write carefully; the user compiles with `run.bat` (build-first) and
   pastes errors back.
5. When a feature lands: update tests, the "Feature state" section above,
   `claude/DURUM.md`, and **delete the finished item from `TODO.md`** (add newly
   discovered work there at the right priority instead of growing lists here).
