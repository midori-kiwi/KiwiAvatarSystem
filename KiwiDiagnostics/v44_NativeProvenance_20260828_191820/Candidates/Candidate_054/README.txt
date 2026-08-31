KiwiAvatarSystem v43 Cadence No-CSV Probe
============================================

PURPOSE
-------
Test whether Development diagnostics are reducing Unity render/presentation cadence
before changing the tracking architecture.

This package adds ONE observer-only runtime file:
Assets\KiwiAvatarSystem\Runtime\Validation\KiwiCadenceNoCsvProbe.cs

It does NOT modify:
- FaceLandmarkerRunner.cs
- KiwiInferenceFaceTracker.cs
- KiwiFaceMotion.cs
- WindowsNativeWebCamSource.cs
- KiwiNativeCameraPlugin.cpp
- thresholds
- ROI math
- provider/root authority
- FaceParts
- Zero-Copy bridge
- presentation texture policy

INSTALL
-------
PowerShell:

Set-ExecutionPolicy -Scope Process Bypass

.\Installer\Apply-v43-CadenceNoCsvProbe.ps1 -ProjectRoot "D:\KiwiAvatarSystem"
.\Installer\Validate-v43-CadenceNoCsvProbe.ps1 -ProjectRoot "D:\KiwiAvatarSystem"

Then rebuild the Development Windows DX12 Standalone with the project's current
KiwiStandaloneBuildDiagnostic.BuildWindows64 flow.

RUNTIME TEST
------------
Keep Zero-Copy OFF for all 4 runs.

Do NOT press F9.
Do NOT record MP4.
Front/static only.

Run order:
  O1 -> N1 -> N2 -> O2

O = normal Development overlay enabled, CSV OFF
N = Frame Comparison Overlay component disabled, CSV OFF

PowerShell environment for O:
  $env:KIWI_CAMERA_CAPTURE_TRANSPORT="B"
  $env:KIWI_ORT_DML_ZERO_COPY_SHADOW="0"
  $env:KIWI_ORT_DML_SHADOW="0"
  $env:KIWI_CADENCE_NODIAG_PROBE="1"
  $env:KIWI_CADENCE_NODIAG_DISABLE_OVERLAY="0"

PowerShell environment for N:
  $env:KIWI_CAMERA_CAPTURE_TRANSPORT="B"
  $env:KIWI_ORT_DML_ZERO_COPY_SHADOW="0"
  $env:KIWI_ORT_DML_SHADOW="0"
  $env:KIWI_CADENCE_NODIAG_PROBE="1"
  $env:KIWI_CADENCE_NODIAG_DISABLE_OVERLAY="1"

The probe waits 10 seconds, measures 30 seconds, then writes exactly one small TXT:
%USERPROFILE%\AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem\KiwiCadenceNoCsvProbe\

Upload:
- O1 TXT
- N1 TXT
- N2 TXT
- O2 TXT
- final Player.log

INTERPRETATION
--------------
If OVERLAY_OFF raises render/present/schedule strongly toward 60 Hz:
  diagnostic instrumentation is a major confounder; optimize/disable validation
  instrumentation before redesigning the production tracking lane.

If OVERLAY_OFF remains around the current ~32-40 Hz:
  the render-bound presentation/scheduling architecture is the real immediate
  cadence ceiling; proceed to a dedicated capture-cadence latest-only tracking lane.

Threshold changes are not part of this test.
