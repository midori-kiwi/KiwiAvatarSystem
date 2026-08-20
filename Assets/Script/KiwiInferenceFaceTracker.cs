using System;
using Unity.InferenceEngine;
using UnityEngine;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    /// <summary>
    /// Low-latency GPU landmark path.
    ///
    /// v2.3 changes the GPU -> CPU boundary from a synchronous
    /// ReadbackAndClone() to a one-in-flight asynchronous readback mailbox.
    ///
    /// Important timing rule:
    /// the crop matrix and source host timestamp are captured when the frame is
    /// actually scheduled. When readback completes later, that exact matrix and
    /// timestamp are used for the returned landmarks.
    ///
    /// MediaPipe remains responsible for initial acquisition, periodic
    /// correction, blendshapes and fail-safe fallback.
    /// </summary>
    public sealed class KiwiInferenceFaceTracker : IDisposable
    {
        public const int BaseLandmarkCount = 468;
        public const int CompatibleLandmarkCount = 478;

        private const int InputSize = 192;
        private const int PackedOutputLength =
            BaseLandmarkCount * 3 + 1;

        private const string LandmarkOutputName =
            "conv2d_20";

        private const string PresenceOutputName =
            "conv2d_30";

        private readonly Worker _worker;
        private readonly Tensor<float> _input;
        private readonly RenderTexture _cropTexture;
        private readonly Material _cropMaterial;
        private readonly TextureTransform _textureTransform;

        private readonly Vector3[] _landmarks =
            new Vector3[CompatibleLandmarkCount];

        private Vector2 _regionCenter;
        private float _regionSize;
        private float _regionRollRadians;
        private bool _hasRegion;
        private int _consecutiveFailures;

        // ---------------------------------------------------------
        // Async output mailbox
        // ---------------------------------------------------------

        private Tensor<float> _pendingOutput;
        private bool _readbackPending;

        private Matrix4x4 _pendingCropMatrix =
            Matrix4x4.identity;

        private long _pendingSourceHostTicks;
        private long _pendingStartedHostTicks;
        private int _pendingAnchorRevision;
        private int _anchorRevision;

        private long _latestCompletedSourceHostTicks;
        private long _latestCompletedArrivalHostTicks;

        private int _scheduledFrameCount;
        private int _completedFrameCount;
        private int _droppedFreshFrameCount;

        public float MinimumPresence { get; set; } = 0.5f;

        public bool HasRegion => _hasRegion;

        public bool IsTracking =>
            _hasRegion &&
            _consecutiveFailures < 4;

        public bool IsAsyncReadbackPending =>
            _readbackPending;

        public float LatestPresence { get; private set; }

        /// <summary>
        /// Schedule-to-readback-completion latency for the latest completed
        /// inference result. It no longer contains a forced main-thread stall.
        /// </summary>
        public float LatestLatencyMs { get; private set; }

        public long LatestCompletedSourceHostTicks =>
            _latestCompletedSourceHostTicks;

        public long LatestCompletedArrivalHostTicks =>
            _latestCompletedArrivalHostTicks;

        public int ScheduledFrameCount =>
            _scheduledFrameCount;

        public int CompletedFrameCount =>
            _completedFrameCount;

        public int DroppedFreshFrameCount =>
            _droppedFreshFrameCount;

        public KiwiInferenceFaceTracker(
            ModelAsset modelAsset,
            Shader cropShader)
        {
            if (modelAsset == null)
            {
                throw new ArgumentNullException(
                    nameof(modelAsset));
            }

            if (cropShader == null)
            {
                throw new ArgumentNullException(
                    nameof(cropShader));
            }

            Model model =
                BuildSingleReadbackModel(
                    ModelLoader.Load(modelAsset));

            _worker =
                new Worker(
                    model,
                    BackendType.GPUCompute);

            _input =
                new Tensor<float>(
                    new TensorShape(
                        1,
                        3,
                        InputSize,
                        InputSize));

            _cropTexture =
                new RenderTexture(
                    InputSize,
                    InputSize,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear)
                {
                    name =
                        "Kiwi Inference Face Crop",
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
                        "Kiwi Inference Face Crop Material",
                    hideFlags =
                        HideFlags.DontSave
                };

            _textureTransform =
                new TextureTransform()
                    .SetTensorLayout(
                        TensorLayout.NCHW)
                    .SetCoordOrigin(
                        CoordOrigin.TopLeft);
        }

        public void Dispose()
        {
            _readbackPending = false;
            _pendingOutput = null;

            _worker?.Dispose();
            _input?.Dispose();

            if (_cropTexture != null)
            {
                if (_cropTexture.IsCreated())
                {
                    _cropTexture.Release();
                }

                UnityEngine.Object.Destroy(
                    _cropTexture);
            }

            if (_cropMaterial != null)
            {
                UnityEngine.Object.Destroy(
                    _cropMaterial);
            }
        }

        public void Reset()
        {
            _hasRegion = false;
            _consecutiveFailures = 0;

            LatestPresence = 0f;
            LatestLatencyMs = 0f;

            _readbackPending = false;
            _pendingOutput = null;

            _pendingSourceHostTicks = 0L;
            _pendingStartedHostTicks = 0L;
            _pendingAnchorRevision = 0;
            _anchorRevision = 0;
            _latestCompletedSourceHostTicks = 0L;
            _latestCompletedArrivalHostTicks = 0L;
        }

        public void ApplyExternalAnchor(
            UnityEngine.Rect regionTopLeft,
            float rollRadiansBottomLeft,
            bool force)
        {
            float size =
                Mathf.Clamp(
                    Mathf.Max(
                        regionTopLeft.width,
                        regionTopLeft.height),
                    0.08f,
                    1.40f);

            Vector2 centerTopLeft =
                regionTopLeft.center;

            Vector2 centerBottomLeft =
                new Vector2(
                    centerTopLeft.x,
                    1f - centerTopLeft.y);

            if (!_hasRegion || force)
            {
                _regionCenter =
                    centerBottomLeft;

                _regionSize =
                    size;

                _regionRollRadians =
                    rollRadiansBottomLeft;

                _hasRegion =
                    true;

                _consecutiveFailures =
                    0;

                _anchorRevision++;

                return;
            }

            float distance =
                Vector2.Distance(
                    _regionCenter,
                    centerBottomLeft);

            // A current Inference Engine ROI is newer than asynchronous
            // MediaPipe correction. Accept only material drift/loss.
            if (
                distance >
                Mathf.Max(
                    0.025f,
                    _regionSize * 0.35f)
            )
            {
                _regionCenter =
                    centerBottomLeft;

                _regionSize =
                    size;

                _regionRollRadians =
                    rollRadiansBottomLeft;

                _consecutiveFailures =
                    0;

                _anchorRevision++;
            }
        }

        /// <summary>
        /// Compatibility synchronous path.
        ///
        /// Kept so older callers still compile. The v2.3 Runner installer moves
        /// the live hybrid path to TryProcessAsync().
        /// </summary>
        public bool TryProcess(
            Texture source,
            bool flipHorizontally,
            bool flipVertically,
            out Vector3[] landmarks,
            out Quaternion geometricRotation)
        {
            landmarks = null;
            geometricRotation =
                Quaternion.identity;

            if (!_hasRegion || source == null)
            {
                return false;
            }

            long started =
                System.Diagnostics.Stopwatch
                    .GetTimestamp();

            Matrix4x4 cropMatrix =
                BuildCropMatrix();

            ScheduleModel(
                source,
                flipHorizontally,
                flipVertically,
                cropMatrix);

            Tensor<float> packedOutput =
                _worker.PeekOutput(0)
                as Tensor<float>;

            if (
                packedOutput == null ||
                packedOutput.shape.length !=
                    PackedOutputLength
            )
            {
                RegisterFailure();
                return false;
            }

            using Tensor<float> readableOutput =
                packedOutput.ReadbackAndClone();

            bool valid =
                DecodeReadableOutput(
                    readableOutput,
                    cropMatrix,
                    true,
                    out landmarks,
                    out geometricRotation);

            RecordLatency(started);

            return valid;
        }

        /// <summary>
        /// Non-blocking live path.
        ///
        /// One inference/readback is allowed in flight. While it is pending,
        /// the Runner keeps only its newest camera generation as a one-slot
        /// mailbox. No old inference queue is allowed to accumulate.
        ///
        /// A completed result returns the exact host timestamp belonging to the
        /// source frame that was scheduled for that inference.
        /// </summary>
        public bool TryProcessAsync(
            Texture source,
            bool flipHorizontally,
            bool flipVertically,
            long latestSourceHostTicks,
            bool scheduleLatestSource,
            out bool scheduledLatestSource,
            out Vector3[] landmarks,
            out Quaternion geometricRotation,
            out long completedSourceHostTicks)
        {
            scheduledLatestSource =
                false;

            landmarks =
                null;

            geometricRotation =
                Quaternion.identity;

            completedSourceHostTicks =
                0L;

            bool completedValidResult =
                false;

            // -------------------------------------------------
            // Consume first, without blocking.
            // -------------------------------------------------

            if (_readbackPending)
            {
                if (
                    _pendingOutput != null &&
                    _pendingOutput.IsReadbackRequestDone()
                )
                {
                    long arrivalHostTicks =
                        System.Diagnostics.Stopwatch
                            .GetTimestamp();

                    Matrix4x4 completedCropMatrix =
                        _pendingCropMatrix;

                    long completedTicks =
                        _pendingSourceHostTicks;

                    long startedTicks =
                        _pendingStartedHostTicks;

                    int completedAnchorRevision =
                        _pendingAnchorRevision;

                    Tensor<float> completedOutput =
                        _pendingOutput;

                    _readbackPending =
                        false;

                    _pendingOutput =
                        null;

                    _pendingSourceHostTicks =
                        0L;

                    _pendingStartedHostTicks =
                        0L;

                    _pendingAnchorRevision =
                        0;

                    try
                    {
                        // ReadbackRequest() is already complete, so this copy
                        // should not wait for GPU execution.
                        using Tensor<float> readableOutput =
                            completedOutput
                                .ReadbackAndClone();

                        completedValidResult =
                            DecodeReadableOutput(
                                readableOutput,
                                completedCropMatrix,
                                completedAnchorRevision ==
                                    _anchorRevision,
                                out landmarks,
                                out geometricRotation);

                        if (completedValidResult)
                        {
                            _latestCompletedSourceHostTicks =
                                completedTicks;

                            _latestCompletedArrivalHostTicks =
                                arrivalHostTicks;

                            completedSourceHostTicks =
                                completedTicks;

                            _completedFrameCount++;
                        }
                    }
                    catch
                    {
                        RegisterFailure();
                        completedValidResult =
                            false;
                    }

                    RecordLatency(
                        startedTicks,
                        arrivalHostTicks);
                }
                else
                {
                    if (scheduleLatestSource)
                    {
                        _droppedFreshFrameCount++;
                    }

                    return false;
                }
            }

            // -------------------------------------------------
            // Schedule newest mailbox frame after the previous
            // output has been consumed.
            // -------------------------------------------------

            if (
                scheduleLatestSource &&
                !_readbackPending &&
                _hasRegion &&
                source != null
            )
            {
                scheduledLatestSource =
                    TryScheduleAsync(
                        source,
                        flipHorizontally,
                        flipVertically,
                        latestSourceHostTicks);
            }

            return completedValidResult;
        }

        /// <summary>
        /// Packs landmarks and presence on the GPU so the live path crosses the
        /// GPU/CPU boundary only once.
        ///
        /// Output layout:
        /// 1404 landmark floats followed by one presence float.
        /// </summary>
        public static Model BuildSingleReadbackModel(
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
                presenceIndex < 0
            )
            {
                throw new InvalidOperationException(
                    "Face landmark model does not expose the expected outputs.");
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
                i++
            )
            {
                if (
                    model.outputs[i].name ==
                    outputName
                )
                {
                    return i;
                }
            }

            return -1;
        }

        private bool TryScheduleAsync(
            Texture source,
            bool flipHorizontally,
            bool flipVertically,
            long sourceHostTicks)
        {
            if (
                _readbackPending ||
                !_hasRegion ||
                source == null
            )
            {
                return false;
            }

            try
            {
                Matrix4x4 cropMatrix =
                    BuildCropMatrix();

                ScheduleModel(
                    source,
                    flipHorizontally,
                    flipVertically,
                    cropMatrix);

                Tensor<float> packedOutput =
                    _worker.PeekOutput(0)
                    as Tensor<float>;

                if (
                    packedOutput == null ||
                    packedOutput.shape.length !=
                        PackedOutputLength
                )
                {
                    RegisterFailure();
                    return false;
                }

                _pendingCropMatrix =
                    cropMatrix;

                _pendingSourceHostTicks =
                    sourceHostTicks > 0L
                        ? sourceHostTicks
                        : System.Diagnostics.Stopwatch
                            .GetTimestamp();

                _pendingStartedHostTicks =
                    System.Diagnostics.Stopwatch
                        .GetTimestamp();

                _pendingAnchorRevision =
                    _anchorRevision;

                _pendingOutput =
                    packedOutput;

                // This call schedules the GPU->CPU transfer and returns.
                _pendingOutput.ReadbackRequest();

                _readbackPending =
                    true;

                _scheduledFrameCount++;

                return true;
            }
            catch
            {
                _pendingOutput =
                    null;

                _readbackPending =
                    false;

                _pendingSourceHostTicks =
                    0L;

                _pendingStartedHostTicks =
                    0L;

                _pendingAnchorRevision =
                    0;

                RegisterFailure();

                return false;
            }
        }

        private void ScheduleModel(
            Texture source,
            bool flipHorizontally,
            bool flipVertically,
            Matrix4x4 cropMatrix)
        {
            Matrix4x4 samplingMatrix =
                BuildFlipMatrix(
                    flipHorizontally,
                    flipVertically) *
                cropMatrix;

            _cropMaterial.SetMatrix(
                "_Xform",
                samplingMatrix);

            Graphics.Blit(
                source,
                _cropTexture,
                _cropMaterial,
                0);

            TextureConverter.ToTensor(
                _cropTexture,
                _input,
                _textureTransform);

            _worker.Schedule(_input);
        }

        private bool DecodeReadableOutput(
            Tensor<float> readableOutput,
            Matrix4x4 cropMatrix,
            bool updateRegionFromResult,
            out Vector3[] landmarks,
            out Quaternion geometricRotation)
        {
            landmarks =
                null;

            geometricRotation =
                Quaternion.identity;

            if (
                readableOutput == null ||
                readableOutput.shape.length !=
                    PackedOutputLength
            )
            {
                RegisterFailure();
                return false;
            }

            float rawPresence =
                readableOutput[
                    BaseLandmarkCount * 3];

            LatestPresence =
                NormalizePresence(
                    rawPresence);

            if (
                !IsFinite(LatestPresence) ||
                LatestPresence <
                    Mathf.Clamp01(
                        MinimumPresence)
            )
            {
                RegisterFailure();
                return false;
            }

            for (
                int i = 0;
                i < BaseLandmarkCount;
                i++
            )
            {
                float cropX =
                    readableOutput[
                        i * 3] /
                    InputSize;

                float cropYBottom =
                    1f -
                    readableOutput[
                        i * 3 + 1] /
                    InputSize;

                float cropZ =
                    readableOutput[
                        i * 3 + 2] /
                    InputSize;

                Vector3 sourceBottom =
                    cropMatrix
                        .MultiplyPoint3x4(
                            new Vector3(
                                cropX,
                                cropYBottom,
                                0f));

                Vector3 point =
                    new Vector3(
                        sourceBottom.x,
                        1f - sourceBottom.y,
                        cropZ *
                        ExtractRegionSize(
                            cropMatrix));

                if (!IsFinite(point))
                {
                    RegisterFailure();
                    return false;
                }

                _landmarks[i] =
                    point;
            }

            SynthesizeIrisLandmarks(
                _landmarks);

            if (updateRegionFromResult)
            {
                UpdateRegionFromLandmarks(
                    _landmarks);
            }

            geometricRotation =
                CalculateGeometricRotation(
                    _landmarks);

            _consecutiveFailures =
                0;

            landmarks =
                _landmarks;

            return true;
        }

        /// <summary>
        /// Recover scheduled ROI scale from the affine crop matrix instead of
        /// using the current ROI, because the current ROI can be updated after a
        /// later MediaPipe anchor arrives while this async result is in flight.
        /// </summary>
        private static float ExtractRegionSize(
            Matrix4x4 cropMatrix)
        {
            Vector3 xAxis =
                cropMatrix.MultiplyVector(
                    Vector3.right);

            Vector3 yAxis =
                cropMatrix.MultiplyVector(
                    Vector3.up);

            float size =
                Mathf.Max(
                    xAxis.magnitude,
                    yAxis.magnitude);

            return Mathf.Clamp(
                size,
                0.08f,
                1.40f);
        }

        private Matrix4x4 BuildCropMatrix()
        {
            return
                Matrix4x4.Translate(
                    new Vector3(
                        _regionCenter.x,
                        _regionCenter.y,
                        0f)) *
                Matrix4x4.Rotate(
                    Quaternion.Euler(
                        0f,
                        0f,
                        _regionRollRadians *
                        Mathf.Rad2Deg)) *
                Matrix4x4.Scale(
                    new Vector3(
                        _regionSize,
                        _regionSize,
                        1f)) *
                Matrix4x4.Translate(
                    new Vector3(
                        -0.5f,
                        -0.5f,
                        0f));
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

        private void UpdateRegionFromLandmarks(
            Vector3[] points)
        {
            float minX =
                float.PositiveInfinity;

            float minYBottom =
                float.PositiveInfinity;

            float maxX =
                float.NegativeInfinity;

            float maxYBottom =
                float.NegativeInfinity;

            for (
                int i = 0;
                i < BaseLandmarkCount;
                i++
            )
            {
                Vector3 point =
                    points[i];

                float yBottom =
                    1f - point.y;

                minX =
                    Mathf.Min(
                        minX,
                        point.x);

                maxX =
                    Mathf.Max(
                        maxX,
                        point.x);

                minYBottom =
                    Mathf.Min(
                        minYBottom,
                        yBottom);

                maxYBottom =
                    Mathf.Max(
                        maxYBottom,
                        yBottom);
            }

            Vector2 targetCenter =
                new Vector2(
                    (minX + maxX) * 0.5f,
                    (minYBottom + maxYBottom) * 0.5f);

            float targetSize =
                Mathf.Clamp(
                    Mathf.Max(
                        maxX - minX,
                        maxYBottom - minYBottom) *
                    1.50f,
                    0.08f,
                    1.40f);

            Vector3 eyeA =
                (
                    points[33] +
                    points[133]
                ) *
                0.5f;

            Vector3 eyeB =
                (
                    points[362] +
                    points[263]
                ) *
                0.5f;

            float targetRoll =
                Mathf.Atan2(
                    -(eyeB.y - eyeA.y),
                    eyeB.x - eyeA.x);

            float displacement =
                Vector2.Distance(
                    _regionCenter,
                    targetCenter);

            float response =
                Mathf.Lerp(
                    0.38f,
                    0.94f,
                    Mathf.InverseLerp(
                        0.0015f,
                        0.025f,
                        displacement));

            _regionCenter =
                Vector2.Lerp(
                    _regionCenter,
                    targetCenter,
                    response);

            _regionSize =
                Mathf.Lerp(
                    _regionSize,
                    targetSize,
                    Mathf.Max(
                        0.50f,
                        response));

            _regionRollRadians =
                Mathf.LerpAngle(
                    _regionRollRadians *
                        Mathf.Rad2Deg,
                    targetRoll *
                        Mathf.Rad2Deg,
                    response) *
                Mathf.Deg2Rad;
        }

        private static Quaternion
            CalculateGeometricRotation(
                Vector3[] points)
        {
            Vector3 eyeA =
                ToPoseSpace(
                    (
                        points[33] +
                        points[133]
                    ) *
                    0.5f);

            Vector3 eyeB =
                ToPoseSpace(
                    (
                        points[362] +
                        points[263]
                    ) *
                    0.5f);

            Vector3 forehead =
                ToPoseSpace(
                    points[10]);

            Vector3 chin =
                ToPoseSpace(
                    points[152]);

            Vector3 right =
                eyeB - eyeA;

            Vector3 upHint =
                forehead - chin;

            if (
                right.sqrMagnitude <
                    0.0000001f ||
                upHint.sqrMagnitude <
                    0.0000001f
            )
            {
                return Quaternion.identity;
            }

            right.Normalize();
            upHint.Normalize();

            Vector3 forward =
                Vector3.Cross(
                    right,
                    upHint);

            if (
                forward.sqrMagnitude <
                    0.0000001f
            )
            {
                return Quaternion.identity;
            }

            forward.Normalize();

            Vector3 up =
                Vector3.Cross(
                    forward,
                    right)
                .normalized;

            Quaternion rotation =
                Quaternion.LookRotation(
                    forward,
                    up);

            return
                IsFinite(rotation)
                    ? rotation
                    : Quaternion.identity;
        }

        private static Vector3 ToPoseSpace(
            Vector3 point)
        {
            return
                new Vector3(
                    point.x,
                    -point.y,
                    -point.z);
        }

        private static void
            SynthesizeIrisLandmarks(
                Vector3[] points)
        {
            Vector3 irisA =
                (
                    points[33] +
                    points[133] +
                    points[159] +
                    points[145]
                ) *
                0.25f;

            points[468] = irisA;
            points[469] = points[33];
            points[470] = points[159];
            points[471] = points[133];
            points[472] = points[145];

            Vector3 irisB =
                (
                    points[362] +
                    points[263] +
                    points[386] +
                    points[374]
                ) *
                0.25f;

            points[473] = irisB;
            points[474] = points[362];
            points[475] = points[386];
            points[476] = points[263];
            points[477] = points[374];
        }

        private void RegisterFailure()
        {
            _consecutiveFailures++;

            if (
                _consecutiveFailures >= 4
            )
            {
                _hasRegion = false;
            }
        }

        private void RecordLatency(
            long started)
        {
            long finished =
                System.Diagnostics.Stopwatch
                    .GetTimestamp();

            RecordLatency(
                started,
                finished);
        }

        private void RecordLatency(
            long started,
            long finished)
        {
            if (
                started <= 0L ||
                finished <= started
            )
            {
                return;
            }

            float milliseconds =
                (float)(
                    (finished - started) *
                    1000.0 /
                    System.Diagnostics.Stopwatch
                        .Frequency);

            LatestLatencyMs =
                LatestLatencyMs > 0f
                    ? Mathf.Lerp(
                        LatestLatencyMs,
                        milliseconds,
                        0.20f)
                    : milliseconds;
        }

        private static float NormalizePresence(
            float value)
        {
            if (
                value >= 0f &&
                value <= 1f
            )
            {
                return value;
            }

            return
                1f /
                (
                    1f +
                    Mathf.Exp(
                        -Mathf.Clamp(
                            value,
                            -30f,
                            30f))
                );
        }

        private static bool IsFinite(
            float value)
        {
            return
                !float.IsNaN(value) &&
                !float.IsInfinity(value);
        }

        private static bool IsFinite(
            Vector3 value)
        {
            return
                IsFinite(value.x) &&
                IsFinite(value.y) &&
                IsFinite(value.z);
        }

        private static bool IsFinite(
            Quaternion value)
        {
            return
                IsFinite(value.x) &&
                IsFinite(value.y) &&
                IsFinite(value.z) &&
                IsFinite(value.w);
        }
    }
}
