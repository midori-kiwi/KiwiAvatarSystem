using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

using Mediapipe.Unity;

/// <summary>
/// KiwiAvatarSystem v5.1 Phase 16.20.35 / v44.3 controlled render-isolation diagnostic.
///
/// Goal:
/// - break down the current Windows DX12 Development Standalone frame cost;
/// - preserve tracking, thresholds, ROI, provider authority, Root, FaceParts,
///   Native Camera Path B, inference policy, presentation, Spout and preview;
/// - use ProfilerRecorder only (no Profiler connection, no frame CSV, no MP4);
/// - collect known Unity/D3D12 markers proven available in v30-v32;
/// - record scene-render inventory without changing any component state.
///
/// Environment:
///   KIWI_WORKLOAD_BREAKDOWN_PROBE=1
///
/// Recommended companion environment:
///   KIWI_CAMERA_CAPTURE_TRANSPORT=B
///   KIWI_ORT_DML_ZERO_COPY_SHADOW=0
///   KIWI_ORT_DML_SHADOW=0
///   KIWI_INFERENCE_ASYNC_COMPUTE_PROBE=0
///   KIWI_INFERENCE_CADENCE_BUDGET_HZ=0
///   KIWI_CADENCE_NODIAG_PROBE=0
/// </summary>
[DefaultExecutionOrder(-31890)]
[DisallowMultipleComponent]
public sealed class KiwiStandaloneWorkloadBreakdownProbe : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Standalone Workload Breakdown Probe";

    private const string EnableEnvironment =
        "KIWI_WORKLOAD_BREAKDOWN_PROBE";

    private const string AvatarRenderOffEnvironment =
        "KIWI_WORKLOAD_AVATAR_RENDER_OFF";

    private const string Contract =
        "KIWI_V5_1_PHASE16_20_35_V44_3_AVATAR_RENDER_ISOLATION";

    private const double SetupDelaySeconds = 3.0;
    private const double WarmupAfterSetupSeconds = 7.0;
    private const double MeasureSeconds = 30.0;
    private const double IsolationSettleSeconds = 3.0;
    private const double SetupTimeoutSeconds = 25.0;

    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private static readonly MetricSpec[] RequestedMetrics =
    {
        new MetricSpec("CPU Main Thread Frame Time", "Render"),
        new MetricSpec("CPU Render Thread Frame Time", "Render"),
        new MetricSpec("CPU Total Frame Time", "Render"),
        new MetricSpec("CPU Main Thread Present Wait Time", "Render"),
        new MetricSpec("GPU Frame Time", "Render"),
        new MetricSpec("PlayerLoop", "PlayerLoop"),
        new MetricSpec("Update.ScriptRunBehaviourUpdate", "PlayerLoop"),
        new MetricSpec("PreLateUpdate.ScriptRunBehaviourLateUpdate", "PlayerLoop"),
        new MetricSpec("PostLateUpdate.UpdateAllRenderers", "PlayerLoop"),
        new MetricSpec("PostLateUpdate.UpdateAllSkinnedMeshes", "PlayerLoop"),
        new MetricSpec("Camera.Render", "Render"),
        new MetricSpec("RenderLoop", "Render"),
        new MetricSpec("RenderLoop.Draw", "Render"),
        new MetricSpec("Drawing", "Render"),
        new MetricSpec("Render.Mesh", "Render"),
        new MetricSpec("MeshRenderer.Render", "Render"),
        new MetricSpec("MeshSkinning.SkinOnGPU", "Render"),
        new MetricSpec("Graphics.Blit", "Render"),
        new MetricSpec("Compute.Dispatch", "Render"),
        new MetricSpec("Gfx.UploadTextureData", "Render"),
        new MetricSpec("Gfx.UploadTexture", "Render"),
        new MetricSpec("Gfx.CopyTextureData", "Render"),
        new MetricSpec("GfxDeviceD3D12.ExecuteCommandList", "Render"),
        new MetricSpec("GfxDeviceD3D12.FinishRendering", "Render"),
        new MetricSpec("GfxDeviceD3D12.FinishRenderingWithMessagePump", "Render"),
        new MetricSpec("GfxDeviceD3D12.WaitForGPU", "Render"),
        new MetricSpec("GfxDeviceD3D12.WaitForLastPresentation", "Render"),
        new MetricSpec("GfxDeviceD3D12.WaitForLastPresentation.WaitForGPU", "Render"),
        new MetricSpec("GfxDeviceD3D12.WaitForLastPresent", "VSync"),
        new MetricSpec("GfxDeviceD3D12.Swap", "Render"),
        new MetricSpec("IDXGISwapChain::Present", "Render"),
        new MetricSpec("GfxTask_PluginEventAndData", "Render"),
        new MetricSpec("GfxTask_WaitOnGpuFence", "Render"),
        new MetricSpec("Gfx.WaitForGfxCommandWriteSpace", "Internal"),
        new MetricSpec("Gfx.WaitForGfxCommandsFromMainThread", "Internal"),
        new MetricSpec("Gfx.WaitForPresentOnGfxThread", "Render"),
        new MetricSpec("Gfx.WaitForRenderThread", "Render"),
        new MetricSpec("WaitForTargetFPS", "VSync"),
        new MetricSpec("Gfx.PresentFrame", "Render"),
        new MetricSpec("Canvas.BuildBatch", "UI Render"),
        new MetricSpec("GUI.Repaint", "Gui"),
        new MetricSpec("TextCoreRendering.Render", "Gui"),
        new MetricSpec("Batches Count", "Render"),
        new MetricSpec("Draw Calls Count", "Render")
    };

    private static KiwiStandaloneWorkloadBreakdownProbe _instance;

    private readonly List<MetricRuntime> _metrics =
        new List<MetricRuntime>(RequestedMetrics.Length);

    private double _createdRealtime;
    private double _configuredRealtime;
    private double _measureStartRealtime;
    private int _measureStartFrame;
    private bool _configured;
    private bool _started;
    private bool _completed;
    private CounterSnapshot _begin;
    private RenderInventory _inventory;
    private bool _avatarRenderOff;
    private bool _isolationApplied;
    private double _isolationAppliedRealtime;
    private string _isolatedRendererName = string.Empty;
    private long _isolatedRendererTriangles;
    private bool _isolatedRendererOriginalForceRenderingOff;
    private SkinnedMeshRenderer _isolatedRenderer;

    private struct MetricSpec
    {
        public readonly string Name;
        public readonly string PreferredCategory;

        public MetricSpec(
            string name,
            string preferredCategory)
        {
            Name = name;
            PreferredCategory = preferredCategory;
        }
    }

    private sealed class MetricRuntime
    {
        public string name;
        public string category;
        public string unit;
        public ProfilerRecorder recorder;
        public readonly List<double> samples =
            new List<double>(2048);
    }

    private struct CounterSnapshot
    {
        public ulong capture;
        public ulong dropped;
        public ulong presented;
        public long scheduled;
        public long readback;
        public long completed;
        public long presenceRejected;
        public long invalidRejected;
        public long droppedFresh;
        public long discardedStale;
        public long discardedCrossSystem;
        public long canonicalAdoption;
    }

    private struct RenderInventory
    {
        public int rendererCount;
        public int enabledRendererCount;
        public int meshRendererCount;
        public int enabledMeshRendererCount;
        public int skinnedRendererCount;
        public int enabledSkinnedRendererCount;
        public long enabledRendererTriangles;
        public int canvasCount;
        public int enabledCanvasCount;
        public int cameraCount;
        public int enabledCameraCount;
        public int spoutBehaviourCount;
        public int enabledSpoutBehaviourCount;
        public int previewBehaviourCount;
        public int enabledPreviewBehaviourCount;
        public int overlayBehaviourCount;
        public int enabledOverlayBehaviourCount;
        public int surfaceBakedColliderCount;
        public long surfaceBakedColliderTriangles;
        public int surfaceBakedRenderedCount;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        if (!IsEnabled(EnableEnvironment))
        {
            return;
        }

        if (
            Application.platform != RuntimePlatform.WindowsPlayer ||
            !Debug.isDebugBuild)
        {
            Debug.LogWarning(
                "[KiwiWorkloadBreakdown] ignored: " +
                "Development Windows Player is required.");
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiStandaloneWorkloadBreakdownProbe>();
    }

    private static bool IsEnabled(
        string environmentName)
    {
        string value =
            Environment.GetEnvironmentVariable(
                environmentName);

        return
            string.Equals(
                value,
                "1",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                value,
                "true",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                value,
                "on",
                StringComparison.OrdinalIgnoreCase);
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);

        _createdRealtime =
            Time.realtimeSinceStartupAsDouble;

        _avatarRenderOff =
            IsEnabled(AvatarRenderOffEnvironment);

        Debug.Log(
            "[KiwiWorkloadBreakdown] contract=" +
            Contract +
            " requested=1 setupDelaySeconds=" +
            SetupDelaySeconds.ToString("F1", Invariant) +
            " warmupAfterSetupSeconds=" +
            WarmupAfterSetupSeconds.ToString("F1", Invariant) +
            " measureSeconds=" +
            MeasureSeconds.ToString("F1", Invariant) +
            " mode=" +
            (_avatarRenderOff ? "AVATAR_RENDER_OFF" : "BASELINE"));
    }

    private void Update()
    {
        if (_completed)
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        if (!_configured)
        {
            if (
                now - _createdRealtime <
                SetupDelaySeconds)
            {
                return;
            }

            TryConfigure(now);
            return;
        }

        if (!_started)
        {
            if (
                now - _configuredRealtime <
                WarmupAfterSetupSeconds)
            {
                return;
            }

            if (_avatarRenderOff && !_isolationApplied)
            {
                if (!TryApplyAvatarRenderIsolation())
                {
                    CompleteSetupFailure(
                        "No eligible active SkinnedMeshRenderer was found for avatar render isolation.");
                    return;
                }

                _isolationApplied = true;
                _isolationAppliedRealtime = now;

                Debug.Log(
                    "[KiwiWorkloadBreakdown] AVATAR_RENDER_OFF applied renderer='" +
                    _isolatedRendererName +
                    "' trianglesApprox=" +
                    _isolatedRendererTriangles);

                return;
            }

            if (
                _avatarRenderOff &&
                now - _isolationAppliedRealtime <
                IsolationSettleSeconds)
            {
                return;
            }

            if (!TryReadCounters(out _begin))
            {
                if (
                    now - _createdRealtime >=
                    SetupTimeoutSeconds)
                {
                    CompleteSetupFailure(
                        "Native camera telemetry was not active.");
                }

                return;
            }

            _inventory =
                CaptureRenderInventory();

            ResetMetricSamples();

            _measureStartRealtime = now;
            _measureStartFrame = Time.frameCount;
            _started = true;

            Debug.Log(
                "[KiwiWorkloadBreakdown] BEGIN frame=" +
                _measureStartFrame +
                " metrics=" +
                _metrics.Count +
                " capture=" +
                _begin.capture +
                " presented=" +
                _begin.presented +
                " scheduled=" +
                _begin.scheduled);

            return;
        }

        SampleMetrics();

        if (
            now - _measureStartRealtime <
            MeasureSeconds)
        {
            return;
        }

        if (!TryReadCounters(out CounterSnapshot end))
        {
            return;
        }

        CompleteMeasurement(
            now,
            end);
    }

    private void TryConfigure(
        double now)
    {
        try
        {
            ConfigureMetricRecorders();

            if (_metrics.Count == 0)
            {
                CompleteSetupFailure(
                    "No requested ProfilerRecorder metrics were available.");
                return;
            }

            _configured = true;
            _configuredRealtime = now;

            Debug.Log(
                "[KiwiWorkloadBreakdown] configured metrics=" +
                _metrics.Count +
                "/" +
                RequestedMetrics.Length +
                " frameTimingFeature=" +
                (FrameTimingManager.IsFeatureEnabled() ? "1" : "0") +
                " gpuRecorderSupport=" +
                (SystemInfo.supportsGpuRecorder ? "1" : "0"));
        }
        catch (Exception ex)
        {
            CompleteSetupFailure(
                "ProfilerRecorder setup failed: " +
                ex.Message);
        }
    }

    private void ConfigureMetricRecorders()
    {
        DisposeMetrics();
        _metrics.Clear();

        List<ProfilerRecorderHandle> handles =
            new List<ProfilerRecorderHandle>();

        ProfilerRecorderHandle.GetAvailable(
            handles);

        List<AvailableMetric> available =
            new List<AvailableMetric>(
                handles.Count);

        foreach (ProfilerRecorderHandle handle in handles)
        {
            if (!handle.Valid)
            {
                continue;
            }

            ProfilerRecorderDescription description =
                ProfilerRecorderHandle.GetDescription(
                    handle);

            available.Add(
                new AvailableMetric(
                    handle,
                    description.Name,
                    description.Category.ToString(),
                    description.UnitType.ToString()));
        }

        foreach (MetricSpec spec in RequestedMetrics)
        {
            AvailableMetric? match =
                FindBestAvailableMetric(
                    available,
                    spec);

            if (!match.HasValue)
            {
                continue;
            }

            AvailableMetric selected =
                match.Value;

            ProfilerRecorder recorder =
                new ProfilerRecorder(
                    selected.handle,
                    1,
                    ProfilerRecorderOptions.Default |
                    ProfilerRecorderOptions.StartImmediately);

            if (!recorder.Valid)
            {
                recorder.Dispose();
                continue;
            }

            MetricRuntime metric =
                new MetricRuntime
                {
                    name = selected.name,
                    category = selected.category,
                    unit = selected.unit,
                    recorder = recorder
                };

            _metrics.Add(metric);
        }
    }

    private readonly struct AvailableMetric
    {
        public readonly ProfilerRecorderHandle handle;
        public readonly string name;
        public readonly string category;
        public readonly string unit;

        public AvailableMetric(
            ProfilerRecorderHandle handle,
            string name,
            string category,
            string unit)
        {
            this.handle = handle;
            this.name = name;
            this.category = category;
            this.unit = unit;
        }
    }

    private static AvailableMetric? FindBestAvailableMetric(
        List<AvailableMetric> available,
        MetricSpec spec)
    {
        AvailableMetric? fallback = null;

        for (int i = 0; i < available.Count; i++)
        {
            AvailableMetric candidate =
                available[i];

            if (!string.Equals(
                    candidate.name,
                    spec.Name,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (
                string.Equals(
                    candidate.category,
                    spec.PreferredCategory,
                    StringComparison.Ordinal))
            {
                return candidate;
            }

            if (!fallback.HasValue)
            {
                fallback = candidate;
            }
        }

        return fallback;
    }

    private void ResetMetricSamples()
    {
        for (int i = 0; i < _metrics.Count; i++)
        {
            _metrics[i].samples.Clear();
        }
    }

    private void SampleMetrics()
    {
        for (int i = 0; i < _metrics.Count; i++)
        {
            MetricRuntime metric =
                _metrics[i];

            ProfilerRecorder recorder =
                metric.recorder;

            if (
                !recorder.Valid ||
                recorder.Count <= 0)
            {
                continue;
            }

            double value =
                recorder.LastValueAsDouble;

            if (
                double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value < 0.0)
            {
                continue;
            }

            if (
                IsTimeNanosecondsUnit(
                    metric.unit))
            {
                value *= 0.000001;
            }

            metric.samples.Add(value);
        }
    }

    private static bool IsTimeNanosecondsUnit(
        string unit)
    {
        return
            unit.IndexOf(
                "TimeNanoseconds",
                StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool TryReadCounters(
        out CounterSnapshot snapshot)
    {
        snapshot = default;

        if (
            !KiwiNativeCameraTelemetry.TryGetSnapshot(
                out KiwiNativeCameraTelemetrySnapshot camera) ||
            !camera.active)
        {
            return false;
        }

        snapshot.capture =
            camera.captureFrameCount;
        snapshot.dropped =
            camera.droppedFrameCount;
        snapshot.presented =
            camera.presentedFrameCount;

        snapshot.scheduled =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceScheduledCount;
        snapshot.readback =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceReadbackCompletedCount;
        snapshot.completed =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceCompletedCount;
        snapshot.presenceRejected =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceRejectedPresenceCount;
        snapshot.invalidRejected =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceRejectedInvalidCount;
        snapshot.droppedFresh =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceDroppedFreshCount;
        snapshot.discardedStale =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceDiscardedStaleCount;
        snapshot.discardedCrossSystem =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceDiscardedCrossSystemCount;
        snapshot.canonicalAdoption =
            KiwiCommercialCadencePipelineTelemetry
                .AdoptionCount;

        return true;
    }

    private bool TryApplyAvatarRenderIsolation()
    {
        SkinnedMeshRenderer[] renderers =
            FindObjectsByType<SkinnedMeshRenderer>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        SkinnedMeshRenderer best = null;
        long bestTriangles = -1L;

        for (int i = 0; i < renderers.Length; i++)
        {
            SkinnedMeshRenderer renderer = renderers[i];

            if (
                renderer == null ||
                !renderer.enabled ||
                !renderer.gameObject.activeInHierarchy ||
                renderer.forceRenderingOff ||
                renderer.sharedMesh == null)
            {
                continue;
            }

            long triangles =
                EstimateTriangleCount(renderer.sharedMesh);

            if (triangles > bestTriangles)
            {
                best = renderer;
                bestTriangles = triangles;
            }
        }

        if (best == null)
        {
            return false;
        }

        _isolatedRenderer = best;
        _isolatedRendererOriginalForceRenderingOff =
            best.forceRenderingOff;
        _isolatedRendererName =
            best.gameObject.name + " [" +
            best.GetType().Name + "]";
        _isolatedRendererTriangles =
            Math.Max(0L, bestTriangles);

        best.forceRenderingOff = true;
        return true;
    }

    private void RestoreAvatarRenderIsolation()
    {
        if (_isolatedRenderer == null)
        {
            return;
        }

        try
        {
            _isolatedRenderer.forceRenderingOff =
                _isolatedRendererOriginalForceRenderingOff;

            Debug.Log(
                "[KiwiWorkloadBreakdown] avatar render isolation restored renderer='" +
                _isolatedRendererName + "'.");
        }
        catch
        {
        }

        _isolatedRenderer = null;
    }

    private static RenderInventory CaptureRenderInventory()
    {
        RenderInventory result =
            default;

        Renderer[] renderers =
            FindObjectsByType<Renderer>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        HashSet<Mesh> enabledMeshes =
            new HashSet<Mesh>();

        HashSet<Mesh> surfaceMeshes =
            new HashSet<Mesh>();

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer =
                renderers[i];

            if (renderer == null)
            {
                continue;
            }

            result.rendererCount++;

            bool enabled =
                renderer.enabled &&
                renderer.gameObject.activeInHierarchy;

            if (enabled)
            {
                result.enabledRendererCount++;
            }

            Mesh mesh = null;

            if (renderer is MeshRenderer)
            {
                result.meshRendererCount++;

                if (enabled)
                {
                    result.enabledMeshRendererCount++;
                }

                MeshFilter filter =
                    renderer.GetComponent<MeshFilter>();

                if (filter != null)
                {
                    mesh = filter.sharedMesh;
                }
            }
            else if (
                renderer is SkinnedMeshRenderer skinned)
            {
                result.skinnedRendererCount++;

                if (enabled)
                {
                    result.enabledSkinnedRendererCount++;
                }

                mesh = skinned.sharedMesh;
            }

            if (
                enabled &&
                mesh != null &&
                enabledMeshes.Add(mesh))
            {
                result.enabledRendererTriangles +=
                    EstimateTriangleCount(mesh);
            }

            if (
                mesh != null &&
                string.Equals(
                    mesh.name,
                    "__KiwiSurfaceBakedMesh",
                    StringComparison.Ordinal))
            {
                result.surfaceBakedRenderedCount++;
                surfaceMeshes.Add(mesh);
            }
        }

        MeshCollider[] colliders =
            FindObjectsByType<MeshCollider>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        for (int i = 0; i < colliders.Length; i++)
        {
            MeshCollider collider =
                colliders[i];

            if (
                collider == null ||
                collider.sharedMesh == null ||
                !string.Equals(
                    collider.sharedMesh.name,
                    "__KiwiSurfaceBakedMesh",
                    StringComparison.Ordinal))
            {
                continue;
            }

            result.surfaceBakedColliderCount++;

            if (surfaceMeshes.Add(collider.sharedMesh))
            {
                result.surfaceBakedColliderTriangles +=
                    EstimateTriangleCount(
                        collider.sharedMesh);
            }
        }

        Canvas[] canvases =
            FindObjectsByType<Canvas>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        result.canvasCount =
            canvases.Length;

        for (int i = 0; i < canvases.Length; i++)
        {
            if (
                canvases[i] != null &&
                canvases[i].enabled &&
                canvases[i].gameObject.activeInHierarchy)
            {
                result.enabledCanvasCount++;
            }
        }

        Camera[] cameras =
            FindObjectsByType<Camera>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        result.cameraCount =
            cameras.Length;

        for (int i = 0; i < cameras.Length; i++)
        {
            if (
                cameras[i] != null &&
                cameras[i].enabled &&
                cameras[i].gameObject.activeInHierarchy)
            {
                result.enabledCameraCount++;
            }
        }

        MonoBehaviour[] behaviours =
            FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

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

            string name =
                type.FullName ?? type.Name;

            bool enabled =
                behaviour.enabled &&
                behaviour.gameObject.activeInHierarchy;

            if (
                name.IndexOf(
                    "Spout",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.spoutBehaviourCount++;

                if (enabled)
                {
                    result.enabledSpoutBehaviourCount++;
                }
            }

            if (
                name.IndexOf(
                    "Preview",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.previewBehaviourCount++;

                if (enabled)
                {
                    result.enabledPreviewBehaviourCount++;
                }
            }

            if (
                name.IndexOf(
                    "Overlay",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.overlayBehaviourCount++;

                if (enabled)
                {
                    result.enabledOverlayBehaviourCount++;
                }
            }
        }

        return result;
    }

    private static long EstimateTriangleCount(
        Mesh mesh)
    {
        if (mesh == null)
        {
            return 0L;
        }

        long indexCount = 0L;

        try
        {
            int subMeshCount =
                mesh.subMeshCount;

            for (int i = 0; i < subMeshCount; i++)
            {
                MeshTopology topology =
                    mesh.GetTopology(i);

                if (
                    topology != MeshTopology.Triangles)
                {
                    continue;
                }

                indexCount +=
                    (long)mesh.GetIndexCount(i);
            }
        }
        catch
        {
            return 0L;
        }

        return indexCount / 3L;
    }

    private void CompleteMeasurement(
        double now,
        CounterSnapshot end)
    {
        double duration =
            Math.Max(
                0.001,
                now - _measureStartRealtime);

        int renderFrames =
            Math.Max(
                0,
                Time.frameCount -
                _measureStartFrame);

        string result =
            BuildResultText(
                duration,
                renderFrames,
                end);

        WriteResultFile(result);

        RestoreAvatarRenderIsolation();

        Debug.Log(
            "[KiwiWorkloadBreakdown] RESULT " +
            FlattenResultForLog(
                BuildCompactLogSummary(
                    duration,
                    renderFrames,
                    end)));

        DisposeMetrics();

        _completed = true;
    }

    private string BuildResultText(
        double duration,
        int renderFrames,
        CounterSnapshot end)
    {
        ulong captureDelta =
            Delta(end.capture, _begin.capture);
        ulong droppedDelta =
            Delta(end.dropped, _begin.dropped);
        ulong presentedDelta =
            Delta(end.presented, _begin.presented);

        long scheduledDelta =
            Delta(end.scheduled, _begin.scheduled);
        long readbackDelta =
            Delta(end.readback, _begin.readback);
        long completedDelta =
            Delta(end.completed, _begin.completed);
        long presenceRejectedDelta =
            Delta(
                end.presenceRejected,
                _begin.presenceRejected);
        long invalidRejectedDelta =
            Delta(
                end.invalidRejected,
                _begin.invalidRejected);
        long droppedFreshDelta =
            Delta(
                end.droppedFresh,
                _begin.droppedFresh);
        long discardedStaleDelta =
            Delta(
                end.discardedStale,
                _begin.discardedStale);
        long discardedCrossSystemDelta =
            Delta(
                end.discardedCrossSystem,
                _begin.discardedCrossSystem);
        long canonicalDelta =
            Delta(
                end.canonicalAdoption,
                _begin.canonicalAdoption);

        StringBuilder sb =
            new StringBuilder(16384);

        sb.AppendLine(
            "KiwiAvatarSystem v44.2 Standalone Workload Breakdown");
        Append(sb, "contract", Contract);
        Append(sb, "durationSeconds", duration);
        Append(sb, "renderFrames", renderFrames);
        Append(sb, "renderHz", renderFrames / duration);

        Append(
            sb,
            "unityVersion",
            Application.unityVersion);
        Append(
            sb,
            "graphicsDevice",
            SystemInfo.graphicsDeviceName);
        Append(
            sb,
            "graphicsApi",
            SystemInfo.graphicsDeviceType.ToString());
        Append(
            sb,
            "supportsGpuRecorder",
            SystemInfo.supportsGpuRecorder ? 1L : 0L);
        Append(
            sb,
            "frameTimingFeatureEnabled",
            FrameTimingManager.IsFeatureEnabled() ? 1L : 0L);
        Append(
            sb,
            "screen",
            UnityEngine.Screen.width + "x" + UnityEngine.Screen.height);
        Append(
            sb,
            "vSyncCount",
            QualitySettings.vSyncCount);
        Append(
            sb,
            "targetFrameRate",
            Application.targetFrameRate);
        Append(
            sb,
            "maxQueuedFrames",
            QualitySettings.maxQueuedFrames);
        Append(
            sb,
            "mode",
            _avatarRenderOff ? "AVATAR_RENDER_OFF" : "BASELINE");
        Append(
            sb,
            "isolatedRenderer",
            _isolatedRendererName);
        Append(
            sb,
            "isolatedRendererTrianglesApprox",
            _isolatedRendererTriangles);
        Append(
            sb,
            "isolatedRendererForceRenderingOff",
            _avatarRenderOff ? 1L : 0L);

        sb.AppendLine();
        sb.AppendLine("[CADENCE]");
        Append(sb, "captureDelta", captureDelta);
        Append(sb, "captureHz", captureDelta / duration);
        Append(sb, "cameraDroppedDelta", droppedDelta);
        Append(sb, "presentedDelta", presentedDelta);
        Append(sb, "presentedHz", presentedDelta / duration);
        Append(sb, "scheduledDelta", scheduledDelta);
        Append(sb, "scheduledHz", scheduledDelta / duration);
        Append(sb, "readbackDelta", readbackDelta);
        Append(sb, "readbackHz", readbackDelta / duration);
        Append(sb, "completedDelta", completedDelta);
        Append(sb, "completedHz", completedDelta / duration);
        Append(
            sb,
            "presenceRejectedDelta",
            presenceRejectedDelta);
        Append(
            sb,
            "presenceRejectedHz",
            presenceRejectedDelta / duration);
        Append(
            sb,
            "invalidRejectedDelta",
            invalidRejectedDelta);
        Append(
            sb,
            "droppedFreshDelta",
            droppedFreshDelta);
        Append(
            sb,
            "discardedStaleDelta",
            discardedStaleDelta);
        Append(
            sb,
            "discardedCrossSystemDelta",
            discardedCrossSystemDelta);
        Append(sb, "canonicalDelta", canonicalDelta);
        Append(sb, "canonicalHz", canonicalDelta / duration);

        sb.AppendLine();
        sb.AppendLine("[RENDER_INVENTORY]");
        Append(
            sb,
            "rendererCount",
            _inventory.rendererCount);
        Append(
            sb,
            "enabledRendererCount",
            _inventory.enabledRendererCount);
        Append(
            sb,
            "meshRendererCount",
            _inventory.meshRendererCount);
        Append(
            sb,
            "enabledMeshRendererCount",
            _inventory.enabledMeshRendererCount);
        Append(
            sb,
            "skinnedRendererCount",
            _inventory.skinnedRendererCount);
        Append(
            sb,
            "enabledSkinnedRendererCount",
            _inventory.enabledSkinnedRendererCount);
        Append(
            sb,
            "uniqueEnabledRendererTrianglesApprox",
            _inventory.enabledRendererTriangles);
        Append(
            sb,
            "canvasCount",
            _inventory.canvasCount);
        Append(
            sb,
            "enabledCanvasCount",
            _inventory.enabledCanvasCount);
        Append(
            sb,
            "cameraCount",
            _inventory.cameraCount);
        Append(
            sb,
            "enabledCameraCount",
            _inventory.enabledCameraCount);
        Append(
            sb,
            "spoutBehaviourCount",
            _inventory.spoutBehaviourCount);
        Append(
            sb,
            "enabledSpoutBehaviourCount",
            _inventory.enabledSpoutBehaviourCount);
        Append(
            sb,
            "previewBehaviourCount",
            _inventory.previewBehaviourCount);
        Append(
            sb,
            "enabledPreviewBehaviourCount",
            _inventory.enabledPreviewBehaviourCount);
        Append(
            sb,
            "overlayBehaviourCount",
            _inventory.overlayBehaviourCount);
        Append(
            sb,
            "enabledOverlayBehaviourCount",
            _inventory.enabledOverlayBehaviourCount);
        Append(
            sb,
            "surfaceBakedColliderCount",
            _inventory.surfaceBakedColliderCount);
        Append(
            sb,
            "surfaceBakedColliderTrianglesApprox",
            _inventory.surfaceBakedColliderTriangles);
        Append(
            sb,
            "surfaceBakedRenderedCount",
            _inventory.surfaceBakedRenderedCount);

        sb.AppendLine();
        sb.AppendLine("[PROFILER_METRICS]");
        sb.AppendLine(
            "format=category|name|unit|samples|mean|median|p95|max");
        sb.AppendLine(
            "note=Profiler markers are nested; values are diagnostic and MUST NOT be added together.");

        List<MetricSummary> summaries =
            new List<MetricSummary>(
                _metrics.Count);

        for (int i = 0; i < _metrics.Count; i++)
        {
            MetricSummary summary =
                SummarizeMetric(
                    _metrics[i]);

            summaries.Add(summary);

            sb.Append(summary.category);
            sb.Append('|');
            sb.Append(summary.name);
            sb.Append('|');
            sb.Append(summary.outputUnit);
            sb.Append('|');
            sb.Append(summary.count.ToString(Invariant));
            sb.Append('|');
            sb.Append(summary.mean.ToString("F6", Invariant));
            sb.Append('|');
            sb.Append(summary.median.ToString("F6", Invariant));
            sb.Append('|');
            sb.Append(summary.p95.ToString("F6", Invariant));
            sb.Append('|');
            sb.AppendLine(summary.max.ToString("F6", Invariant));
        }

        sb.AppendLine();
        sb.AppendLine("[TOP_TIMING_MARKERS_BY_P95]");
        sb.AppendLine(
            "note=nested markers are intentionally ranked but not summed.");

        foreach (
            MetricSummary summary in summaries
                .Where(x => x.isTiming && x.count > 0)
                .OrderByDescending(x => x.p95)
                .Take(16))
        {
            sb.Append(summary.category);
            sb.Append('|');
            sb.Append(summary.name);
            sb.Append("|p95ms=");
            sb.Append(summary.p95.ToString("F6", Invariant));
            sb.Append("|medianms=");
            sb.Append(summary.median.ToString("F6", Invariant));
            sb.Append("|meanms=");
            sb.AppendLine(summary.mean.ToString("F6", Invariant));
        }

        sb.AppendLine();
        sb.AppendLine("[UNAVAILABLE_REQUESTED]");
        HashSet<string> availableNames =
            new HashSet<string>(
                _metrics.Select(x => x.name),
                StringComparer.Ordinal);

        for (int i = 0; i < RequestedMetrics.Length; i++)
        {
            if (!availableNames.Contains(RequestedMetrics[i].Name))
            {
                sb.AppendLine(
                    RequestedMetrics[i].PreferredCategory +
                    "|" +
                    RequestedMetrics[i].Name);
            }
        }

        sb.AppendLine();
        sb.AppendLine(
            "note=controlled A/B; only Renderer.forceRenderingOff may be temporarily written in AVATAR_RENDER_OFF mode; no tracking/threshold/ROI/authority/camera/inference/presentation writes");

        return sb.ToString();
    }

    private string BuildCompactLogSummary(
        double duration,
        int renderFrames,
        CounterSnapshot end)
    {
        ulong captureDelta =
            Delta(end.capture, _begin.capture);
        ulong presentedDelta =
            Delta(end.presented, _begin.presented);
        long scheduledDelta =
            Delta(end.scheduled, _begin.scheduled);

        MetricSummary main =
            FindSummary(
                "CPU Main Thread Frame Time");
        MetricSummary render =
            FindSummary(
                "CPU Render Thread Frame Time");
        MetricSummary gpu =
            FindSummary(
                "GPU Frame Time");
        MetricSummary present =
            FindSummary(
                "Gfx.PresentFrame");

        return
            "renderHz=" +
            (renderFrames / duration).ToString("F3", Invariant) +
            " captureHz=" +
            (captureDelta / duration).ToString("F3", Invariant) +
            " presentedHz=" +
            (presentedDelta / duration).ToString("F3", Invariant) +
            " scheduledHz=" +
            (scheduledDelta / duration).ToString("F3", Invariant) +
            " mainMedianMs=" +
            main.median.ToString("F3", Invariant) +
            " renderThreadMedianMs=" +
            render.median.ToString("F3", Invariant) +
            " gpuMedianMs=" +
            gpu.median.ToString("F3", Invariant) +
            " presentMedianMs=" +
            present.median.ToString("F3", Invariant);
    }

    private MetricSummary FindSummary(
        string name)
    {
        for (int i = 0; i < _metrics.Count; i++)
        {
            if (
                string.Equals(
                    _metrics[i].name,
                    name,
                    StringComparison.Ordinal))
            {
                return
                    SummarizeMetric(
                        _metrics[i]);
            }
        }

        return default;
    }

    private struct MetricSummary
    {
        public string name;
        public string category;
        public string outputUnit;
        public int count;
        public double mean;
        public double median;
        public double p95;
        public double max;
        public bool isTiming;
    }

    private static MetricSummary SummarizeMetric(
        MetricRuntime metric)
    {
        MetricSummary result =
            new MetricSummary
            {
                name = metric.name,
                category = metric.category,
                outputUnit =
                    IsTimeNanosecondsUnit(metric.unit)
                        ? "ms"
                        : metric.unit,
                count = metric.samples.Count,
                isTiming =
                    IsTimeNanosecondsUnit(metric.unit)
            };

        if (metric.samples.Count == 0)
        {
            return result;
        }

        double[] values =
            metric.samples.ToArray();

        Array.Sort(values);

        double sum = 0.0;
        double max = 0.0;

        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];

            if (values[i] > max)
            {
                max = values[i];
            }
        }

        result.mean =
            sum / values.Length;
        result.median =
            PercentileSorted(
                values,
                0.50);
        result.p95 =
            PercentileSorted(
                values,
                0.95);
        result.max =
            max;

        return result;
    }

    private static double PercentileSorted(
        double[] sorted,
        double percentile)
    {
        if (
            sorted == null ||
            sorted.Length == 0)
        {
            return 0.0;
        }

        double position =
            (sorted.Length - 1) *
            Math.Max(
                0.0,
                Math.Min(
                    1.0,
                    percentile));

        int lower =
            (int)Math.Floor(position);
        int upper =
            (int)Math.Ceiling(position);

        if (lower == upper)
        {
            return sorted[lower];
        }

        double t =
            position - lower;

        return
            sorted[lower] +
            ((sorted[upper] - sorted[lower]) * t);
    }

    private void WriteResultFile(
        string result)
    {
        try
        {
            string directory =
                Path.Combine(
                    Application.persistentDataPath,
                    "KiwiStandaloneWorkloadBreakdown");

            Directory.CreateDirectory(directory);

            string path =
                Path.Combine(
                    directory,
                    "KiwiStandaloneWorkloadBreakdown_" +
                    DateTime.Now.ToString(
                        "yyyyMMdd_HHmmss",
                        Invariant) +
                    ".txt");

            File.WriteAllText(
                path,
                result,
                new UTF8Encoding(false));

            Debug.Log(
                "[KiwiWorkloadBreakdown] resultPath=" +
                path);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[KiwiWorkloadBreakdown] result write failed: " +
                ex.Message);
        }
    }

    private void CompleteSetupFailure(
        string reason)
    {
        string result =
            "KiwiAvatarSystem v44.2 Standalone Workload Breakdown\n" +
            "contract=" + Contract + "\n" +
            "result=SETUP_FAIL\n" +
            "reason=" + reason + "\n";

        Debug.LogError(
            "[KiwiWorkloadBreakdown] " +
            FlattenResultForLog(result));

        WriteResultFile(
            result);

        DisposeMetrics();

        _completed = true;
    }

    private void DisposeMetrics()
    {
        for (int i = 0; i < _metrics.Count; i++)
        {
            try
            {
                _metrics[i].recorder.Dispose();
            }
            catch
            {
            }
        }
    }

    private void OnDestroy()
    {
        RestoreAvatarRenderIsolation();
        DisposeMetrics();

        if (_instance == this)
        {
            _instance = null;
        }
    }

    private static ulong Delta(
        ulong end,
        ulong begin)
    {
        return end >= begin
            ? end - begin
            : 0UL;
    }

    private static long Delta(
        long end,
        long begin)
    {
        return end >= begin
            ? end - begin
            : 0L;
    }

    private static string FlattenResultForLog(
        string result)
    {
        return result
            .Replace("\r", string.Empty)
            .Replace("\n", " | ")
            .TrimEnd(' ', '|');
    }

    private static void Append(
        StringBuilder sb,
        string name,
        string value)
    {
        sb.Append(name);
        sb.Append('=');
        sb.AppendLine(
            value ?? string.Empty);
    }

    private static void Append(
        StringBuilder sb,
        string name,
        int value)
    {
        Append(
            sb,
            name,
            value.ToString(Invariant));
    }

    private static void Append(
        StringBuilder sb,
        string name,
        long value)
    {
        Append(
            sb,
            name,
            value.ToString(Invariant));
    }

    private static void Append(
        StringBuilder sb,
        string name,
        ulong value)
    {
        Append(
            sb,
            name,
            value.ToString(Invariant));
    }

    private static void Append(
        StringBuilder sb,
        string name,
        double value)
    {
        Append(
            sb,
            name,
            value.ToString("F6", Invariant));
    }
}
