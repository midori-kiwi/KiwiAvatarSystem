param(
    [Parameter(Mandatory=$true)]
    [string]$EvidenceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -lt 1) {
    throw "Windows PowerShell 5.1 is required. Current=$($PSVersionTable.PSVersion)"
}

if (-not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)) {
    throw "EvidenceRoot not found: $EvidenceRoot"
}

function Read-KeyValueReport([string]$Path) {
    $map = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        $index = $line.IndexOf('=')
        if ($index -le 0) { continue }
        $key = $line.Substring(0, $index).Trim()
        $value = $line.Substring($index + 1).Trim()
        if (-not $map.ContainsKey($key)) {
            $map[$key] = $value
        }
    }
    return $map
}

function Require-Zero([hashtable]$Map, [string]$Key, [string]$Arm) {
    if (-not $Map.ContainsKey($Key)) {
        throw "$Arm report is missing $Key"
    }
    if ([int64]$Map[$Key] -ne 0) {
        throw "$Arm observer integrity failure: $Key=$($Map[$Key])"
    }
}

function Analyze-Arm([string]$Arm) {
    $dir = Join-Path $EvidenceRoot $Arm
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        throw "Missing arm directory: $dir"
    }

    $txt = @(Get-ChildItem -LiteralPath $dir -Filter 'KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_*.txt' -File)
    $csv = @(Get-ChildItem -LiteralPath $dir -Filter 'KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_*.csv' -File)
    $run = Join-Path $dir 'run_identity.txt'
    $log = Join-Path $dir 'Player.log'

    if ($txt.Count -ne 1 -or $csv.Count -ne 1) {
        throw "$Arm requires exactly one v27 TXT+CSV. TXT=$($txt.Count) CSV=$($csv.Count)"
    }
    if (-not (Test-Path -LiteralPath $run -PathType Leaf)) {
        throw "$Arm run_identity.txt missing"
    }
    if (-not (Test-Path -LiteralPath $log -PathType Leaf)) {
        throw "$Arm Player.log missing"
    }

    $report = Read-KeyValueReport $txt[0].FullName
    $identity = Read-KeyValueReport $run

    if ($report['status'] -ne 'COMPLETE') {
        throw "$Arm v27 status is not COMPLETE: $($report['status'])"
    }
    if ([int]$identity['exitCode'] -ne 0) {
        throw "$Arm did not exit normally: exitCode=$($identity['exitCode'])"
    }

    foreach ($key in @(
        'observerFaultCount',
        'identityMismatchCount',
        'duplicateDecodePayloadCount',
        'duplicateShadowPayloadCount',
        'arrayAliasCount',
        'fieldMappingMismatchCount',
        'payloadShapeMismatchCount',
        'nonFinitePayloadCount',
        'partialCaptureCount',
        'pendingAtCompletion'
    )) {
        Require-Zero $report $key $Arm
    }

    $eligible = [int]$report['payloadEligiblePairCount']
    $anomalousEligible = [int]$report['anomalousPayloadEligiblePairCount']
    $dependency = [int]$report['dependencyTraceCount']
    $completed = [int]$report['completedPairCount']

    if ($eligible -lt 60) {
        throw "$Arm payload eligible count below fixed gate: $eligible"
    }
    if ($anomalousEligible -lt 3) {
        throw "$Arm anomalous eligible count below fixed gate: $anomalousEligible"
    }
    if ($dependency -ne $completed) {
        throw "$Arm dependency/completed mismatch: $dependency/$completed"
    }

    $rows = @(Import-Csv -LiteralPath $csv[0].FullName |
        Where-Object { $_.payloadEligible -eq '1' })

    if ($rows.Count -ne $eligible) {
        throw "$Arm CSV/report eligible mismatch: CSV=$($rows.Count) report=$eligible"
    }

    $refOnly = @($rows | Where-Object { $_.classification -eq 'ACTUAL_EQUALS_REF_ONLY' }).Count
    $mode2Only = @($rows | Where-Object { $_.classification -eq 'ACTUAL_EQUALS_MODE2_ONLY' }).Count
    $both = @($rows | Where-Object { $_.classification -eq 'ACTUAL_EQUALS_BOTH' }).Count
    $neither = @($rows | Where-Object { $_.classification -eq 'ACTUAL_EQUALS_NEITHER' }).Count

    if (($refOnly + $mode2Only + $both + $neither) -ne $eligible) {
        throw "$Arm contains unexpected eligible classification values."
    }

    $playerText = [System.IO.File]::ReadAllText($log)
    $expectedModeToken = 'mode=' + $Arm
    if ($playerText.IndexOf($expectedModeToken, [System.StringComparison]::Ordinal) -lt 0) {
        throw "$Arm Player.log lacks v44.55.29 mode identity token: $expectedModeToken"
    }

    return New-Object PSObject -Property @{
        Arm = $Arm
        ExeSha = $identity['EXE.sha256']
        Eligible = $eligible
        AnomalousEligible = $anomalousEligible
        RefOnly = $refOnly
        Mode2Only = $mode2Only
        Both = $both
        Neither = $neither
        V27Decision = $report['decision']
        Txt = $txt[0].FullName
        Csv = $csv[0].FullName
    }
}

$direct = Analyze-Arm 'DIRECT_GRAPHICS'
$cbGraphics = Analyze-Arm 'COMMAND_BUFFER_GRAPHICS'
$cbAsync = Analyze-Arm 'COMMAND_BUFFER_ASYNC'

if ($direct.ExeSha -ne $cbGraphics.ExeSha -or $direct.ExeSha -ne $cbAsync.ExeSha) {
    throw 'Same-build gate failed: EXE SHA differs between arms.'
}

$decision = 'UNRESOLVED_EXECUTION_CONTEXT_ISOLATION'
$commandBufferForm = 'UNRESOLVED'
$asyncQueue = 'UNRESOLVED'

$directRefExact = $direct.RefOnly -eq $direct.Eligible
$cbGraphicsRefExact = $cbGraphics.RefOnly -eq $cbGraphics.Eligible
$cbGraphicsNeither = $cbGraphics.Neither -eq $cbGraphics.Eligible
$cbAsyncNeither = $cbAsync.Neither -eq $cbAsync.Eligible
$cbAsyncRefExact = $cbAsync.RefOnly -eq $cbAsync.Eligible

if (-not $directRefExact) {
    $decision = 'BASELINE_NOT_REPRODUCED'
}
elseif ($cbGraphicsRefExact -and $cbAsyncNeither) {
    $decision = 'ASYNC_QUEUE_EXECUTION_CONTEXT_CAUSAL_CONTRIBUTOR_CANDIDATE'
    $commandBufferForm = 'REJECTED_AS_PRIMARY_CAUSE_FOR_THIS_SAME_BUILD_AB'
    $asyncQueue = 'SUPPORTED_AS_CAUSAL_CONTRIBUTOR_CANDIDATE'
}
elseif ($cbGraphicsNeither -and $cbAsyncNeither) {
    $decision = 'COMMAND_BUFFER_EXECUTION_FORM_CAUSAL_CONTRIBUTOR_CANDIDATE'
    $commandBufferForm = 'SUPPORTED_AS_CAUSAL_CONTRIBUTOR_CANDIDATE'
    $asyncQueue = 'NOT_ISOLATED'
}
elseif ($cbGraphicsRefExact -and $cbAsyncRefExact) {
    $decision = 'V28_ASYNC_PATTERN_NOT_REPRODUCED'
    $commandBufferForm = 'NOT_CAUSAL_IN_THIS_RUN'
    $asyncQueue = 'NOT_CAUSAL_IN_THIS_RUN'
}

$summaryPath = Join-Path $EvidenceRoot 'V44_55_29_SUMMARY.txt'
$summary = @(
    'KiwiAvatarSystem v44.55.29 CommandBuffer Graphics Execution-Form Isolation',
    'contract=KIWI_V44_55_29_COMMAND_BUFFER_GRAPHICS_EXECUTION_FORM_ISOLATION',
    ('validatedUtc=' + [DateTime]::UtcNow.ToString('O')),
    ('sameBuildExeSha256=' + $direct.ExeSha),
    ('decision=' + $decision),
    ('commandBufferExecutionForm=' + $commandBufferForm),
    ('asyncQueueExecutionContext=' + $asyncQueue),
    'performanceAuthority=0',
    'productionChangeAuthorized=0',
    '',
    '[DIRECT_GRAPHICS]',
    ('eligible=' + $direct.Eligible),
    ('anomalousEligible=' + $direct.AnomalousEligible),
    ('refOnly=' + $direct.RefOnly),
    ('mode2Only=' + $direct.Mode2Only),
    ('both=' + $direct.Both),
    ('neither=' + $direct.Neither),
    ('v27Decision=' + $direct.V27Decision),
    '',
    '[COMMAND_BUFFER_GRAPHICS]',
    ('eligible=' + $cbGraphics.Eligible),
    ('anomalousEligible=' + $cbGraphics.AnomalousEligible),
    ('refOnly=' + $cbGraphics.RefOnly),
    ('mode2Only=' + $cbGraphics.Mode2Only),
    ('both=' + $cbGraphics.Both),
    ('neither=' + $cbGraphics.Neither),
    ('v27Decision=' + $cbGraphics.V27Decision),
    '',
    '[COMMAND_BUFFER_ASYNC]',
    ('eligible=' + $cbAsync.Eligible),
    ('anomalousEligible=' + $cbAsync.AnomalousEligible),
    ('refOnly=' + $cbAsync.RefOnly),
    ('mode2Only=' + $cbAsync.Mode2Only),
    ('both=' + $cbAsync.Both),
    ('neither=' + $cbAsync.Neither),
    ('v27Decision=' + $cbAsync.V27Decision),
    '',
    '[SCOPE]',
    'claim=Actual Production decode payload execution-form/queue isolation only',
    'canonicalSemanticAuthority=0',
    'performanceAuthority=0',
    'visualAuthority=0',
    'lifecycleAuthority=normal-exit-only',
    'fenceOrWaitConclusion=NOT_PROVEN',
    'productionAsyncDisableConclusion=NOT_AUTHORIZED'
)
$summary | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host ''
Write-Host 'KIWI_V44_55_29_RUNTIME_VALIDATION_PASS' -ForegroundColor Green
Write-Host "Decision: $decision" -ForegroundColor Yellow
Write-Host "CommandBuffer form: $commandBufferForm"
Write-Host "Async queue context: $asyncQueue"
Write-Host "Summary: $summaryPath"
