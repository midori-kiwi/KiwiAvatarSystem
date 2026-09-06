using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using Unity.InferenceEngine;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// KiwiAvatarSystem v44.55.25 Production Schedule Transaction Trace.
///
/// Observer-only transaction-identity closure for the exact pairs already owned
/// by v44.55.20 and snapshotted by v44.55.24. No tensor content is read here.
/// The observer freezes Production lane metadata and object references at pair
/// attach, verifies them immediately after the v44.55.24 early snapshot probe
/// and before ordinary Production Update, then binds the same v44.55.24 result.
/// Object tokens are diagnostic representations only; ReferenceEquals and exact
/// field comparisons are the authority.
/// </summary>
[DefaultExecutionOrder(35000)]
internal sealed class KiwiProductionScheduleTransactionTraceV44_55_25
    : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE";

    private const string EnableVariable =
        "KIWI_V44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE";

    private const int MinimumCompletedTraces = 60;
    private const int MinimumAnomalousPairs = 3;
    private const double CompletionGraceSeconds = 8.0;

    private static readonly BindingFlags InstanceFlags =
        BindingFlags.Instance |
        BindingFlags.Public |
        BindingFlags.NonPublic;

    private static bool _installed;

    private KiwiCommonTensorBackendStageIsolationV44_55_20 _v20;
    private KiwiPairBoundShadowOutputSnapshotV44_55_24 _v24;
    private FaceLandmarkerRunner _runner;
    private Type _v20Type;
    private Type _v24Type;

    private FieldInfo _v20PairPendingField;
    private FieldInfo _v20PairTokenField;
    private FieldInfo _v20PendingRecordField;
    private FieldInfo _v20RecordsField;
    private FieldInfo _v20ReportWrittenField;
    private FieldInfo _v20ObserverFaultCountField;
    private FieldInfo _v20LanesField;

    private FieldInfo _v24CapturesField;
    private FieldInfo _v24ResultsField;
    private FieldInfo _v24ReportWrittenField;
    private FieldInfo _v24ObserverFaultCountField;

    private FieldInfo _v24StateIndexField;
    private FieldInfo _v24StatePairTokenField;
    private FieldInfo _v24StateSequenceField;
    private FieldInfo _v24StateNativeHostTicksField;
    private FieldInfo _v24StateManagedHostTicksField;
    private FieldInfo _v24StateLaneStartedHostTicksField;
    private FieldInfo _v24StateProductionLaneField;
    private FieldInfo _v24StateProductionSnapshotSubmittedField;
    private FieldInfo _v24StateFailedField;

    private FieldInfo _v24ResultIndexField;
    private FieldInfo _v24ResultPairTokenField;
    private FieldInfo _v24ResultRecordIndexField;
    private FieldInfo _v24ResultSequenceField;
    private FieldInfo _v24ResultSnapshotSequenceField;
    private FieldInfo _v24ResultRecordCompletionFrameField;
    private FieldInfo _v24ResultRefMode2Ge4Field;
    private FieldInfo _v24ResultClassificationField;

    private FieldInfo _laneWorkerField;
    private FieldInfo _laneInputField;
    private FieldInfo _lanePendingOutputField;
    private FieldInfo _laneReadbackPendingField;
    private FieldInfo _lanePendingCropMatrixField;
    private FieldInfo _lanePendingSourceHostTicksField;
    private FieldInfo _lanePendingScheduleBeginHostTicksField;
    private FieldInfo _lanePendingStartedHostTicksField;
    private FieldInfo _lanePendingReadbackRequestHostTicksField;
    private FieldInfo _lanePendingReadbackRequestUnityFrameField;
    private FieldInfo _lanePendingAnchorRevisionField;
    private FieldInfo _lanePendingExternalAnchorEpochField;
    private FieldInfo _lanePendingTrackerGenerationField;
    private FieldInfo _lanePendingCameraGenerationField;
    private FieldInfo _lanePendingTrackingSessionGenerationField;
    private FieldInfo _lanePendingMinimumPresenceField;

    private bool _bound;
    private bool _reportWritten;
    private int _dependencyMissingFrames;
    private double _dependenciesCompleteSince = -1.0;

    private int _completedTraceCount;
    private int _dependencyPairCount;
    private int _anomalousPairCount;
    private int _scheduleAttachMissCount;
    private int _ambiguousLaneMatchCount;
    private int _laneIdentityMismatchCount;
    private int _workerIdentityMismatchCount;
    private int _inputTensorIdentityMismatchCount;
    private int _pendingOutputIdentityMismatchCount;
    private int _sourceHostTicksMismatchCount;
    private int _scheduleBeginTicksMismatchCount;
    private int _startedHostTicksMismatchCount;
    private int _readbackRequestTicksMismatchCount;
    private int _readbackRequestFrameMismatchCount;
    private int _anchorRevisionMismatchCount;
    private int _externalAnchorEpochMismatchCount;
    private int _trackerGenerationMismatchCount;
    private int _cameraGenerationMismatchCount;
    private int _trackingSessionGenerationMismatchCount;
    private int _minimumPresenceMismatchCount;
    private int _cropMatrixMismatchCount;
    private int _v24PairIdentityMismatchCount;
    private int _outputReadyObservationMissCount;
    private int _snapshotBindingMissCount;
    private int _canonicalPublicationMissCount;
    private int _canonicalPublicationIdentityInvalidCount;
    private int _canonicalPublicationDuplicateCount;
    private int _duplicateTraceCount;
    private int _partialTraceCount;
    private int _observerFaultCount;

    private readonly Dictionary<int, TraceState> _traces =
        new Dictionary<int, TraceState>();

    private readonly HashSet<int> _missingTraceIndices =
        new HashSet<int>();

    private readonly Dictionary<object, int> _objectTokens =
        new Dictionary<object, int>(ReferenceComparer.Instance);

    private int _nextObjectToken = 1;
    private ulong _lastObservedPublishedFrameId;

    private sealed class TraceState
    {
        internal int RecordIndex;
        internal ulong Sequence;
        internal int PairToken;
        internal long NativeHostTicks;
        internal long ManagedHostTicks;
        internal long LaneStartedHostTicks;
        internal int LaneIndex;
        internal object Lane;
        internal object Worker;
        internal object InputTensor;
        internal Tensor<float> PendingOutput;
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
        internal readonly int[] CropMatrixBits = new int[16];
        internal int LaneIdentityToken;
        internal int WorkerIdentityToken;
        internal int InputTensorIdentityToken;
        internal int PendingOutputIdentityToken;
        internal int PairAttachFrame = -1;
        internal int OutputReadyFrame = -1;
        internal int SnapshotSubmissionFrame = -1;
        internal int RecordCompletionFrame = -1;
        internal int CanonicalPublicationFrame = -1;
        internal ulong CanonicalPublicationFrameId;
        internal long CanonicalPublicationTimestamp = -1L;
        internal long CanonicalPublicationArrivalHostTicks;
        internal bool BoundaryA;
        internal bool BoundaryB;
        internal bool BoundaryC;
        internal bool BoundaryD;
        internal bool ScheduleMismatch;
        internal bool CanonicalPublicationMismatch;
        internal bool ObserverInvalid;
        internal bool Completed;
        internal bool Anomalous;
        internal string FailureReason = "-";
        internal string V24Classification = "-";
        internal string Classification = "PENDING";
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance =
            new ReferenceComparer();

        public new bool Equals(object left, object right)
        {
            return object.ReferenceEquals(left, right);
        }

        public int GetHashCode(object value)
        {
            return RuntimeHelpers.GetHashCode(value);
        }
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed || !ReadBoolEnvironment(EnableVariable, false))
        {
            return;
        }

        _installed = true;

        GameObject go = new GameObject(
            "[Kiwi] v44.55.25 Production Schedule Transaction Trace");

        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.DontSave;

        KiwiProductionScheduleTransactionTraceV44_55_25 observer =
            go.AddComponent<
                KiwiProductionScheduleTransactionTraceV44_55_25>();

        KiwiProductionScheduleTransactionEarlyProbeV44_55_25 earlyProbe =
            go.AddComponent<
                KiwiProductionScheduleTransactionEarlyProbeV44_55_25>();

        earlyProbe.Owner = observer;
    }

    private void Awake()
    {
        Debug.Log(
            "[Kiwi v44.55.25 ScheduleTrace] WAIT_DEPENDENCY" +
            " contract=" + Contract +
            " observerOnly=1" +
            " productionWrites=0" +
            " productionInputContentReadback=0" +
            " repeatedInference=0" +
            " blockingWait=0" +
            " newGpuSnapshot=0" +
            " performanceAuthority=0");
    }

    private void Update()
    {
        PollMain();
    }

    private void LateUpdate()
    {
        PollMain();
    }

    private void PollMain()
    {
        if (_reportWritten)
        {
            return;
        }

        if (!_bound)
        {
            TryBindDependencies();

            if (!_bound)
            {
                _dependencyMissingFrames++;

                if (_dependencyMissingFrames >= 300)
                {
                    RegisterGlobalObserverFault(
                        "DEPENDENCY_BIND_TIMEOUT");
                    WriteReport("BIND_FAIL");
                }

                return;
            }
        }

        try
        {
            AttachPendingPair();
            ObserveCanonicalPublication();
            BindCompletedV24Results();
            DetectMissedOutputReadyBoundaries();
            TryFinishAfterDependencies();
        }
        catch (Exception exception)
        {
            RegisterGlobalObserverFault(
                "MAIN_POLL_EXCEPTION " +
                exception.GetType().Name +
                " " +
                exception.Message);
            WriteReport("OBSERVER_EXCEPTION");
        }
    }

    internal void PollAfterV24BeforeProductionUpdate()
    {
        if (_reportWritten || !_bound || _traces.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<int, TraceState> pair in _traces)
        {
            TraceState state = pair.Value;

            if (
                state == null ||
                state.Completed ||
                state.ObserverInvalid ||
                state.ScheduleMismatch ||
                state.BoundaryB)
            {
                continue;
            }

            try
            {
                VerifyReadyBoundary(state);
            }
            catch (Exception exception)
            {
                RegisterObserverInvalid(
                    state,
                    "EARLY_PROBE_EXCEPTION " +
                    exception.GetType().Name +
                    " " +
                    exception.Message);
            }
        }
    }

    private void TryBindDependencies()
    {
        KiwiCommonTensorBackendStageIsolationV44_55_20[] v20Candidates =
            Resources.FindObjectsOfTypeAll<
                KiwiCommonTensorBackendStageIsolationV44_55_20>();

        KiwiPairBoundShadowOutputSnapshotV44_55_24[] v24Candidates =
            Resources.FindObjectsOfTypeAll<
                KiwiPairBoundShadowOutputSnapshotV44_55_24>();

        FaceLandmarkerRunner[] runnerCandidates =
            Resources.FindObjectsOfTypeAll<FaceLandmarkerRunner>();

        if (
            v20Candidates == null ||
            v20Candidates.Length == 0 ||
            v24Candidates == null ||
            v24Candidates.Length == 0 ||
            runnerCandidates == null ||
            runnerCandidates.Length == 0)
        {
            return;
        }

        if (v20Candidates.Length != 1 || v24Candidates.Length != 1)
        {
            RegisterGlobalObserverFault(
                "AMBIGUOUS_DEPENDENCY v20=" +
                v20Candidates.Length +
                " v24=" +
                v24Candidates.Length);
            WriteReport("BIND_FAIL");
            return;
        }

        try
        {
            _v20 = v20Candidates[0];
            _v24 = v24Candidates[0];

            int activeRunnerCount = 0;

            for (int i = 0; i < runnerCandidates.Length; i++)
            {
                FaceLandmarkerRunner candidate = runnerCandidates[i];

                if (
                    candidate != null &&
                    candidate.isActiveAndEnabled &&
                    candidate.gameObject.activeInHierarchy)
                {
                    activeRunnerCount++;
                    _runner = candidate;
                }
            }

            if (activeRunnerCount != 1)
            {
                throw new InvalidOperationException(
                    "ACTIVE_RUNNER_COUNT " + activeRunnerCount);
            }
            _v20Type = _v20.GetType();
            _v24Type = _v24.GetType();

            _v20PairPendingField = RequiredField(_v20Type, "_pairPending");
            _v20PairTokenField = RequiredField(_v20Type, "_pairToken");
            _v20PendingRecordField = RequiredField(_v20Type, "_pendingRecord");
            _v20RecordsField = RequiredField(_v20Type, "_records");
            _v20ReportWrittenField = RequiredField(_v20Type, "_reportWritten");
            _v20ObserverFaultCountField = RequiredField(
                _v20Type,
                "_observerFaultCount");
            _v20LanesField = RequiredField(_v20Type, "_lanes");

            _v24CapturesField = RequiredField(_v24Type, "_captures");
            _v24ResultsField = RequiredField(_v24Type, "_results");
            _v24ReportWrittenField = RequiredField(
                _v24Type,
                "_reportWritten");
            _v24ObserverFaultCountField = RequiredField(
                _v24Type,
                "_observerFaultCount");

            Type v24StateType = RequiredNestedType(_v24Type, "CaptureState");
            _v24StateIndexField = RequiredField(v24StateType, "Index");
            _v24StatePairTokenField = RequiredField(v24StateType, "PairToken");
            _v24StateSequenceField = RequiredField(v24StateType, "Sequence");
            _v24StateNativeHostTicksField = RequiredField(
                v24StateType,
                "NativeHostTicks");
            _v24StateManagedHostTicksField = RequiredField(
                v24StateType,
                "ManagedHostTicks");
            _v24StateLaneStartedHostTicksField = RequiredField(
                v24StateType,
                "LaneStartedHostTicks");
            _v24StateProductionLaneField = RequiredField(
                v24StateType,
                "ProductionLane");
            _v24StateProductionSnapshotSubmittedField = RequiredField(
                v24StateType,
                "ProductionSnapshotSubmitted");
            _v24StateFailedField = RequiredField(v24StateType, "Failed");

            Type v24ResultType = RequiredNestedType(_v24Type, "PairResult");
            _v24ResultIndexField = RequiredField(v24ResultType, "Index");
            _v24ResultPairTokenField = RequiredField(
                v24ResultType,
                "ShadowSnapshotPairToken");
            _v24ResultRecordIndexField = RequiredField(
                v24ResultType,
                "ShadowSnapshotRecordIndex");
            _v24ResultSequenceField = RequiredField(v24ResultType, "Sequence");
            _v24ResultSnapshotSequenceField = RequiredField(
                v24ResultType,
                "ShadowSnapshotSequence");
            _v24ResultRecordCompletionFrameField = RequiredField(
                v24ResultType,
                "RecordCompletionFrame");
            _v24ResultRefMode2Ge4Field = RequiredField(
                v24ResultType,
                "RefMode2Ge4");
            _v24ResultClassificationField = RequiredField(
                v24ResultType,
                "Classification");

            Array lanes = _v20LanesField.GetValue(_v20) as Array;

            if (lanes == null || lanes.Length == 0 || lanes.GetValue(0) == null)
            {
                return;
            }

            Type laneType = lanes.GetValue(0).GetType();
            _laneWorkerField = RequiredField(laneType, "worker");
            _laneInputField = RequiredField(laneType, "input");
            _lanePendingOutputField = RequiredField(laneType, "pendingOutput");
            _laneReadbackPendingField = RequiredField(laneType, "readbackPending");
            _lanePendingCropMatrixField = RequiredField(
                laneType,
                "pendingCropMatrix");
            _lanePendingSourceHostTicksField = RequiredField(
                laneType,
                "pendingSourceHostTicks");
            _lanePendingScheduleBeginHostTicksField = RequiredField(
                laneType,
                "pendingScheduleBeginHostTicks");
            _lanePendingStartedHostTicksField = RequiredField(
                laneType,
                "pendingStartedHostTicks");
            _lanePendingReadbackRequestHostTicksField = RequiredField(
                laneType,
                "pendingReadbackRequestHostTicks");
            _lanePendingReadbackRequestUnityFrameField = RequiredField(
                laneType,
                "pendingReadbackRequestUnityFrame");
            _lanePendingAnchorRevisionField = RequiredField(
                laneType,
                "pendingAnchorRevision");
            _lanePendingExternalAnchorEpochField = RequiredField(
                laneType,
                "pendingExternalAnchorEpoch");
            _lanePendingTrackerGenerationField = RequiredField(
                laneType,
                "pendingTrackerGeneration");
            _lanePendingCameraGenerationField = RequiredField(
                laneType,
                "pendingCameraGeneration");
            _lanePendingTrackingSessionGenerationField = RequiredField(
                laneType,
                "pendingTrackingSessionGeneration");
            _lanePendingMinimumPresenceField = RequiredField(
                laneType,
                "pendingMinimumPresence");

            _bound = true;

            Debug.Log(
                "[Kiwi v44.55.25 ScheduleTrace] DEPENDENCY_BOUND" +
                " mainExecutionOrder=35000" +
                " earlyExecutionOrder=-32000" +
                " v24MainExecutionOrder=34000" +
                " v24EarlyExecutionOrder=-33000" +
                " productionOrdinaryUpdateAfterEarlyProbe=1" +
                " referenceIdentityAuthority=ReferenceEquals" +
                " exactMetadataIdentity=1" +
                " canonicalPublicationIdentity=frameId+backend+submissionHostTicks" +
                " performanceAuthority=0");
        }
        catch (Exception exception)
        {
            RegisterGlobalObserverFault(
                "DEPENDENCY_BIND_FAIL " +
                exception.GetType().Name +
                " " +
                exception.Message);
            WriteReport("BIND_FAIL");
        }
    }

    private void AttachPendingPair()
    {
        if (!ReadBool(_v20, _v20PairPendingField))
        {
            return;
        }

        int pairToken = ReadInt(_v20, _v20PairTokenField);
        object record = _v20PendingRecordField.GetValue(_v20);

        if (record == null || pairToken <= 0)
        {
            return;
        }

        int index = ReadMemberInt(record, "Index");
        ulong sequence = ReadMemberULong(record, "Sequence");
        long nativeHostTicks = ReadMemberLong(record, "NativeHostTicks");
        long managedHostTicks = ReadMemberLong(record, "ManagedHostTicks");
        long laneStartedHostTicks = ReadMemberLong(
            record,
            "LaneStartedHostTicks");

        if (_traces.TryGetValue(index, out TraceState existing))
        {
            if (
                existing.PairToken != pairToken ||
                existing.Sequence != sequence ||
                existing.NativeHostTicks != nativeHostTicks ||
                existing.ManagedHostTicks != managedHostTicks ||
                existing.LaneStartedHostTicks != laneStartedHostTicks)
            {
                _duplicateTraceCount++;
                RegisterObserverInvalid(
                    existing,
                    "DUPLICATE_TRACE_IDENTITY_CHANGED");
            }

            return;
        }

        if (
            index < 0 ||
            sequence == 0UL ||
            nativeHostTicks <= 0L ||
            managedHostTicks <= 0L ||
            laneStartedHostTicks <= 0L)
        {
            RegisterMissingAttach(index, "PAIR_IDENTITY_INVALID");
            return;
        }

        Array lanes = _v20LanesField.GetValue(_v20) as Array;
        object matchedLane = null;
        int matchedLaneIndex = -1;
        int matchCount = 0;

        if (lanes != null)
        {
            for (int i = 0; i < lanes.Length; i++)
            {
                object lane = lanes.GetValue(i);

                if (
                    lane != null &&
                    ReadBool(lane, _laneReadbackPendingField) &&
                    ReadLong(lane, _lanePendingSourceHostTicksField) ==
                        managedHostTicks &&
                    ReadLong(lane, _lanePendingStartedHostTicksField) ==
                        laneStartedHostTicks)
                {
                    matchCount++;
                    matchedLane = lane;
                    matchedLaneIndex = i;
                }
            }
        }

        if (matchCount == 0)
        {
            RegisterMissingAttach(index, "PRODUCTION_LANE_NOT_MATCHED");
            return;
        }

        if (matchCount != 1)
        {
            _ambiguousLaneMatchCount++;
            RegisterMissingAttach(index, "AMBIGUOUS_PRODUCTION_LANE_MATCH");
            return;
        }

        object v24State = FindV24Capture(index);

        if (v24State == null)
        {
            RegisterMissingAttach(index, "V24_CAPTURE_NOT_ATTACHED");
            return;
        }

        if (
            ReadInt(v24State, _v24StateIndexField) != index ||
            ReadInt(v24State, _v24StatePairTokenField) != pairToken ||
            ReadULong(v24State, _v24StateSequenceField) != sequence ||
            ReadLong(v24State, _v24StateNativeHostTicksField) !=
                nativeHostTicks ||
            ReadLong(v24State, _v24StateManagedHostTicksField) !=
                managedHostTicks ||
            ReadLong(v24State, _v24StateLaneStartedHostTicksField) !=
                laneStartedHostTicks ||
            !object.ReferenceEquals(
                _v24StateProductionLaneField.GetValue(v24State),
                matchedLane) ||
            ReadBool(v24State, _v24StateFailedField))
        {
            _v24PairIdentityMismatchCount++;
            RegisterMissingAttach(index, "V24_CAPTURE_IDENTITY_MISMATCH");
            return;
        }

        object worker = _laneWorkerField.GetValue(matchedLane);
        object inputTensor = _laneInputField.GetValue(matchedLane);
        Tensor<float> pendingOutput =
            _lanePendingOutputField.GetValue(matchedLane) as Tensor<float>;

        long sourceHostTicks = ReadLong(
            matchedLane,
            _lanePendingSourceHostTicksField);
        long scheduleBeginHostTicks = ReadLong(
            matchedLane,
            _lanePendingScheduleBeginHostTicksField);
        long startedHostTicks = ReadLong(
            matchedLane,
            _lanePendingStartedHostTicksField);
        long readbackRequestHostTicks = ReadLong(
            matchedLane,
            _lanePendingReadbackRequestHostTicksField);
        int readbackRequestFrame = ReadInt(
            matchedLane,
            _lanePendingReadbackRequestUnityFrameField);

        if (
            worker == null ||
            inputTensor == null ||
            pendingOutput == null ||
            sourceHostTicks != managedHostTicks ||
            scheduleBeginHostTicks <= 0L ||
            startedHostTicks != laneStartedHostTicks ||
            readbackRequestHostTicks <= 0L ||
            readbackRequestFrame < 0)
        {
            RegisterMissingAttach(index, "PRODUCTION_SCHEDULE_IDENTITY_INVALID");
            return;
        }

        Matrix4x4 cropMatrix = ReadMatrix(
            matchedLane,
            _lanePendingCropMatrixField);

        TraceState state = new TraceState
        {
            RecordIndex = index,
            Sequence = sequence,
            PairToken = pairToken,
            NativeHostTicks = nativeHostTicks,
            ManagedHostTicks = managedHostTicks,
            LaneStartedHostTicks = laneStartedHostTicks,
            LaneIndex = matchedLaneIndex,
            Lane = matchedLane,
            Worker = worker,
            InputTensor = inputTensor,
            PendingOutput = pendingOutput,
            SourceHostTicks = sourceHostTicks,
            ScheduleBeginHostTicks = scheduleBeginHostTicks,
            StartedHostTicks = startedHostTicks,
            ReadbackRequestHostTicks = readbackRequestHostTicks,
            ReadbackRequestFrame = readbackRequestFrame,
            AnchorRevision = ReadInt(
                matchedLane,
                _lanePendingAnchorRevisionField),
            ExternalAnchorEpoch = ReadInt(
                matchedLane,
                _lanePendingExternalAnchorEpochField),
            TrackerGeneration = ReadInt(
                matchedLane,
                _lanePendingTrackerGenerationField),
            CameraGeneration = ReadInt(
                matchedLane,
                _lanePendingCameraGenerationField),
            TrackingSessionGeneration = ReadInt(
                matchedLane,
                _lanePendingTrackingSessionGenerationField),
            MinimumPresenceBits = BitConverter.SingleToInt32Bits(
                ReadFloat(matchedLane, _lanePendingMinimumPresenceField)),
            PairAttachFrame = Time.frameCount,
            BoundaryA = true
        };

        CaptureMatrixBits(cropMatrix, state.CropMatrixBits);
        state.LaneIdentityToken = GetObjectToken(state.Lane);
        state.WorkerIdentityToken = GetObjectToken(state.Worker);
        state.InputTensorIdentityToken = GetObjectToken(state.InputTensor);
        state.PendingOutputIdentityToken = GetObjectToken(state.PendingOutput);

        _traces.Add(index, state);
        KiwiAsyncProducerTailSnapshotV44_55_31.BindTrace(state);

        Debug.Log(
            "[Kiwi v44.55.25 ScheduleTrace] PAIR_ATTACHED" +
            " recordIndex=" + index +
            " sequence=" + sequence +
            " pairToken=" + pairToken +
            " laneIndex=" + matchedLaneIndex +
            " laneToken=" + state.LaneIdentityToken +
            " workerToken=" + state.WorkerIdentityToken +
            " inputToken=" + state.InputTensorIdentityToken +
            " outputToken=" + state.PendingOutputIdentityToken +
            " boundaryA=1");
    }

    private void VerifyReadyBoundary(TraceState state)
    {
        Array lanes = _v20LanesField.GetValue(_v20) as Array;

        if (
            lanes == null ||
            state.LaneIndex < 0 ||
            state.LaneIndex >= lanes.Length ||
            !object.ReferenceEquals(lanes.GetValue(state.LaneIndex), state.Lane))
        {
            RegisterScheduleMismatch(
                state,
                "LANE_IDENTITY_MISMATCH",
                ref _laneIdentityMismatchCount);
            return;
        }

        if (!ReadBool(state.Lane, _laneReadbackPendingField))
        {
            _outputReadyObservationMissCount++;
            RegisterObserverInvalid(
                state,
                "OUTPUT_READY_CONSUMED_BEFORE_EARLY_PROBE");
            return;
        }

        if (!object.ReferenceEquals(
                _laneWorkerField.GetValue(state.Lane),
                state.Worker))
        {
            RegisterScheduleMismatch(
                state,
                "WORKER_IDENTITY_MISMATCH",
                ref _workerIdentityMismatchCount);
            return;
        }

        if (!object.ReferenceEquals(
                _laneInputField.GetValue(state.Lane),
                state.InputTensor))
        {
            RegisterScheduleMismatch(
                state,
                "INPUT_TENSOR_IDENTITY_MISMATCH",
                ref _inputTensorIdentityMismatchCount);
            return;
        }

        Tensor<float> currentOutput =
            _lanePendingOutputField.GetValue(state.Lane) as Tensor<float>;

        if (!object.ReferenceEquals(currentOutput, state.PendingOutput))
        {
            RegisterScheduleMismatch(
                state,
                "PENDING_OUTPUT_IDENTITY_MISMATCH",
                ref _pendingOutputIdentityMismatchCount);
            return;
        }

        if (ReadLong(state.Lane, _lanePendingSourceHostTicksField) !=
            state.SourceHostTicks)
        {
            RegisterScheduleMismatch(
                state,
                "SOURCE_HOST_TICKS_MISMATCH",
                ref _sourceHostTicksMismatchCount);
            return;
        }

        if (ReadLong(state.Lane, _lanePendingScheduleBeginHostTicksField) !=
            state.ScheduleBeginHostTicks)
        {
            RegisterScheduleMismatch(
                state,
                "SCHEDULE_BEGIN_TICKS_MISMATCH",
                ref _scheduleBeginTicksMismatchCount);
            return;
        }

        if (ReadLong(state.Lane, _lanePendingStartedHostTicksField) !=
            state.StartedHostTicks)
        {
            RegisterScheduleMismatch(
                state,
                "STARTED_HOST_TICKS_MISMATCH",
                ref _startedHostTicksMismatchCount);
            return;
        }

        if (ReadLong(state.Lane, _lanePendingReadbackRequestHostTicksField) !=
            state.ReadbackRequestHostTicks)
        {
            RegisterScheduleMismatch(
                state,
                "READBACK_REQUEST_TICKS_MISMATCH",
                ref _readbackRequestTicksMismatchCount);
            return;
        }

        if (ReadInt(state.Lane, _lanePendingReadbackRequestUnityFrameField) !=
            state.ReadbackRequestFrame)
        {
            RegisterScheduleMismatch(
                state,
                "READBACK_REQUEST_FRAME_MISMATCH",
                ref _readbackRequestFrameMismatchCount);
            return;
        }

        if (ReadInt(state.Lane, _lanePendingAnchorRevisionField) !=
            state.AnchorRevision)
        {
            RegisterScheduleMismatch(
                state,
                "ANCHOR_REVISION_MISMATCH",
                ref _anchorRevisionMismatchCount);
            return;
        }

        if (ReadInt(state.Lane, _lanePendingExternalAnchorEpochField) !=
            state.ExternalAnchorEpoch)
        {
            RegisterScheduleMismatch(
                state,
                "EXTERNAL_ANCHOR_EPOCH_MISMATCH",
                ref _externalAnchorEpochMismatchCount);
            return;
        }

        if (ReadInt(state.Lane, _lanePendingTrackerGenerationField) !=
            state.TrackerGeneration)
        {
            RegisterScheduleMismatch(
                state,
                "TRACKER_GENERATION_MISMATCH",
                ref _trackerGenerationMismatchCount);
            return;
        }

        if (ReadInt(state.Lane, _lanePendingCameraGenerationField) !=
            state.CameraGeneration)
        {
            RegisterScheduleMismatch(
                state,
                "CAMERA_GENERATION_MISMATCH",
                ref _cameraGenerationMismatchCount);
            return;
        }

        if (ReadInt(state.Lane, _lanePendingTrackingSessionGenerationField) !=
            state.TrackingSessionGeneration)
        {
            RegisterScheduleMismatch(
                state,
                "TRACKING_SESSION_GENERATION_MISMATCH",
                ref _trackingSessionGenerationMismatchCount);
            return;
        }

        int currentMinimumPresenceBits = BitConverter.SingleToInt32Bits(
            ReadFloat(state.Lane, _lanePendingMinimumPresenceField));

        if (currentMinimumPresenceBits != state.MinimumPresenceBits)
        {
            RegisterScheduleMismatch(
                state,
                "MINIMUM_PRESENCE_MISMATCH",
                ref _minimumPresenceMismatchCount);
            return;
        }

        Matrix4x4 currentCropMatrix = ReadMatrix(
            state.Lane,
            _lanePendingCropMatrixField);

        if (!MatrixBitsEqual(currentCropMatrix, state.CropMatrixBits))
        {
            RegisterScheduleMismatch(
                state,
                "CROP_MATRIX_MISMATCH",
                ref _cropMatrixMismatchCount);
            return;
        }

        if (currentOutput == null || !currentOutput.IsReadbackRequestDone())
        {
            return;
        }

        object v24State = FindV24Capture(state.RecordIndex);

        if (v24State == null)
        {
            _snapshotBindingMissCount++;
            RegisterObserverInvalid(state, "V24_CAPTURE_MISSING_AT_OUTPUT_READY");
            return;
        }

        if (
            ReadBool(v24State, _v24StateFailedField) ||
            !ReadBool(
                v24State,
                _v24StateProductionSnapshotSubmittedField))
        {
            _snapshotBindingMissCount++;
            RegisterObserverInvalid(state, "V24_SNAPSHOT_NOT_SUBMITTED_FIRST");
            return;
        }

        if (!V24CaptureIdentityMatches(state, v24State))
        {
            _v24PairIdentityMismatchCount++;
            RegisterObserverInvalid(
                state,
                "V24_SNAPSHOT_SUBMISSION_IDENTITY_MISMATCH");
            return;
        }

        state.OutputReadyFrame = Time.frameCount;
        state.SnapshotSubmissionFrame = Time.frameCount;
        state.BoundaryB = true;
        state.BoundaryC = true;

        Debug.Log(
            "[Kiwi v44.55.25 ScheduleTrace] OUTPUT_READY_BOUND" +
            " recordIndex=" + state.RecordIndex +
            " sequence=" + state.Sequence +
            " pairToken=" + state.PairToken +
            " laneIndex=" + state.LaneIndex +
            " outputReadyFrame=" + state.OutputReadyFrame +
            " v24SnapshotSubmissionFrame=" +
            state.SnapshotSubmissionFrame +
            " boundaryB=1 boundaryC=1");
    }

    private void DetectMissedOutputReadyBoundaries()
    {
        foreach (KeyValuePair<int, TraceState> pair in _traces)
        {
            TraceState state = pair.Value;

            if (
                state == null ||
                state.Completed ||
                state.ObserverInvalid ||
                state.ScheduleMismatch ||
                state.BoundaryB)
            {
                continue;
            }

            if (
                !ReadBool(state.Lane, _laneReadbackPendingField) ||
                _lanePendingOutputField.GetValue(state.Lane) == null)
            {
                _outputReadyObservationMissCount++;
                RegisterObserverInvalid(
                    state,
                    "OUTPUT_READY_BOUNDARY_MISSED_BY_EXECUTION_ORDER");
            }
        }
    }

    private void BindCompletedV24Results()
    {
        IList results = _v24ResultsField.GetValue(_v24) as IList;

        if (results == null)
        {
            throw new InvalidOperationException("v24 results list unavailable.");
        }

        _dependencyPairCount = results.Count;
        _anomalousPairCount = 0;

        for (int i = 0; i < results.Count; i++)
        {
            object result = results[i];

            if (result == null)
            {
                RegisterMissingAttach(i, "V24_RESULT_NULL");
                continue;
            }

            int resultIndex = ReadInt(result, _v24ResultIndexField);
            int refMode2Ge4 = ReadInt(result, _v24ResultRefMode2Ge4Field);

            if (refMode2Ge4 > 0)
            {
                _anomalousPairCount++;
            }

            if (!_traces.TryGetValue(resultIndex, out TraceState state))
            {
                RegisterMissingAttach(resultIndex, "V24_RESULT_WITHOUT_TRACE");
                continue;
            }

            if (state.Completed)
            {
                continue;
            }

            bool identityMatches =
                resultIndex == state.RecordIndex &&
                ReadInt(result, _v24ResultPairTokenField) == state.PairToken &&
                ReadInt(result, _v24ResultRecordIndexField) ==
                    state.RecordIndex &&
                ReadULong(result, _v24ResultSequenceField) ==
                    state.Sequence &&
                ReadULong(result, _v24ResultSnapshotSequenceField) ==
                    state.Sequence;

            if (!identityMatches)
            {
                _v24PairIdentityMismatchCount++;
                RegisterObserverInvalid(state, "V24_RECORD_BIND_IDENTITY_MISMATCH");
            }

            if (
                !state.BoundaryB &&
                !state.ScheduleMismatch &&
                !state.ObserverInvalid)
            {
                _outputReadyObservationMissCount++;
                RegisterObserverInvalid(
                    state,
                    "V24_RECORD_BOUND_BEFORE_OUTPUT_READY_OBSERVATION");
            }

            if (!state.BoundaryC && !state.ObserverInvalid)
            {
                _snapshotBindingMissCount++;
                RegisterObserverInvalid(
                    state,
                    "V24_RECORD_BOUND_WITHOUT_SNAPSHOT_BINDING");
            }

            state.RecordCompletionFrame = ReadInt(
                result,
                _v24ResultRecordCompletionFrameField);
            state.Anomalous = refMode2Ge4 > 0;
            state.V24Classification =
                _v24ResultClassificationField.GetValue(result) as string ?? "-";
            state.Classification =
                state.ObserverInvalid
                    ? "OBSERVER_INVALID"
                    : state.ScheduleMismatch
                        ? "SCHEDULE_TRANSACTION_MISMATCH"
                        : state.CanonicalPublicationMismatch
                            ? "CANONICAL_PUBLICATION_MISMATCH"
                            : state.BoundaryD
                                ? "TRANSACTION_COHERENT"
                                : "CANONICAL_PUBLICATION_PENDING";
            state.Completed = true;
            _completedTraceCount++;

            Debug.Log(
                "[Kiwi v44.55.25 ScheduleTrace] RECORD_BOUND" +
                " recordIndex=" + state.RecordIndex +
                " sequence=" + state.Sequence +
                " pairToken=" + state.PairToken +
                " scheduleIdentityCoherent=" +
                B(state.BoundaryA && state.BoundaryB && !state.ScheduleMismatch) +
                " v24BindingCoherent=" +
                B(state.BoundaryC && identityMatches) +
                " canonicalPublicationCoherent=" +
                B(state.BoundaryD && !state.CanonicalPublicationMismatch) +
                " anomalous=" + B(state.Anomalous) +
                " classification=" + state.Classification);
        }
    }

    private void ObserveCanonicalPublication()
    {
        if (
            _runner == null ||
            !_runner.TryGetLatestPrecisionTrackingData(
                out FacePrecisionTrackingData data) ||
            !data.isValid ||
            data.frameId == 0UL ||
            data.frameId == _lastObservedPublishedFrameId)
        {
            return;
        }

        _lastObservedPublishedFrameId = data.frameId;

        // MediaPipe and InferenceEngine may publish the same camera source tick.
        // Only the latter can close this Production schedule transaction.
        if (data.backend != KiwiTrackingBackend.InferenceEngine)
        {
            return;
        }

        TraceState matched = null;
        int matchCount = 0;

        foreach (KeyValuePair<int, TraceState> pair in _traces)
        {
            TraceState state = pair.Value;

            if (
                state != null &&
                state.SourceHostTicks == data.submissionHostTicks)
            {
                matchCount++;
                matched = state;
            }
        }

        if (matchCount == 0)
        {
            return;
        }

        if (matchCount != 1 || matched == null)
        {
            _canonicalPublicationDuplicateCount++;
            RegisterGlobalObserverFault(
                "AMBIGUOUS_CANONICAL_PUBLICATION_BINDING hostTicks=" +
                data.submissionHostTicks +
                " matchCount=" + matchCount);
            return;
        }

        if (matched.BoundaryD)
        {
            _canonicalPublicationDuplicateCount++;
            matched.CanonicalPublicationMismatch = true;
            matched.FailureReason = "DUPLICATE_CANONICAL_PUBLICATION";
            return;
        }

        matched.CanonicalPublicationFrame = Time.frameCount;
        matched.CanonicalPublicationFrameId = data.frameId;
        matched.CanonicalPublicationTimestamp = data.timestamp;
        matched.CanonicalPublicationArrivalHostTicks = data.arrivalHostTicks;

        if (
            !data.hasMatchedSubmissionTiming ||
            data.submissionHostTicks <= 0L ||
            data.arrivalHostTicks <= 0L)
        {
            _canonicalPublicationIdentityInvalidCount++;
            matched.CanonicalPublicationMismatch = true;
            matched.Classification = "CANONICAL_PUBLICATION_IDENTITY_INVALID";
            matched.FailureReason = "CANONICAL_PUBLICATION_IDENTITY_INVALID";
            return;
        }

        matched.BoundaryD = true;

        if (matched.Completed && !matched.ScheduleMismatch && !matched.ObserverInvalid)
        {
            matched.Classification = "TRANSACTION_COHERENT";
        }

        Debug.Log(
            "[Kiwi v44.55.25 ScheduleTrace] CANONICAL_PUBLISH_BOUND" +
            " recordIndex=" + matched.RecordIndex +
            " sequence=" + matched.Sequence +
            " pairToken=" + matched.PairToken +
            " frameId=" + data.frameId +
            " sourceHostTicks=" + data.submissionHostTicks +
            " publicationTimestamp=" + data.timestamp +
            " publicationFrame=" + Time.frameCount +
            " boundaryD=1");
    }

    private void TryFinishAfterDependencies()
    {
        bool v20Done = ReadBool(_v20, _v20ReportWrittenField);
        bool v24Done = ReadBool(_v24, _v24ReportWrittenField);

        if (!v20Done || !v24Done)
        {
            _dependenciesCompleteSince = -1.0;
            return;
        }

        if (_dependenciesCompleteSince < 0.0)
        {
            _dependenciesCompleteSince = Time.realtimeSinceStartupAsDouble;
        }

        BindCompletedV24Results();

        if (
            _completedTraceCount == _dependencyPairCount &&
            CountPendingTraces() == 0 &&
            CountMissingCanonicalPublicationBindings() == 0)
        {
            WriteReport("COMPLETE");
            return;
        }

        if (
            Time.realtimeSinceStartupAsDouble -
            _dependenciesCompleteSince >= CompletionGraceSeconds)
        {
            MarkPendingTracesPartial("DEPENDENCY_COMPLETION_GRACE_EXPIRED");
            MarkMissingCanonicalPublicationBindings();
            WriteReport(
                CountPendingTraces() == 0
                    ? "COMPLETE"
                    : "INCOMPLETE");
        }
    }

    private int CountMissingCanonicalPublicationBindings()
    {
        int count = 0;

        foreach (KeyValuePair<int, TraceState> pair in _traces)
        {
            TraceState state = pair.Value;

            if (
                state != null &&
                state.Completed &&
                !state.ObserverInvalid &&
                !state.CanonicalPublicationMismatch &&
                !state.BoundaryD)
            {
                count++;
            }
        }

        return count;
    }

    private void MarkMissingCanonicalPublicationBindings()
    {
        foreach (KeyValuePair<int, TraceState> pair in _traces)
        {
            TraceState state = pair.Value;

            if (
                state == null ||
                !state.Completed ||
                state.ObserverInvalid ||
                state.CanonicalPublicationMismatch ||
                state.BoundaryD)
            {
                continue;
            }

            _canonicalPublicationMissCount++;
            state.Classification = "NO_CANONICAL_PUBLICATION_OBSERVED";
            state.FailureReason = "CANONICAL_PUBLICATION_NOT_OBSERVED";
        }
    }

    private object FindV24Capture(int index)
    {
        IDictionary captures = _v24CapturesField.GetValue(_v24) as IDictionary;

        if (captures == null || !captures.Contains(index))
        {
            return null;
        }

        return captures[index];
    }

    private bool V24CaptureIdentityMatches(
        TraceState state,
        object v24State)
    {
        return
            state != null &&
            v24State != null &&
            ReadInt(v24State, _v24StateIndexField) == state.RecordIndex &&
            ReadInt(v24State, _v24StatePairTokenField) == state.PairToken &&
            ReadULong(v24State, _v24StateSequenceField) == state.Sequence &&
            ReadLong(v24State, _v24StateNativeHostTicksField) ==
                state.NativeHostTicks &&
            ReadLong(v24State, _v24StateManagedHostTicksField) ==
                state.ManagedHostTicks &&
            ReadLong(v24State, _v24StateLaneStartedHostTicksField) ==
                state.LaneStartedHostTicks &&
            object.ReferenceEquals(
                _v24StateProductionLaneField.GetValue(v24State),
                state.Lane);
    }

    private void RegisterMissingAttach(int index, string reason)
    {
        if (_missingTraceIndices.Add(index))
        {
            _scheduleAttachMissCount++;
            _observerFaultCount++;
            Debug.LogWarning(
                "[Kiwi v44.55.25 ScheduleTrace] OBSERVER_FAULT " +
                reason +
                " recordIndex=" + index);
        }
    }

    private void RegisterScheduleMismatch(
        TraceState state,
        string reason,
        ref int counter)
    {
        if (state == null || state.ScheduleMismatch || state.ObserverInvalid)
        {
            return;
        }

        counter++;
        state.ScheduleMismatch = true;
        state.FailureReason = reason;

        Debug.LogWarning(
            "[Kiwi v44.55.25 ScheduleTrace] SCHEDULE_MISMATCH " +
            reason +
            " recordIndex=" + state.RecordIndex +
            " pairToken=" + state.PairToken);
    }

    private void RegisterObserverInvalid(TraceState state, string reason)
    {
        if (state == null || state.ObserverInvalid)
        {
            return;
        }

        state.ObserverInvalid = true;
        state.FailureReason = reason;
        _observerFaultCount++;

        Debug.LogWarning(
            "[Kiwi v44.55.25 ScheduleTrace] OBSERVER_FAULT " +
            reason +
            " recordIndex=" + state.RecordIndex +
            " pairToken=" + state.PairToken);
    }

    private void RegisterGlobalObserverFault(string reason)
    {
        _observerFaultCount++;
        Debug.LogWarning(
            "[Kiwi v44.55.25 ScheduleTrace] OBSERVER_FAULT " + reason);
    }

    private int CountPendingTraces()
    {
        int count = 0;

        foreach (KeyValuePair<int, TraceState> pair in _traces)
        {
            if (pair.Value != null && !pair.Value.Completed)
            {
                count++;
            }
        }

        return count;
    }

    private void MarkPendingTracesPartial(string reason)
    {
        foreach (KeyValuePair<int, TraceState> pair in _traces)
        {
            TraceState state = pair.Value;

            if (state == null || state.Completed)
            {
                continue;
            }

            _partialTraceCount++;
            RegisterObserverInvalid(state, reason);
        }
    }

    private string DetermineDependencyStatus()
    {
        if (!_bound || _v20 == null || _v24 == null)
        {
            return "NOT_BOUND";
        }

        if (
            !ReadBool(_v20, _v20ReportWrittenField) ||
            !ReadBool(_v24, _v24ReportWrittenField))
        {
            return "PENDING";
        }

        if (
            ReadInt(_v20, _v20ObserverFaultCountField) != 0 ||
            ReadInt(_v24, _v24ObserverFaultCountField) != 0)
        {
            return "INVALID";
        }

        return "COMPLETE";
    }

    private string DetermineDecision()
    {
        bool observerInvalid =
            DetermineDependencyStatus() != "COMPLETE" ||
            _observerFaultCount != 0 ||
            _scheduleAttachMissCount != 0 ||
            _ambiguousLaneMatchCount != 0 ||
            _outputReadyObservationMissCount != 0 ||
            _snapshotBindingMissCount != 0 ||
            _canonicalPublicationDuplicateCount != 0 ||
            _duplicateTraceCount != 0 ||
            _partialTraceCount != 0 ||
            CountPendingTraces() != 0 ||
            _completedTraceCount != _dependencyPairCount;

        if (observerInvalid)
        {
            return "OBSERVER_INVALID";
        }

        bool anomalousScheduleMismatch = false;
        bool anyScheduleMismatch = false;

        foreach (KeyValuePair<int, TraceState> pair in _traces)
        {
            TraceState state = pair.Value;

            if (state == null || !state.ScheduleMismatch)
            {
                continue;
            }

            anyScheduleMismatch = true;

            if (state.Anomalous)
            {
                anomalousScheduleMismatch = true;
            }
        }

        if (anomalousScheduleMismatch)
        {
            return "SCHEDULE_TRANSACTION_MISMATCH";
        }

        if (anyScheduleMismatch)
        {
            return "OBSERVER_INVALID";
        }

        if (_canonicalPublicationIdentityInvalidCount != 0)
        {
            return "CANONICAL_PUBLICATION_IDENTITY_INVALID";
        }

        int publishedAnomalousCount =
            CountCanonicalPublicationBindings(true);

        bool coherentGate =
            _completedTraceCount >= MinimumCompletedTraces &&
            _dependencyPairCount == _completedTraceCount &&
            _anomalousPairCount >= MinimumAnomalousPairs &&
            ScheduleMismatchCounterTotal() == 0;

        if (!coherentGate)
        {
            return "OBSERVER_INVALID";
        }

        if (_canonicalPublicationMissCount == 0)
        {
            return "TRANSACTION_COHERENT";
        }

        return publishedAnomalousCount >= MinimumAnomalousPairs
            ? "PUBLISHED_TRANSACTION_COHERENT_PARTIAL_COVERAGE"
            : "INSUFFICIENT_PUBLISHED_ANOMALOUS_DATA";
    }

    private int CountCanonicalPublicationBindings(bool anomalousOnly)
    {
        int count = 0;

        foreach (KeyValuePair<int, TraceState> pair in _traces)
        {
            TraceState state = pair.Value;

            if (
                state != null &&
                state.BoundaryD &&
                !state.CanonicalPublicationMismatch &&
                (!anomalousOnly || state.Anomalous))
            {
                count++;
            }
        }

        return count;
    }

    private int ScheduleMismatchCounterTotal()
    {
        return
            _laneIdentityMismatchCount +
            _workerIdentityMismatchCount +
            _inputTensorIdentityMismatchCount +
            _pendingOutputIdentityMismatchCount +
            _sourceHostTicksMismatchCount +
            _scheduleBeginTicksMismatchCount +
            _startedHostTicksMismatchCount +
            _readbackRequestTicksMismatchCount +
            _readbackRequestFrameMismatchCount +
            _anchorRevisionMismatchCount +
            _externalAnchorEpochMismatchCount +
            _trackerGenerationMismatchCount +
            _cameraGenerationMismatchCount +
            _trackingSessionGenerationMismatchCount +
            _minimumPresenceMismatchCount +
            _cropMatrixMismatchCount +
            _v24PairIdentityMismatchCount;
    }

    private void WriteReport(string status)
    {
        if (_reportWritten)
        {
            return;
        }

        _reportWritten = true;
        string decision = DetermineDecision();
        string directory = Path.Combine(
            Application.persistentDataPath,
            "KiwiFrameBottleneck");
        Directory.CreateDirectory(directory);

        string stamp = DateTime.Now.ToString(
            "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture);
        string textPath = Path.Combine(
            directory,
            "KiwiProductionScheduleTransactionTrace_v44_55_25_" +
            stamp +
            ".txt");
        string csvPath = Path.Combine(
            directory,
            "KiwiProductionScheduleTransactionTrace_v44_55_25_" +
            stamp +
            ".csv");

        List<string> lines = new List<string>();
        lines.Add(
            "KiwiAvatarSystem v44.55.25 Production Schedule Transaction Trace");
        lines.Add("contract=" + Contract);
        lines.Add("status=" + status);
        lines.Add("decision=" + decision);
        lines.Add("dependencyStatus=" + DetermineDependencyStatus());
        lines.Add("observerOnly=1");
        lines.Add("productionWrites=0");
        lines.Add("productionInputContentReadback=0");
        lines.Add("productionOutputContentReadbackAdded=0");
        lines.Add("repeatedInference=0");
        lines.Add("newWorker=0");
        lines.Add("blockingWait=0");
        lines.Add("newGpuSnapshot=0");
        lines.Add("v24SnapshotStateComposed=1");
        lines.Add("referenceIdentityAuthority=ReferenceEquals");
        lines.Add("objectTokensDiagnosticOnly=1");
        lines.Add("cropMatrixComparison=BITWISE_ALL_16_FLOATS");
        lines.Add("minimumPresenceComparison=BITWISE_FLOAT32");
        lines.Add("performanceAuthority=0");
        lines.Add("thresholdsLockedBeforeRuntime=1");
        lines.Add("");
        lines.Add("[COUNTS]");
        lines.Add("completedTraceCount=" + _completedTraceCount);
        lines.Add("dependencyPairCount=" + _dependencyPairCount);
        lines.Add("anomalousPairCount=" + _anomalousPairCount);
        lines.Add("scheduleAttachMissCount=" + _scheduleAttachMissCount);
        lines.Add("ambiguousLaneMatchCount=" + _ambiguousLaneMatchCount);
        lines.Add("laneIdentityMismatchCount=" + _laneIdentityMismatchCount);
        lines.Add("workerIdentityMismatchCount=" + _workerIdentityMismatchCount);
        lines.Add(
            "inputTensorIdentityMismatchCount=" +
            _inputTensorIdentityMismatchCount);
        lines.Add(
            "pendingOutputIdentityMismatchCount=" +
            _pendingOutputIdentityMismatchCount);
        lines.Add("sourceHostTicksMismatchCount=" + _sourceHostTicksMismatchCount);
        lines.Add(
            "scheduleBeginTicksMismatchCount=" +
            _scheduleBeginTicksMismatchCount);
        lines.Add("startedHostTicksMismatchCount=" + _startedHostTicksMismatchCount);
        lines.Add(
            "readbackRequestTicksMismatchCount=" +
            _readbackRequestTicksMismatchCount);
        lines.Add(
            "readbackRequestFrameMismatchCount=" +
            _readbackRequestFrameMismatchCount);
        lines.Add("anchorRevisionMismatchCount=" + _anchorRevisionMismatchCount);
        lines.Add(
            "externalAnchorEpochMismatchCount=" +
            _externalAnchorEpochMismatchCount);
        lines.Add(
            "trackerGenerationMismatchCount=" +
            _trackerGenerationMismatchCount);
        lines.Add("cameraGenerationMismatchCount=" + _cameraGenerationMismatchCount);
        lines.Add(
            "trackingSessionGenerationMismatchCount=" +
            _trackingSessionGenerationMismatchCount);
        lines.Add(
            "minimumPresenceMismatchCount=" +
            _minimumPresenceMismatchCount);
        lines.Add("cropMatrixMismatchCount=" + _cropMatrixMismatchCount);
        lines.Add(
            "v24PairIdentityMismatchCount=" +
            _v24PairIdentityMismatchCount);
        lines.Add(
            "outputReadyObservationMissCount=" +
            _outputReadyObservationMissCount);
        lines.Add("snapshotBindingMissCount=" + _snapshotBindingMissCount);
        lines.Add(
            "canonicalPublicationMissCount=" +
            _canonicalPublicationMissCount);
        lines.Add(
            "canonicalPublicationIdentityInvalidCount=" +
            _canonicalPublicationIdentityInvalidCount);
        lines.Add(
            "canonicalPublicationBoundCount=" +
            CountCanonicalPublicationBindings(false));
        lines.Add(
            "canonicalPublicationBoundAnomalousCount=" +
            CountCanonicalPublicationBindings(true));
        lines.Add(
            "canonicalPublicationDuplicateCount=" +
            _canonicalPublicationDuplicateCount);
        lines.Add("duplicateTraceCount=" + _duplicateTraceCount);
        lines.Add("partialTraceCount=" + _partialTraceCount);
        lines.Add("observerFaultCount=" + _observerFaultCount);
        lines.Add("tracesPending=" + CountPendingTraces());
        lines.Add("");
        lines.Add("[FIXED_RUNTIME_GATE]");
        lines.Add("dependencyStatusRequired=COMPLETE");
        lines.Add("minimumCompletedTraceCount=" + MinimumCompletedTraces);
        lines.Add("dependencyPairCountMustEqualCompletedTraceCount=1");
        lines.Add("minimumAnomalousPairCount=" + MinimumAnomalousPairs);
        lines.Add("observerIntegrityCountersMustBeZero=1");
        lines.Add("findingCountersMustMatchDecision=1");
        lines.Add("");
        lines.Add("[DECISION_RULE]");
        lines.Add(
            "TRANSACTION_COHERENT=all valid pairs have exact pair+schedule+pendingOutput+v24 snapshot binding coherence");
        lines.Add(
            "SCHEDULE_TRANSACTION_MISMATCH=at least one valid anomalous pair has a schedule/output transaction mismatch");
        lines.Add(
            "PUBLISHED_TRANSACTION_COHERENT_PARTIAL_COVERAGE=all observed InferenceEngine publishes bind exactly; some v24 packed outputs were not observed as canonical publishes");
        lines.Add(
            "INSUFFICIENT_PUBLISHED_ANOMALOUS_DATA=fewer than the fixed minimum anomalous pairs were observed as InferenceEngine canonical publishes");
        lines.Add(
            "CANONICAL_PUBLICATION_IDENTITY_INVALID=an observed InferenceEngine publication has invalid matched timing identity");
        lines.Add(
            "OBSERVER_INVALID=observer miss, ambiguity, lifecycle, ordering, dependency, or validator failure");

        File.WriteAllLines(textPath, lines.ToArray());
        WriteCsv(csvPath);

        Debug.Log(
            "[Kiwi v44.55.25 ScheduleTrace] COMPLETE" +
            " status=" + status +
            " decision=" + decision +
            " traces=" + _completedTraceCount +
            " dependencyPairs=" + _dependencyPairCount +
            " anomalousPairs=" + _anomalousPairCount +
            " observerFaults=" + _observerFaultCount +
            " pending=" + CountPendingTraces() +
            " report=" + textPath +
            " csv=" + csvPath);
    }

    private void WriteCsv(string path)
    {
        List<TraceState> rows = new List<TraceState>();

        foreach (KeyValuePair<int, TraceState> pair in _traces)
        {
            if (pair.Value != null)
            {
                rows.Add(pair.Value);
            }
        }

        rows.Sort((left, right) => left.RecordIndex.CompareTo(right.RecordIndex));

        List<string> lines = new List<string>();
        lines.Add(
            "recordIndex,sequence,pairToken,laneIndex,nativeHostTicks,managedHostTicks,laneStartedHostTicks," +
            "sourceHostTicks,scheduleBeginHostTicks,startedHostTicks,readbackRequestHostTicks,readbackRequestFrame," +
            "anchorRevision,externalAnchorEpoch,trackerGeneration,cameraGeneration,trackingSessionGeneration," +
            "minimumPresenceBits,cropMatrixBits,laneIdentityToken,workerIdentityToken,inputTensorIdentityToken," +
            "pendingOutputIdentityToken,pairAttachFrame,outputReadyFrame,snapshotSubmissionFrame,recordCompletionFrame," +
            "canonicalPublicationFrame,canonicalPublicationFrameId,canonicalPublicationTimestamp," +
            "canonicalPublicationArrivalHostTicks,scheduleIdentityCoherent,v24BindingCoherent," +
            "canonicalPublicationCoherent,anomalous,v24Classification,classification,failureReason");

        for (int i = 0; i < rows.Count; i++)
        {
            TraceState state = rows[i];
            lines.Add(
                state.RecordIndex + "," +
                state.Sequence + "," +
                state.PairToken + "," +
                state.LaneIndex + "," +
                state.NativeHostTicks + "," +
                state.ManagedHostTicks + "," +
                state.LaneStartedHostTicks + "," +
                state.SourceHostTicks + "," +
                state.ScheduleBeginHostTicks + "," +
                state.StartedHostTicks + "," +
                state.ReadbackRequestHostTicks + "," +
                state.ReadbackRequestFrame + "," +
                state.AnchorRevision + "," +
                state.ExternalAnchorEpoch + "," +
                state.TrackerGeneration + "," +
                state.CameraGeneration + "," +
                state.TrackingSessionGeneration + "," +
                Hex32(state.MinimumPresenceBits) + "," +
                Csv(FormatMatrixBits(state.CropMatrixBits)) + "," +
                state.LaneIdentityToken + "," +
                state.WorkerIdentityToken + "," +
                state.InputTensorIdentityToken + "," +
                state.PendingOutputIdentityToken + "," +
                state.PairAttachFrame + "," +
                state.OutputReadyFrame + "," +
                state.SnapshotSubmissionFrame + "," +
                state.RecordCompletionFrame + "," +
                state.CanonicalPublicationFrame + "," +
                state.CanonicalPublicationFrameId + "," +
                state.CanonicalPublicationTimestamp + "," +
                state.CanonicalPublicationArrivalHostTicks + "," +
                B(state.BoundaryA && state.BoundaryB && !state.ScheduleMismatch) +
                "," +
                B(state.BoundaryC && !state.ObserverInvalid) + "," +
                B(state.BoundaryD && !state.CanonicalPublicationMismatch) + "," +
                B(state.Anomalous) + "," +
                Csv(state.V24Classification) + "," +
                Csv(state.Classification) + "," +
                Csv(state.FailureReason));
        }

        File.WriteAllLines(path, lines.ToArray());
    }

    private int GetObjectToken(object value)
    {
        if (value == null)
        {
            return 0;
        }

        if (_objectTokens.TryGetValue(value, out int token))
        {
            return token;
        }

        token = _nextObjectToken++;
        _objectTokens.Add(value, token);
        return token;
    }

    private static void CaptureMatrixBits(Matrix4x4 matrix, int[] destination)
    {
        if (destination == null || destination.Length != 16)
        {
            throw new ArgumentException("Matrix bit destination must have 16 entries.");
        }

        for (int i = 0; i < 16; i++)
        {
            destination[i] = BitConverter.SingleToInt32Bits(matrix[i]);
        }
    }

    private static bool MatrixBitsEqual(Matrix4x4 matrix, int[] expected)
    {
        if (expected == null || expected.Length != 16)
        {
            return false;
        }

        for (int i = 0; i < 16; i++)
        {
            if (BitConverter.SingleToInt32Bits(matrix[i]) != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatMatrixBits(int[] bits)
    {
        if (bits == null || bits.Length != 16)
        {
            return "INVALID";
        }

        string[] values = new string[16];

        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Hex32(bits[i]);
        }

        return string.Join("|", values);
    }

    private static string Hex32(int value)
    {
        return unchecked((uint)value).ToString(
            "X8",
            CultureInfo.InvariantCulture);
    }

    private static FieldInfo RequiredField(Type type, string name)
    {
        if (type == null)
        {
            throw new MissingFieldException("Null type for field " + name);
        }

        FieldInfo[] fields = type.GetFields(InstanceFlags);
        FieldInfo match = null;
        int count = 0;

        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].Name == name)
            {
                match = fields[i];
                count++;
            }
        }

        if (count != 1 || match == null)
        {
            throw new MissingFieldException(
                type.FullName + "." + name + " count=" + count);
        }

        return match;
    }

    private static Type RequiredNestedType(Type owner, string name)
    {
        Type[] types = owner.GetNestedTypes(
            BindingFlags.Public | BindingFlags.NonPublic);
        Type match = null;
        int count = 0;

        for (int i = 0; i < types.Length; i++)
        {
            if (types[i].Name == name)
            {
                match = types[i];
                count++;
            }
        }

        if (count != 1 || match == null)
        {
            throw new TypeLoadException(
                owner.FullName + "+" + name + " count=" + count);
        }

        return match;
    }

    private static object ReadMember(object owner, string name)
    {
        if (owner == null)
        {
            return null;
        }

        Type type = owner.GetType();
        FieldInfo field = type.GetField(name, InstanceFlags);

        if (field != null)
        {
            return field.GetValue(owner);
        }

        PropertyInfo property = type.GetProperty(name, InstanceFlags);
        return property != null ? property.GetValue(owner, null) : null;
    }

    private static bool ReadBool(object owner, FieldInfo field)
    {
        object value = field != null && owner != null
            ? field.GetValue(owner)
            : null;
        return value is bool flag && flag;
    }

    private static int ReadInt(object owner, FieldInfo field)
    {
        object value = field != null && owner != null
            ? field.GetValue(owner)
            : null;
        return value is int number ? number : 0;
    }

    private static long ReadLong(object owner, FieldInfo field)
    {
        object value = field != null && owner != null
            ? field.GetValue(owner)
            : null;
        return value is long number ? number : 0L;
    }

    private static ulong ReadULong(object owner, FieldInfo field)
    {
        object value = field != null && owner != null
            ? field.GetValue(owner)
            : null;
        return value is ulong number ? number : 0UL;
    }

    private static float ReadFloat(object owner, FieldInfo field)
    {
        object value = field != null && owner != null
            ? field.GetValue(owner)
            : null;
        return value is float number ? number : 0f;
    }

    private static Matrix4x4 ReadMatrix(object owner, FieldInfo field)
    {
        object value = field != null && owner != null
            ? field.GetValue(owner)
            : null;
        return value is Matrix4x4 matrix ? matrix : default;
    }

    private static int ReadMemberInt(object owner, string name)
    {
        object value = ReadMember(owner, name);
        return value is int number ? number : 0;
    }

    private static long ReadMemberLong(object owner, string name)
    {
        object value = ReadMember(owner, name);
        return value is long number ? number : 0L;
    }

    private static ulong ReadMemberULong(object owner, string name)
    {
        object value = ReadMember(owner, name);
        return value is ulong number ? number : 0UL;
    }

    private static bool ReadBoolEnvironment(string name, bool fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        value = value.Trim();
        return
            value == "1" ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static string B(bool value)
    {
        return value ? "1" : "0";
    }

    private static string Csv(string value)
    {
        string safe = value ?? string.Empty;

        if (
            safe.IndexOf(',') < 0 &&
            safe.IndexOf('"') < 0 &&
            safe.IndexOf('\n') < 0 &&
            safe.IndexOf('\r') < 0)
        {
            return safe;
        }

        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    private void OnDisable()
    {
        if (!_reportWritten && (_bound || _traces.Count > 0))
        {
            try
            {
                MarkPendingTracesPartial("OBSERVER_DISABLED");
                WriteReport("STOPPED_BEFORE_COMPLETE");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "[Kiwi v44.55.25 ScheduleTrace] STOP_REPORT_FAIL " +
                    exception.GetType().Name +
                    " " +
                    exception.Message);
            }
        }

        _traces.Clear();
        _missingTraceIndices.Clear();
        _objectTokens.Clear();
        _v20 = null;
        _v24 = null;
        _bound = false;
    }

    private void OnDestroy()
    {
        _installed = false;
    }
}

/// <summary>
/// Runs after the v44.55.24 early snapshot probe and before ordinary Production
/// Update. This ordering composes v24 snapshot submission with the still-intact
/// Production lane transaction without adding a snapshot or consuming output.
/// </summary>
[DefaultExecutionOrder(-32000)]
internal sealed class KiwiProductionScheduleTransactionEarlyProbeV44_55_25
    : MonoBehaviour
{
    internal KiwiProductionScheduleTransactionTraceV44_55_25 Owner;

    private void Update()
    {
        Owner?.PollAfterV24BeforeProductionUpdate();
    }
}
