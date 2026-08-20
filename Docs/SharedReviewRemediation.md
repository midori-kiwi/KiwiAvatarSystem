# 共有コードレビュー検証・修正記録

検証日: 2026-08-20  
対象: KiwiAvatarSystem 1.0.0 / Unity 6000.0.80f1  
元レビュー: https://chatgpt.com/share/6a86bfe4-1094-83ee-9d52-34dd65e579df

## 採用方針

既存の低遅延経路を維持しながら、確定不具合を修正し、既に実装済みの原子的スナップショットを明示化する段階的案を採用した。

| 案 | 安全性 | 追従回帰リスク | 改善効果 | 判定 |
|---|---:|---:|---:|---|
| 確定不具合だけを最小修正 | 9 | 2 | 7 | 構造上の曖昧さが残る |
| Tracking Coreを直ちに全面分割 | 5 | 9 | 8 | 実機回帰範囲が大きすぎる |
| **確定修正 + 原子的Frameの明示 + 回帰ガード** | **9** | **3** | **9** | **採用** |

## 指摘の検証結果

| 指摘 | 検証 | 対応 |
|---|---|---|
| MediaPipeが`D:\KiwiAvatarSystem`を参照 | 確定 | `Packages`基準の相対tarball参照へ変更。lock fileも同期 |
| Avatar Hot Swapが完全なTransactionではない | 確定 | candidateの所有権を設定完了まで保持し、失敗時に旧モデル、FaceAnchor、SurfaceFitter、fit状態を復元 |
| Pose・位置・品質・Timestampが別フレーム化する | 現コードでは既に対策済み | 同一`_trackingLock`内でランドマーク配列と`FacePrecisionTrackingData`を交換していた。単調増加`frameId`と`backend`を追加して契約を明示 |
| MediaPipeとInference Engineの正解系が不明瞭 | 一部妥当 | 公開スナップショットへ`MediaPipe` / `InferenceEngine`を明記。切替時は予測履歴を破棄し、異なる座標系列から異常速度を作らない。Inference Engine主経路、MediaPipe補助・fallbackという既存挙動は維持 |
| `LateUpdate` / `onBeforeRender`が複雑 | 設計上の注意点 | 二重積分を防ぐ時間管理と最新sample gateが既にあるため変更しない。全面変更は遅延回帰リスクが高い |
| `FacePartShapeMask`の責務集中 | 設計上の注意点 | 今回は挙動変更なし。Contour/Crop同期はちらつき防止に必要 |
| contour responseがREADME/Tooltip/コードで不一致 | 確定 | 実測採用値110を正とし、TooltipとREADMEを110-200の信号別設定へ同期 |

## 実装上の不変条件

- バージョンは1.0.0で固定する。
- 最新フレーム優先、Inference Engine GPU主経路、MediaPipe 10 Hz補助を維持する。
- `FacePrecisionTrackingData`は姿勢、位置、スケール用幾何、品質、時刻、backend、frame IDを一括公開する。
- モデル切替に失敗しても旧モデルを破棄せず、共有FaceAnchorを失敗モデルの子に残さない。
- MediaPipe依存はclone先やドライブ文字に依存しない。

## 検証

- `manifest.json`と`packages-lock.json`のJSON解析に成功。
- 相対MediaPipe参照は`D:\KiwiAvatarSystem\Packages\com.github.homuler.mediapipe-0.16.3.tgz`へ解決し、tarballの存在を確認。
- Unity 6の既存Roslyn応答ファイルで`Assembly-CSharp`と`Assembly-CSharp-Editor`を再コンパイルし、エラー0件。
- 復旧用Tracking Templateは実行ソースとSHA-256完全一致。
- Unity Editorの全検証とPlayer実行はEditorライセンス未認識（終了コード198）のため保留。コード、Package Manager、Player側の失敗ではない。
