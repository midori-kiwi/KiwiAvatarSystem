#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v4.7 migration: make the commercial rigid-pose policy authoritative at every
/// render phase without rebuilding KiwiFaceMotion from scratch.
///
/// Targeted changes only:
/// - LateUpdate and onBeforeRender read the same Provider Hub authority.
/// - continuity Holding freezes the last trusted rigid pose; only Lost returns
///   to neutral.
/// - root X/Y uses a tiny spatial-only rest corridor (no temporal filter).
/// - measured source-age/cadence risk gates prediction instead of adding delay.
///
/// Eye/Mouth never feed the avatar root. Existing calibration, model mapping,
/// display interpolation and transform ownership stay in KiwiFaceMotion.
/// </summary>
[InitializeOnLoad]
public static class KiwiRigidPoseAuthorityMigration
{
    private const string TargetPath =
        "Assets/Script/KiwiFaceMotion.cs";

    private const string PhaseAuthorityMarker =
        "KIWI_V4_7_COMMERCIAL_RIGID_PHASE_AUTHORITY";

    private const string BeforeRenderMarker =
        "KIWI_V4_7_BEFORE_RENDER_RIGID_AUTHORITY";

    private const string LossPolicyMarker =
        "KIWI_V4_7_CONTINUITY_HOLD_POLICY";

    private const string TranslationMarker =
        "KIWI_V4_7_HEAD_TRANSLATION_STABILIZATION";

    private const string PredictionMarker =
        "KIWI_V4_7_MEASURED_PREDICTION_GATE";

    private const string PredictionWarmupMarker =
        "KIWI_V4_7_PREDICTION_CONSISTENCY_WARMUP";

    static KiwiRigidPoseAuthorityMigration()
    {
        EditorApplication.delayCall += ApplyIfNeeded;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v4.7 Commercial Rigid Continuity")]
    private static void ApplyFromMenu()
    {
        ApplyIfNeeded();
    }

    private static void ApplyIfNeeded()
    {
        if (!File.Exists(TargetPath))
        {
            return;
        }

        byte[] bytes = File.ReadAllBytes(TargetPath);
        bool hasBom =
            bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF;

        string source = File.ReadAllText(TargetPath);
        string normalized = source.Replace("\r\n", "\n");
        bool changed = false;
        bool blocked = false;

        changed |= PatchLateUpdateAuthority(
            ref normalized,
            ref blocked);

        changed |= PatchBeforeRenderAuthority(
            ref normalized,
            ref blocked);

        changed |= PatchContinuityLossPolicy(
            ref normalized,
            ref blocked);

        changed |= PatchTranslationStabilization(
            ref normalized,
            ref blocked);

        changed |= PatchPredictionGate(
            ref normalized,
            ref blocked);

        changed |= PatchPredictionWarmup(
            ref normalized,
            ref blocked);

        if (!changed)
        {
            if (!blocked && IsFullyApplied(normalized))
            {
                return;
            }

            if (blocked)
            {
                Debug.LogWarning(
                    "[KiwiAvatarSystem] v4.7 commercial rigid migration could " +
                    "not find one or more expected KiwiFaceMotion blocks. The " +
                    "unmatched blocks were left unchanged; no blind rewrite was " +
                    "performed.");
            }

            return;
        }

        string lineEnding =
            source.Contains("\r\n")
                ? "\r\n"
                : "\n";

        if (lineEnding == "\r\n")
        {
            normalized = normalized.Replace("\n", "\r\n");
        }

        File.WriteAllText(
            TargetPath,
            normalized,
            new UTF8Encoding(hasBom));

        AssetDatabase.ImportAsset(
            TargetPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] v4.7 applied commercial rigid continuity, " +
            "phase authority and measured prediction gating to KiwiFaceMotion. " +
            "No new Kalman filter, frame buffer or transform owner was added.");
    }

    private static bool PatchLateUpdateAuthority(
        ref string source,
        ref bool blocked)
    {
        if (source.Contains(PhaseAuthorityMarker))
        {
            return false;
        }

        const string V455Block =
            "        // KIWI_V4_5_5_PROVIDER_HUB_RIGID_AUTHORITY\n" +
            "        // The Hub is the rigid-provider authority. Only fall back to\n" +
            "        // Runner-direct access if the Hub itself is not installed.\n" +
            "        FacePrecisionTrackingData precisionData;\n\n" +
            "        bool hasTracking =\n" +
            "            KiwiTrackingProviderHub.HasRuntimeInstance\n" +
            "                ? KiwiTrackingProviderHub.TryGetCurrentRigidFrame(\n" +
            "                    out precisionData)\n" +
            "                : runner.TryGetLatestPrecisionTrackingData(\n" +
            "                    out precisionData);";

        const string BaseBlock =
            "        bool hasTracking =\n" +
            "            runner.TryGetLatestPrecisionTrackingData(\n" +
            "                out FacePrecisionTrackingData precisionData\n" +
            "            );";

        const string Replacement =
            "        // KIWI_V4_7_COMMERCIAL_RIGID_PHASE_AUTHORITY\n" +
            "        // LateUpdate and onBeforeRender must consume the same\n" +
            "        // canonical provider selection. Runner-direct access is\n" +
            "        // compatibility-only when the Hub is not installed.\n" +
            "        FacePrecisionTrackingData precisionData;\n\n" +
            "        bool hasTracking =\n" +
            "            KiwiCommercialRigidMotionPolicy.TryGetAuthoritativeFrame(\n" +
            "                runner,\n" +
            "                out precisionData);";

        if (source.Contains(V455Block))
        {
            source = source.Replace(
                V455Block,
                Replacement);
            return true;
        }

        if (source.Contains(BaseBlock))
        {
            source = source.Replace(
                BaseBlock,
                Replacement);
            return true;
        }

        blocked = true;
        return false;
    }

    private static bool PatchBeforeRenderAuthority(
        ref string source,
        ref bool blocked)
    {
        if (source.Contains(BeforeRenderMarker))
        {
            return false;
        }

        const string Old =
            "        if (enableUltraLowLatencyTracking && ultraConsumeLatestSampleBeforeRender && runner != null)\n" +
            "        {\n" +
            "            if (runner.TryGetLatestPrecisionTrackingData(out FacePrecisionTrackingData latestData) &&\n" +
            "                IsNewPrecisionFrame(latestData))";

        const string Replacement =
            "        // KIWI_V4_7_BEFORE_RENDER_RIGID_AUTHORITY\n" +
            "        // Never bypass the Provider Hub at the render boundary.\n" +
            "        if (enableUltraLowLatencyTracking && ultraConsumeLatestSampleBeforeRender && runner != null)\n" +
            "        {\n" +
            "            if (KiwiCommercialRigidMotionPolicy.TryGetAuthoritativeFrame(\n" +
            "                    runner,\n" +
            "                    out FacePrecisionTrackingData latestData) &&\n" +
            "                IsNewPrecisionFrame(latestData))";

        if (!source.Contains(Old))
        {
            blocked = true;
            return false;
        }

        source = source.Replace(
            Old,
            Replacement);
        return true;
    }

    private static bool PatchContinuityLossPolicy(
        ref string source,
        ref bool blocked)
    {
        if (source.Contains(LossPolicyMarker))
        {
            return false;
        }

        const string Old =
            "        bool trackingLost =\n" +
            "            Time.unscaledTime -\n" +
            "            _lastSeenTime\n" +
            "            >\n" +
            "            trackingLostTime;\n\n\n" +
            "        if (trackingLost)\n" +
            "        {\n" +
            "            if (!_trackingWasLost)\n" +
            "            {\n" +
            "                ResetMotionAccent();\n" +
            "                ResetReactionState();\n" +
            "                ResetRejectedCandidateState();\n" +
            "                ResetPredictionHistory();\n" +
            "                ResetUltraStaticLocks();\n\n" +
            "                _trackingWasLost = true;\n" +
            "            }\n\n\n" +
            "            ReturnToNeutral(\n" +
            "                dt\n" +
            "            );\n\n\n" +
            "            return;\n" +
            "        }";

        const string Replacement =
            "        // KIWI_V4_7_CONTINUITY_HOLD_POLICY\n" +
            "        // A short inference/GPU stall holds the last trusted rigid\n" +
            "        // pose. Only continuity Lost returns the avatar to neutral.\n" +
            "        bool fallbackTrackingLost =\n" +
            "            Time.unscaledTime -\n" +
            "            _lastSeenTime\n" +
            "            >\n" +
            "            trackingLostTime;\n\n" +
            "        KiwiCommercialRigidMotionPolicy.ResolveLossPolicy(\n" +
            "            fallbackTrackingLost,\n" +
            "            out bool holdRigidPose,\n" +
            "            out bool trackingLost);\n\n" +
            "        if (holdRigidPose)\n" +
            "        {\n" +
            "            // Stop extrapolation immediately, but keep the last\n" +
            "            // rendered root pose. No neutral-return oscillation.\n" +
            "            ResetPredictionHistory();\n" +
            "            RenderDisplayPose();\n" +
            "            return;\n" +
            "        }\n\n" +
            "        if (trackingLost)\n" +
            "        {\n" +
            "            if (!_trackingWasLost)\n" +
            "            {\n" +
            "                ResetMotionAccent();\n" +
            "                ResetReactionState();\n" +
            "                ResetRejectedCandidateState();\n" +
            "                ResetPredictionHistory();\n" +
            "                ResetUltraStaticLocks();\n\n" +
            "                _trackingWasLost = true;\n" +
            "            }\n\n" +
            "            ReturnToNeutral(\n" +
            "                dt\n" +
            "            );\n\n" +
            "            return;\n" +
            "        }";

        if (!source.Contains(Old))
        {
            blocked = true;
            return false;
        }

        source = source.Replace(
            Old,
            Replacement);
        return true;
    }

    private static bool PatchTranslationStabilization(
        ref string source,
        ref bool blocked)
    {
        if (source.Contains(TranslationMarker))
        {
            return false;
        }

        const string OldEntry =
            "        if (enableUltraLowLatencyTracking && ultraAdaptiveMicroFilter)\n" +
            "        {\n" +
            "            float safeHeight = Mathf.Max(_modelHeight, 0.0001f);\n" +
            "            float positionError = Vector3.Distance(\n" +
            "                _samplePosition,\n" +
            "                rawTarget\n" +
            "            ) / safeHeight;";

        const string NewEntry =
            "        // KIWI_V4_7_HEAD_TRANSLATION_STABILIZATION\n" +
            "        // Adapt the EXISTING static position corridor from measured\n" +
            "        // source/cadence quality. No second filter is stacked.\n" +
            "        float effectiveUltraPositionDeadZone =\n" +
            "            KiwiCommercialRigidMotionPolicy.GetAdaptivePositionDeadZone(\n" +
            "                ultraPositionDeadZone,\n" +
            "                quality);\n\n" +
            "        if (enableUltraLowLatencyTracking && ultraAdaptiveMicroFilter)\n" +
            "        {\n" +
            "            float safeHeight = Mathf.Max(_modelHeight, 0.0001f);\n" +
            "            float positionError = Vector3.Distance(\n" +
            "                _samplePosition,\n" +
            "                rawTarget\n" +
            "            ) / safeHeight;";

        const string ReleaseOld =
            "                    ultraPositionDeadZone * 2.0f,\n" +
            "                    ultraPositionDeadZone + 0.000001f";
        const string ReleaseNew =
            "                    effectiveUltraPositionDeadZone * 2.0f,\n" +
            "                    effectiveUltraPositionDeadZone + 0.000001f";

        const string CandidateOld =
            "                    ultraPositionDeadZone * 1.5f,\n" +
            "                    ultraPositionDeadZone + 0.000001f";
        const string CandidateNew =
            "                    effectiveUltraPositionDeadZone * 1.5f,\n" +
            "                    effectiveUltraPositionDeadZone + 0.000001f";

        const string StaticNoiseOld =
            "                positionError < ultraPositionDeadZone;";
        const string StaticNoiseNew =
            "                positionError < effectiveUltraPositionDeadZone;";

        if (
            !source.Contains(OldEntry) ||
            !source.Contains(ReleaseOld) ||
            !source.Contains(CandidateOld) ||
            !source.Contains(StaticNoiseOld)
        )
        {
            blocked = true;
            return false;
        }

        source = source.Replace(
            OldEntry,
            NewEntry);
        source = source.Replace(
            ReleaseOld,
            ReleaseNew);
        source = source.Replace(
            CandidateOld,
            CandidateNew);
        source = source.Replace(
            StaticNoiseOld,
            StaticNoiseNew);

        return true;
    }

    private static bool PatchPredictionGate(
        ref string source,
        ref bool blocked)
    {
        if (source.Contains(PredictionMarker))
        {
            return false;
        }

        const string Old =
            "        float baseLead = Mathf.Min(\n" +
            "            age + captureAgeCompensation,\n" +
            "            intervalBound\n" +
            "        ) * configuredStrength * qualityWeight;\n" +
            "        if (baseLead <= 0.00001f)";

        const string Replacement =
            "        float baseLead = Mathf.Min(\n" +
            "            age + captureAgeCompensation,\n" +
            "            intervalBound\n" +
            "        ) * configuredStrength * qualityWeight;\n\n" +
            "        // KIWI_V4_7_MEASURED_PREDICTION_GATE\n" +
            "        // Prediction is a latency-compensation privilege, not a\n" +
            "        // permanent filter stage. Stale/irregular cadence drives\n" +
            "        // this allowance toward zero.\n" +
            "        baseLead *=\n" +
            "            KiwiCommercialRigidMotionPolicy.GetPredictionAllowance(\n" +
            "                age);\n\n" +
            "        if (baseLead <= 0.00001f)";

        if (!source.Contains(Old))
        {
            blocked = true;
            return false;
        }

        source = source.Replace(
            Old,
            Replacement);
        return true;
    }

    private static bool PatchPredictionWarmup(
        ref string source,
        ref bool blocked)
    {
        if (source.Contains(PredictionWarmupMarker))
        {
            return false;
        }

        const string Old =
            "        else\n" +
            "        {\n" +
            "            // Allow a small amount of useful lead on the second accepted\n" +
            "            // sample, then use measured direction consistency thereafter.\n" +
            "            _predictionRotationConsistency =\n" +
            "                0.70f;\n\n" +
            "            _predictionPositionConsistency =\n" +
            "                0.70f;\n\n" +
            "            _predictionScaleConsistency =\n" +
            "                0.65f;\n\n" +
            "            _hasPredictionRawVelocityHistory =\n" +
            "                true;\n" +
            "        }";

        const string Replacement =
            "        else\n" +
            "        {\n" +
            "            // KIWI_V4_7_PREDICTION_CONSISTENCY_WARMUP\n" +
            "            // One velocity observation does not establish a motion\n" +
            "            // direction. Wait for the next accepted sample before\n" +
            "            // granting extrapolation after startup/reacquisition.\n" +
            "            _predictionRotationConsistency =\n" +
            "                0f;\n\n" +
            "            _predictionPositionConsistency =\n" +
            "                0f;\n\n" +
            "            _predictionScaleConsistency =\n" +
            "                0f;\n\n" +
            "            _hasPredictionRawVelocityHistory =\n" +
            "                true;\n" +
            "        }";

        if (!source.Contains(Old))
        {
            blocked = true;
            return false;
        }

        source = source.Replace(
            Old,
            Replacement);
        return true;
    }

    private static bool IsFullyApplied(
        string source)
    {
        return
            source.Contains(PhaseAuthorityMarker) &&
            source.Contains(BeforeRenderMarker) &&
            source.Contains(LossPolicyMarker) &&
            source.Contains(TranslationMarker) &&
            source.Contains(PredictionMarker) &&
            source.Contains(PredictionWarmupMarker);
    }
}
#endif
