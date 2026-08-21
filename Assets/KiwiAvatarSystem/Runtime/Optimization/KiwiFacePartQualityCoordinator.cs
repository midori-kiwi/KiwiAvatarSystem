using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v2.6 face-part containment.
///
/// Source crops remain generous, while semantic visibility is independently
/// constrained. Side-view hiding no longer relies primarily on RenderedYaw:
/// the two fitted eye centers provide a direct camera-depth separation signal.
///
/// CanvasRenderer alpha is also driven as the final safety gate, so a far-side
/// patch cannot remain visible merely because a shader material did not consume
/// _PoseVisibility.
/// </summary>
[DefaultExecutionOrder(990)]
[DisallowMultipleComponent]
public sealed class KiwiFacePartQualityCoordinator : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Face-Part Quality Coordinator";

    private static readonly int PoseVisibilityId =
        Shader.PropertyToID("_PoseVisibility");

    private static readonly int SampleScaleXYId =
        Shader.PropertyToID("_SampleScaleXY");

    [Header("Overscan source crop")]
    public bool applySafeCropPreset = true;

    [Range(1.0f, 3.0f)]
    public float eyeWidthScale = 1.62f;

    [Range(0.2f, 1.5f)]
    public float eyeHeightToWidth = 0.65f;

    [Range(0f, 0.1f)]
    public float eyePaddingX = 0.017f;

    [Range(0f, 0.1f)]
    public float eyePaddingY = 0.015f;

    [Range(1.0f, 3.0f)]
    public float mouthWidthScale = 1.52f;

    [Range(0.2f, 1.5f)]
    public float mouthHeightToWidth = 0.66f;

    [Range(0f, 0.1f)]
    public float mouthPaddingX = 0.018f;

    [Range(0f, 0.1f)]
    public float mouthPaddingY = 0.020f;

    [Range(0f, 0.8f)]
    public float mouthContourSafetyX = 0.20f;

    [Range(0f, 0.8f)]
    public float mouthContourSafetyY = 0.24f;

    [Header("Tight semantic mask")]
    [Range(-0.10f, 0.50f)]
    public float eyeContourMargin = 0.095f;

    [Range(-0.10f, 0.20f)]
    public float mouthContourMargin = 0.012f;

    [Range(0.001f, 0.10f)]
    public float maskFeather = 0.028f;

    [Range(0f, 0.15f)]
    public float cropLocalSafetyMargin = 0.014f;

    [Range(0f, 0.01f)]
    public float fittedSurfaceOffset = 0.0005f;

    [Header("Depth-ratio far-eye guard")]
    public bool enableDepthRatioGuard = true;

    [Tooltip("Far eye stays fully visible below this normalized depth separation.")]
    [Range(0f, 1f)]
    public float farEyeDepthFadeStart = 0.20f;

    [Tooltip("Far eye is completely hidden above this normalized depth separation.")]
    [Range(0.05f, 1.5f)]
    public float farEyeDepthHidden = 0.55f;

    [Tooltip("Small depth changes near frontal pose are ignored.")]
    [Range(0f, 0.02f)]
    public float minimumAbsoluteDepthDifference = 0.001f;

    [Header("Surface-facing guard")]
    public bool enableSurfaceFacingGuard = true;

    [Range(0.05f, 0.95f)]
    public float fullVisibilityFacing = 0.46f;

    [Range(-0.20f, 0.80f)]
    public float hiddenVisibilityFacing = 0.14f;

    [Range(-0.20f, 0.30f)]
    public float mouthHiddenFacingBias = -0.06f;

    [Range(5f, 30f)]
    public float facingCalibrationMaximumYaw = 18f;

    [Header("Yaw fallback")]
    public bool enableYawFallback = true;

    [Range(10f, 60f)]
    public float farEyeFadeStartYaw = 32f;

    [Range(20f, 80f)]
    public float farEyeHiddenYaw = 48f;

    [Range(30f, 80f)]
    public float nearEyeFadeStartYaw = 62f;

    [Range(40f, 89f)]
    public float nearEyeHiddenYaw = 76f;

    [Range(20f, 80f)]
    public float mouthFadeStartYaw = 58f;

    [Range(30f, 89f)]
    public float mouthHiddenYaw = 74f;

    [Header("Final mouth display cap")]
    public bool clampFinalMouthDisplaySize = true;

    [Tooltip("Final visible width cannot exceed this relative shader scale.")]
    [Range(0.35f, 1f)]
    public float maximumMouthVisibleWidth = 0.72f;

    [Tooltip("Final visible height cannot exceed this relative shader scale.")]
    [Range(0.35f, 1f)]
    public float maximumMouthVisibleHeight = 0.68f;

    [Header("Visibility hysteresis")]
    [Tooltip("Hide quickly before a fitted patch can detach from the silhouette.")]
    [Range(5f, 120f)]
    public float visibilityHideResponse = 48f;

    [Tooltip("Restore more slowly so threshold noise cannot flicker the part on/off.")]
    [Range(5f, 120f)]
    public float visibilityShowResponse = 22f;

    [Header("Post-switch tracking warmup")]
    [Range(1, 6)]
    public int requiredFreshTrackingFramesAfterSwap = 2;

    [Range(0.05f, 1.0f)]
    public float maximumTrackingWarmupSeconds = 0.35f;

    [Header("Avatar swap safety")]
    public bool refitAfterAvatarSwitch = true;

    [Range(1, 8)]
    public int settleFramesBeforeFit = 2;

    [Range(0, 3)]
    public int maximumFitRetries = 2;

    [Range(0.02f, 0.50f)]
    public float facePartFadeInSeconds = 0.09f;

    [Header("Diagnostics")]
    [SerializeField] private float debugYaw;
    [SerializeField] private float debugEyeDepthRatio;
    [SerializeField] private string debugFarEye = "-";
    [SerializeField] private float debugLeftFacing;
    [SerializeField] private float debugRightFacing;
    [SerializeField] private float debugMouthFacing;
    [SerializeField] private float debugLeftEyeVisibility = 1f;
    [SerializeField] private float debugRightEyeVisibility = 1f;
    [SerializeField] private float debugMouthVisibility = 1f;
    [SerializeField] private float debugSwapGate = 1f;
    [SerializeField] private int debugFitRetries;
    [SerializeField] private float debugLastFitSuccessRate = 1f;

    private FacePartCropper _cropper;
    private KiwiTrackingProviderHub _trackingHub;
    private FaceLandmarkerRunner _runner;
    private KiwiFaceMotion _faceMotion;
    private KiwiAvatarRuntimeManager _runtimeManager;
    private Camera _camera;

    private FacePartShapeMask[] _shapeMasks =
        Array.Empty<FacePartShapeMask>();

    private SurfaceFittedRawImage[] _surfaceParts =
        Array.Empty<SurfaceFittedRawImage>();

    private KiwiSurfaceFitter[] _surfaceFitters =
        Array.Empty<KiwiSurfaceFitter>();

    private readonly Dictionary<int, float>
        _surfaceNormalSigns =
            new Dictionary<int, float>();

    private int _lastPresetSignature;
    private bool _lastBusy;
    private string _lastAvatarName =
        string.Empty;

    private bool _swapSettling;
    private int _settleFramesRemaining;
    private int _fitRetryCount;
    private float _swapGate = 1f;
    private bool _fadeInAfterFit;

    private float _leftEyeVisibilityState = 1f;
    private float _rightEyeVisibilityState = 1f;
    private float _mouthVisibilityState = 1f;

    private int _freshTrackingFramesAfterSwap;
    private ulong _lastSwapWarmupFrameId;
    private double _swapWarmupStartedRealtime;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiFacePartQualityCoordinator>(
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
            KiwiFacePartQualityCoordinator>();
    }

    private void Start()
    {
        RefreshReferences(true);
        ApplySafetyPresetIfNeeded(true);

        if (_runtimeManager != null)
        {
            _lastBusy =
                _runtimeManager.IsBusy;

            _lastAvatarName =
                _runtimeManager.CurrentAvatarName ??
                string.Empty;
        }
    }

    private void LateUpdate()
    {
        RefreshReferences(false);
        ApplySafetyPresetIfNeeded(false);

        UpdateAvatarSwapState();
        UpdateSwapRefit();

        float yaw =
            _faceMotion != null
                ? _faceMotion.RenderedYawDegrees
                : 0f;

        debugYaw =
            yaw;

        float leftVisibility = 1f;
        float rightVisibility = 1f;
        float mouthVisibility = 1f;

        SurfaceFittedRawImage left =
            _cropper != null
                ? _cropper.leftEyeImage
                    as SurfaceFittedRawImage
                : null;

        SurfaceFittedRawImage right =
            _cropper != null
                ? _cropper.rightEyeImage
                    as SurfaceFittedRawImage
                : null;

        SurfaceFittedRawImage mouth =
            _cropper != null
                ? _cropper.mouthImage
                    as SurfaceFittedRawImage
                : null;

        bool leftFacingValid =
            TryGetCalibratedFacing(
                left,
                yaw,
                out float leftFacing);

        bool rightFacingValid =
            TryGetCalibratedFacing(
                right,
                yaw,
                out float rightFacing);

        bool mouthFacingValid =
            TryGetCalibratedFacing(
                mouth,
                yaw,
                out float mouthFacing);

        debugLeftFacing =
            leftFacing;

        debugRightFacing =
            rightFacing;

        debugMouthFacing =
            mouthFacing;

        if (enableSurfaceFacingGuard)
        {
            if (leftFacingValid)
            {
                leftVisibility =
                    Mathf.Min(
                        leftVisibility,
                        FacingVisibility(
                            leftFacing,
                            hiddenVisibilityFacing,
                            fullVisibilityFacing));
            }

            if (rightFacingValid)
            {
                rightVisibility =
                    Mathf.Min(
                        rightVisibility,
                        FacingVisibility(
                            rightFacing,
                            hiddenVisibilityFacing,
                            fullVisibilityFacing));
            }

            if (mouthFacingValid)
            {
                mouthVisibility =
                    Mathf.Min(
                        mouthVisibility,
                        FacingVisibility(
                            mouthFacing,
                            hiddenVisibilityFacing +
                                mouthHiddenFacingBias,
                            fullVisibilityFacing));
            }
        }

        bool depthGuardResolved =
            false;

        if (enableDepthRatioGuard)
        {
            depthGuardResolved =
                ApplyDepthRatioGuard(
                    left,
                    right,
                    ref leftVisibility,
                    ref rightVisibility);
        }

        if (
            enableYawFallback &&
            Mathf.Abs(yaw) >
                0.01f
        )
        {
            ApplyYawFallback(
                yaw,
                ref leftVisibility,
                ref rightVisibility,
                ref mouthVisibility,
                leftFacingValid ||
                    depthGuardResolved,
                rightFacingValid ||
                    depthGuardResolved);
        }

        leftVisibility *=
            _swapGate;

        rightVisibility *=
            _swapGate;

        mouthVisibility *=
            _swapGate;

        float visibilityDt =
            Mathf.Max(
                0.000001f,
                Time.unscaledDeltaTime);

        _leftEyeVisibilityState =
            FilterVisibility(
                _leftEyeVisibilityState,
                leftVisibility,
                visibilityDt);

        _rightEyeVisibilityState =
            FilterVisibility(
                _rightEyeVisibilityState,
                rightVisibility,
                visibilityDt);

        _mouthVisibilityState =
            FilterVisibility(
                _mouthVisibilityState,
                mouthVisibility,
                visibilityDt);

        leftVisibility =
            _leftEyeVisibilityState;

        rightVisibility =
            _rightEyeVisibilityState;

        mouthVisibility =
            _mouthVisibilityState;

        ApplyPartVisibility(
            _cropper != null
                ? _cropper.leftEyeImage
                : null,
            leftVisibility);

        ApplyPartVisibility(
            _cropper != null
                ? _cropper.rightEyeImage
                : null,
            rightVisibility);

        ApplyPartVisibility(
            _cropper != null
                ? _cropper.mouthImage
                : null,
            mouthVisibility);

        ClampFinalMouthSize();

        debugLeftEyeVisibility =
            leftVisibility;

        debugRightEyeVisibility =
            rightVisibility;

        debugMouthVisibility =
            mouthVisibility;

        debugSwapGate =
            _swapGate;
    }

    private bool ApplyDepthRatioGuard(
        SurfaceFittedRawImage left,
        SurfaceFittedRawImage right,
        ref float leftVisibility,
        ref float rightVisibility)
    {
        debugEyeDepthRatio =
            0f;

        debugFarEye =
            "-";

        if (
            left == null ||
            right == null ||
            _camera == null
        )
        {
            return false;
        }

        if (
            !TryGetSurfaceCenterWorld(
                left,
                out Vector3 leftWorld) ||
            !TryGetSurfaceCenterWorld(
                right,
                out Vector3 rightWorld)
        )
        {
            return false;
        }

        Vector3 leftCamera =
            _camera.transform
                .InverseTransformPoint(
                    leftWorld);

        Vector3 rightCamera =
            _camera.transform
                .InverseTransformPoint(
                    rightWorld);

        float depthDifference =
            Mathf.Abs(
                leftCamera.z -
                rightCamera.z);

        if (
            depthDifference <
                minimumAbsoluteDepthDifference
        )
        {
            return false;
        }

        float eyeDistance =
            Vector3.Distance(
                leftWorld,
                rightWorld);

        if (eyeDistance <= 0.00001f)
        {
            return false;
        }

        float ratio =
            depthDifference /
            eyeDistance;

        debugEyeDepthRatio =
            ratio;

        float farVisibility =
            1f -
            Smooth01(
                Mathf.InverseLerp(
                    farEyeDepthFadeStart,
                    Mathf.Max(
                        farEyeDepthFadeStart +
                        0.001f,
                        farEyeDepthHidden),
                    ratio));

        bool leftIsFar =
            leftCamera.z >
            rightCamera.z;

        if (leftIsFar)
        {
            leftVisibility =
                Mathf.Min(
                    leftVisibility,
                    farVisibility);

            debugFarEye =
                "Left";
        }
        else
        {
            rightVisibility =
                Mathf.Min(
                    rightVisibility,
                    farVisibility);

            debugFarEye =
                "Right";
        }

        return true;
    }

    private static bool TryGetSurfaceCenterWorld(
        SurfaceFittedRawImage part,
        out Vector3 world)
    {
        world =
            Vector3.zero;

        if (
            part == null ||
            !part.TryGetSurfaceLocalPosition(
                new Vector2(
                    0.5f,
                    0.5f),
                out Vector3 local)
        )
        {
            return false;
        }

        world =
            part.rectTransform
                .TransformPoint(
                    local);

        return
            IsFinite(
                world);
    }

    private bool TryGetCalibratedFacing(
        SurfaceFittedRawImage part,
        float yaw,
        out float facing)
    {
        facing =
            1f;

        if (
            part == null ||
            _camera == null ||
            !TryGetSurfaceNormalDot(
                part,
                out float rawDot)
        )
        {
            return false;
        }

        int id =
            part.GetInstanceID();

        if (
            !_surfaceNormalSigns.TryGetValue(
                id,
                out float sign)
        )
        {
            if (
                Mathf.Abs(yaw) <=
                    facingCalibrationMaximumYaw &&
                Mathf.Abs(rawDot) >=
                    0.20f
            )
            {
                sign =
                    rawDot >= 0f
                        ? 1f
                        : -1f;

                _surfaceNormalSigns[id] =
                    sign;
            }
            else
            {
                return false;
            }
        }

        facing =
            rawDot *
            sign;

        return
            !float.IsNaN(facing) &&
            !float.IsInfinity(facing);
    }

    private bool TryGetSurfaceNormalDot(
        SurfaceFittedRawImage part,
        out float dot)
    {
        dot =
            1f;

        if (
            part == null ||
            _camera == null
        )
        {
            return false;
        }

        if (
            !part.TryGetSurfaceLocalPosition(
                new Vector2(
                    0.42f,
                    0.50f),
                out Vector3 leftLocal) ||
            !part.TryGetSurfaceLocalPosition(
                new Vector2(
                    0.58f,
                    0.50f),
                out Vector3 rightLocal) ||
            !part.TryGetSurfaceLocalPosition(
                new Vector2(
                    0.50f,
                    0.42f),
                out Vector3 bottomLocal) ||
            !part.TryGetSurfaceLocalPosition(
                new Vector2(
                    0.50f,
                    0.58f),
                out Vector3 topLocal) ||
            !part.TryGetSurfaceLocalPosition(
                new Vector2(
                    0.50f,
                    0.50f),
                out Vector3 centerLocal)
        )
        {
            return false;
        }

        Transform transform =
            part.rectTransform;

        Vector3 left =
            transform.TransformPoint(
                leftLocal);

        Vector3 right =
            transform.TransformPoint(
                rightLocal);

        Vector3 bottom =
            transform.TransformPoint(
                bottomLocal);

        Vector3 top =
            transform.TransformPoint(
                topLocal);

        Vector3 center =
            transform.TransformPoint(
                centerLocal);

        Vector3 normal =
            Vector3.Cross(
                right - left,
                top - bottom);

        if (
            normal.sqrMagnitude <
                0.000000001f
        )
        {
            return false;
        }

        normal.Normalize();

        Vector3 toCamera =
            _camera.transform.position -
            center;

        if (
            toCamera.sqrMagnitude <
                0.000000001f
        )
        {
            return false;
        }

        toCamera.Normalize();

        dot =
            Vector3.Dot(
                normal,
                toCamera);

        return
            !float.IsNaN(dot) &&
            !float.IsInfinity(dot);
    }

    private static float FacingVisibility(
        float facing,
        float hidden,
        float full)
    {
        float t =
            Mathf.InverseLerp(
                hidden,
                Mathf.Max(
                    hidden +
                    0.01f,
                    full),
                facing);

        return
            Smooth01(
                Mathf.Clamp01(
                    t));
    }

    private void ApplyYawFallback(
        float yaw,
        ref float leftVisibility,
        ref float rightVisibility,
        ref float mouthVisibility,
        bool leftResolved,
        bool rightResolved)
    {
        float absYaw =
            Mathf.Abs(
                yaw);

        float far =
            VisibilityFromYaw(
                absYaw,
                farEyeFadeStartYaw,
                farEyeHiddenYaw);

        float near =
            VisibilityFromYaw(
                absYaw,
                nearEyeFadeStartYaw,
                nearEyeHiddenYaw);

        float mouth =
            VisibilityFromYaw(
                absYaw,
                mouthFadeStartYaw,
                mouthHiddenYaw);

        if (
            !leftResolved ||
            !rightResolved
        )
        {
            if (
                TryResolveFarEyeByDepth(
                    out bool leftIsFar)
            )
            {
                if (leftIsFar)
                {
                    leftVisibility =
                        Mathf.Min(
                            leftVisibility,
                            far);

                    rightVisibility =
                        Mathf.Min(
                            rightVisibility,
                            near);
                }
                else
                {
                    rightVisibility =
                        Mathf.Min(
                            rightVisibility,
                            far);

                    leftVisibility =
                        Mathf.Min(
                            leftVisibility,
                            near);
                }
            }
            else
            {
                leftVisibility =
                    Mathf.Min(
                        leftVisibility,
                        near);

                rightVisibility =
                    Mathf.Min(
                        rightVisibility,
                        near);
            }
        }

        mouthVisibility =
            Mathf.Min(
                mouthVisibility,
                mouth);
    }

    private bool TryResolveFarEyeByDepth(
        out bool leftIsFar)
    {
        leftIsFar =
            false;

        if (
            _camera == null ||
            _cropper == null
        )
        {
            return false;
        }

        SurfaceFittedRawImage left =
            _cropper.leftEyeImage
            as SurfaceFittedRawImage;

        SurfaceFittedRawImage right =
            _cropper.rightEyeImage
            as SurfaceFittedRawImage;

        if (
            left == null ||
            right == null
        )
        {
            return false;
        }

        if (
            !TryGetSurfaceCenterWorld(
                left,
                out Vector3 leftWorld) ||
            !TryGetSurfaceCenterWorld(
                right,
                out Vector3 rightWorld)
        )
        {
            return false;
        }

        float leftDepth =
            _camera.transform
                .InverseTransformPoint(
                    leftWorld)
                .z;

        float rightDepth =
            _camera.transform
                .InverseTransformPoint(
                    rightWorld)
                .z;

        if (
            Mathf.Abs(
                leftDepth -
                rightDepth) <
            minimumAbsoluteDepthDifference
        )
        {
            return false;
        }

        leftIsFar =
            leftDepth >
            rightDepth;

        return true;
    }

    private void ClampFinalMouthSize()
    {
        if (
            !clampFinalMouthDisplaySize ||
            _cropper == null ||
            _cropper.mouthImage == null ||
            _cropper.mouthImage.material == null
        )
        {
            return;
        }

        Material material =
            _cropper.mouthImage.material;

        if (
            !material.HasProperty(
                SampleScaleXYId)
        )
        {
            return;
        }

        Vector4 current =
            material.GetVector(
                SampleScaleXYId);

        float minimumSampleScaleX =
            1f /
            Mathf.Max(
                0.05f,
                maximumMouthVisibleWidth);

        float minimumSampleScaleY =
            1f /
            Mathf.Max(
                0.05f,
                maximumMouthVisibleHeight);

        float scaleX =
            Mathf.Max(
                current.x,
                minimumSampleScaleX);

        float scaleY =
            Mathf.Max(
                current.y,
                minimumSampleScaleY);

        material.SetVector(
            SampleScaleXYId,
            new Vector4(
                scaleX,
                scaleY,
                current.z,
                current.w));
    }

    // KIWI_FACE_PART_VISIBILITY_LATCH_FIX_V3_3
    // Side-view visibility belongs to CanvasRenderer alpha only.
    // Semantic/blink material visibility remains owned by FacePartShapeMask.
    private static void ApplyPartVisibility(
        RawImage image,
        float guardVisibility)
    {
        if (image == null)
        {
            return;
        }

        image.canvasRenderer.SetAlpha(
            Mathf.Clamp01(
                guardVisibility));
    }

    private float FilterVisibility(
        float current,
        float target,
        float dt)
    {
        if (target <= 0.001f)
        {
            return 0f;
        }

        float response =
            target < current
                ? visibilityHideResponse
                : visibilityShowResponse;

        return
            Mathf.Lerp(
                current,
                target,
                1f -
                Mathf.Exp(
                    -Mathf.Max(
                        0f,
                        response) *
                    dt));
    }

    private void ResetSwapTrackingWarmup()
    {
        _freshTrackingFramesAfterSwap =
            0;

        _lastSwapWarmupFrameId =
            0UL;

        _swapWarmupStartedRealtime =
            Time.realtimeSinceStartupAsDouble;
    }

    private bool HasSwapTrackingWarmupCompleted()
    {
        if (
            requiredFreshTrackingFramesAfterSwap <=
                0)
        {
            return true;
        }

        double elapsed =
            Time.realtimeSinceStartupAsDouble -
            _swapWarmupStartedRealtime;

        if (
            elapsed >=
            maximumTrackingWarmupSeconds)
        {
            return true;
        }

        FacePrecisionTrackingData data =
            default;

        bool hasTracking =
            _trackingHub != null &&
            _trackingHub.TryGetLatestFrame(
                out data,
                out _);

        if (
            !hasTracking &&
            _runner != null
        )
        {
            hasTracking =
                _runner.TryGetLatestPrecisionTrackingData(
                    out data);
        }

        if (
            !hasTracking ||
            !data.isValid ||
            data.frameId == 0UL)
        {
            return false;
        }

        if (
            data.frameId !=
            _lastSwapWarmupFrameId)
        {
            _lastSwapWarmupFrameId =
                data.frameId;

            _freshTrackingFramesAfterSwap++;
        }

        return
            _freshTrackingFramesAfterSwap >=
            requiredFreshTrackingFramesAfterSwap;
    }


    private void RefreshReferences(
        bool force)
    {
        if (force || _cropper == null)
        {
            _cropper =
                FindFirstObjectByType<
                    FacePartCropper>(
                    FindObjectsInactive.Include);
        }

        if (force || _trackingHub == null)
        {
            _trackingHub =
                FindFirstObjectByType<
                    KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }

        if (force || _runner == null)
        {
            _runner =
                FindFirstObjectByType<
                    FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        if (force || _faceMotion == null)
        {
            _faceMotion =
                FindFirstObjectByType<
                    KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }

        if (force || _runtimeManager == null)
        {
            _runtimeManager =
                FindFirstObjectByType<
                    KiwiAvatarRuntimeManager>(
                    FindObjectsInactive.Include);
        }

        if (force || _camera == null)
        {
            _camera =
                Camera.main != null
                    ? Camera.main
                    : FindFirstObjectByType<
                        Camera>(
                        FindObjectsInactive.Exclude);
        }

        if (
            force ||
            _shapeMasks == null ||
            _shapeMasks.Length == 0
        )
        {
            _shapeMasks =
                FindObjectsByType<
                    FacePartShapeMask>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
        }

        if (
            force ||
            _surfaceParts == null ||
            _surfaceParts.Length == 0
        )
        {
            _surfaceParts =
                FindObjectsByType<
                    SurfaceFittedRawImage>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
        }

        if (
            force ||
            _surfaceFitters == null ||
            _surfaceFitters.Length == 0
        )
        {
            _surfaceFitters =
                FindObjectsByType<
                    KiwiSurfaceFitter>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
        }
    }

    private void ApplySafetyPresetIfNeeded(
        bool force)
    {
        if (!applySafeCropPreset)
        {
            return;
        }

        int signature =
            CalculatePresetSignature();

        if (
            !force &&
            signature ==
                _lastPresetSignature
        )
        {
            return;
        }

        if (_cropper != null)
        {
            _cropper.eyeWidthScale =
                eyeWidthScale;

            _cropper.eyeHeightToWidth =
                eyeHeightToWidth;

            _cropper.eyePaddingX =
                eyePaddingX;

            _cropper.eyePaddingY =
                eyePaddingY;

            _cropper.preserveEyeCenterAtTextureEdges =
                true;

            _cropper.mouthWidthScale =
                mouthWidthScale;

            _cropper.mouthHeightToWidth =
                mouthHeightToWidth;

            _cropper.mouthPaddingX =
                mouthPaddingX;

            _cropper.mouthPaddingY =
                mouthPaddingY;

            _cropper.useMouthContourSafeCrop =
                true;

            _cropper.mouthContourSafetyX =
                mouthContourSafetyX;

            _cropper.mouthContourSafetyY =
                mouthContourSafetyY;

            _cropper.preserveMouthCenterAtTextureEdges =
                true;
        }

        if (_shapeMasks != null)
        {
            for (
                int i = 0;
                i < _shapeMasks.Length;
                i++
            )
            {
                FacePartShapeMask mask =
                    _shapeMasks[i];

                if (mask == null)
                {
                    continue;
                }

                bool isMouth =
                    mask.facePart ==
                        FacePartShapeMask.FacePartType.Mouth ||
                    (
                        mask.facePart ==
                            FacePartShapeMask.FacePartType.Auto &&
                        mask.gameObject.name
                            .ToLowerInvariant()
                            .Contains("mouth")
                    );

                if (isMouth)
                {
                    mask.mouthContourMargin =
                        mouthContourMargin;

                    mask.mouthHideEdgeMargin =
                        0.006f;

                    mask.mouthShowEdgeMargin =
                        0.022f;

                    mask.mouthEdgeHideConfirmationSamples =
                        2;

                    mask.mouthEdgeHideGraceSeconds =
                        0.080f;
                }
                else
                {
                    mask.eyeContourMargin =
                        eyeContourMargin;
                }

                mask.feather =
                    maskFeather;

                mask.cropLocalSafetyMargin =
                    cropLocalSafetyMargin;

                mask.fullVisibilityYaw =
                    64f;

                mask.hiddenVisibilityYaw =
                    80f;

                mask.stabilizeSurfaceOcclusion =
                    true;
            }
        }

        if (_surfaceParts != null)
        {
            for (
                int i = 0;
                i < _surfaceParts.Length;
                i++
            )
            {
                if (_surfaceParts[i] != null)
                {
                    _surfaceParts[i]
                        .surfaceOffset =
                        fittedSurfaceOffset;
                }
            }
        }

        _lastPresetSignature =
            signature;
    }

    private int CalculatePresetSignature()
    {
        unchecked
        {
            int hash =
                17;

            if (_cropper != null)
            {
                hash =
                    hash * 31 +
                    _cropper.GetInstanceID();
            }

            if (_shapeMasks != null)
            {
                for (
                    int i = 0;
                    i < _shapeMasks.Length;
                    i++
                )
                {
                    if (_shapeMasks[i] != null)
                    {
                        hash =
                            hash * 31 +
                            _shapeMasks[i]
                                .GetInstanceID();
                    }
                }
            }

            if (_surfaceParts != null)
            {
                for (
                    int i = 0;
                    i < _surfaceParts.Length;
                    i++
                )
                {
                    if (_surfaceParts[i] != null)
                    {
                        hash =
                            hash * 31 +
                            _surfaceParts[i]
                                .GetInstanceID();
                    }
                }
            }

            return hash;
        }
    }

    private static float VisibilityFromYaw(
        float absYaw,
        float fullYaw,
        float hiddenYaw)
    {
        float t =
            Mathf.InverseLerp(
                fullYaw,
                Mathf.Max(
                    fullYaw +
                    0.1f,
                    hiddenYaw),
                absYaw);

        return
            1f -
            Smooth01(
                t);
    }

    private static float Smooth01(
        float value)
    {
        value =
            Mathf.Clamp01(
                value);

        return
            value *
            value *
            (3f -
             2f *
             value);
    }

    private void UpdateAvatarSwapState()
    {
        if (_runtimeManager == null)
        {
            return;
        }

        bool busy =
            _runtimeManager.IsBusy;

        string avatarName =
            _runtimeManager.CurrentAvatarName ??
            string.Empty;

        bool nameChanged =
            !string.IsNullOrEmpty(
                _lastAvatarName) &&
            !string.Equals(
                avatarName,
                _lastAvatarName,
                StringComparison.Ordinal);

        if (busy)
        {
            _swapGate =
                0f;

            _fadeInAfterFit =
                false;

            _swapSettling =
                true;

            _settleFramesRemaining =
                Mathf.Max(
                    1,
                    settleFramesBeforeFit);

            _fitRetryCount =
                0;

            ResetSwapTrackingWarmup();
        }
        else if (
            (_lastBusy && !busy) ||
            nameChanged
        )
        {
            _swapGate =
                0f;

            _fadeInAfterFit =
                false;

            _swapSettling =
                true;

            _settleFramesRemaining =
                Mathf.Max(
                    1,
                    settleFramesBeforeFit);

            _fitRetryCount =
                0;

            ResetSwapTrackingWarmup();

            _surfaceNormalSigns.Clear();

            RefreshReferences(true);
            ApplySafetyPresetIfNeeded(true);
        }

        _lastBusy =
            busy;

        _lastAvatarName =
            avatarName;
    }

    private void UpdateSwapRefit()
    {
        if (
            _runtimeManager != null &&
            _runtimeManager.IsBusy
        )
        {
            return;
        }

        if (_swapSettling)
        {
            _swapGate =
                0f;

            if (
                _settleFramesRemaining >
                    0
            )
            {
                _settleFramesRemaining--;
                return;
            }

            if (!HasSwapTrackingWarmupCompleted())
            {
                return;
            }

            bool fitSucceeded =
                !refitAfterAvatarSwitch ||
                RefitAllSurfaces();

            if (
                !fitSucceeded &&
                _fitRetryCount <
                    maximumFitRetries
            )
            {
                _fitRetryCount++;

                debugFitRetries =
                    _fitRetryCount;

                _settleFramesRemaining =
                    2;

                return;
            }

            RecalibrateFacePartPresentation();

            _surfaceNormalSigns.Clear();

            _swapSettling =
                false;

            _fadeInAfterFit =
                true;

            _swapGate =
                0f;
        }

        if (_fadeInAfterFit)
        {
            float seconds =
                Mathf.Max(
                    0.001f,
                    facePartFadeInSeconds);

            _swapGate =
                Mathf.MoveTowards(
                    _swapGate,
                    1f,
                    Time.unscaledDeltaTime /
                    seconds);

            if (
                _swapGate >=
                    0.9999f
            )
            {
                _swapGate =
                    1f;

                _fadeInAfterFit =
                    false;
            }
        }
    }

    private bool RefitAllSurfaces()
    {
        RefreshReferences(true);

        if (
            _surfaceFitters == null ||
            _surfaceFitters.Length == 0
        )
        {
            return true;
        }

        bool any =
            false;

        bool allSucceeded =
            true;

        float worstRate =
            1f;

        for (
            int i = 0;
            i < _surfaceFitters.Length;
            i++
        )
        {
            KiwiSurfaceFitter fitter =
                _surfaceFitters[i];

            if (
                fitter == null ||
                !fitter.isActiveAndEnabled
            )
            {
                continue;
            }

            any =
                true;

            Canvas.ForceUpdateCanvases();

            fitter.FitAllNow();

            worstRate =
                Mathf.Min(
                    worstRate,
                    fitter.LastSuccessRate);

            if (!fitter.LastFitSucceeded)
            {
                allSucceeded =
                    false;
            }
        }

        debugLastFitSuccessRate =
            any
                ? worstRate
                : 1f;

        RefreshReferences(true);
        ApplySafetyPresetIfNeeded(true);

        return
            !any ||
            allSucceeded;
    }

    private static void
        RecalibrateFacePartPresentation()
    {
        KiwiFacePartSharedTiltLock shared =
            FindFirstObjectByType<
                KiwiFacePartSharedTiltLock>(
                FindObjectsInactive.Include);

        if (shared != null)
        {
            shared.Recalibrate();
        }

        KiwiFacePartRigidCenterLock rigid =
            FindFirstObjectByType<
                KiwiFacePartRigidCenterLock>(
                FindObjectsInactive.Include);

        if (rigid != null)
        {
            rigid.Recalibrate();
        }

        FacePartAngleLock[] angleLocks =
            FindObjectsByType<
                FacePartAngleLock>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        for (
            int i = 0;
            i < angleLocks.Length;
            i++
        )
        {
            if (angleLocks[i] != null)
            {
                angleLocks[i]
                    .ResetAngleLock();
            }
        }
    }

    private static bool IsFinite(
        Vector3 value)
    {
        return
            !float.IsNaN(value.x) &&
            !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) &&
            !float.IsInfinity(value.z);
    }
}
