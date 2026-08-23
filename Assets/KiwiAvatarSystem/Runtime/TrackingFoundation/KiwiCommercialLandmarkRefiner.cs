using System;
using UnityEngine;

/// <summary>
/// Phase 16.7 commercial semantic landmark precision layer.
///
/// The detector remains the source of truth. This class never invents a second
/// tracking provider and never feeds Eye/Mouth data back into the rigid Root.
/// It removes only presentation-local landmark noise after the canonical frame
/// identity has already been validated.
///
/// Core idea:
/// - solve one face-local similarity basis from the two eye centres;
/// - express all landmarks in that basis, so global translation/roll/scale pass
///   through immediately and are not mistaken for jitter;
/// - pass coherent eye/mouth/brow/iris group motion directly;
/// - attenuate only tiny, direction-incoherent residual motion;
/// - hold one catastrophic isolated contour-point shock, then reacquire on a
///   second mutually-consistent sample.
///
/// This is not a frame buffer and it adds no global prediction. Holding/Lost
/// therefore still has prediction = 0 through the existing tracking policy.
/// </summary>
public static class KiwiCommercialLandmarkRefiner
{
    // KIWI_V5_1_PHASE16_7_COMMERCIAL_LANDMARK_REFINER
    private const int MaxLandmarks = 512;
    private const int NoGroup = -1;

    private const int GroupEyeA = 0;
    private const int GroupEyeB = 1;
    private const int GroupMouth = 2;
    private const int GroupBrowA = 3;
    private const int GroupBrowB = 4;
    private const int GroupIrisA = 5;
    private const int GroupIrisB = 6;
    private const int GroupCount = 7;

    // Thresholds are measured in inter-eye-centre spans. This makes behaviour
    // stable across camera resolutions and subject distance.
    private const float StructuralMicroStart = 0.0035f;
    private const float StructuralMotionFull = 0.0200f;
    private const float StructuralMinimumGain = 0.18f;

    private const float DynamicMicroStart = 0.0022f;
    private const float DynamicMotionFull = 0.0140f;
    private const float DynamicMinimumGain = 0.28f;

    private const float IrisMicroStart = 0.0016f;
    private const float IrisMotionFull = 0.0100f;
    private const float IrisMinimumGain = 0.38f;

    // Only an isolated contour point can hit this path. Shared contour motion,
    // blink, speech and normal perspective deformation are preserved.
    // KIWI_V5_1_PHASE16_8_LANDMARK_REFINER_FALSE_POSITIVE_GUARD
    // Phase 16.7 field data showed that perspective/yaw changes could make
    // dozens of structural vertices look "isolated" at once. Commercial
    // tracking must never freeze a large part of the face mesh. Thresholds are
    // therefore stricter and a per-sample mass-reject bypass is enforced.
    private const float IsolatedPointResidualThreshold = 0.140f;
    private const float IsolatedPointPredictionErrorThreshold = 0.100f;
    private const float IsolatedNeighborAgreementThreshold = 0.035f;
    private const float PendingPointConsistencyThreshold = 0.050f;

    private const float StructuralNeighborResidualThreshold = 0.180f;
    private const float StructuralNeighborPredictionErrorThreshold = 0.140f;
    private const float StructuralNeighborAgreementThreshold = 0.035f;

    private const int MaximumIsolatedRejectsPerSample = 4;

    private const float GroupOutlierTrimThreshold = 0.080f;
    private const float MaximumStateGapSeconds = 0.25f;

    private static readonly int[] EyeA =
    {
        362, 398, 384, 385, 386, 387, 388, 466,
        263, 249, 390, 373, 374, 380, 381, 382
    };

    private static readonly int[] EyeB =
    {
        33, 246, 161, 160, 159, 158, 157, 173,
        133, 155, 154, 153, 145, 144, 163, 7
    };

    private static readonly int[] Mouth =
    {
        61, 185, 40, 39, 37, 0, 267, 269, 270, 409, 291,
        375, 321, 405, 314, 17, 84, 181, 91, 146
    };

    private static readonly int[] BrowA =
    {
        276, 283, 282, 295, 285, 300, 293, 334, 296, 336
    };

    private static readonly int[] BrowB =
    {
        46, 53, 52, 65, 55, 107, 66, 105, 63, 70
    };

    private static readonly int[] IrisA =
    {
        468, 469, 470, 471, 472
    };

    private static readonly int[] IrisB =
    {
        473, 474, 475, 476, 477
    };

    private static readonly Vector2[] CurrentLocal =
        new Vector2[MaxLandmarks];

    private static readonly Vector2[] LastRawLocal =
        new Vector2[MaxLandmarks];

    private static readonly Vector2[] LastRefinedLocal =
        new Vector2[MaxLandmarks];

    private static readonly Vector2[] LastRawDeltaLocal =
        new Vector2[MaxLandmarks];

    private static readonly Vector2[] PendingOutlierLocal =
        new Vector2[MaxLandmarks];

    private static readonly byte[] PendingOutlierStreak =
        new byte[MaxLandmarks];

    // KIWI_V5_1_PHASE16_9_STABLE_ISOLATED_CANDIDATE_SNAPSHOT
    // Candidate classification must be immutable for the whole semantic sample.
    // Phase 16.8 counted candidates first but re-ran the predicate while the
    // refinement loop was already mutating LastRawLocal. That made later points
    // see a different neighbour history and allowed rejectCount to exceed the
    // pre-count (observed candidate<=2 but rejects up to 61).
    private static readonly bool[] IsolatedCandidateByLandmark =
        new bool[MaxLandmarks];

    private static readonly sbyte[] GroupByLandmark =
        new sbyte[MaxLandmarks];

    private static readonly short[] PreviousContourNeighbor =
        new short[MaxLandmarks];

    private static readonly short[] NextContourNeighbor =
        new short[MaxLandmarks];

    // Face-local nearest-neighbour graph is rebuilt only on identity reset. It
    // covers structural points that are not part of the explicit eye/mouth
    // contours, allowing a single nose/cheek/oval point spike to be held too.
    private static readonly short[] StructuralNeighborA =
        new short[MaxLandmarks];

    private static readonly short[] StructuralNeighborB =
        new short[MaxLandmarks];

    private static readonly short[] StructuralNeighborC =
        new short[MaxLandmarks];

    private static readonly Vector2[] GroupDelta =
        new Vector2[GroupCount];

    private static readonly int[] GroupValidCount =
        new int[GroupCount];

    private static bool _initialized;
    private static int _lastLandmarkCount;
    private static long _lastTimestamp = -1L;
    private static string _lastProviderId = string.Empty;
    private static int _lastCameraGeneration;
    private static int _lastProviderGeneration;
    private static int _lastModelGeneration;
    private static int _lastConfigEpoch;
    private static int _lastCalibrationGeneration;
    private static int _lastTrackingSessionGeneration;

    private static int _totalSamples;
    private static int _totalRefinedPoints;
    private static int _totalIsolatedPointRejects;
    private static int _lastRefinedPointCount;
    private static int _lastIsolatedPointRejectCount;
    private static int _lastIsolatedCandidateCount;
    private static bool _lastMassRejectBypass;
    private static int _totalMassRejectBypasses;
    private static float _lastMeanAdjustmentEyeSpans;
    private static float _lastMaxAdjustmentEyeSpans;
    private static float _lastGeometryQuality;
    private static long _lastRefinedTimestamp = -1L;

    public static int TotalSamples => _totalSamples;
    public static int TotalRefinedPoints => _totalRefinedPoints;
    public static int TotalIsolatedPointRejects => _totalIsolatedPointRejects;
    public static int LastRefinedPointCount => _lastRefinedPointCount;
    public static int LastIsolatedPointRejectCount => _lastIsolatedPointRejectCount;
    public static int LastIsolatedCandidateCount => _lastIsolatedCandidateCount;
    public static bool LastMassRejectBypass => _lastMassRejectBypass;
    public static int TotalMassRejectBypasses => _totalMassRejectBypasses;
    public static float LastMeanAdjustmentEyeSpans => _lastMeanAdjustmentEyeSpans;
    public static float LastMaxAdjustmentEyeSpans => _lastMaxAdjustmentEyeSpans;
    public static float LastGeometryQuality => _lastGeometryQuality;
    public static long LastRefinedTimestamp => _lastRefinedTimestamp;

    static KiwiCommercialLandmarkRefiner()
    {
        for (int i = 0; i < MaxLandmarks; i++)
        {
            GroupByLandmark[i] = NoGroup;
            PreviousContourNeighbor[i] = -1;
            NextContourNeighbor[i] = -1;
            StructuralNeighborA[i] = -1;
            StructuralNeighborB[i] = -1;
            StructuralNeighborC[i] = -1;
        }

        RegisterContour(EyeA, GroupEyeA, true);
        RegisterContour(EyeB, GroupEyeB, true);
        RegisterContour(Mouth, GroupMouth, true);
        RegisterContour(BrowA, GroupBrowA, false);
        RegisterContour(BrowB, GroupBrowB, false);
        RegisterContour(IrisA, GroupIrisA, false);
        RegisterContour(IrisB, GroupIrisB, false);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetRuntimeState()
    {
        _initialized = false;
        _lastLandmarkCount = 0;
        _lastTimestamp = -1L;
        _lastProviderId = string.Empty;
        _lastCameraGeneration = 0;
        _lastProviderGeneration = 0;
        _lastModelGeneration = 0;
        _lastConfigEpoch = 0;
        _lastCalibrationGeneration = 0;
        _lastTrackingSessionGeneration = 0;

        _totalSamples = 0;
        _totalRefinedPoints = 0;
        _totalIsolatedPointRejects = 0;
        _lastRefinedPointCount = 0;
        _lastIsolatedPointRejectCount = 0;
        _lastIsolatedCandidateCount = 0;
        _lastMassRejectBypass = false;
        _totalMassRejectBypasses = 0;
        _lastMeanAdjustmentEyeSpans = 0f;
        _lastMaxAdjustmentEyeSpans = 0f;
        _lastGeometryQuality = 0f;
        _lastRefinedTimestamp = -1L;

        Array.Clear(PendingOutlierStreak, 0, PendingOutlierStreak.Length);
        Array.Clear(LastRawDeltaLocal, 0, LastRawDeltaLocal.Length);
    }

    /// <summary>
    /// Refine one already identity-matched canonical semantic sample.
    /// destination is caller-owned and never aliases the producer buffer.
    /// </summary>
    public static bool TryRefine(
        Vector2[] raw,
        int count,
        long timestamp,
        string providerId,
        KiwiRuntimeGenerationContext.Snapshot generation,
        float geometryQuality,
        ref Vector2[] destination,
        out int refinedCount)
    {
        refinedCount = 0;

        if (
            raw == null ||
            count <= 362 ||
            count > MaxLandmarks ||
            timestamp < 0L)
        {
            return false;
        }

        if (
            destination == null ||
            destination.Length < count)
        {
            destination = new Vector2[count];
        }

        if (!TryBuildFaceBasis(
                raw,
                count,
                out Vector2 origin,
                out Vector2 axisX,
                out Vector2 axisY,
                out float eyeSpan))
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            Vector2 point = raw[i];
            if (!IsFinite(point))
            {
                return false;
            }

            Vector2 delta = point - origin;
            CurrentLocal[i] = new Vector2(
                Vector2.Dot(delta, axisX) / eyeSpan,
                Vector2.Dot(delta, axisY) / eyeSpan);
        }

        providerId = providerId ?? string.Empty;

        bool identityChanged =
            !_initialized ||
            _lastLandmarkCount != count ||
            !string.Equals(
                _lastProviderId,
                providerId,
                StringComparison.Ordinal) ||
            _lastCameraGeneration != generation.cameraGeneration ||
            _lastProviderGeneration != generation.providerGeneration ||
            _lastModelGeneration != generation.modelGeneration ||
            _lastConfigEpoch != generation.configEpoch ||
            _lastCalibrationGeneration != generation.calibrationGeneration ||
            _lastTrackingSessionGeneration != generation.trackingSessionGeneration ||
            timestamp <= _lastTimestamp;

        float dt =
            _lastTimestamp >= 0L && timestamp > _lastTimestamp
                ? Mathf.Clamp((timestamp - _lastTimestamp) / 1000f, 1f / 240f, 1f)
                : 0f;

        if (
            identityChanged ||
            dt <= 0f ||
            dt > MaximumStateGapSeconds)
        {
            ResetToRaw(
                raw,
                count,
                timestamp,
                providerId,
                generation,
                geometryQuality,
                destination);

            refinedCount = count;
            return true;
        }

        CalculateAllGroupDeltas(count);

        int isolatedCandidateCount =
            CountPotentialIsolatedPointShocks(count);

        bool massRejectBypass =
            isolatedCandidateCount >
                MaximumIsolatedRejectsPerSample;

        if (massRejectBypass)
        {
            // A true one-point detector glitch is sparse. If many vertices fire
            // together the detector is describing real perspective/expression
            // deformation (or the face basis moved), so preserving raw geometry
            // is more accurate than freezing a patch of the face.
            _totalMassRejectBypasses++;
        }

        geometryQuality =
            float.IsNaN(geometryQuality) ||
            float.IsInfinity(geometryQuality)
                ? 0.5f
                : Mathf.Clamp01(geometryQuality);

        int refinedPoints = 0;
        int isolatedRejects = 0;
        float adjustmentSum = 0f;
        float adjustmentMax = 0f;

        for (int i = 0; i < count; i++)
        {
            int group = GroupByLandmark[i];

            Vector2 coherentDelta =
                group >= 0 && group < GroupCount
                    ? GroupDelta[group]
                    : Vector2.zero;

            Vector2 predicted =
                LastRefinedLocal[i] + coherentDelta;

            Vector2 rawLocal = CurrentLocal[i];
            Vector2 rawDelta = rawLocal - LastRawLocal[i];
            Vector2 predictionError = rawLocal - predicted;

            // KIWI_V5_1_PHASE16_9_APPLY_STABLE_ISOLATED_CANDIDATE
            // Never re-evaluate neighbour topology after earlier points in this
            // same loop have already advanced LastRawLocal/LastRefinedLocal.
            bool isolatedShock =
                !massRejectBypass &&
                IsolatedCandidateByLandmark[i];

            bool rejectThisSample = false;

            if (isolatedShock)
            {
                if (
                    PendingOutlierStreak[i] > 0 &&
                    Vector2.Distance(
                        PendingOutlierLocal[i],
                        rawLocal) <= PendingPointConsistencyThreshold)
                {
                    PendingOutlierStreak[i]++;
                }
                else
                {
                    PendingOutlierLocal[i] = rawLocal;
                    PendingOutlierStreak[i] = 1;
                }

                // One isolated point cannot be trusted. A genuine persistent
                // contour deformation is reacquired on the second consistent
                // sample with no long-lived freeze.
                if (PendingOutlierStreak[i] < 2)
                {
                    rejectThisSample = true;
                    isolatedRejects++;
                    _totalIsolatedPointRejects++;
                }
                else
                {
                    PendingOutlierStreak[i] = 0;
                }
            }
            else
            {
                PendingOutlierStreak[i] = 0;
            }

            Vector2 refinedLocal;

            if (rejectThisSample)
            {
                refinedLocal = predicted;
            }
            else
            {
                GetPointFilterParameters(
                    group,
                    geometryQuality,
                    out float microStart,
                    out float motionFull,
                    out float minimumGain);

                float motionMagnitude =
                    Mathf.Max(
                        rawDelta.magnitude,
                        predictionError.magnitude);

                float release =
                    Mathf.InverseLerp(
                        microStart,
                        motionFull,
                        motionMagnitude);

                release = SmoothStep01(release);

                float gain =
                    Mathf.Lerp(
                        minimumGain,
                        1f,
                        release);

                // Slow real motion has direction persistence while sensor/model
                // jitter is direction-incoherent. Raise the gain immediately for
                // coherent motion so this filter does not create rubber-band lag.
                Vector2 previousRawDelta = LastRawDeltaLocal[i];
                float rawMagnitude = rawDelta.magnitude;
                float previousMagnitude = previousRawDelta.magnitude;

                if (
                    rawMagnitude > microStart &&
                    previousMagnitude > microStart)
                {
                    float direction =
                        Vector2.Dot(
                            rawDelta / rawMagnitude,
                            previousRawDelta / previousMagnitude);

                    if (direction > 0.55f)
                    {
                        float coherentRelease =
                            Mathf.InverseLerp(0.55f, 0.92f, direction);

                        float coherentGain =
                            Mathf.Lerp(0.72f, 0.96f, coherentRelease);

                        gain = Mathf.Max(gain, coherentGain);
                    }
                }

                // Any clearly non-micro deformation is direct. Blink, speech,
                // gaze and abrupt expression therefore do not wait on a temporal
                // low-pass stage.
                if (predictionError.magnitude >= motionFull * 1.35f)
                {
                    gain = 1f;
                }

                refinedLocal =
                    Vector2.LerpUnclamped(
                        predicted,
                        rawLocal,
                        gain);
            }

            Vector2 adjustment =
                refinedLocal - rawLocal;

            float adjustmentMagnitude =
                adjustment.magnitude;

            if (adjustmentMagnitude > 0.000001f)
            {
                refinedPoints++;
            }

            adjustmentSum += adjustmentMagnitude;
            adjustmentMax = Mathf.Max(adjustmentMax, adjustmentMagnitude);

            destination[i] =
                origin +
                axisX * (refinedLocal.x * eyeSpan) +
                axisY * (refinedLocal.y * eyeSpan);

            if (!rejectThisSample)
            {
                LastRawDeltaLocal[i] = rawDelta;
                LastRawLocal[i] = rawLocal;
                LastRefinedLocal[i] = refinedLocal;
            }
            else
            {
                // Keep the last trusted raw point so a one-frame glitch that
                // returns to normal does not create an artificial opposite shock.
                LastRefinedLocal[i] = refinedLocal;
            }
        }

        _initialized = true;
        _lastLandmarkCount = count;
        _lastTimestamp = timestamp;
        _lastProviderId = providerId;
        _lastCameraGeneration = generation.cameraGeneration;
        _lastProviderGeneration = generation.providerGeneration;
        _lastModelGeneration = generation.modelGeneration;
        _lastConfigEpoch = generation.configEpoch;
        _lastCalibrationGeneration = generation.calibrationGeneration;
        _lastTrackingSessionGeneration = generation.trackingSessionGeneration;

        _totalSamples++;
        _totalRefinedPoints += refinedPoints;
        _lastRefinedPointCount = refinedPoints;
        _lastIsolatedPointRejectCount = isolatedRejects;
        _lastIsolatedCandidateCount = isolatedCandidateCount;
        _lastMassRejectBypass = massRejectBypass;
        _lastMeanAdjustmentEyeSpans = adjustmentSum / Mathf.Max(1, count);
        _lastMaxAdjustmentEyeSpans = adjustmentMax;
        _lastGeometryQuality = geometryQuality;
        _lastRefinedTimestamp = timestamp;

        refinedCount = count;
        return true;
    }

    private static void ResetToRaw(
        Vector2[] raw,
        int count,
        long timestamp,
        string providerId,
        KiwiRuntimeGenerationContext.Snapshot generation,
        float geometryQuality,
        Vector2[] destination)
    {
        for (int i = 0; i < count; i++)
        {
            destination[i] = raw[i];
            LastRawLocal[i] = CurrentLocal[i];
            LastRefinedLocal[i] = CurrentLocal[i];
            LastRawDeltaLocal[i] = Vector2.zero;
            PendingOutlierStreak[i] = 0;
        }

        BuildStructuralNeighborGraph(count);

        _initialized = true;
        _lastLandmarkCount = count;
        _lastTimestamp = timestamp;
        _lastProviderId = providerId;
        _lastCameraGeneration = generation.cameraGeneration;
        _lastProviderGeneration = generation.providerGeneration;
        _lastModelGeneration = generation.modelGeneration;
        _lastConfigEpoch = generation.configEpoch;
        _lastCalibrationGeneration = generation.calibrationGeneration;
        _lastTrackingSessionGeneration = generation.trackingSessionGeneration;

        _totalSamples++;
        _lastRefinedPointCount = 0;
        _lastIsolatedPointRejectCount = 0;
        _lastIsolatedCandidateCount = 0;
        _lastMassRejectBypass = false;
        _lastMeanAdjustmentEyeSpans = 0f;
        _lastMaxAdjustmentEyeSpans = 0f;
        _lastGeometryQuality =
            float.IsNaN(geometryQuality) ||
            float.IsInfinity(geometryQuality)
                ? 0f
                : Mathf.Clamp01(geometryQuality);
        _lastRefinedTimestamp = timestamp;
    }

    private static void CalculateAllGroupDeltas(int count)
    {
        for (int group = 0; group < GroupCount; group++)
        {
            GroupDelta[group] = Vector2.zero;
            GroupValidCount[group] = 0;
        }

        CalculateGroupDelta(EyeA, GroupEyeA, count);
        CalculateGroupDelta(EyeB, GroupEyeB, count);
        CalculateGroupDelta(Mouth, GroupMouth, count);
        CalculateGroupDelta(BrowA, GroupBrowA, count);
        CalculateGroupDelta(BrowB, GroupBrowB, count);
        CalculateGroupDelta(IrisA, GroupIrisA, count);
        CalculateGroupDelta(IrisB, GroupIrisB, count);
    }

    private static void CalculateGroupDelta(
        int[] indices,
        int group,
        int count)
    {
        Vector2 firstMean = Vector2.zero;
        int firstCount = 0;

        for (int n = 0; n < indices.Length; n++)
        {
            int index = indices[n];
            if (index < 0 || index >= count)
            {
                continue;
            }

            firstMean += CurrentLocal[index] - LastRawLocal[index];
            firstCount++;
        }

        if (firstCount <= 0)
        {
            return;
        }

        firstMean /= firstCount;

        Vector2 trimmedMean = Vector2.zero;
        int trimmedCount = 0;

        for (int n = 0; n < indices.Length; n++)
        {
            int index = indices[n];
            if (index < 0 || index >= count)
            {
                continue;
            }

            Vector2 delta = CurrentLocal[index] - LastRawLocal[index];

            if (
                firstCount <= 3 ||
                Vector2.Distance(delta, firstMean) <= GroupOutlierTrimThreshold)
            {
                trimmedMean += delta;
                trimmedCount++;
            }
        }

        if (trimmedCount <= 0)
        {
            GroupDelta[group] = firstMean;
            GroupValidCount[group] = firstCount;
            return;
        }

        GroupDelta[group] = trimmedMean / trimmedCount;
        GroupValidCount[group] = trimmedCount;
    }

    private static int CountPotentialIsolatedPointShocks(int count)
    {
        int candidateCount = 0;

        // KIWI_V5_1_PHASE16_9_STABLE_ISOLATED_CANDIDATE_PASS
        // Evaluate every point against the same previous-sample state, then keep
        // that boolean snapshot unchanged until the whole sample is committed.
        for (int i = 0; i < count; i++)
        {
            int group = GroupByLandmark[i];

            Vector2 coherentDelta =
                group >= 0 && group < GroupCount
                    ? GroupDelta[group]
                    : Vector2.zero;

            Vector2 predicted =
                LastRefinedLocal[i] + coherentDelta;

            Vector2 rawLocal =
                CurrentLocal[i];

            Vector2 rawDelta =
                rawLocal - LastRawLocal[i];

            Vector2 predictionError =
                rawLocal - predicted;

            bool candidate =
                IsIsolatedContourPointShock(
                    i,
                    count,
                    rawDelta,
                    predictionError);

            IsolatedCandidateByLandmark[i] = candidate;

            if (candidate)
            {
                candidateCount++;
            }
        }

        // Clear unused tail values so a later sample with fewer landmarks cannot
        // expose stale diagnostic state if the model changes.
        for (int i = count; i < MaxLandmarks; i++)
        {
            IsolatedCandidateByLandmark[i] = false;
        }

        return candidateCount;
    }

    private static bool IsIsolatedContourPointShock(
        int index,
        int count,
        Vector2 rawDelta,
        Vector2 predictionError)
    {
        int group = GroupByLandmark[index];

        // Iris centre/rim points are allowed to move independently during gaze.
        // Applying an isolated-point guard there would trade accuracy for a
        // visually stable but incorrect pupil position.
        if (group == GroupIrisA || group == GroupIrisB)
        {
            return false;
        }

        int previous = PreviousContourNeighbor[index];
        int next = NextContourNeighbor[index];

        if (
            previous >= 0 &&
            next >= 0 &&
            previous < count &&
            next < count)
        {
            Vector2 previousDelta =
                CurrentLocal[previous] - LastRawLocal[previous];

            Vector2 nextDelta =
                CurrentLocal[next] - LastRawLocal[next];

            Vector2 neighborMean =
                (previousDelta + nextDelta) * 0.5f;

            float isolatedResidual =
                Vector2.Distance(rawDelta, neighborMean);

            float neighborAgreement =
                Vector2.Distance(previousDelta, nextDelta);

            return
                isolatedResidual >= IsolatedPointResidualThreshold &&
                predictionError.magnitude >= IsolatedPointPredictionErrorThreshold &&
                neighborAgreement <= IsolatedNeighborAgreementThreshold;
        }

        // Non-contour structural points use the identity-local nearest-neighbour
        // graph built from the first trusted frame. It is intentionally broad
        // and only catches a one-point shock while all three neighbours agree.
        int a = StructuralNeighborA[index];
        int b = StructuralNeighborB[index];
        int c = StructuralNeighborC[index];

        if (
            a < 0 || b < 0 || c < 0 ||
            a >= count || b >= count || c >= count)
        {
            return false;
        }

        Vector2 da = CurrentLocal[a] - LastRawLocal[a];
        Vector2 db = CurrentLocal[b] - LastRawLocal[b];
        Vector2 dc = CurrentLocal[c] - LastRawLocal[c];

        float neighborAgreementMax = Mathf.Max(
            Vector2.Distance(da, db),
            Mathf.Max(
                Vector2.Distance(db, dc),
                Vector2.Distance(dc, da)));

        if (neighborAgreementMax > StructuralNeighborAgreementThreshold)
        {
            return false;
        }

        Vector2 structuralMean = (da + db + dc) / 3f;

        return
            Vector2.Distance(rawDelta, structuralMean) >=
                StructuralNeighborResidualThreshold &&
            predictionError.magnitude >=
                StructuralNeighborPredictionErrorThreshold;
    }

    private static void BuildStructuralNeighborGraph(int count)
    {
        for (int i = 0; i < count; i++)
        {
            StructuralNeighborA[i] = -1;
            StructuralNeighborB[i] = -1;
            StructuralNeighborC[i] = -1;

            // Explicit dynamic groups already have region-specific handling.
            // Their nearest Euclidean neighbour may sit across an eyelid/lip
            // boundary, so do not construct a structural graph for them.
            if (GroupByLandmark[i] != NoGroup)
            {
                continue;
            }

            float bestA = float.PositiveInfinity;
            float bestB = float.PositiveInfinity;
            float bestC = float.PositiveInfinity;
            int indexA = -1;
            int indexB = -1;
            int indexC = -1;

            for (int j = 0; j < count; j++)
            {
                if (
                    j == i ||
                    GroupByLandmark[j] != NoGroup)
                {
                    continue;
                }

                float distanceSquared =
                    (CurrentLocal[j] - CurrentLocal[i]).sqrMagnitude;

                if (distanceSquared < bestA)
                {
                    bestC = bestB;
                    indexC = indexB;
                    bestB = bestA;
                    indexB = indexA;
                    bestA = distanceSquared;
                    indexA = j;
                }
                else if (distanceSquared < bestB)
                {
                    bestC = bestB;
                    indexC = indexB;
                    bestB = distanceSquared;
                    indexB = j;
                }
                else if (distanceSquared < bestC)
                {
                    bestC = distanceSquared;
                    indexC = j;
                }
            }

            StructuralNeighborA[i] = (short)indexA;
            StructuralNeighborB[i] = (short)indexB;
            StructuralNeighborC[i] = (short)indexC;
        }
    }

    private static void GetPointFilterParameters(
        int group,
        float geometryQuality,
        out float microStart,
        out float motionFull,
        out float minimumGain)
    {
        bool iris =
            group == GroupIrisA ||
            group == GroupIrisB;

        bool dynamic =
            group == GroupEyeA ||
            group == GroupEyeB ||
            group == GroupMouth ||
            group == GroupBrowA ||
            group == GroupBrowB;

        if (iris)
        {
            microStart = IrisMicroStart;
            motionFull = IrisMotionFull;
            minimumGain = IrisMinimumGain;
        }
        else if (dynamic)
        {
            microStart = DynamicMicroStart;
            motionFull = DynamicMotionFull;
            minimumGain = DynamicMinimumGain;
        }
        else
        {
            microStart = StructuralMicroStart;
            motionFull = StructuralMotionFull;
            minimumGain = StructuralMinimumGain;
        }

        // geometryQuality is a broad size/topology confidence proxy from the
        // detector. High-quality samples are trusted sooner; low-quality ones
        // receive slightly more micro-noise attenuation, never a hard delay.
        float trust = Mathf.InverseLerp(0.20f, 0.90f, geometryQuality);

        microStart *= Mathf.Lerp(1.20f, 0.86f, trust);
        motionFull *= Mathf.Lerp(1.18f, 0.90f, trust);
        minimumGain *= Mathf.Lerp(0.86f, 1.12f, trust);
        minimumGain = Mathf.Clamp(minimumGain, 0.12f, 0.55f);
    }

    private static bool TryBuildFaceBasis(
        Vector2[] landmarks,
        int count,
        out Vector2 origin,
        out Vector2 axisX,
        out Vector2 axisY,
        out float eyeSpan)
    {
        origin = Vector2.zero;
        axisX = Vector2.right;
        axisY = Vector2.up;
        eyeSpan = 0f;

        if (
            count <= 362 ||
            count <= 263 ||
            count <= 133 ||
            count <= 33)
        {
            return false;
        }

        Vector2 eyeA =
            (landmarks[362] + landmarks[263]) * 0.5f;

        Vector2 eyeB =
            (landmarks[33] + landmarks[133]) * 0.5f;

        if (!IsFinite(eyeA) || !IsFinite(eyeB))
        {
            return false;
        }

        Vector2 eyeVector = eyeA - eyeB;
        eyeSpan = eyeVector.magnitude;

        if (
            eyeSpan <= 0.000001f ||
            float.IsNaN(eyeSpan) ||
            float.IsInfinity(eyeSpan))
        {
            return false;
        }

        axisX = eyeVector / eyeSpan;
        axisY = new Vector2(-axisX.y, axisX.x);
        origin = (eyeA + eyeB) * 0.5f;
        return true;
    }

    private static void RegisterContour(
        int[] indices,
        int group,
        bool cyclic)
    {
        for (int i = 0; i < indices.Length; i++)
        {
            int index = indices[i];
            if (index < 0 || index >= MaxLandmarks)
            {
                continue;
            }

            GroupByLandmark[index] = (sbyte)group;

            if (indices.Length < 3)
            {
                continue;
            }

            if (!cyclic && (i == 0 || i == indices.Length - 1))
            {
                continue;
            }

            int previous =
                cyclic
                    ? indices[(i - 1 + indices.Length) % indices.Length]
                    : indices[i - 1];

            int next =
                cyclic
                    ? indices[(i + 1) % indices.Length]
                    : indices[i + 1];

            PreviousContourNeighbor[index] = (short)previous;
            NextContourNeighbor[index] = (short)next;
        }
    }

    private static float SmoothStep01(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }

    private static bool IsFinite(Vector2 value)
    {
        return
            !float.IsNaN(value.x) &&
            !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsInfinity(value.y);
    }
}
