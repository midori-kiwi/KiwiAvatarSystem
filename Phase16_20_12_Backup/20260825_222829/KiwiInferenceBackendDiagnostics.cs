using Unity.InferenceEngine;
using UnityEngine;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    /// <summary>
    /// Phase16.20.12 backend-selection telemetry only.
    /// This class does not own scheduling, tracking, ROI, decode or presentation.
    /// </summary>
    public static class KiwiInferenceBackendDiagnostics
    {
        public const string ContractMarker =
            "KIWI_V5_1_PHASE16_20_12_V24_INFERENCE_BACKEND_AB";

        public const int GpuComputeBackendId = 0;
        public const int CpuBackendId = 1;

        public static int BackendId { get; private set; } =
            GpuComputeBackendId;

        public static int CpuBackendEnabled =>
            BackendId == CpuBackendId ? 1 : 0;

        public static void Publish(BackendType backendType)
        {
            BackendId =
                backendType == BackendType.CPU
                    ? CpuBackendId
                    : GpuComputeBackendId;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ResetOnLoad()
        {
            BackendId =
                GpuComputeBackendId;
        }
    }
}
