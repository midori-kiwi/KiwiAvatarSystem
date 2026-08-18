#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public sealed class KiwiAvatarBuildPostprocessor : IPostprocessBuildWithReport
{
    public int callbackOrder => 1000;

    public void OnPostprocessBuild(BuildReport report)
    {
        if (report == null ||
            (report.summary.platform != BuildTarget.StandaloneWindows &&
             report.summary.platform != BuildTarget.StandaloneWindows64))
        {
            return;
        }

        try
        {
            string buildPath = report.summary.outputPath;
            string buildDirectory = Path.GetDirectoryName(buildPath);

            if (string.IsNullOrEmpty(buildDirectory))
            {
                return;
            }

            string destinationModels = Path.Combine(buildDirectory, "Models");
            string destinationProfiles = Path.Combine(destinationModels, "Profiles");
            Directory.CreateDirectory(destinationModels);
            Directory.CreateDirectory(destinationProfiles);

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string sourceModels = Path.Combine(projectRoot, "Models");
            string sourceProfiles = Path.Combine(sourceModels, "Profiles");

            CopyMatchingFiles(sourceModels, destinationModels, ".vrm");
            CopyMatchingFiles(sourceProfiles, destinationProfiles, ".json");

            Debug.Log(
                "[KiwiAvatarSystem] Standalone Models folder prepared: " +
                destinationModels
            );
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Build completed, but Models folder preparation failed: " +
                ex.Message
            );
        }
    }

    private static void CopyMatchingFiles(
        string sourceDirectory,
        string destinationDirectory,
        string extension)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        string[] files = Directory.GetFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly);
        for (int i = 0; i < files.Length; i++)
        {
            string source = files[i];
            if (!string.Equals(
                Path.GetExtension(source),
                extension,
                StringComparison.OrdinalIgnoreCase
            ))
            {
                continue;
            }

            string destination = Path.Combine(
                destinationDirectory,
                Path.GetFileName(source)
            );

            string sourceFull = Path.GetFullPath(source);
            string destinationFull = Path.GetFullPath(destination);
            if (string.Equals(sourceFull, destinationFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(source, destination, true);
        }
    }
}
#endif
