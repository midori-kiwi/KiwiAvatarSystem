#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class KiwiBuildV44_55_31
{
    public static void Validate()
    {
        AssertHash("Assets/Script/FaceLandmarkerRunner.cs", "6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93");
        AssertHash("Assets/Script/KiwiFaceMotion.cs", "D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6");
        AssertHash("Assets/Plugins/x86_64/KiwiNativeCamera.dll", "82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5");
        const string shaderPath = "Assets/KiwiAvatarSystem/Resources/KiwiProducerTailSnapshotV44_55_31.compute";
        AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceUpdate);
        var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(shaderPath);
        if (shader == null || !shader.HasKernel("CopyPackedOutput")) throw new Exception("Snapshot kernel absent");
        foreach (var message in ShaderUtil.GetComputeShaderMessages(shader))
            if (message.severity.ToString() == "Error") throw new Exception(message.message);
        Debug.Log("[KiwiV31Build] STATIC_SHADER_PASS logicalWords=1405 headerWords=8");
    }
    public static void Build()
    {
        Validate();
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length != 1 || scenes[0] != "Assets/Scenes/Face Landmark Detection.unity")
            throw new Exception("Unexpected build scene");
        string root = Directory.GetParent(Application.dataPath).FullName;
        string directory = Path.Combine(root, "Builds", "v44_55_31AsyncProducerTailSnapshot");
        if (File.Exists(Path.Combine(directory, "KiwiAvatarSystem_v44_55_31.exe"))) throw new Exception("Build target already exists");
        Directory.CreateDirectory(directory);
        KiwiReleaseCandidatePreflight.PreflightReport preflight =
            KiwiReleaseCandidatePreflight.RunFullPreflight(true);
        if (preflight == null || !preflight.passed || !preflight.releaseCandidateReady)
            throw new Exception("Mandatory full preflight failed");
        if (!KiwiReleaseCandidatePreflight.HasCurrentPassingStamp(out string preflightReason))
            throw new Exception("Mandatory full preflight stamp is not current: " + preflightReason);
        Debug.Log(
            "[KiwiV31Build] FULL_PREFLIGHT_PASS fingerprint=" +
            preflight.fingerprint +
            " warning/error/critical=" +
            preflight.warningCount + "/" +
            preflight.errorCount + "/" +
            preflight.criticalCount);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = scenes, locationPathName = Path.Combine(directory, "KiwiAvatarSystem_v44_55_31.exe"),
            target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development | BuildOptions.StrictMode
        });
        if (report.summary.result != BuildResult.Succeeded || report.summary.totalErrors != 0)
            throw new Exception("v31 build failed: " + report.summary.result);
        Validate();
        Debug.Log("[KiwiV31Build] BUILD_SUCCESS errors=0 size=" + report.summary.totalSize);
    }
    private static void AssertHash(string path, string expected)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create())
            if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") != expected)
                throw new Exception("Protected SHA drift: " + path);
    }
}
#endif
