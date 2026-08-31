# KiwiAvatarSystem Automated Runtime Validation Harness v1 — 設計仕様

Status: DESIGN COMPLETE / IMPLEMENTATION NOT STARTED  
Target baseline: `v42.3` ORT DirectML Native D3D12 Zero-Copy Shadow  
Target host: local Windows / Unity `6000.0.80f1` / RTX 4090  
Updated: 2026-08-27

## 1. 結論

Harness v1は、PowerShell 7を唯一の外側オーケストレータとし、Unity Player側にはDevelopment Validation時だけ有効になる小さな自動計測コントローラを追加する。

採用経路は次のとおり。

```text
Run-Kiwi-FullValidation.ps1
  -> 排他ロック / identity解決 / transaction準備
  -> Unity別プロセス: PluginImporter Validate/Apply
  -> Unity別プロセス: Static Validate
  -> Unity別プロセス: Standalone Build
  -> build artifact / version / plugin検証
  -> BASELINE_METRICS Player
  -> ZEROCOPY_METRICS Player
  -> BASELINE_VISUAL Player + FFmpeg capture
  -> ZEROCOPY_VISUAL Player + FFmpeg capture
  -> exact-token artifact収集
  -> completeness -> telemetry分析 -> A/B回帰判定
  -> report.json -> schema validation -> report.md
  -> automatedVerdict + finalVerdict
```

性能判定用Runと動画用Runを分ける。FFmpeg/encoder負荷がGPU 98–100%の本体計測を非線形に汚す可能性があるため、MP4付きRunの性能値は参考値とし、A/B性能gateには使わない。

`finalVerdict=GO`には、全自動gateに加えてMP4の1フレーム単位レビュー記録が必要である。レビューが未完ならfail-closedで`finalVerdict=NO_GO`とし、理由を`VISUAL_REVIEW_REQUIRED`にする。自動処理だけでvisual correctnessを推測してPASSにはしない。

## 2. 非変更契約

Harness実装は以下を変更しない。

- `Assets/Script/KiwiFaceMotion.cs`
- `Assets/Script/KiwiInferenceFaceTracker.cs`のtracking math、ROI、threshold、decode、smoothing、canonical rules
- `Assets/Script/FaceLandmarkerRunner.cs`のFace Landmarker Primary authority
- `Assets/KiwiAvatarSystem/Runtime/Camera/`のNative Camera Path B
- model、timestamp、FaceTexture transaction、stale/generation gate
- Unity graphics queue wait、CPU fence wait、camera texture identity

追加するUnity runtimeコードは診断制御だけを担当し、次の全条件が成立しない限り即時returnする。

```text
WindowsPlayer
AND Debug.isDebugBuild
AND KIWI_AUTO_RECORD=1
AND valid KIWI_VALIDATION_TOKEN
```

Production build、Editor Play Mode、通常のDevelopment Playerでは挙動を変えない。

## 3. 外部調査結果

### 3.1 Unity batch build

Unity 6は、CLI buildで`-projectPath`、`-quit`、`-batchmode`、`-logFile`、明示的な`-buildTarget StandaloneWindows64`、`-executeMethod`を使う方式を公式に案内している。batch中のtarget切替には制約があるため、Apply、Static Validate、Buildはそれぞれ独立したUnityプロセスにする。

Source: [Unity 6: Build a player from the command line](https://docs.unity3d.com/6000.0/Documentation/Manual/build-command-line.html)

### 3.2 Player起動・終了監視

`Start-Process -PassThru`または`System.Diagnostics.Process`でPIDを保持できる。Harnessは途中のlog markerとtimeoutを監視する必要があるため、単純な`Start-Process -Wait`だけには依存しない。有限timeout後に`WaitForExit()`を呼び、終了後だけ`ExitCode`を読む。

Source: [PowerShell Start-Process](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/start-process), [.NET Process.WaitForExit](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.waitforexit), [.NET Process.ExitCode](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.exitcode)

Unity batch script内の例外はnon-zero exitにでき、必要なら`EditorApplication.Exit(int)`で明示的なreturn codeを返せる。各Editor entrypointは結果receiptも書き、`ExitCode=0`だけを成功根拠にしない。

Source: [Unity EditorApplication.Exit](https://docs.unity3d.com/ScriptReference/EditorApplication.Exit.html)

### 3.3 Player log・crash detection

Windows Playerの既定logはLocalLowにあり、Unity crash folderは`-crash-report-folder`で変更できる。Harnessはcase固有の`-logFile`とcrash folderを指定し、process exit、startup/completion marker、log fatal pattern、crash artifactの4系統を独立判定する。

Source: [Unity log files](https://docs.unity3d.com/Manual/LogFiles.html)

Windows Error Reporting LocalDumpsは追加診断として有効だが、HKLM設定と管理者権限が必要でHarness本体の必須条件にはしない。既に有効な場合だけdumpを収集し、Harness自身はregistryを変更しない。

Source: [Microsoft: Collecting user-mode dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps)

### 3.4 GPU telemetry

`nvidia-smi --query-gpu ... --format=csv`と`--loop-ms`は機械可読な周期収集を提供する。GPU indexは再起動で安定しないため、事前に一意に解決したGPU UUIDを`-i`へ渡す。

Source: [NVIDIA System Management Interface](https://docs.nvidia.com/deploy/nvidia-smi/index.html)

現在ホストで確認済み:

- GPU UUID: `GPU-6efc5d27-d766-eeac-cb81-8aa14cebbfa3`
- GPU: `NVIDIA GeForce RTX 4090`
- Driver: `610.88`
- `nvidia-smi`: `C:\Windows\System32\nvidia-smi.exe`

これらは固定値として信頼せず、各Runのpreflightで再取得しreportへ保存する。

### 3.5 video capture / 1-frame検証

Unity RecorderはUnity EditorのPlay Mode capture用で、Standalone Playerそのものの計測からEditorを分離する本要件には適さない。

Source: [Unity Recorder 5.1](https://docs.unity3d.com/6000.0/Documentation/Manual/com.unity.recorder.html)

FFmpeg `gdigrab`はWindowsの特定windowをtitleまたはHWNDでcaptureできる。Harnessはtitle一致ではなく、起動したPlayer PIDから検証したMainWindowHandleを得て`hwnd=`を使う。capture後は`ffprobe`でcontainer、codec、duration、dimensions、frame count、timestamp monotonicity、decode errorを検査する。

Source: [FFmpeg gdigrab](https://ffmpeg.org/ffmpeg-devices.html#gdigrab), [FFmpeg ffprobe](https://ffmpeg.org/ffprobe.html)

現在のbundled toolで確認済み:

- `D:\KiwiAvatarSystem\Tools\ffmpeg\bin\ffmpeg.exe`
- build: `2026-08-09-git-6bbc22dc09`
- `gdigrab`, `libx264`, `h264_nvenc`あり

v1の既定encoderはCPU側`libx264`の`ultrafast`とする。visual caseだけで使用し、metrics gateから分離する。NVENCは選択可能だが、GPU saturationへの影響を別途較正するまで既定にしない。

OBSは`--startrecording`を持つが、scene collection/profile/停止制御という外部状態を増やすためv1の標準経路には採用しない。

Source: [OBS launch parameters](https://obsproject.com/kb/launch-parameters)

### 3.6 CI / local runner分離

実カメラ、RTX 4090、DX12、対話desktopを必要とするRuntime計測はGitHub-hosted runnerへ移さない。CIはschema/config/static script testsまで、Runtime jobはWindows self-hosted runnerの専用labelへrouteする。GitHub公式もself-hosted runnerで任意hardwareを選択でき、GPU等のcustom labelを使えるとしている。

Source: [GitHub self-hosted runners](https://docs.github.com/en/actions/concepts/runners/self-hosted-runners), [custom labels](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/use-in-a-workflow)

推奨label:

```text
self-hosted, windows, x64, kiwi-camera, rtx4090, unity6000-0-80f1, interactive-desktop
```

Service sessionの非対話desktopからカメラ/video captureを実行しない。最初のv1はlocal interactive executionを正式経路とし、CI dispatchは後段とする。

## 4. 類似方式比較

| 案 | 利点 | 欠点 / 回帰リスク | 判断 |
|---|---|---|---|
| PowerShell orchestrator + diagnostic Player controller | 現在のWindows運用、Unity entrypoint、CSV、FFmpeg、nvidia-smiを再利用できる。PID、timeout、copy、hash、JSON生成が明示的 | shellとUnityの二層契約が必要 | 採用 |
| Unity Test Framework Player tests | test result XMLとPlayer test lifecycleを利用できる | Editor/PlayerConnection依存、実カメラ/GPU/外部capture/artifact transactionに弱く、製品scene runtimeと異なるtest buildになり得る | 不採用 |
| Unity Editor + Recorder | scripting APIとvideo captureが統合 | Editor processが性能値を汚し、Standalone分離要件を満たさない | 不採用 |
| OBS主導 | Game CaptureとNVENCが成熟 | profile/scene/hotkey/websocketの外部状態、停止・保存先の整合性、導入差異 | v2候補 |
| 独自C#/C++ runner | 型安全、Win32/WGCを完全制御 | 実装・配布・署名・保守面積が過大 | v2以降 |

## 5. Kiwi現状との比較

### 再利用する既存実装

- `KiwiOptimizationValidator.RunBatchValidation()`：失敗時`BuildFailedException`
- `KiwiV42PluginImportPolicy.ValidateBatch()` / `ApplyAndValidateBatch()`：PluginImporter APIがsource of truth
- `KiwiStandaloneBuildDiagnostic.BuildWindows64()`：scene解決、preflight、DX12-only Development build
- `KiwiFrameComparisonOverlay`：Render frame CSV、F9 manual start/stop API
- `KiwiOrtDmlZeroCopyRuntime`：`KIWI_ORT_DML_ZERO_COPY_SHADOW=1`、Zero-Copy CSV、parity
- `KiwiRuntimeValidationHarness`：generation / canonical / provider / presentation violation report
- `KiwiStandaloneRuntime`：run-in-background、VSync、target frame rate

### 実装前に解消すべきgap

1. 外側Runnerがrepoに存在しない。
2. F9操作と手動quitが必要。
3. CSV filenameにcase tokenがなく、mtime/latest依存になりやすい。
4. Zero-Copy原本はLocalLowだが、正式収集対象はRunnerがrootへコピーしたfileである。
5. Runtime validation JSONは固定名で上書きされる。
6. case completion receiptがない。
7. GPU / video / crash / exact artifact completenessが統合されていない。
8. CSV schemaとthresholdの機械検証がない。
9. `KiwiStandaloneBuildDiagnostic`のcontract、environment名、meta名、説明が`v39`のままで、v42.3 buildにも`KiwiStandalone_v39.build.meta.txt`を出している。
10. installed markerは`5.1.0-phase16.20.32-v42.3`、preflight reportは`5.1.0-phase16.19.5.1`であり、役割の異なるversionを一つのversionとして比較してはならない。
11. `ortMinusSentisObservedMs`既知bugをcolumn値のまま使用できない。
12. shutdown resource warningを既知issueとして明示分類する必要がある。

## 6. file構成

実装時の完全版file構成:

```text
Tools/
  Run-Kiwi-FullValidation.ps1
  Invoke-KiwiStandaloneCase.ps1
  Analyze-KiwiTelemetry.ps1
  Test-KiwiArtifactCompleteness.ps1
  Test-KiwiVideo.ps1
  Set-KiwiVideoReview.ps1

Validation/
  KiwiAutomatedRuntimeValidationHarnessV1.md
  KiwiValidationThresholds.json
  KiwiValidationReport.schema.json

Assets/KiwiAvatarSystem/Runtime/Validation/
  KiwiAutomatedValidationController.cs

Assets/KiwiAvatarSystem/Editor/
  KiwiAutomatedValidationEditorEntryPoints.cs
```

既存fileを無理由に再実装しない。Editor entrypointは既存validator/build/plugin policyを呼び、結果receiptとversion identityを追加するadapterに限定する。

## 7. identityとversion-consistency

version文字列を複数scriptへ手書きしない。`KiwiValidationThresholds.json`のprofileが次のexact identityを1回だけ定義し、全artifact名をそこから派生する。

```text
systemVersion      = v42.3
installedVersion   = 5.1.0-phase16.20.32-v42.3
installedManifest  = Phase16_20_32_v42_3_NATIVE_PLUGIN_DEVICE_LIFECYCLE_ROOTFIX.json
unityVersion       = 6000.0.80f1
mode               = BASELINE | ZEROCOPY
purpose            = METRICS | VISUAL
resolution         = 720P (1280x720 Player window)
```

preflight version-consistency gate:

1. profileのmanifest pathがexactに存在する。
2. manifest JSONの`version`から`v42.3`を抽出しprofileと一致する。
3. Unity `ProjectVersion.txt`が`6000.0.80f1`。
4. model SHA256がhandoff値`ed487...fb335`。
5. source bridge hashとinstalled manifestのbridge hashが一致する。
6. build receiptのsystemVersion、Unity version、scene、DX12-only、Developmentが一致する。
7. exe basename、Data folder basename、Player log marker、case receipt、CSV tokenが同じidentityを持つ。
8. build treeのbridge DLLはexactly one。
9. PDBはbridgeの隣、MAPはbuild root。
10. 旧`v39`meta/contractをv42.3の成功根拠に使わない。

preflight reportや内部phase versionは別fieldとして保存し、systemVersionとの文字列一致を要求しない。役割を`systemVersion`、`preflightContractVersion`、`runtimeHarnessVersion`に分離する。

## 8. RunID / case token / artifact関連付け

RunID:

```text
YYYYMMDD_HHMMSS_<VERSION>_FULLVALIDATION_<RESOLUTION>
```

例:

```text
20260827_210500_v42_3_FULLVALIDATION_720P
```

CaseID:

```text
<RunID>_<ORDER>_<MODE>_<PURPOSE>
```

例:

```text
20260827_210500_v42_3_FULLVALIDATION_720P_01_BASELINE_METRICS
```

Playerへ渡す`KIWI_VALIDATION_TOKEN`はASCII `[A-Z0-9_]+`へ正規化したCaseIDそのものとする。各writerはenvironmentが有効なときだけexact tokenをfilenameへ含める。

禁止:

- directory内の「最新file」を採用
- wildcard結果が複数でも先頭を採用
- mtimeだけで別caseのartifactを関連付け
- sourceを移動/削除

収集規則:

1. case開始前にsource directory inventoryをhash付きで保存。
2. controller receiptが返したexact absolute pathを使用。
3. filename token、receipt CaseID、CSV内marker、time windowを相互検証。
4. exact one matchでなければFAIL。
5. copy後にsource/destination SHA256一致を確認。
6. 原本は保持。

正式なsource / copy経路:

```text
FrameComparison:
  LocalLow/.../KiwiFrameComparison/<token>.csv
  -> ValidationRuns/<RunID>/<CaseID>/FrameComparison/

ZeroCopy:
  LocalLow/.../KiwiOrtDmlZeroCopy/<token>.csv
  -> D:/KiwiAvatarSystem/KiwiOrtDmlZeroCopyTelemetry_<token>.csv
  -> ValidationRuns/<RunID>/<CaseID>/ZeroCopy/

Player log:
  Player -logFile D:/KiwiAvatarSystem/KiwiStandalonePlayer_<token>.log
  -> ValidationRuns/<RunID>/<CaseID>/Logs/

GPU:
  D:/KiwiAvatarSystem/KiwiGpuTelemetry_<token>.csv
  D:/KiwiAvatarSystem/KiwiGpuTelemetry_<token>.meta.txt
  -> ValidationRuns/<RunID>/<CaseID>/GPU/
```

## 9. transaction / idempotency

### 9.1 排他

Harness起動時にproject root基準のnamed mutexとexclusive lock fileを取得する。別Harness、Unity Editor、同じPlayer executableの既存processが検出されたら、対象をkillせずFAILする。

### 9.2 state

`ValidationRuns/<RunID>/transaction.json`をatomic replaceで更新する。

```text
PREPARED
  -> APPLYING
  -> COMMITTED

failure before COMMITTED:
  -> ROLLING_BACK
  -> ROLLED_BACK | ROLLBACK_FAILED
```

### 9.3 PluginImporter apply

1. 先に`ValidateBatch`を実行する。
2. validならApplyを省略し、hashを変えない。
3. invalidでbridge `.meta`にunknown user editがある場合はapplyせずFAIL。
4. owned targetのpre-hashとbyte-for-byte backupをRun stagingへ保存。
5. stateを`APPLYING`へ変更。
6. Unity Editor API経由`ApplyAndValidateBatch`を実行。
7. 別Unity processで`ValidateBatch`を再実行。
8. current hashとreceiptを記録して`COMMITTED`。
9. 途中失敗時は、current hashがHarness直後hashと一致する場合だけbackupを復元。
10. restore後にhash一致とUnity API validationを行う。
11. concurrent/unknown変更でhashが異なる場合は上書きせず`ROLLBACK_FAILED`。

`.meta`のYAML/text parse・文字列置換は禁止。backup restoreは解析せずexact bytesを戻すだけで、その後の意味検証はPluginImporter APIで行う。

### 9.4 rerun

同じRunIDを上書きしない。`-Resume <RunID>`は将来optionとし、v1初版では既存RunIDを見つけたらFAILする。これが最も単純なidempotent/fail-closed動作である。

## 10. Unity diagnostic controller

`KiwiAutomatedValidationController`は既存tracking結果を観測するだけで、次のstate machineを持つ。

```text
BOOT
  -> WAITING_FOR_PREREQUISITES
  -> STARTUP_READY
  -> WARMUP
  -> RECORDING
  -> FLUSHING
  -> RECEIPT_COMMITTED
  -> QUITTING
```

prerequisite:

- `KiwiFrameComparisonOverlay.Instance`存在
- Native Camera active
- source texture dimensions > 0
- native source timestamps calibrated
- Runtime Validation Harness存在
- ZEROCOPY時はv42.3 bridge ready / enabled marker
- graphics API = Direct3D12
- requested Player window = 1280x720

環境変数:

```text
KIWI_AUTO_RECORD=1
KIWI_AUTO_RECORD_DELAY_SEC=5
KIWI_AUTO_RECORD_DURATION_SEC=30
KIWI_AUTO_QUIT=1
KIWI_VALIDATION_RUN_ID=<RunID>
KIWI_VALIDATION_CASE_ID=<CaseID>
KIWI_VALIDATION_TOKEN=<token>
KIWI_VALIDATION_MODE=BASELINE|ZEROCOPY
KIWI_VALIDATION_PURPOSE=METRICS|VISUAL
KIWI_VALIDATION_RESOLUTION=720P
```

record終了時:

1. `StopCsvRecording()`
2. Zero-Copy writerの停止を待つ
3. 2 render frame待つ
4. Runtime validation JSONをtoken付きでexport
5. exact artifact path、rows、runtime status、timestampsをcase receiptへtemporary write
6. flush + atomic rename
7. `[KiwiValidationV1] RECEIPT_COMMITTED token=...`をlog
8. `Application.Quit(0)`

controller内部errorはerror receiptを可能な限り書き、`Application.Quit(nonzero)`を使う。例外を握り潰して0で終了しない。

## 11. Player process monitor

`Invoke-KiwiStandaloneCase.ps1`はProcess objectを直接保持する。

launch arguments:

```text
-force-d3d12
-screen-fullscreen 0
-screen-width 1280
-screen-height 720
-logFile <exact Player log path>
-crash-report-folder <case Crash directory>
```

monitor gates:

1. process start API成功
2. PID > 0
3. executable path一致
4. startup timeout内にprocessが生存
5. `[KiwiValidationV1] STARTUP_READY token=...`
6. mode marker一致
7. ZEROCOPY時は`[KiwiInferenceV42.3] ... enabled=1 ... gpuInputReadback=0 ... authority=UNITY_INFERENCE_ENGINE`
8. receipt committed marker
9. graceful quit timeout内にexit
10. ExitCode exact 0
11. completion receipt exact one / valid JSON / token一致
12. crash artifact 0
13. fatal/error log rule 0

`ExitCode=0`でもready/completion/receiptのどれかが無ければFAIL。startup直後に終了したPlayerをrun completeにしない。

timeout時はまずcompletion/receiptを再確認し、まだrunningなら`CloseMainWindow()`を1回だけ試す。grace timeout後はprocess treeを停止し、`forcedTermination=true`として必ずFAILする。killを成功扱いしない。

## 12. GPU telemetry

caseごとにPlayerより先にcollectorを起動し、Player終了後に止める。

query候補:

```text
timestamp,index,uuid,name,driver_version,pstate,
temperature.gpu,utilization.gpu,utilization.memory,
memory.used,power.draw,clocks.current.graphics,clocks.current.memory
```

rules:

- GPU UUIDをexact指定
- sampling intervalはprofile値（初期値200ms）
- command、tool version、GPU identity、start/end、collector termination reasonをmetaへ保存
- CSV header、minimum rows、time span、numeric parseを検証
- collectorが欠落/空/別GPUならFAIL
- visual caseのGPU値はreportするがperformance A/Bには使わない

NVIDIA utilization値の内部sample periodは製品により異なるため、200ms queryを200ms精度の独立sampleとは解釈しない。median/P95は観測値として扱い、FrameComparisonのrender FPSと混同しない。

## 13. video captureとframe QA

visual caseのみ、PlayerのSTARTUP_READY後にMainWindowHandleを解決してFFmpegを開始する。recording markerとcapture開始の差をreceiptへ記録する。

最低検証:

- FFmpeg exit 0
- MP4 exists / nonzero
- H.264 video stream exact one
- width 1280 / height 720
- duration 29–32秒
- decoded framesがthreshold以上
- PTSが単調増加
- decode error 0
- first/last frameのblack-frame率をreport
- frame index CSVを保存

1-frame visual review protocol:

1. `ffprobe -show_frames`で全frame index/timestampを生成。
2. review toolは1 frameずつ進め、skipなしでreviewed frame countを保存。
3. reviewerはavatar root、eye/mouth texture、matched preview、freeze、one-frame jump、epoch mismatchを判定。
4. `video-review.json`へvideo SHA256、frame count、reviewer、reviewedAt、PASS/FAIL、findingsを保存。
5. report再生成時にvideo SHA256とreview recordを照合。

署名recordなし、frame count不一致、video hash不一致、FAIL findingありのいずれかは`finalVerdict=NO_GO`。

## 14. artifact completeness

各caseでrequired artifact setをprofileから解決する。最低構成:

| Case | Required |
|---|---|
| BASELINE_METRICS | FrameComparison, GPU CSV/meta, Player log, runtime validation JSON, case receipt |
| ZEROCOPY_METRICS | 上記 + ZeroCopy CSV |
| BASELINE_VISUAL | 上記 + MP4 + ffprobe JSON + frame index |
| ZEROCOPY_VISUAL | 上記 + ZeroCopy CSV + MP4 + ffprobe JSON + frame index |

Run共通required:

- apply receipt
- static validation receipt/log
- build receipt/log
- build artifact inventory/hash
- PluginImporter receipt
- thresholds snapshot/hash
- schema snapshot/hash
- `report.json`
- `report.md`

file存在だけでPASSにしない。typeごとにparser validation、row/frame count、duration、token、SHA256、time windowを検査する。

## 15. CSV分析契約

### 15.1 一般

- UTF-8 / invariant culture
- duplicate header禁止
- required column exact match
- required numeric cellのempty/NaN/Infinity禁止
- `realtimeSeconds`単調非減少
- counter regressionはFAIL
- record duration不足はFAIL
- analysis windowはrecording開始後2秒、終了前1秒をtrim（counterはfull window deltaも保持）
- 全metricにsource file hash、column、statistic、sample countを付与

### 15.2 FrameComparison

主要mapping:

| Report metric | Source / calculation |
|---|---|
| camera.nativeSourceHz | `nativeSourceCount` delta / elapsed、`nativeSourceIntervalMs`でcross-check |
| render.fps | `renderFps` p50/p95 |
| sentis.requestToDoneMs | `inferenceRequestToDoneObservedMs` p50/p95 |
| sentis.observedFrameDelta | `inferenceReadbackObservedFrameDelta` p50/p95 |
| tracking.acceptedSourceAgeMs | `inferenceAcceptedSourceAgeMs` p50/p95 |
| tracking.canonicalHz | `canonicalAdoptionCount` delta / elapsed、`canonicalAdoptionHz` cross-check |
| native.dropCountDelta | drop系counterすべてのdeltaとmax |
| native.gpuWaitCountDelta | `nativeCaptureGpuWaitCount` + `nativeProcessingGpuWaitCount` delta |
| tracking.crossSystemDiscardDelta | `inferenceDiscardedCrossSystemCount` delta |
| epoch.violationDelta | handoff / face part / texture transaction violation counter delta |

### 15.3 Zero-Copy

| Report metric | Source / calculation |
|---|---|
| ort.inferenceMs | `ortInferenceMs` p50/p95 |
| ort.bridgeToDoneMs | `bridgeToDoneMs` p50/p95 |
| ort.sourceToObservedMs | `sourceToOrtObservedMs` p50/p95 |
| ort.effectiveHz | distinct completion rows / elapsed |
| ort.readyReplacementDelta | `nativeReadyReplacements` delta |
| parity.landmarkMaeNormalized | `landmarkMaeNormalized` p50/p95/max |
| parity.landmarkMaxAbsRaw | `landmarkMaxAbsRaw` p50/p95/max |
| parity.presenceAbsDiff | `presenceAbsDiff` p50/p95/max |

`ortMinusSentisObservedMs`columnはv42.3既知bugのためgateに使わない。次式で必ず再計算する。

```text
(ortObservedHostTicks - sentisArrivalHostTicks) * 1000 / Stopwatch.Frequency
```

PowerShell hostの`Stopwatch.Frequency`とFrameComparisonの`nativeQpcFrequency`が一致しない場合はFAILする。derived metricは`source=hostTicksDerived`をreportへ明記する。

## 16. 判定

threshold値は[KiwiValidationThresholds.json](./KiwiValidationThresholds.json)を唯一の設定sourceとする。初期値はv42.3既存実測から置いたprovisional値であり、最初の同条件BASELINEを得た後にversioned reviewを行う。

gate class:

```text
STATIC
BUILD
PLAYER_START
EARLY_CRASH
EXIT_CODE
LOG_FATAL_ERROR
ARTIFACT_COMPLETENESS
CSV_SCHEMA_AND_ROWS
CAMERA
RENDER
SENTIS_LATENCY
ACCEPTED_SOURCE_AGE
CANONICAL_RATE
ORT_LATENCY_AND_RATE
QUEUE_DROP_WAIT_REPLACEMENT
PARITY
GPU
RUNTIME_EPOCH
VIDEO_INTEGRITY
VISUAL_REVIEW
```

verdict:

```text
automatedVerdict = GO
  iff all required automated gates PASS

finalVerdict = GO
  iff automatedVerdict == GO
  AND BASELINE + ZEROCOPY video review PASS
  AND reviewed video SHA256 matches artifacts

otherwise NO_GO
```

WARNはreportへ残すが、required gateのWARNをPASSへ自動昇格しない。`knownIssues`はexact patternとmaximum countを持ち、未知の類似messageを吸収しない。

## 17. log policy

即FAIL例:

- native crash / access violation / stack trace crash header
- `DllNotFoundException`
- `EntryPointNotFoundException`
- D3D12 device removed / graphics device lost
- zero-copy disabled / bridge timeout / ABI mismatch
- unhandled `NullReferenceException`
- case controller error
- application quit before receipt

既知issueとして分離しcountする例:

- `Found unreferenced, but undisposed ComputeTensorData`
- `GarbageCollector disposing of ComputeBuffer`
- persistent native leak warning
- `d3d12: upload buffer was too small`

これらは現v42.3 Shadow A/Bでは`KNOWN_ISSUE`としてreportするが、ORT Authority adoption profileではshutdown stability gateをFAILさせる。profileごとにseverityを明示し、code側に例外を埋め込まない。

## 18. report

`report.json`は[KiwiValidationReport.schema.json](./KiwiValidationReport.schema.json)に対して検証してから`report.md`を生成する。Markdownをprimary dataにしない。

reportには最低限次を含める。

- Run/profile/version/tool/GPU/camera identity
- transaction stateとrollback evidence
- case別process lifecycle
- source/destination artifact path、SHA256、bytes
- metric value、statistic、sample count、threshold、status、evidence
- BASELINE/ZEROCOPY absolute値とdelta/ratio
- known issue / unknown error counts
- automated verdict / final verdict / blocking reasons
- visual review status

schema validation失敗時は`report.json`を`report.invalid.json`として保全し、Harness exitをnon-zeroにする。壊れたreportからMarkdown PASSを生成しない。

## 19. local / CI責務分離

### CIで可能

- PowerShell PSScriptAnalyzer/Pester
- JSON parse / JSON Schema validation
- analyzer fixture tests
- artifact completeness negative tests
- static Unity batch validation（license環境がある場合）
- build（camera/GPU runtimeとは別job）

### local interactive Windows runnerのみ

- physical camera preflight
- DX12 Player runtime
- GPU telemetry
- window capture
- BASELINE/ZEROCOPY A/B
- frame-by-frame visual review

CIがlocal runtime reportを受け取る場合、report schema、artifact SHA、runner labels、tool identityを再検証する。remote CIはcamera/GPU結果を再計算せず、署名なしreportを信用しない。

## 20. 実装順

1. version identity adapterとv39 stale namingの解消
2. diagnostic controller + tokenized receipts/filenames
3. single-case runner + process/crash/GPU monitor
4. artifact completeness
5. telemetry analyzer + known `ortMinusSentis` derivation
6. FFmpeg visual case + ffprobe QA
7. full orchestrator + transaction/rollback
8. JSON schema / Markdown generation
9. negative-path tests（missing CSV、early exit、wrong token、duplicate artifact、timeout、bad hash）
10. Unity static validation
11. v42.3 BASELINE 720P / ZEROCOPY 720P実測
12. threshold review
13. MP4 1-frame review

## 21. v1受入条件

- 1 commandで4 caseとreport生成まで進む
- required artifact欠落のfixtureが必ずNO_GO
- startup即死/ExitCode未確認が必ずNO_GO
- wrong token/duplicate artifactが必ずNO_GO
- PluginImporterはUnity APIだけで検証/変更
- transaction failureでrestore/hash verifyされる
- unknown concurrent editを上書きしない
- BASELINEとZEROCOPYの設定差が`KIWI_ORT_DML_ZERO_COPY_SHADOW`だけであることをenvironment snapshotで証明
- metrics caseにvideo encoder processが存在しない
- Zero-Copy parityとhost-tick derived latencyを計算
- report.jsonがschema PASS
- automated/final verdictの根拠が全てartifactへ辿れる
- Production挙動と保護対象codeが不変

## 22. 現時点の設計判断

Harness v1の設計は実装へ進める状態である。ただし現repoの`KiwiStandaloneBuildDiagnostic`にはv39 identityが残るため、version-consistent v42.3 Harnessは現状のままではfail-closedでNO_GOになる。これはfalse failureではなく、実装第1段階で直すべき契約不整合である。

