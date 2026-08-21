using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// v5.0.1 commercial head-local camera sample frame.
///
/// FacePartCropper intentionally keeps a generous axis-aligned source ROI.
/// Camera landmarks inside that ROI still contain the actor's rigid head Roll,
/// while the fitted 3D avatar surface is already rotated by KiwiFaceMotion.
/// Sampling the raw rolled ROI directly therefore applies rigid Roll twice to
/// the visible eye/mouth patch. This component estimates the shared rigid Roll
/// from the bilateral eye crop centers and asks SurfaceFittedRawImage to sample
/// texture + semantic mask in a de-rolled local frame.
///
/// It never writes Avatar Root, RectTransform, crop position, mask contour,
/// surface fit, or tracking provider state.
/// </summary>
[DefaultExecutionOrder(830)]
[DisallowMultipleComponent]
public sealed class KiwiFacePartRigidSampleFrame : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Face-Part Rigid Sample Frame";

    [Header("Head-local sample frame")]
    public bool enableHeadLocalSampleFrame = true;

    [Tooltip("Maximum camera rigid Roll removed from the local eye/mouth sample. Avatar Root still owns the actual visible Roll.")]
    [Range(5f, 60f)]
    public float maximumCorrectionDegrees = 50f;

    [Tooltip("Very small eye-line angle noise is ignored without adding a temporal low-pass stage.")]
    [Range(0f, 2f)]
    public float restAngleDeadZoneDegrees = 0.25f;

    [Tooltip("Reject a one-sample eye-line angle jump this large. This protects all three patches from one isolated Eye crop outlier.")]
    [Range(5f, 45f)]
    public float maximumAcceptedFrameAngleJumpDegrees = 18f;

    [Header("Actor-neutral eye line")]
    [Tooltip("The first valid bilateral eye line becomes neutral immediately; nearby early samples only refine that reference.")]
    [Range(1, 20)]
    public int neutralRefineSamples = 8;

    [Range(0.5f, 8f)]
    public float neutralRefineToleranceDegrees = 3.5f;

    [Header("Diagnostics")]
    [SerializeField] private bool debugOperational;
    [SerializeField] private float debugEyeLineAngle;
    [SerializeField] private float debugNeutralEyeLineAngle;
    [SerializeField] private float debugAppliedRotation;
    [SerializeField] private int debugRejectedAngleJumps;

    private FacePartCropper _cropper;
    private SurfaceFittedRawImage _left;
    private SurfaceFittedRawImage _right;
    private SurfaceFittedRawImage _mouth;

    private bool _hasNeutral;
    private float _neutralAngle;
    private float _lastAcceptedAngle;
    private int _neutralSamples;
    private int _lastBindingSignature;
    private bool _hasPendingAngleJump;
    private float _pendingAngleJump;
    private int _pendingAngleJumpSamples;

    public static bool IsOperational { get; private set; }
    public static float AppliedRotationDegrees { get; private set; }
    public static float EyeLineAngleDegrees { get; private set; }
    public static int RejectedAngleJumpCount { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiFacePartRigidSampleFrame>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiFacePartRigidSampleFrame>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        RefreshReferences(true);
        ResetNeutralReference();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        ResetPartRotations();
    }

    private void OnDisable()
    {
        ResetPartRotations();
        IsOperational = false;
        AppliedRotationDegrees = 0f;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        RefreshReferences(true);
        ResetNeutralReference();
    }

    private void LateUpdate()
    {
        RefreshReferences(false);

        if (!enableHeadLocalSampleFrame)
        {
            ResetPartRotations();
            SetDiagnostics(false, 0f, 0f);
            return;
        }

        if (!TryResolveEyeLineAngle(out float angle))
        {
            // Keep the last safe local frame during a single missing semantic
            // sample. Resetting to zero here would create visible Roll flicker.
            SetDiagnostics(false, debugEyeLineAngle, debugAppliedRotation);
            return;
        }

        if (!_hasNeutral)
        {
            _hasNeutral = true;
            _neutralAngle = angle;
            _lastAcceptedAngle = angle;
            _neutralSamples = 1;

            ApplyRotation(0f);
            SetDiagnostics(true, angle, 0f);
            return;
        }

        float frameJump =
            Mathf.Abs(
                DeltaLineAngle(
                    _lastAcceptedAngle,
                    angle));

        if (
            frameJump >
            Mathf.Max(
                1f,
                maximumAcceptedFrameAngleJumpDegrees)
        )
        {
            // One isolated Eye crop must not rotate every face part. However,
            // a real fast Roll or a post-loss reacquisition can also move by a
            // large angle. Adopt a large jump only after two consecutive
            // geometrically-consistent samples at the new line angle.
            bool consistentPendingJump =
                _hasPendingAngleJump &&
                Mathf.Abs(
                    DeltaLineAngle(
                        _pendingAngleJump,
                        angle)) <= 4f;

            if (!consistentPendingJump)
            {
                _hasPendingAngleJump = true;
                _pendingAngleJump = angle;
                _pendingAngleJumpSamples = 1;
            }
            else
            {
                _pendingAngleJumpSamples++;
            }

            if (_pendingAngleJumpSamples < 2)
            {
                debugRejectedAngleJumps++;
                RejectedAngleJumpCount = debugRejectedAngleJumps;

                SetDiagnostics(
                    true,
                    _lastAcceptedAngle,
                    debugAppliedRotation);
                return;
            }

            angle = _pendingAngleJump;
            _hasPendingAngleJump = false;
            _pendingAngleJumpSamples = 0;
        }
        else
        {
            _hasPendingAngleJump = false;
            _pendingAngleJumpSamples = 0;
        }

        _lastAcceptedAngle = angle;

        if (
            _neutralSamples <
                Mathf.Max(1, neutralRefineSamples) &&
            Mathf.Abs(
                DeltaLineAngle(
                    _neutralAngle,
                    angle)) <=
                Mathf.Max(
                    0.1f,
                    neutralRefineToleranceDegrees)
        )
        {
            _neutralSamples++;

            float weight =
                1f /
                Mathf.Max(1, _neutralSamples);

            _neutralAngle =
                NormalizeLineAngle(
                    _neutralAngle +
                    DeltaLineAngle(
                        _neutralAngle,
                        angle) *
                    weight);
        }

        float relativeRoll =
            DeltaLineAngle(
                _neutralAngle,
                angle);

        if (
            Mathf.Abs(relativeRoll) <=
            Mathf.Max(0f, restAngleDeadZoneDegrees)
        )
        {
            relativeRoll = 0f;
        }

        float correction =
            Mathf.Clamp(
                relativeRoll,
                -Mathf.Max(0f, maximumCorrectionDegrees),
                Mathf.Max(0f, maximumCorrectionDegrees));

        ApplyRotation(correction);
        SetDiagnostics(true, angle, correction);
    }

    private bool TryResolveEyeLineAngle(
        out float angle)
    {
        angle = 0f;

        if (
            _cropper == null ||
            _left == null ||
            _right == null ||
            _cropper.sourceImage == null ||
            _cropper.sourceImage.texture == null
        )
        {
            return false;
        }

        Rect leftRect = _left.uvRect;
        Rect rightRect = _right.uvRect;

        if (
            !IsValidRect(leftRect) ||
            !IsValidRect(rightRect)
        )
        {
            return false;
        }

        float sourceAspect =
            _cropper.sourceImage.texture.width /
            (float)Mathf.Max(
                1,
                _cropper.sourceImage.texture.height);

        Vector2 delta =
            rightRect.center -
            leftRect.center;

        delta.x *=
            Mathf.Max(0.01f, sourceAspect);

        if (delta.sqrMagnitude < 0.0000001f)
        {
            return false;
        }

        angle =
            NormalizeLineAngle(
                Mathf.Atan2(
                    delta.y,
                    delta.x) *
                Mathf.Rad2Deg);

        return
            !float.IsNaN(angle) &&
            !float.IsInfinity(angle);
    }

    private void ApplyRotation(
        float degrees)
    {
        _left?.SetSampleFrameRotationDegrees(degrees);
        _right?.SetSampleFrameRotationDegrees(degrees);
        _mouth?.SetSampleFrameRotationDegrees(degrees);

        AppliedRotationDegrees = degrees;
    }

    private void ResetPartRotations()
    {
        _left?.ResetSampleFrameRotation();
        _right?.ResetSampleFrameRotation();
        _mouth?.ResetSampleFrameRotation();
    }

    private void RefreshReferences(
        bool force)
    {
        if (force || _cropper == null)
        {
            _cropper =
                FindFirstObjectByType<FacePartCropper>(
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

        if (
            force ||
            signature != _lastBindingSignature
        )
        {
            ResetPartRotations();

            _left = left;
            _right = right;
            _mouth = mouth;
            _lastBindingSignature = signature;

            ResetNeutralReference();
        }
    }

    private void ResetNeutralReference()
    {
        _hasNeutral = false;
        _neutralAngle = 0f;
        _lastAcceptedAngle = 0f;
        _neutralSamples = 0;
        _hasPendingAngleJump = false;
        _pendingAngleJump = 0f;
        _pendingAngleJumpSamples = 0;

        debugOperational = false;
        debugEyeLineAngle = 0f;
        debugNeutralEyeLineAngle = 0f;
        debugAppliedRotation = 0f;
        debugRejectedAngleJumps = 0;

        IsOperational = false;
        AppliedRotationDegrees = 0f;
        EyeLineAngleDegrees = 0f;
        RejectedAngleJumpCount = 0;
    }

    private void SetDiagnostics(
        bool operational,
        float eyeLineAngle,
        float appliedRotation)
    {
        debugOperational = operational;
        debugEyeLineAngle = eyeLineAngle;
        debugNeutralEyeLineAngle = _neutralAngle;
        debugAppliedRotation = appliedRotation;

        IsOperational = operational;
        EyeLineAngleDegrees = eyeLineAngle;
        AppliedRotationDegrees = appliedRotation;
        RejectedAngleJumpCount = debugRejectedAngleJumps;
    }

    private static int GetInstanceIdSafe(
        Object value)
    {
        return value != null
            ? value.GetInstanceID()
            : 0;
    }

    private static bool IsValidRect(
        Rect rect)
    {
        return
            rect.width > 0.000001f &&
            rect.height > 0.000001f &&
            IsFinite(rect.x) &&
            IsFinite(rect.y) &&
            IsFinite(rect.width) &&
            IsFinite(rect.height);
    }

    private static bool IsFinite(
        float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
    }

    private static float DeltaLineAngle(
        float from,
        float to)
    {
        return
            0.5f *
            Mathf.DeltaAngle(
                from * 2f,
                to * 2f);
    }

    private static float NormalizeLineAngle(
        float angle)
    {
        while (angle >= 90f)
        {
            angle -= 180f;
        }

        while (angle < -90f)
        {
            angle += 180f;
        }

        return angle;
    }
}
