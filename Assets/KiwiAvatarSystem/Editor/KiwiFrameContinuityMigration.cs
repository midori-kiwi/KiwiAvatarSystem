#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 16 targeted KiwiFaceMotion integration driven by a complete
/// 950-frame capture audit.
///
/// The recording showed excellent stationary stability but repeated one-render
/// accepted-pose teleports while tracking cadence was only about 5.5-8 Hz.
/// Existing display-rate smoothing was being bypassed by the zero-lag direct
/// motion path; additionally its configured fast response (up to 220) is close
/// to a snap at ~50 fps.
///
/// This migration does NOT add a second Root writer, frame buffer, Kalman, or
/// global low-pass. It only makes the existing KiwiFaceMotion display resampler
/// cadence-aware and temporarily disables its direct bypass after a timing /
/// provider discontinuity.
/// </summary>
[InitializeOnLoad]
public static class KiwiFrameContinuityMigration
{
    private const string TargetPath =
        "Assets/Script/KiwiFaceMotion.cs";

    private const string Phase47Prerequisite =
        "KIWI_V4_7_COMMERCIAL_RIGID_PHASE_AUTHORITY";

    private const string Phase9Prerequisite =
        "KIWI_V5_1_PHASE9_PROVIDER_TIMEBASE_GAP";

    private const string Marker =
        "KIWI_V5_1_PHASE16_FRAME_CONTINUITY_GUARD";

    private static int _retryCount;

    static KiwiFrameContinuityMigration()
    {
        EditorApplication.delayCall += ApplyWhenReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 16 Frame Continuity Guard")]
    private static void ApplyFromMenu()
    {
        _retryCount = 0;
        ApplyWhenReady();
    }

    private static void ApplyWhenReady()
    {
        if (!File.Exists(TargetPath))
        {
            RetryOrWarn(
                "KiwiFaceMotion.cs was not found. No rewrite was performed.");
            return;
        }

        string original = File.ReadAllText(TargetPath);
        string source = NormalizeNewlines(original);

        if (source.Contains(Marker))
        {
            return;
        }

        if (
            !source.Contains(Phase47Prerequisite) ||
            !source.Contains(Phase9Prerequisite)
        )
        {
            RetryOrWarn(
                "Phase 16 waited for the v4.7 rigid authority and v5.1 Phase 9 " +
                "timebase markers. No blind/partial rewrite was performed.");
            return;
        }

        string patched = source;
        bool ok = true;
        ok &= PatchSettings(ref patched);
        ok &= PatchState(ref patched);
        ok &= PatchPredictionDiscontinuity(ref patched);
        ok &= PatchDisplayResponse(ref patched);
        ok &= PatchDisplayStepCaps(ref patched);
        ok &= PatchDirectBypassGate(ref patched);
        ok &= PatchContinuityMethod(ref patched);

        if (!ok)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 16 could not uniquely locate every " +
                "validated KiwiFaceMotion anchor. The file was left unchanged; " +
                "no partial rewrite was performed.");
            return;
        }

        WritePreservingFormat(
            TargetPath,
            original,
            patched);

        AssetDatabase.ImportAsset(
            TargetPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] v5.1 Phase 16 applied frame-accurate sparse-" +
            "cadence continuity protection to the existing KiwiFaceMotion " +
            "display resampler.");
    }

    private static bool PatchSettings(ref string source)
    {
        const string anchor =
            "    [Tooltip(\"Fast correction used only when velocity loses consistency at a stop, acceleration, or reversal.\")]\n" +
            "    [Range(45f, 400f)] public float ultraPositionRecoveryResponse = 180f;\n\n\n" +
            "    // =========================================================\n" +
            "    // Landmarker Primary Hybrid Precision Tracking\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "    [Tooltip(\"Fast correction used only when velocity loses consistency at a stop, acceleration, or reversal.\")]\n" +
            "    [Range(45f, 400f)] public float ultraPositionRecoveryResponse = 180f;\n\n" +
            "    // KIWI_V5_1_PHASE16_FRAME_CONTINUITY_GUARD\n" +
            "    [Header(\"Frame-Accurate Sparse-Cadence Continuity\")]\n" +
            "    [Tooltip(\"Keep accepted samples raw, but prevent one-render pose teleports when tracking cadence is sparse or the provider/timebase just changed.\")]\n" +
            "    public bool enableUltraFrameContinuityGuard = true;\n\n" +
            "    [Tooltip(\"Below this measured tracking rate, the existing zero-lag direct display bypass is suppressed and the existing display resampler is used instead.\")]\n" +
            "    [Range(8f, 30f)] public float ultraDirectBypassMinimumTrackingRateHz = 18f;\n\n" +
            "    [Tooltip(\"Fraction of a newly accepted correction the display should close before the next expected accepted sample.\")]\n" +
            "    [Range(0.70f, 0.98f)] public float ultraFrameContinuityConvergencePerSample = 0.90f;\n\n" +
            "    [Tooltip(\"Render frames that remain protected after a provider/timebase/stale-gap discontinuity.\")]\n" +
            "    [Range(1, 6)] public int ultraFrameContinuityDiscontinuityFrames = 3;\n\n" +
            "    [Tooltip(\"Maximum display response while the explicit discontinuity guard is active.\")]\n" +
            "    [Range(8f, 60f)] public float ultraFrameContinuityDiscontinuityResponseCap = 18f;\n\n" +
            "    [Tooltip(\"Maximum Root position correction speed while sparse/discontinuous, in avatar body heights per second. High enough for intentional motion, low enough to make one-render teleports impossible.\")]\n" +
            "    [Range(1f, 12f)] public float ultraFrameContinuityMaxPositionSpeedHeightsPerSecond = 6f;\n\n" +
            "    [Tooltip(\"Maximum Root angular correction speed while sparse/discontinuous.\")]\n" +
            "    [Range(90f, 720f)] public float ultraFrameContinuityMaxRotationSpeedDegreesPerSecond = 420f;\n\n" +
            "    [Tooltip(\"Maximum uniform scale-factor correction speed while sparse/discontinuous.\")]\n" +
            "    [Range(0.25f, 5f)] public float ultraFrameContinuityMaxScaleSpeedPerSecond = 2f;\n\n\n" +
            "    // =========================================================\n" +
            "    // Landmarker Primary Hybrid Precision Tracking\n";

        source = source.Replace(anchor, replacement);
        return true;
    }

    private static bool PatchState(ref string source)
    {
        const string anchor =
            "    private long _lastDisplayAdvanceHostTicks;\n" +
            "    private Vector3 _renderPositionVelocity;\n\n\n" +
            "    // =========================================================\n" +
            "    // Raw previous values\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "    private long _lastDisplayAdvanceHostTicks;\n" +
            "    private Vector3 _renderPositionVelocity;\n\n" +
            "    // Phase 16: this is a short render-boundary privilege guard,\n" +
            "    // not a buffered pose history. It only prevents the existing\n" +
            "    // direct bypass from turning a discontinuity into one-frame Root motion.\n" +
            "    private int _kiwiFrameContinuityGuardUntilFrame = -1;\n\n\n" +
            "    // =========================================================\n" +
            "    // Raw previous values\n";

        source = source.Replace(anchor, replacement);
        return true;
    }

    private static bool PatchPredictionDiscontinuity(ref string source)
    {
        const string anchor =
            "        if (predictionGap)\n" +
            "        {\n" +
            "            ResetPredictionHistory();\n" +
            "        }\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "        if (predictionGap)\n" +
            "        {\n" +
            "            if (enableUltraFrameContinuityGuard)\n" +
            "            {\n" +
            "                _kiwiFrameContinuityGuardUntilFrame =\n" +
            "                    Mathf.Max(\n" +
            "                        _kiwiFrameContinuityGuardUntilFrame,\n" +
            "                        Time.frameCount +\n" +
            "                        Mathf.Max(1, ultraFrameContinuityDiscontinuityFrames));\n" +
            "            }\n\n" +
            "            ResetPredictionHistory();\n" +
            "        }\n";

        source = source.Replace(anchor, replacement);
        return true;
    }

    private static bool PatchDisplayResponse(ref string source)
    {
        const string anchor =
            "        float baseResponse = Mathf.Max(1f, ultraDisplaySmoothingResponse);\n" +
            "        float fastResponse = Mathf.Max(baseResponse, ultraDisplayFastResponse);\n\n" +
            "        float rotationError = Quaternion.Angle(_displayRotation, targetRotation);\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "        float baseResponse = Mathf.Max(1f, ultraDisplaySmoothingResponse);\n" +
            "        float fastResponse = Mathf.Max(baseResponse, ultraDisplayFastResponse);\n\n" +
            "        float kiwiTrackingRateHz = GetFrameContinuityMeasuredTrackingRateHz();\n" +
            "        float kiwiContinuityInterval =\n" +
            "            KiwiFrameContinuityMath.ResolveEffectiveSampleInterval(\n" +
            "                _lastAcceptedSampleInterval,\n" +
            "                kiwiTrackingRateHz);\n\n" +
            "        bool kiwiDiscontinuityGuard =\n" +
            "            enableUltraFrameContinuityGuard &&\n" +
            "            Time.frameCount <= _kiwiFrameContinuityGuardUntilFrame;\n\n" +
            "        bool kiwiSparseCadence =\n" +
            "            enableUltraFrameContinuityGuard &&\n" +
            "            KiwiFrameContinuityMath.IsSparseCadence(\n" +
            "                kiwiContinuityInterval,\n" +
            "                ultraDirectBypassMinimumTrackingRateHz);\n\n" +
            "        bool kiwiSuppressDirectBypass =\n" +
            "            kiwiDiscontinuityGuard ||\n" +
            "            kiwiSparseCadence;\n\n" +
            "        float kiwiResponseCap = 0f;\n\n" +
            "        if (kiwiSuppressDirectBypass)\n" +
            "        {\n" +
            "            kiwiResponseCap =\n" +
            "                KiwiFrameContinuityMath.CalculateResponseCap(\n" +
            "                    kiwiContinuityInterval,\n" +
            "                    ultraFrameContinuityConvergencePerSample,\n" +
            "                    kiwiDiscontinuityGuard,\n" +
            "                    ultraFrameContinuityDiscontinuityResponseCap);\n\n" +
            "            baseResponse = Mathf.Min(baseResponse, kiwiResponseCap);\n" +
            "            fastResponse = Mathf.Min(fastResponse, kiwiResponseCap);\n" +
            "        }\n\n" +
            "        KiwiFrameContinuityDiagnostics.Report(\n" +
            "            enableUltraFrameContinuityGuard,\n" +
            "            kiwiSuppressDirectBypass,\n" +
            "            kiwiDiscontinuityGuard,\n" +
            "            kiwiTrackingRateHz,\n" +
            "            kiwiContinuityInterval,\n" +
            "            kiwiResponseCap);\n\n" +
            "        float rotationError = Quaternion.Angle(_displayRotation, targetRotation);\n";

        source = source.Replace(anchor, replacement);
        return true;
    }

    private static bool PatchDisplayStepCaps(ref string source)
    {
        const string beforeAnchor =
            "        bool positionHandled = ApplyZeroLagMotionTarget(\n" +
            "            targetRotation,\n" +
            "            targetPosition,\n" +
            "            targetScale,\n" +
            "            dt\n" +
            "        );\n";

        if (CountOccurrences(source, beforeAnchor) != 1)
        {
            return false;
        }

        const string beforeReplacement =
            "        Quaternion kiwiPreviousDisplayRotation = _displayRotation;\n" +
            "        Vector3 kiwiPreviousDisplayPosition = _displayPosition;\n" +
            "        Vector3 kiwiPreviousDisplayScale = _displayScale;\n\n" +
            "        bool positionHandled = ApplyZeroLagMotionTarget(\n" +
            "            targetRotation,\n" +
            "            targetPosition,\n" +
            "            targetScale,\n" +
            "            dt\n" +
            "        );\n";

        source = source.Replace(
            beforeAnchor,
            beforeReplacement);

        const string afterAnchor =
            "        _displayScale = Vector3.Lerp(\n" +
            "            _displayScale,\n" +
            "            targetScale,\n" +
            "            ExpFactor(scaleResponse, dt)\n" +
            "        );\n";

        if (CountOccurrences(source, afterAnchor) != 1)
        {
            return false;
        }

        const string afterReplacement =
            "        _displayScale = Vector3.Lerp(\n" +
            "            _displayScale,\n" +
            "            targetScale,\n" +
            "            ExpFactor(scaleResponse, dt)\n" +
            "        );\n\n" +
            "        if (kiwiSuppressDirectBypass)\n" +
            "        {\n" +
            "            ApplyFrameContinuityStepCaps(\n" +
            "                kiwiPreviousDisplayRotation,\n" +
            "                kiwiPreviousDisplayPosition,\n" +
            "                kiwiPreviousDisplayScale,\n" +
            "                dt);\n" +
            "        }\n";

        source = source.Replace(
            afterAnchor,
            afterReplacement);

        return true;
    }

    private static bool PatchDirectBypassGate(ref string source)
    {
        const string anchor =
            "        if (\n" +
            "            !ultraDirectDisplayDuringMotion ||\n" +
            "            !_displayPoseInitialized\n" +
            "        )\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "        if (\n" +
            "            !ultraDirectDisplayDuringMotion ||\n" +
            "            !_displayPoseInitialized ||\n" +
            "            IsFrameContinuityDirectBypassSuppressed()\n" +
            "        )\n";

        source = source.Replace(anchor, replacement);
        return true;
    }

    private static bool PatchContinuityMethod(ref string source)
    {
        const string anchor =
            "    private void RenderDisplayPose()\n" +
            "    {\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "    private float GetFrameContinuityMeasuredTrackingRateHz()\n" +
            "    {\n" +
            "        // Built-in MediaPipe/Inference cadence is exposed by Runner.\n" +
            "        // External providers use their normalized accepted-sample\n" +
            "        // interval instead, avoiding a stale Runner cadence cap.\n" +
            "        return _lastAcceptedBackend != KiwiTrackingBackend.Unknown\n" +
            "            ? PrecisionTrackingRateHz\n" +
            "            : 0f;\n" +
            "    }\n\n" +
            "    private void ApplyFrameContinuityStepCaps(\n" +
            "        Quaternion previousRotation,\n" +
            "        Vector3 previousPosition,\n" +
            "        Vector3 previousScale,\n" +
            "        float dt)\n" +
            "    {\n" +
            "        float safeDt = Mathf.Clamp(dt, 0f, 0.05f);\n" +
            "        float safeHeight = Mathf.Max(_modelHeight, 0.0001f);\n\n" +
            "        _displayRotation = Quaternion.RotateTowards(\n" +
            "            previousRotation,\n" +
            "            _displayRotation,\n" +
            "            Mathf.Max(0f, ultraFrameContinuityMaxRotationSpeedDegreesPerSecond) * safeDt);\n\n" +
            "        _displayPosition = Vector3.MoveTowards(\n" +
            "            previousPosition,\n" +
            "            _displayPosition,\n" +
            "            safeHeight *\n" +
            "            Mathf.Max(0f, ultraFrameContinuityMaxPositionSpeedHeightsPerSecond) *\n" +
            "            safeDt);\n\n" +
            "        float previousScaleFactor = SafeScaleRatio(\n" +
            "            previousScale.x,\n" +
            "            _baseScale.x);\n" +
            "        float nextScaleFactor = SafeScaleRatio(\n" +
            "            _displayScale.x,\n" +
            "            _baseScale.x);\n" +
            "        float boundedScaleFactor = Mathf.MoveTowards(\n" +
            "            previousScaleFactor,\n" +
            "            nextScaleFactor,\n" +
            "            Mathf.Max(0f, ultraFrameContinuityMaxScaleSpeedPerSecond) * safeDt);\n" +
            "        _displayScale = _baseScale * boundedScaleFactor;\n" +
            "    }\n\n" +
            "    private bool IsFrameContinuityDirectBypassSuppressed()\n" +
            "    {\n" +
            "        if (!enableUltraFrameContinuityGuard)\n" +
            "        {\n" +
            "            return false;\n" +
            "        }\n\n" +
            "        float interval =\n" +
            "            KiwiFrameContinuityMath.ResolveEffectiveSampleInterval(\n" +
            "                _lastAcceptedSampleInterval,\n" +
            "                GetFrameContinuityMeasuredTrackingRateHz());\n\n" +
            "        bool discontinuityGuard =\n" +
            "            Time.frameCount <= _kiwiFrameContinuityGuardUntilFrame;\n\n" +
            "        return\n" +
            "            discontinuityGuard ||\n" +
            "            KiwiFrameContinuityMath.IsSparseCadence(\n" +
            "                interval,\n" +
            "                ultraDirectBypassMinimumTrackingRateHz);\n" +
            "    }\n\n" +
            "    private void RenderDisplayPose()\n" +
            "    {\n";

        source = source.Replace(anchor, replacement);
        return true;
    }

    private static void RetryOrWarn(string message)
    {
        _retryCount++;
        if (_retryCount <= 12)
        {
            EditorApplication.delayCall += ApplyWhenReady;
            return;
        }

        Debug.LogWarning(
            "[KiwiAvatarSystem] " + message);
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

        while (
            (index = source.IndexOf(
                value,
                index,
                StringComparison.Ordinal)) >= 0
        )
        {
            count++;
            index += value.Length;
        }

        return count;
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
