# KiwiAvatarSystem v3.0 — Provider Foundation

v3.0 includes every Human Motion v2.x fix through v2.7.

## Foundation decision

The reference systems show that a mature VTuber app should not hard-code one
tracker as the only possible source.

- VTube Studio supports multiple face tracking backends.
- OpenSeeFace is robust and avatar-oriented but has different strengths from
  MediaPipe, especially around eyes.
- LuppetX supports webcam, iFacialMocap and external-device modes.
- TDPT is primarily a 24-point 3D body provider.
- FaceRig/Animaze separates normal and high-intensity smoothing.

## New architecture

Providers
  Runner/Inference Engine
  Runner/MediaPipe
  future OpenSeeFace
  future iFacialMocap / ARKit
  future TDPT body/head
  future NVIDIA-backed provider
        |
KiwiTrackingProviderHub
  priority
  freshness
  geometry quality
  two-frame switch confirmation
  stale-provider failover
  monotonic hub frameId
        |
Human Motion v2.7
        |
Avatar Runtime / Eye-Mouth presentation

Dense Eye/Mouth camera extraction remains on the current MediaPipe topology.
A future body/head provider therefore does not have to replace the existing
478-point eye/mouth path.
