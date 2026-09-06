param(
    [string]$ProjectRoot = 'D:\KiwiAvatarSystem',
    [ValidateSet('ALL','DIRECT_GRAPHICS','COMMAND_BUFFER_GRAPHICS','COMMAND_BUFFER_ASYNC')]
    [string]$Mode = 'ALL',
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -lt 1) {
    throw "Windows PowerShell 5.1 is required. Current=$($PSVersionTable.PSVersion)"
}
if ($TimeoutSeconds -lt 180 -or $TimeoutSeconds -gt 600) {
    throw 'TimeoutSeconds must be between 180 and 600.'
}

function Get-Sha256([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Identity file missing: $Path"
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Resolve-SinglePackageJson([string]$Cache, [string]$Pattern) {
    $found = @(
        Get-ChildItem -LiteralPath $Cache -Directory -Filter $Pattern |
            ForEach-Object { Join-Path $_.FullName 'package.json' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
    )
    if ($found.Count -ne 1) {
        throw "Expected one package.json for $Pattern; count=$($found.Count)"
    }
    return $found[0]
}

$BuildRoot = Join-Path $ProjectRoot 'Builds\v44_55_29CommandBufferGraphicsExecutionFormIsolation'
$ExePath = Join-Path $BuildRoot 'KiwiAvatarSystem_v44_55_29_COMMAND_BUFFER_GRAPHICS_EXECUTION_FORM_ISOLATION.exe'
$DataRoot = Join-Path $BuildRoot 'KiwiAvatarSystem_v44_55_29_COMMAND_BUFFER_GRAPHICS_EXECUTION_FORM_ISOLATION_Data'
$PackageCache = Join-Path $ProjectRoot 'Library\PackageCache'
$identityPaths = [ordered]@{
    EXE = $ExePath
    ASSEMBLY_CSHARP = (Join-Path $DataRoot 'Managed\Assembly-CSharp.dll')
    INFERENCE_ENGINE_ASSEMBLY = (Join-Path $DataRoot 'Managed\Unity.InferenceEngine.dll')
    MEDIAPIPE_RUNTIME_ASSEMBLY = (Join-Path $DataRoot 'Managed\Mediapipe.Runtime.dll')
    INFERENCE_MODEL = (Join-Path $DataRoot 'StreamingAssets\KiwiFaceLandmarkInference.onnx')
    MEDIAPIPE_MODEL = (Join-Path $DataRoot 'StreamingAssets\face_landmarker_v2_with_blendshapes.bytes')
    NATIVE_BUILD = (Join-Path $DataRoot 'Plugins\x86_64\KiwiNativeCamera.dll')
    MEDIAPIPE_NATIVE_BUILD = (Join-Path $DataRoot 'Plugins\x86_64\mediapipe_c.dll')
    NATIVE_PRODUCTION = (Join-Path $ProjectRoot 'Assets\Plugins\x86_64\KiwiNativeCamera.dll')
    TRACKER = (Join-Path $ProjectRoot 'Assets\Script\KiwiInferenceFaceTracker.cs')
    FACE_LANDMARKER_RUNNER = (Join-Path $ProjectRoot 'Assets\Script\FaceLandmarkerRunner.cs')
    KIWI_FACE_MOTION = (Join-Path $ProjectRoot 'Assets\Script\KiwiFaceMotion.cs')
    PACKAGES_MANIFEST = (Join-Path $ProjectRoot 'Packages\manifest.json')
    PACKAGES_LOCK = (Join-Path $ProjectRoot 'Packages\packages-lock.json')
    INFERENCE_PACKAGE_JSON = (Resolve-SinglePackageJson $PackageCache 'com.unity.ai.inference@*')
    MEDIAPIPE_PACKAGE_JSON = (Resolve-SinglePackageJson $PackageCache 'com.github.homuler.mediapipe@*')
    SOURCE_PACKAGE_ZIP = 'D:\Users\main\Downloads\KiwiAvatarSystem_v44_55_29_CommandBufferGraphicsExecutionFormIsolation.zip'
    VENDOR_HOTFIX_PACKAGE_ZIP = 'D:\Users\main\Downloads\KiwiAvatarSystem_v44_55_29_1_PowerShell51BuildHotfix.zip'
    RUN_SCRIPT = $MyInvocation.MyCommand.Path
    RUNTIME_VALIDATOR = (Join-Path $ProjectRoot 'Tools\KiwiV44_55_29\Validate-Runtime-KiwiV44_55_29.ps1')
}
$identityHashes = [ordered]@{}
foreach ($entry in $identityPaths.GetEnumerator()) {
    $identityHashes[$entry.Key] = Get-Sha256 ([string]$entry.Value)
}
if ($identityHashes['SOURCE_PACKAGE_ZIP'] -ne 'D4C5B5001E3730D6E9C03B803209C50E7C20EBF96EB31226C53908A9F0B0E0D7') {
    throw 'Source package SHA drift.'
}
if ($identityHashes['VENDOR_HOTFIX_PACKAGE_ZIP'] -ne 'D4EE1B7C37B202C79A09AADE2A13795A565B9894BE423BE022284A8BFB11A8CB') {
    throw 'Vendor hotfix package SHA drift.'
}

function Assert-Identity([string]$Boundary) {
    foreach ($entry in $identityPaths.GetEnumerator()) {
        $actual = Get-Sha256 ([string]$entry.Value)
        if ($actual -ne $identityHashes[$entry.Key]) {
            throw "$Boundary identity drift $($entry.Key) expected=$($identityHashes[$entry.Key]) actual=$actual"
        }
    }
}

$PersistentEvidence = Join-Path $env:USERPROFILE 'AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem\KiwiFrameBottleneck'
if (-not (Test-Path -LiteralPath $PersistentEvidence -PathType Container)) {
    New-Item -ItemType Directory -Path $PersistentEvidence -Force | Out-Null
}
$Stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$EvidenceRoot = Join-Path $ProjectRoot ("KiwiValidation\RuntimeEvidence_v44_55_29_" + $Stamp)
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
$Modes = if ($Mode -eq 'ALL') {
    @('DIRECT_GRAPHICS','COMMAND_BUFFER_GRAPHICS','COMMAND_BUFFER_ASYNC')
}
else {
    @($Mode)
}

function Copy-NewEvidence([DateTime]$StartedUtc, [string]$ArmDir) {
    foreach ($pattern in @(
        'KiwiCommonTensorBackendStageIsolation_v44_55_20_*.*',
        'KiwiPairBoundShadowOutputSnapshot_v44_55_24_*.*',
        'KiwiProductionScheduleTransactionTrace_v44_55_25_*.*',
        'KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_*.*'
    )) {
        $files = Get-ChildItem -LiteralPath $PersistentEvidence -Filter $pattern -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -ge $StartedUtc.AddSeconds(-2) }
        foreach ($file in $files) {
            Copy-Item -LiteralPath $file.FullName -Destination $ArmDir -Force
        }
    }
}

foreach ($arm in $Modes) {
    Assert-Identity ($arm + '_PRELAUNCH')
    $ArmDir = Join-Path $EvidenceRoot $arm
    New-Item -ItemType Directory -Path $ArmDir -Force | Out-Null
    $PlayerLog = Join-Path $ArmDir 'Player.log'
    $RunInfo = Join-Path $ArmDir 'run_identity.txt'

    $asyncValue = if ($arm -eq 'COMMAND_BUFFER_ASYNC') { '1' } else { '0' }
    $cbGraphicsValue = if ($arm -eq 'COMMAND_BUFFER_GRAPHICS') { '1' } else { '0' }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ExePath
    $psi.WorkingDirectory = Split-Path -Parent $ExePath
    $psi.UseShellExecute = $false
    $psi.Arguments = '-force-d3d12 -screen-width 1280 -screen-height 720 -logFile "' + $PlayerLog + '"'

    $settings = [ordered]@{
        KIWI_INFERENCE_ASYNC_COMPUTE_PROBE = $asyncValue
        KIWI_INFERENCE_COMMAND_BUFFER_GRAPHICS_PROBE = $cbGraphicsValue
        KIWI_V44_55_20_COMMON_TENSOR_AUDIT = '1'
        KIWI_V44_55_20_COMMON_TENSOR_SECONDS = '120'
        KIWI_V44_55_20_COMMON_TENSOR_HZ = '1'
        KIWI_V44_55_20_STABLE_SECONDS = '8'
        KIWI_V44_55_24_PAIR_BOUND_SHADOW_OUTPUT_AUDIT = '1'
        KIWI_V44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE = '1'
        KIWI_V44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUDIT = '1'
        KIWI_V44_55_29_AUTO_QUIT = '1'
        KIWI_V44_55_29_AUTO_QUIT_TIMEOUT_SEC = [string]($TimeoutSeconds - 30)
    }
    foreach ($entry in $settings.GetEnumerator()) {
        $psi.EnvironmentVariables[[string]$entry.Key] = [string]$entry.Value
    }
    foreach ($key in @(
        'KIWI_INFERENCE_CADENCE_BUDGET_HZ',
        'KIWI_V44_55_21_CROP_SAMPLER_AUDIT',
        'KIWI_V44_55_22_REFERENCE_TRANSACTION_AUDIT',
        'KIWI_V44_55_23_PRODUCTION_OUTPUT_AUTHORITY_AUDIT',
        'KIWI_V44_55_26_PRODUCTION_DECODE_PAYLOAD_TRACE',
        'KIWI_ORT_DML_SHADOW',
        'KIWI_ORT_DML_ZERO_COPY_SHADOW'
    )) {
        [void]$psi.EnvironmentVariables.Remove($key)
    }

    $StartedUtc = [DateTime]::UtcNow
    $lines = New-Object 'System.Collections.Generic.List[string]'
    $lines.Add('contract=KIWI_V44_55_29_COMMAND_BUFFER_GRAPHICS_EXECUTION_FORM_ISOLATION')
    $lines.Add('mode=' + $arm)
    $lines.Add('startedUtc=' + $StartedUtc.ToString('O'))
    $lines.Add('sameBuildRequired=1')
    $lines.Add('armSelectionDifferenceOnly=1')
    $lines.Add('v20DependencyOnlyNotClaimAuthority=1')
    foreach ($entry in $identityPaths.GetEnumerator()) {
        $lines.Add('identity.' + $entry.Key + '.path=' + [string]$entry.Value)
        $lines.Add('identity.' + $entry.Key + '.sha256=' + $identityHashes[$entry.Key])
    }
    foreach ($entry in $settings.GetEnumerator()) {
        $lines.Add('ENV.' + [string]$entry.Key + '=' + [string]$entry.Value)
    }
    $lines.Add('ENV.KIWI_INFERENCE_CADENCE_BUDGET_HZ=<UNSET>')
    $lines.ToArray() | Set-Content -LiteralPath $RunInfo -Encoding UTF8

    Write-Host "[RUN] $arm v20_dependency_only=1" -ForegroundColor Cyan
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    if (-not $process.Start()) { throw "Failed to launch $arm" }
    $exited = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        try { $process.Kill() } catch {}
        Copy-NewEvidence $StartedUtc $ArmDir
        throw "$arm launcher timeout after $TimeoutSeconds seconds."
    }
    $exitCode = $process.ExitCode
    Add-Content -LiteralPath $RunInfo -Encoding UTF8 -Value ('exitCode=' + $exitCode)
    Add-Content -LiteralPath $RunInfo -Encoding UTF8 -Value ('endedUtc=' + [DateTime]::UtcNow.ToString('O'))
    Copy-NewEvidence $StartedUtc $ArmDir
    Assert-Identity ($arm + '_POSTEXIT')
    if ($exitCode -ne 0) {
        throw "$arm exited with code $exitCode. Evidence=$ArmDir"
    }

    $v27Txt = @(Get-ChildItem -LiteralPath $ArmDir -Filter 'KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_*.txt' -File)
    $v27Csv = @(Get-ChildItem -LiteralPath $ArmDir -Filter 'KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_*.csv' -File)
    if ($v27Txt.Count -ne 1 -or $v27Csv.Count -ne 1) {
        throw "$arm fresh v27 TXT/CSV count invalid: $($v27Txt.Count)/$($v27Csv.Count)"
    }
    $v27Text = [System.IO.File]::ReadAllText($v27Txt[0].FullName)
    if ($v27Text.IndexOf('status=COMPLETE', [StringComparison]::Ordinal) -lt 0) {
        throw "$arm v27 status is not COMPLETE."
    }
    Write-Host "[PASS] $arm normal exit + v27 COMPLETE + identity stable" -ForegroundColor Green
    Start-Sleep -Seconds 3
}

$EvidenceZip = $EvidenceRoot + '.zip'
Compress-Archive -Path (Join-Path $EvidenceRoot '*') -DestinationPath $EvidenceZip -CompressionLevel Optimal
$EvidenceZipSha = Get-Sha256 $EvidenceZip
Write-Host ''
Write-Host 'KIWI_V44_55_29_RUNTIME_COLLECTION_PASS' -ForegroundColor Green
Write-Host "EvidenceRoot: $EvidenceRoot"
Write-Host "EvidenceZIP: $EvidenceZip"
Write-Host "EvidenceZIP SHA256: $EvidenceZipSha"
