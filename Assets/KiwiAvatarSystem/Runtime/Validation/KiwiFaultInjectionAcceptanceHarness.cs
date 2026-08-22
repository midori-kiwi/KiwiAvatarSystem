using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v5.1 Phase 12 fault-injection / acceptance matrix.
///
/// The harness has two deliberately separate layers:
/// 1) a deterministic shadow-model matrix that is safe in Edit Mode and never
///    touches runtime state;
/// 2) explicit Play Mode scenarios. Safe scenarios use existing public owner
///    boundaries, while hardware/model lifecycle scenarios are armed and then
///    observed while the operator performs the real action.
///
/// Nothing runs automatically. Normal tracking/presentation is unchanged.
/// </summary>
[DefaultExecutionOrder(35500)]
[DisallowMultipleComponent]
public sealed class KiwiFaultInjectionAcceptanceHarness : MonoBehaviour
{
    public const string HarnessVersion = "5.1.0-phase12";

    public enum AcceptanceScenario
    {
        None = 0,
        CameraRestart = 1,
        ProviderSwitch = 2,
        SameProviderResume = 3,
        InferenceStall = 4,
        ModelHotSwap = 5,
        MaskFailure = 6,
        AttachmentReacquire = 7,
        TrackingLossReacquire = 8
    }

    public enum InjectionMode
    {
        ManualPhysical = 0,
        SafeSyntheticIntegration = 1,
        SafeContractPulse = 2,
        SafeExistingLifecycleRequest = 3
    }

    public enum AcceptanceState
    {
        Disabled = 0,
        Idle = 1,
        Armed = 2,
        Running = 3,
        Passed = 4,
        Failed = 5,
        Cancelled = 6
    }

    [Serializable]
    public struct AcceptanceRecord
    {
        public string scenario;
        public string state;
        public string injectionMode;
        public int startFrame;
        public int endFrame;
        public double durationSeconds;
        public string message;
        public string generationDelta;
        public long globalRecoveryDelta;
        public long semanticRecoveryDelta;
        public int cameraEventDelta;
        public int cameraRestartDelta;
        public int validationErrorDelta;
        public int validationCriticalDelta;
        public string observedGlobalReasons;
        public string observedSemanticReasons;
        public string observedSemanticComponents;
        public bool providerChangedObserved;
        public string baselineProvider;
        public string finalProvider;
    }

    [Serializable]
    private struct AcceptanceExport
    {
        public string version;
        public string state;
        public string activeScenario;
        public int passed;
        public int failed;
        public string lastScenario;
        public string lastMessage;
        public string deterministicMatrix;
        public AcceptanceRecord[] recent;
    }

    private struct Snapshot
    {
        public int camera;
        public int provider;
        public int model;
        public int config;
        public int calibration;
        public int session;
        public long globalRecovery;
        public long semanticRecovery;
        public int cameraEvent;
        public int cameraRestart;
        public int validationErrors;
        public int validationCriticals;
        public string providerId;
    }

    private struct Delta
    {
        public int camera;
        public int provider;
        public int model;
        public int config;
        public int calibration;
        public int session;
        public long globalRecovery;
        public long semanticRecovery;
        public int cameraEvent;
        public int cameraRestart;
        public int validationErrors;
        public int validationCriticals;
    }

    private struct ScenarioDefinition
    {
        public AcceptanceScenario scenario;
        public InjectionMode injectionMode;
        public float timeoutSeconds;

        public int cameraMin;
        public int cameraMax;
        public int providerMin;
        public int providerMax;
        public int modelMin;
        public int modelMax;
        public int configMin;
        public int configMax;
        public int calibrationMin;
        public int calibrationMax;
        public int sessionMin;
        public int sessionMax;
        public long globalMin;
        public long globalMax;
        public long semanticMin;
        public long semanticMax;
        public int cameraEventMin;
        public int cameraRestartMin;

        public KiwiRecoveryDomainCoordinator.GlobalRecoveryReason
            requiredGlobalReasons;
        public KiwiRecoveryDomainCoordinator.SemanticRecoveryReason
            requiredSemanticReasons;
        public KiwiRecoveryDomainCoordinator.SemanticComponent
            requiredSemanticComponents;

        public bool requireProviderChangedObserved;
        public bool requireFinalProviderEqualsBaseline;
    }

    private const string RuntimeObjectName =
        "[Kiwi] Fault Injection Acceptance Harness";
    private const string SyntheticProviderId =
        "Kiwi.Phase12.SyntheticProvider";
    private const string InjectionSourcePrefix =
        "Phase12FaultInjection";
    private const int RecordCapacity = 32;

    private static KiwiFaultInjectionAcceptanceHarness _instance;

    [Header("Acceptance execution")]
    [Tooltip("No scenario runs automatically. This only enables explicit menu/API tests in Editor or Development builds.")]
    public bool enableAcceptanceTests = true;

    [Range(1, 8)]
    public int successConfirmationFrames = 2;

    [Range(3f, 60f)]
    public float manualScenarioTimeoutSeconds = 15f;

    [Range(1f, 8f)]
    public float syntheticProviderTimeoutSeconds = 4f;

    [Header("Diagnostics")]
    [SerializeField] private AcceptanceState debugState =
        AcceptanceState.Idle;
    [SerializeField] private AcceptanceScenario debugActiveScenario =
        AcceptanceScenario.None;
    [SerializeField] private string debugLastScenario = "None";
    [SerializeField] private string debugLastMessage = "-";
    [SerializeField] private int debugPassedCount;
    [SerializeField] private int debugFailedCount;
    [SerializeField] private string debugDeterministicMatrix = "NotRun";

    private readonly AcceptanceRecord[] _records =
        new AcceptanceRecord[RecordCapacity];
    private int _recordWriteIndex;
    private int _recordCount;

    private Snapshot _baseline;
    private ScenarioDefinition _definition;
    private double _scenarioStartedRealtime;
    private int _scenarioStartedFrame;
    private int _successStreak;
    private bool _injectionInProgress;
    private string _injectionFailure = string.Empty;

    private long _lastObservedGlobalSequence;
    private long _lastObservedSemanticSequence;
    private int _lastObservedCameraEventCount;
    private KiwiRecoveryDomainCoordinator.GlobalRecoveryReason
        _observedGlobalReasons;
    private KiwiRecoveryDomainCoordinator.SemanticRecoveryReason
        _observedSemanticReasons;
    private KiwiRecoveryDomainCoordinator.SemanticComponent
        _observedSemanticComponents;
    private bool _providerChangedObserved;
    private KiwiCameraGenerationReason _observedCameraReason;

    private KiwiTrackingProviderHub _hub;
    private KiwiRecoveryDomainCoordinator _recovery;
    private KiwiFaceAttachmentRecalibration _attachment;

    public static bool HasRuntimeInstance => _instance != null;
    public static AcceptanceState State =>
        _instance != null ? _instance.debugState : AcceptanceState.Disabled;
    public static AcceptanceScenario ActiveScenario =>
        _instance != null ? _instance.debugActiveScenario : AcceptanceScenario.None;
    public static int PassedCount =>
        _instance != null ? _instance.debugPassedCount : 0;
    public static int FailedCount =>
        _instance != null ? _instance.debugFailedCount : 0;
    public static string LastScenario =>
        _instance != null ? _instance.debugLastScenario : "None";
    public static string LastMessage =>
        _instance != null ? _instance.debugLastMessage : "-";
    public static string DeterministicMatrixStatus =>
        _instance != null ? _instance.debugDeterministicMatrix : "NotRun";

    public static int RequiredScenarioCount => 8;

    /// <summary>
    /// Phase 15 read-only RC evidence API. Returns the current Play-session
    /// acceptance history in chronological order without mutating tracking or
    /// test state.
    /// </summary>
    public static bool TryGetHistorySnapshot(out AcceptanceRecord[] records)
    {
        if (_instance == null)
        {
            records = Array.Empty<AcceptanceRecord>();
            return false;
        }

        records = _instance.GetRecentRecords();
        return true;
    }

    /// <summary>
    /// Requires one PASS for each of the eight distinct Phase 12 acceptance
    /// scenarios. Repeating one scenario never substitutes for missing coverage.
    /// </summary>
    public static bool TryGetRequiredScenarioCoverage(
        out int uniquePassed,
        out string missingScenarios)
    {
        uniquePassed = 0;
        missingScenarios = string.Empty;

        if (_instance == null)
        {
            missingScenarios = "Acceptance harness is not active.";
            return false;
        }

        AcceptanceScenario[] required =
        {
            AcceptanceScenario.CameraRestart,
            AcceptanceScenario.ProviderSwitch,
            AcceptanceScenario.SameProviderResume,
            AcceptanceScenario.InferenceStall,
            AcceptanceScenario.ModelHotSwap,
            AcceptanceScenario.MaskFailure,
            AcceptanceScenario.AttachmentReacquire,
            AcceptanceScenario.TrackingLossReacquire
        };

        AcceptanceRecord[] history = _instance.GetRecentRecords();
        List<string> missing = new List<string>();

        for (int i = 0; i < required.Length; i++)
        {
            string scenarioName = required[i].ToString();
            bool passed = false;

            for (int j = 0; j < history.Length; j++)
            {
                if (
                    string.Equals(history[j].scenario, scenarioName, StringComparison.Ordinal) &&
                    string.Equals(history[j].state, AcceptanceState.Passed.ToString(), StringComparison.Ordinal)
                )
                {
                    passed = true;
                    break;
                }
            }

            if (passed)
            {
                uniquePassed++;
            }
            else
            {
                missing.Add(scenarioName);
            }
        }

        missingScenarios = missing.Count == 0
            ? string.Empty
            : string.Join(", ", missing);

        return uniquePassed == required.Length;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        _instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (!Application.isEditor && !Debug.isDebugBuild)
        {
            return;
        }

        if (
            FindFirstObjectByType<KiwiFaultInjectionAcceptanceHarness>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiFaultInjectionAcceptanceHarness>();
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        RefreshReferences(true);
        debugState = AcceptanceState.Idle;
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }

        if (_hub != null)
        {
            _hub.RemoveExternalProvider(SyntheticProviderId);
        }

        KiwiRecoveryDomainCoordinator.CompleteGlobalRecovery(
            InjectionSourcePrefix + "/Inference",
            true);
        KiwiRecoveryDomainCoordinator.CompleteSemanticRecovery(
            KiwiRecoveryDomainCoordinator.SemanticComponent.AllFaceParts |
            KiwiRecoveryDomainCoordinator.SemanticComponent.Mask,
            InjectionSourcePrefix + "/Mask");
    }

    private void Update()
    {
        if (!enableAcceptanceTests || debugActiveScenario == AcceptanceScenario.None)
        {
            return;
        }

        RefreshReferences(false);
        ObserveScenarioSignals();

        if (_injectionInProgress)
        {
            return;
        }

        ScenarioDefinition definition = _definition;
        if (definition.injectionMode != InjectionMode.ManualPhysical)
        {
            return;
        }

        string message;
        bool satisfied = EvaluateCurrentScenario(false, out message);

        if (satisfied)
        {
            _successStreak++;
            if (_successStreak >= Mathf.Max(1, successConfirmationFrames))
            {
                FinishScenario(true, message);
            }
            return;
        }

        _successStreak = 0;

        if (
            Time.realtimeSinceStartupAsDouble - _scenarioStartedRealtime >=
            Mathf.Max(1f, definition.timeoutSeconds)
        )
        {
            FinishScenario(false, message);
        }
    }

    public bool ArmScenario(AcceptanceScenario scenario)
    {
        if (!CanStartScenario(scenario, out string reason))
        {
            debugLastMessage = reason;
            return false;
        }

        _definition = GetDefinition(scenario);
        if (_definition.injectionMode == InjectionMode.ManualPhysical)
        {
            _definition.timeoutSeconds =
                Mathf.Max(1f, manualScenarioTimeoutSeconds);
        }

        _baseline = CaptureSnapshot();
        _scenarioStartedRealtime = Time.realtimeSinceStartupAsDouble;
        _scenarioStartedFrame = Time.frameCount;
        _successStreak = 0;
        _injectionInProgress = false;
        _injectionFailure = string.Empty;
        _observedGlobalReasons =
            KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.None;
        _observedSemanticReasons =
            KiwiRecoveryDomainCoordinator.SemanticRecoveryReason.None;
        _observedSemanticComponents =
            KiwiRecoveryDomainCoordinator.SemanticComponent.None;
        _providerChangedObserved = false;
        _observedCameraReason = KiwiCameraGenerationReason.None;
        _lastObservedGlobalSequence = _baseline.globalRecovery;
        _lastObservedSemanticSequence = _baseline.semanticRecovery;
        _lastObservedCameraEventCount = KiwiCameraGeneration.EventCount;

        debugActiveScenario = scenario;
        debugState = _definition.injectionMode == InjectionMode.ManualPhysical
            ? AcceptanceState.Armed
            : AcceptanceState.Running;
        debugLastMessage = _definition.injectionMode == InjectionMode.ManualPhysical
            ? "Armed. Perform the real lifecycle action now."
            : "Running safe fault injection.";

        return true;
    }

    public bool RunSafeInjection(AcceptanceScenario scenario)
    {
        ScenarioDefinition definition = GetDefinition(scenario);
        if (definition.injectionMode == InjectionMode.ManualPhysical)
        {
            debugLastMessage =
                scenario + " requires a real/manual lifecycle action; arm it instead.";
            return false;
        }

        if (!ArmScenario(scenario))
        {
            return false;
        }

        _injectionInProgress = true;
        StartCoroutine(RunSafeInjectionCoroutine(scenario));
        return true;
    }

    public void CancelActiveScenario()
    {
        if (debugActiveScenario == AcceptanceScenario.None)
        {
            return;
        }

        StopAllCoroutines();
        CleanupInjectionState();
        RecordResult(false, AcceptanceState.Cancelled, "Scenario cancelled.");
        debugState = AcceptanceState.Cancelled;
        debugActiveScenario = AcceptanceScenario.None;
        _injectionInProgress = false;
    }

    private IEnumerator RunSafeInjectionCoroutine(AcceptanceScenario scenario)
    {
        switch (scenario)
        {
            case AcceptanceScenario.ProviderSwitch:
                yield return RunSyntheticProviderSwitch();
                break;

            case AcceptanceScenario.InferenceStall:
                yield return RunInferenceRecoveryPulse();
                break;

            case AcceptanceScenario.MaskFailure:
                yield return RunMaskRecoveryPulse();
                break;

            case AcceptanceScenario.AttachmentReacquire:
                yield return RunAttachmentReacquire();
                break;

            default:
                debugLastMessage = "No safe injector exists for " + scenario + ".";
                break;
        }

        _injectionInProgress = false;
        ObserveScenarioSignals();

        string message;
        bool passed = EvaluateCurrentScenario(true, out message);
        FinishScenario(passed, message);
    }

    private IEnumerator RunSyntheticProviderSwitch()
    {
        RefreshReferences(true);
        if (_hub == null)
        {
            _injectionFailure = "Provider Hub is unavailable.";
            debugLastMessage = _injectionFailure;
            yield break;
        }

        double deadline = Time.realtimeSinceStartupAsDouble +
            Mathf.Max(1f, syntheticProviderTimeoutSeconds);
        ulong sequence = 1UL;
        bool selected = false;

        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            if (KiwiCanonicalTrackingFrame.TryGetRigidFrame(out FacePrecisionTrackingData data))
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                data.isValid = true;
                data.frameId = 0xF120000000000000UL + sequence++;
                data.submissionHostTicks = now;
                data.arrivalHostTicks = now;
                data.hasMatchedSubmissionTiming = true;

                KiwiExternalTrackingFrameContract contract =
                    new KiwiExternalTrackingFrameContract
                    {
                        timebase = KiwiTrackingTimebase.HostStopwatchTicks,
                        sourceTimestamp = now,
                        arrivalHostTicks = now,
                        horizontalConvention =
                            KiwiTrackingHorizontalConvention.CanonicalPresentation
                    };

                _hub.SubmitExternalFrame(
                    SyntheticProviderId,
                    100000,
                    KiwiTrackingProviderHub.TrackingCapability.HeadPose |
                    KiwiTrackingProviderHub.TrackingCapability.FaceGeometry,
                    data,
                    contract);
                _hub.RefreshCanonicalSelectionNow();
            }

            ObserveScenarioSignals();

            if (string.Equals(_hub.ActiveProviderId, SyntheticProviderId, StringComparison.Ordinal))
            {
                selected = true;
                break;
            }

            yield return null;
        }

        if (selected)
        {
            for (int i = 0; i < 3; i++)
            {
                yield return null;
                ObserveScenarioSignals();
            }
        }

        _hub.RemoveExternalProvider(SyntheticProviderId);
        _hub.RefreshCanonicalSelectionNow();

        for (int i = 0; i < 4; i++)
        {
            yield return null;
            ObserveScenarioSignals();
        }

        if (!selected)
        {
            _injectionFailure =
                "Synthetic provider never became rigid authority before timeout.";
            debugLastMessage = _injectionFailure;
        }
    }

    private IEnumerator RunInferenceRecoveryPulse()
    {
        string source = InjectionSourcePrefix + "/Inference";

        KiwiRecoveryDomainCoordinator.BeginGlobalRecovery(
            source,
            KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.InferencePipelineStalled |
            KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.InferenceRestart);

        for (int i = 0; i < 3; i++)
        {
            yield return null;
            ObserveScenarioSignals();
        }

        KiwiRecoveryDomainCoordinator.CompleteGlobalRecovery(source, true);

        for (int i = 0; i < 2; i++)
        {
            yield return null;
            ObserveScenarioSignals();
        }
    }

    private IEnumerator RunMaskRecoveryPulse()
    {
        string source = InjectionSourcePrefix + "/Mask";
        KiwiRecoveryDomainCoordinator.SemanticComponent components =
            KiwiRecoveryDomainCoordinator.SemanticComponent.AllFaceParts |
            KiwiRecoveryDomainCoordinator.SemanticComponent.Mask;

        KiwiRecoveryDomainCoordinator.BeginSemanticRecovery(
            components,
            KiwiRecoveryDomainCoordinator.SemanticRecoveryReason.MaskInvalid,
            source);

        for (int i = 0; i < 3; i++)
        {
            yield return null;
            ObserveScenarioSignals();
        }

        KiwiRecoveryDomainCoordinator.CompleteSemanticRecovery(
            components,
            source);

        for (int i = 0; i < 2; i++)
        {
            yield return null;
            ObserveScenarioSignals();
        }
    }

    private IEnumerator RunAttachmentReacquire()
    {
        RefreshReferences(true);
        if (_attachment == null)
        {
            _injectionFailure =
                "Face Attachment Recalibration component is unavailable.";
            debugLastMessage = _injectionFailure;
            yield break;
        }

        _attachment.RequestLifecycleRecalibration(
            InjectionSourcePrefix + "/Attachment",
            true);

        double deadline = Time.realtimeSinceStartupAsDouble +
            Mathf.Max(1f, _definition.timeoutSeconds);

        do
        {
            yield return null;
            ObserveScenarioSignals();
        }
        while (
            _attachment.IsPending &&
            Time.realtimeSinceStartupAsDouble < deadline);

        if (_attachment.IsPending)
        {
            _injectionFailure =
                "Attachment reacquire remained pending until the acceptance timeout.";
            debugLastMessage = _injectionFailure;
        }
    }

    private void CleanupInjectionState()
    {
        if (_hub != null)
        {
            _hub.RemoveExternalProvider(SyntheticProviderId);
        }

        KiwiRecoveryDomainCoordinator.CompleteGlobalRecovery(
            InjectionSourcePrefix + "/Inference",
            true);
        KiwiRecoveryDomainCoordinator.CompleteSemanticRecovery(
            KiwiRecoveryDomainCoordinator.SemanticComponent.AllFaceParts |
            KiwiRecoveryDomainCoordinator.SemanticComponent.Mask,
            InjectionSourcePrefix + "/Mask");
    }

    private bool CanStartScenario(
        AcceptanceScenario scenario,
        out string reason)
    {
        if (!enableAcceptanceTests)
        {
            reason = "Acceptance tests are disabled on the harness.";
            return false;
        }

        if (!Application.isEditor && !Debug.isDebugBuild)
        {
            reason = "Fault injection is available only in Editor or Development builds.";
            return false;
        }

        if (scenario == AcceptanceScenario.None)
        {
            reason = "Select a concrete acceptance scenario.";
            return false;
        }

        if (debugActiveScenario != AcceptanceScenario.None)
        {
            reason = "Another acceptance scenario is already active.";
            return false;
        }

        if (!KiwiRuntimeValidationHarness.HasRuntimeInstance)
        {
            reason = "Phase 11 Runtime Validation Harness is not active.";
            return false;
        }

        RefreshReferences(true);

        if (
            scenario == AcceptanceScenario.ProviderSwitch &&
            !KiwiCanonicalTrackingFrame.HasRigidFrame
        )
        {
            reason = "Provider switch injection requires a current canonical rigid frame.";
            return false;
        }

        if (
            (
                scenario == AcceptanceScenario.SameProviderResume ||
                scenario == AcceptanceScenario.TrackingLossReacquire
            ) &&
            string.IsNullOrEmpty(KiwiCanonicalTrackingFrame.ProviderId)
        )
        {
            reason = "This scenario requires an active baseline rigid provider before arming.";
            return false;
        }

        if (
            scenario == AcceptanceScenario.AttachmentReacquire &&
            _attachment == null
        )
        {
            reason = "Face Attachment Recalibration component is unavailable.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void ObserveScenarioSignals()
    {
        if (debugActiveScenario == AcceptanceScenario.None)
        {
            return;
        }

        long globalSequence =
            KiwiRecoveryDomainCoordinator.GlobalRecoverySequence;
        if (globalSequence != _lastObservedGlobalSequence)
        {
            _lastObservedGlobalSequence = globalSequence;
            _observedGlobalReasons |=
                KiwiRecoveryDomainCoordinator.LastGlobalReason;
        }

        long semanticSequence =
            KiwiRecoveryDomainCoordinator.SemanticRecoverySequence;
        if (semanticSequence != _lastObservedSemanticSequence)
        {
            _lastObservedSemanticSequence = semanticSequence;
            _observedSemanticReasons |=
                KiwiRecoveryDomainCoordinator.LastSemanticReason;
        }

        _observedSemanticReasons |=
            KiwiRecoveryDomainCoordinator.ActiveSemanticReasons;

        if (_recovery != null)
        {
            _observedSemanticComponents |=
                _recovery.ActiveSemanticComponents;
        }

        int cameraEvents = KiwiCameraGeneration.EventCount;
        if (cameraEvents != _lastObservedCameraEventCount)
        {
            _lastObservedCameraEventCount = cameraEvents;
            _observedCameraReason = KiwiCameraGeneration.LastReason;
        }

        string currentProvider = KiwiCanonicalTrackingFrame.ProviderId;
        if (
            !string.IsNullOrEmpty(currentProvider) &&
            !string.Equals(
                currentProvider,
                _baseline.providerId,
                StringComparison.Ordinal)
        )
        {
            _providerChangedObserved = true;
        }
    }

    private bool EvaluateCurrentScenario(
        bool finalEvaluation,
        out string message)
    {
        Snapshot current = CaptureSnapshot();
        Delta delta = CalculateDelta(_baseline, current);
        ScenarioDefinition d = _definition;
        List<string> failures = new List<string>(8);

        CheckRange("CameraGeneration", delta.camera, d.cameraMin, d.cameraMax, failures);
        CheckRange("ProviderGeneration", delta.provider, d.providerMin, d.providerMax, failures);
        CheckRange("ModelGeneration", delta.model, d.modelMin, d.modelMax, failures);
        CheckRange("ConfigEpoch", delta.config, d.configMin, d.configMax, failures);
        CheckRange("CalibrationGeneration", delta.calibration, d.calibrationMin, d.calibrationMax, failures);
        CheckRange("TrackingSessionGeneration", delta.session, d.sessionMin, d.sessionMax, failures);
        CheckRange("GlobalRecoverySequence", delta.globalRecovery, d.globalMin, d.globalMax, failures);
        CheckRange("SemanticRecoverySequence", delta.semanticRecovery, d.semanticMin, d.semanticMax, failures);

        if (delta.cameraEvent < d.cameraEventMin)
        {
            failures.Add(
                "Camera source event delta " + delta.cameraEvent +
                " < required " + d.cameraEventMin + ".");
        }

        if (delta.cameraRestart < d.cameraRestartMin)
        {
            failures.Add(
                "Camera restart count delta " + delta.cameraRestart +
                " < required " + d.cameraRestartMin + ".");
        }

        if (
            d.requiredGlobalReasons !=
                KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.None &&
            (_observedGlobalReasons & d.requiredGlobalReasons) !=
                d.requiredGlobalReasons
        )
        {
            failures.Add(
                "Missing global reason(s): " +
                (d.requiredGlobalReasons & ~_observedGlobalReasons) + ".");
        }

        if (
            d.requiredSemanticReasons !=
                KiwiRecoveryDomainCoordinator.SemanticRecoveryReason.None &&
            (_observedSemanticReasons & d.requiredSemanticReasons) !=
                d.requiredSemanticReasons
        )
        {
            failures.Add(
                "Missing semantic reason(s): " +
                (d.requiredSemanticReasons & ~_observedSemanticReasons) + ".");
        }

        if (
            d.requiredSemanticComponents !=
                KiwiRecoveryDomainCoordinator.SemanticComponent.None &&
            (_observedSemanticComponents & d.requiredSemanticComponents) !=
                d.requiredSemanticComponents
        )
        {
            failures.Add(
                "Missing semantic component(s): " +
                (d.requiredSemanticComponents & ~_observedSemanticComponents) + ".");
        }

        if (d.requireProviderChangedObserved && !_providerChangedObserved)
        {
            failures.Add("Provider authority never changed during the scenario.");
        }

        if (
            d.requireFinalProviderEqualsBaseline &&
            !string.Equals(
                current.providerId,
                _baseline.providerId,
                StringComparison.Ordinal)
        )
        {
            failures.Add(
                "Final provider differs from baseline: " +
                _baseline.providerId + " -> " + current.providerId + ".");
        }

        if (delta.validationErrors > 0 || delta.validationCriticals > 0)
        {
            failures.Add(
                "Runtime validation produced new Error/Critical records: " +
                delta.validationErrors + "/" + delta.validationCriticals + ".");
        }

        if (!string.IsNullOrEmpty(_injectionFailure))
        {
            failures.Add(_injectionFailure);
        }

        if (failures.Count == 0)
        {
            message =
                "PASS " + d.scenario + " | " + FormatDelta(delta) +
                " | global=" + _observedGlobalReasons +
                " semantic=" + _observedSemanticReasons +
                " components=" + _observedSemanticComponents + ".";
            return true;
        }

        message =
            (finalEvaluation ? "FAIL " : "Waiting ") +
            d.scenario + ": " + string.Join(" ", failures);
        return false;
    }

    private void FinishScenario(bool passed, string message)
    {
        CleanupInjectionState();
        RecordResult(
            passed,
            passed ? AcceptanceState.Passed : AcceptanceState.Failed,
            message);

        if (passed)
        {
            debugPassedCount++;
            debugState = AcceptanceState.Passed;
        }
        else
        {
            debugFailedCount++;
            debugState = AcceptanceState.Failed;
        }

        debugLastScenario = debugActiveScenario.ToString();
        debugLastMessage = message;
        debugActiveScenario = AcceptanceScenario.None;
        _injectionInProgress = false;
        _successStreak = 0;
    }

    private void RecordResult(
        bool passed,
        AcceptanceState state,
        string message)
    {
        Snapshot current = CaptureSnapshot();
        Delta delta = CalculateDelta(_baseline, current);

        AcceptanceRecord record = new AcceptanceRecord
        {
            scenario = _definition.scenario.ToString(),
            state = state.ToString(),
            injectionMode = _definition.injectionMode.ToString(),
            startFrame = _scenarioStartedFrame,
            endFrame = Time.frameCount,
            durationSeconds = Math.Max(
                0.0,
                Time.realtimeSinceStartupAsDouble - _scenarioStartedRealtime),
            message = message,
            generationDelta = FormatGenerationDelta(delta),
            globalRecoveryDelta = delta.globalRecovery,
            semanticRecoveryDelta = delta.semanticRecovery,
            cameraEventDelta = delta.cameraEvent,
            cameraRestartDelta = delta.cameraRestart,
            validationErrorDelta = delta.validationErrors,
            validationCriticalDelta = delta.validationCriticals,
            observedGlobalReasons = _observedGlobalReasons.ToString(),
            observedSemanticReasons = _observedSemanticReasons.ToString(),
            observedSemanticComponents = _observedSemanticComponents.ToString(),
            providerChangedObserved = _providerChangedObserved,
            baselineProvider = _baseline.providerId ?? string.Empty,
            finalProvider = current.providerId ?? string.Empty
        };

        _records[_recordWriteIndex] = record;
        _recordWriteIndex = (_recordWriteIndex + 1) % RecordCapacity;
        _recordCount = Mathf.Min(_recordCount + 1, RecordCapacity);
    }

    public static bool RunDeterministicMatrix(out string report)
    {
        List<string> failures = new List<string>();
        AcceptanceScenario[] scenarios =
        {
            AcceptanceScenario.CameraRestart,
            AcceptanceScenario.ProviderSwitch,
            AcceptanceScenario.SameProviderResume,
            AcceptanceScenario.InferenceStall,
            AcceptanceScenario.ModelHotSwap,
            AcceptanceScenario.MaskFailure,
            AcceptanceScenario.AttachmentReacquire,
            AcceptanceScenario.TrackingLossReacquire
        };

        for (int i = 0; i < scenarios.Length; i++)
        {
            AcceptanceScenario scenario = scenarios[i];
            ScenarioDefinition definition = GetDefinition(scenario);
            ShadowObservation observation = BuildExpectedShadowObservation(scenario);

            if (!EvaluateShadow(definition, observation, out string failure))
            {
                failures.Add(scenario + ": " + failure);
            }
        }

        // Negative controls prove that the matrix catches the important domain
        // separation failures rather than merely accepting every transition.
        CheckNegativeControl(
            AcceptanceScenario.ProviderSwitch,
            o => o.delta.provider = 0,
            "provider switch without ProviderGeneration",
            failures);
        CheckNegativeControl(
            AcceptanceScenario.SameProviderResume,
            o => o.delta.provider = 1,
            "same-provider resume incremented ProviderGeneration",
            failures);
        CheckNegativeControl(
            AcceptanceScenario.InferenceStall,
            o => o.delta.semanticRecovery = 1,
            "global inference stall mutated semantic recovery",
            failures);
        CheckNegativeControl(
            AcceptanceScenario.ModelHotSwap,
            o => o.delta.calibration = 0,
            "model switch skipped CalibrationGeneration",
            failures);
        CheckNegativeControl(
            AcceptanceScenario.MaskFailure,
            o => o.delta.globalRecovery = 1,
            "mask failure leaked into global recovery",
            failures);
        CheckNegativeControl(
            AcceptanceScenario.AttachmentReacquire,
            o => o.delta.semanticRecovery = 0,
            "attachment reacquire skipped semantic recovery",
            failures);
        CheckNegativeControl(
            AcceptanceScenario.CameraRestart,
            o => o.delta.model = 1,
            "camera restart mutated ModelGeneration",
            failures);
        CheckNegativeControl(
            AcceptanceScenario.TrackingLossReacquire,
            o => o.delta.provider = 1,
            "tracking reacquire changed provider identity",
            failures);

        bool passed = failures.Count == 0;
        report = passed
            ? "PASS: 8 positive scenarios + 8 negative controls."
            : "FAIL: " + string.Join(" | ", failures);

        if (_instance != null)
        {
            _instance.debugDeterministicMatrix = passed ? "PASS" : "FAIL";
            _instance.debugLastMessage = report;
        }

        return passed;
    }

    private sealed class ShadowObservation
    {
        public Delta delta;
        public KiwiRecoveryDomainCoordinator.GlobalRecoveryReason globalReasons;
        public KiwiRecoveryDomainCoordinator.SemanticRecoveryReason semanticReasons;
        public KiwiRecoveryDomainCoordinator.SemanticComponent semanticComponents;
        public bool providerChanged;
        public bool finalProviderMatchesBaseline = true;
    }

    private static ShadowObservation BuildExpectedShadowObservation(
        AcceptanceScenario scenario)
    {
        ShadowObservation o = new ShadowObservation();

        switch (scenario)
        {
            case AcceptanceScenario.CameraRestart:
                o.delta.camera = 1;
                o.delta.globalRecovery = 1;
                o.delta.cameraEvent = 1;
                o.globalReasons =
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.CameraSessionChanged;
                break;

            case AcceptanceScenario.ProviderSwitch:
                o.delta.provider = 1;
                o.delta.globalRecovery = 1;
                o.globalReasons =
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.ProviderSwitch;
                o.providerChanged = true;
                break;

            case AcceptanceScenario.SameProviderResume:
                o.delta.globalRecovery = 1;
                o.globalReasons =
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.TrackingReacquire;
                break;

            case AcceptanceScenario.InferenceStall:
                o.delta.globalRecovery = 1;
                o.globalReasons =
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.InferencePipelineStalled |
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.InferenceRestart;
                break;

            case AcceptanceScenario.ModelHotSwap:
                o.delta.model = 1;
                o.delta.calibration = 1;
                o.delta.semanticRecovery = 1;
                o.semanticReasons =
                    KiwiRecoveryDomainCoordinator.SemanticRecoveryReason.AttachmentRebind;
                o.semanticComponents =
                    KiwiRecoveryDomainCoordinator.SemanticComponent.Attachment;
                break;

            case AcceptanceScenario.MaskFailure:
                o.delta.semanticRecovery = 1;
                o.semanticReasons =
                    KiwiRecoveryDomainCoordinator.SemanticRecoveryReason.MaskInvalid;
                o.semanticComponents =
                    KiwiRecoveryDomainCoordinator.SemanticComponent.Mask;
                break;

            case AcceptanceScenario.AttachmentReacquire:
                o.delta.calibration = 1;
                o.delta.semanticRecovery = 1;
                o.semanticReasons =
                    KiwiRecoveryDomainCoordinator.SemanticRecoveryReason.AttachmentRebind;
                o.semanticComponents =
                    KiwiRecoveryDomainCoordinator.SemanticComponent.Attachment;
                break;

            case AcceptanceScenario.TrackingLossReacquire:
                o.delta.globalRecovery = 2;
                o.globalReasons =
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.TrackingLost |
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.TrackingReacquire;
                break;
        }

        return o;
    }

    private static bool EvaluateShadow(
        ScenarioDefinition d,
        ShadowObservation o,
        out string failure)
    {
        List<string> failures = new List<string>();
        CheckRange("camera", o.delta.camera, d.cameraMin, d.cameraMax, failures);
        CheckRange("provider", o.delta.provider, d.providerMin, d.providerMax, failures);
        CheckRange("model", o.delta.model, d.modelMin, d.modelMax, failures);
        CheckRange("config", o.delta.config, d.configMin, d.configMax, failures);
        CheckRange("calibration", o.delta.calibration, d.calibrationMin, d.calibrationMax, failures);
        CheckRange("session", o.delta.session, d.sessionMin, d.sessionMax, failures);
        CheckRange("global", o.delta.globalRecovery, d.globalMin, d.globalMax, failures);
        CheckRange("semantic", o.delta.semanticRecovery, d.semanticMin, d.semanticMax, failures);

        if (o.delta.cameraEvent < d.cameraEventMin)
        {
            failures.Add("camera source event minimum not met");
        }
        if (o.delta.cameraRestart < d.cameraRestartMin)
        {
            failures.Add("camera restart minimum not met");
        }
        if ((o.globalReasons & d.requiredGlobalReasons) != d.requiredGlobalReasons)
        {
            failures.Add("global reason missing");
        }
        if ((o.semanticReasons & d.requiredSemanticReasons) != d.requiredSemanticReasons)
        {
            failures.Add("semantic reason missing");
        }
        if ((o.semanticComponents & d.requiredSemanticComponents) != d.requiredSemanticComponents)
        {
            failures.Add("semantic component missing");
        }
        if (d.requireProviderChangedObserved && !o.providerChanged)
        {
            failures.Add("provider change not observed");
        }
        if (d.requireFinalProviderEqualsBaseline && !o.finalProviderMatchesBaseline)
        {
            failures.Add("final provider mismatch");
        }
        if (o.delta.validationErrors > 0 || o.delta.validationCriticals > 0)
        {
            failures.Add("validator error/critical");
        }

        failure = string.Join(", ", failures);
        return failures.Count == 0;
    }

    private static void CheckNegativeControl(
        AcceptanceScenario scenario,
        Action<ShadowObservation> mutate,
        string name,
        List<string> failures)
    {
        ScenarioDefinition definition = GetDefinition(scenario);
        ShadowObservation observation = BuildExpectedShadowObservation(scenario);
        mutate(observation);

        if (EvaluateShadow(definition, observation, out _))
        {
            failures.Add("negative control not rejected: " + name);
        }
    }

    private static ScenarioDefinition GetDefinition(AcceptanceScenario scenario)
    {
        ScenarioDefinition d = new ScenarioDefinition
        {
            scenario = scenario,
            injectionMode = InjectionMode.ManualPhysical,
            timeoutSeconds = 8f,
            cameraMin = 0,
            cameraMax = 0,
            providerMin = 0,
            providerMax = 0,
            modelMin = 0,
            modelMax = 0,
            configMin = 0,
            configMax = 0,
            calibrationMin = 0,
            calibrationMax = 0,
            sessionMin = 0,
            sessionMax = 0,
            globalMin = 0,
            globalMax = 0,
            semanticMin = 0,
            semanticMax = 0,
            cameraEventMin = 0,
            cameraRestartMin = 0,
            requiredGlobalReasons =
                KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.None,
            requiredSemanticReasons =
                KiwiRecoveryDomainCoordinator.SemanticRecoveryReason.None,
            requiredSemanticComponents =
                KiwiRecoveryDomainCoordinator.SemanticComponent.None,
            requireProviderChangedObserved = false,
            requireFinalProviderEqualsBaseline = false
        };

        switch (scenario)
        {
            case AcceptanceScenario.CameraRestart:
                d.cameraMin = 1;
                d.cameraMax = -1;
                d.calibrationMax = -1;
                d.sessionMax = -1;
                d.globalMin = 1;
                d.globalMax = -1;
                d.semanticMax = -1;
                d.cameraEventMin = 1;
                d.requiredGlobalReasons =
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.CameraSessionChanged;
                break;

            case AcceptanceScenario.ProviderSwitch:
                d.injectionMode = InjectionMode.SafeSyntheticIntegration;
                d.providerMin = 1;
                d.providerMax = -1;
                d.calibrationMax = -1;
                d.globalMin = 1;
                d.globalMax = -1;
                d.semanticMax = -1;
                d.requiredGlobalReasons =
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.ProviderSwitch;
                d.requireProviderChangedObserved = true;
                break;

            case AcceptanceScenario.SameProviderResume:
                d.calibrationMax = -1;
                d.globalMin = 1;
                d.globalMax = -1;
                d.semanticMax = -1;
                d.requiredGlobalReasons =
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.TrackingReacquire;
                d.requireFinalProviderEqualsBaseline = true;
                break;

            case AcceptanceScenario.InferenceStall:
                d.injectionMode = InjectionMode.SafeContractPulse;
                d.globalMin = 1;
                d.globalMax = -1;
                d.requiredGlobalReasons =
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.InferencePipelineStalled |
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.InferenceRestart;
                break;

            case AcceptanceScenario.ModelHotSwap:
                d.modelMin = 1;
                d.modelMax = -1;
                d.calibrationMin = 1;
                d.calibrationMax = -1;
                d.semanticMin = 1;
                d.semanticMax = -1;
                d.requiredSemanticReasons =
                    KiwiRecoveryDomainCoordinator.SemanticRecoveryReason.AttachmentRebind;
                d.requiredSemanticComponents =
                    KiwiRecoveryDomainCoordinator.SemanticComponent.Attachment;
                break;

            case AcceptanceScenario.MaskFailure:
                d.injectionMode = InjectionMode.SafeContractPulse;
                d.semanticMin = 1;
                d.semanticMax = -1;
                d.requiredSemanticReasons =
                    KiwiRecoveryDomainCoordinator.SemanticRecoveryReason.MaskInvalid;
                d.requiredSemanticComponents =
                    KiwiRecoveryDomainCoordinator.SemanticComponent.Mask;
                break;

            case AcceptanceScenario.AttachmentReacquire:
                d.injectionMode = InjectionMode.SafeExistingLifecycleRequest;
                d.calibrationMin = 1;
                d.calibrationMax = -1;
                d.semanticMin = 1;
                d.semanticMax = -1;
                d.requiredSemanticReasons =
                    KiwiRecoveryDomainCoordinator.SemanticRecoveryReason.AttachmentRebind;
                d.requiredSemanticComponents =
                    KiwiRecoveryDomainCoordinator.SemanticComponent.Attachment;
                d.timeoutSeconds = 8f;
                break;

            case AcceptanceScenario.TrackingLossReacquire:
                d.calibrationMax = -1;
                d.globalMin = 2;
                d.globalMax = -1;
                d.semanticMax = -1;
                d.requiredGlobalReasons =
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.TrackingLost |
                    KiwiRecoveryDomainCoordinator.GlobalRecoveryReason.TrackingReacquire;
                d.requireFinalProviderEqualsBaseline = true;
                break;
        }

        return d;
    }

    private static void CheckRange(
        string name,
        long value,
        long minimum,
        long maximum,
        List<string> failures)
    {
        if (value < minimum)
        {
            failures.Add(name + " delta " + value + " < " + minimum + ".");
        }

        if (maximum >= 0 && value > maximum)
        {
            failures.Add(name + " delta " + value + " > " + maximum + ".");
        }
    }

    private static Snapshot CaptureSnapshot()
    {
        KiwiRuntimeGenerationContext.Snapshot g =
            KiwiRuntimeGenerationContext.Capture();

        return new Snapshot
        {
            camera = g.cameraGeneration,
            provider = g.providerGeneration,
            model = g.modelGeneration,
            config = g.configEpoch,
            calibration = g.calibrationGeneration,
            session = g.trackingSessionGeneration,
            globalRecovery = KiwiRecoveryDomainCoordinator.GlobalRecoverySequence,
            semanticRecovery = KiwiRecoveryDomainCoordinator.SemanticRecoverySequence,
            cameraEvent = KiwiCameraGeneration.EventCount,
            cameraRestart = KiwiCameraGeneration.RestartCount,
            validationErrors = KiwiRuntimeValidationHarness.ErrorCount,
            validationCriticals = KiwiRuntimeValidationHarness.CriticalCount,
            providerId = KiwiCanonicalTrackingFrame.ProviderId
        };
    }

    private static Delta CalculateDelta(Snapshot from, Snapshot to)
    {
        return new Delta
        {
            camera = to.camera - from.camera,
            provider = to.provider - from.provider,
            model = to.model - from.model,
            config = to.config - from.config,
            calibration = to.calibration - from.calibration,
            session = to.session - from.session,
            globalRecovery = to.globalRecovery - from.globalRecovery,
            semanticRecovery = to.semanticRecovery - from.semanticRecovery,
            cameraEvent = to.cameraEvent - from.cameraEvent,
            cameraRestart = to.cameraRestart - from.cameraRestart,
            validationErrors = to.validationErrors - from.validationErrors,
            validationCriticals = to.validationCriticals - from.validationCriticals
        };
    }

    private static string FormatGenerationDelta(Delta d)
    {
        return
            "cam/prov/model/cfg/cal/session=" +
            d.camera + "/" + d.provider + "/" + d.model + "/" +
            d.config + "/" + d.calibration + "/" + d.session;
    }

    private static string FormatDelta(Delta d)
    {
        return
            FormatGenerationDelta(d) +
            " global/semantic=" + d.globalRecovery + "/" + d.semanticRecovery +
            " cameraEvent/restart=" + d.cameraEvent + "/" + d.cameraRestart +
            " validationE/C=" + d.validationErrors + "/" + d.validationCriticals;
    }

    private void RefreshReferences(bool force)
    {
        if (force || _hub == null)
        {
            _hub = FindFirstObjectByType<KiwiTrackingProviderHub>(
                FindObjectsInactive.Include);
        }

        if (force || _recovery == null)
        {
            _recovery = FindFirstObjectByType<KiwiRecoveryDomainCoordinator>(
                FindObjectsInactive.Include);
        }

        if (force || _attachment == null)
        {
            _attachment = FindFirstObjectByType<KiwiFaceAttachmentRecalibration>(
                FindObjectsInactive.Include);
        }
    }

    public string BuildJsonReport(bool prettyPrint = true)
    {
        AcceptanceExport report = new AcceptanceExport
        {
            version = HarnessVersion,
            state = debugState.ToString(),
            activeScenario = debugActiveScenario.ToString(),
            passed = debugPassedCount,
            failed = debugFailedCount,
            lastScenario = debugLastScenario,
            lastMessage = debugLastMessage,
            deterministicMatrix = debugDeterministicMatrix,
            recent = GetRecentRecords()
        };

        return JsonUtility.ToJson(report, prettyPrint);
    }

    public string ExportJsonReport()
    {
        string path = Path.Combine(
            Application.persistentDataPath,
            "KiwiPhase12AcceptanceReport.json");
        File.WriteAllText(path, BuildJsonReport(true));
        return path;
    }

    public void ClearHistory()
    {
        Array.Clear(_records, 0, _records.Length);
        _recordWriteIndex = 0;
        _recordCount = 0;
        debugPassedCount = 0;
        debugFailedCount = 0;
        debugLastScenario = "None";
        debugLastMessage = "-";
        debugState = AcceptanceState.Idle;
    }

    private AcceptanceRecord[] GetRecentRecords()
    {
        AcceptanceRecord[] result = new AcceptanceRecord[_recordCount];
        int start = (_recordWriteIndex - _recordCount + RecordCapacity) % RecordCapacity;

        for (int i = 0; i < _recordCount; i++)
        {
            result[i] = _records[(start + i) % RecordCapacity];
        }

        return result;
    }
}
