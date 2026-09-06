using UnityEngine;

/// <summary>
/// Retired by RAW_DIRECT_MOTION_V1. This former temporal presentation owner has
/// no runtime installation, prediction, reconciliation, filtering, transform
/// writer, or FacePart policy. Required camera/inference defaults remain with
/// KiwiInferenceRecoveryBootstrap and KiwiRuntimePolicyResolver.
/// </summary>
[AddComponentMenu("")]
[DisallowMultipleComponent]
public sealed class KiwiTrackingQuality10Controller : MonoBehaviour
{
    public const string PresetVersion = "RAW_DIRECT_MOTION_V1_RETIRED";

    public bool Phase16_17PolicyOnlyPresentation => false;
    public bool Phase16_19StrictFacePartPresentationEpoch => false;
}