# KiwiAvatarSystem v5.1.0 coherent-prediction review

## Residual latency audit

The v5.0 path already removed stale queues, duplicate-frame readbacks, render-boundary
delay, steady-translation smoothing lag, and slow stop/reversal correction. The next
audit found two earlier sources of phase error:

1. the prediction velocity estimate still used a fixed response of 60 even when
   consecutive motion samples strongly agreed; and
2. the prediction clock began when Unity observed a new WebCamTexture frame, although
   the image content represents an exposure centered before that notification.

## Velocity-estimator design comparison

| Design | Onset speed | Static stability | Reversal safety | Total |
|---|---:|---:|---:|---:|
| A. Use raw finite-difference velocity | 5/5 | 1/5 | 2/5 | 8/15 |
| B. Raise all velocity response from 60 to 180 | 5/5 | 2/5 | 3/5 | 10/15 |
| C. Raise 60 toward 180 only with coherent motion | 5/5 | 5/5 | 5/5 | 15/15 |

Design C is implemented independently for rotation, position, and scale. Consistency
is squared before interpolation, keeping the transition conservative. At consistency
0 the response remains 60; at 0.70 it is 118.8; at 1 it is 180.

At a 60 Hz LandMarker cadence, fixed response 60 applies 63.21% of a new velocity in
one sample. Response 118.8 applies 86.19%, and the next coherent response-180 sample
raises the estimate above 99%. A start from a previously stationary velocity uses the
more conservative consistency 0.45 and applies approximately 75.46% before converging
on the following coherent sample.

## Camera-age design comparison

| Design | Phase accuracy | Overshoot risk | Device adaptation | Total |
|---|---:|---:|---:|---:|
| A. Compensate only observation-to-render age | 2/5 | 5/5 | 3/5 | 10/15 |
| B. Add one fixed/full frame interval | 5/5 | 1/5 | 2/5 | 8/15 |
| C. Add measured half-source-interval with a cap | 5/5 | 5/5 | 5/5 | 15/15 |

Design C is implemented. It uses `LatestFreshSourceRateHz`, never the lower accepted
result rate. The default fraction is 0.50 and the absolute cap is 0.020 seconds:

| Camera source rate | Added capture-age estimate |
|---:|---:|
| 120 Hz | 4.17 ms |
| 60 Hz | 8.33 ms |
| 30 Hz | 16.67 ms |
| 15 Hz | 20.00 ms (capped) |

The added lead still passes through geometry quality, motion activation, velocity
consistency, stale-time, and absolute rotation/position/scale prediction bounds.
Stops and reversals therefore do not receive an unconditional half-frame extrapolation.

## Diminishing-return review

1. Raw velocity was rejected because one landmark spike becomes prediction velocity.
2. Global response 180 was rejected because it accelerates incoherent motion too.
3. Linear consistency weighting was tested conceptually but rejected in favor of the
   squared curve; its small onset gain exposes more medium-confidence noise.
4. Fast response above 180 was rejected because the second coherent sample already
   exceeds 99% convergence.
5. Result-rate-derived capture age was rejected because dropped inference results would
   be mistaken for a slow camera and cause severe overprediction.
6. A fixed 16.7 ms offset was rejected because it is wrong for 30/60/120 Hz devices.
7. One full source interval was rejected because exposure content is best approximated
   by the interval midpoint without a hardware timestamp.
8. More than 20 ms was rejected for low-rate cameras because reversal overshoot grows.
9. Disabling the consistency gate was rejected because it protects stops and reversals.
10. Lowering the microscopic dead zones was rejected because the remaining values are
    already below intentional motion and further reduction increases tremble.
11. Lowering sample-direct thresholds was rejected because high-frequency landmark
    jitter can exceed speed thresholds even when its positional amplitude is small.
12. Enabling global direct snapping was rejected because it recreates movement flicker.
13. Increasing prediction caps was rejected because it affects failure severity rather
    than ordinary latency once real motion exceeds the cap.
14. A second tracker remains rejected due to compute cost and disagreement risk.
15. Camera-driver buffering cannot be inferred reliably without a new device capture.

At this point additional default changes require empirical camera-specific evidence;
otherwise their expected gain is smaller than their jitter or overshoot risk.

## Verification

- Unity 2022.3.62f2 batch compilation succeeded.
- KiwiOptimizationValidator: 23/23 passed.
- Source/template equality and safe-installer target hashes passed.
- Runtime controls use settings key v11.
- Windows remains primary and Android/iOS source compatibility is retained.
- iOS physical-device verification was not performed, as requested.
