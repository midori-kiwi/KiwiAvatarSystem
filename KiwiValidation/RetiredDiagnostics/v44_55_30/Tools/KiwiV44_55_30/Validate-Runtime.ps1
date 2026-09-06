param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'V30.Common.psm1') -Force
Assert-PS51
$EvidenceRoot=[IO.Path]::GetFullPath($EvidenceRoot)
Assert-EvidenceManifest $EvidenceRoot
$buildFile=Join-Path $EvidenceRoot 'build_identity.json'
$build=Get-Content -LiteralPath $buildFile -Raw -Encoding UTF8 | ConvertFrom-Json
if($build.contract -ne 'KIWI_V44_55_30_BUILD' -or $build.exitCode -ne 0 -or @($build.source).Count -lt 40 -or @($build.payload).Count -lt 10){throw 'Build identity incomplete'}
foreach($name in @('Assembly-CSharp.dll','Unity.InferenceEngine.dll','KiwiNativeCamera.dll','KiwiFaceLandmarkInference.onnx','face_landmarker_v2_with_blendshapes.bytes','KiwiAvatarSystem_v44_55_30.exe')){
    if(@($build.payload | Where-Object {[IO.Path]::GetFileName($_.path) -eq $name}).Count -ne 1){throw "Missing unique build authority $name"}
}
if(@($build.source | Where-Object path -like '*KiwiProducerTailSnapshotV44_55_30.compute').Count -ne 1){throw 'Compute shader identity missing'}
$arms=@{};$identities=@{}
foreach($arm in @('CONTROL','PROBE')){
    $dir=Join-Path $EvidenceRoot $arm
    $identity=Get-Content -LiteralPath (Join-Path $dir 'run_identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($identity.contract -ne 'KIWI_V44_55_30_RUN' -or $identity.arm -ne $arm -or $identity.exitCode -ne 0 -or $identity.timedOut){throw "$arm exit/identity failure"}
    if($identity.buildManifestSHA -ne (Get-Hash $buildFile)){throw 'Same build manifest mismatch'}
    $envMap=@{};foreach($p in $identity.environment.PSObject.Properties){$envMap[$p.Name]=[string]$p.Value}
    $probeFlag=if($arm -eq 'PROBE'){'1'}else{'0'}
    $required=@{
        KIWI_INFERENCE_ASYNC_COMPUTE_PROBE='1';KIWI_INFERENCE_COMMAND_BUFFER_GRAPHICS_PROBE='0';
        KIWI_V44_55_20_COMMON_TENSOR_AUDIT='1';KIWI_V44_55_20_COMMON_TENSOR_SECONDS='120';
        KIWI_V44_55_20_COMMON_TENSOR_HZ='1';KIWI_V44_55_20_STABLE_SECONDS='8';
        KIWI_V44_55_24_PAIR_BOUND_SHADOW_OUTPUT_AUDIT='1';KIWI_V44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE='1';
        KIWI_V44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUDIT='1';KIWI_V44_55_30_COLLECTION='1';
        KIWI_V44_55_30_PRODUCER_TAIL_SNAPSHOT=$probeFlag
    }
    foreach($key in $required.Keys){if($envMap[$key] -ne $required[$key]){throw "$arm env $key"}}
    if($envMap.Count -ne $required.Count+1 -or -not $envMap.ContainsKey('KIWI_V44_55_30_EVIDENCE_DIR')){throw 'Unexpected run environment'}
    $identities[$arm]=$envMap
    $log=Get-Content -LiteralPath (Join-Path $dir 'Player.log') -Raw -Encoding UTF8
    if(-not $log.Contains('[KiwiV30] START probe='+$probeFlag) -or -not $log.Contains('[KiwiV30] COMPLETE') -or
        -not $log.Contains('COMMAND_BUFFER_ASYNC')){throw "$arm startup/completion/mode not observed"}
    if($log -match '\[KiwiV30\] FAULT|Shader error|error CS\d+'){throw "$arm runtime error"}
    $summary=Read-Kv (Join-Path $dir 'producer_summary.txt')
    if($summary['status'] -ne 'COMPLETE' -or $summary['probe'] -ne $probeFlag -or $summary['performanceAuthority'] -ne '0'){throw "$arm observer incomplete"}
    foreach($key in @('observerFault','identityMismatch','duplicate','outOfOrder','missingPair','headerErrors','hasError','partialCapture','pendingAtCompletion')){
        if(-not $summary.ContainsKey($key) -or $summary[$key] -ne '0'){throw "$arm integrity $key"}
    }
    if($summary['issuedRequests'] -ne $summary['completedRequests']){throw 'Unresolved request count'}
    if($arm -eq 'CONTROL' -and ($summary['copyCount'] -ne '0' -or $summary['issuedRequests'] -ne '0')){throw 'CONTROL emitted GPU observer work'}
    $texts=@(Get-ChildItem -LiteralPath $dir -File -Filter 'KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_*.txt')
    $csvs=@(Get-ChildItem -LiteralPath $dir -File -Filter 'KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_*.csv')
    if($texts.Count -ne 1 -or $csvs.Count -ne 1){throw "$arm v27 artifact cardinality"}
    $v27=Read-Kv $texts[0].FullName
    if($v27['status'] -ne 'COMPLETE'){throw "$arm v27 not complete"}
    foreach($key in @('observerFaultCount','identityMismatchCount','duplicateDecodePayloadCount','duplicateShadowPayloadCount','arrayAliasCount','fieldMappingMismatchCount','payloadShapeMismatchCount','nonFinitePayloadCount','partialCaptureCount','pendingAtCompletion','noDecodePayloadCoverageCount')){
        if(-not $v27.ContainsKey($key) -or $v27[$key] -ne '0'){throw "$arm v27 $key"}
    }
    $v27Rows=@(Import-Csv -LiteralPath $csvs[0].FullName)
    $byIndex=@{};foreach($v in $v27Rows){if($byIndex.ContainsKey($v.recordIndex)){throw 'Duplicate v27 index'};$byIndex[$v.recordIndex]=$v}
    $rows=@(Get-Content -LiteralPath (Join-Path $dir 'producer_pairs.jsonl') -Encoding UTF8 | ForEach-Object {$_ | ConvertFrom-Json})
    if($rows.Count -lt 60 -or $rows.Count -gt 192 -or $rows.Count -ne $v27Rows.Count -or $rows.Count -ne [int]$summary['finalPairs'] -or $rows.Count -ne [int]$summary['eligiblePairs'] -or $rows.Count -ne [int]$v27['payloadEligiblePairCount']){throw "$arm coverage/cardinality"}
    $seen=@{};$tokens=@{};$pairTokens=@{};$sources=@{};$hist=@{};$decisions=@{}
    foreach($row in $rows){
        $key=[string]$row.recordIndex
        if($seen.ContainsKey($key)){throw 'Duplicate raw record'};$seen[$key]=$true
        if($pairTokens.ContainsKey([string]$row.pairToken) -or $sources.ContainsKey([string]$row.sourceHostTicks)){throw 'Duplicate pair token/source'}
        $pairTokens[[string]$row.pairToken]=$true;$sources[[string]$row.sourceHostTicks]=$true
        if(-not $byIndex.ContainsKey($key) -or -not $row.valid -or -not $row.v27Eligible){throw 'Invalid pair'}
        $v=$byIndex[$key]
        if($v.payloadEligible -ne '1'){throw 'v27 pair ineligible'}
        foreach($field in @('pairToken','sequence','laneIndex','sourceHostTicks','scheduleBeginHostTicks','startedHostTicks','readbackRequestHostTicks','readbackRequestFrame','cameraGeneration','trackingSessionGeneration','trackerGeneration','anchorRevision','externalAnchorEpoch','minimumPresenceBits','laneIdentityToken','workerIdentityToken','pendingOutputIdentityToken')){
            if([string]$row.$field -ne [string]$v.$field){throw "Pair identity $field"}
        }
        if(@($row.cropMatrixBits).Count -ne 16 -or $row.logicalWords -ne 1405 -or [long]$row.sourceHostTicks -le 0 -or [long]$row.scheduleBeginHostTicks -le 0){throw 'Logical/source identity'}
        # v27 serializes matrix as pipe-separated X8 hexadecimal bit words.
        $matrix=@(([string]$v.cropMatrixBits).Split('|') | ForEach-Object {[Convert]::ToInt32($_,16)})
        if(-not (Test-WordEqual $row.cropMatrixBits $matrix)){throw 'Crop identity'}
        foreach($payload in @(@{v=$row.actual},@{v=$row.reference},@{v=$row.mode2})){
            if(@($payload.v).Count -ne 1405){throw 'Partial raw payload'}
            foreach($word in $payload.v){if([long]$word -lt [int]::MinValue -or [long]$word -gt [int]::MaxValue){throw 'Non-int32 payload'}}
        }
        $actual=Get-PayloadClass $row.actual $row.reference $row.mode2 'ACTUAL_EQUALS_'
        if($actual -ne $row.actualClassification -or $actual -ne $v.classification){throw 'Actual independent parity mismatch'}
        if(-not $hist.ContainsKey($actual)){$hist[$actual]=0};$hist[$actual]++
        if($arm -eq 'PROBE'){
            foreach($word in $row.snapshot){if([long]$word -lt [int]::MinValue -or [long]$word -gt [int]::MaxValue){throw 'Non-int32 snapshot'}}
            if(@($row.gpuHeader).Count -ne 8 -or @($row.snapshot).Count -ne 1405 -or $row.observerToken -le 0){throw 'GPU payload/header size'}
            if($tokens.ContainsKey([string]$row.observerToken)){throw 'Duplicate GPU token'};$tokens[[string]$row.observerToken]=$true
            $tickBytes=[BitConverter]::GetBytes([long]$row.sourceHostTicks)
            $expectedHeader=@(0x4B333050,30,[int]$row.observerToken,[int]$row.laneIndex,1405,[BitConverter]::ToInt32($tickBytes,0),[BitConverter]::ToInt32($tickBytes,4),([int]$row.observerToken -bxor 0xA55A30))
            if(-not (Test-WordEqual $row.gpuHeader $expectedHeader)){throw 'GPU header/token identity'}
            $producer=Get-PayloadClass $row.snapshot $row.reference $row.mode2 'PRODUCER_SNAPSHOT_EQUALS_'
            $decision=Get-PrimaryDecision $row.snapshot $row.actual $row.reference $row.mode2
            if($producer -ne $row.producerClassification -or $decision -ne $row.decision -or
                (Test-WordEqual $row.snapshot $row.actual) -ne [bool]$row.snapshotEqualsActual){throw 'Producer independent parity/decision mismatch'}
            if(-not $decisions.ContainsKey($decision)){$decisions[$decision]=0};$decisions[$decision]++
        } elseif($row.producerClassification -ne 'OBSERVER_OFF'){throw 'Control snapshot unexpectedly classified'}
    }
    $laneLast=@{}
    foreach($row in ($rows | Sort-Object scheduleBeginHostTicks)){
        $lane=[string]$row.laneIndex
        if($laneLast.ContainsKey($lane) -and [long]$row.sourceHostTicks -le $laneLast[$lane]){throw 'Out-of-order lane source'}
        $laneLast[$lane]=[long]$row.sourceHostTicks
    }
    if([long]$summary['elapsedMs'] -le 0 -or [long]$summary['renderFrames'] -le 0){throw 'Gross contamination telemetry missing'}
    $arms[$arm]=@{count=$rows.Count;hist=$hist;decisions=$decisions;diagnosticFps=([double]$summary['renderFrames']*1000/[double]$summary['elapsedMs'])}
}
foreach($key in $identities.CONTROL.Keys){
    if($key -in @('KIWI_V44_55_30_PRODUCER_TAIL_SNAPSHOT','KIWI_V44_55_30_EVIDENCE_DIR')){continue}
    if($identities.CONTROL[$key] -ne $identities.PROBE[$key]){throw "Confounded environment $key"}
}
$contaminated=$false
$diagnosticFpsRatio=$arms.PROBE.diagnosticFps/$arms.CONTROL.diagnosticFps
if($diagnosticFpsRatio -lt 0.5 -or $diagnosticFpsRatio -gt 2.0){$contaminated=$true}
foreach($suffix in @('REF_ONLY','MODE2_ONLY','BOTH','NEITHER')){
    $key='ACTUAL_EQUALS_'+$suffix
    $c=0.0;$p=0.0
    if($arms.CONTROL.hist.ContainsKey($key)){$c=$arms.CONTROL.hist[$key]/[double]$arms.CONTROL.count}
    if($arms.PROBE.hist.ContainsKey($key)){$p=$arms.PROBE.hist[$key]/[double]$arms.PROBE.count}
    if([Math]::Abs($p-$c) -gt 0.10){$contaminated=$true}
}
$baseline=0.0
if($arms.CONTROL.hist.ContainsKey('ACTUAL_EQUALS_NEITHER')){$baseline=$arms.CONTROL.hist['ACTUAL_EQUALS_NEITHER']/[double]$arms.CONTROL.count}
$decision=if($contaminated){'OBSERVER_CONTAMINATED'}elseif($baseline -lt 0.95){'INCONCLUSIVE_BASELINE_NOT_REPRODUCED'}elseif($arms.PROBE.decisions.Count -eq 1){[string]@($arms.PROBE.decisions.Keys)[0]}else{'MIXED_COARSE_OUTCOMES_REQUIRES_NARROWER_DIAGNOSTIC'}
[pscustomobject]@{status='EVIDENCE_INTEGRITY_VALID';decision=$decision;controlPairs=$arms.CONTROL.count;probePairs=$arms.PROBE.count;performanceAuthority=0;contaminationAbsoluteFractionThreshold=0.10;controlNeitherMinimumFraction=0.95;scope='COARSE_OUTPUT_OBSERVATION_ENDPOINT_ONLY'} | ConvertTo-Json
if($contaminated -or $baseline -lt 0.95){throw $decision}
