# KiwiAvatarSystem v44.55.21 Production Crop Sampler Border/Footprint Isolation Audit

Date: 2026-09-02 (Asia/Tokyo)

Workspace: `D:\KiwiAvatarSystem`

Scope: observer-only。Production、Native DLL/source、Tracking、ROI、FaceTexture、Root/Head、thresholdは変更しない。

## [EXECUTIVE_VERDICT]

**Final decision（1つのみ）: `INSUFFICIENT_DATA`**

確認済み事実は二層に分かれる。

1. 静的authorityでは、Production crop shaderも既存Native diagnostic mode2も、変換後UVを`0..1`へclampし、範囲外bilinear tapをedge texelへclampする。したがって「mode2にClampが欠けている」という単純な実装差は存在しない。`MODE2_CLAMP`は`MODE2_ORIGINAL`と同じ演算になり、別候補としては退化している。
2. v44.55.21 final Runtimeではobserver dependency bind、Native camera、Path Bは成立したが、Production tracker ROIが全期間`trackerRegion=0`であり、v44.55.20 gateがmeasurementへ入らなかった。completed `0 < 60`、canonical-comparable `0 < 40`なので、source-footprint相関と3入力の正式Runtime判定はできない。

よって、既存v44.55.20の`PREPROCESSING_CONFIRMED`は維持するが、今回の全`>=4 LSB` source-footprint CSVに基づくborder-vs-pixel-center確定は行わない。0件CSVを根拠に`BORDER_REJECTED`や`BORDER_CONTRACT_CONFIRMED`を捏造しない。

## [EXTERNAL_RESEARCH]

### 公開仕様として確認済み

- Unity `TextureWrapMode.Clamp`は、通常範囲外のtexture coordinateでedge pixelを延長するaddress modeである。公式: [Unity TextureWrapMode](https://docs.unity3d.com/ja/2022.3/ScriptReference/TextureWrapMode.html)。
- Unity 6 `Graphics.Blit(source, dest, material, pass)`は、`dest`をrender targetにし、`source`をmaterialの`_MainTex`に設定し、material shaderでfullscreen surfaceを描く。公式: [Unity Graphics.Blit](https://docs.unity3d.com/ja/current/ScriptReference/Graphics.Blit.html)。
- Direct3Dのpixel centerは整数座標から`0.5` offsetし、texture addressingではfilter footprintを求めた後にtexture address wrappingを適用する。公式: [Microsoft Direct3D coordinate systems](https://learn.microsoft.com/en-us/windows/win32/direct3d10/d3d10-graphics-programming-guide-resources-coordinates)。
- MediaPipeの公式GPU converterはlinear filteringとborder modeを別契約として扱い、replicateではclamp-to-edgeを使う。公式source: [MediaPipe GL texture converter](https://github.com/google-ai-edge/mediapipe/blob/master/mediapipe/calculators/tensor/image_to_tensor_converter_gl_texture.cc)。OpenCV経路もborder modeとinterpolationを明示的に分ける。公式source: [MediaPipe OpenCV converter](https://github.com/google-ai-edge/mediapipe/blob/master/mediapipe/calculators/tensor/image_to_tensor_converter_opencv.cc)。

### 公開設計原則

border address mode、pixel-center、subtexel interpolation precision、Y origin、UNORM storeは独立に監査すべきである。同じClamp指定同士で差が残る場合、ただちに「borderが原因」とせず、sample coordinate、filter arithmetic、source representationを分離する。

### 推測ではなく今回採用した境界

Unity/D3Dの一般仕様より、実際にProductionで使うshader/material/source Texture設定を上位authorityとした。非公開GPU sampler内部精度は静的sourceだけでは断定しない。

## [PRODUCTION_CROP_SAMPLER_AUTHORITY]

### Source texture identity / properties

Production camera sourceは`FaceLandmarkerRunner`が`imageSource.GetCurrentTexture()`から取得し、`InitializeSentisTracker`へ渡して`_sentisSourceTexture`として保持する。Windows Native Path Bでは`WindowsNativeWebCamSource`が固定identityのpresentation RTを返す。

| Property | Production authority |
|---|---|
| name | `KiwiNativeCameraPresentation` |
| size | `1920x1080` |
| dimension | `Tex2D` |
| format | `RenderTextureFormat.ARGB32` / runtime `RGBA32-linear` |
| filterMode | `Bilinear` |
| wrapMode | `Clamp` |
| mip | `useMipMap=false`, `autoGenerateMips=false` |
| provider orientation | Native presentation生成時にYを正規化済み |

Source作成authorityは`Assets/KiwiAvatarSystem/Runtime/Camera/WindowsNativeWebCamSource.cs`の`Texture2D.CreateExternalTexture(...RGBA32, mipChain:false, linear:true)`、Native presentationへの既存Y-flip `Graphics.Blit(scale=(1,-1), offset=(0,1))`、`KiwiNativeCameraPresentation` RT作成である。

Final v44.55.21 Runtime logはNative camera `UGREEN Camera 4K`, `1920x1080@60`, `MF-NV12/D3D11->D3D12CompatibilityReverseSharedRGBA`, `Path B`, `texture=RGBA32-linear`, `stablePresentation=True`, `orientation=ProviderGpuYNormalized`を確認した。ただしmeasurement未開始のため、v44.55.21 pair内のsource object/property freezeは未取得である。

### Production 192x192 crop

`Assets/Script/KiwiInferenceFaceTracker.cs`の各laneは次を所有する。

- destination: `RenderTexture(192, 192, 0, ARGB32, Linear)`;
- destination filter/wrap: `Bilinear / Clamp`;
- mip: disabled;
- material: `Resources/KiwiInferenceFaceCrop.shader`から作るlane固有material;
- sampling matrix: `_Xform = BuildFlipMatrix(flipHorizontal, flipVertical) * cropMatrix`;
- generation: `Graphics.Blit(source, lane.cropTexture, lane.cropMaterial, 0)`;
- tensor transform: `NCHW`, `CoordOrigin.TopLeft`。

Crop shader `Assets/KiwiAvatarSystem/Resources/KiwiInferenceFaceCrop.shader`は以下を行う。

```text
sourceUv = mul(_Xform, float4(input.uv, 0, 1)).xy
sourceUv = saturate(sourceUv)
color = tex2D(_MainTex, sourceUv)
```

shaderは明示的sampler state blockを持たず、`sampler2D _MainTex`がsource textureの`Bilinear / Clamp`設定を使う。さらにshader自身がsampling前に`saturate`するため、通常範囲外UVをRepeatへ流す余地はない。Linear color-space時のsRGB preservationは既存`_InputIsSRGB`分岐であり、今回変更していない。

### Native mode2のborder contract

`Native/KiwiNativeCamera/Source/KiwiNativeCameraPlugin.cpp`のmode2 authorityである`SampleNv12AsConvertedRgbBilinearQuantizedDiagnostic`は、既に次を行う。

1. `u = ClampUnit(sourceUvBottomX)`、`v = ClampUnit(sourceUvBottomY)`;
2. `sourceX = u * width - 0.5`;
3. `sourceTopY = (1 - v) * height - 0.5`;
4. `x0/y0=floor`、`x1=x0+1`、`y1=y0+1`;
5. 4 tapを`ConvertNv12PixelToRgb`へ渡す;
6. `ConvertNv12PixelToRgb`は各tapのx/yを`[0,width-1] / [0,height-1]`へclampする;
7. mode2は各source tapをPresentation UNORM8へquantizeし、bilinear lerp後にcrop RT相当のfinal UNORM8へ再quantizeする。

したがって、Productionとmode2はいずれも「変換UV clamp + edge texel replicate」である。Source textureの`wrapMode=Clamp`だけへ依存する契約ではない。

## [V44_55_20_RECAP]

Authority: `KiwiValidation/KIWI_COMMON_TENSOR_BACKEND_STAGE_ISOLATION_V44_55_20_REPORT.md`, SHA256 `EC62B27BA7775C0154DAE7EDDF9862FAF2D4C1B746A241F17A7C60139766F1BA`。

- decision: `PREPROCESSING_CONFIRMED`;
- `REF_GPU vs REF_CPU`: PASS;
- `A_GPU vs A_CPU`: PASS;
- `B_GPU vs B_CPU`: PASS;
- `REF_GPU vs B_GPU`: AGGREGATE_FAIL;
- `REF_CPU vs B_CPU`: AGGREGATE_FAIL;
- completed `115`、REF/B canonical-comparable `81`;
- REF vs mode2 exact ratio `94.6367%`, mean abs `0.056598 LSB`, RMSE `0.268792 LSB`, max約`39 LSB`;
- REF-vs-mode2 `>=4 LSB`: `1,652`、その`94.07%`がtensor bottom 32 rows;
- `A vs B max < 1 LSB`、large-tailはchannel-specificではない。

v44.55.20はpreprocessing stageを確定したが、各outlierのtransform済みsource footprintを保存していないため、tensor bottom-edgeとsource boundaryを同一視できない。

## [OBSERVER_VALIDITY]

### 実装

追加asset:

- `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiProductionCropSamplerIsolationV44_55_21.cs`;
- `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiProductionCropSamplerIsolationV44_55_21.cs.meta`。

opt-inは`KIWI_V44_55_21_CROP_SAMPLER_AUDIT=1`。通常Productionではcomponentを作らない。observerはv44.55.20のexact pairをread-only reflectionで参照し、次を追加する。

- `_referenceNchw` / `_rightNchw` / `_samplingMatrix` /完成済みPairRecordの同一pair取得;
- `MODE2_CLAMP`用の永続`float[110592]`へ`Array.Copy`し、ORIGINALとのbitwise一致をfail-closed検証;
- 全tensor elementを1回走査し、`>=1/2/4/8/16/32 LSB`を集計;
- 全`>=4 LSB`をsource-footprint CSVへ逐次出力;
- v44.55.20の`PreprocessGpuB` semantic recordを、bitwise-identicalなORIGINAL/CLAMP双方のGPUCompute authorityとして再利用;
- source name/size/dimension/graphicsFormat/filter/wrapを各accepted pairで検証。

新observer自身が追加するものは、Blit `0`、RenderTexture `0`、GPU/CPU readback `0`、worker `0`、blocking wait `0`、camera frame `0`、Production state write `0`。v44.55.20側の既存correctness observer GPU work/readbackは残るため、このrun全体はPerformance authorityではない。

### Observer自体の再監査

初回試行ではhidden `DontSave` dependencyを通常object searchで取得できず、`INVALID_OBSERVER`として破棄した。`Resources.FindObjectsOfTypeAll<T>()`へ局所修正し、final buildでは`DEPENDENCY_BOUND executionOrder=32000 reflectionReadOnly=1`を確認した。

次の試行で0 sample時のalias exact ratioを1.0と要求するvalidator bugを検出し、sampleが1件以上ある場合だけbitwise aggregateをgateするよう修正した。最終runではobserver fault `0`、dependency fault `0`、dependency bind成功、decision `INSUFFICIENT_DATA`を確認した。過去2試行は正式authorityから除外した。

### Final Runtime validity

| Gate | Result |
|---|---:|
| Native camera active / Path B / system-memory capture | PASS |
| v44.55.20 reflection | PASS |
| v44.55.21 dependency bind | PASS |
| Production tracker region | FAIL: `trackerRegion=0` |
| warmup 2 | NOT STARTED |
| duration 120 s measurement | NOT STARTED |
| completed >=60 | FAIL: `0` |
| canonicalComparable >=40 | FAIL: `0` |
| observer faults | PASS: `0` |
| source identity mismatch | PASS: `0`（pair未取得） |

Final Runtimeを120秒Performance/Correctness runとして扱わない。ROI gate前で停止したfail-closed確認runである。

## [SOURCE_COORDINATE_METHOD]

accepted pairごとに、v44.55.20がProduction lane materialからfreezeした同一`_Xform` 16 floatを使う。出力tensor pixel `(x,y)`に対し、TopLeft NCHWとProduction Blitのbottom-left UVを次で接続する。

```text
cropTopV        = (y + 0.5) / 192
cropVBottom     = 1 - cropTopV
cropU           = (x + 0.5) / 192

rawU            = M00*cropU + M01*cropVBottom + M03
rawVBottom      = M10*cropU + M11*cropVBottom + M13

sampleU         = clamp(rawU, 0, 1)
sampleVBottom   = clamp(rawVBottom, 0, 1)

rawSourceX      = rawU * sourceWidth - 0.5
rawSourceTopY   = (1 - rawVBottom) * sourceHeight - 0.5
sampleSourceX   = sampleU * sourceWidth - 0.5
sampleSourceTopY= (1 - sampleVBottom) * sourceHeight - 0.5

x0=floor(sampleSourceX), x1=x0+1
y0=floor(sampleSourceTopY), y1=y0+1
fracX=sampleSourceX-x0
fracY=sampleSourceTopY-y0
```

CSVはraw UV、clamped UV、raw/sample source X/Y、4 tap座標、各tap in-bounds、`anyTapOutOfBounds`、`centerOutOfBounds`、clamp対象tap数、四辺分類、raw source centerから最寄りedgeまでのsigned距離を保存する。`centerOutOfBounds`はraw UVが`0..1`外、`anyTapOutOfBounds`はshader clamp後のlinear footprint tapがsource bounds外でedge replicate対象になることを表す。tensor edge距離とは別authorityである。

## [OUTLIER_FOOTPRINT_ANALYSIS]

Final runのmain CSVとfootprint CSVはheaderのみ。accepted pair `0`のため、全`>=4 LSB` outlier数、source-footprint OOB率、center OOB率、clamp tap数、source top/bottom/left/right率、bottom 32-row率は**未測定**。

`0 / 0`を0%相関として扱わない。v44.55.20の`1,652` outlierを今回のexact matrixへ後付け対応させることもできないため、古いheatmapからsource OOBを推測しない。

## [MODE2_CLAMP_INPUT_RESULTS]

Runtime aggregateは0 sampleのため未測定。

一方、実コード契約としては次が確定している。

- MODE2_ORIGINALは既に`ClampUnit(U/V)`を実行する;
- 4 tapの整数座標も`ConvertNv12PixelToRgb`内でedgeへclampする;
- observerのMODE2_CLAMPはORIGINALを永続別配列へcopyし、各float bitを比較する;
- 1件でもbit差があれば`INVALID_OBSERVER`。

これは偽の「別Clamp式」を追加せず、要求された「差分はClampのみ」が実コード上は差分ゼロになることを表現する。別のedge-padding、UV epsilon、half-texel shiftをClamp候補へ混ぜていない。

## [MODE2_CLAMP_SEMANTIC_RESULTS]

Runtime semantic resultはcompleted `0` / canonicalComparable `0`なので`INSUFFICIENT_DATA`。

bitwise-identicalなMODE2_ORIGINALとMODE2_CLAMPに別GPU workerを追加してもborder isolationにはならず、同じfloat tensorを同じmodel/backendへ二重投入するだけである。そのため、accepted pairではv44.55.20の既存`B_GPU` semantic recordを両候補へ使う設計とした。これは新しいsemantic PASSを作らず、v44.55.20のGPUCompute authorityを再利用する。

固定gateは変更していない。

- Presence P95/max <= `0.010/0.030`;
- Canonical point P95/max <= `1.0/2.0 px`;
- Rotation P95/max <= `0.10/0.25 deg`;
- Scale P95/max <= `0.005/0.010`;
- Geometry P95/max <= `0.010/0.025`;
- Expression P95/max <= `0.020/0.050`;
- categorical mismatch/nonfinite `0`;
- precedence `INVALID_OBSERVER > HARD_FAIL > INSUFFICIENT_DATA > AGGREGATE_GATE`。

## [DECISION]

**`INSUFFICIENT_DATA`**

選定理由:

1. final observerはbindしfault 0だが、Production `trackerRegion=0`でmeasurement gateが開かなかった;
2. completed `0 < 60`;
3. canonicalComparable `0 < 40`;
4. footprint CSVはheaderのみで、source OOB concentrationを評価できない;
5. mode2が既にClampである静的事実は「missing Clamp」案を否定するが、全outlierのsource footprintとGPU samplerのsubtexel挙動をRuntimeで確定したことにはならない。

`BORDER_CONTRACT_CONFIRMED`、`BORDER_IMPROVES_BUT_NOT_SUFFICIENT`、`BORDER_REJECTED`、`INVALID_OBSERVER`は今回のfinal decisionとして選ばない。

## [PERFORMANCE_EVIDENCE_BOUNDARY]

このrunとv44.55.20 dependency runをPerformance証拠に使用しない。v44.55.20はobserver-owned crop freeze、TextureConverter、複数GPU/CPU worker、async readbackを含む。v44.55.21自身も全110,592 element scanとCSV formattingを行う。blocking waitは追加していないが、correctness observer workloadであるだけでPerformance authorityから除外する十分な理由になる。

## [REGRESSION_RISK]

- Production risk: low。Production critical fileは一切変更せず、新assetは環境変数opt-in。
- Reflection drift: medium。v44.55.21はv44.55.20のprivate field名とPairRecord shapeに依存し、不一致時はfail-closedする。
- Observer composition: medium。v44.55.20が先にpairを完成し、v44.55.21が高いexecution orderで同frame/次frameに読む。recordが1件以上飛んだ場合は`INVALID_OBSERVER`。
- Source identity: final 0-pair runではpair-level source texture propertyをfreezeできていない。Native startup logと静的実装をpair-level Runtime証拠へ格上げしない。
- Degenerate candidate: high interpretation risk。MODE2_ORIGINALが既にClampなので、MODE2_CLAMPを新しい改善候補と呼ぶと誤解を招く。別式を混ぜればborder-only isolationではなくなる。
- Storage/allocation: observerは永続110,592-float配列、histogram、main rows、CSV writerを持つ。accepted pairごとにCSV文字列とreflection/metric集計allocationがある。通常Productionではcomponent自体が存在しない。

### Compile/build/static validation

- Unity `6000.0.80f1` whole-project compile: PASS;
- Tundra: success, `654 evaluated`;
- DX12 Windows x64 Development diagnostic build: `Succeeded`, errors `0`;
- `git diff --check`: PASS;
- added `Graphics.Blit`: `0`;
- added `RenderTexture`: `0`;
- added readback: `0`;
- added blocking wait: `0`;
- Native rebuild/install: none;
- commit/push: none。

Production critical SHA256は全て要求値と一致し、afterも不変。

| File | SHA256 |
|---|---|
| `Assets/Script/KiwiInferenceFaceTracker.cs` | `52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695` |
| `Assets/Script/FaceLandmarkerRunner.cs` | `6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93` |
| `Assets/KiwiAvatarSystem/Runtime/Camera/KiwiNativeCameraInterop.cs` | `AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5` |
| `Assets/Plugins/x86_64/KiwiNativeCamera.dll` | `82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5` |
| `Native/KiwiNativeCamera/Source/KiwiNativeCameraPlugin.cpp` | `636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A` |
| `Assets/Script/KiwiFaceMotion.cs` | `D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6` |
| `Assets/KiwiAvatarSystem/Runtime/Validation/KiwiFrameComparisonOverlay.cs` | `6DF8CCC9B47A0E2E9BFF4807B4D7CAB4AB11911992A4624EF7A8E40436F39EDD` |

Observer/evidence SHA256:

| Artifact | SHA256 |
|---|---|
| v44.55.21 observer `.cs` | `64E8C651F2ACE5BD4626170F332C21A679A9DE27CA052CCC00EF1E3F6E184015` |
| v44.55.21 observer `.meta` | `AA2F6A2EE97E221E1EE142AC3EFCC492D61D935EE92793F274752D2A5FC954D9` |
| final Unity compile log | `CE77264340A72BB56029247BA1618C70598556303C155403D812A0A011A09615` |
| final build log | `4B691A618B000DE0CD52181D3A08D363C0A58527A180A66801D973C94E2732F9` |
| final Runtime log | `9C589946E881B635EA33EACC98A64AC0D63C13BF61B0744B64EB7A5CDAABD208` |
| raw observer TXT | `1098D1730967992467CEEB5B4A7241DBD0CDFAA03511ACA5F5B45AFACCDC5492` |
| main CSV（header only） | `F2D3AAE0AD794A3569314E7A0E6B89A4F72A64A8F493266D174D7B0793B3D654` |
| `>=4 LSB` footprint CSV（header only） | `56C170D11C9066ACD70177FE97F54B7459132851E0D4131FF67CEFF9CDE3F5BE` |

## [NEXT_BEST_ACTION]

**同じ最終build・同じthreshold・同じobserverで、顔ROIが成立した状態を維持して1回だけ再実行する。**

実行条件:

```text
KIWI_V44_55_20_COMMON_TENSOR_AUDIT=1
KIWI_V44_55_20_COMMON_TENSOR_SECONDS=120
KIWI_V44_55_20_COMMON_TENSOR_HZ=1
KIWI_V44_55_20_STABLE_SECONDS=8
KIWI_V44_55_21_CROP_SAMPLER_AUDIT=1
```

`GATE_MATCH -> READY_FOR_WARMUP -> MEASURE_START -> COMPLETE`、completed `>=60`、canonicalComparable `>=40`、observer/source identity fault `0`を必須にする。そこで全`>=4 LSB` source-footprint CSVをauthorityとして、既登録ruleにより`BORDER_CONTRACT_CONFIRMED / BORDER_IMPROVES_BUT_NOT_SUFFICIENT / BORDER_REJECTED`のいずれかへ閉じる。それまではProduction CPU sampling、Native、Tracking、ROI、thresholdを変更しない。

必要uploadは本REPORT 1ファイルのみ。
