[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CaseResultPath,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ThresholdsPath,

    [string]$OutputPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-CsvDataRowCount([string]$Path) {
    @(Import-Csv -LiteralPath $Path).Count
}

$caseResult = Get-Content -LiteralPath $CaseResultPath -Raw | ConvertFrom-Json -Depth 100
$thresholds = Get-Content -LiteralPath $ThresholdsPath -Raw | ConvertFrom-Json -Depth 100
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Split-Path -Parent $CaseResultPath) "artifact-completeness_$($caseResult.token).json"
}

$requiredTypes = @($thresholds.artifacts.requiredByCase.PSObject.Properties[$caseResult.caseId].Value)
if ($requiredTypes.Count -eq 0) { throw "No artifact contract found for case $($caseResult.caseId)." }
$checks = [System.Collections.Generic.List[object]]::new()
$issues = [System.Collections.Generic.List[object]]::new()

foreach ($type in $requiredTypes) {
    $matches = @($caseResult.artifacts | Where-Object type -eq $type)
    $passed = ($matches.Count -eq 1)
    $evidence = [System.Collections.Generic.List[string]]::new()
    $evidence.Add("count=$($matches.Count)")
    if ($matches.Count -eq 1) {
        $artifact = $matches[0]
        $exists = Test-Path -LiteralPath $artifact.path -PathType Leaf
        $evidence.Add("path=$($artifact.path)")
        $passed = $passed -and $exists
        if ($exists) {
            $actualHash = Get-Sha256 $artifact.path
            $hashMatched = $actualHash -eq [string]$artifact.sha256
            $tokenMatched = (Split-Path -Leaf $artifact.path) -like "*$($caseResult.token)*"
            $passed = $passed -and $hashMatched -and $tokenMatched -and [bool]$artifact.copyHashMatched
            $evidence.Add("sha256=$actualHash")
            $evidence.Add("tokenMatched=$tokenMatched")
        }
    }
    $checks.Add([ordered]@{
        id = "artifact.required.$type"
        status = if ($passed) { 'PASS' } else { 'FAIL' }
        expected = 'exactly one existing, hash-matched, token-matched artifact'
        actual = $evidence -join '; '
        evidence = $evidence.ToArray()
    })
}

foreach ($rule in @(
    @{ Type = 'FRAME_COMPARISON_CSV'; Minimum = [int]$thresholds.artifacts.minimumFrameComparisonRows },
    @{ Type = 'ZERO_COPY_CSV'; Minimum = [int]$thresholds.artifacts.minimumZeroCopyRows },
    @{ Type = 'GPU_CSV'; Minimum = [int]$thresholds.artifacts.minimumGpuRows }
)) {
    $artifact = @($caseResult.artifacts | Where-Object type -eq $rule.Type) | Select-Object -First 1
    if ($null -eq $artifact) {
        if ($requiredTypes -contains $rule.Type) {
            $checks.Add([ordered]@{ id = "artifact.rows.$($rule.Type)"; status = 'FAIL'; expected = $rule.Minimum; actual = $null; evidence = @('artifact missing') })
        }
        continue
    }
    $rowCount = Get-CsvDataRowCount $artifact.path
    $checks.Add([ordered]@{
        id = "artifact.rows.$($rule.Type)"
        status = if ($rowCount -ge $rule.Minimum) { 'PASS' } else { 'FAIL' }
        expected = $rule.Minimum
        actual = $rowCount
        evidence = @([string]$artifact.path)
    })
}

$receiptArtifact = @($caseResult.artifacts | Where-Object type -eq 'CASE_RECEIPT_JSON') | Select-Object -First 1
if ($null -ne $receiptArtifact) {
    $receipt = Get-Content -LiteralPath $receiptArtifact.path -Raw | ConvertFrom-Json -Depth 100
    $receiptMatched = (
        $receipt.runId -eq $caseResult.environment.KIWI_VALIDATION_RUN_ID -and
        $receipt.caseId -eq $caseResult.caseId -and
        $receipt.token -eq $caseResult.token -and
        $receipt.mode -eq $caseResult.mode -and
        $receipt.purpose -eq $caseResult.purpose -and
        $receipt.status -eq 'OK'
    )
    $checks.Add([ordered]@{
        id = 'receipt.identity'
        status = if ($receiptMatched) { 'PASS' } else { 'FAIL' }
        expected = 'exact run/case/token/mode/purpose and status=OK'
        actual = "run=$($receipt.runId);case=$($receipt.caseId);token=$($receipt.token);mode=$($receipt.mode);purpose=$($receipt.purpose);status=$($receipt.status)"
        evidence = @([string]$receiptArtifact.path)
    })
}

$logArtifact = @($caseResult.artifacts | Where-Object type -eq 'PLAYER_LOG') | Select-Object -First 1
if ($null -ne $logArtifact) {
    $logText = Get-Content -LiteralPath $logArtifact.path -Raw
    $exactReady = $logText.Contains("[KiwiValidation] STARTUP_READY runId=$($caseResult.environment.KIWI_VALIDATION_RUN_ID) caseId=$($caseResult.caseId) token=$($caseResult.token)")
    $exactComplete = $logText.Contains('[KiwiValidation] EXIT_REQUESTED code=0')
    $failedMarker = $logText.Contains('[KiwiValidation] FAILED error=')
    $fatalHits = [System.Collections.Generic.List[string]]::new()
    foreach ($pattern in @($thresholds.logRules.fatalPatterns)) {
        if ([regex]::IsMatch($logText, [string]$pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $fatalHits.Add([string]$pattern)
        }
    }
    if ($failedMarker) { $fatalHits.Add('[KiwiValidation] FAILED error=') }
    $logPassed = $exactReady -and $exactComplete -and $fatalHits.Count -eq 0
    $checks.Add([ordered]@{
        id = 'player.log.contract'
        status = if ($logPassed) { 'PASS' } else { 'FAIL' }
        expected = 'exact ready/completion markers and zero fatal patterns'
        actual = "ready=$exactReady;complete=$exactComplete;fatal=$($fatalHits.Count)"
        evidence = @([string]$logArtifact.path) + $fatalHits.ToArray()
    })

    foreach ($known in @($thresholds.logRules.knownIssues)) {
        $count = ([regex]::Matches($logText, [regex]::Escape([string]$known.pattern), [Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count
        if ($count -gt 0) {
            $severity = if ($count -le [int]$known.maximumCount) { [string]$known.shadowSeverity } else { 'ERROR' }
            $issues.Add([ordered]@{
                severity = $severity
                code = [string]$known.id
                message = "Known log pattern count=$count maximum=$($known.maximumCount)"
                caseId = [string]$caseResult.caseId
                evidence = @([string]$logArtifact.path, [string]$known.pattern)
            })
        }
    }
}

$processPassed = (
    $caseResult.process.startSucceeded -and
    $caseResult.process.startupSurvived -and
    $caseResult.process.readyMarkerSeen -and
    $caseResult.process.completionMarkerSeen -and
    $caseResult.process.receiptCommitted -and
    $caseResult.process.exitObserved -and
    $caseResult.process.exitCode -eq 0 -and
    -not $caseResult.process.forcedTermination -and
    $caseResult.process.crashArtifactCount -eq 0
)
$checks.Add([ordered]@{
    id = 'process.lifecycle'
    status = if ($processPassed) { 'PASS' } else { 'FAIL' }
    expected = 'clean lifecycle, exit=0, no forced termination, no crash artifacts'
    actual = ($caseResult.process | ConvertTo-Json -Compress -Depth 10)
    evidence = @($CaseResultPath)
})

$result = [ordered]@{
    schemaVersion = '1.0.0'
    caseId = [string]$caseResult.caseId
    token = [string]$caseResult.token
    status = if (@($checks | Where-Object status -eq 'FAIL').Count -eq 0) { 'PASS' } else { 'FAIL' }
    checkedUtc = [DateTime]::UtcNow.ToString('O')
    checks = $checks.ToArray()
    issues = $issues.ToArray()
}
$result | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output $OutputPath
if ($result.status -eq 'FAIL') { exit 2 }
