param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$ObserverRel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionOutputTransactionAuthorityV44_55_23.cs"
$DependencyRel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiCommonTensorBackendStageIsolationV44_55_20.cs"
$ShaderRel = "Assets\KiwiAvatarSystem\Runtime\Validation\Resources\KiwiValidation\KiwiOutputSnapshotCopyV44_55_23.compute"
$EditorRel = "Assets\Editor\KiwiBuildV44_55_23.cs"
$OutputRel = "Builds\v44_55_23ProductionOutputAuthority\KiwiAvatarSystem_v44_55_23_OUTPUT_AUTHORITY.exe"
$LogRel = "KiwiValidation\KiwiBuild_v44_55_23.log"

$ExpectedObserverSha = "AE2CD168171335E20F6793C5F46A12264D86CCAE21DF62FAFF74FCBA1E92E4A7"
$ExpectedDependencySha = "1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3"
$ExpectedShaderSha = "4552D8A2CB4E47465D1BD765CD38D85F7B839A61B73555CD40557937D803CB71"

function Assert-UnderRoot {
    param([string]$Root, [string]$Path)

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $pathFull = [System.IO.Path]::GetFullPath($Path)

    if (-not $pathFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes ProjectRoot: $pathFull"
    }

    return $pathFull
}

function Assert-Sha256 {
    param([string]$Path, [string]$Expected)

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

$observer = Assert-UnderRoot $ProjectRoot (Join-Path $ProjectRoot $ObserverRel)
$dependency = Assert-UnderRoot $ProjectRoot (Join-Path $ProjectRoot $DependencyRel)
$shader = Assert-UnderRoot $ProjectRoot (Join-Path $ProjectRoot $ShaderRel)
$editorScript = Assert-UnderRoot $ProjectRoot (Join-Path $ProjectRoot $EditorRel)
$outputExe = Assert-UnderRoot $ProjectRoot (Join-Path $ProjectRoot $OutputRel)
$buildLog = Assert-UnderRoot $ProjectRoot (Join-Path $ProjectRoot $LogRel)

Assert-Sha256 $observer $ExpectedObserverSha
Assert-Sha256 $dependency $ExpectedDependencySha
Assert-Sha256 $shader $ExpectedShaderSha

$unityExe = "C:\Program Files\Unity\Hub\Editor\6000.0.80f1\Editor\Unity.exe"

if (-not (Test-Path -LiteralPath $unityExe -PathType Leaf)) {
    throw "Unity 6000.0.80f1 not found: $unityExe"
}

if (Get-Process Unity -ErrorAction SilentlyContinue) {
    throw "Unity Editor is running. Close it before batch build."
}

$editorDir = Split-Path -Parent $editorScript
New-Item -ItemType Directory -Force -Path $editorDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $buildLog) | Out-Null

$editorSource = @'
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

public static class KiwiBuildV44_55_23
{
    public static void PerformBuild()
    {
        string projectRoot =
            Directory.GetParent(Application.dataPath).FullName;

        string observerPath =
            Path.Combine(
                Application.dataPath,
                "KiwiAvatarSystem/Runtime/Validation/KiwiProductionOutputTransactionAuthorityV44_55_23.cs");

        string dependencyPath =
            Path.Combine(
                Application.dataPath,
                "KiwiAvatarSystem/Runtime/Validation/KiwiCommonTensorBackendStageIsolationV44_55_20.cs");

        string shaderPath =
            Path.Combine(
                Application.dataPath,
                "KiwiAvatarSystem/Runtime/Validation/Resources/KiwiValidation/KiwiOutputSnapshotCopyV44_55_23.compute");

        if (!File.Exists(observerPath) ||
            !File.Exists(dependencyPath) ||
            !File.Exists(shaderPath))
        {
            throw new BuildFailedException(
                "v44.55.23 required audit file missing.");
        }

        GraphicsDeviceType[] apis =
            PlayerSettings.GetGraphicsAPIs(
                BuildTarget.StandaloneWindows64);

        if (apis == null ||
            apis.Length == 0 ||
            apis[0] != GraphicsDeviceType.Direct3D12)
        {
            throw new BuildFailedException(
                "DX12 is not first StandaloneWindows64 graphics API.");
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
                "No enabled build scene.");
        }

        Debug.Log(
            "[Kiwi v44.55.23 Build] PREFLIGHT_START mode=FULL bypass=0");

        KiwiReleaseCandidatePreflight.PreflightReport preflight =
            KiwiReleaseCandidatePreflight.RunFullPreflight(true);

        string validationDirectory =
            Path.Combine(
                projectRoot,
                "KiwiValidation");

        Directory.CreateDirectory(
            validationDirectory);

        string preflightPath =
            Path.Combine(
                validationDirectory,
                "KiwiPreflight_v44_55_23.json");

        File.WriteAllText(
            preflightPath,
            JsonUtility.ToJson(preflight, true));

        Debug.Log(
            "[Kiwi v44.55.23 Build] PREFLIGHT_SUMMARY" +
            " passed=" + (preflight.passed ? "1" : "0") +
            " errors=" + preflight.errorCount +
            " critical=" + preflight.criticalCount +
            " report=" + preflightPath);

        if (!preflight.passed ||
            preflight.errorCount != 0 ||
            preflight.criticalCount != 0)
        {
            throw new BuildFailedException(
                "v44.55.23 Full Preflight failed.");
        }

        if (!KiwiReleaseCandidatePreflight.HasCurrentPassingStamp(
                out string stampReason))
        {
            throw new BuildFailedException(
                "Passing stamp invalid: " +
                stampReason);
        }

        string output =
            Path.GetFullPath(
                Path.Combine(
                    projectRoot,
                    "Builds/v44_55_23ProductionOutputAuthority/KiwiAvatarSystem_v44_55_23_OUTPUT_AUTHORITY.exe"));

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

        BuildReport report =
            BuildPipeline.BuildPlayer(
                options);

        BuildSummary summary =
            report.summary;

        Debug.Log(
            "[Kiwi v44.55.23 Build] SUMMARY" +
            " result=" + summary.result +
            " errors=" + summary.totalErrors +
            " warnings=" + summary.totalWarnings +
            " output=" + output);

        if (summary.result != BuildResult.Succeeded ||
            summary.totalErrors != 0 ||
            !File.Exists(output))
        {
            throw new BuildFailedException(
                "v44.55.23 Development build failed.");
        }

        Debug.Log(
            "[Kiwi v44.55.23 Build] PASS output=" +
            output);
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

    $running = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $outputExe }

    if ($running) {
        throw "v44.55.23 output Player is still running. Close it before build."
    }

    if (Test-Path -LiteralPath $outputExe -PathType Leaf) {
        Remove-Item -LiteralPath $outputExe -Force
    }

    Write-Host ""
    Write-Host "Unity:   $unityExe"
    Write-Host "Project: $ProjectRoot"
    Write-Host "Output:  $outputExe"
    Write-Host "Log:     $buildLog"
    Write-Host ""

    $args = @(
        "-batchmode",
        "-quit",
        "-projectPath", $ProjectRoot,
        "-buildTarget", "Win64",
        "-executeMethod", "KiwiBuildV44_55_23.PerformBuild",
        "-logFile", $buildLog
    )

    $p = Start-Process `
        -FilePath $unityExe `
        -ArgumentList $args `
        -Wait `
        -PassThru `
        -NoNewWindow

    if ($p.ExitCode -ne 0) {
        Get-Content -LiteralPath $buildLog -Tail 140 -ErrorAction SilentlyContinue
        throw "Unity batch build failed."
    }

    if (-not (Test-Path -LiteralPath $outputExe -PathType Leaf)) {
        throw "Build PASS but output EXE missing."
    }

    $exeInfo = Get-Item -LiteralPath $outputExe
    $exeHash = (Get-FileHash -LiteralPath $outputExe -Algorithm SHA256).Hash.ToUpperInvariant()

    Write-Host ""
    Write-Host "BUILD PASS"
    Write-Host "EXE:    $($exeInfo.FullName)"
    Write-Host "Bytes:  $($exeInfo.Length)"
    Write-Host "SHA256: $exeHash"
}
finally {
    if ($null -ne $editorBackup) {
        [System.IO.File]::WriteAllBytes($editorScript, $editorBackup)
    }
    elseif (Test-Path -LiteralPath $editorScript -PathType Leaf) {
        Remove-Item -LiteralPath $editorScript -Force
    }

    if ($null -eq $editorBackup -and
        (Test-Path -LiteralPath $editorMeta -PathType Leaf)) {
        Remove-Item -LiteralPath $editorMeta -Force
    }
}
