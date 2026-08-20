using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Provider-neutral tracking router.
///
/// Current built-in providers:
/// - Runner/InferenceEngine
/// - Runner/MediaPipe
///
/// Future tracker SDK adapters can submit normalized frames through
/// SubmitExternalFrame without modifying KiwiFaceMotion or Avatar Runtime.
/// </summary>
[DefaultExecutionOrder(-28000)]
[DisallowMultipleComponent]
public sealed class KiwiTrackingProviderHub : MonoBehaviour
{
    [Flags]
    public enum TrackingCapability
    {
        None = 0,
        HeadPose = 1 << 0,
        FaceGeometry = 1 << 1,
        Expressions = 1 << 2,
        BodyPose = 1 << 3,
        Hands = 1 << 4
    }

    private sealed class ExternalSlot
    {
        public string id;
        public int priority;
        public TrackingCapability capabilities;
        public FacePrecisionTrackingData data;
        public ulong sourceFrameId;
        public double submittedRealtime;
    }

    private struct Candidate
    {
        public bool valid;
        public string id;
        public int priority;
        public TrackingCapability capabilities;
        public FacePrecisionTrackingData data;
        public ulong sourceFrameId;
        public float age;
        public float score;
    }

    private const string RuntimeObjectName =
        "[Kiwi] Tracking Provider Hub";

    [Header("Built-in provider")]
    public bool useFaceLandmarkerRunner = true;

    [Range(0, 200)]
    public int mediaPipePriority = 80;

    [Range(0, 200)]
    public int inferenceEnginePriority = 105;

    [Header("Arbitration")]
    [Range(0.05f, 1f)]
    public float maximumProviderFrameAge = 0.35f;

    [Range(0f, 1f)]
    public float providerSwitchScoreMargin = 0.12f;

    [Range(1, 6)]
    public int providerSwitchConfirmationFrames = 2;

    [Range(0f, 2f)]
    public float qualityScoreWeight = 0.80f;

    [Range(0f, 2f)]
    public float ageScorePenaltyPerSecond = 0.90f;

    [Header("Diagnostics")]
    [SerializeField] private string debugActiveProvider = "-";
    [SerializeField] private float debugActiveScore;
    [SerializeField] private float debugActiveAgeMs;
    [SerializeField] private int debugExternalProviderCount;
    [SerializeField] private string debugSwitchCandidate = "-";
    [SerializeField] private int debugSwitchCandidateFrames;

    private FaceLandmarkerRunner _runner;

    private readonly Dictionary<string, ExternalSlot>
        _external =
            new Dictionary<string, ExternalSlot>(
                StringComparer.Ordinal);

    private string _activeProviderId =
        string.Empty;

    private string _switchCandidateId =
        string.Empty;

    private int _switchCandidateCount;

    private ulong _hubFrameId;
    private string _lastPublishedProviderId =
        string.Empty;
    private ulong _lastPublishedSourceFrameId;

    private FacePrecisionTrackingData
        _latestPublished;

    private bool _hasPublished;

    public string ActiveProviderId =>
        _activeProviderId;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiTrackingProviderHub>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiTrackingProviderHub>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        RefreshRunner();
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
        _runner = null;
        _activeProviderId = string.Empty;
        ClearSwitchCandidate();
        RefreshRunner();
    }

    private void Update()
    {
        RefreshRunner();

        Candidate best =
            FindBestCandidate();

        if (!best.valid)
        {
            debugActiveProvider = "-";
            debugActiveScore = 0f;
            debugActiveAgeMs = 0f;
            return;
        }

        Candidate active =
            GetCandidateById(
                _activeProviderId);

        Candidate selected =
            best;

        if (active.valid)
        {
            selected = active;

            if (
                !string.Equals(
                    best.id,
                    active.id,
                    StringComparison.Ordinal) &&
                best.score >=
                    active.score +
                    providerSwitchScoreMargin
            )
            {
                if (
                    string.Equals(
                        _switchCandidateId,
                        best.id,
                        StringComparison.Ordinal)
                )
                {
                    _switchCandidateCount++;
                }
                else
                {
                    _switchCandidateId = best.id;
                    _switchCandidateCount = 1;
                }

                debugSwitchCandidate =
                    best.id;

                debugSwitchCandidateFrames =
                    _switchCandidateCount;

                if (
                    _switchCandidateCount >=
                    Mathf.Max(
                        1,
                        providerSwitchConfirmationFrames)
                )
                {
                    selected = best;
                    ClearSwitchCandidate();
                }
            }
            else
            {
                ClearSwitchCandidate();
            }
        }
        else
        {
            // Current provider is stale/lost.
            ClearSwitchCandidate();
        }

        _activeProviderId =
            selected.id;

        debugActiveProvider =
            selected.id;

        debugActiveScore =
            selected.score;

        debugActiveAgeMs =
            selected.age *
            1000f;

        PublishIfChanged(
            selected);

        debugExternalProviderCount =
            _external.Count;
    }

    public void SubmitExternalFrame(
        string providerId,
        int priority,
        TrackingCapability capabilities,
        FacePrecisionTrackingData data)
    {
        if (
            string.IsNullOrWhiteSpace(providerId) ||
            !data.isValid
        )
        {
            return;
        }

        if (
            !_external.TryGetValue(
                providerId,
                out ExternalSlot slot)
        )
        {
            slot =
                new ExternalSlot
                {
                    id = providerId
                };

            _external.Add(
                providerId,
                slot);
        }

        slot.priority = priority;
        slot.capabilities = capabilities;
        slot.data = data;
        slot.sourceFrameId = data.frameId;
        slot.submittedRealtime =
            Time.realtimeSinceStartupAsDouble;
    }

    public void RemoveExternalProvider(
        string providerId)
    {
        if (
            string.IsNullOrEmpty(providerId)
        )
        {
            return;
        }

        _external.Remove(providerId);

        if (
            string.Equals(
                _activeProviderId,
                providerId,
                StringComparison.Ordinal)
        )
        {
            _activeProviderId =
                string.Empty;
        }
    }

    public bool TryGetLatestFrame(
        out FacePrecisionTrackingData data,
        out string providerId)
    {
        data =
            _latestPublished;

        providerId =
            _activeProviderId;

        return
            _hasPublished &&
            data.isValid &&
            data.frameId >
                0UL;
    }

    private void RefreshRunner()
    {
        if (!useFaceLandmarkerRunner)
        {
            _runner = null;
            return;
        }

        if (_runner == null)
        {
            _runner =
                FindFirstObjectByType<
                    FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }
    }

    private Candidate FindBestCandidate()
    {
        Candidate best =
            GetRunnerCandidate();

        List<string> staleIds =
            null;

        foreach (
            KeyValuePair<string, ExternalSlot> pair
            in _external)
        {
            Candidate candidate =
                BuildExternalCandidate(
                    pair.Value);

            if (!candidate.valid)
            {
                double age =
                    Time.realtimeSinceStartupAsDouble -
                    pair.Value.submittedRealtime;

                if (
                    age >
                    Mathf.Max(
                        1f,
                        maximumProviderFrameAge *
                        4f)
                )
                {
                    if (staleIds == null)
                    {
                        staleIds =
                            new List<string>();
                    }

                    staleIds.Add(
                        pair.Key);
                }

                continue;
            }

            if (
                !best.valid ||
                candidate.score >
                    best.score
            )
            {
                best =
                    candidate;
            }
        }

        if (staleIds != null)
        {
            for (
                int i = 0;
                i < staleIds.Count;
                i++
            )
            {
                _external.Remove(
                    staleIds[i]);
            }
        }

        return best;
    }

    private Candidate GetCandidateById(
        string id)
    {
        if (
            string.IsNullOrEmpty(id)
        )
        {
            return default;
        }

        if (
            id ==
                "Runner/MediaPipe" ||
            id ==
                "Runner/InferenceEngine"
        )
        {
            Candidate candidate =
                GetRunnerCandidate();

            return
                string.Equals(
                    candidate.id,
                    id,
                    StringComparison.Ordinal)
                    ? candidate
                    : default;
        }

        if (
            _external.TryGetValue(
                id,
                out ExternalSlot slot)
        )
        {
            return
                BuildExternalCandidate(
                    slot);
        }

        return default;
    }

    private Candidate GetRunnerCandidate()
    {
        if (
            _runner == null ||
            !_runner.TryGetLatestPrecisionTrackingData(
                out FacePrecisionTrackingData data) ||
            !data.isValid ||
            data.frameId == 0UL
        )
        {
            return default;
        }

        float age =
            CalculateAge(
                data);

        if (
            age >
            maximumProviderFrameAge
        )
        {
            return default;
        }

        bool inference =
            data.backend ==
                KiwiTrackingBackend.InferenceEngine;

        int priority =
            inference
                ? inferenceEnginePriority
                : mediaPipePriority;

        string id =
            inference
                ? "Runner/InferenceEngine"
                : "Runner/MediaPipe";

        return
            new Candidate
            {
                valid = true,
                id = id,
                priority = priority,
                capabilities =
                    TrackingCapability.HeadPose |
                    TrackingCapability.FaceGeometry |
                    TrackingCapability.Expressions,
                data = data,
                sourceFrameId = data.frameId,
                age = age,
                score =
                    CalculateScore(
                        priority,
                        data.geometryQuality,
                        age)
            };
    }

    private Candidate BuildExternalCandidate(
        ExternalSlot slot)
    {
        if (
            slot == null ||
            !slot.data.isValid
        )
        {
            return default;
        }

        float age =
            slot.data.arrivalHostTicks >
                0L
                ? CalculateAge(
                    slot.data)
                : Mathf.Max(
                    0f,
                    (float)(
                        Time.realtimeSinceStartupAsDouble -
                        slot.submittedRealtime));

        if (
            age >
            maximumProviderFrameAge
        )
        {
            return default;
        }

        return
            new Candidate
            {
                valid = true,
                id = slot.id,
                priority = slot.priority,
                capabilities = slot.capabilities,
                data = slot.data,
                sourceFrameId = slot.sourceFrameId,
                age = age,
                score =
                    CalculateScore(
                        slot.priority,
                        slot.data.geometryQuality,
                        age)
            };
    }

    private float CalculateScore(
        int priority,
        float geometryQuality,
        float age)
    {
        return
            Mathf.Clamp(
                priority / 100f,
                0f,
                2f) +
            Mathf.Clamp01(
                geometryQuality) *
            qualityScoreWeight -
            age *
            ageScorePenaltyPerSecond;
    }

    private static float CalculateAge(
        FacePrecisionTrackingData data)
    {
        if (
            data.arrivalHostTicks <=
                0L
        )
        {
            return 0f;
        }

        long now =
            System.Diagnostics.Stopwatch
                .GetTimestamp();

        long delta =
            now -
            data.arrivalHostTicks;

        if (delta <= 0L)
        {
            return 0f;
        }

        return
            (float)(
                delta /
                (double)
                System.Diagnostics.Stopwatch
                    .Frequency);
    }

    private void PublishIfChanged(
        Candidate selected)
    {
        bool changed =
            !string.Equals(
                selected.id,
                _lastPublishedProviderId,
                StringComparison.Ordinal) ||
            selected.sourceFrameId !=
                _lastPublishedSourceFrameId;

        if (!changed)
        {
            return;
        }

        FacePrecisionTrackingData output =
            selected.data;

        _hubFrameId++;

        if (_hubFrameId == 0UL)
        {
            _hubFrameId++;
        }

        output.frameId =
            _hubFrameId;

        _latestPublished =
            output;

        _hasPublished =
            true;

        _lastPublishedProviderId =
            selected.id;

        _lastPublishedSourceFrameId =
            selected.sourceFrameId;
    }

    private void ClearSwitchCandidate()
    {
        _switchCandidateId =
            string.Empty;

        _switchCandidateCount =
            0;

        debugSwitchCandidate =
            "-";

        debugSwitchCandidateFrames =
            0;
    }
}
