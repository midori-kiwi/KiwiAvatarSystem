# KiwiAvatarSystem v5.3.0 latency and face-part flicker review

## Supplied recording evidence

The supplied v5.2 recording (`2026-08-11 22-25-12`) is 30.18 seconds,
H.264, 2560x1352, and averages 26.28 encoded frames per second. Enlarged
diagnostic samples at 4, 8, 12, 16, 20, 24, and 28 seconds show:

| Measurement | Observed range |
|---|---:|
| Unity render | 87.5-104.4 fps |
| Camera / LandMarker input | 640x360 / 640x360 |
| Fresh camera source | 15.7-18.5 Hz |
| Submission | 16.7-18.0 Hz |
| Accepted results | 16.8-19.2 Hz |
| Windows synchronous readback | 45.3-48.2 ms |
| Source observation to result | approximately 33-86 ms |
| Estimated model/post-readback work | approximately 0-12.6 ms |

v5.2 substantially improved the prior 7.2-7.9 Hz pipeline, but synchronous
readback remains the largest measured delay. The body renders and predicts at
about 90-100 fps while eye/mouth crop, contour, rotation, and blink visibility
were still replaced as discrete 16-19 Hz samples. That cadence mismatch is the
primary source of visible eye/mouth stepping and flicker.

## Design comparison

| Design | Latency | Precision | Flicker | Risk | Total |
|---|---:|---:|---:|---:|---:|
| A. Keep raw 16-19 Hz face-part sample jumps | 5/5 | 5/5 | 1/5 | 5/5 | 16/20 |
| B. Add conventional slow smoothing | 2/5 | 4/5 | 5/5 | 4/5 | 15/20 |
| C. 480x270 inference plus high-response render-rate resampling | 5/5 | 4/5 | 5/5 | 4/5 | 18/20 |

Design C is implemented. The visible eye/mouth texture remains the 640x360
camera image; only LandMarker inference is reduced to 480x270. This reduces the
synchronous readback surface from 230,400 to 129,600 pixels (43.75%) while
retaining substantially more facial geometry than the rejected 320-wide option.

## Implemented changes

- Windows LandMarker input defaults to 480x270; Android/iOS keep CPUAsync.
- The redundant `WaitForEndOfFrame` is skipped when the persistent downscaled
  RenderTexture is used. `ReadPixels` remains the required ordered GPU sync.
- Eye/mouth crop position is resampled at render rate with response 180/200.
- Crop size and velocity estimation use high-response values, eliminating the
  old multi-sample size lag.
- Face-part prediction is OFF: advancing a crop ahead of an older camera image
  causes edge shimmer and reversal overshoot.
- Mask contour points are interpolated at render rate with a 5.56 ms time
  constant and fixed preallocated arrays.
- Eye close/open visibility is faded every render frame instead of changing
  Canvas alpha as a binary LandMarker-sample switch.
- Eye/mouth counter-rotation is interpolated at render rate.
- Contour shader arrays stop uploading after convergence.
- The old mouth-height calibration path defaults OFF so enabling interpolation
  cannot reintroduce downward mouth drift.
- Input width, face-part interpolation, crop/contour/rotation responses, blink
  fade, and mouth-height lock are adjustable and persistent in the runtime panel.

At 100 render fps, response 180 applies 83.5% of a new face-part correction in
the first frame and 97.3% in two frames. Response 200 applies 86.5% and 98.2%.
This removes the 16-19 Hz staircase without adding a conventional long filter.

## Diminishing-return review

1. Raw sample replacement was rejected because the recording visibly exposes its cadence.
2. A 30-60 ms low-pass filter was rejected because it directly adds the delay being removed.
3. 320-wide inference was rejected because eye and lip landmark precision becomes fragile.
4. 640-wide inference was rejected as the default because measured readback is still 45-48 ms.
5. 480-wide inference was selected as the remaining precision/readback balance.
6. Downscaling the visible face texture was rejected; only inference is reduced.
7. Face-part crop prediction was rejected because camera pixels cannot be predicted safely.
8. Unbounded contour response was rejected because it recreates hard sample jumps.
9. Response 180-200 was selected because it settles within two 100 fps frames.
10. Binary Canvas alpha blink hiding was rejected because threshold crossings flicker.
11. Render-rate material visibility was selected and retains complete blink hiding.
12. Per-frame contour allocation was rejected; all buffers remain fixed and reusable.
13. Permanent per-frame contour uploads were removed after convergence.
14. Mouth-height calibration remains available but was rejected as a default due to drift risk.
15. Increasing body prediction was rejected because the remaining measured delay is readback age, not display interpolation.
16. A second face tracker was rejected; MediaPipe Face LandMarker remains the only source of truth.

Further source-only changes would trade meaningful facial precision or introduce
prediction artifacts. The next useful evidence is a live v5.3 recording showing
the new readback, source/result cadence, and eye/mouth behavior.

## Verification

- Unity 2022.3.62f2 batch compilation succeeded.
- KiwiOptimizationValidator: 25/25 passed.
- Runner and motion source/template SHA-256 match.
- Windows CPU / non-Windows CPUAsync selection remains intact.
- No iOS physical-device verification was performed, as requested.
