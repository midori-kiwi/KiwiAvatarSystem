using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// KiwiAvatarSystem v4.0
/// 3D Model Primary + 2D Face-Part Local Surface Constraint.
///
/// Authority is strictly one-way:
/// - KiwiFaceMotion / 3D tracking owns avatar root pose.
/// - FacePartCropper owns camera pixels.
/// - This component may only apply a SMALL LOCAL offset on the already-fitted
///   eye/mouth surface patch.
/// - Eye/mouth residuals NEVER feed back into head translation/rotation/scale.
///
/// The neutral residual is calculated in a 2D face-local coordinate frame, so
/// global camera translation, in-plane roll and most scale change are removed
/// before the local constraint is evaluated.
/// </summary>
[DefaultExecutionOrder(930)]
[DisallowMultipleComponent]
public sealed class KiwiModelPrimaryFacePartConstraint : MonoBehaviour
{
    [System.Serializable]
    private sealed class ConstraintProfile
    {
        public int version = 1;

        public float leftEyeX;
        public float leftEyeY;

        public float rightEyeX;
        public float rightEyeY;

        public float mouthX;
        public float mouthY;

        public float calibrationQuality;
    }

    private const string RuntimeObjectName =
        "[Kiwi] Model-Primary Face-Part Constraint";

    private const string KeyPrefix =
        "Kiwi.ModelPrimaryFacePartConstraint.v1.";

    private const string SurfaceSetterName =
        "SetSurfaceConstraintOffsetNormalized";

    private const int LeftEyeOuter = 362;
    private const int LeftEyeInner = 263;
    private const int LeftEyeUpper = 386;
    private const int LeftEyeLower = 374;

    private const int RightEyeOuter = 33;
    private const int RightEyeInner = 133;
    private const int RightEyeUpper = 159;
    private const int RightEyeLower = 145;

    private const int MouthLeft = 61;
    private const int MouthRight = 291;
    private const int MouthUpper = 13;
    private const int MouthLower = 14;

    private const int FaceTop = 10;
    private const int FaceBottom = 152;
    private const int FaceLeft = 234;
    private const int FaceRight = 454;

    [Header("Master")]
    public bool enableConstraint = true;

    public bool requireSurfaceFit = true;

    [Header("Actor-neutral calibration")]
    public string profileName = "Default";

    public bool followActorCalibrationProfileName = true;

    public bool loadSavedProfile = true;

    public bool saveProfile = true;

    public bool autoCalibrateIfMissing = true;

    [Range(8, 80)]
    public int requiredFreshCalibrationSamples = 24;

    [Range(0.15f, 2.0f)]
    public float minimumCalibrationSeconds = 0.40f;

    [Range(0.30f, 1f)]
    public float minimumCalibrationPartQuality = 0.80f;

    [Range(2f, 25f)]
    public float maximumCalibrationYawDegrees = 9f;

    [Range(0.05f, 0.35f)]
    public float maximumNeutralMouthOpenRatio = 0.18f;

    [Range(0.08f, 0.45f)]
    public float minimumNeutralEyeOpenRatio = 0.12f;

    [Tooltip("Maximum source-frame age accepted while learning the neutral surface profile. Stable and Degraded continuity may both contribute when this freshness gate and semantic quality gates pass.")]
    [Range(0.10f, 0.40f)]
    public float maximumCalibrationSourceAgeSeconds = 0.22f;

    [Header("Constraint quality gate")]
    [Range(0f, 1f)]
    public float minimumPartQuality = 0.48f;

    [Range(0f, 1f)]
    public float fullStrengthPartQuality = 0.82f;

    [Range(0f, 1f)]
    public float minimumDualDomainQuality = 0.45f;

    [Range(0.10f, 0.80f)]
    public float maximum2dAgeSeconds = 0.34f;

    [Header("Yaw / perspective gate")]
    [Range(0f, 75f)]
    public float fullStrengthYawDegrees = 34f;

    [Range(20f, 89f)]
    public float zeroStrengthYawDegrees = 70f;

    [Header("Eye local residual")]
    [Range(0f, 1.5f)]
    public float eyeHorizontalGain = 0.62f;

    [Range(0f, 1.5f)]
    public float eyeVerticalGain = 0.38f;

    public bool suppressEyeVerticalConstraintDuringBlink = true;

    [Range(0f, 1f)]
    public float closedEyeVerticalGain = 0.12f;

    [Range(0.02f, 0.35f)]
    public float maximumEyeSurfaceOffset = 0.16f;

    [Header("Mouth local residual")]
    [Range(0f, 1.5f)]
    public float mouthHorizontalGain = 0.50f;

    [Range(0f, 1.5f)]
    public float mouthVerticalGain = 0.42f;

    [Range(0.02f, 0.35f)]
    public float maximumMouthSurfaceOffset = 0.15f;

    [Header("Residual presentation")]
    [Range(0f, 0.10f)]
    public float cropRelativeDeadZone = 0.012f;

    [Range(5f, 180f)]
    public float minimumResponse = 28f;

    [Range(10f, 240f)]
    public float maximumResponse = 76f;

    [Range(5f, 180f)]
    public float returnToNeutralResponse = 42f;

    [Header("Diagnostics")]
    [SerializeField] private bool debugCalibrated;
    [SerializeField] private bool debugSurfaceApiAvailable;
    [SerializeField] private float debugCalibrationQuality;
    [SerializeField] private float debugConstraintStrength;
    [SerializeField] private Vector2 debugLeftEyeOffset;
    [SerializeField] private Vector2 debugRightEyeOffset;
    [SerializeField] private Vector2 debugMouthOffset;
    [SerializeField] private string debugState = "-";
    [SerializeField] private float debugObservationAgeMs;

    private Mediapipe.Unity.Sample.FaceLandmarkDetection.FaceLandmarkerRunner _runner;
    private KiwiDualDomainFaceQuality _dualDomain;
    private KiwiActorFaceCalibration _actorCalibration;
    private KiwiTrackingContinuityState _continuity;
    private KiwiFaceMotion _faceMotion;
    private KiwiAvatarRuntimeManager _runtimeManager;
    private FacePartCropper _cropper;

    private SurfaceFittedRawImage _leftEye;
    private SurfaceFittedRawImage _rightEye;
    private SurfaceFittedRawImage _mouth;

    private MethodInfo _surfaceSetter;
    private bool _searchedSurfaceSetter;

    private readonly object[] _leftArgs =
        new object[1];

    private readonly object[] _rightArgs =
        new object[1];

    private readonly object[] _mouthArgs =
        new object[1];

    private Vector2[] _landmarks;
    private long _lastTimestamp = long.MinValue;
    private double _lastObservationRealtime = -1000.0;

    private bool _hasFaceBasis;
    private Vector2 _faceCenter;
    private Vector2 _faceXAxis = Vector2.right;
    private Vector2 _faceYAxis = Vector2.down;
    private float _faceWidth;
    private float _faceHeight;

    private Vector2 _leftEyeFaceLocal;
    private Vector2 _rightEyeFaceLocal;
    private Vector2 _mouthFaceLocal;

    private float _leftEyeOpenRatio;
    private float _rightEyeOpenRatio;
    private float _mouthOpenRatio;

    private ConstraintProfile _profile;

    private int _calibrationSamples;
    private double _calibrationStartedRealtime;
    private Vector2 _sumLeftEye;
    private Vector2 _sumRightEye;
    private Vector2 _sumMouth;
    private float _sumCalibrationQuality;

    private Vector2 _leftEyeOffset;
    private Vector2 _rightEyeOffset;
    private Vector2 _mouthOffset;

    public bool IsCalibrated =>
        _profile != null;

    public float CalibrationQuality =>
        _profile != null
            ? _profile.calibrationQuality
            : 0f;

    public int CalibrationSamples =>
        _calibrationSamples;

    public bool SurfaceApiAvailable =>
        _surfaceSetter != null;

    public float ConstraintStrength =>
        debugConstraintStrength;

    public Vector2 LeftEyeOffset =>
        _leftEyeOffset;

    public Vector2 RightEyeOffset =>
        _rightEyeOffset;

    public Vector2 MouthOffset =>
        _mouthOffset;

    public string ConstraintState =>
        debugState;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<
                KiwiModelPrimaryFacePartConstraint>(
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
            KiwiModelPrimaryFacePartConstraint>();
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

        if (loadSavedProfile)
        {
            TryLoadProfile();
        }
    }

    private void OnDisable()
    {
        ResetSurfaceOffsetsImmediate();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        ResetSurfaceOffsetsImmediate();
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _runner = null;
        _dualDomain = null;
        _actorCalibration = null;
        _continuity = null;
        _faceMotion = null;
        _runtimeManager = null;
        _cropper = null;

        _leftEye = null;
        _rightEye = null;
        _mouth = null;

        _surfaceSetter = null;
        _searchedSurfaceSetter = false;

        _landmarks = null;
        _lastTimestamp = long.MinValue;
        _lastObservationRealtime = -1000.0;
        _hasFaceBasis = false;

        ResetCalibrationCollection();
        ResetSurfaceOffsetsImmediate();
        RefreshReferences(true);
    }

    private void LateUpdate()
    {
        RefreshReferences(false);

        bool newObservation =
            ConsumeLatest2DObservation();

        if (
            _profile == null &&
            autoCalibrateIfMissing &&
            newObservation
        )
        {
            TryCollectNeutralCalibration();
        }

        float dt =
            Mathf.Clamp(
                Time.unscaledDeltaTime,
                1f / 500f,
                0.05f);

        if (
            !CanApplyConstraint(
                out float globalStrength)
        )
        {
            debugConstraintStrength =
                0f;

            ReturnOffsetsToNeutral(
                dt);

            UpdateDiagnostics();
            return;
        }

        debugConstraintStrength =
            globalStrength;

        debugState =
            "SurfaceConstraint";

        bool swapEyes =
            _cropper != null &&
            _cropper.swapEyes;

        Vector2 leftCurrent =
            swapEyes
                ? _rightEyeFaceLocal
                : _leftEyeFaceLocal;

        Vector2 rightCurrent =
            swapEyes
                ? _leftEyeFaceLocal
                : _rightEyeFaceLocal;

        Vector2 leftNeutral =
            swapEyes
                ? NeutralRightEye
                : NeutralLeftEye;

        Vector2 rightNeutral =
            swapEyes
                ? NeutralLeftEye
                : NeutralRightEye;

        float leftQuality =
            ResolveEyeQuality(
                swapEyes
                    ? false
                    : true);

        float rightQuality =
            ResolveEyeQuality(
                swapEyes
                    ? true
                    : false);

        float leftCurrentOpen =
            swapEyes
                ? _rightEyeOpenRatio
                : _leftEyeOpenRatio;

        float rightCurrentOpen =
            swapEyes
                ? _leftEyeOpenRatio
                : _rightEyeOpenRatio;

        float leftNeutralOpen =
            ResolveNeutralEyeOpen(
                swapEyes
                    ? false
                    : true);

        float rightNeutralOpen =
            ResolveNeutralEyeOpen(
                swapEyes
                    ? true
                    : false);

        Vector2 leftTarget =
            CalculateSurfaceTarget(
                _leftEye,
                leftCurrent -
                    leftNeutral,
                leftQuality,
                globalStrength,
                eyeHorizontalGain,
                eyeVerticalGain *
                    EyeVerticalFactor(
                        leftCurrentOpen,
                        leftNeutralOpen),
                maximumEyeSurfaceOffset);

        Vector2 rightTarget =
            CalculateSurfaceTarget(
                _rightEye,
                rightCurrent -
                    rightNeutral,
                rightQuality,
                globalStrength,
                eyeHorizontalGain,
                eyeVerticalGain *
                    EyeVerticalFactor(
                        rightCurrentOpen,
                        rightNeutralOpen),
                maximumEyeSurfaceOffset);

        float mouthQuality =
            _dualDomain != null
                ? _dualDomain.MouthQuality
                : 1f;

        Vector2 mouthTarget =
            CalculateSurfaceTarget(
                _mouth,
                _mouthFaceLocal -
                    NeutralMouth,
                mouthQuality,
                globalStrength,
                mouthHorizontalGain,
                mouthVerticalGain,
                maximumMouthSurfaceOffset);

        _leftEyeOffset =
            SmoothOffset(
                _leftEyeOffset,
                leftTarget,
                leftQuality,
                dt);

        _rightEyeOffset =
            SmoothOffset(
                _rightEyeOffset,
                rightTarget,
                rightQuality,
                dt);

        _mouthOffset =
            SmoothOffset(
                _mouthOffset,
                mouthTarget,
                mouthQuality,
                dt);

        ApplyOffset(
            _leftEye,
            _leftEyeOffset,
            _leftArgs);

        ApplyOffset(
            _rightEye,
            _rightEyeOffset,
            _rightArgs);

        ApplyOffset(
            _mouth,
            _mouthOffset,
            _mouthArgs);

        UpdateDiagnostics();
    }

    [ContextMenu(
        "Recalibrate Model-Primary Face-Part Constraint")]
    public void Recalibrate()
    {
        _profile =
            null;

        ResetCalibrationCollection();

        PlayerPrefs.DeleteKey(
            BuildProfileKey());

        PlayerPrefs.Save();
    }

    private bool ConsumeLatest2DObservation()
    {
        if (_runner == null)
        {
            return false;
        }

        bool changed =
            _runner.TryGetLatestLandmarksIfChanged(
                ref _landmarks,
                _lastTimestamp,
                out int count,
                out long timestamp,
                out bool hasFace);

        if (!changed)
        {
            return false;
        }

        _lastTimestamp =
            timestamp;

        if (
            !hasFace ||
            _landmarks == null ||
            count <= FaceRight
        )
        {
            _hasFaceBasis =
                false;

            return true;
        }

        Vector2 faceTop =
            _landmarks[FaceTop];

        Vector2 faceBottom =
            _landmarks[FaceBottom];

        Vector2 faceLeft =
            _landmarks[FaceLeft];

        Vector2 faceRight =
            _landmarks[FaceRight];

        Vector2 faceXVector =
            faceRight -
            faceLeft;

        Vector2 faceYVector =
            faceBottom -
            faceTop;

        _faceWidth =
            faceXVector.magnitude;

        if (
            _faceWidth <=
            0.005f
        )
        {
            _hasFaceBasis =
                false;

            return true;
        }

        _faceXAxis =
            faceXVector /
            _faceWidth;

        Vector2 orthogonalY =
            faceYVector -
            _faceXAxis *
            Vector2.Dot(
                faceYVector,
                _faceXAxis);

        _faceHeight =
            orthogonalY.magnitude;

        if (
            _faceHeight <=
            0.005f
        )
        {
            _hasFaceBasis =
                false;

            return true;
        }

        _faceYAxis =
            orthogonalY /
            _faceHeight;

        if (
            Vector2.Dot(
                _faceYAxis,
                faceYVector) <
            0f
        )
        {
            _faceYAxis =
                -_faceYAxis;
        }

        _faceCenter =
            (
                faceTop +
                faceBottom +
                faceLeft +
                faceRight
            ) *
            0.25f;

        Vector2 leftEyeCenter =
            (
                _landmarks[LeftEyeOuter] +
                _landmarks[LeftEyeInner]
            ) *
            0.5f;

        Vector2 rightEyeCenter =
            (
                _landmarks[RightEyeOuter] +
                _landmarks[RightEyeInner]
            ) *
            0.5f;

        Vector2 mouthCenter =
            (
                _landmarks[MouthLeft] +
                _landmarks[MouthRight]
            ) *
            0.5f;

        _leftEyeFaceLocal =
            ToFaceLocal(
                leftEyeCenter);

        _rightEyeFaceLocal =
            ToFaceLocal(
                rightEyeCenter);

        _mouthFaceLocal =
            ToFaceLocal(
                mouthCenter);

        float leftEyeWidth =
            Vector2.Distance(
                _landmarks[LeftEyeOuter],
                _landmarks[LeftEyeInner]);

        float rightEyeWidth =
            Vector2.Distance(
                _landmarks[RightEyeOuter],
                _landmarks[RightEyeInner]);

        float mouthWidth =
            Vector2.Distance(
                _landmarks[MouthLeft],
                _landmarks[MouthRight]);

        _leftEyeOpenRatio =
            Vector2.Distance(
                _landmarks[LeftEyeUpper],
                _landmarks[LeftEyeLower]) /
            Mathf.Max(
                0.0001f,
                leftEyeWidth);

        _rightEyeOpenRatio =
            Vector2.Distance(
                _landmarks[RightEyeUpper],
                _landmarks[RightEyeLower]) /
            Mathf.Max(
                0.0001f,
                rightEyeWidth);

        _mouthOpenRatio =
            Vector2.Distance(
                _landmarks[MouthUpper],
                _landmarks[MouthLower]) /
            Mathf.Max(
                0.0001f,
                mouthWidth);

        _hasFaceBasis =
            IsFinite(
                _leftEyeFaceLocal) &&
            IsFinite(
                _rightEyeFaceLocal) &&
            IsFinite(
                _mouthFaceLocal);

        if (_hasFaceBasis)
        {
            _lastObservationRealtime =
                Time.realtimeSinceStartupAsDouble;
        }

        return true;
    }

    private Vector2 ToFaceLocal(
        Vector2 point)
    {
        Vector2 delta =
            point -
            _faceCenter;

        return
            new Vector2(
                Vector2.Dot(
                    delta,
                    _faceXAxis) /
                    Mathf.Max(
                        0.0001f,
                        _faceWidth),
                Vector2.Dot(
                    delta,
                    _faceYAxis) /
                    Mathf.Max(
                        0.0001f,
                        _faceHeight));
    }

    private bool TryFaceLocalDeltaToCameraDelta(
        Vector2 localDelta,
        out Vector2 cameraDelta)
    {
        cameraDelta =
            Vector2.zero;

        if (!_hasFaceBasis)
        {
            return false;
        }

        cameraDelta =
            _faceXAxis *
                (
                    localDelta.x *
                    _faceWidth
                ) +
            _faceYAxis *
                (
                    localDelta.y *
                    _faceHeight
                );

        return
            IsFinite(
                cameraDelta);
    }

    private void TryCollectNeutralCalibration()
    {
        if (
            !_hasFaceBasis ||
            _continuity == null
        )
        {
            return;
        }

        // v4.8: neutral calibration is gated by actual observation freshness,
        // not by a requirement that cadence be classified Stable on every
        // sample. A single cadence wobble must not erase an otherwise clean
        // neutral solve. Holding/Lost/Reacquiring still invalidate the window.
        if (
            _continuity.State ==
                KiwiTrackingContinuityState.ContinuityState.Starting ||
            _continuity.State ==
                KiwiTrackingContinuityState.ContinuityState.Holding ||
            _continuity.State ==
                KiwiTrackingContinuityState.ContinuityState.Lost ||
            _continuity.State ==
                KiwiTrackingContinuityState.ContinuityState.Reacquiring
        )
        {
            ResetCalibrationCollection();
            return;
        }

        if (
            _continuity.SourceAgeSeconds >
                maximumCalibrationSourceAgeSeconds
        )
        {
            return;
        }

        if (
            _actorCalibration != null &&
            !_actorCalibration.IsCalibrated
        )
        {
            return;
        }

        if (
            _faceMotion != null &&
            Mathf.Abs(
                _faceMotion.RenderedYawDegrees) >
                maximumCalibrationYawDegrees
        )
        {
            return;
        }

        float leftQuality =
            ResolveEyeQuality(
                true);

        float rightQuality =
            ResolveEyeQuality(
                false);

        float mouthQuality =
            _dualDomain != null
                ? _dualDomain.MouthQuality
                : 1f;

        if (
            leftQuality <
                minimumCalibrationPartQuality ||
            rightQuality <
                minimumCalibrationPartQuality ||
            mouthQuality <
                minimumCalibrationPartQuality
        )
        {
            return;
        }

        if (
            _leftEyeOpenRatio <
                minimumNeutralEyeOpenRatio ||
            _rightEyeOpenRatio <
                minimumNeutralEyeOpenRatio ||
            _mouthOpenRatio >
                maximumNeutralMouthOpenRatio
        )
        {
            return;
        }

        if (_calibrationSamples == 0)
        {
            _calibrationStartedRealtime =
                Time.realtimeSinceStartupAsDouble;
        }

        _calibrationSamples++;

        _sumLeftEye +=
            _leftEyeFaceLocal;

        _sumRightEye +=
            _rightEyeFaceLocal;

        _sumMouth +=
            _mouthFaceLocal;

        float quality =
            _dualDomain != null
                ? _dualDomain.DualDomainQuality
                : Mathf.Min(
                    leftQuality,
                    Mathf.Min(
                        rightQuality,
                        mouthQuality));

        _sumCalibrationQuality +=
            quality;

        if (
            _calibrationSamples <
                Mathf.Max(
                    1,
                    requiredFreshCalibrationSamples)
        )
        {
            return;
        }

        if (
            Time.realtimeSinceStartupAsDouble -
                _calibrationStartedRealtime <
            minimumCalibrationSeconds
        )
        {
            return;
        }

        float inv =
            1f /
            Mathf.Max(
                1,
                _calibrationSamples);

        Vector2 neutralLeft =
            _sumLeftEye *
            inv;

        Vector2 neutralRight =
            _sumRightEye *
            inv;

        Vector2 neutralMouth =
            _sumMouth *
            inv;

        _profile =
            new ConstraintProfile
            {
                leftEyeX =
                    neutralLeft.x,
                leftEyeY =
                    neutralLeft.y,
                rightEyeX =
                    neutralRight.x,
                rightEyeY =
                    neutralRight.y,
                mouthX =
                    neutralMouth.x,
                mouthY =
                    neutralMouth.y,
                calibrationQuality =
                    Mathf.Clamp01(
                        _sumCalibrationQuality *
                        inv)
            };

        if (saveProfile)
        {
            SaveProfileInternal();
        }

        ResetCalibrationCollection();
    }

    private bool CanApplyConstraint(
        out float globalStrength)
    {
        globalStrength =
            0f;

        if (
            !enableConstraint ||
            _profile == null ||
            !_hasFaceBasis ||
            _cropper == null ||
            _surfaceSetter == null
        )
        {
            debugState =
                _surfaceSetter == null
                    ? "SurfaceApiMissing"
                    : _profile == null
                        ? "WaitingNeutral"
                        : "ModelPrimaryOnly";

            return false;
        }

        float observationAge =
            Mathf.Max(
                0f,
                (float)(
                    Time.realtimeSinceStartupAsDouble -
                    _lastObservationRealtime));

        if (
            observationAge >
            maximum2dAgeSeconds
        )
        {
            debugState =
                "2DStale";

            return false;
        }

        if (
            _runtimeManager != null &&
            _runtimeManager.IsBusy
        )
        {
            debugState =
                "ModelSwitch";

            return false;
        }

        if (
            _continuity != null &&
            (
                _continuity.State ==
                    KiwiTrackingContinuityState.ContinuityState.Holding ||
                _continuity.State ==
                    KiwiTrackingContinuityState.ContinuityState.Lost
            )
        )
        {
            debugState =
                "TrackingHold";

            return false;
        }

        if (
            requireSurfaceFit &&
            (
                _leftEye == null ||
                _rightEye == null ||
                _mouth == null ||
                !_leftEye.HasSurfaceFit ||
                !_rightEye.HasSurfaceFit ||
                !_mouth.HasSurfaceFit
            )
        )
        {
            debugState =
                "WaitingSurfaceFit";

            return false;
        }

        float dualQuality =
            _dualDomain != null
                ? Mathf.InverseLerp(
                    minimumDualDomainQuality,
                    1f,
                    _dualDomain.DualDomainQuality)
                : 1f;

        float yaw =
            _faceMotion != null
                ? Mathf.Abs(
                    _faceMotion.RenderedYawDegrees)
                : 0f;

        float yawStrength =
            1f -
            Mathf.InverseLerp(
                fullStrengthYawDegrees,
                Mathf.Max(
                    fullStrengthYawDegrees +
                        1f,
                    zeroStrengthYawDegrees),
                yaw);

        float continuityStrength =
            1f;

        if (_continuity != null)
        {
            switch (_continuity.State)
            {
                case KiwiTrackingContinuityState.ContinuityState.Stable:
                    continuityStrength =
                        1f;
                    break;

                case KiwiTrackingContinuityState.ContinuityState.Degraded:
                    continuityStrength =
                        0.68f;
                    break;

                case KiwiTrackingContinuityState.ContinuityState.Reacquiring:
                    continuityStrength =
                        0.34f;
                    break;

                default:
                    continuityStrength =
                        0f;
                    break;
            }
        }

        globalStrength =
            Mathf.Clamp01(
                dualQuality *
                yawStrength *
                continuityStrength);

        return
            globalStrength >
            0.0001f;
    }

    private Vector2 CalculateSurfaceTarget(
        SurfaceFittedRawImage image,
        Vector2 faceLocalResidual,
        float partQuality,
        float globalStrength,
        float horizontalGain,
        float verticalGain,
        float maximumOffset)
    {
        if (
            image == null ||
            !_cropper.TryGetSampleRect(
                image,
                out Rect sampleRect) ||
            sampleRect.width <=
                0.0001f ||
            sampleRect.height <=
                0.0001f ||
            !TryFaceLocalDeltaToCameraDelta(
                faceLocalResidual,
                out Vector2 cameraDelta)
        )
        {
            return
                Vector2.zero;
        }

        // Match FacePartCropper.MakeUvRect exactly:
        // X may mirror, MediaPipe Y(top-left) becomes UV Y(bottom-left).
        Vector2 cropSpaceDelta =
            new Vector2(
                _cropper.mirrorX
                    ? -cameraDelta.x
                    : cameraDelta.x,
                -cameraDelta.y);

        Vector2 cropRelative =
            new Vector2(
                cropSpaceDelta.x /
                    sampleRect.width,
                cropSpaceDelta.y /
                    sampleRect.height);

        cropRelative.x =
            ApplyDeadZone(
                cropRelative.x);

        cropRelative.y =
            ApplyDeadZone(
                cropRelative.y);

        float partStrength =
            Mathf.InverseLerp(
                minimumPartQuality,
                Mathf.Max(
                    minimumPartQuality +
                        0.01f,
                    fullStrengthPartQuality),
                partQuality);

        Vector2 target =
            new Vector2(
                cropRelative.x *
                    horizontalGain,
                cropRelative.y *
                    verticalGain) *
            (
                globalStrength *
                partStrength
            );

        return
            Vector2.ClampMagnitude(
                target,
                Mathf.Max(
                    0f,
                    maximumOffset));
    }

    private float ResolveEyeQuality(
        bool semanticLeft)
    {
        if (_dualDomain == null)
        {
            return 1f;
        }

        return
            semanticLeft
                ? _dualDomain.LeftEyeQuality
                : _dualDomain.RightEyeQuality;
    }

    private float ResolveNeutralEyeOpen(
        bool semanticLeft)
    {
        if (
            _actorCalibration == null ||
            !_actorCalibration.IsCalibrated
        )
        {
            return 0f;
        }

        return
            semanticLeft
                ? _actorCalibration
                    .NeutralLeftEyeOpenRatio
                : _actorCalibration
                    .NeutralRightEyeOpenRatio;
    }

    private float EyeVerticalFactor(
        float currentOpen,
        float neutralOpen)
    {
        if (
            !suppressEyeVerticalConstraintDuringBlink ||
            neutralOpen <=
                0.0001f
        )
        {
            return 1f;
        }

        float openFraction =
            currentOpen /
            neutralOpen;

        float openStrength =
            Mathf.InverseLerp(
                0.38f,
                0.78f,
                openFraction);

        return
            Mathf.Lerp(
                closedEyeVerticalGain,
                1f,
                openStrength);
    }

    private float ApplyDeadZone(
        float value)
    {
        float absolute =
            Mathf.Abs(
                value);

        if (
            absolute <=
            cropRelativeDeadZone
        )
        {
            return 0f;
        }

        float remaining =
            (
                absolute -
                cropRelativeDeadZone
            ) /
            Mathf.Max(
                0.0001f,
                1f -
                cropRelativeDeadZone);

        return
            Mathf.Sign(
                value) *
            remaining;
    }

    private Vector2 SmoothOffset(
        Vector2 current,
        Vector2 target,
        float partQuality,
        float dt)
    {
        float quality =
            Mathf.InverseLerp(
                minimumPartQuality,
                1f,
                partQuality);

        float response =
            Mathf.Lerp(
                minimumResponse,
                maximumResponse,
                quality);

        float t =
            1f -
            Mathf.Exp(
                -response *
                dt);

        return
            Vector2.Lerp(
                current,
                target,
                t);
    }

    private void ReturnOffsetsToNeutral(
        float dt)
    {
        float t =
            1f -
            Mathf.Exp(
                -returnToNeutralResponse *
                dt);

        _leftEyeOffset =
            Vector2.Lerp(
                _leftEyeOffset,
                Vector2.zero,
                t);

        _rightEyeOffset =
            Vector2.Lerp(
                _rightEyeOffset,
                Vector2.zero,
                t);

        _mouthOffset =
            Vector2.Lerp(
                _mouthOffset,
                Vector2.zero,
                t);

        ApplyOffset(
            _leftEye,
            _leftEyeOffset,
            _leftArgs);

        ApplyOffset(
            _rightEye,
            _rightEyeOffset,
            _rightArgs);

        ApplyOffset(
            _mouth,
            _mouthOffset,
            _mouthArgs);
    }

    private void ResetSurfaceOffsetsImmediate()
    {
        _leftEyeOffset =
            Vector2.zero;

        _rightEyeOffset =
            Vector2.zero;

        _mouthOffset =
            Vector2.zero;

        ApplyOffset(
            _leftEye,
            Vector2.zero,
            _leftArgs);

        ApplyOffset(
            _rightEye,
            Vector2.zero,
            _rightArgs);

        ApplyOffset(
            _mouth,
            Vector2.zero,
            _mouthArgs);
    }

    private void ApplyOffset(
        SurfaceFittedRawImage image,
        Vector2 offset,
        object[] args)
    {
        if (
            image == null ||
            _surfaceSetter == null
        )
        {
            return;
        }

        args[0] =
            offset;

        try
        {
            _surfaceSetter.Invoke(
                image,
                args);
        }
        catch
        {
            _surfaceSetter =
                null;

            _searchedSurfaceSetter =
                false;
        }
    }

    private void RefreshReferences(
        bool force)
    {
        if (
            force ||
            _runner == null
        )
        {
            _runner =
                FindFirstObjectByType<
                    Mediapipe.Unity.Sample.FaceLandmarkDetection.FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _dualDomain == null
        )
        {
            _dualDomain =
                FindFirstObjectByType<
                    KiwiDualDomainFaceQuality>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _actorCalibration == null
        )
        {
            _actorCalibration =
                FindFirstObjectByType<
                    KiwiActorFaceCalibration>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _continuity == null
        )
        {
            _continuity =
                FindFirstObjectByType<
                    KiwiTrackingContinuityState>(
                    FindObjectsInactive.Include);
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
            _cropper == null
        )
        {
            _cropper =
                FindFirstObjectByType<
                    FacePartCropper>(
                    FindObjectsInactive.Include);
        }

        if (_cropper != null)
        {
            SurfaceFittedRawImage nextLeft =
                _cropper.leftEyeImage
                as SurfaceFittedRawImage;

            SurfaceFittedRawImage nextRight =
                _cropper.rightEyeImage
                as SurfaceFittedRawImage;

            SurfaceFittedRawImage nextMouth =
                _cropper.mouthImage
                as SurfaceFittedRawImage;

            if (
                _leftEye !=
                    nextLeft ||
                _rightEye !=
                    nextRight ||
                _mouth !=
                    nextMouth
            )
            {
                ResetSurfaceOffsetsImmediate();

                _leftEye =
                    nextLeft;

                _rightEye =
                    nextRight;

                _mouth =
                    nextMouth;
            }
        }

        if (!_searchedSurfaceSetter)
        {
            _searchedSurfaceSetter =
                true;

            _surfaceSetter =
                typeof(
                    SurfaceFittedRawImage)
                .GetMethod(
                    SurfaceSetterName,
                    BindingFlags.Instance |
                    BindingFlags.Public);
        }
    }

    private void ResetCalibrationCollection()
    {
        _calibrationSamples =
            0;

        _calibrationStartedRealtime =
            0.0;

        _sumLeftEye =
            Vector2.zero;

        _sumRightEye =
            Vector2.zero;

        _sumMouth =
            Vector2.zero;

        _sumCalibrationQuality =
            0f;
    }

    private void TryLoadProfile()
    {
        string key =
            BuildProfileKey();

        if (!PlayerPrefs.HasKey(key))
        {
            return;
        }

        string json =
            PlayerPrefs.GetString(
                key,
                string.Empty);

        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            ConstraintProfile loaded =
                JsonUtility.FromJson<
                    ConstraintProfile>(
                    json);

            if (
                loaded != null &&
                loaded.version ==
                    1 &&
                loaded.calibrationQuality >
                    0.05f
            )
            {
                _profile =
                    loaded;
            }
        }
        catch
        {
            _profile =
                null;
        }
    }

    private void SaveProfileInternal()
    {
        if (_profile == null)
        {
            return;
        }

        PlayerPrefs.SetString(
            BuildProfileKey(),
            JsonUtility.ToJson(
                _profile));

        PlayerPrefs.Save();
    }

    private string BuildProfileKey()
    {
        string selected =
            profileName;

        if (
            followActorCalibrationProfileName &&
            _actorCalibration != null &&
            !string.IsNullOrWhiteSpace(
                _actorCalibration.profileName)
        )
        {
            selected =
                _actorCalibration.profileName;
        }

        string safe =
            string.IsNullOrWhiteSpace(
                selected)
                ? "Default"
                : selected.Trim();

        return
            KeyPrefix +
            safe;
    }

    private Vector2 NeutralLeftEye =>
        _profile != null
            ? new Vector2(
                _profile.leftEyeX,
                _profile.leftEyeY)
            : Vector2.zero;

    private Vector2 NeutralRightEye =>
        _profile != null
            ? new Vector2(
                _profile.rightEyeX,
                _profile.rightEyeY)
            : Vector2.zero;

    private Vector2 NeutralMouth =>
        _profile != null
            ? new Vector2(
                _profile.mouthX,
                _profile.mouthY)
            : Vector2.zero;

    private void UpdateDiagnostics()
    {
        debugCalibrated =
            IsCalibrated;

        debugSurfaceApiAvailable =
            SurfaceApiAvailable;

        debugCalibrationQuality =
            CalibrationQuality;

        debugLeftEyeOffset =
            _leftEyeOffset;

        debugRightEyeOffset =
            _rightEyeOffset;

        debugMouthOffset =
            _mouthOffset;

        debugObservationAgeMs =
            Mathf.Max(
                0f,
                (float)(
                    Time.realtimeSinceStartupAsDouble -
                    _lastObservationRealtime) *
                1000f);
    }

    private static bool IsFinite(
        Vector2 value)
    {
        return
            !float.IsNaN(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsInfinity(value.x) &&
            !float.IsInfinity(value.y);
    }
}
