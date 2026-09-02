# LIVE CAMERA Direct Presentation Fix — Integrated Report

- 対象: `D:\KiwiAvatarSystem`
- 実施日: 2026-09-02 (Asia/Tokyo)
- 対象Unity: `6000.0.80f1`
- scope: LIVE CAMERA final UI presentationのpixel budget / aspect / layoutのみ
- 非scope: Native Camera、Tracking、Inference、color、filter、mipmap、shader、cadence、ROI、FaceTexture、Root/Head

## [EXECUTIVE_VERDICT]

**PRODUCTION ACCEPTED / UNITY COMPILE PASS / RUNTIME PASS / VISUAL PASS**

Production LIVE CAMERAの既定表示を、旧約`412x236`から`560x315`へ局所修正し、2026-09-02のUnity compile、Runtime gate、before/after全フレームvisual比較がすべてPASSした。`KiwiNativeCameraPresentation`を`DIRECT/OFF`で直接描く既存Texture path、mirror texcoords、filter/color、Native/Tracking/Inferenceは変更していない。

1920x1080 sourceでの新しい静的結果:

| metric | before Runtime | after Runtime |
|---|---:|---:|
| drawRect | `412x236` | `560x315` |
| drawAspect | `1.745762712` | `1.777777778` |
| sourceAspect | `1.777777778` | `1.777777778` |
| horizontal compression | `1.800847458%` | `0%` |
| source texels/display pixel X | `4.660194` | `3.428571` |
| source texels/display pixel Y | `4.576271` | `3.428571` |
| displayed pixel area | `97,232` | `176,400` (`+81.421754%`) |

初回実装時点ではUnity batchmodeがLicensing IPC timeoutでblocked、Runtimeはpendingだった。この旧状態は後段のCompile evidence boundaryへ履歴として保持する。その後、同日2026-09-02のUnity Editor runでTundra build successとRuntime fail-closed gateが成立し、別途実施されたbefore/after全フレーム解析もvisual PASSとなったため、Production採用gateを閉じた。

初回verdict（履歴）: **IMPLEMENTED / STATIC VALIDATION PASS / ROSLYN WHOLE-ASSEMBLY COMPILE PASS / UNITY BATCHMODE COMPILE BLOCKED BY LICENSING / RUNTIME PENDING**

## [PRODUCTION_AUTHORITY]

### Fail-closed SHA gate

変更前に実配置を直接hashし、次を確認した。

| file | expected SHA256 | actual before SHA256 | gate |
|---|---|---|---|
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiFrameComparisonOverlay.cs` | `4F5068749B56C17B80F67B6AFD21DF222365A6CF31C36F401466873F5A2140DF` | same | PASS |
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiCameraPreviewQualityService.cs` | `717C1B1106CEBC0D1E0C37F5A3899A1C3D67007EB7B61DACCE64A39C1336043A` | same | PASS |

SHA gate成立後にのみ編集した。

### Pre-fix Runtime authority accepted for this fix

依頼で確定済みとして提示されたRuntime Pixel-Space Audit事実をauthorityとした。

- source: `KiwiNativeCameraPresentation`, `1920x1080`
- `relation=DIRECT_SAME_TEXTURE`
- `previewMode=DIRECT`, `colorMode=OFF`
- service/observer added Blit 0、observer readback 0
- Bilinear、mip chainなし
- Production LIVE CAMERA約`412x236`
- sourceAspect `1.777778`、drawAspect約`1.745763`、約1.80%横圧縮
- same-source 1:1 cropはfull-frameより細部保持が大幅に良い
- Native / Tracking / Inferenceを変更する根拠なし

pre-fix local `Editor.log`のobserver行でも、same Texture ID、DIRECT/OFF、drawRect `412x236`、Blit/readback 0が一貫していた。

### Post-fix Production acceptance authority — 2026-09-02

Unity標準log `C:\Users\main\AppData\Local\Unity\Editor\Editor.log`（SHA256 `5C34E71E415C29CBC4A52696DA35E0D1107EB50D044BE8B7F50A7AB04AFF5E68`）を直接確認した。

- Unity engine: `6000.0.80f1`（line 88）
- `*** Tundra build success (8.24 seconds), 104 items updated, 654 evaluated`（line 372）
- `error CSxxxx`: 0件
- observer READY: `additionalBlit=0 additionalReadback=0 trackingWrite=0 cameraWrite=0 inferenceWrite=0 faceTextureTransactionWrite=0`（line 993）
- post-fix Runtime sample（line 1937以降、同一Texture IDで反復）:
  - source `KiwiNativeCameraPresentation`, `1920x1080`
  - `relation=DIRECT_SAME_TEXTURE`, `fullCropSameTextureObject=1`
  - `previewMode=DIRECT`, `colorMode=OFF`
  - `drawRectWidth=560`, `drawRectHeight=315`
  - `drawAspect=1.777778`, `sourceAspect=1.777778`
  - `horizontalScaleRatio=0.291667`, `verticalScaleRatio=0.291667`
  - `liveBlitsLastPrepare=0`, `observerBlit=0`, `observerReadback=0`
- color diagnostic（line 1923）: `addedLivePreviewBlit=0`, `trackingInputChanged=0`, `nativeDllChanged=0`
- 依頼者が同一camera/照明/距離のbefore/after MP4を全フレーム解析し、顔輪郭、眉、目、肌detail、背景detail、geometryの改善をPASSとして確定済み

このrunはPerformance正式測定には採用しない。

### Worktree boundary

開始時から存在した次のuser-owned stateを保存した。

- modified `.gitignore`
- untracked Pixel-Space observer / meta / prior audit report
- untracked `Tools/KlakSpout_v206_diag/`

初回実装作業ではinstall、commit、push、git add、checkout/reset/cleanを行っていなかった。本Production確定作業では指定された3 assetだけをlocal commit対象とし、`.gitignore`、本REPORT、`KiwiValidation`配下のMP4/log/csv/png/jpg、`Frames/`、`Library/`、`Temp/`、`UserSettings/`、`Tools/KlakSpout_v206_diag/`およびその他無関係なstateをstageしない。pushは行わない。

## [ROOT_CAUSE]

### Confirmed

旧layoutは次の異なるwidth authorityを持っていた。

1. `previewWidth=420`
2. panel request `width + 8`
3. panel確定後 `width = panel.width - 8`
4. final draw Rectでさらに `width - 8`
5. heightは最初の420幅から`420 / sourceAspect`で計算したまま

1920x1080では、実draw widthが412へ減る一方、integer draw heightは236となるため、`412 / 236 = 1.745762712`。sourceAspect `1.777777778`に対して横方向を`1.800847458%`圧縮した。

また、Bilinear/mipmapなしで1920x1080を約412x236へ縮小していた。Runtimeの同一Texture 1:1 cropで細部保持が大幅に良かったという証拠と一致するため、今回の最初の修正authorityはNative/Trackingではなく、final UIのpixel budgetとaspect/layoutである。

### Not claimed

- Bilinear自体を単独root causeとはしていない。
- Native presentation、camera focus、color transfer、Inference、Tracking mathをroot causeとはしていない。
- Point、sharpen、gamma correction、mipmap生成を必要とはしていない。

## [EXTERNAL_RESEARCH_APPLIED]

公式Unity 6.0資料だけを実装判断へ適用した。

1. Unity [`GUI.DrawTextureWithTexCoords`](https://docs.unity3d.com/ja/current/ScriptReference/GUI.DrawTextureWithTexCoords.html) は、第1`Rect`をTextureを描くscreen pixel rectangle、第2`Rect`をnormalized texture coordinatesとして定義する。したがって、source textureやtexcoordsを変えず、final position Rectのpixel budget/aspectだけを直すのが最小変更。
2. Unity [`Rect`](https://docs.unity3d.com/cn/6000.0/ScriptReference/Rect.html) / [`Rect.width`](https://docs.unity3d.com/cn/6000.0/ScriptReference/Rect-width.html) はwidth/heightをpositionから測る矩形sizeとして定義する。panel paddingとcontent widthを分離し、content widthをfinal draw authorityにした。
3. Unity [`Mathf.Floor`](https://docs.unity3d.com/cn/current/ScriptReference/Mathf.Floor.html) と`Mathf.Round`のnearest-integer semanticsを使い、利用可能領域を越えない整数widthと、最小誤差のinteger aspect heightを算出した。

外部方式からfilter、RT、shader、mipmap、color変更は採用していない。Runtime evidenceがまずpixel budget/layoutを指しているためである。

## [IMPLEMENTATION]

### `KiwiFrameComparisonOverlay.cs`

局所変更:

- `previewWidth` default: `420f` → existing range上限の`560f`
- existing rangeを`MinPreviewWidth=260f` / `MaxPreviewWidth=560f`へ定数化
- horizontal padding authorityを`PanelHorizontalPadding=8f`へ定数化
- panel requested widthを`desiredPreviewWidth + 2 * padding`へ変更
- panel確定後のcontent widthを`panel.width - 2 * padding`から一度だけ算出
- integer-aligned `finalPreviewWidth`を`Floor`、heightを`Round(width/sourceAspect)`で算出
- short-window時もavailable heightからwidthを再算出し、aspectを保持
- LIVE CAMERA、matched debug、title/footer widthは同じ`finalPreviewWidth`を使用
- `GetLivePreviewOrSource`、`GUI.DrawTextureWithTexCoords`、texture texcoordsは不変

通常の1920x1080 source / unconstrained 1920x1080 Game view計算:

```text
desiredPreviewWidth = 560
sourceAspect = 1920 / 1080 = 1.7777777777777777
desiredPreviewHeight = round(560 / sourceAspect) = 315
panelWidth = 560 + 8 + 8 = 576
panelHeight = 28 + 315 + 26 + 315 + 142 = 826
availablePreviewWidth = 576 - 16 = 560
availablePreviewHeightPerView = (826 - 28 - 26 - 142) / 2 = 315
finalPreviewRect = 560 x 315
```

### `KiwiLiveCameraPixelSpaceAudit.cs`

このobserverは本作業前から存在するuntracked opt-in診断。Production修正後に旧412x236を「current Production nominal」として誤報しないため、診断計算だけを追随させた。

- fallback planning width: `420` → `560`
- old `planningWidth - 8`再現を削除
- height roundingをProductionと同じnearest integerへ変更
- `KIWI_LIVE_CAMERA_PIXEL_SPACE_AUDIT=1` opt-inは不変
- observer Blit/readback/RTは引き続き0

### Explicitly unchanged

- `KiwiCameraPreviewQualityService`のDIRECT/OFF挙動
- `KiwiNativeCameraPresentation` identity / source path
- `GetCameraPreviewTexCoords()`と`mirrorX ? Rect(1,0,-1,1) : Rect(0,0,1,1)`
- LIVE CAMERAのfilterMode / mipmap / color correction / shader
- Native DLL/source、WindowsNativeWebCamSource
- FaceLandmarkerRunner、KiwiFaceMotion、KiwiInferenceFaceTracker
- ROI、Inference cadence、Tracking threshold、FaceTexture transaction、Root/Head authority

## [CHANGED_FILES]

### Code changed by this task

1. `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiFrameComparisonOverlay.cs`
2. `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiLiveCameraPixelSpaceAudit.cs` — pre-existing untracked opt-in observer; diagnostic values only
3. `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiLiveCameraPixelSpaceAudit.cs.meta` — observerのstable Unity asset GUIDを保持する再現可能性資産

### Report / validation artifacts

4. `KiwiValidation/LIVE_CAMERA_DIRECT_PRESENTATION_FIX_REPORT_20260902.md` — this report; commit対象外
5. `KiwiValidation/KiwiLiveCameraDirectPresentationFix_UnityCompile.log` — licensing-blocked attempt; commit対象外
6. `KiwiValidation/KiwiLiveCameraDirectPresentationFix_UnityCompile_Retry.log` — licensing-blocked retry; commit対象外

observer `.meta`はコード変更ではないが、新規observerを同じGUIDで再現可能にするためcommit対象に含める。Production install/pushは実施しない。

### Local Production commit — 2026-09-02

- branch: `main`
- commit: `5e083e2801ae9ccedea3bcc2645e02d5bd4c5d39`
- message: `fix(live-camera): correct direct preview aspect and pixel budget`
- committed files: 上記3 assetのみ
- forbidden committed files: 0
- commit後staged files: 0
- upstream relation（network fetchなしのlocal ref比較）: `origin/main`に対してahead 12 / behind 0
- push: 未実施

commit対象外として保存した最終worktree state:

```text
 M .gitignore
?? KiwiValidation/LIVE_CAMERA_DIRECT_PRESENTATION_FIX_REPORT_20260902.md
?? KiwiValidation/LIVE_CAMERA_PRESENTATION_PIXEL_SPACE_AUDIT_REPORT_20260902.md
?? Tools/KlakSpout_v206_diag/
```

MP4/log/csv/png/jpg、`Frames/`、`Library/`、`Temp/`、`UserSettings/`はcommitされていない。既存ignore対象のRuntime evidenceもstageされていない。

## [STATIC_VALIDATION]

| gate | result | evidence |
|---|---|---|
| before SHA authority | PASS | overlay/serviceとも指定SHA一致後に編集 |
| changed Production scope | PASS | tracked code差分はoverlayだけ。ambient `.gitignore`は未変更 |
| target size math | PASS | `560x315` |
| drawAspect | PASS | `560/315 = 1.7777777777777777` |
| absolute aspect error | PASS | `0` |
| relative aspect error | PASS | `0%` |
| X/Y scale equality | PASS | both `0.2916666666666667` |
| source texels/display pixel | PASS | X/Y both `3.4285714285714284` |
| mirrorX | PASS unchanged | texcoord method/data unchanged |
| DIRECT/OFF service | PASS unchanged static | service SHA unchanged |
| additional Graphics.Blit | PASS: `0` | no added call |
| additional RenderTexture | PASS: `0` | no added construction/allocation |
| readback | PASS: `0` | no AsyncGPUReadback/ReadPixels/GetPixels addition |
| blocking wait | PASS: `0` | no wait/sleep/fence addition |
| per-frame managed allocation | PASS: no new reference allocation | added calculations are float math; `Rect` remains value type and draw/label count is unchanged |
| filter/mipmap/shader/color | PASS unchanged | no added-line mutation; service/shader/source hashes unchanged |
| critical SHA | PASS unchanged | table below |
| `git diff --check` | PASS | whitespace/error outputなし |
| whole Assembly-CSharp Roslyn compile | PASS with pre-existing warnings | Unity 6000.0.80f1 Bee response + bundled DotNetSdkRoslyn, exit `0` |
| Unity 6000.0.80f1 compile | **PASS** | 後続Editor runでengine version確認、Tundra success、C# error 0件。初回batchmode 2 attemptsのLicensing blockは履歴として保持 |
| Runtime fail-closed gate | **PASS** | `560x315`, exact aspect, same Texture, DIRECT/OFF, added Blit/readback 0, tracking input unchanged |
| Runtime visual | **PASS** | 依頼者確定のbefore/after全フレーム解析。Performance正式測定には不採用 |

### Compile evidence boundary — initial blocked attempts retained as history

Unity batchmodeを通常D3D12と`-nographics`で各1回実行した。両方で次が再現した。

- `Licensing initialization failed after 74.8x s`
- `connection with the Unity Licensing Client has been lost`
- LicensingClient relaunch後もcompileへ進まない
- `Tundra build success`なし
- `Exiting batchmode successfully`なし

この初回時点ではUnity compile PASSにはしていなかった。停止したのは各attemptで起動したUnity processだけで、既存LicensingClientは終了していない。

補助gateとして、Unity 6000.0.80f1が生成済みの`Library/Bee/artifacts/1900b0aE.dag/Assembly-CSharp.rsp`（変更したoverlay/observer両方をsource listに含む）を、同Unity installationの`Data/DotNetSdkRoslyn/csc.dll`でcompileした。exit `0`、output SHA256 `9E0AFE267B82A2C2CE62D31E609517306351442B4C272F7B96FAEFF596DCBF68`。既存source generator warningおよび既存obsolete/unused warningsはあるが、変更fileのcompile errorはない。

このRoslyn gateはC# whole-file/assembly整合性の証拠であり、単独ではUnity import/lifecycle compile成功の代用ではなかった。その後の標準`Editor.log`でUnity 6000.0.80f1 / Tundra success / C# error 0件が確認され、この不足は2026-09-02に解消した。

## [SHA256]

### Changed code

| file | before | after |
|---|---|---|
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiFrameComparisonOverlay.cs` | `4F5068749B56C17B80F67B6AFD21DF222365A6CF31C36F401466873F5A2140DF` | `6DF8CCC9B47A0E2E9BFF4807B4D7CAB4AB11911992A4624EF7A8E40436F39EDD` |
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiLiveCameraPixelSpaceAudit.cs` | `C39EC230925AF830B70746DC60F73311EF9FBE8AA63BE239686A7BAFE4213A82` | `AB41E4F296041DEB6BA800D62BCBE992048CE5CFB1E5AE2B7A82E1BA59EA1602` |
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiLiveCameraPixelSpaceAudit.cs.meta` | untracked | `6BADE5DC8E2524B6EF0E289146789868DFFC7D56720F05712596A3779981A3DC` |

### Unchanged Production / critical files

| file | before/after SHA256 | result |
|---|---|---|
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiCameraPreviewQualityService.cs` | `717C1B1106CEBC0D1E0C37F5A3899A1C3D67007EB7B61DACCE64A39C1336043A` | UNCHANGED |
| `Assets/Script/KiwiInferenceFaceTracker.cs` | `52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695` | UNCHANGED |
| `Assets/Script/FaceLandmarkerRunner.cs` | `6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93` | UNCHANGED |
| `Assets/KiwiAvatarSystem/Runtime/Camera/KiwiNativeCameraInterop.cs` | `AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5` | UNCHANGED |
| `Assets/Plugins/x86_64/KiwiNativeCamera.dll` | `82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5` | UNCHANGED |
| `Native/KiwiNativeCamera/Source/KiwiNativeCameraPlugin.cpp` | `636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A` | UNCHANGED |
| `Assets/Script/KiwiFaceMotion.cs` | `D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6` | UNCHANGED |
| `Assets/KiwiAvatarSystem/Runtime/Tracking/KiwiInferenceFaceTracker.cs` | `EFFCCCF5EE1BF407F065BFA95B491390AC96A55E9A75290A67C5AFAD32AFC1F1` | UNCHANGED |
| `Assets/KiwiAvatarSystem/Runtime/Camera/WindowsNativeWebCamSource.cs` | `D0DD9A78FA57AAB51D3E0C8606844C44F0207DC87277D0B26AEF3B43EF353533` | UNCHANGED |

### Compile logs

| file | SHA256 | verdict |
|---|---|---|
| `KiwiValidation/KiwiLiveCameraDirectPresentationFix_UnityCompile.log` | `88D2CD4F2B7A153251D5F272B314281ABA864D44C49C2DF8415E057B565729BC` | Licensing blocked; not compile PASS |
| `KiwiValidation/KiwiLiveCameraDirectPresentationFix_UnityCompile_Retry.log` | `1D7ADD33749B95D8F54C6A60598DA25699E8593C55B8BC028F9CF6D94BBFCE67` | Licensing blocked; not compile PASS |
| `C:\Users\main\AppData\Local\Unity\Editor\Editor.log` | `5C34E71E415C29CBC4A52696DA35E0D1107EB50D044BE8B7F50A7AB04AFF5E68` | later Unity 6000.0.80f1 Tundra/Runtime authority; PASS |

本REPORT自身のSHA256は自己参照を避け、finalize後のdelivery responseで提示する。

## [REGRESSION_RISK]

1. **UI footprint**: preview面積は81.42%増え、2段のLIVE/MATCHED panel高さは既定826pxになる。1920x1080では既存non-overlap計算内だが、狭い/短いGame viewではemergency fallbackにより560x315未満へ縮小される。
2. **Other diagnostics visibility**: panel widthは576px。telemetryとの重なり回避は既存`CalculateNonOverlappingPanelRect`を維持するが、左laneの他UIを覆う可能性はRuntime visualで確認する。
3. **Integer rounding on non-16:9 sources**: widthを利用可能範囲以下へFloor、heightをnearest integerへRoundするため、任意aspectでは最大sub-pixel相当のaspect誤差が残り得る。1920x1080→560x315は誤差0。
4. **High-DPI/editor capture**: Unity GUI Rectと録画fileの物理pixelがEditor scale/DPIで一致しない場合がある。Runtime logのRect値とMP4上の測定を区別する。
5. **Observer pair width**: opt-in A/B observerは2枚を横並びするため、Screen widthが約1146px未満ならobserver自身の比較Rectは560未満になる。Production overlayのactual Rectとは別に記録する。
6. **Compile history**: 初回batchmodeはLicensingでblockedした。後続Editor runでTundra successとC# error 0件を確認して採用gateは閉じたが、初回blocked log自体はPASSへ読み替えない。
7. **Visual evidence boundary**: before/after全フレームvisual比較はPASS済み。ただしobserver runの数値をPerformance正式測定へ流用しない。

## [RUNTIME_TEST_INSTRUCTIONS]

以下はProduction採用後の再検証・回帰確認手順として保持する。2026-09-02採用runでは同じgateが成立済み。

### 0. Unity compile gateを再確認する

Unity Licensing Clientが正常接続できる状態で、Unity 6000.0.80f1を再起動する。Consoleにcompile errorがなく、batchmodeを使う場合はprocess exit `0`、`Tundra build success`、`Exiting batchmode successfully`をすべて確認する。Licensing failureをcompile PASSへ読み替えない。

### 1. Baseline mode

PowerShell 5.1互換:

```powershell
Remove-Item Env:KIWI_CAMERA_PREVIEW_MODE -ErrorAction SilentlyContinue
Remove-Item Env:KIWI_LIVE_CAMERA_COLOR_CORRECTION -ErrorAction SilentlyContinue
$env:KIWI_LIVE_CAMERA_PIXEL_SPACE_AUDIT = '1'
```

この環境を継承するUnity EditorまたはDevelopment Playerを新規起動する。Native DLL/sourceはbuild/replacementしない。

### 2. Fail-closed log gate

次を同一runで確認する。

- source `KiwiNativeCameraPresentation`, `1920x1080`
- source/comparison Texture ID一致
- `relation=DIRECT_SAME_TEXTURE`
- `previewMode=DIRECT`
- `colorMode=OFF`
- `liveBlitsLastPrepare=0`
- `observerBlit=0 observerReadback=0`
- `fullCropSameTextureObject=1`
- `productionPlanningWidth=560.00`
- `productionDesiredHeight=315.00`
- `productionNominalDrawWidth=560.00`
- unconstrained 1920x1080 Game viewで`drawRectWidth=560`, `drawRectHeight=315`
- `drawAspect=1.777778`, `sourceAspect=1.777778`
- `horizontalScaleRatio=0.291667`, `verticalScaleRatio=0.291667`
- color diagnosticで`addedLivePreviewBlit=0`, `trackingInputChanged=0`

1つでも不成立ならvisual PASSを出さず、actual Screen size、environment、source identity、panel fallbackを記録する。

### 3. MP4 capture

- 同一camera、照明、距離、camera設定、Game view size
- 15〜30秒
- 静止区間と小さいhead movement区間
- Production LIVE CAMERA全体と必要なdiagnostic textを同時収録
- performance正式測定には使用しない

before authority:

`KiwiValidation/LiveCameraPixelAudit/live_camera_audit.mp4`

after MP4/logはcase tokenを一致させ、元before MP4を変更しない。

### 4. Frame-level visual comparison

全フレームを走査し、motion blurの強いframeをsharpness authorityから除外したうえで、最低10のstationary/fine-detail representative framesをbefore/afterで対応させる。

確認対象:

- 顔輪郭とgeometry: 横圧縮が消え、頭部/頬/耳の比率が自然か
- 眉、睫毛、目輪郭: full-frameで細線保持が改善したか
- 肌detail: oversharpenなしにtextureが増えたか
- 髪/衣服edge: aliasing、moire、ringingが増えていないか
- 背景detail: curtain/bookshelf/寝具などの高周波detailが改善したか
- color/gamma: OFF pathのままbeforeから意図しない差がないか

### 5. Runtime acceptance

PASSには次のすべてを要求する。

1. actual LIVE CAMERA Rect `560x315`相当
2. source/draw aspect一致、横圧縮消失
3. DIRECT/OFF、追加preview Blit 0
4. `trackingInputChanged=0`
5. beforeより顔/眉/目/肌/背景detailが一貫して改善
6. aliasing/moire/geometry/UI overlapの重大回帰なし
7. Native/Tracking/Inference critical SHA不変

2026-09-02採用runでは上記Runtime証拠とvisual証拠が揃い、Production visual PASSへ昇格済み。将来の再検証で1つでも不成立なら、そのrunだけをfail-closedでINVALIDとし、本採用時証拠と混同しない。
