# KiwiAvatarSystem v44.55.23 Production Output Transaction Authority Observer Re-Audit / Fix Report

Date: 2026-09-02 (Asia/Tokyo)

## [EXECUTIVE_VERDICT]

**OBSERVER FIX VALIDATED / UNITY COMPILE PASS / FULL PREFLIGHT PASS / WINDOWS X64 DEVELOPMENT BUILD PASS / RUNTIME OBSERVER GATE PASS**

The v44.55.23 observer incorrectly treated a Unity Inference Engine `ComputeTensorData` backing-buffer capacity as the logical tensor length. The local Production package source proves that its GPU tensor allocator may reuse the smallest available buffer whose capacity is greater than or equal to the requested logical tensor length. Therefore a logical 1405-float output backed by a 147456-float buffer is valid.

The observer now keeps the logical tensor contract strict (`tensor.shape.length == 1405`) while accepting a source GPU buffer only when `stride == sizeof(float)` and `count >= 1405`. Its compute dispatch remains bounded to the first 1405 floats. It also consumes a dependency record index once its capture state is already failed, so an observer-failed pair cannot retry the same snapshot every frame.

The same-run Runtime completed 115/115 pairs without observer faults or snapshot failures. The observer validity gate passed. The unchanged v44.55.23 decision rule returned `MIXED_TRANSACTION` from 8 anomalous pairs: 5 Production outputs were bitwise equal to REF_FROZEN_GPU and 3 were equal to neither candidate. This is a domain result, not an observer failure.

No Production, Native, Tracking, ROI, threshold, v44.55.20 decision, packed-output comparison, anomaly selector, or bitwise-equality rule was changed. No commit or push was performed.

## [PRODUCTION_AUTHORITY]

Authority order used:

1. Current local Production placement under `D:\KiwiAvatarSystem`.
2. Current local Unity Inference Engine package source, version 2.4.1.
3. Fresh Unity 6000.0.80f1 compile, Full Preflight, Windows x64 Development build, and same-build Runtime evidence.
4. Official Unity API documentation only as a public API cross-check.

Target observer before SHA256:

`030F1D2F76AAC3686672D343C73203CBE5BD8DC4534570CBB8F12D602DC53C1F`

Target observer after SHA256:

`AE2CD168171335E20F6793C5F46A12264D86CCAE21DF62FAFF74FCBA1E92E4A7`

The observer was untracked in the current dirty worktree before this task. Ambient v44.55.22, diagnostic, build, and validation files were not treated as changes made by this fix.

## [CONFIRMED_FACTS]

### Local package source authority

Resolved package:

`D:\KiwiAvatarSystem\Library\PackageCache\com.unity.ai.inference@587873fd5e1b`

Package version: `com.unity.ai.inference` 2.4.1, Unity requirement 6000.0.

Confirmed from the actual package source:

- `TensorDataPool<T>.AdoptFromPool(int size)` binary-searches the sorted free-buffer capacities and returns the first capacity greater than or equal to the requested size. Exact equality is not required.
- `GPUComputeBackend.AllocTensorFloat` requests `shape.length`, then independently sets the returned tensor's logical `shape` and `count` to that shape while associating the potentially larger reused `ComputeTensorData`.
- Runtime `Tensor` stores `shape`, logical `count`, and one `ITensorData` reference. It has no runtime element-offset or view-origin field.
- `ComputeTensorData` stores only its `ComputeBuffer` and capacity count. It has no tensor element-offset/view field.
- `ComputeTensorData.Upload` writes to destination element index 0.
- Its bounded download/readback overloads use byte offset 0 and return the first requested logical elements.
- Consequently, for this GPUCompute tensor representation, the logical output starts at backing-buffer element 0. A larger `buffer.count` is capacity, not an alternate logical shape.

Source locations:

- `TensorDataPool.cs`: lines 45-67 and 81.
- `GPUCompute.cs`: lines 56-67.
- `Tensor.cs`: lines 30-68 and 83-89.
- `ComputeTensorData.cs`: lines 119-149, 246, 263-283, and 310-346.

Source SHA256:

| Package source | SHA256 |
|---|---|
| `Runtime/Core/Backends/TensorDataPool.cs` | `C0765322525E209E31DA7F578C9BD2DEFA22418FD92FBA58E2231DB5E9F3CE79` |
| `Runtime/Core/Backends/GPUCompute/GPUCompute.cs` | `EFB9DE3F4C7EA7C33142B6B55994378121985713A65ACF97E1F8BCBD7C52BAF7` |
| `Runtime/Core/Backends/GPUCompute/ComputeTensorData.cs` | `38523335457507326E91FA5E40C332E99A18C7BFF556336F3016627788AA02D9` |
| `Runtime/Core/Tensor.cs` | `373377D461C8FE5FCF800E598225FF415D16304B205716B1D9A15CB1942F3781` |

### Original observer defect

The pre-fix observer required:

`source.count == PackedOutputLength && source.stride == sizeof(float)`

For a valid logical output of 1405 floats backed by a reused 147456-float buffer, this produced:

`Snapshot source buffer shape mismatch count=147456 stride=4`

The same `SubmitTensorSnapshot` method is used by all three authorities:

- Production `pendingOutput`
- v44.55.20 `REF_FROZEN_GPU` output
- v44.55.20 `B_GPU` / MODE2 output

Therefore the incorrect exact-capacity test affected all three sources.

### Repeated index=0 failure

`CaptureCompletedDependencyRecordWindow` derives the next record from `_lastRecordCapturedIndex + 1`. Before the fix, `_lastRecordCapturedIndex` advanced only after both v44.55.20 output snapshots submitted successfully. A submit exception set `state.Failed = true` and returned, but the next frame did not check `state.Failed`; it selected the same record index and retried.

## [PUBLIC_DESIGN_PRINCIPLES]

Unity's official `AsyncGPUReadback.Request(ComputeBuffer, size, offset, callback)` API defines `size` and `offset` in bytes, confirming that bounded, offset-explicit buffer transfers are a supported API contract: [Unity 6 AsyncGPUReadback.Request](https://docs.unity3d.com/jp/current/ScriptReference/Rendering.AsyncGPUReadback.Request.html).

The selected design retains the existing observer-owned GPU snapshot before async readback. This preserves temporal ownership when the Inference Engine source buffer may be returned to and reused by its allocator. Directly reading the larger Production-owned backing buffer later was not adopted.

## [ALTERNATIVES_COMPARED]

1. Keep `source.count == 1405`: rejected because it contradicts the current package allocator and the observed valid capacity reuse.
2. Accept `source.count >= 1405`, retain stride and logical-shape checks, and copy only the first 1405 floats: adopted. It preserves strict logical semantics and existing snapshot ownership.
3. Read the Production-owned source buffer directly with a bounded async readback: rejected for this fix because it changes the existing ownership/lifetime strategy and could race allocator reuse.
4. Copy all 147456 floats: rejected because it would snapshot unrelated backing capacity, increase observer work, and violate the packed-output contract.

## [IMPLEMENTATION]

### Capacity-aware validation

`SubmitTensorSnapshot` now requires all of the following:

- `tensor.shape.length == PackedOutputLength` where `PackedOutputLength == 1405`.
- `ComputeTensorData` exists and its `ComputeBuffer` is valid.
- `source.stride == sizeof(float)`.
- `source.count >= PackedOutputLength`.

`source.count > 1405` is valid. `source.count < 1405` or non-float stride remains fail-closed and increments `outputShapeMismatchCount`.

The one shared method applies this validation to Production, Reference, and Mode2 outputs.

### Bounded snapshot

Unchanged and reverified:

- Observer destination buffer length: exactly 1405 floats.
- Compute parameter `_Count`: exactly 1405.
- Dispatch group count: derived from 1405 and 256 threads.
- Shader guard: `index >= _Count` returns.
- Shader copy: `_Destination[index] = _Source[index]` for logical indices 0 through 1404 only.
- Async readback source: observer-owned 1405-float destination, not the 147456-capacity Production buffer.

The compute shader was not changed.

### Failed-pair retry suppression

After record identity lookup and before sequence/input/output processing, an already-failed `CaptureState` now advances `_lastRecordCapturedIndex` to that record and returns. It performs no new snapshot submission.

Fail-closed behavior is retained:

- `state.Failed` remains true.
- Failed captures are not finalized as valid samples.
- A failed capture remains visible to the existing completeness rule, so the final decision cannot silently pass.
- Snapshot buffers remain protected by the existing callback `finally` release, submit-exception release, and `OnDisable` cleanup paths.

No successful-sample path, comparison rule, or decision rule changed.

## [CHANGED_FILES]

Files modified by this task:

1. `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiProductionOutputTransactionAuthorityV44_55_23.cs`
   - Capacity lower-bound validation.
   - Observer-local failed-record retry suppression.
2. `Tools/KiwiV44_55_23/Build-KiwiV44_55_23.ps1`
   - Updated observer SHA guard only.
3. `Tools/KiwiV44_55_23/README.txt`
   - Updated observer SHA only.
4. `KiwiValidation/KIWI_PRODUCTION_OUTPUT_TRANSACTION_AUTHORITY_V44_55_23_REPORT.md`
   - This consolidated report.

Validation/build outputs generated or refreshed by the requested checks:

- `KiwiValidation/KiwiPreflight_v44_55_23.json`
- `KiwiValidation/KiwiBuild_v44_55_23.log` (ignored by the current Git rules)
- `Builds/v44_55_23ProductionOutputAuthority/` Development Player (ignored by the current Git rules)
- v44.55.20/v44.55.23 Runtime TXT/CSV and `Player.log` under the user LocalLow evidence directory; these are outside the repository.

Verified unchanged:

- `Assets/KiwiAvatarSystem/Runtime/Validation/Resources/KiwiValidation/KiwiOutputSnapshotCopyV44_55_23.compute`
- `Tools/KiwiV44_55_23/Run-KiwiV44_55_23.ps1`
- v44.55.20 dependency and all Production/Native/Tracking/ROI files.

The transient `Assets/Editor/KiwiBuildV44_55_23.cs` and `.meta` created by the provided build script were removed after the build. No user-owned file was replaced.

## [STATIC_VALIDATION]

### Unity and build

- Unity version: 6000.0.80f1.
- Whole-project C# compile: PASS as part of the Development build.
- Full Preflight: PASS (`passed=1`, `errors=0`, `critical=0`).
- Windows x64 Development build: PASS (`Succeeded`, 0 errors).
- Output EXE: `D:\KiwiAvatarSystem\Builds\v44_55_23ProductionOutputAuthority\KiwiAvatarSystem_v44_55_23_OUTPUT_AUTHORITY.exe`.
- Output EXE SHA256: `98751D0DFF0DD3ADE563C2B505A0E9F7A46E8E8895864212E1B884EDBCB42E81`.
- Build warnings: 205. No build error. Full Preflight separately retained two non-blocking known warnings: `Package.VersionDrift` for UGUI and `Dependency.UniVRMVersionUnverified`.

### PowerShell and diff

- `Build-KiwiV44_55_23.ps1` PowerShell parser errors: 0.
- `Run-KiwiV44_55_23.ps1` PowerShell parser errors: 0.
- `git diff --check`: PASS.
- No commit or push performed.

### Forbidden-change scan

Within the corrected observer:

| Requirement | Result |
|---|---:|
| Added `Graphics.Blit` | 0 |
| Added `RenderTexture` | 0 |
| Added Worker / Worker factory | 0 |
| `WaitForCompletion` | 0 |
| `CompleteAllPendingOperations` | 0 |
| `AsyncGPUReadback.WaitAllRequests` | 0 |
| Reflection `SetValue` / Production write | 0 |
| Production `pendingInput` / `lane.input` access | 0 |
| Native write | 0 |
| Tracking/ROI write | 0 |
| New blocking wait | 0 |

This fix adds no per-frame allocation. The failed-state check is a branch and integer assignment only; the capacity fix changes one comparison and an exception message. Existing correctness-observer snapshot buffers, command buffers, and async readbacks are unchanged and remain excluded from performance authority.

### Lifecycle

- Opt-in bootstrap behavior is unchanged.
- Static `_installed` lifecycle is unchanged.
- Observer-owned snapshot buffers are released in existing submit-failure, async-callback `finally`, and `OnDisable` paths.
- The failed-state guard cannot release a Production-owned buffer or Tensor.
- Production output state remains read-only and is not consumed by the observer.

## [PROTECTED_SHA256]

The following before values were recorded immediately before editing and were identical after compile/build/Runtime:

| Protected file | Before / after SHA256 |
|---|---|
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiCommonTensorBackendStageIsolationV44_55_20.cs` | `1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3` |
| `Assets/KiwiAvatarSystem/Runtime/Tracking/KiwiInferenceFaceTracker.cs` | `EFFCCCF5EE1BF407F065BFA95B491390AC96A55E9A75290A67C5AFAD32AFC1F1` |
| `Assets/Script/KiwiInferenceFaceTracker.cs` | `52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695` |
| `Assets/Script/FaceLandmarkerRunner.cs` | `6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93` |
| `Assets/Script/KiwiFaceMotion.cs` | `D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6` |
| `Assets/KiwiAvatarSystem/Runtime/Camera/KiwiNativeCameraInterop.cs` | `AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5` |
| `Assets/Plugins/x86_64/KiwiNativeCamera.dll` | `82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5` |
| `Native/KiwiNativeCamera/Source/KiwiNativeCameraPlugin.cpp` | `636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A` |
| `Native/KiwiNativeCamera/Build/x86_64/KiwiNativeCamera.dll` | `8091265D91993B0082BFC63D55FFF8A18CC18AACA9B1F907E874E7011D8017E2` |
| `KiwiOutputSnapshotCopyV44_55_23.compute` | `4552D8A2CB4E47465D1BD765CD38D85F7B839A61B73555CD40557937D803CB71` |

## [RUNTIME_VALIDATION]

Environment used by the supplied v44.55.23 runner:

- `KIWI_V44_55_20_COMMON_TENSOR_AUDIT=1`
- `KIWI_V44_55_21_CROP_SAMPLER_AUDIT` absent/OFF
- `KIWI_V44_55_22_REFERENCE_TRANSACTION_AUDIT` absent/OFF
- `KIWI_V44_55_23_PRODUCTION_OUTPUT_AUTHORITY_AUDIT=1`
- Performance authority: 0

Dependency Runtime:

- v44.55.20 status: `COMPLETE`
- v44.55.20 decision: `PREPROCESSING_CONFIRMED`
- v44.55.20 observer fault: 0
- v44.55.20 source identity mismatch: 0
- v44.55.20 reference input readback failure: 0
- v44.55.20 reference tensor non-finite: 0

v44.55.23 required gate:

| Gate | Observed | Result |
|---|---:|---|
| dependency status | `COMPLETE` | PASS |
| `completedPairCount >= 60` | 115 | PASS |
| `observerFaultCount` | 0 | PASS |
| `attachMissCount` | 0 | PASS |
| `recordCaptureWindowMissCount` | 0 | PASS |
| `productionOutputSnapshotFailureCount` | 0 | PASS |
| `referenceOutputSnapshotFailureCount` | 0 | PASS |
| `mode2OutputSnapshotFailureCount` | 0 | PASS |
| `nonFiniteOutputCount` | 0 | PASS |
| `inputArrayIdentityMismatchCount` | 0 | PASS |
| `outputShapeMismatchCount` | 0 | PASS |
| `capturesPending` | 0 | PASS |
| completed vs dependency records | 115 == 115 | PASS |

CSV integrity:

- Rows: 115.
- Unique indices: 115.
- Index range: 0 through 114, contiguous.
- `NORMAL_NO_GE4`: 107.
- `ANOMALOUS_PRODUCTION_EQUALS_REFERENCE`: 5.
- `ANOMALOUS_NEITHER_EXACT`: 3.
- `V20_OUTPUT_SNAPSHOT_SUBMIT_FAIL` in same-run Player log: 0.
- `Snapshot source buffer` mismatch in same-run Player log: 0.
- v44.55.23 `OBSERVER_FAULT` in same-run Player log: 0.

Runtime decision under the unchanged rule:

`MIXED_TRANSACTION`

This result is valid because all observer/completeness gates passed. It does not authorize a Production CPU backend implementation or any Production/Tracking/Native change.

## [RUNTIME_EVIDENCE_SHA256]

| Evidence | SHA256 |
|---|---|
| `KiwiProductionOutputTransactionAuthority_v44_55_23_20260902_212439.txt` | `160717468E082E06DA4A053A57299AC857A130D3AF16F0A63DB4E21F722D0EF3` |
| `KiwiProductionOutputTransactionAuthority_v44_55_23_20260902_212439.csv` | `69150942D9505C951B2CB063507E6A99808AB7235B8324F4AE023B09C52FAA9A` |
| `KiwiCommonTensorBackendStageIsolation_v44_55_20_20260902_212438.txt` | `51E60CAB21ED6B7A713306F2B0434520A2E3F16EBE3DDA25FD3945CF388A22FC` |
| `KiwiCommonTensorBackendStageIsolation_v44_55_20_20260902_212438.csv` | `E81CE955CA206C2D635E2A373C4A71AB813C0D82A9B201B59FAA59443988D6A2` |
| Same-run `Player.log` after Player exit | `D51E4AF206CB8CC211318E48E3B3B0B0AA0B703D30D23ECC3A4A16254C7F8959` |
| `KiwiBuild_v44_55_23.log` | `B203963EEF3B3C11BB0572A9BDF24AE2FF73CB783C563D107C2075E6F543F663` |
| `KiwiPreflight_v44_55_23.json` | `66FB26416FD68E3582CAE563907074E200D6EECFEC1C931C0BCD34CA4B8AAFC3` |

Runtime output files remain in:

`C:\Users\main\AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem\KiwiFrameBottleneck`

They were not copied into Git staging and were not committed.

## [CURRENT_ARTIFACT_SHA256]

| File | SHA256 |
|---|---|
| `KiwiProductionOutputTransactionAuthorityV44_55_23.cs` | `AE2CD168171335E20F6793C5F46A12264D86CCAE21DF62FAFF74FCBA1E92E4A7` |
| Observer `.cs.meta` | `54682EFE97C234D9089950038AA8A08BF4428EE4F255B55B0AFAB6A273A1FA90` |
| `KiwiOutputSnapshotCopyV44_55_23.compute` | `4552D8A2CB4E47465D1BD765CD38D85F7B839A61B73555CD40557937D803CB71` |
| Compute `.meta` | `CFEBC08D010BF8A9ED8404A59683521E88D0C3C357F52F14849C7B0F95CB2208` |
| `Build-KiwiV44_55_23.ps1` | `5A47AC835ABFE72F1371928DE6F5AACA739A26E22EBB888E39B44341ED272B7D` |
| `Run-KiwiV44_55_23.ps1` | `CEE072BEDF0354A328BC9C56D69266E316A6477BE1463035EE6509C2986C9CE4` |
| `Tools/KiwiV44_55_23/README.txt` | `144A6A85C83767B155A0AA22316A206CB20B402B4E0D1D06CEA855E385177C7F` |

## [REGRESSION_RISK]

### Confirmed bounded risks

- A backing buffer smaller than 1405 or with non-float stride remains `INVALID_OBSERVER`.
- A logical tensor shape other than 1405 remains `INVALID_OBSERVER`, even if its backing capacity is large enough.
- Larger backing capacity is not copied beyond logical index 1404.
- Failed-pair retry suppression cannot convert a failure into success because the failed capture remains subject to the existing completeness/fault decision.

### Open risks

- The failed-state retry branch was statically proven and the original failure condition disappeared in Runtime, so this successful run did not dynamically inject a deliberate post-fix snapshot failure. No fault injection was added because it would broaden the observer and contaminate the requested Production-like run.
- Runtime correctness observers perform existing GPU snapshots and async readbacks; their performance values remain non-authoritative.
- The valid `MIXED_TRANSACTION` result indicates that Production output authority is not explained by a single REF-versus-MODE2 bitwise choice across all reproduced anomalies. This report does not propose or implement a Production backend change.

## [FINAL_BOUNDARY]

- Production CPU backend implementation: not performed.
- Native rebuild/install: not performed.
- Production install: not performed.
- Commit: not performed.
- Push: not performed.
- Production/Native/Tracking/ROI/threshold changes: 0.
