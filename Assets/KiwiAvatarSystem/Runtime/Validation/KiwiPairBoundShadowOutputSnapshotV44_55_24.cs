using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

/// <summary>
/// KiwiAvatarSystem v44.55.24
/// Pair-Bound Shadow Output Snapshot.
///
/// Observer-only follow-up to the valid-but-provisional v44.55.23 result.
///
/// Authority strategy:
/// 1. Attach to the exact v44.55.20 Production lane by managed host ticks and
///    lane-start host ticks.
/// 2. Wait for the existing Production pendingOutput readback request to report
///    completion. Before Production Update releases/reuses the lane, snapshot
///    the completed packed-output GPU buffer into an observer-owned GPU buffer.
/// 3. While the exact v44.55.20 pair is still pending and its common-tensor
///    workers are scheduled, snapshot REF_FROZEN_GPU and B_GPU output buffers.
///    Queue the copy commands after Worker.Schedule flushed the worker writes
///    and before a later pair can replace the Worker output.
/// 4. Compare all 1405 packed float values bit-for-bit. No numeric matching
///    threshold is invented after runtime.
/// 5. v44.55.20 REF-vs-mode2 input >=4 LSB remains the anomaly selector only.
///
/// No Production/Native/Tracking/ROI/threshold write is performed. No
/// Production worker/tensor/readback state is consumed or modified. Added work
/// is correctness-only: three small observer GPU snapshots + async readbacks per
/// measured pair. Performance authority remains zero.
/// </summary>
[DefaultExecutionOrder(34000)]
internal sealed class KiwiPairBoundShadowOutputSnapshotV44_55_24
    : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_55_24_PAIR_BOUND_SHADOW_OUTPUT_SNAPSHOT";

    private const string EnableVariable =
        "KIWI_V44_55_24_PAIR_BOUND_SHADOW_OUTPUT_AUDIT";

    private const int InputFloatCount =
        192 * 192 * 3;

    private const int PackedOutputLength =
        468 * 3 + 1;

    private const int SnapshotThreads =
        256;

    private const string SnapshotShaderResource =
        "KiwiValidation/KiwiOutputSnapshotCopyV44_55_24";

    private const int MinimumCompletedPairs =
        60;

    private const int MinimumAnomalousPairs =
        3;

    private const double PairMeanToleranceLsb =
        0.000001;

    private const double DependencyCompletionGraceSeconds =
        8.0;

    private static readonly BindingFlags InstanceFlags =
        BindingFlags.Instance |
        BindingFlags.Public |
        BindingFlags.NonPublic;

    private static bool _installed;

    private KiwiCommonTensorBackendStageIsolationV44_55_20 _dependency;
    private Type _dependencyType;

    private FieldInfo _pairPendingField;
    private FieldInfo _pairTokenField;
    private FieldInfo _commonScheduledField;
    private FieldInfo _pendingRecordField;
    private FieldInfo _recordsField;
    private FieldInfo _reportWrittenField;
    private FieldInfo _observerFaultCountField;
    private FieldInfo _sourceIdentityMismatchCountField;
    private FieldInfo _referenceTensorNonFiniteCountField;
    private FieldInfo _referenceInputReadbackFailureCountField;

    private FieldInfo _referenceNchwField;
    private FieldInfo _mode2NchwField;
    private FieldInfo _referenceFrozenGpuWorkerField;
    private FieldInfo _bGpuWorkerField;
    private FieldInfo _lanesField;

    private FieldInfo _lanePendingOutputField;
    private FieldInfo _laneReadbackPendingField;
    private FieldInfo _lanePendingSourceHostTicksField;
    private FieldInfo _lanePendingStartedHostTicksField;

    private ComputeShader _snapshotShader;
    private int _snapshotKernel = -1;

    private bool _bound;
    private bool _reportWritten;

    private int _observerFaultCount;
    private int _dependencyMissingFrames;
    private int _attachMissCount;
    private int _recordCaptureWindowMissCount;
    private int _shadowOutputCaptureMissCount;
    private int _shadowIdentityMismatchCount;
    private int _duplicateShadowSnapshotCount;
    private int _nextPairTokenObservedBeforeRecordBindCount;
    private int _productionOutputSnapshotFailureCount;
    private int _referenceOutputSnapshotFailureCount;
    private int _mode2OutputSnapshotFailureCount;
    private int _nonFiniteOutputCount;
    private int _inputArrayIdentityMismatchCount;
    private int _outputShapeMismatchCount;
    private int _duplicatePairCount;

    private int _completedPairCount;
    private int _anomalousPairCount;
    private int _referenceExactMatchCount;
    private int _mode2ExactMatchCount;
    private int _bothExactCount;
    private int _neitherExactCount;

    private int _lastAttachedIndex = -1;
    private int _lastRecordCapturedIndex = -1;

    private double _dependencyCompleteSince = -1.0;

    private readonly Dictionary<int, CaptureState> _captures =
        new Dictionary<int, CaptureState>();

    private readonly HashSet<int> _attachedIndices =
        new HashSet<int>();

    private readonly Dictionary<int, int> _shadowTokenOwners =
        new Dictionary<int, int>();

    private readonly List<PairResult> _results =
        new List<PairResult>(160);

    private sealed class CaptureState
    {
        internal int Index;
        internal int PairToken;
        internal ulong Sequence;
        internal long NativeHostTicks;
        internal long ManagedHostTicks;
        internal long LaneStartedHostTicks;
        internal object ProductionLane;

        internal bool ProductionSnapshotSubmitted;
        internal bool ProductionSnapshotDone;
        internal bool ReferenceSnapshotSubmitted;
        internal bool ReferenceSnapshotDone;
        internal bool Mode2SnapshotSubmitted;
        internal bool Mode2SnapshotDone;
        internal bool DependencyRecordCaptured;
        internal bool InputArraysCaptured;
        internal bool Failed;

        internal int ShadowSnapshotPairToken;
        internal int ShadowSnapshotRecordIndex;
        internal ulong ShadowSnapshotSequence;
        internal int ReferenceSnapshotFrame = -1;
        internal int Mode2SnapshotFrame = -1;
        internal int RecordCompletionFrame = -1;
        internal bool NextPairTokenObservedBeforeRecordBind;

        internal int RefMode2Ge4;
        internal double RefMode2MeanAbsLsb;
        internal double DependencyRecordedMeanAbsLsb;

        internal long AttachTicks;
        internal long ProductionOutputReadyTicks;
        internal long DependencyRecordCaptureTicks;

        internal ComputeBuffer ProductionSnapshotBuffer;
        internal ComputeBuffer ReferenceSnapshotBuffer;
        internal ComputeBuffer Mode2SnapshotBuffer;

        internal readonly float[] ProductionOutput =
            new float[PackedOutputLength];

        internal readonly float[] ReferenceOutput =
            new float[PackedOutputLength];

        internal readonly float[] Mode2Output =
            new float[PackedOutputLength];
    }

    private sealed class PairResult
    {
        internal int Index;
        internal int ShadowSnapshotPairToken;
        internal int ShadowSnapshotRecordIndex;
        internal ulong Sequence;
        internal ulong ShadowSnapshotSequence;
        internal int ReferenceSnapshotFrame;
        internal int Mode2SnapshotFrame;
        internal int RecordCompletionFrame;
        internal bool NextPairTokenObservedBeforeRecordBind;
        internal int RefMode2Ge4;
        internal double RefMode2MeanAbsLsb;
        internal double ProductionVsReferenceMeanAbs;
        internal double ProductionVsReferenceMaxAbs;
        internal double ProductionVsMode2MeanAbs;
        internal double ProductionVsMode2MaxAbs;
        internal double ReferenceVsMode2MeanAbs;
        internal double ReferenceVsMode2MaxAbs;
        internal int ProductionVsReferenceExactCount;
        internal int ProductionVsMode2ExactCount;
        internal int ReferenceVsMode2ExactCount;
        internal bool ProductionEqualsReferenceBitwise;
        internal bool ProductionEqualsMode2Bitwise;
        internal bool ReferenceEqualsMode2Bitwise;
        internal string Classification;
    }

    private struct OutputParity
    {
        internal int ExactCount;
        internal double MeanAbs;
        internal double MaxAbs;
        internal int NonFiniteCount;
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed)
        {
            return;
        }

        if (!ReadBoolEnvironment(
                EnableVariable,
                false))
        {
            return;
        }

        _installed = true;

        GameObject go =
            new GameObject(
                "[Kiwi] v44.55.24 Pair-Bound Shadow Output Snapshot");

        DontDestroyOnLoad(go);
        go.hideFlags =
            HideFlags.DontSave;

        KiwiPairBoundShadowOutputSnapshotV44_55_24 observer =
            go.AddComponent<
                KiwiPairBoundShadowOutputSnapshotV44_55_24>();

        KiwiPairBoundShadowOutputEarlyProbeV44_55_24 earlyProbe =
            go.AddComponent<
                KiwiPairBoundShadowOutputEarlyProbeV44_55_24>();

        earlyProbe.Owner =
            observer;
    }

    private void Awake()
    {
        Debug.Log(
            "[Kiwi v44.55.24 PairBoundOutput] WAIT_DEPENDENCY" +
            " contract=" + Contract +
            " observerOnly=1" +
            " productionWrites=0" +
            " nativeWrites=0" +
            " trackingMathChange=0" +
            " roiChange=0" +
            " thresholdChange=0" +
            " productionInputReadback=0" +
            " productionOutputStateConsumption=0" +
            " pairBoundShadowSnapshot=1" +
            " blockingWait=0" +
            " performanceAuthority=0");
    }

    private void Update()
    {
        if (_reportWritten)
        {
            return;
        }

        if (!_bound)
        {
            TryBindDependency();

            if (!_bound)
            {
                _dependencyMissingFrames++;
                return;
            }
        }

        CaptureCompletedDependencyRecordWindow();
        ObservePendingDependencyPair();
        TryFinalizeReadyPairs();
        TryFinishAfterDependency();
    }

    private void LateUpdate()
    {
        if (
            _reportWritten ||
            !_bound)
        {
            return;
        }

        CaptureCompletedDependencyRecordWindow();
        ObservePendingDependencyPair();
        TryFinalizeReadyPairs();
    }

    internal void PollProductionOutputBeforeProductionUpdate()
    {
        if (
            _reportWritten ||
            !_bound ||
            _captures.Count == 0)
        {
            return;
        }

        foreach (
            KeyValuePair<int, CaptureState> pair
            in _captures)
        {
            CaptureState state =
                pair.Value;

            if (
                state == null ||
                state.Failed ||
                state.ProductionSnapshotSubmitted)
            {
                continue;
            }

            try
            {
                if (!ProductionLaneMatches(
                        state))
                {
                    continue;
                }

                Tensor<float> pendingOutput =
                    _lanePendingOutputField.GetValue(
                        state.ProductionLane)
                        as Tensor<float>;

                if (
                    pendingOutput == null ||
                    !ReadFieldBool(
                        state.ProductionLane,
                        _laneReadbackPendingField))
                {
                    continue;
                }

                if (!pendingOutput.IsReadbackRequestDone())
                {
                    continue;
                }

                if (!ProductionLaneMatches(
                        state))
                {
                    RegisterFault(
                        "PRODUCTION_LANE_CHANGED_AT_OUTPUT_READY index=" +
                        state.Index);

                    state.Failed =
                        true;

                    continue;
                }

                state.ProductionOutputReadyTicks =
                    Stopwatch.GetTimestamp();

                SubmitTensorSnapshot(
                    state,
                    pendingOutput,
                    SnapshotKind.Production);
            }
            catch (Exception exception)
            {
                _productionOutputSnapshotFailureCount++;

                state.Failed =
                    true;

                RegisterFault(
                    "PRODUCTION_OUTPUT_SNAPSHOT_FAIL index=" +
                    state.Index +
                    " " +
                    exception.GetType().Name +
                    " " +
                    exception.Message);
            }
        }
    }

    private void TryBindDependency()
    {
        KiwiCommonTensorBackendStageIsolationV44_55_20[] candidates =
            Resources.FindObjectsOfTypeAll<
                KiwiCommonTensorBackendStageIsolationV44_55_20>();

        _dependency =
            candidates != null &&
            candidates.Length == 1
                ? candidates[0]
                : null;

        if (_dependency == null)
        {
            return;
        }

        try
        {
            _dependencyType =
                _dependency.GetType();

            _pairPendingField =
                RequiredField(
                    _dependencyType,
                    "_pairPending");

            _pairTokenField =
                RequiredField(
                    _dependencyType,
                    "_pairToken");

            _commonScheduledField =
                RequiredField(
                    _dependencyType,
                    "_commonScheduled");

            _pendingRecordField =
                RequiredField(
                    _dependencyType,
                    "_pendingRecord");

            _recordsField =
                RequiredField(
                    _dependencyType,
                    "_records");

            _reportWrittenField =
                RequiredField(
                    _dependencyType,
                    "_reportWritten");

            _observerFaultCountField =
                RequiredField(
                    _dependencyType,
                    "_observerFaultCount");

            _sourceIdentityMismatchCountField =
                RequiredField(
                    _dependencyType,
                    "_sourceIdentityMismatchCount");

            _referenceTensorNonFiniteCountField =
                RequiredField(
                    _dependencyType,
                    "_referenceTensorNonFiniteCount");

            _referenceInputReadbackFailureCountField =
                RequiredField(
                    _dependencyType,
                    "_referenceInputReadbackFailureCount");

            _referenceNchwField =
                RequiredField(
                    _dependencyType,
                    "_referenceNchw");

            _mode2NchwField =
                RequiredField(
                    _dependencyType,
                    "_rightNchw");

            _referenceFrozenGpuWorkerField =
                RequiredField(
                    _dependencyType,
                    "_referenceFrozenGpuWorker");

            _bGpuWorkerField =
                RequiredField(
                    _dependencyType,
                    "_bGpuWorker");

            _lanesField =
                RequiredField(
                    _dependencyType,
                    "_lanes");

            Array lanes =
                _lanesField.GetValue(
                    _dependency)
                    as Array;

            if (
                lanes == null ||
                lanes.Length == 0 ||
                lanes.GetValue(0) == null)
            {
                return;
            }

            Type laneType =
                lanes.GetValue(0)
                    .GetType();

            _lanePendingOutputField =
                RequiredField(
                    laneType,
                    "pendingOutput");

            _laneReadbackPendingField =
                RequiredField(
                    laneType,
                    "readbackPending");

            _lanePendingSourceHostTicksField =
                RequiredField(
                    laneType,
                    "pendingSourceHostTicks");

            _lanePendingStartedHostTicksField =
                RequiredField(
                    laneType,
                    "pendingStartedHostTicks");

            _snapshotShader =
                Resources.Load<ComputeShader>(
                    SnapshotShaderResource);

            if (_snapshotShader == null)
            {
                throw new InvalidOperationException(
                    "Output snapshot compute shader unavailable: " +
                    SnapshotShaderResource);
            }

            _snapshotKernel =
                _snapshotShader.FindKernel(
                    "CopyOutputSnapshot");

            _bound =
                true;

            Debug.Log(
                "[Kiwi v44.55.24 PairBoundOutput] DEPENDENCY_BOUND" +
                " earlyProductionOutputPoller=1" +
                " pairBoundShadowSnapshot=1" +
                " outputAuthority=PACKED_FLOAT_BITWISE" +
                " reference=V20_PAIR_BOUND_REF_FROZEN_GPU_OUTPUT" +
                " candidate=V20_PAIR_BOUND_MODE2_GPU_OUTPUT" +
                " productionInputReadback=0" +
                " blockingWait=0");
        }
        catch (Exception exception)
        {
            RegisterFault(
                "DEPENDENCY_BIND_FAIL " +
                exception.GetType().Name +
                " " +
                exception.Message);

            WriteReport(
                "BIND_FAIL");
        }
    }

    private void ObservePendingDependencyPair()
    {
        if (
            !_bound ||
            _dependency == null ||
            !ReadFieldBool(
                _dependency,
                _pairPendingField))
        {
            return;
        }

        int pairToken =
            ReadFieldInt(
                _dependency,
                _pairTokenField);

        object record =
            _pendingRecordField.GetValue(
                _dependency);

        if (
            record == null ||
            pairToken <= 0)
        {
            return;
        }

        int index =
            ReadMemberInt(
                record,
                "Index");

        if (index < 0)
        {
            RegisterFault(
                "PAIR_INDEX_INVALID");
            return;
        }

        ulong sequence =
            ReadMemberULong(
                record,
                "Sequence");

        long nativeHostTicks =
            ReadMemberLong(
                record,
                "NativeHostTicks");

        long managedHostTicks =
            ReadMemberLong(
                record,
                "ManagedHostTicks");

        long laneStartedHostTicks =
            ReadMemberLong(
                record,
                "LaneStartedHostTicks");

        bool laneStillMatched =
            ReadMemberBool(
                record,
                "LaneStillMatchedAfterCapture");

        if (
            sequence == 0UL ||
            nativeHostTicks <= 0L ||
            managedHostTicks <= 0L ||
            laneStartedHostTicks <= 0L ||
            !laneStillMatched)
        {
            RegisterFault(
                "DEPENDENCY_PAIR_IDENTITY_INVALID index=" +
                index);

            return;
        }

        if (_attachedIndices.Contains(
                index))
        {
            if (!_captures.TryGetValue(
                    index,
                    out CaptureState existing))
            {
                _duplicatePairCount++;

                RegisterFault(
                    "ATTACHED_PAIR_STATE_MISSING index=" +
                    index);

                return;
            }

            if (!PendingShadowIdentityMatches(
                    existing,
                    record,
                    pairToken,
                    requireCommonScheduled: false))
            {
                RegisterShadowIdentityFailure(
                    existing,
                    "PENDING_PAIR_IDENTITY_CHANGED");

                return;
            }

            if (
                ReadFieldBool(
                    _dependency,
                    _commonScheduledField) &&
                !existing.Failed &&
                !(
                    existing.ReferenceSnapshotSubmitted &&
                    existing.Mode2SnapshotSubmitted
                ))
            {
                TryCapturePairBoundShadowOutputs(
                    existing,
                    record,
                    pairToken);
            }

            return;
        }

        if (
            _lastAttachedIndex >= 0 &&
            index >
                _lastAttachedIndex + 1)
        {
            _attachMissCount++;

            RegisterFault(
                "PAIR_ATTACH_JUMP previous=" +
                _lastAttachedIndex +
                " current=" +
                index);

            return;
        }

        Array lanes =
            _lanesField.GetValue(
                _dependency)
                as Array;

        object matchedLane =
            FindMatchingProductionLane(
                lanes,
                managedHostTicks,
                laneStartedHostTicks);

        if (matchedLane == null)
        {
            RegisterFault(
                "PRODUCTION_LANE_NOT_MATCHED index=" +
                index);

            return;
        }

        CaptureState state =
            new CaptureState
            {
                Index =
                    index,
                PairToken =
                    pairToken,
                Sequence =
                    sequence,
                NativeHostTicks =
                    nativeHostTicks,
                ManagedHostTicks =
                    managedHostTicks,
                LaneStartedHostTicks =
                    laneStartedHostTicks,
                ProductionLane =
                    matchedLane,
                AttachTicks =
                    Stopwatch.GetTimestamp()
            };

        _attachedIndices.Add(
            index);

        _captures.Add(
            index,
            state);

        _lastAttachedIndex =
            index;

        Debug.Log(
            "[Kiwi v44.55.24 PairBoundOutput] PAIR_ATTACHED" +
            " index=" + index +
            " pairToken=" + pairToken +
            " sequence=" + sequence +
            " waitingProductionOutput=1" +
            " waitingPairBoundShadow=1" +
            " waitingV20Record=1");

        if (ReadFieldBool(
                _dependency,
                _commonScheduledField))
        {
            TryCapturePairBoundShadowOutputs(
                state,
                record,
                pairToken);
        }
    }

    private void TryCapturePairBoundShadowOutputs(
        CaptureState state,
        object record,
        int pairToken)
    {
        if (
            state == null ||
            state.Failed)
        {
            return;
        }

        if (
            state.ReferenceSnapshotSubmitted ||
            state.Mode2SnapshotSubmitted)
        {
            if (
                state.ReferenceSnapshotSubmitted &&
                state.Mode2SnapshotSubmitted)
            {
                return;
            }

            _duplicateShadowSnapshotCount++;
            state.Failed = true;

            RegisterFault(
                "PARTIAL_SHADOW_SNAPSHOT_REENTRY index=" +
                state.Index);

            return;
        }

        if (!PendingShadowIdentityMatches(
                state,
                record,
                pairToken,
                requireCommonScheduled: true))
        {
            RegisterShadowIdentityFailure(
                state,
                "SHADOW_CAPTURE_IDENTITY_INVALID");

            return;
        }

        if (_shadowTokenOwners.TryGetValue(
                pairToken,
                out int existingOwner))
        {
            _duplicateShadowSnapshotCount++;

            if (existingOwner != state.Index)
            {
                _shadowIdentityMismatchCount++;
            }

            state.Failed = true;

            RegisterFault(
                "DUPLICATE_SHADOW_PAIR_TOKEN token=" +
                pairToken +
                " existingIndex=" +
                existingOwner +
                " currentIndex=" +
                state.Index);

            return;
        }

        float[] reference =
            _referenceNchwField.GetValue(
                _dependency)
                as float[];

        float[] mode2 =
            _mode2NchwField.GetValue(
                _dependency)
                as float[];

        if (
            reference == null ||
            mode2 == null ||
            reference.Length != InputFloatCount ||
            mode2.Length != InputFloatCount)
        {
            _inputArrayIdentityMismatchCount++;
            state.Failed = true;

            RegisterFault(
                "PAIR_BOUND_INPUT_ARRAY_INVALID index=" +
                state.Index);

            return;
        }

        InputParity inputParity =
            ComputeInputParity(
                reference,
                mode2);

        state.RefMode2Ge4 =
            inputParity.Ge4;

        state.RefMode2MeanAbsLsb =
            inputParity.MeanAbsLsb;

        state.InputArraysCaptured =
            true;

        Worker referenceWorker =
            _referenceFrozenGpuWorkerField.GetValue(
                _dependency)
                as Worker;

        Worker mode2Worker =
            _bGpuWorkerField.GetValue(
                _dependency)
                as Worker;

        if (
            referenceWorker == null ||
            mode2Worker == null)
        {
            RegisterShadowCaptureMiss(
                state,
                "PAIR_BOUND_GPU_WORKER_MISSING");

            return;
        }

        Tensor<float> referenceOutput =
            referenceWorker.PeekOutput(0)
                as Tensor<float>;

        Tensor<float> mode2Output =
            mode2Worker.PeekOutput(0)
                as Tensor<float>;

        if (
            referenceOutput == null ||
            mode2Output == null)
        {
            RegisterShadowCaptureMiss(
                state,
                "PAIR_BOUND_GPU_OUTPUT_MISSING");

            return;
        }

        state.ShadowSnapshotPairToken =
            pairToken;

        state.ShadowSnapshotRecordIndex =
            state.Index;

        state.ShadowSnapshotSequence =
            state.Sequence;

        _shadowTokenOwners.Add(
            pairToken,
            state.Index);

        try
        {
            SubmitTensorSnapshot(
                state,
                referenceOutput,
                SnapshotKind.Reference);

            state.ReferenceSnapshotFrame =
                Time.frameCount;
        }
        catch (Exception exception)
        {
            _referenceOutputSnapshotFailureCount++;
            state.Failed = true;

            RegisterFault(
                "PAIR_BOUND_REFERENCE_SNAPSHOT_FAIL index=" +
                state.Index +
                " " +
                exception.GetType().Name +
                " " +
                exception.Message);

            return;
        }

        if (!PendingShadowIdentityMatches(
                state,
                record,
                pairToken,
                requireCommonScheduled: true))
        {
            RegisterShadowIdentityFailure(
                state,
                "SHADOW_IDENTITY_CHANGED_BETWEEN_OUTPUTS");

            return;
        }

        try
        {
            SubmitTensorSnapshot(
                state,
                mode2Output,
                SnapshotKind.Mode2);

            state.Mode2SnapshotFrame =
                Time.frameCount;
        }
        catch (Exception exception)
        {
            _mode2OutputSnapshotFailureCount++;
            state.Failed = true;

            RegisterFault(
                "PAIR_BOUND_MODE2_SNAPSHOT_FAIL index=" +
                state.Index +
                " " +
                exception.GetType().Name +
                " " +
                exception.Message);

            return;
        }

        if (!PendingShadowIdentityMatches(
                state,
                record,
                pairToken,
                requireCommonScheduled: true))
        {
            RegisterShadowIdentityFailure(
                state,
                "SHADOW_IDENTITY_CHANGED_AFTER_CAPTURE");

            return;
        }

        Debug.Log(
            "[Kiwi v44.55.24 PairBoundOutput] SHADOW_OUTPUTS_CAPTURED" +
            " pairToken=" + pairToken +
            " index=" + state.Index +
            " sequence=" + state.Sequence +
            " refFrame=" + state.ReferenceSnapshotFrame +
            " mode2Frame=" + state.Mode2SnapshotFrame +
            " refMode2Ge4=" + state.RefMode2Ge4 +
            " refMode2MeanLsb=" +
            F(
                state.RefMode2MeanAbsLsb));
    }

    private bool PendingShadowIdentityMatches(
        CaptureState state,
        object record,
        int pairToken,
        bool requireCommonScheduled)
    {
        if (
            state == null ||
            record == null ||
            !object.ReferenceEquals(
                record,
                _pendingRecordField.GetValue(
                    _dependency)) ||
            !ReadFieldBool(
                _dependency,
                _pairPendingField) ||
            ReadFieldInt(
                _dependency,
                _pairTokenField) !=
                pairToken ||
            state.PairToken != pairToken ||
            state.Index != ReadMemberInt(
                record,
                "Index") ||
            state.Sequence != ReadMemberULong(
                record,
                "Sequence") ||
            state.NativeHostTicks != ReadMemberLong(
                record,
                "NativeHostTicks") ||
            state.ManagedHostTicks != ReadMemberLong(
                record,
                "ManagedHostTicks") ||
            state.LaneStartedHostTicks != ReadMemberLong(
                record,
                "LaneStartedHostTicks"))
        {
            return false;
        }

        return
            !requireCommonScheduled ||
            ReadFieldBool(
                _dependency,
                _commonScheduledField);
    }

    private void RegisterShadowIdentityFailure(
        CaptureState state,
        string reason)
    {
        _shadowIdentityMismatchCount++;

        if (state != null)
        {
            state.Failed = true;
        }

        RegisterFault(
            reason +
            " index=" +
            (
                state != null
                    ? state.Index
                    : -1
            ));
    }

    private void RegisterShadowCaptureMiss(
        CaptureState state,
        string reason)
    {
        _shadowOutputCaptureMissCount++;

        if (state != null)
        {
            state.Failed = true;
        }

        RegisterFault(
            reason +
            " index=" +
            (
                state != null
                    ? state.Index
                    : -1
            ));
    }

    private void CaptureCompletedDependencyRecordWindow()
    {
        if (
            !_bound ||
            _dependency == null)
        {
            return;
        }

        IList records =
            _recordsField.GetValue(
                _dependency)
                as IList;

        if (
            records == null ||
            records.Count == 0)
        {
            return;
        }

        int nextIndex =
            _lastRecordCapturedIndex + 1;

        if (nextIndex >= records.Count)
        {
            return;
        }

        object record =
            records[
                nextIndex];

        int recordIndex =
            ReadMemberInt(
                record,
                "Index");

        ulong recordSequence =
            ReadMemberULong(
                record,
                "Sequence");

        long recordNativeHostTicks =
            ReadMemberLong(
                record,
                "NativeHostTicks");

        long recordManagedHostTicks =
            ReadMemberLong(
                record,
                "ManagedHostTicks");

        long recordLaneStartedHostTicks =
            ReadMemberLong(
                record,
                "LaneStartedHostTicks");

        if (
            recordIndex != nextIndex ||
            !_captures.TryGetValue(
                recordIndex,
                out CaptureState state))
        {
            _recordCaptureWindowMissCount++;
            _shadowOutputCaptureMissCount++;

            RegisterFault(
                "V20_RECORD_WITHOUT_PAIR_BOUND_CAPTURE index=" +
                recordIndex);

            _lastRecordCapturedIndex =
                nextIndex;

            return;
        }

        if (state.Failed)
        {
            _lastRecordCapturedIndex =
                recordIndex;

            return;
        }

        int livePairToken =
            ReadFieldInt(
                _dependency,
                _pairTokenField);

        if (livePairToken < state.PairToken)
        {
            RegisterShadowIdentityFailure(
                state,
                "V20_RECORD_PAIR_TOKEN_REGRESSED liveToken=" +
                livePairToken);

            _lastRecordCapturedIndex =
                recordIndex;

            return;
        }

        state.NextPairTokenObservedBeforeRecordBind =
            livePairToken > state.PairToken;

        if (state.NextPairTokenObservedBeforeRecordBind)
        {
            _nextPairTokenObservedBeforeRecordBindCount++;
        }

        state.RecordCompletionFrame =
            Time.frameCount;

        if (
            state.Sequence != recordSequence ||
            state.NativeHostTicks != recordNativeHostTicks ||
            state.ManagedHostTicks != recordManagedHostTicks ||
            state.LaneStartedHostTicks != recordLaneStartedHostTicks ||
            state.ShadowSnapshotPairToken != state.PairToken ||
            state.ShadowSnapshotRecordIndex != recordIndex ||
            state.ShadowSnapshotSequence != recordSequence)
        {
            RegisterShadowIdentityFailure(
                state,
                "V20_RECORD_SHADOW_IDENTITY_MISMATCH");

            _lastRecordCapturedIndex =
                recordIndex;

            return;
        }

        if (
            !state.ReferenceSnapshotSubmitted ||
            !state.Mode2SnapshotSubmitted ||
            !state.InputArraysCaptured)
        {
            RegisterShadowCaptureMiss(
                state,
                "V20_RECORD_PAIR_BOUND_SNAPSHOT_MISSING");

            _lastRecordCapturedIndex =
                recordIndex;

            return;
        }

        state.DependencyRecordedMeanAbsLsb =
            ReadMemberDouble(
                record,
                "ReferenceVsMode2InputMeanAbsLsb");

        if (
            Math.Abs(
                state.RefMode2MeanAbsLsb -
                state.DependencyRecordedMeanAbsLsb) >
            PairMeanToleranceLsb)
        {
            _inputArrayIdentityMismatchCount++;

            state.Failed =
                true;

            RegisterFault(
                "V20_INPUT_ARRAY_RECORD_MISMATCH index=" +
                recordIndex +
                " recomputed=" +
                F(
                    state.RefMode2MeanAbsLsb) +
                " recorded=" +
                F(
                    state.DependencyRecordedMeanAbsLsb));

            _lastRecordCapturedIndex =
                recordIndex;

            return;
        }

        state.DependencyRecordCaptured =
            true;

        state.DependencyRecordCaptureTicks =
            Stopwatch.GetTimestamp();

        _lastRecordCapturedIndex =
            recordIndex;

        Debug.Log(
            "[Kiwi v44.55.24 PairBoundOutput] RECORD_BOUND" +
            " index=" + recordIndex +
            " pairToken=" + state.PairToken +
            " sequence=" + recordSequence +
            " recordCompletionFrame=" +
            state.RecordCompletionFrame +
            " nextPairTokenObservedBeforeRecordBind=" +
            B(
                state.NextPairTokenObservedBeforeRecordBind) +
            " refMode2Ge4=" +
            state.RefMode2Ge4 +
            " refMode2MeanLsb=" +
            F(
                state.RefMode2MeanAbsLsb));
    }

    private enum SnapshotKind
    {
        Production,
        Reference,
        Mode2
    }

    private void SubmitTensorSnapshot(
        CaptureState state,
        Tensor<float> tensor,
        SnapshotKind kind)
    {
        if (
            state == null ||
            tensor == null ||
            tensor.shape.length !=
                PackedOutputLength)
        {
            throw new InvalidOperationException(
                "Snapshot tensor invalid.");
        }

        ComputeTensorData sourceData =
            tensor.dataOnBackend
                as ComputeTensorData;

        if (
            sourceData == null ||
            sourceData.buffer == null ||
            !sourceData.buffer.IsValid())
        {
            throw new InvalidOperationException(
                "Snapshot source is not valid GPUCompute data.");
        }

        ComputeBuffer source =
            sourceData.buffer;

        if (
            source.count < PackedOutputLength ||
            source.stride != sizeof(float))
        {
            _outputShapeMismatchCount++;

            throw new InvalidOperationException(
                "Snapshot source buffer capacity/stride invalid count=" +
                source.count +
                " requiredCount=" +
                PackedOutputLength +
                " stride=" +
                source.stride);
        }

        ComputeBuffer destination =
            new ComputeBuffer(
                PackedOutputLength,
                sizeof(float),
                ComputeBufferType.Structured);

        destination.name =
            "KiwiV44_55_24_" +
            kind +
            "_" +
            state.Index;

        SetSnapshotBuffer(
            state,
            kind,
            destination);

        CommandBuffer cb =
            new CommandBuffer
            {
                name =
                    "Kiwi v44.55.24 Output Snapshot " +
                    kind +
                    " " +
                    state.Index
            };

        try
        {
            cb.SetComputeIntParam(
                _snapshotShader,
                "_Count",
                PackedOutputLength);

            cb.SetComputeBufferParam(
                _snapshotShader,
                _snapshotKernel,
                "_Source",
                source);

            cb.SetComputeBufferParam(
                _snapshotShader,
                _snapshotKernel,
                "_Destination",
                destination);

            int groups =
                (
                    PackedOutputLength +
                    SnapshotThreads -
                    1
                ) /
                SnapshotThreads;

            cb.DispatchCompute(
                _snapshotShader,
                _snapshotKernel,
                groups,
                1,
                1);

            cb.RequestAsyncReadback(
                destination,
                request =>
                {
                    CompleteSnapshot(
                        state,
                        kind,
                        request);
                });

            Graphics.ExecuteCommandBuffer(
                cb);

            SetSnapshotSubmitted(
                state,
                kind,
                true);

            Debug.Log(
                "[Kiwi v44.55.24 PairBoundOutput] SNAPSHOT_SUBMITTED" +
                " kind=" + kind +
                " index=" + state.Index +
                " sequence=" + state.Sequence);
        }
        catch
        {
            ReleaseSnapshotBuffer(
                state,
                kind);

            throw;
        }
        finally
        {
            cb.Release();
        }
    }

    private void CompleteSnapshot(
        CaptureState state,
        SnapshotKind kind,
        AsyncGPUReadbackRequest request)
    {
        try
        {
            if (
                state == null ||
                state.Failed ||
                _reportWritten)
            {
                return;
            }

            if (request.hasError)
            {
                throw new InvalidOperationException(
                    "AsyncGPUReadback failed.");
            }

            var data =
                request.GetData<float>();

            if (data.Length != PackedOutputLength)
            {
                _outputShapeMismatchCount++;

                throw new InvalidOperationException(
                    "Snapshot readback length mismatch: " +
                    data.Length);
            }

            float[] destination =
                GetOutputArray(
                    state,
                    kind);

            for (
                int i = 0;
                i < PackedOutputLength;
                i++)
            {
                float value =
                    data[i];

                destination[i] =
                    value;

                if (!IsFinite(
                        value))
                {
                    _nonFiniteOutputCount++;
                }
            }

            SetSnapshotDone(
                state,
                kind,
                true);

            Debug.Log(
                "[Kiwi v44.55.24 PairBoundOutput] SNAPSHOT_READBACK" +
                " kind=" + kind +
                " index=" + state.Index +
                " sequence=" + state.Sequence);

            TryFinalizeReadyPairs();
        }
        catch (Exception exception)
        {
            state.Failed =
                true;

            switch (kind)
            {
                case SnapshotKind.Production:
                    _productionOutputSnapshotFailureCount++;
                    break;
                case SnapshotKind.Reference:
                    _referenceOutputSnapshotFailureCount++;
                    break;
                case SnapshotKind.Mode2:
                    _mode2OutputSnapshotFailureCount++;
                    break;
            }

            RegisterFault(
                "OUTPUT_SNAPSHOT_READBACK_FAIL kind=" +
                kind +
                " index=" +
                (
                    state != null
                        ? state.Index
                        : -1
                ) +
                " " +
                exception.GetType().Name +
                " " +
                exception.Message);
        }
        finally
        {
            ReleaseSnapshotBuffer(
                state,
                kind);
        }
    }

    private void TryFinalizeReadyPairs()
    {
        if (_captures.Count == 0)
        {
            return;
        }

        List<int> ready =
            null;

        foreach (
            KeyValuePair<int, CaptureState> pair
            in _captures)
        {
            CaptureState state =
                pair.Value;

            if (
                state == null ||
                state.Failed ||
                !state.DependencyRecordCaptured ||
                !state.InputArraysCaptured ||
                !state.ProductionSnapshotDone ||
                !state.ReferenceSnapshotDone ||
                !state.Mode2SnapshotDone)
            {
                continue;
            }

            if (ready == null)
            {
                ready =
                    new List<int>();
            }

            ready.Add(
                pair.Key);
        }

        if (ready == null)
        {
            return;
        }

        ready.Sort();

        for (
            int i = 0;
            i < ready.Count;
            i++)
        {
            int index =
                ready[i];

            if (!_captures.TryGetValue(
                    index,
                    out CaptureState state))
            {
                continue;
            }

            PairResult result =
                FinalizePair(
                    state);

            _results.Add(
                result);

            _completedPairCount++;

            _captures.Remove(
                index);

            Debug.Log(
                "[Kiwi v44.55.24 PairBoundOutput] SAMPLE" +
                " index=" + result.Index +
                " sequence=" + result.Sequence +
                " shadowPairToken=" +
                result.ShadowSnapshotPairToken +
                " refSnapshotFrame=" +
                result.ReferenceSnapshotFrame +
                " mode2SnapshotFrame=" +
                result.Mode2SnapshotFrame +
                " recordCompletionFrame=" +
                result.RecordCompletionFrame +
                " nextPairBeforeRecordBind=" +
                B(
                    result.NextPairTokenObservedBeforeRecordBind) +
                " refMode2Ge4=" +
                result.RefMode2Ge4 +
                " prodRefExact=" +
                B(
                    result.ProductionEqualsReferenceBitwise) +
                " prodMode2Exact=" +
                B(
                    result.ProductionEqualsMode2Bitwise) +
                " refMode2Exact=" +
                B(
                    result.ReferenceEqualsMode2Bitwise) +
                " class=" +
                result.Classification +
                " prodRefMean=" +
                F(
                    result.ProductionVsReferenceMeanAbs) +
                " prodMode2Mean=" +
                F(
                    result.ProductionVsMode2MeanAbs));
        }
    }

    private PairResult FinalizePair(
        CaptureState state)
    {
        OutputParity productionReference =
            ComputeOutputParity(
                state.ProductionOutput,
                state.ReferenceOutput);

        OutputParity productionMode2 =
            ComputeOutputParity(
                state.ProductionOutput,
                state.Mode2Output);

        OutputParity referenceMode2 =
            ComputeOutputParity(
                state.ReferenceOutput,
                state.Mode2Output);

        if (
            productionReference.NonFiniteCount > 0 ||
            productionMode2.NonFiniteCount > 0 ||
            referenceMode2.NonFiniteCount > 0)
        {
            RegisterFault(
                "NONFINITE_OUTPUT_PARITY index=" +
                state.Index);
        }

        bool productionEqualsReference =
            productionReference.ExactCount ==
                PackedOutputLength;

        bool productionEqualsMode2 =
            productionMode2.ExactCount ==
                PackedOutputLength;

        bool referenceEqualsMode2 =
            referenceMode2.ExactCount ==
                PackedOutputLength;

        bool anomalous =
            state.RefMode2Ge4 > 0;

        string classification;

        if (!anomalous)
        {
            classification =
                "NORMAL_NO_GE4";
        }
        else
        {
            _anomalousPairCount++;

            if (
                productionEqualsReference &&
                !productionEqualsMode2)
            {
                _referenceExactMatchCount++;

                classification =
                    "ANOMALOUS_PRODUCTION_EQUALS_REFERENCE";
            }
            else if (
                productionEqualsMode2 &&
                !productionEqualsReference)
            {
                _mode2ExactMatchCount++;

                classification =
                    "ANOMALOUS_PRODUCTION_EQUALS_MODE2";
            }
            else if (
                productionEqualsReference &&
                productionEqualsMode2)
            {
                _bothExactCount++;

                classification =
                    "ANOMALOUS_BOTH_EXACT_NONDISCRIMINATING";
            }
            else
            {
                _neitherExactCount++;

                classification =
                    "ANOMALOUS_NEITHER_EXACT";
            }
        }

        // KIWI_V44_55_26_PRODUCTION_DECODE_PAYLOAD_TRANSACTION_CLOSURE
        // Hands off the already observer-owned snapshot; no new GPU copy.
        KiwiProductionDecodePayloadTransactionTraceV44_55_26
            .RecordPairBoundSnapshot(
                state.Index,
                state.ShadowSnapshotPairToken,
                state.ShadowSnapshotSequence,
                state.ManagedHostTicks,
                classification,
                state.ProductionOutput);

        // KIWI_V44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUTHORITY
        // Hands off the already CPU-owned REF/MODE2 arrays. The rejected
        // Production snapshot is supplied only for reference-alias validation.
        KiwiActualProductionVsShadowPayloadAuthorityV44_55_27
            .RecordPairBoundShadowPayloads(
                state.Index,
                state.ShadowSnapshotPairToken,
                state.ShadowSnapshotSequence,
                state.ManagedHostTicks,
                state.ShadowSnapshotRecordIndex,
                state.ReferenceSnapshotFrame,
                state.Mode2SnapshotFrame,
                classification,
                state.ReferenceOutput,
                state.Mode2Output,
                state.ProductionOutput);

        return
            new PairResult
            {
                Index =
                    state.Index,
                ShadowSnapshotPairToken =
                    state.ShadowSnapshotPairToken,
                ShadowSnapshotRecordIndex =
                    state.ShadowSnapshotRecordIndex,
                Sequence =
                    state.Sequence,
                ShadowSnapshotSequence =
                    state.ShadowSnapshotSequence,
                ReferenceSnapshotFrame =
                    state.ReferenceSnapshotFrame,
                Mode2SnapshotFrame =
                    state.Mode2SnapshotFrame,
                RecordCompletionFrame =
                    state.RecordCompletionFrame,
                NextPairTokenObservedBeforeRecordBind =
                    state.NextPairTokenObservedBeforeRecordBind,
                RefMode2Ge4 =
                    state.RefMode2Ge4,
                RefMode2MeanAbsLsb =
                    state.RefMode2MeanAbsLsb,
                ProductionVsReferenceMeanAbs =
                    productionReference.MeanAbs,
                ProductionVsReferenceMaxAbs =
                    productionReference.MaxAbs,
                ProductionVsMode2MeanAbs =
                    productionMode2.MeanAbs,
                ProductionVsMode2MaxAbs =
                    productionMode2.MaxAbs,
                ReferenceVsMode2MeanAbs =
                    referenceMode2.MeanAbs,
                ReferenceVsMode2MaxAbs =
                    referenceMode2.MaxAbs,
                ProductionVsReferenceExactCount =
                    productionReference.ExactCount,
                ProductionVsMode2ExactCount =
                    productionMode2.ExactCount,
                ReferenceVsMode2ExactCount =
                    referenceMode2.ExactCount,
                ProductionEqualsReferenceBitwise =
                    productionEqualsReference,
                ProductionEqualsMode2Bitwise =
                    productionEqualsMode2,
                ReferenceEqualsMode2Bitwise =
                    referenceEqualsMode2,
                Classification =
                    classification
            };
    }

    private void TryFinishAfterDependency()
    {
        if (
            !_bound ||
            _reportWritten)
        {
            return;
        }

        bool dependencyReportWritten =
            ReadFieldBool(
                _dependency,
                _reportWrittenField);

        if (!dependencyReportWritten)
        {
            _dependencyCompleteSince =
                -1.0;
            return;
        }

        int dependencyFaults =
            ReadFieldInt(
                _dependency,
                _observerFaultCountField) +
            ReadFieldInt(
                _dependency,
                _sourceIdentityMismatchCountField) +
            ReadFieldInt(
                _dependency,
                _referenceTensorNonFiniteCountField) +
            ReadFieldInt(
                _dependency,
                _referenceInputReadbackFailureCountField);

        if (dependencyFaults > 0)
        {
            RegisterFault(
                "DEPENDENCY_FAULT_COUNT=" +
                dependencyFaults);

            WriteReport(
                "DEPENDENCY_INVALID");

            return;
        }

        if (_dependencyCompleteSince < 0.0)
        {
            _dependencyCompleteSince =
                Time.realtimeSinceStartupAsDouble;
        }

        TryFinalizeReadyPairs();

        if (_captures.Count == 0)
        {
            WriteReport(
                "COMPLETE");
            return;
        }

        if (
            Time.realtimeSinceStartupAsDouble -
                _dependencyCompleteSince >=
            DependencyCompletionGraceSeconds)
        {
            RegisterFault(
                "CAPTURES_PENDING_AFTER_DEPENDENCY_COMPLETE count=" +
                _captures.Count);

            WriteReport(
                "PENDING_TIMEOUT");
        }
    }

    private string DetermineDecision()
    {
        int dependencyRecordCount =
            GetDependencyRecordCount();

        if (
            _observerFaultCount > 0 ||
            _attachMissCount > 0 ||
            _recordCaptureWindowMissCount > 0 ||
            _shadowOutputCaptureMissCount > 0 ||
            _shadowIdentityMismatchCount > 0 ||
            _duplicateShadowSnapshotCount > 0 ||
            _productionOutputSnapshotFailureCount > 0 ||
            _referenceOutputSnapshotFailureCount > 0 ||
            _mode2OutputSnapshotFailureCount > 0 ||
            _nonFiniteOutputCount > 0 ||
            _inputArrayIdentityMismatchCount > 0 ||
            _outputShapeMismatchCount > 0 ||
            _duplicatePairCount > 0 ||
            _captures.Count > 0 ||
            _completedPairCount !=
                dependencyRecordCount)
        {
            return
                "INVALID_OBSERVER";
        }

        if (_completedPairCount < MinimumCompletedPairs)
        {
            return
                "INSUFFICIENT_DATA";
        }

        if (_anomalousPairCount < MinimumAnomalousPairs)
        {
            return
                "INSUFFICIENT_ANOMALY_REPRODUCTION";
        }

        int discriminating =
            _referenceExactMatchCount +
            _mode2ExactMatchCount;

        if (
            discriminating <
            MinimumAnomalousPairs)
        {
            return
                "MIXED_TRANSACTION";
        }

        if (
            _referenceExactMatchCount ==
                _anomalousPairCount &&
            _mode2ExactMatchCount == 0 &&
            _bothExactCount == 0 &&
            _neitherExactCount == 0)
        {
            return
                "REFERENCE_TRANSACTION_VALID_CONFIRMED";
        }

        if (
            _mode2ExactMatchCount ==
                _anomalousPairCount &&
            _referenceExactMatchCount == 0 &&
            _bothExactCount == 0 &&
            _neitherExactCount == 0)
        {
            return
                "REFERENCE_TRANSACTION_INVALID_CONFIRMED";
        }

        return
            "MIXED_TRANSACTION";
    }

    private void WriteReport(
        string status)
    {
        if (_reportWritten)
        {
            return;
        }

        _reportWritten =
            true;

        string decision =
            DetermineDecision();

        string directory =
            Path.Combine(
                Application.persistentDataPath,
                "KiwiFrameBottleneck");

        Directory.CreateDirectory(
            directory);

        string stamp =
            DateTime.Now.ToString(
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture);

        string textPath =
            Path.Combine(
                directory,
                "KiwiPairBoundShadowOutputSnapshot_v44_55_24_" +
                stamp +
                ".txt");

        string csvPath =
            Path.Combine(
                directory,
                "KiwiPairBoundShadowOutputSnapshot_v44_55_24_" +
                stamp +
                ".csv");

        List<string> lines =
            new List<string>();

        lines.Add(
            "KiwiAvatarSystem v44.55.24 Pair-Bound Shadow Output Snapshot");

        lines.Add(
            "contract=" + Contract);

        lines.Add(
            "status=" + status);

        lines.Add(
            "dependencyStatus=" +
            (
                ReadFieldBool(
                    _dependency,
                    _reportWrittenField)
                    ? "COMPLETE"
                    : "INCOMPLETE"
            ));

        lines.Add(
            "decision=" + decision);

        lines.Add(
            "observerOnly=1");

        lines.Add(
            "productionWrites=0");

        lines.Add(
            "nativeWrites=0");

        lines.Add(
            "trackingMathChange=0");

        lines.Add(
            "roiChange=0");

        lines.Add(
            "thresholdChange=0");

        lines.Add(
            "productionInputReadback=0");

        lines.Add(
            "productionOutputStateConsumption=0");

        lines.Add(
            "shadowPeekAfterRecordCompletion=0");

        lines.Add(
            "blockingWait=0");

        lines.Add(
            "performanceAuthority=0");

        lines.Add(
            "productionOutputAuthority=COMPLETED_PENDING_OUTPUT_GPU_SNAPSHOT");

        lines.Add(
            "referenceOutputAuthority=V20_PAIR_BOUND_REF_FROZEN_GPU_SNAPSHOT");

        lines.Add(
            "mode2OutputAuthority=V20_PAIR_BOUND_B_GPU_SNAPSHOT");

        lines.Add(
            "shadowSnapshotWindow=PAIR_PENDING_AND_COMMON_SCHEDULED");

        lines.Add(
            "comparison=PACKED_OUTPUT_1405_FLOAT_BITWISE");

        lines.Add(
            "anomalySelector=V20_REF_VS_MODE2_INPUT_GE4_LSB");

        lines.Add(
            "completedPairCount=" +
            _completedPairCount);

        lines.Add(
            "dependencyRecordCount=" +
            GetDependencyRecordCount());

        lines.Add(
            "anomalousPairCount=" +
            _anomalousPairCount);

        lines.Add(
            "referenceExactMatchCount=" +
            _referenceExactMatchCount);

        lines.Add(
            "mode2ExactMatchCount=" +
            _mode2ExactMatchCount);

        lines.Add(
            "bothExactCount=" +
            _bothExactCount);

        lines.Add(
            "neitherExactCount=" +
            _neitherExactCount);

        lines.Add(
            "observerFaultCount=" +
            _observerFaultCount);

        lines.Add(
            "dependencyMissingFrames=" +
            _dependencyMissingFrames);

        lines.Add(
            "attachMissCount=" +
            _attachMissCount);

        lines.Add(
            "recordCaptureWindowMissCount=" +
            _recordCaptureWindowMissCount);

        lines.Add(
            "shadowOutputCaptureMissCount=" +
            _shadowOutputCaptureMissCount);

        lines.Add(
            "shadowIdentityMismatchCount=" +
            _shadowIdentityMismatchCount);

        lines.Add(
            "duplicateShadowSnapshotCount=" +
            _duplicateShadowSnapshotCount);

        lines.Add(
            "nextPairTokenObservedBeforeRecordBindCount=" +
            _nextPairTokenObservedBeforeRecordBindCount);

        lines.Add(
            "productionOutputSnapshotFailureCount=" +
            _productionOutputSnapshotFailureCount);

        lines.Add(
            "referenceOutputSnapshotFailureCount=" +
            _referenceOutputSnapshotFailureCount);

        lines.Add(
            "mode2OutputSnapshotFailureCount=" +
            _mode2OutputSnapshotFailureCount);

        lines.Add(
            "nonFiniteOutputCount=" +
            _nonFiniteOutputCount);

        lines.Add(
            "inputArrayIdentityMismatchCount=" +
            _inputArrayIdentityMismatchCount);

        lines.Add(
            "outputShapeMismatchCount=" +
            _outputShapeMismatchCount);

        lines.Add(
            "duplicatePairCount=" +
            _duplicatePairCount);

        lines.Add(
            "capturesPending=" +
            _captures.Count);

        lines.Add(
            "");

        lines.Add(
            "[DECISION_RULE]");

        lines.Add(
            "INVALID_OBSERVER if any observer/dependency identity/snapshot/completeness contract fails.");

        lines.Add(
            "INSUFFICIENT_DATA if completedPairCount<60.");

        lines.Add(
            "INSUFFICIENT_ANOMALY_REPRODUCTION if anomalousPairCount<3.");

        lines.Add(
            "REFERENCE_TRANSACTION_VALID_CONFIRMED only when every anomalous >=4-LSB input pair has Production packed output bitwise equal to REF_FROZEN_GPU and not MODE2_GPU.");

        lines.Add(
            "REFERENCE_TRANSACTION_INVALID_CONFIRMED only when every anomalous >=4-LSB input pair has Production packed output bitwise equal to MODE2_GPU and not REF_FROZEN_GPU.");

        lines.Add(
            "Any both-exact, neither-exact, or mixed anomalous population => MIXED_TRANSACTION.");

        lines.Add(
            "No numeric output threshold is tuned after runtime.");

        File.WriteAllLines(
            textPath,
            lines);

        WriteCsv(
            csvPath);

        Debug.Log(
            "[Kiwi v44.55.24 PairBoundOutput] COMPLETE" +
            " decision=" + decision +
            " pairs=" + _completedPairCount +
            " anomalousPairs=" + _anomalousPairCount +
            " prodEqualsRef=" +
            _referenceExactMatchCount +
            " prodEqualsMode2=" +
            _mode2ExactMatchCount +
            " bothExact=" +
            _bothExactCount +
            " neitherExact=" +
            _neitherExactCount +
            " report=" + textPath +
            " csv=" + csvPath);
    }

    private void WriteCsv(
        string path)
    {
        List<string> lines =
            new List<string>();

        lines.Add(
            "index,sequence,shadowSnapshotPairToken,shadowSnapshotRecordIndex," +
            "shadowSnapshotSequence,refSnapshotFrame,mode2SnapshotFrame," +
            "recordCompletionFrame,nextPairTokenObservedBeforeRecordBind," +
            "refMode2Ge4,refMode2MeanAbsLsb," +
            "prodRefExactCount,prodMode2ExactCount,refMode2ExactCount," +
            "prodRefBitwiseExact,prodMode2BitwiseExact,refMode2BitwiseExact," +
            "prodRefMeanAbs,prodRefMaxAbs,prodMode2MeanAbs,prodMode2MaxAbs," +
            "refMode2OutputMeanAbs,refMode2OutputMaxAbs,classification");

        for (
            int i = 0;
            i < _results.Count;
            i++)
        {
            PairResult r =
                _results[i];

            lines.Add(
                r.Index + "," +
                r.Sequence + "," +
                r.ShadowSnapshotPairToken + "," +
                r.ShadowSnapshotRecordIndex + "," +
                r.ShadowSnapshotSequence + "," +
                r.ReferenceSnapshotFrame + "," +
                r.Mode2SnapshotFrame + "," +
                r.RecordCompletionFrame + "," +
                B(
                    r.NextPairTokenObservedBeforeRecordBind) + "," +
                r.RefMode2Ge4 + "," +
                F(
                    r.RefMode2MeanAbsLsb) + "," +
                r.ProductionVsReferenceExactCount + "," +
                r.ProductionVsMode2ExactCount + "," +
                r.ReferenceVsMode2ExactCount + "," +
                B(
                    r.ProductionEqualsReferenceBitwise) + "," +
                B(
                    r.ProductionEqualsMode2Bitwise) + "," +
                B(
                    r.ReferenceEqualsMode2Bitwise) + "," +
                F(
                    r.ProductionVsReferenceMeanAbs) + "," +
                F(
                    r.ProductionVsReferenceMaxAbs) + "," +
                F(
                    r.ProductionVsMode2MeanAbs) + "," +
                F(
                    r.ProductionVsMode2MaxAbs) + "," +
                F(
                    r.ReferenceVsMode2MeanAbs) + "," +
                F(
                    r.ReferenceVsMode2MaxAbs) + "," +
                Csv(
                    r.Classification));
        }

        File.WriteAllLines(
            path,
            lines);
    }

    private object FindMatchingProductionLane(
        Array lanes,
        long managedHostTicks,
        long startedHostTicks)
    {
        if (lanes == null)
        {
            return null;
        }

        for (
            int i = 0;
            i < lanes.Length;
            i++)
        {
            object lane =
                lanes.GetValue(
                    i);

            if (
                lane != null &&
                ReadFieldBool(
                    lane,
                    _laneReadbackPendingField) &&
                ReadFieldLong(
                    lane,
                    _lanePendingSourceHostTicksField) ==
                    managedHostTicks &&
                ReadFieldLong(
                    lane,
                    _lanePendingStartedHostTicksField) ==
                    startedHostTicks)
            {
                return lane;
            }
        }

        return null;
    }

    private bool ProductionLaneMatches(
        CaptureState state)
    {
        return
            state != null &&
            state.ProductionLane != null &&
            ReadFieldBool(
                state.ProductionLane,
                _laneReadbackPendingField) &&
            ReadFieldLong(
                state.ProductionLane,
                _lanePendingSourceHostTicksField) ==
                state.ManagedHostTicks &&
            ReadFieldLong(
                state.ProductionLane,
                _lanePendingStartedHostTicksField) ==
                state.LaneStartedHostTicks;
    }

    private static OutputParity ComputeOutputParity(
        float[] left,
        float[] right)
    {
        OutputParity result =
            default;

        if (
            left == null ||
            right == null ||
            left.Length != PackedOutputLength ||
            right.Length != PackedOutputLength)
        {
            result.NonFiniteCount =
                1;
            return result;
        }

        double sum =
            0.0;

        double max =
            0.0;

        int exact =
            0;

        int nonFinite =
            0;

        for (
            int i = 0;
            i < PackedOutputLength;
            i++)
        {
            float a =
                left[i];

            float b =
                right[i];

            if (
                !IsFinite(a) ||
                !IsFinite(b))
            {
                nonFinite++;
                continue;
            }

            if (
                BitConverter.SingleToInt32Bits(a) ==
                BitConverter.SingleToInt32Bits(b))
            {
                exact++;
            }

            double difference =
                Math.Abs(
                    (double)a -
                    b);

            sum +=
                difference;

            if (difference > max)
            {
                max =
                    difference;
            }
        }

        result.ExactCount =
            exact;

        result.MeanAbs =
            sum /
            PackedOutputLength;

        result.MaxAbs =
            max;

        result.NonFiniteCount =
            nonFinite;

        return result;
    }

    private struct InputParity
    {
        internal int Ge4;
        internal double MeanAbsLsb;
    }

    private static InputParity ComputeInputParity(
        float[] reference,
        float[] mode2)
    {
        InputParity result =
            default;

        if (
            reference == null ||
            mode2 == null ||
            reference.Length != InputFloatCount ||
            mode2.Length != InputFloatCount)
        {
            return result;
        }

        double sum =
            0.0;

        int ge4 =
            0;

        for (
            int i = 0;
            i < InputFloatCount;
            i++)
        {
            double deltaLsb =
                Math.Abs(
                    (
                        (double)reference[i] -
                        mode2[i]
                    ) *
                    255.0);

            sum +=
                deltaLsb;

            if (deltaLsb >= 4.0)
            {
                ge4++;
            }
        }

        result.Ge4 =
            ge4;

        result.MeanAbsLsb =
            sum /
            InputFloatCount;

        return result;
    }

    private void SetSnapshotBuffer(
        CaptureState state,
        SnapshotKind kind,
        ComputeBuffer buffer)
    {
        switch (kind)
        {
            case SnapshotKind.Production:
                state.ProductionSnapshotBuffer =
                    buffer;
                break;
            case SnapshotKind.Reference:
                state.ReferenceSnapshotBuffer =
                    buffer;
                break;
            case SnapshotKind.Mode2:
                state.Mode2SnapshotBuffer =
                    buffer;
                break;
        }
    }

    private ComputeBuffer GetSnapshotBuffer(
        CaptureState state,
        SnapshotKind kind)
    {
        switch (kind)
        {
            case SnapshotKind.Production:
                return state.ProductionSnapshotBuffer;
            case SnapshotKind.Reference:
                return state.ReferenceSnapshotBuffer;
            case SnapshotKind.Mode2:
                return state.Mode2SnapshotBuffer;
            default:
                return null;
        }
    }

    private void SetSnapshotSubmitted(
        CaptureState state,
        SnapshotKind kind,
        bool value)
    {
        switch (kind)
        {
            case SnapshotKind.Production:
                state.ProductionSnapshotSubmitted =
                    value;
                break;
            case SnapshotKind.Reference:
                state.ReferenceSnapshotSubmitted =
                    value;
                break;
            case SnapshotKind.Mode2:
                state.Mode2SnapshotSubmitted =
                    value;
                break;
        }
    }

    private void SetSnapshotDone(
        CaptureState state,
        SnapshotKind kind,
        bool value)
    {
        switch (kind)
        {
            case SnapshotKind.Production:
                state.ProductionSnapshotDone =
                    value;
                break;
            case SnapshotKind.Reference:
                state.ReferenceSnapshotDone =
                    value;
                break;
            case SnapshotKind.Mode2:
                state.Mode2SnapshotDone =
                    value;
                break;
        }
    }

    private float[] GetOutputArray(
        CaptureState state,
        SnapshotKind kind)
    {
        switch (kind)
        {
            case SnapshotKind.Production:
                return state.ProductionOutput;
            case SnapshotKind.Reference:
                return state.ReferenceOutput;
            case SnapshotKind.Mode2:
                return state.Mode2Output;
            default:
                return null;
        }
    }

    private void ReleaseSnapshotBuffer(
        CaptureState state,
        SnapshotKind kind)
    {
        if (state == null)
        {
            return;
        }

        ComputeBuffer buffer =
            GetSnapshotBuffer(
                state,
                kind);

        if (buffer == null)
        {
            return;
        }

        try
        {
            buffer.Release();
        }
        catch
        {
        }

        SetSnapshotBuffer(
            state,
            kind,
            null);
    }

    private int GetDependencyRecordCount()
    {
        IList records =
            _recordsField != null &&
            _dependency != null
                ? _recordsField.GetValue(
                    _dependency)
                    as IList
                : null;

        return
            records != null
                ? records.Count
                : 0;
    }

    private void RegisterFault(
        string reason)
    {
        _observerFaultCount++;

        Debug.LogError(
            "[Kiwi v44.55.24 PairBoundOutput] OBSERVER_FAULT " +
            reason);
    }

    private static FieldInfo RequiredField(
        Type type,
        string name)
    {
        FieldInfo field =
            type?.GetField(
                name,
                InstanceFlags);

        if (field == null)
        {
            throw new MissingFieldException(
                type != null
                    ? type.FullName
                    : "<null>",
                name);
        }

        return field;
    }

    private static bool ReadFieldBool(
        object owner,
        FieldInfo field)
    {
        object value =
            field?.GetValue(
                owner);

        return
            value is bool flag &&
            flag;
    }

    private static int ReadFieldInt(
        object owner,
        FieldInfo field)
    {
        object value =
            field?.GetValue(
                owner);

        return
            value is int integer
                ? integer
                : 0;
    }

    private static long ReadFieldLong(
        object owner,
        FieldInfo field)
    {
        object value =
            field?.GetValue(
                owner);

        return
            value is long ticks
                ? ticks
                : 0L;
    }

    private static int ReadMemberInt(
        object owner,
        string name)
    {
        object value =
            ReadMember(
                owner,
                name);

        return
            value is int integer
                ? integer
                : -1;
    }

    private static ulong ReadMemberULong(
        object owner,
        string name)
    {
        object value =
            ReadMember(
                owner,
                name);

        return
            value is ulong number
                ? number
                : 0UL;
    }

    private static long ReadMemberLong(
        object owner,
        string name)
    {
        object value =
            ReadMember(
                owner,
                name);

        return
            value is long number
                ? number
                : 0L;
    }

    private static bool ReadMemberBool(
        object owner,
        string name)
    {
        object value =
            ReadMember(
                owner,
                name);

        return
            value is bool flag &&
            flag;
    }

    private static double ReadMemberDouble(
        object owner,
        string name)
    {
        object value =
            ReadMember(
                owner,
                name);

        if (value == null)
        {
            return
                double.NaN;
        }

        try
        {
            return
                Convert.ToDouble(
                    value,
                    CultureInfo.InvariantCulture);
        }
        catch
        {
            return
                double.NaN;
        }
    }

    private static object ReadMember(
        object owner,
        string name)
    {
        if (owner == null)
        {
            return null;
        }

        Type type =
            owner.GetType();

        FieldInfo field =
            type.GetField(
                name,
                InstanceFlags);

        if (field != null)
        {
            return
                field.GetValue(
                    owner);
        }

        PropertyInfo property =
            type.GetProperty(
                name,
                InstanceFlags);

        return
            property != null
                ? property.GetValue(
                    owner)
                : null;
    }

    private static bool IsFinite(
        float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
    }

    private static bool ReadBoolEnvironment(
        string name,
        bool fallback)
    {
        string value =
            Environment.GetEnvironmentVariable(
                name);

        if (string.IsNullOrWhiteSpace(
                value))
        {
            return fallback;
        }

        value =
            value.Trim();

        return
            value == "1" ||
            value.Equals(
                "true",
                StringComparison.OrdinalIgnoreCase) ||
            value.Equals(
                "yes",
                StringComparison.OrdinalIgnoreCase) ||
            value.Equals(
                "on",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string F(
        double value)
    {
        return
            value.ToString(
                "F9",
                CultureInfo.InvariantCulture);
    }

    private static string B(
        bool value)
    {
        return
            value
                ? "1"
                : "0";
    }

    private static string Csv(
        string value)
    {
        string safe =
            value ?? string.Empty;

        if (
            safe.IndexOf(',') < 0 &&
            safe.IndexOf('"') < 0 &&
            safe.IndexOf('\n') < 0 &&
            safe.IndexOf('\r') < 0)
        {
            return safe;
        }

        return
            "\"" +
            safe.Replace(
                "\"",
                "\"\"") +
            "\"";
    }

    private void OnDisable()
    {
        foreach (
            KeyValuePair<int, CaptureState> pair
            in _captures)
        {
            ReleaseSnapshotBuffer(
                pair.Value,
                SnapshotKind.Production);

            ReleaseSnapshotBuffer(
                pair.Value,
                SnapshotKind.Reference);

            ReleaseSnapshotBuffer(
                pair.Value,
                SnapshotKind.Mode2);
        }

        if (_reportWritten)
        {
            return;
        }

        if (
            _bound ||
            _completedPairCount > 0 ||
            _captures.Count > 0)
        {
            try
            {
                WriteReport(
                    "STOPPED_BEFORE_COMPLETE");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "[Kiwi v44.55.24 PairBoundOutput] STOP_REPORT_FAIL " +
                    exception.GetType().Name +
                    " " +
                    exception.Message);
            }
        }
    }
}

/// <summary>
/// Runs before ordinary Production Update so a completed pendingOutput can be
/// snapshotted while its Production lane identity is still intact.
/// </summary>
[DefaultExecutionOrder(-33000)]
internal sealed class KiwiPairBoundShadowOutputEarlyProbeV44_55_24
    : MonoBehaviour
{
    internal KiwiPairBoundShadowOutputSnapshotV44_55_24 Owner;

    private void Update()
    {
        Owner?.PollProductionOutputBeforeProductionUpdate();
    }
}
