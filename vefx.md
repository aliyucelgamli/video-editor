# vefx.md — Authoring `.vefx` Effect Files

Reference guide for writing `.vefx` effects for Video Editor. When asked to
"create a … effect" (e.g. *"@vefx.md, create a glitch effect that works on
video tracks"*), follow this document exactly.

## 1. Concept

A `.vefx` file is a **declarative effect definition** — think of it as a Unity
shader for this editor, but data-only. It composes built-in processing
**kernels** into a pipeline and exposes tweakable **parameters** as sliders.
No code, no compilation: drop the file in, and it behaves exactly like a
built-in effect (attachable, reorderable, undoable, keyframe-ready later).

```text
.vefx file  =  metadata  +  parameters (user sliders)  +  steps (kernel pipeline)
```

The same effect definition drives both the live preview and the final export,
so a `.vefx` can never look different after rendering.

## 2. File format

JSON, UTF-8, extension `.vefx`, format version `1`:

```json
{
  "formatVersion": 1,
  "effect": {
    "id": "my-effect",
    "name": "My Effect",
    "category": "Custom",
    "description": "One-line description shown as a tooltip.",
    "targets": "visual",
    "parameters": [
      { "key": "amount", "label": "Amount", "min": 0, "max": 1, "default": 0.5, "unit": "%" }
    ],
    "steps": [
      { "kernel": "blur", "args": { "radius": "$amount" } }
    ]
  }
}
```

### Field reference

| Field | Required | Notes |
|---|---|---|
| `id` | yes | Stable, unique, kebab-case (`"vhs-look"`). Stored in project files — never rename after release. A user `.vefx` with a built-in id **overrides** the built-in. |
| `name` | yes | Display name in the Effects panel. |
| `category` | no | Panel grouping label. Default `"General"`. Use `"Custom"` for user effects. |
| `description` | no | Tooltip text. |
| `targets` | yes | What the effect can attach to: `"video"`, `"audio"`, `"image"`, `"visual"` (= video + image), `"all"`. The editor blocks incompatible drops (e.g. an `audio` effect on a video clip). |
| `parameters` | no | User-adjustable sliders. Each: `key`, `label`, `min`, `max`, `default`, optional `unit` (`"px"`, `"x"`, `"ms"`, `"hz"`). Values are always numbers (doubles) and are clamped to `[min, max]`. |
| `steps` | yes, ≥1 | The kernel pipeline, applied **in order**. |

### Steps and argument binding

Each step is `{ "kernel": "<kernel-key>", "args": { "<argName>": "<value>" } }`.
An arg value is either:

- a **number literal** as a string, invariant culture: `"3"`, `"0.35"`, `"-1"`
- a **parameter reference**: `"$amount"` — substituted with the slider value
  (clamped to the parameter's range) at render time

One parameter can feed several steps, and a step can mix literals with
references. This is how one slider drives a whole composite look.

## 3. Kernel catalog

Kernels are the built-in processing routines. `.vefx` files can only combine
these; a genuinely new algorithm requires a new C# kernel first
(`development/src/MediaEngine/Effects/`, register in
`VideoEffectPipeline.CreateDefaultKernels()`), then it becomes available to
every `.vefx`.

### Video kernels (targets: video / image / visual)

| Kernel | Args (range) | Effect |
|---|---|---|
| `grayscale` | `amount` (0–1) | Black & white; blends toward gray. |
| `sepia` | `amount` (0–1) | Warm brown vintage tone. |
| `temperature` | `amount` (−1–1) | Negative = colder (blue), positive = warmer (red). |
| `brightness` | `amount` (−1–1) | Lightens/darkens. |
| `contrast` | `amount` (−1–1) | Contrast around mid gray. |
| `saturation` | `amount` (0–4) | 0 = gray, 1 = original, >1 boosted. |
| `invert` | `amount` (0–1) | Color negative; blends. |
| `blur` | `radius` (0–100, px) | Gaussian-like separable blur, O(n) at any radius. |
| `vignette` | `amount` (0–1) | Darkened corners, clean center. |
| `glitch` | `amount` (0–1), `speed` (1–60, hz) | Digital glitch: horizontal band displacement + RGB channel splitting. **Time-varying** — the pattern jumps `speed` times per second. |

### Audio kernels (targets: audio)

Audio kernels map to FFmpeg filters at mix/export time:

| Kernel | Args | Effect |
|---|---|---|
| `pitch` | `pitch` (0.25–4, x) | Pitch shift keeping duration (asetrate + atempo). >1 = higher (helium ≈ 1.6), <1 = deeper (≈ 0.7). |
| `echo` | `delay` (1–5000, ms), `decay` (0.01–0.99) | Delayed reflection. |
| `gain` | `amount` (0–4, x) | Level boost/cut (separate from the clip's volume slider). |

### Time-varying kernels

Every kernel call automatically receives a hidden `__time` argument: seconds
since the clip started (clip-local for event effects, timeline time for track
effects). Kernels like `glitch` read it to animate. Rules:

- `.vefx` files never set `__time` — it is injected by the pipeline.
- Animated kernels must stay **deterministic**: same time + same args ⇒ same
  pixels, so preview always matches export. Use hash functions, never RNG state.

## 4. Using `.vefx` files in the editor

Import (any of these):

1. Drop the file in `user/effects/` — loaded automatically at startup.
2. Effects panel → **+** (import) button.
3. Drag & drop the `.vefx` file into the app (library or a track).

Attach to a clip (any of these):

1. Drag the effect from the Effects panel onto a clip.
2. Select a clip, double-click the effect in the panel.
3. Click the **fx** button at the bottom-right of the clip block.
4. Right-click the clip → **Add Effect**.

The applied chain (order, enable/disable, sliders, remove) is edited in the
Effects tab under **SELECTED CLIP**. Different presets of the same look =
multiple `.vefx` files with different `default` values (give each a unique
`id` and `name`, e.g. `glitch-subtle`, `glitch-heavy`).

## 5. Recipes

**Glitch preset for video tracks** (composes the time-varying kernel):

```json
{
  "formatVersion": 1,
  "effect": {
    "id": "glitch-heavy",
    "name": "Glitch (Heavy)",
    "category": "Custom",
    "description": "Aggressive datamosh-style glitch with cold tint.",
    "targets": "video",
    "parameters": [
      { "key": "intensity", "label": "Intensity", "min": 0, "max": 1, "default": 0.85 },
      { "key": "speed", "label": "Speed", "min": 1, "max": 30, "default": 18, "unit": "hz" }
    ],
    "steps": [
      { "kernel": "glitch", "args": { "amount": "$intensity", "speed": "$speed" } },
      { "kernel": "temperature", "args": { "amount": "-0.15" } },
      { "kernel": "contrast", "args": { "amount": "0.1" } }
    ]
  }
}
```

**Chipmunk voice** (audio):

```json
{
  "formatVersion": 1,
  "effect": {
    "id": "chipmunk",
    "name": "Chipmunk",
    "category": "Custom",
    "targets": "audio",
    "parameters": [
      { "key": "pitch", "label": "Pitch", "min": 1.2, "max": 3, "default": 2.1, "unit": "x" }
    ],
    "steps": [ { "kernel": "pitch", "args": { "pitch": "$pitch" } } ]
  }
}
```

Shipped examples to learn from: `user/effects/vhs-look.vefx`,
`dream-look.vefx`, `robot-voice.vefx`.

## 6. Validation rules (a file is rejected when…)

- not valid JSON, or `effect` is missing
- `formatVersion` is newer than the app supports (currently 1)
- `id` or `name` is empty
- `targets` is missing/`none`
- `steps` is empty, or any step has an empty `kernel`

Unknown kernel names do **not** fail validation — those steps are skipped at
render time (forward compatibility). Unknown JSON fields are ignored.

## 7. Checklist for AI-authored effects

When asked to create an effect from this document:

1. Pick `targets` from what the user says it should attach to ("video
   track'inde çalışsın" → `"video"`; sound → `"audio"`).
2. Choose 1–3 meaningful `parameters` — expose what the user will actually
   tweak, hard-code the rest as literals.
3. Compose existing kernels; check the catalog above for exact arg names and
   ranges. Only propose a new C# kernel when no combination can achieve the look.
4. Use a unique kebab-case `id`, `category: "Custom"`, and a one-line
   `description`.
5. Save to `user/effects/<id>.vefx` and validate the JSON (section 6).
6. Multiple intensities of the same look = separate files with different
   defaults, not duplicated pipelines with magic numbers.
