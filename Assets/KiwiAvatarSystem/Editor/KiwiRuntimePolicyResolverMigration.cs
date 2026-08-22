#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 3 migration.
/// Converts KiwiTrackingQuality10Controller from a steady-state writer of
/// collision-prone Runner fields into a policy producer. RuntimePolicyResolver
/// becomes the single owner of the actual mutable tracking configuration.
/// </summary>
[InitializeOnLoad]
public static class KiwiRuntimePolicyResolverMigration
{
    private const string Quality10Path =
        "Assets/KiwiAvatarSystem/Runtime/Optimization/KiwiTrackingQuality10Controller.cs";

    private const string Marker =
        "KIWI_V5_1_RUNTIME_POLICY_SINGLE_WRITER";

    private static int _retryCount;

    static KiwiRuntimePolicyResolverMigration()
    {
        EditorApplication.delayCall += ApplyWhenReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Runtime Policy Single Writer")]
    private static void ApplyFromMenu()
    {
        _retryCount = 0;
        ApplyWhenReady();
    }

    private static void ApplyWhenReady()
    {
        if (!File.Exists(Quality10Path))
        {
            _retryCount++;
            if (_retryCount <= 8)
            {
                EditorApplication.delayCall += ApplyWhenReady;
            }
            return;
        }

        string original = File.ReadAllText(Quality10Path);
        string source = original.Replace("\r\n", "\n");

        if (source.Contains(Marker))
        {
            return;
        }

        const string widthOld =
            "        runner.trackingInputMaxWidth =\n" +
            "            auxiliaryMediaPipeInputWidth;\n";

        const string refreshOld =
            "        runner.sentisMediaPipeRefreshRateHz =\n" +
            "            auxiliaryMediaPipeRefreshHz;\n";

        const string presenceOld =
            "        runner.sentisMinimumPresence =\n" +
            "            inferencePresenceThreshold;\n";

        const string liveMethodStart =
            "    private void ApplyLiveInferenceTuning()\n";

        const string nextMethod =
            "    private void UpdatePipelineDiagnostics()\n";

        if (
            !source.Contains(widthOld) ||
            !source.Contains(refreshOld) ||
            !source.Contains(presenceOld) ||
            !source.Contains(liveMethodStart) ||
            !source.Contains(nextMethod)
        )
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] v5.1 Runtime Policy migration could not " +
                "locate the expected Quality10 anchors. No blind rewrite was " +
                "performed. The file was left unchanged.");
            return;
        }

        source = ReplaceFirst(
            source,
            widthOld,
            "        KiwiRuntimePolicyResolver.SubmitTrackingInputWidth(\n" +
            "            auxiliaryMediaPipeInputWidth,\n" +
            "            KiwiRuntimePolicyResolver.RequestPriority.Preset,\n" +
            "            \"Quality10Preset\");\n");

        source = ReplaceFirst(
            source,
            refreshOld,
            "        KiwiRuntimePolicyResolver.SubmitBaselineMediaPipeRefreshHz(\n" +
            "            auxiliaryMediaPipeRefreshHz,\n" +
            "            KiwiRuntimePolicyResolver.RequestPriority.Preset,\n" +
            "            \"Quality10Preset\");\n");

        source = ReplaceFirst(
            source,
            presenceOld,
            "        KiwiRuntimePolicyResolver.SubmitBaselinePresenceThreshold(\n" +
            "            inferencePresenceThreshold,\n" +
            "            KiwiRuntimePolicyResolver.RequestPriority.Preset,\n" +
            "            \"Quality10Preset\");\n");

        int liveStart = source.IndexOf(
            liveMethodStart,
            System.StringComparison.Ordinal);

        int nextStart = source.IndexOf(
            nextMethod,
            liveStart,
            System.StringComparison.Ordinal);

        if (liveStart < 0 || nextStart <= liveStart)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] v5.1 Runtime Policy migration could not " +
                "isolate ApplyLiveInferenceTuning. No file was changed.");
            return;
        }

        const string replacementMethod =
            "    private void ApplyLiveInferenceTuning()\n" +
            "    {\n" +
            "        // KIWI_V5_1_RUNTIME_POLICY_SINGLE_WRITER\n" +
            "        // Quality10 contributes a persistent preset request only.\n" +
            "        // Adaptive recovery and user overrides are arbitrated by\n" +
            "        // KiwiRuntimePolicyResolver, which alone writes Runner and\n" +
            "        // the already-created Inference tracker.\n" +
            "        if (_runner == null)\n" +
            "        {\n" +
            "            return;\n" +
            "        }\n\n" +
            "        KiwiRuntimePolicyResolver.SubmitBaselinePresenceThreshold(\n" +
            "            inferencePresenceThreshold,\n" +
            "            KiwiRuntimePolicyResolver.RequestPriority.Preset,\n" +
            "            \"Quality10LiveTuning\");\n" +
            "    }\n\n";

        source =
            source.Substring(0, liveStart) +
            replacementMethod +
            source.Substring(nextStart);

        WritePreservingFormat(
            Quality10Path,
            original,
            source);

        AssetDatabase.ImportAsset(
            Quality10Path,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] v5.1 Runtime Policy Single Writer applied " +
            "to KiwiTrackingQuality10Controller.");
    }

    private static string ReplaceFirst(
        string source,
        string oldValue,
        string newValue)
    {
        int index = source.IndexOf(
            oldValue,
            System.StringComparison.Ordinal);

        if (index < 0)
        {
            return source;
        }

        return
            source.Substring(0, index) +
            newValue +
            source.Substring(index + oldValue.Length);
    }

    private static void WritePreservingFormat(
        string path,
        string original,
        string normalized)
    {
        byte[] bytes = File.ReadAllBytes(path);

        bool hasBom =
            bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF;

        string lineEnding =
            original.Contains("\r\n")
                ? "\r\n"
                : "\n";

        if (lineEnding == "\r\n")
        {
            normalized = normalized.Replace("\n", "\r\n");
        }

        File.WriteAllText(
            path,
            normalized,
            new UTF8Encoding(hasBom));
    }
}
#endif
