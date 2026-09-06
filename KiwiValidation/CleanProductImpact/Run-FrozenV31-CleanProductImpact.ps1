[CmdletBinding()]
param(
    [ValidateRange(60, 1800)]
    [int]$TimeoutSeconds = 600
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$ProjectRoot = 'D:\KiwiAvatarSystem'
$EvidenceRoot = Join-Path $ProjectRoot 'KiwiValidation\CleanProductImpact'
$FrozenRuntimeZip = Join-Path $ProjectRoot 'KiwiValidation\RuntimeEvidence_v44_55_31_20260905_165629_f05a2230.zip'
$BuildIdentityPath = Join-Path $ProjectRoot 'KiwiValidation\v44_55_31_build_identity.json'
$BuildRoot = Join-Path $ProjectRoot 'Builds\v44_55_31AsyncProducerTailSnapshot'
$FrozenExe = Join-Path $BuildRoot 'KiwiAvatarSystem_v44_55_31.exe'

$ExpectedRuntimeZipSha256 = 'FCA6097A4B01E725F8ED2203B330A259C2BBCE5549ABBC001B139ED9FEAD9B9E'
$ExpectedBuildIdentitySha256 = 'DAA8C22A890DEEB128758D587359D4EDBF859631E3A8306D63F77D746949D7A8'
$ExpectedExeSha256 = '98751D0DFF0DD3ADE563C2B505A0E9F7A46E8E8895864212E1B884EDBCB42E81'
$ExpectedBuildContract = 'KIWI_V44_55_31_BUILD'
$ExpectedPayloadCount = 288

$HeavyDiagnosticVariables = @(
    'KIWI_V44_55_20_COMMON_TENSOR_AUDIT',
    'KIWI_V44_55_24_PAIR_BOUND_SHADOW_OUTPUT_AUDIT',
    'KIWI_V44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE',
    'KIWI_V44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUDIT',
    'KIWI_V44_55_31_COLLECTION'
)

$AdditionalSafetyVariables = @(
    'KIWI_V44_55_20_COMMON_TENSOR_SECONDS',
    'KIWI_V44_55_20_COMMON_TENSOR_HZ',
    'KIWI_V44_55_20_STABLE_SECONDS',
    'KIWI_V44_55_31_PRODUCER_TAIL_SNAPSHOT',
    'KIWI_V44_55_31_EVIDENCE_DIR',
    'KIWI_V44_55_29_AUTO_QUIT',
    'KIWI_V44_55_29_AUTO_QUIT_TIMEOUT_SEC'
)

$HeavyDiagnosticMarkers = [ordered]@{
    V20 = '[Kiwi v44.55.20 CommonTensor]'
    V24 = '[Kiwi v44.55.24 PairBoundOutput]'
    V25 = '[Kiwi v44.55.25 ScheduleTrace]'
    V27 = '[Kiwi v44.55.27 ActualVsShadow]'
    V31 = '[KiwiV31]'
    V29AutoQuit = '[KiwiV44_55_29AutoQuit]'
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
}

function Write-Utf8NoBomText {
    param(
        [Parameter(Mandatory = $true)][string]$LiteralPath,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text
    )

    [System.IO.File]::WriteAllText(
        $LiteralPath,
        $Text,
        (New-Object System.Text.UTF8Encoding($false)))
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$LiteralPath,
        [Parameter(Mandatory = $true)][AllowNull()][AllowEmptyCollection()]$Value,
        [int]$Depth = 8
    )

    $json = ConvertTo-Json -InputObject $Value -Depth $Depth
    Write-Utf8NoBomText -LiteralPath $LiteralPath -Text ($json + "`r`n")
}

function Assert-FileSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$LiteralPath,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "$Label is missing: $LiteralPath"
    }

    $actual = Get-FileSha256 -LiteralPath $LiteralPath
    if ($actual -ne $ExpectedSha256) {
        throw "$Label SHA256 mismatch. expected=$ExpectedSha256 actual=$actual path=$LiteralPath"
    }

    return $actual
}

function Get-KiwiProcessEnvironmentSnapshot {
    $items = New-Object 'System.Collections.Generic.List[object]'
    $environment = [Environment]::GetEnvironmentVariables('Process')
    $names = @($environment.Keys | ForEach-Object { [string]$_ } | Where-Object {
        $_.StartsWith('KIWI_', [StringComparison]::OrdinalIgnoreCase)
    } | Sort-Object)

    foreach ($name in $names) {
        $items.Add([pscustomobject][ordered]@{
            name = $name
            value = [Environment]::GetEnvironmentVariable($name, 'Process')
        })
    }

    return $items.ToArray()
}

function Restore-KiwiProcessEnvironment {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Snapshot)

    $current = Get-KiwiProcessEnvironmentSnapshot
    foreach ($entry in @($current)) {
        [Environment]::SetEnvironmentVariable($entry.name, $null, 'Process')
    }

    foreach ($entry in @($Snapshot)) {
        [Environment]::SetEnvironmentVariable(
            [string]$entry.name,
            [string]$entry.value,
            'Process')
    }
}

function Test-EnvironmentSnapshotsEqual {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Left,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Right
    )

    if (@($Left).Count -ne @($Right).Count) {
        return $false
    }

    for ($i = 0; $i -lt @($Left).Count; $i++) {
        if ([string]$Left[$i].name -ne [string]$Right[$i].name -or
            [string]$Left[$i].value -ne [string]$Right[$i].value) {
            return $false
        }
    }

    return $true
}

function Get-LiteralMatchCount {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Text
    )

    return @($Lines | Where-Object {
        $_.IndexOf($Text, [StringComparison]::Ordinal) -ge 0
    }).Count
}

function Get-UnityPlayerErrorLines {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines)

    $pattern = '(?i)(^Crash!!!|^\s*FATAL\b|^\s*ERROR\b|\bNullReferenceException\b|\bArgumentOutOfRangeException\b|\bIndexOutOfRangeException\b|\bInvalidOperationException:|\bDllNotFoundException\b|\bEntryPointNotFoundException\b|\bMissingMethodException\b|\bAccessViolationException\b|\bOutOfMemoryException\b|\bStackOverflowException\b|\[Error\])'
    return @($Lines | Where-Object { $_ -match $pattern })
}

function Get-MaxSpoutPublishCount {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines)

    [long]$maximum = 0
    foreach ($line in $Lines) {
        if ($line -match '^\[KiwiSpoutV44_20\].*\bpublish=([0-9]+)') {
            [long]$value = [long]$matches[1]
            if ($value -gt $maximum) {
                $maximum = $value
            }
        }
    }

    return $maximum
}

function Write-Sha256Manifest {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $manifestPath = Join-Path $Directory 'SHA256_MANIFEST.json'
    $entries = New-Object 'System.Collections.Generic.List[object]'
    $files = @(Get-ChildItem -LiteralPath $Directory -File | Where-Object {
        $_.FullName -ne $manifestPath -and $_.Extension -ne '.zip'
    } | Sort-Object Name)

    foreach ($file in $files) {
        $entries.Add([pscustomobject][ordered]@{
            path = $file.Name
            bytes = $file.Length
            sha256 = Get-FileSha256 -LiteralPath $file.FullName
        })
    }

    Write-JsonFile -LiteralPath $manifestPath -Value $entries.ToArray() -Depth 5
    return $manifestPath
}

if ($PSVersionTable.PSEdition -ne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5 -or
    $PSVersionTable.PSVersion.Minor -ne 1) {
    throw 'This runner requires Windows PowerShell 5.1 (Desktop edition).'
}

$resolvedProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)
$resolvedEvidenceRoot = [System.IO.Path]::GetFullPath($EvidenceRoot)
if (-not $resolvedEvidenceRoot.StartsWith(
    $resolvedProjectRoot + [System.IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Evidence root containment check failed.'
}

$runtimeZipSha = Assert-FileSha256 -LiteralPath $FrozenRuntimeZip -ExpectedSha256 $ExpectedRuntimeZipSha256 -Label 'Frozen v31 Runtime ZIP'

$buildIdentitySha = Assert-FileSha256 -LiteralPath $BuildIdentityPath -ExpectedSha256 $ExpectedBuildIdentitySha256 -Label 'Frozen v31 build identity'

$exeSha = Assert-FileSha256 -LiteralPath $FrozenExe -ExpectedSha256 $ExpectedExeSha256 -Label 'Frozen v31 executable'

$buildIdentity = Get-Content -LiteralPath $BuildIdentityPath -Raw | ConvertFrom-Json
if ($buildIdentity.contract -ne $ExpectedBuildContract -or
    [int]$buildIdentity.exitCode -ne 0) {
    throw 'Frozen v31 build identity contract/exit gate failed.'
}

$payload = @($buildIdentity.payload)
if ($payload.Count -ne $ExpectedPayloadCount) {
    throw "Frozen v31 payload cardinality mismatch. expected=$ExpectedPayloadCount actual=$($payload.Count)"
}

$payloadMissing = New-Object 'System.Collections.Generic.List[string]'
$payloadMismatch = New-Object 'System.Collections.Generic.List[string]'
[int]$payloadMatched = 0
foreach ($entry in $payload) {
    $payloadPath = Join-Path $ProjectRoot ([string]$entry.path)
    if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
        $payloadMissing.Add([string]$entry.path)
        continue
    }

    $payloadSha = Get-FileSha256 -LiteralPath $payloadPath
    if ($payloadSha -ne [string]$entry.sha256) {
        $payloadMismatch.Add(
            ([string]$entry.path) +
            '|expected=' + ([string]$entry.sha256) +
            '|actual=' + $payloadSha)
    }
    else {
        $payloadMatched++
    }
}

$expectedBuildPrefix = 'Builds\v44_55_31AsyncProducerTailSnapshot\'
$expectedBuildRelativePaths = @($payload | Where-Object {
    ([string]$_.path).StartsWith(
        $expectedBuildPrefix,
        [StringComparison]::OrdinalIgnoreCase)
} | ForEach-Object {
    ([string]$_.path).Substring($expectedBuildPrefix.Length)
})

$actualBuildFiles = @(Get-ChildItem -LiteralPath $BuildRoot -File -Recurse)
$buildExtras = New-Object 'System.Collections.Generic.List[string]'
foreach ($file in $actualBuildFiles) {
    $relative = $file.FullName.Substring($BuildRoot.Length + 1)
    if ($expectedBuildRelativePaths -notcontains $relative) {
        $buildExtras.Add($relative)
    }
}

if ($payloadMissing.Count -ne 0 -or
    $payloadMismatch.Count -ne 0 -or
    $buildExtras.Count -ne 0 -or
    $actualBuildFiles.Count -ne $ExpectedPayloadCount) {
    throw (
        'Frozen v31 payload closure failed. matched=' + $payloadMatched +
        ' missing=' + $payloadMissing.Count +
        ' mismatch=' + $payloadMismatch.Count +
        ' extras=' + $buildExtras.Count +
        ' actualFiles=' + $actualBuildFiles.Count)
}

$runStamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$runToken = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $EvidenceRoot ("Run_${runStamp}_${runToken}")
$resolvedRunDirectory = [System.IO.Path]::GetFullPath($runDirectory)
if (-not $resolvedRunDirectory.StartsWith(
    $resolvedEvidenceRoot + [System.IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Run directory containment check failed.'
}

if (Test-Path -LiteralPath $runDirectory) {
    throw "Evidence directory already exists: $runDirectory"
}
[System.IO.Directory]::CreateDirectory($runDirectory) | Out-Null

$playerLogPath = Join-Path $runDirectory 'Player.log'
$environmentBeforePath = Join-Path $runDirectory 'environment_before.json'
$environmentAppliedPath = Join-Path $runDirectory 'environment_applied.json'
$environmentRestorationPath = Join-Path $runDirectory 'environment_restoration.json'
$runIdentityPath = Join-Path $runDirectory 'run_identity.json'
$automatedResultPath = Join-Path $runDirectory 'automated_result.json'
$errorLinesPath = Join-Path $runDirectory 'player_error_lines.txt'
$visualChecklistPath = Join-Path $runDirectory 'visual_checklist.txt'

$preflight = [pscustomobject][ordered]@{
    contract = 'KIWI_FROZEN_V31_CLEAN_PRODUCT_IMPACT_PREFLIGHT_V1'
    createdUtc = [DateTime]::UtcNow.ToString('o')
    windowsPowerShell = $PSVersionTable.PSVersion.ToString()
    projectRoot = $ProjectRoot
    frozenRuntimeZip = $FrozenRuntimeZip
    frozenRuntimeZipSha256 = $runtimeZipSha
    buildIdentity = $BuildIdentityPath
    buildIdentitySha256 = $buildIdentitySha
    buildContract = [string]$buildIdentity.contract
    buildUtc = [string]$buildIdentity.builtUtc
    frozenExe = $FrozenExe
    frozenExeSha256 = $exeSha
    payloadExpected = $ExpectedPayloadCount
    payloadMatched = $payloadMatched
    payloadMissing = $payloadMissing.Count
    payloadMismatch = $payloadMismatch.Count
    payloadExtra = $buildExtras.Count
    runtimeZipBuildCorrelated = $true
    currentWorkspaceBuildPerformed = $false
}
Write-JsonFile -LiteralPath (Join-Path $runDirectory 'preflight.json') -Value $preflight -Depth 6

$visualChecklist = @'
VISUAL_AUTHORITY=PENDING_HUMAN_REVIEW
Instructions: Observe the visible frozen-v31 application, exercise each item, then close the application normally.

HEAD_TRANSLATION=
YAW=
PITCH=
ROLL=
EYE=
BLINK=
MOUTH_OPEN=
MOUTH_SHAPE_EXPRESSION=
FRONTAL_STILLNESS=
FAST_MOVEMENT_RESPONSE=
OBVIOUS_LAG=
STALE_OR_FREEZE=
FACEPART_MISMATCH=
TRANSITION_ARTIFACT=
LIVE_CAMERA_REGRESSION=
SPOUT_OUTPUT=
OTHER=
'@
Write-Utf8NoBomText -LiteralPath $visualChecklistPath -Text $visualChecklist

$environmentBefore = @(Get-KiwiProcessEnvironmentSnapshot)
Write-JsonFile -LiteralPath $environmentBeforePath -Value $environmentBefore -Depth 5

$process = $null
$processExit = $null
$timedOut = $false
$startedUtc = $null
$endedUtc = $null
$environmentRestored = $false

try {
    foreach ($entry in @($environmentBefore)) {
        [Environment]::SetEnvironmentVariable(
            [string]$entry.name,
            $null,
            'Process')
    }

    foreach ($name in $HeavyDiagnosticVariables) {
        [Environment]::SetEnvironmentVariable($name, '0', 'Process')
    }

    foreach ($name in $AdditionalSafetyVariables) {
        if ($name -eq 'KIWI_V44_55_20_COMMON_TENSOR_SECONDS' -or
            $name -eq 'KIWI_V44_55_20_COMMON_TENSOR_HZ' -or
            $name -eq 'KIWI_V44_55_20_STABLE_SECONDS' -or
            $name -eq 'KIWI_V44_55_31_EVIDENCE_DIR' -or
            $name -eq 'KIWI_V44_55_29_AUTO_QUIT_TIMEOUT_SEC') {
            [Environment]::SetEnvironmentVariable($name, $null, 'Process')
        }
        else {
            [Environment]::SetEnvironmentVariable($name, '0', 'Process')
        }
    }

    # These are execution-form overrides, not collection switches. Leave them
    # unset so the frozen build applies its own Windows/DX12 Product defaults.
    [Environment]::SetEnvironmentVariable(
        'KIWI_INFERENCE_ASYNC_COMPUTE_PROBE',
        $null,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'KIWI_INFERENCE_COMMAND_BUFFER_GRAPHICS_PROBE',
        $null,
        'Process')

    $environmentApplied = @(Get-KiwiProcessEnvironmentSnapshot)
    Write-JsonFile -LiteralPath $environmentAppliedPath -Value $environmentApplied -Depth 5

    $heavyState = [ordered]@{}
    foreach ($name in $HeavyDiagnosticVariables) {
        $heavyState[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }

    $appliedRecord = [pscustomobject][ordered]@{
        contract = 'KIWI_FROZEN_V31_CLEAN_PRODUCT_IMPACT_ENVIRONMENT_V1'
        inheritedKiwiVariableCount = $environmentBefore.Count
        inheritedKiwiVariablesRecorded = $true
        sourceDefaultsRequested = $true
        inferenceAsyncComputeOverride = 'UNSET_FOR_FROZEN_BUILD_PRODUCT_DEFAULT'
        commandBufferGraphicsOverride = 'UNSET_FOR_FROZEN_BUILD_PRODUCT_DEFAULT'
        heavyDiagnosticVariables = $heavyState
        v20Collection = 'OFF_EXPLICIT_0'
        v24Collection = 'OFF_EXPLICIT_0'
        v25Collection = 'OFF_EXPLICIT_0'
        v27Collection = 'OFF_EXPLICIT_0'
        v31Collection = 'OFF_EXPLICIT_0'
        heavyDiagnosticsOffBeforeLaunch = $true
    }
    Write-JsonFile -LiteralPath (Join-Path $runDirectory 'clean_configuration.json') -Value $appliedRecord -Depth 7

    Write-Host ''
    Write-Host 'Frozen v31 clean Product Impact run is starting.'
    Write-Host "Evidence output: $runDirectory"
    Write-Host 'Observe the app using visual_checklist.txt, then close the app normally.'
    Write-Host "Safety timeout: $TimeoutSeconds seconds"
    Write-Host ''

    $startedUtc = [DateTime]::UtcNow
    $processArguments = @{
        FilePath = $FrozenExe
        ArgumentList = @('-logFile', $playerLogPath)
        WorkingDirectory = $BuildRoot
        PassThru = $true
    }
    $process = Start-Process @processArguments

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
    }

    if (-not $process.HasExited) {
        $timedOut = $true
        try {
            [void]$process.CloseMainWindow()
        }
        catch {
            # The timeout result remains authoritative even if graceful close fails.
        }

        if (-not $process.WaitForExit(5000)) {
            $process.Kill()
            $process.WaitForExit()
        }
    }
    else {
        $process.WaitForExit()
    }

    $processExit = $process.ExitCode
    $endedUtc = [DateTime]::UtcNow
}
finally {
    Restore-KiwiProcessEnvironment -Snapshot $environmentBefore
    $environmentAfterRestore = @(Get-KiwiProcessEnvironmentSnapshot)
    $environmentRestored = Test-EnvironmentSnapshotsEqual -Left $environmentBefore -Right $environmentAfterRestore

    $restoration = [pscustomobject][ordered]@{
        contract = 'KIWI_FROZEN_V31_CLEAN_PRODUCT_IMPACT_ENV_RESTORE_V1'
        restoredUtc = [DateTime]::UtcNow.ToString('o')
        restored = $environmentRestored
        beforeCount = $environmentBefore.Count
        afterCount = $environmentAfterRestore.Count
        after = $environmentAfterRestore
    }
    Write-JsonFile -LiteralPath $environmentRestorationPath -Value $restoration -Depth 6
}

if ($null -eq $startedUtc -or $null -eq $endedUtc -or $null -eq $processExit) {
    throw 'Process lifecycle evidence is incomplete.'
}

$runIdentity = [pscustomobject][ordered]@{
    contract = 'KIWI_FROZEN_V31_CLEAN_PRODUCT_IMPACT_RUN_V1'
    startedUtc = $startedUtc.ToString('o')
    endedUtc = $endedUtc.ToString('o')
    durationSeconds = [Math]::Round(($endedUtc - $startedUtc).TotalSeconds, 3)
    pid = $process.Id
    processExit = $processExit
    timedOut = $timedOut
    frozenExe = $FrozenExe
    frozenExeSha256 = $exeSha
    buildIdentitySha256 = $buildIdentitySha
    frozenRuntimeZipSha256 = $runtimeZipSha
    runtimeZipBuildCorrelated = $true
    heavyDiagnosticsRequestedOff = $true
    environmentRestored = $environmentRestored
    playerLog = $playerLogPath
    humanReviewRequired = $true
}
Write-JsonFile -LiteralPath $runIdentityPath -Value $runIdentity -Depth 6

$playerLines = @()
if (Test-Path -LiteralPath $playerLogPath -PathType Leaf) {
    $playerLines = @(Get-Content -LiteralPath $playerLogPath)
}

$heavyMarkerCounts = [ordered]@{}
foreach ($entry in $HeavyDiagnosticMarkers.GetEnumerator()) {
    $heavyMarkerCounts[$entry.Key] = Get-LiteralMatchCount -Lines $playerLines -Text ([string]$entry.Value)
}

$heavyMarkerTotal = 0
foreach ($count in $heavyMarkerCounts.Values) {
    $heavyMarkerTotal += [int]$count
}
$heavyDiagnosticsOffObserved = $heavyMarkerTotal -eq 0

$errorLines = @(Get-UnityPlayerErrorLines -Lines $playerLines)
$errorText = ''
if ($errorLines.Count -gt 0) {
    $errorText = ($errorLines -join "`r`n") + "`r`n"
}
Write-Utf8NoBomText -LiteralPath $errorLinesPath -Text $errorText

$cameraActiveCount = Get-LiteralMatchCount -Lines $playerLines -Text '[WindowsNativeWebCamSource] ACTIVE'
$inferenceInitializedCount = Get-LiteralMatchCount -Lines $playerLines -Text '[KiwiInference] Hybrid GPU single-readback landmark path initialized on Direct3D12.'
$spoutPublishMaximum = Get-MaxSpoutPublishCount -Lines $playerLines

$cameraLiveness = 'NOT_OBSERVED'
if ($cameraActiveCount -gt 0) {
    $cameraLiveness = 'STARTUP_ACTIVE_OBSERVED_CONTINUOUS_LIVENESS_NOT_OBSERVED'
}

$trackingLiveness = 'NOT_OBSERVED'
if ($inferenceInitializedCount -gt 0) {
    $trackingLiveness = 'INITIALIZATION_ONLY_CONTINUOUS_LIVENESS_NOT_OBSERVED'
}

$spoutLiveness = 'NOT_OBSERVED'
if ($spoutPublishMaximum -gt 0) {
    $spoutLiveness = 'PUBLISH_PROGRESS_OBSERVED'
}

$cleanRuntimeStatus = 'INCONCLUSIVE'
if ($timedOut) {
    $cleanRuntimeStatus = 'TIMEOUT'
}
elseif ($processExit -ne 0) {
    $cleanRuntimeStatus = 'PROCESS_EXIT_NONZERO'
}
elseif ($errorLines.Count -gt 0) {
    $cleanRuntimeStatus = 'COMPLETED_WITH_PLAYER_ERRORS'
}
elseif (-not $heavyDiagnosticsOffObserved) {
    $cleanRuntimeStatus = 'HEAVY_DIAGNOSTIC_CONTAMINATION'
}
else {
    $cleanRuntimeStatus = 'AUTOMATED_RUN_COMPLETE_HUMAN_VISUAL_PENDING'
}

$automatedResult = [pscustomobject][ordered]@{
    contract = 'KIWI_FROZEN_V31_CLEAN_PRODUCT_IMPACT_AUTOMATED_RESULT_V1'
    processExit = $processExit
    timeout = $timedOut
    playerLogPresent = (Test-Path -LiteralPath $playerLogPath -PathType Leaf)
    playerLogBytes = $(
        if (Test-Path -LiteralPath $playerLogPath -PathType Leaf) {
            (Get-Item -LiteralPath $playerLogPath).Length
        }
        else {
            0
        })
    playerErrorCount = $errorLines.Count
    playerErrorDefinition = 'Fail-closed explicit fatal/error/exception line pattern; see player_error_lines.txt'
    cameraLiveness = $cameraLiveness
    trackingLiveness = $trackingLiveness
    canonicalLiveness = 'NOT_OBSERVED'
    avatarPresentationLiveness = 'NOT_OBSERVED'
    spoutLiveness = $spoutLiveness
    spoutMaximumPublishCount = $spoutPublishMaximum
    availableIdentityContinuity = 'NOT_OBSERVED'
    productIdentityDepth = 'LIMITED'
    heavyDiagnosticMarkerCounts = $heavyMarkerCounts
    heavyDiagnosticsOff = $heavyDiagnosticsOffObserved
    cleanRuntimeStatus = $cleanRuntimeStatus
    humanReviewRequired = $true
    visualAuthority = 'PENDING_HUMAN_REVIEW'
    performanceAuthority = 'UNAVAILABLE_NOT_A_DEDICATED_BENCHMARK'
    productImpact = 'INCONCLUSIVE_PENDING_HUMAN_REVIEW'
}
Write-JsonFile -LiteralPath $automatedResultPath -Value $automatedResult -Depth 8

$manifestPath = Write-Sha256Manifest -Directory $runDirectory
$evidenceZipPath = $runDirectory + '.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $runDirectory,
    $evidenceZipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true)
$evidenceZipSha = Get-FileSha256 -LiteralPath $evidenceZipPath

Write-Host ''
Write-Host "CLEAN_RUNTIME_STATUS=$cleanRuntimeStatus"
Write-Host "PROCESS_EXIT=$processExit"
Write-Host "TIMEOUT=$timedOut"
Write-Host "PLAYER_ERROR_COUNT=$($errorLines.Count)"
Write-Host "HEAVY_DIAGNOSTICS_OFF=$heavyDiagnosticsOffObserved"
Write-Host "CAMERA_LIVENESS=$cameraLiveness"
Write-Host "TRACKING_LIVENESS=$trackingLiveness"
Write-Host 'CANONICAL_LIVENESS=NOT_OBSERVED'
Write-Host 'AVATAR_PRESENTATION_LIVENESS=NOT_OBSERVED'
Write-Host "SPOUT_LIVENESS=$spoutLiveness"
Write-Host 'AVAILABLE_IDENTITY_CONTINUITY=NOT_OBSERVED'
Write-Host 'VISUAL_AUTHORITY=PENDING_HUMAN_REVIEW'
Write-Host 'PERFORMANCE_AUTHORITY=UNAVAILABLE_NOT_A_DEDICATED_BENCHMARK'
Write-Host "EVIDENCE_OUTPUT=$runDirectory"
Write-Host "EVIDENCE_ZIP=$evidenceZipPath"
Write-Host "EVIDENCE_ZIP_SHA256=$evidenceZipSha"
Write-Host "VISUAL_CHECKLIST=$visualChecklistPath"
Write-Host ''

if ($timedOut) {
    exit 20
}
if ($processExit -ne 0) {
    exit 21
}
if ($errorLines.Count -ne 0) {
    exit 22
}
if (-not $heavyDiagnosticsOffObserved) {
    exit 23
}
if (-not $environmentRestored) {
    exit 24
}

exit 0
