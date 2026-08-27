#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// KiwiAvatarSystem Phase16.20.27 v39 DX12 Windows Standalone diagnostic build helper.
///
/// Editor-only build helper. It does not modify runtime behavior.
///
/// Invoked from command line:
///   -executeMethod KiwiStandaloneBuildDiagnostic.BuildWindows64
///
/// Environment:
///   KIWI_V39_BUILD_OUTPUT=<absolute path to exe>
///
/// Build sequence:
/// 1. Resolve the release scene deterministically.
/// 2. Run the existing KiwiReleaseCandidatePreflight full preflight.
/// 3. Require PASS + releaseCandidateReady.
/// 4. Require the newly written passing fingerprint stamp to be current.
/// 5. Build Windows x64 with BuildOptions.Development.
///
/// The release build gate is NEVER bypassed or disabled. The normal
/// IPreprocessBuildWithReport gate still executes inside BuildPipeline.BuildPlayer
/// and independently verifies the same current passing stamp.
///
/// Scene selection:
/// 1. enabled EditorBuildSettings scenes;
/// 2. if none, exactly one scene asset whose filename is "Face Landmark Detection";
/// 3. otherwise fail rather than guessing.
///
/// Build is intentionally DEVELOPMENT but without Autoconnect/Deep Profiling/
/// Script Debugging. This is a diagnostic-only Player used to exclude the
/// Unity Editor process. It is NOT a release-performance baseline.
/// </summary>
internal static class KiwiStandaloneBuildDiagnostic
{
    internal const string ContractMarker =
        "KIWI_V5_1_PHASE16_20_27_V39_DX12_PLAYER_BUILD_API_LOCK";

    private const string OutputEnvironment =
        "KIWI_V39_BUILD_OUTPUT";

    public static void BuildWindows64()
    {
        string output =
            Environment.GetEnvironmentVariable(
                OutputEnvironment);

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(
                OutputEnvironment +
                " is not set.");
        }

        output =
            Path.GetFullPath(
                output.Trim());

        string outputDirectory =
            Path.GetDirectoryName(output);

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException(
                "Invalid build output path: " +
                output);
        }

        Directory.CreateDirectory(
            outputDirectory);

        string[] scenes =
            ResolveScenes();

        if (
            scenes == null ||
            scenes.Length == 0)
        {
            throw new InvalidOperationException(
                "No build scene could be resolved.");
        }

        Debug.Log(
            "[KiwiV39Build] contract=" +
            ContractMarker);

        Debug.Log(
            "[KiwiV39Build] output=" +
            output);

        Debug.Log(
            "[KiwiV39Build] scenes=" +
            string.Join(
                ";",
                scenes));

        EnsureDx12OnlyBuildGraphicsApi();

        RunRequiredFullPreflight(
            outputDirectory);

        BuildPlayerOptions options =
            new BuildPlayerOptions
            {
                scenes =
                    scenes,

                locationPathName =
                    output,

                target =
                    BuildTarget.StandaloneWindows64,

                targetGroup =
                    BuildTargetGroup.Standalone,

                // Development diagnostic. No Autoconnect / Deep Profiling / Script Debugging.
                options =
                    BuildOptions.Development
            };

        BuildReport report =
            BuildPipeline.BuildPlayer(
                options);

        BuildSummary summary =
            report.summary;

        Debug.Log(
            "[KiwiV39Build] result=" +
            summary.result +
            " totalSize=" +
            summary.totalSize +
            " warnings=" +
            summary.totalWarnings +
            " errors=" +
            summary.totalErrors);

        if (
            summary.result !=
            BuildResult.Succeeded)
        {
            throw new InvalidOperationException(
                "v39 Windows standalone build failed: " +
                summary.result +
                " errors=" +
                summary.totalErrors);
        }

        WriteBuildMeta(
            output,
            scenes,
            summary);
    }

    private static void EnsureDx12OnlyBuildGraphicsApi()
    {
        const BuildTarget target =
            BuildTarget.StandaloneWindows64;

        bool beforeAutomatic =
            PlayerSettings.GetUseDefaultGraphicsAPIs(
                target);

        GraphicsDeviceType[] beforeApis =
            PlayerSettings.GetGraphicsAPIs(
                target);

        Debug.Log(
            "[KiwiV39Build] graphics API before automatic=" +
            (beforeAutomatic ? "1" : "0") +
            " apis=" +
            JoinGraphicsApis(beforeApis));

        bool exactDx12Only =
            !beforeAutomatic &&
            beforeApis != null &&
            beforeApis.Length == 1 &&
            beforeApis[0] == GraphicsDeviceType.Direct3D12;

        if (!exactDx12Only)
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(
                target,
                false);

            PlayerSettings.SetGraphicsAPIs(
                target,
                new[]
                {
                    GraphicsDeviceType.Direct3D12
                });

            AssetDatabase.SaveAssets();
        }

        bool afterAutomatic =
            PlayerSettings.GetUseDefaultGraphicsAPIs(
                target);

        GraphicsDeviceType[] afterApis =
            PlayerSettings.GetGraphicsAPIs(
                target);

        bool verified =
            !afterAutomatic &&
            afterApis != null &&
            afterApis.Length == 1 &&
            afterApis[0] == GraphicsDeviceType.Direct3D12;

        Debug.Log(
            "[KiwiV39Build] graphics API after automatic=" +
            (afterAutomatic ? "1" : "0") +
            " apis=" +
            JoinGraphicsApis(afterApis) +
            " verified=" +
            (verified ? "1" : "0"));

        if (!verified)
        {
            throw new InvalidOperationException(
                "Failed to configure Windows Standalone build as Direct3D12-only.");
        }
    }

    private static string JoinGraphicsApis(
        GraphicsDeviceType[] apis)
    {
        if (
            apis == null ||
            apis.Length == 0)
        {
            return "(none)";
        }

        return string.Join(
            ",",
            apis.Select(
                api => api.ToString()));
    }

    private static void RunRequiredFullPreflight(
        string outputDirectory)
    {
        Debug.Log(
            "[KiwiV39Build] Running mandatory Release Candidate Full Preflight before build.");

        KiwiReleaseCandidatePreflight.PreflightReport preflight =
            KiwiReleaseCandidatePreflight.RunFullPreflight(
                true);

        string reportPath =
            Path.Combine(
                outputDirectory,
                "KiwiStandalone_v39.preflight.json");

        if (preflight != null)
        {
            File.WriteAllText(
                reportPath,
                JsonUtility.ToJson(
                    preflight,
                    true));
        }

        if (preflight == null)
        {
            throw new InvalidOperationException(
                "Mandatory Kiwi Release Candidate Full Preflight returned null.");
        }

        Debug.Log(
            "[KiwiV39Build] preflight passed=" +
            preflight.passed +
            " releaseCandidateReady=" +
            preflight.releaseCandidateReady +
            " warning/error/critical=" +
            preflight.warningCount +
            "/" +
            preflight.errorCount +
            "/" +
            preflight.criticalCount +
            " fingerprint=" +
            preflight.fingerprint);

        if (
            !preflight.passed ||
            !preflight.releaseCandidateReady)
        {
            throw new InvalidOperationException(
                "Mandatory Kiwi Release Candidate Full Preflight FAILED. " +
                "warning/error/critical=" +
                preflight.warningCount +
                "/" +
                preflight.errorCount +
                "/" +
                preflight.criticalCount +
                ". See " +
                reportPath);
        }

        if (
            !KiwiReleaseCandidatePreflight.TryGetCurrentPassingFingerprint(
                out string currentFingerprint,
                out string reason))
        {
            throw new InvalidOperationException(
                "Full Preflight reported PASS but no current passing fingerprint " +
                "is available: " +
                reason);
        }

        if (
            !string.Equals(
                currentFingerprint,
                preflight.fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Preflight fingerprint mismatch immediately after PASS. " +
                "report=" +
                preflight.fingerprint +
                " current=" +
                currentFingerprint);
        }

        Debug.Log(
            "[KiwiV39Build] mandatory preflight PASS; current fingerprint=" +
            currentFingerprint);
    }

    private static string[] ResolveScenes()
    {
        string[] enabled =
            EditorBuildSettings.scenes
                .Where(
                    scene =>
                        scene != null &&
                        scene.enabled &&
                        !string.IsNullOrWhiteSpace(
                            scene.path))
                .Select(
                    scene =>
                        scene.path)
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (enabled.Length > 0)
        {
            return enabled;
        }

        string[] guids =
            AssetDatabase.FindAssets(
                "t:Scene");

        List<string> exact =
            new List<string>();

        foreach (string guid in guids)
        {
            string path =
                AssetDatabase.GUIDToAssetPath(
                    guid);

            if (
                string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    "Face Landmark Detection",
                    StringComparison.OrdinalIgnoreCase))
            {
                exact.Add(path);
            }
        }

        if (exact.Count == 1)
        {
            return
                new[]
                {
                    exact[0]
                };
        }

        throw new InvalidOperationException(
            "No enabled build scenes and exact 'Face Landmark Detection' scene count=" +
            exact.Count +
            ". Refusing to guess.");
    }

    private static void WriteBuildMeta(
        string output,
        string[] scenes,
        BuildSummary summary)
    {
        string path =
            Path.Combine(
                Path.GetDirectoryName(output),
                "KiwiStandalone_v39.build.meta.txt");

        string content =
            "KiwiAvatarSystem v39 DX12 Development Standalone Diagnostic" +
            Environment.NewLine +
            "contract=" +
            ContractMarker +
            Environment.NewLine +
            "unityVersion=" +
            Application.unityVersion +
            Environment.NewLine +
            "output=" +
            output +
            Environment.NewLine +
            "buildTarget=StandaloneWindows64" +
            Environment.NewLine +
            "developmentBuild=1" +
            Environment.NewLine +
            "autoconnectProfiler=0" +
            Environment.NewLine +
            "deepProfiling=0" +
            Environment.NewLine +
            "scriptDebugging=0" +
            Environment.NewLine +
            "diagnosticOnly=1" +
            Environment.NewLine +
            "releaseCandidateArtifact=0" +
            Environment.NewLine +
            "windowsAutoGraphicsApi=0" +
            Environment.NewLine +
            "windowsGraphicsApis=Direct3D12" +
            Environment.NewLine +
            "dx12OnlyBuild=1" +
            Environment.NewLine +
            "mandatoryFullPreflight=1" +
            Environment.NewLine +
            "buildGateBypassed=0" +
            Environment.NewLine +
            "buildResult=" +
            summary.result +
            Environment.NewLine +
            "totalSize=" +
            summary.totalSize +
            Environment.NewLine +
            "warnings=" +
            summary.totalWarnings +
            Environment.NewLine +
            "errors=" +
            summary.totalErrors +
            Environment.NewLine +
            "scenes=" +
            string.Join(
                ";",
                scenes) +
            Environment.NewLine;

        File.WriteAllText(
            path,
            content);
    }
}
#endif
