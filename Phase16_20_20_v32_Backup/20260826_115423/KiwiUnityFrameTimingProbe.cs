using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

/// <summary>
/// KiwiAvatarSystem Phase16.20.19 v31 accepted-face downstream workload probe.
///
/// Observer-only diagnostic. No tracking, camera, inference, FaceTexture,
/// FacePart, avatar transform, or preview behavior is changed.
///
/// Enabled only by:
///   KIWI_UNITY_WORKLOAD_PROBE=1
///
/// The probe:
/// 1. enumerates all profiler handles available in the running Unity build;
/// 2. records the fixed frame bottleneck counters proven useful in v30;
/// 3. dynamically selects up to 48 time markers relevant to:
///    texture upload, rendering, GUI/UI, camera rendering, skinning,
///    animation, compute/inference, command buffers, and Kiwi markers;
/// 4. sums all occurrences of each selected marker per frame;
/// 5. buffers samples in memory and writes only when Play Mode stops.
///
/// This is intentionally designed to identify accepted-face downstream cost
/// without disabling any production subsystem.
/// </summary>
[DefaultExecutionOrder(32100)]
public sealed class KiwiUnityFrameTimingProbe : MonoBehaviour
{
    public const string ContractMarker =
        "KIWI_V5_1_PHASE16_20_19_V31_ACCEPTED_FACE_DOWNSTREAM_WORKLOAD_PROBE";

    private const string EnableEnvironment =
        "KIWI_UNITY_WORKLOAD_PROBE";

    private const int MaximumSamples =
        12000;

    private const int MaximumDynamicMetrics =
        48;

    private static readonly string[] FixedMetricNames =
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
        "GfxDeviceD3D12.WaitForLastPresent",
        "Update.ScriptRunBehaviourUpdate",
        "PreLateUpdate.ScriptRunBehaviourLateUpdate",
        "Batches Count",
        "Draw Calls Count"
    };

    private static readonly string[] ExactPriorityNames =
    {
        "Texture2D.Apply",
        "Texture2D.SetPixels32",
        "Camera.Render",
        "Camera.RenderByRenderGraph",
        "RenderPipelineManager.DoRenderLoop_Internal",
        "UI.Rendering.UpdateBatches",
        "Canvas.BuildBatch",
        "GUI.Repaint",
        "IMGUI.Repaint",
        "Graphics.Blit",
        "CommandBuffer.Execute",
        "ComputeShader.Dispatch",
        "SkinnedMeshRenderer.UpdateSkinning",
        "MeshSkinning.SkinOnGPU",
        "Animator.Update",
        "EditorLoop"
    };

    private sealed class Metric : IDisposable
    {
        public readonly string name;
        public readonly string category;
        public readonly ProfilerMarkerDataUnit unit;
        public readonly int score;
        public readonly bool isFixed;

        private ProfilerRecorder _recorder;

        public Metric(
            string name,
            string category,
            ProfilerMarkerDataUnit unit,
            int score,
            bool isFixed,
            ProfilerRecorder recorder)
        {
            this.name = name;
            this.category = category;
            this.unit = unit;
            this.score = score;
            this.isFixed = isFixed;
            _recorder = recorder;
        }

        public bool IsValid =>
            _recorder.Valid;

        public double Read()
        {
            if (
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
                return raw / 1000000.0;
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

    private sealed class Sample
    {
        public int unityFrame;
        public double realtimeSeconds;
        public double unscaledDeltaMs;
        public double instantaneousFps;
        public double[] fixedValues;
        public double[] dynamicValues;
    }

    private sealed class Candidate
    {
        public ProfilerRecorderHandle handle;
        public ProfilerRecorderDescription description;
        public int score;
    }

    private readonly List<Metric> _fixed =
        new List<Metric>();

    private readonly List<Metric> _dynamic =
        new List<Metric>();

    private readonly List<Sample> _samples =
        new List<Sample>(8192);

    private readonly List<string> _availableCatalog =
        new List<string>();

    private string _outputBasePath;
    private bool _written;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (!IsEnabled())
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
                "KiwiUnityWorkloadProbe");

        host.hideFlags =
            HideFlags.DontSave;

        DontDestroyOnLoad(host);

        host.AddComponent<KiwiUnityFrameTimingProbe>();
    }

    private static bool IsEnabled()
    {
        string raw =
            Environment.GetEnvironmentVariable(
                EnableEnvironment);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        raw = raw.Trim();

        return
            string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "ON", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "TRUE", StringComparison.OrdinalIgnoreCase);
    }

    private void OnEnable()
    {
        if (!IsEnabled())
        {
            enabled = false;
            return;
        }

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
                "KiwiUnityWorkloadTiming_" + stamp);

        DiscoverAndStartMetrics();

        Debug.Log(
            "[KiwiWorkloadProbe] enabled=1 fixed=" +
            _fixed.Count +
            " dynamic=" +
            _dynamic.Count +
            " output=" +
            _outputBasePath +
            ".csv");
    }

    private void LateUpdate()
    {
        if (_samples.Count >= MaximumSamples)
        {
            return;
        }

        Sample sample =
            new Sample
            {
                unityFrame =
                    Time.frameCount,

                realtimeSeconds =
                    Time.realtimeSinceStartupAsDouble,

                unscaledDeltaMs =
                    Time.unscaledDeltaTime > 0f
                        ? Time.unscaledDeltaTime * 1000.0
                        : double.NaN,

                instantaneousFps =
                    Time.unscaledDeltaTime > 0f
                        ? 1.0 / Time.unscaledDeltaTime
                        : double.NaN,

                fixedValues =
                    new double[_fixed.Count],

                dynamicValues =
                    new double[_dynamic.Count]
            };

        for (int i = 0; i < _fixed.Count; i++)
        {
            sample.fixedValues[i] =
                _fixed[i].Read();
        }

        for (int i = 0; i < _dynamic.Count; i++)
        {
            sample.dynamicValues[i] =
                _dynamic[i].Read();
        }

        _samples.Add(sample);
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

    private void DiscoverAndStartMetrics()
    {
        List<ProfilerRecorderHandle> handles =
            new List<ProfilerRecorderHandle>(
                2048);

        ProfilerRecorderHandle.GetAvailable(
            handles);

        Dictionary<string, ProfilerRecorderDescription> byName =
            new Dictionary<string, ProfilerRecorderDescription>(
                StringComparer.Ordinal);

        foreach (ProfilerRecorderHandle handle in handles)
        {
            if (!handle.Valid)
            {
                continue;
            }

            ProfilerRecorderDescription description =
                ProfilerRecorderHandle.GetDescription(
                    handle);

            string catalogLine =
                description.Category +
                "|" +
                description.Name +
                "|" +
                description.UnitType;

            _availableCatalog.Add(
                catalogLine);

            if (!byName.ContainsKey(description.Name))
            {
                byName.Add(
                    description.Name,
                    description);
            }
        }

        _availableCatalog.Sort(
            StringComparer.Ordinal);

        foreach (string name in FixedMetricNames)
        {
            if (!byName.TryGetValue(
                    name,
                    out ProfilerRecorderDescription description))
            {
                continue;
            }

            Metric metric =
                StartMetric(
                    description,
                    int.MaxValue,
                    true);

            if (metric != null)
            {
                _fixed.Add(metric);
            }
        }

        HashSet<string> fixedNames =
            new HashSet<string>(
                _fixed.Select(x => x.name),
                StringComparer.Ordinal);

        List<Candidate> candidates =
            new List<Candidate>();

        foreach (ProfilerRecorderHandle handle in handles)
        {
            if (!handle.Valid)
            {
                continue;
            }

            ProfilerRecorderDescription description =
                ProfilerRecorderHandle.GetDescription(
                    handle);

            if (
                description.UnitType !=
                ProfilerMarkerDataUnit.TimeNanoseconds
            )
            {
                continue;
            }

            if (fixedNames.Contains(description.Name))
            {
                continue;
            }

            int score =
                ScoreMetric(
                    description.Name);

            if (score <= 0)
            {
                continue;
            }

            candidates.Add(
                new Candidate
                {
                    handle = handle,
                    description = description,
                    score = score
                });
        }

        foreach (
            Candidate candidate
            in candidates
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.description.Name, StringComparer.Ordinal)
                .Take(MaximumDynamicMetrics)
        )
        {
            Metric metric =
                StartMetric(
                    candidate.description,
                    candidate.score,
                    false);

            if (metric != null)
            {
                _dynamic.Add(metric);
            }
        }
    }

    private static Metric StartMetric(
        ProfilerRecorderDescription description,
        int score,
        bool isFixed)
    {
        try
        {
            ProfilerRecorderOptions options =
                ProfilerRecorderOptions.SumAllSamplesInFrame |
                ProfilerRecorderOptions.WrapAroundWhenCapacityReached;

            ProfilerRecorder recorder =
                ProfilerRecorder.StartNew(
                    description.Category,
                    description.Name,
                    1,
                    options);

            if (!recorder.Valid)
            {
                recorder.Dispose();
                return null;
            }

            return
                new Metric(
                    description.Name,
                    description.Category.ToString(),
                    description.UnitType,
                    score,
                    isFixed,
                    recorder);
        }
        catch
        {
            return null;
        }
    }

    private static int ScoreMetric(
        string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return 0;
        }

        foreach (string exact in ExactPriorityNames)
        {
            if (
                string.Equals(
                    name,
                    exact,
                    StringComparison.Ordinal)
            )
            {
                return 1000;
            }
        }

        int score = 0;

        ScoreContains(
            name,
            "Kiwi",
            950,
            ref score);

        ScoreContains(
            name,
            "Sentis",
            940,
            ref score);

        ScoreContains(
            name,
            "Inference",
            930,
            ref score);

        ScoreContains(
            name,
            "Tensor",
            920,
            ref score);

        ScoreContains(
            name,
            "Texture2D",
            900,
            ref score);

        ScoreContains(
            name,
            "Upload",
            890,
            ref score);

        ScoreContains(
            name,
            "Blit",
            880,
            ref score);

        ScoreContains(
            name,
            "Camera.Render",
            870,
            ref score);

        ScoreContains(
            name,
            "RenderPipeline",
            860,
            ref score);

        ScoreContains(
            name,
            "Compute",
            850,
            ref score);

        ScoreContains(
            name,
            "Dispatch",
            840,
            ref score);

        ScoreContains(
            name,
            "CommandBuffer",
            830,
            ref score);

        ScoreContains(
            name,
            "Skinn",
            820,
            ref score);

        ScoreContains(
            name,
            "Animator",
            810,
            ref score);

        ScoreContains(
            name,
            "Animation",
            800,
            ref score);

        ScoreContains(
            name,
            "Canvas",
            790,
            ref score);

        ScoreContains(
            name,
            "GUI",
            780,
            ref score);

        ScoreContains(
            name,
            "UI.",
            770,
            ref score);

        ScoreContains(
            name,
            "Render",
            500,
            ref score);

        ScoreContains(
            name,
            "Draw",
            450,
            ref score);

        return score;
    }

    private static void ScoreContains(
        string name,
        string token,
        int candidateScore,
        ref int score)
    {
        if (
            name.IndexOf(
                token,
                StringComparison.OrdinalIgnoreCase) >= 0
        )
        {
            score =
                Math.Max(
                    score,
                    candidateScore);
        }
    }

    private void DisposeMetrics()
    {
        foreach (Metric metric in _fixed)
        {
            metric.Dispose();
        }

        foreach (Metric metric in _dynamic)
        {
            metric.Dispose();
        }

        _fixed.Clear();
        _dynamic.Clear();
    }

    private void WriteOutputOnce()
    {
        if (_written)
        {
            return;
        }

        _written = true;

        if (string.IsNullOrWhiteSpace(_outputBasePath))
        {
            return;
        }

        try
        {
            WriteCsv(
                _outputBasePath + ".csv");

            WriteMeta(
                _outputBasePath + ".meta.txt");

            Debug.Log(
                "[KiwiWorkloadProbe] wrote " +
                _samples.Count +
                " samples.");
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[KiwiWorkloadProbe] output failed: " +
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
                new UTF8Encoding(false));

        StringBuilder header =
            new StringBuilder(
                8192);

        header.Append(
            "unityFrame,realtimeSeconds,unscaledDeltaMs,instantaneousFps");

        foreach (Metric metric in _fixed)
        {
            header.Append(',');
            header.Append(
                EscapeCsv(
                    "fixed:" +
                    metric.category +
                    ":" +
                    metric.name));
        }

        foreach (Metric metric in _dynamic)
        {
            header.Append(',');
            header.Append(
                EscapeCsv(
                    "dynamic:" +
                    metric.category +
                    ":" +
                    metric.name));
        }

        writer.WriteLine(
            header.ToString());

        foreach (Sample sample in _samples)
        {
            StringBuilder row =
                new StringBuilder(
                    8192);

            Append(row, sample.unityFrame);
            Sep(row);
            Append(row, sample.realtimeSeconds);
            Sep(row);
            Append(row, sample.unscaledDeltaMs);
            Sep(row);
            Append(row, sample.instantaneousFps);

            foreach (double value in sample.fixedValues)
            {
                Sep(row);
                Append(row, value);
            }

            foreach (double value in sample.dynamicValues)
            {
                Sep(row);
                Append(row, value);
            }

            writer.WriteLine(
                row.ToString());
        }
    }

    private void WriteMeta(
        string path)
    {
        using StreamWriter writer =
            new StreamWriter(
                path,
                false,
                new UTF8Encoding(false));

        writer.WriteLine(
            "KiwiAvatarSystem v31 Accepted-Face Downstream Workload Probe");

        writer.WriteLine(
            "contract=" +
            ContractMarker);

        writer.WriteLine(
            "unityVersion=" +
            Application.unityVersion);

        writer.WriteLine(
            "graphicsDevice=" +
            SystemInfo.graphicsDeviceName);

        writer.WriteLine(
            "graphicsApi=" +
            SystemInfo.graphicsDeviceType);

        writer.WriteLine(
            "sampleCount=" +
            _samples.Count);

        writer.WriteLine(
            "fixedMetricCount=" +
            _fixed.Count);

        writer.WriteLine(
            "dynamicMetricCount=" +
            _dynamic.Count);

        writer.WriteLine();
        writer.WriteLine("SELECTED_FIXED");

        foreach (Metric metric in _fixed)
        {
            writer.WriteLine(
                metric.category +
                "|" +
                metric.name +
                "|" +
                metric.unit);
        }

        writer.WriteLine();
        writer.WriteLine("SELECTED_DYNAMIC");

        foreach (Metric metric in _dynamic)
        {
            writer.WriteLine(
                metric.score +
                "|" +
                metric.category +
                "|" +
                metric.name +
                "|" +
                metric.unit);
        }

        writer.WriteLine();
        writer.WriteLine("AVAILABLE_CATALOG");

        foreach (string line in _availableCatalog)
        {
            writer.WriteLine(line);
        }
    }

    private static string EscapeCsv(
        string value)
    {
        if (
            value.IndexOfAny(
                new[] { ',', '"', '\n', '\r' }) < 0
        )
        {
            return value;
        }

        return
            "\"" +
            value.Replace(
                "\"",
                "\"\"") +
            "\"";
    }

    private static void Sep(
        StringBuilder builder)
    {
        builder.Append(',');
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
