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
    public MouthDisplaySizeLock mouthDisplaySizeLock;
    public FacePartShapeMask leftEyeShapeMask;
    public FacePartShapeMask rightEyeShapeMask;
    public FacePartShapeMask mouthShapeMask;


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
    public float mouthOpenStart = 0.06f;

    [Range(0f, 1f)]
    public float mouthOpenFull = 0.50f;


    // =========================================================
    // Smile
    // =========================================================

    [Header("Smile")]

    [Range(0f, 1f)]
    public float smileStart = 0.10f;

    [Range(0f, 1f)]
    public float smileFull = 0.50f;


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

    [Tooltip("Enlarges the rendered eye patches toward the supplied character reference without changing LandMarker crop coordinates.")]
    public bool enableEyeDisplayScale = true;

    [Range(0.75f, 2.00f)]
    public float eyeBaseDisplayScaleX = 1.18f;

    [Range(0.75f, 2.50f)]
    public float eyeBaseDisplayScaleY = 1.25f;

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

    [Header("Mouth Visual - Native GPU")]

    public bool enableMouthVisualZoom = true;


    [Tooltip("Base vertical placement of the mouth within the face canvas. More negative values move it down without changing its tracking crop.")]
    [Range(-650f, -200f)]
    public float mouthLayoutPositionY = -400f;


    [Tooltip("口を最大まで開いた時の横サイズ")]
    [Range(1f, 3f)]
    public float mouthOpenMaxZoomX = 2.00f;


    [Tooltip("口を最大まで開いた時の縦サイズ")]
    [Range(1f, 3f)]
    public float mouthOpenMaxZoomY = 2.00f;


    [Tooltip("すぼめ口の最大横サイズ")]
    [Range(1f, 3f)]
    public float mouthPoutMaxZoomX = 1.25f;


    [Tooltip("すぼめ口の最大縦サイズ")]
    [Range(1f, 3f)]
    public float mouthPoutMaxZoomY = 1.15f;


    [Tooltip("Maximum horizontal mouth size while smiling. Smile width is independent from jaw-open height so overlap protection cannot collapse the whole smile.")]
    [Range(1f, 3f)]
    public float mouthSmileMaxZoomX = 2.60f;


    [Tooltip("Small vertical lift retained for a smile without pushing the mouth into the eyes.")]
    [Range(1f, 2f)]
    public float mouthSmileMaxZoomY = 1.35f;


    [Tooltip("Maximum render-rate response. Large changes approach this response continuously instead of snapping to the target.")]
    [Range(30f, 400f)]
    public float mouthEffectResponse = 72f;


    [Tooltip("Expression error at which the adaptive deformation reaches its maximum response. No error size bypasses interpolation.")]
    [Range(0.05f, 1f)]
    public float mouthEffectDirectThreshold = 0.24f;


    [Tooltip("Sub-pixel expression noise below this amount is held to prevent shimmer.")]
    [Range(0f, 0.05f)]
    public float mouthEffectRestDeadZone = 0.004f;


    [Header("Eye / Mouth Separation")]

    [Tooltip("Limits mouth enlargement before its rendered contour reaches either eye.")]
    public bool preventMouthEyeOverlap = true;


    [Tooltip("Minimum visible separation between the mouth and eyes, in screen pixels.")]
    [Range(4f, 120f)]
    public float mouthEyeSafetyMarginPixels = 14f;


    [Tooltip("How quickly a previously limited mouth may expand again after more space becomes available.")]
    [Range(20f, 240f)]
    public float mouthEyeLimitReleaseResponse = 80f;


    [Tooltip("Limits GPU mouth enlargement only when the transformed lip contour would leave its fitted surface.")]
    public bool preventMouthSurfaceClipping = true;


    [Tooltip("Normalized clearance retained between the transformed lip contour and the fitted surface edge.")]
    [Range(0f, 0.15f)]
    public float mouthSurfaceSafetyMargin = 0.035f;


    [Tooltip("Release response after additional safe surface area becomes available.")]
    [Range(20f, 240f)]
    public float mouthSurfaceLimitReleaseResponse = 100f;


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
    private float _displayMouthOpen;
    private float _displayPout;
    private float _displaySmile;
    private float _collisionLimitedZoomY = 1f;
    private bool _mouthCollisionLimited;
    private Vector2 _surfaceLimitedZoom = Vector2.one;
    private bool _mouthSurfaceLimited;
    private Camera _mouthCollisionCamera;


    public bool IsMouthEyeLimited => _mouthCollisionLimited;

    public float CollisionLimitedMouthZoomY => _collisionLimitedZoomY;

    public bool IsMouthSurfaceLimited => _mouthSurfaceLimited;


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


        ApplyVisualReaction(dt);
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

    private void ApplyVisualReaction(float dt)
    {
        CacheMouthDisplaySizeLock();


        // =====================================================
        // Eyes
        // =========================================================

        if (
            leftEyeImage != null &&
            rightEyeImage != null
        )
        {
            float eyeDisplayScaleX =
                enableEyeDisplayScale
                    ? eyeBaseDisplayScaleX
                    : 1f;


            float eyeDisplayScaleY =
                enableEyeDisplayScale
                    ? eyeBaseDisplayScaleY
                    : 1f;


            leftEyeShapeMask?.SetVisibleScale(
                eyeDisplayScaleX,
                eyeDisplayScaleY
            );


            rightEyeShapeMask?.SetVisibleScale(
                eyeDisplayScaleX,
                eyeDisplayScaleY
            );


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
            RectTransform mouthRectTransform = mouthImage.rectTransform;
            Vector2 mouthPosition = mouthRectTransform.anchoredPosition;
            if (!Mathf.Approximately(mouthPosition.y, mouthLayoutPositionY))
            {
                mouthPosition.y = mouthLayoutPositionY;
                mouthRectTransform.anchoredPosition = mouthPosition;
            }

            if (enableMouthVisualZoom)
            {
                _displayMouthOpen = KiwiNativeFaceEffectMath.AdvanceAmount(
                    _displayMouthOpen,
                    _mouthOpen,
                    dt,
                    mouthEffectResponse,
                    mouthEffectDirectThreshold,
                    mouthEffectRestDeadZone
                );

                _displayPout = KiwiNativeFaceEffectMath.AdvanceAmount(
                    _displayPout,
                    _pout,
                    dt,
                    mouthEffectResponse,
                    mouthEffectDirectThreshold,
                    mouthEffectRestDeadZone
                );

                _displaySmile = KiwiNativeFaceEffectMath.AdvanceAmount(
                    _displaySmile,
                    _smile,
                    dt,
                    mouthEffectResponse,
                    mouthEffectDirectThreshold,
                    mouthEffectRestDeadZone
                );

                // Blend complete 2D mouth shapes. Selecting the horizontal and
                // vertical maxima independently can combine a smile width with a
                // jaw-open height and create a shape that never existed in the
                // tracked face.
                Vector2 coherentZoom =
                    KiwiMouthShapeBlendMath.CalculateCoherentZoom(
                        _displayMouthOpen,
                        _displayPout,
                        _displaySmile,
                        new Vector2(
                            mouthOpenMaxZoomX,
                            mouthOpenMaxZoomY
                        ),
                        new Vector2(
                            mouthPoutMaxZoomX,
                            mouthPoutMaxZoomY
                        ),
                        new Vector2(
                            mouthSmileMaxZoomX,
                            mouthSmileMaxZoomY
                        )
                    );


                float finalZoomX = coherentZoom.x;
                float finalZoomY = coherentZoom.y;


                finalZoomY = LimitMouthZoomAgainstEyes(
                    finalZoomX,
                    finalZoomY,
                    dt
                );


                Vector2 surfaceSafeZoom = LimitMouthZoomToSurface(
                    new Vector2(finalZoomX, finalZoomY),
                    dt
                );
                finalZoomX = surfaceSafeZoom.x;
                finalZoomY = surfaceSafeZoom.y;


                if (mouthDisplaySizeLock != null)
                {
                    mouthImage.ResetVisualZoom();
                    mouthDisplaySizeLock.SetExpressionZoom(
                        finalZoomX,
                        finalZoomY
                    );
                }
                else
                {
                    mouthImage.SetVisualZoom(
                        finalZoomX,
                        finalZoomY
                    );
                }
            }
            else
            {
                _displayMouthOpen = 0f;
                _displayPout = 0f;
                _displaySmile = 0f;
                mouthImage.ResetVisualZoom();
                mouthDisplaySizeLock?.ResetExpressionZoom();
            }
        }
    }


    private void CacheMouthDisplaySizeLock()
    {
        if (mouthDisplaySizeLock == null && mouthImage != null)
        {
            mouthDisplaySizeLock = mouthImage.GetComponent<MouthDisplaySizeLock>();
        }

        if (leftEyeShapeMask == null && leftEyeImage != null)
        {
            leftEyeShapeMask = leftEyeImage.GetComponent<FacePartShapeMask>();
        }

        if (rightEyeShapeMask == null && rightEyeImage != null)
        {
            rightEyeShapeMask = rightEyeImage.GetComponent<FacePartShapeMask>();
        }

        if (mouthShapeMask == null && mouthImage != null)
        {
            mouthShapeMask = mouthImage.GetComponent<FacePartShapeMask>();
        }
    }


    private float LimitMouthZoomAgainstEyes(
        float zoomX,
        float desiredZoomY,
        float dt)
    {
        if (
            !preventMouthEyeOverlap ||
            mouthDisplaySizeLock == null ||
            mouthShapeMask == null
        )
        {
            _mouthCollisionLimited = false;
            _collisionLimitedZoomY = desiredZoomY;
            return desiredZoomY;
        }

        Camera camera = null;
        Canvas canvas = mouthImage != null ? mouthImage.canvas : null;
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
        {
            if (canvas.worldCamera != null)
            {
                _mouthCollisionCamera = canvas.worldCamera;
            }
            else if (_mouthCollisionCamera == null)
            {
                _mouthCollisionCamera = Camera.main;
            }

            camera = _mouthCollisionCamera;
        }

        float eyeBottom = float.PositiveInfinity;
        bool hasEyeBounds = TryAccumulateEyeBottom(
            leftEyeShapeMask,
            camera,
            ref eyeBottom
        );
        hasEyeBounds |= TryAccumulateEyeBottom(
            rightEyeShapeMask,
            camera,
            ref eyeBottom
        );

        if (!hasEyeBounds)
        {
            _mouthCollisionLimited = false;
            _collisionLimitedZoomY = desiredZoomY;
            return desiredZoomY;
        }

        float allowedTop = eyeBottom - Mathf.Max(4f, mouthEyeSafetyMarginPixels);
        Vector2 desiredScale =
            mouthDisplaySizeLock.CalculateSampleScaleForExpression(
                zoomX,
                desiredZoomY
            );
        if (
            mouthShapeMask.TryGetRenderedContourScreenRect(
                camera,
                desiredScale,
                out Rect desiredBounds
            ) &&
            desiredBounds.yMax <= allowedTop
        )
        {
            _mouthCollisionLimited = false;
            _collisionLimitedZoomY = desiredZoomY;
            return desiredZoomY;
        }

        float low = 1f;
        float high = Mathf.Max(1f, desiredZoomY);

        for (int iteration = 0; iteration < 8; iteration++)
        {
            float candidate = (low + high) * 0.5f;
            Vector2 sampleScale =
                mouthDisplaySizeLock.CalculateSampleScaleForExpression(
                    zoomX,
                    candidate
                );

            bool hasMouthBounds = mouthShapeMask.TryGetRenderedContourScreenRect(
                camera,
                sampleScale,
                out Rect mouthBounds
            );

            if (!hasMouthBounds || mouthBounds.yMax <= allowedTop)
            {
                low = candidate;
            }
            else
            {
                high = candidate;
            }
        }

        float safeZoomY = Mathf.Min(desiredZoomY, low);
        bool limited = safeZoomY < desiredZoomY - 0.001f;
        if (!limited)
        {
            _mouthCollisionLimited = false;
            _collisionLimitedZoomY = desiredZoomY;
            return desiredZoomY;
        }

        if (!_mouthCollisionLimited || safeZoomY < _collisionLimitedZoomY)
        {
            _collisionLimitedZoomY = safeZoomY;
        }
        else
        {
            float response = Mathf.Max(20f, mouthEyeLimitReleaseResponse);
            float blend = 1f - Mathf.Exp(-response * Mathf.Max(0f, dt));
            _collisionLimitedZoomY = Mathf.Lerp(
                _collisionLimitedZoomY,
                safeZoomY,
                blend
            );
        }

        _mouthCollisionLimited = true;
        return Mathf.Min(desiredZoomY, _collisionLimitedZoomY);
    }


    private Vector2 LimitMouthZoomToSurface(
        Vector2 desiredZoom,
        float dt)
    {
        if (
            !preventMouthSurfaceClipping ||
            mouthDisplaySizeLock == null ||
            mouthShapeMask == null
        )
        {
            _mouthSurfaceLimited = false;
            _surfaceLimitedZoom = desiredZoom;
            return desiredZoom;
        }


        Vector2 desiredScale =
            mouthDisplaySizeLock.CalculateSampleScaleForExpression(
                desiredZoom.x,
                desiredZoom.y
            );
        bool desiredFits = mouthShapeMask.IsRenderedContourInsideSurface(
            desiredScale,
            mouthSurfaceSafetyMargin
        );


        Vector2 safeZoom = desiredZoom;
        if (!desiredFits)
        {
            float low = 0f;
            float high = 1f;
            for (int iteration = 0; iteration < 10; iteration++)
            {
                float amount = (low + high) * 0.5f;
                Vector2 candidate = Vector2.Lerp(
                    Vector2.one,
                    desiredZoom,
                    amount
                );
                Vector2 candidateScale =
                    mouthDisplaySizeLock.CalculateSampleScaleForExpression(
                        candidate.x,
                        candidate.y
                    );


                if (
                    mouthShapeMask.IsRenderedContourInsideSurface(
                        candidateScale,
                        mouthSurfaceSafetyMargin
                    )
                )
                {
                    low = amount;
                }
                else
                {
                    high = amount;
                }
            }


            safeZoom = Vector2.Lerp(Vector2.one, desiredZoom, low);
        }


        bool limited =
            safeZoom.x < desiredZoom.x - 0.001f ||
            safeZoom.y < desiredZoom.y - 0.001f;
        bool tighterThanRendered =
            safeZoom.x < _surfaceLimitedZoom.x - 0.001f ||
            safeZoom.y < _surfaceLimitedZoom.y - 0.001f;
        if (!_mouthSurfaceLimited || tighterThanRendered)
        {
            _surfaceLimitedZoom = safeZoom;
        }
        else
        {
            float response = Mathf.Max(20f, mouthSurfaceLimitReleaseResponse);
            float blend = 1f - Mathf.Exp(-response * Mathf.Max(0f, dt));
            Vector2 candidate = Vector2.Lerp(
                _surfaceLimitedZoom,
                safeZoom,
                blend
            );
            Vector2 candidateScale =
                mouthDisplaySizeLock.CalculateSampleScaleForExpression(
                    candidate.x,
                    candidate.y
                );


            if (
                mouthShapeMask.IsRenderedContourInsideSurface(
                    candidateScale,
                    mouthSurfaceSafetyMargin
                )
            )
            {
                _surfaceLimitedZoom = candidate;
            }
        }


        _mouthSurfaceLimited = limited ||
            Vector2.SqrMagnitude(_surfaceLimitedZoom - desiredZoom) > 0.000001f;
        return new Vector2(
            Mathf.Min(desiredZoom.x, _surfaceLimitedZoom.x),
            Mathf.Min(desiredZoom.y, _surfaceLimitedZoom.y)
        );
    }


    private static bool TryAccumulateEyeBottom(
        FacePartShapeMask shapeMask,
        Camera camera,
        ref float eyeBottom)
    {
        if (
            shapeMask == null ||
            !shapeMask.TryGetRenderedContourScreenRect(camera, out Rect bounds)
        )
        {
            return false;
        }

        eyeBottom = Mathf.Min(eyeBottom, bounds.yMin);
        return true;
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
        _displayMouthOpen = 0f;
        _displayPout = 0f;
        _displaySmile = 0f;
        _collisionLimitedZoomY = 1f;
        _mouthCollisionLimited = false;


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

        CacheMouthDisplaySizeLock();
        mouthDisplaySizeLock?.ResetExpressionZoom();
    }
}


public static class KiwiMouthShapeBlendMath
{
    public static Vector2 CalculateCoherentZoom(
        float mouthOpen,
        float pout,
        float smile,
        Vector2 openMaximum,
        Vector2 poutMaximum,
        Vector2 smileMaximum)
    {
        mouthOpen = Sanitize01(mouthOpen);
        pout = Sanitize01(pout);
        smile = Sanitize01(smile);

        float total = mouthOpen + pout + smile;
        if (total <= 0.000001f)
        {
            return Vector2.one;
        }

        openMaximum = SanitizeMaximum(openMaximum);
        poutMaximum = SanitizeMaximum(poutMaximum);
        smileMaximum = SanitizeMaximum(smileMaximum);

        Vector2 direction =
            (
                (openMaximum - Vector2.one) * mouthOpen +
                (poutMaximum - Vector2.one) * pout +
                (smileMaximum - Vector2.one) * smile
            ) /
            total;

        // The strongest expression controls magnitude while all active
        // expressions contribute to one coherent width/height direction.
        float magnitude = Mathf.Max(mouthOpen, Mathf.Max(pout, smile));
        Vector2 zoom = Vector2.one + direction * magnitude;
        return new Vector2(
            Mathf.Max(1f, zoom.x),
            Mathf.Max(1f, zoom.y)
        );
    }


    private static float Sanitize01(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return 0f;
        }

        return Mathf.Clamp01(value);
    }


    private static Vector2 SanitizeMaximum(Vector2 value)
    {
        return new Vector2(
            float.IsNaN(value.x) || float.IsInfinity(value.x)
                ? 1f
                : Mathf.Max(1f, value.x),
            float.IsNaN(value.y) || float.IsInfinity(value.y)
                ? 1f
                : Mathf.Max(1f, value.y)
        );
    }
}


public static class KiwiNativeFaceEffectMath
{
    public static float AdvanceAmount(
        float current,
        float target,
        float deltaTime,
        float response,
        float directThreshold,
        float restDeadZone)
    {
        current = Sanitize01(current);
        target = Sanitize01(target);
        float error = target - current;
        float absoluteError = Mathf.Abs(error);

        if (absoluteError <= Mathf.Max(0f, restDeadZone))
        {
            return current;
        }

        float dt = Mathf.Clamp(deltaTime, 0f, 0.10f);
        float threshold = Mathf.Max(
            Mathf.Max(0f, restDeadZone) + 0.0001f,
            directThreshold
        );
        float errorAmount = Mathf.InverseLerp(
            Mathf.Max(0f, restDeadZone),
            threshold,
            absoluteError
        );
        errorAmount = Mathf.SmoothStep(0f, 1f, errorAmount);

        // Small changes use a gentler response; large intentional changes
        // reach the configured maximum response without a one-frame snap.
        float adaptiveMultiplier = Mathf.Lerp(0.55f, 1f, errorAmount);
        float rate = Mathf.Max(0f, response) * adaptiveMultiplier;
        float blend = 1f - Mathf.Exp(-rate * dt);
        return Mathf.Lerp(current, target, blend);
    }


    private static float Sanitize01(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return 0f;
        }

        return Mathf.Clamp01(value);
    }
}
