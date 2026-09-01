param([string]$ProjectRoot = "D:\KiwiAvatarSystem")
$ErrorActionPreference = "Stop"
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackageRoot = Split-Path -Parent $ScriptRoot
$Native = Join-Path $ProjectRoot "Native\KiwiNativeCamera\Source\KiwiNativeCameraPlugin.cpp"
$Interop = Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Camera\KiwiNativeCameraInterop.cs"
$Dll = Join-Path $ProjectRoot "Assets\Plugins\x86_64\KiwiNativeCamera.dll"
$Observer = Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiD3DFixedPointSubtexelSamplingParityAuditV44_55_12.cs"
$OldObserver = Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiIdentityCorrectedCpuGpuOutputEquivalenceAuditV44_55_11.cs"
$Launcher = Join-Path $PackageRoot "Launch-v44.55.12-BuildAndAudit.ps1"
$ExpectedNative = "636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A"
$ExpectedInterop = "AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5"
$ExpectedObserver = "BE98143C691D2980013599135FE7F5083EC446C709006CF5BCFF64A22F10EF58"
foreach ($path in @($Native,$Interop,$Dll,$Observer,$Launcher)) { if (-not (Test-Path -LiteralPath $path)) { throw "Missing: $path" } }
$n = Get-Content -LiteralPath $Native -Raw
$i = Get-Content -LiteralPath $Interop -Raw
$o = Get-Content -LiteralPath $Observer -Raw
$l = Get-Content -LiteralPath $Launcher -Raw
$checks = [ordered]@{
    NativeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Native).Hash -eq $ExpectedNative
    InteropHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Interop).Hash -eq $ExpectedInterop
    ObserverHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Observer).Hash -eq $ExpectedObserver
    OldObserverRemoved = -not (Test-Path -LiteralPath $OldObserver)
    Contract = $o.Contains("KIWI_V44_55_12_D3D_FIXED_POINT_SUBTEXEL_SAMPLING_PARITY_AUDIT")
    CandidateList = $o.Contains("FULL_FLOAT,16.8,16.9,16.10,16.12")
    SampleHzDefault1 = $o.Contains("DefaultSampleHz = 1f")
    IdentityCorrection = $o.Contains("ExpandIdentityNeighborhoodIfNeeded")
    SubtexelEvaluation = $o.Contains("EvaluateSubtexelCandidates")
    SubtexelInterop = $i.Contains("TryCopyDiagnosticCpuIdentityCandidateCropNchwFloatSubtexel")
    NativeSnap = $n.Contains("SnapTexelCoordinateFixedDiagnostic")
    RoundEven = $n.Contains("RoundToNearestEvenDiagnostic")
    NativeExport = $n.Contains("KiwiNativeCamera_CopyDiagnosticCpuIdentityCandidateCropNchwFloatSubtexel")
    NoSampleAddRef = -not $n.Contains("sample->AddRef")
    NoUnityQueueWait = -not $n.Contains("g_unityD3D12Queue->Wait")
    NoUpdateExternalTexture = -not $n.Contains("UpdateExternalTexture")
    NoFifo = -not $n.Contains("std::queue")
    NoSetValue = -not $o.Contains(".SetValue(")
    NoProductionSchedule = -not $o.Contains(".Schedule(")
    NoWaitForCompletion = -not $o.Contains("WaitForCompletion")
    LauncherEnable = $l.Contains('$env:KIWI_V44_55_12_SUBTEXEL_AUDIT = "1"')
    LauncherSteady = $l.Contains('$env:KIWI_V44_47_STEADY_AUDIT = "1"')
}
$pass = 0
foreach ($entry in $checks.GetEnumerator()) { if ($entry.Value) { Write-Host ("PASS " + $entry.Key); $pass++ } else { Write-Host ("FAIL " + $entry.Key) } }
Write-Host ("Result: {0}/{1} PASS" -f $pass,$checks.Count)
if ($pass -ne $checks.Count) { exit 1 }
Write-Host "V44_55_12_VALIDATE_PASS"
