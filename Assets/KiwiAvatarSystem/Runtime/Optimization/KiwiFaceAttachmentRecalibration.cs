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

    public bool recalibrateAfterProviderChange = true;
    public bool recalibrateAfterTrackingLoss = true;

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

    private ulong _lastObservedFrameId;
    private int _stableFreshFrames;
    private double _stableStartedRealtime;
    private double _lastRecalibrationRealtime =
        -1000.0;

    public bool IsPending =>
        _pending;

    public int RecalibrationCount =>
        debugRecalibrationCount;

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

        _pending =
            false;

        _pendingReason =
            string.Empty;

        _lastObservedFrameId =
            0UL;

        _stableFreshFrames =
            0;

        _stableStartedRealtime =
            0.0;

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

        if (
            providerChanged &&
            recalibrateAfterProviderChange
        )
        {
            RequestRecalibration(
                "ProviderSwitch");
        }

        KiwiTrackingContinuityState.ContinuityState current =
            _continuity.State;

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

        if (
            recoveredFromLoss &&
            recalibrateAfterTrackingLoss
        )
        {
            RequestRecalibration(
                "Reacquisition");
        }

        if (!string.IsNullOrEmpty(provider))
        {
            _lastProvider =
                provider;
        }

        _lastContinuity =
            current;
    }

    private void RequestRecalibration(
        string reason)
    {
        if (
            Time.realtimeSinceStartupAsDouble -
                _lastRecalibrationRealtime <
            minimumSecondsBetweenRecalibrations
        )
        {
            return;
        }

        _pending =
            true;

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
