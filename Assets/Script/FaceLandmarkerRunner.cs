using System.Collections;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    public struct FaceExpressionData
    {
        public bool isValid;

        public float eyeBlinkLeft;
        public float eyeBlinkRight;

        public float eyeWideLeft;
        public float eyeWideRight;

        public float cheekSquintLeft;
        public float cheekSquintRight;

        public float jawOpen;

        public float mouthSmileLeft;
        public float mouthSmileRight;

        public float mouthFrownLeft;
        public float mouthFrownRight;

        public float mouthPucker;
        public float mouthFunnel;

        public float browInnerUp;

        public float browDownLeft;
        public float browDownRight;
    }


    public class FaceLandmarkerRunner
        : VisionTaskApiRunner<FaceLandmarker>
    {
        [SerializeField]
        private FaceLandmarkerResultAnnotationController
            _faceLandmarkerResultAnnotationController;


        private Experimental.TextureFramePool
            _textureFramePool;


        public readonly FaceLandmarkDetectionConfig config =
            new FaceLandmarkDetectionConfig();


        // =====================================================
        // Shared Tracking Data
        // =====================================================

        private readonly object _trackingLock =
            new object();


        private Vector2[] _latestLandmarks;

        private int _latestLandmarkCount = 0;

        private long _latestLandmarkTimestamp = -1;


        private Vector2 _latestFaceCenter =
            new Vector2(
                0.5f,
                0.5f
            );


        private float _latestFaceEyeSpan = 0f;


        private Quaternion _latestFaceRotation =
            Quaternion.identity;


        private bool _hasLatestFaceRotation = false;


        private long _latestMotionTimestamp = -1;


        // =====================================================
        // Expression Data
        // =====================================================

        private FaceExpressionData
            _latestExpressionData;


        private long _latestExpressionTimestamp =
            -1;


        // =====================================================
        // Public Landmark Access
        // =====================================================

        public bool TryGetLatestLandmarks(
            ref Vector2[] destination,
            out int count,
            out long timestamp)
        {
            lock (_trackingLock)
            {
                count =
                    _latestLandmarkCount;


                timestamp =
                    _latestLandmarkTimestamp;


                if (
                    count <= 0 ||
                    _latestLandmarks == null
                )
                {
                    return false;
                }


                if (
                    destination == null ||
                    destination.Length < count
                )
                {
                    destination =
                        new Vector2[count];
                }


                System.Array.Copy(
                    _latestLandmarks,
                    destination,
                    count
                );


                return true;
            }
        }


        // =====================================================
        // Public Motion Access
        // =====================================================

        public bool TryGetLatestMotionData(
            out Vector2 faceCenter,
            out float eyeSpan,
            out Quaternion faceRotation,
            out long timestamp)
        {
            lock (_trackingLock)
            {
                faceCenter =
                    _latestFaceCenter;


                eyeSpan =
                    _latestFaceEyeSpan;


                faceRotation =
                    _latestFaceRotation;


                timestamp =
                    _latestMotionTimestamp;


                return
                    _latestLandmarkCount > 362
                    &&
                    _latestFaceEyeSpan > 0.0001f
                    &&
                    _hasLatestFaceRotation
                    &&
                    _latestMotionTimestamp >= 0;
            }
        }


        // =====================================================
        // Public Expression Access
        // =====================================================

        public bool TryGetLatestExpressionData(
            out FaceExpressionData data,
            out long timestamp)
        {
            lock (_trackingLock)
            {
                data =
                    _latestExpressionData;


                timestamp =
                    _latestExpressionTimestamp;


                return
                    data.isValid
                    &&
                    timestamp >= 0;
            }
        }


        // =====================================================
        // Stop
        // =====================================================

        public override void Stop()
        {
            base.Stop();


            _textureFramePool?.Dispose();

            _textureFramePool =
                null;


            ClearTrackingData();
        }


        // =====================================================
        // Run
        // =====================================================

        protected override IEnumerator Run()
        {
            Debug.Log(
                $"Delegate = {config.Delegate}"
            );

            Debug.Log(
                $"Image Read Mode = {config.ImageReadMode}"
            );

            Debug.Log(
                $"Running Mode = {config.RunningMode}"
            );

            Debug.Log(
                $"NumFaces = {config.NumFaces}"
            );

            Debug.Log(
                $"MinFaceDetectionConfidence = {config.MinFaceDetectionConfidence}"
            );

            Debug.Log(
                $"MinFacePresenceConfidence = {config.MinFacePresenceConfidence}"
            );

            Debug.Log(
                $"MinTrackingConfidence = {config.MinTrackingConfidence}"
            );

            Debug.Log(
                $"OutputFaceBlendshapes = {config.OutputFaceBlendshapes}"
            );

            Debug.Log(
                $"OutputFacialTransformationMatrixes = {config.OutputFacialTransformationMatrixes}"
            );


            yield return
                AssetLoader.PrepareAssetAsync(
                    config.ModelPath
                );


            var options =
                config.GetFaceLandmarkerOptions(
                    config.RunningMode ==
                    Tasks.Vision.Core.RunningMode.LIVE_STREAM
                        ?
                        OnFaceLandmarkDetectionOutput
                        :
                        null
                );


            taskApi =
                FaceLandmarker.CreateFromOptions(
                    options,
                    GpuManager.GpuResources
                );


            var imageSource =
                ImageSourceProvider.ImageSource;


            yield return
                imageSource.Play();


            if (!imageSource.isPrepared)
            {
                Debug.LogError(
                    "Failed to start ImageSource, exiting..."
                );

                yield break;
            }


            _textureFramePool =
                new Experimental.TextureFramePool(
                    imageSource.textureWidth,
                    imageSource.textureHeight,
                    TextureFormat.RGBA32,
                    10
                );


            screen.Initialize(
                imageSource
            );


            SetupAnnotationController(
                _faceLandmarkerResultAnnotationController,
                imageSource
            );


            var transformationOptions =
                imageSource.GetTransformationOptions();


            var flipHorizontally =
                transformationOptions.flipHorizontally;


            var flipVertically =
                transformationOptions.flipVertically;


            var imageProcessingOptions =
                new Tasks.Vision.Core.ImageProcessingOptions(
                    rotationDegrees:
                    (int)transformationOptions.rotationAngle
                );


            AsyncGPUReadbackRequest req =
                default;


            var waitUntilReqDone =
                new WaitUntil(
                    () => req.done
                );


            var waitForEndOfFrame =
                new WaitForEndOfFrame();


            var result =
                FaceLandmarkerResult.Alloc(
                    options.numFaces
                );


            var canUseGpuImage =
                SystemInfo.graphicsDeviceType ==
                GraphicsDeviceType.OpenGLES3
                &&
                GpuManager.GpuResources != null;


            using var glContext =
                canUseGpuImage
                    ?
                    GpuManager.GetGlContext()
                    :
                    null;


            while (true)
            {
                if (isPaused)
                {
                    yield return
                        new WaitWhile(
                            () => isPaused
                        );
                }


                if (
                    !_textureFramePool
                        .TryGetTextureFrame(
                            out var textureFrame
                        )
                )
                {
                    yield return null;

                    continue;
                }


                Image image;


                switch (config.ImageReadMode)
                {
                    case ImageReadMode.GPU:
                        {
                            if (!canUseGpuImage)
                            {
                                throw new System.Exception(
                                    "ImageReadMode.GPU is not supported"
                                );
                            }


                            textureFrame.ReadTextureOnGPU(
                                imageSource.GetCurrentTexture(),
                                flipHorizontally,
                                flipVertically
                            );


                            image =
                                textureFrame.BuildGPUImage(
                                    glContext
                                );


                            yield return
                                waitForEndOfFrame;

                            break;
                        }


                    case ImageReadMode.CPU:
                        {
                            yield return
                                waitForEndOfFrame;


                            textureFrame.ReadTextureOnCPU(
                                imageSource.GetCurrentTexture(),
                                flipHorizontally,
                                flipVertically
                            );


                            image =
                                textureFrame.BuildCPUImage();


                            textureFrame.Release();

                            break;
                        }


                    case ImageReadMode.CPUAsync:

                    default:
                        {
                            req =
                                textureFrame.ReadTextureAsync(
                                    imageSource.GetCurrentTexture(),
                                    flipHorizontally,
                                    flipVertically
                                );


                            yield return
                                waitUntilReqDone;


                            if (req.hasError)
                            {
                                textureFrame.Release();


                                Debug.LogWarning(
                                    "Failed to read texture from the image source"
                                );


                                continue;
                            }


                            image =
                                textureFrame.BuildCPUImage();


                            textureFrame.Release();

                            break;
                        }
                }


                switch (taskApi.runningMode)
                {
                    case Tasks.Vision.Core.RunningMode.IMAGE:
                        {
                            if (
                                taskApi.TryDetect(
                                    image,
                                    imageProcessingOptions,
                                    ref result
                                )
                            )
                            {
                                StoreTrackingData(
                                    result,
                                    GetCurrentTimestampMillisec()
                                );


                                _faceLandmarkerResultAnnotationController
                                    .DrawNow(
                                        result
                                    );
                            }
                            else
                            {
                                ClearTrackingData();


                                _faceLandmarkerResultAnnotationController
                                    .DrawNow(
                                        default
                                    );
                            }

                            break;
                        }


                    case Tasks.Vision.Core.RunningMode.VIDEO:
                        {
                            long timestamp =
                                GetCurrentTimestampMillisec();


                            if (
                                taskApi.TryDetectForVideo(
                                    image,
                                    timestamp,
                                    imageProcessingOptions,
                                    ref result
                                )
                            )
                            {
                                StoreTrackingData(
                                    result,
                                    timestamp
                                );


                                _faceLandmarkerResultAnnotationController
                                    .DrawNow(
                                        result
                                    );
                            }
                            else
                            {
                                ClearTrackingData();


                                _faceLandmarkerResultAnnotationController
                                    .DrawNow(
                                        default
                                    );
                            }

                            break;
                        }


                    case Tasks.Vision.Core.RunningMode.LIVE_STREAM:
                        {
                            taskApi.DetectAsync(
                                image,
                                GetCurrentTimestampMillisec(),
                                imageProcessingOptions
                            );

                            break;
                        }
                }
            }
        }


        // =====================================================
        // Live Stream Callback
        // =====================================================

        private void OnFaceLandmarkDetectionOutput(
            FaceLandmarkerResult result,
            Image image,
            long timestamp)
        {
            StoreTrackingData(
                result,
                timestamp
            );


            // •K‚¸Žc‚·
            _faceLandmarkerResultAnnotationController
                .DrawLater(
                    result
                );
        }


        // =====================================================
        // Store Tracking
        // =====================================================

        private void StoreTrackingData(
            FaceLandmarkerResult result,
            long timestamp)
        {
            if (
                result.faceLandmarks == null
                || result.faceLandmarks.Count == 0
                || result.faceLandmarks[0].landmarks == null
                || result.faceLandmarks[0].landmarks.Count == 0
            )
            {
                ClearTrackingData();
                return;
            }

            var landmarks =
                result.faceLandmarks[0].landmarks;

            int count =
                landmarks.Count;

            Vector2 center =
                new Vector2(0.5f, 0.5f);

            // Kept under the old field/API name for scene compatibility.
            // The value is now a robust whole-face size signal.
            float eyeSpan =
                0f;

            // =====================================================
            // Rigid, roll-stable translation anchor
            // =====================================================
            if (count > 454)
            {
                Vector2 forehead =
                    new Vector2(landmarks[10].x, landmarks[10].y);

                Vector2 chin =
                    new Vector2(landmarks[152].x, landmarks[152].y);

                Vector2 cheekA =
                    new Vector2(landmarks[234].x, landmarks[234].y);

                Vector2 cheekB =
                    new Vector2(landmarks[454].x, landmarks[454].y);

                Vector2 nose =
                    new Vector2(landmarks[1].x, landmarks[1].y);

                Vector2 rigidCenter =
                    (
                        forehead +
                        chin +
                        cheekA +
                        cheekB +
                        nose * 2f
                    ) / 6f;

                Vector2 faceAxis =
                    chin - forehead;

                float faceHeight =
                    faceAxis.magnitude;

                if (faceHeight > 0.000001f)
                {
                    Vector2 faceDown =
                        faceAxis / faceHeight;

                    // Near the physical neck pivot. Moving along the current
                    // face axis keeps head roll from becoming false X motion.
                    const float neckOffsetFromCenter = 0.65f;

                    center =
                        rigidCenter +
                        faceDown *
                        faceHeight *
                        neckOffsetFromCenter;
                }
                else
                {
                    center = rigidCenter;
                }

                float faceWidth =
                    Vector2.Distance(cheekA, cheekB);

                if (
                    faceWidth > 0.000001f &&
                    faceHeight > 0.000001f
                )
                {
                    // Geometric mean is less sensitive than eye distance to
                    // yaw/pitch, therefore depth does not pulse as strongly.
                    eyeSpan =
                        Mathf.Sqrt(faceWidth * faceHeight);
                }
            }

            // Compatibility fallback.
            if (
                eyeSpan <= 0.0001f &&
                count > 362
            )
            {
                Vector2 rightEyeCenter =
                    (
                        new Vector2(landmarks[33].x, landmarks[33].y) +
                        new Vector2(landmarks[133].x, landmarks[133].y)
                    ) * 0.5f;

                Vector2 leftEyeCenter =
                    (
                        new Vector2(landmarks[362].x, landmarks[362].y) +
                        new Vector2(landmarks[263].x, landmarks[263].y)
                    ) * 0.5f;

                center =
                    (rightEyeCenter + leftEyeCenter) * 0.5f;

                eyeSpan =
                    Vector2.Distance(rightEyeCenter, leftEyeCenter);
            }

            Quaternion rotation =
                Quaternion.identity;

            bool hasRotation =
                false;

            if (
                result.facialTransformationMatrixes != null &&
                result.facialTransformationMatrixes.Count > 0
            )
            {
                Matrix4x4 matrix =
                    result.facialTransformationMatrixes[0];

                rotation =
                    matrix.rotation;

                if (IsValidQuaternion(rotation))
                {
                    rotation =
                        NormalizeQuaternion(rotation);

                    hasRotation =
                        true;
                }
            }

            FaceExpressionData expression =
                ExtractExpressionData(result);

            lock (_trackingLock)
            {
                if (
                    _latestLandmarks == null ||
                    _latestLandmarks.Length < count
                )
                {
                    _latestLandmarks =
                        new Vector2[count];
                }

                for (int i = 0; i < count; i++)
                {
                    _latestLandmarks[i] =
                        new Vector2(
                            landmarks[i].x,
                            landmarks[i].y
                        );
                }

                _latestLandmarkCount = count;
                _latestLandmarkTimestamp = timestamp;
                _latestFaceCenter = center;
                _latestFaceEyeSpan = eyeSpan;
                _latestFaceRotation = rotation;
                _hasLatestFaceRotation = hasRotation;
                _latestMotionTimestamp = timestamp;
                _latestExpressionData = expression;
                _latestExpressionTimestamp =
                    expression.isValid ? timestamp : -1;
            }
        }


        // =====================================================
        // Blendshape Extraction
        // =====================================================

        private FaceExpressionData ExtractExpressionData(
            FaceLandmarkerResult result)
        {
            FaceExpressionData data =
                default;


            if (
                result.faceBlendshapes == null
                ||
                result.faceBlendshapes.Count == 0
                ||
                result.faceBlendshapes[0].categories == null
            )
            {
                return data;
            }


            var categories =
                result.faceBlendshapes[0].categories;


            for (
                int i = 0;
                i < categories.Count;
                i++
            )
            {
                var category =
                    categories[i];


                switch (category.categoryName)
                {
                    case "eyeBlinkLeft":
                        data.eyeBlinkLeft =
                            category.score;
                        break;


                    case "eyeBlinkRight":
                        data.eyeBlinkRight =
                            category.score;
                        break;


                    case "eyeWideLeft":
                        data.eyeWideLeft =
                            category.score;
                        break;


                    case "eyeWideRight":
                        data.eyeWideRight =
                            category.score;
                        break;


                    case "cheekSquintLeft":
                        data.cheekSquintLeft =
                            category.score;
                        break;


                    case "cheekSquintRight":
                        data.cheekSquintRight =
                            category.score;
                        break;


                    case "jawOpen":
                        data.jawOpen =
                            category.score;
                        break;


                    case "mouthSmileLeft":
                        data.mouthSmileLeft =
                            category.score;
                        break;


                    case "mouthSmileRight":
                        data.mouthSmileRight =
                            category.score;
                        break;


                    case "mouthFrownLeft":
                        data.mouthFrownLeft =
                            category.score;
                        break;


                    case "mouthFrownRight":
                        data.mouthFrownRight =
                            category.score;
                        break;


                    case "mouthPucker":
                        data.mouthPucker =
                            category.score;
                        break;


                    case "mouthFunnel":
                        data.mouthFunnel =
                            category.score;
                        break;


                    case "browInnerUp":
                        data.browInnerUp =
                            category.score;
                        break;


                    case "browDownLeft":
                        data.browDownLeft =
                            category.score;
                        break;


                    case "browDownRight":
                        data.browDownRight =
                            category.score;
                        break;
                }
            }


            data.isValid =
                true;


            return data;
        }


        // =====================================================
        // Clear
        // =====================================================

        private void ClearTrackingData()
        {
            lock (_trackingLock)
            {
                _latestLandmarkCount =
                    0;


                _latestLandmarkTimestamp =
                    -1;


                _latestFaceCenter =
                    new Vector2(
                        0.5f,
                        0.5f
                    );


                _latestFaceEyeSpan =
                    0f;


                _latestFaceRotation =
                    Quaternion.identity;


                _hasLatestFaceRotation =
                    false;


                _latestMotionTimestamp =
                    -1;


                _latestExpressionData =
                    default;


                _latestExpressionTimestamp =
                    -1;
            }
        }


        // =====================================================
        // Quaternion
        // =====================================================

        private bool IsValidQuaternion(
            Quaternion q)
        {
            return
                !float.IsNaN(q.x)
                &&
                !float.IsNaN(q.y)
                &&
                !float.IsNaN(q.z)
                &&
                !float.IsNaN(q.w)
                &&
                !float.IsInfinity(q.x)
                &&
                !float.IsInfinity(q.y)
                &&
                !float.IsInfinity(q.z)
                &&
                !float.IsInfinity(q.w);
        }


        private Quaternion NormalizeQuaternion(
            Quaternion q)
        {
            float magnitude =
                Mathf.Sqrt(
                    q.x * q.x
                    +
                    q.y * q.y
                    +
                    q.z * q.z
                    +
                    q.w * q.w
                );


            if (magnitude < 0.000001f)
            {
                return Quaternion.identity;
            }


            float inv =
                1f /
                magnitude;


            return new Quaternion(
                q.x * inv,
                q.y * inv,
                q.z * inv,
                q.w * inv
            );
        }
    }
}