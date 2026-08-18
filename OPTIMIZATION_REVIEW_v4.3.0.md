# KiwiAvatarSystem v4.3.0 raw-response review

Date: 2026-08-11  
Source recording: `KiwiAvatarSystem - Face Landmark Detection - Windows, Mac, Linux - Unity 2022.3.62f2 _DX11_ 2026-08-11 09-58-05.mp4`

## Recording findings

- Duration: 42.96 seconds.
- Resolution: 2560x1352.
- Recorded average frame rate: approximately 12.18 fps.
- Runtime panel in the recording: quality 1.00, inference 8.6 ms,
  render/result age 57.7 ms.

The inference itself was not the dominant delay. The previous v4.2 path could
predict only 28 ms and also capped prediction to 95% of one accepted-sample
interval. At the recorded 57.7 ms result age, roughly 30 ms therefore remained
uncompensated before display smoothing. Slow motion below 12 degrees/second was
also prevented from using most of the prediction lead.

The recording does not contain a side-by-side raw camera view, so exact
camera-to-avatar phase difference cannot be measured from pixels alone. The
runtime host timestamps shown in the panel are used as the authoritative delay
measurement.

## Three evaluated directions

| Direction | Latency | Continuity | Jitter | Throughput | Risk | Weighted total |
|---|---:|---:|---:|---:|---:|---:|
| A. Directly render each raw LandMarker result | 8 | 3 | 3 | 8 | 8 | 41/70 |
| B. Only increase inference/render target fps | 6 | 6 | 6 | 5 | 7 | 43/70 |
| C. Full measured-age compensation + continuous fast display pose + remove unrelated debug presentation | 10 | 9 | 8 | 9 | 8 | 63/70 |

Direction C received the highest score and is the only implemented direction.
It addresses both the measured age and visible continuity without replacing
MediaPipe or inventing a second tracking source.

## Implemented result

- Removed the one-sample-interval restriction in Ultra mode.
- Raised the absolute compensation ceiling from 28 ms to 100 ms.
- Raised prediction strength from 0.90 to 1.00.
- Reduced normal rotation prediction activation from 12 to 3 degrees/second.
- Reduced position/depth prediction activation thresholds proportionally.
- Raised continuous display response from 48/96 to 110/220.
- Disabled static pose locking by default to prevent hold-then-jump motion.
- Retained adaptive micro-jitter filtering and reversal consistency gates.
- Disabled 478-point debug landmark annotation rendering by default.
- Added accepted result-rate diagnostics and runtime controls.
- Migrated the settings sentinel to v5 so older slow values do not override v4.3.

## Open-ended self-review

Review was not stopped at ten passes. It continued until no further safe,
code-verifiable correction was found:

1. Recording metadata and visible diagnostics.
2. LandMarker submission loop and absence of a fixed 10 Hz throttle.
3. Callback submission-to-arrival timing.
4. Result-age calculation.
5. One-interval prediction restriction.
6. Absolute prediction safety ceiling.
7. Slow-motion prediction gate.
8. Direction-reversal consistency suppression.
9. Rotation extrapolation cap.
10. Position and scale extrapolation caps.
11. Display-rate smoothing response.
12. onBeforeRender double-step prevention.
13. Static-lock hold/release behavior.
14. Micro-jitter behavior after disabling static lock.
15. Hidden 478-point annotation presentation cost.
16. Tracking-result Hz thread-safe publication.
17. Runtime control ranges and persistence.
18. Old PlayerPrefs migration behavior.
19. Active scene serialization.
20. Windows/Android/iOS API compatibility.
21. Right/right-down direction invariants.
22. Template/source and installer SHA-256 integrity.
23. Unity compilation and automated validator execution.
24. Post-validation review found the remaining 12 degrees/second slow-motion
    gate; it was reduced to 3 degrees/second and revalidated.

Final Unity validation passed 15/15 checks. The 100,000-iteration direction
mapping check completed in 15.21 ms. No new compiler-warning category was added.

## Safety boundary

Compensation is not mathematically unbounded: a 100 ms stale-time-aware ceiling,
geometry quality, velocity consistency, reversal suppression, and absolute
rotation/position/scale caps remain. Removing all safety limits would create
large overshoot on direction reversals and tracking loss and would be less like
the raw camera, not more.
