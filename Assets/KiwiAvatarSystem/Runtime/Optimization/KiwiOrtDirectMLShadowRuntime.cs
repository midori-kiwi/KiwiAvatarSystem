using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// KiwiAvatarSystem v5.1 Phase 16.20.28 / v41 observer-only
/// ONNX Runtime DirectML shadow backend.
///
/// Contract:
/// - disabled unless KIWI_ORT_DML_SHADOW=1;
/// - Windows Development Standalone + DX12 only;
/// - never publishes tracking data or changes ROI/threshold/generation state;
/// - consumes the exact 192x192 ARGB32 crop already produced for the authority
///   Unity Inference Engine path;
/// - uses AsyncGPUReadback + a bounded three-slot latest-only CPU mailbox;
/// - runs ORT DirectML on one background worker;
/// - compares raw model-space output with the exact matching Sentis source tick;
/// - writes a separate CSV, leaving KiwiFrameComparison CSV schema untouched.
/// </summary>
[DefaultExecutionOrder(-31850)]
[DisallowMultipleComponent]
public sealed class KiwiOrtDirectMLShadowRuntime : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] ORT DirectML Shadow Runtime";

    private const string ShadowEnvironment =
        "KIWI_ORT_DML_SHADOW";

    private const string Contract =
        "KIWI_V5_1_PHASE16_20_28_V41_ORT_DIRECTML_SHADOW";

    private const string ModelFileName =
        "KiwiFaceLandmarkInference.onnx";

    private const string LandmarkOutputName =
        "conv2d_20";

    private const string PresenceOutputName =
        "conv2d_30";

    private const int InputSize = 192;
    private const int BaseLandmarkCount = 468;
    private const int LandmarkValueCount = BaseLandmarkCount * 3;
    private const int PackedOutputLength = LandmarkValueCount + 1;
    private const int PixelCount = InputSize * InputSize;
    private const int RgbaByteCount = PixelCount * 4;
    private const int SlotCount = 3;
    private const int PairHistoryCapacity = 96;
    private const int CsvFlushIntervalRows = 60;
    private const int OrientationCalibrationSamplesPerMode = 3;

    private enum SlotState
    {
        Free = 0,
        ReadbackPending = 1,
        Ready = 2,
        Working = 3
    }

    private sealed class ShadowSlot
    {
        public readonly byte[] rgba =
            new byte[RgbaByteCount];

        public SlotState state;
        public long sourceHostTicks;
        public long submitHostTicks;
        public long readbackDoneHostTicks;
        public int rowMode;
    }

    private sealed class PairState
    {
        public long sourceHostTicks;
        public long shadowSubmitHostTicks;
        public long shadowReadbackDoneHostTicks;
        public long ortStartHostTicks;
        public long ortDoneHostTicks;
        public long sentisArrivalHostTicks;
        public float gpuReadbackMs;
        public float workerQueueMs;
        public float preprocessMs;
        public float ortInferenceMs;
        public int rowMode;
        public float[] ortPacked;
        public float[] sentisPacked;
    }

    private struct MatchedRecord
    {
        public long sourceHostTicks;
        public long shadowSubmitHostTicks;
        public long shadowReadbackDoneHostTicks;
        public long ortStartHostTicks;
        public long ortDoneHostTicks;
        public long sentisArrivalHostTicks;
        public float gpuReadbackMs;
        public float workerQueueMs;
        public float preprocessMs;
        public float ortInferenceMs;
        public float shadowSubmitToOrtMs;
        public float sourceToOrtMs;
        public float sourceToSentisMs;
        public float ortMinusSentisCompletionMs;
        public float landmarkMaeRaw;
        public float landmarkMaeNormalized;
        public float landmarkMaxAbsRaw;
        public float sentisPresenceLogit;
        public float ortPresenceLogit;
        public float presenceLogitAbsDiff;
        public float sentisPresence;
        public float ortPresence;
        public float presenceAbsDiff;
        public int rowMode;
        public int selectedRowMode;
    }

    private static KiwiOrtDirectMLShadowRuntime _instance;

    private readonly object _slotLock = new object();
    private readonly object _pairLock = new object();
    private readonly object _matchedLock = new object();
    private readonly object _statusLock = new object();

    private readonly ShadowSlot[] _slots =
        new ShadowSlot[SlotCount];

    private readonly Action<AsyncGPUReadbackRequest>[] _callbacks =
        new Action<AsyncGPUReadbackRequest>[SlotCount];

    private readonly Dictionary<long, PairState> _pairs =
        new Dictionary<long, PairState>();

    private readonly Queue<long> _pairOrder =
        new Queue<long>();

    private readonly Queue<MatchedRecord> _matchedQueue =
        new Queue<MatchedRecord>();

    private readonly AutoResetEvent _workerWake =
        new AutoResetEvent(false);

    private readonly float[] _workerInput =
        new float[PixelCount * 3];

    private Thread _workerThread;
    private string _modelPath = string.Empty;
    private volatile bool _stopWorker;
    private volatile bool _sessionReady;
    private volatile bool _runtimeDisabled;

    private InferenceSession _session;
    private DenseTensor<float> _inputTensor;
    private List<NamedOnnxValue> _inputContainer;
    private string _inputName;
    private string[] _outputNames;

    private string _pendingStatusMessage = string.Empty;
    private bool _statusMessageIsError;
    private bool _statusLogged;

    private int _shadowScheduledCount;
    private int _shadowReadbackCompletedCount;
    private int _shadowReadbackErrorCount;
    private int _shadowInputSkippedCount;
    private int _shadowReadyReplacementCount;
    private int _ortCompletedCount;
    private int _ortFailureCount;
    private int _matchedCount;

    // AsyncGPUReadback byte-row orientation is not documented as a model-input
    // contract. Calibrate observer-side using exact same-source Sentis parity,
    // then lock the lower-error row interpretation for the rest of the run.
    private int _orientationCalibrationSequence;
    private int _selectedRowMode = -1;
    private int _nativeRowCalibrationCount;
    private int _flipRowCalibrationCount;
    private double _nativeRowMaeSum;
    private double _flipRowMaeSum;

    private StreamWriter _csvWriter;
    private string _currentCsvPath = string.Empty;
    private int _csvRowsSinceFlush;
    private int _csvRowsWritten;

    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        if (!IsShadowRequested())
        {
            return;
        }

        if (
            Application.platform != RuntimePlatform.WindowsPlayer ||
            !Debug.isDebugBuild)
        {
            Debug.LogWarning(
                "[KiwiInferenceV41] ORT DirectML shadow ignored: " +
                "Development Windows Player is required.");
            return;
        }

        if (_instance != null)
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiOrtDirectMLShadowRuntime>();
    }

    private static bool IsShadowRequested()
    {
        string value =
            Environment.GetEnvironmentVariable(
                ShadowEnvironment);

        return
            string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "shadow", StringComparison.OrdinalIgnoreCase);
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
            _runtimeDisabled = true;
            _pendingStatusMessage =
                "ORT DirectML shadow requires Direct3D12.";
            _statusMessageIsError = true;
            return;
        }

        for (int i = 0; i < SlotCount; i++)
        {
            _slots[i] = new ShadowSlot();
            int captured = i;
            _callbacks[i] =
                delegate(AsyncGPUReadbackRequest request)
                {
                    HandleReadbackCompleted(
                        captured,
                        request);
                };
        }

        _modelPath =
            Path.Combine(
                Application.streamingAssetsPath,
                ModelFileName);

        _workerThread =
            new Thread(WorkerMain)
            {
                IsBackground = true,
                Name = "Kiwi ORT DirectML Shadow"
            };

        _workerThread.Start();
    }

    private void Update()
    {
        EmitPendingStatus();

        bool frameCsvRecording =
            KiwiFrameComparisonOverlay.Instance != null &&
            KiwiFrameComparisonOverlay.Instance.IsCsvRecording;

        if (frameCsvRecording && _csvWriter == null)
        {
            StartCsvRecording();
        }
        else if (!frameCsvRecording && _csvWriter != null)
        {
            StopCsvRecording();
        }

        DrainMatchedRecords();
    }

    private void OnDestroy()
    {
        StopCsvRecording();

        _stopWorker = true;
        _workerWake.Set();

        bool workerStopped =
            _workerThread == null ||
            !_workerThread.IsAlive ||
            _workerThread.Join(2000);

        if (_session != null)
        {
            _session.Dispose();
            _session = null;
        }

        if (workerStopped)
        {
            _workerWake.Dispose();
        }

        if (_instance == this)
        {
            _instance = null;
        }
    }

    /// <summary>
    /// Called by the authority tracker immediately after its exact 192x192 crop
    /// Graphics.Blit and before the authority model is submitted.
    /// </summary>
    public static void TrySubmitCrop(
        RenderTexture exactAuthorityCrop,
        long sourceHostTicks)
    {
        KiwiOrtDirectMLShadowRuntime instance = _instance;

        if (
            instance == null ||
            instance._runtimeDisabled ||
            !instance._sessionReady ||
            exactAuthorityCrop == null ||
            sourceHostTicks <= 0L)
        {
            return;
        }

        instance.SubmitCrop(
            exactAuthorityCrop,
            sourceHostTicks);
    }

    /// <summary>
    /// Called only after the matching authority GPU output has completed and
    /// become CPU-readable. The packed raw tensor is copied for parity analysis;
    /// no value is fed back to tracking.
    /// </summary>
    public static void RecordSentisPackedOutput(
        long sourceHostTicks,
        Unity.InferenceEngine.Tensor<float> packedOutput,
        long sentisArrivalHostTicks)
    {
        // KIWI_V44_55_26_PRODUCTION_DECODE_PAYLOAD_TRANSACTION_CLOSURE
        // Reads only the CPU tensor already created by ReadbackAndClone.
        KiwiProductionDecodePayloadTransactionTraceV44_55_26.RecordDecodePayload(
            sourceHostTicks,
            packedOutput,
            sentisArrivalHostTicks);

        // KIWI_V44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUTHORITY
        // Reuses the same already CPU-readable tensor seam. Only the enabled
        // observer copies it; v26 and v27 are never enabled together.
        KiwiActualProductionVsShadowPayloadAuthorityV44_55_27
            .RecordActualDecodePayload(
                sourceHostTicks,
                packedOutput,
                sentisArrivalHostTicks);

        KiwiOrtDirectMLShadowRuntime instance = _instance;

        if (
            instance == null ||
            instance._runtimeDisabled ||
            packedOutput == null ||
            sourceHostTicks <= 0L ||
            packedOutput.shape.length != PackedOutputLength)
        {
            return;
        }

        instance.StoreSentisPackedOutput(
            sourceHostTicks,
            packedOutput,
            sentisArrivalHostTicks);
    }

    private void SubmitCrop(
        RenderTexture crop,
        long sourceHostTicks)
    {
        int slotIndex = -1;
        long submitHostTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();

        lock (_slotLock)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (_slots[i].state == SlotState.Free)
                {
                    slotIndex = i;
                    break;
                }
            }

            if (slotIndex < 0)
            {
                Interlocked.Increment(
                    ref _shadowInputSkippedCount);
                return;
            }

            ShadowSlot slot = _slots[slotIndex];
            slot.state = SlotState.ReadbackPending;
            slot.sourceHostTicks = sourceHostTicks;
            slot.submitHostTicks = submitHostTicks;
            slot.readbackDoneHostTicks = 0L;
            slot.rowMode = ResolveRowModeForNextSubmission();
        }

        RegisterPairSubmission(
            sourceHostTicks,
            submitHostTicks,
            _slots[slotIndex].rowMode);

        try
        {
            AsyncGPUReadback.Request(
                crop,
                0,
                TextureFormat.RGBA32,
                _callbacks[slotIndex]);

            Interlocked.Increment(
                ref _shadowScheduledCount);
        }
        catch (Exception exception)
        {
            lock (_slotLock)
            {
                ClearSlotLocked(slotIndex);
            }

            Interlocked.Increment(
                ref _shadowReadbackErrorCount);

            QueueStatus(
                "AsyncGPUReadback submit failed: " +
                exception.GetType().Name +
                " " +
                exception.Message,
                true);
        }
    }

    private void HandleReadbackCompleted(
        int slotIndex,
        AsyncGPUReadbackRequest request)
    {
        if (
            slotIndex < 0 ||
            slotIndex >= SlotCount)
        {
            return;
        }

        long doneHostTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();

        lock (_slotLock)
        {
            ShadowSlot slot = _slots[slotIndex];

            if (slot.state != SlotState.ReadbackPending)
            {
                return;
            }

            if (request.hasError)
            {
                ClearSlotLocked(slotIndex);
                Interlocked.Increment(
                    ref _shadowReadbackErrorCount);
                return;
            }

            try
            {
                var data = request.GetData<byte>();

                if (data.Length < RgbaByteCount)
                {
                    ClearSlotLocked(slotIndex);
                    Interlocked.Increment(
                        ref _shadowReadbackErrorCount);
                    return;
                }

                data.CopyTo(slot.rgba);
                slot.readbackDoneHostTicks = doneHostTicks;
                slot.state = SlotState.Ready;

                Interlocked.Increment(
                    ref _shadowReadbackCompletedCount);
            }
            catch
            {
                ClearSlotLocked(slotIndex);
                Interlocked.Increment(
                    ref _shadowReadbackErrorCount);
                return;
            }
        }

        _workerWake.Set();
    }

    private void WorkerMain()
    {
        try
        {
            InitializeSessionOnWorker();
        }
        catch (Exception exception)
        {
            _runtimeDisabled = true;
            QueueStatus(
                "ORT DirectML initialization failed: " +
                exception.GetType().Name +
                " " +
                exception.Message,
                true);
            return;
        }

        _sessionReady = true;
        QueueStatus(
            "contract=" + Contract +
            " enabled=1 backend=ORT_DIRECTML_1_22_0" +
            " input=192x192 exactAuthorityCrop=1" +
            " policy=OBSERVER_ONLY_LATEST_THREE_SLOT" +
            " graphicsApi=Direct3D12",
            false);

        while (!_stopWorker)
        {
            _workerWake.WaitOne(100);

            if (_stopWorker)
            {
                break;
            }

            while (true)
            {
                int slotIndex =
                    AcquireNewestReadySlot();

                if (slotIndex < 0)
                {
                    break;
                }

                ProcessReadySlot(slotIndex);
            }
        }
    }

    private void InitializeSessionOnWorker()
    {
        string modelPath = _modelPath;

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                "Shadow ONNX model is missing.",
                modelPath);
        }

        string applicationDirectory =
            AppDomain.CurrentDomain.BaseDirectory;

        SetDllDirectory(applicationDirectory);

        try
        {
            using (SessionOptions options = new SessionOptions())
            {
                // Official DirectML EP constraints: sequential execution and
                // memory pattern disabled. Graph optimization stays fully on.
                options.ExecutionMode =
                    ExecutionMode.ORT_SEQUENTIAL;
                options.EnableMemoryPattern = false;
                options.GraphOptimizationLevel =
                    GraphOptimizationLevel.ORT_ENABLE_ALL;
                options.InterOpNumThreads = 1;
                options.IntraOpNumThreads = 1;
                options.AppendExecutionProvider_DML(0);

                _session =
                    new InferenceSession(
                        modelPath,
                        options);
            }
        }
        finally
        {
            SetDllDirectory(null);
        }

        _inputName = null;

        foreach (
            KeyValuePair<string, NodeMetadata> entry
            in _session.InputMetadata)
        {
            _inputName = entry.Key;
            break;
        }

        if (string.IsNullOrEmpty(_inputName))
        {
            throw new InvalidOperationException(
                "ORT model exposes no input tensor.");
        }

        if (
            !_session.OutputMetadata.ContainsKey(LandmarkOutputName) ||
            !_session.OutputMetadata.ContainsKey(PresenceOutputName))
        {
            throw new InvalidOperationException(
                "ORT model does not expose expected landmark/presence outputs.");
        }

        _inputTensor =
            new DenseTensor<float>(
                _workerInput,
                new int[]
                {
                    1,
                    3,
                    InputSize,
                    InputSize
                });

        _inputContainer =
            new List<NamedOnnxValue>(1)
            {
                NamedOnnxValue.CreateFromTensor<float>(
                    _inputName,
                    _inputTensor)
            };

        _outputNames =
            new string[]
            {
                LandmarkOutputName,
                PresenceOutputName
            };
    }

    private int AcquireNewestReadySlot()
    {
        lock (_slotLock)
        {
            int newestIndex = -1;
            long newestSourceTicks = long.MinValue;

            for (int i = 0; i < SlotCount; i++)
            {
                ShadowSlot slot = _slots[i];

                if (
                    slot.state == SlotState.Ready &&
                    slot.sourceHostTicks > newestSourceTicks)
                {
                    newestIndex = i;
                    newestSourceTicks = slot.sourceHostTicks;
                }
            }

            if (newestIndex < 0)
            {
                return -1;
            }

            for (int i = 0; i < SlotCount; i++)
            {
                if (
                    i != newestIndex &&
                    _slots[i].state == SlotState.Ready)
                {
                    ClearSlotLocked(i);
                    Interlocked.Increment(
                        ref _shadowReadyReplacementCount);
                }
            }

            _slots[newestIndex].state = SlotState.Working;
            return newestIndex;
        }
    }

    private void ProcessReadySlot(
        int slotIndex)
    {
        ShadowSlot slot;

        lock (_slotLock)
        {
            slot = _slots[slotIndex];

            if (slot.state != SlotState.Working)
            {
                return;
            }
        }

        long ortStartHostTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();

        float gpuReadbackMs =
            TicksToMilliseconds(
                slot.readbackDoneHostTicks -
                slot.submitHostTicks);

        float workerQueueMs =
            TicksToMilliseconds(
                ortStartHostTicks -
                slot.readbackDoneHostTicks);

        long preprocessBeginHostTicks =
            ortStartHostTicks;

        ConvertRgbaToNchw(
            slot.rgba,
            _workerInput,
            slot.rowMode == 1);

        long preprocessEndHostTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();

        float preprocessMs =
            TicksToMilliseconds(
                preprocessEndHostTicks -
                preprocessBeginHostTicks);

        long inferenceBeginHostTicks =
            preprocessEndHostTicks;

        float[] packed =
            new float[PackedOutputLength];

        try
        {
            using (var results =
                _session.Run(
                    _inputContainer,
                    _outputNames))
            {
                bool hasLandmarks = false;
                bool hasPresence = false;

                foreach (var result in results)
                {
                    if (result.Name == LandmarkOutputName)
                    {
                        int index = 0;

                        foreach (
                            float value in
                            result.AsEnumerable<float>())
                        {
                            if (index >= LandmarkValueCount)
                            {
                                break;
                            }

                            packed[index++] = value;
                        }

                        hasLandmarks =
                            index == LandmarkValueCount;
                    }
                    else if (result.Name == PresenceOutputName)
                    {
                        foreach (
                            float value in
                            result.AsEnumerable<float>())
                        {
                            packed[LandmarkValueCount] = value;
                            hasPresence = true;
                            break;
                        }
                    }
                }

                if (!hasLandmarks || !hasPresence)
                {
                    throw new InvalidOperationException(
                        "ORT output shape/name contract mismatch.");
                }
            }

            long ortDoneHostTicks =
                System.Diagnostics.Stopwatch.GetTimestamp();

            float ortInferenceMs =
                TicksToMilliseconds(
                    ortDoneHostTicks -
                    inferenceBeginHostTicks);

            Interlocked.Increment(
                ref _ortCompletedCount);

            StoreOrtPackedOutput(
                slot.sourceHostTicks,
                slot.submitHostTicks,
                slot.readbackDoneHostTicks,
                ortStartHostTicks,
                ortDoneHostTicks,
                gpuReadbackMs,
                workerQueueMs,
                preprocessMs,
                ortInferenceMs,
                slot.rowMode,
                packed);
        }
        catch (Exception exception)
        {
            int failures =
                Interlocked.Increment(
                    ref _ortFailureCount);

            QueueStatus(
                "ORT DirectML inference failed: " +
                exception.GetType().Name +
                " " +
                exception.Message,
                true);

            if (failures >= 3)
            {
                _runtimeDisabled = true;
            }
        }
        finally
        {
            lock (_slotLock)
            {
                ClearSlotLocked(slotIndex);
            }
        }
    }

    private static void ConvertRgbaToNchw(
        byte[] rgba,
        float[] destination,
        bool flipRows)
    {
        const float inverse255 =
            1f / 255f;

        for (int y = 0; y < InputSize; y++)
        {
            int sourceY =
                flipRows
                    ? InputSize - 1 - y
                    : y;

            int sourceRow =
                sourceY * InputSize * 4;

            int destinationRow =
                y * InputSize;

            for (int x = 0; x < InputSize; x++)
            {
                int source =
                    sourceRow + x * 4;

                int pixel =
                    destinationRow + x;

                destination[pixel] =
                    rgba[source] * inverse255;

                destination[PixelCount + pixel] =
                    rgba[source + 1] * inverse255;

                destination[PixelCount * 2 + pixel] =
                    rgba[source + 2] * inverse255;
            }
        }
    }

    private void StoreSentisPackedOutput(
        long sourceHostTicks,
        Unity.InferenceEngine.Tensor<float> packedOutput,
        long sentisArrivalHostTicks)
    {
        PairState pair;

        lock (_pairLock)
        {
            if (!_pairs.TryGetValue(sourceHostTicks, out pair))
            {
                return;
            }
        }

        float[] copy =
            new float[PackedOutputLength];

        for (int i = 0; i < PackedOutputLength; i++)
        {
            copy[i] = packedOutput[i];
        }

        MatchedRecord matched = default(MatchedRecord);
        bool hasMatch = false;

        lock (_pairLock)
        {
            if (!_pairs.TryGetValue(sourceHostTicks, out pair))
            {
                return;
            }

            pair.sentisPacked = copy;
            pair.sentisArrivalHostTicks =
                sentisArrivalHostTicks;

            if (pair.ortPacked != null)
            {
                matched = BuildMatchedRecord(pair);
                _pairs.Remove(sourceHostTicks);
                hasMatch = true;
            }
        }

        if (hasMatch)
        {
            EnqueueMatchedRecord(matched);
        }
    }

    private void StoreOrtPackedOutput(
        long sourceHostTicks,
        long submitHostTicks,
        long readbackDoneHostTicks,
        long ortStartHostTicks,
        long ortDoneHostTicks,
        float gpuReadbackMs,
        float workerQueueMs,
        float preprocessMs,
        float ortInferenceMs,
        int rowMode,
        float[] packed)
    {
        PairState pair;
        MatchedRecord matched = default(MatchedRecord);
        bool hasMatch = false;

        lock (_pairLock)
        {
            if (!_pairs.TryGetValue(sourceHostTicks, out pair))
            {
                pair =
                    new PairState
                    {
                        sourceHostTicks = sourceHostTicks,
                        shadowSubmitHostTicks = submitHostTicks,
                        rowMode = rowMode
                    };

                _pairs[sourceHostTicks] = pair;
                _pairOrder.Enqueue(sourceHostTicks);
            }

            pair.shadowSubmitHostTicks = submitHostTicks;
            pair.shadowReadbackDoneHostTicks = readbackDoneHostTicks;
            pair.ortStartHostTicks = ortStartHostTicks;
            pair.ortDoneHostTicks = ortDoneHostTicks;
            pair.gpuReadbackMs = gpuReadbackMs;
            pair.workerQueueMs = workerQueueMs;
            pair.preprocessMs = preprocessMs;
            pair.ortInferenceMs = ortInferenceMs;
            pair.rowMode = rowMode;
            pair.ortPacked = packed;

            if (pair.sentisPacked != null)
            {
                matched = BuildMatchedRecord(pair);
                _pairs.Remove(sourceHostTicks);
                hasMatch = true;
            }

            TrimPairHistoryLocked();
        }

        if (hasMatch)
        {
            EnqueueMatchedRecord(matched);
        }
    }

    private void RegisterPairSubmission(
        long sourceHostTicks,
        long submitHostTicks,
        int rowMode)
    {
        lock (_pairLock)
        {
            if (!_pairs.ContainsKey(sourceHostTicks))
            {
                _pairs[sourceHostTicks] =
                    new PairState
                    {
                        sourceHostTicks = sourceHostTicks,
                        shadowSubmitHostTicks = submitHostTicks
                    };

                _pairOrder.Enqueue(sourceHostTicks);
            }

            if (_pairs.TryGetValue(sourceHostTicks, out PairState registeredPair))
            {
                registeredPair.rowMode = rowMode;
            }

            TrimPairHistoryLocked();
        }
    }

    private void TrimPairHistoryLocked()
    {
        while (
            _pairs.Count > PairHistoryCapacity &&
            _pairOrder.Count > 0)
        {
            long key = _pairOrder.Dequeue();
            _pairs.Remove(key);
        }

        while (_pairOrder.Count > PairHistoryCapacity * 2)
        {
            _pairOrder.Dequeue();
        }
    }

    private static MatchedRecord BuildMatchedRecord(
        PairState pair)
    {
        double sum = 0.0;
        float maximum = 0f;

        for (int i = 0; i < LandmarkValueCount; i++)
        {
            float difference =
                (float)Math.Abs(
                    pair.sentisPacked[i] -
                    pair.ortPacked[i]);

            sum += difference;

            if (difference > maximum)
            {
                maximum = difference;
            }
        }

        float mae =
            (float)(sum / LandmarkValueCount);

        float sentisLogit =
            pair.sentisPacked[LandmarkValueCount];

        float ortLogit =
            pair.ortPacked[LandmarkValueCount];

        float sentisPresence =
            Sigmoid(sentisLogit);

        float ortPresence =
            Sigmoid(ortLogit);

        return
            new MatchedRecord
            {
                sourceHostTicks = pair.sourceHostTicks,
                shadowSubmitHostTicks = pair.shadowSubmitHostTicks,
                shadowReadbackDoneHostTicks = pair.shadowReadbackDoneHostTicks,
                ortStartHostTicks = pair.ortStartHostTicks,
                ortDoneHostTicks = pair.ortDoneHostTicks,
                sentisArrivalHostTicks = pair.sentisArrivalHostTicks,
                gpuReadbackMs = pair.gpuReadbackMs,
                workerQueueMs = pair.workerQueueMs,
                preprocessMs = pair.preprocessMs,
                ortInferenceMs = pair.ortInferenceMs,
                shadowSubmitToOrtMs = TicksToMilliseconds(
                    pair.ortDoneHostTicks -
                    pair.shadowSubmitHostTicks),
                sourceToOrtMs = TicksToMilliseconds(
                    pair.ortDoneHostTicks -
                    pair.sourceHostTicks),
                sourceToSentisMs = TicksToMilliseconds(
                    pair.sentisArrivalHostTicks -
                    pair.sourceHostTicks),
                ortMinusSentisCompletionMs = TicksToMilliseconds(
                    pair.ortDoneHostTicks -
                    pair.sentisArrivalHostTicks),
                landmarkMaeRaw = mae,
                landmarkMaeNormalized = mae / InputSize,
                landmarkMaxAbsRaw = maximum,
                sentisPresenceLogit = sentisLogit,
                ortPresenceLogit = ortLogit,
                presenceLogitAbsDiff =
                    (float)Math.Abs(sentisLogit - ortLogit),
                sentisPresence = sentisPresence,
                ortPresence = ortPresence,
                presenceAbsDiff =
                    (float)Math.Abs(sentisPresence - ortPresence),
                rowMode = pair.rowMode,
                selectedRowMode = -1
            };
    }

    private void EnqueueMatchedRecord(
        MatchedRecord record)
    {
        ObserveOrientationParity(ref record);

        lock (_matchedLock)
        {
            _matchedQueue.Enqueue(record);
        }

        Interlocked.Increment(
            ref _matchedCount);
    }

    private void DrainMatchedRecords()
    {
        while (true)
        {
            MatchedRecord record;

            lock (_matchedLock)
            {
                if (_matchedQueue.Count == 0)
                {
                    break;
                }

                record = _matchedQueue.Dequeue();
            }

            if (_csvWriter != null)
            {
                WriteMatchedCsvRow(record);
            }
        }
    }

    private void StartCsvRecording()
    {
        if (_csvWriter != null)
        {
            return;
        }

        try
        {
            string directory =
                Path.Combine(
                    Application.persistentDataPath,
                    "KiwiOrtShadow");

            Directory.CreateDirectory(directory);

            _currentCsvPath =
                Path.Combine(
                    directory,
                    "KiwiOrtShadowTelemetry_" +
                    DateTime.Now.ToString(
                        "yyyyMMdd_HHmmss",
                        Invariant) +
                    ".csv");

            _csvWriter =
                new StreamWriter(
                    _currentCsvPath,
                    false,
                    new UTF8Encoding(false));

            _csvWriter.WriteLine(
                "sourceHostTicks,shadowSubmitHostTicks,shadowReadbackDoneHostTicks," +
                "ortStartHostTicks,ortDoneHostTicks,sentisArrivalHostTicks," +
                "gpuReadbackMs,workerQueueMs,preprocessMs,ortInferenceMs," +
                "shadowSubmitToOrtMs,sourceToOrtMs,sourceToSentisMs," +
                "ortMinusSentisCompletionMs,landmarkMaeRaw,landmarkMaeNormalized," +
                "landmarkMaxAbsRaw,sentisPresenceLogit,ortPresenceLogit," +
                "presenceLogitAbsDiff,sentisPresence,ortPresence,presenceAbsDiff," +
                "shadowScheduledCount,shadowReadbackCompletedCount," +
                "shadowReadbackErrorCount,shadowInputSkippedCount," +
                "shadowReadyReplacementCount,ortCompletedCount,ortFailureCount," +
                "matchedCount,rowMode,selectedRowMode");

            _csvRowsSinceFlush = 0;
            _csvRowsWritten = 0;

            Debug.Log(
                "[KiwiInferenceV41] shadow CSV started: " +
                _currentCsvPath);
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "[KiwiInferenceV41] shadow CSV start failed: " +
                exception.Message);

            _csvWriter = null;
            _currentCsvPath = string.Empty;
        }
    }

    private void StopCsvRecording()
    {
        if (_csvWriter == null)
        {
            return;
        }

        try
        {
            DrainMatchedRecords();
            _csvWriter.Flush();
            _csvWriter.Dispose();
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "[KiwiInferenceV41] shadow CSV close warning: " +
                exception.Message);
        }
        finally
        {
            _csvWriter = null;
        }

        Debug.Log(
            "[KiwiInferenceV41] shadow CSV stopped rows=" +
            _csvRowsWritten +
            " path=" +
            _currentCsvPath);
    }

    private void WriteMatchedCsvRow(
        MatchedRecord record)
    {
        StringBuilder row =
            new StringBuilder(512);

        Append(row, record.sourceHostTicks); Sep(row);
        Append(row, record.shadowSubmitHostTicks); Sep(row);
        Append(row, record.shadowReadbackDoneHostTicks); Sep(row);
        Append(row, record.ortStartHostTicks); Sep(row);
        Append(row, record.ortDoneHostTicks); Sep(row);
        Append(row, record.sentisArrivalHostTicks); Sep(row);
        Append(row, record.gpuReadbackMs); Sep(row);
        Append(row, record.workerQueueMs); Sep(row);
        Append(row, record.preprocessMs); Sep(row);
        Append(row, record.ortInferenceMs); Sep(row);
        Append(row, record.shadowSubmitToOrtMs); Sep(row);
        Append(row, record.sourceToOrtMs); Sep(row);
        Append(row, record.sourceToSentisMs); Sep(row);
        Append(row, record.ortMinusSentisCompletionMs); Sep(row);
        Append(row, record.landmarkMaeRaw); Sep(row);
        Append(row, record.landmarkMaeNormalized); Sep(row);
        Append(row, record.landmarkMaxAbsRaw); Sep(row);
        Append(row, record.sentisPresenceLogit); Sep(row);
        Append(row, record.ortPresenceLogit); Sep(row);
        Append(row, record.presenceLogitAbsDiff); Sep(row);
        Append(row, record.sentisPresence); Sep(row);
        Append(row, record.ortPresence); Sep(row);
        Append(row, record.presenceAbsDiff); Sep(row);
        Append(row, Volatile.Read(ref _shadowScheduledCount)); Sep(row);
        Append(row, Volatile.Read(ref _shadowReadbackCompletedCount)); Sep(row);
        Append(row, Volatile.Read(ref _shadowReadbackErrorCount)); Sep(row);
        Append(row, Volatile.Read(ref _shadowInputSkippedCount)); Sep(row);
        Append(row, Volatile.Read(ref _shadowReadyReplacementCount)); Sep(row);
        Append(row, Volatile.Read(ref _ortCompletedCount)); Sep(row);
        Append(row, Volatile.Read(ref _ortFailureCount)); Sep(row);
        Append(row, Volatile.Read(ref _matchedCount)); Sep(row);
        Append(row, record.rowMode); Sep(row);
        Append(row, record.selectedRowMode);

        _csvWriter.WriteLine(row.ToString());
        _csvRowsWritten++;
        _csvRowsSinceFlush++;

        if (_csvRowsSinceFlush >= CsvFlushIntervalRows)
        {
            _csvWriter.Flush();
            _csvRowsSinceFlush = 0;
        }
    }

    private void QueueStatus(
        string message,
        bool isError)
    {
        lock (_statusLock)
        {
            _pendingStatusMessage = message ?? string.Empty;
            _statusMessageIsError = isError;
            _statusLogged = false;
        }
    }

    private void EmitPendingStatus()
    {
        string message;
        bool isError;

        lock (_statusLock)
        {
            if (
                _statusLogged ||
                string.IsNullOrEmpty(_pendingStatusMessage))
            {
                return;
            }

            message = _pendingStatusMessage;
            isError = _statusMessageIsError;
            _statusLogged = true;
        }

        if (isError)
        {
            Debug.LogError(
                "[KiwiInferenceV41] " +
                message);
        }
        else
        {
            Debug.Log(
                "[KiwiInferenceV41] " +
                message);
        }
    }

    private void ClearSlotLocked(
        int slotIndex)
    {
        ShadowSlot slot = _slots[slotIndex];
        slot.state = SlotState.Free;
        slot.sourceHostTicks = 0L;
        slot.submitHostTicks = 0L;
        slot.readbackDoneHostTicks = 0L;
        slot.rowMode = 0;
    }

    private int ResolveRowModeForNextSubmission()
    {
        int selected = Volatile.Read(ref _selectedRowMode);

        if (selected >= 0)
        {
            return selected;
        }

        int sequence =
            Interlocked.Increment(
                ref _orientationCalibrationSequence);

        return sequence & 1;
    }

    private void ObserveOrientationParity(
        ref MatchedRecord record)
    {
        int selected = Volatile.Read(ref _selectedRowMode);

        if (selected >= 0)
        {
            record.selectedRowMode = selected;
            return;
        }

        lock (_pairLock)
        {
            selected = _selectedRowMode;

            if (selected < 0)
            {
                if (record.rowMode == 0)
                {
                    _nativeRowCalibrationCount++;
                    _nativeRowMaeSum += record.landmarkMaeRaw;
                }
                else
                {
                    _flipRowCalibrationCount++;
                    _flipRowMaeSum += record.landmarkMaeRaw;
                }

                if (
                    _nativeRowCalibrationCount >= OrientationCalibrationSamplesPerMode &&
                    _flipRowCalibrationCount >= OrientationCalibrationSamplesPerMode)
                {
                    double nativeMean =
                        _nativeRowMaeSum /
                        Math.Max(1, _nativeRowCalibrationCount);

                    double flipMean =
                        _flipRowMaeSum /
                        Math.Max(1, _flipRowCalibrationCount);

                    _selectedRowMode =
                        flipMean < nativeMean
                            ? 1
                            : 0;

                    selected = _selectedRowMode;

                    QueueStatus(
                        "input row calibration selected=" +
                        (selected == 1 ? "FLIP_Y" : "NATIVE") +
                        " nativeMae=" + nativeMean.ToString("F6", Invariant) +
                        " flipMae=" + flipMean.ToString("F6", Invariant),
                        false);
                }
            }
        }

        record.selectedRowMode = selected;
    }

    private static float Sigmoid(
        float value)
    {
        if (value >= 0f)
        {
            float z = (float)Math.Exp(-value);
            return 1f / (1f + z);
        }

        float negativeZ = (float)Math.Exp(value);
        return negativeZ / (1f + negativeZ);
    }

    private static float TicksToMilliseconds(
        long ticks)
    {
        if (ticks <= 0L)
        {
            return 0f;
        }

        return
            (float)(
                ticks * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency);
    }

    private static void Append(
        StringBuilder row,
        long value)
    {
        row.Append(
            value.ToString(Invariant));
    }

    private static void Append(
        StringBuilder row,
        int value)
    {
        row.Append(
            value.ToString(Invariant));
    }

    private static void Append(
        StringBuilder row,
        float value)
    {
        row.Append(
            value.ToString("R", Invariant));
    }

    private static void Sep(
        StringBuilder row)
    {
        row.Append(',');
    }
}
