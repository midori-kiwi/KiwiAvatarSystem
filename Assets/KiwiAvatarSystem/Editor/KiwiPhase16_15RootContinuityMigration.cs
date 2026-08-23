#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 16.15 targeted Root continuity repair.
///
/// Contracts:
/// - no authoritative rigid frame for a short interval => hold the rendered Root;
/// - same-provider resume is bridged in presentation only, never treated as a switch;
/// - an explicit discontinuity/resume gets a final Root correction envelope immediately
///   before the sole Root writer renders the pose;
/// - no global low-pass, permanent frame buffer, calibration change, provider change,
///   second Root writer, or Eye/Mouth -> Root feedback is introduced.
/// </summary>
[InitializeOnLoad]
public static class KiwiPhase16_15RootContinuityMigration
{
    private const string TargetPath =
        "Assets/Script/KiwiFaceMotion.cs";

    private const string Prerequisite =
        "KIWI_V5_1_PHASE16_14_RENDER_BOUNDARY_FRESH_ONLY";

    public const string Marker =
        "KIWI_V5_1_PHASE16_15_NO_FRAME_HOLD_RESUME_ENVELOPE";

    private static int _retryCount;

    static KiwiPhase16_15RootContinuityMigration()
    {
        EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 16.15 Root Continuity")]
    private static void ApplyFromMenu()
    {
        if (!TryApplyNow(out string failure))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase 16.15 Root continuity migration failed: " +
                failure);
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

        string originalDisk = File.ReadAllText(TargetPath);
        string source = NormalizeNewlines(originalDisk);

        if (!source.Contains(Prerequisite))
        {
            failure =
                "Phase 16.14 fresh-only render-boundary marker is missing. " +
                "Refusing to patch an unknown KiwiFaceMotion path.";
            return false;
        }

        if (HasCompletePatch(source))
        {
            return true;
        }

        if (source.Contains(Marker))
        {
            failure =
                "A Phase 16.15 marker exists but the complete Root continuity " +
                "contract is not present. No partial rewrite was performed.";
            return false;
        }

        if (!PatchState(ref source, out failure) ||
            !PatchLateUpdate(ref source, out failure) ||
            !PatchRenderEnvelope(ref source, out failure) ||
            !PatchPredictionDiagnostics(ref source, out failure) ||
            !PatchCorrectionBacklogDiagnostics(ref source, out failure) ||
            !PatchResetHook(ref source, out failure) ||
            !PatchMethods(ref source, out failure))
        {
            return false;
        }

        if (!HasCompletePatch(source))
        {
            failure =
                "Phase 16.15 patch did not satisfy its complete-marker contract.";
            return false;
        }

        WritePreservingFormat(TargetPath, originalDisk, source);
        AssetDatabase.ImportAsset(TargetPath, ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] Applied v5.1 Phase 16.15 no-frame hold / " +
            "same-provider resume / Root correction envelope to KiwiFaceMotion.cs.");

        return true;
    }

    private static bool PatchState(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string anchor =
            "    // =========================================================\n" +
            "    // Motion Accent\n";

        int anchorIndex = source.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            failure = "Motion Accent state anchor was not found.";
            return false;
        }

        const string block =
            "    // KIWI_V5_1_PHASE16_15_NO_FRAME_HOLD_RESUME_ENVELOPE\n" +
            "    // Presentation-only recovery state. Provider identity, calibration\n" +
            "    // and accepted tracking samples remain owned by their existing systems.\n" +
            "    private bool _phase16_15AuthoritativeFrameMissing;\n" +
            "    private int _phase16_15MissingProviderGeneration;\n" +
            "    private KiwiTrackingBackend _phase16_15MissingBackend = KiwiTrackingBackend.Unknown;\n" +
            "    private bool _phase16_15ResumeBridgeActive;\n" +
            "    private int _phase16_15ResumeBridgeAcceptedSamplesRemaining;\n" +
            "    private int _phase16_15NoFrameHoldCount;\n" +
            "    private int _phase16_15SameProviderResumeCount;\n\n" +
            "    public bool Phase16_15AuthoritativeFrameMissing =>\n" +
            "        _phase16_15AuthoritativeFrameMissing;\n\n" +
            "    public bool Phase16_15SameProviderResumeActive =>\n" +
            "        _phase16_15ResumeBridgeActive;\n\n\n";

        source = source.Insert(anchorIndex, block);
        return true;
    }

    private static bool PatchLateUpdate(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string signature =
            "    private void LateUpdate()";

        if (!TryGetMethod(
                source,
                signature,
                out int methodIndex,
                out int closingBrace,
                out string method))
        {
            failure = "LateUpdate was not found or its braces could not be resolved.";
            return false;
        }

        const string acceptedAnchor =
            "            if (accepted)\n" +
            "            {\n" +
            "                _lastSeenTime =\n" +
            "                    Time.unscaledTime;\n\n" +
            "                _trackingWasLost =\n" +
            "                    false;\n" +
            "            }\n";

        if (!method.Contains(acceptedAnchor))
        {
            failure = "LateUpdate accepted-sample anchor was not found.";
            return false;
        }

        method = method.Replace(
            acceptedAnchor,
            "            if (accepted)\n" +
            "            {\n" +
            "                HandlePhase16_15AcceptedAuthoritativeFrame(\n" +
            "                    KiwiCommercialRigidMotionPolicy.GetAuthoritativeProviderGeneration(),\n" +
            "                    precisionData.backend);\n\n" +
            "                _lastSeenTime =\n" +
            "                    Time.unscaledTime;\n\n" +
            "                _trackingWasLost =\n" +
            "                    false;\n" +
            "            }\n");

        const string lossPolicyAnchor =
            "        KiwiCommercialRigidMotionPolicy.ResolveLossPolicy(\n" +
            "            fallbackTrackingLost,\n" +
            "            out bool holdRigidPose,\n" +
            "            out bool trackingLost);\n";

        if (!method.Contains(lossPolicyAnchor))
        {
            failure = "LateUpdate continuity loss-policy anchor was not found.";
            return false;
        }

        string noFrameHold =
            "\n" +
            "        // A missing canonical rigid frame is not a new pose. During a\n" +
            "        // short gap, keep exactly the already-rendered Root and kill\n" +
            "        // extrapolation. Only continuity Lost may return to neutral.\n" +
            "        if (!hasTracking && !trackingLost)\n" +
            "        {\n" +
            "            BeginPhase16_15NoFrameHold();\n" +
            "            ResetPredictionHistory();\n" +
            "            RenderDisplayPose();\n" +
            "            return;\n" +
            "        }\n";

        method = method.Replace(
            lossPolicyAnchor,
            lossPolicyAnchor + noFrameHold);

        const string lostAnchor =
            "        if (trackingLost)\n" +
            "        {\n";

        if (!method.Contains(lostAnchor))
        {
            failure = "LateUpdate tracking-lost branch was not found.";
            return false;
        }

        method = method.Replace(
            lostAnchor,
            lostAnchor +
            "            ClearPhase16_15PresentationRecovery();\n");

        source =
            source.Substring(0, methodIndex) +
            method +
            source.Substring(closingBrace + 1);

        return true;
    }

    private static bool PatchRenderEnvelope(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string signature =
            "    private void RenderDisplayPose()";

        if (!TryGetMethod(
                source,
                signature,
                out int methodIndex,
                out int closingBrace,
                out string method))
        {
            failure = "RenderDisplayPose was not found.";
            return false;
        }

        const string anchor =
            "        RenderRotation(_displayRotation);\n";

        if (!method.Contains(anchor))
        {
            failure = "RenderDisplayPose rotation writer anchor was not found.";
            return false;
        }

        method = method.Replace(
            anchor,
            "        ApplyPhase16_15RootCorrectionEnvelope();\n\n" +
            anchor);

        source =
            source.Substring(0, methodIndex) +
            method +
            source.Substring(closingBrace + 1);

        return true;
    }

    private static bool PatchPredictionDiagnostics(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string signature =
            "    private void CalculateRenderTrackingTarget(";

        if (!TryGetMethod(
                source,
                signature,
                out int methodIndex,
                out int closingBrace,
                out string method))
        {
            failure = "CalculateRenderTrackingTarget was not found.";
            return false;
        }

        const string resetAnchor =
            "        _lastCaptureAgeCompensationMs = 0f;\n";

        if (!method.Contains(resetAnchor))
        {
            failure = "Prediction reset diagnostic anchor was not found.";
            return false;
        }

        method = method.Replace(
            resetAnchor,
            resetAnchor +
            "        KiwiPhase16_15RootContinuityDiagnostics.ReportPrediction(0f, 0f);\n");

        const string predictionAnchor =
            "        targetPosition = _samplePosition + positionDelta;\n";

        if (!method.Contains(predictionAnchor))
        {
            failure = "Prediction position target anchor was not found.";
            return false;
        }

        method = method.Replace(
            predictionAnchor,
            predictionAnchor +
            "        KiwiPhase16_15RootContinuityDiagnostics.ReportPrediction(\n" +
            "            positionDelta.magnitude,\n" +
            "            positionLead * 1000f);\n");

        source =
            source.Substring(0, methodIndex) +
            method +
            source.Substring(closingBrace + 1);

        return true;
    }

    private static bool PatchCorrectionBacklogDiagnostics(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string signature =
            "    private bool ApplyZeroLagMotionTarget(";

        if (!TryGetMethod(
                source,
                signature,
                out int methodIndex,
                out int closingBrace,
                out string method))
        {
            failure = "ApplyZeroLagMotionTarget was not found.";
            return false;
        }

        int openingBrace = method.IndexOf('{');
        if (openingBrace < 0)
        {
            failure = "ApplyZeroLagMotionTarget opening brace was not found.";
            return false;
        }

        method = method.Insert(
            openingBrace + 1,
            "\n        KiwiPhase16_15RootContinuityDiagnostics.ReportCorrectionBacklog(false);\n");

        const string backlogTargetAnchor =
            "            _displayPosition =\n";

        int targetIndex = method.IndexOf(
            backlogTargetAnchor,
            StringComparison.Ordinal);

        if (targetIndex < 0 ||
            !method.Substring(0, targetIndex).Contains("bool correctionBacklog ="))
        {
            failure = "Position correction backlog anchor was not found.";
            return false;
        }

        method = method.Insert(
            targetIndex,
            "            KiwiPhase16_15RootContinuityDiagnostics.ReportCorrectionBacklog(\n" +
            "                correctionBacklog);\n\n");

        source =
            source.Substring(0, methodIndex) +
            method +
            source.Substring(closingBrace + 1);

        return true;
    }

    private static bool PatchResetHook(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string signature =
            "    private void ResetPrecisionState()";

        if (!TryGetMethod(
                source,
                signature,
                out int methodIndex,
                out int closingBrace,
                out string method))
        {
            failure = "ResetPrecisionState was not found.";
            return false;
        }

        const string anchor =
            "        ResetUltraStaticLocks();\n";

        if (!method.Contains(anchor))
        {
            failure = "ResetPrecisionState static-lock reset anchor was not found.";
            return false;
        }

        method = method.Replace(
            anchor,
            anchor +
            "        ResetPhase16_15RootContinuityState();\n");

        source =
            source.Substring(0, methodIndex) +
            method +
            source.Substring(closingBrace + 1);

        return true;
    }

    private static bool PatchMethods(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string anchor =
            "    private void RenderDisplayPose()";

        int anchorIndex = source.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            failure = "RenderDisplayPose insertion anchor was not found.";
            return false;
        }

        const string methods =
            "    private void BeginPhase16_15NoFrameHold()\n" +
            "    {\n" +
            "        if (!_phase16_15AuthoritativeFrameMissing)\n" +
            "        {\n" +
            "            _phase16_15AuthoritativeFrameMissing = true;\n" +
            "            _phase16_15MissingProviderGeneration =\n" +
            "                KiwiCommercialRigidMotionPolicy.GetAuthoritativeProviderGeneration();\n" +
            "            _phase16_15MissingBackend = _lastAcceptedBackend;\n" +
            "            _phase16_15NoFrameHoldCount++;\n" +
            "            KiwiPhase16_15RootContinuityDiagnostics.RecordNoFrameHoldStart();\n" +
            "        }\n\n" +
            "        KiwiPhase16_15RootContinuityDiagnostics.ReportFrameAvailability(\n" +
            "            true,\n" +
            "            true);\n" +
            "    }\n\n" +
            "    private void HandlePhase16_15AcceptedAuthoritativeFrame(\n" +
            "        int providerGeneration,\n" +
            "        KiwiTrackingBackend backend)\n" +
            "    {\n" +
            "        bool startedResume = false;\n\n" +
            "        if (_phase16_15AuthoritativeFrameMissing)\n" +
            "        {\n" +
            "            bool sameGeneration =\n" +
            "                providerGeneration == _phase16_15MissingProviderGeneration;\n" +
            "            bool sameBackend =\n" +
            "                _phase16_15MissingBackend == KiwiTrackingBackend.Unknown ||\n" +
            "                backend == KiwiTrackingBackend.Unknown ||\n" +
            "                backend == _phase16_15MissingBackend;\n\n" +
            "            _phase16_15AuthoritativeFrameMissing = false;\n" +
            "            KiwiPhase16_15RootContinuityDiagnostics.ReportFrameAvailability(\n" +
            "                false,\n" +
            "                false);\n\n" +
            "            if (sameGeneration && sameBackend)\n" +
            "            {\n" +
            "                _phase16_15ResumeBridgeActive = true;\n" +
            "                _phase16_15ResumeBridgeAcceptedSamplesRemaining = 3;\n" +
            "                _phase16_15SameProviderResumeCount++;\n" +
            "                startedResume = true;\n" +
            "            }\n" +
            "            else\n" +
            "            {\n" +
            "                _phase16_15ResumeBridgeActive = false;\n" +
            "                _phase16_15ResumeBridgeAcceptedSamplesRemaining = 0;\n" +
            "            }\n" +
            "        }\n" +
            "        else if (_phase16_15ResumeBridgeActive)\n" +
            "        {\n" +
            "            _phase16_15ResumeBridgeAcceptedSamplesRemaining =\n" +
            "                Mathf.Max(\n" +
            "                    0,\n" +
            "                    _phase16_15ResumeBridgeAcceptedSamplesRemaining - 1);\n\n" +
            "            if (_phase16_15ResumeBridgeAcceptedSamplesRemaining <= 0)\n" +
            "            {\n" +
            "                _phase16_15ResumeBridgeActive = false;\n" +
            "            }\n" +
            "        }\n\n" +
            "        KiwiPhase16_15RootContinuityDiagnostics.ReportSameProviderResume(\n" +
            "            _phase16_15ResumeBridgeActive,\n" +
            "            _phase16_15ResumeBridgeAcceptedSamplesRemaining,\n" +
            "            startedResume);\n" +
            "    }\n\n" +
            "    private void ClearPhase16_15PresentationRecovery()\n" +
            "    {\n" +
            "        _phase16_15AuthoritativeFrameMissing = false;\n" +
            "        _phase16_15ResumeBridgeActive = false;\n" +
            "        _phase16_15ResumeBridgeAcceptedSamplesRemaining = 0;\n" +
            "        KiwiPhase16_15RootContinuityDiagnostics.ReportFrameAvailability(false, false);\n" +
            "        KiwiPhase16_15RootContinuityDiagnostics.ReportSameProviderResume(false, 0, false);\n" +
            "    }\n\n" +
            "    private void ResetPhase16_15RootContinuityState()\n" +
            "    {\n" +
            "        _phase16_15MissingProviderGeneration = 0;\n" +
            "        _phase16_15MissingBackend = KiwiTrackingBackend.Unknown;\n" +
            "        ClearPhase16_15PresentationRecovery();\n" +
            "        KiwiPhase16_15RootContinuityDiagnostics.ReportPrediction(0f, 0f);\n" +
            "        KiwiPhase16_15RootContinuityDiagnostics.ReportCorrectionBacklog(false);\n" +
            "    }\n\n" +
            "    private float CalculatePhase16_15MappedPositionStepLimit(float dt)\n" +
            "    {\n" +
            "        float safeDt = Mathf.Clamp(dt, 0f, 0.05f);\n" +
            "        float safeHeight = Mathf.Max(_modelHeight, 0.0001f);\n" +
            "        float modelHeightLimit =\n" +
            "            safeHeight *\n" +
            "            Mathf.Max(0f, ultraFrameContinuityMaxPositionSpeedHeightsPerSecond) *\n" +
            "            safeDt;\n\n" +
            "        if (!useScreenSpacePositionMapping || safeDt <= 0f)\n" +
            "        {\n" +
            "            return modelHeightLimit;\n" +
            "        }\n\n" +
            "        const float Probe = 0.05f;\n" +
            "        Vector3 xMapped = CalculateScreenMappedPosition(new Vector2(Probe, 0f));\n" +
            "        Vector3 yMapped = CalculateScreenMappedPosition(new Vector2(0f, Probe));\n" +
            "        float unitsPerNormalized =\n" +
            "            Mathf.Max(\n" +
            "                Vector3.Distance(_basePosition, xMapped),\n" +
            "                Vector3.Distance(_basePosition, yMapped)) / Probe;\n\n" +
            "        if (\n" +
            "            unitsPerNormalized <= 0.0001f ||\n" +
            "            float.IsNaN(unitsPerNormalized) ||\n" +
            "            float.IsInfinity(unitsPerNormalized)\n" +
            "        )\n" +
            "        {\n" +
            "            return modelHeightLimit;\n" +
            "        }\n\n" +
            "        float normalizedSpeedLimit =\n" +
            "            Mathf.Max(0.25f, precisionPositionOutlierSpeed) *\n" +
            "            Mathf.Max(0.25f, faceMotionMultiplier) *\n" +
            "            1.50f;\n" +
            "        float mappedLimit =\n" +
            "            unitsPerNormalized * normalizedSpeedLimit * safeDt;\n\n" +
            "        return Mathf.Min(\n" +
            "            modelHeightLimit,\n" +
            "            Mathf.Max(0.0005f, mappedLimit));\n" +
            "    }\n\n" +
            "    private void ApplyPhase16_15RootCorrectionEnvelope()\n" +
            "    {\n" +
            "        if (kiwiRoot == null || !_displayPoseInitialized)\n" +
            "        {\n" +
            "            return;\n" +
            "        }\n\n" +
            "        Vector3 previousPosition = kiwiRoot.localPosition;\n" +
            "        Quaternion previousRotation = kiwiRoot.localRotation;\n" +
            "        Vector3 previousScale = kiwiRoot.localScale;\n\n" +
            "        float rawPositionDelta = Vector3.Distance(previousPosition, _samplePosition);\n" +
            "        float rawRotationDelta = Quaternion.Angle(previousRotation, _sampleRotation);\n" +
            "        float previousScaleFactor = SafeScaleRatio(previousScale.x, _baseScale.x);\n" +
            "        float rawScaleFactor = SafeScaleRatio(_sampleScale.x, _baseScale.x);\n" +
            "        float rawScaleDelta = Mathf.Abs(rawScaleFactor - previousScaleFactor);\n\n" +
            "        float prePositionDelta = Vector3.Distance(previousPosition, _displayPosition);\n" +
            "        float preRotationDelta = Quaternion.Angle(previousRotation, _displayRotation);\n" +
            "        float preScaleFactor = SafeScaleRatio(_displayScale.x, _baseScale.x);\n" +
            "        float preScaleDelta = Mathf.Abs(preScaleFactor - previousScaleFactor);\n\n" +
            "        float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1f / 500f, 0.05f);\n" +
            "        float maxPositionStep = 0f;\n" +
            "        float maxRotationStep = 0f;\n" +
            "        float maxScaleStep = 0f;\n" +
            "        bool capApplied = false;\n\n" +
            "        bool finalEnvelopeActive =\n" +
            "            enableUltraLowLatencyTracking &&\n" +
            "            ultraDisableSecondaryBodyMotion &&\n" +
            "            (\n" +
            "                _phase16_15ResumeBridgeActive ||\n" +
            "                KiwiFrameContinuityDiagnostics.DiscontinuityGuardActive\n" +
            "            );\n\n" +
            "        if (finalEnvelopeActive)\n" +
            "        {\n" +
            "            maxPositionStep = CalculatePhase16_15MappedPositionStepLimit(dt);\n" +
            "            maxRotationStep =\n" +
            "                Mathf.Max(0f, ultraFrameContinuityMaxRotationSpeedDegreesPerSecond) * dt;\n" +
            "            maxScaleStep =\n" +
            "                Mathf.Max(0f, ultraFrameContinuityMaxScaleSpeedPerSecond) * dt;\n\n" +
            "            Vector3 boundedPosition = Vector3.MoveTowards(\n" +
            "                previousPosition,\n" +
            "                _displayPosition,\n" +
            "                maxPositionStep);\n" +
            "            Quaternion boundedRotation = Quaternion.RotateTowards(\n" +
            "                previousRotation,\n" +
            "                _displayRotation,\n" +
            "                maxRotationStep);\n" +
            "            float boundedScaleFactor = Mathf.MoveTowards(\n" +
            "                previousScaleFactor,\n" +
            "                preScaleFactor,\n" +
            "                maxScaleStep);\n" +
            "            Vector3 boundedScale = _baseScale * boundedScaleFactor;\n\n" +
            "            capApplied =\n" +
            "                Vector3.Distance(boundedPosition, _displayPosition) > 0.000001f ||\n" +
            "                Quaternion.Angle(boundedRotation, _displayRotation) > 0.0001f ||\n" +
            "                Vector3.Distance(boundedScale, _displayScale) > 0.000001f;\n\n" +
            "            _displayPosition = boundedPosition;\n" +
            "            _displayRotation = boundedRotation;\n" +
            "            _displayScale = boundedScale;\n" +
            "        }\n\n" +
            "        float postPositionDelta = Vector3.Distance(previousPosition, _displayPosition);\n" +
            "        float postRotationDelta = Quaternion.Angle(previousRotation, _displayRotation);\n" +
            "        float postScaleFactor = SafeScaleRatio(_displayScale.x, _baseScale.x);\n" +
            "        float postScaleDelta = Mathf.Abs(postScaleFactor - previousScaleFactor);\n\n" +
            "        KiwiPhase16_15RootContinuityDiagnostics.ReportEnvelope(\n" +
            "            _modelHeight,\n" +
            "            rawPositionDelta,\n" +
            "            rawRotationDelta,\n" +
            "            rawScaleDelta,\n" +
            "            prePositionDelta,\n" +
            "            preRotationDelta,\n" +
            "            preScaleDelta,\n" +
            "            postPositionDelta,\n" +
            "            postRotationDelta,\n" +
            "            postScaleDelta,\n" +
            "            maxPositionStep,\n" +
            "            maxRotationStep,\n" +
            "            maxScaleStep,\n" +
            "            capApplied);\n" +
            "    }\n\n\n";

        source = source.Insert(anchorIndex, methods);
        return true;
    }

    private static bool HasCompletePatch(string source)
    {
        return
            source.Contains(Marker) &&
            source.Contains("BeginPhase16_15NoFrameHold();") &&
            source.Contains("HandlePhase16_15AcceptedAuthoritativeFrame(") &&
            source.Contains("ApplyPhase16_15RootCorrectionEnvelope();") &&
            source.Contains("CalculatePhase16_15MappedPositionStepLimit(") &&
            source.Contains("ReportPrediction(") &&
            source.Contains("ReportCorrectionBacklog(") &&
            source.Contains("ResetPhase16_15RootContinuityState();");
    }

    private static bool TryGetMethod(
        string source,
        string signature,
        out int methodIndex,
        out int closingBrace,
        out string method)
    {
        methodIndex = source.IndexOf(signature, StringComparison.Ordinal);
        closingBrace = -1;
        method = string.Empty;

        if (methodIndex < 0)
        {
            return false;
        }

        int openingBrace = source.IndexOf('{', methodIndex + signature.Length);
        closingBrace = FindMatchingBrace(source, openingBrace);

        if (openingBrace < 0 || closingBrace < 0)
        {
            return false;
        }

        method = source.Substring(
            methodIndex,
            closingBrace - methodIndex + 1);
        return true;
    }

    private static int FindMatchingBrace(
        string source,
        int openingBrace)
    {
        if (openingBrace < 0 || openingBrace >= source.Length)
        {
            return -1;
        }

        int depth = 0;
        bool inString = false;
        bool inChar = false;
        bool inLineComment = false;
        bool inBlockComment = false;
        bool escape = false;

        for (int i = openingBrace; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
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
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '"') inString = false;
                continue;
            }

            if (inChar)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '\'') inChar = false;
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

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
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
        string output =
            original.Contains("\r\n")
                ? normalized.Replace("\n", "\r\n")
                : normalized;

        File.WriteAllText(path, output);
    }

    private static void ApplyAutomaticallyWhenReady()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
            return;
        }

        if (!File.Exists(TargetPath))
        {
            return;
        }

        string source = NormalizeNewlines(File.ReadAllText(TargetPath));

        if (HasCompletePatch(source))
        {
            return;
        }

        if (!source.Contains(Prerequisite))
        {
            _retryCount++;
            if (_retryCount <= 24)
            {
                EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
            }
            else
            {
                Debug.LogWarning(
                    "[KiwiAvatarSystem] Phase 16.15 waited for the Phase 16.14 " +
                    "prerequisite marker but it never appeared.");
            }
            return;
        }

        if (!TryApplyNow(out string failure))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Automatic Phase 16.15 Root continuity " +
                "migration failed: " + failure);
        }
    }
}
#endif
