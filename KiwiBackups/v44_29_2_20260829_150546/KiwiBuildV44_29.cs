#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class KiwiBuildV44_29
{
    public static void Build()
    {
        string projectRoot =
            Directory.GetParent(
                Application.dataPath).FullName;

        string outputDirectory =
            Path.Combine(
                projectRoot,
                "Builds",
                "v44_29LiveCameraMetadataCorrection");

        Directory.CreateDirectory(
            outputDirectory);

        string outputPath =
            Path.Combine(
                outputDirectory,
                "KiwiAvatarSystem_v44_29_COLOR_DIAG.exe");

        string[] scenes =
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
                .ToArray();

        if (scenes.Length == 0)
        {
            throw new InvalidOperationException(
                "No enabled scenes in EditorBuildSettings.");
        }

        BuildPlayerOptions options =
            new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName =
                    outputPath,
                target =
                    BuildTarget.StandaloneWindows64,
                options =
                    BuildOptions.Development
            };

        BuildReport report =
            BuildPipeline.BuildPlayer(
                options);

        if (
            report.summary.result !=
            BuildResult.Succeeded)
        {
            throw new Exception(
                "v44.29 build failed: " +
                report.summary.result);
        }

        Debug.Log(
            "[KiwiBuildV44_29] BUILD_SUCCESS " +
            "totalSize=" +
            report.summary.totalSize);
    }
}
#endif
