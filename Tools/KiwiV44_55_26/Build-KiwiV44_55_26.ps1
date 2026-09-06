param([string]$ProjectRoot = "D:\KiwiAvatarSystem")

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0
$modulePath = Join-Path $PSScriptRoot "..\KiwiPowerShell\KiwiPsCompat.psm1"
Import-Module -Name $modulePath -Force
$ProjectRoot = Resolve-KiwiProjectRoot -ProjectRoot $ProjectRoot
$allowedRoots = @($ProjectRoot)
$template = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot "Tools\KiwiV44_55_25\Build-KiwiV44_55_25.ps1") -AllowedRoots $allowedRoots -Label "v25 build template"
$generated = Assert-KiwiPathWithinRoot -Path (Join-Path $PSScriptRoot "Build-KiwiV44_55_26.generated.ps1") -AllowedRoots $allowedRoots -Label "generated v26 build"
if (Test-Path -LiteralPath $generated) { throw "Refusing stale generated build script: $generated" }

$extraProtected = @{
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_25.cs") = "90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Optimization\KiwiOrtDirectMLShadowRuntime.cs") = "B1D0A24F9DE459FA9AE1E7B420B4CE73C15585042B662C2E92DEE4A9165DE436"
}
foreach ($path in @($extraProtected.Keys)) {
    [void](Assert-KiwiFileSha256 -Path ([string]$path) -ExpectedSha256 ([string]$extraProtected[$path]) -Label "Before build extra protected")
}

$source = [System.IO.File]::ReadAllText($template)
$source = $source.Replace("44_55_25", "44_55_26")
$source = $source.Replace("44.55.25", "44.55.26")
$source = $source.Replace("v44_55_26ProductionScheduleTransactionTrace", "v44_55_26ProductionDecodePayloadTransactionClosure")
$source = $source.Replace("PRODUCTION_SCHEDULE_TRANSACTION_TRACE", "PRODUCTION_DECODE_PAYLOAD_TRANSACTION_CLOSURE")
$source = $source.Replace("90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814", "A4CDF5A9FF424E56D0ADA15D09AD0BED00B8BEE6B517F59463A2C15E068845DC")
$source = $source.Replace("4CE42873100AF234F45C23C3F5B0D6B8E96B0D34B94FCAEA5E3E5A20EC1A9874", "82BA94DA1511BE36A4284275A78E41E95030B9034A983CF4ACADEC32072260F6")
$source = $source.Replace("805E746E453F3785FCAA46EFB07745B0038A428789D4A09BB944854F15477FBA", "CC6918E20708DD490AA0E7758CDF9BC5F9D03885A248B4078FC1F0FD70665FDA")

try {
    [void](Write-KiwiTextFile -Path $generated -Text $source -AllowedRoots $allowedRoots -Encoding "Utf8NoBom")
    & "C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $generated -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Generated v26 build failed with exit $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $generated -PathType Leaf) {
        [System.IO.File]::Delete($generated)
    }
}

foreach ($path in @($extraProtected.Keys)) {
    [void](Assert-KiwiFileSha256 -Path ([string]$path) -ExpectedSha256 ([string]$extraProtected[$path]) -Label "After build extra protected")
}
Write-Host "V44_55_26_GUARDED_BUILD_PASS"
