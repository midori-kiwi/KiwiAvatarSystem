KiwiAvatarSystem v44.55.25 Production Schedule Transaction Trace

Purpose
-------
Prove whether each v44.55.24 Production packed-output snapshot belongs to the
same Production Worker schedule transaction as the exact v44.55.20 sample/pair.
This is observer-only correctness evidence. performanceAuthority=0.

Authority composition
---------------------
Boundary A (pair attach): v44.55.20 exact pair identity is matched to exactly
one Production lane. Lane, Worker, input Tensor, pendingOutput Tensor, schedule
timestamps/generations/policy, and all 16 crop-matrix float bits are frozen.

Boundary B (Production output ready): the v44.55.25 early probe runs at -32000,
after the v44.55.24 early probe at -33000 and before ordinary Production Update.
Every frozen reference and metadata value is checked by ReferenceEquals or exact
bit/tick/integer equality. IsReadbackRequestDone is queried; content is not read.

Boundary C (v44.55.24 snapshot/record bind): v44.55.24 existing capture state is
composed directly. No new GPU snapshot is submitted. Its final record identity is
then bound by record index, sequence, pair token, and snapshot sequence.

Boundary D (canonical publication): once per newly published precision frame, the
existing atomic FaceLandmarkerRunner snapshot is read. MediaPipe publications are
ignored because both backends may publish the same source tick. InferenceEngine,
frameId, publication timestamp, arrivalHostTicks, and exact submissionHostTicks are
bound to the pair. No landmark array or tensor content is copied.

Environment contract
--------------------
KIWI_V44_55_20_COMMON_TENSOR_AUDIT=1
KIWI_V44_55_24_PAIR_BOUND_SHADOW_OUTPUT_AUDIT=1
KIWI_V44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE=1
v44.55.21, v44.55.22, and v44.55.23 observers are OFF.

Build (Windows PowerShell 5.1)
--------------------------------
Set-ExecutionPolicy -Scope Process Bypass
& "D:\KiwiAvatarSystem\Tools\KiwiV44_55_25\Build-KiwiV44_55_25.ps1" `
    -ProjectRoot "D:\KiwiAvatarSystem"

Runtime
-------
& "D:\KiwiAvatarSystem\Tools\KiwiV44_55_25\Run-KiwiV44_55_25.ps1" `
    -ProjectRoot "D:\KiwiAvatarSystem"

Runtime validation
------------------
& "D:\KiwiAvatarSystem\Tools\KiwiV44_55_25\Validate-KiwiV44_55_25.ps1" `
    -ProjectRoot "D:\KiwiAvatarSystem" `
    -EvidenceStamp "yyyyMMdd_HHmmss"

Pre-registered coherent gate
----------------------------
dependencyStatus=COMPLETE
completedTraceCount >= 60
dependencyPairCount == completedTraceCount
anomalousPairCount >= 3
observerFaultCount == 0
scheduleAttachMissCount == 0
ambiguousLaneMatchCount == 0
lane/Worker/input/pendingOutput identity mismatch counters == 0
all timestamp/generation/policy/crop-matrix mismatch counters == 0
v24PairIdentityMismatchCount == 0
outputReadyObservationMissCount == 0
snapshotBindingMissCount == 0
canonicalPublicationDuplicateCount == 0
duplicateTraceCount == 0
partialTraceCount == 0
tracesPending == 0

Decision rule
-------------
TRANSACTION_COHERENT: every valid pair closes pair + Production schedule +
pendingOutput + v44.55.24 snapshot + canonical publication identity exactly.

SCHEDULE_TRANSACTION_MISMATCH: at least one valid anomalous pair contains a
Production schedule/output transaction identity mismatch.

PUBLISHED_TRANSACTION_COHERENT_PARTIAL_COVERAGE: every observed InferenceEngine
publication binds exactly, with at least three anomalous published pairs, while
some v24 packed outputs were not observed as canonical publishes.

INSUFFICIENT_PUBLISHED_ANOMALOUS_DATA: fewer than three anomalous v24 pairs were
observed as InferenceEngine canonical publications.

CANONICAL_PUBLICATION_IDENTITY_INVALID: a matching InferenceEngine publication
has invalid matched-timing identity. Absence alone is coverage, not a defect,
because Production may correctly reject or supersede a packed output.

OBSERVER_INVALID: observer miss, ambiguous match, lifecycle, execution order,
dependency, or validator failure. Do not infer a Production defect.

Explicitly absent
-----------------
No Production source write. No lane.input readback/copy. No texture readback.
No repeated inference. No added Worker or schedule call. No blocking wait. No
new ComputeBuffer/GPU snapshot. The existing no-landmark-copy precision snapshot
accessor is sampled once per new frameId. No Native/Camera/ROI/tracking/decode change.
