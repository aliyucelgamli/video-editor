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
18. **Playback overlap speed-up** — use `SequentialCompositor` on the
    playback composite path too (currently one process per frame during
    overlaps; crossfades and text overlays make overlaps common now).
19. **Split of a linked pair** — cross-link the two second halves
    (`SplitEventCommand` leaves them unlinked).
20. **Dark ContextMenu + menu dropdown styling** (top-level menu bar is dark,
    the popup submenus still use the light system theme; app dialogs are already
    themed via DialogWindow); fix the stale event reference in
    EventPropertiesWindow.
21. **More settings** — autosave interval, preview quality, single-key
    shortcut suppression while a text box has focus.
    Also: a separate "playback selection" if the shared yellow range ever feels
    wrong for export vs loop (they are one range today, VEGAS-style).
22. **Menu InputGestureText from the ShortcutMap** — menu hints are static
    defaults today and go stale after remapping.
