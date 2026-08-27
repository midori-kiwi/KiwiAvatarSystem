using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

/// <summary>
/// KiwiAvatarSystem Phase16.20.20 v32 Editor Isolation A/B Exact Marker Probe.
///
/// Observer-only.
/// No Native camera / tracker / inference / FaceTexture / FacePart / avatar /
/// preview behavior is modified.
///
/// Enable:
///   KIWI_EDITOR_ISOLATION_PROBE=1
///
/// Label:
///   KIWI_EDITOR_LAYOUT_CASE=NORMAL|MAXIMIZED
///
/// Output:
///   <ProjectRoot>\KiwiUnityEditorIsolation_yyyyMMdd_HHmmss.csv
///   <ProjectRoot>\KiwiUnityEditorIsolation_yyyyMMdd_HHmmss.meta.txt
///
/// The marker list is fixed from the exact catalog observed in v31.
/// This avoids v31's ranking bias toward Kiwi Editor delayCall markers.
/// </summary>
[DefaultExecutionOrder(32200)]
public sealed class KiwiUnityFrameTimingProbe : MonoBehaviour
{
    public const string ContractMarker =
        "KIWI_V5_1_PHASE16_20_20_V32_EDITOR_ISOLATION_EXACT_MARKER_PROBE";

    private const string EnableEnvironment =
        "KIWI_EDITOR_ISOLATION_PROBE";

    private const string LayoutCaseEnvironment =
        "KIWI_EDITOR_LAYOUT_CASE";

    private const int MaximumSamples =
        12000;

    private static readonly string[] MarkerNames =
    {
        // Frame timing.
        "CPU Main Thread Frame Time",
        "CPU Render Thread Frame Time",
        "CPU Total Frame Time",
        "GPU Frame Time",

        // PlayerLoop / scripts.
        "PlayerLoop",
        "Update.ScriptRunBehaviourUpdate",
        "PreLateUpdate.ScriptRunBehaviourLateUpdate",
        "PostLateUpdate.UpdateAllRenderers",
        "PostLateUpdate.UpdateAllSkinnedMeshes",
        "RenderPlayModeViewCameras",

        // Editor / GameView.
        "EditorLoop",
        "EditorApplication.update",
        "Application.Tick",
        "Application.UpdateScene",
        "GameView.Repaint",
        "GameView.Paint",
        "GUIView.RepaintAll.PlayerLoopController",
        "GUI.Repaint",
        "TextCoreRendering.Render",

        // Camera/render loop.
        "Camera.Render",
        "RenderLoop",
        "RenderLoop.Draw",
        "Drawing",
        "Render.Mesh",
        "MeshRenderer.Render",
        "MeshSkinning.SkinOnGPU",
        "Graphics.Blit",
        "Compute.Dispatch",

        // Texture / upload.
        "Gfx.UploadTextureData",
        "Gfx.UploadTexture",
        "Gfx.CopyTextureData",
        "GraphicsTexture.AcquireUploadMemory",
        "GraphicsTexture.CopyToTextureMemory",
        "GraphicsTexture.ReleaseUploadMemory",
        "Webcam.UploadTexture",

        // D3D12 / render-thread / GPU queue.
        "GfxDeviceD3D12.ExecuteCommandList",
        "GfxDeviceD3D12.FinishRendering",
        "GfxDeviceD3D12.FinishRenderingWithMessagePump",
        "GfxDeviceD3D12.InsertEditorWaitForPresentFence",
        "GfxDeviceD3D12.WaitForGPU",
        "GfxDeviceD3D12.WaitForLastPresentation",
        "GfxDeviceD3D12.WaitForLastPresentation.WaitForGPU",
        "GfxDeviceD3D12.Swap",
        "IDXGISwapChain::Present",
        "GfxTask_PluginEventAndData",
        "GfxTask_WaitOnGpuFence",
        "Gfx.WaitForGfxCommandWriteSpace",
        "Gfx.WaitForGfxCommandsFromMainThread",

        // Present / waits.
        "Gfx.WaitForPresentOnGfxThread",
        "Gfx.WaitForRenderThread",
        "WaitForTargetFPS",
        "Gfx.PresentFrame",

        // Counts.
        "Batches Count",
        "Draw Calls Count"
    };

    private sealed class Metric : IDisposable
    {
        public readonly string name;
        public readonly string category;
        public readonly ProfilerMarkerDataUnit unit;
        private ProfilerRecorder _recorder;

        public Metric(
            string name,
            string category,
            ProfilerMarkerDataUnit unit,
            ProfilerRecorder recorder)
        {
            this.name = name;
            this.category = category;
            this.unit = unit;
            _recorder = recorder;
        }

        public double Read()
        {
            if (
                !_recorder.Valid ||
                !_recorder.IsRunning ||
                _recorder.Count <= 0)
            {
                return double.NaN;
            }

            double raw = _recorder.LastValueAsDouble;

            if (
                _recorder.UnitType ==
                ProfilerMarkerDataUnit.TimeNanoseconds)
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
        public double[] values;
    }

    private readonly List<Metric> _metrics =
        new List<Metric>();

    private readonly List<Sample> _samples =
        new List<Sample>(8192);

    private readonly List<string> _unavailable =
        new List<string>();

    private string _outputBasePath;
    private string _layoutCase;
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
            null)
        {
            return;
        }

        GameObject host =
            new GameObject(
                "KiwiUnityEditorIsolationProbe");

        host.hideFlags = HideFlags.DontSave;
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

        _layoutCase =
            Environment.GetEnvironmentVariable(
                LayoutCaseEnvironment);

        if (string.IsNullOrWhiteSpace(_layoutCase))
        {
            _layoutCase = "UNLABELED";
        }
        else
        {
            _layoutCase =
                _layoutCase.Trim().ToUpperInvariant();
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
                "KiwiUnityEditorIsolation_" + stamp);

        StartMetrics();

        Debug.Log(
            "[KiwiEditorIsolationProbe] case=" +
            _layoutCase +
            " metrics=" +
            _metrics.Count +
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
                unityFrame = Time.frameCount,
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
                values =
                    new double[_metrics.Count]
            };

        for (int i = 0; i < _metrics.Count; i++)
        {
            sample.values[i] =
                _metrics[i].Read();
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

    private void StartMetrics()
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

            if (!byName.ContainsKey(description.Name))
            {
                byName.Add(
                    description.Name,
                    description);
            }
        }

        foreach (string name in MarkerNames)
        {
            if (
                !byName.TryGetValue(
                    name,
                    out ProfilerRecorderDescription description))
            {
                _unavailable.Add(name);
                continue;
            }

            try
            {
                ProfilerRecorder recorder =
                    ProfilerRecorder.StartNew(
                        description.Category,
                        description.Name,
                        1,
                        ProfilerRecorderOptions.SumAllSamplesInFrame |
                        ProfilerRecorderOptions.WrapAroundWhenCapacityReached);

                if (!recorder.Valid)
                {
                    recorder.Dispose();
                    _unavailable.Add(name);
                    continue;
                }

                _metrics.Add(
                    new Metric(
                        description.Name,
                        description.Category.ToString(),
                        description.UnitType,
                        recorder));
            }
            catch
            {
                _unavailable.Add(name);
            }
        }
    }

    private void DisposeMetrics()
    {
        foreach (Metric metric in _metrics)
        {
            metric.Dispose();
        }

        _metrics.Clear();
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
                "[KiwiEditorIsolationProbe] wrote " +
                _samples.Count +
                " samples.");
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[KiwiEditorIsolationProbe] output failed: " +
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
            new StringBuilder(8192);

        header.Append(
            "unityFrame,realtimeSeconds,unscaledDeltaMs,instantaneousFps");

        foreach (Metric metric in _metrics)
        {
            header.Append(',');
            header.Append(
                EscapeCsv(
                    metric.category +
                    ":" +
                    metric.name));
        }

        writer.WriteLine(header.ToString());

        foreach (Sample sample in _samples)
        {
            StringBuilder row =
                new StringBuilder(8192);

            Append(row, sample.unityFrame);
            Sep(row);
            Append(row, sample.realtimeSeconds);
            Sep(row);
            Append(row, sample.unscaledDeltaMs);
            Sep(row);
            Append(row, sample.instantaneousFps);

            foreach (double value in sample.values)
            {
                Sep(row);
                Append(row, value);
            }

            writer.WriteLine(row.ToString());
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
            "KiwiAvatarSystem v32 Editor Isolation A/B Exact Marker Probe");

        writer.WriteLine(
            "contract=" +
            ContractMarker);

        writer.WriteLine(
            "layoutCase=" +
            _layoutCase);

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
            "targetFrameRate=" +
            Application.targetFrameRate);

        writer.WriteLine(
            "vSyncCount=" +
            QualitySettings.vSyncCount);

        writer.WriteLine(
            "maxQueuedFrames=" +
            QualitySettings.maxQueuedFrames);

        writer.WriteLine(
            "sampleCount=" +
            _samples.Count);

        writer.WriteLine();
        writer.WriteLine("AVAILABLE_SELECTED");

        foreach (Metric metric in _metrics)
        {
            writer.WriteLine(
                metric.category +
                "|" +
                metric.name +
                "|" +
                metric.unit);
        }

        writer.WriteLine();
        writer.WriteLine("UNAVAILABLE_REQUESTED");

        foreach (string name in _unavailable)
        {
            writer.WriteLine(name);
        }
    }

    private static string EscapeCsv(
        string value)
    {
        if (
            value.IndexOfAny(
                new[] { ',', '"', '\n', '\r' }) < 0)
        {
            return value;
        }

        return
            "\"" +
            value.Replace("\"", "\"\"") +
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
            double.IsInfinity(value))
        {
            return;
        }

        builder.Append(
            value.ToString(
                "R",
                CultureInfo.InvariantCulture));
    }
}
