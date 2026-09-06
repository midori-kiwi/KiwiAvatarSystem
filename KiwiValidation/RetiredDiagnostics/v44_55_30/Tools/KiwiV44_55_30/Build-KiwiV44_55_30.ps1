param([string]$ProjectRoot='D:\KiwiAvatarSystem')
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'V30.Common.psm1') -Force
Assert-PS51
& (Join-Path $PSScriptRoot 'Validate-Static.ps1') -ProjectRoot $ProjectRoot
$before=@(Get-SourceIdentity $ProjectRoot)
$target=Assert-UnderRoot (Join-Path $ProjectRoot 'Builds/v44_55_30AsyncProducerTailSnapshot') $ProjectRoot
if(Test-Path -LiteralPath (Join-Path $target 'KiwiAvatarSystem_v44_55_30.exe')){throw 'Build-once target already exists'}
if(@(Get-Process Unity -ErrorAction SilentlyContinue).Count -ne 0){throw 'Unity already running'}
$log=Join-Path $ProjectRoot ('KiwiValidation/v44_55_30_build_'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'.log')
$psi=[Diagnostics.ProcessStartInfo]::new()
$psi.FileName='C:\Program Files\Unity\Hub\Editor\6000.0.80f1\Editor\Unity.exe'
$psi.Arguments='-batchmode -force-d3d12 -quit -projectPath "'+$ProjectRoot+'" -executeMethod KiwiBuildV44_55_30.Build -logFile "'+$log+'"'
$psi.UseShellExecute=$false
$psi.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$process=[Diagnostics.Process]::Start($psi)
Write-Host ("UNITY_BUILD_PID="+$process.Id)
$process.WaitForExit()
$exitCode=$process.ExitCode
if($exitCode -ne 0){throw "Build exit=$exitCode log=$log"}
$logText=Get-Content -LiteralPath $log -Raw -Encoding UTF8
if(-not $logText.Contains('[KiwiV30Build] BUILD_SUCCESS') -or -not $logText.Contains('[KiwiV30Build] STATIC_SHADER_PASS')){throw 'Build/compile markers missing'}
Assert-IdentityRows $ProjectRoot $before
Assert-Protected $ProjectRoot
$payload=[Collections.Generic.List[object]]::new()
foreach($file in Get-ChildItem -LiteralPath $target -File -Recurse | Sort-Object FullName){
    $payload.Add([pscustomobject]@{path=$file.FullName.Substring($ProjectRoot.TrimEnd('\').Length+1);sha256=(Get-Hash $file.FullName)})
}
$mandatory=@('KiwiAvatarSystem_v44_55_30.exe','KiwiAvatarSystem_v44_55_30_Data/Managed/Assembly-CSharp.dll','KiwiAvatarSystem_v44_55_30_Data/Managed/Unity.InferenceEngine.dll','KiwiAvatarSystem_v44_55_30_Data/StreamingAssets/KiwiFaceLandmarkInference.onnx','KiwiAvatarSystem_v44_55_30_Data/StreamingAssets/face_landmarker_v2_with_blendshapes.bytes','KiwiAvatarSystem_v44_55_30_Data/Plugins/x86_64/KiwiNativeCamera.dll')
foreach($p in $mandatory){[void](Get-Hash (Join-Path $target $p))}
$identity=[pscustomobject]@{contract='KIWI_V44_55_30_BUILD';builtUtc=[DateTime]::UtcNow.ToString('O');exitCode=$exitCode;log=$log;source=$before;payload=$payload.ToArray()}
Write-JsonNew (Join-Path $ProjectRoot 'KiwiValidation/v44_55_30_build_identity.json') $identity
Write-Host 'V30_BUILD_PASS compile=PASS shader=PASS Development=1 protected=UNCHANGED'
