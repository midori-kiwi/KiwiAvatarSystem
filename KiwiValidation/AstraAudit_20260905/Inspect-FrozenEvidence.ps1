Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1){throw 'PS 5.1 required'}
$projectRoot='D:\KiwiAvatarSystem'
$outRoot=Join-Path $projectRoot 'KiwiValidation\AstraAudit_20260905'
$rawRoot=Join-Path $projectRoot 'KiwiValidation\RuntimeEvidence_v44_55_31_20260905_165629_f05a2230'
$result=[ordered]@{contract='ASTRA_EXISTING_EVIDENCE_INSPECTION';psVersion=$PSVersionTable.PSVersion.ToString();utc=[DateTime]::UtcNow.ToString('o');runtimeRerun=$false}
function HashFile([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function ExactWords($a,$b){
 if(@($a).Count -ne @($b).Count){return $false}
 for($i=0;$i -lt $a.Count;$i++){if($a[$i] -ne $b[$i]){return $false}}
 return $true
}
$parsedManifest=Get-Content -LiteralPath (Join-Path $rawRoot 'SHA256_MANIFEST.json') -Raw|ConvertFrom-Json
$manifest=@($parsedManifest)
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $manifest){
 $full=[IO.Path]::GetFullPath((Join-Path $rawRoot $entry.path))
 if(-not $full.StartsWith($rawRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest containment'}
 if(-not $seen.Add($full)){throw 'Duplicate manifest path'}
 if((HashFile $full) -ne $entry.sha256){throw ('Raw hash mismatch '+$entry.path)}
}
$actualFiles=@(Get-ChildItem -LiteralPath $rawRoot -Recurse -File|Where-Object Name -ne 'SHA256_MANIFEST.json')
foreach($file in $actualFiles){if(-not $seen.Contains($file.FullName)){throw 'Unmanifested raw file'}}
$result.manifestFiles=$manifest.Count
$result.actualPayloadFiles=$actualFiles.Count
$result.zipSha256=HashFile ($rawRoot+'.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($rawRoot+'.zip')
try{
 $zipSeen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
 foreach($entry in $zip.Entries){
  if($entry.Name -eq ''){continue}
  $rel=$entry.FullName.Replace('/','\')
  if($rel.StartsWith((Split-Path $rawRoot -Leaf)+'\')){$rel=$rel.Substring((Split-Path $rawRoot -Leaf).Length+1)}
  $full=[IO.Path]::GetFullPath((Join-Path $rawRoot $rel))
  if(-not $full.StartsWith($rawRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $zipSeen.Add($full)){throw 'ZIP path/duplicate'}
  $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
  try{$entryHash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
  if($entryHash -ne (HashFile $full)){throw ('ZIP vs extracted mismatch '+$rel)}
 }
 if($zipSeen.Count -ne $actualFiles.Count+1){throw 'ZIP cardinality'}
 $result.zipFiles=$zipSeen.Count
}finally{$zip.Dispose()}
$arms=[ordered]@{}
foreach($arm in @('CONTROL','PROBE')){
 $dir=Join-Path $rawRoot $arm
 $identity=Get-Content -LiteralPath (Join-Path $dir 'run_identity.json') -Raw|ConvertFrom-Json
 if($identity.exitCode -ne 0 -or $identity.timedOut){throw 'Exit failure'}
 if($identity.buildManifestSHA -ne (HashFile (Join-Path $rawRoot 'build_identity.json'))){throw 'Build binding'}
 $rows=@(Get-Content -LiteralPath (Join-Path $dir 'producer_pairs.jsonl')|ForEach-Object{$_|ConvertFrom-Json})
 $csvFiles=@(Get-ChildItem -LiteralPath $dir -Filter 'KiwiActualProductionVsShadowPayloadAuthority*.csv')
 if($csvFiles.Count -ne 1){throw 'Ambiguous CSV'}
 $csv=@(Import-Csv -LiteralPath $csvFiles[0].FullName)
 $pairMap=@{};foreach($row in $csv){if($pairMap.ContainsKey([string]$row.recordIndex)){throw 'Duplicate CSV record'};$pairMap[[string]$row.recordIndex]=$row}
 $counts=[ordered]@{pairs=$rows.Count;csvPairs=$csv.Count;actualNeither=0;snapshotEqualsActual=0;snapshotNeither=0;payloadWordsChecked=0;presenceBitsEqual=0;otherIdentityFieldsEqual=0;gpuHeaderMatches=0;declaredClassificationMatches=0;exitCode=$identity.exitCode;startedUtc=$identity.startedUtc;endedUtc=$identity.endedUtc}
 $keys=@('recordIndex','pairToken','laneIndex','sequence','sourceHostTicks','scheduleBeginHostTicks','startedHostTicks','readbackRequestHostTicks','cameraGeneration','trackingSessionGeneration','trackerGeneration','anchorRevision','externalAnchorEpoch','readbackRequestFrame','laneIdentityToken','workerIdentityToken','pendingOutputIdentityToken')
 $rowSeen=[Collections.Generic.HashSet[string]]::new()
 foreach($row in $rows){
  if(-not $rowSeen.Add([string]$row.recordIndex)){throw 'Duplicate JSON record'}
  $joined=$pairMap[[string]$row.recordIndex];if($null -eq $joined){throw 'Missing join'}
  foreach($key in $keys){if([string]$row.$key -ne [string]$joined.$key){throw ('Identity mismatch '+$key)}}
  $counts.otherIdentityFieldsEqual++
  $bits=[BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$row.minimumPresenceBits),0)
  if([string]$joined.minimumPresenceBits -cnotmatch '^[0-9A-F]{8}$' -or $bits -ne [Convert]::ToUInt32($joined.minimumPresenceBits,16)){throw 'Presence bits mismatch'}
  $counts.presenceBitsEqual++
  $payloadNames=if($arm -eq 'PROBE'){@('actual','reference','mode2','snapshot')}else{@('actual','reference','mode2')}
  foreach($name in $payloadNames){
   if(@($row.$name).Count -ne 1405){throw 'Payload length'}
   foreach($word in $row.$name){
    if(($word -isnot [int] -and $word -isnot [long]) -or $word -lt [int]::MinValue -or $word -gt [int]::MaxValue){throw ('Invalid Int32 JSON payload '+$name)}
    $counts.payloadWordsChecked++
   }
  }
  $isRef=ExactWords $row.actual $row.reference;$isMode=ExactWords $row.actual $row.mode2
  $class=if($isRef){if($isMode){'ACTUAL_EQUALS_BOTH'}else{'ACTUAL_EQUALS_REF_ONLY'}}else{if($isMode){'ACTUAL_EQUALS_MODE2_ONLY'}else{'ACTUAL_EQUALS_NEITHER'}}
  if($class -ne $row.actualClassification){throw 'Declared class mismatch'}
  $counts.declaredClassificationMatches++
  if(-not $isRef -and -not $isMode){$counts.actualNeither++}
  if($arm -eq 'PROBE'){
   if(ExactWords $row.snapshot $row.actual){$counts.snapshotEqualsActual++}
   if(-not (ExactWords $row.snapshot $row.reference) -and -not (ExactWords $row.snapshot $row.mode2)){$counts.snapshotNeither++}
   if(@($row.gpuHeader).Count -ne 8){throw 'GPU header length'}
   $tickBytes=[BitConverter]::GetBytes([long]$row.sourceHostTicks)
   $headerExpected=@(1261646160,31,[int]$row.observerToken,[int]$row.laneIndex,1405,[BitConverter]::ToInt32($tickBytes,0),[BitConverter]::ToInt32($tickBytes,4),([int]$row.observerToken -bxor 0xA55A31))
   if(-not (ExactWords $row.gpuHeader $headerExpected)){throw 'GPU header identity'}
   $counts.gpuHeaderMatches++
  }
 }
 if($rowSeen.Count -ne $pairMap.Count){throw 'Cardinality gap'}
 $arms[$arm]=$counts
}
$result.arms=$arms
$result.limit='Independent offline recomputation of captured bytes; no independent GPU observation, no new runtime, performance or visual authority.'
$outPath=Join-Path $outRoot 'frozen_evidence_recomputation.json'
if(Test-Path -LiteralPath $outPath){throw 'Refusing overwrite'}
[IO.File]::WriteAllText($outPath,($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$result|ConvertTo-Json -Depth 8
