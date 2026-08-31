[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CaseResultPath,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ThresholdsPath,

    [string]$FfmpegPath = 'D:\KiwiAvatarSystem\Tools\ffmpeg\bin\ffmpeg.exe',
    [string]$FfprobePath = 'D:\KiwiAvatarSystem\Tools\ffmpeg\bin\ffprobe.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Add-LocalArtifact([System.Collections.Generic.List[object]]$List, [string]$Type, [string]$Path, [string]$Token) {
    $item = Get-Item -LiteralPath $Path
    $List.Add([ordered]@{
        type = $Type
        sourcePath = $item.FullName
        path = $item.FullName
        sha256 = Get-Sha256 $item.FullName
        bytes = [long]$item.Length
        createdUtc = $item.CreationTimeUtc.ToString('O')
        tokenMatched = ($item.Name -like "*$Token*")
        copyHashMatched = $true
    })
}

$caseResult = Get-Content -LiteralPath $CaseResultPath -Raw | ConvertFrom-Json -Depth 100
$thresholds = Get-Content -LiteralPath $ThresholdsPath -Raw | ConvertFrom-Json -Depth 100
if ($caseResult.purpose -ne 'VISUAL') { throw 'Test-KiwiVideo.ps1 only accepts VISUAL cases.' }
$videoArtifact = @($caseResult.artifacts | Where-Object type -eq 'VIDEO_MP4') | Select-Object -First 1
if ($null -eq $videoArtifact) { throw 'VIDEO_MP4 artifact is missing from the case result.' }

$videoDirectory = Split-Path -Parent $videoArtifact.path
$probePath = Join-Path $videoDirectory "KiwiVideoProbe_$($caseResult.token).json"
$frameIndexPath = Join-Path $videoDirectory "KiwiVideoFrameIndex_$($caseResult.token).csv"

$probeText = & $FfprobePath -v error -show_streams -show_format -of json -- $videoArtifact.path
if ($LASTEXITCODE -ne 0) { throw "ffprobe stream inspection failed with exit code $LASTEXITCODE." }
$probe = ($probeText -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100
$videoStreams = @($probe.streams | Where-Object codec_type -eq 'video')
$audioStreams = @($probe.streams | Where-Object codec_type -eq 'audio')

$framesText = & $FfprobePath -v error -select_streams v:0 -show_frames -show_entries frame=best_effort_timestamp_time,pkt_duration_time,key_frame,pict_type -of json -- $videoArtifact.path
if ($LASTEXITCODE -ne 0) { throw "ffprobe frame inspection failed with exit code $LASTEXITCODE." }
$frames = @(($framesText -join [Environment]::NewLine | ConvertFrom-Json -Depth 100).frames)

$index = [System.Collections.Generic.List[object]]::new()
$previousPts = [double]::NegativeInfinity
$monotonic = $true
for ($i = 0; $i -lt $frames.Count; $i++) {
    $pts = [double]::Parse([string]$frames[$i].best_effort_timestamp_time, [Globalization.CultureInfo]::InvariantCulture)
    if ($pts -le $previousPts) { $monotonic = $false }
    $previousPts = $pts
    $durationProperty = $frames[$i].PSObject.Properties['pkt_duration_time']
    $index.Add([pscustomobject][ordered]@{
        frameIndex = $i
        bestEffortTimestampSeconds = $pts
        packetDurationSeconds = if ($null -ne $durationProperty -and $null -ne $durationProperty.Value) { [double]$durationProperty.Value } else { $null }
        keyFrame = [int]$frames[$i].key_frame
        pictureType = [string]$frames[$i].pict_type
    })
}
$index | Export-Csv -LiteralPath $frameIndexPath -NoTypeInformation -Encoding utf8NoBOM

$decodeOutput = & $FfmpegPath -hide_banner -v error -i $videoArtifact.path -map 0:v:0 -f null NUL 2>&1
$decodeExitCode = $LASTEXITCODE
$duration = [double]::Parse([string]$probe.format.duration, [Globalization.CultureInfo]::InvariantCulture)
$codec = if ($videoStreams.Count -eq 1) { [string]$videoStreams[0].codec_name } else { '' }
$checks = [ordered]@{
    exactlyOneVideoStream = ($videoStreams.Count -eq 1)
    noAudioStream = ($audioStreams.Count -eq 0)
    codecIsH264 = ($codec -eq [string]$thresholds.video.requiredCodec)
    minimumFrameCount = ($frames.Count -ge [int]$thresholds.artifacts.minimumVideoFrames)
    durationInRange = ($duration -ge [double]$thresholds.artifacts.minimumVideoDurationSeconds -and $duration -le [double]$thresholds.artifacts.maximumVideoDurationSeconds)
    monotonicPts = $monotonic
    decodeWithoutErrors = ($decodeExitCode -eq 0 -and @($decodeOutput).Count -eq 0)
}
$failedChecks = @($checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object Key)
$probeReceipt = [ordered]@{
    schemaVersion = '1.0.0'
    caseId = [string]$caseResult.caseId
    token = [string]$caseResult.token
    videoSha256 = Get-Sha256 $videoArtifact.path
    status = if ($failedChecks.Count -eq 0) { 'PASS' } else { 'FAIL' }
    checkedUtc = [DateTime]::UtcNow.ToString('O')
    decodedFrameCount = $frames.Count
    durationSeconds = $duration
    codec = $codec
    checks = $checks
    failedChecks = $failedChecks
    decodeDiagnostics = @($decodeOutput)
    ffprobe = $probe
}
$probeReceipt | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $probePath -Encoding utf8NoBOM

$artifactList = [System.Collections.Generic.List[object]]::new()
foreach ($artifact in @($caseResult.artifacts | Where-Object { $_.type -notin @('VIDEO_PROBE_JSON', 'VIDEO_FRAME_INDEX_CSV') })) {
    $artifactList.Add($artifact)
}
Add-LocalArtifact $artifactList 'VIDEO_PROBE_JSON' $probePath ([string]$caseResult.token)
Add-LocalArtifact $artifactList 'VIDEO_FRAME_INDEX_CSV' $frameIndexPath ([string]$caseResult.token)
$caseResult.artifacts = @($artifactList)
$caseResult | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $CaseResultPath -Encoding utf8NoBOM

Write-Output $probePath
if ($failedChecks.Count -gt 0) {
    Write-Error "Video validation failed: $($failedChecks -join ', ')"
    exit 2
}
