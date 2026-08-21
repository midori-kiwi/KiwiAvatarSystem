using System.Collections;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    public enum KiwiTrackingBackend
    {
        Unknown = 0,
        MediaPipe = 1,
        InferenceEngine = 2
    }

    public struct FacePrecisionTrackingData
    {
        public bool isValid;
        public ulong frameId;
        public KiwiTrackingBackend backend;

        public Vector2 faceCenter;
        public float eyeSpan2D;
        public float eyeSpan3D;
        public Quaternion faceRotation;

        public Vector2 rightEyeCenter;
        public Vector2 leftEyeCenter;
        public Vector2 eyeCenter;
        public Vector2 chin;
        public Vector2 nose;
        public Vector2 cheekCenter;
        public Vector2 forehead;

        public float faceWidth2D;
        public float faceHeight2D;
        public float geometryQuality;

        public long timestamp;
        public long submissionHostTicks;
        public long arrivalHostTicks;

        // True only when submissionHostTicks was matched to the exact
        // LIVE_STREAM input frame. When false, consumers must not mix
        // arrival timing with prior matched submission timing for velocity/dt.
        public bool hasMatchedSubmissionTiming;
    }

    /// <summary>
    /// Allocation-free math helpers shared by the patched tracking core.
    /// </summary>
    public static class KiwiPrecisionTrackingMath
    {
        private static readonly double HostTickSeconds =
            1.0 / System.Diagnostics.Stopwatch.Frequency;

        public static double HostTicksToSeconds(long ticks)
        {
            if (ticks <= 0L)
            {
                return 0.0;
            }

            return ticks * HostTickSeconds;
        }

        public static Vector3 CalculateAvatarEulerDegrees(
            Quaternion neutralRotation,
            Quaternion faceRotation)
        {
            return CalculateAvatarEulerDegrees(
                neutralRotation,
                faceRotation,
                true
            );
        }

        public static Vector3 CalculateAvatarEulerDegrees(
            Quaternion neutralRotation,
            Quaternion faceRotation,
            bool inputHorizontallyMirrored)
        {
            Quaternion delta =
                faceRotation * Quaternion.Inverse(neutralRotation);

            Vector3 euler = delta.eulerAngles;
            float horizontalSign = inputHorizontallyMirrored ? -1f : 1f;

            return new Vector3(
                ToSignedDegrees(euler.x),
                ToSignedDegrees(euler.y) * horizontalSign,
                ToSignedDegrees(euler.z) * horizontalSign
            );
        }

        public static Vector2 CalculateAvatarCentricPositionDelta(
            Vector2 trackedDelta)
        {
            return CalculateAvatarCentricPositionDelta(
                trackedDelta,
                true
            );
        }

        public static Vector2 CalculateAvatarCentricPositionDelta(
            Vector2 trackedDelta,
            bool inputHorizontallyMirrored)
        {
            // A front-facing avatar's own right appears on the viewer's left.
            // MediaPipe coordinates already include the frame transform, so only
            // mirrored inputs need their horizontal axis inverted here.
            // Keep Y in MediaPipe space; the position mapper handles its inversion.
            float horizontalSign = inputHorizontallyMirrored ? -1f : 1f;
            return new Vector2(
                trackedDelta.x * horizontalSign,
                trackedDelta.y
            );
        }

        public static float CalculateGeometryQuality(
            float eyeSpan,
            float faceWidth,
            float faceHeight)
        {
            float eye = RangeQuality(eyeSpan, 0.010f, 0.035f);
            float width = RangeQuality(faceWidth, 0.025f, 0.090f);
            float height = RangeQuality(faceHeight, 0.030f, 0.110f);

            float sizeQuality = Mathf.Clamp01(
                eye * 0.45f +
                width * 0.30f +
                height * 0.25f
            );

            if (
                faceWidth <= 0.0001f ||
                faceHeight <= 0.0001f
            )
            {
                return 0f;
            }

            // Broad human-face topology sanity checks. These are intentionally
            // permissive so normal yaw/pitch and fast motion remain Landmarker-primary.
            float eyeToWidth = eyeSpan / faceWidth;
            float widthToHeight = faceWidth / faceHeight;

            float proportionQuality =
                BandQuality(eyeToWidth, 0.14f, 0.24f, 0.74f, 0.92f) * 0.60f +
                BandQuality(widthToHeight, 0.30f, 0.45f, 1.90f, 2.60f) * 0.40f;

            return Mathf.Clamp01(
                sizeQuality * Mathf.Lerp(0.35f, 1f, proportionQuality)
            );
        }

        public static float QualityDeadZoneMultiplier(float quality)
        {
            quality = Mathf.Clamp01(quality);
            return Mathf.Lerp(1.75f, 0.80f, quality);
        }

        public static Vector3 AngularVelocityDegrees(
            Quaternion from,
            Quaternion to,
            float dt)
        {
            dt = Mathf.Max(0.0001f, dt);

            Quaternion delta = to * Quaternion.Inverse(from);
            if (delta.w < 0f)
            {
                delta.x = -delta.x;
                delta.y = -delta.y;
                delta.z = -delta.z;
                delta.w = -delta.w;
            }

            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (
                float.IsNaN(angle) ||
                float.IsInfinity(angle) ||
                axis.sqrMagnitude < 0.0000001f)
            {
                return Vector3.zero;
            }

            if (angle > 180f)
            {
                angle -= 360f;
            }

            return axis.normalized * (angle / dt);
        }

        public static Quaternion ExtrapolateRotation(
            Quaternion rotation,
            Vector3 angularVelocityDegrees,
            float seconds,
            float maxDegrees)
        {
            Vector3 deltaDegrees = angularVelocityDegrees * Mathf.Max(0f, seconds);
            float angle = deltaDegrees.magnitude;

            if (angle < 0.00001f)
            {
                return rotation;
            }

            angle = Mathf.Min(angle, Mathf.Max(0f, maxDegrees));
            Vector3 axis = deltaDegrees.normalized;

            return Quaternion.AngleAxis(angle, axis) * rotation;
        }

        private static float RangeQuality(
            float value,
            float minimum,
            float fullQuality)
        {
            if (
                float.IsNaN(value) ||
                float.IsInfinity(value) ||
                value <= minimum)
            {
                return 0f;
            }

            return Mathf.InverseLerp(minimum, fullQuality, value);
        }

        private static float ToSignedDegrees(float angle)
        {
            return angle > 180f
                ? angle - 360f
                : angle;
        }

        private static float BandQuality(
            float value,
            float hardMin,
            float softMin,
            float softMax,
            float hardMax)
        {
            if (
                float.IsNaN(value) ||
                float.IsInfinity(value) ||
                value <= hardMin ||
                value >= hardMax
            )
            {
                return 0f;
            }

            if (value < softMin)
            {
                return Mathf.InverseLerp(hardMin, softMin, value);
            }

            if (value > softMax)
            {
                return 1f - Mathf.InverseLerp(softMax, hardMax, value);
            }

            return 1f;
        }
    }

    public struct FaceExpressionData
    {
        public bool isValid;

        public float eyeBlinkLeft;
        public float eyeBlinkRight;

        public float eyeWideLeft;
        public float eyeWideRight;

        public float cheekSquintLeft;
        public float cheekSquintRight;

        public float jawOpen;

        public float mouthSmileLeft;
        public float mouthSmileRight;

        public float mouthFrownLeft;
        public float mouthFrownRight;

        public float mouthPucker;
        public float mouthFunnel;

        public float browInnerUp;

        public float browDownLeft;
        public float browDownRight;
    }


    public class FaceLandmarkerRunner
        : VisionTaskApiRunner<FaceLandmarker>
    {
        [SerializeField]
        private FaceLandmarkerResultAnnotationController
            _faceLandmarkerResultAnnotationController;

        [Header("Tracking Throughput")]
        [Tooltip("Draw the 478-point MediaPipe debug overlay. Keep OFF for avatar use so annotation rendering cannot reduce tracking/render cadence.")]
        public bool renderDebugLandmarkAnnotations = false;

        [Tooltip("For webcam input, read back only a genuinely new camera frame. Prevents duplicate 1280x720 GPU readbacks from starving rendering.")]
        public bool processOnlyFreshWebCamFrames = true;

        [Tooltip("Keep one LIVE_STREAM request in flight and coalesce camera updates to the newest frame. This prevents an inference backlog during fast motion.")]
        public bool latestFrameOnlyLiveStream = true;

        [Tooltip("Downscale only the LandMarker inference input. Eye/mouth source textures stay at the original camera resolution.")]
        public bool downscaleTrackingInput = true;

        [Tooltip("Maximum LandMarker input width. 480 reduces Windows/DX11 synchronous readback while the visible eye/mouth texture remains at camera resolution.")]
        [Range(320, 1920)]
        public int trackingInputMaxWidth = 480;

        [Tooltip("When a UGREEN CM831 profile is active, use the evaluated 480px LandMarker input with the high-speed 720p60 camera profile.")]
        public bool autoOptimizeCm831 = true;

        [Range(480, 960)]
        public int cm831TrackingInputWidth = 480;

        [Header("Inference Engine + MediaPipe Hybrid")]
        [Tooltip("Use Unity Inference Engine GPU inference for fresh-frame landmarks and keep MediaPipe for acquisition, blendshapes, pose calibration and fallback.")]
        public bool enableSentisHybridTracking = true;

        [Tooltip("MediaPipe refresh rate while Inference Engine is tracking. It corrects ROI drift and refreshes blendshapes without blocking every camera frame.")]
        [Range(2f, 30f)]
        public float sentisMediaPipeRefreshRateHz = 10f;

        [Tooltip("Minimum face-presence output accepted from the Inference Engine landmark model.")]
        [Range(0.1f, 0.95f)]
        public float sentisMinimumPresence = 0.5f;


        private Experimental.TextureFramePool
            _textureFramePool;

        private RenderTexture _trackingInputTexture;
        private int _trackingInputWidth;
        private int _trackingInputHeight;
        private int _sourceTextureWidth;
        private int _sourceTextureHeight;
        private string _sourceName = string.Empty;
        private float _sourceRequestedFrameRate;
        private bool _cm831ProfileActive;
        private KiwiInferenceFaceTracker _sentisTracker;
        private Texture _sentisSourceTexture;
        private bool _sentisFlipHorizontally;
        private bool _sentisFlipVertically;
        private int _lastSentisProcessedGeneration = -1;
        private long _latestSentisSourceFrameHostTicks;
        // KIWI_ASYNC_INFERENCE_MAILBOX_V2_3
        private long _lastSentisAnchorTimestamp = -1L;
        private UnityEngine.Rect _latestSentisAnchorRegion;
        private float _latestSentisAnchorRollRadians;
        private bool _hasLatestSentisAnchor;
        private volatile bool _sentisPrimaryActive;
        private long _lastMediaPipeRefreshHostTicks;
        private Quaternion _latestMediaPipeAuxRotation = Quaternion.identity;
        private bool _hasLatestMediaPipeAuxRotation;
        private Quaternion _sentisRotationOffset = Quaternion.identity;
        private bool _hasSentisRotationOffset;
        private float _latestSentisLatencyMs;
        private float _latestSentisPresence;


        public readonly FaceLandmarkDetectionConfig config =
            new FaceLandmarkDetectionConfig();


        // =====================================================
        // Shared Tracking Data
        // =====================================================

        private readonly object _trackingLock =
            new object();

        // Callback-only staging lock/buffer. Landmark conversion happens here,
        // outside _trackingLock, then the completed buffer is swapped atomically.
        private readonly object _storeTrackingLock =
            new object();

        // Serializes final LIVE_STREAM publish/DrawLater against Stop. This closes
        // the tiny race where StoreTrackingData succeeded just before Stop and the
        // callback queued DrawLater after teardown had already started.
        private readonly object _callbackLifecycleLock =
            new object();

        private Vector2[] _latestLandmarks;
        private Vector2[] _stagingLandmarks;

        private int _latestLandmarkCount = 0;

        private long _latestLandmarkTimestamp = -1;

        // Monotonic callback ordering survives no-face clears.
        private long _latestResultTimestamp = -1;

        // Native LIVE_STREAM callbacks may arrive after Stop during teardown.
        // This volatile gate is checked again under _trackingLock before publish.
        private volatile bool _acceptTrackingResults;

        // LIVE_STREAM latency control. A single outstanding request is enough
        // because MediaPipe's flow limiter drops queued frames internally. Doing
        // the gating here avoids duplicate GPU readbacks and preserves render FPS.
        private volatile bool _liveStreamRequestInFlight;

        // A camera update can occur while the optional one-request gate is
        // closed. Keep a one-slot latest-frame mailbox so that update is not
        // lost merely because the coroutine and camera update phases differ.
        private bool _pendingFreshWebCamFrame;
        private long _pendingSourceFrameHostTicks;
        private int _freshWebCamGeneration;
        private int _lastObservedWebCamUnityFrame = -1;
        private WebCamTexture _observedWebCamTexture;

        // Main-thread pipeline diagnostics. Source/submission/result cadence is
        // intentionally reported separately so a camera, readback, or inference
        // bottleneck can be identified from one recording.
        private long _previousFreshSourceHostTicks;
        private long _previousSubmissionRateHostTicks;
        private float _latestFreshSourceRateHz;
        private float _latestSubmissionRateHz;
        private float _latestReadbackLatencyMs;


        private Vector2 _latestFaceCenter =
            new Vector2(
                0.5f,
                0.5f
            );


        private float _latestFaceEyeSpan = 0f;


        private Quaternion _latestFaceRotation =
            Quaternion.identity;


        private bool _hasLatestFaceRotation = false;


        private long _latestMotionTimestamp = -1;


        // =====================================================
        // Expression Data
        // =====================================================

        private FaceExpressionData
            _latestExpressionData;


        private long _latestExpressionTimestamp =
            -1;


        // =====================================================
        // Precision Tracking Snapshot / Host Timing
        // =====================================================

        private FacePrecisionTrackingData
            _latestPrecisionData;

        private ulong _nextPublishedTrackingFrameId;

        private long _previousPublishedArrivalHostTicks;
        private float _latestTrackingResultRateHz;


        private const int SubmissionHistoryCapacity = 64;

        private readonly object _submissionLock =
            new object();

        private readonly long[] _submissionTimestamps =
            new long[SubmissionHistoryCapacity];

        private readonly long[] _submissionHostTicks =
            new long[SubmissionHistoryCapacity];

        private int _submissionWriteIndex;


        // =====================================================
        // Public Landmark Access
        // =====================================================

        public float LatestTrackingResultRateHz
        {
            get
            {
                lock (_trackingLock)
                {
                    return _latestTrackingResultRateHz;
                }
            }
        }

        public float LatestFreshSourceRateHz =>
            _latestFreshSourceRateHz;

        public float LatestSubmissionRateHz =>
            _latestSubmissionRateHz;

        public float LatestReadbackLatencyMs =>
            _latestReadbackLatencyMs;

        public int TrackingInputWidth =>
            _trackingInputWidth;

        public int TrackingInputHeight =>
            _trackingInputHeight;

        public int SourceTextureWidth =>
            _sourceTextureWidth;

        public int SourceTextureHeight =>
            _sourceTextureHeight;

        public string SourceName =>
            _sourceName;

        public float SourceRequestedFrameRate =>
            _sourceRequestedFrameRate;

        public bool Cm831ProfileActive =>
            _cm831ProfileActive;

        public bool IsInputHorizontallyMirrored { get; private set; }

        public bool SentisHybridEnabled =>
            enableSentisHybridTracking && _sentisTracker != null;

        public bool SentisPrimaryActive =>
            _sentisPrimaryActive;

        public float LatestSentisLatencyMs =>
            _latestSentisLatencyMs;

        public float LatestSentisPresence =>
            _latestSentisPresence;

        // Unity 6 / Inference Engine names. The Sentis-named accessors above
        // remain available so existing scenes and integrations do not break.
        public bool InferenceEngineHybridEnabled =>
            SentisHybridEnabled;

        public bool InferenceEnginePrimaryActive =>
            SentisPrimaryActive;

        public float LatestInferenceEngineLatencyMs =>
            LatestSentisLatencyMs;

        public float LatestInferenceEnginePresence =>
            LatestSentisPresence;

        public string ActiveTrackingBackend =>
            _sentisPrimaryActive
                ? "Inference Engine GPU + MediaPipe"
                : "MediaPipe FaceLandmarker";


        private void Update()
        {
            // This observer continues to run while the processing coroutine is
            // awaiting AsyncGPUReadback. Without it, camera updates occurring in
            // that wait frame are invisible and effective source fps can halve.
            ObserveFreshWebCamFrame(_observedWebCamTexture);
            ProcessSentisFreshFrame();
        }


        private void ProcessSentisFreshFrame()
        {
            if (
                !_acceptTrackingResults ||
                _sentisTracker == null ||
                _sentisSourceTexture == null)
            {
                return;
            }

            bool hasFreshSentisSource =
                _freshWebCamGeneration !=
                _lastSentisProcessedGeneration;

            UnityEngine.Rect anchorRegion = default;
            float anchorRoll = 0f;
            long anchorTimestamp = -1L;
            bool hasAnchor = false;

            lock (_trackingLock)
            {
                hasAnchor = _hasLatestSentisAnchor;
                anchorRegion = _latestSentisAnchorRegion;
                anchorRoll = _latestSentisAnchorRollRadians;
                anchorTimestamp = _lastSentisAnchorTimestamp;
            }

            if (
                hasAnchor &&
                anchorTimestamp >= 0L &&
                anchorTimestamp != _lastSentisAnchorTimestampApplied)
            {
                _sentisTracker.ApplyExternalAnchor(
                    anchorRegion,
                    anchorRoll,
                    !_sentisTracker.HasRegion);
                _lastSentisAnchorTimestampApplied = anchorTimestamp;
            }

            long sourceHostTicks =
                _latestSentisSourceFrameHostTicks > 0L
                    ? _latestSentisSourceFrameHostTicks
                    : System.Diagnostics.Stopwatch.GetTimestamp();

            bool hasCompletedSentisResult =
                _sentisTracker.TryProcessAsync(
                    _sentisSourceTexture,
                    _sentisFlipHorizontally,
                    _sentisFlipVertically,
                    sourceHostTicks,
                    hasFreshSentisSource,
                    out bool scheduledSentisSource,
                    out Vector3[] landmarks,
                    out Quaternion geometricRotation,
                    out long completedSourceHostTicks);

            if (scheduledSentisSource)
            {
                _lastSentisProcessedGeneration =
                    _freshWebCamGeneration;
            }

            if (hasCompletedSentisResult)
            {
                _latestSentisLatencyMs = _sentisTracker.LatestLatencyMs;
                _latestSentisPresence = _sentisTracker.LatestPresence;

                if (
                    !_hasSentisRotationOffset &&
                    _hasLatestMediaPipeAuxRotation &&
                    IsValidQuaternion(geometricRotation)
                )
                {
                    _sentisRotationOffset =
                        _latestMediaPipeAuxRotation *
                        Quaternion.Inverse(geometricRotation);
                    _sentisRotationOffset = NormalizeQuaternion(
                        _sentisRotationOffset);
                    _hasSentisRotationOffset = true;
                }

                Quaternion rotation = _hasSentisRotationOffset
                    ? _sentisRotationOffset * geometricRotation
                    : geometricRotation;

                bool sentisAcceptedForPublish = StoreSentisTrackingData(
                    landmarks,
                    NormalizeQuaternion(rotation),
                    GetCurrentTimestampMillisec(),
                    completedSourceHostTicks > 0L
                        ? completedSourceHostTicks
                        : sourceHostTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp());

                if (sentisAcceptedForPublish)
                {
                    _sentisPublishFailureStreak = 0;
                    _sentisPrimaryActive = true;
                }
                else if (_sentisPrimaryActive)
                {
                    _sentisPublishFailureStreak++;

                    if (
                        _sentisPublishFailureStreak >=
                        SentisPublishFailureGraceFrames)
                    {
                        _sentisPrimaryActive = false;
                        _hasSentisRotationOffset = false;
                        _sentisPublishFailureStreak = 0;
                    }
                }
            }
            else if (
                !_sentisTracker.IsAsyncReadbackPending &&
                !_sentisTracker.IsTracking)
            {
                _sentisPrimaryActive = false;
                _hasSentisRotationOffset = false;
            }
        }


        private long _lastSentisAnchorTimestampApplied = -1L;

        // KIWI_INFERENCE_BACKEND_HYSTERESIS_V2_7
        private int _sentisPublishFailureStreak;
        private const int SentisPublishFailureGraceFrames = 3;


        private void InitializeSentisTracker(
            Texture source,
            bool flipHorizontally,
            bool flipVertically)
        {
            DisposeSentisTracker();

            if (!enableSentisHybridTracking)
            {
                return;
            }

            if (!SystemInfo.supportsComputeShaders)
            {
                Debug.LogWarning(
                    "[KiwiInference] Compute shaders are unavailable; using MediaPipe fallback.");
                return;
            }

            ModelAsset model = Resources.Load<ModelAsset>(
                "KiwiFaceLandmarkInference");
            Shader cropShader = Resources.Load<Shader>(
                "KiwiInferenceFaceCrop");

            if (model == null || cropShader == null)
            {
                Debug.LogWarning(
                    "[KiwiInference] Required asset is missing; model=" +
                    (model != null) + ", shader=" + (cropShader != null) +
                    ". Using MediaPipe fallback.");
                return;
            }

            try
            {
                _sentisTracker = new KiwiInferenceFaceTracker(
                    model,
                    cropShader)
                {
                    MinimumPresence = sentisMinimumPresence
                };
                _sentisSourceTexture = source;
                _sentisFlipHorizontally = flipHorizontally;
                _sentisFlipVertically = flipVertically;
                _lastSentisProcessedGeneration = -1;
                _lastSentisAnchorTimestampApplied = -1L;
                _sentisPrimaryActive = false;
                _hasSentisRotationOffset = false;
                _lastMediaPipeRefreshHostTicks = 0L;
                Debug.Log(
                    "[KiwiInference] Hybrid GPU single-readback landmark path " +
                    "initialized on " + SystemInfo.graphicsDeviceType + ".");
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning(
                    "[KiwiInference] Initialization failed; MediaPipe fallback remains active. " +
                    exception.Message);
                DisposeSentisTracker();
            }
        }


        private void DisposeSentisTracker()
        {
            _sentisPrimaryActive = false;
            _sentisTracker?.Dispose();
            _sentisTracker = null;
            _sentisSourceTexture = null;
            _lastSentisProcessedGeneration = -1;
            _lastSentisAnchorTimestampApplied = -1L;
            _hasSentisRotationOffset = false;
            _latestSentisLatencyMs = 0f;
            _latestSentisPresence = 0f;
        }


        private bool ShouldThrottleMediaPipeRefresh(long nowHostTicks)
        {
            if (!_sentisPrimaryActive)
            {
                return false;
            }

            float rate = Mathf.Clamp(
                sentisMediaPipeRefreshRateHz,
                2f,
                30f);
            long minimumTicks = (long)(
                System.Diagnostics.Stopwatch.Frequency / rate);

            if (
                _lastMediaPipeRefreshHostTicks > 0L &&
                nowHostTicks - _lastMediaPipeRefreshHostTicks < minimumTicks)
            {
                return true;
            }

            _lastMediaPipeRefreshHostTicks = nowHostTicks;
            return false;
        }


        private void ObserveFreshWebCamFrame(WebCamTexture webCamTexture)
        {
            if (
                !_acceptTrackingResults ||
                webCamTexture == null ||
                !webCamTexture.didUpdateThisFrame ||
                _lastObservedWebCamUnityFrame == Time.frameCount
            )
            {
                return;
            }

            long hostTicks =
                System.Diagnostics.Stopwatch.GetTimestamp();

            _lastObservedWebCamUnityFrame = Time.frameCount;
            _freshWebCamGeneration++;
            _pendingFreshWebCamFrame = true;
            _pendingSourceFrameHostTicks = hostTicks;
            _latestSentisSourceFrameHostTicks = hostTicks;
            RecordFreshSourceFrame(hostTicks);
        }

        public bool TryGetLatestLandmarks(
            ref Vector2[] destination,
            out int count,
            out long timestamp)
        {
            return TryGetLatestLandmarksIfChanged(
                ref destination,
                long.MinValue,
                out count,
                out timestamp,
                out _
            );
        }


        // Rendering can run much faster than Face LandMarker inference. Consumers
        // that already processed the current timestamp use this overload to avoid
        // copying all 478 landmarks again on unchanged render frames.
        public bool TryGetLatestLandmarksIfChanged(
            ref Vector2[] destination,
            long previousTimestamp,
            out int count,
            out long timestamp,
            out bool hasFace)
        {
            lock (_trackingLock)
            {
                count =
                    _latestLandmarkCount;


                timestamp =
                    _latestLandmarkTimestamp;


                hasFace =
                    count > 0 &&
                    _latestLandmarks != null;


                if (!hasFace)
                {
                    return false;
                }


                if (timestamp == previousTimestamp)
                {
                    return false;
                }


                if (
                    destination == null ||
                    destination.Length < count
                )
                {
                    destination =
                        new Vector2[count];
                }


                System.Array.Copy(
                    _latestLandmarks,
                    destination,
                    count
                );


                return true;
            }
        }


        // =====================================================
        // Public Precision Snapshot Access
        //
        // Motion data + landmark array are copied while holding one lock,
        // so consumers never combine different FaceLandmarker timestamps.
        // =====================================================

        public bool TryGetLatestPrecisionTrackingData(
            ref Vector2[] destination,
            out int count,
            out FacePrecisionTrackingData data)
        {
            lock (_trackingLock)
            {
                count =
                    _latestLandmarkCount;


                data =
                    _latestPrecisionData;


                if (
                    !data.isValid ||
                    count <= 362 ||
                    _latestLandmarks == null
                )
                {
                    return false;
                }


                if (
                    destination == null ||
                    destination.Length < count
                )
                {
                    destination =
                        new Vector2[count];
                }


                System.Array.Copy(
                    _latestLandmarks,
                    destination,
                    count
                );


                return true;
            }
        }


        // Precision motion consumers that only need the coherent geometry snapshot
        // should use this overload. It avoids copying the complete landmark array
        // while still reading the snapshot atomically under the tracking lock.
        public bool TryGetLatestPrecisionTrackingData(
            out FacePrecisionTrackingData data)
        {
            lock (_trackingLock)
            {
                data =
                    _latestPrecisionData;


                return
                    data.isValid &&
                    _latestLandmarkCount > 362;
            }
        }


        // =====================================================
        // Public Motion Access
        // =====================================================

        public bool TryGetLatestMotionData(
            out Vector2 faceCenter,
            out float eyeSpan,
            out Quaternion faceRotation,
            out long timestamp)
        {
            lock (_trackingLock)
            {
                faceCenter =
                    _latestFaceCenter;


                eyeSpan =
                    _latestFaceEyeSpan;


                faceRotation =
                    _latestFaceRotation;


                timestamp =
                    _latestMotionTimestamp;


                return
                    _latestLandmarkCount > 362
                    &&
                    _latestFaceEyeSpan > 0.0001f
                    &&
                    _hasLatestFaceRotation
                    &&
                    _latestMotionTimestamp >= 0;
            }
        }


        // =====================================================
        // Public Expression Access
        // =====================================================

        public bool TryGetLatestExpressionData(
            out FaceExpressionData data,
            out long timestamp)
        {
            lock (_trackingLock)
            {
                data =
                    _latestExpressionData;


                timestamp =
                    _latestExpressionTimestamp;


                return
                    data.isValid
                    &&
                    timestamp >= 0;
            }
        }


        // =====================================================
        // Stop
        // =====================================================

        public override void Stop()
        {
            // Close the publish gate before asking the base runner to stop so a
            // callback already in flight cannot republish tracking after teardown.
            _acceptTrackingResults = false;
            _liveStreamRequestInFlight = false;

            // Wait for any callback that was already inside the final publish
            // section. New callbacks see the closed volatile gate and cannot enter.
            lock (_callbackLifecycleLock)
            {
            }

            DisposeSentisTracker();

            base.Stop();


            _textureFramePool?.Dispose();

            _textureFramePool =
                null;


            ReleaseTrackingInputTexture();


            ClearTrackingData(
                true
            );
        }


        // =====================================================
        // Run
        // =====================================================

        protected override IEnumerator Run()
        {
            ClearTrackingData(true);
            _acceptTrackingResults = true;
            _liveStreamRequestInFlight = false;
            _pendingFreshWebCamFrame = false;
            _pendingSourceFrameHostTicks = 0L;
            _freshWebCamGeneration = 0;
            _lastObservedWebCamUnityFrame = -1;
            _observedWebCamTexture = null;

            Debug.Log(
                $"Delegate = {config.Delegate}"
            );

            Debug.Log(
                $"Image Read Mode = {config.ImageReadMode}"
            );

            Debug.Log(
                $"Running Mode = {config.RunningMode}"
            );

            Debug.Log(
                $"NumFaces = {config.NumFaces}"
            );

            Debug.Log(
                $"MinFaceDetectionConfidence = {config.MinFaceDetectionConfidence}"
            );

            Debug.Log(
                $"MinFacePresenceConfidence = {config.MinFacePresenceConfidence}"
            );

            Debug.Log(
                $"MinTrackingConfidence = {config.MinTrackingConfidence}"
            );

            Debug.Log(
                $"OutputFaceBlendshapes = {config.OutputFaceBlendshapes}"
            );

            Debug.Log(
                $"OutputFacialTransformationMatrixes = {config.OutputFacialTransformationMatrixes}"
            );


            yield return
                AssetLoader.PrepareAssetAsync(
                    config.ModelPath
                );


            var options =
                config.GetFaceLandmarkerOptions(
                    config.RunningMode ==
                    Tasks.Vision.Core.RunningMode.LIVE_STREAM
                        ?
                        OnFaceLandmarkDetectionOutput
                        :
                        null
                );


            taskApi =
                FaceLandmarker.CreateFromOptions(
                    options,
                    GpuManager.GpuResources
                );


            var imageSource =
                ImageSourceProvider.ImageSource;


            yield return
                imageSource.Play();


            if (!imageSource.isPrepared)
            {
                Debug.LogError(
                    "Failed to start ImageSource, exiting..."
                );

                yield break;
            }


            _observedWebCamTexture =
                imageSource.GetCurrentTexture() as WebCamTexture;


            _sourceName =
                imageSource.sourceName ?? string.Empty;


            _sourceRequestedFrameRate =
                (float)imageSource.resolution.frameRate;


            _cm831ProfileActive =
                WebCamSource.IsCm831DeviceName(_sourceName);


            if (
                autoOptimizeCm831 &&
                _cm831ProfileActive
            )
            {
                trackingInputMaxWidth =
                    Mathf.Clamp(
                        cm831TrackingInputWidth,
                        480,
                        960
                    );
            }


            _sourceTextureWidth = Mathf.Max(1, imageSource.textureWidth);
            _sourceTextureHeight = Mathf.Max(1, imageSource.textureHeight);


            Debug.Log(
                "[KiwiCamera] source=" + _sourceName +
                " actual=" + _sourceTextureWidth + "x" + _sourceTextureHeight +
                " requested=" + imageSource.resolution +
                " cm831=" + _cm831ProfileActive +
                " trackingWidth=" + trackingInputMaxWidth
            );

            PrepareTrackingInputTexture(
                _sourceTextureWidth,
                _sourceTextureHeight
            );


            int textureFramePoolSize =
                config.ImageReadMode == ImageReadMode.GPU
                    ? 4
                    : 2;


            _textureFramePool =
                new Experimental.TextureFramePool(
                    _trackingInputWidth,
                    _trackingInputHeight,
                    TextureFormat.RGBA32,
                    textureFramePoolSize
                );


            screen.Initialize(
                imageSource
            );


            SetupAnnotationController(
                _faceLandmarkerResultAnnotationController,
                imageSource
            );

            if (!renderDebugLandmarkAnnotations)
            {
                _faceLandmarkerResultAnnotationController.DrawNow(default);
            }


            var transformationOptions =
                imageSource.GetTransformationOptions();


            var flipHorizontally =
                transformationOptions.flipHorizontally;


            IsInputHorizontallyMirrored =
                flipHorizontally;


            var flipVertically =
                transformationOptions.flipVertically;


            var imageProcessingOptions =
                new Tasks.Vision.Core.ImageProcessingOptions(
                    rotationDegrees:
                    (int)transformationOptions.rotationAngle
                );


            InitializeSentisTracker(
                imageSource.GetCurrentTexture(),
                flipHorizontally,
                flipVertically
            );

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (_sentisTracker != null)
            {
                // In the hybrid path MediaPipe is only a low-rate auxiliary
                // tracker. Async readback prevents its periodic refresh from
                // stalling the high-rate Inference Engine/render loop on DX11.
                config.ImageReadMode = ImageReadMode.CPUAsync;
            }
#endif


            AsyncGPUReadbackRequest req =
                default;


            var waitUntilReqDone =
                new WaitUntil(
                    () => req.done
                );


            var waitForEndOfFrame =
                new WaitForEndOfFrame();


            var result =
                FaceLandmarkerResult.Alloc(
                    options.numFaces
                );


            var canUseGpuImage =
                SystemInfo.graphicsDeviceType ==
                GraphicsDeviceType.OpenGLES3
                &&
                GpuManager.GpuResources != null;


            using var glContext =
                canUseGpuImage
                    ?
                    GpuManager.GetGlContext()
                    :
                    null;


            while (true)
            {
                if (isPaused)
                {
                    yield return
                        new WaitWhile(
                            () => isPaused
                        );
                }


                bool isLiveStream =
                    taskApi.runningMode ==
                    Tasks.Vision.Core.RunningMode.LIVE_STREAM;

                Texture sourceTexture =
                    imageSource.GetCurrentTexture();


                long sourceObservationHostTicks =
                    System.Diagnostics.Stopwatch.GetTimestamp();


                WebCamTexture webCamTexture =
                    sourceTexture as WebCamTexture;


                bool isFreshWebCamFrame =
                    webCamTexture != null &&
                    webCamTexture.didUpdateThisFrame;


                if (isFreshWebCamFrame)
                {
                    ObserveFreshWebCamFrame(webCamTexture);
                }


                if (
                    isLiveStream &&
                    ShouldThrottleMediaPipeRefresh(
                        sourceObservationHostTicks)
                )
                {
                    yield return null;
                    continue;
                }


                // Observe and remember camera updates before checking the
                // optional in-flight gate. The old order discarded every update
                // that arrived while inference was active, reducing a 30 fps
                // camera to roughly 6-8 result Hz in the supplied recording.
                if (
                    isLiveStream &&
                    latestFrameOnlyLiveStream &&
                    _liveStreamRequestInFlight
                )
                {
                    yield return null;

                    continue;
                }


                if (
                    isLiveStream &&
                    processOnlyFreshWebCamFrames &&
                    sourceTexture is WebCamTexture &&
                    !_pendingFreshWebCamFrame
                )
                {
                    yield return null;

                    continue;
                }


                // Latch timing before GPU readback. The landmark result describes
                // this source frame, not the later moment DetectAsync is called.
                long sourceFrameHostTicks =
                    _pendingFreshWebCamFrame &&
                    _pendingSourceFrameHostTicks > 0L
                        ? _pendingSourceFrameHostTicks
                        : sourceObservationHostTicks;


                int submittedFreshGeneration =
                    _freshWebCamGeneration;


                if (
                    !_textureFramePool
                        .TryGetTextureFrame(
                            out var textureFrame
                        )
                )
                {
                    yield return null;

                    continue;
                }


                Image image;


                long readbackStartHostTicks =
                    System.Diagnostics.Stopwatch.GetTimestamp();


                Texture trackingSourceTexture =
                    sourceTexture;


                if (_trackingInputTexture != null)
                {
                    Graphics.Blit(
                        sourceTexture,
                        _trackingInputTexture
                    );

                    trackingSourceTexture =
                        _trackingInputTexture;
                }


                switch (config.ImageReadMode)
                {
                    case ImageReadMode.GPU:
                        {
                            if (!canUseGpuImage)
                            {
                                throw new System.Exception(
                                    "ImageReadMode.GPU is not supported"
                                );
                            }


                            textureFrame.ReadTextureOnGPU(
                                trackingSourceTexture,
                                flipHorizontally,
                                flipVertically
                            );


                            image =
                                textureFrame.BuildGPUImage(
                                    glContext
                                );


                            yield return
                                waitForEndOfFrame;

                            break;
                        }


                    case ImageReadMode.CPU:
                        {
                            // A persistent downscaled RenderTexture is already ordered by
                            // Graphics.Blit. ReadPixels performs the required GPU sync, so
                            // waiting for EndOfFrame here only adds up to one display frame.
                            if (_trackingInputTexture == null)
                            {
                                yield return
                                    waitForEndOfFrame;
                            }


                            textureFrame.ReadTextureOnCPU(
                                trackingSourceTexture,
                                flipHorizontally,
                                flipVertically
                            );


                            image =
                                textureFrame.BuildCPUImage();


                            textureFrame.Release();

                            break;
                        }


                    case ImageReadMode.CPUAsync:

                    default:
                        {
                            req =
                                textureFrame.ReadTextureAsync(
                                    trackingSourceTexture,
                                    flipHorizontally,
                                    flipVertically
                                );


                            yield return
                                waitUntilReqDone;


                            if (req.hasError)
                            {
                                textureFrame.Release();


                                Debug.LogWarning(
                                    "Failed to read texture from the image source"
                                );


                                continue;
                            }


                            image =
                                textureFrame.BuildCPUImage();


                            textureFrame.Release();

                            break;
                        }
                }


                RecordReadbackLatency(
                    readbackStartHostTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp()
                );


                switch (taskApi.runningMode)
                {
                    case Tasks.Vision.Core.RunningMode.IMAGE:
                        {
                            if (
                                taskApi.TryDetect(
                                    image,
                                    imageProcessingOptions,
                                    ref result
                                )
                            )
                            {
                                StoreTrackingData(
                                    result,
                                    GetCurrentTimestampMillisec(),
                                    0L,
                                    System.Diagnostics.Stopwatch.GetTimestamp()
                                );


                                if (renderDebugLandmarkAnnotations)
                                {
                                    _faceLandmarkerResultAnnotationController
                                        .DrawNow(
                                            result
                                        );
                                }
                            }
                            else
                            {
                                ClearTrackingData();


                                if (renderDebugLandmarkAnnotations)
                                {
                                    _faceLandmarkerResultAnnotationController
                                        .DrawNow(
                                            default
                                        );
                                }
                            }

                            break;
                        }


                    case Tasks.Vision.Core.RunningMode.VIDEO:
                        {
                            long timestamp =
                                GetCurrentTimestampMillisec();


                            if (
                                taskApi.TryDetectForVideo(
                                    image,
                                    timestamp,
                                    imageProcessingOptions,
                                    ref result
                                )
                            )
                            {
                                StoreTrackingData(
                                    result,
                                    timestamp,
                                    0L,
                                    System.Diagnostics.Stopwatch.GetTimestamp()
                                );


                                if (renderDebugLandmarkAnnotations)
                                {
                                    _faceLandmarkerResultAnnotationController
                                        .DrawNow(
                                            result
                                        );
                                }
                            }
                            else
                            {
                                ClearTrackingData();


                                if (renderDebugLandmarkAnnotations)
                                {
                                    _faceLandmarkerResultAnnotationController
                                        .DrawNow(
                                            default
                                        );
                                }
                            }

                            break;
                        }


                    case Tasks.Vision.Core.RunningMode.LIVE_STREAM:
                        {
                            long timestamp =
                                GetCurrentTimestampMillisec();


                            long submissionHostTicks =
                                sourceFrameHostTicks;


                            RememberSubmittedFrame(
                                timestamp,
                                submissionHostTicks
                            );


                            RecordSubmittedFrame(
                                System.Diagnostics.Stopwatch.GetTimestamp()
                            );


                            // Preserve a newer camera update observed by Update
                            // while this submission awaited GPU readback.
                            if (_freshWebCamGeneration == submittedFreshGeneration)
                            {
                                _pendingFreshWebCamFrame = false;
                                _pendingSourceFrameHostTicks = 0L;
                            }

                            if (latestFrameOnlyLiveStream)
                            {
                                _liveStreamRequestInFlight = true;
                            }


                            try
                            {
                                taskApi.DetectAsync(
                                    image,
                                    timestamp,
                                    imageProcessingOptions
                                );
                            }
                            catch
                            {
                                _liveStreamRequestInFlight = false;
                                throw;
                            }

                            break;
                        }
                }
            }
        }


        // =====================================================
        // Live Stream Callback
        // =====================================================

        private void PrepareTrackingInputTexture(
            int sourceWidth,
            int sourceHeight)
        {
            ReleaseTrackingInputTexture();

            _trackingInputWidth = Mathf.Max(1, sourceWidth);
            _trackingInputHeight = Mathf.Max(1, sourceHeight);

            int maximumWidth =
                Mathf.Clamp(trackingInputMaxWidth, 320, 1920);

            if (
                !downscaleTrackingInput ||
                _trackingInputWidth <= maximumWidth
            )
            {
                return;
            }

            float scale =
                maximumWidth / (float)_trackingInputWidth;

            _trackingInputWidth = maximumWidth;
            _trackingInputHeight = Mathf.Max(
                2,
                Mathf.RoundToInt(sourceHeight * scale)
            );

            // Even dimensions avoid driver-specific chroma/readback alignment
            // penalties while retaining the camera's exact aspect ratio.
            _trackingInputHeight =
                (_trackingInputHeight + 1) & ~1;

            _trackingInputTexture =
                new RenderTexture(
                    _trackingInputWidth,
                    _trackingInputHeight,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default
                )
                {
                    name = "Kiwi LandMarker Input",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false,
                    hideFlags = HideFlags.DontSave
                };

            _trackingInputTexture.Create();
        }


        private void ReleaseTrackingInputTexture()
        {
            if (_trackingInputTexture != null)
            {
                if (_trackingInputTexture.IsCreated())
                {
                    _trackingInputTexture.Release();
                }

                if (Application.isPlaying)
                {
                    Destroy(_trackingInputTexture);
                }
                else
                {
                    DestroyImmediate(_trackingInputTexture);
                }

                _trackingInputTexture = null;
            }

            _trackingInputWidth = 0;
            _trackingInputHeight = 0;
        }

        private void RecordFreshSourceFrame(long hostTicks)
        {
            UpdateMeasuredRate(
                ref _previousFreshSourceHostTicks,
                ref _latestFreshSourceRateHz,
                hostTicks
            );
        }


        private void RecordSubmittedFrame(long hostTicks)
        {
            UpdateMeasuredRate(
                ref _previousSubmissionRateHostTicks,
                ref _latestSubmissionRateHz,
                hostTicks
            );
        }


        private static void UpdateMeasuredRate(
            ref long previousHostTicks,
            ref float smoothedRateHz,
            long hostTicks)
        {
            if (previousHostTicks > 0L && hostTicks > previousHostTicks)
            {
                double seconds =
                    (hostTicks - previousHostTicks) /
                    (double)System.Diagnostics.Stopwatch.Frequency;

                if (seconds > 0.0001 && seconds < 2.0)
                {
                    float instantaneousRate =
                        Mathf.Clamp((float)(1.0 / seconds), 0f, 240f);

                    smoothedRateHz =
                        smoothedRateHz > 0f
                            ? Mathf.Lerp(smoothedRateHz, instantaneousRate, 0.20f)
                            : instantaneousRate;
                }
            }

            previousHostTicks = hostTicks;
        }


        private void RecordReadbackLatency(long startHostTicks, long endHostTicks)
        {
            if (startHostTicks <= 0L || endHostTicks <= startHostTicks)
            {
                return;
            }

            float milliseconds =
                (float)(
                    (endHostTicks - startHostTicks) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency
                );

            _latestReadbackLatencyMs =
                _latestReadbackLatencyMs > 0f
                    ? Mathf.Lerp(_latestReadbackLatencyMs, milliseconds, 0.20f)
                    : milliseconds;
        }

        private void OnFaceLandmarkDetectionOutput(
            FaceLandmarkerResult result,
            Image image,
            long timestamp)
        {
            // Capture callback entry immediately. This is intentionally taken
            // before geometry/expression extraction so arrival timing is not
            // inflated by our own C# post-processing work.
            long arrivalHostTicks =
                System.Diagnostics.Stopwatch.GetTimestamp();


            // Release the newest-frame gate immediately on callback entry. C#
            // geometry extraction can overlap selection of the next camera frame.
            _liveStreamRequestInFlight = false;


            if (!_acceptTrackingResults)
            {
                return;
            }


            long submissionHostTicks =
                ResolveSubmittedFrameHostTicks(
                    timestamp
                );


            // Every legitimate callback from the current LIVE_STREAM run must map
            // to a frame remembered by this run. An unmatched callback is either
            // older than the bounded timing history or belongs to a previous run;
            // in both cases publishing it would be more harmful than dropping it.
            if (submissionHostTicks <= 0L)
            {
                return;
            }


            lock (_callbackLifecycleLock)
            {
                if (!_acceptTrackingResults)
                {
                    return;
                }


                bool published =
                    StoreTrackingData(
                        result,
                        timestamp,
                        submissionHostTicks,
                        arrivalHostTicks
                    );


                // 必ず残す。
                // ただし、より新しい結果を既に処理済みの stale callback や
                // Stop 後の callback は描画キューへ戻さない。
                if (published && _acceptTrackingResults && renderDebugLandmarkAnnotations)
                {
                    _faceLandmarkerResultAnnotationController
                        .DrawLater(
                            result
                        );
                }
            }
        }


        // =====================================================
        // Store Tracking
        // =====================================================

        private bool StoreTrackingData(
            FaceLandmarkerResult result,
            long timestamp,
            long submissionHostTicks,
            long arrivalHostTicks = 0L)
        {
            if (!_acceptTrackingResults)
            {
                return false;
            }


            if (
                result.faceLandmarks == null
                ||
                result.faceLandmarks.Count == 0
                ||
                result.faceLandmarks[0].landmarks == null
                ||
                result.faceLandmarks[0].landmarks.Count == 0
            )
            {
                return ClearTrackingDataForTimestamp(
                    timestamp
                );
            }


            var landmarks =
                result.faceLandmarks[0].landmarks;


            int count =
                landmarks.Count;


            Vector2 center =
                new Vector2(
                    0.5f,
                    0.5f
                );


            float eyeSpan =
                0f;


            float eyeSpan3D =
                0f;


            Vector2 rightEyeCenter =
                Vector2.zero;


            Vector2 leftEyeCenter =
                Vector2.zero;


            Vector2 eyeCenter =
                Vector2.zero;


            Vector2 chin =
                Vector2.zero;


            Vector2 nose =
                Vector2.zero;


            Vector2 cheekCenter =
                Vector2.zero;


            Vector2 forehead =
                Vector2.zero;


            float faceWidth2D =
                0f;


            float faceHeight2D =
                0f;


            float geometryQuality =
                0f;


            if (count > 362)
            {
                Vector3 rightEyeCenter3D =
                    (
                        new Vector3(
                            landmarks[33].x,
                            landmarks[33].y,
                            landmarks[33].z
                        )
                        +
                        new Vector3(
                            landmarks[133].x,
                            landmarks[133].y,
                            landmarks[133].z
                        )
                    )
                    *
                    0.5f;


                Vector3 leftEyeCenter3D =
                    (
                        new Vector3(
                            landmarks[362].x,
                            landmarks[362].y,
                            landmarks[362].z
                        )
                        +
                        new Vector3(
                            landmarks[263].x,
                            landmarks[263].y,
                            landmarks[263].z
                        )
                    )
                    *
                    0.5f;


                rightEyeCenter =
                    new Vector2(
                        rightEyeCenter3D.x,
                        rightEyeCenter3D.y
                    );


                leftEyeCenter =
                    new Vector2(
                        leftEyeCenter3D.x,
                        leftEyeCenter3D.y
                    );


                eyeCenter =
                    (
                        rightEyeCenter +
                        leftEyeCenter
                    )
                    *
                    0.5f;


                eyeSpan =
                    Vector2.Distance(
                        rightEyeCenter,
                        leftEyeCenter
                    );


                eyeSpan3D =
                    Vector3.Distance(
                        rightEyeCenter3D,
                        leftEyeCenter3D
                    );


                if (count > 152)
                {
                    chin =
                        new Vector2(
                            landmarks[152].x,
                            landmarks[152].y
                        );


                    const float neckExtension =
                        1.30f;


                    center =
                        eyeCenter
                        +
                        (
                            chin -
                            eyeCenter
                        )
                        *
                        neckExtension;
                }
                else
                {
                    center =
                        eyeCenter;
                }


                if (count > 454)
                {
                    Vector2 leftCheek =
                        new Vector2(
                            landmarks[234].x,
                            landmarks[234].y
                        );


                    Vector2 rightCheek =
                        new Vector2(
                            landmarks[454].x,
                            landmarks[454].y
                        );


                    cheekCenter =
                        (
                            leftCheek +
                            rightCheek
                        )
                        *
                        0.5f;


                    faceWidth2D =
                        Vector2.Distance(
                            leftCheek,
                            rightCheek
                        );


                    nose =
                        new Vector2(
                            landmarks[1].x,
                            landmarks[1].y
                        );


                    forehead =
                        new Vector2(
                            landmarks[10].x,
                            landmarks[10].y
                        );


                    faceHeight2D =
                        Vector2.Distance(
                            forehead,
                            chin
                        );


                    geometryQuality =
                        KiwiPrecisionTrackingMath.CalculateGeometryQuality(
                            eyeSpan,
                            faceWidth2D,
                            faceHeight2D
                        );
                }
                else
                {
                    geometryQuality =
                        KiwiPrecisionTrackingMath.CalculateGeometryQuality(
                            eyeSpan,
                            eyeSpan * 2.2f,
                            eyeSpan * 2.7f
                        )
                        *
                        0.75f;
                }
            }

            Quaternion rotation =
                Quaternion.identity;


            bool hasRotation =
                false;


            if (
                result.facialTransformationMatrixes != null
                &&
                result.facialTransformationMatrixes.Count > 0
            )
            {
                Matrix4x4 matrix =
                    result.facialTransformationMatrixes[0];


                rotation =
                    matrix.rotation;


                if (
                    IsValidQuaternion(
                        rotation
                    )
                )
                {
                    rotation =
                        NormalizeQuaternion(
                            rotation
                        );


                    hasRotation =
                        true;
                }
            }


            FaceExpressionData expression =
                ExtractExpressionData(
                    result
                );


            // KIWI_MEDIAPIPE_ROI_PARITY_V3_5
            // Match MediaPipe FaceLandmarkLandmarksToRoi:
            // full landmark bounds -> 33/263 roll -> 1.5x pixel-square long side.
            UnityEngine.Rect sentisAnchorRegion =
                default;

            float sentisAnchorRollRadians =
                0f;

            bool hasSentisAnchor =
                count >
                    454 &&
                _sourceTextureWidth >
                    0 &&
                _sourceTextureHeight >
                    0;

            if (hasSentisAnchor)
            {
                float minX =
                    float.PositiveInfinity;

                float minY =
                    float.PositiveInfinity;

                float maxX =
                    float.NegativeInfinity;

                float maxY =
                    float.NegativeInfinity;

                for (
                    int i = 0;
                    i < count;
                    i++
                )
                {
                    float x =
                        landmarks[i].x;

                    float y =
                        landmarks[i].y;

                    if (
                        float.IsNaN(x) ||
                        float.IsInfinity(x) ||
                        float.IsNaN(y) ||
                        float.IsInfinity(y)
                    )
                    {
                        hasSentisAnchor =
                            false;

                        break;
                    }

                    minX =
                        Mathf.Min(
                            minX,
                            x);

                    minY =
                        Mathf.Min(
                            minY,
                            y);

                    maxX =
                        Mathf.Max(
                            maxX,
                            x);

                    maxY =
                        Mathf.Max(
                            maxY,
                            y);
                }

                if (hasSentisAnchor)
                {
                    float imageWidth =
                        Mathf.Max(
                            1f,
                            _sourceTextureWidth);

                    float imageHeight =
                        Mathf.Max(
                            1f,
                            _sourceTextureHeight);

                    float boxWidthPixels =
                        (
                            maxX -
                            minX
                        ) *
                        imageWidth;

                    float boxHeightPixels =
                        (
                            maxY -
                            minY
                        ) *
                        imageHeight;

                    float squareSidePixels =
                        Mathf.Max(
                            boxWidthPixels,
                            boxHeightPixels) *
                        1.50f;

                    if (
                        squareSidePixels <=
                            1f
                    )
                    {
                        hasSentisAnchor =
                            false;
                    }
                    else
                    {
                        float anchorWidth =
                            Mathf.Clamp(
                                squareSidePixels /
                                imageWidth,
                                0.04f,
                                2.50f);

                        float anchorHeight =
                            Mathf.Clamp(
                                squareSidePixels /
                                imageHeight,
                                0.04f,
                                2.50f);

                        Vector2 anchorCenter =
                            new Vector2(
                                (
                                    minX +
                                    maxX
                                ) *
                                0.5f,
                                (
                                    minY +
                                    maxY
                                ) *
                                0.5f);

                        sentisAnchorRegion =
                            new UnityEngine.Rect(
                                anchorCenter.x -
                                    anchorWidth *
                                    0.5f,
                                anchorCenter.y -
                                    anchorHeight *
                                    0.5f,
                                anchorWidth,
                                anchorHeight);

                        float eyeDxPixels =
                            (
                                landmarks[263].x -
                                landmarks[33].x
                            ) *
                            imageWidth;

                        float eyeDyPixels =
                            (
                                landmarks[263].y -
                                landmarks[33].y
                            ) *
                            imageHeight;

                        if (
                            eyeDxPixels *
                                eyeDxPixels +
                            eyeDyPixels *
                                eyeDyPixels <=
                                0.000001f
                        )
                        {
                            hasSentisAnchor =
                                false;
                        }
                        else
                        {
                            // Runner landmarks use top-left Y; the tracker crop
                            // transform uses bottom-left Y.
                            sentisAnchorRollRadians =
                                -Mathf.Atan2(
                                    eyeDyPixels,
                                    eyeDxPixels);
                        }
                    }
                }
            }


            if (arrivalHostTicks <= 0L)
            {
                arrivalHostTicks =
                    System.Diagnostics.Stopwatch.GetTimestamp();
            }


            bool hasMatchedSubmissionTiming =
                submissionHostTicks > 0L;


            FacePrecisionTrackingData precisionData =
                new FacePrecisionTrackingData
                {
                    isValid =
                        count > 362 &&
                        eyeSpan > 0.0001f &&
                        hasRotation,

                    faceCenter = center,
                    eyeSpan2D = eyeSpan,
                    eyeSpan3D = eyeSpan3D,
                    faceRotation = rotation,

                    rightEyeCenter = rightEyeCenter,
                    leftEyeCenter = leftEyeCenter,
                    eyeCenter = eyeCenter,
                    chin = chin,
                    nose = nose,
                    cheekCenter = cheekCenter,
                    forehead = forehead,

                    faceWidth2D = faceWidth2D,
                    faceHeight2D = faceHeight2D,
                    geometryQuality = geometryQuality,

                    timestamp = timestamp,
                    submissionHostTicks = submissionHostTicks,
                    arrivalHostTicks = arrivalHostTicks,
                    hasMatchedSubmissionTiming = hasMatchedSubmissionTiming
                };


            lock (_storeTrackingLock)
            {
                if (
                    _stagingLandmarks == null ||
                    _stagingLandmarks.Length < count
                )
                {
                    _stagingLandmarks =
                        new Vector2[count];
                }


                for (
                    int i = 0;
                    i < count;
                    i++
                )
                {
                    _stagingLandmarks[i] =
                        new Vector2(
                            landmarks[i].x,
                            landmarks[i].y
                        );
                }


                lock (_trackingLock)
                {
                    // Never let an older asynchronous callback overwrite a newer
                    // tracking snapshot that has already been published.
                    if (
                        !_acceptTrackingResults ||
                        (
                            _latestResultTimestamp >= 0L &&
                            timestamp < _latestResultTimestamp
                        )
                    )
                    {
                        return false;
                    }


                    _latestResultTimestamp =
                        timestamp;


                    if (hasSentisAnchor)
                    {
                        _latestSentisAnchorRegion = sentisAnchorRegion;
                        _latestSentisAnchorRollRadians =
                            sentisAnchorRollRadians;
                        _lastSentisAnchorTimestamp = timestamp;
                        _hasLatestSentisAnchor = true;
                    }


                    if (hasRotation)
                    {
                        _latestMediaPipeAuxRotation = rotation;
                        _hasLatestMediaPipeAuxRotation = true;
                    }


                    // Once Inference Engine owns high-rate landmarks, asynchronous
                    // MediaPipe callbacks update only auxiliary data. Publishing
                    // their older image coordinates would reintroduce visible lag.
                    if (_sentisPrimaryActive)
                    {
                        if (expression.isValid)
                        {
                            _latestExpressionData = expression;
                            _latestExpressionTimestamp = timestamp;
                        }

                        return true;
                    }


                    if (
                        _previousPublishedArrivalHostTicks > 0L &&
                        arrivalHostTicks > _previousPublishedArrivalHostTicks
                    )
                    {
                        float interval = (float)KiwiPrecisionTrackingMath.HostTicksToSeconds(
                            arrivalHostTicks - _previousPublishedArrivalHostTicks
                        );
                        if (interval > 0.0001f && interval < 1f)
                        {
                            float instantaneousRate = Mathf.Clamp(1f / interval, 0f, 240f);
                            _latestTrackingResultRateHz = _latestTrackingResultRateHz > 0f
                                ? Mathf.Lerp(_latestTrackingResultRateHz, instantaneousRate, 0.20f)
                                : instantaneousRate;
                        }
                    }

                    _previousPublishedArrivalHostTicks = arrivalHostTicks;


                    Vector2[] previousLatest =
                        _latestLandmarks;


                    _latestLandmarks =
                        _stagingLandmarks;


                    _stagingLandmarks =
                        previousLatest;


                    _latestLandmarkCount =
                        count;


                    _latestLandmarkTimestamp =
                        timestamp;


                    _latestFaceCenter =
                        center;


                    _latestFaceEyeSpan =
                        eyeSpan;


                    _latestFaceRotation =
                        rotation;


                    _hasLatestFaceRotation =
                        hasRotation;


                    _latestMotionTimestamp =
                        timestamp;


                    precisionData.frameId =
                        ++_nextPublishedTrackingFrameId;


                    precisionData.backend =
                        KiwiTrackingBackend.MediaPipe;


                    _latestPrecisionData =
                        precisionData;


                    _latestExpressionData =
                        expression;


                    _latestExpressionTimestamp =
                        expression.isValid
                            ?
                            timestamp
                            :
                            -1;


                    return true;
                }
            }
        }


        private bool StoreSentisTrackingData(
            Vector3[] landmarks,
            Quaternion rotation,
            long timestamp,
            long submissionHostTicks,
            long arrivalHostTicks)
        {
            if (
                !_acceptTrackingResults ||
                landmarks == null ||
                landmarks.Length < KiwiInferenceFaceTracker.CompatibleLandmarkCount ||
                !IsValidQuaternion(rotation)
            )
            {
                return false;
            }

            const int count = KiwiInferenceFaceTracker.CompatibleLandmarkCount;
            Vector3 rightEyeCenter3D =
                (landmarks[33] + landmarks[133]) * 0.5f;
            Vector3 leftEyeCenter3D =
                (landmarks[362] + landmarks[263]) * 0.5f;
            Vector2 rightEyeCenter = new Vector2(
                rightEyeCenter3D.x,
                rightEyeCenter3D.y);
            Vector2 leftEyeCenter = new Vector2(
                leftEyeCenter3D.x,
                leftEyeCenter3D.y);
            Vector2 eyeCenter =
                (rightEyeCenter + leftEyeCenter) * 0.5f;
            Vector2 chin = new Vector2(
                landmarks[152].x,
                landmarks[152].y);
            Vector2 center =
                eyeCenter + (chin - eyeCenter) * 1.30f;
            Vector2 leftCheek = new Vector2(
                landmarks[234].x,
                landmarks[234].y);
            Vector2 rightCheek = new Vector2(
                landmarks[454].x,
                landmarks[454].y);
            Vector2 cheekCenter =
                (leftCheek + rightCheek) * 0.5f;
            Vector2 nose = new Vector2(
                landmarks[1].x,
                landmarks[1].y);
            Vector2 forehead = new Vector2(
                landmarks[10].x,
                landmarks[10].y);
            float eyeSpan = Vector2.Distance(
                rightEyeCenter,
                leftEyeCenter);
            float eyeSpan3D = Vector3.Distance(
                rightEyeCenter3D,
                leftEyeCenter3D);
            float faceWidth2D = Vector2.Distance(
                leftCheek,
                rightCheek);
            float faceHeight2D = Vector2.Distance(
                forehead,
                chin);
            float geometryQuality =
                KiwiPrecisionTrackingMath.CalculateGeometryQuality(
                    eyeSpan,
                    faceWidth2D,
                    faceHeight2D);

            // KIWI_SENTIS_CONTINUITY_ADOPTION_V2_6
            if (
                eyeSpan <= 0.0001f ||
                faceWidth2D <= 0.0001f ||
                faceHeight2D <= 0.0001f ||
                float.IsNaN(eyeSpan) ||
                float.IsInfinity(eyeSpan) ||
                float.IsNaN(faceWidth2D) ||
                float.IsInfinity(faceWidth2D) ||
                float.IsNaN(faceHeight2D) ||
                float.IsInfinity(faceHeight2D))
            {
                return false;
            }

            if (geometryQuality <= 0f)
            {
                bool coherentWithPublishedFace = false;

                lock (_trackingLock)
                {
                    if (
                        _latestLandmarkCount > 362 &&
                        _latestFaceEyeSpan > 0.0001f)
                    {
                        float spanRatio =
                            eyeSpan /
                            _latestFaceEyeSpan;

                        float centerDistance =
                            Vector2.Distance(
                                center,
                                _latestFaceCenter);

                        float allowedCenterDistance =
                            Mathf.Max(
                                0.10f,
                                _latestFaceEyeSpan * 5.0f);

                        coherentWithPublishedFace =
                            spanRatio >= 0.45f &&
                            spanRatio <= 2.20f &&
                            centerDistance <=
                                allowedCenterDistance &&
                            center.x > -0.20f &&
                            center.x < 1.20f &&
                            center.y > -0.25f &&
                            center.y < 1.35f;
                    }
                }

                if (!coherentWithPublishedFace)
                {
                    return false;
                }

                // Keep downstream quality-aware smoothing conservative.
                geometryQuality = 0.10f;
            }

            rotation = NormalizeQuaternion(rotation);
            FacePrecisionTrackingData precisionData =
                new FacePrecisionTrackingData
                {
                    isValid = true,
                    faceCenter = center,
                    eyeSpan2D = eyeSpan,
                    eyeSpan3D = eyeSpan3D,
                    faceRotation = rotation,
                    rightEyeCenter = rightEyeCenter,
                    leftEyeCenter = leftEyeCenter,
                    eyeCenter = eyeCenter,
                    chin = chin,
                    nose = nose,
                    cheekCenter = cheekCenter,
                    forehead = forehead,
                    faceWidth2D = faceWidth2D,
                    faceHeight2D = faceHeight2D,
                    geometryQuality = geometryQuality,
                    timestamp = timestamp,
                    submissionHostTicks = submissionHostTicks,
                    arrivalHostTicks = arrivalHostTicks,
                    hasMatchedSubmissionTiming = submissionHostTicks > 0L
                };

            FaceExpressionData geometryExpression =
                ExtractGeometryExpressionData(landmarks, faceWidth2D);

            lock (_storeTrackingLock)
            {
                if (
                    _stagingLandmarks == null ||
                    _stagingLandmarks.Length < count
                )
                {
                    _stagingLandmarks = new Vector2[count];
                }

                for (int i = 0; i < count; i++)
                {
                    _stagingLandmarks[i] = new Vector2(
                        landmarks[i].x,
                        landmarks[i].y);
                }

                lock (_trackingLock)
                {
                    if (!_acceptTrackingResults)
                    {
                        return false;
                    }

                    if (timestamp <= _latestLandmarkTimestamp)
                    {
                        timestamp = _latestLandmarkTimestamp + 1L;
                        precisionData.timestamp = timestamp;
                    }

                    if (
                        _previousPublishedArrivalHostTicks > 0L &&
                        arrivalHostTicks > _previousPublishedArrivalHostTicks
                    )
                    {
                        float interval = (float)
                            KiwiPrecisionTrackingMath.HostTicksToSeconds(
                                arrivalHostTicks -
                                _previousPublishedArrivalHostTicks);
                        if (interval > 0.0001f && interval < 1f)
                        {
                            float rate = Mathf.Clamp(1f / interval, 0f, 240f);
                            _latestTrackingResultRateHz =
                                _latestTrackingResultRateHz > 0f
                                    ? Mathf.Lerp(
                                        _latestTrackingResultRateHz,
                                        rate,
                                        0.20f)
                                    : rate;
                        }
                    }

                    _previousPublishedArrivalHostTicks = arrivalHostTicks;
                    Vector2[] previousLatest = _latestLandmarks;
                    _latestLandmarks = _stagingLandmarks;
                    _stagingLandmarks = previousLatest;
                    _latestLandmarkCount = count;
                    _latestLandmarkTimestamp = timestamp;
                    _latestFaceCenter = center;
                    _latestFaceEyeSpan = eyeSpan;
                    _latestFaceRotation = rotation;
                    _hasLatestFaceRotation = true;
                    _latestMotionTimestamp = timestamp;
                    precisionData.frameId =
                        ++_nextPublishedTrackingFrameId;
                    precisionData.backend =
                        KiwiTrackingBackend.InferenceEngine;
                    _latestPrecisionData = precisionData;

                    // Preserve MediaPipe's learned 52-coefficient result while it
                    // is fresh. Geometry expressions keep the API alive during
                    // startup, temporary native tracking loss and mobile fallback.
                    if (
                        !_latestExpressionData.isValid ||
                        _latestExpressionTimestamp < 0L ||
                        timestamp - _latestExpressionTimestamp > 500L
                    )
                    {
                        _latestExpressionData = geometryExpression;
                        _latestExpressionTimestamp = timestamp;
                    }

                    return true;
                }
            }
        }


        private static FaceExpressionData ExtractGeometryExpressionData(
            Vector3[] landmarks,
            float faceWidth)
        {
            float rightEyeWidth = Mathf.Max(
                0.0001f,
                Vector2.Distance(landmarks[33], landmarks[133]));
            float leftEyeWidth = Mathf.Max(
                0.0001f,
                Vector2.Distance(landmarks[362], landmarks[263]));
            float rightEyeOpen =
                Vector2.Distance(landmarks[159], landmarks[145]) /
                rightEyeWidth;
            float leftEyeOpen =
                Vector2.Distance(landmarks[386], landmarks[374]) /
                leftEyeWidth;
            float mouthWidth = Mathf.Max(
                0.0001f,
                Vector2.Distance(landmarks[61], landmarks[291]));
            float mouthOpen =
                Vector2.Distance(landmarks[13], landmarks[14]) /
                mouthWidth;
            float mouthToFace = mouthWidth / Mathf.Max(0.0001f, faceWidth);

            return new FaceExpressionData
            {
                isValid = true,
                eyeBlinkRight = 1f - Mathf.InverseLerp(
                    0.035f,
                    0.22f,
                    rightEyeOpen),
                eyeBlinkLeft = 1f - Mathf.InverseLerp(
                    0.035f,
                    0.22f,
                    leftEyeOpen),
                eyeWideRight = Mathf.InverseLerp(
                    0.22f,
                    0.36f,
                    rightEyeOpen),
                eyeWideLeft = Mathf.InverseLerp(
                    0.22f,
                    0.36f,
                    leftEyeOpen),
                jawOpen = Mathf.InverseLerp(
                    0.025f,
                    0.42f,
                    mouthOpen),
                mouthSmileLeft = Mathf.InverseLerp(
                    0.34f,
                    0.48f,
                    mouthToFace),
                mouthSmileRight = Mathf.InverseLerp(
                    0.34f,
                    0.48f,
                    mouthToFace),
                mouthPucker = 1f - Mathf.InverseLerp(
                    0.24f,
                    0.38f,
                    mouthToFace),
                mouthFunnel = Mathf.Clamp01(
                    mouthOpen *
                    (1f - Mathf.InverseLerp(
                        0.26f,
                        0.42f,
                        mouthToFace)))
            };
        }


        // =====================================================
        // LIVE_STREAM submission timing
        // =====================================================

        private void RememberSubmittedFrame(
            long timestamp,
            long hostTicks)
        {
            lock (_submissionLock)
            {
                int index =
                    _submissionWriteIndex;


                _submissionTimestamps[index] =
                    timestamp;


                _submissionHostTicks[index] =
                    hostTicks;


                _submissionWriteIndex =
                    (
                        index +
                        1
                    )
                    %
                    SubmissionHistoryCapacity;
            }
        }


        private long ResolveSubmittedFrameHostTicks(
            long timestamp)
        {
            lock (_submissionLock)
            {
                // LIVE_STREAM callbacks normally correspond to one of the most
                // recently submitted frames. Search newest-to-oldest so the common
                // case exits after only a few comparisons even when the ring holds
                // many skipped/ignored submissions.
                for (
                    int offset = 0;
                    offset < SubmissionHistoryCapacity;
                    offset++
                )
                {
                    int index =
                        _submissionWriteIndex
                        -
                        1
                        -
                        offset;


                    if (index < 0)
                    {
                        index +=
                            SubmissionHistoryCapacity;
                    }


                    if (
                        _submissionTimestamps[index] ==
                        timestamp
                    )
                    {
                        long value =
                            _submissionHostTicks[index];


                        _submissionTimestamps[index] =
                            -1L;


                        _submissionHostTicks[index] =
                            0L;


                        return value;
                    }
                }
            }


            return 0L;
        }


        // =====================================================
        // Blendshape Extraction
        // =====================================================

        private FaceExpressionData ExtractExpressionData(
            FaceLandmarkerResult result)
        {
            FaceExpressionData data =
                default;


            if (
                result.faceBlendshapes == null
                ||
                result.faceBlendshapes.Count == 0
                ||
                result.faceBlendshapes[0].categories == null
            )
            {
                return data;
            }


            var categories =
                result.faceBlendshapes[0].categories;


            for (
                int i = 0;
                i < categories.Count;
                i++
            )
            {
                var category =
                    categories[i];


                switch (category.categoryName)
                {
                    case "eyeBlinkLeft":
                        data.eyeBlinkLeft =
                            category.score;
                        break;


                    case "eyeBlinkRight":
                        data.eyeBlinkRight =
                            category.score;
                        break;


                    case "eyeWideLeft":
                        data.eyeWideLeft =
                            category.score;
                        break;


                    case "eyeWideRight":
                        data.eyeWideRight =
                            category.score;
                        break;


                    case "cheekSquintLeft":
                        data.cheekSquintLeft =
                            category.score;
                        break;


                    case "cheekSquintRight":
                        data.cheekSquintRight =
                            category.score;
                        break;


                    case "jawOpen":
                        data.jawOpen =
                            category.score;
                        break;


                    case "mouthSmileLeft":
                        data.mouthSmileLeft =
                            category.score;
                        break;


                    case "mouthSmileRight":
                        data.mouthSmileRight =
                            category.score;
                        break;


                    case "mouthFrownLeft":
                        data.mouthFrownLeft =
                            category.score;
                        break;


                    case "mouthFrownRight":
                        data.mouthFrownRight =
                            category.score;
                        break;


                    case "mouthPucker":
                        data.mouthPucker =
                            category.score;
                        break;


                    case "mouthFunnel":
                        data.mouthFunnel =
                            category.score;
                        break;


                    case "browInnerUp":
                        data.browInnerUp =
                            category.score;
                        break;


                    case "browDownLeft":
                        data.browDownLeft =
                            category.score;
                        break;


                    case "browDownRight":
                        data.browDownRight =
                            category.score;
                        break;
                }
            }


            data.isValid =
                true;


            return data;
        }


        // =====================================================
        // Clear
        // =====================================================

        private void ResetLatestTrackingDataLocked()
        {
            _latestLandmarkCount =
                0;


            _latestLandmarkTimestamp =
                -1;


            _latestFaceCenter =
                new Vector2(
                    0.5f,
                    0.5f
                );


            _latestFaceEyeSpan =
                0f;


            _latestFaceRotation =
                Quaternion.identity;


            _hasLatestFaceRotation =
                false;


            _latestMotionTimestamp =
                -1;


            _latestPrecisionData =
                default;


            _latestExpressionData =
                default;


            _latestExpressionTimestamp =
                -1;


            _previousPublishedArrivalHostTicks =
                0L;


            _latestTrackingResultRateHz =
                0f;
        }


        private bool ClearTrackingDataForTimestamp(
            long timestamp)
        {
            lock (_trackingLock)
            {
                if (
                    !_acceptTrackingResults ||
                    (
                        _latestResultTimestamp >= 0L &&
                        timestamp < _latestResultTimestamp
                    )
                )
                {
                    return false;
                }


                _latestResultTimestamp =
                    timestamp;


                if (_sentisPrimaryActive)
                {
                    // A delayed auxiliary no-face callback must not blank a
                    // valid newer Inference Engine frame. It has its own four-frame
                    // loss hysteresis and falls back here only after that gate.
                    return true;
                }


                ResetLatestTrackingDataLocked();
                return true;
            }
        }


        private void ClearTrackingData(
            bool clearSubmissionHistory = false)
        {
            if (clearSubmissionHistory)
            {
                _liveStreamRequestInFlight = false;
                _pendingFreshWebCamFrame = false;
                _pendingSourceFrameHostTicks = 0L;
                _freshWebCamGeneration = 0;
                _lastObservedWebCamUnityFrame = -1;
                _observedWebCamTexture = null;
                _previousFreshSourceHostTicks = 0L;
                _previousSubmissionRateHostTicks = 0L;
                _latestFreshSourceRateHz = 0f;
                _latestSubmissionRateHz = 0f;
                _latestReadbackLatencyMs = 0f;
                _sentisPrimaryActive = false;
                _lastSentisAnchorTimestamp = -1L;
                _lastSentisAnchorTimestampApplied = -1L;
                _hasLatestSentisAnchor = false;
                _hasLatestMediaPipeAuxRotation = false;
                _hasSentisRotationOffset = false;
                _latestSentisLatencyMs = 0f;
                _latestSentisPresence = 0f;
            }


            lock (_trackingLock)
            {
                ResetLatestTrackingDataLocked();
            }


            // A no-face LIVE_STREAM callback must not erase timing entries for
            // newer frames that have already been submitted and may still produce
            // callbacks. Only lifecycle reset/Stop clears the submission ring.
            if (!clearSubmissionHistory)
            {
                return;
            }


            lock (_trackingLock)
            {
                _latestResultTimestamp =
                    -1L;
            }


            lock (_submissionLock)
            {
                for (
                    int i = 0;
                    i < SubmissionHistoryCapacity;
                    i++
                )
                {
                    _submissionTimestamps[i] =
                        -1L;


                    _submissionHostTicks[i] =
                        0L;
                }


                _submissionWriteIndex =
                    0;
            }
        }


        // =====================================================
        // Quaternion
        // =====================================================

        private bool IsValidQuaternion(
            Quaternion q)
        {
            return
                !float.IsNaN(q.x)
                &&
                !float.IsNaN(q.y)
                &&
                !float.IsNaN(q.z)
                &&
                !float.IsNaN(q.w)
                &&
                !float.IsInfinity(q.x)
                &&
                !float.IsInfinity(q.y)
                &&
                !float.IsInfinity(q.z)
                &&
                !float.IsInfinity(q.w);
        }


        private Quaternion NormalizeQuaternion(
            Quaternion q)
        {
            float magnitude =
                Mathf.Sqrt(
                    q.x * q.x
                    +
                    q.y * q.y
                    +
                    q.z * q.z
                    +
                    q.w * q.w
                );


            if (magnitude < 0.000001f)
            {
                return Quaternion.identity;
            }


            float inv =
                1f /
                magnitude;


            return new Quaternion(
                q.x * inv,
                q.y * inv,
                q.z * inv,
                q.w * inv
            );
        }
    }
}
