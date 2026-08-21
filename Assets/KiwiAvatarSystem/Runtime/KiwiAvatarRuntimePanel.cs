using System.Collections.Generic;
using System.IO;
using UnityEngine;

[DefaultExecutionOrder(20000)]
[DisallowMultipleComponent]
public sealed class KiwiAvatarRuntimePanel : MonoBehaviour
{
    public KiwiAvatarRuntimeManager manager;
    public bool visible = true;
    public KeyCode toggleKey = KeyCode.F8;

    private static readonly KeyCode[] QuickModelKeys =
    {
        KeyCode.Alpha2,
        KeyCode.Alpha3,
        KeyCode.Alpha4,
        KeyCode.Alpha5,
        KeyCode.Alpha6,
        KeyCode.Alpha7,
        KeyCode.Alpha8,
        KeyCode.Alpha9,
    };

    private const string TrackingSavedKey = "KiwiAvatarSystem.TrackingSettings.v12.Saved";
    private const string WindowTitle =
        "Kiwi Avatar System v" + KiwiAvatarRuntimeManager.PackageVersion;

    private sealed class SliderLabelCache
    {
        public bool initialized;
        public float value;
        public string format;
        public string text;
    }

    private static readonly GUILayoutOption SliderLabelWidth =
        GUILayout.Width(190f);

    private Vector2 _modelScroll;
    private Vector2 _panelScroll;
    private Rect _window = new Rect(16f, 16f, 470f, 820f);
    private bool _trackingSettingsLoaded;
    private bool _showTracking;
    private bool _showMotionExtras;
    private bool _showModelAdjustments;
    private FacePartShapeMask _mouthShapeMask;
    private FacePartCropper _facePartCropper;
    private FacePartShapeMask[] _facePartShapeMasks;
    private FacePartAngleLock[] _facePartAngleLocks;
    private KiwiExpressionReaction _expressionReaction;
    private MouthDisplaySizeLock _mouthDisplaySizeLock;
    private readonly Dictionary<string, SliderLabelCache> _sliderLabels =
        new Dictionary<string, SliderLabelCache>();
    private readonly List<string> _modelLabelPaths = new List<string>();
    private readonly List<string> _modelLabels = new List<string>();

    private string _cachedAvatarName;
    private string _cachedStatus;
    private string _currentAvatarLabel = "Current:";
    private string _statusLabel = "Status:";
    private string _faceFitLabel = string.Empty;
    private string _cachedFaceFitMethod;
    private float _cachedFaceFitConfidence = float.NaN;
    private string _trackingDiagnosticsLabel =
        "Render -- fps  source -- Hz  submit -- Hz  results -- Hz";
    private float _nextDiagnosticsRefreshTime;
    private float _smoothedRenderFrameRate;

    private float _cachedButtonHeight = -1f;
    private float _cachedListHeight = -1f;
    private float _cachedLoadWidth = -1f;
    private GUILayoutOption _buttonHeightOption;
    private GUILayoutOption _importButtonHeightOption;
    private GUILayoutOption _modelListHeightOption;
    private GUILayoutOption _modelLoadWidthOption;

    private bool IsMobileRuntime
    {
        get
        {
#if UNITY_ANDROID || UNITY_IOS
            return !Application.isEditor;
#else
            return false;
#endif
        }
    }

    private KiwiFaceMotion FaceMotion => manager != null ? manager.faceMotion : null;

    private void Awake()
    {
        if (manager == null)
        {
            manager = GetComponent<KiwiAvatarRuntimeManager>();
        }

        if (IsMobileRuntime)
        {
            visible = false;
        }

        CacheFacePartControls();
        TryLoadTrackingSettings();
    }

    private void Update()
    {
        if (visible && Time.unscaledDeltaTime > 0.000001f)
        {
            float instantaneousRenderRate =
                Mathf.Clamp(1f / Time.unscaledDeltaTime, 0f, 500f);
            _smoothedRenderFrameRate = _smoothedRenderFrameRate > 0f
                ? Mathf.Lerp(_smoothedRenderFrameRate, instantaneousRenderRate, 0.08f)
                : instantaneousRenderRate;
        }

        TryLoadTrackingSettings();
        // KiwiAvatarSystem v4.5.4: compatibility load restored; legacy tracking UI retired
        // Legacy diagnostics are retired with the legacy tracking UI.

        if (!IsMobileRuntime && Input.GetKeyDown(toggleKey))
        {
            visible = !visible;
        }

        if (IsMobileRuntime || manager == null || manager.IsBusy)
        {
            return;
        }

        bool ctrl =
            Input.GetKey(KeyCode.LeftControl) ||
            Input.GetKey(KeyCode.RightControl);

        if (!ctrl)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            manager.SwitchToFallback();
            return;
        }

        for (int i = 0; i < QuickModelKeys.Length; i++)
        {
            if (Input.GetKeyDown(QuickModelKeys[i]) && i < manager.ModelFiles.Count)
            {
                manager.SwitchToModel(manager.ModelFiles[i]);
                return;
            }
        }
    }

    private void OnGUI()
    {
        if (manager == null)
        {
            return;
        }

        GUI.depth = 0;

        if (IsMobileRuntime)
        {
            DrawMobileToggle();
            if (!visible)
            {
                return;
            }

            Rect safe = GetGuiSafeArea();
            float margin = Mathf.Max(12f, safe.width * 0.02f);
            float width = Mathf.Min(940f, Mathf.Max(300f, safe.width - margin * 2f));
            float height = Mathf.Min(980f, Mathf.Max(360f, safe.height - margin * 2f));

            _window = new Rect(
                safe.x + (safe.width - width) * 0.5f,
                safe.y + (safe.height - height) * 0.5f,
                width,
                height
            );
        }
        else if (!visible)
        {
            return;
        }

        int oldLabelSize = GUI.skin.label.fontSize;
        int oldButtonSize = GUI.skin.button.fontSize;
        int oldWindowSize = GUI.skin.window.fontSize;
        int oldTextFieldSize = GUI.skin.textField.fontSize;

        if (IsMobileRuntime)
        {
            int mobileFont = Mathf.Clamp(Mathf.RoundToInt(Screen.width / 70f), 16, 24);
            GUI.skin.label.fontSize = mobileFont;
            GUI.skin.button.fontSize = mobileFont;
            GUI.skin.window.fontSize = mobileFont;
            GUI.skin.textField.fontSize = Mathf.Max(14, mobileFont - 2);
        }

        _window = GUILayout.Window(
            GetInstanceID(),
            _window,
            DrawWindow,
            WindowTitle
        );

        GUI.skin.label.fontSize = oldLabelSize;
        GUI.skin.button.fontSize = oldButtonSize;
        GUI.skin.window.fontSize = oldWindowSize;
        GUI.skin.textField.fontSize = oldTextFieldSize;
    }

    private void DrawMobileToggle()
    {
        Rect safe = GetGuiSafeArea();
        float size = Mathf.Clamp(safe.width * 0.11f, 56f, 92f);
        Rect button = new Rect(
            safe.xMax - size - 10f,
            safe.y + 10f,
            size,
            size
        );

        if (GUI.Button(button, visible ? "X" : "UI"))
        {
            visible = !visible;
        }
    }

    private static Rect GetGuiSafeArea()
    {
        Rect safe = Screen.safeArea;
        float guiY = Screen.height - safe.yMax;
        return new Rect(safe.x, guiY, safe.width, safe.height);
    }

    private void DrawWindow(int id)
    {
        float buttonHeight = IsMobileRuntime
            ? Mathf.Clamp(Screen.height * 0.06f, 52f, 76f)
            : 24f;
        float listHeight = IsMobileRuntime ? 190f : 145f;

        RefreshLayoutOptions(buttonHeight, listHeight);
        RefreshSummaryLabels();
        RefreshModelLabels();

        _panelScroll = GUILayout.BeginScrollView(_panelScroll);

        GUILayout.Label(_currentAvatarLabel);
        GUILayout.Label(_statusLabel);
        if (manager.IsExternalAvatarActive)
        {
            GUILayout.Label(_faceFitLabel);
        }
        if (IsMobileRuntime)
        {
            GUILayout.Label("Runtime VRM limit: " + manager.EffectiveModelSizeLimitMB + " MB");
        }
        GUILayout.Space(6f);

        GUI.enabled = !manager.IsBusy;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Embedded Kiwi", _buttonHeightOption))
        {
            manager.SwitchToFallback();
        }
        if (GUILayout.Button("Rescan Models", _buttonHeightOption))
        {
            manager.ScanModels();
        }
        GUILayout.EndHorizontal();

        if (GUILayout.Button("Import VRM", _importButtonHeightOption))
        {
            manager.ImportVrmFromPicker();
        }

        if (!IsMobileRuntime)
        {
            if (GUILayout.Button("Open Models Folder", _buttonHeightOption))
            {
                manager.OpenModelsFolder();
            }
            GUILayout.Label("VRM 0.x storage:");
            GUILayout.TextField(manager.ModelsDirectory);
        }
        else
        {
            GUILayout.Label("Imported VRM files are copied into app storage.");
        }
        GUI.enabled = true;

        GUILayout.Space(8f);
        GUILayout.Label(IsMobileRuntime
            ? "Models"
            : "Models  (Ctrl+1 = Kiwi, Ctrl+2..9 = external models)");

        _modelScroll = GUILayout.BeginScrollView(_modelScroll, _modelListHeightOption);
        for (int i = 0; i < manager.ModelFiles.Count; i++)
        {
            string path = manager.ModelFiles[i];
            GUILayout.BeginHorizontal();
            GUILayout.Label(_modelLabels[i]);
            GUI.enabled = !manager.IsBusy;
            if (GUILayout.Button("Load", _modelLoadWidthOption, _buttonHeightOption))
            {
                manager.SwitchToModel(path);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();

        // KiwiAvatarSystem v4.5.4: compatibility load restored; legacy tracking UI retired
        // Legacy Tracking / latency / jitter controls intentionally not rendered.

        if (manager.IsExternalAvatarActive)
        {
            _showModelAdjustments = GUILayout.Toggle(_showModelAdjustments, "Model / Face / Spring adjustments");
            if (_showModelAdjustments)
            {
                DrawAdjustmentControls(buttonHeight);
            }
        }

        GUILayout.Space(8f);
        if (IsMobileRuntime)
        {
            if (GUILayout.Button("Close", _buttonHeightOption))
            {
                visible = false;
            }
        }
        else
        {
            GUILayout.Label("F8: show / hide this panel");
            GUI.DragWindow();
        }

        GUILayout.EndScrollView();
    }

    private void DrawTrackingControls(float buttonHeight)
    {
        KiwiFaceMotion motion = FaceMotion;
        if (motion == null)
        {
            return;
        }

        GUILayout.Space(10f);
        _showTracking = GUILayout.Toggle(_showTracking, "Tracking / latency / jitter controls");
        GUILayout.Label(_trackingDiagnosticsLabel);
        if (!_showTracking)
        {
            return;
        }

        motion.enableUltraLowLatencyTracking = GUILayout.Toggle(
            motion.enableUltraLowLatencyTracking,
            "Ultra Low Latency Tracking"
        );
        motion.ultraAdaptiveMicroFilter = GUILayout.Toggle(
            motion.ultraAdaptiveMicroFilter,
            "Micro-jitter filter"
        );
        motion.useBoundedLatestResultCorrection = GUILayout.Toggle(
            motion.useBoundedLatestResultCorrection,
            "Direct high-quality motion / bound low-quality spikes"
        );
        motion.ultraStaticPoseLock = GUILayout.Toggle(
            motion.ultraStaticPoseLock,
            "Zero-jitter rest pose lock"
        );
        motion.enableRenderTimeLatePrediction = GUILayout.Toggle(
            motion.enableRenderTimeLatePrediction,
            "Motion-only render prediction"
        );
        motion.ultraCompensateFullResultAge = GUILayout.Toggle(
            motion.ultraCompensateFullResultAge,
            "Full measured-latency compensation"
        );
        motion.ultraCompensateCameraCaptureAge = GUILayout.Toggle(
            motion.ultraCompensateCameraCaptureAge,
            "Camera exposure midpoint compensation"
        );
        motion.ultraDisplayRateSmoothing = GUILayout.Toggle(
            motion.ultraDisplayRateSmoothing,
            "Display-rate smoothing"
        );
        motion.ultraDirectDisplayDuringMotion = GUILayout.Toggle(
            motion.ultraDirectDisplayDuringMotion,
            "Zero-lag body motion"
        );
        motion.ultraPredictivePositionResampling = GUILayout.Toggle(
            motion.ultraPredictivePositionResampling,
            "Flicker-free predictive movement"
        );
        motion.ultraConsumeLatestSampleBeforeRender = GUILayout.Toggle(
            motion.ultraConsumeLatestSampleBeforeRender,
            "Latest real sample before render"
        );
        motion.ultraDisableSecondaryBodyMotion = GUILayout.Toggle(
            motion.ultraDisableSecondaryBodyMotion,
            "Pure tracking pose (disable body extras)"
        );
        motion.avatarCentricHorizontalMovement = GUILayout.Toggle(
            motion.avatarCentricHorizontalMovement,
            "Avatar-centric X (right = Kiwi-right)"
        );
        if (motion.runner != null)
        {
            motion.runner.processOnlyFreshWebCamFrames = GUILayout.Toggle(
                motion.runner.processOnlyFreshWebCamFrames,
                "Fresh camera frames only"
            );
            motion.runner.latestFrameOnlyLiveStream = GUILayout.Toggle(
                motion.runner.latestFrameOnlyLiveStream,
                "Single in-flight (lower load, more latency)"
            );
            motion.runner.renderDebugLandmarkAnnotations = GUILayout.Toggle(
                motion.runner.renderDebugLandmarkAnnotations,
                "Debug landmark overlay (slower)"
            );
            motion.runner.autoOptimizeCm831 = GUILayout.Toggle(
                motion.runner.autoOptimizeCm831,
                "CM831 high-speed 720p60 profile"
            );

            float cm831InputWidth = motion.runner.cm831TrackingInputWidth;
            DrawSlider("CM831 LandMarker width", ref cm831InputWidth, 480f, 960f, "F0");
            motion.runner.cm831TrackingInputWidth =
                Mathf.RoundToInt(cm831InputWidth / 16f) * 16;

            float trackingInputWidth = motion.runner.trackingInputMaxWidth;
            DrawSlider("LandMarker input width", ref trackingInputWidth, 320f, 960f, "F0");
            motion.runner.trackingInputMaxWidth = Mathf.RoundToInt(trackingInputWidth / 16f) * 16;
        }

        CacheFacePartControls();
        if (_facePartCropper != null)
        {
            bool renderRateFaceParts = GUILayout.Toggle(
                !_facePartCropper.strictLandmarkerTracking,
                "Flicker-free eye/mouth interpolation"
            );
            SetFacePartInterpolation(renderRateFaceParts);

            _facePartCropper.preserveEyeCenterAtTextureEdges = GUILayout.Toggle(
                _facePartCropper.preserveEyeCenterAtTextureEdges,
                "Preserve eye center at camera edges"
            );
            bool freezePartsOnLoss = GUILayout.Toggle(
                !_facePartCropper.hidePartsWhenLost,
                "Freeze parts through tracking dropouts"
            );
            _facePartCropper.hidePartsWhenLost = !freezePartsOnLoss;
            _facePartCropper.rejectIsolatedMouthOutliers = GUILayout.Toggle(
                _facePartCropper.rejectIsolatedMouthOutliers,
                "Reject isolated mouth crop spikes"
            );
            _facePartCropper.enablePrediction = GUILayout.Toggle(
                _facePartCropper.enablePrediction,
                "Compensate eye/mouth tracking age"
            );
            _facePartCropper.compensateMatchedFrameAge = GUILayout.Toggle(
                _facePartCropper.compensateMatchedFrameAge,
                "Use matched camera-frame timing"
            );
            DrawSlider(
                "Part prediction limit",
                ref _facePartCropper.maxExtrapolationSeconds,
                0.005f,
                0.12f,
                "F3"
            );
            DrawSlider(
                "Part prediction distance",
                ref _facePartCropper.maxPredictionDistance,
                0.001f,
                0.02f,
                "F4"
            );
            _facePartCropper.stabilizeCoherentVerticalMotion = GUILayout.Toggle(
                _facePartCropper.stabilizeCoherentVerticalMotion,
                "Stable eye / mouth spacing in vertical moves"
            );
            _facePartCropper.phaseLockVerticalPrediction = GUILayout.Toggle(
                _facePartCropper.phaseLockVerticalPrediction,
                "Phase-lock eye / mouth vertical motion"
            );
            DrawSlider(
                "Shared vertical crop response",
                ref _facePartCropper.coherentVerticalRenderResponse,
                30f,
                250f,
                "F0"
            );
            DrawSlider(
                "Vertical grouping speed",
                ref _facePartCropper.coherentVerticalMotionMinSpeed,
                0.005f,
                0.50f,
                "F3"
            );
            DrawSlider(
                "Vertical grouping tolerance",
                ref _facePartCropper.coherentVerticalDeltaTolerance,
                0.0005f,
                0.02f,
                "F4"
            );
            DrawSlider(
                "Mouth spike tolerance",
                ref _facePartCropper.mouthOutlierAbsoluteTolerance,
                0.01f,
                0.20f,
                "F3"
            );
            DrawSlider("Eye crop response", ref _facePartCropper.eyeRenderResponse, 30f, 250f, "F0");
            DrawSlider("Mouth crop response", ref _facePartCropper.mouthRenderResponse, 30f, 250f, "F0");
            DrawSlider(
                "Part size jitter zone",
                ref _facePartCropper.restSizeJitterThreshold,
                0f,
                0.005f,
                "F5"
            );
        }
        if (_expressionReaction != null)
        {
            _expressionReaction.enableEyeDisplayScale = GUILayout.Toggle(
                _expressionReaction.enableEyeDisplayScale,
                "Reference eye proportions"
            );
            DrawSlider(
                "Eye display width",
                ref _expressionReaction.eyeBaseDisplayScaleX,
                0.75f,
                2f,
                "F2"
            );
            DrawSlider(
                "Eye display height",
                ref _expressionReaction.eyeBaseDisplayScaleY,
                0.75f,
                2.5f,
                "F2"
            );
            _expressionReaction.enableMouthVisualZoom = GUILayout.Toggle(
                _expressionReaction.enableMouthVisualZoom,
                "Native GPU Big Mouth"
            );
            DrawSlider(
                "Mouth vertical placement",
                ref _expressionReaction.mouthLayoutPositionY,
                -650f,
                -200f,
                "F0"
            );
            DrawSlider(
                "Mouth open start",
                ref _expressionReaction.mouthOpenStart,
                0f,
                0.5f,
                "F2"
            );
            DrawSlider(
                "Mouth open full",
                ref _expressionReaction.mouthOpenFull,
                0.1f,
                1f,
                "F2"
            );
            _expressionReaction.mouthOpenFull = Mathf.Max(
                _expressionReaction.mouthOpenStart + 0.01f,
                _expressionReaction.mouthOpenFull
            );
            DrawSlider(
                "Smile start",
                ref _expressionReaction.smileStart,
                0f,
                0.5f,
                "F2"
            );
            DrawSlider(
                "Smile full",
                ref _expressionReaction.smileFull,
                0.1f,
                1f,
                "F2"
            );
            _expressionReaction.smileFull = Mathf.Max(
                _expressionReaction.smileStart + 0.01f,
                _expressionReaction.smileFull
            );
            DrawSlider(
                "Mouth open width",
                ref _expressionReaction.mouthOpenMaxZoomX,
                1f,
                3f,
                "F2"
            );
            DrawSlider(
                "Mouth open height",
                ref _expressionReaction.mouthOpenMaxZoomY,
                1f,
                3f,
                "F2"
            );
            DrawSlider(
                "Smile width",
                ref _expressionReaction.mouthSmileMaxZoomX,
                1f,
                3f,
                "F2"
            );
            DrawSlider(
                "Smile height",
                ref _expressionReaction.mouthSmileMaxZoomY,
                1f,
                2f,
                "F2"
            );
            DrawSlider(
                "Mouth effect response",
                ref _expressionReaction.mouthEffectResponse,
                30f,
                400f,
                "F0"
            );
            DrawSlider(
                "Mouth direct threshold",
                ref _expressionReaction.mouthEffectDirectThreshold,
                0.05f,
                1f,
                "F2"
            );
            DrawSlider(
                "Mouth jitter zone",
                ref _expressionReaction.mouthEffectRestDeadZone,
                0f,
                0.05f,
                "F3"
            );
            _expressionReaction.preventMouthEyeOverlap = GUILayout.Toggle(
                _expressionReaction.preventMouthEyeOverlap,
                "Prevent mouth / eye overlap"
            );
            _expressionReaction.preventMouthSurfaceClipping = GUILayout.Toggle(
                _expressionReaction.preventMouthSurfaceClipping,
                "Keep enlarged mouth inside surface"
            );
            DrawSlider(
                "Mouth surface safety margin",
                ref _expressionReaction.mouthSurfaceSafetyMargin,
                0f,
                0.15f,
                "F3"
            );
            DrawSlider(
                "Mouth surface limit release",
                ref _expressionReaction.mouthSurfaceLimitReleaseResponse,
                20f,
                240f,
                "F0"
            );
            DrawSlider(
                "Eye-mouth safety margin",
                ref _expressionReaction.mouthEyeSafetyMarginPixels,
                4f,
                120f,
                "F0"
            );
            DrawSlider(
                "Overlap limit release",
                ref _expressionReaction.mouthEyeLimitReleaseResponse,
                20f,
                240f,
                "F0"
            );
        }
        if (_mouthDisplaySizeLock != null)
        {
            DrawSlider(
                "Mouth calibrated size",
                ref _mouthDisplaySizeLock.maximumVisibleScale,
                0.25f,
                1f,
                "F2"
            );
        }
        if (_facePartShapeMasks != null && _facePartShapeMasks.Length > 0)
        {
            float contourResponse = _facePartShapeMasks[0].contourRenderResponse;
            float contourJitterZone = _facePartShapeMasks[0].microJitterDeadZone;
            float eyeHideFade = _facePartShapeMasks[0].eyeHideFadeSeconds;
            float eyeShowFade = _facePartShapeMasks[0].eyeShowFadeSeconds;
            bool stabilizeEyeVisibility = _facePartShapeMasks[0].stabilizeEyeVisibility;
            bool lockContourToMovingCrop = _facePartShapeMasks[0].lockContourToMovingCrop;
            float cropLocalSafetyMargin = _facePartShapeMasks[0].cropLocalSafetyMargin;
            float eyeCloseConfirmations = _facePartShapeMasks[0].eyeCloseConfirmationSamples;
            float closedEyeVisibilityFloor = _facePartShapeMasks[0].closedEyeVisibilityFloor;
            DrawSlider("Part contour response", ref contourResponse, 30f, 400f, "F0");
            DrawSlider("Part contour jitter zone", ref contourJitterZone, 0f, 0.003f, "F5");
            lockContourToMovingCrop = GUILayout.Toggle(
                lockContourToMovingCrop,
                "Keep eye/mouth masks inside moving crops"
            );
            DrawSlider(
                "Part mask safety margin",
                ref cropLocalSafetyMargin,
                0f,
                0.15f,
                "F3"
            );
            stabilizeEyeVisibility = GUILayout.Toggle(
                stabilizeEyeVisibility,
                "Flicker-free eye visibility"
            );
            DrawSlider("Eye close confirmations", ref eyeCloseConfirmations, 1f, 4f, "F0");
            DrawSlider("Closed-eye visibility floor", ref closedEyeVisibilityFloor, 0.10f, 1f, "F2");
            DrawSlider("Eye hide fade", ref eyeHideFade, 0.005f, 0.10f, "F3");
            DrawSlider("Eye show fade", ref eyeShowFade, 0.005f, 0.15f, "F3");
            for (int i = 0; i < _facePartShapeMasks.Length; i++)
            {
                if (_facePartShapeMasks[i] == null)
                {
                    continue;
                }
                _facePartShapeMasks[i].contourRenderResponse = contourResponse;
                _facePartShapeMasks[i].microJitterDeadZone = contourJitterZone;
                _facePartShapeMasks[i].lockContourToMovingCrop = lockContourToMovingCrop;
                _facePartShapeMasks[i].cropLocalSafetyMargin = cropLocalSafetyMargin;
                _facePartShapeMasks[i].stabilizeEyeVisibility = stabilizeEyeVisibility;
                _facePartShapeMasks[i].eyeCloseConfirmationSamples = Mathf.RoundToInt(eyeCloseConfirmations);
                _facePartShapeMasks[i].closedEyeVisibilityFloor = closedEyeVisibilityFloor;
                _facePartShapeMasks[i].eyeHideFadeSeconds = eyeHideFade;
                _facePartShapeMasks[i].eyeShowFadeSeconds = eyeShowFade;
            }
        }
        if (_facePartAngleLocks != null && _facePartAngleLocks.Length > 0)
        {
            float angleResponse = _facePartAngleLocks[0].correctionRenderResponse;
            DrawSlider("Part rotation response", ref angleResponse, 30f, 400f, "F0");
            bool lockPivotToRenderedCrop = GUILayout.Toggle(
                _facePartAngleLocks[0].lockPivotToRenderedCrop,
                "Lock tilted part pivot to displayed crop"
            );
            for (int i = 0; i < _facePartAngleLocks.Length; i++)
            {
                if (_facePartAngleLocks[i] != null)
                {
                    _facePartAngleLocks[i].correctionRenderResponse = angleResponse;
                    _facePartAngleLocks[i].lockPivotToRenderedCrop = lockPivotToRenderedCrop;
                }
            }
        }
        if (_mouthShapeMask != null)
        {
            _mouthShapeMask.lockMouthHeight = GUILayout.Toggle(
                _mouthShapeMask.lockMouthHeight,
                "Calibrated mouth height lock"
            );
            _mouthShapeMask.hideMouthOutsideTexture = GUILayout.Toggle(
                _mouthShapeMask.hideMouthOutsideTexture,
                "Hide incomplete mouth at camera edges"
            );
            _mouthShapeMask.protectMouthDuringBlink = GUILayout.Toggle(
                _mouthShapeMask.protectMouthDuringBlink,
                "Keep mouth visible during blinks"
            );
            DrawSlider(
                "Mouth blink protection",
                ref _mouthShapeMask.mouthBlinkProtectionThreshold,
                0.10f,
                0.90f,
                "F2"
            );
            DrawSlider("Mouth edge hide margin", ref _mouthShapeMask.mouthHideEdgeMargin, 0f, 0.05f, "F3");
            DrawSlider("Mouth safe re-entry", ref _mouthShapeMask.mouthShowEdgeMargin, 0f, 0.10f, "F3");
            float mouthEdgeConfirmation = _mouthShapeMask.mouthEdgeHideConfirmationSamples;
            DrawSlider("Mouth edge confirmations", ref mouthEdgeConfirmation, 1f, 6f, "F0");
            _mouthShapeMask.mouthEdgeHideConfirmationSamples = Mathf.RoundToInt(mouthEdgeConfirmation);
            DrawSlider("Mouth edge grace", ref _mouthShapeMask.mouthEdgeHideGraceSeconds, 0f, 0.30f, "F3");
            float mouthShowConfirmation = _mouthShapeMask.mouthEdgeShowConfirmationSamples;
            DrawSlider("Mouth re-entry confirmations", ref mouthShowConfirmation, 1f, 4f, "F0");
            _mouthShapeMask.mouthEdgeShowConfirmationSamples = Mathf.RoundToInt(mouthShowConfirmation);
            _mouthShapeMask.mouthShowEdgeMargin = Mathf.Max(
                _mouthShapeMask.mouthHideEdgeMargin,
                _mouthShapeMask.mouthShowEdgeMargin
            );
            DrawSlider("Mouth hide fade", ref _mouthShapeMask.mouthHideFadeSeconds, 0.005f, 0.20f, "F3");
            DrawSlider("Mouth show fade", ref _mouthShapeMask.mouthShowFadeSeconds, 0.005f, 0.30f, "F3");
        }

        DrawSlider("Face motion", ref motion.faceMotionMultiplier, 0f, 2f);
        DrawSlider("Pitch", ref motion.pitchGain, 0f, 2f);
        DrawSlider("Yaw", ref motion.yawGain, 0f, 2f);
        DrawSlider("Roll", ref motion.rollGain, 0f, 2f);
        DrawSlider("Screen X move", ref motion.screenPositionGainX, 0f, 2f);
        DrawSlider("Screen Y move", ref motion.screenPositionGainY, 0f, 2f);
        DrawSlider("Depth move", ref motion.depthMovementMultiplier, 1f, 8f);
        DrawSlider("Prediction", ref motion.ultraPredictionStrength, 0f, 1f);
        DrawSlider("Velocity estimate base", ref motion.predictionVelocityResponse, 5f, 100f, "F0");
        DrawSlider("Velocity estimate steady", ref motion.predictionVelocityFastResponse, 60f, 400f, "F0");
        DrawSlider("Capture interval fraction", ref motion.ultraCaptureIntervalFraction, 0f, 1.5f, "F2");
        DrawSlider("Capture compensation cap", ref motion.ultraMaxCaptureAgeSeconds, 0f, 0.05f, "F3");
        DrawSlider("Max latency compensation", ref motion.ultraMaxPredictionSeconds, 0.02f, 0.15f, "F3");
        DrawSlider("Display smooth response", ref motion.ultraDisplaySmoothingResponse, 15f, 240f, "F0");
        DrawSlider("Fast catch-up response", ref motion.ultraDisplayFastResponse, 30f, 400f, "F0");
        DrawSlider("Rest lock time", ref motion.ultraStaticLockSeconds, 0.03f, 0.20f, "F3");
        DrawSlider("Direct rotation speed", ref motion.ultraDirectDisplayRotationSpeed, 0f, 80f, "F1");
        DrawSlider("Direct position speed", ref motion.ultraDirectDisplayPositionSpeed, 0f, 0.10f, "F3");
        DrawSlider("Direct depth speed", ref motion.ultraDirectDisplayScaleSpeed, 0f, 0.30f, "F3");
        DrawSlider("Movement correction", ref motion.ultraPositionCorrectionResponse, 20f, 240f, "F0");
        DrawSlider("Stop / reversal recovery", ref motion.ultraPositionRecoveryResponse, 45f, 400f, "F0");
        DrawSlider("Rotation jitter zone", ref motion.ultraRotationDeadZone, 0f, 0.30f, "F3");
        DrawSlider("Position jitter zone", ref motion.ultraPositionDeadZone, 0f, 0.0015f, "F5");
        DrawSlider("Depth jitter zone", ref motion.ultraScaleDeadZone, 0f, 0.004f, "F5");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Ultra Preset", _buttonHeightOption))
        {
            motion.ApplyUltraTrackingPreset();
        }
        if (GUILayout.Button("Direct Raw", _buttonHeightOption))
        {
            motion.ApplyDirectLandmarkerPreset();
        }
        if (GUILayout.Button("Recenter", _buttonHeightOption))
        {
            motion.RecenterTracking();
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save Tracking", _buttonHeightOption))
        {
            SaveTrackingSettings();
        }
        if (GUILayout.Button("Load Tracking", _buttonHeightOption))
        {
            LoadTrackingSettings();
        }
        GUILayout.EndHorizontal();

        _showMotionExtras = GUILayout.Toggle(_showMotionExtras, "Secondary motion / expression body motion");
        if (_showMotionExtras)
        {
            motion.enableMotionAccent = GUILayout.Toggle(motion.enableMotionAccent, "Motion Accent");
            motion.enableSurpriseJump = GUILayout.Toggle(motion.enableSurpriseJump, "Surprise Jump");
            motion.enableSurpriseSquash = GUILayout.Toggle(motion.enableSurpriseSquash, "Surprise Squash");
            motion.enableHappyWiggle = GUILayout.Toggle(motion.enableHappyWiggle, "Happy Wiggle");
            motion.enableBlinkSquash = GUILayout.Toggle(motion.enableBlinkSquash, "Blink Squash");
            motion.enableTalkingMotion = GUILayout.Toggle(motion.enableTalkingMotion, "Talking Motion");
            motion.enablePoutPuff = GUILayout.Toggle(motion.enablePoutPuff, "Pout Puff");
            motion.enableGrumpyShake = GUILayout.Toggle(motion.enableGrumpyShake, "Grumpy Shake");
            motion.enableIdleLife = GUILayout.Toggle(motion.enableIdleLife, "Idle Life");
            DrawSlider("Reaction amount", ref motion.reactionMultiplier, 1f, 8f);
        }
    }

    private void DrawSlider(
        string label,
        ref float value,
        float minimum,
        float maximum,
        string format = "F2")
    {
        if (!_sliderLabels.TryGetValue(label, out SliderLabelCache cache))
        {
            cache = new SliderLabelCache();
            _sliderLabels.Add(label, cache);
        }

        if (
            !cache.initialized ||
            cache.value != value ||
            cache.format != format
        )
        {
            cache.initialized = true;
            cache.value = value;
            cache.format = format;
            cache.text = label + ": " + value.ToString(format);
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label(cache.text, SliderLabelWidth);
        value = GUILayout.HorizontalSlider(value, minimum, maximum);
        GUILayout.EndHorizontal();
    }

    private void DrawAdjustmentControls(float buttonHeight)
    {
        KiwiAvatarProfile profile = manager.ActiveProfile;
        if (profile == null)
        {
            return;
        }

        GUILayout.Space(8f);
        GUILayout.Label("Model adjustment");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("X -", _buttonHeightOption)) manager.NudgeModel(new Vector3(-0.01f, 0f, 0f));
        if (GUILayout.Button("X +", _buttonHeightOption)) manager.NudgeModel(new Vector3(0.01f, 0f, 0f));
        if (GUILayout.Button("Y -", _buttonHeightOption)) manager.NudgeModel(new Vector3(0f, -0.01f, 0f));
        if (GUILayout.Button("Y +", _buttonHeightOption)) manager.NudgeModel(new Vector3(0f, 0.01f, 0f));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Model -5%", _buttonHeightOption)) manager.ScaleModel(0.95f);
        if (GUILayout.Button("Model +5%", _buttonHeightOption)) manager.ScaleModel(1.05f);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Adaptive Head Fit", _buttonHeightOption)) manager.ResetModelAutoFit();
        if (GUILayout.Button("Whole Height Fit", _buttonHeightOption)) manager.SetReferenceHeightFit();
        GUILayout.EndHorizontal();

        GUILayout.Space(6f);
        GUILayout.Label("FaceAnchor / automatic eye-mouth placement");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("X -", _buttonHeightOption)) manager.NudgeFaceAnchor(new Vector3(-0.005f, 0f, 0f));
        if (GUILayout.Button("X +", _buttonHeightOption)) manager.NudgeFaceAnchor(new Vector3(0.005f, 0f, 0f));
        if (GUILayout.Button("Y -", _buttonHeightOption)) manager.NudgeFaceAnchor(new Vector3(0f, -0.005f, 0f));
        if (GUILayout.Button("Y +", _buttonHeightOption)) manager.NudgeFaceAnchor(new Vector3(0f, 0.005f, 0f));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Z -", _buttonHeightOption)) manager.NudgeFaceAnchor(new Vector3(0f, 0f, -0.005f));
        if (GUILayout.Button("Z +", _buttonHeightOption)) manager.NudgeFaceAnchor(new Vector3(0f, 0f, 0.005f));
        if (GUILayout.Button("Face -5%", _buttonHeightOption)) manager.ScaleFaceAnchor(0.95f);
        if (GUILayout.Button("Face +5%", _buttonHeightOption)) manager.ScaleFaceAnchor(1.05f);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Auto Eye/Face Fit", _buttonHeightOption)) manager.ResetActiveFaceAnchorToAuto();
        if (GUILayout.Button("Legacy Face", _buttonHeightOption)) manager.SetLegacyFaceFit();
        if (GUILayout.Button("Save Profile", _buttonHeightOption)) manager.SaveActiveProfile();
        GUILayout.EndHorizontal();

        GUILayout.Space(6f);
        GUILayout.Label("SpringBone / tail / hair / accessories");

        GUILayout.BeginHorizontal();
        string springLabel = profile.springBoneEnabled ? "Spring: ON" : "Spring: OFF";
        if (GUILayout.Button(springLabel, _buttonHeightOption))
        {
            manager.SetActiveSpringBoneEnabled(!profile.springBoneEnabled);
        }
        if (GUILayout.Button("Spring Reset", _buttonHeightOption)) manager.RestoreSpringBoneInitialTransform();
        if (GUILayout.Button("Spring Rebuild", _buttonHeightOption)) manager.ReconstructSpringBone();
        GUILayout.EndHorizontal();
    }

    private void RefreshLayoutOptions(float buttonHeight, float listHeight)
    {
        float loadWidth = IsMobileRuntime ? 100f : 70f;
        if (
            _buttonHeightOption != null &&
            Mathf.Approximately(_cachedButtonHeight, buttonHeight) &&
            Mathf.Approximately(_cachedListHeight, listHeight) &&
            Mathf.Approximately(_cachedLoadWidth, loadWidth)
        )
        {
            return;
        }

        _cachedButtonHeight = buttonHeight;
        _cachedListHeight = listHeight;
        _cachedLoadWidth = loadWidth;
        _buttonHeightOption = GUILayout.Height(buttonHeight);
        _importButtonHeightOption = GUILayout.Height(
            buttonHeight + (IsMobileRuntime ? 6f : 0f)
        );
        _modelListHeightOption = GUILayout.Height(listHeight);
        _modelLoadWidthOption = GUILayout.Width(loadWidth);
    }

    private void RefreshSummaryLabels()
    {
        string avatarName = manager.CurrentAvatarName ?? string.Empty;
        if (_cachedAvatarName != avatarName)
        {
            _cachedAvatarName = avatarName;
            _currentAvatarLabel = "Current: " + avatarName;
        }

        string status = manager.Status ?? string.Empty;
        if (_cachedStatus != status)
        {
            _cachedStatus = status;
            _statusLabel = "Status: " + status;
        }

        if (
            _cachedFaceFitMethod != manager.ActiveFaceFitMethod ||
            !Mathf.Approximately(
                _cachedFaceFitConfidence,
                manager.ActiveFaceFitConfidence
            )
        )
        {
            _cachedFaceFitMethod = manager.ActiveFaceFitMethod;
            _cachedFaceFitConfidence = manager.ActiveFaceFitConfidence;
            _faceFitLabel =
                "Face Fit: " + _cachedFaceFitMethod +
                "  (confidence " + _cachedFaceFitConfidence.ToString("F2") + ")";
        }
    }

    private void RefreshModelLabels()
    {
        bool changed = _modelLabelPaths.Count != manager.ModelFiles.Count;
        if (!changed)
        {
            for (int i = 0; i < _modelLabelPaths.Count; i++)
            {
                if (_modelLabelPaths[i] != manager.ModelFiles[i])
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
        {
            return;
        }

        _modelLabelPaths.Clear();
        _modelLabels.Clear();
        for (int i = 0; i < manager.ModelFiles.Count; i++)
        {
            string path = manager.ModelFiles[i];
            _modelLabelPaths.Add(path);
            _modelLabels.Add(
                (i + 2) + ". " + Path.GetFileNameWithoutExtension(path)
            );
        }
    }

    private void RefreshTrackingDiagnostics()
    {
        if (!visible)
        {
            return;
        }

        KiwiFaceMotion motion = FaceMotion;
        float now = Time.unscaledTime;
        if (motion == null || now < _nextDiagnosticsRefreshTime)
        {
            return;
        }

        _nextDiagnosticsRefreshTime = now + 0.20f;
        _trackingDiagnosticsLabel =
            "Render " + _smoothedRenderFrameRate.ToString("F1") + " fps" +
            "  backend " + motion.PrecisionTrackingBackend +
            "  device " + motion.PrecisionSourceName +
            "  CM831 " + (motion.PrecisionCm831ProfileActive ? "ON" : "OFF") +
            "  camera " + motion.PrecisionSourceWidth + "x" + motion.PrecisionSourceHeight +
            "@" + motion.PrecisionRequestedSourceRateHz.ToString("F0") +
            "  input " + motion.PrecisionInputWidth + "x" + motion.PrecisionInputHeight +
            "  source " + motion.PrecisionSourceRateHz.ToString("F1") + " Hz" +
            "  submit " + motion.PrecisionSubmissionRateHz.ToString("F1") + " Hz" +
            "  Tracking Q " + motion.PrecisionGeometryQuality.ToString("F2") +
            "  results " + motion.PrecisionTrackingRateHz.ToString("F1") + " Hz" +
            "  aux readback " + motion.PrecisionReadbackLatencyMs.ToString("F1") + " ms" +
            "  Inference " + motion.PrecisionInferenceEngineLatencyMs.ToString("F1") + " ms" +
            " p=" + motion.PrecisionInferenceEnginePresence.ToString("F2") +
            "  source->result " + motion.PrecisionInferenceLatencyMs.ToString("F1") + " ms" +
            "  model est " + motion.PrecisionEstimatedModelLatencyMs.ToString("F1") + " ms" +
            "  capture comp " + motion.PrecisionCaptureAgeCompensationMs.ToString("F1") + " ms" +
            "  render age " + motion.PrecisionPredictionAgeMs.ToString("F1") + " ms" +
            "  bounded " + motion.PrecisionBoundedCorrectionChannels +
            "/" + motion.PrecisionBoundedCorrectionCount;
    }

    private void CacheFacePartControls()
    {
        if (_expressionReaction == null)
        {
            _expressionReaction = FindFirstObjectByType<KiwiExpressionReaction>();
        }

        if (_mouthDisplaySizeLock == null)
        {
            _mouthDisplaySizeLock = FindFirstObjectByType<MouthDisplaySizeLock>();
        }

        if (_facePartCropper == null)
        {
            _facePartCropper = FindFirstObjectByType<FacePartCropper>();
        }

        if (_facePartShapeMasks == null || _facePartShapeMasks.Length == 0)
        {
            _facePartShapeMasks = FindObjectsByType<FacePartShapeMask>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None
            );
        }

        if (_facePartAngleLocks == null || _facePartAngleLocks.Length == 0)
        {
            _facePartAngleLocks = FindObjectsByType<FacePartAngleLock>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None
            );
        }

        if (_mouthShapeMask != null || _facePartShapeMasks == null)
        {
            return;
        }

        for (int i = 0; i < _facePartShapeMasks.Length; i++)
        {
            if (
                _facePartShapeMasks[i] != null &&
                _facePartShapeMasks[i].name.IndexOf(
                    "mouth",
                    System.StringComparison.OrdinalIgnoreCase
                ) >= 0
            )
            {
                _mouthShapeMask = _facePartShapeMasks[i];
                break;
            }
        }
    }

    private void SetFacePartInterpolation(bool enabled)
    {
        bool strict = !enabled;
        if (_facePartCropper != null)
        {
            _facePartCropper.strictLandmarkerTracking = strict;
        }
        if (_facePartShapeMasks != null)
        {
            for (int i = 0; i < _facePartShapeMasks.Length; i++)
            {
                if (_facePartShapeMasks[i] != null)
                {
                    _facePartShapeMasks[i].strictLandmarkerTracking = strict;
                }
            }
        }
        if (_facePartAngleLocks != null)
        {
            for (int i = 0; i < _facePartAngleLocks.Length; i++)
            {
                if (_facePartAngleLocks[i] != null)
                {
                    _facePartAngleLocks[i].strictLandmarkerTracking = strict;
                }
            }
        }
    }

    private void TryLoadTrackingSettings()
    {
        if (_trackingSettingsLoaded || FaceMotion == null)
        {
            return;
        }

        _trackingSettingsLoaded = true;
        if (PlayerPrefs.GetInt(TrackingSavedKey, 0) == 1)
        {
            LoadTrackingSettings();
        }
    }

    private void SaveTrackingSettings()
    {
        KiwiFaceMotion m = FaceMotion;
        if (m == null)
        {
            return;
        }

        PlayerPrefs.SetInt(TrackingSavedKey, 1);
        SetBool("Ultra", m.enableUltraLowLatencyTracking);
        SetBool("Micro", m.ultraAdaptiveMicroFilter);
        SetBool("BoundLatest", m.useBoundedLatestResultCorrection);
        SetBool("RestStabilityV2", m.ultraStaticPoseLock);
        PlayerPrefs.SetFloat("KiwiTrack.RestLockSecondsV2", m.ultraStaticLockSeconds);
        SetBool("Predict", m.enableRenderTimeLatePrediction);
        SetBool("FullAge", m.ultraCompensateFullResultAge);
        SetBool("CaptureAge", m.ultraCompensateCameraCaptureAge);
        SetBool("DisplaySmooth", m.ultraDisplayRateSmoothing);
        SetBool("LateActual", m.ultraConsumeLatestSampleBeforeRender);
        SetBool("PurePose", m.ultraDisableSecondaryBodyMotion);
        SetBool("AvatarCentricX", m.avatarCentricHorizontalMovement);
        if (m.runner != null)
        {
            SetBool("FreshWebCamFrames", m.runner.processOnlyFreshWebCamFrames);
            SetBool("LatestFrameOnlyV2", m.runner.latestFrameOnlyLiveStream);
            SetBool("Cm831Profile", m.runner.autoOptimizeCm831);
            PlayerPrefs.SetInt(
                "KiwiTrack.Cm831HighSpeedInputWidth",
                m.runner.cm831TrackingInputWidth
            );
            PlayerPrefs.SetInt("KiwiTrack.InputWidth", m.runner.trackingInputMaxWidth);
        }
        PlayerPrefs.SetFloat("KiwiTrack.Face", m.faceMotionMultiplier);
        PlayerPrefs.SetFloat("KiwiTrack.Pitch", m.pitchGain);
        PlayerPrefs.SetFloat("KiwiTrack.Yaw", m.yawGain);
        PlayerPrefs.SetFloat("KiwiTrack.Roll", m.rollGain);
        PlayerPrefs.SetFloat("KiwiTrack.ScreenX", m.screenPositionGainX);
        PlayerPrefs.SetFloat("KiwiTrack.ScreenY", m.screenPositionGainY);
        PlayerPrefs.SetFloat("KiwiTrack.Depth", m.depthMovementMultiplier);
        PlayerPrefs.SetFloat("KiwiTrack.PredictStrength", m.ultraPredictionStrength);
        PlayerPrefs.SetFloat("KiwiTrack.VelocityResponse", m.predictionVelocityResponse);
        PlayerPrefs.SetFloat("KiwiTrack.VelocityFastResponse", m.predictionVelocityFastResponse);
        PlayerPrefs.SetFloat("KiwiTrack.CaptureIntervalFraction", m.ultraCaptureIntervalFraction);
        PlayerPrefs.SetFloat("KiwiTrack.CaptureAgeCap", m.ultraMaxCaptureAgeSeconds);
        PlayerPrefs.SetFloat("KiwiTrack.MaxCompensation", m.ultraMaxPredictionSeconds);
        PlayerPrefs.SetFloat("KiwiTrack.RestDisplayResponseV2", m.ultraDisplaySmoothingResponse);
        PlayerPrefs.SetFloat("KiwiTrack.DisplayFast", m.ultraDisplayFastResponse);
        SetBool("DirectDisplayMotion", m.ultraDirectDisplayDuringMotion);
        PlayerPrefs.SetFloat("KiwiTrack.RestSafeDirectRotationV2", m.ultraDirectDisplayRotationSpeed);
        PlayerPrefs.SetFloat("KiwiTrack.RestSafeDirectPositionV2", m.ultraDirectDisplayPositionSpeed);
        PlayerPrefs.SetFloat("KiwiTrack.RestSafeDirectScaleV2", m.ultraDirectDisplayScaleSpeed);
        SetBool("PredictivePositionResampling", m.ultraPredictivePositionResampling);
        PlayerPrefs.SetFloat("KiwiTrack.PositionCorrection", m.ultraPositionCorrectionResponse);
        PlayerPrefs.SetFloat("KiwiTrack.PositionRecovery", m.ultraPositionRecoveryResponse);
        PlayerPrefs.SetFloat("KiwiTrack.RestRotationZoneV2", m.ultraRotationDeadZone);
        PlayerPrefs.SetFloat("KiwiTrack.RestPositionZoneV2", m.ultraPositionDeadZone);
        PlayerPrefs.SetFloat("KiwiTrack.RestScaleZoneV2", m.ultraScaleDeadZone);
        if (_facePartCropper != null)
        {
            SetBool("EyeEdgeCenter", _facePartCropper.preserveEyeCenterAtTextureEdges);
            SetBool("FacePartInterpolation", !_facePartCropper.strictLandmarkerTracking);
            SetBool("FreezePartsOnLoss", !_facePartCropper.hidePartsWhenLost);
            SetBool("RejectMouthCropSpikes", _facePartCropper.rejectIsolatedMouthOutliers);
            PlayerPrefs.SetFloat("KiwiTrack.MouthSpikeTolerance", _facePartCropper.mouthOutlierAbsoluteTolerance);
            PlayerPrefs.SetFloat("KiwiTrack.EyeCropResponse", _facePartCropper.eyeRenderResponse);
            PlayerPrefs.SetFloat("KiwiTrack.MouthCropResponse", _facePartCropper.mouthRenderResponse);
            PlayerPrefs.SetFloat("KiwiTrack.PartSizeJitterZoneV2", _facePartCropper.restSizeJitterThreshold);
            SetBool("FacePartPrediction", _facePartCropper.enablePrediction);
            SetBool("FacePartMatchedTiming", _facePartCropper.compensateMatchedFrameAge);
            PlayerPrefs.SetFloat("KiwiTrack.FacePartPredictionLimit", _facePartCropper.maxExtrapolationSeconds);
            PlayerPrefs.SetFloat("KiwiTrack.FacePartPredictionDistanceV1", _facePartCropper.maxPredictionDistance);
            SetBool("CoherentVerticalPartsV1", _facePartCropper.stabilizeCoherentVerticalMotion);
            SetBool("FacePartVerticalPhaseLockV1", _facePartCropper.phaseLockVerticalPrediction);
            PlayerPrefs.SetFloat("KiwiTrack.SharedVerticalCropResponseV1", _facePartCropper.coherentVerticalRenderResponse);
            PlayerPrefs.SetFloat("KiwiTrack.CoherentVerticalSpeedV1", _facePartCropper.coherentVerticalMotionMinSpeed);
            PlayerPrefs.SetFloat("KiwiTrack.CoherentVerticalToleranceV1", _facePartCropper.coherentVerticalDeltaTolerance);
        }
        if (_facePartShapeMasks != null && _facePartShapeMasks.Length > 0)
        {
            PlayerPrefs.SetFloat("KiwiTrack.PartContourResponseV2", _facePartShapeMasks[0].contourRenderResponse);
            PlayerPrefs.SetFloat("KiwiTrack.PartContourJitterZoneV2", _facePartShapeMasks[0].microJitterDeadZone);
            SetBool("CropLockedMask", _facePartShapeMasks[0].lockContourToMovingCrop);
            PlayerPrefs.SetFloat("KiwiTrack.PartMaskSafetyMargin", _facePartShapeMasks[0].cropLocalSafetyMargin);
            SetBool("StableEyeVisibility", _facePartShapeMasks[0].stabilizeEyeVisibility);
            PlayerPrefs.SetInt("KiwiTrack.EyeCloseConfirmations", _facePartShapeMasks[0].eyeCloseConfirmationSamples);
            PlayerPrefs.SetFloat("KiwiTrack.ClosedEyeVisibilityFloor", _facePartShapeMasks[0].closedEyeVisibilityFloor);
            PlayerPrefs.SetFloat("KiwiTrack.EyeHideFade", _facePartShapeMasks[0].eyeHideFadeSeconds);
            PlayerPrefs.SetFloat("KiwiTrack.EyeShowFade", _facePartShapeMasks[0].eyeShowFadeSeconds);
        }
        if (_facePartAngleLocks != null && _facePartAngleLocks.Length > 0)
        {
            PlayerPrefs.SetFloat("KiwiTrack.PartAngleResponse", _facePartAngleLocks[0].correctionRenderResponse);
            SetBool("PartRenderedCropPivotV1", _facePartAngleLocks[0].lockPivotToRenderedCrop);
        }
        if (_mouthShapeMask != null)
        {
            SetBool("MouthHeightLock", _mouthShapeMask.lockMouthHeight);
            SetBool("MouthEdgeHide", _mouthShapeMask.hideMouthOutsideTexture);
            SetBool("MouthBlinkProtection", _mouthShapeMask.protectMouthDuringBlink);
            PlayerPrefs.SetFloat("KiwiTrack.MouthBlinkThreshold", _mouthShapeMask.mouthBlinkProtectionThreshold);
            PlayerPrefs.SetFloat("KiwiTrack.MouthHideMargin", _mouthShapeMask.mouthHideEdgeMargin);
            PlayerPrefs.SetFloat("KiwiTrack.MouthShowMargin", _mouthShapeMask.mouthShowEdgeMargin);
            PlayerPrefs.SetInt("KiwiTrack.MouthEdgeConfirmations", _mouthShapeMask.mouthEdgeHideConfirmationSamples);
            PlayerPrefs.SetFloat("KiwiTrack.MouthEdgeGrace", _mouthShapeMask.mouthEdgeHideGraceSeconds);
            PlayerPrefs.SetInt("KiwiTrack.MouthShowConfirmations", _mouthShapeMask.mouthEdgeShowConfirmationSamples);
            PlayerPrefs.SetFloat("KiwiTrack.MouthHideFade", _mouthShapeMask.mouthHideFadeSeconds);
            PlayerPrefs.SetFloat("KiwiTrack.MouthShowFade", _mouthShapeMask.mouthShowFadeSeconds);
        }
        if (_expressionReaction != null)
        {
            SetBool("ReferenceSizedEyesV4", _expressionReaction.enableEyeDisplayScale);
            PlayerPrefs.SetFloat("KiwiTrack.EyeDisplayScaleXV4", _expressionReaction.eyeBaseDisplayScaleX);
            PlayerPrefs.SetFloat("KiwiTrack.EyeDisplayScaleYV4", _expressionReaction.eyeBaseDisplayScaleY);
            SetBool("NativeGpuBigMouth", _expressionReaction.enableMouthVisualZoom);
            PlayerPrefs.SetFloat("KiwiTrack.MouthLayoutPositionYV1", _expressionReaction.mouthLayoutPositionY);
            PlayerPrefs.SetFloat("KiwiTrack.MouthOpenStartV1", _expressionReaction.mouthOpenStart);
            PlayerPrefs.SetFloat("KiwiTrack.MouthOpenFullV1", _expressionReaction.mouthOpenFull);
            PlayerPrefs.SetFloat("KiwiTrack.SmileStartV1", _expressionReaction.smileStart);
            PlayerPrefs.SetFloat("KiwiTrack.SmileFullV1", _expressionReaction.smileFull);
            PlayerPrefs.SetFloat("KiwiTrack.MouthOpenZoomXV3", _expressionReaction.mouthOpenMaxZoomX);
            PlayerPrefs.SetFloat("KiwiTrack.MouthOpenZoomYV3", _expressionReaction.mouthOpenMaxZoomY);
            PlayerPrefs.SetFloat("KiwiTrack.MouthSmileZoomXV3", _expressionReaction.mouthSmileMaxZoomX);
            PlayerPrefs.SetFloat("KiwiTrack.MouthSmileZoomYV3", _expressionReaction.mouthSmileMaxZoomY);
            PlayerPrefs.SetFloat("KiwiTrack.MouthEffectResponseV3", _expressionReaction.mouthEffectResponse);
            PlayerPrefs.SetFloat("KiwiTrack.MouthDirectThresholdV3", _expressionReaction.mouthEffectDirectThreshold);
            PlayerPrefs.SetFloat("KiwiTrack.MouthEffectDeadZoneV3", _expressionReaction.mouthEffectRestDeadZone);
            SetBool("PreventMouthEyeOverlap", _expressionReaction.preventMouthEyeOverlap);
            PlayerPrefs.SetFloat("KiwiTrack.MouthEyeMarginV3", _expressionReaction.mouthEyeSafetyMarginPixels);
            PlayerPrefs.SetFloat("KiwiTrack.MouthEyeRelease", _expressionReaction.mouthEyeLimitReleaseResponse);
            SetBool("PreventMouthSurfaceClippingV1", _expressionReaction.preventMouthSurfaceClipping);
            PlayerPrefs.SetFloat("KiwiTrack.MouthSurfaceSafetyMarginV1", _expressionReaction.mouthSurfaceSafetyMargin);
            PlayerPrefs.SetFloat("KiwiTrack.MouthSurfaceReleaseV1", _expressionReaction.mouthSurfaceLimitReleaseResponse);
        }
        if (_mouthDisplaySizeLock != null)
        {
            PlayerPrefs.SetFloat("KiwiTrack.MouthCalibratedSizeV3", _mouthDisplaySizeLock.maximumVisibleScale);
        }
        SetBool("MotionAccent", m.enableMotionAccent);
        SetBool("SurpriseJump", m.enableSurpriseJump);
        SetBool("SurpriseSquash", m.enableSurpriseSquash);
        SetBool("Happy", m.enableHappyWiggle);
        SetBool("Blink", m.enableBlinkSquash);
        SetBool("Talk", m.enableTalkingMotion);
        SetBool("Pout", m.enablePoutPuff);
        SetBool("Grumpy", m.enableGrumpyShake);
        SetBool("Idle", m.enableIdleLife);
        PlayerPrefs.SetFloat("KiwiTrack.Reaction", m.reactionMultiplier);
        PlayerPrefs.Save();
    }

    private void LoadTrackingSettings()
    {
        KiwiFaceMotion m = FaceMotion;
        if (m == null || PlayerPrefs.GetInt(TrackingSavedKey, 0) != 1)
        {
            return;
        }

        m.enableUltraLowLatencyTracking = GetBool("Ultra", m.enableUltraLowLatencyTracking);
        m.ultraAdaptiveMicroFilter = GetBool("Micro", m.ultraAdaptiveMicroFilter);
        m.useBoundedLatestResultCorrection = GetBool(
            "BoundLatest",
            m.useBoundedLatestResultCorrection
        );
        m.ultraStaticPoseLock = GetBool("RestStabilityV2", true);
        m.ultraStaticLockSeconds = GetClampedFloat(
            "KiwiTrack.RestLockSecondsV2",
            m.ultraStaticLockSeconds,
            0.03f,
            0.20f
        );
        m.enableRenderTimeLatePrediction = GetBool("Predict", m.enableRenderTimeLatePrediction);
        m.ultraCompensateFullResultAge = GetBool("FullAge", m.ultraCompensateFullResultAge);
        m.ultraCompensateCameraCaptureAge = GetBool(
            "CaptureAge",
            m.ultraCompensateCameraCaptureAge
        );
        m.ultraDisplayRateSmoothing = GetBool("DisplaySmooth", m.ultraDisplayRateSmoothing);
        m.ultraConsumeLatestSampleBeforeRender = GetBool("LateActual", m.ultraConsumeLatestSampleBeforeRender);
        m.ultraDisableSecondaryBodyMotion = GetBool("PurePose", m.ultraDisableSecondaryBodyMotion);
        m.avatarCentricHorizontalMovement = GetBool("AvatarCentricX", m.avatarCentricHorizontalMovement);
        if (m.runner != null)
        {
            m.runner.processOnlyFreshWebCamFrames = GetBool(
                "FreshWebCamFrames",
                m.runner.processOnlyFreshWebCamFrames
            );
            m.runner.latestFrameOnlyLiveStream = GetBool(
                "LatestFrameOnlyV2",
                m.runner.latestFrameOnlyLiveStream
            );
            m.runner.autoOptimizeCm831 = GetBool(
                "Cm831Profile",
                m.runner.autoOptimizeCm831
            );
            m.runner.cm831TrackingInputWidth = Mathf.Clamp(
                PlayerPrefs.GetInt(
                    "KiwiTrack.Cm831HighSpeedInputWidth",
                    m.runner.cm831TrackingInputWidth
                ),
                480,
                960
            );
            m.runner.trackingInputMaxWidth = Mathf.Clamp(
                PlayerPrefs.GetInt("KiwiTrack.InputWidth", m.runner.trackingInputMaxWidth),
                320,
                960
            );
        }
        m.faceMotionMultiplier = GetClampedFloat("KiwiTrack.Face", m.faceMotionMultiplier, 0f, 2f);
        m.pitchGain = GetClampedFloat("KiwiTrack.Pitch", m.pitchGain, 0f, 2f);
        m.yawGain = GetClampedFloat("KiwiTrack.Yaw", m.yawGain, 0f, 2f);
        m.rollGain = GetClampedFloat("KiwiTrack.Roll", m.rollGain, 0f, 2f);
        m.screenPositionGainX = GetClampedFloat("KiwiTrack.ScreenX", m.screenPositionGainX, 0f, 2f);
        m.screenPositionGainY = GetClampedFloat("KiwiTrack.ScreenY", m.screenPositionGainY, 0f, 2f);
        m.depthMovementMultiplier = GetClampedFloat("KiwiTrack.Depth", m.depthMovementMultiplier, 1f, 8f);
        m.ultraPredictionStrength = GetClampedFloat("KiwiTrack.PredictStrength", m.ultraPredictionStrength, 0f, 1f);
        m.predictionVelocityResponse = GetClampedFloat(
            "KiwiTrack.VelocityResponse",
            m.predictionVelocityResponse,
            5f,
            100f
        );
        m.predictionVelocityFastResponse = GetClampedFloat(
            "KiwiTrack.VelocityFastResponse",
            m.predictionVelocityFastResponse,
            60f,
            400f
        );
        m.ultraCaptureIntervalFraction = GetClampedFloat(
            "KiwiTrack.CaptureIntervalFraction",
            m.ultraCaptureIntervalFraction,
            0f,
            1.5f
        );
        m.ultraMaxCaptureAgeSeconds = GetClampedFloat(
            "KiwiTrack.CaptureAgeCap",
            m.ultraMaxCaptureAgeSeconds,
            0f,
            0.05f
        );
        m.ultraMaxPredictionSeconds = GetClampedFloat(
            "KiwiTrack.MaxCompensation",
            m.ultraMaxPredictionSeconds,
            0.02f,
            0.15f
        );
        m.ultraDisplaySmoothingResponse = GetClampedFloat(
            "KiwiTrack.RestDisplayResponseV2",
            m.ultraDisplaySmoothingResponse,
            15f,
            240f
        );
        m.ultraDisplayFastResponse = GetClampedFloat(
            "KiwiTrack.DisplayFast",
            m.ultraDisplayFastResponse,
            30f,
            400f
        );
        m.ultraDirectDisplayDuringMotion = GetBool(
            "DirectDisplayMotion",
            m.ultraDirectDisplayDuringMotion
        );
        m.ultraDirectDisplayRotationSpeed = GetClampedFloat(
            "KiwiTrack.RestSafeDirectRotationV2",
            m.ultraDirectDisplayRotationSpeed,
            0f,
            80f
        );
        m.ultraDirectDisplayPositionSpeed = GetClampedFloat(
            "KiwiTrack.RestSafeDirectPositionV2",
            m.ultraDirectDisplayPositionSpeed,
            0f,
            0.10f
        );
        m.ultraDirectDisplayScaleSpeed = GetClampedFloat(
            "KiwiTrack.RestSafeDirectScaleV2",
            m.ultraDirectDisplayScaleSpeed,
            0f,
            0.30f
        );
        m.ultraPredictivePositionResampling = GetBool(
            "PredictivePositionResampling",
            m.ultraPredictivePositionResampling
        );
        m.ultraPositionCorrectionResponse = GetClampedFloat(
            "KiwiTrack.PositionCorrection",
            m.ultraPositionCorrectionResponse,
            20f,
            240f
        );
        m.ultraPositionRecoveryResponse = GetClampedFloat(
            "KiwiTrack.PositionRecovery",
            m.ultraPositionRecoveryResponse,
            45f,
            400f
        );
        m.ultraRotationDeadZone = GetClampedFloat("KiwiTrack.RestRotationZoneV2", m.ultraRotationDeadZone, 0f, 0.30f);
        m.ultraPositionDeadZone = GetClampedFloat("KiwiTrack.RestPositionZoneV2", m.ultraPositionDeadZone, 0f, 0.0015f);
        m.ultraScaleDeadZone = GetClampedFloat("KiwiTrack.RestScaleZoneV2", m.ultraScaleDeadZone, 0f, 0.004f);
        CacheFacePartControls();
        if (_facePartCropper != null)
        {
            _facePartCropper.preserveEyeCenterAtTextureEdges = GetBool(
                "EyeEdgeCenter",
                _facePartCropper.preserveEyeCenterAtTextureEdges
            );
            SetFacePartInterpolation(GetBool(
                "FacePartInterpolation",
                !_facePartCropper.strictLandmarkerTracking
            ));
            _facePartCropper.hidePartsWhenLost = !GetBool(
                "FreezePartsOnLoss",
                !_facePartCropper.hidePartsWhenLost
            );
            _facePartCropper.rejectIsolatedMouthOutliers = GetBool(
                "RejectMouthCropSpikes",
                _facePartCropper.rejectIsolatedMouthOutliers
            );
            _facePartCropper.mouthOutlierAbsoluteTolerance = GetClampedFloat(
                "KiwiTrack.MouthSpikeTolerance",
                _facePartCropper.mouthOutlierAbsoluteTolerance,
                0.01f,
                0.20f
            );
            _facePartCropper.eyeRenderResponse = GetClampedFloat(
                "KiwiTrack.EyeCropResponse",
                _facePartCropper.eyeRenderResponse,
                30f,
                250f
            );
            _facePartCropper.mouthRenderResponse = GetClampedFloat(
                "KiwiTrack.MouthCropResponse",
                _facePartCropper.mouthRenderResponse,
                30f,
                250f
            );
            _facePartCropper.restSizeJitterThreshold = GetClampedFloat(
                "KiwiTrack.PartSizeJitterZoneV2",
                _facePartCropper.restSizeJitterThreshold,
                0f,
                0.005f
            );
            _facePartCropper.enablePrediction = GetBool(
                "FacePartPrediction",
                _facePartCropper.enablePrediction
            );
            _facePartCropper.compensateMatchedFrameAge = GetBool(
                "FacePartMatchedTiming",
                _facePartCropper.compensateMatchedFrameAge
            );
            _facePartCropper.maxExtrapolationSeconds = GetClampedFloat(
                "KiwiTrack.FacePartPredictionLimit",
                _facePartCropper.maxExtrapolationSeconds,
                0.005f,
                0.12f
            );
            _facePartCropper.maxPredictionDistance = GetClampedFloat(
                "KiwiTrack.FacePartPredictionDistanceV1",
                _facePartCropper.maxPredictionDistance,
                0.001f,
                0.02f
            );
            _facePartCropper.stabilizeCoherentVerticalMotion = GetBool(
                "CoherentVerticalPartsV1",
                _facePartCropper.stabilizeCoherentVerticalMotion
            );
            _facePartCropper.phaseLockVerticalPrediction = GetBool(
                "FacePartVerticalPhaseLockV1",
                _facePartCropper.phaseLockVerticalPrediction
            );
            _facePartCropper.coherentVerticalRenderResponse = GetClampedFloat(
                "KiwiTrack.SharedVerticalCropResponseV1",
                _facePartCropper.coherentVerticalRenderResponse,
                30f,
                250f
            );
            _facePartCropper.coherentVerticalMotionMinSpeed = GetClampedFloat(
                "KiwiTrack.CoherentVerticalSpeedV1",
                _facePartCropper.coherentVerticalMotionMinSpeed,
                0.005f,
                0.50f
            );
            _facePartCropper.coherentVerticalDeltaTolerance = GetClampedFloat(
                "KiwiTrack.CoherentVerticalToleranceV1",
                _facePartCropper.coherentVerticalDeltaTolerance,
                0.0005f,
                0.02f
            );
        }
        if (_facePartShapeMasks != null && _facePartShapeMasks.Length > 0)
        {
            float contourResponse = GetClampedFloat(
                "KiwiTrack.PartContourResponseV2",
                _facePartShapeMasks[0].contourRenderResponse,
                30f,
                400f
            );
            float contourJitterZone = GetClampedFloat(
                "KiwiTrack.PartContourJitterZoneV2",
                _facePartShapeMasks[0].microJitterDeadZone,
                0f,
                0.003f
            );
            bool lockContourToMovingCrop = GetBool(
                "CropLockedMask",
                _facePartShapeMasks[0].lockContourToMovingCrop
            );
            float cropLocalSafetyMargin = GetClampedFloat(
                "KiwiTrack.PartMaskSafetyMargin",
                _facePartShapeMasks[0].cropLocalSafetyMargin,
                0f,
                0.15f
            );
            bool stabilizeEyeVisibility = GetBool(
                "StableEyeVisibility",
                _facePartShapeMasks[0].stabilizeEyeVisibility
            );
            int eyeCloseConfirmations = Mathf.Clamp(
                PlayerPrefs.GetInt(
                    "KiwiTrack.EyeCloseConfirmations",
                    _facePartShapeMasks[0].eyeCloseConfirmationSamples
                ),
                1,
                4
            );
            float closedEyeVisibilityFloor = GetClampedFloat(
                "KiwiTrack.ClosedEyeVisibilityFloor",
                _facePartShapeMasks[0].closedEyeVisibilityFloor,
                0.10f,
                1f
            );
            float eyeHideFade = GetClampedFloat(
                "KiwiTrack.EyeHideFade",
                _facePartShapeMasks[0].eyeHideFadeSeconds,
                0.005f,
                0.10f
            );
            float eyeShowFade = GetClampedFloat(
                "KiwiTrack.EyeShowFade",
                _facePartShapeMasks[0].eyeShowFadeSeconds,
                0.005f,
                0.15f
            );
            for (int i = 0; i < _facePartShapeMasks.Length; i++)
            {
                if (_facePartShapeMasks[i] == null)
                {
                    continue;
                }
                _facePartShapeMasks[i].contourRenderResponse = contourResponse;
                _facePartShapeMasks[i].microJitterDeadZone = contourJitterZone;
                _facePartShapeMasks[i].lockContourToMovingCrop = lockContourToMovingCrop;
                _facePartShapeMasks[i].cropLocalSafetyMargin = cropLocalSafetyMargin;
                _facePartShapeMasks[i].stabilizeEyeVisibility = stabilizeEyeVisibility;
                _facePartShapeMasks[i].eyeCloseConfirmationSamples = eyeCloseConfirmations;
                _facePartShapeMasks[i].closedEyeVisibilityFloor = closedEyeVisibilityFloor;
                _facePartShapeMasks[i].eyeHideFadeSeconds = eyeHideFade;
                _facePartShapeMasks[i].eyeShowFadeSeconds = eyeShowFade;
            }
        }
        if (_facePartAngleLocks != null && _facePartAngleLocks.Length > 0)
        {
            float angleResponse = GetClampedFloat(
                "KiwiTrack.PartAngleResponse",
                _facePartAngleLocks[0].correctionRenderResponse,
                30f,
                400f
            );
            for (int i = 0; i < _facePartAngleLocks.Length; i++)
            {
                if (_facePartAngleLocks[i] != null)
                {
                    _facePartAngleLocks[i].correctionRenderResponse = angleResponse;
                    _facePartAngleLocks[i].lockPivotToRenderedCrop = GetBool(
                        "PartRenderedCropPivotV1",
                        _facePartAngleLocks[i].lockPivotToRenderedCrop
                    );
                }
            }
        }
        if (_mouthShapeMask != null)
        {
            _mouthShapeMask.lockMouthHeight = GetBool(
                "MouthHeightLock",
                _mouthShapeMask.lockMouthHeight
            );
            _mouthShapeMask.hideMouthOutsideTexture = GetBool(
                "MouthEdgeHide",
                _mouthShapeMask.hideMouthOutsideTexture
            );
            _mouthShapeMask.protectMouthDuringBlink = GetBool(
                "MouthBlinkProtection",
                _mouthShapeMask.protectMouthDuringBlink
            );
            _mouthShapeMask.mouthBlinkProtectionThreshold = GetClampedFloat(
                "KiwiTrack.MouthBlinkThreshold",
                _mouthShapeMask.mouthBlinkProtectionThreshold,
                0.10f,
                0.90f
            );
            _mouthShapeMask.mouthHideEdgeMargin = GetClampedFloat(
                "KiwiTrack.MouthHideMargin",
                _mouthShapeMask.mouthHideEdgeMargin,
                0f,
                0.05f
            );
            _mouthShapeMask.mouthShowEdgeMargin = GetClampedFloat(
                "KiwiTrack.MouthShowMargin",
                _mouthShapeMask.mouthShowEdgeMargin,
                _mouthShapeMask.mouthHideEdgeMargin,
                0.10f
            );
            _mouthShapeMask.mouthEdgeHideConfirmationSamples = Mathf.Clamp(
                PlayerPrefs.GetInt(
                    "KiwiTrack.MouthEdgeConfirmations",
                    _mouthShapeMask.mouthEdgeHideConfirmationSamples
                ),
                1,
                6
            );
            _mouthShapeMask.mouthEdgeHideGraceSeconds = GetClampedFloat(
                "KiwiTrack.MouthEdgeGrace",
                _mouthShapeMask.mouthEdgeHideGraceSeconds,
                0f,
                0.30f
            );
            _mouthShapeMask.mouthEdgeShowConfirmationSamples = Mathf.Clamp(
                PlayerPrefs.GetInt(
                    "KiwiTrack.MouthShowConfirmations",
                    _mouthShapeMask.mouthEdgeShowConfirmationSamples
                ),
                1,
                4
            );
            _mouthShapeMask.mouthHideFadeSeconds = GetClampedFloat(
                "KiwiTrack.MouthHideFade",
                _mouthShapeMask.mouthHideFadeSeconds,
                0.005f,
                0.20f
            );
            _mouthShapeMask.mouthShowFadeSeconds = GetClampedFloat(
                "KiwiTrack.MouthShowFade",
                _mouthShapeMask.mouthShowFadeSeconds,
                0.005f,
                0.30f
            );
        }
        if (_expressionReaction != null)
        {
            _expressionReaction.enableEyeDisplayScale = GetBool(
                "ReferenceSizedEyesV4",
                _expressionReaction.enableEyeDisplayScale
            );
            _expressionReaction.eyeBaseDisplayScaleX = GetClampedFloat(
                "KiwiTrack.EyeDisplayScaleXV4",
                _expressionReaction.eyeBaseDisplayScaleX,
                0.75f,
                2f
            );
            _expressionReaction.eyeBaseDisplayScaleY = GetClampedFloat(
                "KiwiTrack.EyeDisplayScaleYV4",
                _expressionReaction.eyeBaseDisplayScaleY,
                0.75f,
                2.5f
            );
            _expressionReaction.enableMouthVisualZoom = GetBool(
                "NativeGpuBigMouth",
                _expressionReaction.enableMouthVisualZoom
            );
            _expressionReaction.mouthLayoutPositionY = GetClampedFloat(
                "KiwiTrack.MouthLayoutPositionYV1",
                _expressionReaction.mouthLayoutPositionY,
                -650f,
                -200f
            );
            _expressionReaction.mouthOpenStart = GetClampedFloat(
                "KiwiTrack.MouthOpenStartV1",
                _expressionReaction.mouthOpenStart,
                0f,
                0.5f
            );
            _expressionReaction.mouthOpenFull = Mathf.Max(
                _expressionReaction.mouthOpenStart + 0.01f,
                GetClampedFloat(
                    "KiwiTrack.MouthOpenFullV1",
                    _expressionReaction.mouthOpenFull,
                    0.1f,
                    1f
                )
            );
            _expressionReaction.smileStart = GetClampedFloat(
                "KiwiTrack.SmileStartV1",
                _expressionReaction.smileStart,
                0f,
                0.5f
            );
            _expressionReaction.smileFull = Mathf.Max(
                _expressionReaction.smileStart + 0.01f,
                GetClampedFloat(
                    "KiwiTrack.SmileFullV1",
                    _expressionReaction.smileFull,
                    0.1f,
                    1f
                )
            );
            _expressionReaction.mouthOpenMaxZoomX = GetClampedFloat(
                "KiwiTrack.MouthOpenZoomXV3",
                _expressionReaction.mouthOpenMaxZoomX,
                1f,
                3f
            );
            _expressionReaction.mouthOpenMaxZoomY = GetClampedFloat(
                "KiwiTrack.MouthOpenZoomYV3",
                _expressionReaction.mouthOpenMaxZoomY,
                1f,
                3f
            );
            _expressionReaction.mouthSmileMaxZoomX = GetClampedFloat(
                "KiwiTrack.MouthSmileZoomXV3",
                _expressionReaction.mouthSmileMaxZoomX,
                1f,
                3f
            );
            _expressionReaction.mouthSmileMaxZoomY = GetClampedFloat(
                "KiwiTrack.MouthSmileZoomYV3",
                _expressionReaction.mouthSmileMaxZoomY,
                1f,
                2f
            );
            _expressionReaction.mouthEffectResponse = GetClampedFloat(
                "KiwiTrack.MouthEffectResponseV3",
                _expressionReaction.mouthEffectResponse,
                30f,
                400f
            );
            _expressionReaction.mouthEffectDirectThreshold = GetClampedFloat(
                "KiwiTrack.MouthDirectThresholdV3",
                _expressionReaction.mouthEffectDirectThreshold,
                0.05f,
                1f
            );
            _expressionReaction.mouthEffectRestDeadZone = GetClampedFloat(
                "KiwiTrack.MouthEffectDeadZoneV3",
                _expressionReaction.mouthEffectRestDeadZone,
                0f,
                0.05f
            );
            _expressionReaction.preventMouthEyeOverlap = GetBool(
                "PreventMouthEyeOverlap",
                _expressionReaction.preventMouthEyeOverlap
            );
            _expressionReaction.mouthEyeSafetyMarginPixels = GetClampedFloat(
                "KiwiTrack.MouthEyeMarginV3",
                _expressionReaction.mouthEyeSafetyMarginPixels,
                4f,
                120f
            );
            _expressionReaction.mouthEyeLimitReleaseResponse = GetClampedFloat(
                "KiwiTrack.MouthEyeRelease",
                _expressionReaction.mouthEyeLimitReleaseResponse,
                20f,
                240f
            );
            _expressionReaction.preventMouthSurfaceClipping = GetBool(
                "PreventMouthSurfaceClippingV1",
                _expressionReaction.preventMouthSurfaceClipping
            );
            _expressionReaction.mouthSurfaceSafetyMargin = GetClampedFloat(
                "KiwiTrack.MouthSurfaceSafetyMarginV1",
                _expressionReaction.mouthSurfaceSafetyMargin,
                0f,
                0.15f
            );
            _expressionReaction.mouthSurfaceLimitReleaseResponse = GetClampedFloat(
                "KiwiTrack.MouthSurfaceReleaseV1",
                _expressionReaction.mouthSurfaceLimitReleaseResponse,
                20f,
                240f
            );
        }
        if (_mouthDisplaySizeLock != null)
        {
            _mouthDisplaySizeLock.maximumVisibleScale = GetClampedFloat(
                "KiwiTrack.MouthCalibratedSizeV3",
                _mouthDisplaySizeLock.maximumVisibleScale,
                0.25f,
                1f
            );
        }
        m.enableMotionAccent = GetBool("MotionAccent", m.enableMotionAccent);
        m.enableSurpriseJump = GetBool("SurpriseJump", m.enableSurpriseJump);
        m.enableSurpriseSquash = GetBool("SurpriseSquash", m.enableSurpriseSquash);
        m.enableHappyWiggle = GetBool("Happy", m.enableHappyWiggle);
        m.enableBlinkSquash = GetBool("Blink", m.enableBlinkSquash);
        m.enableTalkingMotion = GetBool("Talk", m.enableTalkingMotion);
        m.enablePoutPuff = GetBool("Pout", m.enablePoutPuff);
        m.enableGrumpyShake = GetBool("Grumpy", m.enableGrumpyShake);
        m.enableIdleLife = GetBool("Idle", m.enableIdleLife);
        m.reactionMultiplier = GetClampedFloat("KiwiTrack.Reaction", m.reactionMultiplier, 1f, 8f);
    }

    private static void SetBool(string key, bool value)
    {
        PlayerPrefs.SetInt("KiwiTrack." + key, value ? 1 : 0);
    }

    private static bool GetBool(string key, bool fallback)
    {
        return PlayerPrefs.GetInt("KiwiTrack." + key, fallback ? 1 : 0) != 0;
    }

    private static float GetClampedFloat(
        string key,
        float fallback,
        float minimum,
        float maximum)
    {
        float value = PlayerPrefs.GetFloat(key, fallback);
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            value = fallback;
        }

        return Mathf.Clamp(value, minimum, maximum);
    }
}
