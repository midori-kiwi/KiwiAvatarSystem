# KiwiAvatarSystem v5.0.0 face-synchronous optimization review

## Goal and measured boundary

The complete camera-to-render path was reviewed again without a fixed review-count
limit. Software can remove queueing, stale-frame presentation, avoidable filtering,
and render-boundary age. It cannot make a 30 fps camera provide ground-truth samples
more often than every 33.3 ms, or recover motion hidden by exposure and USB transfer.
For that reason v5.0 targets the highest available capture rate and minimizes the
residual display error between real measurements without inventing a second tracker.

## Design comparison

| Design | Motion latency | Jitter/flicker | Reversal accuracy | Device safety | Total |
|---|---:|---:|---:|---:|---:|
| A. Present every accepted position as an immediate snap | 5/5 | 1/5 | 5/5 | 5/5 | 16/20 |
| B. Use one globally stronger position response | 4/5 | 2/5 | 4/5 | 5/5 | 15/20 |
| C. Predict steady motion; accelerate correction only on inconsistency | 5/5 | 5/5 | 5/5 | 5/5 | 20/20 |

Design C is implemented. A direct snap was rejected because noisy landmark updates
become visible translation flicker. A globally high response was rejected because it
spends stability even during constant-velocity motion, where feed-forward already
removes smoothing lag. The adaptive design uses the existing velocity-consistency
signal to separate these cases.

## Implemented changes

- Default webcam request: 1280x720 at 60 fps instead of 30 fps.
- Equal-resolution device modes: highest reported frame rate wins using a precise
  floating-point comparison, so fractional rates are not truncated into a tie.
- Constant velocity: response 45 plus continuous velocity feed-forward.
- Stop, acceleration, or reversal: correction rises continuously toward response 180
  as velocity consistency falls.
- Runtime control: `Stop / reversal recovery`, saved with the v10 tracking settings.
- The source and historical-path tracking template remain byte-identical; the safe
  installer recognizes the previous source hash and targets the v5.0 hash.

## Quantitative evaluation

Exponential correction has residual `exp(-response / render_fps)` after one rendered
frame. The selected recovery response 180 has a 5.56 ms time constant.

| Render rate | Response 45 residual | Response 180 residual | Improvement |
|---:|---:|---:|---:|
| 30 fps | 22.31% | 0.25% | 98.9% less residual |
| 60 fps | 47.24% | 4.98% | 89.5% less residual |
| 120 fps | 68.73% | 22.31% | 67.5% less residual |

At 120 fps the remaining recovery residual is 4.98% after two frames and 1.11%
after three. During steady motion this correction is paired with feed-forward, so
the response-45 column is not an added steady-motion phase delay.

## Diminishing-return review

1. A FIFO of camera or inference frames remains prohibited because it creates age.
2. The one-slot fresh-frame mailbox remains the minimum-latency ownership model.
3. A 60 fps request was selected; forcing a fabricated rate above device support was rejected.
4. 1280x720 was retained because 640-wide source capture reduces facial detail.
5. Only inference is downscaled to 960 width; visible eye/mouth sampling stays full resolution.
6. Response 180 was selected for inconsistent motion.
7. Response 220 improves the 120 fps one-frame residual by only 6.32 percentage points.
8. Response 240 improves it by another 2.46 points but increases measurement-noise visibility.
9. Response 300 approaches a snap and was rejected for the reported movement flicker.
10. Globally raising response 45 was rejected because it damages steady-motion stability.
11. Lowering response 45 was rejected because it slows correction of model error.
12. Disabling prediction was rejected because it restores sample-and-hold lag.
13. Unbounded prediction was rejected because reversals overshoot.
14. The existing measured-age and interval caps remain enabled.
15. Static pose locking remains off by default to avoid sticky slow motion.
16. The microscopic filter remains enabled because its thresholds are below intentional motion.
17. Rotation and scale already bypass the final smoother during intentional motion.
18. onBeforeRender late latching remains enabled and removes the last Unity update boundary.
19. Reprocessing duplicate webcam frames remains disabled.
20. Multiple simultaneous readbacks remain rejected due to render contention.
21. CPU/CPUAsync pool capacity remains two because only one readback is owned at a time.
22. Removing face-part edge hiding was rejected because incomplete camera imagery must not display.
23. Reducing surface-fit geometry was rejected because it trades shape accuracy for little gain.
24. Reducing spring-bone updates was rejected because it makes tails/accessories unnatural.
25. Adding a second tracker was rejected because it introduces disagreement and more compute age.

The remaining software-only alternatives either increase visible jitter, reduce landmark
precision, introduce extrapolation error, or save less latency than their regression risk.
Further trustworthy improvement now requires a new live capture with source/submission/
result/render-age diagnostics and the actual camera rate visible.

## Verification

- Unity 2022.3.62f2 batch compilation succeeded.
- KiwiOptimizationValidator: 21/21 passed.
- Source/template SHA-256 alignment passed.
- Windows remains primary; Android/iOS source compatibility is retained.
- iOS physical-device verification was not performed, as requested.
