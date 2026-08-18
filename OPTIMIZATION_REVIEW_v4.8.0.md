# KiwiAvatarSystem v4.8.0 latency analysis and design review

Analyzed recording: `D:/Users/main/ビデオ/Captures/KiwiAvatarSystem - Face Landmark Detection - Windows, Mac, Linux - Unity 2022.3.62f2 _DX11_ 2026-08-11 19-22-33.mp4`

## Evidence

- Duration: 50.228 seconds; 2560x1352 H.264; 23.9308 fps.
- After Play stabilizes, the runtime panel reports about 40-48 render fps.
- Accepted Face LandMarker result cadence is only about 6-8 Hz.
- Inference is generally several to tens of milliseconds, so inference compute
  alone cannot explain the low result cadence.
- The v4.7 loop checked `_liveStreamRequestInFlight` before reading
  `WebCamTexture.didUpdateThisFrame`. Every camera update during an active
  request was therefore invisible to the coroutine.
- The coroutine also pauses during AsyncGPUReadback. A freshness check performed
  only inside that coroutine can miss an update even without the explicit gate.

## Compared designs

| Design | Latency | Jitter/flicker | Accuracy | Load | Total |
|---|---:|---:|---:|---:|---:|
| A. Increase prediction/smoothing only | 1/5 | 2/5 | 2/5 | 5/5 | 10/20 |
| B. Keep one request in flight, add a latest-frame mailbox | 3/5 | 4/5 | 5/5 | 5/5 | 17/20 |
| C. Observe every camera update, submit every fresh frame, use MediaPipe flow control | 5/5 | 5/5 | 5/5 | 4/5 | 19/20 |

Design C is implemented. Design B remains as an optional low-load toggle.
Design A was rejected because extrapolating a 6-8 Hz staircase can hide the
symptom but cannot recover missing observations and can overshoot reversals.

## Implemented changes

- Camera freshness is observed from `Update`, independently of the readback
  coroutine's current yield state.
- A one-slot, generation-counted mailbox stores only the newest observed camera
  frame. It does not form a FIFO backlog.
- A newer update observed during AsyncGPUReadback survives completion of the
  older submission and is submitted on the next loop.
- The explicit single-in-flight gate now defaults OFF. Only genuinely fresh
  frames are submitted, while MediaPipe LIVE_STREAM discards busy inputs by its
  native policy.
- The gate remains available as `Single in-flight (lower load, more latency)`.
- Runtime diagnostics now expose render fps, source Hz, submit Hz, result Hz,
  readback latency, inference latency, and render age.
- Saved tracking settings moved to v9 so v4.7's enabled serial gate cannot be
  silently restored.

## Iterative review and diminishing returns

1. Raising prediction was rejected because it increases reversal error.
2. Removing all freshness checks was rejected because duplicate 1280x720
   readbacks can reduce render cadence.
3. Moving the in-flight check after freshness observation fixed gate-phase loss.
4. A one-slot mailbox was chosen over a queue to prevent old-frame latency.
5. Disabling the serial gate removed readback/inference serialization.
6. An `Update` observer fixed camera updates missed during coroutine yields.
7. A Unity-frame marker prevents double-counting the same camera update.
8. A generation number prevents readback completion from erasing a newer update.
9. Source timestamps remain latched before readback for full-age compensation.
10. Source, submit, and result rates were separated to make future evidence conclusive.
11. Lowering camera resolution was rejected because eye/mouth crop texture detail
    would regress and the new readback metric should be measured first.
12. Allowing an application-side multi-frame queue was rejected because it
    necessarily increases worst-case latency.
13. Multiple simultaneous readbacks were rejected because they raise GPU memory
    pressure and can reintroduce render flicker.
14. CPU synchronous readback was rejected for Windows/DX11 because it stalls the
    render thread.
15. Further prediction gain changes were rejected until a new v4.8 recording
    supplies source/submit/result rates; without those values, they are more likely
    to trade overshoot for an already-resolved scheduling bottleneck.

At this point, remaining unimplemented changes either add buffering, add render
work, or reduce input detail. Their expected benefit is lower than their latency,
flicker, or accuracy risk.

## Verification

- Unity 2022.3.62f2 batch compilation succeeded.
- `KiwiOptimizationValidator`: 18/18 passed.
- Tracking source/template SHA-256 pairs match.
- Safe installer recognizes v4.7 and targets v4.8 hashes.
- No duplicate filename suffix is introduced.
- Android/iOS remain source-compatible; iOS physical-device verification is out
  of scope as requested.

Live webcam behavior must be confirmed with a new recording. In that recording,
`source`, `submit`, and `results` should be compared directly: a large
source-to-submit gap identifies readback/render pressure; a large submit-to-result
gap identifies MediaPipe flow limiting or inference pressure.
