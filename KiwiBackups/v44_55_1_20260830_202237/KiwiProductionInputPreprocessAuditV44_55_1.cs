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
internal sealed class KiwiProductionInputPreprocessAuditV44_55_1
    : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_55_1_ARMED_SNAPSHOT_PRODUCTION_INPUT_PREPROCESS_EQUIVALENCE";

    private const string EnableVariable =
        "KIWI_V44_55_1_PREPROCESS_EQUIVALENCE_AUDIT";

    private const string DurationVariable =
        "KIWI_V44_55_1_PREPROCESS_EQUIVALENCE_SECONDS";

    private const string SampleHzVariable =
        "KIWI_V44_55_1_PREPROCESS_EQUIVALENCE_HZ";

    private const string StableSecondsVariable =
        "KIWI_V44_55_1_STABLE_SECONDS";

    private const string ExpectedTrianglesVariable =
        "KIWI_V44_55_1_EXPECTED_TRIANGLES";

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

    private Type _laneType;
    private FieldInfo _laneInputField;
    private FieldInfo _laneCropMaterialField;
    private FieldInfo _laneReadbackPendingField;
    private FieldInfo _lanePendingSourceHostTicksField;
    private FieldInfo _lanePendingStartedHostTicksField;
    private FieldInfo _lanePendingCropMatrixField;

    private Array _lanes;

    private readonly float[] _samplingMatrix =
        new float[16];

    private readonly float[] _nativeNchw =
        new float[InputFloatCount];

    private readonly float[] _gpuNchw =
        new float[InputFloatCount];

    private readonly long[] _globalHistogram =
        new long[HistogramLength];

    private readonly int[] _pairHistogram =
        new int[HistogramLength];

    private readonly double[] _channelSignedSum =
        new double[3];

    private readonly double[] _channelAbsSum =
        new double[3];

    private readonly double[] _channelSquaredSum =
        new double[3];

    private readonly double[] _channelMaxAbsLsb =
        new double[3];

    private long _comparedValueCount;
    private long _exactFloatMatchCount;
    private long _withinHalfLsbCount;
    private long _withinOneLsbCount;
    private long _withinTwoLsbCount;
    private long _withinFourLsbCount;
    private long _withinEightLsbCount;

    private double _globalAbsLsbSum;
    private double _globalSquaredNormalizedSum;
    private double _globalMaxAbsLsb;

    private readonly List<double> _pairMeanAbsLsb =
        new List<double>(128);

    private readonly List<double> _pairP95AbsLsb =
        new List<double>(128);

    private readonly List<double> _pairP99AbsLsb =
        new List<double>(128);

    private readonly List<double> _pairMaxAbsLsb =
        new List<double>(128);

    private readonly List<double> _nativeExactCropCpuMs =
        new List<double>(128);

    private readonly List<double> _nativeExactCropWallMs =
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

    private long _lastCapturedProductionStartedTicks;

    private const double SnapshotMatchTimeoutSeconds = 0.250;

    private bool _snapshotRequestActive;
    private double _snapshotArmRealtime;
    private ulong _snapshotSequence;
    private long _snapshotHostTicks;

    private int _snapshotArmCount;
    private int _snapshotReadyCount;
    private int _snapshotMatchTimeoutCount;
    private int _snapshotArmFailureCount;
    private int _snapshotCropFailureCount;
    private int _snapshotPresentedButNotScheduledCount;

    private long _pendingStartedTicks;
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
                "[Kiwi] v44.55 Production Input Preprocess Audit");

        DontDestroyOnLoad(go);
        go.hideFlags =
            HideFlags.DontSave;

        go.AddComponent<
            KiwiProductionInputPreprocessAuditV44_55_1>();
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
            "[Kiwi v44.55.1 Preprocess] WAIT_GATE " +
            "contract=" + Contract +
            " observerOnly=1" +
            " productionTrackerWrites=0" +
            " productionWorkerWrites=0" +
            " productionBackendChange=0" +
            " productionRoiWrites=0" +
            " productionCameraChange=0" +
            " gpuSide=ACTUAL_PRODUCTION_LANE_INPUT" +
            " cpuSide=ARMED_NEXT_FRAME_NATIVE_PATH_B_SNAPSHOT" +
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
                    "[Kiwi v44.55.1 Preprocess] GATE_MATCH " +
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
            "[Kiwi v44.55.1 Preprocess] READY_FOR_WARMUP " +
            "actualProductionLaneInput=1" +
            " armedNativeSnapshot=1" +
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
            _snapshotArmCount++;

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
                    _snapshotHostTicks ||
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

        FlattenMatrix(
            samplingMatrix);

        long pairStart =
            Stopwatch.GetTimestamp();

        long nativeBegin =
            pairStart;

        bool copied =
            KiwiNativeCameraInterop
                .TryCopyDiagnosticCpuSnapshotCropNchwFloat(
                    _snapshotHostTicks,
                    _samplingMatrix,
                    InputSize,
                    _nativeNchw,
                    out ulong nativeSequence,
                    out long matchedHostTicks,
                    out ulong nativeCpuMicroseconds);

        long afterNative =
            Stopwatch.GetTimestamp();

        if (
            !copied ||
            matchedHostTicks !=
                _snapshotHostTicks ||
            nativeSequence !=
                _snapshotSequence)
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
                _snapshotHostTicks)
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
        _pendingNativeSequence =
            nativeSequence;
        _pendingPairStartTicks =
            pairStart;
        _pendingStartFrame =
            Time.frameCount;

        if (!isWarmup)
        {
            _nativeExactCropCpuMs.Add(
                nativeCpuMicroseconds /
                1000.0);

            _nativeExactCropWallMs.Add(
                TicksToMilliseconds(
                    afterNative -
                    nativeBegin));

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
                        "[Kiwi v44.55.1 Preprocess] READBACK_ERROR " +
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
        bool finite =
            CompareInputs();

        bool isWarmup =
            _pendingIsWarmup;

        _pairPending = false;
        _snapshotRequestActive = false;
        _snapshotSequence = 0;
        _snapshotHostTicks = 0;

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
            _warmupPairsRemaining--;

            if (
                _warmupPairsRemaining <=
                0)
            {
                BeginMeasurement();
            }

            return;
        }

        _pairCompletedCount++;
    }

    private bool CompareInputs()
    {
        Array.Clear(
            _pairHistogram,
            0,
            _pairHistogram.Length);

        double pairAbsLsbSum = 0.0;
        double pairMaxLsb = 0.0;

        for (
            int i = 0;
            i < InputFloatCount;
            i++)
        {
            float nativeValue =
                _nativeNchw[i];

            float gpuValue =
                _gpuNchw[i];

            if (
                !IsFinite(nativeValue) ||
                !IsFinite(gpuValue))
            {
                return false;
            }

            double signed =
                (double)gpuValue -
                nativeValue;

            double absNormalized =
                Math.Abs(signed);

            double absLsb =
                absNormalized *
                255.0;

            int channel =
                i /
                PlaneLength;

            channel =
                Mathf.Clamp(
                    channel,
                    0,
                    2);

            _channelSignedSum[channel] +=
                signed;

            _channelAbsSum[channel] +=
                absNormalized;

            _channelSquaredSum[channel] +=
                signed *
                signed;

            if (
                absLsb >
                _channelMaxAbsLsb[channel])
            {
                _channelMaxAbsLsb[channel] =
                    absLsb;
            }

            _globalAbsLsbSum +=
                absLsb;

            _globalSquaredNormalizedSum +=
                signed *
                signed;

            if (
                absLsb >
                _globalMaxAbsLsb)
            {
                _globalMaxAbsLsb =
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

            _globalHistogram[bin]++;
            _pairHistogram[bin]++;

            _comparedValueCount++;

            if (
                BitConverter.SingleToInt32Bits(
                    nativeValue) ==
                BitConverter.SingleToInt32Bits(
                    gpuValue))
            {
                _exactFloatMatchCount++;
            }

            if (absLsb <= 0.5)
            {
                _withinHalfLsbCount++;
            }

            if (absLsb <= 1.0)
            {
                _withinOneLsbCount++;
            }

            if (absLsb <= 2.0)
            {
                _withinTwoLsbCount++;
            }

            if (absLsb <= 4.0)
            {
                _withinFourLsbCount++;
            }

            if (absLsb <= 8.0)
            {
                _withinEightLsbCount++;
            }
        }

        _pairMeanAbsLsb.Add(
            pairAbsLsbSum /
            InputFloatCount);

        _pairP95AbsLsb.Add(
            HistogramPercentile(
                _pairHistogram,
                InputFloatCount,
                0.95));

        _pairP99AbsLsb.Add(
            HistogramPercentile(
                _pairHistogram,
                InputFloatCount,
                0.99));

        _pairMaxAbsLsb.Add(
            pairMaxLsb);

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
            "[Kiwi v44.55.1 Preprocess] MEASURE_START " +
            "observerOnly=1" +
            " gpuInput=ACTUAL_PRODUCTION_LANE_INPUT" +
            " armedNativeSnapshotMatch=1" +
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

            if (trackerField == null)
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
                "[Kiwi v44.55.1 Preprocess] REFLECTION_READY " +
                "tracker=" +
                    trackerType.FullName +
                " laneType=" +
                    laneType.FullName +
                " laneCount=" +
                    lanes.Length +
                " readOnlyFields=7" +
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
            _lanesField == null)
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

    private void FlattenMatrix(
        Matrix4x4 matrix)
    {
        _samplingMatrix[0] = matrix.m00;
        _samplingMatrix[1] = matrix.m01;
        _samplingMatrix[2] = matrix.m02;
        _samplingMatrix[3] = matrix.m03;

        _samplingMatrix[4] = matrix.m10;
        _samplingMatrix[5] = matrix.m11;
        _samplingMatrix[6] = matrix.m12;
        _samplingMatrix[7] = matrix.m13;

        _samplingMatrix[8] = matrix.m20;
        _samplingMatrix[9] = matrix.m21;
        _samplingMatrix[10] = matrix.m22;
        _samplingMatrix[11] = matrix.m23;

        _samplingMatrix[12] = matrix.m30;
        _samplingMatrix[13] = matrix.m31;
        _samplingMatrix[14] = matrix.m32;
        _samplingMatrix[15] = matrix.m33;
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
                "KiwiPreprocessEquivalence_v44_55_1_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture) +
                ".txt");

        List<string> lines =
            new List<string>();

        lines.Add(
            "KiwiAvatarSystem v44.55.1 Production Input / Native CPU Preprocess Equivalence Audit");
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
        lines.Add("cpuCandidate=NATIVE_PATH_B_ARMED_SNAPSHOT_DIRECT_CROP");
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
        lines.Add("laneRaceDiscardCount=" + _laneRaceDiscardCount);
        lines.Add("gpuInputReadbackErrorCount=" + _gpuInputReadbackErrorCount);
        lines.Add("inputShapeMismatchCount=" + _inputShapeMismatchCount);
        lines.Add("nonFinitePairCount=" + _nonFinitePairCount);
        lines.Add("latestNativeSequence=" + _pendingNativeSequence);
        lines.Add("latestSourceHostTicks=" + _pendingSourceHostTicks);
        lines.Add("");

        AppendDoubleStats(
            lines,
            "nativeExactCropCpuMs",
            _nativeExactCropCpuMs);

        AppendDoubleStats(
            lines,
            "nativeExactCropWallMs",
            _nativeExactCropWallMs);

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

        lines.Add("");
        lines.Add("[PREPROCESS_EQUIVALENCE]");

        lines.Add(
            "comparedValueCount=" +
            _comparedValueCount);

        lines.Add(
            "globalMeanAbsLsb=" +
            (
                _comparedValueCount > 0
                    ? _globalAbsLsbSum /
                        _comparedValueCount
                    : 0.0
            ).ToString(
                "F9",
                CultureInfo.InvariantCulture));

        double globalMse =
            _comparedValueCount > 0
                ? _globalSquaredNormalizedSum /
                    _comparedValueCount
                : 0.0;

        lines.Add(
            "globalRmseNormalized=" +
            Math.Sqrt(
                globalMse).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "globalRmseLsb=" +
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
            "globalPsnrDb=" +
            psnr.ToString(
                "F6",
                CultureInfo.InvariantCulture));

        lines.Add(
            "globalP95AbsLsbApprox=" +
            HistogramPercentile(
                _globalHistogram,
                _comparedValueCount,
                0.95).ToString(
                    "F6",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "globalP99AbsLsbApprox=" +
            HistogramPercentile(
                _globalHistogram,
                _comparedValueCount,
                0.99).ToString(
                    "F6",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "globalMaxAbsLsb=" +
            _globalMaxAbsLsb.ToString(
                "F9",
                CultureInfo.InvariantCulture));

        lines.Add(
            "exactFloatMatchRatio=" +
            Ratio(
                _exactFloatMatchCount,
                _comparedValueCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "within0_5LsbRatio=" +
            Ratio(
                _withinHalfLsbCount,
                _comparedValueCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "within1LsbRatio=" +
            Ratio(
                _withinOneLsbCount,
                _comparedValueCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "within2LsbRatio=" +
            Ratio(
                _withinTwoLsbCount,
                _comparedValueCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "within4LsbRatio=" +
            Ratio(
                _withinFourLsbCount,
                _comparedValueCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "within8LsbRatio=" +
            Ratio(
                _withinEightLsbCount,
                _comparedValueCount).ToString(
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
                _pairCompletedCount *
                (long)PlaneLength;

            double signedMean =
                channelCount > 0
                    ? _channelSignedSum[c] /
                        channelCount
                    : 0.0;

            double absMean =
                channelCount > 0
                    ? _channelAbsSum[c] /
                        channelCount
                    : 0.0;

            double mse =
                channelCount > 0
                    ? _channelSquaredSum[c] /
                        channelCount
                    : 0.0;

            lines.Add(
                "channel" +
                channel +
                ".signedBiasLsb=" +
                (
                    signedMean *
                    255.0
                ).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

            lines.Add(
                "channel" +
                channel +
                ".meanAbsLsb=" +
                (
                    absMean *
                    255.0
                ).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

            lines.Add(
                "channel" +
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
                "channel" +
                channel +
                ".maxAbsLsb=" +
                _channelMaxAbsLsb[c].ToString(
                    "F9",
                    CultureInfo.InvariantCulture));
        }

        AppendDoubleStats(
            lines,
            "pairMeanAbsLsb",
            _pairMeanAbsLsb);

        AppendDoubleStats(
            lines,
            "pairP95AbsLsbApprox",
            _pairP95AbsLsb);

        AppendDoubleStats(
            lines,
            "pairP99AbsLsbApprox",
            _pairP99AbsLsb);

        AppendDoubleStats(
            lines,
            "pairMaxAbsLsb",
            _pairMaxAbsLsb);

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
            "STRONG preprocess parity: >=50 matched pairs; errors/nonfinite=0; " +
            "snapshot match timeout ratio <=20%; globalMeanAbsLsb <=1.0; globalP95 <=2 LSB; " +
            "globalP99 <=4 LSB; >=99% values <=4 LSB; each channel bias <=0.5 LSB; " +
            "PSNR >=42 dB; no Production camera/render/safety regression.");
        lines.Add(
            "CONDITIONAL: mean <=2 LSB; p95 <=4 LSB; >=99% <=8 LSB; PSNR >=36 dB. " +
            "Requires same-backend semantic output A/B before Production CPU adoption.");
        lines.Add(
            "REJECT direct Native CPU preprocessing if larger/systematic color or geometry " +
            "difference is observed.");
        lines.Add(
            "Even STRONG does not switch Production. Final low-rate CPU Production " +
            "shadow/publish A/B remains a separate gate.");

        File.WriteAllLines(
            path,
            lines);

        Debug.Log(
            "[Kiwi v44.55.1 Preprocess] " +
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
