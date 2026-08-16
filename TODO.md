# TODO — Prioritized Roadmap

> Usage: the list stays ordered by importance (top = most important). When an
> item ships, **delete its line** — no archive here; history lives in git and
> `claude/DURUM.md`. Add new ideas at the right priority. When a major item
> lands, also update the "Feature state" section in CLAUDE.md.
>
> Priority basis: what editor users do most (cut/split/trim > text & captions >
> transitions > audio > speed > platform export presets > color > keyframes)
> plus this project's current gaps.

## P1 — High-demand modern features

1. **Auto-captions** — the most loved feature in modern editors. Drive
   whisper.cpp as an **external exe** (same pattern as ffmpeg, keeps the
   zero-NuGet rule); output SRT → text events; SRT import/export.
2. **Keyframe UI** — animate position/scale/opacity/volume/effect parameters
   over time (`Domain/Keyframes.cs` skeleton exists); mini keyframe lane on
   clips; easing per keyframe (reuse `Domain/Easing`).
3. **Custom easing curves** — a small curve editor (cubic-bezier handles) as
   an extra `EasingType.Custom`; applies to fades and keyframes.
4. **Audio playback overhaul** — replace SoundPlayer with a seekable WASAPI
   interop path (no NuGet); short scrub audio; later auto-ducking (music dips
   under speech).
5. **Rotation + crop** — render support in `FrameCompositor.ApplyTransform`
   plus a rotation handle and crop mode in the transform editor.
6. **Chroma key (green screen)** — new kernel + color-picker parameter.
7. **Markers (M) and named regions** — quick navigation next to the export range.
8. **Autosave + crash recovery** — periodic `.autosave`, "Recovered project"
   offer on start; missing-media Locate/Relink dialog.
9. **Text polish** — alignment presets (lower third, corners), per-line
   styling, background box behind text.
9b. **Layers window: drag to reorder** — today it has bring-forward /
    send-backward buttons and typed numbers; hold-drag reordering would be
    faster for long stacks.

## P2 — Professional polish

10. **Color panel** — one combined panel (exposure/temperature/tint on top of
    the existing kernels); `.cube` LUT support (ffmpeg lut3d or CPU kernel).
11. **Speed ramp + reverse** — keyframed speed, reverse playback.
12. **Proxy editing** — background 720p proxies for 4K sources; preview uses
    the proxy, export the original.
13. **Stabilization** — one-click ffmpeg `deshake`/`vidstab` (when the build
    has it), non-destructive intermediate.
14. **AV1 export** — av1_nvenc on RTX 40 GPUs; add MP4/WebM AV1 formats.
15. **PiP / collage layout presets** — transform already supports it; ship
    corner-PiP, side-by-side, 2x2 presets.

## Tech debt / performance

16. **Timeline virtualization** — materialize only visible clips (1000+ event
    target).
17. **Diff-based RebuildFromModel** — update changed track/event view models
    instead of rebuilding everything on every command.
18. **Backward scrub reuse** — `ScrubRenderer` only serves requests that move
    FORWARD from the primed position (a stream cannot rewind). Dragging the
    playhead left is cold every time. A small ring of recently composed frames
    around the playhead would cover it.
19. **Reusable transform buffer** — `FrameCompositor.ApplyTransform` allocates
    a full frame per layer per frame (~1 MB at 640px, ~8 MB at 1080p); take a
    caller-owned scratch buffer like `SequentialCompositor` already does for
    stills.
20. **Split of a linked pair** — cross-link the two second halves
    (`SplitEventCommand` leaves them unlinked).
21. **Stale event reference in EventPropertiesWindow** — it keeps the clip it
    was opened with, so an undo that replaces the event leaves it editing a
    ghost.
22. **More settings** — autosave interval, single-key shortcut suppression
    while a text box has focus (MainWindow has no text box today, so nothing
    steals Ctrl+C yet — this bites the moment one is added).
    Also: a separate "playback selection" if the shared yellow range ever feels
    wrong for export vs loop (they are one range today, VEGAS-style).
23. **Menu InputGestureText from the ShortcutMap** — menu hints are static
    defaults today and go stale after remapping.

## Playback performance — researched options, in order of value

Run **Settings → Diagnostics → Run performance test** first; the report says
which of these the machine actually needs.

24. ~~GPU decoding~~ — **measured and rejected** (RTX 4070 + Ryzen 5 7500F:
    289 ms vs 127 ms per cold frame). Per-process accelerator init costs more
    than it saves at preview sizes. The probe still times it, so a machine or a
    codec where it wins would show up. Revisit only for 4K/HEVC sources, and
    then inside `StreamingFramePipe` where one process serves many frames.
25. **Proxy media** (also P2 #12) — the largest and most reliable win for 4K
    footage: edit against 720p intermediates, export from the originals.
26. **SIMD pixel operations** — `System.Numerics.Vector<T>` over the BGRA
    buffers in blend/opacity/flatten. Zero dependencies, ~4-8x on the pixel
    loops. Only worth it once decoding is no longer dominant.
27. **D3DImage / hardware surface presentation** — replaces `WriteableBitmap`
    and removes one full-frame CPU copy per presented frame. Large change
    (needs a D3D9Ex device via interop) and only pays off when rendering is
    already under budget.
28. **Third-party engines rejected for now** — SkiaSharp (fast raster, but a
    NuGet dependency and it does not solve decoding), LibVLCSharp / mpv
    (excellent players, but they own the pipeline and cannot composite our
    layer/effect stack), MediaFoundation interop (Windows-only, large surface
    area). All conflict with the zero-dependency rule; revisit only if the
    measurements above stop being enough.
