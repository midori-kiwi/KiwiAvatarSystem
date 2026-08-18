using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;


[DefaultExecutionOrder(900)]
public class KiwiExpressionReaction : MonoBehaviour
{
    [Header("Landmarker Direct Tracking")]
    [Tooltip("ON: blendshape intensities are applied from the newest Landmarker sample without attack/release smoothing.")]
    public bool strictLandmarkerTracking = true;


    // =========================================================
    // References
    // =========================================================

    [Header("MediaPipe")]
    public FaceLandmarkerRunner runner;


    [Header("Face Parts")]
    public SurfaceFittedRawImage leftEyeImage;
    public SurfaceFittedRawImage rightEyeImage;
    public SurfaceFittedRawImage mouthImage;


    // =========================================================
    // Blink
    // =========================================================

    [Header("Blink")]

    [Range(0f, 1f)]
    public float blinkStart = 0.28f;

    [Range(0f, 1f)]
    public float blinkFull = 0.72f;


    // =========================================================
    // Eye Wide
    // =========================================================

    [Header("Eye Wide")]

    [Range(0f, 1f)]
    public float eyeWideStart = 0.12f;

    [Range(0f, 1f)]
    public float eyeWideFull = 0.52f;


    // =========================================================
    // Mouth Open
    // =========================================================

    [Header("Mouth Open")]

    [Range(0f, 1f)]
    public float mouthOpenStart = 0.07f;

    [Range(0f, 1f)]
    public float mouthOpenFull = 0.62f;


    // =========================================================
    // Smile
    // =========================================================

    [Header("Smile")]

    [Range(0f, 1f)]
    public float smileStart = 0.16f;

    [Range(0f, 1f)]
    public float smileFull = 0.62f;


    // =========================================================
    // Brow
    // =========================================================

    [Header("Brow Up")]

    [Range(0f, 1f)]
    public float browUpStart = 0.12f;

    [Range(0f, 1f)]
    public float browUpFull = 0.55f;


    // =========================================================
    // Pout
    // =========================================================

    [Header("Pout")]

    [Range(0f, 1f)]
    public float poutStart = 0.12f;

    [Range(0f, 1f)]
    public float poutFull = 0.55f;


    // =========================================================
    // Grumpy
    // =========================================================

    [Header("Grumpy")]

    [Range(0f, 1f)]
    public float grumpyStart = 0.15f;

    [Range(0f, 1f)]
    public float grumpyFull = 0.58f;


    // =========================================================
    // Smoothing
    // =========================================================

    [Header("Expression Smoothing")]

    [Range(1f, 100f)]
    public float fastAttack = 30f;

    [Range(1f, 100f)]
    public float fastRelease = 18f;

    [Range(1f, 50f)]
    public float emotionAttack = 13f;

    [Range(1f, 50f)]
    public float emotionRelease = 7f;

    [Range(0.4f, 2f)]
    public float responseCurve = 0.80f;


    // =========================================================
    // Talk Detection
    // =========================================================

    [Header("Talk Detection")]

    [Range(0.05f, 5f)]
    public float talkMotionStart = 0.25f;

    [Range(0.2f, 10f)]
    public float talkMotionFull = 3.0f;


    // =========================================================
    // Eye Visual
    // =========================================================

    [Header("Eye Visual")]

    public bool enableEyeVisualZoom = true;

    [Range(1f, 3f)]
    public float eyeMaxZoomX = 1.06f;

    [Range(1f, 3f)]
    public float eyeMaxZoomY = 1.16f;


    // =========================================================
    // Mouth Visual
    //
    // ★今回変更
    //
    // 口を最大まで開いても
    // X = 1.08
    // Y = 1.30
    //
    // までに制限。
    // =========================================================

    [Header("Mouth Visual - Reduced")]

    public bool enableMouthVisualZoom = false;


    [Tooltip("口を最大まで開いた時の横サイズ")]
    [Range(0.1f, 2f)]
    public float mouthOpenMaxZoomX = 0.70f;


    [Tooltip("口を最大まで開いた時の縦サイズ")]
    [Range(0.1f, 2f)]
    public float mouthOpenMaxZoomY = 0.70f;


    [Tooltip("すぼめ口の最大横サイズ")]
    [Range(0.1f, 2f)]
    public float mouthPoutMaxZoomX = 1.0f;


    [Tooltip("すぼめ口の最大縦サイズ")]
    [Range(0.1f, 2f)]
    public float mouthPoutMaxZoomY = 1.0f;


    // =========================================================
    // Tracking Lost
    // =========================================================

    [Header("Tracking Lost")]

    [Range(0.05f, 1f)]
    public float trackingLostTime = 0.20f;


    // =========================================================
    // Public Values
    // =========================================================

    public float BlinkIntensity => _blink;

    public float EyeWideIntensity => _eyeWide;

    public float MouthOpenIntensity => _mouthOpen;

    public float SmileIntensity => _smile;

    public float SurpriseIntensity => _surprise;

    public float TalkPulseIntensity => _talkPulse;

    public float PoutIntensity => _pout;

    public float GrumpyIntensity => _grumpy;


    // =========================================================
    // Targets
    // =========================================================

    private float _blinkTarget;
    private float _eyeWideTarget;
    private float _mouthOpenTarget;

    private float _smileTarget;
    private float _surpriseTarget;

    private float _talkPulseTarget;

    private float _poutTarget;
    private float _grumpyTarget;


    // =========================================================
    // Current
    // =========================================================

    private float _blink;
    private float _eyeWide;
    private float _mouthOpen;

    private float _smile;
    private float _surprise;

    private float _talkPulse;

    private float _pout;
    private float _grumpy;


    // =========================================================
    // Timing
    // =========================================================

    private long _lastTimestamp = -1;

    private float _lastSeenTime = -100f;

    private float _lastRawJawOpen = 0f;

    private bool _hasPreviousJaw = false;


    // =========================================================
    // LateUpdate
    // =========================================================

    private void LateUpdate()
    {
        if (runner == null)
        {
            return;
        }


        bool hasExpression =
            runner.TryGetLatestExpressionData(
                out FaceExpressionData data,
                out long timestamp
            );


        if (
            hasExpression &&
            timestamp != _lastTimestamp
        )
        {
            float sampleDt =
                1f / 30f;


            if (
                _lastTimestamp >= 0 &&
                timestamp > _lastTimestamp
            )
            {
                sampleDt =
                    (
                        timestamp -
                        _lastTimestamp
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


            ProcessExpression(
                data,
                sampleDt
            );


            _lastTimestamp =
                timestamp;


            _lastSeenTime =
                Time.unscaledTime;
        }


        bool lost =
            Time.unscaledTime -
            _lastSeenTime
            >
            trackingLostTime;


        if (lost)
        {
            ClearTargets();

            _hasPreviousJaw =
                false;
        }


        float dt =
            Mathf.Clamp(
                Time.unscaledDeltaTime,
                1f / 500f,
                0.05f
            );


        // =====================================================
        // Fast
        // =========================================================

        if (strictLandmarkerTracking)
        {
            _blink = _blinkTarget;
            _eyeWide = _eyeWideTarget;
            _mouthOpen = _mouthOpenTarget;
            _talkPulse = _talkPulseTarget;
            _smile = _smileTarget;
            _surprise = _surpriseTarget;
            _pout = _poutTarget;
            _grumpy = _grumpyTarget;
        }
        else
        {
        _blink =
            SmoothAttackRelease(
                _blink,
                _blinkTarget,
                fastAttack,
                fastRelease,
                dt
            );


        _eyeWide =
            SmoothAttackRelease(
                _eyeWide,
                _eyeWideTarget,
                fastAttack,
                fastRelease,
                dt
            );


        _mouthOpen =
            SmoothAttackRelease(
                _mouthOpen,
                _mouthOpenTarget,
                fastAttack,
                fastRelease,
                dt
            );


        _talkPulse =
            SmoothAttackRelease(
                _talkPulse,
                _talkPulseTarget,
                fastAttack,
                fastRelease,
                dt
            );


        // =====================================================
        // Emotion
        // =========================================================

        _smile =
            SmoothAttackRelease(
                _smile,
                _smileTarget,
                emotionAttack,
                emotionRelease,
                dt
            );


        _surprise =
            SmoothAttackRelease(
                _surprise,
                _surpriseTarget,
                emotionAttack,
                emotionRelease,
                dt
            );


        _pout =
            SmoothAttackRelease(
                _pout,
                _poutTarget,
                emotionAttack,
                emotionRelease,
                dt
            );


        _grumpy =
            SmoothAttackRelease(
                _grumpy,
                _grumpyTarget,
                emotionAttack,
                emotionRelease,
                dt
            );


        }


        ApplyVisualReaction();
    }


    // =========================================================
    // Process Expression
    // =========================================================

    private void ProcessExpression(
        FaceExpressionData data,
        float sampleDt)
    {
        float blinkRaw =
            Mathf.Max(
                data.eyeBlinkLeft,
                data.eyeBlinkRight
            );


        float eyeWideRaw =
            (
                data.eyeWideLeft +
                data.eyeWideRight
            )
            *
            0.5f;


        float smileRaw =
            (
                data.mouthSmileLeft +
                data.mouthSmileRight
            )
            *
            0.5f;


        float cheekSmile =
            (
                data.cheekSquintLeft +
                data.cheekSquintRight
            )
            *
            0.5f;


        smileRaw =
            Mathf.Clamp01(
                smileRaw * 0.80f +
                cheekSmile * 0.20f
            );


        float poutRaw =
            Mathf.Max(
                data.mouthPucker,
                data.mouthFunnel
            );


        float frownRaw =
            (
                data.mouthFrownLeft +
                data.mouthFrownRight
            )
            *
            0.5f;


        float browDownRaw =
            (
                data.browDownLeft +
                data.browDownRight
            )
            *
            0.5f;


        float grumpyRaw =
            frownRaw * 0.55f +
            browDownRaw * 0.45f;


        // =====================================================
        // Remap
        // =========================================================

        _blinkTarget =
            RemapExpression(
                blinkRaw,
                blinkStart,
                blinkFull
            );


        _eyeWideTarget =
            RemapExpression(
                eyeWideRaw,
                eyeWideStart,
                eyeWideFull
            );


        _mouthOpenTarget =
            RemapExpression(
                data.jawOpen,
                mouthOpenStart,
                mouthOpenFull
            );


        _smileTarget =
            RemapExpression(
                smileRaw,
                smileStart,
                smileFull
            );


        float browUp =
            RemapExpression(
                data.browInnerUp,
                browUpStart,
                browUpFull
            );


        _poutTarget =
            RemapExpression(
                poutRaw,
                poutStart,
                poutFull
            );


        _grumpyTarget =
            RemapExpression(
                grumpyRaw,
                grumpyStart,
                grumpyFull
            );


        // =====================================================
        // Surprise
        // =========================================================

        _surpriseTarget =
            Mathf.Clamp01(
                _eyeWideTarget * 0.45f +
                browUp * 0.35f +
                _mouthOpenTarget * 0.20f
            );


        // =====================================================
        // Talk Pulse
        // =========================================================

        if (!_hasPreviousJaw)
        {
            _lastRawJawOpen =
                data.jawOpen;


            _talkPulseTarget =
                0f;


            _hasPreviousJaw =
                true;
        }
        else
        {
            float jawVelocity =
                Mathf.Abs(
                    data.jawOpen -
                    _lastRawJawOpen
                )
                /
                Mathf.Max(
                    sampleDt,
                    0.0001f
                );


            float talkFactor =
                Mathf.InverseLerp(
                    talkMotionStart,
                    talkMotionFull,
                    jawVelocity
                );


            _talkPulseTarget =
                Mathf.SmoothStep(
                    0f,
                    1f,
                    talkFactor
                );


            _lastRawJawOpen =
                data.jawOpen;
        }
    }


    // =========================================================
    // Visual
    // =========================================================

    private void ApplyVisualReaction()
    {
        // =====================================================
        // Eyes
        // =========================================================

        if (
            leftEyeImage != null &&
            rightEyeImage != null
        )
        {
            if (enableEyeVisualZoom)
            {
                float zoomX =
                    Mathf.Lerp(
                        1f,
                        eyeMaxZoomX,
                        _eyeWide
                    );


                float zoomY =
                    Mathf.Lerp(
                        1f,
                        eyeMaxZoomY,
                        _eyeWide
                    );


                leftEyeImage.SetVisualZoom(
                    zoomX,
                    zoomY
                );


                rightEyeImage.SetVisualZoom(
                    zoomX,
                    zoomY
                );
            }
            else
            {
                leftEyeImage.ResetVisualZoom();

                rightEyeImage.ResetVisualZoom();
            }
        }


        // =====================================================
        // Mouth
        //
        // ★今回の修正
        //
        // global 5倍を掛けない。
        //
        // 口を開いても最大サイズを直接指定。
        // =========================================================

        if (mouthImage != null)
        {
            if (enableMouthVisualZoom)
            {
                float openZoomX =
                    Mathf.Lerp(
                        1f,
                        mouthOpenMaxZoomX,
                        _mouthOpen
                    );


                float openZoomY =
                    Mathf.Lerp(
                        1f,
                        mouthOpenMaxZoomY,
                        _mouthOpen
                    );


                float poutZoomX =
                    Mathf.Lerp(
                        1f,
                        mouthPoutMaxZoomX,
                        _pout
                    );


                float poutZoomY =
                    Mathf.Lerp(
                        1f,
                        mouthPoutMaxZoomY,
                        _pout
                    );


                // =================================================
                // 大きい方を採用するだけ。
                //
                // OpenとPoutが重なって
                // 倍率が掛け算されるのを防ぐ。
                // =================================================

                float finalZoomX =
                    Mathf.Max(
                        openZoomX,
                        poutZoomX
                    );


                float finalZoomY =
                    Mathf.Max(
                        openZoomY,
                        poutZoomY
                    );


                mouthImage.SetVisualZoom(
                    finalZoomX,
                    finalZoomY
                );
            }
            else
            {
                mouthImage.ResetVisualZoom();
            }
        }
    }


    // =========================================================
    // Remap
    // =========================================================

    private float RemapExpression(
        float value,
        float start,
        float full)
    {
        float t =
            Mathf.InverseLerp(
                start,
                full,
                value
            );


        t =
            Mathf.SmoothStep(
                0f,
                1f,
                t
            );


        return Mathf.Pow(
            t,
            responseCurve
        );
    }


    // =========================================================
    // Attack / Release
    // =========================================================

    private float SmoothAttackRelease(
        float current,
        float target,
        float attack,
        float release,
        float dt)
    {
        float response =
            target > current
            ?
            attack
            :
            release;


        float t =
            1f -
            Mathf.Exp(
                -response *
                dt
            );


        return Mathf.Lerp(
            current,
            target,
            t
        );
    }


    // =========================================================
    // Clear
    // =========================================================

    private void ClearTargets()
    {
        _blinkTarget = 0f;
        _eyeWideTarget = 0f;
        _mouthOpenTarget = 0f;

        _smileTarget = 0f;
        _surpriseTarget = 0f;

        _talkPulseTarget = 0f;

        _poutTarget = 0f;
        _grumpyTarget = 0f;
    }


    // =========================================================
    // Reset
    // =========================================================

    [ContextMenu("Reset Expression")]
    public void ResetExpression()
    {
        ClearTargets();


        _blink = 0f;
        _eyeWide = 0f;
        _mouthOpen = 0f;

        _smile = 0f;
        _surprise = 0f;

        _talkPulse = 0f;

        _pout = 0f;
        _grumpy = 0f;


        _lastTimestamp =
            -1;


        _lastSeenTime =
            -100f;


        _lastRawJawOpen =
            0f;


        _hasPreviousJaw =
            false;


        leftEyeImage?.ResetVisualZoom();

        rightEyeImage?.ResetVisualZoom();

        mouthImage?.ResetVisualZoom();
    }
}