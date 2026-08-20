using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Reference contract for future tracking SDK adapters.
/// </summary>
[DisallowMultipleComponent]
public sealed class KiwiExternalTrackingAdapterTemplate : MonoBehaviour
{
    public bool enableAdapter;

    public string providerId =
        "External/Template";

    [Range(0, 200)]
    public int providerPriority = 110;

    private KiwiTrackingProviderHub _hub;

    private void Awake()
    {
        _hub =
            FindFirstObjectByType<
                KiwiTrackingProviderHub>(
                FindObjectsInactive.Include);
    }

    public void SubmitNormalizedFrame(
        FacePrecisionTrackingData data,
        KiwiTrackingProviderHub.TrackingCapability capabilities)
    {
        if (
            !enableAdapter ||
            _hub == null ||
            !data.isValid
        )
        {
            return;
        }

        _hub.SubmitExternalFrame(
            providerId,
            providerPriority,
            capabilities,
            data);
    }

    private void OnDisable()
    {
        if (_hub != null)
        {
            _hub.RemoveExternalProvider(
                providerId);
        }
    }
}
