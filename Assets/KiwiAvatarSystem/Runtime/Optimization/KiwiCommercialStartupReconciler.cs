using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// v5.0 startup ownership reconciler.
///
/// KiwiAvatarRuntimePanel still contains the old one-time PlayerPrefs loader
/// because that loader restores validated face-part / presentation compatibility
/// settings that are not owned by the v4.4 Commercial Profile. The v4.5.3
/// migration disabled that loader too broadly and could leave eye/mouth
/// presentation at scene defaults.
///
/// This component waits until that one-time compatibility load has completed,
/// then reapplies the Commercial Profile exactly once. No steady-state writer is
/// added: after startup the Commercial Profile / Latency Budget / Quality
/// Governor remain the authoritative control plane.
/// </summary>
[DefaultExecutionOrder(650)]
[DisallowMultipleComponent]
public sealed class KiwiCommercialStartupReconciler : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Commercial Startup Reconciler";

    private const double RetryIntervalSeconds =
        0.10;

    private static readonly FieldInfo TrackingSettingsLoadedField =
        typeof(KiwiAvatarRuntimePanel).GetField(
            "_trackingSettingsLoaded",
            BindingFlags.Instance |
            BindingFlags.NonPublic);

    private KiwiCommercialProfileController _profile;
    private KiwiAvatarRuntimePanel _legacyPanel;
    private KiwiFaceMotion _faceMotion;
    private KiwiTrackingProviderHub _trackingHub;
    private KiwiTrackingContinuityState _trackingContinuity;
    private KiwiMatureVTuberSupervisor _matureSupervisor;
    private KiwiFaceAttachmentRecalibration _attachmentRecalibration;
    private FacePartCropper _facePartCropper;
    private FacePartShapeMask[] _facePartShapeMasks;

    private bool _reconciled;
    private double _nextRetryRealtime;

    public bool Reconciled =>
        _reconciled;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiCommercialStartupReconciler>(
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
            KiwiCommercialStartupReconciler>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(
            gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        ResetForScene();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -=
            HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        ResetForScene();
    }

    private void ResetForScene()
    {
        _profile = null;
        _legacyPanel = null;
        _faceMotion = null;
        _trackingHub = null;
        _trackingContinuity = null;
        _matureSupervisor = null;
        _attachmentRecalibration = null;
        _facePartCropper = null;
        _facePartShapeMasks = null;
        _reconciled = false;
        _nextRetryRealtime = 0.0;
    }

    private void LateUpdate()
    {
        if (_reconciled)
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        if (now < _nextRetryRealtime)
        {
            return;
        }

        _nextRetryRealtime =
            now +
            RetryIntervalSeconds;

        if (_profile == null)
        {
            _profile =
                FindFirstObjectByType<
                    KiwiCommercialProfileController>(
                    FindObjectsInactive.Include);
        }

        if (_profile == null)
        {
            return;
        }

        if (_legacyPanel == null)
        {
            _legacyPanel =
                FindFirstObjectByType<
                    KiwiAvatarRuntimePanel>(
                    FindObjectsInactive.Include);
        }

        if (!LegacyCompatibilityLoadCompleted())
        {
            return;
        }

        // Reassert only the v4.4+ Commercial Profile-owned fields after the
        // legacy compatibility loader has restored the remaining presentation
        // settings. This is a one-shot startup reconciliation, not a second
        // steady-state owner.
        _profile.ApplyCurrentProfile();

        ApplyCommercialRigidPoseContract();
        ApplyCommercialTrackingContinuityContract();

        _reconciled = true;
    }

    /// <summary>
    /// v4.5.5 commercial rigid-pose safety contract.
    ///
    /// The legacy PlayerPrefs loader is retained only because it also restores
    /// validated eye/mouth presentation settings. Old keys must not be allowed
    /// to re-enable idle/reaction body animation or disable the microscopic
    /// rest lock after that compatibility load.
    ///
    /// This is applied once per scene before KiwiFaceMotion LateUpdate. It is
    /// not a per-frame transform writer and does not touch eye/mouth -> root.
    /// </summary>
    private void ApplyCommercialRigidPoseContract()
    {
        if (_faceMotion == null)
        {
            _faceMotion =
                FindFirstObjectByType<KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }

        if (_faceMotion == null)
        {
            return;
        }

        _faceMotion.enableHybridPrecisionTracking = true;
        _faceMotion.enableUltraLowLatencyTracking = true;
        _faceMotion.landMarkerSpeedMode = true;
        _faceMotion.useBoundedLatestResultCorrection = true;

        // KIWI_V5_1_PHASE16_17_SINGLE_PRESENTATION_AUTHORITY_CONTRACT
        // KiwiFaceMotion is the single rigid Root temporal presenter.
        // Keep only the final spatial rest latch; do not stack the older
        // sample-domain micro hold underneath it.
        _faceMotion.useBeforeRenderLateLatch = true;
        _faceMotion.ultraConsumeLatestSampleBeforeRender = true;
        _faceMotion.ultraUseRunnerPositionAnchor = true;
        _faceMotion.ultraAdaptiveMicroFilter = false;
        _faceMotion.ultraStaticPoseLock = true;

        // One display-rate resampler lives inside the Root owner.
        _faceMotion.ultraDisplayRateSmoothing = true;
        _faceMotion.ultraDirectDisplayDuringMotion = true;
        _faceMotion.ultraPredictivePositionResampling = false;

        // Quality10 no longer owns Root temporal prediction in Phase16.17.
        // Keep FaceMotion's duplicate long-horizon predictors disabled too;
        // source freshness and provider continuity remain separate concerns.
        _faceMotion.ultraPredictionStrength = 0f;
        _faceMotion.ultraCompensateFullResultAge = false;
        _faceMotion.ultraCompensateCameraCaptureAge = false;
        _faceMotion.enableRenderTimeLatePrediction = false;
        _faceMotion.predictionStrength = 0f;

        // Critical: expression/idle animation remains a presentation option,
        // but it must never move the rigid tracking root in the commercial core.
        _faceMotion.ultraDisableSecondaryBodyMotion = true;

        ApplyCommercialFacePartContract();
    }

    private void ApplyCommercialTrackingContinuityContract()
    {
        if (_trackingHub == null)
        {
            _trackingHub =
                FindFirstObjectByType<KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }

        if (_trackingHub != null)
        {
            // KIWI_V5_1_PHASE16_18_SINGLE_HANDOFF_AUTHORITY_CONTRACT
            // Canonical provider-space normalization is the one handoff
            // coordinate owner. KiwiFaceMotion may keep only a final bounded
            // safety envelope; it must not independently normalize the same
            // provider transition.
            _trackingHub.enableProviderHandoffNormalization = true;

            // KIWI_V5_0_NATURAL_CONTINUITY_CONTRACT
            // Keep Inference as the rigid geometry owner while it is still
            // delivering frames. End-to-end latency lowers quality/prediction,
            // but only result-arrival silence is treated as a short dropout.
            _trackingHub.maximumProviderFrameAge = 0.45f;
            _trackingHub.handoffReferenceMaximumAge = 0.45f;
            _trackingHub.resumeHandoffMaximumGapSeconds = 0.45f;
            _trackingHub.resumeHandoffReleaseFrames = 3;
            _trackingHub.minimumArrivalFreshnessSeconds = 0.10f;
            _trackingHub.arrivalFreshnessIntervalMultiplier = 2.8f;
            _trackingHub.maximumArrivalFreshnessSeconds = 0.22f;
            _trackingHub.sourceAgeScoreFullSeconds = 0.35f;
            _trackingHub.minimumProviderGeometryQuality = 0.20f;
            _trackingHub.providerSwitchScoreMargin = 0.14f;
            _trackingHub.providerSwitchConfirmationFrames = 3;
            _trackingHub.minimumProviderHoldSeconds = 0.45f;
            _trackingHub.freshnessScoreWeight = 0.18f;
            _trackingHub.cadenceScoreWeight = 0.12f;

            // KIWI_V5_1_PHASE16_8_COMMERCIAL_STICKY_RIGID_AUTHORITY
            // A short inference cadence miss becomes Holding instead of a
            // backend switch. This preserves one rigid coordinate authority
            // while still refusing to publish stale observations.
            _trackingHub.enableCommercialStickyRigidAuthority = true;
            _trackingHub.commercialTransientFailoverGraceSeconds = 0.60f;
            _trackingHub.commercialFailoverSourceAgeCeilingSeconds = 0.80f;
            _trackingHub.commercialMinimumAuthorityDwellSeconds = 1.00f;
            _trackingHub.commercialPrimaryRecoveryConfirmationFrames = 4;
        }

        if (_trackingContinuity == null)
        {
            _trackingContinuity =
                FindFirstObjectByType<KiwiTrackingContinuityState>(
                    FindObjectsInactive.Include);
        }

        if (_matureSupervisor == null)
        {
            _matureSupervisor =
                FindFirstObjectByType<KiwiMatureVTuberSupervisor>(
                    FindObjectsInactive.Include);
        }

        if (_matureSupervisor != null)
        {
            _matureSupervisor.enableCommercialCadenceBoost = true;
            _matureSupervisor.commercialCadenceMinimumRenderFps = 34f;
            _matureSupervisor.commercialCadenceDisableRenderFps = 30f;
            _matureSupervisor.commercialCadenceHealthyTargetHz = 12f;
            _matureSupervisor.commercialCadenceAgedTargetHz = 15f;
        }

        if (_attachmentRecalibration == null)
        {
            _attachmentRecalibration =
                FindFirstObjectByType<KiwiFaceAttachmentRecalibration>(
                    FindObjectsInactive.Include);
        }

        if (_attachmentRecalibration != null)
        {
            _attachmentRecalibration.ignoreBuiltInProviderSwitchForAttachmentCalibration = true;
            _attachmentRecalibration.minimumLossSecondsForAttachmentRecalibration = 0.60f;
        }

        if (_trackingContinuity != null)
        {
            _trackingContinuity.freshIntervalMultiplier = 2.4f;
            _trackingContinuity.minimumFreshAgeSeconds = 0.08f;
            _trackingContinuity.maximumFreshAgeSeconds = 0.18f;
            _trackingContinuity.degradedMaximumAgeSeconds = 0.24f;
            _trackingContinuity.lostAgeSeconds = 0.65f;
            _trackingContinuity.maximumStableSourceAgeSeconds = 0.26f;
            _trackingContinuity.maximumUsableSourceAgeSeconds = 0.45f;
            _trackingContinuity.shortHoldResumeSeconds = 0.22f;
        }
    }

    private void ApplyCommercialFacePartContract()
    {
        if (_facePartCropper == null)
        {
            _facePartCropper =
                FindFirstObjectByType<FacePartCropper>(
                    FindObjectsInactive.Include);
        }

        if (_facePartCropper != null)
        {
            // v4.8: prediction is compensation, not a permanent extrapolated
            // pose. Keep the existing predictor but bound it to the measured
            // commercial live window observed in the supplied recording.
            _facePartCropper.enablePrediction = true;
            _facePartCropper.compensateMatchedFrameAge = true;
            _facePartCropper.directPositionDuringMotion = true;
            _facePartCropper.maxExtrapolationSeconds =
                Mathf.Min(
                    _facePartCropper.maxExtrapolationSeconds,
                    0.050f);
            _facePartCropper.maxPredictionDistance =
                Mathf.Min(
                    _facePartCropper.maxPredictionDistance,
                    0.0035f);

            // A short semantic stall must hold the last trusted crop rather
            // than blanking the eye/mouth renderer.
            _facePartCropper.hidePartsWhenLost = false;
        }

        if (
            _facePartShapeMasks == null ||
            _facePartShapeMasks.Length == 0
        )
        {
            _facePartShapeMasks =
                FindObjectsByType<FacePartShapeMask>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
        }

        if (_facePartShapeMasks == null)
        {
            return;
        }

        for (int i = 0; i < _facePartShapeMasks.Length; i++)
        {
            FacePartShapeMask mask =
                _facePartShapeMasks[i];

            if (mask == null)
            {
                continue;
            }

            // The semantic contour is solved in the moving presentation crop
            // basis. This is required by the v4.8 mask-coherence migration.
            mask.lockContourToMovingCrop = true;
        }
    }

    private bool LegacyCompatibilityLoadCompleted()
    {
        if (_legacyPanel == null)
        {
            return true;
        }

        if (TrackingSettingsLoadedField == null)
        {
            // Fail safe for a future canonical RuntimePanel that removes the
            // private compatibility flag entirely. In that version there is
            // nothing left to wait for.
            return true;
        }

        object value =
            TrackingSettingsLoadedField.GetValue(
                _legacyPanel);

        return
            value is bool loaded &&
            loaded;
    }
}
