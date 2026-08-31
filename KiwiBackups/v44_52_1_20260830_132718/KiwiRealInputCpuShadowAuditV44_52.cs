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
/// KiwiAvatarSystem v44.52
/// Real-input CPU shadow inference audit.
///
/// Observer-only:
/// - Production KiwiInferenceFaceTracker is never written to.
/// - Production backend, lane count, thresholds, ROI policy and publish path
///   are unchanged.
/// - The current source texture, flip flags and current crop matrix are read
///   through reflection.
/// - A separate 192x192 crop, CPU tensor and BackendType.CPU worker are used.
///
/// Purpose:
/// measure the REAL camera path cost:
/// source texture -> same ROI crop shader -> CPU tensor -> CPU model ->
/// async readable packed landmark output.
///
/// This is a diagnostic only. It publishes no landmarks.
/// </summary>
internal sealed class KiwiRealInputCpuShadowAuditV44_52 : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_52_REAL_INPUT_CPU_SHADOW_OBSERVER";

    private const string EnableVariable =
        "KIWI_V44_52_REAL_CPU_SHADOW_AUDIT";

    private const string DurationVariable =
        "KIWI_V44_52_REAL_CPU_SHADOW_SECONDS";

    private const string SampleHzVariable =
        "KIWI_V44_52_REAL_CPU_SHADOW_HZ";

    private const string ExpectedTrianglesVariable =
        "KIWI_V44_52_EXPECTED_TRIANGLES";

    private const string StableSecondsVariable =
        "KIWI_V44_52_STABLE_SECONDS";

    private const int InputSize = 192;
    private const int BaseLandmarkCount = 468;
    private const int PackedOutputLength =
        BaseLandmarkCount * 3 + 1;

    private const string LandmarkOutputName =
        "conv2d_20";

    private const string PresenceOutputName =
        "conv2d_30";

    private const int WarmupCount = 3;
    private const float DefaultDurationSeconds = 30f;
    private const float DefaultSampleHz = 2f;
    private const float DefaultStableSeconds = 8f;
    private const int DefaultExpectedTriangles = 254296;

    private static readonly int InputIsSrgbId =
        Shader.PropertyToID("_InputIsSRGB");

    private static bool _installed;

    private Worker _cpuWorker;
    private Tensor<float> _cpuInput;
    private Tensor<float> _pendingOutput;
    private RenderTexture _cropTexture;
    private Material _cropMaterial;
    private TextureTransform _textureTransform;

    private MonoBehaviour _runner;
    private Type _runnerType;
    private object _tracker;
    private Type _trackerType;

    private FieldInfo _trackerField;
    private FieldInfo _sourceField;
    private FieldInfo _flipHorizontalField;
    private FieldInfo _flipVerticalField;
    private MethodInfo _buildCropMatrixMethod;

    private bool _workerReady;
    private bool _pending;
    private bool _measuring;
    private bool _reportWritten;
    private bool _gateAnnounced;

    private int _warmupRemaining = WarmupCount;
    private int _expectedTriangles;
    private float _durationSeconds;
    private float _sampleHz;
    private float _stableSeconds;

    private double _stableSince = -1.0;
    private double _measurementStart;
    private double _nextRequestAt;
    private double _nextDiscoveryAt;

    private long _pendingTotalStartTicks;
    private int _pendingStartFrame;
    private double _pendingBlitSubmitMs;
    private double _pendingToTensorMs;
    private double _pendingScheduleMs;
    private float _pendingProductionPresenceAtIssue;

    private int _requestCount;
    private int _completedCount;
    private int _errorCount;
    private int _invalidCropCount;
    private int _presenceValidCount;
    private int _presenceLowCount;
    private int _nonFiniteOutputCount;

    private readonly List<double> _blitSubmitMs =
        new List<double>(128);

    private readonly List<double> _toTensorCpuMs =
        new List<double>(128);

    private readonly List<double> _scheduleCpuMs =
        new List<double>(128);

    private readonly List<double> _endToEndMs =
        new List<double>(128);

    private readonly List<int> _completionFrames =
        new List<int>(128);

    private readonly List<float> _shadowPresence =
        new List<float>(128);

    private readonly List<float> _productionPresenceAtIssue =
        new List<float>(128);

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

        if (!ReadBoolEnvironment(EnableVariable, false))
        {
            return;
        }

        _installed = true;

        GameObject go =
            new GameObject(
                "[Kiwi] v44.52 Real Input CPU Shadow Audit");

        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.DontSave;

        go.AddComponent<KiwiRealInputCpuShadowAuditV44_52>();
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
            "[Kiwi v44.52 Real CPU Shadow] WAIT_GATE " +
            "contract=" + Contract +
            " observerOnly=1" +
            " productionBackendChange=0" +
            " productionTrackerWrites=0" +
            " productionRoiWrites=0" +
            " sourceMode=REAL_CURRENT_TEXTURE" +
            " cropMode=SAME_SHADER_SAME_ROI_MATRIX" +
            " shadowBackend=CPU" +
            " shadowHz=" +
                _sampleHz.ToString("F2", CultureInfo.InvariantCulture) +
            " stableSeconds=" +
                _stableSeconds.ToString("F1", CultureInfo.InvariantCulture) +
            " expectedTriangles=" + _expectedTriangles);
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

        if (!_workerReady)
        {
            UpdateGate(now);
            return;
        }

        SampleProductionTelemetry();

        if (!_measuring)
        {
            return;
        }

        if (
            now - _measurementStart >=
            _durationSeconds)
        {
            if (!_pending)
            {
                CaptureEndCounters();
                WriteReport("COMPLETE");
            }

            return;
        }

        if (
            !_pending &&
            now >= _nextRequestAt)
        {
            IssueShadowRequest(
                isWarmup: false);

            _nextRequestAt =
                now + 1.0 / _sampleHz;
        }
    }

    private void UpdateGate(double now)
    {
        bool ready =
            CountEnabledSkinnedTriangles() ==
                _expectedTriangles &&
            RuntimeReflectionReady() &&
            TrackerHasRegion() &&
            GetCurrentSourceTexture() != null;

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
                    "[Kiwi v44.52 Real CPU Shadow] GATE_MATCH " +
                    "triangles=" + _expectedTriangles +
                    " trackerReady=1 sourceReady=1" +
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
            InstallShadowWorker();

            IssueShadowRequest(
                isWarmup: true);
        }
        catch (Exception exception)
        {
            _errorCount++;

            Debug.LogError(
                "[Kiwi v44.52 Real CPU Shadow] INIT_FAIL " +
                exception.GetType().Name + " " +
                exception.Message);

            WriteReport("INIT_FAIL");
        }
    }

    private void DiscoverRuntimeObjects(double now)
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

            FieldInfo sourceField =
                type.GetField(
                    "_sentisSourceTexture",
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
                sourceField == null ||
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

            _trackerField = trackerField;
            _sourceField = sourceField;
            _flipHorizontalField = flipHField;
            _flipVerticalField = flipVField;
            _buildCropMatrixMethod = cropMethod;

            Debug.Log(
                "[Kiwi v44.52 Real CPU Shadow] REFLECTION_READY " +
                "runner=" + _runnerType.FullName +
                " tracker=" + _trackerType.FullName +
                " readOnlyFields=4" +
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
            _sourceField == null ||
            _flipHorizontalField == null ||
            _flipVerticalField == null ||
            _buildCropMatrixMethod == null)
        {
            return false;
        }

        // If Production recreated the tracker, rediscover rather than using
        // a stale object reference.
        try
        {
            object currentTracker =
                _trackerField.GetValue(
                    _runner);

            if (
                currentTracker == null ||
                !ReferenceEquals(
                    currentTracker,
                    _tracker))
            {
                _runner = null;
                _tracker = null;
                _runnerType = null;
                _trackerType = null;
                _trackerField = null;
                _sourceField = null;
                _flipHorizontalField = null;
                _flipVerticalField = null;
                _buildCropMatrixMethod = null;

                return false;
            }
        }
        catch
        {
            return false;
        }

        return true;
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

    private Texture GetCurrentSourceTexture()
    {
        if (
            _runner == null ||
            _sourceField == null)
        {
            return null;
        }

        try
        {
            return
                _sourceField.GetValue(_runner)
                as Texture;
        }
        catch
        {
            return null;
        }
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
                field.GetValue(_runner);

            return
                value is bool flag &&
                flag;
        }
        catch
        {
            return false;
        }
    }

    private Matrix4x4 GetCurrentCropMatrix()
    {
        if (
            _tracker == null ||
            _buildCropMatrixMethod == null)
        {
            throw new InvalidOperationException(
                "Crop matrix reflection unavailable.");
        }

        object value =
            _buildCropMatrixMethod.Invoke(
                _tracker,
                null);

        if (!(value is Matrix4x4 matrix))
        {
            throw new InvalidOperationException(
                "BuildCropMatrix returned invalid value.");
        }

        return matrix;
    }

    private void InstallShadowWorker()
    {
        if (_workerReady)
        {
            return;
        }

        ModelAsset asset =
            Resources.Load<ModelAsset>(
                "KiwiFaceLandmarkInference");

        Shader cropShader =
            Resources.Load<Shader>(
                "KiwiInferenceFaceCrop");

        if (
            asset == null ||
            cropShader == null)
        {
            throw new InvalidOperationException(
                "Required shadow model/crop shader is missing.");
        }

        Model source =
            ModelLoader.Load(asset);

        Model packed =
            BuildSingleReadbackModel(
                source);

        _cpuWorker =
            new Worker(
                packed,
                BackendType.CPU);

        float[] zero =
            new float[
                3 *
                InputSize *
                InputSize];

        _cpuInput =
            new Tensor<float>(
                new TensorShape(
                    1,
                    3,
                    InputSize,
                    InputSize),
                zero);

        _cropTexture =
            new RenderTexture(
                InputSize,
                InputSize,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                name =
                    "Kiwi v44.52 Real CPU Shadow Crop",
                filterMode =
                    FilterMode.Bilinear,
                wrapMode =
                    TextureWrapMode.Clamp,
                useMipMap =
                    false,
                autoGenerateMips =
                    false,
                hideFlags =
                    HideFlags.DontSave
            };

        _cropTexture.Create();

        _cropMaterial =
            new Material(cropShader)
            {
                name =
                    "Kiwi v44.52 Real CPU Shadow Crop Material",
                hideFlags =
                    HideFlags.DontSave
            };

        _textureTransform =
            new TextureTransform()
                .SetTensorLayout(
                    TensorLayout.NCHW)
                .SetCoordOrigin(
                    CoordOrigin.TopLeft);

        _workerReady = true;

        Debug.Log(
            "[Kiwi v44.52 Real CPU Shadow] WORKER_READY " +
            "backend=CPU" +
            " model=KiwiFaceLandmarkInference" +
            " crop=192x192_ARGB32_LINEAR" +
            " tensor=NCHW_TOP_LEFT" +
            " productionWorkerTouched=0");
    }

    private void IssueShadowRequest(
        bool isWarmup)
    {
        if (
            !_workerReady ||
            _pending ||
            _cpuWorker == null ||
            _cpuInput == null ||
            _cropTexture == null ||
            _cropMaterial == null)
        {
            return;
        }

        Texture source =
            GetCurrentSourceTexture();

        if (
            source == null ||
            !TrackerHasRegion())
        {
            _invalidCropCount++;
            return;
        }

        Matrix4x4 cropMatrix;

        try
        {
            cropMatrix =
                GetCurrentCropMatrix();
        }
        catch
        {
            _invalidCropCount++;
            return;
        }

        bool flipH =
            GetFlip(
                _flipHorizontalField);

        bool flipV =
            GetFlip(
                _flipVerticalField);

        Matrix4x4 samplingMatrix =
            BuildFlipMatrix(
                flipH,
                flipV) *
            cropMatrix;

        _cropMaterial.SetMatrix(
            "_Xform",
            samplingMatrix);

        bool preserveStoredSrgb =
            QualitySettings.activeColorSpace ==
                ColorSpace.Linear &&
            source.isDataSRGB;

        _cropMaterial.SetFloat(
            InputIsSrgbId,
            preserveStoredSrgb
                ? 1f
                : 0f);

        long totalStart =
            Stopwatch.GetTimestamp();

        long blitStart =
            totalStart;

        Graphics.Blit(
            source,
            _cropTexture,
            _cropMaterial,
            0);

        long afterBlit =
            Stopwatch.GetTimestamp();

        TextureConverter.ToTensor(
            _cropTexture,
            _cpuInput,
            _textureTransform);

        long afterTensor =
            Stopwatch.GetTimestamp();

        try
        {
            _cpuWorker.Schedule(
                _cpuInput);

            Tensor<float> output =
                _cpuWorker.PeekOutput(0)
                as Tensor<float>;

            if (
                output == null ||
                output.shape.length !=
                    PackedOutputLength)
            {
                throw new InvalidOperationException(
                    "Packed CPU output invalid.");
            }

            long afterSchedule =
                Stopwatch.GetTimestamp();

            double blitMs =
                TicksToMilliseconds(
                    afterBlit -
                    blitStart);

            double toTensorMs =
                TicksToMilliseconds(
                    afterTensor -
                    afterBlit);

            double scheduleMs =
                TicksToMilliseconds(
                    afterSchedule -
                    afterTensor);

            if (!isWarmup)
            {
                _requestCount++;
                _blitSubmitMs.Add(blitMs);
                _toTensorCpuMs.Add(toTensorMs);
                _scheduleCpuMs.Add(scheduleMs);
                _productionPresenceAtIssue.Add(
                    ReadTrackerFloat(
                        "LatestPresence",
                        0f));
            }

            _pendingTotalStartTicks =
                totalStart;

            _pendingStartFrame =
                Time.frameCount;

            _pendingBlitSubmitMs =
                blitMs;

            _pendingToTensorMs =
                toTensorMs;

            _pendingScheduleMs =
                scheduleMs;

            _pendingProductionPresenceAtIssue =
                ReadTrackerFloat(
                    "LatestPresence",
                    0f);

            _pendingOutput =
                output;

            _pending = true;

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

                        double totalMs =
                            TicksToMilliseconds(
                                done -
                                _pendingTotalStartTicks);

                        int frameDelta =
                            Mathf.Max(
                                0,
                                Time.frameCount -
                                _pendingStartFrame);

                        float rawPresence =
                            readable[
                                BaseLandmarkCount *
                                3];

                        float presence =
                            Sigmoid(
                                rawPresence);

                        bool finite =
                            IsFinite(
                                rawPresence) &&
                            IsFinite(
                                presence);

                        if (finite)
                        {
                            int probeCount =
                                Mathf.Min(
                                    BaseLandmarkCount *
                                    3,
                                    60);

                            for (
                                int i = 0;
                                i < probeCount;
                                i++)
                            {
                                if (
                                    !IsFinite(
                                        readable[i]))
                                {
                                    finite = false;
                                    break;
                                }
                            }
                        }

                        if (!isWarmup)
                        {
                            _endToEndMs.Add(
                                totalMs);

                            _completionFrames.Add(
                                frameDelta);

                            _shadowPresence.Add(
                                presence);

                            _completedCount++;

                            if (!finite)
                            {
                                _nonFiniteOutputCount++;
                            }
                            else if (presence >= 0.5f)
                            {
                                _presenceValidCount++;
                            }
                            else
                            {
                                _presenceLowCount++;
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        _errorCount++;

                        Debug.LogWarning(
                            "[Kiwi v44.52 Real CPU Shadow] " +
                            "COMPLETE_ERROR " +
                            exception.GetType().Name);
                    }
                    finally
                    {
                        if (readable != null)
                        {
                            readable.Dispose();
                        }

                        _pendingOutput = null;
                        _pending = false;

                        if (isWarmup)
                        {
                            _warmupRemaining--;

                            if (
                                _warmupRemaining >
                                0)
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
            _pending = false;
            _pendingOutput = null;
            _errorCount++;

            Debug.LogWarning(
                "[Kiwi v44.52 Real CPU Shadow] REQUEST_ERROR " +
                exception.GetType().Name + " " +
                exception.Message);

            if (isWarmup)
            {
                WriteReport(
                    "WARMUP_FAIL");
            }
        }
    }

    private void BeginMeasurement()
    {
        _measuring = true;

        _measurementStart =
            Time.realtimeSinceStartupAsDouble;

        _nextRequestAt =
            _measurementStart;

        CaptureStartCounters();

        Debug.Log(
            "[Kiwi v44.52 Real CPU Shadow] MEASURE_START " +
            "observerOnly=1" +
            " backend=CPU" +
            " input=REAL_CURRENT_CAMERA_CROP" +
            " shadowHz=" +
                _sampleHz.ToString(
                    "F2",
                    CultureInfo.InvariantCulture) +
            " durationSeconds=" +
                _durationSeconds.ToString(
                    "F1",
                    CultureInfo.InvariantCulture) +
            " warmupComplete=1" +
            " stableGateComplete=1" +
            " productionBackendChange=0");
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

    private int CountEnabledSkinnedTriangles()
    {
        SkinnedMeshRenderer[] renderers =
            FindObjectsByType<SkinnedMeshRenderer>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

        long total =
            0L;

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
                    (long)mesh.GetIndexCount(s) /
                    3L;
            }
        }

        return
            total > int.MaxValue
                ? int.MaxValue
                : (int)total;
    }

    private void WriteReport(
        string status)
    {
        if (_reportWritten)
        {
            return;
        }

        _reportWritten =
            true;

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
                "KiwiRealInputCpuShadow_v44_52_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture) +
                ".txt");

        List<string> lines =
            new List<string>();

        lines.Add(
            "KiwiAvatarSystem v44.52 Real Input CPU Shadow Audit");
        lines.Add(
            "contract=" + Contract);
        lines.Add(
            "status=" + status);
        lines.Add(
            "observerOnly=1");
        lines.Add(
            "productionBackendChange=0");
        lines.Add(
            "productionTrackerWrites=0");
        lines.Add(
            "productionRoiWrites=0");
        lines.Add(
            "shadowBackend=CPU");
        lines.Add(
            "shadowModel=KiwiFaceLandmarkInference");
        lines.Add(
            "shadowInput=REAL_CURRENT_CAMERA_CROP");
        lines.Add(
            "shadowCrop=192x192_ARGB32_LINEAR_NCHW_TOP_LEFT");
        lines.Add(
            "stableGateSeconds=" +
            _stableSeconds.ToString(
                "F3",
                CultureInfo.InvariantCulture));
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
        lines.Add(
            "shadowRequestCount=" +
            _requestCount);
        lines.Add(
            "shadowCompletedCount=" +
            _completedCount);
        lines.Add(
            "shadowErrorCount=" +
            _errorCount);
        lines.Add(
            "invalidCropCount=" +
            _invalidCropCount);
        lines.Add(
            "presenceValidCount=" +
            _presenceValidCount);
        lines.Add(
            "presenceLowCount=" +
            _presenceLowCount);
        lines.Add(
            "nonFiniteOutputCount=" +
            _nonFiniteOutputCount);
        lines.Add("");

        AppendDoubleStats(
            lines,
            "realCropBlitSubmitCpuMs",
            _blitSubmitMs);

        AppendDoubleStats(
            lines,
            "realCropTextureToCpuTensorMs",
            _toTensorCpuMs);

        AppendDoubleStats(
            lines,
            "realCpuWorkerScheduleCpuMs",
            _scheduleCpuMs);

        AppendDoubleStats(
            lines,
            "realCpuEndToEndServiceMs",
            _endToEndMs);

        AppendIntStats(
            lines,
            "realCpuCompletionFrames",
            _completionFrames);

        AppendFloatStats(
            lines,
            "realCpuShadowPresence",
            _shadowPresence);

        AppendFloatStats(
            lines,
            "productionPresenceAtShadowIssue",
            _productionPresenceAtIssue);

        AppendFloatStats(
            lines,
            "sampledProductionTrackerLatencyMs",
            _productionLatencySamples);

        AppendIntStats(
            lines,
            "sampledProductionActiveLanes",
            _productionActiveLanes);

        lines.Add("");
        lines.Add(
            "[PRODUCTION_COUNTER_DELTAS]");

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

        int droppedDelta =
            Math.Max(
                0,
                _endDropped -
                _startDropped);

        int staleDelta =
            Math.Max(
                0,
                _endStale -
                _startStale);

        lines.Add(
            "scheduledDelta=" +
            scheduledDelta);
        lines.Add(
            "readbackCompletedDelta=" +
            readbackDelta);
        lines.Add(
            "completedDelta=" +
            completedDelta);
        lines.Add(
            "droppedFreshDelta=" +
            droppedDelta);
        lines.Add(
            "discardedStaleDelta=" +
            staleDelta);

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
        lines.Add(
            "[DECISION_GUIDE]");
        lines.Add(
            "CPU_REAL_INPUT_STRONG if end-to-end median remains <25 ms, " +
            "completion is usually 1 frame, and TextureToCpuTensor cost is small.");
        lines.Add(
            "GPU_PRESENTATION_TO_CPU_PATH_WEAK if TextureToCpuTensor itself " +
            "dominates or causes frame-time regression.");
        lines.Add(
            "Even a strong result does NOT switch Production; next step is a " +
            "real-output equivalence/correctness A/B before backend adoption.");

        File.WriteAllLines(
            path,
            lines);

        Debug.Log(
            "[Kiwi v44.52 Real CPU Shadow] " +
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
                "Expected landmark model outputs are missing.");
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
            lines.Add(
                name + ".samples=0");
            return;
        }

        double[] data =
            values.ToArray();

        Array.Sort(data);

        double sum =
            0.0;

        for (
            int i = 0;
            i < data.Length;
            i++)
        {
            sum +=
                data[i];
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
            lines.Add(
                name + ".samples=0");
            return;
        }

        double[] data =
            new double[
                values.Count];

        for (
            int i = 0;
            i < values.Count;
            i++)
        {
            data[i] =
                values[i];
        }

        Array.Sort(data);

        double sum =
            0.0;

        for (
            int i = 0;
            i < data.Length;
            i++)
        {
            sum +=
                data[i];
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

    private static void AppendIntStats(
        List<string> lines,
        string name,
        List<int> values)
    {
        if (
            values == null ||
            values.Count == 0)
        {
            lines.Add(
                name + ".samples=0");
            return;
        }

        int[] data =
            values.ToArray();

        Array.Sort(data);

        long sum =
            0L;

        for (
            int i = 0;
            i < data.Length;
            i++)
        {
            sum +=
                data[i];
        }

        lines.Add(
            name + ".samples=" +
            data.Length);

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
            WriteReport(
                "QUIT");
        }
    }

    private void OnDestroy()
    {
        if (_cpuWorker != null)
        {
            _cpuWorker.Dispose();
            _cpuWorker =
                null;
        }

        if (_cpuInput != null)
        {
            _cpuInput.Dispose();
            _cpuInput =
                null;
        }

        if (_cropTexture != null)
        {
            if (_cropTexture.IsCreated())
            {
                _cropTexture.Release();
            }

            Destroy(
                _cropTexture);

            _cropTexture =
                null;
        }

        if (_cropMaterial != null)
        {
            Destroy(
                _cropMaterial);

            _cropMaterial =
                null;
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
