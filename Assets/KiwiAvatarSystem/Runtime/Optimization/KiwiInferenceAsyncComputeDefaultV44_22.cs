using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    /// <summary>
    /// KiwiAvatarSystem v44.22
    /// Makes the already-validated v38 async-compute inference path the
    /// Windows DX12 default without modifying KiwiInferenceFaceTracker.
    ///
    /// Explicit process overrides remain authoritative:
    ///   KIWI_INFERENCE_ASYNC_COMPUTE_PROBE=0 / false / off -> baseline
    ///   KIWI_INFERENCE_ASYNC_COMPUTE_PROBE=1 / true / on   -> async compute
    ///
    /// With no override, Windows DX12 enables async compute only when
    /// SystemInfo.supportsAsyncCompute is true.
    /// </summary>
    internal static class KiwiInferenceAsyncComputeDefaultV44_22
    {
        private const string Contract =
            "KIWI_V5_1_PHASE16_20_50_V44_22_ASYNC_COMPUTE_DEFAULT";

        private const string EnvironmentVariable =
            "KIWI_INFERENCE_ASYNC_COMPUTE_PROBE";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyDefault()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string existing =
                Environment.GetEnvironmentVariable(
                    EnvironmentVariable);

            if (!string.IsNullOrWhiteSpace(existing))
            {
                Debug.Log(
                    $"[KiwiInferenceV44_22] contract={Contract} " +
                    $"overridePreserved=1 value='{existing}' " +
                    $"graphicsApi={SystemInfo.graphicsDeviceType} " +
                    $"supportsAsyncCompute={(SystemInfo.supportsAsyncCompute ? 1 : 0)}");
                return;
            }

            bool eligible =
                !Application.isMobilePlatform &&
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12 &&
                SystemInfo.supportsAsyncCompute;

            if (eligible)
            {
                Environment.SetEnvironmentVariable(
                    EnvironmentVariable,
                    "1");

                Debug.Log(
                    $"[KiwiInferenceV44_22] contract={Contract} " +
                    "defaultEnabled=1 queue=ASYNC_COMPUTE_DEFAULT " +
                    $"graphicsApi={SystemInfo.graphicsDeviceType} " +
                    $"supportsAsyncCompute={(SystemInfo.supportsAsyncCompute ? 1 : 0)}");
            }
            else
            {
                Debug.Log(
                    $"[KiwiInferenceV44_22] contract={Contract} " +
                    "defaultEnabled=0 queue=GRAPHICS_BASELINE " +
                    $"graphicsApi={SystemInfo.graphicsDeviceType} " +
                    $"supportsAsyncCompute={(SystemInfo.supportsAsyncCompute ? 1 : 0)}");
            }
#else
            Debug.Log(
                $"[KiwiInferenceV44_22] contract={Contract} " +
                "defaultEnabled=0 reason=NON_WINDOWS");
#endif
        }
    }
}
