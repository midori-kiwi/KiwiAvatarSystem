# KiwiAvatarSystem v5.2.0 Windows readback optimization review

## Supplied recording evidence

The supplied v5.1 recording is 28.16 seconds, H.264, 2560x1352, with 357 decoded
frames. Its variable frame timestamps range from 33.37 to 200.20 ms. Seven enlarged
diagnostic samples from 4-27 seconds show:

| Measurement | Observed range |
|---|---:|
| Unity render | 50.3-54.5 fps |
| LandMarker input | 960x540 |
| Fresh camera source | 7.2-7.8 Hz |
| Submission | 7.2-7.9 Hz |
| Accepted results | 7.2-7.9 Hz |
| DX11 AsyncGPUReadback | 89.5-95.8 ms |
| Source observation to result | 99.3-104.0 ms |
| Estimated model/post-readback portion | approximately 5-10 ms |

The avatar render loop is not the bottleneck. Camera cadence and GPU-to-CPU readback
dominate; increasing prediction further would hide symptoms while increasing reversal
overshoot.

## Design comparison

| Design | Expected latency | Landmark precision | Render stability | Total |
|---|---:|---:|---:|---:|
| A. Keep 1280 source and reduce only tracking input to 640 | 2/5 | 5/5 | 4/5 | 11/15 |
| B. Keep CPUAsync and use 640 camera/input | 3/5 | 4/5 | 5/5 | 12/15 |
| C. Windows 640 camera/input with bounded synchronous CPU readback | 5/5 | 4/5 | 5/5 | 14/15 |

Design C is implemented. At 640x360 the readback surface is 230,400 pixels, 75%
smaller than 1280x720 and 55.56% smaller than the recorded 960x540 tracking surface.
Synchronous readback is restricted to Windows, where the supplied recording proves
that the asynchronous DX11 queue is the dominant cost. Android/iOS retain CPUAsync.

## Implemented changes

- Preferred default webcam width: 1280 to 640.
- Selected default mode: 640x360 at 30 fps; higher modes remain available manually.
- Default and scene LandMarker maximum input width: 960 to 640.
- Windows Editor/Standalone image read mode: CPUAsync to CPU.
- Non-Windows image read mode: CPUAsync retained.
- Runtime diagnostics add actual camera width/height.
- The previous `inference` label is clarified as `source->result`.
- Estimated post-readback/model time is displayed separately as `model est`.

## Expected effect and limits

The exact new readback time cannot be claimed until another live recording is made.
The change directly removes both measured contributors: the slow high-resolution camera
mode and the 89-96 ms asynchronous readback queue. If the camera delivers its requested
30 fps, ground-truth cadence can rise from about 7.5 Hz toward 30 Hz. Synchronous CPU
readback may cost a few render milliseconds, but its surface is deliberately bounded;
this is preferable to presenting every result roughly 100 ms late.

The visible eye/mouth source also becomes 640x360 by default. This is the explicit
quality/latency tradeoff selected from the recording. A higher camera mode remains
available from MediaPipe image-source configuration when texture sharpness matters more
than latency.

## Diminishing-return review

1. More display prediction was rejected because recorded measurement age is already ~100 ms.
2. Larger capture-age compensation was rejected because it increases reversal overshoot.
3. 960 tracking with synchronous readback was rejected because it blocks on 2.25x more pixels.
4. 640 tracking with the old 1280 camera was rejected because source cadence remained unresolved.
5. CPUAsync at 640 was retained as a fallback but rejected as Windows default because queue age dominates.
6. Direct Windows GPU images were rejected because this plugin path supports OpenGLES3, not DX11.
7. 320-wide input was rejected because face detection/edge landmark precision becomes fragile.
8. 640x360 was selected over 640x480 because it preserves the existing 16:9 capture shape and reads fewer pixels.
9. 60 fps remains available but is not the default because the recorded device delivered only ~7.5 Hz under the high-rate request.
10. LIVE_STREAM remains enabled; IMAGE/VIDEO modes add blocking latency.
11. The latest-frame mailbox remains enabled and prevents a FIFO backlog.
12. Single-in-flight remains optional because MediaPipe already drops busy frames.
13. Model blendshapes remain enabled because eyes/mouth expressions require them.
14. Visible texture resolution is not downscaled a second time after camera acquisition.
15. Further default changes require v5.2 camera/readback diagnostics from a new recording.

## Verification

- Unity 2022.3.62f2 batch compilation succeeded.
- KiwiOptimizationValidator: 24/24 passed.
- Windows CPU / non-Windows CPUAsync compile-time selection passed.
- Runner and motion source/template hashes passed.
- iOS physical-device verification was not performed, as requested.
