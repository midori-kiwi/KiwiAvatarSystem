using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v4.5.3 commercial live camera-frame tracker with source-age-safe GPU mailbox and adaptive search budget.
///
/// Commercial AR/filter systems commonly combine a slower semantic detector
/// with a lightweight local tracker between detector updates. Kiwi uses the
/// same hierarchy:
///
/// 1) FacePartCropper/ML provides the semantic eye/mouth anchor.
/// 2) This GPU block matcher measures the residual motion between consecutive
///    LIVE camera frames.
/// 3) Only the camera uvRect is corrected before FacePartShapeMask executes.
/// 4) The correction NEVER changes model/root pose or semantic landmarks.
///
/// This directly targets the failure mode where the newest camera texture has
/// already moved but its ML landmark/mask sample is still 80-150 ms old.
/// </summary>
[DefaultExecutionOrder(790)]
[DisallowMultipleComponent]
public sealed class KiwiFacePartLiveMotionBridge : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Live Face-Part Motion Bridge";

    private const int PartCount = 3;

    private const int LeftPart = 0;
    private const int RightPart = 1;
    private const int MouthPart = 2;

    [Header("Master")]
    public bool enableLiveFrameTracking = true;

    [Tooltip("Desktop/DX11 target. Mobile keeps the ML/prediction path unless explicitly enabled.")]
    public bool enableOnMobile = false;

    [Header("GPU matching")]
    [Range(256, 768)]
    public int trackingLongSide = 512;

    [Range(6, 28)]
    public int searchRadiusPixels = 20;

    [Range(1, 4)]
    public int patchStridePixels = 2;

    [Header("Adaptive Search Budget")]
    [Tooltip("Use a smaller search grid at rest and expand to searchRadiusPixels only when motion/latency risk rises. The GPU buffer remains fixed at the maximum radius, so no runtime reallocations are introduced.")]
    public bool adaptiveSearchRadius = true;

    [Range(4, 24)]
    public int restingSearchRadiusPixels = 12;

    [Range(0f, 1f)]
    public float minimumSearchRisk = 0.08f;

    [Range(0.05f, 1f)]
    public float fullSearchRisk = 0.72f;

    [Range(0f, 0.03f)]
    public float shiftPenalty = 0.0035f;

    [Range(0.4f, 1.4f)]
    public float eyePatchFraction = 0.92f;

    [Range(0.4f, 1.4f)]
    public float mouthPatchFraction = 0.88f;

    [Header("Acceptance")]
    [Range(0f, 1f)]
    public float minimumMatchConfidence = 0.18f;

    [Range(0.02f, 0.20f)]
    public float maximumAcceptedCost = 0.105f;

    [Range(0.10f, 0.80f)]
    public float maximumCorrectionCropFraction = 0.46f;

    [Range(0.02f, 0.25f)]
    public float maximumCorrectionHoldSeconds = 0.10f;

    [Header("Latency safety")]
    [Tooltip("Reject a GPU local-motion result when it is already too old to be useful by the time the readback completes. The limit is derived from the global face-part prediction budget plus a small scheduling slack.")]
    public bool rejectStaleReadbacks = true;

    [Range(0f, 0.05f)]
    public float readbackFreshnessSlackSeconds = 0.015f;

    [Tooltip("Start with one GPU readback in flight. A second slot is used only after measured readback latency proves that it stays below this threshold.")]
    [Range(8f, 100f)]
    public float dualFlightMaximumReadbackMs = 28f;

    [Tooltip("If repeated GPU local-motion readbacks are too old to be useful, temporarily stop dispatching block matches instead of spending GPU time on results that will be rejected.")]
    public bool enableReadbackOverloadCircuitBreaker = true;

    [Range(2, 12)]
    public int staleReadbacksBeforeSuspend = 4;

    [Range(40f, 200f)]
    public float overloadReadbackThresholdMs = 75f;

    [Range(0.25f, 5f)]
    public float overloadSuspendSeconds = 1.25f;

    [Header("Presentation")]
    [Range(10f, 240f)]
    public float acceptedCorrectionResponse = 105f;

    [Range(5f, 180f)]
    public float rejectedCorrectionReturnResponse = 48f;

    [Range(0f, 0.10f)]
    public float correctionDeadZoneCropFraction = 0.006f;

    [Header("Bilateral Eye Rigid Coherence")]

    [Tooltip("Solve the two eyes as one short-baseline similarity transform, then allow only a bounded eye-local residual. This prevents one eye from flying away during roll while still preserving genuine roll/scale motion.")]
    public bool enableBilateralEyeRigidSolve = true;

    [Range(0f, 1f)]
    public float minimumReliableEyeConfidence = 0.28f;

    [Range(1f, 20f)]
    public float maximumInterFrameEyeRotationDegrees = 7.0f;

    [Range(0.70f, 1f)]
    public float minimumInterFrameEyeScale = 0.88f;

    [Range(1f, 1.40f)]
    public float maximumInterFrameEyeScale = 1.14f;

    [Range(0f, 0.20f)]
    public float frontalEyeLocalResidualEyeSpan = 0.070f;

    [Range(0f, 0.12f)]
    public float tiltedEyeLocalResidualEyeSpan = 0.028f;

    [Range(0f, 45f)]
    public float tiltTighteningStartDegrees = 12f;

    [Range(5f, 70f)]
    public float tiltTighteningFullDegrees = 32f;

    [Header("Eye / Mouth Anatomical Layout")]

    [Tooltip("The eye pair owns rigid 2D face motion. Mouth tracking is treated as a local residual around that rigid motion so the mouth cannot jump into an eye region.")]
    public bool constrainMouthToEyePair = true;

    [Range(0f, 0.40f)]
    public float maximumMouthLocalResidualEyeSpanX = 0.16f;

    [Range(0f, 0.50f)]
    public float maximumMouthLocalResidualEyeSpanY = 0.20f;

    [Range(0.10f, 1f)]
    public float minimumMouthEyeLineSeparationFromBase = 0.58f;

    [Range(0.05f, 0.80f)]
    public float minimumMouthEyeLineSeparationEyeSpan = 0.28f;

    [Range(0.20f, 1.20f)]
    public float minimumMouthEyeCenterDistanceEyeSpan = 0.50f;

    [Header("References")]
    public FacePartCropper cropper;

    public KiwiDualDomainFaceQuality dualDomain;

    public KiwiTrackingContinuityState continuity;

    public KiwiFacePartAdaptiveContainment adaptiveContainment;

    public KiwiLatencyBudgetController latencyBudget;

    [Header("Diagnostics")]
    [SerializeField] private bool debugOperational;
    [SerializeField] private int debugTrackingWidth;
    [SerializeField] private int debugTrackingHeight;
    [SerializeField] private int debugPendingReadbacks;
    [SerializeField] private int debugDroppedMatchFrames;
    [SerializeField] private int debugActiveSearchRadius;
    [SerializeField] private float debugMatchRateHz;
    [SerializeField] private float debugReadbackLatencyMs;
    [SerializeField] private float debugCorrectionAgeMs;
    [SerializeField] private int debugMaximumConcurrentReadbacks = 1;
    [SerializeField] private int debugStaleReadbackDrops;
    [SerializeField] private bool debugOverloadSuspended;
    [SerializeField] private int debugOverloadSuspensions;
    [SerializeField] private int debugConsecutiveStaleReadbacks;
    [SerializeField] private float debugLeftConfidence;
    [SerializeField] private float debugRightConfidence;
    [SerializeField] private float debugMouthConfidence;
    [SerializeField] private Vector2 debugLeftCorrection;
    [SerializeField] private Vector2 debugRightCorrection;
    [SerializeField] private Vector2 debugMouthCorrection;
    [SerializeField] private float debugEyePairRollDegrees;
    [SerializeField] private float debugEyePairRotationDeltaDegrees;
    [SerializeField] private float debugEyePairScale = 1f;
    [SerializeField] private bool debugEyePairFallback;
    [SerializeField] private bool debugMouthAnatomyClamped;
    [SerializeField] private float debugMouthSeparationRatio = 1f;

    private sealed class ReadbackSlot
    {
        public ComputeBuffer buffer;
        public AsyncGPUReadbackRequest request;
        public float[] costMailbox;
        public bool pending;
        public bool completedReady;
        public int sequence;
        public int generation;
        public int searchRadius;
        public int gridSize;
        public long startedHostTicks;
        public long completedHostTicks;

        public readonly Rect[] baseRects =
            new Rect[PartCount];

        public readonly bool[] partEnabled =
            new bool[PartCount];
    }

    private ComputeShader _matcher;
    private int _kernel = -1;

    private RenderTexture _previousFrame;
    private RenderTexture _currentFrame;

    private int _trackingWidth;
    private int _trackingHeight;
    private int _allocatedSearchRadius = -1;

    private readonly ReadbackSlot[] _slots =
    {
        new ReadbackSlot(),
        new ReadbackSlot()
    };

    private readonly Action<AsyncGPUReadbackRequest>[] _readbackCallbacks =
        new Action<AsyncGPUReadbackRequest>[2];

    // AsyncGPUReadback callbacks only publish immutable cost data into this
    // two-slot mailbox. Presentation state is consumed deterministically from
    // LateUpdate; callbacks never mutate uvRect/corrections directly.
    private readonly object _readbackMailboxLock =
        new object();

    private readonly Vector4[] _previousCentersGpu =
        new Vector4[PartCount];

    private readonly Vector4[] _currentCentersGpu =
        new Vector4[PartCount];

    private readonly Vector4[] _patchHalfSizesGpu =
        new Vector4[PartCount];

    private readonly Vector2[] _previousBaseCenters =
        new Vector2[PartCount];

    private readonly Vector2[] _currentBaseCenters =
        new Vector2[PartCount];

    private readonly Vector2[] _decodedCorrection =
        new Vector2[PartCount];

    private readonly float[] _decodedConfidence =
        new float[PartCount];

    private readonly Vector2[] _targetCorrection =
        new Vector2[PartCount];

    private readonly Vector2[] _renderCorrection =
        new Vector2[PartCount];

    private readonly float[] _matchConfidence =
        new float[PartCount];

    private readonly Rect[] _currentBaseRects =
        new Rect[PartCount];

    private bool _hasPreviousFrame;
    private bool _hasPreviousCenters;

    private int _sequence;
    private int _generation;

    private int _latestCompletedSequence = -1;

    private long _latestCorrectionHostTicks;

    private long _previousCompletionHostTicks;
    private float _matchRateHz;
    private float _readbackLatencyMs;
    private int _staleReadbackDrops;
    private int _consecutiveStaleReadbacks;
    private int _overloadSuspensions;
    private double _overloadSuspendUntilRealtime;

    private int _lastNonWebCamUnityFrame = -1;

    public bool IsOperational =>
        debugOperational;

    public float MatchRateHz =>
        _matchRateHz;

    public float ReadbackLatencyMs =>
        _readbackLatencyMs;

    public int PendingReadbacks =>
        CountPendingReadbacks();

    public int DroppedMatchFrames =>
        debugDroppedMatchFrames;

    public int StaleReadbackDrops =>
        _staleReadbackDrops;

    public bool IsOverloadSuspended =>
        enableReadbackOverloadCircuitBreaker &&
        Time.realtimeSinceStartupAsDouble <
            _overloadSuspendUntilRealtime;

    public int OverloadSuspensions =>
        _overloadSuspensions;

    public int ActiveSearchRadiusPixels =>
        debugActiveSearchRadius;

    public float LeftConfidence =>
        _matchConfidence[LeftPart];

    public float RightConfidence =>
        _matchConfidence[RightPart];

    public float MouthConfidence =>
        _matchConfidence[MouthPart];

    public Vector2 LeftCorrection =>
        _renderCorrection[LeftPart];

    public Vector2 RightCorrection =>
        _renderCorrection[RightPart];

    public Vector2 MouthCorrection =>
        _renderCorrection[MouthPart];

    public float EyePairRollDegrees =>
        debugEyePairRollDegrees;

    public float EyePairRotationDeltaDegrees =>
        debugEyePairRotationDeltaDegrees;

    public float EyePairScale =>
        debugEyePairScale;

    public bool EyePairFallbackUsed =>
        debugEyePairFallback;

    public bool MouthAnatomyClamped =>
        debugMouthAnatomyClamped;

    public float MouthSeparationRatio =>
        debugMouthSeparationRatio;

    public float CorrectionAgeSeconds
    {
        get
        {
            if (_latestCorrectionHostTicks <= 0L)
            {
                return 1000f;
            }

            long now =
                System.Diagnostics.Stopwatch
                    .GetTimestamp();

            long delta =
                now - _latestCorrectionHostTicks;

            if (delta <= 0L)
            {
                return 0f;
            }

            return
                (float)(
                    delta /
                    (double)
                    System.Diagnostics.Stopwatch
                        .Frequency);
        }
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<
                KiwiFacePartLiveMotionBridge>(
                    FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(
                RuntimeObjectName);

        DontDestroyOnLoad(
            host);

        host.AddComponent<
            KiwiFacePartLiveMotionBridge>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(
            gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        _readbackCallbacks[0] =
            request =>
                CompleteReadback(
                    0,
                    request);

        _readbackCallbacks[1] =
            request =>
                CompleteReadback(
                    1,
                    request);

        RefreshReferences(true);
        LoadMatcher();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        ReleaseResources();
    }

    private void OnDisable()
    {
        // A component can be disabled and re-enabled without a scene load.
        // Invalidate results produced by the previous enabled lifetime so an
        // old local residual can never be presented after re-enable. Pending
        // requests stay marked pending until their callbacks retire them; this
        // prevents a ComputeBuffer slot from being reused while the GPU still
        // owns it.
        _generation++;

        lock (_readbackMailboxLock)
        {
            for (
                int i = 0;
                i < _slots.Length;
                i++
            )
            {
                _slots[i].completedReady =
                    false;
            }
        }

        ResetCorrections();

        _consecutiveStaleReadbacks = 0;
        _overloadSuspendUntilRealtime = 0.0;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _generation++;

        cropper = null;
        dualDomain = null;
        continuity = null;
        adaptiveContainment = null;
        latencyBudget = null;

        ReleaseResources();

        _hasPreviousFrame = false;
        _hasPreviousCenters = false;

        _consecutiveStaleReadbacks = 0;
        _overloadSuspendUntilRealtime = 0.0;

        ResetCorrections();

        RefreshReferences(true);
        LoadMatcher();
    }

    private void LateUpdate()
    {
        RefreshReferences(false);

        if (!CanRun())
        {
            DecayCorrectionsToZero(
                Time.unscaledDeltaTime);

            ApplyCorrections();

            UpdateDiagnostics();
            return;
        }

        Texture source =
            cropper.sourceImage.texture;

        EnsureFrameResources(
            source);

        if (
            _previousFrame == null ||
            _currentFrame == null ||
            _matcher == null ||
            _kernel < 0
        )
        {
            DecayCorrectionsToZero(
                Time.unscaledDeltaTime);

            ApplyCorrections();

            UpdateDiagnostics();
            return;
        }

        ConsumeCompletedReadbackMailbox();

        bool freshCameraFrame =
            IsFreshCameraFrame(
                source);

        if (freshCameraFrame)
        {
            CaptureAndScheduleMatch(
                source);
        }

        UpdateRenderedCorrections(
            Time.unscaledDeltaTime);

        ApplyCorrections();

        UpdateDiagnostics();
    }

    private bool CanRun()
    {
        if (
            !enableLiveFrameTracking ||
            (
                Application.isMobilePlatform &&
                !enableOnMobile
            ) ||
            !SystemInfo.supportsComputeShaders ||
            cropper == null ||
            cropper.sourceImage == null ||
            cropper.sourceImage.texture == null ||
            cropper.leftEyeImage == null ||
            cropper.rightEyeImage == null ||
            cropper.mouthImage == null
        )
        {
            return false;
        }

        if (
            continuity != null &&
            (
                continuity.State ==
                    KiwiTrackingContinuityState.ContinuityState.Holding ||
                continuity.State ==
                    KiwiTrackingContinuityState.ContinuityState.Lost
            )
        )
        {
            return false;
        }

        return true;
    }

    private void LoadMatcher()
    {
        if (_matcher != null)
        {
            return;
        }

        _matcher =
            Resources.Load<ComputeShader>(
                "KiwiFacePartBlockMatch");

        if (_matcher == null)
        {
            _kernel =
                -1;

            return;
        }

        try
        {
            _kernel =
                _matcher.FindKernel(
                    "Match");
        }
        catch
        {
            _kernel =
                -1;
        }
    }

    private void EnsureFrameResources(
        Texture source)
    {
        int sourceWidth =
            Mathf.Max(
                1,
                source.width);

        int sourceHeight =
            Mathf.Max(
                1,
                source.height);

        float scale =
            Mathf.Clamp(
                trackingLongSide /
                    (float)Mathf.Max(
                        sourceWidth,
                        sourceHeight),
                0.05f,
                1f);

        int width =
            Mathf.Max(
                128,
                Mathf.RoundToInt(
                    sourceWidth *
                    scale));

        int height =
            Mathf.Max(
                96,
                Mathf.RoundToInt(
                    sourceHeight *
                    scale));

        bool sizeChanged =
            width !=
                _trackingWidth ||
            height !=
                _trackingHeight;

        bool searchChanged =
            _allocatedSearchRadius !=
                searchRadiusPixels;

        if (
            !sizeChanged &&
            !searchChanged &&
            _previousFrame != null &&
            _currentFrame != null &&
            SlotsAllocated()
        )
        {
            return;
        }

        if (CountPendingReadbacks() > 0)
        {
            return;
        }

        ReleaseGpuOnly();

        _trackingWidth =
            width;

        _trackingHeight =
            height;

        _allocatedSearchRadius =
            searchRadiusPixels;

        _previousFrame =
            CreateFrameTexture(
                width,
                height,
                "Kiwi FacePart Motion Previous");

        _currentFrame =
            CreateFrameTexture(
                width,
                height,
                "Kiwi FacePart Motion Current");

        int grid =
            searchRadiusPixels *
                2 +
            1;

        int costCount =
            PartCount *
            grid *
            grid;

        for (
            int i = 0;
            i < _slots.Length;
            i++
        )
        {
            _slots[i].buffer =
                new ComputeBuffer(
                    costCount,
                    sizeof(float),
                    ComputeBufferType.Structured);

            _slots[i].costMailbox =
                new float[
                    costCount];

            _slots[i].pending =
                false;

            _slots[i].completedReady =
                false;
        }

        _hasPreviousFrame =
            false;

        _hasPreviousCenters =
            false;
    }

    private static RenderTexture CreateFrameTexture(
        int width,
        int height,
        string name)
    {
        RenderTexture texture =
            new RenderTexture(
                width,
                height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                name =
                    name,
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

        texture.Create();

        return
            texture;
    }

    private bool SlotsAllocated()
    {
        for (
            int i = 0;
            i < _slots.Length;
            i++
        )
        {
            if (
                _slots[i].buffer ==
                null
            )
            {
                return false;
            }
        }

        return true;
    }

    private bool IsFreshCameraFrame(
        Texture source)
    {
        WebCamTexture webcam =
            source as WebCamTexture;

        if (webcam != null)
        {
            return
                webcam.didUpdateThisFrame;
        }

        if (
            _lastNonWebCamUnityFrame ==
            Time.frameCount
        )
        {
            return false;
        }

        _lastNonWebCamUnityFrame =
            Time.frameCount;

        return true;
    }

    private void CaptureAndScheduleMatch(
        Texture source)
    {
        if (
            !TryGetBaseRects(
                _currentBaseRects)
        )
        {
            return;
        }

        Graphics.Blit(
            source,
            _currentFrame);

        _currentBaseCenters[LeftPart] =
            _currentBaseRects[LeftPart].center;

        _currentBaseCenters[RightPart] =
            _currentBaseRects[RightPart].center;

        _currentBaseCenters[MouthPart] =
            _currentBaseRects[MouthPart].center;

        if (
            !_hasPreviousFrame ||
            !_hasPreviousCenters
        )
        {
            Graphics.Blit(
                _currentFrame,
                _previousFrame);

            for (
                int i = 0;
                i < PartCount;
                i++
            )
            {
                _previousBaseCenters[i] =
                    _currentBaseCenters[i];
            }

            _hasPreviousFrame =
                true;

            _hasPreviousCenters =
                true;

            return;
        }

        if (IsOverloadSuspended)
        {
            // Keep only a current image reference while overloaded. No compute
            // match/readback is queued, so the primary Inference/MediaPipe GPU
            // path gets the budget instead of competing with unusable 100 ms
            // local-motion jobs. The next post-suspension dispatch is therefore
            // local to the latest camera frame rather than a stale long-baseline
            // optical-flow attempt.
            AdvanceReferenceWithoutScheduling();
            return;
        }

        int maximumPending =
            ResolveMaximumConcurrentReadbacks();

        if (
            CountPendingReadbacks() >=
                maximumPending
        )
        {
            debugDroppedMatchFrames++;
            AdvanceReferenceWithoutScheduling();
            return;
        }

        int slotIndex =
            FindFreeSlot();

        if (slotIndex < 0)
        {
            debugDroppedMatchFrames++;
            AdvanceReferenceWithoutScheduling();
            return;
        }

        ReadbackSlot slot =
            _slots[
                slotIndex];

        int sequence =
            ++_sequence;

        int generation =
            _generation;

        int activeRadius =
            ResolveActiveSearchRadius();

        int grid =
            activeRadius *
                2 +
            1;

        bool uvStartsAtTop =
            SystemInfo.graphicsUVStartsAtTop;

        for (
            int i = 0;
            i < PartCount;
            i++
        )
        {
            Vector2 previousCenter =
                ToGpuUv(
                    _previousBaseCenters[i],
                    uvStartsAtTop);

            Vector2 currentCenter =
                ToGpuUv(
                    _currentBaseCenters[i],
                    uvStartsAtTop);

            _previousCentersGpu[i] =
                new Vector4(
                    previousCenter.x,
                    previousCenter.y,
                    0f,
                    0f);

            _currentCentersGpu[i] =
                new Vector4(
                    currentCenter.x,
                    currentCenter.y,
                    0f,
                    0f);

            Rect rect =
                _currentBaseRects[i];

            float fraction =
                i ==
                    MouthPart
                    ? mouthPatchFraction
                    : eyePatchFraction;

            float halfX =
                Mathf.Clamp(
                    rect.width *
                        _trackingWidth *
                        0.5f *
                        fraction,
                    5f,
                    36f);

            float halfY =
                Mathf.Clamp(
                    rect.height *
                        _trackingHeight *
                        0.5f *
                        fraction,
                    4f,
                    28f);

            _patchHalfSizesGpu[i] =
                new Vector4(
                    halfX,
                    halfY,
                    0f,
                    0f);

            slot.baseRects[i] =
                rect;

            slot.partEnabled[i] =
                rect.width >
                    0.001f &&
                rect.height >
                    0.001f;
        }

        _matcher.SetInt(
            "_Width",
            _trackingWidth);

        _matcher.SetInt(
            "_Height",
            _trackingHeight);

        _matcher.SetInt(
            "_SearchRadius",
            activeRadius);

        _matcher.SetInt(
            "_GridSize",
            grid);

        _matcher.SetInt(
            "_PatchStride",
            patchStridePixels);

        _matcher.SetFloat(
            "_ShiftPenalty",
            shiftPenalty);

        _matcher.SetVectorArray(
            "_PrevCenters",
            _previousCentersGpu);

        _matcher.SetVectorArray(
            "_CurrCenters",
            _currentCentersGpu);

        _matcher.SetVectorArray(
            "_PatchHalfSizes",
            _patchHalfSizesGpu);

        _matcher.SetTexture(
            _kernel,
            "_Previous",
            _previousFrame);

        _matcher.SetTexture(
            _kernel,
            "_Current",
            _currentFrame);

        _matcher.SetBuffer(
            _kernel,
            "_Costs",
            slot.buffer);

        int groups =
            Mathf.CeilToInt(
                grid /
                8f);

        _matcher.Dispatch(
            _kernel,
            groups,
            groups,
            PartCount);

        slot.pending =
            true;

        slot.sequence =
            sequence;

        slot.generation =
            generation;

        slot.searchRadius =
            activeRadius;

        slot.gridSize =
            grid;

        slot.startedHostTicks =
            System.Diagnostics.Stopwatch
                .GetTimestamp();

        slot.request =
            AsyncGPUReadback.Request(
                slot.buffer,
                _readbackCallbacks[
                    slotIndex]);

        SwapFrameTextures();

        for (
            int i = 0;
            i < PartCount;
            i++
        )
        {
            _previousBaseCenters[i] =
                _currentBaseCenters[i];
        }
    }

    private void AdvanceReferenceWithoutScheduling()
    {
        // Latest-frame priority: when GPU/readback capacity is occupied we do
        // not queue another old job. We still advance the reference to the
        // newest camera image so the next scheduled match is local in time.
        SwapFrameTextures();

        for (
            int i = 0;
            i < PartCount;
            i++
        )
        {
            _previousBaseCenters[i] =
                _currentBaseCenters[i];
        }
    }

    private int ResolveMaximumConcurrentReadbacks()
    {
        // Start single-flight. Two concurrent requests are allowed only when
        // measured completion latency is already comfortably low. This keeps
        // the existing two-slot mailbox without turning it into a two-frame
        // GPU queue on overloaded systems.
        int maximum =
            _readbackLatencyMs > 0f &&
            _readbackLatencyMs <=
                dualFlightMaximumReadbackMs
                ? 2
                : 1;

        debugMaximumConcurrentReadbacks =
            maximum;

        return maximum;
    }

    private bool TryGetBaseRects(
        Rect[] rects)
    {
        if (
            rects == null ||
            rects.Length <
                PartCount ||
            cropper == null
        )
        {
            return false;
        }

        rects[LeftPart] =
            cropper.leftEyeImage.uvRect;

        rects[RightPart] =
            cropper.rightEyeImage.uvRect;

        rects[MouthPart] =
            cropper.mouthImage.uvRect;

        for (
            int i = 0;
            i < PartCount;
            i++
        )
        {
            if (
                rects[i].width <=
                    0.001f ||
                rects[i].height <=
                    0.001f
            )
            {
                return false;
            }
        }

        return true;
    }

    private void CompleteReadback(
        int slotIndex,
        AsyncGPUReadbackRequest request)
    {
        if (
            slotIndex < 0 ||
            slotIndex >=
                _slots.Length
        )
        {
            return;
        }

        ReadbackSlot slot =
            _slots[
                slotIndex];

        int generation =
            slot.generation;

        if (
            generation !=
                _generation ||
            request.hasError
        )
        {
            lock (_readbackMailboxLock)
            {
                slot.pending =
                    false;

                slot.completedReady =
                    false;
            }

            return;
        }

        NativeArray<float> data =
            request.GetData<float>();

        if (
            slot.costMailbox == null ||
            data.Length >
                slot.costMailbox.Length
        )
        {
            lock (_readbackMailboxLock)
            {
                slot.pending =
                    false;

                slot.completedReady =
                    false;
            }

            return;
        }

        lock (_readbackMailboxLock)
        {
            // Copy while request data is valid. All decode/anatomy/presentation
            // work is intentionally deferred to LateUpdate.
            data.CopyTo(
                slot.costMailbox);

            slot.completedHostTicks =
                System.Diagnostics.Stopwatch
                    .GetTimestamp();

            slot.pending =
                false;

            slot.completedReady =
                true;
        }
    }

    private void ConsumeCompletedReadbackMailbox()
    {
        int selected =
            -1;

        int selectedSequence =
            _latestCompletedSequence;

        lock (_readbackMailboxLock)
        {
            for (
                int i = 0;
                i < _slots.Length;
                i++
            )
            {
                ReadbackSlot slot =
                    _slots[i];

                if (
                    slot.completedReady &&
                    slot.generation ==
                        _generation &&
                    slot.sequence >
                        selectedSequence
                )
                {
                    selected =
                        i;

                    selectedSequence =
                        slot.sequence;
                }
            }

            // Older completed results are superseded by the newest frame.
            for (
                int i = 0;
                i < _slots.Length;
                i++
            )
            {
                if (
                    i != selected &&
                    _slots[i].completedReady &&
                    _slots[i].sequence <=
                        selectedSequence
                )
                {
                    _slots[i].completedReady =
                        false;
                }
            }
        }

        if (selected < 0)
        {
            return;
        }

        ReadbackSlot chosen =
            _slots[
                selected];

        // The slot cannot be reused while completedReady is true, so decoding
        // its preallocated mailbox requires no copy and cannot race a new GPU job.
        DecodeCompletedSlot(
            chosen);

        lock (_readbackMailboxLock)
        {
            chosen.completedReady =
                false;
        }
    }

    private void DecodeCompletedSlot(
        ReadbackSlot slot)
    {
        if (
            slot == null ||
            slot.costMailbox == null ||
            slot.sequence <=
                _latestCompletedSequence ||
            slot.generation !=
                _generation
        )
        {
            return;
        }

        int grid =
            Mathf.Max(
                1,
                slot.gridSize);

        int activeRadius =
            Mathf.Max(
                1,
                slot.searchRadius);

        int expected =
            PartCount *
            grid *
            grid;

        if (
            slot.costMailbox.Length <
            expected
        )
        {
            return;
        }

        RecordCompletedTiming(
            slot,
            out long finishedHostTicks,
            out float completedReadbackSeconds);

        if (
            rejectStaleReadbacks &&
            completedReadbackSeconds >
                ResolveMaximumUsefulReadbackSeconds()
        )
        {
            _latestCompletedSequence =
                slot.sequence;

            _staleReadbackDrops++;
            _consecutiveStaleReadbacks++;

            if (
                enableReadbackOverloadCircuitBreaker &&
                _consecutiveStaleReadbacks >=
                    Mathf.Max(2, staleReadbacksBeforeSuspend) &&
                _readbackLatencyMs >=
                    Mathf.Max(40f, overloadReadbackThresholdMs)
            )
            {
                _overloadSuspendUntilRealtime =
                    Time.realtimeSinceStartupAsDouble +
                    Mathf.Max(0.25f, overloadSuspendSeconds);

                _overloadSuspensions++;
                _consecutiveStaleReadbacks = 0;
            }

            RejectDecodedCorrection();
            return;
        }

        _consecutiveStaleReadbacks = 0;

        bool uvStartsAtTop =
            SystemInfo.graphicsUVStartsAtTop;

        for (
            int part = 0;
            part < PartCount;
            part++
        )
        {
            if (!slot.partEnabled[part])
            {
                _decodedCorrection[part] =
                    Vector2.zero;

                _decodedConfidence[part] =
                    0f;

                continue;
            }

            DecodePartMatch(
                slot.costMailbox,
                part,
                grid,
                activeRadius,
                slot.baseRects[part],
                uvStartsAtTop,
                out Vector2 correction,
                out float confidence);

            float partQuality =
                ResolvePartQuality(
                    part);

            confidence *=
                Mathf.InverseLerp(
                    0.35f,
                    0.85f,
                    partQuality);

            if (
                confidence <
                    minimumMatchConfidence
            )
            {
                correction =
                    Vector2.zero;
            }

            _decodedCorrection[part] =
                correction;

            _decodedConfidence[part] =
                confidence;
        }

        ResolveRigidAnatomicalCorrections(
            slot);

        _latestCompletedSequence =
            slot.sequence;

        // Correction age is measured from the frame/job observation time, not
        // from callback completion. A 100 ms readback must never become a
        // "0 ms old" correction merely because it just arrived.
        _latestCorrectionHostTicks =
            slot.startedHostTicks > 0L
                ? slot.startedHostTicks
                : finishedHostTicks;
    }

    private void RecordCompletedTiming(
        ReadbackSlot slot,
        out long finishedHostTicks,
        out float readbackSeconds)
    {
        finishedHostTicks =
            slot.completedHostTicks > 0L
                ? slot.completedHostTicks
                : System.Diagnostics.Stopwatch
                    .GetTimestamp();

        readbackSeconds = 0f;

        if (
            slot.startedHostTicks > 0L &&
            finishedHostTicks >
                slot.startedHostTicks
        )
        {
            readbackSeconds =
                (float)(
                    (finishedHostTicks -
                        slot.startedHostTicks) /
                    (double)
                    System.Diagnostics.Stopwatch
                        .Frequency);

            float milliseconds =
                readbackSeconds *
                1000f;

            _readbackLatencyMs =
                _readbackLatencyMs > 0f
                    ? Mathf.Lerp(
                        _readbackLatencyMs,
                        milliseconds,
                        0.20f)
                    : milliseconds;
        }

        if (
            _previousCompletionHostTicks > 0L &&
            finishedHostTicks >
                _previousCompletionHostTicks
        )
        {
            float hz =
                (float)(
                    System.Diagnostics.Stopwatch
                        .Frequency /
                    (double)(
                        finishedHostTicks -
                        _previousCompletionHostTicks));

            _matchRateHz =
                _matchRateHz > 0f
                    ? Mathf.Lerp(
                        _matchRateHz,
                        hz,
                        0.18f)
                    : hz;
        }

        _previousCompletionHostTicks =
            finishedHostTicks;
    }

    private float ResolveMaximumUsefulReadbackSeconds()
    {
        float budget =
            latencyBudget != null
                ? latencyBudget.FacePartPredictionBudgetSeconds +
                    readbackFreshnessSlackSeconds
                : 0.065f;

        float hold =
            Mathf.Max(
                0.02f,
                maximumCorrectionHoldSeconds);

        return
            Mathf.Min(
                hold,
                Mathf.Clamp(
                    budget,
                    0.035f,
                    0.080f));
    }

    private void RejectDecodedCorrection()
    {
        for (
            int i = 0;
            i < PartCount;
            i++
        )
        {
            _decodedCorrection[i] =
                Vector2.zero;

            _decodedConfidence[i] =
                0f;

            _targetCorrection[i] =
                Vector2.zero;

            _matchConfidence[i] =
                0f;
        }
    }

    private void DecodePartMatch(
        float[] data,
        int part,
        int grid,
        int radius,
        Rect baseRect,
        bool uvStartsAtTop,
        out Vector2 correction,
        out float confidence)
    {
        correction =
            Vector2.zero;

        confidence =
            0f;

        int baseIndex =
            part *
            grid *
            grid;

        int centerIndex =
            baseIndex +
            radius *
                grid +
            radius;

        float centerCost =
            data[
                centerIndex];

        float bestCost =
            float.PositiveInfinity;

        int bestX =
            radius;

        int bestY =
            radius;

        for (
            int y = 0;
            y < grid;
            y++
        )
        {
            for (
                int x = 0;
                x < grid;
                x++
            )
            {
                float cost =
                    data[
                        baseIndex +
                        y *
                            grid +
                        x];

                if (
                    cost <
                    bestCost
                )
                {
                    bestCost =
                        cost;

                    bestX =
                        x;

                    bestY =
                        y;
                }
            }
        }

        if (
            float.IsNaN(
                bestCost) ||
            float.IsInfinity(
                bestCost) ||
            bestCost >
                maximumAcceptedCost
        )
        {
            return;
        }

        float secondBest =
            float.PositiveInfinity;

        for (
            int y = 0;
            y < grid;
            y++
        )
        {
            for (
                int x = 0;
                x < grid;
                x++
            )
            {
                if (
                    Mathf.Abs(
                        x -
                        bestX) <=
                        1 &&
                    Mathf.Abs(
                        y -
                        bestY) <=
                        1
                )
                {
                    continue;
                }

                float cost =
                    data[
                        baseIndex +
                        y *
                            grid +
                        x];

                secondBest =
                    Mathf.Min(
                        secondBest,
                        cost);
            }
        }

        int dx =
            bestX -
            radius;

        int dy =
            bestY -
            radius;

        float costQuality =
            1f -
            Mathf.InverseLerp(
                0.025f,
                maximumAcceptedCost,
                bestCost);

        float centerImprovement =
            (
                centerCost -
                bestCost
            ) /
            Mathf.Max(
                0.015f,
                centerCost);

        float uniqueness =
            float.IsInfinity(
                secondBest)
                ? 0f
                : (
                    secondBest -
                    bestCost
                  ) /
                  Mathf.Max(
                      0.015f,
                      secondBest);

        bool nearZeroShift =
            Mathf.Abs(
                dx) <=
                1 &&
            Mathf.Abs(
                dy) <=
                1;

        if (nearZeroShift)
        {
            confidence =
                Mathf.Clamp01(
                    0.45f +
                    costQuality *
                    0.50f);
        }
        else
        {
            confidence =
                Mathf.Clamp01(
                    costQuality *
                    (
                        0.38f +
                        Mathf.Clamp01(
                            centerImprovement) *
                            0.42f +
                        Mathf.Clamp01(
                            uniqueness *
                            4f) *
                            0.20f
                    ));
        }

        float uvX =
            dx /
            (float)Mathf.Max(
                1,
                _trackingWidth);

        float uvY =
            dy /
            (float)Mathf.Max(
                1,
                _trackingHeight);

        if (uvStartsAtTop)
        {
            uvY =
                -uvY;
        }

        correction =
            new Vector2(
                uvX,
                uvY);

        float maxX =
            baseRect.width *
            maximumCorrectionCropFraction;

        float maxY =
            baseRect.height *
            maximumCorrectionCropFraction;

        correction.x =
            Mathf.Clamp(
                correction.x,
                -maxX,
                maxX);

        correction.y =
            Mathf.Clamp(
                correction.y,
                -maxY,
                maxY);

        float deadX =
            baseRect.width *
            correctionDeadZoneCropFraction;

        float deadY =
            baseRect.height *
            correctionDeadZoneCropFraction;

        if (
            Mathf.Abs(
                correction.x) <
            deadX
        )
        {
            correction.x =
                0f;
        }

        if (
            Mathf.Abs(
                correction.y) <
            deadY
        )
        {
            correction.y =
                0f;
        }
    }

    private void ResolveRigidAnatomicalCorrections(
        ReadbackSlot slot)
    {
        debugEyePairFallback =
            false;

        debugMouthAnatomyClamped =
            false;

        debugEyePairRotationDeltaDegrees =
            0f;

        debugEyePairScale =
            1f;

        debugMouthSeparationRatio =
            1f;

        Rect leftRect =
            slot.baseRects[
                LeftPart];

        Rect rightRect =
            slot.baseRects[
                RightPart];

        Rect mouthRect =
            slot.baseRects[
                MouthPart];

        Vector2 baseLeft =
            leftRect.center;

        Vector2 baseRight =
            rightRect.center;

        Vector2 baseMouth =
            mouthRect.center;

        Vector2 baseEyeVector =
            baseRight -
            baseLeft;

        float eyeSpan =
            baseEyeVector.magnitude;

        if (
            !enableBilateralEyeRigidSolve ||
            eyeSpan <=
                0.0001f
        )
        {
            for (
                int i = 0;
                i < PartCount;
                i++
            )
            {
                _targetCorrection[i] =
                    _decodedCorrection[i];

                _matchConfidence[i] =
                    _decodedConfidence[i];
            }

            return;
        }

        Vector2 baseEyeMid =
            (
                baseLeft +
                baseRight
            ) *
            0.5f;

        float baseRoll =
            Mathf.Abs(
                NormalizeSignedAngle(
                    Mathf.Atan2(
                        baseEyeVector.y,
                        baseEyeVector.x) *
                    Mathf.Rad2Deg));

        if (baseRoll > 90f)
        {
            baseRoll =
                180f -
                baseRoll;
        }

        debugEyePairRollDegrees =
            baseRoll;

        float leftConfidence =
            _decodedConfidence[
                LeftPart];

        float rightConfidence =
            _decodedConfidence[
                RightPart];

        bool leftReliable =
            leftConfidence >=
            minimumReliableEyeConfidence;

        bool rightReliable =
            rightConfidence >=
            minimumReliableEyeConfidence;

        Vector2 leftCorrection =
            _decodedCorrection[
                LeftPart];

        Vector2 rightCorrection =
            _decodedCorrection[
                RightPart];

        Vector2 pairTranslation =
            Vector2.zero;

        float pairRotationDegrees =
            0f;

        float pairScale =
            1f;

        float pairConfidence =
            0f;

        if (
            leftReliable &&
            rightReliable
        )
        {
            float weightSum =
                Mathf.Max(
                    0.0001f,
                    leftConfidence +
                    rightConfidence);

            pairTranslation =
                (
                    leftCorrection *
                        leftConfidence +
                    rightCorrection *
                        rightConfidence
                ) /
                weightSum;

            Vector2 rawTargetLeft =
                baseLeft +
                leftCorrection;

            Vector2 rawTargetRight =
                baseRight +
                rightCorrection;

            Vector2 rawTargetVector =
                rawTargetRight -
                rawTargetLeft;

            float rawScale =
                rawTargetVector.magnitude /
                Mathf.Max(
                    0.0001f,
                    eyeSpan);

            float rawRotation =
                Vector2.SignedAngle(
                    baseEyeVector,
                    rawTargetVector);

            float shapeReliability =
                Mathf.Clamp01(
                    Mathf.Min(
                        leftConfidence,
                        rightConfidence) /
                    Mathf.Max(
                        0.0001f,
                        Mathf.Max(
                            leftConfidence,
                            rightConfidence)));

            pairScale =
                Mathf.Lerp(
                    1f,
                    Mathf.Clamp(
                        rawScale,
                        minimumInterFrameEyeScale,
                        maximumInterFrameEyeScale),
                    shapeReliability);

            pairRotationDegrees =
                Mathf.Lerp(
                    0f,
                    Mathf.Clamp(
                        rawRotation,
                        -maximumInterFrameEyeRotationDegrees,
                        maximumInterFrameEyeRotationDegrees),
                    shapeReliability);

            pairConfidence =
                Mathf.Max(
                    leftConfidence,
                    rightConfidence);
        }
        else if (
            leftReliable ||
            rightReliable
        )
        {
            debugEyePairFallback =
                true;

            bool useLeft =
                leftReliable &&
                (
                    !rightReliable ||
                    leftConfidence >=
                        rightConfidence
                );

            pairTranslation =
                useLeft
                    ? leftCorrection
                    : rightCorrection;

            pairScale =
                1f;

            pairRotationDegrees =
                0f;

            pairConfidence =
                useLeft
                    ? leftConfidence
                    : rightConfidence;
        }
        else
        {
            _targetCorrection[
                LeftPart] =
                Vector2.zero;

            _targetCorrection[
                RightPart] =
                Vector2.zero;

            _targetCorrection[
                MouthPart] =
                Vector2.zero;

            _matchConfidence[
                LeftPart] =
                0f;

            _matchConfidence[
                RightPart] =
                0f;

            _matchConfidence[
                MouthPart] =
                _decodedConfidence[
                    MouthPart];

            return;
        }

        debugEyePairRotationDeltaDegrees =
            pairRotationDegrees;

        debugEyePairScale =
            pairScale;

        Vector2 fittedEyeVector =
            RotateVector(
                baseEyeVector *
                    pairScale,
                pairRotationDegrees);

        Vector2 pairMid =
            baseEyeMid +
            pairTranslation;

        Vector2 pairLeftTarget =
            pairMid -
            fittedEyeVector *
                0.5f;

        Vector2 pairRightTarget =
            pairMid +
            fittedEyeVector *
                0.5f;

        float tiltFactor =
            Mathf.InverseLerp(
                tiltTighteningStartDegrees,
                Mathf.Max(
                    tiltTighteningStartDegrees +
                        0.01f,
                    tiltTighteningFullDegrees),
                baseRoll);

        float eyeResidualLimit =
            eyeSpan *
            Mathf.Lerp(
                frontalEyeLocalResidualEyeSpan,
                tiltedEyeLocalResidualEyeSpan,
                tiltFactor);

        if (leftReliable)
        {
            Vector2 rawLeftTarget =
                baseLeft +
                leftCorrection;

            Vector2 localResidual =
                Vector2.ClampMagnitude(
                    rawLeftTarget -
                        pairLeftTarget,
                    eyeResidualLimit);

            pairLeftTarget +=
                localResidual;
        }

        if (rightReliable)
        {
            Vector2 rawRightTarget =
                baseRight +
                rightCorrection;

            Vector2 localResidual =
                Vector2.ClampMagnitude(
                    rawRightTarget -
                        pairRightTarget,
                    eyeResidualLimit);

            pairRightTarget +=
                localResidual;
        }

        _targetCorrection[
            LeftPart] =
            pairLeftTarget -
            baseLeft;

        _targetCorrection[
            RightPart] =
            pairRightTarget -
            baseRight;

        _matchConfidence[
            LeftPart] =
            Mathf.Max(
                leftConfidence,
                pairConfidence *
                    0.78f);

        _matchConfidence[
            RightPart] =
            Mathf.Max(
                rightConfidence,
                pairConfidence *
                    0.78f);

        if (!constrainMouthToEyePair)
        {
            _targetCorrection[
                MouthPart] =
                _decodedCorrection[
                    MouthPart];

            _matchConfidence[
                MouthPart] =
                _decodedConfidence[
                    MouthPart];

            return;
        }

        Vector2 fittedMouthRigidTarget =
            pairMid +
            RotateVector(
                (
                    baseMouth -
                    baseEyeMid
                ) *
                pairScale,
                pairRotationDegrees);

        Vector2 mouthResolvedTarget =
            fittedMouthRigidTarget;

        float mouthConfidence =
            _decodedConfidence[
                MouthPart];

        if (
            mouthConfidence >=
            minimumMatchConfidence
        )
        {
            Vector2 rawMouthTarget =
                baseMouth +
                _decodedCorrection[
                    MouthPart];

            Vector2 localResidual =
                rawMouthTarget -
                fittedMouthRigidTarget;

            Vector2 xAxis =
                fittedEyeVector.sqrMagnitude >
                    0.0000001f
                    ? fittedEyeVector.normalized
                    : baseEyeVector.normalized;

            Vector2 yAxis =
                new Vector2(
                    -xAxis.y,
                    xAxis.x);

            if (
                Vector2.Dot(
                    baseMouth -
                        baseEyeMid,
                    yAxis) <
                0f
            )
            {
                yAxis =
                    -yAxis;
            }

            float localX =
                Vector2.Dot(
                    localResidual,
                    xAxis);

            float localY =
                Vector2.Dot(
                    localResidual,
                    yAxis);

            localX =
                Mathf.Clamp(
                    localX,
                    -eyeSpan *
                        maximumMouthLocalResidualEyeSpanX,
                    eyeSpan *
                        maximumMouthLocalResidualEyeSpanX);

            localY =
                Mathf.Clamp(
                    localY,
                    -eyeSpan *
                        maximumMouthLocalResidualEyeSpanY,
                    eyeSpan *
                        maximumMouthLocalResidualEyeSpanY);

            mouthResolvedTarget =
                fittedMouthRigidTarget +
                xAxis *
                    localX +
                yAxis *
                    localY;

            float baseSeparation =
                Mathf.Abs(
                    Vector2.Dot(
                        baseMouth -
                            baseEyeMid,
                        yAxis));

            float minimumSeparation =
                Mathf.Max(
                    baseSeparation *
                        minimumMouthEyeLineSeparationFromBase,
                    eyeSpan *
                        minimumMouthEyeLineSeparationEyeSpan);

            float resolvedSeparation =
                Vector2.Dot(
                    mouthResolvedTarget -
                        pairMid,
                    yAxis);

            if (
                resolvedSeparation <
                minimumSeparation
            )
            {
                mouthResolvedTarget +=
                    yAxis *
                    (
                        minimumSeparation -
                        resolvedSeparation
                    );

                debugMouthAnatomyClamped =
                    true;
            }

            float minimumEyeDistance =
                eyeSpan *
                minimumMouthEyeCenterDistanceEyeSpan;

            EnforceMinimumEyeDistance(
                pairLeftTarget,
                pairMid,
                yAxis,
                minimumEyeDistance,
                ref mouthResolvedTarget);

            EnforceMinimumEyeDistance(
                pairRightTarget,
                pairMid,
                yAxis,
                minimumEyeDistance,
                ref mouthResolvedTarget);

            float finalSeparation =
                Mathf.Max(
                    0.0001f,
                    Vector2.Dot(
                        mouthResolvedTarget -
                            pairMid,
                        yAxis));

            debugMouthSeparationRatio =
                finalSeparation /
                Mathf.Max(
                    0.0001f,
                    baseSeparation);
        }

        _targetCorrection[
            MouthPart] =
            mouthResolvedTarget -
            baseMouth;

        _matchConfidence[
            MouthPart] =
            Mathf.Max(
                mouthConfidence,
                pairConfidence *
                    0.62f);

        _targetCorrection[
            LeftPart] =
            ClampCorrectionToRect(
                _targetCorrection[
                    LeftPart],
                leftRect);

        _targetCorrection[
            RightPart] =
            ClampCorrectionToRect(
                _targetCorrection[
                    RightPart],
                rightRect);

        _targetCorrection[
            MouthPart] =
            ClampCorrectionToRect(
                _targetCorrection[
                    MouthPart],
                mouthRect);
    }

    private Vector2 ClampCorrectionToRect(
        Vector2 correction,
        Rect rect)
    {
        float maxX =
            rect.width *
            maximumCorrectionCropFraction;

        float maxY =
            rect.height *
            maximumCorrectionCropFraction;

        correction.x =
            Mathf.Clamp(
                correction.x,
                -maxX,
                maxX);

        correction.y =
            Mathf.Clamp(
                correction.y,
                -maxY,
                maxY);

        return
            correction;
    }

    private void EnforceMinimumEyeDistance(
        Vector2 eyeTarget,
        Vector2 eyeMid,
        Vector2 yAxis,
        float minimumDistance,
        ref Vector2 mouthTarget)
    {
        Vector2 delta =
            mouthTarget -
            eyeTarget;

        float currentDistance =
            delta.magnitude;

        if (
            currentDistance >=
                minimumDistance ||
            minimumDistance <=
                0.0001f
        )
        {
            return;
        }

        float requiredPush =
            minimumDistance -
            currentDistance;

        float side =
            Vector2.Dot(
                mouthTarget -
                    eyeMid,
                yAxis) >=
                0f
                ? 1f
                : -1f;

        mouthTarget +=
            yAxis *
            (
                requiredPush *
                side
            );

        debugMouthAnatomyClamped =
            true;
    }

    private static Vector2 RotateVector(
        Vector2 vector,
        float degrees)
    {
        float radians =
            degrees *
            Mathf.Deg2Rad;

        float c =
            Mathf.Cos(
                radians);

        float s =
            Mathf.Sin(
                radians);

        return
            new Vector2(
                vector.x *
                    c -
                vector.y *
                    s,
                vector.x *
                    s +
                vector.y *
                    c);
    }

    private static float NormalizeSignedAngle(
        float angle)
    {
        while (angle > 180f)
        {
            angle -=
                360f;
        }

        while (angle < -180f)
        {
            angle +=
                360f;
        }

        return
            angle;
    }


    private void UpdateRenderedCorrections(
        float dt)
    {
        float correctionAge =
            CorrectionAgeSeconds;

        bool fresh =
            correctionAge <=
            maximumCorrectionHoldSeconds;

        float continuityStrength =
            ResolveContinuityStrength();

        for (
            int i = 0;
            i < PartCount;
            i++
        )
        {
            bool accepted =
                fresh &&
                _matchConfidence[i] >=
                    minimumMatchConfidence;

            Vector2 target =
                accepted
                    ? _targetCorrection[i] *
                        continuityStrength
                    : Vector2.zero;

            float response =
                accepted
                    ? acceptedCorrectionResponse
                    : rejectedCorrectionReturnResponse;

            float t =
                1f -
                Mathf.Exp(
                    -response *
                    Mathf.Clamp(
                        dt,
                        1f / 500f,
                        0.05f));

            _renderCorrection[i] =
                Vector2.Lerp(
                    _renderCorrection[i],
                    target,
                    t);
        }
    }

    private void DecayCorrectionsToZero(
        float dt)
    {
        float t =
            1f -
            Mathf.Exp(
                -rejectedCorrectionReturnResponse *
                Mathf.Clamp(
                    dt,
                    1f / 500f,
                    0.05f));

        for (
            int i = 0;
            i < PartCount;
            i++
        )
        {
            _targetCorrection[i] =
                Vector2.zero;

            _renderCorrection[i] =
                Vector2.Lerp(
                    _renderCorrection[i],
                    Vector2.zero,
                    t);

            _matchConfidence[i] =
                0f;
        }
    }

    private void ApplyCorrections()
    {
        if (cropper == null)
        {
            return;
        }

        ApplyCorrection(
            cropper.leftEyeImage,
            _renderCorrection[
                LeftPart]);

        ApplyCorrection(
            cropper.rightEyeImage,
            _renderCorrection[
                RightPart]);

        ApplyCorrection(
            cropper.mouthImage,
            _renderCorrection[
                MouthPart]);
    }

    private static void ApplyCorrection(
        RawImage image,
        Vector2 correction)
    {
        if (image == null)
        {
            return;
        }

        Rect rect =
            image.uvRect;

        Vector2 center =
            rect.center +
            correction;

        rect.center =
            center;

        image.uvRect =
            rect;
    }

    private float ResolvePartQuality(
        int outputPart)
    {
        if (dualDomain == null)
        {
            return 1f;
        }

        if (
            outputPart ==
            MouthPart
        )
        {
            return
                dualDomain.MouthQuality;
        }

        bool outputLeft =
            outputPart ==
            LeftPart;

        bool semanticLeft =
            cropper == null ||
            !cropper.swapEyes
                ? outputLeft
                : !outputLeft;

        return
            semanticLeft
                ? dualDomain.LeftEyeQuality
                : dualDomain.RightEyeQuality;
    }

    private float ResolveContinuityStrength()
    {
        if (continuity == null)
        {
            return 1f;
        }

        switch (
            continuity.State)
        {
            case KiwiTrackingContinuityState.ContinuityState.Stable:
                return 1f;

            case KiwiTrackingContinuityState.ContinuityState.Degraded:
                return 0.78f;

            case KiwiTrackingContinuityState.ContinuityState.Reacquiring:
                return 0.42f;

            default:
                return 0f;
        }
    }

    private int ResolveActiveSearchRadius()
    {
        int maximum =
            Mathf.Max(
                1,
                _allocatedSearchRadius);

        if (!adaptiveSearchRadius)
        {
            debugActiveSearchRadius =
                maximum;

            return maximum;
        }

        int minimum =
            Mathf.Clamp(
                restingSearchRadiusPixels,
                1,
                maximum);

        float risk =
            adaptiveContainment != null
                ? adaptiveContainment.MotionRisk
                : 0.45f;

        float normalizedRisk =
            Mathf.InverseLerp(
                minimumSearchRisk,
                Mathf.Max(
                    minimumSearchRisk +
                        0.001f,
                    fullSearchRisk),
                risk);

        int active =
            Mathf.Clamp(
                Mathf.RoundToInt(
                    Mathf.Lerp(
                        minimum,
                        maximum,
                        normalizedRisk)),
                minimum,
                maximum);

        debugActiveSearchRadius =
            active;

        return active;
    }

    private int FindFreeSlot()
    {
        for (
            int i = 0;
            i < _slots.Length;
            i++
        )
        {
            if (
                !_slots[i].pending &&
                !_slots[i].completedReady &&
                _slots[i].buffer !=
                    null
            )
            {
                return i;
            }
        }

        return -1;
    }

    private int CountPendingReadbacks()
    {
        int count =
            0;

        for (
            int i = 0;
            i < _slots.Length;
            i++
        )
        {
            if (_slots[i].pending)
            {
                count++;
            }
        }

        return count;
    }

    private void SwapFrameTextures()
    {
        RenderTexture temp =
            _previousFrame;

        _previousFrame =
            _currentFrame;

        _currentFrame =
            temp;
    }

    private static Vector2 ToGpuUv(
        Vector2 uv,
        bool uvStartsAtTop)
    {
        return
            new Vector2(
                uv.x,
                uvStartsAtTop
                    ? 1f -
                        uv.y
                    : uv.y);
    }

    private void RefreshReferences(
        bool force)
    {
        if (
            force ||
            cropper == null
        )
        {
            cropper =
                FindFirstObjectByType<
                    FacePartCropper>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            dualDomain == null
        )
        {
            dualDomain =
                FindFirstObjectByType<
                    KiwiDualDomainFaceQuality>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            continuity == null
        )
        {
            continuity =
                FindFirstObjectByType<
                    KiwiTrackingContinuityState>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            adaptiveContainment == null
        )
        {
            adaptiveContainment =
                FindFirstObjectByType<
                    KiwiFacePartAdaptiveContainment>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            latencyBudget == null
        )
        {
            latencyBudget =
                FindFirstObjectByType<
                    KiwiLatencyBudgetController>(
                    FindObjectsInactive.Include);
        }
    }

    private void ReleaseResources()
    {
        _generation++;

        // Wait only for this component's requests. Global WaitAllRequests can
        // stall unrelated capture/render systems and is unsuitable for a
        // commercial multi-provider runtime.
        for (
            int i = 0;
            i < _slots.Length;
            i++
        )
        {
            ReadbackSlot slot =
                _slots[i];

            if (slot.pending)
            {
                try
                {
                    slot.request.WaitForCompletion();
                }
                catch
                {
                    // Teardown must continue even if the graphics device is
                    // already shutting down.
                }
            }
        }

        ReleaseGpuOnly();

        _matcher =
            null;

        _kernel =
            -1;

        _hasPreviousFrame =
            false;

        _hasPreviousCenters =
            false;
    }

    private void ReleaseGpuOnly()
    {
        for (
            int i = 0;
            i < _slots.Length;
            i++
        )
        {
            if (
                _slots[i].buffer !=
                null
            )
            {
                _slots[i].buffer.Release();
                _slots[i].buffer = null;
            }

            _slots[i].pending =
                false;

            _slots[i].completedReady =
                false;

            _slots[i].costMailbox =
                null;
        }

        ReleaseTexture(
            ref _previousFrame);

        ReleaseTexture(
            ref _currentFrame);
    }

    private static void ReleaseTexture(
        ref RenderTexture texture)
    {
        if (texture == null)
        {
            return;
        }

        if (texture.IsCreated())
        {
            texture.Release();
        }

        UnityEngine.Object.Destroy(
            texture);

        texture =
            null;
    }

    private void ResetCorrections()
    {
        for (
            int i = 0;
            i < PartCount;
            i++
        )
        {
            _decodedCorrection[i] =
                Vector2.zero;

            _decodedConfidence[i] =
                0f;

            _targetCorrection[i] =
                Vector2.zero;

            _renderCorrection[i] =
                Vector2.zero;

            _matchConfidence[i] =
                0f;
        }

        debugEyePairRollDegrees =
            0f;

        debugEyePairRotationDeltaDegrees =
            0f;

        debugEyePairScale =
            1f;

        debugEyePairFallback =
            false;

        debugMouthAnatomyClamped =
            false;

        debugMouthSeparationRatio =
            1f;

        _latestCorrectionHostTicks =
            0L;
    }

    private void UpdateDiagnostics()
    {
        debugOperational =
            CanRun() &&
            _matcher != null &&
            _kernel >=
                0;

        debugTrackingWidth =
            _trackingWidth;

        debugTrackingHeight =
            _trackingHeight;

        debugPendingReadbacks =
            CountPendingReadbacks();

        debugMatchRateHz =
            _matchRateHz;

        debugReadbackLatencyMs =
            _readbackLatencyMs;

        debugCorrectionAgeMs =
            CorrectionAgeSeconds *
            1000f;

        debugMaximumConcurrentReadbacks =
            ResolveMaximumConcurrentReadbacks();

        debugStaleReadbackDrops =
            _staleReadbackDrops;

        debugOverloadSuspended =
            IsOverloadSuspended;

        debugOverloadSuspensions =
            _overloadSuspensions;

        debugConsecutiveStaleReadbacks =
            _consecutiveStaleReadbacks;

        debugLeftConfidence =
            _matchConfidence[
                LeftPart];

        debugRightConfidence =
            _matchConfidence[
                RightPart];

        debugMouthConfidence =
            _matchConfidence[
                MouthPart];

        debugLeftCorrection =
            _renderCorrection[
                LeftPart];

        debugRightCorrection =
            _renderCorrection[
                RightPart];

        debugMouthCorrection =
            _renderCorrection[
                MouthPart];
    }
}
