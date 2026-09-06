param([string]$ProjectRoot='D:\KiwiAvatarSystem')
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'V31.Common.psm1') -Force
Assert-PS51
Assert-Protected $ProjectRoot
$runPath=Join-Path $PSScriptRoot 'Run-KiwiV44_55_31.ps1'
$run=[IO.File]::ReadAllText($runPath)
$markers=@(
    "Write-JsonNew (Join-Path `$armDir 'run_identity.json')",
    'if($timedOut -or $exitCode -ne 0){throw',
    "Write-JsonNew (Join-Path `$root 'collection_failure.json')",
    'New-EvidenceManifest $root',
    'Compress-Archive -LiteralPath $root',
    'if($null -ne $collectError){throw $collectError}',
    "& (Join-Path `$PSScriptRoot 'Validate-Runtime.ps1')"
)
$last=-1
foreach($marker in $markers){
    $index=$run.IndexOf($marker,[StringComparison]::Ordinal)
    if($index -le $last){throw "Collector ordering marker missing/out of order: $marker"}
    $last=$index
}
$root=Assert-UnderRoot (Join-Path $ProjectRoot ('KiwiValidation/v44_55_31_collector_selftest_'+[Guid]::NewGuid().ToString('N'))) $ProjectRoot
[void][IO.Directory]::CreateDirectory($root)
$control=Join-Path $root 'CONTROL';[void][IO.Directory]::CreateDirectory($control)
$psi=[Diagnostics.ProcessStartInfo]::new()
$psi.FileName='C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe'
$psi.Arguments='-NoProfile -Command "exit 30"'
$psi.UseShellExecute=$false
$started=[DateTime]::UtcNow
$p=[Diagnostics.Process]::Start($psi)
$timedOut=-not $p.WaitForExit(30000)
if($timedOut){$p.Kill();$p.WaitForExit()}
$exitCode=$p.ExitCode
Write-JsonNew (Join-Path $control 'run_identity.json') ([pscustomobject]@{contract='KIWI_V44_55_31_RUN';arm='CONTROL';startedUtc=$started.ToString('O');endedUtc=[DateTime]::UtcNow.ToString('O');exitCode=$exitCode;timedOut=$timedOut;pid=$p.Id;synthetic=$true})
if($timedOut -or $exitCode -ne 30){throw "Child exit propagation failure: exit=$exitCode timeout=$timedOut"}
Write-JsonNew (Join-Path $root 'collection_failure.json') ([pscustomobject]@{error='CONTROL failed normal exit: 30 timeout=False';status='INCONCLUSIVE';synthetic=$true})
New-EvidenceManifest $root
Assert-EvidenceManifest $root
$zip=$root+'.zip'
Compress-Archive -LiteralPath $root -DestinationPath $zip -CompressionLevel Optimal
if(-not(Test-Path -LiteralPath $zip -PathType Leaf)){throw 'Failure ZIP missing'}
$failure=Get-Content -LiteralPath (Join-Path $root 'collection_failure.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$identity=Get-Content -LiteralPath (Join-Path $control 'run_identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($failure.status -ne 'INCONCLUSIVE' -or $identity.exitCode -ne 30 -or $identity.timedOut){throw 'Failure classification mismatch'}
Write-Host ('COLLECTOR_SELFTEST_PASS childExit=30 launcherTimeout=0 failurePreserved=1 manifest=PASS zipSHA256='+(Get-Hash $zip)+' root='+$root)