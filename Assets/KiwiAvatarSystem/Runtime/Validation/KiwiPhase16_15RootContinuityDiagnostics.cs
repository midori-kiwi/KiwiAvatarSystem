using UnityEngine;

/// <summary>
/// Phase 16.15 observer-only diagnostics for the final Root presentation path.
/// This class never writes Transform, tracking authority, provider state,
/// calibration, Eye/Mouth state, or prediction.
/// </summary>
public static class KiwiPhase16_15RootContinuityDiagnostics
{
    // KIWI_V5_1_PHASE16_15_ROOT_CONTINUITY_DIAGNOSTICS
    public static bool AuthoritativeFrameMissing { get; private set; }
    public static bool NoFrameHoldActive { get; private set; }
    public static int NoFrameHoldCount { get; private set; }

    public static bool SameProviderResumeActive { get; private set; }
    public static int SameProviderResumeCount { get; private set; }
    public static int SameProviderResumeSamplesRemaining { get; private set; }

    public static float RootModelHeight { get; private set; }
    public static float RootRawTargetPositionDelta { get; private set; }
    public static float RootRawTargetRotationDelta { get; private set; }
    public static float RootRawTargetScaleDelta { get; private set; }

    public static float RootDisplayPreCapPositionDelta { get; private set; }
    public static float RootDisplayPreCapRotationDelta { get; private set; }
    public static float RootDisplayPreCapScaleDelta { get; private set; }
    public static float RootDisplayPostCapPositionDelta { get; private set; }
    public static float RootDisplayPostCapRotationDelta { get; private set; }
    public static float RootDisplayPostCapScaleDelta { get; private set; }

    public static float RootContinuityMaxPositionStep { get; private set; }
    public static float RootContinuityMaxRotationStep { get; private set; }
    public static float RootContinuityMaxScaleStep { get; private set; }
    public static bool RootContinuityCapApplied { get; private set; }

    public static float RootPredictionPositionDelta { get; private set; }
    public static float RootPredictionLeadMs { get; private set; }
    public static bool RootCorrectionBacklog { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetRuntimeState()
    {
        AuthoritativeFrameMissing = false;
        NoFrameHoldActive = false;
        NoFrameHoldCount = 0;
        SameProviderResumeActive = false;
        SameProviderResumeCount = 0;
        SameProviderResumeSamplesRemaining = 0;
        RootModelHeight = 0f;
        RootRawTargetPositionDelta = 0f;
        RootRawTargetRotationDelta = 0f;
        RootRawTargetScaleDelta = 0f;
        RootDisplayPreCapPositionDelta = 0f;
        RootDisplayPreCapRotationDelta = 0f;
        RootDisplayPreCapScaleDelta = 0f;
        RootDisplayPostCapPositionDelta = 0f;
        RootDisplayPostCapRotationDelta = 0f;
        RootDisplayPostCapScaleDelta = 0f;
        RootContinuityMaxPositionStep = 0f;
        RootContinuityMaxRotationStep = 0f;
        RootContinuityMaxScaleStep = 0f;
        RootContinuityCapApplied = false;
        RootPredictionPositionDelta = 0f;
        RootPredictionLeadMs = 0f;
        RootCorrectionBacklog = false;
    }

    public static void ReportFrameAvailability(
        bool missing,
        bool holdActive)
    {
        AuthoritativeFrameMissing = missing;
        NoFrameHoldActive = holdActive;
    }

    public static void RecordNoFrameHoldStart()
    {
        NoFrameHoldCount++;
    }

    public static void ReportSameProviderResume(
        bool active,
        int samplesRemaining,
        bool started)
    {
        SameProviderResumeActive = active;
        SameProviderResumeSamplesRemaining = Mathf.Max(0, samplesRemaining);

        if (started)
        {
            SameProviderResumeCount++;
        }
    }

    public static void ReportEnvelope(
        float modelHeight,
        float rawTargetPositionDelta,
        float rawTargetRotationDelta,
        float rawTargetScaleDelta,
        float preCapPositionDelta,
        float preCapRotationDelta,
        float preCapScaleDelta,
        float postCapPositionDelta,
        float postCapRotationDelta,
        float postCapScaleDelta,
        float maxPositionStep,
        float maxRotationStep,
        float maxScaleStep,
        bool capApplied)
    {
        RootModelHeight = Mathf.Max(0f, modelHeight);
        RootRawTargetPositionDelta = Mathf.Max(0f, rawTargetPositionDelta);
        RootRawTargetRotationDelta = Mathf.Max(0f, rawTargetRotationDelta);
        RootRawTargetScaleDelta = Mathf.Max(0f, rawTargetScaleDelta);
        RootDisplayPreCapPositionDelta = Mathf.Max(0f, preCapPositionDelta);
        RootDisplayPreCapRotationDelta = Mathf.Max(0f, preCapRotationDelta);
        RootDisplayPreCapScaleDelta = Mathf.Max(0f, preCapScaleDelta);
        RootDisplayPostCapPositionDelta = Mathf.Max(0f, postCapPositionDelta);
        RootDisplayPostCapRotationDelta = Mathf.Max(0f, postCapRotationDelta);
        RootDisplayPostCapScaleDelta = Mathf.Max(0f, postCapScaleDelta);
        RootContinuityMaxPositionStep = Mathf.Max(0f, maxPositionStep);
        RootContinuityMaxRotationStep = Mathf.Max(0f, maxRotationStep);
        RootContinuityMaxScaleStep = Mathf.Max(0f, maxScaleStep);
        RootContinuityCapApplied = capApplied;
    }

    public static void ReportPrediction(
        float positionDelta,
        float leadMs)
    {
        RootPredictionPositionDelta = Mathf.Max(0f, positionDelta);
        RootPredictionLeadMs = Mathf.Max(0f, leadMs);
    }

    public static void ReportCorrectionBacklog(bool active)
    {
        RootCorrectionBacklog = active;
    }
}
