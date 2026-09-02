using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

/// <summary>
/// KiwiAvatarSystem v44.55.20
/// Common-Tensor Backend + Spatial Outlier Stage Isolation Gate.
///
/// Observer-only correctness gate:
///   T_REF = matched Production crop Texture -> private freeze ->
///           TextureConverter NCHW/TopLeft -> async FLOAT32 freeze
///   T_A = CURRENT_FLOAT exact native snapshot FLOAT32
///   T_B = PRESENTATION_PLUS_FINAL_UNORM8 mode2 exact native snapshot FLOAT32
///
/// Each frozen tensor is uploaded without further sampling to one GPUCompute
/// worker and one CPU worker. The first ten measured T_REF samples additionally
/// compare direct TextureConverter->GPU inference with readback->GPU reupload.
///
/// All three paths use the same native sequence mapping, raw native hostTicks,
/// matched Production lane crop identity, exact pendingCropMatrix and the same
/// packed model. Production DecodeReadableOutput is invoked read-only through
/// reflection. The validator never republishes or writes tracking, ROI,
/// FaceTexture, Root/Head, Production worker, camera or authority state.
///
/// The independent reference intentionally adds observer-owned GPU work and
/// asynchronous readback. This run is correctness-only and is never performance
/// authority.
///
/// Hard semantic gate stops at stateless canonical tracking semantics. Stateful
/// neutral calibration, provider offset, dropout continuity, prediction, temporal
/// presentation and final Root/Head transforms are deliberately excluded.
/// </summary>
internal sealed class KiwiCommonTensorBackendStageIsolationV44_55_20
    : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_55_20_COMMON_TENSOR_BACKEND_SPATIAL_STAGE_ISOLATION";

    private const string EnableVariable =
        "KIWI_V44_55_20_COMMON_TENSOR_AUDIT";

    private const string DurationVariable =
        "KIWI_V44_55_20_COMMON_TENSOR_SECONDS";

    private const string SampleHzVariable =
        "KIWI_V44_55_20_COMMON_TENSOR_HZ";

    private const string StableSecondsVariable =
        "KIWI_V44_55_20_STABLE_SECONDS";

    private const int InputSize = 192;
    private const int PlaneLength = InputSize * InputSize;
    private const int InputFloatCount = PlaneLength * 3;
    private const int CompatibleLandmarkCount = 478;

    private const float DefaultDurationSeconds = 120f;
    private const float DefaultSampleHz = 1.0f;
    private const float DefaultStableSeconds = 8f;
    private const int WarmupPairCount = 2;
    private const int TransitValidationSamples = 10;

    // Inherited unchanged from the pre-registered v44.55.16 gate. Do not tune after seeing data.
    private const int MinimumCompletedPairs = 60;
    private const int MinimumCanonicalComparablePairs = 40;

    private const double PresenceP95Gate = 0.01;
    private const double PresenceMaxGate = 0.03;
    private const double CanonicalPointP95PxGate = 1.0;
    private const double CanonicalPointMaxPxGate = 2.0;
    private const double RotationP95DegreesGate = 0.10;
    private const double RotationMaxDegreesGate = 0.25;
    private const double ScaleRelativeP95Gate = 0.005;
    private const double ScaleRelativeMaxGate = 0.010;
    private const double GeometryQualityP95Gate = 0.010;
    private const double GeometryQualityMaxGate = 0.025;
    private const double ExpressionP95Gate = 0.020;
    private const double ExpressionMaxGate = 0.050;

    private const double SnapshotMatchTimeoutSeconds = 0.250;

    // Categorical semantic contradictions are zero-tolerance hard failures.
    // These bits are diagnostic and do not alter Production behavior.
    private const int HardDecodeStatusMismatch = 1;
    private const int HardAcceptanceMismatch = 2;
    private const int HardCanonicalValidityMismatch = 4;
    private const int HardNonFiniteSemantic = 8;

    private static readonly int XformId =
        Shader.PropertyToID(
            "_Xform");

    private static bool _installed;

    private MonoBehaviour _runner;
    private object _tracker;
    private Type _trackerType;
    private Array _lanes;

    private FieldInfo _runnerTrackerField;
    private FieldInfo _runnerLastObservedFreshSequenceField;
    private FieldInfo _runnerLatestSentisSourceHostTicksField;

    private FieldInfo _trackerLanesField;
    private FieldInfo _trackerSourceWidthField;
    private FieldInfo _trackerSourceHeightField;
    private FieldInfo _laneCropTextureField;
    private FieldInfo _laneCropMaterialField;
    private FieldInfo _laneReadbackPendingField;
    private FieldInfo _lanePendingSourceHostTicksField;
    private FieldInfo _lanePendingStartedHostTicksField;
    private FieldInfo _lanePendingCropMatrixField;
    private FieldInfo _lanePendingMinimumPresenceField;

    private MethodInfo _decodeReadableOutputMethod;
    private MethodInfo _extractGeometryExpressionDataMethod;

    private readonly float[] _samplingMatrix = new float[16];
    private readonly float[] _referenceNchw = new float[InputFloatCount];
    private readonly float[] _leftNchw = new float[InputFloatCount];
    private readonly float[] _rightNchw = new float[InputFloatCount];

    private readonly Vector3[] _referenceDecoded =
        new Vector3[CompatibleLandmarkCount];

    private readonly Vector3[] _leftDecoded =
        new Vector3[CompatibleLandmarkCount];

    private readonly Vector3[] _rightDecoded =
        new Vector3[CompatibleLandmarkCount];

    private readonly Vector3[] _referenceFrozenGpuDecoded =
        new Vector3[CompatibleLandmarkCount];

    private readonly Vector3[] _referenceCpuDecoded =
        new Vector3[CompatibleLandmarkCount];

    private readonly Vector3[] _aGpuDecoded =
        new Vector3[CompatibleLandmarkCount];

    private readonly Vector3[] _bGpuDecoded =
        new Vector3[CompatibleLandmarkCount];

    private Worker _referenceWorker;
    private Worker _leftWorker;
    private Worker _rightWorker;
    private Worker _referenceFrozenGpuWorker;
    private Worker _referenceCpuWorker;
    private Worker _aGpuWorker;
    private Worker _bGpuWorker;
    private Tensor<float> _referenceInput;
    private Tensor<float> _leftInput;
    private Tensor<float> _rightInput;
    private Tensor<float> _referenceFrozenGpuInput;
    private Tensor<float> _referenceCpuInput;
    private Tensor<float> _aGpuInput;
    private Tensor<float> _bGpuInput;
    private RenderTexture _referenceSnapshotTexture;
    private TextureTransform _referenceTextureTransform;
    private CommandBuffer _referenceCommandBuffer;
    private bool _workersReady;

    private bool _reflectionReady;
    private bool _measuring;
    private bool _pairPending;
    private bool _reportWritten;
    private bool _gateAnnounced;

    private int _warmupPairsRemaining = WarmupPairCount;

    private float _durationSeconds;
    private float _sampleHz;
    private float _stableSeconds;

    private double _stableSince = -1.0;
    private double _measurementStart;
    private double _nextPairAt;
    private double _nextDiscoveryAt;
    private double _nextSnapshotArmAt;
    private double _nextGateDiagnosticAt;

    private bool _snapshotRequestActive;
    private double _snapshotArmRealtime;
    private ulong _snapshotSequence;
    private long _snapshotNativeHostTicks;
    private long _snapshotManagedHostTicks;

    private long _lastCapturedProductionStartedTicks;

    private int _pairToken;
    private bool _pendingIsWarmup;
    private PairRecord _pendingRecord;
    private Matrix4x4 _pendingCropMatrix;
    private float _pendingMinimumPresence;
    private int _pendingSourceWidth;
    private int _pendingSourceHeight;
    private long _referenceScheduleDoneTicks;
    private long _leftScheduleDoneTicks;
    private long _rightScheduleDoneTicks;
    private int _scheduleFrame;
    private bool _referenceOutputDone;
    private bool _referenceInputDone;
    private bool _leftDone;
    private bool _rightDone;
    private bool _commonScheduled;
    private bool _transitRequired;
    private bool _referenceFrozenGpuDone;
    private bool _referenceCpuDone;
    private bool _aGpuDone;
    private bool _bGpuDone;
    private SideResult _referenceResult;
    private SideResult _leftResult;
    private SideResult _rightResult;
    private SideResult _referenceFrozenGpuResult;
    private SideResult _referenceCpuResult;
    private SideResult _aGpuResult;
    private SideResult _bGpuResult;
    private int _transitValidationCount;
    private int _referenceTensorNonFiniteCount;

    private int _snapshotArmCount;
    private int _snapshotReadyCount;
    private int _snapshotMatchTimeoutCount;
    private int _snapshotArmFailureCount;
    private int _snapshotSequenceSkippedCount;
    private int _snapshotManagedTimestampMapFailureCount;
    private int _snapshotPresentedButNotScheduledCount;

    private int _pairAttemptCount;
    private int _pairCompletedCount;
    private int _pairErrorCount;
    private int _observerFaultCount;
    private int _sourceIdentityMismatchCount;
    private int _decodeStatusMismatchCount;
    private int _acceptanceMismatchCount;
    private int _canonicalValidityMismatchCount;
    private int _nonFiniteSemanticPairCount;
    private int _canonicalComparablePairCount;
    private int _decodeReflectionFailureCount;
    private int _expressionReflectionFailureCount;
    private int _laneChangedAfterCaptureCount;
    private int _hardInvariantViolationPairCount;
    private int _presenceThresholdCrossingPairCount;

    private int _referenceFreezeFailureCount;
    private int _referenceInputReadbackFailureCount;

    private readonly ComparisonMetrics _referenceVsCurrent =
        new ComparisonMetrics("REFERENCE_VS_CURRENT_FLOAT");

    private readonly ComparisonMetrics _referenceVsMode2 =
        new ComparisonMetrics("REFERENCE_VS_MODE2");

    private readonly ComparisonMetrics _transitDirectVsFrozen =
        new ComparisonMetrics("REF_DIRECT_GPU_VS_REF_FROZEN_GPU");

    private readonly ComparisonMetrics _backendReference =
        new ComparisonMetrics("BACKEND_ONLY_REF_GPU_VS_REF_CPU");

    private readonly ComparisonMetrics _backendA =
        new ComparisonMetrics("BACKEND_ONLY_A_GPU_VS_A_CPU");

    private readonly ComparisonMetrics _backendB =
        new ComparisonMetrics("BACKEND_ONLY_B_GPU_VS_B_CPU");

    private readonly ComparisonMetrics _preprocessCpuA =
        new ComparisonMetrics("PREPROCESSING_CPU_REF_VS_A");

    private readonly ComparisonMetrics _preprocessCpuB =
        new ComparisonMetrics("PREPROCESSING_CPU_REF_VS_B");

    private readonly InputParityAccumulator _referenceVsCurrentInput =
        new InputParityAccumulator("REFERENCE_VS_CURRENT_FLOAT_INPUT");

    private readonly InputParityAccumulator _referenceVsMode2Input =
        new InputParityAccumulator("REFERENCE_VS_MODE2_INPUT");

    private readonly InputParityAccumulator _currentVsMode2Input =
        new InputParityAccumulator("CURRENT_FLOAT_VS_MODE2_INPUT");

    private readonly SpatialOutlierAccumulator _referenceVsASpatial =
        new SpatialOutlierAccumulator("REFERENCE_VS_CURRENT_FLOAT");

    private readonly SpatialOutlierAccumulator _referenceVsBSpatial =
        new SpatialOutlierAccumulator("REFERENCE_VS_MODE2");

    private readonly List<SpatialOutlierRecord> _spatialTopRecords =
        new List<SpatialOutlierRecord>(8192);

    private readonly List<string> _hardInvariantDetails =
        new List<string>(32);

    private readonly List<double> _presenceDiff =
        new List<double>(128);

    private readonly List<double> _canonicalPointDiffPx =
        new List<double>(1024);

    private readonly List<double> _rotationDiffDegrees =
        new List<double>(128);

    private readonly List<double> _scaleRelativeDiff =
        new List<double>(512);

    private readonly List<double> _geometryQualityDiff =
        new List<double>(128);

    private readonly List<double> _expressionDiff =
        new List<double>(1536);

    // Diagnostic only. Never a standalone pass/fail gate.
    private readonly List<double> _landmark2dDiffPx =
        new List<double>(65536);

    private readonly List<double> _landmarkZAbsDiff =
        new List<double>(65536);

    private readonly List<double> _leftNativeCropCpuMs =
        new List<double>(128);

    private readonly List<double> _rightNativeCropCpuMs =
        new List<double>(128);

    private readonly List<double> _nativePairWallMs =
        new List<double>(128);

    private readonly List<double> _leftCpuServiceMs =
        new List<double>(128);

    private readonly List<double> _rightCpuServiceMs =
        new List<double>(128);

    private readonly List<int> _pairCompletionFrames =
        new List<int>(128);

    private readonly List<PairRecord> _records =
        new List<PairRecord>(128);

    private sealed class PairRecord
    {
        internal int Index;
        internal ulong Sequence;
        internal long NativeHostTicks;
        internal long ManagedHostTicks;
        internal long LaneStartedHostTicks;
        internal int SourceWidth;
        internal int SourceHeight;
        internal float MinimumPresence;
        internal bool LaneStillMatchedAfterCapture;
        internal int LeftDecodeStatus;
        internal int RightDecodeStatus;
        internal string LeftDecodeStatusName;
        internal string RightDecodeStatusName;
        internal float LeftRawPresence;
        internal float RightRawPresence;
        internal float LeftPresence;
        internal float RightPresence;
        internal double LeftPresenceMarginToThreshold;
        internal double RightPresenceMarginToThreshold;
        internal double MinimumAbsPresenceMargin;
        internal bool PresenceThresholdCrossed;
        internal int HardInvariantMask;
        internal bool HardInvariantViolation;
        internal bool LeftAccepted;
        internal bool RightAccepted;
        internal bool LeftCanonicalValid;
        internal bool RightCanonicalValid;
        internal bool CanonicalComparable;
        internal double PresenceAbsDiff;
        internal double CanonicalPointMaxPx;
        internal double RotationDiffDegrees;
        internal double ScaleRelativeMax;
        internal double GeometryQualityAbsDiff;
        internal double ExpressionMaxAbsDiff;
        internal double Landmark2dP95Px;
        internal double Landmark2dMaxPx;
        internal double LandmarkZP95Abs;
        internal double LandmarkZMaxAbs;
        internal double LeftNativeCpuMs;
        internal double RightNativeCpuMs;
        internal double NativePairWallMs;
        internal int CompletionFrames;
        internal int ReferenceDecodeStatus;
        internal string ReferenceDecodeStatusName;
        internal float ReferenceRawPresence;
        internal float ReferencePresence;
        internal bool ReferenceAccepted;
        internal bool ReferenceCanonicalValid;
        internal ComparisonRecord ReferenceVsCurrent;
        internal ComparisonRecord ReferenceVsMode2;
        internal double ReferenceVsCurrentInputMeanAbsLsb;
        internal double ReferenceVsMode2InputMeanAbsLsb;
        internal double CurrentVsMode2InputMeanAbsLsb;
        internal ComparisonRecord TransitDirectVsFrozen;
        internal ComparisonRecord BackendReference;
        internal ComparisonRecord BackendA;
        internal ComparisonRecord BackendB;
        internal ComparisonRecord PreprocessGpuA;
        internal ComparisonRecord PreprocessGpuB;
        internal ComparisonRecord PreprocessCpuA;
        internal ComparisonRecord PreprocessCpuB;
        internal bool TransitRequired;
    }

    private struct ComparisonRecord
    {
        internal bool Comparable;
        internal int HardMask;
        internal double PresenceAbsDiff;
        internal double CanonicalPointMaxPx;
        internal double RotationDiffDegrees;
        internal double ScaleRelativeMax;
        internal double GeometryQualityAbsDiff;
        internal double ExpressionMaxAbsDiff;
        internal double Landmark2dP95Px;
        internal double Landmark2dMaxPx;
        internal double LandmarkZP95Abs;
        internal double LandmarkZMaxAbs;
    }

    private sealed class ComparisonMetrics
    {
        internal readonly string Name;
        internal readonly List<double> Presence = new List<double>(128);
        internal readonly List<double> CanonicalPoints = new List<double>(1024);
        internal readonly List<double> Rotation = new List<double>(128);
        internal readonly List<double> Scale = new List<double>(512);
        internal readonly List<double> GeometryQuality = new List<double>(128);
        internal readonly List<double> Expression = new List<double>(1536);
        internal readonly List<double> Landmark2d = new List<double>(65536);
        internal readonly List<double> LandmarkZ = new List<double>(65536);
        internal readonly List<string> HardDetails = new List<string>(32);
        internal int DecodeStatusMismatchCount;
        internal int AcceptanceMismatchCount;
        internal int CanonicalValidityMismatchCount;
        internal int NonFiniteCount;
        internal int HardInvariantViolationCount;
        internal int CanonicalComparableCount;

        internal ComparisonMetrics(string name)
        {
            Name = name;
        }
    }

    private sealed class InputParityAccumulator
    {
        private const int HistogramBins = 4097;
        private const double HistogramBinsPerLsb = 16.0;

        internal readonly string Name;
        private readonly long[] _histogram = new long[HistogramBins];
        private readonly double[] _signedChannelSum = new double[3];
        private long _count;
        private long _exactCount;
        private double _sumAbsLsb;
        private double _sumSquareLsb;
        private double _maxAbsLsb;
        private int _pairCount;
        private double _lastPairMeanAbsLsb;

        internal InputParityAccumulator(string name)
        {
            Name = name;
        }

        internal long Count => _count;
        internal int PairCount => _pairCount;
        internal double MeanAbsLsb => _count > 0 ? _sumAbsLsb / _count : 0.0;
        internal double RmseLsb => _count > 0 ? Math.Sqrt(_sumSquareLsb / _count) : 0.0;
        internal double MaxAbsLsb => _maxAbsLsb;
        internal double ExactMatchRatio => _count > 0 ? _exactCount / (double)_count : 0.0;
        internal double LastPairMeanAbsLsb => _lastPairMeanAbsLsb;

        internal double SignedChannelBiasLsb(int channel)
        {
            long channelCount = _count / 3;
            return channelCount > 0 ? _signedChannelSum[channel] / channelCount : 0.0;
        }

        internal void Accumulate(float[] reference, float[] candidate)
        {
            if (
                reference == null ||
                candidate == null ||
                reference.Length != InputFloatCount ||
                candidate.Length != InputFloatCount)
            {
                throw new InvalidOperationException("Input parity shape mismatch.");
            }

            double pairSum = 0.0;

            for (int i = 0; i < InputFloatCount; i++)
            {
                double signedLsb = ((double)candidate[i] - reference[i]) * 255.0;
                double absLsb = Math.Abs(signedLsb);
                pairSum += absLsb;
                _sumAbsLsb += absLsb;
                _sumSquareLsb += signedLsb * signedLsb;
                _maxAbsLsb = Math.Max(_maxAbsLsb, absLsb);
                _signedChannelSum[i / PlaneLength] += signedLsb;
                _count++;

                if (BitConverter.SingleToInt32Bits(reference[i]) ==
                    BitConverter.SingleToInt32Bits(candidate[i]))
                {
                    _exactCount++;
                }

                int bin = Mathf.Clamp(
                    (int)Math.Ceiling(absLsb * HistogramBinsPerLsb),
                    0,
                    HistogramBins - 1);
                _histogram[bin]++;
            }

            _lastPairMeanAbsLsb = pairSum / InputFloatCount;
            _pairCount++;
        }

        internal double Percentile(double percentile)
        {
            if (_count <= 0)
            {
                return 0.0;
            }

            long target = (long)Math.Ceiling(Mathf.Clamp01((float)percentile) * _count);
            long cumulative = 0;

            for (int i = 0; i < _histogram.Length; i++)
            {
                cumulative += _histogram[i];
                if (cumulative >= target)
                {
                    return i / HistogramBinsPerLsb;
                }
            }

            return _maxAbsLsb;
        }
    }

    private enum CommonLane
    {
        ReferenceFrozenGpu,
        ReferenceCpu,
        AGpu,
        ACpu,
        BGpu,
        BCpu
    }

    private struct SpatialOutlierRecord
    {
        internal string Comparison;
        internal int PairIndex;
        internal ulong Sequence;
        internal int Channel;
        internal int Y;
        internal int X;
        internal int EdgeDistance;
        internal float ReferenceValue;
        internal float CandidateValue;
        internal double SignedDeltaLsb;
        internal double AbsDeltaLsb;
    }

    private sealed class SpatialOutlierAccumulator
    {
        internal static readonly double[] Thresholds =
        {
            0.0,
            0.5,
            1.0,
            2.0,
            4.0,
            8.0,
            16.0,
            32.0
        };

        internal readonly string Name;
        internal readonly long[] ThresholdCounts =
            new long[Thresholds.Length];
        internal readonly long[,] ChannelCounts =
            new long[Thresholds.Length, 3];
        internal readonly long[,] XCounts =
            new long[Thresholds.Length, InputSize];
        internal readonly long[,] YCounts =
            new long[Thresholds.Length, InputSize];
        // Columns are cumulative <=1, <=2, <=4, <=8 and interior >8.
        internal readonly long[,] EdgeCounts =
            new long[Thresholds.Length, 5];
        internal readonly double[] HeatSumAbsLsb =
            new double[PlaneLength];
        internal readonly double[] HeatMaxAbsLsb =
            new double[PlaneLength];
        internal readonly long[] HeatElementCount =
            new long[PlaneLength];
        internal readonly long[,] HeatThresholdCounts =
            new long[Thresholds.Length, PlaneLength];

        internal int PairCount;
        internal long ElementCount;
        internal int NonFiniteCount;

        internal SpatialOutlierAccumulator(string name)
        {
            Name = name;
        }

        internal void Accumulate(
            int pairIndex,
            ulong sequence,
            float[] reference,
            float[] candidate,
            List<SpatialOutlierRecord> destination)
        {
            if (
                reference == null ||
                candidate == null ||
                reference.Length != InputFloatCount ||
                candidate.Length != InputFloatCount)
            {
                throw new InvalidOperationException(
                    "Spatial outlier tensor shape mismatch.");
            }

            SpatialOutlierRecord[] top =
                new SpatialOutlierRecord[32];
            int topCount = 0;

            for (int i = 0; i < InputFloatCount; i++)
            {
                float referenceValue = reference[i];
                float candidateValue = candidate[i];

                if (
                    !IsFinite(referenceValue) ||
                    !IsFinite(candidateValue))
                {
                    NonFiniteCount++;
                    continue;
                }

                int channel = i / PlaneLength;
                int pixel = i - channel * PlaneLength;
                int y = pixel / InputSize;
                int x = pixel - y * InputSize;
                int edgeDistance =
                    Math.Min(
                        Math.Min(x, InputSize - 1 - x),
                        Math.Min(y, InputSize - 1 - y));
                double signedDeltaLsb =
                    ((double)candidateValue - referenceValue) * 255.0;
                double absDeltaLsb = Math.Abs(signedDeltaLsb);

                ElementCount++;
                HeatElementCount[pixel]++;
                HeatSumAbsLsb[pixel] += absDeltaLsb;
                HeatMaxAbsLsb[pixel] =
                    Math.Max(HeatMaxAbsLsb[pixel], absDeltaLsb);

                for (int thresholdIndex = 0;
                     thresholdIndex < Thresholds.Length;
                     thresholdIndex++)
                {
                    bool passes =
                        thresholdIndex == 0
                            ? absDeltaLsb > 0.0
                            : absDeltaLsb >= Thresholds[thresholdIndex];

                    if (!passes)
                    {
                        continue;
                    }

                    ThresholdCounts[thresholdIndex]++;
                    ChannelCounts[thresholdIndex, channel]++;
                    XCounts[thresholdIndex, x]++;
                    YCounts[thresholdIndex, y]++;
                    HeatThresholdCounts[thresholdIndex, pixel]++;

                    if (edgeDistance <= 1) EdgeCounts[thresholdIndex, 0]++;
                    if (edgeDistance <= 2) EdgeCounts[thresholdIndex, 1]++;
                    if (edgeDistance <= 4) EdgeCounts[thresholdIndex, 2]++;
                    if (edgeDistance <= 8) EdgeCounts[thresholdIndex, 3]++;
                    if (edgeDistance > 8) EdgeCounts[thresholdIndex, 4]++;
                }

                SpatialOutlierRecord item =
                    new SpatialOutlierRecord
                    {
                        Comparison = Name,
                        PairIndex = pairIndex,
                        Sequence = sequence,
                        Channel = channel,
                        Y = y,
                        X = x,
                        EdgeDistance = edgeDistance,
                        ReferenceValue = referenceValue,
                        CandidateValue = candidateValue,
                        SignedDeltaLsb = signedDeltaLsb,
                        AbsDeltaLsb = absDeltaLsb
                    };

                int insertAt = topCount;
                if (topCount < top.Length)
                {
                    topCount++;
                }
                else if (absDeltaLsb <= top[top.Length - 1].AbsDeltaLsb)
                {
                    continue;
                }
                else
                {
                    insertAt = top.Length - 1;
                }

                while (
                    insertAt > 0 &&
                    absDeltaLsb > top[insertAt - 1].AbsDeltaLsb)
                {
                    if (insertAt < top.Length)
                    {
                        top[insertAt] = top[insertAt - 1];
                    }
                    insertAt--;
                }

                top[insertAt] = item;
            }

            for (int i = 0; i < topCount; i++)
            {
                destination.Add(top[i]);
            }

            PairCount++;
        }

        internal double EdgeWithin8Fraction(int thresholdIndex)
        {
            long total = ThresholdCounts[thresholdIndex];
            return total > 0
                ? EdgeCounts[thresholdIndex, 3] / (double)total
                : 0.0;
        }
    }

    private struct SideResult
    {
        internal bool DecodeInvocationSucceeded;
        internal int DecodeStatus;
        internal string DecodeStatusName;
        internal float RawPresence;
        internal float Presence;
        internal Quaternion Rotation;
        internal bool Accepted;
        internal bool CanonicalValid;
        internal CanonicalSnapshot Canonical;
    }

    private struct CanonicalSnapshot
    {
        internal bool IsValid;
        internal Vector2 FaceCenter;
        internal Vector2 RightEyeCenter;
        internal Vector2 LeftEyeCenter;
        internal Vector2 EyeCenter;
        internal Vector2 Chin;
        internal Vector2 Nose;
        internal Vector2 CheekCenter;
        internal Vector2 Forehead;
        internal float EyeSpan2D;
        internal float EyeSpan3D;
        internal float FaceWidth2D;
        internal float FaceHeight2D;
        internal float GeometryQuality;
        internal Quaternion Rotation;
        internal FaceExpressionData Expression;
    }

    private struct Stats
    {
        internal int Count;
        internal double Mean;
        internal double P50;
        internal double P95;
        internal double Max;
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
                "[Kiwi] v44.55.20 Exact-HostTicks Canonical Semantic Gate");

        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.DontSave;

        go.AddComponent<
            KiwiCommonTensorBackendStageIsolationV44_55_20>();
    }

    private void Awake()
    {
        _durationSeconds =
            Mathf.Clamp(
                ReadFloatEnvironment(
                    DurationVariable,
                    DefaultDurationSeconds),
                60f,
                300f);

        _sampleHz =
            Mathf.Clamp(
                ReadFloatEnvironment(
                    SampleHzVariable,
                    DefaultSampleHz),
                0.5f,
                2.0f);

        _stableSeconds =
            Mathf.Clamp(
                ReadFloatEnvironment(
                    StableSecondsVariable,
                    DefaultStableSeconds),
                2f,
                30f);

        WriteArmedProof();

        Debug.Log(
            "[Kiwi v44.55.20 CommonTensor] WAIT_GATE " +
            "contract=" + Contract +
            " observerOnly=1" +
            " productionWrites=0" +
            " productionBackendChange=0" +
            " nativeDllChange=0" +
            " trackingMathChange=0" +
            " observerGpuReadback=1" +
            " performanceAuthority=0" +
            " reference=INDEPENDENT_TEXTURECONVERTER_SHADOW_GPU" +
            " left=CPU_CURRENT_FLOAT" +
            " right=CPU_PRESENTATION_PLUS_FINAL_UNORM8_MODE2" +
            " decode=PRODUCTION_PRIVATE_METHOD_READ_ONLY" +
            " canonical=STATELESS_SENTIS_GEOMETRY" +
            " finalRootHeadHardGate=0" +
            " decisionPrecedence=INVALID_OBSERVER>HARD_FAIL>INSUFFICIENT_DATA>AGGREGATE_GATE" +
            " sampleHz=" +
            _sampleHz.ToString(
                "F2",
                CultureInfo.InvariantCulture) +
            " durationSeconds=" +
            _durationSeconds.ToString(
                "F1",
                CultureInfo.InvariantCulture));
    }

    private void Update()
    {
        if (_reportWritten)
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        DiscoverRuntimeObjects(now);

        if (!_reflectionReady)
        {
            return;
        }

        if (!_measuring)
        {
            UpdateGate(now);
            return;
        }

        if (
            now - _measurementStart >=
            _durationSeconds)
        {
            if (!_pairPending)
            {
                WriteReport("COMPLETE");
            }
        }
    }

    private void LateUpdate()
    {
        if (
            _reportWritten ||
            !_reflectionReady ||
            _pairPending)
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        if (!_measuring)
        {
            if (_warmupPairsRemaining > 0)
            {
                TryCaptureExactPair(
                    isWarmup: true);
            }

            return;
        }

        if (now < _nextPairAt)
        {
            return;
        }

        if (TryCaptureExactPair(
                isWarmup: false))
        {
            _nextPairAt =
                now +
                1.0 /
                _sampleHz;
        }
    }

    private void UpdateGate(
        double now)
    {
        bool reflectionValid =
            RuntimeReflectionStillValid();

        bool trackerRegion =
            TrackerHasRegion();

        bool nativeRunning =
            KiwiNativeCameraInterop.IsRunning;

        bool systemMemoryCapture =
            KiwiNativeCameraInterop.SystemMemoryCaptureEnabled;

        bool pathB =
            KiwiNativeCameraInterop.CaptureTransportId == 1;

        bool ready =
            reflectionValid &&
            trackerRegion &&
            nativeRunning &&
            systemMemoryCapture &&
            pathB;

        if (!ready)
        {
            _stableSince = -1.0;
            _gateAnnounced = false;

            if (now >= _nextGateDiagnosticAt)
            {
                _nextGateDiagnosticAt =
                    now + 5.0;

                Debug.Log(
                    "[Kiwi v44.55.20 CommonTensor] GATE_WAIT " +
                    "reflectionValid=" +
                    (reflectionValid ? "1" : "0") +
                    " trackerRegion=" +
                    (trackerRegion ? "1" : "0") +
                    " nativeRunning=" +
                    (nativeRunning ? "1" : "0") +
                    " systemMemoryCapture=" +
                    (systemMemoryCapture ? "1" : "0") +
                    " pathB=" +
                    (pathB ? "1" : "0"));
            }

            return;
        }

        if (_stableSince < 0.0)
        {
            _stableSince = now;

            if (!_gateAnnounced)
            {
                _gateAnnounced = true;

                Debug.Log(
                    "[Kiwi v44.55.20 CommonTensor] GATE_MATCH " +
                    "nativePathB=1" +
                    " reflectionReadOnly=1" +
                    " waitingStableSeconds=" +
                    _stableSeconds.ToString(
                        "F1",
                        CultureInfo.InvariantCulture));
            }

            return;
        }

        if (
            now - _stableSince <
            _stableSeconds)
        {
            return;
        }

        try
        {
            InstallShadowWorkers();
        }
        catch (Exception exception)
        {
            _observerFaultCount++;

            Debug.LogError(
                "[Kiwi v44.55.20 CommonTensor] WORKER_INIT_FAIL " +
                exception.GetType().Name +
                " " +
                exception.Message);

            WriteReport("INIT_FAIL");
            return;
        }

        _stableSince =
            double.PositiveInfinity;

        Debug.Log(
            "[Kiwi v44.55.20 CommonTensor] READY_FOR_WARMUP " +
            "observerGpuReadback=1" +
            " exactPendingCropMatrix=1" +
            " blockingGpuWait=0");
    }

    private void InstallShadowWorkers()
    {
        if (_workersReady)
        {
            return;
        }

        ModelAsset asset =
            Resources.Load<ModelAsset>(
                "KiwiFaceLandmarkInference");

        if (asset == null)
        {
            throw new InvalidOperationException(
                "Resources/KiwiFaceLandmarkInference ModelAsset not found.");
        }

        _referenceWorker = CreateShadowWorker(asset, BackendType.GPUCompute);
        _referenceFrozenGpuWorker = CreateShadowWorker(asset, BackendType.GPUCompute);
        _referenceCpuWorker = CreateShadowWorker(asset, BackendType.CPU);
        _aGpuWorker = CreateShadowWorker(asset, BackendType.GPUCompute);
        _leftWorker = CreateShadowWorker(asset, BackendType.CPU);
        _bGpuWorker = CreateShadowWorker(asset, BackendType.GPUCompute);
        _rightWorker = CreateShadowWorker(asset, BackendType.CPU);

        _referenceInput = CreateInputTensor(pinGpu: true);
        _referenceFrozenGpuInput = CreateInputTensor(pinGpu: true);
        _referenceCpuInput = CreateInputTensor(pinGpu: false);
        _aGpuInput = CreateInputTensor(pinGpu: true);
        _leftInput = CreateInputTensor(pinGpu: false);
        _bGpuInput = CreateInputTensor(pinGpu: true);
        _rightInput = CreateInputTensor(pinGpu: false);

        _referenceSnapshotTexture =
            new RenderTexture(
                InputSize,
                InputSize,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                name = "Kiwi v44.55.20 Frozen Production Crop",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.DontSave
            };

        _referenceSnapshotTexture.Create();

        _referenceTextureTransform =
            new TextureTransform()
                .SetTensorLayout(TensorLayout.NCHW)
                .SetCoordOrigin(CoordOrigin.TopLeft);

        _referenceCommandBuffer =
            new CommandBuffer
            {
                name = "Kiwi v44.55.20 Independent GPU Reference"
            };

        _workersReady = true;

        Debug.Log(
            "[Kiwi v44.55.20 CommonTensor] WORKERS_READY " +
            "lanes=REF_DIRECT_GPU,REF_FROZEN_GPU,REF_CPU,A_GPU,A_CPU,B_GPU,B_CPU" +
            " referenceInput=FROZEN_MATCHED_PRODUCTION_CROP_TEXTURECONVERTER_NCHW_TOPLEFT" +
            " commonFrozenTensor=1 gpuInputsPinned=1" +
            " sameModel=KiwiFaceLandmarkInference" +
            " productionWorkerTouched=0" +
            " observerGpuReadback=1 performanceAuthority=0");
    }

    private static Worker CreateShadowWorker(
        ModelAsset asset,
        BackendType backend)
    {
        Model packed =
            KiwiInferenceFaceTracker
                .BuildSingleReadbackModel(
                    ModelLoader.Load(asset));

        return new Worker(packed, backend);
    }

    private static Tensor<float> CreateInputTensor(bool pinGpu)
    {
        Tensor<float> tensor =
            new Tensor<float>(
                new TensorShape(
                    1,
                    3,
                    InputSize,
                    InputSize));

        if (pinGpu)
        {
            ComputeTensorData.Pin(tensor);
        }

        return tensor;
    }

    private bool TryCaptureExactPair(
        bool isWarmup)
    {
        if (
            !_reflectionReady ||
            !_workersReady ||
            _pairPending ||
            _lanes == null)
        {
            return false;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        if (!_snapshotRequestActive)
        {
            if (now < _nextSnapshotArmAt)
            {
                return false;
            }

            if (!KiwiNativeCameraInterop
                    .TryArmDiagnosticCpuSnapshot())
            {
                _snapshotArmFailureCount++;
                return false;
            }

            _snapshotRequestActive = true;
            _snapshotArmRealtime = now;
            _snapshotSequence = 0UL;
            _snapshotNativeHostTicks = 0L;
            _snapshotManagedHostTicks = 0L;
            _snapshotArmCount++;

            _nextSnapshotArmAt =
                now +
                1.0 /
                _sampleHz;

            return false;
        }

        if (_snapshotNativeHostTicks <= 0L)
        {
            if (
                KiwiNativeCameraInterop
                    .TryGetDiagnosticCpuSnapshotIdentity(
                        out ulong sequence,
                        out long nativeHostTicks))
            {
                _snapshotSequence = sequence;
                _snapshotNativeHostTicks =
                    nativeHostTicks;
                _snapshotReadyCount++;
            }
            else
            {
                if (
                    now -
                    _snapshotArmRealtime >
                    SnapshotMatchTimeoutSeconds)
                {
                    _snapshotMatchTimeoutCount++;
                    ResetSnapshotRequest();
                }

                return false;
            }
        }

        if (_snapshotManagedHostTicks <= 0L)
        {
            ulong observedSequence =
                ReadRunnerULong(
                    _runnerLastObservedFreshSequenceField);

            long observedManagedHostTicks =
                ReadRunnerLong(
                    _runnerLatestSentisSourceHostTicksField);

            if (
                observedSequence ==
                    _snapshotSequence &&
                observedManagedHostTicks > 0L)
            {
                _snapshotManagedHostTicks =
                    observedManagedHostTicks;
            }
            else if (
                observedSequence >
                    _snapshotSequence)
            {
                _snapshotSequenceSkippedCount++;
                ResetSnapshotRequest();
                return false;
            }
            else
            {
                if (
                    now -
                    _snapshotArmRealtime >
                    SnapshotMatchTimeoutSeconds)
                {
                    _snapshotMatchTimeoutCount++;
                    _snapshotManagedTimestampMapFailureCount++;
                    ResetSnapshotRequest();
                }

                return false;
            }
        }

        object matchedLane = null;
        long matchedStartedTicks = 0L;

        for (
            int i = 0;
            i < _lanes.Length;
            i++)
        {
            object lane =
                _lanes.GetValue(i);

            if (lane == null)
            {
                continue;
            }

            bool pending =
                ReadLaneBool(
                    lane,
                    _laneReadbackPendingField);

            long started =
                ReadLaneLong(
                    lane,
                    _lanePendingStartedHostTicksField);

            long sourceHostTicks =
                ReadLaneLong(
                    lane,
                    _lanePendingSourceHostTicksField);

            if (
                !pending ||
                started <= 0L ||
                sourceHostTicks !=
                    _snapshotManagedHostTicks ||
                started <=
                    _lastCapturedProductionStartedTicks)
            {
                continue;
            }

            if (
                matchedLane == null ||
                started >
                    matchedStartedTicks)
            {
                matchedLane = lane;
                matchedStartedTicks = started;
            }
        }

        if (matchedLane == null)
        {
            if (
                now -
                _snapshotArmRealtime >
                SnapshotMatchTimeoutSeconds)
            {
                _snapshotMatchTimeoutCount++;
                _snapshotPresentedButNotScheduledCount++;
                ResetSnapshotRequest();
            }

            return false;
        }

        _pairAttemptCount++;
        _lastCapturedProductionStartedTicks =
            matchedStartedTicks;

        Material cropMaterial =
            _laneCropMaterialField
                .GetValue(
                    matchedLane)
                as Material;

        object cropValue =
            _lanePendingCropMatrixField
                .GetValue(
                    matchedLane);

        object minimumPresenceValue =
            _lanePendingMinimumPresenceField
                .GetValue(
                    matchedLane);

        if (
            cropMaterial == null ||
            !(cropValue is Matrix4x4 cropMatrix) ||
            !(minimumPresenceValue is float minimumPresence) ||
            minimumPresence <= 0f)
        {
            RegisterObserverFault(
                isWarmup,
                "EXACT_LANE_SEMANTIC_STATE_INVALID");
            return false;
        }

        // Production sampling and Production decode deliberately use different
        // matrices. ScheduleModel stores BuildFlipMatrix * cropMatrix in _Xform
        // for image sampling, while pendingCropMatrix keeps the raw cropMatrix
        // consumed by DecodeReadableOutput. Preserve that exact split.
        Matrix4x4 samplingMatrix =
            cropMaterial.GetMatrix(
                XformId);

        int sourceWidth =
            ReadTrackerPrivateInt(
                _trackerSourceWidthField,
                0);

        int sourceHeight =
            ReadTrackerPrivateInt(
                _trackerSourceHeightField,
                0);

        if (
            sourceWidth <= 0 ||
            sourceHeight <= 0)
        {
            RegisterObserverFault(
                isWarmup,
                "SOURCE_DIMENSIONS_INVALID");
            return false;
        }

        minimumPresence =
            Mathf.Clamp01(
                minimumPresence);

        FlattenMatrix(
            samplingMatrix,
            _samplingMatrix);

        RenderTexture productionCropTexture =
            _laneCropTextureField.GetValue(matchedLane)
                as RenderTexture;

        if (
            productionCropTexture == null ||
            !productionCropTexture.IsCreated() ||
            productionCropTexture.width != InputSize ||
            productionCropTexture.height != InputSize ||
            _referenceSnapshotTexture == null ||
            !_referenceSnapshotTexture.IsCreated() ||
            _referenceCommandBuffer == null)
        {
            _referenceFreezeFailureCount++;
            RegisterObserverFault(
                isWarmup,
                "REFERENCE_CROP_TEXTURE_CONTRACT_INVALID");
            return false;
        }

        // Freeze the matched Production crop before the Native CPU candidate
        // work. CopyTexture is queued on the graphics queue, preserving order
        // with the Production Graphics.Blit that produced this lane crop. The
        // private RT is never bound back into Production.
        _referenceCommandBuffer.Clear();
        _referenceCommandBuffer.CopyTexture(
            productionCropTexture,
            _referenceSnapshotTexture);
        Graphics.ExecuteCommandBuffer(
            _referenceCommandBuffer);

        long nativeBegin =
            Stopwatch.GetTimestamp();

        bool leftCopied =
            KiwiNativeCameraInterop
                .TryCopyDiagnosticCpuSnapshotCropNchwFloatQuantized(
                    _snapshotNativeHostTicks,
                    _samplingMatrix,
                    InputSize,
                    0,
                    _leftNchw,
                    out ulong leftSequence,
                    out long leftHostTicks,
                    out ulong leftCpuMicroseconds);

        bool rightCopied =
            KiwiNativeCameraInterop
                .TryCopyDiagnosticCpuSnapshotCropNchwFloatQuantized(
                    _snapshotNativeHostTicks,
                    _samplingMatrix,
                    InputSize,
                    2,
                    _rightNchw,
                    out ulong rightSequence,
                    out long rightHostTicks,
                    out ulong rightCpuMicroseconds);

        long nativeEnd =
            Stopwatch.GetTimestamp();

        if (
            !leftCopied ||
            !rightCopied)
        {
            RegisterObserverFault(
                isWarmup,
                "NATIVE_EXACT_CROP_COPY_FAILED");
            return false;
        }

        if (
            leftSequence !=
                _snapshotSequence ||
            rightSequence !=
                _snapshotSequence ||
            leftHostTicks !=
                _snapshotNativeHostTicks ||
            rightHostTicks !=
                _snapshotNativeHostTicks)
        {
            _sourceIdentityMismatchCount++;

            RegisterObserverFault(
                isWarmup,
                "SOURCE_IDENTITY_MISMATCH");
            return false;
        }

        bool laneStillMatched =
            ReadLaneBool(
                matchedLane,
                _laneReadbackPendingField) &&
            ReadLaneLong(
                matchedLane,
                _lanePendingStartedHostTicksField) ==
                matchedStartedTicks &&
            ReadLaneLong(
                matchedLane,
                _lanePendingSourceHostTicksField) ==
                _snapshotManagedHostTicks;

        if (!laneStillMatched)
        {
            _laneChangedAfterCaptureCount++;
        }

        PairRecord record =
            isWarmup
                ? null
                : new PairRecord
                {
                    Index =
                        _pairCompletedCount,
                    Sequence =
                        _snapshotSequence,
                    NativeHostTicks =
                        _snapshotNativeHostTicks,
                    ManagedHostTicks =
                        _snapshotManagedHostTicks,
                    LaneStartedHostTicks =
                        matchedStartedTicks,
                    SourceWidth =
                        sourceWidth,
                    SourceHeight =
                        sourceHeight,
                    MinimumPresence =
                        minimumPresence,
                    LaneStillMatchedAfterCapture =
                        laneStillMatched,
                    LeftNativeCpuMs =
                        leftCpuMicroseconds /
                        1000.0,
                    RightNativeCpuMs =
                        rightCpuMicroseconds /
                        1000.0,
                    NativePairWallMs =
                        TicksToMilliseconds(
                            nativeEnd -
                            nativeBegin)
                };

        if (!isWarmup)
        {
            _leftNativeCropCpuMs.Add(
                leftCpuMicroseconds /
                1000.0);

            _rightNativeCropCpuMs.Add(
                rightCpuMicroseconds /
                1000.0);

            _nativePairWallMs.Add(
                TicksToMilliseconds(
                    nativeEnd -
                    nativeBegin));
        }

        _pendingIsWarmup = isWarmup;
        _pendingRecord = record;
        _pendingCropMatrix = cropMatrix;
        _pendingMinimumPresence =
            minimumPresence;
        _pendingSourceWidth = sourceWidth;
        _pendingSourceHeight = sourceHeight;
        _transitRequired =
            !isWarmup &&
            _transitValidationCount < TransitValidationSamples;
        _referenceOutputDone = !_transitRequired;
        _referenceInputDone = false;
        _leftDone = false;
        _rightDone = false;
        _commonScheduled = false;
        _referenceFrozenGpuDone = false;
        _referenceCpuDone = false;
        _aGpuDone = false;
        _bGpuDone = false;
        _referenceResult = default;
        _leftResult = default;
        _rightResult = default;
        _referenceFrozenGpuResult = default;
        _referenceCpuResult = default;
        _aGpuResult = default;
        _bGpuResult = default;
        if (record != null)
        {
            record.TransitRequired = _transitRequired;
        }
        _pairPending = true;
        _scheduleFrame = Time.frameCount;

        int token = ++_pairToken;

        try
        {
            ScheduleShadowPair(
                token);
        }
        catch (Exception exception)
        {
            RegisterObserverFault(
                isWarmup,
                "SHADOW_SCHEDULE_FAIL " +
                exception.GetType().Name +
                " " +
                exception.Message);

            return false;
        }

        return true;
    }

    private void ScheduleShadowPair(
        int token)
    {
        _referenceCommandBuffer.Clear();
        _referenceCommandBuffer.ToTensor(
            _referenceSnapshotTexture,
            _referenceInput,
            _referenceTextureTransform);

        if (_transitRequired)
        {
            _referenceCommandBuffer.ScheduleWorker(
                _referenceWorker,
                _referenceInput);
        }

        Graphics.ExecuteCommandBuffer(
            _referenceCommandBuffer);

        if (_transitRequired)
        {
            Tensor<float> referenceOutput =
                _referenceWorker.PeekOutput(0)
                as Tensor<float>;

            if (referenceOutput == null)
            {
                throw new InvalidOperationException(
                    "Direct reference output missing.");
            }

            _referenceScheduleDoneTicks =
                Stopwatch.GetTimestamp();

            BeginReferenceOutputReadback(
                referenceOutput,
                token);
        }

        BeginReferenceInputReadback(
            token);
    }

    private void ScheduleCommonTensorLanes(int token)
    {
        if (_commonScheduled)
        {
            throw new InvalidOperationException(
                "Common tensor lanes already scheduled.");
        }

        _commonScheduled = true;

        _referenceFrozenGpuInput.Upload(_referenceNchw);
        _referenceCpuInput.Upload(_referenceNchw);
        _aGpuInput.Upload(_leftNchw);
        _leftInput.Upload(_leftNchw);
        _bGpuInput.Upload(_rightNchw);
        _rightInput.Upload(_rightNchw);

        _referenceFrozenGpuWorker.Schedule(_referenceFrozenGpuInput);
        _referenceCpuWorker.Schedule(_referenceCpuInput);
        _aGpuWorker.Schedule(_aGpuInput);
        _leftWorker.Schedule(_leftInput);
        _bGpuWorker.Schedule(_bGpuInput);
        _rightWorker.Schedule(_rightInput);

        Tensor<float> referenceFrozenGpuOutput =
            _referenceFrozenGpuWorker.PeekOutput(0) as Tensor<float>;
        Tensor<float> referenceCpuOutput =
            _referenceCpuWorker.PeekOutput(0) as Tensor<float>;
        Tensor<float> aGpuOutput =
            _aGpuWorker.PeekOutput(0) as Tensor<float>;
        Tensor<float> aCpuOutput =
            _leftWorker.PeekOutput(0) as Tensor<float>;
        Tensor<float> bGpuOutput =
            _bGpuWorker.PeekOutput(0) as Tensor<float>;
        Tensor<float> bCpuOutput =
            _rightWorker.PeekOutput(0) as Tensor<float>;

        if (
            referenceFrozenGpuOutput == null ||
            referenceCpuOutput == null ||
            aGpuOutput == null ||
            aCpuOutput == null ||
            bGpuOutput == null ||
            bCpuOutput == null)
        {
            throw new InvalidOperationException(
                "One or more common-tensor outputs are missing.");
        }

        _leftScheduleDoneTicks = Stopwatch.GetTimestamp();
        _rightScheduleDoneTicks = _leftScheduleDoneTicks;

        BeginCommonReadback(
            referenceFrozenGpuOutput,
            CommonLane.ReferenceFrozenGpu,
            token);
        BeginCommonReadback(
            referenceCpuOutput,
            CommonLane.ReferenceCpu,
            token);
        BeginCommonReadback(
            aGpuOutput,
            CommonLane.AGpu,
            token);
        BeginCommonReadback(
            aCpuOutput,
            CommonLane.ACpu,
            token);
        BeginCommonReadback(
            bGpuOutput,
            CommonLane.BGpu,
            token);
        BeginCommonReadback(
            bCpuOutput,
            CommonLane.BCpu,
            token);
    }

    private void BeginReferenceOutputReadback(
        Tensor<float> output,
        int token)
    {
        var awaiter =
            output
                .ReadbackAndCloneAsync()
                .GetAwaiter();

        awaiter.OnCompleted(
            () =>
            {
                Tensor<float> readable = null;

                try
                {
                    readable = awaiter.GetResult();

                    if (
                        token != _pairToken ||
                        !_pairPending)
                    {
                        return;
                    }

                    _referenceResult =
                        DecodeSideReadOnly(
                            readable,
                            _referenceDecoded);
                    _referenceOutputDone = true;
                    TryFinalizePair();
                }
                catch (Exception exception)
                {
                    _decodeReflectionFailureCount++;
                    RegisterObserverFault(
                        _pendingIsWarmup,
                        "REFERENCE_GPU_OUTPUT_DECODE_FAIL " +
                        exception.GetType().Name +
                        " " +
                        exception.Message);
                }
                finally
                {
                    readable?.Dispose();
                }
            });
    }

    private void BeginReferenceInputReadback(
        int token)
    {
        var awaiter =
            _referenceInput
                .ReadbackAndCloneAsync()
                .GetAwaiter();

        awaiter.OnCompleted(
            () =>
            {
                Tensor<float> readable = null;

                try
                {
                    readable = awaiter.GetResult();

                    if (
                        token != _pairToken ||
                        !_pairPending)
                    {
                        return;
                    }

                    if (
                        readable == null ||
                        readable.shape.length != InputFloatCount)
                    {
                        throw new InvalidOperationException(
                            "Reference input tensor shape mismatch.");
                    }

                    for (int i = 0; i < InputFloatCount; i++)
                    {
                        _referenceNchw[i] = readable[i];

                        if (!IsFinite(_referenceNchw[i]))
                        {
                            _referenceTensorNonFiniteCount++;
                        }
                    }

                    if (!_pendingIsWarmup)
                    {
                        _referenceVsCurrentInput.Accumulate(
                            _referenceNchw,
                            _leftNchw);
                        _referenceVsMode2Input.Accumulate(
                            _referenceNchw,
                            _rightNchw);
                        _currentVsMode2Input.Accumulate(
                            _leftNchw,
                            _rightNchw);

                        if (_pendingRecord != null)
                        {
                            _pendingRecord.ReferenceVsCurrentInputMeanAbsLsb =
                                _referenceVsCurrentInput.LastPairMeanAbsLsb;
                            _pendingRecord.ReferenceVsMode2InputMeanAbsLsb =
                                _referenceVsMode2Input.LastPairMeanAbsLsb;
                            _pendingRecord.CurrentVsMode2InputMeanAbsLsb =
                                _currentVsMode2Input.LastPairMeanAbsLsb;

                            _referenceVsASpatial.Accumulate(
                                _pendingRecord.Index,
                                _pendingRecord.Sequence,
                                _referenceNchw,
                                _leftNchw,
                                _spatialTopRecords);

                            _referenceVsBSpatial.Accumulate(
                                _pendingRecord.Index,
                                _pendingRecord.Sequence,
                                _referenceNchw,
                                _rightNchw,
                                _spatialTopRecords);
                        }
                    }

                    _referenceInputDone = true;
                    ScheduleCommonTensorLanes(token);
                    TryFinalizePair();
                }
                catch (Exception exception)
                {
                    _referenceInputReadbackFailureCount++;
                    RegisterObserverFault(
                        _pendingIsWarmup,
                        "REFERENCE_INPUT_READBACK_FAIL " +
                        exception.GetType().Name +
                        " " +
                        exception.Message);
                }
                finally
                {
                    readable?.Dispose();
                }
            });
    }

    private void BeginSideReadback(
        Tensor<float> output,
        bool left,
        int token)
    {
        var awaiter =
            output
                .ReadbackAndCloneAsync()
                .GetAwaiter();

        awaiter.OnCompleted(
            () =>
            {
                Tensor<float> readable = null;

                try
                {
                    // Always consume/dispose the clone even if this observer pair
                    // became stale after the async request was registered.
                    readable =
                        awaiter.GetResult();

                    if (
                        token !=
                            _pairToken ||
                        !_pairPending)
                    {
                        return;
                    }

                    SideResult result =
                        DecodeSideReadOnly(
                            readable,
                            left
                                ? _leftDecoded
                                : _rightDecoded);

                    long doneTicks =
                        Stopwatch.GetTimestamp();

                    if (left)
                    {
                        _leftResult = result;
                        _leftDone = true;

                        if (!_pendingIsWarmup)
                        {
                            _leftCpuServiceMs.Add(
                                TicksToMilliseconds(
                                    doneTicks -
                                    _leftScheduleDoneTicks));
                        }
                    }
                    else
                    {
                        _rightResult = result;
                        _rightDone = true;

                        if (!_pendingIsWarmup)
                        {
                            _rightCpuServiceMs.Add(
                                TicksToMilliseconds(
                                    doneTicks -
                                    _rightScheduleDoneTicks));
                        }
                    }

                    TryFinalizePair();
                }
                catch (Exception exception)
                {
                    _decodeReflectionFailureCount++;

                    RegisterObserverFault(
                        _pendingIsWarmup,
                        "CPU_OUTPUT_DECODE_FAIL " +
                        exception.GetType().Name +
                        " " +
                        exception.Message);
                }
                finally
                {
                    readable?.Dispose();
                }
            });
    }

    private void BeginCommonReadback(
        Tensor<float> output,
        CommonLane lane,
        int token)
    {
        var awaiter =
            output
                .ReadbackAndCloneAsync()
                .GetAwaiter();

        awaiter.OnCompleted(
            () =>
            {
                Tensor<float> readable = null;

                try
                {
                    readable = awaiter.GetResult();

                    if (
                        token != _pairToken ||
                        !_pairPending)
                    {
                        return;
                    }

                    Vector3[] destination =
                        lane switch
                        {
                            CommonLane.ReferenceFrozenGpu => _referenceFrozenGpuDecoded,
                            CommonLane.ReferenceCpu => _referenceCpuDecoded,
                            CommonLane.AGpu => _aGpuDecoded,
                            CommonLane.ACpu => _leftDecoded,
                            CommonLane.BGpu => _bGpuDecoded,
                            CommonLane.BCpu => _rightDecoded,
                            _ => throw new ArgumentOutOfRangeException(nameof(lane))
                        };

                    SideResult result =
                        DecodeSideReadOnly(
                            readable,
                            destination);

                    switch (lane)
                    {
                        case CommonLane.ReferenceFrozenGpu:
                            _referenceFrozenGpuResult = result;
                            _referenceFrozenGpuDone = true;
                            break;
                        case CommonLane.ReferenceCpu:
                            _referenceCpuResult = result;
                            _referenceCpuDone = true;
                            break;
                        case CommonLane.AGpu:
                            _aGpuResult = result;
                            _aGpuDone = true;
                            break;
                        case CommonLane.ACpu:
                            _leftResult = result;
                            _leftDone = true;
                            break;
                        case CommonLane.BGpu:
                            _bGpuResult = result;
                            _bGpuDone = true;
                            break;
                        case CommonLane.BCpu:
                            _rightResult = result;
                            _rightDone = true;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(lane));
                    }

                    TryFinalizePair();
                }
                catch (Exception exception)
                {
                    _decodeReflectionFailureCount++;
                    RegisterObserverFault(
                        _pendingIsWarmup,
                        "COMMON_OUTPUT_DECODE_FAIL lane=" +
                        lane +
                        " " +
                        exception.GetType().Name +
                        " " +
                        exception.Message);
                }
                finally
                {
                    readable?.Dispose();
                }
            });
    }

    private SideResult DecodeSideReadOnly(
        Tensor<float> readable,
        Vector3[] destination)
    {
        if (
            readable == null ||
            destination == null ||
            destination.Length <
                CompatibleLandmarkCount)
        {
            throw new InvalidOperationException(
                "Decode input invalid.");
        }

        object[] args =
        {
            readable,
            _pendingCropMatrix,
            destination,
            _pendingMinimumPresence,
            0f,
            0f,
            Quaternion.identity
        };

        object statusObject =
            _decodeReadableOutputMethod
                .Invoke(
                    _tracker,
                    args);

        int status =
            statusObject != null
                ? Convert.ToInt32(
                    statusObject,
                    CultureInfo.InvariantCulture)
                : -1;

        string statusName =
            statusObject != null
                ? statusObject.ToString()
                : "<null>";

        float rawPresence =
            args[4] is float raw
                ? raw
                : float.NaN;

        float presence =
            args[5] is float normalized
                ? normalized
                : float.NaN;

        Quaternion rotation =
            args[6] is Quaternion q
                ? q
                : Quaternion.identity;

        bool accepted =
            string.Equals(
                statusName,
                "Valid",
                StringComparison.Ordinal);

        SideResult result =
            new SideResult
            {
                DecodeInvocationSucceeded =
                    true,
                DecodeStatus =
                    status,
                DecodeStatusName =
                    statusName,
                RawPresence =
                    rawPresence,
                Presence =
                    presence,
                Rotation =
                    rotation,
                Accepted =
                    accepted
            };

        if (accepted)
        {
            result.Canonical =
                BuildStatelessCanonical(
                    destination,
                    rotation);

            result.CanonicalValid =
                result.Canonical.IsValid;
        }

        return result;
    }

    private CanonicalSnapshot BuildStatelessCanonical(
        Vector3[] landmarks,
        Quaternion rotation)
    {
        CanonicalSnapshot data =
            default;

        if (
            landmarks == null ||
            landmarks.Length <
                CompatibleLandmarkCount ||
            !IsFinite(rotation))
        {
            return data;
        }

        Vector3 rightEyeCenter3D =
            (
                landmarks[33] +
                landmarks[133]
            ) *
            0.5f;

        Vector3 leftEyeCenter3D =
            (
                landmarks[362] +
                landmarks[263]
            ) *
            0.5f;

        Vector2 rightEyeCenter =
            new Vector2(
                rightEyeCenter3D.x,
                rightEyeCenter3D.y);

        Vector2 leftEyeCenter =
            new Vector2(
                leftEyeCenter3D.x,
                leftEyeCenter3D.y);

        Vector2 eyeCenter =
            (
                rightEyeCenter +
                leftEyeCenter
            ) *
            0.5f;

        Vector2 chin =
            new Vector2(
                landmarks[152].x,
                landmarks[152].y);

        Vector2 faceCenter =
            eyeCenter +
            (
                chin -
                eyeCenter
            ) *
            1.30f;

        Vector2 leftCheek =
            new Vector2(
                landmarks[234].x,
                landmarks[234].y);

        Vector2 rightCheek =
            new Vector2(
                landmarks[454].x,
                landmarks[454].y);

        Vector2 cheekCenter =
            (
                leftCheek +
                rightCheek
            ) *
            0.5f;

        Vector2 nose =
            new Vector2(
                landmarks[1].x,
                landmarks[1].y);

        Vector2 forehead =
            new Vector2(
                landmarks[10].x,
                landmarks[10].y);

        float eyeSpan2D =
            Vector2.Distance(
                rightEyeCenter,
                leftEyeCenter);

        float eyeSpan3D =
            Vector3.Distance(
                rightEyeCenter3D,
                leftEyeCenter3D);

        float faceWidth2D =
            Vector2.Distance(
                leftCheek,
                rightCheek);

        float faceHeight2D =
            Vector2.Distance(
                forehead,
                chin);

        float geometryQuality =
            KiwiPrecisionTrackingMath
                .CalculateGeometryQuality(
                    eyeSpan2D,
                    faceWidth2D,
                    faceHeight2D);

        FaceExpressionData expression =
            default;

        try
        {
            object expressionObject =
                _extractGeometryExpressionDataMethod
                    .Invoke(
                        null,
                        new object[]
                        {
                            landmarks,
                            faceWidth2D
                        });

            if (expressionObject is FaceExpressionData value)
            {
                expression = value;
            }
            else
            {
                _expressionReflectionFailureCount++;
                return data;
            }
        }
        catch
        {
            _expressionReflectionFailureCount++;
            return data;
        }

        bool valid =
            eyeSpan2D > 0.0001f &&
            faceWidth2D > 0.0001f &&
            faceHeight2D > 0.0001f &&
            IsFinite(eyeSpan2D) &&
            IsFinite(eyeSpan3D) &&
            IsFinite(faceWidth2D) &&
            IsFinite(faceHeight2D) &&
            IsFinite(geometryQuality) &&
            IsFinite(faceCenter) &&
            IsFinite(rightEyeCenter) &&
            IsFinite(leftEyeCenter) &&
            IsFinite(eyeCenter) &&
            IsFinite(chin) &&
            IsFinite(nose) &&
            IsFinite(cheekCenter) &&
            IsFinite(forehead) &&
            IsFinite(rotation) &&
            IsFinite(expression);

        if (!valid)
        {
            return data;
        }

        data.IsValid = true;
        data.FaceCenter = faceCenter;
        data.RightEyeCenter = rightEyeCenter;
        data.LeftEyeCenter = leftEyeCenter;
        data.EyeCenter = eyeCenter;
        data.Chin = chin;
        data.Nose = nose;
        data.CheekCenter = cheekCenter;
        data.Forehead = forehead;
        data.EyeSpan2D = eyeSpan2D;
        data.EyeSpan3D = eyeSpan3D;
        data.FaceWidth2D = faceWidth2D;
        data.FaceHeight2D = faceHeight2D;
        data.GeometryQuality = geometryQuality;
        data.Rotation = rotation;
        data.Expression = expression;

        return data;
    }

    private void TryFinalizePair()
    {
        if (
            !_pairPending ||
            !_referenceOutputDone ||
            !_referenceInputDone ||
            !_commonScheduled ||
            !_referenceFrozenGpuDone ||
            !_referenceCpuDone ||
            !_aGpuDone ||
            !_leftDone ||
            !_bGpuDone ||
            !_rightDone)
        {
            return;
        }

        bool isWarmup =
            _pendingIsWarmup;

        int completionFrames =
            Mathf.Max(
                0,
                Time.frameCount -
                _scheduleFrame);

        if (!isWarmup)
        {
            ComparisonRecord transit = default;

            if (_transitRequired)
            {
                transit =
                    CompareReferenceCandidate(
                        _referenceResult,
                        _referenceFrozenGpuResult,
                        _referenceDecoded,
                        _referenceFrozenGpuDecoded,
                        _transitDirectVsFrozen,
                        _pendingRecord);

                _transitValidationCount++;
            }

            ComparisonRecord backendReference =
                CompareReferenceCandidate(
                    _referenceFrozenGpuResult,
                    _referenceCpuResult,
                    _referenceFrozenGpuDecoded,
                    _referenceCpuDecoded,
                    _backendReference,
                    _pendingRecord);

            ComparisonRecord backendA =
                CompareReferenceCandidate(
                    _aGpuResult,
                    _leftResult,
                    _aGpuDecoded,
                    _leftDecoded,
                    _backendA,
                    _pendingRecord);

            ComparisonRecord backendB =
                CompareReferenceCandidate(
                    _bGpuResult,
                    _rightResult,
                    _bGpuDecoded,
                    _rightDecoded,
                    _backendB,
                    _pendingRecord);

            ComparisonRecord preprocessGpuA =
                CompareReferenceCandidate(
                    _referenceFrozenGpuResult,
                    _aGpuResult,
                    _referenceFrozenGpuDecoded,
                    _aGpuDecoded,
                    _referenceVsCurrent,
                    _pendingRecord);

            ComparisonRecord preprocessGpuB =
                CompareReferenceCandidate(
                    _referenceFrozenGpuResult,
                    _bGpuResult,
                    _referenceFrozenGpuDecoded,
                    _bGpuDecoded,
                    _referenceVsMode2,
                    _pendingRecord);

            ComparisonRecord preprocessCpuA =
                CompareReferenceCandidate(
                    _referenceCpuResult,
                    _leftResult,
                    _referenceCpuDecoded,
                    _leftDecoded,
                    _preprocessCpuA,
                    _pendingRecord);

            ComparisonRecord preprocessCpuB =
                CompareReferenceCandidate(
                    _referenceCpuResult,
                    _rightResult,
                    _referenceCpuDecoded,
                    _rightDecoded,
                    _preprocessCpuB,
                    _pendingRecord);

            CompareSemanticPair(
                _leftResult,
                _rightResult,
                _pendingRecord);

            if (_pendingRecord != null)
            {
                _pendingRecord.ReferenceDecodeStatus =
                    _referenceFrozenGpuResult.DecodeStatus;
                _pendingRecord.ReferenceDecodeStatusName =
                    _referenceFrozenGpuResult.DecodeStatusName;
                _pendingRecord.ReferenceRawPresence =
                    _referenceFrozenGpuResult.RawPresence;
                _pendingRecord.ReferencePresence =
                    _referenceFrozenGpuResult.Presence;
                _pendingRecord.ReferenceAccepted =
                    _referenceFrozenGpuResult.Accepted;
                _pendingRecord.ReferenceCanonicalValid =
                    _referenceFrozenGpuResult.CanonicalValid;
                _pendingRecord.ReferenceVsCurrent =
                    preprocessGpuA;
                _pendingRecord.ReferenceVsMode2 =
                    preprocessGpuB;
                _pendingRecord.TransitDirectVsFrozen = transit;
                _pendingRecord.BackendReference = backendReference;
                _pendingRecord.BackendA = backendA;
                _pendingRecord.BackendB = backendB;
                _pendingRecord.PreprocessGpuA = preprocessGpuA;
                _pendingRecord.PreprocessGpuB = preprocessGpuB;
                _pendingRecord.PreprocessCpuA = preprocessCpuA;
                _pendingRecord.PreprocessCpuB = preprocessCpuB;
                _pendingRecord.CompletionFrames =
                    completionFrames;

                _records.Add(
                    _pendingRecord);
            }

            _pairCompletionFrames.Add(
                completionFrames);

            _pairCompletedCount++;
        }
        else
        {
            _warmupPairsRemaining--;
        }

        _pairPending = false;
        _pendingRecord = null;
        _referenceOutputDone = false;
        _referenceInputDone = false;
        _leftDone = false;
        _rightDone = false;
        _commonScheduled = false;
        _referenceFrozenGpuDone = false;
        _referenceCpuDone = false;
        _aGpuDone = false;
        _bGpuDone = false;

        ResetSnapshotRequest();

        if (
            isWarmup &&
            _warmupPairsRemaining <= 0)
        {
            BeginMeasurement();
        }
    }

    private ComparisonRecord CompareReferenceCandidate(
        SideResult reference,
        SideResult candidate,
        Vector3[] referenceLandmarks,
        Vector3[] candidateLandmarks,
        ComparisonMetrics metrics,
        PairRecord record)
    {
        ComparisonRecord result = default;

        if (
            !reference.DecodeInvocationSucceeded ||
            !candidate.DecodeInvocationSucceeded)
        {
            _observerFaultCount++;
            return result;
        }

        bool decodeMismatch =
            reference.DecodeStatus != candidate.DecodeStatus;
        bool acceptanceMismatch =
            reference.Accepted != candidate.Accepted;
        bool canonicalMismatch =
            reference.CanonicalValid != candidate.CanonicalValid;
        bool nonFinite =
            !IsFinite(reference.RawPresence) ||
            !IsFinite(candidate.RawPresence) ||
            !IsFinite(reference.Presence) ||
            !IsFinite(candidate.Presence);

        if (decodeMismatch) metrics.DecodeStatusMismatchCount++;
        if (acceptanceMismatch) metrics.AcceptanceMismatchCount++;
        if (canonicalMismatch) metrics.CanonicalValidityMismatchCount++;
        if (nonFinite) metrics.NonFiniteCount++;

        int hardMask = 0;
        if (decodeMismatch) hardMask |= HardDecodeStatusMismatch;
        if (acceptanceMismatch) hardMask |= HardAcceptanceMismatch;
        if (canonicalMismatch) hardMask |= HardCanonicalValidityMismatch;
        if (nonFinite) hardMask |= HardNonFiniteSemantic;

        result.HardMask = hardMask;

        if (hardMask != 0)
        {
            metrics.HardInvariantViolationCount++;
            string detail =
                "comparison=" + metrics.Name +
                "|index=" +
                (record != null ? record.Index.ToString(CultureInfo.InvariantCulture) : "-1") +
                "|sequence=" +
                (record != null ? record.Sequence.ToString(CultureInfo.InvariantCulture) : "0") +
                "|referenceDecode=" + (reference.DecodeStatusName ?? "<null>") +
                "|candidateDecode=" + (candidate.DecodeStatusName ?? "<null>") +
                "|referenceAccepted=" + B(reference.Accepted) +
                "|candidateAccepted=" + B(candidate.Accepted) +
                "|referenceCanonical=" + B(reference.CanonicalValid) +
                "|candidateCanonical=" + B(candidate.CanonicalValid) +
                "|hardMask=" + hardMask.ToString(CultureInfo.InvariantCulture);
            metrics.HardDetails.Add(detail);
            Debug.LogWarning(
                "[Kiwi v44.55.20 CommonTensor] HARD_INVARIANT " +
                detail);
        }

        if (!nonFinite)
        {
            result.PresenceAbsDiff =
                Math.Abs((double)reference.Presence - candidate.Presence);
            metrics.Presence.Add(result.PresenceAbsDiff);
        }

        bool comparable =
            reference.Accepted &&
            candidate.Accepted &&
            reference.CanonicalValid &&
            candidate.CanonicalValid;

        result.Comparable = comparable;

        if (!comparable)
        {
            return result;
        }

        metrics.CanonicalComparableCount++;

        CanonicalSnapshot a = reference.Canonical;
        CanonicalSnapshot b = candidate.Canonical;
        double pointMax = 0.0;

        AddComparisonPoint(metrics, a.FaceCenter, b.FaceCenter, ref pointMax);
        AddComparisonPoint(metrics, a.RightEyeCenter, b.RightEyeCenter, ref pointMax);
        AddComparisonPoint(metrics, a.LeftEyeCenter, b.LeftEyeCenter, ref pointMax);
        AddComparisonPoint(metrics, a.EyeCenter, b.EyeCenter, ref pointMax);
        AddComparisonPoint(metrics, a.Chin, b.Chin, ref pointMax);
        AddComparisonPoint(metrics, a.Nose, b.Nose, ref pointMax);
        AddComparisonPoint(metrics, a.CheekCenter, b.CheekCenter, ref pointMax);
        AddComparisonPoint(metrics, a.Forehead, b.Forehead, ref pointMax);
        result.CanonicalPointMaxPx = pointMax;

        result.RotationDiffDegrees =
            Quaternion.Angle(
                NormalizeQuaternionSafe(a.Rotation),
                NormalizeQuaternionSafe(b.Rotation));
        metrics.Rotation.Add(result.RotationDiffDegrees);

        double scaleMax = 0.0;
        AddComparisonScale(metrics, a.EyeSpan2D, b.EyeSpan2D, ref scaleMax);
        AddComparisonScale(metrics, a.EyeSpan3D, b.EyeSpan3D, ref scaleMax);
        AddComparisonScale(metrics, a.FaceWidth2D, b.FaceWidth2D, ref scaleMax);
        AddComparisonScale(metrics, a.FaceHeight2D, b.FaceHeight2D, ref scaleMax);
        result.ScaleRelativeMax = scaleMax;

        result.GeometryQualityAbsDiff =
            Math.Abs((double)a.GeometryQuality - b.GeometryQuality);
        metrics.GeometryQuality.Add(result.GeometryQualityAbsDiff);

        double expressionMax = 0.0;
        AddComparisonExpression(metrics, a.Expression.eyeBlinkLeft, b.Expression.eyeBlinkLeft, ref expressionMax);
        AddComparisonExpression(metrics, a.Expression.eyeBlinkRight, b.Expression.eyeBlinkRight, ref expressionMax);
        AddComparisonExpression(metrics, a.Expression.eyeWideLeft, b.Expression.eyeWideLeft, ref expressionMax);
        AddComparisonExpression(metrics, a.Expression.eyeWideRight, b.Expression.eyeWideRight, ref expressionMax);
        AddComparisonExpression(metrics, a.Expression.jawOpen, b.Expression.jawOpen, ref expressionMax);
        AddComparisonExpression(metrics, a.Expression.mouthSmileLeft, b.Expression.mouthSmileLeft, ref expressionMax);
        AddComparisonExpression(metrics, a.Expression.mouthSmileRight, b.Expression.mouthSmileRight, ref expressionMax);
        AddComparisonExpression(metrics, a.Expression.mouthPucker, b.Expression.mouthPucker, ref expressionMax);
        AddComparisonExpression(metrics, a.Expression.mouthFunnel, b.Expression.mouthFunnel, ref expressionMax);
        result.ExpressionMaxAbsDiff = expressionMax;

        List<double> pair2d = new List<double>(CompatibleLandmarkCount);
        List<double> pairZ = new List<double>(CompatibleLandmarkCount);
        double landmark2dMax = 0.0;
        double landmarkZMax = 0.0;

        for (int i = 0; i < CompatibleLandmarkCount; i++)
        {
            double dx = (referenceLandmarks[i].x - candidateLandmarks[i].x) * _pendingSourceWidth;
            double dy = (referenceLandmarks[i].y - candidateLandmarks[i].y) * _pendingSourceHeight;
            double d2 = Math.Sqrt(dx * dx + dy * dy);
            double dz = Math.Abs((double)referenceLandmarks[i].z - candidateLandmarks[i].z);

            if (
                double.IsNaN(d2) ||
                double.IsInfinity(d2) ||
                double.IsNaN(dz) ||
                double.IsInfinity(dz))
            {
                metrics.NonFiniteCount++;
                continue;
            }

            metrics.Landmark2d.Add(d2);
            metrics.LandmarkZ.Add(dz);
            pair2d.Add(d2);
            pairZ.Add(dz);
            landmark2dMax = Math.Max(landmark2dMax, d2);
            landmarkZMax = Math.Max(landmarkZMax, dz);
        }

        result.Landmark2dP95Px = ComputeStats(pair2d).P95;
        result.Landmark2dMaxPx = landmark2dMax;
        result.LandmarkZP95Abs = ComputeStats(pairZ).P95;
        result.LandmarkZMaxAbs = landmarkZMax;

        return result;
    }

    private void AddComparisonPoint(
        ComparisonMetrics metrics,
        Vector2 reference,
        Vector2 candidate,
        ref double pairMax)
    {
        double dx = (reference.x - candidate.x) * _pendingSourceWidth;
        double dy = (reference.y - candidate.y) * _pendingSourceHeight;
        double value = Math.Sqrt(dx * dx + dy * dy);
        metrics.CanonicalPoints.Add(value);
        pairMax = Math.Max(pairMax, value);
    }

    private static void AddComparisonScale(
        ComparisonMetrics metrics,
        float reference,
        float candidate,
        ref double pairMax)
    {
        double denominator = Math.Max(0.000001, Math.Abs(reference));
        double value = Math.Abs((double)reference - candidate) / denominator;
        metrics.Scale.Add(value);
        pairMax = Math.Max(pairMax, value);
    }

    private static void AddComparisonExpression(
        ComparisonMetrics metrics,
        float reference,
        float candidate,
        ref double pairMax)
    {
        double value = Math.Abs((double)reference - candidate);
        metrics.Expression.Add(value);
        pairMax = Math.Max(pairMax, value);
    }

    private void CompareSemanticPair(
        SideResult left,
        SideResult right,
        PairRecord record)
    {
        if (
            !left.DecodeInvocationSucceeded ||
            !right.DecodeInvocationSucceeded)
        {
            _observerFaultCount++;
            return;
        }

        bool decodeStatusMismatch =
            left.DecodeStatus !=
            right.DecodeStatus;

        bool acceptanceMismatch =
            left.Accepted !=
            right.Accepted;

        bool canonicalValidityMismatch =
            left.CanonicalValid !=
            right.CanonicalValid;

        bool semanticNonFinite =
            !IsFinite(left.RawPresence) ||
            !IsFinite(right.RawPresence) ||
            !IsFinite(left.Presence) ||
            !IsFinite(right.Presence);

        if (decodeStatusMismatch)
        {
            _decodeStatusMismatchCount++;
        }

        if (acceptanceMismatch)
        {
            _acceptanceMismatchCount++;
        }

        if (canonicalValidityMismatch)
        {
            _canonicalValidityMismatchCount++;
        }

        if (semanticNonFinite)
        {
            _nonFiniteSemanticPairCount++;
        }

        double presenceDiff = 0.0;
        double leftMargin = double.NaN;
        double rightMargin = double.NaN;
        double minimumAbsMargin = double.NaN;
        bool thresholdCrossed = false;

        if (!semanticNonFinite)
        {
            presenceDiff =
                Math.Abs(
                    (double)left.Presence -
                    right.Presence);

            leftMargin =
                (double)left.Presence -
                _pendingMinimumPresence;

            rightMargin =
                (double)right.Presence -
                _pendingMinimumPresence;

            minimumAbsMargin =
                Math.Min(
                    Math.Abs(leftMargin),
                    Math.Abs(rightMargin));

            thresholdCrossed =
                (
                    left.Presence >=
                    _pendingMinimumPresence
                ) !=
                (
                    right.Presence >=
                    _pendingMinimumPresence
                );

            _presenceDiff.Add(
                presenceDiff);

            if (thresholdCrossed)
            {
                _presenceThresholdCrossingPairCount++;
            }
        }

        int hardMask = 0;

        if (decodeStatusMismatch)
        {
            hardMask |=
                HardDecodeStatusMismatch;
        }

        if (acceptanceMismatch)
        {
            hardMask |=
                HardAcceptanceMismatch;
        }

        if (canonicalValidityMismatch)
        {
            hardMask |=
                HardCanonicalValidityMismatch;
        }

        if (semanticNonFinite)
        {
            hardMask |=
                HardNonFiniteSemantic;
        }

        bool hardViolation =
            hardMask != 0;

        if (hardViolation)
        {
            _hardInvariantViolationPairCount++;

            string detail =
                "index=" +
                (record != null ? record.Index.ToString(CultureInfo.InvariantCulture) : "-1") +
                "|sequence=" +
                (record != null ? record.Sequence.ToString(CultureInfo.InvariantCulture) : "0") +
                "|nativeHostTicks=" +
                (record != null ? record.NativeHostTicks.ToString(CultureInfo.InvariantCulture) : "0") +
                "|managedHostTicks=" +
                (record != null ? record.ManagedHostTicks.ToString(CultureInfo.InvariantCulture) : "0") +
                "|minimumPresence=" +
                _pendingMinimumPresence.ToString("F9", CultureInfo.InvariantCulture) +
                "|leftRawPresence=" +
                left.RawPresence.ToString("F9", CultureInfo.InvariantCulture) +
                "|rightRawPresence=" +
                right.RawPresence.ToString("F9", CultureInfo.InvariantCulture) +
                "|leftPresence=" +
                left.Presence.ToString("F9", CultureInfo.InvariantCulture) +
                "|rightPresence=" +
                right.Presence.ToString("F9", CultureInfo.InvariantCulture) +
                "|leftMargin=" +
                leftMargin.ToString("F9", CultureInfo.InvariantCulture) +
                "|rightMargin=" +
                rightMargin.ToString("F9", CultureInfo.InvariantCulture) +
                "|minAbsMargin=" +
                minimumAbsMargin.ToString("F9", CultureInfo.InvariantCulture) +
                "|thresholdCrossed=" +
                B(thresholdCrossed) +
                "|leftDecode=" +
                (left.DecodeStatusName ?? "<null>") +
                "|rightDecode=" +
                (right.DecodeStatusName ?? "<null>") +
                "|leftAccepted=" +
                B(left.Accepted) +
                "|rightAccepted=" +
                B(right.Accepted) +
                "|leftCanonical=" +
                B(left.CanonicalValid) +
                "|rightCanonical=" +
                B(right.CanonicalValid) +
                "|hardMask=" +
                hardMask.ToString(CultureInfo.InvariantCulture);

            _hardInvariantDetails.Add(detail);

            Debug.LogWarning(
                "[Kiwi v44.55.20 CommonTensor] HARD_INVARIANT " +
                detail);
        }

        if (
            left.Accepted &&
            right.Accepted &&
            left.CanonicalValid &&
            right.CanonicalValid)
        {
            _canonicalComparablePairCount++;

            CompareCanonical(
                left.Canonical,
                right.Canonical,
                record);

            CompareLandmarkDiagnostics(
                _leftDecoded,
                _rightDecoded,
                record);
        }

        if (record != null)
        {
            record.LeftDecodeStatus = left.DecodeStatus;
            record.RightDecodeStatus = right.DecodeStatus;
            record.LeftDecodeStatusName = left.DecodeStatusName;
            record.RightDecodeStatusName = right.DecodeStatusName;
            record.LeftRawPresence = left.RawPresence;
            record.RightRawPresence = right.RawPresence;
            record.LeftPresence = left.Presence;
            record.RightPresence = right.Presence;
            record.LeftPresenceMarginToThreshold = leftMargin;
            record.RightPresenceMarginToThreshold = rightMargin;
            record.MinimumAbsPresenceMargin = minimumAbsMargin;
            record.PresenceThresholdCrossed = thresholdCrossed;
            record.HardInvariantMask = hardMask;
            record.HardInvariantViolation = hardViolation;
            record.LeftAccepted = left.Accepted;
            record.RightAccepted = right.Accepted;
            record.LeftCanonicalValid = left.CanonicalValid;
            record.RightCanonicalValid = right.CanonicalValid;
            record.CanonicalComparable =
                left.Accepted &&
                right.Accepted &&
                left.CanonicalValid &&
                right.CanonicalValid;
            record.PresenceAbsDiff = presenceDiff;
        }
    }

    private void CompareCanonical(
        CanonicalSnapshot left,
        CanonicalSnapshot right,
        PairRecord record)
    {
        double pairPointMax = 0.0;

        AddPointMetric(
            left.FaceCenter,
            right.FaceCenter,
            ref pairPointMax);

        AddPointMetric(
            left.RightEyeCenter,
            right.RightEyeCenter,
            ref pairPointMax);

        AddPointMetric(
            left.LeftEyeCenter,
            right.LeftEyeCenter,
            ref pairPointMax);

        AddPointMetric(
            left.EyeCenter,
            right.EyeCenter,
            ref pairPointMax);

        AddPointMetric(
            left.Chin,
            right.Chin,
            ref pairPointMax);

        AddPointMetric(
            left.Nose,
            right.Nose,
            ref pairPointMax);

        AddPointMetric(
            left.CheekCenter,
            right.CheekCenter,
            ref pairPointMax);

        AddPointMetric(
            left.Forehead,
            right.Forehead,
            ref pairPointMax);

        double rotationDiff =
            Quaternion.Angle(
                NormalizeQuaternionSafe(
                    left.Rotation),
                NormalizeQuaternionSafe(
                    right.Rotation));

        _rotationDiffDegrees.Add(
            rotationDiff);

        double pairScaleMax = 0.0;

        AddScaleMetric(
            left.EyeSpan2D,
            right.EyeSpan2D,
            ref pairScaleMax);

        AddScaleMetric(
            left.EyeSpan3D,
            right.EyeSpan3D,
            ref pairScaleMax);

        AddScaleMetric(
            left.FaceWidth2D,
            right.FaceWidth2D,
            ref pairScaleMax);

        AddScaleMetric(
            left.FaceHeight2D,
            right.FaceHeight2D,
            ref pairScaleMax);

        double qualityDiff =
            Math.Abs(
                (double)left.GeometryQuality -
                right.GeometryQuality);

        _geometryQualityDiff.Add(
            qualityDiff);

        double pairExpressionMax = 0.0;

        AddExpressionMetric(
            left.Expression.eyeBlinkLeft,
            right.Expression.eyeBlinkLeft,
            ref pairExpressionMax);

        AddExpressionMetric(
            left.Expression.eyeBlinkRight,
            right.Expression.eyeBlinkRight,
            ref pairExpressionMax);

        AddExpressionMetric(
            left.Expression.eyeWideLeft,
            right.Expression.eyeWideLeft,
            ref pairExpressionMax);

        AddExpressionMetric(
            left.Expression.eyeWideRight,
            right.Expression.eyeWideRight,
            ref pairExpressionMax);

        AddExpressionMetric(
            left.Expression.jawOpen,
            right.Expression.jawOpen,
            ref pairExpressionMax);

        AddExpressionMetric(
            left.Expression.mouthSmileLeft,
            right.Expression.mouthSmileLeft,
            ref pairExpressionMax);

        AddExpressionMetric(
            left.Expression.mouthSmileRight,
            right.Expression.mouthSmileRight,
            ref pairExpressionMax);

        AddExpressionMetric(
            left.Expression.mouthPucker,
            right.Expression.mouthPucker,
            ref pairExpressionMax);

        AddExpressionMetric(
            left.Expression.mouthFunnel,
            right.Expression.mouthFunnel,
            ref pairExpressionMax);

        if (record != null)
        {
            record.CanonicalPointMaxPx =
                pairPointMax;

            record.RotationDiffDegrees =
                rotationDiff;

            record.ScaleRelativeMax =
                pairScaleMax;

            record.GeometryQualityAbsDiff =
                qualityDiff;

            record.ExpressionMaxAbsDiff =
                pairExpressionMax;
        }
    }

    private void CompareLandmarkDiagnostics(
        Vector3[] left,
        Vector3[] right,
        PairRecord record)
    {
        List<double> pair2d =
            new List<double>(
                CompatibleLandmarkCount);

        List<double> pairZ =
            new List<double>(
                CompatibleLandmarkCount);

        double pair2dMax = 0.0;
        double pairZMax = 0.0;

        for (
            int i = 0;
            i < CompatibleLandmarkCount;
            i++)
        {
            double dx =
                (
                    left[i].x -
                    right[i].x
                ) *
                _pendingSourceWidth;

            double dy =
                (
                    left[i].y -
                    right[i].y
                ) *
                _pendingSourceHeight;

            double d2 =
                Math.Sqrt(
                    dx * dx +
                    dy * dy);

            double dz =
                Math.Abs(
                    (double)left[i].z -
                    right[i].z);

            if (
                double.IsNaN(d2) ||
                double.IsInfinity(d2) ||
                double.IsNaN(dz) ||
                double.IsInfinity(dz))
            {
                _nonFiniteSemanticPairCount++;
                continue;
            }

            _landmark2dDiffPx.Add(d2);
            _landmarkZAbsDiff.Add(dz);
            pair2d.Add(d2);
            pairZ.Add(dz);

            pair2dMax =
                Math.Max(
                    pair2dMax,
                    d2);

            pairZMax =
                Math.Max(
                    pairZMax,
                    dz);
        }

        if (record != null)
        {
            Stats p2 =
                ComputeStats(
                    pair2d);

            Stats pz =
                ComputeStats(
                    pairZ);

            record.Landmark2dP95Px =
                p2.P95;

            record.Landmark2dMaxPx =
                pair2dMax;

            record.LandmarkZP95Abs =
                pz.P95;

            record.LandmarkZMaxAbs =
                pairZMax;
        }
    }

    private void AddPointMetric(
        Vector2 left,
        Vector2 right,
        ref double pairMax)
    {
        double dx =
            (
                left.x -
                right.x
            ) *
            _pendingSourceWidth;

        double dy =
            (
                left.y -
                right.y
            ) *
            _pendingSourceHeight;

        double value =
            Math.Sqrt(
                dx * dx +
                dy * dy);

        if (
            double.IsNaN(value) ||
            double.IsInfinity(value))
        {
            _nonFiniteSemanticPairCount++;
            return;
        }

        _canonicalPointDiffPx.Add(
            value);

        pairMax =
            Math.Max(
                pairMax,
                value);
    }

    private void AddScaleMetric(
        float left,
        float right,
        ref double pairMax)
    {
        double denominator =
            Math.Max(
                0.000001,
                Math.Max(
                    Math.Abs(left),
                    Math.Abs(right)));

        double value =
            Math.Abs(
                (double)left -
                right) /
            denominator;

        if (
            double.IsNaN(value) ||
            double.IsInfinity(value))
        {
            _nonFiniteSemanticPairCount++;
            return;
        }

        _scaleRelativeDiff.Add(
            value);

        pairMax =
            Math.Max(
                pairMax,
                value);
    }

    private void AddExpressionMetric(
        float left,
        float right,
        ref double pairMax)
    {
        double value =
            Math.Abs(
                (double)left -
                right);

        if (
            double.IsNaN(value) ||
            double.IsInfinity(value))
        {
            _nonFiniteSemanticPairCount++;
            return;
        }

        _expressionDiff.Add(
            value);

        pairMax =
            Math.Max(
                pairMax,
                value);
    }

    private void BeginMeasurement()
    {
        _measuring = true;
        _measurementStart =
            Time.realtimeSinceStartupAsDouble;
        _nextPairAt =
            _measurementStart;

        Debug.Log(
            "[Kiwi v44.55.20 CommonTensor] MEASURE_START " +
            "observerOnly=1" +
            " productionCommonTensor=1" +
            " cpuAuthority=0" +
            " observerGpuReadback=1" +
            " performanceAuthority=0" +
            " sourceIdentity=SAME_SEQUENCE_NATIVE_HOSTTICKS" +
            " nativeSamplingMatrix=EXACT_PRODUCTION_XFORM" +
            " decodeCropMatrix=EXACT_PENDING_PRODUCTION_LANE" +
            " minimumPresence=EXACT_PENDING_PRODUCTION_LANE" +
            " decode=PRODUCTION_DECODE_READ_ONLY" +
            " canonicalStatefulPresentationExcluded=1" +
            " minCompletedPairs=" +
            MinimumCompletedPairs +
            " minCanonicalPairs=" +
            MinimumCanonicalComparablePairs);
    }

    private void DiscoverRuntimeObjects(
        double now)
    {
        if (
            _reflectionReady &&
            RuntimeReflectionStillValid())
        {
            return;
        }

        if (now < _nextDiscoveryAt)
        {
            return;
        }

        _nextDiscoveryAt =
            now + 0.5;

        BindingFlags instanceFlags =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic;

        MonoBehaviour[] behaviours =
            FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

        for (
            int i = 0;
            i < behaviours.Length;
            i++)
        {
            MonoBehaviour behaviour =
                behaviours[i];

            if (
                behaviour == null ||
                behaviour.GetType().Name !=
                    "FaceLandmarkerRunner")
            {
                continue;
            }

            Type runnerType =
                behaviour.GetType();

            FieldInfo trackerField =
                runnerType.GetField(
                    "_sentisTracker",
                    instanceFlags);

            FieldInfo sequenceField =
                runnerType.GetField(
                    "_lastObservedFreshFrameSequence",
                    instanceFlags);

            FieldInfo managedHostTicksField =
                runnerType.GetField(
                    "_latestSentisSourceFrameHostTicks",
                    instanceFlags);

            MethodInfo expressionMethod =
                runnerType.GetMethod(
                    "ExtractGeometryExpressionData",
                    BindingFlags.Static |
                    BindingFlags.NonPublic);

            if (
                trackerField == null ||
                sequenceField == null ||
                managedHostTicksField == null ||
                expressionMethod == null)
            {
                continue;
            }

            object tracker =
                trackerField.GetValue(
                    behaviour);

            if (tracker == null)
            {
                continue;
            }

            Type trackerType =
                tracker.GetType();

            FieldInfo lanesField =
                trackerType.GetField(
                    "_lanes",
                    instanceFlags);

            FieldInfo sourceWidthField =
                trackerType.GetField(
                    "_sourceWidth",
                    instanceFlags);

            FieldInfo sourceHeightField =
                trackerType.GetField(
                    "_sourceHeight",
                    instanceFlags);

            MethodInfo decodeMethod =
                trackerType.GetMethod(
                    "DecodeReadableOutput",
                    BindingFlags.Instance |
                    BindingFlags.NonPublic);

            if (
                lanesField == null ||
                sourceWidthField == null ||
                sourceHeightField == null ||
                decodeMethod == null)
            {
                continue;
            }

            Array lanes =
                lanesField.GetValue(
                    tracker)
                as Array;

            if (
                lanes == null ||
                lanes.Length == 0)
            {
                continue;
            }

            object lane0 =
                lanes.GetValue(0);

            if (lane0 == null)
            {
                continue;
            }

            Type laneType =
                lane0.GetType();

            FieldInfo cropTextureField =
                laneType.GetField(
                    "cropTexture",
                    instanceFlags);

            FieldInfo cropMaterialField =
                laneType.GetField(
                    "cropMaterial",
                    instanceFlags);

            FieldInfo readbackPendingField =
                laneType.GetField(
                    "readbackPending",
                    instanceFlags);

            FieldInfo sourceHostTicksField =
                laneType.GetField(
                    "pendingSourceHostTicks",
                    instanceFlags);

            FieldInfo startedHostTicksField =
                laneType.GetField(
                    "pendingStartedHostTicks",
                    instanceFlags);

            FieldInfo cropMatrixField =
                laneType.GetField(
                    "pendingCropMatrix",
                    instanceFlags);

            FieldInfo pendingMinimumPresenceField =
                laneType.GetField(
                    "pendingMinimumPresence",
                    instanceFlags);

            if (
                cropTextureField == null ||
                cropMaterialField == null ||
                readbackPendingField == null ||
                sourceHostTicksField == null ||
                startedHostTicksField == null ||
                cropMatrixField == null ||
                pendingMinimumPresenceField == null)
            {
                continue;
            }

            _runner = behaviour;
            _tracker = tracker;
            _trackerType = trackerType;
            _lanes = lanes;
            _runnerTrackerField = trackerField;
            _runnerLastObservedFreshSequenceField =
                sequenceField;
            _runnerLatestSentisSourceHostTicksField =
                managedHostTicksField;
            _trackerLanesField = lanesField;
            _trackerSourceWidthField =
                sourceWidthField;
            _trackerSourceHeightField =
                sourceHeightField;
            _decodeReadableOutputMethod =
                decodeMethod;
            _extractGeometryExpressionDataMethod =
                expressionMethod;
            _laneCropTextureField =
                cropTextureField;
            _laneCropMaterialField =
                cropMaterialField;
            _laneReadbackPendingField =
                readbackPendingField;
            _lanePendingSourceHostTicksField =
                sourceHostTicksField;
            _lanePendingStartedHostTicksField =
                startedHostTicksField;
            _lanePendingCropMatrixField =
                cropMatrixField;
            _lanePendingMinimumPresenceField =
                pendingMinimumPresenceField;

            _reflectionReady = true;

            Debug.Log(
                "[Kiwi v44.55.20 CommonTensor] REFLECTION_READY " +
                "tracker=" +
                trackerType.FullName +
                " laneCount=" +
                lanes.Length +
                " cropTextureField=cropTexture" +
                " decodeMethod=DecodeReadableOutput" +
                " expressionMethod=ExtractGeometryExpressionData" +
                " writes=0");

            return;
        }
    }

    private bool RuntimeReflectionStillValid()
    {
        if (
            !_reflectionReady ||
            _runner == null ||
            _tracker == null ||
            _runnerTrackerField == null ||
            _trackerLanesField == null ||
            _laneCropTextureField == null ||
            _laneCropMaterialField == null ||
            _decodeReadableOutputMethod == null ||
            _extractGeometryExpressionDataMethod == null)
        {
            return false;
        }

        try
        {
            if (
                !ReferenceEquals(
                    _runnerTrackerField.GetValue(
                        _runner),
                    _tracker))
            {
                _reflectionReady = false;
                return false;
            }

            Array currentLanes =
                _trackerLanesField.GetValue(
                    _tracker)
                as Array;

            if (
                currentLanes == null ||
                !ReferenceEquals(
                    currentLanes,
                    _lanes))
            {
                _reflectionReady = false;
                return false;
            }

            return true;
        }
        catch
        {
            _reflectionReady = false;
            return false;
        }
    }

    private bool TrackerHasRegion()
    {
        if (
            _tracker == null ||
            _trackerType == null)
        {
            return false;
        }

        try
        {
            PropertyInfo property =
                _trackerType.GetProperty(
                    "HasRegion",
                    BindingFlags.Instance |
                    BindingFlags.Public);

            object value =
                property != null
                    ? property.GetValue(
                        _tracker)
                    : null;

            return
                value is bool flag &&
                flag;
        }
        catch
        {
            return false;
        }
    }

    private int ReadTrackerPrivateInt(
        FieldInfo field,
        int fallback)
    {
        if (
            _tracker == null ||
            field == null)
        {
            return fallback;
        }

        try
        {
            object value =
                field.GetValue(
                    _tracker);

            return
                value is int integer
                    ? integer
                    : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private ulong ReadRunnerULong(
        FieldInfo field)
    {
        if (
            _runner == null ||
            field == null)
        {
            return 0UL;
        }

        try
        {
            object value =
                field.GetValue(
                    _runner);

            return
                value is ulong sequence
                    ? sequence
                    : 0UL;
        }
        catch
        {
            return 0UL;
        }
    }

    private long ReadRunnerLong(
        FieldInfo field)
    {
        if (
            _runner == null ||
            field == null)
        {
            return 0L;
        }

        try
        {
            object value =
                field.GetValue(
                    _runner);

            return
                value is long ticks
                    ? ticks
                    : 0L;
        }
        catch
        {
            return 0L;
        }
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

    private void RegisterObserverFault(
        bool isWarmup,
        string reason)
    {
        _observerFaultCount++;
        _pairErrorCount++;
        _pairPending = false;
        _pendingRecord = null;
        _referenceOutputDone = false;
        _referenceInputDone = false;
        _leftDone = false;
        _rightDone = false;
        _commonScheduled = false;
        _referenceFrozenGpuDone = false;
        _referenceCpuDone = false;
        _aGpuDone = false;
        _bGpuDone = false;

        ResetSnapshotRequest();

        Debug.LogWarning(
            "[Kiwi v44.55.20 CommonTensor] OBSERVER_FAULT " +
            reason);

        if (isWarmup)
        {
            WriteReport(
                "WARMUP_FAIL");
        }
    }

    private void ResetSnapshotRequest()
    {
        _snapshotRequestActive = false;
        _snapshotSequence = 0UL;
        _snapshotNativeHostTicks = 0L;
        _snapshotManagedHostTicks = 0L;
    }

    private static void FlattenMatrix(
        Matrix4x4 matrix,
        float[] destination)
    {
        if (
            destination == null ||
            destination.Length != 16)
        {
            throw new ArgumentException(
                "Matrix destination must contain 16 floats.",
                nameof(destination));
        }

        destination[0] = matrix.m00;
        destination[1] = matrix.m01;
        destination[2] = matrix.m02;
        destination[3] = matrix.m03;
        destination[4] = matrix.m10;
        destination[5] = matrix.m11;
        destination[6] = matrix.m12;
        destination[7] = matrix.m13;
        destination[8] = matrix.m20;
        destination[9] = matrix.m21;
        destination[10] = matrix.m22;
        destination[11] = matrix.m23;
        destination[12] = matrix.m30;
        destination[13] = matrix.m31;
        destination[14] = matrix.m32;
        destination[15] = matrix.m33;
    }

    private string DetermineDecision(
        string status)
    {
        if (
            !string.Equals(
                status,
                "COMPLETE",
                StringComparison.Ordinal))
        {
            bool observerInvalid =
                _observerFaultCount > 0 ||
                _sourceIdentityMismatchCount > 0 ||
                _decodeReflectionFailureCount > 0 ||
                _expressionReflectionFailureCount > 0 ||
                _referenceFreezeFailureCount > 0 ||
                _referenceInputReadbackFailureCount > 0 ||
                status.StartsWith(
                    "OBSERVER_FAULT",
                    StringComparison.Ordinal) ||
                string.Equals(
                    status,
                    "INIT_FAIL",
                    StringComparison.Ordinal);

            return
                observerInvalid
                    ? "INVALID_OBSERVER"
                    : "INSUFFICIENT_DATA";
        }

        if (
            _observerFaultCount > 0 ||
            _sourceIdentityMismatchCount > 0 ||
            _decodeReflectionFailureCount > 0 ||
            _expressionReflectionFailureCount > 0 ||
            _referenceFreezeFailureCount > 0 ||
            _referenceInputReadbackFailureCount > 0 ||
            _referenceTensorNonFiniteCount > 0 ||
            _referenceVsCurrentInput.PairCount != _pairCompletedCount ||
            _referenceVsMode2Input.PairCount != _pairCompletedCount ||
            _referenceVsASpatial.PairCount != _pairCompletedCount ||
            _referenceVsBSpatial.PairCount != _pairCompletedCount ||
            _referenceVsASpatial.NonFiniteCount > 0 ||
            _referenceVsBSpatial.NonFiniteCount > 0 ||
            DetermineTransitOutcome() != "PASS")
        {
            return "INVALID_OBSERVER";
        }

        ComparisonMetrics[] required =
        {
            _backendReference,
            _backendA,
            _backendB,
            _referenceVsCurrent,
            _referenceVsMode2,
            _preprocessCpuA,
            _preprocessCpuB
        };

        for (int i = 0; i < required.Length; i++)
        {
            if (DetermineCandidateOutcome(required[i]) == "INSUFFICIENT_DATA")
            {
                return "INSUFFICIENT_DATA";
            }
        }

        bool backendPass =
            DetermineCandidateOutcome(_backendReference) == "PASS" &&
            DetermineCandidateOutcome(_backendA) == "PASS" &&
            DetermineCandidateOutcome(_backendB) == "PASS";

        bool preprocessGpuAPass =
            DetermineCandidateOutcome(_referenceVsCurrent) == "PASS";
        bool preprocessGpuBPass =
            DetermineCandidateOutcome(_referenceVsMode2) == "PASS";
        bool preprocessCpuAPass =
            DetermineCandidateOutcome(_preprocessCpuA) == "PASS";
        bool preprocessCpuBPass =
            DetermineCandidateOutcome(_preprocessCpuB) == "PASS";
        bool preprocessingPass =
            preprocessGpuAPass &&
            preprocessGpuBPass &&
            preprocessCpuAPass &&
            preprocessCpuBPass;

        if (
            preprocessGpuBPass &&
            !preprocessCpuBPass &&
            DetermineCandidateOutcome(_backendB) != "PASS" &&
            DetermineCandidateOutcome(_backendReference) == "PASS" &&
            DetermineCandidateOutcome(_backendA) == "PASS" &&
            preprocessGpuAPass &&
            preprocessCpuAPass)
        {
            return "MODE2_GPU_PASS";
        }

        if (backendPass && !preprocessingPass)
        {
            return "PREPROCESSING_CONFIRMED";
        }

        if (!backendPass && preprocessingPass)
        {
            return "BACKEND_CONFIRMED";
        }

        if (!backendPass && !preprocessingPass)
        {
            return "BOTH_CONTRIBUTE";
        }

        // The requested decision taxonomy has no "all comparisons pass"
        // outcome. Treat failure to reproduce either stage as invalid rather
        // than inventing a seventh conclusion after runtime.
        return "INVALID_OBSERVER";
    }

    private string DetermineTransitOutcome()
    {
        if (
            _transitValidationCount != TransitValidationSamples ||
            _transitDirectVsFrozen.CanonicalComparableCount <= 0)
        {
            return "INSUFFICIENT_DATA";
        }

        if (
            _transitDirectVsFrozen.HardInvariantViolationCount > 0 ||
            _transitDirectVsFrozen.DecodeStatusMismatchCount > 0 ||
            _transitDirectVsFrozen.AcceptanceMismatchCount > 0 ||
            _transitDirectVsFrozen.CanonicalValidityMismatchCount > 0 ||
            _transitDirectVsFrozen.NonFiniteCount > 0)
        {
            return "HARD_FAIL";
        }

        Stats presence = ComputeStats(_transitDirectVsFrozen.Presence);
        Stats points = ComputeStats(_transitDirectVsFrozen.CanonicalPoints);
        Stats rotation = ComputeStats(_transitDirectVsFrozen.Rotation);
        Stats scale = ComputeStats(_transitDirectVsFrozen.Scale);
        Stats quality = ComputeStats(_transitDirectVsFrozen.GeometryQuality);
        Stats expression = ComputeStats(_transitDirectVsFrozen.Expression);

        bool pass =
            presence.P95 <= PresenceP95Gate &&
            presence.Max <= PresenceMaxGate &&
            points.P95 <= CanonicalPointP95PxGate &&
            points.Max <= CanonicalPointMaxPxGate &&
            rotation.P95 <= RotationP95DegreesGate &&
            rotation.Max <= RotationMaxDegreesGate &&
            scale.P95 <= ScaleRelativeP95Gate &&
            scale.Max <= ScaleRelativeMaxGate &&
            quality.P95 <= GeometryQualityP95Gate &&
            quality.Max <= GeometryQualityMaxGate &&
            expression.P95 <= ExpressionP95Gate &&
            expression.Max <= ExpressionMaxGate;

        return pass ? "PASS" : "AGGREGATE_FAIL";
    }

    private string DetermineCandidateOutcome(
        ComparisonMetrics metrics)
    {
        if (
            metrics.HardInvariantViolationCount > 0 ||
            metrics.DecodeStatusMismatchCount > 0 ||
            metrics.AcceptanceMismatchCount > 0 ||
            metrics.CanonicalValidityMismatchCount > 0 ||
            metrics.NonFiniteCount > 0)
        {
            return "HARD_FAIL";
        }

        if (
            _pairCompletedCount < MinimumCompletedPairs ||
            metrics.CanonicalComparableCount < MinimumCanonicalComparablePairs)
        {
            return "INSUFFICIENT_DATA";
        }

        Stats presence = ComputeStats(metrics.Presence);
        Stats points = ComputeStats(metrics.CanonicalPoints);
        Stats rotation = ComputeStats(metrics.Rotation);
        Stats scale = ComputeStats(metrics.Scale);
        Stats quality = ComputeStats(metrics.GeometryQuality);
        Stats expression = ComputeStats(metrics.Expression);

        bool pass =
            presence.P95 <= PresenceP95Gate &&
            presence.Max <= PresenceMaxGate &&
            points.P95 <= CanonicalPointP95PxGate &&
            points.Max <= CanonicalPointMaxPxGate &&
            rotation.P95 <= RotationP95DegreesGate &&
            rotation.Max <= RotationMaxDegreesGate &&
            scale.P95 <= ScaleRelativeP95Gate &&
            scale.Max <= ScaleRelativeMaxGate &&
            quality.P95 <= GeometryQualityP95Gate &&
            quality.Max <= GeometryQualityMaxGate &&
            expression.P95 <= ExpressionP95Gate &&
            expression.Max <= ExpressionMaxGate;

        return pass ? "PASS" : "AGGREGATE_FAIL";
    }

    private void WriteArmedProof()
    {
        try
        {
            string directory =
                GetReportDirectory();

            string path =
                Path.Combine(
                    directory,
                    "KiwiProductionCommonTensorTriangulation_v44_55_20_ARMED.txt");

            string[] lines =
            {
                "KiwiAvatarSystem v44.55.20 Common-Tensor Backend + Spatial Outlier Stage Isolation Gate",
                "contract=" + Contract,
                "state=ARMED",
                "generated=" +
                    DateTime.Now.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                "observerOnly=1",
                "productionAuthority=GPU_UNCHANGED",
                "nativeDllChange=0",
                "trackerRunnerChange=0",
                "observerGpuReadback=1",
                "performanceAuthority=0",
                "reference=PRODUCTION_COMPATIBLE_TEXTURECONVERTER_FROM_MATCHED_PRODUCTION_CROP_TO_FROZEN_FLOAT32_NCHW",
                "commonTensorLanes=REF_GPU,REF_CPU,A_GPU,A_CPU,B_GPU,B_CPU",
                "transitValidation=FIRST_10_REF_DIRECT_GPU_VS_REF_FROZEN_GPU",
                "durationSeconds=" +
                    _durationSeconds.ToString(
                        "F1",
                        CultureInfo.InvariantCulture),
                "sampleHz=" +
                    _sampleHz.ToString(
                        "F2",
                        CultureInfo.InvariantCulture),
                "minimumCompletedPairs=" +
                    MinimumCompletedPairs,
                "minimumCanonicalComparablePairs=" +
                    MinimumCanonicalComparablePairs,
                "thresholdsLockedBeforeRuntime=1"
            };

            File.WriteAllLines(
                path,
                lines);

            Debug.Log(
                "[Kiwi v44.55.20 CommonTensor] ARMED_PROOF path=" +
                path);
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "[Kiwi v44.55.20 CommonTensor] ARMED_PROOF_FAIL " +
                exception.GetType().Name +
                " " +
                exception.Message);
        }
    }

    private void WriteReport(
        string status)
    {
        if (_reportWritten)
        {
            return;
        }

        _reportWritten = true;

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
                "KiwiCommonTensorBackendStageIsolation_v44_55_20_" +
                stamp +
                ".txt");

        string csvPath =
            Path.Combine(
                directory,
                "KiwiCommonTensorBackendStageIsolation_v44_55_20_" +
                stamp +
                ".csv");

        string topOutlierPath =
            Path.Combine(
                directory,
                "KiwiCommonTensorSpatialTop32_v44_55_20_" +
                stamp +
                ".csv");

        string heatmapPath =
            Path.Combine(
                directory,
                "KiwiCommonTensorSpatialHeatmap_v44_55_20_" +
                stamp +
                ".csv");

        List<string> lines =
            new List<string>();

        lines.Add(
            "KiwiAvatarSystem v44.55.20 Common-Tensor Backend + Spatial Outlier Stage Isolation Gate");
        lines.Add("contract=" + Contract);
        lines.Add("status=" + status);
        lines.Add("decision=" + decision);
        lines.Add("observerOnly=1");
        lines.Add("productionTrackerWrites=0");
        lines.Add("productionWorkerWrites=0");
        lines.Add("productionBackendChange=0");
        lines.Add("productionRoiWrites=0");
        lines.Add("productionFaceTextureWrites=0");
        lines.Add("productionRootHeadWrites=0");
        lines.Add("productionCameraChange=0");
        lines.Add("nativeDllChange=0");
        lines.Add("trackingMathChange=0");
        lines.Add("observerGpuReadback=1");
        lines.Add("referenceInput=INDEPENDENT_TEXTURECONVERTER_FROM_MATCHED_PRODUCTION_CROP_TEXTURE");
        lines.Add("lanes=REF_DIRECT_GPU,REF_FROZEN_GPU,REF_CPU,A_GPU,A_CPU,B_GPU,B_CPU");
        lines.Add("commonTensorBackendFactorial=1");
        lines.Add("textureConverterNotRepeatedAfterFreeze=1");
        lines.Add("referenceFreeze=OBSERVER_OWNED_ARGB32_LINEAR_RT_COPYTEXTURE_BEFORE_CPU_CANDIDATES");
        lines.Add("referenceTensorLayout=NCHW");
        lines.Add("referenceCoordOrigin=TopLeft");
        lines.Add("leftInput=CURRENT_FLOAT_EXACT_SNAPSHOT");
        lines.Add("rightInput=PRESENTATION_PLUS_FINAL_UNORM8_MODE2_EXACT_SNAPSHOT");
        lines.Add("gpuInputsPinned=1");
        lines.Add("sourceIdentity=SAME_NATIVE_SEQUENCE_AND_HOSTTICKS");
        lines.Add("nativeSamplingMatrixAuthority=EXACT_PRODUCTION_LANE_CROP_MATERIAL_XFORM");
        lines.Add("decodeCropMatrixAuthority=EXACT_PRODUCTION_LANE_pendingCropMatrix");
        lines.Add("decodeMinimumPresenceAuthority=EXACT_PRODUCTION_LANE_pendingMinimumPresence");
        lines.Add("decodeAuthority=PRODUCTION_KiwiInferenceFaceTracker.DecodeReadableOutput");
        lines.Add("expressionAuthority=PRODUCTION_FaceLandmarkerRunner.ExtractGeometryExpressionData");
        lines.Add("canonicalGeometryAuthority=RUNNER_SENTIS_STATELESS_FORMULAS_FOR_BASELINE_HASH");
        lines.Add("statefulGeometryQualityCoherenceFallbackExcluded=1");
        lines.Add("providerOffsetExcluded=1");
        lines.Add("neutralCalibrationExcluded=1");
        lines.Add("dropoutContinuityExcluded=1");
        lines.Add("predictionExcluded=1");
        lines.Add("temporalPresentationExcluded=1");
        lines.Add("finalRootHeadTransformHardGate=0");
        lines.Add("rawLandmarkMetricsDiagnosticOnly=1");
        lines.Add("performanceAuthority=0");
        lines.Add("thresholdsLockedBeforeRuntime=1");
        lines.Add("decisionPrecedence=INVALID_OBSERVER>HARD_FAIL>INSUFFICIENT_DATA>AGGREGATE_GATE");
        lines.Add("hardInvariantPrecedesSampleCompleteness=1");
        lines.Add("transitOutcome=" + DetermineTransitOutcome());
        lines.Add("backendReferenceOutcome=" + DetermineCandidateOutcome(_backendReference));
        lines.Add("backendAOutcome=" + DetermineCandidateOutcome(_backendA));
        lines.Add("backendBOutcome=" + DetermineCandidateOutcome(_backendB));
        lines.Add("preprocessGpuAOutcome=" + DetermineCandidateOutcome(_referenceVsCurrent));
        lines.Add("preprocessGpuBOutcome=" + DetermineCandidateOutcome(_referenceVsMode2));
        lines.Add("preprocessCpuAOutcome=" + DetermineCandidateOutcome(_preprocessCpuA));
        lines.Add("preprocessCpuBOutcome=" + DetermineCandidateOutcome(_preprocessCpuB));
        lines.Add("");

        lines.Add("[COUNTS]");
        lines.Add("pairAttemptCount=" + _pairAttemptCount);
        lines.Add("pairCompletedCount=" + _pairCompletedCount);
        lines.Add("canonicalComparablePairCount=" + _canonicalComparablePairCount);
        lines.Add("referenceVsCurrentCanonicalComparable=" + _referenceVsCurrent.CanonicalComparableCount);
        lines.Add("referenceVsMode2CanonicalComparable=" + _referenceVsMode2.CanonicalComparableCount);
        lines.Add("pairErrorCount=" + _pairErrorCount);
        lines.Add("observerFaultCount=" + _observerFaultCount);
        lines.Add("sourceIdentityMismatchCount=" + _sourceIdentityMismatchCount);
        lines.Add("decodeStatusMismatchCount=" + _decodeStatusMismatchCount);
        lines.Add("acceptanceMismatchCount=" + _acceptanceMismatchCount);
        lines.Add("canonicalValidityMismatchCount=" + _canonicalValidityMismatchCount);
        lines.Add("nonFiniteSemanticPairCount=" + _nonFiniteSemanticPairCount);
        lines.Add("decodeReflectionFailureCount=" + _decodeReflectionFailureCount);
        lines.Add("expressionReflectionFailureCount=" + _expressionReflectionFailureCount);
        lines.Add("laneChangedAfterCaptureCount=" + _laneChangedAfterCaptureCount);
        lines.Add("hardInvariantViolationPairCount=" + _hardInvariantViolationPairCount);
        lines.Add("presenceThresholdCrossingPairCount=" + _presenceThresholdCrossingPairCount);
        lines.Add("referenceFreezeFailureCount=" + _referenceFreezeFailureCount);
        lines.Add("referenceInputReadbackFailureCount=" + _referenceInputReadbackFailureCount);
        lines.Add("referenceTensorNonFiniteCount=" + _referenceTensorNonFiniteCount);
        lines.Add("transitValidationCount=" + _transitValidationCount);
        lines.Add("spatialReferenceVsAPairCount=" + _referenceVsASpatial.PairCount);
        lines.Add("spatialReferenceVsBPairCount=" + _referenceVsBSpatial.PairCount);
        lines.Add("snapshotArmCount=" + _snapshotArmCount);
        lines.Add("snapshotReadyCount=" + _snapshotReadyCount);
        lines.Add("snapshotMatchTimeoutCount=" + _snapshotMatchTimeoutCount);
        lines.Add("snapshotArmFailureCount=" + _snapshotArmFailureCount);
        lines.Add("snapshotSequenceSkippedCount=" + _snapshotSequenceSkippedCount);
        lines.Add("snapshotManagedTimestampMapFailureCount=" + _snapshotManagedTimestampMapFailureCount);
        lines.Add("snapshotPresentedButNotScheduledCount=" + _snapshotPresentedButNotScheduledCount);
        lines.Add("");

        lines.Add("[FIXED_GATES]");
        lines.Add("minimumCompletedPairs=" + MinimumCompletedPairs);
        lines.Add("minimumCanonicalComparablePairs=" + MinimumCanonicalComparablePairs);
        lines.Add("presenceP95Gate=" + F(PresenceP95Gate));
        lines.Add("presenceMaxGate=" + F(PresenceMaxGate));
        lines.Add("canonicalPointP95PxGate=" + F(CanonicalPointP95PxGate));
        lines.Add("canonicalPointMaxPxGate=" + F(CanonicalPointMaxPxGate));
        lines.Add("rotationP95DegreesGate=" + F(RotationP95DegreesGate));
        lines.Add("rotationMaxDegreesGate=" + F(RotationMaxDegreesGate));
        lines.Add("scaleRelativeP95Gate=" + F(ScaleRelativeP95Gate));
        lines.Add("scaleRelativeMaxGate=" + F(ScaleRelativeMaxGate));
        lines.Add("geometryQualityP95Gate=" + F(GeometryQualityP95Gate));
        lines.Add("geometryQualityMaxGate=" + F(GeometryQualityMaxGate));
        lines.Add("expressionP95Gate=" + F(ExpressionP95Gate));
        lines.Add("expressionMaxGate=" + F(ExpressionMaxGate));
        lines.Add("");

        lines.Add("[REF_TRANSIT_VALIDATION]");
        AppendComparison(lines, _transitDirectVsFrozen, DetermineTransitOutcome());
        lines.Add("");

        lines.Add("[BACKEND_ONLY_REF_GPU_VS_REF_CPU]");
        AppendComparison(lines, _backendReference);
        lines.Add("");

        lines.Add("[BACKEND_ONLY_A_GPU_VS_A_CPU]");
        AppendComparison(lines, _backendA);
        lines.Add("");

        lines.Add("[BACKEND_ONLY_B_GPU_VS_B_CPU]");
        AppendComparison(lines, _backendB);
        lines.Add("");

        lines.Add("[PREPROCESSING_GPU_REF_VS_CURRENT_FLOAT]");
        AppendComparison(lines, _referenceVsCurrent);
        lines.Add("");

        lines.Add("[PREPROCESSING_GPU_REF_VS_MODE2]");
        AppendComparison(lines, _referenceVsMode2);
        lines.Add("");

        lines.Add("[PREPROCESSING_CPU_REF_VS_CURRENT_FLOAT]");
        AppendComparison(lines, _preprocessCpuA);
        lines.Add("");

        lines.Add("[PREPROCESSING_CPU_REF_VS_MODE2]");
        AppendComparison(lines, _preprocessCpuB);
        lines.Add("");

        lines.Add("[INPUT_PARITY_DIAGNOSTIC_ONLY]");
        AppendInputParity(lines, _referenceVsCurrentInput);
        AppendInputParity(lines, _referenceVsMode2Input);
        AppendInputParity(lines, _currentVsMode2Input);
        lines.Add("Input parity cannot produce semantic PASS.");
        lines.Add("");

        lines.Add("[SPATIAL_OUTLIERS_REFERENCE_VS_CURRENT_FLOAT]");
        AppendSpatial(lines, _referenceVsASpatial);
        lines.Add("");

        lines.Add("[SPATIAL_OUTLIERS_REFERENCE_VS_MODE2]");
        AppendSpatial(lines, _referenceVsBSpatial);
        lines.Add("");

        lines.Add("[CURRENT_FLOAT_VS_MODE2_DIAGNOSTIC_ONLY]");
        AppendStats(lines, "presenceAbsDiff", _presenceDiff);
        AppendStats(lines, "canonicalPointDiffPx", _canonicalPointDiffPx);
        AppendStats(lines, "rotationDiffDegrees", _rotationDiffDegrees);
        AppendStats(lines, "scaleRelativeDiff", _scaleRelativeDiff);
        AppendStats(lines, "geometryQualityAbsDiff", _geometryQualityDiff);
        AppendStats(lines, "expressionAbsDiff", _expressionDiff);
        lines.Add("");

        lines.Add("[HARD_INVARIANT_DETAILS]");
        lines.Add("format=index|sequence|nativeHostTicks|managedHostTicks|minimumPresence|leftRawPresence|rightRawPresence|leftPresence|rightPresence|leftMargin|rightMargin|minAbsMargin|thresholdCrossed|leftDecode|rightDecode|leftAccepted|rightAccepted|leftCanonical|rightCanonical|hardMask");
        ComparisonMetrics[] allComparisons =
        {
            _transitDirectVsFrozen,
            _backendReference,
            _backendA,
            _backendB,
            _referenceVsCurrent,
            _referenceVsMode2,
            _preprocessCpuA,
            _preprocessCpuB
        };

        bool anyComparisonHardDetail = false;
        for (int i = 0; i < allComparisons.Length; i++)
        {
            anyComparisonHardDetail |= allComparisons[i].HardDetails.Count > 0;
        }

        if (_hardInvariantDetails.Count == 0 && !anyComparisonHardDetail)
        {
            lines.Add("none");
        }
        else
        {
            for (int i = 0; i < _hardInvariantDetails.Count; i++)
            {
                lines.Add(_hardInvariantDetails[i]);
            }

            for (int comparisonIndex = 0;
                 comparisonIndex < allComparisons.Length;
                 comparisonIndex++)
            {
                for (int i = 0;
                     i < allComparisons[comparisonIndex].HardDetails.Count;
                     i++)
                {
                    lines.Add(allComparisons[comparisonIndex].HardDetails[i]);
                }
            }
        }

        lines.Add("");
        lines.Add("[DIAGNOSTIC_ONLY]");
        AppendStats(lines, "landmark2dDiffPx", _landmark2dDiffPx);
        AppendStats(lines, "landmarkZAbsDiff", _landmarkZAbsDiff);
        AppendStats(lines, "leftNativeCropCpuMs", _leftNativeCropCpuMs);
        AppendStats(lines, "rightNativeCropCpuMs", _rightNativeCropCpuMs);
        AppendStats(lines, "nativePairWallMs", _nativePairWallMs);
        AppendStats(lines, "leftCpuServiceMs", _leftCpuServiceMs);
        AppendStats(lines, "rightCpuServiceMs", _rightCpuServiceMs);
        AppendIntStats(lines, "pairCompletionFrames", _pairCompletionFrames);
        lines.Add("");

        lines.Add("[DECISION_RULE]");
        lines.Add("Precedence is strict: INVALID_OBSERVER > HARD FAIL > INSUFFICIENT_DATA > AGGREGATE GATE.");
        lines.Add("INVALID_OBSERVER if observer/source identity/reflection contract fails.");
        lines.Add("HARD FAIL if decode status, accepted/rejected, canonical validity, or non-finite mismatch exists; sample completeness cannot mask it.");
        lines.Add("INSUFFICIENT_DATA only when no hard invariant failed and completed<60 or canonicalComparable<40.");
        lines.Add("Each candidate passes only if no hard invariant failed, sample completeness passes, and every fixed canonical aggregate gate passes against REFERENCE.");
        lines.Add("same-tensor backend PASS plus cross-tensor FAIL => PREPROCESSING_CONFIRMED.");
        lines.Add("same-tensor backend FAIL plus cross-tensor PASS => BACKEND_CONFIRMED.");
        lines.Add("both stage groups FAIL => BOTH_CONTRIBUTE.");
        lines.Add("REF_GPU vs B_GPU PASS with B_CPU-only failure and every non-B-CPU comparison PASS => MODE2_GPU_PASS.");
        lines.Add("Raw 478-landmark/bbox/centroid style proxy metrics are diagnostic-only and cannot fail the candidate alone.");
        lines.Add("Do not change thresholds after observing this run.");

        File.WriteAllLines(
            textPath,
            lines);

        WriteCsv(
            csvPath);

        WriteSpatialTopCsv(
            topOutlierPath);

        WriteSpatialHeatmapCsv(
            heatmapPath);

        Debug.Log(
            "[Kiwi v44.55.20 CommonTensor] " +
            status +
            " decision=" +
            decision +
            " report=" +
            textPath +
            " csv=" +
            csvPath +
            " topOutliers=" +
            topOutlierPath +
            " heatmap=" +
            heatmapPath);
    }

    private void WriteCsv(
        string path)
    {
        List<string> lines =
            new List<string>();

        lines.Add(
            "index,sequence,nativeHostTicks,managedHostTicks,laneStartedHostTicks," +
            "sourceWidth,sourceHeight,minimumPresence,laneStillMatchedAfterCapture," +
            "leftDecodeStatus,rightDecodeStatus,leftDecodeStatusName,rightDecodeStatusName," +
            "leftRawPresence,rightRawPresence,leftPresence,rightPresence," +
            "leftPresenceMarginToThreshold,rightPresenceMarginToThreshold,minimumAbsPresenceMargin," +
            "presenceThresholdCrossed,hardInvariantMask,hardInvariantViolation," +
            "leftAccepted,rightAccepted,leftCanonicalValid,rightCanonicalValid," +
            "canonicalComparable,presenceAbsDiff,canonicalPointMaxPx,rotationDiffDegrees," +
            "scaleRelativeMax,geometryQualityAbsDiff,expressionMaxAbsDiff," +
            "landmark2dP95Px,landmark2dMaxPx,landmarkZP95Abs,landmarkZMaxAbs," +
            "leftNativeCpuMs,rightNativeCpuMs,nativePairWallMs,completionFrames," +
            "referenceDecodeStatus,referenceDecodeStatusName,referenceRawPresence,referencePresence," +
            "referenceAccepted,referenceCanonicalValid," +
            "referenceVsCurrentComparable,referenceVsCurrentHardMask,referenceVsCurrentPresenceAbsDiff," +
            "referenceVsCurrentCanonicalPointMaxPx,referenceVsCurrentRotationDiffDegrees," +
            "referenceVsCurrentScaleRelativeMax,referenceVsCurrentGeometryQualityAbsDiff," +
            "referenceVsCurrentExpressionMaxAbsDiff,referenceVsCurrentLandmark2dP95Px," +
            "referenceVsCurrentLandmark2dMaxPx,referenceVsCurrentLandmarkZP95Abs,referenceVsCurrentLandmarkZMaxAbs," +
            "referenceVsMode2Comparable,referenceVsMode2HardMask,referenceVsMode2PresenceAbsDiff," +
            "referenceVsMode2CanonicalPointMaxPx,referenceVsMode2RotationDiffDegrees," +
            "referenceVsMode2ScaleRelativeMax,referenceVsMode2GeometryQualityAbsDiff," +
            "referenceVsMode2ExpressionMaxAbsDiff,referenceVsMode2Landmark2dP95Px," +
            "referenceVsMode2Landmark2dMaxPx,referenceVsMode2LandmarkZP95Abs,referenceVsMode2LandmarkZMaxAbs," +
            "referenceVsCurrentInputMeanAbsLsb,referenceVsMode2InputMeanAbsLsb,currentVsMode2InputMeanAbsLsb," +
            ComparisonCsvHeader("transitDirectVsFrozen") + "," +
            ComparisonCsvHeader("backendReference") + "," +
            ComparisonCsvHeader("backendA") + "," +
            ComparisonCsvHeader("backendB") + "," +
            ComparisonCsvHeader("preprocessGpuA") + "," +
            ComparisonCsvHeader("preprocessGpuB") + "," +
            ComparisonCsvHeader("preprocessCpuA") + "," +
            ComparisonCsvHeader("preprocessCpuB") + ",transitRequired");

        for (
            int i = 0;
            i < _records.Count;
            i++)
        {
            PairRecord r =
                _records[i];

            lines.Add(
                r.Index + "," +
                r.Sequence + "," +
                r.NativeHostTicks + "," +
                r.ManagedHostTicks + "," +
                r.LaneStartedHostTicks + "," +
                r.SourceWidth + "," +
                r.SourceHeight + "," +
                r.MinimumPresence.ToString("F9", CultureInfo.InvariantCulture) + "," +
                B(r.LaneStillMatchedAfterCapture) + "," +
                r.LeftDecodeStatus + "," +
                r.RightDecodeStatus + "," +
                Csv(r.LeftDecodeStatusName) + "," +
                Csv(r.RightDecodeStatusName) + "," +
                r.LeftRawPresence.ToString("F9", CultureInfo.InvariantCulture) + "," +
                r.RightRawPresence.ToString("F9", CultureInfo.InvariantCulture) + "," +
                r.LeftPresence.ToString("F9", CultureInfo.InvariantCulture) + "," +
                r.RightPresence.ToString("F9", CultureInfo.InvariantCulture) + "," +
                F(r.LeftPresenceMarginToThreshold) + "," +
                F(r.RightPresenceMarginToThreshold) + "," +
                F(r.MinimumAbsPresenceMargin) + "," +
                B(r.PresenceThresholdCrossed) + "," +
                r.HardInvariantMask + "," +
                B(r.HardInvariantViolation) + "," +
                B(r.LeftAccepted) + "," +
                B(r.RightAccepted) + "," +
                B(r.LeftCanonicalValid) + "," +
                B(r.RightCanonicalValid) + "," +
                B(r.CanonicalComparable) + "," +
                F(r.PresenceAbsDiff) + "," +
                F(r.CanonicalPointMaxPx) + "," +
                F(r.RotationDiffDegrees) + "," +
                F(r.ScaleRelativeMax) + "," +
                F(r.GeometryQualityAbsDiff) + "," +
                F(r.ExpressionMaxAbsDiff) + "," +
                F(r.Landmark2dP95Px) + "," +
                F(r.Landmark2dMaxPx) + "," +
                F(r.LandmarkZP95Abs) + "," +
                F(r.LandmarkZMaxAbs) + "," +
                F(r.LeftNativeCpuMs) + "," +
                F(r.RightNativeCpuMs) + "," +
                F(r.NativePairWallMs) + "," +
                r.CompletionFrames + "," +
                r.ReferenceDecodeStatus + "," +
                Csv(r.ReferenceDecodeStatusName) + "," +
                F(r.ReferenceRawPresence) + "," +
                F(r.ReferencePresence) + "," +
                B(r.ReferenceAccepted) + "," +
                B(r.ReferenceCanonicalValid) + "," +
                B(r.ReferenceVsCurrent.Comparable) + "," +
                r.ReferenceVsCurrent.HardMask + "," +
                F(r.ReferenceVsCurrent.PresenceAbsDiff) + "," +
                F(r.ReferenceVsCurrent.CanonicalPointMaxPx) + "," +
                F(r.ReferenceVsCurrent.RotationDiffDegrees) + "," +
                F(r.ReferenceVsCurrent.ScaleRelativeMax) + "," +
                F(r.ReferenceVsCurrent.GeometryQualityAbsDiff) + "," +
                F(r.ReferenceVsCurrent.ExpressionMaxAbsDiff) + "," +
                F(r.ReferenceVsCurrent.Landmark2dP95Px) + "," +
                F(r.ReferenceVsCurrent.Landmark2dMaxPx) + "," +
                F(r.ReferenceVsCurrent.LandmarkZP95Abs) + "," +
                F(r.ReferenceVsCurrent.LandmarkZMaxAbs) + "," +
                B(r.ReferenceVsMode2.Comparable) + "," +
                r.ReferenceVsMode2.HardMask + "," +
                F(r.ReferenceVsMode2.PresenceAbsDiff) + "," +
                F(r.ReferenceVsMode2.CanonicalPointMaxPx) + "," +
                F(r.ReferenceVsMode2.RotationDiffDegrees) + "," +
                F(r.ReferenceVsMode2.ScaleRelativeMax) + "," +
                F(r.ReferenceVsMode2.GeometryQualityAbsDiff) + "," +
                F(r.ReferenceVsMode2.ExpressionMaxAbsDiff) + "," +
                F(r.ReferenceVsMode2.Landmark2dP95Px) + "," +
                F(r.ReferenceVsMode2.Landmark2dMaxPx) + "," +
                F(r.ReferenceVsMode2.LandmarkZP95Abs) + "," +
                F(r.ReferenceVsMode2.LandmarkZMaxAbs) + "," +
                F(r.ReferenceVsCurrentInputMeanAbsLsb) + "," +
                F(r.ReferenceVsMode2InputMeanAbsLsb) + "," +
                F(r.CurrentVsMode2InputMeanAbsLsb) + "," +
                ComparisonCsv(r.TransitDirectVsFrozen) + "," +
                ComparisonCsv(r.BackendReference) + "," +
                ComparisonCsv(r.BackendA) + "," +
                ComparisonCsv(r.BackendB) + "," +
                ComparisonCsv(r.PreprocessGpuA) + "," +
                ComparisonCsv(r.PreprocessGpuB) + "," +
                ComparisonCsv(r.PreprocessCpuA) + "," +
                ComparisonCsv(r.PreprocessCpuB) + "," +
                B(r.TransitRequired));
        }

        File.WriteAllLines(
            path,
            lines);
    }

    private static string ComparisonCsvHeader(string prefix)
    {
        return
            prefix + "Comparable," +
            prefix + "HardMask," +
            prefix + "PresenceAbsDiff," +
            prefix + "CanonicalPointMaxPx," +
            prefix + "RotationDiffDegrees," +
            prefix + "ScaleRelativeMax," +
            prefix + "GeometryQualityAbsDiff," +
            prefix + "ExpressionMaxAbsDiff";
    }

    private static string ComparisonCsv(ComparisonRecord record)
    {
        return
            B(record.Comparable) + "," +
            record.HardMask + "," +
            F(record.PresenceAbsDiff) + "," +
            F(record.CanonicalPointMaxPx) + "," +
            F(record.RotationDiffDegrees) + "," +
            F(record.ScaleRelativeMax) + "," +
            F(record.GeometryQualityAbsDiff) + "," +
            F(record.ExpressionMaxAbsDiff);
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

    private void AppendComparison(
        List<string> lines,
        ComparisonMetrics metrics,
        string forcedOutcome = null)
    {
        lines.Add(
            "outcome=" +
            (forcedOutcome ?? DetermineCandidateOutcome(metrics)));
        lines.Add("canonicalComparable=" + metrics.CanonicalComparableCount);
        lines.Add("decodeStatusMismatch=" + metrics.DecodeStatusMismatchCount);
        lines.Add("acceptanceMismatch=" + metrics.AcceptanceMismatchCount);
        lines.Add("canonicalValidityMismatch=" + metrics.CanonicalValidityMismatchCount);
        lines.Add("nonFinite=" + metrics.NonFiniteCount);
        lines.Add("hardInvariantViolation=" + metrics.HardInvariantViolationCount);
        AppendStats(lines, "presenceAbsDiff", metrics.Presence);
        AppendStats(lines, "canonicalPointDiffPx", metrics.CanonicalPoints);
        AppendStats(lines, "rotationDiffDegrees", metrics.Rotation);
        AppendStats(lines, "scaleRelativeDiff", metrics.Scale);
        AppendStats(lines, "geometryQualityAbsDiff", metrics.GeometryQuality);
        AppendStats(lines, "expressionAbsDiff", metrics.Expression);
        AppendStats(lines, "raw478Landmark2dDiffPxDiagnosticOnly", metrics.Landmark2d);
        AppendStats(lines, "raw478LandmarkZAbsDiffDiagnosticOnly", metrics.LandmarkZ);
    }

    private static void AppendSpatial(
        List<string> lines,
        SpatialOutlierAccumulator metrics)
    {
        lines.Add("name=" + metrics.Name);
        lines.Add("pairs=" + metrics.PairCount);
        lines.Add("elements=" + metrics.ElementCount);
        lines.Add("nonFinite=" + metrics.NonFiniteCount);

        for (int thresholdIndex = 0;
             thresholdIndex < SpatialOutlierAccumulator.Thresholds.Length;
             thresholdIndex++)
        {
            string thresholdName =
                thresholdIndex == 0
                    ? "gt0"
                    : "ge" +
                      SpatialOutlierAccumulator.Thresholds[thresholdIndex]
                          .ToString("0.0", CultureInfo.InvariantCulture)
                          .Replace(".", "p");
            long total = metrics.ThresholdCounts[thresholdIndex];

            lines.Add(thresholdName + ".count=" + total);
            lines.Add(
                thresholdName + ".ratio=" +
                F(metrics.ElementCount > 0
                    ? total / (double)metrics.ElementCount
                    : 0.0));
            lines.Add(
                thresholdName + ".channelRGB=" +
                metrics.ChannelCounts[thresholdIndex, 0] + "," +
                metrics.ChannelCounts[thresholdIndex, 1] + "," +
                metrics.ChannelCounts[thresholdIndex, 2]);
            lines.Add(
                thresholdName + ".edgeLE1=" +
                metrics.EdgeCounts[thresholdIndex, 0]);
            lines.Add(
                thresholdName + ".edgeLE2=" +
                metrics.EdgeCounts[thresholdIndex, 1]);
            lines.Add(
                thresholdName + ".edgeLE4=" +
                metrics.EdgeCounts[thresholdIndex, 2]);
            lines.Add(
                thresholdName + ".edgeLE8=" +
                metrics.EdgeCounts[thresholdIndex, 3]);
            lines.Add(
                thresholdName + ".interiorGT8=" +
                metrics.EdgeCounts[thresholdIndex, 4]);
            lines.Add(
                thresholdName + ".edgeLE8Fraction=" +
                F(metrics.EdgeWithin8Fraction(thresholdIndex)));
            lines.Add(
                thresholdName + ".xHistogram=" +
                JoinHistogramRow(
                    metrics.XCounts,
                    thresholdIndex));
            lines.Add(
                thresholdName + ".yHistogram=" +
                JoinHistogramRow(
                    metrics.YCounts,
                    thresholdIndex));
        }
    }

    private static string JoinHistogramRow(
        long[,] histogram,
        int thresholdIndex)
    {
        string[] values =
            new string[InputSize];

        for (int i = 0; i < InputSize; i++)
        {
            values[i] =
                histogram[thresholdIndex, i]
                    .ToString(CultureInfo.InvariantCulture);
        }

        return string.Join(",", values);
    }

    private void WriteSpatialTopCsv(string path)
    {
        List<string> lines =
            new List<string>(_spatialTopRecords.Count + 1)
            {
                "comparison,pairIndex,sequence,channel,y,x,edgeDistance," +
                "referenceValue,candidateValue,signedDeltaLsb,absDeltaLsb"
            };

        for (int i = 0; i < _spatialTopRecords.Count; i++)
        {
            SpatialOutlierRecord r = _spatialTopRecords[i];
            lines.Add(
                Csv(r.Comparison) + "," +
                r.PairIndex + "," +
                r.Sequence + "," +
                r.Channel + "," +
                r.Y + "," +
                r.X + "," +
                r.EdgeDistance + "," +
                F(r.ReferenceValue) + "," +
                F(r.CandidateValue) + "," +
                F(r.SignedDeltaLsb) + "," +
                F(r.AbsDeltaLsb));
        }

        File.WriteAllLines(path, lines);
    }

    private void WriteSpatialHeatmapCsv(string path)
    {
        List<string> lines =
            new List<string>(PlaneLength + 1);

        string thresholdHeaders = string.Empty;
        for (int i = 0;
             i < SpatialOutlierAccumulator.Thresholds.Length;
             i++)
        {
            string suffix =
                i == 0
                    ? "gt0"
                    : "ge" +
                      SpatialOutlierAccumulator.Thresholds[i]
                          .ToString("0.0", CultureInfo.InvariantCulture)
                          .Replace(".", "p");
            thresholdHeaders +=
                ",a_" + suffix +
                ",b_" + suffix;
        }

        lines.Add(
            "y,x,edgeDistance,aMeanAbsLsb,aMaxAbsLsb," +
            "bMeanAbsLsb,bMaxAbsLsb" +
            thresholdHeaders);

        for (int y = 0; y < InputSize; y++)
        {
            for (int x = 0; x < InputSize; x++)
            {
                int pixel = y * InputSize + x;
                int edgeDistance =
                    Math.Min(
                        Math.Min(x, InputSize - 1 - x),
                        Math.Min(y, InputSize - 1 - y));
                long aCount = _referenceVsASpatial.HeatElementCount[pixel];
                long bCount = _referenceVsBSpatial.HeatElementCount[pixel];
                string row =
                    y + "," +
                    x + "," +
                    edgeDistance + "," +
                    F(aCount > 0
                        ? _referenceVsASpatial.HeatSumAbsLsb[pixel] / aCount
                        : 0.0) + "," +
                    F(_referenceVsASpatial.HeatMaxAbsLsb[pixel]) + "," +
                    F(bCount > 0
                        ? _referenceVsBSpatial.HeatSumAbsLsb[pixel] / bCount
                        : 0.0) + "," +
                    F(_referenceVsBSpatial.HeatMaxAbsLsb[pixel]);

                for (int thresholdIndex = 0;
                     thresholdIndex < SpatialOutlierAccumulator.Thresholds.Length;
                     thresholdIndex++)
                {
                    row +=
                        "," +
                        _referenceVsASpatial
                            .HeatThresholdCounts[thresholdIndex, pixel] +
                        "," +
                        _referenceVsBSpatial
                            .HeatThresholdCounts[thresholdIndex, pixel];
                }

                lines.Add(row);
            }
        }

        File.WriteAllLines(path, lines);
    }

    private static void AppendInputParity(
        List<string> lines,
        InputParityAccumulator metrics)
    {
        lines.Add(metrics.Name + ".pairs=" + metrics.PairCount);
        lines.Add(metrics.Name + ".samples=" + metrics.Count);
        lines.Add(metrics.Name + ".meanAbsLsb=" + F(metrics.MeanAbsLsb));
        lines.Add(metrics.Name + ".rmseLsb=" + F(metrics.RmseLsb));
        lines.Add(metrics.Name + ".signedBiasR=" + F(metrics.SignedChannelBiasLsb(0)));
        lines.Add(metrics.Name + ".signedBiasG=" + F(metrics.SignedChannelBiasLsb(1)));
        lines.Add(metrics.Name + ".signedBiasB=" + F(metrics.SignedChannelBiasLsb(2)));
        lines.Add(metrics.Name + ".p95AbsLsbApprox=" + F(metrics.Percentile(0.95)));
        lines.Add(metrics.Name + ".p99AbsLsbApprox=" + F(metrics.Percentile(0.99)));
        lines.Add(metrics.Name + ".maxAbsLsb=" + F(metrics.MaxAbsLsb));
        lines.Add(metrics.Name + ".exactMatchRatio=" + F(metrics.ExactMatchRatio));
    }

    private static Stats ComputeStats(
        List<double> values)
    {
        Stats result =
            default;

        if (
            values == null ||
            values.Count == 0)
        {
            return result;
        }

        double[] data =
            values.ToArray();

        Array.Sort(data);

        double sum = 0.0;

        for (
            int i = 0;
            i < data.Length;
            i++)
        {
            sum += data[i];
        }

        result.Count = data.Length;
        result.Mean = sum / data.Length;
        result.P50 = Percentile(data, 0.50);
        result.P95 = Percentile(data, 0.95);
        result.Max = data[data.Length - 1];

        return result;
    }

    private static void AppendStats(
        List<string> lines,
        string name,
        List<double> values)
    {
        Stats stats =
            ComputeStats(
                values);

        lines.Add(name + ".samples=" + stats.Count);
        lines.Add(name + ".mean=" + F(stats.Mean));
        lines.Add(name + ".median=" + F(stats.P50));
        lines.Add(name + ".p95=" + F(stats.P95));
        lines.Add(name + ".max=" + F(stats.Max));
    }

    private static void AppendIntStats(
        List<string> lines,
        string name,
        List<int> values)
    {
        List<double> converted =
            new List<double>();

        if (values != null)
        {
            for (
                int i = 0;
                i < values.Count;
                i++)
            {
                converted.Add(
                    values[i]);
            }
        }

        AppendStats(
            lines,
            name,
            converted);
    }

    private static double Percentile(
        double[] sorted,
        double percentile)
    {
        if (
            sorted == null ||
            sorted.Length == 0)
        {
            return 0.0;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        double position =
            (
                sorted.Length -
                1
            ) *
            percentile;

        int lower =
            Mathf.Clamp(
                (int)Math.Floor(
                    position),
                0,
                sorted.Length - 1);

        int upper =
            Mathf.Clamp(
                (int)Math.Ceiling(
                    position),
                0,
                sorted.Length - 1);

        if (lower == upper)
        {
            return sorted[lower];
        }

        double t =
            position -
            lower;

        return
            sorted[lower] +
            (
                sorted[upper] -
                sorted[lower]
            ) *
            t;
    }

    private static Quaternion NormalizeQuaternionSafe(
        Quaternion q)
    {
        double magnitude =
            Math.Sqrt(
                q.x * q.x +
                q.y * q.y +
                q.z * q.z +
                q.w * q.w);

        if (
            magnitude < 0.000001 ||
            double.IsNaN(magnitude) ||
            double.IsInfinity(magnitude))
        {
            return Quaternion.identity;
        }

        float inv =
            (float)(
                1.0 /
                magnitude);

        return
            new Quaternion(
                q.x * inv,
                q.y * inv,
                q.z * inv,
                q.w * inv);
    }

    private static bool IsFinite(
        float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
    }

    private static bool IsFinite(
        Vector2 value)
    {
        return
            IsFinite(value.x) &&
            IsFinite(value.y);
    }

    private static bool IsFinite(
        Quaternion value)
    {
        return
            IsFinite(value.x) &&
            IsFinite(value.y) &&
            IsFinite(value.z) &&
            IsFinite(value.w);
    }

    private static bool IsFinite(
        FaceExpressionData value)
    {
        return
            IsFinite(value.eyeBlinkLeft) &&
            IsFinite(value.eyeBlinkRight) &&
            IsFinite(value.eyeWideLeft) &&
            IsFinite(value.eyeWideRight) &&
            IsFinite(value.jawOpen) &&
            IsFinite(value.mouthSmileLeft) &&
            IsFinite(value.mouthSmileRight) &&
            IsFinite(value.mouthPucker) &&
            IsFinite(value.mouthFunnel);
    }

    private static double TicksToMilliseconds(
        long ticks)
    {
        return
            ticks *
            1000.0 /
            Stopwatch.Frequency;
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
        if (_reportWritten)
        {
            return;
        }

        if (
            _measuring ||
            _workersReady ||
            _reflectionReady ||
            _pairAttemptCount > 0)
        {
            try
            {
                WriteReport(
                    _measuring
                        ? "STOPPED_BEFORE_COMPLETE"
                        : "STOPPED_BEFORE_MEASURE");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "[Kiwi v44.55.20 CommonTensor] STOP_REPORT_FAIL " +
                    exception.GetType().Name +
                    " " +
                    exception.Message);
            }
        }
    }

    private void OnDestroy()
    {
        _referenceWorker?.Dispose();
        _referenceWorker = null;

        _leftWorker?.Dispose();
        _leftWorker = null;

        _rightWorker?.Dispose();
        _rightWorker = null;

        _referenceFrozenGpuWorker?.Dispose();
        _referenceFrozenGpuWorker = null;

        _referenceCpuWorker?.Dispose();
        _referenceCpuWorker = null;

        _aGpuWorker?.Dispose();
        _aGpuWorker = null;

        _bGpuWorker?.Dispose();
        _bGpuWorker = null;

        _referenceInput?.Dispose();
        _referenceInput = null;

        _leftInput?.Dispose();
        _leftInput = null;

        _rightInput?.Dispose();
        _rightInput = null;

        _referenceFrozenGpuInput?.Dispose();
        _referenceFrozenGpuInput = null;

        _referenceCpuInput?.Dispose();
        _referenceCpuInput = null;

        _aGpuInput?.Dispose();
        _aGpuInput = null;

        _bGpuInput?.Dispose();
        _bGpuInput = null;

        if (_referenceSnapshotTexture != null)
        {
            if (_referenceSnapshotTexture.IsCreated())
            {
                _referenceSnapshotTexture.Release();
            }

            Destroy(_referenceSnapshotTexture);
            _referenceSnapshotTexture = null;
        }

        if (_referenceCommandBuffer != null)
        {
            _referenceCommandBuffer.Release();
            _referenceCommandBuffer = null;
        }
    }

    private void OnApplicationQuit()
    {
        if (_reportWritten)
        {
            return;
        }

        if (
            _measuring ||
            _workersReady ||
            _reflectionReady ||
            _pairAttemptCount > 0)
        {
            WriteReport(
                "QUIT");
        }
    }

    private static bool ReadBoolEnvironment(
        string name,
        bool fallback)
    {
        string value =
            Environment.GetEnvironmentVariable(
                name);

        if (string.IsNullOrWhiteSpace(value))
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

    private static float ReadFloatEnvironment(
        string name,
        float fallback)
    {
        string value =
            Environment.GetEnvironmentVariable(
                name);

        if (
            float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsed))
        {
            return parsed;
        }

        return fallback;
    }
}
