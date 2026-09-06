using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

// Opt-in diagnostic. No inference scheduling, input access, or tracking publication.
[DefaultExecutionOrder(38000)]
internal sealed class KiwiAsyncProducerTailSnapshotV44_55_31 : MonoBehaviour
{
    internal const int Words = 1405, Header = 8, SlotCount = 6, MaxCandidates = 64, MaxPairs = 192;
    internal const int Magic = 0x4B333150;
    private const string Flag = "KIWI_V44_55_31_PRODUCER_TAIL_SNAPSHOT";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static KiwiAsyncProducerTailSnapshotV44_55_31 instance;
    // A rooted bounded slot array survives component destruction. An unresolved GPU resource is
    // NEVER released by OnDisable/OnDestroy/OnApplicationQuit. Resolved callbacks retire it;
    // abandoned requests remain bounded and owned until OS process teardown.
    private static readonly Slot[] slots = new Slot[SlotCount];
    private static bool bootstrapped;
    private readonly List<Capture> candidates = new List<Capture>(MaxCandidates);
    private readonly Dictionary<int, Capture> paired = new Dictionary<int, Capture>(MaxPairs);
    private readonly Dictionary<int, Row> waiting = new Dictionary<int, Row>(MaxPairs);
    // v27 releases its observer-owned float payload arrays immediately after parity calculation.
    // Preserve only bitwise observer-owned copies across that release-to-finalization boundary.
    private readonly Dictionary<int, PayloadBits> payloadSnapshots = new Dictionary<int, PayloadBits>(MaxPairs);
    private readonly HashSet<int> finalRecords = new HashSet<int>();
    private readonly Dictionary<int, long> lastLaneTicks = new Dictionary<int, long>();
    private readonly Dictionary<string, FieldInfo> fieldCache = new Dictionary<string, FieldInfo>();
    private readonly object completionGate = new object();
    private SynchronizationContext mainContext;
    private int mainThread;
    private ComputeShader shader;
    private int kernel, nextToken, faults, busySkips, identityMismatch, duplicate, outOfOrder, missingPair;
    private int completedRequests, issuedRequests, submittedCopies, headerErrors, readbackErrors, pairedCount;
    private int capturedPairs, eligiblePairs, pendingAtShutdown, firstFrame, payloadCaptureCount, payloadMissing;
    private bool probe, stopped, written;
    private double started, doneSince = -1;
    private object v20, v27;
    private string directory;
    private StreamWriter raw;
    private sealed class Slot
    {
        internal ComputeBuffer Buffer;
        internal Capture Capture;
        internal bool Busy, Retire, Submitted, CompletionReady;
        internal string Error;
        internal int[] Result, HeaderResult;
        internal bool Resolved, RequestError, HeaderError;
    }
    private sealed class Capture
    {
        internal int Token, LaneIndex, Frame, Anchor, Epoch, Tracker, Camera, Session;
        internal long Source, Begin, Started, Readback;
        internal int ReadbackFrame, Minimum;
        internal object Lane, Worker, Output, Trace;
        internal int[] Matrix, Snapshot, HeaderWords;
        internal bool Done, Failed, Bound, PendingCaptured;
    }
    private sealed class PayloadBits
    {
        internal int[] Actual, Reference, Mode2;
    }
    [Serializable] private sealed class Row
    {
        public int recordIndex, pairToken, observerToken, laneIndex, logicalWords = Words;
        public string sequence;
        public long sourceHostTicks, scheduleBeginHostTicks, startedHostTicks, readbackRequestHostTicks;
        public int cameraGeneration, trackingSessionGeneration, trackerGeneration, anchorRevision, externalAnchorEpoch;
        public int readbackRequestFrame, minimumPresenceBits, laneIdentityToken, workerIdentityToken, pendingOutputIdentityToken;
        public int[] cropMatrixBits, gpuHeader, snapshot, actual, reference, mode2;
        public bool v27Eligible, valid, snapshotEqualsActual;
        public string actualClassification, producerClassification, decision, failure;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (bootstrapped || !Debug.isDebugBuild ||
            Environment.GetEnvironmentVariable("KIWI_V44_55_31_COLLECTION") != "1") return;
        bootstrapped = true;
        var host = new GameObject("[Kiwi] v44.55.30 Producer Tail Snapshot");
        DontDestroyOnLoad(host);
        instance = host.AddComponent<KiwiAsyncProducerTailSnapshotV44_55_31>();
    }
    private void Awake()
    {
        probe = Environment.GetEnvironmentVariable(Flag) == "1";
        started = Time.realtimeSinceStartupAsDouble;
        firstFrame = Time.frameCount;
        mainContext = SynchronizationContext.Current;
        mainThread = Thread.CurrentThread.ManagedThreadId;
        directory = Environment.GetEnvironmentVariable("KIWI_V44_55_31_EVIDENCE_DIR");
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
                throw new InvalidOperationException("Absolute evidence directory required");
            Directory.CreateDirectory(directory);
            raw = new StreamWriter(new FileStream(Path.Combine(directory, "producer_pairs.jsonl"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            raw.AutoFlush = true;
            if (probe)
            {
                if (mainContext == null) throw new InvalidOperationException("Main synchronization context required");
                if (Environment.GetEnvironmentVariable("KIWI_INFERENCE_ASYNC_COMPUTE_PROBE") != "1" ||
                    SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 ||
                    !SystemInfo.supportsAsyncCompute || !SystemInfo.supportsAsyncGPUReadback)
                    throw new InvalidOperationException("Async DX12/readback capability required");
                shader = Resources.Load<ComputeShader>("KiwiProducerTailSnapshotV44_55_31");
                if (shader == null) throw new InvalidOperationException("Copy shader missing");
                kernel = shader.FindKernel("CopyPackedOutput");
                for (int i = 0; i < SlotCount; i++)
                    slots[i] = new Slot { Buffer = new ComputeBuffer(Words + Header, sizeof(uint)) };
            }
            Debug.Log("[KiwiV31] START probe=" + (probe ? 1 : 0) + " slots=6 words=1405 header=8 performanceAuthority=0");
        }
        catch (Exception e) { Fault("INIT " + e.Message); stopped = true; }
    }
    private object Get(object target, string name)
    {
        if (target == null) throw new InvalidOperationException("Null field target: " + name);
        string key = target.GetType().FullName + "." + name;
        if (!fieldCache.TryGetValue(key, out var field))
        {
            field = target.GetType().GetField(name, Fields);
            if (field == null) throw new MissingFieldException(key);
            fieldCache.Add(key, field);
        }
        return field.GetValue(target);
    }
    private T Read<T>(object target, string name) { return (T)Get(target, name); }
    private void Fault(string reason) { faults++; Debug.LogError("[KiwiV31] FAULT " + reason); }
    private static int[] Bits(float[] values)
    {
        if (values == null || values.Length != Words) throw new InvalidOperationException("Payload length");
        var result = new int[Words];
        Buffer.BlockCopy(values, 0, result, 0, Words * 4);
        return result;
    }
    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
    internal static void CapturePayloadsBeforeRelease(object pair)
    {
        var x = instance; if (x == null || x.stopped || x.written) return;
        try
        {
            int index = x.Read<int>(pair, "RecordIndex");
            if (x.payloadSnapshots.Count >= MaxPairs || x.payloadSnapshots.ContainsKey(index))
                throw new InvalidOperationException("Duplicate/capacity payload snapshot");
            x.payloadSnapshots.Add(index, new PayloadBits {
                Actual = Bits(x.Read<float[]>(pair, "ActualPayload")),
                Reference = Bits(x.Read<float[]>(pair, "ReferencePayload")),
                Mode2 = Bits(x.Read<float[]>(pair, "Mode2Payload"))
            });
            x.payloadCaptureCount++;
        }
        catch (Exception e) { x.Fault("PAYLOAD_CAPTURE " + e.Message); }
    }
    private static int[] MatrixBits(Matrix4x4 m)
    {
        var b = new int[16]; for (int i = 0; i < 16; i++) b[i] = BitConverter.SingleToInt32Bits(m[i]); return b;
    }
    private static bool Equal(int[] a, int[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    internal static string Classify(int[] value, int[] reference, int[] mode2, string prefix)
    {
        bool r = Equal(value, reference), m = Equal(value, mode2);
        return prefix + (r ? (m ? "BOTH" : "REF_ONLY") : (m ? "MODE2_ONLY" : "NEITHER"));
    }
    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
    internal static void Append(CommandBuffer cb, Worker worker, object lane, object tracker, long source, Matrix4x4 crop)
    {
        var x = instance; if (x == null || !x.probe || x.stopped || x.written) return;
        try { x.AppendCore(cb, worker, lane, tracker, source, crop); }
        catch (Exception e) { x.Fault("APPEND " + e.Message); }
    }
    private void AppendCore(CommandBuffer cb, Worker worker, object lane, object tracker, long source, Matrix4x4 crop)
    {
        // v20 arms its exact native snapshot in LateUpdate before the matching Production schedule.
        // Capture candidates while that request is active; never invoke any v20 method or alter cadence.
        if (v20 == null || !Read<bool>(v20, "_measuring") ||
            !Read<bool>(v20, "_snapshotRequestActive") || Read<bool>(v20, "_pairPending") ||
            Read<bool>(v20, "_reportWritten")) return;
        Slot slot = null;
        foreach (var s in slots) if (s != null && !s.Busy && !s.Retire) { slot = s; break; }
        if (slot == null) { busySkips++; return; }
        if (candidates.Count >= MaxCandidates) { busySkips++; return; }
        Tensor output = worker.PeekOutput(0);
        var data = output == null ? null : output.dataOnBackend as ComputeTensorData;
        if (output == null || output.count != Words || data == null || data.buffer == null ||
            data.buffer.stride != 4 || data.buffer.count < Words)
            throw new InvalidOperationException("Existing output backing shape/type");
        int laneIndex = Array.IndexOf((Array)Get(tracker, "_lanes"), lane);
        if (laneIndex < 0 || laneIndex >= 3 || source <= 0) throw new InvalidOperationException("Lane/source identity");
        if (lastLaneTicks.TryGetValue(laneIndex, out long previous) && source <= previous)
        { outOfOrder++; throw new InvalidOperationException("Duplicate/out-of-order source in lane"); }
        lastLaneTicks[laneIndex] = source;
        var generation = KiwiRuntimeGenerationContext.Capture();
        var capture = new Capture {
            Token = checked(++nextToken), LaneIndex = laneIndex, Frame = Time.frameCount,
            Source = source, Lane = lane, Worker = worker, Output = output, Matrix = MatrixBits(crop),
            Anchor = Read<int>(tracker, "_anchorRevision"), Epoch = Read<int>(tracker, "_externalAnchorEpoch"),
            Tracker = Read<int>(tracker, "_trackerGeneration"),
            Camera = generation.cameraGeneration, Session = generation.trackingSessionGeneration
        };
        slot.Capture = capture; slot.Busy = true; // acquired before recording; exception => retain, fail closed
        candidates.Add(capture);
        cb.SetComputeBufferParam(shader, kernel, "_ProductionOutput", data.buffer);
        cb.SetComputeBufferParam(shader, kernel, "_ObserverSnapshot", slot.Buffer);
        cb.SetComputeIntParam(shader, "_ObserverToken", capture.Token);
        cb.SetComputeIntParam(shader, "_Lane", laneIndex);
        cb.SetComputeIntParam(shader, "_SourceLow", unchecked((int)source));
        cb.SetComputeIntParam(shader, "_SourceHigh", unchecked((int)(source >> 32)));
        cb.DispatchCompute(shader, kernel, (Words + 63) / 64, 1, 1);
        submittedCopies++;
    }
    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
    internal static void AfterPending(object lane)
    {
        var x = instance; if (x == null || !x.probe || x.stopped) return;
        try
        {
            foreach (var slot in slots)
            {
                if (slot == null || !slot.Busy || slot.Submitted || !ReferenceEquals(slot.Capture.Lane, lane)) continue;
                var c = slot.Capture;
                if (!ReferenceEquals(c.Output, x.Get(lane, "pendingOutput")) ||
                    c.Source != x.Read<long>(lane, "pendingSourceHostTicks") ||
                    c.Anchor != x.Read<int>(lane, "pendingAnchorRevision") ||
                    c.Epoch != x.Read<int>(lane, "pendingExternalAnchorEpoch") ||
                    c.Tracker != x.Read<int>(lane, "pendingTrackerGeneration") ||
                    c.Camera != x.Read<int>(lane, "pendingCameraGeneration") ||
                    c.Session != x.Read<int>(lane, "pendingTrackingSessionGeneration") ||
                    !Equal(c.Matrix, MatrixBits(x.Read<Matrix4x4>(lane, "pendingCropMatrix"))))
                { x.identityMismatch++; throw new InvalidOperationException("Pre/post submit identity mismatch"); }
                c.Begin = x.Read<long>(lane, "pendingScheduleBeginHostTicks");
                c.Started = x.Read<long>(lane, "pendingStartedHostTicks");
                c.Readback = x.Read<long>(lane, "pendingReadbackRequestHostTicks");
                c.ReadbackFrame = x.Read<int>(lane, "pendingReadbackRequestUnityFrame");
                c.Minimum = BitConverter.SingleToInt32Bits(x.Read<float>(lane, "pendingMinimumPresence"));
                c.PendingCaptured = true;
                slot.Submitted = true;
                x.issuedRequests++;
                AsyncGPUReadback.Request(slot.Buffer, (Words + Header) * 4, 0, request => x.OnReadback(slot, c, request));
                return;
            }
        }
        catch (Exception e) { x.Fault("AFTER_PENDING " + e.Message); }
    }
    private void OnReadback(Slot slot, Capture c, AsyncGPUReadbackRequest request)
    {
        // Copy callback data immediately; never assume the callback is on the Unity main thread.
        // All Unity resource disposal and lifecycle state mutation is marshalled to captured context.
        int[] result = null, header = null; string error = null;
        bool resolved = request.done, requestError = request.hasError, headerError = false;
        try
        {
            if (!resolved || requestError) throw new InvalidOperationException("Readback not successful");
            var data = request.GetData<uint>();
            if (data.Length != Words + Header || data[0] != Magic || data[1] != 31 ||
                data[2] != (uint)c.Token || data[3] != (uint)c.LaneIndex || data[4] != Words ||
                data[5] != unchecked((uint)c.Source) || data[6] != unchecked((uint)(c.Source >> 32)) ||
                data[7] != ((uint)c.Token ^ 0xA55A31u))
            { headerError = true; throw new InvalidOperationException("Snapshot header/token/size"); }
            result = new int[Words];
            header = new int[Header];
            for (int i = 0; i < Header; i++) header[i] = unchecked((int)data[i]);
            for (int i = 0; i < Words; i++) result[i] = unchecked((int)data[Header + i]);
        }
        catch (Exception e) { error = e.Message; }
        lock (completionGate)
        {
            slot.Result = result; slot.HeaderResult = header; slot.Error = error; slot.Resolved = resolved;
            slot.RequestError = requestError; slot.HeaderError = headerError; slot.CompletionReady = true;
        }
        // At most one post per fixed slot. If context is no longer pumped during process exit,
        // static ownership retains the unresolved slot; it is never prematurely released.
        try { mainContext.Post(_ => DrainCompletions(), null); }
        catch { /* Rejected post: keep the fixed slot busy and rooted until process teardown. */ }
    }
    private void DrainCompletions()
    {
        if (Thread.CurrentThread.ManagedThreadId != mainThread) return; // retain, never release off-thread
        lock (completionGate)
        {
            foreach (var slot in slots)
            {
                if (slot == null || !slot.CompletionReady) continue;
                slot.CompletionReady = false;
                var c = slot.Capture;
                if (slot.RequestError) readbackErrors++;
                if (slot.HeaderError) headerErrors++;
                c.Snapshot = slot.Result; c.HeaderWords = slot.HeaderResult;
                c.Failed = slot.Error != null; c.Done = slot.Resolved;
                if (c.Failed) Fault("READBACK " + slot.Error);
                slot.Result = null; slot.HeaderResult = null;
                if (slot.Resolved)
                {
                    completedRequests++;
                    slot.Busy = false; slot.Submitted = false; slot.Capture = null;
                    if (slot.Retire && slot.Buffer != null) { slot.Buffer.Release(); slot.Buffer = null; }
                }
            }
        }
    }
    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
    internal static void BindTrace(object trace)
    {
        var x = instance; if (x == null || !x.probe || x.stopped || x.written) return;
        try
        {
            int index = x.Read<int>(trace, "RecordIndex");
            if (x.paired.ContainsKey(index)) { x.duplicate++; throw new InvalidOperationException("Duplicate trace"); }
            Capture found = null; int count = 0;
            foreach (var c in x.candidates)
                if (ReferenceEquals(c.Lane, x.Get(trace, "Lane")) &&
                    c.Source == x.Read<long>(trace, "SourceHostTicks") &&
                    c.Started == x.Read<long>(trace, "StartedHostTicks"))
                { found = c; count++; }
            if (count != 1 || found == null || !found.PendingCaptured)
            { x.missingPair++; throw new InvalidOperationException("Missing/ambiguous producer candidate"); }
            if (!ReferenceEquals(found.Worker, x.Get(trace, "Worker")) ||
                !ReferenceEquals(found.Output, x.Get(trace, "PendingOutput")) ||
                found.Begin != x.Read<long>(trace, "ScheduleBeginHostTicks") ||
                found.Readback != x.Read<long>(trace, "ReadbackRequestHostTicks") ||
                found.LaneIndex != x.Read<int>(trace, "LaneIndex") ||
                found.Camera != x.Read<int>(trace, "CameraGeneration") ||
                found.Session != x.Read<int>(trace, "TrackingSessionGeneration") ||
                found.Tracker != x.Read<int>(trace, "TrackerGeneration") ||
                found.Anchor != x.Read<int>(trace, "AnchorRevision") || found.Epoch != x.Read<int>(trace, "ExternalAnchorEpoch") ||
                !Equal(found.Matrix, x.Read<int[]>(trace, "CropMatrixBits")))
            { x.identityMismatch++; throw new InvalidOperationException("Trace sidecar mismatch"); }
            if (x.paired.Count >= MaxPairs) throw new InvalidOperationException("Pair capacity");
            found.Trace = trace; found.Bound = true; x.paired.Add(index, found); x.pairedCount++;
        }
        catch (Exception e) { x.Fault("BIND_TRACE " + e.Message); }
    }
    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
    internal static void RecordFinalPair(object pair)
    {
        var x = instance; if (x == null || x.stopped || x.written) return;
        try { x.RecordCore(pair); } catch (Exception e) { x.Fault("FINAL_PAIR " + e.Message); }
    }
    private void RecordCore(object pair)
    {
        int index = Read<int>(pair, "RecordIndex");
        if (finalRecords.Count >= MaxPairs || !finalRecords.Add(index))
        { duplicate++; throw new InvalidOperationException("Duplicate/capacity final pair"); }
        var row = new Row {
            recordIndex = index, pairToken = Read<int>(pair, "PairToken"), sequence = Get(pair, "Sequence").ToString(),
            laneIndex = Read<int>(pair, "LaneIndex"), sourceHostTicks = Read<long>(pair, "SourceHostTicks"),
            scheduleBeginHostTicks = Read<long>(pair, "ScheduleBeginHostTicks"), startedHostTicks = Read<long>(pair, "StartedHostTicks"),
            readbackRequestHostTicks = Read<long>(pair, "ReadbackRequestHostTicks"),
            readbackRequestFrame = Read<int>(pair, "ReadbackRequestFrame"),
            cameraGeneration = Read<int>(pair, "CameraGeneration"), trackingSessionGeneration = Read<int>(pair, "TrackingSessionGeneration"),
            trackerGeneration = Read<int>(pair, "TrackerGeneration"), anchorRevision = Read<int>(pair, "AnchorRevision"),
            externalAnchorEpoch = Read<int>(pair, "ExternalAnchorEpoch"), minimumPresenceBits = Read<int>(pair, "MinimumPresenceBits"),
            laneIdentityToken = Read<int>(pair, "LaneIdentityToken"), workerIdentityToken = Read<int>(pair, "WorkerIdentityToken"),
            pendingOutputIdentityToken = Read<int>(pair, "PendingOutputIdentityToken"),
            cropMatrixBits = (int[])Read<int[]>(pair, "CropMatrixBits").Clone(),
            v27Eligible = Read<bool>(pair, "Eligible"), actualClassification = Read<string>(pair, "Classification")
        };
        if (!row.v27Eligible) { payloadSnapshots.Remove(index); row.failure = "V27_INELIGIBLE"; Emit(row, null); return; }
        if (!payloadSnapshots.TryGetValue(index, out var payload))
        { payloadMissing++; throw new InvalidOperationException("Pre-release payload snapshot missing"); }
        payloadSnapshots.Remove(index);
        row.actual = payload.Actual; row.reference = payload.Reference; row.mode2 = payload.Mode2;
        if (Classify(row.actual, row.reference, row.mode2, "ACTUAL_EQUALS_") != row.actualClassification)
        { identityMismatch++; throw new InvalidOperationException("Independent Actual classification"); }
        if (!probe) { Emit(row, null); return; }
        if (!paired.TryGetValue(index, out var capture) || !ReferenceEquals(capture.Trace, Get(pair, "Trace")) ||
            capture.Source != row.sourceHostTicks || capture.Started != row.startedHostTicks ||
            capture.Begin != row.scheduleBeginHostTicks || capture.Minimum != row.minimumPresenceBits)
        { missingPair++; row.failure = "EXACT_SNAPSHOT_PAIR_MISSING"; Emit(row, null); return; }
        if (capture.Done) Emit(row, capture); else waiting.Add(index, row);
    }
    private void Emit(Row row, Capture c)
    {
        row.valid = row.v27Eligible && row.failure == null && (!probe || (c != null && c.Done && !c.Failed));
        row.decision = "INCONCLUSIVE";
        if (probe && c != null) { row.observerToken = c.Token; row.snapshot = c.Snapshot; row.gpuHeader = c.HeaderWords; }
        if (row.valid)
        {
            eligiblePairs++;
            if (probe)
            {
                capturedPairs++;
                row.producerClassification = Classify(row.snapshot, row.reference, row.mode2, "PRODUCER_SNAPSHOT_EQUALS_");
                row.snapshotEqualsActual = Equal(row.snapshot, row.actual);
                if (row.producerClassification == "PRODUCER_SNAPSHOT_EQUALS_REF_ONLY" && row.actualClassification == "ACTUAL_EQUALS_NEITHER")
                    row.decision = "DIVERGENCE_AFTER_PRODUCER_TAIL_SUPPORTED";
                else if (row.snapshotEqualsActual && row.actualClassification == "ACTUAL_EQUALS_NEITHER")
                    row.decision = "DIVERGENCE_PRESENT_BY_PRODUCER_TAIL_OR_COMMON_OBSERVATION_PATH";
                else if (!row.snapshotEqualsActual) row.decision = "OUTPUT_OBSERVATION_PATH_DISAGREEMENT_CONFIRMED";
                else row.decision = "NONDISCRIMINATING";
            }
            else { row.producerClassification = "OBSERVER_OFF"; row.decision = "CONTROL_ONLY"; }
        }
        raw.WriteLine(JsonUtility.ToJson(row));
    }
    private void Update()
    {
        if (written) return;
        try
        {
            if (v20 == null) v20 = FindUniqueActiveLoaded<KiwiCommonTensorBackendStageIsolationV44_55_20>();
            if (v27 == null) v27 = FindUniqueActiveLoaded<KiwiActualProductionVsShadowPayloadAuthorityV44_55_27>();
            var ready = new List<int>();
            foreach (var entry in waiting) if (paired[entry.Key].Done) { Emit(entry.Value, paired[entry.Key]); ready.Add(entry.Key); }
            foreach (int key in ready) waiting.Remove(key);
            // Unselected candidate storage is a bounded lookup cache, never a Production frame queue.
            candidates.RemoveAll(c => c.Done && (c.Bound || Time.frameCount - c.Frame > 180));
            if (v27 != null && Read<bool>(v27, "_reportWritten"))
            {
                stopped = true;
                if (doneSince < 0) doneSince = Time.realtimeSinceStartupAsDouble;
                if (Pending() == 0 && waiting.Count == 0)
                {
                    int expected = ((System.Collections.ICollection)Get(v27, "_results")).Count;
                    bool pass = faults == 0 && payloadMissing == 0 && payloadSnapshots.Count == 0 &&
                        finalRecords.Count == expected && eligiblePairs >= 60 &&
                        (!probe || (capturedPairs == eligiblePairs && pairedCount == expected));
                    Finish(pass ? "COMPLETE" : "INCONCLUSIVE");
                    Application.Quit(pass ? 0 : 30);
                }
                else if (Time.realtimeSinceStartupAsDouble - doneSince > 10)
                { Finish("INCONCLUSIVE_PENDING"); Application.Quit(30); }
            }
            if (!written && Time.realtimeSinceStartupAsDouble - started > 270)
            { stopped = true; Finish("TIMEOUT"); Application.Quit(30); }
        }
        catch (Exception e) { Fault("UPDATE " + e.Message); stopped = true; Finish("INCONCLUSIVE"); Application.Quit(30); }
    }
    private static int Pending()
    {
        int count = 0; foreach (var s in slots) if (s != null && s.Busy) count++; return count;
    }
    private static T FindUniqueActiveLoaded<T>() where T : MonoBehaviour
    {
        T found = null; int count = 0;
        foreach (var candidate in Resources.FindObjectsOfTypeAll<T>())
        {
            if (candidate == null || !candidate.isActiveAndEnabled || !candidate.gameObject.activeInHierarchy) continue;
            found = candidate; count++;
        }
        if (count > 1) throw new InvalidOperationException("Ambiguous loaded dependency " + typeof(T).FullName);
        return found;
    }
    private void Retire()
    {
        foreach (var s in slots)
        {
            if (s == null) continue; s.Retire = true;
            if (!s.Busy && s.Buffer != null) { s.Buffer.Release(); s.Buffer = null; }
        }
    }
    private void Finish(string status)
    {
        if (written) return;
        written = true; stopped = true; pendingAtShutdown = Pending();
        raw?.Flush(); raw?.Dispose(); raw = null;
        if (!string.IsNullOrEmpty(directory))
            File.WriteAllLines(Path.Combine(directory, "producer_summary.txt"), new[] {
                "contract=KIWI_V44_55_31_CONTROL_MEASUREMENT_CONTRACT_FIX",
                "status=" + status, "probe=" + (probe ? 1 : 0), "performanceAuthority=0",
                "renderFrames=" + (Time.frameCount - firstFrame),
                "elapsedMs=" + (long)((Time.realtimeSinceStartupAsDouble - started) * 1000),
                "finalPairs=" + finalRecords.Count, "eligiblePairs=" + eligiblePairs, "snapshotPairs=" + capturedPairs,
                "observerFault=" + faults, "identityMismatch=" + identityMismatch, "duplicate=" + duplicate,
                "outOfOrder=" + outOfOrder, "missingPair=" + missingPair, "headerErrors=" + headerErrors,
                "hasError=" + readbackErrors, "busySkip=" + busySkips, "partialCapture=" + waiting.Count,
                "copyCount=" + submittedCopies, "issuedRequests=" + issuedRequests, "completedRequests=" + completedRequests,
                "pendingAtCompletion=" + pendingAtShutdown, "unresolvedRetainedUntilCallbackOrProcessTeardown=" + pendingAtShutdown,
                "payloadCaptureCount=" + payloadCaptureCount, "payloadSnapshotPending=" + payloadSnapshots.Count,
                "payloadMissing=" + payloadMissing
            }, new UTF8Encoding(false));
        Debug.Log("[KiwiV31] " + status + " eligible=" + eligiblePairs + " pending=" + pendingAtShutdown);
        Retire();
    }
    private void OnDisable() { stopped = true; if (!written) Finish("DISABLED_INCONCLUSIVE"); else Retire(); }
    private void OnApplicationQuit() { if (!written) Finish("QUIT_INCONCLUSIVE"); }
    private void OnDestroy() { if (!written) Finish("DESTROYED_INCONCLUSIVE"); }
}
