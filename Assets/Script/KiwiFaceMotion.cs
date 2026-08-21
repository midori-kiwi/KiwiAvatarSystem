using UnityEngine;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;


// FaceLandmarkerの結果がそのフレーム内で更新されたあとに
// できるだけ遅いタイミングで最新値を取得する。
[DefaultExecutionOrder(10000)]
public class KiwiFaceMotion : MonoBehaviour
{
    // =========================================================
    // References
    // =========================================================

    [Header("References")]
    public FaceLandmarkerRunner runner;
    public Transform kiwiRoot;
    public KiwiExpressionReaction expressionReaction;


    // =========================================================
    // Existing Project Compatibility
    //
    // These fields intentionally keep the serialized names used by the
    // completed pre-v3.7 project. This prevents Inspector data loss when
    // upgrading the same MonoScript GUID in-place. Hybrid Precision remains
    // the tracking core; screen-space mapping stays available because it is
    // part of the established camera-relative motion behavior.
    // =========================================================

    [Header("Existing Project Compatibility")]

    [Tooltip("Serialized compatibility flag from the completed Strict Landmarker setup. Hybrid Precision remains Landmarker-primary and does not add temporal pose smoothing.")]
    public bool strictLandmarkerTracking = true;

    [Tooltip("Allow the final render-time precision latch/prediction pass.")]
    public bool useBeforeRenderLateLatch = true;

    [Tooltip("Map tracked X/Y movement through the VTuber output camera so motion remains stable across model size/FOV changes.")]
    public bool useScreenSpacePositionMapping = true;

    [Tooltip("Camera used for screen-space position mapping. VTuberCamera is auto-resolved when empty.")]
    public Camera positionReferenceCamera;

    [Range(0f, 3f)]
    public float screenPositionGainX = 1.00f;

    [Range(0f, 3f)]
    public float screenPositionGainY = 1.00f;

    [Tooltip("Move in the avatar's own horizontal direction: tracked right becomes Kiwi-right (viewer-left for a front-facing avatar).")]
    public bool avatarCentricHorizontalMovement = true;


    // =========================================================
    // MASTER
    // =========================================================

    [Header("MASTER MULTIPLIERS")]

    [Tooltip("通常の顔追従倍率")]
    [Range(0f, 3f)]
    public float faceMotionMultiplier = 1.0f;

    [Tooltip("前後移動倍率")]
    [Range(1f, 8f)]
    public float depthMovementMultiplier = 5.0f;

    [Tooltip("表情・リアクション倍率")]
    [Range(1f, 8f)]
    public float reactionMultiplier = 5.0f;


    // =========================================================
    // Rotation
    // =========================================================

    [Header("Rotation")]

    [Range(0f, 2f)]
    public float pitchGain = 1.0f;

    [Range(0f, 2f)]
    public float yawGain = 1.0f;

    [Range(0f, 2f)]
    public float rollGain = 1.0f;


    [Header("Rotation Limit")]

    [Range(0f, 90f)]
    public float maxPitch = 45f;

    [Range(0f, 90f)]
    public float maxYaw = 60f;

    [Range(0f, 90f)]
    public float maxRoll = 50f;


    // =========================================================
    // Optional Head Boost
    //
    // 基本1倍なので全部1.0
    // =========================================================

    [Header("Head Tracking Extra Boost")]

    [Range(1f, 3f)]
    public float pitchReactionBoost = 1.0f;

    [Range(1f, 3f)]
    public float yawReactionBoost = 1.0f;

    [Range(1f, 3f)]
    public float rollReactionBoost = 1.0f;

    [Range(0f, 20f)]
    public float reactionBoostStart = 3f;

    [Range(3f, 50f)]
    public float reactionBoostFull = 15f;


    // =========================================================
    // LANDMARK SPEED MODE
    //
    // ★重要
    //
    // 時間方向の平滑化を一切行わない。
    //
    // 静止ノイズだけHold。
    // Hold条件を外れた瞬間に最新値へ100% Snap。
    // =========================================================

    [Header("LandMarker Speed Tracking")]

    [Tooltip("LandMarker並みの超低遅延追従")]
    public bool landMarkerSpeedMode = true;


    // =========================================================
    // Ultra Low Latency Tracking v1.0.0
    //
    // LandMarker accepted sample is still the source of truth.
    // Large/intentional motion stays direct; only the microscopic
    // noise corridor is filtered. A real result arriving after
    // LateUpdate can be consumed again in onBeforeRender, then a
    // bounded motion-only prediction may compensate the remaining age.
    // =========================================================

    [Header("Ultra Low Latency Tracking v1.0.0")]
    [Tooltip("BigMouse-style low-latency / low-jitter tracking path. Recommended.")]
    public bool enableUltraLowLatencyTracking = true;

    [Tooltip("Use Runner's virtual-neck faceCenter for body X/Y. Keeps Roll from creating a U-shaped translation arc and keeps eye/mouth coordinates coherent.")]
    public bool ultraUseRunnerPositionAnchor = true;

    [Tooltip("Consume a newer real LandMarker sample in onBeforeRender before using any prediction.")]
    public bool ultraConsumeLatestSampleBeforeRender = true;

    [Tooltip("Keep body wobble/bounce/squash out of the core pose. Eye/mouth expressions themselves remain active.")]
    public bool ultraDisableSecondaryBodyMotion = true;

    [Tooltip("Filter only microscopic motion. Intentional/fast movement snaps to the newest LandMarker result.")]
    public bool ultraAdaptiveMicroFilter = true;

    [Tooltip("After a few truly stable samples, lock only the microscopic rest pose. The lock releases from accumulated real motion, so slow movement cannot be held forever.")]
    public bool ultraStaticPoseLock = true;

    [Tooltip("Time that the raw target must remain inside one fixed microscopic corridor before rest lock engages. Time-based so 30/60/120 fps behave consistently.")]
    [Range(0.03f, 0.20f)] public float ultraStaticLockSeconds = 0.065f;

    [Header("Ultra Rotation Micro Jitter")]
    [Range(0f, 0.5f)] public float ultraRotationDeadZone = 0.18f;
    [Range(0f, 80f)] public float ultraRotationStaticReleaseSpeed = 18f;
    [Range(10f, 300f)] public float ultraRotationSlowResponse = 120f;
    [Range(20f, 400f)] public float ultraRotationFastResponse = 220f;
    [Range(20f, 400f)] public float ultraRotationDirectSpeed = 110f;
    [Range(0.1f, 5f)] public float ultraRotationDirectError = 1.20f;

    [Header("Ultra Position Micro Jitter")]
    [Range(0f, 0.005f)] public float ultraPositionDeadZone = 0.00060f;
    [Range(0f, 0.5f)] public float ultraPositionStaticReleaseSpeed = 0.035f;
    [Range(10f, 300f)] public float ultraPositionSlowResponse = 80f;
    [Range(20f, 400f)] public float ultraPositionFastResponse = 180f;
    [Range(0.01f, 2f)] public float ultraPositionDirectSpeed = 0.150f;
    [Range(0.0001f, 0.02f)] public float ultraPositionDirectError = 0.00150f;

    [Header("Ultra Depth Micro Jitter")]
    [Range(0f, 0.02f)] public float ultraScaleDeadZone = 0.00150f;
    [Range(0f, 2f)] public float ultraScaleStaticReleaseSpeed = 0.200f;
    [Range(10f, 300f)] public float ultraScaleSlowResponse = 80f;
    [Range(20f, 400f)] public float ultraScaleFastResponse = 160f;
    [Range(0.05f, 4f)] public float ultraScaleDirectSpeed = 0.500f;
    [Range(0.001f, 0.08f)] public float ultraScaleDirectError = 0.0120f;

    [Header("Ultra Motion Prediction")]
    [Range(0f, 1f)] public float ultraPredictionStrength = 1.00f;
    [Range(0.002f, 0.150f)] public float ultraMaxPredictionSeconds = 0.100f;
    [Range(0f, 100f)] public float ultraPredictionMinRotationSpeed = 3f;
    [Range(0f, 0.5f)] public float ultraPredictionMinPositionSpeed = 0.005f;
    [Range(0f, 1f)] public float ultraPredictionMinScaleSpeed = 0.020f;

    [Tooltip("Compensate the complete measured result age instead of limiting lead to one LandMarker interval. Consistency, stale-time and absolute motion caps remain active.")]
    public bool ultraCompensateFullResultAge = true;

    [Tooltip("Also compensate the estimated camera exposure midpoint age that WebCamTexture timestamps cannot report directly.")]
    public bool ultraCompensateCameraCaptureAge = true;

    [Range(0f, 1.5f)] public float ultraCaptureIntervalFraction = 0.50f;
    [Range(0f, 0.050f)] public float ultraMaxCaptureAgeSeconds = 0.020f;

    [Header("Ultra Display-Rate Smoothing")]
    [Tooltip("Resample accepted LandMarker poses at the display frame rate. This removes low-inference-fps stair steps without changing the accepted tracking source.")]
    public bool ultraDisplayRateSmoothing = true;

    [Tooltip("Base display response. Lower is smoother; higher is faster. Prediction compensates most of the small smoothing delay.")]
    [Range(15f, 240f)] public float ultraDisplaySmoothingResponse = 90f;

    [Tooltip("Maximum response during large intentional motion. Bounded so fast turns remain continuous instead of snapping.")]
    [Range(30f, 400f)] public float ultraDisplayFastResponse = 220f;

    [Header("Ultra Zero-Lag Motion Bypass")]
    [Tooltip("During intentional movement, apply the newest render-time predicted pose directly while retaining display smoothing at rest.")]
    public bool ultraDirectDisplayDuringMotion = true;

    [Range(0f, 80f)] public float ultraDirectDisplayRotationSpeed = 18f;
    [Range(0f, 0.5f)] public float ultraDirectDisplayPositionSpeed = 0.035f;
    [Range(0f, 1f)] public float ultraDirectDisplayScaleSpeed = 0.200f;

    [Header("Ultra Predictive Translation Resampling")]
    [Tooltip("Advance body translation continuously from measured velocity, then blend only the newest LandMarker correction. Removes position lag without sample-arrival flicker.")]
    public bool ultraPredictivePositionResampling = true;

    [Tooltip("Correction response for new position samples. Feed-forward motion itself remains immediate.")]
    [Range(20f, 240f)] public float ultraPositionCorrectionResponse = 45f;

    [Tooltip("Fast correction used only when velocity loses consistency at a stop, acceleration, or reversal.")]
    [Range(45f, 400f)] public float ultraPositionRecoveryResponse = 180f;


    // =========================================================
    // Landmarker Primary Hybrid Precision Tracking
    //
    // Landmarkerを主役のまま維持し、
    // 同一Timestamp取得 / 低信頼スパイク除外 / 深度融合 /
    // 描画時刻までの短時間Late Predictionだけを追加する。
    // =========================================================

    [Header("Landmarker Primary Hybrid Precision Tracking")]

    [Tooltip("Landmarker Primaryのハイブリッド精密追従を有効化します。")]
    public bool enableHybridPrecisionTracking = true;

    [Tooltip("低信頼かつ不自然に大きい1フレーム変化だけを除外します。")]
    public bool enablePrecisionOutlierGuard = true;

    [Tooltip("Accept every high-quality latest position directly. Only low-quality channel spikes are rate-limited, so genuine fast translation never builds a correction backlog.")]
    public bool useBoundedLatestResultCorrection = true;

    [Range(0f, 1f)]
    public float precisionOutlierQualityThreshold = 0.45f;

    [Range(300f, 2000f)]
    public float precisionAngularOutlierSpeed = 950f;

    [Range(0.1f, 3f)]
    public float precisionPositionOutlierSpeed = 1.35f;

    [Range(0.5f, 8f)]
    public float precisionDepthOutlierSpeed = 4.0f;

    [Tooltip("2D eye spanだけでなく3D eye span/顔幅/顔高も使って前後距離を安定化します。")]
    public bool usePrecisionDepthFusion = true;


    [Header("Precision Render-Time Late Prediction")]

    [Tooltip("描画直前に、取得済み速度から処理時間分だけ補償します。")]
    public bool enableRenderTimeLatePrediction = true;

    [Range(0f, 1f)]
    public float predictionStrength = 0.80f;

    [Range(0.003f, 0.040f)]
    public float maxPredictionSeconds = 0.022f;

    [Range(0.030f, 0.300f)]
    public float predictionStaleTime = 0.180f;

    [Range(0f, 1f)]
    public float predictionMinQuality = 0.45f;

    [Range(5f, 100f)]
    public float predictionVelocityResponse = 60f;

    [Tooltip("Fast velocity-estimate response used only while consecutive LandMarker motion is directionally consistent.")]
    [Range(60f, 400f)]
    public float predictionVelocityFastResponse = 180f;

    [Range(0f, 30f)]
    public float maxRotationPredictionDegrees = 16.0f;

    [Range(0f, 0.08f)]
    public float maxPositionPredictionHeight = 0.040f;

    [Range(0f, 0.08f)]
    public float maxScalePrediction = 0.045f;


    // =========================================================
    // Rotation Jitter Hold
    // =========================================================

    [Header("Rotation Static Jitter Hold")]

    [Tooltip("静止中、この角度以内の差だけHold")]
    [Range(0f, 1f)]
    public float rotationStaticDeadZone = 0.10f;

    [Tooltip("この角速度を超えたらDeadZoneを即解除")]
    [Range(0f, 50f)]
    public float rotationDeadZoneReleaseSpeed = 4.0f;


    // =========================================================
    // Position
    // =========================================================

    [Header("Position Tracking")]

    public bool enablePosition = true;

    [Range(0f, 3f)]
    public float positionGainX = 0.55f;

    [Range(0f, 3f)]
    public float positionGainY = 0.40f;


    // =========================================================
    // Roll-stable position anchor
    //
    // 首を傾けたときのRollを、左右移動として誤検出しないための
    // 位置追従専用アンカー。正面キャリブレーション時の顔形状から
    // 仮想的な首の支点を作り、その支点をRollに合わせて回転させる。
    // =========================================================

    [Header("Position Roll Isolation")]

    [Tooltip("首の傾きによる見かけ上の左右移動を分離します。ON推奨。")]
    public bool useRollStablePositionAnchor = true;

    [Tooltip("目の中心から顎方向へ延長して仮想的な首の支点を作る倍率。")]
    [Range(1.0f, 2.5f)]
    public float virtualNeckExtension = 1.30f;

    [Tooltip("1でRoll由来の左右移動を最大限分離します。")]
    [Range(0f, 1f)]
    public float rollIsolationStrength = 1.0f;


    [Header("Position Static Jitter Hold")]

    [Tooltip("モデル身長に対する静止DeadZone")]
    [Range(0f, 0.01f)]
    public float positionStaticDeadZone = 0.00025f;

    [Tooltip("顔中心がこの速度以上なら即追従")]
    [Range(0f, 0.2f)]
    public float positionDeadZoneReleaseSpeed = 0.006f;


    // =========================================================
    // Screen-space position mapping
    //
    // Camera-relative mapping is retained from the completed project.
    // It makes X/Y response independent of avatar height and output-camera FOV.
    // =========================================================

    private Vector3 CalculateScreenMappedPosition(
        Vector2 inputDelta)
    {
        if (positionReferenceCamera == null)
        {
            positionReferenceCamera =
                ResolvePositionReferenceCamera();
        }

        if (positionReferenceCamera == null)
        {
            return CalculateLegacyPosition(
                inputDelta
            );
        }

        Camera cam = positionReferenceCamera;
        Transform parent = kiwiRoot.parent;

        Vector3 baseWorldPosition =
            parent != null
                ? parent.TransformPoint(_basePosition)
                : _basePosition;

        float depth =
            Vector3.Dot(
                baseWorldPosition - cam.transform.position,
                cam.transform.forward
            );

        depth = Mathf.Max(
            depth,
            cam.nearClipPlane + 0.05f
        );

        Vector3 viewportBase =
            new Vector3(
                0.5f,
                0.5f,
                depth
            );

        // Horizontal direction is already converted to avatar-centric space in
        // UpdatePositionSample when requested. For a front-facing Kiwi, own-right
        // appears on the viewer's left.
        // MediaPipe Y grows downward, so Y remains inverted for Unity viewport space.
        Vector3 viewportMoved =
            new Vector3(
                0.5f + inputDelta.x * screenPositionGainX,
                0.5f - inputDelta.y * screenPositionGainY,
                depth
            );

        Vector3 worldDelta =
            cam.ViewportToWorldPoint(viewportMoved)
            -
            cam.ViewportToWorldPoint(viewportBase);

        Vector3 targetWorldPosition =
            baseWorldPosition + worldDelta;

        return
            parent != null
                ? parent.InverseTransformPoint(targetWorldPosition)
                : targetWorldPosition;
    }


    private Vector3 CalculateLegacyPosition(
        Vector2 delta)
    {
        return
            _basePosition
            +
            new Vector3(
                delta.x * _modelHeight * positionGainX,
                -delta.y * _modelHeight * positionGainY,
                0f
            );
    }


    private Camera ResolvePositionReferenceCamera()
    {
        Camera[] cameras =
            FindObjectsByType<Camera>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

        for (int i = 0; i < cameras.Length; i++)
        {
            Camera cam = cameras[i];
            if (cam != null && cam.name == "VTuberCamera")
            {
                return cam;
            }
        }

        for (int i = 0; i < cameras.Length; i++)
        {
            Camera cam = cameras[i];
            if (cam != null && cam.targetTexture != null)
            {
                return cam;
            }
        }

        return Camera.main;
    }


    // =========================================================
    // Depth
    // =========================================================

    [Header("Depth Tracking - 5x")]

    public bool enableDistanceScale = true;

    [Range(0f, 2f)]
    public float distanceScaleGain = 0.35f;

    [Range(0.3f, 1f)]
    public float minimumScale = 0.65f;

    [Range(1f, 3f)]
    public float maximumScale = 1.60f;


    [Header("Depth Static Jitter Hold")]

    [Range(0f, 0.02f)]
    public float scaleStaticDeadZone = 0.0008f;

    [Range(0f, 0.2f)]
    public float scaleDeadZoneReleaseSpeed = 0.004f;


    // =========================================================
    // Motion Accent
    //
    // 基本Trackingには影響しない。
    // 速度に応じて後から勢いだけ追加。
    // =========================================================

    [Header("Motion Accent")]

    public bool enableMotionAccent = true;

    [Range(1f, 100f)]
    public float motionAccentVelocityResponse = 28f;

    [Range(1f, 100f)]
    public float motionAccentResponse = 22f;

    [Range(1f, 100f)]
    public float motionAccentRelease = 14f;


    [Range(30f, 500f)]
    public float motionAccentYawFullSpeed = 180f;

    [Range(30f, 500f)]
    public float motionAccentPitchFullSpeed = 150f;

    [Range(30f, 500f)]
    public float motionAccentRollFullSpeed = 180f;


    [Range(0f, 10f)]
    public float motionAccentYawAmount = 1.6f;

    [Range(0f, 10f)]
    public float motionAccentPitchAmount = 1.3f;

    [Range(0f, 10f)]
    public float motionAccentRollFromYaw = 2.0f;

    [Range(0f, 10f)]
    public float motionAccentRollAmount = 1.2f;

    [Range(0f, 0.10f)]
    public float motionAccentStretchAmount = 0.018f;


    // =========================================================
    // Surprise
    // =========================================================

    [Header("Surprise Jump")]

    public bool enableSurpriseJump = true;

    [Range(0f, 1f)]
    public float surpriseThreshold = 0.55f;

    [Range(0f, 0.5f)]
    public float surpriseJumpHeight = 0.10f;

    [Range(1f, 20f)]
    public float surpriseJumpFrequency = 7.5f;

    [Range(1f, 30f)]
    public float surpriseJumpDecay = 10f;

    [Range(0.05f, 2f)]
    public float surpriseCooldown = 0.45f;

    [Range(0.1f, 1.5f)]
    public float surpriseJumpDuration = 0.45f;


    [Header("Surprise Squash Stretch")]

    public bool enableSurpriseSquash = true;

    [Range(0f, 0.4f)]
    public float surpriseStretchAmount = 0.15f;

    [Range(0f, 0.3f)]
    public float surpriseSquashAmount = 0.10f;

    [Range(0f, 0.5f)]
    public float surpriseStretchHoldTime = 0.10f;

    [Range(1f, 15f)]
    public float squashStretchFrequency = 4.8f;

    [Range(1f, 30f)]
    public float squashStretchDecay = 5.5f;

    [Range(0.2f, 2f)]
    public float surpriseReactionDuration = 0.95f;


    // =========================================================
    // Happy
    // =========================================================

    [Header("Happy Wiggle")]

    public bool enableHappyWiggle = true;

    [Range(0f, 20f)]
    public float happyRollAmount = 8f;

    [Range(0f, 15f)]
    public float happyYawAmount = 3.5f;

    [Range(0.2f, 5f)]
    public float happyWiggleSpeed = 1.8f;

    [Range(0.5f, 3f)]
    public float happyIntensityPower = 1.35f;


    // =========================================================
    // Blink
    // =========================================================

    [Header("Blink Squash")]

    public bool enableBlinkSquash = true;

    [Range(0f, 0.20f)]
    public float blinkSquashAmount = 0.055f;

    [Range(0.5f, 3f)]
    public float blinkSquashPower = 1.25f;


    // =========================================================
    // Talking
    // =========================================================

    [Header("Talking Motion")]

    public bool enableTalkingMotion = true;

    [Range(0f, 0.05f)]
    public float talkBounceHeight = 0.012f;

    [Range(0f, 10f)]
    public float talkRollAmount = 1.25f;

    [Range(0f, 0.10f)]
    public float talkStretchAmount = 0.012f;

    [Range(0.5f, 10f)]
    public float talkWiggleSpeed = 3.4f;


    // =========================================================
    // Pout
    // =========================================================

    [Header("Pout Puff")]

    public bool enablePoutPuff = true;

    [Range(0f, 0.10f)]
    public float poutPuffAmount = 0.025f;


    // =========================================================
    // Grumpy
    // =========================================================

    [Header("Grumpy Shake")]

    public bool enableGrumpyShake = true;

    [Range(0f, 10f)]
    public float grumpyYawAmount = 2.2f;

    [Range(0.5f, 10f)]
    public float grumpyShakeSpeed = 4.0f;

    [Range(0f, 0.05f)]
    public float grumpyDropAmount = 0.008f;


    // =========================================================
    // Idle
    // =========================================================

    [Header("Idle Life")]

    public bool enableIdleLife = true;

    [Range(0f, 0.05f)]
    public float idleBreathAmount = 0.012f;

    [Range(0.05f, 1f)]
    public float idleBreathSpeed = 0.28f;

    [Range(0f, 5f)]
    public float idleSwayRollAmount = 0.85f;

    [Range(0.05f, 1f)]
    public float idleSwaySpeed = 0.20f;

    [Range(0f, 0.03f)]
    public float idleBobAmount = 0.004f;


    // =========================================================
    // Calibration
    // =========================================================

    [Header("Calibration")]

    [Range(0.1f, 2f)]
    public float calibrationSeconds = 0.50f;

    [Range(3, 60)]
    public int minimumCalibrationSamples = 8;


    // =========================================================
    // Lost Tracking
    // =========================================================

    [Header("Tracking Lost")]

    [Range(0.05f, 2f)]
    public float trackingLostTime = 0.30f;

    [Range(1f, 30f)]
    public float returnToNeutralResponse = 5f;


    // =========================================================
    // Base
    // =========================================================

    private Vector3 _basePosition;
    private Quaternion _baseRotation = Quaternion.identity;
    private Vector3 _baseScale;

    private float _modelHeight = 1f;


    // =========================================================
    // Calibration
    // =========================================================

    private bool _calibrated;
    private bool _calibrationStarted;

    private float _calibrationStartTime;
    private int _calibrationSamples;

    private Vector2 _neutralCenter;
    private float _neutralEyeSpan;

    private float _neutralEyeSpan3D;
    private float _neutralFaceWidth2D;
    private float _neutralFaceHeight2D;
    private int _precisionGeometryCalibrationSamples;

    private Quaternion _neutralFaceRotation =
        Quaternion.identity;


    // =========================================================
    // Roll-stable position calibration geometry
    // =========================================================

    private int _positionGeometrySamples;

    private Vector2 _neutralEyeCenter;
    private Vector2 _neutralEyeLine;
    private Vector2 _neutralEyeToChin;

    private bool _hasNeutralPositionGeometry;


    private struct PositionGeometry
    {
        public Vector2 eyeCenter;
        public Vector2 eyeLine;
        public Vector2 eyeToChin;
    }


    // =========================================================
    // Tracking
    // =========================================================

    // Observed frame ID is used to avoid processing the same atomic snapshot
    // twice. Timestamp remains the timing source for velocity and prediction.
    private ulong _lastObservedFrameId;

    // Observed timestamp is retained as a compatibility fallback for snapshots
    // produced before frame IDs were introduced.
    // Accepted timestamp is used for velocity/dt. Keeping them separate prevents a
    // rejected spike from shortening the next accepted sample interval.
    private long _lastObservedTimestamp = -1;
    private long _lastAcceptedTimestamp = -1;
    private long _lastAcceptedSampleHostTicks;
    private bool _lastAcceptedUsedMatchedSubmissionTiming;
    private KiwiTrackingBackend _lastAcceptedBackend =
        KiwiTrackingBackend.Unknown;

    private float _lastSeenTime = -100f;

    private bool _trackingWasLost = true;


    // =========================================================
    // Latest Accepted Values
    // =========================================================

    private Quaternion _sampleRotation =
        Quaternion.identity;

    private Vector3 _samplePosition;

    private Vector3 _sampleScale =
        Vector3.one;

    // Accepted LandMarker samples may arrive below the display refresh rate.
    // This separate pose advances once per Unity frame toward the predicted
    // target, removing sample-and-hold stair steps from the rendered model.
    private bool _displayPoseInitialized;
    private Quaternion _displayRotation = Quaternion.identity;
    private Vector3 _displayPosition;
    private Vector3 _displayScale = Vector3.one;
    private long _lastDisplayAdvanceHostTicks;
    private Vector3 _renderPositionVelocity;


    // =========================================================
    // Raw previous values
    // =========================================================

    private Quaternion _lastRawRotation =
        Quaternion.identity;

    private Vector2 _lastRawCenter =
        new Vector2(
            0.5f,
            0.5f
        );

    private float _lastRawScaleFactor =
        1f;


    // =========================================================
    // Current raw speeds
    //
    // DeadZone解除には平滑化前の速度を使う。
    // ここが低遅延化の重要部分。
    // =========================================================

    private float _rawAngularSpeed;
    private float _rawPositionSpeed;
    private float _rawScaleSpeed;

    // Ultra rest-pose hysteresis. These anchors freeze only a microscopic
    // stationary corridor. Any accumulated real movement beyond 3x the
    // configured jitter zone releases immediately.
    private bool _ultraRotationStaticLocked;
    private bool _ultraPositionStaticLocked;
    private bool _ultraScaleStaticLocked;
    private float _ultraRotationStaticTime;
    private float _ultraPositionStaticTime;
    private float _ultraScaleStaticTime;
    private Quaternion _ultraRotationStaticAnchor = Quaternion.identity;
    private Vector3 _ultraPositionStaticAnchor;
    private Vector3 _ultraScaleStaticAnchor = Vector3.one;


    // =========================================================
    // Hybrid Precision State
    // =========================================================

    private bool _hasPrecisionInputHistory;
    private Quaternion _lastPrecisionInputRotation =
        Quaternion.identity;
    private Vector2 _lastPrecisionInputCenter;
    private float _lastPrecisionDepthRatio = 1f;

    // Rejected samples are tracked separately. A stable run of low-quality
    // candidates can be reacquired without letting a single spike through.
    private bool _hasPrecisionRejectedCandidate;
    private Quaternion _precisionRejectedRotation =
        Quaternion.identity;
    private Vector2 _precisionRejectedCenter;
    private float _precisionRejectedDepthRatio = 1f;
    private int _precisionRejectedStreak;
    private long _precisionRejectedHostTicks;
    private bool _precisionRejectedUsedMatchedSubmissionTiming;
    private long _precisionRejectedTimestamp = -1;

    private bool _hasPredictionHistory;
    private Quaternion _predictionPreviousRotation =
        Quaternion.identity;
    private Vector3 _predictionPreviousPosition;
    private Vector3 _predictionPreviousScale =
        Vector3.one;

    private Vector3 _predictionAngularVelocityDegrees;
    private Vector3 _predictionPositionVelocity;
    private Vector3 _predictionScaleVelocity;

    private bool _hasPredictionRawVelocityHistory;
    private Vector3 _predictionPreviousRawAngularVelocity;
    private Vector3 _predictionPreviousRawPositionVelocity;
    private Vector3 _predictionPreviousRawScaleVelocity;

    private float _predictionRotationConsistency;
    private float _predictionPositionConsistency;
    private float _predictionScaleConsistency;
    private float _lastAcceptedSampleInterval = 1f / 30f;

    private long _lastPrecisionSubmissionHostTicks;
    private long _lastPrecisionArrivalHostTicks;
    private float _lastPrecisionQuality = 1f;
    private float _lastPredictionAgeMs;
    private float _lastInferenceLatencyMs;
    private float _lastCaptureAgeCompensationMs;
    private int _lastBoundedCorrectionChannels;
    private int _boundedCorrectionCount;

    public float PrecisionGeometryQuality =>
        _lastPrecisionQuality;

    public float PrecisionPredictionAgeMs =>
        _lastPredictionAgeMs;

    public float PrecisionInferenceLatencyMs =>
        _lastInferenceLatencyMs;

    public float PrecisionEstimatedModelLatencyMs =>
        runner != null && runner.SentisPrimaryActive
            ? runner.LatestSentisLatencyMs
            : Mathf.Max(0f, _lastInferenceLatencyMs - PrecisionReadbackLatencyMs);

    public float PrecisionCaptureAgeCompensationMs =>
        _lastCaptureAgeCompensationMs;

    public int PrecisionBoundedCorrectionChannels =>
        _lastBoundedCorrectionChannels;

    public int PrecisionBoundedCorrectionCount =>
        _boundedCorrectionCount;

    public float PrecisionPredictionConsistency =>
        Mathf.Min(
            _predictionRotationConsistency,
            _predictionPositionConsistency
        );

    public float PrecisionTrackingRateHz =>
        runner != null ? runner.LatestTrackingResultRateHz : 0f;

    public float PrecisionSourceRateHz =>
        runner != null ? runner.LatestFreshSourceRateHz : 0f;

    public float PrecisionSubmissionRateHz =>
        runner != null ? runner.LatestSubmissionRateHz : 0f;

    public float PrecisionReadbackLatencyMs =>
        runner != null ? runner.LatestReadbackLatencyMs : 0f;

    public int PrecisionInputWidth =>
        runner != null ? runner.TrackingInputWidth : 0;

    public int PrecisionInputHeight =>
        runner != null ? runner.TrackingInputHeight : 0;

    public int PrecisionSourceWidth =>
        runner != null ? runner.SourceTextureWidth : 0;

    public int PrecisionSourceHeight =>
        runner != null ? runner.SourceTextureHeight : 0;

    public string PrecisionSourceName =>
        runner != null ? runner.SourceName : string.Empty;

    public float PrecisionRequestedSourceRateHz =>
        runner != null ? runner.SourceRequestedFrameRate : 0f;

    public bool PrecisionCm831ProfileActive =>
        runner != null && runner.Cm831ProfileActive;

    public string PrecisionTrackingBackend =>
        runner != null
            ? runner.ActiveTrackingBackend
            : "Unavailable";

    public bool PrecisionSentisPrimaryActive =>
        runner != null && runner.SentisPrimaryActive;

    public float PrecisionSentisLatencyMs =>
        runner != null ? runner.LatestSentisLatencyMs : 0f;

    public float PrecisionSentisPresence =>
        runner != null ? runner.LatestSentisPresence : 0f;

    public bool PrecisionInferenceEnginePrimaryActive =>
        runner != null && runner.InferenceEnginePrimaryActive;

    public float PrecisionInferenceEngineLatencyMs =>
        runner != null ? runner.LatestInferenceEngineLatencyMs : 0f;

    public float PrecisionInferenceEnginePresence =>
        runner != null ? runner.LatestInferenceEnginePresence : 0f;

    /// <summary>
    /// Yaw of the pose that is actually being rendered, relative to the
    /// calibrated model rotation. Face-part rendering uses this value so all
    /// parts share one coherent front-surface visibility decision.
    /// </summary>
    public float RenderedYawDegrees
    {
        get
        {
            Quaternion relative =
                Quaternion.Inverse(_baseRotation) *
                (_displayPoseInitialized ? _displayRotation : _sampleRotation);

            return SignedAngle(relative.eulerAngles.y);
        }
    }


    // =========================================================
    // Motion Accent
    // =========================================================

    private bool _hasPreviousAngles;

    private float _previousPitch;
    private float _previousYaw;
    private float _previousRoll;

    private float _signedPitchVelocity;
    private float _signedYawVelocity;
    private float _signedRollVelocity;

    private float _accentPitch;
    private float _accentYaw;
    private float _accentRoll;
    private float _accentStretch;

    private float _lastMotionSampleTime =
        -100f;


    // =========================================================
    // Surprise
    // =========================================================

    private float _previousSurprise;

    private bool _surpriseActive;

    private float _surpriseStartTime =
        -100f;

    private float _lastSurpriseTriggerTime =
        -100f;

    private float _surpriseStrength =
        0.70f;


    // =========================================================
    // Animation phases
    // =========================================================

    private float _happyPhase;
    private float _talkPhase;
    private float _grumpyPhase;

    private float _idleBreathPhase;
    private float _idleSwayPhase;


    // =========================================================
    // Render-time late latch lifecycle
    // =========================================================

    private void OnEnable()
    {
        Application.onBeforeRender +=
            OnBeforeRenderPrecision;
    }


    private void OnDisable()
    {
        Application.onBeforeRender -=
            OnBeforeRenderPrecision;
    }


    // =========================================================
    // Start
    // =========================================================

    private void Start()
    {
        if (kiwiRoot == null)
        {
            kiwiRoot = transform;
        }


        _basePosition =
            kiwiRoot.localPosition;

        _baseRotation =
            kiwiRoot.localRotation;

        _baseScale =
            kiwiRoot.localScale;


        _samplePosition =
            _basePosition;

        _sampleRotation =
            _baseRotation;

        _sampleScale =
            _baseScale;

        ResetDisplayPoseToSamples();


        _lastRawRotation =
            _baseRotation;


        _modelHeight =
            CalculateModelLocalHeight();


        if (
            useScreenSpacePositionMapping &&
            positionReferenceCamera == null
        )
        {
            positionReferenceCamera =
                ResolvePositionReferenceCamera();
        }


        BeginCalibration();
    }


    // =========================================================
    // LateUpdate
    //
    // 最終描画直前に最新MediaPipe結果を読む。
    // =========================================================

    private void LateUpdate()
    {
        if (
            runner == null ||
            kiwiRoot == null
        )
        {
            return;
        }


        // KIWI_V4_7_COMMERCIAL_RIGID_PHASE_AUTHORITY
        // LateUpdate and onBeforeRender must consume the same
        // canonical provider selection. Runner-direct access is
        // compatibility-only when the Hub is not installed.
        FacePrecisionTrackingData precisionData;

        bool hasTracking =
            KiwiCommercialRigidMotionPolicy.TryGetAuthoritativeFrame(
                runner,
                out precisionData);


        if (
            hasTracking &&
            IsNewPrecisionFrame(precisionData)
        )
        {
            bool hasPositionGeometry =
                TryGetPositionGeometry(
                    precisionData,
                    out PositionGeometry positionGeometry
                );


            Vector2 positionCenter =
                precisionData.faceCenter;


            if (
                (!enableUltraLowLatencyTracking || !ultraUseRunnerPositionAnchor) &&
                _calibrated &&
                useRollStablePositionAnchor &&
                _hasNeutralPositionGeometry &&
                hasPositionGeometry
            )
            {
                positionCenter =
                    CalculateRollStablePositionAnchor(
                        positionGeometry
                    );
            }


            bool accepted =
                ProcessNewSample(
                    positionCenter,
                    precisionData,
                    hasPositionGeometry,
                    positionGeometry
                );


            _lastObservedTimestamp =
                precisionData.timestamp;

            _lastObservedFrameId =
                precisionData.frameId;


            if (accepted)
            {
                _lastSeenTime =
                    Time.unscaledTime;

                _trackingWasLost =
                    false;
            }
        }


        float dt =
            Mathf.Clamp(
                Time.unscaledDeltaTime,
                1f / 500f,
                0.05f
            );


        if (!_calibrated)
        {
            return;
        }


        // KIWI_V4_7_CONTINUITY_HOLD_POLICY
        // A short inference/GPU stall holds the last trusted rigid
        // pose. Only continuity Lost returns the avatar to neutral.
        bool fallbackTrackingLost =
            Time.unscaledTime -
            _lastSeenTime
            >
            trackingLostTime;

        KiwiCommercialRigidMotionPolicy.ResolveLossPolicy(
            fallbackTrackingLost,
            out bool holdRigidPose,
            out bool trackingLost);

        if (holdRigidPose)
        {
            // Stop extrapolation immediately, but keep the last
            // rendered root pose. No neutral-return oscillation.
            ResetPredictionHistory();
            RenderDisplayPose();
            return;
        }

        if (trackingLost)
        {
            if (!_trackingWasLost)
            {
                ResetMotionAccent();
                ResetReactionState();
                ResetRejectedCandidateState();
                ResetPredictionHistory();
                ResetUltraStaticLocks();

                _trackingWasLost = true;
            }

            ReturnToNeutral(
                dt
            );

            return;
        }


        UpdateMotionAccent(
            dt
        );


        UpdateReactionState(
            dt
        );


        // LandMarker samples are targets; the display pose advances every Unity
        // frame so 20-30 fps inference does not appear as stepwise motion.
        UpdateAndRenderDisplayPose(dt);
    }


    // =========================================================
    // New MediaPipe Sample
    // =========================================================

    private bool ProcessNewSample(
        Vector2 center,
        FacePrecisionTrackingData precisionData,
        bool hasPositionGeometry,
        PositionGeometry positionGeometry)
    {
        bool sampleUsesMatchedSubmissionTiming;
        long sampleHostTicks =
            GetPrecisionSampleHostTicks(
                precisionData,
                out sampleUsesMatchedSubmissionTiming
            );


        if (!_calibrated)
        {
            AddCalibrationSample(
                center,
                precisionData,
                hasPositionGeometry,
                positionGeometry
            );

            _lastAcceptedTimestamp =
                precisionData.timestamp;

            _lastAcceptedSampleHostTicks =
                sampleHostTicks;

            _lastAcceptedUsedMatchedSubmissionTiming =
                sampleUsesMatchedSubmissionTiming;

            _lastAcceptedBackend =
                precisionData.backend;

            return true;
        }


        float sampleInterval =
            1f / 30f;


        if (
            _lastAcceptedSampleHostTicks > 0L &&
            sampleHostTicks > _lastAcceptedSampleHostTicks &&
            sampleUsesMatchedSubmissionTiming ==
                _lastAcceptedUsedMatchedSubmissionTiming
        )
        {
            // Never mix exact LIVE_STREAM submission timing with arrival fallback
            // timing across adjacent samples. A source switch would inject inference
            // latency jitter directly into velocity/outlier calculations.
            sampleInterval =
                (float)KiwiPrecisionTrackingMath.HostTicksToSeconds(
                    sampleHostTicks -
                    _lastAcceptedSampleHostTicks
                );
        }
        else if (
            _lastAcceptedTimestamp >= 0 &&
            precisionData.timestamp > _lastAcceptedTimestamp
        )
        {
            // Fallback for IMAGE/VIDEO or any callback whose submission timing
            // could not be matched to the LIVE_STREAM submission ring.
            sampleInterval =
                (
                    precisionData.timestamp -
                    _lastAcceptedTimestamp
                )
                /
                1000f;
        }


        bool backendChanged =
            _lastAcceptedBackend != KiwiTrackingBackend.Unknown &&
            precisionData.backend != KiwiTrackingBackend.Unknown &&
            precisionData.backend != _lastAcceptedBackend;


        bool predictionGap =
            backendChanged ||
            sampleInterval >
            Mathf.Max(
                0.10f,
                predictionStaleTime
            );


        float dt =
            Mathf.Clamp(
                sampleInterval,
                1f / 240f,
                0.10f
            );


        float depthRatio =
            CalculatePrecisionDepthRatio(
                precisionData
            );


        float quality =
            enableHybridPrecisionTracking
                ?
                CalculatePrecisionSampleQuality(
                    precisionData,
                    depthRatio
                )
                :
                1f;


        Vector2 guardedCenter = center;
        Quaternion guardedRotation = precisionData.faceRotation;
        float guardedDepthRatio = depthRatio;

        _lastBoundedCorrectionChannels = 0;
        if (
            !predictionGap &&
            useBoundedLatestResultCorrection
        )
        {
            _lastBoundedCorrectionChannels =
                ApplyQualityGatedLatestResultCorrection(
                    ref guardedCenter,
                    ref guardedRotation,
                    ref guardedDepthRatio,
                    quality,
                    dt
                );

            if (_lastBoundedCorrectionChannels != 0)
            {
                _boundedCorrectionCount++;
            }
        }

        // Legacy whole-pose rejection remains available for A/B diagnosis.
        // The default bounded path accepts the newest timestamp every time and
        // limits only the implausible channel, preventing 2-3 result intervals
        // of stale-pose latency at low Landmarker result rates.
        bool rejected =
            !useBoundedLatestResultCorrection &&
            !predictionGap &&
            ShouldRejectPrecisionSample(
                center,
                precisionData.faceRotation,
                depthRatio,
                quality,
                dt
            );


        if (rejected)
        {
            if (
                !ShouldReacquireRejectedSample(
                    center,
                    precisionData.faceRotation,
                    depthRatio,
                    quality,
                    sampleHostTicks,
                    sampleUsesMatchedSubmissionTiming,
                    precisionData.timestamp
                )
            )
            {
                return false;
            }

            // A coherent low-quality run likely represents real motion/recovery,
            // not a one-frame spike. Accept it, but discard old prediction
            // velocity so the reacquisition itself is never extrapolated.
            ResetPredictionHistory();
        }
        else
        {
            ResetRejectedCandidateState();
        }


        if (predictionGap)
        {
            ResetPredictionHistory();
        }


        UpdateRotationSample(
            guardedRotation,
            quality,
            dt
        );


        UpdatePositionSample(
            guardedCenter,
            quality,
            dt
        );


        UpdateScaleSample(
            guardedDepthRatio,
            quality,
            dt
        );


        _lastRawCenter =
            guardedCenter;


        _lastPrecisionInputRotation =
            guardedRotation;

        _lastPrecisionInputCenter =
            guardedCenter;

        _lastPrecisionDepthRatio =
            guardedDepthRatio;

        _hasPrecisionInputHistory =
            true;


        _lastAcceptedSampleInterval =
            Mathf.Clamp(
                sampleInterval,
                1f / 240f,
                0.25f
            );


        UpdatePredictionState(
            precisionData,
            quality,
            dt
        );


        _lastAcceptedTimestamp =
            precisionData.timestamp;

        _lastAcceptedSampleHostTicks =
            sampleHostTicks;

        _lastAcceptedUsedMatchedSubmissionTiming =
            sampleUsesMatchedSubmissionTiming;

        _lastAcceptedBackend =
            precisionData.backend;


        _lastMotionSampleTime =
            Time.unscaledTime;


        return true;
    }


    private long GetPrecisionSampleHostTicks(
        FacePrecisionTrackingData data,
        out bool usedMatchedSubmissionTiming)
    {
        if (
            data.hasMatchedSubmissionTiming &&
            data.submissionHostTicks > 0L
        )
        {
            usedMatchedSubmissionTiming = true;
            return data.submissionHostTicks;
        }


        usedMatchedSubmissionTiming = false;


        if (data.arrivalHostTicks > 0L)
        {
            return data.arrivalHostTicks;
        }


        return 0L;
    }


    private bool IsNewPrecisionFrame(FacePrecisionTrackingData data)
    {
        return data.frameId > 0UL
            ? data.frameId != _lastObservedFrameId
            : data.timestamp != _lastObservedTimestamp;
    }

    // =========================================================
    // Rotation
    // =========================================================

    private void UpdateRotationSample(
        Quaternion faceRotation,
        float quality,
        float dt)
    {
        Vector3 avatarEuler =
            KiwiPrecisionTrackingMath.CalculateAvatarEulerDegrees(
                _neutralFaceRotation,
                faceRotation,
                runner != null && runner.IsInputHorizontallyMirrored
            );


        float pitch = avatarEuler.x;


        // 自分が右を見る
        // → キウイ自身から見ても右
        float yaw = avatarEuler.y;


        // 右に首を傾ける -> キウイ自身から見て右に倒れる。
        float roll = avatarEuler.z;


        pitch =
            ApplyReactionBoost(
                pitch,
                pitchReactionBoost
            );


        yaw =
            ApplyReactionBoost(
                yaw,
                yawReactionBoost
            );


        roll =
            ApplyReactionBoost(
                roll,
                rollReactionBoost
            );


        pitch *=
            pitchGain *
            faceMotionMultiplier;


        yaw *=
            yawGain *
            faceMotionMultiplier;


        roll *=
            rollGain *
            faceMotionMultiplier;


        pitch =
            Mathf.Clamp(
                pitch,
                -maxPitch,
                maxPitch
            );


        yaw =
            Mathf.Clamp(
                yaw,
                -maxYaw,
                maxYaw
            );


        roll =
            Mathf.Clamp(
                roll,
                -maxRoll,
                maxRoll
            );


        // Accent用方向速度
        UpdateSignedAngularVelocity(
            pitch,
            yaw,
            roll,
            dt
        );


        Quaternion rawTarget =
            _baseRotation
            *
            Quaternion.Euler(
                pitch,
                yaw,
                roll
            );


        // =====================================================
        // ★ RAW角速度
        //
        // 平滑化してからDeadZone解除すると
        // 動き出しが遅れる。
        //
        // なので最新2サンプルから直接算出。
        // =====================================================

        _rawAngularSpeed =
            Quaternion.Angle(
                _lastRawRotation,
                rawTarget
            )
            /
            Mathf.Max(
                dt,
                0.0001f
            );


        _lastRawRotation =
            rawTarget;


        // =====================================================
        // Ultra micro-jitter corridor
        // =====================================================

        if (enableUltraLowLatencyTracking && ultraAdaptiveMicroFilter)
        {
            float error = Quaternion.Angle(_sampleRotation, rawTarget);

            if (ultraStaticPoseLock)
            {
                float lockReleaseError = Mathf.Max(
                    ultraRotationDeadZone * 2.0f,
                    ultraRotationDeadZone + 0.0001f
                );
                float candidateRadius = Mathf.Max(
                    ultraRotationDeadZone * 1.5f,
                    ultraRotationDeadZone + 0.0001f
                );

                if (_ultraRotationStaticLocked)
                {
                    float lockError = Quaternion.Angle(_ultraRotationStaticAnchor, rawTarget);
                    if (lockError <= lockReleaseError)
                    {
                        _sampleRotation = _ultraRotationStaticAnchor;
                        return;
                    }

                    _ultraRotationStaticLocked = false;
                    _ultraRotationStaticTime = 0f;
                    _ultraRotationStaticAnchor = rawTarget;
                }

                if (_ultraRotationStaticTime <= 0f)
                {
                    _ultraRotationStaticAnchor = rawTarget;
                    _ultraRotationStaticTime = dt;
                }
                else
                {
                    float candidateError = Quaternion.Angle(_ultraRotationStaticAnchor, rawTarget);
                    if (candidateError <= candidateRadius && _rawAngularSpeed < ultraRotationDirectSpeed)
                    {
                        _ultraRotationStaticTime += dt;
                    }
                    else
                    {
                        _ultraRotationStaticAnchor = rawTarget;
                        _ultraRotationStaticTime = dt;
                    }
                }

                if (_ultraRotationStaticTime >= Mathf.Max(0.03f, ultraStaticLockSeconds))
                {
                    _ultraRotationStaticAnchor = _sampleRotation;
                    _ultraRotationStaticLocked = true;
                    _sampleRotation = _ultraRotationStaticAnchor;
                    return;
                }
            }
            else
            {
                _ultraRotationStaticLocked = false;
                _ultraRotationStaticTime = 0f;
            }

            bool staticNoise =
                _rawAngularSpeed < ultraRotationStaticReleaseSpeed &&
                error < ultraRotationDeadZone;

            if (staticNoise)
            {
                return;
            }

            if (_rawAngularSpeed >= ultraRotationDirectSpeed || error >= ultraRotationDirectError)
            {
                _sampleRotation = rawTarget;
                return;
            }

            float speedWeight = Mathf.InverseLerp(
                ultraRotationStaticReleaseSpeed,
                ultraRotationDirectSpeed,
                _rawAngularSpeed
            );
            float response = Mathf.Lerp(
                ultraRotationSlowResponse,
                ultraRotationFastResponse,
                speedWeight
            );
            _sampleRotation = Quaternion.Slerp(
                _sampleRotation,
                rawTarget,
                ExpFactor(response, dt)
            );
            return;
        }

        if (!landMarkerSpeedMode)
        {
            _sampleRotation = rawTarget;
            return;
        }

        float errorLegacy = Quaternion.Angle(_sampleRotation, rawTarget);
        float deadZoneLegacy = rotationStaticDeadZone *
            (enableHybridPrecisionTracking
                ? KiwiPrecisionTrackingMath.QualityDeadZoneMultiplier(quality)
                : 1f);
        bool staticNoiseLegacy =
            _rawAngularSpeed < rotationDeadZoneReleaseSpeed &&
            errorLegacy < deadZoneLegacy;

        if (!staticNoiseLegacy)
        {
            _sampleRotation = rawTarget;
        }
    }


    // =========================================================
    // Roll-stable position geometry
    // =========================================================

    private bool TryGetPositionGeometry(
        FacePrecisionTrackingData data,
        out PositionGeometry geometry)
    {
        geometry =
            default;


        if (!data.isValid)
        {
            return false;
        }


        Vector2 eyeLine =
            data.leftEyeCenter
            -
            data.rightEyeCenter;


        if (
            eyeLine.sqrMagnitude < 0.0000001f ||
            data.eyeCenter == Vector2.zero ||
            data.chin == Vector2.zero
        )
        {
            return false;
        }


        geometry.eyeCenter =
            data.eyeCenter;


        geometry.eyeLine =
            eyeLine;


        geometry.eyeToChin =
            data.chin
            -
            data.eyeCenter;


        return
            geometry.eyeToChin.sqrMagnitude > 0.0000001f;
    }


    private Vector2 CalculateRollStablePositionAnchor(
        PositionGeometry geometry)
    {
        if (
            !_hasNeutralPositionGeometry ||
            _neutralEyeLine.sqrMagnitude < 0.0000001f
        )
        {
            return geometry.eyeCenter
                +
                geometry.eyeToChin
                *
                virtualNeckExtension;
        }


        float scaleRatio =
            geometry.eyeLine.magnitude
            /
            Mathf.Max(
                _neutralEyeLine.magnitude,
                0.000001f
            );


        scaleRatio =
            Mathf.Clamp(
                scaleRatio,
                0.50f,
                2.00f
            );


        float rollRadians =
            SignedAngle2D(
                _neutralEyeLine,
                geometry.eyeLine
            );


        Vector2 neutralEyeToNeck =
            _neutralEyeToChin
            *
            virtualNeckExtension;


        Vector2 rotatedEyeToNeck =
            Rotate2D(
                neutralEyeToNeck
                *
                scaleRatio,
                rollRadians
            );


        Vector2 stableAnchor =
            geometry.eyeCenter
            +
            rotatedEyeToNeck;


        Vector2 currentFrameAnchor =
            geometry.eyeCenter
            +
            geometry.eyeToChin
            *
            virtualNeckExtension;


        return Vector2.Lerp(
            currentFrameAnchor,
            stableAnchor,
            rollIsolationStrength
        );
    }


    private float SignedAngle2D(
        Vector2 from,
        Vector2 to)
    {
        float cross =
            from.x * to.y
            -
            from.y * to.x;


        float dot =
            Vector2.Dot(
                from,
                to
            );


        return Mathf.Atan2(
            cross,
            dot
        );
    }


    private Vector2 Rotate2D(
        Vector2 value,
        float radians)
    {
        float c =
            Mathf.Cos(
                radians
            );


        float s =
            Mathf.Sin(
                radians
            );


        return new Vector2(
            value.x * c
            -
            value.y * s,

            value.x * s
            +
            value.y * c
        );
    }


    // =========================================================
    // Position
    // =========================================================

    private void UpdatePositionSample(
        Vector2 center,
        float quality,
        float dt)
    {
        if (!enablePosition)
        {
            _samplePosition =
                _basePosition;

            return;
        }


        Vector2 delta =
            (
                center -
                _neutralCenter
            )
            *
            faceMotionMultiplier;


        if (avatarCentricHorizontalMovement)
        {
            delta = KiwiPrecisionTrackingMath.CalculateAvatarCentricPositionDelta(
                delta,
                runner != null && runner.IsInputHorizontallyMirrored
            );
        }


        Vector3 rawTarget =
            useScreenSpacePositionMapping
                ?
                CalculateScreenMappedPosition(
                    delta
                )
                :
                CalculateLegacyPosition(
                    delta
                );


        _rawPositionSpeed =
            Vector2.Distance(
                center,
                _lastRawCenter
            )
            /
            Mathf.Max(
                dt,
                0.0001f
            );


        // KIWI_V4_7_HEAD_TRANSLATION_STABILIZATION
        // Adapt the EXISTING static position corridor from measured
        // source/cadence quality. No second filter is stacked.
        float effectiveUltraPositionDeadZone =
            KiwiCommercialRigidMotionPolicy.GetAdaptivePositionDeadZone(
                ultraPositionDeadZone,
                quality);

        if (enableUltraLowLatencyTracking && ultraAdaptiveMicroFilter)
        {
            float safeHeight = Mathf.Max(_modelHeight, 0.0001f);
            float positionError = Vector3.Distance(
                _samplePosition,
                rawTarget
            ) / safeHeight;

            if (ultraStaticPoseLock)
            {
                float lockReleaseError = Mathf.Max(
                    effectiveUltraPositionDeadZone * 2.0f,
                    effectiveUltraPositionDeadZone + 0.000001f
                );
                float candidateRadius = Mathf.Max(
                    effectiveUltraPositionDeadZone * 1.5f,
                    effectiveUltraPositionDeadZone + 0.000001f
                );

                if (_ultraPositionStaticLocked)
                {
                    float lockError = Vector3.Distance(_ultraPositionStaticAnchor, rawTarget) / safeHeight;
                    if (lockError <= lockReleaseError)
                    {
                        _samplePosition = _ultraPositionStaticAnchor;
                        return;
                    }

                    _ultraPositionStaticLocked = false;
                    _ultraPositionStaticTime = 0f;
                    _ultraPositionStaticAnchor = rawTarget;
                }

                if (_ultraPositionStaticTime <= 0f)
                {
                    _ultraPositionStaticAnchor = rawTarget;
                    _ultraPositionStaticTime = dt;
                }
                else
                {
                    float candidateError = Vector3.Distance(_ultraPositionStaticAnchor, rawTarget) / safeHeight;
                    if (candidateError <= candidateRadius && _rawPositionSpeed < ultraPositionDirectSpeed)
                    {
                        _ultraPositionStaticTime += dt;
                    }
                    else
                    {
                        _ultraPositionStaticAnchor = rawTarget;
                        _ultraPositionStaticTime = dt;
                    }
                }

                if (_ultraPositionStaticTime >= Mathf.Max(0.03f, ultraStaticLockSeconds))
                {
                    _ultraPositionStaticAnchor = _samplePosition;
                    _ultraPositionStaticLocked = true;
                    _samplePosition = _ultraPositionStaticAnchor;
                    return;
                }
            }
            else
            {
                _ultraPositionStaticLocked = false;
                _ultraPositionStaticTime = 0f;
            }

            bool staticNoise =
                _rawPositionSpeed < ultraPositionStaticReleaseSpeed &&
                positionError < effectiveUltraPositionDeadZone;

            if (staticNoise)
            {
                return;
            }

            if (_rawPositionSpeed >= ultraPositionDirectSpeed || positionError >= ultraPositionDirectError)
            {
                _samplePosition = rawTarget;
                return;
            }

            float speedWeight = Mathf.InverseLerp(
                ultraPositionStaticReleaseSpeed,
                ultraPositionDirectSpeed,
                _rawPositionSpeed
            );
            float response = Mathf.Lerp(
                ultraPositionSlowResponse,
                ultraPositionFastResponse,
                speedWeight
            );
            _samplePosition = Vector3.Lerp(
                _samplePosition,
                rawTarget,
                ExpFactor(response, dt)
            );
            return;
        }

        if (!landMarkerSpeedMode)
        {
            _samplePosition = rawTarget;
            return;
        }

        float positionErrorLegacy = Vector3.Distance(
            _samplePosition,
            rawTarget
        ) / Mathf.Max(_modelHeight, 0.0001f);
        float deadZoneLegacy = positionStaticDeadZone *
            (enableHybridPrecisionTracking
                ? KiwiPrecisionTrackingMath.QualityDeadZoneMultiplier(quality)
                : 1f);
        bool staticNoiseLegacy =
            _rawPositionSpeed < positionDeadZoneReleaseSpeed &&
            positionErrorLegacy < deadZoneLegacy;

        if (!staticNoiseLegacy)
        {
            _samplePosition = rawTarget;
        }
    }


    // =========================================================
    // Depth
    // =========================================================

    private void UpdateScaleSample(
        float depthRatio,
        float quality,
        float dt)
    {
        float targetFactor =
            1f;


        if (
            enableDistanceScale &&
            depthRatio > 0.0001f
        )
        {
            targetFactor =
                1f
                +
                (
                    depthRatio -
                    1f
                )
                *
                distanceScaleGain
                *
                depthMovementMultiplier;


            targetFactor =
                Mathf.Clamp(
                    targetFactor,
                    minimumScale,
                    maximumScale
                );
        }


        Vector3 rawTarget =
            _baseScale *
            targetFactor;


        _rawScaleSpeed =
            Mathf.Abs(
                targetFactor -
                _lastRawScaleFactor
            )
            /
            Mathf.Max(
                dt,
                0.0001f
            );


        _lastRawScaleFactor =
            targetFactor;


        float currentFactor = SafeScaleRatio(_sampleScale.x, _baseScale.x);
        float error = Mathf.Abs(targetFactor - currentFactor);

        if (enableUltraLowLatencyTracking && ultraAdaptiveMicroFilter)
        {
            if (ultraStaticPoseLock)
            {
                float lockReleaseError = Mathf.Max(
                    ultraScaleDeadZone * 2.0f,
                    ultraScaleDeadZone + 0.000001f
                );
                float candidateRadius = Mathf.Max(
                    ultraScaleDeadZone * 1.5f,
                    ultraScaleDeadZone + 0.000001f
                );

                if (_ultraScaleStaticLocked)
                {
                    float lockFactor = SafeScaleRatio(_ultraScaleStaticAnchor.x, _baseScale.x);
                    float lockError = Mathf.Abs(targetFactor - lockFactor);
                    if (lockError <= lockReleaseError)
                    {
                        _sampleScale = _ultraScaleStaticAnchor;
                        return;
                    }

                    _ultraScaleStaticLocked = false;
                    _ultraScaleStaticTime = 0f;
                    _ultraScaleStaticAnchor = rawTarget;
                }

                if (_ultraScaleStaticTime <= 0f)
                {
                    _ultraScaleStaticAnchor = rawTarget;
                    _ultraScaleStaticTime = dt;
                }
                else
                {
                    float lockFactor = SafeScaleRatio(_ultraScaleStaticAnchor.x, _baseScale.x);
                    float candidateError = Mathf.Abs(targetFactor - lockFactor);
                    if (candidateError <= candidateRadius && _rawScaleSpeed < ultraScaleDirectSpeed)
                    {
                        _ultraScaleStaticTime += dt;
                    }
                    else
                    {
                        _ultraScaleStaticAnchor = rawTarget;
                        _ultraScaleStaticTime = dt;
                    }
                }

                if (_ultraScaleStaticTime >= Mathf.Max(0.03f, ultraStaticLockSeconds))
                {
                    _ultraScaleStaticAnchor = _sampleScale;
                    _ultraScaleStaticLocked = true;
                    _sampleScale = _ultraScaleStaticAnchor;
                    return;
                }
            }
            else
            {
                _ultraScaleStaticLocked = false;
                _ultraScaleStaticTime = 0f;
            }

            bool staticNoise =
                _rawScaleSpeed < ultraScaleStaticReleaseSpeed &&
                error < ultraScaleDeadZone;

            if (staticNoise)
            {
                return;
            }

            if (_rawScaleSpeed >= ultraScaleDirectSpeed || error >= ultraScaleDirectError)
            {
                _sampleScale = rawTarget;
                return;
            }

            float speedWeight = Mathf.InverseLerp(
                ultraScaleStaticReleaseSpeed,
                ultraScaleDirectSpeed,
                _rawScaleSpeed
            );
            float response = Mathf.Lerp(
                ultraScaleSlowResponse,
                ultraScaleFastResponse,
                speedWeight
            );
            _sampleScale = Vector3.Lerp(
                _sampleScale,
                rawTarget,
                ExpFactor(response, dt)
            );
            return;
        }

        if (!landMarkerSpeedMode)
        {
            _sampleScale = rawTarget;
            return;
        }

        float deadZoneLegacy = scaleStaticDeadZone *
            (enableHybridPrecisionTracking
                ? KiwiPrecisionTrackingMath.QualityDeadZoneMultiplier(quality)
                : 1f);
        bool staticNoiseLegacy =
            _rawScaleSpeed < scaleDeadZoneReleaseSpeed &&
            error < deadZoneLegacy;

        if (!staticNoiseLegacy)
        {
            _sampleScale = rawTarget;
        }
    }

    // =========================================================
    // Hybrid Precision - depth / outlier / render-time prediction
    // =========================================================

    private float CalculatePrecisionDepthRatio(
        FacePrecisionTrackingData data)
    {
        float fallback =
            _neutralEyeSpan > 0.0001f &&
            data.eyeSpan2D > 0.0001f
                ?
                data.eyeSpan2D /
                _neutralEyeSpan
                :
                1f;


        if (
            !enableHybridPrecisionTracking ||
            !usePrecisionDepthFusion ||
            _precisionGeometryCalibrationSamples < 3 ||
            _neutralEyeSpan3D <= 0.0001f ||
            _neutralFaceWidth2D <= 0.0001f ||
            _neutralFaceHeight2D <= 0.0001f
        )
        {
            return Mathf.Clamp(
                fallback,
                0.50f,
                2.00f
            );
        }


        Quaternion delta =
            data.faceRotation
            *
            Quaternion.Inverse(
                _neutralFaceRotation
            );


        Vector3 euler =
            delta.eulerAngles;


        float pitch =
            Mathf.Abs(
                SignedAngle(
                    euler.x
                )
            );


        float yaw =
            Mathf.Abs(
                SignedAngle(
                    euler.y
                )
            );


        float yawReliability =
            Mathf.Lerp(
                1f,
                0.12f,
                Mathf.InverseLerp(
                    10f,
                    55f,
                    yaw
                )
            );


        float pitchReliability =
            Mathf.Lerp(
                1f,
                0.20f,
                Mathf.InverseLerp(
                    10f,
                    45f,
                    pitch
                )
            );


        float weightedLog =
            0f;

        float totalWeight =
            0f;


        AddDepthRatio(
            data.eyeSpan3D /
            _neutralEyeSpan3D,
            0.55f,
            ref weightedLog,
            ref totalWeight
        );


        AddDepthRatio(
            data.eyeSpan2D /
            _neutralEyeSpan,
            0.18f *
            yawReliability,
            ref weightedLog,
            ref totalWeight
        );


        AddDepthRatio(
            data.faceWidth2D /
            _neutralFaceWidth2D,
            0.17f *
            yawReliability,
            ref weightedLog,
            ref totalWeight
        );


        AddDepthRatio(
            data.faceHeight2D /
            _neutralFaceHeight2D,
            0.20f *
            pitchReliability,
            ref weightedLog,
            ref totalWeight
        );


        if (totalWeight <= 0.0001f)
        {
            return Mathf.Clamp(
                fallback,
                0.50f,
                2.00f
            );
        }


        float ratio =
            Mathf.Exp(
                weightedLog /
                totalWeight
            );


        return Mathf.Clamp(
            ratio,
            0.50f,
            2.00f
        );
    }


    private void AddDepthRatio(
        float ratio,
        float weight,
        ref float weightedLog,
        ref float totalWeight)
    {
        if (
            weight <= 0f ||
            float.IsNaN(ratio) ||
            float.IsInfinity(ratio) ||
            ratio <= 0.0001f
        )
        {
            return;
        }


        ratio =
            Mathf.Clamp(
                ratio,
                0.50f,
                2.00f
            );


        weightedLog +=
            Mathf.Log(
                ratio
            )
            *
            weight;


        totalWeight +=
            weight;
    }


    private float CalculatePrecisionSampleQuality(
        FacePrecisionTrackingData data,
        float depthRatio)
    {
        float baseQuality =
            Mathf.Clamp01(
                data.geometryQuality
            );


        if (
            _precisionGeometryCalibrationSamples < 3 ||
            _neutralEyeSpan3D <= 0.0001f ||
            _neutralEyeSpan <= 0.0001f ||
            _neutralFaceWidth2D <= 0.0001f ||
            _neutralFaceHeight2D <= 0.0001f ||
            depthRatio <= 0.0001f
        )
        {
            return baseQuality;
        }


        Quaternion delta =
            data.faceRotation *
            Quaternion.Inverse(
                _neutralFaceRotation
            );


        Vector3 euler =
            delta.eulerAngles;


        float pitch =
            Mathf.Abs(
                SignedAngle(
                    euler.x
                )
            );


        float yaw =
            Mathf.Abs(
                SignedAngle(
                    euler.y
                )
            );


        float yawReliability =
            Mathf.Lerp(
                1f,
                0.15f,
                Mathf.InverseLerp(
                    12f,
                    58f,
                    yaw
                )
            );


        float pitchReliability =
            Mathf.Lerp(
                1f,
                0.22f,
                Mathf.InverseLerp(
                    12f,
                    48f,
                    pitch
                )
            );


        float weightedError = 0f;
        float totalWeight = 0f;


        AddDepthAgreement(
            data.eyeSpan3D / _neutralEyeSpan3D,
            depthRatio,
            0.35f,
            ref weightedError,
            ref totalWeight
        );


        AddDepthAgreement(
            data.eyeSpan2D / _neutralEyeSpan,
            depthRatio,
            0.25f * yawReliability,
            ref weightedError,
            ref totalWeight
        );


        AddDepthAgreement(
            data.faceWidth2D / _neutralFaceWidth2D,
            depthRatio,
            0.20f * yawReliability,
            ref weightedError,
            ref totalWeight
        );


        AddDepthAgreement(
            data.faceHeight2D / _neutralFaceHeight2D,
            depthRatio,
            0.20f * pitchReliability,
            ref weightedError,
            ref totalWeight
        );


        if (totalWeight <= 0.0001f)
        {
            return baseQuality;
        }


        float meanLogError =
            weightedError /
            totalWeight;


        float agreement =
            1f -
            Mathf.InverseLerp(
                0.055f,
                0.280f,
                meanLogError
            );


        // Agreement only reduces trust. It never creates confidence that the
        // current Landmarker geometry did not already have.
        return Mathf.Clamp01(
            baseQuality *
            Mathf.Lerp(
                0.30f,
                1f,
                agreement
            )
        );
    }


    private void AddDepthAgreement(
        float ratio,
        float referenceRatio,
        float weight,
        ref float weightedError,
        ref float totalWeight)
    {
        if (
            weight <= 0f ||
            ratio <= 0.0001f ||
            referenceRatio <= 0.0001f ||
            float.IsNaN(ratio) ||
            float.IsInfinity(ratio) ||
            float.IsNaN(referenceRatio) ||
            float.IsInfinity(referenceRatio)
        )
        {
            return;
        }


        float relative =
            Mathf.Clamp(
                ratio / referenceRatio,
                0.50f,
                2.00f
            );


        weightedError +=
            Mathf.Abs(
                Mathf.Log(
                    relative
                )
            ) *
            weight;


        totalWeight +=
            weight;
    }


    private int ApplyQualityGatedLatestResultCorrection(
        ref Vector2 center,
        ref Quaternion rotation,
        ref float depthRatio,
        float quality,
        float dt)
    {
        if (
            !enableHybridPrecisionTracking ||
            !enablePrecisionOutlierGuard ||
            !_hasPrecisionInputHistory
        )
        {
            return 0;
        }

        float safeDt = Mathf.Max(dt, 0.0001f);
        float angularSpeed =
            Quaternion.Angle(_lastPrecisionInputRotation, rotation) /
            safeDt;
        float positionSpeed =
            Vector2.Distance(_lastPrecisionInputCenter, center) /
            safeDt;
        float depthSpeed =
            Mathf.Abs(depthRatio - _lastPrecisionDepthRatio) /
            safeDt;

        const float CatastrophicAngularSpeed = 3000f;
        const float CatastrophicDepthSpeed = 10.0f;
        bool lowQuality = quality < precisionOutlierQualityThreshold;
        int channels = 0;

        if (
            angularSpeed > CatastrophicAngularSpeed ||
            (lowQuality && angularSpeed > precisionAngularOutlierSpeed)
        )
        {
            rotation = Quaternion.RotateTowards(
                _lastPrecisionInputRotation,
                rotation,
                Mathf.Max(0f, precisionAngularOutlierSpeed * safeDt)
            );
            channels |= 1;
        }

        if (
            lowQuality &&
            positionSpeed > precisionPositionOutlierSpeed
        )
        {
            center = Vector2.MoveTowards(
                _lastPrecisionInputCenter,
                center,
                Mathf.Max(0f, precisionPositionOutlierSpeed * safeDt)
            );
            channels |= 2;
        }

        if (
            depthSpeed > CatastrophicDepthSpeed ||
            (lowQuality && depthSpeed > precisionDepthOutlierSpeed)
        )
        {
            depthRatio = Mathf.MoveTowards(
                _lastPrecisionDepthRatio,
                depthRatio,
                Mathf.Max(0f, precisionDepthOutlierSpeed * safeDt)
            );
            channels |= 4;
        }

        return channels;
    }


    private bool ShouldRejectPrecisionSample(
        Vector2 center,
        Quaternion rotation,
        float depthRatio,
        float quality,
        float dt)
    {
        if (
            !enableHybridPrecisionTracking ||
            !enablePrecisionOutlierGuard ||
            !_hasPrecisionInputHistory
        )
        {
            return false;
        }


        float safeDt =
            Mathf.Max(
                dt,
                0.0001f
            );


        float angularSpeed =
            Quaternion.Angle(
                _lastPrecisionInputRotation,
                rotation
            )
            /
            safeDt;


        float positionSpeed =
            Vector2.Distance(
                _lastPrecisionInputCenter,
                center
            )
            /
            safeDt;


        float depthSpeed =
            Mathf.Abs(
                depthRatio -
                _lastPrecisionDepthRatio
            )
            /
            safeDt;


        // Geometry quality cannot detect a transformation-matrix-only glitch.
        // Keep this guard deliberately very high so genuine fast motion remains
        // Landmarker-primary, while impossible single-frame jumps are rejected.
        const float CatastrophicAngularSpeed = 3000f;
        const float CatastrophicPositionSpeed = 4.0f;
        const float CatastrophicDepthSpeed = 10.0f;


        if (
            angularSpeed > CatastrophicAngularSpeed ||
            positionSpeed > CatastrophicPositionSpeed ||
            depthSpeed > CatastrophicDepthSpeed
        )
        {
            return true;
        }


        if (
            quality >=
            precisionOutlierQualityThreshold
        )
        {
            return false;
        }


        return
            angularSpeed >
            precisionAngularOutlierSpeed
            ||
            positionSpeed >
            precisionPositionOutlierSpeed
            ||
            depthSpeed >
            precisionDepthOutlierSpeed;
    }


    private bool ShouldReacquireRejectedSample(
        Vector2 center,
        Quaternion rotation,
        float depthRatio,
        float quality,
        long sampleHostTicks,
        bool sampleUsesMatchedSubmissionTiming,
        long sampleTimestamp)
    {
        const float MinimumReacquireQuality = 0.20f;
        const int RequiredConsistentRejects = 3;


        float candidateDt = 1f / 30f;


        if (
            _hasPrecisionRejectedCandidate &&
            _precisionRejectedHostTicks > 0L &&
            sampleHostTicks > _precisionRejectedHostTicks &&
            sampleUsesMatchedSubmissionTiming ==
                _precisionRejectedUsedMatchedSubmissionTiming
        )
        {
            candidateDt =
                (float)KiwiPrecisionTrackingMath.HostTicksToSeconds(
                    sampleHostTicks -
                    _precisionRejectedHostTicks
                );
        }
        else if (
            _hasPrecisionRejectedCandidate &&
            _precisionRejectedTimestamp >= 0L &&
            sampleTimestamp > _precisionRejectedTimestamp
        )
        {
            candidateDt =
                (sampleTimestamp - _precisionRejectedTimestamp) / 1000f;
        }


        candidateDt = Mathf.Clamp(
            candidateDt,
            1f / 240f,
            0.10f
        );


        // Reacquisition compares consecutive rejected candidates, so its window
        // must use candidate-to-candidate dt, not time since the last accepted pose.
        // At low tracking rates a legitimate fast movement travels farther
        // between callbacks. Scale the consistency window with the accepted-sample
        // interval, while keeping strict caps so unrelated spikes cannot chain.
        float maximumAngularDifference =
            Mathf.Clamp(
                precisionAngularOutlierSpeed *
                Mathf.Max(candidateDt, 1f / 240f) *
                1.20f,
                18f,
                55f
            );


        float maximumPositionDifference =
            Mathf.Clamp(
                precisionPositionOutlierSpeed *
                Mathf.Max(candidateDt, 1f / 240f) *
                1.25f,
                0.040f,
                0.100f
            );


        float maximumDepthLogDifference =
            Mathf.Clamp(
                precisionDepthOutlierSpeed *
                Mathf.Max(candidateDt, 1f / 240f) *
                1.25f,
                0.16f,
                0.30f
            );


        if (quality < MinimumReacquireQuality)
        {
            ResetRejectedCandidateState();
            return false;
        }


        bool consistent =
            _hasPrecisionRejectedCandidate
            &&
            Quaternion.Angle(
                _precisionRejectedRotation,
                rotation
            )
            <=
            maximumAngularDifference
            &&
            Vector2.Distance(
                _precisionRejectedCenter,
                center
            )
            <=
            maximumPositionDifference
            &&
            Mathf.Abs(
                Mathf.Log(
                    Mathf.Clamp(
                        depthRatio /
                        Mathf.Max(
                            _precisionRejectedDepthRatio,
                            0.0001f
                        ),
                        0.50f,
                        2.00f
                    )
                )
            )
            <=
            maximumDepthLogDifference;


        if (consistent)
        {
            _precisionRejectedStreak++;
        }
        else
        {
            _precisionRejectedStreak =
                1;
        }


        _hasPrecisionRejectedCandidate =
            true;

        _precisionRejectedRotation =
            rotation;

        _precisionRejectedCenter =
            center;

        _precisionRejectedDepthRatio =
            depthRatio;

        _precisionRejectedHostTicks =
            sampleHostTicks;

        _precisionRejectedUsedMatchedSubmissionTiming =
            sampleUsesMatchedSubmissionTiming;

        _precisionRejectedTimestamp =
            sampleTimestamp;


        if (
            _precisionRejectedStreak <
            RequiredConsistentRejects
        )
        {
            return false;
        }


        ResetRejectedCandidateState();
        return true;
    }


    private void ResetRejectedCandidateState()
    {
        _hasPrecisionRejectedCandidate =
            false;

        _precisionRejectedRotation =
            Quaternion.identity;

        _precisionRejectedCenter =
            Vector2.zero;

        _precisionRejectedDepthRatio =
            1f;

        _precisionRejectedStreak =
            0;

        _precisionRejectedHostTicks =
            0L;

        _precisionRejectedUsedMatchedSubmissionTiming =
            false;

        _precisionRejectedTimestamp =
            -1L;
    }


    private void UpdatePredictionState(
        FacePrecisionTrackingData data,
        float quality,
        float dt)
    {
        _lastPrecisionSubmissionHostTicks =
            data.submissionHostTicks;

        _lastPrecisionArrivalHostTicks =
            data.arrivalHostTicks;

        _lastPrecisionQuality =
            quality;


        if (
            data.submissionHostTicks > 0L &&
            data.arrivalHostTicks >= data.submissionHostTicks
        )
        {
            _lastInferenceLatencyMs =
                (float)KiwiPrecisionTrackingMath.HostTicksToSeconds(
                    data.arrivalHostTicks -
                    data.submissionHostTicks
                )
                *
                1000f;
        }
        else
        {
            _lastInferenceLatencyMs =
                0f;
        }


        if (!_hasPredictionHistory)
        {
            _predictionPreviousRotation =
                _sampleRotation;

            _predictionPreviousPosition =
                _samplePosition;

            _predictionPreviousScale =
                _sampleScale;

            _predictionRotationConsistency =
                0f;

            _predictionPositionConsistency =
                0f;

            _predictionScaleConsistency =
                0f;

            _hasPredictionHistory =
                true;

            return;
        }


        Vector3 rawAngularVelocity =
            KiwiPrecisionTrackingMath.AngularVelocityDegrees(
                _predictionPreviousRotation,
                _sampleRotation,
                dt
            );


        if (
            rawAngularVelocity.magnitude >
            1200f
        )
        {
            rawAngularVelocity =
                rawAngularVelocity.normalized *
                1200f;
        }


        Vector3 rawPositionVelocity =
            (
                _samplePosition -
                _predictionPreviousPosition
            )
            /
            Mathf.Max(
                dt,
                0.0001f
            );


        float maxPositionVelocity =
            Mathf.Max(
                0.01f,
                _modelHeight *
                3.0f
            );


        if (
            rawPositionVelocity.magnitude >
            maxPositionVelocity
        )
        {
            rawPositionVelocity =
                rawPositionVelocity.normalized *
                maxPositionVelocity;
        }


        Vector3 rawScaleVelocity =
            (
                _sampleScale -
                _predictionPreviousScale
            )
            /
            Mathf.Max(
                dt,
                0.0001f
            );


        if (_hasPredictionRawVelocityHistory)
        {
            _predictionRotationConsistency =
                CalculateVectorPredictionConsistency(
                    _predictionPreviousRawAngularVelocity,
                    rawAngularVelocity,
                    2.0f
                );

            _predictionPositionConsistency =
                CalculateVectorPredictionConsistency(
                    _predictionPreviousRawPositionVelocity,
                    rawPositionVelocity,
                    Mathf.Max(
                        0.0005f,
                        _modelHeight *
                        0.002f
                    )
                );

            _predictionScaleConsistency =
                CalculateScalarPredictionConsistency(
                    _predictionPreviousRawScaleVelocity.x,
                    rawScaleVelocity.x,
                    Mathf.Max(
                        0.0005f,
                        Mathf.Abs(
                            _baseScale.x
                        )
                        *
                        0.002f
                    )
                );
        }
        else
        {
            // KIWI_V4_7_PREDICTION_CONSISTENCY_WARMUP
            // One velocity observation does not establish a motion
            // direction. Wait for the next accepted sample before
            // granting extrapolation after startup/reacquisition.
            _predictionRotationConsistency =
                0f;

            _predictionPositionConsistency =
                0f;

            _predictionScaleConsistency =
                0f;

            _hasPredictionRawVelocityHistory =
                true;
        }


        _predictionPreviousRawAngularVelocity =
            rawAngularVelocity;

        _predictionPreviousRawPositionVelocity =
            rawPositionVelocity;

        _predictionPreviousRawScaleVelocity =
            rawScaleVelocity;


        float rotationVelocityResponse =
            KiwiUltraDisplayMath.CalculateAdaptiveVelocityResponse(
                predictionVelocityResponse,
                predictionVelocityFastResponse,
                _predictionRotationConsistency
            );

        float positionVelocityResponse =
            KiwiUltraDisplayMath.CalculateAdaptiveVelocityResponse(
                predictionVelocityResponse,
                predictionVelocityFastResponse,
                _predictionPositionConsistency
            );

        float scaleVelocityResponse =
            KiwiUltraDisplayMath.CalculateAdaptiveVelocityResponse(
                predictionVelocityResponse,
                predictionVelocityFastResponse,
                _predictionScaleConsistency
            );


        _predictionAngularVelocityDegrees =
            Vector3.Lerp(
                _predictionAngularVelocityDegrees,
                rawAngularVelocity,
                ExpFactor(rotationVelocityResponse, dt)
            );


        _predictionPositionVelocity =
            Vector3.Lerp(
                _predictionPositionVelocity,
                rawPositionVelocity,
                ExpFactor(positionVelocityResponse, dt)
            );


        _predictionScaleVelocity =
            Vector3.Lerp(
                _predictionScaleVelocity,
                rawScaleVelocity,
                ExpFactor(scaleVelocityResponse, dt)
            );


        _predictionPreviousRotation =
            _sampleRotation;

        _predictionPreviousPosition =
            _samplePosition;

        _predictionPreviousScale =
            _sampleScale;
    }


    private float CalculateVectorPredictionConsistency(
        Vector3 previous,
        Vector3 current,
        float minimumSpeed)
    {
        float previousMagnitude =
            previous.magnitude;

        float currentMagnitude =
            current.magnitude;


        if (
            previousMagnitude < minimumSpeed &&
            currentMagnitude < minimumSpeed
        )
        {
            return 1f;
        }


        if (
            previousMagnitude < minimumSpeed ||
            currentMagnitude < minimumSpeed
        )
        {
            return 0.45f;
        }


        float direction =
            Mathf.Clamp01(
                (
                    Vector3.Dot(
                        previous / previousMagnitude,
                        current / currentMagnitude
                    )
                    +
                    1f
                )
                *
                0.5f
            );


        // Strongly suppress extrapolation during reversals while remaining
        // permissive for curved but continuous head motion.
        direction =
            direction *
            direction;


        float magnitudeSimilarity =
            Mathf.Min(
                previousMagnitude,
                currentMagnitude
            )
            /
            Mathf.Max(
                previousMagnitude,
                currentMagnitude
            );


        return Mathf.Clamp01(
            direction *
            Mathf.Lerp(
                0.65f,
                1f,
                magnitudeSimilarity
            )
        );
    }


    private float CalculateScalarPredictionConsistency(
        float previous,
        float current,
        float minimumSpeed)
    {
        float previousAbs =
            Mathf.Abs(
                previous
            );

        float currentAbs =
            Mathf.Abs(
                current
            );


        if (
            previousAbs < minimumSpeed &&
            currentAbs < minimumSpeed
        )
        {
            return 1f;
        }


        if (
            previousAbs < minimumSpeed ||
            currentAbs < minimumSpeed
        )
        {
            return 0.40f;
        }


        if (
            Mathf.Sign(
                previous
            )
            !=
            Mathf.Sign(
                current
            )
        )
        {
            return 0f;
        }


        return Mathf.Clamp01(
            Mathf.Min(
                previousAbs,
                currentAbs
            )
            /
            Mathf.Max(
                previousAbs,
                currentAbs
            )
        );
    }


    private void OnBeforeRenderPrecision()
    {
        if (!useBeforeRenderLateLatch || kiwiRoot == null)
        {
            return;
        }

        // Prefer a genuinely newer accepted LandMarker result over prediction.
        // KIWI_V4_7_BEFORE_RENDER_RIGID_AUTHORITY
        // Never bypass the Provider Hub at the render boundary.
        if (enableUltraLowLatencyTracking && ultraConsumeLatestSampleBeforeRender && runner != null)
        {
            if (KiwiCommercialRigidMotionPolicy.TryGetAuthoritativeFrame(
                    runner,
                    out FacePrecisionTrackingData latestData) &&
                IsNewPrecisionFrame(latestData))
            {
                bool hasPositionGeometry = TryGetPositionGeometry(
                    latestData,
                    out PositionGeometry positionGeometry
                );

                Vector2 positionCenter = latestData.faceCenter;
                if (!ultraUseRunnerPositionAnchor &&
                    _calibrated &&
                    useRollStablePositionAnchor &&
                    _hasNeutralPositionGeometry &&
                    hasPositionGeometry)
                {
                    positionCenter = CalculateRollStablePositionAnchor(positionGeometry);
                }

                bool accepted = ProcessNewSample(
                    positionCenter,
                    latestData,
                    hasPositionGeometry,
                    positionGeometry
                );

                _lastObservedTimestamp = latestData.timestamp;
                _lastObservedFrameId = latestData.frameId;

                if (accepted)
                {
                    _lastSeenTime = Time.unscaledTime;
                    _trackingWasLost = false;
                }
            }
        }

        // Calibration owns the neutral pose. Avoid rendering an uncalibrated
        // display state from Application.onBeforeRender.
        if (!_calibrated)
        {
            return;
        }

        if (
            enableUltraLowLatencyTracking &&
            _displayPoseInitialized
        )
        {
            CalculateRenderTrackingTarget(
                out Quaternion targetRotation,
                out Vector3 targetPosition,
                out Vector3 targetScale
            );

            float renderBoundaryDt =
                ConsumeDisplayAdvanceDelta(0f);

            if (renderBoundaryDt > 0f)
            {
                // Recalculate and advance at the actual render boundary. The
                // delta is measured since the last display advance, so LateUpdate
                // and onBeforeRender never double-apply motion time.
                AdvanceDisplayPose(
                    targetRotation,
                    targetPosition,
                    targetScale,
                    renderBoundaryDt
                );
            }
        }

        RenderDisplayPose();
    }

    private void UpdateAndRenderDisplayPose(float dt)
    {
        CalculateRenderTrackingTarget(
            out Quaternion targetRotation,
            out Vector3 targetPosition,
            out Vector3 targetScale
        );

        float displayDt =
            ConsumeDisplayAdvanceDelta(dt);

        AdvanceDisplayPose(
            targetRotation,
            targetPosition,
            targetScale,
            displayDt
        );
        RenderDisplayPose();
    }

    private void CalculateRenderTrackingTarget(
        out Quaternion targetRotation,
        out Vector3 targetPosition,
        out Vector3 targetScale)
    {
        targetRotation = _sampleRotation;
        targetPosition = _samplePosition;
        targetScale = _sampleScale;
        _renderPositionVelocity = Vector3.zero;
        _lastCaptureAgeCompensationMs = 0f;

        if (!enableHybridPrecisionTracking ||
            !enableRenderTimeLatePrediction ||
            !_calibrated ||
            _trackingWasLost ||
            !_hasPredictionHistory ||
            _lastPrecisionQuality < predictionMinQuality)
        {
            return;
        }

        long sourceTicks = _lastPrecisionSubmissionHostTicks > 0L
            ? _lastPrecisionSubmissionHostTicks
            : _lastPrecisionArrivalHostTicks;
        if (sourceTicks <= 0L)
        {
            return;
        }

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        float age = (float)KiwiPrecisionTrackingMath.HostTicksToSeconds(
            nowTicks - sourceTicks
        );
        _lastPredictionAgeMs = age * 1000f;
        if (age < 0f || age > predictionStaleTime)
        {
            return;
        }

        float qualityWeight = Mathf.InverseLerp(
            predictionMinQuality,
            1f,
            _lastPrecisionQuality
        );
        float configuredMaxLead = enableUltraLowLatencyTracking
            ? ultraMaxPredictionSeconds
            : maxPredictionSeconds;
        float configuredStrength = enableUltraLowLatencyTracking
            ? ultraPredictionStrength
            : predictionStrength;
        float intervalBound = enableUltraLowLatencyTracking && ultraCompensateFullResultAge
            ? Mathf.Max(0f, configuredMaxLead)
            : Mathf.Min(
                Mathf.Max(0f, configuredMaxLead),
                Mathf.Max(0f, _lastAcceptedSampleInterval * 0.95f)
            );
        float captureAgeCompensation =
            enableUltraLowLatencyTracking &&
            ultraCompensateCameraCaptureAge
                ? KiwiUltraDisplayMath.CalculateCaptureAgeCompensation(
                    PrecisionSourceRateHz,
                    ultraCaptureIntervalFraction,
                    ultraMaxCaptureAgeSeconds
                )
                : 0f;
        _lastCaptureAgeCompensationMs = captureAgeCompensation * 1000f;
        float baseLead = Mathf.Min(
            age + captureAgeCompensation,
            intervalBound
        ) * configuredStrength * qualityWeight;

        // KIWI_V4_7_MEASURED_PREDICTION_GATE
        // Prediction is a latency-compensation privilege, not a
        // permanent filter stage. Stale/irregular cadence drives
        // this allowance toward zero.
        baseLead *=
            KiwiCommercialRigidMotionPolicy.GetPredictionAllowance(
                age);

        if (baseLead <= 0.00001f)
        {
            return;
        }

        float rotationMotionGate = 1f;
        float positionMotionGate = 1f;
        float scaleMotionGate = 1f;
        if (enableUltraLowLatencyTracking)
        {
            rotationMotionGate = Mathf.InverseLerp(
                ultraPredictionMinRotationSpeed,
                Mathf.Max(ultraPredictionMinRotationSpeed + 1f, ultraPredictionMinRotationSpeed * 2f),
                _rawAngularSpeed
            );
            positionMotionGate = Mathf.InverseLerp(
                ultraPredictionMinPositionSpeed,
                Mathf.Max(ultraPredictionMinPositionSpeed + 0.001f, ultraPredictionMinPositionSpeed * 2f),
                _rawPositionSpeed
            );
            scaleMotionGate = Mathf.InverseLerp(
                ultraPredictionMinScaleSpeed,
                Mathf.Max(ultraPredictionMinScaleSpeed + 0.001f, ultraPredictionMinScaleSpeed * 2f),
                _rawScaleSpeed
            );

            if (ultraAdaptiveMicroFilter && ultraStaticPoseLock)
            {
                if (_ultraRotationStaticLocked) rotationMotionGate = 0f;
                if (_ultraPositionStaticLocked) positionMotionGate = 0f;
                if (_ultraScaleStaticLocked) scaleMotionGate = 0f;
            }
        }

        float rotationLead = baseLead * _predictionRotationConsistency * rotationMotionGate;
        float positionLead = baseLead * _predictionPositionConsistency * positionMotionGate;
        float scaleLead = baseLead * _predictionScaleConsistency * scaleMotionGate;

        targetRotation = KiwiPrecisionTrackingMath.ExtrapolateRotation(
            _sampleRotation,
            _predictionAngularVelocityDegrees,
            rotationLead,
            maxRotationPredictionDegrees
        );

        Vector3 positionDelta = _predictionPositionVelocity * positionLead;
        float maxPositionDelta = Mathf.Max(0f, _modelHeight * maxPositionPredictionHeight);
        bool positionPredictionClamped = false;
        if (positionDelta.magnitude > maxPositionDelta && maxPositionDelta > 0f)
        {
            positionDelta = positionDelta.normalized * maxPositionDelta;
            positionPredictionClamped = true;
        }
        targetPosition = _samplePosition + positionDelta;

        if (
            !positionPredictionClamped &&
            age < intervalBound
        )
        {
            _renderPositionVelocity =
                _predictionPositionVelocity *
                configuredStrength *
                qualityWeight *
                _predictionPositionConsistency *
                positionMotionGate;
        }

        float currentScaleFactor = SafeScaleRatio(_sampleScale.x, _baseScale.x);
        float scaleVelocityFactor = Mathf.Abs(_baseScale.x) > 0.00001f
            ? _predictionScaleVelocity.x / _baseScale.x
            : 0f;
        float predictedScaleFactor = currentScaleFactor + Mathf.Clamp(
            scaleVelocityFactor * scaleLead,
            -maxScalePrediction,
            maxScalePrediction
        );
        predictedScaleFactor = Mathf.Clamp(
            predictedScaleFactor,
            minimumScale,
            maximumScale
        );
        targetScale = _baseScale * predictedScaleFactor;
    }

    private float ConsumeDisplayAdvanceDelta(
        float fallback)
    {
        long now =
            System.Diagnostics.Stopwatch.GetTimestamp();

        float dt = fallback;
        if (
            _lastDisplayAdvanceHostTicks > 0L &&
            now > _lastDisplayAdvanceHostTicks
        )
        {
            dt =
                (float)KiwiPrecisionTrackingMath.HostTicksToSeconds(
                    now -
                    _lastDisplayAdvanceHostTicks
                );
        }

        _lastDisplayAdvanceHostTicks = now;

        return Mathf.Clamp(dt, 0f, 0.05f);
    }

    private void AdvanceDisplayPose(
        Quaternion targetRotation,
        Vector3 targetPosition,
        Vector3 targetScale,
        float dt)
    {
        if (!_displayPoseInitialized ||
            !enableUltraLowLatencyTracking ||
            !ultraDisplayRateSmoothing)
        {
            _displayRotation = targetRotation;
            _displayPosition = targetPosition;
            _displayScale = targetScale;
            _displayPoseInitialized = true;
            return;
        }

        dt = Mathf.Clamp(dt, 0f, 0.05f);

        bool positionHandled = ApplyZeroLagMotionTarget(
            targetRotation,
            targetPosition,
            targetScale,
            dt
        );

        float baseResponse = Mathf.Max(1f, ultraDisplaySmoothingResponse);
        float fastResponse = Mathf.Max(baseResponse, ultraDisplayFastResponse);

        float rotationError = Quaternion.Angle(_displayRotation, targetRotation);
        float rotationWeight = Mathf.Sqrt(Mathf.InverseLerp(0.15f, 12f, rotationError));
        float rotationResponse = Mathf.Lerp(baseResponse, fastResponse, rotationWeight);
        _displayRotation = Quaternion.Slerp(
            _displayRotation,
            targetRotation,
            ExpFactor(rotationResponse, dt)
        );

        if (!positionHandled)
        {
            float safeHeight = Mathf.Max(_modelHeight, 0.0001f);
            float positionError = Vector3.Distance(_displayPosition, targetPosition) / safeHeight;
            float positionWeight = Mathf.Sqrt(Mathf.InverseLerp(0.0002f, 0.03f, positionError));
            float positionResponse = Mathf.Lerp(
                baseResponse * 0.85f,
                fastResponse * 0.85f,
                positionWeight
            );
            _displayPosition = Vector3.Lerp(
                _displayPosition,
                targetPosition,
                ExpFactor(positionResponse, dt)
            );
        }

        float displayScaleFactor = SafeScaleRatio(_displayScale.x, _baseScale.x);
        float targetScaleFactor = SafeScaleRatio(targetScale.x, _baseScale.x);
        float scaleError = Mathf.Abs(displayScaleFactor - targetScaleFactor);
        float scaleWeight = Mathf.Sqrt(Mathf.InverseLerp(0.0005f, 0.06f, scaleError));
        float scaleResponse = Mathf.Lerp(
            baseResponse * 0.70f,
            fastResponse * 0.70f,
            scaleWeight
        );
        _displayScale = Vector3.Lerp(
            _displayScale,
            targetScale,
            ExpFactor(scaleResponse, dt)
        );
    }

    private bool ApplyZeroLagMotionTarget(
        Quaternion targetRotation,
        Vector3 targetPosition,
        Vector3 targetScale,
        float dt)
    {
        if (
            !ultraDirectDisplayDuringMotion ||
            !_displayPoseInitialized
        )
        {
            return false;
        }

        bool positionHandled = false;

        float rotationError =
            Quaternion.Angle(
                _displayRotation,
                targetRotation
            );

        if (
            KiwiUltraDisplayMath.ShouldApplyDirectMotion(
                _rawAngularSpeed,
                ultraDirectDisplayRotationSpeed,
                rotationError,
                ultraRotationDeadZone
            )
        )
        {
            _displayRotation = targetRotation;
        }

        float safeHeight = Mathf.Max(_modelHeight, 0.0001f);
        float positionError =
            Vector3.Distance(
                _displayPosition,
                targetPosition
            ) /
            safeHeight;

        if (
            KiwiUltraDisplayMath.ShouldApplyDirectMotion(
                _rawPositionSpeed,
                ultraDirectDisplayPositionSpeed,
                positionError,
                ultraPositionDeadZone
            )
        )
        {
            float adaptiveCorrectionResponse =
                KiwiUltraDisplayMath.CalculateAdaptiveCorrectionResponse(
                    ultraPositionCorrectionResponse,
                    ultraPositionRecoveryResponse,
                    _predictionPositionConsistency
                );

            bool correctionBacklog =
                positionError >= Mathf.Max(
                    0.003f,
                    ultraPositionDirectError * 2f
                ) ||
                _predictionPositionConsistency < 0.35f;

            _displayPosition =
                !ultraPredictivePositionResampling || correctionBacklog
                    ? targetPosition
                    : KiwiUltraDisplayMath.AdvancePredictivePosition(
                        _displayPosition,
                        targetPosition,
                        _renderPositionVelocity,
                        dt,
                        adaptiveCorrectionResponse
                    );

            positionHandled = true;
        }

        float displayScaleFactor =
            SafeScaleRatio(
                _displayScale.x,
                _baseScale.x
            );
        float targetScaleFactor =
            SafeScaleRatio(
                targetScale.x,
                _baseScale.x
            );
        float scaleError =
            Mathf.Abs(
                displayScaleFactor -
                targetScaleFactor
            );

        if (
            KiwiUltraDisplayMath.ShouldApplyDirectMotion(
                _rawScaleSpeed,
                ultraDirectDisplayScaleSpeed,
                scaleError,
                ultraScaleDeadZone
            )
        )
        {
            _displayScale = targetScale;
        }

        return positionHandled;
    }

    private void RenderDisplayPose()
    {
        if (!_displayPoseInitialized)
        {
            ResetDisplayPoseToSamples();
        }

        RenderRotation(_displayRotation);
        RenderPosition(_displayPosition);
        RenderScale(_displayScale);
    }

    private void ResetDisplayPoseToSamples()
    {
        _displayRotation = _sampleRotation;
        _displayPosition = _samplePosition;
        _displayScale = _sampleScale;
        _displayPoseInitialized = true;
        _lastDisplayAdvanceHostTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();
    }


    private void ResetUltraStaticLocks()
    {
        _ultraRotationStaticLocked = false;
        _ultraPositionStaticLocked = false;
        _ultraScaleStaticLocked = false;
        _ultraRotationStaticTime = 0f;
        _ultraPositionStaticTime = 0f;
        _ultraScaleStaticTime = 0f;
        _ultraRotationStaticAnchor = _sampleRotation;
        _ultraPositionStaticAnchor = _samplePosition;
        _ultraScaleStaticAnchor = _sampleScale;
    }


    private void ResetPrecisionState()
    {
        ResetUltraStaticLocks();
        _hasPrecisionInputHistory =
            false;

        _lastPrecisionInputRotation =
            Quaternion.identity;

        _lastPrecisionInputCenter =
            Vector2.zero;

        _lastPrecisionDepthRatio =
            1f;

        _lastPrecisionQuality =
            1f;

        ResetRejectedCandidateState();
        ResetPredictionHistory();
    }


    private void ResetPredictionHistory()
    {
        _hasPredictionHistory =
            false;

        _predictionPreviousRotation =
            _sampleRotation;

        _predictionPreviousPosition =
            _samplePosition;

        _predictionPreviousScale =
            _sampleScale;

        _predictionAngularVelocityDegrees =
            Vector3.zero;

        _predictionPositionVelocity =
            Vector3.zero;

        _predictionScaleVelocity =
            Vector3.zero;

        _hasPredictionRawVelocityHistory =
            false;

        _predictionPreviousRawAngularVelocity =
            Vector3.zero;

        _predictionPreviousRawPositionVelocity =
            Vector3.zero;

        _predictionPreviousRawScaleVelocity =
            Vector3.zero;

        _predictionRotationConsistency =
            0f;

        _predictionPositionConsistency =
            0f;

        _predictionScaleConsistency =
            0f;

        _lastAcceptedSampleInterval =
            1f / 30f;

        _lastPrecisionSubmissionHostTicks =
            0L;

        _lastPrecisionArrivalHostTicks =
            0L;

        _lastPredictionAgeMs =
            0f;

        _lastInferenceLatencyMs =
            0f;

        _lastCaptureAgeCompensationMs =
            0f;
    }


    // =========================================================
    // Signed angular velocity
    //
    // Motion Accentだけに使用。
    // Trackingには使用しない。
    // =========================================================

    private void UpdateSignedAngularVelocity(
        float pitch,
        float yaw,
        float roll,
        float dt)
    {
        if (!_hasPreviousAngles)
        {
            _previousPitch =
                pitch;

            _previousYaw =
                yaw;

            _previousRoll =
                roll;

            _hasPreviousAngles =
                true;

            return;
        }


        float pitchVelocity =
            (
                pitch -
                _previousPitch
            )
            /
            Mathf.Max(
                dt,
                0.0001f
            );


        float yawVelocity =
            (
                yaw -
                _previousYaw
            )
            /
            Mathf.Max(
                dt,
                0.0001f
            );


        float rollVelocity =
            (
                roll -
                _previousRoll
            )
            /
            Mathf.Max(
                dt,
                0.0001f
            );


        pitchVelocity =
            Mathf.Clamp(
                pitchVelocity,
                -720f,
                720f
            );


        yawVelocity =
            Mathf.Clamp(
                yawVelocity,
                -720f,
                720f
            );


        rollVelocity =
            Mathf.Clamp(
                rollVelocity,
                -720f,
                720f
            );


        // この平滑化はAccentだけ。
        // 本体追従には一切使わない。

        float t =
            ExpFactor(
                motionAccentVelocityResponse,
                dt
            );


        _signedPitchVelocity =
            Mathf.Lerp(
                _signedPitchVelocity,
                pitchVelocity,
                t
            );


        _signedYawVelocity =
            Mathf.Lerp(
                _signedYawVelocity,
                yawVelocity,
                t
            );


        _signedRollVelocity =
            Mathf.Lerp(
                _signedRollVelocity,
                rollVelocity,
                t
            );


        _previousPitch =
            pitch;

        _previousYaw =
            yaw;

        _previousRoll =
            roll;
    }


    // =========================================================
    // Motion Accent
    // =========================================================

    private void UpdateMotionAccent(
        float dt)
    {
        if (!enableMotionAccent)
        {
            ResetMotionAccentSmooth(
                dt
            );

            return;
        }


        float sampleAge =
            Time.unscaledTime -
            _lastMotionSampleTime;


        float freshness =
            1f
            -
            Mathf.InverseLerp(
                0.045f,
                0.12f,
                sampleAge
            );


        freshness =
            Mathf.Clamp01(
                freshness
            );


        float yawVelocity =
            Mathf.Clamp(
                _signedYawVelocity /
                Mathf.Max(
                    1f,
                    motionAccentYawFullSpeed
                ),
                -1f,
                1f
            )
            *
            freshness;


        float pitchVelocity =
            Mathf.Clamp(
                _signedPitchVelocity /
                Mathf.Max(
                    1f,
                    motionAccentPitchFullSpeed
                ),
                -1f,
                1f
            )
            *
            freshness;


        float rollVelocity =
            Mathf.Clamp(
                _signedRollVelocity /
                Mathf.Max(
                    1f,
                    motionAccentRollFullSpeed
                ),
                -1f,
                1f
            )
            *
            freshness;


        float targetYaw =
            yawVelocity
            *
            motionAccentYawAmount
            *
            reactionMultiplier;


        float targetPitch =
            pitchVelocity
            *
            motionAccentPitchAmount
            *
            reactionMultiplier;


        float targetRoll =
            (
                -yawVelocity
                *
                motionAccentRollFromYaw

                +

                rollVelocity
                *
                motionAccentRollAmount
            )
            *
            reactionMultiplier;


        targetYaw =
            Mathf.Clamp(
                targetYaw,
                -12f,
                12f
            );


        targetPitch =
            Mathf.Clamp(
                targetPitch,
                -10f,
                10f
            );


        targetRoll =
            Mathf.Clamp(
                targetRoll,
                -15f,
                15f
            );


        float speed =
            Mathf.Max(
                Mathf.Abs(yawVelocity),
                Mathf.Abs(pitchVelocity),
                Mathf.Abs(rollVelocity)
            );


        float targetStretch =
            speed
            *
            motionAccentStretchAmount
            *
            reactionMultiplier;


        targetStretch =
            Mathf.Clamp(
                targetStretch,
                0f,
                0.10f
            );


        _accentYaw =
            SmoothAccent(
                _accentYaw,
                targetYaw,
                dt
            );


        _accentPitch =
            SmoothAccent(
                _accentPitch,
                targetPitch,
                dt
            );


        _accentRoll =
            SmoothAccent(
                _accentRoll,
                targetRoll,
                dt
            );


        _accentStretch =
            SmoothAccent(
                _accentStretch,
                targetStretch,
                dt
            );
    }


    private float SmoothAccent(
        float current,
        float target,
        float dt)
    {
        float response =
            Mathf.Abs(target) >
            Mathf.Abs(current)
                ?
                motionAccentResponse
                :
                motionAccentRelease;


        return Mathf.Lerp(
            current,
            target,
            ExpFactor(
                response,
                dt
            )
        );
    }


    private void ResetMotionAccentSmooth(
        float dt)
    {
        float t =
            ExpFactor(
                motionAccentRelease,
                dt
            );


        _accentPitch =
            Mathf.Lerp(
                _accentPitch,
                0f,
                t
            );


        _accentYaw =
            Mathf.Lerp(
                _accentYaw,
                0f,
                t
            );


        _accentRoll =
            Mathf.Lerp(
                _accentRoll,
                0f,
                t
            );


        _accentStretch =
            Mathf.Lerp(
                _accentStretch,
                0f,
                t
            );
    }


    // =========================================================
    // Reaction State
    // =========================================================

    private void UpdateReactionState(
        float dt)
    {
        float surprise =
            ExpressionSurprise();


        bool crossed =
            _previousSurprise <
            surpriseThreshold
            &&
            surprise >=
            surpriseThreshold;


        bool cooldownReady =
            Time.unscaledTime -
            _lastSurpriseTriggerTime
            >=
            surpriseCooldown;


        if (
            enableSurpriseJump &&
            crossed &&
            cooldownReady
        )
        {
            _surpriseActive =
                true;


            _surpriseStartTime =
                Time.unscaledTime;


            _lastSurpriseTriggerTime =
                Time.unscaledTime;


            _surpriseStrength =
                0.70f;
        }


        if (_surpriseActive)
        {
            float strength =
                Mathf.InverseLerp(
                    surpriseThreshold,
                    1f,
                    surprise
                );


            strength =
                Mathf.SmoothStep(
                    0f,
                    1f,
                    strength
                );


            _surpriseStrength =
                Mathf.Max(
                    _surpriseStrength,
                    Mathf.Lerp(
                        0.70f,
                        1f,
                        strength
                    )
                );


            if (
                Time.unscaledTime -
                _surpriseStartTime
                >=
                surpriseReactionDuration
            )
            {
                _surpriseActive =
                    false;
            }
        }


        _previousSurprise =
            surprise;


        _happyPhase =
            RepeatPhase(
                _happyPhase
                +
                dt *
                happyWiggleSpeed *
                Mathf.PI *
                2f
            );


        _talkPhase =
            RepeatPhase(
                _talkPhase
                +
                dt *
                talkWiggleSpeed *
                Mathf.PI *
                2f
            );


        _grumpyPhase =
            RepeatPhase(
                _grumpyPhase
                +
                dt *
                grumpyShakeSpeed *
                Mathf.PI *
                2f
            );


        _idleBreathPhase =
            RepeatPhase(
                _idleBreathPhase
                +
                dt *
                idleBreathSpeed *
                Mathf.PI *
                2f
            );


        _idleSwayPhase =
            RepeatPhase(
                _idleSwayPhase
                +
                dt *
                idleSwaySpeed *
                Mathf.PI *
                2f
            );
    }


    // =========================================================
    // Render Rotation
    //
    // ★Tracking値を直接Transformへ入れる。
    // =========================================================

    private void RenderRotation(
        Quaternion trackingRotation)
    {
        if (enableUltraLowLatencyTracking && ultraDisableSecondaryBodyMotion)
        {
            kiwiRoot.localRotation = trackingRotation;
            return;
        }

        float surprise =
            ExpressionSurprise();


        float smile =
            ExpressionSmile();


        float talk =
            ExpressionTalk();


        float grumpy =
            ExpressionGrumpy();


        // =====================================================
        // Happy
        // =====================================================

        float happyWeight =
            Mathf.Pow(
                Mathf.Clamp01(
                    smile
                    *
                    (
                        1f -
                        surprise
                    )
                    *
                    (
                        1f -
                        grumpy *
                        0.70f
                    )
                ),
                happyIntensityPower
            );


        float happyWave =
            Mathf.Sin(
                _happyPhase
            );


        float happyYaw =
            enableHappyWiggle
                ?
                -happyWave
                *
                happyYawAmount
                *
                reactionMultiplier
                *
                happyWeight
                :
                0f;


        float happyRoll =
            enableHappyWiggle
                ?
                happyWave
                *
                happyRollAmount
                *
                reactionMultiplier
                *
                happyWeight
                :
                0f;


        // =====================================================
        // Talk
        // =====================================================

        float talkRoll =
            enableTalkingMotion
                ?
                Mathf.Sin(
                    _talkPhase
                )
                *
                talkRollAmount
                *
                reactionMultiplier
                *
                talk
                *
                (
                    1f -
                    surprise
                )
                :
                0f;


        // =====================================================
        // Grumpy
        // =====================================================

        float grumpyYaw =
            enableGrumpyShake
                ?
                Mathf.Sin(
                    _grumpyPhase
                )
                *
                grumpyYawAmount
                *
                reactionMultiplier
                *
                grumpy
                *
                (
                    1f -
                    surprise
                )
                :
                0f;


        // =====================================================
        // Idle
        // =====================================================

        float idleRoll =
            enableIdleLife
                ?
                Mathf.Sin(
                    _idleSwayPhase
                )
                *
                idleSwayRollAmount
                *
                CalculateIdleWeight()
                :
                0f;


        happyYaw =
            Mathf.Clamp(
                happyYaw,
                -25f,
                25f
            );


        grumpyYaw =
            Mathf.Clamp(
                grumpyYaw,
                -18f,
                18f
            );


        float extraRoll =
            Mathf.Clamp(
                happyRoll +
                talkRoll +
                idleRoll,
                -45f,
                45f
            );


        float accentSuppression =
            1f -
            surprise *
            0.70f;


        Quaternion secondary =
            Quaternion.Euler(
                _accentPitch *
                accentSuppression,

                happyYaw +
                grumpyYaw +
                _accentYaw *
                accentSuppression,

                extraRoll +
                _accentRoll *
                accentSuppression
            );


        // =====================================================
        // ★NO SLERP
        // =====================================================

        kiwiRoot.localRotation =
            trackingRotation *
            secondary;
    }


    // =========================================================
    // Render Position
    //
    // ★NO LERP
    // =========================================================

    private void RenderPosition(
        Vector3 trackingPosition)
    {
        if (enableUltraLowLatencyTracking && ultraDisableSecondaryBodyMotion)
        {
            kiwiRoot.localPosition = trackingPosition;
            return;
        }

        Vector3 target =
            trackingPosition;


        float surprise =
            ExpressionSurprise();


        target.y +=
            CalculateSurpriseJump();


        if (enableTalkingMotion)
        {
            float talk =
                ExpressionTalk()
                *
                (
                    1f -
                    surprise
                );


            float wave =
                Mathf.Abs(
                    Mathf.Sin(
                        _talkPhase
                    )
                );


            target.y +=
                _modelHeight
                *
                talkBounceHeight
                *
                reactionMultiplier
                *
                talk
                *
                wave;
        }


        if (enableGrumpyShake)
        {
            target.y -=
                _modelHeight
                *
                grumpyDropAmount
                *
                reactionMultiplier
                *
                ExpressionGrumpy()
                *
                (
                    1f -
                    surprise
                );
        }


        if (enableIdleLife)
        {
            target.y +=
                _modelHeight
                *
                idleBobAmount
                *
                Mathf.Sin(
                    _idleBreathPhase
                )
                *
                CalculateIdleWeight();
        }


        kiwiRoot.localPosition =
            target;
    }


    // =========================================================
    // Render Scale
    //
    // ★NO LERP
    // =========================================================

    private void RenderScale(
        Vector3 trackingScale)
    {
        if (enableUltraLowLatencyTracking && ultraDisableSecondaryBodyMotion)
        {
            kiwiRoot.localScale = trackingScale;
            return;
        }

        Vector3 target =
            trackingScale;


        target =
            Vector3.Scale(
                target,
                CalculateIdleBreathingScale()
            );


        target =
            Vector3.Scale(
                target,
                CalculateMotionAccentScale()
            );


        target =
            Vector3.Scale(
                target,
                CalculateSurpriseScale()
            );


        target =
            Vector3.Scale(
                target,
                CalculateBlinkScale()
            );


        target =
            Vector3.Scale(
                target,
                CalculateTalkScale()
            );


        target =
            Vector3.Scale(
                target,
                CalculatePoutScale()
            );


        kiwiRoot.localScale =
            target;
    }


    // =========================================================
    // Motion Accent Scale
    // =========================================================

    private Vector3 CalculateMotionAccentScale()
    {
        if (!enableMotionAccent)
        {
            return Vector3.one;
        }


        return VolumePreservingYScale(
            1f +
            Mathf.Clamp(
                _accentStretch,
                0f,
                0.10f
            )
        );
    }


    // =========================================================
    // Surprise Jump
    // =========================================================

    private float CalculateSurpriseJump()
    {
        if (
            !enableSurpriseJump ||
            !_surpriseActive
        )
        {
            return 0f;
        }


        float elapsed =
            Time.unscaledTime -
            _surpriseStartTime;


        if (
            elapsed >=
            surpriseJumpDuration
        )
        {
            return 0f;
        }


        float amplitude =
            _modelHeight
            *
            surpriseJumpHeight
            *
            reactionMultiplier
            *
            _surpriseStrength;


        float wave =
            Mathf.Sin(
                elapsed
                *
                surpriseJumpFrequency
                *
                Mathf.PI
                +
                Mathf.PI *
                0.5f
            );


        float damping =
            Mathf.Exp(
                -surpriseJumpDecay *
                elapsed
            );


        return
            amplitude *
            wave *
            damping;
    }


    // =========================================================
    // Surprise Scale
    // =========================================================

    private Vector3 CalculateSurpriseScale()
    {
        if (
            !enableSurpriseSquash ||
            !_surpriseActive
        )
        {
            return Vector3.one;
        }


        float elapsed =
            Time.unscaledTime -
            _surpriseStartTime;


        float stretch =
            surpriseStretchAmount *
            reactionMultiplier;


        float squash =
            surpriseSquashAmount *
            reactionMultiplier;


        float y;


        if (
            elapsed <
            surpriseStretchHoldTime
        )
        {
            float attackTime =
                Mathf.Min(
                    0.045f,
                    surpriseStretchHoldTime *
                    0.5f
                );


            float attack =
                Mathf.SmoothStep(
                    0f,
                    1f,
                    Mathf.InverseLerp(
                        0f,
                        attackTime,
                        elapsed
                    )
                );


            y =
                1f +
                stretch *
                _surpriseStrength *
                attack;
        }
        else
        {
            float animationTime =
                elapsed -
                surpriseStretchHoldTime;


            float wave =
                Mathf.Cos(
                    animationTime
                    *
                    squashStretchFrequency
                    *
                    Mathf.PI
                );


            float damping =
                Mathf.Exp(
                    -squashStretchDecay *
                    animationTime
                );


            float strength =
                damping *
                _surpriseStrength;


            if (wave >= 0f)
            {
                y =
                    1f +
                    wave *
                    stretch *
                    strength;
            }
            else
            {
                y =
                    1f +
                    wave *
                    squash *
                    strength;
            }
        }


        y =
            Mathf.Clamp(
                y,
                0.55f,
                1.65f
            );


        return VolumePreservingYScale(
            y
        );
    }


    // =========================================================
    // Blink
    // =========================================================

    private Vector3 CalculateBlinkScale()
    {
        if (!enableBlinkSquash)
        {
            return Vector3.one;
        }


        float blink =
            Mathf.Pow(
                ExpressionBlink(),
                blinkSquashPower
            );


        float y =
            1f
            -
            blinkSquashAmount
            *
            reactionMultiplier
            *
            blink;


        y =
            Mathf.Clamp(
                y,
                0.60f,
                1f
            );


        return VolumePreservingYScale(
            y
        );
    }


    // =========================================================
    // Talk
    // =========================================================

    private Vector3 CalculateTalkScale()
    {
        if (!enableTalkingMotion)
        {
            return Vector3.one;
        }


        float talk =
            ExpressionTalk()
            *
            (
                1f -
                ExpressionSurprise()
            );


        float wave =
            0.5f
            +
            0.5f
            *
            Mathf.Abs(
                Mathf.Sin(
                    _talkPhase
                )
            );


        float y =
            1f
            +
            talkStretchAmount
            *
            reactionMultiplier
            *
            talk
            *
            wave;


        y =
            Mathf.Clamp(
                y,
                1f,
                1.35f
            );


        return VolumePreservingYScale(
            y
        );
    }


    // =========================================================
    // Pout
    // =========================================================

    private Vector3 CalculatePoutScale()
    {
        if (!enablePoutPuff)
        {
            return Vector3.one;
        }


        float pout =
            ExpressionPout()
            *
            (
                1f -
                ExpressionSurprise()
            );


        float horizontal =
            1f
            +
            poutPuffAmount
            *
            reactionMultiplier
            *
            pout;


        horizontal =
            Mathf.Clamp(
                horizontal,
                1f,
                1.35f
            );


        float y =
            1f /
            Mathf.Max(
                0.5f,
                horizontal *
                horizontal
            );


        return new Vector3(
            horizontal,
            y,
            horizontal
        );
    }


    // =========================================================
    // Idle breathing
    // =========================================================

    private Vector3 CalculateIdleBreathingScale()
    {
        if (!enableIdleLife)
        {
            return Vector3.one;
        }


        float y =
            1f
            +
            Mathf.Sin(
                _idleBreathPhase
            )
            *
            idleBreathAmount
            *
            CalculateIdleWeight();


        return VolumePreservingYScale(
            y
        );
    }


    // =========================================================
    // Idle weight
    // =========================================================

    private float CalculateIdleWeight()
    {
        float expressionActivity =
            Mathf.Max(
                ExpressionSurprise(),
                ExpressionSmile(),
                ExpressionMouthOpen(),
                ExpressionGrumpy(),
                ExpressionPout()
            );


        float motionActivity =
            Mathf.InverseLerp(
                5f,
                60f,
                _rawAngularSpeed
            );


        return Mathf.Clamp01(
            (
                1f -
                expressionActivity
            )
            *
            (
                1f -
                motionActivity
            )
        );
    }


    // =========================================================
    // Expressions
    // =========================================================

    private float ExpressionBlink()
    {
        return expressionReaction != null
            ? expressionReaction.BlinkIntensity
            : 0f;
    }


    private float ExpressionSmile()
    {
        return expressionReaction != null
            ? expressionReaction.SmileIntensity
            : 0f;
    }


    private float ExpressionSurprise()
    {
        return expressionReaction != null
            ? expressionReaction.SurpriseIntensity
            : 0f;
    }


    private float ExpressionTalk()
    {
        return expressionReaction != null
            ? expressionReaction.TalkPulseIntensity
            : 0f;
    }


    private float ExpressionMouthOpen()
    {
        return expressionReaction != null
            ? expressionReaction.MouthOpenIntensity
            : 0f;
    }


    private float ExpressionPout()
    {
        return expressionReaction != null
            ? expressionReaction.PoutIntensity
            : 0f;
    }


    private float ExpressionGrumpy()
    {
        return expressionReaction != null
            ? expressionReaction.GrumpyIntensity
            : 0f;
    }


    // =========================================================
    // Runtime presets / app controls
    // =========================================================

    public void ApplyUltraTrackingPreset()
    {
        avatarCentricHorizontalMovement = true;
        enableUltraLowLatencyTracking = true;
        landMarkerSpeedMode = true;
        enableHybridPrecisionTracking = true;
        enablePrecisionOutlierGuard = true;
        useBoundedLatestResultCorrection = true;
        enableRenderTimeLatePrediction = true;
        ultraUseRunnerPositionAnchor = true;
        virtualNeckExtension = 1.30f;
        ultraConsumeLatestSampleBeforeRender = true;
        ultraDisableSecondaryBodyMotion = true;
        ultraAdaptiveMicroFilter = true;
        ultraStaticPoseLock = true;
        ultraStaticLockSeconds = 0.065f;

        ultraRotationDeadZone = 0.18f;
        ultraRotationStaticReleaseSpeed = 18f;
        ultraRotationSlowResponse = 120f;
        ultraRotationFastResponse = 220f;
        ultraRotationDirectSpeed = 110f;
        ultraRotationDirectError = 1.20f;

        ultraPositionDeadZone = 0.00060f;
        ultraPositionStaticReleaseSpeed = 0.035f;
        ultraPositionSlowResponse = 80f;
        ultraPositionFastResponse = 180f;
        ultraPositionDirectSpeed = 0.150f;
        ultraPositionDirectError = 0.00150f;

        ultraScaleDeadZone = 0.00150f;
        ultraScaleStaticReleaseSpeed = 0.200f;
        ultraScaleSlowResponse = 80f;
        ultraScaleFastResponse = 160f;
        ultraScaleDirectSpeed = 0.500f;
        ultraScaleDirectError = 0.0120f;

        ultraPredictionStrength = 1.00f;
        ultraMaxPredictionSeconds = 0.100f;
        ultraPredictionMinRotationSpeed = 3f;
        ultraPredictionMinPositionSpeed = 0.005f;
        ultraPredictionMinScaleSpeed = 0.020f;
        ultraCompensateFullResultAge = true;
        ultraCompensateCameraCaptureAge = true;
        ultraCaptureIntervalFraction = 0.50f;
        ultraMaxCaptureAgeSeconds = 0.020f;

        ultraDisplayRateSmoothing = true;
        ultraDisplaySmoothingResponse = 90f;
        ultraDisplayFastResponse = 220f;
        ultraDirectDisplayDuringMotion = true;
        ultraDirectDisplayRotationSpeed = 18f;
        ultraDirectDisplayPositionSpeed = 0.035f;
        ultraDirectDisplayScaleSpeed = 0.200f;
        ultraPredictivePositionResampling = true;
        ultraPositionCorrectionResponse = 45f;
        ultraPositionRecoveryResponse = 180f;

        predictionStaleTime = 0.180f;
        predictionVelocityResponse = 60f;
        predictionVelocityFastResponse = 180f;
        maxRotationPredictionDegrees = 16f;
        maxPositionPredictionHeight = 0.040f;
        maxScalePrediction = 0.045f;
    }

    public void ApplyDirectLandmarkerPreset()
    {
        avatarCentricHorizontalMovement = true;
        enableUltraLowLatencyTracking = true;
        ultraUseRunnerPositionAnchor = true;
        virtualNeckExtension = 1.30f;
        ultraConsumeLatestSampleBeforeRender = true;
        ultraDisableSecondaryBodyMotion = true;
        ultraAdaptiveMicroFilter = false;
        ultraStaticPoseLock = false;
        enableRenderTimeLatePrediction = false;
        ultraCompensateFullResultAge = false;
        ultraDisplayRateSmoothing = false;
        ultraDirectDisplayDuringMotion = true;
        landMarkerSpeedMode = false;
    }

    public void RecenterTracking()
    {
        BeginCalibration();
    }


    // =========================================================
    // Calibration
    // =========================================================

    [ContextMenu("Recalibrate")]
    public void BeginCalibration()
    {
        _calibrated =
            false;


        _calibrationStarted =
            false;


        _calibrationSamples =
            0;


        _neutralCenter =
            Vector2.zero;


        _neutralEyeSpan =
            0f;


        _neutralEyeSpan3D =
            0f;

        _neutralFaceWidth2D =
            0f;

        _neutralFaceHeight2D =
            0f;

        _precisionGeometryCalibrationSamples =
            0;


        _neutralFaceRotation =
            Quaternion.identity;


        _positionGeometrySamples =
            0;


        _neutralEyeCenter =
            Vector2.zero;


        _neutralEyeLine =
            Vector2.zero;


        _neutralEyeToChin =
            Vector2.zero;


        _hasNeutralPositionGeometry =
            false;


        _lastObservedTimestamp =
            -1;

        _lastObservedFrameId =
            0UL;

        _lastAcceptedTimestamp =
            -1;

        _lastAcceptedSampleHostTicks =
            0L;

        _lastAcceptedUsedMatchedSubmissionTiming =
            false;

        _lastAcceptedBackend =
            KiwiTrackingBackend.Unknown;

        _lastBoundedCorrectionChannels =
            0;

        _boundedCorrectionCount =
            0;


        _lastSeenTime =
            -100f;


        _trackingWasLost =
            true;


        _lastRawScaleFactor =
            1f;


        _rawAngularSpeed =
            0f;


        _rawPositionSpeed =
            0f;


        _rawScaleSpeed =
            0f;


        ResetPrecisionState();


        ResetMotionAccent();
        ResetReactionState();
    }


    private void AddCalibrationSample(
        Vector2 center,
        FacePrecisionTrackingData precisionData,
        bool hasPositionGeometry,
        PositionGeometry positionGeometry)
    {
        float eyeSpan =
            precisionData.eyeSpan2D;


        Quaternion rotation =
            precisionData.faceRotation;


        const float MinimumPrecisionCalibrationGeometryQuality =
            0.30f;


        bool precisionGeometryUsable =
            precisionData.geometryQuality >=
            MinimumPrecisionCalibrationGeometryQuality;


        if (!_calibrationStarted)
        {
            _calibrationStarted =
                true;


            _calibrationStartTime =
                Time.unscaledTime;
        }


        _calibrationSamples++;


        float weight =
            1f /
            _calibrationSamples;


        if (_calibrationSamples == 1)
        {
            _neutralCenter =
                center;


            _neutralEyeSpan =
                eyeSpan;


            _neutralFaceRotation =
                rotation;
        }
        else
        {
            _neutralCenter =
                Vector2.Lerp(
                    _neutralCenter,
                    center,
                    weight
                );


            _neutralEyeSpan =
                Mathf.Lerp(
                    _neutralEyeSpan,
                    eyeSpan,
                    weight
                );


            _neutralFaceRotation =
                Quaternion.Slerp(
                    _neutralFaceRotation,
                    rotation,
                    weight
                );
        }


        if (
            precisionGeometryUsable &&
            precisionData.eyeSpan3D > 0.0001f &&
            precisionData.faceWidth2D > 0.0001f &&
            precisionData.faceHeight2D > 0.0001f
        )
        {
            _precisionGeometryCalibrationSamples++;


            float precisionWeight =
                1f /
                _precisionGeometryCalibrationSamples;


            if (_precisionGeometryCalibrationSamples == 1)
            {
                _neutralEyeSpan3D =
                    precisionData.eyeSpan3D;

                _neutralFaceWidth2D =
                    precisionData.faceWidth2D;

                _neutralFaceHeight2D =
                    precisionData.faceHeight2D;
            }
            else
            {
                _neutralEyeSpan3D =
                    Mathf.Lerp(
                        _neutralEyeSpan3D,
                        precisionData.eyeSpan3D,
                        precisionWeight
                    );

                _neutralFaceWidth2D =
                    Mathf.Lerp(
                        _neutralFaceWidth2D,
                        precisionData.faceWidth2D,
                        precisionWeight
                    );

                _neutralFaceHeight2D =
                    Mathf.Lerp(
                        _neutralFaceHeight2D,
                        precisionData.faceHeight2D,
                        precisionWeight
                    );
            }
        }


        if (
            hasPositionGeometry &&
            precisionGeometryUsable
        )
        {
            _positionGeometrySamples++;


            float geometryWeight =
                1f /
                _positionGeometrySamples;


            if (_positionGeometrySamples == 1)
            {
                _neutralEyeCenter =
                    positionGeometry.eyeCenter;


                _neutralEyeLine =
                    positionGeometry.eyeLine;


                _neutralEyeToChin =
                    positionGeometry.eyeToChin;
            }
            else
            {
                _neutralEyeCenter =
                    Vector2.Lerp(
                        _neutralEyeCenter,
                        positionGeometry.eyeCenter,
                        geometryWeight
                    );


                _neutralEyeLine =
                    Vector2.Lerp(
                        _neutralEyeLine,
                        positionGeometry.eyeLine,
                        geometryWeight
                    );


                _neutralEyeToChin =
                    Vector2.Lerp(
                        _neutralEyeToChin,
                        positionGeometry.eyeToChin,
                        geometryWeight
                    );
            }
        }


        if (
            Time.unscaledTime -
            _calibrationStartTime
            >=
            calibrationSeconds
            &&
            _calibrationSamples >=
            minimumCalibrationSamples
        )
        {
            FinishCalibration();
        }
    }


    private void FinishCalibration()
    {
        _hasNeutralPositionGeometry =
            _positionGeometrySamples >= 3
            &&
            _neutralEyeLine.sqrMagnitude > 0.0000001f
            &&
            _neutralEyeToChin.sqrMagnitude > 0.0000001f;


        if (
            (!enableUltraLowLatencyTracking || !ultraUseRunnerPositionAnchor) &&
            useRollStablePositionAnchor &&
            _hasNeutralPositionGeometry
        )
        {
            _neutralCenter =
                _neutralEyeCenter
                +
                _neutralEyeToChin
                *
                virtualNeckExtension;
        }


        _calibrated =
            true;


        _sampleRotation =
            _baseRotation;


        _samplePosition =
            _basePosition;


        _sampleScale =
            _baseScale;


        ResetDisplayPoseToSamples();


        _lastRawRotation =
            _baseRotation;


        _lastRawCenter =
            _neutralCenter;


        _lastRawScaleFactor =
            1f;


        _lastSeenTime =
            Time.unscaledTime;


        _trackingWasLost =
            false;


        _lastPrecisionInputRotation =
            _neutralFaceRotation;

        _lastPrecisionInputCenter =
            _neutralCenter;

        _lastPrecisionDepthRatio =
            1f;

        _hasPrecisionInputHistory =
            true;


        ResetPredictionHistory();
        ResetUltraStaticLocks();


        ResetMotionAccent();
    }


    // =========================================================
    // Tracking lost
    // =========================================================

    private void ReturnToNeutral(
        float dt)
    {
        float t =
            ExpFactor(
                returnToNeutralResponse,
                dt
            );


        // Tracking消失時だけ補間する。
        // 通常追従には一切影響なし。

        kiwiRoot.localRotation =
            Quaternion.Slerp(
                kiwiRoot.localRotation,
                _baseRotation,
                t
            );


        kiwiRoot.localPosition =
            Vector3.Lerp(
                kiwiRoot.localPosition,
                _basePosition,
                t
            );


        kiwiRoot.localScale =
            Vector3.Lerp(
                kiwiRoot.localScale,
                _baseScale,
                t
            );


        _sampleRotation =
            kiwiRoot.localRotation;


        _samplePosition =
            kiwiRoot.localPosition;


        _sampleScale =
            kiwiRoot.localScale;


        ResetDisplayPoseToSamples();
    }


    // =========================================================
    // Reset
    // =========================================================

    private void ResetMotionAccent()
    {
        _hasPreviousAngles =
            false;


        _previousPitch = 0f;
        _previousYaw = 0f;
        _previousRoll = 0f;


        _signedPitchVelocity = 0f;
        _signedYawVelocity = 0f;
        _signedRollVelocity = 0f;


        _accentPitch = 0f;
        _accentYaw = 0f;
        _accentRoll = 0f;
        _accentStretch = 0f;


        _lastMotionSampleTime =
            -100f;
    }


    private void ResetReactionState()
    {
        _previousSurprise =
            0f;


        _surpriseActive =
            false;


        _surpriseStrength =
            0.70f;
    }


    // =========================================================
    // Optional nonlinear boost
    // =========================================================

    private float ApplyReactionBoost(
        float angle,
        float maximumBoost)
    {
        if (maximumBoost <= 1.0001f)
        {
            return angle;
        }


        float t =
            Mathf.InverseLerp(
                reactionBoostStart,
                reactionBoostFull,
                Mathf.Abs(angle)
            );


        t =
            Mathf.SmoothStep(
                0f,
                1f,
                t
            );


        return
            angle *
            Mathf.Lerp(
                1f,
                maximumBoost,
                t
            );
    }


    // =========================================================
    // Model height
    // =========================================================

    private float CalculateModelLocalHeight()
    {
        Renderer[] renderers =
            kiwiRoot.GetComponentsInChildren<Renderer>(
                true
            );


        if (
            renderers == null ||
            renderers.Length == 0
        )
        {
            return 1f;
        }


        Bounds bounds =
            renderers[0].bounds;


        for (
            int i = 1;
            i < renderers.Length;
            i++
        )
        {
            bounds.Encapsulate(
                renderers[i].bounds
            );
        }


        float scaleY =
            Mathf.Abs(
                kiwiRoot.lossyScale.y
            );


        if (scaleY < 0.00001f)
        {
            scaleY = 1f;
        }


        return Mathf.Max(
            0.1f,
            bounds.size.y /
            scaleY
        );
    }


    // =========================================================
    // Utilities
    // =========================================================

    private float SignedAngle(
        float angle)
    {
        if (angle > 180f)
        {
            angle -= 360f;
        }


        return angle;
    }


    private float SafeScaleRatio(
        float value,
        float baseValue)
    {
        if (
            Mathf.Abs(baseValue)
            <
            0.000001f
        )
        {
            return 1f;
        }


        return
            value /
            baseValue;
    }


    private float ExpFactor(
        float response,
        float dt)
    {
        return
            1f -
            Mathf.Exp(
                -response *
                dt
            );
    }


    private float RepeatPhase(
        float phase)
    {
        return Mathf.Repeat(
            phase,
            Mathf.PI *
            2f
        );
    }


    private Vector3 VolumePreservingYScale(
        float y)
    {
        y =
            Mathf.Max(
                0.1f,
                y
            );


        float horizontal =
            1f /
            Mathf.Sqrt(y);


        return new Vector3(
            horizontal,
            y,
            horizontal
        );
    }
}


public static class KiwiUltraDisplayMath
{
    public static float CalculateCaptureAgeCompensation(
        float sourceRateHz,
        float intervalFraction,
        float maximumSeconds)
    {
        if (
            float.IsNaN(sourceRateHz) ||
            float.IsInfinity(sourceRateHz) ||
            sourceRateHz <= 0f
        )
        {
            return 0f;
        }

        float interval = 1f / Mathf.Clamp(sourceRateHz, 1f, 240f);
        return Mathf.Min(
            interval * Mathf.Max(0f, intervalFraction),
            Mathf.Max(0f, maximumSeconds)
        );
    }

    public static float CalculateAdaptiveVelocityResponse(
        float baseResponse,
        float fastResponse,
        float velocityConsistency)
    {
        float stable = Mathf.Max(0f, baseResponse);
        float fast = Mathf.Max(stable, fastResponse);
        float consistency = Mathf.Clamp01(velocityConsistency);

        // Trust fast velocity changes only when consecutive LandMarker motion
        // agrees. Inconsistent/noisy motion retains the stable base response.
        return Mathf.Lerp(
            stable,
            fast,
            consistency * consistency
        );
    }

    public static float CalculateAdaptiveCorrectionResponse(
        float steadyResponse,
        float recoveryResponse,
        float velocityConsistency)
    {
        float steady = Mathf.Max(0f, steadyResponse);
        float recovery = Mathf.Max(steady, recoveryResponse);
        float consistency = Mathf.Clamp01(velocityConsistency);

        // Constant-velocity movement keeps the low, flicker-resistant response.
        // Stops, accelerations and reversals rapidly converge to the real sample.
        return Mathf.Lerp(
            recovery,
            steady,
            consistency * consistency
        );
    }

    public static bool ShouldApplyDirectMotion(
        float speed,
        float speedThreshold,
        float displayError,
        float errorThreshold)
    {
        if (
            float.IsNaN(speed) ||
            float.IsInfinity(speed) ||
            float.IsNaN(displayError) ||
            float.IsInfinity(displayError)
        )
        {
            return false;
        }

        return
            speed >= Mathf.Max(0f, speedThreshold) &&
            displayError > Mathf.Max(0f, errorThreshold);
    }

    public static Vector3 AdvancePredictivePosition(
        Vector3 current,
        Vector3 target,
        Vector3 velocity,
        float deltaTime,
        float correctionResponse)
    {
        float dt = Mathf.Clamp(deltaTime, 0f, 0.05f);
        Vector3 feedForward =
            current +
            velocity * dt;
        float correction =
            1f -
            Mathf.Exp(
                -Mathf.Max(0f, correctionResponse) *
                dt
            );

        return Vector3.Lerp(
            feedForward,
            target,
            correction
        );
    }
}
