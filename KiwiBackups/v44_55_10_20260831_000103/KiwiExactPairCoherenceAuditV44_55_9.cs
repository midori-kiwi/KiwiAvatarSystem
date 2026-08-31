using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Mediapipe.Unity;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

/// <summary>
/// KiwiAvatarSystem v44.55.9
/// Exact Pair Coherence / Outlier Isolation Audit.
///
/// v44.55.8 showed a strong median benefit from Production-equivalent double
/// UNORM8 quantization, but a small set of 3-5 LSB pair outliers dominated the
/// aggregate mean/p95.
///
/// v44.55.9 changes only observer ordering:
///   1. match exact Native sequence -> calibrated Production lane;
///   2. IMMEDIATELY submit CopyTexture of that lane.cropTexture to an
///      observer-owned private RT;
///   3. derive the observer TextureConverter tensor from that frozen RT;
///   4. only then compute CURRENT_FLOAT and PRESENTATION_PLUS_FINAL_UNORM8
///      Native CPU candidates.
///
/// Every measured pair records identity/timing/error data individually.
/// Production camera, tracker, worker, ROI, thresholds and render settings are
/// untouched.
/// </summary>
internal sealed class KiwiExactPairCoherenceAuditV44_55_9
    : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_55_9_EXACT_PAIR_COHERENCE_OUTLIER_ISOLATION_AUDIT";

    private const string EnableVariable =
        "KIWI_V44_55_9_COHERENCE_AUDIT";

    private const string DurationVariable =
        "KIWI_V44_55_9_COHERENCE_SECONDS";

    private const string SampleHzVariable =
        "KIWI_V44_55_9_COHERENCE_HZ";

    private const string StableSecondsVariable =
        "KIWI_V44_55_9_STABLE_SECONDS";

    private const string ExpectedTrianglesVariable =
        "KIWI_V44_55_9_EXPECTED_TRIANGLES";

    private const int InputSize = 192;
    private const int PlaneLength =
        InputSize *
        InputSize;
    private const int InputFloatCount =
        PlaneLength *
        3;

    private const float DefaultDurationSeconds = 60f;
    private const float DefaultSampleHz = 2f;
    private const float DefaultStableSeconds = 8f;
    private const int DefaultExpectedTriangles = 254296;
    private const int WarmupPairCount = 3;

    // 0.125-LSB bins from 0 to 64 LSB plus one overflow bin.
    private const double HistogramBinsPerLsb = 8.0;
    private const int HistogramFiniteBinCount = 512;
    private const int HistogramOverflowBin =
        HistogramFiniteBinCount;
    private const int HistogramLength =
        HistogramFiniteBinCount + 1;

    private static readonly int XformId =
        Shader.PropertyToID(
            "_Xform");

    private static bool _installed;

    private MonoBehaviour _runner;
    private object _tracker;
    private Type _trackerType;

    private FieldInfo _trackerField;
    private FieldInfo _lanesField;

    // v44.55.4 read-only source dimensions. These are the exact dimensions
    // used by Production BuildCropMatrix immediately before scheduling.
    private FieldInfo _trackerSourceWidthField;
    private FieldInfo _trackerSourceHeightField;

    // v44.55.1 FIX2: Native snapshot identity is raw Native QPC, while
    // Production Runner publishes calibrated Managed Stopwatch host ticks.
    // Sequence is the common identity. These fields are read-only.
    private FieldInfo _runnerLastObservedFreshSequenceField;
    private FieldInfo _runnerLatestSentisSourceHostTicksField;

    private Type _laneType;
    private FieldInfo _laneInputField;
    private FieldInfo _laneCropTextureField;
    private FieldInfo _laneCropMaterialField;
    private FieldInfo _laneReadbackPendingField;
    private FieldInfo _lanePendingSourceHostTicksField;
    private FieldInfo _lanePendingStartedHostTicksField;
    private FieldInfo _lanePendingCropMatrixField;

    private Array _lanes;

    private readonly float[] _samplingMatrixCurrent =
        new float[16];

    private readonly float[] _nativeCurrentNchw =
        new float[InputFloatCount];

    private readonly float[] _nativeFinalUnorm8Nchw =
        new float[InputFloatCount];

    private readonly float[] _nativePresentationFinalUnorm8Nchw =
        new float[InputFloatCount];

    private readonly float[] _snapshotDirectNchw =
        new float[InputFloatCount];

    private readonly float[] _snapshotFlipYNchw =
        new float[InputFloatCount];

    private readonly float[] _observerTensorNchw =
        new float[InputFloatCount];

    private RenderTexture _observerSnapshotTexture;
    private Tensor<float> _observerTensor;
    private TextureTransform _observerTextureTransform;
    private CommandBuffer _observerGraphicsCommandBuffer;

    private bool _pendingSnapshotReadbackDone;
    private bool _pendingObserverTensorReadbackDone;
    private bool _pendingProductionTensorReadbackDone;
    private bool _pendingCpuCandidatesDone;
    private int _pendingPairToken;

    private sealed class PairRecord
    {
        internal int Index;
        internal ulong Sequence;
        internal long NativeHostTicks;
        internal long ManagedHostTicks;
        internal long LaneStartedTicks;
        internal double SourceAgeAtFreezeSubmitMs;
        internal double SourceAgeAfterCpuMs;
        internal double FreezeSubmitCpuMs;
        internal double SnapshotReadbackMs;
        internal double ObserverTensorReadbackMs;
        internal bool LaneStillMatchedAfterCpu;
        internal double CurrentMeanAbsLsb;
        internal double QuantizedMeanAbsLsb;
        internal double QuantizedToCurrentRatio;
        internal double SnapshotFlipYMeanAbsLsb;
        internal double QuantizedPairP95Lsb;
        internal double QuantizedPairP99Lsb;
        internal double QuantizedPairMaxLsb;
    }

    private readonly List<PairRecord> _pairRecords =
        new List<PairRecord>(128);

    private PairRecord _pendingPairRecord;

    private int _laneChangedAfterFreezeCount;
    private int _snapshotTensorMismatchCount;
    private int _quantizedOverHalfLsbCount;
    private int _quantizedOverOneLsbCount;
    private int _quantizedOverTwoLsbCount;

    private sealed class MetricAccumulator
    {
        internal readonly string Name;

        internal readonly long[] GlobalHistogram =
            new long[HistogramLength];

        internal readonly int[] PairHistogram =
            new int[HistogramLength];

        internal readonly double[] ChannelSignedSum =
            new double[3];

        internal readonly double[] ChannelAbsSum =
            new double[3];

        internal readonly double[] ChannelSquaredSum =
            new double[3];

        internal readonly double[] ChannelMaxAbsLsb =
            new double[3];

        internal long ComparedValueCount;
        internal long ExactFloatMatchCount;
        internal long WithinHalfLsbCount;
        internal long WithinOneLsbCount;
        internal long WithinTwoLsbCount;
        internal long WithinFourLsbCount;
        internal long WithinEightLsbCount;

        internal double GlobalAbsLsbSum;
        internal double GlobalSquaredNormalizedSum;
        internal double GlobalMaxAbsLsb;

        internal readonly List<double> PairMeanAbsLsb =
            new List<double>(128);

        internal readonly List<double> PairP95AbsLsb =
            new List<double>(128);

        internal readonly List<double> PairP99AbsLsb =
            new List<double>(128);

        internal readonly List<double> PairMaxAbsLsb =
            new List<double>(128);

        internal MetricAccumulator(
            string name)
        {
            Name = name;
        }

        internal int PairCount =>
            PairMeanAbsLsb.Count;

        internal double LastPairMeanAbsLsb =>
            PairMeanAbsLsb.Count > 0
                ? PairMeanAbsLsb[
                    PairMeanAbsLsb.Count - 1]
                : 0.0;

        internal bool AccumulatePair(
            float[] candidateNchw,
            float[] gpuNchw)
        {
            if (
                candidateNchw == null ||
                gpuNchw == null ||
                candidateNchw.Length <
                    InputFloatCount ||
                gpuNchw.Length <
                    InputFloatCount)
            {
                return false;
            }

            Array.Clear(
                PairHistogram,
                0,
                PairHistogram.Length);

            double pairAbsLsbSum = 0.0;
            double pairMaxLsb = 0.0;

            for (
                int channel = 0;
                channel < 3;
                channel++)
            {
                int channelBase =
                    channel *
                    PlaneLength;

                for (
                    int y = 0;
                    y < InputSize;
                    y++)
                {
                    int gpuRow =
                        channelBase +
                        y *
                        InputSize;

                    int candidateRow =
                        channelBase +
                        y *
                        InputSize;

                    for (
                        int x = 0;
                        x < InputSize;
                        x++)
                    {
                        float candidateValue =
                            candidateNchw[
                                candidateRow +
                                x];

                        float gpuValue =
                            gpuNchw[
                                gpuRow +
                                x];

                        if (
                            !IsFinite(candidateValue) ||
                            !IsFinite(gpuValue))
                        {
                            return false;
                        }

                        double signed =
                            (double)gpuValue -
                            candidateValue;

                        double absNormalized =
                            Math.Abs(signed);

                        double absLsb =
                            absNormalized *
                            255.0;

                        ChannelSignedSum[channel] +=
                            signed;

                        ChannelAbsSum[channel] +=
                            absNormalized;

                        ChannelSquaredSum[channel] +=
                            signed *
                            signed;

                        if (
                            absLsb >
                            ChannelMaxAbsLsb[channel])
                        {
                            ChannelMaxAbsLsb[channel] =
                                absLsb;
                        }

                        GlobalAbsLsbSum +=
                            absLsb;

                        GlobalSquaredNormalizedSum +=
                            signed *
                            signed;

                        if (
                            absLsb >
                            GlobalMaxAbsLsb)
                        {
                            GlobalMaxAbsLsb =
                                absLsb;
                        }

                        pairAbsLsbSum +=
                            absLsb;

                        if (
                            absLsb >
                            pairMaxLsb)
                        {
                            pairMaxLsb =
                                absLsb;
                        }

                        int bin =
                            HistogramBin(
                                absLsb);

                        GlobalHistogram[bin]++;
                        PairHistogram[bin]++;

                        ComparedValueCount++;

                        if (
                            BitConverter.SingleToInt32Bits(
                                candidateValue) ==
                            BitConverter.SingleToInt32Bits(
                                gpuValue))
                        {
                            ExactFloatMatchCount++;
                        }

                        if (absLsb <= 0.5)
                        {
                            WithinHalfLsbCount++;
                        }

                        if (absLsb <= 1.0)
                        {
                            WithinOneLsbCount++;
                        }

                        if (absLsb <= 2.0)
                        {
                            WithinTwoLsbCount++;
                        }

                        if (absLsb <= 4.0)
                        {
                            WithinFourLsbCount++;
                        }

                        if (absLsb <= 8.0)
                        {
                            WithinEightLsbCount++;
                        }
                    }
                }
            }

            PairMeanAbsLsb.Add(
                pairAbsLsbSum /
                InputFloatCount);

            PairP95AbsLsb.Add(
                HistogramPercentile(
                    PairHistogram,
                    InputFloatCount,
                    0.95));

            PairP99AbsLsb.Add(
                HistogramPercentile(
                    PairHistogram,
                    InputFloatCount,
                    0.99));

            PairMaxAbsLsb.Add(
                pairMaxLsb);

            return true;
        }
    }

    private readonly MetricAccumulator _currentToObserverMetrics =
        new MetricAccumulator(
            "CURRENT_FLOAT_TO_OBSERVER");

    private readonly MetricAccumulator _finalUnorm8ToObserverMetrics =
        new MetricAccumulator(
            "FINAL_UNORM8_TO_OBSERVER");

    private readonly MetricAccumulator _presentationFinalUnorm8ToObserverMetrics =
        new MetricAccumulator(
            "PRESENTATION_PLUS_FINAL_UNORM8_TO_OBSERVER");

    private readonly MetricAccumulator _snapshotToObserverDirectMetrics =
        new MetricAccumulator(
            "SNAPSHOT_TO_OBSERVER_DIRECT");

    private readonly MetricAccumulator _snapshotToObserverFlipYMetrics =
        new MetricAccumulator(
            "SNAPSHOT_TO_OBSERVER_FLIP_Y");

    private readonly List<double> _nativeCurrentCropCpuMs =
        new List<double>(128);

    private readonly List<double> _nativeFinalUnorm8CropCpuMs =
        new List<double>(128);

    private readonly List<double> _nativePresentationFinalUnorm8CropCpuMs =
        new List<double>(128);

    private readonly List<double> _nativeThreeCandidateWallMs =
        new List<double>(128);

    private readonly List<double> _observerSnapshotReadbackMs =
        new List<double>(128);

    private readonly List<double> _observerSnapshotReadbackFrames =
        new List<double>(128);

    private readonly List<double> _observerTensorReadbackMs =
        new List<double>(128);

    private readonly List<double> _observerTensorReadbackFrames =
        new List<double>(128);

    private readonly List<double> _productionInputReadbackMs =
        new List<double>(128);

    private readonly List<int> _productionInputReadbackFrames =
        new List<int>(128);

    private readonly List<double> _sourceAgeAtCaptureMs =
        new List<double>(128);

    private readonly List<double> _sourceAgeAtCompareMs =
        new List<double>(128);

    private readonly List<float> _productionTrackerLatency =
        new List<float>(4096);

    private readonly List<int> _productionActiveLanes =
        new List<int>(4096);

    private bool _reflectionReady;
    private bool _pairPending;
    private bool _measuring;
    private bool _reportWritten;
    private bool _gateAnnounced;

    private int _warmupPairsRemaining =
        WarmupPairCount;

    private int _expectedTriangles;
    private float _durationSeconds;
    private float _sampleHz;
    private float _stableSeconds;

    private double _stableSince = -1.0;
    private double _measurementStart;
    private double _nextPairAt;
    private double _nextDiscoveryAt;
    private double _nextSnapshotArmAt;

    private long _lastCapturedProductionStartedTicks;

    private const double SnapshotMatchTimeoutSeconds = 0.250;

    private bool _snapshotRequestActive;
    private double _snapshotArmRealtime;
    private ulong _snapshotSequence;
    private long _snapshotHostTicks;
    private long _snapshotManagedHostTicks;

    private int _snapshotArmCount;
    private int _snapshotReadyCount;
    private int _snapshotMatchTimeoutCount;
    private int _snapshotArmFailureCount;
    private int _snapshotCropFailureCount;
    private int _snapshotPresentedButNotScheduledCount;
    private int _snapshotSequenceObservedCount;
    private int _snapshotSequenceSkippedCount;
    private int _snapshotManagedTimestampMapFailureCount;

    private readonly List<double> _snapshotManagedMinusNativeTicks =
        new List<double>(128);

    private long _pendingStartedTicks;
    private long _pendingManagedSourceHostTicks;
    private long _pendingSourceHostTicks;
    private ulong _pendingNativeSequence;
    private long _pendingPairStartTicks;
    private int _pendingStartFrame;
    private bool _pendingIsWarmup;

    private int _pairAttemptCount;
    private int _pairCompletedCount;
    private int _pairErrorCount;
    private int _exactSlotMissCount;
    private int _laneRaceDiscardCount;
    private int _gpuInputReadbackErrorCount;
    private int _nonFinitePairCount;
    private int _inputShapeMismatchCount;

    private int _startScheduled;
    private int _startReadbackCompleted;
    private int _startCompleted;
    private int _startDropped;
    private int _startStale;

    private int _endScheduled;
    private int _endReadbackCompleted;
    private int _endCompleted;
    private int _endDropped;
    private int _endStale;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed)
        {
            return;
        }

        if (!ReadBoolEnvironment(
                EnableVariable,
                false))
        {
            return;
        }

        _installed = true;

        GameObject go =
            new GameObject(
                "[Kiwi] v44.55.9 Exact Pair Coherence / Outlier Isolation Audit");

        DontDestroyOnLoad(go);
        go.hideFlags =
            HideFlags.DontSave;

        go.AddComponent<
            KiwiExactPairCoherenceAuditV44_55_9>();
    }

    private void Awake()
    {
        _durationSeconds =
            Mathf.Clamp(
                ReadFloatEnvironment(
                    DurationVariable,
                    DefaultDurationSeconds),
                10f,
                180f);

        _sampleHz =
            Mathf.Clamp(
                ReadFloatEnvironment(
                    SampleHzVariable,
                    DefaultSampleHz),
                0.5f,
                5f);

        _stableSeconds =
            Mathf.Clamp(
                ReadFloatEnvironment(
                    StableSecondsVariable,
                    DefaultStableSeconds),
                2f,
                30f);

        _expectedTriangles =
            Mathf.Max(
                1,
                ReadIntEnvironment(
                    ExpectedTrianglesVariable,
                    DefaultExpectedTriangles));

        _observerSnapshotTexture =
            new RenderTexture(
                InputSize,
                InputSize,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                name =
                    "Kiwi v44.55.9 Observer Crop Snapshot",
                filterMode =
                    FilterMode.Bilinear,
                wrapMode =
                    TextureWrapMode.Clamp,
                useMipMap =
                    false,
                autoGenerateMips =
                    false,
                hideFlags =
                    HideFlags.DontSave
            };

        _observerSnapshotTexture.Create();

        _observerTensor =
            new Tensor<float>(
                new TensorShape(
                    1,
                    3,
                    InputSize,
                    InputSize));

        _observerTextureTransform =
            new TextureTransform()
                .SetTensorLayout(
                    TensorLayout.NCHW)
                .SetCoordOrigin(
                    CoordOrigin.TopLeft);

        _observerGraphicsCommandBuffer =
            new CommandBuffer
            {
                name =
                    "Kiwi v44.55.9 Observer TextureConverter"
            };

        Debug.Log(
            "[Kiwi v44.55.9 Coherence] WAIT_GATE " +
            "contract=" + Contract +
            " observerOnly=1" +
            " productionTrackerWrites=0" +
            " productionWorkerWrites=0" +
            " productionBackendChange=0" +
            " productionRoiWrites=0" +
            " productionCameraChange=0" +
            " productionTensorReadback=DISABLED" +
            " observerTensor=INDEPENDENT_GRAPHICS_QUEUE_TEXTURECONVERTER" +
            " observerSnapshot=OBSERVER_OWNED_RENDER_TEXTURE" +
            " sampleHz=" +
                _sampleHz.ToString(
                    "F2",
                    CultureInfo.InvariantCulture) +
            " stableSeconds=" +
                _stableSeconds.ToString(
                    "F1",
                    CultureInfo.InvariantCulture));
    }

    private void Update()
    {
        if (_reportWritten)
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        DiscoverRuntimeObjects(now);

        if (!_reflectionReady)
        {
            return;
        }

        SampleProductionTelemetry();

        if (!_measuring)
        {
            UpdateGate(now);
            return;
        }

        if (
            now - _measurementStart >=
            _durationSeconds)
        {
            if (!_pairPending)
            {
                CaptureEndCounters();
                WriteReport("COMPLETE");
            }

            return;
        }
    }

    private void LateUpdate()
    {
        if (
            _reportWritten ||
            !_reflectionReady ||
            _pairPending)
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        if (!_measuring)
        {
            if (
                _warmupPairsRemaining >
                0)
            {
                TryCaptureNewestProductionLane(
                    isWarmup: true);
            }

            return;
        }

        if (
            now <
            _nextPairAt)
        {
            return;
        }

        if (
            TryCaptureNewestProductionLane(
                isWarmup: false))
        {
            _nextPairAt =
                now +
                1.0 /
                _sampleHz;
        }
    }

    private void UpdateGate(
        double now)
    {
        bool ready =
            CountEnabledSkinnedTriangles() ==
                _expectedTriangles &&
            RuntimeReflectionStillValid() &&
            TrackerHasRegion() &&
            KiwiNativeCameraInterop.IsRunning &&
            KiwiNativeCameraInterop.SystemMemoryCaptureEnabled &&
            KiwiNativeCameraInterop.CaptureTransportId == 1;

        if (!ready)
        {
            _stableSince = -1.0;
            _gateAnnounced = false;
            return;
        }

        if (_stableSince < 0.0)
        {
            _stableSince = now;

            if (!_gateAnnounced)
            {
                _gateAnnounced = true;

                Debug.Log(
                    "[Kiwi v44.55.9 Coherence] GATE_MATCH " +
                    "triangles=" +
                        _expectedTriangles +
                    " trackerRegion=1" +
                    " nativePathB=1" +
                    " laneReflection=1" +
                    " waitingStableSeconds=" +
                        _stableSeconds.ToString(
                            "F1",
                            CultureInfo.InvariantCulture));
            }

            return;
        }

        if (
            now - _stableSince <
            _stableSeconds)
        {
            return;
        }

        Debug.Log(
            "[Kiwi v44.55.9 Coherence] READY_FOR_WARMUP " +
            "productionCropTextureFreeze=1" +
            " freezeBeforeCpuCandidates=1" +
            " sequenceMappedTimestampDomain=1" +
            " blockingGpuWait=0");

        // Warmup is intentionally driven by LateUpdate so a lane scheduled
        // earlier in this Unity frame can be captured without modifying Runner.
        _stableSince =
            double.PositiveInfinity;
    }

    private bool TryCaptureNewestProductionLane(
        bool isWarmup)
    {
        if (
            !_reflectionReady ||
            _pairPending ||
            _lanes == null)
        {
            return false;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        if (!_snapshotRequestActive)
        {
            if (
                now <
                _nextSnapshotArmAt)
            {
                return false;
            }

            if (!KiwiNativeCameraInterop
                    .TryArmDiagnosticCpuSnapshot())
            {
                _snapshotArmFailureCount++;
                return false;
            }

            _snapshotRequestActive = true;
            _snapshotArmRealtime = now;
            _snapshotSequence = 0;
            _snapshotHostTicks = 0;
            _snapshotManagedHostTicks = 0;
            _snapshotArmCount++;

            _nextSnapshotArmAt =
                now +
                1.0 /
                _sampleHz;

            return false;
        }

        if (_snapshotHostTicks <= 0)
        {
            if (
                KiwiNativeCameraInterop
                    .TryGetDiagnosticCpuSnapshotIdentity(
                        out ulong snapshotSequence,
                        out long snapshotHostTicks))
            {
                _snapshotSequence =
                    snapshotSequence;

                _snapshotHostTicks =
                    snapshotHostTicks;

                _snapshotReadyCount++;
            }
            else
            {
                if (
                    now -
                    _snapshotArmRealtime >
                    SnapshotMatchTimeoutSeconds)
                {
                    _snapshotMatchTimeoutCount++;
                    ResetSnapshotRequest();
                }

                return false;
            }
        }

        if (_snapshotManagedHostTicks <= 0)
        {
            ulong observedSequence =
                ReadRunnerULong(
                    _runnerLastObservedFreshSequenceField);

            long observedManagedHostTicks =
                ReadRunnerLong(
                    _runnerLatestSentisSourceHostTicksField);

            if (
                observedSequence ==
                    _snapshotSequence &&
                observedManagedHostTicks >
                    0)
            {
                _snapshotManagedHostTicks =
                    observedManagedHostTicks;

                _snapshotSequenceObservedCount++;

                _snapshotManagedMinusNativeTicks.Add(
                    (double)
                    _snapshotManagedHostTicks -
                    _snapshotHostTicks);
            }
            else if (
                observedSequence >
                    _snapshotSequence)
            {
                _snapshotSequenceSkippedCount++;
                ResetSnapshotRequest();
                return false;
            }
            else
            {
                if (
                    now -
                    _snapshotArmRealtime >
                    SnapshotMatchTimeoutSeconds)
                {
                    _snapshotMatchTimeoutCount++;
                    _snapshotManagedTimestampMapFailureCount++;
                    ResetSnapshotRequest();
                }

                return false;
            }
        }

        object matchedLane = null;
        long matchedStartedTicks = 0;

        for (
            int i = 0;
            i < _lanes.Length;
            i++)
        {
            object lane =
                _lanes.GetValue(i);

            if (lane == null)
            {
                continue;
            }

            bool pending =
                ReadLaneBool(
                    lane,
                    _laneReadbackPendingField);

            long started =
                ReadLaneLong(
                    lane,
                    _lanePendingStartedHostTicksField);

            long sourceHostTicks =
                ReadLaneLong(
                    lane,
                    _lanePendingSourceHostTicksField);

            if (
                !pending ||
                started <= 0 ||
                sourceHostTicks !=
                    _snapshotManagedHostTicks ||
                started <=
                    _lastCapturedProductionStartedTicks)
            {
                continue;
            }

            if (
                matchedLane == null ||
                started >
                    matchedStartedTicks)
            {
                matchedLane = lane;
                matchedStartedTicks =
                    started;
            }
        }

        if (matchedLane == null)
        {
            if (
                now -
                _snapshotArmRealtime >
                SnapshotMatchTimeoutSeconds)
            {
                _snapshotMatchTimeoutCount++;
                _snapshotPresentedButNotScheduledCount++;
                ResetSnapshotRequest();
            }

            return false;
        }

        _lastCapturedProductionStartedTicks =
            matchedStartedTicks;

        _pairAttemptCount++;

        Material cropMaterial =
            _laneCropMaterialField.GetValue(
                matchedLane)
            as Material;

        RenderTexture cropTexture =
            _laneCropTextureField.GetValue(
                matchedLane)
            as RenderTexture;

        if (
            cropMaterial == null ||
            cropTexture == null ||
            !cropTexture.IsCreated() ||
            cropTexture.width != InputSize ||
            cropTexture.height != InputSize ||
            _observerSnapshotTexture == null ||
            !_observerSnapshotTexture.IsCreated() ||
            _observerTensor == null ||
            _observerGraphicsCommandBuffer == null)
        {
            _inputShapeMismatchCount++;
            _pairErrorCount++;
            ResetSnapshotRequest();

            if (isWarmup)
            {
                WriteReport("WARMUP_FAIL");
            }

            return false;
        }

        Matrix4x4 samplingMatrix =
            cropMaterial.GetMatrix(
                XformId);

        FlattenMatrix(
            samplingMatrix,
            _samplingMatrixCurrent);

        long pairStart =
            Stopwatch.GetTimestamp();

        // IMPORTANT v44.55.9 change:
        // Freeze the matched Production cropTexture FIRST, before any 192x192
        // Native CPU candidate work. CopyTexture -> ToTensor -> snapshot
        // readback are submitted in this order on the same observer graphics
        // CommandBuffer.
        _pairPending = true;
        _pendingIsWarmup =
            isWarmup;
        _pendingStartedTicks =
            matchedStartedTicks;
        _pendingSourceHostTicks =
            _snapshotHostTicks;
        _pendingManagedSourceHostTicks =
            _snapshotManagedHostTicks;
        _pendingNativeSequence =
            _snapshotSequence;
        _pendingPairStartTicks =
            pairStart;
        _pendingStartFrame =
            Time.frameCount;

        _pendingSnapshotReadbackDone =
            false;
        _pendingObserverTensorReadbackDone =
            false;
        _pendingProductionTensorReadbackDone =
            true;
        _pendingCpuCandidatesDone =
            false;

        int pairToken =
            ++_pendingPairToken;

        PairRecord pairRecord =
            isWarmup
                ? null
                : new PairRecord
                {
                    Index =
                        _pairCompletedCount,
                    Sequence =
                        _snapshotSequence,
                    NativeHostTicks =
                        _snapshotHostTicks,
                    ManagedHostTicks =
                        _snapshotManagedHostTicks,
                    LaneStartedTicks =
                        matchedStartedTicks,
                    SourceAgeAtFreezeSubmitMs =
                        QpcAgeMilliseconds(
                            _snapshotHostTicks)
                };

        _pendingPairRecord =
            pairRecord;

        int snapshotReadbackStartFrame =
            Time.frameCount;

        long snapshotReadbackStartTicks =
            Stopwatch.GetTimestamp();

        CommandBuffer observerCb =
            _observerGraphicsCommandBuffer;

        observerCb.Clear();

        observerCb.CopyTexture(
            cropTexture,
            _observerSnapshotTexture);

        observerCb.ToTensor(
            _observerSnapshotTexture,
            _observerTensor,
            _observerTextureTransform);

        observerCb.RequestAsyncReadback(
            _observerSnapshotTexture,
            0,
            TextureFormat.RGBA32,
            request =>
            {
                if (
                    pairToken !=
                        _pendingPairToken ||
                    !_pairPending)
                {
                    return;
                }

                if (request.hasError)
                {
                    HandlePairReadbackError(
                        isWarmup,
                        "OBSERVER_SNAPSHOT_ASYNC_GPU_READBACK_ERROR");
                    return;
                }

                try
                {
                    var data =
                        request.GetData<byte>();

                    int expectedBytes =
                        InputSize *
                        InputSize *
                        4;

                    if (
                        data.Length <
                        expectedBytes)
                    {
                        throw new InvalidOperationException(
                            "Observer snapshot readback byte count mismatch.");
                    }

                    ConvertSnapshotRgbaToNchw(
                        data);

                    double readbackMs =
                        TicksToMilliseconds(
                            Stopwatch.GetTimestamp() -
                            snapshotReadbackStartTicks);

                    if (!isWarmup)
                    {
                        _observerSnapshotReadbackMs.Add(
                            readbackMs);

                        _observerSnapshotReadbackFrames.Add(
                            Mathf.Max(
                                0,
                                Time.frameCount -
                                snapshotReadbackStartFrame));

                        if (_pendingPairRecord != null)
                        {
                            _pendingPairRecord.SnapshotReadbackMs =
                                readbackMs;
                        }
                    }

                    _pendingSnapshotReadbackDone =
                        true;

                    TryFinishPendingPair(
                        pairToken);
                }
                catch (Exception exception)
                {
                    HandlePairReadbackError(
                        isWarmup,
                        "OBSERVER_SNAPSHOT_READBACK_EXCEPTION " +
                        exception.GetType().Name +
                        " " +
                        exception.Message);
                }
            });

        long freezeSubmitBegin =
            Stopwatch.GetTimestamp();

        Graphics.ExecuteCommandBuffer(
            observerCb);

        long freezeSubmitEnd =
            Stopwatch.GetTimestamp();

        if (
            !isWarmup &&
            _pendingPairRecord != null)
        {
            _pendingPairRecord.FreezeSubmitCpuMs =
                TicksToMilliseconds(
                    freezeSubmitEnd -
                    freezeSubmitBegin);
        }

        int observerTensorReadbackStartFrame =
            Time.frameCount;

        long observerTensorReadbackStartTicks =
            Stopwatch.GetTimestamp();

        var observerAwaiter =
            _observerTensor
                .ReadbackAndCloneAsync()
                .GetAwaiter();

        observerAwaiter.OnCompleted(
            () =>
            {
                if (
                    pairToken !=
                        _pendingPairToken ||
                    !_pairPending)
                {
                    return;
                }

                Tensor<float> readable =
                    null;

                try
                {
                    readable =
                        observerAwaiter.GetResult();

                    if (
                        readable == null ||
                        readable.shape.length !=
                            InputFloatCount)
                    {
                        throw new InvalidOperationException(
                            "Observer tensor readback shape mismatch.");
                    }

                    for (
                        int i = 0;
                        i < InputFloatCount;
                        i++)
                    {
                        _observerTensorNchw[i] =
                            readable[i];
                    }

                    double readbackMs =
                        TicksToMilliseconds(
                            Stopwatch.GetTimestamp() -
                            observerTensorReadbackStartTicks);

                    if (!isWarmup)
                    {
                        _observerTensorReadbackMs.Add(
                            readbackMs);

                        _observerTensorReadbackFrames.Add(
                            Mathf.Max(
                                0,
                                Time.frameCount -
                                observerTensorReadbackStartFrame));

                        if (_pendingPairRecord != null)
                        {
                            _pendingPairRecord.ObserverTensorReadbackMs =
                                readbackMs;
                        }
                    }

                    _pendingObserverTensorReadbackDone =
                        true;

                    TryFinishPendingPair(
                        pairToken);
                }
                catch (Exception exception)
                {
                    HandlePairReadbackError(
                        isWarmup,
                        "OBSERVER_TENSOR_READBACK_EXCEPTION " +
                        exception.GetType().Name +
                        " " +
                        exception.Message);
                }
                finally
                {
                    readable?.Dispose();
                }
            });

        // Only after the Production texture is frozen do we perform the two
        // CPU-side candidates.
        long nativeBegin =
            Stopwatch.GetTimestamp();

        bool currentCopied =
            TryCopyQuantizationCandidate(
                0,
                _nativeCurrentNchw,
                out ulong currentSequence,
                out long currentHostTicks,
                out ulong currentCpuMicroseconds);

        bool presentationFinalCopied =
            TryCopyQuantizationCandidate(
                2,
                _nativePresentationFinalUnorm8Nchw,
                out ulong presentationFinalSequence,
                out long presentationFinalHostTicks,
                out ulong presentationFinalCpuMicroseconds);

        long afterNative =
            Stopwatch.GetTimestamp();

        if (
            !currentCopied ||
            !presentationFinalCopied ||
            currentHostTicks !=
                _snapshotHostTicks ||
            presentationFinalHostTicks !=
                _snapshotHostTicks ||
            currentSequence !=
                _snapshotSequence ||
            presentationFinalSequence !=
                _snapshotSequence)
        {
            _snapshotCropFailureCount++;
            _pairErrorCount++;
            _pairPending = false;
            _pendingPairRecord = null;
            ResetSnapshotRequest();

            if (isWarmup)
            {
                WriteReport("WARMUP_FAIL");
            }

            return false;
        }

        bool laneStillMatched =
            ReadLaneBool(
                matchedLane,
                _laneReadbackPendingField) &&
            ReadLaneLong(
                matchedLane,
                _lanePendingStartedHostTicksField) ==
                matchedStartedTicks &&
            ReadLaneLong(
                matchedLane,
                _lanePendingSourceHostTicksField) ==
                _snapshotManagedHostTicks;

        if (!laneStillMatched)
        {
            _laneChangedAfterFreezeCount++;
        }

        if (!isWarmup)
        {
            _nativeCurrentCropCpuMs.Add(
                currentCpuMicroseconds /
                1000.0);

            _nativePresentationFinalUnorm8CropCpuMs.Add(
                presentationFinalCpuMicroseconds /
                1000.0);

            _nativeThreeCandidateWallMs.Add(
                TicksToMilliseconds(
                    afterNative -
                    nativeBegin));

            _sourceAgeAtCaptureMs.Add(
                QpcAgeMilliseconds(
                    _snapshotHostTicks));

            if (_pendingPairRecord != null)
            {
                _pendingPairRecord.SourceAgeAfterCpuMs =
                    QpcAgeMilliseconds(
                        _snapshotHostTicks);

                _pendingPairRecord.LaneStillMatchedAfterCpu =
                    laneStillMatched;
            }
        }

        _pendingCpuCandidatesDone =
            true;

        TryFinishPendingPair(
            pairToken);

        return true;
    }


    private void ResetSnapshotRequest()
    {
        _snapshotRequestActive = false;
        _snapshotSequence = 0;
        _snapshotHostTicks = 0;
        _snapshotManagedHostTicks = 0;
    }

    private void ConvertSnapshotRgbaToNchw(
        Unity.Collections.NativeArray<byte> data)
    {
        for (
            int y = 0;
            y < InputSize;
            y++)
        {
            int flippedY =
                InputSize -
                1 -
                y;

            for (
                int x = 0;
                x < InputSize;
                x++)
            {
                int rgba =
                    (
                        y *
                        InputSize +
                        x
                    ) *
                    4;

                int directIndex =
                    y *
                    InputSize +
                    x;

                int flipIndex =
                    flippedY *
                    InputSize +
                    x;

                float r =
                    data[rgba] /
                    255.0f;

                float g =
                    data[rgba + 1] /
                    255.0f;

                float b =
                    data[rgba + 2] /
                    255.0f;

                _snapshotDirectNchw[directIndex] =
                    r;

                _snapshotDirectNchw[
                    PlaneLength +
                    directIndex] =
                    g;

                _snapshotDirectNchw[
                    PlaneLength * 2 +
                    directIndex] =
                    b;

                _snapshotFlipYNchw[flipIndex] =
                    r;

                _snapshotFlipYNchw[
                    PlaneLength +
                    flipIndex] =
                    g;

                _snapshotFlipYNchw[
                    PlaneLength * 2 +
                    flipIndex] =
                    b;
            }
        }
    }

    private void TryFinishPendingPair(
        int pairToken)
    {
        if (
            pairToken !=
                _pendingPairToken ||
            !_pairPending ||
            !_pendingSnapshotReadbackDone ||
            !_pendingObserverTensorReadbackDone ||
            !_pendingProductionTensorReadbackDone ||
            !_pendingCpuCandidatesDone)
        {
            return;
        }

        FinishPair();
    }


    private void HandlePairReadbackError(
        bool isWarmup,
        string message)
    {
        _gpuInputReadbackErrorCount++;
        _pairErrorCount++;
        _pairPending = false;
        _pendingPairRecord = null;
        ResetSnapshotRequest();

        Debug.LogWarning(
            "[Kiwi v44.55.9 Coherence] READBACK_ERROR " +
            message);

        if (isWarmup)
        {
            WriteReport("WARMUP_FAIL");
        }
        else
        {
            WriteReport("PAIR_FAIL");
        }
    }


    private void FinishPair()
    {
        bool isWarmup =
            _pendingIsWarmup;

        bool finite =
            ValidateFiniteInputs();

        _pairPending = false;
        ResetSnapshotRequest();

        if (!finite)
        {
            _pendingPairRecord = null;
            _nonFinitePairCount++;
            _pairErrorCount++;

            WriteReport(
                isWarmup
                    ? "WARMUP_FAIL"
                    : "PAIR_FAIL");

            return;
        }

        if (isWarmup)
        {
            _pendingPairRecord = null;
            _warmupPairsRemaining--;

            if (
                _warmupPairsRemaining <=
                0)
            {
                BeginMeasurement();
            }

            return;
        }

        if (!CompareMeasuredPair())
        {
            _pendingPairRecord = null;
            _nonFinitePairCount++;
            _pairErrorCount++;
            WriteReport("PAIR_FAIL");
            return;
        }

        _pairCompletedCount++;
        _pendingPairRecord = null;
    }


    private bool ValidateFiniteInputs()
    {
        for (
            int i = 0;
            i < InputFloatCount;
            i++)
        {
            if (
                !IsFinite(_nativeCurrentNchw[i]) ||
                !IsFinite(_nativePresentationFinalUnorm8Nchw[i]) ||
                !IsFinite(_snapshotDirectNchw[i]) ||
                !IsFinite(_snapshotFlipYNchw[i]) ||
                !IsFinite(_observerTensorNchw[i]))
            {
                return false;
            }
        }

        return true;
    }


    private bool CompareMeasuredPair()
    {
        bool currentOk =
            _currentToObserverMetrics.AccumulatePair(
                _nativeCurrentNchw,
                _observerTensorNchw);

        bool quantizedOk =
            _presentationFinalUnorm8ToObserverMetrics.AccumulatePair(
                _nativePresentationFinalUnorm8Nchw,
                _observerTensorNchw);

        bool snapshotFlipOk =
            _snapshotToObserverFlipYMetrics.AccumulatePair(
                _snapshotFlipYNchw,
                _observerTensorNchw);

        if (
            !currentOk ||
            !quantizedOk ||
            !snapshotFlipOk)
        {
            return false;
        }

        double currentMae =
            _currentToObserverMetrics.LastPairMeanAbsLsb;

        double quantizedMae =
            _presentationFinalUnorm8ToObserverMetrics
                .LastPairMeanAbsLsb;

        double snapshotFlipMae =
            _snapshotToObserverFlipYMetrics
                .LastPairMeanAbsLsb;

        if (
            snapshotFlipMae >
            0.001)
        {
            _snapshotTensorMismatchCount++;
        }

        if (
            quantizedMae >
            0.5)
        {
            _quantizedOverHalfLsbCount++;
        }

        if (
            quantizedMae >
            1.0)
        {
            _quantizedOverOneLsbCount++;
        }

        if (
            quantizedMae >
            2.0)
        {
            _quantizedOverTwoLsbCount++;
        }

        if (_pendingPairRecord != null)
        {
            _pendingPairRecord.CurrentMeanAbsLsb =
                currentMae;

            _pendingPairRecord.QuantizedMeanAbsLsb =
                quantizedMae;

            _pendingPairRecord.QuantizedToCurrentRatio =
                currentMae > 0.0
                    ? quantizedMae /
                        currentMae
                    : 0.0;

            _pendingPairRecord.SnapshotFlipYMeanAbsLsb =
                snapshotFlipMae;

            int metricIndex =
                _presentationFinalUnorm8ToObserverMetrics
                    .PairCount -
                1;

            if (metricIndex >= 0)
            {
                _pendingPairRecord.QuantizedPairP95Lsb =
                    _presentationFinalUnorm8ToObserverMetrics
                        .PairP95AbsLsb[
                            metricIndex];

                _pendingPairRecord.QuantizedPairP99Lsb =
                    _presentationFinalUnorm8ToObserverMetrics
                        .PairP99AbsLsb[
                            metricIndex];

                _pendingPairRecord.QuantizedPairMaxLsb =
                    _presentationFinalUnorm8ToObserverMetrics
                        .PairMaxAbsLsb[
                            metricIndex];
            }

            _pairRecords.Add(
                _pendingPairRecord);
        }

        return true;
    }







    private void BeginMeasurement()
    {
        _measuring = true;

        _measurementStart =
            Time.realtimeSinceStartupAsDouble;

        _nextPairAt =
            _measurementStart;

        CaptureStartCounters();

        Debug.Log(
            "[Kiwi v44.55.9 Coherence] MEASURE_START " +
            "observerOnly=1" +
            " freezeOrder=MATCH_THEN_COPYTEXTURE_BEFORE_CPU_CROP" +
            " observerTensor=FROZEN_PRIVATE_RT_TEXTURECONVERTER" +
            " candidates=CURRENT_FLOAT,PRESENTATION_PLUS_FINAL_UNORM8" +
            " perPairIdentityTelemetry=1" +
            " productionLaneInputReadback=0" +
            " warmupStatsExcluded=1" +
            " armedNativeSnapshotMatch=1" +
            " sequenceMappedTimestampDomain=1" +
            " sampleHz=" +
                _sampleHz.ToString(
                    "F2",
                    CultureInfo.InvariantCulture) +
            " durationSeconds=" +
                _durationSeconds.ToString(
                    "F1",
                    CultureInfo.InvariantCulture) +
            " productionWrites=0");
    }


    private void DiscoverRuntimeObjects(
        double now)
    {
        if (
            _reflectionReady &&
            RuntimeReflectionStillValid())
        {
            return;
        }

        if (
            now <
            _nextDiscoveryAt)
        {
            return;
        }

        _nextDiscoveryAt =
            now + 0.5;

        BindingFlags flags =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic;

        MonoBehaviour[] behaviours =
            FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

        for (
            int i = 0;
            i < behaviours.Length;
            i++)
        {
            MonoBehaviour behaviour =
                behaviours[i];

            if (
                behaviour == null ||
                behaviour.GetType().Name !=
                    "FaceLandmarkerRunner")
            {
                continue;
            }

            Type runnerType =
                behaviour.GetType();

            FieldInfo trackerField =
                runnerType.GetField(
                    "_sentisTracker",
                    flags);

            FieldInfo lastObservedFreshSequenceField =
                runnerType.GetField(
                    "_lastObservedFreshFrameSequence",
                    flags);

            FieldInfo latestSentisSourceHostTicksField =
                runnerType.GetField(
                    "_latestSentisSourceFrameHostTicks",
                    flags);

            if (
                trackerField == null ||
                lastObservedFreshSequenceField == null ||
                latestSentisSourceHostTicksField == null)
            {
                continue;
            }

            object tracker =
                trackerField.GetValue(
                    behaviour);

            if (tracker == null)
            {
                continue;
            }

            Type trackerType =
                tracker.GetType();

            FieldInfo lanesField =
                trackerType.GetField(
                    "_lanes",
                    flags);

            FieldInfo sourceWidthField =
                trackerType.GetField(
                    "_sourceWidth",
                    flags);

            FieldInfo sourceHeightField =
                trackerType.GetField(
                    "_sourceHeight",
                    flags);

            if (
                lanesField == null ||
                sourceWidthField == null ||
                sourceHeightField == null)
            {
                continue;
            }

            Array lanes =
                lanesField.GetValue(
                    tracker)
                as Array;

            if (
                lanes == null ||
                lanes.Length == 0)
            {
                continue;
            }

            object lane0 =
                lanes.GetValue(0);

            if (lane0 == null)
            {
                continue;
            }

            Type laneType =
                lane0.GetType();

            FieldInfo inputField =
                laneType.GetField(
                    "input",
                    flags);

            FieldInfo cropTextureField =
                laneType.GetField(
                    "cropTexture",
                    flags);

            FieldInfo cropMaterialField =
                laneType.GetField(
                    "cropMaterial",
                    flags);

            FieldInfo readbackPendingField =
                laneType.GetField(
                    "readbackPending",
                    flags);

            FieldInfo pendingSourceHostTicksField =
                laneType.GetField(
                    "pendingSourceHostTicks",
                    flags);

            FieldInfo pendingStartedHostTicksField =
                laneType.GetField(
                    "pendingStartedHostTicks",
                    flags);

            FieldInfo pendingCropMatrixField =
                laneType.GetField(
                    "pendingCropMatrix",
                    flags);

            if (
                inputField == null ||
                cropTextureField == null ||
                cropMaterialField == null ||
                readbackPendingField == null ||
                pendingSourceHostTicksField == null ||
                pendingStartedHostTicksField == null ||
                pendingCropMatrixField == null)
            {
                continue;
            }

            _runner = behaviour;
            _tracker = tracker;
            _trackerType = trackerType;
            _trackerField = trackerField;
            _lanesField = lanesField;
            _trackerSourceWidthField =
                sourceWidthField;
            _trackerSourceHeightField =
                sourceHeightField;
            _runnerLastObservedFreshSequenceField =
                lastObservedFreshSequenceField;
            _runnerLatestSentisSourceHostTicksField =
                latestSentisSourceHostTicksField;
            _laneType = laneType;
            _laneInputField = inputField;
            _laneCropTextureField =
                cropTextureField;
            _laneCropMaterialField =
                cropMaterialField;
            _laneReadbackPendingField =
                readbackPendingField;
            _lanePendingSourceHostTicksField =
                pendingSourceHostTicksField;
            _lanePendingStartedHostTicksField =
                pendingStartedHostTicksField;
            _lanePendingCropMatrixField =
                pendingCropMatrixField;
            _lanes = lanes;

            _reflectionReady = true;

            Debug.Log(
                "[Kiwi v44.55.9 Coherence] REFLECTION_READY " +
                "tracker=" +
                    trackerType.FullName +
                " laneType=" +
                    laneType.FullName +
                " laneCount=" +
                    lanes.Length +
                " readOnlyFields=12" +
                " materialRead=_Xform" +
                " SetValueCalls=0");

            return;
        }
    }

    private bool RuntimeReflectionStillValid()
    {
        if (
            !_reflectionReady ||
            _runner == null ||
            _tracker == null ||
            _trackerField == null ||
            _lanesField == null ||
            _trackerSourceWidthField == null ||
            _trackerSourceHeightField == null ||
            _runnerLastObservedFreshSequenceField == null ||
            _runnerLatestSentisSourceHostTicksField == null ||
            _laneCropTextureField == null)
        {
            return false;
        }

        try
        {
            if (
                !ReferenceEquals(
                    _trackerField.GetValue(
                        _runner),
                    _tracker))
            {
                _reflectionReady = false;
                return false;
            }

            Array currentLanes =
                _lanesField.GetValue(
                    _tracker)
                as Array;

            if (
                currentLanes == null ||
                !ReferenceEquals(
                    currentLanes,
                    _lanes))
            {
                _reflectionReady = false;
                return false;
            }

            return true;
        }
        catch
        {
            _reflectionReady = false;
            return false;
        }
    }

    private bool TrackerHasRegion()
    {
        object value =
            ReadTrackerProperty(
                "HasRegion");

        return
            value is bool flag &&
            flag;
    }

    private object ReadTrackerProperty(
        string propertyName)
    {
        if (
            _tracker == null ||
            _trackerType == null)
        {
            return null;
        }

        try
        {
            PropertyInfo property =
                _trackerType.GetProperty(
                    propertyName,
                    BindingFlags.Instance |
                    BindingFlags.Public);

            return
                property != null
                    ? property.GetValue(
                        _tracker)
                    : null;
        }
        catch
        {
            return null;
        }
    }

    private int ReadTrackerInt(
        string propertyName,
        int fallback)
    {
        object value =
            ReadTrackerProperty(
                propertyName);

        return
            value is int integer
                ? integer
                : fallback;
    }

    private float ReadTrackerFloat(
        string propertyName,
        float fallback)
    {
        object value =
            ReadTrackerProperty(
                propertyName);

        if (value is float single)
        {
            return single;
        }

        if (value is double dbl)
        {
            return (float)dbl;
        }

        return fallback;
    }

    private ulong ReadRunnerULong(
        FieldInfo field)
    {
        if (
            _runner == null ||
            field == null)
        {
            return 0UL;
        }

        try
        {
            object value =
                field.GetValue(
                    _runner);

            return
                value is ulong sequence
                    ? sequence
                    : 0UL;
        }
        catch
        {
            return 0UL;
        }
    }

    private long ReadRunnerLong(
        FieldInfo field)
    {
        if (
            _runner == null ||
            field == null)
        {
            return 0L;
        }

        try
        {
            object value =
                field.GetValue(
                    _runner);

            return
                value is long ticks
                    ? ticks
                    : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    private static bool ReadLaneBool(
        object lane,
        FieldInfo field)
    {
        if (
            lane == null ||
            field == null)
        {
            return false;
        }

        object value =
            field.GetValue(
                lane);

        return
            value is bool flag &&
            flag;
    }

    private static long ReadLaneLong(
        object lane,
        FieldInfo field)
    {
        if (
            lane == null ||
            field == null)
        {
            return 0L;
        }

        object value =
            field.GetValue(
                lane);

        return
            value is long ticks
                ? ticks
                : 0L;
    }

    private static void FlattenMatrix(
        Matrix4x4 matrix,
        float[] destination)
    {
        if (
            destination == null ||
            destination.Length != 16)
        {
            throw new ArgumentException(
                "Matrix destination must contain exactly 16 floats.",
                nameof(destination));
        }

        destination[0] = matrix.m00;
        destination[1] = matrix.m01;
        destination[2] = matrix.m02;
        destination[3] = matrix.m03;

        destination[4] = matrix.m10;
        destination[5] = matrix.m11;
        destination[6] = matrix.m12;
        destination[7] = matrix.m13;

        destination[8] = matrix.m20;
        destination[9] = matrix.m21;
        destination[10] = matrix.m22;
        destination[11] = matrix.m23;

        destination[12] = matrix.m30;
        destination[13] = matrix.m31;
        destination[14] = matrix.m32;
        destination[15] = matrix.m33;
    }

    private bool TryCopyQuantizationCandidate(
        int quantizationMode,
        float[] destination,
        out ulong sequence,
        out long matchedHostTicks,
        out ulong nativeCpuMicroseconds)
    {
        return
            KiwiNativeCameraInterop
                .TryCopyDiagnosticCpuSnapshotCropNchwFloatQuantized(
                    _snapshotHostTicks,
                    _samplingMatrixCurrent,
                    InputSize,
                    quantizationMode,
                    destination,
                    out sequence,
                    out matchedHostTicks,
                    out nativeCpuMicroseconds);
    }


    private void SampleProductionTelemetry()
    {
        if (
            !_measuring ||
            _tracker == null)
        {
            return;
        }

        float latency =
            ReadTrackerFloat(
                "LatestLatencyMs",
                0f);

        if (
            latency > 0f &&
            IsFinite(latency))
        {
            _productionTrackerLatency.Add(
                latency);
        }

        _productionActiveLanes.Add(
            ReadTrackerInt(
                "ActiveLaneCount",
                0));
    }

    private void CaptureStartCounters()
    {
        _startScheduled =
            ReadTrackerInt(
                "ScheduledFrameCount",
                0);

        _startReadbackCompleted =
            ReadTrackerInt(
                "ReadbackCompletedFrameCount",
                0);

        _startCompleted =
            ReadTrackerInt(
                "CompletedFrameCount",
                0);

        _startDropped =
            ReadTrackerInt(
                "DroppedFreshFrameCount",
                0);

        _startStale =
            ReadTrackerInt(
                "DiscardedStaleFrameCount",
                0);
    }

    private void CaptureEndCounters()
    {
        _endScheduled =
            ReadTrackerInt(
                "ScheduledFrameCount",
                _startScheduled);

        _endReadbackCompleted =
            ReadTrackerInt(
                "ReadbackCompletedFrameCount",
                _startReadbackCompleted);

        _endCompleted =
            ReadTrackerInt(
                "CompletedFrameCount",
                _startCompleted);

        _endDropped =
            ReadTrackerInt(
                "DroppedFreshFrameCount",
                _startDropped);

        _endStale =
            ReadTrackerInt(
                "DiscardedStaleFrameCount",
                _startStale);
    }

    private int CountEnabledSkinnedTriangles()
    {
        SkinnedMeshRenderer[] renderers =
            FindObjectsByType<SkinnedMeshRenderer>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

        long total = 0L;

        for (
            int i = 0;
            i < renderers.Length;
            i++)
        {
            SkinnedMeshRenderer renderer =
                renderers[i];

            if (
                renderer == null ||
                !renderer.enabled ||
                renderer.forceRenderingOff ||
                renderer.sharedMesh == null)
            {
                continue;
            }

            Mesh mesh =
                renderer.sharedMesh;

            for (
                int s = 0;
                s < mesh.subMeshCount;
                s++)
            {
                if (
                    mesh.GetTopology(s) ==
                    MeshTopology.Triangles)
                {
                    total +=
                        (long)
                        mesh.GetIndexCount(s) /
                        3L;
                }
            }
        }

        return
            total > int.MaxValue
                ? int.MaxValue
                : (int)total;
    }

    private static int HistogramBin(
        double absLsb)
    {
        if (
            double.IsNaN(absLsb) ||
            double.IsInfinity(absLsb) ||
            absLsb < 0.0)
        {
            return HistogramOverflowBin;
        }

        int bin =
            (int)Math.Floor(
                absLsb *
                HistogramBinsPerLsb);

        if (
            bin < 0)
        {
            return 0;
        }

        if (
            bin >=
            HistogramFiniteBinCount)
        {
            return HistogramOverflowBin;
        }

        return bin;
    }

    private static double HistogramPercentile(
        int[] histogram,
        long count,
        double percentile)
    {
        if (
            histogram == null ||
            count <= 0)
        {
            return 0.0;
        }

        long target =
            (long)Math.Ceiling(
                Math.Max(
                    1.0,
                    count *
                    percentile));

        long cumulative = 0;

        for (
            int i = 0;
            i < histogram.Length;
            i++)
        {
            cumulative +=
                histogram[i];

            if (
                cumulative <
                target)
            {
                continue;
            }

            if (
                i >=
                HistogramFiniteBinCount)
            {
                return 64.0;
            }

            return
                (
                    i +
                    1
                ) /
                HistogramBinsPerLsb;
        }

        return 64.0;
    }

    private static double HistogramPercentile(
        long[] histogram,
        long count,
        double percentile)
    {
        if (
            histogram == null ||
            count <= 0)
        {
            return 0.0;
        }

        long target =
            (long)Math.Ceiling(
                Math.Max(
                    1.0,
                    count *
                    percentile));

        long cumulative = 0;

        for (
            int i = 0;
            i < histogram.Length;
            i++)
        {
            cumulative +=
                histogram[i];

            if (
                cumulative <
                target)
            {
                continue;
            }

            if (
                i >=
                HistogramFiniteBinCount)
            {
                return 64.0;
            }

            return
                (
                    i +
                    1
                ) /
                HistogramBinsPerLsb;
        }

        return 64.0;
    }

    private double QpcAgeMilliseconds(
        long hostTicks)
    {
        if (hostTicks <= 0L)
        {
            return 0.0;
        }

        long frequency =
            KiwiNativeCameraInterop.QpcFrequency;

        long now =
            KiwiNativeCameraInterop.QpcNow;

        if (
            frequency <= 0L ||
            now <= hostTicks)
        {
            return 0.0;
        }

        return
            (now - hostTicks) *
            1000.0 /
            frequency;
    }

    private static bool IsFinite(
        float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
    }

    private static double Ratio(
        long numerator,
        long denominator)
    {
        if (denominator <= 0L)
        {
            return 0.0;
        }

        return
            (double)numerator /
            denominator;
    }

    private static double TicksToMilliseconds(
        long ticks)
    {
        return
            ticks *
            1000.0 /
            Stopwatch.Frequency;
    }

    private static void AppendDoubleStats(
        List<string> lines,
        string name,
        List<double> values)
    {
        if (
            values == null ||
            values.Count == 0)
        {
            lines.Add(name + ".samples=0");
            return;
        }

        double[] data =
            values.ToArray();

        Array.Sort(data);

        double sum = 0.0;

        for (
            int i = 0;
            i < data.Length;
            i++)
        {
            sum += data[i];
        }

        lines.Add(
            name +
            ".samples=" +
            data.Length);

        lines.Add(
            name +
            ".mean=" +
            (
                sum /
                data.Length
            ).ToString(
                "F9",
                CultureInfo.InvariantCulture));

        lines.Add(
            name +
            ".median=" +
            Percentile(
                data,
                0.50).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            name +
            ".p95=" +
            Percentile(
                data,
                0.95).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            name +
            ".max=" +
            data[data.Length - 1].ToString(
                "F9",
                CultureInfo.InvariantCulture));
    }

    private static void AppendFloatStats(
        List<string> lines,
        string name,
        List<float> values)
    {
        if (
            values == null ||
            values.Count == 0)
        {
            lines.Add(name + ".samples=0");
            return;
        }

        List<double> converted =
            new List<double>(
                values.Count);

        for (
            int i = 0;
            i < values.Count;
            i++)
        {
            converted.Add(
                values[i]);
        }

        AppendDoubleStats(
            lines,
            name,
            converted);
    }

    private static void AppendIntStats(
        List<string> lines,
        string name,
        List<int> values)
    {
        if (
            values == null ||
            values.Count == 0)
        {
            lines.Add(name + ".samples=0");
            return;
        }

        List<double> converted =
            new List<double>(
                values.Count);

        for (
            int i = 0;
            i < values.Count;
            i++)
        {
            converted.Add(
                values[i]);
        }

        AppendDoubleStats(
            lines,
            name,
            converted);
    }

    private static double Percentile(
        double[] sorted,
        double percentile)
    {
        if (
            sorted == null ||
            sorted.Length == 0)
        {
            return 0.0;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        double position =
            (
                sorted.Length -
                1
            ) *
            percentile;

        int lower =
            Mathf.Clamp(
                (int)Math.Floor(
                    position),
                0,
                sorted.Length - 1);

        int upper =
            Mathf.Clamp(
                (int)Math.Ceiling(
                    position),
                0,
                sorted.Length - 1);

        if (
            lower ==
            upper)
        {
            return
                sorted[lower];
        }

        double t =
            position -
            lower;

        return
            sorted[lower] +
            (
                sorted[upper] -
                sorted[lower]
            ) *
            t;
    }

    private static double MeanAbsLsb(
        MetricAccumulator metrics)
    {
        if (
            metrics == null ||
            metrics.ComparedValueCount <= 0)
        {
            return 0.0;
        }

        return
            metrics.GlobalAbsLsbSum /
            metrics.ComparedValueCount;
    }

    private static double SafeRatio(
        double numerator,
        double denominator)
    {
        if (
            denominator <= 0.0)
        {
            return
                numerator <= 0.0
                    ? 1.0
                    : 999999.0;
        }

        return
            numerator /
            denominator;
    }

    private void AppendBestPairReport(
        List<string> lines,
        string name,
        int bestPairCount)
    {
        lines.Add(
            name +
            ".bestPairCount=" +
            bestPairCount);

        lines.Add(
            name +
            ".bestPairRatio=" +
            Ratio(
                bestPairCount,
                _pairCompletedCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));
    }

    private static void AppendMetricReport(
        List<string> lines,
        string prefix,
        MetricAccumulator metrics)
    {
        if (
            lines == null ||
            metrics == null)
        {
            return;
        }

        lines.Add(
            prefix +
            ".pairCount=" +
            metrics.PairCount);

        lines.Add(
            prefix +
            ".comparedValueCount=" +
            metrics.ComparedValueCount);

        lines.Add(
            prefix +
            ".globalMeanAbsLsb=" +
            (
                metrics.ComparedValueCount > 0
                    ? metrics.GlobalAbsLsbSum /
                        metrics.ComparedValueCount
                    : 0.0
            ).ToString(
                "F9",
                CultureInfo.InvariantCulture));

        double globalMse =
            metrics.ComparedValueCount > 0
                ? metrics.GlobalSquaredNormalizedSum /
                    metrics.ComparedValueCount
                : 0.0;

        lines.Add(
            prefix +
            ".globalRmseNormalized=" +
            Math.Sqrt(
                globalMse).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            prefix +
            ".globalRmseLsb=" +
            (
                Math.Sqrt(
                    globalMse) *
                255.0
            ).ToString(
                "F9",
                CultureInfo.InvariantCulture));

        double psnr =
            globalMse > 0.0
                ? 10.0 *
                    Math.Log10(
                        1.0 /
                        globalMse)
                : 999.0;

        lines.Add(
            prefix +
            ".globalPsnrDb=" +
            psnr.ToString(
                "F6",
                CultureInfo.InvariantCulture));

        lines.Add(
            prefix +
            ".globalP95AbsLsbApprox=" +
            HistogramPercentile(
                metrics.GlobalHistogram,
                metrics.ComparedValueCount,
                0.95).ToString(
                    "F6",
                    CultureInfo.InvariantCulture));

        lines.Add(
            prefix +
            ".globalP99AbsLsbApprox=" +
            HistogramPercentile(
                metrics.GlobalHistogram,
                metrics.ComparedValueCount,
                0.99).ToString(
                    "F6",
                    CultureInfo.InvariantCulture));

        lines.Add(
            prefix +
            ".globalMaxAbsLsb=" +
            metrics.GlobalMaxAbsLsb.ToString(
                "F9",
                CultureInfo.InvariantCulture));

        lines.Add(
            prefix +
            ".exactFloatMatchRatio=" +
            Ratio(
                metrics.ExactFloatMatchCount,
                metrics.ComparedValueCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            prefix +
            ".within0_5LsbRatio=" +
            Ratio(
                metrics.WithinHalfLsbCount,
                metrics.ComparedValueCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            prefix +
            ".within1LsbRatio=" +
            Ratio(
                metrics.WithinOneLsbCount,
                metrics.ComparedValueCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            prefix +
            ".within2LsbRatio=" +
            Ratio(
                metrics.WithinTwoLsbCount,
                metrics.ComparedValueCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            prefix +
            ".within4LsbRatio=" +
            Ratio(
                metrics.WithinFourLsbCount,
                metrics.ComparedValueCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            prefix +
            ".within8LsbRatio=" +
            Ratio(
                metrics.WithinEightLsbCount,
                metrics.ComparedValueCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        for (
            int c = 0;
            c < 3;
            c++)
        {
            string channel =
                c == 0
                    ? "R"
                    : (
                        c == 1
                            ? "G"
                            : "B"
                    );

            long channelCount =
                metrics.PairCount *
                (long)PlaneLength;

            double signedMean =
                channelCount > 0
                    ? metrics.ChannelSignedSum[c] /
                        channelCount
                    : 0.0;

            double absMean =
                channelCount > 0
                    ? metrics.ChannelAbsSum[c] /
                        channelCount
                    : 0.0;

            double mse =
                channelCount > 0
                    ? metrics.ChannelSquaredSum[c] /
                        channelCount
                    : 0.0;

            lines.Add(
                prefix +
                ".channel" +
                channel +
                ".signedBiasLsb=" +
                (
                    signedMean *
                    255.0
                ).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

            lines.Add(
                prefix +
                ".channel" +
                channel +
                ".meanAbsLsb=" +
                (
                    absMean *
                    255.0
                ).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

            lines.Add(
                prefix +
                ".channel" +
                channel +
                ".rmseLsb=" +
                (
                    Math.Sqrt(
                        mse) *
                    255.0
                ).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

            lines.Add(
                prefix +
                ".channel" +
                channel +
                ".maxAbsLsb=" +
                metrics.ChannelMaxAbsLsb[c].ToString(
                    "F9",
                    CultureInfo.InvariantCulture));
        }

        AppendDoubleStats(
            lines,
            prefix +
                ".pairMeanAbsLsb",
            metrics.PairMeanAbsLsb);

        AppendDoubleStats(
            lines,
            prefix +
                ".pairP95AbsLsbApprox",
            metrics.PairP95AbsLsb);

        AppendDoubleStats(
            lines,
            prefix +
                ".pairP99AbsLsbApprox",
            metrics.PairP99AbsLsb);

        AppendDoubleStats(
            lines,
            prefix +
                ".pairMaxAbsLsb",
            metrics.PairMaxAbsLsb);
    }

    private void WriteReport(
        string status)
    {
        if (_reportWritten)
        {
            return;
        }

        _reportWritten = true;

        CaptureEndCounters();

        double measuredSeconds =
            _measuring
                ? Math.Max(
                    0.001,
                    Math.Min(
                        Time.realtimeSinceStartupAsDouble -
                            _measurementStart,
                        _durationSeconds))
                : 0.0;

        string directory =
            Path.Combine(
                Application.persistentDataPath,
                "KiwiFrameBottleneck");

        Directory.CreateDirectory(
            directory);

        string path =
            Path.Combine(
                directory,
                "KiwiPairCoherence_v44_55_9_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture) +
                ".txt");

        List<string> lines =
            new List<string>();

        lines.Add(
            "KiwiAvatarSystem v44.55.9 Exact Pair Coherence / Outlier Isolation Audit");
        lines.Add("contract=" + Contract);
        lines.Add("status=" + status);
        lines.Add("observerOnly=1");
        lines.Add("productionTrackerWrites=0");
        lines.Add("productionWorkerWrites=0");
        lines.Add("productionBackendChange=0");
        lines.Add("productionRoiWrites=0");
        lines.Add("productionCameraChange=0");
        lines.Add("referenceTensor=INDEPENDENT_TEXTURECONVERTER_FROM_MATCHED_PRODUCTION_CROP_TEXTURE");
        lines.Add("productionLaneInputReadback=0");
        lines.Add("candidate0=CURRENT_FLOAT");
        lines.Add("candidate1=PRESENTATION_PLUS_FINAL_UNORM8");
        lines.Add("freezeOrder=MATCH_THEN_COPYTEXTURE_BEFORE_CPU_CROP");
        lines.Add("pairRecordsEnabled=1");
        lines.Add("unorm8ReferenceRule=CLAMP_X255_PLUS_0_5_DROP_FRACTION_DIV255");
        lines.Add("productionPresentationFormat=DXGI_FORMAT_R8G8B8A8_UNORM");
        lines.Add("productionCropTexture=ARGB32_LINEAR_192X192");
        lines.Add("nativeProductionPathChanged=0");
        lines.Add("nativeDiagnosticApiAdded=1");
        lines.Add("interopDiagnosticApiAdded=1");
        lines.Add("nativeCropCallsPerMatchedPair=2");
        lines.Add("identityBridge=NATIVE_SEQUENCE_TO_RUNNER_CALIBRATED_HOST_TICKS");
        lines.Add("nativeSnapshotTimestampDomain=RAW_NATIVE_QPC");
        lines.Add("productionLaneTimestampDomain=RUNNER_CALIBRATED_MANAGED_STOPWATCH");
        lines.Add("samplingMatrixSource=PRODUCTION_LANE_CROP_MATERIAL_XFORM");
        lines.Add("tensorLayout=NCHW");
        lines.Add("coordOrigin=TopLeft");
        lines.Add("inputShape=1x3x192x192");
        lines.Add(
            "durationSeconds=" +
            measuredSeconds.ToString(
                "F6",
                CultureInfo.InvariantCulture));
        lines.Add(
            "requestedPairHz=" +
            _sampleHz.ToString(
                "F3",
                CultureInfo.InvariantCulture));
        lines.Add("warmupPairCount=" + WarmupPairCount);
        lines.Add("warmupIncludedInNumericalStats=0");
        lines.Add("pairAttemptCount=" + _pairAttemptCount);
        lines.Add("pairCompletedCount=" + _pairCompletedCount);
        lines.Add("pairErrorCount=" + _pairErrorCount);
        lines.Add("legacyRetrospectiveExactSlotMissCount=" + _exactSlotMissCount);
        lines.Add("snapshotArmCount=" + _snapshotArmCount);
        lines.Add("snapshotReadyCount=" + _snapshotReadyCount);
        lines.Add("snapshotMatchTimeoutCount=" + _snapshotMatchTimeoutCount);
        lines.Add("snapshotArmFailureCount=" + _snapshotArmFailureCount);
        lines.Add("snapshotCropFailureCount=" + _snapshotCropFailureCount);
        lines.Add("snapshotPresentedButNotScheduledCount=" + _snapshotPresentedButNotScheduledCount);
        lines.Add("snapshotSequenceObservedCount=" + _snapshotSequenceObservedCount);
        lines.Add("snapshotSequenceSkippedCount=" + _snapshotSequenceSkippedCount);
        lines.Add("snapshotManagedTimestampMapFailureCount=" + _snapshotManagedTimestampMapFailureCount);
        lines.Add("laneRaceDiscardCount=" + _laneRaceDiscardCount);
        lines.Add("laneChangedAfterFreezeCount=" + _laneChangedAfterFreezeCount);
        lines.Add("snapshotTensorMismatchCount=" + _snapshotTensorMismatchCount);
        lines.Add("quantizedOver0_5LsbCount=" + _quantizedOverHalfLsbCount);
        lines.Add("quantizedOver1LsbCount=" + _quantizedOverOneLsbCount);
        lines.Add("quantizedOver2LsbCount=" + _quantizedOverTwoLsbCount);
        lines.Add("gpuInputReadbackErrorCount=" + _gpuInputReadbackErrorCount);
        lines.Add("inputShapeMismatchCount=" + _inputShapeMismatchCount);
        lines.Add("nonFinitePairCount=" + _nonFinitePairCount);
        lines.Add("latestNativeSequence=" + _pendingNativeSequence);
        lines.Add("latestSourceHostTicks=" + _pendingSourceHostTicks);
        lines.Add("");

        AppendDoubleStats(
            lines,
            "nativeCurrentCropCpuMs",
            _nativeCurrentCropCpuMs);

        AppendDoubleStats(
            lines,
            "nativeFinalUnorm8CropCpuMs",
            _nativeFinalUnorm8CropCpuMs);

        AppendDoubleStats(
            lines,
            "nativePresentationFinalUnorm8CropCpuMs",
            _nativePresentationFinalUnorm8CropCpuMs);

        AppendDoubleStats(
            lines,
            "nativeThreeCandidateWallMs",
            _nativeThreeCandidateWallMs);

        AppendDoubleStats(
            lines,
            "observerSnapshotReadbackMs",
            _observerSnapshotReadbackMs);

        AppendDoubleStats(
            lines,
            "observerSnapshotReadbackFrames",
            _observerSnapshotReadbackFrames);

        AppendDoubleStats(
            lines,
            "observerTensorReadbackMs",
            _observerTensorReadbackMs);

        AppendDoubleStats(
            lines,
            "observerTensorReadbackFrames",
            _observerTensorReadbackFrames);



        AppendDoubleStats(
            lines,
            "sourceAgeAtCaptureMs",
            _sourceAgeAtCaptureMs);

        AppendDoubleStats(
            lines,
            "sourceAgeAtCompareMs",
            _sourceAgeAtCompareMs);

        AppendDoubleStats(
            lines,
            "snapshotManagedMinusNativeTicks",
            _snapshotManagedMinusNativeTicks);

        lines.Add("");
        lines.Add("[UNORM8_QUANTIZATION_PARITY]");

        AppendMetricReport(
            lines,
            "CURRENT_FLOAT_TO_OBSERVER",
            _currentToObserverMetrics);

        lines.Add("");

        AppendMetricReport(
            lines,
            "FINAL_UNORM8_TO_OBSERVER",
            _finalUnorm8ToObserverMetrics);

        lines.Add("");

        AppendMetricReport(
            lines,
            "PRESENTATION_PLUS_FINAL_UNORM8_TO_OBSERVER",
            _presentationFinalUnorm8ToObserverMetrics);

        lines.Add("");

        AppendMetricReport(
            lines,
            "SNAPSHOT_TO_OBSERVER_DIRECT",
            _snapshotToObserverDirectMetrics);

        lines.Add("");

        AppendMetricReport(
            lines,
            "SNAPSHOT_TO_OBSERVER_FLIP_Y",
            _snapshotToObserverFlipYMetrics);

        lines.Add("");

        double currentMean =
            MeanAbsLsb(
                _currentToObserverMetrics);

        double finalMean =
            MeanAbsLsb(
                _finalUnorm8ToObserverMetrics);

        double presentationFinalMean =
            MeanAbsLsb(
                _presentationFinalUnorm8ToObserverMetrics);

        double bestMean =
            Math.Min(
                currentMean,
                Math.Min(
                    finalMean,
                    presentationFinalMean));

        string bestCandidate =
            presentationFinalMean <= finalMean &&
            presentationFinalMean <= currentMean
                ? "PRESENTATION_PLUS_FINAL_UNORM8"
                : (
                    finalMean <= currentMean
                        ? "FINAL_UNORM8"
                        : "CURRENT_FLOAT"
                );

        lines.Add(
            "currentMeanAbsLsb=" +
            currentMean.ToString(
                "F9",
                CultureInfo.InvariantCulture));

        lines.Add(
            "finalUnorm8MeanAbsLsb=" +
            finalMean.ToString(
                "F9",
                CultureInfo.InvariantCulture));

        lines.Add(
            "presentationFinalUnorm8MeanAbsLsb=" +
            presentationFinalMean.ToString(
                "F9",
                CultureInfo.InvariantCulture));

        lines.Add(
            "bestMeanAbsLsb=" +
            bestMean.ToString(
                "F9",
                CultureInfo.InvariantCulture));

        lines.Add(
            "bestCandidate=" +
            bestCandidate);

        lines.Add(
            "presentationFinalToCurrentMeanRatio=" +
            SafeRatio(
                presentationFinalMean,
                currentMean).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add("");

        lines.Add("[PRODUCTION_OBSERVATION]");

        AppendFloatStats(
            lines,
            "sampledProductionTrackerLatencyMs",
            _productionTrackerLatency);

        AppendIntStats(
            lines,
            "sampledProductionActiveLanes",
            _productionActiveLanes);

        int scheduledDelta =
            Math.Max(
                0,
                _endScheduled -
                _startScheduled);

        int readbackDelta =
            Math.Max(
                0,
                _endReadbackCompleted -
                _startReadbackCompleted);

        int completedDelta =
            Math.Max(
                0,
                _endCompleted -
                _startCompleted);

        lines.Add("scheduledDelta=" + scheduledDelta);
        lines.Add("readbackCompletedDelta=" + readbackDelta);
        lines.Add("completedDelta=" + completedDelta);
        lines.Add(
            "droppedFreshDelta=" +
            Math.Max(
                0,
                _endDropped -
                _startDropped));
        lines.Add(
            "discardedStaleDelta=" +
            Math.Max(
                0,
                _endStale -
                _startStale));

        if (measuredSeconds > 0.0)
        {
            lines.Add(
                "scheduledHz=" +
                (
                    scheduledDelta /
                    measuredSeconds
                ).ToString(
                    "F6",
                    CultureInfo.InvariantCulture));

            lines.Add(
                "readbackCompletedHz=" +
                (
                    readbackDelta /
                    measuredSeconds
                ).ToString(
                    "F6",
                    CultureInfo.InvariantCulture));

            lines.Add(
                "completedHz=" +
                (
                    completedDelta /
                    measuredSeconds
                ).ToString(
                    "F6",
                    CultureInfo.InvariantCulture));
        }

        lines.Add("");
        lines.Add("[PAIR_RECORDS]");
        lines.Add(
            "format=index|sequence|nativeHostTicks|managedHostTicks|laneStartedTicks|" +
            "sourceAgeFreezeMs|sourceAgeAfterCpuMs|freezeSubmitCpuMs|snapshotReadbackMs|" +
            "observerTensorReadbackMs|laneStillMatchedAfterCpu|currentMaeLsb|quantizedMaeLsb|" +
            "quantizedToCurrentRatio|snapshotFlipYMaeLsb|quantizedP95Lsb|quantizedP99Lsb|" +
            "quantizedMaxLsb");

        for (
            int i = 0;
            i < _pairRecords.Count;
            i++)
        {
            PairRecord record =
                _pairRecords[i];

            lines.Add(
                record.Index + "|" +
                record.Sequence + "|" +
                record.NativeHostTicks + "|" +
                record.ManagedHostTicks + "|" +
                record.LaneStartedTicks + "|" +
                record.SourceAgeAtFreezeSubmitMs.ToString(
                    "F6",
                    CultureInfo.InvariantCulture) + "|" +
                record.SourceAgeAfterCpuMs.ToString(
                    "F6",
                    CultureInfo.InvariantCulture) + "|" +
                record.FreezeSubmitCpuMs.ToString(
                    "F6",
                    CultureInfo.InvariantCulture) + "|" +
                record.SnapshotReadbackMs.ToString(
                    "F6",
                    CultureInfo.InvariantCulture) + "|" +
                record.ObserverTensorReadbackMs.ToString(
                    "F6",
                    CultureInfo.InvariantCulture) + "|" +
                (record.LaneStillMatchedAfterCpu ? 1 : 0) + "|" +
                record.CurrentMeanAbsLsb.ToString(
                    "F9",
                    CultureInfo.InvariantCulture) + "|" +
                record.QuantizedMeanAbsLsb.ToString(
                    "F9",
                    CultureInfo.InvariantCulture) + "|" +
                record.QuantizedToCurrentRatio.ToString(
                    "F9",
                    CultureInfo.InvariantCulture) + "|" +
                record.SnapshotFlipYMeanAbsLsb.ToString(
                    "F9",
                    CultureInfo.InvariantCulture) + "|" +
                record.QuantizedPairP95Lsb.ToString(
                    "F6",
                    CultureInfo.InvariantCulture) + "|" +
                record.QuantizedPairP99Lsb.ToString(
                    "F6",
                    CultureInfo.InvariantCulture) + "|" +
                record.QuantizedPairMaxLsb.ToString(
                    "F6",
                    CultureInfo.InvariantCulture));
        }

        lines.Add("");

        lines.Add("[DECISION_GUIDE]");
        lines.Add(
            "COHERENCE_ARTIFACT_CONFIRMED if early-freeze PRESENTATION_PLUS_FINAL_UNORM8 " +
            "has global mean <=0.15 LSB, pair p95 <=0.50 LSB, quantizedOver0_5LsbRatio <=0.05, " +
            "and SNAPSHOT_TO_OBSERVER_FLIP_Y remains exact. Then v44.55.8 outliers were caused " +
            "by observer freeze timing rather than preprocessing math.");
        lines.Add(
            "RESIDUAL_SAMPLING_CONFIRMED if SNAPSHOT_TO_OBSERVER_FLIP_Y stays exact but " +
            "early-freeze quantized pair p95 remains >0.50 LSB or >5% pairs exceed 0.5 LSB. " +
            "Then investigate exact GPU bilinear/sample precision; do not change color/chroma.");
        lines.Add(
            "IDENTITY_OR_FREEZE_FAILURE if snapshotTensorMismatchCount >0 or pair identity " +
            "records show lane/source mismatch. Then fix the audit before judging preprocessing.");
        lines.Add(
            "This audit does not switch Production to CPU and does not change Production " +
            "camera/tracker/worker/ROI/threshold/render behavior.");


        File.WriteAllLines(
            path,
            lines);

        Debug.Log(
            "[Kiwi v44.55.9 Coherence] " +
            status +
            " report=" +
            path);
    }

    private void OnDestroy()
    {
        if (_observerGraphicsCommandBuffer != null)
        {
            _observerGraphicsCommandBuffer.Release();
            _observerGraphicsCommandBuffer = null;
        }

        _observerTensor?.Dispose();
        _observerTensor = null;

        if (_observerSnapshotTexture != null)
        {
            if (_observerSnapshotTexture.IsCreated())
            {
                _observerSnapshotTexture.Release();
            }

            Destroy(
                _observerSnapshotTexture);

            _observerSnapshotTexture = null;
        }
    }

    private void OnApplicationQuit()
    {
        if (
            _measuring &&
            !_reportWritten)
        {
            WriteReport("QUIT");
        }
    }

    private static bool ReadBoolEnvironment(
        string name,
        bool fallback)
    {
        string value =
            Environment.GetEnvironmentVariable(
                name);

        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        value =
            value.Trim();

        return
            value == "1" ||
            value.Equals(
                "true",
                StringComparison.OrdinalIgnoreCase) ||
            value.Equals(
                "yes",
                StringComparison.OrdinalIgnoreCase) ||
            value.Equals(
                "on",
                StringComparison.OrdinalIgnoreCase);
    }

    private static float ReadFloatEnvironment(
        string name,
        float fallback)
    {
        string value =
            Environment.GetEnvironmentVariable(
                name);

        if (
            float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static int ReadIntEnvironment(
        string name,
        int fallback)
    {
        string value =
            Environment.GetEnvironmentVariable(
                name);

        if (
            int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed))
        {
            return parsed;
        }

        return fallback;
    }
}
