param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",
    [int]$AuditSeconds = 60,
    [int]$ScreenWidth = 1280,
    [int]$ScreenHeight = 720
)
$ErrorActionPreference = "Stop"
$Unity = "C:\Program Files\Unity\Hub\Editor\6000.0.80f1\Editor\Unity.exe"
$BuildDir = Join-Path $ProjectRoot "Builds\v44_55_12D3DFixedPointSubtexelAudit"
$Exe = Join-Path $BuildDir "KiwiAvatarSystem_v44_55_12_SUBTEXEL.exe"
$BuildLog = Join-Path $BuildDir "KiwiStandalone_v44_55_12_SUBTEXEL.build.log"
$Observer = Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiD3DFixedPointSubtexelSamplingParityAuditV44_55_12.cs"
$SteadyAudit = Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiSteadyStateBottleneckAuditV44_47.cs"
$Dll = Join-Path $ProjectRoot "Assets\Plugins\x86_64\KiwiNativeCamera.dll"
foreach ($path in @($Unity,$Observer,$SteadyAudit,$Dll)) { if (-not (Test-Path -LiteralPath $path)) { throw "Required file missing: $path" } }
New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
$env:KIWI_V42_3_BUILD_OUTPUT = $Exe
if (Test-Path $Exe) { Remove-Item -LiteralPath $Exe -Force }
$DataDir = Join-Path $BuildDir "KiwiAvatarSystem_v44_55_12_SUBTEXEL_Data"
if (Test-Path $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
$unityArgs = @("-batchmode","-quit","-projectPath","`"$ProjectRoot`"","-buildTarget","StandaloneWindows64","-executeMethod","KiwiStandaloneBuildDiagnostic.BuildWindows64","-logFile","`"$BuildLog`"")
$build = Start-Process -FilePath $Unity -ArgumentList $unityArgs -Wait -PassThru -NoNewWindow
if ($build.ExitCode -ne 0 -or -not (Test-Path $Exe)) { if (Test-Path $BuildLog) { Get-Content -LiteralPath $BuildLog -Tail 200 }; throw "Unity build failed. ExitCode=$($build.ExitCode)" }
Write-Host "V44_55_12_BUILD_PASS"
$controlledNames = @("KIWI_V44_55_11_OUTPUT_EQ_AUDIT","KIWI_V44_55_12_SUBTEXEL_AUDIT","KIWI_V44_55_12_SUBTEXEL_SECONDS","KIWI_V44_55_12_SUBTEXEL_HZ","KIWI_V44_55_12_STABLE_SECONDS","KIWI_V44_55_12_EXPECTED_TRIANGLES","KIWI_V44_47_STEADY_AUDIT","KIWI_V44_47_STEADY_AUDIT_SECONDS","KIWI_V44_47_STABLE_SECONDS","KIWI_V44_47_EXPECTED_TRIANGLES")
$saved = @{}
foreach ($name in $controlledNames) { $saved[$name] = [Environment]::GetEnvironmentVariable($name,"Process"); Remove-Item ("Env:"+$name) -ErrorAction SilentlyContinue }
$env:KIWI_V44_55_12_SUBTEXEL_AUDIT = "1"
$env:KIWI_V44_55_12_SUBTEXEL_SECONDS = [string]$AuditSeconds
$env:KIWI_V44_55_12_SUBTEXEL_HZ = "1"
$env:KIWI_V44_55_12_STABLE_SECONDS = "8"
$env:KIWI_V44_55_12_EXPECTED_TRIANGLES = "254296"
$env:KIWI_V44_47_STEADY_AUDIT = "1"
$env:KIWI_V44_47_STEADY_AUDIT_SECONDS = [string]$AuditSeconds
$env:KIWI_V44_47_STABLE_SECONDS = "8"
$env:KIWI_V44_47_EXPECTED_TRIANGLES = "254296"
try {
    $player = Start-Process -FilePath $Exe -ArgumentList @("-force-d3d12","-screen-width",[string]$ScreenWidth,"-screen-height",[string]$ScreenHeight) -WorkingDirectory $BuildDir -PassThru
    Write-Host "V44_55_12_LAUNCH_PASS"
    Write-Host "PID: $($player.Id)"
} finally {
    foreach ($name in $controlledNames) { $old=$saved[$name]; if ($null -eq $old) { Remove-Item ("Env:"+$name) -ErrorAction SilentlyContinue } else { [Environment]::SetEnvironmentVariable($name,$old,"Process") } }
}
Write-Host ""
Write-Host "Wait for BOTH:"
Write-Host "  [Kiwi v44.55.12 Subtexel] MEASURE_START"
Write-Host "  [Kiwi v44.47 Steady Audit] MEASURE_START"
Write-Host "Record CSV + MP4 for at least $AuditSeconds seconds."
Write-Host "Upload KiwiSubtexelParity_v44_55_12_*.txt / v44.47 txt / CSV / MP4 / Player.log"
