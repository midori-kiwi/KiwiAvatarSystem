using UnityEngine;

/// <summary>
/// Phase 16.19 observer-only audit for one FacePart presentation epoch.
///
/// Phase16.9 pins Texture + Crop + Mask + semantic geometry to an immutable
/// semantic/camera transaction. Phase16.19 verifies that no older compatibility
/// path advances crop geometry toward a newer time while those pixels remain
/// pinned. This component never changes crop, mask, texture or Root state.
/// </summary>
[DefaultExecutionOrder(36390)]
[DisallowMultipleComponent]
public sealed class KiwiPhase16_19FacePartEpochDiagnostics : MonoBehaviour
{
    // KIWI_V5_1_PHASE16_19_STRICT_FACEPART_PRESENTATION_EPOCH_DIAGNOSTICS

    private const string RuntimeObjectName =
        "[Kiwi] Phase16.19 FacePart Epoch Diagnostics";

    private FacePartCropper _cropper;
    private KiwiFacePartLiveMotionBridge _liveMotionBridge;
    private int _lastViolationFrame = -1;

    public static bool StrictTransactionActive { get; private set; }
    public static bool PredictionDisabled { get; private set; }
    public static bool MatchedAgeCompensationDisabled { get; private set; }
    public static bool DirectMotionDisabled { get; private set; }
    public static bool LiveResidualDisabled { get; private set; }
    public static bool ContractAligned { get; private set; }
    public static int ViolationCount { get; private set; }
    public static long SemanticTimestamp { get; private set; } = -1L;
    public static ulong TextureCanonicalFrameId { get; private set; }

    public static bool StrictPresentationEpochActive =>
        StrictTransactionActive && ContractAligned;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (FindFirstObjectByType<KiwiPhase16_19FacePartEpochDiagnostics>(
                FindObjectsInactive.Include) != null)
        {
            return;
        }

        GameObject host = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiPhase16_19FacePartEpochDiagnostics>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        StrictTransactionActive = false;
        PredictionDisabled = false;
        MatchedAgeCompensationDisabled = false;
        DirectMotionDisabled = false;
        LiveResidualDisabled = false;
        ContractAligned = false;
        ViolationCount = 0;
        SemanticTimestamp = -1L;
        TextureCanonicalFrameId = 0UL;
    }

    private void LateUpdate()
    {
        if (_cropper == null)
        {
            _cropper = FindFirstObjectByType<FacePartCropper>(
                FindObjectsInactive.Include);
        }

        if (_liveMotionBridge == null)
        {
            _liveMotionBridge =
                FindFirstObjectByType<KiwiFacePartLiveMotionBridge>(
                    FindObjectsInactive.Include);
        }

        StrictTransactionActive =
            KiwiFacePartTextureTransaction.StrictPresentationStarted;

        PredictionDisabled =
            _cropper != null && !_cropper.enablePrediction;

        MatchedAgeCompensationDisabled =
            _cropper != null && !_cropper.compensateMatchedFrameAge;

        DirectMotionDisabled =
            _cropper != null && !_cropper.directPositionDuringMotion;

        LiveResidualDisabled =
            _liveMotionBridge == null ||
            !_liveMotionBridge.enableLiveFrameTracking;

        ContractAligned =
            PredictionDisabled &&
            MatchedAgeCompensationDisabled &&
            DirectMotionDisabled &&
            LiveResidualDisabled;

        SemanticTimestamp =
            KiwiFacePartTextureTransaction.LastCommittedSemanticTimestamp;

        TextureCanonicalFrameId =
            KiwiFacePartTextureTransaction.LastCommittedCanonicalFrameId;

        if (
            StrictTransactionActive &&
            !ContractAligned &&
            _lastViolationFrame != Time.frameCount)
        {
            _lastViolationFrame = Time.frameCount;
            ViolationCount++;
        }
    }
}
