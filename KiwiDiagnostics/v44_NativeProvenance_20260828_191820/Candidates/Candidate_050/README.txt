KiwiAvatarSystem v44 Native Source Locator
===========================================

Why this is needed
------------------
The v44 Source Audit contains the CURRENT installed KiwiNativeCamera.dll and
current C# interop, but not the matching current KiwiNativeCameraPlugin.cpp.

The older 2026-08-23 source is not safe to patch because it predates the
System-Memory NV12 Path B exports currently present in the installed DLL.

Run
---
Set-ExecutionPolicy -Scope Process Bypass
.\Tools\Find-v44-CurrentNativeSource.ps1 -ProjectRoot "D:\KiwiAvatarSystem"

Output
------
D:\KiwiAvatarSystem\KiwiDiagnostics\v44_NativeSource_YYYYMMDD_HHMMSS.zip

Upload the generated ZIP.

Safety
------
Read-only search/copy. It does not modify Assets, Native DLLs, tracking code,
thresholds, ROI, authority, or project settings.
