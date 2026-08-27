using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v5.1 Phase 16 diagnostic-only camera / landmark / avatar comparator.
///
/// The overlay observes the same canonical snapshot used by Root / FaceParts.
/// It never submits tracking data, advances generations, mutates recovery,
/// changes Canvas alpha, or writes the avatar Transform. Its only purposes are:
/// 1) show the semantic-frame-matched camera snapshot with canonical
///    semantic landmarks in the same mirrored presentation space;
/// 2) expose source/semantic/rigid identity on the recorded Game view; and
/// 3) optionally write one CSV row per Unity render frame so camera freshness,
///    landmark movement, canonical identity and final rendered Root motion can
///    be correlated after a capture.
/// </summary>
[DefaultExecutionOrder(36500)]
[DisallowMultipleComponent]
public sealed class KiwiFrameComparisonOverlay : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Frame Comparison Overlay";

    private const int LandmarkTextureSize = 384;
    private const int CsvFlushIntervalFrames = 60;

    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    public static KiwiFrameComparisonOverlay Instance { get; private set; }

    [Header("Camera / Landmark comparison")]
    public bool visible = true;
    public bool drawAllLandmarks = true;
    public bool drawRigidAnchors = true;
    public bool showHelp = true;

    [Range(260f, 560f)]
    public float previewWidth = 420f;

    [Header("Frame CSV")]
    public bool recordCsvOnStart = false;

    [SerializeField] private string currentCsvPath = string.Empty;
    [SerializeField] private int recordedFrameCount;
    [SerializeField] private long debugSemanticTimestamp = -1L;
    [SerializeField] private long debugRigidTimestamp = -1L;
    [SerializeField] private string debugProvider = string.Empty;
    [SerializeField] private float debugLandmarkMaxStep;
    [SerializeField] private float debugLandmarkMeanStep;
    [SerializeField] private float debugRootRotationStep;
    [SerializeField] private float debugRootPositionStep;
    [SerializeField] private float debugRootScaleStep;
    [SerializeField] private bool debugLandmarkPreviewEpochMatched;
    [SerializeField] private long debugLandmarkPreviewSemanticTimestamp = -1L;

    private FaceLandmarkerRunner _runner;
    private FacePartCropper _cropper;
    private KiwiFaceMotion _faceMotion;
    private KiwiMatureTrackingTelemetry _telemetry;
    private KiwiTrackingProviderHub _trackingHub;
    private KiwiMatureVTuberSupervisor _supervisor;

    private Vector2[] _landmarks;
    private Vector2[] _previousSemanticLandmarks;
    private int _landmarkCount;
    private long _lastSemanticTimestamp = long.MinValue;
    private bool _semanticChangedThisFrame;
    private bool _hasFace;

    private Texture2D _landmarkOverlayTexture;
    private Color32[] _landmarkPixels;

    private bool _hasCanonicalFrame;
    private KiwiTrackingFrame _canonicalFrame;
    private ulong _lastOverlayRigidFrameId;
    private bool _cameraFreshThisFrame;
    // KIWI_V5_1_PHASE16_20_7_V13_NATIVE_CAMERA_TELEMETRY
    // KIWI_V5_1_PHASE16_20_7_V14_ASYNC_ACQUISITION_TELEMETRY
    // KIWI_V5_1_PHASE16_20_7_V15_NV12_INGEST_TELEMETRY
    // KIWI_V5_1_PHASE16_20_7_V16_IMMEDIATE_INGEST_FLUSH_TELEMETRY
    // KIWI_V5_1_PHASE16_20_7_V17_CAPTURE_DEVICE_ISOLATION_TELEMETRY
    // KIWI_V5_1_PHASE16_20_7_V18_FULLY_DECOUPLED_LATEST_FRAME_TELEMETRY
    // KIWI_V5_1_PHASE16_20_7_V19_CAPTURE_TRANSPORT_AB_TELEMETRY
    // KIWI_V5_1_PHASE16_20_9_V21_FRAME_PACING_DIAGNOSTIC_TELEMETRY
    // KIWI_V5_1_PHASE16_20_10_V22_INFERENCE_READBACK_BOUNDARY_TELEMETRY
    // KIWI_V5_1_PHASE16_20_12_V24_INFERENCE_BACKEND_TELEMETRY
    // KIWI_V5_1_PHASE16_20_13_V25_CPU_FAILURE_STAGE_TELEMETRY
    private ulong _lastNativePresentedSequenceForFreshness;
    private bool _hasNativeCameraTelemetry;
    private Mediapipe.Unity.KiwiNativeCameraTelemetrySnapshot
        _nativeCameraTelemetry;

    private bool _hasPreviousRoot;
    private Vector3 _previousRootPosition;
    private Quaternion _previousRootRotation = Quaternion.identity;
    private Vector3 _previousRootScale = Vector3.one;

    private StreamWriter _csvWriter;
    private int _framesSinceCsvFlush;

    private GUIStyle _headerStyle;
    private GUIStyle _bodyStyle;
    private GUIStyle _smallStyle;
    private float _overlayStartUnscaledTime;

    public bool IsCsvRecording => _csvWriter != null;
    public string CurrentCsvPath => currentCsvPath;
    public int RecordedFrameCount => recordedFrameCount;

    /// <summary>
    /// Height this diagnostic panel wants at the current camera aspect. The
    /// telemetry panel reserves this space so the right diagnostic column can
    /// stack cleanly even in shorter Game views.
    ///
    /// Phase16.19.5 deliberately reserves two presentation regions:
    /// 1) the current live camera texture at render cadence; and
    /// 2) the immutable semantic-frame-matched snapshot used only for precise
    ///    Landmark epoch inspection.
    /// </summary>
    public float PreferredPanelHeight
    {
        get
        {
            Texture liveSourceTexture = GetSourceTexture();
            Texture matchedTexture =
                GetLandmarkPreviewTexture(
                    out _,
                    out _);

            Texture aspectTexture =
                liveSourceTexture != null
                    ? liveSourceTexture
                    : matchedTexture;

            float width = Mathf.Clamp(previewWidth, 260f, 560f);
            float sourceAspect =
                aspectTexture != null && aspectTexture.height > 0
                    ? aspectTexture.width / (float)aspectTexture.height
                    : 16f / 9f;

            float preferredPreviewHeight = Mathf.Clamp(
                width / Mathf.Max(0.25f, sourceAspect),
                150f,
                330f);

            const float firstPreviewHeaderHeight = 28f;
            const float secondPreviewHeaderHeight = 26f;
            return
                firstPreviewHeaderHeight +
                preferredPreviewHeight * 2f +
                secondPreviewHeaderHeight +
                (showHelp ? 142f : 120f);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (!Application.isEditor && !Debug.isDebugBuild)
        {
            return;
        }

        EnsureInstance();
    }

    public static KiwiFrameComparisonOverlay EnsureInstance()
    {
        KiwiFrameComparisonOverlay existing =
            FindFirstObjectByType<KiwiFrameComparisonOverlay>();

        if (existing != null)
        {
            Instance = existing;
            return existing;
        }

        GameObject host = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        return host.AddComponent<KiwiFrameComparisonOverlay>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        _overlayStartUnscaledTime = Time.unscaledTime;
        EnsureLandmarkTexture();
    }

    private void Start()
    {
        RefreshReferences(true);

        if (recordCsvOnStart)
        {
            StartCsvRecording();
        }
    }

    private void OnDestroy()
    {
        StopCsvRecording();

        if (_landmarkOverlayTexture != null)
        {
            Destroy(_landmarkOverlayTexture);
            _landmarkOverlayTexture = null;
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void Update()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.F8))
        {
            visible = !visible;
        }

        if (Input.GetKeyDown(KeyCode.F9))
        {
            ToggleCsvRecording();
        }
#endif
    }

    private void LateUpdate()
    {
        RefreshReferences(false);
        CaptureFrameComparisonState();

        if (_csvWriter != null)
        {
            WriteCsvRow();
        }
    }

    public void ToggleOverlay()
    {
        visible = !visible;
    }

    public void ToggleCsvRecording()
    {
        if (_csvWriter == null)
        {
            StartCsvRecording();
        }
        else
        {
            StopCsvRecording();
        }
    }

    public void StartCsvRecording()
    {
        if (_csvWriter != null)
        {
            return;
        }

        try
        {
            string directory = Path.Combine(
                Application.persistentDataPath,
                "KiwiFrameComparison");

            Directory.CreateDirectory(directory);

            currentCsvPath = Path.Combine(
                directory,
                "KiwiFrameComparison_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss", Invariant) +
                ".csv");

            _csvWriter = new StreamWriter(
                currentCsvPath,
                false,
                new UTF8Encoding(false));

            // KIWI_V5_1_PHASE16_8_COMMERCIAL_GAP_DIAGNOSTICS
            _csvWriter.WriteLine(
                "unityFrame,realtimeSeconds,cameraFresh,sourceTextureId,sourceWidth,sourceHeight," +
                "nativeCameraActive,nativeSessionGeneration,nativeCaptureSequence,nativePresentedSequence," +
                "nativeCaptureCount,nativeDroppedCount,nativePresentedCount,nativeProducerFenceCompleted," +
                "nativeCaptureAgeMs,nativePresentedAgeMs,nativeTargetFps,nativePresentationTextureId," +
                "nativeSourceSequence,nativeSourceCount,nativeSupersededCount,nativeProcessingFailureCount," +
                "nativeSourceAgeMs,nativeWorkerRunning,nativeTimestampCalibrated,nativeQpcFrequency," +
                "nativeSourceIntervalMs,nativeIngestSequence,nativeIngestCount,nativeIngestFailureCount," +
                "nativeIngestAgeMs,nativeIngestCopySubmitMs,nativeIngestFenceCompleted,nativeD3D11MultithreadProtected,nativeProcessingCpuMs," +
                "nativeCallbackCpuMs,nativeRequestNextCpuMs,nativeCallbackToRequestNextMs,nativeSourceTimestampIntervalMs,nativeArrivalIntervalMs," +
                "nativeIngestConsumerFenceCompleted,nativeCaptureDeviceIsolated,nativeCaptureD3D11MultithreadProtected," +
                "nativeCaptureGpuWaitCount,nativeProcessingGpuWaitCount,nativeReadyReplacementCount,nativeAllSlotsBusyDropCount," +
                "nativeIngestAcceptedCount,nativeIngestAcceptedRatio,nativeReadyUnclaimedCount,nativeOldestReadyAgeMs," +
                "nativeProducerFenceLag,nativeConsumerFenceLag,nativeLatestProcessedSourceAgeMs," +
                "nativeCaptureTransportId,nativeSystemMemoryCaptureEnabled,nativeMfSampleResidenceMs," +
                "nativeCaptureCopyGpuSubmitMs,nativeCaptureCopyGpuCompletionMs,nativeCaptureCopyGpuOutstandingDepth," +
                "nativeCpuNv12CopyMs,nativeCpuNv12CopyBytes,nativeCpuLatestReplacementCount,nativeCpuAllSlotsBusyDropCount," +
                "nativeGpuUploadSubmitMs,nativeGpuUploadCount," +
                "framePacingModeId,framePacingVSyncCount,framePacingTargetFrameRate," +
                "framePacingRenderFrameInterval,framePacingEffectiveRenderFrameRate,framePacingWillCurrentFrameRender," +
                "inferencePreReadbackSubmitCpuMs,inferenceReadbackRequestCpuMs,inferenceRequestToDoneObservedMs," +
                "inferenceReadbackObservedFrameDelta,inferencePollIntervalMs,inferenceReadbackCloneCpuMs," +
                "inferenceDecodeMathCpuMs,inferenceBoundarySampleCount," +
                "inferenceBackendId,inferenceCpuBackendEnabled," +
                "inferenceFailureStageCurrentId,inferenceFailureStageLastId," +
                "inferenceFailureKindLastId,inferenceFailureExceptionTypeId,inferenceFailureExceptionHResult," +
                "inferenceFailureStageAttemptCount,inferenceFailureStageSuccessCount,inferenceFailureStageFailureCount," +
                "inferenceFailureStageExceptionCount,inferenceFailureStageNullOutputCount,inferenceFailureStageShapeMismatchCount," +
                "cameraGeneration,providerGeneration,trackingSessionGeneration,providerId,canonicalFrameId," +
                "rigidFrameId,rigidTimestamp,semanticTimestamp,semanticMatched,semanticChanged,landmarkCount," +
                "landmarkMeanStepNorm,landmarkMaxStepNorm,rigidFaceCenterX,rigidFaceCenterY," +
                "rigidLeftEyeX,rigidLeftEyeY,rigidRightEyeX,rigidRightEyeY,rigidNoseX,rigidNoseY," +
                "rigidChinX,rigidChinY,observationAgeMs,arrivalAgeMs," +
                "rootLocalX,rootLocalY,rootLocalZ,rootEulerX,rootEulerY,rootEulerZ," +
                "rootScaleX,rootScaleY,rootScaleZ,rootPositionDelta,rootRotationDeltaDeg,rootScaleDelta," +
                "continuityEnabled,directBypassSuppressed,discontinuityGuard,trackingRateHz,effectiveIntervalMs,responseCap," +
                "handoffActive,handoffWeight,handoffTargetWeight,handoffReleaseStep,handoffCenterOffset,handoffRotationOffsetDeg,handoffScaleRatio,handoffCount," +
                "commercialCadenceBoost,renderFps,auxMediaPipeHz," +
                // KIWI_V5_1_PHASE16_9_COMMERCIAL_PIPELINE_AND_FACEPART_DIAGNOSTICS
                // KIWI_V5_1_PHASE16_10_INFERENCE_PIPELINE_DIAGNOSTICS
                // KIWI_V5_1_PHASE16_11_FRESHNESS_AGE_DIAGNOSTICS
                // KIWI_V5_1_PHASE16_12_PERSISTENT_ROI_DIAGNOSTICS
                // KIWI_V5_1_PHASE16_13_LATENCY_AND_STATIC_REST_DIAGNOSTICS
                // KIWI_V5_1_PHASE16_14_STABLE_PIPELINE_RENDER_BOUNDARY_DIAGNOSTICS
                // KIWI_V5_1_PHASE16_15_ROOT_CONTINUITY_DIAGNOSTICS
                // KIWI_V5_1_PHASE16_16_ROOT_SPACE_PROVIDER_BRIDGE_DIAGNOSTICS
                // KIWI_V5_1_PHASE16_17_SINGLE_PRESENTATION_AUTHORITY_DIAGNOSTICS
                // KIWI_V5_1_PHASE16_18_SINGLE_HANDOFF_AUTHORITY_DIAGNOSTICS
                // KIWI_V5_1_PHASE16_19_STRICT_FACEPART_PRESENTATION_EPOCH_DIAGNOSTICS
                "runnerFreshSourceHz,runnerSubmissionHz,runnerResultHz,canonicalAdoptionHz,runnerReadbackLatencyMs,sourceToSubmissionGapHz,submissionToResultGapHz,resultToAdoptionGapHz,submissionEfficiency,resultEfficiency,adoptionEfficiency,canonicalAdoptionCount," +
                "inferenceTelemetryOperational,inferencePipelineDepth,inferenceLaneLimit,inferenceActiveLanes,inferenceOldestPendingAgeMs,inferenceLatencyMs,inferenceScheduleDelayMs,inferenceSourceToCompletionAgeMs,inferenceAcceptedSourceAgeMs," +
                "inferenceRawPresenceLogit,inferencePresence,inferenceConsecutiveFailures,inferenceHasRegion,inferenceTrackingHealthy,inferenceRegionRetentionActive,inferenceRegionTrustedAgeMs,inferenceRegionGraceRemainingMs,inferenceRegionRecoveryScale,inferenceRegionRetainedFailureCount,inferenceRegionReleaseCount,inferenceRegionCenterX,inferenceRegionCenterY,inferenceRegionWidth,inferenceRegionHeight,inferenceScheduleCpuMs,inferenceGpuReadbackWaitMs,inferenceDecodeCpuMs," +
                "inferenceLatencyFirstScheduling,inferenceStableDesktopScheduling,inferenceCompletionIntervalMs,inferenceLanePromotionCount,inferenceLaneDemotionCount,inferenceSingleFlightProbeCount," +
                "inferenceScheduledCount,inferenceReadbackCompletedCount,inferenceCompletedCount,inferenceDroppedFreshCount,inferenceRejectedPresenceCount,inferenceRejectedInvalidCount,inferenceDiscardedStaleCount,inferenceDiscardedCrossSystemCount,inferenceDiscardedStaleAnchorCount,inferenceDiscardedStaleGenerationCount,inferenceDiscardedStaleSourceCount,inferenceSoftAnchorUpdateCount,inferenceHardAnchorInvalidationCount,inferenceSoftAnchorSupersededRoiUpdateCount,inferenceDropRatio,inferenceCompletionRatio," +
                "commercialRigidShockCount,commercialRigidCenterResidualEyeSpans,commercialRigidRotationDelta,commercialRigidDepthLogDelta," +
                "semanticTopologyRejectCount,semanticTransactionCanonicalFrameId,semanticTransactionSequence," +
                "landmarkRefinedPointCount,landmarkIsolatedRejectCount,landmarkIsolatedCandidateCount,landmarkMassRejectBypass,landmarkMassRejectBypassCount,landmarkMeanAdjustmentEyeSpans,landmarkMaxAdjustmentEyeSpans,landmarkGeometryQuality,landmarkRefinerTimestamp," +
                "commercialFailoverDeferred,commercialFailoverGraceRemainingMs,commercialDeferredFailoverCount," +
                "writerAuditFrame,rootUpdateToLatePosDelta,rootUpdateToLateRotDelta,rootUpdateToLateScaleDelta,rootLateToRenderPosDelta,rootLateToRenderRotDelta,rootLateToRenderScaleDelta," +
                "visualRelativeUpdateToLatePosDelta,visualRelativeUpdateToLateRotDelta,visualRelativeUpdateToLateScaleDelta,visualRelativeLateToRenderPosDelta,visualRelativeLateToRenderRotDelta,visualRelativeLateToRenderScaleDelta," +
                "writerAuditRenderBoundaryObserved,visualMovedWithoutRoot,rootMovedAfterLate,visualMovedWithoutRootCount,rootMovedAfterLateCount," +
                "staticRestActive,staticRestCandidateSeconds,staticRestLockCount,staticRestReleaseCount,beforeRenderRestHoldCount,beforeRenderNewSampleCount,beforeRenderFreshOnlyPolicy,beforeRenderSameSampleSkipCount,beforeRenderAcceptedNewSampleCount," +
                "rootModelHeight,rootRawTargetPosDelta,rootRawTargetRotDelta,rootRawTargetScaleDelta,rootDisplayPreCapPosDelta,rootDisplayPreCapRotDelta,rootDisplayPreCapScaleDelta,rootDisplayPostCapPosDelta,rootDisplayPostCapRotDelta,rootDisplayPostCapScaleDelta,rootContinuityMaxPosStep,rootContinuityMaxRotStep,rootContinuityMaxScaleStep,rootContinuityCapApplied,rootPredictionPositionDelta,rootPredictionLeadMs,rootCorrectionBacklog,rootAuthoritativeFrameMissing,noFrameHoldActive,noFrameHoldCount,sameProviderResumeActive,sameProviderResumeCount,sameProviderResumeSamplesRemaining," +
                "rootProviderBridgeActive,rootProviderBridgeCount,rootProviderBridgeGeneration,rootProviderBridgeBackend,rootProviderBridgeWeight,rootProviderBridgeTargetWeight,rootProviderBridgeReleaseStep,rootProviderBridgeAcceptedSamples,rootProviderBridgeMotionProgress,rootProviderBridgePositionOffset,rootProviderBridgeRotationOffsetDeg,rootProviderBridgeScaleRatio,rootProviderBridgeAppliedPosDelta,rootProviderBridgeAppliedRotDelta,rootProviderBridgeAppliedScaleDelta," +
                "singlePresentationAuthority,quality10PolicyOnly,quality10SharedRootBinding,quality10SuppressedLateUpdateCount,quality10SuppressedBeforeRenderCount,quality10LegacyRootWriteCount,quality10LegacyWriteViolationCount,faceMotionDisplayRateSmoothing,faceMotionStaticRestEnabled,faceMotionAdaptiveMicroFilter,faceMotionPredictionDisabled," +
                "singleHandoffAuthority,canonicalHandoffNormalizationEnabled,canonicalHandoffActive,canonicalHandoffIsResume,localRootProviderBridgeActive,localRootProviderBridgeSuppressedCount,handoffAuthorityViolationCount,hubHandoffEnvelopeGuardActivationCount," +
                "strictFacePartPresentationEpoch,facePartPredictionDisabled,facePartMatchedAgeCompensationDisabled,facePartDirectMotionDisabled,facePartLiveResidualDisabled,facePartPresentationEpochAligned,facePartPresentationEpochViolationCount,facePartEpochSemanticTimestamp,facePartEpochTextureCanonicalFrameId," +
                "faceTextureTransactionOperational,faceTextureSceneBindingValid,faceTextureStrictPresentation,faceTextureSemanticTimestamp,faceTextureCanonicalFrameId,faceTextureMatchDeltaMs,faceTextureBufferedFrames,faceTextureCaptureCount,faceTextureCommitCount,faceTextureMissCount,faceTextureHoldCount,faceTextureExternalWriter,faceTextureExternalWriterCount,liveTextureAdvancedWhileSemanticHeld");

            recordedFrameCount = 0;
            _framesSinceCsvFlush = 0;

            Debug.Log(
                "[KiwiAvatarSystem] Frame comparison CSV recording started: " +
                currentCsvPath);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Could not start frame comparison CSV: " +
                ex.Message);

            _csvWriter = null;
            currentCsvPath = string.Empty;
        }
    }

    public void StopCsvRecording()
    {
        if (_csvWriter == null)
        {
            return;
        }

        try
        {
            _csvWriter.Flush();
            _csvWriter.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Frame comparison CSV close warning: " +
                ex.Message);
        }
        finally
        {
            _csvWriter = null;
        }

        Debug.Log(
            "[KiwiAvatarSystem] Frame comparison CSV recording stopped. rows=" +
            recordedFrameCount +
            " path=" +
            currentCsvPath);
    }

    public string BuildStatusLine()
    {
        return
            "visible=" + visible +
            " csv=" + IsCsvRecording +
            " provider=" + debugProvider +
            " rigid=" + debugRigidTimestamp +
            " semantic=" + debugSemanticTimestamp +
            " landmarkStep(max/mean)=" +
            debugLandmarkMaxStep.ToString("F5", Invariant) +
            "/" +
            debugLandmarkMeanStep.ToString("F5", Invariant) +
            " rootStep(pos/deg)=" +
            debugRootPositionStep.ToString("F6", Invariant) +
            "/" +
            debugRootRotationStep.ToString("F3", Invariant);
    }

    private void RefreshReferences(bool force)
    {
        if (force || _runner == null)
        {
            _runner = FindFirstObjectByType<FaceLandmarkerRunner>();
        }

        if (force || _cropper == null)
        {
            _cropper = FindFirstObjectByType<FacePartCropper>();
        }

        if (force || _faceMotion == null)
        {
            _faceMotion = FindFirstObjectByType<KiwiFaceMotion>();
        }

        if (force || _telemetry == null)
        {
            _telemetry = FindFirstObjectByType<KiwiMatureTrackingTelemetry>();
        }

        if (force || _trackingHub == null)
        {
            _trackingHub = FindFirstObjectByType<KiwiTrackingProviderHub>();
        }

        if (force || _supervisor == null)
        {
            _supervisor = FindFirstObjectByType<KiwiMatureVTuberSupervisor>();
        }
    }

    private void CaptureFrameComparisonState()
    {
        _semanticChangedThisFrame = false;
        _hasCanonicalFrame =
            KiwiCanonicalTrackingFrame.TryGetFrame(
                out _canonicalFrame);

        if (_hasCanonicalFrame)
        {
            debugProvider = _canonicalFrame.providerId ?? string.Empty;
            debugRigidTimestamp = _canonicalFrame.rigid.timestamp;
        }
        else
        {
            debugProvider = string.Empty;
            debugRigidTimestamp = -1L;
        }

        bool semanticChanged =
            KiwiCanonicalTrackingFrame.TryGetSemanticLandmarksIfChanged(
                _runner,
                ref _landmarks,
                _lastSemanticTimestamp,
                out int count,
                out long timestamp,
                out bool hasFace);

        _hasFace = hasFace;

        bool rigidChangedForOverlay =
            _hasCanonicalFrame &&
            _canonicalFrame.rigid.frameId != _lastOverlayRigidFrameId;

        if (semanticChanged)
        {
            _semanticChangedThisFrame = true;
            _landmarkCount = count;
            debugSemanticTimestamp = timestamp;

            UpdateLandmarkStepMetrics(count);
            _lastSemanticTimestamp = timestamp;
            RebuildLandmarkOverlay();
            _lastOverlayRigidFrameId =
                _hasCanonicalFrame
                    ? _canonicalFrame.rigid.frameId
                    : 0UL;
        }
        else if (_hasCanonicalFrame)
        {
            debugSemanticTimestamp =
                _canonicalFrame.hasSemanticLandmarks
                    ? _canonicalFrame.semanticTimestamp
                    : -1L;

            if (rigidChangedForOverlay)
            {
                RebuildLandmarkOverlay();
                _lastOverlayRigidFrameId = _canonicalFrame.rigid.frameId;
            }
        }

        Texture sourceTexture = GetSourceTexture();
        WebCamTexture webCam = sourceTexture as WebCamTexture;

        _hasNativeCameraTelemetry =
            Mediapipe.Unity.KiwiNativeCameraTelemetry.TryGetSnapshot(
                out _nativeCameraTelemetry);

        bool nativeFresh =
            _hasNativeCameraTelemetry &&
            _nativeCameraTelemetry.latestPresentedSequence > 0UL &&
            _nativeCameraTelemetry.latestPresentedSequence !=
                _lastNativePresentedSequenceForFreshness;

        if (nativeFresh)
        {
            _lastNativePresentedSequenceForFreshness =
                _nativeCameraTelemetry.latestPresentedSequence;
        }

        _cameraFreshThisFrame =
            nativeFresh ||
            (webCam != null && webCam.didUpdateThisFrame);

        CaptureRootStepMetrics();
    }

    private void CaptureRootStepMetrics()
    {
        Transform root =
            _faceMotion != null
                ? _faceMotion.kiwiRoot
                : null;

        if (root == null)
        {
            debugRootPositionStep = 0f;
            debugRootRotationStep = 0f;
            debugRootScaleStep = 0f;
            _hasPreviousRoot = false;
            return;
        }

        Vector3 position = root.localPosition;
        Quaternion rotation = root.localRotation;
        Vector3 scale = root.localScale;

        if (_hasPreviousRoot)
        {
            debugRootPositionStep =
                Vector3.Distance(_previousRootPosition, position);
            debugRootRotationStep =
                Quaternion.Angle(_previousRootRotation, rotation);
            debugRootScaleStep =
                Vector3.Distance(_previousRootScale, scale);
        }
        else
        {
            debugRootPositionStep = 0f;
            debugRootRotationStep = 0f;
            debugRootScaleStep = 0f;
        }

        _previousRootPosition = position;
        _previousRootRotation = rotation;
        _previousRootScale = scale;
        _hasPreviousRoot = true;
    }

    private void UpdateLandmarkStepMetrics(int count)
    {
        debugLandmarkMeanStep = 0f;
        debugLandmarkMaxStep = 0f;

        if (_landmarks == null || count <= 0)
        {
            return;
        }

        if (
            _previousSemanticLandmarks != null &&
            _previousSemanticLandmarks.Length >= count)
        {
            float sum = 0f;
            float maximum = 0f;

            for (int i = 0; i < count; i++)
            {
                float distance =
                    Vector2.Distance(
                        _previousSemanticLandmarks[i],
                        _landmarks[i]);

                sum += distance;
                maximum = Mathf.Max(maximum, distance);
            }

            debugLandmarkMeanStep = sum / Mathf.Max(1, count);
            debugLandmarkMaxStep = maximum;
        }

        if (
            _previousSemanticLandmarks == null ||
            _previousSemanticLandmarks.Length < count)
        {
            _previousSemanticLandmarks = new Vector2[count];
        }

        Array.Copy(
            _landmarks,
            _previousSemanticLandmarks,
            count);
    }

    private void EnsureLandmarkTexture()
    {
        if (_landmarkOverlayTexture != null)
        {
            return;
        }

        _landmarkOverlayTexture = new Texture2D(
            LandmarkTextureSize,
            LandmarkTextureSize,
            TextureFormat.RGBA32,
            false,
            true)
        {
            name = "Kiwi Frame Comparison Landmarks",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave
        };

        _landmarkPixels =
            new Color32[LandmarkTextureSize * LandmarkTextureSize];

        ClearLandmarkPixels();
        _landmarkOverlayTexture.SetPixels32(_landmarkPixels);
        _landmarkOverlayTexture.Apply(false, false);
    }

    private void RebuildLandmarkOverlay()
    {
        EnsureLandmarkTexture();
        ClearLandmarkPixels();

        // KIWI_V5_1_PHASE16_19_4_LANDMARK_ANNOTATION_MIRROR_ALIGNMENT
        // MediaPipe's front-camera input is already transformed into the same
        // mirrored presentation space used by the sample Screen. Canonical
        // normalized landmark X therefore must NOT be mirrored again here.
        // FacePartCropper.mirrorX is a separate raw-texture sampling conversion
        // and must not be reused by this annotation-only overlay.
        const bool mirrorLandmarksX = false;

        if (drawAllLandmarks && _landmarks != null)
        {
            Color32 landmarkColor =
                new Color32(50, 235, 255, 235);

            for (int i = 0; i < _landmarkCount; i++)
            {
                DrawNormalizedPoint(
                    _landmarks[i],
                    mirrorLandmarksX,
                    landmarkColor,
                    1);
            }
        }

        if (drawRigidAnchors && _hasCanonicalFrame)
        {
            FacePrecisionTrackingData rigid =
                _canonicalFrame.rigid;

            DrawNormalizedPoint(
                rigid.faceCenter,
                mirrorLandmarksX,
                new Color32(255, 70, 70, 255),
                4);

            DrawNormalizedPoint(
                rigid.leftEyeCenter,
                mirrorLandmarksX,
                new Color32(70, 255, 90, 255),
                3);

            DrawNormalizedPoint(
                rigid.rightEyeCenter,
                mirrorLandmarksX,
                new Color32(70, 255, 90, 255),
                3);

            DrawNormalizedPoint(
                rigid.nose,
                mirrorLandmarksX,
                new Color32(255, 230, 60, 255),
                3);

            DrawNormalizedPoint(
                rigid.chin,
                mirrorLandmarksX,
                new Color32(255, 90, 235, 255),
                3);
        }

        _landmarkOverlayTexture.SetPixels32(_landmarkPixels);
        _landmarkOverlayTexture.Apply(false, false);
    }

    private void ClearLandmarkPixels()
    {
        if (_landmarkPixels == null)
        {
            return;
        }

        Array.Clear(
            _landmarkPixels,
            0,
            _landmarkPixels.Length);
    }

    private void DrawNormalizedPoint(
        Vector2 point,
        bool mirrorX,
        Color32 color,
        int radius)
    {
        if (
            float.IsNaN(point.x) ||
            float.IsNaN(point.y) ||
            float.IsInfinity(point.x) ||
            float.IsInfinity(point.y)
        )
        {
            return;
        }

        float x = mirrorX ? 1f - point.x : point.x;
        float y = 1f - point.y;

        int pixelX = Mathf.RoundToInt(
            Mathf.Clamp01(x) *
            (LandmarkTextureSize - 1));

        int pixelY = Mathf.RoundToInt(
            Mathf.Clamp01(y) *
            (LandmarkTextureSize - 1));

        int safeRadius = Mathf.Clamp(radius, 0, 6);

        for (int yy = -safeRadius; yy <= safeRadius; yy++)
        {
            int py = pixelY + yy;
            if (py < 0 || py >= LandmarkTextureSize)
            {
                continue;
            }

            for (int xx = -safeRadius; xx <= safeRadius; xx++)
            {
                int px = pixelX + xx;
                if (px < 0 || px >= LandmarkTextureSize)
                {
                    continue;
                }

                if (
                    safeRadius > 1 &&
                    xx * xx + yy * yy > safeRadius * safeRadius)
                {
                    continue;
                }

                _landmarkPixels[
                    py * LandmarkTextureSize + px] = color;
            }
        }
    }

    private Texture GetSourceTexture()
    {
        return
            _cropper != null &&
            _cropper.sourceImage != null
                ? _cropper.sourceImage.texture
                : null;
    }

    // KIWI_V5_1_PHASE16_19_3_MATCHED_LANDMARK_PREVIEW_EPOCH
    // The old diagnostic panel overlaid canonical Landmarks on the current live
    // WebCamTexture. With ~100 ms inference/semantic age that deliberately
    // compared two different moments and made correct Landmarks look spatially
    // wrong during motion. Use the immutable camera snapshot already matched by
    // the Phase16.9 Texture Transaction instead.
    private Texture GetLandmarkPreviewTexture(
        out bool epochMatched,
        out long previewSemanticTimestamp)
    {
        epochMatched = false;
        previewSemanticTimestamp = -1L;

        if (
            !KiwiFacePartTextureTransaction.
                TryGetLastCommittedPresentationFrame(
                    out Texture matchedTexture,
                    out long matchedSemanticTimestamp,
                    out _) ||
            matchedTexture == null
        )
        {
            return null;
        }

        previewSemanticTimestamp =
            matchedSemanticTimestamp;

        epochMatched =
            debugSemanticTimestamp >= 0L &&
            matchedSemanticTimestamp ==
                debugSemanticTimestamp;

        return epochMatched
            ? matchedTexture
            : null;
    }

    // KIWI_V5_1_PHASE16_19_5_LIVE_CAMERA_MATCHED_DEBUG_SEPARATION
    // Presentation cadence and semantic diagnostic cadence are intentionally
    // different. The live camera must never wait for a Landmarker result, while
    // canonical Landmarks must never be drawn over an unmatched live frame.
    // Both views are observer-only and do not alter tracking, texture transaction,
    // Root presentation, provider authority, or FacePart production state.
    private void OnGUI()
    {
        if (!visible)
        {
            return;
        }

        EnsureGuiStyles();

        Texture liveSourceTexture = GetSourceTexture();
        Texture matchedTexture =
            GetLandmarkPreviewTexture(
                out bool previewEpochMatched,
                out long previewSemanticTimestamp);

        debugLandmarkPreviewEpochMatched =
            previewEpochMatched;
        debugLandmarkPreviewSemanticTimestamp =
            previewSemanticTimestamp;

        Texture aspectTexture =
            liveSourceTexture != null
                ? liveSourceTexture
                : matchedTexture;

        float width = Mathf.Clamp(previewWidth, 260f, 560f);
        float sourceAspect =
            aspectTexture != null && aspectTexture.height > 0
                ? aspectTexture.width / (float)aspectTexture.height
                : 16f / 9f;

        float desiredPreviewHeight = Mathf.Clamp(
            width / Mathf.Max(0.25f, sourceAspect),
            150f,
            330f);

        const float firstPreviewHeaderHeight = 28f;
        const float secondPreviewHeaderHeight = 26f;
        float footerHeight = showHelp ? 142f : 120f;
        float desiredPanelHeight =
            firstPreviewHeaderHeight +
            desiredPreviewHeight +
            secondPreviewHeaderHeight +
            desiredPreviewHeight +
            footerHeight;

        Rect panel = CalculateNonOverlappingPanelRect(
            width + 8f,
            desiredPanelHeight);

        // The emergency small-window fallback may narrow/shorten the panel to
        // preserve non-overlap. Split the remaining preview budget evenly so
        // neither observer view can cover the telemetry panel.
        width = Mathf.Max(1f, panel.width - 8f);
        float availablePreviewHeight = Mathf.Max(
            2f,
            panel.height -
            firstPreviewHeaderHeight -
            secondPreviewHeaderHeight -
            footerHeight);
        float previewHeight = Mathf.Min(
            desiredPreviewHeight,
            availablePreviewHeight * 0.5f);

        Color oldColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.76f);
        GUI.Box(panel, GUIContent.none);
        GUI.color = oldColor;

        Rect liveTitleRect = new Rect(
            panel.x + 8f,
            panel.y + 5f,
            width - 8f,
            22f);

        GUI.Label(
            liveTitleRect,
            "LIVE CAMERA",
            _headerStyle);

        Rect livePreviewRect = new Rect(
            panel.x + 8f,
            panel.y + firstPreviewHeaderHeight,
            width - 8f,
            previewHeight);

        DrawLiveCameraPreview(
            livePreviewRect,
            liveSourceTexture);

        float matchedTitleY =
            livePreviewRect.yMax + 4f;

        Rect matchedTitleRect = new Rect(
            panel.x + 8f,
            matchedTitleY,
            width - 8f,
            22f);

        GUI.Label(
            matchedTitleRect,
            previewEpochMatched
                ? "MATCHED LANDMARK DEBUG"
                : "MATCHED LANDMARK DEBUG - WAITING FOR MATCHED FRAME",
            _headerStyle);

        Rect matchedPreviewRect = new Rect(
            panel.x + 8f,
            matchedTitleY + secondPreviewHeaderHeight,
            width - 8f,
            previewHeight);

        DrawMatchedLandmarkDebugPreview(
            matchedPreviewRect,
            matchedTexture,
            previewEpochMatched);

        float textY = matchedPreviewRect.yMax + 5f;
        bool semanticMatched =
            _hasCanonicalFrame &&
            _canonicalFrame.hasSemanticLandmarks &&
            _canonicalFrame.rigid.timestamp ==
                _canonicalFrame.semanticTimestamp;

        string line1 =
            "provider=" +
            (string.IsNullOrEmpty(debugProvider) ? "-" : debugProvider) +
            "  liveCam=" +
            (_cameraFreshThisFrame ? "NEW" : "hold") +
            "  landmark=" +
            (_semanticChangedThisFrame ? "NEW" : "hold") +
            "  rigid/semantic=" +
            semanticMatched +
            "  matchedDebug=" +
            (previewEpochMatched ? "MATCHED" : "WAIT");

        GUI.Label(
            new Rect(panel.x + 8f, textY, width - 8f, 20f),
            line1,
            _bodyStyle);

        float observationAgeMs = GetCanonicalObservationAgeMs();

        string line2 =
            "rigid=" + debugRigidTimestamp +
            "  semantic=" + debugSemanticTimestamp +
            "  previewTs=" +
            debugLandmarkPreviewSemanticTimestamp +
            "  pts=" + _landmarkCount +
            "  age=" +
            (observationAgeMs >= 0f
                ? observationAgeMs.ToString("F0", Invariant) + "ms"
                : "-") +
            "  gen=" +
            KiwiRuntimeGenerationContext.CameraGeneration +
            "/" +
            KiwiRuntimeGenerationContext.ProviderGeneration;

        GUI.Label(
            new Rect(panel.x + 8f, textY + 19f, width - 8f, 20f),
            line2,
            _smallStyle);

        string line3 =
            "landmark step mean/max=" +
            debugLandmarkMeanStep.ToString("F5", Invariant) +
            "/" +
            debugLandmarkMaxStep.ToString("F5", Invariant) +
            "  avatar frame step pos/deg=" +
            debugRootPositionStep.ToString("F5", Invariant) +
            "/" +
            debugRootRotationStep.ToString("F2", Invariant);

        GUI.Label(
            new Rect(panel.x + 8f, textY + 38f, width - 8f, 20f),
            line3,
            _smallStyle);

        string line4 =
            "continuity active=" +
            KiwiFrameContinuityDiagnostics.Enabled +
            " suppress=" +
            KiwiFrameContinuityDiagnostics.DirectBypassSuppressed +
            " guard=" +
            KiwiFrameContinuityDiagnostics.DiscontinuityGuardActive +
            " rate=" +
            KiwiFrameContinuityDiagnostics.TrackingRateHz.ToString("F1", Invariant) +
            "Hz cap=" +
            KiwiFrameContinuityDiagnostics.ResponseCap.ToString("F1", Invariant);

        Color continuityColor = GUI.color;
        if (Time.unscaledTime - _overlayStartUnscaledTime > 2f &&
            _faceMotion != null &&
            !KiwiFrameContinuityDiagnostics.Enabled)
        {
            GUI.color = new Color(1f, 0.38f, 0.30f, 1f);
        }

        GUI.Label(
            new Rect(panel.x + 8f, textY + 57f, width - 8f, 20f),
            line4,
            _smallStyle);

        GUI.color = continuityColor;

        string line5 =
            IsCsvRecording
                ? "CSV REC  rows=" + recordedFrameCount
                : "CSV off";

        GUI.Label(
            new Rect(panel.x + 8f, textY + 76f, width - 8f, 20f),
            line5,
            _smallStyle);

        if (showHelp)
        {
            GUI.Label(
                new Rect(panel.x + 8f, textY + 95f, width - 8f, 20f),
                "LIVE = current camera | MATCHED DEBUG = semantic epoch | F8/F9 when legacy input is enabled",
                _smallStyle);
        }
    }


    private void DrawLiveCameraPreview(
        Rect previewRect,
        Texture liveSourceTexture)
    {
        if (liveSourceTexture == null)
        {
            GUI.Label(
                previewRect,
                "Live camera texture unavailable",
                _bodyStyle);
            return;
        }

        GUI.DrawTextureWithTexCoords(
            previewRect,
            liveSourceTexture,
            GetCameraPreviewTexCoords(),
            true);
    }


    private void DrawMatchedLandmarkDebugPreview(
        Rect previewRect,
        Texture matchedTexture,
        bool previewEpochMatched)
    {
        if (!previewEpochMatched || matchedTexture == null)
        {
            GUI.Label(
                previewRect,
                "Waiting for semantic-frame-matched camera snapshot",
                _bodyStyle);
            return;
        }

        GUI.DrawTextureWithTexCoords(
            previewRect,
            matchedTexture,
            GetCameraPreviewTexCoords(),
            true);

        // KIWI_V5_1_PHASE16_19_3_MATCHED_LANDMARK_PREVIEW_EPOCH
        // Canonical Landmarks are rendered only over their exact committed camera
        // epoch. Never substitute the current live frame here.
        if (
            previewEpochMatched &&
            _landmarkOverlayTexture != null
        )
        {
            GUI.DrawTexture(
                previewRect,
                _landmarkOverlayTexture,
                ScaleMode.StretchToFill,
                true);
        }
    }


    private Rect GetCameraPreviewTexCoords()
    {
        bool mirrorX =
            _cropper != null &&
            _cropper.mirrorX;

        return
            mirrorX
                ? new Rect(1f, 0f, -1f, 1f)
                : new Rect(0f, 0f, 1f, 1f);
    }


    private Rect CalculateNonOverlappingPanelRect(
        float panelWidth,
        float panelHeight)
    {
        const float margin = 10f;
        const float gap = 10f;

        panelWidth = Mathf.Min(
            panelWidth,
            Mathf.Max(220f, Screen.width - margin * 2f));

        panelHeight = Mathf.Min(
            panelHeight,
            Mathf.Max(160f, Screen.height - margin * 2f));

        Rect preferred = new Rect(
            Mathf.Max(8f, Screen.width - panelWidth - 18f),
            margin,
            panelWidth,
            panelHeight);

        if (
            _telemetry == null ||
            !_telemetry.IsOverlayVisible
        )
        {
            return preferred;
        }

        Rect telemetryRect =
            _telemetry.OverlayRect;

        // First choice: keep all right-side diagnostics in one clean vertical
        // column. At 1920x1080 and the 2560x1352 capture resolution, the frame
        // comparator fits directly under the 666px telemetry panel.
        float belowY =
            telemetryRect.yMax + gap;

        if (belowY + panelHeight <= Screen.height - margin)
        {
            return new Rect(
                Mathf.Max(margin, Screen.width - panelWidth - 18f),
                belowY,
                panelWidth,
                panelHeight);
        }

        // Short screens cannot stack vertically. Move the comparator completely
        // to the left of telemetry instead of drawing both into the same pixels.
        float leftOfTelemetryX =
            telemetryRect.x - gap - panelWidth;

        if (leftOfTelemetryX >= margin)
        {
            return new Rect(
                margin,
                margin,
                panelWidth,
                panelHeight);
        }

        // Extremely small windows cannot physically host both full panels.
        // Keep the comparator out of telemetry's rectangle by using the largest
        // left lane that exists. This is diagnostic-only and deliberately clips
        // before it overlaps another Kiwi-owned panel.
        float availableLeftWidth =
            Mathf.Max(0f, telemetryRect.x - gap - margin);

        return new Rect(
            margin,
            margin,
            Mathf.Max(1f, Mathf.Min(panelWidth, availableLeftWidth)),
            panelHeight);
    }


    private static float GetNativeHostTicksAgeMs(long hostTicks)
    {
        if (hostTicks <= 0L)
        {
            return -1f;
        }

        long nowTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();

        if (nowTicks < hostTicks)
        {
            return -1f;
        }

        return
            (float)(
                (nowTicks - hostTicks) *
                1000.0 /
                System.Diagnostics.Stopwatch.Frequency);
    }
    private float GetCanonicalObservationAgeMs()
    {
        if (
            !_hasCanonicalFrame ||
            !_canonicalFrame.normalization.valid ||
            _canonicalFrame.normalization.observationHostTicks <= 0L
        )
        {
            return -1f;
        }

        long nowTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();

        if (nowTicks < _canonicalFrame.normalization.observationHostTicks)
        {
            return -1f;
        }

        return
            (float)KiwiPrecisionTrackingMath.HostTicksToSeconds(
                nowTicks -
                _canonicalFrame.normalization.observationHostTicks) *
            1000f;
    }

    private void EnsureGuiStyles()
    {
        if (_headerStyle != null)
        {
            return;
        }

        _headerStyle = new GUIStyle(GUI.skin.label);
        _headerStyle.fontSize = 13;
        _headerStyle.fontStyle = FontStyle.Bold;
        _headerStyle.normal.textColor = Color.white;

        _bodyStyle = new GUIStyle(GUI.skin.label);
        _bodyStyle.fontSize = 12;
        _bodyStyle.normal.textColor = Color.white;

        _smallStyle = new GUIStyle(GUI.skin.label);
        _smallStyle.fontSize = 10;
        _smallStyle.normal.textColor =
            new Color(0.92f, 0.92f, 0.92f, 1f);
    }

    private void WriteCsvRow()
    {
        if (_csvWriter == null)
        {
            return;
        }

        try
        {
            Texture sourceTexture = GetSourceTexture();
            int sourceId =
                sourceTexture != null
                    ? sourceTexture.GetInstanceID()
                    : 0;
            int sourceWidth = sourceTexture != null ? sourceTexture.width : 0;
            int sourceHeight = sourceTexture != null ? sourceTexture.height : 0;

            KiwiRuntimeGenerationContext.Snapshot generation =
                _hasCanonicalFrame
                    ? _canonicalFrame.generation
                    : KiwiRuntimeGenerationContext.Capture();

            FacePrecisionTrackingData rigid =
                _hasCanonicalFrame
                    ? _canonicalFrame.rigid
                    : default;

            long nowTicks =
                System.Diagnostics.Stopwatch.GetTimestamp();

            float observationAgeMs =
                _hasCanonicalFrame &&
                _canonicalFrame.normalization.valid &&
                _canonicalFrame.normalization.observationHostTicks > 0L
                    ? (float)KiwiPrecisionTrackingMath.HostTicksToSeconds(
                        nowTicks -
                        _canonicalFrame.normalization.observationHostTicks) * 1000f
                    : -1f;

            float arrivalAgeMs =
                _hasCanonicalFrame &&
                _canonicalFrame.normalization.valid &&
                _canonicalFrame.normalization.arrivalHostTicks > 0L
                    ? (float)KiwiPrecisionTrackingMath.HostTicksToSeconds(
                        nowTicks -
                        _canonicalFrame.normalization.arrivalHostTicks) * 1000f
                    : -1f;

            Transform root =
                _faceMotion != null
                    ? _faceMotion.kiwiRoot
                    : null;

            Vector3 rootPosition = root != null ? root.localPosition : Vector3.zero;
            Vector3 rootEuler = root != null ? root.localRotation.eulerAngles : Vector3.zero;
            Vector3 rootScale = root != null ? root.localScale : Vector3.zero;

            float rootScaleDelta = debugRootScaleStep;

            StringBuilder row = new StringBuilder(768);

            Append(row, Time.frameCount); Sep(row);
            Append(row, Time.realtimeSinceStartupAsDouble); Sep(row);
            Append(row, _cameraFreshThisFrame); Sep(row);
            Append(row, sourceId); Sep(row);
            Append(row, sourceWidth); Sep(row);
            Append(row, sourceHeight); Sep(row);
            Append(row, _hasNativeCameraTelemetry); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.sessionGeneration : 0); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.latestCaptureSequence : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.latestPresentedSequence : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.captureFrameCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.droppedFrameCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.presentedFrameCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.producerCompletedFenceValue : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? GetNativeHostTicksAgeMs(_nativeCameraTelemetry.latestCaptureHostTicks) : -1f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? GetNativeHostTicksAgeMs(_nativeCameraTelemetry.latestPresentedHostTicks) : -1f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.targetFrameRate : 0); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.presentationTextureId : 0); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.latestSourceSequence : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.sourceFrameCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.supersededFrameCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.processingFailureFrameCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? GetNativeHostTicksAgeMs(_nativeCameraTelemetry.latestSourceHostTicks) : -1f); Sep(row);
            Append(row, _hasNativeCameraTelemetry && _nativeCameraTelemetry.processingWorkerRunning); Sep(row);
            Append(row, _hasNativeCameraTelemetry && _nativeCameraTelemetry.timestampCalibrationValid); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.nativeQpcFrequency : 0L); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.sourceIntervalMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.latestIngestSequence : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.ingestCopyFrameCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.ingestCopyFailureFrameCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? GetNativeHostTicksAgeMs(_nativeCameraTelemetry.latestIngestHostTicks) : -1f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.ingestCopySubmitMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.ingestCompletedFenceValue : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry && _nativeCameraTelemetry.d3d11MultithreadProtected); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.processingCpuMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.callbackCpuMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.requestNextCpuMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.callbackToRequestNextMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.sourceTimestampIntervalMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.arrivalIntervalMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.ingestConsumerCompletedFenceValue : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry && _nativeCameraTelemetry.captureDeviceIsolated); Sep(row);
            Append(row, _hasNativeCameraTelemetry && _nativeCameraTelemetry.captureD3D11MultithreadProtected); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.captureGpuWaitCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.processingGpuWaitCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.readyReplacementCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.allSlotsBusyDropCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.ingestAcceptedCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.ingestAcceptedRatio : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.readyUnclaimedCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.oldestReadyAgeMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.producerFenceLag : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.consumerFenceLag : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.latestProcessedSourceAgeMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.captureTransportId : 0); Sep(row);
            Append(row, _hasNativeCameraTelemetry && _nativeCameraTelemetry.systemMemoryCaptureEnabled); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.mfSampleResidenceMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.captureCopyGpuSubmitMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.captureCopyGpuCompletionMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.captureCopyGpuOutstandingDepth : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.cpuNv12CopyMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.cpuNv12CopyBytes : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.cpuLatestReplacementCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.cpuAllSlotsBusyDropCount : 0UL); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.gpuUploadSubmitMs : 0f); Sep(row);
            Append(row, _hasNativeCameraTelemetry ? _nativeCameraTelemetry.gpuUploadCount : 0UL); Sep(row);
            Append(row, KiwiFramePacingDiagnosticController.ModeId); Sep(row);
            Append(row, KiwiFramePacingDiagnosticController.VSyncCount); Sep(row);
            Append(row, KiwiFramePacingDiagnosticController.TargetFrameRate); Sep(row);
            Append(row, KiwiFramePacingDiagnosticController.RenderFrameInterval); Sep(row);
            Append(row, KiwiFramePacingDiagnosticController.EffectiveRenderFrameRate); Sep(row);
            Append(row, KiwiFramePacingDiagnosticController.WillCurrentFrameRender); Sep(row);
            Append(row, KiwiInferenceReadbackBoundaryDiagnostics.PreReadbackSubmitCpuMs); Sep(row);
            Append(row, KiwiInferenceReadbackBoundaryDiagnostics.ReadbackRequestCpuMs); Sep(row);
            Append(row, KiwiInferenceReadbackBoundaryDiagnostics.RequestToDoneObservedMs); Sep(row);
            Append(row, KiwiInferenceReadbackBoundaryDiagnostics.ReadbackObservedFrameDelta); Sep(row);
            Append(row, KiwiInferenceReadbackBoundaryDiagnostics.PollIntervalMs); Sep(row);
            Append(row, KiwiInferenceReadbackBoundaryDiagnostics.ReadbackCloneCpuMs); Sep(row);
            Append(row, KiwiInferenceReadbackBoundaryDiagnostics.DecodeMathCpuMs); Sep(row);
            Append(row, KiwiInferenceReadbackBoundaryDiagnostics.BoundarySampleCount); Sep(row);
            Append(row, KiwiInferenceBackendDiagnostics.BackendId); Sep(row);
            Append(row, KiwiInferenceBackendDiagnostics.CpuBackendEnabled); Sep(row);
            Append(row, KiwiInferenceFailureStageDiagnostics.CurrentStageId); Sep(row);
            Append(row, KiwiInferenceFailureStageDiagnostics.LastFailureStageId); Sep(row);
            Append(row, KiwiInferenceFailureStageDiagnostics.LastFailureKindId); Sep(row);
            Append(row, KiwiInferenceFailureStageDiagnostics.LastExceptionTypeId); Sep(row);
            Append(row, KiwiInferenceFailureStageDiagnostics.LastExceptionHResult); Sep(row);
            Append(row, KiwiInferenceFailureStageDiagnostics.AttemptCount); Sep(row);
            Append(row, KiwiInferenceFailureStageDiagnostics.SuccessCount); Sep(row);
            Append(row, KiwiInferenceFailureStageDiagnostics.FailureCount); Sep(row);
            Append(row, KiwiInferenceFailureStageDiagnostics.ExceptionCount); Sep(row);
            Append(row, KiwiInferenceFailureStageDiagnostics.NullOutputCount); Sep(row);
            Append(row, KiwiInferenceFailureStageDiagnostics.ShapeMismatchCount); Sep(row);
            Append(row, generation.cameraGeneration); Sep(row);
            Append(row, generation.providerGeneration); Sep(row);
            Append(row, generation.trackingSessionGeneration); Sep(row);
            AppendCsvString(row, _hasCanonicalFrame ? _canonicalFrame.providerId : string.Empty); Sep(row);
            Append(row, _hasCanonicalFrame ? _canonicalFrame.canonicalFrameId : 0UL); Sep(row);
            Append(row, rigid.frameId); Sep(row);
            Append(row, _hasCanonicalFrame ? rigid.timestamp : -1L); Sep(row);
            Append(row, _hasCanonicalFrame && _canonicalFrame.hasSemanticLandmarks ? _canonicalFrame.semanticTimestamp : -1L); Sep(row);
            Append(row, _hasCanonicalFrame && _canonicalFrame.hasSemanticLandmarks && _canonicalFrame.semanticTimestamp == rigid.timestamp); Sep(row);
            Append(row, _semanticChangedThisFrame); Sep(row);
            Append(row, _landmarkCount); Sep(row);
            Append(row, debugLandmarkMeanStep); Sep(row);
            Append(row, debugLandmarkMaxStep); Sep(row);
            Append(row, rigid.faceCenter.x); Sep(row);
            Append(row, rigid.faceCenter.y); Sep(row);
            Append(row, rigid.leftEyeCenter.x); Sep(row);
            Append(row, rigid.leftEyeCenter.y); Sep(row);
            Append(row, rigid.rightEyeCenter.x); Sep(row);
            Append(row, rigid.rightEyeCenter.y); Sep(row);
            Append(row, rigid.nose.x); Sep(row);
            Append(row, rigid.nose.y); Sep(row);
            Append(row, rigid.chin.x); Sep(row);
            Append(row, rigid.chin.y); Sep(row);
            Append(row, observationAgeMs); Sep(row);
            Append(row, arrivalAgeMs); Sep(row);
            Append(row, rootPosition.x); Sep(row);
            Append(row, rootPosition.y); Sep(row);
            Append(row, rootPosition.z); Sep(row);
            Append(row, rootEuler.x); Sep(row);
            Append(row, rootEuler.y); Sep(row);
            Append(row, rootEuler.z); Sep(row);
            Append(row, rootScale.x); Sep(row);
            Append(row, rootScale.y); Sep(row);
            Append(row, rootScale.z); Sep(row);
            Append(row, debugRootPositionStep); Sep(row);
            Append(row, debugRootRotationStep); Sep(row);
            Append(row, rootScaleDelta); Sep(row);
            Append(row, KiwiFrameContinuityDiagnostics.Enabled); Sep(row);
            Append(row, KiwiFrameContinuityDiagnostics.DirectBypassSuppressed); Sep(row);
            Append(row, KiwiFrameContinuityDiagnostics.DiscontinuityGuardActive); Sep(row);
            Append(row, KiwiFrameContinuityDiagnostics.TrackingRateHz); Sep(row);
            Append(row, KiwiFrameContinuityDiagnostics.EffectiveSampleInterval * 1000f); Sep(row);
            Append(row, KiwiFrameContinuityDiagnostics.ResponseCap); Sep(row);
            Append(row, _trackingHub != null && _trackingHub.HandoffActive); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.HandoffWeight : 0f); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.HandoffTargetWeight : 0f); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.HandoffReleaseStep : 0f); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.HandoffCenterOffsetMagnitude : 0f); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.HandoffRotationOffsetDegrees : 0f); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.HandoffScaleRatio : 1f); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.HandoffCount : 0); Sep(row);
            Append(row, _supervisor != null && _supervisor.CommercialCadenceBoostActive); Sep(row);
            Append(row, _supervisor != null ? _supervisor.CurrentRenderFps : 0f); Sep(row);
            Append(row, _supervisor != null ? _supervisor.CurrentAuxiliaryMediaPipeHz : 0f); Sep(row);
            // KIWI_V5_1_PHASE16_9_COMMERCIAL_PIPELINE_AND_FACEPART_DIAGNOSTICS
            Append(row, KiwiCommercialCadencePipelineTelemetry.FreshSourceRateHz); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.SubmissionRateHz); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.ResultRateHz); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.CanonicalAdoptionRateHz); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.ReadbackLatencyMs); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.SourceToSubmissionGapHz); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.SubmissionToResultGapHz); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.ResultToAdoptionGapHz); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.SubmissionEfficiency); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.ResultEfficiency); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.AdoptionEfficiency); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.AdoptionCount); Sep(row);
            // KIWI_V5_1_PHASE16_10_INFERENCE_PIPELINE_DIAGNOSTICS
            // KIWI_V5_1_PHASE16_11_FRESHNESS_AGE_DIAGNOSTICS
            // KIWI_V5_1_PHASE16_12_PERSISTENT_ROI_DIAGNOSTICS
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceTelemetryOperational); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferencePipelineDepth); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceLaneLimit); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceActiveLanes); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceOldestPendingAgeMs); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceLatencyMs); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceScheduleDelayMs); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceSourceToCompletionAgeMs); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceAcceptedSourceAgeMs); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRawPresenceLogit); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferencePresence); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceConsecutiveFailures); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceHasRegion); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceTrackingHealthy); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRegionRetentionActive); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRegionTrustedAgeMs); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRegionGraceRemainingMs); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRegionRecoveryScale); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRegionRetainedFailureCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRegionReleaseCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRegionCenterX); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRegionCenterY); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRegionWidth); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRegionHeight); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceScheduleCpuMs); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceGpuReadbackWaitMs); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceDecodeCpuMs); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceLatencyFirstSchedulingActive); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceStableDesktopSchedulingActive); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceCompletionIntervalMs); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceLanePromotionCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceLaneDemotionCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceSingleFlightProbeCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceScheduledCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceReadbackCompletedCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceCompletedCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceDroppedFreshCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRejectedPresenceCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceRejectedInvalidCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceDiscardedStaleCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceDiscardedCrossSystemCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceDiscardedStaleAnchorCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceDiscardedStaleGenerationCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceDiscardedStaleSourceCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceSoftAnchorUpdateCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceHardAnchorInvalidationCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceSoftAnchorSupersededRoiUpdateCount); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceDropRatio); Sep(row);
            Append(row, KiwiCommercialCadencePipelineTelemetry.InferenceCompletionRatio); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.CommercialRigidShockCount : 0); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.CommercialRigidLastCenterResidualEyeSpans : 0f); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.CommercialRigidLastRotationDelta : 0f); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.CommercialRigidLastDepthLogDelta : 0f); Sep(row);
            Append(row, KiwiCommercialFacePartPolicy.SemanticTopologyRejectCount); Sep(row);
            Append(row, KiwiCommercialFacePartPolicy.TransactionCanonicalFrameId); Sep(row);
            Append(row, KiwiRuntimeGenerationContext.SemanticTransactionSequence); Sep(row);
            Append(row, KiwiCommercialLandmarkRefiner.LastRefinedPointCount); Sep(row);
            Append(row, KiwiCommercialLandmarkRefiner.LastIsolatedPointRejectCount); Sep(row);
            Append(row, KiwiCommercialLandmarkRefiner.LastIsolatedCandidateCount); Sep(row);
            Append(row, KiwiCommercialLandmarkRefiner.LastMassRejectBypass); Sep(row);
            Append(row, KiwiCommercialLandmarkRefiner.TotalMassRejectBypasses); Sep(row);
            Append(row, KiwiCommercialLandmarkRefiner.LastMeanAdjustmentEyeSpans); Sep(row);
            Append(row, KiwiCommercialLandmarkRefiner.LastMaxAdjustmentEyeSpans); Sep(row);
            Append(row, KiwiCommercialLandmarkRefiner.LastGeometryQuality); Sep(row);
            Append(row, KiwiCommercialLandmarkRefiner.LastRefinedTimestamp); Sep(row);
            Append(row, _trackingHub != null && _trackingHub.CommercialFailoverDeferred); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.CommercialFailoverGraceRemainingMilliseconds : 0f); Sep(row);
            Append(row, _trackingHub != null ? _trackingHub.CommercialDeferredFailoverCount : 0); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.LastCompletedFrame); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.RootUpdateToLatePositionDelta); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.RootUpdateToLateRotationDelta); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.RootUpdateToLateScaleDelta); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.RootLateToRenderPositionDelta); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.RootLateToRenderRotationDelta); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.RootLateToRenderScaleDelta); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.VisualRelativeUpdateToLatePositionDelta); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.VisualRelativeUpdateToLateRotationDelta); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.VisualRelativeUpdateToLateScaleDelta); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.VisualRelativeLateToRenderPositionDelta); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.VisualRelativeLateToRenderRotationDelta); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.VisualRelativeLateToRenderScaleDelta); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.RenderBoundaryObserved); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.VisualMovedWithoutRoot); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.RootMovedAfterLate); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.VisualMovedWithoutRootCount); Sep(row);
            Append(row, KiwiCommercialTransformWriterAudit.RootMovedAfterLateCount); Sep(row);
            Append(row, KiwiPhase16_13PresentationDiagnostics.StaticRestActive); Sep(row);
            Append(row, KiwiPhase16_13PresentationDiagnostics.StaticRestCandidateSeconds); Sep(row);
            Append(row, KiwiPhase16_13PresentationDiagnostics.StaticRestLockCount); Sep(row);
            Append(row, KiwiPhase16_13PresentationDiagnostics.StaticRestReleaseCount); Sep(row);
            Append(row, KiwiPhase16_13PresentationDiagnostics.BeforeRenderRestHoldCount); Sep(row);
            Append(row, KiwiPhase16_13PresentationDiagnostics.BeforeRenderNewSampleCount); Sep(row);
            Append(row, KiwiPhase16_13PresentationDiagnostics.BeforeRenderFreshOnlyPolicyActive); Sep(row);
            Append(row, KiwiPhase16_13PresentationDiagnostics.BeforeRenderSameSampleSkipCount); Sep(row);
            Append(row, KiwiPhase16_13PresentationDiagnostics.BeforeRenderAcceptedNewSampleCount); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootModelHeight); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootRawTargetPositionDelta); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootRawTargetRotationDelta); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootRawTargetScaleDelta); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootDisplayPreCapPositionDelta); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootDisplayPreCapRotationDelta); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootDisplayPreCapScaleDelta); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootDisplayPostCapPositionDelta); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootDisplayPostCapRotationDelta); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootDisplayPostCapScaleDelta); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootContinuityMaxPositionStep); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootContinuityMaxRotationStep); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootContinuityMaxScaleStep); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootContinuityCapApplied); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootPredictionPositionDelta); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootPredictionLeadMs); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.RootCorrectionBacklog); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.AuthoritativeFrameMissing); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.NoFrameHoldActive); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.NoFrameHoldCount); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.SameProviderResumeActive); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.SameProviderResumeCount); Sep(row);
            Append(row, KiwiPhase16_15RootContinuityDiagnostics.SameProviderResumeSamplesRemaining); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.Active); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.Count); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.ProviderGeneration); Sep(row);
            Append(row, (int)KiwiPhase16_16ProviderBridgeDiagnostics.Backend); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.Weight); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.TargetWeight); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.ReleaseStep); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.AcceptedSamples); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.MotionProgress); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.PositionOffset); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.RotationOffsetDegrees); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.ScaleRatio); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.AppliedPositionDelta); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.AppliedRotationDeltaDegrees); Sep(row);
            Append(row, KiwiPhase16_16ProviderBridgeDiagnostics.AppliedScaleDelta); Sep(row);
            Append(row, KiwiPhase16_17PresentationAuthorityDiagnostics.SinglePresentationAuthority); Sep(row);
            Append(row, KiwiPhase16_17PresentationAuthorityDiagnostics.Quality10PolicyOnlyActive); Sep(row);
            Append(row, KiwiPhase16_17PresentationAuthorityDiagnostics.SharedRootBinding); Sep(row);
            Append(row, KiwiPhase16_17PresentationAuthorityDiagnostics.SuppressedLateUpdateCount); Sep(row);
            Append(row, KiwiPhase16_17PresentationAuthorityDiagnostics.SuppressedBeforeRenderCount); Sep(row);
            Append(row, KiwiPhase16_17PresentationAuthorityDiagnostics.LegacyRootWriteCount); Sep(row);
            Append(row, KiwiPhase16_17PresentationAuthorityDiagnostics.LegacyWriteViolationCount); Sep(row);
            Append(row, KiwiPhase16_17PresentationAuthorityDiagnostics.FaceMotionDisplayRateSmoothing); Sep(row);
            Append(row, KiwiPhase16_17PresentationAuthorityDiagnostics.FaceMotionStaticRestEnabled); Sep(row);
            Append(row, KiwiPhase16_17PresentationAuthorityDiagnostics.FaceMotionAdaptiveMicroFilter); Sep(row);
            Append(row, KiwiPhase16_17PresentationAuthorityDiagnostics.FaceMotionPredictionDisabled); Sep(row);
            Append(row, KiwiPhase16_18HandoffAuthorityDiagnostics.SingleHandoffAuthority); Sep(row);
            Append(row, KiwiPhase16_18HandoffAuthorityDiagnostics.CanonicalNormalizationEnabled); Sep(row);
            Append(row, KiwiPhase16_18HandoffAuthorityDiagnostics.CanonicalHandoffActive); Sep(row);
            Append(row, KiwiPhase16_18HandoffAuthorityDiagnostics.CanonicalHandoffIsResume); Sep(row);
            Append(row, KiwiPhase16_18HandoffAuthorityDiagnostics.LocalRootProviderBridgeActive); Sep(row);
            Append(row, KiwiPhase16_18HandoffAuthorityDiagnostics.LocalBridgeSuppressedCount); Sep(row);
            Append(row, KiwiPhase16_18HandoffAuthorityDiagnostics.AuthorityViolationCount); Sep(row);
            Append(row, KiwiPhase16_18HandoffAuthorityDiagnostics.HubEnvelopeGuardActivationCount); Sep(row);
            Append(row, KiwiPhase16_19FacePartEpochDiagnostics.StrictPresentationEpochActive); Sep(row);
            Append(row, KiwiPhase16_19FacePartEpochDiagnostics.PredictionDisabled); Sep(row);
            Append(row, KiwiPhase16_19FacePartEpochDiagnostics.MatchedAgeCompensationDisabled); Sep(row);
            Append(row, KiwiPhase16_19FacePartEpochDiagnostics.DirectMotionDisabled); Sep(row);
            Append(row, KiwiPhase16_19FacePartEpochDiagnostics.LiveResidualDisabled); Sep(row);
            Append(row, KiwiPhase16_19FacePartEpochDiagnostics.ContractAligned); Sep(row);
            Append(row, KiwiPhase16_19FacePartEpochDiagnostics.ViolationCount); Sep(row);
            Append(row, KiwiPhase16_19FacePartEpochDiagnostics.SemanticTimestamp); Sep(row);
            Append(row, KiwiPhase16_19FacePartEpochDiagnostics.TextureCanonicalFrameId); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.IsOperational); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.SceneBindingValid); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.StrictPresentationStarted); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.LastCommittedSemanticTimestamp); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.LastCommittedCanonicalFrameId); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.LastMatchDeltaMs); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.BufferedFrameCount); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.CaptureCount); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.TransactionCommitCount); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.TransactionMissCount); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.SemanticHoldCount); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.ExternalTextureWriterDetected); Sep(row);
            Append(row, KiwiFacePartTextureTransaction.ExternalTextureWriterCount); Sep(row);
            Append(
                row,
                _cameraFreshThisFrame &&
                !_semanticChangedThisFrame &&
                _cropper != null &&
                _cropper.sourceImage != null &&
                (
                    _cropper.sourceImage.texture is WebCamTexture ||
                    _hasNativeCameraTelemetry
                ));

            _csvWriter.WriteLine(row.ToString());
            recordedFrameCount++;
            _framesSinceCsvFlush++;

            if (_framesSinceCsvFlush >= CsvFlushIntervalFrames)
            {
                _csvWriter.Flush();
                _framesSinceCsvFlush = 0;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Frame comparison CSV write failed; " +
                "recording was stopped. " +
                ex.Message);
            StopCsvRecording();
        }
    }

    private static void Sep(StringBuilder b)
    {
        b.Append(',');
    }

    private static void Append(StringBuilder b, bool value)
    {
        b.Append(value ? "1" : "0");
    }

    private static void Append(StringBuilder b, int value)
    {
        b.Append(value.ToString(Invariant));
    }

    private static void Append(StringBuilder b, long value)
    {
        b.Append(value.ToString(Invariant));
    }

    private static void Append(StringBuilder b, ulong value)
    {
        b.Append(value.ToString(Invariant));
    }

    private static void Append(StringBuilder b, float value)
    {
        b.Append(value.ToString("R", Invariant));
    }

    private static void Append(StringBuilder b, double value)
    {
        b.Append(value.ToString("R", Invariant));
    }

    private static void AppendCsvString(StringBuilder b, string value)
    {
        string safe = value ?? string.Empty;
        b.Append('"');
        b.Append(safe.Replace("\"", "\"\""));
        b.Append('"');
    }
}
