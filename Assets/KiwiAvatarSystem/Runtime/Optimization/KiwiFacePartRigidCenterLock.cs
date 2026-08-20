using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Conservative roll-time mouth attachment correction.
///
/// v2.3 fixes two failure modes from the earlier helper:
/// 1) it no longer mixes sample-domain eye/mouth rectangles with the
///    render-domain uvRect,
/// 2) it never accumulates its previous correction when FacePartCropper did
///    not write a new uvRect on that render frame.
///
/// The shared tilt sampler remains responsible for most rigid-roll stability.
/// This component contributes only a small bounded mouth-center correction.
/// </summary>
[DefaultExecutionOrder(825)]
[DisallowMultipleComponent]
public sealed class KiwiFacePartRigidCenterLock : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Face-Part Rigid Center Lock";

    [Header("Roll attachment")]
    public bool enableRigidMouthAttachment = true;

    [Range(0f, 20f)]
    public float correctionStartDegrees = 5f;

    [Range(5f, 45f)]
    public float correctionFullDegrees = 20f;

    [Range(0f, 1f)]
    public float maximumCorrectionStrength = 0.45f;

    [Range(0f, 0.03f)]
    public float maximumCorrectionUv = 0.006f;

    [Range(30f, 300f)]
    public float correctionResponse = 110f;

    [Header("Neutral calibration")]
    [Range(0f, 1.5f)]
    public float calibrationDelay = 0.65f;

    [Range(3, 30)]
    public int calibrationFrames = 15;

    [Header("Neutral-pose calibration gate")]
    [Tooltip("Neutral calibration only accumulates while the rendered head is close to frontal.")]
    [Range(3f, 30f)]
    public float maximumCalibrationYawDegrees = 12f;

    [Header("Diagnostics")]
    [SerializeField]
    private bool debugCalibrated;

    [SerializeField]
    private float debugRollDegrees;

    [SerializeField]
    private Vector2 debugAppliedCorrection;

    [SerializeField]
    private bool debugRecoveredPreviousWrite;

    private FacePartCropper _cropper;
    private KiwiFaceMotion _faceMotion;

    private float _enabledAt;
    private int _calibrationCount;

    private float _referenceLineSin;
    private float _referenceLineCos;

    private float _mouthLocalXSum;
    private float _mouthLocalYSum;

    private bool _calibrated;
    private float _referenceEyeAngle;
    private Vector2 _referenceMouthLocal;

    private Vector2 _renderedCorrection;

    // Non-cumulative output bookkeeping.
    private bool _hasPreviousWrite;
    private Vector2 _lastWrittenMouthCenter;
    private Vector2 _lastAppliedCorrection;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        KiwiFacePartRigidCenterLock existing =
            FindFirstObjectByType<KiwiFacePartRigidCenterLock>(
                FindObjectsInactive.Include);

        if (existing != null)
        {
            return;
        }

        GameObject host =
            new GameObject(
                RuntimeObjectName);

        DontDestroyOnLoad(host);

        host.AddComponent<
            KiwiFacePartRigidCenterLock>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        RefreshReferences(true);
        ResetCalibration();
    }

    private void OnEnable()
    {
        _enabledAt =
            Time.unscaledTime;
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
        RefreshReferences(true);
        ResetCalibration();
    }

    private void LateUpdate()
    {
        RefreshReferences(false);

        if (
            _cropper == null ||
            _cropper.leftEyeImage == null ||
            _cropper.rightEyeImage == null ||
            _cropper.mouthImage == null
        )
        {
            return;
        }

        Rect leftDisplay =
            _cropper.leftEyeImage.uvRect;

        Rect rightDisplay =
            _cropper.rightEyeImage.uvRect;

        Rect mouthBase =
            RecoverUncorrectedMouthRect(
                _cropper.mouthImage.uvRect);

        if (
            !IsValidRect(leftDisplay) ||
            !IsValidRect(rightDisplay) ||
            !IsValidRect(mouthBase)
        )
        {
            ApplyCorrection(
                mouthBase,
                Vector2.zero);

            return;
        }

        float aspect =
            GetSourceAspect();

        if (
            !TryBuildEyeBasis(
                leftDisplay.center,
                rightDisplay.center,
                aspect,
                out Vector2 eyeMidMetric,
                out Vector2 eyeAxis,
                out Vector2 downAxis,
                out float eyeSpan,
                out float eyeAngle)
        )
        {
            ApplyCorrection(
                mouthBase,
                Vector2.zero);

            return;
        }

        if (!_calibrated)
        {
            ProcessCalibration(
                mouthBase.center,
                aspect,
                eyeMidMetric,
                eyeAxis,
                downAxis,
                eyeSpan,
                eyeAngle);

            ApplyCorrection(
                mouthBase,
                Vector2.zero);

            return;
        }

        float roll =
            DeltaLineAngle(
                _referenceEyeAngle,
                eyeAngle);

        debugRollDegrees =
            roll;

        float tiltWeight =
            Smooth01(
                Mathf.InverseLerp(
                    correctionStartDegrees,
                    Mathf.Max(
                        correctionStartDegrees +
                        0.001f,
                        correctionFullDegrees),
                    Mathf.Abs(roll)));

        tiltWeight *=
            maximumCorrectionStrength;

        Vector2 expectedMouthMetric =
            eyeMidMetric +
            eyeAxis *
            (
                _referenceMouthLocal.x *
                eyeSpan
            ) +
            downAxis *
            (
                _referenceMouthLocal.y *
                eyeSpan
            );

        Vector2 currentMouthMetric =
            ToMetric(
                mouthBase.center,
                aspect);

        Vector2 correctionMetric =
            expectedMouthMetric -
            currentMouthMetric;

        Vector2 correctionUv =
            FromMetricDelta(
                correctionMetric,
                aspect);

        correctionUv =
            Vector2.ClampMagnitude(
                correctionUv,
                maximumCorrectionUv);

        Vector2 targetCorrection =
            enableRigidMouthAttachment
                ? correctionUv *
                  tiltWeight
                : Vector2.zero;

        ApplyCorrection(
            mouthBase,
            targetCorrection);
    }

    private Rect RecoverUncorrectedMouthRect(
        Rect current)
    {
        debugRecoveredPreviousWrite =
            false;

        if (!_hasPreviousWrite)
        {
            return current;
        }

        // If Cropper did not rewrite the mouth this render frame, uvRect still
        // contains our previous correction. Remove it before calculating the
        // next one. If Cropper did write a new rect, use that fresh rect as-is.
        const float sameCenterTolerance =
            0.000003f;

        if (
            Vector2.Distance(
                current.center,
                _lastWrittenMouthCenter) <=
            sameCenterTolerance
        )
        {
            Vector2 center =
                current.center -
                _lastAppliedCorrection;

            current.center =
                center;

            debugRecoveredPreviousWrite =
                true;
        }

        return current;
    }

    private void ApplyCorrection(
        Rect baseRect,
        Vector2 targetCorrection)
    {
        if (
            _cropper == null ||
            _cropper.mouthImage == null ||
            !IsValidRect(baseRect)
        )
        {
            return;
        }

        float dt =
            Mathf.Max(
                0.000001f,
                Time.unscaledDeltaTime);

        _renderedCorrection =
            Vector2.Lerp(
                _renderedCorrection,
                targetCorrection,
                ExpAlpha(
                    correctionResponse,
                    dt));

        Rect output =
            baseRect;

        output.center +=
            _renderedCorrection;

        _cropper.mouthImage.uvRect =
            output;

        _lastAppliedCorrection =
            _renderedCorrection;

        _lastWrittenMouthCenter =
            output.center;

        _hasPreviousWrite =
            true;

        debugAppliedCorrection =
            _renderedCorrection;
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

            _hasPreviousWrite =
                false;

            _renderedCorrection =
                Vector2.zero;
        }

        if (force || _faceMotion == null)
        {
            _faceMotion =
                FindFirstObjectByType<KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }
    }

    private void ProcessCalibration(
        Vector2 mouthUv,
        float aspect,
        Vector2 eyeMidMetric,
        Vector2 eyeAxis,
        Vector2 downAxis,
        float eyeSpan,
        float eyeAngle)
    {
        if (
            Time.unscaledTime -
            _enabledAt <
            calibrationDelay
        )
        {
            return;
        }

        if (
            _faceMotion != null &&
            Mathf.Abs(
                _faceMotion.RenderedYawDegrees) >
                maximumCalibrationYawDegrees)
        {
            return;
        }

        Vector2 relative =
            ToMetric(
                mouthUv,
                aspect) -
            eyeMidMetric;

        float localX =
            Vector2.Dot(
                relative,
                eyeAxis) /
            eyeSpan;

        float localY =
            Vector2.Dot(
                relative,
                downAxis) /
            eyeSpan;

        float doubleAngle =
            eyeAngle *
            2f *
            Mathf.Deg2Rad;

        _referenceLineSin +=
            Mathf.Sin(
                doubleAngle);

        _referenceLineCos +=
            Mathf.Cos(
                doubleAngle);

        _mouthLocalXSum +=
            localX;

        _mouthLocalYSum +=
            localY;

        _calibrationCount++;

        if (
            _calibrationCount <
            Mathf.Max(
                3,
                calibrationFrames)
        )
        {
            return;
        }

        _referenceEyeAngle =
            NormalizeLineAngle(
                0.5f *
                Mathf.Atan2(
                    _referenceLineSin,
                    _referenceLineCos) *
                Mathf.Rad2Deg);

        _referenceMouthLocal =
            new Vector2(
                _mouthLocalXSum /
                _calibrationCount,
                _mouthLocalYSum /
                _calibrationCount);

        _calibrated =
            true;

        debugCalibrated =
            true;
    }

    private static bool TryBuildEyeBasis(
        Vector2 leftUv,
        Vector2 rightUv,
        float aspect,
        out Vector2 eyeMid,
        out Vector2 eyeAxis,
        out Vector2 downAxis,
        out float eyeSpan,
        out float lineAngle)
    {
        Vector2 left =
            ToMetric(
                leftUv,
                aspect);

        Vector2 right =
            ToMetric(
                rightUv,
                aspect);

        Vector2 delta =
            right - left;

        eyeSpan =
            delta.magnitude;

        eyeMid =
            (left + right) *
            0.5f;

        if (
            eyeSpan <
            0.0001f
        )
        {
            eyeAxis =
                Vector2.right;

            downAxis =
                Vector2.down;

            lineAngle =
                0f;

            return false;
        }

        eyeAxis =
            delta /
            eyeSpan;

        downAxis =
            new Vector2(
                -eyeAxis.y,
                eyeAxis.x);

        lineAngle =
            NormalizeLineAngle(
                Mathf.Atan2(
                    eyeAxis.y,
                    eyeAxis.x) *
                Mathf.Rad2Deg);

        return true;
    }

    private float GetSourceAspect()
    {
        Texture texture =
            _cropper != null &&
            _cropper.sourceImage != null
                ? _cropper.sourceImage.texture
                : null;

        if (
            texture == null ||
            texture.width <= 0 ||
            texture.height <= 0
        )
        {
            return 1f;
        }

        return
            texture.width /
            (float)texture.height;
    }

    public void Recalibrate()
    {
        ResetCalibration();
    }

    private void ResetCalibration()
    {
        _enabledAt =
            Time.unscaledTime;

        _calibrationCount =
            0;

        _referenceLineSin =
            0f;

        _referenceLineCos =
            0f;

        _mouthLocalXSum =
            0f;

        _mouthLocalYSum =
            0f;

        _calibrated =
            false;

        _referenceEyeAngle =
            0f;

        _referenceMouthLocal =
            Vector2.zero;

        _renderedCorrection =
            Vector2.zero;

        _hasPreviousWrite =
            false;

        _lastAppliedCorrection =
            Vector2.zero;

        _lastWrittenMouthCenter =
            Vector2.zero;

        debugCalibrated =
            false;

        debugRollDegrees =
            0f;

        debugAppliedCorrection =
            Vector2.zero;

        debugRecoveredPreviousWrite =
            false;
    }

    private static bool IsValidRect(
        Rect rect)
    {
        return
            rect.width >
                0.000001f &&
            rect.height >
                0.000001f &&
            IsFinite(
                rect.x) &&
            IsFinite(
                rect.y) &&
            IsFinite(
                rect.width) &&
            IsFinite(
                rect.height);
    }

    private static Vector2 ToMetric(
        Vector2 uv,
        float aspect)
    {
        return
            new Vector2(
                uv.x *
                Mathf.Max(
                    0.0001f,
                    aspect),
                uv.y);
    }

    private static Vector2 FromMetricDelta(
        Vector2 metricDelta,
        float aspect)
    {
        return
            new Vector2(
                metricDelta.x /
                Mathf.Max(
                    0.0001f,
                    aspect),
                metricDelta.y);
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
            angle -=
                180f;
        }

        while (angle < -90f)
        {
            angle +=
                180f;
        }

        return angle;
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
            (3f - 2f * value);
    }

    private static float ExpAlpha(
        float response,
        float dt)
    {
        if (response <= 0f)
        {
            return 1f;
        }

        return
            1f -
            Mathf.Exp(
                -response *
                Mathf.Max(
                    0f,
                    dt));
    }

    private static bool IsFinite(
        float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
    }
}
