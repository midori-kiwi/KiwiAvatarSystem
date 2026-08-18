# Kiwi Avatar System 最適化方式評価（v1.0.0）

アプリ表示バージョン、バンドルバージョン、検証バージョンは `1.0.0` に固定する。

## 追従方式

同一の30 Hzランドマーク入力を120 Hzで表示し、移動・停止・反転・決定的ノイズを含む信号で比較する。移動誤差を85%、静止時ジッターを15%として評価した結果、1Euro Filterより予測ハイブリッド方式を採用した。

| 方式 | 移動追従 | 静止安定性 | 判定 |
|---|---|---|---|
| Predictive Hybrid | 実測結果間を速度整合性付きで補間・予測し、意図的な動きは直通 | Dead zone、静止ロック、上限付き補正 | 採用 |
| 1Euro Filter | 適応ローパスの位相遅延が移動時に残る | 良好 | 比較用として保持 |

## 任意形状への顔パーツ自動フィット

| 方式 | 形状精度 | 実行コスト | 判定 |
|---|---|---|---|
| 頭部Bounds＋目の意味情報 | アンカー単位の近似 | 低い | フォールバック |
| BakeMesh＋両面Raycast＋法線 | 非球体を頂点単位で追従 | モデル切替時のみ | 採用 |

Raycast成功率が70%未満の場合は従来の適応フィットを維持する。成功時は一時MeshColliderを即時破棄するため、フレームごとのPhysics負荷は増えない。

## 顔エフェクト方式

現行CPU Mesh UV、Snap Camera Server、Webcamoid、OBS FaceMask、Native MediaPipe＋GPUを比較し、Native MediaPipe＋GPUを採用した。詳細な配点、判定根拠、実装内容は [EFFECT_STRATEGY_REVIEW_v1.0.0.md](EFFECT_STRATEGY_REVIEW_v1.0.0.md) を参照。

採用方式では既存の478点ランドマークとblendshapeを再利用し、口のサイズ固定とBig Mouth倍率をシェーダーの単一サンプリング変換へ統合する。追加プロセス、仮想カメラ、二重推論、通常時のCPUメッシュ再生成は発生しない。

## 自動検証

`KiwiOptimizationValidator` は以下を継続判定する。

- バージョン1.0.0固定
- Predictive Hybrid対1Euro Filter
- Native MediaPipe＋GPU対4方式
- Raycast＋法線フィット
- 右向き・右傾き・右移動の符号
- フレームレート非依存応答
- 追従停止・反転復帰、カメラ露光中点補償
- 目・口の補間、輪郭、映像端の非完全パーツ非表示
- 重複サフィックス不在、テンプレート整合性
