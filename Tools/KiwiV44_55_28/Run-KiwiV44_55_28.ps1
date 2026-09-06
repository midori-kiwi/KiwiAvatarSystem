param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("ASYNC", "GRAPHICS")]
    [string]$Mode,
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",
    [string]$EvidenceDirectory = "",
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$modulePath = Join-Path $PSScriptRoot "..\KiwiPowerShell\KiwiPsCompat.psm1"
Import-Module -Name $modulePath -Force
$ProjectRoot = Resolve-KiwiProjectRoot -ProjectRoot $ProjectRoot
$allowedRoots = @($ProjectRoot)

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Read-SharedText {
    param([string]$Path)
    $stream = New-Object IO.FileStream(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object IO.StreamReader($stream)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-OnlyFreshArtifact {
    param(
        [string]$Directory,
        [string]$Prefix,
        [string]$Extension,
        [hashtable]$Before,
        [datetime]$StartedUtc
    )
    $matches = @(
        Get-ChildItem -LiteralPath $Directory -File |
            Where-Object {
                $_.Name.StartsWith($Prefix, [StringComparison]::Ordinal) -and
                $_.Extension -eq $Extension -and
                -not $Before.ContainsKey($_.FullName) -and
                $_.LastWriteTimeUtc -ge $StartedUtc.AddSeconds(-2)
            }
    )
    Assert-True ($matches.Count -eq 1) "Expected one fresh $Prefix$Extension artifact; found $($matches.Count)."
    return $matches[0]
}

if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path $ProjectRoot (
        "KiwiValidation\RuntimeEvidence_v44_55_28_WORK\" + $Mode)
}
$EvidenceDirectory = Assert-KiwiPathWithinRoot `
    -Path $EvidenceDirectory `
    -AllowedRoots $allowedRoots `
    -Label "v28 evidence directory"
Assert-True (-not (Test-Path -LiteralPath $EvidenceDirectory)) "Refusing stale evidence directory: $EvidenceDirectory"
[System.IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null
$rollbackRoot = Join-Path $EvidenceDirectory ".rollback"

$buildRoot = Join-Path $ProjectRoot "Builds\v44_55_27ActualProductionVsShadowPayloadAuthority"
$exe = Assert-KiwiFile -Path (Join-Path $buildRoot "KiwiAvatarSystem_v44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUTHORITY.exe") -Label "shared A/B Player"
$dataRoot = Join-Path $buildRoot "KiwiAvatarSystem_v44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUTHORITY_Data"
$assembly = Assert-KiwiFile -Path (Join-Path $dataRoot "Managed\Assembly-CSharp.dll") -Label "shared A/B Assembly-CSharp"
$model = Assert-KiwiFile -Path (Join-Path $dataRoot "StreamingAssets\KiwiFaceLandmarkInference.onnx") -Label "shared A/B inference model"
$mediapipeModel = Assert-KiwiFile -Path (Join-Path $dataRoot "StreamingAssets\face_landmarker_v2_with_blendshapes.bytes") -Label "shared A/B MediaPipe model"
$nativeBuild = Assert-KiwiFile -Path (Join-Path $dataRoot "Plugins\x86_64\KiwiNativeCamera.dll") -Label "shared A/B Native DLL"

$identityPaths = [ordered]@{
    EXE = $exe
    ASSEMBLY_CSHARP = $assembly
    INFERENCE_MODEL = $model
    MEDIAPIPE_MODEL = $mediapipeModel
    NATIVE_BUILD = $nativeBuild
    NATIVE_PRODUCTION = (Join-Path $ProjectRoot "Assets\Plugins\x86_64\KiwiNativeCamera.dll")
    KIWI_INFERENCE_FACE_TRACKER = (Join-Path $ProjectRoot "Assets\Script\KiwiInferenceFaceTracker.cs")
    FACE_LANDMARKER_RUNNER = (Join-Path $ProjectRoot "Assets\Script\FaceLandmarkerRunner.cs")
    KIWI_FACE_MOTION = (Join-Path $ProjectRoot "Assets\Script\KiwiFaceMotion.cs")
    V20 = (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiCommonTensorBackendStageIsolationV44_55_20.cs")
    V24 = (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiPairBoundShadowOutputSnapshotV44_55_24.cs")
    V25 = (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_25.cs")
    V27 = (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiActualProductionVsShadowPayloadAuthorityV44_55_27.cs")
    DIAGNOSTIC_BRIDGE = (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Optimization\KiwiOrtDirectMLShadowRuntime.cs")
    ASYNC_DEFAULT_OWNER = (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Optimization\KiwiInferenceAsyncComputeDefaultV44_22.cs")
    PACKAGE_JSON = (Join-Path $ProjectRoot "Library\PackageCache\com.unity.ai.inference@587873fd5e1b\package.json")
}
foreach ($entry in $identityPaths.GetEnumerator()) {
    [void](Assert-KiwiFile -Path ([string]$entry.Value) -Label ([string]$entry.Key))
}

$expected = @{
    EXE = "98751D0DFF0DD3ADE563C2B505A0E9F7A46E8E8895864212E1B884EDBCB42E81"
    ASSEMBLY_CSHARP = "CB00626ACB12427075D340D28301156AA948B4E394D0D78EBDDA6E6579EA53BF"
    INFERENCE_MODEL = "ED487104519B0A88CB2CB2EC3678E183F447FB9DD63560998E960FFBAA8FB335"
    MEDIAPIPE_MODEL = "B261925D4AAD812B47A0E8D58C1BAA1223270A5D1F663D78338BC881C003879D"
    NATIVE_BUILD = "82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5"
    NATIVE_PRODUCTION = "82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5"
    KIWI_INFERENCE_FACE_TRACKER = "52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695"
    FACE_LANDMARKER_RUNNER = "6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93"
    KIWI_FACE_MOTION = "D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6"
    V20 = "1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3"
    V24 = "1F8F26D21DEA1DE17DE43A0A7B8FFA0513C1C9E23A65A1AEDFCFF7BA54AB26EC"
    V25 = "90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814"
    V27 = "B8E215D1C3D4103996B7B30BFD8B26DB508782AC6529D12BEF2617E9C65C8B86"
    DIAGNOSTIC_BRIDGE = "D30A700A7B9E3C3D09BC64FA0CA5722729EE41821DE1F1B5E30E30D986B947C2"
}
foreach ($name in @($expected.Keys)) {
    [void](Assert-KiwiFileSha256 -Path ([string]$identityPaths[$name]) -ExpectedSha256 ([string]$expected[$name]) -Label "v28 protected $name")
}

$controlledNames = @(
    "KIWI_INFERENCE_ASYNC_COMPUTE_PROBE",
    "KIWI_INFERENCE_CADENCE_BUDGET_HZ",
    "KIWI_INFERENCE_LANE_LIMIT",
    "KIWI_FACE_FLAG_SIGMOID_V3_2",
    "KIWI_NATIVE_CAMERA_MODE",
    "KIWI_CAMERA_PREVIEW_MODE",
    "KIWI_LIVE_CAMERA_COLOR_CORRECTION",
    "KIWI_AVATAR_MESHOPT_ENABLE",
    "KIWI_AVATAR_MESHOPT_RATIO",
    "KIWI_AVATAR_MESHOPT_ERROR",
    "KIWI_AVATAR_MESHOPT_MIN_TRIANGLES",
    "KIWI_LANDMARKER_READBACK",
    "KIWI_WORKLOAD_BREAKDOWN_PROBE",
    "KIWI_WORKLOAD_AVATAR_RENDER_OFF",
    "KIWI_CADENCE_NODIAG_PROBE",
    "KIWI_CADENCE_NODIAG_DISABLE_OVERLAY",
    "KIWI_FRAME_PACING_MODE",
    "KIWI_EDITOR_ISOLATION_PROBE",
    "KIWI_PREVIEW_PATH_DIAG",
    "KIWI_LIVE_CAMERA_PIXEL_SPACE_AUDIT",
    "KIWI_V44_51_CPU_SHADOW_AUDIT",
    "KIWI_V44_50_GPU_SERVICE_AUDIT",
    "KIWI_V44_45_AUDIT",
    "KIWI_V44_47_STEADY_AUDIT",
    "KIWI_V44_52_1_REAL_CPU_SHADOW_AUDIT",
    "KIWI_V44_55_14_SEMANTIC_AB_AUDIT",
    "KIWI_V44_55_15_EXACT_CPU_INPUT_AUDIT",
    "KIWI_V44_55_16_CANONICAL_AUDIT",
    "KIWI_V44_55_17_CANONICAL_AUDIT",
    "KIWI_V44_55_19_GPU_AUTHORITY_AUDIT",
    "KIWI_V44_55_21_CROP_SAMPLER_AUDIT",
    "KIWI_V44_55_22_REFERENCE_TRANSACTION_AUDIT",
    "KIWI_V44_55_23_PRODUCTION_OUTPUT_AUTHORITY_AUDIT",
    "KIWI_V44_55_26_PRODUCTION_DECODE_PAYLOAD_TRACE",
    "KIWI_ORT_DML_SHADOW",
    "KIWI_ORT_DML_ZERO_COPY_SHADOW",
    "KIWI_VALIDATION_RUN_ID",
    "KIWI_VALIDATION_CASE_ID",
    "KIWI_VALIDATION_TOKEN",
    "KIWI_VALIDATION_MODE",
    "KIWI_VALIDATION_PURPOSE",
    "KIWI_AUTO_RECORD",
    "KIWI_AUTO_QUIT"
)
foreach ($name in $controlledNames) {
    [Environment]::SetEnvironmentVariable($name, $null, "Process")
}

$activeSettings = [ordered]@{
    KIWI_INFERENCE_ASYNC_COMPUTE_PROBE = $(if ($Mode -eq "ASYNC") { "1" } else { "0" })
    KIWI_V44_55_20_COMMON_TENSOR_AUDIT = "1"
    KIWI_V44_55_20_COMMON_TENSOR_SECONDS = "120"
    KIWI_V44_55_20_COMMON_TENSOR_HZ = "1"
    KIWI_V44_55_20_STABLE_SECONDS = "8"
    KIWI_V44_55_24_PAIR_BOUND_SHADOW_OUTPUT_AUDIT = "1"
    KIWI_V44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE = "1"
    KIWI_V44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUDIT = "1"
}
foreach ($entry in $activeSettings.GetEnumerator()) {
    [Environment]::SetEnvironmentVariable([string]$entry.Key, [string]$entry.Value, "Process")
}

$allowedNonEmpty = @{}
foreach ($entry in $activeSettings.GetEnumerator()) {
    $allowedNonEmpty[[string]$entry.Key] = [string]$entry.Value
}
$actualKiwi = @(
    Get-ChildItem Env: |
        Where-Object { $_.Name.StartsWith("KIWI_", [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object Name
)
foreach ($entry in $actualKiwi) {
    Assert-True ($allowedNonEmpty.ContainsKey($entry.Name)) "Unexpected non-empty KIWI environment variable: $($entry.Name)"
    Assert-True ($entry.Value -eq $allowedNonEmpty[$entry.Name]) "Unexpected KIWI value: $($entry.Name)=$($entry.Value)"
}
Assert-True ($actualKiwi.Count -eq $allowedNonEmpty.Count) "Active KIWI environment count mismatch."

$runtimeDirectory = Join-Path $env:USERPROFILE "AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem\KiwiFrameBottleneck"
[void](Assert-KiwiPathWithinRoot -Path $runtimeDirectory -AllowedRoots @((Join-Path $env:USERPROFILE "AppData\LocalLow")) -Label "runtime output directory")
$before = @{}
if (Test-Path -LiteralPath $runtimeDirectory -PathType Container) {
    foreach ($file in @(Get-ChildItem -LiteralPath $runtimeDirectory -File)) {
        $before[$file.FullName] = $true
    }
}

$playerRoot = Join-Path $env:USERPROFILE "AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem"
$playerLog = Join-Path $playerRoot "Player.log"

$identityLines = New-Object 'System.Collections.Generic.List[string]'
$identityLines.Add("contract=KIWI_V44_55_28_ASYNC_COMPUTE_EXECUTION_CONTEXT_ISOLATION")
$identityLines.Add("mode=$Mode")
$identityLines.Add("gitHead=$(git -C $ProjectRoot rev-parse HEAD)")
$identityLines.Add("performanceAuthority=0")
$identityLines.Add("sameBuildRequired=1")
$identityLines.Add("environmentDiffAllowlist=KIWI_INFERENCE_ASYNC_COMPUTE_PROBE")
foreach ($entry in $identityPaths.GetEnumerator()) {
    $identityLines.Add("identity.$($entry.Key).path=$($entry.Value)")
    $identityLines.Add("identity.$($entry.Key).sha256=$(Get-KiwiSha256 -Path ([string]$entry.Value))")
}
foreach ($name in ($controlledNames + @($activeSettings.Keys) | Sort-Object -Unique)) {
    $value = [Environment]::GetEnvironmentVariable([string]$name, "Process")
    if ([string]::IsNullOrEmpty($value)) { $value = "<UNSET>" }
    $identityLines.Add("ENV.$name=$value")
}

$startedUtc = [datetime]::UtcNow
$identityLines.Add("launchStartedUtc=$($startedUtc.ToString('o'))")
$process = Start-Process -FilePath $exe -PassThru
$timer = [Diagnostics.Stopwatch]::StartNew()
$completeSeen = $false
$sessionLog = ""
while (-not $process.WaitForExit(500)) {
    if (Test-Path -LiteralPath $playerLog -PathType Leaf) {
        $currentLog = Read-SharedText -Path $playerLog
        $lastStart = $currentLog.LastIndexOf("Mono path[0]", [StringComparison]::Ordinal)
        if ($lastStart -ge 0) {
            $sessionLog = $currentLog.Substring($lastStart)
            if ($sessionLog.Contains("[Kiwi v44.55.27 ActualVsShadow] COMPLETE")) {
                $completeSeen = $true
            }
        }
    }
    if ($completeSeen) { [void]$process.CloseMainWindow() }
    if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
        [void]$process.CloseMainWindow()
        throw "v44.55.28 $Mode run timed out after $TimeoutSeconds seconds."
    }
}
$process.Refresh()
Assert-True ($process.ExitCode -eq 0) "Player exited with code $($process.ExitCode)."
Assert-True $completeSeen "Player exited before v27 COMPLETE."
$endedUtc = [datetime]::UtcNow
$fullLogText = [IO.File]::ReadAllText($playerLog)
$lastStart = $fullLogText.LastIndexOf("Mono path[0]", [StringComparison]::Ordinal)
Assert-True ($lastStart -ge 0) "Could not isolate current Player.log session."
$sessionLog = $fullLogText.Substring($lastStart)

$expectedOverride = if ($Mode -eq "ASYNC") { "1" } else { "0" }
$expectedEnabled = if ($Mode -eq "ASYNC") { "1" } else { "0" }
$expectedQueue = if ($Mode -eq "ASYNC") { "ASYNC_COMPUTE_DEFAULT" } else { "GRAPHICS_BASELINE" }
$overridePattern = "\[KiwiInferenceV44_22\].*overridePreserved=1 value='" + $expectedOverride + "'.*graphicsApi=Direct3D12.*supportsAsyncCompute=1"
$runtimePattern = "\[KiwiInferenceV38\].*requested=" + $expectedEnabled + ".*enabled=" + $expectedEnabled + ".*queue=" + $expectedQueue + ".*supportsAsyncCompute=1.*graphicsApi=Direct3D12"
Assert-True ([regex]::IsMatch($sessionLog, $overridePattern)) "v44.22 explicit override was not preserved for $Mode."
Assert-True ([regex]::IsMatch($sessionLog, $runtimePattern)) "Production async runtime contract mismatch for $Mode."
Assert-True ([regex]::IsMatch($sessionLog, '\[KiwiInferenceV39\].*requested=0 enabled=0 hz=0.*asyncCompute=[01].*graphicsApi=Direct3D12')) "Cadence budget is not disabled."
Assert-True ($sessionLog.Contains("captureTransport=B:SystemMemoryNV12")) "Camera Path B SystemMemoryNV12 not observed."
Assert-True ($sessionLog.Contains("profile=1920x1080@60")) "Expected camera profile not observed."
Assert-True ($sessionLog.Contains("laneCount=3")) "Expected three-lane configuration not observed."
Assert-True ($sessionLog.Contains("[Kiwi v44.55.27 ActualVsShadow] COMPLETE")) "v27 completion line missing."

[void](Write-KiwiTextFile -Path (Join-Path $EvidenceDirectory "Player.log") -Text $sessionLog -AllowedRoots $allowedRoots -Encoding "Utf8NoBom")

$prefixes = @(
    "KiwiCommonTensorBackendStageIsolation_v44_55_20_",
    "KiwiPairBoundShadowOutputSnapshot_v44_55_24_",
    "KiwiProductionScheduleTransactionTrace_v44_55_25_",
    "KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_"
)
$copied = New-Object 'System.Collections.Generic.List[string]'
foreach ($prefix in $prefixes) {
    foreach ($extension in @(".txt", ".csv")) {
        $source = Get-OnlyFreshArtifact -Directory $runtimeDirectory -Prefix $prefix -Extension $extension -Before $before -StartedUtc $startedUtc
        Assert-True ($sessionLog.Contains($source.Name)) "Fresh artifact is not correlated in current Player.log: $($source.Name)"
        $destination = Join-Path $EvidenceDirectory $source.Name
        $transaction = Copy-KiwiFileTransactional -Source $source.FullName -Destination $destination -AllowedDestinationRoots $allowedRoots -RollbackRoot $rollbackRoot
        Complete-KiwiFileTransaction -Transaction $transaction
        $copied.Add($destination)
    }
}
if (Test-Path -LiteralPath $rollbackRoot -PathType Container) {
    Assert-True (@(Get-ChildItem -LiteralPath $rollbackRoot -Force).Count -eq 0) "Rollback directory is not empty."
}

$cameraMatch = [regex]::Match($sessionLog, "\[WindowsNativeWebCamSource\] ACTIVE camera='([^']+)' profile=([^ ]+).*captureTransport=([^ ]+)")
Assert-True $cameraMatch.Success "Camera identity line parse failed."
$identityLines.Add("launchEndedUtc=$($endedUtc.ToString('o'))")
$identityLines.Add("runtime.exitCode=$($process.ExitCode)")
$identityLines.Add("runtime.completeSeen=1")
$identityLines.Add("runtime.overridePreserved=1")
$identityLines.Add("runtime.requested=$expectedEnabled")
$identityLines.Add("runtime.enabled=$expectedEnabled")
$identityLines.Add("runtime.queue=$expectedQueue")
$identityLines.Add("runtime.graphicsApi=Direct3D12")
$identityLines.Add("runtime.supportsAsyncCompute=1")
$identityLines.Add("runtime.cadenceRequested=0")
$identityLines.Add("runtime.cadenceEnabled=0")
$identityLines.Add("runtime.cadenceHz=0")
$identityLines.Add("runtime.camera=$($cameraMatch.Groups[1].Value)")
$identityLines.Add("runtime.cameraProfile=$($cameraMatch.Groups[2].Value)")
$identityLines.Add("runtime.cameraTransport=$($cameraMatch.Groups[3].Value)")
$identityLines.Add("runtime.laneCount=3")
$identityLines.Add("runtime.evidenceFileCount=$($copied.Count + 1)")
foreach ($path in @($copied | Sort-Object)) {
    $identityLines.Add("artifact.$([IO.Path]::GetFileName($path)).sha256=$(Get-KiwiSha256 -Path $path)")
}
$identityLines.Add("artifact.Player.log.sha256=$(Get-KiwiSha256 -Path (Join-Path $EvidenceDirectory 'Player.log'))")
[void](Write-KiwiTextFile -Path (Join-Path $EvidenceDirectory "env_build_identity.txt") -Text (($identityLines -join "`r`n") + "`r`n") -AllowedRoots $allowedRoots -Encoding "Utf8NoBom")

Write-Host "V44_55_28_RUN_PASS"
Write-Host "MODE=$Mode"
Write-Host "QUEUE=$expectedQueue"
Write-Host "EVIDENCE_DIRECTORY=$EvidenceDirectory"
Write-Host "EXE_SHA256=$(Get-KiwiSha256 -Path $exe)"
Write-Host "MODEL_SHA256=$(Get-KiwiSha256 -Path $model)"
Write-Host "NATIVE_SHA256=$(Get-KiwiSha256 -Path $nativeBuild)"
Write-Host "PLAYER_EXIT_CODE=$($process.ExitCode)"
exit 0
