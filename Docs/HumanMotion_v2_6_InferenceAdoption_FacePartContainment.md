# Human Motion v2.6 — Inference Adoption + Face-Part Containment

This is cumulative and includes all v2 through v2.5 changes.

## Video analyzed

`... 2026-08-20 21-47-33.mp4`

Decoded:
- duration ~39.37 s
- resolution 2560x1352
- recording cadence ~25.53 fps

### Render-motion comparison vs 21:24 recording

Using the same brown-avatar center measurement:

21:24:
- median adjacent step ~0.45 px
- 95th percentile step ~24.0 px
- all-frame <0.5 px share ~52%

21:47:
- median adjacent step ~0.33 px
- 95th percentile step ~17.9 px
- all-frame <0.5 px share ~58%

So v2.5 reduced ordinary large-step motion, but motion became more hold-like.
Restoring the intended high-rate Inference backend is now more important than
adding more smoothing.

## Diagnostic panel

From 5-33 s:
- Render ~48-51 fps
- Camera source ~46-50 Hz
- MediaPipe results ~8-9 Hz
- Backend remains MediaPipe
- Inference 0.0 ms / p=0.00

Around 34 s:
- Inference suddenly becomes ~98.6 ms
- p≈0.45
- Backend still remains MediaPipe

This gives two concrete failures:

1. a tracker object can exist but make no progress for ~30 s;
2. once a valid-presence inference result finally appears, the Runner still
   rejects it before primary adoption.

v2.6 addresses both.

## Inference recovery

`KiwiInferenceRecoveryBootstrap` now restarts a tracker that exists but has made
no actual presence progress. After restart it immediately seeds the newest
MediaPipe ROI rather than waiting for another callback.

## Continuity-guarded Inference adoption

`KiwiInferenceAdoptionInstaller` changes only the zero-geometry-quality rejection
inside StoreSentisTrackingData.

Normal positive-quality results are unchanged.

A zero-quality result is allowed only when:
- landmark dimensions are finite and positive,
- eye span is within 0.45x to 2.20x of the currently published face,
- center displacement is bounded,
- the face center is within a broad normalized camera region.

Such a result is published with geometryQuality=0.10, so downstream
quality-aware smoothing remains conservative.

This is safer than globally lowering MediaPipe geometry validation.

## 13-16 s detached far-side eye

The recording still shows a small eye patch detached from the right edge of the
Kiwi during a deep side turn.

v2.5's surface-normal/yaw logic was not sufficient.

v2.6 adds a yaw-independent depth-ratio guard:
- get both fitted eye centers in world/camera space,
- compute |leftZ-rightZ| / worldEyeDistance,
- fade only the physically farther eye,
- use CanvasRenderer alpha as the final gate so shader behavior cannot leave a
  visible detached patch.

## Large mouth around 22-23 s

The source crop must stay large enough not to clip camera pixels, but that does
not mean the rendered mouth should become arbitrarily large.

v2.6 keeps overscan while adding a final shader display cap:
- maximum visible width 0.72
- maximum visible height 0.68

This runs after MouthDisplaySizeLock, so expression zoom cannot grow the final
visible mouth beyond the cap.

The semantic mouth contour is also tightened to margin 0.012 while source crop
overscan remains generous.
