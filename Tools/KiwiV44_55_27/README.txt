KiwiAvatarSystem v44.55.27 Actual Production vs Pair-Bound Shadow Payload Authority

Purpose:
Compare the logical 1405 floats in Production's existing CPU-readable tensor
immediately before DecodeReadableOutput with the same pair's already captured,
observer-owned v44.55.24 REF_FROZEN_GPU and MODE2 B_GPU arrays.

Design:
- Development/correctness diagnostic only; performance authority is zero.
- KiwiInferenceFaceTracker.cs is unchanged.
- v27 reuses the v26 readableOutput seam; v26 is explicitly disabled at run time.
- v24 hands off its existing REF/MODE2 CPU arrays at pair finalization.
- The rejected v24 Production snapshot is used only for array-alias validation,
  never as a comparison side.
- No ReadbackAndClone, ReadbackRequest, AsyncGPUReadback, Worker, Schedule,
  GPU copy, lane.input oracle, blocking wait, Production buffer, smoothing,
  ROI, camera, Native, backend-selection, or tracking-authority change.
- Candidate payloads are bounded to 192 exact v25 transactions and released
  immediately after A-vs-B, A-vs-C, and B-vs-C comparison.
- Primary eligibility does not require canonical publication. The v25
  TRANSACTION_COHERENT published subset is reported separately.

Commands (Windows PowerShell 5.1):
  .\Tools\KiwiV44_55_27\Validate-KiwiV44_55_27.ps1 -StaticOnly
  .\Tools\KiwiV44_55_27\Build-KiwiV44_55_27.ps1
  .\Tools\KiwiV44_55_27\Run-KiwiV44_55_27.ps1
  .\Tools\KiwiV44_55_27\Validate-KiwiV44_55_27.ps1 -EvidenceStamp <yyyyMMdd_HHmmss>

Validator PASS certifies the observer/evidence contract. It does not convert a
payload-authority result into Production Semantic PASS or authorize a fix.
