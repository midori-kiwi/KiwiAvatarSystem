# KiwiAvatarSystem Tracking Quality 10 Overlay

## Installation

1. Close Play Mode.
2. Extract this ZIP.
3. Drag the included `Assets` folder onto the root of your local `KiwiAvatarSystem` Unity project.
4. Allow Windows to merge/overwrite files if asked.
5. Open Unity and wait for script compilation.

No scene editing is required. `KiwiTrackingQuality10Controller` auto-installs itself at runtime.

## What this overlay changes

This is intentionally an additive overlay. It does **not** replace the current MediaPipe / Inference Engine tracking core.

### Body tracking
- keeps the hybrid high-rate tracking architecture,
- disables the render-time "direct during motion" bypass that can expose low-rate sample steps,
- keeps bounded late prediction,
- adds a display-rate retimer that only engages when source updates are below display rate,
- bypasses itself when the source is already updating continuously,
- decays velocity after a stop/reversal to avoid prediction overshoot,
- resets presentation velocity when an avatar hot swap completes.

### Eye / mouth
- keeps crop and contour tracking at render rate,
- disables direct crop snapping during intentional translation,
- preserves matched-frame-age compensation,
- keeps coherent vertical phase locking,
- keeps independent blink stabilization and mouth outlier protection,
- reduces micro-jitter thresholds without adding a long temporal window.

### Avatar runtime
- keeps the existing transactional hot-swap system,
- preserves adaptive head fit and spring-bone support,
- clears presentation prediction state after a model switch so old-avatar velocity cannot carry into the new avatar.

## Video finding

The supplied 32.65 s Unity recording was inspected frame-by-frame. The recording is 2560x1352 and the decoded average cadence is about 27 fps. A simple object-center trajectory measurement found a strong sample-and-hold signature: within windows classified as active translation, about 77% of adjacent decoded frames changed by less than 0.5 px, followed by larger jumps. That pattern is consistent with a render path that exposes lower-rate accepted samples directly instead of continuously retiming them.

This overlay therefore targets the presentation boundary first rather than replacing the current face detector.

## Comparable-system design choices used

- OpenSeeFace is designed specifically for avatar tracking and emphasizes stable landmarks and 30-60 fps operation.
- KalidoKit separates blink stabilization from general head/pose smoothing and uses minimal easing for avatar rigging.
- OpenVHead filters head-motion landmarks while deliberately leaving expression measurements less filtered because long smoothing windows add expression latency.

The overlay follows the same principle: stronger continuity on body presentation, lighter and channel-specific filtering for eyes/mouth.

## Rollback

Delete:

`Assets/KiwiAvatarSystem/Runtime/Optimization/KiwiTrackingQuality10Controller.cs`

The original tracking scripts are untouched by this overlay.
