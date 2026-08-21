using System;
using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    /// <summary>
    /// KiwiAvatarSystem v3.6 MediaPipe-input-parity pipelined GPU face-landmark tracker.
    ///
    /// Design goals:
    /// - keep the exact public API used by FaceLandmarkerRunner;
    /// - overlap GPU inference/readback using independent workers;
    /// - never queue an unbounded history of camera frames;
    /// - publish only the newest valid completed source frame;
    /// - keep the exact crop matrix, source timestamp and anchor revision that
    ///   belonged to each scheduled frame;
    /// - discard stale-anchor completions rather than letting an old ROI snap
    ///   the avatar after reacquisition.
    ///
    /// v5.0 keeps three preallocated desktop lanes and adapts the scheduling
    /// budget between one, two, and three lanes. Sustained severe GPU latency
    /// reduces the queue to one lane, two lanes are the normal operating point,
    /// and the third lane is enabled only after a sustained low-latency streak.
    /// This favors low end-to-end latency over backlog depth without reallocating
    /// runtime buffers.
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

        private static readonly int InputIsSrgbId =
            Shader.PropertyToID(
                "_InputIsSRGB");

        private const string PresenceSigmoidMarker =
            "KIWI_FACE_FLAG_SIGMOID_V3_2";

        private enum DecodeStatus
        {
            None = 0,
            Valid = 1,
            InvalidOutput = 2,
            PresenceLow = 3,
            NonFiniteLandmark = 4,
            StaleAnchor = 5,
            StaleGeneration = 6,
            StaleSource = 7,
            Exception = 8
        }

        private sealed class Lane : IDisposable
        {
            public readonly Worker worker;
            public readonly Tensor<float> input;
            public readonly RenderTexture cropTexture;
            public readonly Material cropMaterial;
            public readonly TextureTransform textureTransform;

            public readonly Vector3[] decodedLandmarks =
                new Vector3[CompatibleLandmarkCount];

            public Tensor<float> pendingOutput;
            public bool readbackPending;

            public Matrix4x4 pendingCropMatrix =
                Matrix4x4.identity;

            public long pendingSourceHostTicks;
            public long pendingStartedHostTicks;
            public int pendingAnchorRevision;
            public int pendingTrackerGeneration;

            public Lane(
                Model model,
                Shader cropShader,
                int index)
            {
                worker =
                    new Worker(
                        model,
                        BackendType.GPUCompute);

                input =
                    new Tensor<float>(
                        new TensorShape(
                            1,
                            3,
                            InputSize,
                            InputSize));

                cropTexture =
                    new RenderTexture(
                        InputSize,
                        InputSize,
                        0,
                        RenderTextureFormat.ARGB32,
                        RenderTextureReadWrite.Linear)
                    {
                        name =
                            "Kiwi Inference Face Crop Lane " +
                            index,
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

                cropTexture.Create();

                cropMaterial =
                    new Material(
                        cropShader)
                    {
                        name =
                            "Kiwi Inference Crop Material Lane " +
                            index,
                        hideFlags =
                            HideFlags.DontSave
                    };

                textureTransform =
                    new TextureTransform()
                        .SetTensorLayout(
                            TensorLayout.NCHW)
                        .SetCoordOrigin(
                            CoordOrigin.TopLeft);
            }

            public void Dispose()
            {
                readbackPending =
                    false;

                pendingOutput =
                    null;

                worker?.Dispose();
                input?.Dispose();

                if (cropTexture != null)
                {
                    if (cropTexture.IsCreated())
                    {
                        cropTexture.Release();
                    }

                    UnityEngine.Object.Destroy(
                        cropTexture);
                }

                if (cropMaterial != null)
                {
                    UnityEngine.Object.Destroy(
                        cropMaterial);
                }
            }
        }

        private struct Completion
        {
            public bool exists;
            public bool valid;
            public int laneIndex;
            public DecodeStatus status;
            public long sourceHostTicks;
            public long arrivalHostTicks;
            public long startedHostTicks;
            public int anchorRevision;
            public int trackerGeneration;
            public float rawPresence;
            public float presence;
            public Quaternion rotation;
        }

        private readonly Lane[] _lanes;

        // KIWI_V5_0_ADAPTIVE_LANE_BUDGET
        // All workers are allocated once. Runtime only changes how many lanes
        // may receive new work, so no GC/reallocation is introduced.
        private int _schedulingLaneLimit = 1;
        private int _lowLatencyCompletionStreak;
        private int _highLatencyCompletionStreak;
        private int _severeLatencyCompletionStreak;
        private int _recoveryLatencyCompletionStreak;

        private const float EnableThirdLaneBelowMs = 48f;
        private const float DisableThirdLaneAboveMs = 62f;
        private const float ReduceToSingleLaneAboveMs = 92f;
        private const float RecoverSecondLaneBelowMs = 68f;
        private const int EnableThirdLaneStreak = 24;
        private const int DisableThirdLaneStreak = 4;
        private const int ReduceToSingleLaneStreak = 6;
        private const int RecoverSecondLaneStreak = 14;

        private readonly Vector3[] _landmarks =
            new Vector3[CompatibleLandmarkCount];

        private Vector2 _regionCenter;
        private float _regionWidth;
        private float _regionHeight;
        private float _regionRollRadians;
        private bool _hasRegion;

        // The MediaPipe ROI is square in PIXELS, not in normalized UV space.
        // Source dimensions are therefore part of the transform.
        private int _sourceWidth = 1;
        private int _sourceHeight = 1;
        private bool _inputGammaPreservationActive;

        private int _anchorRevision;
        private int _trackerGeneration;
        private int _consecutiveFailures;
        private int _nextLaneIndex;

        private long _latestCompletedSourceHostTicks;
        private long _latestCompletedArrivalHostTicks;

        private int _scheduledFrameCount;
        private int _readbackCompletedFrameCount;
        private int _completedFrameCount;
        private int _droppedFreshFrameCount;
        private int _rejectedPresenceFrameCount;
        private int _rejectedInvalidFrameCount;
        private int _discardedStaleFrameCount;

        public float MinimumPresence { get; set; } =
            0.5f;

        public bool HasRegion =>
            _hasRegion;

        public bool IsTracking =>
            _hasRegion &&
            _consecutiveFailures < 4;

        public bool IsAsyncReadbackPending =>
            ActiveLaneCount > 0;

        public float RegionWidthNormalized =>
            _regionWidth;

        public float RegionHeightNormalized =>
            _regionHeight;

        public bool InputGammaPreservationActive =>
            _inputGammaPreservationActive;

        public float RegionPixelAspectError
        {
            get
            {
                float pixelWidth =
                    _regionWidth *
                    Mathf.Max(
                        1,
                        _sourceWidth);

                float pixelHeight =
                    _regionHeight *
                    Mathf.Max(
                        1,
                        _sourceHeight);

                if (
                    pixelWidth <= 0.000001f ||
                    pixelHeight <= 0.000001f
                )
                {
                    return 0f;
                }

                return
                    Mathf.Abs(
                        pixelWidth /
                        pixelHeight -
                        1f);
            }
        }

        public int PipelineDepth =>
            _lanes != null
                ? _lanes.Length
                : 0;

        public int SchedulingLaneLimit =>
            _schedulingLaneLimit;

        public int ActiveLaneCount
        {
            get
            {
                if (_lanes == null)
                {
                    return 0;
                }

                int count = 0;

                for (
                    int i = 0;
                    i < _lanes.Length;
                    i++
                )
                {
                    if (
                        _lanes[i] != null &&
                        _lanes[i].readbackPending
                    )
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public float OldestPendingAgeMs
        {
            get
            {
                if (_lanes == null)
                {
                    return 0f;
                }

                long now =
                    System.Diagnostics.Stopwatch
                        .GetTimestamp();

                long oldestStarted =
                    0L;

                for (
                    int i = 0;
                    i < _lanes.Length;
                    i++
                )
                {
                    Lane lane =
                        _lanes[i];

                    if (
                        lane == null ||
                        !lane.readbackPending ||
                        lane.pendingStartedHostTicks <= 0L
                    )
                    {
                        continue;
                    }

                    if (
                        oldestStarted <= 0L ||
                        lane.pendingStartedHostTicks <
                            oldestStarted
                    )
                    {
                        oldestStarted =
                            lane.pendingStartedHostTicks;
                    }
                }

                if (
                    oldestStarted <= 0L ||
                    now <= oldestStarted
                )
                {
                    return 0f;
                }

                return
                    (float)(
                        (now - oldestStarted) *
                        1000.0 /
                        System.Diagnostics.Stopwatch
                            .Frequency);
            }
        }

        public float LatestPresence { get; private set; }

        public float LatestRawPresenceLogit { get; private set; }

        public string LatestRejectionReason { get; private set; } =
            "-";

        public float LatestLatencyMs { get; private set; }

        public long LatestCompletedSourceHostTicks =>
            _latestCompletedSourceHostTicks;

        public long LatestCompletedArrivalHostTicks =>
            _latestCompletedArrivalHostTicks;

        public int ScheduledFrameCount =>
            _scheduledFrameCount;

        /// <summary>
        /// Number of GPU readbacks that reached CPU, whether accepted or rejected.
        /// </summary>
        public int ReadbackCompletedFrameCount =>
            _readbackCompletedFrameCount;

        /// <summary>
        /// Kept for compatibility: number of valid inference frames accepted by
        /// this tracker before Runner-level geometry adoption.
        /// </summary>
        public int CompletedFrameCount =>
            _completedFrameCount;

        public int DroppedFreshFrameCount =>
            _droppedFreshFrameCount;

        public int RejectedPresenceFrameCount =>
            _rejectedPresenceFrameCount;

        public int RejectedInvalidFrameCount =>
            _rejectedInvalidFrameCount;

        public int DiscardedStaleFrameCount =>
            _discardedStaleFrameCount;

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

            int requestedDepth =
                Application.isMobilePlatform
                    ? 2
                    : 3;

            List<Lane> lanes =
                new List<Lane>(
                    requestedDepth);

            for (
                int i = 0;
                i < requestedDepth;
                i++
            )
            {
                try
                {
                    // Give each lane its own model/worker state. This avoids
                    // reusing a worker output tensor while its async readback is
                    // still in flight.
                    Model model =
                        BuildSingleReadbackModel(
                            ModelLoader.Load(
                                modelAsset));

                    lanes.Add(
                        new Lane(
                            model,
                            cropShader,
                            i));
                }
                catch
                {
                    if (lanes.Count == 0)
                    {
                        throw;
                    }

                    // A secondary lane is an optimization, not a requirement.
                    // If resource allocation fails, continue with the lanes that
                    // were created successfully.
                    break;
                }
            }

            _lanes =
                lanes.ToArray();

            _schedulingLaneLimit =
                Mathf.Clamp(
                    Application.isMobilePlatform
                        ? 2
                        : 2,
                    1,
                    Mathf.Max(
                        1,
                        _lanes.Length));
        }

        public void Dispose()
        {
            if (_lanes == null)
            {
                return;
            }

            for (
                int i = 0;
                i < _lanes.Length;
                i++
            )
            {
                _lanes[i]?.Dispose();
            }
        }

        public void Reset()
        {
            _trackerGeneration++;

            _hasRegion =
                false;

            _regionWidth =
                0f;

            _regionHeight =
                0f;

            _inputGammaPreservationActive =
                false;

            _consecutiveFailures =
                0;

            _lowLatencyCompletionStreak =
                0;

            _highLatencyCompletionStreak =
                0;

            _severeLatencyCompletionStreak =
                0;

            _recoveryLatencyCompletionStreak =
                0;

            _schedulingLaneLimit =
                Mathf.Clamp(
                    Application.isMobilePlatform
                        ? 2
                        : 2,
                    1,
                    Mathf.Max(
                        1,
                        _lanes != null
                            ? _lanes.Length
                            : 1));

            LatestPresence =
                0f;

            LatestRawPresenceLogit =
                0f;

            LatestRejectionReason =
                "-";

            LatestLatencyMs =
                0f;

            _latestCompletedSourceHostTicks =
                0L;

            _latestCompletedArrivalHostTicks =
                0L;

            // Existing GPU requests cannot be cancelled. Keep each occupied
            // lane pending and discard it later by trackerGeneration.
        }

        public void ApplyExternalAnchor(
            UnityEngine.Rect regionTopLeft,
            float rollRadiansBottomLeft,
            bool force)
        {
            float width =
                Mathf.Clamp(
                    Mathf.Abs(
                        regionTopLeft.width),
                    0.04f,
                    2.50f);

            float height =
                Mathf.Clamp(
                    Mathf.Abs(
                        regionTopLeft.height),
                    0.04f,
                    2.50f);

            Vector2 centerTopLeft =
                regionTopLeft.center;

            Vector2 centerBottomLeft =
                new Vector2(
                    centerTopLeft.x,
                    1f -
                    centerTopLeft.y);

            if (
                !_hasRegion ||
                force
            )
            {
                AdoptExternalAnchor(
                    centerBottomLeft,
                    width,
                    height,
                    rollRadiansBottomLeft);

                return;
            }

            float imageWidth =
                Mathf.Max(
                    1f,
                    _sourceWidth);

            float imageHeight =
                Mathf.Max(
                    1f,
                    _sourceHeight);

            float centerDxPixels =
                (
                    centerBottomLeft.x -
                    _regionCenter.x
                ) *
                imageWidth;

            float centerDyPixels =
                (
                    centerBottomLeft.y -
                    _regionCenter.y
                ) *
                imageHeight;

            float centerDistancePixels =
                Mathf.Sqrt(
                    centerDxPixels *
                        centerDxPixels +
                    centerDyPixels *
                        centerDyPixels);

            float regionSidePixels =
                Mathf.Max(
                    _regionWidth *
                        imageWidth,
                    _regionHeight *
                        imageHeight);

            float widthRatioDelta =
                Mathf.Abs(
                    width -
                    _regionWidth) /
                Mathf.Max(
                    0.001f,
                    _regionWidth);

            float heightRatioDelta =
                Mathf.Abs(
                    height -
                    _regionHeight) /
                Mathf.Max(
                    0.001f,
                    _regionHeight);

            float rollDelta =
                Mathf.Abs(
                    Mathf.DeltaAngle(
                        _regionRollRadians *
                            Mathf.Rad2Deg,
                        rollRadiansBottomLeft *
                            Mathf.Rad2Deg));

            // MediaPipe correction is asynchronous. Keep the current fresh
            // inference ROI unless the auxiliary result indicates material
            // translation, size or roll drift. Translation is compared in
            // pixel space so a 16:9 source cannot bias vertical corrections.
            if (
                centerDistancePixels >
                    Mathf.Max(
                        12f,
                        regionSidePixels *
                        0.20f) ||
                widthRatioDelta >
                    0.22f ||
                heightRatioDelta >
                    0.22f ||
                rollDelta >
                    18f
            )
            {
                AdoptExternalAnchor(
                    centerBottomLeft,
                    width,
                    height,
                    rollRadiansBottomLeft);
            }
        }

        private void AdoptExternalAnchor(
            Vector2 centerBottomLeft,
            float width,
            float height,
            float rollRadiansBottomLeft)
        {
            _regionCenter =
                centerBottomLeft;

            _regionWidth =
                Mathf.Clamp(
                    width,
                    0.04f,
                    2.50f);

            _regionHeight =
                Mathf.Clamp(
                    height,
                    0.04f,
                    2.50f);

            _regionRollRadians =
                rollRadiansBottomLeft;

            _hasRegion =
                true;

            _consecutiveFailures =
                0;

            _anchorRevision++;
        }

        /// <summary>
        /// Compatibility synchronous path.
        /// </summary>
        public bool TryProcess(
            Texture source,
            bool flipHorizontally,
            bool flipVertically,
            out Vector3[] landmarks,
            out Quaternion geometricRotation)
        {
            landmarks =
                null;

            geometricRotation =
                Quaternion.identity;

            if (
                !_hasRegion ||
                source == null ||
                _lanes == null ||
                _lanes.Length == 0 ||
                _lanes[0].readbackPending
            )
            {
                return false;
            }

            Lane lane =
                _lanes[0];

            long started =
                System.Diagnostics.Stopwatch
                    .GetTimestamp();

            UpdateSourceDimensions(
                source);

            Matrix4x4 cropMatrix =
                BuildCropMatrix();

            try
            {
                ScheduleModel(
                    lane,
                    source,
                    flipHorizontally,
                    flipVertically,
                    cropMatrix);

                Tensor<float> packedOutput =
                    lane.worker.PeekOutput(0)
                    as Tensor<float>;

                if (
                    packedOutput == null ||
                    packedOutput.shape.length !=
                        PackedOutputLength
                )
                {
                    RegisterFailure();
                    LatestRejectionReason =
                        DecodeStatus.InvalidOutput.ToString();
                    return false;
                }

                using Tensor<float> readableOutput =
                    packedOutput.ReadbackAndClone();

                DecodeStatus status =
                    DecodeReadableOutput(
                        readableOutput,
                        cropMatrix,
                        lane.decodedLandmarks,
                        out float rawPresence,
                        out float presence,
                        out geometricRotation);

                LatestRawPresenceLogit =
                    rawPresence;

                LatestPresence =
                    presence;

                RecordLatency(
                    started,
                    System.Diagnostics.Stopwatch
                        .GetTimestamp());

                if (status != DecodeStatus.Valid)
                {
                    RegisterDecodeFailure(
                        status);

                    return false;
                }

                Array.Copy(
                    lane.decodedLandmarks,
                    _landmarks,
                    CompatibleLandmarkCount);

                UpdateRegionFromLandmarks(
                    _landmarks);

                _consecutiveFailures =
                    0;

                _completedFrameCount++;

                LatestRejectionReason =
                    "-";

                landmarks =
                    _landmarks;

                return true;
            }
            catch
            {
                RegisterDecodeFailure(
                    DecodeStatus.Exception);

                return false;
            }
        }

        /// <summary>
        /// Multi-lane non-blocking live path.
        ///
        /// Each camera generation is scheduled into one free lane. While GPU
        /// work/readback is in flight, other independent lanes may accept newer
        /// camera frames. On completion, the newest valid source frame wins.
        /// Older completed results are consumed but not published.
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

            Completion newestCompletion =
                default;

            Completion newestValidCompletion =
                default;

            bool anyNonStaleFailure =
                false;

            PollCompletedLanes(
                ref newestCompletion,
                ref newestValidCompletion,
                ref anyNonStaleFailure);

            bool hasValidResult =
                newestValidCompletion.exists &&
                newestValidCompletion.valid;

            if (hasValidResult)
            {
                Lane winner =
                    _lanes[
                        newestValidCompletion.laneIndex];

                Array.Copy(
                    winner.decodedLandmarks,
                    _landmarks,
                    CompatibleLandmarkCount);

                geometricRotation =
                    newestValidCompletion.rotation;

                completedSourceHostTicks =
                    newestValidCompletion.sourceHostTicks;

                _latestCompletedSourceHostTicks =
                    newestValidCompletion.sourceHostTicks;

                _latestCompletedArrivalHostTicks =
                    newestValidCompletion.arrivalHostTicks;

                // Only the newest accepted result from the current anchor may
                // advance the ROI. Old in-flight crops are never allowed to
                // pull the current ROI backwards.
                UpdateRegionFromLandmarks(
                    _landmarks);

                _consecutiveFailures =
                    0;

                _completedFrameCount++;

                LatestRejectionReason =
                    "-";

                landmarks =
                    _landmarks;
            }
            else if (anyNonStaleFailure)
            {
                RegisterFailure();
            }

            if (newestCompletion.exists)
            {
                LatestRawPresenceLogit =
                    newestCompletion.rawPresence;

                LatestPresence =
                    newestCompletion.presence;

                if (
                    !hasValidResult &&
                    newestCompletion.status !=
                        DecodeStatus.StaleAnchor &&
                    newestCompletion.status !=
                        DecodeStatus.StaleGeneration &&
                    newestCompletion.status !=
                        DecodeStatus.StaleSource
                )
                {
                    LatestRejectionReason =
                        newestCompletion.status.ToString();
                }
            }

            if (
                scheduleLatestSource &&
                _hasRegion &&
                source != null
            )
            {
                scheduledLatestSource =
                    TryScheduleNewestSource(
                        source,
                        flipHorizontally,
                        flipVertically,
                        latestSourceHostTicks);

                if (!scheduledLatestSource)
                {
                    _droppedFreshFrameCount++;
                }
            }

            return
                hasValidResult;
        }

        private void PollCompletedLanes(
            ref Completion newestCompletion,
            ref Completion newestValidCompletion,
            ref bool anyNonStaleFailure)
        {
            if (_lanes == null)
            {
                return;
            }

            for (
                int i = 0;
                i < _lanes.Length;
                i++
            )
            {
                Lane lane =
                    _lanes[i];

                if (
                    lane == null ||
                    !lane.readbackPending ||
                    lane.pendingOutput == null ||
                    !lane.pendingOutput.IsReadbackRequestDone()
                )
                {
                    continue;
                }

                long arrivalHostTicks =
                    System.Diagnostics.Stopwatch
                        .GetTimestamp();

                Tensor<float> completedOutput =
                    lane.pendingOutput;

                Matrix4x4 completedCropMatrix =
                    lane.pendingCropMatrix;

                long completedSourceTicks =
                    lane.pendingSourceHostTicks;

                long startedTicks =
                    lane.pendingStartedHostTicks;

                int completedAnchorRevision =
                    lane.pendingAnchorRevision;

                int completedGeneration =
                    lane.pendingTrackerGeneration;

                lane.readbackPending =
                    false;

                lane.pendingOutput =
                    null;

                lane.pendingSourceHostTicks =
                    0L;

                lane.pendingStartedHostTicks =
                    0L;

                lane.pendingAnchorRevision =
                    0;

                lane.pendingTrackerGeneration =
                    0;

                _readbackCompletedFrameCount++;

                Completion completion =
                    new Completion
                    {
                        exists =
                            true,
                        laneIndex =
                            i,
                        sourceHostTicks =
                            completedSourceTicks,
                        arrivalHostTicks =
                            arrivalHostTicks,
                        startedHostTicks =
                            startedTicks,
                        anchorRevision =
                            completedAnchorRevision,
                        trackerGeneration =
                            completedGeneration
                    };

                try
                {
                    using Tensor<float> readableOutput =
                        completedOutput
                            .ReadbackAndClone();

                    DecodeStatus status =
                        DecodeReadableOutput(
                            readableOutput,
                            completedCropMatrix,
                            lane.decodedLandmarks,
                            out float rawPresence,
                            out float presence,
                            out Quaternion rotation);

                    completion.rawPresence =
                        rawPresence;

                    completion.presence =
                        presence;

                    completion.rotation =
                        rotation;

                    if (
                        completedGeneration !=
                            _trackerGeneration
                    )
                    {
                        status =
                            DecodeStatus.StaleGeneration;

                        _discardedStaleFrameCount++;
                    }
                    else if (
                        completedAnchorRevision !=
                            _anchorRevision
                    )
                    {
                        status =
                            DecodeStatus.StaleAnchor;

                        _discardedStaleFrameCount++;
                    }
                    else if (
                        status ==
                            DecodeStatus.Valid &&
                        completedSourceTicks >
                            0L &&
                        _latestCompletedSourceHostTicks >
                            0L &&
                        completedSourceTicks <=
                            _latestCompletedSourceHostTicks
                    )
                    {
                        // Multi-lane GPU jobs may complete out of order.
                        // A result older than the latest already-published
                        // source frame is consumed but never allowed to move
                        // the avatar backwards in time.
                        status =
                            DecodeStatus.StaleSource;

                        _discardedStaleFrameCount++;
                    }

                    completion.status =
                        status;

                    completion.valid =
                        status ==
                        DecodeStatus.Valid;

                    if (
                        status ==
                        DecodeStatus.PresenceLow
                    )
                    {
                        _rejectedPresenceFrameCount++;
                    }
                    else if (
                        status ==
                            DecodeStatus.InvalidOutput ||
                        status ==
                            DecodeStatus.NonFiniteLandmark
                    )
                    {
                        _rejectedInvalidFrameCount++;
                    }

                    if (
                        status !=
                            DecodeStatus.Valid &&
                        status !=
                            DecodeStatus.StaleAnchor &&
                        status !=
                            DecodeStatus.StaleGeneration &&
                        status !=
                            DecodeStatus.StaleSource
                    )
                    {
                        anyNonStaleFailure =
                            true;
                    }
                }
                catch
                {
                    completion.status =
                        DecodeStatus.Exception;

                    completion.valid =
                        false;

                    _rejectedInvalidFrameCount++;

                    anyNonStaleFailure =
                        true;
                }

                RecordLatency(
                    startedTicks,
                    arrivalHostTicks);

                if (
                    !newestCompletion.exists ||
                    IsCompletionNewer(
                        completion,
                        newestCompletion)
                )
                {
                    newestCompletion =
                        completion;
                }

                if (
                    completion.valid &&
                    (
                        !newestValidCompletion.exists ||
                        IsCompletionNewer(
                            completion,
                            newestValidCompletion)
                    )
                )
                {
                    newestValidCompletion =
                        completion;
                }
            }
        }

        private static bool IsCompletionNewer(
            Completion candidate,
            Completion current)
        {
            if (!current.exists)
            {
                return true;
            }

            if (
                candidate.sourceHostTicks >
                current.sourceHostTicks
            )
            {
                return true;
            }

            if (
                candidate.sourceHostTicks ==
                    current.sourceHostTicks &&
                candidate.arrivalHostTicks >
                    current.arrivalHostTicks
            )
            {
                return true;
            }

            return false;
        }

        private bool TryScheduleNewestSource(
            Texture source,
            bool flipHorizontally,
            bool flipVertically,
            long sourceHostTicks)
        {
            int laneIndex =
                FindFreeLane();

            if (laneIndex < 0)
            {
                return false;
            }

            Lane lane =
                _lanes[laneIndex];

            try
            {
                UpdateSourceDimensions(
                    source);

                Matrix4x4 cropMatrix =
                    BuildCropMatrix();

                ScheduleModel(
                    lane,
                    source,
                    flipHorizontally,
                    flipVertically,
                    cropMatrix);

                Tensor<float> packedOutput =
                    lane.worker.PeekOutput(0)
                    as Tensor<float>;

                if (
                    packedOutput == null ||
                    packedOutput.shape.length !=
                        PackedOutputLength
                )
                {
                    _rejectedInvalidFrameCount++;
                    RegisterFailure();
                    return false;
                }

                lane.pendingCropMatrix =
                    cropMatrix;

                lane.pendingSourceHostTicks =
                    sourceHostTicks > 0L
                        ? sourceHostTicks
                        : System.Diagnostics.Stopwatch
                            .GetTimestamp();

                lane.pendingStartedHostTicks =
                    System.Diagnostics.Stopwatch
                        .GetTimestamp();

                lane.pendingAnchorRevision =
                    _anchorRevision;

                lane.pendingTrackerGeneration =
                    _trackerGeneration;

                lane.pendingOutput =
                    packedOutput;

                lane.pendingOutput
                    .ReadbackRequest();

                lane.readbackPending =
                    true;

                _scheduledFrameCount++;

                _nextLaneIndex =
                    (
                        laneIndex +
                        1
                    ) %
                    Mathf.Max(
                        1,
                        Mathf.Min(
                            _schedulingLaneLimit,
                            _lanes.Length));

                return true;
            }
            catch
            {
                lane.pendingOutput =
                    null;

                lane.readbackPending =
                    false;

                lane.pendingSourceHostTicks =
                    0L;

                lane.pendingStartedHostTicks =
                    0L;

                lane.pendingAnchorRevision =
                    0;

                lane.pendingTrackerGeneration =
                    0;

                _rejectedInvalidFrameCount++;

                RegisterFailure();

                return false;
            }
        }

        private int FindFreeLane()
        {
            if (
                _lanes == null ||
                _lanes.Length == 0
            )
            {
                return -1;
            }

            int laneLimit =
                Mathf.Clamp(
                    _schedulingLaneLimit,
                    1,
                    _lanes.Length);

            for (
                int offset = 0;
                offset < laneLimit;
                offset++
            )
            {
                int index =
                    (
                        _nextLaneIndex +
                        offset
                    ) %
                    laneLimit;

                Lane lane =
                    _lanes[index];

                if (
                    lane != null &&
                    !lane.readbackPending
                )
                {
                    return index;
                }
            }

            return -1;
        }

        private void ScheduleModel(
            Lane lane,
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

            lane.cropMaterial.SetMatrix(
                "_Xform",
                samplingMatrix);

            bool preserveStoredSrgb =
                QualitySettings.activeColorSpace ==
                    ColorSpace.Linear &&
                source.isDataSRGB;

            _inputGammaPreservationActive =
                preserveStoredSrgb;

            lane.cropMaterial.SetFloat(
                InputIsSrgbId,
                preserveStoredSrgb
                    ? 1f
                    : 0f);

            Graphics.Blit(
                source,
                lane.cropTexture,
                lane.cropMaterial,
                0);

            TextureConverter.ToTensor(
                lane.cropTexture,
                lane.input,
                lane.textureTransform);

            lane.worker.Schedule(
                lane.input);
        }

        private DecodeStatus DecodeReadableOutput(
            Tensor<float> readableOutput,
            Matrix4x4 cropMatrix,
            Vector3[] destination,
            out float rawPresence,
            out float presence,
            out Quaternion geometricRotation)
        {
            rawPresence =
                0f;

            presence =
                0f;

            geometricRotation =
                Quaternion.identity;

            if (
                readableOutput == null ||
                destination == null ||
                destination.Length <
                    CompatibleLandmarkCount ||
                readableOutput.shape.length !=
                    PackedOutputLength
            )
            {
                return
                    DecodeStatus.InvalidOutput;
            }

            rawPresence =
                readableOutput[
                    BaseLandmarkCount * 3];

            // KIWI_FACE_FLAG_SIGMOID_V3_2
            // The face flag is a raw logit. Apply sigmoid unconditionally even
            // when the raw numeric value happens to lie inside [0, 1].
            presence =
                NormalizePresence(
                    rawPresence);

            if (
                !IsFinite(presence) ||
                presence <
                    Mathf.Clamp01(
                        MinimumPresence)
            )
            {
                return
                    DecodeStatus.PresenceLow;
            }

            float regionZScale =
                ExtractRegionZScale(
                    cropMatrix);

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
                        1f -
                        sourceBottom.y,
                        cropZ *
                        regionZScale);

                if (!IsFinite(point))
                {
                    return
                        DecodeStatus.NonFiniteLandmark;
                }

                destination[i] =
                    point;
            }

            SynthesizeIrisLandmarks(
                destination);

            geometricRotation =
                CalculateGeometricRotation(
                    destination);

            if (!IsFinite(geometricRotation))
            {
                return
                    DecodeStatus.InvalidOutput;
            }

            return
                DecodeStatus.Valid;
        }

        /// <summary>
        /// Packs landmarks and face-flag logit into one GPU output so each lane
        /// performs only one GPU-to-CPU readback.
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
                graph.AddInputs(
                    source);

            FunctionalTensor[] outputs =
                Functional.Forward(
                    source,
                    inputs);

            FunctionalTensor landmarks =
                outputs[
                    landmarkIndex]
                    .Reshape(
                        new[]
                        {
                            BaseLandmarkCount *
                            3
                        });

            FunctionalTensor presence =
                outputs[
                    presenceIndex]
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

        /// <summary>
        /// MediaPipe LandmarkProjectionCalculator scales Z by the projected
        /// crop X-axis length. Do the same instead of using max(X,Y).
        /// </summary>
        private static float ExtractRegionZScale(
            Matrix4x4 cropMatrix)
        {
            Vector3 xAxis =
                cropMatrix.MultiplyVector(
                    Vector3.right);

            float scale =
                xAxis.magnitude;

            return
                Mathf.Clamp(
                    scale,
                    0.02f,
                    3.0f);
        }

        private void UpdateSourceDimensions(
            Texture source)
        {
            if (source == null)
            {
                return;
            }

            _sourceWidth =
                Mathf.Max(
                    1,
                    source.width);

            _sourceHeight =
                Mathf.Max(
                    1,
                    source.height);
        }

        /// <summary>
        /// Crop-local UV -> original source UV.
        ///
        /// Rotation is performed in PIXEL space. A rotation performed directly
        /// in normalized UV space is geometrically wrong on a 16:9 camera and
        /// turns a pixel-square MediaPipe ROI into a skewed crop.
        /// </summary>
        private Matrix4x4 BuildCropMatrix()
        {
            float imageWidth =
                Mathf.Max(
                    1f,
                    _sourceWidth);

            float imageHeight =
                Mathf.Max(
                    1f,
                    _sourceHeight);

            Vector2 centerPixels =
                new Vector2(
                    _regionCenter.x *
                        imageWidth,
                    _regionCenter.y *
                        imageHeight);

            Vector2 sizePixels =
                new Vector2(
                    _regionWidth *
                        imageWidth,
                    _regionHeight *
                        imageHeight);

            return
                Matrix4x4.Scale(
                    new Vector3(
                        1f /
                            imageWidth,
                        1f /
                            imageHeight,
                        1f)) *
                Matrix4x4.Translate(
                    new Vector3(
                        centerPixels.x,
                        centerPixels.y,
                        0f)) *
                Matrix4x4.Rotate(
                    Quaternion.Euler(
                        0f,
                        0f,
                        _regionRollRadians *
                            Mathf.Rad2Deg)) *
                Matrix4x4.Scale(
                    new Vector3(
                        sizePixels.x,
                        sizePixels.y,
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

        /// <summary>
        /// Recreates MediaPipe's landmark -> ROI policy:
        /// tight full-landmark bounds -> eye-line rotation -> 1.5x
        /// square_long in PIXELS.
        /// </summary>
        private void UpdateRegionFromLandmarks(
            Vector3[] points)
        {
            if (
                points == null ||
                points.Length <
                    BaseLandmarkCount
            )
            {
                return;
            }

            float imageWidth =
                Mathf.Max(
                    1f,
                    _sourceWidth);

            float imageHeight =
                Mathf.Max(
                    1f,
                    _sourceHeight);

            float minXPixels =
                float.PositiveInfinity;

            float minYPixelsBottom =
                float.PositiveInfinity;

            float maxXPixels =
                float.NegativeInfinity;

            float maxYPixelsBottom =
                float.NegativeInfinity;

            for (
                int i = 0;
                i < BaseLandmarkCount;
                i++
            )
            {
                Vector3 point =
                    points[i];

                if (!IsFinite(point))
                {
                    return;
                }

                float xPixels =
                    point.x *
                    imageWidth;

                float yPixelsBottom =
                    (
                        1f -
                        point.y
                    ) *
                    imageHeight;

                minXPixels =
                    Mathf.Min(
                        minXPixels,
                        xPixels);

                maxXPixels =
                    Mathf.Max(
                        maxXPixels,
                        xPixels);

                minYPixelsBottom =
                    Mathf.Min(
                        minYPixelsBottom,
                        yPixelsBottom);

                maxYPixelsBottom =
                    Mathf.Max(
                        maxYPixelsBottom,
                        yPixelsBottom);
            }

            float boxWidthPixels =
                maxXPixels -
                minXPixels;

            float boxHeightPixels =
                maxYPixelsBottom -
                minYPixelsBottom;

            if (
                boxWidthPixels <=
                    1f ||
                boxHeightPixels <=
                    1f
            )
            {
                return;
            }

            Vector2 targetCenter =
                new Vector2(
                    (
                        minXPixels +
                        maxXPixels
                    ) *
                    0.5f /
                    imageWidth,
                    (
                        minYPixelsBottom +
                        maxYPixelsBottom
                    ) *
                    0.5f /
                    imageHeight);

            // MediaPipe RectTransformationCalculator:
            // scale_x=1.5, scale_y=1.5, square_long=true.
            float squareSidePixels =
                Mathf.Max(
                    boxWidthPixels,
                    boxHeightPixels) *
                1.50f;

            float targetWidth =
                Mathf.Clamp(
                    squareSidePixels /
                    imageWidth,
                    0.04f,
                    2.50f);

            float targetHeight =
                Mathf.Clamp(
                    squareSidePixels /
                    imageHeight,
                    0.04f,
                    2.50f);

            // MediaPipe uses landmark 33 -> 263 as the rotation vector.
            // Convert the top-left landmark Y convention to the bottom-left
            // convention used by the Unity crop matrix.
            float eyeDxPixels =
                (
                    points[263].x -
                    points[33].x
                ) *
                imageWidth;

            float eyeDyPixelsTop =
                (
                    points[263].y -
                    points[33].y
                ) *
                imageHeight;

            float targetRoll =
                Mathf.Atan2(
                    -eyeDyPixelsTop,
                    eyeDxPixels);

            // Advance the ROI from the newest accepted source timestamp only.
            // The multi-lane stale-source guard already prevents time reversal.
            _regionCenter =
                targetCenter;

            _regionWidth =
                targetWidth;

            _regionHeight =
                targetHeight;

            _regionRollRadians =
                targetRoll;
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
                eyeB -
                eyeA;

            Vector3 upHint =
                forehead -
                chin;

            if (
                right.sqrMagnitude <
                    0.0000001f ||
                upHint.sqrMagnitude <
                    0.0000001f
            )
            {
                return
                    Quaternion.identity;
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
                return
                    Quaternion.identity;
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

        private static void SynthesizeIrisLandmarks(
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

            points[468] =
                irisA;

            points[469] =
                points[33];

            points[470] =
                points[159];

            points[471] =
                points[133];

            points[472] =
                points[145];

            Vector3 irisB =
                (
                    points[362] +
                    points[263] +
                    points[386] +
                    points[374]
                ) *
                0.25f;

            points[473] =
                irisB;

            points[474] =
                points[362];

            points[475] =
                points[386];

            points[476] =
                points[263];

            points[477] =
                points[374];
        }

        private void RegisterDecodeFailure(
            DecodeStatus status)
        {
            LatestRejectionReason =
                status.ToString();

            if (
                status ==
                DecodeStatus.PresenceLow
            )
            {
                _rejectedPresenceFrameCount++;
            }
            else
            {
                _rejectedInvalidFrameCount++;
            }

            RegisterFailure();
        }

        private void RegisterFailure()
        {
            _consecutiveFailures++;

            if (
                _consecutiveFailures >=
                    4
            )
            {
                _hasRegion =
                    false;
            }
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
                    (
                        finished -
                        started
                    ) *
                    1000.0 /
                    System.Diagnostics.Stopwatch
                        .Frequency);

            LatestLatencyMs =
                LatestLatencyMs >
                    0f
                    ? Mathf.Lerp(
                        LatestLatencyMs,
                        milliseconds,
                        0.16f)
                    : milliseconds;

            UpdateSchedulingLaneBudget();
        }

        private void UpdateSchedulingLaneBudget()
        {
            if (
                Application.isMobilePlatform ||
                _lanes == null ||
                _lanes.Length <= 1 ||
                LatestLatencyMs <= 0f
            )
            {
                return;
            }

            // KIWI_V5_0_ADAPTIVE_1_2_3_LANE_BUDGET
            // Commercial realtime trackers prefer a lower-latency stable cadence
            // over a deeper queue. One lane is therefore allowed under sustained
            // severe GPU latency, two lanes are the normal desktop operating
            // point, and the third preallocated lane is used only after a long
            // low-latency streak. No buffer is allocated or destroyed here.
            if (
                _schedulingLaneLimit >= 2 &&
                LatestLatencyMs >= ReduceToSingleLaneAboveMs
            )
            {
                _severeLatencyCompletionStreak =
                    Mathf.Min(
                        _severeLatencyCompletionStreak + 1,
                        ReduceToSingleLaneStreak);

                _recoveryLatencyCompletionStreak = 0;
                _lowLatencyCompletionStreak = 0;

                if (
                    _severeLatencyCompletionStreak >=
                        ReduceToSingleLaneStreak
                )
                {
                    _schedulingLaneLimit = 1;
                    _nextLaneIndex = 0;
                    _highLatencyCompletionStreak = 0;
                }

                return;
            }

            _severeLatencyCompletionStreak = 0;

            if (_schedulingLaneLimit <= 1)
            {
                if (LatestLatencyMs <= RecoverSecondLaneBelowMs)
                {
                    _recoveryLatencyCompletionStreak =
                        Mathf.Min(
                            _recoveryLatencyCompletionStreak + 1,
                            RecoverSecondLaneStreak);

                    if (
                        _recoveryLatencyCompletionStreak >=
                            RecoverSecondLaneStreak
                    )
                    {
                        _schedulingLaneLimit =
                            Mathf.Min(
                                2,
                                _lanes.Length);

                        _recoveryLatencyCompletionStreak = 0;
                    }
                }
                else
                {
                    _recoveryLatencyCompletionStreak = 0;
                }

                return;
            }

            _recoveryLatencyCompletionStreak = 0;

            if (
                _schedulingLaneLimit >= 3 &&
                LatestLatencyMs >= DisableThirdLaneAboveMs
            )
            {
                _highLatencyCompletionStreak =
                    Mathf.Min(
                        _highLatencyCompletionStreak + 1,
                        DisableThirdLaneStreak);

                _lowLatencyCompletionStreak = 0;

                if (
                    _highLatencyCompletionStreak >=
                        DisableThirdLaneStreak
                )
                {
                    // Pending work in lane 2 is allowed to finish. We only stop
                    // assigning new frames to it, so buffers are never reused
                    // while a GPU readback still owns them.
                    _schedulingLaneLimit =
                        Mathf.Min(
                            2,
                            _lanes.Length);

                    _nextLaneIndex =
                        _nextLaneIndex %
                        Mathf.Max(
                            1,
                            _schedulingLaneLimit);

                    _highLatencyCompletionStreak = 0;
                }

                return;
            }

            _highLatencyCompletionStreak = 0;

            if (
                _schedulingLaneLimit == 2 &&
                _lanes.Length >= 3 &&
                LatestLatencyMs <= EnableThirdLaneBelowMs
            )
            {
                _lowLatencyCompletionStreak =
                    Mathf.Min(
                        _lowLatencyCompletionStreak + 1,
                        EnableThirdLaneStreak);

                if (
                    _lowLatencyCompletionStreak >=
                        EnableThirdLaneStreak
                )
                {
                    _schedulingLaneLimit = 3;
                    _lowLatencyCompletionStreak = 0;
                }
            }
            else
            {
                _lowLatencyCompletionStreak = 0;
            }
        }

        private static float NormalizePresence(
            float value)
        {
            if (
                float.IsNaN(value) ||
                float.IsInfinity(value)
            )
            {
                return 0f;
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
