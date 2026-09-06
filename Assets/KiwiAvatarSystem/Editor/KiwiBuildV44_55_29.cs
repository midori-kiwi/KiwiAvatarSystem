#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class KiwiBuildV44_55_29
{
    public const string Contract =
        "KIWI_V44_55_29_COMMAND_BUFFER_GRAPHICS_EXECUTION_FORM_ISOLATION";

    public static void Build()
    {
        string projectRoot =
            Directory.GetParent(Application.dataPath).FullName;

        string outputDirectory = Path.Combine(
            projectRoot,
            "Builds",
            "v44_55_29CommandBufferGraphicsExecutionFormIsolation");

        Directory.CreateDirectory(outputDirectory);

        string outputPath = Path.Combine(
            outputDirectory,
            "KiwiAvatarSystem_v44_55_29_COMMAND_BUFFER_GRAPHICS_EXECUTION_FORM_ISOLATION.exe");

        string[] scenes = EditorBuildSettings.scenes
            .Where(
                scene =>
                    scene != null &&
                    scene.enabled &&
                    !string.IsNullOrWhiteSpace(scene.path))
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            throw new InvalidOperationException(
                "No enabled scenes in EditorBuildSettings.");
        }

        Debug.Log(
            "[KiwiBuildV44_55_29] BUILD_BEGIN contract=" +
            Contract +
            " scenes=" + scenes.Length +
            " output=" + outputPath);

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new Exception(
                "v44.55.29 build failed: " +
                report.summary.result);
        }

        Debug.Log(
            "[KiwiBuildV44_55_29] BUILD_SUCCESS contract=" +
            Contract +
            " totalSize=" + report.summary.totalSize +
            " output=" + outputPath);
    }
}
#endif
