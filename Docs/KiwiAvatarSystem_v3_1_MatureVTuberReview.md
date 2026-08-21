# KiwiAvatarSystem v3.1 — Mature VTuber Review

Base GitHub commit: `6b4d2448e3446a3b026165fb87617cdd0253e532`

## Supplied video

- duration: ~40.04 s
- decoded recording cadence: ~25.90 fps
- resolution: 2560x1352

From the on-screen diagnostic panel around 34-38 s:

- Render: ~41.6 fps
- Camera source: ~41.3-41.5 Hz
- MediaPipe submit/result: ~7.0-7.2 Hz
- Tracking geometry quality: 1.00
- MediaPipe readback: ~115.7-119.6 ms
- source -> result: ~127.7-129.7 ms
- model estimate: ~10.1-11.5 ms
- Inference display: 0.0 ms / p=0.00

A simple brown-avatar center measurement across the recording found:

- median adjacent center step: ~0.14 px
- 95th percentile step: ~16.93 px
- 99th percentile step: ~41.36 px
- maximum step: ~70.83 px
- adjacent frames below 0.5 px: ~69.1%

That is a strong sample/hold signature: most render frames barely change and
then a much larger step arrives.

## Reference-system principles applied

### FaceRig / Animaze
Normal movement smoothing and high-intensity smoothing should not be identical.
Fast intentional motion gets the faster response.

### VTube Studio
Tracking cadence and render cadence are separate. Smooth presentation should
interpolate/retime tracking to the render loop rather than require tracker FPS
to equal render FPS. Side-view eye handling is a separate channel.

### VSeeFace / OpenSeeFace
Robust avatar tracking values stable landmarks, freshness and quality, not only
raw numerical image fit. OpenSeeFace also exposes explicit speed/quality
trade-offs and is designed to remain useful at 20-30+ fps.

### Luppet / LuppetX
A frontal neutral reference is treated explicitly. Kiwi already has frontal
calibration gates, so v3.1 preserves that model rather than learning neutral pose
during a turned head.

### Three D Pose Tracker
Its public filtering guidance explicitly notes that stronger low-pass filtering
reduces jitter at the cost of lag. v3.1 therefore does not stack another global
low-pass filter on top of Kiwi's existing predictor/presentation layer.

## v3.1 code changes

### 1. Real provider hysteresis
`KiwiTrackingProviderHub` now caches MediaPipe and Inference snapshots
separately for a short freshness window.

The v3.0 hub could not truly confirm a Runner backend switch because the
previous Runner backend disappeared as soon as the Runner published the new
backend.

v3.1 requires two independent candidate source frames for a normal switch.
A stale active provider still fails over immediately.

Provider score includes:
- configured priority,
- geometry quality,
- frame freshness,
- cadence regularity.

### 2. Completion-aware Inference health
`KiwiInferenceRecoveryBootstrap` no longer uses only accepted presence changes
as proof that the GPU tracker is alive.

It observes:
- ScheduledFrameCount
- CompletedFrameCount
- DroppedFreshFrameCount
- IsAsyncReadbackPending
- LatestPresence
- LatestLatencyMs

This distinguishes:
- tracker not scheduling,
- readback stalled,
- GPU work completing but every result rejected.

### 3. Safe adaptive presence gate
A marginal Inference presence stream may adapt below 0.30 only when the current
MediaPipe geometry quality is at least 0.72.

The adaptive floor is 0.18. Near-zero output never lowers the threshold.

### 4. High-intensity vs normal motion
`KiwiMatureVTuberSupervisor` measures visible avatar translation and rotation
speed.

Fast intentional movement gets a faster response. Rest/ordinary motion retains
more stabilization.

### 5. Low-rate protection
The supplied video has ~128 ms source-to-result age. v3.1 does not extrapolate
almost that full interval.

When the pipeline is degraded:
- prediction strength is capped around 0.58,
- prediction horizon is capped around 55 ms,
- sample reconciliation and causal smoothing handle continuity.

This trades a small amount of raw lead for much lower overshoot / snap risk.

### 6. Eye / mouth
- near-side eye remains visible longer,
- far-side eye still uses depth + fitted-surface guards,
- faster hide / slower restore hysteresis,
- final mouth visible size is capped at 0.66 x 0.60,
- eye/mouth render prediction is bounded more tightly.

### 7. Model switching
After a model switch:
- wait for three fresh tracking frames (max ~0.45 s),
- refit surfaces,
- retry failures,
- fade face parts back in.

## Review passes

The generated overlay was checked repeatedly for:
1. one temporal owner for body motion,
2. no duplicate tracker type,
3. no fragile source-rewriting Editor installer added,
4. no unbounded prediction,
5. no provider switch on repeated copies of one frame,
6. immediate stale-provider fallback,
7. actual GPU completion watchdog,
8. adaptive threshold safety gate,
9. eye/mouth channel separation,
10. model-switch fresh-frame gating,
11. brace / lexical structure,
12. accidental vendor-specific tracker references.

No additional material static-code change remained after the final pass without
requiring new real-device telemetry or changing MediaPipe/Unity package internals.

### 8. Raw tracking telemetry
A small F9-toggle overlay shows raw Inference schedule/accepted/drop counters,
raw presence, live threshold, ROI state and pending-readback state. This is
important because an accepted-result panel alone cannot distinguish a tracker
that never ran from one that ran but rejected every result.
