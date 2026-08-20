#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

public static class KiwiOptimizationValidator
{
    private const string FaceRunnerPath = "Assets/Script/FaceLandmarkerRunner.cs";
    private const string FaceTemplatePath =
        "Assets/KiwiAvatarSystem/TrackingTemplates/FaceLandmarkerRunner_v3.5.0.cs.txt";
    private const string MotionPath = "Assets/Script/KiwiFaceMotion.cs";
    private const string MotionTemplatePath =
        "Assets/KiwiAvatarSystem/TrackingTemplates/KiwiFaceMotion_v3.5.0.cs.txt";

    [MenuItem("Kiwi VTuber/Optimization/Validate v1.0.0")]
    public static void ValidateMenu()
    {
        string report = RunValidation();
        EditorUtility.DisplayDialog("Kiwi Optimization Validation", report, "OK");
    }

    public static void RunBatchValidation()
    {
        string report = RunValidation();
        UnityEngine.Debug.Log(report);
    }

    [MenuItem("Kiwi VTuber/Optimization/Benchmark inference readback")]
    public static void RunInferenceReadbackBenchmark()
    {
        ModelAsset modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>(
            "Assets/KiwiAvatarSystem/Resources/KiwiFaceLandmarkInference.onnx"
        );
        Require(modelAsset != null, "imported ONNX ModelAsset is unavailable");

        InferenceBenchmarkResult dualFirst = MeasureInferenceReadback(
            modelAsset,
            false,
            false,
            12,
            80
        );
        InferenceBenchmarkResult parallelFirst = MeasureInferenceReadback(
            modelAsset,
            false,
            true,
            12,
            80
        );
        InferenceBenchmarkResult packedFirst = MeasureInferenceReadback(
            modelAsset,
            true,
            false,
            12,
            80
        );
        // Repeat in reverse order to cancel GPU clock and run-order bias.
        InferenceBenchmarkResult packedSecond = MeasureInferenceReadback(
            modelAsset,
            true,
            false,
            12,
            80
        );
        InferenceBenchmarkResult parallelSecond = MeasureInferenceReadback(
            modelAsset,
            false,
            true,
            12,
            80
        );
        InferenceBenchmarkResult dualSecond = MeasureInferenceReadback(
            modelAsset,
            false,
            false,
            12,
            80
        );
        InferenceBenchmarkResult dual = InferenceBenchmarkResult.Average(
            dualFirst,
            dualSecond
        );
        InferenceBenchmarkResult packed = InferenceBenchmarkResult.Average(
            packedFirst,
            packedSecond
        );
        InferenceBenchmarkResult parallel = InferenceBenchmarkResult.Average(
            parallelFirst,
            parallelSecond
        );
        double improvement = dual.meanMs > 0.0
            ? (dual.meanMs - packed.meanMs) / dual.meanMs * 100.0
            : 0.0;
        UnityEngine.Debug.Log(
            "[KiwiOptimization] Inference readback A/B: dual mean=" +
            dual.meanMs.ToString("F3") + " ms, p95=" +
            dual.p95Ms.ToString("F3") + " ms; parallel-request mean=" +
            parallel.meanMs.ToString("F3") + " ms, p95=" +
            parallel.p95Ms.ToString("F3") + " ms; packed mean=" +
            packed.meanMs.ToString("F3") + " ms, p95=" +
            packed.p95Ms.ToString("F3") + " ms; mean improvement=" +
            improvement.ToString("F1") + "%"
        );
    }

    public static string RunValidation()
    {
        List<string> failures = new List<string>();
        int checks = 0;

        Check("version alignment", failures, ref checks, () =>
        {
            Require(
                KiwiAvatarRuntimeManager.PackageVersion ==
                KiwiPrecisionTrackingInstaller.PrecisionVersion,
                "runtime and tracking package versions differ"
            );
            Require(
                KiwiAvatarRuntimeManager.PackageVersion == "1.0.0" &&
                PlayerSettings.bundleVersion == "1.0.0",
                "application version must remain fixed at 1.0.0"
            );
            Require(
                Application.unityVersion == "6000.0.80f1",
                "validation must run with Unity 6000.0.80f1"
            );
        });

        Check("portable MediaPipe package dependency", failures, ref checks, () =>
        {
            string manifest = File.ReadAllText(ToFullPath("Packages/manifest.json"));
            string lockFile = File.ReadAllText(ToFullPath("Packages/packages-lock.json"));
            const string portableReference =
                "file:com.github.homuler.mediapipe-0.16.3.tgz";
            Require(
                manifest.Contains(portableReference) &&
                lockFile.Contains(portableReference),
                "MediaPipe tarball must use a Packages-relative reference"
            );
            Require(
                !manifest.Contains("file:D:") &&
                !lockFile.Contains("file:D:") &&
                File.Exists(ToFullPath(
                    "Packages/com.github.homuler.mediapipe-0.16.3.tgz"
                )),
                "MediaPipe dependency is machine-specific or its tarball is missing"
            );
        });

        Check("transactional avatar hot swap", failures, ref checks, () =>
        {
            string manager = File.ReadAllText(ToFullPath(
                "Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimeManager.cs"
            ));
            int configureIndex = manager.IndexOf(
                "ApplyActiveProfile();",
                manager.IndexOf("public async void SwitchToModel", StringComparison.Ordinal),
                StringComparison.Ordinal
            );
            int commitIndex = manager.IndexOf(
                "swapCommitted = true;",
                StringComparison.Ordinal
            );
            int releaseCandidateIndex = manager.IndexOf(
                "candidate = null;",
                commitIndex,
                StringComparison.Ordinal
            );
            Require(
                manager.Contains("private struct ActiveAvatarState") &&
                manager.Contains("CaptureActiveAvatarState()") &&
                manager.Contains("RestoreActiveAvatarState(previousState)") &&
                manager.Contains("TryRestorePreviousAvatarPresentation(previousState)") &&
                manager.Contains("candidateBound && !swapCommitted") &&
                configureIndex >= 0 &&
                commitIndex > configureIndex &&
                releaseCandidateIndex > commitIndex,
                "avatar candidate must remain rollback-owned until configuration succeeds"
            );
        });

        Check("atomic tracking frame identity", failures, ref checks, () =>
        {
            string runner = File.ReadAllText(ToFullPath(FaceRunnerPath));
            string motion = File.ReadAllText(ToFullPath(MotionPath));
            Require(
                runner.Contains("public ulong frameId") &&
                runner.Contains("public KiwiTrackingBackend backend") &&
                runner.Contains("++_nextPublishedTrackingFrameId") &&
                runner.Contains("KiwiTrackingBackend.MediaPipe") &&
                runner.Contains("KiwiTrackingBackend.InferenceEngine") &&
                runner.Contains("lock (_trackingLock)") &&
                runner.Contains("_latestPrecisionData =") &&
                motion.Contains("IsNewPrecisionFrame") &&
                motion.Contains("_lastObservedFrameId") &&
                motion.Contains("bool backendChanged") &&
                motion.Contains("_lastAcceptedBackend"),
                "pose snapshots need one atomic frame ID and an explicit backend"
            );
        });

        Check("face-part response documentation alignment", failures, ref checks, () =>
        {
            string mask = File.ReadAllText(ToFullPath(
                "Assets/Script/FacePartShapeMask.cs"
            ));
            string readme = File.ReadAllText(ToFullPath(
                "Assets/KiwiAvatarSystem/README.txt"
            ));
            Require(
                mask.Contains("validated 110 default") &&
                mask.Contains("contourRenderResponse =") &&
                mask.Contains("110f;") &&
                readme.Contains("signal-specific 110-200 response") &&
                readme.Contains("contour default is the validated 110"),
                "contour response tooltip, README, and runtime default disagree"
            );
        });

        Check("Inference Engine hybrid tracking architecture", failures, ref checks, () =>
        {
            const string sentisTrackerPath =
                "Assets/Script/KiwiInferenceFaceTracker.cs";
            const string sentisModelPath =
                "Assets/KiwiAvatarSystem/Resources/KiwiFaceLandmarkInference.onnx";
            const string sentisShaderPath =
                "Assets/KiwiAvatarSystem/Resources/KiwiInferenceFaceCrop.shader";
            const string sentisLicensePath =
                "Assets/KiwiAvatarSystem/ThirdParty/FaceLandmarkONNX_LICENSE.txt";

            string runner = File.ReadAllText(ToFullPath(FaceRunnerPath));
            string tracker = File.ReadAllText(ToFullPath(sentisTrackerPath));
            string shader = File.ReadAllText(ToFullPath(sentisShaderPath));
            string manifest = File.ReadAllText(ToFullPath("Packages/manifest.json"));
            string license = File.ReadAllText(ToFullPath(sentisLicensePath));
            FileInfo model = new FileInfo(ToFullPath(sentisModelPath));

            Require(
                manifest.Contains("\"com.unity.ai.inference\"") &&
                manifest.Contains("\"2.4.1\""),
                "Unity 6 Inference Engine dependency is missing or changed"
            );
            Require(
                model.Exists && model.Length > 2000000L,
                "Inference Engine face-landmark model is missing or unexpectedly small"
            );
            Require(
                tracker.Contains("using Unity.InferenceEngine") &&
                tracker.Contains("new Worker(model, BackendType.GPUCompute)") &&
                tracker.Contains("_worker.Schedule(_input)") &&
                tracker.Contains("conv2d_20") &&
                tracker.Contains("CompatibleLandmarkCount = 478") &&
                tracker.Contains("_consecutiveFailures >= 4"),
                "GPU inference, model outputs, 478 compatibility, or failure hysteresis is missing"
            );
            Require(
                runner.Contains("enableSentisHybridTracking = true") &&
                runner.Contains("sentisMediaPipeRefreshRateHz = 10") &&
                runner.Contains("MediaPipe fallback remains active"),
                "hybrid defaults or MediaPipe fallback is missing"
            );
            Require(
                shader.Contains("Hidden/KiwiAvatar/InferenceFaceCrop"),
                "GPU crop shader is missing"
            );
            Require(
                AssetDatabase.LoadAssetAtPath<Shader>(sentisShaderPath) != null,
                "GPU crop shader is not imported as a Shader asset"
            );
            Require(
                license.Contains("Apache License") &&
                license.Contains("FaceLandmarkBarracuda") &&
                license.Contains("google-ai-edge/mediapipe"),
                "third-party model attribution is missing"
            );
        });

        Check("Inference Engine GPU model smoke test", failures, ref checks, () =>
        {
            Require(
                SystemInfo.supportsComputeShaders,
                "GPUCompute smoke test requires compute-shader support"
            );

            ModelAsset modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>(
                "Assets/KiwiAvatarSystem/Resources/KiwiFaceLandmarkInference.onnx"
            );
            Require(modelAsset != null, "imported ONNX ModelAsset is unavailable");

            Model model = ModelLoader.Load(modelAsset);
            using Worker worker = new Worker(model, BackendType.GPUCompute);
            using Tensor<float> input = new Tensor<float>(
                new TensorShape(1, 3, 192, 192)
            );
            worker.Schedule(input);

            Tensor<float> gpuLandmarks =
                worker.PeekOutput("conv2d_20") as Tensor<float>;
            Tensor<float> gpuPresence =
                worker.PeekOutput("conv2d_30") as Tensor<float>;
            Require(
                gpuLandmarks != null && gpuPresence != null,
                "expected landmark or presence output is unavailable"
            );

            using Tensor<float> landmarks = gpuLandmarks.ReadbackAndClone();
            using Tensor<float> presence = gpuPresence.ReadbackAndClone();
            Require(
                landmarks.shape.length == 1404,
                "468x3 output length is " + landmarks.shape.length
            );
            Require(
                presence.shape.length == 1,
                "presence output length is " + presence.shape.length
            );
            Require(
                !float.IsNaN(presence[0]) && !float.IsInfinity(presence[0]),
                "presence output is not finite"
            );

            float[] expected = new float[1405];
            for (int i = 0; i < 1404; i++)
            {
                expected[i] = landmarks[i];
            }
            expected[1404] = presence[0];

            Model packedModel = KiwiInferenceFaceTracker.BuildSingleReadbackModel(
                ModelLoader.Load(modelAsset)
            );
            using Worker packedWorker = new Worker(
                packedModel,
                BackendType.GPUCompute
            );
            packedWorker.Schedule(input);
            Tensor<float> gpuPacked = packedWorker.PeekOutput(0) as Tensor<float>;
            Require(gpuPacked != null, "packed inference output is unavailable");
            using Tensor<float> packed = gpuPacked.ReadbackAndClone();
            Require(
                packed.shape.length == 1405,
                "packed output length is " + packed.shape.length
            );
            float maximumDifference = 0f;
            for (int i = 0; i < expected.Length; i++)
            {
                maximumDifference = Mathf.Max(
                    maximumDifference,
                    Mathf.Abs(expected[i] - packed[i])
                );
            }
            Require(
                maximumDifference <= 0.0001f,
                "packed output changed inference values; max delta=" +
                maximumDifference
            );
        });

        Check("single-sync inference readback", failures, ref checks, () =>
        {
            string tracker = File.ReadAllText(ToFullPath(
                "Assets/Script/KiwiInferenceFaceTracker.cs"
            ));
            Require(
                tracker.Contains("BuildSingleReadbackModel") &&
                tracker.Contains("Functional.Concat") &&
                tracker.Contains("packedOutput.ReadbackAndClone()") &&
                CountOccurrences(tracker, "ReadbackAndClone()") == 1 &&
                !tracker.Contains("ReadbackAndCloneAsync"),
                "live inference path must preserve same-frame data with one GPU/CPU synchronization"
            );
        });

        Check("predictive tracking versus 1Euro", failures, ref checks, () =>
        {
            KiwiTrackingStrategyResult comparison =
                KiwiTrackingStrategyEvaluator.Compare();
            Require(
                comparison.winner == KiwiTrackingStrategy.PredictiveHybrid,
                "1Euro unexpectedly outscored the predictive hybrid; review defaults before switching"
            );
            Require(
                comparison.predictiveMotionRmse < comparison.oneEuroMotionRmse,
                "predictive hybrid did not reduce movement tracking error"
            );
            UnityEngine.Debug.Log(
                "[KiwiOptimization] Tracking A/B: predictive=" +
                comparison.predictiveScore.ToString("F6") +
                ", 1Euro=" + comparison.oneEuroScore.ToString("F6") +
                ", winner=" + comparison.winner
            );
        });

        Check("latest-result acceptance strategy", failures, ref checks, () =>
        {
            KiwiResultAcceptanceComparison comparison =
                KiwiResultAcceptanceStrategyEvaluator.Compare();
            Require(
                comparison.winner == KiwiResultAcceptanceStrategy.QualityGatedDirect,
                "quality-gated direct acceptance did not beat whole-pose hold, immediate raw, and fixed bounded input"
            );

            for (int i = 0; i < comparison.scores.Length; i++)
            {
                KiwiResultAcceptanceScore score = comparison.scores[i];
                UnityEngine.Debug.Log(
                    "[KiwiOptimization] Result acceptance: " + score.strategy +
                    " total=" + score.total.ToString("F6") +
                    " motion=" + score.motionRmse.ToString("F6") +
                    " jitter=" + score.restJitter.ToString("F6")
                );
            }

            string motionSource = File.ReadAllText(ToFullPath(MotionPath));
            string sceneSource = File.ReadAllText(ToFullPath(
                "Assets/Scenes/Face Landmark Detection.unity"
            ));
            string panelSource = File.ReadAllText(ToFullPath(
                "Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs"
            ));
            Require(
                motionSource.Contains("useBoundedLatestResultCorrection = true") &&
                motionSource.Contains("ApplyQualityGatedLatestResultCorrection") &&
                motionSource.Contains("Quaternion.RotateTowards") &&
                motionSource.Contains("Vector2.MoveTowards") &&
                motionSource.Contains("Mathf.MoveTowards") &&
                motionSource.Contains("lowQuality &&") &&
                motionSource.Contains("bool correctionBacklog") &&
                motionSource.Contains("!useBoundedLatestResultCorrection &&") &&
                sceneSource.Contains("useBoundedLatestResultCorrection: 1") &&
                panelSource.Contains("Direct high-quality motion / bound low-quality spikes") &&
                panelSource.Contains("\"BoundLatest\"") &&
                panelSource.Contains("PrecisionBoundedCorrectionCount"),
                "quality-gated direct-result runtime path, diagnostics, or persistence is incomplete"
            );
        });

        Check("face-part depth flicker strategy", failures, ref checks, () =>
        {
            KiwiFacePartRenderComparison comparison =
                KiwiFacePartRenderStrategyEvaluator.Compare();
            Require(
                comparison.winner == KiwiFacePartRenderStrategy.DepthIndependentPoseGate,
                "depth-independent coherent pose gate did not win the face-part flicker comparison"
            );

            for (int i = 0; i < comparison.scores.Length; i++)
            {
                KiwiFacePartRenderScore score = comparison.scores[i];
                UnityEngine.Debug.Log(
                    "[KiwiOptimization] Face-part render: " + score.strategy +
                    " total=" + score.total.ToString("F2") +
                    " flicker=" + score.flickerResistance.ToString("F1") +
                    " latency=" + score.latency.ToString("F1") +
                    " occlusion=" + score.occlusionSafety.ToString("F1") +
                    " safety=" + score.implementationSafety.ToString("F1")
                );
            }

            string maskSource = File.ReadAllText(ToFullPath(
                "Assets/Script/FacePartShapeMask.cs"
            ));
            string shaderSource = File.ReadAllText(ToFullPath(
                "Assets/Shader/FacePartSoftMask.shader"
            ));
            Require(
                maskSource.Contains("stabilizeSurfaceOcclusion") &&
                maskSource.Contains("RenderedYawDegrees") &&
                maskSource.Contains("CalculatePoseVisibility") &&
                maskSource.Contains("Application.onBeforeRender") &&
                shaderSource.Contains("_PoseVisibility") &&
                shaderSource.Contains("ZTest Always"),
                "coherent face-part pose gate or depth-independent shader path is incomplete"
            );

            RequireNear(
                KiwiFacePartVisibilityMath.CalculatePoseVisibility(48f, 48f, 58f),
                1f,
                0.000001f,
                "face-part full-visibility yaw"
            );
            RequireNear(
                KiwiFacePartVisibilityMath.CalculatePoseVisibility(-58f, 48f, 58f),
                0f,
                0.000001f,
                "face-part hidden yaw"
            );
            float transition =
                KiwiFacePartVisibilityMath.CalculatePoseVisibility(53f, 48f, 58f);
            Require(
                transition > 0f && transition < 1f,
                "face-part pose gate has no continuous transition band"
            );
        });

        Check("native face-effect strategy comparison", failures, ref checks, () =>
        {
            KiwiFaceEffectStrategyResult comparison =
                KiwiFaceEffectStrategyEvaluator.Compare();
            Require(
                comparison.winner == KiwiFaceEffectStrategy.NativeMediaPipeGpu,
                "an external or CPU mesh effect path unexpectedly outscored the native GPU path"
            );

            float nativeScore = -1f;
            float currentScore = -1f;
            for (int i = 0; i < comparison.scores.Length; i++)
            {
                KiwiFaceEffectStrategyScore score = comparison.scores[i];
                if (score.strategy == KiwiFaceEffectStrategy.NativeMediaPipeGpu)
                {
                    nativeScore = score.Total;
                }
                else if (score.strategy == KiwiFaceEffectStrategy.CurrentMeshUv)
                {
                    currentScore = score.Total;
                }

                UnityEngine.Debug.Log(
                    "[KiwiOptimization] Face effect: " + score.strategy +
                    " total=" + score.Total.ToString("F1") +
                    " latency=" + score.latency.ToString("F1") +
                    " precision=" + score.precision.ToString("F1") +
                    " stability=" + score.stability.ToString("F1") +
                    " portability=" + score.portability.ToString("F1") +
                    " maintainability=" + score.maintainability.ToString("F1")
                );
            }
            Require(nativeScore > currentScore, "GPU shader path did not improve on current mesh UV updates");

            float largeStep = KiwiNativeFaceEffectMath.AdvanceAmount(
                0f, 0.50f, 1f / 120f, 72f, 0.24f, 0.004f
            );
            Require(
                largeStep > 0.15f && largeStep < 0.50f,
                "large mouth deformation is either delayed excessively or still snaps in one frame"
            );

            float oneStep = KiwiNativeFaceEffectMath.AdvanceAmount(
                0f, 0.10f, 1f / 60f, 72f, 0.24f, 0f
            );
            float twoSteps = 0f;
            for (int i = 0; i < 2; i++)
            {
                twoSteps = KiwiNativeFaceEffectMath.AdvanceAmount(
                    twoSteps, 0.10f, 1f / 120f, 72f, 0.24f, 0f
                );
            }
            Require(
                Mathf.Abs(oneStep - twoSteps) < 0.004f &&
                oneStep > 0.05f &&
                oneStep < 0.10f,
                "adaptive mouth deformation is cadence-dependent, delayed, or discontinuous"
            );
            RequireNear(
                KiwiNativeFaceEffectMath.AdvanceAmount(
                    0.50f, 0.504f, 1f / 60f, 180f, 0.20f, 0.006f
                ),
                0.50f,
                0.000001f,
                "rest jitter was not held"
            );

            Vector2 openOnly = KiwiMouthShapeBlendMath.CalculateCoherentZoom(
                1f,
                0f,
                0f,
                new Vector2(2f, 2f),
                new Vector2(1.25f, 1.15f),
                new Vector2(2.6f, 1.35f)
            );
            RequireNear(openOnly.x, 2f, 0.000001f, "open-only mouth width");
            RequireNear(openOnly.y, 2f, 0.000001f, "open-only mouth height");

            Vector2 openSmile = KiwiMouthShapeBlendMath.CalculateCoherentZoom(
                1f,
                0f,
                1f,
                new Vector2(2f, 2f),
                new Vector2(1.25f, 1.15f),
                new Vector2(2.6f, 1.35f)
            );
            RequireNear(openSmile.x, 2.3f, 0.000001f, "coherent open-smile width");
            RequireNear(openSmile.y, 1.675f, 0.000001f, "coherent open-smile height");
            Require(
                openSmile.x < 2.6f && openSmile.y < 2f,
                "mouth blend still combines independent axis maxima"
            );

            string reactionSource = File.ReadAllText(
                ToFullPath("Assets/Script/KiwiExpressionReaction.cs")
            );
            string lockSource = File.ReadAllText(
                ToFullPath("Assets/Script/MouthDisplaySizeLock.cs")
            );
            string shapeSource = File.ReadAllText(
                ToFullPath("Assets/Script/FacePartShapeMask.cs")
            );
            string surfaceSource = File.ReadAllText(
                ToFullPath("Assets/Script/SurfaceFittedRawImage.cs")
            );
            string sceneSource = File.ReadAllText(
                ToFullPath("Assets/Scenes/Face Landmark Detection.unity")
            );
            string panelSource = File.ReadAllText(
                ToFullPath("Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs")
            );
            Require(
                reactionSource.Contains("mouthDisplaySizeLock.SetExpressionZoom") &&
                reactionSource.Contains("KiwiNativeFaceEffectMath.AdvanceAmount") &&
                reactionSource.Contains("LimitMouthZoomAgainstEyes") &&
                reactionSource.Contains("eyeBaseDisplayScaleX = 1.18f") &&
                reactionSource.Contains("eyeBaseDisplayScaleY = 1.25f") &&
                reactionSource.Contains("mouthLayoutPositionY = -400f") &&
                reactionSource.Contains("adaptiveMultiplier") &&
                reactionSource.Contains("SetVisibleScale") &&
                reactionSource.Contains("mouthOpenMaxZoomX = 2.00f") &&
                reactionSource.Contains("mouthSmileMaxZoomX = 2.60f") &&
                reactionSource.Contains("KiwiMouthShapeBlendMath.CalculateCoherentZoom") &&
                reactionSource.Contains("mouthOpenStart = 0.06f") &&
                reactionSource.Contains("smileStart = 0.10f") &&
                reactionSource.Contains("mouthEyeSafetyMarginPixels = 14f") &&
                lockSource.Contains("expressionZoomX") &&
                lockSource.Contains("(limitWidth ? inverseScale : 1.00f) /") &&
                shapeSource.Contains("TryGetRenderedContourScreenRect") &&
                shapeSource.Contains("public void SetVisibleScale") &&
                shapeSource.Contains("sampleUv - offset - pivot") &&
                surfaceSource.Contains("TryGetSurfaceLocalPosition") &&
                sceneSource.Contains("mouthDisplaySizeLock: {fileID: 553149540}") &&
                sceneSource.Contains("m_AnchoredPosition: {x: 0, y: -400}") &&
                sceneSource.Contains("eyeBaseDisplayScaleX: 1.18") &&
                sceneSource.Contains("eyeBaseDisplayScaleY: 1.25") &&
                sceneSource.Contains("mouthLayoutPositionY: -400") &&
                sceneSource.Contains("maximumVisibleScale: 0.78") &&
                sceneSource.Contains("mouthOpenMaxZoomX: 2") &&
                sceneSource.Contains("mouthSmileMaxZoomX: 2.6") &&
                sceneSource.Contains("mouthOpenStart: 0.06") &&
                sceneSource.Contains("mouthOpenFull: 0.5") &&
                sceneSource.Contains("smileStart: 0.1") &&
                sceneSource.Contains("smileFull: 0.5") &&
                sceneSource.Contains("preventMouthEyeOverlap: 1") &&
                sceneSource.Contains("mouthEyeSafetyMarginPixels: 14") &&
                panelSource.Contains("Native GPU Big Mouth") &&
                panelSource.Contains("Reference eye proportions") &&
                panelSource.Contains("Mouth vertical placement") &&
                panelSource.Contains("Mouth open start") &&
                panelSource.Contains("Smile full") &&
                panelSource.Contains("Smile width") &&
                panelSource.Contains("Prevent mouth / eye overlap") &&
                panelSource.Contains("KiwiTrack.MouthEyeMarginV3") &&
                panelSource.Contains("KiwiTrack.EyeDisplayScaleYV4") &&
                panelSource.Contains("KiwiTrack.MouthLayoutPositionYV1") &&
                panelSource.Contains("KiwiTrack.MouthOpenStartV1") &&
                panelSource.Contains("KiwiTrack.SmileFullV1") &&
                panelSource.Contains("KiwiTrack.MouthEffectResponseV3"),
                "the selected MediaPipe-to-GPU path is not fully wired or adjustable"
            );
        });

        Check("arbitrary mesh raycast normal fit", failures, ref checks, () =>
        {
            string fitterSource = File.ReadAllText(
                ToFullPath("Assets/Script/KiwiSurfaceFitter.cs")
            );
            string managerSource = File.ReadAllText(
                ToFullPath("Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimeManager.cs")
            );
            Require(
                fitterSource.Contains("targetRenderer.BakeMesh") &&
                fitterSource.Contains("MeshCollider") &&
                fitterSource.Contains("hit.normal") &&
                fitterSource.Contains("FindBestFaceRenderer") &&
                fitterSource.Contains("LastSuccessRate") &&
                managerSource.Contains("ApplySurfaceFit") &&
                managerSource.Contains("Raycast + Normal"),
                "raycast/normal arbitrary-mesh fit is not the preferred runtime path"
            );
        });

        Check("host timing conversion", failures, ref checks, () =>
        {
            double seconds = KiwiPrecisionTrackingMath.HostTicksToSeconds(
                Stopwatch.Frequency
            );
            Require(Math.Abs(seconds - 1.0) < 0.000001, "tick conversion is not monotonic seconds");
        });

        Check("avatar-right yaw", failures, ref checks, () =>
        {
            Vector3 mirrored = KiwiPrecisionTrackingMath.CalculateAvatarEulerDegrees(
                Quaternion.identity,
                Quaternion.Euler(0f, -20f, 0f),
                true
            );
            Vector3 unmirrored = KiwiPrecisionTrackingMath.CalculateAvatarEulerDegrees(
                Quaternion.identity,
                Quaternion.Euler(0f, 20f, 0f),
                false
            );
            RequireNear(mirrored.y, 20f, 0.05f, "mirrored right yaw sign");
            RequireNear(unmirrored.y, 20f, 0.05f, "unmirrored right yaw sign");
        });

        Check("avatar-right roll", failures, ref checks, () =>
        {
            Vector3 mirrored = KiwiPrecisionTrackingMath.CalculateAvatarEulerDegrees(
                Quaternion.identity,
                Quaternion.Euler(0f, 0f, -20f),
                true
            );
            Vector3 unmirrored = KiwiPrecisionTrackingMath.CalculateAvatarEulerDegrees(
                Quaternion.identity,
                Quaternion.Euler(0f, 0f, 20f),
                false
            );
            RequireNear(mirrored.z, 20f, 0.05f, "mirrored right roll sign");
            RequireNear(unmirrored.z, 20f, 0.05f, "unmirrored right roll sign");
        });

        Check("combined right/down mapping", failures, ref checks, () =>
        {
            Vector3 mirrored = KiwiPrecisionTrackingMath.CalculateAvatarEulerDegrees(
                Quaternion.identity,
                Quaternion.Euler(12f, -18f, -10f),
                true
            );
            Vector3 unmirrored = KiwiPrecisionTrackingMath.CalculateAvatarEulerDegrees(
                Quaternion.identity,
                Quaternion.Euler(12f, 18f, 10f),
                false
            );
            RequireNear(mirrored.x, 12f, 0.05f, "mirrored down pitch amount");
            RequireNear(mirrored.y, 18f, 0.05f, "mirrored combined right yaw sign");
            RequireNear(mirrored.z, 10f, 0.05f, "mirrored combined right roll sign");
            RequireNear(unmirrored.x, 12f, 0.05f, "unmirrored down pitch amount");
            RequireNear(unmirrored.y, 18f, 0.05f, "unmirrored combined right yaw sign");
            RequireNear(unmirrored.z, 10f, 0.05f, "unmirrored combined right roll sign");
        });

        Check("avatar-own-right translation", failures, ref checks, () =>
        {
            Vector2 mirrored = KiwiPrecisionTrackingMath.CalculateAvatarCentricPositionDelta(
                new Vector2(0.25f, 0f),
                true
            );
            Vector2 unmirrored = KiwiPrecisionTrackingMath.CalculateAvatarCentricPositionDelta(
                new Vector2(-0.25f, 0f),
                false
            );
            Require(mirrored.x < 0f, "mirrored tracked right did not map to Kiwi-own-right");
            Require(unmirrored.x < 0f, "unmirrored tracked right did not map to Kiwi-own-right");
            RequireNear(mirrored.y, 0f, 0.000001f, "mirrored mapping changed vertical movement");
            RequireNear(unmirrored.y, 0f, 0.000001f, "unmirrored mapping changed vertical movement");

            string runnerSource = File.ReadAllText(ToFullPath(FaceRunnerPath));
            string motionSource = File.ReadAllText(ToFullPath(MotionPath));
            Require(
                runnerSource.Contains("IsInputHorizontallyMirrored") &&
                runnerSource.Contains("IsInputHorizontallyMirrored =") &&
                motionSource.Contains("runner.IsInputHorizontallyMirrored"),
                "actual MediaPipe input mirror state is not wired into motion mapping"
            );
        });

        Check("prediction cap", failures, ref checks, () =>
        {
            Quaternion predicted = KiwiPrecisionTrackingMath.ExtrapolateRotation(
                Quaternion.identity,
                new Vector3(0f, 100f, 0f),
                0.10f,
                3f
            );
            RequireNear(
                Quaternion.Angle(Quaternion.identity, predicted),
                3f,
                0.01f,
                "rotation prediction exceeded cap"
            );
        });

        Check("display-rate smoothing invariance", failures, ref checks, () =>
        {
            const double response = 48.0;
            double at60 = ApplyExponentialSteps(response, 1.0 / 60.0, 6);
            double at120 = ApplyExponentialSteps(response, 1.0 / 120.0, 12);
            Require(Math.Abs(at60 - at120) < 0.000001, "smoothing changes with render frame rate");
            Require(at60 > 0.98 && at60 < 1.0, "display response is too slow or snaps");

            string motionSource = File.ReadAllText(ToFullPath(MotionPath));
            Require(
                motionSource.Contains("UpdateAndRenderDisplayPose(dt)") &&
                motionSource.Contains("ultraDisplayRateSmoothing") &&
                !motionSource.Contains("RenderRotation(_sampleRotation)"),
                "sample-and-hold render path is still active"
            );
        });

        Check("zero-lag intentional motion", failures, ref checks, () =>
        {
            double previousLateSampleFraction =
                1.0 - Math.Exp(-110.0 / 240.0);
            Require(
                previousLateSampleFraction > 0.36 &&
                previousLateSampleFraction < 0.38,
                "previous late-sample partial-step baseline changed unexpectedly"
            );
            Require(
                KiwiUltraDisplayMath.ShouldApplyDirectMotion(20f, 3f, 1f, 0.08f),
                "intentional rotation did not bypass display smoothing"
            );
            Require(
                !KiwiUltraDisplayMath.ShouldApplyDirectMotion(2f, 3f, 1f, 0.08f),
                "rest motion bypassed display smoothing"
            );
            Require(
                !KiwiUltraDisplayMath.ShouldApplyDirectMotion(20f, 3f, 0.04f, 0.08f),
                "microscopic display error bypassed smoothing"
            );
            Require(
                !KiwiUltraDisplayMath.ShouldApplyDirectMotion(float.NaN, 3f, 1f, 0.08f),
                "non-finite motion was accepted"
            );

            Vector3 steadyPosition = KiwiUltraDisplayMath.AdvancePredictivePosition(
                Vector3.zero,
                new Vector3(0.01f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                0.01f,
                45f
            );
            RequireNear(steadyPosition.x, 0.01f, 0.000001f, "steady movement feed-forward lagged");

            Vector3 correctedPosition = KiwiUltraDisplayMath.AdvancePredictivePosition(
                Vector3.zero,
                new Vector3(0.01f, 0f, 0f),
                Vector3.zero,
                0.01f,
                45f
            );
            Require(
                correctedPosition.x > 0f && correctedPosition.x < 0.01f,
                "new position correction snapped instead of blending"
            );

            Vector3 oneStep = KiwiUltraDisplayMath.AdvancePredictivePosition(
                Vector3.zero,
                new Vector3(0.01f, 0f, 0f),
                Vector3.zero,
                0.02f,
                45f
            );
            Vector3 twoSteps = Vector3.zero;
            for (int i = 0; i < 2; i++)
            {
                twoSteps = KiwiUltraDisplayMath.AdvancePredictivePosition(
                    twoSteps,
                    new Vector3(0.01f, 0f, 0f),
                    Vector3.zero,
                    0.01f,
                    45f
                );
            }
            RequireNear(oneStep.x, twoSteps.x, 0.000001f, "position correction changed with render cadence");

            string motionSource = File.ReadAllText(ToFullPath(MotionPath));
            string sceneSource = File.ReadAllText(ToFullPath("Assets/Scenes/Face Landmark Detection.unity"));
            string panelSource = File.ReadAllText(ToFullPath("Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs"));

            Rect coherenceRect = new Rect(0.20f, 0.30f, 0.20f, 0.10f);
            Vector2 clampedLocal = KiwiFacePartMaskCoherenceMath.ToCropLocal(
                new Vector2(-1f, 2f),
                coherenceRect,
                0.05f
            );
            Vector2 remappedPoint = KiwiFacePartMaskCoherenceMath.FromCropLocal(
                clampedLocal,
                coherenceRect
            );

            Require(
                clampedLocal.x >= 0.05f && clampedLocal.x <= 0.95f &&
                clampedLocal.y >= 0.05f && clampedLocal.y <= 0.95f &&
                remappedPoint.x > coherenceRect.xMin && remappedPoint.x < coherenceRect.xMax &&
                remappedPoint.y > coherenceRect.yMin && remappedPoint.y < coherenceRect.yMax,
                "crop-local mask safety margin does not keep contours inside the moving crop"
            );
            Require(
                motionSource.Contains("ApplyZeroLagMotionTarget") &&
                motionSource.Contains("ConsumeDisplayAdvanceDelta") &&
                motionSource.Contains("AdvancePredictivePosition") &&
                motionSource.Contains("ultraStaticPoseLock = true") &&
                motionSource.Contains("ultraStaticLockSeconds = 0.065f") &&
                motionSource.Contains("ultraRotationDeadZone = 0.18f") &&
                motionSource.Contains("ultraPositionDeadZone = 0.00060f") &&
                motionSource.Contains("ultraScaleDeadZone = 0.00150f") &&
                motionSource.Contains("ultraDisplaySmoothingResponse = 90f") &&
                motionSource.Contains("ultraDirectDisplayDuringMotion = true") &&
                motionSource.Contains("ultraDirectDisplayRotationSpeed = 18f") &&
                motionSource.Contains("ultraDirectDisplayPositionSpeed = 0.035f") &&
                motionSource.Contains("ultraDirectDisplayScaleSpeed = 0.200f") &&
                motionSource.Contains("ultraPredictivePositionResampling = true") &&
                sceneSource.Contains("ultraStaticPoseLock: 1") &&
                sceneSource.Contains("ultraStaticLockSeconds: 0.065") &&
                sceneSource.Contains("ultraRotationDeadZone: 0.18") &&
                sceneSource.Contains("ultraPositionDeadZone: 0.0006") &&
                sceneSource.Contains("ultraScaleDeadZone: 0.0015") &&
                sceneSource.Contains("ultraDisplaySmoothingResponse: 90") &&
                sceneSource.Contains("ultraDirectDisplayDuringMotion: 1") &&
                sceneSource.Contains("ultraDirectDisplayRotationSpeed: 18") &&
                sceneSource.Contains("ultraDirectDisplayPositionSpeed: 0.035") &&
                sceneSource.Contains("ultraDirectDisplayScaleSpeed: 0.2") &&
                sceneSource.Contains("ultraPredictivePositionResampling: 1") &&
                panelSource.Contains("Zero-jitter rest pose lock") &&
                panelSource.Contains("RestStabilityV2") &&
                panelSource.Contains("RestSafeDirectRotationV2") &&
                panelSource.Contains("RestDisplayResponseV2") &&
                panelSource.Contains("Flicker-free predictive movement") &&
                panelSource.Contains("Fresh camera frames only") &&
                panelSource.Contains("Single in-flight (lower load, more latency)") &&
                panelSource.Contains("Render \" + _smoothedRenderFrameRate") &&
                motionSource.Contains("ultraPositionCorrectionResponse = 45f") &&
                sceneSource.Contains("ultraPositionCorrectionResponse: 45") &&
                panelSource.Contains("TrackingSettings.v12.Saved"),
                "render-boundary zero-lag path or safe defaults are incomplete"
            );
        });

        Check("stop and reversal recovery", failures, ref checks, () =>
        {
            float steadyResponse = KiwiUltraDisplayMath.CalculateAdaptiveCorrectionResponse(
                45f,
                180f,
                1f
            );
            float reversalResponse = KiwiUltraDisplayMath.CalculateAdaptiveCorrectionResponse(
                45f,
                180f,
                0f
            );
            Vector3 steady = KiwiUltraDisplayMath.AdvancePredictivePosition(
                Vector3.zero,
                Vector3.one,
                Vector3.zero,
                1f / 120f,
                steadyResponse
            );
            Vector3 reversal = KiwiUltraDisplayMath.AdvancePredictivePosition(
                Vector3.zero,
                Vector3.one,
                Vector3.zero,
                1f / 120f,
                reversalResponse
            );
            string motionSource = File.ReadAllText(ToFullPath(MotionPath));
            string sceneSource = File.ReadAllText(ToFullPath("Assets/Scenes/Face Landmark Detection.unity"));
            string panelSource = File.ReadAllText(ToFullPath("Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs"));
            RequireNear(steadyResponse, 45f, 0.0001f, "steady movement correction changed");
            RequireNear(reversalResponse, 180f, 0.0001f, "reversal recovery response changed");
            Require(
                Vector3.Distance(reversal, Vector3.one) <
                    Vector3.Distance(steady, Vector3.one) * 0.40f &&
                motionSource.Contains("ultraPositionRecoveryResponse = 180f") &&
                motionSource.Contains("_predictionPositionConsistency") &&
                sceneSource.Contains("ultraPositionRecoveryResponse: 180") &&
                panelSource.Contains("Stop / reversal recovery") &&
                panelSource.Contains("KiwiTrack.PositionRecovery"),
                "stop/reversal motion does not converge substantially faster than steady resampling"
            );
        });

        Check("consistency-adaptive velocity estimation", failures, ref checks, () =>
        {
            float noisyResponse = KiwiUltraDisplayMath.CalculateAdaptiveVelocityResponse(
                60f,
                180f,
                0f
            );
            float onsetResponse = KiwiUltraDisplayMath.CalculateAdaptiveVelocityResponse(
                60f,
                180f,
                0.70f
            );
            float steadyResponse = KiwiUltraDisplayMath.CalculateAdaptiveVelocityResponse(
                60f,
                180f,
                1f
            );
            float baseFirst = 1f - Mathf.Exp(-60f / 60f);
            float adaptiveFirst = 1f - Mathf.Exp(-onsetResponse / 60f);
            float adaptiveSecond = adaptiveFirst +
                (1f - adaptiveFirst) * (1f - Mathf.Exp(-steadyResponse / 60f));
            string motionSource = File.ReadAllText(ToFullPath(MotionPath));
            string sceneSource = File.ReadAllText(ToFullPath("Assets/Scenes/Face Landmark Detection.unity"));
            string panelSource = File.ReadAllText(ToFullPath("Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs"));
            RequireNear(noisyResponse, 60f, 0.0001f, "noisy velocity response changed");
            RequireNear(onsetResponse, 118.8f, 0.001f, "motion-onset velocity response changed");
            RequireNear(steadyResponse, 180f, 0.0001f, "steady velocity response changed");
            Require(
                adaptiveFirst > baseFirst + 0.20f &&
                adaptiveSecond > 0.99f &&
                motionSource.Contains("predictionVelocityFastResponse = 180f") &&
                motionSource.Contains("_predictionRotationConsistency") &&
                motionSource.Contains("_predictionPositionConsistency") &&
                motionSource.Contains("_predictionScaleConsistency") &&
                sceneSource.Contains("predictionVelocityFastResponse: 180") &&
                panelSource.Contains("Velocity estimate steady") &&
                panelSource.Contains("KiwiTrack.VelocityFastResponse"),
                "velocity estimator does not accelerate only for coherent motion"
            );
        });

        Check("camera exposure midpoint compensation", failures, ref checks, () =>
        {
            float at60 = KiwiUltraDisplayMath.CalculateCaptureAgeCompensation(
                60f,
                0.50f,
                0.020f
            );
            float at30 = KiwiUltraDisplayMath.CalculateCaptureAgeCompensation(
                30f,
                0.50f,
                0.020f
            );
            float capped = KiwiUltraDisplayMath.CalculateCaptureAgeCompensation(
                15f,
                0.50f,
                0.020f
            );
            float invalid = KiwiUltraDisplayMath.CalculateCaptureAgeCompensation(
                0f,
                0.50f,
                0.020f
            );
            string motionSource = File.ReadAllText(ToFullPath(MotionPath));
            string sceneSource = File.ReadAllText(ToFullPath("Assets/Scenes/Face Landmark Detection.unity"));
            string panelSource = File.ReadAllText(ToFullPath("Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs"));
            RequireNear(at60, 1f / 120f, 0.00001f, "60 Hz capture midpoint");
            RequireNear(at30, 1f / 60f, 0.00001f, "30 Hz capture midpoint");
            RequireNear(capped, 0.020f, 0.00001f, "capture compensation cap");
            RequireNear(invalid, 0f, 0.00001f, "invalid source-rate compensation");
            Require(
                motionSource.Contains("ultraCompensateCameraCaptureAge = true") &&
                motionSource.Contains("age + captureAgeCompensation") &&
                motionSource.Contains("PrecisionSourceRateHz") &&
                sceneSource.Contains("ultraCaptureIntervalFraction: 0.5") &&
                sceneSource.Contains("ultraMaxCaptureAgeSeconds: 0.02") &&
                panelSource.Contains("Camera exposure midpoint compensation") &&
                panelSource.Contains("capture comp ") &&
                panelSource.Contains("KiwiTrack.CaptureAgeCap"),
                "camera capture age is not bounded, observable, and adjustable"
            );
        });

        Check("low-latency camera preference", failures, ref checks, () =>
        {
            string settingsSource = File.ReadAllText(ToFullPath("Assets/MediaPipeUnity/Samples/Common/Scripts/AppSettings.cs"));
            string settingsAsset = File.ReadAllText(ToFullPath("Assets/MediaPipeUnity/Samples/Scenes/AppSettings.asset"));
            string webCamSource = File.ReadAllText(ToFullPath("Assets/MediaPipeUnity/Samples/Common/Scripts/ImageSource/WebCamSource.cs"));
            Require(
                settingsSource.Contains("_preferredDefaultWebCamWidth = 640") &&
                settingsSource.Contains("ResolutionStruct(640, 360, 30)") &&
                settingsSource.Contains("ResolutionStruct(1280, 720, 60)") &&
                settingsAsset.Contains("_preferredDefaultWebCamWidth: 640") &&
                settingsAsset.Contains("frameRate: 60") &&
                webCamSource.Contains("b.frameRate.CompareTo(a.frameRate)"),
                "camera source does not default to the bounded Windows latency profile"
            );
        });

        Check("CM831 measured high-speed capture profile", failures, ref checks, () =>
        {
            KiwiCm831TrackingComparison comparison =
                KiwiCm831TrackingStrategyEvaluator.Compare();
            Require(
                comparison.winner == KiwiCm831TrackingStrategy.HighSpeedHd60Input480,
                "CM831 720p60/480px high-speed profile did not beat the recorded and full-HD 60fps alternatives"
            );

            for (int i = 0; i < comparison.scores.Length; i++)
            {
                KiwiCm831TrackingScore score = comparison.scores[i];
                UnityEngine.Debug.Log(
                    "[KiwiOptimization] CM831: " + score.strategy +
                    " total=" + score.total.ToString("F2") +
                    " source=" + score.sourceRateHz.ToString("F1") + "Hz" +
                    " input=" + score.trackingInputWidth +
                    " readback=" + score.readbackMs.ToString("F1") + "ms" +
                    " motionLatency=" + score.estimatedMotionLatencyMs.ToString("F1") + "ms" +
                    " quality=" + score.landmarkQuality.ToString("F2")
                );
            }

            Require(
                Mediapipe.Unity.WebCamSource.IsCm831DeviceName("UGREEN Camera") &&
                Mediapipe.Unity.WebCamSource.IsCm831DeviceName("CM831") &&
                !Mediapipe.Unity.WebCamSource.IsCm831DeviceName("Logitech C270"),
                "CM831 camera-name matching is unsafe"
            );

            string settingsSource = File.ReadAllText(ToFullPath(
                "Assets/MediaPipeUnity/Samples/Common/Scripts/AppSettings.cs"
            ));
            string settingsAsset = File.ReadAllText(ToFullPath(
                "Assets/MediaPipeUnity/Samples/Scenes/AppSettings.asset"
            ));
            string webCamSource = File.ReadAllText(ToFullPath(
                "Assets/MediaPipeUnity/Samples/Common/Scripts/ImageSource/WebCamSource.cs"
            ));
            string runnerSource = File.ReadAllText(ToFullPath(FaceRunnerPath));
            string sceneSource = File.ReadAllText(ToFullPath(
                "Assets/Scenes/Face Landmark Detection.unity"
            ));
            string panelSource = File.ReadAllText(ToFullPath(
                "Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs"
            ));
            Require(
                settingsSource.Contains("\"CM831\", \"UGREEN\"") &&
                settingsSource.Contains("_preferredWebCamProfileWidth = 1280") &&
                settingsSource.Contains("_preferredWebCamProfileHeight = 720") &&
                settingsSource.Contains("_preferredWebCamProfileFrameRate = 60") &&
                settingsSource.Contains("ResolutionStruct(1280, 720, 60)") &&
                settingsSource.Contains("ResolutionStruct(1920, 1080, 60)") &&
                settingsAsset.Contains("_preferredWebCamProfileWidth: 1280") &&
                settingsAsset.Contains("_preferredWebCamProfileHeight: 720") &&
                settingsAsset.Contains("_preferredWebCamProfileFrameRate: 60") &&
                webCamSource.Contains("FindPreferredSourceIndex") &&
                webCamSource.Contains("usesPreferredProfile") &&
                runnerSource.Contains("autoOptimizeCm831 = true") &&
                runnerSource.Contains("cm831TrackingInputWidth = 480") &&
                runnerSource.Contains("WebCamSource.IsCm831DeviceName") &&
                sceneSource.Contains("autoOptimizeCm831: 1") &&
                sceneSource.Contains("cm831TrackingInputWidth: 480") &&
                panelSource.Contains("CM831 high-speed 720p60 profile") &&
                panelSource.Contains("KiwiTrack.Cm831HighSpeedInputWidth"),
                "CM831 selection, 720p60 request, 480px inference, diagnostics, or persistence is incomplete"
            );
        });

        Check("Windows bounded synchronous readback", failures, ref checks, () =>
        {
            string configSource = File.ReadAllText(ToFullPath(
                "Assets/MediaPipeUnity/Samples/Scenes/Face Landmark Detection/FaceLandmarkDetectionConfig.cs"
            ));
            string runnerSource = File.ReadAllText(ToFullPath(FaceRunnerPath));
            string sceneSource = File.ReadAllText(ToFullPath("Assets/Scenes/Face Landmark Detection.unity"));
            Require(
                configSource.Contains("#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN") &&
                configSource.Contains("ImageReadMode.CPU;") &&
                configSource.Contains("ImageReadMode.CPUAsync;") &&
                runnerSource.Contains("trackingInputMaxWidth = 480") &&
                runnerSource.Contains("Mathf.Clamp(trackingInputMaxWidth, 320, 1920)") &&
                runnerSource.Contains("if (_trackingInputTexture == null)") &&
                runnerSource.Contains("SourceTextureWidth") &&
                runnerSource.Contains("SourceTextureHeight") &&
                sceneSource.Contains("trackingInputMaxWidth: 480"),
                "Windows readback is not bounded while mobile retains CPUAsync"
            );
        });

        Check("full measured-latency compensation", failures, ref checks, () =>
        {
            string motionSource = File.ReadAllText(ToFullPath(MotionPath));
            Require(
                motionSource.Contains("ultraCompensateFullResultAge") &&
                motionSource.Contains("ultraMaxPredictionSeconds = 0.100f") &&
                motionSource.Contains("ultraPredictionStrength = 1.00f"),
                "raw-response latency compensation defaults are not installed"
            );
        });

        Check("latest-frame tracking throughput", failures, ref checks, () =>
        {
            string runnerSource = File.ReadAllText(ToFullPath(FaceRunnerPath));
            string sceneSource = File.ReadAllText(ToFullPath("Assets/Scenes/Face Landmark Detection.unity"));
            string panelSource = File.ReadAllText(ToFullPath("Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs"));
            Require(
                runnerSource.Contains("renderDebugLandmarkAnnotations = false") &&
                runnerSource.Contains("LatestTrackingResultRateHz") &&
                runnerSource.Contains("LatestFreshSourceRateHz") &&
                runnerSource.Contains("LatestSubmissionRateHz") &&
                runnerSource.Contains("LatestReadbackLatencyMs") &&
                runnerSource.Contains("processOnlyFreshWebCamFrames = true") &&
                runnerSource.Contains("latestFrameOnlyLiveStream = true") &&
                runnerSource.Contains("downscaleTrackingInput = true") &&
                runnerSource.Contains("trackingInputMaxWidth = 480") &&
                runnerSource.Contains("Graphics.Blit(") &&
                runnerSource.Contains("? 4") &&
                runnerSource.Contains(": 2;") &&
                runnerSource.Contains("ObserveFreshWebCamFrame(_observedWebCamTexture)") &&
                runnerSource.Contains("_freshWebCamGeneration == submittedFreshGeneration") &&
                runnerSource.Contains("_liveStreamRequestInFlight") &&
                runnerSource.Contains("long sourceFrameHostTicks") &&
                runnerSource.IndexOf("long sourceFrameHostTicks", StringComparison.Ordinal) <
                    runnerSource.IndexOf("ReadTextureAsync", StringComparison.Ordinal) &&
                runnerSource.Contains("submissionHostTicks =") &&
                runnerSource.Contains("sourceFrameHostTicks;") &&
                runnerSource.Contains("published && _acceptTrackingResults && renderDebugLandmarkAnnotations") &&
                panelSource.Contains("PrecisionSourceRateHz") &&
                panelSource.Contains("PrecisionSubmissionRateHz") &&
                panelSource.Contains("PrecisionReadbackLatencyMs") &&
                panelSource.Contains("PrecisionInputWidth") &&
                panelSource.Contains("PrecisionSourceWidth") &&
                panelSource.Contains("PrecisionEstimatedModelLatencyMs") &&
                panelSource.Contains("LatestFrameOnlyV2") &&
                sceneSource.Contains("processOnlyFreshWebCamFrames: 1") &&
                sceneSource.Contains("latestFrameOnlyLiveStream: 1") &&
                sceneSource.Contains("downscaleTrackingInput: 1") &&
                sceneSource.Contains("trackingInputMaxWidth: 480"),
                "fresh-frame observation, latest-frame LIVE_STREAM coalescing, or pipeline diagnostics are incomplete"
            );
        });

        Check("render-rate face-part stability", failures, ref checks, () =>
        {
            string cropSource = File.ReadAllText(ToFullPath("Assets/Script/FacePartCropper.cs"));
            string maskSource = File.ReadAllText(ToFullPath("Assets/Script/FacePartShapeMask.cs"));
            string angleSource = File.ReadAllText(ToFullPath("Assets/Script/FacePartAngleLock.cs"));
            string sceneSource = File.ReadAllText(ToFullPath("Assets/Scenes/Face Landmark Detection.unity"));
            string panelSource = File.ReadAllText(ToFullPath("Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs"));

            float matchedPredictionTime = KiwiFacePartPredictionMath.CalculatePredictionTime(
                true,
                0.070f,
                0.010f,
                0.002f,
                0.090f
            );
            Vector2 boundedPrediction = KiwiFacePartPredictionMath.PredictCenter(
                new Vector2(0.5f, 0.5f),
                new Vector2(1f, 0f),
                matchedPredictionTime,
                0.03f
            );
            RequireNear(matchedPredictionTime, 0.072f, 0.000001f, "matched camera-frame age was not used");
            RequireNear(boundedPrediction.x, 0.53f, 0.000001f, "face-part prediction exceeded its spatial bound");

            Vector2 phaseLockedEye = KiwiFacePartPredictionMath.PredictCenterPhaseLocked(
                new Vector2(0.40f, 0.45f),
                1.0f,
                0.50f,
                matchedPredictionTime,
                0.004f
            );
            Vector2 phaseLockedMouth = KiwiFacePartPredictionMath.PredictCenterPhaseLocked(
                new Vector2(0.50f, 0.65f),
                -0.80f,
                0.50f,
                matchedPredictionTime,
                0.004f
            );
            RequireNear(phaseLockedEye.x, 0.404f, 0.000001f, "eye X phase-lock bound");
            RequireNear(phaseLockedMouth.x, 0.496f, 0.000001f, "mouth X phase-lock bound");
            RequireNear(
                phaseLockedEye.y - 0.45f,
                phaseLockedMouth.y - 0.65f,
                0.000001f,
                "eye and mouth received different vertical prediction"
            );
            RequireNear(
                phaseLockedEye.y - 0.45f,
                0.004f,
                0.000001f,
                "shared vertical prediction exceeded its axis bound"
            );

            Vector2[] stableContour =
            {
                new Vector2(0.40f, 0.40f),
                new Vector2(0.60f, 0.40f),
                new Vector2(0.60f, 0.60f),
                new Vector2(0.40f, 0.60f),
            };
            Vector2[] microscopicContour =
            {
                new Vector2(0.4002f, 0.4001f),
                new Vector2(0.6001f, 0.3999f),
                new Vector2(0.6002f, 0.6001f),
                new Vector2(0.4001f, 0.5999f),
            };
            Vector2[] expressionContour =
            {
                new Vector2(0.40f, 0.38f),
                new Vector2(0.60f, 0.38f),
                new Vector2(0.60f, 0.62f),
                new Vector2(0.40f, 0.62f),
            };
            Require(
                !KiwiFacePartContourStabilityMath.ShouldUpdateContour(
                    stableContour,
                    microscopicContour,
                    4,
                    0.00055f,
                    0.00044f
                ) &&
                KiwiFacePartContourStabilityMath.ShouldUpdateContour(
                    stableContour,
                    expressionContour,
                    4,
                    0.00055f,
                    0.00044f
                ),
                "coherent contour hold does not separate sub-pixel noise from expression motion"
            );
            Require(
                KiwiFacePartRectStabilityMath.ShouldHoldSize(
                    new Vector2(0.10f, 0.06f),
                    new Vector2(0.1008f, 0.0606f),
                    0.01f,
                    0.02f,
                    0.0012f
                ) &&
                !KiwiFacePartRectStabilityMath.ShouldHoldSize(
                    new Vector2(0.10f, 0.06f),
                    new Vector2(0.104f, 0.07f),
                    0.01f,
                    0.02f,
                    0.0012f
                ),
                "crop-size hold does not release for real expression changes"
            );

            Require(
                KiwiFacePartCoherentMotionMath.TryResolveSharedVerticalDelta(
                    0.020f,
                    0.019f,
                    0.021f,
                    1f / 30f,
                    0.025f,
                    0.003f,
                    out float sharedVerticalDelta,
                    out float sharedVerticalSpeed
                ) &&
                Mathf.Abs(sharedVerticalDelta - 0.020f) < 0.000001f &&
                sharedVerticalSpeed > 0.5f &&
                !KiwiFacePartCoherentMotionMath.TryResolveSharedVerticalDelta(
                    0.020f,
                    0.019f,
                    0.040f,
                    1f / 30f,
                    0.025f,
                    0.003f,
                    out _,
                    out _
                ),
                "vertical grouping does not separate head translation from local expression motion"
            );

            Require(
                KiwiFacePartCoherentMotionMath.TryResolvePhaseLockedVerticalDeltas(
                    0.020f,
                    0.019f,
                    0.024f,
                    1f / 30f,
                    0.025f,
                    0.003f,
                    out float phaseLeftDelta,
                    out float phaseRightDelta,
                    out float phaseMouthDelta,
                    out float phaseSharedSpeed
                ) &&
                Mathf.Abs(phaseLeftDelta - 0.020f) < 0.000001f &&
                Mathf.Abs(phaseRightDelta - 0.020f) < 0.000001f &&
                phaseMouthDelta > 0.020f &&
                phaseMouthDelta < 0.021f &&
                phaseSharedSpeed > 0.5f,
                "phase-locked grouping did not suppress a small local Y residual continuously"
            );

            Require(
                KiwiFacePartCoherentMotionMath.TryResolvePhaseLockedVerticalDeltas(
                    0.020f,
                    0.019f,
                    0.040f,
                    1f / 30f,
                    0.025f,
                    0.003f,
                    out _,
                    out _,
                    out float releasedMouthDelta,
                    out _
                ) &&
                Mathf.Abs(releasedMouthDelta - 0.040f) < 0.000001f,
                "phase-locked grouping did not release a real mouth or pose change"
            );

            Require(
                cropSource.Contains("strictLandmarkerTracking = false") &&
                cropSource.Contains("eyeRenderResponse = 180f") &&
                cropSource.Contains("mouthRenderResponse = 200f") &&
                cropSource.Contains("eyeSampleSizeResponse = 80f") &&
                cropSource.Contains("mouthSampleSizeResponse = 90f") &&
                cropSource.Contains("restSizeJitterThreshold = 0.00120f") &&
                cropSource.Contains("KiwiFacePartRectStabilityMath.ShouldHoldSize") &&
                cropSource.Contains("stabilizeCoherentVerticalMotion = true") &&
                cropSource.Contains("KiwiFacePartCoherentMotionMath.TryResolveSharedVerticalDelta") &&
                cropSource.Contains("TryResolvePhaseLockedVerticalDeltas") &&
                cropSource.Contains("FilterLocalResidual") &&
                cropSource.Contains("SynchronizeCoherentVerticalVelocities") &&
                cropSource.Contains("phaseLockVerticalPrediction = true") &&
                cropSource.Contains("coherentVerticalRenderResponse = 200f") &&
                cropSource.Contains("RefreshSharedVerticalVelocity") &&
                cropSource.Contains("PredictCenterPhaseLocked") &&
                cropSource.Contains("verticalPositionT") &&
                cropSource.Contains("compensateMatchedFrameAge = true") &&
                cropSource.Contains("KiwiFacePartPredictionMath.CalculatePredictionTime") &&
                cropSource.Contains("directPositionDuringMotion = true"),
                "eye/mouth crop still uses low-rate sample-and-hold defaults"
            );
            Require(
                maskSource.Contains("contourRenderResponse") &&
                maskSource.Contains("SetContourTarget") &&
                maskSource.Contains("AdvanceRenderedContour") &&
                maskSource.Contains("KiwiFacePartContourStabilityMath.ShouldUpdateContour") &&
                maskSource.Contains("_contourUploadDirty") &&
                maskSource.Contains("AdvanceFrameVisibility") &&
                maskSource.Contains("eyeHideFadeSeconds") &&
                maskSource.Contains("strictLandmarkerTracking || !lockMouthHeight") &&
                maskSource.Contains("lockContourToMovingCrop = true") &&
                maskSource.Contains("KiwiFacePartMaskCoherenceMath.ToCropLocal") &&
                maskSource.Contains("KiwiFacePartMaskCoherenceMath.FromCropLocal") &&
                cropSource.Contains("TryGetSampleRect") &&
                maskSource.Contains("_image.canvasRenderer.SetAlpha(1f)"),
                "contour/crop coherence or blink visibility is not rendered continuously"
            );
            Require(
                angleSource.Contains("correctionRenderResponse") &&
                angleSource.Contains("AdvanceRenderedCorrection") &&
                angleSource.Contains("Mathf.LerpAngle") &&
                angleSource.Contains("lockPivotToRenderedCrop") &&
                angleSource.Contains("KiwiFacePartTiltMath.ResolveRotationPivot"),
                "face-part rotation still jumps at landmark cadence"
            );
            Rect renderedEyeCrop = new Rect(0.20f, 0.30f, 0.40f, 0.20f);
            Vector2 rawEyePivot = new Vector2(0.82f, 0.76f);
            Vector2 lockedEyePivot = KiwiFacePartTiltMath.ResolveRotationPivot(
                true,
                renderedEyeCrop,
                rawEyePivot
            );
            Vector2 unlockedEyePivot = KiwiFacePartTiltMath.ResolveRotationPivot(
                false,
                renderedEyeCrop,
                rawEyePivot
            );
            RequireNear(lockedEyePivot.x, renderedEyeCrop.center.x, 0.000001f, "tilted eye rendered pivot X");
            RequireNear(lockedEyePivot.y, renderedEyeCrop.center.y, 0.000001f, "tilted eye rendered pivot Y");
            RequireNear(unlockedEyePivot.x, rawEyePivot.x, 0.000001f, "unlocked eye landmark pivot X");
            Require(
                CountOccurrences(sceneSource, "contourRenderResponse: 110") == 3 &&
                CountOccurrences(sceneSource, "microJitterDeadZone: 0.00055") == 3 &&
                CountOccurrences(sceneSource, "lockContourToMovingCrop: 1") == 3 &&
                CountOccurrences(sceneSource, "cropLocalSafetyMargin: 0.025") == 3 &&
                CountOccurrences(sceneSource, "correctionRenderResponse: 180") == 3 &&
                CountOccurrences(sceneSource, "lockPivotToRenderedCrop: 1") == 3 &&
                sceneSource.Contains("sampleIdleResponse: 120") &&
                sceneSource.Contains("restSizeJitterThreshold: 0.0012") &&
                sceneSource.Contains("mouthRenderResponse: 200") &&
                sceneSource.Contains("lockMouthHeight: 0") &&
                sceneSource.Contains("enablePrediction: 1") &&
                sceneSource.Contains("compensateMatchedFrameAge: 1") &&
                sceneSource.Contains("maxExtrapolationSeconds: 0.09") &&
                sceneSource.Contains("maxPredictionDistance: 0.004") &&
                sceneSource.Contains("phaseLockVerticalPrediction: 1") &&
                sceneSource.Contains("coherentVerticalRenderResponse: 200") &&
                sceneSource.Contains("preventMouthSurfaceClipping: 1") &&
                sceneSource.Contains("mouthSurfaceSafetyMargin: 0.035"),
                "scene does not contain the flicker-free low-delay face-part profile"
            );
            Require(
                panelSource.Contains("Flicker-free eye/mouth interpolation") &&
                panelSource.Contains("Compensate eye/mouth tracking age") &&
                panelSource.Contains("KiwiTrack.FacePartPredictionLimit") &&
                panelSource.Contains("Part prediction distance") &&
                panelSource.Contains("Phase-lock eye / mouth vertical motion") &&
                panelSource.Contains("Shared vertical crop response") &&
                panelSource.Contains("KiwiTrack.FacePartPredictionDistanceV1") &&
                panelSource.Contains("FacePartVerticalPhaseLockV1") &&
                panelSource.Contains("LandMarker input width") &&
                panelSource.Contains("Calibrated mouth height lock") &&
                panelSource.Contains("KiwiTrack.PartContourResponseV2") &&
                panelSource.Contains("KiwiTrack.PartContourJitterZoneV2") &&
                panelSource.Contains("KiwiTrack.PartSizeJitterZoneV2") &&
                panelSource.Contains("Keep eye/mouth masks inside moving crops") &&
                panelSource.Contains("KiwiTrack.PartMaskSafetyMargin") &&
                panelSource.Contains("KiwiTrack.PartAngleResponse") &&
                panelSource.Contains("Lock tilted part pivot to displayed crop") &&
                panelSource.Contains("PartRenderedCropPivotV1") &&
                panelSource.Contains("Keep enlarged mouth inside surface") &&
                panelSource.Contains("MouthSurfaceSafetyMarginV1") &&
                panelSource.Contains("MouthSurfaceReleaseV1"),
                "face-part latency/stability controls are not exposed and persisted"
            );
        });

        Check("face-part dropout continuity", failures, ref checks, () =>
        {
            string cropSource = File.ReadAllText(ToFullPath("Assets/Script/FacePartCropper.cs"));
            string maskSource = File.ReadAllText(ToFullPath("Assets/Script/FacePartShapeMask.cs"));
            string sceneSource = File.ReadAllText(ToFullPath("Assets/Scenes/Face Landmark Detection.unity"));
            string panelSource = File.ReadAllText(ToFullPath("Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs"));

            bool coherentTranslation = KiwiFacePartContinuityMath.IsMouthSamplePlausible(
                new Vector2(0.40f, 0.60f),
                new Vector2(0.60f, 0.60f),
                new Vector2(0.50f, 0.45f),
                new Vector2(0.47f, 0.58f),
                new Vector2(0.67f, 0.58f),
                new Vector2(0.57f, 0.43f),
                0.045f,
                1.25f
            );
            bool isolatedSpike = KiwiFacePartContinuityMath.IsMouthSamplePlausible(
                new Vector2(0.40f, 0.60f),
                new Vector2(0.60f, 0.60f),
                new Vector2(0.50f, 0.45f),
                new Vector2(0.41f, 0.60f),
                new Vector2(0.61f, 0.60f),
                new Vector2(0.85f, 0.10f),
                0.045f,
                1.25f
            );

            Require(
                coherentTranslation && !isolatedSpike,
                "mouth continuity guard rejects coherent motion or accepts an isolated crop spike"
            );
            Require(
                cropSource.Contains("hidePartsWhenLost = false") &&
                cropSource.Contains("_statesResetForLoss") &&
                cropSource.Contains("rejectIsolatedMouthOutliers = true") &&
                maskSource.Contains("mouthEdgeHideConfirmationSamples") &&
                maskSource.Contains("ConfirmMouthEdgeViolation") &&
                !maskSource.Contains("_mouthFrameVisibility =\n                0f;"),
                "tracking-loss freeze, isolated-part rejection, or confirmed edge hiding is incomplete"
            );
            Require(
                sceneSource.Contains("hidePartsWhenLost: 0") &&
                sceneSource.Contains("mouthEdgeHideConfirmationSamples: 3") &&
                panelSource.Contains("Freeze parts through tracking dropouts") &&
                panelSource.Contains("KiwiTrack.MouthEdgeConfirmations"),
                "dropout-continuity defaults are not active or configurable"
            );
        });

        Check("confidence-gated face-part visibility", failures, ref checks, () =>
        {
            Require(
                !KiwiFacePartVisibilityMath.HasCoherentEyeClosure(
                    true,
                    true,
                    false,
                    true
                ),
                "one noisy eye signal can still confirm a closed eye"
            );
            Require(
                KiwiFacePartVisibilityMath.HasCoherentEyeClosure(
                    true,
                    true,
                    true,
                    true
                ),
                "coherent blink and geometry evidence was rejected"
            );

            float disagreementClose =
                KiwiFacePartVisibilityMath.FuseEyeCloseAmount(
                    0f,
                    1f,
                    true,
                    true
                );
            Require(
                disagreementClose < 0.25f,
                "geometry-only eye noise still collapses the eye contour"
            );

            int evidence = 0;
            evidence = KiwiFacePartVisibilityMath.AdvanceEvidenceCounter(evidence, true, 2);
            Require(evidence == 1, "eye close confirmation entered too early");
            evidence = KiwiFacePartVisibilityMath.AdvanceEvidenceCounter(evidence, false, 2);
            Require(evidence == 0, "eye close evidence did not reset after disagreement");

            Require(
                !KiwiFacePartVisibilityMath.ShouldConfirmVisibilityLoss(
                    3,
                    3,
                    0.08f,
                    0.12f
                ),
                "mouth hid before the edge grace period elapsed"
            );
            Require(
                !KiwiFacePartVisibilityMath.ShouldConfirmVisibilityLoss(
                    2,
                    3,
                    0.20f,
                    0.12f
                ),
                "mouth hid without enough consecutive edge samples"
            );
            Require(
                KiwiFacePartVisibilityMath.ShouldConfirmVisibilityLoss(
                    3,
                    3,
                    0.12f,
                    0.12f
                ),
                "persistent incomplete mouth did not enter the hide state"
            );

            Require(
                KiwiFacePartVisibilityMath.IsMouthBlinkProtectionActive(
                    true,
                    true,
                    0.82f,
                    0.10f,
                    0.35f,
                    0.20f,
                    0.20f,
                    0.13f
                ),
                "BlendShape blink did not protect mouth visibility"
            );
            Require(
                KiwiFacePartVisibilityMath.IsMouthBlinkProtectionActive(
                    true,
                    false,
                    0f,
                    0f,
                    0.35f,
                    0.09f,
                    0.10f,
                    0.13f
                ),
                "landmark-only blink did not protect mouth visibility"
            );
            Require(
                !KiwiFacePartVisibilityMath.IsMouthBlinkProtectionActive(
                    true,
                    true,
                    0.12f,
                    0.14f,
                    0.35f,
                    0.09f,
                    0.10f,
                    0.13f
                ),
                "geometry fallback overrode a valid open-eye BlendShape result"
            );

            string maskSource = File.ReadAllText(ToFullPath("Assets/Script/FacePartShapeMask.cs"));
            string sceneSource = File.ReadAllText(ToFullPath("Assets/Scenes/Face Landmark Detection.unity"));
            string panelSource = File.ReadAllText(ToFullPath(
                "Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs"
            ));
            Require(
                maskSource.Contains("stabilizeEyeVisibility =") &&
                maskSource.Contains("closedEyeVisibilityFloor =") &&
                maskSource.Contains("UpdateEyeVisibilityState") &&
                maskSource.Contains("_holdMouthVisual") &&
                maskSource.Contains("_safeMouthUvRect") &&
                maskSource.Contains("mouthEdgeHideGraceSeconds") &&
                maskSource.Contains("mouthEdgeShowConfirmationSamples") &&
                maskSource.Contains("ProtectMouthVisibilityDuringBlink") &&
                maskSource.Contains("IsMouthBlinkProtectionActive"),
                "eye agreement gate or last-safe mouth hold is incomplete"
            );
            Require(
                CountOccurrences(sceneSource, "stabilizeEyeVisibility: 1") == 3 &&
                sceneSource.Contains("closedEyeVisibilityFloor: 0.35") &&
                sceneSource.Contains("mouthEdgeHideGraceSeconds: 0.12") &&
                sceneSource.Contains("mouthEdgeShowConfirmationSamples: 2") &&
                sceneSource.Contains("protectMouthDuringBlink: 1") &&
                sceneSource.Contains("mouthBlinkProtectionThreshold: 0.35"),
                "flicker-free visibility defaults are not active in the scene"
            );
            Require(
                panelSource.Contains("Flicker-free eye visibility") &&
                panelSource.Contains("Mouth edge grace") &&
                panelSource.Contains("Keep mouth visible during blinks") &&
                panelSource.Contains("KiwiTrack.ClosedEyeVisibilityFloor") &&
                panelSource.Contains("KiwiTrack.MouthShowConfirmations") &&
                panelSource.Contains("KiwiTrack.MouthBlinkThreshold"),
                "visibility-state controls are not exposed or persisted"
            );
        });

        Check("runtime hot-path reductions", failures, ref checks, () =>
        {
            string panelSource = File.ReadAllText(ToFullPath("Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs"));
            string mouthLockSource = File.ReadAllText(ToFullPath("Assets/Script/MouthDisplaySizeLock.cs"));
            string shapeMaskSource = File.ReadAllText(ToFullPath("Assets/Script/FacePartShapeMask.cs"));
            string framingSource = File.ReadAllText(ToFullPath("Assets/Script/KiwiAutoFraming.cs"));
            Require(
                panelSource.Contains("private bool _showTracking;") &&
                panelSource.Contains("private bool _showModelAdjustments;") &&
                mouthLockSource.Contains("_lastAppliedScaleX") &&
                mouthLockSource.Contains("material == _lastMaterial") &&
                shapeMaskSource.Contains("_lastAppliedFrameVisibility") &&
                framingSource.Contains("effectiveHorizontalFollow <= 0f"),
                "runtime GUI, shader-property, or camera-framing hot paths still repeat avoidable work"
            );
        });

        Check("geometry quality ordering", failures, ref checks, () =>
        {
            float good = KiwiPrecisionTrackingMath.CalculateGeometryQuality(
                0.06f,
                0.16f,
                0.20f
            );
            float poor = KiwiPrecisionTrackingMath.CalculateGeometryQuality(
                0.011f,
                0.026f,
                0.031f
            );
            Require(good > poor, "valid geometry does not outrank marginal geometry");
        });

        Check("duplicate suffix normalization", failures, ref checks, () =>
        {
            RequireEqual(
                KiwiAvatarStorage.NormalizeDuplicateSuffixes("Avatar (1) (2).vrm"),
                "Avatar.vrm",
                "parenthesized suffix"
            );
            RequireEqual(
                KiwiAvatarStorage.NormalizeDuplicateSuffixes("Avatar - Copy.vrm"),
                "Avatar.vrm",
                "copy suffix"
            );
            RequireEqual(
                KiwiAvatarStorage.NormalizeDuplicateSuffixes("Avatar_copy.vrm"),
                "Avatar.vrm",
                "underscore copy suffix"
            );
            RequireEqual(
                KiwiAvatarStorage.NormalizeDuplicateSuffixes("Avatar (0).vrm"),
                "Avatar.vrm",
                "zero duplicate suffix"
            );
        });

        Check("tracking template integrity", failures, ref checks, () =>
        {
            string faceHash = HashAsset(FaceRunnerPath);
            string motionHash = HashAsset(MotionPath);
            RequireEqual(faceHash, HashAsset(FaceTemplatePath), "face runner template");
            RequireEqual(motionHash, HashAsset(MotionTemplatePath), "motion template");
            RequireEqual(
                faceHash,
                GetInstallerConstant("FaceRunnerTargetSha256"),
                "face installer target"
            );
            RequireEqual(
                motionHash,
                GetInstallerConstant("KiwiMotionTargetSha256"),
                "motion installer target"
            );
        });

        Check("changed-only landmark consumers", failures, ref checks, () =>
        {
            string[] consumers =
            {
                "Assets/Script/FacePartAngleLock.cs",
                "Assets/Script/FacePartCropper.cs",
                "Assets/Script/FacePartShapeMask.cs",
                "Assets/Script/MouthCropGuard.cs",
            };

            for (int i = 0; i < consumers.Length; i++)
            {
                string text = File.ReadAllText(ToFullPath(consumers[i]));
                Require(
                    text.Contains("TryGetLatestLandmarksIfChanged"),
                    consumers[i] + " still copies unchanged landmarks"
                );
            }
        });

        Check("face-part camera-edge safety", failures, ref checks, () =>
        {
            Rect openMouth = KiwiMouthCropMath.CalculateSafeRect(
                new Vector2(0.40f, 0.50f),
                new Vector2(0.60f, 0.50f),
                new Vector2(0.40f, 0.42f),
                new Vector2(0.60f, 0.70f),
                1.0f,
                1.35f,
                0.55f,
                0f,
                0f,
                0.10f,
                0.14f
            );

            RequireNear(openMouth.center.y, 0.56f, 0.000001f, "open-mouth contour center");
            Require(openMouth.yMin < 0.42f, "upper lip has no safety clearance");
            Require(openMouth.yMax > 0.70f, "lower lip has no safety clearance");

            Rect neutralMouth = KiwiMouthCropMath.CalculateSafeRect(
                new Vector2(0.40f, 0.50f),
                new Vector2(0.60f, 0.50f),
                new Vector2(0.40f, 0.48f),
                new Vector2(0.60f, 0.52f),
                1.0f,
                1.35f,
                0.55f,
                0.025f,
                0.035f,
                0.10f,
                0.14f
            );

            RequireNear(neutralMouth.width, 0.32f, 0.000001f, "neutral mouth width changed");
            RequireNear(neutralMouth.height, 0.18f, 0.000001f, "neutral mouth height changed");

            Rect edgeMouth = KiwiMouthCropMath.CalculateCenteredUvRect(
                new Vector2(0.50f, 0.04f),
                0.20f,
                0.18f
            );

            RequireNear(edgeMouth.center.y, 0.04f, 0.000001f, "camera-edge center moved");
            RequireNear(edgeMouth.yMin, -0.05f, 0.000001f, "camera-edge overscan was clamped");
            RequireNear(
                (0.04f - edgeMouth.yMin) / edgeMouth.height,
                0.50f,
                0.000001f,
                "camera-edge mouth is not centered on the surface mesh"
            );

            Rect surfaceCrop = new Rect(0.40f, 0.40f, 0.20f, 0.20f);
            Vector2 safeSurfacePoint = KiwiFacePartSurfaceSafetyMath.CalculateNormalizedSurface(
                new Vector2(0.58f, 0.50f),
                surfaceCrop,
                surfaceCrop.center,
                Vector2.zero,
                1f,
                Vector2.one,
                0f,
                1f,
                Vector2.one
            );
            Vector2 clippedSurfacePoint = KiwiFacePartSurfaceSafetyMath.CalculateNormalizedSurface(
                new Vector2(0.58f, 0.50f),
                surfaceCrop,
                surfaceCrop.center,
                Vector2.zero,
                1f,
                new Vector2(0.5f, 1f),
                0f,
                1f,
                Vector2.one
            );
            Require(
                KiwiFacePartSurfaceSafetyMath.IsInsideSurface(safeSurfacePoint, 0.02f),
                "safe mouth contour was rejected by surface guard"
            );
            Require(
                !KiwiFacePartSurfaceSafetyMath.IsInsideSurface(clippedSurfacePoint, 0.02f),
                "enlarged mouth contour still passes outside its surface"
            );

            GameObject cropObject = new GameObject("__KiwiMouthCropValidation");
            try
            {
                FacePartCropper cropper = cropObject.AddComponent<FacePartCropper>();
                cropper.mirrorX = false;
                cropper.useMouthContourSafeCrop = true;
                cropper.preserveEyeCenterAtTextureEdges = true;
                cropper.preserveMouthCenterAtTextureEdges = true;

                Vector2[] landmarks = new Vector2[478];
                int[] mouthIndices =
                {
                    61, 185, 40, 39, 37, 0, 267, 269, 270, 409, 291,
                    375, 321, 405, 314, 17, 84, 181, 91, 146
                };

                for (int i = 0; i < mouthIndices.Length; i++)
                {
                    landmarks[mouthIndices[i]] = new Vector2(0.50f, 0.96f);
                }
                landmarks[61] = new Vector2(0.45f, 0.96f);
                landmarks[291] = new Vector2(0.55f, 0.96f);
                landmarks[0] = new Vector2(0.50f, 0.93f);
                landmarks[17] = new Vector2(0.50f, 0.99f);

                System.Reflection.FieldInfo bufferField = typeof(FacePartCropper).GetField(
                    "_landmarkBuffer",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance
                );
                System.Reflection.MethodInfo buildMethod = typeof(FacePartCropper).GetMethod(
                    "BuildMouthRect",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance
                );

                Require(bufferField != null && buildMethod != null, "mouth crop runtime methods unavailable");
                bufferField.SetValue(cropper, landmarks);
                object[] arguments = { 478, 1.0f, null };
                bool built = (bool)buildMethod.Invoke(cropper, arguments);
                Rect runtimeEdgeMouth = (Rect)arguments[2];

                Require(built, "runtime mouth crop rejected valid edge landmarks");
                RequireNear(runtimeEdgeMouth.center.y, 0.04f, 0.000001f, "runtime crop moved edge center");
                Require(runtimeEdgeMouth.yMin < 0f, "runtime mouth path still clamps texture-edge Y");

                landmarks[362] = new Vector2(0.45f, 0.98f);
                landmarks[263] = new Vector2(0.55f, 0.98f);

                System.Reflection.MethodInfo buildEyeMethod = typeof(FacePartCropper).GetMethod(
                    "BuildEyeRect",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance
                );

                Require(buildEyeMethod != null, "eye crop runtime method unavailable");
                object[] eyeArguments = { 362, 263, 478, 1.0f, null };
                bool eyeBuilt = (bool)buildEyeMethod.Invoke(cropper, eyeArguments);
                Rect runtimeEdgeEye = (Rect)eyeArguments[4];

                Require(eyeBuilt, "runtime eye crop rejected valid edge landmarks");
                RequireNear(runtimeEdgeEye.center.y, 0.02f, 0.000001f, "runtime eye crop moved edge center");
                Require(runtimeEdgeEye.yMin < 0f, "runtime eye path still clamps texture-edge Y");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cropObject);
            }

            string angleSource = File.ReadAllText(
                ToFullPath("Assets/Script/FacePartAngleLock.cs")
            );
            Require(
                angleSource.Contains("CalculateOrientedBoundsCenter") &&
                angleSource.Contains("pivot = CalculateOrientedBoundsCenter"),
                "mouth scale/rotation pivot is still tied only to mouth corners"
            );

            string cropSource = File.ReadAllText(ToFullPath("Assets/Script/FacePartCropper.cs"));
            string maskSource = File.ReadAllText(ToFullPath("Assets/Script/FacePartShapeMask.cs"));
            string reactionSource = File.ReadAllText(ToFullPath("Assets/Script/KiwiExpressionReaction.cs"));
            string panelSource = File.ReadAllText(ToFullPath("Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs"));
            string shaderSource = File.ReadAllText(ToFullPath("Assets/Shader/FacePartSoftMask.shader"));
            int mouthMethodStart = cropSource.IndexOf("private bool BuildMouthRect", StringComparison.Ordinal);
            int mouthMethodEnd = cropSource.IndexOf("private bool TryGetMouthContourBounds", StringComparison.Ordinal);
            int eyeMethodStart = cropSource.IndexOf("private bool BuildEyeRect", StringComparison.Ordinal);
            string mouthMethod = cropSource.Substring(mouthMethodStart, mouthMethodEnd - mouthMethodStart);
            string eyeMethod = cropSource.Substring(eyeMethodStart, mouthMethodStart - eyeMethodStart);
            Require(
                cropSource.Contains("preserveEyeCenterAtTextureEdges") &&
                cropSource.Contains("preserveMouthCenterAtTextureEdges") &&
                mouthMethod.Contains("preserveMouthCenterAtTextureEdges") &&
                !mouthMethod.Contains("preserveEyeCenterAtTextureEdges") &&
                eyeMethod.Contains("preserveEyeCenterAtTextureEdges") &&
                !eyeMethod.Contains("preserveMouthCenterAtTextureEdges") &&
                shaderSource.Contains("float insideTexture") &&
                shaderSource.Contains("saturate(sampleUV)"),
                "camera-edge transparent overscan path is incomplete"
            );
            Require(
                maskSource.Contains("IsRenderedContourInsideSurface") &&
                maskSource.Contains("KiwiFacePartSurfaceSafetyMath.CalculateNormalizedSurface") &&
                reactionSource.Contains("LimitMouthZoomToSurface") &&
                reactionSource.Contains("preventMouthSurfaceClipping") &&
                reactionSource.Contains("mouthSurfaceSafetyMargin"),
                "GPU mouth enlargement can still leave the fitted surface"
            );

            float clippedClearance = KiwiFacePartVisibilityMath.CalculateTextureEdgeClearance(
                0.40f,
                -0.01f,
                0.60f,
                0.10f
            );
            RequireNear(clippedClearance, -0.01f, 0.000001f, "edge clearance calculation");
            Require(
                !KiwiFacePartVisibilityMath.ResolveVisibleState(true, 0.002f, 0.003f, 0.015f),
                "visible mouth did not hide at the camera edge"
            );
            Require(
                !KiwiFacePartVisibilityMath.ResolveVisibleState(false, 0.010f, 0.003f, 0.015f),
                "hidden mouth reappeared inside the hysteresis band"
            );
            Require(
                KiwiFacePartVisibilityMath.ResolveVisibleState(false, 0.020f, 0.003f, 0.015f),
                "hidden mouth did not reappear after safe re-entry"
            );

            float hiddenVisibility = 1f;
            for (int i = 0; i < 4; i++)
            {
                hiddenVisibility = KiwiFacePartVisibilityMath.MoveVisibility(
                    hiddenVisibility,
                    0f,
                    0.010f,
                    0.040f,
                    0.060f
                );
            }
            RequireNear(hiddenVisibility, 0f, 0.000001f, "mouth hide duration");

            float restoredVisibility = 0f;
            for (int i = 0; i < 6; i++)
            {
                restoredVisibility = KiwiFacePartVisibilityMath.MoveVisibility(
                    restoredVisibility,
                    1f,
                    0.010f,
                    0.040f,
                    0.060f
                );
            }
            RequireNear(restoredVisibility, 1f, 0.000001f, "mouth restore duration");

            Require(
                maskSource.Contains("UpdateMouthFrameVisibilityTarget") &&
                maskSource.Contains("MouthIndices") &&
                maskSource.Contains("ConfirmMouthEdgeViolation") &&
                maskSource.Contains("mouthEdgeHideConfirmationSamples") &&
                !maskSource.Contains("debugMouthUvOverscan"),
                "mouth edge visibility is not driven by the actual outer-lip contour"
            );
            Require(
                panelSource.Contains("Hide incomplete mouth at camera edges") &&
                panelSource.Contains("Preserve eye center at camera edges") &&
                panelSource.Contains("KiwiTrack.MouthHideMargin") &&
                panelSource.Contains("KiwiTrack.MouthShowFade"),
                "camera-edge safety controls are not exposed and persisted in the runtime panel"
            );
        });

        Check("duplicate artifact names", failures, ref checks, ValidateArtifactNames);

        Check("tracking math hot loop", failures, ref checks, () =>
        {
            Stopwatch timer = Stopwatch.StartNew();
            Vector3 accumulator = Vector3.zero;
            Quaternion sample = Quaternion.Euler(12f, -18f, -10f);
            for (int i = 0; i < 100000; i++)
            {
                accumulator += KiwiPrecisionTrackingMath.CalculateAvatarEulerDegrees(
                    Quaternion.identity,
                    sample
                );
            }
            timer.Stop();

            Require(IsFinite(accumulator), "tracking math produced a non-finite value");
            Require(timer.ElapsedMilliseconds < 2000, "tracking math hot loop regressed severely");
            UnityEngine.Debug.Log(
                "[KiwiOptimization] 100k direction mappings: " +
                timer.Elapsed.TotalMilliseconds.ToString("F2") + " ms"
            );
        });

        if (failures.Count > 0)
        {
            throw new BuildFailedException(
                "Kiwi optimization validation failed (" + failures.Count + "/" + checks + "):\n" +
                string.Join("\n", failures)
            );
        }

        return "Kiwi Avatar System v1.0.0: " + checks + "/" + checks + " checks passed.";
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private readonly struct InferenceBenchmarkResult
    {
        public readonly double meanMs;
        public readonly double p95Ms;

        public InferenceBenchmarkResult(double meanMs, double p95Ms)
        {
            this.meanMs = meanMs;
            this.p95Ms = p95Ms;
        }

        public static InferenceBenchmarkResult Average(
            InferenceBenchmarkResult first,
            InferenceBenchmarkResult second)
        {
            return new InferenceBenchmarkResult(
                (first.meanMs + second.meanMs) * 0.5,
                (first.p95Ms + second.p95Ms) * 0.5
            );
        }
    }

    private static InferenceBenchmarkResult MeasureInferenceReadback(
        ModelAsset modelAsset,
        bool packed,
        bool parallelRequests,
        int warmupIterations,
        int measuredIterations)
    {
        Model model = ModelLoader.Load(modelAsset);
        if (packed)
        {
            model = KiwiInferenceFaceTracker.BuildSingleReadbackModel(model);
        }

        using Worker worker = new Worker(model, BackendType.GPUCompute);
        using Tensor<float> input = new Tensor<float>(
            new TensorShape(1, 3, 192, 192)
        );

        for (int i = 0; i < warmupIterations; i++)
        {
            worker.Schedule(input);
            if (packed)
            {
                using Tensor<float> output =
                    (worker.PeekOutput(0) as Tensor<float>).ReadbackAndClone();
            }
            else
            {
                Tensor<float> gpuLandmarks =
                    worker.PeekOutput("conv2d_20") as Tensor<float>;
                Tensor<float> gpuPresence =
                    worker.PeekOutput("conv2d_30") as Tensor<float>;
                if (parallelRequests)
                {
                    gpuLandmarks.ReadbackRequest();
                    gpuPresence.ReadbackRequest();
                }
                using Tensor<float> landmarks = gpuLandmarks.ReadbackAndClone();
                using Tensor<float> presence = gpuPresence.ReadbackAndClone();
            }
        }

        double[] samples = new double[measuredIterations];
        for (int i = 0; i < measuredIterations; i++)
        {
            Stopwatch timer = Stopwatch.StartNew();
            worker.Schedule(input);
            if (packed)
            {
                using Tensor<float> output =
                    (worker.PeekOutput(0) as Tensor<float>).ReadbackAndClone();
            }
            else
            {
                Tensor<float> gpuLandmarks =
                    worker.PeekOutput("conv2d_20") as Tensor<float>;
                Tensor<float> gpuPresence =
                    worker.PeekOutput("conv2d_30") as Tensor<float>;
                if (parallelRequests)
                {
                    gpuLandmarks.ReadbackRequest();
                    gpuPresence.ReadbackRequest();
                }
                using Tensor<float> landmarks = gpuLandmarks.ReadbackAndClone();
                using Tensor<float> presence = gpuPresence.ReadbackAndClone();
            }
            timer.Stop();
            samples[i] = timer.Elapsed.TotalMilliseconds;
        }

        double total = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            total += samples[i];
        }
        Array.Sort(samples);
        int p95Index = Mathf.Clamp(
            Mathf.CeilToInt(samples.Length * 0.95f) - 1,
            0,
            samples.Length - 1
        );
        return new InferenceBenchmarkResult(
            total / samples.Length,
            samples[p95Index]
        );
    }

    private static double ApplyExponentialSteps(double response, double dt, int steps)
    {
        double value = 0.0;
        double factor = 1.0 - Math.Exp(-response * dt);
        for (int i = 0; i < steps; i++)
        {
            value += (1.0 - value) * factor;
        }
        return value;
    }

    private static void Check(
        string name,
        List<string> failures,
        ref int checks,
        Action action)
    {
        checks++;
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add("[FAIL] " + name + ": " + exception.Message);
        }
    }

    private static void ValidateArtifactNames()
    {
        string[] roots =
        {
            "Assets/KiwiAvatarSystem/Runtime",
            "Assets/KiwiAvatarSystem/Editor",
            "Assets/Script",
            "Models",
        };

        for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
        {
            string root = ToFullPath(roots[rootIndex]);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                Require(!HasParenthesizedNumericSuffix(stem), "duplicate suffix: " + file);
                Require(!stem.EndsWith("_copy", StringComparison.OrdinalIgnoreCase), "copy suffix: " + file);
                Require(!stem.EndsWith(" - Copy", StringComparison.OrdinalIgnoreCase), "copy suffix: " + file);
            }
        }
    }

    private static bool HasParenthesizedNumericSuffix(string stem)
    {
        int open = stem.LastIndexOf(" (", StringComparison.Ordinal);
        if (open < 0 || !stem.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        int digitCount = stem.Length - open - 3;
        if (digitCount <= 0)
        {
            return false;
        }

        for (int i = open + 2; i < stem.Length - 1; i++)
        {
            if (stem[i] < '0' || stem[i] > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFinite(Vector3 value)
    {
        return
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    private static string HashAsset(string assetPath)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return BitConverter.ToString(
                sha.ComputeHash(File.ReadAllBytes(ToFullPath(assetPath)))
            ).Replace("-", string.Empty);
        }
    }

    private static string GetInstallerConstant(string fieldName)
    {
        System.Reflection.FieldInfo field = typeof(KiwiPrecisionTrackingInstaller).GetField(
            fieldName,
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static
        );
        Require(field != null, "installer field is missing: " + fieldName);
        return (string)field.GetRawConstantValue();
    }

    private static string ToFullPath(string assetPath)
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireNear(float actual, float expected, float tolerance, string label)
    {
        Require(
            Mathf.Abs(actual - expected) <= tolerance,
            label + ": expected " + expected + ", actual " + actual
        );
    }

    private static void RequireEqual(string actual, string expected, string label)
    {
        Require(
            string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            label + ": expected " + expected + ", actual " + actual
        );
    }
}
#endif
