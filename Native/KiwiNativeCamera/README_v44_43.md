# KiwiNativeCamera — v44.43 Production Mode 9

This directory is the formal source-of-truth for the Windows Native Camera
Production mode 9 pipeline validated in KiwiAvatarSystem v44.42.

## Runtime-validated source

`Source/KiwiNativeCameraPlugin.cpp`

SHA256:

`8C28F7A9E22DF9DA66455C37777069E5922037DB62AD7897DBDFEC8931975E03`

This is byte-identical to the C++ source used for the v44.42 metadata-driven
color + shared-tier-parity runtime validation.

## Preserved architecture

- Media Foundation Source Reader
- no `MF_SOURCE_READER_D3D_MANAGER` in Production Path B
- system-memory NV12 capture
- immediate sample release
- latest-frame priority / no FIFO
- 3 CPU latest slots + 3 GPU slots
- D3D11 upload/compute
- D3D12 compatibility reverse sharing
- no CPU-blocking GPU wait
- fixed Unity-visible presentation identity
- metadata-driven `MF_MT_VIDEO_NOMINAL_RANGE`
- metadata-driven `MF_MT_YUV_MATRIX`
- BT.601 Full/Limited + BT.709 Full/Limited
- real `D3D12_FEATURE_D3D12_OPTIONS4.SharedResourceCompatibilityTier` query

## Known limitation

`KiwiNativeCamera_StartDiagnostic` keeps the 66-export ABI but modes 1–8 return
`E_NOTIMPL`.

This is deliberate. The reconstructed source has proven Production mode 9
behavior, but the exact historical semantics of diagnostic modes 1–8 were not
recovered. They are not silently aliased to mode 9.

Normal Kiwi Production camera startup uses `KiwiNativeCamera_Start`, which
selects mode 9 and was runtime-validated in v44.41/v44.42.

## Rebuild

PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass

cd "D:\KiwiAvatarSystem\Native\KiwiNativeCamera"

.\BuildProduction_v44_43.ps1 -ProjectRoot "D:\KiwiAvatarSystem"
```

To build and install into `Assets\Plugins\x86_64`:

```powershell
.\BuildProduction_v44_43.ps1 -ProjectRoot "D:\KiwiAvatarSystem" -Install
```

The build script verifies the exact source/ABI/HLSL hashes, the 66-export ABI,
the HLSL compile probe, and frozen Path B prohibitions before installation.
