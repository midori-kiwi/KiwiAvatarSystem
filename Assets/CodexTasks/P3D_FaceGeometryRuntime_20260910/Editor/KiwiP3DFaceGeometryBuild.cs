#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

internal static class KiwiP3DFaceGeometryBuild
{
    private const string OutputEnvironment = "KIWI_P3D_BUILD_OUTPUT";

    public static void BuildWindows64()
    {
        string output = Environment.GetEnvironmentVariable(OutputEnvironment);
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(OutputEnvironment + " is not set.");
        }
        output = Path.GetFullPath(output.Trim());
        Directory.CreateDirectory(Path.GetDirectoryName(output));

        if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64))
        {
            throw new InvalidOperationException("Windows graphics API selection is automatic; exact DX12 identity is required.");
        }
        GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        if (apis == null || apis.Length != 1 || apis[0] != GraphicsDeviceType.Direct3D12)
        {
            throw new InvalidOperationException("Windows build is not configured as exact Direct3D12-only.");
        }

        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene != null && scene.enabled && !string.IsNullOrWhiteSpace(scene.path))
            .Select(scene => scene.path)
            .ToArray();
        if (scenes.Length != 1 ||
            !string.Equals(scenes[0], "Assets/Scenes/Face Landmark Detection.unity", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Exact P3D build scene identity mismatch.");
        }

        KiwiReleaseCandidatePreflight.PreflightReport preflight =
            KiwiReleaseCandidatePreflight.RunFullPreflight(true);
        if (preflight == null || !preflight.passed || !preflight.releaseCandidateReady)
        {
            throw new InvalidOperationException(
                "Mandatory Release Candidate Full Preflight did not pass.");
        }
        if (!KiwiReleaseCandidatePreflight.TryGetCurrentPassingFingerprint(
                out string passingFingerprint,
                out string fingerprintReason) ||
            !string.Equals(
                passingFingerprint,
                preflight.fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Mandatory preflight fingerprint is not current: " +
                fingerprintReason);
        }
        Debug.Log("[KiwiP3DBuild] preflight=PASS fingerprint=" + passingFingerprint);

        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.Development
        });

        Debug.Log("[KiwiP3DBuild] result=" + report.summary.result +
            " errors=" + report.summary.totalErrors +
            " warnings=" + report.summary.totalWarnings +
            " size=" + report.summary.totalSize +
            " output=" + output);
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException("P3D Development build failed: " + report.summary.result);
        }
    }
}
#endif
