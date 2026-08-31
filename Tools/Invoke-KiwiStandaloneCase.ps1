[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('BASELINE_METRICS', 'ZEROCOPY_METRICS', 'BASELINE_VISUAL', 'ZEROCOPY_VISUAL')]
    [string]$CaseId,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$')]
    [string]$RunId,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$')]
    [string]$Token,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$Executable,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$RunDirectory,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ThresholdsPath,

    [string]$FfmpegPath = 'D:\KiwiAvatarSystem\Tools\ffmpeg\bin\ffmpeg.exe',
    [string]$NvidiaSmiPath = 'C:\Windows\System32\nvidia-smi.exe',
    [string]$GpuUuid = 'GPU-6efc5d27-d766-eeac-cb81-8aa14cebbfa3'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-UtcNow {
    [DateTime]::UtcNow.ToString('O')
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-Process([string]$FilePath, [string[]]$Arguments, [hashtable]$Environment) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $false
    foreach ($argument in $Arguments) {
        [void]$info.ArgumentList.Add($argument)
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $info.Environment[$entry.Key] = [string]$entry.Value
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) {
        throw "Process start returned false: $FilePath"
    }
    $process
}

function Copy-VerifiedArtifact([string]$Source, [string]$Destination, [string]$Type, [bool]$Required) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        if ($Required) {
            throw "Required artifact is missing: type=$Type source=$Source"
        }
        return $null
    }

    $destinationDirectory = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    $sourceHash = Get-Sha256 $Source
    $destinationHash = Get-Sha256 $Destination
    if ($sourceHash -ne $destinationHash) {
        throw "Artifact copy hash mismatch: type=$Type source=$Source destination=$Destination"
    }

    $item = Get-Item -LiteralPath $Destination
    [ordered]@{
        type = $Type
        sourcePath = [System.IO.Path]::GetFullPath($Source)
        path = $item.FullName
        sha256 = $destinationHash
        bytes = [long]$item.Length
        createdUtc = $item.CreationTimeUtc.ToString('O')
        tokenMatched = ($Destination -like "*$Token*")
        copyHashMatched = $true
    }
}

function Stop-HelperProcess([System.Diagnostics.Process]$Process) {
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) {
            $Process.Kill($true)
            [void]$Process.WaitForExit(5000)
        }
    }
    catch {
        Write-Warning "Could not stop helper PID $($Process.Id): $($_.Exception.Message)"
    }
}

$thresholds = Get-Content -LiteralPath $ThresholdsPath -Raw | ConvertFrom-Json -Depth 100
$mode = if ($CaseId.StartsWith('ZEROCOPY_', [StringComparison]::Ordinal)) { 'ZEROCOPY' } else { 'BASELINE' }
$purpose = if ($CaseId.EndsWith('_VISUAL', [StringComparison]::Ordinal)) { 'VISUAL' } else { 'METRICS' }
$caseDirectory = Join-Path $RunDirectory $CaseId
$logsDirectory = Join-Path $caseDirectory 'Logs'
$gpuDirectory = Join-Path $caseDirectory 'GPU'
$telemetryDirectory = Join-Path $caseDirectory 'Telemetry'
$receiptsDirectory = Join-Path $caseDirectory 'Receipts'
$videoDirectory = Join-Path $caseDirectory 'Video'
$crashDirectory = Join-Path $caseDirectory 'Crash'
foreach ($directory in @($logsDirectory, $gpuDirectory, $telemetryDirectory, $receiptsDirectory, $videoDirectory, $crashDirectory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$playerLog = Join-Path $logsDirectory "KiwiStandalonePlayer_${Token}.log"
$gpuCsv = Join-Path $gpuDirectory "KiwiGpuTelemetry_${Token}.csv"
$gpuMeta = Join-Path $gpuDirectory "KiwiGpuTelemetry_${Token}.meta.json"
$videoPath = Join-Path $videoDirectory "KiwiValidation_${Token}.mp4"
$caseResultPath = Join-Path $caseDirectory "case-result_${Token}.json"
$persistentRoot = Join-Path $env:USERPROFILE 'AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem'
$receiptSource = Join-Path $persistentRoot "KiwiValidationHarness\$Token\receipt.json"

if (Test-Path -LiteralPath $receiptSource) {
    throw "Refusing to reuse an existing validation token receipt: $receiptSource"
}

$recordSeconds = [double]$thresholds.execution.recordDurationSeconds
$startupTimeout = [double]$thresholds.execution.startupTimeoutSeconds
$warmupSeconds = [double]$thresholds.execution.warmupDelaySeconds
$quitTimeout = [double]$thresholds.execution.gracefulQuitTimeoutSeconds
$gpuIntervalMs = [int]$thresholds.execution.gpuSampleIntervalMs
$startedUtc = Get-UtcNow
$caseStopwatch = [Diagnostics.Stopwatch]::StartNew()
$player = $null
$gpu = $null
$ffmpeg = $null
$forcedTermination = $false
$captureStarted = $false
$gpuStartedUtc = $null
$captureStartedUtc = $null
$startupSurvived = $false
$readyMarkerSeen = $false
$recordingMarkerSeen = $false
$completionMarkerSeen = $false
$receiptCommitted = $false
$fatalError = $null

try {
    $playerArguments = @(
        '-force-d3d12',
        '-screen-fullscreen', '0',
        '-screen-width', '1280',
        '-screen-height', '720',
        '-logFile', $playerLog,
        '-crash-report-folder', $crashDirectory
    )
    $playerEnvironment = @{
        KIWI_VALIDATION_RUN_ID = $RunId
        KIWI_VALIDATION_CASE_ID = $CaseId
        KIWI_VALIDATION_TOKEN = $Token
        KIWI_VALIDATION_MODE = $mode
        KIWI_VALIDATION_PURPOSE = $purpose
        KIWI_VALIDATION_STARTUP_TIMEOUT_SEC = [string]$startupTimeout
        KIWI_AUTO_RECORD = '1'
        KIWI_AUTO_RECORD_DELAY_SEC = [string]$warmupSeconds
        KIWI_AUTO_RECORD_DURATION_SEC = [string]$recordSeconds
        KIWI_AUTO_QUIT = '1'
        KIWI_ORT_DML_ZERO_COPY_SHADOW = if ($mode -eq 'ZEROCOPY') { '1' } else { '0' }
        KIWI_ORT_DML_SHADOW = '0'
    }
    $player = New-Process -FilePath $Executable -Arguments $playerArguments -Environment $playerEnvironment

    $overallDeadline = [DateTime]::UtcNow.AddSeconds($startupTimeout + $warmupSeconds + $recordSeconds + $quitTimeout + 15)
    while (-not $player.HasExited -and [DateTime]::UtcNow -lt $overallDeadline) {
        if ($caseStopwatch.Elapsed.TotalSeconds -ge 2) {
            $startupSurvived = $true
        }

        if (Test-Path -LiteralPath $playerLog -PathType Leaf) {
            $logText = Get-Content -LiteralPath $playerLog -Raw
            $readyMarkerSeen = $logText.Contains("[KiwiValidation] STARTUP_READY runId=$RunId caseId=$CaseId token=$Token")
            $recordingMarkerSeen = $logText.Contains("[KiwiValidation] RECORDING_STARTED token=$Token")
            $completionMarkerSeen = $logText.Contains('[KiwiValidation] EXIT_REQUESTED code=0')
        }

        if ($recordingMarkerSeen -and -not $captureStarted) {
            $gpuArguments = @(
                "--query-gpu=timestamp,uuid,name,driver_version,utilization.gpu,memory.used,memory.total,power.draw,temperature.gpu",
                '--format=csv,nounits',
                '--loop-ms', [string]$gpuIntervalMs,
                '-i', $GpuUuid,
                '-f', $gpuCsv
            )
            $gpu = New-Process -FilePath $NvidiaSmiPath -Arguments $gpuArguments -Environment @{}
            $gpuStartedUtc = Get-UtcNow

            if ($purpose -eq 'VISUAL') {
                $player.Refresh()
                $windowHandle = $player.MainWindowHandle
                if ($windowHandle -eq [IntPtr]::Zero) {
                    throw 'Player MainWindowHandle is zero at visual capture start.'
                }
                $hwnd = ('0x{0:x}' -f $windowHandle.ToInt64())
                $ffmpegArguments = @(
                    '-hide_banner', '-loglevel', 'error', '-y',
                    '-f', 'gdigrab', '-framerate', [string]$thresholds.video.captureFps,
                    '-draw_mouse', '0', '-i', "hwnd=$hwnd",
                    '-t', ([string]($recordSeconds - 0.25)),
                    '-c:v', [string]$thresholds.video.encoder,
                    '-preset', [string]$thresholds.video.preset,
                    '-pix_fmt', [string]$thresholds.video.pixelFormat,
                    $videoPath
                )
                $ffmpeg = New-Process -FilePath $FfmpegPath -Arguments $ffmpegArguments -Environment @{}
                $captureStartedUtc = Get-UtcNow
            }
            $captureStarted = $true
        }

        Start-Sleep -Milliseconds 100
        $player.Refresh()
    }

    if (-not $player.HasExited) {
        $forcedTermination = $true
        $player.Kill($true)
        [void]$player.WaitForExit(5000)
        throw 'Player exceeded the case deadline and was terminated.'
    }

    $player.Refresh()
    $exitCode = $player.ExitCode
    if (Test-Path -LiteralPath $playerLog -PathType Leaf) {
        $finalLogText = Get-Content -LiteralPath $playerLog -Raw
        $readyMarkerSeen = $finalLogText.Contains("[KiwiValidation] STARTUP_READY runId=$RunId caseId=$CaseId token=$Token")
        $recordingMarkerSeen = $finalLogText.Contains("[KiwiValidation] RECORDING_STARTED token=$Token")
        $completionMarkerSeen = $finalLogText.Contains('[KiwiValidation] EXIT_REQUESTED code=0')
    }
    if ($exitCode -ne 0) {
        throw "Player exit code was $exitCode."
    }
    if (-not $readyMarkerSeen -or -not $recordingMarkerSeen -or -not $completionMarkerSeen) {
        throw 'Player did not emit the exact ready, recording, and completion markers.'
    }
    if (-not (Test-Path -LiteralPath $receiptSource -PathType Leaf)) {
        throw "Exact case receipt was not committed: $receiptSource"
    }
    $runtimeReceipt = Get-Content -LiteralPath $receiptSource -Raw | ConvertFrom-Json -Depth 100
    if ($runtimeReceipt.token -ne $Token -or $runtimeReceipt.caseId -ne $CaseId -or $runtimeReceipt.status -ne 'OK') {
        throw 'Runtime receipt identity/status mismatch.'
    }
    $receiptCommitted = $true
}
catch {
    $fatalError = $_.Exception.Message
}
finally {
    Stop-HelperProcess $ffmpeg
    Stop-HelperProcess $gpu
    if ($null -ne $player -and -not $player.HasExited) {
        $forcedTermination = $true
        Stop-HelperProcess $player
    }
}

$completedUtc = Get-UtcNow
if (Test-Path -LiteralPath $receiptSource -PathType Leaf) {
    try {
        $observedReceipt = Get-Content -LiteralPath $receiptSource -Raw | ConvertFrom-Json -Depth 100
        if ($observedReceipt.token -eq $Token -and $observedReceipt.caseId -eq $CaseId) {
            $receiptCommitted = $true
        }
    }
    catch {
        if ($null -eq $fatalError) { $fatalError = "Could not parse exact runtime receipt: $($_.Exception.Message)" }
    }
}
$gpuMetaObject = [ordered]@{
    schemaVersion = '1.0.0'
    token = $Token
    caseId = $CaseId
    gpuUuid = $GpuUuid
    query = 'timestamp,uuid,name,driver_version,utilization.gpu,memory.used,memory.total,power.draw,temperature.gpu'
    intervalMs = $gpuIntervalMs
    startedUtc = $gpuStartedUtc
    completedUtc = $completedUtc
    terminationReason = if ($captureStarted) { 'case-completed' } else { 'not-started' }
}
$gpuMetaObject | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $gpuMeta -Encoding utf8NoBOM

$artifacts = [System.Collections.Generic.List[object]]::new()
try {
    if ($receiptCommitted) {
        $runtimeReceipt = Get-Content -LiteralPath $receiptSource -Raw | ConvertFrom-Json -Depth 100
        $receiptDestination = Join-Path $receiptsDirectory "KiwiValidationReceipt_${Token}.json"
        $artifact = Copy-VerifiedArtifact $receiptSource $receiptDestination 'CASE_RECEIPT_JSON' $true
        if ($null -ne $artifact) { $artifacts.Add($artifact) }

        $frameSource = [string]$runtimeReceipt.frameComparisonCsv.path
        if (-not [string]::IsNullOrWhiteSpace($frameSource)) {
            $frameDestination = Join-Path $telemetryDirectory (Split-Path -Leaf $frameSource)
            $artifact = Copy-VerifiedArtifact $frameSource $frameDestination 'FRAME_COMPARISON_CSV' $true
            if ($null -ne $artifact) { $artifacts.Add($artifact) }
        }

        $runtimeSource = [string]$runtimeReceipt.runtimeValidationJson.path
        if (-not [string]::IsNullOrWhiteSpace($runtimeSource)) {
            $runtimeDestination = Join-Path $receiptsDirectory "KiwiRuntimeValidation_${Token}.json"
            $artifact = Copy-VerifiedArtifact $runtimeSource $runtimeDestination 'RUNTIME_VALIDATION_JSON' $true
            if ($null -ne $artifact) { $artifacts.Add($artifact) }
        }

        if ($mode -eq 'ZEROCOPY') {
            $ortSource = [string]$runtimeReceipt.ortZeroCopyCsv.path
            if (-not [string]::IsNullOrWhiteSpace($ortSource)) {
                $ortDestination = Join-Path $telemetryDirectory "KiwiOrtDmlZeroCopyTelemetry_v42_3_${Token}.csv"
                $artifact = Copy-VerifiedArtifact $ortSource $ortDestination 'ZERO_COPY_CSV' $true
                if ($null -ne $artifact) { $artifacts.Add($artifact) }
            }
        }
    }

    foreach ($entry in @(
        @{ Source = $playerLog; Destination = $playerLog; Type = 'PLAYER_LOG'; Required = $true },
        @{ Source = $gpuCsv; Destination = $gpuCsv; Type = 'GPU_CSV'; Required = $true },
        @{ Source = $gpuMeta; Destination = $gpuMeta; Type = 'GPU_META'; Required = $true },
        @{ Source = $videoPath; Destination = $videoPath; Type = 'VIDEO_MP4'; Required = ($purpose -eq 'VISUAL') }
    )) {
        if (Test-Path -LiteralPath $entry.Source -PathType Leaf) {
            $item = Get-Item -LiteralPath $entry.Source
            $artifacts.Add([ordered]@{
                type = $entry.Type
                sourcePath = $item.FullName
                path = $item.FullName
                sha256 = Get-Sha256 $item.FullName
                bytes = [long]$item.Length
                createdUtc = $item.CreationTimeUtc.ToString('O')
                tokenMatched = ($item.Name -like "*$Token*")
                copyHashMatched = $true
            })
        }
        elseif ($entry.Required -and $null -eq $fatalError) {
            $fatalError = "Required artifact is missing: $($entry.Type) $($entry.Source)"
        }
    }
}
catch {
    if ($null -eq $fatalError) { $fatalError = $_.Exception.Message }
}

$crashArtifacts = @(Get-ChildItem -LiteralPath $crashDirectory -File -Recurse -ErrorAction SilentlyContinue)
$result = [ordered]@{
    schemaVersion = '1.0.0'
    caseId = $CaseId
    mode = $mode
    purpose = $purpose
    token = $Token
    status = if ($null -eq $fatalError) { 'COMPLETE' } else { 'FAILED' }
    error = $fatalError
    startedUtc = $startedUtc
    completedUtc = $completedUtc
    environment = [ordered]@{
        KIWI_VALIDATION_RUN_ID = $RunId
        KIWI_VALIDATION_CASE_ID = $CaseId
        KIWI_VALIDATION_TOKEN = $Token
        KIWI_VALIDATION_MODE = $mode
        KIWI_VALIDATION_PURPOSE = $purpose
        KIWI_ORT_DML_ZERO_COPY_SHADOW = if ($mode -eq 'ZEROCOPY') { '1' } else { '0' }
    }
    process = [ordered]@{
        executable = [System.IO.Path]::GetFullPath($Executable)
        arguments = @($playerArguments)
        pid = if ($null -ne $player) { $player.Id } else { $null }
        startSucceeded = ($null -ne $player)
        startupSurvived = $startupSurvived
        readyMarkerSeen = $readyMarkerSeen
        completionMarkerSeen = $completionMarkerSeen
        receiptCommitted = $receiptCommitted
        exitObserved = ($null -ne $player -and $player.HasExited)
        exitCode = if ($null -ne $player -and $player.HasExited) { $player.ExitCode } else { $null }
        forcedTermination = $forcedTermination
        crashArtifactCount = $crashArtifacts.Count
    }
    artifacts = @($artifacts)
    videoCapture = [ordered]@{
        required = ($purpose -eq 'VISUAL')
        started = ($null -ne $ffmpeg -or $null -ne $captureStartedUtc)
        startedUtc = $captureStartedUtc
    }
}

$result | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $caseResultPath -Encoding utf8NoBOM
Write-Output $caseResultPath
if ($null -ne $fatalError) {
    Write-Error $fatalError
    exit 1
}
