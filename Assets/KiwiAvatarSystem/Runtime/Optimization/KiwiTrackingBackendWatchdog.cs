using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Lightweight runtime diagnostics for the hybrid tracker.
/// It does not change tracking data. It reports when Inference Engine is
/// producing a confident presence score but is still not becoming the primary
/// publisher, which usually means a downstream geometry/publish guard rejected
/// the frame.
/// </summary>
[DefaultExecutionOrder(30100)]
[DisallowMultipleComponent]
public sealed class KiwiTrackingBackendWatchdog : MonoBehaviour
{
    private FaceLandmarkerRunner _runner;

    [Range(0.5f, 5f)]
    public float warningDelaySeconds = 1.5f;

    [Range(0.4f, 0.95f)]
    public float confidentPresence = 0.70f;

    [SerializeField]
    private float debugConfidentButFallbackSeconds;

    private bool _warned;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiTrackingBackendWatchdog>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(
                "[Kiwi] Tracking Backend Watchdog");

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiTrackingBackendWatchdog>();
    }

    private void Update()
    {
        if (_runner == null)
        {
            _runner =
                FindFirstObjectByType<FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);

            if (_runner == null)
            {
                return;
            }
        }

        bool suspicious =
            _runner.InferenceEngineHybridEnabled &&
            !_runner.InferenceEnginePrimaryActive &&
            _runner.LatestInferenceEnginePresence >=
                confidentPresence;

        if (!suspicious)
        {
            debugConfidentButFallbackSeconds = 0f;
            _warned = false;
            return;
        }

        debugConfidentButFallbackSeconds +=
            Time.unscaledDeltaTime;

        if (
            !_warned &&
            debugConfidentButFallbackSeconds >=
                warningDelaySeconds
        )
        {
            _warned = true;

            Debug.LogWarning(
                "[Kiwi Tracking] Inference Engine presence is confident (" +
                _runner.LatestInferenceEnginePresence.ToString("F2") +
                ") but MediaPipe is still primary. " +
                "If this persists, inspect the Inference Engine geometry/publish " +
                "guard rather than adding more display smoothing.",
                this);
        }
    }
}
