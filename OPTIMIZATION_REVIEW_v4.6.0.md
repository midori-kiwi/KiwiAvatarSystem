# KiwiAvatarSystem v4.6.0 movement latency and flicker review

Date: 2026-08-11

## Evaluated directions

| Direction | Movement latency | Motion continuity | Rest stability | Score |
|---|---:|---:|---:|---:|
| Direct position snap at each render target | 10 | 4 | 8 | 22/30 |
| High-response exponential position smoothing | 7 | 9 | 9 | 25/30 |
| Velocity feed-forward with blended LandMarker correction | 10 | 10 | 9 | 29/30 |
| Larger prediction lead/caps on the direct-snap path | 9 | 3 | 6 | 18/30 |

The third direction is implemented. A direct position snap minimizes mathematical
delay but turns every accepted-sample correction into a visible model jump. Pure
exponential smoothing hides the jump by delaying all motion. Feed-forward advances
steady motion continuously and smooths only the correction error, removing both
verified problems without slowing the main movement.

## Implemented behavior

- The display position advances by measured render-position velocity every actual elapsed render interval.
- A newly accepted LandMarker position affects the correction term rather than replacing the whole position.
- Correction uses an exponential response of 70, approximately a 14.3 ms time constant.
- Constant-velocity feed-forward reaches the next target with zero smoothing lag.
- LateUpdate and `onBeforeRender` share a Stopwatch-based delta accumulator, so elapsed time is never applied twice.
- Rotation and depth retain the v4.5 intentional-motion direct path.
- Rest and sub-dead-zone movement retain the existing display-rate anti-jitter smoother.
- Prediction quality, consistency, stale-time, and absolute displacement caps remain unchanged.
- Runtime controls expose `Flicker-free predictive movement` and correction response.
- Settings key v7 prevents a saved v6 direct-snap configuration overriding these defaults.

## Numeric checks

- Steady case: current 0.000, velocity 1.0, dt 0.010, target 0.010 -> output 0.010 exactly.
- New correction: current 0.000, velocity 0, dt 0.010, target 0.010 -> output is between 0 and 0.010, never a snap.
- One 0.020-second correction and two 0.010-second corrections agree within 0.000001.
- Previous late-step diagnosis remains: the old response-110 / 1/240 step applied only 36.75% in-frame.

## Review passes to improvement saturation

1. Reconfirmed movement delay is strongest on Kiwi X/Y translation.
2. Separated source-frame age, prediction age, and final display correction age.
3. Audited runner-to-sample timing and retained exact LIVE_STREAM submission timestamps.
4. Audited sample-to-display timing in LateUpdate.
5. Audited the second display advance in `onBeforeRender`.
6. Confirmed v4.5 removed final motion delay but introduced direct position correction jumps.
7. Identified those sample-arrival jumps as the likely movement flicker path.
8. Compared keeping direct snap against the reported visual failure.
9. Compared response-only smoothing and quantified its unavoidable following delay.
10. Compared increasing prediction caps and rejected additional overshoot.
11. Selected velocity feed-forward plus error-only correction.
12. Kept the predictor LandMarker-primary rather than synthesizing independent motion.
13. Added a separate predicted render-position velocity.
14. Reset render velocity to zero whenever prediction is unavailable.
15. Scaled velocity by quality, consistency, configured strength, and motion gate.
16. Stopped feed-forward when prediction age reaches its lead ceiling.
17. Stopped feed-forward when the absolute position prediction cap is hit.
18. Preserved the current sample as the correction anchor.
19. Applied velocity before correction so steady motion has no lag.
20. Applied exponential correction after velocity so sample changes cannot snap.
21. Chose response 70 as a short 14.3 ms correction time constant.
22. Kept position activation aligned with prediction onset at 0.005/second.
23. Required position error above the existing micro dead zone.
24. Left rotation on render-boundary direct motion.
25. Left scale/depth on render-boundary direct motion.
26. Left rest rotation, position, and scale smoothing active.
27. Replaced approximate onBeforeRender dt with exact Stopwatch elapsed time.
28. Shared one display-time accumulator between LateUpdate and onBeforeRender.
29. Prevented multiple onBeforeRender callbacks from double-advancing movement.
30. Allowed new late LandMarker results to enter the same continuous resampler.
31. Preserved tracking-loss neutral return.
32. Preserved calibration and reacquisition display resets.
33. Preserved avatar-centric horizontal direction.
34. Preserved the existing 0.100-second prediction ceiling.
35. Preserved position displacement cap relative to model height.
36. Preserved prediction reversal consistency.
37. Added a pure allocation-free position-resampler math path.
38. Tested exact zero-lag steady feed-forward.
39. Tested correction output cannot equal a one-frame snap.
40. Tested exponential correction invariance across render subdivisions.
41. Tested non-finite direct-motion gates remain rejected.
42. Added current-scene defaults for predictive movement and response.
43. Added Ultra-preset defaults.
44. Added runtime toggle and response slider.
45. Added finite, clamped save/load persistence.
46. Moved saved settings from v6 to v7.
47. Updated runtime source and safe-upgrade template identically.
48. Added the prior v4.5 source hash to the safe-upgrade allowlist.
49. Updated installer target SHA-256 to the exact v4.6 source.
50. Recompiled under Unity 2022.3.62f2 and passed all 18 optimization checks.
51. Rechecked mouth/eye camera-edge safety remains unchanged.
52. Rechecked no new heap allocation was introduced in the render loop.
53. Reviewed a lower correction response: it visibly delays direction changes.
54. Reviewed a higher correction response: it approaches the rejected direct-snap flicker.
55. Reviewed acceleration prediction: at the observed low result cadence it amplifies reversal noise.

Review stopped at pass 55. Further correction slowing reintroduces movement delay;
faster correction converges toward flicker; larger extrapolation increases overshoot.
The selected design is the remaining Pareto-best point for the two reported failures.

## Final verification

- Unity 2022.3.62f2 batch compilation: passed.
- Optimization validator: 18/18 passed.
- Source/template SHA-256 equality: passed.
- Batch exit code: 0.
