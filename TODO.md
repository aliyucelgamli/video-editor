# TODO — Prioritized Roadmap

> Usage: the list stays ordered by importance (top = most important). When an
> item ships, **delete its line** — no archive here; history lives in git and
> `claude/DURUM.md`. Add new ideas at the right priority. When a major item
> lands, also update the "Feature state" section in CLAUDE.md.
>
> Priority basis: what editor users do most (cut/split/trim > text & captions >
> transitions > audio > speed > platform export presets > color > keyframes)
> plus this project's current gaps.

## P0 — Core editing (daily-use, still missing)

1. **Plain edge drag = trim** (VEGAS style; Shift+edge = stretch stays) +
   **slip** (Alt+drag: timeline position fixed, source range slides).
2. **Text/title events** — fonts (user/fonts + system), size, color,
   outline/shadow, alignment; rendered as an overlay layer in the compositor;
   transform/opacity/fade already work.
3. **Export presets** — YouTube 1080p/4K, TikTok/Reels (9:16), Instagram
   square, Discord (size-targeted); one click sets format+resolution+fps+
   quality; user-saved presets.

## P1 — High-demand modern features

4. **Auto-captions** — the most loved feature in modern editors. Drive
   whisper.cpp as an **external exe** (same pattern as ffmpeg, keeps the
   zero-NuGet rule); output SRT → text events; SRT import/export.
5. **Keyframe UI** — animate position/scale/opacity/volume/effect parameters
   over time (`Domain/Keyframes.cs` skeleton exists); mini keyframe lane on
   clips; easing per keyframe (reuse `Domain/Easing`).
6. **Custom easing curves** — a small curve editor (cubic-bezier handles) as
   an extra `EasingType.Custom`; applies to fades and keyframes.
7. **Audio playback overhaul** — replace SoundPlayer with a seekable WASAPI
   interop path (no NuGet); short scrub audio; later auto-ducking (music dips
   under speech).
8. **Rotation + crop** — render support in `FrameCompositor.ApplyTransform`
   plus a rotation handle and crop mode in the transform editor.
9. **Chroma key (green screen)** — new kernel + color-picker parameter.
10. **Markers (M) and named regions** — quick navigation next to the export range.
11. **Autosave + crash recovery** — periodic `.autosave`, "Recovered project"
    offer on start; missing-media Locate/Relink dialog.

## P2 — Professional polish

12. **Color panel** — one combined panel (exposure/temperature/tint on top of
    the existing kernels); `.cube` LUT support (ffmpeg lut3d or CPU kernel).
13. **Speed ramp + reverse** — keyframed speed, reverse playback.
14. **Proxy editing** — background 720p proxies for 4K sources; preview uses
    the proxy, export the original.
15. **Stabilization** — one-click ffmpeg `deshake`/`vidstab` (when the build
    has it), non-destructive intermediate.
16. **AV1 export** — av1_nvenc on RTX 40 GPUs; add MP4/WebM AV1 formats.
17. **PiP / collage layout presets** — transform already supports it; ship
    corner-PiP, side-by-side, 2x2 presets.

## Tech debt / performance

18. **Timeline virtualization** — materialize only visible clips (1000+ event
    target).
19. **Diff-based RebuildFromModel** — update changed track/event view models
    instead of rebuilding everything on every command.
20. **Playback overlap speed-up** — use `SequentialCompositor` on the
    playback composite path too (currently one process per frame during
    overlaps; crossfades make overlaps common now).
21. **Customizable shortcuts** — Settings > Keyboard.
22. **Dark ContextMenu style**; **IDialogService** (move MessageBox out of
    view models); fix stale event reference in EventPropertiesWindow.
