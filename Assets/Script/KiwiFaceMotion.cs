using UnityEngine;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;


// FaceLandmarkerの結果がそのフレーム内で更新されたあとに
// できるだけ遅いタイミングで最新値を取得する。
[DefaultExecutionOrder(10000)]
public class KiwiFaceMotion : MonoBehaviour
{
    [Header("Landmarker Direct Tracking")]
    [Tooltip("ON: rotation, translation and scale follow Landmarker samples directly; dead-zone and procedural body motion are bypassed.")]
    public bool strictLandmarkerTracking = true;

    [Tooltip("ON: consumes a result that arrives after LateUpdate again immediately before rendering.")]
    public bool useBeforeRenderLateLatch = true;


    // =========================================================
    // References
    // =========================================================

    [Header("References")]
    public FaceLandmarkerRunner runner;
    public Transform kiwiRoot;
    public KiwiExpressionReaction expressionReaction;


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

    // 旧方式（配信用Cameraが見つからない場合のみ使用）
    [Range(0f, 3f)]
    public float positionGainX = 0.55f;

    [Range(0f, 3f)]
    public float positionGainY = 0.40f;


    // =========================================================
    // Position Screen Mapping
    //
    // 入力映像上で顔が動いた「画面割合」を、
    // 配信用Camera上の同じ「画面割合」へ直接変換する。
    //
    // 旧方式:
    //   顔移動量 × モデル身長 × Gain
    //
    // ではCamera距離/FOVによって画面上の移動が極端に小さくなる。
    // この方式ならCamera距離・FOV・モデルサイズに依存しない。
    // =========================================================

    [Header("Position Screen Mapping")]

    [Tooltip("ON推奨。顔の平行移動量を配信用Cameraの画面割合へ直接変換します。")]
    public bool useScreenSpacePositionMapping = true;

    [Tooltip("配信用VTuberCameraを指定。未指定時は VTuberCamera → TargetTexture付きCamera → MainCamera の順で自動検索します。")]
    public Camera positionReferenceCamera;

    [Tooltip("入力画面の横移動を配信画面へ反映する倍率。1.0で同じ画面割合。")]
    [Range(0f, 2f)]
    public float screenPositionGainX = 1.00f;

    [Tooltip("入力画面の縦移動を配信画面へ反映する倍率。1.0で同じ画面割合。")]
    [Range(0f, 2f)]
    public float screenPositionGainY = 1.00f;


    [Header("Position Static Jitter Hold")]

    [Tooltip("モデル身長に対する静止DeadZone")]
    [Range(0f, 0.01f)]
    public float positionStaticDeadZone = 0.00025f;

    [Tooltip("顔中心がこの速度以上なら即追従")]
    [Range(0f, 0.2f)]
    public float positionDeadZoneReleaseSpeed = 0.006f;


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
    public float trackingLostTime = 0.20f;

    [Range(1f, 30f)]
    public float returnToNeutralResponse = 5f;


    // =========================================================
    // Base
    // =========================================================

    private Vector3 _basePosition;
    private Quaternion _baseRotation;
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

    private Quaternion _neutralFaceRotation =
        Quaternion.identity;


    // =========================================================
    // Tracking
    // =========================================================

    private long _lastTimestamp = -1;

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
    // Before-render late latch
    // =========================================================

    private void OnEnable()
    {
        Application.onBeforeRender -= OnBeforeRender;
        Application.onBeforeRender += OnBeforeRender;
    }

    private void OnDisable()
    {
        Application.onBeforeRender -= OnBeforeRender;
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


        ConsumeLatestTrackingSample();


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


        bool trackingLost =
            Time.unscaledTime -
            _lastSeenTime
            >
            trackingLostTime;


        if (trackingLost)
        {
            if (!_trackingWasLost)
            {
                ResetMotionAccent();
                ResetReactionState();

                _trackingWasLost = true;
            }


            ReturnToNeutral(
                dt
            );


            return;
        }


        if (strictLandmarkerTracking)
        {
            RenderDirectLandmarkerTransform();
            return;
        }


        UpdateMotionAccent(dt);
        UpdateReactionState(dt);

        RenderRotation();
        RenderPosition();
        RenderScale();
    }


    // =========================================================
    // Latest-sample consumption / late latch
    // =========================================================

    private bool ConsumeLatestTrackingSample()
    {
        if (runner == null || kiwiRoot == null)
        {
            return false;
        }

        bool hasTracking =
            runner.TryGetLatestMotionData(
                out Vector2 center,
                out float faceScale,
                out Quaternion faceRotation,
                out long timestamp
            );

        if (!hasTracking || timestamp == _lastTimestamp)
        {
            return false;
        }

        _lastSeenTime = Time.unscaledTime;

        ProcessNewSample(
            center,
            faceScale,
            faceRotation,
            timestamp
        );

        _lastTimestamp = timestamp;
        _trackingWasLost = false;
        return true;
    }

    private void OnBeforeRender()
    {
        if (!useBeforeRenderLateLatch || runner == null || kiwiRoot == null)
        {
            return;
        }

        ConsumeLatestTrackingSample();

        if (!_calibrated)
        {
            return;
        }

        if (Time.unscaledTime - _lastSeenTime > trackingLostTime)
        {
            return;
        }

        if (strictLandmarkerTracking)
        {
            RenderDirectLandmarkerTransform();
        }
        else
        {
            RenderRotation();
            RenderPosition();
            RenderScale();
        }
    }

    private void RenderDirectLandmarkerTransform()
    {
        kiwiRoot.localRotation = _sampleRotation;
        kiwiRoot.localPosition = _samplePosition;
        kiwiRoot.localScale = _sampleScale;
    }


    // =========================================================
    // New MediaPipe Sample
    // =========================================================

    private void ProcessNewSample(
        Vector2 center,
        float eyeSpan,
        Quaternion faceRotation,
        long timestamp)
    {
        if (!_calibrated)
        {
            AddCalibrationSample(
                center,
                eyeSpan,
                faceRotation
            );

            return;
        }


        float dt =
            1f / 30f;


        if (
            _lastTimestamp >= 0 &&
            timestamp > _lastTimestamp
        )
        {
            dt =
                (
                    timestamp -
                    _lastTimestamp
                )
                /
                1000f;
        }


        dt =
            Mathf.Clamp(
                dt,
                1f / 240f,
                0.10f
            );


        UpdateRotationSample(
            faceRotation,
            dt
        );


        UpdatePositionSample(
            center,
            dt
        );


        UpdateScaleSample(
            eyeSpan,
            dt
        );


        _lastRawCenter =
            center;


        _lastMotionSampleTime =
            Time.unscaledTime;
    }


    // =========================================================
    // Rotation
    // =========================================================

    private void UpdateRotationSample(
        Quaternion faceRotation,
        float dt)
    {
        Quaternion delta =
            faceRotation
            *
            Quaternion.Inverse(
                _neutralFaceRotation
            );


        Vector3 euler =
            delta.eulerAngles;


        float pitch =
            SignedAngle(
                euler.x
            );


        // 自分が右を見る
        // → キウイ自身から見ても右
        float yaw =
            -SignedAngle(
                euler.y
            );


        float roll =
            -SignedAngle(
                euler.z
            );


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


        if (strictLandmarkerTracking)
        {
            _sampleRotation = rawTarget;
            return;
        }


        // =====================================================
        // ★ LandMarker Speed
        // =====================================================

        if (!landMarkerSpeedMode)
        {
            _sampleRotation =
                rawTarget;

            return;
        }


        float error =
            Quaternion.Angle(
                _sampleRotation,
                rawTarget
            );


        bool staticNoise =
            _rawAngularSpeed <
            rotationDeadZoneReleaseSpeed
            &&
            error <
            rotationStaticDeadZone;


        if (!staticNoise)
        {
            // ★100%即反映
            _sampleRotation =
                rawTarget;
        }

        // staticNoiseなら現在位置をHold。
        // Lerp / Slerpは一切しない。
    }


    // =========================================================
    // Position
    // =========================================================

    private void UpdatePositionSample(
        Vector2 center,
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


        Vector3 rawTarget;


        if (useScreenSpacePositionMapping)
        {
            rawTarget =
                CalculateScreenMappedPosition(
                    delta
                );
        }
        else
        {
            rawTarget =
                CalculateLegacyPosition(
                    delta
                );
        }


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


        if (strictLandmarkerTracking)
        {
            _samplePosition = rawTarget;
            return;
        }


        if (!landMarkerSpeedMode)
        {
            _samplePosition =
                rawTarget;

            return;
        }


        // Screen Mappingではモデル身長ではなく、
        // 入力映像上の実移動量で静止ノイズだけをHoldする。
        float inputError =
            Vector2.Distance(
                center,
                _lastRawCenter
            );


        bool staticNoise =
            _rawPositionSpeed <
            positionDeadZoneReleaseSpeed
            &&
            inputError <
            positionStaticDeadZone;


        if (!staticNoise)
        {
            // ★100%即反映
            _samplePosition =
                rawTarget;
        }
    }


    // =========================================================
    // Screen-space position mapping
    //
    // 入力映像で顔が10%横へ動いたら、
    // 配信用Camera上でも10% × Gainだけ動かす。
    //
    // Camera距離・FOV・モデル身長に依存しない。
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
            return
                CalculateLegacyPosition(
                    inputDelta
                );
        }


        Camera cam =
            positionReferenceCamera;


        Transform parent =
            kiwiRoot.parent;


        Vector3 baseWorldPosition =
            parent != null
                ?
                parent.TransformPoint(
                    _basePosition
                )
                :
                _basePosition;


        // ViewportToWorldPointのZはCameraから見た前方向距離。
        float depth =
            Vector3.Dot(
                baseWorldPosition -
                cam.transform.position,

                cam.transform.forward
            );


        depth =
            Mathf.Max(
                depth,
                cam.nearClipPlane +
                0.05f
            );


        // MediaPipe:
        // X = 右が+
        // Y = 下が+
        //
        // Unity Viewport:
        // X = 右が+
        // Y = 上が+
        //
        // キウイは正面向きなので、画面上の右はキウイ自身から見た左。
        // 「頭を右へ平行移動 → キウイ自身から見て右」を成立させるため、
        // XとYを反転する。
        Vector3 viewportBase =
            new Vector3(
                0.5f,
                0.5f,
                depth
            );


        Vector3 viewportMoved =
            new Vector3(
                0.5f -
                inputDelta.x *
                screenPositionGainX,

                0.5f -
                inputDelta.y *
                screenPositionGainY,

                depth
            );


        Vector3 worldBase =
            cam.ViewportToWorldPoint(
                viewportBase
            );


        Vector3 worldMoved =
            cam.ViewportToWorldPoint(
                viewportMoved
            );


        Vector3 worldDelta =
            worldMoved -
            worldBase;


        Vector3 targetWorldPosition =
            baseWorldPosition +
            worldDelta;


        if (parent != null)
        {
            return
                parent.InverseTransformPoint(
                    targetWorldPosition
                );
        }


        return
            targetWorldPosition;
    }


    // =========================================================
    // Legacy position mapping
    //
    // VTuberCameraが取得できない場合だけ使用する。
    // =========================================================

    private Vector3 CalculateLegacyPosition(
        Vector2 delta)
    {
        return
            _basePosition
            +
            new Vector3(
                -delta.x
                *
                _modelHeight
                *
                positionGainX,

                -delta.y
                *
                _modelHeight
                *
                positionGainY,

                0f
            );
    }


    // =========================================================
    // Resolve VTuber output camera
    // =========================================================

    private Camera ResolvePositionReferenceCamera()
    {
        Camera[] cameras =
            FindObjectsOfType<Camera>(
                true
            );


        // 1. 名前がVTuberCamera
        for (
            int i = 0;
            i < cameras.Length;
            i++
        )
        {
            Camera cam =
                cameras[i];


            if (
                cam != null &&
                cam.name ==
                "VTuberCamera"
            )
            {
                return cam;
            }
        }


        // 2. RenderTextureへ出力しているCamera
        for (
            int i = 0;
            i < cameras.Length;
            i++
        )
        {
            Camera cam =
                cameras[i];


            if (
                cam != null &&
                cam.targetTexture != null
            )
            {
                return cam;
            }
        }


        // 3. 最終フォールバック
        return
            Camera.main;
    }


    // =========================================================
    // Depth
    // =========================================================

    private void UpdateScaleSample(
        float eyeSpan,
        float dt)
    {
        float targetFactor =
            1f;


        if (
            enableDistanceScale &&
            _neutralEyeSpan > 0.0001f &&
            eyeSpan > 0.0001f
        )
        {
            float ratio =
                eyeSpan /
                _neutralEyeSpan;


            targetFactor =
                1f
                +
                (
                    ratio -
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


        if (strictLandmarkerTracking)
        {
            _sampleScale = rawTarget;
            return;
        }


        if (!landMarkerSpeedMode)
        {
            _sampleScale =
                rawTarget;

            return;
        }


        float currentFactor =
            SafeScaleRatio(
                _sampleScale.x,
                _baseScale.x
            );


        float error =
            Mathf.Abs(
                targetFactor -
                currentFactor
            );


        bool staticNoise =
            _rawScaleSpeed <
            scaleDeadZoneReleaseSpeed
            &&
            error <
            scaleStaticDeadZone;


        if (!staticNoise)
        {
            // ★100%即反映
            _sampleScale =
                rawTarget;
        }
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

    private void RenderRotation()
    {
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
            _sampleRotation *
            secondary;
    }


    // =========================================================
    // Render Position
    //
    // ★NO LERP
    // =========================================================

    private void RenderPosition()
    {
        Vector3 target =
            _samplePosition;


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

    private void RenderScale()
    {
        Vector3 target =
            _sampleScale;


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


        _neutralFaceRotation =
            Quaternion.identity;


        _lastTimestamp =
            -1;


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


        ResetMotionAccent();
        ResetReactionState();
    }


    private void AddCalibrationSample(
        Vector2 center,
        float eyeSpan,
        Quaternion rotation)
    {
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
        _calibrated =
            true;


        _sampleRotation =
            _baseRotation;


        _samplePosition =
            _basePosition;


        _sampleScale =
            _baseScale;


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