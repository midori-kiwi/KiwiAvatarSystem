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
/// KiwiAvatarSystem v44.55.14
/// Single-Run Production-Input CPU Semantic + Steady Gate.
///
/// Goal: minimize the NUMBER of runtime measurements, not their duration.
/// Static validation remains mandatory. The runtime phase is one long
/// integrated measurement: 180 seconds at 0.5 Hz (~90 semantic pairs) while
/// the existing Production GPU path remains the sole authority.
///
/// The observer uses v44.55.10 identity correction, then compares an
/// observer-owned CPU Worker against an observer-owned GPUCompute Worker fed
/// from the same frozen Production crop. It evaluates task-level semantics:
/// presence decision, face centroid, bounding-box center/size, landmark
/// displacement and depth. The launcher simultaneously enables the frozen
/// v44.47 steady-state profiler in the SAME Player process.
///
/// No CPU result is published to tracking, ROI, FaceTexture or authority.
/// </summary>
internal sealed class KiwiExactHostTicksCpuPreprocessSemanticV44_55_15
    : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_55_15_EXACT_HOSTTICKS_CPU_PREPROCESS_SEMANTIC_GATE";

    private const string EnableVariable =
        "KIWI_V44_55_15_EXACT_CPU_INPUT_AUDIT";

    private const string DurationVariable =
        "KIWI_V44_55_15_EXACT_CPU_INPUT_SECONDS";

    private const string SampleHzVariable =
        "KIWI_V44_55_15_EXACT_CPU_INPUT_HZ";

    private const string StableSecondsVariable =
        "KIWI_V44_55_15_STABLE_SECONDS";

    private const string ExpectedTrianglesVariable =
        "KIWI_V44_55_15_EXPECTED_TRIANGLES";

    private const int InputSize = 192;
    private const int PlaneLength =
        InputSize *
        InputSize;
    private const int InputFloatCount =
        PlaneLength *
        3;

    private const int BaseLandmarkCount = 468;
    private const int PackedOutputLength =
        BaseLandmarkCount * 3 + 1;

    private const string LandmarkOutputName =
        "conv2d_20";

    private const string PresenceOutputName =
        "conv2d_30";

    private const float DefaultDurationSeconds = 60f;
    private const float DefaultSampleHz = 1.0f;
    private const float DefaultStableSeconds = 8f;
    private const int DefaultExpectedTriangles = 254296;
    private const int WarmupPairCount = 2;
    private const int IdentityCandidateCapacity = 5;
    private const double IdentityExpansionThresholdLsb = 0.5;

    // Task-level gate. These thresholds are intentionally looser than bitwise
    // backend parity and are applied only to observer data.
    private const double SemanticLandmarkP95PxGate = 1.0;
    private const double SemanticLandmarkWithin2PxRatioGate = 0.995;
    private const double SemanticCentroidP95PxGate = 0.25;
    private const double SemanticBboxCenterP95PxGate = 0.25;
    private const double SemanticBboxSizeP95PxGate = 0.50;

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

    private readonly float[] _identityCandidateNchw =
        new float[InputFloatCount];

    private readonly float[] _identityCorrectedNchw =
        new float[InputFloatCount];

    private readonly float[] _cpuOutput =
        new float[PackedOutputLength];

    private readonly float[] _gpuOutput =
        new float[PackedOutputLength];

    private Worker _cpuWorker;
    private Worker _gpuWorker;
    private Tensor<float> _cpuInput;
    private Tensor<float> _gpuInput;
    private CommandBuffer _outputGpuCommandBuffer;
    private bool _workersReady;

    private bool _outputComparisonScheduled;
    private bool _cpuOutputDone;
    private bool _gpuOutputDone;
    private long _outputScheduleStartTicks;
    private long _cpuScheduleDoneTicks;
    private long _gpuScheduleDoneTicks;
    private int _outputScheduleStartFrame;
    private int _cpuOutputDoneFrame;
    private int _gpuOutputDoneFrame;

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
        internal int IdentityValidCandidateCount;
        internal bool IdentityBurstComplete;
        internal bool IdentityExpanded;
        internal ulong IdentityBestSequence;
        internal long IdentityBestHostTicks;
        internal int IdentityBestRole;
        internal double IdentityBestMeanAbsLsb;
        internal double IdentityBestToTargetRatio;
        internal bool IdentityBestIsTarget;
        internal string IdentityCandidateSummary;
        internal int IdentityBestCandidateIndex = -1;
        internal double IdentityCorrectedInputMeanAbsLsb;
        internal bool OutputComparisonScheduled;
        internal double Landmark2dMeanPx;
        internal double Landmark2dP95Px;
        internal double Landmark2dMaxPx;
        internal double PresenceSigmoidAbsDiff;
        internal double SemanticCentroidDiffPx;
        internal double SemanticBboxCenterDiffPx;
        internal double SemanticBboxWidthDiffPx;
        internal double SemanticBboxHeightDiffPx;
        internal double SemanticMeanDepthAbsDiff;
        internal bool SemanticPresenceDecisionMatch;
    }

    private readonly List<PairRecord> _pairRecords =
        new List<PairRecord>(128);

    private PairRecord _pendingPairRecord;

    private int _laneChangedAfterFreezeCount;
    private int _snapshotTensorMismatchCount;
    private int _quantizedOverHalfLsbCount;
    private int _quantizedOverOneLsbCount;
    private int _quantizedOverTwoLsbCount;

    private int _identityCandidateApiFailureCount;
    private int _identityExpandedPairCount;
    private int _identityNeighborWinsCount;
    private int _identityTargetWinsOutlierCount;
    private int _identityNeighborUnderHalfLsbCount;

    private int _identityCorrectionCopyFailureCount;
    private int _identityCorrectedOverHalfLsbCount;
    private int _outputPairCompletedCount;
    private int _outputPairErrorCount;
    private int _cpuNonFiniteOutputPairCount;
    private int _gpuNonFiniteOutputPairCount;
    private int _outputNonFinitePairCount;
    private int _presenceDecisionMismatchCount;
    private int _presenceFarThresholdMismatchCount;
    private int _presenceNearThresholdPairCount;

    private int _semanticPresenceMismatchCount;
    private int _semanticPairPassCount;
    private int _semanticPairFailCount;

    private readonly List<double> _semanticCentroidDiffPx =
        new List<double>(64);

    private readonly List<double> _semanticBboxCenterDiffPx =
        new List<double>(64);

    private readonly List<double> _semanticBboxWidthDiffPx =
        new List<double>(64);

    private readonly List<double> _semanticBboxHeightDiffPx =
        new List<double>(64);

    private readonly List<double> _semanticMeanDepthAbsDiff =
        new List<double>(64);

    private long _landmarkCoordinateCount;
    private long _exactCoordinateMatchCount;
    private long _landmark2dCount;
    private long _within025PxCount;
    private long _within050PxCount;
    private long _within100PxCount;
    private long _within200PxCount;

    private readonly List<double> _identityCorrectedInputMeanAbsLsb =
        new List<double>(128);

    private readonly List<double> _cpuInputUploadMs =
        new List<double>(128);

    private readonly List<double> _cpuScheduleCpuMs =
        new List<double>(128);

    private readonly List<double> _gpuScheduleSubmitCpuMs =
        new List<double>(128);

    private readonly List<double> _cpuServiceMs =
        new List<double>(128);

    private readonly List<double> _gpuServiceMs =
        new List<double>(128);

    private readonly List<double> _outputPairServiceMs =
        new List<double>(128);

    private readonly List<int> _cpuCompletionFrames =
        new List<int>(128);

    private readonly List<int> _gpuCompletionFrames =
        new List<int>(128);

    private readonly List<int> _outputPairCompletionFrames =
        new List<int>(128);

    private readonly List<double> _landmarkXAbsRaw =
        new List<double>(32768);

    private readonly List<double> _landmarkYAbsRaw =
        new List<double>(32768);

    private readonly List<double> _landmarkZAbsRaw =
        new List<double>(32768);

    private readonly List<double> _landmark2dPixelDiff =
        new List<double>(32768);

    private readonly List<double> _landmark2dMeanPerPair =
        new List<double>(128);

    private readonly List<double> _landmark2dP95PerPair =
        new List<double>(128);

    private readonly List<double> _landmark2dMaxPerPair =
        new List<double>(128);

    private readonly List<double> _presenceRawAbsDiff =
        new List<double>(128);

    private readonly List<double> _presenceSigmoidAbsDiff =
        new List<double>(128);

    private readonly List<double> _cpuPresence =
        new List<double>(128);

    private readonly List<double> _gpuPresence =
        new List<double>(128);

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
                "[Kiwi] v44.55.15 Exact-HostTicks CPU Preprocess Semantic Gate");

        DontDestroyOnLoad(go);
        go.hideFlags =
            HideFlags.DontSave;

        go.AddComponent<
            KiwiExactHostTicksCpuPreprocessSemanticV44_55_15>();
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

        WriteArmedProof();

        _observerSnapshotTexture =
            new RenderTexture(
                InputSize,
                InputSize,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                name =
                    "Kiwi v44.55.15 Exact CPU Input Snapshot",
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
                    "Kiwi v44.55.15 Exact CPU Input Oracle"
            };

        Debug.Log(
            "[Kiwi v44.55.15 ExactCpuInput] WAIT_GATE " +
            "contract=" + Contract +
            " observerOnly=1" +
            " productionTrackerWrites=0" +
            " productionWorkerWrites=0" +
            " productionBackendChange=0" +
            " productionRoiWrites=0" +
            " productionCameraChange=0" +
            " productionTensorReadback=DISABLED" +
            " observerTensor=INPUT_ORACLE_ONLY" +
            " frozenProductionCrop=OBSERVER_OWNED_RENDER_TEXTURE" +
            " cpuShadowBackend=CPU" +
            " rightShadowBackend=CPU" +
            " identityCorrection=DISABLED_SAME_EXACT_SNAPSHOT" +
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

        if (_pairPending)
        {
            TryFinishPendingPair(
                _pendingPairToken);
        }

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

    private void InstallShadowWorkers()
    {
        if (_workersReady)
        {
            return;
        }

        ModelAsset asset =
            Resources.Load<ModelAsset>(
                "KiwiFaceLandmarkInference");

        if (asset == null)
        {
            throw new InvalidOperationException(
                "Resources/KiwiFaceLandmarkInference ModelAsset not found.");
        }

        Model cpuPacked =
            BuildSingleReadbackModel(
                ModelLoader.Load(asset));

        Model gpuPacked =
            BuildSingleReadbackModel(
                ModelLoader.Load(asset));

        _cpuWorker =
            new Worker(
                cpuPacked,
                BackendType.CPU);

        // v44.55.15: second independent CPU Worker. Legacy field name is
        // retained only to minimize changes to the proven semantic comparator.
        _gpuWorker =
            new Worker(
                gpuPacked,
                BackendType.CPU);

        _cpuInput =
            new Tensor<float>(
                new TensorShape(
                    1,
                    3,
                    InputSize,
                    InputSize));

        _gpuInput =
            new Tensor<float>(
                new TensorShape(
                    1,
                    3,
                    InputSize,
                    InputSize));

        _outputGpuCommandBuffer =
            new CommandBuffer
            {
                name =
                    "Kiwi v44.55.14 Semantic GPU Shadow Worker"
            };

        _workersReady = true;

        Debug.Log(
            "[Kiwi v44.55.15 ExactCpuInput] WORKERS_READY " +
            "left=CPU_CURRENT_FLOAT" +
            " right=CPU_PRESENTATION_PLUS_FINAL_UNORM8" +
            " sameModel=KiwiFaceLandmarkInference" +
            " packedOutputLength=1405" +
            " sourceIdentity=SAME_DIAGNOSTIC_SNAPSHOT_SEQUENCE_HOSTTICKS" +
            " productionWorkerTouched=0");
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
                    "[Kiwi v44.55.15 ExactCpuInput] GATE_MATCH " +
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

        try
        {
            InstallShadowWorkers();
        }
        catch (Exception exception)
        {
            _pairErrorCount++;

            Debug.LogError(
                "[Kiwi v44.55.15 ExactCpuInput] WORKER_INIT_FAIL " +
                exception.GetType().Name +
                " " +
                exception.Message);

            WriteReport("INIT_FAIL");
            return;
        }

        Debug.Log(
            "[Kiwi v44.55.15 ExactCpuInput] READY_FOR_WARMUP " +
            "productionCropTextureFreeze=1" +
            " identityCorrection=0" +
            " cpuShadowLeft=1" +
            " cpuShadowRight=1" +
            " blockingGpuWait=0");

        _stableSince =
            double.PositiveInfinity;
    }

    private bool TryCaptureNewestProductionLane(
        bool isWarmup)
    {
        if (
            !_reflectionReady ||
            !_workersReady ||
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

        _outputComparisonScheduled =
            false;
        _cpuOutputDone =
            false;
        _gpuOutputDone =
            false;
        _cpuOutputDoneFrame =
            -1;
        _gpuOutputDoneFrame =
            -1;

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
            !_pairPending)
        {
            return;
        }

        if (!_outputComparisonScheduled)
        {
            if (
                !_pendingSnapshotReadbackDone ||
                !_pendingObserverTensorReadbackDone ||
                !_pendingProductionTensorReadbackDone ||
                !_pendingCpuCandidatesDone)
            {
                return;
            }

            // v44.55.15:
            // Do NOT use v44.55.10 identity-neighbor correction here.
            // Both candidates below are produced from the same exact diagnostic
            // snapshot sequence/hostTicks and the same Production crop matrix.
            // This isolates preprocessing semantics from the mutable GPU
            // Presentation texture race.
            if (!ValidateFiniteInputs())
            {
                _nonFinitePairCount++;
                _pairErrorCount++;
                AbortCurrentPair(
                    _pendingIsWarmup,
                    "INPUT_NONFINITE");
                return;
            }

            try
            {
                ScheduleOutputComparison(
                    _pendingIsWarmup,
                    pairToken);
            }
            catch (Exception exception)
            {
                _outputPairErrorCount++;

                AbortCurrentPair(
                    _pendingIsWarmup,
                    "OUTPUT_SCHEDULE_FAIL " +
                    exception.GetType().Name +
                    " " +
                    exception.Message);
            }

            return;
        }

        if (
            !_cpuOutputDone ||
            !_gpuOutputDone)
        {
            return;
        }

        FinalizeOutputPair();
    }




    private void HandlePairReadbackError(
        bool isWarmup,
        string message)
    {
        _gpuInputReadbackErrorCount++;
        _pairErrorCount++;
        _pairPending = false;
        _outputComparisonScheduled = false;
        _cpuOutputDone = false;
        _gpuOutputDone = false;
        _pendingPairRecord = null;

        ResetSnapshotRequest();

        Debug.LogWarning(
            "[Kiwi v44.55.15 ExactCpuInput] READBACK_ERROR " +
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



    private void FinalizeOutputPair()
    {
        bool isWarmup =
            _pendingIsWarmup;

        long done =
            Stopwatch.GetTimestamp();

        if (!isWarmup)
        {
            CompareOutputs(
                _pendingPairRecord);

            if (_pendingPairRecord != null)
            {
                _pairRecords.Add(
                    _pendingPairRecord);
            }

            _outputPairServiceMs.Add(
                TicksToMilliseconds(
                    done -
                    _outputScheduleStartTicks));

            _outputPairCompletionFrames.Add(
                Mathf.Max(
                    0,
                    Mathf.Max(
                        _cpuOutputDoneFrame,
                        _gpuOutputDoneFrame) -
                    _outputScheduleStartFrame));

            _outputPairCompletedCount++;
            _pairCompletedCount++;
        }
        else
        {
            _warmupPairsRemaining--;
        }

        _pairPending = false;
        _outputComparisonScheduled = false;
        _cpuOutputDone = false;
        _gpuOutputDone = false;
        _pendingPairRecord = null;

        ResetSnapshotRequest();

        if (
            isWarmup &&
            _warmupPairsRemaining <=
                0)
        {
            BeginMeasurement();
        }
    }

    private void AbortCurrentPair(
        bool isWarmup,
        string reason)
    {
        Debug.LogWarning(
            "[Kiwi v44.55.15 ExactCpuInput] PAIR_ABORT " +
            reason);

        _pairPending = false;
        _outputComparisonScheduled = false;
        _cpuOutputDone = false;
        _gpuOutputDone = false;
        _pendingPairRecord = null;

        ResetSnapshotRequest();

        if (isWarmup)
        {
            WriteReport("WARMUP_FAIL");
        }
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


    private double ComputeMeanAbsLsb(
        float[] candidate,
        float[] reference)
    {
        if (
            candidate == null ||
            reference == null ||
            candidate.Length <
                InputFloatCount ||
            reference.Length <
                InputFloatCount)
        {
            return double.PositiveInfinity;
        }

        double sum = 0.0;

        for (
            int i = 0;
            i < InputFloatCount;
            i++)
        {
            float a = candidate[i];
            float b = reference[i];

            if (
                !IsFinite(a) ||
                !IsFinite(b))
            {
                return double.PositiveInfinity;
            }

            sum +=
                Math.Abs(
                    (double)a -
                    b) *
                255.0;
        }

        return
            sum /
            InputFloatCount;
    }

    private void ExpandIdentityNeighborhoodIfNeeded(
        PairRecord record)
    {
        if (
            record == null ||
            record.QuantizedMeanAbsLsb <=
                IdentityExpansionThresholdLsb)
        {
            return;
        }

        record.IdentityExpanded = true;
        _identityExpandedPairCount++;

        double bestMae =
            record.QuantizedMeanAbsLsb;

        ulong bestSequence =
            record.Sequence;

        long bestHostTicks =
            record.NativeHostTicks;

        int bestRole = 0;
        int bestCandidateIndex = -1;

        List<string> summaries =
            new List<string>(
                IdentityCandidateCapacity);

        bool targetSeen = false;

        for (
            int candidateIndex = 0;
            candidateIndex <
                IdentityCandidateCapacity;
            candidateIndex++)
        {
            if (!KiwiNativeCameraInterop
                    .TryGetDiagnosticCpuIdentityCandidate(
                        candidateIndex,
                        out ulong candidateSequence,
                        out long candidateHostTicks,
                        out int candidateRole))
            {
                continue;
            }

            bool copied =
                KiwiNativeCameraInterop
                    .TryCopyDiagnosticCpuIdentityCandidateCropNchwFloatQuantized(
                        candidateIndex,
                        _samplingMatrixCurrent,
                        InputSize,
                        2,
                        _identityCandidateNchw,
                        out ulong copiedSequence,
                        out long copiedHostTicks,
                        out int copiedRole,
                        out ulong nativeCpuMicroseconds);

            if (
                !copied ||
                copiedSequence !=
                    candidateSequence ||
                copiedHostTicks !=
                    candidateHostTicks ||
                copiedRole !=
                    candidateRole)
            {
                _identityCandidateApiFailureCount++;
                continue;
            }

            double mae =
                ComputeMeanAbsLsb(
                    _identityCandidateNchw,
                    _observerTensorNchw);

            summaries.Add(
                candidateRole.ToString(
                    CultureInfo.InvariantCulture) +
                ":" +
                candidateSequence.ToString(
                    CultureInfo.InvariantCulture) +
                ":" +
                mae.ToString(
                    "F9",
                    CultureInfo.InvariantCulture) +
                ":" +
                nativeCpuMicroseconds.ToString(
                    CultureInfo.InvariantCulture));

            if (
                candidateSequence ==
                    record.Sequence)
            {
                targetSeen = true;
            }

            if (mae < bestMae)
            {
                bestMae = mae;
                bestSequence = candidateSequence;
                bestHostTicks = candidateHostTicks;
                bestRole = candidateRole;
                bestCandidateIndex = candidateIndex;
            }
        }

        if (!targetSeen)
        {
            _identityCandidateApiFailureCount++;
        }

        record.IdentityBestSequence =
            bestSequence;

        record.IdentityBestHostTicks =
            bestHostTicks;

        record.IdentityBestRole =
            bestRole;

        record.IdentityBestCandidateIndex =
            bestCandidateIndex;

        record.IdentityBestMeanAbsLsb =
            bestMae;

        record.IdentityBestToTargetRatio =
            record.QuantizedMeanAbsLsb >
                0.0
                ? bestMae /
                    record.QuantizedMeanAbsLsb
                : 0.0;

        record.IdentityBestIsTarget =
            bestSequence ==
                record.Sequence;

        record.IdentityCandidateSummary =
            string.Join(
                ",",
                summaries);

        if (record.IdentityBestIsTarget)
        {
            _identityTargetWinsOutlierCount++;
        }
        else
        {
            _identityNeighborWinsCount++;

            if (
                bestMae <=
                    IdentityExpansionThresholdLsb)
            {
                _identityNeighborUnderHalfLsbCount++;
            }
        }
    }


    private bool PrepareIdentityCorrectedInput(
        PairRecord record)
    {
        if (record == null)
        {
            return false;
        }

        if (
            record.IdentityExpanded &&
            !record.IdentityBestIsTarget)
        {
            if (
                record.IdentityBestCandidateIndex <
                    0)
            {
                _identityCorrectionCopyFailureCount++;
                return false;
            }

            bool copied =
                KiwiNativeCameraInterop
                    .TryCopyDiagnosticCpuIdentityCandidateCropNchwFloatQuantized(
                        record.IdentityBestCandidateIndex,
                        _samplingMatrixCurrent,
                        InputSize,
                        2,
                        _identityCorrectedNchw,
                        out ulong sequence,
                        out long hostTicks,
                        out int role,
                        out ulong nativeCpuMicroseconds);

            if (
                !copied ||
                sequence !=
                    record.IdentityBestSequence ||
                hostTicks !=
                    record.IdentityBestHostTicks ||
                role !=
                    record.IdentityBestRole)
            {
                _identityCorrectionCopyFailureCount++;
                return false;
            }
        }
        else
        {
            Array.Copy(
                _nativePresentationFinalUnorm8Nchw,
                _identityCorrectedNchw,
                InputFloatCount);
        }

        double mae =
            ComputeMeanAbsLsb(
                _identityCorrectedNchw,
                _observerTensorNchw);

        if (
            double.IsNaN(mae) ||
            double.IsInfinity(mae))
        {
            return false;
        }

        record.IdentityCorrectedInputMeanAbsLsb =
            mae;

        _identityCorrectedInputMeanAbsLsb.Add(
            mae);

        if (mae > 0.5)
        {
            _identityCorrectedOverHalfLsbCount++;
        }

        return true;
    }

    private void ScheduleOutputComparison(
        bool isWarmup,
        int pairToken)
    {
        if (
            pairToken !=
                _pendingPairToken ||
            !_pairPending ||
            _outputComparisonScheduled)
        {
            return;
        }

        long scheduleStart =
            Stopwatch.GetTimestamp();

        // LEFT: plain CURRENT_FLOAT preprocessing from the exact snapshot.
        long cpuUploadBegin =
            scheduleStart;

        _cpuInput.Upload(
            _nativeCurrentNchw);

        long afterCpuUpload =
            Stopwatch.GetTimestamp();

        long cpuScheduleBegin =
            afterCpuUpload;

        _cpuWorker.Schedule(
            _cpuInput);

        Tensor<float> cpuTensor =
            _cpuWorker.PeekOutput(0)
            as Tensor<float>;

        long afterCpuSchedule =
            Stopwatch.GetTimestamp();

        // RIGHT: PRESENTATION_PLUS_FINAL_UNORM8 (quantization mode 2)
        // generated from the SAME native snapshot sequence/hostTicks.
        long rightUploadBegin =
            afterCpuSchedule;

        _gpuInput.Upload(
            _nativePresentationFinalUnorm8Nchw);

        long afterRightUpload =
            Stopwatch.GetTimestamp();

        long rightScheduleBegin =
            afterRightUpload;

        _gpuWorker.Schedule(
            _gpuInput);

        Tensor<float> gpuTensor =
            _gpuWorker.PeekOutput(0)
            as Tensor<float>;

        long afterRightSchedule =
            Stopwatch.GetTimestamp();

        if (
            cpuTensor == null ||
            gpuTensor == null ||
            cpuTensor.shape.length !=
                PackedOutputLength ||
            gpuTensor.shape.length !=
                PackedOutputLength)
        {
            throw new InvalidOperationException(
                "Exact-hostTicks CPU preprocess packed output invalid.");
        }

        _outputComparisonScheduled = true;
        _cpuOutputDone = false;
        _gpuOutputDone = false;
        _outputScheduleStartTicks =
            scheduleStart;
        _cpuScheduleDoneTicks =
            afterCpuSchedule;
        _gpuScheduleDoneTicks =
            afterRightSchedule;
        _outputScheduleStartFrame =
            Time.frameCount;

        if (_pendingPairRecord != null)
        {
            _pendingPairRecord.OutputComparisonScheduled =
                true;
        }

        if (!isWarmup)
        {
            _cpuInputUploadMs.Add(
                TicksToMilliseconds(
                    afterCpuUpload -
                    cpuUploadBegin));

            _cpuScheduleCpuMs.Add(
                TicksToMilliseconds(
                    afterCpuSchedule -
                    cpuScheduleBegin));

            // Legacy metric name retained for report compatibility.
            // It now represents RIGHT CPU upload + Schedule CPU time.
            _gpuScheduleSubmitCpuMs.Add(
                TicksToMilliseconds(
                    afterRightSchedule -
                    rightUploadBegin));
        }

        BeginCpuOutputReadback(
            cpuTensor,
            isWarmup,
            pairToken);

        BeginGpuOutputReadback(
            gpuTensor,
            isWarmup,
            pairToken);
    }


    private void BeginCpuOutputReadback(
        Tensor<float> output,
        bool isWarmup,
        int pairToken)
    {
        var awaiter =
            output
                .ReadbackAndCloneAsync()
                .GetAwaiter();

        awaiter.OnCompleted(
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
                        awaiter.GetResult();

                    long done =
                        Stopwatch.GetTimestamp();

                    CopyReadableOutput(
                        readable,
                        _cpuOutput);

                    _cpuOutputDoneFrame =
                        Time.frameCount;

                    if (!isWarmup)
                    {
                        _cpuServiceMs.Add(
                            TicksToMilliseconds(
                                done -
                                _cpuScheduleDoneTicks));

                        _cpuCompletionFrames.Add(
                            Mathf.Max(
                                0,
                                _cpuOutputDoneFrame -
                                _outputScheduleStartFrame));
                    }

                    _cpuOutputDone = true;

                    TryFinishPendingPair(
                        pairToken);
                }
                catch (Exception exception)
                {
                    _outputPairErrorCount++;

                    HandlePairReadbackError(
                        isWarmup,
                        "LEFT_CPU_OUTPUT_READBACK " +
                        exception.GetType().Name +
                        " " +
                        exception.Message);
                }
                finally
                {
                    readable?.Dispose();
                }
            });
    }

    private void BeginGpuOutputReadback(
        Tensor<float> output,
        bool isWarmup,
        int pairToken)
    {
        var awaiter =
            output
                .ReadbackAndCloneAsync()
                .GetAwaiter();

        awaiter.OnCompleted(
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
                        awaiter.GetResult();

                    long done =
                        Stopwatch.GetTimestamp();

                    CopyReadableOutput(
                        readable,
                        _gpuOutput);

                    _gpuOutputDoneFrame =
                        Time.frameCount;

                    if (!isWarmup)
                    {
                        _gpuServiceMs.Add(
                            TicksToMilliseconds(
                                done -
                                _gpuScheduleDoneTicks));

                        _gpuCompletionFrames.Add(
                            Mathf.Max(
                                0,
                                _gpuOutputDoneFrame -
                                _outputScheduleStartFrame));
                    }

                    _gpuOutputDone = true;

                    TryFinishPendingPair(
                        pairToken);
                }
                catch (Exception exception)
                {
                    _outputPairErrorCount++;

                    HandlePairReadbackError(
                        isWarmup,
                        "RIGHT_CPU_OUTPUT_READBACK " +
                        exception.GetType().Name +
                        " " +
                        exception.Message);
                }
                finally
                {
                    readable?.Dispose();
                }
            });
    }

    private void CompareSemanticGeometry(
        PairRecord record,
        float cpuPresence,
        float gpuPresence)
    {
        double cpuSumX = 0.0;
        double cpuSumY = 0.0;
        double gpuSumX = 0.0;
        double gpuSumY = 0.0;
        double depthAbsSum = 0.0;

        double cpuMinX = double.PositiveInfinity;
        double cpuMaxX = double.NegativeInfinity;
        double cpuMinY = double.PositiveInfinity;
        double cpuMaxY = double.NegativeInfinity;
        double gpuMinX = double.PositiveInfinity;
        double gpuMaxX = double.NegativeInfinity;
        double gpuMinY = double.PositiveInfinity;
        double gpuMaxY = double.NegativeInfinity;

        for (int i = 0; i < BaseLandmarkCount; i++)
        {
            int baseIndex = i * 3;
            double cx = _cpuOutput[baseIndex];
            double cy = _cpuOutput[baseIndex + 1];
            double cz = _cpuOutput[baseIndex + 2];
            double gx = _gpuOutput[baseIndex];
            double gy = _gpuOutput[baseIndex + 1];
            double gz = _gpuOutput[baseIndex + 2];

            cpuSumX += cx;
            cpuSumY += cy;
            gpuSumX += gx;
            gpuSumY += gy;
            depthAbsSum += Math.Abs(cz - gz);

            cpuMinX = Math.Min(cpuMinX, cx);
            cpuMaxX = Math.Max(cpuMaxX, cx);
            cpuMinY = Math.Min(cpuMinY, cy);
            cpuMaxY = Math.Max(cpuMaxY, cy);
            gpuMinX = Math.Min(gpuMinX, gx);
            gpuMaxX = Math.Max(gpuMaxX, gx);
            gpuMinY = Math.Min(gpuMinY, gy);
            gpuMaxY = Math.Max(gpuMaxY, gy);
        }

        double inv = 1.0 / BaseLandmarkCount;
        double cpuCx = cpuSumX * inv;
        double cpuCy = cpuSumY * inv;
        double gpuCx = gpuSumX * inv;
        double gpuCy = gpuSumY * inv;

        double centroid = Math.Sqrt(
            (cpuCx - gpuCx) * (cpuCx - gpuCx) +
            (cpuCy - gpuCy) * (cpuCy - gpuCy));

        double cpuBoxCx = (cpuMinX + cpuMaxX) * 0.5;
        double cpuBoxCy = (cpuMinY + cpuMaxY) * 0.5;
        double gpuBoxCx = (gpuMinX + gpuMaxX) * 0.5;
        double gpuBoxCy = (gpuMinY + gpuMaxY) * 0.5;
        double bboxCenter = Math.Sqrt(
            (cpuBoxCx - gpuBoxCx) * (cpuBoxCx - gpuBoxCx) +
            (cpuBoxCy - gpuBoxCy) * (cpuBoxCy - gpuBoxCy));

        double bboxWidth = Math.Abs(
            (cpuMaxX - cpuMinX) - (gpuMaxX - gpuMinX));
        double bboxHeight = Math.Abs(
            (cpuMaxY - cpuMinY) - (gpuMaxY - gpuMinY));
        double meanDepth = depthAbsSum * inv;

        bool presenceMatch =
            (cpuPresence >= 0.5f) == (gpuPresence >= 0.5f);

        _semanticCentroidDiffPx.Add(centroid);
        _semanticBboxCenterDiffPx.Add(bboxCenter);
        _semanticBboxWidthDiffPx.Add(bboxWidth);
        _semanticBboxHeightDiffPx.Add(bboxHeight);
        _semanticMeanDepthAbsDiff.Add(meanDepth);

        if (!presenceMatch)
        {
            _semanticPresenceMismatchCount++;
        }

        bool pairPass =
            presenceMatch &&
            centroid <= SemanticCentroidP95PxGate &&
            bboxCenter <= SemanticBboxCenterP95PxGate &&
            bboxWidth <= SemanticBboxSizeP95PxGate &&
            bboxHeight <= SemanticBboxSizeP95PxGate;

        if (pairPass)
        {
            _semanticPairPassCount++;
        }
        else
        {
            _semanticPairFailCount++;
        }

        if (record != null)
        {
            record.SemanticCentroidDiffPx = centroid;
            record.SemanticBboxCenterDiffPx = bboxCenter;
            record.SemanticBboxWidthDiffPx = bboxWidth;
            record.SemanticBboxHeightDiffPx = bboxHeight;
            record.SemanticMeanDepthAbsDiff = meanDepth;
            record.SemanticPresenceDecisionMatch = presenceMatch;
        }
    }

    private void CompareOutputs(
        PairRecord record)
    {
        bool cpuFinite = true;
        bool gpuFinite = true;

        List<double> pair2d =
            new List<double>(
                BaseLandmarkCount);

        double pair2dSum = 0.0;
        double pair2dMax = 0.0;

        for (
            int i = 0;
            i < BaseLandmarkCount;
            i++)
        {
            int baseIndex =
                i * 3;

            float cpuX = _cpuOutput[baseIndex];
            float cpuY = _cpuOutput[baseIndex + 1];
            float cpuZ = _cpuOutput[baseIndex + 2];

            float gpuX = _gpuOutput[baseIndex];
            float gpuY = _gpuOutput[baseIndex + 1];
            float gpuZ = _gpuOutput[baseIndex + 2];

            cpuFinite &=
                IsFinite(cpuX) &&
                IsFinite(cpuY) &&
                IsFinite(cpuZ);

            gpuFinite &=
                IsFinite(gpuX) &&
                IsFinite(gpuY) &&
                IsFinite(gpuZ);

            if (
                !IsFinite(cpuX) ||
                !IsFinite(cpuY) ||
                !IsFinite(cpuZ) ||
                !IsFinite(gpuX) ||
                !IsFinite(gpuY) ||
                !IsFinite(gpuZ))
            {
                continue;
            }

            double dx =
                Math.Abs(
                    (double)cpuX -
                    gpuX);

            double dy =
                Math.Abs(
                    (double)cpuY -
                    gpuY);

            double dz =
                Math.Abs(
                    (double)cpuZ -
                    gpuZ);

            _landmarkXAbsRaw.Add(dx);
            _landmarkYAbsRaw.Add(dy);
            _landmarkZAbsRaw.Add(dz);

            double d2 =
                Math.Sqrt(
                    dx * dx +
                    dy * dy);

            _landmark2dPixelDiff.Add(d2);
            pair2d.Add(d2);
            pair2dSum += d2;

            if (d2 > pair2dMax)
            {
                pair2dMax = d2;
            }

            _landmark2dCount++;

            if (d2 <= 0.25) _within025PxCount++;
            if (d2 <= 0.50) _within050PxCount++;
            if (d2 <= 1.00) _within100PxCount++;
            if (d2 <= 2.00) _within200PxCount++;

            for (
                int c = 0;
                c < 3;
                c++)
            {
                float a =
                    _cpuOutput[
                        baseIndex + c];

                float b =
                    _gpuOutput[
                        baseIndex + c];

                _landmarkCoordinateCount++;

                if (
                    BitConverter.SingleToInt32Bits(a) ==
                    BitConverter.SingleToInt32Bits(b))
                {
                    _exactCoordinateMatchCount++;
                }
            }
        }

        float cpuRawPresence =
            _cpuOutput[
                BaseLandmarkCount * 3];

        float gpuRawPresence =
            _gpuOutput[
                BaseLandmarkCount * 3];

        cpuFinite &=
            IsFinite(cpuRawPresence);

        gpuFinite &=
            IsFinite(gpuRawPresence);

        if (!cpuFinite)
        {
            _cpuNonFiniteOutputPairCount++;
        }

        if (!gpuFinite)
        {
            _gpuNonFiniteOutputPairCount++;
        }

        if (
            !cpuFinite ||
            !gpuFinite)
        {
            _outputNonFinitePairCount++;
            return;
        }

        double pairMean = 0.0;
        double pairP95 = 0.0;

        if (pair2d.Count > 0)
        {
            double[] sorted =
                pair2d.ToArray();

            Array.Sort(sorted);

            pairMean =
                pair2dSum /
                pair2d.Count;

            pairP95 =
                Percentile(
                    sorted,
                    0.95);

            _landmark2dMeanPerPair.Add(
                pairMean);

            _landmark2dP95PerPair.Add(
                pairP95);

            _landmark2dMaxPerPair.Add(
                pair2dMax);
        }

        double rawPresenceDiff =
            Math.Abs(
                (double)cpuRawPresence -
                gpuRawPresence);

        float cpuPresence =
            Sigmoid(
                cpuRawPresence);

        float gpuPresence =
            Sigmoid(
                gpuRawPresence);

        double sigmoidDiff =
            Math.Abs(
                (double)cpuPresence -
                gpuPresence);

        _presenceRawAbsDiff.Add(
            rawPresenceDiff);

        _presenceSigmoidAbsDiff.Add(
            sigmoidDiff);

        _cpuPresence.Add(
            cpuPresence);

        _gpuPresence.Add(
            gpuPresence);

        bool cpuDecision =
            cpuPresence >=
            0.5f;

        bool gpuDecision =
            gpuPresence >=
            0.5f;

        bool nearThreshold =
            Math.Abs(
                cpuPresence -
                0.5f) <
            0.05f ||
            Math.Abs(
                gpuPresence -
                0.5f) <
            0.05f;

        if (nearThreshold)
        {
            _presenceNearThresholdPairCount++;
        }

        if (
            cpuDecision !=
            gpuDecision)
        {
            _presenceDecisionMismatchCount++;

            if (!nearThreshold)
            {
                _presenceFarThresholdMismatchCount++;
            }
        }

        CompareSemanticGeometry(
            record,
            cpuPresence,
            gpuPresence);

        if (record != null)
        {
            record.Landmark2dMeanPx =
                pairMean;
            record.Landmark2dP95Px =
                pairP95;
            record.Landmark2dMaxPx =
                pair2dMax;
            record.PresenceSigmoidAbsDiff =
                sigmoidDiff;
        }
    }

    private static void CopyReadableOutput(
        Tensor<float> readable,
        float[] destination)
    {
        if (
            readable == null ||
            destination == null ||
            destination.Length <
                PackedOutputLength ||
            readable.shape.length !=
                PackedOutputLength)
        {
            throw new InvalidOperationException(
                "Readable packed output invalid.");
        }

        for (
            int i = 0;
            i < PackedOutputLength;
            i++)
        {
            destination[i] =
                readable[i];
        }
    }

    private static Model BuildSingleReadbackModel(
        Model source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(
                nameof(source));
        }

        int landmarkIndex =
            FindOutputIndex(
                source,
                LandmarkOutputName);

        int presenceIndex =
            FindOutputIndex(
                source,
                PresenceOutputName);

        if (
            landmarkIndex < 0 ||
            presenceIndex < 0)
        {
            throw new InvalidOperationException(
                "Expected landmark model outputs are missing.");
        }

        FunctionalGraph graph =
            new FunctionalGraph();

        FunctionalTensor[] inputs =
            graph.AddInputs(
                source);

        FunctionalTensor[] outputs =
            Functional.Forward(
                source,
                inputs);

        FunctionalTensor landmarks =
            outputs[landmarkIndex]
                .Reshape(
                    new[]
                    {
                        BaseLandmarkCount *
                        3
                    });

        FunctionalTensor presence =
            outputs[presenceIndex]
                .Reshape(
                    new[] { 1 });

        FunctionalTensor packed =
            Functional.Concat(
                new[]
                {
                    landmarks,
                    presence
                },
                0);

        return
            graph.Compile(
                packed);
    }

    private static int FindOutputIndex(
        Model model,
        string name)
    {
        for (
            int i = 0;
            i < model.outputs.Count;
            i++)
        {
            if (
                model.outputs[i].name ==
                name)
            {
                return i;
            }
        }

        return -1;
    }

    private static float Sigmoid(
        float value)
    {
        if (!IsFinite(value))
        {
            return 0f;
        }

        double x = value;

        if (x >= 0.0)
        {
            double z =
                Math.Exp(-x);

            return
                (float)(
                    1.0 /
                    (1.0 + z));
        }

        double e =
            Math.Exp(x);

        return
            (float)(
                e /
                (1.0 + e));
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

            ExpandIdentityNeighborhoodIfNeeded(
                _pendingPairRecord);

        }

        return true;
    }







    private void BeginMeasurement()
    {
        _measuring = true;
        _measurementStart = Time.realtimeSinceStartupAsDouble;
        _nextPairAt = _measurementStart;
        CaptureStartCounters();

        Debug.Log(
            "[Kiwi v44.55.15 ExactCpuInput] MEASURE_START " +
            "observerOnly=1" +
            " staticFirst=1" +
            " measurementRunPlan=ONE_LONG_INTEGRATED_RUN" +
            " productionGpuAuthority=1" +
            " runtimeGatePurpose=EXACT_HOSTTICKS_PREPROCESS_SEMANTIC_ISOLATION" +
            " cpuAuthority=0" +
            " identityCorrection=DISABLED_SAME_EXACT_SNAPSHOT" +
            " cpuPath=LEFT_CURRENT_FLOAT_CPU_WORKER" +
            " gpuPath=RIGHT_MODE2_CPU_WORKER_LEGACY_NAME" +
            " semanticMetrics=PRESENCE,CENTROID,BBOX,LANDMARKS,DEPTH" +
            " productionWorkerTouched=0" +
            " productionTrackerWrites=0" +
            " productionFaceTextureWrites=0" +
            " productionLaneInputReadback=0" +
            " sampleHz=" + _sampleHz.ToString("F2", CultureInfo.InvariantCulture) +
            " durationSeconds=" + _durationSeconds.ToString("F1", CultureInfo.InvariantCulture) +
            " blockingGpuWait=0");
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
                "[Kiwi v44.55.15 ExactCpuInput] REFLECTION_READY " +
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

    private void WriteArmedProof()
    {
        try
        {
            string directory =
                Path.Combine(
                    Application.persistentDataPath,
                    "KiwiFrameBottleneck");

            Directory.CreateDirectory(
                directory);

            string path =
                Path.Combine(
                    directory,
                    "KiwiExactCpuInputSemantic_v44_55_15_ARMED.txt");

            string[] lines =
            {
                "KiwiAvatarSystem v44.55.15 Exact-HostTicks CPU Preprocess Semantic Gate",
                "contract=" + Contract,
                "state=ARMED",
                "generated=" +
                    DateTime.Now.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                "persistentDataPath=" +
                    Application.persistentDataPath,
                "enableEnv=" +
                    (Environment.GetEnvironmentVariable(
                        EnableVariable) ?? "<null>"),
                "durationSeconds=" +
                    _durationSeconds.ToString(
                        "F1",
                        CultureInfo.InvariantCulture),
                "sampleHz=" +
                    _sampleHz.ToString(
                        "F2",
                        CultureInfo.InvariantCulture),
                "stableSeconds=" +
                    _stableSeconds.ToString(
                        "F1",
                        CultureInfo.InvariantCulture),
                "expectedTriangles=" +
                    _expectedTriangles.ToString(
                        CultureInfo.InvariantCulture),
                "productionAuthority=GPU_UNCHANGED",
                "observerOnly=1"
            };

            File.WriteAllLines(
                path,
                lines);

            Debug.Log(
                "[Kiwi v44.55.15 ExactCpuInput] ARMED_PROOF " +
                "path=" + path);
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "[Kiwi v44.55.15 ExactCpuInput] ARMED_PROOF_FAIL " +
                exception.GetType().Name +
                " " +
                exception.Message);
        }
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
                "KiwiExactCpuInputSemantic_v44_55_15_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture) +
                ".txt");

        List<string> lines =
            new List<string>();

        lines.Add(
            "KiwiAvatarSystem v44.55.15 Exact-HostTicks CPU Preprocess Semantic Gate");
        lines.Add("contract=" + Contract);
        lines.Add("status=" + status);
        lines.Add("observerOnly=1");
        lines.Add("productionTrackerWrites=0");
        lines.Add("productionWorkerWrites=0");
        lines.Add("productionBackendChange=0");
        lines.Add("productionRoiWrites=0");
        lines.Add("productionCameraChange=0");
        lines.Add("comparisonPurpose=ISOLATE_PLAIN_EXACT_HOSTTICKS_VS_MODE2_PREPROCESS_SEMANTICS");
        lines.Add("sourceIdentity=SAME_DIAGNOSTIC_SNAPSHOT_SEQUENCE_AND_HOSTTICKS");
        lines.Add("samplingMatrixIdentity=SAME_PRODUCTION_LANE_CROP_MATERIAL_XFORM");
        lines.Add("leftInput=CURRENT_FLOAT_EXACT_SNAPSHOT");
        lines.Add("rightInput=PRESENTATION_PLUS_FINAL_UNORM8_MODE2_EXACT_SNAPSHOT");
        lines.Add("leftBackend=CPU");
        lines.Add("rightBackend=CPU");
        lines.Add("identityNeighborCorrectionUsed=0");
        lines.Add("mutablePresentationTextureUsedForDecision=0");
        lines.Add("productionLaneInputReadback=0");
        lines.Add("sameModel=KiwiFaceLandmarkInference");
        lines.Add("validationPriority=STATIC_FIRST");
        lines.Add("productionGpuAuthority=1");
        lines.Add("cpuAuthority=0");
        lines.Add("semanticAuthorityWrites=0");
        lines.Add("runtimeDefaultSeconds=60");
        lines.Add("runtimeDefaultHz=1.0");
        lines.Add("packedOutputLength=1405");
        lines.Add("packedLandmarkOutput=conv2d_20");
        lines.Add("packedPresenceOutput=conv2d_30");
        lines.Add("identityBurstUsedForDecision=0");
        lines.Add("identityExpansionThresholdLsb=NOT_APPLICABLE");
        lines.Add("identityExpandOnlyOutliers=0");
        lines.Add("freezeOrder=MATCH_THEN_COPYTEXTURE_BEFORE_CPU_CROP");
        lines.Add("pairRecordsEnabled=1");
        lines.Add("unorm8ReferenceRule=CLAMP_X255_PLUS_0_5_DROP_FRACTION_DIV255");
        lines.Add("productionPresentationFormat=DXGI_FORMAT_R8G8B8A8_UNORM");
        lines.Add("productionCropTexture=ARGB32_LINEAR_192X192");
        lines.Add("nativeProductionPathChanged=0");
        lines.Add("nativeDiagnosticApiAddedByThisVersion=0");
        lines.Add("interopDiagnosticApiAddedByThisVersion=0");
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
        lines.Add("identityCandidateApiFailureCount=" + _identityCandidateApiFailureCount);
        lines.Add("identityExpandedPairCount=" + _identityExpandedPairCount);
        lines.Add("identityNeighborWinsCount=" + _identityNeighborWinsCount);
        lines.Add("identityTargetWinsOutlierCount=" + _identityTargetWinsOutlierCount);
        lines.Add("identityNeighborUnder0_5LsbCount=" + _identityNeighborUnderHalfLsbCount);
        lines.Add("identityCorrectionCopyFailureCount=" + _identityCorrectionCopyFailureCount);
        lines.Add("identityCorrectedOver0_5LsbCount=" + _identityCorrectedOverHalfLsbCount);
        lines.Add("legacyCpuMetricAlias=LEFT_CPU_CURRENT_FLOAT");
        lines.Add("legacyGpuMetricAlias=RIGHT_CPU_PRESENTATION_PLUS_FINAL_UNORM8");
        lines.Add("outputPairCompletedCount=" + _outputPairCompletedCount);
        lines.Add("outputPairErrorCount=" + _outputPairErrorCount);
        lines.Add("cpuNonFiniteOutputPairCount=" + _cpuNonFiniteOutputPairCount);
        lines.Add("gpuNonFiniteOutputPairCount=" + _gpuNonFiniteOutputPairCount);
        lines.Add("outputNonFinitePairCount=" + _outputNonFinitePairCount);
        lines.Add("presenceDecisionMismatchCount=" + _presenceDecisionMismatchCount);
        lines.Add("presenceFarThresholdMismatchCount=" + _presenceFarThresholdMismatchCount);
        lines.Add("presenceNearThresholdPairCount=" + _presenceNearThresholdPairCount);
        lines.Add("semanticPresenceMismatchCount=" + _semanticPresenceMismatchCount);
        lines.Add("semanticPairPassCount=" + _semanticPairPassCount);
        lines.Add("semanticPairFailCount=" + _semanticPairFailCount);
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

        AppendDoubleStats(
            lines,
            "identityCorrectedInputMeanAbsLsb",
            _identityCorrectedInputMeanAbsLsb);

        AppendDoubleStats(
            lines,
            "cpuInputUploadMs",
            _cpuInputUploadMs);

        AppendDoubleStats(
            lines,
            "cpuScheduleCpuMs",
            _cpuScheduleCpuMs);

        AppendDoubleStats(
            lines,
            "gpuScheduleSubmitCpuMs",
            _gpuScheduleSubmitCpuMs);

        AppendDoubleStats(
            lines,
            "cpuServiceMs",
            _cpuServiceMs);

        AppendDoubleStats(
            lines,
            "gpuServiceMs",
            _gpuServiceMs);

        AppendDoubleStats(
            lines,
            "outputPairServiceMs",
            _outputPairServiceMs);

        AppendIntStats(
            lines,
            "cpuCompletionFrames",
            _cpuCompletionFrames);

        AppendIntStats(
            lines,
            "gpuCompletionFrames",
            _gpuCompletionFrames);

        AppendIntStats(
            lines,
            "outputPairCompletionFrames",
            _outputPairCompletionFrames);

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
                presentationFinalMean);

        string bestCandidate =
            presentationFinalMean <=
                currentMean
                ? "PRESENTATION_PLUS_FINAL_UNORM8"
                : "CURRENT_FLOAT";

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
        lines.Add("[IDENTITY_CORRECTED_CPU_GPU_OUTPUT_EQUIVALENCE]");

        AppendDoubleStats(
            lines,
            "landmarkXAbsRaw",
            _landmarkXAbsRaw);

        AppendDoubleStats(
            lines,
            "landmarkYAbsRaw",
            _landmarkYAbsRaw);

        AppendDoubleStats(
            lines,
            "landmarkZAbsRaw",
            _landmarkZAbsRaw);

        AppendDoubleStats(
            lines,
            "landmark2dPixelDiff",
            _landmark2dPixelDiff);

        AppendDoubleStats(
            lines,
            "landmark2dMeanPerPair",
            _landmark2dMeanPerPair);

        AppendDoubleStats(
            lines,
            "landmark2dP95PerPair",
            _landmark2dP95PerPair);

        AppendDoubleStats(
            lines,
            "landmark2dMaxPerPair",
            _landmark2dMaxPerPair);

        AppendDoubleStats(
            lines,
            "presenceRawAbsDiff",
            _presenceRawAbsDiff);

        AppendDoubleStats(
            lines,
            "presenceSigmoidAbsDiff",
            _presenceSigmoidAbsDiff);

        AppendDoubleStats(
            lines,
            "cpuPresence",
            _cpuPresence);

        AppendDoubleStats(
            lines,
            "gpuPresence",
            _gpuPresence);

        lines.Add(
            "exactCoordinateMatchRatio=" +
            Ratio(
                _exactCoordinateMatchCount,
                _landmarkCoordinateCount)
                .ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "landmarkWithin0_25PxRatio=" +
            Ratio(
                _within025PxCount,
                _landmark2dCount)
                .ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "landmarkWithin0_50PxRatio=" +
            Ratio(
                _within050PxCount,
                _landmark2dCount)
                .ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "landmarkWithin1_00PxRatio=" +
            Ratio(
                _within100PxCount,
                _landmark2dCount)
                .ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "landmarkWithin2_00PxRatio=" +
            Ratio(
                _within200PxCount,
                _landmark2dCount)
                .ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add("");

        lines.Add("[TASK_LEVEL_SEMANTIC_AB]");
        AppendDoubleStats(lines, "semanticCentroidDiffPx", _semanticCentroidDiffPx);
        AppendDoubleStats(lines, "semanticBboxCenterDiffPx", _semanticBboxCenterDiffPx);
        AppendDoubleStats(lines, "semanticBboxWidthDiffPx", _semanticBboxWidthDiffPx);
        AppendDoubleStats(lines, "semanticBboxHeightDiffPx", _semanticBboxHeightDiffPx);
        AppendDoubleStats(lines, "semanticMeanDepthAbsDiff", _semanticMeanDepthAbsDiff);
        lines.Add("semanticLandmarkP95PxGate=" + SemanticLandmarkP95PxGate.ToString("F3", CultureInfo.InvariantCulture));
        lines.Add("semanticLandmarkWithin2PxRatioGate=" + SemanticLandmarkWithin2PxRatioGate.ToString("F6", CultureInfo.InvariantCulture));
        lines.Add("semanticCentroidP95PxGate=" + SemanticCentroidP95PxGate.ToString("F3", CultureInfo.InvariantCulture));
        lines.Add("semanticBboxCenterP95PxGate=" + SemanticBboxCenterP95PxGate.ToString("F3", CultureInfo.InvariantCulture));
        lines.Add("semanticBboxSizeP95PxGate=" + SemanticBboxSizeP95PxGate.ToString("F3", CultureInfo.InvariantCulture));
        lines.Add("");

        lines.Add("[PAIR_RECORDS]");
        lines.Add(
            "format=index|sequence|nativeHostTicks|managedHostTicks|laneStartedTicks|" +
            "sourceAgeFreezeMs|sourceAgeAfterCpuMs|freezeSubmitCpuMs|snapshotReadbackMs|" +
            "observerTensorReadbackMs|laneStillMatchedAfterCpu|currentMaeLsb|quantizedMaeLsb|" +
            "quantizedToCurrentRatio|snapshotFlipYMaeLsb|quantizedP95Lsb|quantizedP99Lsb|" +
            "quantizedMaxLsb|identityValidCount|identityBurstComplete|identityExpanded|" +
            "identityBestSequence|identityBestHostTicks|identityBestRole|identityBestMaeLsb|" +
            "identityBestToTargetRatio|identityBestIsTarget|identityCorrectedInputMaeLsb|" +
            "landmark2dMeanPx|landmark2dP95Px|landmark2dMaxPx|presenceSigmoidAbsDiff|" +
            "semanticCentroidDiffPx|semanticBboxCenterDiffPx|semanticBboxWidthDiffPx|" +
            "semanticBboxHeightDiffPx|semanticMeanDepthAbsDiff|semanticPresenceMatch|" +
            "identityCandidateSummary");

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
                    CultureInfo.InvariantCulture) + "|" +
                record.IdentityValidCandidateCount + "|" +
                (record.IdentityBurstComplete ? 1 : 0) + "|" +
                (record.IdentityExpanded ? 1 : 0) + "|" +
                record.IdentityBestSequence + "|" +
                record.IdentityBestHostTicks + "|" +
                record.IdentityBestRole + "|" +
                record.IdentityBestMeanAbsLsb.ToString(
                    "F9",
                    CultureInfo.InvariantCulture) + "|" +
                record.IdentityBestToTargetRatio.ToString(
                    "F9",
                    CultureInfo.InvariantCulture) + "|" +
                (record.IdentityBestIsTarget ? 1 : 0) + "|" +
                record.IdentityCorrectedInputMeanAbsLsb.ToString(
                    "F9",
                    CultureInfo.InvariantCulture) + "|" +
                record.Landmark2dMeanPx.ToString(
                    "F9",
                    CultureInfo.InvariantCulture) + "|" +
                record.Landmark2dP95Px.ToString(
                    "F9",
                    CultureInfo.InvariantCulture) + "|" +
                record.Landmark2dMaxPx.ToString(
                    "F9",
                    CultureInfo.InvariantCulture) + "|" +
                record.PresenceSigmoidAbsDiff.ToString(
                    "F9",
                    CultureInfo.InvariantCulture) + "|" +
                record.SemanticCentroidDiffPx.ToString("F9", CultureInfo.InvariantCulture) + "|" +
                record.SemanticBboxCenterDiffPx.ToString("F9", CultureInfo.InvariantCulture) + "|" +
                record.SemanticBboxWidthDiffPx.ToString("F9", CultureInfo.InvariantCulture) + "|" +
                record.SemanticBboxHeightDiffPx.ToString("F9", CultureInfo.InvariantCulture) + "|" +
                record.SemanticMeanDepthAbsDiff.ToString("F9", CultureInfo.InvariantCulture) + "|" +
                (record.SemanticPresenceDecisionMatch ? 1 : 0) + "|" +
                (record.IdentityCandidateSummary ?? string.Empty));
        }

        lines.Add("");

        lines.Add("[DECISION_GUIDE]");
        lines.Add(
            "STATIC_GATE mandatory: exact Production tracker/runner/interop/native DLL hashes unchanged; " +
            "this version installs only one observer Validation source.");
        lines.Add(
            "PREPROCESS_SEMANTIC_PASS if presenceDecisionMismatchCount=0, semanticPresenceMismatchCount=0, " +
            "outputNonFinitePairCount=0, landmark2dPixelDiff p95 <=1.0 px, " +
            "landmarkWithin2_00PxRatio >=0.995, semanticCentroidDiffPx p95 <=0.25 px, " +
            "semanticBboxCenterDiffPx p95 <=0.25 px, and bbox width/height p95 <=0.50 px.");
        lines.Add(
            "PASS means the active-DLL plain exact-hostTicks CURRENT_FLOAT input is task-semantically " +
            "equivalent to mode-2 PRESENTATION_PLUS_FINAL_UNORM8 on the same native frame. " +
            "Only then may plain exact-hostTicks be considered for Production CPU authority.");
        lines.Add(
            "REJECT on any source identity mismatch, presence decision mismatch, non-finite output, " +
            "semantic geometry gate failure, Production hash drift, or camera/Production regression.");




        File.WriteAllLines(
            path,
            lines);

        Debug.Log(
            "[Kiwi v44.55.15 ExactCpuInput] " +
            status +
            " report=" +
            path);
    }

    private void OnDisable()
    {
        if (_reportWritten)
        {
            return;
        }

        bool observerProgressed =
            _measuring ||
            _workersReady ||
            _reflectionReady ||
            _pairAttemptCount > 0;

        if (!observerProgressed)
        {
            Debug.LogWarning(
                "[Kiwi v44.55.15 ExactCpuInput] STOPPED_BEFORE_OBSERVER_READY " +
                "armedProofShouldExist=1");
            return;
        }

        try
        {
            WriteReport(
                _measuring
                    ? "STOPPED_BEFORE_COMPLETE"
                    : "STOPPED_BEFORE_MEASURE");
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[Kiwi v44.55.15 ExactCpuInput] STOP_REPORT_FAIL " +
                exception.GetType().Name +
                " " +
                exception.Message);
        }
    }


    private void OnDestroy()
    {
        if (_outputGpuCommandBuffer != null)
        {
            _outputGpuCommandBuffer.Release();
            _outputGpuCommandBuffer = null;
        }

        _cpuWorker?.Dispose();
        _cpuWorker = null;

        _gpuWorker?.Dispose();
        _gpuWorker = null;

        _cpuInput?.Dispose();
        _cpuInput = null;

        _gpuInput?.Dispose();
        _gpuInput = null;

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
        if (_reportWritten)
        {
            return;
        }

        if (
            _measuring ||
            _workersReady ||
            _reflectionReady ||
            _pairAttemptCount > 0)
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
