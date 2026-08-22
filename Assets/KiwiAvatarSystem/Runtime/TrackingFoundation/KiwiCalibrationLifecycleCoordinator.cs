using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Runtime owner for calibration lifecycle events that are not initiated by a
/// user-facing Recalibrate button. In Phase 7, a committed avatar-model change
/// invalidates RootPose, model-specific face-part neutral and attachment fits in
/// one transaction. ActorFace remains performer-specific and is preserved.
/// </summary>
[DefaultExecutionOrder(620)]
[DisallowMultipleComponent]
public sealed class KiwiCalibrationLifecycleCoordinator : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Calibration Lifecycle";

    [Header("Diagnostics")]
    [SerializeField] private int debugCalibrationGeneration;
    [SerializeField] private int debugObservedModelGeneration;
    [SerializeField] private int debugModelRecalibrationCount;
    [SerializeField] private string debugLastReason = "-";
    [SerializeField] private string debugLastScope = "None";

    private KiwiFaceMotion _faceMotion;
    private KiwiModelPrimaryFacePartConstraint _modelConstraint;
    private KiwiFaceAttachmentRecalibration _attachmentRecalibration;
    private int _observedModelGeneration;

    public int ModelRecalibrationCount =>
        debugModelRecalibrationCount;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<
                KiwiCalibrationLifecycleCoordinator>(
                FindObjectsInactive.Include) != null)
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<
            KiwiCalibrationLifecycleCoordinator>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        _observedModelGeneration =
            KiwiRuntimeGenerationContext.ModelGeneration;

        RefreshReferences(true);
        UpdateDiagnostics();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _faceMotion = null;
        _modelConstraint = null;
        _attachmentRecalibration = null;

        // Preserve the previously observed model generation across scene-load
        // callbacks. If a committed model change happened during the transition,
        // LateUpdate must still see that delta and start one calibration transaction.
        RefreshReferences(true);
    }

    private void LateUpdate()
    {
        RefreshReferences(false);

        int currentModelGeneration =
            KiwiRuntimeGenerationContext.ModelGeneration;

        if (
            currentModelGeneration !=
            _observedModelGeneration)
        {
            _observedModelGeneration =
                currentModelGeneration;

            RecalibrateForCommittedModelChange();
        }

        UpdateDiagnostics();
    }

    private void RecalibrateForCommittedModelChange()
    {
        const KiwiCalibrationScope scope =
            KiwiCalibrationScope.RootPose |
            KiwiCalibrationScope.ModelFaceParts |
            KiwiCalibrationScope.Attachments;

        using (
            KiwiCalibrationGeneration.BeginTransaction(
                scope,
                "ModelSwitch"))
        {
            // 3D model / Head remains the only rigid pose authority. This only
            // resets its neutral solve; FacePart never writes Root.
            if (_faceMotion != null)
            {
                _faceMotion.BeginCalibration();
            }

            // This profile is model-primary and its PlayerPrefs key is not model
            // specific, so carrying it across a model identity would mix old
            // attachment geometry into the new avatar.
            if (_modelConstraint != null)
            {
                _modelConstraint.Recalibrate();
            }

            if (_attachmentRecalibration != null)
            {
                _attachmentRecalibration.
                    RequestLifecycleRecalibration(
                        "ModelSwitch",
                        true);
            }
        }

        debugModelRecalibrationCount++;
    }

    private void RefreshReferences(bool force)
    {
        if (force || _faceMotion == null)
        {
            _faceMotion =
                FindFirstObjectByType<KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }

        if (force || _modelConstraint == null)
        {
            _modelConstraint =
                FindFirstObjectByType<
                    KiwiModelPrimaryFacePartConstraint>(
                    FindObjectsInactive.Include);
        }

        if (force || _attachmentRecalibration == null)
        {
            _attachmentRecalibration =
                FindFirstObjectByType<
                    KiwiFaceAttachmentRecalibration>(
                    FindObjectsInactive.Include);
        }
    }

    private void UpdateDiagnostics()
    {
        debugCalibrationGeneration =
            KiwiRuntimeGenerationContext.CalibrationGeneration;

        debugObservedModelGeneration =
            _observedModelGeneration;

        debugLastReason =
            KiwiCalibrationGeneration.LastReason;

        debugLastScope =
            KiwiCalibrationGeneration.LastScope.ToString();
    }
}
