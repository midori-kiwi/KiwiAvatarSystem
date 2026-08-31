using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

/// <summary>
/// KiwiAvatarSystem v44.50
/// Observer-only Inference GPU Service Breakdown Audit.
///
/// Purpose:
/// - preserve the frozen Production inference/tracking path unchanged;
/// - measure Unity/DX12 AsyncGPUReadback completion-floor latency with a tiny,
///   independent persistent GPU buffer;
/// - read existing KiwiInferenceFaceTracker public telemetry via reflection;
/// - compare the control readback floor against the existing inference
///   request->done telemetry from KiwiFrameComparison CSV.
///
/// Important:
/// The control request is NOT a model-output request. It is intentionally an
/// independent 4-byte lower-bound/floor probe. Do not mechanically subtract
/// the two medians as if they were serialized stages.
/// </summary>
internal sealed class KiwiInferenceGpuServiceBreakdownAuditV44_50 : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_50_INFERENCE_GPU_SERVICE_BREAKDOWN_OBSERVER";

    private const string EnableVariable =
        "KIWI_V44_50_GPU_SERVICE_AUDIT";

    private const string DurationVariable =
        "KIWI_V44_50_GPU_SERVICE_AUDIT_SECONDS";

    private const string StableVariable =
        "KIWI_V44_50_STABLE_SECONDS";

    private const string SampleHzVariable =
        "KIWI_V44_50_CONTROL_READBACK_HZ";

    private const string ExpectedTrianglesVariable =
        "KIWI_V44_50_EXPECTED_TRIANGLES";

    private const int DefaultExpectedTriangles = 254296;
    private const float DefaultDurationSeconds = 30f;
    private const float DefaultStableSeconds = 2f;
    private const float DefaultControlReadbackHz = 5f;
    private const int SlotCount = 4;

    private sealed class ProbeSlot
    {
        public bool pending;
        public AsyncGPUReadbackRequest request;
        public long startedTicks;
        public int startedFrame;
    }

    private static bool _installed;

    private readonly ProbeSlot[] _slots = new ProbeSlot[SlotCount];
    private readonly List<double> _controlMs = new List<double>(256);
    private readonly List<int> _controlFrameDelta = new List<int>(256);
    private readonly List<float> _sampledInferenceLatencyMs = new List<float>(4096);
    private readonly List<int> _sampledLaneLimits = new List<int>(4096);
    private readonly List<int> _sampledActiveLanes = new List<int>(4096);

    private ComputeBuffer _controlBuffer;

    private float _durationSeconds;
    private float _stableSeconds;
    private float _controlReadbackHz;
    private int _expectedTriangles;

    private double _gateMatchedSince = -1.0;
    private double _measurementStartedAt = -1.0;
    private double _nextControlRequestAt = -1.0;
    private double _finishRequestedAt = -1.0;

    private bool _measuring;
    private bool _measurementDurationElapsed;
    private bool _reportWritten;

    private int _controlRequestCount;
    private int _controlCompletedCount;
    private int _controlErrorCount;
    private int _controlNoFreeSlotCount;

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
            "[Kiwi] v44.50 Inference GPU Service Breakdown Audit");

        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.DontSave;

        go.AddComponent<KiwiInferenceGpuServiceBreakdownAuditV44_50>();
    }

    private void Awake()
    {
        _durationSeconds = Mathf.Clamp(
            ReadFloatEnvironment(
                DurationVariable,
                DefaultDurationSeconds),
            5f,
            180f);

        _stableSeconds = Mathf.Clamp(
            ReadFloatEnvironment(
                StableVariable,
                DefaultStableSeconds),
            0.5f,
            10f);

        _controlReadbackHz = Mathf.Clamp(
            ReadFloatEnvironment(
                SampleHzVariable,
                DefaultControlReadbackHz),
            1f,
            15f);

        _expectedTriangles = Mathf.Max(
            1,
            ReadIntEnvironment(
                ExpectedTrianglesVariable,
                DefaultExpectedTriangles));

        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new ProbeSlot();
        }

        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            Debug.LogError(
                "[Kiwi v44.50 GPU Service Audit] FAIL " +
                "supportsAsyncGPUReadback=0");

            enabled = false;
            return;
        }

        _controlBuffer = new ComputeBuffer(
            1,
            sizeof(float),
            ComputeBufferType.Structured);

        _controlBuffer.SetData(new[] { 1.0f });

        Debug.Log(
            "[Kiwi v44.50 GPU Service Audit] WAIT_GATE " +
            "contract=" + Contract +
            " observerOnly=1" +
            " expectedTriangles=" + _expectedTriangles +
            " stableSeconds=" +
                _stableSeconds.ToString("F2", CultureInfo.InvariantCulture) +
            " durationSeconds=" +
                _durationSeconds.ToString("F1", CultureInfo.InvariantCulture) +
            " controlReadbackHz=" +
                _controlReadbackHz.ToString("F1", CultureInfo.InvariantCulture) +
            " controlBytes=4" +
            " waitCalls=0" +
            " trackerWrites=0" +
            " inferenceScheduleChanges=0");
    }

    private void Update()
    {
        if (_reportWritten || _controlBuffer == null)
        {
            return;
        }

        PollControlRequests();

        if (!_measuring)
        {
            UpdateGate();
            return;
        }

        SampleTrackerTelemetry();

        double now = Time.realtimeSinceStartupAsDouble;

        if (!_measurementDurationElapsed)
        {
            if (now >= _nextControlRequestAt)
            {
                IssueControlRequest(now);

                _nextControlRequestAt =
                    now + 1.0 / _controlReadbackHz;
            }

            if (
                now - _measurementStartedAt >=
                _durationSeconds)
            {
                _measurementDurationElapsed = true;
                _finishRequestedAt = now;
                CaptureEndCounters();

                Debug.Log(
                    "[Kiwi v44.50 GPU Service Audit] " +
                    "MEASURE_DURATION_COMPLETE " +
                    "waitingForControlRequestsNonBlocking=1");
            }

            return;
        }

        // Never wait synchronously. Give already-issued tiny requests a short
        // non-blocking drain window, then write the report regardless.
        if (
            PendingControlCount() == 0 ||
            now - _finishRequestedAt >= 2.0)
        {
            WriteReport();
        }
    }

    private void UpdateGate()
    {
        double now = Time.realtimeSinceStartupAsDouble;

        DiscoverTrackerIfNeeded(now);

        int triangles = CountEnabledSkinnedTriangles();

        bool triangleMatch =
            triangles == _expectedTriangles;

        bool trackerReady =
            _tracker != null &&
            ReadTrackerInt("ScheduledFrameCount", 0) > 0;

        if (!triangleMatch || !trackerReady)
        {
            _gateMatchedSince = -1.0;
            return;
        }

        if (_gateMatchedSince < 0.0)
        {
            _gateMatchedSince = now;

            Debug.Log(
                "[Kiwi v44.50 GPU Service Audit] GATE_MATCH " +
                "triangles=" + triangles +
                " trackerReady=1" +
                " waitingStableSeconds=" +
                    _stableSeconds.ToString(
                        "F2",
                        CultureInfo.InvariantCulture));

            return;
        }

        if (
            now - _gateMatchedSince <
            _stableSeconds)
        {
            return;
        }

        BeginMeasurement(now);
    }

    private void BeginMeasurement(double now)
    {
        _measuring = true;
        _measurementStartedAt = now;
        _nextControlRequestAt = now;
        _measurementDurationElapsed = false;

        _controlRequestCount = 0;
        _controlCompletedCount = 0;
        _controlErrorCount = 0;
        _controlNoFreeSlotCount = 0;

        _controlMs.Clear();
        _controlFrameDelta.Clear();
        _sampledInferenceLatencyMs.Clear();
        _sampledLaneLimits.Clear();
        _sampledActiveLanes.Clear();

        CaptureStartCounters();

        Debug.Log(
            "[Kiwi v44.50 GPU Service Audit] MEASURE_START " +
            "observerOnly=1" +
            " triangles=" + _expectedTriangles +
            " durationSeconds=" +
                _durationSeconds.ToString("F1", CultureInfo.InvariantCulture) +
            " controlReadbackHz=" +
                _controlReadbackHz.ToString("F1", CultureInfo.InvariantCulture) +
            " controlBytes=4" +
            " noWait=1");
    }

    private void IssueControlRequest(double now)
    {
        ProbeSlot slot = null;

        for (int i = 0; i < _slots.Length; i++)
        {
            if (!_slots[i].pending)
            {
                slot = _slots[i];
                break;
            }
        }

        if (slot == null)
        {
            _controlNoFreeSlotCount++;
            return;
        }

        try
        {
            slot.startedTicks = Stopwatch.GetTimestamp();
            slot.startedFrame = Time.frameCount;
            slot.request = AsyncGPUReadback.Request(_controlBuffer);
            slot.pending = true;
            _controlRequestCount++;
        }
        catch (Exception exception)
        {
            _controlErrorCount++;

            Debug.LogWarning(
                "[Kiwi v44.50 GPU Service Audit] " +
                "CONTROL_REQUEST_EXCEPTION " +
                exception.GetType().Name);
        }
    }

    private void PollControlRequests()
    {
        long nowTicks = Stopwatch.GetTimestamp();

        for (int i = 0; i < _slots.Length; i++)
        {
            ProbeSlot slot = _slots[i];

            if (!slot.pending || !slot.request.done)
            {
                continue;
            }

            slot.pending = false;
            _controlCompletedCount++;

            if (slot.request.hasError)
            {
                _controlErrorCount++;
                continue;
            }

            if (slot.startedTicks > 0L)
            {
                double ms =
                    (nowTicks - slot.startedTicks) *
                    1000.0 /
                    Stopwatch.Frequency;

                _controlMs.Add(ms);
            }

            _controlFrameDelta.Add(
                Mathf.Max(
                    0,
                    Time.frameCount - slot.startedFrame));
        }
    }

    private int PendingControlCount()
    {
        int count = 0;

        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].pending)
            {
                count++;
            }
        }

        return count;
    }

    private void DiscoverTrackerIfNeeded(double now)
    {
        if (_tracker != null || now < _nextTrackerDiscoveryAt)
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
                    value = field.GetValue(behaviour);
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
                    "[Kiwi v44.50 GPU Service Audit] " +
                    "TRACKER_FOUND readOnlyReflection=1" +
                    " type=" + _trackerType.FullName);

                return;
            }
        }
    }

    private void SampleTrackerTelemetry()
    {
        if (_tracker == null)
        {
            return;
        }

        float latency = ReadTrackerFloat(
            "LatestLatencyMs",
            0f);

        int laneLimit = ReadTrackerInt(
            "SchedulingLaneLimit",
            0);

        int activeLanes = ReadTrackerInt(
            "ActiveLaneCount",
            0);

        if (
            latency > 0f &&
            !float.IsNaN(latency) &&
            !float.IsInfinity(latency))
        {
            _sampledInferenceLatencyMs.Add(latency);
        }

        _sampledLaneLimits.Add(laneLimit);
        _sampledActiveLanes.Add(activeLanes);
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

    private int ReadTrackerInt(string propertyName, int fallback)
    {
        object value = ReadTrackerProperty(propertyName);

        if (value is int integer)
        {
            return integer;
        }

        return fallback;
    }

    private float ReadTrackerFloat(string propertyName, float fallback)
    {
        object value = ReadTrackerProperty(propertyName);

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

    private int CountEnabledSkinnedTriangles()
    {
        SkinnedMeshRenderer[] renderers =
            FindObjectsByType<SkinnedMeshRenderer>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

        long total = 0L;

        for (int i = 0; i < renderers.Length; i++)
        {
            SkinnedMeshRenderer renderer = renderers[i];

            if (
                renderer == null ||
                !renderer.enabled ||
                renderer.forceRenderingOff ||
                renderer.sharedMesh == null)
            {
                continue;
            }

            Mesh mesh = renderer.sharedMesh;

            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                MeshTopology topology =
                    mesh.GetTopology(s);

                if (topology != MeshTopology.Triangles)
                {
                    continue;
                }

                total +=
                    (long)mesh.GetIndexCount(s) / 3L;
            }
        }

        if (total > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)total;
    }

    private void WriteReport()
    {
        if (_reportWritten)
        {
            return;
        }

        _reportWritten = true;

        CaptureEndCounters();

        double measuredSeconds =
            Math.Max(
                0.001,
                Math.Min(
                    Time.realtimeSinceStartupAsDouble -
                        _measurementStartedAt,
                    _durationSeconds));

        string directory = Path.Combine(
            Application.persistentDataPath,
            "KiwiFrameBottleneck");

        Directory.CreateDirectory(directory);

        string path = Path.Combine(
            directory,
            "KiwiInferenceGpuServiceBreakdown_v44_50_" +
            DateTime.Now.ToString(
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture) +
            ".txt");

        List<string> lines = new List<string>();

        lines.Add(
            "KiwiAvatarSystem v44.50 Inference GPU Service Breakdown Audit");
        lines.Add("contract=" + Contract);
        lines.Add("observerOnly=1");
        lines.Add("trackerWrites=0");
        lines.Add("inferenceScheduleChanges=0");
        lines.Add("blockingWaitCalls=0");
        lines.Add("controlProbe=ASYNC_GPU_READBACK_PERSISTENT_4_BYTE_BUFFER");
        lines.Add("controlProbeRole=READBACK_COMPLETION_FLOOR_NOT_MODEL_STAGE");
        lines.Add(
            "durationSeconds=" +
            measuredSeconds.ToString("F6", CultureInfo.InvariantCulture));
        lines.Add("expectedEnabledSkinnedTriangles=" + _expectedTriangles);
        lines.Add(
            "controlReadbackRequestedHz=" +
            _controlReadbackHz.ToString("F3", CultureInfo.InvariantCulture));
        lines.Add("controlRequestCount=" + _controlRequestCount);
        lines.Add("controlCompletedCount=" + _controlCompletedCount);
        lines.Add("controlErrorCount=" + _controlErrorCount);
        lines.Add("controlNoFreeSlotCount=" + _controlNoFreeSlotCount);
        lines.Add("");

        AppendDoubleStats(
            lines,
            "controlReadbackCompletionMs",
            _controlMs);

        AppendIntStats(
            lines,
            "controlReadbackCompletionFrames",
            _controlFrameDelta);

        AppendFloatStats(
            lines,
            "sampledTrackerLatestLatencyMs",
            _sampledInferenceLatencyMs);

        AppendIntStats(
            lines,
            "sampledSchedulingLaneLimit",
            _sampledLaneLimits);

        AppendIntStats(
            lines,
            "sampledActiveLaneCount",
            _sampledActiveLanes);

        lines.Add("");
        lines.Add("[TRACKER_COUNTER_DELTAS]");
        lines.Add(
            "scheduledDelta=" +
            Math.Max(0, _endScheduled - _startScheduled));
        lines.Add(
            "readbackCompletedDelta=" +
            Math.Max(
                0,
                _endReadbackCompleted - _startReadbackCompleted));
        lines.Add(
            "completedDelta=" +
            Math.Max(0, _endCompleted - _startCompleted));
        lines.Add(
            "droppedFreshDelta=" +
            Math.Max(0, _endDropped - _startDropped));
        lines.Add(
            "discardedStaleDelta=" +
            Math.Max(0, _endStale - _startStale));

        lines.Add(
            "scheduledHz=" +
            (
                Math.Max(0, _endScheduled - _startScheduled) /
                measuredSeconds
            ).ToString("F6", CultureInfo.InvariantCulture));

        lines.Add(
            "readbackCompletedHz=" +
            (
                Math.Max(
                    0,
                    _endReadbackCompleted - _startReadbackCompleted) /
                measuredSeconds
            ).ToString("F6", CultureInfo.InvariantCulture));

        lines.Add(
            "completedHz=" +
            (
                Math.Max(0, _endCompleted - _startCompleted) /
                measuredSeconds
            ).ToString("F6", CultureInfo.InvariantCulture));

        lines.Add("");
        lines.Add("[INTERPRETATION]");
        lines.Add(
            "Compare controlReadbackCompletionFrames with the existing " +
            "KiwiFrameComparison inference request->done frame delta.");
        lines.Add(
            "If control is ~1 frame while inference is ~3 frames, " +
            "the additional delay is upstream model/GPU dependency service.");
        lines.Add(
            "If control is also ~3 frames, Unity/DX12 asynchronous readback " +
            "completion/poll cadence is a dominant contributor.");
        lines.Add(
            "Do not subtract medians mechanically: the control buffer and " +
            "model output are independent GPU dependency chains.");

        File.WriteAllLines(path, lines);

        Debug.Log(
            "[Kiwi v44.50 GPU Service Audit] COMPLETE report=" +
            path);
    }

    private static void AppendDoubleStats(
        List<string> lines,
        string name,
        List<double> values)
    {
        if (values == null || values.Count == 0)
        {
            lines.Add(name + ".samples=0");
            return;
        }

        double[] data = values.ToArray();
        Array.Sort(data);

        double sum = 0.0;

        for (int i = 0; i < data.Length; i++)
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

    private static void AppendFloatStats(
        List<string> lines,
        string name,
        List<float> values)
    {
        if (values == null || values.Count == 0)
        {
            lines.Add(name + ".samples=0");
            return;
        }

        double[] data = new double[values.Count];

        for (int i = 0; i < values.Count; i++)
        {
            data[i] = values[i];
        }

        Array.Sort(data);

        double sum = 0.0;

        for (int i = 0; i < data.Length; i++)
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
        if (values == null || values.Count == 0)
        {
            lines.Add(name + ".samples=0");
            return;
        }

        int[] data = values.ToArray();
        Array.Sort(data);

        long sum = 0L;

        for (int i = 0; i < data.Length; i++)
        {
            sum += data[i];
        }

        lines.Add(name + ".samples=" + data.Length);
        lines.Add(
            name + ".mean=" +
            (
                (double)sum / data.Length
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

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted == null || sorted.Length == 0)
        {
            return 0.0;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        double position =
            (sorted.Length - 1) * percentile;

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

        double t = position - lower;

        return
            sorted[lower] +
            (sorted[upper] - sorted[lower]) * t;
    }

    private static double Percentile(int[] sorted, double percentile)
    {
        if (sorted == null || sorted.Length == 0)
        {
            return 0.0;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        double position =
            (sorted.Length - 1) * percentile;

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

        double t = position - lower;

        return
            sorted[lower] +
            (sorted[upper] - sorted[lower]) * t;
    }

    private void OnDestroy()
    {
        if (
            _controlBuffer != null)
        {
            _controlBuffer.Dispose();
            _controlBuffer = null;
        }
    }

    private void OnApplicationQuit()
    {
        if (
            _measuring &&
            !_reportWritten)
        {
            WriteReport();
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
