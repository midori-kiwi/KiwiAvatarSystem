# Kiwi Avatar System 追跡欠損・瞬間消失レビュー（1.0.0）

作成日: 2026-08-13  
対象動画: `KiwiAvatarSystem - Face Landmark Detection - Windows, Mac, Linux - Unity 2022.3.62f2 _DX11_ 2026-08-13 16-36-31.mp4`

## 動画から確認した現象

- 317フレーム、約12.26秒を全フレーム確認した。
- frame 166（6.418秒）とframe 224（8.661秒）で、キウイ本体は残ったまま左右目・口が同じ1フレームだけ完全消失し、次フレームで復帰した。
- 3パーツが同時に消えるため、個別の瞬き・口端マスクではなく、`FacePartCropper.SetPartsVisible(false)` の共通追跡喪失経路が原因と判定した。
- frame 279（10.788秒）付近では口だけが不正な映像位置を参照した。口ランドマークの単発外れ値が、応答率の高いUVクロップへほぼそのまま反映される経路が原因である。

## GitHub・類似アプリ比較

以下は同一PC・同一動画による速度ベンチマークではなく、公開実装とKiwi要件に基づく設計比較である。

| 方式 | 欠損耐性 | 正常時の追加遅延 | 目・口精度 | 導入負荷 | 評価 |
|---|---:|---:|---:|---:|---|
| MediaPipe閾値を下げる | 中 | なし | 誤検出リスクあり | 低 | 不採用 |
| OpenSeeFaceへ置換 | 高 | CPU/別プロセス依存 | 口は強いが目はMediaPipeより不利 | 高 | 不採用 |
| Streamlabs FaceMask方式を外部連携 | 高 | バッファ・OBS経路あり | 68点ベース | 高 | 不採用 |
| 最終正常値保持＋孤立外れ値拒否＋空間ヒステリシス | 高 | 正常時なし | 478点を維持 | 低 | 採用 |

参考:

- MediaPipe Face Landmarker `detect_async` は低遅延化のため入力画像を落とす場合があり、入力ごとの結果は保証されない。`https://ai.google.dev/edge/api/mediapipe/python/mp/tasks/vision/FaceLandmarker`
- MediaPipeは検出・顔存在・追跡を別の信頼度閾値として扱う。`https://github.com/google/mediapipe/blob/master/mediapipe/tasks/cc/vision/face_landmarker/face_landmarker.h`
- Streamlabs FaceMaskは検出を別スレッド化し、循環バッファで結果を描画側へ渡して現在状態を更新する。`https://github.com/stream-labs/facemask-plugin`
- OpenSeeFaceはアバター用途の安定性と口姿勢に強い一方、READMEでは目領域がMediaPipeより不正確になり得ると説明している。`https://github.com/emilianavt/OpenSeeFace`
- VTube Studioは追跡喪失時にモデルをその場でフリーズする動作を選択できる。`https://github.com/DenchiSoft/VTubeStudio/wiki/VTube-Studio-Settings`

## 採用実装

1. 追跡喪失時はRawImageを無効化せず、最後の正常な目・口クロップを保持する。
2. 追跡復帰時は、喪失前の状態を破棄せず最新結果へ通常の高応答補間で復帰する。
3. 両目の共通移動から予測した口位置と大きく異なる「口だけの単発ジャンプ」を拒否する。両目と口が同時に移動する通常の顔移動は即時反映する。
4. 口がカメラ端へ出た判定は2回連続のLandmarker結果で確定する。正常な追従には待ち時間を追加せず、単発外れ値だけで非表示にしない。
5. 本当に端へ出た後は既存の0.040秒フェードと再入場マージンを維持する。
6. 上記の保持、口外れ値拒否、許容値、端確認回数をランタイム画面から変更・保存可能にした。

## 検証結果

- Unity 2022.3.62f2 batchmodeコンパイル成功。
- `KiwiOptimizationValidator`: 29 / 29 PASS。
- 共通移動する口サンプルは受理し、口だけの大ジャンプは拒否する純粋数学テストを追加。
- Verはアプリ、パッケージ、検証表示とも `1.0.0` のまま。

## 残る実機確認

録画は修正前のため、修正後のライブカメラ映像を新規録画し、6秒以上の高速移動・瞬き・大口・顔の一時遮蔽・カメラ端移動を通して再確認する必要がある。iOS実機確認は方針どおり対象外。
