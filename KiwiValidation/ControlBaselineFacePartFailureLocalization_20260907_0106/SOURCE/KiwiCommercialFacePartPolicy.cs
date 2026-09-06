using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v5.0 commercial semantic face-part policy.
///
/// Responsibilities are intentionally presentation-local:
/// - reject source-age-expired semantic samples;
/// - publish one per-part adoption transaction for each landmark timestamp so
///   Cropper and ShapeMask cannot present different semantic frames;
/// - reject only catastrophic isolated one-eye crop jumps when the other eye
///   and mouth agree on coherent face motion.
///
/// This policy owns no Transform, material, crop, or mask and can never feed
/// Eye/Mouth motion back into the avatar rigid root.
/// </summary>
public static class KiwiCommercialFacePartPolicy
{
    // KIWI_V5_1_PHASE16_6_COMMERCIAL_SEMANTIC_TRANSACTION
    // Commercial semantic presentation keeps one trusted topology baseline and
    // one immutable generation/canonical identity per Crop+Mask transaction.
    private const float SemanticTopologyMeanResidualEyeSpans = 0.42f;
    private const float SemanticTopologyMaxResidualEyeSpans = 0.72f;
    private const float SemanticEyeSpanMinimumRatio = 0.60f;
    private const float SemanticEyeSpanMaximumRatio = 1.65f;
    private const float SemanticPendingConsistencyEyeSpans = 0.22f;

    public enum SemanticPart
    {
        LeftEye = 0,
        RightEye = 1,
        Mouth = 2
    }

    // The v4.7.1/v4.8 recordings show normal semantic source ages around
    // 110-170 ms and pathological updates above ~220 ms. Semantic presentation
    // may safely hold its last complete crop/mask slightly longer than rigid
    // root adoption, but it must never treat a newly-arrived old frame as fresh.
    public const float MaximumSemanticSourceAgeSeconds = 0.22f;

    // The eye guard is deliberately broad. It is not a normal motion filter;
    // it only catches an isolated, topology-breaking eye crop jump.
    public const float EyeOutlierAbsoluteCenterTolerance = 0.018f;
    public const float EyeOutlierEyeSpanToleranceMultiplier = 0.22f;
    public const float EyeCompanionCoherenceAbsoluteTolerance = 0.012f;
    public const float EyeCompanionCoherenceSpanMultiplier = 0.12f;
    public const float EyeOutlierMinimumSizeRatio = 0.72f;
    public const float EyeOutlierMaximumSizeRatio = 1.40f;
    public const float EyeSpanMinimumRatio = 0.70f;
    public const float EyeSpanMaximumRatio = 1.38f;

    // Face-local triangle coherence catches a wrong-eye crop even when its raw
    // screen-space center shift is not large enough to trip the center guard.
    public const float EyeTopologySuspectAbsoluteTolerance = 0.018f;
    public const float EyeTopologySuspectSpanMultiplier = 0.18f;
    public const float EyeTopologyCompanionAbsoluteTolerance = 0.010f;
    public const float EyeTopologyCompanionSpanMultiplier = 0.10f;

    private static float _lastSemanticSourceAgeMs = -1f;
    private static int _staleSemanticRejectCount;
    private static long _lastRejectedTimestamp = -1L;

    private static long _partDecisionTimestamp = -1L;
    private static long _semanticTransactionSequence;
    private static bool _leftEyeAccepted = true;
    private static bool _rightEyeAccepted = true;
    private static bool _mouthAccepted = true;
    private static int _leftEyeRejectCount;
    private static int _rightEyeRejectCount;
    private static int _mouthRejectCount;
    private static long _lastLeftEyeRejectedTimestamp = -1L;
    private static long _lastRightEyeRejectedTimestamp = -1L;
    private static long _lastMouthRejectedTimestamp = -1L;

    private static KiwiRuntimeGenerationContext.Snapshot _transactionGeneration;
    private static ulong _transactionCanonicalFrameId;

    private static readonly Vector2[] _trustedSemanticAnchors = new Vector2[4];
    private static readonly Vector2[] _pendingSemanticAnchors = new Vector2[4];
    private static readonly Vector2[] _currentSemanticAnchors = new Vector2[4];
    private static bool _hasTrustedSemanticAnchors;
    private static bool _hasPendingSemanticShock;
    private static int _pendingSemanticShockCount;
    private static string _trustedSemanticProvider = string.Empty;
    private static int _trustedSemanticCameraGeneration;
    private static int _trustedSemanticProviderGeneration;
    private static int _trustedSemanticTrackingSessionGeneration;
    private static int _semanticTopologyRejectCount;
    private static long _lastSemanticTopologyRejectedTimestamp = -1L;

    public static int SemanticTopologyRejectCount => _semanticTopologyRejectCount;
    public static ulong TransactionCanonicalFrameId => _transactionCanonicalFrameId;

    public static float LastSemanticSourceAgeMs =>
        _lastSemanticSourceAgeMs;

    public static int StaleSemanticRejectCount =>
        _staleSemanticRejectCount;

    public static long LastRejectedTimestamp =>
        _lastRejectedTimestamp;

    public static long PartDecisionTimestamp =>
        _partDecisionTimestamp;

    public static long SemanticTransactionSequence =>
        _semanticTransactionSequence;

    public static bool LastLeftEyeAccepted =>
        _leftEyeAccepted;

    public static bool LastRightEyeAccepted =>
        _rightEyeAccepted;

    public static bool LastMouthAccepted =>
        _mouthAccepted;

    public static int LeftEyeRejectCount =>
        _leftEyeRejectCount;

    public static int RightEyeRejectCount =>
        _rightEyeRejectCount;

    public static int MouthRejectCount =>
        _mouthRejectCount;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetRuntimeState()
    {
        _lastSemanticSourceAgeMs = -1f;
        _staleSemanticRejectCount = 0;
        _lastRejectedTimestamp = -1L;

        _partDecisionTimestamp = -1L;
        _semanticTransactionSequence = 0L;
        _leftEyeAccepted = true;
        _rightEyeAccepted = true;
        _mouthAccepted = true;
        _leftEyeRejectCount = 0;
        _rightEyeRejectCount = 0;
        _mouthRejectCount = 0;
        _lastLeftEyeRejectedTimestamp = -1L;
        _lastRightEyeRejectedTimestamp = -1L;
        _lastMouthRejectedTimestamp = -1L;

        _transactionGeneration = default;
        _transactionCanonicalFrameId = 0UL;
        _hasTrustedSemanticAnchors = false;
        _hasPendingSemanticShock = false;
        _pendingSemanticShockCount = 0;
        _trustedSemanticProvider = string.Empty;
        _trustedSemanticCameraGeneration = 0;
        _trustedSemanticProviderGeneration = 0;
        _trustedSemanticTrackingSessionGeneration = 0;
        _semanticTopologyRejectCount = 0;
        _lastSemanticTopologyRejectedTimestamp = -1L;
    }

    public static bool IsSemanticSampleAdoptable(
        FaceLandmarkerRunner runner,
        long semanticTimestamp)
    {
        if (runner == null)
        {
            return false;
        }

        FacePrecisionTrackingData precision;

        // KIWI_V5_1_PHASE5_CANONICAL_SEMANTIC_AGE
        // When the display-cycle latch is active, age the semantic crop/mask
        // against the exact rigid snapshot that Root consumes. Never consult a
        // newer Runner precision sample here, because that would reintroduce a
        // cross-phase timestamp into an otherwise canonical FacePart frame.
        if (KiwiCanonicalTrackingFrame.IsRuntimeCoordinatorActive)
        {
            if (
                !KiwiCanonicalTrackingFrame.IsCurrentSemanticTimestamp(
                    semanticTimestamp) ||
                !KiwiCanonicalTrackingFrame.TryGetRigidFrame(
                    out precision) ||
                !precision.isValid
            )
            {
                return false;
            }
        }
        else if (
            !runner.TryGetLatestPrecisionTrackingData(
                out precision) ||
            !precision.isValid
        )
        {
            return true;
        }

        // Landmark and precision snapshots can cross a Unity phase boundary by
        // one callback. If precision is older than the semantic sample, its
        // source clock cannot safely age the newer contour. If precision is
        // newer, the timestamp delta is a safe lower bound on how much older
        // the semantic sample is.
        float semanticLagSeconds = 0f;
        if (
            precision.timestamp > semanticTimestamp &&
            semanticTimestamp >= 0L
        )
        {
            semanticLagSeconds =
                Mathf.Clamp(
                    (precision.timestamp - semanticTimestamp) / 1000f,
                    0f,
                    1f);
        }
        else if (precision.timestamp < semanticTimestamp)
        {
            return true;
        }

        long sourceTicks =
            precision.hasMatchedSubmissionTiming &&
            precision.submissionHostTicks > 0L
                ? precision.submissionHostTicks
                : precision.arrivalHostTicks;

        if (sourceTicks <= 0L)
        {
            _lastSemanticSourceAgeMs = -1f;
            return true;
        }

        long nowTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();

        if (nowTicks <= sourceTicks)
        {
            _lastSemanticSourceAgeMs = 0f;
            return true;
        }

        float ageSeconds =
            (float)KiwiPrecisionTrackingMath.HostTicksToSeconds(
                nowTicks - sourceTicks) +
            semanticLagSeconds;

        if (
            float.IsNaN(ageSeconds) ||
            float.IsInfinity(ageSeconds) ||
            ageSeconds < 0f
        )
        {
            return false;
        }

        _lastSemanticSourceAgeMs =
            ageSeconds * 1000f;

        if (
            ageSeconds <=
                MaximumSemanticSourceAgeSeconds
        )
        {
            return true;
        }

        if (_lastRejectedTimestamp != semanticTimestamp)
        {
            _lastRejectedTimestamp = semanticTimestamp;
            _staleSemanticRejectCount++;
        }

        return false;
    }

    /// <summary>
    /// Publish the exact output-part decision made by FacePartCropper for one
    /// semantic timestamp. ShapeMask consumes this transaction later in the
    /// same frame and holds its previous contour when the matching crop held.
    /// </summary>
    public static void ReportPartSampleDecision(
        long semanticTimestamp,
        bool leftEyeAccepted,
        bool rightEyeAccepted,
        bool mouthAccepted)
    {
        if (semanticTimestamp != _partDecisionTimestamp)
        {
            _semanticTransactionSequence =
                KiwiRuntimeGenerationContext.AdvanceSemanticTransaction();
        }

        _partDecisionTimestamp =
            semanticTimestamp;

        _transactionGeneration =
            KiwiRuntimeGenerationContext.Capture();

        _transactionCanonicalFrameId =
            KiwiCanonicalTrackingFrame.CanonicalFrameId;

        _leftEyeAccepted =
            leftEyeAccepted;

        _rightEyeAccepted =
            rightEyeAccepted;

        _mouthAccepted =
            mouthAccepted;

        if (
            !leftEyeAccepted &&
            _lastLeftEyeRejectedTimestamp != semanticTimestamp
        )
        {
            _lastLeftEyeRejectedTimestamp = semanticTimestamp;
            _leftEyeRejectCount++;
        }

        if (
            !rightEyeAccepted &&
            _lastRightEyeRejectedTimestamp != semanticTimestamp
        )
        {
            _lastRightEyeRejectedTimestamp = semanticTimestamp;
            _rightEyeRejectCount++;
        }

        if (
            !mouthAccepted &&
            _lastMouthRejectedTimestamp != semanticTimestamp
        )
        {
            _lastMouthRejectedTimestamp = semanticTimestamp;
            _mouthRejectCount++;
        }
    }

    public static bool IsPartSampleAdoptable(
        long semanticTimestamp,
        SemanticPart part)
    {
        // A missing transaction means the caller is an older compatible path;
        // do not blank presentation merely because no v4.9 reporter exists.
        if (semanticTimestamp < 0L)
        {
            return false;
        }

        if (semanticTimestamp != _partDecisionTimestamp)
        {
            // Once the canonical coordinator is active, Cropper and ShapeMask
            // must consume the same transaction; a missing reporter is not a
            // compatibility case anymore.
            return !KiwiCanonicalTrackingFrame.IsRuntimeCoordinatorActive;
        }

        KiwiRuntimeGenerationContext.Snapshot currentGeneration =
            KiwiRuntimeGenerationContext.Capture();

        if (
            _transactionCanonicalFrameId == 0UL ||
            _transactionCanonicalFrameId != KiwiCanonicalTrackingFrame.CanonicalFrameId ||
            _transactionGeneration.cameraGeneration != currentGeneration.cameraGeneration ||
            _transactionGeneration.providerGeneration != currentGeneration.providerGeneration ||
            _transactionGeneration.modelGeneration != currentGeneration.modelGeneration ||
            _transactionGeneration.configEpoch != currentGeneration.configEpoch ||
            _transactionGeneration.calibrationGeneration != currentGeneration.calibrationGeneration ||
            _transactionGeneration.trackingSessionGeneration != currentGeneration.trackingSessionGeneration ||
            _transactionGeneration.semanticTransactionSequence != currentGeneration.semanticTransactionSequence
        )
        {
            return false;
        }

        switch (part)
        {
            case SemanticPart.LeftEye:
                return _leftEyeAccepted;

            case SemanticPart.RightEye:
                return _rightEyeAccepted;

            case SemanticPart.Mouth:
                return _mouthAccepted;

            default:
                return true;
        }
    }

    /// <summary>
    /// Conservative isolated-eye topology guard. Shared translation, yaw/roll
    /// deformation and simultaneous pair changes are intentionally preserved.
    /// The method only rejects one eye when the companion eye and mouth provide
    /// a coherent reference and the suspect eye is a catastrophic outlier.
    /// </summary>
    public static void ResolveIsolatedEyeCropOutliers(
        Rect previousLeft,
        Rect previousRight,
        Rect previousMouth,
        Rect currentLeft,
        Rect currentRight,
        Rect currentMouth,
        ref bool leftAccepted,
        ref bool rightAccepted)
    {
        if (
            !leftAccepted ||
            !rightAccepted ||
            !IsValidRect(previousLeft) ||
            !IsValidRect(previousRight) ||
            !IsValidRect(previousMouth) ||
            !IsValidRect(currentLeft) ||
            !IsValidRect(currentRight) ||
            !IsValidRect(currentMouth)
        )
        {
            return;
        }

        Vector2 leftDelta =
            currentLeft.center - previousLeft.center;

        Vector2 rightDelta =
            currentRight.center - previousRight.center;

        Vector2 mouthDelta =
            currentMouth.center - previousMouth.center;

        float previousEyeSpan =
            Vector2.Distance(
                previousLeft.center,
                previousRight.center);

        float currentEyeSpan =
            Vector2.Distance(
                currentLeft.center,
                currentRight.center);

        float referenceEyeSpan =
            Mathf.Max(
                0.0001f,
                Mathf.Max(
                    previousEyeSpan,
                    currentEyeSpan));

        float companionTolerance =
            Mathf.Max(
                EyeCompanionCoherenceAbsoluteTolerance,
                referenceEyeSpan *
                    EyeCompanionCoherenceSpanMultiplier);

        bool rightAndMouthCoherent =
            Vector2.Distance(
                rightDelta,
                mouthDelta) <=
            companionTolerance;

        bool leftAndMouthCoherent =
            Vector2.Distance(
                leftDelta,
                mouthDelta) <=
            companionTolerance;

        float isolatedTolerance =
            Mathf.Max(
                EyeOutlierAbsoluteCenterTolerance,
                referenceEyeSpan *
                    EyeOutlierEyeSpanToleranceMultiplier);

        Vector2 expectedLeftDelta =
            rightDelta * 0.65f +
            mouthDelta * 0.35f;

        Vector2 expectedRightDelta =
            leftDelta * 0.65f +
            mouthDelta * 0.35f;

        float leftResidual =
            Vector2.Distance(
                leftDelta,
                expectedLeftDelta);

        float rightResidual =
            Vector2.Distance(
                rightDelta,
                expectedRightDelta);

        float leftSizeRatio =
            CalculateSizeRatio(
                previousLeft,
                currentLeft);

        float rightSizeRatio =
            CalculateSizeRatio(
                previousRight,
                currentRight);

        bool leftSizeCatastrophic =
            IsCatastrophicSizeRatio(
                leftSizeRatio);

        bool rightSizeCatastrophic =
            IsCatastrophicSizeRatio(
                rightSizeRatio);

        bool eyeSpanCatastrophic =
            previousEyeSpan > 0.0001f &&
            (
                currentEyeSpan /
                    previousEyeSpan <
                    EyeSpanMinimumRatio ||
                currentEyeSpan /
                    previousEyeSpan >
                    EyeSpanMaximumRatio
            );

        Vector2 previousLeftFromMouth =
            previousLeft.center - previousMouth.center;

        Vector2 previousRightFromMouth =
            previousRight.center - previousMouth.center;

        Vector2 currentLeftFromMouth =
            currentLeft.center - currentMouth.center;

        Vector2 currentRightFromMouth =
            currentRight.center - currentMouth.center;

        float leftTopologyChange =
            Vector2.Distance(
                previousLeftFromMouth,
                currentLeftFromMouth);

        float rightTopologyChange =
            Vector2.Distance(
                previousRightFromMouth,
                currentRightFromMouth);

        float topologySuspectTolerance =
            Mathf.Max(
                EyeTopologySuspectAbsoluteTolerance,
                referenceEyeSpan *
                    EyeTopologySuspectSpanMultiplier);

        float topologyCompanionTolerance =
            Mathf.Max(
                EyeTopologyCompanionAbsoluteTolerance,
                referenceEyeSpan *
                    EyeTopologyCompanionSpanMultiplier);

        bool leftTopologySuspect =
            leftTopologyChange >
                topologySuspectTolerance &&
            rightTopologyChange <=
                topologyCompanionTolerance;

        bool rightTopologySuspect =
            rightTopologyChange >
                topologySuspectTolerance &&
            leftTopologyChange <=
                topologyCompanionTolerance;

        bool leftCandidate =
            rightAndMouthCoherent &&
            (
                leftResidual > isolatedTolerance ||
                leftTopologySuspect ||
                leftSizeCatastrophic ||
                eyeSpanCatastrophic
            );

        bool rightCandidate =
            leftAndMouthCoherent &&
            (
                rightResidual > isolatedTolerance ||
                rightTopologySuspect ||
                rightSizeCatastrophic ||
                eyeSpanCatastrophic
            );

        if (leftCandidate && rightCandidate)
        {
            // Ambiguous pair-wide change: preserve normal tracking. This guard
            // must never become a general face-motion filter.
            return;
        }

        if (leftCandidate)
        {
            leftAccepted = false;
        }
        else if (rightCandidate)
        {
            rightAccepted = false;
        }
    }

    public static bool IsCanonicalSemanticGeometryCoherent(
        Vector2[] landmarks,
        int landmarkCount,
        long semanticTimestamp,
        string providerId,
        KiwiRuntimeGenerationContext.Snapshot generation)
    {
        if (
            landmarks == null ||
            landmarkCount <= 362
        )
        {
            return false;
        }

        Vector2[] current = _currentSemanticAnchors;
        if (!TryBuildSemanticAnchors(landmarks, landmarkCount, current))
        {
            return false;
        }

        bool identityChanged =
            !_hasTrustedSemanticAnchors ||
            !string.Equals(
                _trustedSemanticProvider,
                providerId ?? string.Empty,
                System.StringComparison.Ordinal) ||
            _trustedSemanticCameraGeneration != generation.cameraGeneration ||
            _trustedSemanticProviderGeneration != generation.providerGeneration ||
            _trustedSemanticTrackingSessionGeneration != generation.trackingSessionGeneration;

        if (identityChanged)
        {
            AcceptSemanticAnchors(current, providerId, generation);
            return true;
        }

        Vector2 previousEyeVector =
            _trustedSemanticAnchors[1] -
            _trustedSemanticAnchors[0];

        Vector2 currentEyeVector =
            current[1] -
            current[0];

        float previousSpan =
            previousEyeVector.magnitude;

        float currentSpan =
            currentEyeVector.magnitude;

        if (
            previousSpan <= 0.000001f ||
            currentSpan <= 0.000001f
        )
        {
            return false;
        }

        float spanRatio =
            currentSpan / previousSpan;

        float previousAngle =
            Mathf.Atan2(
                previousEyeVector.y,
                previousEyeVector.x);

        float currentAngle =
            Mathf.Atan2(
                currentEyeVector.y,
                currentEyeVector.x);

        float rotation =
            currentAngle - previousAngle;

        float c = Mathf.Cos(rotation);
        float sin = Mathf.Sin(rotation);

        Vector2 previousEyeCenter =
            (_trustedSemanticAnchors[0] +
             _trustedSemanticAnchors[1]) * 0.5f;

        Vector2 currentEyeCenter =
            (current[0] + current[1]) * 0.5f;

        float noseResidual =
            TransformResidual(
                _trustedSemanticAnchors[2],
                current[2],
                previousEyeCenter,
                currentEyeCenter,
                c,
                sin,
                spanRatio) /
            Mathf.Max(0.000001f, currentSpan);

        float chinResidual =
            TransformResidual(
                _trustedSemanticAnchors[3],
                current[3],
                previousEyeCenter,
                currentEyeCenter,
                c,
                sin,
                spanRatio) /
            Mathf.Max(0.000001f, currentSpan);

        float meanResidual =
            (noseResidual + chinResidual) * 0.5f;

        float maxResidual =
            Mathf.Max(noseResidual, chinResidual);

        bool suspect =
            spanRatio < SemanticEyeSpanMinimumRatio ||
            spanRatio > SemanticEyeSpanMaximumRatio ||
            meanResidual > SemanticTopologyMeanResidualEyeSpans ||
            maxResidual > SemanticTopologyMaxResidualEyeSpans;

        if (!suspect)
        {
            _hasPendingSemanticShock = false;
            _pendingSemanticShockCount = 0;
            AcceptSemanticAnchors(current, providerId, generation);
            return true;
        }

        bool consistentPending =
            _hasPendingSemanticShock &&
            SemanticAnchorDistance(
                _pendingSemanticAnchors,
                current) <=
            SemanticPendingConsistencyEyeSpans *
            Mathf.Max(0.000001f, currentSpan);

        if (consistentPending)
        {
            _pendingSemanticShockCount++;
        }
        else
        {
            CopyAnchors(current, _pendingSemanticAnchors);
            _hasPendingSemanticShock = true;
            _pendingSemanticShockCount = 1;
        }

        // A genuine abrupt pose can violate the broad topology gate. Reacquire
        // after two mutually-consistent samples; a one-frame semantic glitch
        // never reaches Cropper/ShapeMask.
        if (_pendingSemanticShockCount >= 2)
        {
            _hasPendingSemanticShock = false;
            _pendingSemanticShockCount = 0;
            AcceptSemanticAnchors(current, providerId, generation);
            return true;
        }

        if (_lastSemanticTopologyRejectedTimestamp != semanticTimestamp)
        {
            _lastSemanticTopologyRejectedTimestamp = semanticTimestamp;
            _semanticTopologyRejectCount++;
        }

        return false;
    }

    private static bool TryBuildSemanticAnchors(
        Vector2[] landmarks,
        int count,
        Vector2[] output)
    {
        // [0]=left eye center, [1]=right eye center, [2]=nose, [3]=chin.
        if (
            count <= 362 ||
            count <= 263 ||
            count <= 133 ||
            count <= 33 ||
            count <= 152 ||
            count <= 1
        )
        {
            return false;
        }

        output[0] = (landmarks[362] + landmarks[263]) * 0.5f;
        output[1] = (landmarks[33] + landmarks[133]) * 0.5f;
        output[2] = landmarks[1];
        output[3] = landmarks[152];

        for (int i = 0; i < output.Length; i++)
        {
            if (!IsFinite(output[i].x) || !IsFinite(output[i].y))
            {
                return false;
            }
        }

        return
            Vector2.Distance(output[0], output[1]) >
            0.000001f;
    }

    private static float TransformResidual(
        Vector2 previousPoint,
        Vector2 currentPoint,
        Vector2 previousCenter,
        Vector2 currentCenter,
        float c,
        float s,
        float scale)
    {
        Vector2 local =
            previousPoint - previousCenter;

        Vector2 rotated =
            new Vector2(
                local.x * c - local.y * s,
                local.x * s + local.y * c) *
            scale;

        Vector2 predicted =
            currentCenter + rotated;

        return Vector2.Distance(predicted, currentPoint);
    }

    private static float SemanticAnchorDistance(
        Vector2[] a,
        Vector2[] b)
    {
        float max = 0f;
        for (int i = 0; i < 4; i++)
        {
            max = Mathf.Max(
                max,
                Vector2.Distance(a[i], b[i]));
        }
        return max;
    }

    private static void AcceptSemanticAnchors(
        Vector2[] anchors,
        string providerId,
        KiwiRuntimeGenerationContext.Snapshot generation)
    {
        CopyAnchors(anchors, _trustedSemanticAnchors);
        _hasTrustedSemanticAnchors = true;
        _trustedSemanticProvider = providerId ?? string.Empty;
        _trustedSemanticCameraGeneration = generation.cameraGeneration;
        _trustedSemanticProviderGeneration = generation.providerGeneration;
        _trustedSemanticTrackingSessionGeneration =
            generation.trackingSessionGeneration;
    }

    private static void CopyAnchors(
        Vector2[] source,
        Vector2[] destination)
    {
        for (int i = 0; i < 4; i++)
        {
            destination[i] = source[i];
        }
    }

    private static float CalculateSizeRatio(
        Rect previous,
        Rect current)
    {
        float previousArea =
            Mathf.Max(
                0.0000001f,
                previous.width * previous.height);

        float currentArea =
            Mathf.Max(
                0.0000001f,
                current.width * current.height);

        return
            Mathf.Sqrt(
                currentArea /
                previousArea);
    }

    private static bool IsCatastrophicSizeRatio(
        float ratio)
    {
        return
            !float.IsNaN(ratio) &&
            !float.IsInfinity(ratio) &&
            (
                ratio < EyeOutlierMinimumSizeRatio ||
                ratio > EyeOutlierMaximumSizeRatio
            );
    }

    private static bool IsValidRect(
        Rect rect)
    {
        return
            rect.width > 0.000001f &&
            rect.height > 0.000001f &&
            IsFinite(rect.x) &&
            IsFinite(rect.y) &&
            IsFinite(rect.width) &&
            IsFinite(rect.height);
    }

    private static bool IsFinite(float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
    }
}
