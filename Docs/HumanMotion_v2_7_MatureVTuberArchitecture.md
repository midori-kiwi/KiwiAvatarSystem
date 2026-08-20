# Human Motion v2.7 — Mature VTuber Architecture

This package is cumulative and includes every change from v2 through v2.6.

## Public systems reviewed

### FaceRig / Animaze
Public tracking documentation distinguishes normal Movement Smoothing from
High-Intensity Smoothing. Fast / large head movements should use less smoothing
than ordinary small motion so responsiveness is not sacrificed.

Kiwi already had moving/rest response separation. v2.7 keeps that and adds only
a very short new-sample reconciliation window instead of a permanent delay
buffer.

### Luppet / LuppetX
Luppet explicitly asks the user to look straight into the camera for a front
correction/calibration. The principle is important even when calibration is
automatic: do not learn neutral pose while the face is turned.

v2.7 therefore lengthens SharedTilt/RigidCenter neutral calibration and only
accumulates it while RenderedYaw is within ±12 degrees.

### VTube Studio
VTube Studio treats tracker FPS and application FPS separately and interpolates
tracking to the render rate. It also exposes per-parameter smoothing and can
link eye behavior when the face is rotated heavily to one side.

Kiwi keeps independent body, eye/mouth and visibility channels. Face-part
occlusion gets its own hysteresis rather than reusing body smoothing.

### VSeeFace / OpenSeeFace
VSeeFace interpolates even relatively low tracking rates and exposes
quality/performance choices. OpenSeeFace is optimized for avatar-useful,
stable landmarks rather than only exact image fitting.

v2.7 strengthens geometry-quality-aware prediction and smoothing instead of
treating every accepted landmark sample as equally trustworthy.

### Three D Pose Tracker (TDPT)
TDPT exposes filter adjustment and lightweight modes, reinforcing that one fixed
filter is not appropriate for every hardware/quality condition. Related Digital
Standard pose-analysis tools expose low-pass and Kalman-filtered data.

Kiwi does not add a global Kalman filter in v2.7: the existing timestamp-aware
velocity estimator plus critically damped presentation already has prediction
and would risk double-filter latency. Instead, its filtering budget adapts to
cadence and geometry quality.

## v2.7 implemented changes

1. **Sample-arrival reconciliation**
   - high-quality correction: ~8 ms
   - low-quality correction: up to ~18 ms
   - blends only the discontinuity when a new tracking sample arrives
   - no permanent 50-100 ms movement buffer

2. **Quality-aware prediction**
   - geometryQuality=0 can reduce prediction to 25%
   - low-quality tracking gets a small extra smoothing budget
   - high-quality fast motion remains low-latency

3. **Inference backend hysteresis**
   - once Inference is primary, two isolated publish rejects are tolerated
   - the third reject returns ownership to MediaPipe
   - genuine tracker-loss still uses the existing immediate fallback

4. **Stale-primary watchdog**
   - an Inference-primary snapshot older than ~350 ms is treated as unhealthy
   - ownership is released and the tracker is recovered
   - prevents a frozen Inference primary from suppressing MediaPipe fallback

5. **Eye/mouth visibility hysteresis**
   - hide quickly
   - show more slowly
   - prevents far-side surface flicker around silhouette thresholds

6. **Neutral calibration gate**
   - SharedTilt delay ~0.60 s / 15 samples
   - RigidCenter delay ~0.65 s / 15 samples
   - only while |RenderedYaw| <= 12 degrees

7. **Model-switch tracking warmup**
   - after transactional swap, wait for two fresh atomic frameIds
   - then SurfaceFitter refit / calibration / fade-in
   - fallback timeout ~350 ms prevents a permanent hidden state

## Why no global Kalman filter was added

A Kalman/low-pass stage is useful for raw pose systems such as TDPT, but Kiwi
already has:
- timestamp-aware sample velocity,
- bounded prediction,
- cadence-adaptive smoothing,
- render-time presentation,
- outlier confirmation.

Putting a global Kalman filter in front of this would duplicate prediction and
can introduce overshoot or extra latency. The v2.7 approach is closer to the
channel-specific filtering behavior documented by FaceRig/Animaze, VTS and
VSeeFace.
