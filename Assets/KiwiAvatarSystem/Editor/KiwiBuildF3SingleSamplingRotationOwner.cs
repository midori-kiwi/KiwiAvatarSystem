#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class KiwiBuildF3SingleSamplingRotationOwner
{
    private const string OutputEnvironment =
        "KIWI_F3_BUILD_OUTPUT";

    public static void Build()
    {
        string output =
            Environment.GetEnvironmentVariable(OutputEnvironment);

        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException(
                OutputEnvironment + " is not set.");

        output = Path.GetFullPath(output.Trim());
        string directory = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException(
                "Invalid F3 build output: " + output);

        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene != null && scene.enabled &&
                            !string.IsNullOrWhiteSpace(scene.path))
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length != 1 ||
            scenes[0] != "Assets/Scenes/Face Landmark Detection.unity")
        {
            throw new InvalidOperationException(
                "F3 requires exactly the Product-effective Face Landmark Detection scene.");
        }

        Directory.CreateDirectory(directory);
        BuildReport report = BuildPipeline.BuildPlayer(
            new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development | BuildOptions.StrictMode
            });

        if (report.summary.result != BuildResult.Succeeded ||
            report.summary.totalErrors != 0)
        {
            throw new InvalidOperationException(
                "F3 build failed: " + report.summary.result +
                " errors=" + report.summary.totalErrors);
        }

        UnityEngine.Debug.Log(
            "[F3_SINGLE_SAMPLING_ROTATION_OWNER] BUILD_SUCCESS output=" +
            output + " size=" + report.summary.totalSize);
    }
}
#endif
