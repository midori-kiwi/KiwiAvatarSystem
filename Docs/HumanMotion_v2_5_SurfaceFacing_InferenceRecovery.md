# Human Motion v2.5 — Surface Facing + Inference Recovery

This package is cumulative. v2 through v2.4 remain included.

## 21:24:55 recording

Decoded:
- duration ~39.04 s
- 2560x1352
- recording cadence ~25.87 fps

Measured avatar-center motion still has a strong hold-and-step signature:
during active windows about 65% of adjacent decoded frames change by less than
0.5 px.

The visible diagnostic panel is more important:
- render roughly 47 fps,
- camera source roughly 46 Hz,
- MediaPipe submit/result roughly 8 Hz,
- backend remains MediaPipe FaceLandmarker,
- Inference = 0.0 ms,
- p = 0.00,
- MediaPipe auxiliary/readback roughly ~100 ms.

So v2.4 is not limited mainly by Unity rendering. The intended Inference Engine
primary path is not running.

## v2.5 tracking recovery

`KiwiInferenceRecoveryBootstrap` applies the hybrid preset before scene Start and
checks the actual Resources model/shader.

If the tracker object is missing after source startup, it performs at most two
bounded calls to the Runner's existing `InitializeSentisTracker`.

If the ONNX cannot be imported, the Console message also checks whether the
local file is suspiciously small (<1 KB), which usually means the repository has
a Git LFS pointer instead of the real ONNX. In that case `git lfs pull` is
required; display smoothing cannot substitute for a missing GPU model.

## Side-view part detachment

Frames around 10-14 s still show the eye/mouth surface patches separating from
the Kiwi silhouette. The v2.4 depth+yaw guard is not reliable enough because
visual avatar rotation and `RenderedYawDegrees` are not the same thing.

v2.5 instead samples the actual fitted surface around each part center, builds a
local surface normal, calibrates its neutral sign, and fades each part by its
real camera-facing score.

This works per part and per model shape:
- a far/tangent eye fades before it can float outside the body,
- the near eye can remain visible,
- the mouth gets a slightly more permissive threshold,
- yaw is only a fallback when surface orientation is unavailable.

## Crop vs semantic mask

v2.4 expanded both the source crop and semantic mask. The new recording shows
very large visible mouth regions around 17-23 s.

v2.5 separates the two concerns:

- source crop: larger overscan (so real eye/lip pixels are always available),
- semantic mask: tighter around the actual eye/lip contour.

Eye mask margin returns to ~0.105.
Mouth mask margin returns to ~0.020 while source crop safety is expanded.

This is the intended VTuber-style separation: sample generously, display only
the semantic feature.
