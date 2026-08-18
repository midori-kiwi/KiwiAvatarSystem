# KiwiAvatarSystem v4.2.0 smooth-tracking review

Date: 2026-08-11  
Primary target: Windows / Unity 2022.3.62f2  
Secondary targets: Android and iOS source/build compatibility  
iOS physical-device validation: out of scope

## Root cause

The accepted tracking pose was updated only when a new LandMarker result arrived.
Normal intentional motion crossed the direct thresholds and assigned that result
straight to the sample pose. At a typical 30 inference fps on a 60/120 Hz display,
the transform therefore remained still for two to four render frames and jumped
at the next result. The 0.016 second prediction window covered less than half of
one 30 fps interval, and the late-render path could render the raw sample directly.
This was a sample-and-hold staircase, not insufficient ordinary damping.

## Three evaluated directions

Scores are 1-10; latency and visual continuity are weighted twice.

| Direction | Latency | Continuity | Jitter | Portability | Risk | Weighted total |
|---|---:|---:|---:|---:|---:|---:|
| A. Raise LandMarker inference fps only | 7 | 6 | 5 | 4 | 6 | 41/70 |
| B. Add a strong low-pass filter to accepted samples | 4 | 9 | 9 | 10 | 8 | 53/70 |
| C. Display-rate adaptive resampling plus bounded age prediction | 9 | 10 | 9 | 10 | 8 | 65/70 |

Direction C received the highest score and is the only implemented direction.
It preserves accepted LandMarker results as the source of truth, generates a
continuous pose every display frame, and compensates most of the small smoothing
delay with already-bounded motion prediction.

## Implemented fix

- Added a separate display pose for rotation, position and scale.
- Resampled toward the newest predicted target every render frame with an
  FPS-independent exponential response.
- Used error-adaptive response: 48 at small error and up to 96 for fast catch-up.
- Removed direct raw-sample rendering from the late-render path.
- Allowed prediction up to 0.028 seconds and 95% of the measured sample interval.
- Limited a genuinely newer onBeforeRender sample to a small same-frame correction,
  avoiding a double full smoothing step.
- Synchronized the display pose after calibration and while returning to neutral.
- Explicitly serialized v4.2 defaults into the current Face Landmark Detection scene.
- Added runtime toggle and response sliders with persistent, range-checked settings.
- Added an automated frame-rate-invariance and sample-hold regression check.

## Ten self-review passes

1. Data-flow review: Face LandMarker remains the sole tracking source.
2. Staircase review: no direct `RenderRotation(_sampleRotation)` path remains.
3. Latency review: adaptive response and bounded prediction avoid a heavy low-pass delay.
4. FPS review: exponential integration matches at 60 and 120 Hz over equal elapsed time.
5. Late-latch review: a new real sample is preferred before prediction.
6. Double-step review: onBeforeRender correction is capped after LateUpdate.
7. Jitter review: micro-filter, static lock, and prediction rest gates remain intact.
8. Direction review: right yaw, right roll and combined right/down tests remain passing.
9. Lifecycle review: startup, calibration and tracking-loss state were traced.
10. Upgrade review: source/template hashes and safe installer version were checked.

## Five additional review passes

Further improvement was possible, so five more passes were completed.

11. Pre-calibration review: added a guard so onBeforeRender cannot apply an uncalibrated pose.
12. Scene migration review: explicitly added all Ultra v4.2 defaults to the active scene.
13. Tuning review: exposed base and fast display responses in the runtime panel.
14. Persistence review: save/load uses bounded values and safe defaults for older settings.
15. Final integration review: Unity batch compilation and all 13 validator checks passed.

## Verification

- Unity 2022.3.62f2 compilation: passed.
- Optimization validator: 13/13 passed.
- Tracking math hot loop: 100,000 direction mappings in 14.98 ms.
- Face motion main/template SHA-256 pair: identical.
- Existing warnings only: two MediaPipe obsolete refresh-rate warnings and four
  unused debug-field warnings; no new warning category.

The batch run proves compilation, invariants, hashes and deterministic smoothing
behavior. Final perceptual tuning still requires observing the Windows webcam feed;
camera exposure, inference throughput and device-specific capture buffering cannot
be measured in batch mode.
