param([string]$ProjectRoot = "D:\KiwiAvatarSystem")

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0
$modulePath = Join-Path $PSScriptRoot "..\KiwiPowerShell\KiwiPsCompat.psm1"
Import-Module -Name $modulePath -Force
$ProjectRoot = Resolve-KiwiProjectRoot -ProjectRoot $ProjectRoot
$allowedRoots = @($ProjectRoot)
$template = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot "Tools\KiwiV44_55_25\Build-KiwiV44_55_25.ps1") -AllowedRoots $allowedRoots -Label "v25 build template"
$generated = Assert-KiwiPathWithinRoot -Path (Join-Path $PSScriptRoot "Build-KiwiV44_55_27.generated.ps1") -AllowedRoots $allowedRoots -Label "generated v27 build"
if (Test-Path -LiteralPath $generated) { throw "Refusing stale generated build script: $generated" }

$extraProtected = @{
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_25.cs") = "90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_26.cs") = "A4CDF5A9FF424E56D0ADA15D09AD0BED00B8BEE6B517F59463A2C15E068845DC"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Optimization\KiwiOrtDirectMLShadowRuntime.cs") = "D30A700A7B9E3C3D09BC64FA0CA5722729EE41821DE1F1B5E30E30D986B947C2"
}
foreach ($path in @($extraProtected.Keys)) {
    [void](Assert-KiwiFileSha256 -Path ([string]$path) -ExpectedSha256 ([string]$extraProtected[$path]) -Label "Before build extra protected")
}

$source = [System.IO.File]::ReadAllText($template)
$source = $source.Replace("44_55_25", "44_55_27")
$source = $source.Replace("44.55.25", "44.55.27")
$source = $source.Replace("KiwiProductionScheduleTransactionTraceV44_55_27.cs", "KiwiActualProductionVsShadowPayloadAuthorityV44_55_27.cs")
$source = $source.Replace("v44_55_27ProductionScheduleTransactionTrace", "v44_55_27ActualProductionVsShadowPayloadAuthority")
$source = $source.Replace("PRODUCTION_SCHEDULE_TRANSACTION_TRACE", "ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUTHORITY")
$source = $source.Replace("90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814", "B8E215D1C3D4103996B7B30BFD8B26DB508782AC6529D12BEF2617E9C65C8B86")
$source = $source.Replace("4CE42873100AF234F45C23C3F5B0D6B8E96B0D34B94FCAEA5E3E5A20EC1A9874", "3FF3511AAA2EA67E8177C7CCAC7FF4990CE20286B5DBA973303CEE33D7FB809A")
$source = $source.Replace("805E746E453F3785FCAA46EFB07745B0038A428789D4A09BB944854F15477FBA", "1F8F26D21DEA1DE17DE43A0A7B8FFA0513C1C9E23A65A1AEDFCFF7BA54AB26EC")

try {
    [void](Write-KiwiTextFile -Path $generated -Text $source -AllowedRoots $allowedRoots -Encoding "Utf8NoBom")
    & "C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $generated -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Generated v27 build failed with exit $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $generated -PathType Leaf) {
        [System.IO.File]::Delete($generated)
    }
}

foreach ($path in @($extraProtected.Keys)) {
    [void](Assert-KiwiFileSha256 -Path ([string]$path) -ExpectedSha256 ([string]$extraProtected[$path]) -Label "After build extra protected")
}
Write-Host "V44_55_27_GUARDED_BUILD_PASS"
