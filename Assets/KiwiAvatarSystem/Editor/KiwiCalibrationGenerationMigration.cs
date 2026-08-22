#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 7 guarded integration for the completed KiwiFaceMotion script.
///
/// The script remains the sole 3D Root/Head pose owner. This migration only
/// tags its existing calibration collection with CalibrationGeneration and
/// refuses a commit if a newer RootPose calibration epoch started meanwhile.
/// No tracking algorithm, pose filter or Transform owner is replaced.
/// </summary>
[InitializeOnLoad]
public static class KiwiCalibrationGenerationMigration
{
    private const string TargetPath =
        "Assets/Script/KiwiFaceMotion.cs";

    private const string Prerequisite =
        "KIWI_V4_7_COMMERCIAL_RIGID_PHASE_AUTHORITY";

    private const string Marker =
        "KIWI_V5_1_PHASE7_ROOT_CALIBRATION_GENERATION";

    private static int _retryCount;

    static KiwiCalibrationGenerationMigration()
    {
        EditorApplication.delayCall +=
            ApplyWhenPrerequisitesReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 7 Calibration Generation")]
    private static void ApplyFromMenu()
    {
        _retryCount = 0;
        ApplyWhenPrerequisitesReady();
    }

    private static void ApplyWhenPrerequisitesReady()
    {
        if (!File.Exists(TargetPath))
        {
            return;
        }

        string original =
            File.ReadAllText(TargetPath);

        if (original.IndexOf(
                Marker,
                StringComparison.Ordinal) >= 0)
        {
            return;
        }

        if (original.IndexOf(
                Prerequisite,
                StringComparison.Ordinal) < 0)
        {
            _retryCount++;

            if (_retryCount <= 10)
            {
                EditorApplication.delayCall +=
                    ApplyWhenPrerequisitesReady;
            }
            else
            {
                Debug.LogWarning(
                    "[KiwiAvatarSystem] Phase 7 waited for the v4.7 rigid " +
                    "authority marker in KiwiFaceMotion, but it was not " +
                    "available. No blind source rewrite was performed.");
            }

            return;
        }

        string source =
            NormalizeNewlines(original);

        bool blocked = false;

        blocked |= !PatchCalibrationFields(ref source);
        blocked |= !PatchBeginCalibration(ref source);
        blocked |= !PatchCollectionGuard(ref source);
        blocked |= !PatchFinishGuard(ref source);

        if (blocked)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 7 could not uniquely locate one or " +
                "more validated KiwiFaceMotion calibration anchors. The file " +
                "was left unchanged; no partial/blind rewrite was performed.");
            return;
        }

        WritePreservingFormat(
            TargetPath,
            original,
            source);

        AssetDatabase.ImportAsset(
            TargetPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] v5.1 Phase 7 integrated RootPose " +
            "CalibrationGeneration without changing tracking/presentation " +
            "authority.");
    }

    private static bool PatchCalibrationFields(
        ref string source)
    {
        const string anchor =
            "    private bool _calibrated;\n" +
            "    private bool _calibrationStarted;\n\n" +
            "    private float _calibrationStartTime;\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "    private bool _calibrated;\n" +
            "    private bool _calibrationStarted;\n\n" +
            "    // KIWI_V5_1_PHASE7_ROOT_CALIBRATION_GENERATION\n" +
            "    // The existing Root neutral collection is generation-tagged;\n" +
            "    // no second pose/calibration owner is introduced.\n" +
            "    private int _kiwiCalibrationGeneration;\n" +
            "    private bool _kiwiSuppressCalibrationGenerationAdvance;\n\n" +
            "    public int RuntimeCalibrationGeneration =>\n" +
            "        _kiwiCalibrationGeneration;\n\n" +
            "    public bool IsRuntimeCalibrated =>\n" +
            "        _calibrated;\n\n" +
            "    private float _calibrationStartTime;\n";

        source =
            source.Replace(
                anchor,
                replacement);

        return true;
    }

    private static bool PatchBeginCalibration(
        ref string source)
    {
        const string anchor =
            "    [ContextMenu(\"Recalibrate\")]\n" +
            "    public void BeginCalibration()\n" +
            "    {\n" +
            "        _calibrated =\n" +
            "            false;\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "    [ContextMenu(\"Recalibrate\")]\n" +
            "    public void BeginCalibration()\n" +
            "    {\n" +
            "        _kiwiCalibrationGeneration =\n" +
            "            _kiwiSuppressCalibrationGenerationAdvance\n" +
            "                ? KiwiCalibrationGeneration.CurrentGeneration\n" +
            "                : KiwiCalibrationGeneration.BeginOrJoin(\n" +
            "                    KiwiCalibrationScope.RootPose,\n" +
            "                    \"RootPose\");\n\n" +
            "        _calibrated =\n" +
            "            false;\n";

        source =
            source.Replace(
                anchor,
                replacement);

        return true;
    }

    private static bool PatchCollectionGuard(
        ref string source)
    {
        const string methodAnchor =
            "    private void AddCalibrationSample(\n" +
            "        Vector2 center,\n" +
            "        FacePrecisionTrackingData precisionData,\n" +
            "        bool hasPositionGeometry,\n" +
            "        PositionGeometry positionGeometry)\n" +
            "    {\n";

        if (CountOccurrences(source, methodAnchor) != 1)
        {
            return false;
        }

        const string helper =
            "    private void RestartCalibrationForCurrentGeneration()\n" +
            "    {\n" +
            "        _kiwiSuppressCalibrationGenerationAdvance = true;\n\n" +
            "        try\n" +
            "        {\n" +
            "            BeginCalibration();\n" +
            "        }\n" +
            "        finally\n" +
            "        {\n" +
            "            _kiwiSuppressCalibrationGenerationAdvance = false;\n" +
            "        }\n\n" +
            "        _kiwiCalibrationGeneration =\n" +
            "            KiwiCalibrationGeneration.CurrentGeneration;\n" +
            "    }\n\n\n";

        const string guard =
            "    private void AddCalibrationSample(\n" +
            "        Vector2 center,\n" +
            "        FacePrecisionTrackingData precisionData,\n" +
            "        bool hasPositionGeometry,\n" +
            "        PositionGeometry positionGeometry)\n" +
            "    {\n" +
            "        if (_kiwiCalibrationGeneration <= 0)\n" +
            "        {\n" +
            "            _kiwiCalibrationGeneration =\n" +
            "                KiwiCalibrationGeneration.CurrentGeneration;\n" +
            "        }\n" +
            "        else if (\n" +
            "            KiwiCalibrationGeneration.HasScopeChangedSince(\n" +
            "                _kiwiCalibrationGeneration,\n" +
            "                KiwiCalibrationScope.RootPose))\n" +
            "        {\n" +
            "            RestartCalibrationForCurrentGeneration();\n" +
            "            return;\n" +
            "        }\n\n";

        source =
            source.Replace(
                methodAnchor,
                helper + guard);

        return true;
    }

    private static bool PatchFinishGuard(
        ref string source)
    {
        const string finishAnchor =
            "    private void FinishCalibration()\n" +
            "    {\n";

        const string commitAnchor =
            "        _calibrated =\n" +
            "            true;\n";

        if (
            CountOccurrences(source, finishAnchor) != 1 ||
            CountOccurrences(source, commitAnchor) != 1)
        {
            return false;
        }

        const string finishReplacement =
            "    private void FinishCalibration()\n" +
            "    {\n" +
            "        if (\n" +
            "            KiwiCalibrationGeneration.HasScopeChangedSince(\n" +
            "                _kiwiCalibrationGeneration,\n" +
            "                KiwiCalibrationScope.RootPose))\n" +
            "        {\n" +
            "            RestartCalibrationForCurrentGeneration();\n" +
            "            return;\n" +
            "        }\n\n";

        const string commitReplacement =
            "        if (\n" +
            "            !KiwiCalibrationGeneration.TryRecordCommit(\n" +
            "                nameof(KiwiFaceMotion),\n" +
            "                _kiwiCalibrationGeneration,\n" +
            "                KiwiCalibrationScope.RootPose))\n" +
            "        {\n" +
            "            RestartCalibrationForCurrentGeneration();\n" +
            "            return;\n" +
            "        }\n\n" +
            "        _calibrated =\n" +
            "            true;\n";

        source =
            source.Replace(
                finishAnchor,
                finishReplacement);

        source =
            source.Replace(
                commitAnchor,
                commitReplacement);

        return true;
    }

    private static int CountOccurrences(
        string source,
        string value)
    {
        int count = 0;
        int index = 0;

        while (true)
        {
            index = source.IndexOf(
                value,
                index,
                StringComparison.Ordinal);

            if (index < 0)
            {
                return count;
            }

            count++;
            index += value.Length;
        }
    }

    private static string NormalizeNewlines(
        string source)
    {
        return source
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }

    private static void WritePreservingFormat(
        string path,
        string original,
        string normalized)
    {
        byte[] bytes =
            File.ReadAllBytes(path);

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
            normalized =
                normalized.Replace("\n", "\r\n");
        }

        File.WriteAllText(
            path,
            normalized,
            new UTF8Encoding(hasBom));
    }
}
#endif
