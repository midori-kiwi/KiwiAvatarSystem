using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Unity.InferenceEngine;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// KiwiAvatarSystem v44.55.26 Production Decode Payload Transaction Closure.
/// Compares the logical 1405 floats copied by v44.55.24 with the exact
/// CPU-readable Tensor produced by Production's existing ReadbackAndClone call.
/// It reuses v44.55.25 transaction identity and never schedules inference,
/// requests readback, waits for the GPU, or writes tracking state.
/// </summary>
[DefaultExecutionOrder(36000)]
internal sealed class KiwiProductionDecodePayloadTransactionTraceV44_55_26
    : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_55_26_PRODUCTION_DECODE_PAYLOAD_TRANSACTION_CLOSURE";
    private const string EnableVariable =
        "KIWI_V44_55_26_PRODUCTION_DECODE_PAYLOAD_TRACE";
    private const int PackedOutputLength = 468 * 3 + 1;
    private const int MinimumCompletedPairs = 60;
    private const int MinimumAnomalousEligiblePairs = 1;
    private const int MaximumBoundedPairs = 192;
    private const double CompletionGraceSeconds = 8.0;

    private static readonly BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static bool _installed;
    private static KiwiProductionDecodePayloadTransactionTraceV44_55_26 _instance;

    private KiwiProductionScheduleTransactionTraceV44_55_25 _v25;
    private KiwiPairBoundShadowOutputSnapshotV44_55_24 _v24;
    private FieldInfo _v25TracesField;
    private FieldInfo _v25ReportWrittenField;
    private FieldInfo _v25ObserverFaultCountField;
    private FieldInfo _v24ReportWrittenField;
    private FieldInfo _v24ObserverFaultCountField;
    private FieldInfo _recordIndexField;
    private FieldInfo _sequenceField;
    private FieldInfo _pairTokenField;
    private FieldInfo _nativeHostTicksField;
    private FieldInfo _managedHostTicksField;
    private FieldInfo _laneStartedHostTicksField;
    private FieldInfo _laneIndexField;
    private FieldInfo _sourceHostTicksField;
    private FieldInfo _scheduleBeginHostTicksField;
    private FieldInfo _startedHostTicksField;
    private FieldInfo _readbackRequestHostTicksField;
    private FieldInfo _readbackRequestFrameField;
    private FieldInfo _anchorRevisionField;
    private FieldInfo _externalAnchorEpochField;
    private FieldInfo _trackerGenerationField;
    private FieldInfo _cameraGenerationField;
    private FieldInfo _trackingSessionGenerationField;
    private FieldInfo _minimumPresenceBitsField;
    private FieldInfo _cropMatrixBitsField;
    private FieldInfo _laneIdentityTokenField;
    private FieldInfo _workerIdentityTokenField;
    private FieldInfo _pendingOutputIdentityTokenField;
    private FieldInfo _boundaryAField;
    private FieldInfo _boundaryBField;
    private FieldInfo _boundaryCField;
    private FieldInfo _boundaryDField;
    private FieldInfo _scheduleMismatchField;
    private FieldInfo _canonicalPublicationMismatchField;
    private FieldInfo _observerInvalidField;
    private FieldInfo _completedField;
    private FieldInfo _anomalousField;
    private FieldInfo _v24ClassificationField;
    private FieldInfo _v25ClassificationField;
    private FieldInfo _canonicalPublicationFrameField;
    private FieldInfo _canonicalPublicationFrameIdField;
    private FieldInfo _canonicalPublicationTimestampField;
    private FieldInfo _canonicalPublicationArrivalHostTicksField;

    private readonly Dictionary<int, PayloadPair> _pairs =
        new Dictionary<int, PayloadPair>();
    private readonly List<PayloadPair> _results =
        new List<PayloadPair>(MaximumBoundedPairs);
    private bool _bound;
    private bool _reportWritten;
    private double _dependencyCompleteSince = -1.0;
    private int _observerFaultCount;
    private int _identityMismatchCount;
    private int _duplicateDecodePayloadCount;
    private int _duplicateSnapshotPayloadCount;
    private int _payloadShapeMismatchCount;
    private int _nonFinitePayloadCount;
    private int _partialCaptureCount;
    private int _noDecodePayloadCoverageCount;
    private int _noCanonicalPublicationCoverageCount;

    private sealed class PayloadPair
    {
        internal object Trace;
        internal int RecordIndex;
        internal ulong Sequence;
        internal int PairToken;
        internal long NativeHostTicks;
        internal long ManagedHostTicks;
        internal long LaneStartedHostTicks;
        internal int LaneIndex;
        internal long SourceHostTicks;
        internal long ScheduleBeginHostTicks;
        internal long StartedHostTicks;
        internal long ReadbackRequestHostTicks;
        internal int ReadbackRequestFrame;
        internal int AnchorRevision;
        internal int ExternalAnchorEpoch;
        internal int TrackerGeneration;
        internal int CameraGeneration;
        internal int TrackingSessionGeneration;
        internal int MinimumPresenceBits;
        internal int[] CropMatrixBits;
        internal int LaneIdentityToken;
        internal int WorkerIdentityToken;
        internal int PendingOutputIdentityToken;
        internal long DecodeArrivalHostTicks;
        internal float[] SnapshotPayload;
        internal float[] DecodePayload;
        internal bool SnapshotCaptured;
        internal bool DecodeCaptured;
        internal bool Compared;
        internal bool Finalized;
        internal bool Eligible;
        internal bool Anomalous;
        internal string V24Classification = "-";
        internal string V25Classification = "-";
        internal string Classification = "PENDING";
        internal string FailureReason = "-";
        internal int CanonicalPublicationFrame = -1;
        internal ulong CanonicalPublicationFrameId;
        internal long CanonicalPublicationTimestamp = -1L;
        internal long CanonicalPublicationArrivalHostTicks;
        internal int ExactCount;
        internal int MismatchCount;
        internal int FirstMismatchIndex = -1;
        internal double MeanAbs;
        internal double MaxAbs;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed || !IsEnabled()) return;
        _installed = true;
        GameObject host = new GameObject(
            "[Kiwi] v44.55.26 Decode Payload Transaction Closure");
        DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.DontSave;
        host.AddComponent<
            KiwiProductionDecodePayloadTransactionTraceV44_55_26>();
    }

    private static bool IsEnabled()
    {
        string value = Environment.GetEnvironmentVariable(EnableVariable);
        return Debug.isDebugBuild &&
            (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            _observerFaultCount++;
            enabled = false;
            return;
        }
        _instance = this;
        Debug.Log(
            "[Kiwi v44.55.26 DecodePayload] WAIT_DEPENDENCY" +
            " contract=" + Contract +
            " observerOnly=1 existingReadbackAndClone=1" +
            " newReadback=0 newWorker=0 repeatedInference=0" +
            " blockingWait=0 performanceAuthority=0");
    }

    private void OnDestroy()
    {
        if (!_reportWritten) MarkIncompleteAndWrite("DESTROYED");
        if (_instance == this) _instance = null;
    }

    private void OnApplicationQuit()
    {
        if (!_reportWritten) MarkIncompleteAndWrite("APPLICATION_QUIT");
    }

    private void Update()
    {
        if (_reportWritten) return;
        if (!_bound)
        {
            TryBindDependencies();
            return;
        }
        try
        {
            TryFinalizePairs(false);
            TryComplete();
        }
        catch (Exception exception)
        {
            RegisterFault(
                "UPDATE_EXCEPTION " + exception.GetType().Name + " " +
                exception.Message);
        }
    }

    [Conditional("DEVELOPMENT_BUILD")]
    [Conditional("UNITY_EDITOR")]
    internal static void RecordDecodePayload(
        long sourceHostTicks,
        Tensor<float> readableOutput,
        long arrivalHostTicks)
    {
        KiwiProductionDecodePayloadTransactionTraceV44_55_26 instance =
            _instance;
        if (instance == null || !instance._bound) return;
        instance.CaptureDecodePayload(
            sourceHostTicks,
            readableOutput,
            arrivalHostTicks);
    }

    [Conditional("DEVELOPMENT_BUILD")]
    [Conditional("UNITY_EDITOR")]
    internal static void RecordPairBoundSnapshot(
        int recordIndex,
        int pairToken,
        ulong sequence,
        long sourceHostTicks,
        string v24Classification,
        float[] productionSnapshot)
    {
        KiwiProductionDecodePayloadTransactionTraceV44_55_26 instance =
            _instance;
        if (instance == null || !instance._bound) return;
        instance.CapturePairBoundSnapshot(
            recordIndex,
            pairToken,
            sequence,
            sourceHostTicks,
            v24Classification,
            productionSnapshot);
    }

    private void TryBindDependencies()
    {
        KiwiProductionScheduleTransactionTraceV44_55_25[] v25Candidates =
            Resources.FindObjectsOfTypeAll<
                KiwiProductionScheduleTransactionTraceV44_55_25>();
        KiwiPairBoundShadowOutputSnapshotV44_55_24[] v24Candidates =
            Resources.FindObjectsOfTypeAll<
                KiwiPairBoundShadowOutputSnapshotV44_55_24>();
        _v25 = FindSingleActive(v25Candidates, "v25");
        _v24 = FindSingleActive(v24Candidates, "v24");
        if (_v25 == null || _v24 == null) return;

        Type v25Type = _v25.GetType();
        Type traceType = RequiredNestedType(v25Type, "TraceState");
        Type v24Type = _v24.GetType();
        _v25TracesField = RequiredField(v25Type, "_traces");
        _v25ReportWrittenField = RequiredField(v25Type, "_reportWritten");
        _v25ObserverFaultCountField = RequiredField(
            v25Type, "_observerFaultCount");
        _v24ReportWrittenField = RequiredField(v24Type, "_reportWritten");
        _v24ObserverFaultCountField = RequiredField(
            v24Type, "_observerFaultCount");

        _recordIndexField = RequiredField(traceType, "RecordIndex");
        _sequenceField = RequiredField(traceType, "Sequence");
        _pairTokenField = RequiredField(traceType, "PairToken");
        _nativeHostTicksField = RequiredField(traceType, "NativeHostTicks");
        _managedHostTicksField = RequiredField(traceType, "ManagedHostTicks");
        _laneStartedHostTicksField = RequiredField(
            traceType, "LaneStartedHostTicks");
        _laneIndexField = RequiredField(traceType, "LaneIndex");
        _sourceHostTicksField = RequiredField(traceType, "SourceHostTicks");
        _scheduleBeginHostTicksField = RequiredField(
            traceType, "ScheduleBeginHostTicks");
        _startedHostTicksField = RequiredField(traceType, "StartedHostTicks");
        _readbackRequestHostTicksField = RequiredField(
            traceType, "ReadbackRequestHostTicks");
        _readbackRequestFrameField = RequiredField(
            traceType, "ReadbackRequestFrame");
        _anchorRevisionField = RequiredField(traceType, "AnchorRevision");
        _externalAnchorEpochField = RequiredField(
            traceType, "ExternalAnchorEpoch");
        _trackerGenerationField = RequiredField(
            traceType, "TrackerGeneration");
        _cameraGenerationField = RequiredField(
            traceType, "CameraGeneration");
        _trackingSessionGenerationField = RequiredField(
            traceType, "TrackingSessionGeneration");
        _minimumPresenceBitsField = RequiredField(
            traceType, "MinimumPresenceBits");
        _cropMatrixBitsField = RequiredField(traceType, "CropMatrixBits");
        _laneIdentityTokenField = RequiredField(
            traceType, "LaneIdentityToken");
        _workerIdentityTokenField = RequiredField(
            traceType, "WorkerIdentityToken");
        _pendingOutputIdentityTokenField = RequiredField(
            traceType, "PendingOutputIdentityToken");
        _boundaryAField = RequiredField(traceType, "BoundaryA");
        _boundaryBField = RequiredField(traceType, "BoundaryB");
        _boundaryCField = RequiredField(traceType, "BoundaryC");
        _boundaryDField = RequiredField(traceType, "BoundaryD");
        _scheduleMismatchField = RequiredField(traceType, "ScheduleMismatch");
        _canonicalPublicationMismatchField = RequiredField(
            traceType, "CanonicalPublicationMismatch");
        _observerInvalidField = RequiredField(traceType, "ObserverInvalid");
        _completedField = RequiredField(traceType, "Completed");
        _anomalousField = RequiredField(traceType, "Anomalous");
        _v24ClassificationField = RequiredField(
            traceType, "V24Classification");
        _v25ClassificationField = RequiredField(traceType, "Classification");
        _canonicalPublicationFrameField = RequiredField(
            traceType, "CanonicalPublicationFrame");
        _canonicalPublicationFrameIdField = RequiredField(
            traceType, "CanonicalPublicationFrameId");
        _canonicalPublicationTimestampField = RequiredField(
            traceType, "CanonicalPublicationTimestamp");
        _canonicalPublicationArrivalHostTicksField = RequiredField(
            traceType, "CanonicalPublicationArrivalHostTicks");
        _bound = true;
        Debug.Log(
            "[Kiwi v44.55.26 DecodePayload] DEPENDENCY_BOUND" +
            " v24SnapshotPayloadCallback=1" +
            " productionReadablePayloadCallback=1" +
            " v25ExactTransactionContract=1 logicalFloatCount=" +
            PackedOutputLength);
    }

    private T FindSingleActive<T>(T[] candidates, string label)
        where T : MonoBehaviour
    {
        T result = null;
        int count = 0;
        if (candidates != null)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                T candidate = candidates[i];
                if (candidate == null || !candidate.isActiveAndEnabled) continue;
                result = candidate;
                count++;
            }
        }
        if (count > 1)
        {
            RegisterFault("AMBIGUOUS_ACTIVE_" + label.ToUpperInvariant());
            return null;
        }
        return count == 1 ? result : null;
    }

    private void CaptureDecodePayload(
        long sourceHostTicks,
        Tensor<float> readableOutput,
        long arrivalHostTicks)
    {
        try
        {
            object trace = FindUniqueTraceBySource(sourceHostTicks, out int matches);
            if (matches == 0) return;
            if (matches != 1 || trace == null)
            {
                _identityMismatchCount++;
                RegisterFault(
                    "DECODE_SOURCE_TRACE_MATCH_COUNT sourceHostTicks=" +
                    sourceHostTicks + " matches=" + matches);
                return;
            }
            PayloadPair pair = GetOrCreatePair(trace);
            if (pair == null) return;
            if (pair.DecodeCaptured)
            {
                _duplicateDecodePayloadCount++;
                RegisterFault(
                    "DUPLICATE_DECODE_PAYLOAD recordIndex=" + pair.RecordIndex);
                return;
            }
            if (readableOutput == null ||
                readableOutput.shape.length != PackedOutputLength)
            {
                _payloadShapeMismatchCount++;
                RegisterFault(
                    "DECODE_PAYLOAD_SHAPE recordIndex=" + pair.RecordIndex);
                return;
            }
            pair.DecodePayload = new float[PackedOutputLength];
            for (int i = 0; i < PackedOutputLength; i++)
            {
                pair.DecodePayload[i] = readableOutput[i];
            }
            pair.DecodeArrivalHostTicks = arrivalHostTicks;
            pair.DecodeCaptured = true;
            TryCompare(pair);
        }
        catch (Exception exception)
        {
            RegisterFault(
                "DECODE_CAPTURE_EXCEPTION " + exception.GetType().Name +
                " " + exception.Message);
        }
    }

    private void CapturePairBoundSnapshot(
        int recordIndex,
        int pairToken,
        ulong sequence,
        long sourceHostTicks,
        string v24Classification,
        float[] productionSnapshot)
    {
        try
        {
            object trace = FindTraceByRecordIndex(recordIndex);
            if (trace == null)
            {
                _identityMismatchCount++;
                RegisterFault("V24_TRACE_MISSING recordIndex=" + recordIndex);
                return;
            }
            PayloadPair pair = GetOrCreatePair(trace);
            if (pair == null) return;
            if (pair.SnapshotCaptured)
            {
                _duplicateSnapshotPayloadCount++;
                RegisterFault("DUPLICATE_V24_SNAPSHOT recordIndex=" + recordIndex);
                return;
            }
            if (pair.PairToken != pairToken ||
                pair.Sequence != sequence ||
                pair.SourceHostTicks != sourceHostTicks)
            {
                _identityMismatchCount++;
                RegisterFault(
                    "V24_CALLBACK_IDENTITY_MISMATCH recordIndex=" + recordIndex);
                return;
            }
            if (productionSnapshot == null ||
                productionSnapshot.Length != PackedOutputLength)
            {
                _payloadShapeMismatchCount++;
                RegisterFault("V24_PAYLOAD_SHAPE recordIndex=" + recordIndex);
                return;
            }
            pair.SnapshotPayload = new float[PackedOutputLength];
            Array.Copy(
                productionSnapshot,
                pair.SnapshotPayload,
                PackedOutputLength);
            pair.V24Classification = v24Classification ?? "-";
            pair.SnapshotCaptured = true;
            TryCompare(pair);
        }
        catch (Exception exception)
        {
            RegisterFault(
                "SNAPSHOT_CAPTURE_EXCEPTION " + exception.GetType().Name +
                " " + exception.Message);
        }
    }

    private PayloadPair GetOrCreatePair(object trace)
    {
        int recordIndex = ReadInt(trace, _recordIndexField);
        if (_pairs.TryGetValue(recordIndex, out PayloadPair existing))
        {
            if (!TraceIdentityMatches(existing, trace))
            {
                _identityMismatchCount++;
                RegisterFault("TRACE_IDENTITY_CHANGED recordIndex=" + recordIndex);
                return null;
            }
            return existing;
        }
        if (_pairs.Count >= MaximumBoundedPairs)
        {
            RegisterFault("BOUNDED_PAIR_CAPACITY_EXCEEDED");
            return null;
        }
        if (!ReadBool(trace, _boundaryAField) ||
            !ReadBool(trace, _boundaryBField) ||
            !ReadBool(trace, _boundaryCField))
        {
            _identityMismatchCount++;
            RegisterFault(
                "V25_SCHEDULE_BOUNDARY_NOT_CLOSED recordIndex=" + recordIndex);
            return null;
        }
        int[] matrix = _cropMatrixBitsField.GetValue(trace) as int[];
        if (matrix == null || matrix.Length != 16)
        {
            _identityMismatchCount++;
            RegisterFault("CROP_MATRIX_BITS_INVALID recordIndex=" + recordIndex);
            return null;
        }
        PayloadPair pair = new PayloadPair
        {
            Trace = trace,
            RecordIndex = recordIndex,
            Sequence = ReadULong(trace, _sequenceField),
            PairToken = ReadInt(trace, _pairTokenField),
            NativeHostTicks = ReadLong(trace, _nativeHostTicksField),
            ManagedHostTicks = ReadLong(trace, _managedHostTicksField),
            LaneStartedHostTicks = ReadLong(trace, _laneStartedHostTicksField),
            LaneIndex = ReadInt(trace, _laneIndexField),
            SourceHostTicks = ReadLong(trace, _sourceHostTicksField),
            ScheduleBeginHostTicks = ReadLong(
                trace, _scheduleBeginHostTicksField),
            StartedHostTicks = ReadLong(trace, _startedHostTicksField),
            ReadbackRequestHostTicks = ReadLong(
                trace, _readbackRequestHostTicksField),
            ReadbackRequestFrame = ReadInt(trace, _readbackRequestFrameField),
            AnchorRevision = ReadInt(trace, _anchorRevisionField),
            ExternalAnchorEpoch = ReadInt(trace, _externalAnchorEpochField),
            TrackerGeneration = ReadInt(trace, _trackerGenerationField),
            CameraGeneration = ReadInt(trace, _cameraGenerationField),
            TrackingSessionGeneration = ReadInt(
                trace, _trackingSessionGenerationField),
            MinimumPresenceBits = ReadInt(trace, _minimumPresenceBitsField),
            CropMatrixBits = (int[])matrix.Clone(),
            LaneIdentityToken = ReadInt(trace, _laneIdentityTokenField),
            WorkerIdentityToken = ReadInt(trace, _workerIdentityTokenField),
            PendingOutputIdentityToken = ReadInt(
                trace, _pendingOutputIdentityTokenField)
        };
        _pairs.Add(recordIndex, pair);
        return pair;
    }

    private void TryCompare(PayloadPair pair)
    {
        if (pair == null || pair.Compared ||
            !pair.SnapshotCaptured || !pair.DecodeCaptured) return;
        if (pair.SnapshotPayload == null || pair.DecodePayload == null ||
            pair.SnapshotPayload.Length != PackedOutputLength ||
            pair.DecodePayload.Length != PackedOutputLength)
        {
            _partialCaptureCount++;
            RegisterFault("PARTIAL_PAYLOAD_CAPTURE recordIndex=" + pair.RecordIndex);
            return;
        }
        double sum = 0.0;
        double max = 0.0;
        int exact = 0;
        int mismatch = 0;
        int first = -1;
        for (int i = 0; i < PackedOutputLength; i++)
        {
            float a = pair.SnapshotPayload[i];
            float b = pair.DecodePayload[i];
            if (!IsFinite(a) || !IsFinite(b))
            {
                _nonFinitePayloadCount++;
                RegisterFault(
                    "NONFINITE_PAYLOAD recordIndex=" + pair.RecordIndex +
                    " index=" + i);
                return;
            }
            if (BitConverter.SingleToInt32Bits(a) ==
                BitConverter.SingleToInt32Bits(b))
            {
                exact++;
            }
            else
            {
                mismatch++;
                if (first < 0) first = i;
            }
            double delta = Math.Abs((double)a - b);
            sum += delta;
            if (delta > max) max = delta;
        }
        pair.ExactCount = exact;
        pair.MismatchCount = mismatch;
        pair.FirstMismatchIndex = first;
        pair.MeanAbs = sum / PackedOutputLength;
        pair.MaxAbs = max;
        pair.Compared = true;
        pair.SnapshotPayload = null;
        pair.DecodePayload = null;
    }

    private void TryFinalizePairs(bool forceCoverage)
    {
        foreach (KeyValuePair<int, PayloadPair> item in _pairs)
        {
            PayloadPair pair = item.Value;
            if (pair == null || pair.Finalized) continue;
            object trace = FindTraceByRecordIndex(pair.RecordIndex);
            if (trace == null || !ReadBool(trace, _completedField))
            {
                if (forceCoverage)
                {
                    _partialCaptureCount++;
                    pair.Classification = "INVALID_OBSERVER";
                    pair.FailureReason = "V25_TRACE_NOT_COMPLETED";
                    FinishPair(pair);
                }
                continue;
            }
            if (!TraceIdentityMatches(pair, trace) ||
                ReadBool(trace, _scheduleMismatchField) ||
                ReadBool(trace, _canonicalPublicationMismatchField) ||
                ReadBool(trace, _observerInvalidField))
            {
                _identityMismatchCount++;
                pair.Classification = "INVALID_OBSERVER";
                pair.FailureReason = "V25_TRACE_IDENTITY_INVALID";
                FinishPair(pair);
                continue;
            }
            pair.Anomalous = ReadBool(trace, _anomalousField);
            string traceV24 = ReadString(trace, _v24ClassificationField);
            pair.V25Classification = ReadString(trace, _v25ClassificationField);
            if (!pair.SnapshotCaptured || pair.V24Classification != traceV24)
            {
                _identityMismatchCount++;
                pair.Classification = "INVALID_OBSERVER";
                pair.FailureReason = "V24_RESULT_IDENTITY_INVALID";
                FinishPair(pair);
                continue;
            }
            bool canonicalBound = ReadBool(trace, _boundaryDField);
            if (canonicalBound)
            {
                pair.CanonicalPublicationFrame = ReadInt(
                    trace, _canonicalPublicationFrameField);
                pair.CanonicalPublicationFrameId = ReadULong(
                    trace, _canonicalPublicationFrameIdField);
                pair.CanonicalPublicationTimestamp = ReadLong(
                    trace, _canonicalPublicationTimestampField);
                pair.CanonicalPublicationArrivalHostTicks = ReadLong(
                    trace, _canonicalPublicationArrivalHostTicksField);
            }
            if (!pair.DecodeCaptured)
            {
                if (!forceCoverage) continue;
                _noDecodePayloadCoverageCount++;
                pair.Classification = "PARTIAL_COVERAGE";
                pair.FailureReason = "DECODE_PAYLOAD_NOT_OBSERVED";
                FinishPair(pair);
                continue;
            }
            if (!pair.Compared)
            {
                _partialCaptureCount++;
                pair.Classification = "INVALID_OBSERVER";
                pair.FailureReason = "PAYLOAD_COMPARISON_INCOMPLETE";
                FinishPair(pair);
                continue;
            }
            if (!canonicalBound || pair.CanonicalPublicationFrameId == 0UL ||
                pair.CanonicalPublicationArrivalHostTicks <= 0L)
            {
                if (!forceCoverage) continue;
                _noCanonicalPublicationCoverageCount++;
                pair.Classification = "PARTIAL_COVERAGE";
                pair.FailureReason = "CANONICAL_PUBLICATION_NOT_OBSERVED";
                FinishPair(pair);
                continue;
            }
            pair.Eligible = true;
            pair.Classification = pair.MismatchCount == 0 &&
                pair.ExactCount == PackedOutputLength
                    ? "DECODE_PAYLOAD_EXACT"
                    : "DECODE_PAYLOAD_MISMATCH";
            FinishPair(pair);
        }
    }

    private void FinishPair(PayloadPair pair)
    {
        if (pair.Finalized) return;
        pair.Finalized = true;
        pair.SnapshotPayload = null;
        pair.DecodePayload = null;
        _results.Add(pair);
        Debug.Log(
            "[Kiwi v44.55.26 DecodePayload] SAMPLE" +
            " recordIndex=" + pair.RecordIndex +
            " sequence=" + pair.Sequence +
            " pairToken=" + pair.PairToken +
            " anomalous=" + (pair.Anomalous ? 1 : 0) +
            " exactCount=" + pair.ExactCount +
            " mismatchCount=" + pair.MismatchCount +
            " classification=" + pair.Classification);
    }

    private void TryComplete()
    {
        if (!DependenciesComplete()) return;
        if (_dependencyCompleteSince < 0.0)
            _dependencyCompleteSince = Time.realtimeSinceStartupAsDouble;
        TryFinalizePairs(false);
        int expected = TraceCount();
        if (_pairs.Count == expected && CountFinalized() == expected)
        {
            WriteReport("COMPLETE");
            return;
        }
        if (Time.realtimeSinceStartupAsDouble - _dependencyCompleteSince >=
            CompletionGraceSeconds)
        {
            TryFinalizePairs(true);
            if (_pairs.Count != expected)
            {
                _partialCaptureCount += Math.Abs(expected - _pairs.Count);
                RegisterFault(
                    "PAIR_COUNT_MISMATCH expected=" + expected +
                    " observed=" + _pairs.Count);
            }
            WriteReport("COMPLETE_WITH_COVERAGE");
        }
    }

    private bool DependenciesComplete()
    {
        return _bound && ReadBool(_v25, _v25ReportWrittenField) &&
            ReadBool(_v24, _v24ReportWrittenField);
    }

    private void MarkIncompleteAndWrite(string status)
    {
        if (!_bound) RegisterFault("DEPENDENCY_NOT_BOUND");
        else TryFinalizePairs(true);
        WriteReport(status);
    }

    private string DetermineDecision()
    {
        int expected = TraceCount();
        bool invalid = !_bound || !DependenciesComplete() ||
            ReadInt(_v25, _v25ObserverFaultCountField) != 0 ||
            ReadInt(_v24, _v24ObserverFaultCountField) != 0 ||
            _observerFaultCount != 0 || _identityMismatchCount != 0 ||
            _duplicateDecodePayloadCount != 0 ||
            _duplicateSnapshotPayloadCount != 0 ||
            _payloadShapeMismatchCount != 0 || _nonFinitePayloadCount != 0 ||
            _partialCaptureCount != 0 || expected < MinimumCompletedPairs ||
            _pairs.Count != expected || CountFinalized() != expected;
        if (invalid) return "INVALID_OBSERVER";
        if (CountAnomalous() == 0) return "INSUFFICIENT_ANOMALOUS_COVERAGE";
        if (CountEligibleAnomalous() < MinimumAnomalousEligiblePairs)
            return "PARTIAL_COVERAGE";
        if (CountClassification("DECODE_PAYLOAD_MISMATCH") != 0)
            return "DECODE_PAYLOAD_MISMATCH";
        return CountClassification("DECODE_PAYLOAD_EXACT") > 0
            ? "DECODE_PAYLOAD_EXACT"
            : "PARTIAL_COVERAGE";
    }

    private void WriteReport(string status)
    {
        if (_reportWritten) return;
        _reportWritten = true;
        string decision = DetermineDecision();
        string directory = Path.Combine(
            Application.persistentDataPath, "KiwiFrameBottleneck");
        Directory.CreateDirectory(directory);
        string stamp = DateTime.Now.ToString(
            "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string textPath = Path.Combine(
            directory,
            "KiwiProductionDecodePayloadTransactionClosure_v44_55_26_" +
            stamp + ".txt");
        string csvPath = Path.Combine(
            directory,
            "KiwiProductionDecodePayloadTransactionClosure_v44_55_26_" +
            stamp + ".csv");
        List<string> lines = new List<string>();
        lines.Add("KiwiAvatarSystem v44.55.26 Production Decode Payload Transaction Closure");
        lines.Add("contract=" + Contract);
        lines.Add("status=" + status);
        lines.Add("decision=" + decision);
        lines.Add("observerOnly=1");
        lines.Add("developmentDiagnosticOnly=1");
        lines.Add("existingReadbackAndClone=1");
        lines.Add("newReadbackAndClone=0");
        lines.Add("productionInputOracle=0");
        lines.Add("newWorker=0");
        lines.Add("repeatedInference=0");
        lines.Add("newGpuCopy=0");
        lines.Add("blockingWait=0");
        lines.Add("logicalFloatCount=" + PackedOutputLength);
        lines.Add("performanceAuthority=0");
        lines.Add("");
        lines.Add("[COUNTS]");
        lines.Add("dependencyTraceCount=" + TraceCount());
        lines.Add("capturedPairCount=" + _pairs.Count);
        lines.Add("completedPairCount=" + CountFinalized());
        lines.Add("comparedPairCount=" + CountCompared());
        lines.Add("eligiblePairCount=" + CountEligible());
        lines.Add("anomalousPairCount=" + CountAnomalous());
        lines.Add("eligibleAnomalousPairCount=" + CountEligibleAnomalous());
        lines.Add("decodePayloadExactCount=" +
            CountClassification("DECODE_PAYLOAD_EXACT"));
        lines.Add("decodePayloadMismatchCount=" +
            CountClassification("DECODE_PAYLOAD_MISMATCH"));
        lines.Add("partialCoverageCount=" +
            CountClassification("PARTIAL_COVERAGE"));
        lines.Add("noDecodePayloadCoverageCount=" +
            _noDecodePayloadCoverageCount);
        lines.Add("noCanonicalPublicationCoverageCount=" +
            _noCanonicalPublicationCoverageCount);
        lines.Add("observerFaultCount=" + _observerFaultCount);
        lines.Add("identityMismatchCount=" + _identityMismatchCount);
        lines.Add("duplicateDecodePayloadCount=" +
            _duplicateDecodePayloadCount);
        lines.Add("duplicateSnapshotPayloadCount=" +
            _duplicateSnapshotPayloadCount);
        lines.Add("payloadShapeMismatchCount=" + _payloadShapeMismatchCount);
        lines.Add("nonFinitePayloadCount=" + _nonFinitePayloadCount);
        lines.Add("partialCaptureCount=" + _partialCaptureCount);
        lines.Add("pendingAtCompletion=" + CountPending());
        lines.Add("");
        lines.Add("[FIXED_RUNTIME_GATE]");
        lines.Add("minimumCompletedPairCount=" + MinimumCompletedPairs);
        lines.Add("minimumEligibleAnomalousPairCount=" +
            MinimumAnomalousEligiblePairs);
        lines.Add("dependencyCountMustEqualCompletedCount=1");
        lines.Add("observerIntegrityCountersMustBeZero=1");
        lines.Add("decisionAndRowsMustAgree=1");
        lines.Add("");
        lines.Add("[DECISION_RULE]");
        lines.Add("DECODE_PAYLOAD_EXACT=all eligible logical 1405-float comparisons are bitwise exact and at least one anomalous eligible pair exists");
        lines.Add("DECODE_PAYLOAD_MISMATCH=at least one exact-transaction eligible payload differs bitwise");
        lines.Add("PARTIAL_COVERAGE=no eligible anomalous comparison without observer-integrity failure");
        lines.Add("INSUFFICIENT_ANOMALOUS_COVERAGE=dependency produced zero anomalous pairs");
        lines.Add("INVALID_OBSERVER=identity duplicate partial shape finite dependency or count contract failed");
        File.WriteAllLines(textPath, lines.ToArray(), new UTF8Encoding(false));
        WriteCsv(csvPath);
        Debug.Log(
            "[Kiwi v44.55.26 DecodePayload] COMPLETE" +
            " status=" + status + " decision=" + decision +
            " completedPairCount=" + CountFinalized() +
            " anomalousPairCount=" + CountAnomalous() +
            " eligibleAnomalousPairCount=" + CountEligibleAnomalous() +
            " mismatchCount=" +
            CountClassification("DECODE_PAYLOAD_MISMATCH") +
            " observerFaultCount=" + _observerFaultCount +
            " text=" + textPath + " csv=" + csvPath);
    }

    private void WriteCsv(string path)
    {
        StringBuilder builder = new StringBuilder(65536);
        builder.AppendLine(
            "recordIndex,sequence,pairToken,nativeHostTicks,managedHostTicks,laneStartedHostTicks," +
            "laneIndex,sourceHostTicks,scheduleBeginHostTicks,startedHostTicks,readbackRequestHostTicks," +
            "readbackRequestFrame,anchorRevision,externalAnchorEpoch,trackerGeneration,cameraGeneration," +
            "trackingSessionGeneration,minimumPresenceBits,cropMatrixBits,laneIdentityToken," +
            "workerIdentityToken,pendingOutputIdentityToken,decodeArrivalHostTicks," +
            "canonicalPublicationFrame,canonicalPublicationFrameId,canonicalPublicationTimestamp," +
            "canonicalPublicationArrivalHostTicks,v24Classification,v25Classification,anomalous," +
            "snapshotCaptured,decodeCaptured,eligible,exactCount,bitwiseExact,meanAbs,maxAbs," +
            "firstMismatchIndex,mismatchCount,classification,failureReason");
        _results.Sort((left, right) =>
            left.RecordIndex.CompareTo(right.RecordIndex));
        for (int i = 0; i < _results.Count; i++)
        {
            PayloadPair p = _results[i];
            Append(builder, p.RecordIndex); Sep(builder);
            Append(builder, p.Sequence); Sep(builder);
            Append(builder, p.PairToken); Sep(builder);
            Append(builder, p.NativeHostTicks); Sep(builder);
            Append(builder, p.ManagedHostTicks); Sep(builder);
            Append(builder, p.LaneStartedHostTicks); Sep(builder);
            Append(builder, p.LaneIndex); Sep(builder);
            Append(builder, p.SourceHostTicks); Sep(builder);
            Append(builder, p.ScheduleBeginHostTicks); Sep(builder);
            Append(builder, p.StartedHostTicks); Sep(builder);
            Append(builder, p.ReadbackRequestHostTicks); Sep(builder);
            Append(builder, p.ReadbackRequestFrame); Sep(builder);
            Append(builder, p.AnchorRevision); Sep(builder);
            Append(builder, p.ExternalAnchorEpoch); Sep(builder);
            Append(builder, p.TrackerGeneration); Sep(builder);
            Append(builder, p.CameraGeneration); Sep(builder);
            Append(builder, p.TrackingSessionGeneration); Sep(builder);
            builder.Append(p.MinimumPresenceBits.ToString(
                "X8", CultureInfo.InvariantCulture)); Sep(builder);
            builder.Append(MatrixBits(p.CropMatrixBits)); Sep(builder);
            Append(builder, p.LaneIdentityToken); Sep(builder);
            Append(builder, p.WorkerIdentityToken); Sep(builder);
            Append(builder, p.PendingOutputIdentityToken); Sep(builder);
            Append(builder, p.DecodeArrivalHostTicks); Sep(builder);
            Append(builder, p.CanonicalPublicationFrame); Sep(builder);
            Append(builder, p.CanonicalPublicationFrameId); Sep(builder);
            Append(builder, p.CanonicalPublicationTimestamp); Sep(builder);
            Append(builder, p.CanonicalPublicationArrivalHostTicks); Sep(builder);
            builder.Append(Csv(p.V24Classification)); Sep(builder);
            builder.Append(Csv(p.V25Classification)); Sep(builder);
            Append(builder, p.Anomalous ? 1 : 0); Sep(builder);
            Append(builder, p.SnapshotCaptured ? 1 : 0); Sep(builder);
            Append(builder, p.DecodeCaptured ? 1 : 0); Sep(builder);
            Append(builder, p.Eligible ? 1 : 0); Sep(builder);
            Append(builder, p.ExactCount); Sep(builder);
            Append(builder, p.ExactCount == PackedOutputLength &&
                p.MismatchCount == 0 ? 1 : 0); Sep(builder);
            builder.Append(p.MeanAbs.ToString("R", CultureInfo.InvariantCulture));
            Sep(builder);
            builder.Append(p.MaxAbs.ToString("R", CultureInfo.InvariantCulture));
            Sep(builder);
            Append(builder, p.FirstMismatchIndex); Sep(builder);
            Append(builder, p.MismatchCount); Sep(builder);
            builder.Append(Csv(p.Classification)); Sep(builder);
            builder.Append(Csv(p.FailureReason));
            builder.AppendLine();
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private object FindUniqueTraceBySource(long sourceHostTicks, out int matches)
    {
        matches = 0;
        object result = null;
        IDictionary traces = GetTraces();
        if (traces == null) return null;
        foreach (DictionaryEntry entry in traces)
        {
            object trace = entry.Value;
            if (trace != null &&
                ReadLong(trace, _sourceHostTicksField) == sourceHostTicks)
            {
                matches++;
                result = trace;
            }
        }
        return result;
    }

    private object FindTraceByRecordIndex(int recordIndex)
    {
        IDictionary traces = GetTraces();
        return traces != null && traces.Contains(recordIndex)
            ? traces[recordIndex]
            : null;
    }

    private IDictionary GetTraces()
    {
        return _bound ? _v25TracesField.GetValue(_v25) as IDictionary : null;
    }

    private bool TraceIdentityMatches(PayloadPair pair, object trace)
    {
        int[] matrix = _cropMatrixBitsField.GetValue(trace) as int[];
        if (matrix == null || matrix.Length != 16 ||
            pair.CropMatrixBits == null || pair.CropMatrixBits.Length != 16)
            return false;
        for (int i = 0; i < 16; i++)
            if (matrix[i] != pair.CropMatrixBits[i]) return false;
        return
            ReadInt(trace, _recordIndexField) == pair.RecordIndex &&
            ReadULong(trace, _sequenceField) == pair.Sequence &&
            ReadInt(trace, _pairTokenField) == pair.PairToken &&
            ReadLong(trace, _nativeHostTicksField) == pair.NativeHostTicks &&
            ReadLong(trace, _managedHostTicksField) == pair.ManagedHostTicks &&
            ReadLong(trace, _laneStartedHostTicksField) ==
                pair.LaneStartedHostTicks &&
            ReadInt(trace, _laneIndexField) == pair.LaneIndex &&
            ReadLong(trace, _sourceHostTicksField) == pair.SourceHostTicks &&
            ReadLong(trace, _scheduleBeginHostTicksField) ==
                pair.ScheduleBeginHostTicks &&
            ReadLong(trace, _startedHostTicksField) == pair.StartedHostTicks &&
            ReadLong(trace, _readbackRequestHostTicksField) ==
                pair.ReadbackRequestHostTicks &&
            ReadInt(trace, _readbackRequestFrameField) ==
                pair.ReadbackRequestFrame &&
            ReadInt(trace, _anchorRevisionField) == pair.AnchorRevision &&
            ReadInt(trace, _externalAnchorEpochField) ==
                pair.ExternalAnchorEpoch &&
            ReadInt(trace, _trackerGenerationField) ==
                pair.TrackerGeneration &&
            ReadInt(trace, _cameraGenerationField) == pair.CameraGeneration &&
            ReadInt(trace, _trackingSessionGenerationField) ==
                pair.TrackingSessionGeneration &&
            ReadInt(trace, _minimumPresenceBitsField) ==
                pair.MinimumPresenceBits &&
            ReadInt(trace, _laneIdentityTokenField) ==
                pair.LaneIdentityToken &&
            ReadInt(trace, _workerIdentityTokenField) ==
                pair.WorkerIdentityToken &&
            ReadInt(trace, _pendingOutputIdentityTokenField) ==
                pair.PendingOutputIdentityToken;
    }

    private int TraceCount()
    {
        IDictionary traces = GetTraces();
        return traces == null ? 0 : traces.Count;
    }

    private int CountFinalized()
    {
        int count = 0;
        foreach (KeyValuePair<int, PayloadPair> item in _pairs)
            if (item.Value != null && item.Value.Finalized) count++;
        return count;
    }

    private int CountPending()
    {
        return _pairs.Count - CountFinalized();
    }

    private int CountCompared()
    {
        int count = 0;
        foreach (KeyValuePair<int, PayloadPair> item in _pairs)
            if (item.Value != null && item.Value.Compared) count++;
        return count;
    }

    private int CountEligible()
    {
        int count = 0;
        for (int i = 0; i < _results.Count; i++)
            if (_results[i].Eligible) count++;
        return count;
    }

    private int CountAnomalous()
    {
        int count = 0;
        for (int i = 0; i < _results.Count; i++)
            if (_results[i].Anomalous) count++;
        return count;
    }

    private int CountEligibleAnomalous()
    {
        int count = 0;
        for (int i = 0; i < _results.Count; i++)
            if (_results[i].Eligible && _results[i].Anomalous) count++;
        return count;
    }

    private int CountClassification(string classification)
    {
        int count = 0;
        for (int i = 0; i < _results.Count; i++)
            if (_results[i].Classification == classification) count++;
        return count;
    }

    private void RegisterFault(string reason)
    {
        _observerFaultCount++;
        Debug.LogWarning(
            "[Kiwi v44.55.26 DecodePayload] OBSERVER_FAULT " + reason);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static Type RequiredNestedType(Type owner, string name)
    {
        Type type = owner.GetNestedType(name, BindingFlags.NonPublic);
        if (type == null) throw new MissingMemberException(owner.FullName, name);
        return type;
    }

    private static FieldInfo RequiredField(Type owner, string name)
    {
        FieldInfo field = owner.GetField(name, InstanceFlags);
        if (field == null) throw new MissingFieldException(owner.FullName, name);
        return field;
    }

    private static int ReadInt(object owner, FieldInfo field)
    {
        return (int)field.GetValue(owner);
    }

    private static long ReadLong(object owner, FieldInfo field)
    {
        return (long)field.GetValue(owner);
    }

    private static ulong ReadULong(object owner, FieldInfo field)
    {
        return (ulong)field.GetValue(owner);
    }

    private static bool ReadBool(object owner, FieldInfo field)
    {
        return (bool)field.GetValue(owner);
    }

    private static string ReadString(object owner, FieldInfo field)
    {
        return field.GetValue(owner) as string ?? "-";
    }

    private static void Append(StringBuilder builder, int value)
    {
        builder.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void Append(StringBuilder builder, long value)
    {
        builder.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void Append(StringBuilder builder, ulong value)
    {
        builder.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void Sep(StringBuilder builder)
    {
        builder.Append(',');
    }

    private static string MatrixBits(int[] bits)
    {
        if (bits == null || bits.Length != 16) return "-";
        StringBuilder builder = new StringBuilder(143);
        for (int i = 0; i < bits.Length; i++)
        {
            if (i != 0) builder.Append('|');
            builder.Append(bits[i].ToString(
                "X8", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static string Csv(string value)
    {
        string safe = value ?? string.Empty;
        if (safe.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return safe;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }
}
