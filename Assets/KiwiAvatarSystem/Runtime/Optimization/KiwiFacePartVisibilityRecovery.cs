using System;
using System.Collections;
using System.Reflection;
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
/// - cached coordinator visibility/surface-normal state is reset on recovery.
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
    private KiwiFacePartQualityCoordinator _coordinator;
    private KiwiTrackingProviderHub _trackingHub;
    private FaceLandmarkerRunner _runner;

    private bool _wasFrontRecoveryActive;
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

    private FieldInfo _leftStateField;
    private FieldInfo _rightStateField;
    private FieldInfo _mouthStateField;
    private FieldInfo _surfaceSignsField;

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
        _coordinator = null;
        _trackingHub = null;
        _runner = null;

        _wasFrontRecoveryActive =
            false;

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
            bool hardHidden =
                IsHardHidden(
                    _cropper.leftEyeImage) ||
                IsHardHidden(
                    _cropper.rightEyeImage) ||
                IsHardHidden(
                    _cropper.mouthImage);

            RecoverCanvasAlpha(
                _cropper.leftEyeImage);

            RecoverCanvasAlpha(
                _cropper.rightEyeImage);

            RecoverCanvasAlpha(
                _cropper.mouthImage);

            if (
                hardHidden ||
                !_wasFrontRecoveryActive
            )
            {
                ResetCoordinatorRecoveryState();

                if (hardHidden)
                {
                    _recoveryCount++;
                }
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

        _wasFrontRecoveryActive =
            frontRecovery;

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

        ResetCoordinatorRecoveryState();

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

        // Hide only the presentation renderer. Do not disable the component:
        // Cropper/ShapeMask must keep running so the first valid contour can
        // automatically reopen the part.
        image.canvasRenderer.SetAlpha(0f);
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
        FacePrecisionTrackingData data =
            default;

        if (_trackingHub != null)
        {
            if (
                _trackingHub.TryGetLatestFrame(
                    out data,
                    out _)
            )
            {
                return
                    data.isValid &&
                    data.frameId > 0UL;
            }

            // The Hub is the rigid freshness authority. Do not bypass a stale
            // Hub decision with a direct Runner read just to trigger a visual
            // recovery; that can reset otherwise-correct held masks during a
            // short semantic/ML stall.
            return false;
        }

        if (
            _runner != null &&
            _runner.TryGetLatestPrecisionTrackingData(
                out data)
        )
        {
            return
                data.isValid &&
                data.frameId > 0UL;
        }

        return false;
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

        image.canvasRenderer.SetAlpha(
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

    private void ResetCoordinatorRecoveryState()
    {
        if (_coordinator == null)
        {
            return;
        }

        CacheCoordinatorReflection();

        _leftStateField?.SetValue(
            _coordinator,
            1f);

        _rightStateField?.SetValue(
            _coordinator,
            1f);

        _mouthStateField?.SetValue(
            _coordinator,
            1f);

        object signs =
            _surfaceSignsField != null
                ? _surfaceSignsField.GetValue(
                    _coordinator)
                : null;

        if (signs is IDictionary dictionary)
        {
            dictionary.Clear();
        }
    }

    private void CacheCoordinatorReflection()
    {
        if (
            _coordinator == null ||
            _leftStateField != null
        )
        {
            return;
        }

        Type type =
            _coordinator.GetType();

        const BindingFlags flags =
            BindingFlags.Instance |
            BindingFlags.NonPublic;

        _leftStateField =
            type.GetField(
                "_leftEyeVisibilityState",
                flags);

        _rightStateField =
            type.GetField(
                "_rightEyeVisibilityState",
                flags);

        _mouthStateField =
            type.GetField(
                "_mouthVisibilityState",
                flags);

        _surfaceSignsField =
            type.GetField(
                "_surfaceNormalSigns",
                flags);
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

        image.canvasRenderer.SetAlpha(
            1f);

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
            _coordinator == null
        )
        {
            _coordinator =
                FindFirstObjectByType<
                    KiwiFacePartQualityCoordinator>(
                    FindObjectsInactive.Include);

            _leftStateField = null;
            _rightStateField = null;
            _mouthStateField = null;
            _surfaceSignsField = null;
        }

        if (
            force ||
            _trackingHub == null
        )
        {
            _trackingHub =
                FindFirstObjectByType<
                    KiwiTrackingProviderHub>(
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
