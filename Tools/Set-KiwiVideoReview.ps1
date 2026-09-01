[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CaseResultPath,

    [Parameter(Mandatory)]
    [ValidateSet('PASS', 'FAIL')]
    [string]$Status,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Reviewer,

    [string[]]$Findings = @()
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$caseResult = Get-Content -LiteralPath $CaseResultPath -Raw | ConvertFrom-Json -Depth 100
if ($caseResult.purpose -ne 'VISUAL') { throw 'Only VISUAL cases accept a video review.' }
$video = @($caseResult.artifacts | Where-Object type -eq 'VIDEO_MP4') | Select-Object -First 1
$probe = @($caseResult.artifacts | Where-Object type -eq 'VIDEO_PROBE_JSON') | Select-Object -First 1
$index = @($caseResult.artifacts | Where-Object type -eq 'VIDEO_FRAME_INDEX_CSV') | Select-Object -First 1
if ($null -eq $video -or $null -eq $probe -or $null -eq $index) {
    throw 'Video, probe, and frame-index artifacts must exist before review.'
}
$videoHash = (Get-FileHash -LiteralPath $video.path -Algorithm SHA256).Hash.ToLowerInvariant()
if ($videoHash -ne [string]$video.sha256) { throw 'Video hash no longer matches the case artifact receipt.' }
$probeJson = Get-Content -LiteralPath $probe.path -Raw | ConvertFrom-Json -Depth 100
$indexCount = @(Import-Csv -LiteralPath $index.path).Count
if ([int]$probeJson.decodedFrameCount -ne $indexCount) { throw 'Decoded frame count does not match the frame index.' }
if ($Status -eq 'FAIL' -and $Findings.Count -eq 0) { throw 'A FAIL review requires at least one finding.' }

$reviewPath = Join-Path (Split-Path -Parent $video.path) "video-review_$($caseResult.token).json"
$review = [ordered]@{
    schemaVersion = '1.0.0'
    caseId = [string]$caseResult.caseId
    token = [string]$caseResult.token
    status = $Status
    videoSha256 = $videoHash
    decodedFrameCount = [int]$probeJson.decodedFrameCount
    reviewedFrameCount = $indexCount
    reviewer = $Reviewer
    reviewedUtc = [DateTime]::UtcNow.ToString('O')
    findings = @($Findings)
}
$review | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $reviewPath -Encoding utf8NoBOM

$caseDirectory = Split-Path -Parent $CaseResultPath
$runDirectory = Split-Path -Parent $caseDirectory
$reportPath = Join-Path $runDirectory 'report.json'
$schemaPath = Join-Path $runDirectory 'Config\KiwiValidationReport.schema.json'
if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
    if (-not (Test-Path -LiteralPath $schemaPath -PathType Leaf)) {
        throw "Report exists but its schema snapshot is missing: $schemaPath"
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -Depth 100
    $target = @($report.visualReview.cases | Where-Object caseId -eq $caseResult.caseId)
    if ($target.Count -ne 1) {
        throw "Report visual review case identity is not unique: $($caseResult.caseId)"
    }
    $target[0].status = $Status
    $target[0].videoSha256 = $videoHash
    $target[0].decodedFrameCount = [int]$probeJson.decodedFrameCount
    $target[0].reviewedFrameCount = $indexCount
    $target[0].reviewer = $Reviewer
    $target[0].reviewedUtc = [string]$review.reviewedUtc
    $target[0].findings = @($Findings)

    $reviewStatuses = @($report.visualReview.cases.status)
    $visualStatus = if ($reviewStatuses -contains 'FAIL') { 'FAIL' } elseif ($reviewStatuses -contains 'PENDING') { 'PENDING' } else { 'PASS' }
    $report.visualReview.status = $visualStatus
    $visualGate = @($report.gates | Where-Object id -eq 'visual.review')
    if ($visualGate.Count -eq 1) {
        $visualGate[0].status = if ($visualStatus -eq 'PASS') { 'PASS' } elseif ($visualStatus -eq 'FAIL') { 'FAIL' } else { 'WARN' }
        $visualGate[0].actual = $visualStatus
        $visualGate[0].evidence = @($reviewPath)
    }
    $blocking = [System.Collections.Generic.List[string]]::new()
    foreach ($reason in @($report.blockingReasons | Where-Object { -not $_.StartsWith('visual.review:', [StringComparison]::Ordinal) })) {
        $blocking.Add([string]$reason)
    }
    if ($visualStatus -ne 'PASS') { $blocking.Add("visual.review: $visualStatus") }
    $report.blockingReasons = $blocking.ToArray()
    $report.finalVerdict = if ($report.automatedVerdict -eq 'GO' -and $visualStatus -eq 'PASS') { 'GO' } else { 'NO_GO' }
    $report.completedUtc = [DateTime]::UtcNow.ToString('O')

    $temporaryReport = "$reportPath.tmp.$PID"
    $report | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporaryReport -Encoding utf8NoBOM
    $reportText = Get-Content -LiteralPath $temporaryReport -Raw
    if (-not ($reportText | Test-Json -SchemaFile $schemaPath -ErrorAction Stop)) {
        throw 'Updated validation report failed its snapshotted JSON schema.'
    }
    Move-Item -LiteralPath $temporaryReport -Destination $reportPath -Force

    $markdownPath = Join-Path $runDirectory 'report.md'
    $markdown = @(
        '# KiwiAvatarSystem v42.3 Full Validation',
        '',
        "- Run: $($report.runId)",
        "- Automated verdict: $($report.automatedVerdict)",
        "- Final verdict: $($report.finalVerdict)",
        "- Visual review: $visualStatus",
        "- Report: $reportPath",
        '',
        '## Blocking reasons',
        ''
    ) + @($report.blockingReasons | ForEach-Object { "- $_" })
    $temporaryMarkdown = "$markdownPath.tmp.$PID"
    $markdown | Set-Content -LiteralPath $temporaryMarkdown -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporaryMarkdown -Destination $markdownPath -Force
}
Write-Output $reviewPath
