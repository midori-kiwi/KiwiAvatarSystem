param([string]$ProjectRoot='D:\KiwiAvatarSystem',[ValidateRange(285,600)][int]$TimeoutSeconds=300)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'V31.Common.psm1') -Force
Assert-PS51
Assert-Protected $ProjectRoot
$manifestPath=Join-Path $ProjectRoot 'KiwiValidation/v44_55_31_build_identity.json'
$build=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if($build.contract -ne 'KIWI_V44_55_31_BUILD' -or $build.exitCode -ne 0){throw 'Unverified build'}
Assert-IdentityRows $ProjectRoot $build.source
Assert-IdentityRows $ProjectRoot $build.payload
$exe=Join-Path $ProjectRoot 'Builds/v44_55_31AsyncProducerTailSnapshot/KiwiAvatarSystem_v44_55_31.exe'
if(@(Get-Process KiwiAvatarSystem_v44_55_31 -ErrorAction SilentlyContinue).Count -ne 0){throw 'Existing Player'}
$stamp=(Get-Date -Format 'yyyyMMdd_HHmmss')+'_'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$root=Assert-UnderRoot (Join-Path $ProjectRoot ('KiwiValidation/RuntimeEvidence_v44_55_31_'+$stamp)) $ProjectRoot
if(Test-Path -LiteralPath $root){throw 'Evidence target exists'}
[void][IO.Directory]::CreateDirectory($root)
[IO.File]::Copy($manifestPath,(Join-Path $root 'build_identity.json'),$false)
$persistent=Join-Path $env:USERPROFILE 'AppData/LocalLow/MidoriKiwi/KiwiAvatarSystem/KiwiFrameBottleneck'
$collectError=$null
try {
    foreach($arm in @('CONTROL','PROBE')){
        Assert-IdentityRows $ProjectRoot $build.source
        Assert-IdentityRows $ProjectRoot $build.payload
        $armDir=Join-Path $root $arm
        [void][IO.Directory]::CreateDirectory($armDir)
        $previous=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        if(Test-Path -LiteralPath $persistent){foreach($f in Get-ChildItem -LiteralPath $persistent -File){[void]$previous.Add($f.FullName)}}
        $settings=[ordered]@{
            KIWI_INFERENCE_ASYNC_COMPUTE_PROBE='1'
            KIWI_INFERENCE_COMMAND_BUFFER_GRAPHICS_PROBE='0'
            KIWI_V44_55_20_COMMON_TENSOR_AUDIT='1'
            KIWI_V44_55_20_COMMON_TENSOR_SECONDS='120'
            KIWI_V44_55_20_COMMON_TENSOR_HZ='1'
            KIWI_V44_55_20_STABLE_SECONDS='8'
            KIWI_V44_55_24_PAIR_BOUND_SHADOW_OUTPUT_AUDIT='1'
            KIWI_V44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE='1'
            KIWI_V44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUDIT='1'
            KIWI_V44_55_31_COLLECTION='1'
            KIWI_V44_55_31_PRODUCER_TAIL_SNAPSHOT= $(if($arm -eq 'PROBE'){'1'}else{'0'})
            KIWI_V44_55_31_EVIDENCE_DIR=$armDir
        }
        $psi=[Diagnostics.ProcessStartInfo]::new()
        $psi.FileName=$exe
        $psi.WorkingDirectory=Split-Path -Parent $exe
        $psi.UseShellExecute=$false
        $psi.Arguments='-force-d3d12 -screen-width 1280 -screen-height 720 -logFile "'+(Join-Path $armDir 'Player.log')+'"'
        # Clear inherited Kiwi flags, so unrelated diagnostics cannot silently alter an arm.
        foreach($key in @($psi.EnvironmentVariables.Keys)){if([string]$key -like 'KIWI_*'){[void]$psi.EnvironmentVariables.Remove([string]$key)}}
        foreach($item in $settings.GetEnumerator()){$psi.EnvironmentVariables[$item.Key]=[string]$item.Value}
        $started=[DateTime]::UtcNow
        Write-Host "[V31] $arm : remain in camera view until the Player exits (up to 270 s)."
        $p=[Diagnostics.Process]::Start($psi)
        $timedOut=-not $p.WaitForExit($TimeoutSeconds*1000)
        if($timedOut){$p.Kill();$p.WaitForExit()}
        $exitCode=$p.ExitCode
        Write-JsonNew (Join-Path $armDir 'run_identity.json') ([pscustomobject]@{
            contract='KIWI_V44_55_31_RUN';arm=$arm;startedUtc=$started.ToString('O');endedUtc=[DateTime]::UtcNow.ToString('O')
            exitCode=$exitCode;timedOut=$timedOut;pid=$p.Id;buildManifestSHA=(Get-Hash $manifestPath);environment=$settings
        })
        if(Test-Path -LiteralPath $persistent){
            foreach($f in Get-ChildItem -LiteralPath $persistent -File){
                if($previous.Contains($f.FullName)){continue}
                if($f.Name -notmatch '^Kiwi(CommonTensorBackendStageIsolation_v44_55_20_|PairBoundShadowOutputSnapshot_v44_55_24_|ProductionScheduleTransactionTrace_v44_55_25_|ActualProductionVsShadowPayloadAuthority_v44_55_27_)'){continue}
                if($f.LastWriteTimeUtc -lt $started.AddSeconds(-1)){throw 'Evidence freshness mismatch'}
                $dest=Assert-UnderRoot (Join-Path $armDir $f.Name) $root
                [IO.File]::Copy($f.FullName,$dest,$false)
                if((Get-Hash $f.FullName) -ne (Get-Hash $dest)){throw 'Evidence copy hash mismatch'}
            }
        }
        Assert-IdentityRows $ProjectRoot $build.source
        Assert-IdentityRows $ProjectRoot $build.payload
        if($timedOut -or $exitCode -ne 0){throw "$arm failed normal exit: $exitCode timeout=$timedOut"}
    }
} catch {
    $collectError=$_.Exception.Message
    Write-JsonNew (Join-Path $root 'collection_failure.json') ([pscustomobject]@{error=$collectError;status='INCONCLUSIVE'})
}
New-EvidenceManifest $root
Assert-EvidenceManifest $root
$zip=$root+'.zip'
Compress-Archive -LiteralPath $root -DestinationPath $zip -CompressionLevel Optimal
Write-Host "ONE_RUNTIME_ZIP=$zip"
Write-Host ("ZIP_SHA256="+(Get-Hash $zip))
if($null -ne $collectError){throw $collectError}
& (Join-Path $PSScriptRoot 'Validate-Runtime.ps1') -EvidenceRoot $root
