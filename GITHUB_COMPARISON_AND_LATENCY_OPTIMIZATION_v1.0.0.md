# KiwiAvatarSystem GitHub比較・低遅延最適化報告

- 対象バージョン: 1.0.0（固定）
- 調査・検証日: 2026-08-13
- 主対象: Windows / Unity 2022.3.62f2 / DX11
- 補助対象: Android / iOS（iOS実機確認は対象外）

## 結論

現行の「Unity同一プロセス内でMediaPipe Face Landmarkerを実行し、最新結果だけをロック付きダブルバッファで公開する」構成を維持した。比較対象よりフレーム段数が少なく、Windows / Android / iOSを同じコード基盤で保ちやすく、現在利用中のMediaPipeUnityPlugin 0.16.3も調査時点の最新リリースである。

録画で確認された主な遅延要因は推論そのものではなく、低信頼時に回転・位置・奥行きのいずれか1項目が閾値を超えると、結果全体を破棄して3回の整合確認を待つ受理方式だった。結果レート11～12 Hzでは約2結果間隔、理論上およそ167 msの追加ホールドになり得る。

今回、全姿勢ホールドを標準経路から外し、有効な最新結果は毎回受理したうえで、異常と判定した成分だけを経過時間に比例した最大変化量へ制限する方式へ変更した。位置だけが急変した場合に回転・奥行きまで古くなることもなく、受理時刻が更新されるため描画予測が古い結果を基準にし続ける問題も解消する。旧方式はA/B診断用に切替可能なまま残した。

## GitHub類似システム比較

| システム | 設計・技術構成 | 遅延面 | 安全性・ライセンス | 更新状況（調査時） | Kiwiへの判断 |
|---|---|---|---|---|---|
| [MediaPipeUnityPlugin](https://github.com/homuler/MediaPipeUnityPlugin) | Unityネイティブプラグイン、MediaPipe Tasks、CPU/GPU delegate、478点＋blendshape | 同一プロセスで最短。Windows配布バイナリはCPU。GPUはVulkanが必要 | MIT、MediaPipeはApache-2.0。ネイティブコードのクラッシュ境界に注意 | v0.16.3、2025-11-08。現行Kiwiと一致 | 採用継続。0.16.0でフレームコピー時刻によるジッター修正とcallback data race修正が入っている |
| [VMagicMirror](https://github.com/malaybaku/VMagicMirror) | Unity描画＋WPF設定アプリ。VRM、MediaPipeUnityPlugin、各種入力を統合 | 高機能だが別UIプロセス／IPC分だけKiwiの単一経路より複雑 | MIT。多数の外部DLL・有償アセット・スクリプティング依存は攻撃面と更新管理を広げる | README v5.1.0、2026-07-31。活発 | UI分離やモデル運用は参考。顔姿勢の最短経路としては移植しない |
| [XR Animator](https://github.com/ButzYung/SystemAnimatorOnline) | MediaPipe＋TensorFlow.js、WebWorker、OffscreenCanvas、three.js、Electron/Web | Worker分離で描画60 fps・検出30 fpsを主張。Web/Electron境界とコピーが増える | CC BY-NC-SA 4.0。Web/CDN/Electron依存とVMC通信の管理が必要 | 0.34.2、2026-06-19。活発 | 全身・Web展開は強いが、Kiwiの商用／配布条件と低遅延単一経路には不適 |
| [OpenSeeFace](https://github.com/emilianavt/OpenSeeFace) | Python/EXE、ONNX Runtime CPU、MobileNetV3、66点、UDPでUnityへ送信 | 30～60 fpsのCPU性能は優秀。ただし別プロセス、キャプチャ、UDP受信が追加 | BSD-2-Clause。カスタムONNX Runtimeはtelemetryなし。UDPは暗号化・認証・bind範囲に注意 | v1.20.4、2021-09-17。長期更新なし | 別PC追跡には有効。単一PC・最小遅延ではKiwiより段数が多く、点数も少ない |
| [Streamlabs Face Mask Plugin](https://github.com/stream-labs/facemask-plugin) | OBS C++ plugin、Dlib 68点、検出専用thread、3コピー＋circular buffer、mesh subdivision | thread分離は参考になるが、3コピーとキューは結果年齢を増やす | ネイティブC++／OBS／Dlib依存。古い依存とメモリ安全性の監査負担が大きい | 約8年前の設計。1,097 commitsだが現在の保守は弱い | メッシュ変形の考え方のみ参考。バッファ設計は採用しない |
| [KalidoKit](https://github.com/yeemachine/kalidokit) | JS/TSのMediaPipe/TensorFlow.js用kinematics solver、VRM/Live2D | 軽量だがブラウザ／JSパイプラインを追加 | MIT。CDN利用時はsupply-chain管理が必要 | 公式にdeprecated、MediaPipeへ統合 | 新規採用しない |
| [MesekaiUnity](https://github.com/Neleac/MesekaiUnity) | Unity＋fork版MediaPipeUnityPlugin、ReadyPlayerMe、顔・体・指 | Kiwiと近いがfork構築とサブモジュール運用が重い | GPL-3.0、Git LFSとfork依存 | 小規模（37 stars） | 技術的新規性・保守性で現行Kiwiを上回らない |
| [UniVRM](https://github.com/vrm-c/UniVRM) | UnityのVRM/glTF標準実装、runtime async import | 顔推論ではなくモデル基盤 | MIT。外部モデルは入力検証と資源上限が必要 | 最新v0.131.0、現行Kiwiは0.130.1 | 0.131.0への更新は顔追跡遅延を改善しないため今回は見送り。別途互換性回帰を伴う更新対象 |

## 方式評価

### 追跡・受理方式

| 案 | 長所 | 短所 | 評価 |
|---|---|---|---|
| A. 現行の3結果再捕捉 | 単発スパイクを強く遮断 | 11～12 Hz時に正当な高速移動まで最大約167 ms保持。位置異常で回転まで停止 | 不採用（診断用として残す） |
| B. 最新結果を無加工で即時反映 | 最小のアルゴリズム遅延 | 単発スパイクがそのまま表示され、目・口・全身のちらつき原因になる | 不採用 |
| C. 最新結果を受理し、異常成分だけ時間制限 | 結果年齢を増やさず、回転・位置・奥行きを独立保護。単発スパイクも上限化 | 極端な実動作は1サンプルで全量到達せず、上限制御される | **採用** |
| D. 1Euro全面置換 | 実装が簡潔、静止ノイズを抑えやすい | フィルタ自体が位相遅れを作る。既存A/Bで移動誤差が大きい | 不採用 |
| E. Windows GPU delegate | 理論上モデル推論を短縮可能 | DX11では使えずVulkan必須。PC GPU経路はplugin側で実験的 | 今回不採用 |
| F. 外部tracker / UDP | 別PCへ負荷分散可能 | IPC、キュー、同期、配布物、firewall、安全設定が増える | 今回不採用 |

決定論的テストは、録画で観測した11～12 Hzに合わせ、120 Hz描画、移動・停止・反転・ノイズ・孤立スパイクを含めた。合成スコアは移動誤差80%、静止ジッター20%で低いほど良い。

| 方式 | 合成 | 移動RMSE | 静止ジッター |
|---|---:|---:|---:|
| 3結果ホールド | 0.147628 | 0.179468 | 0.020269 |
| 即時RAW | 0.058006 | 0.059588 | 0.051678 |
| **成分別上限制御** | **0.051445** | **0.059588** | **0.018877** |

採用方式は3結果ホールド比で合成誤差65.2%、移動RMSE66.8%を低減し、即時RAW比では静止ジッター63.5%、合成誤差11.3%を低減した。これは決定論的合成評価であり、実カメラのmotion-to-photon実測値ではない。

既存の予測ハイブリッド対1Euro比較も再実行し、予測ハイブリッド0.019387、1Euro 0.151700で予測ハイブリッドを維持した。

## 実装変更

1. `KiwiFaceMotion.ProcessNewSample`で有効な最新timestampを毎回受理する標準経路を追加。
2. 回転は`Quaternion.RotateTowards`、位置は`Vector2.MoveTowards`、奥行きは`Mathf.MoveTowards`で、経過時間×許容速度の上限に個別制限。
3. 高品質結果は通常そのまま通し、低品質かつ閾値超過、または破滅的速度だけを制限。
4. 1成分の異常で他成分とtimestampを破棄しない。
5. Runtime Panelに`Bound spikes without holding latest pose`を追加し、PlayerPrefsへ保存・復元。
6. 診断表示に直近の制限channel bitmaskと累積制限回数を追加。
7. シーン標準値、再インストール用tracking template、installer SHA-256を同期。
8. 3方式の決定論的A/B evaluatorとvalidator検査を追加。

## 現行ホットパスと残る下限

現在のWindows/DX11経路は、WebCamTextureの新規フレームだけを480幅RenderTextureへ縮小し、同期readback後にMediaPipe LIVE_STREAMへ送る。debug annotationは無効、MediaPipe側のflow limiterにより処理中フレームを蓄積しない。callbackは正確な送信時刻と対応付け、古いcallbackを捨て、478点をダブルバッファで公開する。

既存録画の表示値はreadback約4.1 ms、source-to-result約27.7 ms、推定model約23.6 ms、render age約147.6 msだった。今回の修正対象は、このうち推論後に古い受理結果を保持していた時間である。カメラ露光、USB転送、約23.6 msのCPU推論、ディスプレイ走査は残るため、「0 ms」を保証するものではない。

480幅からさらに解像度を落とす変更は速度向上余地があるが、顔が小さい場面・横顔・目口精度を悪化させるため、端末別の実測なしには標準値へ採用しなかった。CPUAsync readbackは既存960×540評価で89～96 msとなり、現在の同期4.1 msより悪かったため不採用。`latestFrameOnlyLiveStream`も既存録画で結果レート6～8 Hzへ低下したため標準OFFを維持した。

## 安全性

- カメラ画像とランドマーク処理は標準構成ではローカル同一プロセス内で完結し、OpenSeeFace/VMCのようなUDP送信を追加していない。
- MediaPipe modelはローカル固定ファイル、pluginは0.16.3 tarballをプロジェクト内で固定している。CDN実行時の依存差し替えリスクはない。
- VRM importは`.vrm`拡張子、空ファイル、最大サイズ（Windows標準200 MB、mobile標準128 MB、低メモリ64 MB）、管理directory内pathを検証し、一時ファイルへ排他的にコピー後renameする。
- 外部VRMは依然として不信入力である。UniVRM parserや画像decoderの脆弱性、巨大texture／vertexによる資源枯渇を完全には排除できないため、size limitを無効化せず、UniVRM更新時はmalformed model回帰を行う。
- MediaPipeUnityPluginはネイティブコードを含み、公式READMEも特にWindows Editorのnative abortを完全には捕捉できない旨を示す。Editor以外の配布buildを含めたクラッシュ確認が必要。
- XR AnimatorのCC BY-NC-SA、MesekaiUnityのGPL-3.0、各システムのthird-party asset条件はKiwiへの直接移植に適さない。今回、外部コードはコピーしていない。

## 検証結果

- Unity 2022.3.62f2 batch compile: 成功
- Kiwi Optimization Validator: **30 / 30 PASS**
- 追跡方式A/B: PredictiveHybrid勝利
- 結果受理3方式: BoundedLatest勝利
- tracking source / template SHA-256: 一致
- application version: 1.0.0のまま
- iOS実機: 方針どおり未確認

実カメラでの最終確認は、Runtime Panelの`render age`と`bounded current/total`を表示し、左右への高速移動・停止・反転を同一条件で再録画する。期待結果は、`source->result`を悪化させず、移動開始時に`render age`が複数結果分伸びないこと、孤立スパイク時だけ`bounded`のchannel値が一時的に非0になることである。

