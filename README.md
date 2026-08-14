# Video Editor

A non-destructive video/audio editor for Windows, inspired by VEGAS Pro.
Import media, arrange clips on a track/event timeline, apply effects, preview
in real time and render the selection to MP4 — source files are never modified.

Docs: [CLAUDE.md](CLAUDE.md) (architecture & conventions) ·
[vefx.md](vefx.md) (effect file authoring) ·
[VEGAS_EDITOR_REFERENCE.md](VEGAS_EDITOR_REFERENCE.md) (UX reference)

## Requirements

- Windows 10/11
- [.NET 10 SDK x64](https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.exe)
- FFmpeg — not required to start: the app offers a one-click download that
  installs it into `tools\ffmpeg\` (media features are disabled until then)

## Running

```bat
run.bat      :: incremental build + start in development mode
test.bat     :: run the test suite (50 tests, zero dependencies)
build.bat    :: publish a self-contained release EXE into build\
```

## What the app does today

**Media** — Import video (mp4/mov/avi/mkv/webm), audio (wav/mp3/aac/flac/ogg)
and images (png/jpg/bmp/webp/tiff) via dialog or drag & drop from Explorer.
The library shows real thumbnails and probed metadata (resolution, duration,
size); multi-select with Shift/Ctrl and Delete removes references (assets used
on the timeline are protected; files on disk are never touched).

**Timeline** — Track/event model (video, audio, overlay lanes). Dropping a
video creates a linked video + audio pair that moves, stretches and deletes
together (unlink with T or right-click). Clips show film strips (video) or
waveforms (audio). Drag to move with snapping; Shift + edge-drag time-stretches
a clip (shorter = faster, playback rate adjusts, source untouched). Zoom with
the mouse wheel, scrub by clicking or dragging anywhere on the ruler or lanes.
Full undo/redo across every operation (command pattern).

**Preview** — Real-time playback engine: a background producer streams frames
from ffmpeg (single process, frame dropping and auto re-seek when decoding
falls behind), a fixed-rate consumer keeps the playhead moving with the wall
clock, and the timeline auto-scrolls to follow. Timeline audio plays along —
mixed with all volumes, mutes and effects applied. Space = play/pause.

**Effects** — Data-driven effect system shared by preview and export. Built-in
video effects (black & white, sepia, warm/cold, brightness, contrast,
saturation, invert, blur, vignette, animated glitch) and audio effects (helium,
deep voice, echo, gain). Attach by dragging from the Effects panel, via the
**fx** button on every clip (opens the Event FX window with add / tweak /
toggle / remove and live-updating sliders) or the right-click menu. Custom
effects are plain `.vefx` JSON files (Unity-shader-like kernel pipelines with
user parameters) dropped into `user/effects/` — see [vefx.md](vefx.md).

**Mixing** — Per-clip and per-track volume (0–200 %), mute/solo, fades and
opacity in the model; a video clip's volume slider controls its linked audio.

**Export** — The Export button (top right) renders the span between the two
yellow bars on the ruler to MP4 (H.264 + AAC) with progress and cancel. The
bars are always visible — at the project bounds by default, draggable, or set
with the I/O keys. Preview and export share the same composition pipeline, so
what you see is what you render.

## Layout

```
development/          Source code (kept apart from user data)
  src/Domain/         Pure model: Project, Track, TimelineEvent, MediaItem,
                      TimeRange, Effects (definitions, parameters, kernels)
  src/Application/    Use cases: undoable commands, ProjectService, EffectCatalog
  src/ProjectIO/      Persistence: .veproj project format, .vefx effect files
  src/MediaEngine/    FFmpeg integration: probe, thumbnails, waveforms, frame
                      extraction & caching, playback engine, export
  src/App/            WPF UI (MVVM) — views, view models, UI services
  tests/Tests/        Zero-dependency test suite
examples/             Tiny sample clips, images and sounds for testing
user/                 User assets (effects, templates, fonts, exports…) —
                      never deleted by updates
cache/                Regenerable caches (thumbnails, waveforms, previews)
projects/             Suggested location for .veproj files
```

## Architecture

```
WPF UI (MVVM) → Application services / commands → Domain model
                          ↓
               MediaEngine (FFmpeg as an external process)
```

Core principles: **non-destructive editing** (source files are never written),
**Media ≠ Event** (one file can appear on the timeline any number of times),
every edit is an undoable `IEditorCommand`, and **preview and final render use
the same composition pipeline**. No NuGet packages — the whole app builds from
the .NET SDK alone, with FFmpeg driven as an external process.
