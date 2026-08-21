using System;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// v3.9 actor-specific neutral calibration.
///
/// Commercial facial mocap systems calibrate to the performer rather than
/// assuming one global open-eye geometry for everybody. This component learns
/// only a guarded neutral pose and persists the resulting small profile.
/// </summary>
[DefaultExecutionOrder(-16000)]
[DisallowMultipleComponent]
public sealed class KiwiActorFaceCalibration : MonoBehaviour
{
    [Serializable]
    private sealed class PersistedProfile
    {
        public int version = 1;
        public float leftEyeOpenRatio;
        public float rightEyeOpenRatio;
        public float mouthOpenRatio;
        public float eyeSpan;
        public float faceWidth;
        public float calibrationQuality;
    }

    private const string RuntimeObjectName =
        "[Kiwi] Actor Face Calibration";

    private const string KeyPrefix =
        "Kiwi.ActorFaceCalibration.v1.";

    [Header("Profile")]
    public string profileName = "Default";
    public bool loadSavedProfile = true;
    public bool saveProfile = true;

    [Header("Automatic neutral calibration")]
    public bool autoCalibrateIfMissing = true;

    [Range(8, 80)]
    public int requiredFreshSamples = 24;

    [Range(0.15f, 2.0f)]
    public float minimumCalibrationSeconds = 0.40f;

    [Range(0.30f, 1f)]
    public float minimumPartQuality = 0.78f;

    [Range(2f, 25f)]
    public float maximumNeutralYawDegrees = 9f;

    [Range(0.05f, 0.35f)]
    public float maximumNeutralMouthOpenRatio = 0.18f;

    [Range(0.08f, 0.45f)]
    public float minimumNeutralEyeOpenRatio = 0.12f;

    [Tooltip("Maximum camera-observation age accepted for automatic neutral calibration. Degraded cadence may still calibrate when the actual source frame is fresh enough.")]
    [Range(0.10f, 0.40f)]
    public float maximumCalibrationSourceAgeSeconds = 0.22f;

    [Header("Adaptive blink threshold")]
    [Range(0.45f, 0.90f)]
    public float closeStartFromNeutral = 0.68f;

    [Range(0.20f, 0.70f)]
    public float closeFullFromNeutral = 0.38f;

    [Header("Diagnostics")]
    [SerializeField] private bool debugCalibrated;
    [SerializeField] private bool debugCollecting;
    [SerializeField] private int debugSamples;
    [SerializeField] private float debugNeutralEyeOpen;
    [SerializeField] private float debugNeutralMouthOpen;
    [SerializeField] private float debugSuggestedCloseStart;
    [SerializeField] private float debugSuggestedCloseFull;
    [SerializeField] private float debugCalibrationQuality;

    private KiwiDualDomainFaceQuality _dualDomain;
    private KiwiTrackingContinuityState _continuity;
    private KiwiFaceMotion _faceMotion;

    private PersistedProfile _profile;

    private long _lastObserved2dTimestamp = long.MinValue;
    private int _samples;
    private double _collectStartedRealtime;

    private float _sumLeftEyeOpen;
    private float _sumRightEyeOpen;
    private float _sumMouthOpen;
    private float _sumEyeSpan;
    private float _sumFaceWidth;
    private float _sumQuality;

    public bool IsCalibrated => _profile != null;

    public bool IsCollecting =>
        _samples > 0 &&
        _profile == null;

    public int CollectedSamples =>
        _samples;

    public float NeutralLeftEyeOpenRatio =>
        _profile != null
            ? _profile.leftEyeOpenRatio
            : 0f;

    public float NeutralRightEyeOpenRatio =>
        _profile != null
            ? _profile.rightEyeOpenRatio
            : 0f;

    public float NeutralEyeOpenRatio =>
        _profile != null
            ? (
                _profile.leftEyeOpenRatio +
                _profile.rightEyeOpenRatio
            ) * 0.5f
            : 0f;

    public float NeutralMouthOpenRatio =>
        _profile != null
            ? _profile.mouthOpenRatio
            : 0f;

    public float CalibrationQuality =>
        _profile != null
            ? _profile.calibrationQuality
            : 0f;

    public float SuggestedGeometryCloseStart
    {
        get
        {
            float neutral = NeutralEyeOpenRatio;

            if (neutral <= 0.0001f)
            {
                return 0.18f;
            }

            return
                Mathf.Clamp(
                    neutral *
                    closeStartFromNeutral,
                    0.10f,
                    0.30f);
        }
    }

    public float SuggestedGeometryCloseFull
    {
        get
        {
            float neutral = NeutralEyeOpenRatio;

            if (neutral <= 0.0001f)
            {
                return 0.095f;
            }

            float value =
                Mathf.Clamp(
                    neutral *
                    closeFullFromNeutral,
                    0.045f,
                    0.18f);

            return
                Mathf.Min(
                    value,
                    SuggestedGeometryCloseStart -
                    0.020f);
        }
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiActorFaceCalibration>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(
                RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiActorFaceCalibration>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        RefreshReferences();

        if (loadSavedProfile)
        {
            TryLoad();
        }
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _dualDomain = null;
        _continuity = null;
        _faceMotion = null;
        _lastObserved2dTimestamp = long.MinValue;

        RefreshReferences();
    }

    private void Update()
    {
        RefreshReferences();

        if (
            _profile == null &&
            autoCalibrateIfMissing
        )
        {
            TryCollectNeutralSample();
        }

        UpdateDiagnostics();
    }

    [ContextMenu("Recalibrate Actor Face")]
    public void Recalibrate()
    {
        _profile = null;
        ResetCollection();

        PlayerPrefs.DeleteKey(BuildKey());
        PlayerPrefs.Save();
    }

    [ContextMenu("Save Actor Face Profile")]
    public void SaveNow()
    {
        if (_profile != null)
        {
            SaveProfileInternal();
        }
    }

    private void TryCollectNeutralSample()
    {
        if (
            _dualDomain == null ||
            !_dualDomain.Has2DFace ||
            _continuity == null
        )
        {
            return;
        }

        // v4.8 commercial neutral solve:
        // Stable and high-quality Degraded samples are both usable when their
        // actual camera observation is still fresh. Do not throw away an
        // otherwise-good neutral collection just because cadence jitter caused
        // one Degraded frame. Holding/Lost/Reacquiring cannot contribute.
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
            ResetCollection();
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
            _faceMotion != null &&
            Mathf.Abs(
                _faceMotion.RenderedYawDegrees) >
                maximumNeutralYawDegrees
        )
        {
            return;
        }

        if (
            _dualDomain.LeftEyeQuality <
                minimumPartQuality ||
            _dualDomain.RightEyeQuality <
                minimumPartQuality ||
            _dualDomain.MouthQuality <
                minimumPartQuality
        )
        {
            return;
        }

        if (
            _dualDomain.LeftEyeOpenRatio <
                minimumNeutralEyeOpenRatio ||
            _dualDomain.RightEyeOpenRatio <
                minimumNeutralEyeOpenRatio ||
            _dualDomain.MouthOpenRatio >
                maximumNeutralMouthOpenRatio
        )
        {
            return;
        }

        long timestamp =
            _dualDomain.FrameTimestamp;

        if (
            timestamp ==
                long.MinValue ||
            timestamp ==
                _lastObserved2dTimestamp
        )
        {
            return;
        }

        _lastObserved2dTimestamp = timestamp;

        if (_samples == 0)
        {
            _collectStartedRealtime =
                Time.realtimeSinceStartupAsDouble;
        }

        _samples++;

        _sumLeftEyeOpen +=
            _dualDomain.LeftEyeOpenRatio;

        _sumRightEyeOpen +=
            _dualDomain.RightEyeOpenRatio;

        _sumMouthOpen +=
            _dualDomain.MouthOpenRatio;

        _sumEyeSpan +=
            _dualDomain.EyeSpan;

        _sumFaceWidth +=
            _dualDomain.FaceWidth;

        _sumQuality +=
            _dualDomain.DualDomainQuality;

        if (
            _samples <
                Mathf.Max(
                    1,
                    requiredFreshSamples)
        )
        {
            return;
        }

        if (
            Time.realtimeSinceStartupAsDouble -
                _collectStartedRealtime <
            minimumCalibrationSeconds
        )
        {
            return;
        }

        float inv =
            1f /
            Mathf.Max(
                1,
                _samples);

        _profile =
            new PersistedProfile
            {
                leftEyeOpenRatio =
                    _sumLeftEyeOpen * inv,
                rightEyeOpenRatio =
                    _sumRightEyeOpen * inv,
                mouthOpenRatio =
                    _sumMouthOpen * inv,
                eyeSpan =
                    _sumEyeSpan * inv,
                faceWidth =
                    _sumFaceWidth * inv,
                calibrationQuality =
                    Mathf.Clamp01(
                        _sumQuality * inv)
            };

        if (saveProfile)
        {
            SaveProfileInternal();
        }

        ResetCollection();
    }

    private void ResetCollection()
    {
        _samples = 0;
        _collectStartedRealtime = 0.0;
        _sumLeftEyeOpen = 0f;
        _sumRightEyeOpen = 0f;
        _sumMouthOpen = 0f;
        _sumEyeSpan = 0f;
        _sumFaceWidth = 0f;
        _sumQuality = 0f;
    }

    private void TryLoad()
    {
        string key = BuildKey();

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
            PersistedProfile loaded =
                JsonUtility.FromJson<
                    PersistedProfile>(
                    json);

            if (
                loaded != null &&
                loaded.version == 1 &&
                loaded.leftEyeOpenRatio > 0.01f &&
                loaded.rightEyeOpenRatio > 0.01f
            )
            {
                _profile = loaded;
            }
        }
        catch
        {
            _profile = null;
        }
    }

    private void SaveProfileInternal()
    {
        if (_profile == null)
        {
            return;
        }

        PlayerPrefs.SetString(
            BuildKey(),
            JsonUtility.ToJson(
                _profile));

        PlayerPrefs.Save();
    }

    private string BuildKey()
    {
        string safe =
            string.IsNullOrWhiteSpace(
                profileName)
                ? "Default"
                : profileName.Trim();

        return KeyPrefix + safe;
    }

    private void RefreshReferences()
    {
        if (_dualDomain == null)
        {
            _dualDomain =
                FindFirstObjectByType<
                    KiwiDualDomainFaceQuality>(
                    FindObjectsInactive.Include);
        }

        if (_continuity == null)
        {
            _continuity =
                FindFirstObjectByType<
                    KiwiTrackingContinuityState>(
                    FindObjectsInactive.Include);
        }

        if (_faceMotion == null)
        {
            _faceMotion =
                FindFirstObjectByType<
                    KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }
    }

    private void UpdateDiagnostics()
    {
        debugCalibrated = IsCalibrated;
        debugCollecting = IsCollecting;
        debugSamples = _samples;
        debugNeutralEyeOpen = NeutralEyeOpenRatio;
        debugNeutralMouthOpen = NeutralMouthOpenRatio;
        debugSuggestedCloseStart = SuggestedGeometryCloseStart;
        debugSuggestedCloseFull = SuggestedGeometryCloseFull;
        debugCalibrationQuality = CalibrationQuality;
    }
}
