using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// KiwiAvatarSystem Phase16.20.18 v30 Unity frame bottleneck probe.
///
/// Observer-only diagnostic.
/// No tracking, Native camera, inference, FaceTexture or preview source
/// ownership is modified.
///
/// Enabled only when:
///   KIWI_UNITY_FRAME_TIMING_PROBE=1
///
/// The probe uses Unity ProfilerRecorder for only selected built-in counters
/// and markers. Samples are buffered in managed memory and written only when
/// the probe is disabled/destroyed, so there is no per-frame disk I/O.
///
/// Output:
///   <ProjectRoot>\KiwiUnityFrameTiming_yyyyMMdd_HHmmss.csv
///   <ProjectRoot>\KiwiUnityFrameTiming_yyyyMMdd_HHmmss.meta.txt
/// </summary>
[DefaultExecutionOrder(32000)]
public sealed class KiwiUnityFrameTimingProbe : MonoBehaviour
{
    public const string ContractMarker =
        "KIWI_V5_1_PHASE16_20_18_V30_UNITY_FRAME_BOTTLENECK_PROBE";

    private const string EnableEnvironment =
        "KIWI_UNITY_FRAME_TIMING_PROBE";

    private const int MaximumSamples =
        12000;

    private sealed class Metric : IDisposable
    {
        public readonly string requestedName;
        public readonly string resolvedCategory;
        public readonly ProfilerMarkerDataUnit unit;
        public readonly bool valid;

        private ProfilerRecorder _recorder;

        public Metric(
            string requestedName,
            ProfilerRecorder recorder,
            string resolvedCategory,
            ProfilerMarkerDataUnit unit,
            bool valid)
        {
            this.requestedName =
                requestedName;

            _recorder =
                recorder;

            this.resolvedCategory =
                resolvedCategory;

            this.unit =
                unit;

            this.valid =
                valid;
        }

        public double ReadAsDouble()
        {
            if (
                !valid ||
                !_recorder.Valid ||
                !_recorder.IsRunning ||
                _recorder.Count <= 0
            )
            {
                return double.NaN;
            }

            double raw =
                _recorder.LastValueAsDouble;

            if (
                _recorder.UnitType ==
                ProfilerMarkerDataUnit.TimeNanoseconds
            )
            {
                return
                    raw /
                    1000000.0;
            }

            return raw;
        }

        public void Dispose()
        {
            if (_recorder.Valid)
            {
                _recorder.Dispose();
            }
        }
    }

    private struct Sample
    {
        public int unityFrame;
        public double realtimeSeconds;
        public double unscaledDeltaMs;
        public double instantaneousFps;
        public double processCpuCoreEquivalent;
        public double processCpuPercentOfLogical;
        public long processWorkingSetBytes;
        public int processThreadCount;
        public int gc0;
        public int gc1;
        public int gc2;

        public double cpuMainThreadFrameMs;
        public double cpuRenderThreadFrameMs;
        public double cpuTotalFrameMs;
        public double gpuFrameMs;

        public double playerLoopMs;
        public double waitForPresentOnGfxThreadMs;
        public double waitForRenderThreadMs;
        public double waitForTargetFpsMs;
        public double presentFrameMs;
        public double processCommandsMs;
        public double waitForCommandsMs;
        public double d3d12WaitForLastPresentMs;
        public double scriptUpdateMs;
        public double scriptLateUpdateMs;

        public double batchesCount;
        public double drawCallsCount;
    }

    private static readonly string[] MetricNames =
    {
        "CPU Main Thread Frame Time",
        "CPU Render Thread Frame Time",
        "CPU Total Frame Time",
        "GPU Frame Time",

        "PlayerLoop",
        "Gfx.WaitForPresentOnGfxThread",
        "Gfx.WaitForRenderThread",
        "WaitForTargetFPS",
        "Gfx.PresentFrame",
        "Gfx.ProcessCommands",
        "Gfx.WaitForCommands",
        "GfxDeviceD3D12.WaitForLastPresent",
        "Update.ScriptRunBehaviourUpdate",
        "PreLateUpdate.ScriptRunBehaviourLateUpdate",

        "Batches Count",
        "Draw Calls Count"
    };

    private readonly List<Sample> _samples =
        new List<Sample>(
            8192);

    private readonly Dictionary<string, Metric> _metrics =
        new Dictionary<string, Metric>(
            StringComparer.Ordinal);

    private Process _process;
    private TimeSpan _lastProcessCpuTime;
    private double _lastProcessSampleRealtime;
    private bool _hasProcessCpuBaseline;
    private bool _written;
    private string _outputBasePath;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (!IsEnabledByEnvironment())
        {
            return;
        }

        if (
            FindFirstObjectByType<KiwiUnityFrameTimingProbe>() !=
            null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(
                "KiwiUnityFrameTimingProbe");

        host.hideFlags =
            HideFlags.DontSave;

        DontDestroyOnLoad(
            host);

        host.AddComponent<KiwiUnityFrameTimingProbe>();
    }

    private static bool IsEnabledByEnvironment()
    {
        string raw =
            Environment.GetEnvironmentVariable(
                EnableEnvironment);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        raw =
            raw.Trim();

        return
            string.Equals(
                raw,
                "1",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                raw,
                "ON",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                raw,
                "TRUE",
                StringComparison.OrdinalIgnoreCase);
    }

    private void OnEnable()
    {
        if (!IsEnabledByEnvironment())
        {
            enabled =
                false;
            return;
        }

        _process =
            Process.GetCurrentProcess();

        _lastProcessCpuTime =
            _process.TotalProcessorTime;

        _lastProcessSampleRealtime =
            Time.realtimeSinceStartupAsDouble;

        _hasProcessCpuBaseline =
            true;

        string projectRoot =
            Directory.GetParent(
                Application.dataPath)?.FullName
            ?? Application.dataPath;

        string stamp =
            DateTime.Now.ToString(
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture);

        _outputBasePath =
            Path.Combine(
                projectRoot,
                "KiwiUnityFrameTiming_" + stamp);

        DiscoverMetrics();

        Debug.Log(
            "[KiwiFrameTimingProbe] enabled=1 " +
            "output=" +
            _outputBasePath +
            ".csv " +
            "previewMode=" +
            SafePreviewModeName());
    }

    private void LateUpdate()
    {
        if (_samples.Count >= MaximumSamples)
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        double cpuCoreEquivalent =
            double.NaN;

        double cpuPercentLogical =
            double.NaN;

        if (
            _process != null &&
            _hasProcessCpuBaseline
        )
        {
            _process.Refresh();

            TimeSpan totalCpu =
                _process.TotalProcessorTime;

            double wallSeconds =
                now -
                _lastProcessSampleRealtime;

            double cpuSeconds =
                (
                    totalCpu -
                    _lastProcessCpuTime
                ).TotalSeconds;

            if (wallSeconds > 0.000001)
            {
                cpuCoreEquivalent =
                    cpuSeconds /
                    wallSeconds;

                cpuPercentLogical =
                    cpuCoreEquivalent /
                    Math.Max(
                        1,
                        Environment.ProcessorCount) *
                    100.0;
            }

            _lastProcessCpuTime =
                totalCpu;

            _lastProcessSampleRealtime =
                now;
        }

        double dt =
            Time.unscaledDeltaTime;

        Sample sample =
            new Sample
            {
                unityFrame =
                    Time.frameCount,

                realtimeSeconds =
                    now,

                unscaledDeltaMs =
                    dt > 0.0
                        ? dt * 1000.0
                        : double.NaN,

                instantaneousFps =
                    dt > 0.0
                        ? 1.0 / dt
                        : double.NaN,

                processCpuCoreEquivalent =
                    cpuCoreEquivalent,

                processCpuPercentOfLogical =
                    cpuPercentLogical,

                processWorkingSetBytes =
                    _process != null
                        ? _process.WorkingSet64
                        : 0L,

                processThreadCount =
                    _process != null
                        ? _process.Threads.Count
                        : 0,

                gc0 =
                    GC.CollectionCount(0),

                gc1 =
                    GC.CollectionCount(1),

                gc2 =
                    GC.CollectionCount(2),

                cpuMainThreadFrameMs =
                    ReadMetric(
                        "CPU Main Thread Frame Time"),

                cpuRenderThreadFrameMs =
                    ReadMetric(
                        "CPU Render Thread Frame Time"),

                cpuTotalFrameMs =
                    ReadMetric(
                        "CPU Total Frame Time"),

                gpuFrameMs =
                    ReadMetric(
                        "GPU Frame Time"),

                playerLoopMs =
                    ReadMetric(
                        "PlayerLoop"),

                waitForPresentOnGfxThreadMs =
                    ReadMetric(
                        "Gfx.WaitForPresentOnGfxThread"),

                waitForRenderThreadMs =
                    ReadMetric(
                        "Gfx.WaitForRenderThread"),

                waitForTargetFpsMs =
                    ReadMetric(
                        "WaitForTargetFPS"),

                presentFrameMs =
                    ReadMetric(
                        "Gfx.PresentFrame"),

                processCommandsMs =
                    ReadMetric(
                        "Gfx.ProcessCommands"),

                waitForCommandsMs =
                    ReadMetric(
                        "Gfx.WaitForCommands"),

                d3d12WaitForLastPresentMs =
                    ReadMetric(
                        "GfxDeviceD3D12.WaitForLastPresent"),

                scriptUpdateMs =
                    ReadMetric(
                        "Update.ScriptRunBehaviourUpdate"),

                scriptLateUpdateMs =
                    ReadMetric(
                        "PreLateUpdate.ScriptRunBehaviourLateUpdate"),

                batchesCount =
                    ReadMetric(
                        "Batches Count"),

                drawCallsCount =
                    ReadMetric(
                        "Draw Calls Count")
            };

        _samples.Add(
            sample);
    }

    private void OnDisable()
    {
        WriteOutputOnce();
        DisposeMetrics();
    }

    private void OnDestroy()
    {
        WriteOutputOnce();
        DisposeMetrics();
    }

    private void OnApplicationQuit()
    {
        WriteOutputOnce();
    }

    private void DiscoverMetrics()
    {
        List<ProfilerRecorderHandle> handles =
            new List<ProfilerRecorderHandle>(
                512);

        ProfilerRecorderHandle.GetAvailable(
            handles);

        foreach (string requested in MetricNames)
        {
            ProfilerRecorderHandle selected =
                default;

            ProfilerRecorderDescription description =
                default;

            bool found =
                false;

            foreach (ProfilerRecorderHandle handle in handles)
            {
                if (!handle.Valid)
                {
                    continue;
                }

                ProfilerRecorderDescription candidate =
                    ProfilerRecorderHandle.GetDescription(
                        handle);

                if (
                    !string.Equals(
                        candidate.Name,
                        requested,
                        StringComparison.Ordinal)
                )
                {
                    continue;
                }

                selected =
                    handle;

                description =
                    candidate;

                found =
                    true;
                break;
            }

            if (!found)
            {
                _metrics[requested] =
                    new Metric(
                        requested,
                        default,
                        "UNAVAILABLE",
                        ProfilerMarkerDataUnit.Undefined,
                        false);

                continue;
            }

            ProfilerRecorder recorder =
                ProfilerRecorder.StartNew(
                    description.Category,
                    description.Name,
                    1);

            bool valid =
                recorder.Valid;

            _metrics[requested] =
                new Metric(
                    requested,
                    recorder,
                    description.Category.ToString(),
                    description.UnitType,
                    valid);
        }
    }

    private double ReadMetric(
        string name)
    {
        if (
            _metrics.TryGetValue(
                name,
                out Metric metric)
        )
        {
            return
                metric.ReadAsDouble();
        }

        return double.NaN;
    }

    private void DisposeMetrics()
    {
        foreach (
            KeyValuePair<string, Metric> pair
            in _metrics
        )
        {
            pair.Value.Dispose();
        }

        _metrics.Clear();
    }

    private void WriteOutputOnce()
    {
        if (_written)
        {
            return;
        }

        _written =
            true;

        if (
            string.IsNullOrWhiteSpace(
                _outputBasePath)
        )
        {
            return;
        }

        try
        {
            WriteCsv(
                _outputBasePath +
                ".csv");

            WriteMetadata(
                _outputBasePath +
                ".meta.txt");

            Debug.Log(
                "[KiwiFrameTimingProbe] wrote " +
                _samples.Count +
                " samples: " +
                _outputBasePath +
                ".csv");
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[KiwiFrameTimingProbe] write failed: " +
                exception);
        }
    }

    private void WriteCsv(
        string path)
    {
        using StreamWriter writer =
            new StreamWriter(
                path,
                false,
                new UTF8Encoding(
                    false));

        writer.WriteLine(
            "unityFrame,realtimeSeconds,unscaledDeltaMs,instantaneousFps," +
            "processCpuCoreEquivalent,processCpuPercentOfLogical,processWorkingSetBytes,processThreadCount," +
            "gc0,gc1,gc2," +
            "cpuMainThreadFrameMs,cpuRenderThreadFrameMs,cpuTotalFrameMs,gpuFrameMs," +
            "playerLoopMs,waitForPresentOnGfxThreadMs,waitForRenderThreadMs,waitForTargetFpsMs," +
            "presentFrameMs,processCommandsMs,waitForCommandsMs,d3d12WaitForLastPresentMs," +
            "scriptUpdateMs,scriptLateUpdateMs,batchesCount,drawCallsCount");

        foreach (Sample sample in _samples)
        {
            StringBuilder row =
                new StringBuilder(
                    512);

            Append(
                row,
                sample.unityFrame);
            Sep(row);

            Append(
                row,
                sample.realtimeSeconds);
            Sep(row);

            Append(
                row,
                sample.unscaledDeltaMs);
            Sep(row);

            Append(
                row,
                sample.instantaneousFps);
            Sep(row);

            Append(
                row,
                sample.processCpuCoreEquivalent);
            Sep(row);

            Append(
                row,
                sample.processCpuPercentOfLogical);
            Sep(row);

            Append(
                row,
                sample.processWorkingSetBytes);
            Sep(row);

            Append(
                row,
                sample.processThreadCount);
            Sep(row);

            Append(
                row,
                sample.gc0);
            Sep(row);

            Append(
                row,
                sample.gc1);
            Sep(row);

            Append(
                row,
                sample.gc2);
            Sep(row);

            Append(
                row,
                sample.cpuMainThreadFrameMs);
            Sep(row);

            Append(
                row,
                sample.cpuRenderThreadFrameMs);
            Sep(row);

            Append(
                row,
                sample.cpuTotalFrameMs);
            Sep(row);

            Append(
                row,
                sample.gpuFrameMs);
            Sep(row);

            Append(
                row,
                sample.playerLoopMs);
            Sep(row);

            Append(
                row,
                sample.waitForPresentOnGfxThreadMs);
            Sep(row);

            Append(
                row,
                sample.waitForRenderThreadMs);
            Sep(row);

            Append(
                row,
                sample.waitForTargetFpsMs);
            Sep(row);

            Append(
                row,
                sample.presentFrameMs);
            Sep(row);

            Append(
                row,
                sample.processCommandsMs);
            Sep(row);

            Append(
                row,
                sample.waitForCommandsMs);
            Sep(row);

            Append(
                row,
                sample.d3d12WaitForLastPresentMs);
            Sep(row);

            Append(
                row,
                sample.scriptUpdateMs);
            Sep(row);

            Append(
                row,
                sample.scriptLateUpdateMs);
            Sep(row);

            Append(
                row,
                sample.batchesCount);
            Sep(row);

            Append(
                row,
                sample.drawCallsCount);

            writer.WriteLine(
                row.ToString());
        }
    }

    private void WriteMetadata(
        string path)
    {
        using StreamWriter writer =
            new StreamWriter(
                path,
                false,
                new UTF8Encoding(
                    false));

        writer.WriteLine(
            "KiwiAvatarSystem v30 Unity Frame Bottleneck Probe");

        writer.WriteLine(
            "contract=" +
            ContractMarker);

        writer.WriteLine(
            "unityVersion=" +
            Application.unityVersion);

        writer.WriteLine(
            "previewMode=" +
            SafePreviewModeName());

        writer.WriteLine(
            "targetFrameRate=" +
            Application.targetFrameRate);

        writer.WriteLine(
            "vSyncCount=" +
            QualitySettings.vSyncCount);

        writer.WriteLine(
            "maxQueuedFrames=" +
            QualitySettings.maxQueuedFrames);

        writer.WriteLine(
            "processorCount=" +
            SystemInfo.processorCount);

        writer.WriteLine(
            "processorFrequencyMHz=" +
            SystemInfo.processorFrequency);

        writer.WriteLine(
            "processorType=" +
            SystemInfo.processorType);

        writer.WriteLine(
            "graphicsDeviceName=" +
            SystemInfo.graphicsDeviceName);

        writer.WriteLine(
            "graphicsDeviceType=" +
            SystemInfo.graphicsDeviceType);

        writer.WriteLine(
            "graphicsMemoryMB=" +
            SystemInfo.graphicsMemorySize);

        writer.WriteLine(
            "supportsGpuRecorder=" +
            SystemInfo.supportsGpuRecorder);

        writer.WriteLine(
            "sampleCount=" +
            _samples.Count);

        writer.WriteLine();
        writer.WriteLine(
            "ProfilerRecorder metrics:");

        foreach (string name in MetricNames)
        {
            if (
                !_metrics.TryGetValue(
                    name,
                    out Metric metric)
            )
            {
                writer.WriteLine(
                    name +
                    "|valid=0|category=UNKNOWN|unit=Undefined");
                continue;
            }

            writer.WriteLine(
                name +
                "|valid=" +
                (metric.valid ? "1" : "0") +
                "|category=" +
                metric.resolvedCategory +
                "|unit=" +
                metric.unit);
        }
    }

    private static string SafePreviewModeName()
    {
        try
        {
            return
                KiwiCameraPreviewQualityService.
                    CurrentModeName;
        }
        catch
        {
            return "UNKNOWN";
        }
    }

    private static void Sep(
        StringBuilder builder)
    {
        builder.Append(
            ',');
    }

    private static void Append(
        StringBuilder builder,
        int value)
    {
        builder.Append(
            value.ToString(
                CultureInfo.InvariantCulture));
    }

    private static void Append(
        StringBuilder builder,
        long value)
    {
        builder.Append(
            value.ToString(
                CultureInfo.InvariantCulture));
    }

    private static void Append(
        StringBuilder builder,
        double value)
    {
        if (
            double.IsNaN(value) ||
            double.IsInfinity(value)
        )
        {
            return;
        }

        builder.Append(
            value.ToString(
                "R",
                CultureInfo.InvariantCulture));
    }
}
