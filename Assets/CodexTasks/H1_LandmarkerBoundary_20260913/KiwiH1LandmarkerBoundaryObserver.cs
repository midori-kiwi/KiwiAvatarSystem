#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// DIAGNOSTIC_ONLY, O(1), latest-only observer for the right-side
/// MATCHED LANDMARK DEBUG path. It never publishes tracking state, changes
/// cadence/backend/settings or queues Product samples. The only GPU readback
/// is a bounded DEVELOPMENT_BUILD/UNITY_EDITOR diagnostic snapshot taken at
/// the existing right-overlay draw-input boundary; it never feeds Product state.
/// </summary>
[DefaultExecutionOrder(40000)]
public sealed class KiwiH1LandmarkerBoundaryObserver : MonoBehaviour
{
    private const string EnableEnvironment = "KIWI_H1_BOUNDARY_RUNTIME";
    private const string OutputDirectoryEnvironment = "KIWI_H1_BOUNDARY_OUTPUT";
    private const int GpuDrawInputWidth = 384;
    private const int GpuDrawInputHeight = 384;
    private const int GpuDrawInputBytesPerPixel = 4;
    private const int GpuDrawInputByteCount =
        GpuDrawInputWidth * GpuDrawInputHeight * GpuDrawInputBytesPerPixel;
    private const int GpuDrawInputSlotCount = 8;
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
        public float[] selected;
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

    private struct OverlayVisibilityState
    {
        public bool valid;
        public int instanceId;
        public bool activeInHierarchy;
        public bool enabled;
        public bool isActiveAndEnabled;
        public bool visible;
        public int screenWidth;
        public int screenHeight;
        public bool reflectionReady;
        public bool headerStyleCreated;
        public bool previewEpochMatched;
        public long previewSemanticTimestamp;
        public long lastSemanticTimestamp;
    }

    private struct InferenceDecodeObservation
    {
        public bool rawValid;
        public bool valid;
        public long sequence;
        public ulong sourceFrameId;
        public bool sourceFrameIdIsNativeSequence;
        public int sourceGeneration;
        public int trackerGeneration;
        public int cameraGeneration;
        public int trackingSessionGeneration;
        public int width;
        public int height;
        public long sourceHostTicks;
        public long arrivalHostTicks;
        public string status;
        public Matrix4x4 cropMatrix;
        public Vector3 rawP1;
        public Vector3 rawP33;
        public Vector3 rawP152;
        public Vector3 rawP263;
        public Vector3 rawP454;
        public Vector3 normalizedP1;
        public Vector3 normalizedP33;
        public Vector3 normalizedP152;
        public Vector3 normalizedP263;
        public Vector3 normalizedP454;
    }

    private readonly struct AcceptedPublicationObservation
    {
        public readonly bool valid;
        public readonly ulong publicationSequence;
        public readonly long runnerTimestamp;
        public readonly InferenceDecodeObservation decode;

        public AcceptedPublicationObservation(
            ulong publicationSequence,
            long runnerTimestamp,
            InferenceDecodeObservation decode)
        {
            valid = true;
            this.publicationSequence = publicationSequence;
            this.runnerTimestamp = runnerTimestamp;
            this.decode = decode;
        }
    }

    private struct Full468Comparison
    {
        public bool compared;
        public bool exact;
        public int firstMismatchPoint;
        public int firstMismatchAxis;
        public int expectedBits;
        public float expectedValue;
        public int actualBits;
        public float actualValue;
        public double maxAbsResidual;
    }

    private struct Full468PublicationSnapshot
    {
        public bool valid;
        public ulong publicationSequence;
        public long runnerTimestamp;
        public long sourceHostTicks;
        public long decodeSequence;
        public int pointCount;
        public bool payloadAvailable;
        public Full468Comparison a1Store;
        public Full468Comparison storePublication;
    }

    private readonly struct VisualIdentitySnapshot
    {
        public readonly bool valid;
        public readonly long visualSequence;
        public readonly int unityFrame;
        public readonly int canonicalUnityFrame;
        public readonly long observationHostTicks;
        public readonly long semanticTimestamp;
        public readonly ulong canonicalFrameId;
        public readonly string providerId;
        public readonly KiwiTrackingBackend backend;
        public readonly bool normalizationValid;
        public readonly ulong providerSourceFrameId;
        public readonly bool acceptedPublicationExact;
        public readonly AcceptedPublicationObservation acceptedPublication;

        public VisualIdentitySnapshot(
            long visualSequence,
            int unityFrame,
            KiwiTrackingFrame canonicalFrame,
            long semanticTimestamp,
            long observationHostTicks,
            bool acceptedPublicationExact,
            AcceptedPublicationObservation acceptedPublication)
        {
            valid = true;
            this.visualSequence = visualSequence;
            this.unityFrame = unityFrame;
            canonicalUnityFrame = canonicalFrame.unityFrame;
            this.observationHostTicks = observationHostTicks;
            this.semanticTimestamp = semanticTimestamp;
            canonicalFrameId = canonicalFrame.canonicalFrameId;
            providerId = canonicalFrame.providerId ?? string.Empty;
            backend = canonicalFrame.rigid.backend;
            normalizationValid = canonicalFrame.normalization.valid;
            providerSourceFrameId = canonicalFrame.normalization.valid
                ? canonicalFrame.normalization.providerSourceFrameId
                : 0UL;
            this.acceptedPublicationExact = acceptedPublicationExact;
            this.acceptedPublication = acceptedPublication;
        }
    }

    private readonly struct GpuDrawInputIdentity
    {
        public readonly long requestSequence;
        public readonly long requestHostTicks;
        public readonly string segment;
        public readonly long visualSequence;
        public readonly int unityFrame;
        public readonly long semanticTimestamp;
        public readonly ulong canonicalFrameId;
        public readonly ulong publicationSequence;
        public readonly ulong sourceFrameId;
        public readonly string sourceFrameIdDomain;
        public readonly int sourceGeneration;
        public readonly long sourceHostTicks;
        public readonly int textureInstanceId;
        public readonly int width;
        public readonly int height;
        public readonly string format;
        public readonly string graphicsFormat;
        public readonly bool sourceReadable;
        public readonly string cpuSha256;

        public GpuDrawInputIdentity(
            long requestSequence,
            long requestHostTicks,
            string segment,
            VisualIdentitySnapshot visual,
            Texture2D texture,
            string cpuSha256)
        {
            this.requestSequence = requestSequence;
            this.requestHostTicks = requestHostTicks;
            this.segment = segment ?? string.Empty;
            visualSequence = visual.visualSequence;
            unityFrame = visual.unityFrame;
            semanticTimestamp = visual.semanticTimestamp;
            canonicalFrameId = visual.canonicalFrameId;

            AcceptedPublicationObservation publication =
                visual.acceptedPublication;
            InferenceDecodeObservation decode =
                publication.decode;

            publicationSequence =
                visual.acceptedPublicationExact
                    ? publication.publicationSequence
                    : 0UL;
            sourceFrameId =
                visual.acceptedPublicationExact
                    ? decode.sourceFrameId
                    : 0UL;
            sourceFrameIdDomain =
                !visual.acceptedPublicationExact
                    ? string.Empty
                    : decode.sourceFrameIdIsNativeSequence
                        ? "NATIVE_PRESENTED_SEQUENCE"
                        : "RUNNER_FRESH_SOURCE_GENERATION_FALLBACK";
            sourceGeneration =
                visual.acceptedPublicationExact
                    ? decode.sourceGeneration
                    : 0;
            sourceHostTicks =
                visual.acceptedPublicationExact
                    ? decode.sourceHostTicks
                    : 0L;
            textureInstanceId =
                texture != null
                    ? texture.GetInstanceID()
                    : 0;
            width =
                texture != null
                    ? texture.width
                    : 0;
            height =
                texture != null
                    ? texture.height
                    : 0;
            format =
                texture != null
                    ? texture.format.ToString()
                    : string.Empty;
            graphicsFormat =
                texture != null
                    ? texture.graphicsFormat.ToString()
                    : string.Empty;
            sourceReadable =
                texture != null &&
                texture.isReadable;
            this.cpuSha256 =
                cpuSha256 ?? string.Empty;
        }
    }

    private sealed class GpuDrawInputSlot
    {
        public Texture2D snapshot;
        public readonly byte[] cpuBytes =
            new byte[GpuDrawInputByteCount];
        public readonly byte[] gpuBytes =
            new byte[GpuDrawInputByteCount];
        public bool inFlight;
        public long requestSequence;
    }

    public struct RoiStateSnapshot
    {
        public float centerX;
        public float centerYBottom;
        public float width;
        public float height;
        public float rollRadians;
        public bool hasRegion;
        public int anchorRevision;
        public int externalAnchorEpoch;
        public long lastWriterSequence;
        public string lastWriterKind;
        public long lastWriterSourceHostTicks;
        public int lastWriterSourceGeneration;
        public int lastWriterCameraGeneration;
        public int lastWriterTrackingSessionGeneration;
    }

    public struct RoiSampleIdentity
    {
        public long externalAnchorObserverSequence;
        public ulong sourceFrameId;
        public bool sourceFrameIdIsNativeSequence;
        public int sourceGeneration;
        public int trackerGeneration;
        public int cameraGeneration;
        public int trackingSessionGeneration;
        public long sourceHostTicks;
        public long callbackTimestamp;
        public long arrivalHostTicks;
    }

    public struct RoiLandmarkSummary
    {
        public bool valid;
        public string sourceKind;
        public int landmarkCount;
        public float maxA0XyStepOverInput;
        public int maxA0XyStepIndex;
        public float maxA0XyzStepOverInput;
        public int maxA0XyzStepIndex;
        public float maxA1XyStep;
        public int maxA1XyStepIndex;
        public float minXPixels;
        public int minXIndex;
        public Vector2 minXPoint;
        public float maxXPixels;
        public int maxXIndex;
        public Vector2 maxXPoint;
        public float minYPixelsBottom;
        public int minYIndex;
        public Vector2 minYPoint;
        public float maxYPixelsBottom;
        public int maxYIndex;
        public Vector2 maxYPoint;
        public float boxWidthPixels;
        public float boxHeightPixels;
        public float squareSidePixels;
        public float targetCenterX;
        public float targetCenterYBottom;
        public float targetWidth;
        public float targetHeight;
        public float targetRollRadians;
        public Vector2 eye33;
        public Vector2 eye263;
    }

    public struct RoiDecisionMetrics
    {
        public bool valid;
        public long applyHostTicks;
        public bool force;
        public bool hasRegion;
        public bool regionRetentionActive;
        public float centerDistancePixels;
        public float centerThresholdPixels;
        public float widthRatioDelta;
        public float heightRatioDelta;
        public float rollDeltaDegrees;
        public float sizeRatioThreshold;
        public float rollThresholdDegrees;
        public string decision;
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
    private static long _inferenceDecodeSequence;
    private static long _inferenceDecodeCount;
    private static long _inferenceDecodeValidCount;
    private static long _inferenceDecodeRejectCount;
    private static long _inferenceStoreAttemptCount;
    private static long _inferenceStoreAcceptedCount;
    private static long _inferenceStoreRejectedCount;
    private static long _inferenceStoreCorrelationMismatchCount;
    private static InferenceDecodeObservation _latestValidInferenceDecode;
    private static AcceptedPublicationObservation _latestAcceptedPublication;
    private static readonly Vector3[] PreviousAcceptedWinnerA0 =
        new Vector3[KiwiInferenceFaceTracker.BaseLandmarkCount];
    private static readonly Vector3[] PreviousAcceptedWinnerA1 =
        new Vector3[KiwiInferenceFaceTracker.BaseLandmarkCount];
    private static readonly Vector3[] LatestValidInferenceA0 =
        new Vector3[KiwiInferenceFaceTracker.BaseLandmarkCount];
    private static readonly Vector3[] LatestValidInferenceA1 =
        new Vector3[KiwiInferenceFaceTracker.BaseLandmarkCount];
    private static readonly Vector2[] LatestAcceptedPublicationFull468 =
        new Vector2[KiwiInferenceFaceTracker.BaseLandmarkCount];
    private static long _latestFullDecodeSequence;
    private static Full468PublicationSnapshot _latestFull468Publication;
    private static long _acceptedFull468RowCount;
    private static bool _hasPreviousAcceptedWinner;
    private static long _roiWriterEventCount;
    private static long _roiScheduleCount;

    private FaceLandmarkerRunner _runner;
    private KiwiFrameComparisonOverlay _overlay;
    private KiwiTrackingProviderHub _hub;
    private KiwiTrackingContinuityState _continuity;
    private FieldInfo _overlayTimestampField;
    private FieldInfo _overlayLandmarksField;
    private FieldInfo _overlayCountField;
    private FieldInfo _overlayHeaderStyleField;
    private FieldInfo _overlayPreviewEpochMatchedField;
    private FieldInfo _overlayPreviewSemanticTimestampField;
    private StreamWriter _aggregateWriter;
    private StreamWriter _segmentBoundaryWriter;
    private StreamWriter _correlationWriter;
    private StreamWriter _exactBoundaryWriter;
    private StreamWriter _inferenceDecodeWriter;
    private StreamWriter _inferencePublicationWriter;
    private StreamWriter _acceptedFull468Writer;
    private StreamWriter _roiWriterEventsWriter;
    private StreamWriter _roiScheduleLinkWriter;
    private StreamWriter _visualIdentityWriter;
    private StreamWriter _full468BoundaryWriter;
    private StreamWriter _gpuDrawInputWriter;
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
    private long _visualIdentitySequence;
    private VisualIdentitySnapshot _latestVisualIdentity;
    private GUIStyle _visualIdentityStyle;
    private bool _quitting;
    private bool _visibilityGateEvaluated;
    private bool _visibilityGatePassed;
    private bool _hasOverlayVisibilityState;
    private OverlayVisibilityState _lastOverlayVisibilityState;
    private bool _previewTimestampAdvanceLogged;
    private int _previewEpochStateMask;
    private long _overlayVisibilityLogCount;
    private GpuDrawInputSlot[] _gpuDrawInputSlots;
    private long _gpuDrawInputRequestSequence;
    private long _lastGpuDrawInputVisualSequence;
    private int _gpuDrawInputInFlight;
    private int _gpuDrawInputMaxInFlight;
    private long _gpuDrawInputRequestedCount;
    private long _gpuDrawInputCompletedCount;
    private long _gpuDrawInputExactCount;
    private long _gpuDrawInputNonidenticalCount;
    private long _gpuDrawInputVerticalFlipOnlyCount;
    private long _gpuDrawInputReadbackErrorCount;
    private long _gpuDrawInputCoverageGapCount;
    private long _gpuDrawInputPoolExhaustedCount;
    private long _gpuDrawInputOutOfScopeCount;

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

                if (_latestRaw.valid && _latestRaw.sequence == rawSequence)
                {
                    Sample rawWithPublishedIdentity = _latestRaw;
                    rawWithPublishedIdentity.provider = "Runner/MediaPipe";
                    rawWithPublishedIdentity.backend = published.backend;
                    rawWithPublishedIdentity.providerSourceFrameId = published.frameId;
                    _latestRaw = rawWithPublishedIdentity;
                }
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
                !SelectedExactlyEqual(_latestRaw, handoff) ||
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

    internal static void ObserveRightOverlayGpuDrawInput(
        Texture2D overlayTexture,
        Color32[] cpuPixels,
        long semanticTimestamp,
        long observationHostTicks)
    {
        if (!_armed) return;

        KiwiH1LandmarkerBoundaryObserver instance =
            _activeInstance;
        if (instance == null) return;

        instance.CaptureGpuDrawInput(
            overlayTexture,
            cpuPixels,
            semanticTimestamp,
            observationHostTicks);
    }

    internal static void ObserveInferenceDecode(
        ulong sourceFrameId,
        bool sourceFrameIdIsNativeSequence,
        int sourceGeneration,
        int trackerGeneration,
        int cameraGeneration,
        int trackingSessionGeneration,
        int width,
        int height,
        long sourceHostTicks,
        long arrivalHostTicks,
        Matrix4x4 cropMatrix,
        Tensor<float> readableOutput,
        Vector3[] normalizedLandmarks,
        bool normalizedValid,
        string status)
    {
        if (!_armed) return;

        KiwiH1LandmarkerBoundaryObserver instance = _activeInstance;
        if (instance == null) return;

        bool rawValid =
            readableOutput != null &&
            readableOutput.shape.length >= 455 * 3;
        bool fullPayloadValid =
            readableOutput != null &&
            readableOutput.shape.length >=
                KiwiInferenceFaceTracker.BaseLandmarkCount * 3 &&
            normalizedLandmarks != null &&
            normalizedLandmarks.Length >=
                KiwiInferenceFaceTracker.BaseLandmarkCount;

        InferenceDecodeObservation observation =
            new InferenceDecodeObservation
            {
                rawValid = rawValid,
                valid = rawValid && normalizedValid &&
                    normalizedLandmarks != null &&
                    normalizedLandmarks.Length > 454,
                sourceFrameId = sourceFrameId,
                sourceFrameIdIsNativeSequence =
                    sourceFrameIdIsNativeSequence,
                sourceGeneration = sourceGeneration,
                trackerGeneration = trackerGeneration,
                cameraGeneration = cameraGeneration,
                trackingSessionGeneration = trackingSessionGeneration,
                width = width,
                height = height,
                sourceHostTicks = sourceHostTicks,
                arrivalHostTicks = arrivalHostTicks,
                status = status ?? string.Empty,
                cropMatrix = cropMatrix
            };

        if (rawValid)
        {
            observation.rawP1 = ReadRawTensorPoint(readableOutput, 1);
            observation.rawP33 = ReadRawTensorPoint(readableOutput, 33);
            observation.rawP152 = ReadRawTensorPoint(readableOutput, 152);
            observation.rawP263 = ReadRawTensorPoint(readableOutput, 263);
            observation.rawP454 = ReadRawTensorPoint(readableOutput, 454);
        }

        if (observation.valid)
        {
            observation.normalizedP1 = normalizedLandmarks[1];
            observation.normalizedP33 = normalizedLandmarks[33];
            observation.normalizedP152 = normalizedLandmarks[152];
            observation.normalizedP263 = normalizedLandmarks[263];
            observation.normalizedP454 = normalizedLandmarks[454];
        }

        lock (Sync)
        {
            observation.sequence = ++_inferenceDecodeSequence;
            _inferenceDecodeCount++;
            if (observation.valid) _inferenceDecodeValidCount++;
            else _inferenceDecodeRejectCount++;

            instance.WriteInferenceDecode(observation, rawValid);

            if (
                observation.valid &&
                fullPayloadValid &&
                IsInferenceDecodeNewer(
                    observation,
                    _latestValidInferenceDecode))
            {
                // Keep the full payload aligned with the exact same newest
                // decode selection used by the publication observer. A valid
                // but out-of-order completion must not replace this payload.
                for (int i = 0;
                     i < KiwiInferenceFaceTracker.BaseLandmarkCount;
                     i++)
                {
                    int offset = i * 3;
                    LatestValidInferenceA0[i] = new Vector3(
                        readableOutput[offset],
                        readableOutput[offset + 1],
                        readableOutput[offset + 2]);
                    LatestValidInferenceA1[i] = normalizedLandmarks[i];
                }
                _latestFullDecodeSequence = observation.sequence;
                _latestValidInferenceDecode = observation;
            }
        }
    }

    internal static void ObserveInferenceDecodeFailure(
        ulong sourceFrameId,
        bool sourceFrameIdIsNativeSequence,
        int sourceGeneration,
        int trackerGeneration,
        int cameraGeneration,
        int trackingSessionGeneration,
        int width,
        int height,
        long sourceHostTicks,
        long arrivalHostTicks,
        string status)
    {
        if (!_armed) return;

        KiwiH1LandmarkerBoundaryObserver instance = _activeInstance;
        if (instance == null) return;

        InferenceDecodeObservation observation =
            new InferenceDecodeObservation
            {
                valid = false,
                sourceFrameId = sourceFrameId,
                sourceFrameIdIsNativeSequence =
                    sourceFrameIdIsNativeSequence,
                sourceGeneration = sourceGeneration,
                trackerGeneration = trackerGeneration,
                cameraGeneration = cameraGeneration,
                trackingSessionGeneration = trackingSessionGeneration,
                width = width,
                height = height,
                sourceHostTicks = sourceHostTicks,
                arrivalHostTicks = arrivalHostTicks,
                status = status ?? string.Empty,
                cropMatrix = Matrix4x4.identity
            };

        lock (Sync)
        {
            observation.sequence = ++_inferenceDecodeSequence;
            _inferenceDecodeCount++;
            _inferenceDecodeRejectCount++;
            instance.WriteInferenceDecode(observation, false);
        }
    }

    internal static void ObserveInferenceRunnerPublication(
        long sourceHostTicks,
        long runnerTimestamp,
        long runnerArrivalHostTicks,
        bool accepted,
        ulong publicationSequence,
        string reason,
        Vector3[] storeInputLandmarks,
        Vector2[] publishedLandmarks,
        int publishedCount)
    {
        if (!_armed) return;

        KiwiH1LandmarkerBoundaryObserver instance = _activeInstance;
        if (instance == null) return;

        lock (Sync)
        {
            _inferenceStoreAttemptCount++;
            if (accepted) _inferenceStoreAcceptedCount++;
            else _inferenceStoreRejectedCount++;

            InferenceDecodeObservation decode =
                _latestValidInferenceDecode;

            bool decodeCorrelationExact =
                decode.valid &&
                decode.sourceHostTicks == sourceHostTicks;

            bool inputMatchesNormalized =
                decodeCorrelationExact &&
                storeInputLandmarks != null &&
                storeInputLandmarks.Length > 454 &&
                SelectedVector3ExactlyEqual(
                    decode,
                    storeInputLandmarks);

            bool publicationMatchesInput =
                accepted &&
                storeInputLandmarks != null &&
                storeInputLandmarks.Length > 454 &&
                publishedLandmarks != null &&
                publishedCount > 454 &&
                SelectedVector2ExactlyEqual(
                    storeInputLandmarks,
                    publishedLandmarks);

            bool fullDecodeCorrelationExact =
                decodeCorrelationExact &&
                _latestFullDecodeSequence == decode.sequence;
            Full468Comparison a1StoreFull =
                fullDecodeCorrelationExact &&
                storeInputLandmarks != null &&
                storeInputLandmarks.Length >=
                    KiwiInferenceFaceTracker.BaseLandmarkCount
                    ? CompareA1ToStoreFull468(storeInputLandmarks)
                    : UncomparedFull468();
            Full468Comparison storePublicationFull =
                accepted &&
                storeInputLandmarks != null &&
                storeInputLandmarks.Length >=
                    KiwiInferenceFaceTracker.BaseLandmarkCount &&
                publishedLandmarks != null &&
                publishedCount >=
                    KiwiInferenceFaceTracker.BaseLandmarkCount
                    ? CompareStoreToPublicationFull468(
                        storeInputLandmarks,
                        publishedLandmarks)
                    : UncomparedFull468();

            if (!decodeCorrelationExact || !inputMatchesNormalized ||
                (accepted && !publicationMatchesInput))
            {
                _inferenceStoreCorrelationMismatchCount++;
            }

            instance.WriteInferencePublication(
                decode,
                sourceHostTicks,
                runnerTimestamp,
                runnerArrivalHostTicks,
                accepted,
                publicationSequence,
                reason ?? string.Empty,
                storeInputLandmarks,
                publishedLandmarks,
                publishedCount,
                decodeCorrelationExact,
                inputMatchesNormalized,
                publicationMatchesInput);

            if (accepted && publicationSequence > 0UL)
            {
                bool payloadAvailable =
                    publishedLandmarks != null &&
                    publishedCount >=
                        KiwiInferenceFaceTracker.BaseLandmarkCount;
                int pointCount = publishedLandmarks == null
                    ? 0
                    : Math.Min(
                        publishedCount,
                        publishedLandmarks.Length);

                if (payloadAvailable)
                {
                    for (int i = 0;
                         i < KiwiInferenceFaceTracker.BaseLandmarkCount;
                         i++)
                    {
                        LatestAcceptedPublicationFull468[i] =
                            publishedLandmarks[i];
                    }
                }

                var fullSnapshot = new Full468PublicationSnapshot
                {
                    valid = true,
                    publicationSequence = publicationSequence,
                    runnerTimestamp = runnerTimestamp,
                    sourceHostTicks = sourceHostTicks,
                    decodeSequence = decode.sequence,
                    pointCount = payloadAvailable
                        ? KiwiInferenceFaceTracker.BaseLandmarkCount
                        : pointCount,
                    payloadAvailable = payloadAvailable,
                    a1Store = a1StoreFull,
                    storePublication = storePublicationFull
                };
                _latestFull468Publication = fullSnapshot;
                instance.WriteFull468PublicationRow(
                    fullSnapshot,
                    System.Diagnostics.Stopwatch.GetTimestamp());
            }

            if (
                accepted &&
                publicationSequence > 0UL &&
                decodeCorrelationExact &&
                inputMatchesNormalized &&
                publicationMatchesInput)
            {
                instance.WriteAcceptedDecodeFull468(
                    decode,
                    publicationSequence,
                    runnerTimestamp);
                _latestAcceptedPublication =
                    new AcceptedPublicationObservation(
                        publicationSequence,
                        runnerTimestamp,
                        decode);
            }

            if (decodeCorrelationExact)
            {
                _latestValidInferenceDecode = default;
            }
        }
    }

    internal static void ObserveRoiWriterEvent(
        long roiWriterSequence,
        string writerKind,
        bool roiStateWritten,
        RoiStateSnapshot before,
        RoiStateSnapshot after,
        RoiSampleIdentity identity,
        Vector3[] rawLandmarks,
        Vector3[] normalizedLandmarks,
        int sourceWidth,
        int sourceHeight,
        RoiLandmarkSummary suppliedSummary,
        RoiDecisionMetrics decision)
    {
        if (!_armed) return;

        KiwiH1LandmarkerBoundaryObserver instance = _activeInstance;
        if (instance == null) return;

        lock (Sync)
        {
            RoiLandmarkSummary summary = suppliedSummary.valid
                ? suppliedSummary
                : BuildInternalRoiLandmarkSummary(
                    rawLandmarks,
                    normalizedLandmarks,
                    sourceWidth,
                    sourceHeight);

            _roiWriterEventCount++;
            instance.WriteRoiWriterEvent(
                roiWriterSequence,
                writerKind ?? string.Empty,
                roiStateWritten,
                before,
                after,
                identity,
                summary,
                decision);
        }
    }

    internal static void ObserveRoiSchedule(
        string schedulePhase,
        RoiSampleIdentity identity,
        int sourceWidth,
        int sourceHeight,
        long roiWriterSequence,
        string lastWriterKind,
        RoiStateSnapshot state,
        Matrix4x4 cropMatrix)
    {
        if (!_armed) return;

        KiwiH1LandmarkerBoundaryObserver instance = _activeInstance;
        if (instance == null) return;

        lock (Sync)
        {
            _roiScheduleCount++;
            instance.WriteRoiSchedule(
                schedulePhase ?? string.Empty,
                identity,
                sourceWidth,
                sourceHeight,
                roiWriterSequence,
                lastWriterKind ?? string.Empty,
                state,
                cropMatrix);
        }
    }

    internal static void ResetAcceptedWinnerSnapshot()
    {
        lock (Sync)
        {
            _hasPreviousAcceptedWinner = false;
            _latestFull468Publication = default;
        }
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
        _exactBoundaryWriter = CreateWriter(
            "boundary_exact.csv",
            "row,segment,consumeHostTicks,consumeTimestamp,consumeProvider,consumeBackend,consumeProviderSourceFrameId," +
            "rawValid,rawSequence,rawTimestamp,rawProviderSourceFrameId,rawP1X,rawP1Y,rawP33X,rawP33Y,rawP152X,rawP152Y,rawP263X,rawP263Y,rawP454X,rawP454Y," +
            "handoffValid,handoffSequence,handoffTimestamp,handoffProviderSourceFrameId,handoffP1X,handoffP1Y,handoffP33X,handoffP33Y,handoffP152X,handoffP152Y,handoffP263X,handoffP263Y,handoffP454X,handoffP454Y," +
            "consumeValid,consumeP1X,consumeP1Y,consumeP33X,consumeP33Y,consumeP152X,consumeP152Y,consumeP263X,consumeP263Y,consumeP454X,consumeP454Y," +
            "rawHandoffExact,rawConsumeExact,handoffConsumeExact,identityExact");
        _inferenceDecodeWriter = CreateWriter(
            "inference_decode_a0_a1.csv",
            "row,segment,sourceFrameId,sourceFrameIdDomain,sourceGeneration,trackerGeneration,cameraGeneration,trackingSessionGeneration,width,height,sourceHostTicks,decodeArrivalHostTicks,backend,decodeStatus,a0Valid,a1Valid," +
            "cropM00,cropM01,cropM03,cropM10,cropM11,cropM13,regionZScale," +
            SelectedXyzHeader("a0Raw") + "," +
            SelectedXyzHeader("a1Normalized"));
        _inferencePublicationWriter = CreateWriter(
            "inference_runner_publication.csv",
            "row,segment,observationHostTicks,decodeSequence,sourceFrameId,sourceFrameIdDomain,sourceGeneration,trackerGeneration,cameraGeneration,trackingSessionGeneration,width,height,sourceHostTicks,decodeArrivalHostTicks,runnerTimestamp,runnerArrivalHostTicks,backend,decodeStatus,storeAttempted,accepted,rejectReason,publicationSequence,decodeCorrelationExact,storeInputMatchesA1,publishedXYMatchesStoreInput," +
            "cropM00,cropM01,cropM03,cropM10,cropM11,cropM13,regionZScale," +
            SelectedXyzHeader("a0Raw") + "," +
            SelectedXyzHeader("a1Normalized") + "," +
            SelectedXyzHeader("storeInput") + "," +
            SelectedXyHeader("published"));
        _acceptedFull468Writer = CreateWriter(
            "accepted_decode_full_468.csv",
            "row,segment,acceptedPublicationSequence,runnerTimestamp,decodeSequence," +
            "sourceFrameId,sourceFrameIdDomain,sourceGeneration,trackerGeneration,cameraGeneration,trackingSessionGeneration,width,height,sourceHostTicks,decodeArrivalHostTicks," +
            "pointIndex,a0X,a0Y,a0Z,a1X,a1Y,a1Z," +
            "cropM00,cropM01,cropM03,cropM10,cropM11,cropM13,regionZScale");
        _roiWriterEventsWriter = CreateWriter(
            "roi_writer_events.csv",
            "row,segment,hostTicks,roiWriterSequence,writerKind,roiStateWritten," +
            "externalAnchorObserverSequence,sourceFrameId,sourceFrameIdDomain,sourceGeneration,trackerGeneration,cameraGeneration,trackingSessionGeneration,sourceHostTicks,callbackTimestamp,arrivalHostTicks," +
            "anchorRevisionBefore,anchorRevisionAfter,externalAnchorEpochBefore,externalAnchorEpochAfter," +
            "preCenterX,preCenterYBottom,preWidth,preHeight,preRollRadians,preHasRegion,preLastWriterSequence,preLastWriterKind,preLastWriterSourceHostTicks,preLastWriterSourceGeneration,preLastWriterCameraGeneration,preLastWriterTrackingSessionGeneration," +
            "postCenterX,postCenterYBottom,postWidth,postHeight,postRollRadians,postHasRegion,postLastWriterSequence,postLastWriterKind,postLastWriterSourceHostTicks,postLastWriterSourceGeneration,postLastWriterCameraGeneration,postLastWriterTrackingSessionGeneration," +
            "landmarkSummaryValid,landmarkSourceKind,landmarkCount,maxA0XYStepOver192,maxA0XYStepIndex,maxA0XYZStepOver192,maxA0XYZStepIndex,maxA1XYStep,maxA1XYStepIndex," +
            "minXPixels,minXIndex,minXPointX,minXPointY,maxXPixels,maxXIndex,maxXPointX,maxXPointY," +
            "minYPixelsBottom,minYIndex,minYPointX,minYPointY,maxYPixelsBottom,maxYIndex,maxYPointX,maxYPointY," +
            "boxWidthPixels,boxHeightPixels,squareSidePixels,targetCenterX,targetCenterYBottom,targetWidth,targetHeight,targetRollRadians,eye33X,eye33Y,eye263X,eye263Y," +
            "decisionMetricsValid,applyHostTicks,force,decisionHasRegion,regionRetentionActive,centerDistancePixels,centerThresholdPixels,widthRatioDelta,heightRatioDelta,rollDeltaDegrees,sizeRatioThreshold,rollThresholdDegrees,decision");
        _roiScheduleLinkWriter = CreateWriter(
            "roi_schedule_link.csv",
            "row,segment,scheduleHostTicks,schedulePhase,externalAnchorObserverSequence,sourceFrameId,sourceFrameIdDomain,sourceGeneration,trackerGeneration,cameraGeneration,trackingSessionGeneration,sourceHostTicks,callbackTimestamp,arrivalHostTicks,width,height," +
            "anchorRevision,externalAnchorEpoch,roiWriterSequence,lastRoiWriterKind,centerX,centerYBottom,regionWidth,regionHeight,rollRadians,hasRegion," +
            "roiStateLastWriterSequence,roiStateLastWriterKind,roiStateLastWriterSourceHostTicks,roiStateLastWriterSourceGeneration,roiStateLastWriterCameraGeneration,roiStateLastWriterTrackingSessionGeneration," +
            "cropM00,cropM01,cropM02,cropM03,cropM10,cropM11,cropM12,cropM13,cropM20,cropM21,cropM22,cropM23,cropM30,cropM31,cropM32,cropM33,regionZScale");
        _visualIdentityWriter = CreateWriter(
            "human_visual_identity.csv",
            "visualSequence,h1VisualId,unityFrame,canonicalUnityFrame,observationHostTicks,semanticTimestamp,canonicalFrameId,providerId,backend,normalizationValid,providerSourceFrameId,acceptedPublicationExact,acceptedPublicationSequence,sourceFrameId,sourceFrameIdDomain,sourceGeneration,sourceHostTicks");
        _full468BoundaryWriter = CreateWriter(
            "full468_boundary.csv",
            "eventKind,hostTicks,segment,h1VisualId,publicationSequence,runnerTimestamp,sourceHostTicks,decodeSequence,pointCount," +
            Full468ComparisonHeader("a1Store") + "," +
            Full468ComparisonHeader("storePublication") + "," +
            Full468ComparisonHeader("publicationConsume") + "," +
            "identityExact,coverageStatus");
        _gpuDrawInputWriter = CreateWriter(
            "gpu_draw_input_exact.csv",
            "requestSequence,requestHostTicks,completionHostTicks,segment,h1VisualId,visualSequence,unityFrame,semanticTimestamp,canonicalFrameId," +
            "publicationSequence,sourceFrameId,sourceFrameIdDomain,sourceGeneration,sourceHostTicks," +
            "textureInstanceId,width,height,format,graphicsFormat,sourceReadable,copyTextureSupport,asyncGpuReadbackSupported," +
            "cpuSha256,gpuSha256,byteCount,directExact,verticalFlipExact,mismatchByteCount,firstMismatchByte,firstMismatchPixel,firstMismatchChannel,readbackError,coverageStatus");
        _correlationWriter = CreateWriter("segment_correlation.csv",
            "row,segment,observationHostTicks,elapsedSeconds,canonicalAvailable,canonicalValid,canonicalFrameId,canonicalUnityFrame,semanticTimestamp,semanticLandmarkCount,providerId,backend,rigidFrameId,rigidTimestamp,sourceHostTicks,arrivalHostTicks,normalizationValid,providerSourceFrameId,providerSourceTimestamp,cameraGeneration,trackingSessionGeneration,providerGeneration,modelGeneration,canonicalFaceCenterX,canonicalFaceCenterY,canonicalRotationX,canonicalRotationY,canonicalRotationZ,canonicalRotationW,canonicalEulerX,canonicalEulerY,canonicalEulerZ,continuityAvailable,continuityState,continuityProviderId,continuitySourceAgeMs,continuityArrivalAgeMs,continuityCadenceJitterRatio,hubAvailable,hubActiveProviderId,hubSourceAgeMs,hubArrivalAgeMs,hubHandoffActive,hubHandoffIsResume,hubHandoffCount,providerTransitionObserved,rawSequence,rawTimestamp,rawFingerprint,handoffSequence,handoffTimestamp,handoffFingerprint,consumeValid,consumeTimestamp,consumeFingerprint,consumeProvider,consumeBackend,consumeProviderSourceFrameId,consumeHostTicks," +
            "latestPresentationValid,latestPresentationObservationHostTicks,latestPresentationCommittedSemanticTimestamp,latestPresentationCommittedCanonicalFrameId,latestPresentationCanonicalCorrelationStatus,latestPresentationProviderMetadataExactMatch,latestPresentationNormalizationValid,latestPresentationProviderId,latestPresentationBackend,latestPresentationProviderSourceFrameId," +
            "decisionValid,decisionSequence,decisionObservationHostTicks,decisionCandidateReceived,decisionAccepted,decisionReason,decisionSemanticTimestamp,decisionCanonicalFrameId,decisionProviderId,decisionBackend,decisionProviderSourceFrameId,decisionProviderSourceTimestamp,decisionSourceHostTicks,decisionArrivalHostTicks,decisionSourceAgeMs,decisionArrivalAgeMs,decisionSemanticFreshnessEvaluated,decisionSemanticFreshnessAgeMs,decisionCameraGeneration,decisionProviderGeneration,decisionTrackingSessionGeneration,decisionLeftEyeAccepted,decisionRightEyeAccepted,decisionMouthAccepted,decisionCommittedSemanticTimestamp,decisionCommittedCanonicalFrameId,decisionConsumeRelation," +
            "geometryDiagnosticValid,geometryDiagnosticObservationHostTicks,geometryDiagnosticSemanticTimestamp,geometryDiagnosticCanonicalFrameId,geometryDiagnosticCorrelationStatus,firstGeometrySnapshotIdentityFailure,geometryPipelineSubBoundary,expectedProviderSourceFrameId,expectedSourceHostTicks,expectedCameraGeneration,expectedTrackingSessionGeneration,expectedProviderGeneration,expectedModelGeneration,expectedBackend,expectedSemanticFrameWidth,expectedSemanticFrameHeight," +
            "predicateProductServiceAvailable,predicateSemanticDimensionsValid,predicateCanonicalFrameAvailable,predicateCanonicalFrameValid,predicateCanonicalSemanticPresent,predicateSemanticTimestampMatches,predicateRigidStateValid,predicateRigidValid,predicateRigidTimestampMatches,predicateRigidFrameIdPresent,predicateNormalizationValid,predicateProviderSourceFrameIdPresent,predicateCanonicalBackendMatches,predicateMatchedSubmissionTimingPresent,predicateSourceHostTicksPresent,predicateAcceptedSnapshotAvailable,predicateSourceFrameIdMatches,predicateSourceHostTicksMatches,predicateCameraGenerationMatches,predicateTrackingSessionGenerationMatches,predicateProviderGenerationMatches,predicateModelGenerationMatches,predicateAcceptedBackendMatches,predicateSemanticWidthMatches,predicateSemanticHeightMatches,predicateAccepted," +
            "acceptedSnapshotExists,acceptedGraphRunId,acceptedStreamId,acceptedFrameId,acceptedSourceHostTicks,acceptedCameraGeneration,acceptedTrackingSessionGeneration,acceptedProviderGeneration,acceptedModelGeneration,acceptedBackend,acceptedSemanticFrameWidth,acceptedSemanticFrameHeight,inFlightExists,inFlightFrameId,inFlightSourceHostTicks,latestPendingExists,latestPendingFrameId,latestPendingSourceHostTicks,callbackCompletionExists,callbackCompletionFrameId,callbackCompletionStatus,serviceShutdownRequested,serviceResetRequested,lastAcceptedGeometryFrameId,currentExpectedFrameInAccepted,currentExpectedFrameInFlight,currentExpectedFrameInPending,currentExpectedFrameInCompletion");
        WriteSummary(false);
        Debug.Log("[KiwiH1Boundary] DIAGNOSTIC_ONLY fixed-segment observer armed output=" + _outputDirectory);
    }

    private void Start()
    {
        RefreshReferences();
        OverlayVisibilityState state = CaptureOverlayVisibilityState();
        LogOverlayVisibilityState("PRE_SEGMENT_GATE", state, true);

        _visibilityGateEvaluated = true;
        _visibilityGatePassed =
            state.valid &&
            state.activeInHierarchy &&
            state.enabled &&
            state.isActiveAndEnabled &&
            state.visible &&
            state.reflectionReady;

        Debug.Log(
            "[KiwiH1Visibility] PRE_SEGMENT_GATE=" +
            (_visibilityGatePassed ? "PASS" : "FAIL"));

        WriteSummary(false);

        if (!_visibilityGatePassed)
        {
            _armed = false;
            Debug.LogError(
                "[KiwiH1Visibility] FAIL_CLOSED segment_not_started=1");
            Application.Quit(2);
            return;
        }

        _overlay.StartCsvRecording();
        if (!_overlay.IsCsvRecording ||
            string.IsNullOrWhiteSpace(_overlay.CurrentCsvPath))
        {
            _armed = false;
            Debug.LogError(
                "[KiwiH1Boundary] FAIL_CLOSED frame_comparison_csv_not_armed=1");
            WriteSummary(false);
            Application.Quit(3);
            return;
        }

        WriteSummary(false);

        _startedRealtime = Time.realtimeSinceStartupAsDouble;
        _nextAggregateRealtime = _startedRealtime + AggregatePeriodSeconds;
        UpdateSegmentBoundary();
    }

    private void Update()
    {
        if (!_visibilityGatePassed) return;
        UpdateSegmentBoundary();
    }

    private void LateUpdate()
    {
        RefreshReferences();
        LogOverlayVisibilityState(
            "STATE_CHANGE",
            CaptureOverlayVisibilityState(),
            false);
        if (!_visibilityGatePassed) return;
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
        GUI.Box(new Rect(10f, 10f, 920f, 122f), GUIContent.none);
        if (_visualIdentityStyle == null)
        {
            _visualIdentityStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18
            };
        }

        GUI.Label(new Rect(20f, 16f, 890f, 24f),
            "H1 fixed-segment diagnostic: " + SegmentInstruction(),
            _visualIdentityStyle);
        GUI.Label(new Rect(20f, 40f, 890f, 24f),
            "録画後に表示中のH1_VISUAL_IDを指定してください。segment=" + SegmentName() +
            " raw=" + ReadRawCount() + " consume=" + _consumeCount,
            _visualIdentityStyle);

        VisualIdentitySnapshot visual = _latestVisualIdentity;
        if (visual.valid)
        {
            GUI.Label(new Rect(20f, 64f, 890f, 24f),
                "H1_VISUAL_ID=" + VisualId(visual.visualSequence) +
                " unityFrame=" + visual.unityFrame +
                " semanticTs=" + visual.semanticTimestamp +
                " canonicalFrameId=" + visual.canonicalFrameId,
                _visualIdentityStyle);
            GUI.Label(new Rect(20f, 88f, 890f, 24f),
                "backend=" + visual.backend +
                " providerSourceFrameId=" + visual.providerSourceFrameId +
                " acceptedPublicationSequence=" +
                (visual.acceptedPublicationExact
                    ? visual.acceptedPublication.publicationSequence.ToString(Invariant)
                    : "UNAVAILABLE"),
                _visualIdentityStyle);
        }
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
            _overlayHeaderStyleField = type.GetField("_headerStyle", flags);
            _overlayPreviewEpochMatchedField =
                type.GetField("debugLandmarkPreviewEpochMatched", flags);
            _overlayPreviewSemanticTimestampField =
                type.GetField("debugLandmarkPreviewSemanticTimestamp", flags);
        }
    }

    private OverlayVisibilityState CaptureOverlayVisibilityState()
    {
        KiwiFrameComparisonOverlay overlay = _overlay;
        if (overlay == null) return default;

        bool reflectionReady =
            _overlayHeaderStyleField != null &&
            _overlayPreviewEpochMatchedField != null &&
            _overlayPreviewSemanticTimestampField != null &&
            _overlayTimestampField != null;

        return new OverlayVisibilityState
        {
            valid = true,
            instanceId = overlay.GetInstanceID(),
            activeInHierarchy = overlay.gameObject.activeInHierarchy,
            enabled = overlay.enabled,
            isActiveAndEnabled = overlay.isActiveAndEnabled,
            visible = overlay.visible,
            screenWidth = Screen.width,
            screenHeight = Screen.height,
            reflectionReady = reflectionReady,
            headerStyleCreated =
                _overlayHeaderStyleField != null &&
                _overlayHeaderStyleField.GetValue(overlay) != null,
            previewEpochMatched =
                _overlayPreviewEpochMatchedField != null &&
                (bool)_overlayPreviewEpochMatchedField.GetValue(overlay),
            previewSemanticTimestamp =
                _overlayPreviewSemanticTimestampField != null
                    ? (long)_overlayPreviewSemanticTimestampField.GetValue(overlay)
                    : long.MinValue,
            lastSemanticTimestamp =
                _overlayTimestampField != null
                    ? (long)_overlayTimestampField.GetValue(overlay)
                    : long.MinValue
        };
    }

    private void LogOverlayVisibilityState(
        string reason,
        OverlayVisibilityState state,
        bool force)
    {
        bool lifecycleChanged =
            !_hasOverlayVisibilityState ||
            state.valid != _lastOverlayVisibilityState.valid ||
            state.instanceId != _lastOverlayVisibilityState.instanceId ||
            state.activeInHierarchy != _lastOverlayVisibilityState.activeInHierarchy ||
            state.enabled != _lastOverlayVisibilityState.enabled ||
            state.isActiveAndEnabled != _lastOverlayVisibilityState.isActiveAndEnabled ||
            state.visible != _lastOverlayVisibilityState.visible ||
            state.screenWidth != _lastOverlayVisibilityState.screenWidth ||
            state.screenHeight != _lastOverlayVisibilityState.screenHeight ||
            state.reflectionReady != _lastOverlayVisibilityState.reflectionReady ||
            state.headerStyleCreated != _lastOverlayVisibilityState.headerStyleCreated;

        bool previewTimestampAdvanced =
            _hasOverlayVisibilityState &&
            state.previewSemanticTimestamp >= 0L &&
            state.previewSemanticTimestamp !=
                _lastOverlayVisibilityState.previewSemanticTimestamp;

        int epochBit = state.previewEpochMatched ? 2 : 1;
        bool newEpochState = state.valid && (_previewEpochStateMask & epochBit) == 0;

        bool shouldLog =
            force ||
            lifecycleChanged ||
            (!_previewTimestampAdvanceLogged && previewTimestampAdvanced) ||
            newEpochState;

        _lastOverlayVisibilityState = state;
        _hasOverlayVisibilityState = true;
        if (previewTimestampAdvanced) _previewTimestampAdvanceLogged = true;
        if (state.valid) _previewEpochStateMask |= epochBit;

        if (!shouldLog) return;

        _overlayVisibilityLogCount++;
        Debug.Log(
            "[KiwiH1Visibility] STATE" +
            " reason=" + reason +
            " hostTicks=" + System.Diagnostics.Stopwatch.GetTimestamp() +
            " overlayInstanceId=" + state.instanceId +
            " activeInHierarchy=" + B(state.activeInHierarchy) +
            " enabled=" + B(state.enabled) +
            " isActiveAndEnabled=" + B(state.isActiveAndEnabled) +
            " visible=" + B(state.visible) +
            " screen=" + state.screenWidth + "x" + state.screenHeight +
            " reflectionReady=" + B(state.reflectionReady) +
            " headerStyleCreated=" + B(state.headerStyleCreated) +
            " previewEpochMatched=" + B(state.previewEpochMatched) +
            " previewSemanticTimestamp=" + state.previewSemanticTimestamp +
            " lastSemanticTimestamp=" + state.lastSemanticTimestamp);
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
        VisualIdentitySnapshot visual = CaptureVisualIdentity(
            canonicalFrame,
            timestamp,
            observationHostTicks);
        WriteFull468PreDrawRow(
            canonicalFrame,
            landmarks,
            count,
            timestamp,
            observationHostTicks,
            VisualId(visual.visualSequence));
    }

    private VisualIdentitySnapshot CaptureVisualIdentity(
        KiwiTrackingFrame canonicalFrame,
        long semanticTimestamp,
        long observationHostTicks)
    {
        AcceptedPublicationObservation publication;
        lock (Sync)
        {
            publication = _latestAcceptedPublication;
        }

        bool acceptedPublicationExact =
            publication.valid &&
            canonicalFrame.rigid.backend ==
                KiwiTrackingBackend.InferenceEngine &&
            canonicalFrame.normalization.valid &&
            canonicalFrame.normalization.providerSourceFrameId ==
                publication.publicationSequence &&
            semanticTimestamp == publication.runnerTimestamp;

        var snapshot = new VisualIdentitySnapshot(
            ++_visualIdentitySequence,
            Time.frameCount,
            canonicalFrame,
            semanticTimestamp,
            observationHostTicks,
            acceptedPublicationExact,
            acceptedPublicationExact ? publication : default);

        _latestVisualIdentity = snapshot;
        WriteVisualIdentity(snapshot);
        return snapshot;
    }

    private void WriteVisualIdentity(VisualIdentitySnapshot snapshot)
    {
        if (_visualIdentityWriter == null || !snapshot.valid) return;

        _visualIdentityWriter.WriteLine(
            snapshot.visualSequence + "," +
            Csv(VisualId(snapshot.visualSequence)) + "," +
            snapshot.unityFrame + "," +
            snapshot.canonicalUnityFrame + "," +
            snapshot.observationHostTicks + "," +
            snapshot.semanticTimestamp + "," +
            snapshot.canonicalFrameId + "," +
            Csv(snapshot.providerId) + "," +
            Csv(snapshot.backend.ToString()) + "," +
            B(snapshot.normalizationValid) + "," +
            snapshot.providerSourceFrameId + "," +
            B(snapshot.acceptedPublicationExact) + "," +
            AcceptedPublicationCsv(snapshot));
    }

    private static string AcceptedPublicationCsv(
        VisualIdentitySnapshot snapshot)
    {
        if (!snapshot.acceptedPublicationExact)
        {
            return ",,,,";
        }

        AcceptedPublicationObservation publication =
            snapshot.acceptedPublication;
        InferenceDecodeObservation decode = publication.decode;

        return publication.publicationSequence + "," +
            decode.sourceFrameId + "," +
            Csv(decode.sourceFrameIdIsNativeSequence
                ? "NATIVE_PRESENTED_SEQUENCE"
                : "RUNNER_FRESH_SOURCE_GENERATION_FALLBACK") + "," +
            decode.sourceGeneration + "," +
            decode.sourceHostTicks;
    }

    private static string VisualId(long visualSequence)
    {
        return "H1V-" + visualSequence.ToString("D6", Invariant);
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
                    !SelectedExactlyEqual(_latestHandoff, consume) ||
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

        WriteExactBoundaryRow(consume);

        _lastConsumeTimestamp = consume.timestamp;
        _latestConsume = consume;
        ObservePeak(consume, ref _consumePeakDelta, ref _consumePeakTimestamp);
    }

    private void CaptureGpuDrawInput(
        Texture2D overlayTexture,
        Color32[] cpuPixels,
        long semanticTimestamp,
        long observationHostTicks)
    {
        VisualIdentitySnapshot visual =
            _latestVisualIdentity;
        if (
            !visual.valid ||
            visual.visualSequence <= 0L ||
            visual.visualSequence ==
                _lastGpuDrawInputVisualSequence
        )
        {
            return;
        }

        _lastGpuDrawInputVisualSequence =
            visual.visualSequence;

        if (
            visual.backend !=
                KiwiTrackingBackend.InferenceEngine
        )
        {
            _gpuDrawInputOutOfScopeCount++;
            return;
        }

        if (!visual.acceptedPublicationExact)
        {
            _gpuDrawInputCoverageGapCount++;
            WriteGpuDrawInputGap(
                visual,
                overlayTexture,
                observationHostTicks,
                "COVERAGE_GAP_ACCEPTED_PUBLICATION_NOT_EXACT");
            return;
        }

        if (
            semanticTimestamp !=
                visual.semanticTimestamp
        )
        {
            _gpuDrawInputCoverageGapCount++;
            WriteGpuDrawInputGap(
                visual,
                overlayTexture,
                observationHostTicks,
                "COVERAGE_GAP_VISUAL_IDENTITY_TIMESTAMP_MISMATCH");
            return;
        }

        if (
            overlayTexture == null ||
            cpuPixels == null ||
            overlayTexture.width !=
                GpuDrawInputWidth ||
            overlayTexture.height !=
                GpuDrawInputHeight ||
            overlayTexture.format !=
                TextureFormat.RGBA32 ||
            cpuPixels.Length !=
                GpuDrawInputWidth *
                GpuDrawInputHeight
        )
        {
            _gpuDrawInputCoverageGapCount++;
            WriteGpuDrawInputGap(
                visual,
                overlayTexture,
                observationHostTicks,
                "COVERAGE_GAP_SOURCE_TEXTURE_OR_CPU_PIXELS_INVALID");
            return;
        }

        if (
            !SystemInfo.supportsAsyncGPUReadback ||
            (
                SystemInfo.copyTextureSupport &
                CopyTextureSupport.Basic
            ) == 0
        )
        {
            _gpuDrawInputCoverageGapCount++;
            WriteGpuDrawInputGap(
                visual,
                overlayTexture,
                observationHostTicks,
                "COVERAGE_GAP_GPU_COPY_OR_ASYNC_READBACK_UNSUPPORTED");
            return;
        }

        EnsureGpuDrawInputSlots();

        int slotIndex =
            FindFreeGpuDrawInputSlot();
        if (slotIndex < 0)
        {
            _gpuDrawInputPoolExhaustedCount++;
            _gpuDrawInputCoverageGapCount++;
            WriteGpuDrawInputGap(
                visual,
                overlayTexture,
                observationHostTicks,
                "COVERAGE_GAP_READBACK_POOL_EXHAUSTED");
            return;
        }

        GpuDrawInputSlot slot =
            _gpuDrawInputSlots[slotIndex];
        EncodeColor32Bytes(
            cpuPixels,
            slot.cpuBytes);

        string cpuSha256 =
            Sha256Hex(slot.cpuBytes);
        long requestSequence =
            ++_gpuDrawInputRequestSequence;
        var identity =
            new GpuDrawInputIdentity(
                requestSequence,
                observationHostTicks,
                SegmentName(),
                visual,
                overlayTexture,
                cpuSha256);

        slot.inFlight = true;
        slot.requestSequence =
            requestSequence;
        _gpuDrawInputInFlight++;
        _gpuDrawInputRequestedCount++;
        _gpuDrawInputMaxInFlight =
            Math.Max(
                _gpuDrawInputMaxInFlight,
                _gpuDrawInputInFlight);

        var commandBuffer =
            new CommandBuffer
            {
                name =
                    "Kiwi H1 Overlay GPU Draw Input Exact"
            };

        try
        {
            commandBuffer.CopyTexture(
                new RenderTargetIdentifier(
                    overlayTexture),
                new RenderTargetIdentifier(
                    slot.snapshot));

            commandBuffer.RequestAsyncReadback(
                slot.snapshot,
                0,
                TextureFormat.RGBA32,
                request =>
                    CompleteGpuDrawInputReadback(
                        slotIndex,
                        identity,
                        request));

            Graphics.ExecuteCommandBuffer(
                commandBuffer);
        }
        catch (Exception ex)
        {
            slot.inFlight = false;
            slot.requestSequence = 0L;
            _gpuDrawInputInFlight =
                Math.Max(
                    0,
                    _gpuDrawInputInFlight - 1);
            _gpuDrawInputCoverageGapCount++;

            WriteGpuDrawInputRow(
                identity,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                string.Empty,
                0,
                false,
                false,
                0L,
                -1,
                -1,
                -1,
                true,
                "COVERAGE_GAP_REQUEST_EXCEPTION_" +
                    ex.GetType().Name);
        }
        finally
        {
            commandBuffer.Release();
        }
    }

    private void EnsureGpuDrawInputSlots()
    {
        if (_gpuDrawInputSlots != null)
        {
            return;
        }

        _gpuDrawInputSlots =
            new GpuDrawInputSlot[
                GpuDrawInputSlotCount];

        for (
            int i = 0;
            i < _gpuDrawInputSlots.Length;
            i++)
        {
            var slot =
                new GpuDrawInputSlot();

            slot.snapshot =
                new Texture2D(
                    GpuDrawInputWidth,
                    GpuDrawInputHeight,
                    TextureFormat.RGBA32,
                    false,
                    true)
                {
                    name =
                        "Kiwi H1 GPU Draw Input Snapshot " +
                        i,
                    filterMode =
                        FilterMode.Point,
                    wrapMode =
                        TextureWrapMode.Clamp,
                    hideFlags =
                        HideFlags.DontSave
                };

            slot.snapshot.Apply(
                false,
                true);

            _gpuDrawInputSlots[i] =
                slot;
        }
    }

    private int FindFreeGpuDrawInputSlot()
    {
        if (_gpuDrawInputSlots == null)
        {
            return -1;
        }

        for (
            int i = 0;
            i < _gpuDrawInputSlots.Length;
            i++)
        {
            if (
                !_gpuDrawInputSlots[i]
                    .inFlight
            )
            {
                return i;
            }
        }

        return -1;
    }

    private void CompleteGpuDrawInputReadback(
        int slotIndex,
        GpuDrawInputIdentity identity,
        AsyncGPUReadbackRequest request)
    {
        if (
            _gpuDrawInputSlots == null ||
            slotIndex < 0 ||
            slotIndex >=
                _gpuDrawInputSlots.Length
        )
        {
            _gpuDrawInputCoverageGapCount++;
            return;
        }

        GpuDrawInputSlot slot =
            _gpuDrawInputSlots[slotIndex];

        if (
            !slot.inFlight ||
            slot.requestSequence !=
                identity.requestSequence
        )
        {
            _gpuDrawInputCoverageGapCount++;
            return;
        }

        long completionHostTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();
        string gpuSha256 =
            string.Empty;
        int byteCount = 0;
        bool directExact = false;
        bool verticalFlipExact = false;
        long mismatchByteCount = 0L;
        int firstMismatchByte = -1;
        int firstMismatchPixel = -1;
        int firstMismatchChannel = -1;
        bool readbackError =
            request.hasError;
        string coverageStatus =
            "COVERAGE_GAP_UNSET";

        try
        {
            if (readbackError)
            {
                _gpuDrawInputReadbackErrorCount++;
                _gpuDrawInputCoverageGapCount++;
                coverageStatus =
                    "COVERAGE_GAP_ASYNC_READBACK_ERROR";
            }
            else
            {
                var data =
                    request.GetData<byte>();
                byteCount =
                    data.Length;

                if (
                    byteCount !=
                        GpuDrawInputByteCount
                )
                {
                    _gpuDrawInputCoverageGapCount++;
                    coverageStatus =
                        "COVERAGE_GAP_UNEXPECTED_READBACK_BYTE_COUNT";
                }
                else
                {
                    data.CopyTo(
                        slot.gpuBytes);

                    gpuSha256 =
                        Sha256Hex(
                            slot.gpuBytes);

                    directExact =
                        CompareBytes(
                            slot.cpuBytes,
                            slot.gpuBytes,
                            out mismatchByteCount,
                            out firstMismatchByte);

                    if (directExact)
                    {
                        _gpuDrawInputExactCount++;
                        coverageStatus =
                            "COMPLETE_EXACT";
                    }
                    else
                    {
                        _gpuDrawInputNonidenticalCount++;

                        verticalFlipExact =
                            CompareVerticalFlip(
                                slot.cpuBytes,
                                slot.gpuBytes);

                        if (verticalFlipExact)
                        {
                            _gpuDrawInputVerticalFlipOnlyCount++;
                        }

                        if (
                            firstMismatchByte >=
                                0
                        )
                        {
                            firstMismatchPixel =
                                firstMismatchByte /
                                GpuDrawInputBytesPerPixel;
                            firstMismatchChannel =
                                firstMismatchByte %
                                GpuDrawInputBytesPerPixel;
                        }

                        coverageStatus =
                            verticalFlipExact
                                ? "UNRESOLVED_READBACK_LAYOUT_VERTICAL_FLIP_ONLY"
                                : "UNRESOLVED_NONIDENTICAL_READBACK_BYTES";
                    }
                }
            }

            WriteGpuDrawInputRow(
                identity,
                completionHostTicks,
                gpuSha256,
                byteCount,
                directExact,
                verticalFlipExact,
                mismatchByteCount,
                firstMismatchByte,
                firstMismatchPixel,
                firstMismatchChannel,
                readbackError,
                coverageStatus);
        }
        catch (Exception ex)
        {
            _gpuDrawInputReadbackErrorCount++;
            _gpuDrawInputCoverageGapCount++;

            WriteGpuDrawInputRow(
                identity,
                completionHostTicks,
                gpuSha256,
                byteCount,
                false,
                false,
                mismatchByteCount,
                firstMismatchByte,
                firstMismatchPixel,
                firstMismatchChannel,
                true,
                "COVERAGE_GAP_READBACK_CALLBACK_EXCEPTION_" +
                    ex.GetType().Name);
        }
        finally
        {
            _gpuDrawInputCompletedCount++;
            slot.inFlight = false;
            slot.requestSequence = 0L;
            _gpuDrawInputInFlight =
                Math.Max(
                    0,
                    _gpuDrawInputInFlight - 1);
        }
    }

    private void WriteGpuDrawInputGap(
        VisualIdentitySnapshot visual,
        Texture2D texture,
        long hostTicks,
        string coverageStatus)
    {
        var identity =
            new GpuDrawInputIdentity(
                0L,
                hostTicks,
                SegmentName(),
                visual,
                texture,
                string.Empty);

        WriteGpuDrawInputRow(
            identity,
            hostTicks,
            string.Empty,
            0,
            false,
            false,
            0L,
            -1,
            -1,
            -1,
            false,
            coverageStatus);
    }

    private void WriteGpuDrawInputRow(
        GpuDrawInputIdentity identity,
        long completionHostTicks,
        string gpuSha256,
        int byteCount,
        bool directExact,
        bool verticalFlipExact,
        long mismatchByteCount,
        int firstMismatchByte,
        int firstMismatchPixel,
        int firstMismatchChannel,
        bool readbackError,
        string coverageStatus)
    {
        if (_gpuDrawInputWriter == null)
        {
            return;
        }

        _gpuDrawInputWriter.WriteLine(
            identity.requestSequence + "," +
            identity.requestHostTicks + "," +
            completionHostTicks + "," +
            Csv(identity.segment) + "," +
            Csv(
                VisualId(
                    identity.visualSequence)) + "," +
            identity.visualSequence + "," +
            identity.unityFrame + "," +
            identity.semanticTimestamp + "," +
            identity.canonicalFrameId + "," +
            identity.publicationSequence + "," +
            identity.sourceFrameId + "," +
            Csv(
                identity.sourceFrameIdDomain) + "," +
            identity.sourceGeneration + "," +
            identity.sourceHostTicks + "," +
            identity.textureInstanceId + "," +
            identity.width + "," +
            identity.height + "," +
            Csv(identity.format) + "," +
            Csv(identity.graphicsFormat) + "," +
            B(identity.sourceReadable) + "," +
            Csv(
                SystemInfo.copyTextureSupport
                    .ToString()) + "," +
            B(
                SystemInfo
                    .supportsAsyncGPUReadback) + "," +
            Csv(identity.cpuSha256) + "," +
            Csv(gpuSha256) + "," +
            byteCount + "," +
            B(directExact) + "," +
            B(verticalFlipExact) + "," +
            mismatchByteCount + "," +
            firstMismatchByte + "," +
            firstMismatchPixel + "," +
            firstMismatchChannel + "," +
            B(readbackError) + "," +
            Csv(coverageStatus));
    }

    private static void EncodeColor32Bytes(
        Color32[] pixels,
        byte[] destination)
    {
        for (
            int i = 0;
            i < pixels.Length;
            i++)
        {
            int offset =
                i *
                GpuDrawInputBytesPerPixel;
            Color32 pixel =
                pixels[i];

            destination[offset] =
                pixel.r;
            destination[offset + 1] =
                pixel.g;
            destination[offset + 2] =
                pixel.b;
            destination[offset + 3] =
                pixel.a;
        }
    }

    private static string Sha256Hex(
        byte[] bytes)
    {
        using (
            SHA256 sha256 =
                SHA256.Create())
        {
            byte[] hash =
                sha256.ComputeHash(
                    bytes);
            var builder =
                new StringBuilder(
                    hash.Length * 2);

            for (
                int i = 0;
                i < hash.Length;
                i++)
            {
                builder.Append(
                    hash[i].ToString(
                        "X2",
                        Invariant));
            }

            return builder.ToString();
        }
    }

    private static bool CompareBytes(
        byte[] expected,
        byte[] actual,
        out long mismatchCount,
        out int firstMismatchByte)
    {
        mismatchCount = 0L;
        firstMismatchByte = -1;

        if (
            expected == null ||
            actual == null ||
            expected.Length !=
                actual.Length
        )
        {
            return false;
        }

        for (
            int i = 0;
            i < expected.Length;
            i++)
        {
            if (
                expected[i] ==
                actual[i]
            )
            {
                continue;
            }

            if (
                firstMismatchByte <
                    0
            )
            {
                firstMismatchByte =
                    i;
            }

            mismatchCount++;
        }

        return mismatchCount == 0L;
    }

    private static bool CompareVerticalFlip(
        byte[] cpu,
        byte[] gpu)
    {
        if (
            cpu == null ||
            gpu == null ||
            cpu.Length !=
                GpuDrawInputByteCount ||
            gpu.Length !=
                GpuDrawInputByteCount
        )
        {
            return false;
        }

        int rowBytes =
            GpuDrawInputWidth *
            GpuDrawInputBytesPerPixel;

        for (
            int y = 0;
            y < GpuDrawInputHeight;
            y++)
        {
            int cpuOffset =
                y * rowBytes;
            int gpuOffset =
                (
                    GpuDrawInputHeight -
                    1 -
                    y
                ) *
                rowBytes;

            for (
                int x = 0;
                x < rowBytes;
                x++)
            {
                if (
                    cpu[cpuOffset + x] !=
                    gpu[gpuOffset + x]
                )
                {
                    return false;
                }
            }
        }

        return true;
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
        if (_inferenceDecodeWriter != null) _inferenceDecodeWriter.Flush();
        if (_inferencePublicationWriter != null) _inferencePublicationWriter.Flush();
        if (_acceptedFull468Writer != null) _acceptedFull468Writer.Flush();
        if (_roiWriterEventsWriter != null) _roiWriterEventsWriter.Flush();
        if (_roiScheduleLinkWriter != null) _roiScheduleLinkWriter.Flush();
        if (_gpuDrawInputWriter != null) _gpuDrawInputWriter.Flush();
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
            "OVERLAY_VISIBILITY_PRE_SEGMENT_GATE=" +
                (!_visibilityGateEvaluated
                    ? "NOT_EVALUATED"
                    : _visibilityGatePassed ? "PASS" : "FAIL"),
            "OVERLAY_VISIBILITY_STATE_LOG_COUNT=" + _overlayVisibilityLogCount,
            "OVERLAY_INSTANCE_ID=" + _lastOverlayVisibilityState.instanceId,
            "OVERLAY_ACTIVE_IN_HIERARCHY=" + B(_lastOverlayVisibilityState.activeInHierarchy),
            "OVERLAY_ENABLED=" + B(_lastOverlayVisibilityState.enabled),
            "OVERLAY_IS_ACTIVE_AND_ENABLED=" + B(_lastOverlayVisibilityState.isActiveAndEnabled),
            "OVERLAY_VISIBLE=" + B(_lastOverlayVisibilityState.visible),
            "OVERLAY_SCREEN=" + _lastOverlayVisibilityState.screenWidth + "x" + _lastOverlayVisibilityState.screenHeight,
            "OVERLAY_REFLECTION_READY=" + B(_lastOverlayVisibilityState.reflectionReady),
            "OVERLAY_HEADER_STYLE_CREATED=" + B(_lastOverlayVisibilityState.headerStyleCreated),
            "OVERLAY_PREVIEW_EPOCH_MATCHED=" + B(_lastOverlayVisibilityState.previewEpochMatched),
            "OVERLAY_PREVIEW_SEMANTIC_TIMESTAMP=" + _lastOverlayVisibilityState.previewSemanticTimestamp,
            "OVERLAY_LAST_SEMANTIC_TIMESTAMP=" + _lastOverlayVisibilityState.lastSemanticTimestamp,
            "OVERLAY_ON_GUI_OWNED_TIMESTAMP_ADVANCED=" + B(_previewTimestampAdvanceLogged),
            "FRAME_COMPARISON_CSV_RECORDING=" + B(_overlay != null && _overlay.IsCsvRecording),
            "FRAME_COMPARISON_CSV_PATH=" + (_overlay != null ? _overlay.CurrentCsvPath : string.Empty),
            "FRAME_COMPARISON_CSV_RECORDED_ROWS=" + (_overlay != null ? _overlay.RecordedFrameCount : 0),
            "RIGHT_SIDE_VISIBLE_SINK=KiwiFrameComparisonOverlay.MATCHED_LANDMARK_DEBUG",
            "PACKAGE_FACE_LANDMARKER_ANNOTATION_ACTIVE=" + B(_runner != null && _runner.renderDebugLandmarkAnnotations),
            "RAW_MEDIAPIPE_CALLBACK_COUNT=" + rawCount,
            "RAW_RESULT_ABSENT_COUNT=" + rawAbsent,
            "RAW_LATEST_OBSERVER_OVERWRITE_COUNT=" + rawOverwrite,
            "RAW_DUPLICATE_TIMESTAMP_COUNT=" + rawDuplicate,
            "RAW_OUT_OF_ORDER_TIMESTAMP_COUNT=" + rawOutOfOrder,
            "RAW_ABNORMAL_SOURCE_GAP_COUNT=" + rawGap,
            "RAW_AUX_ONLY_WHILE_IE_PRIMARY_COUNT=" + rawAux,
            "INFERENCE_DECODE_COUNT=" + _inferenceDecodeCount,
            "INFERENCE_DECODE_VALID_COUNT=" + _inferenceDecodeValidCount,
            "INFERENCE_DECODE_REJECT_COUNT=" + _inferenceDecodeRejectCount,
            "INFERENCE_STORE_ATTEMPT_COUNT=" + _inferenceStoreAttemptCount,
            "INFERENCE_STORE_ACCEPTED_COUNT=" + _inferenceStoreAcceptedCount,
            "INFERENCE_STORE_REJECTED_COUNT=" + _inferenceStoreRejectedCount,
            "INFERENCE_STORE_CORRELATION_MISMATCH_COUNT=" + _inferenceStoreCorrelationMismatchCount,
            "ROI_WRITER_EVENT_COUNT=" + _roiWriterEventCount,
            "ROI_SCHEDULE_LINK_COUNT=" + _roiScheduleCount,
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
            "GPU_DRAW_INPUT_SLOT_COUNT=" + GpuDrawInputSlotCount,
            "GPU_DRAW_INPUT_BLOCKING_WAIT_COUNT=0",
            "GPU_DRAW_INPUT_REQUESTED_COUNT=" + _gpuDrawInputRequestedCount,
            "GPU_DRAW_INPUT_COMPLETED_COUNT=" + _gpuDrawInputCompletedCount,
            "GPU_DRAW_INPUT_IN_FLIGHT_AT_SUMMARY=" + _gpuDrawInputInFlight,
            "GPU_DRAW_INPUT_MAX_IN_FLIGHT=" + _gpuDrawInputMaxInFlight,
            "GPU_DRAW_INPUT_EXACT_COUNT=" + _gpuDrawInputExactCount,
            "GPU_DRAW_INPUT_NONIDENTICAL_COUNT=" + _gpuDrawInputNonidenticalCount,
            "GPU_DRAW_INPUT_VERTICAL_FLIP_ONLY_COUNT=" + _gpuDrawInputVerticalFlipOnlyCount,
            "GPU_DRAW_INPUT_READBACK_ERROR_COUNT=" + _gpuDrawInputReadbackErrorCount,
            "GPU_DRAW_INPUT_COVERAGE_GAP_COUNT=" + _gpuDrawInputCoverageGapCount,
            "GPU_DRAW_INPUT_POOL_EXHAUSTED_COUNT=" + _gpuDrawInputPoolExhaustedCount,
            "GPU_DRAW_INPUT_OUT_OF_SCOPE_COUNT=" + _gpuDrawInputOutOfScopeCount,
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
            maxDelta = hadPrevious ? maximum : 0f,
            selected = selected
        };
    }

    private static bool SelectedExactlyEqual(Sample a, Sample b)
    {
        if (!a.valid || !b.valid || a.selected == null || b.selected == null ||
            a.selected.Length != b.selected.Length)
        {
            return false;
        }

        for (int i = 0; i < a.selected.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(a.selected[i]) !=
                BitConverter.SingleToInt32Bits(b.selected[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void WriteExactBoundaryRow(Sample consume)
    {
        if (_exactBoundaryWriter == null) return;

        Sample raw;
        Sample handoff;
        lock (Sync)
        {
            raw = _latestRaw;
            handoff = _latestHandoff;
        }

        bool rawHandoffExact =
            raw.valid && handoff.valid &&
            raw.sequence == handoff.sequence &&
            raw.timestamp == handoff.timestamp &&
            SelectedExactlyEqual(raw, handoff);

        bool rawConsumeExact =
            raw.valid && consume.valid &&
            raw.timestamp == consume.timestamp &&
            SelectedExactlyEqual(raw, consume);

        bool handoffConsumeExact =
            handoff.valid && consume.valid &&
            handoff.timestamp == consume.timestamp &&
            SelectedExactlyEqual(handoff, consume);

        bool identityExact =
            handoffConsumeExact &&
            handoff.providerSourceFrameId > 0UL &&
            consume.providerSourceFrameId > 0UL &&
            handoff.providerSourceFrameId == consume.providerSourceFrameId;

        _exactBoundaryWriter.WriteLine(
            _consumeCount + "," +
            Csv(_activeSegment) + "," +
            consume.hostTicks + "," +
            consume.timestamp + "," +
            Csv(consume.provider ?? string.Empty) + "," +
            Csv(consume.backend.ToString()) + "," +
            consume.providerSourceFrameId + "," +
            B(raw.valid) + "," +
            raw.sequence + "," +
            raw.timestamp + "," +
            raw.providerSourceFrameId + "," +
            SelectedCsv(raw) + "," +
            B(handoff.valid) + "," +
            handoff.sequence + "," +
            handoff.timestamp + "," +
            handoff.providerSourceFrameId + "," +
            SelectedCsv(handoff) + "," +
            B(consume.valid) + "," +
            SelectedCsv(consume) + "," +
            B(rawHandoffExact) + "," +
            B(rawConsumeExact) + "," +
            B(handoffConsumeExact) + "," +
            B(identityExact));
    }

    private static string SelectedCsv(Sample sample)
    {
        if (sample.selected == null ||
            sample.selected.Length != FingerprintIndices.Length * 2)
        {
            return ",,,,,,,,,";
        }

        StringBuilder builder = new StringBuilder(160);
        for (int i = 0; i < sample.selected.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(sample.selected[i].ToString("R", Invariant));
        }

        return builder.ToString();
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

    private void WriteInferenceDecode(
        InferenceDecodeObservation observation,
        bool rawValid)
    {
        if (_inferenceDecodeWriter == null) return;

        _inferenceDecodeWriter.WriteLine(
            observation.sequence + "," +
            Csv(SegmentName()) + "," +
            InferenceIdentityCsv(observation) + "," +
            Csv(KiwiTrackingBackend.InferenceEngine.ToString()) + "," +
            Csv(observation.status) + "," +
            B(observation.rawValid) + "," +
            B(observation.valid) + "," +
            MatrixCsv(observation.cropMatrix) + "," +
            InferenceSelectedCsv(observation, true) + "," +
            InferenceSelectedCsv(observation, false));
    }

    private void WriteInferencePublication(
        InferenceDecodeObservation decode,
        long sourceHostTicks,
        long runnerTimestamp,
        long runnerArrivalHostTicks,
        bool accepted,
        ulong publicationSequence,
        string reason,
        Vector3[] storeInputLandmarks,
        Vector2[] publishedLandmarks,
        int publishedCount,
        bool decodeCorrelationExact,
        bool inputMatchesNormalized,
        bool publicationMatchesInput)
    {
        if (_inferencePublicationWriter == null) return;

        _inferencePublicationWriter.WriteLine(
            _inferenceStoreAttemptCount + "," +
            Csv(SegmentName()) + "," +
            System.Diagnostics.Stopwatch.GetTimestamp() + "," +
            decode.sequence + "," +
            decode.sourceFrameId + "," +
            Csv(decode.sourceFrameIdIsNativeSequence
                ? "NATIVE_PRESENTED_SEQUENCE"
                : "RUNNER_FRESH_SOURCE_GENERATION_FALLBACK") + "," +
            decode.sourceGeneration + "," +
            decode.trackerGeneration + "," +
            decode.cameraGeneration + "," +
            decode.trackingSessionGeneration + "," +
            decode.width + "," +
            decode.height + "," +
            sourceHostTicks + "," +
            decode.arrivalHostTicks + "," +
            runnerTimestamp + "," +
            runnerArrivalHostTicks + "," +
            Csv(KiwiTrackingBackend.InferenceEngine.ToString()) + "," +
            Csv(decode.status) + ",1," +
            B(accepted) + "," +
            Csv(reason) + "," +
            publicationSequence + "," +
            B(decodeCorrelationExact) + "," +
            B(inputMatchesNormalized) + "," +
            B(publicationMatchesInput) + "," +
            MatrixCsv(decode.cropMatrix) + "," +
            InferenceSelectedCsv(decode, true) + "," +
            InferenceSelectedCsv(decode, false) + "," +
            StoreSelectedCsv(storeInputLandmarks) + "," +
            PublishedSelectedCsv(publishedLandmarks, publishedCount));
    }

    private void WriteFull468PublicationRow(
        Full468PublicationSnapshot snapshot,
        long hostTicks)
    {
        if (_full468BoundaryWriter == null || !snapshot.valid) return;

        string coverageStatus = !snapshot.payloadAvailable
            ? "COVERAGE_GAP_POINT_COUNT_INSUFFICIENT"
            : !snapshot.a1Store.compared
                ? "COVERAGE_GAP_A1_IDENTITY_NOT_AVAILABLE"
                : !snapshot.storePublication.compared
                    ? "COVERAGE_GAP_STORE_PUBLICATION_NOT_AVAILABLE"
                    : "PUBLICATION_SNAPSHOT_READY";

        _full468BoundaryWriter.WriteLine(
            "ACCEPTED_PUBLICATION," +
            hostTicks + "," +
            Csv(SegmentName()) + "," +
            Csv(string.Empty) + "," +
            snapshot.publicationSequence + "," +
            snapshot.runnerTimestamp + "," +
            snapshot.sourceHostTicks + "," +
            snapshot.decodeSequence + "," +
            snapshot.pointCount + "," +
            Full468ComparisonCsv(snapshot.a1Store) + "," +
            Full468ComparisonCsv(snapshot.storePublication) + "," +
            Full468ComparisonCsv(UncomparedFull468()) + ",0," +
            Csv(coverageStatus));
    }

    private void WriteFull468PreDrawRow(
        KiwiTrackingFrame canonicalFrame,
        Vector2[] landmarks,
        int count,
        long semanticTimestamp,
        long hostTicks,
        string h1VisualId)
    {
        if (_full468BoundaryWriter == null) return;

        lock (Sync)
        {
            Full468PublicationSnapshot snapshot =
                _latestFull468Publication;
            bool identityExact =
                snapshot.valid &&
                canonicalFrame.normalization.valid &&
                canonicalFrame.normalization.providerSourceFrameId ==
                    snapshot.publicationSequence &&
                semanticTimestamp == snapshot.runnerTimestamp;
            bool consumePayloadAvailable =
                landmarks != null &&
                count >= KiwiInferenceFaceTracker.BaseLandmarkCount &&
                landmarks.Length >=
                    KiwiInferenceFaceTracker.BaseLandmarkCount;
            Full468Comparison publicationConsume =
                identityExact &&
                snapshot.payloadAvailable &&
                consumePayloadAvailable
                    ? ComparePublicationToConsumeFull468(landmarks)
                    : UncomparedFull468();

            string coverageStatus;
            if (!identityExact)
            {
                coverageStatus =
                    "COVERAGE_GAP_IDENTITY_NOT_AVAILABLE";
            }
            else if (!snapshot.payloadAvailable ||
                     !consumePayloadAvailable)
            {
                coverageStatus =
                    "COVERAGE_GAP_POINT_COUNT_INSUFFICIENT";
            }
            else if (!snapshot.a1Store.compared ||
                     !snapshot.storePublication.compared)
            {
                coverageStatus =
                    "COVERAGE_GAP_UPSTREAM_COMPARISON_NOT_AVAILABLE";
            }
            else
            {
                coverageStatus =
                    snapshot.a1Store.exact &&
                    snapshot.storePublication.exact &&
                    publicationConsume.exact
                        ? "COMPLETE_EXACT"
                        : "COMPLETE_MISMATCH";
            }

            int pointCount = consumePayloadAvailable
                ? KiwiInferenceFaceTracker.BaseLandmarkCount
                : landmarks == null
                    ? 0
                    : Math.Min(count, landmarks.Length);
            _full468BoundaryWriter.WriteLine(
                "RIGHT_OVERLAY_PREDRAW," +
                hostTicks + "," +
                Csv(SegmentName()) + "," +
                Csv(h1VisualId) + "," +
                snapshot.publicationSequence + "," +
                snapshot.runnerTimestamp + "," +
                snapshot.sourceHostTicks + "," +
                snapshot.decodeSequence + "," +
                pointCount + "," +
                Full468ComparisonCsv(snapshot.a1Store) + "," +
                Full468ComparisonCsv(snapshot.storePublication) + "," +
                Full468ComparisonCsv(publicationConsume) + "," +
                B(identityExact) + "," +
                Csv(coverageStatus));
        }
    }

    private void WriteAcceptedDecodeFull468(
        InferenceDecodeObservation decode,
        ulong publicationSequence,
        long runnerTimestamp)
    {
        if (_acceptedFull468Writer == null ||
            _latestFullDecodeSequence != decode.sequence)
        {
            return;
        }

        for (int i = 0;
             i < KiwiInferenceFaceTracker.BaseLandmarkCount;
             i++)
        {
            _acceptedFull468RowCount++;
            _acceptedFull468Writer.WriteLine(
                _acceptedFull468RowCount + "," +
                Csv(SegmentName()) + "," +
                publicationSequence + "," +
                runnerTimestamp + "," +
                decode.sequence + "," +
                InferenceIdentityCsv(decode) + "," +
                i + "," +
                Vector3Csv(LatestValidInferenceA0[i]) + "," +
                Vector3Csv(LatestValidInferenceA1[i]) + "," +
                MatrixCsv(decode.cropMatrix));
        }
    }

    private static RoiLandmarkSummary BuildInternalRoiLandmarkSummary(
        Vector3[] rawLandmarks,
        Vector3[] normalizedLandmarks,
        int sourceWidth,
        int sourceHeight)
    {
        const int count = KiwiInferenceFaceTracker.BaseLandmarkCount;
        if (rawLandmarks == null || normalizedLandmarks == null ||
            rawLandmarks.Length < count || normalizedLandmarks.Length < count ||
            sourceWidth <= 0 || sourceHeight <= 0)
        {
            return default;
        }

        RoiLandmarkSummary summary = new RoiLandmarkSummary
        {
            valid = true,
            sourceKind = "INFERENCE_ACCEPTED_WINNER_FIRST468",
            landmarkCount = count,
            maxA0XyStepIndex = -1,
            maxA0XyzStepIndex = -1,
            maxA1XyStepIndex = -1,
            minXPixels = float.PositiveInfinity,
            maxXPixels = float.NegativeInfinity,
            minYPixelsBottom = float.PositiveInfinity,
            maxYPixelsBottom = float.NegativeInfinity,
            minXIndex = -1,
            maxXIndex = -1,
            minYIndex = -1,
            maxYIndex = -1
        };

        for (int i = 0; i < count; i++)
        {
            Vector3 raw = rawLandmarks[i];
            Vector3 normalized = normalizedLandmarks[i];
            float xPixels = normalized.x * sourceWidth;
            float yPixelsBottom = (1f - normalized.y) * sourceHeight;

            if (xPixels < summary.minXPixels)
            {
                summary.minXPixels = xPixels;
                summary.minXIndex = i;
                summary.minXPoint = normalized;
            }
            if (xPixels > summary.maxXPixels)
            {
                summary.maxXPixels = xPixels;
                summary.maxXIndex = i;
                summary.maxXPoint = normalized;
            }
            if (yPixelsBottom < summary.minYPixelsBottom)
            {
                summary.minYPixelsBottom = yPixelsBottom;
                summary.minYIndex = i;
                summary.minYPoint = normalized;
            }
            if (yPixelsBottom > summary.maxYPixelsBottom)
            {
                summary.maxYPixelsBottom = yPixelsBottom;
                summary.maxYIndex = i;
                summary.maxYPoint = normalized;
            }

            if (_hasPreviousAcceptedWinner)
            {
                Vector3 rawDelta = raw - PreviousAcceptedWinnerA0[i];
                float rawXy = new Vector2(rawDelta.x, rawDelta.y).magnitude / 192f;
                float rawXyz = rawDelta.magnitude / 192f;
                float normalizedXy = new Vector2(
                    normalized.x - PreviousAcceptedWinnerA1[i].x,
                    normalized.y - PreviousAcceptedWinnerA1[i].y).magnitude;

                if (rawXy > summary.maxA0XyStepOverInput)
                {
                    summary.maxA0XyStepOverInput = rawXy;
                    summary.maxA0XyStepIndex = i;
                }
                if (rawXyz > summary.maxA0XyzStepOverInput)
                {
                    summary.maxA0XyzStepOverInput = rawXyz;
                    summary.maxA0XyzStepIndex = i;
                }
                if (normalizedXy > summary.maxA1XyStep)
                {
                    summary.maxA1XyStep = normalizedXy;
                    summary.maxA1XyStepIndex = i;
                }
            }

            PreviousAcceptedWinnerA0[i] = raw;
            PreviousAcceptedWinnerA1[i] = normalized;
        }

        _hasPreviousAcceptedWinner = true;
        summary.boxWidthPixels = summary.maxXPixels - summary.minXPixels;
        summary.boxHeightPixels = summary.maxYPixelsBottom - summary.minYPixelsBottom;
        summary.squareSidePixels = Mathf.Max(
            summary.boxWidthPixels,
            summary.boxHeightPixels) * 1.50f;
        summary.targetCenterX =
            (summary.minXPixels + summary.maxXPixels) * 0.5f / sourceWidth;
        summary.targetCenterYBottom =
            (summary.minYPixelsBottom + summary.maxYPixelsBottom) * 0.5f / sourceHeight;
        summary.targetWidth = summary.squareSidePixels / sourceWidth;
        summary.targetHeight = summary.squareSidePixels / sourceHeight;
        summary.eye33 = normalizedLandmarks[33];
        summary.eye263 = normalizedLandmarks[263];
        summary.targetRollRadians = Mathf.Atan2(
            -(summary.eye263.y - summary.eye33.y) * sourceHeight,
            (summary.eye263.x - summary.eye33.x) * sourceWidth);
        return summary;
    }

    private void WriteRoiWriterEvent(
        long roiWriterSequence,
        string writerKind,
        bool roiStateWritten,
        RoiStateSnapshot before,
        RoiStateSnapshot after,
        RoiSampleIdentity identity,
        RoiLandmarkSummary summary,
        RoiDecisionMetrics decision)
    {
        if (_roiWriterEventsWriter == null) return;

        _roiWriterEventsWriter.WriteLine(
            _roiWriterEventCount + "," +
            Csv(SegmentName()) + "," +
            System.Diagnostics.Stopwatch.GetTimestamp() + "," +
            roiWriterSequence + "," +
            Csv(writerKind) + "," +
            B(roiStateWritten) + "," +
            RoiIdentityCsv(identity) + "," +
            before.anchorRevision + "," + after.anchorRevision + "," +
            before.externalAnchorEpoch + "," + after.externalAnchorEpoch + "," +
            RoiStateValuesCsv(before) + "," + RoiWriterProvenanceCsv(before) + "," +
            RoiStateValuesCsv(after) + "," + RoiWriterProvenanceCsv(after) + "," +
            RoiLandmarkSummaryCsv(summary) + "," +
            RoiDecisionCsv(decision));
    }

    private void WriteRoiSchedule(
        string schedulePhase,
        RoiSampleIdentity identity,
        int sourceWidth,
        int sourceHeight,
        long roiWriterSequence,
        string lastWriterKind,
        RoiStateSnapshot state,
        Matrix4x4 cropMatrix)
    {
        if (_roiScheduleLinkWriter == null) return;

        _roiScheduleLinkWriter.WriteLine(
            _roiScheduleCount + "," +
            Csv(SegmentName()) + "," +
            System.Diagnostics.Stopwatch.GetTimestamp() + "," +
            Csv(schedulePhase) + "," +
            RoiIdentityCsv(identity) + "," +
            sourceWidth + "," + sourceHeight + "," +
            state.anchorRevision + "," + state.externalAnchorEpoch + "," +
            roiWriterSequence + "," + Csv(lastWriterKind) + "," +
            RoiStateValuesCsv(state) + "," +
            RoiWriterProvenanceCsv(state) + "," +
            FullMatrixCsv(cropMatrix));
    }

    private static string RoiIdentityCsv(RoiSampleIdentity identity)
    {
        return identity.externalAnchorObserverSequence + "," +
            identity.sourceFrameId + "," +
            Csv(identity.sourceFrameIdIsNativeSequence
                ? "NATIVE_PRESENTED_SEQUENCE"
                : identity.sourceFrameId > 0UL
                    ? "RUNNER_FRESH_SOURCE_GENERATION_FALLBACK"
                    : "UNAVAILABLE") + "," +
            identity.sourceGeneration + "," +
            identity.trackerGeneration + "," +
            identity.cameraGeneration + "," +
            identity.trackingSessionGeneration + "," +
            identity.sourceHostTicks + "," +
            identity.callbackTimestamp + "," +
            identity.arrivalHostTicks;
    }

    private static string RoiWriterProvenanceCsv(RoiStateSnapshot state)
    {
        return state.lastWriterSequence + "," +
            Csv(state.lastWriterKind ?? string.Empty) + "," +
            state.lastWriterSourceHostTicks + "," +
            state.lastWriterSourceGeneration + "," +
            state.lastWriterCameraGeneration + "," +
            state.lastWriterTrackingSessionGeneration;
    }

    private static string RoiStateValuesCsv(RoiStateSnapshot state)
    {
        return F(state.centerX) + "," +
            F(state.centerYBottom) + "," +
            F(state.width) + "," +
            F(state.height) + "," +
            F(state.rollRadians) + "," +
            B(state.hasRegion);
    }

    private static string RoiLandmarkSummaryCsv(RoiLandmarkSummary summary)
    {
        if (!summary.valid) return BlankFields(37);

        return B(true) + "," + Csv(summary.sourceKind ?? string.Empty) + "," +
            summary.landmarkCount + "," +
            F(summary.maxA0XyStepOverInput) + "," + summary.maxA0XyStepIndex + "," +
            F(summary.maxA0XyzStepOverInput) + "," + summary.maxA0XyzStepIndex + "," +
            F(summary.maxA1XyStep) + "," + summary.maxA1XyStepIndex + "," +
            F(summary.minXPixels) + "," + summary.minXIndex + "," + Vector2Csv(summary.minXPoint) + "," +
            F(summary.maxXPixels) + "," + summary.maxXIndex + "," + Vector2Csv(summary.maxXPoint) + "," +
            F(summary.minYPixelsBottom) + "," + summary.minYIndex + "," + Vector2Csv(summary.minYPoint) + "," +
            F(summary.maxYPixelsBottom) + "," + summary.maxYIndex + "," + Vector2Csv(summary.maxYPoint) + "," +
            F(summary.boxWidthPixels) + "," + F(summary.boxHeightPixels) + "," +
            F(summary.squareSidePixels) + "," +
            F(summary.targetCenterX) + "," + F(summary.targetCenterYBottom) + "," +
            F(summary.targetWidth) + "," + F(summary.targetHeight) + "," +
            F(summary.targetRollRadians) + "," +
            Vector2Csv(summary.eye33) + "," + Vector2Csv(summary.eye263);
    }

    private static string RoiDecisionCsv(RoiDecisionMetrics decision)
    {
        if (!decision.valid) return BlankFields(13);

        return B(true) + "," + decision.applyHostTicks + "," + B(decision.force) + "," +
            B(decision.hasRegion) + "," + B(decision.regionRetentionActive) + "," +
            F(decision.centerDistancePixels) + "," + F(decision.centerThresholdPixels) + "," +
            F(decision.widthRatioDelta) + "," + F(decision.heightRatioDelta) + "," +
            F(decision.rollDeltaDegrees) + "," + F(decision.sizeRatioThreshold) + "," +
            F(decision.rollThresholdDegrees) + "," + Csv(decision.decision ?? string.Empty);
    }

    private static string FullMatrixCsv(Matrix4x4 matrix)
    {
        Vector3 xAxis = matrix.MultiplyVector(Vector3.right);
        return F(matrix.m00) + "," + F(matrix.m01) + "," + F(matrix.m02) + "," + F(matrix.m03) + "," +
            F(matrix.m10) + "," + F(matrix.m11) + "," + F(matrix.m12) + "," + F(matrix.m13) + "," +
            F(matrix.m20) + "," + F(matrix.m21) + "," + F(matrix.m22) + "," + F(matrix.m23) + "," +
            F(matrix.m30) + "," + F(matrix.m31) + "," + F(matrix.m32) + "," + F(matrix.m33) + "," +
            F(xAxis.magnitude);
    }

    private static string InferenceIdentityCsv(
        InferenceDecodeObservation observation)
    {
        return observation.sourceFrameId + "," +
            Csv(observation.sourceFrameIdIsNativeSequence
                ? "NATIVE_PRESENTED_SEQUENCE"
                : "RUNNER_FRESH_SOURCE_GENERATION_FALLBACK") + "," +
            observation.sourceGeneration + "," +
            observation.trackerGeneration + "," +
            observation.cameraGeneration + "," +
            observation.trackingSessionGeneration + "," +
            observation.width + "," +
            observation.height + "," +
            observation.sourceHostTicks + "," +
            observation.arrivalHostTicks;
    }

    private static string MatrixCsv(Matrix4x4 matrix)
    {
        Vector3 xAxis = matrix.MultiplyVector(Vector3.right);
        return F(matrix.m00) + "," +
            F(matrix.m01) + "," +
            F(matrix.m03) + "," +
            F(matrix.m10) + "," +
            F(matrix.m11) + "," +
            F(matrix.m13) + "," +
            F(xAxis.magnitude);
    }

    private static string InferenceSelectedCsv(
        InferenceDecodeObservation observation,
        bool raw)
    {
        if (raw)
        {
            if (!observation.rawValid) return BlankFields(15);

            return Vector3Csv(observation.rawP1) + "," +
                Vector3Csv(observation.rawP33) + "," +
                Vector3Csv(observation.rawP152) + "," +
                Vector3Csv(observation.rawP263) + "," +
                Vector3Csv(observation.rawP454);
        }

        if (!observation.valid) return BlankFields(15);

        return Vector3Csv(observation.normalizedP1) + "," +
            Vector3Csv(observation.normalizedP33) + "," +
            Vector3Csv(observation.normalizedP152) + "," +
            Vector3Csv(observation.normalizedP263) + "," +
            Vector3Csv(observation.normalizedP454);
    }

    private static string StoreSelectedCsv(Vector3[] landmarks)
    {
        if (landmarks == null || landmarks.Length <= 454)
            return BlankFields(15);

        return Vector3Csv(landmarks[1]) + "," +
            Vector3Csv(landmarks[33]) + "," +
            Vector3Csv(landmarks[152]) + "," +
            Vector3Csv(landmarks[263]) + "," +
            Vector3Csv(landmarks[454]);
    }

    private static string PublishedSelectedCsv(
        Vector2[] landmarks,
        int count)
    {
        if (landmarks == null || count <= 454 || landmarks.Length <= 454)
            return BlankFields(10);

        return Vector2Csv(landmarks[1]) + "," +
            Vector2Csv(landmarks[33]) + "," +
            Vector2Csv(landmarks[152]) + "," +
            Vector2Csv(landmarks[263]) + "," +
            Vector2Csv(landmarks[454]);
    }

    private static Vector3 ReadRawTensorPoint(
        Tensor<float> readableOutput,
        int index)
    {
        return new Vector3(
            readableOutput[index * 3],
            readableOutput[index * 3 + 1],
            readableOutput[index * 3 + 2]);
    }

    private static bool IsInferenceDecodeNewer(
        InferenceDecodeObservation candidate,
        InferenceDecodeObservation current)
    {
        return !current.valid ||
            candidate.sourceHostTicks > current.sourceHostTicks ||
            (candidate.sourceHostTicks == current.sourceHostTicks &&
             candidate.arrivalHostTicks > current.arrivalHostTicks);
    }

    private static Full468Comparison UncomparedFull468()
    {
        return new Full468Comparison
        {
            firstMismatchPoint = -1,
            firstMismatchAxis = -1
        };
    }

    private static Full468Comparison NewFull468Comparison()
    {
        return new Full468Comparison
        {
            compared = true,
            exact = true,
            firstMismatchPoint = -1,
            firstMismatchAxis = -1
        };
    }

    private static Full468Comparison CompareA1ToStoreFull468(
        Vector3[] storeInputLandmarks)
    {
        Full468Comparison result = NewFull468Comparison();
        for (int i = 0;
             i < KiwiInferenceFaceTracker.BaseLandmarkCount;
             i++)
        {
            Vector3 expected = LatestValidInferenceA1[i];
            Vector3 actual = storeInputLandmarks[i];
            ObserveFull468Value(ref result, i, 0, expected.x, actual.x);
            ObserveFull468Value(ref result, i, 1, expected.y, actual.y);
            ObserveFull468Value(ref result, i, 2, expected.z, actual.z);
        }
        return result;
    }

    private static Full468Comparison CompareStoreToPublicationFull468(
        Vector3[] storeInputLandmarks,
        Vector2[] publishedLandmarks)
    {
        Full468Comparison result = NewFull468Comparison();
        for (int i = 0;
             i < KiwiInferenceFaceTracker.BaseLandmarkCount;
             i++)
        {
            Vector3 expected = storeInputLandmarks[i];
            Vector2 actual = publishedLandmarks[i];
            ObserveFull468Value(ref result, i, 0, expected.x, actual.x);
            ObserveFull468Value(ref result, i, 1, expected.y, actual.y);
        }
        return result;
    }

    private static Full468Comparison ComparePublicationToConsumeFull468(
        Vector2[] consumeLandmarks)
    {
        Full468Comparison result = NewFull468Comparison();
        for (int i = 0;
             i < KiwiInferenceFaceTracker.BaseLandmarkCount;
             i++)
        {
            Vector2 expected = LatestAcceptedPublicationFull468[i];
            Vector2 actual = consumeLandmarks[i];
            ObserveFull468Value(ref result, i, 0, expected.x, actual.x);
            ObserveFull468Value(ref result, i, 1, expected.y, actual.y);
        }
        return result;
    }

    private static void ObserveFull468Value(
        ref Full468Comparison result,
        int pointIndex,
        int axis,
        float expected,
        float actual)
    {
        int expectedBits = BitConverter.SingleToInt32Bits(expected);
        int actualBits = BitConverter.SingleToInt32Bits(actual);
        double residual = Math.Abs((double)expected - actual);
        if (double.IsNaN(residual)) residual = double.PositiveInfinity;
        if (residual > result.maxAbsResidual)
            result.maxAbsResidual = residual;

        if (expectedBits == actualBits) return;

        result.exact = false;
        if (result.firstMismatchPoint >= 0) return;

        result.firstMismatchPoint = pointIndex;
        result.firstMismatchAxis = axis;
        result.expectedBits = expectedBits;
        result.expectedValue = expected;
        result.actualBits = actualBits;
        result.actualValue = actual;
    }

    private static string Full468ComparisonHeader(string prefix)
    {
        return prefix + "Compared," +
            prefix + "Exact," +
            prefix + "FirstMismatchPoint," +
            prefix + "FirstMismatchAxis," +
            prefix + "ExpectedBits," +
            prefix + "ExpectedValue," +
            prefix + "ActualBits," +
            prefix + "ActualValue," +
            prefix + "MaxAbsResidual";
    }

    private static string Full468ComparisonCsv(
        Full468Comparison comparison)
    {
        if (!comparison.compared)
            return "0,0,-1,,,,,,";

        if (comparison.exact)
            return "1,1,-1,,,,,," + F(comparison.maxAbsResidual);

        return "1,0," + comparison.firstMismatchPoint + "," +
            Full468Axis(comparison.firstMismatchAxis) + "," +
            "0x" + unchecked((uint)comparison.expectedBits)
                .ToString("X8", Invariant) + "," +
            F(comparison.expectedValue) + "," +
            "0x" + unchecked((uint)comparison.actualBits)
                .ToString("X8", Invariant) + "," +
            F(comparison.actualValue) + "," +
            F(comparison.maxAbsResidual);
    }

    private static string Full468Axis(int axis)
    {
        return axis == 0 ? "X" : axis == 1 ? "Y" : axis == 2 ? "Z" : string.Empty;
    }

    private static bool SelectedVector3ExactlyEqual(
        InferenceDecodeObservation decode,
        Vector3[] landmarks)
    {
        return Vector3ExactlyEqual(decode.normalizedP1, landmarks[1]) &&
            Vector3ExactlyEqual(decode.normalizedP33, landmarks[33]) &&
            Vector3ExactlyEqual(decode.normalizedP152, landmarks[152]) &&
            Vector3ExactlyEqual(decode.normalizedP263, landmarks[263]) &&
            Vector3ExactlyEqual(decode.normalizedP454, landmarks[454]);
    }

    private static bool SelectedVector2ExactlyEqual(
        Vector3[] source,
        Vector2[] destination)
    {
        return Vector2ExactlyEqual(source[1], destination[1]) &&
            Vector2ExactlyEqual(source[33], destination[33]) &&
            Vector2ExactlyEqual(source[152], destination[152]) &&
            Vector2ExactlyEqual(source[263], destination[263]) &&
            Vector2ExactlyEqual(source[454], destination[454]);
    }

    private static bool Vector3ExactlyEqual(Vector3 a, Vector3 b)
    {
        return a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);
    }

    private static bool Vector2ExactlyEqual(Vector3 a, Vector2 b)
    {
        return a.x.Equals(b.x) && a.y.Equals(b.y);
    }

    private static string Vector3Csv(Vector3 value)
    {
        return F(value.x) + "," + F(value.y) + "," + F(value.z);
    }

    private static string Vector2Csv(Vector2 value)
    {
        return F(value.x) + "," + F(value.y);
    }

    private static string BlankFields(int count)
    {
        return count > 1 ? new string(',', count - 1) : string.Empty;
    }

    private static string SelectedXyzHeader(string prefix)
    {
        StringBuilder builder = new StringBuilder(160);
        for (int i = 0; i < FingerprintIndices.Length; i++)
        {
            if (i > 0) builder.Append(',');
            string name = prefix + "P" + FingerprintIndices[i];
            builder.Append(name).Append("X,")
                .Append(name).Append("Y,")
                .Append(name).Append("Z");
        }
        return builder.ToString();
    }

    private static string SelectedXyHeader(string prefix)
    {
        StringBuilder builder = new StringBuilder(120);
        for (int i = 0; i < FingerprintIndices.Length; i++)
        {
            if (i > 0) builder.Append(',');
            string name = prefix + "P" + FingerprintIndices[i];
            builder.Append(name).Append("X,")
                .Append(name).Append("Y");
        }
        return builder.ToString();
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
        if (_exactBoundaryWriter != null) { _exactBoundaryWriter.Flush(); _exactBoundaryWriter.Dispose(); _exactBoundaryWriter = null; }
        if (_inferenceDecodeWriter != null) { _inferenceDecodeWriter.Flush(); _inferenceDecodeWriter.Dispose(); _inferenceDecodeWriter = null; }
        if (_inferencePublicationWriter != null) { _inferencePublicationWriter.Flush(); _inferencePublicationWriter.Dispose(); _inferencePublicationWriter = null; }
        if (_acceptedFull468Writer != null) { _acceptedFull468Writer.Flush(); _acceptedFull468Writer.Dispose(); _acceptedFull468Writer = null; }
        if (_roiWriterEventsWriter != null) { _roiWriterEventsWriter.Flush(); _roiWriterEventsWriter.Dispose(); _roiWriterEventsWriter = null; }
        if (_roiScheduleLinkWriter != null) { _roiScheduleLinkWriter.Flush(); _roiScheduleLinkWriter.Dispose(); _roiScheduleLinkWriter = null; }
        if (_visualIdentityWriter != null) { _visualIdentityWriter.Flush(); _visualIdentityWriter.Dispose(); _visualIdentityWriter = null; }
        if (_full468BoundaryWriter != null) { _full468BoundaryWriter.Flush(); _full468BoundaryWriter.Dispose(); _full468BoundaryWriter = null; }
        if (_gpuDrawInputWriter != null) { _gpuDrawInputWriter.Flush(); _gpuDrawInputWriter.Dispose(); _gpuDrawInputWriter = null; }
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
