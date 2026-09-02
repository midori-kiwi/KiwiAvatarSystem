# LIVE CAMERA Presentation Pixel-Space Audit — Integrated Report

- 対象: `D:\KiwiAvatarSystem`
- 監査日: 2026-09-02 (Asia/Tokyo)
- Unity: `6000.0.80f1`
- Production authority: 実配置 → 最新Production/Runtime証拠 → GitHub main → 過去版
- 結論: **Production画質FIXは未実施。既存表示を変更しないopt-in observerのみ追加。静的監査とUnity batchmode compileはPASS、pixel-space Runtime MP4判定は未実施のため画質原因の最終判定はPENDING。**

## [CONFIRMED_FACTS]

### 1. Production authorityと実配置

| 項目 | 実配置 / 証拠 | 確認結果 |
|---|---|---|
| LIVE CAMERA overlay | `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiFrameComparisonOverlay.cs` | SHA256 `4F5068749B56C17B80F67B6AFD21DF222365A6CF31C36F401466873F5A2140DF` |
| preview quality service | `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiCameraPreviewQualityService.cs` | SHA256 `717C1B1106CEBC0D1E0C37F5A3899A1C3D67007EB7B61DACCE64A39C1336043A` |
| 既存texture diagnostic | `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiLiveCameraPathDiagnosticV44_23.cs` | SHA256 `5C1ACFB793D63570E54FD91B080D235F21843EEEA1FC9B2F408DCF0B9BF4DE31` |
| Production Native DLL | `Assets/Plugins/x86_64/KiwiNativeCamera.dll` | SHA256 `82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5` |
| Native source | `Native/KiwiNativeCamera/Source/KiwiNativeCameraPlugin.cpp` | SHA256 `636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A` |
| 最新関連Runtime証拠 | `KiwiValidation/KiwiV44_55_15_Editor.log` | SHA256 `24AA56E442F590D976C29986B30582AB86DB88D322A95616B9B48FF3D0DBC814` |

Git authorityは、local `main` HEAD `fadfe5d764cb71cd51b18170eac18bdb868c49ae`、取得時の `origin/main` HEAD `102934e6b6d1347fd4696297b33a1a99691432df`、localは11 commits ahead / 0 behindだった。公開 [GitHub main](https://github.com/midori-kiwi/KiwiAvatarSystem) には現在のoverlay、quality service、Windows native camera実装が存在せず、今回の判断では実配置および最新Runtimeよりauthorityが低い。

### 2. LIVE CAMERA sourceから最終drawまで

確認できた実経路は次のとおり。

1. `Assets/Script/FaceLandmarkerRunner.cs:1495` 付近でProduction `ImageSource`を取得し、`Assets/Script/FaceLandmarkerRunner.cs:1615` でMediaPipe `Screen.Initialize(imageSource)`を呼ぶ。
2. `Assets/MediaPipeUnity/Samples/Common/Scripts/Screen.cs:29-36` は `imageSource.GetCurrentTexture()`を既存Screen/RawImageへ設定する。
3. scene `Assets/Scenes/Face Landmark Detection.unity:1966` の `FacePartCropper.sourceImage` は同じScreen/RawImageを参照する。sceneの `mirrorX: 1` は同scene `:1970`。
4. Native経路の `Assets/KiwiAvatarSystem/Runtime/Camera/WindowsNativeWebCamSource.cs:1015-1023` はframe prepare後に `_nativeTexture` を返す。native presentation作成は同file `:1146-1177`、名前は `KiwiNativeCameraPresentation`。
5. `KiwiFrameComparisonOverlay.GetSourceTexture()` (`:821-827`) は `_cropper.sourceImage.texture`を読む。
6. overlayは `KiwiCameraPreviewQualityService.GetLivePreviewOrSource(...)` (`:964-969`) を通したTextureを `GUI.DrawTextureWithTexCoords` (`:1107-1125`) で描く。mirror texcoordは `:1165-1175`。

したがって、Native Production経路ではLIVE CAMERA sourceは固定identityのpresentation `RenderTexture` `KiwiNativeCameraPresentation`である。最新関連Runtime logにも `source=KiwiNativeCameraPresentation size=1920x1080`、`preview mode=DIRECT`、color correction `mode=OFF`、`addedLivePreviewBlit=0`、`trackingInputChanged=0` が残る。ただし、そのrunには今回のsame-object比較observerがないため、**現在の起動runでsource/comparison object identityが同一であることは新observer logで再確認する必要がある。**

### 3. Texture属性: 静的に確定した構成とRuntimeで再確認する値

Native wrapperは各external slotを `Texture2D.CreateExternalTexture(1920, 1080, RGBA32, mipChain:false, linear:true, ...)` として作り (`WindowsNativeWebCamSource.cs:1078`)、縦orientation正規化時に既存 `Graphics.Blit` で固定presentation RTへ転送する (`:1091-1109`)。presentation RTは次の構成 (`:1161-1176`)。

- `RenderTextureFormat.ARGB32`
- `RenderTextureReadWrite.Linear`
- name `KiwiNativeCameraPresentation`
- `filterMode = Bilinear`
- `wrapMode = Clamp`
- `useMipMap = false`
- `autoGenerateMips = false`

ProjectSettingsの `m_ActiveColorSpace: 1` (`ProjectSettings/ProjectSettings.asset:50`) はこのcheckoutのLinear設定である。最新Runtimeでsource size `1920x1080` とnameは確認済み。一方、実行時の正確な `Texture.dimension`、`graphicsFormat`、`mipmapCount`、`isDataSRGB` は、今回のobserverがactive objectから直接記録するまでRuntime確定とはしない。

`activeTextureColorSpace` は架空のmutable stateとして扱わず、observerは `texture.graphicsFormat` に対する `GraphicsFormatUtility.IsSRGBFormat` の結果、`texture.isDataSRGB`、および `QualitySettings.activeColorSpace` を別々に表示する。

### 4. previewWidth / panel / drawRect authority不一致

`KiwiFrameComparisonOverlay.cs` の既定 `previewWidth=420` (`:44`) に対し、layoutは次の順で計算される。

1. planning `width = 420` (`:903`)
2. `desiredPreviewHeight = width / sourceAspect` (`:909-914`)
3. panel widthを `width + 8` として確保 (`:915-923`)
4. panel確定後 `width = panel.width - 8` (`:931`)
5. 実LIVE CAMERA draw幅はさらに `width - 8` (`:950`, `:964-969`)

通常ウィンドウ、1920x1080 sourceの数値は以下。

| metric | 値 |
|---|---:|
| sourceAspect | `1920 / 1080 = 1.777777778` |
| desiredPreviewHeight | `420 / 1.777777778 = 236.25 px` |
| actual nominal draw width | `420 - 8 = 412 px` |
| actual nominal drawAspect | `412 / 236.25 = 1.743915344` |
| horizontalScaleRatio | `412 / 1920 = 0.214583333` |
| verticalScaleRatio | `236.25 / 1080 = 0.218750000` |
| drawAspect / sourceAspect | `0.980952381` |
| source texels / draw pixel | X `4.660194` / Y `4.571429` |

よって、**width/height authority不一致と約1.9048%の横圧縮は静的に確認済み**。ただし、それが知覚上の主因かどうかはMP4未確認であり、今回は修正していない。緊急panel縮小時は別のclampが入るため、実runのdrawRect値をobserver logで採る。

### 5. 変更禁止境界

以下はbefore/after SHA一致を確認し、変更していない。

- `Assets/Script/KiwiFaceMotion.cs`
- `Assets/Script/KiwiInferenceFaceTracker.cs`
- `Assets/KiwiAvatarSystem/Runtime/Tracking/KiwiInferenceFaceTracker.cs`
- `Assets/Script/FaceLandmarkerRunner.cs`
- `Assets/KiwiAvatarSystem/Runtime/Camera/WindowsNativeWebCamSource.cs`
- `Assets/KiwiAvatarSystem/Runtime/Camera/KiwiNativeCameraInterop.cs`
- Production Native DLL/source
- scene、ProjectSettings、overlay、quality service、metadata correction shader

camera cadence、Inference、ROI、FaceTexture transaction、Root/Head authorityへの変更は0。

## [PUBLIC_DESIGN_PRINCIPLES]

- Unityの [`GUI.DrawTextureWithTexCoords`](https://docs.unity3d.com/ja/current/ScriptReference/GUI.DrawTextureWithTexCoords.html) はpixel-spaceのposition Rectとnormalized texcoordsでTextureをscale/cropできる。したがって追加RT/Blitなしにsame Textureのfull-frameとcenter cropを比較できる。
- Unityの [`FilterMode`](https://docs.unity3d.com/kr/6000.0/ScriptReference/FilterMode.html) ではBilinearは近傍texelを平均化する。Unityは[mipmapがなければTrilinearはBilinearと同等](https://docs.unity3d.com/jp/current/ScriptReference/FilterMode.Trilinear.html)とも明記する。よって、mipmapなしで約4.6 texels/pixelへ縮小される現経路はsampling仮説を検証すべき条件だが、これだけで視覚的原因を確定しない。
- Unityの [`RenderTextureReadWrite`](https://docs.unity3d.com/ja/current/ScriptReference/RenderTextureReadWrite.html)、[`Texture.isDataSRGB`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture-isDataSRGB.html)、[`CreateExternalTexture`](https://docs.unity3d.com/ja/current/ScriptReference/Texture2D.CreateExternalTexture.html) はGPU formatとsampling color interpretationを区別する根拠になる。
- Microsoftの [Extended Color Information](https://learn.microsoft.com/en-us/windows/win32/medfound/extended-color-information) はYUV matrix、nominal range、transfer functionを別属性として扱う。[MFVideoTransferFunction](https://learn.microsoft.com/en-us/windows/win32/api/mfobjects/ne-mfobjects-mfvideotransferfunction) もtransfer functionを独立定義する。このためrange/matrixだけの確認でLinear/sRGB interpretationまでPASSにはできない。
- 最重要比較対象のWebcam Motion Captureについて、公開サイト/GitHubからcurrent LIVE CAMERA pixel presentation sourceを確認できなかった。非公開実装のsampling、color pipeline、RT identityを事実として推定しない。

## [EXISTING_DIAGNOSTICS]

Productionコード変更前に、現配置、history/backups、runtime logs、GitHub mainを検索した。

### 既に確認されていた問い

1. `KiwiLiveCameraPathDiagnosticV44_23.cs` はsource/preview relation、Texture name/id/size/type、filter/wrap/aniso/mips、RT format/graphicsFormat/sRGB/dimension/createを出せる。
2. `KiwiCameraPreviewQualityService.cs` のv28/v29系は `DIRECT / SINGLE / STAGED` のpreview-only A/B/Cと追加Blit数を持つ。
3. v44.29はmetadata correctionをpreview-only optionとして導入し、v44.31 historyはcorrected outputのsRGB RT/graphics formatを明示した。
4. current serviceの既定は `DIRECT` とcolor correction `OFF`。最新関連Runtime logも `DIRECT/OFF`、added live preview Blit 0だった。
5. current Native sourceはnegotiated media typeからnominal range、YUV matrix、transfer function metadataを読む。色定数作成はrange/matrixを使用する。

### 未検証だった問い

- 同一Texture object・同一OnGUI repaintからのfull-frameと1:1 center texel cropの同時肉眼比較
- actual drawRect width/height/aspectとsource width/height/aspectの同時記録
- horizontal / vertical scale ratioの同時記録
- current runで `sourceTexture == comparisonTexture` を画面とlogで明示すること
- current active Textureのformat/color/mipmapと上記pixel comparisonを同じ時点で相関すること

repository内に `1:1 texel`、same-object center crop、上記比率を揃えた既存診断は見つからなかった。この差分だけが新observer追加の根拠である。

### v44.29 / v44.31の扱い

- shader/history commentでは、range/matrix補正とLinear/sRGB interpretationの試行錯誤が記録されている。
- それらはpreview-only diagnostic historyであり、current Native側には後続のmetadata-driven range/matrix処理がある。
- current Runtimeは補正OFF。v44.29/v44.31を自動有効化する根拠にはしない。
- `Validated Decisions` / `Rejected Approaches` のv44.29/v44.31専用恒久文書は現checkout内で確認できなかった。source comments、backups、runtime logsを存在する証拠として扱い、未発見の正式decisionを捏造しない。

### 今回採らなかった方式

- Production layoutの同時修正
- quality serviceの作り直し
- METADATA/SINGLE/STAGEDの自動有効化
- new camera frame、Inference input、snapshot生成
- observer用RenderTexture、Material、Blit、GPU readback、blocking wait
- Native DLL/source変更またはrebuild
- observer runからの正式performance値採用

## [HYPOTHESES]

| 仮説 | 現時点の評価 | 確認済み根拠 | 未確定点 / 判定gate |
|---|---|---|---|
| A: source自体の画質問題 | OPEN | runtime sourceは1920x1080 `KiwiNativeCameraPresentation`。Native以前のfocus/exposure/compressionやNative presentation入力品質はpixel-space static auditだけでは判定不能。 | 1:1 cropもfullと同程度に劣化するか。元camera設定と同一条件のMP4が必要。 |
| B: UI minification/sampling | **有力候補、未確定** | 1920x1080を名目412x236へ縮小、X/Y約4.66/4.57 texels/pixel、Bilinear、mipmapなし。 | 1:1 cropのみ明瞭になるか。肉眼・frame確認が必要。 |
| C: aspect/layout distortion | **計算不一致はCONFIRMED、主因性はOPEN** | 1.777778 sourceに対し名目drawAspect 1.743915、約1.9048%横圧縮。 | geometryだけに違和感が集中するか。実run rectとMP4が必要。 |
| D: color-space/format interpretation | OPEN、再監査価値あり | Project/presentationはLinear構成。Nativeはrange/matrix metadataを使う一方、transfer metadataは取得しても色定数経路で使用していない。色変換後の非線形RGB/Linear解釈は別gate。 | observerのgraphicsFormat/isDataSRGB/activeColorSpaceと、full/cropで同一の色・shadow差を記録。sharpness判定とは分離。 |
| E: quality service等の中間presentation | current `DIRECT/OFF`では低い。ただしruntime確認待ち | latest logはDIRECT/OFF、added live preview Blit 0。static current defaultも同じ。 | observerの `relation=DIRECT_SAME_TEXTURE`、同一ID、`liveBlitsLastPrepare=0` を必須gateにする。異なればbaseline runを不成立とする。 |

最善の診断案は、Production fixを入れず、current sourceとservice-selected Textureを読み、同じlocal Texture referenceを同じrepaint内でfull-frameと1:1 cropへ直接IMGUI drawするobserverである。

## [IMPLEMENTATION]

追加した唯一のcodeは:

`Assets/KiwiAvatarSystem/Runtime/Validation/KiwiLiveCameraPixelSpaceAudit.cs`

### 起動条件

- opt-in環境変数: `KIWI_LIVE_CAMERA_PIXEL_SPACE_AUDIT=1`
- unset/0時はGameObjectもComponentも生成せず、Production動作に影響しない。
- `RuntimeInitializeOnLoadMethod(AfterSceneLoad)`で1 instanceのみ作成。

### 診断A / B

- A: current service-selected LIVE CAMERA Textureのfull-frame。既存overlayは無変更で残る。
- B: Aと同じlocal `comparisonTexture` reference、同じOnGUI repaint、center integer texel crop。
- crop destinationのscreen pixel width/heightとsource texel width/heightを一致させるため1:1 texel相当。
- A/Bとも既存 `FacePartCropper.mirrorX` と同じnormalized texcoord orientation。
- `GUI.DrawTextureWithTexCoords`を直接使用。observer RT/Blit/readbackは0。

### 画面 / log metric

最低要求された全項目を出す。

- `sourceTextureId`
- `sourceName`
- `sourceWidth` / `sourceHeight`
- `format` / `graphicsFormat` / `dimension`
- `filterMode` / `mipmapCount` / `wrapMode`
- `activeTextureColorSpace`
- `QualitySettings.activeColorSpace`
- `sourceIsDataSRGB`
- `drawRectWidth` / `drawRectHeight` / `drawAspect`
- `sourceAspect`
- `horizontalScaleRatio` / `verticalScaleRatio`

追加相関値:

- `comparisonTextureId`
- `relation=DIRECT_SAME_TEXTURE|INTERMEDIATE_TEXTURE`
- `comparisonSize/Format/ColorSpace`
- `centerCropTexels=x/y/w/h`
- `mirrorX`
- `fullCropSameTextureObject=1`
- `previewMode` / `colorMode`
- `liveBlitsLastPrepare`
- `observerBlit=0` / `observerReadback=0`

## [STATIC_VALIDATION]

| gate | 結果 | 証拠 / 注記 |
|---|---|---|
| changed code scope | PASS | 新規observer `.cs` + `.meta`のみ。既存Production code差分0。 |
| whole-file compile整合性 | PASS | Unity `6000.0.80f1` batchmode compile: Tundra build success、exit code 0。 |
| Unity API / namespace | PASS | `UnityEngine`, `UnityEngine.Experimental.Rendering`; `FacePartCropper`、overlay、serviceと同じglobal assembly/accessibilityでcompile成功。 |
| lifecycle | PASS | one static instance、`DontDestroyOnLoad`、duplicate destroy、`OnDestroy`でstatic clear。RT/Material/Texture native resourceなし。 |
| static state | PASS | observer自身のinstanceだけ。Production static/stateへのwriteなし。 |
| allocation | CONDITIONAL PASS | enabled時のみGameObject + Component + 2 GUIStyle。metrics string/logは2秒ごと。毎repaintのnew RT/Texture/arrayなし。IMGUI自体のobserver overheadがあるためperformance authorityにはしない。 |
| additional Blit | PASS: 0 | observer fileに `Graphics.Blit` callなし。Production既存Native presentation Blitは変更なし。 |
| GPU/CPU readback | PASS: 0 | `ReadPixels`, `AsyncGPUReadback`, texture data accessなし。 |
| blocking wait | PASS: 0 | fence/wait/task blockingなし。 |
| camera/inference sample generation | PASS: 0 | `GetCurrentTexture`, frame prepare、snapshot、Inference invokeなし。既にRawImageへbindingされたTextureを読むだけ。 |
| Tracking/Inference/Native write | PASS: 0 | observer自身のUI/cache以外へのassignment/API writeなし。 |
| Native DLL/source | PASS unchanged | before/after SHA一致。build/replacementなし。 |
| critical Production files | PASS unchanged | 下記SHA tableのbefore/after一致。 |
| runtime pixel-space result | **NOT RUN / PENDING** | Compile-onlyで原因PASS/FAILを代用しない。MP4 + observer logが必要。 |

Batchmode command:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.0.80f1\Editor\Unity.exe' `
  -batchmode -quit `
  -projectPath 'D:\KiwiAvatarSystem' `
  -logFile 'D:\KiwiAvatarSystem\KiwiValidation\KiwiLiveCameraPixelSpaceAudit_UnityCompile.log'
```

compile log SHA256: `D77A8990F7E56816B4173361869ECD0C0229871F3B699839395CE652F947014C`。

既存warningはあるが、新observer由来のC# error/warningはなく、`*** Tundra build success`、`Exiting batchmode successfully now!`、process exit 0を確認した。

## [REGRESSION_RISKS]

1. **observer overlap**: Game viewが狭いと既存overlayや他observerと画面が重なる。Production表示そのものは変更しないが、MP4の視認性が落ちる。1280x720以上を推奨。
2. **IMGUI/log overhead**: 2 draw calls、labels、2秒ごとのstring/log allocationがある。correctness observer専用で、FPS/latency/cadenceの正式測定値を採用しない。
3. **environment contamination**: `KIWI_CAMERA_PREVIEW_MODE=SINGLE|STAGED` または `KIWI_LIVE_CAMERA_COLOR_CORRECTION=METADATA` が残るとsame-source baselineではない。logのmode/relation/blitでfail-closedにする。
4. **runtime identity transition**: source unavailable、scene reload、camera restart時はobserverがsourceを再探索する。same Texture判定は各log時点のobject identityで行う。
5. **1:1の意味**: 1:1はTexture object上のtexel対screen pixelであり、camera sensor pixel対screen pixelを保証しない。Native presentationより上流のresample/compressionはA仮説に残る。
6. **colorとsharpness混同**: color-space mismatchはcontrast/shadow/detail perceptionに影響し得るが、minification判定と同一視しない。A/B sharpness、C geometry、D colorを別欄でレビューする。
7. **未修正のlayout mismatch**: 約1.9%横圧縮をobserverでも診断対象として再現する。今回これを修正しないため、geometry違和感は残る。

## [RUNTIME_TEST_INSTRUCTIONS]

### 前提

- camera、camera設定、照明、被写体距離、Game view/window sizeを固定する。
- current LIVE CAMERA full frameと1:1 texel cropを**同時に**同じMP4へ撮る。
- observer runはcorrectness/visual専用。Performance正式測定には採用しない。
- observerを含むEditor/Player processを新規起動する。環境変数は起動前に設定する。

### 推奨baseline起動（PowerShell 5.1互換）

```powershell
Remove-Item Env:KIWI_CAMERA_PREVIEW_MODE -ErrorAction SilentlyContinue
Remove-Item Env:KIWI_LIVE_CAMERA_COLOR_CORRECTION -ErrorAction SilentlyContinue
$env:KIWI_LIVE_CAMERA_PIXEL_SPACE_AUDIT = '1'

# 上の環境を継承する同じPowerShellからUnity Editorまたはobserverを含むPlayerを起動する。
```

既存Player binaryは今回の新observerを含まない可能性がある。Editor runまたは新observerを含めて通常手順で作成したDevelopment Playerを使用する。Native DLLのbuild/replacementは行わない。

### Fail-closed startup gate

logで以下を全て確認してから録画する。

1. `[KiwiLiveCameraPixelSpaceAudit] READY`
2. source名/sizeが安定し、例として `KiwiNativeCameraPresentation`, `1920x1080`
3. `relation=DIRECT_SAME_TEXTURE`
4. `previewMode=DIRECT`
5. `colorMode=OFF`
6. `liveBlitsLastPrepare=0`
7. `fullCropSameTextureObject=1`
8. `observerBlit=0 observerReadback=0`

1つでも不成立なら、そのrunをpixel-space baselineとして採用しない。環境変数、active source、scene bindingを再確認する。

### MP4 capture

1. Game viewを最低1280x720にし、window sizeを途中変更しない。
2. 同一camera/照明/距離で、顔、髪、眉、目、衣服の細線、背景の高周波detailが両viewに入る位置を選ぶ。
3. 既存LIVE CAMERA full frameを表示したまま、observerのA FULL FRAMEとB CENTER 1:1 TEXEL CROPを同時表示する。
4. 最低15–30秒、静止区間と小さな頭部移動区間を撮る。camera focus/exposure設定を途中変更しない。
5. MP4と対応Editor/Player logを同じcase tokenで保存する。
6. 必要ならMP4をframe単位で確認するが、observer FPSをperformance proofにしない。

### 判定規則

| 観察 | 主因候補 | 次の監査。今回はFIXしない |
|---|---|---|
| 1:1だけ鮮明 | B: UI minification/sampling。relationがINTERMEDIATEならEも併記 | full-frame sampling/layoutとservice modeを分離A/Bする。 |
| 両方同程度に劣化 | A、またはPresentation Textureより上流 | camera source、Native conversion/presentation入力、focus/exposure/compressionを再監査。tracking mathへ逃がさない。 |
| geometryだけ違和感 | C: aspect/layout | actual logのsourceAspect/drawAspect/X-Y ratioとMP4 geometryを相関する。 |
| full/cropとも同じ色・shadow違和感 | Dまたは上流source color | graphicsFormat/isDataSRGB/project color space、MF metadata/transferを別correctness caseで再監査。 |
| fullだけdetail/moire/aliasing差 | B | filter/minificationを主因候補とする。 |
| source/comparison IDが異なる | Eまたは環境汚染 | baselineを無効化し、DIRECT/OFFで再実行。 |

このRuntime証拠がない現時点では、原因の最終判定は **PENDING**。静的layout mismatchを見つけたことをProduction画質FIXの承認とはしない。

## [CHANGED_FILES]

### 追加したcode

1. `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiLiveCameraPixelSpaceAudit.cs`
2. `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiLiveCameraPixelSpaceAudit.cs.meta`

### 追加したvalidation artifacts

3. `KiwiValidation/KiwiLiveCameraPixelSpaceAudit_UnityCompile.log`
4. `KiwiValidation/LIVE_CAMERA_PRESENTATION_PIXEL_SPACE_AUDIT_REPORT_20260902.md`（本REPORT）

### 変更していない既存ファイル

overlay、quality service、Native DLL/source、tracking/inference、scene、ProjectSettings、shaderを含む全既存Production file。`Tools/KlakSpout_v206_diag/` は監査開始前から存在したuntracked user-owned directoryであり、今回触れていない。

## [SHA256]

### 変更/追加物

| file | before | after |
|---|---|---|
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiLiveCameraPixelSpaceAudit.cs` | ABSENT | `C39EC230925AF830B70746DC60F73311EF9FBE8AA63BE239686A7BAFE4213A82` |
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiLiveCameraPixelSpaceAudit.cs.meta` | ABSENT | `6BADE5DC8E2524B6EF0E289146789868DFFC7D56720F05712596A3779981A3DC` |
| `KiwiValidation/KiwiLiveCameraPixelSpaceAudit_UnityCompile.log` | ABSENT | `D77A8990F7E56816B4173361869ECD0C0229871F3B699839395CE652F947014C` |

本REPORT自身のSHAは自己参照でREPORT本文に固定できないため、file finalize後のdelivery responseで提示する。

### Critical Production before/after

| file | before SHA256 | after SHA256 | result |
|---|---|---|---|
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiFrameComparisonOverlay.cs` | `4F5068749B56C17B80F67B6AFD21DF222365A6CF31C36F401466873F5A2140DF` | same | UNCHANGED |
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiCameraPreviewQualityService.cs` | `717C1B1106CEBC0D1E0C37F5A3899A1C3D67007EB7B61DACCE64A39C1336043A` | same | UNCHANGED |
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiLiveCameraPathDiagnosticV44_23.cs` | `5C1ACFB793D63570E54FD91B080D235F21843EEEA1FC9B2F408DCF0B9BF4DE31` | same | UNCHANGED |
| `Assets/KiwiAvatarSystem/Runtime/Camera/WindowsNativeWebCamSource.cs` | `D0DD9A78FA57AAB51D3E0C8606844C44F0207DC87277D0B26AEF3B43EF353533` | same | UNCHANGED |
| `Assets/KiwiAvatarSystem/Runtime/Camera/KiwiNativeCameraInterop.cs` | `AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5` | same | UNCHANGED |
| `Assets/KiwiAvatarSystem/Resources/KiwiLiveCameraMetadataCorrectionV44_29.shader` | `57BF11D3FCBE7DD89C3F61444F72067FA581597C5F145AF8397C6A30E38A0559` | same | UNCHANGED |
| `Assets/Script/KiwiFaceMotion.cs` | `D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6` | same | UNCHANGED |
| `Assets/Script/KiwiInferenceFaceTracker.cs` | `52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695` | same | UNCHANGED |
| `Assets/KiwiAvatarSystem/Runtime/Tracking/KiwiInferenceFaceTracker.cs` | `EFFCCCF5EE1BF407F065BFA95B491390AC96A55E9A75290A67C5AFAD32AFC1F1` | same | UNCHANGED |
| `Assets/Script/FaceLandmarkerRunner.cs` | `6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93` | same | UNCHANGED |
| `Assets/Plugins/x86_64/KiwiNativeCamera.dll` | `82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5` | same | UNCHANGED |
| `Native/KiwiNativeCamera/Source/KiwiNativeCameraPlugin.cpp` | `636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A` | same | UNCHANGED |
| `Assets/Scenes/Face Landmark Detection.unity` | `E28B0B81F731B92B04B97D4AA7DB1187C8DD57C5A39F49B08B2E6799BBF5B3D1` | same | UNCHANGED |
| `ProjectSettings/ProjectSettings.asset` | `88CB3F68F275E1A7DCBF1FEA8C59D123F50743C6F93B0F861E1FD653865EC6AA` | same | UNCHANGED |

## Final audit verdict

- **Implementation gate: PASS** — observer-only追加、compile成功、禁止境界のSHA不変。
- **Production quality fix: NOT IMPLEMENTED** — 意図どおり。
- **Runtime visual root cause: PENDING / NO PASS CLAIM** — same-condition MP4 + correlated logが未取得。
- 現時点で確定できる問題は、nominal layoutの約1.9%横圧縮だけ。最も有力な未確定仮説はmipmapなしBilinearで約4.6 texels/pixelへ縮小するBだが、1:1 crop Runtime比較までは推測として保持する。
