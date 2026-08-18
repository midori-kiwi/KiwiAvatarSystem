using UnityEngine;
using UnityEngine.UI;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;


[DefaultExecutionOrder(700)]
public class FacePartCropper : MonoBehaviour
{
    [Header("Landmarker Direct Tracking")]
    [Tooltip("ON: each new Landmarker crop is applied immediately with no temporal smoothing, prediction or dead-zone.")]
    public bool strictLandmarkerTracking = true;


    // =========================================================
    // MediaPipe
    // =========================================================

    [Header("MediaPipe")]

    public FaceLandmarkerRunner runner;
    public RawImage sourceImage;


    // =========================================================
    // Output
    // =========================================================

    [Header("Output")]

    public RawImage leftEyeImage;
    public RawImage rightEyeImage;
    public RawImage mouthImage;


    // =========================================================
    // Camera
    // =========================================================

    [Header("Camera")]

    public bool mirrorX = true;

    // 左右の目が逆ならON
    public bool swapEyes = true;


    // =========================================================
    // Render
    // =========================================================

    [Header("Render Interpolation")]

    public bool request120Fps = true;

    [Range(60, 240)]
    public int targetRenderFrameRate = 120;


    // =========================================================
    // Eye Crop
    // =========================================================

    [Header("Eye Fixed Anchor Crop")]

    [Range(1.0f, 3.0f)]
    public float eyeWidthScale = 1.45f;

    [Range(0.2f, 1.5f)]
    public float eyeHeightToWidth = 0.58f;

    [Range(0f, 0.1f)]
    public float eyePaddingX = 0.010f;

    [Range(0f, 0.1f)]
    public float eyePaddingY = 0.008f;


    // =========================================================
    // Mouth Crop
    // =========================================================

    [Header("Mouth Fixed Anchor Crop")]

    [Range(1.0f, 3.0f)]
    public float mouthWidthScale = 1.35f;

    [Range(0.2f, 1.5f)]
    public float mouthHeightToWidth = 0.55f;

    [Range(0f, 0.1f)]
    public float mouthPaddingX = 0.012f;

    [Range(0f, 0.1f)]
    public float mouthPaddingY = 0.012f;


    // =========================================================
    // ★ MediaPipe Sample Stabilizer
    //
    // FaceLandmarkListに近づける上で一番重要
    // =========================================================

    [Header("Sample Stabilizer")]

    [Tooltip("ほぼ静止時のMediaPipeサンプル平滑化")]
    [Range(1f, 100f)]
    public float sampleIdleResponse = 20f;

    [Tooltip("顔が動いている時。大きいほど低遅延")]
    [Range(1f, 200f)]
    public float sampleMovingResponse = 85f;

    [Tooltip("この速度でMoving Response最大")]
    [Range(0.05f, 3f)]
    public float sampleMotionFullSpeed = 0.35f;


    // =========================================================
    // ★ Soft Jitter Suppression
    //
    // DeadZoneと違い、急にカクッと動き出しにくい
    // =========================================================

    [Header("Micro Jitter Suppression")]

    [Tooltip("この距離以下はかなり強く抑える")]
    [Range(0f, 0.003f)]
    public float microJitterStart = 0.00015f;

    [Tooltip("この距離以上なら生の移動をほぼ通す")]
    [Range(0.0002f, 0.01f)]
    public float microJitterFull = 0.0015f;

    [Tooltip("非常に小さい動きも完全停止させず少しだけ通す")]
    [Range(0f, 0.5f)]
    public float microJitterMinimumGain = 0.08f;


    // =========================================================
    // Sample Size Stabilizer
    //
    // 切り抜きサイズは位置より強く平滑化
    // =========================================================

    [Header("Sample Size Stabilizer")]

    [Range(1f, 50f)]
    public float eyeSampleSizeResponse = 6f;

    [Range(1f, 50f)]
    public float mouthSampleSizeResponse = 7f;


    // =========================================================
    // Unity Render Interpolation
    //
    // Sample Filter後の位置を描画fpsで滑らかにつなぐ
    // =========================================================

    [Header("Position Interpolation")]

    [Tooltip("目の描画追従速度")]
    [Range(1f, 250f)]
    public float eyeRenderResponse = 110f;

    [Tooltip("口の描画追従速度")]
    [Range(1f, 250f)]
    public float mouthRenderResponse = 120f;


    [Header("Size Interpolation")]

    [Range(1f, 100f)]
    public float eyeRenderSizeResponse = 14f;

    [Range(1f, 100f)]
    public float mouthRenderSizeResponse = 16f;


    // =========================================================
    // Velocity
    // =========================================================

    [Header("Velocity Estimation")]

    [Tooltip("速度推定自体のプルプルを抑える")]
    [Range(1f, 100f)]
    public float velocityResponse = 15f;

    [Range(0.1f, 10f)]
    public float maxCenterVelocity = 2.5f;


    // =========================================================
    // Prediction
    //
    // FaceLandmarkList寄せではOFF推奨
    // =========================================================

    [Header("Prediction")]

    [Tooltip(
        "FaceLandmarkListの安定感に近づけるならOFF推奨。" +
        "遅延が気になる時だけON。"
    )]
    public bool enablePrediction = false;

    [Range(0f, 0.02f)]
    public float predictionLeadSeconds = 0.001f;

    [Range(0.005f, 0.05f)]
    public float maxExtrapolationSeconds = 0.010f;

    [Range(0f, 0.05f)]
    public float maxPredictionDistance = 0.004f;


    // =========================================================
    // Rest Stabilization
    // =========================================================

    [Header("Rest Stabilization")]

    [Tooltip("これ以下なら静止付近")]
    [Range(0f, 0.1f)]
    public float restSpeed = 0.020f;

    [Tooltip("静止時の微小な動きをさらに抑える")]
    [Range(0f, 0.005f)]
    public float restJitterThreshold = 0.00050f;


    // =========================================================
    // Tracking Lost
    // =========================================================

    [Header("Tracking Lost")]

    [Range(0.05f, 2f)]
    public float lostTrackingResetTime = 0.20f;

    public bool hidePartsWhenLost = true;


    // =========================================================
    // MediaPipe Landmark IDs
    // =========================================================

    // 左目の左右端
    private const int LEFT_EYE_A = 362;
    private const int LEFT_EYE_B = 263;

    // 右目の左右端
    private const int RIGHT_EYE_A = 33;
    private const int RIGHT_EYE_B = 133;

    // 口の左右端
    private const int MOUTH_LEFT = 61;
    private const int MOUTH_RIGHT = 291;


    // =========================================================
    // Landmark Buffer
    // =========================================================

    private Vector2[] _landmarkBuffer;

    private long _lastProcessedTimestamp = -1;


    // =========================================================
    // Tracking State
    // =========================================================

    private float _lastTrackingTime = -100f;

    private bool _trackingLost = true;


    // =========================================================
    // Part State
    // =========================================================

    private class PartState
    {
        public bool initialized;

        // 最新の安定化済みMediaPipeサンプル
        public UnityEngine.Rect sampleRect;

        // 現在画面に表示しているRect
        public UnityEngine.Rect displayRect;

        // 前回の生Anchor中心
        public Vector2 lastRawCenter;

        // 安定化後の移動速度
        public Vector2 centerVelocity;

        public long sampleTimestamp = -1;

        public float sampleArrivalTime;
    }


    private readonly PartState _leftEyeState =
        new PartState();

    private readonly PartState _rightEyeState =
        new PartState();

    private readonly PartState _mouthState =
        new PartState();


    // =========================================================
    // Start
    // =========================================================

    private void Start()
    {
        if (request120Fps)
        {
            Application.targetFrameRate =
                targetRenderFrameRate;
        }
    }


    // =========================================================
    // Update
    // =========================================================

    private void LateUpdate()
    {
        if (runner == null)
            return;

        if (sourceImage == null)
            return;

        if (sourceImage.texture == null)
            return;

        if (
            leftEyeImage == null ||
            rightEyeImage == null ||
            mouthImage == null
        )
        {
            return;
        }


        // =====================================================
        // 高画質Webカメラ映像
        // =====================================================

        leftEyeImage.texture =
            sourceImage.texture;

        rightEyeImage.texture =
            sourceImage.texture;

        mouthImage.texture =
            sourceImage.texture;


        // =====================================================
        // 最新MediaPipeランドマーク
        // =====================================================

        bool hasFace =
            runner.TryGetLatestLandmarks(
                ref _landmarkBuffer,
                out int landmarkCount,
                out long timestamp
            );


        if (
            hasFace &&
            _landmarkBuffer != null &&
            landmarkCount > 0
        )
        {
            if (
                timestamp !=
                _lastProcessedTimestamp
            )
            {
                if (_trackingLost)
                {
                    ResetStates();

                    SetPartsVisible(true);
                }


                ProcessSample(
                    landmarkCount,
                    timestamp
                );


                _lastProcessedTimestamp =
                    timestamp;


                _lastTrackingTime =
                    Time.unscaledTime;


                _trackingLost =
                    false;
            }
        }
        else
        {
            HandleTrackingLost();
        }


        // =====================================================
        // Unity描画fpsで毎フレーム補間
        // =====================================================

        float dt =
            Mathf.Clamp(
                Time.unscaledDeltaTime,
                1f / 500f,
                0.05f
            );


        RenderPart(
            leftEyeImage,
            _leftEyeState,
            true,
            dt
        );


        RenderPart(
            rightEyeImage,
            _rightEyeState,
            true,
            dt
        );


        RenderPart(
            mouthImage,
            _mouthState,
            false,
            dt
        );
    }


    // =========================================================
    // Process MediaPipe Sample
    // =========================================================

    private void ProcessSample(
        int landmarkCount,
        long timestamp)
    {
        float sourceAspect =
            sourceImage.texture.width /
            (float)Mathf.Max(
                1,
                sourceImage.texture.height
            );


        UnityEngine.Rect leftRect;
        UnityEngine.Rect rightRect;
        UnityEngine.Rect mouthRect;


        bool leftOK =
            BuildEyeRect(
                LEFT_EYE_A,
                LEFT_EYE_B,
                landmarkCount,
                sourceAspect,
                out leftRect
            );


        bool rightOK =
            BuildEyeRect(
                RIGHT_EYE_A,
                RIGHT_EYE_B,
                landmarkCount,
                sourceAspect,
                out rightRect
            );


        bool mouthOK =
            BuildMouthRect(
                landmarkCount,
                sourceAspect,
                out mouthRect
            );


        if (swapEyes)
        {
            if (leftOK)
            {
                UpdateSample(
                    rightEyeImage,
                    _rightEyeState,
                    leftRect,
                    timestamp,
                    true
                );
            }


            if (rightOK)
            {
                UpdateSample(
                    leftEyeImage,
                    _leftEyeState,
                    rightRect,
                    timestamp,
                    true
                );
            }
        }
        else
        {
            if (leftOK)
            {
                UpdateSample(
                    leftEyeImage,
                    _leftEyeState,
                    leftRect,
                    timestamp,
                    true
                );
            }


            if (rightOK)
            {
                UpdateSample(
                    rightEyeImage,
                    _rightEyeState,
                    rightRect,
                    timestamp,
                    true
                );
            }
        }


        if (mouthOK)
        {
            UpdateSample(
                mouthImage,
                _mouthState,
                mouthRect,
                timestamp,
                false
            );
        }
    }


    // =========================================================
    // Eye Rect
    //
    // min/maxは使わない。
    // 固定した目尻・目頭のみを使用。
    // =========================================================

    private bool BuildEyeRect(
        int indexA,
        int indexB,
        int landmarkCount,
        float sourceAspect,
        out UnityEngine.Rect rect)
    {
        rect =
            default;


        if (
            !TryGetLandmark(
                indexA,
                landmarkCount,
                out Vector2 a
            )
        )
        {
            return false;
        }


        if (
            !TryGetLandmark(
                indexB,
                landmarkCount,
                out Vector2 b
            )
        )
        {
            return false;
        }


        Vector2 center =
            (a + b) *
            0.5f;


        float baseWidth =
            GetAnchorDistanceAsWidth(
                a,
                b,
                sourceAspect
            );


        float width =
            baseWidth *
            eyeWidthScale +
            eyePaddingX * 2f;


        float height =
            baseWidth *
            sourceAspect *
            eyeHeightToWidth +
            eyePaddingY * 2f;


        rect =
            MakeUvRect(
                center,
                width,
                height
            );


        return true;
    }


    // =========================================================
    // Mouth Rect
    //
    // 口の上下点をサイズ計算に使わない。
    //
    // これが口を開閉した時の
    // サイズプルプル低減にかなり効く。
    // =========================================================

    private bool BuildMouthRect(
        int landmarkCount,
        float sourceAspect,
        out UnityEngine.Rect rect)
    {
        rect =
            default;


        if (
            !TryGetLandmark(
                MOUTH_LEFT,
                landmarkCount,
                out Vector2 left
            )
        )
        {
            return false;
        }


        if (
            !TryGetLandmark(
                MOUTH_RIGHT,
                landmarkCount,
                out Vector2 right
            )
        )
        {
            return false;
        }


        Vector2 center =
            (left + right) *
            0.5f;


        float baseWidth =
            GetAnchorDistanceAsWidth(
                left,
                right,
                sourceAspect
            );


        float width =
            baseWidth *
            mouthWidthScale +
            mouthPaddingX * 2f;


        float height =
            baseWidth *
            sourceAspect *
            mouthHeightToWidth +
            mouthPaddingY * 2f;


        rect =
            MakeUvRect(
                center,
                width,
                height
            );


        return true;
    }


    // =========================================================
    // Update Sample
    //
    // ★ここが今回の中心
    // =========================================================

    private void UpdateSample(
        RawImage image,
        PartState state,
        UnityEngine.Rect rawRect,
        long timestamp,
        bool isEye)
    {
        if (!state.initialized)
        {
            state.initialized =
                true;


            state.sampleRect =
                rawRect;


            state.displayRect =
                rawRect;


            state.lastRawCenter =
                rawRect.center;


            state.centerVelocity =
                Vector2.zero;


            state.sampleTimestamp =
                timestamp;


            state.sampleArrivalTime =
                Time.unscaledTime;


            image.uvRect =
                rawRect;


            return;
        }


        if (strictLandmarkerTracking)
        {
            state.sampleRect = rawRect;
            state.displayRect = rawRect;
            state.lastRawCenter = rawRect.center;
            state.centerVelocity = Vector2.zero;
            state.sampleTimestamp = timestamp;
            state.sampleArrivalTime = Time.unscaledTime;
            image.uvRect = rawRect;
            return;
        }


        // =====================================================
        // MediaPipe dt
        // =====================================================

        float sampleDt =
            1f / 30f;


        if (
            state.sampleTimestamp >= 0 &&
            timestamp >
            state.sampleTimestamp
        )
        {
            sampleDt =
                (
                    timestamp -
                    state.sampleTimestamp
                )
                /
                1000f;
        }


        sampleDt =
            Mathf.Clamp(
                sampleDt,
                1f / 240f,
                0.10f
            );


        // =====================================================
        // 生の移動速度
        // =====================================================

        Vector2 rawCenter =
            rawRect.center;


        Vector2 rawDelta =
            rawCenter -
            state.lastRawCenter;


        float rawSpeed =
            rawDelta.magnitude /
            Mathf.Max(
                sampleDt,
                0.0001f
            );


        state.lastRawCenter =
            rawCenter;


        // =====================================================
        // ★ Soft Micro Jitter Suppression
        //
        // 小さなブレ
        // → 強く抑える
        //
        // 本当に動いた
        // → ほぼそのまま通す
        // =====================================================

        Vector2 previousCenter =
            state.sampleRect.center;


        Vector2 targetCenter =
            ApplySoftJitterSuppression(
                previousCenter,
                rawCenter
            );


        // =====================================================
        // Adaptive Sample Response
        //
        // 静止
        // → 強い平滑化
        //
        // 移動
        // → 高速追従
        // =====================================================

        float motionFactor =
            Mathf.InverseLerp(
                0.01f,
                sampleMotionFullSpeed,
                rawSpeed
            );


        float sampleResponse =
            Mathf.Lerp(
                sampleIdleResponse,
                sampleMovingResponse,
                motionFactor
            );


        float sampleT =
            1f -
            Mathf.Exp(
                -sampleResponse *
                sampleDt
            );


        Vector2 filteredCenter =
            Vector2.Lerp(
                previousCenter,
                targetCenter,
                sampleT
            );


        // =====================================================
        // Size
        //
        // サイズは位置よりかなり強く平滑化
        // =====================================================

        Vector2 currentSize =
            state.sampleRect.size;


        Vector2 rawSize =
            rawRect.size;


        float sizeResponse =
            isEye
                ? eyeSampleSizeResponse
                : mouthSampleSizeResponse;


        float sizeT =
            1f -
            Mathf.Exp(
                -sizeResponse *
                sampleDt
            );


        Vector2 filteredSize =
            Vector2.Lerp(
                currentSize,
                rawSize,
                sizeT
            );


        // =====================================================
        // Filter後の速度
        // =====================================================

        Vector2 instantaneousVelocity =
            (
                filteredCenter -
                previousCenter
            )
            /
            Mathf.Max(
                sampleDt,
                0.0001f
            );


        if (
            instantaneousVelocity.magnitude >
            maxCenterVelocity
        )
        {
            instantaneousVelocity =
                instantaneousVelocity.normalized *
                maxCenterVelocity;
        }


        float velocityT =
            1f -
            Mathf.Exp(
                -velocityResponse *
                sampleDt
            );


        state.centerVelocity =
            Vector2.Lerp(
                state.centerVelocity,
                instantaneousVelocity,
                velocityT
            );


        // =====================================================
        // Save
        // =====================================================

        state.sampleRect =
            MakeRectFromCenter(
                filteredCenter,
                filteredSize
            );


        state.sampleTimestamp =
            timestamp;


        state.sampleArrivalTime =
            Time.unscaledTime;
    }


    // =========================================================
    // Soft Jitter Suppression
    // =========================================================

    private Vector2 ApplySoftJitterSuppression(
        Vector2 previous,
        Vector2 target)
    {
        Vector2 delta =
            target -
            previous;


        float distance =
            delta.magnitude;


        if (distance <= 0f)
        {
            return previous;
        }


        float factor =
            Mathf.InverseLerp(
                microJitterStart,
                microJitterFull,
                distance
            );


        factor =
            Mathf.SmoothStep(
                0f,
                1f,
                factor
            );


        float gain =
            Mathf.Lerp(
                microJitterMinimumGain,
                1f,
                factor
            );


        return previous +
            delta *
            gain;
    }


    // =========================================================
    // RenderPart
    //
    // MediaPipe結果が来ていないフレームも
    // Unity側で滑らかにつなぐ
    // =========================================================

    private void RenderPart(
        RawImage image,
        PartState state,
        bool isEye,
        float dt)
    {
        if (
            image == null ||
            !state.initialized
        )
        {
            return;
        }


        if (strictLandmarkerTracking)
        {
            state.displayRect = state.sampleRect;
            image.uvRect = state.sampleRect;
            return;
        }


        Vector2 targetCenter =
            state.sampleRect.center;


        float speed =
            state.centerVelocity.magnitude;


        // =====================================================
        // Prediction
        //
        // 初期値はOFF。
        // FaceLandmarkList寄せならOFFが安定。
        // =====================================================

        if (
            enablePrediction &&
            speed > restSpeed
        )
        {
            float elapsed =
                Mathf.Max(
                    0f,
                    Time.unscaledTime -
                    state.sampleArrivalTime
                );


            float predictionTime =
                Mathf.Min(
                    elapsed +
                    predictionLeadSeconds,
                    maxExtrapolationSeconds
                );


            Vector2 prediction =
                state.centerVelocity *
                predictionTime;


            if (
                prediction.magnitude >
                maxPredictionDistance
            )
            {
                prediction =
                    prediction.normalized *
                    maxPredictionDistance;
            }


            targetCenter +=
                prediction;
        }


        // =====================================================
        // Rest Stabilization
        // =====================================================

        Vector2 displayCenter =
            state.displayRect.center;


        if (speed < restSpeed)
        {
            float distance =
                Vector2.Distance(
                    displayCenter,
                    targetCenter
                );


            if (
                distance <
                restJitterThreshold
            )
            {
                targetCenter =
                    displayCenter;
            }
        }


        // =====================================================
        // Position interpolation
        // =====================================================

        float renderResponse =
            isEye
                ? eyeRenderResponse
                : mouthRenderResponse;


        float positionT =
            1f -
            Mathf.Exp(
                -renderResponse *
                dt
            );


        Vector2 newCenter =
            Vector2.Lerp(
                displayCenter,
                targetCenter,
                positionT
            );


        // =====================================================
        // Size interpolation
        // =====================================================

        Vector2 currentSize =
            state.displayRect.size;


        Vector2 targetSize =
            state.sampleRect.size;


        float sizeResponse =
            isEye
                ? eyeRenderSizeResponse
                : mouthRenderSizeResponse;


        float sizeT =
            1f -
            Mathf.Exp(
                -sizeResponse *
                dt
            );


        Vector2 newSize =
            Vector2.Lerp(
                currentSize,
                targetSize,
                sizeT
            );


        UnityEngine.Rect output =
            MakeRectFromCenter(
                newCenter,
                newSize
            );


        output =
            ClampUvRect(
                output
            );


        state.displayRect =
            output;


        image.uvRect =
            output;
    }


    // =========================================================
    // Landmark
    // =========================================================

    private bool TryGetLandmark(
        int index,
        int landmarkCount,
        out Vector2 point)
    {
        point =
            Vector2.zero;


        if (_landmarkBuffer == null)
            return false;


        if (
            index < 0 ||
            index >= landmarkCount ||
            index >= _landmarkBuffer.Length
        )
        {
            return false;
        }


        point =
            _landmarkBuffer[index];


        return true;
    }


    // =========================================================
    // Anchor Distance
    // =========================================================

    private float GetAnchorDistanceAsWidth(
        Vector2 a,
        Vector2 b,
        float sourceAspect)
    {
        float dx =
            b.x -
            a.x;


        float dy =
            (
                b.y -
                a.y
            )
            /
            Mathf.Max(
                0.01f,
                sourceAspect
            );


        return Mathf.Sqrt(
            dx * dx +
            dy * dy
        );
    }


    // =========================================================
    // Landmark -> UV Rect
    // =========================================================

    private UnityEngine.Rect MakeUvRect(
        Vector2 landmarkCenter,
        float width,
        float height)
    {
        width =
            Mathf.Clamp(
                width,
                0.001f,
                1f
            );


        height =
            Mathf.Clamp(
                height,
                0.001f,
                1f
            );


        float centerX =
            mirrorX
                ? 1f - landmarkCenter.x
                : landmarkCenter.x;


        float centerY =
            1f -
            landmarkCenter.y;


        UnityEngine.Rect rect =
            new UnityEngine.Rect(
                centerX -
                width * 0.5f,

                centerY -
                height * 0.5f,

                width,

                height
            );


        return ClampUvRect(
            rect
        );
    }


    // =========================================================
    // Rect from Center
    // =========================================================

    private UnityEngine.Rect MakeRectFromCenter(
        Vector2 center,
        Vector2 size)
    {
        return new UnityEngine.Rect(
            center.x -
            size.x * 0.5f,

            center.y -
            size.y * 0.5f,

            size.x,

            size.y
        );
    }


    // =========================================================
    // Clamp
    // =========================================================

    private UnityEngine.Rect ClampUvRect(
        UnityEngine.Rect rect)
    {
        rect.width =
            Mathf.Clamp(
                rect.width,
                0.001f,
                1f
            );


        rect.height =
            Mathf.Clamp(
                rect.height,
                0.001f,
                1f
            );


        rect.x =
            Mathf.Clamp(
                rect.x,
                0f,
                Mathf.Max(
                    0f,
                    1f -
                    rect.width
                )
            );


        rect.y =
            Mathf.Clamp(
                rect.y,
                0f,
                Mathf.Max(
                    0f,
                    1f -
                    rect.height
                )
            );


        return rect;
    }


    // =========================================================
    // Tracking Lost
    // =========================================================

    private void HandleTrackingLost()
    {
        if (_trackingLost)
            return;


        if (
            Time.unscaledTime -
            _lastTrackingTime
            <
            lostTrackingResetTime
        )
        {
            return;
        }


        ResetStates();


        _lastProcessedTimestamp =
            -1;


        if (hidePartsWhenLost)
        {
            SetPartsVisible(
                false
            );
        }


        _trackingLost =
            true;
    }


    // =========================================================
    // Reset States
    // =========================================================

    private void ResetStates()
    {
        ResetState(
            _leftEyeState
        );

        ResetState(
            _rightEyeState
        );

        ResetState(
            _mouthState
        );
    }


    private void ResetState(
        PartState state)
    {
        state.initialized =
            false;


        state.sampleRect =
            default;


        state.displayRect =
            default;


        state.lastRawCenter =
            Vector2.zero;


        state.centerVelocity =
            Vector2.zero;


        state.sampleTimestamp =
            -1;


        state.sampleArrivalTime =
            0f;
    }


    // =========================================================
    // Visibility
    // =========================================================

    private void SetPartsVisible(
        bool visible)
    {
        if (leftEyeImage != null)
        {
            leftEyeImage.enabled =
                visible;
        }


        if (rightEyeImage != null)
        {
            rightEyeImage.enabled =
                visible;
        }


        if (mouthImage != null)
        {
            mouthImage.enabled =
                visible;
        }
    }


    // =========================================================
    // Manual Reset
    // =========================================================

    public void ResetTracking()
    {
        ResetStates();


        _lastProcessedTimestamp =
            -1;


        _trackingLost =
            true;
    }
}