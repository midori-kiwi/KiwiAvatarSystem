using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// KiwiAvatarSystem v5.1 Phase 16.20.32 / v42.3 observer-only
/// ORT DirectML D3D12 zero-copy shadow runtime.
///
/// Contract:
/// - disabled unless KIWI_ORT_DML_ZERO_COPY_SHADOW=1;
/// - Windows Development Standalone + DX12 only;
/// - Sentis remains the only tracking/publish authority;
/// - consumes the exact authority 192x192 crop;
/// - GPU tensorization writes directly to persistent GraphicsBuffers;
/// - native ORT DirectML wraps their ID3D12Resource without image readback;
/// - only the tiny model output is copied back to CPU by ORT on its worker;
/// - fixed three slots, newest-ready wins, never an unbounded FIFO;
/// - writes a separate CSV and does not alter KiwiFrameComparison schema.
/// </summary>
[DefaultExecutionOrder(-31840)]
[DisallowMultipleComponent]
public sealed class KiwiOrtDmlZeroCopyRuntime : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] ORT DML Zero-Copy Shadow";

    private const string EnvironmentName =
        "KIWI_ORT_DML_ZERO_COPY_SHADOW";

    private const string LegacyShadowEnvironment =
        "KIWI_ORT_DML_SHADOW";

    private const string Contract =
        "KIWI_V5_1_PHASE16_20_32_V42_3_ORT_DML_NATIVE_LIFECYCLE_ROOTFIX";

    private const string ModelFileName =
        "KiwiFaceLandmarkInference.onnx";

    private const int InputSize = 192;
    private const int PixelCount = InputSize * InputSize;
    private const int InputFloatCount = PixelCount * 3;
    private const int InputByteCount = InputFloatCount * sizeof(float);
    private const int BaseLandmarkCount = 468;
    private const int LandmarkValueCount = BaseLandmarkCount * 3;
    private const int PackedOutputLength = LandmarkValueCount + 1;
    private const int SlotCount = 3;
    private const int PairHistoryCapacity = 96;
    private const int CsvFlushIntervalRows = 60;
    private const int OrientationSamplesPerMode = 3;
    private const int BridgeReadyRetryFrames = 300;
    private const int BridgeReadyLogIntervalFrames = 60;

    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private static KiwiOrtDmlZeroCopyRuntime _instance;

    private readonly object _pairLock = new object();
    private readonly Dictionary<long, PairState> _pairs =
        new Dictionary<long, PairState>();
    private readonly Queue<long> _pairOrder =
        new Queue<long>();
    private readonly Queue<MatchedRecord> _matched =
        new Queue<MatchedRecord>();

    private GraphicsBuffer[] _inputBuffers;
    private ComputeShader _tensorizeShader;
    private int _kernel = -1;
    private CommandBuffer _commandBuffer;
    private IntPtr _renderEventFunc;
    private int _eventBase;
    private bool _ready;
    private bool _disabled;
    private bool _initializing;
    private int _bridgeReadyAttempts;

    private readonly float[] _nativePacked =
        new float[PackedOutputLength];

    private int _submissionCount;
    private int _matchedCount;
    private int _selectedRowMode = -1;
    private int _orientationSequence;
    private int _nativeCalibrationCount;
    private int _flipCalibrationCount;
    private double _nativeMaeSum;
    private double _flipMaeSum;

    private StreamWriter _csvWriter;
    private string _csvPath = string.Empty;
    private int _csvRows;
    private int _rowsSinceFlush;

    private sealed class PairState
    {
        public long sourceHostTicks;
        public long sentisArrivalHostTicks;
        public long ortObservedHostTicks;
        public int rowMode;
        public float workerQueueMs;
        public float ortInferenceMs;
        public float bridgeToDoneMs;
        public float[] ortPacked;
        public float[] sentisPacked;
    }

    private struct MatchedRecord
    {
        public int unityFrame;
        public double realtimeSeconds;
        public long sourceHostTicks;
        public long sentisArrivalHostTicks;
        public long ortObservedHostTicks;
        public int rowMode;
        public int selectedRowMode;
        public float workerQueueMs;
        public float ortInferenceMs;
        public float bridgeToDoneMs;
        public float sourceToOrtObservedMs;
        public float sourceToSentisArrivalMs;
        public float ortMinusSentisObservedMs;
        public float landmarkMaeRaw;
        public float landmarkMaeNormalized;
        public float landmarkMaxAbsRaw;
        public float sentisPresenceLogit;
        public float ortPresenceLogit;
        public float presenceLogitAbsDiff;
        public float sentisPresence;
        public float ortPresence;
        public float presenceAbsDiff;
    }

    private static readonly int SourceId =
        Shader.PropertyToID("_Source");
    private static readonly int OutputId =
        Shader.PropertyToID("_Output");
    private static readonly int FlipYId =
        Shader.PropertyToID("_FlipY");

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern int KiwiOrtDml_IsSupported();

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern int KiwiOrtDml_GetLifecycleState();

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr KiwiOrtDml_GetLifecycleMessage();

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern int KiwiOrtDml_RegisterInputBuffer(
        int slotIndex,
        IntPtr d3d12Resource,
        int byteCount);

    [DllImport(
        "KiwiOrtDmlZeroCopyBridge",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    private static extern int KiwiOrtDml_Initialize(
        string modelPath);

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern void KiwiOrtDml_Shutdown();

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern int KiwiOrtDml_TryBeginSubmission(
        long sourceHostTicks,
        int rowMode,
        out int slotIndex);

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr KiwiOrtDml_GetRenderEventFunc();

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern int KiwiOrtDml_GetSignalEventBase();

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern int KiwiOrtDml_TryGetLatestResult(
        [Out] float[] packedOutput,
        int packedOutputCount,
        out long sourceHostTicks,
        out int rowMode,
        out float workerQueueMs,
        out float ortInferenceMs,
        out float bridgeToDoneMs);

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong KiwiOrtDml_GetCompletedCount();

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong KiwiOrtDml_GetFailureCount();

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong KiwiOrtDml_GetSubmissionSkippedCount();

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong KiwiOrtDml_GetReadyReplacementCount();

    [DllImport("KiwiOrtDmlZeroCopyBridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr KiwiOrtDml_GetLastErrorMessage();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        if (!IsRequested())
        {
            return;
        }

        if (
            Application.platform != RuntimePlatform.WindowsPlayer ||
            !Debug.isDebugBuild)
        {
            Debug.LogWarning(
                "[KiwiInferenceV42] zero-copy shadow ignored: " +
                "Development Windows Player is required.");
            return;
        }

        GameObject host = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiOrtDmlZeroCopyRuntime>();
    }

    private static bool IsRequested()
    {
        string value = Environment.GetEnvironmentVariable(EnvironmentName);
        return
            string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
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

        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
        {
            Disable("Direct3D12 is required.");
            return;
        }

        string oldShadow =
            Environment.GetEnvironmentVariable(LegacyShadowEnvironment);
        if (!string.IsNullOrEmpty(oldShadow) && oldShadow != "0")
        {
            Disable("v41 AsyncGPUReadback shadow must be OFF for v42 isolation.");
            return;
        }

        _tensorizeShader =
            Resources.Load<ComputeShader>("KiwiOrtDmlTensorize");
        if (_tensorizeShader == null)
        {
            Disable("KiwiOrtDmlTensorize compute shader is missing.");
            return;
        }

        try
        {
            _kernel = _tensorizeShader.FindKernel("CSMain");
        }
        catch (Exception ex)
        {
            Disable("Tensorize compute kernel initialization failed: " + ex.Message);
            return;
        }

        // v42.3: bridge readiness is device-lifecycle driven and bounded-retry observed.
        // The build/import policy guarantees Load on startup, while this bounded
        // retry absorbs harmless Unity graphics/plugin initialization ordering.
        _initializing = true;
        StartCoroutine(InitializeWhenBridgeReady());
    }

    private IEnumerator InitializeWhenBridgeReady()
    {
        string lastReason = "not probed";

        for (int attempt = 0; attempt < BridgeReadyRetryFrames; attempt++)
        {
            if (_disabled || _ready)
            {
                _initializing = false;
                yield break;
            }

            _bridgeReadyAttempts = attempt + 1;

            try
            {
                if (TryProbeBridgeReady(out lastReason))
                {
                    InitializeReadyBridge();
                    _initializing = false;
                    yield break;
                }
            }
            catch (DllNotFoundException ex)
            {
                _initializing = false;
                Disable("Native zero-copy bridge DLL could not be loaded: " + ex.Message);
                yield break;
            }
            catch (EntryPointNotFoundException ex)
            {
                _initializing = false;
                Disable("Native zero-copy bridge ABI mismatch: " + ex.Message);
                yield break;
            }
            catch (Exception ex)
            {
                _initializing = false;
                Disable("Native ORT DirectML initialization failed: " + ex.Message);
                yield break;
            }

            if (attempt == 0 || ((attempt + 1) % BridgeReadyLogIntervalFrames) == 0)
            {
                Debug.LogWarning(
                    "[KiwiInferenceV42.3] waiting for preloaded Unity DX12 bridge" +
                    " attempt=" + (attempt + 1) +
                    "/" + BridgeReadyRetryFrames +
                    " reason=" + lastReason);
            }

            yield return null;
        }

        _initializing = false;
        Disable(
            "Preloaded Unity DX12 bridge did not become ready after " +
            BridgeReadyRetryFrames +
            " frames. Last probe: " +
            lastReason +
            ". Verify PluginImporter Load on startup and that no duplicate bridge DLL exists beside the Player EXE.");
    }

    private bool TryProbeBridgeReady(out string reason)
    {
        _renderEventFunc = KiwiOrtDml_GetRenderEventFunc();
        _eventBase = KiwiOrtDml_GetSignalEventBase();
        int supported = KiwiOrtDml_IsSupported();

        if (_renderEventFunc == IntPtr.Zero)
        {
            reason = "render event function is null";
            return false;
        }

        if (_eventBase <= 0)
        {
            reason = "signal event base is invalid";
            return false;
        }

        if (supported == 0)
        {
            int lifecycle = KiwiOrtDml_GetLifecycleState();
            IntPtr messagePtr = KiwiOrtDml_GetLifecycleMessage();
            string message =
                messagePtr == IntPtr.Zero
                    ? string.Empty
                    : Marshal.PtrToStringAnsi(messagePtr);
            reason =
                "nativeLifecycle=" + lifecycle +
                " message=" + message;
            return false;
        }

        reason =
            "ready lifecycle=" +
            KiwiOrtDml_GetLifecycleState();
        return true;
    }

    private void InitializeReadyBridge()
    {
        if (_ready || _disabled)
        {
            return;
        }

        _inputBuffers = new GraphicsBuffer[SlotCount];
        try
        {
            for (int i = 0; i < SlotCount; i++)
            {
                GraphicsBuffer buffer =
                    new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured,
                        InputFloatCount,
                        sizeof(float));
                buffer.name = "Kiwi ORT DML Input Slot " + i;
                _inputBuffers[i] = buffer;

                IntPtr native = buffer.GetNativeBufferPtr();
                if (
                    native == IntPtr.Zero ||
                    KiwiOrtDml_RegisterInputBuffer(
                        i,
                        native,
                        InputByteCount) == 0)
                {
                    throw new InvalidOperationException(
                        "Failed to register D3D12 input buffer slot " + i + ".");
                }
            }

            string modelPath =
                Path.Combine(
                    Application.streamingAssetsPath,
                    ModelFileName);

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException(
                    "v42 ONNX model is missing.",
                    modelPath);
            }

            if (KiwiOrtDml_Initialize(modelPath) == 0)
            {
                throw new InvalidOperationException(
                    "Native ORT DirectML initialization failed: " +
                    GetNativeError());
            }

            _commandBuffer = new CommandBuffer
            {
                name = "Kiwi ORT DML Zero-Copy Tensorize"
            };

            _ready = true;

            Debug.Log(
                "[KiwiInferenceV42.3] contract=" + Contract +
                " enabled=1 backend=ORT_DIRECTML_NATIVE_D3D12" +
                " input=192x192 gpuInputReadback=0" +
                " authority=UNITY_INFERENCE_ENGINE" +
                " slots=3 latestOnly=1" +
                " bridgeReadyAttempts=" + _bridgeReadyAttempts +
                " graphicsApi=" +
                SystemInfo.graphicsDeviceType);
        }
        catch
        {
            ReleaseRuntimeGpuResources();
            throw;
        }
    }

    private void ReleaseRuntimeGpuResources()
    {
        if (_commandBuffer != null)
        {
            _commandBuffer.Release();
            _commandBuffer = null;
        }

        if (_inputBuffers != null)
        {
            for (int i = 0; i < _inputBuffers.Length; i++)
            {
                _inputBuffers[i]?.Dispose();
                _inputBuffers[i] = null;
            }

            _inputBuffers = null;
        }
    }

    private void Update()
    {
        if (!_ready)
        {
            return;
        }

        bool frameCsvRecording =
            KiwiFrameComparisonOverlay.Instance != null &&
            KiwiFrameComparisonOverlay.Instance.IsCsvRecording;

        if (frameCsvRecording && _csvWriter == null)
        {
            StartCsv();
        }
        else if (!frameCsvRecording && _csvWriter != null)
        {
            StopCsv();
        }

        DrainNativeResults();
        DrainMatchedRecords();
    }

    private void OnDestroy()
    {
        StopCsv();

        try
        {
            KiwiOrtDml_Shutdown();
        }
        catch
        {
        }

        ReleaseRuntimeGpuResources();

        if (_instance == this)
        {
            _instance = null;
        }
    }

    public static void TrySubmitCrop(
        RenderTexture exactAuthorityCrop,
        long sourceHostTicks)
    {
        KiwiOrtDmlZeroCopyRuntime instance = _instance;
        if (
            instance == null ||
            !instance._ready ||
            exactAuthorityCrop == null ||
            sourceHostTicks <= 0L)
        {
            return;
        }

        instance.SubmitCrop(
            exactAuthorityCrop,
            sourceHostTicks);
    }

    public static void RecordSentisPackedOutput(
        long sourceHostTicks,
        Unity.InferenceEngine.Tensor<float> packedOutput,
        long sentisArrivalHostTicks)
    {
        KiwiOrtDmlZeroCopyRuntime instance = _instance;
        if (
            instance == null ||
            !instance._ready ||
            packedOutput == null ||
            sourceHostTicks <= 0L ||
            packedOutput.shape.length != PackedOutputLength)
        {
            return;
        }

        float[] copy = new float[PackedOutputLength];
        for (int i = 0; i < PackedOutputLength; i++)
        {
            copy[i] = packedOutput[i];
        }

        lock (instance._pairLock)
        {
            PairState pair =
                instance.GetOrCreatePairLocked(sourceHostTicks);
            pair.sentisPacked = copy;
            pair.sentisArrivalHostTicks = sentisArrivalHostTicks;
            instance.TryMatchLocked(pair);
        }
    }

    private void SubmitCrop(
        RenderTexture crop,
        long sourceHostTicks)
    {
        if (
            crop.width != InputSize ||
            crop.height != InputSize)
        {
            return;
        }

        int rowMode = ResolveRowModeForSubmission();

        if (
            KiwiOrtDml_TryBeginSubmission(
                sourceHostTicks,
                rowMode,
                out int slotIndex) == 0 ||
            slotIndex < 0 ||
            slotIndex >= SlotCount)
        {
            return;
        }

        RegisterPairSubmission(
            sourceHostTicks,
            rowMode);

        CommandBuffer cb = _commandBuffer;
        cb.Clear();
        cb.SetComputeIntParam(
            _tensorizeShader,
            FlipYId,
            rowMode);
        cb.SetComputeTextureParam(
            _tensorizeShader,
            _kernel,
            SourceId,
            crop);
        cb.SetComputeBufferParam(
            _tensorizeShader,
            _kernel,
            OutputId,
            _inputBuffers[slotIndex]);
        cb.DispatchCompute(
            _tensorizeShader,
            _kernel,
            InputSize / 8,
            InputSize / 8,
            1);

        // The plugin event is in the same Unity CommandBuffer after tensorization.
        // Native code only signals a fence on Unity's graphics queue; there is no
        // CPU wait and no g_unityD3D12Queue->Wait style stall.
        cb.IssuePluginEvent(
            _renderEventFunc,
            _eventBase + slotIndex);

        Graphics.ExecuteCommandBuffer(cb);
        _submissionCount++;
    }

    private int ResolveRowModeForSubmission()
    {
        if (_selectedRowMode >= 0)
        {
            return _selectedRowMode;
        }

        int rowMode =
            _orientationSequence % 2;
        _orientationSequence++;
        return rowMode;
    }

    private void DrainNativeResults()
    {
        while (
            KiwiOrtDml_TryGetLatestResult(
                _nativePacked,
                _nativePacked.Length,
                out long sourceHostTicks,
                out int rowMode,
                out float workerQueueMs,
                out float ortInferenceMs,
                out float bridgeToDoneMs) != 0)
        {
            long observed =
                System.Diagnostics.Stopwatch.GetTimestamp();
            float[] copy = new float[PackedOutputLength];
            Array.Copy(
                _nativePacked,
                copy,
                PackedOutputLength);

            lock (_pairLock)
            {
                PairState pair =
                    GetOrCreatePairLocked(sourceHostTicks);
                pair.rowMode = rowMode;
                pair.workerQueueMs = workerQueueMs;
                pair.ortInferenceMs = ortInferenceMs;
                pair.bridgeToDoneMs = bridgeToDoneMs;
                pair.ortObservedHostTicks = observed;
                pair.ortPacked = copy;
                TryMatchLocked(pair);
            }
        }
    }

    private void RegisterPairSubmission(
        long sourceHostTicks,
        int rowMode)
    {
        lock (_pairLock)
        {
            PairState pair =
                GetOrCreatePairLocked(sourceHostTicks);
            pair.rowMode = rowMode;
        }
    }

    private PairState GetOrCreatePairLocked(
        long sourceHostTicks)
    {
        if (_pairs.TryGetValue(
                sourceHostTicks,
                out PairState pair))
        {
            return pair;
        }

        pair = new PairState
        {
            sourceHostTicks = sourceHostTicks
        };

        _pairs[sourceHostTicks] = pair;
        _pairOrder.Enqueue(sourceHostTicks);

        while (_pairOrder.Count > PairHistoryCapacity)
        {
            long old = _pairOrder.Dequeue();
            _pairs.Remove(old);
        }

        return pair;
    }

    private void TryMatchLocked(
        PairState pair)
    {
        if (
            pair == null ||
            pair.ortPacked == null ||
            pair.sentisPacked == null)
        {
            return;
        }

        float sum = 0f;
        float maximum = 0f;
        for (int i = 0; i < LandmarkValueCount; i++)
        {
            float delta =
                Mathf.Abs(
                    pair.ortPacked[i] -
                    pair.sentisPacked[i]);
            sum += delta;
            maximum = Mathf.Max(maximum, delta);
        }

        float mae =
            sum / LandmarkValueCount;

        if (_selectedRowMode < 0)
        {
            if (pair.rowMode == 0)
            {
                _nativeMaeSum += mae;
                _nativeCalibrationCount++;
            }
            else
            {
                _flipMaeSum += mae;
                _flipCalibrationCount++;
            }

            if (
                _nativeCalibrationCount >= OrientationSamplesPerMode &&
                _flipCalibrationCount >= OrientationSamplesPerMode)
            {
                double nativeMean =
                    _nativeMaeSum /
                    _nativeCalibrationCount;
                double flipMean =
                    _flipMaeSum /
                    _flipCalibrationCount;

                _selectedRowMode =
                    flipMean < nativeMean
                        ? 1
                        : 0;

                Debug.Log(
                    "[KiwiInferenceV42] input row calibration selected=" +
                    (_selectedRowMode == 0 ? "NATIVE" : "FLIP_Y") +
                    " nativeMae=" +
                    nativeMean.ToString("F6", Invariant) +
                    " flipMae=" +
                    flipMean.ToString("F6", Invariant));
            }
        }

        float sentisPresenceLogit =
            pair.sentisPacked[LandmarkValueCount];
        float ortPresenceLogit =
            pair.ortPacked[LandmarkValueCount];
        float sentisPresence =
            Sigmoid(sentisPresenceLogit);
        float ortPresence =
            Sigmoid(ortPresenceLogit);

        MatchedRecord record = new MatchedRecord
        {
            unityFrame = Time.frameCount,
            realtimeSeconds = Time.realtimeSinceStartupAsDouble,
            sourceHostTicks = pair.sourceHostTicks,
            sentisArrivalHostTicks = pair.sentisArrivalHostTicks,
            ortObservedHostTicks = pair.ortObservedHostTicks,
            rowMode = pair.rowMode,
            selectedRowMode = _selectedRowMode,
            workerQueueMs = pair.workerQueueMs,
            ortInferenceMs = pair.ortInferenceMs,
            bridgeToDoneMs = pair.bridgeToDoneMs,
            sourceToOrtObservedMs = HostTickDeltaMs(
                pair.sourceHostTicks,
                pair.ortObservedHostTicks),
            sourceToSentisArrivalMs = HostTickDeltaMs(
                pair.sourceHostTicks,
                pair.sentisArrivalHostTicks),
            ortMinusSentisObservedMs = HostTickDeltaMs(
                pair.sentisArrivalHostTicks,
                pair.ortObservedHostTicks),
            landmarkMaeRaw = mae,
            landmarkMaeNormalized = mae / InputSize,
            landmarkMaxAbsRaw = maximum,
            sentisPresenceLogit = sentisPresenceLogit,
            ortPresenceLogit = ortPresenceLogit,
            presenceLogitAbsDiff = Mathf.Abs(
                sentisPresenceLogit - ortPresenceLogit),
            sentisPresence = sentisPresence,
            ortPresence = ortPresence,
            presenceAbsDiff = Mathf.Abs(
                sentisPresence - ortPresence)
        };

        _matched.Enqueue(record);
        _matchedCount++;
        _pairs.Remove(pair.sourceHostTicks);
    }

    private void DrainMatchedRecords()
    {
        while (true)
        {
            MatchedRecord record;
            lock (_pairLock)
            {
                if (_matched.Count == 0)
                {
                    break;
                }
                record = _matched.Dequeue();
            }

            if (_csvWriter != null)
            {
                WriteCsv(record);
            }
        }
    }

    private static float HostTickDeltaMs(
        long begin,
        long end)
    {
        if (begin <= 0L || end <= begin)
        {
            return 0f;
        }

        return (float)(
            (end - begin) * 1000.0 /
            System.Diagnostics.Stopwatch.Frequency);
    }

    private static float Sigmoid(float value)
    {
        if (value >= 0f)
        {
            float z = Mathf.Exp(-value);
            return 1f / (1f + z);
        }

        float ez = Mathf.Exp(value);
        return ez / (1f + ez);
    }

    private void StartCsv()
    {
        try
        {
            string directory = Path.Combine(
                Application.persistentDataPath,
                "KiwiOrtDmlZeroCopy");
            Directory.CreateDirectory(directory);
            _csvPath = Path.Combine(
                directory,
                "KiwiOrtDmlZeroCopyTelemetry_v42_3_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss",
                    Invariant) +
                ".csv");

            _csvWriter = new StreamWriter(
                _csvPath,
                false,
                new UTF8Encoding(false));
            _csvWriter.WriteLine(
                "unityFrame,realtimeSeconds,sourceHostTicks,sentisArrivalHostTicks,ortObservedHostTicks," +
                "rowMode,selectedRowMode,workerQueueMs,ortInferenceMs,bridgeToDoneMs," +
                "sourceToOrtObservedMs,sourceToSentisArrivalMs,ortMinusSentisObservedMs," +
                "landmarkMaeRaw,landmarkMaeNormalized,landmarkMaxAbsRaw," +
                "sentisPresenceLogit,ortPresenceLogit,presenceLogitAbsDiff," +
                "sentisPresence,ortPresence,presenceAbsDiff," +
                "nativeCompleted,nativeFailures,nativeSkipped,nativeReadyReplacements");
            _csvRows = 0;
            _rowsSinceFlush = 0;

            Debug.Log(
                "[KiwiInferenceV42] zero-copy CSV started: " +
                _csvPath);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[KiwiInferenceV42] CSV start failed: " +
                ex.Message);
            _csvWriter = null;
        }
    }

    private void StopCsv()
    {
        if (_csvWriter == null)
        {
            return;
        }

        try
        {
            _csvWriter.Flush();
            _csvWriter.Dispose();
        }
        catch
        {
        }
        finally
        {
            _csvWriter = null;
        }

        Debug.Log(
            "[KiwiInferenceV42] zero-copy CSV stopped rows=" +
            _csvRows +
            " path=" +
            _csvPath);
    }

    private void WriteCsv(MatchedRecord r)
    {
        StreamWriter writer = _csvWriter;
        if (writer == null)
        {
            return;
        }

        writer.Write(r.unityFrame); writer.Write(',');
        writer.Write(r.realtimeSeconds.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.sourceHostTicks); writer.Write(',');
        writer.Write(r.sentisArrivalHostTicks); writer.Write(',');
        writer.Write(r.ortObservedHostTicks); writer.Write(',');
        writer.Write(r.rowMode); writer.Write(',');
        writer.Write(r.selectedRowMode); writer.Write(',');
        writer.Write(r.workerQueueMs.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.ortInferenceMs.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.bridgeToDoneMs.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.sourceToOrtObservedMs.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.sourceToSentisArrivalMs.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.ortMinusSentisObservedMs.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.landmarkMaeRaw.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.landmarkMaeNormalized.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.landmarkMaxAbsRaw.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.sentisPresenceLogit.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.ortPresenceLogit.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.presenceLogitAbsDiff.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.sentisPresence.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.ortPresence.ToString("R", Invariant)); writer.Write(',');
        writer.Write(r.presenceAbsDiff.ToString("R", Invariant)); writer.Write(',');
        writer.Write(KiwiOrtDml_GetCompletedCount()); writer.Write(',');
        writer.Write(KiwiOrtDml_GetFailureCount()); writer.Write(',');
        writer.Write(KiwiOrtDml_GetSubmissionSkippedCount()); writer.Write(',');
        writer.Write(KiwiOrtDml_GetReadyReplacementCount());
        writer.WriteLine();

        _csvRows++;
        _rowsSinceFlush++;
        if (_rowsSinceFlush >= CsvFlushIntervalRows)
        {
            writer.Flush();
            _rowsSinceFlush = 0;
        }
    }

    private static string GetNativeError()
    {
        try
        {
            IntPtr pointer = KiwiOrtDml_GetLastErrorMessage();
            return pointer != IntPtr.Zero
                ? Marshal.PtrToStringAnsi(pointer)
                : "unknown";
        }
        catch
        {
            return "unavailable";
        }
    }

    private void Disable(string reason)
    {
        _disabled = true;
        _ready = false;

        Debug.LogError(
            "[KiwiInferenceV42] zero-copy shadow disabled: " +
            reason +
            " native=" +
            GetNativeError());

        try
        {
            KiwiOrtDml_Shutdown();
        }
        catch
        {
        }

        ReleaseRuntimeGpuResources();
    }
}
