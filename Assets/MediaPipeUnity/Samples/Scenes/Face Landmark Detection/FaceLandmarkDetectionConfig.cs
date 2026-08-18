using Mediapipe.Tasks.Vision.FaceLandmarker;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    public class FaceLandmarkDetectionConfig
    {
        // WindowsではCPU
        public Tasks.Core.BaseOptions.Delegate Delegate { get; set; } =
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            Tasks.Core.BaseOptions.Delegate.CPU;
#else
            Tasks.Core.BaseOptions.Delegate.GPU;
#endif

        // Windows/DX11 AsyncGPUReadback measured 89-96 ms at 960x540 in the
        // supplied v5.1 recording. A bounded 640-wide synchronous read avoids
        // that queued GPU age. Mobile keeps the non-blocking path.
        public ImageReadMode ImageReadMode { get; set; } =
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            ImageReadMode.CPU;
#else
            ImageReadMode.CPUAsync;
#endif

        public Tasks.Vision.Core.RunningMode RunningMode { get; set; }
            = Tasks.Vision.Core.RunningMode.LIVE_STREAM;

        public int NumFaces { get; set; } = 1;

        public float MinFaceDetectionConfidence { get; set; } = 0.5f;
        public float MinFacePresenceConfidence { get; set; } = 0.5f;
        public float MinTrackingConfidence { get; set; } = 0.5f;

        public bool OutputFaceBlendshapes { get; set; } = true;
        public bool OutputFacialTransformationMatrixes { get; set; } = true;
        public string ModelPath =>
            OutputFaceBlendshapes
                ? "face_landmarker_v2_with_blendshapes.bytes"
                : "face_landmarker_v2.bytes";


        public FaceLandmarkerOptions GetFaceLandmarkerOptions(
            FaceLandmarkerOptions.ResultCallback resultCallback = null)
        {
            return new FaceLandmarkerOptions(
                new Tasks.Core.BaseOptions(
                    Delegate,
                    modelAssetPath: ModelPath
                ),

                runningMode: RunningMode,

                numFaces: NumFaces,

                minFaceDetectionConfidence:
                    MinFaceDetectionConfidence,

                minFacePresenceConfidence:
                    MinFacePresenceConfidence,

                minTrackingConfidence:
                    MinTrackingConfidence,

                outputFaceBlendshapes:
                    OutputFaceBlendshapes,

                outputFaceTransformationMatrixes:
                    OutputFacialTransformationMatrixes,

                resultCallback:
                    resultCallback
            );
        }
    }
}
