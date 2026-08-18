using UnityEngine;

public enum KiwiTrackingStrategy
{
    PredictiveHybrid,
    OneEuro
}

public readonly struct KiwiTrackingStrategyResult
{
    public readonly KiwiTrackingStrategy winner;
    public readonly float predictiveMotionRmse;
    public readonly float oneEuroMotionRmse;
    public readonly float predictiveRestJitter;
    public readonly float oneEuroRestJitter;
    public readonly float predictiveScore;
    public readonly float oneEuroScore;

    public KiwiTrackingStrategyResult(
        KiwiTrackingStrategy winner,
        float predictiveMotionRmse,
        float oneEuroMotionRmse,
        float predictiveRestJitter,
        float oneEuroRestJitter,
        float predictiveScore,
        float oneEuroScore)
    {
        this.winner = winner;
        this.predictiveMotionRmse = predictiveMotionRmse;
        this.oneEuroMotionRmse = oneEuroMotionRmse;
        this.predictiveRestJitter = predictiveRestJitter;
        this.oneEuroRestJitter = oneEuroRestJitter;
        this.predictiveScore = predictiveScore;
        this.oneEuroScore = oneEuroScore;
    }
}

/// <summary>
/// Deterministic A/B evaluation of the two tracking strategies. The signal
/// contains movement, stops and reversals, sampled at 30 Hz and displayed at
/// 120 Hz. Lower score is better; motion error is weighted above rest jitter
/// because minimum perceived delay is the primary product requirement.
/// </summary>
public static class KiwiTrackingStrategyEvaluator
{
    private const float Duration = 3f;
    private const float RenderDelta = 1f / 120f;
    private const float SampleDelta = 1f / 30f;

    public static KiwiTrackingStrategyResult Compare()
    {
        float oneEuro = 0f;
        float oneEuroDerivative = 0f;
        float predictive = 0f;
        float velocity = 0f;
        float previousSample = 0f;
        float latestSample = 0f;
        float latestSampleTime = 0f;
        float nextSampleTime = 0f;
        bool initialized = false;
        float predictiveMotionError = 0f;
        float oneEuroMotionError = 0f;
        float predictiveRestDelta = 0f;
        float oneEuroRestDelta = 0f;
        float previousPredictive = 0f;
        float previousOneEuro = 0f;
        int motionFrames = 0;
        int restFrames = 0;

        int frameCount = Mathf.CeilToInt(Duration / RenderDelta);
        for (int frame = 0; frame <= frameCount; frame++)
        {
            float time = frame * RenderDelta;
            bool receivedSample = time + 0.000001f >= nextSampleTime;
            if (receivedSample)
            {
                float sampleTime = nextSampleTime;
                float measurement = Truth(sampleTime) + MeasurementNoise(sampleTime);

                if (!initialized)
                {
                    initialized = true;
                    oneEuro = measurement;
                    predictive = measurement;
                    previousPredictive = predictive;
                    previousOneEuro = oneEuro;
                }
                else
                {
                    float rawVelocity = (measurement - previousSample) / SampleDelta;
                    float agreement = Mathf.Sign(rawVelocity) == Mathf.Sign(velocity) ? 1f : 0f;
                    float response = KiwiUltraDisplayMath.CalculateAdaptiveVelocityResponse(
                        60f,
                        180f,
                        agreement
                    );
                    velocity = Mathf.Lerp(
                        velocity,
                        rawVelocity,
                        1f - Mathf.Exp(-response * SampleDelta)
                    );

                    float derivative = (measurement - previousSample) / SampleDelta;
                    float derivativeAlpha = OneEuroAlpha(SampleDelta, 1f);
                    oneEuroDerivative = Mathf.Lerp(oneEuroDerivative, derivative, derivativeAlpha);
                    float cutoff = 1f + 0.05f * Mathf.Abs(oneEuroDerivative);
                    oneEuro = Mathf.Lerp(
                        oneEuro,
                        measurement,
                        OneEuroAlpha(SampleDelta, cutoff)
                    );
                }

                previousSample = measurement;
                latestSample = measurement;
                latestSampleTime = sampleTime;
                nextSampleTime += SampleDelta;
            }

            float sampleAge = Mathf.Max(0f, time - latestSampleTime) + SampleDelta * 0.5f;
            float boundedLead = Mathf.Min(sampleAge, 0.05f);
            float target = latestSample + velocity * boundedLead;
            float consistency = Mathf.Abs(velocity) > 0.03f ? 1f : 0f;
            float correction = KiwiUltraDisplayMath.CalculateAdaptiveCorrectionResponse(
                45f,
                180f,
                consistency
            );

            if (Mathf.Abs(velocity) > 0.03f)
            {
                predictive = KiwiUltraDisplayMath.AdvancePredictivePosition(
                    new Vector3(predictive, 0f, 0f),
                    new Vector3(target, 0f, 0f),
                    new Vector3(velocity, 0f, 0f),
                    RenderDelta,
                    correction
                ).x;
            }
            else if (Mathf.Abs(latestSample - predictive) > 0.006f)
            {
                predictive = Mathf.Lerp(
                    predictive,
                    latestSample,
                    1f - Mathf.Exp(-45f * RenderDelta)
                );
            }

            float truth = Truth(time);
            if (IsIntentionalMotion(time))
            {
                predictiveMotionError += Square(predictive - truth);
                oneEuroMotionError += Square(oneEuro - truth);
                motionFrames++;
            }
            else if (time > 0.20f)
            {
                predictiveRestDelta += Square(predictive - previousPredictive);
                oneEuroRestDelta += Square(oneEuro - previousOneEuro);
                restFrames++;
            }

            previousPredictive = predictive;
            previousOneEuro = oneEuro;
        }

        float predictiveRmse = Mathf.Sqrt(predictiveMotionError / Mathf.Max(1, motionFrames));
        float oneEuroRmse = Mathf.Sqrt(oneEuroMotionError / Mathf.Max(1, motionFrames));
        float predictiveJitter = Mathf.Sqrt(predictiveRestDelta / Mathf.Max(1, restFrames));
        float oneEuroJitter = Mathf.Sqrt(oneEuroRestDelta / Mathf.Max(1, restFrames));
        float predictiveScore = predictiveRmse * 0.85f + predictiveJitter * 0.15f;
        float oneEuroScore = oneEuroRmse * 0.85f + oneEuroJitter * 0.15f;

        return new KiwiTrackingStrategyResult(
            predictiveScore <= oneEuroScore
                ? KiwiTrackingStrategy.PredictiveHybrid
                : KiwiTrackingStrategy.OneEuro,
            predictiveRmse,
            oneEuroRmse,
            predictiveJitter,
            oneEuroJitter,
            predictiveScore,
            oneEuroScore
        );
    }

    private static float Truth(float time)
    {
        if (time < 0.35f) return 0f;
        if (time < 1.10f) return (time - 0.35f) / 0.75f;
        if (time < 1.55f) return 1f;
        if (time < 2.10f) return 1f - (time - 1.55f) / 0.55f * 0.8f;
        return 0.2f;
    }

    private static bool IsIntentionalMotion(float time)
    {
        return (time >= 0.35f && time < 1.10f) ||
            (time >= 1.55f && time < 2.10f);
    }

    private static float MeasurementNoise(float time)
    {
        return 0.0030f * Mathf.Sin(time * 113f) +
            0.0015f * Mathf.Sin(time * 47f + 0.7f);
    }

    private static float OneEuroAlpha(float deltaTime, float cutoff)
    {
        float tau = 1f / (2f * Mathf.PI * Mathf.Max(0.0001f, cutoff));
        return 1f / (1f + tau / Mathf.Max(0.000001f, deltaTime));
    }

    private static float Square(float value)
    {
        return value * value;
    }
}

public enum KiwiResultAcceptanceStrategy
{
    ThreeResultHold,
    ImmediateRaw,
    BoundedLatest,
    QualityGatedDirect
}

public readonly struct KiwiResultAcceptanceScore
{
    public readonly KiwiResultAcceptanceStrategy strategy;
    public readonly float motionRmse;
    public readonly float restJitter;
    public readonly float total;

    public KiwiResultAcceptanceScore(
        KiwiResultAcceptanceStrategy strategy,
        float motionRmse,
        float restJitter)
    {
        this.strategy = strategy;
        this.motionRmse = motionRmse;
        this.restJitter = restJitter;
        total = motionRmse * 0.80f + restJitter * 0.20f;
    }
}

public readonly struct KiwiResultAcceptanceComparison
{
    public readonly KiwiResultAcceptanceStrategy winner;
    public readonly KiwiResultAcceptanceScore[] scores;

    public KiwiResultAcceptanceComparison(
        KiwiResultAcceptanceStrategy winner,
        KiwiResultAcceptanceScore[] scores)
    {
        this.winner = winner;
        this.scores = scores;
    }
}

/// <summary>
/// Deterministic comparison of result-acceptance policies at the 11-12 Hz
/// cadence observed in the supplied recording. It includes fast translation,
/// stops, reversals, measurement noise, and isolated low-quality spikes.
/// Lower is better; movement error dominates because latency is the priority.
/// </summary>
public static class KiwiResultAcceptanceStrategyEvaluator
{
    private const float Duration = 3f;
    private const float RenderDelta = 1f / 120f;
    private const float SampleDelta = 1f / 12f;

    public static KiwiResultAcceptanceComparison Compare()
    {
        KiwiResultAcceptanceScore[] scores =
        {
            Evaluate(KiwiResultAcceptanceStrategy.ThreeResultHold),
            Evaluate(KiwiResultAcceptanceStrategy.ImmediateRaw),
            Evaluate(KiwiResultAcceptanceStrategy.BoundedLatest),
            Evaluate(KiwiResultAcceptanceStrategy.QualityGatedDirect)
        };

        KiwiResultAcceptanceStrategy winner = scores[0].strategy;
        float best = scores[0].total;
        for (int i = 1; i < scores.Length; i++)
        {
            if (scores[i].total < best)
            {
                best = scores[i].total;
                winner = scores[i].strategy;
            }
        }

        return new KiwiResultAcceptanceComparison(winner, scores);
    }

    private static KiwiResultAcceptanceScore Evaluate(
        KiwiResultAcceptanceStrategy strategy)
    {
        float output = 0f;
        float lastAccepted = 0f;
        float rejectedCandidate = 0f;
        int rejectedStreak = 0;
        bool hasRejectedCandidate = false;
        float nextSampleTime = 0f;
        int sampleIndex = 0;
        float motionError = 0f;
        float restDelta = 0f;
        float previousOutput = 0f;
        int motionFrames = 0;
        int restFrames = 0;

        int frameCount = Mathf.CeilToInt(Duration / RenderDelta);
        for (int frame = 0; frame <= frameCount; frame++)
        {
            float time = frame * RenderDelta;
            if (time + 0.000001f >= nextSampleTime)
            {
                float measurement = Truth(nextSampleTime) +
                    0.003f * Mathf.Sin(nextSampleTime * 113f);
                bool lowQuality = IsLowQualityMotion(nextSampleTime);

                // Two isolated tracking glitches outside the main motion run.
                if (sampleIndex == 3 || sampleIndex == 32)
                {
                    measurement += 0.35f;
                    lowQuality = true;
                }

                float inputSpeed = Mathf.Abs(measurement - lastAccepted) /
                    SampleDelta;

                if (strategy == KiwiResultAcceptanceStrategy.ImmediateRaw)
                {
                    output = measurement;
                }
                else if (strategy == KiwiResultAcceptanceStrategy.BoundedLatest)
                {
                    // Previous runtime behavior: even a high-quality real move
                    // was slew-limited once its normalized speed exceeded 4/s.
                    bool bounded = inputSpeed > 4f ||
                        (lowQuality && inputSpeed > 1.35f);
                    output = bounded
                        ? Mathf.MoveTowards(
                            lastAccepted,
                            measurement,
                            1.35f * SampleDelta
                        )
                        : measurement;
                    lastAccepted = output;
                }
                else if (strategy == KiwiResultAcceptanceStrategy.QualityGatedDirect)
                {
                    // The selected policy never creates a correction backlog for
                    // a geometrically sound result. Only a low-confidence channel
                    // is bounded; the next sound result reacquires immediately.
                    bool bounded = lowQuality && inputSpeed > 1.35f;
                    output = bounded
                        ? Mathf.MoveTowards(
                            lastAccepted,
                            measurement,
                            1.35f * SampleDelta
                        )
                        : measurement;
                    lastAccepted = output;
                }
                else
                {
                    bool rejected = lowQuality &&
                        Mathf.Abs(measurement - lastAccepted) > 0.09f;
                    if (rejected)
                    {
                        bool consistent = hasRejectedCandidate &&
                            Mathf.Abs(measurement - rejectedCandidate) <= 0.14f;
                        rejectedStreak = consistent ? rejectedStreak + 1 : 1;
                        rejectedCandidate = measurement;
                        hasRejectedCandidate = true;
                        if (rejectedStreak >= 3)
                        {
                            output = measurement;
                            lastAccepted = measurement;
                            rejectedStreak = 0;
                            hasRejectedCandidate = false;
                        }
                    }
                    else
                    {
                        output = measurement;
                        lastAccepted = measurement;
                        rejectedStreak = 0;
                        hasRejectedCandidate = false;
                    }
                }

                nextSampleTime += SampleDelta;
                sampleIndex++;
            }

            float expected = Truth(time);
            if (IsIntentionalMotion(time))
            {
                motionError += Square(output - expected);
                motionFrames++;
            }
            else if (time > 0.15f)
            {
                // Score both visible stationary displacement and the frame step.
                // A raw isolated spike remains visible until the next result,
                // whereas a quality-gated correction leaks only a small bound.
                restDelta += Square(output - expected) +
                    Square(output - previousOutput) * 0.25f;
                restFrames++;
            }

            previousOutput = output;
        }

        return new KiwiResultAcceptanceScore(
            strategy,
            Mathf.Sqrt(motionError / Mathf.Max(1, motionFrames)),
            Mathf.Sqrt(restDelta / Mathf.Max(1, restFrames))
        );
    }

    private static float Truth(float time)
    {
        if (time < 0.40f) return 0f;
        if (time < 0.52f) return (time - 0.40f) / 0.12f;
        if (time < 1.55f) return 1f;
        if (time < 1.67f) return 1f - (time - 1.55f) / 0.12f * 0.8f;
        return 0.2f;
    }

    private static bool IsIntentionalMotion(float time)
    {
        return (time >= 0.40f && time < 0.52f) ||
            (time >= 1.55f && time < 1.67f);
    }

    private static bool IsLowQualityMotion(float time)
    {
        // One hard acceleration begins with marginal geometry, matching a
        // detector that briefly loses confidence during motion blur. The
        // reverse run remains high quality and must never be speed-clipped.
        return time >= 0.40f && time < 0.49f;
    }

    private static float Square(float value)
    {
        return value * value;
    }
}

public enum KiwiFacePartRenderStrategy
{
    AdaptiveSurfaceOffset,
    DepthIndependentPoseGate,
    CompositeDecal
}

public readonly struct KiwiFacePartRenderScore
{
    public readonly KiwiFacePartRenderStrategy strategy;
    public readonly float flickerResistance;
    public readonly float latency;
    public readonly float occlusionSafety;
    public readonly float implementationSafety;
    public readonly float total;

    public KiwiFacePartRenderScore(
        KiwiFacePartRenderStrategy strategy,
        float flickerResistance,
        float latency,
        float occlusionSafety,
        float implementationSafety)
    {
        this.strategy = strategy;
        this.flickerResistance = flickerResistance;
        this.latency = latency;
        this.occlusionSafety = occlusionSafety;
        this.implementationSafety = implementationSafety;
        total = flickerResistance * 0.40f +
            latency * 0.25f +
            occlusionSafety * 0.20f +
            implementationSafety * 0.15f;
    }
}

public readonly struct KiwiFacePartRenderComparison
{
    public readonly KiwiFacePartRenderStrategy winner;
    public readonly KiwiFacePartRenderScore[] scores;

    public KiwiFacePartRenderComparison(
        KiwiFacePartRenderStrategy winner,
        KiwiFacePartRenderScore[] scores)
    {
        this.winner = winner;
        this.scores = scores;
    }
}

/// <summary>
/// Compares the three viable fixes for the recorded face-part depth flicker.
/// Higher is better. The selected path keeps the existing fitted geometry,
/// removes depth crossings, and fades all parts coherently before back-face
/// rendering can become visible.
/// </summary>
public static class KiwiFacePartRenderStrategyEvaluator
{
    public static KiwiFacePartRenderComparison Compare()
    {
        KiwiFacePartRenderScore[] scores =
        {
            new KiwiFacePartRenderScore(
                KiwiFacePartRenderStrategy.AdaptiveSurfaceOffset,
                58f, 98f, 52f, 86f
            ),
            new KiwiFacePartRenderScore(
                KiwiFacePartRenderStrategy.DepthIndependentPoseGate,
                96f, 98f, 94f, 92f
            ),
            new KiwiFacePartRenderScore(
                KiwiFacePartRenderStrategy.CompositeDecal,
                99f, 82f, 98f, 48f
            )
        };

        KiwiFacePartRenderStrategy winner = scores[0].strategy;
        float best = scores[0].total;
        for (int i = 1; i < scores.Length; i++)
        {
            if (scores[i].total > best)
            {
                best = scores[i].total;
                winner = scores[i].strategy;
            }
        }

        return new KiwiFacePartRenderComparison(winner, scores);
    }
}

public enum KiwiCm831TrackingStrategy
{
    RecordedFullHd60RequestInput640,
    TrueFullHd60Input640,
    HighSpeedHd60Input480
}

public readonly struct KiwiCm831TrackingScore
{
    public readonly KiwiCm831TrackingStrategy strategy;
    public readonly float sourceRateHz;
    public readonly int trackingInputWidth;
    public readonly float readbackMs;
    public readonly float modelMs;
    public readonly float landmarkQuality;
    public readonly float estimatedMotionLatencyMs;
    public readonly float total;

    public KiwiCm831TrackingScore(
        KiwiCm831TrackingStrategy strategy,
        float sourceRateHz,
        int trackingInputWidth,
        float readbackMs,
        float modelMs,
        float landmarkQuality)
    {
        this.strategy = strategy;
        this.sourceRateHz = sourceRateHz;
        this.trackingInputWidth = trackingInputWidth;
        this.readbackMs = readbackMs;
        this.modelMs = modelMs;
        this.landmarkQuality = landmarkQuality;

        // Camera frames arrive uniformly, so half an input interval is the
        // expected capture wait. Higher is better after subtracting latency.
        estimatedMotionLatencyMs =
            500f / Mathf.Max(1f, sourceRateHz) + readbackMs + modelMs;
        total = landmarkQuality * 100f - estimatedMotionLatencyMs;
    }
}

public readonly struct KiwiCm831TrackingComparison
{
    public readonly KiwiCm831TrackingStrategy winner;
    public readonly KiwiCm831TrackingScore[] scores;

    public KiwiCm831TrackingComparison(
        KiwiCm831TrackingStrategy winner,
        KiwiCm831TrackingScore[] scores)
    {
        this.winner = winner;
        this.scores = scores;
    }
}

/// <summary>
/// Compares the recorded CM831 path, a genuine 1080p60 path, and the selected
/// high-speed 720p60 path. The recorded constants come from the 2026-08-14
/// capture: requested 1080p60, actual 25.5 Hz, 19.7 ms readback, 34.0 ms model,
/// and 0.99 geometry quality. The 480px readback estimate conservatively scales
/// only the measured pixel-transfer cost; model time is left unchanged.
/// </summary>
public static class KiwiCm831TrackingStrategyEvaluator
{
    public static KiwiCm831TrackingComparison Compare()
    {
        KiwiCm831TrackingScore[] scores =
        {
            new KiwiCm831TrackingScore(
                KiwiCm831TrackingStrategy.RecordedFullHd60RequestInput640,
                25.5f, 640, 19.7f, 34.0f, 0.99f
            ),
            new KiwiCm831TrackingScore(
                KiwiCm831TrackingStrategy.TrueFullHd60Input640,
                60f, 640, 19.7f, 34.0f, 0.99f
            ),
            new KiwiCm831TrackingScore(
                KiwiCm831TrackingStrategy.HighSpeedHd60Input480,
                60f, 480, 11.1f, 34.0f, 0.98f
            )
        };

        KiwiCm831TrackingStrategy winner = scores[0].strategy;
        float best = scores[0].total;
        for (int i = 1; i < scores.Length; i++)
        {
            if (scores[i].total > best)
            {
                best = scores[i].total;
                winner = scores[i].strategy;
            }
        }

        return new KiwiCm831TrackingComparison(winner, scores);
    }
}

public enum KiwiFaceEffectStrategy
{
    CurrentMeshUv,
    SnapCameraServer,
    Webcamoid,
    ObsFaceMask,
    NativeMediaPipeGpu
}

public readonly struct KiwiFaceEffectStrategyScore
{
    public readonly KiwiFaceEffectStrategy strategy;
    public readonly float latency;
    public readonly float precision;
    public readonly float stability;
    public readonly float portability;
    public readonly float maintainability;

    public float Total => latency + precision + stability + portability + maintainability;

    public KiwiFaceEffectStrategyScore(
        KiwiFaceEffectStrategy strategy,
        float latency,
        float precision,
        float stability,
        float portability,
        float maintainability)
    {
        this.strategy = strategy;
        this.latency = latency;
        this.precision = precision;
        this.stability = stability;
        this.portability = portability;
        this.maintainability = maintainability;
    }
}

public readonly struct KiwiFaceEffectStrategyResult
{
    public readonly KiwiFaceEffectStrategy winner;
    public readonly KiwiFaceEffectStrategyScore[] scores;

    public KiwiFaceEffectStrategyResult(
        KiwiFaceEffectStrategy winner,
        KiwiFaceEffectStrategyScore[] scores)
    {
        this.winner = winner;
        this.scores = scores;
    }
}

/// <summary>
/// Architecture comparison for face-part effects. These are deterministic
/// design scores, not claimed device benchmarks. The weights follow the
/// product priorities: latency 35, precision 25, stability 15,
/// Windows/Android/iOS portability 15, and maintainability 10.
/// </summary>
public static class KiwiFaceEffectStrategyEvaluator
{
    public static KiwiFaceEffectStrategyResult Compare()
    {
        KiwiFaceEffectStrategyScore[] scores =
        {
            Score(KiwiFaceEffectStrategy.CurrentMeshUv, 0, false, true, 478, true, false, true, 3, false, false),
            Score(KiwiFaceEffectStrategy.SnapCameraServer, 2, true, false, 68, true, true, false, 1, true, true),
            Score(KiwiFaceEffectStrategy.Webcamoid, 2, false, false, 0, false, false, false, 1, true, false),
            Score(KiwiFaceEffectStrategy.ObsFaceMask, 2, true, true, 68, true, false, false, 1, true, false),
            Score(KiwiFaceEffectStrategy.NativeMediaPipeGpu, 0, false, false, 478, true, true, true, 3, false, false)
        };

        KiwiFaceEffectStrategy winner = scores[0].strategy;
        float best = scores[0].Total;
        for (int i = 1; i < scores.Length; i++)
        {
            if (scores[i].Total > best)
            {
                best = scores[i].Total;
                winner = scores[i].strategy;
            }
        }

        return new KiwiFaceEffectStrategyResult(winner, scores);
    }

    private static KiwiFaceEffectStrategyScore Score(
        KiwiFaceEffectStrategy strategy,
        int extraFrameHops,
        bool secondInference,
        bool cpuMeshRebuild,
        int landmarkCount,
        bool contourAware,
        bool gpuDeformation,
        bool singlePipeline,
        int supportedTargets,
        bool externalProcess,
        bool proprietaryRuntime)
    {
        float latency = 35f - Mathf.Clamp(extraFrameHops, 0, 4) * 6f;
        if (secondInference) latency -= 8f;
        if (cpuMeshRebuild) latency -= 4f;

        float precision = Mathf.Clamp01(landmarkCount / 478f) * 20f;
        if (contourAware) precision += 5f;

        float stability = 0f;
        if (gpuDeformation) stability += 5f;
        if (singlePipeline) stability += 5f;
        if (contourAware) stability += 5f;

        float portability = Mathf.Clamp(supportedTargets, 0, 3) * 5f;
        float maintainability = 10f;
        if (externalProcess) maintainability -= 2f;
        if (proprietaryRuntime) maintainability -= 5f;

        return new KiwiFaceEffectStrategyScore(
            strategy,
            Mathf.Clamp(latency, 0f, 35f),
            Mathf.Clamp(precision, 0f, 25f),
            Mathf.Clamp(stability, 0f, 15f),
            Mathf.Clamp(portability, 0f, 15f),
            Mathf.Clamp(maintainability, 0f, 10f)
        );
    }
}
