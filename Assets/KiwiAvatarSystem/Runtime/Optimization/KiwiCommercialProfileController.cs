using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// v4.4 commercial user/profile layer.
///
/// Design goals borrowed from high-quality VTuber / facial-mocap software:
/// - actor/profile settings persist separately from raw tracker code;
/// - user-facing sensitivity/range controls are explicit;
/// - one profile selects the existing latency policy rather than adding a new
///   temporal filter;
/// - calibration is one action and is persistent.
///
/// This component never writes avatar transforms directly. It only configures
/// the established Kiwi tracking/presentation owners.
/// </summary>
[DefaultExecutionOrder(-16650)]
[DisallowMultipleComponent]
public sealed class KiwiCommercialProfileController : MonoBehaviour
{
    public enum MotionStyle
    {
        Responsive = 0,
        Balanced = 1,
        Stable = 2
    }

    [System.Serializable]
    private sealed class PersistedProfile
    {
        public int version = 1;
        public int motionStyle = 1;

        public float screenPositionGainX = 1f;
        public float screenPositionGainY = 1f;

        public float positionGainX = 0.55f;
        public float positionGainY = 0.40f;

        public float pitchGain = 1f;
        public float yawGain = 1f;
        public float rollGain = 1f;

        public float maxPitch = 45f;
        public float maxYaw = 60f;
        public float maxRoll = 50f;

        public float eyeResponseMultiplier = 1f;
        public float mouthResponseMultiplier = 1f;
        public float contourResponseMultiplier = 1f;
    }

    private const string RuntimeObjectName =
        "[Kiwi] Commercial Profile";

    private const string KeyPrefix =
        "Kiwi.CommercialProfile.v1.";

    [Header("Profile")]
    public string profileName = "Default";

    public bool loadProfileOnStart = true;
    public bool saveProfileWhenPresetChanges = true;

    public MotionStyle motionStyle =
        MotionStyle.Balanced;

    [Header("Head translation mapping")]
    [Range(0f, 3f)]
    public float screenPositionGainX = 1f;

    [Range(0f, 3f)]
    public float screenPositionGainY = 1f;

    [Range(0f, 3f)]
    public float positionGainX = 0.55f;

    [Range(0f, 3f)]
    public float positionGainY = 0.40f;

    [Header("Head rotation mapping")]
    [Range(0f, 2f)]
    public float pitchGain = 1f;

    [Range(0f, 2f)]
    public float yawGain = 1f;

    [Range(0f, 2f)]
    public float rollGain = 1f;

    [Range(0f, 90f)]
    public float maxPitch = 45f;

    [Range(0f, 90f)]
    public float maxYaw = 60f;

    [Range(0f, 90f)]
    public float maxRoll = 50f;

    [Header("Face-part response")]
    [Range(0.65f, 1.35f)]
    public float eyeResponseMultiplier = 1f;

    [Range(0.65f, 1.35f)]
    public float mouthResponseMultiplier = 1f;

    [Range(0.65f, 1.35f)]
    public float contourResponseMultiplier = 1f;

    [Header("Diagnostics")]
    [SerializeField] private bool debugApplied;
    [SerializeField] private string debugLoadedProfile = "-";
    [SerializeField] private string debugLatencyPolicy = "-";

    private KiwiFaceMotion _faceMotion;
    private KiwiMatureVTuberSupervisor _supervisor;
    private KiwiLatencyBudgetController _latencyBudget;
    private KiwiActorFaceCalibration _actorCalibration;
    private KiwiModelPrimaryFacePartConstraint _surfaceConstraint;

    private double _nextReferenceRefreshRealtime;
    private bool _needsApply = true;

    public string ActiveProfileName =>
        string.IsNullOrWhiteSpace(profileName)
            ? "Default"
            : profileName.Trim();

    public string CurrentStyleName =>
        motionStyle.ToString();

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<
                KiwiCommercialProfileController>(
                    FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);

        host.AddComponent<
            KiwiCommercialProfileController>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        RefreshReferences(true);

        if (loadProfileOnStart)
        {
            LoadNow();
        }
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
        _faceMotion = null;
        _supervisor = null;
        _latencyBudget = null;
        _actorCalibration = null;
        _surfaceConstraint = null;

        _nextReferenceRefreshRealtime = 0.0;
        _needsApply = true;

        RefreshReferences(true);
    }

    private void Update()
    {
        double now =
            Time.realtimeSinceStartupAsDouble;

        if (
            now >=
            _nextReferenceRefreshRealtime
        )
        {
            _nextReferenceRefreshRealtime =
                now + 1.0;

            RefreshReferences(false);
        }

        if (_needsApply)
        {
            ApplyCurrentProfile();
        }
    }

    public void ApplyStyle(
        MotionStyle style)
    {
        motionStyle =
            style;

        switch (style)
        {
            case MotionStyle.Responsive:
                eyeResponseMultiplier = 1.08f;
                mouthResponseMultiplier = 1.08f;
                contourResponseMultiplier = 1.06f;
                break;

            case MotionStyle.Stable:
                eyeResponseMultiplier = 0.92f;
                mouthResponseMultiplier = 0.92f;
                contourResponseMultiplier = 0.90f;
                break;

            default:
                eyeResponseMultiplier = 1f;
                mouthResponseMultiplier = 1f;
                contourResponseMultiplier = 1f;
                break;
        }

        _needsApply = true;

        if (saveProfileWhenPresetChanges)
        {
            SaveNow();
        }
    }

    [ContextMenu("Apply Commercial Profile")]
    public void ApplyCurrentProfile()
    {
        RefreshReferences(false);

        if (_faceMotion != null)
        {
            _faceMotion.screenPositionGainX =
                screenPositionGainX;

            _faceMotion.screenPositionGainY =
                screenPositionGainY;

            _faceMotion.positionGainX =
                positionGainX;

            _faceMotion.positionGainY =
                positionGainY;

            _faceMotion.pitchGain =
                pitchGain;

            _faceMotion.yawGain =
                yawGain;

            _faceMotion.rollGain =
                rollGain;

            _faceMotion.maxPitch =
                maxPitch;

            _faceMotion.maxYaw =
                maxYaw;

            _faceMotion.maxRoll =
                maxRoll;
        }

        if (_supervisor != null)
        {
            _supervisor.userEyeResponseMultiplier =
                eyeResponseMultiplier;

            _supervisor.userMouthResponseMultiplier =
                mouthResponseMultiplier;

            _supervisor.userContourResponseMultiplier =
                contourResponseMultiplier;
        }

        if (_latencyBudget != null)
        {
            switch (motionStyle)
            {
                case MotionStyle.Responsive:
                    _latencyBudget.profile =
                        KiwiLatencyBudgetController
                            .PolicyProfile
                            .UltraLowLatency;
                    break;

                case MotionStyle.Stable:
                    _latencyBudget.profile =
                        KiwiLatencyBudgetController
                            .PolicyProfile
                            .Stable;
                    break;

                default:
                    _latencyBudget.profile =
                        KiwiLatencyBudgetController
                            .PolicyProfile
                            .AdaptiveCommercial;
                    break;
            }

            debugLatencyPolicy =
                _latencyBudget.profile.ToString();
        }

        debugApplied =
            _faceMotion != null &&
            _supervisor != null;

        _needsApply =
            !debugApplied;
    }

    [ContextMenu("Quick Recalibrate Commercial Profile")]
    public void QuickRecalibrate()
    {
        RefreshReferences(false);

        if (_actorCalibration != null)
        {
            _actorCalibration.Recalibrate();
        }

        if (_surfaceConstraint != null)
        {
            _surfaceConstraint.Recalibrate();
        }
    }

    [ContextMenu("Save Commercial Profile")]
    public void SaveNow()
    {
        PersistedProfile data =
            new PersistedProfile
            {
                motionStyle =
                    (int)motionStyle,

                screenPositionGainX =
                    screenPositionGainX,

                screenPositionGainY =
                    screenPositionGainY,

                positionGainX =
                    positionGainX,

                positionGainY =
                    positionGainY,

                pitchGain =
                    pitchGain,

                yawGain =
                    yawGain,

                rollGain =
                    rollGain,

                maxPitch =
                    maxPitch,

                maxYaw =
                    maxYaw,

                maxRoll =
                    maxRoll,

                eyeResponseMultiplier =
                    eyeResponseMultiplier,

                mouthResponseMultiplier =
                    mouthResponseMultiplier,

                contourResponseMultiplier =
                    contourResponseMultiplier
            };

        PlayerPrefs.SetString(
            BuildKey(),
            JsonUtility.ToJson(data));

        PlayerPrefs.Save();

        debugLoadedProfile =
            ActiveProfileName;
    }

    [ContextMenu("Load Commercial Profile")]
    public void LoadNow()
    {
        string key =
            BuildKey();

        if (!PlayerPrefs.HasKey(key))
        {
            debugLoadedProfile =
                "Default";

            _needsApply =
                true;

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
            PersistedProfile data =
                JsonUtility.FromJson<
                    PersistedProfile>(
                    json);

            if (
                data == null ||
                data.version != 1
            )
            {
                return;
            }

            motionStyle =
                (MotionStyle)
                Mathf.Clamp(
                    data.motionStyle,
                    0,
                    2);

            screenPositionGainX =
                data.screenPositionGainX;

            screenPositionGainY =
                data.screenPositionGainY;

            positionGainX =
                data.positionGainX;

            positionGainY =
                data.positionGainY;

            pitchGain =
                data.pitchGain;

            yawGain =
                data.yawGain;

            rollGain =
                data.rollGain;

            maxPitch =
                data.maxPitch;

            maxYaw =
                data.maxYaw;

            maxRoll =
                data.maxRoll;

            eyeResponseMultiplier =
                Mathf.Clamp(
                    data.eyeResponseMultiplier,
                    0.65f,
                    1.35f);

            mouthResponseMultiplier =
                Mathf.Clamp(
                    data.mouthResponseMultiplier,
                    0.65f,
                    1.35f);

            contourResponseMultiplier =
                Mathf.Clamp(
                    data.contourResponseMultiplier,
                    0.65f,
                    1.35f);

            debugLoadedProfile =
                ActiveProfileName;

            _needsApply =
                true;
        }
        catch
        {
            debugLoadedProfile =
                "LoadFailed";
        }
    }

    [ContextMenu("Reset Commercial Profile")]
    public void ResetToDefaults()
    {
        motionStyle =
            MotionStyle.Balanced;

        screenPositionGainX =
            1f;

        screenPositionGainY =
            1f;

        positionGainX =
            0.55f;

        positionGainY =
            0.40f;

        pitchGain =
            1f;

        yawGain =
            1f;

        rollGain =
            1f;

        maxPitch =
            45f;

        maxYaw =
            60f;

        maxRoll =
            50f;

        eyeResponseMultiplier =
            1f;

        mouthResponseMultiplier =
            1f;

        contourResponseMultiplier =
            1f;

        PlayerPrefs.DeleteKey(
            BuildKey());

        PlayerPrefs.Save();

        _needsApply =
            true;
    }

    private string BuildKey()
    {
        return
            KeyPrefix +
            ActiveProfileName;
    }

    private void RefreshReferences(
        bool force)
    {
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
            _supervisor == null
        )
        {
            _supervisor =
                FindFirstObjectByType<
                    KiwiMatureVTuberSupervisor>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _latencyBudget == null
        )
        {
            _latencyBudget =
                FindFirstObjectByType<
                    KiwiLatencyBudgetController>(
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
            _surfaceConstraint == null
        )
        {
            _surfaceConstraint =
                FindFirstObjectByType<
                    KiwiModelPrimaryFacePartConstraint>(
                    FindObjectsInactive.Include);
        }
    }
}
