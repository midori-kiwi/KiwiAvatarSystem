param([string]$ProjectRoot='D:\KiwiAvatarSystem')
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'V30.Common.psm1') -Force
Assert-PS51
Assert-Protected $ProjectRoot
$backup=Join-Path $ProjectRoot 'KiwiValidation/Backups/v44_55_30_20260905'
$edits=@(
    @{file='Assets/Script/KiwiInferenceFaceTracker.cs';hash='1DBAAB45A5393D688B2ACE2A4FB5FBA995FE7B09E12843D3DAD536A15D8C539A';remove=@(
        "                KiwiAsyncProducerTailSnapshotV44_55_30.AfterPending(lane);\n",
        "                KiwiAsyncProducerTailSnapshotV44_55_30.Append(\n                    cb, lane.worker, lane, this, shadowSourceHostTicks, cropMatrix);\n\n")},
    @{file='Assets/KiwiAvatarSystem/Runtime/Validation/KiwiProductionScheduleTransactionTraceV44_55_25.cs';hash='90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814';remove=@("        KiwiAsyncProducerTailSnapshotV44_55_30.BindTrace(state);\n")},
    @{file='Assets/KiwiAvatarSystem/Runtime/Validation/KiwiActualProductionVsShadowPayloadAuthorityV44_55_27.cs';hash='B8E215D1C3D4103996B7B30BFD8B26DB508782AC6529D12BEF2617E9C65C8B86';remove=@("        KiwiAsyncProducerTailSnapshotV44_55_30.RecordFinalPair(pair);\n")}
)
foreach($edit in $edits){
    $old=Join-Path $backup ([IO.Path]::GetFileName($edit.file))
    if((Get-Hash $old) -ne $edit.hash){throw 'Exact pre-SHA backup guard'}
    $current=[IO.File]::ReadAllText((Join-Path $ProjectRoot $edit.file)).Replace("`r`n","`n")
    foreach($hook in $edit.remove){
        $actualHook=$hook.Replace('\n',"`n")
        if(([regex]::Matches($current,[regex]::Escape($actualHook))).Count -ne 1){throw "Hook cardinality $($edit.file)"}
        $current=$current.Replace($actualHook,'')
    }
    if($current -cne [IO.File]::ReadAllText($old).Replace("`r`n","`n")){throw "Non-observer source edit $($edit.file)"}
}
$source=Get-Content -LiteralPath (Join-Path $ProjectRoot 'Assets/KiwiAvatarSystem/Runtime/Validation/KiwiAsyncProducerTailSnapshotV44_55_30.cs') -Raw -Encoding UTF8
foreach($bad in @('WaitForCompletion\s*\(','CreateGraphicsFence\s*\(','WaitOnAsyncGraphicsFence\s*\(','ComputeTensorData\.Pin\s*\(','\.Schedule\s*\(','\.RequestAsyncReadback\s*\(','\.input\b','\.SetData\s*\(')){
    if($source -match $bad){throw "Forbidden observer operation $bad"}
}
foreach($need in @('SlotCount = 6','MaxCandidates = 64','MaxPairs = 192','request.hasError','request.done','SynchronizationContext.Current','s.Retire = true','!s.Busy','slot.Resolved','Header = 8')){
    if(-not $source.Contains($need)){throw "Lifecycle invariant absent $need"}
}
$shader=Get-Content -LiteralPath (Join-Path $ProjectRoot 'Assets/KiwiAvatarSystem/Resources/KiwiProducerTailSnapshotV44_55_30.compute') -Raw -Encoding UTF8
$shader=[regex]::Replace($shader,'(?m)//.*$','')
if(-not $shader.Contains('StructuredBuffer<uint> _ProductionOutput') -or -not $shader.Contains('_ObserverSnapshot[8 + id.x] = _ProductionOutput[id.x]') -or $shader -match '\bfloat\d?\s+\w'){throw 'Shader bit-preservation contract'}
foreach($file in Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -in @('.ps1','.psm1')){
    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    if(@($errors).Count -ne 0){throw "PS51 parser: $($file.Name): $errors"}
}
$identities=@(Get-SourceIdentity $ProjectRoot)
Write-Output "V30_STATIC_PASS protected=3 preSHA=3 sourceEditReconstruction=3 sourceIdentities=$($identities.Count) observerLifecycle=BOUNDED_NONBLOCKING_RETIREMENT_STATIC"
