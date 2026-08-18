# KiwiAvatarSystem 顔追跡バックエンド比較

評価日: 2026-08-14  
対象: KiwiAvatarSystem 1.0.0 / Unity 6000.0.80f1 / Windows DX11

## 結論

実装対象は **Unity Inference Engine GPUによる高頻度ランドマーク追跡 + MediaPipe FaceLandmarkerによる低頻度補正・表情・姿勢・フォールバック** とする。

Unity 6 + Inference Engine 2.4.1へ移行し、MediaPipeネイティブプラグイン、VRM/UniGLTF、シェーダーを同じEditorで回帰検証する。BarracudaはSentis/Inference Engineに置換済みの旧系統なので、新規の主経路には採用しない。

## 比較結果

100点満点。遅延30%、精度・機能25%、安定性15%、DX11適合10%、移植性10%、保守性10%で評価した。Unity 6案だけは移行・検証不能リスクを実行評価へ反映している。

| 方式 | 総合 | 主な評価 |
|---|---:|---|
| 現行MediaPipe単独 | 76 | 478点、52 blendshape、姿勢行列は強いが、DX11のGPU→CPU画像読戻しが主遅延 |
| Barracuda単独 | 69 | GPU推論は速いが468点中心で表情・姿勢機能を失い、Barracuda自体がSentisへ置換済み |
| Sentis 1.6単独 | 82 | Unity 2022.3でGPUComputeが動作し低遅延。ただしMediaPipe相当の補助出力を失う |
| Sentis 1.6 + MediaPipe補助 | 89 | Unity 2022.3上の旧採用案 |
| **Unity 6 + Inference Engine 2.4.1 + MediaPipe補助** | **92** | GPU主経路、478互換、表情・姿勢・再捕捉、正式リリース版の保守性 |

Unity 6移行後の総合評価は92点で、実装対象として採用した。

## GitHub類似実装の再分析と同期方式の選定

2026-08-14時点で、実際のソースを取得して以下を比較した。

- Unity-Technologies/sentis-samples (`17b7d83`, 2026-08-12): Unity公式。Blaze Face例は推論後に複数出力の非同期読戻しを同時発行する。メインスレッド停止には強い一方、ライブ表示は少なくとも次フレームになる。
- homuler/MediaPipeUnityPlugin (v0.16.3 / MediaPipe 0.10.22): 478点、blendshape、姿勢、再捕捉が強い。MITおよび同梱Third Party Noticesを維持。WindowsではMediaPipe GPUモード非対応のため、主経路ではなく低頻度補助が適する。
- keijiro/FaceMeshBarracuda (`cf6199d`, 2023-02-13): detector + landmark + irisをUnity内GPUで完結する設計は有効。ただしBarracuda世代で更新が止まっており、現行Inference Engineへそのまま採用しない。Apache-2.0通知は既存ONNX資産に同梱済み。
- emilianavt/OpenSeeFace (`85aa70f`, 2025-12-28): CPU ONNX、周期的な検出、ROI追跡、UDP Unity連携。BSD-2-Clause。堅牢だが外部プロセス、IPC、別のカメラ所有、66点中心という追加コストがKiwiの密な目・口合成には不利。

同一カメラフレームの保持を最優先し、遅延35%、精度維持25%、安定性15%、移植性10%、保守性10%、安全性5%で再評価した。

| 方式 | 総合 | 判定 |
|---|---:|---|
| 現実装: GPU推論 + 同期2回読戻し | 86 | 精度とデータ鮮度は保つがGPU/CPU同期が重複 |
| 公式例型: 複数出力を非同期並列読戻し | 89 | フレーム時間は安定するが表示結果が最低1フレーム古くなる |
| 同期のまま2出力の読戻し要求を並列発行 | 87 | 同一フレームだが要求管理と2回のCPUコピーが残る |
| OpenSeeFace外部CPU + UDP | 72 | 分離運用は強いが密度、IPC、配布、モバイルで不利 |
| MediaPipe単独LIVE_STREAM | 78 | 補助出力は最良だがWindows主経路のCPU画像入力が遅い |
| **Inference Engine GPU側出力結合 + 同期1回読戻し** | **96** | **同一フレーム、同一推論値のまま同期点を1回へ削減** |

最高評価のGPU側出力結合のみを実装した。Functional APIで1404個のランドマーク値と1個のpresenceをGPU上で1405値へ結合し、CPU読戻しを1回にした。非同期メールボックスは高スループット用途の選択肢としては有効だが、60 Hz表示で約16.7 ms以上の追加データ年齢が生じ得るため採用していない。外部通信、クラウド送信、新規ネイティブ実行ファイルは追加していない。

RTX 4090 / Direct3D 11、192x192 ONNX入力、各方式ウォームアップ12回 + 計測80回を旧→並列→結合、結合→並列→旧の交互順序で平均した最終A/B結果:

- 同期2回読戻し: 平均1.650 ms、p95 2.084 ms
- 同期並列要求・2回読戻し: 平均1.974 ms、p95 2.534 ms
- GPU側結合・同期1回読戻し: 平均1.402 ms、p95 1.735 ms
- 最終ラン平均短縮率: 15.0%。別の交互順序ランでは44.7%であり、全反復で結合方式が最速だった。

1405値すべてを旧経路と比較し、最大差0.0001以下を必須とするGPUスモーク検証を追加した。これにより追従精度やpresence判定を変えず、同期コストだけを削減している。

## 実測根拠

既存録画の診断表示では、カメラ入力約19.9 Hz、結果約22.6 Hz、DX11 GPU→CPU読戻し33.7 ms、推定モデル処理7.7 ms、描画時データ年齢102.7 msだった。主因は顔モデルそのものより、毎回の画像読戻しと低い実入力更新頻度である。

同じUnity 2022.3.62f2 / DX11 / RTX 4090での分離ベンチマーク:

- Sentis GPUCompute 顔ランドマーク（1280x720 RTから192x192前処理、推論、1404値読戻し）: 平均2.905 ms、中央値2.461 ms、p95 5.235 ms
- Sentis GPUCompute BlazeFace（前処理、推論、出力読戻し）: 平均2.752 ms、中央値2.318 ms、p95 5.117 ms

ベンチマークは経路性能の比較であり、ライブカメラ上の絶対精度を保証するものではない。最終的な遅延・揺れ・遮蔽復帰は実カメラ録画でも確認する。

## 採用設計

- 新しいカメラフレームごとにInference Engine GPUComputeで468点を追跡する。
- ランドマークとpresenceをGPU上で結合し、同一フレームのCPU読戻しを1回だけ行う。
- 虹彩10点を合成して既存478点APIを維持する。
- MediaPipeはWindows上でCPUAsync・10 Hzとし、ROI再捕捉、ドリフト補正、52 blendshape、姿勢校正を担当する。
- Inference Engine結果が新しい間は、遅れて到着したMediaPipe座標で上書きしない。
- 瞬間的な推論失敗は4フレーム連続まで保持し、目・口の一瞬の消失を防ぐ。
- GPUCompute、モデル、シェーダーの初期化失敗時はMediaPipe単独へ自動復帰する。
- 診断パネルに使用中バックエンド、Inference Engine推論時間、presenceを表示する。

## 安全性・ライセンス・保守

- 推論はローカル完結で、カメラ画像を外部送信しない。
- ONNXモデルはMediaPipe由来のFaceMeshBarracuda資産を利用し、Apache-2.0の通知を同梱する。
- Barracudaは公式にSentisへ置換済みで、本移行では正式リリースのInference Engine 2.4.1へ更新した。
- Unity 6移行はEditor更新だけでなく、Windows DX11、Android、iOSビルド、MediaPipeネイティブライブラリ、VRM/UniGLTF、シェーダー、シリアライズ済みSceneを一括検証してから採用する。

## Unity 6移行検証結果

- Unity Editor 6000.0.80f1で全スクリプト、MediaPipe 0.16.3、UniVRM/UniGLTF、Inference Engine 2.4.1を再インポートした。
- 最適化検証36/36に合格した。検証内でGPUCompute Workerを生成し、旧2出力と新しい1405値結合出力の数値同一性も確認した。
- Windows 64-bit Playerビルドに成功し、`Unity.InferenceEngine.dll`と`mediapipe_c.dll`の同梱を確認した。
- 生成PlayerをUGREEN CM831環境で起動し、1280x720/60 Hz要求、Direct3D 11、`Hybrid GPU landmark path initialized`を確認した。
- Hiddenシェーダーが初回Playerビルドでストリップされる問題を実行検証で検出し、モデルとシェーダーをResourcesから明示ロードする構造へ修正した。
- Inference Engine 2.4.1内部の`AttributeBasedFieldGenerator`警告と、CollectionsテストDLL対MediaPipe ProtobufのUnsafe DLL重複警告がEditorログに残る。どちらもUnity/外部パッケージ由来で、コンパイル、GPU推論スモークテスト、Windows Player起動には影響していない。
- Android/iOSはこのWindows環境に各Build Supportを追加していないため未ビルド。方針どおりiOS実機確認は行っていない。

## 参照

- https://github.com/keijiro/FaceMeshBarracuda
- https://docs.unity3d.com/Packages/com.unity.barracuda@3.0/manual/index.html
- https://docs.unity3d.com/Packages/com.unity.sentis@1.6/manual/index.html
- https://github.com/Unity-Technologies/sentis-samples
- https://unity.com/releases/editor/whats-new/6000.0.80f1
- https://docs.unity3d.com/ja/current/Manual/UpgradeGuides.html
- https://github.com/google-ai-edge/mediapipe/wiki/MediaPipe-Face-Mesh
- https://qiita.com/kumi0708/items/cf6384f5e327c1bde5e5
- https://qiita.com/SatoshiGachiFujimoto/items/739f5986f65c0d7465f0
