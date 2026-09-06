using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Opt-in, read-only CONTROL FacePart failure-boundary observer.
///
/// The observer never derives replacement landmarks/crops, never writes Product
/// state, and performs no file I/O while capture is active. Samples are stored in
/// one fixed-capacity array and written once after a terminal observation or
/// normal shutdown. With the environment arm absent it installs no object.
/// </summary>
[DefaultExecutionOrder(32700)]
[DisallowMultipleComponent]
public sealed class KiwiControlBaselineFacePartTrace : MonoBehaviour
{
    private const string EnableEnvironment =
        "KIWI_CONTROL_FACEPART_TRACE";

    private const string EvidenceDirectoryEnvironment =
        "KIWI_CONTROL_FACEPART_TRACE_DIR";

    private const string RuntimeObjectName =
        "[Kiwi] CONTROL FacePart Failure Trace";

    private const int MaximumSamples = 4096;
    private const int RequiredPersistentFailureSamples = 120;

    private static readonly int MaskPointCountId =
        Shader.PropertyToID("_MaskPointCount");

    private static readonly int MaskVisibilityId =
        Shader.PropertyToID("_MaskVisibility");

    private static readonly int PoseVisibilityId =
        Shader.PropertyToID("_PoseVisibility");

    private static readonly int SampleRotationRadId =
        Shader.PropertyToID("_SampleRotationRad");

    private struct PartSample
    {
        public bool bound;
        public bool activeInHierarchy;
        public bool componentEnabled;
        public bool activeAndEnabled;
        public int textureId;
        public int materialId;
        public int shaderId;
        public bool equalsLatestCommittedTexture;
        public Rect uvRect;
        public bool uvRectValid;
        public bool sampleRectAvailable;
        public Rect sampleRect;
        public float maskPointCount;
        public float maskVisibility;
        public float poseVisibility;
        public float sampleRotationRad;
        public float canvasAlpha;
        public float colorAlpha;
        public bool resolverStateAvailable;
        public float resolverAlpha;
        public int resolverReasons;
        public bool nonRenderable;
    }

    private struct Sample
    {
        public int unityFrame;
        public double realtimeSeconds;

        public bool cropperBound;
        public int sourceTextureId;
        public int sourceWidth;
        public int sourceHeight;

        public bool canonicalAvailable;
        public bool canonicalValid;
        public ulong canonicalFrameId;
        public int canonicalUnityFrame;
        public string providerId;
        public bool semanticAvailable;
        public long semanticTimestamp;
        public int semanticLandmarkCount;
        public bool rigidValid;
        public ulong rigidFrameId;
        public int rigidBackend;
        public long rigidTimestamp;
        public long submissionHostTicks;
        public long arrivalHostTicks;
        public bool matchedSubmissionTiming;
        public float geometryQuality;
        public Vector2 faceCenter;
        public Vector2 leftEyeCenter;
        public Vector2 rightEyeCenter;
        public Vector2 nose;
        public Vector2 chin;
        public Quaternion faceRotation;

        public int cameraGeneration;
        public int providerGeneration;
        public int modelGeneration;
        public int configEpoch;
        public int calibrationGeneration;
        public int trackingSessionGeneration;
        public long observationSequence;
        public long semanticTransactionSequence;

        public bool textureTransactionOperational;
        public bool textureSceneBindingValid;
        public bool textureStrictPresentation;
        public long textureSemanticTimestamp;
        public ulong textureCanonicalFrameId;
        public float textureMatchDeltaMs;
        public int textureBufferedFrames;
        public int textureCaptureCount;
        public int textureCommitCount;
        public int textureMissCount;
        public int textureHoldCount;
        public bool textureExternalWriter;
        public int textureExternalWriterCount;
        public bool latestCommittedTextureAvailable;
        public int latestCommittedTextureId;
        public long latestCommittedTextureSemanticTimestamp;
        public ulong latestCommittedTextureCanonicalFrameId;

        public long partDecisionTimestamp;
        public long partDecisionSequence;
        public ulong partDecisionCanonicalFrameId;
        public bool partLeftAccepted;
        public bool partRightAccepted;
        public bool partMouthAccepted;
        public int partLeftRejectCount;
        public int partRightRejectCount;
        public int partMouthRejectCount;

        public PartSample leftEye;
        public PartSample rightEye;
        public PartSample mouth;
        public bool measurementReady;
        public bool allPartsNonRenderable;
    }

    private readonly Sample[] _samples =
        new Sample[MaximumSamples];

    private FacePartCropper _cropper;
    private string _evidenceDirectory;
    private int _sampleCount;
    private int _persistentFailureSamples;
    private int _firstPersistentFailureCandidate = -1;
    private bool _completed;
    private bool _applicationQuitting;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        string enabled =
            Environment.GetEnvironmentVariable(EnableEnvironment);

        if (!string.Equals(enabled, "1", StringComparison.Ordinal))
        {
            return;
        }

        if (
            FindFirstObjectByType<KiwiControlBaselineFacePartTrace>(
                FindObjectsInactive.Include) != null)
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiControlBaselineFacePartTrace>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        string directory =
            Environment.GetEnvironmentVariable(
                EvidenceDirectoryEnvironment);

        if (string.IsNullOrWhiteSpace(directory))
        {
            Debug.LogError(
                "[CONTROL_FACEPART_TRACE] TRACE_DISABLED " +
                "reason=EVIDENCE_DIRECTORY_MISSING");
            enabled = false;
            return;
        }

        _evidenceDirectory =
            Path.GetFullPath(directory.Trim());

        RefreshReferences();

        Debug.Log(
            "[CONTROL_FACEPART_TRACE] TRACE_STARTED " +
            "mode=CONTROL_ONLY memory_only_during_capture=1 " +
            "capacity=" + MaximumSamples.ToString(Invariant) +
            " output=" + _evidenceDirectory);
    }

    private void OnApplicationQuit()
    {
        _applicationQuitting = true;
        Complete("NORMAL_APPLICATION_QUIT");
    }

    private void OnDestroy()
    {
        if (!_completed && !_applicationQuitting)
        {
            Complete("OBSERVER_DESTROYED");
        }
    }

    private void LateUpdate()
    {
        if (_completed || !enabled)
        {
            return;
        }

        if (_sampleCount >= MaximumSamples)
        {
            Complete("BUFFER_CAPACITY_REACHED");
            return;
        }

        RefreshReferences();

        Sample sample =
            CaptureSample();

        _samples[_sampleCount] = sample;

        if (
            sample.measurementReady &&
            sample.allPartsNonRenderable)
        {
            if (_persistentFailureSamples == 0)
            {
                _firstPersistentFailureCandidate =
                    _sampleCount;
            }

            _persistentFailureSamples++;
        }
        else
        {
            _persistentFailureSamples = 0;
            _firstPersistentFailureCandidate = -1;
        }

        _sampleCount++;

        if (
            _persistentFailureSamples >=
                RequiredPersistentFailureSamples)
        {
            Complete("ALL_PARTS_NON_RENDERABLE_PERSISTED");
        }
        else if (_sampleCount >= MaximumSamples)
        {
            Complete("BUFFER_CAPACITY_REACHED");
        }
    }

    private void RefreshReferences()
    {
        if (_cropper == null)
        {
            _cropper =
                FindFirstObjectByType<FacePartCropper>(
                    FindObjectsInactive.Include);
        }
    }

    private Sample CaptureSample()
    {
        Sample sample = default;

        sample.unityFrame = Time.frameCount;
        sample.realtimeSeconds =
            Time.realtimeSinceStartupAsDouble;

        sample.cropperBound =
            _cropper != null;

        Texture source =
            _cropper != null && _cropper.sourceImage != null
                ? _cropper.sourceImage.texture
                : null;

        sample.sourceTextureId =
            GetInstanceId(source);
        sample.sourceWidth =
            source != null ? source.width : 0;
        sample.sourceHeight =
            source != null ? source.height : 0;

        sample.canonicalAvailable =
            KiwiCanonicalTrackingFrame.TryGetFrame(
                out KiwiTrackingFrame frame);

        if (sample.canonicalAvailable)
        {
            sample.canonicalValid = frame.isValid;
            sample.canonicalFrameId = frame.canonicalFrameId;
            sample.canonicalUnityFrame = frame.unityFrame;
            sample.providerId = frame.providerId;
            sample.semanticAvailable = frame.hasSemanticLandmarks;
            sample.semanticTimestamp = frame.semanticTimestamp;
            sample.semanticLandmarkCount =
                frame.semanticLandmarkCount;

            FacePrecisionTrackingData rigid = frame.rigid;
            sample.rigidValid = rigid.isValid;
            sample.rigidFrameId = rigid.frameId;
            sample.rigidBackend = (int)rigid.backend;
            sample.rigidTimestamp = rigid.timestamp;
            sample.submissionHostTicks =
                rigid.submissionHostTicks;
            sample.arrivalHostTicks = rigid.arrivalHostTicks;
            sample.matchedSubmissionTiming =
                rigid.hasMatchedSubmissionTiming;
            sample.geometryQuality = rigid.geometryQuality;
            sample.faceCenter = rigid.faceCenter;
            sample.leftEyeCenter = rigid.leftEyeCenter;
            sample.rightEyeCenter = rigid.rightEyeCenter;
            sample.nose = rigid.nose;
            sample.chin = rigid.chin;
            sample.faceRotation = rigid.faceRotation;

            KiwiRuntimeGenerationContext.Snapshot generation =
                frame.generation;

            sample.cameraGeneration =
                generation.cameraGeneration;
            sample.providerGeneration =
                generation.providerGeneration;
            sample.modelGeneration =
                generation.modelGeneration;
            sample.configEpoch = generation.configEpoch;
            sample.calibrationGeneration =
                generation.calibrationGeneration;
            sample.trackingSessionGeneration =
                generation.trackingSessionGeneration;
            sample.observationSequence =
                generation.observationSequence;
            sample.semanticTransactionSequence =
                generation.semanticTransactionSequence;
        }
        else
        {
            KiwiRuntimeGenerationContext.Snapshot generation =
                KiwiRuntimeGenerationContext.Capture();

            sample.semanticTimestamp = -1L;
            sample.rigidTimestamp = -1L;
            sample.cameraGeneration =
                generation.cameraGeneration;
            sample.providerGeneration =
                generation.providerGeneration;
            sample.modelGeneration =
                generation.modelGeneration;
            sample.configEpoch = generation.configEpoch;
            sample.calibrationGeneration =
                generation.calibrationGeneration;
            sample.trackingSessionGeneration =
                generation.trackingSessionGeneration;
            sample.observationSequence =
                generation.observationSequence;
            sample.semanticTransactionSequence =
                generation.semanticTransactionSequence;
        }

        sample.textureTransactionOperational =
            KiwiFacePartTextureTransaction.IsOperational;
        sample.textureSceneBindingValid =
            KiwiFacePartTextureTransaction.SceneBindingValid;
        sample.textureStrictPresentation =
            KiwiFacePartTextureTransaction.StrictPresentationStarted;
        sample.textureSemanticTimestamp =
            KiwiFacePartTextureTransaction.LastCommittedSemanticTimestamp;
        sample.textureCanonicalFrameId =
            KiwiFacePartTextureTransaction.LastCommittedCanonicalFrameId;
        sample.textureMatchDeltaMs =
            KiwiFacePartTextureTransaction.LastMatchDeltaMs;
        sample.textureBufferedFrames =
            KiwiFacePartTextureTransaction.BufferedFrameCount;
        sample.textureCaptureCount =
            KiwiFacePartTextureTransaction.CaptureCount;
        sample.textureCommitCount =
            KiwiFacePartTextureTransaction.TransactionCommitCount;
        sample.textureMissCount =
            KiwiFacePartTextureTransaction.TransactionMissCount;
        sample.textureHoldCount =
            KiwiFacePartTextureTransaction.SemanticHoldCount;
        sample.textureExternalWriter =
            KiwiFacePartTextureTransaction.ExternalTextureWriterDetected;
        sample.textureExternalWriterCount =
            KiwiFacePartTextureTransaction.ExternalTextureWriterCount;

        sample.latestCommittedTextureAvailable =
            KiwiFacePartTextureTransaction.
                TryGetLastCommittedPresentationFrame(
                    out Texture latestCommittedTexture,
                    out sample.latestCommittedTextureSemanticTimestamp,
                    out sample.latestCommittedTextureCanonicalFrameId);

        sample.latestCommittedTextureId =
            GetInstanceId(latestCommittedTexture);

        sample.partDecisionTimestamp =
            KiwiCommercialFacePartPolicy.PartDecisionTimestamp;
        sample.partDecisionSequence =
            KiwiCommercialFacePartPolicy.SemanticTransactionSequence;
        sample.partDecisionCanonicalFrameId =
            KiwiCommercialFacePartPolicy.TransactionCanonicalFrameId;
        sample.partLeftAccepted =
            KiwiCommercialFacePartPolicy.LastLeftEyeAccepted;
        sample.partRightAccepted =
            KiwiCommercialFacePartPolicy.LastRightEyeAccepted;
        sample.partMouthAccepted =
            KiwiCommercialFacePartPolicy.LastMouthAccepted;
        sample.partLeftRejectCount =
            KiwiCommercialFacePartPolicy.LeftEyeRejectCount;
        sample.partRightRejectCount =
            KiwiCommercialFacePartPolicy.RightEyeRejectCount;
        sample.partMouthRejectCount =
            KiwiCommercialFacePartPolicy.MouthRejectCount;

        sample.leftEye =
            CapturePart(
                _cropper != null
                    ? _cropper.leftEyeImage
                    : null,
                latestCommittedTexture);

        sample.rightEye =
            CapturePart(
                _cropper != null
                    ? _cropper.rightEyeImage
                    : null,
                latestCommittedTexture);

        sample.mouth =
            CapturePart(
                _cropper != null
                    ? _cropper.mouthImage
                    : null,
                latestCommittedTexture);

        sample.measurementReady =
            sample.cropperBound &&
            sample.sourceTextureId != 0 &&
            sample.sourceWidth > 0 &&
            sample.sourceHeight > 0 &&
            sample.canonicalAvailable &&
            sample.canonicalValid &&
            sample.semanticAvailable &&
            sample.rigidValid;

        sample.allPartsNonRenderable =
            sample.leftEye.nonRenderable &&
            sample.rightEye.nonRenderable &&
            sample.mouth.nonRenderable;

        return sample;
    }

    private PartSample CapturePart(
        RawImage image,
        Texture latestCommittedTexture)
    {
        PartSample part = default;
        part.bound = image != null;

        if (image == null)
        {
            part.maskPointCount = -1f;
            part.maskVisibility = float.NaN;
            part.poseVisibility = float.NaN;
            part.sampleRotationRad = float.NaN;
            part.canvasAlpha = float.NaN;
            part.colorAlpha = float.NaN;
            part.resolverAlpha = float.NaN;
            part.nonRenderable = true;
            return part;
        }

        part.activeInHierarchy =
            image.gameObject.activeInHierarchy;
        part.componentEnabled = image.enabled;
        part.activeAndEnabled = image.isActiveAndEnabled;
        part.textureId = GetInstanceId(image.texture);
        part.equalsLatestCommittedTexture =
            latestCommittedTexture != null &&
            ReferenceEquals(
                image.texture,
                latestCommittedTexture);
        part.uvRect = image.uvRect;
        part.uvRectValid = IsValidRect(part.uvRect);

        part.sampleRectAvailable =
            _cropper != null &&
            _cropper.TryGetSampleRect(
                image,
                out part.sampleRect);

        Material material = image.material;
        part.materialId = GetInstanceId(material);
        part.shaderId =
            material != null
                ? GetInstanceId(material.shader)
                : 0;
        part.maskPointCount =
            GetMaterialFloat(
                material,
                MaskPointCountId,
                -1f);
        part.maskVisibility =
            GetMaterialFloat(
                material,
                MaskVisibilityId,
                float.NaN);
        part.poseVisibility =
            GetMaterialFloat(
                material,
                PoseVisibilityId,
                float.NaN);
        part.sampleRotationRad =
            GetMaterialFloat(
                material,
                SampleRotationRadId,
                float.NaN);
        part.canvasAlpha =
            image.canvasRenderer.GetAlpha();
        part.colorAlpha = image.color.a;

        part.resolverStateAvailable =
            KiwiFacePartPresentationResolver.TryGetResolvedState(
                image,
                out part.resolverAlpha,
                out KiwiFacePartPresentationResolver.Reason reasons);

        part.resolverReasons = (int)reasons;

        part.nonRenderable =
            !part.activeInHierarchy ||
            !part.componentEnabled ||
            !part.activeAndEnabled ||
            part.textureId == 0 ||
            !part.uvRectValid ||
            part.maskPointCount < 3f ||
            !IsPositiveVisibility(part.maskVisibility) ||
            !IsPositiveVisibility(part.poseVisibility) ||
            !IsPositiveVisibility(part.canvasAlpha) ||
            !IsPositiveVisibility(part.colorAlpha);

        return part;
    }

    private void Complete(string reason)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;

        try
        {
            Directory.CreateDirectory(_evidenceDirectory);

            string csvPath =
                Path.Combine(
                    _evidenceDirectory,
                    "CONTROL_facepart_trace.csv");

            string summaryPath =
                Path.Combine(
                    _evidenceDirectory,
                    "CONTROL_facepart_trace_summary.txt");

            WriteCsv(csvPath);
            WriteSummary(summaryPath, reason);

            Debug.Log(
                "[CONTROL_FACEPART_TRACE] TRACE_COMPLETE " +
                "reason=" + reason +
                " samples=" + _sampleCount.ToString(Invariant) +
                " firstPersistentFailureCandidate=" +
                _firstPersistentFailureCandidate.ToString(Invariant) +
                " csv=" + csvPath +
                " summary=" + summaryPath);
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[CONTROL_FACEPART_TRACE] TRACE_WRITE_FAILED " +
                exception);
        }
    }

    private void WriteCsv(string path)
    {
        using (
            StreamWriter writer =
                new StreamWriter(
                    new FileStream(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read),
                    new UTF8Encoding(false)))
        {
            writer.WriteLine(BuildHeader());

            for (int i = 0; i < _sampleCount; i++)
            {
                WriteSample(writer, i, _samples[i]);
            }
        }
    }

    private void WriteSummary(
        string path,
        string reason)
    {
        int canonicalValidSamples = 0;
        int semanticSamples = 0;
        int strictTextureSamples = 0;
        int measurementReadySamples = 0;
        int allPartsNonRenderableSamples = 0;
        int leftRenderableSamples = 0;
        int rightRenderableSamples = 0;
        int mouthRenderableSamples = 0;

        for (int i = 0; i < _sampleCount; i++)
        {
            Sample sample = _samples[i];

            if (sample.canonicalAvailable && sample.canonicalValid)
            {
                canonicalValidSamples++;
            }

            if (sample.semanticAvailable)
            {
                semanticSamples++;
            }

            if (sample.textureStrictPresentation)
            {
                strictTextureSamples++;
            }

            if (sample.measurementReady)
            {
                measurementReadySamples++;
            }

            if (sample.allPartsNonRenderable)
            {
                allPartsNonRenderableSamples++;
            }

            if (!sample.leftEye.nonRenderable)
            {
                leftRenderableSamples++;
            }

            if (!sample.rightEye.nonRenderable)
            {
                rightRenderableSamples++;
            }

            if (!sample.mouth.nonRenderable)
            {
                mouthRenderableSamples++;
            }
        }

        Sample last =
            _sampleCount > 0
                ? _samples[_sampleCount - 1]
                : default;

        using (
            StreamWriter writer =
                new StreamWriter(
                    new FileStream(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read),
                    new UTF8Encoding(false)))
        {
            writer.WriteLine(
                "CONTROL_BASELINE_FACEPART_TRACE");
            writer.WriteLine("capture_mode=CONTROL_ONLY");
            writer.WriteLine("capture_hotpath_file_io=0");
            writer.WriteLine("capture_hotpath_gpu_readback=0");
            writer.WriteLine("capture_hotpath_texture_copy=0");
            writer.WriteLine("capture_hotpath_product_write=0");
            writer.WriteLine(
                "buffer_capacity=" +
                MaximumSamples.ToString(Invariant));
            writer.WriteLine(
                "completion_reason=" + reason);
            writer.WriteLine(
                "sample_count=" +
                _sampleCount.ToString(Invariant));
            writer.WriteLine(
                "first_persistent_failure_candidate_index=" +
                _firstPersistentFailureCandidate.ToString(Invariant));
            writer.WriteLine(
                "canonical_valid_samples=" +
                canonicalValidSamples.ToString(Invariant));
            writer.WriteLine(
                "semantic_available_samples=" +
                semanticSamples.ToString(Invariant));
            writer.WriteLine(
                "strict_texture_samples=" +
                strictTextureSamples.ToString(Invariant));
            writer.WriteLine(
                "measurement_ready_samples=" +
                measurementReadySamples.ToString(Invariant));
            writer.WriteLine(
                "all_parts_nonrenderable_samples=" +
                allPartsNonRenderableSamples.ToString(Invariant));
            writer.WriteLine(
                "left_renderable_samples=" +
                leftRenderableSamples.ToString(Invariant));
            writer.WriteLine(
                "right_renderable_samples=" +
                rightRenderableSamples.ToString(Invariant));
            writer.WriteLine(
                "mouth_renderable_samples=" +
                mouthRenderableSamples.ToString(Invariant));
            writer.WriteLine(
                "last_canonical_frame_id=" +
                last.canonicalFrameId.ToString(Invariant));
            writer.WriteLine(
                "last_texture_canonical_frame_id=" +
                last.textureCanonicalFrameId.ToString(Invariant));
            writer.WriteLine(
                "last_texture_commit_count=" +
                last.textureCommitCount.ToString(Invariant));
            writer.WriteLine(
                "last_texture_miss_count=" +
                last.textureMissCount.ToString(Invariant));
            writer.WriteLine(
                "last_texture_hold_count=" +
                last.textureHoldCount.ToString(Invariant));
            writer.WriteLine(
                "per_part_committed_semantic_identity=N/A");
            writer.WriteLine(
                "per_part_committed_semantic_identity_reason=" +
                "Production exposes one global last-committed semantic/canonical " +
                "identity and per-part actual texture references, but no per-part " +
                "semantic identity.");
            writer.WriteLine(
                "selected_capture_host_ticks=N/A");
            writer.WriteLine(
                "selected_capture_host_ticks_reason=" +
                "Production exposes only the resulting nearest-match delta, not " +
                "the selected history slot hostTicks.");
            writer.WriteLine(
                "prepare_result_per_semantic=N/A");
            writer.WriteLine(
                "prepare_result_per_semantic_reason=" +
                "Production exposes cumulative commit/miss/hold counters, not a " +
                "per-semantic Prepare result.");
            writer.WriteLine(
                "f2_exact_native_identity_proven=NO");
        }
    }

    private static string BuildHeader()
    {
        const string canonical =
            "sampleIndex,unityFrame,realtimeSeconds," +
            "cropperBound,sourceTextureId,sourceWidth,sourceHeight," +
            "canonicalAvailable,canonicalValid,canonicalFrameId,canonicalUnityFrame," +
            "providerId,semanticAvailable,semanticTimestamp,semanticLandmarkCount," +
            "rigidValid,rigidFrameId,rigidBackend,rigidTimestamp,submissionHostTicks," +
            "arrivalHostTicks,matchedSubmissionTiming,geometryQuality," +
            "faceCenterX,faceCenterY,leftEyeCenterX,leftEyeCenterY," +
            "rightEyeCenterX,rightEyeCenterY,noseX,noseY,chinX,chinY," +
            "faceRotationX,faceRotationY,faceRotationZ,faceRotationW," +
            "cameraGeneration,providerGeneration,modelGeneration,configEpoch," +
            "calibrationGeneration,trackingSessionGeneration,observationSequence," +
            "semanticTransactionSequence," +
            "textureTransactionOperational,textureSceneBindingValid," +
            "textureStrictPresentation,textureSemanticTimestamp," +
            "textureCanonicalFrameId,textureMatchDeltaMs,textureBufferedFrames," +
            "textureCaptureCount,textureCommitCount,textureMissCount," +
            "textureHoldCount,textureExternalWriter,textureExternalWriterCount," +
            "latestCommittedTextureAvailable,latestCommittedTextureId," +
            "latestCommittedTextureSemanticTimestamp," +
            "latestCommittedTextureCanonicalFrameId," +
            "partDecisionTimestamp,partDecisionSequence," +
            "partDecisionCanonicalFrameId,partLeftAccepted,partRightAccepted," +
            "partMouthAccepted,partLeftRejectCount,partRightRejectCount," +
            "partMouthRejectCount,measurementReady,allPartsNonRenderable";

        return
            canonical +
            PartHeader("left") +
            PartHeader("right") +
            PartHeader("mouth");
    }

    private static string PartHeader(string prefix)
    {
        return
            "," + prefix + "Bound" +
            "," + prefix + "ActiveInHierarchy" +
            "," + prefix + "ComponentEnabled" +
            "," + prefix + "ActiveAndEnabled" +
            "," + prefix + "TextureId" +
            "," + prefix + "MaterialId" +
            "," + prefix + "ShaderId" +
            "," + prefix + "EqualsLatestCommittedTexture" +
            "," + prefix + "UvX" +
            "," + prefix + "UvY" +
            "," + prefix + "UvW" +
            "," + prefix + "UvH" +
            "," + prefix + "UvValid" +
            "," + prefix + "SampleRectAvailable" +
            "," + prefix + "SampleX" +
            "," + prefix + "SampleY" +
            "," + prefix + "SampleW" +
            "," + prefix + "SampleH" +
            "," + prefix + "MaskPointCount" +
            "," + prefix + "MaskVisibility" +
            "," + prefix + "PoseVisibility" +
            "," + prefix + "SampleRotationRad" +
            "," + prefix + "CanvasAlpha" +
            "," + prefix + "ColorAlpha" +
            "," + prefix + "ResolverStateAvailable" +
            "," + prefix + "ResolverAlpha" +
            "," + prefix + "ResolverReasons" +
            "," + prefix + "NonRenderable";
    }

    private static void WriteSample(
        StreamWriter writer,
        int index,
        Sample sample)
    {
        Write(writer, index); Sep(writer);
        Write(writer, sample.unityFrame); Sep(writer);
        Write(writer, sample.realtimeSeconds); Sep(writer);
        Write(writer, sample.cropperBound); Sep(writer);
        Write(writer, sample.sourceTextureId); Sep(writer);
        Write(writer, sample.sourceWidth); Sep(writer);
        Write(writer, sample.sourceHeight); Sep(writer);
        Write(writer, sample.canonicalAvailable); Sep(writer);
        Write(writer, sample.canonicalValid); Sep(writer);
        Write(writer, sample.canonicalFrameId); Sep(writer);
        Write(writer, sample.canonicalUnityFrame); Sep(writer);
        WriteEscaped(writer, sample.providerId); Sep(writer);
        Write(writer, sample.semanticAvailable); Sep(writer);
        Write(writer, sample.semanticTimestamp); Sep(writer);
        Write(writer, sample.semanticLandmarkCount); Sep(writer);
        Write(writer, sample.rigidValid); Sep(writer);
        Write(writer, sample.rigidFrameId); Sep(writer);
        Write(writer, sample.rigidBackend); Sep(writer);
        Write(writer, sample.rigidTimestamp); Sep(writer);
        Write(writer, sample.submissionHostTicks); Sep(writer);
        Write(writer, sample.arrivalHostTicks); Sep(writer);
        Write(writer, sample.matchedSubmissionTiming); Sep(writer);
        Write(writer, sample.geometryQuality); Sep(writer);
        Write(writer, sample.faceCenter.x); Sep(writer);
        Write(writer, sample.faceCenter.y); Sep(writer);
        Write(writer, sample.leftEyeCenter.x); Sep(writer);
        Write(writer, sample.leftEyeCenter.y); Sep(writer);
        Write(writer, sample.rightEyeCenter.x); Sep(writer);
        Write(writer, sample.rightEyeCenter.y); Sep(writer);
        Write(writer, sample.nose.x); Sep(writer);
        Write(writer, sample.nose.y); Sep(writer);
        Write(writer, sample.chin.x); Sep(writer);
        Write(writer, sample.chin.y); Sep(writer);
        Write(writer, sample.faceRotation.x); Sep(writer);
        Write(writer, sample.faceRotation.y); Sep(writer);
        Write(writer, sample.faceRotation.z); Sep(writer);
        Write(writer, sample.faceRotation.w); Sep(writer);
        Write(writer, sample.cameraGeneration); Sep(writer);
        Write(writer, sample.providerGeneration); Sep(writer);
        Write(writer, sample.modelGeneration); Sep(writer);
        Write(writer, sample.configEpoch); Sep(writer);
        Write(writer, sample.calibrationGeneration); Sep(writer);
        Write(writer, sample.trackingSessionGeneration); Sep(writer);
        Write(writer, sample.observationSequence); Sep(writer);
        Write(writer, sample.semanticTransactionSequence); Sep(writer);
        Write(writer, sample.textureTransactionOperational); Sep(writer);
        Write(writer, sample.textureSceneBindingValid); Sep(writer);
        Write(writer, sample.textureStrictPresentation); Sep(writer);
        Write(writer, sample.textureSemanticTimestamp); Sep(writer);
        Write(writer, sample.textureCanonicalFrameId); Sep(writer);
        Write(writer, sample.textureMatchDeltaMs); Sep(writer);
        Write(writer, sample.textureBufferedFrames); Sep(writer);
        Write(writer, sample.textureCaptureCount); Sep(writer);
        Write(writer, sample.textureCommitCount); Sep(writer);
        Write(writer, sample.textureMissCount); Sep(writer);
        Write(writer, sample.textureHoldCount); Sep(writer);
        Write(writer, sample.textureExternalWriter); Sep(writer);
        Write(writer, sample.textureExternalWriterCount); Sep(writer);
        Write(writer, sample.latestCommittedTextureAvailable); Sep(writer);
        Write(writer, sample.latestCommittedTextureId); Sep(writer);
        Write(writer, sample.latestCommittedTextureSemanticTimestamp); Sep(writer);
        Write(writer, sample.latestCommittedTextureCanonicalFrameId); Sep(writer);
        Write(writer, sample.partDecisionTimestamp); Sep(writer);
        Write(writer, sample.partDecisionSequence); Sep(writer);
        Write(writer, sample.partDecisionCanonicalFrameId); Sep(writer);
        Write(writer, sample.partLeftAccepted); Sep(writer);
        Write(writer, sample.partRightAccepted); Sep(writer);
        Write(writer, sample.partMouthAccepted); Sep(writer);
        Write(writer, sample.partLeftRejectCount); Sep(writer);
        Write(writer, sample.partRightRejectCount); Sep(writer);
        Write(writer, sample.partMouthRejectCount); Sep(writer);
        Write(writer, sample.measurementReady); Sep(writer);
        Write(writer, sample.allPartsNonRenderable);

        WritePart(writer, sample.leftEye);
        WritePart(writer, sample.rightEye);
        WritePart(writer, sample.mouth);
        writer.WriteLine();
    }

    private static void WritePart(
        StreamWriter writer,
        PartSample part)
    {
        Sep(writer); Write(writer, part.bound);
        Sep(writer); Write(writer, part.activeInHierarchy);
        Sep(writer); Write(writer, part.componentEnabled);
        Sep(writer); Write(writer, part.activeAndEnabled);
        Sep(writer); Write(writer, part.textureId);
        Sep(writer); Write(writer, part.materialId);
        Sep(writer); Write(writer, part.shaderId);
        Sep(writer); Write(writer, part.equalsLatestCommittedTexture);
        Sep(writer); Write(writer, part.uvRect.x);
        Sep(writer); Write(writer, part.uvRect.y);
        Sep(writer); Write(writer, part.uvRect.width);
        Sep(writer); Write(writer, part.uvRect.height);
        Sep(writer); Write(writer, part.uvRectValid);
        Sep(writer); Write(writer, part.sampleRectAvailable);
        Sep(writer); Write(writer, part.sampleRect.x);
        Sep(writer); Write(writer, part.sampleRect.y);
        Sep(writer); Write(writer, part.sampleRect.width);
        Sep(writer); Write(writer, part.sampleRect.height);
        Sep(writer); Write(writer, part.maskPointCount);
        Sep(writer); Write(writer, part.maskVisibility);
        Sep(writer); Write(writer, part.poseVisibility);
        Sep(writer); Write(writer, part.sampleRotationRad);
        Sep(writer); Write(writer, part.canvasAlpha);
        Sep(writer); Write(writer, part.colorAlpha);
        Sep(writer); Write(writer, part.resolverStateAvailable);
        Sep(writer); Write(writer, part.resolverAlpha);
        Sep(writer); Write(writer, part.resolverReasons);
        Sep(writer); Write(writer, part.nonRenderable);
    }

    private static void Write(
        StreamWriter writer,
        bool value)
    {
        writer.Write(value ? "1" : "0");
    }

    private static void Write(
        StreamWriter writer,
        int value)
    {
        writer.Write(value.ToString(Invariant));
    }

    private static void Write(
        StreamWriter writer,
        long value)
    {
        writer.Write(value.ToString(Invariant));
    }

    private static void Write(
        StreamWriter writer,
        ulong value)
    {
        writer.Write(value.ToString(Invariant));
    }

    private static void Write(
        StreamWriter writer,
        float value)
    {
        writer.Write(value.ToString("R", Invariant));
    }

    private static void Write(
        StreamWriter writer,
        double value)
    {
        writer.Write(value.ToString("R", Invariant));
    }

    private static void WriteEscaped(
        StreamWriter writer,
        string value)
    {
        value = value ?? string.Empty;
        writer.Write('"');
        writer.Write(value.Replace("\"", "\"\""));
        writer.Write('"');
    }

    private static void Sep(StreamWriter writer)
    {
        writer.Write(',');
    }

    private static float GetMaterialFloat(
        Material material,
        int propertyId,
        float fallback)
    {
        return
            material != null &&
            material.HasProperty(propertyId)
                ? material.GetFloat(propertyId)
                : fallback;
    }

    private static int GetInstanceId(UnityEngine.Object value)
    {
        return value != null
            ? value.GetInstanceID()
            : 0;
    }

    private static bool IsPositiveVisibility(float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value) &&
            value > 0.03f;
    }

    private static bool IsValidRect(Rect rect)
    {
        return
            IsFinite(rect.x) &&
            IsFinite(rect.y) &&
            IsFinite(rect.width) &&
            IsFinite(rect.height) &&
            rect.width > 0.000001f &&
            rect.height > 0.000001f;
    }

    private static bool IsFinite(float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
    }

    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;
}
