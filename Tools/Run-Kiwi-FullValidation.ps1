[CmdletBinding()]
param(
    [string]$ProjectRoot = 'D:\KiwiAvatarSystem',
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.0.80f1\Editor\Unity.exe',
    [string]$ThresholdsPath = 'D:\KiwiAvatarSystem\Validation\KiwiValidationThresholds.json',
    [string]$SchemaPath = 'D:\KiwiAvatarSystem\Validation\KiwiValidationReport.schema.json',
    [string]$FfmpegPath = 'D:\KiwiAvatarSystem\Tools\ffmpeg\bin\ffmpeg.exe',
    [string]$FfprobePath = 'D:\KiwiAvatarSystem\Tools\ffmpeg\bin\ffprobe.exe',
    [string]$NvidiaSmiPath = 'C:\Windows\System32\nvidia-smi.exe',
    [string]$GpuUuid = 'GPU-6efc5d27-d766-eeac-cb81-8aa14cebbfa3',
    [switch]$SkipBuild,
    [string]$Executable
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Get-UtcNow { [DateTime]::UtcNow.ToString('O') }
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-JsonAtomic([string]$Path, $Value) {
    $temporary = "$Path.tmp.$PID"
    $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
function Add-Gate([System.Collections.Generic.List[object]]$List, [string]$Id, [string]$Status, $Expected, $Actual, [string[]]$Evidence) {
    $List.Add([ordered]@{ id = $Id; status = $Status; expected = $Expected; actual = $Actual; evidence = @($Evidence) })
}
function Test-Threshold([double]$Actual, [string]$Operator, [double]$Expected) {
    switch ($Operator) {
        '>=' { $Actual -ge $Expected }
        '<=' { $Actual -le $Expected }
        '==' { [Math]::Abs($Actual - $Expected) -le 1e-12 }
        '>' { $Actual -gt $Expected }
        '<' { $Actual -lt $Expected }
        default { throw "Unsupported operator $Operator" }
    }
}
function Get-Artifact([object]$CaseResult, [string]$Type) {
    @($CaseResult.artifacts | Where-Object type -eq $Type) | Select-Object -First 1
}
function Get-MapValue($Map, [string]$Key) {
    if ($null -eq $Map) { return $null }
    if ($Map -is [System.Collections.IDictionary]) {
        if ($Map.Contains($Key)) { return $Map[$Key] }
        return $null
    }
    $property = $Map.PSObject.Properties[$Key]
    if ($null -ne $property) { return $property.Value }
    $null
}
function New-ArtifactReport($Artifact, [string]$CaseId, [bool]$Required, [string]$Status = 'PASS') {
    [ordered]@{
        type = [string]$Artifact.type
        caseId = $CaseId
        required = $Required
        sourcePath = if ($null -ne $Artifact.sourcePath) { [string]$Artifact.sourcePath } else { $null }
        path = [System.IO.Path]::GetFullPath([string]$Artifact.path)
        sha256 = [string]$Artifact.sha256
        bytes = [long]$Artifact.bytes
        createdUtc = [string]$Artifact.createdUtc
        tokenMatched = [bool]$Artifact.tokenMatched
        copyHashMatched = [bool]$Artifact.copyHashMatched
        validationStatus = $Status
    }
}
function New-RunArtifact([string]$Type, [string]$Path) {
    $item = Get-Item -LiteralPath $Path
    [ordered]@{
        type = $Type
        caseId = $null
        required = $true
        sourcePath = $item.FullName
        path = $item.FullName
        sha256 = Get-Sha256 $item.FullName
        bytes = [long]$item.Length
        createdUtc = $item.CreationTimeUtc.ToString('O')
        tokenMatched = $true
        copyHashMatched = $true
        validationStatus = 'PASS'
    }
}
function Invoke-Unity([string]$Method, [string]$LogPath, [hashtable]$Environment) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $UnityPath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($argument in @('-batchmode', '-quit', '-projectPath', $ProjectRoot, '-executeMethod', $Method, '-logFile', $LogPath)) {
        [void]$info.ArgumentList.Add($argument)
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $info.Environment[$entry.Key] = [string]$entry.Value
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) {
        throw "Unity process start returned false for method $Method."
    }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Unity method $Method failed with exit code $($process.ExitCode). Log: $LogPath" }
}
function Invoke-ChildPowerShell([string]$ScriptPath, [string[]]$Arguments) {
    $childOutput = & (Get-Process -Id $PID).Path -NoLogo -NoProfile -NonInteractive -File $ScriptPath @Arguments
    $code = $LASTEXITCODE
    foreach ($line in @($childOutput)) { Write-Host $line }
    return $code
}

$requiredInputs = @($ProjectRoot, $UnityPath, $ThresholdsPath, $SchemaPath, $FfmpegPath, $FfprobePath, $NvidiaSmiPath)
foreach ($path in $requiredInputs) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required path is missing: $path" }
}

$thresholds = Get-Content -LiteralPath $ThresholdsPath -Raw | ConvertFrom-Json -Depth 100
$runId = [DateTime]::Now.ToString('yyyyMMdd_HHmmss') + '_v42_3_FULLVALIDATION_720P'
$runDirectory = Join-Path $ProjectRoot "ValidationRuns\$runId"
$configDirectory = Join-Path $runDirectory 'Config'
$staticDirectory = Join-Path $runDirectory 'Static'
$buildDirectory = Join-Path $runDirectory 'Build'
$transactionDirectory = Join-Path $runDirectory 'Transaction'
foreach ($directory in @($runDirectory, $configDirectory, $staticDirectory, $buildDirectory, $transactionDirectory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$lockPath = Join-Path $ProjectRoot 'ValidationRuns\.kiwi-full-validation.lock'
$lockStream = $null
try {
    $lockStream = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
}
catch {
    throw "Another Kiwi full validation owns the exclusive lock: $lockPath"
}

try {
$createdUtc = Get-UtcNow
$thresholdSnapshot = Join-Path $configDirectory 'KiwiValidationThresholds.json'
$schemaSnapshot = Join-Path $configDirectory 'KiwiValidationReport.schema.json'
Copy-Item -LiteralPath $ThresholdsPath -Destination $thresholdSnapshot
Copy-Item -LiteralPath $SchemaPath -Destination $schemaSnapshot

$manifestPath = Join-Path $ProjectRoot ([string]$thresholds.identity.installedManifest)
$modelPath = Join-Path $ProjectRoot ([string]$thresholds.identity.modelPath).Replace('/', '\')
$runtimeSource = Join-Path $ProjectRoot 'Assets\KiwiAvatarSystem\Runtime\Optimization\KiwiOrtDmlZeroCopyRuntime.cs'
$ortRuntimeDll = Join-Path $ProjectRoot 'Assets\Plugins\KiwiOrtDirectML\x86_64\onnxruntime.dll'
$bridgeDll = Join-Path $ProjectRoot 'Assets\Plugins\KiwiOrtDirectML\x86_64\KiwiOrtDmlZeroCopyBridge.dll'
$bridgePdb = Join-Path $ProjectRoot 'Assets\Plugins\KiwiOrtDirectML\x86_64\KiwiOrtDmlZeroCopyBridge.pdb'
$scenePath = Join-Path $ProjectRoot ([string]$thresholds.identity.scene).Replace('/', '\')
foreach ($path in @($manifestPath, $modelPath, $runtimeSource, $ortRuntimeDll, $bridgeDll, $bridgePdb, $scenePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Identity artifact is missing: $path" }
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100
if ($manifest.version -ne $thresholds.identity.installedVersion) { throw 'Installed manifest version mismatch.' }
if ((Get-Sha256 $modelPath) -ne [string]$thresholds.identity.modelSha256) { throw 'Model SHA-256 mismatch.' }
if ((Get-Sha256 $runtimeSource) -ne [string]$manifest.runtimeSha256) { throw 'v42.3 ORT runtime source SHA-256 mismatch.' }
if ((Get-Sha256 $bridgeDll) -ne [string]$manifest.bridgeSha256) { throw 'Zero-copy bridge SHA-256 mismatch.' }
if ((Get-Sha256 $bridgePdb) -ne [string]$manifest.bridgePdbSha256) { throw 'Zero-copy bridge PDB SHA-256 mismatch.' }

$gpuIdentityText = & $NvidiaSmiPath --query-gpu=index,uuid,name,driver_version --format=csv,noheader,nounits -i $GpuUuid
if ($LASTEXITCODE -ne 0 -or @($gpuIdentityText).Count -ne 1) { throw "GPU UUID did not resolve uniquely: $GpuUuid" }
$gpuFields = @(([string]$gpuIdentityText).Split(',') | ForEach-Object Trim)
if ($gpuFields[1] -ne $GpuUuid) { throw 'Resolved GPU UUID differs from the requested UUID.' }

$transactionPath = Join-Path $runDirectory 'transaction.json'
$transactionTargets = [System.Collections.Generic.List[object]]::new()
$targetPaths = @(
    (Join-Path $ProjectRoot 'Assets\Plugins\KiwiOrtDirectML\x86_64\KiwiOrtDmlZeroCopyBridge.dll.meta'),
    (Join-Path $ProjectRoot 'ProjectSettings\ProjectSettings.asset')
)
foreach ($targetPath in $targetPaths) {
    if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) { throw "Transaction target is missing: $targetPath" }
    $relativeName = ($targetPath.Substring($ProjectRoot.Length).TrimStart('\') -replace '[\\/:*?"<>|]', '_')
    $backupPath = Join-Path $transactionDirectory "$relativeName.backup"
    Copy-Item -LiteralPath $targetPath -Destination $backupPath
    $preHash = Get-Sha256 $targetPath
    if ((Get-Sha256 $backupPath) -ne $preHash) { throw "Transaction backup hash mismatch: $targetPath" }
    $transactionTargets.Add([ordered]@{ path = $targetPath; preSha256 = $preHash; postSha256 = $null; backupPath = $backupPath; restoreSha256 = $null })
}
$transaction = [ordered]@{
    state = 'PREPARED'; startedUtc = Get-UtcNow; updatedUtc = Get-UtcNow
    targets = $transactionTargets.ToArray(); rollbackAttempted = $false; rollbackVerified = $false
}
Write-JsonAtomic $transactionPath $transaction

$applyReceipt = Join-Path $staticDirectory 'plugin-importer-apply.txt'
$applyLog = Join-Path $staticDirectory 'plugin-importer-apply.log'
$staticLog = Join-Path $staticDirectory 'optimization-validator.log'
$staticReceipt = Join-Path $staticDirectory 'static-validation.json'
$buildLog = Join-Path $buildDirectory 'unity-build.log'
$transaction.state = 'APPLYING'; $transaction.updatedUtc = Get-UtcNow
Write-JsonAtomic $transactionPath $transaction
try {
    Invoke-Unity 'KiwiV42PluginImportPolicy.ApplyAndValidateBatch' $applyLog @{ KIWI_V42_1_PLUGIN_POLICY_RESULT = $applyReceipt }
    if (-not (Test-Path -LiteralPath $applyReceipt) -or -not (Get-Content -LiteralPath $applyReceipt -Raw).StartsWith('status=PASS')) {
        throw 'PluginImporter API apply receipt is missing or not PASS.'
    }
    Invoke-Unity 'KiwiAutomatedValidationEditorEntryPoints.ValidateStaticBatch' $staticLog @{ KIWI_VALIDATION_STATIC_RESULT = $staticReceipt }
    if (-not (Test-Path -LiteralPath $staticReceipt) -or (Get-Content -LiteralPath $staticReceipt -Raw | ConvertFrom-Json -Depth 100).status -ne 'PASS') {
        throw 'Harness static validation receipt is missing or not PASS.'
    }
    foreach ($target in $transactionTargets) { $target.postSha256 = Get-Sha256 $target.path }
    $transaction.state = 'COMMITTED'; $transaction.updatedUtc = Get-UtcNow
    Write-JsonAtomic $transactionPath $transaction
}
catch {
    $transaction.state = 'ROLLING_BACK'; $transaction.rollbackAttempted = $true; $transaction.updatedUtc = Get-UtcNow
    Write-JsonAtomic $transactionPath $transaction
    $rollbackPassed = $true
    foreach ($target in $transactionTargets) {
        Copy-Item -LiteralPath $target.backupPath -Destination $target.path -Force
        $target.restoreSha256 = Get-Sha256 $target.path
        if ($target.restoreSha256 -ne $target.preSha256) { $rollbackPassed = $false }
    }
    $transaction.rollbackVerified = $rollbackPassed
    $transaction.state = if ($rollbackPassed) { 'ROLLED_BACK' } else { 'ROLLBACK_FAILED' }
    $transaction.updatedUtc = Get-UtcNow
    Write-JsonAtomic $transactionPath $transaction
    throw
}

if (-not $SkipBuild) {
    $Executable = Join-Path $buildDirectory 'KiwiAvatarSystem_v42_3_DX12_AUTOMATED_VALIDATION.exe'
    Invoke-Unity 'KiwiStandaloneBuildDiagnostic.BuildWindows64' $buildLog @{ KIWI_V42_3_BUILD_OUTPUT = $Executable }
}
elseif ([string]::IsNullOrWhiteSpace($Executable)) {
    throw '-SkipBuild requires -Executable because the Player must contain the validation controller.'
}
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "Built Player is missing: $Executable" }
$buildMeta = Join-Path (Split-Path -Parent $Executable) 'KiwiStandalone_v42_3.build.meta.txt'
$preflightReport = Join-Path (Split-Path -Parent $Executable) 'KiwiStandalone_v42_3.preflight.json'
if (-not $SkipBuild -and (-not (Test-Path $buildMeta) -or -not (Test-Path $preflightReport))) { throw 'v42.3 build meta or preflight receipt is missing.' }
$preflightVersion = if (Test-Path $preflightReport) { [string](Get-Content $preflightReport -Raw | ConvertFrom-Json -Depth 100).version } else { 'external-build' }

$caseDefinitions = @(
    @{ Id = 'BASELINE_METRICS'; Order = 1 },
    @{ Id = 'ZEROCOPY_METRICS'; Order = 2 },
    @{ Id = 'BASELINE_VISUAL'; Order = 3 },
    @{ Id = 'ZEROCOPY_VISUAL'; Order = 4 }
)
$caseResultPaths = [System.Collections.Generic.List[string]]::new()
foreach ($definition in $caseDefinitions) {
    $token = "$runId-$($definition.Id)-$([guid]::NewGuid().ToString('N'))"
    $expectedResult = Join-Path $runDirectory "$($definition.Id)\case-result_${token}.json"
    $caseExit = Invoke-ChildPowerShell (Join-Path $ProjectRoot 'Tools\Invoke-KiwiStandaloneCase.ps1') @(
        '-CaseId', $definition.Id, '-RunId', $runId, '-Token', $token,
        '-Executable', $Executable, '-RunDirectory', $runDirectory,
        '-ThresholdsPath', $thresholdSnapshot, '-FfmpegPath', $FfmpegPath,
        '-NvidiaSmiPath', $NvidiaSmiPath, '-GpuUuid', $GpuUuid
    )
    if (Test-Path -LiteralPath $expectedResult) {
        $caseResultPaths.Add($expectedResult)
        $observedCaseResult = Get-Content -LiteralPath $expectedResult -Raw | ConvertFrom-Json -Depth 100
        if ($observedCaseResult.status -eq 'COMPLETE' -and $definition.Id.EndsWith('_VISUAL')) {
            [void](Invoke-ChildPowerShell (Join-Path $ProjectRoot 'Tools\Test-KiwiVideo.ps1') @(
                '-CaseResultPath', $expectedResult, '-ThresholdsPath', $thresholdSnapshot,
                '-FfmpegPath', $FfmpegPath, '-FfprobePath', $FfprobePath
            ))
        }
        elseif ($observedCaseResult.status -eq 'COMPLETE') {
            [void](Invoke-ChildPowerShell (Join-Path $ProjectRoot 'Tools\Analyze-KiwiTelemetry.ps1') @(
                '-CaseResultPath', $expectedResult, '-ThresholdsPath', $thresholdSnapshot
            ))
        }
        [void](Invoke-ChildPowerShell (Join-Path $ProjectRoot 'Tools\Test-KiwiArtifactCompleteness.ps1') @(
            '-CaseResultPath', $expectedResult, '-ThresholdsPath', $thresholdSnapshot
        ))
    }
    else {
        Write-Warning "Case did not produce its exact result path: $expectedResult exit=$caseExit"
    }
    if ([int]$thresholds.execution.caseCooldownSeconds -gt 0) {
        Start-Sleep -Seconds ([int]$thresholds.execution.caseCooldownSeconds)
    }
}

$allArtifacts = [System.Collections.Generic.List[object]]::new()
$allIssues = [System.Collections.Generic.List[object]]::new()
$globalGates = [System.Collections.Generic.List[object]]::new()
$caseReports = [System.Collections.Generic.List[object]]::new()
Add-Gate $globalGates 'identity.v42_3' 'PASS' 'all exact identity and hashes match' 'matched' @($manifestPath, $modelPath, $runtimeSource, $ortRuntimeDll, $bridgeDll)
Add-Gate $globalGates 'transaction.apply' 'PASS' 'COMMITTED' $transaction.state @($transactionPath)
Add-Gate $globalGates 'static.validation' 'PASS' 'PASS' 'PASS' @($applyReceipt, $staticReceipt)
Add-Gate $globalGates 'build.v42_3' 'PASS' 'v42.3 Development DX12 Player' $Executable @($buildMeta, $preflightReport)

for ($i = 0; $i -lt $caseDefinitions.Count; $i++) {
    $definition = $caseDefinitions[$i]
    $resultPath = @($caseResultPaths | Where-Object { $_ -like "*\$($definition.Id)\*" }) | Select-Object -First 1
    if ($null -eq $resultPath) {
        Add-Gate $globalGates "case.$($definition.Id)" 'FAIL' 'complete exact case result' 'missing' @($runDirectory)
        continue
    }
    $caseResult = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json -Depth 100
    $caseGates = [System.Collections.Generic.List[object]]::new()
    $caseIssues = [System.Collections.Generic.List[object]]::new()
    $metrics = [ordered]@{}

    $completenessPath = Join-Path (Split-Path -Parent $resultPath) "artifact-completeness_$($caseResult.token).json"
    if (Test-Path $completenessPath) {
        $completeness = Get-Content $completenessPath -Raw | ConvertFrom-Json -Depth 100
        foreach ($gate in @($completeness.checks)) { $caseGates.Add($gate) }
        foreach ($issue in @($completeness.issues)) { $caseIssues.Add($issue); $allIssues.Add($issue) }
    }
    else { Add-Gate $caseGates 'artifact.completeness' 'FAIL' 'receipt' 'missing' @($completenessPath) }

    if ($caseResult.purpose -eq 'METRICS') {
        $analysisPath = Join-Path (Split-Path -Parent $resultPath) "analysis_$($caseResult.token).json"
        if (Test-Path $analysisPath) {
            $analysis = Get-Content $analysisPath -Raw | ConvertFrom-Json -Depth 100
            foreach ($property in $analysis.metrics.PSObject.Properties) { $metrics[$property.Name] = $property.Value }
            foreach ($evaluation in @($analysis.evaluations)) {
                Add-Gate $caseGates $evaluation.id $evaluation.status $evaluation.expected $evaluation.actual @($analysisPath)
            }
        }
        else { Add-Gate $caseGates 'telemetry.analysis' 'FAIL' 'analysis receipt' 'missing' @($analysisPath) }
    }
    else {
        $videoForProbe = Get-Artifact $caseResult 'VIDEO_MP4'
        $probePath = if ($null -ne $videoForProbe) { Join-Path (Split-Path -Parent $videoForProbe.path) "KiwiVideoProbe_$($caseResult.token).json" } else { '' }
        if (-not [string]::IsNullOrEmpty($probePath) -and (Test-Path $probePath)) {
            $probe = Get-Content $probePath -Raw | ConvertFrom-Json -Depth 100
            Add-Gate $caseGates 'video.automated' $probe.status 'all video checks PASS' ($probe.failedChecks -join ',') @($probePath)
        }
        else {
            $probeEvidence = if ($probePath) { $probePath } else { $resultPath }
            Add-Gate $caseGates 'video.automated' 'FAIL' 'probe receipt' 'missing' @($probeEvidence)
        }
    }

    $requiredTypes = @($thresholds.artifacts.requiredByCase.PSObject.Properties[$definition.Id].Value)
    foreach ($artifact in @($caseResult.artifacts)) {
        $allArtifacts.Add((New-ArtifactReport $artifact $definition.Id ($requiredTypes -contains $artifact.type)))
    }
    if ($caseResult.status -ne 'COMPLETE') {
        $issue = [ordered]@{ severity = 'ERROR'; code = 'case.execution.failed'; message = [string]$caseResult.error; caseId = $definition.Id; evidence = @($resultPath) }
        $caseIssues.Add($issue); $allIssues.Add($issue)
    }
    $casePassed = ($caseResult.status -eq 'COMPLETE' -and @($caseGates | Where-Object status -eq 'FAIL').Count -eq 0)
    $caseGateStatus = if ($casePassed) { 'PASS' } else { 'FAIL' }
    Add-Gate $globalGates "case.$($definition.Id)" $caseGateStatus 'case complete and all automated gates PASS' $caseResult.status @($resultPath)
    $caseReports.Add([ordered]@{
        caseId = $definition.Id; order = [int]$definition.Order; mode = [string]$caseResult.mode; purpose = [string]$caseResult.purpose
        status = [string]$caseResult.status; startedUtc = [string]$caseResult.startedUtc; completedUtc = [string]$caseResult.completedUtc
        environment = $caseResult.environment; process = $caseResult.process; metrics = $metrics; gates = $caseGates.ToArray()
        artifacts = @($allArtifacts | Where-Object caseId -eq $definition.Id); issues = $caseIssues.ToArray()
    })
}

$baseline = @($caseReports | Where-Object caseId -eq 'BASELINE_METRICS') | Select-Object -First 1
$candidate = @($caseReports | Where-Object caseId -eq 'ZEROCOPY_METRICS') | Select-Object -First 1
$relativeEvaluations = [System.Collections.Generic.List[object]]::new()
foreach ($threshold in @($thresholds.relativeThresholds)) {
    $actual = $null
    if ($null -ne $baseline -and $null -ne $candidate) {
        $baselineMetric = Get-MapValue $baseline.metrics ([string]$threshold.metric)
        $candidateMetric = Get-MapValue $candidate.metrics ([string]$threshold.metric)
        if ($null -ne $baselineMetric -and $null -ne $candidateMetric) {
            $baselineStatistic = Get-MapValue $baselineMetric.statistics ([string]$threshold.statistic)
            $candidateStatistic = Get-MapValue $candidateMetric.statistics ([string]$threshold.statistic)
            if ($null -ne $baselineStatistic -and $null -ne $candidateStatistic) {
                $baselineValue = [double]$baselineStatistic
                $candidateValue = [double]$candidateStatistic
                $actual = if ($threshold.comparison -eq 'ratio') { if ($baselineValue -ne 0) { $candidateValue / $baselineValue } else { $null } } else { $candidateValue - $baselineValue }
            }
        }
    }
    $status = if ($null -eq $actual) { 'SKIP' } elseif (Test-Threshold ([double]$actual) ([string]$threshold.operator) ([double]$threshold.value)) { 'PASS' } else { 'FAIL' }
    $relativeEvaluations.Add([ordered]@{ id = [string]$threshold.id; metric = [string]$threshold.metric; statistic = [string]$threshold.statistic; operator = [string]$threshold.operator; expected = [double]$threshold.value; actual = $actual; unit = [string]$threshold.unit; status = $status })
}
$comparisonStatus = if (@($relativeEvaluations | Where-Object status -eq 'FAIL').Count -gt 0) { 'FAIL' } elseif (@($relativeEvaluations | Where-Object status -eq 'SKIP').Count -gt 0) { 'SKIP' } else { 'PASS' }
Add-Gate $globalGates 'comparison.relative' $comparisonStatus 'all relative A/B thresholds PASS' $comparisonStatus @($thresholdSnapshot)

$visualReviewCases = [System.Collections.Generic.List[object]]::new()
foreach ($caseId in @($thresholds.video.requiredVisualReviewCases)) {
    $case = @($caseReports | Where-Object caseId -eq $caseId) | Select-Object -First 1
    $video = if ($null -ne $case) { @($case.artifacts | Where-Object type -eq 'VIDEO_MP4') | Select-Object -First 1 } else { $null }
    $caseSourcePath = @($caseResultPaths | Where-Object { $_ -like "*\$caseId\*" }) | Select-Object -First 1
    $caseSource = if ($null -ne $caseSourcePath) { Get-Content $caseSourcePath -Raw | ConvertFrom-Json -Depth 100 } else { $null }
    $reviewPath = if ($null -ne $video -and $null -ne $caseSource) { Join-Path (Split-Path -Parent $video.path) "video-review_$($caseSource.token).json" } else { '' }
    $review = $null
    if ($null -ne $video -and -not [string]::IsNullOrEmpty($reviewPath) -and (Test-Path -LiteralPath $reviewPath -PathType Leaf)) {
            $loaded = Get-Content $reviewPath -Raw | ConvertFrom-Json -Depth 100
            if ($loaded.videoSha256 -eq $video.sha256 -and $loaded.caseId -eq $caseId) { $review = $loaded }
    }
    if ($null -ne $review) {
        $visualReviewCases.Add([ordered]@{ caseId = $caseId; status = [string]$review.status; videoSha256 = [string]$review.videoSha256; decodedFrameCount = [int]$review.decodedFrameCount; reviewedFrameCount = [int]$review.reviewedFrameCount; reviewer = [string]$review.reviewer; reviewedUtc = [string]$review.reviewedUtc; findings = @($review.findings) })
    }
    else {
        $decoded = 0
        if ($null -ne $video) {
            $probeArtifact = @($case.artifacts | Where-Object type -eq 'VIDEO_PROBE_JSON') | Select-Object -First 1
            if ($null -ne $probeArtifact) { $decoded = [int](Get-Content $probeArtifact.path -Raw | ConvertFrom-Json -Depth 100).decodedFrameCount }
        }
        $visualReviewCases.Add([ordered]@{ caseId = $caseId; status = 'PENDING'; videoSha256 = if ($null -ne $video) { [string]$video.sha256 } else { $null }; decodedFrameCount = $decoded; reviewedFrameCount = 0; reviewer = $null; reviewedUtc = $null; findings = @() })
    }
}
$visualStatus = if (@($visualReviewCases | Where-Object status -eq 'FAIL').Count -gt 0) { 'FAIL' } elseif (@($visualReviewCases | Where-Object status -eq 'PENDING').Count -gt 0) { 'PENDING' } else { 'PASS' }
$visualGateStatus = if ($visualStatus -eq 'PASS') { 'PASS' } elseif ($visualStatus -eq 'FAIL') { 'FAIL' } else { 'WARN' }
Add-Gate $globalGates 'visual.review' $visualGateStatus 'frame-by-frame review PASS for both visual cases' $visualStatus @($runDirectory)

$configArtifacts = @((New-RunArtifact 'THRESHOLDS_SNAPSHOT' $thresholdSnapshot), (New-RunArtifact 'SCHEMA_SNAPSHOT' $schemaSnapshot))
foreach ($artifact in $configArtifacts) { $allArtifacts.Add($artifact) }
$allAutomatedPassed = @($globalGates | Where-Object { $_.id -ne 'visual.review' -and $_.status -ne 'PASS' }).Count -eq 0
$automatedVerdict = if ($allAutomatedPassed) { 'GO' } else { 'NO_GO' }
$finalVerdict = if ($allAutomatedPassed -and $visualStatus -eq 'PASS') { 'GO' } else { 'NO_GO' }
$blockingReasons = [System.Collections.Generic.List[string]]::new()
foreach ($gate in @($globalGates | Where-Object status -in @('FAIL', 'SKIP'))) { $blockingReasons.Add("$($gate.id): $($gate.actual)") }
if ($visualStatus -ne 'PASS') { $blockingReasons.Add("visual.review: $visualStatus") }
$runComplete = ($caseReports.Count -eq 4)

$ffmpegVersion = (& $FfmpegPath -version | Select-Object -First 1)
$ffprobeVersion = (& $FfprobePath -version | Select-Object -First 1)
$nvidiaVersion = (& $NvidiaSmiPath --version | Select-Object -First 1)
$report = [ordered]@{
    schemaVersion = '1.0.0'; harnessVersion = 'v42.3-validation-v1'; runId = $runId; profileId = [string]$thresholds.profileId
    status = if ($runComplete) { 'COMPLETE' } else { 'INCOMPLETE' }; automatedVerdict = $automatedVerdict; finalVerdict = $finalVerdict
    blockingReasons = $blockingReasons.ToArray(); createdUtc = $createdUtc; completedUtc = Get-UtcNow
    identity = [ordered]@{
        systemVersion = 'v42.3'; installedVersion = [string]$manifest.version; installedManifest = $manifestPath; installedManifestSha256 = Get-Sha256 $manifestPath
        unityVersion = '6000.0.80f1'; scene = [string]$thresholds.identity.scene; graphicsApi = 'Direct3D12'; developmentBuild = $true
        resolution = [ordered]@{ label = '720P'; width = 1280; height = 720 }; modelPath = $modelPath; modelSha256 = Get-Sha256 $modelPath
        preflightContractVersion = $preflightVersion; runtimeHarnessVersion = 'KIWI_V5_1_PHASE16_20_32_V42_3_AUTOMATED_VALIDATION_V1'
    }
    environment = [ordered]@{
        machineName = [Environment]::MachineName; os = [Environment]::OSVersion.VersionString; powerShellVersion = $PSVersionTable.PSVersion.ToString()
        gpu = [ordered]@{ index = [int]$gpuFields[0]; uuid = $gpuFields[1]; name = $gpuFields[2]; driverVersion = $gpuFields[3] }
        tools = @(
            [ordered]@{ name = 'Unity'; path = $UnityPath; version = '6000.0.80f1'; sha256 = Get-Sha256 $UnityPath },
            [ordered]@{ name = 'ffmpeg'; path = $FfmpegPath; version = [string]$ffmpegVersion; sha256 = Get-Sha256 $FfmpegPath },
            [ordered]@{ name = 'ffprobe'; path = $FfprobePath; version = [string]$ffprobeVersion; sha256 = Get-Sha256 $FfprobePath },
            [ordered]@{ name = 'nvidia-smi'; path = $NvidiaSmiPath; version = [string]$nvidiaVersion; sha256 = Get-Sha256 $NvidiaSmiPath }
        )
        thresholdsSha256 = Get-Sha256 $thresholdSnapshot; reportSchemaSha256 = Get-Sha256 $schemaSnapshot
    }
    transaction = $transaction; cases = $caseReports.ToArray()
    comparison = [ordered]@{ baselineCaseId = if ($null -ne $baseline) { 'BASELINE_METRICS' } else { $null }; candidateCaseId = if ($null -ne $candidate) { 'ZEROCOPY_METRICS' } else { $null }; evaluations = $relativeEvaluations.ToArray(); status = $comparisonStatus }
    gates = $globalGates.ToArray(); artifacts = $allArtifacts.ToArray()
    visualReview = [ordered]@{ required = $true; status = $visualStatus; cases = $visualReviewCases.ToArray() }
    issues = $allIssues.ToArray()
}

$reportPath = Join-Path $runDirectory 'report.json'
$invalidPath = Join-Path $runDirectory 'report.invalid.json'
Write-JsonAtomic $reportPath $report
$reportText = Get-Content -LiteralPath $reportPath -Raw
if (-not ($reportText | Test-Json -SchemaFile $schemaSnapshot -ErrorAction Stop)) {
    Move-Item -LiteralPath $reportPath -Destination $invalidPath -Force
    throw "Report schema validation failed; preserved at $invalidPath"
}
$markdownPath = Join-Path $runDirectory 'report.md'
$markdown = @(
    "# KiwiAvatarSystem v42.3 Full Validation",
    '',
    "- Run: $runId",
    "- Automated verdict: $automatedVerdict",
    "- Final verdict: $finalVerdict",
    "- Visual review: $visualStatus",
    "- Report: $reportPath",
    '',
    '## Blocking reasons',
    ''
) + @($blockingReasons | ForEach-Object { "- $_" })
$markdown | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM

Write-Output $reportPath
if ($finalVerdict -ne 'GO') { exit 2 }
}
finally {
    if ($null -ne $lockStream) { $lockStream.Dispose() }
}
