using System;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v5.1 Phase 11 passive runtime contract validator.
///
/// This component never writes tracking, recovery, presentation, calibration,
/// provider selection or avatar transforms. It only observes already-published
/// state after the normal presentation pipeline has run and reports contract
/// violations that would otherwise be difficult to distinguish from ordinary
/// tracking noise.
/// </summary>
[DefaultExecutionOrder(35000)]
[DisallowMultipleComponent]
public sealed class KiwiRuntimeValidationHarness : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Runtime Validation Harness";

    public const string HarnessVersion =
        "5.1.0-phase11";

    public enum ValidationHealth
    {
        Starting = 0,
        Healthy = 1,
        Warning = 2,
        Failed = 3
    }

    public enum ValidationSeverity
    {
        Warning = 0,
        Error = 1,
        Critical = 2
    }

    public enum ValidationCode
    {
        None = 0,
        GenerationInvalid = 1,
        GenerationRegression = 2,
        SequenceRegression = 3,
        CanonicalFrameIdRegression = 4,
        CanonicalLatchStalled = 5,
        CanonicalSemanticAtomicity = 6,
        CanonicalNormalizationInvalid = 7,
        CanonicalCausalityViolation = 8,
        CanonicalGenerationStale = 9,
        ProviderSwitchWithoutGeneration = 10,
        ProviderCanonicalMismatch = 11,
        PolicyRunnerDrift = 12,
        PolicyLive2DDrift = 13,
        PresentationAlphaDrift = 14,
        DuplicateRoleOwner = 15,
        RecoverySequenceRegression = 16,
        Count = 17
    }

    [Serializable]
    public struct ValidationRecord
    {
        public int frame;
        public double realtime;
        public string code;
        public string severity;
        public string message;
    }

    [Serializable]
    private struct GenerationReport
    {
        public int camera;
        public int provider;
        public int model;
        public int config;
        public int calibration;
        public int session;
        public long observation;
        public long semantic;
    }

    [Serializable]
    private sealed class ExportReport
    {
        public string version;
        public string health;
        public int frame;
        public double realtime;
        public int totalViolations;
        public int activeViolations;
        public int warnings;
        public int errors;
        public int criticals;
        public string lastCode;
        public string lastMessage;
        public string provider;
        public ulong canonicalFrameId;
        public long rigidTimestamp;
        public long semanticTimestamp;
        public GenerationReport generation;
        public ValidationRecord[] recent;
    }

    [Header("Validation")]
    public bool enableValidation = true;

    [Tooltip("Avoid reporting normal auto-install and first-frame ordering while a scene is starting.")]
    [Range(0.1f, 5f)]
    public float startupGraceSeconds = 1.00f;

    [Tooltip("Log newly active validation failures to the Unity Console.")]
    public bool logViolations = true;

    [Tooltip("How long the same still-active failure must wait before it may be logged again.")]
    [Range(0.5f, 30f)]
    public float repeatLogIntervalSeconds = 5f;

    [Tooltip("Number of consecutive frames required for cross-system writer drift checks.")]
    [Range(1, 10)]
    public int writerDriftConfirmationFrames = 2;

    [Tooltip("Number of consecutive frames required before a generation mismatch is considered a stale canonical latch.")]
    [Range(1, 10)]
    public int staleGenerationConfirmationFrames = 2;

    [Header("Diagnostics")]
    [SerializeField] private ValidationHealth debugHealth =
        ValidationHealth.Starting;
    [SerializeField] private int debugTotalViolations;
    [SerializeField] private int debugActiveViolations;
    [SerializeField] private int debugWarningCount;
    [SerializeField] private int debugErrorCount;
    [SerializeField] private int debugCriticalCount;
    [SerializeField] private string debugLastCode = "None";
    [SerializeField] private string debugLastMessage = "-";
    [SerializeField] private int debugLastViolationFrame = -1;
    [SerializeField] private ulong debugCanonicalFrameId;
    [SerializeField] private string debugCanonicalProvider = "-";
    [SerializeField] private int debugHealthyFrameStreak;

    private const int RecordCapacity = 64;

    private static KiwiRuntimeValidationHarness _instance;

    private readonly int[] _failureStreak =
        new int[(int)ValidationCode.Count];
    private readonly bool[] _active =
        new bool[(int)ValidationCode.Count];
    private readonly ValidationSeverity[] _activeSeverity =
        new ValidationSeverity[(int)ValidationCode.Count];
    private readonly double[] _nextRepeatLogRealtime =
        new double[(int)ValidationCode.Count];
    private readonly ValidationRecord[] _records =
        new ValidationRecord[RecordCapacity];

    private int _recordWriteIndex;
    private int _recordCount;

    private double _graceUntilRealtime;
    private double _nextReferenceRefreshRealtime;
    private double _nextSingletonAuditRealtime;

    private FaceLandmarkerRunner _runner;
    private FacePartCropper _cropper;
    private KiwiTrackingProviderHub _hub;
    private KiwiFacePartLiveMotionBridge _liveMotion;

    private KiwiRuntimeGenerationContext.Snapshot _previousGeneration;
    private long _previousGlobalRecoverySequence;
    private long _previousSemanticRecoverySequence;
    private ulong _previousCanonicalFrameId;
    private string _previousProviderId = string.Empty;
    private int _previousProviderGeneration;

    private bool _duplicateRoleDetected;
    private string _duplicateRoleDetails = string.Empty;

    public static bool HasRuntimeInstance =>
        _instance != null;

    public static ValidationHealth Health =>
        _instance != null
            ? _instance.debugHealth
            : ValidationHealth.Starting;

    public static int TotalViolationCount =>
        _instance != null
            ? _instance.debugTotalViolations
            : 0;

    public static int ActiveViolationCount =>
        _instance != null
            ? _instance.debugActiveViolations
            : 0;

    public static int WarningCount =>
        _instance != null
            ? _instance.debugWarningCount
            : 0;

    public static int ErrorCount =>
        _instance != null
            ? _instance.debugErrorCount
            : 0;

    public static int CriticalCount =>
        _instance != null
            ? _instance.debugCriticalCount
            : 0;

    public static string LastViolationCode =>
        _instance != null
            ? _instance.debugLastCode
            : "None";

    public static string LastViolationMessage =>
        _instance != null
            ? _instance.debugLastMessage
            : "-";

    public static int LastViolationFrame =>
        _instance != null
            ? _instance.debugLastViolationFrame
            : -1;

    public static int HealthyFrameStreak =>
        _instance != null
            ? _instance.debugHealthyFrameStreak
            : 0;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        _instance = null;
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiRuntimeValidationHarness>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiRuntimeValidationHarness>();
    }

    private void Awake()
    {
        if (
            _instance != null &&
            _instance != this
        )
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        RefreshReferences(true);
        ResetBaselines();
        EnterStartupGrace();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;

        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _runner = null;
        _cropper = null;
        _hub = null;
        _liveMotion = null;
        _nextReferenceRefreshRealtime = 0.0;
        _nextSingletonAuditRealtime = 0.0;

        ClearTransientActives();
        RefreshReferences(true);
        ResetBaselines();
        EnterStartupGrace();
    }

    private void LateUpdate()
    {
        if (!enableValidation)
        {
            debugHealth = ValidationHealth.Starting;
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        if (now >= _nextReferenceRefreshRealtime)
        {
            _nextReferenceRefreshRealtime = now + 0.50;
            RefreshReferences(false);
        }

        ValidateMonotonicIdentity();
        ValidateRecoverySequences();

        bool graceActive =
            now < _graceUntilRealtime;

        if (!graceActive)
        {
            ValidateCanonicalContract();
            ValidateProviderContract();
            ValidatePolicyOwnership();
            ValidatePresentationOwnership();
            ValidateRoleOwnership(now);
        }
        else
        {
            Resolve(ValidationCode.CanonicalFrameIdRegression);
            Resolve(ValidationCode.CanonicalLatchStalled);
            Resolve(ValidationCode.CanonicalSemanticAtomicity);
            Resolve(ValidationCode.CanonicalNormalizationInvalid);
            Resolve(ValidationCode.CanonicalCausalityViolation);
            Resolve(ValidationCode.CanonicalGenerationStale);
            Resolve(ValidationCode.ProviderSwitchWithoutGeneration);
            Resolve(ValidationCode.ProviderCanonicalMismatch);
            Resolve(ValidationCode.PolicyRunnerDrift);
            Resolve(ValidationCode.PolicyLive2DDrift);
            Resolve(ValidationCode.PresentationAlphaDrift);
            Resolve(ValidationCode.DuplicateRoleOwner);
        }

        RecalculateHealth(graceActive);
    }

    private void ValidateMonotonicIdentity()
    {
        KiwiRuntimeGenerationContext.Snapshot current =
            KiwiRuntimeGenerationContext.Capture();

        bool invalid =
            current.cameraGeneration <= 0 ||
            current.providerGeneration <= 0 ||
            current.modelGeneration <= 0 ||
            current.configEpoch <= 0 ||
            current.calibrationGeneration <= 0 ||
            current.trackingSessionGeneration <= 0 ||
            current.observationSequence < 0L ||
            current.semanticTransactionSequence < 0L;

        Observe(
            ValidationCode.GenerationInvalid,
            invalid,
            ValidationSeverity.Critical,
            1,
            invalid
                ? "One or more runtime generation/sequence values became non-positive or negative."
                : null);

        bool generationRegression =
            current.cameraGeneration < _previousGeneration.cameraGeneration ||
            current.providerGeneration < _previousGeneration.providerGeneration ||
            current.modelGeneration < _previousGeneration.modelGeneration ||
            current.configEpoch < _previousGeneration.configEpoch ||
            current.calibrationGeneration < _previousGeneration.calibrationGeneration ||
            current.trackingSessionGeneration < _previousGeneration.trackingSessionGeneration;

        Observe(
            ValidationCode.GenerationRegression,
            generationRegression,
            ValidationSeverity.Critical,
            1,
            generationRegression
                ? "A cross-system generation counter regressed inside the same play session."
                : null);

        bool sequenceRegression =
            current.observationSequence < _previousGeneration.observationSequence ||
            current.semanticTransactionSequence <
                _previousGeneration.semanticTransactionSequence;

        Observe(
            ValidationCode.SequenceRegression,
            sequenceRegression,
            ValidationSeverity.Critical,
            1,
            sequenceRegression
                ? "ObservationSequence or SemanticTransactionSequence regressed."
                : null);

        _previousGeneration = current;
    }

    private void ValidateRecoverySequences()
    {
        long global =
            KiwiRecoveryDomainCoordinator.GlobalRecoverySequence;
        long semantic =
            KiwiRecoveryDomainCoordinator.SemanticRecoverySequence;

        bool regression =
            global < _previousGlobalRecoverySequence ||
            semantic < _previousSemanticRecoverySequence;

        Observe(
            ValidationCode.RecoverySequenceRegression,
            regression,
            ValidationSeverity.Critical,
            1,
            regression
                ? "Global or Semantic recovery sequence regressed."
                : null);

        _previousGlobalRecoverySequence = global;
        _previousSemanticRecoverySequence = semantic;
    }

    private void ValidateCanonicalContract()
    {
        if (!KiwiCanonicalTrackingFrame.IsRuntimeCoordinatorActive)
        {
            Resolve(ValidationCode.CanonicalFrameIdRegression);
            Resolve(ValidationCode.CanonicalLatchStalled);
            Resolve(ValidationCode.CanonicalSemanticAtomicity);
            Resolve(ValidationCode.CanonicalNormalizationInvalid);
            Resolve(ValidationCode.CanonicalCausalityViolation);
            Resolve(ValidationCode.CanonicalGenerationStale);
            return;
        }

        if (
            !KiwiCanonicalTrackingFrame.TryGetFrame(
                out KiwiTrackingFrame frame)
        )
        {
            // No valid face is a tracking state, not a validator failure.
            Resolve(ValidationCode.CanonicalFrameIdRegression);
            Resolve(ValidationCode.CanonicalLatchStalled);
            Resolve(ValidationCode.CanonicalSemanticAtomicity);
            Resolve(ValidationCode.CanonicalNormalizationInvalid);
            Resolve(ValidationCode.CanonicalCausalityViolation);
            Resolve(ValidationCode.CanonicalGenerationStale);
            return;
        }

        debugCanonicalFrameId =
            frame.canonicalFrameId;
        debugCanonicalProvider =
            string.IsNullOrEmpty(frame.providerId)
                ? "-"
                : frame.providerId;

        bool frameIdRegression =
            _previousCanonicalFrameId > 0UL &&
            frame.canonicalFrameId < _previousCanonicalFrameId;

        Observe(
            ValidationCode.CanonicalFrameIdRegression,
            frameIdRegression,
            ValidationSeverity.Critical,
            1,
            frameIdRegression
                ? "CanonicalFrameId regressed inside the same play session."
                : null);

        if (frame.canonicalFrameId > _previousCanonicalFrameId)
        {
            _previousCanonicalFrameId =
                frame.canonicalFrameId;
        }

        bool latchStalled =
            frame.unityFrame != Time.frameCount;

        Observe(
            ValidationCode.CanonicalLatchStalled,
            latchStalled,
            ValidationSeverity.Error,
            2,
            latchStalled
                ? "Canonical tracking frame was not republished in the current display cycle."
                : null);

        bool semanticAtomicityViolation =
            frame.hasSemanticLandmarks &&
            (
                frame.semanticTimestamp != frame.rigid.timestamp ||
                frame.semanticLandmarkCount <= 362
            );

        semanticAtomicityViolation |=
            frame.hasExpression &&
            (
                !frame.expression.isValid ||
                frame.expressionTimestamp < 0L ||
                frame.expressionTimestamp > frame.rigid.timestamp
            );

        Observe(
            ValidationCode.CanonicalSemanticAtomicity,
            semanticAtomicityViolation,
            ValidationSeverity.Critical,
            1,
            semanticAtomicityViolation
                ? "Canonical semantic/expression data violated the rigid-frame timestamp contract."
                : null);

        bool normalizationInvalid =
            !frame.normalization.valid ||
            frame.normalization.observationHostTicks <= 0L ||
            frame.normalization.arrivalHostTicks <= 0L;

        Observe(
            ValidationCode.CanonicalNormalizationInvalid,
            normalizationInvalid,
            ValidationSeverity.Error,
            2,
            normalizationInvalid
                ? "A valid canonical rigid frame is missing normalized observation/arrival timing metadata."
                : null);

        bool causalityViolation =
            frame.normalization.valid &&
            frame.normalization.observationHostTicks > 0L &&
            frame.normalization.arrivalHostTicks > 0L &&
            frame.normalization.observationHostTicks >
                frame.normalization.arrivalHostTicks;

        Observe(
            ValidationCode.CanonicalCausalityViolation,
            causalityViolation,
            ValidationSeverity.Critical,
            1,
            causalityViolation
                ? "Canonical observation time is later than arrival time; provider timebase mapping is non-causal."
                : null);

        KiwiRuntimeGenerationContext.Snapshot current =
            KiwiRuntimeGenerationContext.Capture();

        bool generationAhead =
            frame.generation.cameraGeneration > current.cameraGeneration ||
            frame.generation.providerGeneration > current.providerGeneration ||
            frame.generation.modelGeneration > current.modelGeneration ||
            frame.generation.configEpoch > current.configEpoch ||
            frame.generation.calibrationGeneration > current.calibrationGeneration ||
            frame.generation.trackingSessionGeneration > current.trackingSessionGeneration ||
            frame.generation.observationSequence > current.observationSequence ||
            frame.generation.semanticTransactionSequence >
                current.semanticTransactionSequence;

        bool staleGeneration =
            !generationAhead &&
            (
                frame.generation.cameraGeneration != current.cameraGeneration ||
                frame.generation.providerGeneration != current.providerGeneration ||
                frame.generation.modelGeneration != current.modelGeneration ||
                frame.generation.calibrationGeneration != current.calibrationGeneration ||
                frame.generation.trackingSessionGeneration != current.trackingSessionGeneration
            );

        Observe(
            ValidationCode.CanonicalGenerationStale,
            generationAhead || staleGeneration,
            generationAhead
                ? ValidationSeverity.Critical
                : ValidationSeverity.Error,
            generationAhead
                ? 1
                : Mathf.Max(1, staleGenerationConfirmationFrames),
            generationAhead
                ? "Canonical frame contains a future generation/sequence identity."
                : staleGeneration
                    ? "Canonical frame remained on an older camera/provider/model/calibration/session generation."
                    : null);
    }

    private void ValidateProviderContract()
    {
        if (
            !KiwiCanonicalTrackingFrame.TryGetFrame(
                out KiwiTrackingFrame frame)
        )
        {
            Resolve(ValidationCode.ProviderSwitchWithoutGeneration);
            Resolve(ValidationCode.ProviderCanonicalMismatch);
            return;
        }

        int providerGeneration =
            frame.generation.providerGeneration;

        string providerId =
            frame.providerId ?? string.Empty;

        bool providerChanged =
            !string.IsNullOrEmpty(_previousProviderId) &&
            !string.IsNullOrEmpty(providerId) &&
            !string.Equals(
                providerId,
                _previousProviderId,
                StringComparison.Ordinal);

        bool missedGeneration =
            providerChanged &&
            providerGeneration <=
                _previousProviderGeneration;

        Observe(
            ValidationCode.ProviderSwitchWithoutGeneration,
            missedGeneration,
            ValidationSeverity.Critical,
            1,
            missedGeneration
                ? "Rigid provider changed without a matching ProviderGeneration advance."
                : null);

        if (!string.IsNullOrEmpty(providerId))
        {
            _previousProviderId = providerId;
            _previousProviderGeneration = providerGeneration;
        }

        bool providerMismatch =
            _hub != null &&
            _hub.HasPublishedFrame &&
            !string.IsNullOrEmpty(_hub.ActiveProviderId) &&
            !string.Equals(
                _hub.ActiveProviderId,
                providerId,
                StringComparison.Ordinal);

        Observe(
            ValidationCode.ProviderCanonicalMismatch,
            providerMismatch,
            ValidationSeverity.Error,
            2,
            providerMismatch
                ? "Provider Hub active authority and canonical rigid provider disagree."
                : null);
    }

    private void ValidatePolicyOwnership()
    {
        if (
            _runner == null ||
            KiwiRuntimePolicyResolver.PolicyVersion <= 0
        )
        {
            Resolve(ValidationCode.PolicyRunnerDrift);
        }
        else
        {
            bool runnerDrift = false;

            int resolvedWidth =
                KiwiRuntimePolicyResolver.ResolvedTrackingInputWidth;
            float resolvedHz =
                KiwiRuntimePolicyResolver.ResolvedMediaPipeRefreshHz;
            float resolvedPresence =
                KiwiRuntimePolicyResolver.ResolvedPresenceThreshold;

            if (
                resolvedWidth > 0 &&
                _runner.trackingInputMaxWidth != resolvedWidth
            )
            {
                runnerDrift = true;
            }

            if (
                resolvedHz > 0f &&
                Mathf.Abs(
                    _runner.sentisMediaPipeRefreshRateHz -
                    resolvedHz) > 0.001f
            )
            {
                runnerDrift = true;
            }

            if (
                resolvedPresence > 0f &&
                Mathf.Abs(
                    _runner.sentisMinimumPresence -
                    resolvedPresence) > 0.0005f
            )
            {
                runnerDrift = true;
            }

            Observe(
                ValidationCode.PolicyRunnerDrift,
                runnerDrift,
                ValidationSeverity.Error,
                Mathf.Max(1, writerDriftConfirmationFrames),
                runnerDrift
                    ? "Runner runtime policy fields differ from RuntimePolicyResolver resolved values; a competing writer may be active."
                    : null);
        }

        if (
            _liveMotion == null ||
            !KiwiRuntimePolicyResolver.HasLive2DPolicy
        )
        {
            Resolve(ValidationCode.PolicyLive2DDrift);
            return;
        }

        bool liveDrift =
            _liveMotion.trackingLongSide !=
                KiwiRuntimePolicyResolver.ResolvedLiveTrackingLongSide ||
            _liveMotion.patchStridePixels !=
                KiwiRuntimePolicyResolver.ResolvedLivePatchStridePixels ||
            _liveMotion.restingSearchRadiusPixels !=
                KiwiRuntimePolicyResolver.ResolvedLiveRestingSearchRadiusPixels;

        Observe(
            ValidationCode.PolicyLive2DDrift,
            liveDrift,
            ValidationSeverity.Error,
            Mathf.Max(1, writerDriftConfirmationFrames),
            liveDrift
                ? "Live2D mutable quality fields differ from RuntimePolicyResolver resolved values; a competing writer may be active."
                : null);
    }

    private void ValidatePresentationOwnership()
    {
        if (_cropper == null)
        {
            Resolve(ValidationCode.PresentationAlphaDrift);
            return;
        }

        bool drift =
            HasPresentationAlphaDrift(
                _cropper.leftEyeImage) ||
            HasPresentationAlphaDrift(
                _cropper.rightEyeImage) ||
            HasPresentationAlphaDrift(
                _cropper.mouthImage);

        Observe(
            ValidationCode.PresentationAlphaDrift,
            drift,
            ValidationSeverity.Error,
            Mathf.Max(1, writerDriftConfirmationFrames),
            drift
                ? "A FacePart CanvasRenderer alpha differs from PresentationResolver output; another runtime alpha writer may be active."
                : null);
    }

    private static bool HasPresentationAlphaDrift(
        RawImage image)
    {
        if (image == null)
        {
            return false;
        }

        if (
            !KiwiFacePartPresentationResolver.TryGetResolvedState(
                image,
                out float resolved,
                out _)
        )
        {
            return false;
        }

        float actual =
            image.canvasRenderer.GetAlpha();

        return
            Mathf.Abs(
                actual - resolved) > 0.005f;
    }

    private void ValidateRoleOwnership(double now)
    {
        if (now >= _nextSingletonAuditRealtime)
        {
            _nextSingletonAuditRealtime = now + 1.0;
            AuditRoleOwners();
        }

        Observe(
            ValidationCode.DuplicateRoleOwner,
            _duplicateRoleDetected,
            ValidationSeverity.Error,
            2,
            _duplicateRoleDetected
                ? _duplicateRoleDetails
                : null);
    }

    private void AuditRoleOwners()
    {
        _duplicateRoleDetected = false;
        _duplicateRoleDetails = string.Empty;

        AppendDuplicateRole<KiwiTrackingProviderHub>(
            "TrackingProviderHub");
        AppendDuplicateRole<KiwiCanonicalTrackingFrameCoordinator>(
            "CanonicalTrackingFrameCoordinator");
        AppendDuplicateRole<KiwiRecoveryDomainCoordinator>(
            "RecoveryDomainCoordinator");
        AppendDuplicateRole<KiwiFacePartPresentationResolver>(
            "FacePartPresentationResolver");
        AppendDuplicateRole<KiwiRuntimePolicyResolver>(
            "RuntimePolicyResolver");
        AppendDuplicateRole<KiwiRuntimeValidationHarness>(
            "RuntimeValidationHarness");
        AppendDuplicateRole<KiwiFaultInjectionAcceptanceHarness>(
            "FaultInjectionAcceptanceHarness");
    }

    private void AppendDuplicateRole<T>(
        string roleName)
        where T : UnityEngine.Object
    {
        T[] instances =
            FindObjectsByType<T>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        if (instances.Length <= 1)
        {
            return;
        }

        _duplicateRoleDetected = true;

        if (!string.IsNullOrEmpty(_duplicateRoleDetails))
        {
            _duplicateRoleDetails += "; ";
        }

        _duplicateRoleDetails +=
            roleName + "=" + instances.Length;
    }

    private void Observe(
        ValidationCode code,
        bool failing,
        ValidationSeverity severity,
        int requiredFrames,
        string message)
    {
        int codeValue =
            (int)code;

        if (
            codeValue <= (int)ValidationCode.None ||
            codeValue >= (int)ValidationCode.Count
        )
        {
            return;
        }

        int index =
            codeValue;

        if (!failing)
        {
            _failureStreak[index] = 0;
            _active[index] = false;
            return;
        }

        _failureStreak[index]++;

        if (
            _failureStreak[index] <
            Mathf.Max(1, requiredFrames)
        )
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        bool newlyActive =
            !_active[index];

        if (newlyActive)
        {
            _active[index] = true;
            _activeSeverity[index] = severity;
            debugTotalViolations++;

            switch (severity)
            {
                case ValidationSeverity.Warning:
                    debugWarningCount++;
                    break;

                case ValidationSeverity.Error:
                    debugErrorCount++;
                    break;

                default:
                    debugCriticalCount++;
                    break;
            }

            RecordViolation(
                code,
                severity,
                message);

            _nextRepeatLogRealtime[index] =
                now +
                Mathf.Max(
                    0.5f,
                    repeatLogIntervalSeconds);
        }
        else if (
            (int)severity >
            (int)_activeSeverity[index]
        )
        {
            _activeSeverity[index] = severity;
        }

        if (
            logViolations &&
            (
                newlyActive ||
                now >= _nextRepeatLogRealtime[index]
            )
        )
        {
            _nextRepeatLogRealtime[index] =
                now +
                Mathf.Max(
                    0.5f,
                    repeatLogIntervalSeconds);

            string text =
                "[KiwiValidation] " +
                severity + " " +
                code + ": " +
                (message ?? "runtime contract violation");

            if (severity == ValidationSeverity.Critical)
            {
                Debug.LogError(text, this);
            }
            else
            {
                Debug.LogWarning(text, this);
            }
        }
    }

    private void Resolve(
        ValidationCode code)
    {
        int codeValue =
            (int)code;

        if (
            codeValue <= (int)ValidationCode.None ||
            codeValue >= (int)ValidationCode.Count
        )
        {
            return;
        }

        int index =
            codeValue;

        _failureStreak[index] = 0;
        _active[index] = false;
    }

    private void RecordViolation(
        ValidationCode code,
        ValidationSeverity severity,
        string message)
    {
        ValidationRecord record =
            new ValidationRecord
            {
                frame = Time.frameCount,
                realtime = Time.realtimeSinceStartupAsDouble,
                code = code.ToString(),
                severity = severity.ToString(),
                message = message ?? "runtime contract violation"
            };

        _records[_recordWriteIndex] =
            record;

        _recordWriteIndex =
            (_recordWriteIndex + 1) %
            RecordCapacity;

        _recordCount =
            Mathf.Min(
                _recordCount + 1,
                RecordCapacity);

        debugLastCode =
            record.code;
        debugLastMessage =
            record.message;
        debugLastViolationFrame =
            record.frame;
    }

    private void RecalculateHealth(
        bool graceActive)
    {
        int activeCount = 0;
        bool hasWarning = false;
        bool hasFailure = false;

        for (
            int i = 1;
            i < (int)ValidationCode.Count;
            i++
        )
        {
            if (!_active[i])
            {
                continue;
            }

            activeCount++;

            if (
                (int)_activeSeverity[i] >=
                (int)ValidationSeverity.Error
            )
            {
                hasFailure = true;
            }
            else
            {
                hasWarning = true;
            }
        }

        debugActiveViolations =
            activeCount;

        if (graceActive)
        {
            debugHealth =
                ValidationHealth.Starting;
            debugHealthyFrameStreak = 0;
            return;
        }

        if (hasFailure)
        {
            debugHealth =
                ValidationHealth.Failed;
            debugHealthyFrameStreak = 0;
        }
        else if (hasWarning)
        {
            debugHealth =
                ValidationHealth.Warning;
            debugHealthyFrameStreak = 0;
        }
        else
        {
            debugHealth =
                ValidationHealth.Healthy;
            debugHealthyFrameStreak++;
        }
    }

    private void RefreshReferences(
        bool force)
    {
        if (force || _runner == null)
        {
            _runner =
                FindFirstObjectByType<FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        if (force || _cropper == null)
        {
            _cropper =
                FindFirstObjectByType<FacePartCropper>(
                    FindObjectsInactive.Include);
        }

        if (force || _hub == null)
        {
            _hub =
                FindFirstObjectByType<KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }

        if (force || _liveMotion == null)
        {
            _liveMotion =
                FindFirstObjectByType<KiwiFacePartLiveMotionBridge>(
                    FindObjectsInactive.Include);
        }
    }

    private void ResetBaselines()
    {
        _previousGeneration =
            KiwiRuntimeGenerationContext.Capture();
        _previousGlobalRecoverySequence =
            KiwiRecoveryDomainCoordinator.GlobalRecoverySequence;
        _previousSemanticRecoverySequence =
            KiwiRecoveryDomainCoordinator.SemanticRecoverySequence;
        _previousCanonicalFrameId =
            KiwiCanonicalTrackingFrame.CanonicalFrameId;
        _previousProviderId =
            KiwiCanonicalTrackingFrame.ProviderId ??
            string.Empty;
        _previousProviderGeneration =
            KiwiRuntimeGenerationContext.ProviderGeneration;
    }

    private void EnterStartupGrace()
    {
        _graceUntilRealtime =
            Time.realtimeSinceStartupAsDouble +
            Mathf.Max(
                0.1f,
                startupGraceSeconds);

        debugHealth =
            ValidationHealth.Starting;
        debugHealthyFrameStreak = 0;
    }

    private void ClearTransientActives()
    {
        Array.Clear(
            _failureStreak,
            0,
            _failureStreak.Length);
        Array.Clear(
            _active,
            0,
            _active.Length);
        debugActiveViolations = 0;
    }

    public void ClearValidationHistory()
    {
        ClearTransientActives();
        Array.Clear(
            _records,
            0,
            _records.Length);
        Array.Clear(
            _nextRepeatLogRealtime,
            0,
            _nextRepeatLogRealtime.Length);

        _recordWriteIndex = 0;
        _recordCount = 0;
        debugTotalViolations = 0;
        debugWarningCount = 0;
        debugErrorCount = 0;
        debugCriticalCount = 0;
        debugLastCode = "None";
        debugLastMessage = "-";
        debugLastViolationFrame = -1;
        debugHealthyFrameStreak = 0;

        ResetBaselines();
        EnterStartupGrace();
    }

    public string BuildJsonReport(
        bool prettyPrint = true)
    {
        KiwiRuntimeGenerationContext.Snapshot generation =
            KiwiRuntimeGenerationContext.Capture();

        ValidationRecord[] recent =
            GetRecentRecords();

        ExportReport report =
            new ExportReport
            {
                version = HarnessVersion,
                health = debugHealth.ToString(),
                frame = Time.frameCount,
                realtime = Time.realtimeSinceStartupAsDouble,
                totalViolations = debugTotalViolations,
                activeViolations = debugActiveViolations,
                warnings = debugWarningCount,
                errors = debugErrorCount,
                criticals = debugCriticalCount,
                lastCode = debugLastCode,
                lastMessage = debugLastMessage,
                provider = KiwiCanonicalTrackingFrame.ProviderId,
                canonicalFrameId = KiwiCanonicalTrackingFrame.CanonicalFrameId,
                rigidTimestamp = KiwiCanonicalTrackingFrame.RigidTimestamp,
                semanticTimestamp = KiwiCanonicalTrackingFrame.SemanticTimestamp,
                generation = new GenerationReport
                {
                    camera = generation.cameraGeneration,
                    provider = generation.providerGeneration,
                    model = generation.modelGeneration,
                    config = generation.configEpoch,
                    calibration = generation.calibrationGeneration,
                    session = generation.trackingSessionGeneration,
                    observation = generation.observationSequence,
                    semantic = generation.semanticTransactionSequence
                },
                recent = recent
            };

        return
            JsonUtility.ToJson(
                report,
                prettyPrint);
    }

    public string ExportJsonReport()
    {
        string directory =
            Application.persistentDataPath;

        string path =
            Path.Combine(
                directory,
                "KiwiRuntimeValidationReport.json");

        File.WriteAllText(
            path,
            BuildJsonReport(true));

        return path;
    }

    private ValidationRecord[] GetRecentRecords()
    {
        ValidationRecord[] result =
            new ValidationRecord[_recordCount];

        int start =
            (_recordWriteIndex - _recordCount + RecordCapacity) %
            RecordCapacity;

        for (int i = 0; i < _recordCount; i++)
        {
            result[i] =
                _records[
                    (start + i) %
                    RecordCapacity];
        }

        return result;
    }
}
