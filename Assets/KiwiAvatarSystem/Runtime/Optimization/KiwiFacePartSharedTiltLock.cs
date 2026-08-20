using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Applies one shared render-phase head-roll correction to both eyes and mouth.
/// Rigid head roll is separated from local blink/lip deformation.
/// </summary>
[DefaultExecutionOrder(900)]
[DisallowMultipleComponent]
public sealed class KiwiFacePartSharedTiltLock : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Shared Face-Part Tilt Lock";

    private static readonly int SamplePivotId =
        Shader.PropertyToID("_SamplePivot");
    private static readonly int SampleRotationRadId =
        Shader.PropertyToID("_SampleRotationRad");
    private static readonly int SourceAspectId =
        Shader.PropertyToID("_SourceAspect");

    [Header("Shared rigid roll")]
    public bool enableSharedTiltLock = true;
    [Range(0f, 1f)] public float correctionStrength = 1.0f;
    [Range(0f, 1f)] public float softDeadZoneDegrees = 0.10f;
    [Range(10f, 80f)] public float maximumCorrectionDegrees = 55f;
    [Range(30f, 400f)] public float renderResponse = 190f;

    [Header("Neutral calibration")]
    [Range(0f, 1.5f)] public float calibrationDelay = 0.60f;
    [Range(3, 30)] public int calibrationFrames = 15;

    [Header("Neutral-pose calibration gate")]
    [Tooltip("Neutral calibration only accumulates while the rendered head is close to frontal.")]
    [Range(3f, 30f)]
    public float maximumCalibrationYawDegrees = 12f;

    [Header("Diagnostics")]
    [SerializeField] private bool debugCalibrated;
    [SerializeField] private float debugReferenceAngle;
    [SerializeField] private float debugRenderedEyeAngle;
    [SerializeField] private float debugCorrectionAngle;

    private FacePartCropper _cropper;
    private KiwiFaceMotion _faceMotion;
    private FacePartAngleLock[] _legacyLocks =
        Array.Empty<FacePartAngleLock>();

    private float _enabledAt;
    private int _calibrationCount;
    private float _sumSin;
    private float _sumCos;
    private bool _calibrated;
    private float _referenceAngle;
    private float _renderedCorrection;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        KiwiFacePartSharedTiltLock existing =
            FindFirstObjectByType<KiwiFacePartSharedTiltLock>(
                FindObjectsInactive.Include);

        if (existing != null)
            return;

        GameObject host = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiFacePartSharedTiltLock>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += HandleSceneLoaded;
        RefreshReferences(true);
        ResetCalibration();
    }

    private void OnEnable()
    {
        _enabledAt = Time.unscaledTime;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        ResetShaderRotation();
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

        DisableIndependentAngleLocks();

        if (!enableSharedTiltLock)
        {
            _renderedCorrection = 0f;
            ApplyToAllParts(0f);
            return;
        }

        if (!TryGetRenderedEyeAngle(
            out float eyeAngle,
            out float aspect))
        {
            return;
        }

        debugRenderedEyeAngle = eyeAngle;

        if (!_calibrated)
        {
            ProcessCalibration(eyeAngle);
            ApplyToAllParts(0f);
            return;
        }

        float delta =
            DeltaLineAngle(
                _referenceAngle,
                eyeAngle);

        float absDelta = Mathf.Abs(delta);
        float softWeight =
            softDeadZoneDegrees <= 0.0001f
                ? 1f
                : Smooth01(
                    Mathf.InverseLerp(
                        0f,
                        softDeadZoneDegrees,
                        absDelta));

        float target =
            Mathf.Clamp(
                delta *
                softWeight *
                correctionStrength,
                -maximumCorrectionDegrees,
                maximumCorrectionDegrees);

        float dt =
            Mathf.Max(
                0.000001f,
                Time.unscaledDeltaTime);

        _renderedCorrection =
            Mathf.LerpAngle(
                _renderedCorrection,
                target,
                ExpAlpha(
                    renderResponse,
                    dt));

        debugCorrectionAngle =
            _renderedCorrection;

        ApplyToAllParts(
            _renderedCorrection);
    }

    private void RefreshReferences(bool force)
    {
        if (force || _cropper == null)
        {
            _cropper =
                FindFirstObjectByType<FacePartCropper>(
                    FindObjectsInactive.Include);
        }

        if (force || _faceMotion == null)
        {
            _faceMotion =
                FindFirstObjectByType<KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _legacyLocks == null ||
            _legacyLocks.Length == 0
        )
        {
            _legacyLocks =
                FindObjectsByType<FacePartAngleLock>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
        }
    }

    private void DisableIndependentAngleLocks()
    {
        if (_legacyLocks == null)
            return;

        for (int i = 0; i < _legacyLocks.Length; i++)
        {
            FacePartAngleLock angleLock =
                _legacyLocks[i];

            if (angleLock != null)
            {
                // Keep rigid head roll in one shared channel.
                angleLock.enableAngleLock = false;
            }
        }
    }

    private bool TryGetRenderedEyeAngle(
        out float angle,
        out float aspect)
    {
        angle = 0f;
        aspect = GetSourceAspect();

        Rect leftRect = _cropper.leftEyeImage.uvRect;
        Rect rightRect = _cropper.rightEyeImage.uvRect;

        if (
            leftRect.width <= 0.000001f ||
            leftRect.height <= 0.000001f ||
            rightRect.width <= 0.000001f ||
            rightRect.height <= 0.000001f
        )
        {
            return false;
        }

        Vector2 delta =
            rightRect.center -
            leftRect.center;

        float dx = delta.x * aspect;
        float dy = delta.y;

        if (dx * dx + dy * dy < 0.00000001f)
            return false;

        angle =
            NormalizeLineAngle(
                Mathf.Atan2(dy, dx) *
                Mathf.Rad2Deg);

        return true;
    }

    private void ProcessCalibration(
        float currentAngle)
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

        float radians =
            currentAngle *
            2f *
            Mathf.Deg2Rad;

        _sumSin += Mathf.Sin(radians);
        _sumCos += Mathf.Cos(radians);
        _calibrationCount++;

        if (
            _calibrationCount <
            Mathf.Max(3, calibrationFrames)
        )
        {
            return;
        }

        _referenceAngle =
            NormalizeLineAngle(
                0.5f *
                Mathf.Atan2(
                    _sumSin,
                    _sumCos) *
                Mathf.Rad2Deg);

        _calibrated = true;
        debugCalibrated = true;
        debugReferenceAngle = _referenceAngle;
    }

    private void ApplyToAllParts(
        float angleDegrees)
    {
        if (_cropper == null)
            return;

        float aspect = GetSourceAspect();

        ApplyToImage(
            _cropper.leftEyeImage,
            angleDegrees,
            aspect);
        ApplyToImage(
            _cropper.rightEyeImage,
            angleDegrees,
            aspect);
        ApplyToImage(
            _cropper.mouthImage,
            angleDegrees,
            aspect);
    }

    private static void ApplyToImage(
        RawImage image,
        float angleDegrees,
        float aspect)
    {
        if (
            image == null ||
            image.material == null
        )
        {
            return;
        }

        Material material = image.material;

        if (!material.HasProperty(
            SampleRotationRadId))
        {
            return;
        }

        Vector2 pivot = image.uvRect.center;

        if (material.HasProperty(SamplePivotId))
        {
            material.SetVector(
                SamplePivotId,
                new Vector4(
                    pivot.x,
                    pivot.y,
                    0f,
                    0f));
        }

        if (material.HasProperty(SourceAspectId))
        {
            material.SetFloat(
                SourceAspectId,
                aspect);
        }

        material.SetFloat(
            SampleRotationRadId,
            angleDegrees *
            Mathf.Deg2Rad);
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

        return texture.width /
            (float)texture.height;
    }

    public void Recalibrate()
    {
        ResetCalibration();
    }

    private void ResetCalibration()
    {
        _enabledAt = Time.unscaledTime;
        _calibrationCount = 0;
        _sumSin = 0f;
        _sumCos = 0f;
        _calibrated = false;
        _referenceAngle = 0f;
        _renderedCorrection = 0f;

        debugCalibrated = false;
        debugReferenceAngle = 0f;
        debugRenderedEyeAngle = 0f;
        debugCorrectionAngle = 0f;

        ResetShaderRotation();
    }

    private void ResetShaderRotation()
    {
        if (_cropper != null)
            ApplyToAllParts(0f);
    }

    private static float DeltaLineAngle(
        float from,
        float to)
    {
        return 0.5f *
            Mathf.DeltaAngle(
                from * 2f,
                to * 2f);
    }

    private static float NormalizeLineAngle(
        float angle)
    {
        while (angle >= 90f)
            angle -= 180f;

        while (angle < -90f)
            angle += 180f;

        return angle;
    }

    private static float Smooth01(float value)
    {
        value = Mathf.Clamp01(value);
        return value *
            value *
            (3f - 2f * value);
    }

    private static float ExpAlpha(
        float response,
        float dt)
    {
        if (response <= 0f)
            return 1f;

        return 1f -
            Mathf.Exp(
                -response *
                Mathf.Max(0f, dt));
    }
}
