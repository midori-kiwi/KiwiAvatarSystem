param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem"
)

$ErrorActionPreference = "Stop"
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackageRoot = Split-Path -Parent $ScriptRoot
$BuildScript = Join-Path $ScriptRoot "Build-KiwiNativeCamera-v44.55.12.ps1"
$BuildDir = Join-Path $PackageRoot "Build"

$PackageNative = Join-Path $PackageRoot "Native\KiwiNativeCameraPlugin.cpp"
$PackageInterop = Join-Path $PackageRoot "Runtime\KiwiNativeCameraInterop.cs"
$PackageObserver = Join-Path $PackageRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiD3DFixedPointSubtexelSamplingParityAuditV44_55_12.cs"

$ProjectNative = Join-Path $ProjectRoot "Native\KiwiNativeCamera\Source\KiwiNativeCameraPlugin.cpp"
$ProjectInterop = Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Camera\KiwiNativeCameraInterop.cs"
$ProjectDll = Join-Path $ProjectRoot "Assets\Plugins\x86_64\KiwiNativeCamera.dll"
$ProjectObserver = Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiD3DFixedPointSubtexelSamplingParityAuditV44_55_12.cs"
$OldObserver = Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiIdentityCorrectedCpuGpuOutputEquivalenceAuditV44_55_11.cs"

$BaseNative = "367D6407803220DD141587F16B2F8A4B76E908B53A1A558C67882BB6BE285959"
$BaseInterop = "3309490628D44BA2FC57718E81B25C6C31E9672252E5127D51DA9D292738E163"
$BaseObserver = "C1A4D51B09E730630752215D43D59310630F437742C13BA55E71DA124937A9AD"
$NewNative = "636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A"
$NewInterop = "AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5"
$NewObserver = "BE98143C691D2980013599135FE7F5083EC446C709006CF5BCFF64A22F10EF58"

foreach ($path in @($BuildScript,$PackageNative,$PackageInterop,$PackageObserver,$ProjectNative,$ProjectInterop,$ProjectDll)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required v44.55.12 file missing: $path" }
}

$currentNative = (Get-FileHash -Algorithm SHA256 -LiteralPath $ProjectNative).Hash
$currentInterop = (Get-FileHash -Algorithm SHA256 -LiteralPath $ProjectInterop).Hash
if ($currentNative -notin @($BaseNative,$NewNative)) { throw "Unexpected Native base: $currentNative" }
if ($currentInterop -notin @($BaseInterop,$NewInterop)) { throw "Unexpected Interop base: $currentInterop" }

if (Test-Path -LiteralPath $OldObserver) {
    $oldHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $OldObserver).Hash
    if ($oldHash -ne $BaseObserver) { throw "Unexpected v44.55.11 observer hash: $oldHash" }
}

& $BuildScript -ProjectRoot $ProjectRoot -OutputDirectory $BuildDir
$BuiltDll = Join-Path $BuildDir "KiwiNativeCamera.dll"
if (-not (Test-Path -LiteralPath $BuiltDll)) { throw "Built DLL missing: $BuiltDll" }

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$backup = Join-Path $ProjectRoot ("KiwiBackups\v44_55_12_" + $stamp)
New-Item -ItemType Directory -Force -Path $backup | Out-Null
foreach ($item in @(
    @{Path=$ProjectNative;Name="KiwiNativeCameraPlugin.cpp"},
    @{Path=$ProjectInterop;Name="KiwiNativeCameraInterop.cs"},
    @{Path=$ProjectDll;Name="KiwiNativeCamera.dll"},
    @{Path=$ProjectObserver;Name="KiwiD3DFixedPointSubtexelSamplingParityAuditV44_55_12.cs"},
    @{Path=$OldObserver;Name="KiwiIdentityCorrectedCpuGpuOutputEquivalenceAuditV44_55_11.cs"}
)) {
    if (Test-Path -LiteralPath $item.Path) { Copy-Item -LiteralPath $item.Path -Destination (Join-Path $backup $item.Name) -Force }
}

Copy-Item -LiteralPath $PackageNative -Destination $ProjectNative -Force
Copy-Item -LiteralPath $PackageInterop -Destination $ProjectInterop -Force
Copy-Item -LiteralPath $PackageObserver -Destination $ProjectObserver -Force
Copy-Item -LiteralPath $BuiltDll -Destination $ProjectDll -Force
if (Test-Path -LiteralPath $OldObserver) { Remove-Item -LiteralPath $OldObserver -Force }

Write-Host "V44_55_12_APPLY_PASS"
Write-Host "Backup: $backup"
Write-Host "Native SHA256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $ProjectNative).Hash)"
Write-Host "Interop SHA256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $ProjectInterop).Hash)"
Write-Host "DLL SHA256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $ProjectDll).Hash)"
Write-Host "Observer SHA256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $ProjectObserver).Hash)"
