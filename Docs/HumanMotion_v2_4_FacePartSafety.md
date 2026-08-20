# KiwiAvatarSystem Human Motion v2.4 — Face Part Safety + Swap Refit

This package is cumulative and contains the earlier v2, v2.1, v2.2, v2.3 and
v2.3.1 changes.

## Supplied video analysis

File:
`KiwiAvatarSystem - Face Landmark Detection - Windows, Mac, Linux -
Unity 6.0 (6000.0.80f1) _DX11_ 2026-08-20 21-03-43.mp4`

Decoded:
- duration: ~20.82 s
- cadence: ~25.70 fps
- resolution: 2560x1352

During active motion, about 62% of adjacent decoded frames moved less than
0.5 px at the rendered avatar center. This still indicates a lower accepted
tracking cadence than render cadence.

Around 5.3-6.3 s, the physically far eye can be seen as a separate dark oval
outside the Kiwi silhouette during a large side turn.

## v2.4 changes

### Tracking
- preserves v2.3 timestamp-aware Async GPU mailbox,
- MediaPipe tracking input is reduced from 384 to 320 px,
- prediction cap is increased modestly to 110 ms for low-result-rate fallback,
- velocity decays faster after stale data,
- cadence-adaptive smoothing budget increases from 16 to 20 ms,
- v2.3 large-jump confirmation remains active.

### Eye / mouth crop and mask
Eye:
- crop width 1.60,
- height/width 0.64,
- padding 0.016 / 0.014,
- contour margin 0.14.

Mouth:
- crop width 1.50,
- height/width 0.66,
- padding 0.016 / 0.018,
- outer-contour safety 0.18 / 0.22,
- contour margin 0.055.

Mask feather is 0.035 and crop-local safety margin is 0.008. This creates room
for the actual webcam eye/lip pixels before the mask boundary is reached.

### Side-view silhouette guard
The previous mask used one absolute-yaw visibility curve for the whole face.
v2.4 resolves which surface-fitted eye is physically farther from the camera:
- far eye fades from 28 to 44 degrees,
- near eye remains until 58-72 degrees,
- mouth fades from 52 to 68 degrees,
- surface offset is reduced to 0.001.

This prevents the far eye from floating outside the Kiwi body during large yaw.

### Model switching
After a transactional avatar swap finishes:
1. face parts remain hidden,
2. wait two stable frames,
3. run KiwiSurfaceFitter on the new renderer,
4. retry failed fitting up to two times,
5. recalibrate SharedTilt and RigidCenter,
6. reset legacy FacePartAngleLock calibration,
7. fade face parts in over ~90 ms.

This avoids showing old-model surface coordinates on the new avatar.
