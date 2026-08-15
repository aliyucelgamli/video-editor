# Video Editor

A non-destructive video/audio editor for Windows. Import media, arrange clips
on a track/event timeline, apply effects, preview in real time and export —
source files are never modified.

Docs: [CLAUDE.md](CLAUDE.md) (architecture & conventions) ·
[vefx.md](vefx.md) (effect authoring) ·
[TODO.md](TODO.md) (prioritized roadmap — items are deleted as they ship)

## Requirements

- Windows 10/11
- [.NET 10 SDK x64](https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.exe)
- FFmpeg — optional at first start: the app offers a one-click download into `tools\ffmpeg\`

## Running

```bat
run.bat      :: incremental build + start
test.bat     :: run the test suite (zero dependencies)
build.bat    :: publish a self-contained EXE into build\
```

## Features

- **Timeline** — video/audio/overlay tracks; linked A/V pairs; drag with
  snapping; Shift+edge time stretch; split at playhead (S/X); zoom, scrub,
  full undo/redo.
- **Fades & crossfades** — drag a clip's top corner to fade in/out with a
  choice of easing curves (sine, quad, cubic, back, linear); overlapping
  clips on one track crossfade automatically, video and audio together.
- **Effects** — data-driven system shared by preview and export; built-in
  video/audio effects plus custom `.vefx` files in `user/effects/`.
- **Size & position** — visual transform editor per clip: drag corners to
  scale, edges to stretch, inside to move; Ctrl snaps to frame edges/center.
- **Preview** — real-time playback engine with streaming decode, frame
  dropping and timeline audio.
- **Export** — MP4 (H.264/H.265), WebM (VP9), MP3, WAV; GPU encoders
  (NVENC/Quick Sync/AMF) auto-detected; progress window with cancel and
  Play / Open folder on completion. Preview and export share one composition
  pipeline — what you see is what you render.

## Layout & architecture

```
development/src/   Domain · Application · ProjectIO · MediaEngine · App (WPF)
development/tests/ Zero-dependency test suite
user/              User assets (effects, fonts, exports…) — never touched by updates
cache/             Regenerable caches    projects/  .veproj files
```

```
WPF UI (MVVM) → Application services / commands → Domain model
                          ↓
               MediaEngine (FFmpeg as an external process)
```

Principles: non-destructive editing, Media ≠ Event, every edit is an undoable
command, preview == export, zero NuGet packages.
