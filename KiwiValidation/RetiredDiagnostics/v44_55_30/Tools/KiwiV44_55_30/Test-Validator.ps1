param([string]$ProjectRoot='D:\KiwiAvatarSystem')
$ErrorActionPreference='Stop'; Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'V30.Common.psm1') -Force
Assert-PS51
$root=Assert-UnderRoot (Join-Path $ProjectRoot ('KiwiValidation/v44_55_30_validator_tests_'+[Guid]::NewGuid().ToString('N'))) $ProjectRoot
[void][IO.Directory]::CreateDirectory($root)
$passed=0
function Fixture([string]$Name,[string]$Outcome,[string]$Mutation) {
    $dir=Join-Path $root $Name;[void][IO.Directory]::CreateDirectory($dir)
    $source=@(); for($i=0;$i -lt 40;$i++){$source+=[pscustomobject]@{path=('synthetic'+$i);sha256=('0'*64)}}
    $source[0].path='Assets/KiwiProducerTailSnapshotV44_55_30.compute'
    $payload=@();foreach($name in @('Assembly-CSharp.dll','Unity.InferenceEngine.dll','KiwiNativeCamera.dll','KiwiFaceLandmarkInference.onnx','face_landmarker_v2_with_blendshapes.bytes','KiwiAvatarSystem_v44_55_30.exe','extra1','extra2','extra3','extra4')){$payload+=[pscustomobject]@{path=$name;sha256=('0'*64)}}
    Write-JsonNew (Join-Path $dir 'build_identity.json') ([pscustomobject]@{contract='KIWI_V44_55_30_BUILD';exitCode=0;source=$source;payload=$payload;synthetic=$true})
    foreach($arm in @('CONTROL','PROBE')){
        $ad=Join-Path $dir $arm;[void][IO.Directory]::CreateDirectory($ad)
        $flag=if($arm -eq 'PROBE'){'1'}else{'0'}
        $envs=[ordered]@{
            KIWI_INFERENCE_ASYNC_COMPUTE_PROBE='1';KIWI_INFERENCE_COMMAND_BUFFER_GRAPHICS_PROBE='0';
            KIWI_V44_55_20_COMMON_TENSOR_AUDIT='1';KIWI_V44_55_20_COMMON_TENSOR_SECONDS='120';
            KIWI_V44_55_20_COMMON_TENSOR_HZ='1';KIWI_V44_55_20_STABLE_SECONDS='8';
            KIWI_V44_55_24_PAIR_BOUND_SHADOW_OUTPUT_AUDIT='1';KIWI_V44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE='1';
            KIWI_V44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUDIT='1';KIWI_V44_55_30_COLLECTION='1';
            KIWI_V44_55_30_PRODUCER_TAIL_SNAPSHOT=$flag;KIWI_V44_55_30_EVIDENCE_DIR=$ad
        }
        if($Mutation -eq 'ENV' -and $arm -eq 'PROBE'){$envs.KIWI_V44_55_20_COMMON_TENSOR_HZ='2'}
        Write-JsonNew (Join-Path $ad 'run_identity.json') ([pscustomobject]@{contract='KIWI_V44_55_30_RUN';arm=$arm;exitCode=0;timedOut=$false;buildManifestSHA=(Get-Hash (Join-Path $dir 'build_identity.json'));environment=$envs})
        [IO.File]::WriteAllText((Join-Path $ad 'Player.log'),("[KiwiV30] START probe="+$flag+"`nCOMMAND_BUFFER_ASYNC`n[KiwiV30] COMPLETE"))
        $summary=[Collections.Generic.List[string]]::new()
        $summary.Add('renderFrames=3600');$summary.Add('elapsedMs=120000')
        foreach($s in @('status=COMPLETE','performanceAuthority=0','finalPairs=60','eligiblePairs=60',('probe='+$flag),'observerFault=0','identityMismatch=0','duplicate=0','outOfOrder=0','missingPair=0','headerErrors=0','hasError=0','partialCapture=0','pendingAtCompletion=0',('copyCount='+(60*[int]$flag)),('issuedRequests='+(60*[int]$flag)),('completedRequests='+(60*[int]$flag)))){$summary.Add($s)}
        [IO.File]::WriteAllLines((Join-Path $ad 'producer_summary.txt'),$summary.ToArray())
        $v27=[Collections.Generic.List[string]]::new()
        $v27.Add('status=COMPLETE');$v27.Add('payloadEligiblePairCount=60')
        foreach($key in @('observerFaultCount','identityMismatchCount','duplicateDecodePayloadCount','duplicateShadowPayloadCount','arrayAliasCount','fieldMappingMismatchCount','payloadShapeMismatchCount','nonFinitePayloadCount','partialCaptureCount','pendingAtCompletion','noDecodePayloadCoverageCount')){$v27.Add($key+'=0')}
        [IO.File]::WriteAllLines((Join-Path $ad 'KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_SYNTHETIC.txt'),$v27.ToArray())
        $raw=[Collections.Generic.List[string]]::new();$csv=[Collections.Generic.List[object]]::new()
        for($i=0;$i -lt 60;$i++){
            $ref=[int[]]::new(1405);$mode=[int[]]::new(1405);$actual=[int[]]::new(1405)
            for($j=0;$j -lt 1405;$j++){$mode[$j]=2;$actual[$j]=3}
            $snapshot=if($Outcome -eq 'E2'){[int[]]$actual.Clone()}elseif($Outcome -eq 'E3'){[int[]]$mode.Clone()}else{[int[]]$ref.Clone()}
            if($Outcome -eq 'E5' -and $arm -eq 'PROBE'){$actual=[int[]]$ref.Clone()}
            $row=[ordered]@{
                recordIndex=$i;pairToken=$i+1;observerToken=$i+100;sequence=[string]($i+1000);laneIndex=($i%3);
                logicalWords=1405;sourceHostTicks=[long](1000000+$i);scheduleBeginHostTicks=[long](2000000+$i);
                startedHostTicks=[long](3000000+$i);readbackRequestHostTicks=[long](4000000+$i);readbackRequestFrame=$i;
                cameraGeneration=1;trackingSessionGeneration=1;trackerGeneration=1;anchorRevision=1;externalAnchorEpoch=1;
                minimumPresenceBits=0;laneIdentityToken=1;workerIdentityToken=2;pendingOutputIdentityToken=3;
                cropMatrixBits=[int[]]::new(16);gpuHeader=@(0x4B333050,30,($i+100),($i%3),1405,(1000000+$i),0,(($i+100) -bxor 0xA55A30));
                snapshot=$snapshot;actual=$actual;reference=$ref;mode2=$mode;
                v27Eligible=$true;valid=$true;snapshotEqualsActual=(Test-WordEqual $snapshot $actual);
                actualClassification=(Get-PayloadClass $actual $ref $mode 'ACTUAL_EQUALS_');
                producerClassification=(Get-PayloadClass $snapshot $ref $mode 'PRODUCER_SNAPSHOT_EQUALS_');
                decision=(Get-PrimaryDecision $snapshot $actual $ref $mode)
            }
            if($arm -eq 'CONTROL'){$row.producerClassification='OBSERVER_OFF';$row.snapshot=$null;$row.gpuHeader=$null}
            $v=[ordered]@{}
            foreach($key in @('recordIndex','pairToken','sequence','laneIndex','sourceHostTicks','scheduleBeginHostTicks','startedHostTicks','readbackRequestHostTicks','readbackRequestFrame','cameraGeneration','trackingSessionGeneration','trackerGeneration','anchorRevision','externalAnchorEpoch','minimumPresenceBits','laneIdentityToken','workerIdentityToken','pendingOutputIdentityToken')){$v[$key]=$row[$key]}
            $v.cropMatrixBits=([string[]](@('00000000')*16) -join '|');$v.payloadEligible='1';$v.classification=$row.actualClassification
            $csv.Add([pscustomobject]$v)
            if($arm -eq 'PROBE' -and $i -eq 0){
                if($Mutation -eq 'TOKEN'){$row.gpuHeader[2]=999999}
                if($Mutation -eq 'SOURCE'){$row.sourceHostTicks++}
                if($Mutation -eq 'BITS'){$row.snapshot[1404]=99}
                if($Mutation -eq 'SIZE'){$row.snapshot=@(1,2)}
            }
            $raw.Add(($row | ConvertTo-Json -Depth 8 -Compress))
        }
        [IO.File]::WriteAllLines((Join-Path $ad 'producer_pairs.jsonl'),$raw.ToArray(),[Text.UTF8Encoding]::new($false))
        $csv.ToArray() | Export-Csv -LiteralPath (Join-Path $ad 'KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_SYNTHETIC.csv') -NoTypeInformation
    }
    New-EvidenceManifest $dir
    return $dir
}
$cases=@(
    @{name='positive_e1';out='E1';mutation='';expect='DIVERGENCE_AFTER_PRODUCER_TAIL_SUPPORTED'},
    @{name='positive_e2';out='E2';mutation='';expect='DIVERGENCE_PRESENT_BY_PRODUCER_TAIL_OR_COMMON_OBSERVATION_PATH'},
    @{name='positive_e3';out='E3';mutation='';expect='OUTPUT_OBSERVATION_PATH_DISAGREEMENT_CONFIRMED'},
    @{name='negative_token';out='E1';mutation='TOKEN';error='GPU header/token identity'},
    @{name='negative_source';out='E1';mutation='SOURCE';error='Pair identity sourceHostTicks'},
    @{name='negative_bits';out='E1';mutation='BITS';error='Producer independent parity/decision mismatch'},
    @{name='negative_size';out='E1';mutation='SIZE';error='GPU payload/header size'},
    @{name='negative_env';out='E1';mutation='ENV';error='PROBE env'},
    @{name='contaminated_e5';out='E5';mutation='';error='OBSERVER_CONTAMINATED'}
)
foreach($case in $cases){
    $dir=Fixture $case.name $case.out $case.mutation
    $caught=$null;$result=$null
    try{$result=(& (Join-Path $PSScriptRoot 'Validate-Runtime.ps1') -EvidenceRoot $dir) | ConvertFrom-Json}catch{$caught=$_.Exception.Message}
    if($case.ContainsKey('expect')){if($caught -or $result.decision -ne $case.expect){throw "Positive test failed $($case.name): $caught"}}
    elseif(-not $caught -or -not $caught.Contains($case.error)){throw "Negative test failed $($case.name): $caught"}
    $passed++;Write-Host ("SYNTHETIC_PASS="+$case.name)
}
Write-JsonNew (Join-Path $root 'test_summary.json') ([pscustomobject]@{synthetic=$true;tests=$passed;runtimeAuthority=$false;status='PASS'})
Write-Host "VALIDATOR_SELFTEST_PASS=$passed/9 fixtures=$root"
