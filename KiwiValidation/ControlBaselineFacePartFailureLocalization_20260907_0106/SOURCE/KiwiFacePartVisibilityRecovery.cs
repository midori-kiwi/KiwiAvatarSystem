using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v3.3 face-part recovery watchdog.
///
/// Guarantees:
/// - legacy _PoseVisibility latch values are released;
/// - a valid near-frontal pose fails open instead of leaving a part hidden;
/// - blink/semantic opacity is not forced open;
/// - semantic repair is isolated from global/provider/root recovery state.
/// </summary>
[DefaultExecutionOrder(1250)]
[DisallowMultipleComponent]
public sealed class KiwiFacePartVisibilityRecovery : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Face-Part Visibility Recovery";

    private static readonly int PoseVisibilityId =
        Shader.PropertyToID(
            "_PoseVisibility");

    private static readonly int MaskVisibilityId =
        Shader.PropertyToID(
            "_MaskVisibility");

    private static readonly int MaskPointCountId =
        Shader.PropertyToID(
            "_MaskPointCount");

    [Header("Front recovery")]
    public bool enableFrontRecovery = true;

    [Range(5f, 40f)]
    public float frontalRecoveryYaw = 24f;

    [Range(20f, 240f)]
    public float frontalShowResponse = 120f;

    [Range(0f, 0.25f)]
    public float hardHiddenAlpha = 0.03f;

    [Header("Legacy latch")]
    public bool releaseLegacyPoseVisibilityLatch = true;

    [Header("All-parts fail-open recovery")]
    [Tooltip("If all three face parts are simultaneously non-renderable while stable near-frontal tracking is valid, reset only their mask/contour presentation state. This never writes avatar root pose.")]
    public bool recoverAllPartsMissing = true;

    [Range(0.10f, 1.50f)]
    public float allPartsMissingGraceSeconds = 0.30f;

    [Range(0.25f, 5f)]
    public float allPartsRecoveryCooldownSeconds = 1.0f;

    [Header("Diagnostics")]
    [SerializeField] private bool debugFrontRecoveryActive;
    [SerializeField] private float debugYaw;
    [SerializeField] private float debugLeftEyeCanvasAlpha = 1f;
    [SerializeField] private float debugRightEyeCanvasAlpha = 1f;
    [SerializeField] private float debugMouthCanvasAlpha = 1f;
    [SerializeField] private float debugLeftEyeMaskVisibility = 1f;
    [SerializeField] private float debugRightEyeMaskVisibility = 1f;
    [SerializeField] private float debugMouthMaskVisibility = 1f;
    [SerializeField] private int debugLeftEyeMaskPoints;
    [SerializeField] private int debugRightEyeMaskPoints;
    [SerializeField] private int debugMouthMaskPoints;
    [SerializeField] private bool debugAllPartsMissingRecovery;
    [SerializeField] private int debugRecoveryCount;
    [SerializeField] private bool debugMaskReadinessComplete;

    private FacePartCropper _cropper;
    private KiwiFaceMotion _faceMotion;
    private KiwiAvatarRuntimeManager _runtimeManager;
    private FaceLandmarkerRunner _runner;

    private int _recoveryCount;
    private FacePartShapeMask[] _shapeMasks;
    private double _allPartsMissingSince = -1.0;
    private double _nextAllPartsRecoveryRealtime;

    // v4.8 startup/recovery render gate. A RawImage is not allowed to render
    // as an unmasked rectangle before its semantic contour has produced at
    // least three mask points. These latches reopen automatically on the first
    // valid contour and are reset only on scene/model recovery.
    private bool _leftMaskReady;
    private bool _rightMaskReady;
    private bool _mouthMaskReady;

    public int RecoveryCount =>
        _recoveryCount;

    public float LeftEyeCanvasAlpha =>
        GetCanvasAlpha(
            _cropper != null
                ? _cropper.leftEyeImage
                : null);

    public float RightEyeCanvasAlpha =>
        GetCanvasAlpha(
            _cropper != null
                ? _cropper.rightEyeImage
                : null);

    public float MouthCanvasAlpha =>
        GetCanvasAlpha(
            _cropper != null
                ? _cropper.mouthImage
                : null);

    public float LeftEyeMaskVisibility =>
        debugLeftEyeMaskVisibility;

    public float RightEyeMaskVisibility =>
        debugRightEyeMaskVisibility;

    public float MouthMaskVisibility =>
        debugMouthMaskVisibility;

    public int LeftEyeMaskPoints =>
        debugLeftEyeMaskPoints;

    public int RightEyeMaskPoints =>
        debugRightEyeMaskPoints;

    public int MouthMaskPoints =>
        debugMouthMaskPoints;

    public bool AllPartsMissingRecoveryActive =>
        debugAllPartsMissingRecovery;

    public bool MaskReadinessComplete =>
        _leftMaskReady &&
        _rightMaskReady &&
        _mouthMaskReady;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiFacePartVisibilityRecovery>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(
                RuntimeObjectName);

        DontDestroyOnLoad(
            host);

        host.AddComponent<
            KiwiFacePartVisibilityRecovery>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(
            gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        RefreshReferences(true);
        ReleaseAllLegacyMaterialLatches();
    }

    private void OnDisable()
    {
        RestoreRendererOwnership();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        RestoreRendererOwnership();
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _cropper = null;
        _faceMotion = null;
        _runtimeManager = null;
        _runner = null;

        _allPartsMissingSince = -1.0;
        _nextAllPartsRecoveryRealtime = 0.0;
        _shapeMasks = null;
        ResetMaskReadiness();

        RefreshReferences(true);
        ReleaseAllLegacyMaterialLatches();
    }

    private void LateUpdate()
    {
        RefreshReferences(false);

        if (_cropper == null)
        {
            return;
        }

        // v4.8: legacy _PoseVisibility is released only at lifecycle/rebind
        // boundaries. Writing it to 1 every frame made this recovery watchdog a
        // second presentation owner and could override ShapeMask/SideView.
        UpdateMaskDiagnostics();
        ApplyMaskReadinessGate();

        float yaw =
            _faceMotion != null
                ? _faceMotion.RenderedYawDegrees
                : 0f;

        debugYaw =
            yaw;

        bool frontRecovery =
            enableFrontRecovery &&
            HasUsableTracking() &&
            (
                _runtimeManager == null ||
                !_runtimeManager.IsBusy
            ) &&
            Mathf.Abs(yaw) <=
                frontalRecoveryYaw;

        debugFrontRecoveryActive =
            frontRecovery;

        if (frontRecovery)
        {
            bool leftHardHidden =
                IsHardHidden(
                    _cropper.leftEyeImage);

            bool rightHardHidden =
                IsHardHidden(
                    _cropper.rightEyeImage);

            bool mouthHardHidden =
                IsHardHidden(
                    _cropper.mouthImage);

            RecoverCanvasAlpha(
                _cropper.leftEyeImage);

            RecoverCanvasAlpha(
                _cropper.rightEyeImage);

            RecoverCanvasAlpha(
                _cropper.mouthImage);

            KiwiRecoveryDomainCoordinator.SemanticComponent
                recoveredComponents =
                    KiwiRecoveryDomainCoordinator.SemanticComponent.None;

            if (leftHardHidden)
            {
                recoveredComponents |=
                    KiwiRecoveryDomainCoordinator.SemanticComponent.LeftEye;
            }

            if (rightHardHidden)
            {
                recoveredComponents |=
                    KiwiRecoveryDomainCoordinator.SemanticComponent.RightEye;
            }

            if (mouthHardHidden)
            {
                recoveredComponents |=
                    KiwiRecoveryDomainCoordinator.SemanticComponent.Mouth;
            }

            if (
                recoveredComponents !=
                    KiwiRecoveryDomainCoordinator.SemanticComponent.None
            )
            {
                KiwiRecoveryDomainCoordinator.PulseSemanticRecovery(
                    recoveredComponents,
                    KiwiRecoveryDomainCoordinator.SemanticRecoveryReason
                        .VisibilityLatch,
                    nameof(KiwiFacePartVisibilityRecovery),
                    0.35f);

                _recoveryCount++;
            }

            if (MaskReadinessComplete)
            {
                UpdateAllPartsMissingRecovery();
            }
            else
            {
                _allPartsMissingSince = -1.0;
                debugAllPartsMissingRecovery = false;
            }
        }
        else
        {
            _allPartsMissingSince = -1.0;
            debugAllPartsMissingRecovery = false;
        }

        debugMaskReadinessComplete =
            MaskReadinessComplete;

        debugRecoveryCount =
            _recoveryCount;

        debugLeftEyeCanvasAlpha =
            LeftEyeCanvasAlpha;

        debugRightEyeCanvasAlpha =
            RightEyeCanvasAlpha;

        debugMouthCanvasAlpha =
            MouthCanvasAlpha;
    }


    private void UpdateAllPartsMissingRecovery()
    {
        debugAllPartsMissingRecovery = false;

        if (!recoverAllPartsMissing)
        {
            _allPartsMissingSince = -1.0;
            debugAllPartsMissingRecovery = false;
            return;
        }

        bool allMissing =
            IsPartNonRenderable(_cropper.leftEyeImage) &&
            IsPartNonRenderable(_cropper.rightEyeImage) &&
            IsPartNonRenderable(_cropper.mouthImage);

        if (!allMissing)
        {
            _allPartsMissingSince = -1.0;
            debugAllPartsMissingRecovery = false;
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        if (_allPartsMissingSince < 0.0)
        {
            _allPartsMissingSince = now;
            return;
        }

        if (
            now - _allPartsMissingSince <
                Mathf.Max(0.10f, allPartsMissingGraceSeconds) ||
            now < _nextAllPartsRecoveryRealtime
        )
        {
            return;
        }

        _nextAllPartsRecoveryRealtime =
            now +
            Mathf.Max(
                0.25f,
                allPartsRecoveryCooldownSeconds);

        _allPartsMissingSince = now;

        if (_shapeMasks == null || _shapeMasks.Length == 0)
        {
            _shapeMasks =
                FindObjectsByType<FacePartShapeMask>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
        }

        ResetMaskReadiness();

        if (_shapeMasks != null)
        {
            for (int i = 0; i < _shapeMasks.Length; i++)
            {
                if (_shapeMasks[i] != null)
                {
                    _shapeMasks[i].ResetContour();
                }
            }
        }

        // Reset only the presentation latch. Semantic blink/mouth decisions are
        // rebuilt from the latest landmark sample on the following frame.
        ReleaseMaskVisibility(_cropper.leftEyeImage);
        ReleaseMaskVisibility(_cropper.rightEyeImage);
        ReleaseMaskVisibility(_cropper.mouthImage);

        KiwiRecoveryDomainCoordinator.PulseSemanticRecovery(
            KiwiRecoveryDomainCoordinator.SemanticComponent.AllFaceParts |
            KiwiRecoveryDomainCoordinator.SemanticComponent.Mask,
            KiwiRecoveryDomainCoordinator.SemanticRecoveryReason
                .AllPartsMissing |
            KiwiRecoveryDomainCoordinator.SemanticRecoveryReason
                .MaskInvalid,
            nameof(KiwiFacePartVisibilityRecovery),
            0.75f);

        _recoveryCount++;
        debugAllPartsMissingRecovery = true;
    }

    private static bool IsPartNonRenderable(
        RawImage image)
    {
        if (image == null || !image.isActiveAndEnabled)
        {
            return true;
        }

        if (image.canvasRenderer.GetAlpha() <= 0.03f)
        {
            return true;
        }

        Material material = image.material;
        if (material == null)
        {
            return false;
        }

        if (
            material.HasProperty(MaskVisibilityId) &&
            material.GetFloat(MaskVisibilityId) <= 0.03f
        )
        {
            return true;
        }

        if (
            material.HasProperty(MaskPointCountId) &&
            material.GetFloat(MaskPointCountId) < 3f
        )
        {
            return true;
        }

        return false;
    }

    private static void ReleaseMaskVisibility(
        RawImage image)
    {
        if (image == null || image.material == null)
        {
            return;
        }

        if (image.material.HasProperty(MaskVisibilityId))
        {
            image.material.SetFloat(
                MaskVisibilityId,
                1f);
        }

        if (image.material.HasProperty(PoseVisibilityId))
        {
            image.material.SetFloat(
                PoseVisibilityId,
                1f);
        }
    }

    private void UpdateMaskDiagnostics()
    {
        debugLeftEyeMaskVisibility =
            GetMaterialFloat(
                _cropper.leftEyeImage,
                MaskVisibilityId,
                1f);

        debugRightEyeMaskVisibility =
            GetMaterialFloat(
                _cropper.rightEyeImage,
                MaskVisibilityId,
                1f);

        debugMouthMaskVisibility =
            GetMaterialFloat(
                _cropper.mouthImage,
                MaskVisibilityId,
                1f);

        debugLeftEyeMaskPoints =
            Mathf.RoundToInt(
                GetMaterialFloat(
                    _cropper.leftEyeImage,
                    MaskPointCountId,
                    0f));

        debugRightEyeMaskPoints =
            Mathf.RoundToInt(
                GetMaterialFloat(
                    _cropper.rightEyeImage,
                    MaskPointCountId,
                    0f));

        debugMouthMaskPoints =
            Mathf.RoundToInt(
                GetMaterialFloat(
                    _cropper.mouthImage,
                    MaskPointCountId,
                    0f));

        _leftMaskReady |=
            debugLeftEyeMaskPoints >= 3;

        _rightMaskReady |=
            debugRightEyeMaskPoints >= 3;

        _mouthMaskReady |=
            debugMouthMaskPoints >= 3;
    }

    private void ApplyMaskReadinessGate()
    {
        GateUnreadyPart(
            _cropper != null
                ? _cropper.leftEyeImage
                : null,
            _leftMaskReady);

        GateUnreadyPart(
            _cropper != null
                ? _cropper.rightEyeImage
                : null,
            _rightMaskReady);

        GateUnreadyPart(
            _cropper != null
                ? _cropper.mouthImage
                : null,
            _mouthMaskReady);
    }

    private static void GateUnreadyPart(
        RawImage image,
        bool ready)
    {
        if (image == null || ready)
        {
            return;
        }

        // KIWI_V5_1_PHASE4_PRESENTATION_ARBITRATION
        // Hide only the presentation renderer. Do not disable the component:
        // Cropper/ShapeMask must keep running so the first valid contour can
        // automatically reopen the part. The final resolver is the sole
        // CanvasRenderer alpha writer.
        KiwiFacePartPresentationResolver
            .SubmitHardHide(
                image,
                KiwiFacePartPresentationResolver
                    .Reason.MaskNotReady);
    }

    private bool IsMaskReady(
        RawImage image)
    {
        if (_cropper == null || image == null)
        {
            return false;
        }

        if (image == _cropper.leftEyeImage)
        {
            return _leftMaskReady;
        }

        if (image == _cropper.rightEyeImage)
        {
            return _rightMaskReady;
        }

        if (image == _cropper.mouthImage)
        {
            return _mouthMaskReady;
        }

        return true;
    }

    private void ResetMaskReadiness()
    {
        _leftMaskReady = false;
        _rightMaskReady = false;
        _mouthMaskReady = false;
        debugMaskReadinessComplete = false;
    }

    private static float GetMaterialFloat(
        RawImage image,
        int propertyId,
        float fallback)
    {
        if (
            image == null ||
            image.material == null ||
            !image.material.HasProperty(propertyId)
        )
        {
            return fallback;
        }

        return image.material.GetFloat(propertyId);
    }

    private bool HasUsableTracking()
    {
        // KIWI_V5_1_PHASE5_CANONICAL_RECOVERY_RIGID
        // Recovery is presentation-only and must never react to a Runner sample
        // newer than the Root frame latched for this display cycle.
        return
            _runner != null &&
            KiwiCommercialRigidMotionPolicy.TryGetAuthoritativeFrame(
                _runner,
                out FacePrecisionTrackingData data) &&
            data.isValid &&
            data.frameId > 0UL;
    }

    private void RecoverCanvasAlpha(
        RawImage image)
    {
        if (
            image == null ||
            !IsMaskReady(image)
        )
        {
            return;
        }

        float current =
            image.canvasRenderer
                .GetAlpha();

        float dt =
            Mathf.Max(
                0.000001f,
                Time.unscaledDeltaTime);

        float next =
            Mathf.Lerp(
                current,
                1f,
                1f -
                Mathf.Exp(
                    -Mathf.Max(
                        0f,
                        frontalShowResponse) *
                    dt));

        if (next >= 0.999f)
        {
            next = 1f;
        }

        // Front recovery is a floor request, not an unconditional show. The
        // resolver applies it only when no intentional quality/far-eye cap is
        // suppressing this part.
        KiwiFacePartPresentationResolver
            .SubmitRecoveryFloor(
                image,
                next);
    }

    private bool IsHardHidden(
        RawImage image)
    {
        return
            image != null &&
            image.canvasRenderer.GetAlpha() <=
                hardHiddenAlpha;
    }

    private void ReleaseAllLegacyMaterialLatches()
    {
        if (_cropper == null)
        {
            return;
        }

        ReleasePoseVisibility(
            _cropper.leftEyeImage);

        ReleasePoseVisibility(
            _cropper.rightEyeImage);

        ReleasePoseVisibility(
            _cropper.mouthImage);
    }

    private static void ReleasePoseVisibility(
        RawImage image)
    {
        if (
            image == null ||
            image.material == null ||
            !image.material.HasProperty(
                PoseVisibilityId)
        )
        {
            return;
        }

        image.material.SetFloat(
            PoseVisibilityId,
            1f);
    }

    private void RestoreRendererOwnership()
    {
        if (_cropper == null)
        {
            return;
        }

        RestorePart(
            _cropper.leftEyeImage);

        RestorePart(
            _cropper.rightEyeImage);

        RestorePart(
            _cropper.mouthImage);
    }

    private static void RestorePart(
        RawImage image)
    {
        if (image == null)
        {
            return;
        }

        // Renderer alpha is intentionally not restored here. Requests are
        // frame-scoped, so disabling this watchdog automatically relinquishes
        // its influence while the presentation resolver keeps final ownership.
        ReleasePoseVisibility(
            image);
    }

    private static float GetCanvasAlpha(
        RawImage image)
    {
        return
            image != null
                ? image.canvasRenderer.GetAlpha()
                : 1f;
    }

    private void RefreshReferences(
        bool force)
    {
        if (force)
        {
            _shapeMasks = null;
        }

        if (
            force ||
            _cropper == null
        )
        {
            FacePartCropper previousCropper =
                _cropper;

            _cropper =
                FindFirstObjectByType<
                    FacePartCropper>(
                    FindObjectsInactive.Include);

            if (
                previousCropper != _cropper
            )
            {
                ResetMaskReadiness();
            }
        }

        if (
            force ||
            _faceMotion == null
        )
        {
            _faceMotion =
                FindFirstObjectByType<
                    KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _runtimeManager == null
        )
        {
            _runtimeManager =
                FindFirstObjectByType<
                    KiwiAvatarRuntimeManager>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _runner == null
        )
        {
            _runner =
                FindFirstObjectByType<
                    FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }
    }
}
