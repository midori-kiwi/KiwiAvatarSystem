using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// v5.1 Phase 10 recovery-domain contract.
///
/// Global recovery owns only provider / rigid-pose / tracking-continuity
/// recovery. Semantic recovery owns only face-part-local concerns such as
/// eye/mouth presentation, attachment fitting, masks, surface presentation and
/// head-local sample-frame reacquisition.
///
/// This coordinator intentionally owns no Transform, material, mask, renderer,
/// provider switch or tracker restart. It records domain-specific recovery
/// transactions so one domain can never reset the other merely by sharing a
/// generic "recovery" concept.
/// </summary>
[DefaultExecutionOrder(-16000)]
[DisallowMultipleComponent]
public sealed class KiwiRecoveryDomainCoordinator : MonoBehaviour
{
    public const string CoordinatorVersion =
        "5.1.0-phase10";

    public enum GlobalRecoveryState
    {
        Starting = 0,
        Stable = 1,
        Degraded = 2,
        Holding = 3,
        Reacquiring = 4,
        Lost = 5,
        Restarting = 6
    }

    [Flags]
    public enum GlobalRecoveryReason
    {
        None = 0,
        ProviderSwitch = 1 << 0,
        TrackingLost = 1 << 1,
        TrackingReacquire = 1 << 2,
        InferencePipelineStalled = 1 << 3,
        InferenceRestart = 1 << 4,
        CameraSessionChanged = 1 << 5
    }

    [Flags]
    public enum SemanticComponent
    {
        None = 0,
        LeftEye = 1 << 0,
        RightEye = 1 << 1,
        Mouth = 1 << 2,
        Attachment = 1 << 3,
        Mask = 1 << 4,
        Surface = 1 << 5,
        HeadLocalSampleFrame = 1 << 6,
        AllFaceParts = LeftEye | RightEye | Mouth
    }

    [Flags]
    public enum SemanticRecoveryReason
    {
        None = 0,
        VisibilityLatch = 1 << 0,
        MaskInvalid = 1 << 1,
        AllPartsMissing = 1 << 2,
        AttachmentRebind = 1 << 3,
        HeadLocalAngleReacquire = 1 << 4,
        SurfaceInvalid = 1 << 5,
        GeometryChannelDegraded = 1 << 6,
        ExpressionChannelDegraded = 1 << 7
    }

    public enum SemanticHealthState
    {
        Healthy = 0,
        Degraded = 1,
        RecoveryPending = 2
    }

    private const string RuntimeObjectName =
        "[Kiwi] Recovery Domain Coordinator";

    private sealed class SemanticRequest
    {
        public SemanticComponent component;
        public SemanticRecoveryReason reason;
        public string source;
        public bool persistent;
        public double expiryRealtime;
    }

    [Header("Diagnostics")]
    [SerializeField] private GlobalRecoveryState debugGlobalState =
        GlobalRecoveryState.Starting;
    [SerializeField] private long debugGlobalRecoverySequence;
    [SerializeField] private string debugGlobalReason = "None";
    [SerializeField] private string debugGlobalSource = "-";
    [SerializeField] private SemanticHealthState debugSemanticHealth =
        SemanticHealthState.Healthy;
    [SerializeField] private string debugActiveSemanticComponents = "None";
    [SerializeField] private long debugSemanticRecoverySequence;
    [SerializeField] private string debugSemanticReason = "None";
    [SerializeField] private string debugSemanticSource = "-";
    [SerializeField] private int debugActiveSemanticRequestCount;

    private static KiwiRecoveryDomainCoordinator _instance;

    private static readonly Dictionary<string, SemanticRequest>
        SemanticRequests =
            new Dictionary<string, SemanticRequest>(StringComparer.Ordinal);

    private static readonly HashSet<string>
        ActiveGlobalSources =
            new HashSet<string>(StringComparer.Ordinal);

    private static long _globalRecoverySequence;
    private static long _semanticRecoverySequence;

    private static GlobalRecoveryReason _lastGlobalReason;
    private static string _lastGlobalSource = string.Empty;
    private static SemanticRecoveryReason _lastSemanticReason;
    private static string _lastSemanticSource = string.Empty;

    private KiwiTrackingContinuityState _continuity;
    private KiwiFaceChannelContinuity _faceChannels;

    private KiwiTrackingContinuityState.ContinuityState _lastContinuityState =
        KiwiTrackingContinuityState.ContinuityState.Starting;

    private int _lastProviderGeneration;
    private int _lastCameraGeneration;

    public static bool HasRuntimeInstance =>
        _instance != null;

    public static long GlobalRecoverySequence =>
        _globalRecoverySequence;

    public static long SemanticRecoverySequence =>
        _semanticRecoverySequence;

    public static GlobalRecoveryReason LastGlobalReason =>
        _lastGlobalReason;

    public static string LastGlobalSource =>
        string.IsNullOrEmpty(_lastGlobalSource)
            ? "-"
            : _lastGlobalSource;

    public static SemanticRecoveryReason LastSemanticReason =>
        _lastSemanticReason;

    public static string LastSemanticSource =>
        string.IsNullOrEmpty(_lastSemanticSource)
            ? "-"
            : _lastSemanticSource;

    /// <summary>
    /// Read-only Phase 12 diagnostic: OR of reasons owned by semantic requests
    /// that are currently active. This does not create, extend or complete a
    /// request and exists so acceptance tests cannot miss two recoveries that
    /// begin inside the same Unity frame.
    /// </summary>
    public static SemanticRecoveryReason ActiveSemanticReasons
    {
        get
        {
            SemanticRecoveryReason reasons =
                SemanticRecoveryReason.None;

            foreach (SemanticRequest request in SemanticRequests.Values)
            {
                reasons |= request.reason;
            }

            return reasons;
        }
    }

    public GlobalRecoveryState CurrentGlobalState =>
        ResolveGlobalState();

    public SemanticHealthState CurrentSemanticHealth =>
        ResolveSemanticHealth();

    public SemanticComponent ActiveSemanticComponents =>
        ResolveActiveSemanticComponents();

    public int ActiveSemanticRequestCount =>
        SemanticRequests.Count;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        _instance = null;
        SemanticRequests.Clear();
        ActiveGlobalSources.Clear();
        _globalRecoverySequence = 0L;
        _semanticRecoverySequence = 0L;
        _lastGlobalReason = GlobalRecoveryReason.None;
        _lastGlobalSource = string.Empty;
        _lastSemanticReason = SemanticRecoveryReason.None;
        _lastSemanticSource = string.Empty;
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiRecoveryDomainCoordinator>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiRecoveryDomainCoordinator>();
    }

    private void Awake()
    {
        if (
            _instance != null &&
            _instance != this
        )
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        RefreshReferences(true);

        _lastProviderGeneration =
            KiwiRuntimeGenerationContext.ProviderGeneration;
        _lastCameraGeneration =
            KiwiRuntimeGenerationContext.CameraGeneration;

        if (_continuity != null)
        {
            _lastContinuityState =
                _continuity.State;
        }
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;

        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        // Active recovery work never crosses a tracking-session/scene boundary.
        // Counters remain monotonic for telemetry; only active leases are cleared.
        ActiveGlobalSources.Clear();
        SemanticRequests.Clear();

        _continuity = null;
        _faceChannels = null;

        RefreshReferences(true);

        _lastProviderGeneration =
            KiwiRuntimeGenerationContext.ProviderGeneration;
        _lastCameraGeneration =
            KiwiRuntimeGenerationContext.CameraGeneration;
        _lastContinuityState =
            _continuity != null
                ? _continuity.State
                : KiwiTrackingContinuityState.ContinuityState.Starting;
    }

    private void Update()
    {
        RefreshReferences(false);
        ExpireSemanticRequests();
        ObserveGlobalRecoveryBoundaries();
        UpdateDiagnostics();
    }

    /// <summary>
    /// Starts a true global recovery transaction. This API does not mutate any
    /// semantic recovery request or face-part state.
    /// </summary>
    public static void BeginGlobalRecovery(
        string source,
        GlobalRecoveryReason reason)
    {
        source = NormalizeSource(source);

        bool added =
            ActiveGlobalSources.Add(source);

        _lastGlobalReason =
            reason == GlobalRecoveryReason.None
                ? GlobalRecoveryReason.InferenceRestart
                : reason;
        _lastGlobalSource = source;

        if (added)
        {
            _globalRecoverySequence++;
        }
    }

    /// <summary>
    /// Completes only the matching global transaction. Semantic recovery state
    /// remains untouched regardless of success/failure.
    /// </summary>
    public static void CompleteGlobalRecovery(
        string source,
        bool success)
    {
        source = NormalizeSource(source);
        ActiveGlobalSources.Remove(source);

        _lastGlobalSource = source;

        if (!success)
        {
            _lastGlobalReason |=
                GlobalRecoveryReason.InferencePipelineStalled;
        }
    }

    /// <summary>
    /// Starts a persistent semantic recovery request. Each source/component pair
    /// is independent, so completing an attachment recovery cannot clear a mask
    /// or head-local recovery owned by another system.
    /// </summary>
    public static void BeginSemanticRecovery(
        SemanticComponent components,
        SemanticRecoveryReason reason,
        string source)
    {
        SubmitSemanticRecovery(
            components,
            reason,
            source,
            true,
            0.0);
    }

    /// <summary>
    /// Emits a bounded semantic recovery lease for synchronous/one-shot repair.
    /// It never changes the global recovery sequence/state.
    /// </summary>
    public static void PulseSemanticRecovery(
        SemanticComponent components,
        SemanticRecoveryReason reason,
        string source,
        float seconds = 0.50f)
    {
        SubmitSemanticRecovery(
            components,
            reason,
            source,
            false,
            Math.Max(
                0.05,
                seconds));
    }

    /// <summary>
    /// Completes only semantic requests created by the same source for the
    /// selected components. Other semantic subsystems and the global domain are
    /// intentionally unaffected.
    /// </summary>
    public static void CompleteSemanticRecovery(
        SemanticComponent components,
        string source)
    {
        source = NormalizeSource(source);

        foreach (
            SemanticComponent component in
            EnumerateSingleComponents(components))
        {
            SemanticRequests.Remove(
                MakeSemanticKey(
                    component,
                    source));
        }
    }

    public static bool IsSemanticRecoveryActive(
        SemanticComponent component)
    {
        if (component == SemanticComponent.None)
        {
            return false;
        }

        ExpireSemanticRequestsStatic();

        foreach (SemanticRequest request in SemanticRequests.Values)
        {
            if ((request.component & component) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void SubmitSemanticRecovery(
        SemanticComponent components,
        SemanticRecoveryReason reason,
        string source,
        bool persistent,
        double leaseSeconds)
    {
        source = NormalizeSource(source);

        if (components == SemanticComponent.None)
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        bool addedAny = false;

        foreach (
            SemanticComponent component in
            EnumerateSingleComponents(components))
        {
            string key =
                MakeSemanticKey(
                    component,
                    source);

            if (!SemanticRequests.TryGetValue(
                    key,
                    out SemanticRequest request))
            {
                request =
                    new SemanticRequest
                    {
                        component = component,
                        source = source
                    };

                addedAny = true;
            }

            request.reason |= reason;
            request.persistent |= persistent;

            if (!request.persistent)
            {
                request.expiryRealtime =
                    Math.Max(
                        request.expiryRealtime,
                        now + leaseSeconds);
            }
            else
            {
                request.expiryRealtime =
                    0.0;
            }

            SemanticRequests[key] = request;
        }

        if (addedAny)
        {
            _semanticRecoverySequence++;
        }

        _lastSemanticReason =
            reason;
        _lastSemanticSource =
            source;
    }

    private void ObserveGlobalRecoveryBoundaries()
    {
        int providerGeneration =
            KiwiRuntimeGenerationContext.ProviderGeneration;

        if (providerGeneration != _lastProviderGeneration)
        {
            _lastProviderGeneration = providerGeneration;
            RecordGlobalBoundary(
                GlobalRecoveryReason.ProviderSwitch,
                "ProviderGeneration");
        }

        int cameraGeneration =
            KiwiRuntimeGenerationContext.CameraGeneration;

        if (cameraGeneration != _lastCameraGeneration)
        {
            _lastCameraGeneration = cameraGeneration;
            RecordGlobalBoundary(
                GlobalRecoveryReason.CameraSessionChanged,
                "CameraGeneration");
        }

        if (_continuity == null)
        {
            return;
        }

        KiwiTrackingContinuityState.ContinuityState current =
            _continuity.State;

        if (current == _lastContinuityState)
        {
            return;
        }

        if (
            current ==
                KiwiTrackingContinuityState.ContinuityState.Lost)
        {
            RecordGlobalBoundary(
                GlobalRecoveryReason.TrackingLost,
                "TrackingContinuity");
        }
        else if (
            current ==
                KiwiTrackingContinuityState.ContinuityState.Reacquiring &&
            (
                _lastContinuityState ==
                    KiwiTrackingContinuityState.ContinuityState.Holding ||
                _lastContinuityState ==
                    KiwiTrackingContinuityState.ContinuityState.Lost
            )
        )
        {
            RecordGlobalBoundary(
                GlobalRecoveryReason.TrackingReacquire,
                "TrackingContinuity");
        }

        _lastContinuityState = current;
    }

    private static void RecordGlobalBoundary(
        GlobalRecoveryReason reason,
        string source)
    {
        _globalRecoverySequence++;
        _lastGlobalReason = reason;
        _lastGlobalSource = source;
    }

    private GlobalRecoveryState ResolveGlobalState()
    {
        if (ActiveGlobalSources.Count > 0)
        {
            return GlobalRecoveryState.Restarting;
        }

        if (_continuity == null)
        {
            return GlobalRecoveryState.Starting;
        }

        switch (_continuity.State)
        {
            case KiwiTrackingContinuityState.ContinuityState.Stable:
                return GlobalRecoveryState.Stable;

            case KiwiTrackingContinuityState.ContinuityState.Degraded:
                return GlobalRecoveryState.Degraded;

            case KiwiTrackingContinuityState.ContinuityState.Holding:
                return GlobalRecoveryState.Holding;

            case KiwiTrackingContinuityState.ContinuityState.Reacquiring:
                return GlobalRecoveryState.Reacquiring;

            case KiwiTrackingContinuityState.ContinuityState.Lost:
                return GlobalRecoveryState.Lost;

            default:
                return GlobalRecoveryState.Starting;
        }
    }

    private SemanticHealthState ResolveSemanticHealth()
    {
        if (SemanticRequests.Count > 0)
        {
            return SemanticHealthState.RecoveryPending;
        }

        if (_faceChannels == null)
        {
            return SemanticHealthState.Degraded;
        }

        bool geometryDegraded =
            _faceChannels.GeometryState ==
                KiwiFaceChannelContinuity.ChannelState.Unavailable ||
            _faceChannels.GeometryState ==
                KiwiFaceChannelContinuity.ChannelState.Stale ||
            _faceChannels.GeometryState ==
                KiwiFaceChannelContinuity.ChannelState.Reacquiring;

        bool expressionDegraded =
            _faceChannels.ExpressionState ==
                KiwiFaceChannelContinuity.ChannelState.Unavailable ||
            _faceChannels.ExpressionState ==
                KiwiFaceChannelContinuity.ChannelState.Stale ||
            _faceChannels.ExpressionState ==
                KiwiFaceChannelContinuity.ChannelState.Reacquiring;

        return
            geometryDegraded || expressionDegraded
                ? SemanticHealthState.Degraded
                : SemanticHealthState.Healthy;
    }

    private static SemanticComponent ResolveActiveSemanticComponents()
    {
        ExpireSemanticRequestsStatic();

        SemanticComponent components =
            SemanticComponent.None;

        foreach (SemanticRequest request in SemanticRequests.Values)
        {
            components |= request.component;
        }

        return components;
    }

    private void ExpireSemanticRequests()
    {
        ExpireSemanticRequestsStatic();
    }

    private static void ExpireSemanticRequestsStatic()
    {
        if (SemanticRequests.Count == 0)
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        List<string> expired = null;

        foreach (
            KeyValuePair<string, SemanticRequest> pair in
            SemanticRequests)
        {
            SemanticRequest request = pair.Value;

            if (
                !request.persistent &&
                request.expiryRealtime > 0.0 &&
                now >= request.expiryRealtime
            )
            {
                if (expired == null)
                {
                    expired = new List<string>();
                }

                expired.Add(pair.Key);
            }
        }

        if (expired == null)
        {
            return;
        }

        for (int i = 0; i < expired.Count; i++)
        {
            SemanticRequests.Remove(expired[i]);
        }
    }

    private void RefreshReferences(bool force)
    {
        if (force || _continuity == null)
        {
            _continuity =
                FindFirstObjectByType<KiwiTrackingContinuityState>(
                    FindObjectsInactive.Include);
        }

        if (force || _faceChannels == null)
        {
            _faceChannels =
                FindFirstObjectByType<KiwiFaceChannelContinuity>(
                    FindObjectsInactive.Include);
        }
    }

    private void UpdateDiagnostics()
    {
        debugGlobalState =
            ResolveGlobalState();
        debugGlobalRecoverySequence =
            _globalRecoverySequence;
        debugGlobalReason =
            _lastGlobalReason.ToString();
        debugGlobalSource =
            LastGlobalSource;

        SemanticComponent active =
            ResolveActiveSemanticComponents();

        debugSemanticHealth =
            ResolveSemanticHealth();
        debugActiveSemanticComponents =
            active.ToString();
        debugSemanticRecoverySequence =
            _semanticRecoverySequence;
        debugSemanticReason =
            _lastSemanticReason.ToString();
        debugSemanticSource =
            LastSemanticSource;
        debugActiveSemanticRequestCount =
            SemanticRequests.Count;
    }

    private static IEnumerable<SemanticComponent>
        EnumerateSingleComponents(
            SemanticComponent components)
    {
        SemanticComponent[] values =
        {
            SemanticComponent.LeftEye,
            SemanticComponent.RightEye,
            SemanticComponent.Mouth,
            SemanticComponent.Attachment,
            SemanticComponent.Mask,
            SemanticComponent.Surface,
            SemanticComponent.HeadLocalSampleFrame
        };

        for (int i = 0; i < values.Length; i++)
        {
            if ((components & values[i]) != 0)
            {
                yield return values[i];
            }
        }
    }

    private static string MakeSemanticKey(
        SemanticComponent component,
        string source)
    {
        return
            ((int)component).ToString() +
            "|" +
            source;
    }

    private static string NormalizeSource(
        string source)
    {
        return
            string.IsNullOrWhiteSpace(source)
                ? "Unknown"
                : source.Trim();
    }
}
