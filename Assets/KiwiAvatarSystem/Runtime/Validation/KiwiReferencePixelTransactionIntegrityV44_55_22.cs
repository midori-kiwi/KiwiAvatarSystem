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
/// KiwiAvatarSystem v44.55.22
/// Reference Pixel Transaction Integrity Isolation.
///
/// Observer-only follow-up to v44.55.20/v44.55.21.
///
/// Purpose:
///   Snapshot the actual Production lane.input GPU buffer after the existing
///   Production pendingOutput completion is observed, then asynchronously read
///   the observer-owned snapshot and compare it with the exact same v44.55.20
///   pair's frozen REF tensor and mode2 tensor.
///
/// This isolates whether the sparse >=4 LSB tails previously attributed to
/// preprocessing are genuine Production-vs-mode2 differences, or whether the
/// observer REF path captured a different pixel transaction.
///
/// No new crop, Blit, RenderTexture, inference worker, Production write, Native
/// write, blocking wait, tracking-math change, ROI change, or threshold change
/// is introduced. FIX4 adds one observer-owned ComputeBuffer snapshot, one tiny
/// compute copy dispatch and one async readback per measured pair. The snapshot
/// decouples the readback lifetime from Production lane reuse. This run is
/// correctness-only and is never performance authority.
/// </summary>
[DefaultExecutionOrder(33000)]
internal sealed class KiwiReferencePixelTransactionIntegrityV44_55_22
    : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_55_22_REFERENCE_PIXEL_TRANSACTION_INTEGRITY";

    private const string EnableVariable =
        "KIWI_V44_55_22_REFERENCE_TRANSACTION_AUDIT";

    private const int InputSize = 192;
    private const int PlaneLength = InputSize * InputSize;
    private const int InputFloatCount = PlaneLength * 3;
    private const int SnapshotCopyThreads = 256;
    private const string SnapshotCopyResourcePath =
        "KiwiValidation/KiwiTensorSnapshotCopyV44_55_22";

    private const int MinimumCompletedPairs = 60;
    private const int MinimumAnomalousPairs = 3;
    private const double PairMeanToleranceLsb = 0.000001;
    private const double DependencyCompletionGraceSeconds = 8.0;

    // The >=4 LSB boundary is inherited from v44.55.20/v44.55.21.
    // It is not tuned after runtime.
    private static readonly int[] Thresholds =
    {
        1, 2, 4, 8, 16, 32
    };

    private static readonly BindingFlags InstanceFlags =
        BindingFlags.Instance |
        BindingFlags.Public |
        BindingFlags.NonPublic;

    private static bool _installed;

    private KiwiCommonTensorBackendStageIsolationV44_55_20 _dependency;
    private Type _dependencyType;

    private FieldInfo _pairPendingField;
    private FieldInfo _pendingRecordField;
    private FieldInfo _pairTokenField;
    private FieldInfo _referenceInputDoneField;
    private FieldInfo _referenceNchwField;
    private FieldInfo _mode2NchwField;
    private FieldInfo _recordsField;
    private FieldInfo _reportWrittenField;
    private FieldInfo _observerFaultCountField;
    private FieldInfo _sourceIdentityMismatchCountField;
    private FieldInfo _referenceTensorNonFiniteCountField;
    private FieldInfo _referenceInputReadbackFailureCountField;
    private FieldInfo _lanesField;

    private FieldInfo _laneInputField;
    private FieldInfo _lanePendingOutputField;
    private FieldInfo _laneReadbackPendingField;
    private FieldInfo _lanePendingSourceHostTicksField;
    private FieldInfo _lanePendingStartedHostTicksField;

    private ComputeShader _snapshotCopyShader;
    private int _snapshotCopyKernel = -1;

    private bool _bound;
    private bool _reportWritten;
    private int _observerFaultCount;
    private int _dependencyMissingFrames;
    private int _attachMissCount;
    private int _arrayFreezeMismatchCount;
    private int _productionInputReadbackFailureCount;
    private int _productionInputNonFiniteCount;
    private int _laneIdentityMismatchAtRequestCount;
    private int _laneChangedBeforeReadbackCompletionCount;
    private int _snapshotSubmissionIdentityMismatchCount;
    private int _snapshotDispatchFailureCount;
    private int _productionOutputReadyObservationFailureCount;
    private int _productionParityContractMismatchCount;
    private int _duplicatePairRequestCount;
    private int _completedResultCount;
    private int _lastAttachedPairIndex = -1;
    private int _lastFinalizedPairIndex = -1;

    private double _dependencyCompleteSince = -1.0;

    private readonly Dictionary<int, CaptureState> _captures =
        new Dictionary<int, CaptureState>();

    private readonly HashSet<int> _requestedPairIndices =
        new HashSet<int>();

    private readonly List<PairResult> _results =
        new List<PairResult>(160);

    private readonly AggregateParity _productionVsReference =
        new AggregateParity("PRODUCTION_INPUT_VS_V20_REFERENCE");

    private readonly AggregateParity _productionVsMode2 =
        new AggregateParity("PRODUCTION_INPUT_VS_MODE2");

    private readonly AggregateParity _referenceVsMode2 =
        new AggregateParity("V20_REFERENCE_VS_MODE2");

    private int _anomalousPairCount;
    private int _productionMatchesReferenceCount;
    private int _productionMatchesMode2Count;
    private int _ambiguousAnomalousPairCount;

    private sealed class CaptureState
    {
        internal int PairIndex;
        internal int PairToken;
        internal ulong Sequence;
        internal long NativeHostTicks;
        internal long ManagedHostTicks;
        internal long LaneStartedHostTicks;
        internal object Lane;
        internal Tensor<float> ProductionInputTensor;
        internal ComputeBuffer SnapshotBuffer;

        internal readonly float[] ProductionInput =
            new float[InputFloatCount];

        internal readonly float[] Reference =
            new float[InputFloatCount];

        internal readonly float[] Mode2 =
            new float[InputFloatCount];

        internal bool ProductionReadbackRequested;
        internal bool ProductionOutputReadyObserved;
        internal bool SnapshotSubmitted;
        internal bool ProductionReadbackDone;
        internal bool DependencyArraysFrozen;
        internal bool ProductionReadbackFailed;
        internal bool LaneMatchedAtRequest;
        internal bool LaneMatchedAtSnapshotSubmission;
        internal bool LaneMatchedAtReadbackCompletion;
        internal bool DependencyLaneStillMatchedAfterCapture;
        internal int AttachUnityFrame;
        internal int OutputReadyUnityFrame;
        internal int SnapshotSubmissionUnityFrame;
        internal int RequestUnityFrame;
        internal int ReadbackCompletionUnityFrame;
        internal long AttachStopwatchTicks;
        internal long OutputReadyStopwatchTicks;
        internal long SnapshotSubmissionStopwatchTicks;
        internal long RequestStopwatchTicks;
        internal long ReadbackDoneStopwatchTicks;
        internal double DependencyRecordedReferenceVsMode2MeanAbsLsb;
    }

    private sealed class PairResult
    {
        internal int PairIndex;
        internal ulong Sequence;
        internal long NativeHostTicks;
        internal long ManagedHostTicks;
        internal long LaneStartedHostTicks;
        internal bool DependencyLaneStillMatchedAfterCapture;
        internal bool LaneMatchedAtRequest;
        internal bool LaneMatchedAtSnapshotSubmission;
        internal bool LaneMatchedAtReadbackCompletion;
        internal int AttachUnityFrame;
        internal int OutputReadyUnityFrame;
        internal int SnapshotSubmissionUnityFrame;
        internal int RequestUnityFrame;
        internal int ReadbackCompletionUnityFrame;
        internal double LaneAgeAtAttachMs;
        internal double OutputReadyWaitMs;
        internal double SnapshotSubmissionAfterOutputReadyMs;
        internal double LaneAgeAtRequestMs;
        internal double ProductionInputReadbackMs;

        internal PairParity ProductionVsReference;
        internal PairParity ProductionVsMode2;
        internal PairParity ReferenceVsMode2;

        internal string AnomalyClassification;
    }

    private struct PairParity
    {
        internal long Count;
        internal long ExactCount;
        internal int NonFiniteCount;
        internal double MeanAbsLsb;
        internal double RmseLsb;
        internal double MaxAbsLsb;
        internal long Ge1;
        internal long Ge2;
        internal long Ge4;
        internal long Ge8;
        internal long Ge16;
        internal long Ge32;

        internal double ExactRatio =>
            Count > 0
                ? ExactCount / (double)Count
                : 0.0;

        internal long ThresholdCount(int threshold)
        {
            switch (threshold)
            {
                case 1: return Ge1;
                case 2: return Ge2;
                case 4: return Ge4;
                case 8: return Ge8;
                case 16: return Ge16;
                case 32: return Ge32;
                default: return 0;
            }
        }
    }

    private sealed class AggregateParity
    {
        internal readonly string Name;

        internal long Count;
        internal long ExactCount;
        internal int PairCount;
        internal int NonFiniteCount;
        internal double SumAbsLsb;
        internal double SumSquareLsb;
        internal double MaxAbsLsb;
        internal readonly long[] ThresholdCounts =
            new long[Thresholds.Length];

        internal AggregateParity(string name)
        {
            Name = name;
        }

        internal double MeanAbsLsb =>
            Count > 0
                ? SumAbsLsb / Count
                : 0.0;

        internal double RmseLsb =>
            Count > 0
                ? Math.Sqrt(SumSquareLsb / Count)
                : 0.0;

        internal double ExactRatio =>
            Count > 0
                ? ExactCount / (double)Count
                : 0.0;

        internal long ThresholdCount(int threshold)
        {
            for (int i = 0; i < Thresholds.Length; i++)
            {
                if (Thresholds[i] == threshold)
                {
                    return ThresholdCounts[i];
                }
            }

            return 0;
        }

        internal PairParity Accumulate(
            float[] left,
            float[] right)
        {
            PairParity result =
                ComputePairParity(
                    left,
                    right);

            Accumulate(
                result);

            return result;
        }

        internal void Accumulate(
            PairParity result)
        {
            PairCount++;
            Count += result.Count;
            ExactCount += result.ExactCount;
            NonFiniteCount += result.NonFiniteCount;
            SumAbsLsb +=
                result.MeanAbsLsb *
                result.Count;
            SumSquareLsb +=
                result.RmseLsb *
                result.RmseLsb *
                result.Count;
            MaxAbsLsb =
                Math.Max(
                    MaxAbsLsb,
                    result.MaxAbsLsb);

            for (int i = 0; i < Thresholds.Length; i++)
            {
                ThresholdCounts[i] +=
                    result.ThresholdCount(
                        Thresholds[i]);
            }
        }
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (
            _installed ||
            !ReadBoolEnvironment(
                EnableVariable,
                false))
        {
            return;
        }

        _installed = true;

        GameObject go =
            new GameObject(
                "[Kiwi] v44.55.22 Reference Pixel Transaction Integrity");

        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.DontSave;

        KiwiReferencePixelTransactionIntegrityV44_55_22 observer =
            go.AddComponent<
                KiwiReferencePixelTransactionIntegrityV44_55_22>();

        KiwiReferencePixelTransactionIntegrityEarlyProbeV44_55_22 earlyProbe =
            go.AddComponent<
                KiwiReferencePixelTransactionIntegrityEarlyProbeV44_55_22>();

        earlyProbe.Owner =
            observer;
    }

    private void Awake()
    {
        Debug.Log(
            "[Kiwi v44.55.22 RefTransaction] WAIT_DEPENDENCY" +
            " contract=" + Contract +
            " observerOnly=1" +
            " productionWrites=0" +
            " nativeWrites=0" +
            " trackingMathChange=0" +
            " thresholdChange=0" +
            " addedWorker=0" +
            " addedBlit=0" +
            " addedRenderTexture=0" +
            " addedComputeBufferSnapshot=1" +
            " addedComputeDispatchPerPair=1" +
            " addedReadback=1" +
            " blockingWait=0" +
            " performanceAuthority=0" +
            " productionInputAuthority=OBSERVER_GPU_SNAPSHOT_OF_ACTUAL_LANE_INPUT" +
            " productionOutputCompletionAuthority=PENDING_OUTPUT_ISREADBACKREQUESTDONE" +
            " earlyCompletionPoller=1" +
            " dependency=KIWI_V44_55_20_COMMON_TENSOR_AUDIT");
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

        ObserveCurrentDependencyPair();
        TryFreezeCurrentDependencyArrays();
        TryFinalizeReadyCaptures();
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

        // v44.55.20 starts measured pairs from LateUpdate. Because this
        // component executes later (33000), this is the earliest observer-only
        // point at which the newly selected Production lane can be read.
        ObserveCurrentDependencyPair();
        TryFreezeCurrentDependencyArrays();
        TryFinalizeReadyCaptures();
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

            _pendingRecordField =
                RequiredField(
                    _dependencyType,
                    "_pendingRecord");

            _pairTokenField =
                RequiredField(
                    _dependencyType,
                    "_pairToken");

            _referenceInputDoneField =
                RequiredField(
                    _dependencyType,
                    "_referenceInputDone");

            _referenceNchwField =
                RequiredField(
                    _dependencyType,
                    "_referenceNchw");

            _mode2NchwField =
                RequiredField(
                    _dependencyType,
                    "_rightNchw");

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

            _lanesField =
                RequiredField(
                    _dependencyType,
                    "_lanes");

            Array lanes =
                _lanesField.GetValue(
                    _dependency)
                as Array;

            // v44.55.20 and v44.55.22 are both bootstrapped from
            // RuntimeInitializeOnLoadMethod(AfterSceneLoad). Unity does not
            // guarantee ordering between callbacks at the same load phase.
            // Therefore the dependency object may legitimately exist before
            // v44.55.20 has discovered the tracker and populated _lanes.
            // Treat that state as transient and retry on a later Update.
            if (
                lanes == null ||
                lanes.Length == 0)
            {
                return;
            }

            object lane0 =
                lanes.GetValue(0);

            if (lane0 == null)
            {
                return;
            }

            Type laneType =
                lane0.GetType();

            _laneInputField =
                RequiredField(
                    laneType,
                    "input");

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

            _snapshotCopyShader =
                Resources.Load<ComputeShader>(
                    SnapshotCopyResourcePath);

            if (_snapshotCopyShader == null)
            {
                throw new InvalidOperationException(
                    "Observer snapshot compute shader is unavailable: " +
                    SnapshotCopyResourcePath);
            }

            _snapshotCopyKernel =
                _snapshotCopyShader.FindKernel(
                    "CopyTensorSnapshot");

            _bound = true;

            Debug.Log(
                "[Kiwi v44.55.22 RefTransaction] DEPENDENCY_BOUND" +
                " executionOrder=33000" +
                " reflectionReadOnly=1" +
                " productionLaneInputSnapshot=OBSERVER_GPU_BUFFER" +
                " snapshotCopy=COMPUTE_DISPATCH" +
                " snapshotReadback=COMMAND_BUFFER_ASYNC" +
                " earlyCompletionPoller=1" +
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

    private void ObserveCurrentDependencyPair()
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

        object record =
            _pendingRecordField.GetValue(
                _dependency);

        // Warmup pairs deliberately carry no PairRecord.
        if (record == null)
        {
            return;
        }

        int pairIndex =
            ReadMemberInt(
                record,
                "Index");

        if (pairIndex < 0)
        {
            RegisterFault(
                "PAIR_INDEX_INVALID");
            return;
        }

        if (
            _requestedPairIndices.Contains(
                pairIndex))
        {
            return;
        }

        if (
            _lastAttachedPairIndex >= 0 &&
            pairIndex >
                _lastAttachedPairIndex + 1)
        {
            _attachMissCount++;

            RegisterFault(
                "PAIR_ATTACH_JUMP previous=" +
                _lastAttachedPairIndex +
                " current=" +
                pairIndex);

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

        bool dependencyLaneStillMatched =
            ReadMemberBool(
                record,
                "LaneStillMatchedAfterCapture");

        if (
            sequence == 0UL ||
            nativeHostTicks <= 0L ||
            managedHostTicks <= 0L ||
            laneStartedHostTicks <= 0L ||
            !dependencyLaneStillMatched)
        {
            _laneIdentityMismatchAtRequestCount++;

            RegisterFault(
                "DEPENDENCY_PAIR_IDENTITY_INVALID index=" +
                pairIndex);

            return;
        }

        Array lanes =
            _lanesField.GetValue(
                _dependency)
                as Array;

        object matchedLane =
            FindMatchingLane(
                lanes,
                managedHostTicks,
                laneStartedHostTicks);

        if (matchedLane == null)
        {
            _laneIdentityMismatchAtRequestCount++;

            RegisterFault(
                "PRODUCTION_LANE_NOT_MATCHED index=" +
                pairIndex);

            return;
        }

        Tensor<float> productionInput =
            _laneInputField.GetValue(
                matchedLane)
                as Tensor<float>;

        if (
            productionInput == null ||
            productionInput.shape.length !=
                InputFloatCount)
        {
            RegisterFault(
                "PRODUCTION_INPUT_TENSOR_INVALID index=" +
                pairIndex);

            return;
        }

        CaptureState state =
            new CaptureState
            {
                PairIndex =
                    pairIndex,
                PairToken =
                    ReadFieldInt(
                        _dependency,
                        _pairTokenField),
                Sequence =
                    sequence,
                NativeHostTicks =
                    nativeHostTicks,
                ManagedHostTicks =
                    managedHostTicks,
                LaneStartedHostTicks =
                    laneStartedHostTicks,
                Lane =
                    matchedLane,
                ProductionInputTensor =
                    productionInput,
                LaneMatchedAtRequest =
                    LaneMatches(
                        matchedLane,
                        managedHostTicks,
                        laneStartedHostTicks),
                DependencyLaneStillMatchedAfterCapture =
                    dependencyLaneStillMatched,
                AttachUnityFrame =
                    Time.frameCount,
                AttachStopwatchTicks =
                    Stopwatch.GetTimestamp()
            };

        if (!state.LaneMatchedAtRequest)
        {
            _laneIdentityMismatchAtRequestCount++;

            RegisterFault(
                "LANE_CHANGED_BEFORE_INPUT_REQUEST index=" +
                pairIndex);

            return;
        }

        _requestedPairIndices.Add(
            pairIndex);

        _captures.Add(
            pairIndex,
            state);

        _lastAttachedPairIndex =
            pairIndex;

        Debug.Log(
            "[Kiwi v44.55.22 RefTransaction] PAIR_ATTACHED" +
            " index=" + pairIndex +
            " sequence=" + sequence +
            " laneAgeMs=" +
            F(
                HostTicksToMilliseconds(
                    state.AttachStopwatchTicks -
                    laneStartedHostTicks)) +
            " waitingProductionOutputReady=1");
    }

    internal void PollProductionOutputReadyBeforeProductionUpdate()
    {
        if (
            _reportWritten ||
            !_bound ||
            _captures.Count == 0)
        {
            return;
        }

        foreach (
            KeyValuePair<int, CaptureState> item
            in _captures)
        {
            CaptureState state =
                item.Value;

            if (
                state == null ||
                state.ProductionReadbackFailed ||
                state.ProductionReadbackRequested)
            {
                continue;
            }

            try
            {
                if (
                    !LaneMatches(
                        state.Lane,
                        state.ManagedHostTicks,
                        state.LaneStartedHostTicks))
                {
                    _laneIdentityMismatchAtRequestCount++;

                    state.ProductionReadbackFailed =
                        true;

                    RegisterFault(
                        "LANE_CHANGED_BEFORE_OUTPUT_READY index=" +
                        state.PairIndex);

                    continue;
                }

                Tensor<float> pendingOutput =
                    _lanePendingOutputField.GetValue(
                        state.Lane)
                        as Tensor<float>;

                if (pendingOutput == null)
                {
                    _productionOutputReadyObservationFailureCount++;

                    state.ProductionReadbackFailed =
                        true;

                    RegisterFault(
                        "PRODUCTION_PENDING_OUTPUT_MISSING index=" +
                        state.PairIndex);

                    continue;
                }

                if (!pendingOutput.IsReadbackRequestDone())
                {
                    continue;
                }

                // Re-check identity after observing output completion. The
                // early probe executes before normal Production Update, so the
                // lane still belongs to the measured transaction at this point.
                if (
                    !LaneMatches(
                        state.Lane,
                        state.ManagedHostTicks,
                        state.LaneStartedHostTicks))
                {
                    _laneIdentityMismatchAtRequestCount++;

                    state.ProductionReadbackFailed =
                        true;

                    RegisterFault(
                        "LANE_CHANGED_AT_OUTPUT_READY index=" +
                        state.PairIndex);

                    continue;
                }

                state.ProductionOutputReadyObserved =
                    true;

                state.OutputReadyUnityFrame =
                    Time.frameCount;

                state.OutputReadyStopwatchTicks =
                    Stopwatch.GetTimestamp();

                state.RequestUnityFrame =
                    Time.frameCount;

                state.RequestStopwatchTicks =
                    Stopwatch.GetTimestamp();

                state.ProductionReadbackRequested =
                    true;

                Debug.Log(
                    "[Kiwi v44.55.22 RefTransaction] OUTPUT_READY" +
                    " index=" + state.PairIndex +
                    " sequence=" + state.Sequence +
                    " waitMs=" +
                    F(
                        HostTicksToMilliseconds(
                            state.OutputReadyStopwatchTicks -
                            state.AttachStopwatchTicks)) +
                    " laneMatched=1");

                BeginProductionInputReadback(
                    state);
            }
            catch (Exception exception)
            {
                _productionOutputReadyObservationFailureCount++;

                state.ProductionReadbackFailed =
                    true;

                RegisterFault(
                    "PRODUCTION_OUTPUT_READY_OBSERVE_FAIL index=" +
                    state.PairIndex +
                    " " +
                    exception.GetType().Name +
                    " " +
                    exception.Message);
            }
        }
    }

    private void BeginProductionInputReadback(
        CaptureState state)
    {
        if (
            state == null ||
            !state.ProductionOutputReadyObserved ||
            !state.ProductionReadbackRequested ||
            state.SnapshotSubmitted ||
            state.ProductionReadbackDone ||
            state.ProductionReadbackFailed)
        {
            return;
        }

        try
        {
            state.LaneMatchedAtSnapshotSubmission =
                LaneMatches(
                    state.Lane,
                    state.ManagedHostTicks,
                    state.LaneStartedHostTicks);

            state.LaneMatchedAtRequest =
                state.LaneMatchedAtSnapshotSubmission;

            if (!state.LaneMatchedAtSnapshotSubmission)
            {
                _snapshotSubmissionIdentityMismatchCount++;
                _laneIdentityMismatchAtRequestCount++;

                state.ProductionReadbackFailed =
                    true;

                RegisterFault(
                    "SNAPSHOT_SUBMISSION_IDENTITY_MISMATCH index=" +
                    state.PairIndex);

                return;
            }

            ComputeTensorData sourceData =
                state
                    .ProductionInputTensor
                    .dataOnBackend
                    as ComputeTensorData;

            if (
                sourceData == null ||
                sourceData.buffer == null ||
                !sourceData.buffer.IsValid())
            {
                throw new InvalidOperationException(
                    "Production input ComputeTensorData is unavailable.");
            }

            ComputeBuffer sourceBuffer =
                sourceData.buffer;

            if (
                sourceBuffer.count != InputFloatCount ||
                sourceBuffer.stride != sizeof(float))
            {
                throw new InvalidOperationException(
                    "Production input ComputeBuffer shape mismatch. count=" +
                    sourceBuffer.count +
                    " stride=" +
                    sourceBuffer.stride);
            }

            state.SnapshotBuffer =
                new ComputeBuffer(
                    InputFloatCount,
                    sizeof(float),
                    ComputeBufferType.Structured);

            state.SnapshotBuffer.name =
                "KiwiV44_55_22_InputSnapshot_" +
                state.PairIndex;

            state.SnapshotSubmissionUnityFrame =
                Time.frameCount;

            state.SnapshotSubmissionStopwatchTicks =
                Stopwatch.GetTimestamp();

            state.RequestUnityFrame =
                state.SnapshotSubmissionUnityFrame;

            state.RequestStopwatchTicks =
                state.SnapshotSubmissionStopwatchTicks;

            CommandBuffer commandBuffer =
                new CommandBuffer
                {
                    name =
                        "Kiwi v44.55.22 Input Snapshot " +
                        state.PairIndex
                };

            try
            {
                commandBuffer.SetComputeIntParam(
                    _snapshotCopyShader,
                    "_Count",
                    InputFloatCount);

                commandBuffer.SetComputeBufferParam(
                    _snapshotCopyShader,
                    _snapshotCopyKernel,
                    "_Source",
                    sourceBuffer);

                commandBuffer.SetComputeBufferParam(
                    _snapshotCopyShader,
                    _snapshotCopyKernel,
                    "_Destination",
                    state.SnapshotBuffer);

                int groups =
                    (
                        InputFloatCount +
                        SnapshotCopyThreads -
                        1
                    ) /
                    SnapshotCopyThreads;

                commandBuffer.DispatchCompute(
                    _snapshotCopyShader,
                    _snapshotCopyKernel,
                    groups,
                    1,
                    1);

                commandBuffer.RequestAsyncReadback(
                    state.SnapshotBuffer,
                    request =>
                    {
                        CompleteSnapshotReadback(
                            state,
                            request);
                    });

                // CPU submission only. There is no blocking GPU wait.
                // Unity tracks the source read / later Production write resource
                // dependency across graphics and async-compute queues.
                Graphics.ExecuteCommandBuffer(
                    commandBuffer);

                state.SnapshotSubmitted =
                    true;

                Debug.Log(
                    "[Kiwi v44.55.22 RefTransaction] SNAPSHOT_SUBMITTED" +
                    " index=" + state.PairIndex +
                    " sequence=" + state.Sequence +
                    " laneMatched=1" +
                    " sourceBackend=GPUCompute" +
                    " count=" + InputFloatCount);
            }
            finally
            {
                commandBuffer.Release();
            }
        }
        catch (Exception exception)
        {
            _snapshotDispatchFailureCount++;
            _productionInputReadbackFailureCount++;

            state.ProductionReadbackFailed =
                true;

            ReleaseSnapshotBuffer(
                state);

            RegisterFault(
                "PRODUCTION_INPUT_SNAPSHOT_SUBMIT_FAIL index=" +
                state.PairIndex +
                " " +
                exception.GetType().Name +
                " " +
                exception.Message);
        }
    }

    private void CompleteSnapshotReadback(
        CaptureState state,
        AsyncGPUReadbackRequest request)
    {
        try
        {
            if (
                state == null ||
                _reportWritten)
            {
                return;
            }

            if (
                request.hasError)
            {
                throw new InvalidOperationException(
                    "Observer snapshot AsyncGPUReadback failed.");
            }

            var data =
                request.GetData<float>();

            if (data.Length != InputFloatCount)
            {
                throw new InvalidOperationException(
                    "Observer snapshot readback length mismatch: " +
                    data.Length);
            }

            for (
                int i = 0;
                i < InputFloatCount;
                i++)
            {
                float value =
                    data[i];

                state.ProductionInput[i] =
                    value;

                if (!IsFinite(value))
                {
                    _productionInputNonFiniteCount++;
                }
            }

            state.ReadbackDoneStopwatchTicks =
                Stopwatch.GetTimestamp();

            state.ReadbackCompletionUnityFrame =
                Time.frameCount;

            // Informational only after FIX4. The Production source was frozen
            // into an observer-owned GPU buffer at SnapshotSubmitted. Therefore
            // later lane reuse cannot mutate the readback source.
            state.LaneMatchedAtReadbackCompletion =
                LaneMatches(
                    state.Lane,
                    state.ManagedHostTicks,
                    state.LaneStartedHostTicks);

            if (
                !state.LaneMatchedAtReadbackCompletion)
            {
                _laneChangedBeforeReadbackCompletionCount++;
            }

            state.ProductionReadbackDone =
                true;

            Debug.Log(
                "[Kiwi v44.55.22 RefTransaction] SNAPSHOT_READBACK" +
                " index=" + state.PairIndex +
                " sequence=" + state.Sequence +
                " laneStillMatched=" +
                B(
                    state.LaneMatchedAtReadbackCompletion) +
                " source=OBSERVER_OWNED_GPU_SNAPSHOT");

            TryFinalizeReadyCaptures();
        }
        catch (Exception exception)
        {
            if (state != null)
            {
                state.ProductionReadbackFailed =
                    true;
            }

            _productionInputReadbackFailureCount++;

            RegisterFault(
                "PRODUCTION_INPUT_SNAPSHOT_READBACK_FAIL index=" +
                (
                    state != null
                        ? state.PairIndex
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
                state);
        }
    }

    private static void ReleaseSnapshotBuffer(
        CaptureState state)
    {
        if (
            state == null ||
            state.SnapshotBuffer == null)
        {
            return;
        }

        try
        {
            state.SnapshotBuffer.Release();
        }
        catch
        {
        }

        state.SnapshotBuffer =
            null;
    }

    private void TryFreezeCurrentDependencyArrays()
    {
        if (
            !_bound ||
            _captures.Count == 0)
        {
            return;
        }

        bool pairPending =
            ReadFieldBool(
                _dependency,
                _pairPendingField);

        object currentPendingRecord =
            pairPending
                ? _pendingRecordField.GetValue(
                    _dependency)
                : null;

        int currentPendingIndex =
            currentPendingRecord != null
                ? ReadMemberInt(
                    currentPendingRecord,
                    "Index")
                : -1;

        bool referenceInputDone =
            ReadFieldBool(
                _dependency,
                _referenceInputDoneField);

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
            RegisterFault(
                "DEPENDENCY_INPUT_ARRAY_INVALID");
            return;
        }

        // Preferred safe window: the pair is still pending and v44.55.20 has
        // completed its REF tensor readback for that same pair.
        if (
            currentPendingIndex >= 0 &&
            referenceInputDone &&
            _captures.TryGetValue(
                currentPendingIndex,
                out CaptureState pendingState) &&
            !pendingState.DependencyArraysFrozen)
        {
            FreezeDependencyArrays(
                pendingState,
                currentPendingRecord,
                reference,
                mode2);

            return;
        }

        // Second safe window: v44.55.20 finalized a pair during this frame and
        // no newer measured pair has started yet. Update executes before the
        // next LateUpdate that can overwrite the shared candidate arrays.
        if (!pairPending)
        {
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

            object lastRecord =
                records[
                    records.Count - 1];

            int lastIndex =
                ReadMemberInt(
                    lastRecord,
                    "Index");

            if (
                _captures.TryGetValue(
                    lastIndex,
                    out CaptureState completedState) &&
                !completedState.DependencyArraysFrozen)
            {
                FreezeDependencyArrays(
                    completedState,
                    lastRecord,
                    reference,
                    mode2);
            }
        }
    }

    private void FreezeDependencyArrays(
        CaptureState state,
        object record,
        float[] reference,
        float[] mode2)
    {
        if (
            state == null ||
            record == null ||
            state.DependencyArraysFrozen)
        {
            return;
        }

        int recordIndex =
            ReadMemberInt(
                record,
                "Index");

        ulong recordSequence =
            ReadMemberULong(
                record,
                "Sequence");

        if (
            recordIndex !=
                state.PairIndex ||
            recordSequence !=
                state.Sequence)
        {
            _arrayFreezeMismatchCount++;

            RegisterFault(
                "DEPENDENCY_ARRAY_PAIR_IDENTITY_MISMATCH expected=" +
                state.PairIndex +
                "/" +
                state.Sequence +
                " actual=" +
                recordIndex +
                "/" +
                recordSequence);

            return;
        }

        Array.Copy(
            reference,
            state.Reference,
            InputFloatCount);

        Array.Copy(
            mode2,
            state.Mode2,
            InputFloatCount);

        state.DependencyRecordedReferenceVsMode2MeanAbsLsb =
            ReadMemberDouble(
                record,
                "ReferenceVsMode2InputMeanAbsLsb");

        PairParity recomputed =
            ComputePairParity(
                state.Reference,
                state.Mode2);

        if (
            recomputed.NonFiniteCount > 0 ||
            Math.Abs(
                recomputed.MeanAbsLsb -
                state.DependencyRecordedReferenceVsMode2MeanAbsLsb) >
                PairMeanToleranceLsb)
        {
            _arrayFreezeMismatchCount++;

            RegisterFault(
                "DEPENDENCY_ARRAY_RECOMPUTE_MISMATCH index=" +
                state.PairIndex +
                " dependencyMean=" +
                F(
                    state.DependencyRecordedReferenceVsMode2MeanAbsLsb) +
                " recomputedMean=" +
                F(
                    recomputed.MeanAbsLsb));

            return;
        }

        state.DependencyArraysFrozen =
            true;
    }

    private void TryFinalizeReadyCaptures()
    {
        if (_captures.Count == 0)
        {
            return;
        }

        List<int> ready =
            null;

        foreach (
            KeyValuePair<int, CaptureState> item
            in _captures)
        {
            CaptureState state =
                item.Value;

            if (state == null)
            {
                continue;
            }

            // FIX4 reads from an observer-owned GPU snapshot. Lane reuse
            // after snapshot submission is informational and cannot mutate the
            // readback source. Snapshot-submission identity remains fail-closed.

            if (
                state.ProductionReadbackFailed ||
                !state.ProductionReadbackDone ||
                !state.DependencyArraysFrozen)
            {
                continue;
            }

            if (ready == null)
            {
                ready =
                    new List<int>();
            }

            ready.Add(
                item.Key);
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
            int pairIndex =
                ready[i];

            CaptureState state =
                _captures[pairIndex];

            FinalizeCapture(
                state);

            _captures.Remove(
                pairIndex);
        }
    }

    private void FinalizeCapture(
        CaptureState state)
    {
        if (state == null)
        {
            return;
        }

        PairParity productionVsReference =
            ComputePairParity(
                state.ProductionInput,
                state.Reference);

        PairParity productionVsMode2 =
            ComputePairParity(
                state.ProductionInput,
                state.Mode2);

        PairParity referenceVsMode2 =
            ComputePairParity(
                state.Reference,
                state.Mode2);

        if (
            productionVsReference.NonFiniteCount > 0 ||
            productionVsMode2.NonFiniteCount > 0 ||
            referenceVsMode2.NonFiniteCount > 0)
        {
            RegisterFault(
                "NONFINITE_PARITY index=" +
                state.PairIndex);
            return;
        }

        // A pair whose REF and mode2 tensors have no >=4 LSB disagreement
        // cannot legitimately classify as NORMAL when the supposedly same
        // Production input is >=4 LSB away from both. Such a result means the
        // Production-input readback transaction is not authoritative.
        if (
            referenceVsMode2.Ge4 == 0 &&
            productionVsReference.Ge4 > 0 &&
            productionVsMode2.Ge4 > 0)
        {
            _productionParityContractMismatchCount++;

            RegisterFault(
                "PRODUCTION_INPUT_TRANSACTION_CONTRACT_MISMATCH index=" +
                state.PairIndex +
                " sequence=" +
                state.Sequence +
                " refMode2Ge4=" +
                referenceVsMode2.Ge4 +
                " prodRefGe4=" +
                productionVsReference.Ge4 +
                " prodMode2Ge4=" +
                productionVsMode2.Ge4);

            WriteReport(
                "PRODUCTION_INPUT_TRANSACTION_CONTRACT_MISMATCH");

            return;
        }

        // Only contract-valid pairs may enter parity aggregates or
        // classification.
        _productionVsReference.Accumulate(
            productionVsReference);

        _productionVsMode2.Accumulate(
            productionVsMode2);

        _referenceVsMode2.Accumulate(
            referenceVsMode2);

        bool anomalous =
            referenceVsMode2.Ge4 > 0;

        string classification =
            "NORMAL_NO_GE4";

        if (anomalous)
        {
            _anomalousPairCount++;

            bool productionMatchesReferenceAtGe4 =
                productionVsReference.Ge4 == 0 &&
                productionVsMode2.Ge4 > 0;

            bool productionMatchesMode2AtGe4 =
                productionVsMode2.Ge4 == 0 &&
                productionVsReference.Ge4 > 0;

            if (productionMatchesReferenceAtGe4)
            {
                classification =
                    "PRODUCTION_MATCHES_REFERENCE";

                _productionMatchesReferenceCount++;
            }
            else if (productionMatchesMode2AtGe4)
            {
                classification =
                    "PRODUCTION_MATCHES_MODE2";

                _productionMatchesMode2Count++;
            }
            else
            {
                classification =
                    "ANOMALOUS_AMBIGUOUS";

                _ambiguousAnomalousPairCount++;
            }
        }

        PairResult result =
            new PairResult
            {
                PairIndex =
                    state.PairIndex,
                Sequence =
                    state.Sequence,
                NativeHostTicks =
                    state.NativeHostTicks,
                ManagedHostTicks =
                    state.ManagedHostTicks,
                LaneStartedHostTicks =
                    state.LaneStartedHostTicks,
                DependencyLaneStillMatchedAfterCapture =
                    state.DependencyLaneStillMatchedAfterCapture,
                LaneMatchedAtRequest =
                    state.LaneMatchedAtRequest,
                LaneMatchedAtSnapshotSubmission =
                    state.LaneMatchedAtSnapshotSubmission,
                LaneMatchedAtReadbackCompletion =
                    state.LaneMatchedAtReadbackCompletion,
                AttachUnityFrame =
                    state.AttachUnityFrame,
                OutputReadyUnityFrame =
                    state.OutputReadyUnityFrame,
                SnapshotSubmissionUnityFrame =
                    state.SnapshotSubmissionUnityFrame,
                RequestUnityFrame =
                    state.RequestUnityFrame,
                ReadbackCompletionUnityFrame =
                    state.ReadbackCompletionUnityFrame,
                LaneAgeAtAttachMs =
                    HostTicksToMilliseconds(
                        state.AttachStopwatchTicks -
                        state.LaneStartedHostTicks),
                OutputReadyWaitMs =
                    HostTicksToMilliseconds(
                        state.OutputReadyStopwatchTicks -
                        state.AttachStopwatchTicks),
                SnapshotSubmissionAfterOutputReadyMs =
                    HostTicksToMilliseconds(
                        state.SnapshotSubmissionStopwatchTicks -
                        state.OutputReadyStopwatchTicks),
                LaneAgeAtRequestMs =
                    HostTicksToMilliseconds(
                        state.RequestStopwatchTicks -
                        state.LaneStartedHostTicks),
                ProductionInputReadbackMs =
                    HostTicksToMilliseconds(
                        state.ReadbackDoneStopwatchTicks -
                        state.RequestStopwatchTicks),
                ProductionVsReference =
                    productionVsReference,
                ProductionVsMode2 =
                    productionVsMode2,
                ReferenceVsMode2 =
                    referenceVsMode2,
                AnomalyClassification =
                    classification
            };

        _results.Add(
            result);

        _completedResultCount++;
        _lastFinalizedPairIndex =
            Math.Max(
                _lastFinalizedPairIndex,
                state.PairIndex);

        Debug.Log(
            "[Kiwi v44.55.22 RefTransaction] SAMPLE" +
            " index=" + state.PairIndex +
            " sequence=" + state.Sequence +
            " refMode2Ge4=" +
            referenceVsMode2.Ge4 +
            " prodRefGe4=" +
            productionVsReference.Ge4 +
            " prodMode2Ge4=" +
            productionVsMode2.Ge4 +
            " class=" +
            classification +
            " laneMatchedAtReadback=" +
            B(
                state.LaneMatchedAtReadbackCompletion));
    }

    private void TryFinishAfterDependency()
    {
        if (
            !_bound ||
            _reportWritten)
        {
            return;
        }

        bool dependencyComplete =
            ReadFieldBool(
                _dependency,
                _reportWrittenField);

        if (!dependencyComplete)
        {
            _dependencyCompleteSince =
                -1.0;
            return;
        }

        IList records =
            _recordsField.GetValue(
                _dependency)
                as IList;

        int dependencyRecordCount =
            records != null
                ? records.Count
                : 0;

        if (
            _dependencyCompleteSince <
            0.0)
        {
            _dependencyCompleteSince =
                Time.realtimeSinceStartupAsDouble;
        }

        TryFreezeCurrentDependencyArrays();
        TryFinalizeReadyCaptures();

        bool allResultsPresent =
            _captures.Count == 0 &&
            _completedResultCount ==
                dependencyRecordCount;

        if (allResultsPresent)
        {
            WriteReport(
                "DEPENDENCY_COMPLETE");
            return;
        }

        if (
            Time.realtimeSinceStartupAsDouble -
                _dependencyCompleteSince >=
            DependencyCompletionGraceSeconds)
        {
            RegisterFault(
                "DEPENDENCY_COMPLETE_WITH_PENDING_CAPTURE" +
                " dependencyRecords=" +
                dependencyRecordCount +
                " completed=" +
                _completedResultCount +
                " capturesPending=" +
                _captures.Count);

            WriteReport(
                "DEPENDENCY_COMPLETE_PENDING_TIMEOUT");
        }
    }

    private object FindMatchingLane(
        Array lanes,
        long managedHostTicks,
        long laneStartedHostTicks)
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
                lanes.GetValue(i);

            if (
                lane != null &&
                LaneMatches(
                    lane,
                    managedHostTicks,
                    laneStartedHostTicks))
            {
                return lane;
            }
        }

        return null;
    }

    private bool LaneMatches(
        object lane,
        long managedHostTicks,
        long laneStartedHostTicks)
    {
        if (lane == null)
        {
            return false;
        }

        try
        {
            return
                ReadLaneBool(
                    lane,
                    _laneReadbackPendingField) &&
                ReadLaneLong(
                    lane,
                    _lanePendingSourceHostTicksField) ==
                    managedHostTicks &&
                ReadLaneLong(
                    lane,
                    _lanePendingStartedHostTicksField) ==
                    laneStartedHostTicks;
        }
        catch
        {
            return false;
        }
    }

    private string DetermineDecision(
        string status)
    {
        int dependencyFaults =
            ReadDependencyFaultCount();

        IList records =
            _recordsField != null &&
            _dependency != null
                ? _recordsField.GetValue(
                    _dependency)
                    as IList
                : null;

        int dependencyRecordCount =
            records != null
                ? records.Count
                : 0;

        bool invalid =
            !string.Equals(
                status,
                "DEPENDENCY_COMPLETE",
                StringComparison.Ordinal) ||
            _observerFaultCount > 0 ||
            dependencyFaults > 0 ||
            _attachMissCount > 0 ||
            _arrayFreezeMismatchCount > 0 ||
            _productionInputReadbackFailureCount > 0 ||
            _productionInputNonFiniteCount > 0 ||
            _laneIdentityMismatchAtRequestCount > 0 ||
            _snapshotSubmissionIdentityMismatchCount > 0 ||
            _snapshotDispatchFailureCount > 0 ||
            _productionOutputReadyObservationFailureCount > 0 ||
            _productionParityContractMismatchCount > 0 ||
            _duplicatePairRequestCount > 0 ||
            _captures.Count > 0 ||
            _completedResultCount !=
                dependencyRecordCount ||
            _productionVsReference.NonFiniteCount > 0 ||
            _productionVsMode2.NonFiniteCount > 0 ||
            _referenceVsMode2.NonFiniteCount > 0;

        if (invalid)
        {
            return "INVALID_OBSERVER";
        }

        if (
            _completedResultCount <
                MinimumCompletedPairs)
        {
            return "INSUFFICIENT_DATA";
        }

        if (
            _anomalousPairCount <
                MinimumAnomalousPairs)
        {
            return "INSUFFICIENT_ANOMALY_REPRODUCTION";
        }

        if (
            _productionMatchesMode2Count ==
                _anomalousPairCount)
        {
            return "REFERENCE_TRANSACTION_INVALID_CONFIRMED";
        }

        if (
            _productionMatchesReferenceCount ==
                _anomalousPairCount)
        {
            return "REFERENCE_TRANSACTION_VALID_CONFIRMED";
        }

        return "MIXED_TRANSACTION";
    }

    private int ReadDependencyFaultCount()
    {
        if (
            !_bound ||
            _dependency == null)
        {
            return 0;
        }

        return
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
            DetermineDecision(
                status);

        string directory =
            GetReportDirectory();

        string stamp =
            DateTime.Now.ToString(
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture);

        string textPath =
            Path.Combine(
                directory,
                "KiwiReferencePixelTransactionIntegrity_v44_55_22_" +
                stamp +
                ".txt");

        string csvPath =
            Path.Combine(
                directory,
                "KiwiReferencePixelTransactionIntegrity_v44_55_22_" +
                stamp +
                ".csv");

        IList dependencyRecords =
            _recordsField != null &&
            _dependency != null
                ? _recordsField.GetValue(
                    _dependency)
                    as IList
                : null;

        int dependencyRecordCount =
            dependencyRecords != null
                ? dependencyRecords.Count
                : 0;

        List<string> lines =
            new List<string>();

        lines.Add(
            "KiwiAvatarSystem v44.55.22 Reference Pixel Transaction Integrity Isolation");

        lines.Add(
            "contract=" +
            Contract);

        lines.Add(
            "status=" +
            status);

        lines.Add(
            "decision=" +
            decision);

        lines.Add(
            "observerOnly=1");

        lines.Add(
            "productionWrites=0");

        lines.Add(
            "nativeWrites=0");

        lines.Add(
            "trackingMathChange=0");

        lines.Add(
            "thresholdChange=0");

        lines.Add(
            "addedWorker=0");

        lines.Add(
            "addedBlit=0");

        lines.Add(
            "addedRenderTexture=0");

        lines.Add(
            "addedReadback=1");

        lines.Add(
            "blockingWait=0");

        lines.Add(
            "performanceAuthority=0");

        lines.Add(
            "productionInputAuthority=OBSERVER_GPU_SNAPSHOT_OF_ACTUAL_PRODUCTION_LANE_INPUT_AFTER_PENDING_OUTPUT_READY");

        lines.Add(
            "snapshotCopyAuthority=COMPUTE_DISPATCH_SOURCE_PRODUCTION_INPUT_DEST_OBSERVER_OWNED_COMPUTEBUFFER");

        lines.Add(
            "snapshotReadbackAuthority=COMMAND_BUFFER_REQUEST_ASYNC_READBACK_OF_OBSERVER_OWNED_BUFFER");

        lines.Add(
            "productionOutputCompletionAuthority=EXISTING_PRODUCTION_PENDING_OUTPUT_ISREADBACKREQUESTDONE");

        lines.Add(
            "laneIdentityValidityPoint=SNAPSHOT_SUBMISSION");

        lines.Add(
            "laneReuseAfterSnapshotSubmission=ALLOWED_SOURCE_ALREADY_DECOUPLED");

        lines.Add(
            "earlyCompletionPoller=1");

        lines.Add(
            "referenceAuthority=V44_55_20_FROZEN_REFERENCE_NCHW");

        lines.Add(
            "mode2Authority=V44_55_20_PRESENTATION_PLUS_FINAL_UNORM8_MODE2");

        lines.Add(
            "largeTailThresholdLsb=4");

        lines.Add(
            "minimumCompletedPairs=" +
            MinimumCompletedPairs);

        lines.Add(
            "minimumAnomalousPairs=" +
            MinimumAnomalousPairs);

        lines.Add("");

        lines.Add(
            "[COUNTS]");

        lines.Add(
            "dependencyRecordCount=" +
            dependencyRecordCount);

        lines.Add(
            "completedResultCount=" +
            _completedResultCount);

        lines.Add(
            "anomalousPairCount=" +
            _anomalousPairCount);

        lines.Add(
            "productionMatchesReferenceCount=" +
            _productionMatchesReferenceCount);

        lines.Add(
            "productionMatchesMode2Count=" +
            _productionMatchesMode2Count);

        lines.Add(
            "ambiguousAnomalousPairCount=" +
            _ambiguousAnomalousPairCount);

        lines.Add(
            "observerFaultCount=" +
            _observerFaultCount);

        lines.Add(
            "dependencyFaultCount=" +
            ReadDependencyFaultCount());

        lines.Add(
            "attachMissCount=" +
            _attachMissCount);

        lines.Add(
            "arrayFreezeMismatchCount=" +
            _arrayFreezeMismatchCount);

        lines.Add(
            "productionInputReadbackFailureCount=" +
            _productionInputReadbackFailureCount);

        lines.Add(
            "productionInputNonFiniteCount=" +
            _productionInputNonFiniteCount);

        lines.Add(
            "laneIdentityMismatchAtRequestCount=" +
            _laneIdentityMismatchAtRequestCount);

        lines.Add(
            "laneChangedBeforeReadbackCompletionCount=" +
            _laneChangedBeforeReadbackCompletionCount);

        lines.Add(
            "laneChangedBeforeReadbackCompletionRole=INFORMATIONAL_AFTER_OBSERVER_GPU_SNAPSHOT");

        lines.Add(
            "snapshotSubmissionIdentityMismatchCount=" +
            _snapshotSubmissionIdentityMismatchCount);

        lines.Add(
            "snapshotDispatchFailureCount=" +
            _snapshotDispatchFailureCount);

        lines.Add(
            "productionOutputReadyObservationFailureCount=" +
            _productionOutputReadyObservationFailureCount);

        lines.Add(
            "productionParityContractMismatchCount=" +
            _productionParityContractMismatchCount);

        lines.Add(
            "duplicatePairRequestCount=" +
            _duplicatePairRequestCount);

        lines.Add(
            "dependencyMissingFrames=" +
            _dependencyMissingFrames);

        lines.Add("");

        AppendAggregate(
            lines,
            _productionVsReference);

        lines.Add("");

        AppendAggregate(
            lines,
            _productionVsMode2);

        lines.Add("");

        AppendAggregate(
            lines,
            _referenceVsMode2);

        lines.Add("");

        lines.Add(
            "[DECISION_RULE]");

        lines.Add(
            "INVALID_OBSERVER if dependency/source/readback/pair-array identity contract fails.");

        lines.Add(
            "INVALID_OBSERVER if REF-vs-mode2 ge4=0 while Production input is ge4>0 from both; such a pair never enters aggregate/classification.");

        lines.Add(
            "INSUFFICIENT_DATA if completedResultCount<60 after a valid dependency run.");

        lines.Add(
            "INSUFFICIENT_ANOMALY_REPRODUCTION if fewer than 3 pairs reproduce REF-vs-mode2 >=4 LSB elements.");

        lines.Add(
            "For each anomalous pair: PROD-vs-REF ge4=0 and PROD-vs-mode2 ge4>0 => PRODUCTION_MATCHES_REFERENCE.");

        lines.Add(
            "For each anomalous pair: PROD-vs-mode2 ge4=0 and PROD-vs-REF ge4>0 => PRODUCTION_MATCHES_MODE2.");

        lines.Add(
            "REFERENCE_TRANSACTION_INVALID_CONFIRMED only if every anomalous pair matches mode2 at the >=4 LSB boundary.");

        lines.Add(
            "REFERENCE_TRANSACTION_VALID_CONFIRMED only if every anomalous pair matches v44.55.20 REF at the >=4 LSB boundary.");

        lines.Add(
            "Otherwise => MIXED_TRANSACTION.");

        lines.Add(
            "No semantic threshold is changed and no Production CPU backend implementation is authorized by this observer alone.");

        File.WriteAllLines(
            textPath,
            lines);

        WriteCsv(
            csvPath);

        Debug.Log(
            "[Kiwi v44.55.22 RefTransaction] COMPLETE" +
            " decision=" +
            decision +
            " pairs=" +
            _completedResultCount +
            " anomalousPairs=" +
            _anomalousPairCount +
            " prodMatchesRef=" +
            _productionMatchesReferenceCount +
            " prodMatchesMode2=" +
            _productionMatchesMode2Count +
            " ambiguous=" +
            _ambiguousAnomalousPairCount +
            " report=" +
            textPath +
            " csv=" +
            csvPath);
    }

    private void WriteCsv(
        string path)
    {
        _results.Sort(
            (a, b) =>
                a.PairIndex.CompareTo(
                    b.PairIndex));

        List<string> lines =
            new List<string>(
                _results.Count + 1);

        lines.Add(
            "pairIndex,sequence,nativeHostTicks,managedHostTicks,laneStartedHostTicks," +
            "dependencyLaneStillMatchedAfterCapture,laneMatchedAtRequest,laneMatchedAtSnapshotSubmission,laneMatchedAtReadbackCompletion," +
            "attachUnityFrame,outputReadyUnityFrame,snapshotSubmissionUnityFrame,requestUnityFrame,readbackCompletionUnityFrame," +
            "laneAgeAtAttachMs,outputReadyWaitMs,snapshotSubmissionAfterOutputReadyMs,laneAgeAtRequestMs,productionInputReadbackMs," +
            PairCsvHeader("prodVsRef") + "," +
            PairCsvHeader("prodVsMode2") + "," +
            PairCsvHeader("refVsMode2") + "," +
            "anomalyClassification");

        for (
            int i = 0;
            i < _results.Count;
            i++)
        {
            PairResult r =
                _results[i];

            lines.Add(
                r.PairIndex + "," +
                r.Sequence + "," +
                r.NativeHostTicks + "," +
                r.ManagedHostTicks + "," +
                r.LaneStartedHostTicks + "," +
                B(r.DependencyLaneStillMatchedAfterCapture) + "," +
                B(r.LaneMatchedAtRequest) + "," +
                B(r.LaneMatchedAtSnapshotSubmission) + "," +
                B(r.LaneMatchedAtReadbackCompletion) + "," +
                r.AttachUnityFrame + "," +
                r.OutputReadyUnityFrame + "," +
                r.SnapshotSubmissionUnityFrame + "," +
                r.RequestUnityFrame + "," +
                r.ReadbackCompletionUnityFrame + "," +
                F(r.LaneAgeAtAttachMs) + "," +
                F(r.OutputReadyWaitMs) + "," +
                F(r.SnapshotSubmissionAfterOutputReadyMs) + "," +
                F(r.LaneAgeAtRequestMs) + "," +
                F(r.ProductionInputReadbackMs) + "," +
                PairCsv(r.ProductionVsReference) + "," +
                PairCsv(r.ProductionVsMode2) + "," +
                PairCsv(r.ReferenceVsMode2) + "," +
                Csv(r.AnomalyClassification));
        }

        File.WriteAllLines(
            path,
            lines);
    }

    private static string PairCsvHeader(
        string prefix)
    {
        return
            prefix + "Count," +
            prefix + "ExactRatio," +
            prefix + "MeanAbsLsb," +
            prefix + "RmseLsb," +
            prefix + "MaxAbsLsb," +
            prefix + "Ge1," +
            prefix + "Ge2," +
            prefix + "Ge4," +
            prefix + "Ge8," +
            prefix + "Ge16," +
            prefix + "Ge32," +
            prefix + "NonFinite";
    }

    private static string PairCsv(
        PairParity value)
    {
        return
            value.Count + "," +
            F(value.ExactRatio) + "," +
            F(value.MeanAbsLsb) + "," +
            F(value.RmseLsb) + "," +
            F(value.MaxAbsLsb) + "," +
            value.Ge1 + "," +
            value.Ge2 + "," +
            value.Ge4 + "," +
            value.Ge8 + "," +
            value.Ge16 + "," +
            value.Ge32 + "," +
            value.NonFiniteCount;
    }

    private static void AppendAggregate(
        List<string> lines,
        AggregateParity value)
    {
        lines.Add(
            "[" +
            value.Name +
            "]");

        lines.Add(
            "pairCount=" +
            value.PairCount);

        lines.Add(
            "count=" +
            value.Count);

        lines.Add(
            "nonFinite=" +
            value.NonFiniteCount);

        lines.Add(
            "exactRatio=" +
            F(
                value.ExactRatio));

        lines.Add(
            "meanAbsLsb=" +
            F(
                value.MeanAbsLsb));

        lines.Add(
            "rmseLsb=" +
            F(
                value.RmseLsb));

        lines.Add(
            "maxAbsLsb=" +
            F(
                value.MaxAbsLsb));

        for (
            int i = 0;
            i < Thresholds.Length;
            i++)
        {
            int threshold =
                Thresholds[i];

            lines.Add(
                "ge" +
                threshold +
                "Count=" +
                value.ThresholdCount(
                    threshold));
        }
    }

    private static PairParity ComputePairParity(
        float[] left,
        float[] right)
    {
        if (
            left == null ||
            right == null ||
            left.Length !=
                InputFloatCount ||
            right.Length !=
                InputFloatCount)
        {
            throw new InvalidOperationException(
                "Tensor shape mismatch.");
        }

        PairParity result =
            default;

        double sumAbs =
            0.0;

        double sumSquare =
            0.0;

        double max =
            0.0;

        long exact =
            0;

        int nonFinite =
            0;

        for (
            int i = 0;
            i < InputFloatCount;
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

            double delta =
                (
                    (double)b -
                    a
                ) *
                255.0;

            double abs =
                Math.Abs(
                    delta);

            sumAbs +=
                abs;

            sumSquare +=
                delta *
                delta;

            max =
                Math.Max(
                    max,
                    abs);

            if (
                BitConverter.SingleToInt32Bits(a) ==
                BitConverter.SingleToInt32Bits(b))
            {
                exact++;
            }

            if (abs >= 1.0) result.Ge1++;
            if (abs >= 2.0) result.Ge2++;
            if (abs >= 4.0) result.Ge4++;
            if (abs >= 8.0) result.Ge8++;
            if (abs >= 16.0) result.Ge16++;
            if (abs >= 32.0) result.Ge32++;
        }

        result.Count =
            InputFloatCount;

        result.ExactCount =
            exact;

        result.NonFiniteCount =
            nonFinite;

        result.MeanAbsLsb =
            sumAbs /
            InputFloatCount;

        result.RmseLsb =
            Math.Sqrt(
                sumSquare /
                InputFloatCount);

        result.MaxAbsLsb =
            max;

        return result;
    }

    private void RegisterFault(
        string reason)
    {
        _observerFaultCount++;

        Debug.LogWarning(
            "[Kiwi v44.55.22 RefTransaction] OBSERVER_FAULT " +
            reason);
    }

    private static FieldInfo RequiredField(
        Type type,
        string name)
    {
        FieldInfo field =
            type.GetField(
                name,
                InstanceFlags);

        if (field == null)
        {
            throw new MissingFieldException(
                type.FullName,
                name);
        }

        return field;
    }

    private static object ReadMemberObject(
        object target,
        string name)
    {
        if (target == null)
        {
            return null;
        }

        FieldInfo field =
            target
                .GetType()
                .GetField(
                    name,
                    InstanceFlags);

        return
            field != null
                ? field.GetValue(
                    target)
                : null;
    }

    private static int ReadMemberInt(
        object target,
        string name)
    {
        object value =
            ReadMemberObject(
                target,
                name);

        return
            value is int number
                ? number
                : 0;
    }

    private static long ReadMemberLong(
        object target,
        string name)
    {
        object value =
            ReadMemberObject(
                target,
                name);

        return
            value is long number
                ? number
                : 0L;
    }

    private static ulong ReadMemberULong(
        object target,
        string name)
    {
        object value =
            ReadMemberObject(
                target,
                name);

        return
            value is ulong number
                ? number
                : 0UL;
    }

    private static bool ReadMemberBool(
        object target,
        string name)
    {
        object value =
            ReadMemberObject(
                target,
                name);

        return
            value is bool flag &&
            flag;
    }

    private static double ReadMemberDouble(
        object target,
        string name)
    {
        object value =
            ReadMemberObject(
                target,
                name);

        return
            value is double number
                ? number
                : 0.0;
    }

    private static int ReadFieldInt(
        object target,
        FieldInfo field)
    {
        if (
            target == null ||
            field == null)
        {
            return 0;
        }

        object value =
            field.GetValue(
                target);

        return
            value is int number
                ? number
                : 0;
    }

    private static bool ReadFieldBool(
        object target,
        FieldInfo field)
    {
        if (
            target == null ||
            field == null)
        {
            return false;
        }

        object value =
            field.GetValue(
                target);

        return
            value is bool flag &&
            flag;
    }

    private static bool ReadLaneBool(
        object lane,
        FieldInfo field)
    {
        if (
            lane == null ||
            field == null)
        {
            return false;
        }

        object value =
            field.GetValue(
                lane);

        return
            value is bool flag &&
            flag;
    }

    private static long ReadLaneLong(
        object lane,
        FieldInfo field)
    {
        if (
            lane == null ||
            field == null)
        {
            return 0L;
        }

        object value =
            field.GetValue(
                lane);

        return
            value is long ticks
                ? ticks
                : 0L;
    }

    private static string GetReportDirectory()
    {
        string directory =
            Path.Combine(
                Application.persistentDataPath,
                "KiwiFrameBottleneck");

        Directory.CreateDirectory(
            directory);

        return directory;
    }

    private static double HostTicksToMilliseconds(
        long ticks)
    {
        if (ticks <= 0L)
        {
            return 0.0;
        }

        return
            ticks *
            1000.0 /
            Stopwatch.Frequency;
    }

    private static bool IsFinite(
        float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
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
            value ??
            string.Empty;

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

    private static bool ReadBoolEnvironment(
        string name,
        bool fallback)
    {
        string value =
            Environment.GetEnvironmentVariable(
                name);

        if (
            string.IsNullOrWhiteSpace(
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

    private void OnDisable()
    {
        foreach (
            KeyValuePair<int, CaptureState> item
            in _captures)
        {
            ReleaseSnapshotBuffer(
                item.Value);
        }

        if (
            _reportWritten ||
            !_bound)
        {
            return;
        }

        try
        {
            WriteReport(
                "STOPPED_BEFORE_DEPENDENCY_COMPLETE");
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[Kiwi v44.55.22 RefTransaction] STOP_REPORT_FAIL " +
                exception.GetType().Name +
                " " +
                exception.Message);
        }
    }

    private void OnApplicationQuit()
    {
        if (
            !_reportWritten &&
            _bound)
        {
            WriteReport(
                "QUIT_BEFORE_DEPENDENCY_COMPLETE");
        }
    }
}

/// <summary>
/// Observer-only early-frame companion for v44.55.22.
///
/// Production consumes completed pendingOutput tensors during its normal Update.
/// This component runs earlier than ordinary scripts so it can observe the
/// already-requested Production output readback completion before the lane is
/// released/reused. It performs no GPU readback, no wait and no Production write.
/// </summary>
[DefaultExecutionOrder(-33000)]
internal sealed class KiwiReferencePixelTransactionIntegrityEarlyProbeV44_55_22
    : MonoBehaviour
{
    internal KiwiReferencePixelTransactionIntegrityV44_55_22 Owner;

    private void Update()
    {
        Owner?.PollProductionOutputReadyBeforeProductionUpdate();
    }
}

