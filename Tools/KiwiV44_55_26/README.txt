KiwiAvatarSystem v44.55.26 Production Decode Payload Transaction Closure

Purpose:
Compare the v44.55.24 exact pair-bound Production packed snapshot with the
logical 1405 floats in Production's existing CPU-readable tensor immediately
before DecodeReadableOutput, reusing the complete v44.55.25 transaction contract.

Design:
- Development/correctness diagnostic only; performance authority is zero.
- KiwiInferenceFaceTracker.cs is unchanged.
- One callback is inserted into the existing readable-output diagnostic seam.
- One callback hands off the already observer-owned v44.55.24 snapshot.
- No ReadbackAndClone, ReadbackRequest, Worker, Schedule, GPU copy, lane.input
  oracle, blocking wait, Production buffer, smoothing, ROI, camera or Native change.
- Candidate payloads are bounded to 192 exact v25 pairs and released after compare.

Commands (Windows PowerShell 5.1):
  .\Tools\KiwiV44_55_26\Validate-KiwiV44_55_26.ps1 -StaticOnly
  .\Tools\KiwiV44_55_26\Build-KiwiV44_55_26.ps1
  .\Tools\KiwiV44_55_26\Run-KiwiV44_55_26.ps1
  .\Tools\KiwiV44_55_26\Validate-KiwiV44_55_26.ps1 -EvidenceStamp <yyyyMMdd_HHmmss>

Interpret the exact decision and counters; validator PASS certifies the evidence
contract and never converts PARTIAL/INSUFFICIENT/INVALID into Production PASS.
