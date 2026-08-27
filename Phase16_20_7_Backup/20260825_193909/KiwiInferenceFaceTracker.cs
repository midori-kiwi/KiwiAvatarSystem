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
    /// Phase 16.10 keeps a bounded commercial fresh-frame pipeline on desktop:
    /// three GPU lanes are preallocated, at least two remain schedulable, and the
    /// third lane is shed only under sustained pressure. Camera frames are still
    /// coalesced latest-first and stale completions are discarded, so increasing
    /// overlap raises fresh-result cadence without creating an unbounded queue.
    ///
    /// Phase 16.11 is freshness-first rather than throughput-only. Soft auxiliary
    /// MediaPipe ROI corrections become future-only updates instead of invalidating
    /// already submitted jobs whose crop matrix/source timestamp are still coherent.
    /// The live path also schedules a newest camera frame before CPU completion
    /// decode when a free lane exists and no completed GPU result is waiting.
    ///
    /// Phase 16.12 separates sample rejection from ROI authority loss. Short
    /// presence/validity dips retain the last trusted ROI and optionally widen only
    /// the recovery crop. After the existing failure-health gate is crossed, high-
    /// rate ownership is allowed to fall back while the internal ROI remains leased;
    /// a trusted inference result or auxiliary MediaPipe anchor re-arms it without a
    /// hard reset. The ROI is released only after the trusted lease actually expires.
    ///
    /// Phase 16.13 is latency-first. Desktop begins single-flight, measures the
    /// actual service latency, and promotes to two/three bounded lanes only if one
    /// lane cannot sustain the minimum commercial cadence. This distinguishes true
    /// model time from queue depth without changing ROI or confidence semantics.
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
            Exception = 8,
            StaleCrossSystem = 9
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

            // KIWI_V5_1_PHASE16_12_INFERENCE_STAGE_PROFILING
            // These host timestamps split CPU submit, GPU+readback wait and
            // CPU decode without introducing any blocking synchronization.
            public long pendingScheduleBeginHostTicks;
            public long pendingReadbackRequestHostTicks;

            public int pendingAnchorRevision;
            public int pendingExternalAnchorEpoch;
            public int pendingTrackerGeneration;

            // KIWI_V5_1_PHASE2_INFERENCE_ASYNC_IDENTITY
            // Capture only identities that can invalidate this camera/inference
            // job. Active provider/model/semantic identity is deliberately not
            // included because this tracker may produce the next provider
            // candidate and does not depend on the currently bound avatar.
            public int pendingCameraGeneration;
            public int pendingTrackingSessionGeneration;

            // Decode policy belongs to the submitted job. A UI/profile threshold
            // change while GPU work is pending must not reinterpret an older
            // result with a newer presence threshold.
            public float pendingMinimumPresence;

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

                pendingScheduleBeginHostTicks = 0L;
                pendingReadbackRequestHostTicks = 0L;
                pendingExternalAnchorEpoch = 0;
                pendingCameraGeneration = 0;
                pendingTrackingSessionGeneration = 0;
                pendingMinimumPresence = 0f;

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
            public int externalAnchorEpoch;
            public int trackerGeneration;
            public int cameraGeneration;
            public int trackingSessionGeneration;
            public float rawPresence;
            public float presence;
            public Quaternion rotation;
        }

        private readonly Lane[] _lanes;

        // KIWI_V5_0_ADAPTIVE_LANE_BUDGET
        // KIWI_V5_1_PHASE16_10_COMMERCIAL_FRESH_FRAME_PIPELINE
        // KIWI_V5_1_PHASE16_10_DESKTOP_BOUNDED_2_3_LANE
        // KIWI_V5_1_PHASE16_13_LATENCY_FIRST_SINGLE_FLIGHT
        // KIWI_V5_1_PHASE16_14_STABLE_DESKTOP_THREE_LANE
        // Phase 16.13 proved that the ~90-100 ms single-flight latency is real
        // service time on this Windows/DX11 path, not merely three-lane queue
        // depth. The adaptive 1/2/3 lane policy therefore reduced cadence without
        // reducing accepted-source age. Phase 16.14 keeps all workers preallocated
        // and fixes desktop scheduling at the available three-lane ceiling
        // (clamped to the actual allocated lane count). Camera history remains
        // bounded because unscheduled frames still coalesce to the newest source.
        private int _schedulingLaneLimit = 3;
        private int _lowLatencyCompletionStreak;
        private int _highLatencyCompletionStreak;
        private int _severeLatencyCompletionStreak;
        private int _recoveryLatencyCompletionStreak;
        private long _previousReadbackCompletionHostTicks;
        private float _latestCompletionIntervalMs;
        private int _latencyFirstLanePromotionCount;
        private int _latencyFirstLaneDemotionCount;
        private int _singleFlightProbeCount;

        private const int DesktopStableSchedulingLanes = 3;
        private const int MobileStableSchedulingLanes = 2;

        // KIWI_V5_1_PHASE16_12_PERSISTENT_ROI_AUTHORITY
        // A rejected sample is not proof that the ROI itself is invalid. Keep
        // the internal crop authority while a recent valid inference or
        // auxiliary MediaPipe anchor still vouches for it. This lease is
        // internal only: it never republishes an old landmark sample.
        private const int PersistentRegionMinimumFailureCount = 4;
        private const float PersistentRegionGraceSeconds = 0.75f;

        // Recovery-only crop widening. This is not a presentation filter and
        // does not move the ROI center. A valid sample immediately tightens the
        // crop back through the normal landmark->ROI solve.
        private const int RecoveryExpansionStartFailures = 2;
        private const int RecoveryExpansionFullFailures = 4;
        private const float RecoveryExpansionMaxScale = 1.35f;

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
        private int _externalAnchorEpoch;
        private int _trackerGeneration;
        private int _consecutiveFailures;
        private int _nextLaneIndex;

        // Phase 16.12 persistent internal ROI lease. The timestamp is refreshed
        // only by a valid inference result or a MediaPipe external anchor.
        private long _lastTrustedRegionHostTicks;
        private float _trustedRegionWidth;
        private float _trustedRegionHeight;
        private bool _regionRetentionActive;
        private float _regionRecoveryScale = 1f;
        private int _retainedRegionFailureCount;
        private int _regionReleaseCount;

        private long _latestCompletedSourceHostTicks;
        private long _latestCompletedArrivalHostTicks;

        private int _scheduledFrameCount;
        private int _readbackCompletedFrameCount;
        private int _completedFrameCount;
        private int _droppedFreshFrameCount;
        private int _rejectedPresenceFrameCount;
        private int _rejectedInvalidFrameCount;
        private int _discardedStaleFrameCount;
        private int _discardedCrossSystemFrameCount;
        // KIWI_V5_1_PHASE16_11_FRESHNESS_FIRST_INFERENCE
        private int _discardedStaleAnchorFrameCount;
        private int _discardedStaleGenerationFrameCount;
        private int _discardedStaleSourceFrameCount;
        private int _softExternalAnchorUpdateCount;
        private int _hardExternalAnchorInvalidationCount;
        private int _softAnchorSupersededRoiUpdateCount;
        private float _latestScheduleDelayMs;
        private float _latestSourceToCompletionAgeMs;
        private float _latestAcceptedSourceAgeMs;

        // Phase 16.12 stage profiling. GPU execution and transfer cannot be
        // separated exactly by the public non-blocking Inference Engine API, so
        // the middle metric is intentionally named GPU+readback wait.
        private float _latestScheduleCpuMs;
        private float _latestGpuReadbackWaitMs;
        private float _latestDecodeCpuMs;

        public float MinimumPresence { get; set; } =
            0.5f;

        public bool HasRegion =>
            _hasRegion;

        // Tracking health and ROI existence are intentionally different.
        // After the failure gate, Runner may fall back to MediaPipe while the
        // internal ROI lease remains available for a soft re-arm.
        public bool IsTracking =>
            _hasRegion &&
            _consecutiveFailures <
                PersistentRegionMinimumFailureCount;

        public int ConsecutiveFailures =>
            _consecutiveFailures;

        public bool RegionRetentionActive =>
            _regionRetentionActive;

        public float RegionTrustedAgeMs =>
            GetTrustedRegionAgeMs(
                System.Diagnostics.Stopwatch.GetTimestamp());

        public float RegionGraceRemainingMs =>
            GetRegionGraceRemainingMs(
                System.Diagnostics.Stopwatch.GetTimestamp());

        public float RegionRecoveryScale =>
            _regionRecoveryScale;

        public int RetainedRegionFailureCount =>
            _retainedRegionFailureCount;

        public int RegionReleaseCount =>
            _regionReleaseCount;

        public float RegionCenterXNormalized =>
            _regionCenter.x;

        public float RegionCenterYNormalized =>
            _regionCenter.y;

        // Runner uses this together with IsTracking to decide whether the
        // high-rate backend may continue owning publication. Once the health
        // gate is crossed, do not let still-draining GPU lanes block immediate
        // MediaPipe fallback. Physical lane pressure remains observable through
        // ActiveLaneCount / OldestPendingAgeMs.
        public bool IsAsyncReadbackPending =>
            IsTracking &&
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

        public bool LatencyFirstSchedulingActive =>
            false;

        public bool StableDesktopSchedulingActive =>
            !Application.isMobilePlatform;

        public float LatestCompletionIntervalMs =>
            _latestCompletionIntervalMs;

        public int LatencyFirstLanePromotionCount =>
            _latencyFirstLanePromotionCount;

        public int LatencyFirstLaneDemotionCount =>
            _latencyFirstLaneDemotionCount;

        public int SingleFlightProbeCount =>
            _singleFlightProbeCount;

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

        public float LatestScheduleDelayMs =>
            _latestScheduleDelayMs;

        public float LatestSourceToCompletionAgeMs =>
            _latestSourceToCompletionAgeMs;

        public float LatestAcceptedSourceAgeMs =>
            _latestAcceptedSourceAgeMs;

        public float LatestScheduleCpuMs =>
            _latestScheduleCpuMs;

        public float LatestGpuReadbackWaitMs =>
            _latestGpuReadbackWaitMs;

        public float LatestDecodeCpuMs =>
            _latestDecodeCpuMs;

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

        public int DiscardedCrossSystemFrameCount =>
            _discardedCrossSystemFrameCount;

        public int DiscardedStaleAnchorFrameCount =>
            _discardedStaleAnchorFrameCount;

        public int DiscardedStaleGenerationFrameCount =>
            _discardedStaleGenerationFrameCount;

        public int DiscardedStaleSourceFrameCount =>
            _discardedStaleSourceFrameCount;

        public int SoftExternalAnchorUpdateCount =>
            _softExternalAnchorUpdateCount;

        public int HardExternalAnchorInvalidationCount =>
            _hardExternalAnchorInvalidationCount;

        public int SoftAnchorSupersededRoiUpdateCount =>
            _softAnchorSupersededRoiUpdateCount;

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
                        ? MobileStableSchedulingLanes
                        : DesktopStableSchedulingLanes,
                    1,
                    Mathf.Max(
                        1,
                        _lanes.Length));

            _singleFlightProbeCount = 0;
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

            _externalAnchorEpoch =
                0;

            _regionWidth =
                0f;

            _regionHeight =
                0f;

            _inputGammaPreservationActive =
                false;

            _consecutiveFailures =
                0;

            _lastTrustedRegionHostTicks =
                0L;

            _trustedRegionWidth =
                0f;

            _trustedRegionHeight =
                0f;

            _regionRetentionActive =
                false;

            _regionRecoveryScale =
                1f;

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
                        ? MobileStableSchedulingLanes
                        : DesktopStableSchedulingLanes,
                    1,
                    Mathf.Max(
                        1,
                        _lanes != null
                            ? _lanes.Length
                            : 1));

            _previousReadbackCompletionHostTicks = 0L;
            _latestCompletionIntervalMs = 0f;
            _latencyFirstLanePromotionCount = 0;
            _latencyFirstLaneDemotionCount = 0;
            _singleFlightProbeCount = 0;

            LatestPresence =
                0f;

            LatestRawPresenceLogit =
                0f;

            LatestRejectionReason =
                "-";

            LatestLatencyMs =
                0f;

            _latestScheduleDelayMs =
                0f;

            _latestSourceToCompletionAgeMs =
                0f;

            _latestAcceptedSourceAgeMs =
                0f;

            _latestScheduleCpuMs =
                0f;

            _latestGpuReadbackWaitMs =
                0f;

            _latestDecodeCpuMs =
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
                    rollRadiansBottomLeft,
                    true);

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
                _regionRetentionActive ||
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
                // KIWI_V5_1_PHASE16_11_SOFT_ANCHOR_FUTURE_ONLY
                // This correction updates future crops only. Jobs already in
                // flight retain the exact crop matrix/source timestamp they were
                // submitted with, so invalidating all of them here only creates
                // avoidable age and gaps. Hard invalidation remains reserved for
                // first-anchor / forced-reacquire transitions.
                AdoptExternalAnchor(
                    centerBottomLeft,
                    width,
                    height,
                    rollRadiansBottomLeft,
                    false);

                return;
            }

            // A close MediaPipe anchor is still valuable ROI evidence. It
            // confirms the current crop without moving it and refreshes the
            // persistent lease. A sub-threshold 1-3 inference failure streak is
            // intentionally preserved; only an already-unhealthy backend is
            // explicitly re-armed by the next trusted anchor.
            MarkExternalAnchorTrusted();
        }

        private void AdoptExternalAnchor(
            Vector2 centerBottomLeft,
            float width,
            float height,
            float rollRadiansBottomLeft,
            bool invalidatePending)
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

            MarkExternalAnchorTrusted();

            _externalAnchorEpoch++;

            if (invalidatePending)
            {
                _anchorRevision++;
                _hardExternalAnchorInvalidationCount++;
            }
            else
            {
                _softExternalAnchorUpdateCount++;
            }
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
                    RegisterDecodeFailure(
                        DecodeStatus.InvalidOutput);
                    return false;
                }

                using Tensor<float> readableOutput =
                    packedOutput.ReadbackAndClone();

                DecodeStatus status =
                    DecodeReadableOutput(
                        readableOutput,
                        cropMatrix,
                        lane.decodedLandmarks,
                        Mathf.Clamp01(MinimumPresence),
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

                MarkRegionTrusted();

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

            EvaluateRegionLeaseExpiry();

            bool shouldScheduleLatest =
                scheduleLatestSource &&
                IsTracking &&
                source != null;

            // KIWI_V5_1_PHASE16_11_SCHEDULE_BEFORE_CPU_DECODE
            // If a lane is already free and no GPU completion is waiting, push
            // the newest camera frame immediately. A ready completion still polls
            // first so its newer ROI can be used for the next crop.
            if (
                shouldScheduleLatest &&
                HasSchedulableFreeLane() &&
                !HasReadyCompletion())
            {
                scheduledLatestSource =
                    TryScheduleNewestSource(
                        source,
                        flipHorizontally,
                        flipVertically,
                        latestSourceHostTicks);
            }

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

                RecordAcceptedSourceAge(
                    newestValidCompletion.sourceHostTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp());

                // A pre-correction result remains valid for presentation in
                // the crop matrix/source frame it was submitted with, but it must
                // not roll a newer external ROI correction backwards.
                if (
                    newestValidCompletion.externalAnchorEpoch ==
                    _externalAnchorEpoch)
                {
                    UpdateRegionFromLandmarks(
                        _landmarks);
                }
                else
                {
                    _softAnchorSupersededRoiUpdateCount++;
                }

                MarkRegionTrusted();

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
                        DecodeStatus.StaleSource &&
                    newestCompletion.status !=
                        DecodeStatus.StaleCrossSystem
                )
                {
                    LatestRejectionReason =
                        newestCompletion.status.ToString();
                }
            }

            if (shouldScheduleLatest)
            {
                if (!scheduledLatestSource)
                {
                    scheduledLatestSource =
                        TryScheduleNewestSource(
                            source,
                            flipHorizontally,
                            flipVertically,
                            latestSourceHostTicks);
                }

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

                RecordSourceToCompletionAge(
                    completedSourceTicks,
                    arrivalHostTicks);

                long startedTicks =
                    lane.pendingStartedHostTicks;

                long readbackRequestTicks =
                    lane.pendingReadbackRequestHostTicks;

                int completedAnchorRevision =
                    lane.pendingAnchorRevision;

                int completedExternalAnchorEpoch =
                    lane.pendingExternalAnchorEpoch;

                int completedGeneration =
                    lane.pendingTrackerGeneration;

                int completedCameraGeneration =
                    lane.pendingCameraGeneration;

                int completedTrackingSessionGeneration =
                    lane.pendingTrackingSessionGeneration;

                float completedMinimumPresence =
                    lane.pendingMinimumPresence;

                lane.readbackPending =
                    false;

                lane.pendingOutput =
                    null;

                lane.pendingSourceHostTicks =
                    0L;

                lane.pendingStartedHostTicks =
                    0L;

                lane.pendingScheduleBeginHostTicks =
                    0L;

                lane.pendingReadbackRequestHostTicks =
                    0L;

                lane.pendingAnchorRevision =
                    0;

                lane.pendingExternalAnchorEpoch =
                    0;

                lane.pendingTrackerGeneration =
                    0;

                lane.pendingCameraGeneration =
                    0;

                lane.pendingTrackingSessionGeneration =
                    0;

                lane.pendingMinimumPresence =
                    0f;

                _readbackCompletedFrameCount++;

                RecordReadbackCompletionInterval(
                    arrivalHostTicks);

                RecordGpuReadbackWait(
                    readbackRequestTicks,
                    arrivalHostTicks);

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
                        externalAnchorEpoch =
                            completedExternalAnchorEpoch,
                        trackerGeneration =
                            completedGeneration,
                        cameraGeneration =
                            completedCameraGeneration,
                        trackingSessionGeneration =
                            completedTrackingSessionGeneration
                    };

                // KIWI_V5_1_PHASE2_INFERENCE_ASYNC_IDENTITY
                // Retire every stale job before ReadbackAndClone. Local tracker
                // generation/anchor/source ordering and cross-system camera/session
                // identity are all known without touching the GPU output. This
                // preserves behavior while avoiding CPU readback/decode work for
                // results that can never be published.
                DecodeStatus preDecodeStaleStatus =
                    DecodeStatus.None;

                if (
                    completedGeneration !=
                        _trackerGeneration
                )
                {
                    preDecodeStaleStatus =
                        DecodeStatus.StaleGeneration;
                }
                else if (
                    completedAnchorRevision !=
                        _anchorRevision
                )
                {
                    preDecodeStaleStatus =
                        DecodeStatus.StaleAnchor;
                }
                else if (
                    completedSourceTicks > 0L &&
                    _latestCompletedSourceHostTicks > 0L &&
                    completedSourceTicks <=
                        _latestCompletedSourceHostTicks
                )
                {
                    preDecodeStaleStatus =
                        DecodeStatus.StaleSource;
                }
                else if (
                    !KiwiRuntimeGenerationContext.IsCameraSessionIdentityCurrent(
                        completedCameraGeneration,
                        completedTrackingSessionGeneration)
                )
                {
                    preDecodeStaleStatus =
                        DecodeStatus.StaleCrossSystem;
                }

                if (preDecodeStaleStatus != DecodeStatus.None)
                {
                    completion.status =
                        preDecodeStaleStatus;

                    completion.valid =
                        false;

                    // The stale job is intentionally not decoded, so keep the
                    // last real presence diagnostics instead of publishing a
                    // synthetic zero from an uninspected tensor.
                    completion.rawPresence =
                        LatestRawPresenceLogit;

                    completion.presence =
                        LatestPresence;

                    _discardedStaleFrameCount++;

                    switch (preDecodeStaleStatus)
                    {
                        case DecodeStatus.StaleAnchor:
                            _discardedStaleAnchorFrameCount++;
                            break;

                        case DecodeStatus.StaleGeneration:
                            _discardedStaleGenerationFrameCount++;
                            break;

                        case DecodeStatus.StaleSource:
                            _discardedStaleSourceFrameCount++;
                            break;

                        case DecodeStatus.StaleCrossSystem:
                            _discardedCrossSystemFrameCount++;
                            break;
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

                    continue;
                }

                long decodeStartHostTicks =
                    System.Diagnostics.Stopwatch.GetTimestamp();

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
                            completedMinimumPresence,
                            out float rawPresence,
                            out float presence,
                            out Quaternion rotation);

                    completion.rawPresence =
                        rawPresence;

                    completion.presence =
                        presence;

                    completion.rotation =
                        rotation;

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
                            DecodeStatus.StaleSource &&
                        status !=
                            DecodeStatus.StaleCrossSystem
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

                RecordDecodeCpu(
                    decodeStartHostTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp());

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

            long scheduleBeginHostTicks =
                System.Diagnostics.Stopwatch.GetTimestamp();

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
                    RegisterDecodeFailure(
                        DecodeStatus.InvalidOutput);
                    return false;
                }

                lane.pendingCropMatrix =
                    cropMatrix;

                lane.pendingSourceHostTicks =
                    sourceHostTicks > 0L
                        ? sourceHostTicks
                        : System.Diagnostics.Stopwatch
                            .GetTimestamp();

                lane.pendingScheduleBeginHostTicks =
                    scheduleBeginHostTicks;

                lane.pendingStartedHostTicks =
                    System.Diagnostics.Stopwatch
                        .GetTimestamp();

                RecordScheduleDelay(
                    lane.pendingSourceHostTicks,
                    lane.pendingStartedHostTicks);

                lane.pendingAnchorRevision =
                    _anchorRevision;

                lane.pendingExternalAnchorEpoch =
                    _externalAnchorEpoch;

                lane.pendingTrackerGeneration =
                    _trackerGeneration;

                KiwiRuntimeGenerationContext.Snapshot generationSnapshot =
                    KiwiRuntimeGenerationContext.Capture();

                lane.pendingCameraGeneration =
                    generationSnapshot.cameraGeneration;

                lane.pendingTrackingSessionGeneration =
                    generationSnapshot.trackingSessionGeneration;

                lane.pendingMinimumPresence =
                    Mathf.Clamp01(MinimumPresence);

                lane.pendingOutput =
                    packedOutput;

                lane.pendingReadbackRequestHostTicks =
                    System.Diagnostics.Stopwatch.GetTimestamp();

                lane.pendingOutput
                    .ReadbackRequest();

                RecordScheduleCpu(
                    lane.pendingScheduleBeginHostTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp());

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

                lane.pendingScheduleBeginHostTicks =
                    0L;

                lane.pendingReadbackRequestHostTicks =
                    0L;

                lane.pendingAnchorRevision =
                    0;

                lane.pendingExternalAnchorEpoch =
                    0;

                lane.pendingTrackerGeneration =
                    0;

                lane.pendingCameraGeneration =
                    0;

                lane.pendingTrackingSessionGeneration =
                    0;

                lane.pendingMinimumPresence =
                    0f;

                RegisterDecodeFailure(
                    DecodeStatus.Exception);

                return false;
            }
        }

        private bool HasSchedulableFreeLane()
        {
            return FindFreeLane() >= 0;
        }

        private bool HasReadyCompletion()
        {
            if (_lanes == null)
            {
                return false;
            }

            for (int i = 0; i < _lanes.Length; i++)
            {
                Lane lane = _lanes[i];

                if (
                    lane != null &&
                    lane.readbackPending &&
                    lane.pendingOutput != null &&
                    lane.pendingOutput.IsReadbackRequestDone())
                {
                    return true;
                }
            }

            return false;
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
            float minimumPresence,
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
                        minimumPresence)
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

            if (!_hasRegion)
            {
                return;
            }

            long now =
                System.Diagnostics.Stopwatch.GetTimestamp();

            float trustedAgeMs =
                GetTrustedRegionAgeMs(now);

            bool hasTrustedLease =
                _lastTrustedRegionHostTicks > 0L &&
                trustedAgeMs <
                    PersistentRegionGraceSeconds * 1000f;

            if (
                _consecutiveFailures <
                    PersistentRegionMinimumFailureCount ||
                hasTrustedLease
            )
            {
                // KIWI_V5_1_PHASE16_12_PRESENCE_RECOVERY_EXPANSION
                // Reject this sample but retain the internal ROI. No old
                // landmark sample is republished. Widen only the recovery crop
                // so fast motion can re-enter the 192x192 model field without
                // inventing a predicted center.
                _regionRetentionActive =
                    true;

                _retainedRegionFailureCount++;

                ExpandRegionForRecovery();
                return;
            }

            ReleasePersistentRegion();
        }

        private void EvaluateRegionLeaseExpiry()
        {
            if (
                !_hasRegion ||
                _consecutiveFailures <
                    PersistentRegionMinimumFailureCount
            )
            {
                return;
            }

            long now =
                System.Diagnostics.Stopwatch.GetTimestamp();

            bool leaseExpired =
                _lastTrustedRegionHostTicks <= 0L ||
                GetTrustedRegionAgeMs(now) >=
                    PersistentRegionGraceSeconds * 1000f;

            if (leaseExpired)
            {
                ReleasePersistentRegion();
            }
        }

        private void ReleasePersistentRegion()
        {
            if (!_hasRegion)
            {
                return;
            }

            // The trusted ROI lease has genuinely expired. Retire already
            // submitted jobs from this ROI before allowing a future external
            // anchor to start a fresh hard-reacquisition generation.
            _hasRegion =
                false;

            _regionRetentionActive =
                false;

            _regionRecoveryScale =
                1f;

            _regionReleaseCount++;

            _anchorRevision++;
        }

        private void MarkExternalAnchorTrusted()
        {
            _hasRegion =
                true;

            // A MediaPipe anchor validates ROI geometry, not the Inference
            // Engine presence classifier itself. Preserve a 1-3 sample failure
            // streak so frequent auxiliary anchors cannot keep a failing high-
            // rate backend falsely "healthy". Once the health gate has already
            // been crossed and Runner has yielded publication to MediaPipe, the
            // next trusted anchor explicitly re-arms one fresh inference cycle.
            if (
                _consecutiveFailures >=
                    PersistentRegionMinimumFailureCount
            )
            {
                _consecutiveFailures =
                    0;
            }

            _regionRetentionActive =
                false;

            _regionRecoveryScale =
                1f;

            _lastTrustedRegionHostTicks =
                System.Diagnostics.Stopwatch.GetTimestamp();

            _trustedRegionWidth =
                Mathf.Clamp(
                    _regionWidth,
                    0.04f,
                    2.50f);

            _trustedRegionHeight =
                Mathf.Clamp(
                    _regionHeight,
                    0.04f,
                    2.50f);
        }

        private void MarkRegionTrusted()
        {
            _hasRegion =
                true;

            _consecutiveFailures =
                0;

            _regionRetentionActive =
                false;

            _regionRecoveryScale =
                1f;

            _lastTrustedRegionHostTicks =
                System.Diagnostics.Stopwatch.GetTimestamp();

            _trustedRegionWidth =
                Mathf.Clamp(
                    _regionWidth,
                    0.04f,
                    2.50f);

            _trustedRegionHeight =
                Mathf.Clamp(
                    _regionHeight,
                    0.04f,
                    2.50f);
        }

        private void ExpandRegionForRecovery()
        {
            if (
                _trustedRegionWidth <= 0.0001f ||
                _trustedRegionHeight <= 0.0001f ||
                _consecutiveFailures <
                    RecoveryExpansionStartFailures
            )
            {
                _regionRecoveryScale =
                    1f;
                return;
            }

            float t =
                Mathf.InverseLerp(
                    RecoveryExpansionStartFailures,
                    RecoveryExpansionFullFailures,
                    _consecutiveFailures);

            _regionRecoveryScale =
                Mathf.Lerp(
                    1f,
                    RecoveryExpansionMaxScale,
                    t);

            _regionWidth =
                Mathf.Clamp(
                    Mathf.Max(
                        _regionWidth,
                        _trustedRegionWidth *
                            _regionRecoveryScale),
                    0.04f,
                    2.50f);

            _regionHeight =
                Mathf.Clamp(
                    Mathf.Max(
                        _regionHeight,
                        _trustedRegionHeight *
                            _regionRecoveryScale),
                    0.04f,
                    2.50f);
        }

        private float GetTrustedRegionAgeMs(
            long nowHostTicks)
        {
            if (
                _lastTrustedRegionHostTicks <= 0L ||
                nowHostTicks <=
                    _lastTrustedRegionHostTicks
            )
            {
                return 0f;
            }

            return
                HostTickDeltaMilliseconds(
                    _lastTrustedRegionHostTicks,
                    nowHostTicks);
        }

        private float GetRegionGraceRemainingMs(
            long nowHostTicks)
        {
            if (_lastTrustedRegionHostTicks <= 0L)
            {
                return 0f;
            }

            return
                Mathf.Max(
                    0f,
                    PersistentRegionGraceSeconds *
                        1000f -
                    GetTrustedRegionAgeMs(
                        nowHostTicks));
        }

        private void RecordScheduleCpu(
            long startedHostTicks,
            long finishedHostTicks)
        {
            if (
                startedHostTicks <= 0L ||
                finishedHostTicks <=
                    startedHostTicks
            )
            {
                return;
            }

            _latestScheduleCpuMs =
                SmoothDiagnosticMilliseconds(
                    _latestScheduleCpuMs,
                    HostTickDeltaMilliseconds(
                        startedHostTicks,
                        finishedHostTicks));
        }

        private void RecordGpuReadbackWait(
            long requestHostTicks,
            long finishedHostTicks)
        {
            if (
                requestHostTicks <= 0L ||
                finishedHostTicks <=
                    requestHostTicks
            )
            {
                return;
            }

            _latestGpuReadbackWaitMs =
                SmoothDiagnosticMilliseconds(
                    _latestGpuReadbackWaitMs,
                    HostTickDeltaMilliseconds(
                        requestHostTicks,
                        finishedHostTicks));
        }

        private void RecordDecodeCpu(
            long startedHostTicks,
            long finishedHostTicks)
        {
            if (
                startedHostTicks <= 0L ||
                finishedHostTicks <=
                    startedHostTicks
            )
            {
                return;
            }

            _latestDecodeCpuMs =
                SmoothDiagnosticMilliseconds(
                    _latestDecodeCpuMs,
                    HostTickDeltaMilliseconds(
                        startedHostTicks,
                        finishedHostTicks));
        }

        private void RecordScheduleDelay(
            long sourceHostTicks,
            long startedHostTicks)
        {
            if (
                sourceHostTicks <= 0L ||
                startedHostTicks <= sourceHostTicks)
            {
                return;
            }

            float milliseconds =
                HostTickDeltaMilliseconds(
                    sourceHostTicks,
                    startedHostTicks);

            _latestScheduleDelayMs =
                SmoothDiagnosticMilliseconds(
                    _latestScheduleDelayMs,
                    milliseconds);
        }

        private void RecordSourceToCompletionAge(
            long sourceHostTicks,
            long arrivalHostTicks)
        {
            if (
                sourceHostTicks <= 0L ||
                arrivalHostTicks <= sourceHostTicks)
            {
                return;
            }

            float milliseconds =
                HostTickDeltaMilliseconds(
                    sourceHostTicks,
                    arrivalHostTicks);

            _latestSourceToCompletionAgeMs =
                SmoothDiagnosticMilliseconds(
                    _latestSourceToCompletionAgeMs,
                    milliseconds);
        }

        private void RecordAcceptedSourceAge(
            long sourceHostTicks,
            long arrivalHostTicks)
        {
            if (
                sourceHostTicks <= 0L ||
                arrivalHostTicks <= sourceHostTicks)
            {
                return;
            }

            float milliseconds =
                HostTickDeltaMilliseconds(
                    sourceHostTicks,
                    arrivalHostTicks);

            _latestAcceptedSourceAgeMs =
                SmoothDiagnosticMilliseconds(
                    _latestAcceptedSourceAgeMs,
                    milliseconds);
        }

        private static float HostTickDeltaMilliseconds(
            long start,
            long end)
        {
            return
                (float)(
                    (end - start) *
                    1000.0 /
                    System.Diagnostics.Stopwatch.Frequency);
        }

        private static float SmoothDiagnosticMilliseconds(
            float current,
            float sample)
        {
            if (
                float.IsNaN(sample) ||
                float.IsInfinity(sample) ||
                sample < 0f)
            {
                return current;
            }

            return
                current > 0f
                    ? Mathf.Lerp(
                        current,
                        sample,
                        0.16f)
                    : sample;
        }

        private void RecordReadbackCompletionInterval(
            long completionHostTicks)
        {
            if (
                _previousReadbackCompletionHostTicks > 0L &&
                completionHostTicks > _previousReadbackCompletionHostTicks
            )
            {
                float milliseconds =
                    (float)(
                        (completionHostTicks -
                         _previousReadbackCompletionHostTicks) *
                        1000.0 /
                        System.Diagnostics.Stopwatch.Frequency);

                if (milliseconds > 0f && milliseconds < 1000f)
                {
                    _latestCompletionIntervalMs =
                        _latestCompletionIntervalMs > 0f
                            ? Mathf.Lerp(
                                _latestCompletionIntervalMs,
                                milliseconds,
                                0.20f)
                            : milliseconds;
                }
            }

            _previousReadbackCompletionHostTicks =
                completionHostTicks;
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
                _lanes == null ||
                _lanes.Length == 0
            )
            {
                return;
            }

            // KIWI_V5_1_PHASE16_14_STABLE_DESKTOP_THREE_LANE
            // Phase 16.13 measured ~89-100 ms even in single-flight. Reducing
            // the number of in-flight jobs therefore cut throughput without
            // removing the dominant service latency. Keep the Windows path at
            // the available three-lane ceiling and leave mobile at the existing
            // conservative two-lane budget. This is scheduling-only: ordering,
            // ROI authority, stale-result guards and confidence remain unchanged.
            int targetLaneLimit =
                Application.isMobilePlatform
                    ? MobileStableSchedulingLanes
                    : DesktopStableSchedulingLanes;

            _schedulingLaneLimit =
                Mathf.Clamp(
                    targetLaneLimit,
                    1,
                    _lanes.Length);

            _nextLaneIndex =
                _nextLaneIndex %
                Mathf.Max(
                    1,
                    _schedulingLaneLimit);

            _lowLatencyCompletionStreak = 0;
            _highLatencyCompletionStreak = 0;
            _severeLatencyCompletionStreak = 0;
            _recoveryLatencyCompletionStreak = 0;

            // Compatibility telemetry from Phase 16.13 remains present so
            // existing CSV readers do not break; stable scheduling never
            // promotes/demotes or launches a single-flight probe.
            _latencyFirstLanePromotionCount = 0;
            _latencyFirstLaneDemotionCount = 0;
            _singleFlightProbeCount = 0;
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
