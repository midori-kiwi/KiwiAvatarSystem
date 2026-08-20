# KiwiAvatarSystem Human Motion v2.2 — Mature VTuber tuning

This package is cumulative: v2, v2.1 avatar-right mapping, shared tilt lock and
rigid mouth attachment are all retained.

## 20:02 recording findings

The supplied recording is about 44.18 s at 2560x1352.

Representative on-screen diagnostics show:
- render roughly 77-89 fps,
- fresh webcam source roughly 20-23 Hz,
- MediaPipe submissions/results roughly 10-13 Hz,
- MediaPipe auxiliary readback roughly 58-88 ms,
- Inference Engine roughly 30-31 ms,
- Inference presence sometimes around 0.49 and later around 0.93,
- backend still reported as MediaPipe FaceLandmarker.

The important configuration clue is `input 480x270`. Human Motion v2 requested a
384 px auxiliary input, but `autoOptimizeCm831=true` caused the CM831 profile to
override it back to 480 px. v2.2 disables that override, so 384 px can actually
be used.

## Mature-system principles applied

- Tracking cadence and render cadence are treated separately.
- Smoothing remains parameter/channel specific instead of applying one long
  whole-face filter.
- Low-rate or irregular cadence receives a tiny additional causal smoothing
  budget; stable high-rate tracking receives almost none.
- Body pose, eye/mouth crops and rigid head-roll correction remain separate
  temporal channels.
- The high-resolution eye/mouth camera texture is not downscaled; only the
  auxiliary MediaPipe inference input is reduced.
- Inference Engine presence threshold is relaxed to 0.35 while the existing
  finite-landmark, geometry and four-consecutive-failure guards remain in place.

## New adaptive cadence retimer

The controller now measures tracking interval deviation.

At stable 30+ Hz:
- extra cadence smoothing approaches zero,
- motion remains very responsive.

At roughly 10-20 Hz or irregular timing:
- position/depth get up to 16 ms of extra causal smoothing near rest,
- fast intentional motion receives only about one third of that boost,
- rotation response is reduced slightly only while cadence is poor.

This avoids using a fixed 50-100 ms movement buffer on a local webcam path that
already has camera/inference latency.

## Backend watchdog

`KiwiTrackingBackendWatchdog` is diagnostic only.

If Inference Engine reports a strong presence score (>= 0.70) for 1.5 s but
MediaPipe remains primary, it logs a warning. That distinguishes a core
Inference-geometry/publish rejection from a presentation-smoothing problem.
