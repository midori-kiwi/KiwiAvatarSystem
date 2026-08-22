#if UNITY_EDITOR
using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 16.2 measured continuity hardening.
///
/// Phase 16.3b makes this migration idempotent and anchor-tolerant. The prior
/// exact-string implementation could fail silently when KiwiFaceMotion had a
/// semantically equivalent formatting/phase variant. This version patches each
/// required feature independently, reports the exact failing stage, and only
/// writes the file after every required Phase 16.2 token is verified.
/// </summary>
[InitializeOnLoad]
public static class KiwiFrameContinuityPhase16_2Migration
{
    private const string TargetPath =
        "Assets/Script/KiwiFaceMotion.cs";

    private const string Phase16Prerequisite =
        "KIWI_V5_1_PHASE16_FRAME_CONTINUITY_GUARD";

    public const string Marker =
        "KIWI_V5_1_PHASE16_2_MEASURED_CONTINUITY_GUARD";

    static KiwiFrameContinuityPhase16_2Migration()
    {
        EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 16.2 Measured Continuity Guard")]
    private static void ApplyFromMenu()
    {
        if (!TryApplyNow(out string failure))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase 16.2 migration failed: " + failure);
        }
    }

    public static bool TryApplyNow(out string failure)
    {
        failure = string.Empty;

        if (!File.Exists(TargetPath))
        {
            failure = "KiwiFaceMotion.cs was not found at " + TargetPath + ".";
            return false;
        }

        string original = File.ReadAllText(TargetPath);
        string source = NormalizeNewlines(original);

        if (!source.Contains(Phase16Prerequisite))
        {
            failure =
                "Phase 16 prerequisite marker is missing. Phase 16.2 will not " +
                "perform a blind partial rewrite.";
            return false;
        }

        if (HasCompletePhase16_2(source, out string existingMissing))
        {
            if (!source.Contains(Marker))
            {
                string marked = EnsureMarker(source);
                if (marked == source)
                {
                    failure =
                        "Phase 16.2 implementation is present, but the migration " +
                        "marker could not be placed.";
                    return false;
                }

                WritePreservingFormat(TargetPath, original, marked);
                AssetDatabase.ImportAsset(TargetPath, ImportAssetOptions.ForceUpdate);
            }

            return true;
        }

        string patched = source;

        if (!EnsureSettings(ref patched, out failure))
        {
            failure = "settings: " + failure;
            return false;
        }

        if (!EnsureState(ref patched, out failure))
        {
            failure = "state: " + failure;
            return false;
        }

        if (!EnsureAcceptedCadenceRecording(ref patched, out failure))
        {
            failure = "accepted-cadence call sites: " + failure;
            return false;
        }

        if (!EnsureMeasuredContinuityMethods(ref patched, out failure))
        {
            failure = "measured-continuity methods: " + failure;
            return false;
        }

        if (!EnsureAdvanceDisplayEntry(ref patched, out failure))
        {
            failure = "AdvanceDisplayPose entry: " + failure;
            return false;
        }

        patched = EnsureMarker(patched);

        if (!HasCompletePhase16_2(patched, out string missingToken))
        {
            failure =
                "post-patch verification is missing token " + missingToken + ".";
            return false;
        }

        if (!patched.Contains(Marker))
        {
            failure = "post-patch verification could not place " + Marker + ".";
            return false;
        }

        if (patched != source)
        {
            WritePreservingFormat(TargetPath, original, patched);
            AssetDatabase.ImportAsset(TargetPath, ImportAssetOptions.ForceUpdate);

            Debug.Log(
                "[KiwiAvatarSystem] v5.1 Phase 16.2 applied measured accepted-" +
                "cadence continuity protection using Phase 16.3b idempotent " +
                "migration repair.");
        }

        return true;
    }

    private static void ApplyAutomaticallyWhenReady()
    {
        if (
            EditorApplication.isCompiling ||
            EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode ||
            !File.Exists(TargetPath)
        )
        {
            return;
        }

        string source = NormalizeNewlines(File.ReadAllText(TargetPath));

        if (
            source.Contains(Marker) ||
            !source.Contains(Phase16Prerequisite)
        )
        {
            return;
        }

        if (!TryApplyNow(out string failure))
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 16.2 automatic migration did not " +
                "rewrite KiwiFaceMotion. Exact stage: " + failure);
        }
    }

    private static bool EnsureSettings(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        if (source.Contains("ultraFrameContinuityEmergencyPositionErrorHeights"))
        {
            return true;
        }

        const string insertBefore =
            "    [Header(\"Landmarker Primary Hybrid Precision Tracking\")]";

        int index = source.IndexOf(insertBefore, StringComparison.Ordinal);
        if (index < 0)
        {
            failure =
                "could not locate the Landmarker Primary Hybrid Precision header.";
            return false;
        }

        const string block =
            "    // KIWI_V5_1_PHASE16_2_MEASURED_CONTINUITY_GUARD\n" +
            "    [Header(\"Measured Continuity Emergency Guard\")]\n" +
            "    [Tooltip(\"If the pending Root target differs by at least this many body heights, route it through the existing continuity resampler instead of allowing a one-render correction.\")]\n" +
            "    [Range(0.05f, 1f)] public float ultraFrameContinuityEmergencyPositionErrorHeights = 0.20f;\n\n" +
            "    [Tooltip(\"Angular target error that arms the short continuity guard even when cadence telemetry is temporarily unavailable.\")]\n" +
            "    [Range(5f, 90f)] public float ultraFrameContinuityEmergencyRotationErrorDegrees = 24f;\n\n" +
            "    [Tooltip(\"Uniform scale-factor target error that arms the short continuity guard.\")]\n" +
            "    [Range(0.02f, 0.50f)] public float ultraFrameContinuityEmergencyScaleError = 0.10f;\n\n\n";

        source = source.Insert(index, block);
        return true;
    }

    private static bool EnsureState(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        if (source.Contains("_kiwiFrameContinuityObservedAcceptedInterval"))
        {
            return true;
        }

        const string anchor =
            "    private int _kiwiFrameContinuityGuardUntilFrame = -1;";

        int index = source.IndexOf(anchor, StringComparison.Ordinal);
        if (index < 0)
        {
            failure =
                "Phase 16 guard state field _kiwiFrameContinuityGuardUntilFrame " +
                "was not found.";
            return false;
        }

        index += anchor.Length;

        const string block =
            "\n\n    // Phase 16.2 observes accepted-sample arrival cadence locally.\n" +
            "    // This is timing telemetry only; no pose/sample history is added.\n" +
            "    private double _kiwiFrameContinuityLastAcceptedRealtime = -1.0;\n" +
            "    private float _kiwiFrameContinuityObservedAcceptedInterval;";

        source = source.Insert(index, block);
        return true;
    }

    private static bool EnsureAcceptedCadenceRecording(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        int callCount = CountOccurrences(
            source,
            "RecordFrameContinuityAcceptedRealtime();");

        if (callCount >= 2)
        {
            return true;
        }

        Regex assignment = new Regex(
            @"(?m)^(?<indent>[ \t]*)_lastAcceptedBackend[ \t]*=[ \t]*(?:\n[ \t]*)?precisionData\.backend[ \t]*;[ \t]*$",
            RegexOptions.CultureInvariant);

        MatchCollection matches = assignment.Matches(source);
        if (matches.Count < 2)
        {
            failure =
                "expected at least two _lastAcceptedBackend = precisionData.backend " +
                "accepted-sample assignments, found " + matches.Count + ".";
            return false;
        }

        string original = source;
        source = assignment.Replace(
            source,
            delegate(Match match)
            {
                int tailStart = match.Index + match.Length;
                int tailLength = Mathf.Min(
                    220,
                    Mathf.Max(0, original.Length - tailStart));

                string tail = tailLength > 0
                    ? original.Substring(tailStart, tailLength)
                    : string.Empty;

                if (Regex.IsMatch(
                    tail,
                    @"^\s*RecordFrameContinuityAcceptedRealtime\(\);",
                    RegexOptions.CultureInvariant))
                {
                    return match.Value;
                }

                return match.Value +
                    "\n\n" +
                    match.Groups["indent"].Value +
                    "RecordFrameContinuityAcceptedRealtime();";
            });

        if (CountOccurrences(
                source,
                "RecordFrameContinuityAcceptedRealtime();") < 2)
        {
            failure = "could not install both accepted-cadence recording calls.";
            return false;
        }

        return true;
    }

    private static bool EnsureMeasuredContinuityMethods(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        bool hasRecordedMethod =
            source.Contains("private void RecordFrameContinuityAcceptedRealtime()");
        bool hasMagnitudeMethod =
            source.Contains("private void ArmFrameContinuityMagnitudeGuard(");
        bool getAlreadyMeasured =
            source.Contains(
                "float acceptedInterval = Mathf.Max(\n" +
                "            _lastAcceptedSampleInterval,\n" +
                "            _kiwiFrameContinuityObservedAcceptedInterval);");

        if (hasRecordedMethod && hasMagnitudeMethod && getAlreadyMeasured)
        {
            return true;
        }

        if (hasRecordedMethod || hasMagnitudeMethod)
        {
            failure =
                "a partial Phase 16.2 method set already exists. No duplicate " +
                "methods were inserted.";
            return false;
        }

        const string signature =
            "private float GetFrameContinuityMeasuredTrackingRateHz()";

        if (!TryFindMethodSpan(
                source,
                signature,
                out int methodStart,
                out int methodEnd))
        {
            failure =
                "Phase 16 method GetFrameContinuityMeasuredTrackingRateHz() " +
                "was not found or was structurally ambiguous.";
            return false;
        }

        const string replacement =
            "    private float GetFrameContinuityMeasuredTrackingRateHz()\n" +
            "    {\n" +
            "        float acceptedInterval = Mathf.Max(\n" +
            "            _lastAcceptedSampleInterval,\n" +
            "            _kiwiFrameContinuityObservedAcceptedInterval);\n\n" +
            "        float acceptedRateHz =\n" +
            "            acceptedInterval > 0.0001f\n" +
            "                ? 1f / acceptedInterval\n" +
            "                : 0f;\n\n" +
            "        if (_lastAcceptedBackend != KiwiTrackingBackend.Unknown)\n" +
            "        {\n" +
            "            float runnerRateHz = PrecisionTrackingRateHz;\n" +
            "            if (runnerRateHz > 0.5f && acceptedRateHz > 0.5f)\n" +
            "            {\n" +
            "                return Mathf.Min(runnerRateHz, acceptedRateHz);\n" +
            "            }\n\n" +
            "            if (runnerRateHz > 0.5f)\n" +
            "            {\n" +
            "                return runnerRateHz;\n" +
            "            }\n" +
            "        }\n\n" +
            "        return acceptedRateHz;\n" +
            "    }\n\n" +
            "    private void RecordFrameContinuityAcceptedRealtime()\n" +
            "    {\n" +
            "        if (!enableUltraFrameContinuityGuard)\n" +
            "        {\n" +
            "            return;\n" +
            "        }\n\n" +
            "        double now = Time.realtimeSinceStartupAsDouble;\n\n" +
            "        if (\n" +
            "            _kiwiFrameContinuityLastAcceptedRealtime >= 0.0 &&\n" +
            "            now > _kiwiFrameContinuityLastAcceptedRealtime\n" +
            "        )\n" +
            "        {\n" +
            "            float interval = (float)(\n" +
            "                now - _kiwiFrameContinuityLastAcceptedRealtime);\n\n" +
            "            if (interval >= 1f / 240f && interval <= 0.75f)\n" +
            "            {\n" +
            "                _kiwiFrameContinuityObservedAcceptedInterval =\n" +
            "                    Mathf.Clamp(interval, 1f / 240f, 0.50f);\n" +
            "            }\n" +
            "        }\n\n" +
            "        _kiwiFrameContinuityLastAcceptedRealtime = now;\n" +
            "    }\n\n" +
            "    private void ArmFrameContinuityMagnitudeGuard(\n" +
            "        Quaternion targetRotation,\n" +
            "        Vector3 targetPosition,\n" +
            "        Vector3 targetScale)\n" +
            "    {\n" +
            "        if (\n" +
            "            !enableUltraFrameContinuityGuard ||\n" +
            "            !_displayPoseInitialized\n" +
            "        )\n" +
            "        {\n" +
            "            return;\n" +
            "        }\n\n" +
            "        float safeHeight = Mathf.Max(_modelHeight, 0.0001f);\n" +
            "        float positionErrorHeights =\n" +
            "            Vector3.Distance(_displayPosition, targetPosition) / safeHeight;\n" +
            "        float rotationErrorDegrees =\n" +
            "            Quaternion.Angle(_displayRotation, targetRotation);\n" +
            "        float displayScaleFactor = SafeScaleRatio(\n" +
            "            _displayScale.x,\n" +
            "            _baseScale.x);\n" +
            "        float targetScaleFactor = SafeScaleRatio(\n" +
            "            targetScale.x,\n" +
            "            _baseScale.x);\n" +
            "        float scaleError = Mathf.Abs(\n" +
            "            displayScaleFactor - targetScaleFactor);\n\n" +
            "        bool largeCorrection =\n" +
            "            positionErrorHeights >=\n" +
            "                Mathf.Max(0.01f, ultraFrameContinuityEmergencyPositionErrorHeights) ||\n" +
            "            rotationErrorDegrees >=\n" +
            "                Mathf.Max(1f, ultraFrameContinuityEmergencyRotationErrorDegrees) ||\n" +
            "            scaleError >=\n" +
            "                Mathf.Max(0.005f, ultraFrameContinuityEmergencyScaleError);\n\n" +
            "        if (!largeCorrection)\n" +
            "        {\n" +
            "            return;\n" +
            "        }\n\n" +
            "        _kiwiFrameContinuityGuardUntilFrame =\n" +
            "            Mathf.Max(\n" +
            "                _kiwiFrameContinuityGuardUntilFrame,\n" +
            "                Time.frameCount +\n" +
            "                Mathf.Max(1, ultraFrameContinuityDiscontinuityFrames));\n" +
            "    }";

        source =
            source.Substring(0, methodStart) +
            replacement +
            source.Substring(methodEnd);

        return true;
    }

    private static bool EnsureAdvanceDisplayEntry(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        if (source.Contains("bool kiwiContinuityProtectionReady ="))
        {
            return true;
        }

        const string signature =
            "private void AdvanceDisplayPose(";

        if (!TryFindMethodSpan(
                source,
                signature,
                out int methodStart,
                out int methodEnd))
        {
            failure = "AdvanceDisplayPose(...) method was not found.";
            return false;
        }

        string method = source.Substring(
            methodStart,
            methodEnd - methodStart);

        int openingBrace = method.IndexOf('{');
        if (openingBrace < 0)
        {
            failure = "AdvanceDisplayPose body opening brace was not found.";
            return false;
        }

        Regex earlyGate = new Regex(
            @"if\s*\(\s*!_displayPoseInitialized\s*\|\|\s*!enableUltraLowLatencyTracking\s*\|\|\s*!ultraDisplayRateSmoothing\s*\)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Match gateMatch = earlyGate.Match(method);
        if (!gateMatch.Success)
        {
            failure =
                "the original display-smoothing early gate was not found. " +
                "This usually means KiwiFaceMotion already has an unrecognized " +
                "presentation-path modification.";
            return false;
        }

        const string gateReplacement =
            "if (!_displayPoseInitialized ||\n" +
            "            !enableUltraLowLatencyTracking ||\n" +
            "            (!ultraDisplayRateSmoothing && !kiwiForceContinuityResampling))";

        method =
            method.Substring(0, gateMatch.Index) +
            gateReplacement +
            method.Substring(gateMatch.Index + gateMatch.Length);

        const string preamble =
            "\n        bool kiwiContinuityProtectionReady =\n" +
            "            enableUltraFrameContinuityGuard &&\n" +
            "            _displayPoseInitialized &&\n" +
            "            enableUltraLowLatencyTracking;\n\n" +
            "        if (kiwiContinuityProtectionReady)\n" +
            "        {\n" +
            "            ArmFrameContinuityMagnitudeGuard(\n" +
            "                targetRotation,\n" +
            "                targetPosition,\n" +
            "                targetScale);\n" +
            "        }\n\n" +
            "        bool kiwiForceContinuityResampling =\n" +
            "            kiwiContinuityProtectionReady &&\n" +
            "            IsFrameContinuityDirectBypassSuppressed();\n\n" +
            "        if (\n" +
            "            enableUltraFrameContinuityGuard &&\n" +
            "            !ultraDisplayRateSmoothing &&\n" +
            "            !kiwiForceContinuityResampling\n" +
            "        )\n" +
            "        {\n" +
            "            float directTrackingRateHz =\n" +
            "                GetFrameContinuityMeasuredTrackingRateHz();\n" +
            "            float directInterval =\n" +
            "                KiwiFrameContinuityMath.ResolveEffectiveSampleInterval(\n" +
            "                    _lastAcceptedSampleInterval,\n" +
            "                    directTrackingRateHz);\n\n" +
            "            KiwiFrameContinuityDiagnostics.Report(\n" +
            "                true,\n" +
            "                false,\n" +
            "                false,\n" +
            "                directTrackingRateHz,\n" +
            "                directInterval,\n" +
            "                0f);\n" +
            "        }\n";

        method = method.Insert(openingBrace + 1, preamble);

        source =
            source.Substring(0, methodStart) +
            method +
            source.Substring(methodEnd);

        return true;
    }

    private static string EnsureMarker(string source)
    {
        if (source.Contains(Marker))
        {
            return source;
        }

        const string header =
            "    [Header(\"Measured Continuity Emergency Guard\")]";

        int index = source.IndexOf(header, StringComparison.Ordinal);
        if (index >= 0)
        {
            return source.Insert(
                index,
                "    // " + Marker + "\n");
        }

        const string field =
            "ultraFrameContinuityEmergencyPositionErrorHeights";

        index = source.IndexOf(field, StringComparison.Ordinal);
        if (index < 0)
        {
            return source;
        }

        int lineStart = source.LastIndexOf('\n', index);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        return source.Insert(
            lineStart,
            "    // " + Marker + "\n");
    }

    private static bool HasCompletePhase16_2(
        string source,
        out string missing)
    {
        string[] required =
        {
            "ultraFrameContinuityEmergencyPositionErrorHeights",
            "_kiwiFrameContinuityObservedAcceptedInterval",
            "RecordFrameContinuityAcceptedRealtime();",
            "private void RecordFrameContinuityAcceptedRealtime()",
            "private void ArmFrameContinuityMagnitudeGuard(",
            "bool kiwiContinuityProtectionReady =",
            "IsFrameContinuityDirectBypassSuppressed()"
        };

        for (int i = 0; i < required.Length; i++)
        {
            if (!source.Contains(required[i]))
            {
                missing = required[i];
                return false;
            }
        }

        if (CountOccurrences(
                source,
                "RecordFrameContinuityAcceptedRealtime();") < 2)
        {
            missing = "second RecordFrameContinuityAcceptedRealtime() call site";
            return false;
        }

        missing = string.Empty;
        return true;
    }

    private static bool TryFindMethodSpan(
        string source,
        string signature,
        out int start,
        out int end)
    {
        start = -1;
        end = -1;

        int signatureIndex = source.IndexOf(
            signature,
            StringComparison.Ordinal);

        if (signatureIndex < 0)
        {
            return false;
        }

        if (source.IndexOf(
                signature,
                signatureIndex + signature.Length,
                StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        int lineStart = source.LastIndexOf('\n', signatureIndex);
        start = lineStart < 0 ? 0 : lineStart + 1;

        int openingBrace = source.IndexOf('{', signatureIndex);
        if (openingBrace < 0)
        {
            return false;
        }

        int depth = 0;
        bool inString = false;
        bool escape = false;
        bool inChar = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int i = openingBrace; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                }
                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (inChar)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '\'')
                {
                    inChar = false;
                }
                continue;
            }

            if (c == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '\'')
            {
                inChar = true;
                continue;
            }

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

    private static int CountOccurrences(
        string source,
        string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

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

    private static string NormalizeNewlines(string source)
    {
        return source
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }

    private static void WritePreservingFormat(
        string path,
        string original,
        string normalizedPatched)
    {
        string newline =
            original.Contains("\r\n")
                ? "\r\n"
                : "\n";

        string output =
            newline == "\n"
                ? normalizedPatched
                : normalizedPatched.Replace("\n", "\r\n");

        File.WriteAllText(
            path,
            output,
            new System.Text.UTF8Encoding(false));
    }
}
#endif
