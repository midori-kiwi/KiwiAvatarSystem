using UnityEngine;

/// <summary>
/// Retired by RAW_DIRECT_MOTION_V1. Inference recovery and the fixed auxiliary
/// cadence baseline are owned by KiwiInferenceRecoveryBootstrap and applied by
/// KiwiRuntimePolicyResolver. The tracker retains its own persistent ROI logic.
/// This type has no runtime installation or update path.
/// </summary>
[AddComponentMenu("")]
[DisallowMultipleComponent]
public sealed class KiwiMatureVTuberSupervisor : MonoBehaviour
{
    public const string Version = "RAW_DIRECT_MOTION_V1_RETIRED";

    // Source compatibility for optional profile/telemetry callers. No Production
    // instance is installed and these values drive no tracking or presentation.
    public float userEyeResponseMultiplier { get; set; } = 1f;
    public float userMouthResponseMultiplier { get; set; } = 1f;
    public float userContourResponseMultiplier { get; set; } = 1f;
    public string CurrentMode => "RAW_DIRECT_RETIRED";
    public float CurrentPolicyQuality => 1f;
    public float CurrentAuxiliaryMediaPipeHz =>
        KiwiRuntimePolicyResolver.ResolvedMediaPipeRefreshHz;
    public float CurrentRenderFps => 0f;
    public bool CommercialCadenceBoostActive => false;
}