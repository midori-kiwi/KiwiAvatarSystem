#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Phase 16.17 commercial processing consolidation.
///
/// KiwiTrackingQuality10Controller historically became a second temporal
/// presenter for the exact same Root written by KiwiFaceMotion. Later phases
/// intentionally concentrated no-frame hold, static-rest, provider bridging and
/// the final discontinuity envelope inside KiwiFaceMotion, so allowing the old
/// Quality10 presenter to run afterwards creates two temporal owners.
///
/// This migration preserves all Quality10 policy / QoS / face-part preset code,
/// preserves the legacy temporal presenter behind a rollback flag, but makes
/// policy-only mode the default. KiwiFaceMotion is therefore the sole normal
/// runtime Root temporal-presentation writer.
/// </summary>
[InitializeOnLoad]
public static class KiwiPhase16_17SinglePresentationAuthorityMigration
{
    private const string TargetPath =
        "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
        "KiwiTrackingQuality10Controller.cs";

    private const string Prerequisite =
        "KIWI_V5_1_RUNTIME_POLICY_SINGLE_WRITER";

    public const string Marker =
        "KIWI_V5_1_PHASE16_17_SINGLE_PRESENTATION_AUTHORITY";

    private static int _retryCount;

    static KiwiPhase16_17SinglePresentationAuthorityMigration()
    {
        EditorApplication.delayCall += ApplyWhenReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 16.17 Single Presentation Authority")]
    private static void ApplyFromMenu()
    {
        _retryCount = 0;
        ApplyWhenReady();
    }

    private static void ApplyWhenReady()
    {
        if (!File.Exists(TargetPath))
        {
            Retry();
            return;
        }

        string originalDisk = File.ReadAllText(TargetPath);
        string source = NormalizeNewlines(originalDisk);

        if (HasCompletePatch(source))
        {
            return;
        }

        // RuntimePolicyResolver migration can run in the same import cycle.
        // Wait for it instead of patching an older Quality10 ownership shape.
        if (!source.Contains(Prerequisite))
        {
            Retry();
            return;
        }

        if (source.Contains(Marker))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase16.17 marker exists but the complete " +
                "single-presentation contract is missing. No partial rewrite " +
                "was performed.");
            return;
        }

        if (!PatchState(ref source) ||
            !PatchLateUpdate(ref source) ||
            !PatchBeforeRender(ref source) ||
            !PatchLegacyWriteAudit(ref source) ||
            !PatchFaceMotionPreset(ref source) ||
            !PatchDiagnosticsHelper(ref source))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase16.17 could not locate one or more " +
                "expected Quality10 anchors. The file was left unchanged.");
            return;
        }

        if (!HasCompletePatch(source))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase16.17 patch failed its complete " +
                "contract check. The file was left unchanged.");
            return;
        }

        WritePreservingFormat(TargetPath, originalDisk, source);

        AssetDatabase.ImportAsset(
            TargetPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] Phase16.17 Single Presentation Authority " +
            "applied. Quality10 is policy/telemetry-only by default; " +
            "KiwiFaceMotion remains the sole Root temporal presenter.");
    }

    private static void Retry()
    {
        _retryCount++;
        if (_retryCount <= 20)
        {
            EditorApplication.delayCall += ApplyWhenReady;
        }
    }

    private static bool PatchState(ref string source)
    {
        const string anchor =
            "    private const string RuntimeObjectName =\n";

        int index = source.IndexOf(anchor, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        const string block =
            "    // KIWI_V5_1_PHASE16_17_SINGLE_PRESENTATION_AUTHORITY\n" +
            "    [Header(\"Phase 16.17 Single Presentation Authority\")]\n" +
            "    [Tooltip(\"Commercial default. Keep Quality10 as policy/telemetry only so KiwiFaceMotion is the sole temporal writer of the rigid Root. Disable only for rollback testing.\")]\n" +
            "    public bool phase16_17PolicyOnlyPresentation = true;\n\n" +
            "    public bool Phase16_17PolicyOnlyPresentation =>\n" +
            "        phase16_17PolicyOnlyPresentation;\n\n";

        source = source.Insert(index, block);
        return true;
    }

    private static bool PatchLateUpdate(ref string source)
    {
        const string signature =
            "    private void LateUpdate()\n";

        if (!TryGetMethodRange(source, signature, out int start, out int end))
        {
            return false;
        }

        string method = source.Substring(start, end - start);
        const string anchor =
            "        CaptureNewTrackingSample();\n" +
            "        PresentAtRenderTime();\n";

        int anchorIndex = method.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            return false;
        }

        const string replacement =
            "        if (phase16_17PolicyOnlyPresentation)\n" +
            "        {\n" +
            "            ReportPhase16_17PolicyState();\n" +
            "            KiwiPhase16_17PresentationAuthorityDiagnostics.\n" +
            "                ReportSuppressedLateUpdate();\n" +
            "            return;\n" +
            "        }\n\n" +
            "        CaptureNewTrackingSample();\n" +
            "        PresentAtRenderTime();\n";

        method = ReplaceFirst(method, anchor, replacement);
        source = source.Substring(0, start) + method + source.Substring(end);
        return true;
    }

    private static bool PatchBeforeRender(ref string source)
    {
        const string signature =
            "    private void HandleBeforeRender()\n";

        if (!TryGetMethodRange(source, signature, out int start, out int end))
        {
            return false;
        }

        string method = source.Substring(start, end - start);
        const string anchor =
            "        RefreshReferences(false);\n";

        int anchorIndex = method.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            return false;
        }

        const string replacement =
            "        RefreshReferences(false);\n\n" +
            "        if (phase16_17PolicyOnlyPresentation)\n" +
            "        {\n" +
            "            ReportPhase16_17PolicyState();\n" +
            "            KiwiPhase16_17PresentationAuthorityDiagnostics.\n" +
            "                ReportSuppressedBeforeRender();\n" +
            "            return;\n" +
            "        }\n";

        method = ReplaceFirst(method, anchor, replacement);
        source = source.Substring(0, start) + method + source.Substring(end);
        return true;
    }

    private static bool PatchLegacyWriteAudit(ref string source)
    {
        const string signature =
            "    private void PresentAtRenderTime()\n";

        if (!TryGetMethodRange(source, signature, out int start, out int end))
        {
            return false;
        }

        string method = source.Substring(start, end - start);
        const string anchor =
            "        _motionRoot.localPosition =\n" +
            "            _renderPosition;\n";

        int anchorIndex = method.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            return false;
        }

        const string insert =
            "        KiwiPhase16_17PresentationAuthorityDiagnostics.\n" +
            "            ReportLegacyRootWrite(\n" +
            "                phase16_17PolicyOnlyPresentation,\n" +
            "                _faceMotion != null &&\n" +
            "                _motionRoot == _faceMotion.kiwiRoot);\n\n";

        method = method.Insert(anchorIndex, insert);
        source = source.Substring(0, start) + method + source.Substring(end);
        return true;
    }

    private static bool PatchFaceMotionPreset(ref string source)
    {
        const string signature =
            "    private static void ApplyFaceMotionPreset(\n";

        if (!TryGetMethodRange(source, signature, out int start, out int end))
        {
            return false;
        }

        string method = source.Substring(start, end - start);

        if (!method.Contains("motion.ultraStaticPoseLock = false;") ||
            !method.Contains("motion.ultraDisplayRateSmoothing = false;") ||
            !method.Contains("motion.ultraPredictionStrength = 0f;") ||
            !method.Contains("motion.enableRenderTimeLatePrediction = false;"))
        {
            return false;
        }

        method = method.Replace(
            "motion.ultraStaticPoseLock = false;",
            "motion.ultraStaticPoseLock = true;");

        method = method.Replace(
            "motion.ultraDisplayRateSmoothing = false;",
            "motion.ultraDisplayRateSmoothing = true;");

        // The final static-rest latch introduced in Phase16.13 owns the rest
        // corridor. Avoid a second sample-domain micro hold underneath it.
        method = method.Replace(
            "motion.ultraAdaptiveMicroFilter = false;",
            "motion.ultraAdaptiveMicroFilter = false;");

        source = source.Substring(0, start) + method + source.Substring(end);
        return true;
    }

    private static bool PatchDiagnosticsHelper(ref string source)
    {
        const string anchor =
            "    private void ApplyRunnerPreset(\n";

        int index = source.IndexOf(anchor, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        const string helper =
            "    private void ReportPhase16_17PolicyState()\n" +
            "    {\n" +
            "        bool sharedRoot =\n" +
            "            _faceMotion != null &&\n" +
            "            _motionRoot != null &&\n" +
            "            _motionRoot == _faceMotion.kiwiRoot;\n\n" +
            "        KiwiPhase16_17PresentationAuthorityDiagnostics.\n" +
            "            ReportPolicyState(\n" +
            "                phase16_17PolicyOnlyPresentation,\n" +
            "                sharedRoot,\n" +
            "                _faceMotion);\n" +
            "    }\n\n";

        source = source.Insert(index, helper);
        return true;
    }

    private static bool HasCompletePatch(string source)
    {
        return
            source.Contains(Marker) &&
            source.Contains("phase16_17PolicyOnlyPresentation") &&
            source.Contains("ReportSuppressedLateUpdate") &&
            source.Contains("ReportSuppressedBeforeRender") &&
            source.Contains("ReportLegacyRootWrite") &&
            source.Contains("ReportPhase16_17PolicyState") &&
            source.Contains("motion.ultraStaticPoseLock = true;") &&
            source.Contains("motion.ultraDisplayRateSmoothing = true;") &&
            source.Contains("motion.ultraPredictionStrength = 0f;") &&
            source.Contains("motion.enableRenderTimeLatePrediction = false;");
    }

    private static bool TryGetMethodRange(
        string source,
        string signature,
        out int start,
        out int end)
    {
        start = source.IndexOf(signature, StringComparison.Ordinal);
        end = -1;

        if (start < 0)
        {
            return false;
        }

        int brace = source.IndexOf('{', start);
        if (brace < 0)
        {
            return false;
        }

        int depth = 0;
        for (int i = brace; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    end = i + 1;
                    return true;
                }
            }
        }

        return false;
    }

    private static string ReplaceFirst(
        string source,
        string oldValue,
        string newValue)
    {
        int index = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0)
        {
            return source;
        }

        return
            source.Substring(0, index) +
            newValue +
            source.Substring(index + oldValue.Length);
    }

    private static string NormalizeNewlines(string value)
    {
        return value.Replace("\r\n", "\n").Replace("\r", "\n");
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
