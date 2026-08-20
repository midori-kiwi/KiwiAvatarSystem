# KiwiAvatarSystem Human Motion v2.3 — Async Mailbox + Tracking Hardening

This package is cumulative. It includes the earlier Human Motion v2, v2.1 and
v2.2 fixes, then adds the v2.3 changes below.

## Video analyzed

File:
KiwiAvatarSystem - Face Landmark Detection - Windows, Mac, Linux -
Unity 6.0 (6000.0.80f1) _DX11_ 2026-08-20 20-27-09.mp4

Decoded recording:
- duration: ~39.18 s
- decoded recording cadence: ~26.11 fps
- resolution: 2560x1352

## Around 12 seconds — tilt / face-part drift

The recording shows the eye/mouth source regions losing stable attachment while
the Kiwi is rolled. The prior v2.1 helper had two design risks:

1. it calculated its rigid correction from sample-domain rectangles but applied
   that correction to the render-domain mouth uvRect,
2. if FacePartCropper did not rewrite the uvRect on a render frame, the helper
   could feed its previous correction back into the next correction.

v2.3 fixes this by:
- using the rendered left-eye/right-eye/mouth uvRects in one coordinate phase,
- recovering the uncorrected mouth center when the current uvRect is still the
  helper's own previous write,
- lowering the rigid mouth correction from 0.012 UV / 0.78 strength to a
  conservative 0.006 UV / 0.45 strength,
- leaving the shared render-phase tilt correction as the primary rigid-roll
  correction,
- preserving blink / mouth deformation instead of locking expressions rigidly.

## Around 24 seconds — abnormal following / large correction

A simple rendered-avatar foreground-center measurement in the 23-25.2 s window
shows a mostly low-motion trajectory interrupted by very large correction
steps. The approximate median center speed is ~9 px/s, while the 95th
percentile is ~1377 px/s and the largest detected step is ~3511 px/s.

The important code issue was in Human Motion v2.2:
a "large discontinuity" was treated as a reason to InitializeSampleState(),
which immediately adopted and snapped to that pose. A single bad tracking frame
could therefore become a visible teleport / scale jump.

v2.3 changes the rule:
- one large same-backend jump is held instead of applied,
- a second spatially consistent tracking frame is required before adoption,
- confirmed large movement is rebased without snapping the rendered pose,
- backend changes reset velocity but do not teleport the presentation layer,
- genuine long-gap reacquisition is still allowed to establish a new origin.

The candidate frame ID is marked as observed while it is held, so LateUpdate and
onBeforeRender cannot count the same tracking result twice.

## Timestamp-preserving async GPU readback mailbox

The synchronous Inference Engine path previously called ReadbackAndClone()
directly after Worker.Schedule(). On GPUCompute this waits for inference and
GPU->CPU transfer on the main thread.

v2.3 changes KiwiInferenceFaceTracker to:
- schedule the model,
- call Tensor.ReadbackRequest(),
- keep one output readback in flight,
- poll Tensor.IsReadbackRequestDone() from subsequent Updates,
- call ReadbackAndClone() only after the request is complete,
- keep only the newest unscheduled webcam generation as a one-slot mailbox.

For each scheduled inference, the tracker stores:
- the exact crop matrix,
- the source-frame host timestamp,
- the anchor revision active when that frame was scheduled.

When the result completes:
- landmarks are mapped with that exact historical crop matrix,
- the historical source timestamp is returned to FaceLandmarkerRunner,
- a newer MediaPipe anchor cannot be overwritten by an older async inference
  result because anchor revisions are compared before ROI update.

## Runner migration

The existing ~95 KB FaceLandmarkerRunner is deliberately not replaced wholesale.

KiwiAsyncInferenceMailboxInstaller.cs performs a narrow, idempotent local
migration of the Inference Engine call site:
- async completion is polled every render Update,
- fresh camera generations are marked consumed only when actually scheduled,
- a dedicated Inference source-frame host timestamp is retained,
- StoreSentisTrackingData receives the timestamp of the frame that actually
  produced the completed inference result,
- an in-flight async readback is not misclassified as tracking loss.

This preserves the accumulated MediaPipe / lifecycle / atomic-publish fixes.

## Expected behavior after v2.3

- lower main-thread stalls from Inference GPU readback,
- more stable render cadence,
- correct camera-frame timing for prediction / matched-age compensation,
- no old ROI result dragging a newer MediaPipe anchor backward,
- no one-frame body teleport from a large tracking outlier,
- no cumulative mouth uvRect drift during tilt,
- all previous avatar-own-right, shared tilt, Eye/Mouth, and hot-swap fixes remain.
