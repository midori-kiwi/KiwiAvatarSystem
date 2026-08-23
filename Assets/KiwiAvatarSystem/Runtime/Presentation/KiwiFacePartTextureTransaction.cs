using System;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Phase 16.9 commercial face-part camera-frame transaction.
///
/// FacePartCropper geometry and FacePartShapeMask already consume one canonical
/// semantic frame. Before Phase 16.9 the three RawImages still referenced the
/// live camera texture every LateUpdate, so pixels could advance while semantic
/// crop/mask geometry was intentionally held. This service keeps a small GPU
/// history of recent camera frames and pins each Eye/Mouth output to the frame
/// whose host time matches the adopted canonical semantic sample.
///
/// This is presentation-local only:
/// - no Root writes;
/// - no tracking-provider changes;
/// - no global one-frame display delay;
/// - no CPU readback;
/// - no GPU callback writes directly to presentation.
/// </summary>
[DefaultExecutionOrder(640)]
[DisallowMultipleComponent]
public sealed class KiwiFacePartTextureTransaction : MonoBehaviour
{
    // KIWI_V5_1_PHASE16_9_FACEPART_TEXTURE_TRANSACTION
    private const string RuntimeObjectName =
        "[Kiwi] Face-Part Texture Transaction";

    private const int DesiredHistorySlots = 20;
    private const int MinimumHistorySlots = 6;
    private const int MaximumHistorySlots = 24;
    private const int MaximumSnapshotWidth = 1280;
    private const long MaximumHistoryBytes = 96L * 1024L * 1024L;
    private const float MaximumSubmissionMatchDeltaMs = 55f;

    private sealed class Slot
    {
        public RenderTexture texture;
        public bool valid;
        public long hostTicks;
        public int unityFrame;
        public int cameraGeneration;
        public int trackingSessionGeneration;
    }

    private static KiwiFacePartTextureTransaction _instance;

    private FacePartCropper _cropper;
    private Slot[] _slots;
    private int _writeCursor;
    private int _leftSlot = -1;
    private int _rightSlot = -1;
    private int _mouthSlot = -1;

    // KIWI_V5_1_PHASE16_19_3_MATCHED_LANDMARK_PREVIEW_EPOCH
    // Observer-only handle to the exact camera snapshot matched to the most
    // recently committed semantic transaction. This is never a presentation
    // writer; the Frame Comparison overlay uses it only for like-for-like
    // camera/Landmark diagnostics.
    private int _lastCommittedSlot = -1;

    private int _stagedSlot = -1;
    private long _stagedSemanticTimestamp = -1L;
    private ulong _stagedCanonicalFrameId;

    private Texture _source;
    private int _sourceInstanceId;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _snapshotWidth;
    private int _snapshotHeight;
    private int _boundCameraGeneration;
    private int _boundTrackingSessionGeneration;
    private int _lastCapturedUnityFrame = -1;

    private bool _strictPresentationStarted;
    private bool _sceneBindingValid;
    private bool _externalTextureWriterDetected;
    private long _lastCommittedSemanticTimestamp = -1L;
    private ulong _lastCommittedCanonicalFrameId;
    private float _lastMatchDeltaMs = -1f;
    private int _captureCount;
    private int _transactionCommitCount;
    private int _transactionMissCount;
    private int _semanticHoldCount;
    private int _externalTextureWriterCount;
    private long _lastMissTimestamp = long.MinValue;
    private long _lastHoldTimestamp = long.MinValue;
    private int _lastExternalWriterFrame = -1;

    public static bool IsOperational =>
        _instance != null &&
        _instance._cropper != null &&
        _instance._slots != null &&
        _instance._slots.Length > 0;

    public static bool SceneBindingValid =>
        _instance != null && _instance._sceneBindingValid;

    public static bool StrictPresentationStarted =>
        _instance != null && _instance._strictPresentationStarted;

    public static bool ExternalTextureWriterDetected =>
        _instance != null && _instance._externalTextureWriterDetected;

    public static long LastCommittedSemanticTimestamp =>
        _instance != null
            ? _instance._lastCommittedSemanticTimestamp
            : -1L;

    public static ulong LastCommittedCanonicalFrameId =>
        _instance != null
            ? _instance._lastCommittedCanonicalFrameId
            : 0UL;

    public static float LastMatchDeltaMs =>
        _instance != null ? _instance._lastMatchDeltaMs : -1f;

    public static int BufferedFrameCount =>
        _instance != null ? _instance.CountValidSlots() : 0;

    public static int CaptureCount =>
        _instance != null ? _instance._captureCount : 0;

    public static int TransactionCommitCount =>
        _instance != null ? _instance._transactionCommitCount : 0;

    public static int TransactionMissCount =>
        _instance != null ? _instance._transactionMissCount : 0;

    public static int SemanticHoldCount =>
        _instance != null ? _instance._semanticHoldCount : 0;

    public static int ExternalTextureWriterCount =>
        _instance != null ? _instance._externalTextureWriterCount : 0;

    // KIWI_V5_1_PHASE16_19_3_MATCHED_LANDMARK_PREVIEW_EPOCH
    /// <summary>
    /// Observer-only access to the camera snapshot that belongs to the latest
    /// committed semantic FacePart transaction. The returned texture is owned
    /// by this service and must never be modified, released, or reassigned by
    /// the caller.
    /// </summary>
    public static bool TryGetLastCommittedPresentationFrame(
        out Texture texture,
        out long semanticTimestamp,
        out ulong canonicalFrameId)
    {
        texture = null;
        semanticTimestamp = -1L;
        canonicalFrameId = 0UL;

        KiwiFacePartTextureTransaction service =
            _instance;

        if (
            service == null ||
            !service._strictPresentationStarted ||
            service._slots == null ||
            service._lastCommittedSlot < 0 ||
            service._lastCommittedSlot >= service._slots.Length
        )
        {
            return false;
        }

        Slot slot =
            service._slots[service._lastCommittedSlot];

        if (
            slot == null ||
            !slot.valid ||
            slot.texture == null ||
            service._lastCommittedSemanticTimestamp < 0L
        )
        {
            return false;
        }

        texture = slot.texture;
        semanticTimestamp =
            service._lastCommittedSemanticTimestamp;
        canonicalFrameId =
            service._lastCommittedCanonicalFrameId;

        return true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        EnsureInstance();
    }

    private static KiwiFacePartTextureTransaction EnsureInstance()
    {
        if (_instance != null)
        {
            return _instance;
        }

        KiwiFacePartTextureTransaction existing =
            FindFirstObjectByType<KiwiFacePartTextureTransaction>(
                FindObjectsInactive.Include);

        if (existing != null)
        {
            _instance = existing;
            return existing;
        }

        GameObject host = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        _instance = host.AddComponent<KiwiFacePartTextureTransaction>();
        return _instance;
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        Application.onBeforeRender -= HandleBeforeRender;
        Application.onBeforeRender += HandleBeforeRender;

        RefreshBinding(true);
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        Application.onBeforeRender -= HandleBeforeRender;
        ReleaseSlots();

        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _cropper = null;
        RefreshBinding(true);
    }

    private void LateUpdate()
    {
        RefreshBinding(false);
        CaptureSourceFrameIfNeeded();
        ApplyPinnedTextures();
    }

    /// <summary>
    /// Called by the Phase 16.9 FacePartCropper migration before semantic crop
    /// adoption. Returning false means there is no camera snapshot that can be
    /// proven to belong to this semantic sample, so Crop + Mask + Texture all
    /// hold together instead of mixing timestamps.
    /// </summary>
    public static bool PrepareSemanticTransaction(
        FacePartCropper cropper,
        long semanticTimestamp)
    {
        KiwiFacePartTextureTransaction service =
            EnsureInstance();

        if (service == null)
        {
            return true;
        }

        service.BindExplicitCropper(cropper);
        service.CaptureSourceFrameIfNeeded();

        if (!KiwiCanonicalTrackingFrame.IsRuntimeCoordinatorActive)
        {
            // Compatibility path for legacy/early startup. Existing behaviour is
            // preserved until the canonical commercial coordinator is active.
            return true;
        }

        if (
            !KiwiCanonicalTrackingFrame.TryGetFrame(
                out KiwiTrackingFrame frame) ||
            !frame.isValid ||
            !frame.hasSemanticLandmarks ||
            frame.semanticTimestamp != semanticTimestamp ||
            !frame.rigid.isValid ||
            frame.rigid.timestamp != semanticTimestamp ||
            !frame.rigid.hasMatchedSubmissionTiming ||
            frame.rigid.submissionHostTicks <= 0L
        )
        {
            service.RecordTransactionMiss(semanticTimestamp);
            return false;
        }

        int bestSlot =
            service.FindClosestSlot(
                frame.rigid.submissionHostTicks,
                frame.generation.cameraGeneration,
                frame.generation.trackingSessionGeneration,
                out float deltaMs);

        if (bestSlot < 0 || deltaMs > MaximumSubmissionMatchDeltaMs)
        {
            service.RecordTransactionMiss(semanticTimestamp);
            return false;
        }

        service._stagedSlot = bestSlot;
        service._stagedSemanticTimestamp = semanticTimestamp;
        service._stagedCanonicalFrameId = frame.canonicalFrameId;
        service._lastMatchDeltaMs = deltaMs;

        return true;
    }

    /// <summary>
    /// Called after FacePartCropper has made its per-part accept/reject decision.
    /// Accepted parts move to the matched camera snapshot. Rejected parts remain
    /// pinned to their previous complete transaction, exactly like their crop and
    /// mask geometry.
    /// </summary>
    public static void CommitPartDecision(
        FacePartCropper cropper,
        long semanticTimestamp,
        bool leftEyeAccepted,
        bool rightEyeAccepted,
        bool mouthAccepted)
    {
        KiwiFacePartTextureTransaction service =
            EnsureInstance();

        if (service == null)
        {
            return;
        }

        service.BindExplicitCropper(cropper);

        if (
            service._stagedSlot < 0 ||
            service._stagedSemanticTimestamp != semanticTimestamp
        )
        {
            service.RecordSemanticHold(semanticTimestamp);
            service.ApplyPinnedTextures();
            return;
        }

        int slot = service._stagedSlot;

        // The first proven semantic transaction must pin all three outputs.
        // Otherwise a part rejected on that first sample would keep a reference
        // to the live WebCamTexture and continue advancing independently.
        bool initializedAnyPart = false;

        if (service._leftSlot < 0 || leftEyeAccepted)
        {
            service._leftSlot = slot;
            initializedAnyPart = true;
        }

        if (service._rightSlot < 0 || rightEyeAccepted)
        {
            service._rightSlot = slot;
            initializedAnyPart = true;
        }

        if (service._mouthSlot < 0 || mouthAccepted)
        {
            service._mouthSlot = slot;
            initializedAnyPart = true;
        }

        if (
            initializedAnyPart ||
            leftEyeAccepted ||
            rightEyeAccepted ||
            mouthAccepted
        )
        {
            service._strictPresentationStarted = true;
            service._lastCommittedSlot = slot;
            service._lastCommittedSemanticTimestamp = semanticTimestamp;
            service._lastCommittedCanonicalFrameId =
                service._stagedCanonicalFrameId;
            service._transactionCommitCount++;
        }

        service._stagedSlot = -1;
        service._stagedSemanticTimestamp = -1L;
        service._stagedCanonicalFrameId = 0UL;

        service.ApplyPinnedTextures();
    }

    /// <summary>
    /// Replaces the old unconditional `RawImage.texture = sourceImage.texture`
    /// writes. Before the first proven transaction only, the live source remains
    /// a startup compatibility fallback. Once strict presentation begins, every
    /// part keeps its pinned immutable snapshot until a matching semantic sample
    /// is accepted.
    /// </summary>
    public static void ApplyCurrentPresentationTextures(
        FacePartCropper cropper)
    {
        KiwiFacePartTextureTransaction service =
            EnsureInstance();

        if (service == null)
        {
            return;
        }

        service.BindExplicitCropper(cropper);
        service.CaptureSourceFrameIfNeeded();
        service.ApplyPinnedTextures();
    }

    private void RefreshBinding(bool force)
    {
        if (force || _cropper == null)
        {
            _cropper =
                FindFirstObjectByType<FacePartCropper>(
                    FindObjectsInactive.Include);
        }

        ValidateSceneBinding();

        if (_cropper == null || _cropper.sourceImage == null)
        {
            ResetSourceBinding();
            return;
        }

        Texture source = _cropper.sourceImage.texture;
        if (source == null)
        {
            ResetSourceBinding();
            return;
        }

        KiwiRuntimeGenerationContext.Snapshot generation =
            KiwiRuntimeGenerationContext.Capture();

        int sourceId = source.GetInstanceID();
        int width = Mathf.Max(1, source.width);
        int height = Mathf.Max(1, source.height);

        bool sourceChanged =
            sourceId != _sourceInstanceId ||
            width != _sourceWidth ||
            height != _sourceHeight ||
            generation.cameraGeneration != _boundCameraGeneration ||
            generation.trackingSessionGeneration !=
                _boundTrackingSessionGeneration;

        _source = source;

        if (sourceChanged)
        {
            RebuildSlots(
                sourceId,
                width,
                height,
                generation.cameraGeneration,
                generation.trackingSessionGeneration);
        }
    }

    private void BindExplicitCropper(
        FacePartCropper cropper)
    {
        if (cropper != null && _cropper != cropper)
        {
            _cropper = cropper;
            RefreshBinding(true);
        }
        else
        {
            RefreshBinding(false);
        }
    }

    private void ValidateSceneBinding()
    {
        _sceneBindingValid =
            _cropper != null &&
            _cropper.sourceImage != null &&
            _cropper.leftEyeImage != null &&
            _cropper.rightEyeImage != null &&
            _cropper.mouthImage != null &&
            _cropper.leftEyeImage != _cropper.rightEyeImage &&
            _cropper.leftEyeImage != _cropper.mouthImage &&
            _cropper.rightEyeImage != _cropper.mouthImage;
    }

    private void CaptureSourceFrameIfNeeded()
    {
        RefreshBinding(false);

        if (
            _source == null ||
            _slots == null ||
            _slots.Length == 0 ||
            _lastCapturedUnityFrame == Time.frameCount
        )
        {
            return;
        }

        if (
            _source is WebCamTexture webCam &&
            !webCam.didUpdateThisFrame
        )
        {
            return;
        }

        int slotIndex = FindWritableSlot();
        if (slotIndex < 0)
        {
            return;
        }

        Slot slot = _slots[slotIndex];
        if (slot == null || slot.texture == null)
        {
            return;
        }

        long captureTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();

        Graphics.Blit(_source, slot.texture);

        KiwiRuntimeGenerationContext.Snapshot generation =
            KiwiRuntimeGenerationContext.Capture();

        slot.valid = true;
        slot.hostTicks = captureTicks;
        slot.unityFrame = Time.frameCount;
        slot.cameraGeneration = generation.cameraGeneration;
        slot.trackingSessionGeneration =
            generation.trackingSessionGeneration;

        _lastCapturedUnityFrame = Time.frameCount;
        _writeCursor = (slotIndex + 1) % _slots.Length;
        _captureCount++;
    }

    private int FindWritableSlot()
    {
        if (_slots == null || _slots.Length == 0)
        {
            return -1;
        }

        for (int offset = 0; offset < _slots.Length; offset++)
        {
            int index =
                (_writeCursor + offset) % _slots.Length;

            if (!IsPinned(index))
            {
                return index;
            }
        }

        return -1;
    }

    private bool IsPinned(int slotIndex)
    {
        return
            slotIndex == _leftSlot ||
            slotIndex == _rightSlot ||
            slotIndex == _mouthSlot;
    }

    private int FindClosestSlot(
        long submissionHostTicks,
        int cameraGeneration,
        int trackingSessionGeneration,
        out float deltaMs)
    {
        deltaMs = float.PositiveInfinity;
        int best = -1;

        if (
            _slots == null ||
            submissionHostTicks <= 0L
        )
        {
            return -1;
        }

        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            if (
                slot == null ||
                !slot.valid ||
                slot.hostTicks <= 0L ||
                slot.cameraGeneration != cameraGeneration ||
                slot.trackingSessionGeneration != trackingSessionGeneration
            )
            {
                continue;
            }

            long tickDelta =
                Math.Abs(slot.hostTicks - submissionHostTicks);

            float currentDeltaMs =
                (float)(
                    KiwiPrecisionTrackingMath.HostTicksToSeconds(
                        tickDelta) *
                    1000.0);

            if (currentDeltaMs < deltaMs)
            {
                deltaMs = currentDeltaMs;
                best = i;
            }
        }

        return best;
    }

    private void ApplyPinnedTextures()
    {
        if (
            _cropper == null ||
            _cropper.sourceImage == null ||
            _cropper.leftEyeImage == null ||
            _cropper.rightEyeImage == null ||
            _cropper.mouthImage == null
        )
        {
            return;
        }

        if (!_strictPresentationStarted)
        {
            Texture startup = _cropper.sourceImage.texture;
            if (startup != null)
            {
                _cropper.leftEyeImage.texture = startup;
                _cropper.rightEyeImage.texture = startup;
                _cropper.mouthImage.texture = startup;
            }

            return;
        }

        ApplyPartTexture(
            _cropper.leftEyeImage,
            _leftSlot);
        ApplyPartTexture(
            _cropper.rightEyeImage,
            _rightSlot);
        ApplyPartTexture(
            _cropper.mouthImage,
            _mouthSlot);
    }

    private void ApplyPartTexture(
        UnityEngine.UI.RawImage image,
        int slotIndex)
    {
        if (
            image == null ||
            slotIndex < 0 ||
            _slots == null ||
            slotIndex >= _slots.Length
        )
        {
            return;
        }

        Slot slot = _slots[slotIndex];
        if (slot != null && slot.valid && slot.texture != null)
        {
            image.texture = slot.texture;
        }
    }

    private void HandleBeforeRender()
    {
        _externalTextureWriterDetected = false;

        if (
            !_strictPresentationStarted ||
            _cropper == null
        )
        {
            return;
        }

        bool mismatch =
            !MatchesPinnedTexture(
                _cropper.leftEyeImage,
                _leftSlot) ||
            !MatchesPinnedTexture(
                _cropper.rightEyeImage,
                _rightSlot) ||
            !MatchesPinnedTexture(
                _cropper.mouthImage,
                _mouthSlot);

        _externalTextureWriterDetected = mismatch;

        if (
            mismatch &&
            _lastExternalWriterFrame != Time.frameCount
        )
        {
            _lastExternalWriterFrame = Time.frameCount;
            _externalTextureWriterCount++;
        }
    }

    private bool MatchesPinnedTexture(
        UnityEngine.UI.RawImage image,
        int slotIndex)
    {
        if (
            image == null ||
            slotIndex < 0 ||
            _slots == null ||
            slotIndex >= _slots.Length
        )
        {
            return true;
        }

        Slot slot = _slots[slotIndex];
        return
            slot == null ||
            !slot.valid ||
            slot.texture == null ||
            ReferenceEquals(image.texture, slot.texture);
    }

    private void RecordTransactionMiss(long semanticTimestamp)
    {
        _stagedSlot = -1;
        _stagedSemanticTimestamp = -1L;
        _stagedCanonicalFrameId = 0UL;

        if (_lastMissTimestamp != semanticTimestamp)
        {
            _lastMissTimestamp = semanticTimestamp;
            _transactionMissCount++;
        }

        RecordSemanticHold(semanticTimestamp);
    }

    private void RecordSemanticHold(long semanticTimestamp)
    {
        if (_lastHoldTimestamp != semanticTimestamp)
        {
            _lastHoldTimestamp = semanticTimestamp;
            _semanticHoldCount++;
        }
    }

    private int CountValidSlots()
    {
        if (_slots == null)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i] != null && _slots[i].valid)
            {
                count++;
            }
        }

        return count;
    }

    private void RebuildSlots(
        int sourceInstanceId,
        int sourceWidth,
        int sourceHeight,
        int cameraGeneration,
        int trackingSessionGeneration)
    {
        ReleaseSlots();

        _sourceInstanceId = sourceInstanceId;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _boundCameraGeneration = cameraGeneration;
        _boundTrackingSessionGeneration = trackingSessionGeneration;

        float scale =
            sourceWidth > MaximumSnapshotWidth
                ? MaximumSnapshotWidth / (float)sourceWidth
                : 1f;

        _snapshotWidth =
            Mathf.Max(1, Mathf.RoundToInt(sourceWidth * scale));
        _snapshotHeight =
            Mathf.Max(1, Mathf.RoundToInt(sourceHeight * scale));

        long bytesPerSlot =
            Math.Max(
                1L,
                (long)_snapshotWidth *
                _snapshotHeight *
                4L);

        int budgetSlots =
            (int)Math.Max(
                MinimumHistorySlots,
                Math.Min(
                    MaximumHistorySlots,
                    MaximumHistoryBytes / bytesPerSlot));

        int slotCount =
            Mathf.Clamp(
                Mathf.Min(
                    DesiredHistorySlots,
                    budgetSlots),
                MinimumHistorySlots,
                MaximumHistorySlots);

        _slots = new Slot[slotCount];

        for (int i = 0; i < slotCount; i++)
        {
            RenderTexture texture =
                new RenderTexture(
                    _snapshotWidth,
                    _snapshotHeight,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default)
                {
                    name =
                        "KiwiFacePartTransaction_" + i,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false
                };

            texture.Create();

            _slots[i] = new Slot
            {
                texture = texture,
                valid = false
            };
        }

        _writeCursor = 0;
        _leftSlot = -1;
        _rightSlot = -1;
        _mouthSlot = -1;
        _lastCommittedSlot = -1;
        _stagedSlot = -1;
        _stagedSemanticTimestamp = -1L;
        _stagedCanonicalFrameId = 0UL;
        _strictPresentationStarted = false;
        _lastCommittedSemanticTimestamp = -1L;
        _lastCommittedCanonicalFrameId = 0UL;
        _lastMatchDeltaMs = -1f;
        _lastCapturedUnityFrame = -1;
    }

    private void ResetSourceBinding()
    {
        _source = null;
        _sourceInstanceId = 0;
        _sourceWidth = 0;
        _sourceHeight = 0;
        _snapshotWidth = 0;
        _snapshotHeight = 0;
        _boundCameraGeneration = 0;
        _boundTrackingSessionGeneration = 0;
        ReleaseSlots();
    }

    private void ReleaseSlots()
    {
        if (_slots != null)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                RenderTexture texture =
                    _slots[i] != null
                        ? _slots[i].texture
                        : null;

                if (texture == null)
                {
                    continue;
                }

                texture.Release();

                if (Application.isPlaying)
                {
                    Destroy(texture);
                }
                else
                {
                    DestroyImmediate(texture);
                }
            }
        }

        _slots = null;
        _writeCursor = 0;
        _leftSlot = -1;
        _rightSlot = -1;
        _mouthSlot = -1;
        _lastCommittedSlot = -1;
        _stagedSlot = -1;
        _strictPresentationStarted = false;
    }
}
