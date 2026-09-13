using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Sole Product writer for FacePart source-rigid roll removal.
///
/// An accepted P3D FaceGeometry pose may advance Eye/Mouth sample rotation only
/// when its complete identity matches the canonical appearance transaction.
/// Texture and semantic-mask sampling remain coupled in
/// SurfaceFittedRawImage.SetSampleFrameRotationDegrees.
/// </summary>
[DefaultExecutionOrder(830)]
[DisallowMultipleComponent]
public sealed class KiwiFacePartRigidSampleFrame : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Face-Part Rigid Sample Frame";

    [Header("Head-local sample frame")]
    public bool enableHeadLocalSampleFrame = true;

    [Tooltip("Maximum accepted P3D rigid Roll removed from the local eye/mouth sample. Avatar Root still owns the visible Roll.")]
    [Range(5f, 60f)]
    public float maximumCorrectionDegrees = 50f;

    [Tooltip("Very small P3D image-plane Roll values are ignored without adding a temporal filter.")]
    [Range(0f, 2f)]
    public float restAngleDeadZoneDegrees = 0.25f;

    [Header("Diagnostics")]
    [SerializeField] private bool debugOperational;
    [SerializeField] private float debugEyeLineAngle;
    [SerializeField] private float debugAppliedRotation;

    private static KiwiFacePartRigidSampleFrame _instance;

    private FacePartCropper _cropper;
    private SurfaceFittedRawImage _left;
    private SurfaceFittedRawImage _right;
    private SurfaceFittedRawImage _mouth;
    private int _lastBindingSignature;

    private bool _hasAppliedIdentity;
    private ulong _lastAppliedFrameId;
    private int _lastAppliedCameraGeneration;
    private int _lastAppliedTrackingSessionGeneration;
    private int _lastAppliedProviderGeneration;
    private int _lastAppliedModelGeneration;

    public static bool IsOperational { get; private set; }
    public static float AppliedRotationDegrees { get; private set; }
    public static float EyeLineAngleDegrees { get; private set; }

    // Retained for existing diagnostics. The retired bilateral-eye estimator no
    // longer performs temporal jump rejection, so this value remains zero.
    public static int RejectedAngleJumpCount => 0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiFacePartRigidSampleFrame>(
                FindObjectsInactive.Include) != null)
        {
            return;
        }

        GameObject host = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiFacePartRigidSampleFrame>();
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        RefreshReferences(true);
        ResetBridgeStateAndRotations();
    }

    private void OnDisable()
    {
        ResetBridgeStateAndRotations();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        ResetBridgeStateAndRotations();

        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        RefreshReferences(true);
        ResetBridgeStateAndRotations();
    }

    private void LateUpdate()
    {
        RefreshReferences(false);

        if (
            !enableHeadLocalSampleFrame ||
            _cropper == null ||
            _cropper.runner == null ||
            !_cropper.runner.IsFacePartGeometryBridgeOperational)
        {
            ResetBridgeStateAndRotations();
            return;
        }

        if (!_hasAppliedIdentity)
        {
            return;
        }

        KiwiRuntimeGenerationContext.Snapshot generation =
            KiwiRuntimeGenerationContext.Capture();

        if (
            generation.cameraGeneration !=
                _lastAppliedCameraGeneration ||
            generation.trackingSessionGeneration !=
                _lastAppliedTrackingSessionGeneration ||
            generation.providerGeneration !=
                _lastAppliedProviderGeneration ||
            generation.modelGeneration !=
                _lastAppliedModelGeneration ||
            !KiwiCanonicalTrackingFrame.TryGetFrame(
                out KiwiTrackingFrame frame) ||
            !frame.isValid)
        {
            ResetBridgeStateAndRotations();
        }
    }

    internal static bool CanCommitSemanticTransaction(
        FacePartCropper cropper,
        long semanticTimestamp,
        ulong canonicalFrameId,
        KiwiFaceGeometryTransactionService.AcceptedSnapshot snapshot)
    {
        KiwiFacePartRigidSampleFrame instance = _instance;
        return
            instance != null &&
            instance.CanAcceptSemanticTransaction(
                cropper,
                semanticTimestamp,
                canonicalFrameId,
                snapshot,
                out _);
    }

    internal static bool CommitSemanticTransaction(
        FacePartCropper cropper,
        long semanticTimestamp,
        ulong canonicalFrameId,
        KiwiFaceGeometryTransactionService.AcceptedSnapshot snapshot,
        bool leftEyeAdvanced,
        bool rightEyeAdvanced,
        bool mouthAdvanced)
    {
        KiwiFacePartRigidSampleFrame instance = _instance;
        return
            instance != null &&
            instance.TryCommitSemanticTransaction(
                cropper,
                semanticTimestamp,
                canonicalFrameId,
                snapshot,
                leftEyeAdvanced,
                rightEyeAdvanced,
                mouthAdvanced);
    }

    private bool TryCommitSemanticTransaction(
        FacePartCropper cropper,
        long semanticTimestamp,
        ulong canonicalFrameId,
        KiwiFaceGeometryTransactionService.AcceptedSnapshot snapshot,
        bool leftEyeAdvanced,
        bool rightEyeAdvanced,
        bool mouthAdvanced)
    {
        if (
            !CanAcceptSemanticTransaction(
                cropper,
                semanticTimestamp,
                canonicalFrameId,
                snapshot,
                out float sourceRollDegrees))
        {
            return false;
        }

        if (cropper.mirrorX)
        {
            sourceRollDegrees = -sourceRollDegrees;
        }

        if (
            Mathf.Abs(sourceRollDegrees) <=
                Mathf.Max(0f, restAngleDeadZoneDegrees))
        {
            sourceRollDegrees = 0f;
        }

        float correction = Mathf.Clamp(
            sourceRollDegrees,
            -Mathf.Max(0f, maximumCorrectionDegrees),
            Mathf.Max(0f, maximumCorrectionDegrees));

        bool applied = false;

        if (leftEyeAdvanced && _left != null)
        {
            _left.SetSampleFrameRotationDegrees(correction);
            applied = true;
        }

        if (rightEyeAdvanced && _right != null)
        {
            _right.SetSampleFrameRotationDegrees(correction);
            applied = true;
        }

        if (mouthAdvanced && _mouth != null)
        {
            _mouth.SetSampleFrameRotationDegrees(correction);
            applied = true;
        }

        if (!applied)
        {
            return true;
        }

        _hasAppliedIdentity = true;
        _lastAppliedFrameId = snapshot.frameId;
        _lastAppliedCameraGeneration = snapshot.cameraGeneration;
        _lastAppliedTrackingSessionGeneration =
            snapshot.trackingSessionGeneration;
        _lastAppliedProviderGeneration = snapshot.providerGeneration;
        _lastAppliedModelGeneration = snapshot.modelGeneration;

        debugOperational = true;
        debugEyeLineAngle = sourceRollDegrees;
        debugAppliedRotation = correction;
        IsOperational = true;
        EyeLineAngleDegrees = sourceRollDegrees;
        AppliedRotationDegrees = correction;
        return true;
    }

    private bool CanAcceptSemanticTransaction(
        FacePartCropper cropper,
        long semanticTimestamp,
        ulong canonicalFrameId,
        KiwiFaceGeometryTransactionService.AcceptedSnapshot snapshot,
        out float sourceRollDegrees)
    {
        sourceRollDegrees = 0f;

        if (
            !isActiveAndEnabled ||
            !enableHeadLocalSampleFrame)
        {
            return false;
        }

        RefreshReferences(false);
        return
            _cropper == cropper &&
            _left != null &&
            _right != null &&
            _mouth != null &&
            cropper.runner != null &&
            semanticTimestamp >= 0L &&
            canonicalFrameId != 0UL &&
            snapshot.isValid &&
            cropper.runner.TryGetFacePartGeometrySnapshot(
                semanticTimestamp,
                out KiwiFaceGeometryTransactionService.AcceptedSnapshot
                    currentSnapshot,
                out ulong currentCanonicalFrameId) &&
            currentCanonicalFrameId == canonicalFrameId &&
            snapshot.HasSameIdentity(currentSnapshot) &&
            snapshot.frameId > _lastAppliedFrameId &&
            snapshot.pose.TryGetImagePlaneRollDegrees(
                out sourceRollDegrees);
    }

    private void RefreshReferences(bool force)
    {
        if (force || _cropper == null)
        {
            _cropper = FindFirstObjectByType<FacePartCropper>(
                FindObjectsInactive.Include);
        }

        SurfaceFittedRawImage left =
            _cropper != null
                ? _cropper.leftEyeImage as SurfaceFittedRawImage
                : null;
        SurfaceFittedRawImage right =
            _cropper != null
                ? _cropper.rightEyeImage as SurfaceFittedRawImage
                : null;
        SurfaceFittedRawImage mouth =
            _cropper != null
                ? _cropper.mouthImage as SurfaceFittedRawImage
                : null;

        int signature =
            GetInstanceIdSafe(left) * 486187739 ^
            GetInstanceIdSafe(right) * 16777619 ^
            GetInstanceIdSafe(mouth);

        if (force || signature != _lastBindingSignature)
        {
            ResetPartRotations();
            _left = left;
            _right = right;
            _mouth = mouth;
            _lastBindingSignature = signature;
            ResetBridgeIdentity();
        }
    }

    private void ResetBridgeStateAndRotations()
    {
        ResetPartRotations();
        ResetBridgeIdentity();
    }

    private void ResetBridgeIdentity()
    {
        _hasAppliedIdentity = false;
        _lastAppliedFrameId = 0UL;
        _lastAppliedCameraGeneration = 0;
        _lastAppliedTrackingSessionGeneration = 0;
        _lastAppliedProviderGeneration = 0;
        _lastAppliedModelGeneration = 0;

        debugOperational = false;
        debugEyeLineAngle = 0f;
        debugAppliedRotation = 0f;
        IsOperational = false;
        EyeLineAngleDegrees = 0f;
        AppliedRotationDegrees = 0f;
    }

    private void ResetPartRotations()
    {
        if (_left != null)
        {
            _left.ResetSampleFrameRotation();
        }

        if (_right != null)
        {
            _right.ResetSampleFrameRotation();
        }

        if (_mouth != null)
        {
            _mouth.ResetSampleFrameRotation();
        }
    }

    private static int GetInstanceIdSafe(Object value)
    {
        return value != null ? value.GetInstanceID() : 0;
    }
}
