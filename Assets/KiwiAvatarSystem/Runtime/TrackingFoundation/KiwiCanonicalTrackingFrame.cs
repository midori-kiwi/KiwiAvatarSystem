using System;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Immutable metadata for one display-cycle tracking snapshot.
///
/// The landmark array is intentionally not exposed by reference. Consumers copy
/// it through KiwiCanonicalTrackingFrame so no later producer can mutate a frame
/// after it has become visible to Root / FacePart presentation.
/// </summary>
public readonly struct KiwiTrackingFrame
{
    public readonly bool isValid;
    public readonly ulong canonicalFrameId;
    public readonly int unityFrame;
    public readonly string providerId;
    public readonly FacePrecisionTrackingData rigid;
    public readonly bool hasSemanticLandmarks;
    public readonly long semanticTimestamp;
    public readonly int semanticLandmarkCount;
    public readonly bool hasExpression;
    public readonly FaceExpressionData expression;
    public readonly long expressionTimestamp;
    public readonly KiwiTrackingNormalizedFrameMetadata normalization;
    public readonly KiwiRuntimeGenerationContext.Snapshot generation;

    public KiwiTrackingFrame(
        bool isValid,
        ulong canonicalFrameId,
        int unityFrame,
        string providerId,
        FacePrecisionTrackingData rigid,
        bool hasSemanticLandmarks,
        long semanticTimestamp,
        int semanticLandmarkCount,
        bool hasExpression,
        FaceExpressionData expression,
        long expressionTimestamp,
        KiwiTrackingNormalizedFrameMetadata normalization,
        KiwiRuntimeGenerationContext.Snapshot generation)
    {
        this.isValid = isValid;
        this.canonicalFrameId = canonicalFrameId;
        this.unityFrame = unityFrame;
        this.providerId = providerId ?? string.Empty;
        this.rigid = rigid;
        this.hasSemanticLandmarks = hasSemanticLandmarks;
        this.semanticTimestamp = semanticTimestamp;
        this.semanticLandmarkCount = semanticLandmarkCount;
        this.hasExpression = hasExpression;
        this.expression = expression;
        this.expressionTimestamp = expressionTimestamp;
        this.normalization = normalization;
        this.generation = generation;
    }
}

/// <summary>
/// v5.1 Phase 5 Root / FacePart canonical frame boundary.
///
/// A single snapshot is latched after normal Update processing and immediately
/// before FacePart LateUpdate consumers. KiwiFaceMotion reads the same rigid
/// snapshot later in LateUpdate and again at onBeforeRender, so a callback that
/// arrives after the latch cannot move Root without the matching Eye/Mouth
/// semantic frame in that render cycle.
///
/// This is an atomic render-cycle latch, not a permanent one-frame history
/// buffer. The newest frame available at the latch boundary is used directly.
/// </summary>
public static class KiwiCanonicalTrackingFrame
{
    private static readonly object Sync = new object();

    private static KiwiTrackingFrame _current;
    private static Vector2[] _publishedLandmarks;
    private static volatile bool _runtimeCoordinatorActive;
    private static ulong _nextCanonicalFrameId;
    private static string _lastProviderId = string.Empty;
    private static ulong _lastRigidSourceFrameId;
    private static long _lastRigidTimestamp = -1L;
    private static KiwiTrackingBackend _lastRigidBackend =
        KiwiTrackingBackend.Unknown;
    private static bool _lastCanonicalInputHorizontallyMirrored;

    private static int _lateRefreshCount;
    private static int _semanticMismatchCount;
    private static int _semanticHoldCount;
    private static int _rigidLatchCount;
    private static int _lastMismatchUnityFrame = -1;
    private static int _lastSemanticHoldUnityFrame = -1;

    public static bool IsRuntimeCoordinatorActive =>
        _runtimeCoordinatorActive;

    public static bool HasRigidFrame
    {
        get
        {
            lock (Sync)
            {
                return
                    _current.isValid &&
                    _current.rigid.isValid;
            }
        }
    }

    public static ulong CanonicalFrameId
    {
        get
        {
            lock (Sync)
            {
                return _current.canonicalFrameId;
            }
        }
    }

    public static string ProviderId
    {
        get
        {
            lock (Sync)
            {
                return _current.providerId ?? string.Empty;
            }
        }
    }

    public static long RigidTimestamp
    {
        get
        {
            lock (Sync)
            {
                return _current.isValid
                    ? _current.rigid.timestamp
                    : -1L;
            }
        }
    }

    public static long SemanticTimestamp
    {
        get
        {
            lock (Sync)
            {
                return _current.hasSemanticLandmarks
                    ? _current.semanticTimestamp
                    : -1L;
            }
        }
    }

    public static bool SemanticMatched
    {
        get
        {
            lock (Sync)
            {
                return
                    _current.isValid &&
                    _current.hasSemanticLandmarks &&
                    _current.semanticTimestamp ==
                        _current.rigid.timestamp;
            }
        }
    }

    public static long ObservationHostTicks
    {
        get
        {
            lock (Sync)
            {
                return _current.normalization.valid
                    ? _current.normalization.observationHostTicks
                    : 0L;
            }
        }
    }

    public static long ArrivalHostTicks
    {
        get
        {
            lock (Sync)
            {
                return _current.normalization.valid
                    ? _current.normalization.arrivalHostTicks
                    : 0L;
            }
        }
    }

    public static KiwiTrackingTimestampQuality TimestampQuality
    {
        get
        {
            lock (Sync)
            {
                return _current.normalization.valid
                    ? _current.normalization.timestampQuality
                    : KiwiTrackingTimestampQuality.ArrivalFallback;
            }
        }
    }

    public static bool CanonicalInputHorizontallyMirrored
    {
        get
        {
            lock (Sync)
            {
                return
                    _current.normalization.valid &&
                    _current.normalization.canonicalInputHorizontallyMirrored;
            }
        }
    }

    public static int LateRefreshCount => _lateRefreshCount;
    public static int SemanticMismatchCount => _semanticMismatchCount;
    public static int SemanticHoldCount => _semanticHoldCount;
    public static int RigidLatchCount => _rigidLatchCount;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetRuntimeState()
    {
        lock (Sync)
        {
            _current = default;
            _publishedLandmarks = null;
            _nextCanonicalFrameId = 0UL;
            _lastProviderId = string.Empty;
            _lastRigidSourceFrameId = 0UL;
            _lastRigidTimestamp = -1L;
            _lastRigidBackend = KiwiTrackingBackend.Unknown;
            _lastCanonicalInputHorizontallyMirrored = false;
        }

        _runtimeCoordinatorActive = false;
        _lateRefreshCount = 0;
        _semanticMismatchCount = 0;
        _semanticHoldCount = 0;
        _rigidLatchCount = 0;
        _lastMismatchUnityFrame = -1;
        _lastSemanticHoldUnityFrame = -1;
    }

    public static bool TryGetFrame(
        out KiwiTrackingFrame frame)
    {
        lock (Sync)
        {
            frame = _current;
            return
                frame.isValid &&
                frame.rigid.isValid;
        }
    }

    public static bool TryGetRigidFrame(
        out FacePrecisionTrackingData data)
    {
        lock (Sync)
        {
            data = _current.rigid;
            return
                _current.isValid &&
                data.isValid;
        }
    }

    /// <summary>
    /// FacePartCropper / ShapeMask compatibility access. When the canonical
    /// coordinator is alive, only the latched semantic frame is exposed. Before
    /// installation (legacy scenes / very early startup), the original Runner
    /// API remains the compatibility fallback.
    /// </summary>
    public static bool TryGetSemanticLandmarksIfChanged(
        FaceLandmarkerRunner fallbackRunner,
        ref Vector2[] destination,
        long previousTimestamp,
        out int count,
        out long timestamp,
        out bool hasFace)
    {
        if (!_runtimeCoordinatorActive)
        {
            if (fallbackRunner == null)
            {
                count = 0;
                timestamp = -1L;
                hasFace = false;
                return false;
            }

            return fallbackRunner.TryGetLatestLandmarksIfChanged(
                ref destination,
                previousTimestamp,
                out count,
                out timestamp,
                out hasFace);
        }

        lock (Sync)
        {
            hasFace =
                _current.isValid &&
                _current.rigid.isValid;

            count =
                _current.hasSemanticLandmarks
                    ? _current.semanticLandmarkCount
                    : 0;

            timestamp =
                _current.hasSemanticLandmarks
                    ? _current.semanticTimestamp
                    : -1L;

            if (
                !_current.hasSemanticLandmarks ||
                _publishedLandmarks == null ||
                count <= 0)
            {
                if (
                    hasFace &&
                    _lastSemanticHoldUnityFrame != Time.frameCount
                )
                {
                    _lastSemanticHoldUnityFrame = Time.frameCount;
                    _semanticHoldCount++;
                }

                return false;
            }

            if (timestamp == previousTimestamp)
            {
                return false;
            }

            if (
                destination == null ||
                destination.Length < count)
            {
                destination = new Vector2[count];
            }

            Array.Copy(
                _publishedLandmarks,
                destination,
                count);

            return true;
        }
    }

    public static bool TryGetExpressionData(
        FaceLandmarkerRunner fallbackRunner,
        out FaceExpressionData data,
        out long timestamp)
    {
        if (!_runtimeCoordinatorActive)
        {
            if (fallbackRunner == null)
            {
                data = default;
                timestamp = -1L;
                return false;
            }

            return fallbackRunner.TryGetLatestExpressionData(
                out data,
                out timestamp);
        }

        lock (Sync)
        {
            data = _current.expression;
            timestamp = _current.expressionTimestamp;

            return
                _current.isValid &&
                _current.hasExpression &&
                data.isValid &&
                timestamp >= 0L;
        }
    }

    public static bool IsCurrentSemanticTimestamp(
        long timestamp)
    {
        lock (Sync)
        {
            return
                _current.isValid &&
                _current.hasSemanticLandmarks &&
                timestamp >= 0L &&
                timestamp == _current.semanticTimestamp;
        }
    }

    internal static void SetRuntimeCoordinatorActive(
        bool active)
    {
        _runtimeCoordinatorActive = active;

        if (!active)
        {
            lock (Sync)
            {
                _current = default;
                _publishedLandmarks = null;
            }
        }
    }

    internal static void RecordLateHubRefresh()
    {
        _lateRefreshCount++;
    }

    internal static void Publish(
        FacePrecisionTrackingData rigid,
        string providerId,
        Vector2[] semanticLandmarks,
        int semanticLandmarkCount,
        bool hasSemanticLandmarks,
        FaceExpressionData expression,
        long expressionTimestamp,
        KiwiTrackingNormalizedFrameMetadata normalization,
        KiwiRuntimeGenerationContext.Snapshot generation)
    {
        providerId = providerId ?? string.Empty;

        lock (Sync)
        {
            bool identityChanged =
                !string.Equals(
                    providerId,
                    _lastProviderId,
                    StringComparison.Ordinal) ||
                rigid.frameId != _lastRigidSourceFrameId ||
                rigid.timestamp != _lastRigidTimestamp ||
                rigid.backend != _lastRigidBackend ||
                (
                    normalization.valid &&
                    normalization.canonicalInputHorizontallyMirrored !=
                        _lastCanonicalInputHorizontallyMirrored
                );

            if (identityChanged)
            {
                _nextCanonicalFrameId++;
                if (_nextCanonicalFrameId == 0UL)
                {
                    _nextCanonicalFrameId++;
                }

                _rigidLatchCount++;
                _lastProviderId = providerId;
                _lastRigidSourceFrameId = rigid.frameId;
                _lastRigidTimestamp = rigid.timestamp;
                _lastRigidBackend = rigid.backend;
                _lastCanonicalInputHorizontallyMirrored =
                    normalization.valid &&
                    normalization.canonicalInputHorizontallyMirrored;
            }

            if (
                hasSemanticLandmarks &&
                semanticLandmarks != null &&
                semanticLandmarkCount > 0)
            {
                if (
                    _publishedLandmarks == null ||
                    _publishedLandmarks.Length < semanticLandmarkCount)
                {
                    _publishedLandmarks =
                        new Vector2[semanticLandmarkCount];
                }

                Array.Copy(
                    semanticLandmarks,
                    _publishedLandmarks,
                    semanticLandmarkCount);
            }

            bool hasExpression =
                expression.isValid &&
                expressionTimestamp >= 0L &&
                expressionTimestamp <= rigid.timestamp;

            _current = new KiwiTrackingFrame(
                isValid: rigid.isValid,
                canonicalFrameId: _nextCanonicalFrameId,
                unityFrame: Time.frameCount,
                providerId: providerId,
                rigid: rigid,
                hasSemanticLandmarks: hasSemanticLandmarks,
                semanticTimestamp:
                    hasSemanticLandmarks
                        ? rigid.timestamp
                        : -1L,
                semanticLandmarkCount:
                    hasSemanticLandmarks
                        ? semanticLandmarkCount
                        : 0,
                hasExpression: hasExpression,
                expression: expression,
                expressionTimestamp:
                    hasExpression
                        ? expressionTimestamp
                        : -1L,
                normalization: normalization,
                generation: generation);
        }
    }

    internal static void PublishUnavailable(
        KiwiRuntimeGenerationContext.Snapshot generation)
    {
        lock (Sync)
        {
            _current = new KiwiTrackingFrame(
                isValid: false,
                canonicalFrameId: _nextCanonicalFrameId,
                unityFrame: Time.frameCount,
                providerId: string.Empty,
                rigid: default,
                hasSemanticLandmarks: false,
                semanticTimestamp: -1L,
                semanticLandmarkCount: 0,
                hasExpression: false,
                expression: default,
                expressionTimestamp: -1L,
                normalization: default,
                generation: generation);
        }
    }

    internal static void RecordSemanticMismatch()
    {
        if (_lastMismatchUnityFrame == Time.frameCount)
        {
            return;
        }

        _lastMismatchUnityFrame = Time.frameCount;
        _semanticMismatchCount++;
    }
}

/// <summary>
/// Captures the newest provider-authoritative Root frame and its matching
/// Runner semantic landmarks once per display cycle.
/// </summary>
[DefaultExecutionOrder(650)]
[DisallowMultipleComponent]
public sealed class KiwiCanonicalTrackingFrameCoordinator : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Canonical Tracking Frame";

    private KiwiTrackingProviderHub _hub;
    private FaceLandmarkerRunner _runner;
    private Vector2[] _semanticScratch;

    [Header("Diagnostics")]
    [SerializeField] private bool debugRigidValid;
    [SerializeField] private bool debugSemanticMatched;
    [SerializeField] private ulong debugCanonicalFrameId;
    [SerializeField] private string debugProvider = "-";
    [SerializeField] private long debugRigidTimestamp = -1L;
    [SerializeField] private long debugSemanticTimestamp = -1L;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<
                KiwiCanonicalTrackingFrameCoordinator>(
                FindObjectsInactive.Include) != null)
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiCanonicalTrackingFrameCoordinator>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        RefreshReferences(true);
        KiwiCanonicalTrackingFrame.SetRuntimeCoordinatorActive(true);
    }

    private void OnEnable()
    {
        KiwiCanonicalTrackingFrame.SetRuntimeCoordinatorActive(true);
    }

    private void OnDisable()
    {
        KiwiCanonicalTrackingFrame.SetRuntimeCoordinatorActive(false);
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        KiwiCanonicalTrackingFrame.SetRuntimeCoordinatorActive(false);
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _hub = null;
        _runner = null;
        _semanticScratch = null;
        RefreshReferences(true);
        KiwiCanonicalTrackingFrame.PublishUnavailable(
            KiwiRuntimeGenerationContext.Capture());
    }

    private void LateUpdate()
    {
        RefreshReferences(false);

        // KIWI_V5_1_PHASE5_LATE_PROVIDER_REFRESH
        // ProviderHub.Update executes before Runner.Update. Refreshing the same
        // arbitration logic here (before FacePartCropper order 700) exposes a
        // Runner sample produced in this Unity frame without changing provider
        // selection semantics or treating same-provider resume as a switch.
        if (_hub != null)
        {
            _hub.RefreshCanonicalSelectionNow();
            KiwiCanonicalTrackingFrame.RecordLateHubRefresh();
        }

        CaptureCanonicalFrame();
        UpdateDiagnostics();
    }

    private void CaptureCanonicalFrame()
    {
        FacePrecisionTrackingData rigid = default;
        string providerId = string.Empty;
        KiwiTrackingNormalizedFrameMetadata normalization = default;

        bool hasRigid;

        if (_hub != null)
        {
            hasRigid =
                _hub.TryGetLatestFrame(
                    out rigid,
                    out providerId,
                    out normalization);
        }
        else
        {
            hasRigid =
                _runner != null &&
                _runner.TryGetLatestPrecisionTrackingData(
                    out rigid);

            if (hasRigid)
            {
                providerId =
                    rigid.backend ==
                        KiwiTrackingBackend.InferenceEngine
                        ? "Runner/InferenceEngine"
                        : "Runner/MediaPipe";

                bool mirrored =
                    _runner != null &&
                    _runner.IsInputHorizontallyMirrored;

                long observationTicks =
                    rigid.submissionHostTicks > 0L
                        ? rigid.submissionHostTicks
                        : rigid.arrivalHostTicks;

                normalization =
                    new KiwiTrackingNormalizedFrameMetadata(
                        valid: true,
                        sourceTimebase:
                            rigid.submissionHostTicks > 0L
                                ? KiwiTrackingTimebase.HostStopwatchTicks
                                : KiwiTrackingTimebase.ArrivalHostOnly,
                        timestampQuality:
                            rigid.submissionHostTicks > 0L
                                ? KiwiTrackingTimestampQuality.ExactHostObservation
                                : KiwiTrackingTimestampQuality.ArrivalFallback,
                        sourceHorizontalConvention:
                            mirrored
                                ? KiwiTrackingHorizontalConvention.Mirrored
                                : KiwiTrackingHorizontalConvention.Unmirrored,
                        canonicalInputHorizontallyMirrored: mirrored,
                        horizontalTransformApplied: false,
                        providerSourceTimestamp:
                            rigid.submissionHostTicks > 0L
                                ? rigid.submissionHostTicks
                                : rigid.timestamp,
                        providerSourceFrameId: rigid.frameId,
                        observationHostTicks: observationTicks,
                        arrivalHostTicks: rigid.arrivalHostTicks,
                        timebaseResetCount: 0);
            }
        }

        KiwiRuntimeGenerationContext.Snapshot generation =
            KiwiRuntimeGenerationContext.Capture();

        if (!hasRigid || !rigid.isValid)
        {
            KiwiCanonicalTrackingFrame.PublishUnavailable(
                generation);
            return;
        }

        FacePrecisionTrackingData semanticPrecision = default;
        int semanticCount = 0;

        bool hasRunnerSemantic =
            _runner != null &&
            _runner.TryGetLatestPrecisionTrackingData(
                ref _semanticScratch,
                out semanticCount,
                out semanticPrecision);

        bool builtInProvider =
            string.Equals(
                providerId,
                "Runner/MediaPipe",
                StringComparison.Ordinal) ||
            string.Equals(
                providerId,
                "Runner/InferenceEngine",
                StringComparison.Ordinal) ||
            _hub == null;

        bool semanticMatched =
            builtInProvider &&
            hasRunnerSemantic &&
            semanticPrecision.isValid &&
            semanticCount > 362 &&
            semanticPrecision.timestamp == rigid.timestamp &&
            semanticPrecision.backend == rigid.backend;

        // KIWI_V5_1_PHASE16_6_COMMERCIAL_CANONICAL_SEMANTIC_COHERENCE
        // Timestamp equality alone is not sufficient for commercial
        // presentation. Reject a one-result topology break after removing the
        // bilateral-eye similarity motion. Rigid Root remains authoritative;
        // only semantic Crop/Mask adoption is held for the suspect result.
        if (
            semanticMatched &&
            !KiwiCommercialFacePartPolicy.IsCanonicalSemanticGeometryCoherent(
                _semanticScratch,
                semanticCount,
                semanticPrecision.timestamp,
                providerId,
                generation)
        )
        {
            semanticMatched = false;
            KiwiCanonicalTrackingFrame.RecordSemanticMismatch();
        }

        FaceExpressionData expression = default;
        long expressionTimestamp = -1L;

        if (semanticMatched && _runner != null)
        {
            _runner.TryGetLatestExpressionData(
                out expression,
                out expressionTimestamp);
        }

        if (!semanticMatched && builtInProvider)
        {
            KiwiCanonicalTrackingFrame.RecordSemanticMismatch();
        }

        KiwiCanonicalTrackingFrame.Publish(
            rigid,
            providerId,
            _semanticScratch,
            semanticCount,
            semanticMatched,
            expression,
            expressionTimestamp,
            normalization,
            generation);
    }

    private void RefreshReferences(
        bool force)
    {
        if (force || _hub == null)
        {
            _hub =
                FindFirstObjectByType<
                    KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }

        if (force || _runner == null)
        {
            _runner =
                FindFirstObjectByType<
                    FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }
    }

    private void UpdateDiagnostics()
    {
        debugRigidValid =
            KiwiCanonicalTrackingFrame.HasRigidFrame;

        debugSemanticMatched =
            KiwiCanonicalTrackingFrame.SemanticMatched;

        debugCanonicalFrameId =
            KiwiCanonicalTrackingFrame.CanonicalFrameId;

        debugProvider =
            KiwiCanonicalTrackingFrame.ProviderId;

        debugRigidTimestamp =
            KiwiCanonicalTrackingFrame.RigidTimestamp;

        debugSemanticTimestamp =
            KiwiCanonicalTrackingFrame.SemanticTimestamp;
    }
}
