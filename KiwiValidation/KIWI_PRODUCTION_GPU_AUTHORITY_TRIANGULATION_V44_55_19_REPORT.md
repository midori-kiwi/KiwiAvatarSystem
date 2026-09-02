# KiwiAvatarSystem Production GPU Authority Triangulation Audit v44.55.19

Date: 2026-09-02 (Asia/Tokyo)  
Repository: `D:\KiwiAvatarSystem`  
Branch / audited HEAD: `main` / `5e083e2801ae9ccedea3bcc2645e02d5bd4c5d39`  
Scope: observer-only correctness audit. Production CPU backend implementation, Native rebuild/install, commit and push were not performed.

## [EXECUTIVE_VERDICT]

**BOTH_REJECTED**

The fail-closed 120-second, 1 Hz run completed with 112 exact-identity pairs. The independent Production-compatible GPU reference was valid for the run: `observerFaultCount=0`, `sourceIdentityMismatchCount=0`, `referenceFreezeFailureCount=0`, `referenceInputReadbackFailureCount=0`, and all 112 records retained the matched Production lane identity.

`PRESENTATION_PLUS_FINAL_UNORM8 mode2` was materially closer to the GPU reference than `CURRENT_FLOAT`, but it still failed the pre-registered v44.55.17 canonical gates for presence, canonical points, and rotation. `CURRENT_FLOAT` additionally produced two categorical decode/acceptance/canonical-validity contradictions against the GPU reference. Therefore neither candidate is accepted for a Production CPU preprocessing path.

Production remains unchanged. There is no evidence from this run that Tracking, ROI, FaceTexture, Root/Head, camera cadence, Native camera source, or the current GPU authority should be modified.

## [EXTERNAL_RESEARCH]

### Confirmed public design facts

- MediaPipe's official `ImageToTensorCalculator` derives an ROI transform, makes tensor dimensions/range explicit, and selects distinct CPU or GPU converter implementations according to the input storage. This supports treating ROI, origin, sampling, range/normalization, and backend as separate comparison stages rather than assuming “same image” implies identical tensors. [Google MediaPipe source](https://github.com/google-ai-edge/mediapipe/blob/master/mediapipe/calculators/tensor/image_to_tensor_calculator.cc)
- Google's official GPU graph examples explicitly declare tensor size, float range, border mode, and `gpu_origin: TOP_LEFT`. Orientation and normalization are therefore part of the preprocessing contract, not presentation details. [Google MediaPipe GPU graph](https://github.com/google-ai-edge/mediapipe/issues/2666)
- Unity's official Sentis examples use `TextureConverter.ToTensor`, a `TextureTransform`, and a `GPUCompute` worker as distinct steps. This matches the repository's separation between the crop texture, texture-to-tensor transform, and model backend. [Unity Technologies Sentis example](https://github.com/Unity-Technologies/sentis-edge-detect)

### Inference, not public fact

- The sparse large input outliers shared by both CPU candidates are more consistent with a residual sampling/border/subtexel-stage difference than with a global channel permutation or full-frame Y inversion. This is an inference from the present tensor statistics; no spatial diff map was captured, so it is not yet a confirmed root cause.
- A prior local WMC static audit described MediaPipe Tasks 0.10.35, NHWC `FLOAT32`, and a likely CPU/XNNPACK execution path. The exact requested `WMC_Full_Audit.md` was not present in the audited workspace, and a commercial executable's active delegate cannot be proven from static packaging alone. WMC is therefore a comparison principle here, not Production authority.

## [PRODUCTION_AUTHORITY]

Authority order used: current installed Production files and hashes, latest correlated runtime, current repository code, then historical observer source. Missing historical evidence was not converted into PASS.

Current Production preprocessing was re-read from `Assets/Script/KiwiInferenceFaceTracker.cs`:

- lane crop: 192x192 `RenderTextureFormat.ARGB32`, Linear, Bilinear, Clamp, no mip chain;
- crop sampling: Production `Graphics.Blit` with `_Xform = BuildFlipMatrix * cropMatrix`;
- tensor: shape `1x3x192x192`, `TextureTransform` = `NCHW` + `TopLeft`;
- authority worker: `BackendType.GPUCompute`;
- runtime path: DX12 async-compute command-buffer `ToTensor` + `ScheduleWorker`;
- decode: raw `pendingCropMatrix` and exact `pendingMinimumPresence` retained separately from the sampling matrix.

The audit reference duplicates those observable contracts without reading or writing the Production worker result. It freezes the matched lane crop into an observer-owned ARGB32 Linear texture, runs the same local Unity Inference Engine 2.4.1 `TextureConverter` transform, and schedules the same model on an independent `GPUCompute` worker.

Model authority:

- `Assets/KiwiAvatarSystem/Resources/KiwiFaceLandmarkInference.onnx`
- SHA256 `ED487104519B0A88CB2CB2EC3678E183F447FB9DD63560998E960FFBAA8FB335`

Authority artifacts actually found and read:

- v44.55.17 runtime report (historical filename retains `v44_55_16`): `C:\Users\main\AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem\KiwiFrameBottleneck\KiwiExactHostTicksCanonicalSemantic_v44_55_16_20260901_215958.txt`
- SHA256 `FDE843C4EDD39532ED3400B5BEFA750DBC6EBEC959F8028DFEB5E4022EF181CB`
- Native provenance report: `KiwiValidation/KiwiCodexConsolidatedReport_v44_55_12_OriginalPackageAudit.txt`
- SHA256 `BB31596006C739778C3D0BB5925A155B5313653680027269620B2129232E1C0A`
- v44.55.8 historical observer source: `KiwiBackups/v44_55_9_20260830_232447/KiwiProductionUnorm8QuantizationParityAuditV44_55_8.cs`
- v44.54 historical observer source: `KiwiBackups/v44_55_20260830_195518/KiwiMatchedBackendEquivalenceAuditV44_54.cs`

Requested evidence not found under the repository or the correlated LocalLow evidence directory:

- `KiwiUnorm8Parity_v44_55_8_20260830_230036.txt`
- `KiwiBackendEquivalence_v44_54_20260830_192801.txt`
- `WMC_Full_Audit.md`

The v44.55.8 and v44.54 source files establish the intended observer designs, not their runtime outcomes. In particular, the missing v44.54 TXT means backend equivalence is not reclassified as cryptographically closed evidence in this report.

Production critical SHA256 before and after build/runtime:

| File | Expected | Before / after | Result |
|---|---|---|---|
| `Assets/Script/KiwiInferenceFaceTracker.cs` | `52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695` | same | unchanged |
| `Assets/Script/FaceLandmarkerRunner.cs` | `6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93` | same | unchanged |
| `Assets/KiwiAvatarSystem/Runtime/Camera/KiwiNativeCameraInterop.cs` | `AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5` | same | unchanged |
| `Assets/Plugins/x86_64/KiwiNativeCamera.dll` | `82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5` | same | unchanged |
| `Native/KiwiNativeCamera/Source/KiwiNativeCameraPlugin.cpp` | `636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A` | same | unchanged |
| `Assets/Script/KiwiFaceMotion.cs` | `D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6` | same | unchanged |
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiFrameComparisonOverlay.cs` | `6DF8CCC9B47A0E2E9BFF4807B4D7CAB4AB11911992A4624EF7A8E40436F39EDD` | same | unchanged |

## [V44_55_17_FAILURE_RECAP]

The correlated v44.55.17 report was read directly. It compared A and B on CPU with exact sequence/hostTicks identity and reported:

- `pairCompleted=69`, `canonicalComparable=47`;
- hard/source/decode/acceptance/canonical-validity/non-finite mismatch = 0;
- FAIL: presence P95 `0.021542326`, max `0.037390903`;
- FAIL: canonical point P95 `1.519886977 px`, max `3.047972122 px`;
- FAIL: rotation P95 `0.548794061°`, max `0.832742035°`;
- PASS: scale, geometry quality, expression.

The present audit did not relax or retune those gates. It changed the question from A-vs-B to each candidate-vs-independent Production-compatible GPU authority.

## [REFERENCE_PIPELINE]

Reference sequence for every sample:

1. Arm the existing Native diagnostic exact snapshot.
2. Obtain exact native `sequence` and raw native `hostTicks`; map to the managed host-tick domain.
3. Find the pending Production lane whose `pendingSourceHostTicks` equals that mapped timestamp and whose schedule generation is newer than the prior capture.
4. Read only that lane's crop texture, `_Xform`, raw `pendingCropMatrix`, `pendingMinimumPresence`, source width and source height.
5. Queue `CopyTexture` from the matched Production 192x192 crop into an observer-owned ARGB32 Linear texture before starting either Native CPU candidate.
6. Apply `TextureConverter` with `NCHW` and `TopLeft` to an observer-owned tensor.
7. Schedule the same model on an independent `GPUCompute` worker.
8. Use asynchronous output/tensor readback; no blocking GPU wait.
9. Invoke Production `DecodeReadableOutput` read-only, then apply the same stateless canonical formulas and expression extraction used by the historical canonical gate.

Fail-closed checks were satisfied:

- `observerFaultCount=0`
- `sourceIdentityMismatchCount=0`
- `referenceFreezeFailureCount=0`
- `referenceInputReadbackFailureCount=0`
- `decodeReflectionFailureCount=0`
- `expressionReflectionFailureCount=0`
- `laneChangedAfterCaptureCount=0`
- all 112 CSV sequences unique
- all 112 sources `1920x1080`
- all 112 `laneStillMatchedAfterCapture=1`

The reference is independent: it does not obtain the Production worker output after the fact and never schedules, replaces, or mutates a Production worker/tensor.

## [THREE_WAY_METHOD]

For the same native sequence, raw native hostTicks, Production sampling matrix, decode matrix, minimum presence, model, and canonical formulas:

- REFERENCE: frozen matched Production crop -> `TextureConverter NCHW/TopLeft` -> shadow `GPUCompute` worker.
- CANDIDATE A: `CURRENT_FLOAT` exact-hostTicks Native diagnostic crop, mode 0 -> shadow CPU worker.
- CANDIDATE B: `PRESENTATION_PLUS_FINAL_UNORM8` exact-hostTicks Native diagnostic crop, mode 2 -> shadow CPU worker.

Recorded comparisons:

- REFERENCE vs CURRENT_FLOAT;
- REFERENCE vs MODE2;
- CURRENT_FLOAT vs MODE2 (diagnostic only).

Recorded semantics include raw/decoded presence, decode status, accepted/rejected, canonical validity, canonical points, quaternion angular difference, scale, geometry quality, expression, and raw 478 landmarks as diagnostic-only data. Input tensor parity never grants semantic PASS.

Runtime configuration and completeness:

- Unity `6000.0.80f1`, Direct3D 12, RTX 4090;
- camera `UGREEN Camera 4K`, `1920x1080@60`;
- duration `120 s`, sample `1 Hz`, stable gate `8 s`, warmup pairs `2`;
- attempts `114`, completed `112`;
- REFERENCE-vs-A canonical comparable `74`;
- REFERENCE-vs-B canonical comparable `75`;
- minimums `60` completed and `40` comparable: PASS.

## [INPUT_PARITY]

Input values are reported in 8-bit LSB-equivalent units (`abs(float delta) * 255`). P95/P99 are histogram approximations at 1/16 LSB resolution.

| Pair | Mean abs LSB | RMSE LSB | Signed R/G/B bias LSB | P95 / P99 approx | Max LSB | Exact ratio |
|---|---:|---:|---|---:|---:|---:|
| REF vs CURRENT_FLOAT | 0.330453 | 0.543477 | +0.036166 / +0.049017 / +0.077316 | 0.8125 / 1.0000 | 53.930537 | 1.2028% |
| REF vs MODE2 | 0.075513 | 0.453379 | +0.025293 / +0.056207 / +0.070665 | 1.0000 / 1.0000 | 54.000001 | 93.8089% |
| CURRENT_FLOAT vs MODE2 | 0.308766 | 0.378648 | -0.010873 / +0.007190 / -0.006652 | 0.7500 / 0.8750 | 0.997598 | 1.2135% |

Confirmed interpretation:

- Mode2 reduces mean absolute input error against REF by approximately **77.2%** relative to CURRENT_FLOAT.
- Mode2's exact-match ratio is **93.81%**, versus **1.20%** for CURRENT_FLOAT.
- Mode2 is therefore the closer input representation, but its sparse non-exact tail remains and semantic gates decide candidacy.

Stage classification:

- presentation/final UNORM8: strongly supported as a material improvement, but insufficient for acceptance;
- orientation/Y and channel/layout: not supported as the primary error because mode2 is exact for 93.81% of elements and no mode2 categorical semantic mismatch occurred;
- sampling/border/subtexel precision: OPEN and the leading residual-stage hypothesis due to sparse large outliers shared against REF;
- normalization: no evidence of a gross range error; small signed channel bias remains;
- backend: not closed by this run because REF uses GPUCompute and candidates use CPU. The requested v44.54 runtime receipt was absent, so the historical backend result cannot be elevated beyond its source-design evidence.

## [CANONICAL_SEMANTIC_RESULTS]

Fixed-gate results against REFERENCE:

| Metric | Gate P95 / max | CURRENT_FLOAT | Result | MODE2 | Result |
|---|---:|---:|---|---:|---|
| Presence abs diff | 0.010 / 0.030 | 0.034980 / 0.094228 | FAIL | 0.019207 / 0.050366 | FAIL |
| Canonical point diff | 1.0 / 2.0 px | 3.535351 / 10.532312 px | FAIL | 1.910662 / 5.164390 px | FAIL |
| Quaternion angle | 0.10° / 0.25° | 1.345084° / 3.070882° | FAIL | 0.630501° / 1.533366° | FAIL |
| Scale relative diff | 0.005 / 0.010 | 0.004906 / 0.011049 | FAIL (max) | 0.003604 / 0.006979 | PASS |
| Geometry quality abs diff | 0.010 / 0.025 | 0.002754 / 0.003601 | PASS | 0.001646 / 0.002806 | PASS |
| Expression abs diff | 0.020 / 0.050 | 0.014669 / 0.047503 | PASS | 0.007476 / 0.025155 | PASS |

Categorical precedence:

- CURRENT_FLOAT: `HARD_FAIL`; decode mismatch 2, acceptance mismatch 2, canonical-validity mismatch 2, non-finite 0.
- MODE2: no hard invariant violation, but `AGGREGATE_FAIL` on the fixed continuous gates.

Mode2 materially improves the failed continuous metrics relative to CURRENT_FLOAT: approximately 45.1% lower presence P95, 46.0% lower canonical-point P95, and 53.1% lower rotation P95. These improvements are not permission to relax the gate.

## [PAIR_OUTLIER_ANALYSIS]

Hard contradictions occurred only for CURRENT_FLOAT:

- index 79 / sequence 5680: REF=`PresenceLow`, CURRENT_FLOAT=`Valid`; accepted and canonical-validity differed. MODE2 matched REF=`PresenceLow`.
- index 97 / sequence 6906: REF=`Valid`, CURRENT_FLOAT=`PresenceLow`; accepted and canonical-validity differed. MODE2 matched REF=`Valid`.

Largest continuous outliers:

- presence: sequence 4633, REF-vs-A `0.094228`, REF-vs-B `0.050366`; all paths remained `PresenceLow`, so this is not a categorical contradiction;
- canonical points CURRENT_FLOAT: sequence 7704, `10.532312 px`; the same pair had the largest A rotation difference, `3.070882°`;
- canonical points MODE2: sequence 6170, `5.164390 px`; the same pair had the largest B rotation difference, `1.533366°`.

The outliers are not explained by duplicated records, source-size drift, or lane replacement: all sequences were unique, all source sizes were 1920x1080, and lane identity remained stable for all completed records.

## [DECISION]

**BOTH_REJECTED**

Decision precedence was fixed before runtime: `INVALID_OBSERVER > HARD_FAIL > INSUFFICIENT_DATA > AGGREGATE_GATE`.

- The run is not `INVALID_OBSERVER`: all identity/reference/reflection gates were clean.
- The run is not insufficient: 112 completed pairs, 74/75 canonical-comparable pairs.
- CURRENT_FLOAT is rejected by categorical hard failure before aggregate evaluation.
- MODE2 is rejected by fixed presence, canonical-point, and rotation gates.

Mode2 is the closer of the two rejected candidates. “Closer” is not “selected.” No Production CPU backend work is authorized by this outcome.

## [PERFORMANCE_EVIDENCE_BOUNDARY]

This was a correctness observer run and is explicitly invalid as formal performance evidence.

Observer-only costs include one private 192x192 ARGB32 Linear RenderTexture, one independent GPUCompute worker, two CPU workers, one `CopyTexture` per sampled pair, asynchronous GPU tensor/output readback, CPU candidate crops, reflection decode, CSV accumulation, and diagnostic logging. No blocking GPU wait was added. Production cadence/latency values observed during this run must not be used for a Production performance comparison.

## [REGRESSION_RISK]

- Production behavior risk is low while the environment variable is unset: bootstrap returns before creating any object. The observer is opt-in via `KIWI_V44_55_19_GPU_AUTHORITY_AUDIT=1`.
- When enabled, the observer deliberately adds GPU/CPU work and allocations and can perturb cadence. This is contained by the performance-evidence boundary, not claimed absent.
- Reflection depends on private Production field/method names. Any mismatch fails closed as `INVALID_OBSERVER`; it does not silently substitute a different authority.
- The reference uses the same TextureConverter/transform/model/backend contract but schedules its own independent GPU work. Queue placement is Production-compatible, not a byte-for-byte capture of the Production command buffer.
- A single 120-second camera run met sample gates but does not cover every camera, lighting condition, ROI trajectory, or threshold neighborhood.
- The missing v44.54 and v44.55.8 runtime TXT receipts limit historical provenance. Their observer sources cannot prove past runtime outcomes.

Static validation:

- observer-only source added; Production files modified: 0;
- Production writes: 0; Production worker schedules/writes: 0;
- Native DLL/source rebuild or replacement: 0;
- tracking math/threshold/ROI/FaceTexture/Root/Head changes: 0;
- `git diff --check`: PASS (ambient `.gitignore` line-ending warning only, no whitespace error);
- Unity 6000.0.80f1 whole `Assembly-CSharp` compile: PASS after correcting one observer-local report-generation syntax error;
- Tundra script compile: success;
- DX12 Development Player build with existing full release preflight: success, 0 errors;
- ProjectSettings SHA before/after build unchanged;
- source/meta SHA256:
  - `KiwiProductionGpuAuthorityTriangulationV44_55_19.cs`: `F18106E4EFA0E5B460AAC2EDBAECD70817836C29037986361EF7AAA515D42434`
  - `.meta`: `8069DD89111109778D9A0DD02172A8A3EB46CB73EB0D88AE4C0F842EC9098442`

Runtime evidence:

- TXT: `C:\Users\main\AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem\KiwiFrameBottleneck\KiwiProductionGpuAuthorityTriangulation_v44_55_19_20260902_123507.txt`
- TXT SHA256: `AFB723CB979641A950A67174C1D00D42981DB394CF350BB8B0D0C3A421C0081A`
- CSV: `C:\Users\main\AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem\KiwiFrameBottleneck\KiwiProductionGpuAuthorityTriangulation_v44_55_19_20260902_123507.csv`
- CSV SHA256: `ACCCB35C87DC932AD116F8D97A343EACB1422506DCF3F451F92E6739636E5CC7`
- runtime log SHA256: `5376C47992EA7F4F3067A9253C95BA9430D4252FDFD6A9B39DC31EF7E7F1C641`
- build log SHA256: `CEDA5031AF8BA31C099EA016248D206F83174F28F33E5F297D382E7CF182706B`
- successful compile log SHA256: `6547489A03D6BC549AD1BB6697256F7546F9CE0D749871255CECBC1F6F94D85E`

Ambient user-owned state was preserved: `.gitignore`, the two existing LIVE CAMERA reports, and `Tools/KlakSpout_v206_diag/` were not edited or included in this audit.

## [NEXT_BEST_ACTION]

Perform one observer-only, common-backend stage-isolation run: feed the exact REF tensor, CURRENT_FLOAT tensor, and mode2 tensor through separate **GPUCompute** shadow workers using the same model, while recording the spatial coordinates of input outliers.

This single next step is preferred because it cleanly answers the remaining fork:

- if A/B still fail against REF on the common GPU backend, the residual is preprocessing—prioritize sampling/border/subtexel precision;
- if a candidate passes on GPU but fails on CPU, re-open backend equivalence before any Production CPU design.

Do not implement a Production CPU backend, rebuild Native code, or relax thresholds before that boundary is closed.
