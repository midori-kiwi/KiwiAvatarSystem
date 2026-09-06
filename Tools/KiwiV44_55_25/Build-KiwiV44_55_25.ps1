param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$modulePath = Join-Path $PSScriptRoot "..\KiwiPowerShell\KiwiPsCompat.psm1"
Import-Module -Name $modulePath -Force

$ProjectRoot = Resolve-KiwiProjectRoot -ProjectRoot $ProjectRoot
$allowedRoots = @($ProjectRoot)

$observerRel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_25.cs"
$observerMetaRel = $observerRel + ".meta"
$dependencyRel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiCommonTensorBackendStageIsolationV44_55_20.cs"
$v24Rel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiPairBoundShadowOutputSnapshotV44_55_24.cs"
$editorRel = "Assets\Editor\KiwiBuildV44_55_25.cs"
$outputRel = "Builds\v44_55_25ProductionScheduleTransactionTrace\KiwiAvatarSystem_v44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE.exe"
$logDirectoryRel = "KiwiValidation\KiwiV44_55_25_BuildLogs"
$unityLogRel = "KiwiValidation\KiwiBuild_v44_55_25.log"
$validatorRel = "Tools\KiwiV44_55_25\Validate-KiwiV44_55_25.ps1"

$observer = Assert-KiwiPathWithinRoot `
    -Path (Join-Path $ProjectRoot $observerRel) `
    -AllowedRoots $allowedRoots `
    -Label "Observer"
$observerMeta = Assert-KiwiPathWithinRoot `
    -Path (Join-Path $ProjectRoot $observerMetaRel) `
    -AllowedRoots $allowedRoots `
    -Label "Observer meta"
$dependency = Assert-KiwiPathWithinRoot `
    -Path (Join-Path $ProjectRoot $dependencyRel) `
    -AllowedRoots $allowedRoots `
    -Label "v20 dependency"
$v24 = Assert-KiwiPathWithinRoot `
    -Path (Join-Path $ProjectRoot $v24Rel) `
    -AllowedRoots $allowedRoots `
    -Label "v24 dependency"
$editorScript = Assert-KiwiPathWithinRoot `
    -Path (Join-Path $ProjectRoot $editorRel) `
    -AllowedRoots $allowedRoots `
    -Label "Temporary Editor script"
$editorMeta = Assert-KiwiPathWithinRoot `
    -Path ($editorScript + ".meta") `
    -AllowedRoots $allowedRoots `
    -Label "Temporary Editor meta"
$outputExe = Assert-KiwiPathWithinRoot `
    -Path (Join-Path $ProjectRoot $outputRel) `
    -AllowedRoots $allowedRoots `
    -Label "Build output"
$logDirectory = Assert-KiwiPathWithinRoot `
    -Path (Join-Path $ProjectRoot $logDirectoryRel) `
    -AllowedRoots $allowedRoots `
    -Label "Build log directory"
$unityLog = Assert-KiwiPathWithinRoot `
    -Path (Join-Path $ProjectRoot $unityLogRel) `
    -AllowedRoots $allowedRoots `
    -Label "Unity log"
$validator = Assert-KiwiPathWithinRoot `
    -Path (Join-Path $ProjectRoot $validatorRel) `
    -AllowedRoots $allowedRoots `
    -Label "Static validator"

[void](Assert-KiwiFileSha256 `
    -Path $observer `
    -ExpectedSha256 "90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814" `
    -Label "v44.55.25 observer")
[void](Assert-KiwiFileSha256 `
    -Path $observerMeta `
    -ExpectedSha256 "4CE42873100AF234F45C23C3F5B0D6B8E96B0D34B94FCAEA5E3E5A20EC1A9874" `
    -Label "v44.55.25 observer meta")
[void](Assert-KiwiFileSha256 `
    -Path $dependency `
    -ExpectedSha256 "1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3" `
    -Label "v44.55.20 dependency")
[void](Assert-KiwiFileSha256 `
    -Path $v24 `
    -ExpectedSha256 "805E746E453F3785FCAA46EFB07745B0038A428789D4A09BB944854F15477FBA" `
    -Label "v44.55.24 dependency")
[void](Assert-KiwiFile -Path $validator -Label "Static validator")

$protectedHashes = @{
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiCommonTensorBackendStageIsolationV44_55_20.cs") = "1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiPairBoundShadowOutputSnapshotV44_55_24.cs") = "805E746E453F3785FCAA46EFB07745B0038A428789D4A09BB944854F15477FBA"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionOutputTransactionAuthorityV44_55_23.cs") = "AE2CD168171335E20F6793C5F46A12264D86CCAE21DF62FAFF74FCBA1E92E4A7"
    (Join-Path $ProjectRoot "Assets\Script\KiwiInferenceFaceTracker.cs") = "52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Tracking\KiwiInferenceFaceTracker.cs") = "EFFCCCF5EE1BF407F065BFA95B491390AC96A55E9A75290A67C5AFAD32AFC1F1"
    (Join-Path $ProjectRoot "Assets\Script\FaceLandmarkerRunner.cs") = "6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93"
    (Join-Path $ProjectRoot "Assets\Script\KiwiFaceMotion.cs") = "D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Camera\KiwiNativeCameraInterop.cs") = "AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5"
    (Join-Path $ProjectRoot "Assets\Plugins\x86_64\KiwiNativeCamera.dll") = "82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5"
    (Join-Path $ProjectRoot "Packages\manifest.json") = "35F00A89D31A8BF7EE50718F38D4BAC90236D8B691F516C5FC14CAB47AFBFEE1"
    (Join-Path $ProjectRoot "Packages\packages-lock.json") = "23845C132DBF51CB53A5CFDE4EF606FA68D10DCB53AE5FF21F4305613A5F9CE1"
}

foreach ($protectedPath in @($protectedHashes.Keys)) {
    [void](Assert-KiwiFileSha256 `
        -Path ([string]$protectedPath) `
        -ExpectedSha256 ([string]$protectedHashes[$protectedPath]) `
        -Label "Before build protected file")
}

$windowsPowerShell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
[void](Invoke-KiwiProcess `
    -Executable $windowsPowerShell `
    -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $validator,
        "-ProjectRoot", $ProjectRoot,
        "-StaticOnly"
    ) `
    -WorkingDirectory $ProjectRoot `
    -LogDirectory $logDirectory `
    -AllowedLogRoots $allowedRoots `
    -LogName "static-validator" `
    -TimeoutSeconds 300 `
    -ProtectedHashes $protectedHashes)

$unityExe = "C:\Program Files\Unity\Hub\Editor\6000.0.80f1\Editor\Unity.exe"
[void](Assert-KiwiFile -Path $unityExe -Label "Unity 6000.0.80f1")

if (Get-Process Unity -ErrorAction SilentlyContinue) {
    throw "Unity Editor is running. Close it before batch build."
}

if (Test-Path -LiteralPath $outputExe -PathType Leaf) {
    throw "Refusing to overwrite existing v44.55.25 output: $outputExe"
}

$editorDirectory = Split-Path -Parent $editorScript
if (-not (Test-Path -LiteralPath $editorDirectory -PathType Container)) {
    [System.IO.Directory]::CreateDirectory($editorDirectory) | Out-Null
}
if (-not (Test-Path -LiteralPath $logDirectory -PathType Container)) {
    [System.IO.Directory]::CreateDirectory($logDirectory) | Out-Null
}

$editorSource = @'
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

public static class KiwiBuildV44_55_25
{
    private static string UnderRoot(string root, string path)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string pathFull = Path.GetFullPath(path);

        if (!pathFull.StartsWith(
                rootFull,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BuildFailedException(
                "Write target escapes project root: " + pathFull);
        }

        return pathFull;
    }

    public static void PerformBuild()
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string observerPath = UnderRoot(
            projectRoot,
            Path.Combine(
                Application.dataPath,
                "KiwiAvatarSystem/Runtime/Validation/KiwiProductionScheduleTransactionTraceV44_55_25.cs"));
        string dependencyPath = UnderRoot(
            projectRoot,
            Path.Combine(
                Application.dataPath,
                "KiwiAvatarSystem/Runtime/Validation/KiwiCommonTensorBackendStageIsolationV44_55_20.cs"));
        string v24Path = UnderRoot(
            projectRoot,
            Path.Combine(
                Application.dataPath,
                "KiwiAvatarSystem/Runtime/Validation/KiwiPairBoundShadowOutputSnapshotV44_55_24.cs"));

        if (!File.Exists(observerPath) ||
            !File.Exists(dependencyPath) ||
            !File.Exists(v24Path))
        {
            throw new BuildFailedException(
                "v44.55.25 required observer/dependency file missing.");
        }

        GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(
            BuildTarget.StandaloneWindows64);

        if (apis == null ||
            apis.Length == 0 ||
            apis[0] != GraphicsDeviceType.Direct3D12)
        {
            throw new BuildFailedException(
                "DX12 is not first StandaloneWindows64 graphics API.");
        }

        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene != null && scene.enabled)
            .Select(scene => scene.path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        if (scenes.Length == 0)
        {
            throw new BuildFailedException("No enabled build scene.");
        }

        Debug.Log(
            "[Kiwi v44.55.25 Build] PREFLIGHT_START mode=FULL bypass=0");

        KiwiReleaseCandidatePreflight.PreflightReport preflight =
            KiwiReleaseCandidatePreflight.RunFullPreflight(true);

        string preflightPath = UnderRoot(
            projectRoot,
            Path.Combine(
                projectRoot,
                "KiwiValidation/KiwiPreflight_v44_55_25.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(preflightPath));
        File.WriteAllText(
            preflightPath,
            JsonUtility.ToJson(preflight, true));

        Debug.Log(
            "[Kiwi v44.55.25 Build] PREFLIGHT_SUMMARY" +
            " passed=" + (preflight.passed ? "1" : "0") +
            " errors=" + preflight.errorCount +
            " critical=" + preflight.criticalCount +
            " report=" + preflightPath);

        if (!preflight.passed ||
            preflight.errorCount != 0 ||
            preflight.criticalCount != 0)
        {
            throw new BuildFailedException(
                "v44.55.25 Full Preflight failed.");
        }

        if (!KiwiReleaseCandidatePreflight.HasCurrentPassingStamp(
                out string stampReason))
        {
            throw new BuildFailedException(
                "Passing stamp invalid: " + stampReason);
        }

        string output = UnderRoot(
            projectRoot,
            Path.Combine(
                projectRoot,
                "Builds/v44_55_25ProductionScheduleTransactionTrace/KiwiAvatarSystem_v44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE.exe"));
        Directory.CreateDirectory(Path.GetDirectoryName(output));

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development | BuildOptions.StrictMode
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        Debug.Log(
            "[Kiwi v44.55.25 Build] SUMMARY" +
            " result=" + summary.result +
            " errors=" + summary.totalErrors +
            " warnings=" + summary.totalWarnings +
            " output=" + output);

        if (summary.result != BuildResult.Succeeded ||
            summary.totalErrors != 0 ||
            !File.Exists(output))
        {
            throw new BuildFailedException(
                "v44.55.25 Development build failed.");
        }

        Debug.Log("[Kiwi v44.55.25 Build] PASS output=" + output);
    }
}
'@

$editorBackup = $null
$editorMetaBackup = $null

try {
    if (Test-Path -LiteralPath $editorScript -PathType Leaf) {
        $editorBackup = [System.IO.File]::ReadAllBytes($editorScript)
    }
    if (Test-Path -LiteralPath $editorMeta -PathType Leaf) {
        $editorMetaBackup = [System.IO.File]::ReadAllBytes($editorMeta)
    }

    [void](Write-KiwiTextFile `
        -Path $editorScript `
        -Text $editorSource `
        -AllowedRoots $allowedRoots `
        -Encoding "Utf8NoBom")

    [void](Invoke-KiwiProcess `
        -Executable $unityExe `
        -ArgumentList @(
            "-batchmode",
            "-quit",
            "-projectPath", $ProjectRoot,
            "-buildTarget", "Win64",
            "-executeMethod", "KiwiBuildV44_55_25.PerformBuild",
            "-logFile", $unityLog
        ) `
        -WorkingDirectory $ProjectRoot `
        -LogDirectory $logDirectory `
        -AllowedLogRoots $allowedRoots `
        -LogName "unity-build" `
        -TimeoutSeconds 7200 `
        -ProtectedHashes $protectedHashes)
}
finally {
    if ($null -ne $editorBackup) {
        [System.IO.File]::WriteAllBytes($editorScript, $editorBackup)
    }
    elseif (Test-Path -LiteralPath $editorScript -PathType Leaf) {
        [System.IO.File]::Delete($editorScript)
    }

    if ($null -ne $editorMetaBackup) {
        [System.IO.File]::WriteAllBytes($editorMeta, $editorMetaBackup)
    }
    elseif (Test-Path -LiteralPath $editorMeta -PathType Leaf) {
        [System.IO.File]::Delete($editorMeta)
    }
}

if (-not (Test-Path -LiteralPath $outputExe -PathType Leaf)) {
    throw "Build process exited successfully but output EXE is missing."
}

$assemblyPath = Join-Path `
    (Split-Path -Parent $outputExe) `
    "KiwiAvatarSystem_v44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE_Data\Managed\Assembly-CSharp.dll"
[void](Assert-KiwiFile -Path $assemblyPath -Label "Assembly-CSharp.dll")

foreach ($protectedPath in @($protectedHashes.Keys)) {
    [void](Assert-KiwiFileSha256 `
        -Path ([string]$protectedPath) `
        -ExpectedSha256 ([string]$protectedHashes[$protectedPath]) `
        -Label "After build protected file")
}

$exeInfo = Get-Item -LiteralPath $outputExe
$assemblyInfo = Get-Item -LiteralPath $assemblyPath
Write-Host "BUILD PASS"
Write-Host ("EXE=" + $exeInfo.FullName)
Write-Host ("EXE_BYTES=" + $exeInfo.Length)
Write-Host ("EXE_SHA256=" + (Get-KiwiSha256 -Path $outputExe))
Write-Host ("ASSEMBLY=" + $assemblyInfo.FullName)
Write-Host ("ASSEMBLY_BYTES=" + $assemblyInfo.Length)
Write-Host ("ASSEMBLY_SHA256=" + (Get-KiwiSha256 -Path $assemblyPath))
