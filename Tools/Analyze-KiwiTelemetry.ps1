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
$invariant = [Globalization.CultureInfo]::InvariantCulture

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Convert-ToFiniteDouble($Value) {
    if ($null -eq $Value) { return $null }
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    $number = 0.0
    if (-not [double]::TryParse($text, [Globalization.NumberStyles]::Float, $invariant, [ref]$number)) {
        return $null
    }
    if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) { return $null }
    $number
}

function Get-NumericValues([object[]]$Rows, [string]$Column) {
    $values = [System.Collections.Generic.List[double]]::new()
    foreach ($row in $Rows) {
        $property = $row.PSObject.Properties[$Column]
        if ($null -eq $property) { continue }
        $value = Convert-ToFiniteDouble $property.Value
        if ($null -ne $value) { $values.Add($value) }
    }
    $values.ToArray()
}

function Get-PositiveNumericValues([object[]]$Rows, [string]$Column) {
    @(Get-NumericValues $Rows $Column | Where-Object { $_ -gt 0.0 })
}

function Get-Percentile([double[]]$Values, [double]$Quantile) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 1) { return [double]$sorted[0] }
    $position = ($sorted.Count - 1) * $Quantile
    $lower = [int][Math]::Floor($position)
    $upper = [int][Math]::Ceiling($position)
    if ($lower -eq $upper) { return [double]$sorted[$lower] }
    $weight = $position - $lower
    ([double]$sorted[$lower] * (1.0 - $weight)) + ([double]$sorted[$upper] * $weight)
}

function Get-Statistics([double[]]$Values, [switch]$SingleValue) {
    if ($Values.Count -eq 0) { return [ordered]@{} }
    if ($SingleValue) { return [ordered]@{ value = [double]$Values[0] } }
    [ordered]@{
        min = [double](($Values | Measure-Object -Minimum).Minimum)
        p50 = [double](Get-Percentile $Values 0.50)
        p95 = [double](Get-Percentile $Values 0.95)
        max = [double](($Values | Measure-Object -Maximum).Maximum)
        mean = [double](($Values | Measure-Object -Average).Average)
    }
}

function New-Metric(
    [string]$Unit,
    [double[]]$Values,
    [System.Collections.IDictionary]$Statistics,
    [string]$ArtifactSha256,
    [string[]]$Columns,
    [string]$Calculation
) {
    [ordered]@{
        unit = $Unit
        sampleCount = $Values.Count
        statistics = $Statistics
        source = [ordered]@{
            artifactSha256 = $ArtifactSha256
            columns = @($Columns)
            calculation = $Calculation
        }
        status = 'PASS'
    }
}

function Get-Delta([object[]]$Rows, [string]$Column) {
    $values = @(Get-NumericValues $Rows $Column)
    if ($values.Count -lt 2) { return $null }
    [double]$values[-1] - [double]$values[0]
}

function Test-Comparison([double]$Actual, [string]$Operator, [double]$Expected) {
    switch ($Operator) {
        '>=' { return $Actual -ge $Expected }
        '<=' { return $Actual -le $Expected }
        '==' { return [Math]::Abs($Actual - $Expected) -le 1e-12 }
        '>'  { return $Actual -gt $Expected }
        '<'  { return $Actual -lt $Expected }
        default { throw "Unsupported threshold operator: $Operator" }
    }
}

function Find-Artifact([string]$Type) {
    @($caseResult.artifacts | Where-Object { $_.type -eq $Type }) | Select-Object -First 1
}

$caseResult = Get-Content -LiteralPath $CaseResultPath -Raw | ConvertFrom-Json -Depth 100
$thresholds = Get-Content -LiteralPath $ThresholdsPath -Raw | ConvertFrom-Json -Depth 100
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Split-Path -Parent $CaseResultPath) "analysis_$($caseResult.token).json"
}

$frameArtifact = Find-Artifact 'FRAME_COMPARISON_CSV'
$gpuArtifact = Find-Artifact 'GPU_CSV'
if ($null -eq $frameArtifact -or $null -eq $gpuArtifact) {
    throw 'FRAME_COMPARISON_CSV and GPU_CSV are required for telemetry analysis.'
}

$frameRowsAll = @(Import-Csv -LiteralPath $frameArtifact.path)
if ($frameRowsAll.Count -eq 0) { throw 'Frame comparison CSV has no data rows.' }
$timeValues = @(Get-NumericValues $frameRowsAll 'realtimeSeconds')
if ($timeValues.Count -ne $frameRowsAll.Count) { throw 'Frame comparison realtimeSeconds is incomplete.' }
$trimBegin = $timeValues[0] + [double]$thresholds.execution.analysisTrimStartSeconds
$trimEnd = $timeValues[-1] - [double]$thresholds.execution.analysisTrimEndSeconds
$frameRows = @($frameRowsAll | Where-Object {
    $t = Convert-ToFiniteDouble $_.realtimeSeconds
    $null -ne $t -and $t -ge $trimBegin -and $t -le $trimEnd
})
if ($frameRows.Count -lt 2) { throw 'Frame comparison trim window contains fewer than two rows.' }

$frameSha = Get-Sha256 $frameArtifact.path
$metrics = [ordered]@{}

$elapsed = (Convert-ToFiniteDouble $frameRows[-1].realtimeSeconds) - (Convert-ToFiniteDouble $frameRows[0].realtimeSeconds)
if ($elapsed -le 0) { throw 'Frame comparison trim window has non-positive elapsed time.' }

$cameraCountDelta = Get-Delta $frameRows 'nativeSourceCount'
$cameraHz = $cameraCountDelta / $elapsed
$metrics['camera.nativeSourceHz'] = New-Metric 'Hz' @([double]$cameraHz) ([ordered]@{ value = [double]$cameraHz }) $frameSha @('realtimeSeconds', 'nativeSourceCount') 'delta(nativeSourceCount)/delta(realtimeSeconds) after trim'

$renderValues = @(Get-NumericValues $frameRows 'renderFps')
$metrics['render.fps'] = New-Metric 'fps' $renderValues (Get-Statistics $renderValues) $frameSha @('renderFps') 'distribution after trim'

$sentisValues = @(Get-PositiveNumericValues $frameRows 'inferenceRequestToDoneObservedMs')
$metrics['sentis.requestToDoneMs'] = New-Metric 'ms' $sentisValues (Get-Statistics $sentisValues) $frameSha @('inferenceRequestToDoneObservedMs') 'distribution after trim'

$acceptedValues = @(Get-PositiveNumericValues $frameRows 'inferenceAcceptedSourceAgeMs')
$metrics['tracking.acceptedSourceAgeMs'] = New-Metric 'ms' $acceptedValues (Get-Statistics $acceptedValues) $frameSha @('inferenceAcceptedSourceAgeMs') 'distribution after trim'

$canonicalDelta = Get-Delta $frameRows 'canonicalAdoptionCount'
$canonicalHz = $canonicalDelta / $elapsed
$metrics['tracking.canonicalHz'] = New-Metric 'Hz' @([double]$canonicalHz) ([ordered]@{ value = [double]$canonicalHz }) $frameSha @('realtimeSeconds', 'canonicalAdoptionCount') 'delta(canonicalAdoptionCount)/delta(realtimeSeconds) after trim'

$dropDelta = Get-Delta $frameRows 'nativeDroppedCount'
$metrics['native.dropCountDelta'] = New-Metric 'count' @([double]$dropDelta) ([ordered]@{ value = [double]$dropDelta }) $frameSha @('nativeDroppedCount') 'last-first after trim'

$captureWaitDelta = Get-Delta $frameRows 'nativeCaptureGpuWaitCount'
$processingWaitDelta = Get-Delta $frameRows 'nativeProcessingGpuWaitCount'
$gpuWaitDelta = $captureWaitDelta + $processingWaitDelta
$metrics['native.gpuWaitCountDelta'] = New-Metric 'count' @([double]$gpuWaitDelta) ([ordered]@{ value = [double]$gpuWaitDelta }) $frameSha @('nativeCaptureGpuWaitCount', 'nativeProcessingGpuWaitCount') 'sum of counter deltas after trim'

$crossSystemDelta = Get-Delta $frameRows 'inferenceDiscardedCrossSystemCount'
$metrics['tracking.crossSystemDiscardDelta'] = New-Metric 'count' @([double]$crossSystemDelta) ([ordered]@{ value = [double]$crossSystemDelta }) $frameSha @('inferenceDiscardedCrossSystemCount') 'last-first after trim'

$handoffViolationDelta = Get-Delta $frameRows 'handoffAuthorityViolationCount'
$facePartViolationDelta = Get-Delta $frameRows 'facePartPresentationEpochViolationCount'
$epochViolationDelta = $handoffViolationDelta + $facePartViolationDelta
$metrics['runtime.epochViolationDelta'] = New-Metric 'count' @([double]$epochViolationDelta) ([ordered]@{ value = [double]$epochViolationDelta }) $frameSha @('handoffAuthorityViolationCount', 'facePartPresentationEpochViolationCount') 'sum of counter deltas after trim'

$runtimeArtifact = Find-Artifact 'RUNTIME_VALIDATION_JSON'
$runtimeJson = Get-Content -LiteralPath $runtimeArtifact.path -Raw | ConvertFrom-Json -Depth 100
$runtimeCount = [double]$runtimeJson.errors + [double]$runtimeJson.criticals
$runtimeSha = Get-Sha256 $runtimeArtifact.path
$metrics['runtime.errorCriticalCount'] = New-Metric 'count' @($runtimeCount) ([ordered]@{ value = $runtimeCount }) $runtimeSha @('errors', 'criticals') 'errors+criticals'

$gpuRows = @(Import-Csv -LiteralPath $gpuArtifact.path)
if ($gpuRows.Count -eq 0) { throw 'GPU CSV has no data rows.' }
$uuidColumn = @($gpuRows[0].PSObject.Properties.Name | Where-Object { $_ -match '^uuid' }) | Select-Object -First 1
$utilColumn = @($gpuRows[0].PSObject.Properties.Name | Where-Object { $_ -match '^utilization\.gpu' }) | Select-Object -First 1
$nameColumn = @($gpuRows[0].PSObject.Properties.Name | Where-Object { $_ -match '^name' }) | Select-Object -First 1
$driverColumn = @($gpuRows[0].PSObject.Properties.Name | Where-Object { $_ -match '^driver_version' }) | Select-Object -First 1
if ($null -eq $uuidColumn -or $null -eq $utilColumn -or $null -eq $nameColumn -or $null -eq $driverColumn) { throw 'GPU CSV required columns are missing.' }
$gpuRowsValid = @($gpuRows | Where-Object {
    $uuid = ([string]$_.PSObject.Properties[$uuidColumn].Value).Trim()
    $name = ([string]$_.PSObject.Properties[$nameColumn].Value).Trim()
    $driver = ([string]$_.PSObject.Properties[$driverColumn].Value).Trim()
    $util = Convert-ToFiniteDouble $_.PSObject.Properties[$utilColumn].Value
    -not [string]::IsNullOrWhiteSpace($uuid) -and
    -not [string]::IsNullOrWhiteSpace($name) -and
    -not [string]::IsNullOrWhiteSpace($driver) -and
    $null -ne $util
})
if ($gpuRowsValid.Count -eq 0) { throw 'GPU CSV has no complete sample rows.' }
$expectedUuid = [string](Get-Content -LiteralPath (Find-Artifact 'GPU_META').path -Raw | ConvertFrom-Json).gpuUuid
$observedUuids = @(
    $gpuRowsValid |
        ForEach-Object { ([string]$_.PSObject.Properties[$uuidColumn].Value).Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
)
if ($observedUuids.Count -ne 1 -or $observedUuids[0] -ne $expectedUuid) {
    throw "GPU CSV UUID mismatch: expected=$expectedUuid actual=$($observedUuids -join ',')"
}
$gpuValues = @(Get-NumericValues $gpuRowsValid $utilColumn)
$gpuSha = Get-Sha256 $gpuArtifact.path
$metrics['gpu.utilizationPercent'] = New-Metric 'percent' $gpuValues (Get-Statistics $gpuValues) $gpuSha @($utilColumn, $uuidColumn) 'nvidia-smi distribution during recording'

if ($caseResult.mode -eq 'ZEROCOPY') {
    $ortArtifact = Find-Artifact 'ZERO_COPY_CSV'
    if ($null -eq $ortArtifact) { throw 'ZERO_COPY_CSV is required for ZEROCOPY analysis.' }
    $ortRowsAll = @(Import-Csv -LiteralPath $ortArtifact.path)
    if ($ortRowsAll.Count -eq 0) { throw 'ORT zero-copy CSV has no data rows.' }
    $ortStart = (Convert-ToFiniteDouble $ortRowsAll[0].realtimeSeconds) + [double]$thresholds.execution.analysisTrimStartSeconds
    $ortEnd = (Convert-ToFiniteDouble $ortRowsAll[-1].realtimeSeconds) - [double]$thresholds.execution.analysisTrimEndSeconds
    $ortRows = @($ortRowsAll | Where-Object {
        $t = Convert-ToFiniteDouble $_.realtimeSeconds
        $null -ne $t -and $t -ge $ortStart -and $t -le $ortEnd
    })
    if ($ortRows.Count -lt 2) { throw 'ORT trim window contains fewer than two rows.' }
    $ortSha = Get-Sha256 $ortArtifact.path

    foreach ($definition in @(
        @{ Name = 'ort.inferenceMs'; Column = 'ortInferenceMs'; Unit = 'ms' },
        @{ Name = 'ort.bridgeToDoneMs'; Column = 'bridgeToDoneMs'; Unit = 'ms' },
        @{ Name = 'ort.sourceToObservedMs'; Column = 'sourceToOrtObservedMs'; Unit = 'ms' },
        @{ Name = 'parity.landmarkMaeNormalized'; Column = 'landmarkMaeNormalized'; Unit = 'normalized' },
        @{ Name = 'parity.landmarkMaxAbsRaw'; Column = 'landmarkMaxAbsRaw'; Unit = 'raw' },
        @{ Name = 'parity.presenceAbsDiff'; Column = 'presenceAbsDiff'; Unit = 'probability' }
    )) {
        $values = @(Get-NumericValues $ortRows $definition.Column)
        $metrics[$definition.Name] = New-Metric $definition.Unit $values (Get-Statistics $values) $ortSha @($definition.Column) 'distribution after trim'
    }

    $ortElapsed = (Convert-ToFiniteDouble $ortRows[-1].realtimeSeconds) - (Convert-ToFiniteDouble $ortRows[0].realtimeSeconds)
    $completedDelta = Get-Delta $ortRows 'nativeCompleted'
    $ortHz = $completedDelta / $ortElapsed
    $metrics['ort.effectiveHz'] = New-Metric 'Hz' @([double]$ortHz) ([ordered]@{ value = [double]$ortHz }) $ortSha @('realtimeSeconds', 'nativeCompleted') 'delta(nativeCompleted)/delta(realtimeSeconds) after trim'

    foreach ($definition in @(
        @{ Name = 'ort.failureCountDelta'; Column = 'nativeFailures' },
        @{ Name = 'ort.skippedCountDelta'; Column = 'nativeSkipped' },
        @{ Name = 'ort.readyReplacementDelta'; Column = 'nativeReadyReplacements' }
    )) {
        $delta = Get-Delta $ortRows $definition.Column
        $metrics[$definition.Name] = New-Metric 'count' @([double]$delta) ([ordered]@{ value = [double]$delta }) $ortSha @($definition.Column) 'last-first after trim'
    }

    $qpcValues = @(Get-NumericValues $frameRows 'nativeQpcFrequency' | Sort-Object -Unique)
    if ($qpcValues.Count -ne 1 -or [long]$qpcValues[0] -ne [Diagnostics.Stopwatch]::Frequency) {
        throw "Host tick frequency mismatch: frame=$($qpcValues -join ',') PowerShell=$([Diagnostics.Stopwatch]::Frequency)"
    }
    $advantage = [System.Collections.Generic.List[double]]::new()
    foreach ($row in $ortRows) {
        $sentisTicks = Convert-ToFiniteDouble $row.sentisArrivalHostTicks
        $ortTicks = Convert-ToFiniteDouble $row.ortObservedHostTicks
        if ($null -ne $sentisTicks -and $null -ne $ortTicks) {
            $advantage.Add((($sentisTicks - $ortTicks) * 1000.0) / [Diagnostics.Stopwatch]::Frequency)
        }
    }
    $advantageValues = $advantage.ToArray()
    $metrics['ort.advantageObservedMs'] = New-Metric 'ms' $advantageValues (Get-Statistics $advantageValues) $ortSha @('sentisArrivalHostTicks', 'ortObservedHostTicks') 'hostTicksDerived=(sentisArrivalHostTicks-ortObservedHostTicks)*1000/Stopwatch.Frequency; CSV ortMinusSentisObservedMs intentionally ignored'
}

$evaluations = [System.Collections.Generic.List[object]]::new()
foreach ($threshold in @($thresholds.absoluteThresholds | Where-Object { $_.cases -contains $caseResult.caseId })) {
    $metric = $metrics[[string]$threshold.metric]
    $actual = $null
    if ($null -ne $metric) {
        $property = $metric.statistics.PSObject.Properties[[string]$threshold.statistic]
        if ($null -eq $property -and $metric.statistics -is [System.Collections.IDictionary]) {
            $actual = $metric.statistics[[string]$threshold.statistic]
        }
        elseif ($null -ne $property) {
            $actual = $property.Value
        }
    }
    $passed = $null -ne $actual -and (Test-Comparison ([double]$actual) ([string]$threshold.operator) ([double]$threshold.value))
    $status = if ($passed) { 'PASS' } else { 'FAIL' }
    $evaluations.Add([ordered]@{
        id = [string]$threshold.id
        metric = [string]$threshold.metric
        statistic = [string]$threshold.statistic
        operator = [string]$threshold.operator
        expected = [double]$threshold.value
        actual = if ($null -ne $actual) { [double]$actual } else { $null }
        unit = [string]$threshold.unit
        status = $status
    })
    if (-not $passed -and $null -ne $metric) { $metric.status = 'FAIL' }
}

$analysis = [ordered]@{
    schemaVersion = '1.0.0'
    caseId = [string]$caseResult.caseId
    token = [string]$caseResult.token
    status = if (@($evaluations | Where-Object status -eq 'FAIL').Count -eq 0) { 'PASS' } else { 'FAIL' }
    analyzedUtc = [DateTime]::UtcNow.ToString('O')
    trim = [ordered]@{ startSeconds = [double]$thresholds.execution.analysisTrimStartSeconds; endSeconds = [double]$thresholds.execution.analysisTrimEndSeconds }
    sourceRowCounts = [ordered]@{ frameComparison = $frameRowsAll.Count; frameComparisonTrimmed = $frameRows.Count; gpu = $gpuRows.Count }
    metrics = $metrics
    evaluations = $evaluations.ToArray()
}
$analysis | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output $OutputPath
if ($analysis.status -eq 'FAIL') { exit 2 }
