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
/// KiwiAvatarSystem v44.55.27 Actual Production Decode Payload vs Pair-Bound
/// Shadow Output Authority. Compares the logical 1405 floats actually passed
/// toward Production DecodeReadableOutput with the already pair-bound,
/// observer-owned v44.55.24 REF_FROZEN_GPU and MODE2 B_GPU arrays. It reuses
/// v44.55.25 transaction identity and never schedules inference, requests
/// readback, waits for the GPU, or writes tracking state.
/// </summary>
[DefaultExecutionOrder(36000)]
internal sealed class KiwiActualProductionVsShadowPayloadAuthorityV44_55_27
    : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUTHORITY";
    private const string EnableVariable =
        "KIWI_V44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUDIT";
    private const int PackedOutputLength = 468 * 3 + 1;
    private const int MinimumCompletedPairs = 60;
    private const int MinimumAnomalousEligiblePairs = 3;
    private const int MaximumBoundedPairs = 192;
    private const double CompletionGraceSeconds = 8.0;

    private static readonly BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static bool _installed;
    private static KiwiActualProductionVsShadowPayloadAuthorityV44_55_27 _instance;

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
    private FieldInfo _recordCompletionFrameField;

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
    private int _duplicateShadowPayloadCount;
    private int _arrayAliasCount;
    private int _fieldMappingMismatchCount;
    private int _payloadShapeMismatchCount;
    private int _nonFinitePayloadCount;
    private int _partialCaptureCount;
    private int _noDecodePayloadCoverageCount;
    private int _publishedEligibleCount;
    private int _publishedEligibleAnomalousCount;

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
        internal float[] ActualPayload;
        internal float[] ReferencePayload;
        internal float[] Mode2Payload;
        internal bool ActualCaptured;
        internal bool ShadowCaptured;
        internal bool Compared;
        internal bool Finalized;
        internal bool Eligible;
        internal bool PublishedSubset;
        internal bool Anomalous;
        internal string V24Classification = "-";
        internal string V25Classification = "-";
        internal string Classification = "PENDING";
        internal string FailureReason = "-";
        internal int ShadowSnapshotRecordIndex = -1;
        internal int ReferenceSnapshotFrame = -1;
        internal int Mode2SnapshotFrame = -1;
        internal int CanonicalPublicationFrame = -1;
        internal ulong CanonicalPublicationFrameId;
        internal long CanonicalPublicationTimestamp = -1L;
        internal long CanonicalPublicationArrivalHostTicks;
        internal PayloadParity ActualReference;
        internal PayloadParity ActualMode2;
        internal PayloadParity ReferenceMode2;
    }

    private struct PayloadParity
    {
        internal int ExactCount;
        internal int MismatchCount;
        internal int FirstMismatchIndex;
        internal double MeanAbs;
        internal double MaxAbs;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed || !IsEnabled()) return;
        _installed = true;
        GameObject host = new GameObject(
            "[Kiwi] v44.55.27 Actual Production vs Shadow Payload Authority");
        DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.DontSave;
        host.AddComponent<
            KiwiActualProductionVsShadowPayloadAuthorityV44_55_27>();
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
            "[Kiwi v44.55.27 ActualVsShadow] WAIT_DEPENDENCY" +
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
    internal static void RecordActualDecodePayload(
        long sourceHostTicks,
        Tensor<float> readableOutput,
        long arrivalHostTicks)
    {
        KiwiActualProductionVsShadowPayloadAuthorityV44_55_27 instance =
            _instance;
        if (instance == null || !instance._bound) return;
        instance.CaptureActualDecodePayload(
            sourceHostTicks,
            readableOutput,
            arrivalHostTicks);
    }

    [Conditional("DEVELOPMENT_BUILD")]
    [Conditional("UNITY_EDITOR")]
    internal static void RecordPairBoundShadowPayloads(
        int recordIndex,
        int pairToken,
        ulong sequence,
        long sourceHostTicks,
        int shadowSnapshotRecordIndex,
        int referenceSnapshotFrame,
        int mode2SnapshotFrame,
        string v24Classification,
        float[] referencePayload,
        float[] mode2Payload,
        float[] rejectedProductionSnapshot)
    {
        KiwiActualProductionVsShadowPayloadAuthorityV44_55_27 instance =
            _instance;
        if (instance == null || !instance._bound) return;
        instance.CapturePairBoundShadowPayloads(
            recordIndex,
            pairToken,
            sequence,
            sourceHostTicks,
            shadowSnapshotRecordIndex,
            referenceSnapshotFrame,
            mode2SnapshotFrame,
            v24Classification,
            referencePayload,
            mode2Payload,
            rejectedProductionSnapshot);
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
        _recordCompletionFrameField = RequiredField(
            traceType, "RecordCompletionFrame");
        _bound = true;
        Debug.Log(
            "[Kiwi v44.55.27 ActualVsShadow] DEPENDENCY_BOUND" +
            " v24ReferenceMode2PayloadCallback=1" +
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

    private void CaptureActualDecodePayload(
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
            if (pair.ActualCaptured)
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
            pair.ActualPayload = new float[PackedOutputLength];
            for (int i = 0; i < PackedOutputLength; i++)
            {
                pair.ActualPayload[i] = readableOutput[i];
            }
            pair.DecodeArrivalHostTicks = arrivalHostTicks;
            pair.ActualCaptured = true;
            TryCompare(pair);
        }
        catch (Exception exception)
        {
            RegisterFault(
                "DECODE_CAPTURE_EXCEPTION " + exception.GetType().Name +
                " " + exception.Message);
        }
    }

    private void CapturePairBoundShadowPayloads(
        int recordIndex,
        int pairToken,
        ulong sequence,
        long sourceHostTicks,
        int shadowSnapshotRecordIndex,
        int referenceSnapshotFrame,
        int mode2SnapshotFrame,
        string v24Classification,
        float[] referencePayload,
        float[] mode2Payload,
        float[] rejectedProductionSnapshot)
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
            if (pair.ShadowCaptured)
            {
                _duplicateShadowPayloadCount++;
                RegisterFault("DUPLICATE_V24_SHADOW_PAYLOAD recordIndex=" + recordIndex);
                return;
            }
            if (pair.PairToken != pairToken ||
                pair.Sequence != sequence ||
                pair.SourceHostTicks != sourceHostTicks ||
                recordIndex != shadowSnapshotRecordIndex)
            {
                _identityMismatchCount++;
                RegisterFault(
                    "V24_CALLBACK_IDENTITY_MISMATCH recordIndex=" + recordIndex);
                return;
            }
            if (referencePayload == null ||
                mode2Payload == null ||
                rejectedProductionSnapshot == null ||
                referencePayload.Length != PackedOutputLength ||
                mode2Payload.Length != PackedOutputLength ||
                rejectedProductionSnapshot.Length != PackedOutputLength)
            {
                _payloadShapeMismatchCount++;
                RegisterFault("V24_PAYLOAD_SHAPE recordIndex=" + recordIndex);
                return;
            }
            if (object.ReferenceEquals(referencePayload, mode2Payload) ||
                object.ReferenceEquals(referencePayload, rejectedProductionSnapshot) ||
                object.ReferenceEquals(mode2Payload, rejectedProductionSnapshot))
            {
                _arrayAliasCount++;
                RegisterFault("V24_PAYLOAD_ARRAY_ALIAS recordIndex=" + recordIndex);
                return;
            }
            pair.ReferencePayload = new float[PackedOutputLength];
            pair.Mode2Payload = new float[PackedOutputLength];
            Array.Copy(
                referencePayload,
                pair.ReferencePayload,
                PackedOutputLength);
            Array.Copy(
                mode2Payload,
                pair.Mode2Payload,
                PackedOutputLength);
            if (object.ReferenceEquals(pair.ReferencePayload, referencePayload) ||
                object.ReferenceEquals(pair.Mode2Payload, mode2Payload) ||
                object.ReferenceEquals(pair.ReferencePayload, pair.Mode2Payload))
            {
                _arrayAliasCount++;
                RegisterFault("V27_DEEP_COPY_ALIAS recordIndex=" + recordIndex);
                return;
            }
            pair.ShadowSnapshotRecordIndex = shadowSnapshotRecordIndex;
            pair.ReferenceSnapshotFrame = referenceSnapshotFrame;
            pair.Mode2SnapshotFrame = mode2SnapshotFrame;
            pair.V24Classification = v24Classification ?? "-";
            pair.ShadowCaptured = true;
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
            !pair.ShadowCaptured || !pair.ActualCaptured) return;
        if (pair.ActualPayload == null ||
            pair.ReferencePayload == null ||
            pair.Mode2Payload == null ||
            pair.ActualPayload.Length != PackedOutputLength ||
            pair.ReferencePayload.Length != PackedOutputLength ||
            pair.Mode2Payload.Length != PackedOutputLength)
        {
            _partialCaptureCount++;
            RegisterFault("PARTIAL_PAYLOAD_CAPTURE recordIndex=" + pair.RecordIndex);
            return;
        }
        if (object.ReferenceEquals(pair.ActualPayload, pair.ReferencePayload) ||
            object.ReferenceEquals(pair.ActualPayload, pair.Mode2Payload) ||
            object.ReferenceEquals(pair.ReferencePayload, pair.Mode2Payload))
        {
            _arrayAliasCount++;
            RegisterFault("V27_OWNED_ARRAY_ALIAS recordIndex=" + pair.RecordIndex);
            return;
        }
        if (!TryComputeParity(
                pair,
                pair.ActualPayload,
                pair.ReferencePayload,
                "ACTUAL_REF",
                out pair.ActualReference) ||
            !TryComputeParity(
                pair,
                pair.ActualPayload,
                pair.Mode2Payload,
                "ACTUAL_MODE2",
                out pair.ActualMode2) ||
            !TryComputeParity(
                pair,
                pair.ReferencePayload,
                pair.Mode2Payload,
                "REF_MODE2",
                out pair.ReferenceMode2))
        {
            return;
        }
        pair.Compared = true;
        KiwiAsyncProducerTailSnapshotV44_55_31.CapturePayloadsBeforeRelease(pair);
        ReleasePayloadArrays(pair);
    }

    private bool TryComputeParity(
        PayloadPair pair,
        float[] left,
        float[] right,
        string label,
        out PayloadParity parity)
    {
        parity = new PayloadParity
        {
            FirstMismatchIndex = -1
        };
        double sum = 0.0;
        double max = 0.0;
        int exact = 0;
        int mismatch = 0;
        int first = -1;
        for (int i = 0; i < PackedOutputLength; i++)
        {
            float a = left[i];
            float b = right[i];
            if (!IsFinite(a) || !IsFinite(b))
            {
                _nonFinitePayloadCount++;
                RegisterFault(
                    "NONFINITE_" + label + " recordIndex=" + pair.RecordIndex +
                    " index=" + i);
                return false;
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
        parity.ExactCount = exact;
        parity.MismatchCount = mismatch;
        parity.FirstMismatchIndex = first;
        parity.MeanAbs = sum / PackedOutputLength;
        parity.MaxAbs = max;
        return true;
    }

    private static void ReleasePayloadArrays(PayloadPair pair)
    {
        if (pair == null) return;
        pair.ActualPayload = null;
        pair.ReferencePayload = null;
        pair.Mode2Payload = null;
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
            if (pair.V25Classification == "CANONICAL_PUBLICATION_PENDING")
            {
                if (!forceCoverage) continue;
                pair.V25Classification =
                    ReadString(trace, _v25ClassificationField);
            }
            if (!pair.ShadowCaptured ||
                pair.V24Classification != traceV24 ||
                pair.ShadowSnapshotRecordIndex != pair.RecordIndex ||
                pair.ReferenceSnapshotFrame < 0 ||
                pair.Mode2SnapshotFrame < 0 ||
                pair.ReferenceSnapshotFrame >
                    ReadInt(trace, _recordCompletionFrameField) ||
                pair.Mode2SnapshotFrame >
                    ReadInt(trace, _recordCompletionFrameField))
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
            if (!pair.ActualCaptured)
            {
                if (!forceCoverage) continue;
                _noDecodePayloadCoverageCount++;
                pair.Classification = "INVALID_OBSERVER";
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
            if (pair.V25Classification != "TRANSACTION_COHERENT" &&
                pair.V25Classification != "NO_CANONICAL_PUBLICATION_OBSERVED")
            {
                _identityMismatchCount++;
                pair.Classification = "INVALID_OBSERVER";
                pair.FailureReason = "V25_FINAL_CLASSIFICATION_INVALID";
                FinishPair(pair);
                continue;
            }
            pair.Eligible = true;
            pair.PublishedSubset =
                pair.V25Classification == "TRANSACTION_COHERENT";
            if (pair.PublishedSubset)
            {
                _publishedEligibleCount++;
                if (pair.Anomalous) _publishedEligibleAnomalousCount++;
            }
            bool actualEqualsReference =
                IsExact(pair.ActualReference);
            bool actualEqualsMode2 =
                IsExact(pair.ActualMode2);
            bool referenceEqualsMode2 =
                IsExact(pair.ReferenceMode2);
            if ((actualEqualsReference && actualEqualsMode2) !=
                    (actualEqualsReference && referenceEqualsMode2) ||
                (actualEqualsReference && actualEqualsMode2) !=
                    (actualEqualsMode2 && referenceEqualsMode2))
            {
                _fieldMappingMismatchCount++;
                pair.Classification = "INVALID_OBSERVER";
                pair.FailureReason = "BITWISE_EQUALITY_TRANSITIVITY_INVALID";
                FinishPair(pair);
                continue;
            }
            if (actualEqualsReference && actualEqualsMode2)
                pair.Classification = "ACTUAL_EQUALS_BOTH";
            else if (actualEqualsReference)
                pair.Classification = "ACTUAL_EQUALS_REF_ONLY";
            else if (actualEqualsMode2)
                pair.Classification = "ACTUAL_EQUALS_MODE2_ONLY";
            else
                pair.Classification = "ACTUAL_EQUALS_NEITHER";
            FinishPair(pair);
        }
    }

    private void FinishPair(PayloadPair pair)
    {
        if (pair.Finalized) return;
        pair.Finalized = true;
        KiwiAsyncProducerTailSnapshotV44_55_31.RecordFinalPair(pair);
        ReleasePayloadArrays(pair);
        _results.Add(pair);
        Debug.Log(
            "[Kiwi v44.55.27 ActualVsShadow] SAMPLE" +
            " recordIndex=" + pair.RecordIndex +
            " sequence=" + pair.Sequence +
            " pairToken=" + pair.PairToken +
            " anomalous=" + (pair.Anomalous ? 1 : 0) +
            " actualRefExact=" + pair.ActualReference.ExactCount +
            " actualMode2Exact=" + pair.ActualMode2.ExactCount +
            " refMode2Exact=" + pair.ReferenceMode2.ExactCount +
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
            _duplicateShadowPayloadCount != 0 ||
            _arrayAliasCount != 0 ||
            _fieldMappingMismatchCount != 0 ||
            _payloadShapeMismatchCount != 0 || _nonFinitePayloadCount != 0 ||
            _partialCaptureCount != 0 ||
            _noDecodePayloadCoverageCount != 0 ||
            expected < MinimumCompletedPairs ||
            _pairs.Count != expected || CountFinalized() != expected ||
            CountEligible() < MinimumCompletedPairs ||
            CountPending() != 0;
        if (invalid) return "INVALID_OBSERVER";
        int anomalous = CountEligibleAnomalous();
        if (anomalous < MinimumAnomalousEligiblePairs)
            return "INSUFFICIENT_ANOMALOUS_COVERAGE";
        if (CountEligibleAnomalousShadowExact() != 0)
            return "SHADOWS_NOT_DISCRIMINATING";
        if (CountEligibleAnomalousClassification(
                "ACTUAL_EQUALS_REF_ONLY") == anomalous)
            return "REFERENCE_PAYLOAD_AUTHORITY_CONFIRMED";
        if (CountEligibleAnomalousClassification(
                "ACTUAL_EQUALS_MODE2_ONLY") == anomalous)
            return "MODE2_PAYLOAD_AUTHORITY_CONFIRMED";
        if (CountEligibleAnomalousClassification(
                "ACTUAL_EQUALS_NEITHER") == anomalous)
            return "ACTUAL_EQUALS_NEITHER_CONFIRMED";
        return "ACTUAL_SHADOW_MIXED";
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
            "KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_" +
            stamp + ".txt");
        string csvPath = Path.Combine(
            directory,
            "KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_" +
            stamp + ".csv");
        List<string> lines = new List<string>();
        lines.Add("KiwiAvatarSystem v44.55.27 Actual Production Decode Payload vs Pair-Bound Shadow Output Authority");
        lines.Add("contract=" + Contract);
        lines.Add("status=" + status);
        lines.Add("decision=" + decision);
        lines.Add("observerOnly=1");
        lines.Add("developmentDiagnosticOnly=1");
        lines.Add("existingReadbackAndClone=1");
        lines.Add("newReadbackAndClone=0");
        lines.Add("productionInputOracle=0");
        lines.Add("rejectedV24ProductionSnapshotComparison=0");
        lines.Add("v26PayloadCopyRequired=0");
        lines.Add("newWorker=0");
        lines.Add("newSchedule=0");
        lines.Add("repeatedInference=0");
        lines.Add("newGpuCopy=0");
        lines.Add("newAsyncGpuReadback=0");
        lines.Add("blockingWait=0");
        lines.Add("logicalFloatCount=" + PackedOutputLength);
        lines.Add("performanceAuthority=0");
        lines.Add("sideA=ACTUAL_PRODUCTION_DECODE_PAYLOAD");
        lines.Add("sideB=REF_FROZEN_GPU_PAIR_BOUND");
        lines.Add("sideC=MODE2_B_GPU_PAIR_BOUND");
        lines.Add("");
        lines.Add("[COUNTS]");
        lines.Add("dependencyTraceCount=" + TraceCount());
        lines.Add("capturedPairCount=" + _pairs.Count);
        lines.Add("completedPairCount=" + CountFinalized());
        lines.Add("comparedPairCount=" + CountCompared());
        lines.Add("payloadEligiblePairCount=" + CountEligible());
        lines.Add("anomalousPairCount=" + CountAnomalous());
        lines.Add("anomalousPayloadEligiblePairCount=" +
            CountEligibleAnomalous());
        lines.Add("actualEqualsRefOnlyCount=" +
            CountClassification("ACTUAL_EQUALS_REF_ONLY"));
        lines.Add("actualEqualsMode2OnlyCount=" +
            CountClassification("ACTUAL_EQUALS_MODE2_ONLY"));
        lines.Add("actualEqualsBothCount=" +
            CountClassification("ACTUAL_EQUALS_BOTH"));
        lines.Add("actualEqualsNeitherCount=" +
            CountClassification("ACTUAL_EQUALS_NEITHER"));
        lines.Add("anomalousActualEqualsRefOnlyCount=" +
            CountEligibleAnomalousClassification("ACTUAL_EQUALS_REF_ONLY"));
        lines.Add("anomalousActualEqualsMode2OnlyCount=" +
            CountEligibleAnomalousClassification("ACTUAL_EQUALS_MODE2_ONLY"));
        lines.Add("anomalousActualEqualsBothCount=" +
            CountEligibleAnomalousClassification("ACTUAL_EQUALS_BOTH"));
        lines.Add("anomalousActualEqualsNeitherCount=" +
            CountEligibleAnomalousClassification("ACTUAL_EQUALS_NEITHER"));
        lines.Add("anomalousShadowExactCount=" +
            CountEligibleAnomalousShadowExact());
        lines.Add("publishedPayloadEligiblePairCount=" +
            _publishedEligibleCount);
        lines.Add("publishedAnomalousPayloadEligiblePairCount=" +
            _publishedEligibleAnomalousCount);
        lines.Add("publishedActualEqualsRefOnlyCount=" +
            CountPublishedClassification("ACTUAL_EQUALS_REF_ONLY"));
        lines.Add("publishedActualEqualsMode2OnlyCount=" +
            CountPublishedClassification("ACTUAL_EQUALS_MODE2_ONLY"));
        lines.Add("publishedActualEqualsBothCount=" +
            CountPublishedClassification("ACTUAL_EQUALS_BOTH"));
        lines.Add("publishedActualEqualsNeitherCount=" +
            CountPublishedClassification("ACTUAL_EQUALS_NEITHER"));
        lines.Add("publishedAnomalousActualEqualsRefOnlyCount=" +
            CountPublishedAnomalousClassification("ACTUAL_EQUALS_REF_ONLY"));
        lines.Add("publishedAnomalousActualEqualsMode2OnlyCount=" +
            CountPublishedAnomalousClassification("ACTUAL_EQUALS_MODE2_ONLY"));
        lines.Add("publishedAnomalousActualEqualsBothCount=" +
            CountPublishedAnomalousClassification("ACTUAL_EQUALS_BOTH"));
        lines.Add("publishedAnomalousActualEqualsNeitherCount=" +
            CountPublishedAnomalousClassification("ACTUAL_EQUALS_NEITHER"));
        lines.Add("noDecodePayloadCoverageCount=" +
            _noDecodePayloadCoverageCount);
        lines.Add("observerFaultCount=" + _observerFaultCount);
        lines.Add("identityMismatchCount=" + _identityMismatchCount);
        lines.Add("duplicateDecodePayloadCount=" +
            _duplicateDecodePayloadCount);
        lines.Add("duplicateShadowPayloadCount=" +
            _duplicateShadowPayloadCount);
        lines.Add("arrayAliasCount=" + _arrayAliasCount);
        lines.Add("fieldMappingMismatchCount=" +
            _fieldMappingMismatchCount);
        lines.Add("payloadShapeMismatchCount=" + _payloadShapeMismatchCount);
        lines.Add("nonFinitePayloadCount=" + _nonFinitePayloadCount);
        lines.Add("partialCaptureCount=" + _partialCaptureCount);
        lines.Add("pendingAtCompletion=" + CountPending());
        lines.Add("");
        lines.Add("[FIXED_RUNTIME_GATE]");
        lines.Add("minimumPayloadEligiblePairCount=" + MinimumCompletedPairs);
        lines.Add("minimumAnomalousPayloadEligiblePairCount=" +
            MinimumAnomalousEligiblePairs);
        lines.Add("dependencyCountMustEqualCompletedCount=1");
        lines.Add("observerIntegrityCountersMustBeZero=1");
        lines.Add("decisionAndRowsMustAgree=1");
        lines.Add("canonicalPublicationRequiredForPrimaryEligibility=0");
        lines.Add("publishedSubsetReportedSeparately=1");
        lines.Add("");
        lines.Add("[DECISION_RULE]");
        lines.Add("REFERENCE_PAYLOAD_AUTHORITY_CONFIRMED=every anomalous payload-eligible pair is ACTUAL_EQUALS_REF_ONLY");
        lines.Add("MODE2_PAYLOAD_AUTHORITY_CONFIRMED=every anomalous payload-eligible pair is ACTUAL_EQUALS_MODE2_ONLY");
        lines.Add("ACTUAL_SHADOW_MIXED=discriminating anomalous population contains more than one actual-vs-shadow classification");
        lines.Add("ACTUAL_EQUALS_NEITHER_CONFIRMED=every anomalous payload-eligible pair is ACTUAL_EQUALS_NEITHER");
        lines.Add("SHADOWS_NOT_DISCRIMINATING=at least one anomalous payload-eligible pair has REF bitwise equal to MODE2");
        lines.Add("INSUFFICIENT_ANOMALOUS_COVERAGE=fewer than three anomalous payload-eligible pairs");
        lines.Add("INVALID_OBSERVER=identity alias deep-copy mapping duplicate shape finite partial dependency callback count or validator contract failed");
        File.WriteAllLines(textPath, lines.ToArray(), new UTF8Encoding(false));
        WriteCsv(csvPath);
        Debug.Log(
            "[Kiwi v44.55.27 ActualVsShadow] COMPLETE" +
            " status=" + status + " decision=" + decision +
            " completedPairCount=" + CountFinalized() +
            " anomalousPairCount=" + CountAnomalous() +
            " eligibleAnomalousPairCount=" + CountEligibleAnomalous() +
            " anomalousRefOnly=" +
            CountEligibleAnomalousClassification("ACTUAL_EQUALS_REF_ONLY") +
            " anomalousMode2Only=" +
            CountEligibleAnomalousClassification("ACTUAL_EQUALS_MODE2_ONLY") +
            " anomalousNeither=" +
            CountEligibleAnomalousClassification("ACTUAL_EQUALS_NEITHER") +
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
            "shadowSnapshotRecordIndex,referenceSnapshotFrame,mode2SnapshotFrame," +
            "shadowCaptured,actualCaptured,payloadEligible,publishedSubset," +
            "actualRefExactCount,actualRefMismatchCount,actualRefBitwiseExact,actualRefMeanAbs," +
            "actualRefMaxAbs,actualRefFirstMismatchIndex," +
            "actualMode2ExactCount,actualMode2MismatchCount,actualMode2BitwiseExact," +
            "actualMode2MeanAbs,actualMode2MaxAbs,actualMode2FirstMismatchIndex," +
            "refMode2ExactCount,refMode2MismatchCount,refMode2BitwiseExact,refMode2MeanAbs," +
            "refMode2MaxAbs,refMode2FirstMismatchIndex,classification,failureReason");
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
            Append(builder, p.ShadowSnapshotRecordIndex); Sep(builder);
            Append(builder, p.ReferenceSnapshotFrame); Sep(builder);
            Append(builder, p.Mode2SnapshotFrame); Sep(builder);
            Append(builder, p.ShadowCaptured ? 1 : 0); Sep(builder);
            Append(builder, p.ActualCaptured ? 1 : 0); Sep(builder);
            Append(builder, p.Eligible ? 1 : 0); Sep(builder);
            Append(builder, p.PublishedSubset ? 1 : 0); Sep(builder);
            AppendParity(builder, p.ActualReference);
            AppendParity(builder, p.ActualMode2);
            AppendParity(builder, p.ReferenceMode2);
            builder.Append(Csv(p.Classification)); Sep(builder);
            builder.Append(Csv(p.FailureReason));
            builder.AppendLine();
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void AppendParity(
        StringBuilder builder,
        PayloadParity parity)
    {
        Append(builder, parity.ExactCount); Sep(builder);
        Append(builder, parity.MismatchCount); Sep(builder);
        Append(builder, IsExact(parity) ? 1 : 0); Sep(builder);
        builder.Append(parity.MeanAbs.ToString(
            "R", CultureInfo.InvariantCulture)); Sep(builder);
        builder.Append(parity.MaxAbs.ToString(
            "R", CultureInfo.InvariantCulture)); Sep(builder);
        Append(builder, parity.FirstMismatchIndex); Sep(builder);
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

    private int CountEligibleAnomalousClassification(string classification)
    {
        int count = 0;
        for (int i = 0; i < _results.Count; i++)
        {
            PayloadPair pair = _results[i];
            if (pair.Eligible && pair.Anomalous &&
                pair.Classification == classification)
                count++;
        }
        return count;
    }

    private int CountEligibleAnomalousShadowExact()
    {
        int count = 0;
        for (int i = 0; i < _results.Count; i++)
        {
            PayloadPair pair = _results[i];
            if (pair.Eligible && pair.Anomalous &&
                IsExact(pair.ReferenceMode2))
                count++;
        }
        return count;
    }

    private int CountPublishedClassification(string classification)
    {
        int count = 0;
        for (int i = 0; i < _results.Count; i++)
        {
            PayloadPair pair = _results[i];
            if (pair.Eligible && pair.PublishedSubset &&
                pair.Classification == classification)
                count++;
        }
        return count;
    }

    private int CountPublishedAnomalousClassification(string classification)
    {
        int count = 0;
        for (int i = 0; i < _results.Count; i++)
        {
            PayloadPair pair = _results[i];
            if (pair.Eligible && pair.PublishedSubset && pair.Anomalous &&
                pair.Classification == classification)
                count++;
        }
        return count;
    }

    private static bool IsExact(PayloadParity parity)
    {
        return parity.ExactCount == PackedOutputLength &&
            parity.MismatchCount == 0 &&
            parity.FirstMismatchIndex == -1;
    }

    private void RegisterFault(string reason)
    {
        _observerFaultCount++;
        Debug.LogWarning(
            "[Kiwi v44.55.27 ActualVsShadow] OBSERVER_FAULT " + reason);
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
