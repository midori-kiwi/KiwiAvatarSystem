using System;
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
/// KiwiAvatarSystem v44.54
/// Matched Input / CPU-vs-GPU Backend Output Equivalence Audit.
///
/// One Native Path-B CPU NV12 crop is generated per diagnostic pair.
/// The exact same managed NCHW float[] snapshot is then uploaded to:
///   A) a separate BackendType.CPU shadow worker
///   B) a separate BackendType.GPUCompute shadow worker
///
/// Both use the same KiwiFaceLandmarkInference model and the same packed
/// 468*XYZ + presence-logit output graph. Neither publishes tracking data.
///
/// Production Face Landmarker, Production KiwiInferenceFaceTracker, ROI,
/// thresholds, authority, camera presentation, Spout and render settings are
/// read-only / untouched.
/// </summary>
internal sealed class KiwiMatchedBackendEquivalenceAuditV44_54 : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_54_MATCHED_INPUT_CPU_GPU_BACKEND_OUTPUT_EQUIVALENCE";

    private const string EnableVariable =
        "KIWI_V44_54_BACKEND_EQUIVALENCE_AUDIT";

    private const string DurationVariable =
        "KIWI_V44_54_BACKEND_EQUIVALENCE_SECONDS";

    private const string SampleHzVariable =
        "KIWI_V44_54_BACKEND_EQUIVALENCE_HZ";

    private const string StableSecondsVariable =
        "KIWI_V44_54_STABLE_SECONDS";

    private const string ExpectedTrianglesVariable =
        "KIWI_V44_54_EXPECTED_TRIANGLES";

    private const int InputSize = 192;
    private const int BaseLandmarkCount = 468;
    private const int PackedOutputLength =
        BaseLandmarkCount * 3 + 1;

    private const string LandmarkOutputName =
        "conv2d_20";

    private const string PresenceOutputName =
        "conv2d_30";

    private const float DefaultDurationSeconds = 30f;
    private const float DefaultSampleHz = 2f;
    private const float DefaultStableSeconds = 8f;
    private const int DefaultExpectedTriangles = 254296;
    private const int WarmupPairCount = 3;

    private static bool _installed;

    private Worker _cpuWorker;
    private Worker _gpuWorker;

    private Tensor<float> _cpuInput;
    private Tensor<float> _gpuInput;

    private readonly float[] _samplingMatrix =
        new float[16];

    private readonly float[] _inputNchw =
        new float[
            3 *
            InputSize *
            InputSize];

    private readonly float[] _cpuOutput =
        new float[PackedOutputLength];

    private readonly float[] _gpuOutput =
        new float[PackedOutputLength];

    private MonoBehaviour _runner;
    private Type _runnerType;
    private object _tracker;
    private Type _trackerType;

    private FieldInfo _trackerField;
    private FieldInfo _flipHorizontalField;
    private FieldInfo _flipVerticalField;
    private MethodInfo _buildCropMatrixMethod;

    private bool _workersReady;
    private bool _pairPending;
    private bool _cpuDone;
    private bool _gpuDone;
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

    private long _pairStartTicks;
    private long _cpuScheduleDoneTicks;
    private long _gpuScheduleDoneTicks;
    private int _pairStartFrame;
    private int _cpuDoneFrame;
    private int _gpuDoneFrame;
    private long _pairSourceHostTicks;
    private ulong _pairSourceSequence;
    private Matrix4x4 _pairCropMatrix =
        Matrix4x4.identity;

    private int _pairRequestCount;
    private int _pairCompletedCount;
    private int _pairErrorCount;
    private int _nativeCropUnavailableCount;
    private int _cpuNonFinitePairCount;
    private int _gpuNonFinitePairCount;
    private int _pairNonFiniteCount;
    private int _presenceDecisionMismatchCount;
    private int _presenceFarThresholdMismatchCount;
    private int _presenceNearThresholdPairCount;
    private long _landmarkCoordinateCount;
    private long _exactCoordinateMatchCount;
    private long _landmark2dCount;
    private long _within025PxCount;
    private long _within050PxCount;
    private long _within100PxCount;
    private long _within200PxCount;

    private readonly List<double> _nativeCropCpuMs =
        new List<double>(128);

    private readonly List<double> _nativeCropCallWallMs =
        new List<double>(128);

    private readonly List<double> _cpuInputUploadMs =
        new List<double>(128);

    private readonly List<double> _gpuInputUploadMs =
        new List<double>(128);

    private readonly List<double> _cpuScheduleCpuMs =
        new List<double>(128);

    private readonly List<double> _gpuScheduleCpuMs =
        new List<double>(128);

    private readonly List<double> _cpuServiceMs =
        new List<double>(128);

    private readonly List<double> _gpuServiceMs =
        new List<double>(128);

    private readonly List<double> _pairServiceMs =
        new List<double>(128);

    private readonly List<int> _cpuCompletionFrames =
        new List<int>(128);

    private readonly List<int> _gpuCompletionFrames =
        new List<int>(128);

    private readonly List<int> _pairCompletionFrames =
        new List<int>(128);

    private readonly List<double> _sourceAgeAtIssueMs =
        new List<double>(128);

    private readonly List<double> _sourceAgeAtPairCompletionMs =
        new List<double>(128);

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

    private readonly List<float> _productionLatencySamples =
        new List<float>(4096);

    private readonly List<int> _productionActiveLanes =
        new List<int>(4096);

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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
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
                "[Kiwi] v44.54 Backend Equivalence Audit");

        DontDestroyOnLoad(go);
        go.hideFlags =
            HideFlags.DontSave;

        go.AddComponent<
            KiwiMatchedBackendEquivalenceAuditV44_54>();
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
            "[Kiwi v44.54 Equivalence] WAIT_GATE " +
            "contract=" + Contract +
            " observerOnly=1" +
            " productionBackendChange=0" +
            " productionTrackerWrites=0" +
            " productionRoiWrites=0" +
            " nativeCameraChange=0" +
            " matchedInput=ONE_NATIVE_NCHW_FLOAT_ARRAY" +
            " cpuShadowBackend=CPU" +
            " gpuShadowBackend=GPUCompute" +
            " gpuShadowQueue=DEFAULT_WORKER_SCHEDULE" +
            " sampleHz=" +
                _sampleHz.ToString(
                    "F2",
                    CultureInfo.InvariantCulture) +
            " stableSeconds=" +
                _stableSeconds.ToString(
                    "F1",
                    CultureInfo.InvariantCulture) +
            " expectedTriangles=" +
                _expectedTriangles);
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

        if (!_workersReady)
        {
            UpdateGate(now);
            return;
        }

        SampleProductionTelemetry();

        if (!_measuring)
        {
            if (
                !_pairPending &&
                _warmupPairsRemaining > 0)
            {
                IssueMatchedPair(
                    isWarmup: true);
            }

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

        if (
            !_pairPending &&
            now >= _nextPairAt)
        {
            IssueMatchedPair(
                isWarmup: false);

            _nextPairAt =
                now +
                1.0 /
                _sampleHz;
        }
    }

    private void UpdateGate(
        double now)
    {
        bool nativeReady =
            KiwiNativeCameraInterop.IsRunning &&
            KiwiNativeCameraInterop.SystemMemoryCaptureEnabled &&
            KiwiNativeCameraInterop.CaptureTransportId == 1;

        bool ready =
            CountEnabledSkinnedTriangles() ==
                _expectedTriangles &&
            RuntimeReflectionReady() &&
            TrackerHasRegion() &&
            nativeReady;

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
                    "[Kiwi v44.54 Equivalence] GATE_MATCH " +
                    "triangles=" +
                        _expectedTriangles +
                    " trackerReady=1" +
                    " nativePathB=1" +
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
                "[Kiwi v44.54 Equivalence] INIT_FAIL " +
                exception.GetType().Name +
                " " +
                exception.Message);

            WriteReport("INIT_FAIL");
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

        _gpuWorker =
            new Worker(
                gpuPacked,
                BackendType.GPUCompute);

        _cpuInput =
            new Tensor<float>(
                new TensorShape(
                    1,
                    3,
                    InputSize,
                    InputSize),
                _inputNchw);

        _gpuInput =
            new Tensor<float>(
                new TensorShape(
                    1,
                    3,
                    InputSize,
                    InputSize));

        // Explicitly pin the GPU comparison input so Upload() writes the same
        // host float snapshot directly into GPUCompute tensor storage.
        ComputeTensorData.Pin(
            _gpuInput);

        _workersReady = true;

        Debug.Log(
            "[Kiwi v44.54 Equivalence] WORKERS_READY " +
            "cpu=CPU" +
            " gpu=GPUCompute" +
            " sameModel=KiwiFaceLandmarkInference" +
            " samePackedOutputs=1405" +
            " sameHostInputArray=1" +
            " gpuInputPinned=1" +
            " productionWorkerTouched=0");
    }

    private void IssueMatchedPair(
        bool isWarmup)
    {
        if (
            !_workersReady ||
            _pairPending ||
            _cpuWorker == null ||
            _gpuWorker == null ||
            _cpuInput == null ||
            _gpuInput == null)
        {
            return;
        }

        if (
            !RuntimeReflectionReady() ||
            !TrackerHasRegion())
        {
            return;
        }

        Matrix4x4 cropMatrix;
        Matrix4x4 samplingMatrix;

        try
        {
            cropMatrix =
                GetCropMatrix();

            samplingMatrix =
                BuildFlipMatrix(
                    GetFlip(
                        _flipHorizontalField),
                    GetFlip(
                        _flipVerticalField)) *
                cropMatrix;
        }
        catch
        {
            _pairErrorCount++;
            return;
        }

        FlattenMatrix(
            samplingMatrix);

        long pairStart =
            Stopwatch.GetTimestamp();

        long nativeBegin =
            pairStart;

        bool copied =
            KiwiNativeCameraInterop
                .TryCopyLatestCpuCropNchwFloat(
                    _samplingMatrix,
                    InputSize,
                    _inputNchw,
                    out ulong sourceSequence,
                    out long sourceHostTicks,
                    out ulong nativeCpuMicroseconds);

        long afterNative =
            Stopwatch.GetTimestamp();

        if (!copied)
        {
            _nativeCropUnavailableCount++;
            return;
        }

        long cpuUploadBegin =
            Stopwatch.GetTimestamp();

        _cpuInput.Upload(
            _inputNchw);

        long afterCpuUpload =
            Stopwatch.GetTimestamp();

        long gpuUploadBegin =
            afterCpuUpload;

        _gpuInput.Upload(
            _inputNchw);

        long afterGpuUpload =
            Stopwatch.GetTimestamp();

        try
        {
            long cpuScheduleBegin =
                afterGpuUpload;

            _cpuWorker.Schedule(
                _cpuInput);

            Tensor<float> cpuTensor =
                _cpuWorker.PeekOutput(0)
                as Tensor<float>;

            long afterCpuSchedule =
                Stopwatch.GetTimestamp();

            long gpuScheduleBegin =
                afterCpuSchedule;

            _gpuWorker.Schedule(
                _gpuInput);

            Tensor<float> gpuTensor =
                _gpuWorker.PeekOutput(0)
                as Tensor<float>;

            long afterGpuSchedule =
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
                    "Matched packed output invalid.");
            }

            _pairPending = true;
            _cpuDone = false;
            _gpuDone = false;

            _pairStartTicks =
                pairStart;

            _cpuScheduleDoneTicks =
                afterCpuSchedule;

            _gpuScheduleDoneTicks =
                afterGpuSchedule;

            _pairStartFrame =
                Time.frameCount;

            _cpuDoneFrame = -1;
            _gpuDoneFrame = -1;

            _pairSourceHostTicks =
                sourceHostTicks;

            _pairSourceSequence =
                sourceSequence;

            _pairCropMatrix =
                cropMatrix;

            if (!isWarmup)
            {
                _pairRequestCount++;

                _nativeCropCallWallMs.Add(
                    TicksToMilliseconds(
                        afterNative -
                        nativeBegin));

                _nativeCropCpuMs.Add(
                    nativeCpuMicroseconds /
                    1000.0);

                _cpuInputUploadMs.Add(
                    TicksToMilliseconds(
                        afterCpuUpload -
                        cpuUploadBegin));

                _gpuInputUploadMs.Add(
                    TicksToMilliseconds(
                        afterGpuUpload -
                        gpuUploadBegin));

                _cpuScheduleCpuMs.Add(
                    TicksToMilliseconds(
                        afterCpuSchedule -
                        cpuScheduleBegin));

                _gpuScheduleCpuMs.Add(
                    TicksToMilliseconds(
                        afterGpuSchedule -
                        gpuScheduleBegin));

                _sourceAgeAtIssueMs.Add(
                    QpcAgeMilliseconds(
                        sourceHostTicks));
            }

            BeginCpuReadback(
                cpuTensor,
                isWarmup);

            BeginGpuReadback(
                gpuTensor,
                isWarmup);
        }
        catch (Exception exception)
        {
            _pairPending = false;
            _cpuDone = false;
            _gpuDone = false;
            _pairErrorCount++;

            Debug.LogWarning(
                "[Kiwi v44.54 Equivalence] REQUEST_ERROR " +
                exception.GetType().Name +
                " " +
                exception.Message);

            if (isWarmup)
            {
                WriteReport("WARMUP_FAIL");
            }
        }
    }

    private void BeginCpuReadback(
        Tensor<float> output,
        bool isWarmup)
    {
        var awaiter =
            output
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

                    long done =
                        Stopwatch.GetTimestamp();

                    CopyReadableOutput(
                        readable,
                        _cpuOutput);

                    _cpuDoneFrame =
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
                                _cpuDoneFrame -
                                _pairStartFrame));
                    }

                    _cpuDone = true;

                    TryFinalizePair(
                        isWarmup);
                }
                catch (Exception exception)
                {
                    _pairErrorCount++;

                    Debug.LogWarning(
                        "[Kiwi v44.54 Equivalence] CPU_COMPLETE_ERROR " +
                        exception.GetType().Name);

                    AbortPair(
                        isWarmup);
                }
                finally
                {
                    if (readable != null)
                    {
                        readable.Dispose();
                    }
                }
            });
    }

    private void BeginGpuReadback(
        Tensor<float> output,
        bool isWarmup)
    {
        var awaiter =
            output
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

                    long done =
                        Stopwatch.GetTimestamp();

                    CopyReadableOutput(
                        readable,
                        _gpuOutput);

                    _gpuDoneFrame =
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
                                _gpuDoneFrame -
                                _pairStartFrame));
                    }

                    _gpuDone = true;

                    TryFinalizePair(
                        isWarmup);
                }
                catch (Exception exception)
                {
                    _pairErrorCount++;

                    Debug.LogWarning(
                        "[Kiwi v44.54 Equivalence] GPU_COMPLETE_ERROR " +
                        exception.GetType().Name);

                    AbortPair(
                        isWarmup);
                }
                finally
                {
                    if (readable != null)
                    {
                        readable.Dispose();
                    }
                }
            });
    }

    private void TryFinalizePair(
        bool isWarmup)
    {
        if (
            !_pairPending ||
            !_cpuDone ||
            !_gpuDone)
        {
            return;
        }

        long done =
            Stopwatch.GetTimestamp();

        if (!isWarmup)
        {
            CompareOutputs();

            _pairServiceMs.Add(
                TicksToMilliseconds(
                    done -
                    _pairStartTicks));

            _pairCompletionFrames.Add(
                Mathf.Max(
                    0,
                    Mathf.Max(
                        _cpuDoneFrame,
                        _gpuDoneFrame) -
                    _pairStartFrame));

            _sourceAgeAtPairCompletionMs.Add(
                QpcAgeMilliseconds(
                    _pairSourceHostTicks));

            _pairCompletedCount++;
        }

        _pairPending = false;
        _cpuDone = false;
        _gpuDone = false;

        if (isWarmup)
        {
            _warmupPairsRemaining--;

            if (
                _warmupPairsRemaining >
                0)
            {
                IssueMatchedPair(
                    isWarmup: true);
            }
            else
            {
                BeginMeasurement();
            }
        }
    }

    private void AbortPair(
        bool isWarmup)
    {
        _pairPending = false;
        _cpuDone = false;
        _gpuDone = false;

        WriteReport(
            isWarmup
                ? "WARMUP_FAIL"
                : "PAIR_FAIL");
    }

    private void CompareOutputs()
    {
        bool cpuFinite =
            true;

        bool gpuFinite =
            true;

        List<double> pair2d =
            new List<double>(
                BaseLandmarkCount);

        double pair2dSum =
            0.0;

        double pair2dMax =
            0.0;

        for (
            int i = 0;
            i < BaseLandmarkCount;
            i++)
        {
            int baseIndex =
                i * 3;

            float cpuX =
                _cpuOutput[
                    baseIndex];

            float cpuY =
                _cpuOutput[
                    baseIndex + 1];

            float cpuZ =
                _cpuOutput[
                    baseIndex + 2];

            float gpuX =
                _gpuOutput[
                    baseIndex];

            float gpuY =
                _gpuOutput[
                    baseIndex + 1];

            float gpuZ =
                _gpuOutput[
                    baseIndex + 2];

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

            _landmark2dPixelDiff.Add(
                d2);

            pair2d.Add(
                d2);

            pair2dSum +=
                d2;

            if (d2 > pair2dMax)
            {
                pair2dMax =
                    d2;
            }

            _landmark2dCount++;

            if (d2 <= 0.25)
            {
                _within025PxCount++;
            }

            if (d2 <= 0.50)
            {
                _within050PxCount++;
            }

            if (d2 <= 1.00)
            {
                _within100PxCount++;
            }

            if (d2 <= 2.00)
            {
                _within200PxCount++;
            }

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
            IsFinite(
                cpuRawPresence);

        gpuFinite &=
            IsFinite(
                gpuRawPresence);

        if (!cpuFinite)
        {
            _cpuNonFinitePairCount++;
        }

        if (!gpuFinite)
        {
            _gpuNonFinitePairCount++;
        }

        if (
            !cpuFinite ||
            !gpuFinite)
        {
            _pairNonFiniteCount++;
            return;
        }

        if (pair2d.Count > 0)
        {
            double[] sorted =
                pair2d.ToArray();

            Array.Sort(sorted);

            _landmark2dMeanPerPair.Add(
                pair2dSum /
                pair2d.Count);

            _landmark2dP95PerPair.Add(
                Percentile(
                    sorted,
                    0.95));

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

    private void BeginMeasurement()
    {
        _measuring = true;

        _measurementStart =
            Time.realtimeSinceStartupAsDouble;

        _nextPairAt =
            _measurementStart;

        CaptureStartCounters();

        Debug.Log(
            "[Kiwi v44.54 Equivalence] MEASURE_START " +
            "observerOnly=1" +
            " matchedInput=1" +
            " cpuBackend=CPU" +
            " gpuBackend=GPUCompute" +
            " sampleHz=" +
                _sampleHz.ToString(
                    "F2",
                    CultureInfo.InvariantCulture) +
            " durationSeconds=" +
                _durationSeconds.ToString(
                    "F1",
                    CultureInfo.InvariantCulture) +
            " warmupComplete=1" +
            " productionBackendChange=0");
    }

    private void DiscoverRuntimeObjects(
        double now)
    {
        if (
            RuntimeReflectionReady() ||
            now < _nextDiscoveryAt)
        {
            return;
        }

        _nextDiscoveryAt =
            now + 0.5;

        MonoBehaviour[] behaviours =
            FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

        BindingFlags flags =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic;

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

            Type type =
                behaviour.GetType();

            FieldInfo trackerField =
                type.GetField(
                    "_sentisTracker",
                    flags);

            FieldInfo flipHField =
                type.GetField(
                    "_sentisFlipHorizontally",
                    flags);

            FieldInfo flipVField =
                type.GetField(
                    "_sentisFlipVertically",
                    flags);

            if (
                trackerField == null ||
                flipHField == null ||
                flipVField == null)
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

            MethodInfo cropMethod =
                trackerType.GetMethod(
                    "BuildCropMatrix",
                    flags);

            if (
                cropMethod == null ||
                cropMethod.ReturnType !=
                    typeof(Matrix4x4) ||
                cropMethod.GetParameters().Length !=
                    0)
            {
                continue;
            }

            _runner = behaviour;
            _runnerType = type;
            _tracker = tracker;
            _trackerType = trackerType;

            _trackerField =
                trackerField;

            _flipHorizontalField =
                flipHField;

            _flipVerticalField =
                flipVField;

            _buildCropMatrixMethod =
                cropMethod;

            Debug.Log(
                "[Kiwi v44.54 Equivalence] REFLECTION_READY " +
                "runner=" +
                    _runnerType.FullName +
                " tracker=" +
                    _trackerType.FullName +
                " readOnlyFields=3" +
                " readOnlyMethod=BuildCropMatrix" +
                " SetValueCalls=0");

            return;
        }
    }

    private bool RuntimeReflectionReady()
    {
        if (
            _runner == null ||
            _tracker == null ||
            _trackerField == null ||
            _flipHorizontalField == null ||
            _flipVerticalField == null ||
            _buildCropMatrixMethod == null)
        {
            return false;
        }

        try
        {
            object current =
                _trackerField.GetValue(
                    _runner);

            if (
                current == null ||
                !ReferenceEquals(
                    current,
                    _tracker))
            {
                ClearReflection();
                return false;
            }
        }
        catch
        {
            ClearReflection();
            return false;
        }

        return true;
    }

    private void ClearReflection()
    {
        _runner = null;
        _runnerType = null;
        _tracker = null;
        _trackerType = null;
        _trackerField = null;
        _flipHorizontalField = null;
        _flipVerticalField = null;
        _buildCropMatrixMethod = null;
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

    private bool GetFlip(
        FieldInfo field)
    {
        if (
            _runner == null ||
            field == null)
        {
            return false;
        }

        try
        {
            object value =
                field.GetValue(
                    _runner);

            return
                value is bool flag &&
                flag;
        }
        catch
        {
            return false;
        }
    }

    private Matrix4x4 GetCropMatrix()
    {
        object value =
            _buildCropMatrixMethod.Invoke(
                _tracker,
                null);

        if (!(value is Matrix4x4 crop))
        {
            throw new InvalidOperationException(
                "BuildCropMatrix returned invalid value.");
        }

        return crop;
    }

    private static Matrix4x4 BuildFlipMatrix(
        bool horizontal,
        bool vertical)
    {
        Matrix4x4 matrix =
            Matrix4x4.identity;

        if (horizontal)
        {
            matrix =
                Matrix4x4.Translate(
                    new Vector3(
                        1f,
                        0f,
                        0f)) *
                Matrix4x4.Scale(
                    new Vector3(
                        -1f,
                        1f,
                        1f)) *
                matrix;
        }

        if (vertical)
        {
            matrix =
                Matrix4x4.Translate(
                    new Vector3(
                        0f,
                        1f,
                        0f)) *
                Matrix4x4.Scale(
                    new Vector3(
                        1f,
                        -1f,
                        1f)) *
                matrix;
        }

        return matrix;
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
            _productionLatencySamples.Add(
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
                "KiwiBackendEquivalence_v44_54_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture) +
                ".txt");

        List<string> lines =
            new List<string>();

        lines.Add(
            "KiwiAvatarSystem v44.54 Matched Input CPU/GPU Backend Output Equivalence Audit");
        lines.Add("contract=" + Contract);
        lines.Add("status=" + status);
        lines.Add("observerOnly=1");
        lines.Add("productionBackendChange=0");
        lines.Add("productionTrackerWrites=0");
        lines.Add("productionRoiWrites=0");
        lines.Add("nativeCameraChange=0");
        lines.Add("cpuShadowBackend=CPU");
        lines.Add("gpuShadowBackend=GPUCompute");
        lines.Add("gpuShadowQueue=DEFAULT_WORKER_SCHEDULE");
        lines.Add("sameModel=KiwiFaceLandmarkInference");
        lines.Add("samePackedOutputLength=" + PackedOutputLength);
        lines.Add("sameHostInputArray=1");
        lines.Add("gpuInputPinned=1");
        lines.Add("inputSource=NATIVE_PATH_B_CPU_NV12_STABLE_SLOT_DIRECT_CROP");
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
        lines.Add("pairRequestCount=" + _pairRequestCount);
        lines.Add("pairCompletedCount=" + _pairCompletedCount);
        lines.Add("pairErrorCount=" + _pairErrorCount);
        lines.Add(
            "nativeCropUnavailableCount=" +
            _nativeCropUnavailableCount);
        lines.Add(
            "cpuNonFinitePairCount=" +
            _cpuNonFinitePairCount);
        lines.Add(
            "gpuNonFinitePairCount=" +
            _gpuNonFinitePairCount);
        lines.Add(
            "pairNonFiniteCount=" +
            _pairNonFiniteCount);
        lines.Add(
            "presenceDecisionMismatchCount=" +
            _presenceDecisionMismatchCount);
        lines.Add(
            "presenceFarThresholdMismatchCount=" +
            _presenceFarThresholdMismatchCount);
        lines.Add(
            "presenceNearThresholdPairCount=" +
            _presenceNearThresholdPairCount);
        lines.Add(
            "sourceSequenceLatest=" +
            _pairSourceSequence);
        lines.Add("");

        AppendDoubleStats(
            lines,
            "nativeCropCallWallMs",
            _nativeCropCallWallMs);

        AppendDoubleStats(
            lines,
            "nativeCropCpuMs",
            _nativeCropCpuMs);

        AppendDoubleStats(
            lines,
            "cpuInputUploadMs",
            _cpuInputUploadMs);

        AppendDoubleStats(
            lines,
            "gpuInputUploadMs",
            _gpuInputUploadMs);

        AppendDoubleStats(
            lines,
            "cpuScheduleCpuMs",
            _cpuScheduleCpuMs);

        AppendDoubleStats(
            lines,
            "gpuScheduleCpuMs",
            _gpuScheduleCpuMs);

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
            "pairEndToEndServiceMs",
            _pairServiceMs);

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
            "pairCompletionFrames",
            _pairCompletionFrames);

        AppendDoubleStats(
            lines,
            "sourceAgeAtIssueMs",
            _sourceAgeAtIssueMs);

        AppendDoubleStats(
            lines,
            "sourceAgeAtPairCompletionMs",
            _sourceAgeAtPairCompletionMs);

        lines.Add("");
        lines.Add("[NUMERICAL_EQUIVALENCE]");

        AppendDoubleStats(
            lines,
            "landmarkXAbsRawPx",
            _landmarkXAbsRaw);

        AppendDoubleStats(
            lines,
            "landmarkYAbsRawPx",
            _landmarkYAbsRaw);

        AppendDoubleStats(
            lines,
            "landmarkZAbsRawPx",
            _landmarkZAbsRaw);

        AppendDoubleStats(
            lines,
            "landmark2dPixelDiff",
            _landmark2dPixelDiff);

        AppendDoubleStats(
            lines,
            "landmark2dMeanPerPairPx",
            _landmark2dMeanPerPair);

        AppendDoubleStats(
            lines,
            "landmark2dP95PerPairPx",
            _landmark2dP95PerPair);

        AppendDoubleStats(
            lines,
            "landmark2dMaxPerPairPx",
            _landmark2dMaxPerPair);

        AppendDoubleStats(
            lines,
            "presenceRawLogitAbsDiff",
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
            "landmarkCoordinateCount=" +
            _landmarkCoordinateCount);

        lines.Add(
            "exactCoordinateMatchCount=" +
            _exactCoordinateMatchCount);

        lines.Add(
            "exactCoordinateMatchRatio=" +
            Ratio(
                _exactCoordinateMatchCount,
                _landmarkCoordinateCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "landmark2dCount=" +
            _landmark2dCount);

        lines.Add(
            "landmarkWithin0_25pxRatio=" +
            Ratio(
                _within025PxCount,
                _landmark2dCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "landmarkWithin0_50pxRatio=" +
            Ratio(
                _within050PxCount,
                _landmark2dCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "landmarkWithin1_00pxRatio=" +
            Ratio(
                _within100PxCount,
                _landmark2dCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add(
            "landmarkWithin2_00pxRatio=" +
            Ratio(
                _within200PxCount,
                _landmark2dCount).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));

        lines.Add("");
        lines.Add("[PRODUCTION_OBSERVATION]");

        AppendFloatStats(
            lines,
            "sampledProductionTrackerLatencyMs",
            _productionLatencySamples);

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
            "STRONG numerical equivalence: pair errors/nonfinite=0; " +
            "landmark2dPixelDiff p95 <=0.75 px; >=99% landmarks <=1 px; " +
            "presenceSigmoidAbsDiff p95 <=0.01; far-threshold decision mismatches=0.");
        lines.Add(
            "CONDITIONAL: landmark2dPixelDiff p95 <=1.50 px and presence p95 <=0.03 " +
            "with no visible tracking regression; requires deeper semantic A/B before adoption.");
        lines.Add(
            "REJECT CPU Production switch if differences exceed conditional bounds, " +
            "far-threshold presence decisions disagree, or Production cadence/safety regresses.");
        lines.Add(
            "Even STRONG does not modify Production in this audit. A separate final " +
            "low-rate Production-backend A/B gate is required before adoption.");

        File.WriteAllLines(
            path,
            lines);

        Debug.Log(
            "[Kiwi v44.54 Equivalence] " +
            status +
            " report=" +
            path);
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
                    mesh.GetTopology(s) !=
                    MeshTopology.Triangles)
                {
                    continue;
                }

                total +=
                    (long)
                    mesh.GetIndexCount(s) /
                    3L;
            }
        }

        return
            total > int.MaxValue
                ? int.MaxValue
                : (int)total;
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

        double x =
            value;

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

        lines.Add(name + ".samples=" + data.Length);
        lines.Add(
            name + ".mean=" +
            (sum / data.Length).ToString(
                "F9",
                CultureInfo.InvariantCulture));
        lines.Add(
            name + ".median=" +
            Percentile(
                data,
                0.50).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));
        lines.Add(
            name + ".p95=" +
            Percentile(
                data,
                0.95).ToString(
                    "F9",
                    CultureInfo.InvariantCulture));
        lines.Add(
            name + ".max=" +
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
            (sorted.Length - 1) *
            percentile;

        int lower =
            Mathf.Clamp(
                (int)Math.Floor(position),
                0,
                sorted.Length - 1);

        int upper =
            Mathf.Clamp(
                (int)Math.Ceiling(position),
                0,
                sorted.Length - 1);

        if (lower == upper)
        {
            return sorted[lower];
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

    private void OnApplicationQuit()
    {
        if (
            _measuring &&
            !_reportWritten)
        {
            WriteReport("QUIT");
        }
    }

    private void OnDestroy()
    {
        if (_cpuWorker != null)
        {
            _cpuWorker.Dispose();
            _cpuWorker = null;
        }

        if (_gpuWorker != null)
        {
            _gpuWorker.Dispose();
            _gpuWorker = null;
        }

        if (_cpuInput != null)
        {
            _cpuInput.Dispose();
            _cpuInput = null;
        }

        if (_gpuInput != null)
        {
            _gpuInput.Dispose();
            _gpuInput = null;
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
