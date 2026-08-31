using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

/// <summary>
/// KiwiAvatarSystem v44.47 - O25 Steady-State Frame Bottleneck Audit.
///
/// Observer-only diagnostic:
/// - does not write tracking / threshold / ROI / authority / camera / inference
///   / presentation state;
/// - does not alter vSync, targetFrameRate, maxQueuedFrames, renderers or Spout;
/// - samples Unity ProfilerRecorder markers once per rendered Update;
/// - inventories the active SkinnedMeshRenderer topology so v44.11 O25 can be
///   verified in the exact runtime being profiled.
///
/// Enable only with:
///   KIWI_V44_47_STEADY_AUDIT=1
///
/// Optional:
///   KIWI_V44_47_STEADY_AUDIT_SECONDS=30
///   KIWI_V44_47_STABLE_SECONDS=2
///   KIWI_V44_47_EXPECTED_TRIANGLES=254296
///
/// Measurement starts only after the enabled SkinnedMeshRenderer triangle
/// inventory equals the expected O25 topology continuously for the requested
/// stable window. This excludes startup simplification and temporary
/// Surface Fit topology transactions.
///
/// Report:
///   Application.persistentDataPath/KiwiFrameBottleneck/
///   KiwiSteadyStateBottleneck_v44_47_YYYYMMDD_HHMMSS.txt
/// </summary>
[DisallowMultipleComponent]
public sealed class KiwiSteadyStateBottleneckAuditV44_47 : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_47_O25_STEADY_STATE_FRAME_BOTTLENECK_AUDIT";

    private const string EnableEnvironment =
        "KIWI_V44_47_STEADY_AUDIT";

    private const string DurationEnvironment =
        "KIWI_V44_47_STEADY_AUDIT_SECONDS";

    private const string StableSecondsEnvironment =
        "KIWI_V44_47_STABLE_SECONDS";

    private const string ExpectedTrianglesEnvironment =
        "KIWI_V44_47_EXPECTED_TRIANGLES";

    private const double DefaultStableSeconds = 2.0;
    private const double MinStableSeconds = 0.5;
    private const double MaxStableSeconds = 10.0;
    private const long DefaultExpectedTriangles = 254296L;
    private const double GatePollIntervalSeconds = 0.25;

    private const double DefaultDurationSeconds = 30.0;
    private const double MinDurationSeconds = 5.0;
    private const double MaxDurationSeconds = 180.0;

    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private static readonly ProfilerCategory PlayerLoopCategory =
        new ProfilerCategory("PlayerLoop");

    private static readonly ProfilerCategory VSyncCategory =
        new ProfilerCategory("VSync");

    private sealed class Metric : IDisposable
    {
        public readonly ProfilerCategory category;
        public readonly string name;
        public ProfilerRecorder recorder;
        public readonly List<long> values = new List<long>(4096);

        public Metric(ProfilerCategory category, string name)
        {
            this.category = category;
            this.name = name;

            try
            {
                recorder = ProfilerRecorder.StartNew(
                    category,
                    name,
                    1,
                    ProfilerRecorderOptions.Default);
            }
            catch
            {
                recorder = default;
            }
        }

        public bool IsValid
        {
            get { return recorder.Valid; }
        }

        public ProfilerMarkerDataUnit Unit
        {
            get
            {
                return recorder.Valid
                    ? recorder.UnitType
                    : ProfilerMarkerDataUnit.Undefined;
            }
        }

        public void Sample()
        {
            if (!recorder.Valid)
            {
                return;
            }

            values.Add(recorder.LastValue);
        }

        public void Dispose()
        {
            if (recorder.Valid)
            {
                recorder.Dispose();
            }
        }
    }

    private static bool _bootstrapped;

    private readonly List<Metric> _metrics =
        new List<Metric>(32);

    private double _durationSeconds;
    private double _stableSeconds;
    private long _expectedTriangles;
    private double _bootstrapRealtime;
    private double _stableSinceRealtime = -1.0;
    private double _nextGatePollRealtime;
    private double _startedRealtime;
    private int _startFrame;
    private long _gateStartTriangles;
    private bool _measurementStarted;
    private bool _written;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_bootstrapped)
        {
            return;
        }

        _bootstrapped = true;

        string enabled =
            Environment.GetEnvironmentVariable(
                EnableEnvironment);

        if (!string.Equals(
                enabled,
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        GameObject go =
            new GameObject(
                "[Kiwi] v44.47 O25 Steady-State Bottleneck Audit");

        DontDestroyOnLoad(go);
        go.AddComponent<KiwiSteadyStateBottleneckAuditV44_47>();
    }

    private void Awake()
    {
        _durationSeconds =
            ReadDurationSeconds();

        _stableSeconds =
            ReadStableSeconds();

        _expectedTriangles =
            ReadExpectedTriangles();

        _bootstrapRealtime =
            Time.realtimeSinceStartupAsDouble;

        _nextGatePollRealtime =
            _bootstrapRealtime;

        Debug.Log(
            "[Kiwi v44.47 Steady Audit] WAIT_GATE observerOnly=1 duration=" +
            _durationSeconds.ToString("F1", Invariant) +
            "s expectedTriangles=" +
            _expectedTriangles.ToString(Invariant) +
            " stableSeconds=" +
            _stableSeconds.ToString("F1", Invariant) +
            " screen=" +
            Screen.width.ToString(Invariant) +
            "x" +
            Screen.height.ToString(Invariant) +
            " targetFrameRate=" +
            Application.targetFrameRate.ToString(Invariant) +
            " vSyncCount=" +
            QualitySettings.vSyncCount.ToString(Invariant) +
            " maxQueuedFrames=" +
            QualitySettings.maxQueuedFrames.ToString(Invariant));
    }

    private void Update()
    {
        double now =
            Time.realtimeSinceStartupAsDouble;

        if (!_measurementStarted)
        {
            if (now < _nextGatePollRealtime)
            {
                return;
            }

            _nextGatePollRealtime =
                now + GatePollIntervalSeconds;

            long enabledTriangles =
                GetEnabledSkinnedTriangles();

            if (enabledTriangles == _expectedTriangles)
            {
                if (_stableSinceRealtime < 0.0)
                {
                    _stableSinceRealtime = now;

                    Debug.Log(
                        "[Kiwi v44.47 Steady Audit] GATE_MATCH triangles=" +
                        enabledTriangles.ToString(Invariant) +
                        " waitingStableSeconds=" +
                        _stableSeconds.ToString("F1", Invariant));
                }

                if (
                    now - _stableSinceRealtime >=
                    _stableSeconds)
                {
                    BeginMeasurement(
                        now,
                        enabledTriangles);
                }
            }
            else
            {
                if (_stableSinceRealtime >= 0.0)
                {
                    Debug.Log(
                        "[Kiwi v44.47 Steady Audit] GATE_RESET triangles=" +
                        enabledTriangles.ToString(Invariant) +
                        " expected=" +
                        _expectedTriangles.ToString(Invariant));
                }

                _stableSinceRealtime = -1.0;
            }

            return;
        }

        for (int i = 0; i < _metrics.Count; i++)
        {
            _metrics[i].Sample();
        }

        if (
            !_written &&
            now -
                _startedRealtime >=
                _durationSeconds)
        {
            WriteReport();
        }
    }

    private void BeginMeasurement(
        double now,
        long enabledTriangles)
    {
        if (_measurementStarted)
        {
            return;
        }

        _measurementStarted = true;
        _gateStartTriangles = enabledTriangles;

        RegisterMetrics();

        _startedRealtime = now;
        _startFrame = Time.frameCount;

        Debug.Log(
            "[Kiwi v44.47 Steady Audit] MEASURE_START observerOnly=1 triangles=" +
            enabledTriangles.ToString(Invariant) +
            " gateWaitSeconds=" +
            (now - _bootstrapRealtime).ToString("F3", Invariant) +
            " duration=" +
            _durationSeconds.ToString("F1", Invariant) +
            "s");
    }

    private void OnApplicationQuit()
    {
        if (_measurementStarted && !_written)
        {
            WriteReport();
        }
    }

    private void OnDestroy()
    {
        for (int i = 0; i < _metrics.Count; i++)
        {
            _metrics[i].Dispose();
        }

        _metrics.Clear();
    }

    private static double ReadDurationSeconds()
    {
        string text =
            Environment.GetEnvironmentVariable(
                DurationEnvironment);

        double parsed;

        if (
            !string.IsNullOrWhiteSpace(text) &&
            double.TryParse(
                text,
                NumberStyles.Float,
                Invariant,
                out parsed))
        {
            return Math.Max(
                MinDurationSeconds,
                Math.Min(
                    MaxDurationSeconds,
                    parsed));
        }

        return DefaultDurationSeconds;
    }

    private static double ReadStableSeconds()
    {
        string text =
            Environment.GetEnvironmentVariable(
                StableSecondsEnvironment);

        double parsed;

        if (
            !string.IsNullOrWhiteSpace(text) &&
            double.TryParse(
                text,
                NumberStyles.Float,
                Invariant,
                out parsed))
        {
            return Math.Max(
                MinStableSeconds,
                Math.Min(
                    MaxStableSeconds,
                    parsed));
        }

        return DefaultStableSeconds;
    }

    private static long ReadExpectedTriangles()
    {
        string text =
            Environment.GetEnvironmentVariable(
                ExpectedTrianglesEnvironment);

        long parsed;

        if (
            !string.IsNullOrWhiteSpace(text) &&
            long.TryParse(
                text,
                NumberStyles.Integer,
                Invariant,
                out parsed) &&
            parsed > 0L)
        {
            return parsed;
        }

        return DefaultExpectedTriangles;
    }

    private static long GetEnabledSkinnedTriangles()
    {
        SkinnedMeshRenderer[] skinned =
            FindObjectsByType<SkinnedMeshRenderer>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        long enabledTriangles = 0L;

        for (int i = 0; i < skinned.Length; i++)
        {
            SkinnedMeshRenderer renderer =
                skinned[i];

            if (
                renderer == null ||
                !renderer.enabled ||
                !renderer.gameObject.activeInHierarchy ||
                renderer.forceRenderingOff)
            {
                continue;
            }

            enabledTriangles +=
                CountTriangles(
                    renderer.sharedMesh);
        }

        return enabledTriangles;
    }

    private void RegisterMetrics()
    {
        Add(ProfilerCategory.Render, "CPU Main Thread Frame Time");
        Add(ProfilerCategory.Render, "CPU Render Thread Frame Time");
        Add(ProfilerCategory.Render, "CPU Total Frame Time");
        Add(ProfilerCategory.Render, "GPU Frame Time");

        Add(PlayerLoopCategory, "PlayerLoop");
        Add(PlayerLoopCategory, "Update.ScriptRunBehaviourUpdate");
        Add(PlayerLoopCategory, "PreLateUpdate.ScriptRunBehaviourLateUpdate");
        Add(PlayerLoopCategory, "PostLateUpdate.UpdateAllRenderers");
        Add(PlayerLoopCategory, "PostLateUpdate.UpdateAllSkinnedMeshes");

        Add(ProfilerCategory.Render, "Camera.Render");
        Add(ProfilerCategory.Render, "RenderLoop");
        Add(ProfilerCategory.Render, "Drawing");
        Add(ProfilerCategory.Render, "Graphics.Blit");
        Add(ProfilerCategory.Render, "Compute.Dispatch");
        Add(ProfilerCategory.Render, "Gfx.UploadTextureData");
        Add(ProfilerCategory.Render, "Gfx.UploadTexture");
        Add(ProfilerCategory.Render, "Gfx.CopyTextureData");
        Add(ProfilerCategory.Render, "GfxDeviceD3D12.ExecuteCommandList");
        Add(ProfilerCategory.Render, "GfxDeviceD3D12.WaitForGPU");
        Add(VSyncCategory, "GfxDeviceD3D12.WaitForLastPresent");
        Add(ProfilerCategory.Render, "GfxDeviceD3D12.Swap");
        Add(ProfilerCategory.Render, "IDXGISwapChain::Present");
        Add(ProfilerCategory.Render, "GfxTask_PluginEventAndData");
        Add(ProfilerCategory.Render, "GfxTask_WaitOnGpuFence");
        Add(ProfilerCategory.Internal, "Gfx.WaitForGfxCommandWriteSpace");
        Add(ProfilerCategory.Internal, "Gfx.WaitForGfxCommandsFromMainThread");
        Add(ProfilerCategory.Render, "Gfx.WaitForPresentOnGfxThread");
        Add(ProfilerCategory.Render, "Gfx.WaitForRenderThread");
        Add(VSyncCategory, "WaitForTargetFPS");
        Add(ProfilerCategory.Render, "Gfx.PresentFrame");
        Add(ProfilerCategory.Gui, "GUI.Repaint");
    }

    private void Add(
        ProfilerCategory category,
        string name)
    {
        _metrics.Add(
            new Metric(
                category,
                name));
    }

    private void WriteReport()
    {
        if (_written)
        {
            return;
        }

        _written = true;

        double ended =
            Time.realtimeSinceStartupAsDouble;

        double elapsed =
            Math.Max(
                0.001,
                ended -
                _startedRealtime);

        int renderFrames =
            Math.Max(
                0,
                Time.frameCount -
                _startFrame);

        string directory =
            Path.Combine(
                Application.persistentDataPath,
                "KiwiFrameBottleneck");

        Directory.CreateDirectory(directory);

        string path =
            Path.Combine(
                directory,
                "KiwiSteadyStateBottleneck_v44_47_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss",
                    Invariant) +
                ".txt");

        using (StreamWriter writer =
            new StreamWriter(
                path,
                false))
        {
            writer.WriteLine(
                "KiwiAvatarSystem v44.47 O25 Steady-State Frame Bottleneck Audit");
            writer.WriteLine(
                "contract=" + Contract);
            writer.WriteLine(
                "observerOnly=1");
            writer.WriteLine(
                "measurementMode=O25_STEADY_STATE_AFTER_TOPOLOGY_GATE");
            writer.WriteLine(
                "expectedEnabledSkinnedTriangles=" +
                _expectedTriangles.ToString(Invariant));
            writer.WriteLine(
                "gateStartTriangles=" +
                _gateStartTriangles.ToString(Invariant));
            writer.WriteLine(
                "gateStableSeconds=" +
                _stableSeconds.ToString("F6", Invariant));
            writer.WriteLine(
                "gateWaitSeconds=" +
                (_startedRealtime - _bootstrapRealtime).ToString("F6", Invariant));
            writer.WriteLine(
                "durationSeconds=" +
                elapsed.ToString("F6", Invariant));
            writer.WriteLine(
                "renderFrames=" +
                renderFrames.ToString(Invariant));
            writer.WriteLine(
                "renderHz=" +
                (renderFrames / elapsed).ToString("F6", Invariant));
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
                "graphicsMemoryMB=" +
                SystemInfo.graphicsMemorySize.ToString(Invariant));
            writer.WriteLine(
                "screen=" +
                Screen.width.ToString(Invariant) +
                "x" +
                Screen.height.ToString(Invariant));
            writer.WriteLine(
                "vSyncCount=" +
                QualitySettings.vSyncCount.ToString(Invariant));
            writer.WriteLine(
                "targetFrameRate=" +
                Application.targetFrameRate.ToString(Invariant));
            writer.WriteLine(
                "maxQueuedFrames=" +
                QualitySettings.maxQueuedFrames.ToString(Invariant));

            writer.WriteLine();
            WriteRenderInventory(writer);

            writer.WriteLine();
            writer.WriteLine("[PROFILER_METRICS]");
            writer.WriteLine(
                "format=category|name|unit|samples|mean|median|p95|max");
            writer.WriteLine(
                "note=Profiler markers are nested; values MUST NOT be summed.");

            for (int i = 0; i < _metrics.Count; i++)
            {
                WriteMetric(
                    writer,
                    _metrics[i]);
            }

            writer.WriteLine();
            writer.WriteLine("[TOP_TIMING_MARKERS_BY_P95]");
            writer.WriteLine(
                "note=nested markers are ranked but not summed.");

            List<Tuple<Metric, double>> ranked =
                new List<Tuple<Metric, double>>();

            for (int i = 0; i < _metrics.Count; i++)
            {
                Metric metric = _metrics[i];

                if (
                    metric.IsValid &&
                    metric.Unit ==
                        ProfilerMarkerDataUnit.TimeNanoseconds &&
                    metric.values.Count > 0)
                {
                    ranked.Add(
                        Tuple.Create(
                            metric,
                            PercentileMilliseconds(
                                metric.values,
                                0.95)));
                }
            }

            ranked.Sort(
                (a, b) =>
                    b.Item2.CompareTo(
                        a.Item2));

            for (
                int i = 0;
                i < Math.Min(
                    20,
                    ranked.Count);
                i++)
            {
                Metric metric =
                    ranked[i].Item1;

                writer.WriteLine(
                    metric.category.Name +
                    "|" +
                    metric.name +
                    "|p95ms=" +
                    ranked[i].Item2.ToString("F6", Invariant) +
                    "|medianms=" +
                    PercentileMilliseconds(
                        metric.values,
                        0.50).ToString("F6", Invariant) +
                    "|meanms=" +
                    MeanMilliseconds(
                        metric.values).ToString("F6", Invariant));
            }

            writer.WriteLine();
            writer.WriteLine("[UNAVAILABLE_REQUESTED]");

            bool anyUnavailable = false;

            for (int i = 0; i < _metrics.Count; i++)
            {
                Metric metric =
                    _metrics[i];

                if (!metric.IsValid)
                {
                    anyUnavailable = true;
                    writer.WriteLine(
                        metric.category.Name +
                        "|" +
                        metric.name);
                }
            }

            if (!anyUnavailable)
            {
                writer.WriteLine("none");
            }

            writer.WriteLine();
            writer.WriteLine(
                "note=No tracking/threshold/ROI/authority/camera/inference/presentation/render-setting writes.");
        }

        Debug.Log(
            "[Kiwi v44.47 Steady Audit] COMPLETE report=" +
            path);
    }

    private static void WriteRenderInventory(
        StreamWriter writer)
    {
        writer.WriteLine("[RENDER_INVENTORY]");

        SkinnedMeshRenderer[] skinned =
            FindObjectsByType<SkinnedMeshRenderer>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        long enabledTriangles = 0L;
        long allTriangles = 0L;
        int enabledSkinned = 0;

        writer.WriteLine(
            "skinnedRendererCount=" +
            skinned.Length.ToString(Invariant));

        for (int i = 0; i < skinned.Length; i++)
        {
            SkinnedMeshRenderer renderer =
                skinned[i];

            Mesh mesh =
                renderer != null
                    ? renderer.sharedMesh
                    : null;

            long triangles =
                CountTriangles(mesh);

            allTriangles +=
                triangles;

            bool enabled =
                renderer != null &&
                renderer.enabled &&
                renderer.gameObject.activeInHierarchy &&
                !renderer.forceRenderingOff;

            if (enabled)
            {
                enabledSkinned++;
                enabledTriangles +=
                    triangles;
            }

            writer.WriteLine(
                "skinned[" +
                i.ToString(Invariant) +
                "]=" +
                HierarchyPath(
                    renderer != null
                        ? renderer.transform
                        : null) +
                "|enabled=" +
                (enabled ? "1" : "0") +
                "|forceRenderingOff=" +
                (renderer != null &&
                 renderer.forceRenderingOff
                    ? "1"
                    : "0") +
                "|mesh=" +
                (mesh != null
                    ? mesh.name
                    : "<null>") +
                "|vertices=" +
                (mesh != null
                    ? mesh.vertexCount.ToString(Invariant)
                    : "0") +
                "|triangles=" +
                triangles.ToString(Invariant) +
                "|blendShapes=" +
                (mesh != null
                    ? mesh.blendShapeCount.ToString(Invariant)
                    : "0"));
        }

        writer.WriteLine(
            "enabledSkinnedRendererCount=" +
            enabledSkinned.ToString(Invariant));
        writer.WriteLine(
            "enabledSkinnedTrianglesApprox=" +
            enabledTriangles.ToString(Invariant));
        writer.WriteLine(
            "allSkinnedTrianglesApprox=" +
            allTriangles.ToString(Invariant));

        Camera[] cameras =
            FindObjectsByType<Camera>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        int enabledCameras =
            cameras.Count(
                camera =>
                    camera != null &&
                    camera.enabled &&
                    camera.gameObject.activeInHierarchy);

        writer.WriteLine(
            "cameraCount=" +
            cameras.Length.ToString(Invariant));
        writer.WriteLine(
            "enabledCameraCount=" +
            enabledCameras.ToString(Invariant));

        MonoBehaviour[] behaviours =
            FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        int spoutCount = 0;
        int enabledSpoutCount = 0;
        int overlayCount = 0;
        int enabledOverlayCount = 0;

        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour =
                behaviours[i];

            if (behaviour == null)
            {
                continue;
            }

            Type type =
                behaviour.GetType();

            string fullName =
                type.FullName ??
                type.Name;

            if (
                fullName.IndexOf(
                    "Spout",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                spoutCount++;

                if (
                    behaviour.enabled &&
                    behaviour.gameObject.activeInHierarchy)
                {
                    enabledSpoutCount++;
                }
            }

            if (
                fullName.IndexOf(
                    "KiwiFrameComparisonOverlay",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                overlayCount++;

                if (
                    behaviour.enabled &&
                    behaviour.gameObject.activeInHierarchy)
                {
                    enabledOverlayCount++;
                }
            }
        }

        writer.WriteLine(
            "spoutBehaviourCount=" +
            spoutCount.ToString(Invariant));
        writer.WriteLine(
            "enabledSpoutBehaviourCount=" +
            enabledSpoutCount.ToString(Invariant));
        writer.WriteLine(
            "overlayBehaviourCount=" +
            overlayCount.ToString(Invariant));
        writer.WriteLine(
            "enabledOverlayBehaviourCount=" +
            enabledOverlayCount.ToString(Invariant));
    }

    private static long CountTriangles(
        Mesh mesh)
    {
        if (mesh == null)
        {
            return 0L;
        }

        long triangles = 0L;

        for (
            int subMesh = 0;
            subMesh < mesh.subMeshCount;
            subMesh++)
        {
            if (
                mesh.GetTopology(subMesh) ==
                    MeshTopology.Triangles)
            {
                triangles +=
                    (long)mesh.GetIndexCount(subMesh) /
                    3L;
            }
        }

        return triangles;
    }

    private static string HierarchyPath(
        Transform transform)
    {
        if (transform == null)
        {
            return "<null>";
        }

        List<string> names =
            new List<string>(16);

        Transform current =
            transform;

        while (current != null)
        {
            names.Add(
                current.name);

            current =
                current.parent;
        }

        names.Reverse();

        return string.Join(
            "/",
            names);
    }

    private static void WriteMetric(
        StreamWriter writer,
        Metric metric)
    {
        if (!metric.IsValid)
        {
            writer.WriteLine(
                metric.category.Name +
                "|" +
                metric.name +
                "|UNAVAILABLE|0|0|0|0|0");
            return;
        }

        if (metric.values.Count == 0)
        {
            writer.WriteLine(
                metric.category.Name +
                "|" +
                metric.name +
                "|" +
                metric.Unit +
                "|0|0|0|0|0");
            return;
        }

        if (
            metric.Unit ==
                ProfilerMarkerDataUnit.TimeNanoseconds)
        {
            writer.WriteLine(
                metric.category.Name +
                "|" +
                metric.name +
                "|ms|" +
                metric.values.Count.ToString(Invariant) +
                "|" +
                MeanMilliseconds(
                    metric.values).ToString("F6", Invariant) +
                "|" +
                PercentileMilliseconds(
                    metric.values,
                    0.50).ToString("F6", Invariant) +
                "|" +
                PercentileMilliseconds(
                    metric.values,
                    0.95).ToString("F6", Invariant) +
                "|" +
                MaxMilliseconds(
                    metric.values).ToString("F6", Invariant));
        }
        else
        {
            double mean =
                metric.values.Average(
                    value =>
                        (double)value);

            long[] sorted =
                metric.values.ToArray();

            Array.Sort(sorted);

            long median =
                PercentileLong(
                    sorted,
                    0.50);

            long p95 =
                PercentileLong(
                    sorted,
                    0.95);

            long max =
                sorted[
                    sorted.Length - 1];

            writer.WriteLine(
                metric.category.Name +
                "|" +
                metric.name +
                "|" +
                metric.Unit +
                "|" +
                metric.values.Count.ToString(Invariant) +
                "|" +
                mean.ToString("F6", Invariant) +
                "|" +
                median.ToString(Invariant) +
                "|" +
                p95.ToString(Invariant) +
                "|" +
                max.ToString(Invariant));
        }
    }

    private static double MeanMilliseconds(
        List<long> values)
    {
        if (
            values == null ||
            values.Count == 0)
        {
            return 0.0;
        }

        double total = 0.0;

        for (int i = 0; i < values.Count; i++)
        {
            total +=
                values[i];
        }

        return
            total /
            values.Count /
            1000000.0;
    }

    private static double MaxMilliseconds(
        List<long> values)
    {
        if (
            values == null ||
            values.Count == 0)
        {
            return 0.0;
        }

        long maximum =
            long.MinValue;

        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] > maximum)
            {
                maximum =
                    values[i];
            }
        }

        return
            maximum /
            1000000.0;
    }

    private static double PercentileMilliseconds(
        List<long> values,
        double percentile)
    {
        if (
            values == null ||
            values.Count == 0)
        {
            return 0.0;
        }

        long[] sorted =
            values.ToArray();

        Array.Sort(sorted);

        return
            PercentileLong(
                sorted,
                percentile) /
            1000000.0;
    }

    private static long PercentileLong(
        long[] sorted,
        double percentile)
    {
        if (
            sorted == null ||
            sorted.Length == 0)
        {
            return 0L;
        }

        double clamped =
            Math.Max(
                0.0,
                Math.Min(
                    1.0,
                    percentile));

        int index =
            (int)Math.Ceiling(
                clamped *
                sorted.Length) -
            1;

        index =
            Math.Max(
                0,
                Math.Min(
                    sorted.Length - 1,
                    index));

        return
            sorted[index];
    }
}
