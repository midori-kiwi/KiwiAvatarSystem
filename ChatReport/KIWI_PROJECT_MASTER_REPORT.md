# KIWI_PROJECT_MASTER_REPORT

Updated: 2026-09-03 JST  
Project: KiwiAvatarSystem  
Project Root: `D:\KiwiAvatarSystem`  
Report Type: PROJECT-WIDE MASTER / CURRENT AUTHORITY INDEX  
Source Scope: 24 ChatReport ZIP archives + available KiwiAvatarSystem project-chat context + current integrated Authority/custom-instruction files  
Local Production Verification: `LOCAL_PRODUCTION_NOT_DIRECTLY_VERIFIED`  
Master Status: `CURRENT_BEST_EFFORT / AUTHORITY-RECONCILED`

---

## 0. PURPOSE

This MASTER REPORT is the single entry point for the current KiwiAvatarSystem project state.

It does **not** replace each Chat Permanent Report.  
Each ChatReport preserves detailed historical context; this MASTER keeps:

- current Production-oriented design principles,
- chronological archive index,
- validated decisions,
- rejected approaches,
- superseded/obsolete conclusions,
- Observer/Validator findings,
- current known SHA/provenance,
- open issues,
- current next action,
- archive coverage gaps,
- handoff guidance.

When this MASTER conflicts with an older ChatReport, use the Authority order below and preserve the older result under `SUPERSEDED / HISTORICAL`.

---

## 1. AUTHORITY ORDER

Use the following order:

1. Current local Production actual placement + SHA256.
2. Latest Runtime Evidence from that exact Production.
3. Actual Package Source used by that Production.
4. Latest KiwiValidation / Codex Consolidated REPORT grounded in that Production.
5. Official Documentation / official Source.
6. Local Git history.
7. GitHub main.
8. Older versions / diagnostics / historical ChatReports.
9. Issues / Discussions.
10. Technical articles.
11. Reddit / X / YouTube / user reports.

Rules:

- GitHub main must not override newer local Production.
- A newer version number is not automatically more authoritative.
- Correctness Observer results are not Performance Authority.
- Observer/Validator implementations are themselves audit targets.
- If timestamp/source identity or transaction ownership is not proven, do not promote proxy metrics to Production truth.
- Never invent missing SHA/time/version details.

---

## 2. CURRENT PROJECT STATE

### Environment

- Project: `D:\KiwiAvatarSystem`
- Unity: `6000.0.80f1`
- Platform: Windows / DX12
- CPU: Ryzen 9 5950X
- GPU: RTX 4090
- Camera: UGREEN Camera 4K
- Camera target: 1920x1080@60 / Native NV12
- MediaPipeUnityPlugin: v0.16.3
- Unity Inference Engine: 2.4.1
- Active Scene: `Face Landmark Detection`

### Current global state

- Tracking Core: **stable baseline; do not redesign without new evidence**
- Face Landmarker: **Primary / Source of Truth**
- Native Camera: **Path B SystemMemoryNV12 stable**
- LIVE CAMERA: **CLOSED / PRODUCTION ACCEPTED**
- Spout / Presentation: **base architecture established**
- Production inference authority: **GPU remains authority**
- Same-tensor CPU/GPU backend math: **not the main discrepancy source**
- Primary unresolved domain: **Preprocessing / Output Transaction Semantic**
- Latest confirmed semantic observer state: **v44.55.23 Observer Validity PASS / 115 of 115**
- Latest v44.55.23 domain label: **MIXED_TRANSACTION is provisional, not final**
- Current project-wide next action: **v44.55.24 Pair-Bound Shadow Output Snapshot**

### Cross-chat priority reconciliation

Some 2026-09-03 historical/branch reports retain their own unresolved next actions:

- Phase16.17 Provider Normalization Ownership Audit.
- PureProject Roll-safe Root translation / `Assets(2).zip` lineage audit.

These remain valid **branch-local open issues**, but they do not automatically override the current v44.55.x mainline priority.

Project-wide priority remains:

`v44.55.24 → Production Output Transaction Authority → Reference Transaction Authority → preprocessing semantic closure`

before new Production CPU conversion, Native redesign, or Tracking-Core changes.

---

## 3. CORE DESIGN PRINCIPLES — CURRENT

### Tracking

- Face Landmarker remains Primary / Source of Truth.
- latest-frame priority.
- FIFO / stale queue prohibited.
- persistent ROI.
- short dropout must not immediately become Lost.
- source liveness and source age are separate concepts.
- accepted Landmarker samples should remain near-raw/responsive.
- no threshold changes without Runtime evidence.
- no strong global smoothing / Kalman to hide upstream defects.
- do not continue predicting stale samples.
- tracking cadence and display cadence are separate.
- `KiwiFaceMotion.cs` / `KiwiInferenceFaceTracker.cs` must not be changed as camera/display-cadence workarounds.

### Authority / presentation

- Head = single rigid authority.
- Root / Head / Eye / Mouth are separate channels.
- Yaw/Pitch/Roll do not translate Root.
- Eye/Blink/Mouth do not drive Root translation.
- Eye/Mouth Texture + Crop + Mask = same-sample transaction.
- Provider normalization should exist at one layer.
- Same-provider Resume != Provider Switch.
- Temporal Root Presentation owner = one layer.
- Prediction owner = one layer.
- permanent one-frame buffer prohibited.
- duplicate / out-of-order / stale generation / mixed backend epoch publish prohibited.

### Native Camera

Current baseline: **Path B SystemMemoryNV12**

Preserve:

- latest-frame,
- fixed Unity-visible Presentation Texture identity,
- timestamp / generation,
- camera-session epoch separate from frame sequence,
- bounded lanes,
- non-blocking operation.

Prohibited without new evidence:

- FIFO,
- stale accumulation,
- `UpdateExternalTexture`,
- callback/worker-crossing `IMFSample` retention,
- `g_unityD3D12Queue->Wait(...)`,
- blocking GPU waits,
- rotating Unity-visible texture identity,
- D3D11On12 1080p Production,
- tracking smoothing/math as a camera workaround.

### Avatar

Current architecture direction:

`Tracking Core fixed + Avatar Adapter + Model Profile + Runtime Import + Transactional Hot Swap`

Model-specific handling must not flow backward into Tracking Core.

---

## 4. CHRONOLOGICAL PROJECT / CHAT ARCHIVE TIMELINE

Chronology rule:

- `LastWorkUpdate` is used, not archive-creation time.
- `DATE_ONLY` entries have no invented clock time.
- Multiple `DATE_ONLY` reports on the same date are not claimed to have exact intra-day ordering.
- Where technical dependency is obvious, the dependency is described, but no false timestamp is created.

### 2026-08-21 23:41 JST — Natural Motion / FacePart / v5.0.1

Archive: `KiwiChat_NaturalMotionTrackingFacePartMask_v5_0_1_Updated_20260821-2341-JST.zip`

Key retained findings:

- Same Provider Resume != Provider Switch.
- Source Age != Liveness.
- Head is a single rigid authority.
- Eye/Mouth → Root feedback rejected.
- semantic face-part transaction required.
- Head-local FacePart sampling required.
- v5.0 Root pinning / mask regressions were fixed in v5.0.1.

Current status: **historical / superseded by later project line**, but design lessons remain valid.

### 2026-08-23 — Commercial tracking / presentation phases

Archive: `KiwiChat_CommercialTrackingLandmarkPresentation_Updated_20260823-JST.zip`

Retained:

- continuity implementation must be proven active before threshold tuning.
- old 96-retry migration loop rejected.
- isolated Landmark rejection must not be treated as whole-frame semantic failure.
- provider-local handling preferred over broad global smoothing.
- Writer/authority auditing became mandatory.

Archive: `KiwiChat_TrackingObserver_FineCam1080p60_Updated_20260823-JST.zip`

Retained:

- LIVE CAMERA and matched historical diagnostic must be separated.
- strict FacePart runtime contract should be repaired, not weakened.
- DX12 cannot be promoted solely from one inference-latency metric.
- camera profile assumptions were later superseded by Native Path B.

### 2026-08-23 20:58 JST — Phase16.19 FacePart / Landmark debug repair

Archive: `KiwiChat_Phase16_19_FacePartLandmarkDebug_Updated_20260823-2058-JST.zip`

Validated:

- Supervisor had reopened strict FacePart temporal options; repaired.
- live-camera pixels must not be compared against delayed semantic landmarks as if same-frame.
- canonical annotation double-mirror was an Observer defect.
- LandMarker X/Y scale/aspect/rotation correction was not justified.
- fix Observer/Validator before changing Production tracking math.

Historical open item at chat end: fast-motion matched-debug freeze / cadence issue.  
Current project authority has moved beyond this exact diagnostic state.

### 2026-08-24 — Native Camera compatibility and v12-v18 isolation

Archive: `KiwiChat_NativeCameraCompatibilityReverseShare_Updated_20260824-JST.zip`

Historical result:

- Compatibility Reverse Share could function.
- forward D3D11-owned shared resource path rejected.
- reverse-share diagnostic was useful but not the final Production camera architecture.

Current status: **SUPERSEDED by Path B SystemMemoryNV12**.

Archive: `KiwiChat_NativeCameraPipeline_v12_v18_Updated_20260824-JST.zip`

Validated durable results:

- standalone MF native NV12 1080p60 proved camera/driver/MF capability.
- D3D11 Shared Bridge ~60fps proved simple CopyResource/shared ring was not the 15fps root cause.
- D3D11On12 1080p rejected.
- stable Presentation Texture identity established.
- `cameraGeneration` = camera session/texture identity epoch, not frame sequence.
- QPC→Managed timestamp-domain calibration established.
- IMFSample retention removed.
- v18 eliminated major wait/drop hypotheses while source remained slow.

Historical next proposal was v19 A/B capture transport isolation.  
Later project work established Path B; therefore “Native currently 13–20Hz” is obsolete.

### 2026-08-26 13:43 JST — Standalone DX12 Inference validation v37.2

Archive: `KiwiChat_StandaloneDX12InferenceValidation_v37_2_Updated_20260826-1343-JST.zip`

Validated:

- v35.3 Standalone performance evidence was invalid because required GPUCompute kernels were missing.
- broad “shader stripping” diagnosis was too coarse.
- serialization label alone did not prove DX12 payload absence.
- v37.2 produced a valid Standalone DX12 GPUCompute path.
- packaging/observer validity must be established before performance comparison.

Current status: historical / superseded by later v44.x inference work.

### 2026-08-27 19:47 JST — ORT DirectML Zero-Copy v42.3

Archive: `KiwiChat_OrtDmlZeroCopy_v42_3_Updated_20260827-1947-JST.zip`

Validated:

- Native rendering plugin graphics-device lifecycle was a real root issue.
- ORT DirectML D3D12 Zero-Copy feasibility established.
- preload-only device-readiness assumption rejected.
- one telemetry column (`ortMinusSentisObservedMs`) was invalid and must not be used as authority.

Current status: historical feasibility evidence, not current Production backend authority.

### 2026-08-29 02:21 JST — Surface Fit / DX12 validation isolation

Archive: `KiwiChat_SurfaceFitV44_17_DX12ValidationIsolation_Updated_20260829-0221-JST.zip`

Validated:

- disabling PhysX Fast Midphase removed warning but caused ~3s synchronous cooking and inference recovery.
- partitioned exact topology reduced collider cook to ~0.5s while preserving intended geometry.
- Spout/external-resource errors were isolated separately from camera/tracking.
- direct D3D11On12 assumptions were not promoted into Camera Production.

Current status: Surface Fit result retained; broader DX12 issues superseded by later isolation.

### 2026-08-30 — Native Camera Color / Production Mode 9 / CPUAsync

Archive: `KiwiChat_NativeCameraColorAndLandmarkerReadMode_Updated_20260830-JST.zip`

Production-accepted results:

- raw camera metadata showed BT.601 + Full nominal range while old Native conversion assumed BT.709 Limited.
- metadata-driven BT.601/BT.709 × Full/Limited conversion adopted.
- color defect closed by semantic YUV correction, not display gamma workaround.
- Production mode 9 source formalized and Runtime accepted.
- steady-state Image Read Mode confirmed CPUAsync; startup CPU log did not imply steady-state CPU-only read mode.
- Windows MediaPipe Delegate=CPU retained.
- v44.44 diagnostic observer removed after proof.

Do not reopen Native color or ImageReadMode without independent regression evidence.

### 2026-08-31 — CPU backend / preprocessing semantic audit

Archive: `KiwiChat_CPUBackendSemanticAudit_Updated_20260831-JST.zip`  
Archive: `KiwiChat_StaticFirstCpuSemanticAB_Updated_20260831-JST.zip`  
Archive: `KiwiChat_CPUBackendParity_StaticFirst_Updated_20260831_1906-JST.zip`

These reports overlap heavily and preserve the same transition from backend-performance interest to static-first semantic validation.

Validated chain:

- v44.54 same exact host-float tensor:
  - CPU median ~11.81ms
  - GPU median ~40.53ms
  - raw model output effectively equivalent.
- backend math itself therefore not the principal correctness discrepancy when inputs are identical.
- v44.55.10: Presentation frame-content race confirmed; metadata target S could contain S+1/S+2 pixels.
- v44.55.11: identity correction succeeded; raw semantic proxy near-pass but predeclared strong gate failed.
- v44.55.12: D3D fixed-point subtexel 16.8/16.9/16.10/16.12 did not improve residual; FULL_FLOAT best; fixed-point hypothesis rejected.
- v44.55.13 FIX2:
  - source guard 29/29 PASS,
  - Unity actual compile PASS,
  - Editor static audit PASS,
  - critical hash recheck PASS.
- static-first principle adopted: maximize static proof and minimize Runtime **run count**, not necessarily run duration.

CPU Production conversion remained prohibited.

### 2026-09-02 00:26:43 JST — Native Provenance + PowerShell foundation

Archive: `KiwiChat_NativeProvenanceAndPowerShellFoundation_Updated_20260902-0026-JST.zip`

Validated:

- recovered original v44.55.12 package strongly supports source→package→Production lineage.
- Native provenance classified as Class B / Strongly Supported.
- guessed Native rebuild definition rejected.
- exact DLL ABI parity alone is not sufficient provenance.
- common Windows PowerShell 5.1 foundation adopted.
- per-version PowerShell compatibility hotfixing rejected.

PowerShell rules retained:

- Windows PowerShell 5.1.
- shared SHA/safe-I/O/subprocess/write-target/rollback helpers.
- Generic List: `::new()` + `.ToArray()`.
- detect PS7-only syntax, cardinality ambiguity, obsolete paths, unsafe Copy/Move/Remove destinations.

### 2026-09-02 — CPU inference semantic authority re-audit

Archive: `KiwiChat_CPUInferenceSemanticAuthorityReview_Updated_20260902-JST.zip`  
Archive: `KiwiChat_CPUInferenceSemanticAuthority_Updated_20260902-JST.zip`

Validated later authority chain:

- v44.55.15 proxy comparison close but not enough for Production CPU.
- v44.55.17 canonical semantic FAIL.
- v44.55.19 Production GPU triangulation rejected both CURRENT_FLOAT and mode2 as CPU Production authority.
- v44.55.20 common-tensor backend isolation:
  - REF GPU/CPU PASS
  - A GPU/CPU PASS
  - B GPU/CPU PASS
  - discrepancy domain = preprocessing, not backend.
- v44.55.21 border/clamp hypothesis rejected.
- anomaly was intermittent: >=4 LSB concentrated in 8 pairs rather than all pairs.
- v44.55.22 Production `lane.input` direct/queue-external oracle rejected because the observer itself can create false mismatch.
- v44.55.23 Production completed-output observer:
  - 115/115 complete,
  - observer faults 0,
  - snapshot failures 0,
  - anomalous 8,
  - Production=REF 5,
  - Production=MODE2 0,
  - Neither exact 3,
  - Observer Validity PASS.
- `MIXED_TRANSACTION` remains provisional because Worker output reuse/lifecycle race was not excluded.

Current next: **v44.55.24 Pair-Bound Shadow Output Snapshot**.

### 2026-09-02 — Commercial FacePart / Avatar architecture audit

Archive: `KiwiChat_CommercialFacePartArchitectureAndCodeAudit_Updated_20260902-JST.zip`

Retained:

- 3D Model Primary + constrained 2D FacePart concept.
- model-specific behavior belongs in Avatar Adapter/Profile, not Tracking Core.
- commercial/OSS comparisons are design input, not Production proof.
- code duplication/potential bug audit is required before broad avatar-system expansion.

Current relation: future Avatar work remains subordinate to current Production authority and semantic correctness.

### 2026-09-02 — Face Landmarker tracking comparison

Archive: `KiwiChat_FaceLandmarkerTrackingComparison_Updated_20260902-JST.zip`

Validated:

- no evidence justifies immediate replacement of Face Landmarker.
- Face Landmarker Primary remains.
- geometry/rigid pose/adaptive stabilization are possible future local improvements only after current mainline correctness work.

### 2026-09-02 — Standalone executable / Spout / OBS

Archive: `KiwiChat_StandaloneBuildSpoutOBS_Updated_20260902-JST.zip`

Chat-scope result:

- standalone executable operational.
- MediaPipe model loading via `StreamingAssets` worked.
- Face Landmarker tracking → Spout sender → OBS display functionally worked by direct observation.

This is **not** automatic current Production acceptance for `D:\KiwiAvatarSystem`; local Production mapping must be reverified before reuse.

### 2026-09-02 — Unity project intake / archive workflow

Archive: `KiwiChat_UnityProjectIntakeAndArchiveWorkflow_Updated_20260902-JST.zip`

Validated:

- normal full-project intake minimum = `Assets/ + Packages/ + ProjectSettings/`.
- generated folders such as Library/Temp/Logs/obj are not normally required for source reconstruction unless a specific investigation needs them.
- Permanent ChatReport workflow is standard.
- archive request means actual ZIP/report creation, not only prose.
- archive timestamp = last relevant work before archive request.
- if time is unknown, DATE_ONLY; never guess.
- main user-facing deliverable should be consolidated to minimize transfers.

### 2026-09-02 — WMC audit / file-handoff diagnosis

Archive: `KiwiChat_WMCStaticAuditAndFileHandoff_Updated_20260902-JST.zip`

Validated:

- Webcam Motion Capture remains the most important commercial comparison target.
- binary-heavy/local static audit: Codex/local inspection → Consolidated REPORT → normal-chat re-audit.
- WMC public/static evidence suggests MediaPipe Tasks/TFLite and likely Windows CPU/XNNPACK, but runtime delegate/thread/cadence internals were not fully proven.
- WMC CPU use is **not** evidence to make Kiwi CPU Production.
- ZIP handoff failure was not explained by size, filename language, EXE/DLL presence, project source limit, or storage capacity.
- ZIP processing root cause remains paused/unresolved; individual text reports were a workaround.

### 2026-09-02 23:21:37 JST — Phase16.19 migration compile recovery archive

Archive: `KiwiChat_Phase16_19MigrationCompileRecovery_Updated_20260902-2321-JST.zip`

Historical branch findings:

- strict FacePart presentation epoch / single handoff design is directionally valid.
- migration overlays exposed multiple repair-script/source-anchor defects.
- stale GitHub main must not be used to reconstruct newer local cumulative `KiwiFaceMotion`.
- r4 defective; r5 not proven; r6 repair awaited compile at that chat endpoint.

Current reconciliation:

This is an archive-local historical unresolved state.  
It does **not** override the later project-wide statement that current Tracking Core baseline is stable unless current local Production proves otherwise.

### 2026-09-03 — Commercial Tracking / Presentation architecture re-audit

Archive: `KiwiChat_CommercialTrackingPresentationArchitectureAudit_Updated_20260903-JST.zip`

Retained:

- Persistent ROI through short confidence dips.
- Static Rest.
- fresh-only onBeforeRender.
- stable desktop 3-lane baseline.
- one temporal Root presentation owner.
- `KiwiFaceMotion` should be sole Root temporal presenter; Quality10 policy/QoS/telemetry only.
- global strong LP/Kalman rejected.
- a branch-local potential duplication remains: ProviderHub normalization vs KiwiFaceMotion resume/provider bridge.

Branch-local next: Provider Normalization Ownership Audit.  
Global next remains v44.55.24.

### 2026-09-03 — Pure Project recovery / Near-Raw tracking

Archive: `KiwiChat_PureProjectRecoveryAndNearRawTracking_Updated_20260903-JST.zip`

Important archive findings:

- Safe Mode/compile recovery completed enough to continue.
- near-raw direction, static micro-jitter suppression and fast-motion responsiveness were explored.
- pure Yaw→false X Root motion and pure Roll→arc Root motion exposed coupling issues.
- attempted Yaw/X decoupling caused Eye regression and was withdrawn.
- critical self-audit found delivered RollArcFix bytes did not match validator SHA; prior “20/20 PASS” claim is not valid for the materialized file.
- `Assets(2).zip` lineage must be established before broad rewrite.
- user-requested future architecture comparison requires at least three designs and ten review passes.

Current reconciliation:

Do not merge or overwrite current mainline Tracking Core from this branch until lineage and current Production authority are proven.  
Global v44.55.24 work remains first unless the user explicitly switches to this branch.

---

## 5. PROJECT CHAT COVERAGE AUDIT

Current known major project-chat mapping:

- Native Camera Pipeline Optimization → **covered directly**
- 再監査結果の要約 → **covered across Native provenance / CPU semantic review / WMC handoff**
- 再監査完了報告 → **covered across WMC / archive workflow / latest authority sync**
- 診断経路統合完了 → **covered across TrackingObserver / Phase16.19 / NativeCamera archives**
- KiwiVTuber開発進行状況 → **covered by NaturalMotion + PureProject**
- トラッキング方式比較 → **covered by FaceLandmarkerTrackingComparison + commercial tracking audits**
- CPU化方針決定 → **covered by StaticFirst / CPUBackend / CPUInference semantic archives**
- Spout原因調査 → **covered by SurfaceFit/DX12 isolation + Standalone Spout/OBS**
- Zero Copy 続き確認 → **covered directly by ORT DML Zero-Copy archive**
- 比較結果まとめ → **covered by Standalone DX12 inference archive**
- Unityプロジェクト修正依頼 → **covered by updated UnityProjectIntakeAndArchiveWorkflow**
- 監査提案まとめ → **covered by StaticFirstCpuSemanticAB / static-first CPU semantic archive**
- 画質経路監査 → **DEDICATED CHAT ARCHIVE STILL MISSING**

Coverage verdict:

- Current Drive ChatReport count: **24 ZIP**
- Added/updated coverage closes the previously uncertain Unity-project and audit-proposal chats.
- A dedicated archive for **画質経路監査** is still absent.
- Its current important conclusions are partially preserved by other archives and project context, but the original chat chronology is not yet preserved in one dedicated ChatReport.

---

## 6. CURRENT VALIDATED DECISIONS

1. Face Landmarker remains Primary / Source of Truth.
2. Latest-frame architecture remains mandatory; FIFO/stale queues rejected.
3. Persistent ROI is required through short confidence dips.
4. Source liveness and source age are separate.
5. Same Provider Resume != Provider Switch.
6. Head is a single rigid authority.
7. Eye/Mouth do not feed Root translation.
8. Texture/Crop/Mask is a same-sample transaction.
9. Tracking cadence and display cadence remain separate.
10. Root temporal presentation requires one owner.
11. Provider normalization should exist at one layer.
12. Native Camera Production baseline = Path B SystemMemoryNV12.
13. `cameraGeneration` = camera session / texture identity epoch.
14. Unity-visible Native Presentation Texture identity stays fixed.
15. D3D11On12 1080p Camera Production remains rejected.
16. Native metadata-driven color conversion is Production accepted.
17. steady-state MediaPipe image read path observed as CPUAsync; no read-mode fix required.
18. Windows MediaPipe delegate remains CPU unless independent evidence justifies change.
19. Spout/external resource handling must remain non-blocking/bounded; skip rather than queue indefinitely.
20. Same exact host tensor CPU/GPU backend math is effectively equivalent; backend itself is not the main semantic discrepancy.
21. Pixel parity alone is not semantic authority.
22. Production `lane.input` external readback is not an authority oracle.
23. v44.55.23 Observer Validity PASS is accepted, but `MIXED_TRANSACTION` is provisional.
24. Native provenance is Class B / Strongly Supported; guessed rebuilds are prohibited.
25. Windows PowerShell 5.1 compatibility is a fixed requirement.
26. Validator/Observer failures must be separated from Production failures.
27. LIVE CAMERA 560x315 presentation fix is CLOSED / Production Accepted.
28. Commercial/OSS behavior supplies design principles, not hidden-implementation facts.
29. WMC remains the principal commercial comparison target.
30. Permanent report/archive workflow is now standard.

---

## 7. REJECTED APPROACHES

- Face Landmarker replacement without independent Production evidence.
- FIFO or stale-frame accumulation.
- strong global smoothing / Kalman as root-cause masking.
- threshold relaxation after a failed gate.
- stale prediction continuation.
- Eye/Mouth/Yaw/Pitch/Roll driving Root translation.
- duplicate temporal Root presentation owners.
- multiple provider normalization layers.
- same-provider Resume treated as Provider Switch.
- `UpdateExternalTexture`.
- callback/worker-crossing IMFSample retention.
- Unity D3D12 queue `Wait`.
- blocking GPU waits.
- rotating Unity-visible texture identity.
- D3D11On12 1080p Camera Production.
- v35.3 Standalone performance as valid GPUCompute equivalence evidence.
- generic shader-stripping diagnosis without packaging proof.
- WMC CPU/XNNPACK behavior as direct reason for Kiwi CPU Production.
- CPU Production solely because CPU same-tensor service time is faster.
- mode2 Production solely because pixel parity looked best.
- fixed-point subtexel pursuit as the principal residual explanation after v44.55.12.
- Production `lane.input` direct oracle.
- treating v44.55.23 `MIXED_TRANSACTION` as final.
- guessed Native rebuild definitions.
- ABI/export parity alone as provenance closure.
- per-version PowerShell compatibility hotfix accumulation.
- display gamma/sRGB compensation for incorrect YUV semantics.
- broad Tracking Core rewrite to solve camera/inference/presentation defects.

---

## 8. SUPERSEDED / OBSOLETE CONCLUSIONS

### Native Camera

OLD: Native Camera is currently limited to ~13–20Hz.  
NEW: Path B SystemMemoryNV12 is stable/current baseline.  
STATUS: OLD CURRENT-STATE CLAIM = OBSOLETE.

OLD: Reverse Share / D3D11On12-style path is likely Production direction.  
NEW: Path B is Production baseline.  
STATUS: SUPERSEDED.

### LIVE CAMERA

OLD: LIVE CAMERA quality remains Runtime/Visual pending.  
NEW: 560x315 fix passed Compile + Runtime + Visual.  
STATUS: CLOSED / PRODUCTION ACCEPTED.

### Standalone inference

OLD: v35.3 Standalone is valid equivalent performance evidence.  
NEW: missing compute kernels invalidated the run; v37.2 was the valid path.  
STATUS: SUPERSEDED.

### CPU/GPU semantic work

OLD: CPU faster ⇒ switch Production CPU.  
NEW: speed alone is insufficient; semantic/transaction authority must pass.  
STATUS: REJECTED.

OLD: mode2 pixel parity best ⇒ Production candidate accepted.  
NEW: later semantic gates rejected that inference.  
STATUS: SUPERSEDED.

OLD: fixed-point D3D sampling precision is likely the remaining root cause.  
NEW: FULL_FLOAT best; fixed-point modes not supported.  
STATUS: REJECTED.

OLD: Production lane.input readback is a direct truth oracle.  
NEW: readback itself can create false mismatch.  
STATUS: REJECTED.

OLD: v44.55.23 MIXED_TRANSACTION is final.  
NEW: provisional pending pair-bound output lifecycle proof.  
STATUS: SUPERSEDED AS FINAL CLAIM.

### Tracking branch

OLD: Landmarker model itself is likely the primary accuracy problem.  
NEW: freshness/ROI/presentation/observer problems explained major quality deficits.  
STATUS: SUPERSEDED.

OLD: more transition bridges are naturally better.  
NEW: prove one normalization owner / one temporal presenter first.  
STATUS: SUPERSEDED.

### PureProject

OLD: delivered RollArcFix was proven 20/20 PASS.  
NEW: delivered source SHA did not match the validator's claimed source.  
STATUS: SUPERSEDED / ARTIFACT PARITY UNRESOLVED.

---

## 9. OBSERVER / VALIDATOR AUDIT HISTORY

Durable lessons from multiple chats:

1. Code existing in source does not prove the migration/runtime actually enabled it.
2. Live camera pixels and delayed semantic landmarks cannot be treated as one same-frame observation.
3. Canonical annotation may contain mirror/crop bugs even if Production tracking is correct.
4. Missing shader/kernel packaging can invalidate a Standalone performance run.
5. Telemetry columns can be invalid even when the underlying feature works.
6. Source-string validation is insufficient; Unity actual compilation is required.
7. Same-name file resolution by filename alone can target the wrong source.
8. PowerShell 7 assumptions can invalidate Windows PowerShell 5.1 tooling.
9. launcher/BAT behavior can falsely classify a valid build/run as failure.
10. Production `lane.input` queue-external readback can alter/contaminate the observation.
11. Inference Engine logical Tensor length is not necessarily backing ComputeBuffer capacity.
12. v44.55.23 initially treated backing buffer capacity mismatch as a shape defect; package source proved pooled capacity may be larger.
13. Worker-owned `PeekOutput()` storage can be reused; post-record reacquisition can create lifecycle ambiguity.
14. Therefore v44.55.24 must bind shadow output snapshots while the same pair is alive.

Rule:

**Fix/validate the Observer or Validator before changing Production math when observer validity is uncertain.**

---

## 10. CURRENT KNOWN CRITICAL SHA / PROVENANCE

The following values are the latest known guarded values from the available reports/project context.  
They are **not claimed as freshly re-read from local Production on 2026-09-03**.

`LOCAL_PRODUCTION_NOT_DIRECTLY_VERIFIED`

- `Assets/Script/KiwiInferenceFaceTracker.cs`
  - SHA256 `52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695`
- `Assets/Script/FaceLandmarkerRunner.cs`
  - SHA256 `6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93`
- `Assets/KiwiAvatarSystem/Runtime/Camera/KiwiNativeCameraInterop.cs`
  - SHA256 `AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5`
- `Assets/Plugins/x86_64/KiwiNativeCamera.dll`
  - SHA256 `82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5`
- `Native/KiwiNativeCamera/Source/KiwiNativeCameraPlugin.cpp`
  - SHA256 `636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A`
- `Assets/Script/KiwiFaceMotion.cs`
  - latest project-context SHA256 `D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6`
  - SOURCE=`PROJECT_CHAT_CONTEXT`, not freshly local-verified.
- `Assets/Scenes/Face Landmark Detection.unity`
  - latest project-context SHA256 `E28B0B81F731B92B04B97D4AA7DB1187C8DD57C5A39F49B08B2E6799BBF5B3D1`
  - SOURCE=`PROJECT_CHAT_CONTEXT`, not freshly local-verified.

Native provenance:

- recovered v44.55.12 original package:
  `KiwiAvatarSystem_v44_55_12_D3DFixedPointSubtexelSamplingParityAudit.zip`
- package SHA256:
  `06666AF4E0FDA1013EC4E9E865C2FC97BE4CC8730F9F1CEACE47EB8F973D0DF2`
- provenance classification:
  **Class B / Strongly Supported**
- Class A deterministic build identity remains unproven and is not a current blocker.
- Do not rebuild Native based on guessed build definition.

---

## 11. RUNTIME / PERFORMANCE AUTHORITY SUMMARY

### Native Camera capability

Standalone Media Foundation / DXGI evidence:

- ~60.3fps native NV12 capability confirmed.
- Shared Bridge ~60fps confirmed.
- D3D11On12 1080p path rejected.

Later Path B Production state supersedes the historical ~13–20Hz Unity diagnostic state.

### v44.54 same-tensor CPU/GPU backend baseline

Same exact host input:

- CPU service median ~11.81ms.
- GPU service median ~40.53ms.
- raw output difference effectively negligible.

Interpretation:

- CPU is faster on that matched test.
- This is **not** sufficient authority for Production CPU because preprocessing/transaction semantic is unresolved.

### v44.55.23 semantic observer

- 115/115 completed.
- Observer faults = 0.
- snapshot failures = 0.
- anomalous = 8.
- Production==REF = 5 anomalous pairs.
- Production==MODE2 = 0.
- Neither exact = 3.
- Observer validity = PASS.
- domain verdict `MIXED_TRANSACTION` = provisional.

### LIVE CAMERA

Final direct preview:

- old 412x236.
- accepted 560x315.
- horizontal aspect compression resolved.
- displayed pixel area increased ~81%.
- Compile PASS.
- Runtime PASS.
- Visual PASS.
- no Native/Tracking/Inference workaround used.

---

## 12. CURRENT OPEN ISSUES

### P0 — Project-wide current blocker

**v44.55.24 Pair-Bound Shadow Output Snapshot**

Need to determine whether v23 `NEITHER_EXACT` is:

- real Production-vs-shadow output divergence, or
- Worker output lifecycle/reuse Observer artifact.

Required:

- capture REF_FROZEN_GPU and MODE2 B_GPU while the same pair is alive.
- fixed pair token / record index / sequence / NativeHostTicks / ManagedHostTicks / LaneStartedHostTicks.
- observer-owned GPU snapshot before next Worker reuse.
- no record-completion PeekOutput fallback.
- Production writes 0.
- Native writes 0.
- Tracking/ROI/threshold writes 0.
- blocking waits 0.

### P1 — After v44.55.24 only

1. Production Output Transaction Authority.
2. Reference Transaction Authority.
3. pixel-center / subtexel / transform arithmetic if still required.
4. TextureConverter sampling semantic.
5. Native sampling arithmetic/FMA/MAD/precise only if evidence still points there.
6. preprocessing semantic PASS.
7. canonical semantic PASS.

### P2 — Only after Semantic Correctness PASS

1. Production CPU backend candidate.
2. CPU/GPU clean Performance A/B.
3. inference latency.
4. accepted source age.
5. canonical cadence.
6. CPU/GPU load.
7. camera cadence regression.
8. long-run resource/memory stability.

### P3 — Lifecycle / Recovery

- Camera restart.
- no-frame hold.
- Face Lost / Reacquire.
- soft-retire.
- same-provider Resume.
- Provider Switch.
- generation/epoch.
- stale/duplicate/out-of-order rejection.
- lane reuse.
- backend fault isolation.
- mixed backend epoch rejection.

### P4 — Visual final validation

- MP4 frame-by-frame when visual authority is needed.
- Yaw / Pitch / Roll.
- Root translation.
- Head response.
- Eye / Blink / Mouth.
- fast motion / reversal.
- static rest / jitter.
- stale movement absence.
- FacePart alignment.
- LIVE CAMERA regression.
- Spout regression.

### Branch-local / deferred open issues

Tracking Presentation branch:

- ProviderHub vs KiwiFaceMotion normalization ownership audit.
- Phase16.17 runtime validation.

PureProject/Avatar branch:

- `Assets(2).zip` lineage.
- exact Roll-safe Root translation artifact parity.
- Eye non-regression.
- minimum-three architecture comparison.
- ten-pass review before final architecture.
- Runtime model import / transactional hot swap / arbitrary shapes / Eye-Mouth placement / spring-tail / in-app tuning.

WMC:

- full tracking/filter/camera/receiver/runtime audit remains incomplete.

Archive/tooling:

- ZIP handoff root cause paused.
- some historical reports use legacy section format.
- dedicated 画質経路監査 Chat Archive missing.

---

## 13. KNOWN ARCHIVE COVERAGE GAPS

### GAP-1 — `画質経路監査` dedicated ChatReport missing

Status: **OPEN / HIGH VALUE**

Current conclusions from that chat are partially represented elsewhere:

- LIVE CAMERA final accepted state.
- Native/Presentation quality separation.
- v44.55.20–23 semantic/transaction chain.
- v44.55.24 handoff.

But the chat's exact start→investigation→decision chronology is not preserved in one dedicated archive.

Recommended archive:

`KiwiChat_LiveCameraAndInferenceQualityPathAudit_Updated_<LastWorkUpdate>-JST.zip`

Use DATE_ONLY if exact last-work time cannot be proven.

### GAP-2 — Native v19 → Path B transition lacks one dedicated archive

v12-v18 archive ends with v19 proposed.  
Later reports already treat Path B SystemMemoryNV12 as established.

The transition itself should eventually be preserved as a dedicated historical archive or a Native domain history appendix.

### GAP-3 — v44.55.20–23 lacks one dedicated ChatReport sequence archive

Authority is preserved in later CPUInference reports, but the full observer-defect/transaction progression is not independently archived as one exact chat-history package.

This is lower urgency than GAP-1 because current authority is well preserved.

### GAP-4 — MASTER REPORT previously absent

Status: **CLOSED BY THIS FILE**

This report is the first project-wide single-entry authority index generated from the current 24 ChatReport set.

---

## 14. DO NOT CHANGE WITHOUT NEW EVIDENCE

- Face Landmarker primary authority.
- `KiwiFaceMotion.cs` / `KiwiInferenceFaceTracker.cs` as camera/performance workaround.
- Tracking thresholds / ROI policy solely to hide camera/inference defects.
- Path B Native Camera stable architecture.
- fixed Presentation Texture identity.
- metadata-driven accepted color path.
- LIVE CAMERA accepted pixel-space fix.
- Production Native DLL.
- Production GPU inference authority before semantic closure.
- Provider/Root authority by adding another filter/bridge layer.
- strict same-sample FacePart transaction.
- no-FIFO/latest-frame design.
- PowerShell 5.1 compatibility foundation.
- Observer/Validator contracts without validating the validator itself.

Commit/push: only on explicit instruction.

---

## 15. FILE / HANDOFF WORKFLOW — CURRENT

### Normal chat

Best for:

- external research,
- design judgment,
- commercial/OSS comparison,
- Codex report re-audit,
- Runtime CSV/TXT/Player.log judgment,
- MP4 frame-by-frame judgment,
- Production acceptance decision,
- Chat Permanent Report / MASTER construction.

### Codex/local repo work

Best for:

- `D:\KiwiAvatarSystem` cross-repo inspection,
- current actual SHA,
- source/history/provenance,
- whole-file edits,
- PowerShell/Validator,
- compile/build,
- ABI/DllImport/export,
- static lint/write-target,
- Unity batchmode,
- Consolidated REPORT.

### File-transfer principle

Minimize transfers:

- Codex work → one Consolidated REPORT.
- Runtime evidence → one ZIP when possible.
- Visual → Runtime ZIP + MP4 only if visual evidence is needed.
- Installer/fix → one Complete ZIP.
- Do not request unnecessary re-uploads.

---

## 16. CURRENT NEXT ACTION

**Do v44.55.24 Pair-Bound Shadow Output Snapshot first.**

Do not advance the current Production mainline to:

- CPU Production conversion,
- Native redesign/rebuild,
- Tracking-Core rewrite,
- pixel-center/FMA deep-dive,

until the Worker-output lifecycle ambiguity is closed.

Decision branch:

A. Pair-bound snapshots eliminate `NEITHER_EXACT` and Production==REF  
→ v23 mixed result was Observer lifecycle artifact.

B. Pair-bound snapshots reproduce `NEITHER_EXACT`  
→ only then treat it as genuine Production-vs-shadow output divergence and continue deeper preprocessing arithmetic investigation.

---

## 17. CHAT ARCHIVE INVENTORY — SHA256

Current Drive ChatReport inventory at re-audit: **24 ZIP archives**.

| LastWorkUpdate | Precision | Archive | Archive Status | Topic | ZIP SHA256 |
|---|---|---|---|---|---|
| 2026-08-21 23:41 JST | DATE_TIME | `KiwiChat_NaturalMotionTrackingFacePartMask_v5_0_1_Updated_20260821-2341-JST.zip` | SUPERSEDED | v4.5 → v5.0.1 Tracking・Natural Motion・FacePart Mask改善チャット | `8B612B452F03300FD5D49281A9CDACC170068A2BB06CF52FB58D5073FA235849` |
| 2026-08-23 | DATE_ONLY | `KiwiChat_CommercialTrackingLandmarkPresentation_Updated_20260823-JST.zip` | SUPERSEDED | 商用VTuber / SNSフィルター品質化 — Landmark / Tracking / Model Follow / Mask / Provider Authority / Presentation | `3E3245BC352B92B65DD152D58792B2AB72A905A2033C14F94900377D6326E4AE` |
| 2026-08-23 | DATE_ONLY | `KiwiChat_TrackingObserver_FineCam1080p60_Updated_20260823-JST.zip` | SUPERSEDED | Phase16.19.5 Live/Matched Observer Separation → Phase16.20 DX11/DX12 Inference A/B → Phase16.20.3 FineCam Preferred 1080p60 | `CF20366E155617B44735B9C34D8EFD3D404871196D1E2535C1552557A2B626F3` |
| 2026-08-23 20:58 JST | DATE_TIME | `KiwiChat_Phase16_19_FacePartLandmarkDebug_Updated_20260823-2058-JST.zip` | PARTIALLY_DONE | Phase16.19.2–16.19.4 Runtime Validation and Landmark Debug Repair | `AE02D4AB4FC8FEEECE1585DCAE66874A5CA9564ABCA61742485A6BCE986B0DFD` |
| 2026-08-24 | DATE_ONLY | `KiwiChat_NativeCameraCompatibilityReverseShare_Updated_20260824-JST.zip` | SUPERSEDED | Windows Native Camera 1080p60 / Unity D3D12 Compatibility Reverse Share診断 | `DDB6BB94CACB803D9147B9AF27467889B5AAF6ECAB32D56AE466B93E137FEAE5` |
| 2026-08-24 | DATE_ONLY | `KiwiChat_NativeCameraPipeline_v12_v18_Updated_20260824-JST.zip` | DONE | Native Camera Pipeline Optimization | `839D2BAE7D88DBB96589A72A92D7BBB0C3DD3F728A6267A5A0051A3F7F4E8DD5` |
| 2026-08-26 13:43 JST | DATE_TIME | `KiwiChat_StandaloneDX12InferenceValidation_v37_2_Updated_20260826-1343-JST.zip` | SUPERSEDED | v35-v37.2 Windows Standalone DX12 Inference Validation | `9B216673D884124B35AF2A72B53EF19DC70804306E50C897CCEC83C8E9184C9E` |
| 2026-08-27 19:47 JST | DATE_TIME | `KiwiChat_OrtDmlZeroCopy_v42_3_Updated_20260827-1947-JST.zip` | DONE (HISTORICAL_CHAT_ARCHIVE; current project work has advanced beyond v42.3) | v42.3 ORT DirectML D3D12 Zero-Copy Shadow 成立とNative Plugin Lifecycle根本修正 | `4CA3E56335ECED3A01733AB913E53FB9067B0D84936B056D60A1AF32B8316D30` |
| 2026-08-29 02:21 JST | DATE_TIME | `KiwiChat_SurfaceFitV44_17_DX12ValidationIsolation_Updated_20260829-0221-JST.zip` | PARTIALLY_DONE | Surface Fit v44.15-v44.17 Optimization and DX12 Validation Isolation | `65092544816D648D33DB1D89F67657AA0612FD5E4E2C744EA41C4C0186BA97AD` |
| 2026-08-30 | DATE_ONLY | `KiwiChat_NativeCameraColorAndLandmarkerReadMode_Updated_20260830-JST.zip` | PRODUCTION_ACCEPTED | Native Camera Color / Production Mode 9 / CPUAsync Audit | `0C1DD46C4AF422003580745C9BE7804C3963726A7521EEB0E2C9AE8E9C5D7ADF` |
| 2026-08-31 (DATE_ONLY) | DATE_ONLY | `KiwiChat_CPUBackendSemanticAudit_Updated_20260831-JST.zip` | ACTIVE / HANDOFF_READY | Legacy report | `BA6788177DB40FEEC5393C9B8B9C9E4220877FE12ADD2AEC3D437131F56DE6D5` |
| 2026-08-31 JST | DATE_ONLY | `KiwiChat_StaticFirstCpuSemanticAB_Updated_20260831-JST.zip` | LEGACY_FORMAT / STATUS_NOT_EXPLICIT | v44.55.10～v44.55.13 FIX2 / Presentation frame identity → CPU semantic A/B static-first validation | `A43566B30E6F6C19CCD61AC5B3C350425E056EC0A7F13CD27008AD6670726563` |
| 2026-08-31 19:06 JST | DATE_TIME | `KiwiChat_CPUBackendParity_StaticFirst_Updated_20260831_1906-JST.zip` | v44.55.13 FIX2 STATIC_ALL_PASS | CPU Backend Parity / Presentation Frame Identity / Static-First Validation | `667BB94318D4BF3C7D094BCE1164E2269C081FB8500E3C6AF9AD17AB5588CFAE` |
| 2026-09-02 | DATE_ONLY | `KiwiChat_CPUInferenceSemanticAuthorityReview_Updated_20260902-JST.zip` | SUPERSEDED | v44.55.15 Exact-HostTicks CPU Semantic Gate and Whole-System Review | `1862488D13CF81F4CD46B251D332DD2CDD5DC1D827515E1394884921E0222A3B` |
| 2026-09-02 | DATE_ONLY | `KiwiChat_CPUInferenceSemanticAuthority_Updated_20260902-JST.zip` | IN_PROGRESS | Native Exact-HostTicks Preflight → v44.55.15 Semantic Gate → Project-Wide Reassessment | `6F56B545308EFEB6239F109A8E0E13300930FBD283A42A133D11ADC97B0C21CD` |
| 2026-09-02 | DATE_ONLY | `KiwiChat_CommercialFacePartArchitectureAndCodeAudit_Updated_20260902-JST.zip` | IN_PROGRESS | 商用システム比較から3D Model Primary + 2D Face-Part Constraintを詰め、v4.1〜v4.5累積実装と全コード重複・潜在バグ監査を実施したチャット | `B9664812C175A295F95A98DDCE2A8685A22CD44B5A11C711EC09B00D080C79B7` |
| 2026-09-02 | DATE_ONLY | `KiwiChat_FaceLandmarkerTrackingComparison_Updated_20260902-JST.zip` | DONE | Landmarker以上のトラッキング処理はある？ | `38E7A665E83FD67E926DB64C505FAE68453E39F6037C207A6D53A64AB8856B64` |
| 2026-09-02 | DATE_ONLY | `KiwiChat_StandaloneBuildSpoutOBS_Updated_20260902-JST.zip` | DONE (CHAT_SCOPE; NOT A GLOBAL PRODUCTION ACCEPTANCE) | KiwiVTuber Standalone executable and OBS integration completion | `7BBDE24F22F4DEDAC6DC3FA3566289E3464B1443DA7FAA991D3F181C5087A56C` |
| 2026-09-02 | DATE_ONLY | `KiwiChat_UnityProjectIntakeAndArchiveWorkflow_Updated_20260902-JST.zip` | DONE | Unityプロジェクト一式からの構築可否確認 / 恒久REPORT・Archive運用確定 | `CA6347409141F7B9FA7E402EC072A35BE2D03BD96AE21A5D72B81B367B20C8E8` |
| 2026-09-02 | DATE_ONLY | `KiwiChat_WMCStaticAuditAndFileHandoff_Updated_20260902-JST.zip` | PARTIALLY_DONE | WMC比較調査・ZIP受け渡し診断・最新Authority同期 | `82B2B3725C59DEBC568C7EA3F619387D5447F5D4123A3BE6C9A530FF1A8239DC` |
| 2026-09-02 00:26:43 JST | DATE_TIME | `KiwiChat_NativeProvenanceAndPowerShellFoundation_Updated_20260902-0026-JST.zip` | DONE | v44.55.12 Native Original Package Provenance Closure + PowerShell Foundation | `2D438E4D0610D562CE47BD8ED68AA5E203296388FB6B7A0D78C853AB4E254787` |
| 2026-09-02T23:21:37+09:00 | DATE_TIME | `KiwiChat_Phase16_19MigrationCompileRecovery_Updated_20260902-2321-JST.zip` | VALIDATION_PENDING | Phase16.19 Strict FacePart Presentation Epoch + Phase16.18 Single Handoff Migration Recovery | `1F73EF9AB669D65B7FA9251A746F064FBC7D865FAB20BAB75DC08314E8D69740` |
| 2026-09-03 | DATE_ONLY | `KiwiChat_CommercialTrackingPresentationArchitectureAudit_Updated_20260903-JST.zip` | IN_PROGRESS / VALIDATION_PENDING | Phase16.9–16.17 Tracking / Presentation 全体監査 | `3D5F686827060815CCA25E4B3E40CEB0CA2154A088861B9D36B59D52A4A95F8A` |
| 2026-09-03 | DATE_ONLY | `KiwiChat_PureProjectRecoveryAndNearRawTracking_Updated_20260903-JST.zip` | PARTIALLY_DONE | Pure Project/Safe Mode Recovery + Near-Raw Tracking Refinement + Avatar System Next Plan | `DF8DC78BF4758B0448D08E73B6E2DE9FFDBA05F55E77225D02B8A8D6A2242D8C` |

Notes:

- Several legacy reports do not use the latest section schema, but their technical content is preserved.
- `KiwiChat_StaticFirstCpuSemanticAB_Updated_20260831-JST.zip` is the newly added audit-proposal/static-first archive and uses a legacy text layout.
- `KiwiChat_UnityProjectIntakeAndArchiveWorkflow_Updated_20260902-JST.zip` is present as the updated Unity-project/archive-workflow report.
- ZIP SHA values above were calculated from the currently retrieved Drive copies in this re-audit.

---

## 18. MASTER UPDATE RULES

Future important chat completion should update this MASTER with only current-impacting items:

1. add Archive Name + LastWorkUpdate to the index.
2. promote only evidence-backed Validated Decisions.
3. move old conclusions to SUPERSEDED, do not silently erase them.
4. update Open Issues and Current Next Action.
5. refresh current Production SHA only from actual local Production or an authoritative same-Production report.
6. preserve Correctness / Performance / Visual / Provenance separation.
7. never infer exact archive time when only the date is known.
8. keep detailed chronology in Chat Permanent Reports; keep this MASTER concise enough to remain the single entry point.

---

## 19. HANDOFF

A new chat should begin from this MASTER plus the latest same-Production Runtime/Consolidated REPORT.

Current handoff:

- Tracking Core baseline: stable.
- Native Camera: Path B stable.
- LIVE CAMERA: closed / Production Accepted.
- Production GPU inference remains authority.
- backend-only same-tensor difference is not the principal semantic problem.
- current unresolved layer is preprocessing/output transaction authority.
- v44.55.23 observer gate passed 115/115.
- v44.55.23 `MIXED_TRANSACTION` is provisional.
- v44.55.24 Pair-Bound Shadow Output Snapshot is the project-wide next action.
- no Production CPU / Native / Tracking changes before that authority question is closed.
- dedicated `画質経路監査` archive remains the main historical reporting gap.

END OF MASTER REPORT
