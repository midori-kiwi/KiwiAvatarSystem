KiwiAvatarSystem v44 Native Provenance Recovery
================================================

Purpose
-------
The current installed KiwiNativeCamera.dll implements Path B
(System-Memory NV12), but the matching current C++ source was not found by the
first source locator.

This recovery pass searches:
- source files under Kiwi/project/download/temp/work folders;
- ZIP contents even when the C++ filename changed;
- PowerShell / PSReadLine history for native build/package paths;
- Windows Recent shortcut targets;
- .7z archive listings when 7-Zip is installed.

Matching rule
-------------
The strongest source must contain all four current Path B exports:

KiwiNativeCamera_GetCaptureTransportId
KiwiNativeCamera_GetLatestCpuNv12CopyMicroseconds
KiwiNativeCamera_GetCpuLatestReplacementCount
KiwiNativeCamera_GetLatestGpuUploadSubmitMicroseconds

An old KiwiNativeCameraPlugin.cpp without those symbols is NOT accepted.

Run
---
Set-ExecutionPolicy -Scope Process Bypass

.\Tools\Recover-v44-NativeProvenance.ps1 `
  -ProjectRoot "D:\KiwiAvatarSystem"

Upload:
D:\KiwiAvatarSystem\KiwiDiagnostics\v44_NativeProvenance_YYYYMMDD_HHMMSS.zip

Safety
------
Read/search/copy only. It does not modify Assets, DLLs, tracking, camera,
thresholds, ROI, authority, or ProjectSettings.
