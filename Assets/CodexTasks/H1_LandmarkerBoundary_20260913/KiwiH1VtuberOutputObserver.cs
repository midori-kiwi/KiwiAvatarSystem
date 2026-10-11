#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Klak.Spout;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Development-only companion for the exact Repaint -> VTuberOutput surface
/// boundary. The existing final-framebuffer observer remains the Game View
/// series. This observer snapshots the actual Camera/Spout RenderTexture at
/// the same frame end, then performs bounded asynchronous readback.
/// </summary>
[DefaultExecutionOrder(40002)]
public sealed class KiwiH1VtuberOutputObserver : MonoBehaviour
{
    private const string EnableEnvironment = "KIWI_H1_BOUNDARY_RUNTIME";
    private const string OutputDirectoryEnvironment = "KIWI_H1_BOUNDARY_OUTPUT";
    private const string VtuberCameraName = "VTuberCamera";
    private const string SpoutObjectName = "SpoutOutput";
    private const string VtuberTextureName = "VTuberOutput";
    private const int SourceWidth = 384;
    private const int SourceHeight = 384;
    private const int BytesPerPixel = 4;
    private const int SourceByteCount = SourceWidth * SourceHeight * BytesPerPixel;
    private const int SlotCount = 4;
    private const int ColorToleranceLsb = 3;
    private const int PixelNeighborhood = 1;
    private const double DrainTimeoutSeconds = 5.0;
    private const float FiducialSize = 11f;
    private const float FiducialMargin = 17f;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();
    private static readonly Color32 FiducialA = new Color32(13, 71, 191, 255);
    private static readonly Color32 FiducialB = new Color32(211, 37, 83, 255);
    private static readonly Color32 FiducialC = new Color32(47, 223, 109, 255);
    private static readonly Color32 FiducialD = new Color32(239, 157, 29, 255);
    private static bool _installed;
    private static bool _armed;
    private static KiwiH1VtuberOutputObserver _activeInstance;

    private enum Disposition
    {
        DRAW_OBSERVED,
        FIDUCIALS_DRAWN,
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
        COVERAGE_GAP_GUI_STATE_INVALID,
        COVERAGE_GAP_VTUBER_TARGET_NOT_EXACT,
        COVERAGE_GAP_LAST_CAMERA_AMBIGUOUS,
        COVERAGE_GAP_SPOUT_SOURCE_MISMATCH,
        COVERAGE_GAP_PENDING_SUPERSEDED,
        COVERAGE_GAP_PENDING_NOT_SAME_FRAME,
        COVERAGE_GAP_TARGET_CHANGED_BEFORE_CAPTURE,
        COVERAGE_GAP_ASYNC_READBACK_UNSUPPORTED,
        COVERAGE_GAP_POOL_EXHAUSTED,
        COVERAGE_GAP_COPY_OR_REQUEST_EXCEPTION,
        REQUEST_SUBMITTED,
        COMPLETE_GEOMETRY_PRESENT,
        COVERAGE_GAP_READBACK_ERROR,
        COVERAGE_GAP_UNEXPECTED_READBACK_SIZE,
        COVERAGE_GAP_EXPECTED_MASK_EMPTY,
        COVERAGE_GAP_PALETTE_INCOMPLETE_OR_UNKNOWN,
        COVERAGE_GAP_FIDUCIAL_CALIBRATION_INVALID,
        COVERAGE_GAP_NEGATIVE_CONTROL_FALSE_POSITIVE,
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
        public Matrix4x4 guiMatrix;
        public Color guiColor;
        public int guiDepth;
        public bool guiEnabled;
        public bool drawCallReturned;
        public int cameraInstanceId;
        public string cameraName;
        public float cameraDepth;
        public int cameraTargetDisplay;
        public bool cameraEnabled;
        public Rect cameraRect;
        public Rect cameraPixelRect;
        public int enabledCameraCount;
        public int lastDepthCameraCount;
        public int targetInstanceId;
        public string targetName;
        public int targetWidth;
        public int targetHeight;
        public string targetGraphicsFormat;
        public string targetFormat;
        public int targetAntiAliasing;
        public string targetDimension;
        public bool targetSrgb;
        public bool targetIsCreated;
        public int spoutInstanceId;
        public string spoutCaptureMethod;
        public int spoutSourceInstanceId;
    }

    private sealed class Pending
    {
        public bool valid;
        public Identity identity;
        public RenderTexture target;
        public readonly byte[] sourceBytes = new byte[SourceByteCount];
    }

    private sealed class Slot
    {
        public RenderTexture snapshot;
        public RenderTexture target;
        public bool inFlight;
        public bool outcomeWritten;
        public long requestSequence;
        public Identity identity;
        public string targetNativePtr = string.Empty;
        public string snapshotNativePtr = string.Empty;
        public readonly byte[] sourceBytes = new byte[SourceByteCount];
        public byte[] surfaceBytes;
    }

    private struct FiducialObservation
    {
        public bool valid;
        public long count;
        public int minX;
        public int maxX;
        public int minRawY;
        public int maxRawY;
        public double centerX;
        public double centerRawY;
    }

    private struct AffineMap
    {
        public bool valid;
        public double originLogicalX;
        public double originLogicalY;
        public double originRawX;
        public double originRawY;
        public double xAxisRawX;
        public double xAxisRawY;
        public double yAxisRawX;
        public double yAxisRawY;
        public double determinant;
        public double fourthResidualPixels;
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
        public long matched;
        public long mismatch;
        public long negativeMatched;
    }

    private struct SurfaceResult
    {
        public Disposition disposition;
        public string sha256;
        public int byteCount;
        public long nonzeroRgbPixels;
        public FiducialObservation a;
        public FiducialObservation b;
        public FiducialObservation c;
        public FiducialObservation d;
        public AffineMap map;
        public Candidate candidate;
    }

    private StreamWriter _dispositionWriter;
    private StreamWriter _requestWriter;
    private StreamWriter _exactWriter;
    private string _outputDirectory;
    private string _summaryPath;
    private readonly Pending _pending = new Pending();
    private readonly Slot[] _slots = new Slot[SlotCount];
    private bool _accepting = true;
    private bool _quitting;
    private bool _shutdownRequested;
    private double _shutdownRequestedRealtime;
    private long _drawEventSequence;
    private long _requestSequence;
    private long _drawObservedCount;
    private long _fiducialDrawCount;
    private long _queuedCount;
    private long _requestedCount;
    private long _completedCount;
    private long _passCount;
    private long _failCount;
    private long _coverageGapCount;
    private long _poolExhaustedCount;
    private long _readbackErrorCount;
    private long _pendingSupersededCount;
    private int _inFlight;
    private int _maxInFlight;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed || !IsEnabled()) return;
        _installed = true;
        _armed = true;
        var host = new GameObject("[Kiwi] H1 VTuberOutput Observer");
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiH1VtuberOutputObserver>();
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
        Color guiColor,
        Matrix4x4 guiMatrix,
        int guiDepth,
        bool guiEnabled)
    {
        KiwiH1VtuberOutputObserver instance = _activeInstance;
        if (instance == null)
        {
            Debug.LogError(
                "[KiwiH1VtuberOutput] " +
                Disposition.COVERAGE_GAP_ACTIVE_INSTANCE_NULL +
                " callAttemptSequence=" + callAttemptSequence);
            return;
        }

        instance.DrawFiducials(screenWidth, screenHeight);
        instance.QueueDraw(
            callAttemptSequence, eventType, callUnityFrame,
            observationHostTicks, segment, visualValid, visualSequence,
            visualUnityFrame, visualSemanticTimestamp, canonicalFrameId,
            acceptedPublicationExact, publicationSequence, sourceFrameId,
            sourceGeneration, sourceHostTicks, semanticTimestamp,
            previewSemanticTimestamp, rasterValid, rasterSequence,
            rasterSemanticTimestamp, rasterCanonicalFrameId, drawRect,
            screenWidth, screenHeight, overlayTexture, cpuPixels, guiColor,
            guiMatrix, guiDepth, guiEnabled);
    }

    internal static bool BeginShutdownAndIsDrained()
    {
        KiwiH1VtuberOutputObserver instance = _activeInstance;
        if (instance == null) return true;
        if (!instance._shutdownRequested)
        {
            instance._shutdownRequested = true;
            instance._accepting = false;
            instance._shutdownRequestedRealtime = Time.realtimeSinceStartupAsDouble;
        }
        bool drained = !instance._pending.valid && instance._inFlight == 0;
        bool timedOut = Time.realtimeSinceStartupAsDouble -
            instance._shutdownRequestedRealtime >= DrainTimeoutSeconds;
        return drained || timedOut;
    }

    private void Awake()
    {
        _activeInstance = this;
        _outputDirectory = ResolveOutputDirectory();
        Directory.CreateDirectory(_outputDirectory);
        _summaryPath = Path.Combine(_outputDirectory, "vtuber_output_summary.txt");
        _dispositionWriter = CreateWriter(
            "vtuber_output_entry_disposition.csv",
            IdentityHeader() + ",disposition");
        _requestWriter = CreateWriter(
            "vtuber_output_request.csv",
            "requestSequence,submitHostTicks,submitUnityFrame,targetNativePtr," +
            "snapshotInstanceId,snapshotNativePtr,snapshotWidth,snapshotHeight," +
            "snapshotGraphicsFormat,snapshotSrgb," + IdentityHeader());
        _exactWriter = CreateWriter(
            "vtuber_output_exact.csv",
            "requestSequence,completionHostTicks,callbackUnityFrame,byteCount," +
            "surfaceSha256,nonzeroRgbPixels," +
            "fiducialAValid,fiducialACount,fiducialACenterX,fiducialACenterRawY," +
            "fiducialBValid,fiducialBCount,fiducialBCenterX,fiducialBCenterRawY," +
            "fiducialCValid,fiducialCCount,fiducialCCenterX,fiducialCCenterRawY," +
            "fiducialDValid,fiducialDCount,fiducialDCenterX,fiducialDCenterRawY," +
            "affineValid,affineOriginRawX,affineOriginRawY,affineXAxisRawX," +
            "affineXAxisRawY,affineYAxisRawX,affineYAxisRawY,affineDeterminant," +
            "affineFourthResidualPixels,expectedOccupiedPixels,landmarkPixels," +
            "redAnchorPixels,greenAnchorPixels,yellowAnchorPixels,magentaAnchorPixels," +
            "unknownPalettePixels,matchedPixels,mismatchPixels,negativeMatchedPixels," +
            "coverageStatus," + IdentityHeader());
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new Slot();
        StartCoroutine(CaptureLoop());
        WriteSummary(false);
    }

    private void DrawFiducials(int width, int height)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint ||
            width < 160 || height < 120) return;
        Color oldColor = GUI.color;
        DrawFiducial(FiducialRectA(width, height), FiducialA);
        DrawFiducial(FiducialRectB(width, height), FiducialB);
        DrawFiducial(FiducialRectC(width, height), FiducialC);
        DrawFiducial(FiducialRectD(width, height), FiducialD);
        GUI.color = oldColor;
        _fiducialDrawCount++;
    }

    private static void DrawFiducial(Rect rect, Color32 color)
    {
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
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
        long callAttemptSequence, EventType eventType, int callUnityFrame,
        long observationHostTicks, string segment, bool visualValid,
        long visualSequence, int visualUnityFrame, long visualSemanticTimestamp,
        ulong canonicalFrameId, bool acceptedPublicationExact,
        ulong publicationSequence, ulong sourceFrameId, int sourceGeneration,
        long sourceHostTicks, long semanticTimestamp,
        long previewSemanticTimestamp, bool rasterValid, long rasterSequence,
        long rasterSemanticTimestamp, ulong rasterCanonicalFrameId,
        Rect drawRect, int screenWidth, int screenHeight,
        Texture2D overlayTexture, Color32[] cpuPixels, Color guiColor,
        Matrix4x4 guiMatrix, int guiDepth, bool guiEnabled)
    {
        Camera camera;
        RenderTexture target;
        SpoutSender spout;
        int enabledCameraCount;
        int lastDepthCameraCount;
        ResolveTarget(out camera, out target, out spout,
            out enabledCameraCount, out lastDepthCameraCount);

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
            sourceTextureInstanceId = overlayTexture != null ? overlayTexture.GetInstanceID() : 0,
            sourceFormat = overlayTexture != null ? overlayTexture.format.ToString() : string.Empty,
            sourceGraphicsFormat = overlayTexture != null ? overlayTexture.graphicsFormat.ToString() : string.Empty,
            sourceFilterMode = overlayTexture != null ? overlayTexture.filterMode.ToString() : string.Empty,
            activeColorSpace = QualitySettings.activeColorSpace.ToString(),
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            guiMatrix = guiMatrix,
            guiColor = guiColor,
            guiDepth = guiDepth,
            guiEnabled = guiEnabled,
            drawCallReturned = true,
            cameraInstanceId = camera != null ? camera.GetInstanceID() : 0,
            cameraName = camera != null ? camera.name : string.Empty,
            cameraDepth = camera != null ? camera.depth : 0f,
            cameraTargetDisplay = camera != null ? camera.targetDisplay : -1,
            cameraEnabled = camera != null && camera.enabled && camera.gameObject.activeInHierarchy,
            cameraRect = camera != null ? camera.rect : default,
            cameraPixelRect = camera != null ? camera.pixelRect : default,
            enabledCameraCount = enabledCameraCount,
            lastDepthCameraCount = lastDepthCameraCount,
            targetInstanceId = target != null ? target.GetInstanceID() : 0,
            targetName = target != null ? target.name : string.Empty,
            targetWidth = target != null ? target.width : 0,
            targetHeight = target != null ? target.height : 0,
            targetGraphicsFormat = target != null ? target.graphicsFormat.ToString() : string.Empty,
            targetFormat = target != null ? target.format.ToString() : string.Empty,
            targetAntiAliasing = target != null ? target.antiAliasing : 0,
            targetDimension = target != null ? target.dimension.ToString() : string.Empty,
            targetSrgb = target != null && target.sRGB,
            targetIsCreated = target != null && target.IsCreated(),
            spoutInstanceId = spout != null ? spout.GetInstanceID() : 0,
            spoutCaptureMethod = spout != null ? spout.captureMethod.ToString() : string.Empty,
            spoutSourceInstanceId = spout != null && spout.sourceTexture != null
                ? spout.sourceTexture.GetInstanceID() : 0
        };

        _drawObservedCount++;
        WriteDisposition(identity, Disposition.DRAW_OBSERVED);
        if (!_armed) { WriteGap(identity, Disposition.COVERAGE_GAP_NOT_ARMED); return; }
        if (eventType != EventType.Repaint) { WriteGap(identity, Disposition.COVERAGE_GAP_NOT_REPAINT); return; }
        if (!_accepting) { WriteGap(identity, Disposition.COVERAGE_GAP_MEASUREMENT_CLOSED); return; }
        if (!visualValid || visualSequence <= 0L || callAttemptSequence <= 0L ||
            canonicalFrameId == 0UL || !acceptedPublicationExact ||
            publicationSequence == 0UL || sourceFrameId == 0UL || sourceHostTicks <= 0L)
        { WriteGap(identity, Disposition.COVERAGE_GAP_IDENTITY_NOT_EXACT); return; }
        if (callUnityFrame != Time.frameCount || visualUnityFrame > callUnityFrame)
        { WriteGap(identity, Disposition.COVERAGE_GAP_CALL_FRAME_IDENTITY_MISMATCH); return; }
        if (semanticTimestamp != visualSemanticTimestamp || previewSemanticTimestamp != semanticTimestamp)
        { WriteGap(identity, Disposition.COVERAGE_GAP_SEMANTIC_IDENTITY_MISMATCH); return; }
        if (!rasterValid || rasterSequence <= 0L || rasterSemanticTimestamp != semanticTimestamp ||
            rasterCanonicalFrameId != canonicalFrameId)
        { WriteGap(identity, Disposition.COVERAGE_GAP_RASTER_IDENTITY_MISMATCH); return; }
        if (overlayTexture == null || overlayTexture.width != SourceWidth ||
            overlayTexture.height != SourceHeight || overlayTexture.format != TextureFormat.RGBA32 ||
            overlayTexture.filterMode != FilterMode.Point || cpuPixels == null ||
            cpuPixels.Length != SourceWidth * SourceHeight)
        { WriteGap(identity, Disposition.COVERAGE_GAP_SOURCE_RASTER_INVALID); return; }
        if (!RectAndScreenValid(drawRect, screenWidth, screenHeight))
        { WriteGap(identity, Disposition.COVERAGE_GAP_RECT_OR_SCREEN_INVALID); return; }
        if (!guiEnabled || !ApproximatelyWhite(guiColor) || !MatrixFinite(guiMatrix))
        { WriteGap(identity, Disposition.COVERAGE_GAP_GUI_STATE_INVALID); return; }
        if (camera == null || target == null || !identity.cameraEnabled ||
            !string.Equals(camera.name, VtuberCameraName, StringComparison.Ordinal) ||
            !string.Equals(target.name, VtuberTextureName, StringComparison.Ordinal) ||
            !target.IsCreated() || target.width <= 0 || target.height <= 0)
        { WriteGap(identity, Disposition.COVERAGE_GAP_VTUBER_TARGET_NOT_EXACT); return; }
        if (lastDepthCameraCount != 1)
        { WriteGap(identity, Disposition.COVERAGE_GAP_LAST_CAMERA_AMBIGUOUS); return; }
        if (spout == null || spout.captureMethod != CaptureMethod.Texture ||
            !ReferenceEquals(spout.sourceTexture, target))
        { WriteGap(identity, Disposition.COVERAGE_GAP_SPOUT_SOURCE_MISMATCH); return; }

        if (_pending.valid)
        {
            _pendingSupersededCount++;
            WriteGap(_pending.identity, Disposition.COVERAGE_GAP_PENDING_SUPERSEDED);
        }
        EncodeColor32Bytes(cpuPixels, _pending.sourceBytes);
        identity.sourceSha256 = Sha256Hex(_pending.sourceBytes);
        _pending.identity = identity;
        _pending.target = target;
        _pending.valid = true;
        _queuedCount++;
        WriteDisposition(identity, Disposition.FIDUCIALS_DRAWN);
        WriteDisposition(identity, Disposition.QUEUED_PENDING_END_OF_FRAME);
    }

    private void CapturePendingAtEndOfFrame()
    {
        if (!_pending.valid) return;
        Identity identity = _pending.identity;
        if (identity.callUnityFrame != Time.frameCount)
        { _pending.valid = false; WriteGap(identity, Disposition.COVERAGE_GAP_PENDING_NOT_SAME_FRAME); return; }
        if (!SystemInfo.supportsAsyncGPUReadback)
        { _pending.valid = false; WriteGap(identity, Disposition.COVERAGE_GAP_ASYNC_READBACK_UNSUPPORTED); return; }

        Camera camera;
        RenderTexture target;
        SpoutSender spout;
        int enabledCameraCount;
        int lastDepthCameraCount;
        ResolveTarget(out camera, out target, out spout,
            out enabledCameraCount, out lastDepthCameraCount);
        if (!ReferenceEquals(target, _pending.target) || target == null ||
            target.GetInstanceID() != identity.targetInstanceId ||
            camera == null || camera.GetInstanceID() != identity.cameraInstanceId ||
            spout == null || spout.GetInstanceID() != identity.spoutInstanceId ||
            !ReferenceEquals(spout.sourceTexture, target) || !target.IsCreated())
        { _pending.valid = false; WriteGap(identity, Disposition.COVERAGE_GAP_TARGET_CHANGED_BEFORE_CAPTURE); return; }

        int slotIndex = FindFreeSlot();
        if (slotIndex < 0)
        { _pending.valid = false; _poolExhaustedCount++; WriteGap(identity, Disposition.COVERAGE_GAP_POOL_EXHAUSTED); return; }

        Slot slot = _slots[slotIndex];
        EnsureSnapshot(slot, target);
        Buffer.BlockCopy(_pending.sourceBytes, 0, slot.sourceBytes, 0, SourceByteCount);
        _pending.valid = false;
        slot.identity = identity;
        slot.target = target;
        slot.requestSequence = ++_requestSequence;
        slot.inFlight = true;
        slot.outcomeWritten = false;
        _inFlight++;
        _maxInFlight = Math.Max(_maxInFlight, _inFlight);
        _requestedCount++;

        try
        {
            Graphics.CopyTexture(target, slot.snapshot);
            slot.targetNativePtr = NativePtr(target);
            slot.snapshotNativePtr = NativePtr(slot.snapshot);
            long submitHostTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            int submitUnityFrame = Time.frameCount;
            WriteRequest(slot, submitHostTicks, submitUnityFrame);
            WriteDisposition(identity, Disposition.REQUEST_SUBMITTED);
            AsyncGPUReadback.Request(
                slot.snapshot, 0, TextureFormat.RGBA32,
                request => CompleteReadback(slotIndex, request));
        }
        catch (Exception ex)
        {
            slot.inFlight = false;
            _inFlight = Math.Max(0, _inFlight - 1);
            WriteGap(identity, Disposition.COVERAGE_GAP_COPY_OR_REQUEST_EXCEPTION, ex.GetType().Name);
        }
    }

    private void CompleteReadback(int slotIndex, AsyncGPUReadbackRequest request)
    {
        if (slotIndex < 0 || slotIndex >= _slots.Length) return;
        Slot slot = _slots[slotIndex];
        if (!slot.inFlight || slot.outcomeWritten) return;
        long completionHostTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        int callbackUnityFrame = Time.frameCount;
        SurfaceResult result = default;
        try
        {
            if (request.hasError)
            {
                _readbackErrorCount++;
                result.disposition = Disposition.COVERAGE_GAP_READBACK_ERROR;
            }
            else
            {
                var data = request.GetData<byte>();
                result.byteCount = data.Length;
                int expected = slot.identity.targetWidth * slot.identity.targetHeight * BytesPerPixel;
                if (data.Length != expected)
                {
                    result.disposition = Disposition.COVERAGE_GAP_UNEXPECTED_READBACK_SIZE;
                }
                else
                {
                    if (slot.surfaceBytes == null || slot.surfaceBytes.Length != expected)
                        slot.surfaceBytes = new byte[expected];
                    data.CopyTo(slot.surfaceBytes);
                    result = EvaluateSurface(slot.identity, slot.sourceBytes, slot.surfaceBytes);
                }
            }
        }
        catch (Exception ex)
        {
            _readbackErrorCount++;
            result.disposition = Disposition.COVERAGE_GAP_READBACK_ERROR;
            Debug.LogError("[KiwiH1VtuberOutput] callback exception=" + ex.GetType().Name);
        }

        WriteExact(slot, completionHostTicks, callbackUnityFrame, result);
        if (result.disposition == Disposition.COMPLETE_GEOMETRY_PRESENT) _passCount++;
        else if (result.disposition == Disposition.FAIL_EXPECTED_PIXEL_MISSING)
        { _failCount++; SaveFirstFailurePng(slot); }
        else _coverageGapCount++;
        slot.outcomeWritten = true;
        slot.inFlight = false;
        slot.target = null;
        _completedCount++;
        _inFlight = Math.Max(0, _inFlight - 1);
    }

    private static SurfaceResult EvaluateSurface(Identity identity, byte[] source, byte[] surface)
    {
        SurfaceResult result = new SurfaceResult
        {
            byteCount = surface.Length,
            sha256 = Sha256Hex(surface),
            disposition = Disposition.COVERAGE_GAP_FIDUCIAL_CALIBRATION_INVALID
        };
        for (int i = 0; i < surface.Length; i += BytesPerPixel)
            if (surface[i] != 0 || surface[i + 1] != 0 || surface[i + 2] != 0)
                result.nonzeroRgbPixels++;
        result.a = FindFiducial(surface, identity.targetWidth, identity.targetHeight, FiducialA);
        result.b = FindFiducial(surface, identity.targetWidth, identity.targetHeight, FiducialB);
        result.c = FindFiducial(surface, identity.targetWidth, identity.targetHeight, FiducialC);
        result.d = FindFiducial(surface, identity.targetWidth, identity.targetHeight, FiducialD);
        result.map = BuildAffine(identity, result.a, result.b, result.c, result.d);
        if (!result.map.valid) return result;
        result.candidate = EvaluateMappedMask(identity, source, surface, result.map, 0f);
        Candidate negative = EvaluateMappedMask(identity, source, surface, result.map, 53f);
        result.candidate.negativeMatched = negative.matched;
        if (result.candidate.occupied <= 0L)
            result.disposition = Disposition.COVERAGE_GAP_EXPECTED_MASK_EMPTY;
        else if (!PaletteComplete(result.candidate))
            result.disposition = Disposition.COVERAGE_GAP_PALETTE_INCOMPLETE_OR_UNKNOWN;
        else if (negative.occupied > 0L && negative.matched * 4L >= negative.occupied)
            result.disposition = Disposition.COVERAGE_GAP_NEGATIVE_CONTROL_FALSE_POSITIVE;
        else if (result.candidate.mismatch == 0L)
            result.disposition = Disposition.COMPLETE_GEOMETRY_PRESENT;
        else
            result.disposition = Disposition.FAIL_EXPECTED_PIXEL_MISSING;
        return result;
    }

    private static FiducialObservation FindFiducial(
        byte[] bytes, int width, int height, Color32 color)
    {
        FiducialObservation result = new FiducialObservation
        { minX = width, minRawY = height, maxX = -1, maxRawY = -1 };
        double sumX = 0d;
        double sumY = 0d;
        for (int rawY = 0; rawY < height; rawY++)
        for (int x = 0; x < width; x++)
        {
            int o = (rawY * width + x) * BytesPerPixel;
            if (!Near(bytes[o], color.r) || !Near(bytes[o + 1], color.g) ||
                !Near(bytes[o + 2], color.b) || bytes[o + 3] < 250) continue;
            result.count++;
            sumX += x + 0.5d;
            sumY += rawY + 0.5d;
            result.minX = Math.Min(result.minX, x);
            result.maxX = Math.Max(result.maxX, x);
            result.minRawY = Math.Min(result.minRawY, rawY);
            result.maxRawY = Math.Max(result.maxRawY, rawY);
        }
        if (result.count > 0)
        {
            result.centerX = sumX / result.count;
            result.centerRawY = sumY / result.count;
            long area = (long)(result.maxX - result.minX + 1) *
                (result.maxRawY - result.minRawY + 1);
            int boxWidth = result.maxX - result.minX + 1;
            int boxHeight = result.maxRawY - result.minRawY + 1;
            result.valid = result.count >= 16 && area <= 4096 &&
                result.count * 100L >= area * 80L &&
                boxWidth >= 4 && boxWidth <= 64 && boxHeight >= 4 && boxHeight <= 64;
        }
        return result;
    }

    private static AffineMap BuildAffine(
        Identity identity, FiducialObservation a, FiducialObservation b,
        FiducialObservation c, FiducialObservation d)
    {
        if (!a.valid || !b.valid || !c.valid || !d.valid) return default;
        Vector2 la = FiducialRectA(identity.screenWidth, identity.screenHeight).center;
        Vector2 lb = FiducialRectB(identity.screenWidth, identity.screenHeight).center;
        Vector2 lc = FiducialRectC(identity.screenWidth, identity.screenHeight).center;
        Vector2 ld = FiducialRectD(identity.screenWidth, identity.screenHeight).center;
        double dx = lb.x - la.x;
        double dy = lc.y - la.y;
        if (Math.Abs(dx) < 1d || Math.Abs(dy) < 1d) return default;
        AffineMap map = new AffineMap
        {
            originLogicalX = la.x,
            originLogicalY = la.y,
            originRawX = a.centerX,
            originRawY = a.centerRawY,
            xAxisRawX = (b.centerX - a.centerX) / dx,
            xAxisRawY = (b.centerRawY - a.centerRawY) / dx,
            yAxisRawX = (c.centerX - a.centerX) / dy,
            yAxisRawY = (c.centerRawY - a.centerRawY) / dy
        };
        map.determinant = map.xAxisRawX * map.yAxisRawY -
            map.xAxisRawY * map.yAxisRawX;
        if (Math.Abs(map.determinant) < 0.01d) return map;
        double predictedX;
        double predictedY;
        Map(map, ld.x, ld.y, out predictedX, out predictedY);
        double ex = predictedX - d.centerX;
        double ey = predictedY - d.centerRawY;
        map.fourthResidualPixels = Math.Sqrt(ex * ex + ey * ey);
        map.valid = map.fourthResidualPixels <= 1.5d;
        return map;
    }

    private static Candidate EvaluateMappedMask(
        Identity identity, byte[] source, byte[] surface, AffineMap map,
        float logicalOffsetX)
    {
        Candidate result = default;
        for (int sy = 0; sy < SourceHeight; sy++)
        for (int sx = 0; sx < SourceWidth; sx++)
        {
            int so = (sy * SourceWidth + sx) * BytesPerPixel;
            byte alpha = source[so + 3];
            if (alpha == 0) continue;
            result.occupied++;
            ClassifyPalette(source[so], source[so + 1], source[so + 2], alpha, ref result);
            double logicalX = identity.drawRect.x + logicalOffsetX +
                (sx + 0.5d) * identity.drawRect.width / SourceWidth;
            double logicalY = identity.drawRect.y +
                (SourceHeight - sy - 0.5d) * identity.drawRect.height / SourceHeight;
            double rawX;
            double rawY;
            Map(map, logicalX, logicalY, out rawX, out rawY);
            bool matched = SampleNeighborhood(
                surface, identity.targetWidth, identity.targetHeight,
                rawX, rawY, source[so], source[so + 1], source[so + 2], alpha,
                identity.targetSrgb);
            if (matched) result.matched++; else result.mismatch++;
        }
        return result;
    }

    private static bool SampleNeighborhood(
        byte[] surface, int width, int height, double rawX, double rawY,
        byte r, byte g, byte b, byte a, bool srgb)
    {
        int cx = Mathf.RoundToInt((float)(rawX - 0.5d));
        int cy = Mathf.RoundToInt((float)(rawY - 0.5d));
        ChannelRange rr = ExpectedRange(r, a, srgb);
        ChannelRange gr = ExpectedRange(g, a, srgb);
        ChannelRange br = ExpectedRange(b, a, srgb);
        for (int oy = -PixelNeighborhood; oy <= PixelNeighborhood; oy++)
        for (int ox = -PixelNeighborhood; ox <= PixelNeighborhood; ox++)
        {
            int x = cx + ox;
            int y = cy + oy;
            if (x < 0 || x >= width || y < 0 || y >= height) continue;
            int o = (y * width + x) * BytesPerPixel;
            if (InRange(surface[o], rr) && InRange(surface[o + 1], gr) &&
                InRange(surface[o + 2], br)) return true;
        }
        return false;
    }

    private struct ChannelRange { public int minimum; public int maximum; }

    private static ChannelRange ExpectedRange(byte source, byte alpha, bool srgb)
    {
        float sourceLinear = source / 255f;
        float af = alpha / 255f;
        float minimumLinear = af * sourceLinear;
        float maximumLinear = minimumLinear + (1f - af);
        float minimum = srgb ? Mathf.LinearToGammaSpace(minimumLinear) : minimumLinear;
        float maximum = srgb ? Mathf.LinearToGammaSpace(maximumLinear) : maximumLinear;
        return new ChannelRange
        {
            minimum = Mathf.Clamp(Mathf.FloorToInt(minimum * 255f) - ColorToleranceLsb, 0, 255),
            maximum = Mathf.Clamp(Mathf.CeilToInt(maximum * 255f) + ColorToleranceLsb, 0, 255)
        };
    }

    private static bool InRange(byte value, ChannelRange range)
    { return value >= range.minimum && value <= range.maximum; }

    private static void Map(AffineMap map, double logicalX, double logicalY,
        out double rawX, out double rawY)
    {
        double dx = logicalX - map.originLogicalX;
        double dy = logicalY - map.originLogicalY;
        rawX = map.originRawX + dx * map.xAxisRawX + dy * map.yAxisRawX;
        rawY = map.originRawY + dx * map.xAxisRawY + dy * map.yAxisRawY;
    }

    private static void ResolveTarget(
        out Camera camera, out RenderTexture target, out SpoutSender spout,
        out int enabledCameraCount, out int lastDepthCameraCount)
    {
        camera = null;
        target = null;
        spout = null;
        enabledCameraCount = 0;
        lastDepthCameraCount = 0;
        float maxDepth = float.NegativeInfinity;
        Camera[] cameras = Camera.allCameras;
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera candidate = cameras[i];
            if (candidate == null || !candidate.enabled || !candidate.gameObject.activeInHierarchy)
                continue;
            enabledCameraCount++;
            if (candidate.depth > maxDepth)
            {
                maxDepth = candidate.depth;
                camera = candidate;
                lastDepthCameraCount = 1;
            }
            else if (Mathf.Approximately(candidate.depth, maxDepth))
            {
                lastDepthCameraCount++;
            }
        }
        if (camera != null) target = camera.targetTexture;
        GameObject spoutObject = GameObject.Find(SpoutObjectName);
        if (spoutObject != null) spout = spoutObject.GetComponent<SpoutSender>();
    }

    private int FindFreeSlot()
    {
        for (int i = 0; i < _slots.Length; i++) if (!_slots[i].inFlight) return i;
        return -1;
    }

    private static void EnsureSnapshot(Slot slot, RenderTexture target)
    {
        bool exact = slot.snapshot != null && slot.snapshot.width == target.width &&
            slot.snapshot.height == target.height &&
            slot.snapshot.graphicsFormat == target.graphicsFormat &&
            slot.snapshot.antiAliasing == target.antiAliasing &&
            slot.snapshot.dimension == target.dimension;
        if (exact)
        {
            if (!slot.snapshot.IsCreated()) slot.snapshot.Create();
            return;
        }
        if (slot.snapshot != null)
        {
            slot.snapshot.Release();
            Destroy(slot.snapshot);
        }
        RenderTextureDescriptor descriptor = target.descriptor;
        descriptor.depthBufferBits = 0;
        descriptor.useMipMap = false;
        descriptor.autoGenerateMips = false;
        descriptor.enableRandomWrite = false;
        slot.snapshot = new RenderTexture(descriptor)
        {
            name = "Kiwi H1 VTuberOutput Stable Snapshot",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave
        };
        slot.snapshot.Create();
    }

    private void WriteRequest(Slot slot, long ticks, int frame)
    {
        _requestWriter.WriteLine(
            slot.requestSequence + "," + ticks + "," + frame + "," +
            Csv(slot.targetNativePtr) + "," + slot.snapshot.GetInstanceID() + "," +
            Csv(slot.snapshotNativePtr) + "," + slot.snapshot.width + "," +
            slot.snapshot.height + "," + Csv(slot.snapshot.graphicsFormat.ToString()) + "," +
            B(slot.snapshot.sRGB) + "," + IdentityValues(slot.identity));
    }

    private void WriteExact(Slot slot, long ticks, int frame, SurfaceResult r)
    {
        Candidate c = r.candidate;
        _exactWriter.WriteLine(
            slot.requestSequence + "," + ticks + "," + frame + "," + r.byteCount + "," +
            Csv(r.sha256) + "," + r.nonzeroRgbPixels + "," +
            FiducialValues(r.a) + "," + FiducialValues(r.b) + "," +
            FiducialValues(r.c) + "," + FiducialValues(r.d) + "," +
            B(r.map.valid) + "," + D(r.map.originRawX) + "," + D(r.map.originRawY) + "," +
            D(r.map.xAxisRawX) + "," + D(r.map.xAxisRawY) + "," +
            D(r.map.yAxisRawX) + "," + D(r.map.yAxisRawY) + "," +
            D(r.map.determinant) + "," + D(r.map.fourthResidualPixels) + "," +
            c.occupied + "," + c.landmark + "," + c.redAnchor + "," +
            c.greenAnchor + "," + c.yellowAnchor + "," + c.magentaAnchor + "," +
            c.unknownPalette + "," + c.matched + "," + c.mismatch + "," +
            c.negativeMatched + "," + Csv(r.disposition.ToString()) + "," +
            IdentityValues(slot.identity));
    }

    private static string FiducialValues(FiducialObservation f)
    { return B(f.valid) + "," + f.count + "," + D(f.centerX) + "," + D(f.centerRawY); }

    private void WriteGap(Identity identity, Disposition disposition, string detail = "")
    {
        _coverageGapCount++;
        WriteDisposition(identity, disposition, detail);
    }

    private void WriteDisposition(Identity identity, Disposition disposition, string detail = "")
    {
        if (_dispositionWriter == null) return;
        _dispositionWriter.WriteLine(IdentityValues(identity) + "," +
            Csv(disposition + (string.IsNullOrEmpty(detail) ? string.Empty : "_" + detail)));
    }

    private static string IdentityHeader()
    {
        var b = new StringBuilder();
        b.Append("drawEventSequence,callAttemptSequence,observationHostTicks,segment,eventType,");
        b.Append("callUnityFrame,h1VisualId,visualSequence,visualUnityFrame,semanticTimestamp,");
        b.Append("previewSemanticTimestamp,canonicalFrameId,publicationSequence,sourceFrameId,");
        b.Append("sourceGeneration,sourceHostTicks,rasterSequence,rasterSemanticTimestamp,");
        b.Append("rasterCanonicalFrameId,drawRectX,drawRectY,drawRectWidth,drawRectHeight,");
        b.Append("screenWidth,screenHeight,sourceTextureInstanceId,sourceFormat,");
        b.Append("sourceGraphicsFormat,sourceFilterMode,sourceSha256,activeColorSpace,");
        b.Append("graphicsDeviceType,guiMatrix,guiColor,guiDepth,guiEnabled,drawCallReturned,");
        b.Append("cameraInstanceId,cameraName,cameraDepth,cameraTargetDisplay,cameraEnabled,");
        b.Append("cameraRect,cameraPixelRect,enabledCameraCount,lastDepthCameraCount,");
        b.Append("targetInstanceId,targetName,targetWidth,targetHeight,targetGraphicsFormat,");
        b.Append("targetFormat,targetAntiAliasing,targetDimension,targetSrgb,targetIsCreated,");
        b.Append("spoutInstanceId,spoutCaptureMethod,spoutSourceInstanceId");
        return b.ToString();
    }

    private static string IdentityValues(Identity i)
    {
        var b = new StringBuilder();
        b.Append(i.drawEventSequence).Append(',').Append(i.callAttemptSequence).Append(',')
            .Append(i.observationHostTicks).Append(',').Append(Csv(i.segment)).Append(',')
            .Append(Csv(i.eventType.ToString())).Append(',').Append(i.callUnityFrame).Append(',')
            .Append(Csv(VisualId(i.visualSequence))).Append(',').Append(i.visualSequence).Append(',')
            .Append(i.visualUnityFrame).Append(',').Append(i.semanticTimestamp).Append(',')
            .Append(i.previewSemanticTimestamp).Append(',').Append(i.canonicalFrameId).Append(',')
            .Append(i.publicationSequence).Append(',').Append(i.sourceFrameId).Append(',')
            .Append(i.sourceGeneration).Append(',').Append(i.sourceHostTicks).Append(',')
            .Append(i.rasterSequence).Append(',').Append(i.rasterSemanticTimestamp).Append(',')
            .Append(i.rasterCanonicalFrameId).Append(',').Append(F(i.drawRect.x)).Append(',')
            .Append(F(i.drawRect.y)).Append(',').Append(F(i.drawRect.width)).Append(',')
            .Append(F(i.drawRect.height)).Append(',').Append(i.screenWidth).Append(',')
            .Append(i.screenHeight).Append(',').Append(i.sourceTextureInstanceId).Append(',')
            .Append(Csv(i.sourceFormat)).Append(',').Append(Csv(i.sourceGraphicsFormat)).Append(',')
            .Append(Csv(i.sourceFilterMode)).Append(',').Append(Csv(i.sourceSha256)).Append(',')
            .Append(Csv(i.activeColorSpace)).Append(',').Append(Csv(i.graphicsDeviceType)).Append(',')
            .Append(Csv(MatrixValue(i.guiMatrix))).Append(',').Append(Csv(ColorValue(i.guiColor))).Append(',')
            .Append(i.guiDepth).Append(',').Append(B(i.guiEnabled)).Append(',')
            .Append(B(i.drawCallReturned)).Append(',').Append(i.cameraInstanceId).Append(',')
            .Append(Csv(i.cameraName)).Append(',').Append(F(i.cameraDepth)).Append(',')
            .Append(i.cameraTargetDisplay).Append(',').Append(B(i.cameraEnabled)).Append(',')
            .Append(Csv(RectValue(i.cameraRect))).Append(',').Append(Csv(RectValue(i.cameraPixelRect))).Append(',')
            .Append(i.enabledCameraCount).Append(',').Append(i.lastDepthCameraCount).Append(',')
            .Append(i.targetInstanceId).Append(',').Append(Csv(i.targetName)).Append(',')
            .Append(i.targetWidth).Append(',').Append(i.targetHeight).Append(',')
            .Append(Csv(i.targetGraphicsFormat)).Append(',').Append(Csv(i.targetFormat)).Append(',')
            .Append(i.targetAntiAliasing).Append(',').Append(Csv(i.targetDimension)).Append(',')
            .Append(B(i.targetSrgb)).Append(',').Append(B(i.targetIsCreated)).Append(',')
            .Append(i.spoutInstanceId).Append(',').Append(Csv(i.spoutCaptureMethod)).Append(',')
            .Append(i.spoutSourceInstanceId);
        return b.ToString();
    }

    private void SaveFirstFailurePng(Slot slot)
    {
        string path = Path.Combine(_outputDirectory, "vtuber_output_first_failure.png");
        if (File.Exists(path) || slot.surfaceBytes == null) return;
        Texture2D texture = null;
        try
        {
            texture = new Texture2D(slot.identity.targetWidth, slot.identity.targetHeight,
                TextureFormat.RGBA32, false, true);
            texture.LoadRawTextureData(slot.surfaceBytes);
            texture.Apply(false, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
        }
        finally { if (texture != null) Destroy(texture); }
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
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            if (slot.snapshot == null || slot.inFlight) continue;
            slot.snapshot.Release();
            Destroy(slot.snapshot);
            slot.snapshot = null;
        }
    }

    private void WriteIncompleteAtShutdown()
    {
        if (_pending.valid)
        {
            WriteGap(_pending.identity, Disposition.QUIT_INCOMPLETE_PENDING);
            _pending.valid = false;
        }
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            if (!slot.inFlight || slot.outcomeWritten) continue;
            WriteGap(slot.identity, Disposition.QUIT_INCOMPLETE_READBACK);
            slot.outcomeWritten = true;
        }
    }

    private void WriteSummary(bool playerClosed)
    {
        if (string.IsNullOrEmpty(_summaryPath)) return;
        string[] lines =
        {
            "KIWI_H1_VTUBEROUTPUT_DUAL_SURFACE_DIAGNOSTIC",
            "UTC=" + DateTime.UtcNow.ToString("O", Invariant),
            "UNITY_VERSION=" + Application.unityVersion,
            "DEBUG_BUILD=" + B(Debug.isDebugBuild),
            "GRAPHICS_API=" + SystemInfo.graphicsDeviceType,
            "DIAGNOSTIC_ONLY=1",
            "PRODUCTION_BEHAVIOR_CHANGED=0",
            "FIDUCIAL_DISPLAY_INTERVENTION=1",
            "PLAYER_CLOSED=" + (playerClosed ? "YES" : "NO"),
            "CAPTURE_SEQUENCE=WaitForEndOfFrame|Graphics.CopyTexture|AsyncGPUReadback",
            "STABLE_SNAPSHOT=1",
            "SLOT_COUNT=" + SlotCount,
            "BLOCKING_WAIT_COUNT=0",
            "FIFO_QUEUE_COUNT=0",
            "COLOR_TOLERANCE_LSB=" + ColorToleranceLsb,
            "PIXEL_NEIGHBORHOOD=" + PixelNeighborhood,
            "MAPPING=FOUR_FIDUCIAL_THREE_POINT_AFFINE_PLUS_FOURTH_POINT_RESIDUAL",
            "MAPPING_SCALE_ASSUMPTION=NONE",
            "DRAW_OBSERVED_COUNT=" + _drawObservedCount,
            "FIDUCIAL_DRAW_COUNT=" + _fiducialDrawCount,
            "QUEUED_COUNT=" + _queuedCount,
            "REQUESTED_COUNT=" + _requestedCount,
            "COMPLETED_COUNT=" + _completedCount,
            "PASS_COUNT=" + _passCount,
            "FAIL_COUNT=" + _failCount,
            "COVERAGE_GAP_COUNT=" + _coverageGapCount,
            "POOL_EXHAUSTED_COUNT=" + _poolExhaustedCount,
            "READBACK_ERROR_COUNT=" + _readbackErrorCount,
            "PENDING_SUPERSEDED_COUNT=" + _pendingSupersededCount,
            "IN_FLIGHT_AT_SUMMARY=" + _inFlight,
            "MAX_IN_FLIGHT=" + _maxInFlight,
            "PENDING_AT_SUMMARY=" + B(_pending.valid),
            "SHUTDOWN_REQUESTED=" + B(_shutdownRequested)
        };
        File.WriteAllLines(_summaryPath, lines, new UTF8Encoding(false));
    }

    private StreamWriter CreateWriter(string name, string header)
    {
        var writer = new StreamWriter(Path.Combine(_outputDirectory, name), false,
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
    }

    private static void Close(ref StreamWriter writer)
    { if (writer == null) return; writer.Flush(); writer.Dispose(); writer = null; }

    private static Rect FiducialRectA(int width, int height)
    { return new Rect(width - FiducialMargin - FiducialSize, height - FiducialMargin - FiducialSize, FiducialSize, FiducialSize); }
    private static Rect FiducialRectB(int width, int height)
    { return new Rect(FiducialMargin, height - FiducialMargin - FiducialSize, FiducialSize, FiducialSize); }
    private static Rect FiducialRectC(int width, int height)
    { return new Rect(width - FiducialMargin - FiducialSize, FiducialMargin, FiducialSize, FiducialSize); }
    private static Rect FiducialRectD(int width, int height)
    { return new Rect(width * 0.5f - FiducialSize * 0.5f, height - FiducialMargin - FiducialSize, FiducialSize, FiducialSize); }

    private static bool RectAndScreenValid(Rect r, int width, int height)
    { return width > 0 && height > 0 && IsFinite(r.x) && IsFinite(r.y) &&
        IsFinite(r.width) && IsFinite(r.height) && r.width > 0f && r.height > 0f &&
        r.xMax > 0f && r.yMax > 0f && r.xMin < width && r.yMin < height; }
    private static bool IsFinite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
    private static bool MatrixFinite(Matrix4x4 m)
    { for (int i = 0; i < 16; i++) if (!IsFinite(m[i])) return false; return true; }
    private static bool ApproximatelyWhite(Color c)
    { return Mathf.Abs(c.r - 1f) <= 0.0001f && Mathf.Abs(c.g - 1f) <= 0.0001f &&
        Mathf.Abs(c.b - 1f) <= 0.0001f && Mathf.Abs(c.a - 1f) <= 0.0001f; }
    private static bool Near(byte actual, byte expected)
    { return Math.Abs(actual - expected) <= ColorToleranceLsb; }
    private static bool PaletteComplete(Candidate c)
    { return c.occupied > 0 && c.landmark > 0 && c.redAnchor > 0 &&
        c.greenAnchor > 0 && c.yellowAnchor > 0 && c.magentaAnchor > 0 &&
        c.unknownPalette == 0; }
    private static void ClassifyPalette(byte r, byte g, byte b, byte a, ref Candidate c)
    {
        if (r == 50 && g == 235 && b == 255 && a == 235) c.landmark++;
        else if (r == 255 && g == 70 && b == 70 && a == 255) c.redAnchor++;
        else if (r == 70 && g == 255 && b == 90 && a == 255) c.greenAnchor++;
        else if (r == 255 && g == 230 && b == 60 && a == 255) c.yellowAnchor++;
        else if (r == 255 && g == 90 && b == 235 && a == 255) c.magentaAnchor++;
        else c.unknownPalette++;
    }
    private static void EncodeColor32Bytes(Color32[] pixels, byte[] destination)
    { for (int i = 0; i < pixels.Length; i++) { int o = i * 4; Color32 p = pixels[i];
        destination[o] = p.r; destination[o + 1] = p.g; destination[o + 2] = p.b; destination[o + 3] = p.a; } }
    private static string Sha256Hex(byte[] bytes)
    { using (SHA256 sha = SHA256.Create()) { byte[] hash = sha.ComputeHash(bytes);
        var b = new StringBuilder(hash.Length * 2); for (int i = 0; i < hash.Length; i++)
        b.Append(hash[i].ToString("x2", Invariant)); return b.ToString(); } }
    private static string NativePtr(Texture texture)
    { try { return "0x" + texture.GetNativeTexturePtr().ToInt64().ToString("X", Invariant); }
      catch (Exception ex) { return "UNAVAILABLE_" + ex.GetType().Name; } }
    private string ResolveOutputDirectory()
    { string configured = Environment.GetEnvironmentVariable(OutputDirectoryEnvironment);
      return !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(configured.Trim()) :
        Path.Combine(Application.persistentDataPath, "KiwiH1LandmarkerBoundary"); }
    private static string VisualId(long sequence)
    { return sequence > 0 ? "H1V-" + sequence.ToString("D6", Invariant) : string.Empty; }
    private static string Csv(string value)
    { string safe = value ?? string.Empty; return "\"" + safe.Replace("\"", "\"\"") + "\""; }
    private static string B(bool value) { return value ? "1" : "0"; }
    private static string F(float value) { return value.ToString("R", Invariant); }
    private static string D(double value) { return value.ToString("R", Invariant); }
    private static string RectValue(Rect r)
    { return F(r.x) + "|" + F(r.y) + "|" + F(r.width) + "|" + F(r.height); }
    private static string ColorValue(Color c)
    { return F(c.r) + "|" + F(c.g) + "|" + F(c.b) + "|" + F(c.a); }
    private static string MatrixValue(Matrix4x4 m)
    { var b = new StringBuilder(); for (int i = 0; i < 16; i++)
      { if (i > 0) b.Append('|'); b.Append(F(m[i])); } return b.ToString(); }
}
#endif
