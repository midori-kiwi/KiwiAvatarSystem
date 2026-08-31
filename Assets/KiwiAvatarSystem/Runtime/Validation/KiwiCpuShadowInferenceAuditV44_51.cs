using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Unity.InferenceEngine;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// KiwiAvatarSystem v44.51
/// Observer-only CPU Shadow Inference Audit.
///
/// Runs the same KiwiFaceLandmarkInference model on a separate BackendType.CPU
/// worker at low frequency. The Production tracker is never modified, paused,
/// queried for outputs, or used as the shadow worker.
///
/// Purpose:
/// - determine whether the current ~3-frame GPU readback floor can be avoided
///   by a CPU backend for this small 192x192 model;
/// - measure CPU shadow schedule->async-readable service time and frame delta;
/// - measure Production tracker counters through read-only reflection.
///
/// This is NOT a Production backend switch.
/// </summary>
internal sealed class KiwiCpuShadowInferenceAuditV44_51 : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_51_CPU_SHADOW_INFERENCE_OBSERVER";

    private const string EnableVariable =
        "KIWI_V44_51_CPU_SHADOW_AUDIT";

    private const string DurationVariable =
        "KIWI_V44_51_CPU_SHADOW_AUDIT_SECONDS";

    private const string SampleHzVariable =
        "KIWI_V44_51_CPU_SHADOW_HZ";

    private const string ExpectedTrianglesVariable =
        "KIWI_V44_51_EXPECTED_TRIANGLES";

    private const int InputSize = 192;
    private const int BaseLandmarkCount = 468;
    private const int PackedOutputLength = BaseLandmarkCount * 3 + 1;
    private const string LandmarkOutputName = "conv2d_20";
    private const string PresenceOutputName = "conv2d_30";

    private const float DefaultDurationSeconds = 30f;
    private const float DefaultSampleHz = 2f;
    private const int DefaultExpectedTriangles = 254296;
    private const int WarmupCount = 3;

    private Worker _cpuWorker;
    private Tensor<float> _cpuInput;
    private Tensor<float> _pendingOutput;

    private bool _installedWorker;
    private bool _shadowPending;
    private bool _measuring;
    private bool _reportWritten;
    private bool _gateMatched;

    private int _warmupRemaining = WarmupCount;
    private int _expectedTriangles;
    private float _durationSeconds;
    private float _sampleHz;

    private long _shadowStartedTicks;
    private int _shadowStartedFrame;
    private double _measurementStartedRealtime;
    private double _nextShadowAt;

    private int _requestCount;
    private int _completedCount;
    private int _errorCount;

    private readonly List<double> _shadowServiceMs =
        new List<double>(128);

    private readonly List<double> _shadowScheduleCpuMs =
        new List<double>(128);

    private readonly List<int> _shadowFrameDelta =
        new List<int>(128);

    private readonly List<float> _productionLatencyMs =
        new List<float>(4096);

    private readonly List<int> _productionActiveLanes =
        new List<int>(4096);

    private readonly List<int> _productionLaneLimit =
        new List<int>(4096);

    private object _tracker;
    private Type _trackerType;
    private double _nextTrackerDiscoveryAt;

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

    private static bool _installed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed)
        {
            return;
        }

        if (!ReadBoolEnvironment(EnableVariable, false))
        {
            return;
        }

        _installed = true;

        GameObject go = new GameObject(
            "[Kiwi] v44.51 CPU Shadow Inference Audit");

        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.DontSave;
        go.AddComponent<KiwiCpuShadowInferenceAuditV44_51>();
    }

    private void Awake()
    {
        _durationSeconds = Mathf.Clamp(
            ReadFloatEnvironment(
                DurationVariable,
                DefaultDurationSeconds),
            10f,
            180f);

        _sampleHz = Mathf.Clamp(
            ReadFloatEnvironment(
                SampleHzVariable,
                DefaultSampleHz),
            0.5f,
            5f);

        _expectedTriangles = Mathf.Max(
            1,
            ReadIntEnvironment(
                ExpectedTrianglesVariable,
                DefaultExpectedTriangles));

        Debug.Log(
            "[Kiwi v44.51 CPU Shadow Audit] WAIT_GATE " +
            "contract=" + Contract +
            " observerOnly=1" +
            " productionBackendChange=0" +
            " productionTrackerWrites=0" +
            " shadowBackend=CPU" +
            " shadowInput=FIXED_ZERO_1x3x192x192" +
            " shadowHz=" +
                _sampleHz.ToString("F2", CultureInfo.InvariantCulture) +
            " warmups=" + WarmupCount +
            " expectedTriangles=" + _expectedTriangles);
    }

    private void Update()
    {
        if (_reportWritten)
        {
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;

        DiscoverTrackerIfNeeded(now);

        if (!_gateMatched)
        {
            if (
                CountEnabledSkinnedTriangles() !=
                    _expectedTriangles ||
                _tracker == null ||
                ReadTrackerInt("ScheduledFrameCount", 0) <= 0)
            {
                return;
            }

            _gateMatched = true;

            Debug.Log(
                "[Kiwi v44.51 CPU Shadow Audit] GATE_MATCH " +
                "triangles=" + _expectedTriangles +
                " trackerReady=1");

            try
            {
                InstallShadowWorker();
            }
            catch (Exception exception)
            {
                _errorCount++;

                Debug.LogError(
                    "[Kiwi v44.51 CPU Shadow Audit] INIT_FAIL " +
                    exception.GetType().Name + " " +
                    exception.Message);

                WriteReport("INIT_FAIL");
                return;
            }

            IssueShadowRequest(isWarmup: true);
            return;
        }

        SampleProductionTelemetry();

        if (!_measuring)
        {
            return;
        }

        if (
            now - _measurementStartedRealtime >=
            _durationSeconds)
        {
            if (!_shadowPending)
            {
                CaptureEndCounters();
                WriteReport("COMPLETE");
            }

            return;
        }

        if (
            !_shadowPending &&
            now >= _nextShadowAt)
        {
            IssueShadowRequest(isWarmup: false);

            _nextShadowAt =
                now + 1.0 / _sampleHz;
        }
    }

    private void InstallShadowWorker()
    {
        if (_installedWorker)
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

        Model source = ModelLoader.Load(asset);
        Model packed = BuildSingleReadbackModel(source);

        _cpuWorker = new Worker(
            packed,
            BackendType.CPU);

        float[] zeroInput =
            new float[3 * InputSize * InputSize];

        _cpuInput = new Tensor<float>(
            new TensorShape(
                1,
                3,
                InputSize,
                InputSize),
            zeroInput);

        _installedWorker = true;

        Debug.Log(
            "[Kiwi v44.51 CPU Shadow Audit] WORKER_READY " +
            "backend=CPU" +
            " model=KiwiFaceLandmarkInference" +
            " packedOutputLength=" + PackedOutputLength +
            " inputElements=" + zeroInput.Length +
            " productionWorkerTouched=0");
    }

    private void IssueShadowRequest(bool isWarmup)
    {
        if (
            !_installedWorker ||
            _cpuWorker == null ||
            _cpuInput == null ||
            _shadowPending)
        {
            return;
        }

        long scheduleBegin = Stopwatch.GetTimestamp();

        try
        {
            _cpuWorker.Schedule(_cpuInput);

            _pendingOutput =
                _cpuWorker.PeekOutput(0)
                as Tensor<float>;

            if (
                _pendingOutput == null ||
                _pendingOutput.shape.length !=
                    PackedOutputLength)
            {
                throw new InvalidOperationException(
                    "CPU shadow packed output invalid.");
            }

            long afterSchedule =
                Stopwatch.GetTimestamp();

            double scheduleCpuMs =
                (afterSchedule - scheduleBegin) *
                1000.0 /
                Stopwatch.Frequency;

            if (!isWarmup)
            {
                _shadowScheduleCpuMs.Add(
                    scheduleCpuMs);

                _requestCount++;
            }

            _shadowStartedTicks = afterSchedule;
            _shadowStartedFrame = Time.frameCount;
            _shadowPending = true;

            var awaiter =
                _pendingOutput
                    .ReadbackAndCloneAsync()
                    .GetAwaiter();

            awaiter.OnCompleted(
                () =>
                {
                    Tensor<float> readable = null;

                    try
                    {
                        readable = awaiter.GetResult();

                        long completedTicks =
                            Stopwatch.GetTimestamp();

                        double serviceMs =
                            (
                                completedTicks -
                                _shadowStartedTicks
                            ) *
                            1000.0 /
                            Stopwatch.Frequency;

                        int frameDelta =
                            Mathf.Max(
                                0,
                                Time.frameCount -
                                _shadowStartedFrame);

                        if (!isWarmup)
                        {
                            _shadowServiceMs.Add(serviceMs);
                            _shadowFrameDelta.Add(frameDelta);
                            _completedCount++;
                        }
                    }
                    catch (Exception exception)
                    {
                        _errorCount++;

                        Debug.LogWarning(
                            "[Kiwi v44.51 CPU Shadow Audit] " +
                            "SHADOW_COMPLETE_ERROR " +
                            exception.GetType().Name);
                    }
                    finally
                    {
                        if (readable != null)
                        {
                            readable.Dispose();
                        }

                        _pendingOutput = null;
                        _shadowPending = false;

                        if (isWarmup)
                        {
                            _warmupRemaining--;

                            if (_warmupRemaining > 0)
                            {
                                IssueShadowRequest(
                                    isWarmup: true);
                            }
                            else
                            {
                                BeginMeasurement();
                            }
                        }
                    }
                });
        }
        catch (Exception exception)
        {
            _shadowPending = false;
            _pendingOutput = null;
            _errorCount++;

            Debug.LogError(
                "[Kiwi v44.51 CPU Shadow Audit] " +
                "SHADOW_REQUEST_ERROR " +
                exception.GetType().Name + " " +
                exception.Message);

            if (isWarmup)
            {
                WriteReport("WARMUP_FAIL");
            }
        }
    }

    private void BeginMeasurement()
    {
        _measuring = true;
        _measurementStartedRealtime =
            Time.realtimeSinceStartupAsDouble;

        _nextShadowAt =
            _measurementStartedRealtime;

        CaptureStartCounters();

        Debug.Log(
            "[Kiwi v44.51 CPU Shadow Audit] MEASURE_START " +
            "observerOnly=1" +
            " backend=CPU" +
            " shadowHz=" +
                _sampleHz.ToString("F2", CultureInfo.InvariantCulture) +
            " durationSeconds=" +
                _durationSeconds.ToString("F1", CultureInfo.InvariantCulture) +
            " warmupComplete=1" +
            " productionBackendChange=0");
    }

    private void DiscoverTrackerIfNeeded(double now)
    {
        if (
            _tracker != null ||
            now < _nextTrackerDiscoveryAt)
        {
            return;
        }

        _nextTrackerDiscoveryAt = now + 0.5;

        MonoBehaviour[] behaviours =
            FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour = behaviours[i];

            if (
                behaviour == null ||
                behaviour.GetType().Name !=
                    "FaceLandmarkerRunner")
            {
                continue;
            }

            FieldInfo[] fields =
                behaviour.GetType().GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

            for (int f = 0; f < fields.Length; f++)
            {
                FieldInfo field = fields[f];

                if (
                    field.FieldType.Name !=
                    "KiwiInferenceFaceTracker")
                {
                    continue;
                }

                object value = null;

                try
                {
                    value =
                        field.GetValue(behaviour);
                }
                catch
                {
                    value = null;
                }

                if (value == null)
                {
                    continue;
                }

                _tracker = value;
                _trackerType = value.GetType();

                Debug.Log(
                    "[Kiwi v44.51 CPU Shadow Audit] " +
                    "TRACKER_FOUND readOnlyReflection=1" +
                    " type=" +
                    _trackerType.FullName);

                return;
            }
        }
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
            !float.IsNaN(latency) &&
            !float.IsInfinity(latency))
        {
            _productionLatencyMs.Add(
                latency);
        }

        _productionActiveLanes.Add(
            ReadTrackerInt(
                "ActiveLaneCount",
                0));

        _productionLaneLimit.Add(
            ReadTrackerInt(
                "SchedulingLaneLimit",
                0));
    }

    private void CaptureStartCounters()
    {
        _startScheduled =
            ReadTrackerInt("ScheduledFrameCount", 0);

        _startReadbackCompleted =
            ReadTrackerInt("ReadbackCompletedFrameCount", 0);

        _startCompleted =
            ReadTrackerInt("CompletedFrameCount", 0);

        _startDropped =
            ReadTrackerInt("DroppedFreshFrameCount", 0);

        _startStale =
            ReadTrackerInt("DiscardedStaleFrameCount", 0);
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

    private object ReadTrackerProperty(string propertyName)
    {
        if (_tracker == null || _trackerType == null)
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

            return property != null
                ? property.GetValue(_tracker)
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
            ReadTrackerProperty(propertyName);

        return value is int integer
            ? integer
            : fallback;
    }

    private float ReadTrackerFloat(
        string propertyName,
        float fallback)
    {
        object value =
            ReadTrackerProperty(propertyName);

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

    private int CountEnabledSkinnedTriangles()
    {
        SkinnedMeshRenderer[] renderers =
            FindObjectsByType<SkinnedMeshRenderer>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

        long total = 0L;

        for (int i = 0; i < renderers.Length; i++)
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
                    (long)mesh.GetIndexCount(s) /
                    3L;
            }
        }

        return total > int.MaxValue
            ? int.MaxValue
            : (int)total;
    }

    private void WriteReport(string status)
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
                            _measurementStartedRealtime,
                        _durationSeconds))
                : 0.0;

        string directory =
            Path.Combine(
                Application.persistentDataPath,
                "KiwiFrameBottleneck");

        Directory.CreateDirectory(directory);

        string path =
            Path.Combine(
                directory,
                "KiwiCpuShadowInference_v44_51_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture) +
                ".txt");

        List<string> lines =
            new List<string>();

        lines.Add(
            "KiwiAvatarSystem v44.51 CPU Shadow Inference Audit");
        lines.Add("contract=" + Contract);
        lines.Add("status=" + status);
        lines.Add("observerOnly=1");
        lines.Add("productionBackendChange=0");
        lines.Add("productionTrackerWrites=0");
        lines.Add("shadowBackend=CPU");
        lines.Add("shadowModel=KiwiFaceLandmarkInference");
        lines.Add("shadowInput=FIXED_ZERO_1x3x192x192");
        lines.Add("shadowPackedOutputLength=" + PackedOutputLength);
        lines.Add("warmupCount=" + WarmupCount);
        lines.Add(
            "durationSeconds=" +
            measuredSeconds.ToString(
                "F6",
                CultureInfo.InvariantCulture));
        lines.Add(
            "requestedShadowHz=" +
            _sampleHz.ToString(
                "F3",
                CultureInfo.InvariantCulture));
        lines.Add("shadowRequestCount=" + _requestCount);
        lines.Add("shadowCompletedCount=" + _completedCount);
        lines.Add("shadowErrorCount=" + _errorCount);
        lines.Add("");

        AppendDoubleStats(
            lines,
            "cpuShadowScheduleCpuMs",
            _shadowScheduleCpuMs);

        AppendDoubleStats(
            lines,
            "cpuShadowServiceMs",
            _shadowServiceMs);

        AppendIntStats(
            lines,
            "cpuShadowCompletionFrames",
            _shadowFrameDelta);

        AppendFloatStats(
            lines,
            "sampledProductionTrackerLatencyMs",
            _productionLatencyMs);

        AppendIntStats(
            lines,
            "sampledProductionActiveLanes",
            _productionActiveLanes);

        AppendIntStats(
            lines,
            "sampledProductionLaneLimit",
            _productionLaneLimit);

        lines.Add("");
        lines.Add("[PRODUCTION_COUNTER_DELTAS]");
        lines.Add(
            "scheduledDelta=" +
            Math.Max(
                0,
                _endScheduled - _startScheduled));
        lines.Add(
            "readbackCompletedDelta=" +
            Math.Max(
                0,
                _endReadbackCompleted -
                _startReadbackCompleted));
        lines.Add(
            "completedDelta=" +
            Math.Max(
                0,
                _endCompleted - _startCompleted));
        lines.Add(
            "droppedFreshDelta=" +
            Math.Max(
                0,
                _endDropped - _startDropped));
        lines.Add(
            "discardedStaleDelta=" +
            Math.Max(
                0,
                _endStale - _startStale));

        if (measuredSeconds > 0.0)
        {
            lines.Add(
                "scheduledHz=" +
                (
                    Math.Max(
                        0,
                        _endScheduled -
                        _startScheduled) /
                    measuredSeconds
                ).ToString(
                    "F6",
                    CultureInfo.InvariantCulture));

            lines.Add(
                "readbackCompletedHz=" +
                (
                    Math.Max(
                        0,
                        _endReadbackCompleted -
                        _startReadbackCompleted) /
                    measuredSeconds
                ).ToString(
                    "F6",
                    CultureInfo.InvariantCulture));

            lines.Add(
                "completedHz=" +
                (
                    Math.Max(
                        0,
                        _endCompleted -
                        _startCompleted) /
                    measuredSeconds
                ).ToString(
                    "F6",
                    CultureInfo.InvariantCulture));
        }

        lines.Add("");
        lines.Add("[DECISION_GUIDE]");
        lines.Add(
            "CPU_CANDIDATE only if cpuShadowServiceMs median is " +
            "materially below the v44.50 GPU/readback ~38 ms floor.");
        lines.Add(
            "Also require Production render/camera/inference counters and " +
            "MP4 to remain stable while the low-rate shadow is active.");
        lines.Add(
            "This audit does not prove tracking correctness on CPU because " +
            "the shadow uses a fixed zero input and publishes nothing.");

        File.WriteAllLines(path, lines);

        Debug.Log(
            "[Kiwi v44.51 CPU Shadow Audit] " +
            status +
            " report=" + path);
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
                "Face landmark model does not expose expected outputs.");
        }

        FunctionalGraph graph =
            new FunctionalGraph();

        FunctionalTensor[] inputs =
            graph.AddInputs(source);

        FunctionalTensor[] outputs =
            Functional.Forward(
                source,
                inputs);

        FunctionalTensor landmarks =
            outputs[landmarkIndex]
                .Reshape(
                    new[]
                    {
                        BaseLandmarkCount * 3
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

        return graph.Compile(packed);
    }

    private static int FindOutputIndex(
        Model model,
        string outputName)
    {
        for (
            int i = 0;
            i < model.outputs.Count;
            i++)
        {
            if (
                model.outputs[i].name ==
                outputName)
            {
                return i;
            }
        }

        return -1;
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
            name + ".samples=" +
            data.Length);
        lines.Add(
            name + ".mean=" +
            (sum / data.Length).ToString(
                "F6",
                CultureInfo.InvariantCulture));
        lines.Add(
            name + ".median=" +
            Percentile(
                data,
                0.50).ToString(
                    "F6",
                    CultureInfo.InvariantCulture));
        lines.Add(
            name + ".p95=" +
            Percentile(
                data,
                0.95).ToString(
                    "F6",
                    CultureInfo.InvariantCulture));
        lines.Add(
            name + ".max=" +
            data[data.Length - 1].ToString(
                "F6",
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

        double[] data =
            new double[values.Count];

        for (
            int i = 0;
            i < values.Count;
            i++)
        {
            data[i] = values[i];
        }

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
                "F6",
                CultureInfo.InvariantCulture));
        lines.Add(
            name + ".median=" +
            Percentile(data, 0.50).ToString(
                "F6",
                CultureInfo.InvariantCulture));
        lines.Add(
            name + ".p95=" +
            Percentile(data, 0.95).ToString(
                "F6",
                CultureInfo.InvariantCulture));
        lines.Add(
            name + ".max=" +
            data[data.Length - 1].ToString(
                "F6",
                CultureInfo.InvariantCulture));
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

        int[] data =
            values.ToArray();

        Array.Sort(data);

        long sum = 0L;

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
            (
                (double)sum /
                data.Length
            ).ToString(
                "F6",
                CultureInfo.InvariantCulture));
        lines.Add(
            name + ".median=" +
            Percentile(data, 0.50).ToString(
                "F6",
                CultureInfo.InvariantCulture));
        lines.Add(
            name + ".p95=" +
            Percentile(data, 0.95).ToString(
                "F6",
                CultureInfo.InvariantCulture));
        lines.Add(
            name + ".max=" +
            data[data.Length - 1]);
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
            position - lower;

        return
            sorted[lower] +
            (
                sorted[upper] -
                sorted[lower]
            ) * t;
    }

    private static double Percentile(
        int[] sorted,
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
            position - lower;

        return
            sorted[lower] +
            (
                sorted[upper] -
                sorted[lower]
            ) * t;
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

        if (_cpuInput != null)
        {
            _cpuInput.Dispose();
            _cpuInput = null;
        }
    }

    private static bool ReadBoolEnvironment(
        string name,
        bool fallback)
    {
        string value =
            Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        value = value.Trim();

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
            Environment.GetEnvironmentVariable(name);

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
            Environment.GetEnvironmentVariable(name);

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
