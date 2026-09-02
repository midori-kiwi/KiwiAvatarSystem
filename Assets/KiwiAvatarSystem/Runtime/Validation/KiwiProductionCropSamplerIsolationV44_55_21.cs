using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
/// KiwiAvatarSystem v44.55.21 Production crop sampler border/footprint isolation.
///
/// This opt-in observer consumes the exact frozen REF and mode2 tensors produced
/// by the v44.55.20 common-tensor observer. It adds no camera frame, crop Blit,
/// RenderTexture, inference input, readback, wait, or Production write.
///
/// Production and Native source inspection establishes that mode2 already applies
/// the same clamp-to-edge rule as the Production crop shader. MODE2_CLAMP is
/// therefore a deliberately separate frozen array whose bitwise identity with
/// MODE2_ORIGINAL is fail-closed. Re-running an identical tensor through another
/// worker would not isolate a border contract; the existing v44.55.20 B_GPU result
/// is the semantic authority for both bitwise-identical candidates.
/// </summary>
[DefaultExecutionOrder(32000)]
internal sealed class KiwiProductionCropSamplerIsolationV44_55_21
    : MonoBehaviour
{
    private const string Contract =
        "KIWI_V44_55_21_PRODUCTION_CROP_SAMPLER_BORDER_FOOTPRINT_ISOLATION";

    private const string EnableVariable =
        "KIWI_V44_55_21_CROP_SAMPLER_AUDIT";

    private const int InputSize = 192;
    private const int PlaneLength = InputSize * InputSize;
    private const int InputFloatCount = PlaneLength * 3;
    private const int MinimumCompletedPairs = 60;
    private const int MinimumCanonicalComparablePairs = 40;
    private const double PairMeanToleranceLsb = 0.000001;
    private const double StrongOobConcentrationRatio = 0.80;
    private const double TailCollapseRatio = 0.25;
    private const double TailMaxCollapseRatio = 0.50;

    private static readonly BindingFlags InstancePrivate =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly int[] Thresholds =
    {
        1, 2, 4, 8, 16, 32
    };

    private static bool _installed;

    private KiwiCommonTensorBackendStageIsolationV44_55_20 _dependency;
    private Type _dependencyType;
    private FieldInfo _recordsField;
    private FieldInfo _referenceNchwField;
    private FieldInfo _mode2NchwField;
    private FieldInfo _samplingMatrixField;
    private FieldInfo _reportWrittenField;
    private FieldInfo _observerFaultCountField;
    private FieldInfo _sourceIdentityMismatchCountField;
    private FieldInfo _referenceTensorNonFiniteCountField;
    private FieldInfo _referenceVsMode2MetricsField;
    private FieldInfo _runnerField;
    private FieldInfo _runnerSourceTextureField;

    private readonly float[] _mode2ClampNchw =
        new float[InputFloatCount];

    private readonly InputAccumulator _referenceVsOriginal =
        new InputAccumulator();

    private readonly InputAccumulator _referenceVsClamp =
        new InputAccumulator();

    private readonly InputAccumulator _originalVsClamp =
        new InputAccumulator();

    private readonly FootprintAccumulator _footprint =
        new FootprintAccumulator();

    private readonly List<string> _mainRows =
        new List<string>(128);

    private int _processedPairs;
    private int _observerFaultCount;
    private int _recordJumpCount;
    private int _pairMeanMismatchCount;
    private int _originalClampBitMismatchCount;
    private int _matrixNonFiniteCount;
    private int _sourceContractMismatchCount;
    private int _dependencyMissingFrames;
    private bool _reportWritten;
    private bool _bound;
    private string _sourceName = "<unresolved>";
    private int _sourceWidth;
    private int _sourceHeight;
    private string _sourceFilterMode = "<unresolved>";
    private string _sourceWrapMode = "<unresolved>";
    private string _sourceDimension = "<unresolved>";
    private string _sourceGraphicsFormat = "<unresolved>";
    private string _outlierPath;
    private StreamWriter _outlierWriter;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed || !ReadBoolEnvironment(EnableVariable, false))
        {
            return;
        }

        _installed = true;

        GameObject go =
            new GameObject(
                "[Kiwi] v44.55.21 Crop Sampler Border Footprint Isolation");

        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.DontSave;
        go.AddComponent<KiwiProductionCropSamplerIsolationV44_55_21>();
    }

    private void Awake()
    {
        string directory =
            Path.Combine(
                Application.persistentDataPath,
                "KiwiFrameBottleneck");

        Directory.CreateDirectory(directory);

        string stamp =
            DateTime.Now.ToString(
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture);

        _outlierPath =
            Path.Combine(
                directory,
                "KiwiProductionCropSamplerFootprints_v44_55_21_" +
                stamp +
                ".csv");

        _outlierWriter =
            new StreamWriter(
                _outlierPath,
                false,
                new UTF8Encoding(false));

        _outlierWriter.WriteLine(
            "pairIndex,sequence,nativeHostTicks,channel,tensorY,tensorX," +
            "referenceValue,mode2OriginalValue,signedDeltaLsb,absDeltaLsb," +
            "threshold8,threshold16,cropU,cropVBottom,rawSourceU,rawSourceVBottom," +
            "clampedSourceU,clampedSourceVBottom,rawSourceX,rawSourceTopY," +
            "sampleSourceX,sampleSourceTopY," +
            "floorX,floorY,ceilX,ceilY,fracX,fracY," +
            "tap00X,tap00Y,tap10X,tap10Y,tap01X,tap01Y,tap11X,tap11Y," +
            "tap00InBounds,tap10InBounds,tap01InBounds,tap11InBounds," +
            "anyTapOutOfBounds,centerOutOfBounds,clampTargetTapCount," +
            "leftBoundary,rightBoundary,topBoundary,bottomBoundary,sourceEdgeDistanceTexels");

        Debug.Log(
            "[Kiwi v44.55.21 CropSampler] WAIT_DEPENDENCY" +
            " contract=" + Contract +
            " observerOnly=1 productionWrites=0 nativeWrites=0" +
            " addedBlit=0 addedRenderTexture=0 addedReadback=0 blockingWait=0" +
            " dependency=KIWI_V44_55_20_COMMON_TENSOR_AUDIT" +
            " mode2OriginalClampContract=STATICALLY_CONFIRMED" +
            " mode2ClampImplementation=BITWISE_FROZEN_ALIAS" +
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

        ProcessAvailablePairs();

        if (ReadFieldBool(_dependency, _reportWrittenField))
        {
            WriteReport("DEPENDENCY_COMPLETE");
        }
    }

    private void TryBindDependency()
    {
        KiwiCommonTensorBackendStageIsolationV44_55_20[] candidates =
            Resources.FindObjectsOfTypeAll<
                KiwiCommonTensorBackendStageIsolationV44_55_20>();

        _dependency =
            candidates != null && candidates.Length == 1
                ? candidates[0]
                : null;

        if (_dependency == null)
        {
            return;
        }

        try
        {
            _dependencyType = _dependency.GetType();
            _recordsField = RequiredField(_dependencyType, "_records");
            _referenceNchwField = RequiredField(_dependencyType, "_referenceNchw");
            _mode2NchwField = RequiredField(_dependencyType, "_rightNchw");
            _samplingMatrixField = RequiredField(_dependencyType, "_samplingMatrix");
            _reportWrittenField = RequiredField(_dependencyType, "_reportWritten");
            _observerFaultCountField = RequiredField(_dependencyType, "_observerFaultCount");
            _sourceIdentityMismatchCountField = RequiredField(_dependencyType, "_sourceIdentityMismatchCount");
            _referenceTensorNonFiniteCountField = RequiredField(_dependencyType, "_referenceTensorNonFiniteCount");
            _referenceVsMode2MetricsField = RequiredField(_dependencyType, "_referenceVsMode2");
            _runnerField = RequiredField(_dependencyType, "_runner");

            object runner = _runnerField.GetValue(_dependency);
            if (runner != null)
            {
                _runnerSourceTextureField =
                    runner.GetType().GetField(
                        "_sentisSourceTexture",
                        InstancePrivate);
            }

            _bound = true;
            Debug.Log(
                "[Kiwi v44.55.21 CropSampler] DEPENDENCY_BOUND" +
                " executionOrder=32000 reflectionReadOnly=1");
        }
        catch (Exception exception)
        {
            RegisterFault(
                "DEPENDENCY_REFLECTION_BIND_FAILED " +
                exception.GetType().Name +
                " " +
                exception.Message);
            WriteReport("BIND_FAIL");
        }
    }

    private void ProcessAvailablePairs()
    {
        IList records =
            _recordsField.GetValue(_dependency)
                as IList;

        if (records == null)
        {
            RegisterFault("DEPENDENCY_RECORDS_NULL");
            return;
        }

        if (records.Count < _processedPairs)
        {
            RegisterFault("DEPENDENCY_RECORDS_REWOUND");
            return;
        }

        if (records.Count - _processedPairs > 1)
        {
            _recordJumpCount++;
            RegisterFault(
                "DEPENDENCY_RECORD_JUMP count=" +
                records.Count +
                " processed=" +
                _processedPairs);
            return;
        }

        if (records.Count == _processedPairs)
        {
            return;
        }

        object record = records[_processedPairs];
        float[] reference =
            _referenceNchwField.GetValue(_dependency)
                as float[];
        float[] original =
            _mode2NchwField.GetValue(_dependency)
                as float[];
        float[] matrix =
            _samplingMatrixField.GetValue(_dependency)
                as float[];

        if (
            record == null ||
            reference == null ||
            original == null ||
            matrix == null ||
            reference.Length != InputFloatCount ||
            original.Length != InputFloatCount ||
            matrix.Length != 16)
        {
            RegisterFault("DEPENDENCY_PAIR_SHAPE_INVALID");
            return;
        }

        for (int i = 0; i < matrix.Length; i++)
        {
            if (!IsFinite(matrix[i]))
            {
                _matrixNonFiniteCount++;
            }
        }

        if (_matrixNonFiniteCount > 0)
        {
            RegisterFault("SAMPLING_MATRIX_NONFINITE");
            return;
        }

        int pairIndex = ReadMemberInt(record, "Index");
        ulong sequence = ReadMemberULong(record, "Sequence");
        long nativeHostTicks = ReadMemberLong(record, "NativeHostTicks");
        int sourceWidth = ReadMemberInt(record, "SourceWidth");
        int sourceHeight = ReadMemberInt(record, "SourceHeight");

        if (
            pairIndex != _processedPairs ||
            sequence == 0UL ||
            nativeHostTicks <= 0L ||
            sourceWidth <= 0 ||
            sourceHeight <= 0 ||
            !ReadMemberBool(record, "LaneStillMatchedAfterCapture"))
        {
            RegisterFault("PAIR_IDENTITY_INVALID");
            return;
        }

        CaptureSourceContract(sourceWidth, sourceHeight);

        Array.Copy(
            original,
            _mode2ClampNchw,
            InputFloatCount);

        PairSummary originalSummary =
            _referenceVsOriginal.Accumulate(
                reference,
                original);

        PairSummary clampSummary =
            _referenceVsClamp.Accumulate(
                reference,
                _mode2ClampNchw);

        PairSummary aliasSummary =
            _originalVsClamp.Accumulate(
                original,
                _mode2ClampNchw);

        double dependencyMean =
            ReadMemberDouble(
                record,
                "ReferenceVsMode2InputMeanAbsLsb");

        if (Math.Abs(originalSummary.MeanAbsLsb - dependencyMean) > PairMeanToleranceLsb)
        {
            _pairMeanMismatchCount++;
            RegisterFault("PAIR_MEAN_RECOMPUTE_MISMATCH");
            return;
        }

        if (
            aliasSummary.MaxAbsLsb != 0.0 ||
            aliasSummary.ExactCount != InputFloatCount)
        {
            _originalClampBitMismatchCount++;
            RegisterFault("MODE2_ORIGINAL_CLAMP_NOT_BITWISE_IDENTICAL");
            return;
        }

        AnalyzeFootprints(
            pairIndex,
            sequence,
            nativeHostTicks,
            sourceWidth,
            sourceHeight,
            matrix,
            reference,
            original);

        object semantic =
            ReadMemberObject(
                record,
                "PreprocessGpuB");

        _mainRows.Add(
            JoinCsv(
                pairIndex,
                sequence,
                nativeHostTicks,
                sourceWidth,
                sourceHeight,
                ReadMemberBool(record, "LaneStillMatchedAfterCapture"),
                originalSummary.MeanAbsLsb,
                originalSummary.RmseLsb,
                originalSummary.ExactCount / (double)InputFloatCount,
                originalSummary.MaxAbsLsb,
                clampSummary.MeanAbsLsb,
                clampSummary.RmseLsb,
                clampSummary.ExactCount / (double)InputFloatCount,
                clampSummary.MaxAbsLsb,
                aliasSummary.ExactCount == InputFloatCount,
                _footprint.LastLargeCount,
                _footprint.LastAnyTapOobCount,
                _footprint.LastCenterOobCount,
                ReadMemberBool(semantic, "Comparable"),
                ReadMemberInt(semantic, "HardMask"),
                ReadMemberDouble(semantic, "PresenceAbsDiff"),
                ReadMemberDouble(semantic, "CanonicalPointMaxPx"),
                ReadMemberDouble(semantic, "RotationDiffDegrees"),
                ReadMemberDouble(semantic, "ScaleRelativeMax"),
                ReadMemberDouble(semantic, "GeometryQualityAbsDiff"),
                ReadMemberDouble(semantic, "ExpressionMaxAbsDiff")));

        _processedPairs++;

        Debug.Log(
            "[Kiwi v44.55.21 CropSampler] SAMPLE" +
            " index=" + pairIndex +
            " sequence=" + sequence +
            " source=" + sourceWidth + "x" + sourceHeight +
            " ge4=" + _footprint.LastLargeCount +
            " ge4AnyTapOob=" + _footprint.LastAnyTapOobCount +
            " ge4CenterOob=" + _footprint.LastCenterOobCount +
            " originalClampExact=1");
    }

    private void CaptureSourceContract(
        int expectedWidth,
        int expectedHeight)
    {
        object runner = _runnerField.GetValue(_dependency);

        if (runner != null && _runnerSourceTextureField == null)
        {
            _runnerSourceTextureField =
                runner.GetType().GetField(
                    "_sentisSourceTexture",
                    InstancePrivate);
        }

        Texture source =
            runner != null && _runnerSourceTextureField != null
                ? _runnerSourceTextureField.GetValue(runner) as Texture
                : null;

        if (source == null)
        {
            _sourceContractMismatchCount++;
            RegisterFault("SOURCE_TEXTURE_UNRESOLVED");
            return;
        }

        _sourceName = source.name ?? string.Empty;
        _sourceWidth = source.width;
        _sourceHeight = source.height;
        _sourceFilterMode = source.filterMode.ToString();
        _sourceWrapMode = source.wrapMode.ToString();
        _sourceDimension = source.dimension.ToString();
        _sourceGraphicsFormat = source.graphicsFormat.ToString();

        if (
            source.width != expectedWidth ||
            source.height != expectedHeight ||
            source.filterMode != FilterMode.Bilinear ||
            source.wrapMode != TextureWrapMode.Clamp ||
            !string.Equals(
                source.name,
                "KiwiNativeCameraPresentation",
                StringComparison.Ordinal))
        {
            _sourceContractMismatchCount++;
            RegisterFault(
                "SOURCE_TEXTURE_CONTRACT_MISMATCH name=" +
                _sourceName +
                " size=" +
                source.width +
                "x" +
                source.height +
                " filter=" +
                source.filterMode +
                " wrap=" +
                source.wrapMode);
        }
    }

    private void AnalyzeFootprints(
        int pairIndex,
        ulong sequence,
        long nativeHostTicks,
        int sourceWidth,
        int sourceHeight,
        float[] matrix,
        float[] reference,
        float[] original)
    {
        _footprint.BeginPair();

        for (int y = 0; y < InputSize; y++)
        {
            double cropTopV = (y + 0.5) / InputSize;
            double cropVBottom = 1.0 - cropTopV;

            for (int x = 0; x < InputSize; x++)
            {
                double cropU = (x + 0.5) / InputSize;
                double rawU =
                    matrix[0] * cropU +
                    matrix[1] * cropVBottom +
                    matrix[3];
                double rawVBottom =
                    matrix[4] * cropU +
                    matrix[5] * cropVBottom +
                    matrix[7];

                Footprint point =
                    Footprint.Create(
                        cropU,
                        cropVBottom,
                        rawU,
                        rawVBottom,
                        sourceWidth,
                        sourceHeight);

                int tensorIndex = y * InputSize + x;

                for (int channel = 0; channel < 3; channel++)
                {
                    int index = channel * PlaneLength + tensorIndex;
                    double signedLsb =
                        ((double)original[index] - reference[index]) * 255.0;
                    double absLsb = Math.Abs(signedLsb);

                    _footprint.Accumulate(
                        absLsb,
                        y,
                        point);

                    if (absLsb < 4.0)
                    {
                        continue;
                    }

                    WriteOutlier(
                        pairIndex,
                        sequence,
                        nativeHostTicks,
                        channel,
                        y,
                        x,
                        reference[index],
                        original[index],
                        signedLsb,
                        absLsb,
                        point);
                }
            }
        }
    }

    private void WriteOutlier(
        int pairIndex,
        ulong sequence,
        long nativeHostTicks,
        int channel,
        int y,
        int x,
        float reference,
        float original,
        double signedLsb,
        double absLsb,
        Footprint p)
    {
        _outlierWriter.WriteLine(
            JoinCsv(
                pairIndex, sequence, nativeHostTicks, channel, y, x,
                reference, original, signedLsb, absLsb,
                absLsb >= 8.0, absLsb >= 16.0,
                p.CropU, p.CropVBottom, p.RawU, p.RawVBottom,
                p.ClampedU, p.ClampedVBottom,
                p.RawSourceX, p.RawSourceTopY,
                p.SourceX, p.SourceTopY,
                p.X0, p.Y0, p.X1, p.Y1, p.FracX, p.FracY,
                p.X0, p.Y0, p.X1, p.Y0, p.X0, p.Y1, p.X1, p.Y1,
                p.Tap00InBounds, p.Tap10InBounds,
                p.Tap01InBounds, p.Tap11InBounds,
                p.AnyTapOutOfBounds, p.CenterOutOfBounds,
                p.ClampTargetTapCount,
                p.LeftBoundary, p.RightBoundary,
                p.TopBoundary, p.BottomBoundary,
                p.SourceEdgeDistanceTexels));
    }

    private void WriteReport(string status)
    {
        if (_reportWritten)
        {
            return;
        }

        _reportWritten = true;
        _outlierWriter?.Flush();
        _outlierWriter?.Dispose();
        _outlierWriter = null;

        string directory =
            Path.GetDirectoryName(_outlierPath);
        string stamp =
            Path.GetFileNameWithoutExtension(_outlierPath)
                .Replace(
                    "KiwiProductionCropSamplerFootprints_v44_55_21_",
                    string.Empty);

        string textPath =
            Path.Combine(
                directory,
                "KiwiProductionCropSamplerIsolation_v44_55_21_" +
                stamp +
                ".txt");

        string csvPath =
            Path.Combine(
                directory,
                "KiwiProductionCropSamplerIsolation_v44_55_21_" +
                stamp +
                ".csv");

        int dependencyFaults =
            ReadFieldInt(_dependency, _observerFaultCountField) +
            ReadFieldInt(_dependency, _sourceIdentityMismatchCountField) +
            ReadFieldInt(_dependency, _referenceTensorNonFiniteCountField);

        object metrics =
            _referenceVsMode2MetricsField != null && _dependency != null
                ? _referenceVsMode2MetricsField.GetValue(_dependency)
                : null;

        int canonicalComparable =
            ReadMemberInt(metrics, "CanonicalComparableCount");

        string originalOutcome =
            DetermineSemanticOutcome(metrics, _processedPairs);
        string clampOutcome = originalOutcome;

        long originalGe4 = _referenceVsOriginal.ThresholdCount(4);
        long clampGe4 = _referenceVsClamp.ThresholdCount(4);
        double tailRatio =
            originalGe4 > 0
                ? clampGe4 / (double)originalGe4
                : 1.0;
        double maxRatio =
            _referenceVsOriginal.MaxAbsLsb > 0.0
                ? _referenceVsClamp.MaxAbsLsb /
                    _referenceVsOriginal.MaxAbsLsb
                : 1.0;
        double oobRatio =
            _footprint.CountAt(4) > 0
                ? _footprint.AnyTapOobAt(4) /
                    (double)_footprint.CountAt(4)
                : 0.0;

        bool invalid =
            _observerFaultCount > 0 ||
            dependencyFaults > 0 ||
            _recordJumpCount > 0 ||
            _pairMeanMismatchCount > 0 ||
            _originalClampBitMismatchCount > 0 ||
            _matrixNonFiniteCount > 0 ||
            _sourceContractMismatchCount > 0 ||
            (
                _processedPairs > 0 &&
                (
                    _originalVsClamp.MaxAbsLsb != 0.0 ||
                    _originalVsClamp.ExactMatchRatio != 1.0
                )
            );

        string decision;
        if (invalid)
        {
            decision = "INVALID_OBSERVER";
        }
        else if (
            !string.Equals(status, "DEPENDENCY_COMPLETE", StringComparison.Ordinal) ||
            !_bound ||
            _processedPairs < MinimumCompletedPairs ||
            canonicalComparable < MinimumCanonicalComparablePairs)
        {
            decision = "INSUFFICIENT_DATA";
        }
        else
        {
            bool stronglyConcentrated =
                oobRatio >= StrongOobConcentrationRatio;
            bool tailCollapsed =
                tailRatio <= TailCollapseRatio &&
                maxRatio <= TailMaxCollapseRatio;

            if (
                stronglyConcentrated &&
                tailCollapsed &&
                string.Equals(clampOutcome, "PASS", StringComparison.Ordinal))
            {
                decision = "BORDER_CONTRACT_CONFIRMED";
            }
            else if (tailCollapsed)
            {
                decision = "BORDER_IMPROVES_BUT_NOT_SUFFICIENT";
            }
            else
            {
                decision = "BORDER_REJECTED";
            }
        }

        List<string> lines = new List<string>();
        lines.Add("contract=" + Contract);
        lines.Add("status=" + status);
        lines.Add("decision=" + decision);
        lines.Add("observerOnly=1");
        lines.Add("performanceAuthority=0");
        lines.Add("productionWrites=0");
        lines.Add("nativeWrites=0");
        lines.Add("addedBlit=0");
        lines.Add("addedRenderTexture=0");
        lines.Add("addedReadback=0");
        lines.Add("blockingWait=0");
        lines.Add("mode2OriginalAlreadyClampsNormalizedUv=1");
        lines.Add("mode2OriginalAlreadyClampsSourceTaps=1");
        lines.Add("mode2ClampBitwiseAlias=1");
        lines.Add("duplicateClampGpuWorkerAdded=0");
        lines.Add("clampSemanticAuthority=identical_frozen_tensor_reuses_v44_55_20_B_GPU");
        lines.Add("processedPairs=" + _processedPairs);
        lines.Add("canonicalComparable=" + canonicalComparable);
        lines.Add("observerFaultCount=" + _observerFaultCount);
        lines.Add("dependencyFaultCount=" + dependencyFaults);
        lines.Add("recordJumpCount=" + _recordJumpCount);
        lines.Add("pairMeanMismatchCount=" + _pairMeanMismatchCount);
        lines.Add("originalClampBitMismatchCount=" + _originalClampBitMismatchCount);
        lines.Add("matrixNonFiniteCount=" + _matrixNonFiniteCount);
        lines.Add("sourceContractMismatchCount=" + _sourceContractMismatchCount);
        lines.Add("dependencyMissingFrames=" + _dependencyMissingFrames);
        lines.Add("sourceName=" + _sourceName);
        lines.Add("sourceWidth=" + _sourceWidth);
        lines.Add("sourceHeight=" + _sourceHeight);
        lines.Add("sourceFilterMode=" + _sourceFilterMode);
        lines.Add("sourceWrapMode=" + _sourceWrapMode);
        lines.Add("sourceDimension=" + _sourceDimension);
        lines.Add("sourceGraphicsFormat=" + _sourceGraphicsFormat);
        lines.Add("strongOobConcentrationRatioGate=" + F(StrongOobConcentrationRatio));
        lines.Add("tailCollapseCountRatioGate=" + F(TailCollapseRatio));
        lines.Add("tailCollapseMaxRatioGate=" + F(TailMaxCollapseRatio));
        lines.Add("ge4AnyTapOobRatio=" + F(oobRatio));
        lines.Add("ge4ClampOriginalCountRatio=" + F(tailRatio));
        lines.Add("clampOriginalMaxRatio=" + F(maxRatio));
        lines.Add("originalSemanticOutcome=" + originalOutcome);
        lines.Add("clampSemanticOutcome=" + clampOutcome);
        lines.Add("");
        AppendInput(lines, "REF_VS_MODE2_ORIGINAL", _referenceVsOriginal);
        AppendInput(lines, "REF_VS_MODE2_CLAMP", _referenceVsClamp);
        AppendInput(lines, "MODE2_ORIGINAL_VS_MODE2_CLAMP", _originalVsClamp);
        lines.Add("");
        AppendFootprints(lines, _footprint);
        lines.Add("");
        AppendSemantic(lines, metrics);
        lines.Add("");
        lines.Add("decisionRule=INVALID_OBSERVER > HARD_FAIL > INSUFFICIENT_DATA > AGGREGATE_GATE");
        lines.Add("borderConfirmedRequires=ge4AnyTapOobRatio>=0.80 AND ge4CountRatio<=0.25 AND maxRatio<=0.50 AND clampSemanticPASS");
        lines.Add("borderRejectedReasonWhenAlias=MODE2_ORIGINAL already has the Production clamp contract, so applying it again cannot collapse the tail");

        File.WriteAllLines(textPath, lines);

        List<string> csv = new List<string>(_mainRows.Count + 1);
        csv.Add(
            "pairIndex,sequence,nativeHostTicks,sourceWidth,sourceHeight,laneStillMatched," +
            "refOriginalMeanAbsLsb,refOriginalRmseLsb,refOriginalExactRatio,refOriginalMaxLsb," +
            "refClampMeanAbsLsb,refClampRmseLsb,refClampExactRatio,refClampMaxLsb," +
            "originalClampBitwiseExact,ge4Count,ge4AnyTapOobCount,ge4CenterOobCount," +
            "semanticComparable,semanticHardMask,presenceAbsDiff,canonicalPointMaxPx," +
            "rotationDiffDegrees,scaleRelativeMax,geometryQualityAbsDiff,expressionMaxAbsDiff");
        csv.AddRange(_mainRows);
        File.WriteAllLines(csvPath, csv);

        Debug.Log(
            "[Kiwi v44.55.21 CropSampler] COMPLETE" +
            " decision=" + decision +
            " pairs=" + _processedPairs +
            " canonicalComparable=" + canonicalComparable +
            " ge4=" + _footprint.CountAt(4) +
            " ge4AnyTapOobRatio=" + F(oobRatio) +
            " originalClampExactRatio=" + F(_originalVsClamp.ExactMatchRatio) +
            " report=" + textPath +
            " csv=" + csvPath +
            " footprints=" + _outlierPath);
    }

    private static string DetermineSemanticOutcome(object metrics, int completed)
    {
        if (metrics == null)
        {
            return "INVALID_OBSERVER";
        }

        if (
            ReadMemberInt(metrics, "DecodeStatusMismatchCount") > 0 ||
            ReadMemberInt(metrics, "AcceptanceMismatchCount") > 0 ||
            ReadMemberInt(metrics, "CanonicalValidityMismatchCount") > 0 ||
            ReadMemberInt(metrics, "NonFiniteCount") > 0 ||
            ReadMemberInt(metrics, "HardInvariantViolationCount") > 0)
        {
            return "HARD_FAIL";
        }

        if (
            completed < MinimumCompletedPairs ||
            ReadMemberInt(metrics, "CanonicalComparableCount") <
                MinimumCanonicalComparablePairs)
        {
            return "INSUFFICIENT_DATA";
        }

        bool pass =
            MetricPass(metrics, "Presence", 0.010, 0.030) &&
            MetricPass(metrics, "CanonicalPoints", 1.0, 2.0) &&
            MetricPass(metrics, "Rotation", 0.10, 0.25) &&
            MetricPass(metrics, "Scale", 0.005, 0.010) &&
            MetricPass(metrics, "GeometryQuality", 0.010, 0.025) &&
            MetricPass(metrics, "Expression", 0.020, 0.050);

        return pass ? "PASS" : "AGGREGATE_FAIL";
    }

    private static bool MetricPass(
        object metrics,
        string name,
        double p95Gate,
        double maxGate)
    {
        IList values = ReadMemberObject(metrics, name) as IList;
        return
            values != null &&
            values.Count > 0 &&
            Percentile(values, 0.95) <= p95Gate &&
            Maximum(values) <= maxGate;
    }

    private static void AppendSemantic(List<string> lines, object metrics)
    {
        lines.Add("[SEMANTIC_REF_GPU_VS_MODE2_ORIGINAL_AND_IDENTICAL_CLAMP]");
        lines.Add("canonicalComparable=" + ReadMemberInt(metrics, "CanonicalComparableCount"));
        lines.Add("decodeStatusMismatch=" + ReadMemberInt(metrics, "DecodeStatusMismatchCount"));
        lines.Add("acceptanceMismatch=" + ReadMemberInt(metrics, "AcceptanceMismatchCount"));
        lines.Add("canonicalValidityMismatch=" + ReadMemberInt(metrics, "CanonicalValidityMismatchCount"));
        lines.Add("nonFinite=" + ReadMemberInt(metrics, "NonFiniteCount"));
        lines.Add("hardInvariant=" + ReadMemberInt(metrics, "HardInvariantViolationCount"));
        AppendMetric(lines, metrics, "Presence", 0.010, 0.030);
        AppendMetric(lines, metrics, "CanonicalPoints", 1.0, 2.0);
        AppendMetric(lines, metrics, "Rotation", 0.10, 0.25);
        AppendMetric(lines, metrics, "Scale", 0.005, 0.010);
        AppendMetric(lines, metrics, "GeometryQuality", 0.010, 0.025);
        AppendMetric(lines, metrics, "Expression", 0.020, 0.050);
    }

    private static void AppendMetric(
        List<string> lines,
        object metrics,
        string name,
        double p95Gate,
        double maxGate)
    {
        IList values = ReadMemberObject(metrics, name) as IList;
        double p95 = values != null ? Percentile(values, 0.95) : double.NaN;
        double max = values != null ? Maximum(values) : double.NaN;
        lines.Add(
            name +
            "P95=" + F(p95) +
            " max=" + F(max) +
            " gate=" + F(p95Gate) + "/" + F(maxGate) +
            " pass=" + B(p95 <= p95Gate && max <= maxGate));
    }

    private static void AppendInput(
        List<string> lines,
        string name,
        InputAccumulator input)
    {
        lines.Add("[" + name + "]");
        lines.Add("count=" + input.Count);
        lines.Add("pairCount=" + input.PairCount);
        lines.Add("meanAbsLsb=" + F(input.MeanAbsLsb));
        lines.Add("rmseLsb=" + F(input.RmseLsb));
        lines.Add("exactRatio=" + F(input.ExactMatchRatio));
        lines.Add("p95LsbApprox=" + F(input.Percentile(0.95)));
        lines.Add("p99LsbApprox=" + F(input.Percentile(0.99)));
        lines.Add("maxLsb=" + F(input.MaxAbsLsb));
        for (int i = 0; i < Thresholds.Length; i++)
        {
            int threshold = Thresholds[i];
            lines.Add("ge" + threshold + "Count=" + input.ThresholdCount(threshold));
        }
    }

    private static void AppendFootprints(
        List<string> lines,
        FootprintAccumulator footprints)
    {
        lines.Add("[OUTLIER_FOOTPRINTS]");
        for (int i = 0; i < Thresholds.Length; i++)
        {
            int threshold = Thresholds[i];
            long count = footprints.CountAt(threshold);
            lines.Add("ge" + threshold + "Count=" + count);
            lines.Add("ge" + threshold + "AnyTapOob=" + footprints.AnyTapOobAt(threshold));
            lines.Add("ge" + threshold + "AnyTapOobRatio=" + F(Ratio(footprints.AnyTapOobAt(threshold), count)));
            lines.Add("ge" + threshold + "CenterOob=" + footprints.CenterOobAt(threshold));
            lines.Add("ge" + threshold + "CenterOobRatio=" + F(Ratio(footprints.CenterOobAt(threshold), count)));
            lines.Add("ge" + threshold + "ClampTargetTapCount=" + footprints.ClampTapCountAt(threshold));
            lines.Add("ge" + threshold + "Bottom32=" + footprints.Bottom32At(threshold));
            lines.Add("ge" + threshold + "Bottom32Ratio=" + F(Ratio(footprints.Bottom32At(threshold), count)));
            lines.Add("ge" + threshold + "LeftBoundary=" + footprints.LeftAt(threshold));
            lines.Add("ge" + threshold + "RightBoundary=" + footprints.RightAt(threshold));
            lines.Add("ge" + threshold + "TopBoundary=" + footprints.TopAt(threshold));
            lines.Add("ge" + threshold + "BottomBoundary=" + footprints.BottomAt(threshold));
        }
    }

    private void RegisterFault(string reason)
    {
        _observerFaultCount++;
        Debug.LogWarning(
            "[Kiwi v44.55.21 CropSampler] OBSERVER_FAULT " + reason);
    }

    private void OnDisable()
    {
        if (!_reportWritten && (_bound || _dependencyMissingFrames > 0))
        {
            try
            {
                WriteReport("STOPPED_BEFORE_DEPENDENCY_COMPLETE");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "[Kiwi v44.55.21 CropSampler] STOP_REPORT_FAIL " +
                    exception.GetType().Name +
                    " " + exception.Message);
            }
        }
    }

    private void OnDestroy()
    {
        _outlierWriter?.Dispose();
        _outlierWriter = null;
    }

    private void OnApplicationQuit()
    {
        if (!_reportWritten)
        {
            WriteReport("QUIT_BEFORE_DEPENDENCY_COMPLETE");
        }
    }

    private sealed class InputAccumulator
    {
        private const int HistogramBins = 8193;
        private const double BinsPerLsb = 16.0;
        private readonly long[] _histogram = new long[HistogramBins];
        private readonly long[] _thresholdCounts = new long[Thresholds.Length];
        private long _count;
        private long _exactCount;
        private int _pairCount;
        private double _sumAbs;
        private double _sumSquare;
        private double _max;

        internal long Count => _count;
        internal int PairCount => _pairCount;
        internal double MeanAbsLsb => _count > 0 ? _sumAbs / _count : 0.0;
        internal double RmseLsb => _count > 0 ? Math.Sqrt(_sumSquare / _count) : 0.0;
        internal double MaxAbsLsb => _max;
        internal double ExactMatchRatio => _count > 0 ? _exactCount / (double)_count : 0.0;

        internal PairSummary Accumulate(float[] left, float[] right)
        {
            double pairAbs = 0.0;
            double pairSquare = 0.0;
            double pairMax = 0.0;
            int pairExact = 0;

            for (int i = 0; i < left.Length; i++)
            {
                double delta = ((double)right[i] - left[i]) * 255.0;
                double abs = Math.Abs(delta);
                pairAbs += abs;
                pairSquare += delta * delta;
                pairMax = Math.Max(pairMax, abs);
                _sumAbs += abs;
                _sumSquare += delta * delta;
                _max = Math.Max(_max, abs);
                _count++;

                if (BitConverter.SingleToInt32Bits(left[i]) ==
                    BitConverter.SingleToInt32Bits(right[i]))
                {
                    _exactCount++;
                    pairExact++;
                }

                for (int t = 0; t < Thresholds.Length; t++)
                {
                    if (abs >= Thresholds[t])
                    {
                        _thresholdCounts[t]++;
                    }
                }

                int bin = Mathf.Clamp(
                    (int)Math.Ceiling(abs * BinsPerLsb),
                    0,
                    HistogramBins - 1);
                _histogram[bin]++;
            }

            _pairCount++;
            return new PairSummary
            {
                MeanAbsLsb = pairAbs / left.Length,
                RmseLsb = Math.Sqrt(pairSquare / left.Length),
                MaxAbsLsb = pairMax,
                ExactCount = pairExact
            };
        }

        internal long ThresholdCount(int threshold)
        {
            for (int i = 0; i < Thresholds.Length; i++)
            {
                if (Thresholds[i] == threshold)
                {
                    return _thresholdCounts[i];
                }
            }
            return 0;
        }

        internal double Percentile(double percentile)
        {
            if (_count <= 0)
            {
                return 0.0;
            }

            long target = (long)Math.Ceiling(percentile * _count);
            long cumulative = 0;
            for (int i = 0; i < _histogram.Length; i++)
            {
                cumulative += _histogram[i];
                if (cumulative >= target)
                {
                    return i / BinsPerLsb;
                }
            }
            return _max;
        }
    }

    private struct PairSummary
    {
        internal double MeanAbsLsb;
        internal double RmseLsb;
        internal double MaxAbsLsb;
        internal int ExactCount;
    }

    private sealed class FootprintAccumulator
    {
        private readonly long[] _count = new long[Thresholds.Length];
        private readonly long[] _anyTapOob = new long[Thresholds.Length];
        private readonly long[] _centerOob = new long[Thresholds.Length];
        private readonly long[] _clampTapCount = new long[Thresholds.Length];
        private readonly long[] _bottom32 = new long[Thresholds.Length];
        private readonly long[] _left = new long[Thresholds.Length];
        private readonly long[] _right = new long[Thresholds.Length];
        private readonly long[] _top = new long[Thresholds.Length];
        private readonly long[] _bottom = new long[Thresholds.Length];

        internal int LastLargeCount { get; private set; }
        internal int LastAnyTapOobCount { get; private set; }
        internal int LastCenterOobCount { get; private set; }

        internal void BeginPair()
        {
            LastLargeCount = 0;
            LastAnyTapOobCount = 0;
            LastCenterOobCount = 0;
        }

        internal void Accumulate(double absLsb, int tensorY, Footprint p)
        {
            for (int i = 0; i < Thresholds.Length; i++)
            {
                if (absLsb < Thresholds[i])
                {
                    continue;
                }

                _count[i]++;
                if (p.AnyTapOutOfBounds) _anyTapOob[i]++;
                if (p.CenterOutOfBounds) _centerOob[i]++;
                _clampTapCount[i] += p.ClampTargetTapCount;
                if (tensorY >= InputSize - 32) _bottom32[i]++;
                if (p.LeftBoundary) _left[i]++;
                if (p.RightBoundary) _right[i]++;
                if (p.TopBoundary) _top[i]++;
                if (p.BottomBoundary) _bottom[i]++;

                if (Thresholds[i] == 4)
                {
                    LastLargeCount++;
                    if (p.AnyTapOutOfBounds) LastAnyTapOobCount++;
                    if (p.CenterOutOfBounds) LastCenterOobCount++;
                }
            }
        }

        internal long CountAt(int threshold) => Get(_count, threshold);
        internal long AnyTapOobAt(int threshold) => Get(_anyTapOob, threshold);
        internal long CenterOobAt(int threshold) => Get(_centerOob, threshold);
        internal long ClampTapCountAt(int threshold) => Get(_clampTapCount, threshold);
        internal long Bottom32At(int threshold) => Get(_bottom32, threshold);
        internal long LeftAt(int threshold) => Get(_left, threshold);
        internal long RightAt(int threshold) => Get(_right, threshold);
        internal long TopAt(int threshold) => Get(_top, threshold);
        internal long BottomAt(int threshold) => Get(_bottom, threshold);

        private static long Get(long[] values, int threshold)
        {
            for (int i = 0; i < Thresholds.Length; i++)
            {
                if (Thresholds[i] == threshold)
                {
                    return values[i];
                }
            }
            return 0;
        }
    }

    private struct Footprint
    {
        internal double CropU;
        internal double CropVBottom;
        internal double RawU;
        internal double RawVBottom;
        internal double ClampedU;
        internal double ClampedVBottom;
        internal double RawSourceX;
        internal double RawSourceTopY;
        internal double SourceX;
        internal double SourceTopY;
        internal int X0;
        internal int Y0;
        internal int X1;
        internal int Y1;
        internal double FracX;
        internal double FracY;
        internal bool Tap00InBounds;
        internal bool Tap10InBounds;
        internal bool Tap01InBounds;
        internal bool Tap11InBounds;
        internal bool AnyTapOutOfBounds;
        internal bool CenterOutOfBounds;
        internal int ClampTargetTapCount;
        internal bool LeftBoundary;
        internal bool RightBoundary;
        internal bool TopBoundary;
        internal bool BottomBoundary;
        internal double SourceEdgeDistanceTexels;

        internal static Footprint Create(
            double cropU,
            double cropVBottom,
            double rawU,
            double rawVBottom,
            int width,
            int height)
        {
            double clampedU = Math.Max(0.0, Math.Min(1.0, rawU));
            double clampedV = Math.Max(0.0, Math.Min(1.0, rawVBottom));
            double rawSourceX = rawU * width - 0.5;
            double rawSourceTopY = (1.0 - rawVBottom) * height - 0.5;
            double sourceX = clampedU * width - 0.5;
            double sourceTopY = (1.0 - clampedV) * height - 0.5;
            int x0 = (int)Math.Floor(sourceX);
            int y0 = (int)Math.Floor(sourceTopY);
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            bool tap00 = InBounds(x0, y0, width, height);
            bool tap10 = InBounds(x1, y0, width, height);
            bool tap01 = InBounds(x0, y1, width, height);
            bool tap11 = InBounds(x1, y1, width, height);
            int clampCount =
                (tap00 ? 0 : 1) +
                (tap10 ? 0 : 1) +
                (tap01 ? 0 : 1) +
                (tap11 ? 0 : 1);

            return new Footprint
            {
                CropU = cropU,
                CropVBottom = cropVBottom,
                RawU = rawU,
                RawVBottom = rawVBottom,
                ClampedU = clampedU,
                ClampedVBottom = clampedV,
                RawSourceX = rawSourceX,
                RawSourceTopY = rawSourceTopY,
                SourceX = sourceX,
                SourceTopY = sourceTopY,
                X0 = x0,
                Y0 = y0,
                X1 = x1,
                Y1 = y1,
                FracX = sourceX - x0,
                FracY = sourceTopY - y0,
                Tap00InBounds = tap00,
                Tap10InBounds = tap10,
                Tap01InBounds = tap01,
                Tap11InBounds = tap11,
                AnyTapOutOfBounds = clampCount > 0,
                CenterOutOfBounds =
                    rawU < 0.0 || rawU > 1.0 ||
                    rawVBottom < 0.0 || rawVBottom > 1.0,
                ClampTargetTapCount = clampCount,
                LeftBoundary = x0 < 0 || x1 < 0,
                RightBoundary = x0 >= width || x1 >= width,
                TopBoundary = y0 < 0 || y1 < 0,
                BottomBoundary = y0 >= height || y1 >= height,
                SourceEdgeDistanceTexels =
                    Math.Min(
                        Math.Min(rawSourceX, width - 1.0 - rawSourceX),
                        Math.Min(rawSourceTopY, height - 1.0 - rawSourceTopY))
            };
        }

        private static bool InBounds(int x, int y, int width, int height)
        {
            return x >= 0 && x < width && y >= 0 && y < height;
        }
    }

    private static FieldInfo RequiredField(Type type, string name)
    {
        FieldInfo field = type.GetField(name, InstancePrivate);
        if (field == null)
        {
            throw new MissingFieldException(type.FullName, name);
        }
        return field;
    }

    private static object ReadMemberObject(object target, string name)
    {
        if (target == null)
        {
            return null;
        }

        FieldInfo field =
            target.GetType().GetField(
                name,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic);

        return field != null ? field.GetValue(target) : null;
    }

    private static int ReadMemberInt(object target, string name)
    {
        object value = ReadMemberObject(target, name);
        return value is int number ? number : 0;
    }

    private static long ReadMemberLong(object target, string name)
    {
        object value = ReadMemberObject(target, name);
        return value is long number ? number : 0L;
    }

    private static ulong ReadMemberULong(object target, string name)
    {
        object value = ReadMemberObject(target, name);
        return value is ulong number ? number : 0UL;
    }

    private static bool ReadMemberBool(object target, string name)
    {
        object value = ReadMemberObject(target, name);
        return value is bool flag && flag;
    }

    private static double ReadMemberDouble(object target, string name)
    {
        object value = ReadMemberObject(target, name);
        return value is double number ? number : 0.0;
    }

    private static int ReadFieldInt(object target, FieldInfo field)
    {
        if (target == null || field == null)
        {
            return 0;
        }
        object value = field.GetValue(target);
        return value is int number ? number : 0;
    }

    private static bool ReadFieldBool(object target, FieldInfo field)
    {
        if (target == null || field == null)
        {
            return false;
        }
        object value = field.GetValue(target);
        return value is bool flag && flag;
    }

    private static double Percentile(IList values, double percentile)
    {
        if (values == null || values.Count == 0)
        {
            return double.NaN;
        }

        double[] copy = new double[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            copy[i] = Convert.ToDouble(values[i], CultureInfo.InvariantCulture);
        }
        Array.Sort(copy);
        int index = Mathf.Clamp(
            (int)Math.Ceiling(percentile * copy.Length) - 1,
            0,
            copy.Length - 1);
        return copy[index];
    }

    private static double Maximum(IList values)
    {
        if (values == null || values.Count == 0)
        {
            return double.NaN;
        }

        double max = double.NegativeInfinity;
        for (int i = 0; i < values.Count; i++)
        {
            max = Math.Max(
                max,
                Convert.ToDouble(values[i], CultureInfo.InvariantCulture));
        }
        return max;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static double Ratio(long numerator, long denominator)
    {
        return denominator > 0 ? numerator / (double)denominator : 0.0;
    }

    private static string JoinCsv(params object[] values)
    {
        StringBuilder builder = new StringBuilder(values.Length * 12);
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) builder.Append(',');
            object value = values[i];
            if (value is bool flag)
            {
                builder.Append(flag ? '1' : '0');
            }
            else if (value is float single)
            {
                builder.Append(single.ToString("R", CultureInfo.InvariantCulture));
            }
            else if (value is double number)
            {
                builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
            }
            else if (value is IFormattable formattable)
            {
                builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(value != null ? value.ToString() : string.Empty);
            }
        }
        return builder.ToString();
    }

    private static string F(double value)
    {
        return value.ToString("F9", CultureInfo.InvariantCulture);
    }

    private static string B(bool value)
    {
        return value ? "1" : "0";
    }

    private static bool ReadBoolEnvironment(string name, bool fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        value = value.Trim();
        return
            string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }
}
