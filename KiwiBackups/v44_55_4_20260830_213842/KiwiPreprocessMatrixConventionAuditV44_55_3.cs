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
using Debug = UnityEngine.Debug;

/// <summary>
/// KiwiAvatarSystem v44.55.1
/// Production Input / Native CPU Preprocess Equivalence Audit.
///
/// This observer does NOT recreate the Production GPU crop path.
///
/// Instead it reads, by reflection only:
/// - a currently pending Production KiwiInferenceFaceTracker lane;
/// - that lane's exact pendingSourceHostTicks;
/// - that lane's exact crop material _Xform;
/// - that lane's actual GPU Tensor<float> input consumed by Production.
///
/// It then asks Native Path-B for the exact CpuSlot whose host timestamp matches
/// pendingSourceHostTicks and applies the same _Xform into a CPU NCHW float crop.
///
/// Finally it asynchronously clones the Production lane input and compares the
/// two 1x3x192x192 tensors numerically.
///
/// No Production tracker field is written. No worker/backend/threshold/ROI/camera
/// presentation/render setting is changed.
/// </summary>
internal sealed class KiwiPreprocessMatrixConventionAuditV44_55_3
    : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_55_3_MATRIX_SPACE_V_CONVENTION_ABCD_AUDIT";

    private const string EnableVariable =
        "KIWI_V44_55_3_MATRIX_CONVENTION_AUDIT";

    private const string DurationVariable =
        "KIWI_V44_55_3_MATRIX_CONVENTION_SECONDS";

    private const string SampleHzVariable =
        "KIWI_V44_55_3_MATRIX_CONVENTION_HZ";

    private const string StableSecondsVariable =
        "KIWI_V44_55_3_STABLE_SECONDS";

    private const string ExpectedTrianglesVariable =
        "KIWI_V44_55_3_EXPECTED_TRIANGLES";

    private const int InputSize = 192;
    private const int PlaneLength =
        InputSize *
        InputSize;
    private const int InputFloatCount =
        PlaneLength *
        3;

    private const float DefaultDurationSeconds = 30f;
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

    // v44.55.1 FIX2: Native snapshot identity is raw Native QPC, while
    // Production Runner publishes calibrated Managed Stopwatch host ticks.
    // Sequence is the common identity. These fields are read-only.
    private FieldInfo _runnerLastObservedFreshSequenceField;
    private FieldInfo _runnerLatestSentisSourceHostTicksField;

    private Type _laneType;
    private FieldInfo _laneInputField;
    private FieldInfo _laneCropMaterialField;
    private FieldInfo _laneReadbackPendingField;
    private FieldInfo _lanePendingSourceHostTicksField;
    private FieldInfo _lanePendingStartedHostTicksField;
    private FieldInfo _lanePendingCropMatrixField;

    private Array _lanes;

    private readonly float[] _samplingMatrixCurrent =
        new float[16];

    private readonly float[] _samplingMatrixInputV =
        new float[16];

    private readonly float[] _samplingMatrixOutputV =
        new float[16];

    private readonly float[] _samplingMatrixBothV =
        new float[16];

    private readonly float[] _nativeCurrentNchw =
        new float[InputFloatCount];

    private readonly float[] _nativeInputVNchw =
        new float[InputFloatCount];

    private readonly float[] _nativeOutputVNchw =
        new float[InputFloatCount];

    private readonly float[] _nativeBothVNchw =
        new float[InputFloatCount];

    private readonly float[] _gpuNchw =
        new float[InputFloatCount];

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

    private readonly MetricAccumulator _currentMetrics =
        new MetricAccumulator(
            "CURRENT_M");

    private readonly MetricAccumulator _inputVMetrics =
        new MetricAccumulator(
            "INPUT_V_MxF");

    private readonly MetricAccumulator _outputVMetrics =
        new MetricAccumulator(
            "OUTPUT_V_FxM");

    private readonly MetricAccumulator _bothVMetrics =
        new MetricAccumulator(
            "BOTH_V_FxMxF");

    private int _currentBestPairCount;
    private int _inputVBestPairCount;
    private int _outputVBestPairCount;
    private int _bothVBestPairCount;

    private int _currentCropFailureCount;
    private int _inputVCropFailureCount;
    private int _outputVCropFailureCount;
    private int _bothVCropFailureCount;

    private readonly List<double> _nativeFourCandidateCpuMs =
        new List<double>(128);

    private readonly List<double> _nativeFourCandidateWallMs =
        new List<double>(128);

    private readonly List<double> _nativeCurrentCropCpuMs =
        new List<double>(128);

    private readonly List<double> _nativeInputVCropCpuMs =
        new List<double>(128);

    private readonly List<double> _nativeOutputVCropCpuMs =
        new List<double>(128);

    private readonly List<double> _nativeBothVCropCpuMs =
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
                "[Kiwi] v44.55.3 Matrix-Space V-Convention A/B/C/D Audit");

        DontDestroyOnLoad(go);
        go.hideFlags =
            HideFlags.DontSave;

        go.AddComponent<
            KiwiPreprocessMatrixConventionAuditV44_55_3>();
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

        Debug.Log(
            "[Kiwi v44.55.3 MatrixABCD] WAIT_GATE " +
            "contract=" + Contract +
            " observerOnly=1" +
            " productionTrackerWrites=0" +
            " productionWorkerWrites=0" +
            " productionBackendChange=0" +
            " productionRoiWrites=0" +
            " productionCameraChange=0" +
            " gpuSide=ACTUAL_PRODUCTION_LANE_INPUT" +
            " cpuSide=MATRIX_CANDIDATES_M_MxF_FxM_FxMxF_SAME_NATIVE_SNAPSHOT" +
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
                    "[Kiwi v44.55.3 MatrixABCD] GATE_MATCH " +
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
            "[Kiwi v44.55.3 MatrixABCD] READY_FOR_WARMUP " +
            "actualProductionLaneInput=1" +
            " armedNativeSnapshot=1" +
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
            // Warmup and retry paths are also capped at the requested audit Hz.
            // Without this guard, a failed identity match could re-arm every
            // Unity frame before MEASURE_START and distort Production timing.
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
                    _snapshotRequestActive = false;
                }

                return false;
            }
        }

        // v44.55.1 FIX2:
        // Native snapshot hostTicks are raw Native QPC. Production's v14
        // provider boundary calibrates timestamps into Managed Stopwatch
        // domain before KiwiInferenceFaceTracker stores pendingSourceHostTicks.
        // Match by sequence first, then latch Runner's calibrated host tick.
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
                // The armed Native frame was legitimately superseded before
                // becoming the Runner's presented fresh-frame identity.
                _snapshotSequenceSkippedCount++;
                _snapshotRequestActive = false;
                _snapshotSequence = 0;
                _snapshotHostTicks = 0;
                _snapshotManagedHostTicks = 0;
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
                    _snapshotRequestActive = false;
                    _snapshotSequence = 0;
                    _snapshotHostTicks = 0;
                    _snapshotManagedHostTicks = 0;
                }

                return false;
            }
        }

        object matchedLane = null;
        long matchedStartedTicks = 0;

        int length =
            _lanes.Length;

        for (
            int i = 0;
            i < length;
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
                _snapshotRequestActive = false;
                _snapshotSequence = 0;
                _snapshotHostTicks = 0;
                _snapshotManagedHostTicks = 0;
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

        Tensor<float> productionInput =
            _laneInputField.GetValue(
                matchedLane)
            as Tensor<float>;

        if (
            cropMaterial == null ||
            productionInput == null ||
            productionInput.shape.length !=
                InputFloatCount)
        {
            _inputShapeMismatchCount++;
            _pairErrorCount++;
            _snapshotRequestActive = false;

            if (isWarmup)
            {
                WriteReport("WARMUP_FAIL");
            }

            return false;
        }

        Matrix4x4 samplingMatrix =
            cropMaterial.GetMatrix(
                XformId);

        BuildMatrixCandidates(
            samplingMatrix);

        long pairStart =
            Stopwatch.GetTimestamp();

        long nativeBegin =
            pairStart;

        bool currentCopied =
            TryCopyMatrixCandidate(
                _samplingMatrixCurrent,
                _nativeCurrentNchw,
                out ulong currentSequence,
                out long currentHostTicks,
                out ulong currentCpuUs);

        bool inputVCopied =
            TryCopyMatrixCandidate(
                _samplingMatrixInputV,
                _nativeInputVNchw,
                out ulong inputVSequence,
                out long inputVHostTicks,
                out ulong inputVCpuUs);

        bool outputVCopied =
            TryCopyMatrixCandidate(
                _samplingMatrixOutputV,
                _nativeOutputVNchw,
                out ulong outputVSequence,
                out long outputVHostTicks,
                out ulong outputVCpuUs);

        bool bothVCopied =
            TryCopyMatrixCandidate(
                _samplingMatrixBothV,
                _nativeBothVNchw,
                out ulong bothVSequence,
                out long bothVHostTicks,
                out ulong bothVCpuUs);

        long afterNative =
            Stopwatch.GetTimestamp();

        bool currentValid =
            currentCopied &&
            currentHostTicks ==
                _snapshotHostTicks &&
            currentSequence ==
                _snapshotSequence;

        bool inputVValid =
            inputVCopied &&
            inputVHostTicks ==
                _snapshotHostTicks &&
            inputVSequence ==
                _snapshotSequence;

        bool outputVValid =
            outputVCopied &&
            outputVHostTicks ==
                _snapshotHostTicks &&
            outputVSequence ==
                _snapshotSequence;

        bool bothVValid =
            bothVCopied &&
            bothVHostTicks ==
                _snapshotHostTicks &&
            bothVSequence ==
                _snapshotSequence;

        if (!currentValid)
        {
            _currentCropFailureCount++;
        }

        if (!inputVValid)
        {
            _inputVCropFailureCount++;
        }

        if (!outputVValid)
        {
            _outputVCropFailureCount++;
        }

        if (!bothVValid)
        {
            _bothVCropFailureCount++;
        }

        if (
            !currentValid ||
            !inputVValid ||
            !outputVValid ||
            !bothVValid)
        {
            _snapshotCropFailureCount++;
            _pairErrorCount++;
            _snapshotRequestActive = false;

            if (isWarmup)
            {
                WriteReport("WARMUP_FAIL");
            }

            return false;
        }

        ulong nativeSequence =
            currentSequence;

        ulong nativeCpuMicroseconds =
            currentCpuUs +
            inputVCpuUs +
            outputVCpuUs +
            bothVCpuUs;

        // The snapshot is now independent of Production CpuSlot reuse.
        // Verify only that the Production lane still identifies the same job
        // before requesting its actual input tensor readback.
        if (
            !ReadLaneBool(
                matchedLane,
                _laneReadbackPendingField) ||
            ReadLaneLong(
                matchedLane,
                _lanePendingStartedHostTicksField) !=
                matchedStartedTicks ||
            ReadLaneLong(
                matchedLane,
                _lanePendingSourceHostTicksField) !=
                _snapshotManagedHostTicks)
        {
            _laneRaceDiscardCount++;
            _snapshotRequestActive = false;
            return false;
        }

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
            nativeSequence;
        _pendingPairStartTicks =
            pairStart;
        _pendingStartFrame =
            Time.frameCount;

        if (!isWarmup)
        {
            _nativeFourCandidateCpuMs.Add(
                nativeCpuMicroseconds /
                1000.0);

            _nativeFourCandidateWallMs.Add(
                TicksToMilliseconds(
                    afterNative -
                    nativeBegin));

            _nativeCurrentCropCpuMs.Add(
                currentCpuUs /
                1000.0);

            _nativeInputVCropCpuMs.Add(
                inputVCpuUs /
                1000.0);

            _nativeOutputVCropCpuMs.Add(
                outputVCpuUs /
                1000.0);

            _nativeBothVCropCpuMs.Add(
                bothVCpuUs /
                1000.0);

            _sourceAgeAtCaptureMs.Add(
                QpcAgeMilliseconds(
                    _snapshotHostTicks));
        }

        var awaiter =
            productionInput
                .ReadbackAndCloneAsync()
                .GetAwaiter();

        awaiter.OnCompleted(
            () =>
            {
                Tensor<float> readable =
                    null;

                try
                {
                    readable =
                        awaiter.GetResult();

                    long completed =
                        Stopwatch.GetTimestamp();

                    if (
                        readable == null ||
                        readable.shape.length !=
                            InputFloatCount)
                    {
                        throw new InvalidOperationException(
                            "Production lane input readback shape mismatch.");
                    }

                    for (
                        int i = 0;
                        i < InputFloatCount;
                        i++)
                    {
                        _gpuNchw[i] =
                            readable[i];
                    }

                    if (!_pendingIsWarmup)
                    {
                        _productionInputReadbackMs.Add(
                            TicksToMilliseconds(
                                completed -
                                _pendingPairStartTicks));

                        _productionInputReadbackFrames.Add(
                            Mathf.Max(
                                0,
                                Time.frameCount -
                                _pendingStartFrame));

                        _sourceAgeAtCompareMs.Add(
                            QpcAgeMilliseconds(
                                _pendingSourceHostTicks));
                    }

                    FinishPair();
                }
                catch (Exception exception)
                {
                    _gpuInputReadbackErrorCount++;
                    _pairErrorCount++;
                    _snapshotRequestActive = false;

                    Debug.LogWarning(
                        "[Kiwi v44.55.3 MatrixABCD] READBACK_ERROR " +
                        exception.GetType().Name +
                        " " +
                        exception.Message);

                    _pairPending = false;

                    if (_pendingIsWarmup)
                    {
                        WriteReport("WARMUP_FAIL");
                    }
                    else
                    {
                        WriteReport("PAIR_FAIL");
                    }
                }
                finally
                {
                    if (readable != null)
                    {
                        readable.Dispose();
                    }
                }
            });

        return true;
    }

    private void FinishPair()
    {
        bool isWarmup =
            _pendingIsWarmup;

        bool finite =
            ValidateFiniteInputs();

        _pairPending = false;
        _snapshotRequestActive = false;
        _snapshotSequence = 0;
        _snapshotHostTicks = 0;
        _snapshotManagedHostTicks = 0;

        if (!finite)
        {
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
            // v44.55.2 bugfix:
            // warmup verifies validity only. It is intentionally excluded
            // from ALL CURRENT / FLIP_Y numerical statistics.
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
            _nonFinitePairCount++;
            _pairErrorCount++;

            WriteReport("PAIR_FAIL");
            return;
        }

        _pairCompletedCount++;
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
                !IsFinite(_nativeInputVNchw[i]) ||
                !IsFinite(_nativeOutputVNchw[i]) ||
                !IsFinite(_nativeBothVNchw[i]) ||
                !IsFinite(_gpuNchw[i]))
            {
                return false;
            }
        }

        return true;
    }

    private bool CompareMeasuredPair()
    {
        bool currentOk =
            _currentMetrics.AccumulatePair(
                _nativeCurrentNchw,
                _gpuNchw);

        bool inputVOk =
            _inputVMetrics.AccumulatePair(
                _nativeInputVNchw,
                _gpuNchw);

        bool outputVOk =
            _outputVMetrics.AccumulatePair(
                _nativeOutputVNchw,
                _gpuNchw);

        bool bothVOk =
            _bothVMetrics.AccumulatePair(
                _nativeBothVNchw,
                _gpuNchw);

        if (
            !currentOk ||
            !inputVOk ||
            !outputVOk ||
            !bothVOk)
        {
            return false;
        }

        double current =
            _currentMetrics.LastPairMeanAbsLsb;

        double inputV =
            _inputVMetrics.LastPairMeanAbsLsb;

        double outputV =
            _outputVMetrics.LastPairMeanAbsLsb;

        double bothV =
            _bothVMetrics.LastPairMeanAbsLsb;

        double best =
            Math.Min(
                Math.Min(
                    current,
                    inputV),
                Math.Min(
                    outputV,
                    bothV));

        const double TieEpsilonLsb =
            0.000001;

        if (
            Math.Abs(
                current -
                best) <=
            TieEpsilonLsb)
        {
            _currentBestPairCount++;
        }

        if (
            Math.Abs(
                inputV -
                best) <=
            TieEpsilonLsb)
        {
            _inputVBestPairCount++;
        }

        if (
            Math.Abs(
                outputV -
                best) <=
            TieEpsilonLsb)
        {
            _outputVBestPairCount++;
        }

        if (
            Math.Abs(
                bothV -
                best) <=
            TieEpsilonLsb)
        {
            _bothVBestPairCount++;
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
            "[Kiwi v44.55.3 MatrixABCD] MEASURE_START " +
            "observerOnly=1" +
            " gpuInput=ACTUAL_PRODUCTION_LANE_INPUT" +
            " candidates=CURRENT_M,INPUT_V_MxF,OUTPUT_V_FxM,BOTH_V_FxMxF" +
            " warmupStatsExcluded=1" +
            " armedNativeSnapshotMatch=1" +
            " sequenceMappedTimestampDomain=1" +
            " inputShape=1x3x192x192" +
            " sampleHz=" +
                _sampleHz.ToString(
                    "F2",
                    CultureInfo.InvariantCulture) +
            " durationSeconds=" +
                _durationSeconds.ToString(
                    "F1",
                    CultureInfo.InvariantCulture) +
            " warmupComplete=1" +
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

            if (lanesField == null)
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
            _runnerLastObservedFreshSequenceField =
                lastObservedFreshSequenceField;
            _runnerLatestSentisSourceHostTicksField =
                latestSentisSourceHostTicksField;
            _laneType = laneType;
            _laneInputField = inputField;
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
                "[Kiwi v44.55.3 MatrixABCD] REFLECTION_READY " +
                "tracker=" +
                    trackerType.FullName +
                " laneType=" +
                    laneType.FullName +
                " laneCount=" +
                    lanes.Length +
                " readOnlyFields=9" +
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
            _runnerLastObservedFreshSequenceField == null ||
            _runnerLatestSentisSourceHostTicksField == null)
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

    private static Matrix4x4 BuildVerticalFlipMatrix()
    {
        Matrix4x4 flip =
            Matrix4x4.identity;

        // Homogeneous affine transform:
        // u' = u
        // v' = 1 - v
        flip.m11 = -1f;
        flip.m13 = 1f;

        return flip;
    }

    private void BuildMatrixCandidates(
        Matrix4x4 productionMatrix)
    {
        Matrix4x4 flipV =
            BuildVerticalFlipMatrix();

        // Unity Matrix4x4 multiplication uses column-vector composition.
        //
        // CURRENT:
        //   M
        //
        // INPUT_V:
        //   M * F
        //   Flip crop-input V before Production's source mapping.
        //
        // OUTPUT_V:
        //   F * M
        //   Flip mapped source V after Production's mapping.
        //
        // BOTH_V:
        //   F * M * F
        //   Apply both conventions.
        FlattenMatrix(
            productionMatrix,
            _samplingMatrixCurrent);

        FlattenMatrix(
            productionMatrix *
                flipV,
            _samplingMatrixInputV);

        FlattenMatrix(
            flipV *
                productionMatrix,
            _samplingMatrixOutputV);

        FlattenMatrix(
            flipV *
                productionMatrix *
                flipV,
            _samplingMatrixBothV);
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

    private bool TryCopyMatrixCandidate(
        float[] matrix,
        float[] destination,
        out ulong sequence,
        out long matchedHostTicks,
        out ulong nativeCpuMicroseconds)
    {
        return
            KiwiNativeCameraInterop
                .TryCopyDiagnosticCpuSnapshotCropNchwFloat(
                    _snapshotHostTicks,
                    matrix,
                    InputSize,
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
                "KiwiPreprocessMatrixABCD_v44_55_3_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture) +
                ".txt");

        List<string> lines =
            new List<string>();

        lines.Add(
            "KiwiAvatarSystem v44.55.3 Matrix-Space V-Convention A/B/C/D Audit");
        lines.Add("contract=" + Contract);
        lines.Add("status=" + status);
        lines.Add("observerOnly=1");
        lines.Add("productionTrackerWrites=0");
        lines.Add("productionWorkerWrites=0");
        lines.Add("productionBackendChange=0");
        lines.Add("productionRoiWrites=0");
        lines.Add("productionCameraChange=0");
        lines.Add("gpuReference=ACTUAL_PRODUCTION_LANE_INPUT");
        lines.Add("gpuInputSource=PRODUCTION_GRAPHICS_BLIT_TEXTURECONVERTER");
        lines.Add("candidateCurrent=CURRENT_M");
        lines.Add("candidateInputV=INPUT_V_MxF");
        lines.Add("candidateOutputV=OUTPUT_V_FxM");
        lines.Add("candidateBothV=BOTH_V_FxMxF");
        lines.Add("verticalFlipDefinition=v_to_1_minus_v");
        lines.Add("matrixCompositionConvention=UNITY_COLUMN_VECTOR");
        lines.Add("nativeDllChanged=0");
        lines.Add("nativeInteropChanged=0");
        lines.Add("nativeCropCallsPerMatchedPair=4");
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
        lines.Add("currentCropFailureCount=" + _currentCropFailureCount);
        lines.Add("inputVCropFailureCount=" + _inputVCropFailureCount);
        lines.Add("outputVCropFailureCount=" + _outputVCropFailureCount);
        lines.Add("bothVCropFailureCount=" + _bothVCropFailureCount);
        lines.Add("snapshotPresentedButNotScheduledCount=" + _snapshotPresentedButNotScheduledCount);
        lines.Add("snapshotSequenceObservedCount=" + _snapshotSequenceObservedCount);
        lines.Add("snapshotSequenceSkippedCount=" + _snapshotSequenceSkippedCount);
        lines.Add("snapshotManagedTimestampMapFailureCount=" + _snapshotManagedTimestampMapFailureCount);
        lines.Add("laneRaceDiscardCount=" + _laneRaceDiscardCount);
        lines.Add("gpuInputReadbackErrorCount=" + _gpuInputReadbackErrorCount);
        lines.Add("inputShapeMismatchCount=" + _inputShapeMismatchCount);
        lines.Add("nonFinitePairCount=" + _nonFinitePairCount);
        lines.Add("latestNativeSequence=" + _pendingNativeSequence);
        lines.Add("latestSourceHostTicks=" + _pendingSourceHostTicks);
        lines.Add("");

        AppendDoubleStats(
            lines,
            "nativeFourCandidateCpuMs",
            _nativeFourCandidateCpuMs);

        AppendDoubleStats(
            lines,
            "nativeFourCandidateWallMs",
            _nativeFourCandidateWallMs);

        AppendDoubleStats(
            lines,
            "nativeCurrentCropCpuMs",
            _nativeCurrentCropCpuMs);

        AppendDoubleStats(
            lines,
            "nativeInputVCropCpuMs",
            _nativeInputVCropCpuMs);

        AppendDoubleStats(
            lines,
            "nativeOutputVCropCpuMs",
            _nativeOutputVCropCpuMs);

        AppendDoubleStats(
            lines,
            "nativeBothVCropCpuMs",
            _nativeBothVCropCpuMs);

        AppendDoubleStats(
            lines,
            "productionInputReadbackMs",
            _productionInputReadbackMs);

        AppendIntStats(
            lines,
            "productionInputReadbackFrames",
            _productionInputReadbackFrames);

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
        lines.Add("[PREPROCESS_MATRIX_SPACE_V_ABCD]");

        AppendMetricReport(
            lines,
            "CURRENT_M",
            _currentMetrics);

        lines.Add("");

        AppendMetricReport(
            lines,
            "INPUT_V_MxF",
            _inputVMetrics);

        lines.Add("");

        AppendMetricReport(
            lines,
            "OUTPUT_V_FxM",
            _outputVMetrics);

        lines.Add("");

        AppendMetricReport(
            lines,
            "BOTH_V_FxMxF",
            _bothVMetrics);

        lines.Add("");

        lines.Add(
            "CURRENT_M.bestPairCount=" +
            _currentBestPairCount);

        lines.Add(
            "INPUT_V_MxF.bestPairCount=" +
            _inputVBestPairCount);

        lines.Add(
            "OUTPUT_V_FxM.bestPairCount=" +
            _outputVBestPairCount);

        lines.Add(
            "BOTH_V_FxMxF.bestPairCount=" +
            _bothVBestPairCount);

        lines.Add(
            "CURRENT_M.bestPairRatio=" +
            Ratio(
                _currentBestPairCount,
                _pairCompletedCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "INPUT_V_MxF.bestPairRatio=" +
            Ratio(
                _inputVBestPairCount,
                _pairCompletedCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "OUTPUT_V_FxM.bestPairRatio=" +
            Ratio(
                _outputVBestPairCount,
                _pairCompletedCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "BOTH_V_FxMxF.bestPairRatio=" +
            Ratio(
                _bothVBestPairCount,
                _pairCompletedCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        double currentMean =
            MeanAbsLsb(
                _currentMetrics);

        double inputVMean =
            MeanAbsLsb(
                _inputVMetrics);

        double outputVMean =
            MeanAbsLsb(
                _outputVMetrics);

        double bothVMean =
            MeanAbsLsb(
                _bothVMetrics);

        double bestMean =
            Math.Min(
                Math.Min(
                    currentMean,
                    inputVMean),
                Math.Min(
                    outputVMean,
                    bothVMean));

        lines.Add(
            "bestGlobalMeanAbsLsb=" +
            bestMean.ToString(
                "F9",
                CultureInfo.InvariantCulture));

        lines.Add(
            "CURRENT_M.toBestMeanRatio=" +
            SafeRatio(
                currentMean,
                bestMean).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "INPUT_V_MxF.toBestMeanRatio=" +
            SafeRatio(
                inputVMean,
                bestMean).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "OUTPUT_V_FxM.toBestMeanRatio=" +
            SafeRatio(
                outputVMean,
                bestMean).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "BOTH_V_FxMxF.toBestMeanRatio=" +
            SafeRatio(
                bothVMean,
                bestMean).ToString(
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
        lines.Add("[DECISION_GUIDE]");
        lines.Add(
            "MATRIX_CONVENTION_CONFIRMED_STRONG when exactly one transformed matrix candidate " +
            "materially beats CURRENT and has >=50 measured pairs; errors/nonfinite=0; " +
            "mean <=1 LSB; p95 <=2 LSB; p99 <=4 LSB; >=99% values <=4 LSB; " +
            "RGB signed bias magnitude <=0.5 LSB; PSNR >=42 dB; and bestPairRatio >=0.95.");
        lines.Add(
            "MATRIX_CONVENTION_CONFIRMED_NEAR_EXACT when the winning candidate mean <=0.10 LSB " +
            "and p99 <=0.50 LSB.");
        lines.Add(
            "MATRIX_CONVENTION_NOT_CONFIRMED when CURRENT remains best or all transformed " +
            "matrix candidates remain far from Production. Then do not alter orientation; " +
            "next isolate half-texel/bilinear sampling and NV12 chroma/color conversion.");
        lines.Add(
            "This is an observer audit only. Native/Interop/Production inference/tracking " +
            "remain unchanged.");


        File.WriteAllLines(
            path,
            lines);

        Debug.Log(
            "[Kiwi v44.55.3 MatrixABCD] " +
            status +
            " report=" +
            path);
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
