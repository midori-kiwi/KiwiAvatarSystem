using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// v4.3 final anatomical safety layer with non-invasive render ownership.
///
/// Commercial face systems do not allow each feature to behave as a completely
/// free sprite. They preserve a canonical face topology and reduce feature-local
/// influence when that topology becomes implausible.
///
/// This guard runs AFTER the model-primary surface constraint and uses the
/// actually rendered eye/mouth contour rectangles plus fitted-surface centers.
///
/// Priority:
/// 1) Preserve both eyes.
/// 2) Relax secondary local surface offsets.
/// 3) If necessary, reduce only the mouth's visible scale.
/// 4) Never move the avatar/root.
/// </summary>
[DefaultExecutionOrder(1290)]
[DisallowMultipleComponent]
public sealed class KiwiFacePartAnatomyGuard : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Face-Part Anatomy Guard";

    [Header("Master")]
    public bool enableAnatomyGuard = true;

    [Header("Rendered contour collision")]
    [Range(0f, 0.30f)]
    public float overlapStartRatio = 0.01f;

    [Range(0.02f, 0.80f)]
    public float overlapFullRatio = 0.20f;

    [Range(0.10f, 0.80f)]
    public float minimumMouthEyeLineSeparationEyeSpan = 0.34f;

    [Header("Single-eye surface excursion")]
    [Range(0f, 0.40f)]
    public float eyeSurfaceAsymmetryStartEyeSpan = 0.055f;

    [Range(0.02f, 0.60f)]
    public float eyeSurfaceAsymmetryFullEyeSpan = 0.16f;

    [Range(0f, 60f)]
    public float tiltAsymmetryBoostStartDegrees = 12f;

    [Range(5f, 85f)]
    public float tiltAsymmetryBoostFullDegrees = 34f;

    [Header("Recovery hierarchy")]
    [Range(0f, 1f)]
    public float severeCollisionMouthSurfaceKeep = 0.10f;

    [Range(0f, 1f)]
    public float severeCollisionEyeSurfaceKeep = 0.58f;

    [Range(0.35f, 1f)]
    public float minimumCollisionMouthScale = 0.68f;

    [Range(1f, 120f)]
    public float collisionAttackResponse = 34f;

    [Range(1f, 60f)]
    public float collisionReleaseResponse = 11f;

    [Range(0f, 0.40f)]
    public float minimumCollisionHoldSeconds = 0.10f;

    [Header("References")]
    public FacePartCropper cropper;
    public KiwiFaceMotion faceMotion;
    public KiwiTrackingContinuityState continuity;
    public KiwiAvatarRuntimeManager runtimeManager;
    public KiwiFacePartQualityCoordinator qualityCoordinator;

    [Header("Diagnostics")]
    [SerializeField] private float debugCollisionSeverity;
    [SerializeField] private float debugOverlapRatio;
    [SerializeField] private float debugEyeMouthSeparationRatio;
    [SerializeField] private float debugSurfaceAsymmetrySeverity;
    [SerializeField] private string debugSurfaceOutlierEye = "-";
    [SerializeField] private float debugRollDegrees;
    [SerializeField] private float debugMouthScaleFactor = 1f;
    [SerializeField] private bool debugRectsValid;

    private SurfaceFittedRawImage _leftEye;
    private SurfaceFittedRawImage _rightEye;
    private SurfaceFittedRawImage _mouth;

    private FacePartShapeMask _leftMask;
    private FacePartShapeMask _rightMask;
    private FacePartShapeMask _mouthMask;

    private Camera _camera;

    private float _collisionSeverity;
    private double _lastCollisionRealtime = -1000.0;
    private bool _mouthScaleOverrideActive;

    public float CollisionSeverity =>
        _collisionSeverity;

    public float OverlapRatio =>
        debugOverlapRatio;

    public float SurfaceAsymmetrySeverity =>
        debugSurfaceAsymmetrySeverity;

    public string SurfaceOutlierEye =>
        debugSurfaceOutlierEye;

    public float RollDegrees =>
        debugRollDegrees;

    public float MouthScaleFactor =>
        debugMouthScaleFactor;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<
                KiwiFacePartAnatomyGuard>(
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
            KiwiFacePartAnatomyGuard>();
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
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -=
            HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        cropper = null;
        faceMotion = null;
        continuity = null;
        runtimeManager = null;
        qualityCoordinator = null;

        _leftEye = null;
        _rightEye = null;
        _mouth = null;

        _leftMask = null;
        _rightMask = null;
        _mouthMask = null;

        _camera = null;

        _collisionSeverity =
            0f;

        _lastCollisionRealtime =
            -1000.0;

        _mouthScaleOverrideActive =
            false;

        RefreshReferences(true);
    }

    private void LateUpdate()
    {
        RefreshReferences(false);

        float dt =
            Mathf.Clamp(
                Time.unscaledDeltaTime,
                1f / 500f,
                0.05f);

        if (!CanEvaluate())
        {
            UpdateCollisionState(
                0f,
                dt);

            ApplyRecoveryHierarchy();

            UpdateDiagnostics();
            return;
        }

        float targetSeverity =
            EvaluateRenderedCollision();

        float asymmetrySeverity =
            EvaluateSingleEyeSurfaceExcursion();

        targetSeverity =
            Mathf.Max(
                targetSeverity,
                asymmetrySeverity);

        UpdateCollisionState(
            targetSeverity,
            dt);

        ApplyRecoveryHierarchy();

        UpdateDiagnostics();
    }

    private bool CanEvaluate()
    {
        if (
            !enableAnatomyGuard ||
            cropper == null ||
            _leftEye == null ||
            _rightEye == null ||
            _mouth == null ||
            _leftMask == null ||
            _rightMask == null ||
            _mouthMask == null ||
            _camera == null
        )
        {
            return false;
        }

        if (
            runtimeManager != null &&
            runtimeManager.IsBusy
        )
        {
            return false;
        }

        if (
            continuity != null &&
            (
                continuity.State ==
                    KiwiTrackingContinuityState.ContinuityState.Holding ||
                continuity.State ==
                    KiwiTrackingContinuityState.ContinuityState.Lost
            )
        )
        {
            return false;
        }

        return true;
    }

    private float EvaluateRenderedCollision()
    {
        debugRectsValid =
            false;

        debugOverlapRatio =
            0f;

        debugEyeMouthSeparationRatio =
            1f;

        if (
            !_leftMask.TryGetRenderedContourScreenRect(
                _camera,
                out Rect leftRect) ||
            !_rightMask.TryGetRenderedContourScreenRect(
                _camera,
                out Rect rightRect) ||
            !_mouthMask.TryGetRenderedContourScreenRect(
                _camera,
                out Rect mouthRect)
        )
        {
            return 0f;
        }

        if (
            leftRect.width <= 0.01f ||
            leftRect.height <= 0.01f ||
            rightRect.width <= 0.01f ||
            rightRect.height <= 0.01f ||
            mouthRect.width <= 0.01f ||
            mouthRect.height <= 0.01f
        )
        {
            return 0f;
        }

        debugRectsValid =
            true;

        float leftOverlap =
            CalculateOverlapRatio(
                leftRect,
                mouthRect);

        float rightOverlap =
            CalculateOverlapRatio(
                rightRect,
                mouthRect);

        float overlap =
            Mathf.Max(
                leftOverlap,
                rightOverlap);

        debugOverlapRatio =
            overlap;

        float overlapSeverity =
            Mathf.InverseLerp(
                overlapStartRatio,
                Mathf.Max(
                    overlapStartRatio +
                        0.001f,
                    overlapFullRatio),
                overlap);

        Vector2 leftCenter =
            leftRect.center;

        Vector2 rightCenter =
            rightRect.center;

        Vector2 mouthCenter =
            mouthRect.center;

        Vector2 eyeVector =
            rightCenter -
            leftCenter;

        float eyeSpan =
            eyeVector.magnitude;

        if (
            eyeSpan <=
            0.001f
        )
        {
            return overlapSeverity;
        }

        Vector2 xAxis =
            eyeVector /
            eyeSpan;

        Vector2 yAxis =
            new Vector2(
                -xAxis.y,
                xAxis.x);

        Vector2 eyeMid =
            (
                leftCenter +
                rightCenter
            ) *
            0.5f;

        if (
            Vector2.Dot(
                mouthCenter -
                    eyeMid,
                yAxis) <
            0f
        )
        {
            yAxis =
                -yAxis;
        }

        float separation =
            Mathf.Max(
                0f,
                Vector2.Dot(
                    mouthCenter -
                        eyeMid,
                    yAxis));

        float minimumSeparation =
            eyeSpan *
            minimumMouthEyeLineSeparationEyeSpan;

        debugEyeMouthSeparationRatio =
            separation /
            Mathf.Max(
                0.001f,
                minimumSeparation);

        float separationSeverity =
            1f -
            Mathf.InverseLerp(
                minimumSeparation *
                    0.55f,
                minimumSeparation,
                separation);

        return
            Mathf.Clamp01(
                Mathf.Max(
                    overlapSeverity,
                    separationSeverity));
    }

    private float EvaluateSingleEyeSurfaceExcursion()
    {
        debugSurfaceAsymmetrySeverity =
            0f;

        debugSurfaceOutlierEye =
            "-";

        if (
            !_leftEye.HasSurfaceFit ||
            !_rightEye.HasSurfaceFit
        )
        {
            return 0f;
        }

        if (
            !TryGetCurrentAndNeutralScreenCenter(
                _leftEye,
                out Vector2 leftCurrent,
                out Vector2 leftNeutral) ||
            !TryGetCurrentAndNeutralScreenCenter(
                _rightEye,
                out Vector2 rightCurrent,
                out Vector2 rightNeutral)
        )
        {
            return 0f;
        }

        float neutralEyeSpan =
            Vector2.Distance(
                leftNeutral,
                rightNeutral);

        if (
            neutralEyeSpan <=
            0.001f
        )
        {
            return 0f;
        }

        float leftMotion =
            Vector2.Distance(
                leftCurrent,
                leftNeutral) /
            neutralEyeSpan;

        float rightMotion =
            Vector2.Distance(
                rightCurrent,
                rightNeutral) /
            neutralEyeSpan;

        float asymmetry =
            Mathf.Abs(
                leftMotion -
                rightMotion);

        Vector2 neutralEyeVector =
            rightNeutral -
            leftNeutral;

        float roll =
            Mathf.Abs(
                Mathf.Atan2(
                    neutralEyeVector.y,
                    neutralEyeVector.x) *
                Mathf.Rad2Deg);

        while (roll > 180f)
        {
            roll -=
                360f;
        }

        roll =
            Mathf.Abs(
                roll);

        if (roll > 90f)
        {
            roll =
                180f -
                roll;
        }

        debugRollDegrees =
            roll;

        float tiltFactor =
            Mathf.InverseLerp(
                tiltAsymmetryBoostStartDegrees,
                Mathf.Max(
                    tiltAsymmetryBoostStartDegrees +
                        0.01f,
                    tiltAsymmetryBoostFullDegrees),
                roll);

        float tightenedStart =
            Mathf.Lerp(
                eyeSurfaceAsymmetryStartEyeSpan,
                eyeSurfaceAsymmetryStartEyeSpan *
                    0.70f,
                tiltFactor);

        float tightenedFull =
            Mathf.Lerp(
                eyeSurfaceAsymmetryFullEyeSpan,
                eyeSurfaceAsymmetryFullEyeSpan *
                    0.72f,
                tiltFactor);

        float severity =
            Mathf.InverseLerp(
                tightenedStart,
                Mathf.Max(
                    tightenedStart +
                        0.001f,
                    tightenedFull),
                asymmetry);

        if (
            severity >
            0.001f
        )
        {
            debugSurfaceOutlierEye =
                leftMotion >
                    rightMotion
                    ? "Left"
                    : "Right";
        }

        debugSurfaceAsymmetrySeverity =
            severity;

        return
            severity;
    }

    private bool TryGetCurrentAndNeutralScreenCenter(
        SurfaceFittedRawImage image,
        out Vector2 currentScreen,
        out Vector2 neutralScreen)
    {
        currentScreen =
            Vector2.zero;

        neutralScreen =
            Vector2.zero;

        if (image == null)
        {
            return false;
        }

        Vector2 offset =
            image.SurfaceConstraintOffsetNormalized;

        if (
            !image.TryGetSurfaceLocalPosition(
                new Vector2(
                    0.5f,
                    0.5f),
                out Vector3 currentLocal) ||
            !image.TryGetSurfaceLocalPosition(
                new Vector2(
                    0.5f -
                        offset.x,
                    0.5f -
                        offset.y),
                out Vector3 neutralLocal)
        )
        {
            return false;
        }

        Vector3 currentWorld =
            image.rectTransform
                .TransformPoint(
                    currentLocal);

        Vector3 neutralWorld =
            image.rectTransform
                .TransformPoint(
                    neutralLocal);

        currentScreen =
            _camera.WorldToScreenPoint(
                currentWorld);

        neutralScreen =
            _camera.WorldToScreenPoint(
                neutralWorld);

        return
            IsFinite(
                currentScreen) &&
            IsFinite(
                neutralScreen);
    }

    private void UpdateCollisionState(
        float target,
        float dt)
    {
        target =
            Mathf.Clamp01(
                target);

        double now =
            Time.realtimeSinceStartupAsDouble;

        if (
            target >
            0.001f
        )
        {
            _lastCollisionRealtime =
                now;
        }
        else if (
            now -
                _lastCollisionRealtime <
            minimumCollisionHoldSeconds
        )
        {
            target =
                _collisionSeverity;
        }

        float response =
            target >
                _collisionSeverity
                ? collisionAttackResponse
                : collisionReleaseResponse;

        _collisionSeverity =
            Mathf.Lerp(
                _collisionSeverity,
                target,
                1f -
                Mathf.Exp(
                    -response *
                    dt));
    }

    private void ApplyRecoveryHierarchy()
    {
        if (
            _leftEye == null ||
            _rightEye == null ||
            _mouth == null
        )
        {
            return;
        }

        float severity =
            Mathf.Clamp01(
                _collisionSeverity);

        float mouthKeep =
            Mathf.Lerp(
                1f,
                severeCollisionMouthSurfaceKeep,
                severity);

        float eyeKeep =
            Mathf.Lerp(
                1f,
                severeCollisionEyeSurfaceKeep,
                severity);

        Vector2 leftOffset =
            _leftEye
                .SurfaceConstraintOffsetNormalized;

        Vector2 rightOffset =
            _rightEye
                .SurfaceConstraintOffsetNormalized;

        Vector2 mouthOffset =
            _mouth
                .SurfaceConstraintOffsetNormalized;

        if (
            debugSurfaceOutlierEye ==
            "Left"
        )
        {
            leftOffset *=
                Mathf.Lerp(
                    eyeKeep,
                    0.28f,
                    debugSurfaceAsymmetrySeverity);

            rightOffset *=
                eyeKeep;
        }
        else if (
            debugSurfaceOutlierEye ==
            "Right"
        )
        {
            rightOffset *=
                Mathf.Lerp(
                    eyeKeep,
                    0.28f,
                    debugSurfaceAsymmetrySeverity);

            leftOffset *=
                eyeKeep;
        }
        else
        {
            leftOffset *=
                eyeKeep;

            rightOffset *=
                eyeKeep;
        }

        mouthOffset *=
            mouthKeep;

        _leftEye
            .SetSurfaceConstraintOffsetNormalized(
                leftOffset);

        _rightEye
            .SetSurfaceConstraintOffsetNormalized(
                rightOffset);

        _mouth
            .SetSurfaceConstraintOffsetNormalized(
                mouthOffset);

        float mouthScaleFactor =
            Mathf.Lerp(
                1f,
                minimumCollisionMouthScale,
                severity);

        debugMouthScaleFactor =
            mouthScaleFactor;

        if (_mouthMask != null)
        {
            float baseWidth =
                qualityCoordinator != null
                    ? qualityCoordinator
                        .maximumMouthVisibleWidth
                    : 1f;

            float baseHeight =
                qualityCoordinator != null
                    ? qualityCoordinator
                        .maximumMouthVisibleHeight
                    : 1f;

            bool needsOverride =
                severity >
                0.001f;

            if (needsOverride)
            {
                _mouthMask.SetVisibleScale(
                    baseWidth *
                        mouthScaleFactor,
                    baseHeight *
                        mouthScaleFactor);

                _mouthScaleOverrideActive =
                    true;
            }
            else if (_mouthScaleOverrideActive)
            {
                // Return ownership once, then stop writing this channel so the
                // normal expression/quality system remains the sole steady-state
                // owner of mouth scale.
                _mouthMask.SetVisibleScale(
                    baseWidth,
                    baseHeight);

                _mouthScaleOverrideActive =
                    false;
            }
        }
    }

    private static float CalculateOverlapRatio(
        Rect a,
        Rect b)
    {
        float xMin =
            Mathf.Max(
                a.xMin,
                b.xMin);

        float xMax =
            Mathf.Min(
                a.xMax,
                b.xMax);

        float yMin =
            Mathf.Max(
                a.yMin,
                b.yMin);

        float yMax =
            Mathf.Min(
                a.yMax,
                b.yMax);

        float width =
            Mathf.Max(
                0f,
                xMax -
                    xMin);

        float height =
            Mathf.Max(
                0f,
                yMax -
                    yMin);

        float intersection =
            width *
            height;

        float minimumArea =
            Mathf.Max(
                0.0001f,
                Mathf.Min(
                    a.width *
                        a.height,
                    b.width *
                        b.height));

        return
            Mathf.Clamp01(
                intersection /
                minimumArea);
    }

    private void RefreshReferences(
        bool force)
    {
        if (
            force ||
            cropper == null
        )
        {
            cropper =
                FindFirstObjectByType<
                    FacePartCropper>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            faceMotion == null
        )
        {
            faceMotion =
                FindFirstObjectByType<
                    KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            continuity == null
        )
        {
            continuity =
                FindFirstObjectByType<
                    KiwiTrackingContinuityState>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            runtimeManager == null
        )
        {
            runtimeManager =
                FindFirstObjectByType<
                    KiwiAvatarRuntimeManager>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            qualityCoordinator == null
        )
        {
            qualityCoordinator =
                FindFirstObjectByType<
                    KiwiFacePartQualityCoordinator>(
                    FindObjectsInactive.Include);
        }

        if (cropper != null)
        {
            _leftEye =
                cropper.leftEyeImage
                as SurfaceFittedRawImage;

            _rightEye =
                cropper.rightEyeImage
                as SurfaceFittedRawImage;

            _mouth =
                cropper.mouthImage
                as SurfaceFittedRawImage;

            _leftMask =
                _leftEye != null
                    ? _leftEye.GetComponent<
                        FacePartShapeMask>()
                    : null;

            _rightMask =
                _rightEye != null
                    ? _rightEye.GetComponent<
                        FacePartShapeMask>()
                    : null;

            _mouthMask =
                _mouth != null
                    ? _mouth.GetComponent<
                        FacePartShapeMask>()
                    : null;
        }

        if (
            force ||
            _camera == null
        )
        {
            _camera =
                faceMotion != null
                    ? faceMotion
                        .positionReferenceCamera
                    : null;

            if (_camera == null)
            {
                _camera =
                    Camera.main;
            }

            if (_camera == null)
            {
                _camera =
                    FindFirstObjectByType<
                        Camera>(
                        FindObjectsInactive.Include);
            }
        }
    }

    private void UpdateDiagnostics()
    {
        debugCollisionSeverity =
            _collisionSeverity;
    }

    private static bool IsFinite(
        Vector2 value)
    {
        return
            !float.IsNaN(
                value.x) &&
            !float.IsNaN(
                value.y) &&
            !float.IsInfinity(
                value.x) &&
            !float.IsInfinity(
                value.y);
    }
}
