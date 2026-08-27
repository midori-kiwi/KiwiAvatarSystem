#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// KiwiAvatarSystem v42.1 root fix.
/// Owns the import/build contract for the native zero-copy bridge.
/// No .meta/YAML parsing is used. UnityEditor.PluginImporter is the sole source of truth.
/// </summary>
[InitializeOnLoad]
public static class KiwiV42PluginImportPolicy
{
    public const string BridgeAssetPath =
        "Assets/Plugins/KiwiOrtDirectML/x86_64/KiwiOrtDmlZeroCopyBridge.dll";

    private const string ResultEnvironmentVariable =
        "KIWI_V42_1_PLUGIN_POLICY_RESULT";

    private static bool _delayCallScheduled;
    private static bool _enforcing;

    static KiwiV42PluginImportPolicy()
    {
        ScheduleEnforce();
    }

    public struct PolicyState
    {
        public bool importerExists;
        public bool nativePlugin;
        public bool preloaded;
        public bool anyPlatform;
        public bool editorCompatible;
        public bool windows64Compatible;
        public string cpu;

        public bool IsValid
        {
            get
            {
                return importerExists &&
                    nativePlugin &&
                    preloaded &&
                    !anyPlatform &&
                    !editorCompatible &&
                    windows64Compatible;
            }
        }
    }

    internal static void ScheduleEnforce()
    {
        if (_delayCallScheduled)
            return;

        _delayCallScheduled = true;
        EditorApplication.delayCall += DelayedEnforce;
    }

    private static void DelayedEnforce()
    {
        _delayCallScheduled = false;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            ScheduleEnforce();
            return;
        }

        try
        {
            ApplyAndValidateInternal(false);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[KiwiInferenceV42.1] plugin import policy could not be enforced: " +
                ex.Message);
        }
    }

    public static void ApplyAndValidateBatch()
    {
        ExecuteBatch(true);
    }

    public static void ValidateBatch()
    {
        ExecuteBatch(false);
    }

    public static PolicyState ApplyAndValidateInternal(bool logSuccess)
    {
        if (_enforcing)
            return ReadState();

        _enforcing = true;
        try
        {
            AssetDatabase.ImportAsset(
                BridgeAssetPath,
                ImportAssetOptions.ForceSynchronousImport |
                ImportAssetOptions.ForceUpdate);

            PluginImporter importer =
                AssetImporter.GetAtPath(BridgeAssetPath) as PluginImporter;
            if (importer == null)
                throw new InvalidOperationException(
                    "Bridge is not handled by PluginImporter: " + BridgeAssetPath);
            if (!importer.isNativePlugin)
                throw new InvalidOperationException(
                    "Bridge is not recognized as a native plugin: " + BridgeAssetPath);

            bool changed = false;

            if (!importer.isPreloaded)
            {
                importer.isPreloaded = true;
                changed = true;
            }

            if (importer.GetCompatibleWithAnyPlatform())
            {
                importer.SetCompatibleWithAnyPlatform(false);
                changed = true;
            }

            if (importer.GetCompatibleWithEditor())
            {
                importer.SetCompatibleWithEditor(false);
                changed = true;
            }

            if (!importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64))
            {
                importer.SetCompatibleWithPlatform(
                    BuildTarget.StandaloneWindows64,
                    true);
                changed = true;
            }

            string cpu = importer.GetPlatformData(
                BuildTarget.StandaloneWindows64,
                "CPU");
            if (!string.Equals(cpu, "x86_64", StringComparison.OrdinalIgnoreCase))
            {
                importer.SetPlatformData(
                    BuildTarget.StandaloneWindows64,
                    "CPU",
                    "x86_64");
                changed = true;
            }

            if (changed)
            {
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
            }

            PolicyState state = ReadState();
            if (!state.IsValid)
                throw new BuildFailedException(
                    "Kiwi v42.1 native plugin policy verification failed. " +
                    Describe(state));

            if (logSuccess)
            {
                Debug.Log(
                    "[KiwiInferenceV42.1] PluginImporter policy PASS " +
                    Describe(state));
            }

            return state;
        }
        finally
        {
            _enforcing = false;
        }
    }

    public static PolicyState ReadState()
    {
        PluginImporter importer =
            AssetImporter.GetAtPath(BridgeAssetPath) as PluginImporter;

        PolicyState state = default;
        state.importerExists = importer != null;
        if (importer == null)
            return state;

        state.nativePlugin = importer.isNativePlugin;
        state.preloaded = importer.isPreloaded;
        state.anyPlatform = importer.GetCompatibleWithAnyPlatform();
        state.editorCompatible = importer.GetCompatibleWithEditor();
        state.windows64Compatible =
            importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64);
        state.cpu = importer.GetPlatformData(
            BuildTarget.StandaloneWindows64,
            "CPU");
        return state;
    }

    public static string Describe(PolicyState state)
    {
        return
            "importer=" + (state.importerExists ? "1" : "0") +
            " native=" + (state.nativePlugin ? "1" : "0") +
            " preloaded=" + (state.preloaded ? "1" : "0") +
            " anyPlatform=" + (state.anyPlatform ? "1" : "0") +
            " editor=" + (state.editorCompatible ? "1" : "0") +
            " win64=" + (state.windows64Compatible ? "1" : "0") +
            " cpu=" + (state.cpu ?? string.Empty) +
            " unity=" + Application.unityVersion;
    }

    private static void ExecuteBatch(bool apply)
    {
        string resultPath =
            Environment.GetEnvironmentVariable(ResultEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(resultPath))
            throw new InvalidOperationException(
                ResultEnvironmentVariable + " is not set.");

        try
        {
            PolicyState state = apply
                ? ApplyAndValidateInternal(true)
                : ReadState();

            if (!state.IsValid)
                throw new BuildFailedException(
                    "Kiwi v42.1 native plugin policy is invalid. " + Describe(state));

            File.WriteAllText(
                resultPath,
                "status=PASS\n" +
                "mode=" + (apply ? "APPLY" : "VALIDATE") + "\n" +
                Describe(state) + "\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                resultPath,
                "status=FAIL\n" +
                "mode=" + (apply ? "APPLY" : "VALIDATE") + "\n" +
                "error=" + ex + "\n");
            throw;
        }
    }
}

public sealed class KiwiV42PluginImportPostprocessor : AssetPostprocessor
{
    private static void OnPostprocessAllAssets(
        string[] importedAssets,
        string[] deletedAssets,
        string[] movedAssets,
        string[] movedFromAssetPaths)
    {
        for (int i = 0; i < importedAssets.Length; i++)
        {
            if (string.Equals(
                importedAssets[i],
                KiwiV42PluginImportPolicy.BridgeAssetPath,
                StringComparison.OrdinalIgnoreCase))
            {
                KiwiV42PluginImportPolicy.ScheduleEnforce();
                return;
            }
        }
    }
}

public sealed class KiwiV42PluginBuildGuard : IPreprocessBuildWithReport
{
    public int callbackOrder => -10000;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.StandaloneWindows64)
            return;

        KiwiV42PluginImportPolicy.PolicyState state =
            KiwiV42PluginImportPolicy.ApplyAndValidateInternal(true);
        if (!state.IsValid)
            throw new BuildFailedException(
                "Kiwi v42.1 zero-copy bridge import policy is invalid before build. " +
                KiwiV42PluginImportPolicy.Describe(state));
    }
}
#endif
