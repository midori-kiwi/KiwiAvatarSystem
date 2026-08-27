using Unity.InferenceEngine;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    /// <summary>
    /// Phase16.20.14 CPU-input residency telemetry.
    ///
    /// This class only records where the already-existing inference input tensor
    /// resides before and after TextureConverter.ToTensor. It does not own the
    /// tensor, scheduling, tracking, ROI, decode, camera or presentation.
    /// </summary>
    public static class KiwiInferenceInputResidencyDiagnostics
    {
        public const string ContractMarker =
            "KIWI_V5_1_PHASE16_20_14_V26_CPU_INPUT_RESIDENCY_AB";

        public const int BackendUnknown = 0;
        public const int BackendCpu = 1;
        public const int BackendGpuCompute = 2;
        public const int BackendGpuPixel = 3;

        public static int CpuInputPrePinEnabled { get; private set; }
        public static int InputBackendBeforeToTensorId { get; private set; }
        public static int InputBackendAfterToTensorId { get; private set; }
        public static int InputDataNullBeforeToTensor { get; private set; }
        public static int InputDataNullAfterToTensor { get; private set; }

        public static void PublishMode(bool prePinEnabled)
        {
            CpuInputPrePinEnabled =
                prePinEnabled
                    ? 1
                    : 0;
        }

        public static void RecordBeforeToTensor(
            Tensor<float> tensor)
        {
            InputDataNullBeforeToTensor =
                tensor == null ||
                tensor.dataOnBackend == null
                    ? 1
                    : 0;

            InputBackendBeforeToTensorId =
                ClassifyBackend(
                    tensor);
        }

        public static void RecordAfterToTensor(
            Tensor<float> tensor)
        {
            InputDataNullAfterToTensor =
                tensor == null ||
                tensor.dataOnBackend == null
                    ? 1
                    : 0;

            InputBackendAfterToTensorId =
                ClassifyBackend(
                    tensor);
        }

        private static int ClassifyBackend(
            Tensor<float> tensor)
        {
            if (
                tensor == null ||
                tensor.dataOnBackend == null
            )
            {
                return BackendUnknown;
            }

            switch (tensor.dataOnBackend.backendType)
            {
                case BackendType.CPU:
                    return BackendCpu;

                case BackendType.GPUCompute:
                    return BackendGpuCompute;

                case BackendType.GPUPixel:
                    return BackendGpuPixel;

                default:
                    return BackendUnknown;
            }
        }
    }
}
