param([string]$ProjectRoot = "D:\KiwiAvatarSystem")
$ErrorActionPreference = "Stop"
$BackupRoot = Join-Path $ProjectRoot "KiwiBackups"
$latest = Get-ChildItem -LiteralPath $BackupRoot -Directory -Filter "v44_55_12_*" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $latest) { throw "No v44.55.12 backup found." }
$mapping = @(
    @{Dst=(Join-Path $ProjectRoot "Native\KiwiNativeCamera\Source\KiwiNativeCameraPlugin.cpp");Name="KiwiNativeCameraPlugin.cpp"},
    @{Dst=(Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Camera\KiwiNativeCameraInterop.cs");Name="KiwiNativeCameraInterop.cs"},
    @{Dst=(Join-Path $ProjectRoot "Assets\Plugins\x86_64\KiwiNativeCamera.dll");Name="KiwiNativeCamera.dll"},
    @{Dst=(Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiD3DFixedPointSubtexelSamplingParityAuditV44_55_12.cs");Name="KiwiD3DFixedPointSubtexelSamplingParityAuditV44_55_12.cs"},
    @{Dst=(Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiIdentityCorrectedCpuGpuOutputEquivalenceAuditV44_55_11.cs");Name="KiwiIdentityCorrectedCpuGpuOutputEquivalenceAuditV44_55_11.cs"}
)
foreach ($item in $mapping) {
    $saved = Join-Path $latest.FullName $item.Name
    if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination $item.Dst -Force }
    elseif ($item.Name -eq "KiwiD3DFixedPointSubtexelSamplingParityAuditV44_55_12.cs" -and (Test-Path -LiteralPath $item.Dst)) { Remove-Item -LiteralPath $item.Dst -Force }
}
Write-Host "V44_55_12_ROLLBACK_PASS"
Write-Host "Backup: $($latest.FullName)"
