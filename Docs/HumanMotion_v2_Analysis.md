# KiwiAvatarSystem Human Motion v2

## Why the design changed

The second supplied recording (2026-08-20 19:23) was analyzed against the earlier recording.

Measured from the rendered avatar trajectory:
- the large-jump tail improved substantially versus the earlier build,
- 95th-percentile acceleration and jerk were lower,
- but active motion still contained long near-static plateaus followed by correction steps.

The on-screen diagnostic panel in the new recording is the key finding:
- render: about 84 fps,
- fresh source: about 21 Hz,
- submitted / accepted tracking: about 12-13 Hz,
- Inference Engine GPU is active during part of the recording,
- MediaPipe auxiliary readback is still expensive,
- render age can be tens of milliseconds and can become much larger during fallback.

At 84 render fps, a 12-13 Hz accepted pose produces roughly 6-7 display frames per tracking result. If the animation path directly exposes each accepted pose, no amount of high render FPS can look fully continuous.

## Human Motion v2 architecture

The previous overlay inferred "new tracking" from transform changes. That is not reliable because KiwiFaceMotion itself can write the transform every display frame.

v2 instead uses `FacePrecisionTrackingData.frameId`.

Pipeline:

camera / inference
    -> atomic tracking frame
    -> KiwiFaceMotion near-raw spatial mapping
    -> Human Motion sample estimator
    -> render-time prediction
    -> critically-damped presentation
    -> final avatar transform

This gives one temporal owner for body movement.

### Body

KiwiFaceMotion keeps:
- roll-stable geometry anchor,
- depth fusion,
- outlier guard,
- screen-space mapping,
- atomic backend/frame identity.

KiwiFaceMotion no longer owns:
- static pose locking,
- adaptive temporal micro filtering,
- display-rate smoothing,
- duplicate prediction.

The new presentation layer:
- updates only on a new `frameId`,
- uses matched host timing when available,
- estimates position / rotation / depth velocity,
- predicts only through the measured sample interval,
- decays prediction after a stale interval,
- speeds up on intentional movement,
- damps harder at rest,
- immediately kills old velocity on a reversal,
- resets on backend changes and avatar hot swaps.

### Eye / mouth

Eye and mouth are intentionally not given the same body filter.

They keep:
- render-rate crop interpolation,
- matched-frame-age compensation,
- independent blink handling,
- crop-local contour lock,
- coherent vertical phase lock,
- mouth outlier / camera-edge protection.

The direct-during-motion crop snap is disabled because it exposes the same sample-and-hold cadence.

Blink confirmation is reduced to one coherent sample plus a short render fade. At a 10-30 Hz tracker, two-sample confirmation can visibly delay a normal blink.

### Throughput

The MediaPipe path is auxiliary while Inference Engine is primary.

v2 lowers the auxiliary MediaPipe tracking input to 384 px and refresh to 8 Hz. The visible eye/mouth source texture remains at the camera's original resolution. This is intended to reduce the expensive DX11 auxiliary readback observed in the recording while retaining MediaPipe for ROI correction, expressions, reacquisition and fallback.

## Public-system comparison

The design follows public principles used by mature VTuber software rather than attempting to reproduce any proprietary Hololive implementation:
- high render rate and tracking rate are separate concerns,
- smoothing is channel-specific,
- stronger smoothing reduces jitter but costs latency,
- tracking and physics should not fight over the same value,
- small movement buffering/retiming can remove stutter when update cadence is irregular.

## What to verify in the next recording

Open the tracking/latency panel and record 20-30 seconds of:
1. slow left-right translation,
2. fast left-right translation,
3. slow yaw,
4. fast yaw,
5. diagonal motion,
6. several blinks and mouth-open/close cycles.

The next target is:
- accepted tracking cadence ideally above 20 Hz,
- fresh source cadence materially above the current ~21 Hz,
- no visible 6-7-frame sample hold,
- low overshoot at stops/reversals,
- eyes/mouth remaining phase-locked to the face.
