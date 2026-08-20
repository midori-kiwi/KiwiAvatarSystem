# Human Motion v2.1

## Horizontal direction
The runtime preset now enables `avatarCentricHorizontalMovement`.
Tracked right movement therefore becomes the Kiwi's own right.

## Tilt drift
The current project already locks each counter-rotation pivot to the rendered
crop, but each FacePartAngleLock still derives its angle independently from
one eye or from the mouth.

v2.1 separates rigid head roll from local expression:
- `KiwiFacePartSharedTiltLock` derives one roll signal from the rendered
  left-eye/right-eye baseline and applies the same correction to both eyes and
  mouth around each rendered crop center.
- `KiwiFacePartRigidCenterLock` learns a neutral mouth position in the
  eye-defined face frame and applies only a small bounded correction while
  roll is large.

Execution order:
- FacePartCropper: 700
- KiwiFacePartRigidCenterLock: 825
- FacePartShapeMask: 850
- FacePartAngleLock: 875
- KiwiFacePartSharedTiltLock: 900

This keeps the corrected crop center visible to the mask and makes the shared
roll correction the final shader sampling transform.
