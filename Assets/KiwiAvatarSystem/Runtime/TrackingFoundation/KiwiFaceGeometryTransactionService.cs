using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using Google.Protobuf;
using Mediapipe;
using Mediapipe.Tasks.Core.Proto;
using Mediapipe.Tasks.Vision.FaceGeometry;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using UnityEngine;
using FaceGeometryProto =
    Mediapipe.Tasks.Vision.FaceGeometry.Proto.FaceGeometry;

/// <summary>
/// P3C-only same-sample IE landmark to FaceGeometry transaction owner.
///
/// This service deliberately has no Avatar, Head, FacePart, Canonical, camera,
/// ROI, smoothing, or presentation responsibility. The caller owns activation;
/// current Production keeps that activation disabled by default.
/// </summary>
internal sealed class KiwiFaceGeometryTransactionService
{
    internal const int LandmarkCount = 468;
    internal const int CoordinateCount = LandmarkCount * 3;
    internal const int First468CopyBytes =
        CoordinateCount * sizeof(float);

    private const string MetadataEntryName =
        "geometry_pipeline_metadata_landmarks.binarypb";

    private const string ExpectedBundleSha256 =
        "B261925D4AAD812B47A0E8D58C1BAA1223270A5D1F663D78338BC881C003879D";

    private const string ExpectedMetadataSha256 =
        "BDBCDA96DFCB7DA883DA124AAA2C55DEE49770D934F0FCC71747F8C21BDC75B4";

    private const double VerticalFieldOfViewDegrees = 63.0;
    private const double Near = 1.0;
    private const double Far = 10000.0;

    private enum CompletionStatus
    {
        None = 0,
        Valid = 1,
        EmptyPacket = 2,
        InvalidPose = 3,
        ParseFailure = 4
    }

    /// <summary>
    /// Raw semantic 4x4 pose values. MatrixData layout is decoded only to name
    /// rows/columns; no transpose, inverse, handedness conversion, or Unity
    /// Transform conversion is performed.
    /// </summary>
    internal readonly struct Pose16
    {
        internal readonly float m00;
        internal readonly float m01;
        internal readonly float m02;
        internal readonly float m03;
        internal readonly float m10;
        internal readonly float m11;
        internal readonly float m12;
        internal readonly float m13;
        internal readonly float m20;
        internal readonly float m21;
        internal readonly float m22;
        internal readonly float m23;
        internal readonly float m30;
        internal readonly float m31;
        internal readonly float m32;
        internal readonly float m33;

        internal Pose16(MatrixData matrix)
        {
            m00 = Read(matrix, 0, 0);
            m01 = Read(matrix, 0, 1);
            m02 = Read(matrix, 0, 2);
            m03 = Read(matrix, 0, 3);
            m10 = Read(matrix, 1, 0);
            m11 = Read(matrix, 1, 1);
            m12 = Read(matrix, 1, 2);
            m13 = Read(matrix, 1, 3);
            m20 = Read(matrix, 2, 0);
            m21 = Read(matrix, 2, 1);
            m22 = Read(matrix, 2, 2);
            m23 = Read(matrix, 2, 3);
            m30 = Read(matrix, 3, 0);
            m31 = Read(matrix, 3, 1);
            m32 = Read(matrix, 3, 2);
            m33 = Read(matrix, 3, 3);
        }

        internal bool IsFinite()
        {
            return
                Finite(m00) && Finite(m01) &&
                Finite(m02) && Finite(m03) &&
                Finite(m10) && Finite(m11) &&
                Finite(m12) && Finite(m13) &&
                Finite(m20) && Finite(m21) &&
                Finite(m22) && Finite(m23) &&
                Finite(m30) && Finite(m31) &&
                Finite(m32) && Finite(m33);
        }

        private static float Read(
            MatrixData matrix,
            int row,
            int column)
        {
            int index =
                matrix.Layout == MatrixData.Types.Layout.RowMajor
                    ? row * 4 + column
                    : column * 4 + row;

            return matrix.PackedData[index];
        }
    }

    private sealed class InputTransaction
    {
        internal readonly ulong frameId;
        internal readonly long sourceHostTicks;
        internal readonly int cameraGeneration;
        internal readonly int trackingSessionGeneration;
        internal readonly int providerGeneration;
        internal readonly int modelGeneration;
        internal readonly KiwiTrackingBackend backend;
        internal readonly int semanticFrameWidth;
        internal readonly int semanticFrameHeight;

        internal bool retired;
        internal bool consumed;
        internal int submitAttempts;

        private float[] _first468;

        internal InputTransaction(
            ulong frameId,
            long sourceHostTicks,
            int cameraGeneration,
            int trackingSessionGeneration,
            int providerGeneration,
            int modelGeneration,
            int semanticFrameWidth,
            int semanticFrameHeight,
            Vector3[] landmarks)
        {
            this.frameId = frameId;
            this.sourceHostTicks = sourceHostTicks;
            this.cameraGeneration = cameraGeneration;
            this.trackingSessionGeneration =
                trackingSessionGeneration;
            this.providerGeneration = providerGeneration;
            this.modelGeneration = modelGeneration;
            backend = KiwiTrackingBackend.InferenceEngine;
            this.semanticFrameWidth = semanticFrameWidth;
            this.semanticFrameHeight = semanticFrameHeight;

            _first468 = new float[CoordinateCount];
            for (int i = 0; i < LandmarkCount; ++i)
            {
                int offset = i * 3;
                Vector3 landmark = landmarks[i];
                _first468[offset] = landmark.x;
                _first468[offset + 1] = landmark.y;
                _first468[offset + 2] = landmark.z;
            }
        }

        internal NormalizedLandmarkList BuildLandmarkList()
        {
            if (_first468 == null)
            {
                throw new InvalidOperationException(
                    "Geometry landmark payload is no longer owned.");
            }

            var result = new NormalizedLandmarkList();
            for (int i = 0; i < LandmarkCount; ++i)
            {
                int offset = i * 3;
                result.Landmark.Add(
                    new NormalizedLandmark
                    {
                        X = _first468[offset],
                        Y = _first468[offset + 1],
                        Z = _first468[offset + 2]
                    });
            }

            return result;
        }

        internal void ReleaseLandmarkPayload()
        {
            _first468 = null;
        }
    }

    private struct CallbackCompletion
    {
        internal bool exists;
        internal int graphRunId;
        internal ulong frameId;
        internal CompletionStatus status;
        internal Pose16 pose;
    }

    private sealed class GraphRun
    {
        internal readonly int runId;
        internal readonly int streamId;
        internal readonly int width;
        internal readonly int height;
        internal readonly CalculatorGraph graph;
        internal readonly IntPtr graphPtr;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        internal readonly int diagnosticServiceId;
#endif

        internal readonly object callbackGate = new object();
        internal bool resultPublicationAccepting = true;
        internal bool nativeGraphAlive = true;
        internal bool nativeGraphDestroyStarted;
        internal bool noFutureCallbacks;
        internal int activeCallbacks;
        internal bool callbackRootsReleased;

        internal GraphRun(
            int runId,
            int streamId,
            int width,
            int height,
            CalculatorGraph graph
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            ,
            int diagnosticServiceId
#endif
            )
        {
            this.runId = runId;
            this.streamId = streamId;
            this.width = width;
            this.height = height;
            this.graph = graph;
            graphPtr = graph.mpPtr;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            this.diagnosticServiceId = diagnosticServiceId;
#endif
        }

        internal bool TryEnterCallbackLifetime()
        {
            lock (callbackGate)
            {
                if (callbackRootsReleased)
                {
                    return false;
                }

                ++activeCallbacks;
                return true;
            }
        }

        internal bool ExitCallbackLifetime()
        {
            lock (callbackGate)
            {
                if (activeCallbacks <= 0)
                {
                    throw new InvalidOperationException(
                        "FaceGeometry callback lifetime counter underflow.");
                }

                --activeCallbacks;
                return
                    noFutureCallbacks &&
                    activeCallbacks == 0 &&
                    !callbackRootsReleased;
            }
        }

        internal void RetireResultPublication()
        {
            lock (callbackGate)
            {
                resultPublicationAccepting = false;
            }
        }

        internal bool IsResultPublicationAccepting()
        {
            lock (callbackGate)
            {
                return resultPublicationAccepting;
            }
        }

        internal bool TryBeginNativeGraphDestroy()
        {
            lock (callbackGate)
            {
                if (
                    nativeGraphDestroyStarted ||
                    !nativeGraphAlive ||
                    callbackRootsReleased)
                {
                    return false;
                }

                nativeGraphDestroyStarted = true;
                return true;
            }
        }

        internal void MarkNativeGraphDestroyed()
        {
            lock (callbackGate)
            {
                if (
                    !nativeGraphDestroyStarted ||
                    !nativeGraphAlive ||
                    callbackRootsReleased)
                {
                    throw new InvalidOperationException(
                        "Invalid FaceGeometry native graph destruction transition.");
                }

                nativeGraphAlive = false;
                noFutureCallbacks = true;
            }
        }

        internal bool TryMarkCallbackRootsReleased()
        {
            lock (callbackGate)
            {
                if (
                    callbackRootsReleased ||
                    nativeGraphAlive ||
                    !noFutureCallbacks ||
                    activeCallbacks != 0)
                {
                    return false;
                }

                callbackRootsReleased = true;
                return true;
            }
        }

        internal bool AreCallbackRootsReleased()
        {
            lock (callbackGate)
            {
                return callbackRootsReleased;
            }
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        internal void CaptureLifecycleForDiagnostics(
            out bool graphAlive,
            out int callbacks)
        {
            lock (callbackGate)
            {
                graphAlive = nativeGraphAlive;
                callbacks = activeCallbacks;
            }
        }
#endif
    }

    private sealed class CallbackRegistration
    {
        internal readonly KiwiFaceGeometryTransactionService owner;
        internal readonly GraphRun run;

        internal CallbackRegistration(
            KiwiFaceGeometryTransactionService owner,
            GraphRun run)
        {
            this.owner = owner;
            this.run = run;
        }
    }

    private static readonly object CallbackRegistryLock =
        new object();

    private static readonly Dictionary<int, CallbackRegistration>
        CallbackRegistry =
            new Dictionary<int, CallbackRegistration>();

    private static readonly CalculatorGraph.NativePacketCallback
        NativeOutputCallback = OnNativeOutput;

    private static int _nextStreamId;

    private readonly object _sync = new object();
    private readonly string _faceLandmarkerBundlePath;

    private byte[] _metadataBytes;
    private GraphRun _activeRun;
    private GraphRun _retiredRun;
    private InputTransaction _inFlight;
    private InputTransaction _latestPending;
    private CallbackCompletion _completion;
    private bool _resetRequested;
    private bool _shutdownRequested;
    private int _nextGraphRunId;
    private ulong _lastAcceptedGeometryFrameId;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
    private readonly int _diagnosticServiceId;
#endif

    internal ulong LastAcceptedGeometryFrameId
    {
        get
        {
            lock (_sync)
            {
                return _lastAcceptedGeometryFrameId;
            }
        }
    }

    internal bool IsShutdownComplete
    {
        get
        {
            lock (_sync)
            {
                return
                    _shutdownRequested &&
                    _activeRun == null &&
                    _retiredRun == null &&
                    _inFlight == null &&
                    _latestPending == null;
            }
        }
    }

    internal KiwiFaceGeometryTransactionService(
        string faceLandmarkerBundlePath)
    {
        if (string.IsNullOrEmpty(faceLandmarkerBundlePath))
        {
            throw new ArgumentException(
                "A Face Landmarker bundle path is required.",
                nameof(faceLandmarkerBundlePath));
        }

        _faceLandmarkerBundlePath = faceLandmarkerBundlePath;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        _diagnosticServiceId =
            KiwiFaceGeometryP3DDiagnostics.RecordServiceCreated();
#endif
    }

    internal bool AdmitAcceptedInferenceResult(
        ulong frameId,
        long sourceHostTicks,
        int cameraGeneration,
        int trackingSessionGeneration,
        int providerGeneration,
        int modelGeneration,
        int semanticFrameWidth,
        int semanticFrameHeight,
        Vector3[] landmarks)
    {
        if (!CanCreateTransaction(
                frameId,
                sourceHostTicks,
                cameraGeneration,
                trackingSessionGeneration,
                providerGeneration,
                modelGeneration,
                semanticFrameWidth,
                semanticFrameHeight,
                landmarks))
        {
            return false;
        }

        var transaction = new InputTransaction(
            frameId,
            sourceHostTicks,
            cameraGeneration,
            trackingSessionGeneration,
            providerGeneration,
            modelGeneration,
            semanticFrameWidth,
            semanticFrameHeight,
            landmarks);

        bool submitNow = false;
        InputTransaction replacedPending = null;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        int diagnosticInFlight = 0;
        int diagnosticPending = 0;
#endif

        lock (_sync)
        {
            if (_shutdownRequested)
            {
                transaction.ReleaseLandmarkPayload();
                return false;
            }

            if (
                _retiredRun != null ||
                _inFlight != null ||
                _resetRequested)
            {
                replacedPending = _latestPending;
                _latestPending = transaction;

                if (
                    _activeRun != null &&
                    (
                        _activeRun.width != semanticFrameWidth ||
                        _activeRun.height != semanticFrameHeight
                    ))
                {
                    if (_inFlight != null)
                    {
                        _inFlight.retired = true;
                    }
                    _resetRequested = true;
                }
            }
            else if (
                _activeRun != null &&
                (
                    _activeRun.width != semanticFrameWidth ||
                    _activeRun.height != semanticFrameHeight
                ))
            {
                replacedPending = _latestPending;
                _latestPending = transaction;
                _resetRequested = true;
            }
            else
            {
                submitNow = true;
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            diagnosticInFlight = _inFlight == null ? 0 : 1;
            diagnosticPending = _latestPending == null ? 0 : 1;
#endif
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        KiwiFaceGeometryP3DDiagnostics.RecordAdmission(
            replacedPending != null,
            diagnosticInFlight,
            diagnosticPending);
#endif

        replacedPending?.ReleaseLandmarkPayload();

        if (submitNow)
        {
            return Submit(transaction);
        }

        return true;
    }

    /// <summary>
    /// Main-thread pump. It consumes the one callback handoff, applies current
    /// authority/order gates, and admits only the newest pending transaction.
    /// </summary>
    internal void Pump(
        int currentSemanticFrameWidth,
        int currentSemanticFrameHeight)
    {
        TryDisposeRetiredRun();

        GraphRun observedRun;
        lock (_sync)
        {
            observedRun = _activeRun;
        }

        if (observedRun != null)
        {
            try
            {
                if (observedRun.graph.HasError())
                {
                    RequestGraphReset();
                }
            }
            catch
            {
                RequestGraphReset();
            }
        }

        CallbackCompletion completion = default;
        bool hasCompletion = false;
        bool shouldReset = false;
        InputTransaction pendingToRelease = null;

        lock (_sync)
        {
            if (_shutdownRequested)
            {
                return;
            }

            if (
                _latestPending != null &&
                !IsTransactionCurrent(
                    _latestPending,
                    currentSemanticFrameWidth,
                    currentSemanticFrameHeight))
            {
                pendingToRelease = _latestPending;
                _latestPending = null;
            }

            if (
                _inFlight != null &&
                !IsTransactionCurrent(
                    _inFlight,
                    currentSemanticFrameWidth,
                    currentSemanticFrameHeight))
            {
                _inFlight.retired = true;
            }

            if (_completion.exists)
            {
                completion = _completion;
                _completion = default;
                hasCompletion = true;
            }

            shouldReset = _resetRequested;
        }

        pendingToRelease?.ReleaseLandmarkPayload();

        if (hasCompletion)
        {
            ConsumeCompletion(
                completion,
                currentSemanticFrameWidth,
                currentSemanticFrameHeight);
        }

        lock (_sync)
        {
            shouldReset = shouldReset || _resetRequested;
        }

        if (shouldReset)
        {
            RetireActiveRun();
            TryDisposeRetiredRun();
        }

        SubmitLatestPendingIfIdle(
            currentSemanticFrameWidth,
            currentSemanticFrameHeight);
    }

    internal void BeginShutdown()
    {
        InputTransaction pending = null;

        lock (_sync)
        {
            if (_shutdownRequested)
            {
                return;
            }

            _shutdownRequested = true;
            pending = _latestPending;
            _latestPending = null;
            _completion = default;

            if (_inFlight != null)
            {
                _inFlight.retired = true;
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            KiwiFaceGeometryP3DDiagnostics.RecordState(
                _inFlight == null ? 0 : 1,
                _latestPending == null ? 0 : 1);
#endif
        }

        pending?.ReleaseLandmarkPayload();
        RetireActiveRun();
        TryDisposeRetiredRun();
    }

    internal void PumpShutdown()
    {
        if (!_shutdownRequested)
        {
            return;
        }

        TryDisposeRetiredRun();
    }

    private bool Submit(InputTransaction transaction)
    {
        ++transaction.submitAttempts;

        if (!EnsureGraph(
                transaction.semanticFrameWidth,
                transaction.semanticFrameHeight))
        {
            return RetainForOneRetry(transaction);
        }

        NormalizedLandmarkList landmarks;
        try
        {
            landmarks = transaction.BuildLandmarkList();
        }
        catch
        {
            transaction.ReleaseLandmarkPayload();
            return false;
        }

        GraphRun run;
        lock (_sync)
        {
            if (
                _shutdownRequested ||
                _activeRun == null ||
                _activeRun.width != transaction.semanticFrameWidth ||
                _activeRun.height != transaction.semanticFrameHeight)
            {
                transaction.ReleaseLandmarkPayload();
                return false;
            }

            run = _activeRun;
            _inFlight = transaction;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            KiwiFaceGeometryP3DDiagnostics.RecordSubmitted(
                1,
                _latestPending == null ? 0 : 1);
#endif
        }

        try
        {
            Packet<NormalizedLandmarkList> packet =
                Packet.CreateProtoAt(
                    landmarks,
                    checked((long)transaction.frameId));

            run.graph.AddPacketToInputStream(
                "face_landmarks",
                packet);

            transaction.ReleaseLandmarkPayload();
            return true;
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_inFlight, transaction))
                {
                    _inFlight = null;
                }
                _resetRequested = true;
            }

            return RetainForOneRetry(transaction);
        }
    }

    private bool RetainForOneRetry(InputTransaction transaction)
    {
        InputTransaction replaced = null;
        bool retained = false;

        lock (_sync)
        {
            if (
                !_shutdownRequested &&
                transaction.submitAttempts <= 1 &&
                IsTransactionCurrent(
                    transaction,
                    transaction.semanticFrameWidth,
                    transaction.semanticFrameHeight))
            {
                replaced = _latestPending;
                _latestPending = transaction;
                _resetRequested = true;
                retained = true;
            }
        }

        replaced?.ReleaseLandmarkPayload();
        if (!retained)
        {
            transaction.ReleaseLandmarkPayload();
        }

        return retained;
    }

    private void SubmitLatestPendingIfIdle(
        int currentSemanticFrameWidth,
        int currentSemanticFrameHeight)
    {
        TryDisposeRetiredRun();

        InputTransaction transaction = null;
        lock (_sync)
        {
            if (
                _shutdownRequested ||
                _retiredRun != null ||
                _inFlight != null ||
                _resetRequested ||
                _latestPending == null)
            {
                return;
            }

            if (!IsTransactionCurrent(
                    _latestPending,
                    currentSemanticFrameWidth,
                    currentSemanticFrameHeight))
            {
                transaction = _latestPending;
                _latestPending = null;
                transaction.ReleaseLandmarkPayload();
                return;
            }

            transaction = _latestPending;
            _latestPending = null;
        }

        Submit(transaction);
    }

    private void ConsumeCompletion(
        CallbackCompletion completion,
        int currentSemanticFrameWidth,
        int currentSemanticFrameHeight)
    {
        InputTransaction transaction = null;

        lock (_sync)
        {
            if (
                _activeRun == null ||
                completion.graphRunId != _activeRun.runId ||
                _inFlight == null ||
                completion.frameId != _inFlight.frameId ||
                _inFlight.consumed)
            {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                KiwiFaceGeometryP3DDiagnostics.RecordCompletionIdentityMismatch();
#endif
                _resetRequested = true;
                return;
            }

            transaction = _inFlight;
            transaction.consumed = true;
            _inFlight = null;

            bool valid =
                completion.status == CompletionStatus.Valid &&
                !transaction.retired &&
                completion.pose.IsFinite() &&
                transaction.frameId > _lastAcceptedGeometryFrameId &&
                IsTransactionCurrent(
                    transaction,
                    currentSemanticFrameWidth,
                    currentSemanticFrameHeight) &&
                _activeRun.width == transaction.semanticFrameWidth &&
                _activeRun.height == transaction.semanticFrameHeight;

            if (valid)
            {
                // P3C intentionally consumes the pose internally. Only channel
                // order is retained; no Head/Canonical/FacePart publication and
                // no Geometry-Core hold-last pose are introduced.
                _lastAcceptedGeometryFrameId = transaction.frameId;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                KiwiFaceGeometryP3DDiagnostics.RecordAccepted(
                    _diagnosticServiceId,
                    _activeRun.runId,
                    _activeRun.streamId,
                    transaction.frameId,
                    transaction.sourceHostTicks,
                    transaction.cameraGeneration,
                    transaction.trackingSessionGeneration,
                    transaction.providerGeneration,
                    transaction.modelGeneration,
                    transaction.backend,
                    transaction.semanticFrameWidth,
                    transaction.semanticFrameHeight,
                    System.Diagnostics.Stopwatch.GetTimestamp());
#endif
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            KiwiFaceGeometryP3DDiagnostics.RecordState(
                0,
                _latestPending == null ? 0 : 1);
#endif
        }
    }

    private bool EnsureGraph(int width, int height)
    {
        lock (_sync)
        {
            if (
                _shutdownRequested ||
                _retiredRun != null)
            {
                return false;
            }

            if (_activeRun != null)
            {
                return
                    _activeRun.width == width &&
                    _activeRun.height == height;
            }
        }

        CalculatorGraph graph = null;
        GraphRun run = null;
        PacketMap sidePackets = null;

        try
        {
            byte[] metadata = LoadMetadataOnce();
            CalculatorGraphConfig config = BuildGraphConfig(metadata);
            graph = new CalculatorGraph(config);

            int runId = Interlocked.Increment(ref _nextGraphRunId);
            int streamId = NextStreamId();
            run = new GraphRun(
                runId,
                streamId,
                width,
                height,
                graph
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                ,
                _diagnosticServiceId
#endif
                );
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            KiwiFaceGeometryP3DDiagnostics.RecordGraphCreated(
                run.streamId);
#endif

            RegisterCallback(run);
            graph.ObserveOutputStream(
                "face_geometry",
                streamId,
                NativeOutputCallback,
                false);

            sidePackets = new PacketMap();
            var dimensionFrame = new ImageFrame(
                ImageFormat.Types.Format.Srgba,
                width,
                height);
            dimensionFrame.SetToZero();
            sidePackets.Emplace(
                "dimension_image",
                Packet.CreateImageFrame(dimensionFrame));

            graph.StartRun(sidePackets);

            lock (_sync)
            {
                if (
                    _shutdownRequested ||
                    _activeRun != null ||
                    _retiredRun != null)
                {
                    run.RetireResultPublication();
                }
                else
                {
                    _activeRun = run;
                    _resetRequested = false;
                    run = null;
                    graph = null;
                }
            }

            if (run != null)
            {
                DestroyGraphAndFinalize(run);
                return false;
            }

            return true;
        }
        catch
        {
            if (run != null)
            {
                run.RetireResultPublication();
                DestroyGraphAndFinalize(run);
                run = null;
                graph = null;
            }
            else if (graph != null)
            {
                graph.Dispose();
            }

            lock (_sync)
            {
                _resetRequested = true;
            }
            return false;
        }
        finally
        {
            sidePackets?.Dispose();
        }
    }

    private void RetireActiveRun()
    {
        GraphRun run = null;

        lock (_sync)
        {
            if (_activeRun == null)
            {
                _resetRequested = false;
                return;
            }

            if (_retiredRun != null)
            {
                return;
            }

            run = _activeRun;
            _activeRun = null;
            _retiredRun = run;
            _completion = default;
            _resetRequested = false;

            if (_inFlight != null)
            {
                _inFlight.retired = true;
                _inFlight = null;
            }

            run.RetireResultPublication();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            KiwiFaceGeometryP3DDiagnostics.RecordGraphRetired();
            KiwiFaceGeometryP3DDiagnostics.RecordState(
                0,
                _latestPending == null ? 0 : 1);
#endif
        }

        try
        {
            run.graph.CloseAllPacketSources();
        }
        catch
        {
        }

        try
        {
            // Cancel is the installed non-waiting teardown path. P3C never calls
            // WaitUntilIdle/WaitUntilDone on the Unity main thread.
            run.graph.Cancel();
        }
        catch
        {
        }
    }

    private void TryDisposeRetiredRun()
    {
        GraphRun run;

        lock (_sync)
        {
            run = _retiredRun;
            if (run == null)
            {
                return;
            }
        }

        DestroyGraphAndFinalize(run);
    }

    private void DestroyGraphAndFinalize(GraphRun run)
    {
        // Result publication authority is retired independently from callback
        // lifetime accounting. Callbacks may still enter while the native graph
        // is alive, but they can no longer publish a completion.
        run.RetireResultPublication();

        if (run.TryBeginNativeGraphDestroy())
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            KiwiFaceGeometryP3DDiagnostics.RecordGraphDestroyStarted();
#endif
            try
            {
                run.graph.Cancel();
            }
            catch
            {
            }

            // The installed wrapper returns from Dispose only after
            // mp_CalculatorGraph__delete has returned. Callback registry state
            // and the static delegate remain rooted throughout this call.
            run.graph.Dispose();
            run.MarkNativeGraphDestroyed();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            KiwiFaceGeometryP3DDiagnostics.RecordGraphDestroyed();
#endif
        }

        TryFinalizeDestroyedRun(run);
    }

    private void TryFinalizeDestroyedRun(GraphRun run)
    {
        if (!TryReleaseCallbackRoots(run))
        {
            return;
        }

        lock (_sync)
        {
            if (ReferenceEquals(_retiredRun, run))
            {
                _retiredRun = null;
            }
        }
    }

    private void HandleNativeOutput(
        GraphRun run,
        IntPtr graphPtr,
        IntPtr packetPtr)
    {
        if (graphPtr != run.graphPtr)
        {
            RequestGraphReset();
            return;
        }

        var completion = new CallbackCompletion
        {
            exists = true,
            graphRunId = run.runId,
            status = CompletionStatus.EmptyPacket
        };

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        bool diagnosticEnabled = KiwiFaceGeometryP3DDiagnostics.Enabled;
        long allocatedBefore = diagnosticEnabled
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0L;
        int gc0Before = diagnosticEnabled ? GC.CollectionCount(0) : 0;
        int gc1Before = diagnosticEnabled ? GC.CollectionCount(1) : 0;
        int gc2Before = diagnosticEnabled ? GC.CollectionCount(2) : 0;
        bool transientFaceGeometryCreated = false;
#endif

        try
        {
            using (Packet<FaceGeometryProto> packet =
                Packet<FaceGeometryProto>.CreateForReference(packetPtr))
            {
                long timestamp = packet.TimestampMicroseconds();
                if (timestamp <= 0L)
                {
                    completion.status = CompletionStatus.InvalidPose;
                }
                else
                {
                    completion.frameId = (ulong)timestamp;
                    if (!packet.IsEmpty())
                    {
                        FaceGeometryProto geometry =
                            packet.Get(FaceGeometryProto.Parser);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                        transientFaceGeometryCreated = true;
#endif
                        MatrixData matrix = geometry.PoseTransformMatrix;

                        if (
                            matrix != null &&
                            matrix.Rows == 4 &&
                            matrix.Cols == 4 &&
                            matrix.PackedData.Count == 16)
                        {
                            var pose = new Pose16(matrix);
                            completion.pose = pose;
                            completion.status = pose.IsFinite()
                                ? CompletionStatus.Valid
                                : CompletionStatus.InvalidPose;
                        }
                        else
                        {
                            completion.status = CompletionStatus.InvalidPose;
                        }

                        // geometry, Mesh3d, MatrixData, SerializedProto, and the
                        // borrowed packet do not leave this callback scope.
                    }
                }
            }
        }
        catch
        {
            completion.status = CompletionStatus.ParseFailure;
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        if (diagnosticEnabled)
        {
            long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            KiwiFaceGeometryP3DDiagnostics.RecordCallbackAllocation(
                transientFaceGeometryCreated,
                allocatedAfter >= allocatedBefore
                    ? allocatedAfter - allocatedBefore
                    : -1L,
                GC.CollectionCount(0) - gc0Before,
                GC.CollectionCount(1) - gc1Before,
                GC.CollectionCount(2) - gc2Before);
        }
#endif

        lock (_sync)
        {
            if (
                _shutdownRequested ||
                _activeRun == null ||
                _activeRun.runId != run.runId ||
                !run.IsResultPublicationAccepting())
            {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                KiwiFaceGeometryP3DDiagnostics.RecordCallbackHandoff(false);
#endif
                return;
            }

            if (_completion.exists)
            {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                KiwiFaceGeometryP3DDiagnostics.RecordCallbackHandoff(false);
#endif
                _resetRequested = true;
                return;
            }

            _completion = completion;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            KiwiFaceGeometryP3DDiagnostics.RecordCallbackHandoff(true);
#endif
        }
    }

    private void RequestGraphReset()
    {
        lock (_sync)
        {
            if (!_shutdownRequested)
            {
                _resetRequested = true;
            }
        }
    }

    [AOT.MonoPInvokeCallback(
        typeof(CalculatorGraph.NativePacketCallback))]
    private static StatusArgs OnNativeOutput(
        IntPtr graphPtr,
        int streamId,
        IntPtr packetPtr)
    {
        CallbackRegistration registration;
        GraphRun run;
        lock (CallbackRegistryLock)
        {
            if (!CallbackRegistry.TryGetValue(
                    streamId,
                    out registration))
            {
                return StatusArgs.NotFound(
                    "FaceGeometry callback registration is retired.");
            }

            run = registration.run;
            if (!run.TryEnterCallbackLifetime())
            {
                return StatusArgs.NotFound(
                    "FaceGeometry callback roots are released.");
            }
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        KiwiFaceGeometryP3DDiagnostics.RecordCallbackEnter(
            run.IsResultPublicationAccepting());
#endif

        try
        {
            if (!run.IsResultPublicationAccepting())
            {
                return StatusArgs.Ok();
            }

            registration.owner.HandleNativeOutput(
                run,
                graphPtr,
                packetPtr);
            return StatusArgs.Ok();
        }
        catch (Exception exception)
        {
            registration.owner.RequestGraphReset();
            return StatusArgs.Internal(exception.ToString());
        }
        finally
        {
            bool shouldFinalize = run.ExitCallbackLifetime();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            KiwiFaceGeometryP3DDiagnostics.RecordCallbackExit();
#endif
            if (shouldFinalize)
            {
                registration.owner.TryFinalizeDestroyedRun(run);
            }
        }
    }

    private void RegisterCallback(GraphRun run)
    {
        lock (CallbackRegistryLock)
        {
            CallbackRegistry.Add(
                run.streamId,
                new CallbackRegistration(this, run));
        }
    }

    private static bool TryReleaseCallbackRoots(GraphRun run)
    {
        lock (CallbackRegistryLock)
        {
            if (!CallbackRegistry.TryGetValue(
                    run.streamId,
                    out CallbackRegistration registration))
            {
                return run.AreCallbackRootsReleased();
            }

            if (!ReferenceEquals(registration.run, run))
            {
                return false;
            }

            if (!run.TryMarkCallbackRootsReleased())
            {
                return false;
            }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            run.CaptureLifecycleForDiagnostics(
                out bool diagnosticGraphAlive,
                out int diagnosticActiveCallbacks);
            KiwiFaceGeometryP3DDiagnostics.RecordCallbackRootsReleased(
                diagnosticGraphAlive,
                diagnosticActiveCallbacks);
#endif
            CallbackRegistry.Remove(run.streamId);
            return true;
        }
    }

    private static int NextStreamId()
    {
        int streamId = Interlocked.Increment(ref _nextStreamId);
        if (streamId <= 0)
        {
            throw new InvalidOperationException(
                "FaceGeometry callback stream id is exhausted.");
        }
        return streamId;
    }

    private byte[] LoadMetadataOnce()
    {
        lock (_sync)
        {
            if (_metadataBytes != null)
            {
                return _metadataBytes;
            }
        }

        byte[] bundleBytes = File.ReadAllBytes(
            _faceLandmarkerBundlePath);
        RequireSha256(bundleBytes, ExpectedBundleSha256, "bundle");

        byte[] metadataBytes;
        using (var bundleStream = new MemoryStream(bundleBytes, false))
        using (var archive = new ZipArchive(
            bundleStream,
            ZipArchiveMode.Read,
            false))
        {
            ZipArchiveEntry entry = archive.GetEntry(MetadataEntryName);
            if (entry == null)
            {
                throw new InvalidDataException(
                    "FaceGeometry metadata entry is absent.");
            }

            using (Stream entryStream = entry.Open())
            using (var metadataStream = new MemoryStream())
            {
                entryStream.CopyTo(metadataStream);
                metadataBytes = metadataStream.ToArray();
            }
        }

        RequireSha256(
            metadataBytes,
            ExpectedMetadataSha256,
            "metadata");

        lock (_sync)
        {
            if (_metadataBytes == null)
            {
                _metadataBytes = metadataBytes;
            }
            return _metadataBytes;
        }
    }

    private static CalculatorGraphConfig BuildGraphConfig(
        byte[] metadataBytes)
    {
        // External one-in-flight admission is the only backlog owner. No graph
        // queue override and no FlowLimiter are introduced.
        var config = new CalculatorGraphConfig
        {
            NumThreads = 1
        };

        config.InputStream.Add("face_landmarks");
        config.InputSidePacket.Add("dimension_image");
        config.OutputStream.Add("face_geometry");

        var dimensionAtTick =
            new CalculatorGraphConfig.Types.Node
            {
                Calculator = "SidePacketToStreamCalculator"
            };
        dimensionAtTick.InputStream.Add(
            "TICK:face_landmarks");
        dimensionAtTick.InputSidePacket.Add(
            "dimension_image");
        dimensionAtTick.OutputStream.Add(
            "AT_TICK:dimension_image_at_tick");
        config.Node.Add(dimensionAtTick);

        var imageProperties =
            new CalculatorGraphConfig.Types.Node
            {
                Calculator = "ImagePropertiesCalculator"
            };
        imageProperties.InputStream.Add(
            "IMAGE:dimension_image_at_tick");
        imageProperties.OutputStream.Add(
            "SIZE:image_size");
        config.Node.Add(imageProperties);

        var environment =
            new CalculatorGraphConfig.Types.Node
            {
                Calculator =
                    "mediapipe.tasks.vision.face_geometry." +
                    "FaceGeometryEnvGeneratorCalculator",
                Options = CalculatorOptions.Parser.ParseFrom(
                    WrapExtension(
                        512499201u,
                        BuildEnvironmentGeneratorOptions()))
            };
        environment.OutputSidePacket.Add(
            "ENVIRONMENT:environment");
        config.Node.Add(environment);

        var geometryOptions =
            new FaceGeometryPipelineCalculatorOptions
            {
                MetadataFile = new ExternalFile
                {
                    FileContent = ByteString.CopyFrom(metadataBytes)
                }
            };

        var geometry =
            new CalculatorGraphConfig.Types.Node
            {
                Calculator =
                    "mediapipe.tasks.vision.face_geometry." +
                    "FaceGeometryPipelineCalculator",
                Options = CalculatorOptions.Parser.ParseFrom(
                    WrapExtension(
                        512499200u,
                        geometryOptions.ToByteArray()))
            };
        geometry.InputStream.Add("IMAGE_SIZE:image_size");
        geometry.InputStream.Add(
            "FACE_LANDMARKS:face_landmarks");
        geometry.InputSidePacket.Add(
            "ENVIRONMENT:environment");
        geometry.OutputStream.Add(
            "FACE_GEOMETRY:face_geometry");
        config.Node.Add(geometry);

        return config;
    }

    private static byte[] BuildEnvironmentGeneratorOptions()
    {
        var camera = new List<byte>();
        WriteFixed32Field(
            camera,
            1,
            (float)VerticalFieldOfViewDegrees);
        WriteFixed32Field(camera, 2, (float)Near);
        WriteFixed32Field(camera, 3, (float)Far);

        var environment = new List<byte>();
        // enum 2 = TOP_LEFT_CORNER in the exact v0.10.22 Environment proto.
        WriteVarintField(environment, 1, 2);
        WriteLengthDelimitedField(
            environment,
            2,
            camera.ToArray());

        var options = new List<byte>();
        WriteLengthDelimitedField(
            options,
            1,
            environment.ToArray());
        return options.ToArray();
    }

    private static byte[] WrapExtension(
        uint fieldNumber,
        byte[] payload)
    {
        var bytes = new List<byte>();
        WriteVarint(
            bytes,
            ((ulong)fieldNumber << 3) | 2u);
        WriteVarint(bytes, (ulong)payload.Length);
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    private static void WriteVarintField(
        List<byte> bytes,
        int field,
        ulong value)
    {
        WriteVarint(
            bytes,
            ((ulong)field << 3) | 0u);
        WriteVarint(bytes, value);
    }

    private static void WriteFixed32Field(
        List<byte> bytes,
        int field,
        float value)
    {
        WriteVarint(
            bytes,
            ((ulong)field << 3) | 5u);

        byte[] payload = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(payload);
        }
        bytes.AddRange(payload);
    }

    private static void WriteLengthDelimitedField(
        List<byte> bytes,
        int field,
        byte[] payload)
    {
        WriteVarint(
            bytes,
            ((ulong)field << 3) | 2u);
        WriteVarint(bytes, (ulong)payload.Length);
        bytes.AddRange(payload);
    }

    private static void WriteVarint(
        List<byte> bytes,
        ulong value)
    {
        while (value >= 0x80)
        {
            bytes.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
    }

    private static bool CanCreateTransaction(
        ulong frameId,
        long sourceHostTicks,
        int cameraGeneration,
        int trackingSessionGeneration,
        int providerGeneration,
        int modelGeneration,
        int semanticFrameWidth,
        int semanticFrameHeight,
        Vector3[] landmarks)
    {
        if (
            frameId == 0UL ||
            frameId > (ulong)long.MaxValue ||
            sourceHostTicks <= 0L ||
            cameraGeneration <= 0 ||
            trackingSessionGeneration <= 0 ||
            providerGeneration <= 0 ||
            modelGeneration <= 0 ||
            semanticFrameWidth <= 0 ||
            semanticFrameHeight <= 0 ||
            landmarks == null ||
            landmarks.Length < LandmarkCount)
        {
            return false;
        }

        for (int i = 0; i < LandmarkCount; ++i)
        {
            Vector3 landmark = landmarks[i];
            if (
                !Finite(landmark.x) ||
                !Finite(landmark.y) ||
                !Finite(landmark.z))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTransactionCurrent(
        InputTransaction transaction,
        int currentSemanticFrameWidth,
        int currentSemanticFrameHeight)
    {
        return
            transaction != null &&
            transaction.frameId > 0UL &&
            transaction.frameId <= (ulong)long.MaxValue &&
            transaction.sourceHostTicks > 0L &&
            transaction.backend == KiwiTrackingBackend.InferenceEngine &&
            transaction.cameraGeneration ==
                KiwiRuntimeGenerationContext.CameraGeneration &&
            transaction.trackingSessionGeneration ==
                KiwiRuntimeGenerationContext.TrackingSessionGeneration &&
            transaction.providerGeneration ==
                KiwiRuntimeGenerationContext.ProviderGeneration &&
            transaction.modelGeneration ==
                KiwiRuntimeGenerationContext.ModelGeneration &&
            transaction.semanticFrameWidth ==
                currentSemanticFrameWidth &&
            transaction.semanticFrameHeight ==
                currentSemanticFrameHeight;
    }

    private static void RequireSha256(
        byte[] bytes,
        string expected,
        string label)
    {
        string actual;
        using (SHA256 sha = SHA256.Create())
        {
            actual = BitConverter.ToString(
                sha.ComputeHash(bytes)).Replace("-", string.Empty);
        }

        if (!string.Equals(
                actual,
                expected,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "FaceGeometry " + label + " identity mismatch.");
        }
    }

    private static bool Finite(float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
    }
}
