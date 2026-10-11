#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// DEVELOPMENT-only, bounded, nonblocking observer for the exact
/// GUI.DrawTexture(Repaint) -> final framebuffer boundary. It owns one
/// persistent WaitForEndOfFrame coroutine and never publishes Product state.
/// </summary>
[DefaultExecutionOrder(40001)]
public sealed class KiwiH1FinalFramebufferObserver : MonoBehaviour
{
    private const string EnableEnvironment = "KIWI_H1_BOUNDARY_RUNTIME";
    private const string OutputDirectoryEnvironment = "KIWI_H1_BOUNDARY_OUTPUT";
    private const int SourceWidth = 384;
    private const int SourceHeight = 384;
    private const int BytesPerPixel = 4;
    private const int SourceByteCount = SourceWidth * SourceHeight * BytesPerPixel;
    private const int SlotCount = 4;
    private const int ColorToleranceLsb = 2;
    private const double DrainTimeoutSeconds = 5.0;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();
    private static bool _installed;
    private static bool _armed;
    private static KiwiH1FinalFramebufferObserver _activeInstance;

    private enum Disposition
    {
        DRAW_OBSERVED,
        QUEUED_PENDING_END_OF_FRAME,
        COVERAGE_GAP_NOT_ARMED,
        COVERAGE_GAP_ACTIVE_INSTANCE_NULL,
        COVERAGE_GAP_NOT_REPAINT,
        COVERAGE_GAP_MEASUREMENT_CLOSED,
        COVERAGE_GAP_IDENTITY_NOT_EXACT,
        COVERAGE_GAP_CALL_FRAME_IDENTITY_MISMATCH,
        COVERAGE_GAP_SEMANTIC_IDENTITY_MISMATCH,
        COVERAGE_GAP_RASTER_IDENTITY_MISMATCH,
        COVERAGE_GAP_SOURCE_RASTER_INVALID,
        COVERAGE_GAP_RECT_OR_SCREEN_INVALID,
        COVERAGE_GAP_GUI_COLOR_NOT_WHITE,
        COVERAGE_GAP_PENDING_SUPERSEDED,
        COVERAGE_GAP_PENDING_NOT_SAME_FRAME,
        COVERAGE_GAP_SCREEN_CHANGED_BEFORE_CAPTURE,
        COVERAGE_GAP_ASYNC_READBACK_UNSUPPORTED,
        COVERAGE_GAP_POOL_EXHAUSTED,
        COVERAGE_GAP_CAPTURE_REQUEST_EXCEPTION,
        REQUEST_SUBMITTED,
        COMPLETE_GEOMETRY_PRESENT,
        COVERAGE_GAP_READBACK_ERROR,
        COVERAGE_GAP_UNEXPECTED_READBACK_SIZE,
        COVERAGE_GAP_ACTIVE_COLOR_SPACE_UNSUPPORTED,
        COVERAGE_GAP_EXPECTED_MASK_EMPTY,
        COVERAGE_GAP_PALETTE_INCOMPLETE_OR_UNKNOWN,
        COVERAGE_GAP_ORIENTATION_AMBIGUOUS,
        FAIL_EXPECTED_PIXEL_MISSING,
        QUIT_INCOMPLETE_PENDING,
        QUIT_INCOMPLETE_READBACK
    }

    private struct Identity
    {
        public long drawEventSequence;
        public long callAttemptSequence;
        public long observationHostTicks;
        public string segment;
        public EventType eventType;
        public int callUnityFrame;
        public long visualSequence;
        public int visualUnityFrame;
        public long semanticTimestamp;
        public ulong canonicalFrameId;
        public ulong publicationSequence;
        public ulong sourceFrameId;
        public int sourceGeneration;
        public long sourceHostTicks;
        public long previewSemanticTimestamp;
        public long rasterSequence;
        public long rasterSemanticTimestamp;
        public ulong rasterCanonicalFrameId;
        public Rect drawRect;
        public int screenWidth;
        public int screenHeight;
        public int sourceTextureInstanceId;
        public string sourceFormat;
        public string sourceGraphicsFormat;
        public string sourceFilterMode;
        public string sourceSha256;
        public string activeColorSpace;
        public string graphicsDeviceType;
    }

    private sealed class Pending
    {
        public bool valid;
        public Identity identity;
        public readonly byte[] sourceBytes = new byte[SourceByteCount];
    }

    private sealed class Slot
    {
        public RenderTexture framebuffer;
        public bool inFlight;
        public bool outcomeWritten;
        public long requestSequence;
        public Identity identity;
        public bool renderTextureSrgb;
        public string renderTextureGraphicsFormat = string.Empty;
        public readonly byte[] sourceBytes = new byte[SourceByteCount];
        public byte[] framebufferBytes;
    }

    private struct PixelFailure
    {
        public bool valid;
        public int screenX;
        public int screenYBottom;
        public int guiYTop;
        public int sourceX;
        public int sourceYBottom;
        public byte sourceR;
        public byte sourceG;
        public byte sourceB;
        public byte sourceA;
        public byte actualR;
        public byte actualG;
        public byte actualB;
        public byte actualA;
        public int expectedRMin;
        public int expectedRMax;
        public int expectedGMin;
        public int expectedGMax;
        public int expectedBMin;
        public int expectedBMax;
    }

    private struct Candidate
    {
        public long occupied;
        public long landmark;
        public long redAnchor;
        public long greenAnchor;
        public long yellowAnchor;
        public long magentaAnchor;
        public long unknownPalette;
        public long mismatch;
        public PixelFailure firstFailure;
    }

    private StreamWriter _dispositionWriter;
    private StreamWriter _requestWriter;
    private StreamWriter _exactWriter;
    private StreamWriter _firstFailureWriter;
    private string _outputDirectory;
    private string _summaryPath;
    private readonly Pending _pending = new Pending();
    private readonly Slot[] _slots = new Slot[SlotCount];
    private bool _accepting = true;
    private bool _quitting;
    private bool _firstFailureWritten;
    private bool _shutdownRequested;
    private double _shutdownRequestedRealtime;
    private long _drawEventSequence;
    private long _requestSequence;
    private long _drawObservedCount;
    private long _queuedCount;
    private long _requestedCount;
    private long _completedCount;
    private long _passCount;
    private long _failCount;
    private long _coverageGapCount;
    private long _poolExhaustedCount;
    private long _readbackErrorCount;
    private long _pendingSupersededCount;
    private long _afterCloseIgnoredCount;
    private int _inFlight;
    private int _maxInFlight;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed || !IsEnabled()) return;
        _installed = true;
        _armed = true;
        var host = new GameObject("[Kiwi] H1 Final Framebuffer Observer");
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiH1FinalFramebufferObserver>();
    }

    private static bool IsEnabled()
    {
        string value = Environment.GetEnvironmentVariable(EnableEnvironment);
        return Debug.isDebugBuild &&
            (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    internal static void ObserveDraw(
        long callAttemptSequence,
        EventType eventType,
        int callUnityFrame,
        long observationHostTicks,
        string segment,
        bool visualValid,
        long visualSequence,
        int visualUnityFrame,
        long visualSemanticTimestamp,
        ulong canonicalFrameId,
        bool acceptedPublicationExact,
        ulong publicationSequence,
        ulong sourceFrameId,
        int sourceGeneration,
        long sourceHostTicks,
        long semanticTimestamp,
        long previewSemanticTimestamp,
        bool rasterValid,
        long rasterSequence,
        long rasterSemanticTimestamp,
        ulong rasterCanonicalFrameId,
        Rect drawRect,
        int screenWidth,
        int screenHeight,
        Texture2D overlayTexture,
        Color32[] cpuPixels,
        Color guiColor)
    {
        KiwiH1FinalFramebufferObserver instance = _activeInstance;
        if (instance == null)
        {
            Debug.LogError(
                "[KiwiH1FinalFramebuffer] " +
                Disposition.COVERAGE_GAP_ACTIVE_INSTANCE_NULL +
                " callAttemptSequence=" + callAttemptSequence);
            return;
        }

        instance.QueueDraw(
            callAttemptSequence,
            eventType,
            callUnityFrame,
            observationHostTicks,
            segment,
            visualValid,
            visualSequence,
            visualUnityFrame,
            visualSemanticTimestamp,
            canonicalFrameId,
            acceptedPublicationExact,
            publicationSequence,
            sourceFrameId,
            sourceGeneration,
            sourceHostTicks,
            semanticTimestamp,
            previewSemanticTimestamp,
            rasterValid,
            rasterSequence,
            rasterSemanticTimestamp,
            rasterCanonicalFrameId,
            drawRect,
            screenWidth,
            screenHeight,
            overlayTexture,
            cpuPixels,
            guiColor);
    }

    internal static bool BeginShutdownAndIsDrained()
    {
        KiwiH1FinalFramebufferObserver instance = _activeInstance;
        if (instance == null) return true;

        if (!instance._shutdownRequested)
        {
            instance._shutdownRequested = true;
            instance._accepting = false;
            instance._shutdownRequestedRealtime =
                Time.realtimeSinceStartupAsDouble;
        }

        bool drained = !instance._pending.valid && instance._inFlight == 0;
        bool timedOut =
            Time.realtimeSinceStartupAsDouble -
                instance._shutdownRequestedRealtime >=
            DrainTimeoutSeconds;
        return drained || timedOut;
    }

    private void Awake()
    {
        _activeInstance = this;
        Application.runInBackground = true;
        _outputDirectory = ResolveOutputDirectory();
        Directory.CreateDirectory(_outputDirectory);
        _summaryPath = Path.Combine(
            _outputDirectory,
            "final_framebuffer_summary.txt");

        _dispositionWriter = CreateWriter(
            "final_framebuffer_entry_disposition.csv",
            IdentityHeader() + ",disposition");
        _requestWriter = CreateWriter(
            "final_framebuffer_request.csv",
            "requestSequence,captureHostTicks,captureUnityFrame," +
            "renderTextureSrgb,renderTextureGraphicsFormat," +
            IdentityHeader());
        _exactWriter = CreateWriter(
            "final_framebuffer_exact.csv",
            "requestSequence,completionHostTicks,completionUnityFrame," +
            "byteCount,renderTextureSrgb,renderTextureGraphicsFormat," +
            "framebufferSha256,nonzeroByteCount,nonzeroRgbPixelCount," +
            "nonopaqueAlphaPixelCount,firstNonzeroX,firstNonzeroYBottom," +
            "firstNonzeroR,firstNonzeroG,firstNonzeroB,firstNonzeroA," +
            "expectedOccupiedPixels,expectedLandmarkPixels," +
            "expectedRedAnchorPixels,expectedGreenAnchorPixels," +
            "expectedYellowAnchorPixels,expectedMagentaAnchorPixels," +
            "unknownPalettePixels,bottomRowZeroMismatchPixels," +
            "topRowZeroMismatchPixels,orientationCandidateCount," +
            "resolvedReadbackRowZero,coverageStatus," +
            IdentityHeader());
        _firstFailureWriter = CreateWriter(
            "final_framebuffer_first_failure.csv",
            "requestSequence,coverageStatus,resolvedOrBestReadbackRowZero," +
            "screenX,screenYBottom,guiYTop,sourceX,sourceYBottom," +
            "sourceR,sourceG,sourceB,sourceA,actualR,actualG,actualB,actualA," +
            "expectedRMin,expectedRMax,expectedGMin,expectedGMax," +
            "expectedBMin,expectedBMax," + IdentityHeader());

        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new Slot();
        }

        StartCoroutine(CaptureLoop());
        WriteSummary(false);
    }

    private IEnumerator CaptureLoop()
    {
        while (true)
        {
            yield return EndOfFrame;
            if (_quitting) yield break;
            CapturePendingAtEndOfFrame();
        }
    }

    private void QueueDraw(
        long callAttemptSequence,
        EventType eventType,
        int callUnityFrame,
        long observationHostTicks,
        string segment,
        bool visualValid,
        long visualSequence,
        int visualUnityFrame,
        long visualSemanticTimestamp,
        ulong canonicalFrameId,
        bool acceptedPublicationExact,
        ulong publicationSequence,
        ulong sourceFrameId,
        int sourceGeneration,
        long sourceHostTicks,
        long semanticTimestamp,
        long previewSemanticTimestamp,
        bool rasterValid,
        long rasterSequence,
        long rasterSemanticTimestamp,
        ulong rasterCanonicalFrameId,
        Rect drawRect,
        int screenWidth,
        int screenHeight,
        Texture2D overlayTexture,
        Color32[] cpuPixels,
        Color guiColor)
    {
        Identity identity = new Identity
        {
            drawEventSequence = ++_drawEventSequence,
            callAttemptSequence = callAttemptSequence,
            observationHostTicks = observationHostTicks,
            segment = segment ?? string.Empty,
            eventType = eventType,
            callUnityFrame = callUnityFrame,
            visualSequence = visualSequence,
            visualUnityFrame = visualUnityFrame,
            semanticTimestamp = semanticTimestamp,
            canonicalFrameId = canonicalFrameId,
            publicationSequence = publicationSequence,
            sourceFrameId = sourceFrameId,
            sourceGeneration = sourceGeneration,
            sourceHostTicks = sourceHostTicks,
            previewSemanticTimestamp = previewSemanticTimestamp,
            rasterSequence = rasterSequence,
            rasterSemanticTimestamp = rasterSemanticTimestamp,
            rasterCanonicalFrameId = rasterCanonicalFrameId,
            drawRect = drawRect,
            screenWidth = screenWidth,
            screenHeight = screenHeight,
            sourceTextureInstanceId = overlayTexture != null
                ? overlayTexture.GetInstanceID()
                : 0,
            sourceFormat = overlayTexture != null
                ? overlayTexture.format.ToString()
                : string.Empty,
            sourceGraphicsFormat = overlayTexture != null
                ? overlayTexture.graphicsFormat.ToString()
                : string.Empty,
            sourceFilterMode = overlayTexture != null
                ? overlayTexture.filterMode.ToString()
                : string.Empty,
            activeColorSpace = QualitySettings.activeColorSpace.ToString(),
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString()
        };

        _drawObservedCount++;
        WriteDisposition(identity, Disposition.DRAW_OBSERVED);

        if (!_armed)
        {
            WriteGap(identity, Disposition.COVERAGE_GAP_NOT_ARMED);
            return;
        }
        if (eventType != EventType.Repaint)
        {
            WriteGap(identity, Disposition.COVERAGE_GAP_NOT_REPAINT);
            return;
        }
        if (!_accepting)
        {
            _afterCloseIgnoredCount++;
            WriteDisposition(identity, Disposition.COVERAGE_GAP_MEASUREMENT_CLOSED);
            return;
        }
        if (!visualValid || visualSequence <= 0L ||
            callAttemptSequence <= 0L || canonicalFrameId == 0UL ||
            !acceptedPublicationExact || publicationSequence == 0UL ||
            sourceFrameId == 0UL || sourceHostTicks <= 0L)
        {
            WriteGap(identity, Disposition.COVERAGE_GAP_IDENTITY_NOT_EXACT);
            return;
        }
        if (callUnityFrame != Time.frameCount || visualUnityFrame > callUnityFrame)
        {
            WriteGap(identity, Disposition.COVERAGE_GAP_CALL_FRAME_IDENTITY_MISMATCH);
            return;
        }
        if (semanticTimestamp != visualSemanticTimestamp ||
            previewSemanticTimestamp != semanticTimestamp)
        {
            WriteGap(identity, Disposition.COVERAGE_GAP_SEMANTIC_IDENTITY_MISMATCH);
            return;
        }
        if (!rasterValid || rasterSequence <= 0L ||
            rasterSemanticTimestamp != semanticTimestamp ||
            rasterCanonicalFrameId != canonicalFrameId)
        {
            WriteGap(identity, Disposition.COVERAGE_GAP_RASTER_IDENTITY_MISMATCH);
            return;
        }
        if (overlayTexture == null ||
            overlayTexture.width != SourceWidth ||
            overlayTexture.height != SourceHeight ||
            overlayTexture.format != TextureFormat.RGBA32 ||
            overlayTexture.filterMode != FilterMode.Point ||
            cpuPixels == null || cpuPixels.Length != SourceWidth * SourceHeight)
        {
            WriteGap(identity, Disposition.COVERAGE_GAP_SOURCE_RASTER_INVALID);
            return;
        }
        if (!RectAndScreenValid(drawRect, screenWidth, screenHeight))
        {
            WriteGap(identity, Disposition.COVERAGE_GAP_RECT_OR_SCREEN_INVALID);
            return;
        }
        if (!ApproximatelyWhite(guiColor))
        {
            WriteGap(identity, Disposition.COVERAGE_GAP_GUI_COLOR_NOT_WHITE);
            return;
        }

        if (_pending.valid)
        {
            _pendingSupersededCount++;
            WriteGap(
                _pending.identity,
                Disposition.COVERAGE_GAP_PENDING_SUPERSEDED);
        }

        EncodeColor32Bytes(cpuPixels, _pending.sourceBytes);
        identity.sourceSha256 = Sha256Hex(_pending.sourceBytes);
        _pending.identity = identity;
        _pending.valid = true;
        _queuedCount++;
        WriteDisposition(identity, Disposition.QUEUED_PENDING_END_OF_FRAME);
    }

    private void CapturePendingAtEndOfFrame()
    {
        if (!_pending.valid) return;

        Identity identity = _pending.identity;
        if (identity.callUnityFrame != Time.frameCount)
        {
            _pending.valid = false;
            WriteGap(identity, Disposition.COVERAGE_GAP_PENDING_NOT_SAME_FRAME);
            return;
        }
        if (Screen.width != identity.screenWidth ||
            Screen.height != identity.screenHeight)
        {
            _pending.valid = false;
            WriteGap(identity, Disposition.COVERAGE_GAP_SCREEN_CHANGED_BEFORE_CAPTURE);
            return;
        }
        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            _pending.valid = false;
            WriteGap(identity, Disposition.COVERAGE_GAP_ASYNC_READBACK_UNSUPPORTED);
            return;
        }

        int slotIndex = FindFreeSlot();
        if (slotIndex < 0)
        {
            _pending.valid = false;
            _poolExhaustedCount++;
            WriteGap(identity, Disposition.COVERAGE_GAP_POOL_EXHAUSTED);
            return;
        }

        Slot slot = _slots[slotIndex];
        EnsureFramebuffer(slot, identity.screenWidth, identity.screenHeight);
        Buffer.BlockCopy(
            _pending.sourceBytes,
            0,
            slot.sourceBytes,
            0,
            SourceByteCount);
        _pending.valid = false;

        slot.identity = identity;
        slot.requestSequence = ++_requestSequence;
        slot.inFlight = true;
        slot.outcomeWritten = false;
        slot.renderTextureSrgb = slot.framebuffer.sRGB;
        slot.renderTextureGraphicsFormat =
            slot.framebuffer.graphicsFormat.ToString();
        _inFlight++;
        _maxInFlight = Math.Max(_maxInFlight, _inFlight);
        _requestedCount++;

        long captureHostTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();
        int captureUnityFrame = Time.frameCount;

        try
        {
            ScreenCapture.CaptureScreenshotIntoRenderTexture(
                slot.framebuffer);
            WriteRequest(
                slot,
                captureHostTicks,
                captureUnityFrame);
            WriteDisposition(identity, Disposition.REQUEST_SUBMITTED);
            AsyncGPUReadback.Request(
                slot.framebuffer,
                0,
                TextureFormat.RGBA32,
                request => CompleteReadback(slotIndex, request));
        }
        catch (Exception ex)
        {
            slot.inFlight = false;
            _inFlight = Math.Max(0, _inFlight - 1);
            WriteGap(
                identity,
                Disposition.COVERAGE_GAP_CAPTURE_REQUEST_EXCEPTION,
                ex.GetType().Name);
        }
    }

    private void CompleteReadback(
        int slotIndex,
        AsyncGPUReadbackRequest request)
    {
        if (slotIndex < 0 || slotIndex >= _slots.Length) return;
        Slot slot = _slots[slotIndex];
        if (!slot.inFlight || slot.outcomeWritten) return;

        long completionHostTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();
        int completionUnityFrame = Time.frameCount;
        int byteCount = 0;
        Disposition disposition;
        Candidate bottom = default;
        Candidate top = default;
        int orientationCandidateCount = 0;
        string resolvedOrientation = string.Empty;
        string framebufferSha256 = string.Empty;
        long nonzeroByteCount = 0L;
        long nonzeroRgbPixelCount = 0L;
        long nonopaqueAlphaPixelCount = 0L;
        int firstNonzeroX = -1;
        int firstNonzeroYBottom = -1;
        byte firstNonzeroR = 0;
        byte firstNonzeroG = 0;
        byte firstNonzeroB = 0;
        byte firstNonzeroA = 0;

        try
        {
            if (request.hasError)
            {
                _readbackErrorCount++;
                disposition = Disposition.COVERAGE_GAP_READBACK_ERROR;
            }
            else
            {
                var data = request.GetData<byte>();
                byteCount = data.Length;
                int expectedByteCount =
                    slot.identity.screenWidth *
                    slot.identity.screenHeight *
                    BytesPerPixel;

                if (byteCount != expectedByteCount)
                {
                    disposition =
                        Disposition.COVERAGE_GAP_UNEXPECTED_READBACK_SIZE;
                }
                else if (!string.Equals(
                    slot.identity.activeColorSpace,
                    ColorSpace.Linear.ToString(),
                    StringComparison.Ordinal))
                {
                    disposition =
                        Disposition.COVERAGE_GAP_ACTIVE_COLOR_SPACE_UNSUPPORTED;
                }
                else
                {
                    if (slot.framebufferBytes == null ||
                        slot.framebufferBytes.Length != expectedByteCount)
                    {
                        slot.framebufferBytes = new byte[expectedByteCount];
                    }
                    data.CopyTo(slot.framebufferBytes);
                    framebufferSha256 = Sha256Hex(slot.framebufferBytes);
                    ComputeFramebufferStats(
                        slot.framebufferBytes,
                        slot.identity.screenWidth,
                        slot.identity.screenHeight,
                        out nonzeroByteCount,
                        out nonzeroRgbPixelCount,
                        out nonopaqueAlphaPixelCount,
                        out firstNonzeroX,
                        out firstNonzeroYBottom,
                        out firstNonzeroR,
                        out firstNonzeroG,
                        out firstNonzeroB,
                        out firstNonzeroA);

                    bottom = EvaluateCandidate(
                        slot.identity,
                        slot.sourceBytes,
                        slot.framebufferBytes,
                        true,
                        slot.renderTextureSrgb);
                    top = EvaluateCandidate(
                        slot.identity,
                        slot.sourceBytes,
                        slot.framebufferBytes,
                        false,
                        slot.renderTextureSrgb);

                    bool bottomPass = CandidatePass(bottom);
                    bool topPass = CandidatePass(top);
                    orientationCandidateCount =
                        (bottomPass ? 1 : 0) +
                        (topPass ? 1 : 0);

                    if (bottom.occupied <= 0L || top.occupied <= 0L)
                    {
                        disposition =
                            Disposition.COVERAGE_GAP_EXPECTED_MASK_EMPTY;
                    }
                    else if (!PaletteComplete(bottom) || !PaletteComplete(top))
                    {
                        disposition =
                            Disposition.COVERAGE_GAP_PALETTE_INCOMPLETE_OR_UNKNOWN;
                    }
                    else if (orientationCandidateCount == 1)
                    {
                        resolvedOrientation =
                            bottomPass
                                ? "BOTTOM"
                                : "TOP";
                        disposition =
                            Disposition.COMPLETE_GEOMETRY_PRESENT;
                    }
                    else if (orientationCandidateCount > 1)
                    {
                        disposition =
                            Disposition.COVERAGE_GAP_ORIENTATION_AMBIGUOUS;
                    }
                    else
                    {
                        disposition =
                            Disposition.FAIL_EXPECTED_PIXEL_MISSING;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            disposition =
                Disposition.COVERAGE_GAP_READBACK_ERROR;
            _readbackErrorCount++;
            Debug.LogError(
                "[KiwiH1FinalFramebuffer] callback exception=" +
                ex.GetType().Name);
        }

        WriteExact(
            slot,
            completionHostTicks,
            completionUnityFrame,
            byteCount,
            framebufferSha256,
            nonzeroByteCount,
            nonzeroRgbPixelCount,
            nonopaqueAlphaPixelCount,
            firstNonzeroX,
            firstNonzeroYBottom,
            firstNonzeroR,
            firstNonzeroG,
            firstNonzeroB,
            firstNonzeroA,
            bottom,
            top,
            orientationCandidateCount,
            resolvedOrientation,
            disposition);

        if (disposition == Disposition.COMPLETE_GEOMETRY_PRESENT)
        {
            _passCount++;
        }
        else if (disposition == Disposition.FAIL_EXPECTED_PIXEL_MISSING)
        {
            _failCount++;
            Candidate best =
                bottom.mismatch <= top.mismatch
                    ? bottom
                    : top;
            WriteFirstFailure(
                slot,
                disposition,
                bottom.mismatch <= top.mismatch
                    ? "BOTTOM"
                    : "TOP",
                best.firstFailure);
        }
        else
        {
            _coverageGapCount++;
            Candidate best =
                bottom.mismatch <= top.mismatch
                    ? bottom
                    : top;
            if (best.firstFailure.valid)
            {
                WriteFirstFailure(
                    slot,
                    disposition,
                    bottom.mismatch <= top.mismatch
                        ? "BOTTOM"
                        : "TOP",
                    best.firstFailure);
            }
        }

        slot.outcomeWritten = true;
        slot.inFlight = false;
        _completedCount++;
        _inFlight = Math.Max(0, _inFlight - 1);
    }

    private static Candidate EvaluateCandidate(
        Identity identity,
        byte[] source,
        byte[] framebuffer,
        bool rowZeroBottom,
        bool srgbReadback)
    {
        Candidate result = default;
        Rect rect = identity.drawRect;
        int minX = Mathf.Max(0, Mathf.CeilToInt(rect.xMin - 0.5f));
        int maxX = Mathf.Min(
            identity.screenWidth,
            Mathf.CeilToInt(rect.xMax - 0.5f));
        int minGuiY = Mathf.Max(0, Mathf.CeilToInt(rect.yMin - 0.5f));
        int maxGuiY = Mathf.Min(
            identity.screenHeight,
            Mathf.CeilToInt(rect.yMax - 0.5f));

        for (int guiY = minGuiY; guiY < maxGuiY; guiY++)
        {
            float vFromTop =
                ((guiY + 0.5f) - rect.yMin) / rect.height;
            int sourceYBottom = Mathf.Clamp(
                Mathf.FloorToInt((1f - vFromTop) * SourceHeight),
                0,
                SourceHeight - 1);
            int screenYBottom = identity.screenHeight - 1 - guiY;
            int readbackY = rowZeroBottom
                ? screenYBottom
                : identity.screenHeight - 1 - screenYBottom;

            for (int x = minX; x < maxX; x++)
            {
                float u = ((x + 0.5f) - rect.xMin) / rect.width;
                int sourceX = Mathf.Clamp(
                    Mathf.FloorToInt(u * SourceWidth),
                    0,
                    SourceWidth - 1);
                int sourceOffset =
                    (sourceYBottom * SourceWidth + sourceX) *
                    BytesPerPixel;
                byte sourceA = source[sourceOffset + 3];
                if (sourceA == 0) continue;

                result.occupied++;
                ClassifyPalette(
                    source[sourceOffset],
                    source[sourceOffset + 1],
                    source[sourceOffset + 2],
                    sourceA,
                    ref result);

                int actualOffset =
                    (readbackY * identity.screenWidth + x) *
                    BytesPerPixel;
                byte actualR = framebuffer[actualOffset];
                byte actualG = framebuffer[actualOffset + 1];
                byte actualB = framebuffer[actualOffset + 2];
                byte actualA = framebuffer[actualOffset + 3];

                ChannelRange r = ExpectedRange(
                    source[sourceOffset], sourceA, srgbReadback);
                ChannelRange g = ExpectedRange(
                    source[sourceOffset + 1], sourceA, srgbReadback);
                ChannelRange b = ExpectedRange(
                    source[sourceOffset + 2], sourceA, srgbReadback);

                bool match =
                    InRange(actualR, r) &&
                    InRange(actualG, g) &&
                    InRange(actualB, b);
                if (match) continue;

                result.mismatch++;
                if (!result.firstFailure.valid)
                {
                    result.firstFailure = new PixelFailure
                    {
                        valid = true,
                        screenX = x,
                        screenYBottom = screenYBottom,
                        guiYTop = guiY,
                        sourceX = sourceX,
                        sourceYBottom = sourceYBottom,
                        sourceR = source[sourceOffset],
                        sourceG = source[sourceOffset + 1],
                        sourceB = source[sourceOffset + 2],
                        sourceA = sourceA,
                        actualR = actualR,
                        actualG = actualG,
                        actualB = actualB,
                        actualA = actualA,
                        expectedRMin = r.minimum,
                        expectedRMax = r.maximum,
                        expectedGMin = g.minimum,
                        expectedGMax = g.maximum,
                        expectedBMin = b.minimum,
                        expectedBMax = b.maximum
                    };
                }
            }
        }

        return result;
    }

    private struct ChannelRange
    {
        public int minimum;
        public int maximum;
    }

    private static ChannelRange ExpectedRange(
        byte source,
        byte alpha,
        bool srgbReadback)
    {
        float sourceLinear = source / 255f;
        float a = alpha / 255f;
        float minimumLinear = a * sourceLinear;
        float maximumLinear = minimumLinear + (1f - a);
        float minimum = srgbReadback
            ? Mathf.LinearToGammaSpace(minimumLinear)
            : minimumLinear;
        float maximum = srgbReadback
            ? Mathf.LinearToGammaSpace(maximumLinear)
            : maximumLinear;
        return new ChannelRange
        {
            minimum = Mathf.Clamp(
                Mathf.FloorToInt(minimum * 255f) - ColorToleranceLsb,
                0,
                255),
            maximum = Mathf.Clamp(
                Mathf.CeilToInt(maximum * 255f) + ColorToleranceLsb,
                0,
                255)
        };
    }

    private static bool InRange(byte actual, ChannelRange range)
    {
        return actual >= range.minimum && actual <= range.maximum;
    }

    private static void ComputeFramebufferStats(
        byte[] bytes,
        int width,
        int height,
        out long nonzeroByteCount,
        out long nonzeroRgbPixelCount,
        out long nonopaqueAlphaPixelCount,
        out int firstNonzeroX,
        out int firstNonzeroYBottom,
        out byte firstNonzeroR,
        out byte firstNonzeroG,
        out byte firstNonzeroB,
        out byte firstNonzeroA)
    {
        nonzeroByteCount = 0L;
        nonzeroRgbPixelCount = 0L;
        nonopaqueAlphaPixelCount = 0L;
        firstNonzeroX = -1;
        firstNonzeroYBottom = -1;
        firstNonzeroR = 0;
        firstNonzeroG = 0;
        firstNonzeroB = 0;
        firstNonzeroA = 0;

        for (int offset = 0; offset < bytes.Length; offset += BytesPerPixel)
        {
            byte r = bytes[offset];
            byte g = bytes[offset + 1];
            byte b = bytes[offset + 2];
            byte a = bytes[offset + 3];
            if (r != 0) nonzeroByteCount++;
            if (g != 0) nonzeroByteCount++;
            if (b != 0) nonzeroByteCount++;
            if (a != 0) nonzeroByteCount++;
            if (a != 255) nonopaqueAlphaPixelCount++;
            if (r == 0 && g == 0 && b == 0) continue;
            nonzeroRgbPixelCount++;
            if (firstNonzeroX >= 0) continue;
            int pixel = offset / BytesPerPixel;
            firstNonzeroX = pixel % width;
            firstNonzeroYBottom = pixel / width;
            firstNonzeroR = r;
            firstNonzeroG = g;
            firstNonzeroB = b;
            firstNonzeroA = a;
        }
    }

    private static void ClassifyPalette(
        byte r,
        byte g,
        byte b,
        byte a,
        ref Candidate result)
    {
        if (r == 50 && g == 235 && b == 255 && a == 235)
            result.landmark++;
        else if (r == 255 && g == 70 && b == 70 && a == 255)
            result.redAnchor++;
        else if (r == 70 && g == 255 && b == 90 && a == 255)
            result.greenAnchor++;
        else if (r == 255 && g == 230 && b == 60 && a == 255)
            result.yellowAnchor++;
        else if (r == 255 && g == 90 && b == 235 && a == 255)
            result.magentaAnchor++;
        else
            result.unknownPalette++;
    }

    private static bool CandidatePass(Candidate candidate)
    {
        return PaletteComplete(candidate) && candidate.mismatch == 0L;
    }

    private static bool PaletteComplete(Candidate candidate)
    {
        return candidate.occupied > 0L &&
            candidate.landmark > 0L &&
            candidate.redAnchor > 0L &&
            candidate.greenAnchor > 0L &&
            candidate.yellowAnchor > 0L &&
            candidate.magentaAnchor > 0L &&
            candidate.unknownPalette == 0L;
    }

    private int FindFreeSlot()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            if (!_slots[i].inFlight) return i;
        }
        return -1;
    }

    private static void EnsureFramebuffer(Slot slot, int width, int height)
    {
        if (slot.framebuffer != null &&
            slot.framebuffer.width == width &&
            slot.framebuffer.height == height)
        {
            if (!slot.framebuffer.IsCreated()) slot.framebuffer.Create();
            return;
        }

        if (slot.framebuffer != null)
        {
            slot.framebuffer.Release();
            Destroy(slot.framebuffer);
        }

        slot.framebuffer = new RenderTexture(
            width,
            height,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Default)
        {
            name = "Kiwi H1 Final Framebuffer Capture",
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave
        };
        slot.framebuffer.Create();
    }

    private void WriteGap(
        Identity identity,
        Disposition disposition,
        string detail = "")
    {
        _coverageGapCount++;
        WriteDisposition(identity, disposition, detail);
        WriteExactGap(identity, disposition, detail);
    }

    private void WriteDisposition(
        Identity identity,
        Disposition disposition,
        string detail = "")
    {
        if (_dispositionWriter == null) return;
        _dispositionWriter.WriteLine(
            IdentityValues(identity) + "," +
            Csv(disposition +
                (string.IsNullOrEmpty(detail) ? string.Empty : "_" + detail)));
    }

    private void WriteRequest(
        Slot slot,
        long captureHostTicks,
        int captureUnityFrame)
    {
        if (_requestWriter == null) return;
        _requestWriter.WriteLine(
            slot.requestSequence + "," +
            captureHostTicks + "," +
            captureUnityFrame + "," +
            B(slot.renderTextureSrgb) + "," +
            Csv(slot.renderTextureGraphicsFormat) + "," +
            IdentityValues(slot.identity));
    }

    private void WriteExact(
        Slot slot,
        long completionHostTicks,
        int completionUnityFrame,
        int byteCount,
        string framebufferSha256,
        long nonzeroByteCount,
        long nonzeroRgbPixelCount,
        long nonopaqueAlphaPixelCount,
        int firstNonzeroX,
        int firstNonzeroYBottom,
        byte firstNonzeroR,
        byte firstNonzeroG,
        byte firstNonzeroB,
        byte firstNonzeroA,
        Candidate bottom,
        Candidate top,
        int orientationCandidateCount,
        string resolvedOrientation,
        Disposition disposition)
    {
        if (_exactWriter == null) return;
        Candidate canonical = bottom.occupied > 0L ? bottom : top;
        _exactWriter.WriteLine(
            slot.requestSequence + "," +
            completionHostTicks + "," +
            completionUnityFrame + "," +
            byteCount + "," +
            B(slot.renderTextureSrgb) + "," +
            Csv(slot.renderTextureGraphicsFormat) + "," +
            Csv(framebufferSha256) + "," +
            nonzeroByteCount + "," +
            nonzeroRgbPixelCount + "," +
            nonopaqueAlphaPixelCount + "," +
            firstNonzeroX + "," +
            firstNonzeroYBottom + "," +
            firstNonzeroR + "," +
            firstNonzeroG + "," +
            firstNonzeroB + "," +
            firstNonzeroA + "," +
            canonical.occupied + "," +
            canonical.landmark + "," +
            canonical.redAnchor + "," +
            canonical.greenAnchor + "," +
            canonical.yellowAnchor + "," +
            canonical.magentaAnchor + "," +
            canonical.unknownPalette + "," +
            bottom.mismatch + "," +
            top.mismatch + "," +
            orientationCandidateCount + "," +
            Csv(resolvedOrientation) + "," +
            Csv(disposition.ToString()) + "," +
            IdentityValues(slot.identity));
    }

    private void WriteExactGap(
        Identity identity,
        Disposition disposition,
        string detail)
    {
        if (_exactWriter == null) return;
        _exactWriter.WriteLine(
            "0," + identity.observationHostTicks + "," +
            Time.frameCount + ",0,0," + Csv(string.Empty) + "," +
            Csv(string.Empty) + ",0,0,0,-1,-1,0,0,0,0," +
            "0,0,0,0,0,0,0,0,0,0," + Csv(string.Empty) + "," +
            Csv(disposition +
                (string.IsNullOrEmpty(detail) ? string.Empty : "_" + detail)) +
            "," + IdentityValues(identity));
    }

    private void WriteFirstFailure(
        Slot slot,
        Disposition disposition,
        string orientation,
        PixelFailure failure)
    {
        if (_firstFailureWritten || _firstFailureWriter == null || !failure.valid)
            return;
        _firstFailureWritten = true;
        _firstFailureWriter.WriteLine(
            slot.requestSequence + "," +
            Csv(disposition.ToString()) + "," +
            Csv(orientation) + "," +
            failure.screenX + "," +
            failure.screenYBottom + "," +
            failure.guiYTop + "," +
            failure.sourceX + "," +
            failure.sourceYBottom + "," +
            failure.sourceR + "," +
            failure.sourceG + "," +
            failure.sourceB + "," +
            failure.sourceA + "," +
            failure.actualR + "," +
            failure.actualG + "," +
            failure.actualB + "," +
            failure.actualA + "," +
            failure.expectedRMin + "," +
            failure.expectedRMax + "," +
            failure.expectedGMin + "," +
            failure.expectedGMax + "," +
            failure.expectedBMin + "," +
            failure.expectedBMax + "," +
            IdentityValues(slot.identity));
        SaveFirstFailurePng(slot);
    }

    private void SaveFirstFailurePng(Slot slot)
    {
        if (slot.framebufferBytes == null ||
            slot.framebufferBytes.Length !=
                slot.identity.screenWidth * slot.identity.screenHeight * BytesPerPixel)
            return;

        Texture2D texture = null;
        try
        {
            texture = new Texture2D(
                slot.identity.screenWidth,
                slot.identity.screenHeight,
                TextureFormat.RGBA32,
                false,
                true);
            texture.LoadRawTextureData(slot.framebufferBytes);
            texture.Apply(false, false);
            byte[] png = texture.EncodeToPNG();
            File.WriteAllBytes(
                Path.Combine(
                    _outputDirectory,
                    "final_framebuffer_first_failure.png"),
                png);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[KiwiH1FinalFramebuffer] first failure PNG exception=" +
                ex.GetType().Name);
        }
        finally
        {
            if (texture != null) Destroy(texture);
        }
    }

    private static string IdentityHeader()
    {
        return "drawEventSequence,callAttemptSequence,observationHostTicks," +
            "segment,eventType,callUnityFrame,h1VisualId,visualSequence," +
            "visualUnityFrame,semanticTimestamp,previewSemanticTimestamp," +
            "canonicalFrameId,publicationSequence,sourceFrameId," +
            "sourceGeneration,sourceHostTicks,rasterSequence," +
            "rasterSemanticTimestamp,rasterCanonicalFrameId," +
            "drawRectX,drawRectY,drawRectWidth,drawRectHeight," +
            "screenWidth,screenHeight,sourceTextureInstanceId," +
            "sourceFormat,sourceGraphicsFormat,sourceFilterMode," +
            "sourceSha256,activeColorSpace,graphicsDeviceType";
    }

    private static string IdentityValues(Identity identity)
    {
        return identity.drawEventSequence + "," +
            identity.callAttemptSequence + "," +
            identity.observationHostTicks + "," +
            Csv(identity.segment) + "," +
            Csv(identity.eventType.ToString()) + "," +
            identity.callUnityFrame + "," +
            Csv(VisualId(identity.visualSequence)) + "," +
            identity.visualSequence + "," +
            identity.visualUnityFrame + "," +
            identity.semanticTimestamp + "," +
            identity.previewSemanticTimestamp + "," +
            identity.canonicalFrameId + "," +
            identity.publicationSequence + "," +
            identity.sourceFrameId + "," +
            identity.sourceGeneration + "," +
            identity.sourceHostTicks + "," +
            identity.rasterSequence + "," +
            identity.rasterSemanticTimestamp + "," +
            identity.rasterCanonicalFrameId + "," +
            F(identity.drawRect.x) + "," +
            F(identity.drawRect.y) + "," +
            F(identity.drawRect.width) + "," +
            F(identity.drawRect.height) + "," +
            identity.screenWidth + "," +
            identity.screenHeight + "," +
            identity.sourceTextureInstanceId + "," +
            Csv(identity.sourceFormat) + "," +
            Csv(identity.sourceGraphicsFormat) + "," +
            Csv(identity.sourceFilterMode) + "," +
            Csv(identity.sourceSha256) + "," +
            Csv(identity.activeColorSpace) + "," +
            Csv(identity.graphicsDeviceType);
    }

    private void OnApplicationQuit()
    {
        _armed = false;
        _accepting = false;
        _quitting = true;
        WriteIncompleteAtShutdown();
        WriteSummary(true);
        CloseWriters();
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_activeInstance, this)) _activeInstance = null;
        if (!_quitting)
        {
            _armed = false;
            _accepting = false;
            WriteIncompleteAtShutdown();
            WriteSummary(false);
            CloseWriters();
        }
        ReleaseCompletedFramebuffers();
    }

    private void WriteIncompleteAtShutdown()
    {
        if (_pending.valid)
        {
            WriteGap(
                _pending.identity,
                Disposition.QUIT_INCOMPLETE_PENDING);
            _pending.valid = false;
        }

        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            if (!slot.inFlight || slot.outcomeWritten) continue;
            WriteGap(
                slot.identity,
                Disposition.QUIT_INCOMPLETE_READBACK);
            slot.outcomeWritten = true;
        }
    }

    private void ReleaseCompletedFramebuffers()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            if (slot.framebuffer == null || slot.inFlight) continue;
            slot.framebuffer.Release();
            Destroy(slot.framebuffer);
            slot.framebuffer = null;
        }
    }

    private void WriteSummary(bool playerClosed)
    {
        if (string.IsNullOrEmpty(_summaryPath)) return;
        string[] lines =
        {
            "KIWI_H1_GUI_DRAW_TEXTURE_TO_FINAL_FRAMEBUFFER_DIAGNOSTIC",
            "UTC=" + DateTime.UtcNow.ToString("O", Invariant),
            "UNITY_VERSION=" + Application.unityVersion,
            "DEBUG_BUILD=" + B(Debug.isDebugBuild),
            "GRAPHICS_API=" + SystemInfo.graphicsDeviceType,
            "ACTIVE_COLOR_SPACE=" + QualitySettings.activeColorSpace,
            "DIAGNOSTIC_ONLY=1",
            "PRODUCTION_BEHAVIOR_CHANGED=0",
            "PLAYER_CLOSED=" + (playerClosed ? "YES" : "NO"),
            "CAPTURE_SEQUENCE=WaitForEndOfFrame|CaptureScreenshotIntoRenderTexture|AsyncGPUReadback",
            "COROUTINE_COUNT=1",
            "SLOT_COUNT=" + SlotCount,
            "BLOCKING_WAIT_COUNT=0",
            "WAIT_ALL_REQUESTS_COUNT=0",
            "FIFO_QUEUE_COUNT=0",
            "COLOR_TOLERANCE_LSB=" + ColorToleranceLsb,
            "GUI_TO_FRAMEBUFFER=guiYTop_to_screenYBottom_equals_screenHeight_minus_1_minus_guiYTop",
            "SOURCE_POINT_SAMPLE=sourceX_floor_u_times_384|sourceYBottom_floor_one_minus_vTop_times_384",
            "READBACK_ORIENTATION_RULE=exactly_one_of_rowZeroBottom_or_rowZeroTop_must_pass",
            "DRAW_OBSERVED_COUNT=" + _drawObservedCount,
            "QUEUED_COUNT=" + _queuedCount,
            "REQUESTED_COUNT=" + _requestedCount,
            "COMPLETED_COUNT=" + _completedCount,
            "PASS_COUNT=" + _passCount,
            "FAIL_COUNT=" + _failCount,
            "COVERAGE_GAP_COUNT=" + _coverageGapCount,
            "POOL_EXHAUSTED_COUNT=" + _poolExhaustedCount,
            "READBACK_ERROR_COUNT=" + _readbackErrorCount,
            "PENDING_SUPERSEDED_COUNT=" + _pendingSupersededCount,
            "AFTER_MEASUREMENT_CLOSE_IGNORED_COUNT=" + _afterCloseIgnoredCount,
            "IN_FLIGHT_AT_SUMMARY=" + _inFlight,
            "MAX_IN_FLIGHT=" + _maxInFlight,
            "PENDING_AT_SUMMARY=" + B(_pending.valid),
            "SHUTDOWN_REQUESTED=" + B(_shutdownRequested),
            "FIRST_FAILURE_WRITTEN=" + B(_firstFailureWritten)
        };
        File.WriteAllLines(
            _summaryPath,
            lines,
            new UTF8Encoding(false));
    }

    private StreamWriter CreateWriter(string name, string header)
    {
        var writer = new StreamWriter(
            Path.Combine(_outputDirectory, name),
            false,
            new UTF8Encoding(false));
        writer.AutoFlush = true;
        writer.WriteLine(header);
        return writer;
    }

    private void CloseWriters()
    {
        Close(ref _dispositionWriter);
        Close(ref _requestWriter);
        Close(ref _exactWriter);
        Close(ref _firstFailureWriter);
    }

    private static void Close(ref StreamWriter writer)
    {
        if (writer == null) return;
        writer.Flush();
        writer.Dispose();
        writer = null;
    }

    private static bool RectAndScreenValid(Rect rect, int width, int height)
    {
        return width > 0 && height > 0 &&
            IsFinite(rect.x) && IsFinite(rect.y) &&
            IsFinite(rect.width) && IsFinite(rect.height) &&
            rect.width > 0f && rect.height > 0f &&
            rect.xMax > 0f && rect.yMax > 0f &&
            rect.xMin < width && rect.yMin < height;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool ApproximatelyWhite(Color color)
    {
        return Mathf.Abs(color.r - 1f) <= 0.0001f &&
            Mathf.Abs(color.g - 1f) <= 0.0001f &&
            Mathf.Abs(color.b - 1f) <= 0.0001f &&
            Mathf.Abs(color.a - 1f) <= 0.0001f;
    }

    private static void EncodeColor32Bytes(
        Color32[] pixels,
        byte[] destination)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            int offset = i * BytesPerPixel;
            Color32 pixel = pixels[i];
            destination[offset] = pixel.r;
            destination[offset + 1] = pixel.g;
            destination[offset + 2] = pixel.b;
            destination[offset + 3] = pixel.a;
        }
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] hash = sha256.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
                builder.Append(hash[i].ToString("x2", Invariant));
            return builder.ToString();
        }
    }

    private string ResolveOutputDirectory()
    {
        string configured =
            Environment.GetEnvironmentVariable(OutputDirectoryEnvironment);
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured.Trim())
            : Path.Combine(
                Application.persistentDataPath,
                "KiwiH1LandmarkerBoundary");
    }

    private static string VisualId(long sequence)
    {
        return sequence > 0L
            ? "H1V-" + sequence.ToString("D6", Invariant)
            : string.Empty;
    }

    private static string Csv(string value)
    {
        string safe = value ?? string.Empty;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    private static string B(bool value)
    {
        return value ? "1" : "0";
    }

    private static string F(float value)
    {
        return value.ToString("R", Invariant);
    }
}
#endif
