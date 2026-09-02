# KiwiAvatarSystem v44.55.20 Common-Tensor Backend + Spatial Outlier Stage Isolation Audit

Date: 2026-09-02 (Asia/Tokyo)

Workspace: `D:\KiwiAvatarSystem`

Git branch / HEAD at audit: `main` / `5e083e2801ae9ccedea3bcc2645e02d5bd4c5d39`

Scope: observer-only correctness audit; no Production backend, Native, tracking, ROI, FaceTexture, Root/Head, or threshold change

## [EXECUTIVE_VERDICT]

**Final decision (exactly one): `PREPROCESSING_CONFIRMED`.**

The common-tensor factorial closed the backend ambiguity left by v44.55.19:

- `REF_GPU vs REF_CPU`: PASS;
- `A_GPU vs A_CPU`: PASS;
- `B_GPU vs B_CPU`: PASS;
- `REF_GPU vs A_GPU`: HARD_FAIL;
- `REF_GPU vs B_GPU`: AGGREGATE_FAIL;
- the corresponding CPU cross-tensor comparisons fail in the same way.

Therefore the fixed `FLOAT32 / NCHW / 1x3x192x192` values remain semantically equivalent across Unity Inference Engine `GPUCompute` and `CPU`; the failing stage is before backend inference, in the preprocessing/sampling contract. There is no evidence in this run to implement a Production CPU backend or to modify Native Camera, Tracking, ROI, FaceTexture, Root/Head, or the registered semantic thresholds.

The spatial phenotype is lower-side/edge concentrated, not channel-specific or uniformly biased. That confirms the stage, but does **not** by itself prove whether the remaining implementation defect is border extrapolation, source-coordinate/pixel-center convention, or their interaction. The strongest next direction is stated as one bounded action in `[NEXT_BEST_ACTION]`.

## [EXTERNAL_RESEARCH]

### Confirmed public implementation facts

Google MediaPipe's official `ImageToTensorCalculator` source makes the preprocessing contract explicit and separate from inference:

- it derives a rotated ROI with `GetRoi`;
- computes output dimensions and optional padding with `PadRoi`;
- emits the ROI-to-image transform through `GetRotatedSubRectToRectTransformMatrix`;
- selects a CPU or GPU converter;
- passes the output range and `BorderMode` into conversion;
- derives whether the GPU input starts at the bottom from `gpu_origin`.

Source: [MediaPipe `image_to_tensor_calculator.cc`](https://github.com/google-ai-edge/mediapipe/blob/master/mediapipe/calculators/tensor/image_to_tensor_calculator.cc).

The official GL texture converter uses `GL_LINEAR` minification/magnification and treats replicate and zero borders differently (`GL_CLAMP_TO_EDGE` versus zero-border handling). It applies the ROI transform in texture coordinates and flips Y when the GPU origin requires it. Source: [MediaPipe `image_to_tensor_converter_gl_texture.cc`](https://github.com/google-ai-edge/mediapipe/blob/master/mediapipe/calculators/tensor/image_to_tensor_converter_gl_texture.cc).

The official OpenCV converter maps `BorderMode::kReplicate` to `cv::BORDER_REPLICATE`, `BorderMode::kZero` to `cv::BORDER_CONSTANT`, and applies the ROI with `cv::warpPerspective`; its public factory defaults to `cv::INTER_LINEAR`. Sources: [MediaPipe OpenCV implementation](https://github.com/google-ai-edge/mediapipe/blob/master/mediapipe/calculators/tensor/image_to_tensor_converter_opencv.cc) and [factory declaration](https://github.com/google-ai-edge/mediapipe/blob/master/mediapipe/calculators/tensor/image_to_tensor_converter_opencv.h).

### Confirmed local Unity Inference Engine 2.4.1 facts

The Production-resolved package is `Library\PackageCache\com.unity.ai.inference@587873fd5e1b`, version `2.4.1` from `Packages\manifest.json`.

| Boundary | Local source evidence | SHA256 |
|---|---|---|
| Texture to tensor | `Runtime/Core/Converters/TextureConverter.cs`: exact dimensions choose `TextureToTensorExact`; size mismatch chooses `TextureToTensorLinear`; command-buffer overload pins the output tensor | `D3905D3D29B70073456FF63F51C54C7B8023A259715504AD14AA436F392DF146` |
| Tensor layout/origin | `Runtime/Core/Converters/TextureTransform.cs`: NCHW maps axes `0,1,2,3`; `SetCoordOrigin` owns origin selection | `C12F6A3B739ACC39588722EABE4AF0E4A125E262C14BCA714F57E1E8652B9D7D` |
| Frozen value upload/readback | `Runtime/Core/TensorGeneric.cs`: typed `Upload(T[])` and async `ReadbackAndCloneAsync()` | `82C1DEC0B0978AC250C54991942E3A80527F4778B6B30E52A3E090F62320A81A` |
| Backend selection/schedule | `Runtime/Core/Backends/Worker.cs`: explicit backend type and `Schedule(Tensor)` | `011BC050A5CA80BBD313E7B4EE679D142B4740181D7F2380DD142C8910752256` |
| GPU storage | `Runtime/Core/Backends/GPUCompute/ComputeTensorData.cs`: `Pin` plus upload through compute-buffer `SetData` | `38523335457507326E91FA5E40C332E99A18C7BFF556336F3016627788AA02D9` |

### Public design principle

ROI transform, coordinate origin, interpolation, border mode, value range/layout, and inference backend are independent contracts. Freezing the final float tensor before dispatching the same values to CPU and GPU is therefore the correct stage-isolation method.

### Similar-product boundary

The prior local WMC static audit indicates a MediaPipe Tasks / CPU-XNNPACK-style comparison path, but a closed commercial executable does not expose its exact active pixel-center, border, and delegate contracts. WMC is a design comparison only; it is not evidence for Kiwi's numerical result. No undocumented WMC behavior was assumed.

### Inference, not public fact

MediaPipe's explicit treatment of origin, interpolation, ROI matrix, and border mode explains why sparse edge-sensitive disagreement can arise before inference. It does not prove which specific Kiwi candidate formula is wrong; only the v44.55.20 Runtime establishes the Kiwi stage result.

## [PRODUCTION_AUTHORITY]

Authority order used: current Production files/hashes -> current build and Runtime evidence -> GitHub/public source -> earlier reports.

Model authority:

- `Assets/KiwiAvatarSystem/Resources/KiwiFaceLandmarkInference.onnx`: `ED487104519B0A88CB2CB2EC3678E183F447FB9DD63560998E960FFBAA8FB335`;
- `Assets/StreamingAssets/KiwiFaceLandmarkInference.onnx`: same SHA256;
- all observer lanes load the same `KiwiFaceLandmarkInference` model and use the same read-only Production decode.

Production critical SHA256 before and after compile/build/Runtime:

| Critical file | Required SHA256 | After | Result |
|---|---|---|---|
| `Assets/Script/KiwiInferenceFaceTracker.cs` | `52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695` | same | unchanged |
| `Assets/Script/FaceLandmarkerRunner.cs` | `6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93` | same | unchanged |
| `Assets/KiwiAvatarSystem/Runtime/Camera/KiwiNativeCameraInterop.cs` | `AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5` | same | unchanged |
| `Assets/Plugins/x86_64/KiwiNativeCamera.dll` | `82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5` | same | unchanged |
| `Native/KiwiNativeCamera/Source/KiwiNativeCameraPlugin.cpp` | `636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A` | same | unchanged |
| `Assets/Script/KiwiFaceMotion.cs` | `D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6` | same | unchanged |
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiFrameComparisonOverlay.cs` | `6DF8CCC9B47A0E2E9BFF4807B4D7CAB4AB11911992A4624EF7A8E40436F39EDD` | same | unchanged |

Build-related settings also remained byte-identical:

- `ProjectSettings/ProjectSettings.asset`: `88CB3F68F275E1A7DCBF1FEA8C59D123F50743C6F93B0F861E1FD653865EC6AA`;
- `ProjectSettings/QualitySettings.asset`: `944B52D523BCB15B945BC80924B9289F59185AB19BCE8A00E99EEC345BDE6440`;
- `ProjectSettings/GraphicsSettings.asset`: `FBF0856B7693639A5388AE693A455BAAAD354C1D8DC548601DE3DC61C4AB12C3`.

Only the following audit source/assets were added by this task:

- `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiCommonTensorBackendStageIsolationV44_55_20.cs` — before: absent; after SHA256 `1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3`;
- `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiCommonTensorBackendStageIsolationV44_55_20.cs.meta` — before: absent; after SHA256 `1CB3B38423E9CA1DABBC2B4FE1E518D38B0834B200CE7EF7AE6A640CB8D816F9`.

The observer is opt-in only through `KIWI_V44_55_20_COMMON_TENSOR_AUDIT=1`. No install, Native rebuild, commit, or push was performed. The ambient modified `.gitignore`, v44.55.19 untracked assets/report, LIVE CAMERA reports, and `Tools/KlakSpout_v206_diag/` predated this task and were not edited or attributed to it.

Compile/build validation:

- Unity `6000.0.80f1` whole-project compile: PASS; Tundra `654 evaluated`; return code `0`;
- Windows x64 Development diagnostic build: PASS; mandatory preflight `passed=True`, `releaseCandidateReady=True`, error/critical `0/0`; build result `Succeeded`, errors `0`;
- build Player SHA256: `98751D0DFF0DD3ADE563C2B505A0E9F7A46E8E8895864212E1B884EDBCB42E81`;
- no `WaitForCompletion`, queue wait, blocking GPU wait, Production tensor/backend write, tracking write, or camera write was added;
- observer-owned work consists of one 192x192 ARGB32 Linear freeze RenderTexture, one command buffer, frozen arrays, seven shadow workers during the first ten measured samples and six thereafter, async tensor readbacks, and diagnostic allocations. This makes the run correctness-only.

## [V44_55_19_RECAP]

The prior report `KiwiValidation/KIWI_PRODUCTION_GPU_AUTHORITY_TRIANGULATION_V44_55_19_REPORT.md` (SHA256 `23D2A08D8F3CB89ED961B8693353FA38FE11E9155EE6AB88FD5339AA6AC90AB7`) concluded `BOTH_REJECTED` from 112 completed exact-identity pairs:

| Input comparison | Mean abs LSB | Max LSB | Exact ratio |
|---|---:|---:|---:|
| REF vs CURRENT_FLOAT | 0.330453 | 53.930537 | 1.2028% |
| REF vs mode2 | 0.075513 | 54.000001 | 93.8089% |
| CURRENT_FLOAT vs mode2 | 0.308766 | 0.997598 | 1.2135% |

CURRENT_FLOAT had two categorical contradictions; mode2 had none but failed fixed presence, canonical-point, and rotation gates. Because REF ran on GPU and candidates ran on CPU, v44.55.19 could not separate preprocessing from backend. v44.55.20 was designed specifically to close that ambiguity without changing the pre-registered gates.

The lower v44.55.20 maximum (~39 LSB instead of ~54 LSB) is not evidence of a contract change: these are different camera samples and poses. The stable authority is stage behavior and the same-run factorial result, not cross-run maximum equality.

## [OBSERVER_VALIDITY]

Final authoritative run only:

- Runtime: Unity `6000.0.80f1`, Windows Player, Direct3D 12 level 12.1, NVIDIA GeForce RTX 4090;
- camera: `UGREEN Camera 4K`, `1920x1080@60`, Native Path B system-memory NV12 diagnostic capture available;
- fixed configuration: `120 s`, `1 Hz`, stable gate `8 s`, warmup `2`;
- `GATE_MATCH`, `READY_FOR_WARMUP`, `MEASURE_START`, and `COMPLETE` are all present;
- attempts `117`, completed `115`;
- canonical-comparable: REF/A `80`, REF/B `81`; required minimums `60/40`: PASS;
- all 115 CSV rows have unique sequence values, strictly increasing sequence and native hostTicks, source `1920x1080`, and `laneStillMatchedAfterCapture=1`;
- observer fault `0`, source identity mismatch `0`, lane changed `0`, freeze failure `0`, input readback failure `0`, tensor non-finite `0`, semantic non-finite `0`;
- two exact snapshot attempts timed out as `presentedButNotScheduled`; they were not emitted as completed pairs and did not contaminate the 115-row dataset.

Two preliminary launches that never entered measurement while `trackerRegion=0` were stopped and excluded. A report-serialization adjustment made the intermediate build non-authoritative. Only `KiwiV44_55_20_Runtime_final.log` and the `20260902_163338` artifacts from the final source/build are used below.

Runtime evidence SHA256:

| Artifact | SHA256 |
|---|---|
| `KiwiValidation/KiwiV44_55_20_Runtime_final.log` | `AB20D4B7ECEA97A4A86B46D0AF2E87039AC71B4ECD2F2A44E28DDA48989F843B` |
| raw observer TXT | `04FA7DF0A7A12C0CE00E7AF151413CA1AD8A8AD733424DEE69216736A47302BA` |
| main 115-row CSV | `F025E2F03FDED0C1A25C16E6F60F02B4290AFFAB6752FBE55CD9FAC70CAE8CA8` |
| per-pair Top32 CSV | `A66C28C9888E2FF34905486D3DDD4FDD2CEDA32F103710F7992425852D38597A` |
| 192x192 heatmap CSV | `44DE4A3860EAB5A40D9A9828F1E251C092328A3FEA6685E9B532C69C56C1661F` |
| final Unity compile log | `3F03CBF662694BE1DFCBA0B5BEF765D7E250EBDBD47E0DA2C3487A0FB4271A5F` |
| final build log | `2517B062D14F58A6AE2EDAB5FB0EB1E538B3B8EE49194AAD8197FA3E1B3588CE` |

Independent PowerShell revalidation of the CSV, rather than trusting the observer summary alone, reproduced row count, identity invariants, transit count, categorical hard-mask counts, per-row P95/max bounds, Top32 cardinality (`115 x 32 x 2 = 7360`), spatial totals, and heatmap threshold sums.

## [TENSOR_FREEZE_METHOD]

For every accepted exact identity pair, the observer reads without writing Production state:

1. Arms the existing Native diagnostic snapshot and requires the same `native sequence` and exact `native hostTicks` as the Production lane.
2. Requires the matched Production lane, source dimensions, 192x192 crop texture, pending source hostTicks, `pendingCropMatrix`, `pendingMinimumPresence`, crop material, and material `_Xform` to remain coherent.
3. Copies the already-produced matched Production crop texture to one observer-owned ARGB32 Linear 192x192 RenderTexture using command-buffer `CopyTexture`.
4. Runs the Production-compatible Unity `TextureConverter` with `NCHW / TopLeft`, then asynchronously reads the tensor to `_referenceNchw`: `T_REF`.
5. Obtains `T_A` from exact-hostTicks Native diagnostic mode `CURRENT_FLOAT` and `T_B` from `PRESENTATION_PLUS_FINAL_UNORM8` mode2, each already frozen as `FLOAT32 / NCHW / 1x3x192x192`.
6. Uploads the same frozen arrays directly, without another TextureConverter or sampling pass, to `REF_GPU/REF_CPU`, `A_GPU/A_CPU`, and `B_GPU/B_CPU`.
7. Uses the same model, exact pending crop matrix, exact minimum-presence value, read-only Production `DecodeReadableOutput`, and the same stateless canonical/expression formulas.

The Production worker/result, tracking state, ROI, FaceTexture transaction, provider, temporal presentation, Root/Head, and camera are never written. Stateful neutral calibration, provider offset, dropout continuity, prediction, and final Root/Head transformation are deliberately outside this stateless correctness gate.

## [REF_TRANSIT_VALIDATION]

The first ten measured samples additionally ran:

- `REF_DIRECT_GPU`: TextureConverter tensor scheduled directly on GPUCompute;
- `REF_FROZEN_GPU`: the same tensor asynchronously read to float values, re-uploaded, and scheduled on GPUCompute.

Result: **PASS**.

- required transit samples: `10`; observed: `10`;
- canonical-comparable transit samples: `6`;
- decode/acceptance/canonical-validity mismatch: `0/0/0`;
- tensor/output non-finite: `0`;
- presence, all canonical points, rotation, scale, geometry, expression, raw 478 points, and raw Z: all recorded differences exactly `0` for comparable outputs.

Thus float readback -> frozen array -> tensor upload did not alter the observed model/decode result, and the common-tensor comparison authority is valid.

## [SPATIAL_OUTLIER_RESULTS]

All values below are `abs(candidate - REF) * 255` over `115 x 3 x 192 x 192 = 12,718,080` elements per candidate. Thresholds were fixed before Runtime.

| Threshold | REF vs A count / ratio | edge distance <=8 | REF vs B count / ratio | edge distance <=8 |
|---|---:|---:|---:|---:|
| >0 LSB | 12,586,914 / 98.9687% | 17.7500% | 682,107 / 5.36329% | 19.1728% |
| >=0.5 | 2,572,515 / 20.2272% | 17.8607% | 682,107 / 5.36329% | 19.1728% |
| >=1 | 51,769 / 0.407050% | 39.2069% | 45,304 / 0.356217% | 32.3018% |
| >=2 | 9,912 / 0.0779363% | 45.1271% | 7,134 / 0.0560934% | 43.8324% |
| >=4 | 1,988 / 0.0156313% | 44.6680% | 1,652 / 0.0129894% | 43.8862% |
| >=8 | 539 / 0.0042381% | 41.0019% | 505 / 0.0039707% | 40.9901% |
| >=16 | 139 / 0.0010929% | 41.7266% | 136 / 0.0010693% | 43.3824% |
| >=32 | 6 / 0.0000472% | 50.0000% | 6 / 0.0000472% | 50.0000% |

The geometric area fraction for output pixels at edge distance <=8 is 17.8711%. Therefore the >=4, >=8, and >=16 tails are enriched near an edge by approximately:

- A: `2.50x`, `2.29x`, `2.33x`;
- B: `2.46x`, `2.29x`, `2.43x`.

The stronger directional fact is the lower band:

- >=4 LSB in bottom 32 tensor rows: A `94.47%`, B `94.07%`;
- >=8 LSB: A `91.09%`, B `91.29%`;
- >=16 LSB: A `95.68%`, B `95.59%`.

Large-tail channels are balanced, not channel-specific:

- >=4 A RGB = `657/630/701`; B RGB = `553/530/569`;
- >=16 A RGB = `45/46/48`; B RGB = `45/45/46`.

Top observed outliers:

- A maximum `39.179637 LSB`; B maximum `38.999998 LSB`;
- all >=32 records occur in pair index `6`, sequence `1011`, at two spatial positions across all RGB channels:
  - `y=180, x=133`, edge distance `11`, positive approximately `+38..39 LSB`;
  - `y=191, x=131`, edge distance `0`, negative approximately `-32..33 LSB`.

The A and B maxima occur at the same source-pair/tensor positions, while `A vs B` never exceeds `0.997895 LSB`. This is strong evidence that the sparse large tail is shared preprocessing/sampling disagreement against REF, not mode2 quantization and not an inference backend artifact.

## [BACKEND_ONLY_RESULTS]

All three same-frozen-tensor CPU/GPU comparisons pass every categorical and fixed aggregate gate by large margins.

| Same tensor | Canonical comparable | Presence P95 / max | Point P95 / max px | Rotation P95 / max | Scale P95 / max | Geometry P95 / max | Expression P95 / max |
|---|---:|---:|---:|---:|---:|---:|---:|
| REF GPU vs CPU | 81 | 0.000001344 / 0.000002205 | 0.000131303 / 0.000263773 | 0 / 0 deg | 0.000000617 / 0.000001353 | 0 / 0.000000298 | 0.000002563 / 0.000010431 |
| A GPU vs CPU | 80 | 0.000000972 / 0.000001431 | 0.000131303 / 0.000257492 | 0 / 0 deg | 0.000000570 / 0.000001272 | 0 / 0.000000298 | 0.000002563 / 0.000011504 |
| B GPU vs CPU | 81 | 0.000000894 / 0.000001341 | 0.000128746 / 0.000262607 | 0 / 0 deg | 0.000000635 / 0.000001086 | 0 / 0.000000298 | 0.000002563 / 0.000023425 |

For every backend-only comparison: decode mismatch `0`, acceptance mismatch `0`, canonical-validity mismatch `0`, non-finite `0`, hard-invariant violation `0`.

Confirmed result: Unity Inference Engine 2.4.1 CPU/GPU backend differences for the same frozen values are many orders of magnitude below the registered semantic gates and do not explain v44.55.19's residual.

## [PREPROCESSING_ONLY_RESULTS]

GPU cross-tensor results:

| Comparison | Outcome | Canonical comparable | Presence P95 / max | Point P95 / max px | Rotation P95 / max | Scale P95 / max | Geometry P95 / max | Expression P95 / max |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| REF_GPU vs A_GPU | HARD_FAIL | 80 | 0.044330 / 0.115556 | 1.948624 / 4.967592 | 0.674443 / 1.372851 deg | 0.003966 / 0.009175 | 0.000054 / 0.003875 | 0.013094 / 0.045660 |
| REF_GPU vs B_GPU | AGGREGATE_FAIL | 81 | 0.027530 / 0.047217 | 0.896107 / 2.393775 | 0.362616 / 0.543925 deg | 0.001744 / 0.003814 | 0 / 0.000892 | 0.006870 / 0.044249 |

CPU cross-tensor results reproduce the same conclusion:

| Comparison | Outcome | Canonical comparable | Presence P95 / max | Point P95 / max px | Rotation P95 / max |
|---|---|---:|---:|---:|---:|
| REF_CPU vs A_CPU | HARD_FAIL | 80 | 0.044331 / 0.115556 | 1.948576 / 4.967547 | 0.674443 / 1.372851 deg |
| REF_CPU vs B_CPU | AGGREGATE_FAIL | 81 | 0.027531 / 0.047219 | 0.896163 / 2.393775 | 0.362616 / 0.545362 deg |

A produces one zero-tolerance categorical contradiction at index `114`, sequence `7771`: REF is `Valid`, A is `PresenceLow`, so decode, acceptance, and canonical-validity all disagree (`hardMask=7`). The same contradiction occurs on GPU and CPU. B remains categorical-consistent but fails fixed continuous gates.

Input parity remains diagnostic-only:

| Input comparison | Mean abs LSB | RMSE LSB | P95 / P99 approx | Max LSB | Exact ratio |
|---|---:|---:|---:|---:|---:|
| REF vs A | 0.315283 | 0.406808 | 0.750 / 0.938 | 39.179637 | 1.0313% |
| REF vs B | 0.056598 | 0.268792 | 1.000 / 1.000 | 38.999998 | 94.6367% |
| A vs B | 0.306359 | 0.375182 | 0.750 / 0.875 | 0.997895 | 1.0478% |

Mode2 reduces mean absolute input error against A's candidate path by approximately `82.0%` when each is compared to REF, but remains rejected by semantic gates. “Closer” is not “Production-approved.”

## [SEMANTIC_RESULTS]

Registered gates were unchanged from v44.55.17/v44.55.19:

- presence P95/max <= `0.010/0.030`;
- canonical point P95/max <= `1.0/2.0 px`;
- rotation P95/max <= `0.10/0.25 deg`;
- scale P95/max <= `0.005/0.010`;
- geometry P95/max <= `0.010/0.025`;
- expression P95/max <= `0.020/0.050`;
- decode, acceptance, canonical validity, and non-finite mismatches must all be zero.

Fail-closed precedence remained `INVALID_OBSERVER > HARD_FAIL > INSUFFICIENT_DATA > AGGREGATE_GATE`.

Gate evaluation:

- transit: PASS;
- completeness: PASS;
- REF/A/B backend-only: PASS/PASS/PASS;
- preprocessing A on GPU/CPU: HARD_FAIL/HARD_FAIL;
- preprocessing B on GPU/CPU: AGGREGATE_FAIL/AGGREGATE_FAIL;
- `MODE2_GPU_PASS` condition is false because `REF_GPU vs B_GPU` itself fails presence, point-max, and rotation gates;
- observer-validity failures: none.

The stage conclusion is robust to backend choice: GPU and CPU reproduce the cross-tensor failures at effectively the same magnitudes.

## [OUTLIER_CLASSIFICATION]

### Confirmed spatial phenotype

- `EDGE_CLUSTERED`: **strongly supported as a phenotype**, especially on the bottom side. >=4..16 LSB outliers are 2.3x..2.5x enriched at edge distance <=8, and 91%..96% are in the bottom 32 rows. Because 56%..59% of those thresholds are still farther than 8 pixels from the crop edge, “pure one-pixel border-only” is not proven.
- `SUBPIXEL_GLOBAL`: **partially supported for the small residual, not established for the sparse large tail**. A differs almost everywhere at tiny magnitude; B is exact for 94.64% of elements. High-contrast sampling displacement can create large localized deltas, but the present CSV does not directly record source-coordinate footprints.
- `CHANNEL_SPECIFIC`: **rejected as the main tail explanation**. >=4 and >=16 counts are nearly balanced across RGB, and the largest spatial events occur across all three channels at the same coordinates.
- `UNIFORM_BIAS`: **rejected as the main explanation**. Signed mean channel biases are below `0.08 LSB`, while the failing tail reaches ~39 LSB and is strongly localized.
- `RANDOM_BACKEND`: **rejected**. Same-tensor CPU/GPU comparisons pass with zero categorical mismatches and extremely small continuous differences; A/B spatial maxima also align before inference.

### Inference requiring another targeted test

The bottom-band concentration and edge enrichment make a mismatched out-of-bounds/border-extrapolation contract the leading mechanism. A source-coordinate/pixel-center mismatch remains plausible, especially where image content has a steep gradient. The current run confirms the preprocessing/sampling stage but cannot distinguish those two mechanisms without recording each tensor pixel's transformed source coordinate and whether the bilinear footprint crosses the source boundary.

## [DECISION]

**`PREPROCESSING_CONFIRMED`**

This is the only final decision for v44.55.20.

Reason:

1. Observer validity and transit validation pass.
2. All three same-tensor CPU/GPU backend comparisons pass.
3. REF vs A/B fail on GPU.
4. The same REF vs A/B failures reproduce on CPU.
5. Spatial large outliers are shared by A and B at the same coordinates, while A vs B remains below 1 LSB.

Consequences:

- `BACKEND_CONFIRMED`: not selected;
- `BOTH_CONTRIBUTE`: not selected;
- `MODE2_GPU_PASS`: not selected;
- `INVALID_OBSERVER`: not selected;
- `INSUFFICIENT_DATA`: not selected.

No threshold relaxation and no Production CPU backend implementation are authorized.

## [PERFORMANCE_EVIDENCE_BOUNDARY]

This run is not Performance evidence.

It deliberately adds observer-owned `CopyTexture`, one TextureConverter pass, six common-tensor inference lanes per sample, a seventh direct GPU lane for the first ten samples, multiple asynchronous GPU/CPU readbacks, full 110,592-element spatial scans, per-pair Top32 bookkeeping, and CSV/TXT allocation. It completed each pair in four rendered frames, but that value and all recorded Native crop timing are diagnostic overhead measurements only. They must not be used for Production latency, cadence, throughput, memory, or power acceptance.

There is no blocking GPU wait in the observer. Async readback still adds GPU work and synchronization pressure, which is sufficient to invalidate performance authority even without a blocking call.

## [REGRESSION_RISK]

- Production behavior risk from this task is low because all changes are opt-in observer assets and every protected SHA remained unchanged.
- Runtime interference risk is intentionally high while the environment variable is enabled: seven model workers/readbacks and full spatial scans can affect cadence. The variable must remain unset for normal Production use.
- Reflection drift risk remains: the observer depends on private Production lane fields and decode methods. Any future Production layout change must fail closed, not silently reinterpret fields.
- Tensor transit risk is closed for this run by ten exact direct-vs-frozen comparisons; it should be revalidated if Unity Inference Engine, model, tensor layout, or backend package changes.
- Spatial-causality risk remains: edge clustering is not proof of a particular border formula. Implementing a Production fix directly from the heatmap would be premature.
- Raw Runtime evidence and build outputs are diagnostic artifacts and are not commit candidates. No commit/push was performed.

## [NEXT_BEST_ACTION]

Perform exactly one observer-only follow-up: **a border/extrapolation-contract isolation for mode2 that records the transformed source coordinate and bilinear footprint for every >=4 LSB REF-vs-B outlier, then changes only the diagnostic candidate's out-of-bounds rule to match the Production TextureConverter contract**.

Keep the same FLOAT32 common-tensor GPU/CPU factorial and all fixed semantic gates. Accept that direction only if the bottom-band/edge tail collapses and `REF_GPU vs B_GPU` passes. Do not implement a Production CPU backend, change Native DLL/source, or modify Tracking/ROI/FaceTexture/Root/Head as part of that follow-up.
