# README_FIRST

Task: KiwiAvatarSystem P2B Design-Frozen Z-Scale Repair + P2C Ordered Static Re-Audit
Project: D:\KiwiAvatarSystem
Owner: Codex / live-local authority required
Unity: 6000.0.80f1
Platform priority: Windows / DX12

Read order:
1. README_FIRST.md
2. 01_TASK_DETAILED.md
3. 02_P2_DESIGN_FREEZE.md
4. 03_REPORT_TIME_SHA_MANIFEST.txt

Goal:
Apply exactly one Production semantic repair only if live-local preflight still matches the proven boundary:
Assets/Script/KiwiInferenceFaceTracker.cs
ExtractRegionZScale(Matrix4x4 cropMatrix)

Then compile with the exact Unity version and perform the P2C ordered Static re-audit from the beginning.
Stop at the next first failing boundary. Do not apply a second Production fix in this task.

Hard rules:
- current live local Production + SHA is Implementation Authority.
- report-time SHA is reference only; never auto-promote it to current.
- no reset / checkout / clean.
- preserve all user-owned dirty/untracked files.
- commit/push = NO.
- Runtime = NO.
- H1 / FaceGeometry solver / ROI recurrence / ROI clamp / Camera / Native / KiwiFaceMotion = NO CHANGE.
- PowerShell 5.1 only.
- fail closed if Unity editor identity is not exactly 6000.0.80f1.
- output one consolidated REPORT.
