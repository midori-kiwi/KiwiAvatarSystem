using System;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

[DefaultExecutionOrder(31500)]
[DisallowMultipleComponent]
public sealed class KiwiFaceAttachmentRecalibration : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Face Attachment Recalibration";

    private const string RecoveryDomainSource =
        "FaceAttachmentRecalibration";

    public bool recalibrateAfterProviderChange = true;
    public bool recalibrateAfterTrackingLoss = true;

    // KIWI_V5_1_PHASE16_8_ATTACHMENT_STABILITY
    [Tooltip("Built-in MediaPipe/InferenceEngine handoffs are normalized into one canonical solve space, so they must not recalibrate model attachments by themselves.")]
    public bool ignoreBuiltInProviderSwitchForAttachmentCalibration = true;

    [Tooltip("A short Holding gap freezes presentation but does not redefine anatomy. Recalibrate after tracking loss only when the gap persisted for at least this long.")]
    [Range(0.20f, 1.50f)]
    public float minimumLossSecondsForAttachmentRecalibration = 0.60f;

    [Range(1, 8)]
    public int stableFreshFramesRequired = 3;

    [Range(0.05f, 0.80f)]
    public float minimumStableSeconds = 0.18f;

    [Range(3f, 25f)]
    public float maximumNeutralYawDegrees = 10f;

    [Range(0.5f, 10f)]
    public float minimumSecondsBetweenRecalibrations = 2.0f;

    [SerializeField] private bool debugPending;
    [SerializeField] private string debugReason = "-";
    [SerializeField] private int debugStableFreshFrames;
    [SerializeField] private int debugRecalibrationCount;
    [SerializeField] private int debugCalibrationGeneration;
    [SerializeField] private int debugPendingCalibrationGeneration;

    private KiwiTrackingContinuityState _continuity;
    private KiwiTrackingProviderHub _hub;
    private KiwiFaceMotion _faceMotion;
    private KiwiFacePartSharedTiltLock _tiltLock;
    private KiwiFacePartRigidCenterLock _rigidCenter;

    private string _lastProvider =
        string.Empty;

    private KiwiTrackingContinuityState.ContinuityState _lastContinuity =
        KiwiTrackingContinuityState.ContinuityState.Starting;

    private bool _pending;
    private string _pendingReason =
        string.Empty;

    private int _pendingCalibrationGeneration;

    private ulong _lastObservedFrameId;
    private int _stableFreshFrames;
    private double _stableStartedRealtime;
    private double _lastRecalibrationRealtime =
        -1000.0;
    private double _lossStartedRealtime = -1.0;

    public bool IsPending =>
        _pending;

    public int RecalibrationCount =>
        debugRecalibrationCount;

    public int PendingCalibrationGeneration =>
        _pendingCalibrationGeneration;

    public string PendingReason =>
        string.IsNullOrEmpty(
            _pendingReason)
                ? "-"
                : _pendingReason;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiFaceAttachmentRecalibration>(
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
            KiwiFaceAttachmentRecalibration>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(
            gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        RefreshReferences(true);
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        KiwiRecoveryDomainCoordinator.CompleteSemanticRecovery(
            KiwiRecoveryDomainCoordinator.SemanticComponent.Attachment,
            RecoveryDomainSource);
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _continuity = null;
        _hub = null;
        _faceMotion = null;
        _tiltLock = null;
        _rigidCenter = null;

        _lastProvider =
            string.Empty;

        _lastContinuity =
            KiwiTrackingContinuityState.ContinuityState.Starting;

        KiwiRecoveryDomainCoordinator.CompleteSemanticRecovery(
            KiwiRecoveryDomainCoordinator.SemanticComponent.Attachment,
            RecoveryDomainSource);

        _pending =
            false;

        _pendingReason =
            string.Empty;

        _pendingCalibrationGeneration =
            0;

        _lastObservedFrameId =
            0UL;

        _stableFreshFrames =
            0;

        _stableStartedRealtime =
            0.0;

        _lossStartedRealtime = -1.0;

        RefreshReferences(true);
    }

    private void LateUpdate()
    {
        RefreshReferences(false);

        if (
            _continuity == null ||
            _hub == null
        )
        {
            return;
        }

        ObserveDiscontinuities();

        if (_pending)
        {
            TryCompletePendingRecalibration();
        }

        debugPending =
            _pending;

        debugReason =
            string.IsNullOrEmpty(
                _pendingReason)
                ? "-"
                : _pendingReason;

        debugStableFreshFrames =
            _stableFreshFrames;

        debugCalibrationGeneration =
            KiwiRuntimeGenerationContext.CalibrationGeneration;

        debugPendingCalibrationGeneration =
            _pendingCalibrationGeneration;
    }

    private void ObserveDiscontinuities()
    {
        string provider =
            _continuity.ProviderId;

        bool providerChanged =
            !string.IsNullOrEmpty(
                _lastProvider) &&
            !string.IsNullOrEmpty(
                provider) &&
            !string.Equals(
                _lastProvider,
                provider,
                StringComparison.Ordinal);

        bool builtInProviderSwitch =
            providerChanged &&
            IsBuiltInRunnerProvider(_lastProvider) &&
            IsBuiltInRunnerProvider(provider);

        if (
            providerChanged &&
            recalibrateAfterProviderChange &&
            !(
                ignoreBuiltInProviderSwitchForAttachmentCalibration &&
                builtInProviderSwitch
            )
        )
        {
            RequestRecalibration(
                "ProviderSwitch");
        }

        KiwiTrackingContinuityState.ContinuityState current =
            _continuity.State;

        bool currentlyMissing =
            current ==
                KiwiTrackingContinuityState.ContinuityState.Holding ||
            current ==
                KiwiTrackingContinuityState.ContinuityState.Lost;

        if (currentlyMissing)
        {
            if (_lossStartedRealtime < 0.0)
            {
                _lossStartedRealtime =
                    Time.realtimeSinceStartupAsDouble;
            }
        }

        bool recoveredFromLoss =
            (
                _lastContinuity ==
                    KiwiTrackingContinuityState.ContinuityState.Holding ||
                _lastContinuity ==
                    KiwiTrackingContinuityState.ContinuityState.Lost
            ) &&
            (
                current ==
                    KiwiTrackingContinuityState.ContinuityState.Reacquiring ||
                current ==
                    KiwiTrackingContinuityState.ContinuityState.Stable
            );

        if (recoveredFromLoss)
        {
            double lossSeconds =
                _lossStartedRealtime >= 0.0
                    ? Time.realtimeSinceStartupAsDouble -
                        _lossStartedRealtime
                    : 0.0;

            if (
                recalibrateAfterTrackingLoss &&
                lossSeconds >=
                    Mathf.Max(
                        0.20f,
                        minimumLossSecondsForAttachmentRecalibration)
            )
            {
                RequestRecalibration(
                    "Reacquisition");
            }

            _lossStartedRealtime = -1.0;
        }
        else if (!currentlyMissing)
        {
            _lossStartedRealtime = -1.0;
        }

        if (!string.IsNullOrEmpty(provider))
        {
            _lastProvider =
                provider;
        }

        _lastContinuity =
            current;
    }

    private static bool IsBuiltInRunnerProvider(string providerId)
    {
        return
            string.Equals(
                providerId,
                "Runner/MediaPipe",
                StringComparison.Ordinal) ||
            string.Equals(
                providerId,
                "Runner/InferenceEngine",
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Model/provider lifecycle owners can request attachment recalibration
    /// without becoming a second Transform owner. When called inside a parent
    /// calibration transaction this component joins the existing generation.
    /// </summary>
    public void RequestLifecycleRecalibration(
        string reason,
        bool ignoreCooldown = false)
    {
        RequestRecalibration(
            reason,
            ignoreCooldown);
    }

    private void RequestRecalibration(
        string reason,
        bool ignoreCooldown = false)
    {
        if (_pending)
        {
            // A newer attachment-domain generation supersedes the pending
            // collection. Keep one pending operation and restart its evidence.
            if (
                KiwiCalibrationGeneration.HasScopeChangedSince(
                    _pendingCalibrationGeneration,
                    KiwiCalibrationScope.Attachments))
            {
                _pendingCalibrationGeneration =
                    KiwiCalibrationGeneration.CurrentGeneration;
                _stableFreshFrames = 0;
                _stableStartedRealtime = 0.0;
                _lastObservedFrameId = 0UL;
            }

            if (
                !string.IsNullOrEmpty(reason) &&
                _pendingReason.IndexOf(
                    reason,
                    StringComparison.Ordinal) < 0)
            {
                _pendingReason =
                    string.IsNullOrEmpty(_pendingReason)
                        ? reason
                        : _pendingReason + "+" + reason;
            }

            KiwiRecoveryDomainCoordinator.BeginSemanticRecovery(
                KiwiRecoveryDomainCoordinator.SemanticComponent.Attachment,
                KiwiRecoveryDomainCoordinator.SemanticRecoveryReason
                    .AttachmentRebind,
                RecoveryDomainSource);

            return;
        }

        if (
            !ignoreCooldown &&
            Time.realtimeSinceStartupAsDouble -
                _lastRecalibrationRealtime <
            minimumSecondsBetweenRecalibrations
        )
        {
            return;
        }

        _pendingCalibrationGeneration =
            KiwiCalibrationGeneration.BeginOrJoin(
                KiwiCalibrationScope.Attachments,
                "Attachment:" + reason);

        _pending =
            true;

        KiwiRecoveryDomainCoordinator.BeginSemanticRecovery(
            KiwiRecoveryDomainCoordinator.SemanticComponent.Attachment,
            KiwiRecoveryDomainCoordinator.SemanticRecoveryReason
                .AttachmentRebind,
            RecoveryDomainSource);

        _pendingReason =
            reason;

        _stableFreshFrames =
            0;

        _stableStartedRealtime =
            0.0;

        _lastObservedFrameId =
            0UL;
    }

    private void TryCompletePendingRecalibration()
    {
        if (_pendingCalibrationGeneration <= 0)
        {
            _pendingCalibrationGeneration =
                KiwiCalibrationGeneration.BeginOrJoin(
                    KiwiCalibrationScope.Attachments,
                    "Attachment:" + _pendingReason);
        }
        else if (
            KiwiCalibrationGeneration.HasScopeChangedSince(
                _pendingCalibrationGeneration,
                KiwiCalibrationScope.Attachments))
        {
            _pendingCalibrationGeneration =
                KiwiCalibrationGeneration.CurrentGeneration;
            _stableFreshFrames = 0;
            _stableStartedRealtime = 0.0;
            _lastObservedFrameId = 0UL;
            return;
        }

        if (
            _continuity.State !=
                KiwiTrackingContinuityState.ContinuityState.Stable
        )
        {
            _stableFreshFrames =
                0;

            _stableStartedRealtime =
                0.0;

            return;
        }

        if (
            _faceMotion != null &&
            Mathf.Abs(
                _faceMotion.RenderedYawDegrees) >
                maximumNeutralYawDegrees
        )
        {
            _stableFreshFrames =
                0;

            _stableStartedRealtime =
                0.0;

            return;
        }

        if (
            !_hub.TryGetLatestFrame(
                out FacePrecisionTrackingData data,
                out _)
        )
        {
            return;
        }

        if (
            data.frameId !=
                0UL &&
            data.frameId !=
                _lastObservedFrameId
        )
        {
            _lastObservedFrameId =
                data.frameId;

            _stableFreshFrames++;

            if (
                _stableStartedRealtime <=
                0.0
            )
            {
                _stableStartedRealtime =
                    Time.realtimeSinceStartupAsDouble;
            }
        }

        if (
            _stableFreshFrames <
                Mathf.Max(
                    1,
                    stableFreshFramesRequired)
        )
        {
            return;
        }

        if (
            Time.realtimeSinceStartupAsDouble -
                _stableStartedRealtime <
            minimumStableSeconds
        )
        {
            return;
        }

        if (
            !KiwiCalibrationGeneration.TryRecordCommit(
                nameof(KiwiFaceAttachmentRecalibration),
                _pendingCalibrationGeneration,
                KiwiCalibrationScope.Attachments))
        {
            _pendingCalibrationGeneration =
                KiwiCalibrationGeneration.CurrentGeneration;
            _stableFreshFrames = 0;
            _stableStartedRealtime = 0.0;
            _lastObservedFrameId = 0UL;
            return;
        }

        if (_tiltLock != null)
        {
            _tiltLock.Recalibrate();
        }

        if (_rigidCenter != null)
        {
            _rigidCenter.Recalibrate();
        }

        _pending =
            false;

        KiwiRecoveryDomainCoordinator.CompleteSemanticRecovery(
            KiwiRecoveryDomainCoordinator.SemanticComponent.Attachment,
            RecoveryDomainSource);

        _pendingCalibrationGeneration =
            0;

        _lastRecalibrationRealtime =
            Time.realtimeSinceStartupAsDouble;

        debugRecalibrationCount++;

        _pendingReason =
            string.Empty;

        _stableFreshFrames =
            0;

        _stableStartedRealtime =
            0.0;
    }

    private void RefreshReferences(
        bool force)
    {
        if (
            force ||
            _continuity == null
        )
        {
            _continuity =
                FindFirstObjectByType<
                    KiwiTrackingContinuityState>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _hub == null
        )
        {
            _hub =
                FindFirstObjectByType<
                    KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _faceMotion == null
        )
        {
            _faceMotion =
                FindFirstObjectByType<
                    KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _tiltLock == null
        )
        {
            _tiltLock =
                FindFirstObjectByType<
                    KiwiFacePartSharedTiltLock>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _rigidCenter == null
        )
        {
            _rigidCenter =
                FindFirstObjectByType<
                    KiwiFacePartRigidCenterLock>(
                    FindObjectsInactive.Include);
        }
    }
}
