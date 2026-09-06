Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
function Assert-PS51 {
    if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Windows PowerShell 5.1 required' }
}
function Get-Hash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing identity file: $Path" }
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function Assert-UnderRoot([string]$Path,[string]$Root) {
    $r=[IO.Path]::GetFullPath($Root).TrimEnd('\')+'\'
    $p=[IO.Path]::GetFullPath($Path)
    if (-not $p.StartsWith($r,[StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes root: $p" }
    $p
}
function Write-JsonNew([string]$Path,$Value) {
    if (Test-Path -LiteralPath $Path) { throw "Refusing overwrite: $Path" }
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
}
function Read-Kv([string]$Path) {
    $map=@{}
    foreach($line in [IO.File]::ReadAllLines($Path)) {
        if($line -match '^([^=\[ ]+?)=(.*)$') {
            if($map.ContainsKey($matches[1])) { throw "Duplicate key $($matches[1]) in $Path" }
            $map[$matches[1]]=$matches[2]
        }
    }
    $map
}
function Assert-Protected([string]$Root) {
    $expected=@{
        'Assets/Script/FaceLandmarkerRunner.cs'='6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93'
        'Assets/Script/KiwiFaceMotion.cs'='D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6'
        'Assets/Plugins/x86_64/KiwiNativeCamera.dll'='82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5'
    }
    foreach($p in $expected.Keys) { if((Get-Hash (Join-Path $Root $p)) -ne $expected[$p]) { throw "Protected SHA drift: $p" } }
}
function Get-SourceIdentity([string]$Root) {
    $files=[Collections.Generic.List[string]]::new()
    $freeze=Join-Path $Root 'KiwiValidation/KiwiCodexConsolidatedReport_v44_55_29_Stage1Ready_20260904_213928.txt'
    foreach($line in [IO.File]::ReadAllLines($freeze)) {
        if($line -match '^([0-9A-F]{64})  (.+)$') {
            $hash=$matches[1]; $rel=$matches[2]; $path=Join-Path $Root $rel
            if($rel -ne 'Assets/Script/KiwiInferenceFaceTracker.cs' -and (Get-Hash $path) -ne $hash) { throw "Freeze drift: $rel" }
            $files.Add($rel)
        }
    }
    foreach($rel in @(
        'Assets/KiwiAvatarSystem/Runtime/Validation/KiwiProductionScheduleTransactionTraceV44_55_25.cs',
        'Assets/KiwiAvatarSystem/Runtime/Validation/KiwiActualProductionVsShadowPayloadAuthorityV44_55_27.cs',
        'Assets/KiwiAvatarSystem/Runtime/Validation/KiwiAsyncProducerTailSnapshotV44_55_31.cs',
        'Assets/KiwiAvatarSystem/Resources/KiwiProducerTailSnapshotV44_55_31.compute',
        'Assets/KiwiAvatarSystem/Resources/KiwiProducerTailSnapshotV44_55_31.compute.meta',
        'Assets/KiwiAvatarSystem/Editor/KiwiBuildV44_55_31.cs',
        'Assets/KiwiAvatarSystem/Editor/KiwiSnapshotCopyTestV44_55_31.cs',
        'Library/PackageCache/com.unity.ai.inference@587873fd5e1b/package.json',
        'Library/PackageCache/com.github.homuler.mediapipe@66127f8e750d/package.json'
    )) { $files.Add($rel) }
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $Root 'Tools/KiwiV44_55_31') -File) { $files.Add($file.FullName.Substring($Root.TrimEnd('\').Length+1)) }
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $Root 'ProjectSettings') -File) { $files.Add($file.FullName.Substring($Root.TrimEnd('\').Length+1)) }
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $Root 'Library/PackageCache/com.unity.ai.inference@587873fd5e1b/Runtime/Core') -File -Recurse -Filter '*.cs') { $files.Add($file.FullName.Substring($Root.TrimEnd('\').Length+1)) }
    $rows=[Collections.Generic.List[object]]::new()
    foreach($rel in @($files.ToArray() | Sort-Object -Unique)) {
        $rows.Add([pscustomobject]@{path=$rel;sha256=(Get-Hash (Join-Path $Root $rel))})
    }
    $rows.ToArray()
}
function Assert-IdentityRows([string]$Root,$Rows) {
    foreach($r in $Rows) {
        $path=Assert-UnderRoot (Join-Path $Root $r.path) $Root
        if((Get-Hash $path) -ne $r.sha256) { throw "Identity drift: $($r.path)" }
    }
}
function New-EvidenceManifest([string]$Root) {
    $rows=[Collections.Generic.List[object]]::new()
    foreach($f in Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName) {
        if($f.Name -eq 'SHA256_MANIFEST.json') {continue}
        $rows.Add([pscustomobject]@{path=$f.FullName.Substring($Root.TrimEnd('\').Length+1);sha256=(Get-Hash $f.FullName)})
    }
    Write-JsonNew (Join-Path $Root 'SHA256_MANIFEST.json') $rows.ToArray()
}
function Assert-EvidenceManifest([string]$Root) {
    $manifest=Get-Content -LiteralPath (Join-Path $Root 'SHA256_MANIFEST.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-IdentityRows $Root $manifest
    $actual=@(Get-ChildItem -LiteralPath $Root -File -Recurse | Where-Object Name -ne 'SHA256_MANIFEST.json')
    if($actual.Count -ne @($manifest).Count){throw 'Unmanifested evidence file'}
}
function Test-WordEqual($A,$B) {
    if($null -eq $A -or $null -eq $B -or @($A).Count -ne @($B).Count){return $false}
    for($i=0;$i -lt $A.Count;$i++){if([int64]$A[$i] -ne [int64]$B[$i]){return $false}}
    return $true
}
function Get-PayloadClass($Value,$Reference,$Mode2,[string]$Prefix) {
    foreach($a in @(@{v=$Value},@{v=$Reference},@{v=$Mode2})){if(@($a.v).Count -ne 1405){throw 'Logical payload size'}}
    $r=Test-WordEqual $Value $Reference; $m=Test-WordEqual $Value $Mode2
    $suffix=if($r){if($m){'BOTH'}else{'REF_ONLY'}}else{if($m){'MODE2_ONLY'}else{'NEITHER'}}
    $Prefix+$suffix
}
function Get-PrimaryDecision($Snapshot,$Actual,$Reference,$Mode2) {
    $s=Get-PayloadClass $Snapshot $Reference $Mode2 'PRODUCER_SNAPSHOT_EQUALS_'
    $a=Get-PayloadClass $Actual $Reference $Mode2 'ACTUAL_EQUALS_'
    if($s -eq 'PRODUCER_SNAPSHOT_EQUALS_REF_ONLY' -and $a -eq 'ACTUAL_EQUALS_NEITHER'){return 'DIVERGENCE_AFTER_PRODUCER_TAIL_SUPPORTED'}
    if((Test-WordEqual $Snapshot $Actual) -and $a -eq 'ACTUAL_EQUALS_NEITHER'){return 'DIVERGENCE_PRESENT_BY_PRODUCER_TAIL_OR_COMMON_OBSERVATION_PATH'}
    if(-not (Test-WordEqual $Snapshot $Actual)){return 'OUTPUT_OBSERVATION_PATH_DISAGREEMENT_CONFIRMED'}
    'NONDISCRIMINATING'
}
Export-ModuleMember -Function *
