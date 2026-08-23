using UnityEngine;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Phase 16.16 observer-only diagnostics for the Root-space provider handoff bridge.
/// Never writes Transform, provider authority, calibration, tracking samples,
/// Eye/Mouth state, or inference state.
/// </summary>
public static class KiwiPhase16_16ProviderBridgeDiagnostics
{
    // KIWI_V5_1_PHASE16_16_ROOT_SPACE_PROVIDER_BRIDGE_DIAGNOSTICS
    public static bool Active { get; private set; }
    public static int Count { get; private set; }
    public static int ProviderGeneration { get; private set; }
    public static KiwiTrackingBackend Backend { get; private set; }
    public static float Weight { get; private set; }
    public static float TargetWeight { get; private set; }
    public static float ReleaseStep { get; private set; }
    public static int AcceptedSamples { get; private set; }
    public static float MotionProgress { get; private set; }
    public static float PositionOffset { get; private set; }
    public static float RotationOffsetDegrees { get; private set; }
    public static float ScaleRatio { get; private set; }
    public static float AppliedPositionDelta { get; private set; }
    public static float AppliedRotationDeltaDegrees { get; private set; }
    public static float AppliedScaleDelta { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetRuntimeState()
    {
        Active = false;
        Count = 0;
        ProviderGeneration = 0;
        Backend = KiwiTrackingBackend.Unknown;
        Weight = 0f;
        TargetWeight = 0f;
        ReleaseStep = 0f;
        AcceptedSamples = 0;
        MotionProgress = 0f;
        PositionOffset = 0f;
        RotationOffsetDegrees = 0f;
        ScaleRatio = 1f;
        AppliedPositionDelta = 0f;
        AppliedRotationDeltaDegrees = 0f;
        AppliedScaleDelta = 0f;
    }

    public static void ReportBridge(
        bool active,
        bool started,
        int providerGeneration,
        KiwiTrackingBackend backend,
        float weight,
        float targetWeight,
        float releaseStep,
        int acceptedSamples,
        float motionProgress,
        float positionOffset,
        float rotationOffsetDegrees,
        float scaleRatio)
    {
        Active = active;
        ProviderGeneration = providerGeneration;
        Backend = backend;
        Weight = Mathf.Clamp01(weight);
        TargetWeight = Mathf.Clamp01(targetWeight);
        ReleaseStep = Mathf.Max(0f, releaseStep);
        AcceptedSamples = Mathf.Max(0, acceptedSamples);
        MotionProgress = Mathf.Clamp01(motionProgress);
        PositionOffset = Mathf.Max(0f, positionOffset);
        RotationOffsetDegrees = Mathf.Max(0f, rotationOffsetDegrees);
        ScaleRatio = Mathf.Max(0.0001f, scaleRatio);

        if (started)
        {
            Count++;
        }

        if (!active)
        {
            AppliedPositionDelta = 0f;
            AppliedRotationDeltaDegrees = 0f;
            AppliedScaleDelta = 0f;
        }
    }

    public static void ReportApplied(
        float positionDelta,
        float rotationDeltaDegrees,
        float scaleDelta)
    {
        AppliedPositionDelta = Mathf.Max(0f, positionDelta);
        AppliedRotationDeltaDegrees = Mathf.Max(0f, rotationDeltaDegrees);
        AppliedScaleDelta = Mathf.Max(0f, scaleDelta);
    }
}
