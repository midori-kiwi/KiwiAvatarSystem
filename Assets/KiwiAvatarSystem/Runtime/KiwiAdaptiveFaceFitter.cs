using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public struct KiwiHeadGeometry
{
    public bool valid;
    public float confidence;
    public int sampleCount;
    public bool hasEyeBones;
    public bool hasEyeSemanticReference;
    public bool hasNeckBone;
    public bool usesSemanticBasis;
    public float basisConfidence;
    public int expansionStep;

    // Optional semantic eye geometry. When a VRM exposes Humanoid eye bones,
    // FaceAnchor placement can be mapped from eye spacing instead of relying
    // only on overall head bounds. This is substantially more robust for
    // non-spherical / stylized heads.
    public Vector3 eyeCenterHeadLocal;
    public float eyeSpanLocal;

    public Vector3 rightAxisHeadLocal;
    public Vector3 upAxisHeadLocal;
    public Vector3 outwardAxisHeadLocal;
    public Quaternion faceRotationHeadLocal;

    public float minRight;
    public float maxRight;
    public float minUp;
    public float maxUp;
    public float backDepth;
    public float frontDepth;

    public float WidthLocal => Mathf.Max(0f, maxRight - minRight);
    public float HeightLocal => Mathf.Max(0f, maxUp - minUp);
    public float DepthLocal => Mathf.Max(0f, frontDepth - backDepth);

    public Vector3 ComposeHeadLocal(float right, float up, float depth)
    {
        return rightAxisHeadLocal * right
            + upAxisHeadLocal * up
            + outwardAxisHeadLocal * depth;
    }

    public Vector3 ProjectHeadLocal(Vector3 point)
    {
        return new Vector3(
            Vector3.Dot(point, rightAxisHeadLocal),
            Vector3.Dot(point, upAxisHeadLocal),
            Vector3.Dot(point, outwardAxisHeadLocal)
        );
    }

    public float GetWorldWidth(Transform head)
    {
        if (head == null) return 0f;
        return WidthLocal * head.TransformVector(rightAxisHeadLocal).magnitude;
    }

    public float GetWorldHeight(Transform head)
    {
        if (head == null) return 0f;
        return HeightLocal * head.TransformVector(upAxisHeadLocal).magnitude;
    }

    public float GetWorldDepth(Transform head)
    {
        if (head == null) return 0f;
        return DepthLocal * head.TransformVector(outwardAxisHeadLocal).magnitude;
    }

    public float GetWorldEyeSpan(Transform head)
    {
        if (head == null || !hasEyeSemanticReference || eyeSpanLocal <= 0.000001f)
        {
            return 0f;
        }

        return eyeSpanLocal * head.TransformVector(rightAxisHeadLocal).magnitude;
    }
}

public static class KiwiAdaptiveFaceFitter
{
    private const int MinimumUsefulSamples = 96;
    private const int MinimumPreferredSamples = 240;

    public static KiwiHeadGeometry Analyze(
        Transform modelRoot,
        Transform head,
        Animator animator,
        Quaternion fallbackFaceLocalRotation,
        float fallbackOutwardSign,
        int maximumSamples)
    {
        KiwiHeadGeometry result = default;

        if (modelRoot == null || head == null)
        {
            return result;
        }

        maximumSamples = Mathf.Clamp(maximumSamples, 2000, 60000);

        Vector3 baseRight = (fallbackFaceLocalRotation * Vector3.right).normalized;
        Vector3 baseUp = (fallbackFaceLocalRotation * Vector3.up).normalized;
        Vector3 baseForward = (fallbackFaceLocalRotation * Vector3.forward).normalized;
        Vector3 baseOutward = baseForward * (fallbackOutwardSign >= 0f ? 1f : -1f);

        if (baseRight.sqrMagnitude < 0.9f ||
            baseUp.sqrMagnitude < 0.9f ||
            baseOutward.sqrMagnitude < 0.9f)
        {
            return result;
        }

        float eyeSpan = 0f;
        float neckDistance = 0f;
        bool hasEyes = false;
        bool hasNeck = false;
        Vector3 leftEyeLocal = Vector3.zero;
        Vector3 rightEyeLocal = Vector3.zero;
        Vector3 neckLocal = Vector3.zero;

        if (animator != null && animator.isHuman)
        {
            Transform leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            Transform rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
            if (leftEye != null && rightEye != null)
            {
                leftEyeLocal = head.InverseTransformPoint(leftEye.position);
                rightEyeLocal = head.InverseTransformPoint(rightEye.position);
                eyeSpan = Vector3.Distance(leftEyeLocal, rightEyeLocal);
                hasEyes = eyeSpan > 0.00001f;
            }

            Transform neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            if (neck != null)
            {
                neckLocal = head.InverseTransformPoint(neck.position);
                neckDistance = neckLocal.magnitude;
                hasNeck = neckDistance > 0.00001f;
            }
        }

        BuildFaceBasis(
            baseRight,
            baseUp,
            baseOutward,
            hasEyes,
            leftEyeLocal,
            rightEyeLocal,
            hasNeck,
            neckLocal,
            out Vector3 rightAxis,
            out Vector3 upAxis,
            out Vector3 outwardAxis,
            out Quaternion faceRotation,
            out bool usesSemanticBasis,
            out float basisConfidence
        );

        List<Vector3> points = new List<Vector3>(Mathf.Min(maximumSamples, 12000));
        CollectModelPoints(modelRoot, head, maximumSamples, points);

        if (points.Count < MinimumUsefulSamples)
        {
            return result;
        }

        float pointSeed = EstimateSeedRadius(points);
        float skeletonSeed = 0f;
        if (hasEyes)
        {
            skeletonSeed = Mathf.Max(skeletonSeed, eyeSpan * 1.75f);
        }
        if (hasNeck)
        {
            skeletonSeed = Mathf.Max(skeletonSeed, neckDistance * 2.10f);
        }

        float seed;
        if (skeletonSeed > 0.00001f)
        {
            seed = Mathf.Clamp(
                pointSeed,
                skeletonSeed * 0.80f,
                skeletonSeed * 2.20f
            );
        }
        else
        {
            seed = pointSeed;
        }
        seed = Mathf.Max(seed, 0.005f);

        int preferred = Mathf.Clamp(points.Count / 80, MinimumPreferredSamples, 1600);
        List<Vector3> selected = new List<Vector3>(Mathf.Min(points.Count, 5000));

        int expansionStep = 0;
        float[] expansions = { 1.0f, 1.35f, 1.8f, 2.4f, 3.2f };

        for (int e = 0; e < expansions.Length; e++)
        {
            selected.Clear();
            float radius = seed * expansions[e];

            for (int i = 0; i < points.Count; i++)
            {
                Vector3 p = points[i];
                float r = Vector3.Dot(p, rightAxis);
                float u = Vector3.Dot(p, upAxis);
                float d = Vector3.Dot(p, outwardAxis);

                if (Mathf.Abs(r) <= radius * 2.45f &&
                    u >= -radius * 1.85f &&
                    u <= radius * 2.85f &&
                    Mathf.Abs(d) <= radius * 2.65f)
                {
                    selected.Add(p);
                }
            }

            expansionStep = e;
            if (selected.Count >= preferred || e == expansions.Length - 1)
            {
                break;
            }
        }

        if (selected.Count < MinimumUsefulSamples)
        {
            return result;
        }

        List<float> rightValues = new List<float>(selected.Count);
        List<float> upValues = new List<float>(selected.Count);
        List<float> depthValues = new List<float>(selected.Count);

        for (int i = 0; i < selected.Count; i++)
        {
            Vector3 p = selected[i];
            rightValues.Add(Vector3.Dot(p, rightAxis));
            upValues.Add(Vector3.Dot(p, upAxis));
            depthValues.Add(Vector3.Dot(p, outwardAxis));
        }

        rightValues.Sort();
        upValues.Sort();
        depthValues.Sort();

        float minRight = Percentile(rightValues, 0.08f);
        float maxRight = Percentile(rightValues, 0.92f);
        float minUp = Percentile(upValues, 0.07f);
        float maxUp = Percentile(upValues, 0.91f);
        float backDepth = Percentile(depthValues, 0.08f);
        float frontDepth = Percentile(depthValues, 0.93f);

        float width = maxRight - minRight;
        float height = maxUp - minUp;
        float depth = frontDepth - backDepth;

        if (width <= 0.00001f || height <= 0.00001f || depth <= 0.000001f)
        {
            return result;
        }

        float aspect = width / Mathf.Max(height, 0.00001f);
        float geometryScore =
            aspect >= 0.22f && aspect <= 4.5f
                ? 0.28f
                : 0.10f;

        float sampleScore = Mathf.Lerp(
            0.10f,
            0.32f,
            Mathf.InverseLerp(MinimumUsefulSamples, 1800f, selected.Count));

        float skeletonScore = 0f;
        if (hasEyes) skeletonScore += 0.12f;
        if (hasNeck) skeletonScore += 0.08f;

        float expansionPenalty = expansionStep * 0.035f;
        float confidence = Mathf.Clamp01(
            sampleScore + skeletonScore + geometryScore + basisConfidence + 0.10f - expansionPenalty);

        result.valid = true;
        result.confidence = confidence;
        result.sampleCount = selected.Count;
        result.hasEyeBones = hasEyes;
        result.hasEyeSemanticReference = hasEyes;
        result.hasNeckBone = hasNeck;
        result.usesSemanticBasis = usesSemanticBasis;
        result.basisConfidence = basisConfidence;
        result.expansionStep = expansionStep;
        result.eyeCenterHeadLocal = hasEyes
            ? (leftEyeLocal + rightEyeLocal) * 0.5f
            : Vector3.zero;
        result.eyeSpanLocal = hasEyes ? eyeSpan : 0f;
        result.rightAxisHeadLocal = rightAxis;
        result.upAxisHeadLocal = upAxis;
        result.outwardAxisHeadLocal = outwardAxis;
        result.faceRotationHeadLocal = faceRotation;
        result.minRight = minRight;
        result.maxRight = maxRight;
        result.minUp = minUp;
        result.maxUp = maxUp;
        result.backDepth = backDepth;
        result.frontDepth = frontDepth;
        return result;
    }

    public static Vector3 MapReferenceAnchor(
        KiwiHeadGeometry reference,
        Vector3 referenceAnchorHeadLocal,
        KiwiHeadGeometry target,
        Vector3 additionalNormalizedOffset)
    {
        Vector3 referenceCoordinates = reference.ProjectHeadLocal(referenceAnchorHeadLocal);

        float nz = reference.DepthLocal > 0.000001f
            ? (referenceCoordinates.z - reference.frontDepth) / reference.DepthLocal
            : 0f;
        nz = Mathf.Clamp(nz, -1.0f, 1.0f);

        float right;
        float up;

        bool canUseEyeSemanticMap =
            reference.hasEyeSemanticReference &&
            target.hasEyeSemanticReference &&
            reference.eyeSpanLocal > 0.000001f &&
            target.eyeSpanLocal > 0.000001f;

        if (canUseEyeSemanticMap)
        {
            Vector3 referenceEye = reference.ProjectHeadLocal(reference.eyeCenterHeadLocal);
            Vector3 targetEye = target.ProjectHeadLocal(target.eyeCenterHeadLocal);

            float relativeRight =
                (referenceCoordinates.x - referenceEye.x) /
                reference.eyeSpanLocal;
            float relativeUp =
                (referenceCoordinates.y - referenceEye.y) /
                reference.eyeSpanLocal;

            relativeRight = Mathf.Clamp(relativeRight, -3.0f, 3.0f);
            relativeUp = Mathf.Clamp(relativeUp, -3.0f, 3.0f);

            right = targetEye.x + relativeRight * target.eyeSpanLocal;
            up = targetEye.y + relativeUp * target.eyeSpanLocal;
        }
        else
        {
            float nx = SafeInverseLerp(reference.minRight, reference.maxRight, referenceCoordinates.x);
            float ny = SafeInverseLerp(reference.minUp, reference.maxUp, referenceCoordinates.y);

            nx = Mathf.Clamp(nx, -0.25f, 1.25f);
            ny = Mathf.Clamp(ny, -0.25f, 1.25f);

            right = Mathf.LerpUnclamped(target.minRight, target.maxRight, nx);
            up = Mathf.LerpUnclamped(target.minUp, target.maxUp, ny);
        }

        right += target.WidthLocal * additionalNormalizedOffset.x;
        up += target.HeightLocal * additionalNormalizedOffset.y;

        float depth = target.frontDepth
            + target.DepthLocal * nz
            + target.DepthLocal * additionalNormalizedOffset.z;

        return target.ComposeHeadLocal(right, up, depth);
    }

    private static void BuildFaceBasis(
        Vector3 baseRight,
        Vector3 baseUp,
        Vector3 baseOutward,
        bool hasEyes,
        Vector3 leftEyeLocal,
        Vector3 rightEyeLocal,
        bool hasNeck,
        Vector3 neckLocal,
        out Vector3 rightAxis,
        out Vector3 upAxis,
        out Vector3 outwardAxis,
        out Quaternion faceRotation,
        out bool usesSemanticBasis,
        out float basisConfidence)
    {
        rightAxis = baseRight;
        upAxis = baseUp;
        outwardAxis = baseOutward;
        usesSemanticBasis = false;
        basisConfidence = 0.04f;

        bool semanticRightValid = false;
        bool semanticUpValid = false;
        Vector3 semanticRight = Vector3.zero;
        Vector3 semanticUp = Vector3.zero;

        if (hasEyes)
        {
            Vector3 eyeLine = rightEyeLocal - leftEyeLocal;
            if (eyeLine.sqrMagnitude > 0.00000001f)
            {
                semanticRight = eyeLine.normalized;
                semanticRightValid = true;
            }
        }

        if (hasNeck && neckLocal.sqrMagnitude > 0.00000001f)
        {
            // Neck is below the head, so Head -> opposite(Neck) is semantic up.
            semanticUp = (-neckLocal).normalized;
            semanticUpValid = true;
        }

        if (semanticRightValid && semanticUpValid)
        {
            Vector3 correctedUp = Vector3.ProjectOnPlane(semanticUp, semanticRight);
            if (correctedUp.sqrMagnitude > 0.00000001f)
            {
                correctedUp.Normalize();
                Vector3 semanticOutward = Vector3.Cross(semanticRight, correctedUp);
                if (semanticOutward.sqrMagnitude > 0.00000001f)
                {
                    semanticOutward.Normalize();
                    rightAxis = semanticRight;
                    upAxis = correctedUp;
                    outwardAxis = semanticOutward;
                    usesSemanticBasis = true;
                    basisConfidence = 0.18f;
                }
            }
        }
        else if (semanticRightValid)
        {
            Vector3 correctedUp = Vector3.ProjectOnPlane(baseUp, semanticRight);
            if (correctedUp.sqrMagnitude > 0.00000001f)
            {
                correctedUp.Normalize();
                Vector3 semanticOutward = Vector3.Cross(semanticRight, correctedUp);
                if (semanticOutward.sqrMagnitude > 0.00000001f)
                {
                    semanticOutward.Normalize();
                    rightAxis = semanticRight;
                    upAxis = correctedUp;
                    outwardAxis = semanticOutward;
                    usesSemanticBasis = true;
                    basisConfidence = 0.11f;
                }
            }
        }
        else if (semanticUpValid)
        {
            Vector3 correctedRight = Vector3.ProjectOnPlane(baseRight, semanticUp);
            if (correctedRight.sqrMagnitude > 0.00000001f)
            {
                correctedRight.Normalize();
                Vector3 semanticOutward = Vector3.Cross(correctedRight, semanticUp);
                if (semanticOutward.sqrMagnitude > 0.00000001f)
                {
                    semanticOutward.Normalize();
                    rightAxis = correctedRight;
                    upAxis = semanticUp;
                    outwardAxis = semanticOutward;
                    usesSemanticBasis = true;
                    basisConfidence = 0.08f;
                }
            }
        }

        // Keep a valid right-handed orthonormal basis even for malformed rigs.
        rightAxis.Normalize();
        upAxis = Vector3.ProjectOnPlane(upAxis, rightAxis).normalized;
        outwardAxis = Vector3.Cross(rightAxis, upAxis).normalized;

        if (rightAxis.sqrMagnitude < 0.9f ||
            upAxis.sqrMagnitude < 0.9f ||
            outwardAxis.sqrMagnitude < 0.9f)
        {
            rightAxis = baseRight;
            upAxis = baseUp;
            outwardAxis = baseOutward;
            usesSemanticBasis = false;
            basisConfidence = 0.04f;
        }

        faceRotation = Quaternion.LookRotation(outwardAxis, upAxis);
    }

    private static void CollectModelPoints(
        Transform modelRoot,
        Transform head,
        int maximumSamples,
        List<Vector3> destination)
    {
        Renderer[] renderers = modelRoot.GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0)
        {
            return;
        }

        Mesh bakedScratch = new Mesh();
        List<Vector3> vertexScratch = new List<Vector3>(4096);

        try
        {
            int renderersRemaining = renderers.Length;
            for (int i = 0; i < renderers.Length && destination.Count < maximumSamples; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    renderersRemaining--;
                    continue;
                }

                int remaining = maximumSamples - destination.Count;
                int perRendererBudget = Mathf.Max(64, remaining / Mathf.Max(1, renderersRemaining));

                try
                {
                    if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                    {
                        AppendSkinnedMeshPoints(
                            skinned,
                            head,
                            perRendererBudget,
                            destination,
                            bakedScratch,
                            vertexScratch
                        );
                    }
                    else
                    {
                        MeshFilter filter = renderer.GetComponent<MeshFilter>();
                        if (filter != null && filter.sharedMesh != null)
                        {
                            AppendStaticMeshPoints(
                                filter,
                                head,
                                perRendererBudget,
                                destination,
                                vertexScratch
                            );
                        }
                        else
                        {
                            AppendBoundsCorners(renderer.bounds, head, destination, maximumSamples);
                        }
                    }
                }
                catch
                {
                    AppendBoundsCorners(renderer.bounds, head, destination, maximumSamples);
                }

                renderersRemaining--;
            }
        }
        finally
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(bakedScratch);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(bakedScratch);
            }
        }
    }

    private static void AppendSkinnedMeshPoints(
        SkinnedMeshRenderer renderer,
        Transform head,
        int budget,
        List<Vector3> destination,
        Mesh bakedScratch,
        List<Vector3> vertexScratch)
    {
        bakedScratch.Clear(false);
        vertexScratch.Clear();
        renderer.BakeMesh(bakedScratch, false);
        bakedScratch.GetVertices(vertexScratch);
        AppendTransformedPoints(vertexScratch, renderer.transform, head, budget, destination);
    }

    private static void AppendStaticMeshPoints(
        MeshFilter filter,
        Transform head,
        int budget,
        List<Vector3> destination,
        List<Vector3> vertexScratch)
    {
        vertexScratch.Clear();
        filter.sharedMesh.GetVertices(vertexScratch);
        AppendTransformedPoints(vertexScratch, filter.transform, head, budget, destination);
    }

    private static void AppendTransformedPoints(
        List<Vector3> vertices,
        Transform source,
        Transform head,
        int budget,
        List<Vector3> destination)
    {
        if (vertices == null || vertices.Count == 0 || budget <= 0)
        {
            return;
        }

        int stride = Mathf.Max(1, Mathf.CeilToInt(vertices.Count / (float)budget));
        int offset = vertices.Count > 1
            ? Mathf.Abs(source.GetInstanceID()) % stride
            : 0;

        for (int i = offset; i < vertices.Count && budget > 0; i += stride)
        {
            Vector3 world = source.TransformPoint(vertices[i]);
            destination.Add(head.InverseTransformPoint(world));
            budget--;
        }
    }

    private static void AppendBoundsCorners(
        Bounds bounds,
        Transform head,
        List<Vector3> destination,
        int maximumSamples)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        for (int x = 0; x < 2 && destination.Count < maximumSamples; x++)
        {
            for (int y = 0; y < 2 && destination.Count < maximumSamples; y++)
            {
                for (int z = 0; z < 2 && destination.Count < maximumSamples; z++)
                {
                    Vector3 world = new Vector3(
                        x == 0 ? min.x : max.x,
                        y == 0 ? min.y : max.y,
                        z == 0 ? min.z : max.z
                    );
                    destination.Add(head.InverseTransformPoint(world));
                }
            }
        }
    }

    private static float EstimateSeedRadius(List<Vector3> points)
    {
        if (points == null || points.Count == 0)
        {
            return 0.05f;
        }

        int sampleCount = Mathf.Min(points.Count, 4096);
        int stride = Mathf.Max(1, points.Count / sampleCount);
        List<float> distances = new List<float>(sampleCount);

        for (int i = 0; i < points.Count && distances.Count < sampleCount; i += stride)
        {
            distances.Add(points[i].magnitude);
        }

        distances.Sort();
        float p12 = Percentile(distances, 0.12f);
        float p25 = Percentile(distances, 0.25f);
        return Mathf.Max(0.005f, Mathf.Lerp(p12, p25, 0.35f) * 1.20f);
    }

    private static float Percentile(List<float> sorted, float t)
    {
        if (sorted == null || sorted.Count == 0)
        {
            return 0f;
        }

        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        float position = Mathf.Clamp01(t) * (sorted.Count - 1);
        int lower = Mathf.FloorToInt(position);
        int upper = Mathf.Min(sorted.Count - 1, lower + 1);
        float fraction = position - lower;
        return Mathf.Lerp(sorted[lower], sorted[upper], fraction);
    }

    private static float SafeInverseLerp(float a, float b, float value)
    {
        float length = b - a;
        if (Mathf.Abs(length) <= 0.000001f)
        {
            return 0.5f;
        }
        return (value - a) / length;
    }
}
