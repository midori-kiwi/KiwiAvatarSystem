# KiwiAvatarSystem v44.55.24 Pair-Bound Shadow Output Snapshot Audit

Date: 2026-09-02 (Asia/Tokyo)

Authority: current local Production at `D:\KiwiAvatarSystem`

Scope: observer implementation, static validation, Unity compile/preflight/build, and one Runtime correctness run

Git: no commit, no push

## [EXECUTIVE_VERDICT]

**IMPLEMENTATION PASS / UNITY COMPILE PASS / FULL PREFLIGHT PASS / WINDOWS X64 DEVELOPMENT BUILD PASS / RUNTIME OBSERVER GATE PASS / DECISION=MIXED_TRANSACTION**

v44.55.24 successfully removes the v44.55.23 post-record shadow `Worker.PeekOutput(0)` ambiguity. The REF_FROZEN_GPU and MODE2 B_GPU outputs are now copied while the exact v44.55.20 pair still has `_pairPending=true` and `_commonScheduled=true`; record completion only binds and verifies the snapshots already owned by the observer.

The valid Runtime completed 76/76 pairs and reproduced 8 anomalous pairs. Production was bitwise equal to REF on 2 anomalies and equal to neither REF nor MODE2 on 6 anomalies. Therefore:

- the old v44.55.23 `MIXED_TRANSACTION` cannot be dismissed as only a next-pair Worker-output reuse artifact;
- pair-bound capture reproduces the `ANOMALOUS_NEITHER_EXACT` class, so those differences must now be treated as a real Production-output-versus-shadow-inference difference within this observer contract;
- the exact three old v44.55.23 records were not replayed (this is a different camera run and sequence population), so this run does not claim per-record reproduction of those same three historical samples;
- no Production CPU backend implementation is justified by this result.

## [CONFIRMED_FACTS]

### Current Production authority

- `KiwiCommonTensorBackendStageIsolationV44_55_20.cs` remained byte-identical at `1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3`.
- Corrected v44.55.23 observer remained byte-identical at `AE2CD168171335E20F6793C5F46A12264D86CCAE21DF62FAFF74FCBA1E92E4A7`.
- Git reported zero tracked modified files before and after this work. All v44.55.24 code/tool additions are new observer assets.
- No Native, Tracking, ROI, threshold, model, decode, packed-output comparison, anomaly-selector, or v44.55.20 decision code was edited.

### Existing v44.55.23 Runtime

The latest pre-v24 authority was:

- status `COMPLETE`;
- decision `MIXED_TRANSACTION`;
- completed/dependency records `115/115`;
- anomalous pairs `8`;
- Production=REF `5`;
- Production=MODE2 `0`;
- both exact `0`;
- neither exact `3`;
- observer faults and snapshot failures `0`.

Its shadow outputs were obtained after a v20 record appeared, while `PeekOutput` ownership was still the Worker allocator's. That result was valid under v44.55.23's stated contract but remained provisional for pair ownership.

### Local Unity Inference Engine source authority

The installed package is `com.unity.ai.inference` 2.4.1 for Unity 6000.0. Its exact local source establishes:

- `Worker.Schedule()` is non-blocking and executes/clears the internal GPUCompute command buffer before returning (`Worker.cs`, lines 199-257).
- `Worker.PeekOutput(index)` returns a reference that is valid only until the next `Schedule`, `ScheduleIterable`, or `Dispose` on that Worker (`Worker.cs`, lines 395-408).
- `ComputeTensorData` exposes one backing `ComputeBuffer` and one capacity count; there is no tensor-view offset member (`ComputeTensorData.cs`, lines 112-149).
- upload uses source offset 0 and destination offset 0 (`ComputeTensorData.cs`, lines 240-246); its asynchronous download paths also begin at byte offset 0 (lines 261-283).

Package source SHA256:

| Source | SHA256 |
|---|---|
| `Library/PackageCache/com.unity.ai.inference@587873fd5e1b/Runtime/Core/Backends/Worker.cs` | `011BC050A5CA80BBD313E7B4EE679D142B4740181D7F2380DD142C8910752256` |
| `Library/PackageCache/com.unity.ai.inference@587873fd5e1b/Runtime/Core/Backends/GPUCompute/ComputeTensorData.cs` | `38523335457507326E91FA5E40C332E99A18C7BFF556336F3016627788AA02D9` |

## [PUBLIC_DESIGN_PRINCIPLES]

- Unity documents `CommandBuffer.RequestAsyncReadback` as adding an asynchronous readback request to the same command buffer. The adopted snapshot dispatch and its readback request are therefore ordered as GPU commands without a CPU-side blocking wait: <https://docs.unity3d.com/ja/2021.3/ScriptReference/Rendering.CommandBuffer.RequestAsyncReadback.html>.
- Unity 6000.0 lists Sentis/Inference Engine 2.4.1 as the released package version used here: <https://docs.unity3d.com/cn/6000.0/Manual/com.unity.ai.inference.html>.
- The local package source, not a generic document version, is the implementation authority for Worker output lifetime and zero-offset backing-buffer behavior.

## [HYPOTHESES_AND_ALTERNATIVES]

| Candidate | Assessment before implementation | Runtime result |
|---|---|---|
| H1: all v44.55.23 neither-exact cases were caused by next-pair Worker output reuse | Plausible because `PeekOutput` is invalidated by the next Worker schedule | **REJECTED as a complete explanation**. Six of eight anomalies remained neither-exact under pair-bound snapshots. |
| H2: Production output is always REF_FROZEN_GPU when identity is pair-bound | Testable with exact bitwise equality | **REJECTED for this run**. Only 2/8 anomalous pairs were exact REF. |
| H3: Production output is MODE2 B_GPU | Testable with exact bitwise equality | **REJECTED for this run**. 0/8 anomalous pairs were exact MODE2. |
| H4: Production/shadow inference still differs for some pair-bound inputs/outputs | Could only be accepted after closing snapshot ownership | **CONFIRMED within the v24 observer contract**. 6/8 anomalous pairs were neither-exact with all identity/failure gates at zero. |

Alternatives compared:

1. Change v44.55.20 to retain or copy outputs internally: rejected because v44.55.20 decision logic and Production-adjacent dependency were protected.
2. Continue post-record `PeekOutput`: rejected because Worker source explicitly limits reference validity to the next schedule.
3. Add new Workers or repeat inference: rejected because that changes scheduling/resource behavior and would not measure the already scheduled pair.
4. **Adopted:** copy both existing shadow Worker outputs to observer-owned buffers during the pending/common-scheduled window, then bind them to the completed record.

## [IMPLEMENTATION]

New observer: `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiPairBoundShadowOutputSnapshotV44_55_24.cs`

- Opt-in only through `KIWI_V44_55_24_PAIR_BOUND_SHADOW_OUTPUT_AUDIT=1`.
- Attaches to the exact pending v20 pair and stores:
  - `_pairToken`;
  - record `Index` and `Sequence`;
  - `NativeHostTicks`;
  - `ManagedHostTicks`;
  - `LaneStartedHostTicks`.
- Shadow capture is permitted only while `_pairPending=true` and `_commonScheduled=true`.
- `REF_FROZEN_GPU` and `MODE2 B_GPU` are each obtained exactly once for that pair and immediately copied to an observer-owned `ComputeBuffer`.
- Every identity component is checked before capture, between the two submissions, after capture, and again when the completed v20 record is bound.
- Record completion never calls `PeekOutput`; missing pair-bound outputs fail closed.
- Duplicate/partial snapshot re-entry and pair-token reuse fail closed.
- Production `pendingOutput` uses the same validated v44.55.23 completion-boundary snapshot mechanism. Production `lane.input` is never read back.
- Logical tensor length remains exactly 1405. Backing buffers require `stride==4` and `count>=1405`; only the leading 1405 floats are copied because shader `_Count` remains 1405.
- Comparison remains bitwise for all 1405 floats. The anomaly selector remains v20 REF-vs-MODE2 input `>=4 LSB`. Decision tokens/rules are unchanged.

Added diagnostics:

- `shadowSnapshotPairToken`;
- `shadowSnapshotRecordIndex`;
- `shadowSnapshotSequence`;
- `refSnapshotFrame`;
- `mode2SnapshotFrame`;
- `recordCompletionFrame`;
- `nextPairTokenObservedBeforeRecordBind`;
- `shadowOutputCaptureMissCount`;
- `shadowIdentityMismatchCount`;
- `duplicateShadowSnapshotCount`.

Lifecycle/resource behavior:

- three observer-owned 1405-float `ComputeBuffer`s can exist per measured pair while asynchronous readbacks are pending;
- each buffer is released in its callback `finally`, on submission failure, or in `OnDisable`;
- each `CaptureState` owns three fixed 1405-float managed arrays and is removed after the record and all three snapshots complete;
- the static install guard prevents duplicate observer bootstrap in the same Player lifetime;
- this is intentional correctness-observer allocation and is not performance authority.

## [STATIC_VALIDATION]

| Check | Result |
|---|---|
| Whole-project Unity compile, Unity 6000.0.80f1 | PASS |
| Full Preflight | PASS: `passed=1`, `errors=0`, `critical=0`, warnings=2 |
| Tundra build | PASS |
| Windows x64 Development build, StrictMode | PASS: `Succeeded`, errors=0, warnings=186 |
| PowerShell 5.1 parser: Build/Run/Validate scripts | PASS, 0 parse errors |
| Runtime gate validator | PASS |
| `git diff --check` for tracked diff | PASS, exit 0 |
| Additional untracked-file trailing-whitespace scan | PASS, 0 findings |
| Tracked modified files | 0 |
| `PeekOutput` after record completion | 0; two calls exist only in pair-bound capture |
| Added `Worker` / `Schedule` | 0 / 0 |
| Added `Graphics.Blit` / `RenderTexture` | 0 / 0 |
| Blocking wait APIs | 0 |
| Production/Native/Tracking/ROI write | 0 |
| Production `lane.input` readback | 0 |
| Native rebuild/install | not performed |
| Commit/push | not performed |

Full Preflight's two pre-existing environment warnings were `Package.VersionDrift` for uGUI 2.0.0 versus the noted main baseline 1.0.0, and unverified UniVRM/UniGLTF package version. Neither was an error or critical finding.

Build outputs:

| Artifact | Bytes | SHA256 |
|---|---:|---|
| `Builds/v44_55_24PairBoundShadowOutput/KiwiAvatarSystem_v44_55_24_PAIR_BOUND_SHADOW_OUTPUT.exe` | 671744 | `98751D0DFF0DD3ADE563C2B505A0E9F7A46E8E8895864212E1B884EDBCB42E81` |
| `..._Data/Managed/Assembly-CSharp.dll` | 1583104 | `7569750685880D2C195C52837A2DAE205535F61B0F00B09CB228CA9E38CBB3DD` |

## [RUNTIME_VALIDATION]

Run configuration:

- v44.55.20 `ON`;
- v44.55.21 `OFF`;
- v44.55.22 `OFF`;
- v44.55.23 `OFF`;
- v44.55.24 `ON`;
- 120-second v20 correctness window at 1 Hz after an 8-second stable gate;
- performance authority `0`.

Raw authority files were generated by the Player under `C:\Users\main\AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem\KiwiFrameBottleneck`:

| Runtime authority | SHA256 |
|---|---|
| `KiwiPairBoundShadowOutputSnapshot_v44_55_24_20260902_220948.txt` | `1018367A812468AAA806CB6D651FA96F37E1B497F47695EF568FAAAACEF036ED` |
| `KiwiPairBoundShadowOutputSnapshot_v44_55_24_20260902_220948.csv` | `F684B19B1FFF7A06EB9C5D6B4A0919412472AB2CC72C53F57448E3C09DA26039` |
| same-run `KiwiCommonTensorBackendStageIsolation_v44_55_20_20260902_220948.txt` | `688B536FAA1D10F4611FE2FC2BABD08AF91A28B21F3CCDBADBFE60683FC00BA9` |
| same-run `KiwiCommonTensorBackendStageIsolation_v44_55_20_20260902_220948.csv` | `B13577796B7664C21569EDF5C22420552F43D8A7ABFB1A330A31899632FEDF8B` |
| `Player.log` after Player exit | `AAD57703A7CDE599B7EDDDAAA2061E51856A5FF1381F6FA74F8971090C700AF0` |

Fail-closed gate:

| Gate | Required | Actual | Result |
|---|---:|---:|---|
| dependency | COMPLETE | COMPLETE | PASS |
| completedPairCount | >=60 | 76 | PASS |
| dependencyRecordCount | equals completed | 76 | PASS |
| anomalousPairCount | >=3 | 8 | PASS |
| observerFaultCount | 0 | 0 | PASS |
| attachMissCount | 0 | 0 | PASS |
| recordCaptureWindowMissCount | 0 | 0 | PASS |
| shadowOutputCaptureMissCount | 0 | 0 | PASS |
| shadowIdentityMismatchCount | 0 | 0 | PASS |
| duplicateShadowSnapshotCount | 0 | 0 | PASS |
| productionOutputSnapshotFailureCount | 0 | 0 | PASS |
| referenceOutputSnapshotFailureCount | 0 | 0 | PASS |
| mode2OutputSnapshotFailureCount | 0 | 0 | PASS |
| nonFiniteOutputCount | 0 | 0 | PASS |
| inputArrayIdentityMismatchCount | 0 | 0 | PASS |
| outputShapeMismatchCount | 0 | 0 | PASS |
| capturesPending | 0 | 0 | PASS |

Additional confirmation:

- 76 CSV rows, 76 distinct positive shadow pair tokens, and 76 matching record index/sequence identities.
- Every REF and MODE2 snapshot frame was at or before its record completion frame.
- In this run both shadow snapshots occurred on the same frame, and the record completed three frames later for all reported anomalous examples.
- `nextPairTokenObservedBeforeRecordBind=0` for every pair.
- v21/v22/v23 observer log-line count was 0.
- v24 `OBSERVER_FAULT` log-line count was 0.

Anomalous pair results:

| index | sequence | token | shadow frame | record frame | >=4-LSB count | Production=REF | Production=MODE2 | Classification |
|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 4 | 901 | 7 | 1017 | 1020 | 37014 | 1 | 0 | ANOMALOUS_PRODUCTION_EQUALS_REFERENCE |
| 30 | 2744 | 33 | 3544 | 3547 | 36538 | 0 | 0 | ANOMALOUS_NEITHER_EXACT |
| 35 | 3597 | 38 | 4801 | 4804 | 20 | 0 | 0 | ANOMALOUS_NEITHER_EXACT |
| 40 | 4269 | 43 | 5796 | 5799 | 12 | 0 | 0 | ANOMALOUS_NEITHER_EXACT |
| 49 | 5549 | 52 | 7680 | 7683 | 6 | 1 | 0 | ANOMALOUS_PRODUCTION_EQUALS_REFERENCE |
| 58 | 6530 | 61 | 9086 | 9089 | 2 | 0 | 0 | ANOMALOUS_NEITHER_EXACT |
| 63 | 6838 | 66 | 9506 | 9509 | 24 | 0 | 0 | ANOMALOUS_NEITHER_EXACT |
| 68 | 7145 | 71 | 9934 | 9937 | 12 | 0 | 0 | ANOMALOUS_NEITHER_EXACT |

The same-run v44.55.20 report reached `status=COMPLETE` and 76 completed pairs, but its broader canonical-semantic decision was `INSUFFICIENT_DATA` because only 16 canonical-comparable pairs were available versus its separate minimum of 40. That does not fail the user-specified v24 output-transaction gate, but it prevents using this run as a complete semantic or Production-release verdict.

## [INTERPRETATION]

### Confirmed

- Pair-bound shadow snapshot mechanics are valid for this run: identity, completeness, capacity, readback, finite-value, and duplicate gates all closed.
- `ANOMALOUS_NEITHER_EXACT` persists after removing post-record Worker-output ownership ambiguity.
- Production exactly equals REF on some anomalous pairs, equals MODE2 on none, and equals neither on most anomalous pairs in this run.
- The v24 decision under the unchanged decision rule is `MIXED_TRANSACTION`.

### Inference

The most defensible inference is that v44.55.23's mixed result was not solely an observer Worker-lifecycle race. Pair-bound evidence now points to a remaining difference between the completed Production packed output and both shadow inference outputs for some same-pair records.

### Not proven

- The exact cause of the remaining neither-exact population is not proven by this observer. It could be an unobserved difference in the Production inference transaction, tensor/input lifetime, backend scheduling context, or another authority boundary not represented by the six recorded identity fields.
- This run does not prove that all historical v44.55.23 neither-exact records would remain neither-exact if replayed bit-for-bit.
- v20's broader semantic validity is not complete in this run.
- No performance conclusion is permitted because the observer adds GPU copies/readbacks and logging.

## [REGRESSION_RISK]

- Production runtime risk is low when the environment variable is absent because v44.55.24 does not bootstrap.
- Observer-enabled resource risk is bounded but nonzero: three 1405-float GPU buffers, three 1405-float managed arrays, command buffers, callbacks, reflection reads, and logs are used per measured pair.
- Execution-order coupling is deliberate: the early probe must inspect a completed Production pending output before Production Update consumes it; the late observer must see shadow Workers after v20 scheduling and before a later pair schedule.
- Private-field reflection is fail-closed. Renaming v20 fields will invalidate the observer instead of silently changing authority.
- Package-source assumptions are version-specific to local Inference Engine 2.4.1.

## [NEXT_BEST_ACTION]

Do not implement a Production CPU backend. The next single best action is a new observer-only transaction trace that binds the exact Production Worker schedule token/input tensor identity and its packed output to the same six-field v20 pair identity, without reading `lane.input`, repeating inference, or adding waits. The purpose should be to locate the authority boundary before proposing any Production fix.

## [CHANGED_FILES]

New source/assets:

1. `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiPairBoundShadowOutputSnapshotV44_55_24.cs`
2. `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiPairBoundShadowOutputSnapshotV44_55_24.cs.meta`
3. `Assets/KiwiAvatarSystem/Runtime/Validation/Resources/KiwiValidation/KiwiOutputSnapshotCopyV44_55_24.compute`
4. `Assets/KiwiAvatarSystem/Runtime/Validation/Resources/KiwiValidation/KiwiOutputSnapshotCopyV44_55_24.compute.meta`
5. `Tools/KiwiV44_55_24/Build-KiwiV44_55_24.ps1`
6. `Tools/KiwiV44_55_24/Run-KiwiV44_55_24.ps1`
7. `Tools/KiwiV44_55_24/Validate-KiwiV44_55_24.ps1`
8. `Tools/KiwiV44_55_24/README.txt`
9. `KiwiValidation/KiwiPreflight_v44_55_24.json`
10. `KiwiValidation/KIWI_PAIR_BOUND_SHADOW_OUTPUT_SNAPSHOT_V44_55_24_REPORT.md`

Generated but ignored/untracked Runtime/build evidence:

- `KiwiValidation/KiwiBuild_v44_55_24.log`;
- `Builds/v44_55_24PairBoundShadowOutput/`;
- raw Runtime TXT/CSV and Player.log under LocalLow as listed above.

No pre-existing tracked file was modified. Pre-existing unrelated/user-owned untracked v44.55.22, v44.55.23, and KlakSpout diagnostic files were preserved.

## [SHA256]

### v44.55.24 additions

| File | Before | After |
|---|---|---|
| `KiwiPairBoundShadowOutputSnapshotV44_55_24.cs` | ABSENT | `805E746E453F3785FCAA46EFB07745B0038A428789D4A09BB944854F15477FBA` |
| `KiwiPairBoundShadowOutputSnapshotV44_55_24.cs.meta` | ABSENT | `3DBE598D07E1EFB25D8AA9BD85957EE56979FD9622B31D800E26C13214D7F712` |
| `KiwiOutputSnapshotCopyV44_55_24.compute` | ABSENT | `4552D8A2CB4E47465D1BD765CD38D85F7B839A61B73555CD40557937D803CB71` |
| `KiwiOutputSnapshotCopyV44_55_24.compute.meta` | ABSENT | `553D930C6BFF1967510B483B61078EAFF984D340D07B6F073351BCAA15AEABD9` |
| `Build-KiwiV44_55_24.ps1` | ABSENT | `8DD7D015704E397943AF63165969DF89D0E294DDC888793CC051B6770BAC95D9` |
| `Run-KiwiV44_55_24.ps1` | ABSENT | `447B0841CB124F2C178711F341F161451A7852F989D5BFFC1DEB34314F84C9B9` |
| `Validate-KiwiV44_55_24.ps1` | ABSENT | `1FC5BC818CBC52D8F648B98FB29DFECF8B9F3B3B331D1580D464C5C608B91F5D` |
| `README.txt` | ABSENT | `6DD76F79620A96B19288FE9ACF2894508956A5BE80FF92723DB8BC27B6C99E03` |
| `KiwiPreflight_v44_55_24.json` | ABSENT | `9A9F94BEFF9F32931C1C9EB35492C49E175FCA829FC2CD2D343D72218564B40F` |

### Protected Production / Native / Tracking files (before = after)

| Protected file | SHA256 |
|---|---|
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiCommonTensorBackendStageIsolationV44_55_20.cs` | `1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3` |
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiProductionOutputTransactionAuthorityV44_55_23.cs` | `AE2CD168171335E20F6793C5F46A12264D86CCAE21DF62FAFF74FCBA1E92E4A7` |
| `Assets/KiwiAvatarSystem/Runtime/Tracking/KiwiInferenceFaceTracker.cs` | `EFFCCCF5EE1BF407F065BFA95B491390AC96A55E9A75290A67C5AFAD32AFC1F1` |
| `Assets/Script/KiwiInferenceFaceTracker.cs` | `52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695` |
| `Assets/Script/FaceLandmarkerRunner.cs` | `6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93` |
| `Assets/Script/KiwiFaceMotion.cs` | `D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6` |
| `Assets/KiwiAvatarSystem/Runtime/Camera/KiwiNativeCameraInterop.cs` | `AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5` |
| `Assets/Plugins/x86_64/KiwiNativeCamera.dll` | `82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5` |
| `Native/KiwiNativeCamera/Source/KiwiNativeCameraPlugin.cpp` | `636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A` |
| `Native/KiwiNativeCamera/Build/x86_64/KiwiNativeCamera.dll` | `8091265D91993B0082BFC63D55FFF8A18CC18AACA9B1F907E874E7011D8017E2` |

Final answer to the protected-scope question: **Production / Native / Tracking / ROI changes: NO.**
