using UnityEngine;

/// <summary>
/// Phase 16.18 single handoff-normalization authority diagnostics.
///
/// Canonical provider-space alignment belongs to KiwiTrackingProviderHub.
/// KiwiFaceMotion may still apply its final discontinuity envelope, but its
/// older Phase16.16 Root-space provider bridge is fallback-only when canonical
/// handoff normalization is unavailable.
/// </summary>
[DefaultExecutionOrder(32150)]
[DisallowMultipleComponent]
public sealed class KiwiPhase16_18HandoffAuthorityDiagnostics : MonoBehaviour
{
    // KIWI_V5_1_PHASE16_18_SINGLE_HANDOFF_AUTHORITY_DIAGNOSTICS
    private const string RuntimeObjectName =
        "[Kiwi] Phase16.18 Handoff Authority Diagnostics";

    private static KiwiPhase16_18HandoffAuthorityDiagnostics _instance;

    private static int _localBridgeSuppressedCount;
    private static int _authorityViolationCount;
    private static int _hubEnvelopeGuardActivationCount;
    private static bool _hubEnvelopeGuardLast;

    [SerializeField] private bool debugSingleHandoffAuthority;
    [SerializeField] private bool debugCanonicalNormalizationEnabled;
    [SerializeField] private bool debugCanonicalHandoffActive;
    [SerializeField] private bool debugCanonicalHandoffIsResume;
    [SerializeField] private bool debugLocalRootProviderBridgeActive;
    [SerializeField] private int debugLocalBridgeSuppressedCount;
    [SerializeField] private int debugAuthorityViolationCount;
    [SerializeField] private int debugHubEnvelopeGuardActivationCount;

    public static bool SingleHandoffAuthority =>
        KiwiTrackingProviderHub.CanonicalHandoffNormalizationEnabled !=
        KiwiPhase16_16ProviderBridgeDiagnostics.Active;

    public static bool CanonicalNormalizationEnabled =>
        KiwiTrackingProviderHub.CanonicalHandoffNormalizationEnabled;

    public static bool CanonicalHandoffActive =>
        KiwiTrackingProviderHub.CanonicalHandoffActive;

    public static bool CanonicalHandoffIsResume =>
        KiwiTrackingProviderHub.CanonicalHandoffIsResume;

    public static bool LocalRootProviderBridgeActive =>
        KiwiPhase16_16ProviderBridgeDiagnostics.Active;

    public static int LocalBridgeSuppressedCount =>
        _localBridgeSuppressedCount;

    public static int AuthorityViolationCount =>
        _authorityViolationCount;

    public static int HubEnvelopeGuardActivationCount =>
        _hubEnvelopeGuardActivationCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiPhase16_18HandoffAuthorityDiagnostics>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiPhase16_18HandoffAuthorityDiagnostics>();
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        ResetRuntimeCounters();
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void LateUpdate()
    {
        bool canonical =
            KiwiTrackingProviderHub.CanonicalHandoffNormalizationEnabled;

        bool local =
            KiwiPhase16_16ProviderBridgeDiagnostics.Active;

        if (canonical && local)
        {
            _authorityViolationCount++;
        }

        debugSingleHandoffAuthority =
            canonical != local;

        debugCanonicalNormalizationEnabled =
            canonical;

        debugCanonicalHandoffActive =
            KiwiTrackingProviderHub.CanonicalHandoffActive;

        debugCanonicalHandoffIsResume =
            KiwiTrackingProviderHub.CanonicalHandoffIsResume;

        debugLocalRootProviderBridgeActive =
            local;

        debugLocalBridgeSuppressedCount =
            _localBridgeSuppressedCount;

        debugAuthorityViolationCount =
            _authorityViolationCount;

        debugHubEnvelopeGuardActivationCount =
            _hubEnvelopeGuardActivationCount;
    }

    public static void ReportLocalProviderBridgeSuppressed()
    {
        _localBridgeSuppressedCount++;
    }

    public static void ReportHubHandoffEnvelopeGuard(bool active)
    {
        if (active && !_hubEnvelopeGuardLast)
        {
            _hubEnvelopeGuardActivationCount++;
        }

        _hubEnvelopeGuardLast = active;
    }

    private static void ResetRuntimeCounters()
    {
        _localBridgeSuppressedCount = 0;
        _authorityViolationCount = 0;
        _hubEnvelopeGuardActivationCount = 0;
        _hubEnvelopeGuardLast = false;
    }
}
