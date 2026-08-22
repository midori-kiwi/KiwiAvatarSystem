#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 9 guarded integration for KiwiFaceMotion.
///
/// ProviderHub already normalizes provider-native clocks onto local Stopwatch.
/// This migration only teaches the existing motion owner to invalidate velocity
/// history when the authoritative provider/timebase identity changes. It does
/// not alter Root ownership, filtering, gains, handedness math, or calibration.
/// </summary>
[InitializeOnLoad]
public static class KiwiTrackingNormalizationMigration
{
    private const string TargetPath =
        "Assets/Script/KiwiFaceMotion.cs";

    private const string Prerequisite =
        "KIWI_V5_1_PHASE7_ROOT_CALIBRATION_GENERATION";

    private const string Marker =
        "KIWI_V5_1_PHASE9_PROVIDER_TIMEBASE_GAP";

    private static int _retryCount;

    static KiwiTrackingNormalizationMigration()
    {
        EditorApplication.delayCall +=
            ApplyWhenPrerequisitesReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 9 Tracking Normalization")]
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

            if (_retryCount <= 12)
            {
                EditorApplication.delayCall +=
                    ApplyWhenPrerequisitesReady;
            }
            else
            {
                Debug.LogWarning(
                    "[KiwiAvatarSystem] Phase 9 waited for the Phase 7 " +
                    "KiwiFaceMotion marker, but it was not available. " +
                    "No blind source rewrite was performed.");
            }

            return;
        }

        string source =
            NormalizeNewlines(original);

        bool blocked = false;
        blocked |= !PatchTimingIdentityFields(ref source);
        blocked |= !PatchTimingIdentityCapture(ref source);
        blocked |= !PatchTimingDiscontinuity(ref source);
        blocked |= !PatchAcceptedIdentityWrites(ref source);

        if (blocked)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 9 could not uniquely locate one or " +
                "more validated KiwiFaceMotion timing anchors. The file was " +
                "left unchanged; no partial/blind rewrite was performed.");
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
            "[KiwiAvatarSystem] v5.1 Phase 9 integrated provider/timebase " +
            "discontinuity handling without changing rigid-pose authority.");
    }

    private static bool PatchTimingIdentityFields(
        ref string source)
    {
        const string anchor =
            "    private KiwiTrackingBackend _lastAcceptedBackend =\n" +
            "        KiwiTrackingBackend.Unknown;\n\n" +
            "    private float _lastSeenTime = -100f;\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "    private KiwiTrackingBackend _lastAcceptedBackend =\n" +
            "        KiwiTrackingBackend.Unknown;\n\n" +
            "    // KIWI_V5_1_PHASE9_PROVIDER_TIMEBASE_GAP\n" +
            "    // ProviderGeneration catches External->External authority\n" +
            "    // changes even when backend == Unknown. The timebase epoch and\n" +
            "    // quality prevent velocity from bridging a clock remap/fallback.\n" +
            "    private int _kiwiLastAcceptedProviderGeneration;\n" +
            "    private int _kiwiLastAcceptedTimebaseResetCount;\n" +
            "    private KiwiTrackingTimestampQuality\n" +
            "        _kiwiLastAcceptedTimestampQuality =\n" +
            "            KiwiTrackingTimestampQuality.ArrivalFallback;\n\n" +
            "    private float _lastSeenTime = -100f;\n";

        source =
            source.Replace(
                anchor,
                replacement);

        return true;
    }

    private static bool PatchTimingIdentityCapture(
        ref string source)
    {
        const string anchor =
            "        bool sampleUsesMatchedSubmissionTiming;\n" +
            "        long sampleHostTicks =\n" +
            "            GetPrecisionSampleHostTicks(\n" +
            "                precisionData,\n" +
            "                out sampleUsesMatchedSubmissionTiming\n" +
            "            );\n\n\n" +
            "        if (!_calibrated)\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "        bool sampleUsesMatchedSubmissionTiming;\n" +
            "        long sampleHostTicks =\n" +
            "            GetPrecisionSampleHostTicks(\n" +
            "                precisionData,\n" +
            "                out sampleUsesMatchedSubmissionTiming\n" +
            "            );\n\n" +
            "        KiwiCommercialRigidMotionPolicy.GetAuthoritativeTimingIdentity(\n" +
            "            out int kiwiProviderGeneration,\n" +
            "            out int kiwiTimebaseResetCount,\n" +
            "            out KiwiTrackingTimestampQuality kiwiTimestampQuality);\n\n\n" +
            "        if (!_calibrated)\n";

        source =
            source.Replace(
                anchor,
                replacement);

        return true;
    }

    private static bool PatchTimingDiscontinuity(
        ref string source)
    {
        const string anchor =
            "        bool backendChanged =\n" +
            "            _lastAcceptedBackend != KiwiTrackingBackend.Unknown &&\n" +
            "            precisionData.backend != KiwiTrackingBackend.Unknown &&\n" +
            "            precisionData.backend != _lastAcceptedBackend;\n\n\n" +
            "        bool predictionGap =\n" +
            "            backendChanged ||\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "        bool backendChanged =\n" +
            "            _lastAcceptedBackend != KiwiTrackingBackend.Unknown &&\n" +
            "            precisionData.backend != KiwiTrackingBackend.Unknown &&\n" +
            "            precisionData.backend != _lastAcceptedBackend;\n\n" +
            "        bool timingIdentityChanged =\n" +
            "            _kiwiLastAcceptedProviderGeneration > 0 &&\n" +
            "            (\n" +
            "                kiwiProviderGeneration !=\n" +
            "                    _kiwiLastAcceptedProviderGeneration ||\n" +
            "                kiwiTimebaseResetCount !=\n" +
            "                    _kiwiLastAcceptedTimebaseResetCount ||\n" +
            "                kiwiTimestampQuality !=\n" +
            "                    _kiwiLastAcceptedTimestampQuality\n" +
            "            );\n\n" +
            "        if (timingIdentityChanged)\n" +
            "        {\n" +
            "            // Never calculate velocity across unrelated clocks.\n" +
            "            // One nominal interval seeds the new identity; the\n" +
            "            // following real sample restores measured cadence.\n" +
            "            sampleInterval = 1f / 30f;\n" +
            "        }\n\n\n" +
            "        bool predictionGap =\n" +
            "            backendChanged ||\n" +
            "            timingIdentityChanged ||\n";

        source =
            source.Replace(
                anchor,
                replacement);

        return true;
    }

    private static bool PatchAcceptedIdentityWrites(
        ref string source)
    {
        const string anchor =
            "            _lastAcceptedBackend =\n" +
            "                precisionData.backend;\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "            _lastAcceptedBackend =\n" +
            "                precisionData.backend;\n\n" +
            "            _kiwiLastAcceptedProviderGeneration =\n" +
            "                kiwiProviderGeneration;\n" +
            "            _kiwiLastAcceptedTimebaseResetCount =\n" +
            "                kiwiTimebaseResetCount;\n" +
            "            _kiwiLastAcceptedTimestampQuality =\n" +
            "                kiwiTimestampQuality;\n";

        source =
            source.Replace(
                anchor,
                replacement);

        const string finalAnchor =
            "        _lastAcceptedBackend =\n" +
            "            precisionData.backend;\n\n\n" +
            "        _lastMotionSampleTime =\n";

        if (CountOccurrences(source, finalAnchor) != 1)
        {
            return false;
        }

        const string finalReplacement =
            "        _lastAcceptedBackend =\n" +
            "            precisionData.backend;\n\n" +
            "        _kiwiLastAcceptedProviderGeneration =\n" +
            "            kiwiProviderGeneration;\n" +
            "        _kiwiLastAcceptedTimebaseResetCount =\n" +
            "            kiwiTimebaseResetCount;\n" +
            "        _kiwiLastAcceptedTimestampQuality =\n" +
            "            kiwiTimestampQuality;\n\n\n" +
            "        _lastMotionSampleTime =\n";

        source =
            source.Replace(
                finalAnchor,
                finalReplacement);

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
