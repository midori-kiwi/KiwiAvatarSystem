#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 8 guarded source-ownership migration for FaceLandmarkerRunner.
///
/// The completed Runner remains the MediaPipe/Inference source owner. This
/// migration adds only camera-session identity, submission-generation tagging,
/// source replacement/restart handling and source-timeline regression safety.
/// It does not replace Landmarker tracking, pose filtering or provider logic.
/// </summary>
[InitializeOnLoad]
public static class KiwiCameraGenerationMigration
{
    private const string TargetPath =
        "Assets/Script/FaceLandmarkerRunner.cs";

    private const string Prerequisite =
        "KIWI_ASYNC_INFERENCE_MAILBOX_V2_3";

    private const string Marker =
        "KIWI_V5_1_PHASE8_CAMERA_GENERATION_OWNER";

    private static int _retryCount;

    static KiwiCameraGenerationMigration()
    {
        EditorApplication.delayCall +=
            ApplyWhenPrerequisitesReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 8 Camera Generation Ownership")]
    private static void ApplyFromMenu()
    {
        _retryCount = 0;
        ApplyWhenPrerequisitesReady();
    }

    private static void ApplyWhenPrerequisitesReady()
    {
        if (!File.Exists(TargetPath))
        {
            return;
        }

        string original =
            File.ReadAllText(TargetPath);

        if (original.IndexOf(
                Marker,
                StringComparison.Ordinal) >= 0)
        {
            return;
        }

        if (original.IndexOf(
                Prerequisite,
                StringComparison.Ordinal) < 0)
        {
            _retryCount++;

            if (_retryCount <= 10)
            {
                EditorApplication.delayCall +=
                    ApplyWhenPrerequisitesReady;
            }
            else
            {
                Debug.LogWarning(
                    "[KiwiAvatarSystem] Phase 8 waited for the existing " +
                    "Inference mailbox marker in FaceLandmarkerRunner, but it " +
                    "was not available. No blind rewrite was performed.");
            }

            return;
        }

        string source =
            NormalizeNewlines(original);

        bool ok = true;
        ok &= PatchOwnerFields(ref source);
        ok &= PatchSubmissionGenerationArray(ref source);
        ok &= PatchPublicDiagnostics(ref source);
        ok &= PatchOwnershipHelpers(ref source);
        ok &= PatchWebCamLifecycleObservation(ref source);
        ok &= PatchRunSessionStart(ref source);
        ok &= PatchRunSourceRefresh(ref source);
        ok &= PatchReadbackGenerationLatch(ref source);
        ok &= PatchTimestampAndStoreCalls(ref source);
        ok &= PatchLiveStreamSubmission(ref source);
        ok &= PatchCallbackGenerationGate(ref source);
        ok &= PatchStoreGenerationGate(ref source);
        ok &= PatchSubmissionHistoryMethods(ref source);
        ok &= PatchClearGenerationHistory(ref source);

        if (!ok)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 8 could not uniquely locate one or " +
                "more validated FaceLandmarkerRunner anchors. The completed " +
                "script was left unchanged; no partial/blind rewrite occurred.");
            return;
        }

        WritePreservingFormat(
            TargetPath,
            original,
            source);

        AssetDatabase.ImportAsset(
            TargetPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] v5.1 Phase 8 moved CameraGeneration ownership " +
            "to FaceLandmarkerRunner and generation-tagged LIVE_STREAM callbacks.");
    }

    private static bool PatchOwnerFields(ref string source)
    {
        const string anchor =
            "        private int _lastObservedWebCamUnityFrame = -1;\n" +
            "        private WebCamTexture _observedWebCamTexture;\n\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "        private int _lastObservedWebCamUnityFrame = -1;\n" +
            "        private WebCamTexture _observedWebCamTexture;\n\n" +
            "        // KIWI_V5_1_PHASE8_CAMERA_GENERATION_OWNER\n" +
            "        // Camera/source session identity is owned here, beside the\n" +
            "        // source lifecycle and submission timeline. Consumers only\n" +
            "        // observe KiwiRuntimeGenerationContext.CameraGeneration.\n" +
            "        private int _kiwiOwnedCameraGeneration;\n" +
            "        private int _kiwiOwnedSourceTextureId;\n" +
            "        private int _kiwiOwnedSourceWidth;\n" +
            "        private int _kiwiOwnedSourceHeight;\n" +
            "        private string _kiwiOwnedSourceName = string.Empty;\n" +
            "        private long _kiwiLastRawSourceTimestamp = -1L;\n" +
            "        private long _kiwiLastTaskTimestamp = -1L;\n" +
            "        private bool _kiwiWebCamPlayingKnown;\n" +
            "        private bool _kiwiWebCamWasPlaying;\n" +
            "        private bool _kiwiTrackingResourceRebuildPending;\n" +
            "        private int _kiwiLiveStreamGateCameraGeneration;\n\n";

        source = source.Replace(anchor, replacement);
        return true;
    }

    private static bool PatchSubmissionGenerationArray(ref string source)
    {
        const string anchor =
            "        private readonly long[] _submissionHostTicks =\n" +
            "            new long[SubmissionHistoryCapacity];\n\n" +
            "        private int _submissionWriteIndex;\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "        private readonly long[] _submissionHostTicks =\n" +
            "            new long[SubmissionHistoryCapacity];\n\n" +
            "        private readonly int[] _submissionCameraGenerations =\n" +
            "            new int[SubmissionHistoryCapacity];\n\n" +
            "        private int _submissionWriteIndex;\n";

        source = source.Replace(anchor, replacement);
        return true;
    }

    private static bool PatchPublicDiagnostics(ref string source)
    {
        const string anchor =
            "        public float SourceRequestedFrameRate =>\n" +
            "            _sourceRequestedFrameRate;\n\n" +
            "        public bool Cm831ProfileActive =>\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "        public float SourceRequestedFrameRate =>\n" +
            "            _sourceRequestedFrameRate;\n\n" +
            "        public int RuntimeCameraGeneration =>\n" +
            "            _kiwiOwnedCameraGeneration > 0\n" +
            "                ? _kiwiOwnedCameraGeneration\n" +
            "                : KiwiRuntimeGenerationContext.CameraGeneration;\n\n" +
            "        public int RuntimeCameraSourceTextureId =>\n" +
            "            _kiwiOwnedSourceTextureId;\n\n" +
            "        public bool Cm831ProfileActive =>\n";

        source = source.Replace(anchor, replacement);
        return true;
    }

    private static bool PatchOwnershipHelpers(ref string source)
    {
        const string anchor =
            "        public bool TryGetLatestLandmarks(\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string helpers =
            "        private void BeginOwnedCameraSourceSession(\n" +
            "            Texture source,\n" +
            "            string sourceName,\n" +
            "            int width,\n" +
            "            int height)\n" +
            "        {\n" +
            "            _kiwiLastRawSourceTimestamp = -1L;\n" +
            "            _kiwiLastTaskTimestamp = -1L;\n" +
            "            _kiwiTrackingResourceRebuildPending = false;\n\n" +
            "            AdvanceOwnedCameraBoundary(\n" +
            "                KiwiCameraGenerationReason.SourceRunStarted,\n" +
            "                source,\n" +
            "                sourceName,\n" +
            "                width,\n" +
            "                height,\n" +
            "                -1L);\n" +
            "        }\n\n" +
            "        private void AdvanceOwnedCameraBoundary(\n" +
            "            KiwiCameraGenerationReason reason,\n" +
            "            Texture source,\n" +
            "            string sourceName,\n" +
            "            int width,\n" +
            "            int height,\n" +
            "            long observedTimestamp)\n" +
            "        {\n" +
            "            int textureId =\n" +
            "                source != null ? source.GetInstanceID() : 0;\n\n" +
            "            _kiwiOwnedCameraGeneration =\n" +
            "                KiwiCameraGeneration.Advance(\n" +
            "                    reason,\n" +
            "                    sourceName,\n" +
            "                    textureId,\n" +
            "                    width,\n" +
            "                    height,\n" +
            "                    observedTimestamp);\n\n" +
            "            // Old MediaPipe callbacks are invalid after this point.\n" +
            "            // Keep the bounded submission ring so old callbacks can\n" +
            "            // resolve their original generation and be counted stale.\n" +
            "            // The generation is rechecked again inside publish locks.\n" +
            "            ClearTrackingData(false);\n\n" +
            "            _pendingFreshWebCamFrame = false;\n" +
            "            _pendingSourceFrameHostTicks = 0L;\n" +
            "            _freshWebCamGeneration = 0;\n" +
            "            _lastObservedWebCamUnityFrame = -1;\n" +
            "            _previousFreshSourceHostTicks = 0L;\n" +
            "            _previousSubmissionRateHostTicks = 0L;\n" +
            "            _latestFreshSourceRateHz = 0f;\n" +
            "            _latestSubmissionRateHz = 0f;\n\n" +
            "            if (!_kiwiTrackingResourceRebuildPending)\n" +
            "            {\n" +
            "                // Same-sized source restarts/replacements can release\n" +
            "                // the app-level gate immediately; generation-tagged\n" +
            "                // callbacks make the abandoned request harmless.\n" +
            "                _liveStreamRequestInFlight = false;\n" +
            "                _kiwiLiveStreamGateCameraGeneration = 0;\n" +
            "            }\n\n" +
            "            _kiwiOwnedSourceTextureId = textureId;\n" +
            "            _kiwiOwnedSourceWidth = Mathf.Max(1, width);\n" +
            "            _kiwiOwnedSourceHeight = Mathf.Max(1, height);\n" +
            "            _kiwiOwnedSourceName = sourceName ?? string.Empty;\n" +
            "            _kiwiLastRawSourceTimestamp = -1L;\n\n" +
            "            _observedWebCamTexture = source as WebCamTexture;\n" +
            "            _sentisSourceTexture = source;\n" +
            "            _lastSentisProcessedGeneration = -1;\n" +
            "            _lastSentisAnchorTimestampApplied = -1L;\n" +
            "            _sentisPrimaryActive = false;\n" +
            "            _hasLatestSentisAnchor = false;\n" +
            "            _hasLatestMediaPipeAuxRotation = false;\n" +
            "            _hasSentisRotationOffset = false;\n" +
            "            _latestSentisSourceFrameHostTicks = 0L;\n\n" +
            "            WebCamTexture webCam = source as WebCamTexture;\n" +
            "            _kiwiWebCamPlayingKnown = webCam != null;\n" +
            "            _kiwiWebCamWasPlaying =\n" +
            "                webCam != null && webCam.isPlaying;\n" +
            "        }\n\n" +
            "        private bool RefreshOwnedCameraSource(\n" +
            "            Texture source,\n" +
            "            string sourceName)\n" +
            "        {\n" +
            "            if (source == null)\n" +
            "            {\n" +
            "                return false;\n" +
            "            }\n\n" +
            "            int textureId = source.GetInstanceID();\n" +
            "            int width = Mathf.Max(1, source.width);\n" +
            "            int height = Mathf.Max(1, source.height);\n" +
            "            string name = sourceName ?? string.Empty;\n\n" +
            "            bool dimensionsChanged =\n" +
            "                _kiwiOwnedSourceWidth > 0 &&\n" +
            "                (width != _kiwiOwnedSourceWidth ||\n" +
            "                 height != _kiwiOwnedSourceHeight);\n\n" +
            "            KiwiCameraGenerationReason reason =\n" +
            "                KiwiCameraGenerationReason.None;\n\n" +
            "            if (\n" +
            "                _kiwiOwnedSourceTextureId != 0 &&\n" +
            "                textureId != _kiwiOwnedSourceTextureId)\n" +
            "            {\n" +
            "                reason = KiwiCameraGenerationReason.SourceTextureChanged;\n" +
            "            }\n" +
            "            else if (\n" +
            "                dimensionsChanged)\n" +
            "            {\n" +
            "                reason = KiwiCameraGenerationReason.SourceDimensionsChanged;\n" +
            "            }\n" +
            "            else if (\n" +
            "                !string.Equals(\n" +
            "                    name,\n" +
            "                    _kiwiOwnedSourceName,\n" +
            "                    System.StringComparison.Ordinal))\n" +
            "            {\n" +
            "                reason = KiwiCameraGenerationReason.SourceNameChanged;\n" +
            "            }\n\n" +
            "            if (reason == KiwiCameraGenerationReason.None)\n" +
            "            {\n" +
            "                return false;\n" +
            "            }\n\n" +
            "            int previousTrackingInputMaxWidth =\n" +
            "                trackingInputMaxWidth;\n\n" +
            "            _sourceName = name;\n" +
            "            _sourceTextureWidth = width;\n" +
            "            _sourceTextureHeight = height;\n" +
            "            _cm831ProfileActive =\n" +
            "                WebCamSource.IsCm831DeviceName(_sourceName);\n\n" +
            "            if (autoOptimizeCm831 && _cm831ProfileActive)\n" +
            "            {\n" +
            "                trackingInputMaxWidth = Mathf.Clamp(\n" +
            "                    cm831TrackingInputWidth, 480, 960);\n" +
            "            }\n\n" +
            "            if (\n" +
            "                dimensionsChanged ||\n" +
            "                trackingInputMaxWidth != previousTrackingInputMaxWidth)\n" +
            "            {\n" +
            "                _kiwiTrackingResourceRebuildPending = true;\n" +
            "            }\n\n" +
            "            AdvanceOwnedCameraBoundary(\n" +
            "                reason,\n" +
            "                source,\n" +
            "                name,\n" +
            "                width,\n" +
            "                height,\n" +
            "                -1L);\n\n" +
            "            return true;\n" +
            "        }\n\n" +
            "        private void RebuildOwnedTrackingResources()\n" +
            "        {\n" +
            "            _textureFramePool?.Dispose();\n" +
            "            _textureFramePool = null;\n\n" +
            "            PrepareTrackingInputTexture(\n" +
            "                _sourceTextureWidth,\n" +
            "                _sourceTextureHeight);\n\n" +
            "            int poolSize =\n" +
            "                config.ImageReadMode == ImageReadMode.GPU ? 4 : 2;\n\n" +
            "            _textureFramePool =\n" +
            "                new Experimental.TextureFramePool(\n" +
            "                    _trackingInputWidth,\n" +
            "                    _trackingInputHeight,\n" +
            "                    TextureFormat.RGBA32,\n" +
            "                    poolSize);\n" +
            "        }\n\n" +
            "        private void ObserveOwnedWebCamLifecycle(\n" +
            "            WebCamTexture webCamTexture)\n" +
            "        {\n" +
            "            if (!_acceptTrackingResults || webCamTexture == null)\n" +
            "            {\n" +
            "                return;\n" +
            "            }\n\n" +
            "            bool isPlaying = webCamTexture.isPlaying;\n\n" +
            "            if (!_kiwiWebCamPlayingKnown)\n" +
            "            {\n" +
            "                _kiwiWebCamPlayingKnown = true;\n" +
            "                _kiwiWebCamWasPlaying = isPlaying;\n" +
            "                return;\n" +
            "            }\n\n" +
            "            if (!_kiwiWebCamWasPlaying && isPlaying)\n" +
            "            {\n" +
            "                AdvanceOwnedCameraBoundary(\n" +
            "                    KiwiCameraGenerationReason.WebCamRestarted,\n" +
            "                    webCamTexture,\n" +
            "                    _sourceName,\n" +
            "                    Mathf.Max(1, webCamTexture.width),\n" +
            "                    Mathf.Max(1, webCamTexture.height),\n" +
            "                    -1L);\n" +
            "            }\n\n" +
            "            _kiwiWebCamWasPlaying = isPlaying;\n" +
            "        }\n\n" +
            "        private long NormalizeOwnedSourceTimestamp(\n" +
            "            long rawTimestamp,\n" +
            "            Texture source,\n" +
            "            out bool generationAdvanced)\n" +
            "        {\n" +
            "            generationAdvanced = false;\n\n" +
            "            if (\n" +
            "                _kiwiLastRawSourceTimestamp >= 0L &&\n" +
            "                rawTimestamp < _kiwiLastRawSourceTimestamp)\n" +
            "            {\n" +
            "                AdvanceOwnedCameraBoundary(\n" +
            "                    KiwiCameraGenerationReason.SourceTimelineRegression,\n" +
            "                    source,\n" +
            "                    _sourceName,\n" +
            "                    _sourceTextureWidth,\n" +
            "                    _sourceTextureHeight,\n" +
            "                    rawTimestamp);\n\n" +
            "                generationAdvanced = true;\n" +
            "            }\n\n" +
            "            _kiwiLastRawSourceTimestamp = rawTimestamp;\n\n" +
            "            long normalized = rawTimestamp;\n" +
            "            if (\n" +
            "                _kiwiLastTaskTimestamp >= 0L &&\n" +
            "                normalized <= _kiwiLastTaskTimestamp)\n" +
            "            {\n" +
            "                normalized = _kiwiLastTaskTimestamp + 1L;\n" +
            "            }\n\n" +
            "            _kiwiLastTaskTimestamp = normalized;\n" +
            "            return normalized;\n" +
            "        }\n\n";

        source = source.Replace(anchor, helpers + anchor);
        return true;
    }

    private static bool PatchWebCamLifecycleObservation(ref string source)
    {
        const string anchor =
            "        private void ObserveFreshWebCamFrame(WebCamTexture webCamTexture)\n" +
            "        {\n" +
            "            if (\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "        private void ObserveFreshWebCamFrame(WebCamTexture webCamTexture)\n" +
            "        {\n" +
            "            ObserveOwnedWebCamLifecycle(webCamTexture);\n\n" +
            "            if (\n";

        source = source.Replace(anchor, replacement);
        return true;
    }

    private static bool PatchRunSessionStart(ref string source)
    {
        const string anchor =
            "            _sourceTextureWidth = Mathf.Max(1, imageSource.textureWidth);\n" +
            "            _sourceTextureHeight = Mathf.Max(1, imageSource.textureHeight);\n\n\n" +
            "            Debug.Log(\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "            _sourceTextureWidth = Mathf.Max(1, imageSource.textureWidth);\n" +
            "            _sourceTextureHeight = Mathf.Max(1, imageSource.textureHeight);\n\n" +
            "            BeginOwnedCameraSourceSession(\n" +
            "                imageSource.GetCurrentTexture(),\n" +
            "                _sourceName,\n" +
            "                _sourceTextureWidth,\n" +
            "                _sourceTextureHeight);\n\n\n" +
            "            Debug.Log(\n";

        source = source.Replace(anchor, replacement);
        return true;
    }

    private static bool PatchRunSourceRefresh(ref string source)
    {
        const string anchor =
            "                Texture sourceTexture =\n" +
            "                    imageSource.GetCurrentTexture();\n\n\n" +
            "                long sourceObservationHostTicks =\n";

        if (CountOccurrences(source, anchor) != 1)
        {
            return false;
        }

        const string replacement =
            "                Texture sourceTexture =\n" +
            "                    imageSource.GetCurrentTexture();\n\n" +
            "                if (RefreshOwnedCameraSource(\n" +
            "                        sourceTexture,\n" +
            "                        imageSource.sourceName ?? string.Empty))\n" +
            "                {\n" +
            "                    var refreshedTransformationOptions =\n" +
            "                        imageSource.GetTransformationOptions();\n\n" +
            "                    flipHorizontally =\n" +
            "                        refreshedTransformationOptions.flipHorizontally;\n" +
            "                    flipVertically =\n" +
            "                        refreshedTransformationOptions.flipVertically;\n" +
            "                    IsInputHorizontallyMirrored = flipHorizontally;\n" +
            "                    imageProcessingOptions =\n" +
            "                        new Tasks.Vision.Core.ImageProcessingOptions(\n" +
            "                            rotationDegrees:\n" +
            "                            (int)refreshedTransformationOptions.rotationAngle);\n\n" +
            "                    _sentisSourceTexture = sourceTexture;\n" +
            "                    _sentisFlipHorizontally = flipHorizontally;\n" +
            "                    _sentisFlipVertically = flipVertically;\n" +
            "                }\n\n" +
            "                if (_kiwiTrackingResourceRebuildPending)\n" +
            "                {\n" +
            "                    // Do not dispose a TextureFramePool while a\n" +
            "                    // LIVE_STREAM image from that pool can still be\n" +
            "                    // owned by native inference. Wait for its callback.\n" +
            "                    if (isLiveStream && _liveStreamRequestInFlight)\n" +
            "                    {\n" +
            "                        yield return null;\n" +
            "                        continue;\n" +
            "                    }\n\n" +
            "                    RebuildOwnedTrackingResources();\n" +
            "                    _kiwiTrackingResourceRebuildPending = false;\n" +
            "                }\n\n\n" +
            "                long sourceObservationHostTicks =\n";

        source = source.Replace(anchor, replacement);
        return true;
    }

    private static bool PatchReadbackGenerationLatch(ref string source)
    {
        const string captureAnchor =
            "                int submittedFreshGeneration =\n" +
            "                    _freshWebCamGeneration;\n\n\n" +
            "                if (\n";

        if (CountOccurrences(source, captureAnchor) != 1)
        {
            return false;
        }

        const string captureReplacement =
            "                int submittedFreshGeneration =\n" +
            "                    _freshWebCamGeneration;\n\n" +
            "                int sourceCameraGeneration =\n" +
            "                    _kiwiOwnedCameraGeneration > 0\n" +
            "                        ? _kiwiOwnedCameraGeneration\n" +
            "                        : KiwiRuntimeGenerationContext.CameraGeneration;\n\n\n" +
            "                if (\n";

        source = source.Replace(captureAnchor, captureReplacement);

        const string postReadbackAnchor =
            "                RecordReadbackLatency(\n" +
            "                    readbackStartHostTicks,\n" +
            "                    System.Diagnostics.Stopwatch.GetTimestamp()\n" +
            "                );\n\n\n" +
            "                switch (taskApi.runningMode)\n";

        if (CountOccurrences(source, postReadbackAnchor) != 1)
        {
            return false;
        }

        const string postReadbackReplacement =
            "                RecordReadbackLatency(\n" +
            "                    readbackStartHostTicks,\n" +
            "                    System.Diagnostics.Stopwatch.GetTimestamp()\n" +
            "                );\n\n" +
            "                // A source boundary can occur while CPUAsync readback\n" +
            "                // is pending. Never submit pixels captured under the\n" +
            "                // previous camera generation as a new-session frame.\n" +
            "                Texture latestSourceTexture =\n" +
            "                    imageSource.GetCurrentTexture();\n\n" +
            "                bool sourceChangedDuringReadback =\n" +
            "                    latestSourceTexture != sourceTexture ||\n" +
            "                    sourceTexture == null ||\n" +
            "                    sourceTexture.width != _kiwiOwnedSourceWidth ||\n" +
            "                    sourceTexture.height != _kiwiOwnedSourceHeight ||\n" +
            "                    !string.Equals(\n" +
            "                        imageSource.sourceName ?? string.Empty,\n" +
            "                        _kiwiOwnedSourceName,\n" +
            "                        System.StringComparison.Ordinal);\n\n" +
            "                if (sourceChangedDuringReadback)\n" +
            "                {\n" +
            "                    // Let the next loop iteration establish the new\n" +
            "                    // source identity before any result is submitted.\n" +
            "                    image.Dispose();\n" +
            "                    continue;\n" +
            "                }\n\n" +
            "                if (\n" +
            "                    sourceCameraGeneration !=\n" +
            "                    KiwiRuntimeGenerationContext.CameraGeneration)\n" +
            "                {\n" +
            "                    image.Dispose();\n" +
            "                    continue;\n" +
            "                }\n\n\n" +
            "                switch (taskApi.runningMode)\n";

        source = source.Replace(postReadbackAnchor, postReadbackReplacement);
        return true;
    }

    private static bool PatchTimestampAndStoreCalls(ref string source)
    {
        const string imageAnchor =
            "                                StoreTrackingData(\n" +
            "                                    result,\n" +
            "                                    GetCurrentTimestampMillisec(),\n" +
            "                                    0L,\n" +
            "                                    System.Diagnostics.Stopwatch.GetTimestamp()\n" +
            "                                );\n";

        if (CountOccurrences(source, imageAnchor) != 1)
        {
            return false;
        }

        const string imageReplacement =
            "                                long timestamp =\n" +
            "                                    NormalizeOwnedSourceTimestamp(\n" +
            "                                        GetCurrentTimestampMillisec(),\n" +
            "                                        sourceTexture,\n" +
            "                                        out bool cameraBoundary);\n\n" +
            "                                if (cameraBoundary)\n" +
            "                                {\n" +
            "                                    sourceCameraGeneration =\n" +
            "                                        KiwiRuntimeGenerationContext.CameraGeneration;\n" +
            "                                }\n\n" +
            "                                StoreTrackingData(\n" +
            "                                    result,\n" +
            "                                    timestamp,\n" +
            "                                    0L,\n" +
            "                                    System.Diagnostics.Stopwatch.GetTimestamp(),\n" +
            "                                    sourceCameraGeneration\n" +
            "                                );\n";

        source = source.Replace(imageAnchor, imageReplacement);

        const string videoAnchor =
            "                            long timestamp =\n" +
            "                                GetCurrentTimestampMillisec();\n\n\n" +
            "                            if (\n";

        if (CountOccurrences(source, videoAnchor) != 1)
        {
            return false;
        }

        const string videoReplacement =
            "                            long timestamp =\n" +
            "                                NormalizeOwnedSourceTimestamp(\n" +
            "                                    GetCurrentTimestampMillisec(),\n" +
            "                                    sourceTexture,\n" +
            "                                    out bool cameraBoundary);\n\n" +
            "                            if (cameraBoundary)\n" +
            "                            {\n" +
            "                                sourceCameraGeneration =\n" +
            "                                    KiwiRuntimeGenerationContext.CameraGeneration;\n" +
            "                            }\n\n\n" +
            "                            if (\n";

        source = source.Replace(videoAnchor, videoReplacement);

        const string videoStoreAnchor =
            "                                StoreTrackingData(\n" +
            "                                    result,\n" +
            "                                    timestamp,\n" +
            "                                    0L,\n" +
            "                                    System.Diagnostics.Stopwatch.GetTimestamp()\n" +
            "                                );\n";

        if (CountOccurrences(source, videoStoreAnchor) != 1)
        {
            return false;
        }

        const string videoStoreReplacement =
            "                                StoreTrackingData(\n" +
            "                                    result,\n" +
            "                                    timestamp,\n" +
            "                                    0L,\n" +
            "                                    System.Diagnostics.Stopwatch.GetTimestamp(),\n" +
            "                                    sourceCameraGeneration\n" +
            "                                );\n";

        source = source.Replace(videoStoreAnchor, videoStoreReplacement);
        return true;
    }

    private static bool PatchLiveStreamSubmission(ref string source)
    {
        const string timestampAnchor =
            "                            long timestamp =\n" +
            "                                GetCurrentTimestampMillisec();\n\n\n" +
            "                            long submissionHostTicks =\n";

        if (CountOccurrences(source, timestampAnchor) != 1)
        {
            return false;
        }

        const string timestampReplacement =
            "                            long timestamp =\n" +
            "                                NormalizeOwnedSourceTimestamp(\n" +
            "                                    GetCurrentTimestampMillisec(),\n" +
            "                                    sourceTexture,\n" +
            "                                    out bool cameraBoundary);\n\n" +
            "                            if (cameraBoundary)\n" +
            "                            {\n" +
            "                                sourceCameraGeneration =\n" +
            "                                    KiwiRuntimeGenerationContext.CameraGeneration;\n" +
            "                            }\n\n\n" +
            "                            long submissionHostTicks =\n";

        source = source.Replace(timestampAnchor, timestampReplacement);

        const string rememberAnchor =
            "                            RememberSubmittedFrame(\n" +
            "                                timestamp,\n" +
            "                                submissionHostTicks\n" +
            "                            );\n";

        if (CountOccurrences(source, rememberAnchor) != 1)
        {
            return false;
        }

        const string rememberReplacement =
            "                            RememberSubmittedFrame(\n" +
            "                                timestamp,\n" +
            "                                submissionHostTicks,\n" +
            "                                sourceCameraGeneration\n" +
            "                            );\n";

        source = source.Replace(rememberAnchor, rememberReplacement);

        const string gateAnchor =
            "                            if (latestFrameOnlyLiveStream)\n" +
            "                            {\n" +
            "                                _liveStreamRequestInFlight = true;\n" +
            "                            }\n";

        if (CountOccurrences(source, gateAnchor) != 1)
        {
            return false;
        }

        const string gateReplacement =
            "                            if (latestFrameOnlyLiveStream)\n" +
            "                            {\n" +
            "                                _liveStreamRequestInFlight = true;\n" +
            "                                _kiwiLiveStreamGateCameraGeneration =\n" +
            "                                    sourceCameraGeneration;\n" +
            "                            }\n";

        source = source.Replace(gateAnchor, gateReplacement);

        const string catchAnchor =
            "                            catch\n" +
            "                            {\n" +
            "                                _liveStreamRequestInFlight = false;\n" +
            "                                throw;\n" +
            "                            }\n";

        if (CountOccurrences(source, catchAnchor) != 1)
        {
            return false;
        }

        const string catchReplacement =
            "                            catch\n" +
            "                            {\n" +
            "                                _liveStreamRequestInFlight = false;\n" +
            "                                _kiwiLiveStreamGateCameraGeneration = 0;\n" +
            "                                throw;\n" +
            "                            }\n";

        source = source.Replace(catchAnchor, catchReplacement);
        return true;
    }

    private static bool PatchCallbackGenerationGate(ref string source)
    {
        const string earlyReleaseAnchor =
            "            // Release the newest-frame gate immediately on callback entry. C#\n" +
            "            // geometry extraction can overlap selection of the next camera frame.\n" +
            "            _liveStreamRequestInFlight = false;\n\n\n" +
            "            if (!_acceptTrackingResults)\n";

        if (CountOccurrences(source, earlyReleaseAnchor) != 1)
        {
            return false;
        }

        const string earlyReleaseReplacement =
            "            // Phase 8: gate release is generation-owned. An old\n" +
            "            // callback must never release a request submitted by the\n" +
            "            // current camera generation. Resolve the submission first.\n\n" +
            "            if (!_acceptTrackingResults)\n";

        source = source.Replace(
            earlyReleaseAnchor,
            earlyReleaseReplacement);

        const string resolveAnchor =
            "            long submissionHostTicks =\n" +
            "                ResolveSubmittedFrameHostTicks(\n" +
            "                    timestamp\n" +
            "                );\n\n\n" +
            "            // Every legitimate callback from the current LIVE_STREAM run must map\n";

        if (CountOccurrences(source, resolveAnchor) != 1)
        {
            return false;
        }

        const string resolveReplacement =
            "            long submissionHostTicks =\n" +
            "                ResolveSubmittedFrameHostTicks(\n" +
            "                    timestamp,\n" +
            "                    out int submissionCameraGeneration\n" +
            "                );\n\n\n" +
            "            // Every legitimate callback from the current LIVE_STREAM run must map\n";

        source = source.Replace(resolveAnchor, resolveReplacement);

        const string unmatchedAnchor =
            "            if (submissionHostTicks <= 0L)\n" +
            "            {\n" +
            "                return;\n" +
            "            }\n\n\n" +
            "            lock (_callbackLifecycleLock)\n";

        if (CountOccurrences(source, unmatchedAnchor) != 1)
        {
            return false;
        }

        const string unmatchedReplacement =
            "            if (submissionHostTicks <= 0L)\n" +
            "            {\n" +
            "                KiwiCameraGeneration.RecordUnmatchedCallbackDrop();\n" +
            "                return;\n" +
            "            }\n\n" +
            "            if (\n" +
            "                _liveStreamRequestInFlight &&\n" +
            "                _kiwiLiveStreamGateCameraGeneration ==\n" +
            "                    submissionCameraGeneration)\n" +
            "            {\n" +
            "                _liveStreamRequestInFlight = false;\n" +
            "                _kiwiLiveStreamGateCameraGeneration = 0;\n" +
            "            }\n\n" +
            "            if (\n" +
            "                submissionCameraGeneration <= 0 ||\n" +
            "                submissionCameraGeneration !=\n" +
            "                    KiwiRuntimeGenerationContext.CameraGeneration)\n" +
            "            {\n" +
            "                KiwiCameraGeneration.RecordStaleCallbackDrop();\n" +
            "                return;\n" +
            "            }\n\n\n" +
            "            lock (_callbackLifecycleLock)\n";

        source = source.Replace(unmatchedAnchor, unmatchedReplacement);

        const string lifecycleAnchor =
            "                if (!_acceptTrackingResults)\n" +
            "                {\n" +
            "                    return;\n" +
            "                }\n\n\n" +
            "                bool published =\n" +
            "                    StoreTrackingData(\n" +
            "                        result,\n" +
            "                        timestamp,\n" +
            "                        submissionHostTicks,\n" +
            "                        arrivalHostTicks\n" +
            "                    );\n";

        if (CountOccurrences(source, lifecycleAnchor) != 1)
        {
            return false;
        }

        const string lifecycleReplacement =
            "                if (!_acceptTrackingResults)\n" +
            "                {\n" +
            "                    return;\n" +
            "                }\n\n" +
            "                if (\n" +
            "                    submissionCameraGeneration !=\n" +
            "                        KiwiRuntimeGenerationContext.CameraGeneration)\n" +
            "                {\n" +
            "                    KiwiCameraGeneration.RecordStaleCallbackDrop();\n" +
            "                    return;\n" +
            "                }\n\n\n" +
            "                bool published =\n" +
            "                    StoreTrackingData(\n" +
            "                        result,\n" +
            "                        timestamp,\n" +
            "                        submissionHostTicks,\n" +
            "                        arrivalHostTicks,\n" +
            "                        submissionCameraGeneration\n" +
            "                    );\n";

        source = source.Replace(lifecycleAnchor, lifecycleReplacement);
        return true;
    }

    private static bool PatchStoreGenerationGate(ref string source)
    {
        const string signatureAnchor =
            "        private bool StoreTrackingData(\n" +
            "            FaceLandmarkerResult result,\n" +
            "            long timestamp,\n" +
            "            long submissionHostTicks,\n" +
            "            long arrivalHostTicks = 0L)\n" +
            "        {\n" +
            "            if (!_acceptTrackingResults)\n";

        if (CountOccurrences(source, signatureAnchor) != 1)
        {
            return false;
        }

        const string signatureReplacement =
            "        private bool StoreTrackingData(\n" +
            "            FaceLandmarkerResult result,\n" +
            "            long timestamp,\n" +
            "            long submissionHostTicks,\n" +
            "            long arrivalHostTicks = 0L,\n" +
            "            int cameraGeneration = 0)\n" +
            "        {\n" +
            "            if (\n" +
            "                !_acceptTrackingResults ||\n" +
            "                (cameraGeneration > 0 &&\n" +
            "                 cameraGeneration !=\n" +
            "                    KiwiRuntimeGenerationContext.CameraGeneration))\n";

        source = source.Replace(signatureAnchor, signatureReplacement);

        const string noFaceAnchor =
            "                return ClearTrackingDataForTimestamp(\n" +
            "                    timestamp\n" +
            "                );\n";

        if (CountOccurrences(source, noFaceAnchor) != 1)
        {
            return false;
        }

        const string noFaceReplacement =
            "                return ClearTrackingDataForTimestamp(\n" +
            "                    timestamp,\n" +
            "                    cameraGeneration\n" +
            "                );\n";

        source = source.Replace(noFaceAnchor, noFaceReplacement);

        const string publishGateAnchor =
            "                    if (\n" +
            "                        !_acceptTrackingResults ||\n" +
            "                        (\n" +
            "                            _latestResultTimestamp >= 0L &&\n" +
            "                            timestamp < _latestResultTimestamp\n" +
            "                        )\n" +
            "                    )\n";

        if (CountOccurrences(source, publishGateAnchor) != 1)
        {
            return false;
        }

        const string publishGateReplacement =
            "                    if (\n" +
            "                        !_acceptTrackingResults ||\n" +
            "                        (cameraGeneration > 0 &&\n" +
            "                         cameraGeneration !=\n" +
            "                            KiwiRuntimeGenerationContext.CameraGeneration) ||\n" +
            "                        (\n" +
            "                            _latestResultTimestamp >= 0L &&\n" +
            "                            timestamp < _latestResultTimestamp\n" +
            "                        )\n" +
            "                    )\n";

        source = source.Replace(publishGateAnchor, publishGateReplacement);

        const string clearSignature =
            "        private bool ClearTrackingDataForTimestamp(\n" +
            "            long timestamp)\n" +
            "        {\n" +
            "            lock (_trackingLock)\n";

        if (CountOccurrences(source, clearSignature) != 1)
        {
            return false;
        }

        const string clearReplacement =
            "        private bool ClearTrackingDataForTimestamp(\n" +
            "            long timestamp,\n" +
            "            int cameraGeneration = 0)\n" +
            "        {\n" +
            "            lock (_trackingLock)\n";

        source = source.Replace(clearSignature, clearReplacement);

        const string clearGate =
            "                    !_acceptTrackingResults ||\n" +
            "                    (\n" +
            "                        _latestResultTimestamp >= 0L &&\n" +
            "                        timestamp < _latestResultTimestamp\n" +
            "                    )\n";

        // This exact gate appears only in ClearTrackingDataForTimestamp after
        // the Store gate has already been expanded above.
        if (CountOccurrences(source, clearGate) != 1)
        {
            return false;
        }

        const string clearGateReplacement =
            "                    !_acceptTrackingResults ||\n" +
            "                    (cameraGeneration > 0 &&\n" +
            "                     cameraGeneration !=\n" +
            "                        KiwiRuntimeGenerationContext.CameraGeneration) ||\n" +
            "                    (\n" +
            "                        _latestResultTimestamp >= 0L &&\n" +
            "                        timestamp < _latestResultTimestamp\n" +
            "                    )\n";

        source = source.Replace(clearGate, clearGateReplacement);
        return true;
    }

    private static bool PatchSubmissionHistoryMethods(ref string source)
    {
        const string rememberSignature =
            "        private void RememberSubmittedFrame(\n" +
            "            long timestamp,\n" +
            "            long hostTicks)\n";

        if (CountOccurrences(source, rememberSignature) != 1)
        {
            return false;
        }

        const string rememberReplacement =
            "        private void RememberSubmittedFrame(\n" +
            "            long timestamp,\n" +
            "            long hostTicks,\n" +
            "            int cameraGeneration)\n";

        source = source.Replace(rememberSignature, rememberReplacement);

        const string rememberAssign =
            "                _submissionHostTicks[index] =\n" +
            "                    hostTicks;\n\n\n" +
            "                _submissionWriteIndex =\n";

        if (CountOccurrences(source, rememberAssign) != 1)
        {
            return false;
        }

        const string rememberAssignReplacement =
            "                _submissionHostTicks[index] =\n" +
            "                    hostTicks;\n\n" +
            "                _submissionCameraGenerations[index] =\n" +
            "                    cameraGeneration;\n\n\n" +
            "                _submissionWriteIndex =\n";

        source = source.Replace(rememberAssign, rememberAssignReplacement);

        const string resolveSignature =
            "        private long ResolveSubmittedFrameHostTicks(\n" +
            "            long timestamp)\n" +
            "        {\n";

        if (CountOccurrences(source, resolveSignature) != 1)
        {
            return false;
        }

        const string resolveReplacement =
            "        private long ResolveSubmittedFrameHostTicks(\n" +
            "            long timestamp,\n" +
            "            out int cameraGeneration)\n" +
            "        {\n" +
            "            cameraGeneration = 0;\n";

        source = source.Replace(resolveSignature, resolveReplacement);

        const string resolveValue =
            "                        long value =\n" +
            "                            _submissionHostTicks[index];\n\n\n" +
            "                        _submissionTimestamps[index] =\n";

        if (CountOccurrences(source, resolveValue) != 1)
        {
            return false;
        }

        const string resolveValueReplacement =
            "                        long value =\n" +
            "                            _submissionHostTicks[index];\n\n" +
            "                        cameraGeneration =\n" +
            "                            _submissionCameraGenerations[index];\n\n\n" +
            "                        _submissionTimestamps[index] =\n";

        source = source.Replace(resolveValue, resolveValueReplacement);

        const string resolveClear =
            "                        _submissionHostTicks[index] =\n" +
            "                            0L;\n\n\n" +
            "                        return value;\n";

        if (CountOccurrences(source, resolveClear) != 1)
        {
            return false;
        }

        const string resolveClearReplacement =
            "                        _submissionHostTicks[index] =\n" +
            "                            0L;\n\n" +
            "                        _submissionCameraGenerations[index] =\n" +
            "                            0;\n\n\n" +
            "                        return value;\n";

        source = source.Replace(resolveClear, resolveClearReplacement);
        return true;
    }

    private static bool PatchClearGenerationHistory(ref string source)
    {
        const string lifecycleAnchor =
            "            if (clearSubmissionHistory)\n" +
            "            {\n" +
            "                _liveStreamRequestInFlight = false;\n";

        if (CountOccurrences(source, lifecycleAnchor) != 1)
        {
            return false;
        }

        const string lifecycleReplacement =
            "            if (clearSubmissionHistory)\n" +
            "            {\n" +
            "                _liveStreamRequestInFlight = false;\n" +
            "                _kiwiLiveStreamGateCameraGeneration = 0;\n";

        source = source.Replace(lifecycleAnchor, lifecycleReplacement);

        const string clearAnchor =
            "                    _submissionHostTicks[i] =\n" +
            "                        0L;\n";

        if (CountOccurrences(source, clearAnchor) != 1)
        {
            return false;
        }

        const string clearReplacement =
            "                    _submissionHostTicks[i] =\n" +
            "                        0L;\n\n" +
            "                    _submissionCameraGenerations[i] =\n" +
            "                        0;\n";

        source = source.Replace(clearAnchor, clearReplacement);
        return true;
    }

    private static int CountOccurrences(
        string source,
        string value)
    {
        int count = 0;
        int index = 0;

        while (true)
        {
            index = source.IndexOf(
                value,
                index,
                StringComparison.Ordinal);

            if (index < 0)
            {
                return count;
            }

            count++;
            index += value.Length;
        }
    }

    private static string NormalizeNewlines(string value)
    {
        return value
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }

    private static void WritePreservingFormat(
        string path,
        string original,
        string normalized)
    {
        byte[] bytes = File.ReadAllBytes(path);

        bool hasBom =
            bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF;

        string lineEnding =
            original.Contains("\r\n")
                ? "\r\n"
                : "\n";

        if (lineEnding == "\r\n")
        {
            normalized = normalized.Replace("\n", "\r\n");
        }

        File.WriteAllText(
            path,
            normalized,
            new UTF8Encoding(hasBom));
    }
}
#endif
