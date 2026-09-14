#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using UnityEngine;

/// <summary>
/// DIAGNOSTIC_ONLY, O(1), latest-only observer for the right-side
/// MATCHED LANDMARK DEBUG path. It never publishes tracking state, changes
/// cadence/backend/settings, queues samples, or reads back a texture.
/// </summary>
[DefaultExecutionOrder(40000)]
internal sealed class KiwiH1LandmarkerBoundaryObserver : MonoBehaviour
{
    private const string EnableEnvironment = "KIWI_H1_BOUNDARY_RUNTIME";
    private const string OutputDirectoryEnvironment = "KIWI_H1_BOUNDARY_OUTPUT";
    private const double AutoQuitSeconds = 107.0;
    private const double AggregatePeriodSeconds = 1.0;
    private const double WarmupEndSeconds = 10.0;
    private const double NeutralStillnessAEndSeconds = 30.0;
    private const double SlowYawEndSeconds = 50.0;
    private const double SlowPitchEndSeconds = 70.0;
    private const double SlowRollEndSeconds = 90.0;
    private const double NeutralStillnessBEndSeconds = 105.0;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly object Sync = new object();
    private static readonly int[] FingerprintIndices = { 1, 33, 152, 263, 454 };
    private static volatile bool _armed;
    private static KiwiH1LandmarkerBoundaryObserver _activeInstance;

    private struct Sample
    {
        public bool valid;
        public long sequence;
        public long timestamp;
        public long hostTicks;
        public ulong fingerprint;
        public int count;
        public float maxDelta;
        public string provider;
        public KiwiTrackingBackend backend;
        public ulong providerSourceFrameId;
    }

    private struct PresentationObservation
    {
        public bool valid;
        public long observationHostTicks;
        public long committedSemanticTimestamp;
        public ulong committedCanonicalFrameId;
        public string canonicalCorrelationStatus;
        public bool providerMetadataExactMatch;
        public bool normalizationValid;
        public string providerId;
        public KiwiTrackingBackend backend;
        public ulong providerSourceFrameId;
    }

    private static bool _installed;
    private static long _rawSequence;
    private static long _rawCallbackCount;
    private static long _rawLatestOverwriteCount;
    private static long _rawDuplicateTimestampCount;
    private static long _rawOutOfOrderTimestampCount;
    private static long _rawAbnormalGapCount;
    private static long _rawResultAbsentCount;
    private static long _rawAuxOnlyCount;
    private static long _handoffCount;
    private static long _handoffIdentityMismatchCount;
    private static long _handoffDuplicateTimestampCount;
    private static long _handoffOutOfOrderTimestampCount;
    private static long _intentionalHandoffOverwriteCount;
    private static long _lastRawTimestamp = long.MinValue;
    private static long _lastRawArrivalTicks;
    private static long _lastHandoffTimestamp = long.MinValue;
    private static long _lastConsumedHandoffSequence;
    private static long _lastRawSequenceObservedByMain;
    private static double _rawIntervalEmaMs;
    private static float[] _previousRawSelected;
    private static Sample _latestRaw;
    private static Sample _latestHandoff;
    private static float _rawPeakDelta;
    private static long _rawPeakTimestamp = -1L;
    private static float _handoffPeakDelta;
    private static long _handoffPeakTimestamp = -1L;

    private FaceLandmarkerRunner _runner;
    private KiwiFrameComparisonOverlay _overlay;
    private KiwiTrackingProviderHub _hub;
    private KiwiTrackingContinuityState _continuity;
    private FieldInfo _overlayTimestampField;
    private FieldInfo _overlayLandmarksField;
    private FieldInfo _overlayCountField;
    private StreamWriter _aggregateWriter;
    private StreamWriter _segmentBoundaryWriter;
    private StreamWriter _correlationWriter;
    private string _outputDirectory;
    private string _summaryPath;
    private double _startedRealtime;
    private double _nextAggregateRealtime;
    private long _consumeCount;
    private long _presentationCount;
    private long _consumeMediaPipeCount;
    private long _consumeInferenceEngineCount;
    private long _consumeOtherCount;
    private long _consumeIdentityMismatchCount;
    private long _consumeDuplicateTimestampCount;
    private long _consumeOutOfOrderTimestampCount;
    private long _presentationDuplicateTimestampCount;
    private long _presentationOutOfOrderTimestampCount;
    private long _segmentBoundaryCount;
    private long _correlationRowCount;
    private long _lastConsumeTimestamp = long.MinValue;
    private long _lastPresentationTimestamp = long.MinValue;
    private float[] _previousConsumeSelected;
    private Sample _latestConsume;
    private Sample _latestPresentation;
    private PresentationObservation _latestCommittedPresentation;
    private KiwiFacePartTextureTransaction.PresentationDecisionSnapshot
        _latestPresentationDecision;
    private float _consumePeakDelta;
    private long _consumePeakTimestamp = -1L;
    private double _rawToConsumeAgeMsLatest = -1.0;
    private double _rawToConsumeAgeMsMaximum = -1.0;
    private string _activeSegment = string.Empty;
    private bool _activeSegmentClosed;
    private ulong _lastCorrelationCanonicalFrameId;
    private long _lastCorrelationConsumeTimestamp = long.MinValue;
    private long _lastCorrelationDecisionSequence;
    private long _lastCorrelationCommittedTimestamp = long.MinValue;
    private ulong _lastCorrelationCommittedCanonicalFrameId;
    private long _lastCorrelationPresentationObservationHostTicks;
    private long _lastCorrelationGeometryObservationHostTicks;
    private string _lastCorrelationProviderId = string.Empty;
    private int _lastCorrelationProviderGeneration = int.MinValue;
    private bool _lastCorrelationHandoffActive;
    private bool _quitting;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed || !IsEnabled()) return;
        _installed = true;
        _armed = true;
        var host = new GameObject("[Kiwi] H1 Landmarker Boundary Observer");
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiH1LandmarkerBoundaryObserver>();
    }

    private static bool IsEnabled()
    {
        string value = Environment.GetEnvironmentVariable(EnableEnvironment);
        return Debug.isDebugBuild &&
            (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    internal static long ObserveRawFaceLandmarkerCallback(
        FaceLandmarkerRunner runner,
        FaceLandmarkerResult result,
        long callbackTimestamp,
        long submissionHostTicks,
        long arrivalHostTicks,
        int cameraGeneration)
    {
        // The callback is not a Unity main-thread callback. Do not access
        // Debug/Application/Time or any other main-thread-only Unity API here.
        if (!_armed) return 0L;

        lock (Sync)
        {
            long sequence = ++_rawSequence;
            _rawCallbackCount++;

            if (_latestRaw.valid && _lastRawSequenceObservedByMain < _latestRaw.sequence)
                _rawLatestOverwriteCount++;

            if (_lastRawTimestamp != long.MinValue)
            {
                if (callbackTimestamp == _lastRawTimestamp) _rawDuplicateTimestampCount++;
                else if (callbackTimestamp < _lastRawTimestamp) _rawOutOfOrderTimestampCount++;
            }

            if (_lastRawArrivalTicks > 0L && arrivalHostTicks > _lastRawArrivalTicks)
            {
                double intervalMs = HostTicksToMilliseconds(arrivalHostTicks - _lastRawArrivalTicks);
                double gapLimitMs = Math.Max(250.0, _rawIntervalEmaMs > 0.0 ? _rawIntervalEmaMs * 3.0 : 250.0);
                if (_rawIntervalEmaMs > 0.0 && intervalMs > gapLimitMs) _rawAbnormalGapCount++;
                _rawIntervalEmaMs = _rawIntervalEmaMs > 0.0
                    ? _rawIntervalEmaMs * 0.85 + intervalMs * 0.15
                    : intervalMs;
            }

            _lastRawTimestamp = callbackTimestamp;
            _lastRawArrivalTicks = arrivalHostTicks;
            Sample sample = BuildSample(result, sequence, callbackTimestamp, arrivalHostTicks, ref _previousRawSelected);
            if (!sample.valid) _rawResultAbsentCount++;
            _latestRaw = sample;
            ObservePeak(sample, ref _rawPeakDelta, ref _rawPeakTimestamp);
            return sequence;
        }
    }

    internal static void ObserveRawToRunnerHandoff(
        FaceLandmarkerRunner runner,
        FaceLandmarkerResult result,
        long rawSequence,
        long timestamp,
        long handoffHostTicks,
        bool storeAccepted)
    {
        if (!_armed || rawSequence <= 0L || !storeAccepted) return;

        lock (Sync)
        {
            // MediaPipe callbacks are auxiliary rather than the right-side
            // semantic source while Inference Engine owns the latest sample.
            if (runner != null && runner.InferenceEnginePrimaryActive)
            {
                _rawAuxOnlyCount++;
                return;
            }

            if (_latestHandoff.valid && _lastConsumedHandoffSequence < _latestHandoff.sequence)
                _intentionalHandoffOverwriteCount++;

            float[] ignoredPrevious = null;
            Sample handoff = BuildSample(result, rawSequence, timestamp, handoffHostTicks, ref ignoredPrevious);

            FacePrecisionTrackingData published = default;
            bool publishedIdentityMatches =
                runner != null &&
                runner.TryGetLatestPrecisionTrackingData(out published) &&
                published.isValid &&
                published.backend == KiwiTrackingBackend.MediaPipe &&
                published.timestamp == timestamp &&
                published.frameId > 0UL;

            if (publishedIdentityMatches)
            {
                handoff.provider = "Runner/MediaPipe";
                handoff.backend = published.backend;
                handoff.providerSourceFrameId = published.frameId;
            }

            if (_lastHandoffTimestamp != long.MinValue)
            {
                if (timestamp == _lastHandoffTimestamp) _handoffDuplicateTimestampCount++;
                else if (timestamp < _lastHandoffTimestamp) _handoffOutOfOrderTimestampCount++;
            }
            _lastHandoffTimestamp = timestamp;
            _handoffCount++;

            if (!_latestRaw.valid || _latestRaw.sequence != rawSequence ||
                _latestRaw.timestamp != handoff.timestamp ||
                _latestRaw.fingerprint != handoff.fingerprint ||
                !publishedIdentityMatches)
                _handoffIdentityMismatchCount++;

            handoff.maxDelta = _latestRaw.sequence == rawSequence ? _latestRaw.maxDelta : 0f;
            _latestHandoff = handoff;
            ObservePeak(handoff, ref _handoffPeakDelta, ref _handoffPeakTimestamp);
        }
    }

    internal static void ObserveRightOverlayPreDraw(
        KiwiTrackingFrame canonicalFrame,
        Vector2[] landmarks,
        int count,
        long timestamp,
        long observationHostTicks)
    {
        if (!_armed) return;

        KiwiH1LandmarkerBoundaryObserver instance = _activeInstance;
        if (instance == null) return;

        instance.CaptureExactRightSidePreDraw(
            canonicalFrame,
            landmarks,
            count,
            timestamp,
            observationHostTicks);
    }

    private void Awake()
    {
        _activeInstance = this;
        Application.runInBackground = true;
        _startedRealtime = Time.realtimeSinceStartupAsDouble;
        _nextAggregateRealtime = _startedRealtime + AggregatePeriodSeconds;
        _outputDirectory = ResolveOutputDirectory();
        Directory.CreateDirectory(_outputDirectory);
        _summaryPath = Path.Combine(_outputDirectory, "summary.txt");
        _aggregateWriter = CreateWriter("aggregate_1hz.csv",
            "elapsedSeconds,segment,rawCount,handoffCount,consumeCount,presentationCount,rawAuxOnlyCount,intentionalHandoffOverwriteCount,rawDuplicateTs,rawOutOfOrderTs,rawAbnormalGap,rawLatestSeq,rawTs,rawFingerprint,rawMaxDelta,handoffSeq,handoffTs,handoffFingerprint,consumeTs,consumeFingerprint,consumeProvider,consumeBackend,providerSourceFrameId,consumeMaxDelta,rawToConsumeAgeMs,presentationTs");
        _segmentBoundaryWriter = CreateWriter("segment_boundaries.csv",
            "sequence,segment,event,hostTicks,elapsedSeconds,utc");
        _correlationWriter = CreateWriter("segment_correlation.csv",
            "row,segment,observationHostTicks,elapsedSeconds,canonicalAvailable,canonicalValid,canonicalFrameId,canonicalUnityFrame,semanticTimestamp,semanticLandmarkCount,providerId,backend,rigidFrameId,rigidTimestamp,sourceHostTicks,arrivalHostTicks,normalizationValid,providerSourceFrameId,providerSourceTimestamp,cameraGeneration,trackingSessionGeneration,providerGeneration,modelGeneration,canonicalFaceCenterX,canonicalFaceCenterY,canonicalRotationX,canonicalRotationY,canonicalRotationZ,canonicalRotationW,canonicalEulerX,canonicalEulerY,canonicalEulerZ,continuityAvailable,continuityState,continuityProviderId,continuitySourceAgeMs,continuityArrivalAgeMs,continuityCadenceJitterRatio,hubAvailable,hubActiveProviderId,hubSourceAgeMs,hubArrivalAgeMs,hubHandoffActive,hubHandoffIsResume,hubHandoffCount,providerTransitionObserved,rawSequence,rawTimestamp,rawFingerprint,handoffSequence,handoffTimestamp,handoffFingerprint,consumeValid,consumeTimestamp,consumeFingerprint,consumeProvider,consumeBackend,consumeProviderSourceFrameId,consumeHostTicks," +
            "latestPresentationValid,latestPresentationObservationHostTicks,latestPresentationCommittedSemanticTimestamp,latestPresentationCommittedCanonicalFrameId,latestPresentationCanonicalCorrelationStatus,latestPresentationProviderMetadataExactMatch,latestPresentationNormalizationValid,latestPresentationProviderId,latestPresentationBackend,latestPresentationProviderSourceFrameId," +
            "decisionValid,decisionSequence,decisionObservationHostTicks,decisionCandidateReceived,decisionAccepted,decisionReason,decisionSemanticTimestamp,decisionCanonicalFrameId,decisionProviderId,decisionBackend,decisionProviderSourceFrameId,decisionProviderSourceTimestamp,decisionSourceHostTicks,decisionArrivalHostTicks,decisionSourceAgeMs,decisionArrivalAgeMs,decisionSemanticFreshnessEvaluated,decisionSemanticFreshnessAgeMs,decisionCameraGeneration,decisionProviderGeneration,decisionTrackingSessionGeneration,decisionLeftEyeAccepted,decisionRightEyeAccepted,decisionMouthAccepted,decisionCommittedSemanticTimestamp,decisionCommittedCanonicalFrameId,decisionConsumeRelation," +
            "geometryDiagnosticValid,geometryDiagnosticObservationHostTicks,geometryDiagnosticSemanticTimestamp,geometryDiagnosticCanonicalFrameId,geometryDiagnosticCorrelationStatus,firstGeometrySnapshotIdentityFailure,geometryPipelineSubBoundary,expectedProviderSourceFrameId,expectedSourceHostTicks,expectedCameraGeneration,expectedTrackingSessionGeneration,expectedProviderGeneration,expectedModelGeneration,expectedBackend,expectedSemanticFrameWidth,expectedSemanticFrameHeight," +
            "predicateProductServiceAvailable,predicateSemanticDimensionsValid,predicateCanonicalFrameAvailable,predicateCanonicalFrameValid,predicateCanonicalSemanticPresent,predicateSemanticTimestampMatches,predicateRigidStateValid,predicateRigidValid,predicateRigidTimestampMatches,predicateRigidFrameIdPresent,predicateNormalizationValid,predicateProviderSourceFrameIdPresent,predicateCanonicalBackendMatches,predicateMatchedSubmissionTimingPresent,predicateSourceHostTicksPresent,predicateAcceptedSnapshotAvailable,predicateSourceFrameIdMatches,predicateSourceHostTicksMatches,predicateCameraGenerationMatches,predicateTrackingSessionGenerationMatches,predicateProviderGenerationMatches,predicateModelGenerationMatches,predicateAcceptedBackendMatches,predicateSemanticWidthMatches,predicateSemanticHeightMatches,predicateAccepted," +
            "acceptedSnapshotExists,acceptedGraphRunId,acceptedStreamId,acceptedFrameId,acceptedSourceHostTicks,acceptedCameraGeneration,acceptedTrackingSessionGeneration,acceptedProviderGeneration,acceptedModelGeneration,acceptedBackend,acceptedSemanticFrameWidth,acceptedSemanticFrameHeight,inFlightExists,inFlightFrameId,inFlightSourceHostTicks,latestPendingExists,latestPendingFrameId,latestPendingSourceHostTicks,callbackCompletionExists,callbackCompletionFrameId,callbackCompletionStatus,serviceShutdownRequested,serviceResetRequested,lastAcceptedGeometryFrameId,currentExpectedFrameInAccepted,currentExpectedFrameInFlight,currentExpectedFrameInPending,currentExpectedFrameInCompletion");
        UpdateSegmentBoundary();
        WriteSummary(false);
        Debug.Log("[KiwiH1Boundary] DIAGNOSTIC_ONLY fixed-segment observer armed output=" + _outputDirectory);
    }

    private void Update()
    {
        UpdateSegmentBoundary();
    }

    private void LateUpdate()
    {
        RefreshReferences();
        CaptureActualRightSideConsume();
        KiwiFacePartTextureTransaction.TryGetLastPresentationDecision(
            out _latestPresentationDecision);
        WriteCorrelationIfChanged();

        lock (Sync)
        {
            if (_latestRaw.valid) _lastRawSequenceObservedByMain = _latestRaw.sequence;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        if (now >= _nextAggregateRealtime)
        {
            WriteAggregate(now - _startedRealtime);
            _nextAggregateRealtime = now + AggregatePeriodSeconds;
        }

        if (now - _startedRealtime >= AutoQuitSeconds) Application.Quit(0);
    }

    private void OnGUI()
    {
        if (Event.current != null && Event.current.type == EventType.Repaint)
        {
            CapturePresentationEligibility();
            WriteCorrelationIfChanged();
        }

        GUI.color = Color.white;
        GUI.Box(new Rect(10f, 10f, 660f, 72f), GUIContent.none);
        GUI.Label(new Rect(20f, 16f, 640f, 24f), "H1 fixed-segment diagnostic: " + SegmentInstruction());
        GUI.Label(new Rect(20f, 40f, 640f, 24f),
            "Human marker不使用。連続録画を保持してください。segment=" + SegmentName() +
            " raw=" + ReadRawCount() + " consume=" + _consumeCount);
    }

    private void OnApplicationQuit()
    {
        _armed = false;
        _quitting = true;
        CloseActiveSegment();
        WriteAggregate(Time.realtimeSinceStartupAsDouble - _startedRealtime);
        WriteSummary(true);
        CloseWriters();
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_activeInstance, this))
        {
            _activeInstance = null;
        }

        if (_quitting) return;
        CloseActiveSegment();
        WriteSummary(false);
        CloseWriters();
    }

    private void RefreshReferences()
    {
        if (_runner == null)
            _runner = FindFirstObjectByType<FaceLandmarkerRunner>(FindObjectsInactive.Include);
        if (_hub == null)
            _hub = FindFirstObjectByType<KiwiTrackingProviderHub>(FindObjectsInactive.Include);
        if (_continuity == null)
            _continuity = FindFirstObjectByType<KiwiTrackingContinuityState>(FindObjectsInactive.Include);
        if (_overlay == null)
        {
            _overlay = KiwiFrameComparisonOverlay.Instance;
            if (_overlay == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = typeof(KiwiFrameComparisonOverlay);
            _overlayTimestampField = type.GetField("_lastSemanticTimestamp", flags);
            _overlayLandmarksField = type.GetField("_landmarks", flags);
            _overlayCountField = type.GetField("_landmarkCount", flags);
        }
    }

    private void CaptureExactRightSidePreDraw(
        KiwiTrackingFrame canonicalFrame,
        Vector2[] landmarks,
        int count,
        long timestamp,
        long observationHostTicks)
    {
        if (
            !canonicalFrame.isValid ||
            !canonicalFrame.rigid.isValid ||
            !canonicalFrame.hasSemanticLandmarks ||
            canonicalFrame.semanticTimestamp != timestamp ||
            landmarks == null ||
            count <= 0)
        {
            return;
        }

        Sample consume = BuildSample(
            landmarks,
            count,
            timestamp,
            observationHostTicks,
            ref _previousConsumeSelected);

        consume.provider = canonicalFrame.providerId ?? string.Empty;
        consume.backend = canonicalFrame.rigid.backend;
        consume.providerSourceFrameId =
            canonicalFrame.normalization.valid
                ? canonicalFrame.normalization.providerSourceFrameId
                : 0UL;

        CommitRightSideConsume(consume);
    }

    private void CommitRightSideConsume(Sample consume)
    {
        if (!consume.valid) return;

        if (
            _lastConsumeTimestamp != long.MinValue &&
            consume.timestamp == _lastConsumeTimestamp)
        {
            return;
        }

        if (
            _lastConsumeTimestamp != long.MinValue &&
            consume.timestamp < _lastConsumeTimestamp)
        {
            _consumeOutOfOrderTimestampCount++;
        }

        _consumeCount++;
        if (consume.backend == KiwiTrackingBackend.MediaPipe) _consumeMediaPipeCount++;
        else if (consume.backend == KiwiTrackingBackend.InferenceEngine) _consumeInferenceEngineCount++;
        else _consumeOtherCount++;

        lock (Sync)
        {
            if (consume.backend == KiwiTrackingBackend.MediaPipe)
            {
                if (
                    !_latestHandoff.valid ||
                    _latestHandoff.timestamp != consume.timestamp ||
                    _latestHandoff.fingerprint != consume.fingerprint ||
                    _latestHandoff.providerSourceFrameId == 0UL ||
                    consume.providerSourceFrameId == 0UL ||
                    _latestHandoff.providerSourceFrameId != consume.providerSourceFrameId)
                {
                    _consumeIdentityMismatchCount++;
                }
                else
                {
                    _lastConsumedHandoffSequence = _latestHandoff.sequence;
                    _rawToConsumeAgeMsLatest =
                        HostTicksToMilliseconds(
                            consume.hostTicks - _latestRaw.hostTicks);
                    if (_rawToConsumeAgeMsLatest > _rawToConsumeAgeMsMaximum)
                        _rawToConsumeAgeMsMaximum = _rawToConsumeAgeMsLatest;
                }
            }
        }

        _lastConsumeTimestamp = consume.timestamp;
        _latestConsume = consume;
        ObservePeak(consume, ref _consumePeakDelta, ref _consumePeakTimestamp);
    }

    private void CaptureActualRightSideConsume()
    {
        if (_overlay == null || _overlayTimestampField == null ||
            _overlayLandmarksField == null || _overlayCountField == null) return;

        long timestamp = (long)_overlayTimestampField.GetValue(_overlay);
        if (timestamp == long.MinValue || timestamp == _lastConsumeTimestamp) return;

        Vector2[] landmarks = _overlayLandmarksField.GetValue(_overlay) as Vector2[];
        int count = (int)_overlayCountField.GetValue(_overlay);
        Sample consume = BuildSample(landmarks, count, timestamp,
            System.Diagnostics.Stopwatch.GetTimestamp(), ref _previousConsumeSelected);

        KiwiTrackingFrame frame = default;
        if (KiwiCanonicalTrackingFrame.TryGetFrame(out frame))
        {
            consume.provider = frame.providerId ?? string.Empty;
            consume.backend = frame.rigid.backend;
            consume.providerSourceFrameId = frame.normalization.valid
                ? frame.normalization.providerSourceFrameId
                : 0UL;
        }

        CommitRightSideConsume(consume);
    }

    private void CapturePresentationEligibility()
    {
        if (_overlay == null || !_overlay.visible || !_latestConsume.valid) return;
        if (!KiwiFacePartTextureTransaction.TryGetLastCommittedPresentationFrame(
                out Texture matchedTexture, out long matchedTimestamp,
                out ulong committedCanonicalFrameId) ||
            matchedTexture == null || matchedTimestamp != _latestConsume.timestamp) return;

        if (_latestCommittedPresentation.valid &&
            _latestCommittedPresentation.committedCanonicalFrameId == committedCanonicalFrameId &&
            _latestCommittedPresentation.committedSemanticTimestamp == matchedTimestamp) return;

        long observationHostTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var observation = new PresentationObservation
        {
            valid = true,
            observationHostTicks = observationHostTicks,
            committedSemanticTimestamp = matchedTimestamp,
            committedCanonicalFrameId = committedCanonicalFrameId,
            canonicalCorrelationStatus = "NO_CANONICAL_SNAPSHOT",
            providerMetadataExactMatch = false,
            normalizationValid = false,
            providerId = string.Empty,
            backend = KiwiTrackingBackend.Unknown,
            providerSourceFrameId = 0UL
        };

        KiwiTrackingFrame frame;
        if (KiwiCanonicalTrackingFrame.TryGetFrame(out frame))
        {
            if (frame.canonicalFrameId != committedCanonicalFrameId)
            {
                observation.canonicalCorrelationStatus = "CANONICAL_ID_MISMATCH";
            }
            else
            {
                observation.canonicalCorrelationStatus = "EXACT_CANONICAL_ID_MATCH";
                observation.providerMetadataExactMatch = true;
                observation.normalizationValid = frame.normalization.valid;
                observation.providerId = frame.providerId ?? string.Empty;
                observation.backend = frame.rigid.backend;
                observation.providerSourceFrameId = frame.normalization.valid
                    ? frame.normalization.providerSourceFrameId
                    : 0UL;
            }
        }

        if (_lastPresentationTimestamp != long.MinValue && matchedTimestamp == _lastPresentationTimestamp)
            _presentationDuplicateTimestampCount++;
        else if (_lastPresentationTimestamp != long.MinValue && matchedTimestamp < _lastPresentationTimestamp)
            _presentationOutOfOrderTimestampCount++;

        _latestCommittedPresentation = observation;
        _presentationCount++;
        _lastPresentationTimestamp = matchedTimestamp;
        _latestPresentation = _latestConsume;
        _latestPresentation.hostTicks = observationHostTicks;
    }

    private void UpdateSegmentBoundary()
    {
        string current = SegmentName();
        if (string.Equals(current, _activeSegment, StringComparison.Ordinal)) return;

        long hostTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsed = Time.realtimeSinceStartupAsDouble - _startedRealtime;
        if (!string.IsNullOrEmpty(_activeSegment) && !_activeSegmentClosed)
        {
            WriteSegmentBoundary(_activeSegment, "END", hostTicks, elapsed);
        }

        _activeSegment = current;
        _activeSegmentClosed = string.Equals(current, "COMPLETE", StringComparison.Ordinal);
        if (!_activeSegmentClosed)
        {
            WriteSegmentBoundary(current, "START", hostTicks, elapsed);
        }
    }

    private void CloseActiveSegment()
    {
        if (string.IsNullOrEmpty(_activeSegment) || _activeSegmentClosed) return;
        WriteSegmentBoundary(
            _activeSegment,
            "END",
            System.Diagnostics.Stopwatch.GetTimestamp(),
            Time.realtimeSinceStartupAsDouble - _startedRealtime);
        _activeSegmentClosed = true;
    }

    private void WriteSegmentBoundary(
        string segment,
        string eventName,
        long hostTicks,
        double elapsed)
    {
        _segmentBoundaryCount++;
        _segmentBoundaryWriter.WriteLine(
            _segmentBoundaryCount + "," + Csv(segment) + "," + Csv(eventName) + "," +
            hostTicks + "," + F(elapsed) + "," +
            Csv(DateTime.UtcNow.ToString("O", Invariant)));
        _segmentBoundaryWriter.Flush();
        Debug.Log("[KiwiH1Boundary] SEGMENT_" + eventName + "=" + segment +
            " hostTicks=" + hostTicks);
    }

    private void WriteCorrelationIfChanged()
    {
        KiwiTrackingFrame frame;
        bool canonicalAvailable = KiwiCanonicalTrackingFrame.TryGetFrame(out frame);

        Texture committedTexture;
        long committedTimestamp;
        ulong committedCanonicalFrameId;
        if (!KiwiFacePartTextureTransaction.TryGetLastCommittedPresentationFrame(
                out committedTexture,
                out committedTimestamp,
                out committedCanonicalFrameId))
        {
            committedTimestamp = -1L;
            committedCanonicalFrameId = 0UL;
        }

        FaceLandmarkerRunner.FacePartGeometrySnapshotDiagnostic
            geometryDiagnostic = default;
        if (_runner != null)
        {
            _runner.TryGetLatestFacePartGeometrySnapshotDiagnostic(
                out geometryDiagnostic);
        }

        ulong canonicalFrameId = canonicalAvailable ? frame.canonicalFrameId : 0UL;
        long geometryObservationHostTicks = geometryDiagnostic.valid
            ? geometryDiagnostic.observationHostTicks
            : 0L;
        if (
            canonicalFrameId == _lastCorrelationCanonicalFrameId &&
            _latestConsume.timestamp == _lastCorrelationConsumeTimestamp &&
            _latestPresentationDecision.sequence == _lastCorrelationDecisionSequence &&
            committedTimestamp == _lastCorrelationCommittedTimestamp &&
            committedCanonicalFrameId == _lastCorrelationCommittedCanonicalFrameId &&
            _latestCommittedPresentation.observationHostTicks ==
                _lastCorrelationPresentationObservationHostTicks &&
            geometryObservationHostTicks == _lastCorrelationGeometryObservationHostTicks)
        {
            return;
        }

        _lastCorrelationCanonicalFrameId = canonicalFrameId;
        _lastCorrelationConsumeTimestamp = _latestConsume.timestamp;
        _lastCorrelationDecisionSequence = _latestPresentationDecision.sequence;
        _lastCorrelationCommittedTimestamp = committedTimestamp;
        _lastCorrelationCommittedCanonicalFrameId = committedCanonicalFrameId;
        _lastCorrelationPresentationObservationHostTicks =
            _latestCommittedPresentation.observationHostTicks;
        _lastCorrelationGeometryObservationHostTicks = geometryObservationHostTicks;

        Sample raw;
        Sample handoff;
        lock (Sync)
        {
            raw = _latestRaw;
            handoff = _latestHandoff;
        }

        KiwiTrackingContinuityState.ContinuityState continuityState =
            KiwiTrackingContinuityState.ContinuityState.Starting;
        float continuitySourceAgeSeconds = float.PositiveInfinity;
        float ignoredPredictionAllowance;
        float cadenceJitterRatio;
        bool continuityAvailable = KiwiTrackingContinuityState.TryGetRuntimeStatus(
            out continuityState,
            out continuitySourceAgeSeconds,
            out ignoredPredictionAllowance,
            out cadenceJitterRatio);
        string continuityProviderId = _continuity != null
            ? _continuity.ProviderId
            : string.Empty;
        float continuityArrivalAgeSeconds = _continuity != null
            ? _continuity.ArrivalAgeSeconds
            : float.PositiveInfinity;

        string providerId = canonicalAvailable ? frame.providerId : string.Empty;
        int providerGeneration = canonicalAvailable
            ? frame.generation.providerGeneration
            : 0;
        bool handoffActive = _hub != null && _hub.HandoffActive;
        bool providerTransitionObserved =
            _lastCorrelationProviderGeneration != int.MinValue &&
            (
                providerGeneration != _lastCorrelationProviderGeneration ||
                !string.Equals(
                    providerId,
                    _lastCorrelationProviderId,
                    StringComparison.Ordinal) ||
                handoffActive != _lastCorrelationHandoffActive
            );
        _lastCorrelationProviderGeneration = providerGeneration;
        _lastCorrelationProviderId = providerId;
        _lastCorrelationHandoffActive = handoffActive;

        FacePrecisionTrackingData rigid = canonicalAvailable
            ? frame.rigid
            : default;
        Quaternion rotation = rigid.isValid
            ? rigid.faceRotation
            : Quaternion.identity;
        Vector3 euler = rotation.eulerAngles;
        long observationHostTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        _correlationRowCount++;
        _correlationWriter.WriteLine(
            _correlationRowCount + "," + Csv(SegmentName()) + "," +
            observationHostTicks + "," +
            F(Time.realtimeSinceStartupAsDouble - _startedRealtime) + "," +
            B(canonicalAvailable) + "," + B(canonicalAvailable && frame.isValid) + "," +
            canonicalFrameId + "," + (canonicalAvailable ? frame.unityFrame : 0) + "," +
            (canonicalAvailable ? frame.semanticTimestamp : -1L) + "," +
            (canonicalAvailable ? frame.semanticLandmarkCount : 0) + "," +
            Csv(providerId) + "," + Csv(rigid.backend.ToString()) + "," +
            rigid.frameId + "," + rigid.timestamp + "," + rigid.submissionHostTicks + "," +
            rigid.arrivalHostTicks + "," +
            B(canonicalAvailable && frame.normalization.valid) + "," +
            (canonicalAvailable && frame.normalization.valid
                ? frame.normalization.providerSourceFrameId
                : 0UL) + "," +
            (canonicalAvailable && frame.normalization.valid
                ? frame.normalization.providerSourceTimestamp
                : 0L) + "," +
            (canonicalAvailable ? frame.generation.cameraGeneration : 0) + "," +
            (canonicalAvailable ? frame.generation.trackingSessionGeneration : 0) + "," +
            providerGeneration + "," +
            (canonicalAvailable ? frame.generation.modelGeneration : 0) + "," +
            F(rigid.faceCenter.x) + "," + F(rigid.faceCenter.y) + "," +
            F(rotation.x) + "," + F(rotation.y) + "," + F(rotation.z) + "," + F(rotation.w) + "," +
            F(euler.x) + "," + F(euler.y) + "," + F(euler.z) + "," +
            B(continuityAvailable) + "," + Csv(continuityState.ToString()) + "," +
            Csv(continuityProviderId) + "," +
            F(SecondsToMilliseconds(continuitySourceAgeSeconds)) + "," +
            F(SecondsToMilliseconds(continuityArrivalAgeSeconds)) + "," +
            F(cadenceJitterRatio) + "," +
            B(_hub != null) + "," + Csv(_hub != null ? _hub.ActiveProviderId : string.Empty) + "," +
            F(_hub != null ? _hub.ActiveProviderSourceAgeMilliseconds : -1f) + "," +
            F(_hub != null ? _hub.ActiveProviderArrivalAgeMilliseconds : -1f) + "," +
            B(handoffActive) + "," + B(_hub != null && KiwiTrackingProviderHub.CanonicalHandoffIsResume) + "," +
            (_hub != null ? _hub.HandoffCount : 0) + "," + B(providerTransitionObserved) + "," +
            raw.sequence + "," + raw.timestamp + "," + raw.fingerprint + "," +
            handoff.sequence + "," + handoff.timestamp + "," + handoff.fingerprint + "," +
            B(_latestConsume.valid) + "," + _latestConsume.timestamp + "," +
            _latestConsume.fingerprint + "," + Csv(_latestConsume.provider) + "," +
            Csv(_latestConsume.backend.ToString()) + "," +
            _latestConsume.providerSourceFrameId + "," + _latestConsume.hostTicks + "," +
            PresentationCsv(_latestCommittedPresentation) + "," +
            PresentationDecisionCsv(_latestPresentationDecision) + "," +
            Csv(PresentationDecisionConsumeRelation(
                _latestPresentationDecision,
                _latestConsume)) + "," +
            GeometryDiagnosticCsv(
                geometryDiagnostic,
                _latestPresentationDecision,
                _latestConsume));
    }

    private void WriteAggregate(double elapsed)
    {
        Sample raw;
        Sample handoff;
        long rawCount;
        long handoffCount;
        long auxCount;
        long overwriteCount;
        long duplicateCount;
        long outOfOrderCount;
        long gapCount;
        lock (Sync)
        {
            raw = _latestRaw;
            handoff = _latestHandoff;
            rawCount = _rawCallbackCount;
            handoffCount = _handoffCount;
            auxCount = _rawAuxOnlyCount;
            overwriteCount = _intentionalHandoffOverwriteCount;
            duplicateCount = _rawDuplicateTimestampCount;
            outOfOrderCount = _rawOutOfOrderTimestampCount;
            gapCount = _rawAbnormalGapCount;
        }

        _aggregateWriter.WriteLine(
            F(elapsed) + "," + SegmentName() + "," + rawCount + "," + handoffCount + "," + _consumeCount + "," +
            _presentationCount + "," + auxCount + "," + overwriteCount + "," + duplicateCount + "," +
            outOfOrderCount + "," + gapCount + "," + raw.sequence + "," + raw.timestamp + "," + raw.fingerprint + "," +
            F(raw.maxDelta) + "," + handoff.sequence + "," + handoff.timestamp + "," + handoff.fingerprint + "," +
            _latestConsume.timestamp + "," + _latestConsume.fingerprint + "," + Csv(_latestConsume.provider) + "," +
            Csv(_latestConsume.backend.ToString()) + "," + _latestConsume.providerSourceFrameId + "," +
            F(_latestConsume.maxDelta) + "," + F(_rawToConsumeAgeMsLatest) + "," + _latestPresentation.timestamp);
        _aggregateWriter.Flush();
        _correlationWriter.Flush();
    }

    private void WriteSummary(bool playerClosed)
    {
        Sample raw;
        Sample handoff;
        long rawCount;
        long rawOverwrite;
        long rawDuplicate;
        long rawOutOfOrder;
        long rawGap;
        long rawAbsent;
        long rawAux;
        long handoffCount;
        long handoffMismatch;
        long handoffDuplicate;
        long handoffOutOfOrder;
        long handoffOverwrite;
        lock (Sync)
        {
            raw = _latestRaw;
            handoff = _latestHandoff;
            rawCount = _rawCallbackCount;
            rawOverwrite = _rawLatestOverwriteCount;
            rawDuplicate = _rawDuplicateTimestampCount;
            rawOutOfOrder = _rawOutOfOrderTimestampCount;
            rawGap = _rawAbnormalGapCount;
            rawAbsent = _rawResultAbsentCount;
            rawAux = _rawAuxOnlyCount;
            handoffCount = _handoffCount;
            handoffMismatch = _handoffIdentityMismatchCount;
            handoffDuplicate = _handoffDuplicateTimestampCount;
            handoffOutOfOrder = _handoffOutOfOrderTimestampCount;
            handoffOverwrite = _intentionalHandoffOverwriteCount;
        }

        string[] lines =
        {
            "KIWI_H1_RIGHT_SIDE_LANDMARKER_BOUNDARY_DIAGNOSTIC",
            "UTC=" + DateTime.UtcNow.ToString("O", Invariant),
            "UNITY_VERSION=" + Application.unityVersion,
            "DEBUG_BUILD=" + B(Debug.isDebugBuild),
            "GRAPHICS_API=" + SystemInfo.graphicsDeviceType,
            "SCENE=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().path,
            "DIAGNOSTIC_ONLY=1",
            "PRODUCTION_BEHAVIOR_CHANGED=0",
            "HUMAN_VISUAL_AUTHORITY=CONTINUOUS_SAME_ARTIFACT_RECORDING",
            "HUMAN_MARKER_USED=0",
            "PRIMARY_HUMAN_VISUAL_SCOPE=NEUTRAL_STILLNESS_A",
            "HUMAN_VISUAL_UNSTABLE_WINDOW=REQUIRES_HUMAN_RECORDING_REVIEW",
            "MATCHED_LANDMARK_DEBUG_WAIT_OR_RESUME_IS_PRODUCT_FAILURE=0",
            "SEGMENT_SCHEDULE=WARMUP_0_10|NEUTRAL_STILLNESS_A_10_30|SLOW_YAW_30_50|SLOW_PITCH_50_70|SLOW_ROLL_70_90|NEUTRAL_STILLNESS_B_90_105",
            "SEGMENT_BOUNDARY_CLOCK=STOPWATCH_HOST_TICKS",
            "OBSERVER_STORAGE=STREAMED_CORRELATION_NO_PRODUCT_QUEUE",
            "AGGREGATE_RATE_HZ=1",
            "PERFORMANCE_AUTHORITY=NONE",
            "PLAYER_CLOSED=" + (playerClosed ? "YES" : "NO"),
            "RIGHT_SIDE_VISIBLE_SINK=KiwiFrameComparisonOverlay.MATCHED_LANDMARK_DEBUG",
            "PACKAGE_FACE_LANDMARKER_ANNOTATION_ACTIVE=" + B(_runner != null && _runner.renderDebugLandmarkAnnotations),
            "RAW_MEDIAPIPE_CALLBACK_COUNT=" + rawCount,
            "RAW_RESULT_ABSENT_COUNT=" + rawAbsent,
            "RAW_LATEST_OBSERVER_OVERWRITE_COUNT=" + rawOverwrite,
            "RAW_DUPLICATE_TIMESTAMP_COUNT=" + rawDuplicate,
            "RAW_OUT_OF_ORDER_TIMESTAMP_COUNT=" + rawOutOfOrder,
            "RAW_ABNORMAL_SOURCE_GAP_COUNT=" + rawGap,
            "RAW_AUX_ONLY_WHILE_IE_PRIMARY_COUNT=" + rawAux,
            "RAW_TO_RUNNER_MEDIA_PIPE_HANDOFF_COUNT=" + handoffCount,
            "HANDOFF_IDENTITY_MISMATCH_COUNT=" + handoffMismatch,
            "HANDOFF_DUPLICATE_TIMESTAMP_COUNT=" + handoffDuplicate,
            "HANDOFF_OUT_OF_ORDER_TIMESTAMP_COUNT=" + handoffOutOfOrder,
            "INTENTIONAL_LATEST_HANDOFF_OVERWRITE_COUNT=" + handoffOverwrite,
            "RIGHT_SIDE_CONSUME_COUNT=" + _consumeCount,
            "RIGHT_SIDE_CONSUME_MEDIAPIPE_COUNT=" + _consumeMediaPipeCount,
            "RIGHT_SIDE_CONSUME_INFERENCE_ENGINE_COUNT=" + _consumeInferenceEngineCount,
            "RIGHT_SIDE_CONSUME_OTHER_COUNT=" + _consumeOtherCount,
            "RIGHT_SIDE_CONSUME_IDENTITY_MISMATCH_COUNT=" + _consumeIdentityMismatchCount,
            "RIGHT_SIDE_CONSUME_DUPLICATE_TIMESTAMP_COUNT=" + _consumeDuplicateTimestampCount,
            "RIGHT_SIDE_CONSUME_OUT_OF_ORDER_TIMESTAMP_COUNT=" + _consumeOutOfOrderTimestampCount,
            "RIGHT_SIDE_PRESENTATION_ELIGIBLE_COUNT=" + _presentationCount,
            "RIGHT_SIDE_PRESENTATION_DUPLICATE_TIMESTAMP_COUNT=" + _presentationDuplicateTimestampCount,
            "RIGHT_SIDE_PRESENTATION_OUT_OF_ORDER_TIMESTAMP_COUNT=" + _presentationOutOfOrderTimestampCount,
            "SEGMENT_BOUNDARY_EVENT_COUNT=" + _segmentBoundaryCount,
            "SEGMENT_CORRELATION_ROW_COUNT=" + _correlationRowCount,
            "LAST_RAW_SEQUENCE=" + raw.sequence,
            "LAST_RAW_TIMESTAMP=" + raw.timestamp,
            "LAST_RAW_FINGERPRINT=" + raw.fingerprint,
            "LAST_HANDOFF_SEQUENCE=" + handoff.sequence,
            "LAST_HANDOFF_TIMESTAMP=" + handoff.timestamp,
            "LAST_HANDOFF_FINGERPRINT=" + handoff.fingerprint,
            "LAST_CONSUME_TIMESTAMP=" + _latestConsume.timestamp,
            "LAST_CONSUME_FINGERPRINT=" + _latestConsume.fingerprint,
            "LAST_CONSUME_PROVIDER=" + (_latestConsume.provider ?? string.Empty),
            "LAST_CONSUME_BACKEND=" + _latestConsume.backend,
            "LAST_CONSUME_PROVIDER_SOURCE_FRAME_ID=" + _latestConsume.providerSourceFrameId,
            "LAST_PRESENTATION_TIMESTAMP=" + _latestPresentation.timestamp,
            "LAST_PRESENTATION_DECISION_VALID=" + B(_latestPresentationDecision.valid),
            "LAST_PRESENTATION_DECISION_SEQUENCE=" + _latestPresentationDecision.sequence,
            "LAST_PRESENTATION_DECISION_REASON=" + _latestPresentationDecision.reason,
            "LAST_PRESENTATION_DECISION_TIMESTAMP=" + _latestPresentationDecision.semanticTimestamp,
            "LAST_PRESENTATION_DECISION_CANONICAL_FRAME_ID=" + _latestPresentationDecision.canonicalFrameId,
            "LAST_PRESENTATION_DECISION_PROVIDER=" + (_latestPresentationDecision.providerId ?? string.Empty),
            "LAST_PRESENTATION_DECISION_BACKEND=" + _latestPresentationDecision.backend,
            "LAST_PRESENTATION_DECISION_COMMITTED_TIMESTAMP=" + _latestPresentationDecision.committedSemanticTimestamp,
            "LAST_PRESENTATION_DECISION_COMMITTED_CANONICAL_FRAME_ID=" + _latestPresentationDecision.committedCanonicalFrameId,
            "LAST_PRESENTATION_DECISION_CONSUME_RELATION=" +
                PresentationDecisionConsumeRelation(
                    _latestPresentationDecision,
                    _latestConsume),
            "RAW_TO_CONSUME_AGE_MS_LATEST=" + F(_rawToConsumeAgeMsLatest),
            "RAW_TO_CONSUME_AGE_MS_MAXIMUM=" + F(_rawToConsumeAgeMsMaximum),
            "RAW_GAP_RULE=max(250ms,3x_previous_interval_ema)",
            "SOURCE_RELATION=" + (_consumeCount == 0L
                ? "NO_RIGHT_SIDE_CONSUME_OBSERVED"
                : _consumeInferenceEngineCount > 0L
                    ? "RIGHT_SIDE_USED_INFERENCE_ENGINE_NOT_RAW_MEDIAPIPE"
                    : "RIGHT_SIDE_MEDIA_PIPE_RELATION_OBSERVED")
        };
        File.WriteAllLines(_summaryPath, lines, new UTF8Encoding(false));
    }

    private static Sample BuildSample(FaceLandmarkerResult result, long sequence, long timestamp,
        long hostTicks, ref float[] previousSelected)
    {
        if (result.faceLandmarks == null || result.faceLandmarks.Count == 0 ||
            result.faceLandmarks[0].landmarks == null)
            return new Sample { sequence = sequence, timestamp = timestamp, hostTicks = hostTicks };

        var landmarks = result.faceLandmarks[0].landmarks;
        int count = landmarks.Count;
        if (count <= FingerprintIndices[FingerprintIndices.Length - 1])
            return new Sample { sequence = sequence, timestamp = timestamp, hostTicks = hostTicks };

        float[] selected = new float[FingerprintIndices.Length * 2];
        for (int i = 0; i < FingerprintIndices.Length; i++)
        {
            int index = Math.Min(FingerprintIndices[i], count - 1);
            if (index < 0) break;
            selected[i * 2] = landmarks[index].x;
            selected[i * 2 + 1] = landmarks[index].y;
        }
        return FinishSample(selected, count, sequence, timestamp, hostTicks, ref previousSelected);
    }

    private static Sample BuildSample(Vector2[] landmarks, int count, long timestamp, long hostTicks,
        ref float[] previousSelected)
    {
        if (landmarks == null || count <= 0)
            return new Sample { timestamp = timestamp, hostTicks = hostTicks };
        int safeCount = Math.Min(count, landmarks.Length);
        if (safeCount <= FingerprintIndices[FingerprintIndices.Length - 1])
            return new Sample { timestamp = timestamp, hostTicks = hostTicks };

        float[] selected = new float[FingerprintIndices.Length * 2];
        for (int i = 0; i < FingerprintIndices.Length; i++)
        {
            int index = Math.Min(FingerprintIndices[i], safeCount - 1);
            selected[i * 2] = landmarks[index].x;
            selected[i * 2 + 1] = landmarks[index].y;
        }
        return FinishSample(selected, safeCount, 0L, timestamp, hostTicks, ref previousSelected);
    }

    private static Sample FinishSample(float[] selected, int count, long sequence, long timestamp,
        long hostTicks, ref float[] previousSelected)
    {
        bool hadPrevious = previousSelected != null;
        float maximum = 0f;
        if (hadPrevious)
        {
            for (int i = 0; i < selected.Length; i += 2)
            {
                double dx = selected[i] - previousSelected[i];
                double dy = selected[i + 1] - previousSelected[i + 1];
                float delta = (float)Math.Sqrt(dx * dx + dy * dy);
                if (delta > maximum) maximum = delta;
            }
        }

        ulong fingerprint = 1469598103934665603UL;
        fingerprint = Mix(fingerprint, (ulong)count);
        for (int i = 0; i < selected.Length; i++)
            fingerprint = Mix(fingerprint, unchecked((ulong)(long)Math.Round(selected[i] * 1000000.0)));

        if (previousSelected == null) previousSelected = new float[selected.Length];
        Array.Copy(selected, previousSelected, selected.Length);

        return new Sample
        {
            valid = true,
            sequence = sequence,
            timestamp = timestamp,
            hostTicks = hostTicks,
            fingerprint = fingerprint,
            count = count,
            maxDelta = hadPrevious ? maximum : 0f
        };
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value;
        return hash * 1099511628211UL;
    }

    private static void ObservePeak(Sample sample, ref float peak, ref long peakTimestamp)
    {
        if (!sample.valid || sample.maxDelta <= peak) return;
        peak = sample.maxDelta;
        peakTimestamp = sample.timestamp;
    }

    private static long ReadRawCount()
    {
        lock (Sync) return _rawCallbackCount;
    }

    private StreamWriter CreateWriter(string name, string header)
    {
        var writer = new StreamWriter(Path.Combine(_outputDirectory, name), false, new UTF8Encoding(false));
        writer.WriteLine(header);
        writer.Flush();
        return writer;
    }

    private void CloseWriters()
    {
        if (_aggregateWriter != null) { _aggregateWriter.Flush(); _aggregateWriter.Dispose(); _aggregateWriter = null; }
        if (_segmentBoundaryWriter != null) { _segmentBoundaryWriter.Flush(); _segmentBoundaryWriter.Dispose(); _segmentBoundaryWriter = null; }
        if (_correlationWriter != null) { _correlationWriter.Flush(); _correlationWriter.Dispose(); _correlationWriter = null; }
    }

    private string ResolveOutputDirectory()
    {
        string configured = Environment.GetEnvironmentVariable(OutputDirectoryEnvironment);
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());
        return Path.Combine(Application.persistentDataPath, "KiwiH1LandmarkerBoundary");
    }

    private string SegmentName()
    {
        double elapsed = Time.realtimeSinceStartupAsDouble - _startedRealtime;
        if (elapsed < WarmupEndSeconds) return "WARMUP";
        if (elapsed < NeutralStillnessAEndSeconds) return "NEUTRAL_STILLNESS_A";
        if (elapsed < SlowYawEndSeconds) return "SLOW_YAW";
        if (elapsed < SlowPitchEndSeconds) return "SLOW_PITCH";
        if (elapsed < SlowRollEndSeconds) return "SLOW_ROLL";
        if (elapsed < NeutralStillnessBEndSeconds) return "NEUTRAL_STILLNESS_B";
        return "COMPLETE";
    }

    private string SegmentInstruction()
    {
        string segment = SegmentName();
        if (segment == "WARMUP") return "準備: 正面で待機";
        if (segment == "NEUTRAL_STILLNESS_A") return "正面・実質静止 (最優先評価区間)";
        if (segment == "SLOW_YAW") return "ゆっくり左右を向く";
        if (segment == "SLOW_PITCH") return "ゆっくり上下を向く";
        if (segment == "SLOW_ROLL") return "ゆっくり左右へ傾ける";
        if (segment == "NEUTRAL_STILLNESS_B") return "正面へ戻り実質静止";
        return "計測完了";
    }

    private static double HostTicksToMilliseconds(long ticks)
    {
        return ticks > 0L ? ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency : -1.0;
    }

    private static double SecondsToMilliseconds(float seconds)
    {
        return float.IsNaN(seconds) || float.IsInfinity(seconds)
            ? -1.0
            : seconds * 1000.0;
    }

    private static string Csv(string value)
    {
        value = value ?? string.Empty;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string PresentationCsv(PresentationObservation observation)
    {
        return B(observation.valid) + "," +
            observation.observationHostTicks + "," +
            observation.committedSemanticTimestamp + "," +
            observation.committedCanonicalFrameId + "," +
            Csv(observation.canonicalCorrelationStatus) + "," +
            B(observation.providerMetadataExactMatch) + "," +
            B(observation.normalizationValid) + "," +
            Csv(observation.providerId) + "," +
            Csv(observation.backend.ToString()) + "," +
            observation.providerSourceFrameId;
    }

    private static string PresentationDecisionCsv(
        KiwiFacePartTextureTransaction.PresentationDecisionSnapshot decision)
    {
        return B(decision.valid) + "," +
            decision.sequence + "," +
            decision.observationHostTicks + "," +
            B(decision.candidateReceived) + "," +
            B(decision.accepted) + "," +
            Csv(decision.reason.ToString()) + "," +
            decision.semanticTimestamp + "," +
            decision.canonicalFrameId + "," +
            Csv(decision.providerId) + "," +
            Csv(decision.backend.ToString()) + "," +
            decision.providerSourceFrameId + "," +
            decision.providerSourceTimestamp + "," +
            decision.sourceHostTicks + "," +
            decision.arrivalHostTicks + "," +
            F(decision.sourceAgeMilliseconds) + "," +
            F(decision.arrivalAgeMilliseconds) + "," +
            B(decision.semanticFreshnessEvaluated) + "," +
            F(decision.semanticFreshnessAgeMilliseconds) + "," +
            decision.cameraGeneration + "," +
            decision.providerGeneration + "," +
            decision.trackingSessionGeneration + "," +
            B(decision.leftEyeAccepted) + "," +
            B(decision.rightEyeAccepted) + "," +
            B(decision.mouthAccepted) + "," +
            decision.committedSemanticTimestamp + "," +
            decision.committedCanonicalFrameId;
    }

    private static string PresentationDecisionConsumeRelation(
        KiwiFacePartTextureTransaction.PresentationDecisionSnapshot decision,
        Sample consume)
    {
        if (!consume.valid) return "NO_CONSUME_OBSERVED";
        if (!decision.valid) return "NO_DECISION_OBSERVED";
        if (decision.semanticTimestamp == consume.timestamp)
            return "DECISION_FOR_CURRENT_CONSUME";
        if (decision.semanticTimestamp < consume.timestamp)
            return "CURRENT_CONSUME_NOT_RECEIVED_BY_DECISION_OWNER";
        return "DECISION_NEWER_THAN_CURRENT_CONSUME";
    }

    private static string GeometryDiagnosticCsv(
        FaceLandmarkerRunner.FacePartGeometrySnapshotDiagnostic diagnostic,
        KiwiFacePartTextureTransaction.PresentationDecisionSnapshot decision,
        Sample consume)
    {
        string correlationStatus = GeometryDiagnosticCorrelationStatus(
            diagnostic,
            decision);
        bool targetDecision =
            PresentationDecisionConsumeRelation(decision, consume) ==
                "DECISION_FOR_CURRENT_CONSUME" &&
            decision.reason == KiwiFacePartTextureTransaction.
                PresentationDecisionReason.RejectedGeometrySnapshotIdentity;
        bool exactCorrelation =
            correlationStatus ==
                "EXACT_SEMANTIC_TIMESTAMP_CANONICAL_FRAME_ID_MATCH";
        string firstFailure = !targetDecision
            ? "NOT_TARGET_GEOMETRY_REJECT_DECISION"
            : !exactCorrelation
                ? "GEOMETRY_DIAGNOSTIC_NOT_CORRELATED"
                : diagnostic.firstFailedPredicate.ToString();
        string pipelineSubBoundary = !targetDecision
            ? "NOT_TARGET_GEOMETRY_REJECT_DECISION"
            : !exactCorrelation
                ? "GEOMETRY_DIAGNOSTIC_NOT_CORRELATED"
                : GeometryPipelineSubBoundary(diagnostic);

        KiwiFaceGeometryTransactionService.PipelineStateSnapshot state =
            diagnostic.pipelineState;
        ulong expectedFrameId = diagnostic.expectedProviderSourceFrameId;
        bool expectedInAccepted =
            state.acceptedSnapshotExists &&
            state.acceptedFrameId == expectedFrameId;
        bool expectedInFlight =
            state.inFlightExists &&
            state.inFlightFrameId == expectedFrameId;
        bool expectedInPending =
            state.latestPendingExists &&
            state.latestPendingFrameId == expectedFrameId;
        bool expectedInCompletion =
            state.callbackCompletionExists &&
            state.callbackCompletionFrameId == expectedFrameId;

        return B(diagnostic.valid) + "," +
            diagnostic.observationHostTicks + "," +
            diagnostic.semanticTimestamp + "," +
            diagnostic.canonicalFrameId + "," +
            Csv(correlationStatus) + "," +
            Csv(firstFailure) + "," +
            Csv(pipelineSubBoundary) + "," +
            diagnostic.expectedProviderSourceFrameId + "," +
            diagnostic.expectedSourceHostTicks + "," +
            diagnostic.expectedCameraGeneration + "," +
            diagnostic.expectedTrackingSessionGeneration + "," +
            diagnostic.expectedProviderGeneration + "," +
            diagnostic.expectedModelGeneration + "," +
            Csv(diagnostic.expectedBackend.ToString()) + "," +
            diagnostic.expectedSemanticFrameWidth + "," +
            diagnostic.expectedSemanticFrameHeight + "," +
            B(diagnostic.productServiceAvailable) + "," +
            B(diagnostic.semanticDimensionsValid) + "," +
            B(diagnostic.canonicalFrameAvailable) + "," +
            B(diagnostic.canonicalFrameValid) + "," +
            B(diagnostic.canonicalSemanticPresent) + "," +
            B(diagnostic.semanticTimestampMatches) + "," +
            B(diagnostic.rigidStateValid) + "," +
            B(diagnostic.rigidValid) + "," +
            B(diagnostic.rigidTimestampMatches) + "," +
            B(diagnostic.rigidFrameIdPresent) + "," +
            B(diagnostic.normalizationValid) + "," +
            B(diagnostic.providerSourceFrameIdPresent) + "," +
            B(diagnostic.canonicalBackendMatches) + "," +
            B(diagnostic.matchedSubmissionTimingPresent) + "," +
            B(diagnostic.sourceHostTicksPresent) + "," +
            B(diagnostic.acceptedSnapshotAvailable) + "," +
            B(diagnostic.sourceFrameIdMatches) + "," +
            B(diagnostic.sourceHostTicksMatches) + "," +
            B(diagnostic.cameraGenerationMatches) + "," +
            B(diagnostic.trackingSessionGenerationMatches) + "," +
            B(diagnostic.providerGenerationMatches) + "," +
            B(diagnostic.modelGenerationMatches) + "," +
            B(diagnostic.acceptedBackendMatches) + "," +
            B(diagnostic.semanticWidthMatches) + "," +
            B(diagnostic.semanticHeightMatches) + "," +
            B(diagnostic.accepted) + "," +
            B(state.acceptedSnapshotExists) + "," +
            state.acceptedGraphRunId + "," +
            state.acceptedStreamId + "," +
            state.acceptedFrameId + "," +
            state.acceptedSourceHostTicks + "," +
            state.acceptedCameraGeneration + "," +
            state.acceptedTrackingSessionGeneration + "," +
            state.acceptedProviderGeneration + "," +
            state.acceptedModelGeneration + "," +
            Csv(state.acceptedBackend.ToString()) + "," +
            state.acceptedSemanticFrameWidth + "," +
            state.acceptedSemanticFrameHeight + "," +
            B(state.inFlightExists) + "," +
            state.inFlightFrameId + "," +
            state.inFlightSourceHostTicks + "," +
            B(state.latestPendingExists) + "," +
            state.latestPendingFrameId + "," +
            state.latestPendingSourceHostTicks + "," +
            B(state.callbackCompletionExists) + "," +
            state.callbackCompletionFrameId + "," +
            Csv(CompletionStatusName(state.callbackCompletionStatus)) + "," +
            B(state.shutdownRequested) + "," +
            B(state.resetRequested) + "," +
            state.lastAcceptedGeometryFrameId + "," +
            B(expectedInAccepted) + "," +
            B(expectedInFlight) + "," +
            B(expectedInPending) + "," +
            B(expectedInCompletion);
    }

    private static string GeometryDiagnosticCorrelationStatus(
        FaceLandmarkerRunner.FacePartGeometrySnapshotDiagnostic diagnostic,
        KiwiFacePartTextureTransaction.PresentationDecisionSnapshot decision)
    {
        if (!diagnostic.valid)
        {
            return "GEOMETRY_DIAGNOSTIC_UNAVAILABLE";
        }

        if (
            !decision.valid ||
            diagnostic.semanticTimestamp != decision.semanticTimestamp ||
            diagnostic.canonicalFrameId != decision.canonicalFrameId)
        {
            return "GEOMETRY_DIAGNOSTIC_NOT_CORRELATED";
        }

        return "EXACT_SEMANTIC_TIMESTAMP_CANONICAL_FRAME_ID_MATCH";
    }

    private static string GeometryPipelineSubBoundary(
        FaceLandmarkerRunner.FacePartGeometrySnapshotDiagnostic diagnostic)
    {
        KiwiFaceGeometryTransactionService.PipelineStateSnapshot state =
            diagnostic.pipelineState;
        ulong expectedFrameId = diagnostic.expectedProviderSourceFrameId;

        if (!state.acceptedSnapshotExists)
        {
            return "A_ACCEPTED_SNAPSHOT_UNAVAILABLE";
        }

        if (diagnostic.accepted)
        {
            return "ACCEPTED";
        }

        if (
            state.acceptedFrameId == expectedFrameId &&
            diagnostic.firstFailedPredicate !=
                FaceLandmarkerRunner.FacePartGeometrySnapshotPredicate.
                    SourceFrameIdMismatch)
        {
            return "B_ACCEPTED_SNAPSHOT_IDENTITY_FIELD_MISMATCH";
        }

        if (
            state.callbackCompletionExists &&
            state.callbackCompletionFrameId == expectedFrameId)
        {
            return "E_CURRENT_EXPECTED_FRAME_IN_COMPLETION_NOT_PUBLISHED";
        }

        if (
            state.inFlightExists &&
            state.inFlightFrameId == expectedFrameId)
        {
            return "C_CURRENT_EXPECTED_FRAME_IN_FLIGHT";
        }

        if (
            state.latestPendingExists &&
            state.latestPendingFrameId == expectedFrameId)
        {
            return "D_CURRENT_EXPECTED_FRAME_IN_LATEST_PENDING";
        }

        bool expectedFrameAnywhere =
            state.acceptedFrameId == expectedFrameId ||
            state.inFlightFrameId == expectedFrameId ||
            state.latestPendingFrameId == expectedFrameId ||
            state.callbackCompletionFrameId == expectedFrameId;
        return expectedFrameAnywhere
            ? "B_ACCEPTED_SNAPSHOT_IDENTITY_FIELD_MISMATCH"
            : "F_CURRENT_EXPECTED_FRAME_NOT_IN_PIPELINE";
    }

    private static string CompletionStatusName(int status)
    {
        if (status == 1) return "Valid";
        if (status == 2) return "EmptyPacket";
        if (status == 3) return "InvalidPose";
        if (status == 4) return "ParseFailure";
        return "None";
    }

    private static string F(double value) { return value.ToString("R", Invariant); }
    private static string B(bool value) { return value ? "1" : "0"; }
}
#endif
