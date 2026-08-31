using System;
using UnityEngine;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    /// <summary>
    /// KiwiAvatarSystem v44.21
    /// Landmarker CPU vs CPUAsync readback isolation controller.
    ///
    /// This component changes only FaceLandmarkerRunner.config.ImageReadMode.
    /// It does not modify tracking math, thresholds, ROI, inference settings,
    /// camera transport, provider arbitration, FaceTexture, FacePart, or Spout.
    ///
    /// Environment variable:
    ///   KIWI_LANDMARKER_READBACK=CPU
    ///   KIWI_LANDMARKER_READBACK=CPUASYNC
    ///
    /// With no variable, the component is a complete no-op.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class KiwiLandmarkerReadbackIsolationV44_21 : MonoBehaviour
    {
        private const string Contract =
            "KIWI_V5_1_PHASE16_20_49_V44_21_LANDMARKER_READBACK_AB";

        private const string EnvironmentVariable =
            "KIWI_LANDMARKER_READBACK";

        private const float TelemetryIntervalSeconds = 2.0f;

        private enum RequestedMode
        {
            Disabled = 0,
            CPU = 1,
            CPUAsync = 2
        }

        private RequestedMode _requestedMode;
        private FaceLandmarkerRunner _runner;
        private bool _configurationLogged;
        private bool _invalidValueLogged;
        private int _enforcementCount;
        private float _nextTelemetryTime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            var raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var go = new GameObject("__KiwiLandmarkerReadbackIsolationV44_21");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.DontSave;
            go.AddComponent<KiwiLandmarkerReadbackIsolationV44_21>();
#endif
        }

        private void Awake()
        {
            _requestedMode = ParseRequestedMode(
                Environment.GetEnvironmentVariable(EnvironmentVariable));

            _nextTelemetryTime =
                Time.unscaledTime + TelemetryIntervalSeconds;
        }

        private void Update()
        {
            EnforceRequestedMode();

            if (
                _runner != null &&
                Time.unscaledTime >= _nextTelemetryTime)
            {
                _nextTelemetryTime =
                    Time.unscaledTime + TelemetryIntervalSeconds;

                Debug.Log(
                    $"[KiwiLandmarkerReadbackV44_21] contract={Contract} " +
                    $"requested={RequestedModeName(_requestedMode)} " +
                    $"active={_runner.config.ImageReadMode} " +
                    $"freshHz={_runner.LatestFreshSourceRateHz:F2} " +
                    $"submissionHz={_runner.LatestSubmissionRateHz:F2} " +
                    $"readbackMs={_runner.LatestReadbackLatencyMs:F3} " +
                    $"enforcementCount={_enforcementCount}");
            }
        }

        private void LateUpdate()
        {
            // FaceLandmarkerRunner can switch Windows Hybrid mode to CPUAsync
            // once during its startup coroutine. Re-assert after coroutine
            // processing as well so CPU A/B remains deterministic.
            EnforceRequestedMode();
        }

        private void OnApplicationQuit()
        {
            if (_runner == null || _requestedMode == RequestedMode.Disabled)
            {
                return;
            }

            Debug.Log(
                $"[KiwiLandmarkerReadbackV44_21] FINAL contract={Contract} " +
                $"requested={RequestedModeName(_requestedMode)} " +
                $"active={_runner.config.ImageReadMode} " +
                $"freshHz={_runner.LatestFreshSourceRateHz:F2} " +
                $"submissionHz={_runner.LatestSubmissionRateHz:F2} " +
                $"readbackMs={_runner.LatestReadbackLatencyMs:F3} " +
                $"enforcementCount={_enforcementCount}");
        }

        private void EnforceRequestedMode()
        {
            if (_requestedMode == RequestedMode.Disabled)
            {
                return;
            }

            if (_runner == null)
            {
                _runner = FindFirstObjectByType<FaceLandmarkerRunner>();

                if (_runner == null)
                {
                    return;
                }
            }

            var desired =
                _requestedMode == RequestedMode.CPU
                    ? ImageReadMode.CPU
                    : ImageReadMode.CPUAsync;

            if (_runner.config.ImageReadMode != desired)
            {
                _runner.config.ImageReadMode = desired;
                _enforcementCount++;
            }

            if (_configurationLogged)
            {
                return;
            }

            _configurationLogged = true;

            Debug.Log(
                $"[KiwiLandmarkerReadbackV44_21] READY contract={Contract} " +
                $"requested={RequestedModeName(_requestedMode)} " +
                $"active={_runner.config.ImageReadMode} " +
                "scope=IMAGE_READ_MODE_ONLY " +
                "trackingMathChange=0 thresholdChange=0 roiChange=0 " +
                "cameraChange=0 spoutChange=0 sentisChange=0");
        }

        private RequestedMode ParseRequestedMode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return RequestedMode.Disabled;
            }

            string normalized =
                raw.Trim()
                   .Replace("-", string.Empty)
                   .Replace("_", string.Empty)
                   .Replace(" ", string.Empty)
                   .ToUpperInvariant();

            switch (normalized)
            {
                case "CPU":
                case "SYNC":
                case "CPUSYNC":
                    return RequestedMode.CPU;

                case "CPUASYNC":
                case "ASYNC":
                    return RequestedMode.CPUAsync;

                default:
                    if (!_invalidValueLogged)
                    {
                        _invalidValueLogged = true;
                        Debug.LogError(
                            $"[KiwiLandmarkerReadbackV44_21] INVALID " +
                            $"{EnvironmentVariable}='{raw}'. " +
                            "Use CPU or CPUASYNC.");
                    }

                    return RequestedMode.Disabled;
            }
        }

        private static string RequestedModeName(RequestedMode mode)
        {
            switch (mode)
            {
                case RequestedMode.CPU:
                    return "CPU";
                case RequestedMode.CPUAsync:
                    return "CPUAsync";
                default:
                    return "DISABLED";
            }
        }
    }
}
