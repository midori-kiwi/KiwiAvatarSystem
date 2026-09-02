param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$ObserverRel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiReferencePixelTransactionIntegrityV44_55_22.cs"
$DependencyRel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiCommonTensorBackendStageIsolationV44_55_20.cs"
$SnapshotShaderRel = "Assets\KiwiAvatarSystem\Runtime\Validation\Resources\KiwiValidation\KiwiTensorSnapshotCopyV44_55_22.compute"
$EditorRel = "Assets\Editor\KiwiBuildV44_55_22.cs"
$OutputRel = "Builds\v44_55_22ReferencePixelTransactionIntegrity\KiwiAvatarSystem_v44_55_22_REF_TRANSACTION.exe"
$LogRel = "KiwiValidation\KiwiBuild_v44_55_22.log"

$ExpectedObserverSha = "BBCECC1ECDF57A7E6D452A4B5B5D3E2CB0F2165755C00C09F191B0997B4F0B9E"
$ExpectedDependencySha = "1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3"
$ExpectedSnapshotShaderSha = "D9B0653E0F478359473D42A43FD0321FADC959DF21378A76EF7999B1322DE48D"

function Assert-UnderRoot {
    param(
        [string]$Root,
        [string]$Path
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $pathFull = [System.IO.Path]::GetFullPath($Path)

    if (-not $pathFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes ProjectRoot: $pathFull"
    }

    return $pathFull
}

function Assert-Sha256 {
    param(
        [string]$Path,
        [string]$Expected
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file missing: $Path"
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()

    if ($actual -ne $Expected) {
        throw "SHA256 mismatch:`nPath: $Path`nExpected: $Expected`nActual:   $actual"
    }

    Write-Host "SHA PASS: $Path"
}

$ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)

if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) {
    throw "ProjectRoot not found: $ProjectRoot"
}

$observer = Assert-UnderRoot $ProjectRoot (Join-Path $ProjectRoot $ObserverRel)
$dependency = Assert-UnderRoot $ProjectRoot (Join-Path $ProjectRoot $DependencyRel)
$snapshotShader = Assert-UnderRoot $ProjectRoot (Join-Path $ProjectRoot $SnapshotShaderRel)
$editorScript = Assert-UnderRoot $ProjectRoot (Join-Path $ProjectRoot $EditorRel)
$outputExe = Assert-UnderRoot $ProjectRoot (Join-Path $ProjectRoot $OutputRel)
$buildLog = Assert-UnderRoot $ProjectRoot (Join-Path $ProjectRoot $LogRel)

Assert-Sha256 $observer $ExpectedObserverSha
Assert-Sha256 $dependency $ExpectedDependencySha
Assert-Sha256 $snapshotShader $ExpectedSnapshotShaderSha

$unityCandidates = @(
    "C:\Program Files\Unity\Hub\Editor\6000.0.80f1\Editor\Unity.exe",
    "C:\Program Files\Unity\Editor\Unity.exe"
)

$unityExe = $null

foreach ($candidate in $unityCandidates) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $unityExe = $candidate
        break
    }
}

if ($null -eq $unityExe) {
    $found = Get-ChildItem "C:\Program Files\Unity" -Filter Unity.exe -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "6000\.0\.80f1" } |
        Select-Object -First 1

    if ($null -ne $found) {
        $unityExe = $found.FullName
    }
}

if ($null -eq $unityExe) {
    throw "Unity 6000.0.80f1 Unity.exe was not found."
}

$runningUnity = Get-Process Unity -ErrorAction SilentlyContinue

if ($null -ne $runningUnity) {
    throw "Unity Editor is running. Close Unity before batch build, then run this script again."
}

$editorDir = Split-Path -Parent $editorScript
New-Item -ItemType Directory -Force -Path $editorDir | Out-Null

$buildLogDir = Split-Path -Parent $buildLog
New-Item -ItemType Directory -Force -Path $buildLogDir | Out-Null

$editorSource = @'
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

public static class KiwiBuildV44_55_22
{
    private const string OutputRelative =
        "Builds/v44_55_22ReferencePixelTransactionIntegrity/" +
        "KiwiAvatarSystem_v44_55_22_REF_TRANSACTION.exe";

    public static void PerformBuild()
    {
        string projectRoot =
            Directory.GetParent(Application.dataPath).FullName;

        string observerPath =
            Path.Combine(
                Application.dataPath,
                "KiwiAvatarSystem/Runtime/Validation/" +
                "KiwiReferencePixelTransactionIntegrityV44_55_22.cs");

        string dependencyPath =
            Path.Combine(
                Application.dataPath,
                "KiwiAvatarSystem/Runtime/Validation/" +
                "KiwiCommonTensorBackendStageIsolationV44_55_20.cs");

        string snapshotShaderPath =
            Path.Combine(
                Application.dataPath,
                "KiwiAvatarSystem/Runtime/Validation/Resources/KiwiValidation/" +
                "KiwiTensorSnapshotCopyV44_55_22.compute");

        if (!File.Exists(observerPath))
        {
            throw new BuildFailedException(
                "v44.55.22 observer source missing: " +
                observerPath);
        }

        if (!File.Exists(dependencyPath))
        {
            throw new BuildFailedException(
                "v44.55.20 dependency source missing: " +
                dependencyPath);
        }

        if (!File.Exists(snapshotShaderPath))
        {
            throw new BuildFailedException(
                "v44.55.22 snapshot compute shader missing: " +
                snapshotShaderPath);
        }

        string[] scenes =
            EditorBuildSettings.scenes
                .Where(s => s != null && s.enabled)
                .Select(s => s.path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

        if (scenes.Length == 0)
        {
            throw new BuildFailedException(
                "No enabled EditorBuildSettings scenes.");
        }

        GraphicsDeviceType[] graphicsApis =
            PlayerSettings.GetGraphicsAPIs(
                BuildTarget.StandaloneWindows64);

        if (
            graphicsApis == null ||
            graphicsApis.Length == 0 ||
            graphicsApis[0] != GraphicsDeviceType.Direct3D12)
        {
            throw new BuildFailedException(
                "DX12 is not the first StandaloneWindows64 graphics API.");
        }

        string output =
            Path.GetFullPath(
                Path.Combine(
                    projectRoot,
                    OutputRelative));

        Directory.CreateDirectory(
            Path.GetDirectoryName(output));

        BuildPlayerOptions options =
            new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options =
                    BuildOptions.Development |
                    BuildOptions.StrictMode
            };

        Debug.Log(
            "[Kiwi v44.55.22 Build] START" +
            " output=" + output +
            " target=StandaloneWindows64" +
            " development=1" +
            " strictMode=1" +
            " graphicsApi0=" + graphicsApis[0] +
            " scenes=" + string.Join(";", scenes));

        Debug.Log(
            "[Kiwi v44.55.22 Build] PREFLIGHT_START" +
            " mode=FULL" +
            " bypass=0");

        KiwiReleaseCandidatePreflight.PreflightReport preflight =
            KiwiReleaseCandidatePreflight.RunFullPreflight(true);

        string validationDirectory =
            Path.Combine(
                projectRoot,
                "KiwiValidation");

        Directory.CreateDirectory(
            validationDirectory);

        string preflightReportPath =
            Path.Combine(
                validationDirectory,
                "KiwiPreflight_v44_55_22.json");

        File.WriteAllText(
            preflightReportPath,
            JsonUtility.ToJson(
                preflight,
                true));

        Debug.Log(
            "[Kiwi v44.55.22 Build] PREFLIGHT_SUMMARY" +
            " passed=" + (preflight.passed ? "1" : "0") +
            " rcReady=" + (preflight.releaseCandidateReady ? "1" : "0") +
            " warnings=" + preflight.warningCount +
            " errors=" + preflight.errorCount +
            " critical=" + preflight.criticalCount +
            " fingerprint=" + (preflight.fingerprint ?? string.Empty) +
            " report=" + preflightReportPath);

        if (
            !preflight.passed ||
            preflight.errorCount != 0 ||
            preflight.criticalCount != 0)
        {
            throw new BuildFailedException(
                "v44.55.22 Full Preflight failed. " +
                "warnings=" + preflight.warningCount +
                " errors=" + preflight.errorCount +
                " critical=" + preflight.criticalCount +
                " report=" + preflightReportPath);
        }

        if (
            !KiwiReleaseCandidatePreflight.HasCurrentPassingStamp(
                out string stampReason))
        {
            throw new BuildFailedException(
                "Full Preflight passed but current passing stamp validation failed: " +
                stampReason);
        }

        Debug.Log(
            "[Kiwi v44.55.22 Build] PREFLIGHT_PASS" +
            " currentStamp=1" +
            " bypass=0");

        BuildReport report =
            BuildPipeline.BuildPlayer(options);

        BuildSummary summary =
            report.summary;

        Debug.Log(
            "[Kiwi v44.55.22 Build] SUMMARY" +
            " result=" + summary.result +
            " errors=" + summary.totalErrors +
            " warnings=" + summary.totalWarnings +
            " size=" + summary.totalSize +
            " duration=" + summary.totalTime +
            " output=" + output);

        if (
            summary.result != BuildResult.Succeeded ||
            summary.totalErrors != 0 ||
            !File.Exists(output))
        {
            throw new BuildFailedException(
                "v44.55.22 Development build failed. " +
                "result=" + summary.result +
                " errors=" + summary.totalErrors +
                " output=" + output);
        }

        Debug.Log(
            "[Kiwi v44.55.22 Build] PASS" +
            " output=" + output);
    }
}
'@

$editorBackup = $null
$editorMeta = $editorScript + ".meta"

try {
    if (Test-Path -LiteralPath $editorScript -PathType Leaf) {
        $editorBackup = [System.IO.File]::ReadAllBytes($editorScript)
    }

    [System.IO.File]::WriteAllText(
        $editorScript,
        $editorSource,
        (New-Object System.Text.UTF8Encoding($false))
    )

    if (Test-Path -LiteralPath $outputExe -PathType Leaf) {
        Remove-Item -LiteralPath $outputExe -Force
    }

    Write-Host ""
    Write-Host "Unity:  $unityExe"
    Write-Host "Project: $ProjectRoot"
    Write-Host "Output:  $outputExe"
    Write-Host "Log:     $buildLog"
    Write-Host ""

    $arguments = @(
        "-batchmode",
        "-quit",
        "-projectPath", $ProjectRoot,
        "-buildTarget", "Win64",
        "-executeMethod", "KiwiBuildV44_55_22.PerformBuild",
        "-logFile", $buildLog
    )

    $process = Start-Process `
        -FilePath $unityExe `
        -ArgumentList $arguments `
        -Wait `
        -PassThru `
        -NoNewWindow

    if ($process.ExitCode -ne 0) {
        Write-Host ""
        Write-Host "Unity exit code: $($process.ExitCode)"
        Write-Host "Last build log lines:"
        Get-Content -LiteralPath $buildLog -Tail 120 -ErrorAction SilentlyContinue
        throw "Unity batch build failed."
    }

    if (-not (Test-Path -LiteralPath $outputExe -PathType Leaf)) {
        throw "Build process exited successfully but EXE was not produced: $outputExe"
    }

    $exeHash = (Get-FileHash -LiteralPath $outputExe -Algorithm SHA256).Hash.ToUpperInvariant()
    $exeInfo = Get-Item -LiteralPath $outputExe

    Write-Host ""
    Write-Host "BUILD PASS"
    Write-Host "EXE:    $($exeInfo.FullName)"
    Write-Host "Bytes:  $($exeInfo.Length)"
    Write-Host "SHA256: $exeHash"
    Write-Host ""
    Write-Host "Next runtime environment:"
    Write-Host '$env:KIWI_V44_55_20_COMMON_TENSOR_AUDIT = "1"'
    Write-Host '$env:KIWI_V44_55_20_COMMON_TENSOR_SECONDS = "120"'
    Write-Host '$env:KIWI_V44_55_20_COMMON_TENSOR_HZ = "1"'
    Write-Host '$env:KIWI_V44_55_20_STABLE_SECONDS = "8"'
    Write-Host 'Remove-Item Env:KIWI_V44_55_21_CROP_SAMPLER_AUDIT -ErrorAction SilentlyContinue'
    Write-Host '$env:KIWI_V44_55_22_REFERENCE_TRANSACTION_AUDIT = "1"'
    Write-Host "& '$outputExe'"
}
finally {
    if ($null -ne $editorBackup) {
        [System.IO.File]::WriteAllBytes($editorScript, $editorBackup)
    }
    elseif (Test-Path -LiteralPath $editorScript -PathType Leaf) {
        Remove-Item -LiteralPath $editorScript -Force
    }

    if (
        $null -eq $editorBackup -and
        (Test-Path -LiteralPath $editorMeta -PathType Leaf)
    ) {
        Remove-Item -LiteralPath $editorMeta -Force
    }
}
