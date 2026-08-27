#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// KiwiAvatarSystem Phase16.20.23 v35.2 Windows Standalone build diagnostic.
///
/// Editor-only build helper. It does not modify runtime behavior.
///
/// Invoked from command line:
///   -executeMethod KiwiStandaloneBuildDiagnostic.BuildWindows64
///
/// Environment:
///   KIWI_V35_BUILD_OUTPUT=<absolute path to exe>
///
/// Build sequence:
/// 1. Resolve the release scene deterministically.
/// 2. Run the existing KiwiReleaseCandidatePreflight full preflight.
/// 3. Require PASS + releaseCandidateReady.
/// 4. Require the newly written passing fingerprint stamp to be current.
/// 5. Build Windows x64 with BuildOptions.None.
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
/// Build is intentionally NON-development / NON-autoconnect to avoid profiler
/// instrumentation during the standalone performance measurement.
/// </summary>
internal static class KiwiStandaloneBuildDiagnostic
{
    internal const string ContractMarker =
        "KIWI_V5_1_PHASE16_20_23_V35_2_WINDOWS_STANDALONE_PREFLIGHT_INTEGRATED";

    private const string OutputEnvironment =
        "KIWI_V35_BUILD_OUTPUT";

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
            "[KiwiV35Build] contract=" +
            ContractMarker);

        Debug.Log(
            "[KiwiV35Build] output=" +
            output);

        Debug.Log(
            "[KiwiV35Build] scenes=" +
            string.Join(
                ";",
                scenes));

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

                // No Development / Autoconnect / Deep Profiling.
                options =
                    BuildOptions.None
            };

        BuildReport report =
            BuildPipeline.BuildPlayer(
                options);

        BuildSummary summary =
            report.summary;

        Debug.Log(
            "[KiwiV35Build] result=" +
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
                "v35.2 Windows standalone build failed: " +
                summary.result +
                " errors=" +
                summary.totalErrors);
        }

        WriteBuildMeta(
            output,
            scenes,
            summary);
    }

    private static void RunRequiredFullPreflight(
        string outputDirectory)
    {
        Debug.Log(
            "[KiwiV35Build] Running mandatory Release Candidate Full Preflight before build.");

        KiwiReleaseCandidatePreflight.PreflightReport preflight =
            KiwiReleaseCandidatePreflight.RunFullPreflight(
                true);

        string reportPath =
            Path.Combine(
                outputDirectory,
                "KiwiStandalone_v35.preflight.json");

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
            "[KiwiV35Build] preflight passed=" +
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
            "[KiwiV35Build] mandatory preflight PASS; current fingerprint=" +
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
                "KiwiStandalone_v35.build.meta.txt");

        string content =
            "KiwiAvatarSystem v35.2 Windows Standalone Isolation" +
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
            "developmentBuild=0" +
            Environment.NewLine +
            "autoconnectProfiler=0" +
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
