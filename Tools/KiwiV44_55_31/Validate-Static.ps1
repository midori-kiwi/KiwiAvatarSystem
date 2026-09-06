param([string]$ProjectRoot='D:\KiwiAvatarSystem')
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'V31.Common.psm1') -Force
Assert-PS51
Assert-Protected $ProjectRoot
$backup=Join-Path $ProjectRoot 'KiwiValidation/Backups/v44_55_31_20260905'
$edits=@(
    @{file='Assets/Script/KiwiInferenceFaceTracker.cs';hash='B89005B71C3DD0A3BFFE67144B7C6B8F92ACCE6729FB0D67CB5DF7AD0D410E88';replace=@(
        @{from='KiwiAsyncProducerTailSnapshotV44_55_31.AfterPending(lane);';to='KiwiAsyncProducerTailSnapshotV44_55_30.AfterPending(lane);'},
        @{from='KiwiAsyncProducerTailSnapshotV44_55_31.Append(';to='KiwiAsyncProducerTailSnapshotV44_55_30.Append('})},
    @{file='Assets/KiwiAvatarSystem/Runtime/Validation/KiwiProductionScheduleTransactionTraceV44_55_25.cs';hash='F0786D4F6A26D87F0867663ED0630145C9E79CB27517C9E1A516E9BA7DBADF9F';replace=@(
        @{from='KiwiAsyncProducerTailSnapshotV44_55_31.BindTrace(state);';to='KiwiAsyncProducerTailSnapshotV44_55_30.BindTrace(state);'})},
    @{file='Assets/KiwiAvatarSystem/Runtime/Validation/KiwiActualProductionVsShadowPayloadAuthorityV44_55_27.cs';hash='03F3AAC36639A57D0DC2018BE9E256865F3E30A88F1631428C52EB159A497071';replace=@(
        @{from="        KiwiAsyncProducerTailSnapshotV44_55_31.CapturePayloadsBeforeRelease(pair);`n";to=''},
        @{from='KiwiAsyncProducerTailSnapshotV44_55_31.RecordFinalPair(pair);';to='KiwiAsyncProducerTailSnapshotV44_55_30.RecordFinalPair(pair);'})}
)
foreach($edit in $edits){
    $old=Join-Path $backup ([IO.Path]::GetFileName($edit.file))
    if((Get-Hash $old) -ne $edit.hash){throw 'Exact pre-SHA backup guard'}
    $current=[IO.File]::ReadAllText((Join-Path $ProjectRoot $edit.file)).Replace("`r`n","`n")
    foreach($change in $edit.replace){
        if(([regex]::Matches($current,[regex]::Escape([string]$change.from))).Count -ne 1){throw "Hook cardinality $($edit.file)"}
        $current=$current.Replace([string]$change.from,[string]$change.to)
    }
    if($current -cne [IO.File]::ReadAllText($old).Replace("`r`n","`n")){throw "Non-observer source edit $($edit.file)"}
}$source=Get-Content -LiteralPath (Join-Path $ProjectRoot 'Assets/KiwiAvatarSystem/Runtime/Validation/KiwiAsyncProducerTailSnapshotV44_55_31.cs') -Raw -Encoding UTF8
foreach($bad in @('WaitForCompletion\s*\(','CreateGraphicsFence\s*\(','WaitOnAsyncGraphicsFence\s*\(','ComputeTensorData\.Pin\s*\(','\.Schedule\s*\(','\.RequestAsyncReadback\s*\(','\.input\b','\.SetData\s*\(','FindFirstObjectByType\s*<')){
    if($source -match $bad){throw "Forbidden observer operation $bad"}
}
foreach($need in @('SlotCount = 6','MaxCandidates = 64','MaxPairs = 192','request.hasError','request.done','SynchronizationContext.Current','s.Retire = true','!s.Busy','slot.Resolved','Header = 8','CapturePayloadsBeforeRelease','Resources.FindObjectsOfTypeAll<T>()','payloadSnapshots.Count == 0','payloadMissing == 0')){
    if(-not $source.Contains($need)){throw "Lifecycle invariant absent $need"}
}
$shader=Get-Content -LiteralPath (Join-Path $ProjectRoot 'Assets/KiwiAvatarSystem/Resources/KiwiProducerTailSnapshotV44_55_31.compute') -Raw -Encoding UTF8
$shader=[regex]::Replace($shader,'(?m)//.*$','')
if(-not $shader.Contains('StructuredBuffer<uint> _ProductionOutput') -or -not $shader.Contains('_ObserverSnapshot[8 + id.x] = _ProductionOutput[id.x]') -or $shader -match '\bfloat\d?\s+\w'){throw 'Shader bit-preservation contract'}
foreach($file in Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -in @('.ps1','.psm1')){
    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    if(@($errors).Count -ne 0){throw "PS51 parser: $($file.Name): $errors"}
}
$identities=@(Get-SourceIdentity $ProjectRoot)
Write-Output "V31_STATIC_PASS protected=3 preSHA=3 sourceEditReconstruction=3 sourceIdentities=$($identities.Count) observerLifecycle=BOUNDED_NONBLOCKING_RETIREMENT_STATIC"
