KiwiAvatarSystem v44.55.24 Pair-Bound Shadow Output Snapshot

Purpose:
- preserve the valid v44.55.23 capacity-aware Production output snapshot;
- separate real Production/shadow output differences from a post-record Worker
  output reuse artifact.

Pair-bound authority:
1. Production completed pendingOutput is copied at its completion boundary.
2. While the v44.55.20 pair has _pairPending=true and
   _commonScheduled=true, REF_FROZEN_GPU and MODE2 B_GPU packed outputs are
   copied to observer-owned ComputeBuffers.
3. The snapshot is keyed by pair token, record index, sequence, native host
   ticks, managed host ticks, and lane-start host ticks.
4. Record completion only binds and validates the already captured snapshots.
   It never obtains a shadow Worker output after record completion.
5. Comparison remains bitwise across exactly 1405 logical float values.

Build:
Set-ExecutionPolicy -Scope Process Bypass
& "D:\KiwiAvatarSystem\Tools\KiwiV44_55_24\Build-KiwiV44_55_24.ps1" -ProjectRoot "D:\KiwiAvatarSystem"

Run after BUILD PASS:
& "D:\KiwiAvatarSystem\Tools\KiwiV44_55_24\Run-KiwiV44_55_24.ps1" -ProjectRoot "D:\KiwiAvatarSystem"

Required environment:
- v44.55.20 ON
- v44.55.21 OFF
- v44.55.22 OFF
- v44.55.23 OFF
- v44.55.24 ON

Runtime gate:
- dependencyStatus=COMPLETE
- completedPairCount >= 60
- completedPairCount == dependencyRecordCount
- anomalousPairCount >= 3
- observerFaultCount=0
- attachMissCount=0
- recordCaptureWindowMissCount=0
- shadowOutputCaptureMissCount=0
- shadowIdentityMismatchCount=0
- duplicateShadowSnapshotCount=0
- production/reference/mode2OutputSnapshotFailureCount=0
- nonFiniteOutputCount=0
- inputArrayIdentityMismatchCount=0
- outputShapeMismatchCount=0
- capturesPending=0

No Production / Native / Tracking / ROI / threshold change.
No Production lane.input readback. No blocking wait. No added Worker.
Performance authority=0.
