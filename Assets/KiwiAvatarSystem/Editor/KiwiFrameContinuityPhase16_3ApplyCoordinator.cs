#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 16.4 migration-application coordinator.
///
/// Phase 16.3b synchronous verification is retained. Phase 16.4 additionally
/// verifies that Tracking Holding keeps prediction at zero while the existing
/// display resampler continues converging toward the last accepted rigid pose.
/// </summary>
[InitializeOnLoad]
public static class KiwiFrameContinuityPhase16_3ApplyCoordinator
{
    private const string TargetPath =
        "Assets/Script/KiwiFaceMotion.cs";

    private const string MarkerPhase7 =
        "KIWI_V5_1_PHASE7_ROOT_CALIBRATION_GENERATION";

    private const string MarkerPhase9 =
        "KIWI_V5_1_PHASE9_PROVIDER_TIMEBASE_GAP";

    private const string MarkerPhase16 =
        "KIWI_V5_1_PHASE16_FRAME_CONTINUITY_GUARD";

    private const string MarkerPhase16_2 =
        "KIWI_V5_1_PHASE16_2_MEASURED_CONTINUITY_GUARD";

    public const string MarkerPhase16_3 =
        "KIWI_V5_1_PHASE16_3_CONTINUITY_APPLY_CONFIRMED";

    private const string MarkerPhase16_4 =
        "KIWI_V5_1_PHASE16_4_PRESENTATION_HOLD_RESAMPLING";

    private static bool _queued;

    static KiwiFrameContinuityPhase16_3ApplyCoordinator()
    {
        QueuePass();
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Repair + Verify v5.1 Phase 16.4 Frame Continuity")]
    private static void RepairFromMenu()
    {
        QueuePass();
    }

    private static void QueuePass()
    {
        if (_queued)
        {
            return;
        }

        _queued = true;
        EditorApplication.delayCall += RunPass;
    }

    private static void RunPass()
    {
        _queued = false;

        if (
            EditorApplication.isCompiling ||
            EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode
        )
        {
            QueuePass();
            return;
        }

        if (!File.Exists(TargetPath))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase 16.4 cannot verify continuity because " +
                "KiwiFaceMotion.cs was not found at " + TargetPath + ".");
            return;
        }

        string source = ReadSource();

        if (!source.Contains(MarkerPhase7))
        {
            if (!ExecuteAndVerifyMigration(
                    "Tools/Kiwi Avatar System/Apply v5.1 Phase 7 Calibration Generation",
                    MarkerPhase7,
                    out string phase7Failure))
            {
                LogStopped("Phase 7", phase7Failure);
                return;
            }

            QueuePass();
            return;
        }

        if (!source.Contains(MarkerPhase9))
        {
            if (!ExecuteAndVerifyMigration(
                    "Tools/Kiwi Avatar System/Apply v5.1 Phase 9 Tracking Normalization",
                    MarkerPhase9,
                    out string phase9Failure))
            {
                LogStopped("Phase 9", phase9Failure);
                return;
            }

            QueuePass();
            return;
        }

        if (!source.Contains(MarkerPhase16))
        {
            if (!ExecuteAndVerifyMigration(
                    "Tools/Kiwi Avatar System/Apply v5.1 Phase 16 Frame Continuity Guard",
                    MarkerPhase16,
                    out string phase16Failure))
            {
                LogStopped("Phase 16", phase16Failure);
                return;
            }

            QueuePass();
            return;
        }

        source = ReadSource();

        string missingPhase16Core = FindMissingPhase16CoreToken(source);
        if (!string.IsNullOrEmpty(missingPhase16Core))
        {
            LogStopped(
                "Phase 16 core verification",
                "The Phase 16 marker exists but required token is missing: " +
                missingPhase16Core + ". No Phase 16.2 partial rewrite was attempted.");
            return;
        }

        if (!source.Contains(MarkerPhase16_2))
        {
            if (!KiwiFrameContinuityPhase16_2Migration.TryApplyNow(
                    out string phase16_2Failure))
            {
                LogStopped(
                    "Phase 16.2",
                    phase16_2Failure);
                return;
            }

            source = ReadSource();
            if (!source.Contains(MarkerPhase16_2))
            {
                LogStopped(
                    "Phase 16.2",
                    "migration returned success but the expected marker was not " +
                    "present in KiwiFaceMotion.cs.");
                return;
            }

            QueuePass();
            return;
        }

        source = ReadSource();

        string missingToken = FindMissingContinuityToken(source);
        if (!string.IsNullOrEmpty(missingToken))
        {
            RemoveInvalidConfirmationMarkerIfPresent(source);

            LogStopped(
                "Phase 16.3 final verification",
                "KiwiFaceMotion is missing required token: " + missingToken + ".");
            return;
        }

        if (!source.Contains(MarkerPhase16_3))
        {
            string stamped = AddConfirmationMarker(source);
            if (stamped == source)
            {
                LogStopped(
                    "Phase 16.3 confirmation",
                    "could not place the confirmation marker beside the validated " +
                    "Phase 16.2 marker.");
                return;
            }

            WriteSource(source, stamped);
            AssetDatabase.ImportAsset(
                TargetPath,
                ImportAssetOptions.ForceUpdate);
        }

        source = ReadSource();

        if (!source.Contains(MarkerPhase16_4))
        {
            if (!KiwiFrameContinuityPhase16_4PresentationHoldMigration.TryApplyNow(
                    out string phase16_4Failure))
            {
                LogStopped(
                    "Phase 16.4",
                    phase16_4Failure);
                return;
            }

            source = ReadSource();
            if (!source.Contains(MarkerPhase16_4))
            {
                LogStopped(
                    "Phase 16.4",
                    "migration returned success but the expected presentation-hold " +
                    "marker was not present in KiwiFaceMotion.cs.");
                return;
            }

            QueuePass();
            return;
        }

        if (!KiwiFrameContinuityPhase16_4PresentationHoldMigration.HasCompletePhase16_4(
                source,
                out string phase16_4VerificationFailure))
        {
            LogStopped(
                "Phase 16.4 final verification",
                phase16_4VerificationFailure);
            return;
        }

        EnsureLoadedSceneContinuityGuardEnabled();

        Debug.Log(
            "[KiwiAvatarSystem] v5.1 Phase 16.4 confirmed Phase 7/9/16/16.2/16.3 " +
            "continuity plus Holding presentation resampling. Prediction remains " +
            "zero while Tracking is Holding; no second Root writer was added.");
    }

    private static bool ExecuteAndVerifyMigration(
        string menuPath,
        string expectedMarker,
        out string failure)
    {
        failure = string.Empty;

        if (!EditorApplication.ExecuteMenuItem(menuPath))
        {
            failure = "Unity could not invoke menu item: " + menuPath + ".";
            return false;
        }

        string source = ReadSource();
        if (!source.Contains(expectedMarker))
        {
            failure =
                "migration menu executed, but KiwiFaceMotion.cs still does not " +
                "contain " + expectedMarker + ". Inspect the migration-specific " +
                "warning immediately above this error.";
            return false;
        }

        return true;
    }

    private static string FindMissingPhase16CoreToken(string source)
    {
        string[] required =
        {
            "enableUltraFrameContinuityGuard",
            "_kiwiFrameContinuityGuardUntilFrame",
            "private float GetFrameContinuityMeasuredTrackingRateHz()",
            "ApplyFrameContinuityStepCaps(",
            "IsFrameContinuityDirectBypassSuppressed()",
            "KiwiFrameContinuityDiagnostics.Report("
        };

        for (int i = 0; i < required.Length; i++)
        {
            if (!source.Contains(required[i]))
            {
                return required[i];
            }
        }

        return string.Empty;
    }

    private static string FindMissingContinuityToken(string source)
    {
        string[] required =
        {
            "enableUltraFrameContinuityGuard",
            "ultraFrameContinuityMaxPositionSpeedHeightsPerSecond",
            "ultraFrameContinuityEmergencyPositionErrorHeights",
            "_kiwiFrameContinuityObservedAcceptedInterval",
            "RecordFrameContinuityAcceptedRealtime();",
            "private void RecordFrameContinuityAcceptedRealtime()",
            "ArmFrameContinuityMagnitudeGuard(",
            "bool kiwiContinuityProtectionReady =",
            "ApplyFrameContinuityStepCaps(",
            "IsFrameContinuityDirectBypassSuppressed()",
            "KiwiFrameContinuityDiagnostics.Report("
        };

        for (int i = 0; i < required.Length; i++)
        {
            if (!source.Contains(required[i]))
            {
                return required[i];
            }
        }

        return string.Empty;
    }

    private static void RemoveInvalidConfirmationMarkerIfPresent(string source)
    {
        if (!source.Contains(MarkerPhase16_3))
        {
            return;
        }

        string normalized = NormalizeNewlines(source);
        string markerLine = "// " + MarkerPhase16_3;
        string[] lines = normalized.Split('\n');
        bool removed = false;

        System.Collections.Generic.List<string> output =
            new System.Collections.Generic.List<string>(lines.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == markerLine)
            {
                removed = true;
                continue;
            }

            output.Add(lines[i]);
        }

        if (!removed)
        {
            return;
        }

        string cleaned = string.Join("\n", output.ToArray());
        WriteSource(source, cleaned);
        AssetDatabase.ImportAsset(TargetPath, ImportAssetOptions.ForceUpdate);
    }

    private static void LogStopped(
        string stage,
        string reason)
    {
        Debug.LogError(
            "[KiwiAvatarSystem] Phase 16.4 stopped at " + stage + ". " +
            reason + " The old 96-attempt retry loop has been removed. " +
            "After correcting the reported stage, run Tools > Kiwi Avatar System > " +
            "Repair + Verify v5.1 Phase 16.4 Frame Continuity once.");
    }

    private static string AddConfirmationMarker(string source)
    {
        string normalized = NormalizeNewlines(source);
        string[] lines = normalized.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].IndexOf(
                    MarkerPhase16_2,
                    StringComparison.Ordinal) < 0)
            {
                continue;
            }

            string indent = lines[i].Substring(
                0,
                lines[i].Length - lines[i].TrimStart().Length);

            string markerLine =
                indent + "// " + MarkerPhase16_3;

            string[] output = new string[lines.Length + 1];
            Array.Copy(lines, 0, output, 0, i + 1);
            output[i + 1] = markerLine;
            Array.Copy(
                lines,
                i + 1,
                output,
                i + 2,
                lines.Length - i - 1);

            return string.Join("\n", output);
        }

        return source;
    }

    private static void EnsureLoadedSceneContinuityGuardEnabled()
    {
        KiwiFaceMotion[] motions =
            Resources.FindObjectsOfTypeAll<KiwiFaceMotion>();

        int repaired = 0;

        for (int i = 0; i < motions.Length; i++)
        {
            KiwiFaceMotion motion = motions[i];
            if (motion == null || EditorUtility.IsPersistent(motion))
            {
                continue;
            }

            SerializedObject serialized =
                new SerializedObject(motion);

            SerializedProperty guard =
                serialized.FindProperty("enableUltraFrameContinuityGuard");

            if (guard == null || guard.boolValue)
            {
                continue;
            }

            guard.boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(motion);
            repaired++;
        }

        if (repaired > 0)
        {
            Debug.Log(
                "[KiwiAvatarSystem] Phase 16.3b enabled Frame Continuity on " +
                repaired + " loaded KiwiFaceMotion component(s). Save the Scene " +
                "to persist this serialized safety setting.");
        }
    }

    private static string ReadSource()
    {
        return NormalizeNewlines(File.ReadAllText(TargetPath));
    }

    private static void WriteSource(
        string originalNormalized,
        string normalizedOutput)
    {
        string originalDisk = File.ReadAllText(TargetPath);
        string newline =
            originalDisk.Contains("\r\n")
                ? "\r\n"
                : "\n";

        string output =
            newline == "\n"
                ? normalizedOutput
                : normalizedOutput.Replace("\n", "\r\n");

        File.WriteAllText(
            TargetPath,
            output,
            new System.Text.UTF8Encoding(false));
    }

    private static string NormalizeNewlines(string source)
    {
        return source
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }
}
#endif
