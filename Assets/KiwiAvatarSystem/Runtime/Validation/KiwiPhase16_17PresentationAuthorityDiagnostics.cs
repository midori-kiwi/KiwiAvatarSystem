using UnityEngine;

/// <summary>
/// Phase 16.17 diagnostics for the single Root temporal-presentation contract.
///
/// KiwiFaceMotion remains the sole writer of the rigid avatar Root. The legacy
/// Quality10 temporal presenter is kept in source for rollback/compatibility,
/// but normal commercial runtime suppresses its LateUpdate/onBeforeRender Root
/// presentation path. This class is telemetry only and never writes transforms.
/// </summary>
public static class KiwiPhase16_17PresentationAuthorityDiagnostics
{
    // KIWI_V5_1_PHASE16_17_SINGLE_PRESENTATION_AUTHORITY_DIAGNOSTICS

    public static bool Quality10PolicyOnlyActive { get; private set; }
    public static bool SharedRootBinding { get; private set; }
    public static int SuppressedLateUpdateCount { get; private set; }
    public static int SuppressedBeforeRenderCount { get; private set; }
    public static int LegacyRootWriteCount { get; private set; }
    public static int LegacyWriteViolationCount { get; private set; }

    public static bool FaceMotionDisplayRateSmoothing { get; private set; }
    public static bool FaceMotionStaticRestEnabled { get; private set; }
    public static bool FaceMotionAdaptiveMicroFilter { get; private set; }
    public static bool FaceMotionPredictionDisabled { get; private set; }

    public static bool SinglePresentationAuthority =>
        Quality10PolicyOnlyActive &&
        SharedRootBinding &&
        LegacyWriteViolationCount == 0 &&
        FaceMotionDisplayRateSmoothing &&
        FaceMotionStaticRestEnabled &&
        !FaceMotionAdaptiveMicroFilter &&
        FaceMotionPredictionDisabled;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetRuntimeState()
    {
        Quality10PolicyOnlyActive = false;
        SharedRootBinding = false;
        SuppressedLateUpdateCount = 0;
        SuppressedBeforeRenderCount = 0;
        LegacyRootWriteCount = 0;
        LegacyWriteViolationCount = 0;
        FaceMotionDisplayRateSmoothing = false;
        FaceMotionStaticRestEnabled = false;
        FaceMotionAdaptiveMicroFilter = false;
        FaceMotionPredictionDisabled = false;
    }

    public static void ReportPolicyState(
        bool policyOnly,
        bool sharedRootBinding,
        KiwiFaceMotion faceMotion)
    {
        Quality10PolicyOnlyActive = policyOnly;
        SharedRootBinding = sharedRootBinding;

        if (faceMotion == null)
        {
            FaceMotionDisplayRateSmoothing = false;
            FaceMotionStaticRestEnabled = false;
            FaceMotionAdaptiveMicroFilter = false;
            FaceMotionPredictionDisabled = false;
            return;
        }

        FaceMotionDisplayRateSmoothing =
            faceMotion.ultraDisplayRateSmoothing;

        FaceMotionStaticRestEnabled =
            faceMotion.ultraStaticPoseLock;

        FaceMotionAdaptiveMicroFilter =
            faceMotion.ultraAdaptiveMicroFilter;

        FaceMotionPredictionDisabled =
            faceMotion.ultraPredictionStrength <= 0.0001f &&
            !faceMotion.ultraCompensateFullResultAge &&
            !faceMotion.ultraCompensateCameraCaptureAge &&
            !faceMotion.enableRenderTimeLatePrediction &&
            faceMotion.predictionStrength <= 0.0001f;
    }

    public static void ReportSuppressedLateUpdate()
    {
        SuppressedLateUpdateCount++;
    }

    public static void ReportSuppressedBeforeRender()
    {
        SuppressedBeforeRenderCount++;
    }

    public static void ReportLegacyRootWrite(
        bool policyOnly,
        bool sharedRootBinding)
    {
        LegacyRootWriteCount++;

        if (policyOnly && sharedRootBinding)
        {
            LegacyWriteViolationCount++;
        }
    }
}
