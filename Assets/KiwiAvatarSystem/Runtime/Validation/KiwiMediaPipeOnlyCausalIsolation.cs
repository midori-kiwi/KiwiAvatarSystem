using System;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Validation-only one-factor isolation for the built-in tracking provider.
///
/// When KIWI_MEDIAPIPE_ONLY_ISOLATION=1, this component configures the existing
/// KiwiInferenceRecoveryBootstrap public disable path before the release scene
/// starts. The Runner then executes its normal InitializeSentisTracker path,
/// which disposes any existing tracker and returns without creating a new one
/// while enableSentisHybridTracking is false.
///
/// Arm absent: no GameObject or component is created and Product behavior is
/// unchanged. This class does not change provider priorities, tracking math,
/// canonical semantics, FacePart state, thresholds, or presentation state.
/// </summary>
[DefaultExecutionOrder(-32760)]
[DisallowMultipleComponent]
public sealed class KiwiMediaPipeOnlyCausalIsolation : MonoBehaviour
{
    private const string ArmEnvironment =
        "KIWI_MEDIAPIPE_ONLY_ISOLATION";

    private const string RuntimeObjectName =
        "[Kiwi Validation] MediaPipe Only Causal Isolation";

    private const string RecoveryObjectName =
        "[Kiwi] Early Inference Recovery";

    private const string MediaPipeProviderId =
        "Runner/MediaPipe";

    private const string InferenceProviderId =
        "Runner/InferenceEngine";

    private KiwiInferenceRecoveryBootstrap _recovery;
    private FaceLandmarkerRunner _runner;
    private KiwiTrackingProviderHub _hub;

    private int _verificationStartFrame;
    private float _nextReferenceSearchTime;

    private ulong _lastRunnerFrameId;
    private ulong _lastHubFrameId;
    private ulong _lastCanonicalFrameId;

    private int _runnerMediaPipeFrameCount;
    private int _runnerInferenceFrameCount;
    private int _hubMediaPipeFrameCount;
    private int _hubInferenceFrameCount;
    private int _hubOtherFrameCount;
    private int _canonicalMediaPipeFrameCount;
    private int _canonicalInferenceFrameCount;
    private int _canonicalOtherFrameCount;
    private int _configurationViolationCount;

    private bool _firstMediaPipeFrameReported;
    private bool _summaryWritten;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        string arm =
            Environment.GetEnvironmentVariable(
                ArmEnvironment);

        if (!string.Equals(arm, "1", StringComparison.Ordinal))
        {
            return;
        }

        KiwiInferenceRecoveryBootstrap recovery =
            FindFirstObjectByType<KiwiInferenceRecoveryBootstrap>(
                FindObjectsInactive.Include);

        if (recovery == null)
        {
            GameObject recoveryHost =
                new GameObject(RecoveryObjectName);

            DontDestroyOnLoad(recoveryHost);

            recovery =
                recoveryHost.AddComponent<
                    KiwiInferenceRecoveryBootstrap>();
        }

        ConfigureOfficialDisable(recovery);

        if (
            FindFirstObjectByType<KiwiMediaPipeOnlyCausalIsolation>(
                FindObjectsInactive.Include) != null)
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiMediaPipeOnlyCausalIsolation>();
    }

    private static void ConfigureOfficialDisable(
        KiwiInferenceRecoveryBootstrap recovery)
    {
        if (recovery == null)
        {
            return;
        }

        // Official public configuration path. Bootstrap propagates enableHybrid
        // to Runner.enableSentisHybridTracking before Runner.Run initializes the
        // optional Inference Engine tracker. Runtime recovery is disabled because
        // restarting a deliberately unavailable provider would violate the
        // one-factor isolation contract.
        recovery.enableHybrid = false;
        recovery.enableRuntimeRecovery = false;
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        ResolveRecoveryAndConfigure();

        Debug.Log(
            "[KiwiMediaPipeOnlyIsolation] ARMED " +
            "arm=KIWI_MEDIAPIPE_ONLY_ISOLATION=1 " +
            "bootstrap.enableHybrid=0 " +
            "bootstrap.enableRuntimeRecovery=0 " +
            "privateReflection=0 providerPriorityWrite=0");
    }

    private void Start()
    {
        ResolveRecoveryAndConfigure();
        _verificationStartFrame = Time.frameCount + 2;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        WriteSummary("DESTROYED");
    }

    private void OnApplicationQuit()
    {
        WriteSummary("APPLICATION_QUIT");
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _runner = null;
        _hub = null;
        _nextReferenceSearchTime = 0f;
        _verificationStartFrame = Time.frameCount + 2;

        ResolveRecoveryAndConfigure();
    }

    private void Update()
    {
        if (Time.frameCount < _verificationStartFrame)
        {
            return;
        }

        ResolveReferences();
        ObserveRunner();
        ObserveHub();
        ObserveCanonical();
    }

    private void ResolveRecoveryAndConfigure()
    {
        if (_recovery == null)
        {
            _recovery =
                FindFirstObjectByType<
                    KiwiInferenceRecoveryBootstrap>(
                    FindObjectsInactive.Include);
        }

        ConfigureOfficialDisable(_recovery);
    }

    private void ResolveReferences()
    {
        if (_runner != null && _hub != null)
        {
            return;
        }

        if (Time.unscaledTime < _nextReferenceSearchTime)
        {
            return;
        }

        _nextReferenceSearchTime =
            Time.unscaledTime + 0.25f;

        if (_runner == null)
        {
            _runner =
                FindFirstObjectByType<FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        if (_hub == null)
        {
            _hub =
                FindFirstObjectByType<KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }
    }

    private void ObserveRunner()
    {
        if (
            _runner == null ||
            !_runner.TryGetLatestPrecisionTrackingData(
                out FacePrecisionTrackingData data) ||
            !data.isValid ||
            data.frameId == 0UL ||
            data.frameId == _lastRunnerFrameId)
        {
            return;
        }

        _lastRunnerFrameId = data.frameId;

        bool configurationValid =
            !_runner.enableSentisHybridTracking &&
            !_runner.InferenceEngineHybridEnabled &&
            !_runner.InferenceEnginePrimaryActive;

        if (!configurationValid)
        {
            _configurationViolationCount++;

            Debug.LogError(
                "[KiwiMediaPipeOnlyIsolation] CONFIGURATION_VIOLATION " +
                "frameId=" + data.frameId + " " +
                "runner.enableSentisHybridTracking=" +
                (_runner.enableSentisHybridTracking ? "1" : "0") + " " +
                "hybridEnabled=" +
                (_runner.InferenceEngineHybridEnabled ? "1" : "0") + " " +
                "primaryActive=" +
                (_runner.InferenceEnginePrimaryActive ? "1" : "0"));
        }

        if (data.backend == KiwiTrackingBackend.MediaPipe)
        {
            _runnerMediaPipeFrameCount++;

            if (!_firstMediaPipeFrameReported)
            {
                _firstMediaPipeFrameReported = true;

                Debug.Log(
                    "[KiwiMediaPipeOnlyIsolation] FIRST_MEDIAPIPE_FRAME " +
                    "frameId=" + data.frameId + " " +
                    "timestamp=" + data.timestamp + " " +
                    "submissionHostTicks=" +
                    data.submissionHostTicks + " " +
                    "arrivalHostTicks=" + data.arrivalHostTicks + " " +
                    "runnerHybridEnabled=0 primaryActive=0");
            }
        }
        else if (data.backend == KiwiTrackingBackend.InferenceEngine)
        {
            _runnerInferenceFrameCount++;

            Debug.LogError(
                "[KiwiMediaPipeOnlyIsolation] INFERENCE_RUNNER_FRAME " +
                "frameId=" + data.frameId + " " +
                "timestamp=" + data.timestamp);
        }
    }

    private void ObserveHub()
    {
        if (
            _hub == null ||
            !_hub.TryGetLatestFrame(
                out FacePrecisionTrackingData data,
                out string providerId) ||
            !data.isValid ||
            data.frameId == 0UL ||
            data.frameId == _lastHubFrameId)
        {
            return;
        }

        _lastHubFrameId = data.frameId;

        if (string.Equals(
            providerId,
            MediaPipeProviderId,
            StringComparison.Ordinal))
        {
            _hubMediaPipeFrameCount++;
        }
        else if (string.Equals(
            providerId,
            InferenceProviderId,
            StringComparison.Ordinal))
        {
            _hubInferenceFrameCount++;

            Debug.LogError(
                "[KiwiMediaPipeOnlyIsolation] INFERENCE_HUB_FRAME " +
                "frameId=" + data.frameId);
        }
        else
        {
            _hubOtherFrameCount++;
        }
    }

    private void ObserveCanonical()
    {
        if (
            !KiwiCanonicalTrackingFrame.TryGetFrame(
                out KiwiTrackingFrame frame) ||
            frame.canonicalFrameId == 0UL ||
            frame.canonicalFrameId == _lastCanonicalFrameId)
        {
            return;
        }

        _lastCanonicalFrameId = frame.canonicalFrameId;

        if (string.Equals(
            frame.providerId,
            MediaPipeProviderId,
            StringComparison.Ordinal))
        {
            _canonicalMediaPipeFrameCount++;
        }
        else if (string.Equals(
            frame.providerId,
            InferenceProviderId,
            StringComparison.Ordinal))
        {
            _canonicalInferenceFrameCount++;

            Debug.LogError(
                "[KiwiMediaPipeOnlyIsolation] INFERENCE_CANONICAL_FRAME " +
                "canonicalFrameId=" + frame.canonicalFrameId);
        }
        else
        {
            _canonicalOtherFrameCount++;
        }
    }

    private void WriteSummary(
        string completionReason)
    {
        if (_summaryWritten)
        {
            return;
        }

        _summaryWritten = true;

        bool gatePassed =
            _runnerMediaPipeFrameCount > 0 &&
            _hubMediaPipeFrameCount > 0 &&
            _canonicalMediaPipeFrameCount > 0 &&
            _runnerInferenceFrameCount == 0 &&
            _hubInferenceFrameCount == 0 &&
            _canonicalInferenceFrameCount == 0 &&
            _configurationViolationCount == 0;

        Debug.Log(
            "[KiwiMediaPipeOnlyIsolation] SUMMARY " +
            "completionReason=" + completionReason + " " +
            "runtimeGate=" + (gatePassed ? "PASS" : "FAIL") + " " +
            "runnerMediaPipeFrames=" + _runnerMediaPipeFrameCount + " " +
            "runnerInferenceFrames=" + _runnerInferenceFrameCount + " " +
            "hubMediaPipeFrames=" + _hubMediaPipeFrameCount + " " +
            "hubInferenceFrames=" + _hubInferenceFrameCount + " " +
            "hubOtherFrames=" + _hubOtherFrameCount + " " +
            "canonicalMediaPipeFrames=" +
            _canonicalMediaPipeFrameCount + " " +
            "canonicalInferenceFrames=" +
            _canonicalInferenceFrameCount + " " +
            "canonicalOtherFrames=" +
            _canonicalOtherFrameCount + " " +
            "configurationViolations=" +
            _configurationViolationCount + " " +
            "humanVisual=PENDING");
    }
}
