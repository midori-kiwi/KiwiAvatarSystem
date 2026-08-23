using UnityEngine;

/// <summary>
/// Observer state published by KiwiFaceMotion's Phase 16.13 targeted migration.
/// It owns no Transform and performs no filtering or prediction.
/// </summary>
public static class KiwiPhase16_13PresentationDiagnostics
{
    // KIWI_V5_1_PHASE16_13_STATIC_REST_DIAGNOSTICS
    // KIWI_V5_1_PHASE16_14_RENDER_BOUNDARY_FRESH_ONLY_DIAGNOSTICS
    public static bool StaticRestActive { get; private set; }
    public static float StaticRestCandidateSeconds { get; private set; }
    public static int StaticRestLockCount { get; private set; }
    public static int StaticRestReleaseCount { get; private set; }
    public static int BeforeRenderRestHoldCount { get; private set; }
    public static int BeforeRenderNewSampleCount { get; private set; }

    public static bool BeforeRenderFreshOnlyPolicyActive => true;
    public static int BeforeRenderSameSampleSkipCount { get; private set; }
    public static int BeforeRenderAcceptedNewSampleCount { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetRuntimeState()
    {
        StaticRestActive = false;
        StaticRestCandidateSeconds = 0f;
        StaticRestLockCount = 0;
        StaticRestReleaseCount = 0;
        BeforeRenderRestHoldCount = 0;
        BeforeRenderNewSampleCount = 0;
        BeforeRenderSameSampleSkipCount = 0;
        BeforeRenderAcceptedNewSampleCount = 0;
    }

    public static void ReportRestState(
        bool active,
        float candidateSeconds)
    {
        StaticRestActive = active;
        StaticRestCandidateSeconds = Mathf.Max(0f, candidateSeconds);
    }

    public static void RecordRestLock()
    {
        StaticRestActive = true;
        StaticRestLockCount++;
    }

    public static void RecordRestRelease()
    {
        StaticRestActive = false;
        StaticRestCandidateSeconds = 0f;
        StaticRestReleaseCount++;
    }

    public static void RecordBeforeRenderRestHold()
    {
        BeforeRenderRestHoldCount++;
    }

    public static void RecordBeforeRenderNewSample()
    {
        BeforeRenderNewSampleCount++;
    }

    public static void RecordBeforeRenderSameSampleSkip()
    {
        BeforeRenderSameSampleSkipCount++;
    }

    public static void RecordBeforeRenderAcceptedNewSample()
    {
        BeforeRenderAcceptedNewSampleCount++;
    }
}
