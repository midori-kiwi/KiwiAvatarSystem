using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

[DisallowMultipleComponent]
internal sealed class KiwiAvatarOptimizedMeshBinding : MonoBehaviour
{
    [NonSerialized] internal SkinnedMeshRenderer renderer;
    [NonSerialized] internal Mesh originalMesh;
    [NonSerialized] internal Mesh optimizedMesh;

    internal void Bind(
        SkinnedMeshRenderer target,
        Mesh original,
        Mesh optimized)
    {
        renderer = target;
        originalMesh = original;
        optimizedMesh = optimized;
    }

    internal bool IsValidFor(
        SkinnedMeshRenderer target,
        Mesh currentOriginal)
    {
        return
            renderer == target &&
            originalMesh == currentOriginal &&
            optimizedMesh != null;
    }

    internal void RestoreOriginal()
    {
        if (
            renderer != null &&
            originalMesh != null &&
            renderer.sharedMesh != originalMesh)
        {
            renderer.sharedMesh =
                originalMesh;
        }
    }

    internal void ApplyOptimized()
    {
        if (
            renderer != null &&
            optimizedMesh != null &&
            renderer.sharedMesh != optimizedMesh)
        {
            renderer.sharedMesh =
                optimizedMesh;
        }
    }

    private void OnDestroy()
    {
        if (optimizedMesh != null)
        {
            Destroy(optimizedMesh);
            optimizedMesh = null;
        }
    }
}

public static class KiwiAvatarRenderMeshOptimizer
{
    private const string EnableEnvironment =
        "KIWI_AVATAR_MESHOPT_ENABLE";

    private const string RatioEnvironment =
        "KIWI_AVATAR_MESHOPT_RATIO";

    private const string ErrorEnvironment =
        "KIWI_AVATAR_MESHOPT_ERROR";

    private const string MinimumTrianglesEnvironment =
        "KIWI_AVATAR_MESHOPT_MIN_TRIANGLES";

    private const string Contract =
        "KIWI_V5_1_PHASE16_20_37_V44_6_MESHOPT_INDEX_ONLY";

    private const float DefaultRatio = 0.25f;
    private const float DefaultTargetError = 0.01f;
    private const int DefaultMinimumTriangles = 200000;
    private const int MaximumAttributeCount = 32;
    private const int MaximumMorphAttributes = 6;

    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private static bool _loggedPolicy;

    public static void RestoreOriginalMeshesForFit(
        Transform modelRoot)
    {
        if (modelRoot == null)
        {
            return;
        }

        KiwiAvatarOptimizedMeshBinding[] bindings =
            modelRoot.GetComponentsInChildren
                <KiwiAvatarOptimizedMeshBinding>(true);

        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i] != null)
            {
                bindings[i].RestoreOriginal();
            }
        }
    }

    public static void ApplyOptimizedRenderMeshesAfterFit(
        Transform modelRoot,
        string context)
    {
        if (modelRoot == null)
        {
            return;
        }

        Policy policy =
            ReadPolicy();

        LogPolicyOnce(policy);

        if (!policy.enabled)
        {
            return;
        }

        SkinnedMeshRenderer[] renderers =
            modelRoot.GetComponentsInChildren
                <SkinnedMeshRenderer>(true);

        for (int i = 0; i < renderers.Length; i++)
        {
            SkinnedMeshRenderer renderer =
                renderers[i];

            if (
                renderer == null ||
                renderer.sharedMesh == null ||
                !renderer.enabled)
            {
                continue;
            }

            Mesh original =
                ResolveOriginalMesh(renderer);

            if (
                original == null ||
                !IsEligible(
                    renderer,
                    original,
                    policy,
                    out string skipReason))
            {
                if (
                    original != null &&
                    CountTriangles(original) >=
                    policy.minimumTriangles)
                {
                    UnityEngine.Debug.Log(
                        "[KiwiMeshOpt] skip renderer='" +
                        GetHierarchyPath(renderer.transform) +
                        "' reason=" +
                        skipReason);
                }

                continue;
            }

            KiwiAvatarOptimizedMeshBinding binding =
                renderer.GetComponent
                    <KiwiAvatarOptimizedMeshBinding>();

            if (
                binding != null &&
                binding.IsValidFor(
                    renderer,
                    original))
            {
                binding.ApplyOptimized();
                continue;
            }

            if (
                TryBuildOptimizedMesh(
                    renderer,
                    original,
                    policy,
                    context,
                    out Mesh optimized,
                    out OptimizationReport report))
            {
                if (binding == null)
                {
                    binding =
                        renderer.gameObject.AddComponent
                            <KiwiAvatarOptimizedMeshBinding>();
                }

                binding.Bind(
                    renderer,
                    original,
                    optimized);

                renderer.sharedMesh =
                    optimized;

                WriteReport(report);

                UnityEngine.Debug.Log(
                    "[KiwiMeshOpt] APPLIED renderer='" +
                    report.rendererPath +
                    "' triangles=" +
                    report.originalTriangles +
                    "->" +
                    report.resultTriangles +
                    " ratio=" +
                    report.actualRatio.ToString("F4", Invariant) +
                    " error=" +
                    report.resultError.ToString("F6", Invariant) +
                    " elapsedMs=" +
                    report.elapsedMs.ToString("F2", Invariant));
            }
            else
            {
                UnityEngine.Debug.LogWarning(
                    "[KiwiMeshOpt] retained original renderer='" +
                    GetHierarchyPath(renderer.transform) +
                    "' reason=" +
                    report.message);
            }
        }
    }

    private struct Policy
    {
        public bool enabled;
        public float ratio;
        public float targetError;
        public int minimumTriangles;
    }

    private struct OptimizationReport
    {
        public string context;
        public string rendererPath;
        public string meshName;
        public int vertexCount;
        public long originalTriangles;
        public long targetTriangles;
        public long resultTriangles;
        public float requestedRatio;
        public float actualRatio;
        public float targetError;
        public float resultError;
        public int blendShapeCount;
        public int boneCount;
        public int subMeshCount;
        public int attributeCount;
        public int selectedMorphCount;
        public int priorityVertexCount;
        public double elapsedMs;
        public string message;
    }

    private static Policy ReadPolicy()
    {
        Policy policy =
            new Policy
            {
                enabled =
                    IsEnabled(
                        EnableEnvironment),
                ratio =
                    ReadFloat(
                        RatioEnvironment,
                        DefaultRatio,
                        0.05f,
                        1f),
                targetError =
                    ReadFloat(
                        ErrorEnvironment,
                        DefaultTargetError,
                        0.00001f,
                        0.25f),
                minimumTriangles =
                    ReadInt(
                        MinimumTrianglesEnvironment,
                        DefaultMinimumTriangles,
                        10000,
                        5000000)
            };

        return policy;
    }

    private static void LogPolicyOnce(
        Policy policy)
    {
        if (_loggedPolicy)
        {
            return;
        }

        _loggedPolicy = true;

        UnityEngine.Debug.Log(
            "[KiwiMeshOpt] contract=" +
            Contract +
            " enabled=" +
            (policy.enabled ? "1" : "0") +
            " ratio=" +
            policy.ratio.ToString("F4", Invariant) +
            " targetError=" +
            policy.targetError.ToString("F6", Invariant) +
            " minTriangles=" +
            policy.minimumTriangles +
            " method=meshoptimizer_simplifyWithAttributes_INDEX_ONLY" +
            " options=REGULARIZE_LIGHT" +
            " fitAuthority=ORIGINAL_MESH");
    }

    private static Mesh ResolveOriginalMesh(
        SkinnedMeshRenderer renderer)
    {
        KiwiAvatarOptimizedMeshBinding binding =
            renderer.GetComponent
                <KiwiAvatarOptimizedMeshBinding>();

        if (
            binding != null &&
            binding.originalMesh != null)
        {
            return binding.originalMesh;
        }

        return renderer.sharedMesh;
    }

    private static bool IsEligible(
        SkinnedMeshRenderer renderer,
        Mesh mesh,
        Policy policy,
        out string reason)
    {
        if (!mesh.isReadable)
        {
            reason =
                "mesh_not_readable";
            return false;
        }

        if (mesh.subMeshCount != 1)
        {
            reason =
                "submesh_count_not_one";
            return false;
        }

        if (
            renderer.sharedMaterials == null ||
            renderer.sharedMaterials.Length != 1)
        {
            reason =
                "material_count_not_one";
            return false;
        }

        if (
            CountTriangles(mesh) <
            policy.minimumTriangles)
        {
            reason =
                "below_triangle_threshold";
            return false;
        }

        if (
            mesh.vertexCount <= 0 ||
            mesh.vertexCount > 4000000)
        {
            reason =
                "unsupported_vertex_count";
            return false;
        }

        if (
            renderer.bones == null ||
            renderer.bones.Length <= 0)
        {
            reason =
                "no_skin_bones";
            return false;
        }

        try
        {
            if (
                mesh.bindposes == null ||
                mesh.bindposes.Length !=
                renderer.bones.Length)
            {
                reason =
                    "bindpose_bone_mismatch";
                return false;
            }

            if (
                mesh.boneWeights == null ||
                mesh.boneWeights.Length !=
                mesh.vertexCount)
            {
                reason =
                    "legacy_bone_weights_unavailable";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason =
                "skinning_read_failed:" +
                ex.GetType().Name;
            return false;
        }

        reason =
            string.Empty;
        return true;
    }

    private static bool TryBuildOptimizedMesh(
        SkinnedMeshRenderer renderer,
        Mesh original,
        Policy policy,
        string context,
        out Mesh optimized,
        out OptimizationReport report)
    {
        optimized = null;

        report =
            new OptimizationReport
            {
                context =
                    context ?? string.Empty,
                rendererPath =
                    GetHierarchyPath(
                        renderer.transform),
                meshName =
                    original.name,
                vertexCount =
                    original.vertexCount,
                originalTriangles =
                    CountTriangles(original),
                requestedRatio =
                    policy.ratio,
                targetError =
                    policy.targetError,
                blendShapeCount =
                    original.blendShapeCount,
                boneCount =
                    renderer.bones != null
                        ? renderer.bones.Length
                        : 0,
                subMeshCount =
                    original.subMeshCount
            };

        Stopwatch stopwatch =
            Stopwatch.StartNew();

        try
        {
            Vector3[] positions =
                original.vertices;

            Vector3[] normals =
                original.normals;

            Vector4[] tangents =
                original.tangents;

            Color[] colors =
                original.colors;

            Vector2[] uv0 =
                original.uv;

            Vector2[] uv1 =
                original.uv2;

            int vertexCount =
                original.vertexCount;

            if (
                positions == null ||
                positions.Length != vertexCount)
            {
                report.message =
                    "positions_unavailable";
                return false;
            }

            int[] indices =
                original.GetIndices(
                    0,
                    true);

            if (
                indices == null ||
                indices.Length < 3)
            {
                report.message =
                    "indices_unavailable";
                return false;
            }

            int targetIndexCount =
                Mathf.Clamp(
                    (int)Math.Floor(
                        indices.Length *
                        policy.ratio /
                        3.0) *
                    3,
                    3,
                    indices.Length);

            report.targetTriangles =
                targetIndexCount / 3L;

            MorphSelection morphSelection =
                BuildMorphSelection(
                    original,
                    vertexCount);

            AttributeLayout layout =
                BuildAttributes(
                    original,
                    positions,
                    normals,
                    tangents,
                    colors,
                    uv0,
                    uv1,
                    morphSelection);

            report.attributeCount =
                layout.attributeCount;
            report.selectedMorphCount =
                morphSelection.selected.Count;
            report.priorityVertexCount =
                morphSelection.priorityVertexCount;

            float[] positionsXYZ =
                new float[
                    vertexCount *
                    3];

            for (int i = 0; i < vertexCount; i++)
            {
                int p = i * 3;

                positionsXYZ[p + 0] =
                    positions[i].x;
                positionsXYZ[p + 1] =
                    positions[i].y;
                positionsXYZ[p + 2] =
                    positions[i].z;
            }

            int[] destination =
                new int[
                    indices.Length];

            bool simplified =
                KiwiMeshOptimizerInterop
                    .TrySimplifyWithAttributes(
                        indices,
                        positionsXYZ,
                        vertexCount,
                        layout.values,
                        layout.attributeCount,
                        layout.weights,
                        morphSelection.vertexFlags,
                        targetIndexCount,
                        policy.targetError,
                        KiwiMeshOptimizerInterop
                            .SimplifyRegularizeLight,
                        destination,
                        out int outputIndexCount,
                        out float resultError,
                        out string nativeError);

            if (!simplified)
            {
                report.message =
                    nativeError;
                return false;
            }

            if (
                outputIndexCount >=
                indices.Length)
            {
                report.message =
                    "simplifier_did_not_reduce_indices";
                return false;
            }

            int[] compactIndices =
                new int[
                    outputIndexCount];

            Array.Copy(
                destination,
                compactIndices,
                outputIndexCount);

            Mesh clone =
                UnityEngine.Object.Instantiate(
                    original);

            clone.name =
                original.name +
                ".kiwi.meshopt." +
                policy.ratio.ToString(
                    "0.###",
                    Invariant);

            clone.SetIndices(
                compactIndices,
                MeshTopology.Triangles,
                0,
                false);

            clone.bounds =
                original.bounds;

            if (
                clone.vertexCount !=
                    original.vertexCount ||
                clone.blendShapeCount !=
                    original.blendShapeCount ||
                clone.bindposes.Length !=
                    original.bindposes.Length ||
                clone.boneWeights.Length !=
                    original.boneWeights.Length ||
                clone.subMeshCount != 1)
            {
                UnityEngine.Object.Destroy(
                    clone);

                report.message =
                    "post_clone_contract_failed";
                return false;
            }

            for (
                int i = 0;
                i < original.blendShapeCount;
                i++)
            {
                if (
                    !string.Equals(
                        original.GetBlendShapeName(i),
                        clone.GetBlendShapeName(i),
                        StringComparison.Ordinal))
                {
                    UnityEngine.Object.Destroy(
                        clone);

                    report.message =
                        "blendshape_order_changed";
                    return false;
                }
            }

            report.resultError =
                resultError;
            report.resultTriangles =
                outputIndexCount / 3L;
            report.actualRatio =
                report.originalTriangles > 0
                    ? (float)report.resultTriangles /
                      report.originalTriangles
                    : 1f;
            report.message =
                "OK";

            optimized =
                clone;

            return true;
        }
        catch (Exception ex)
        {
            report.message =
                ex.GetType().Name +
                ": " +
                ex.Message;

            if (optimized != null)
            {
                UnityEngine.Object.Destroy(
                    optimized);
                optimized = null;
            }

            return false;
        }
        finally
        {
            stopwatch.Stop();
            report.elapsedMs =
                stopwatch.Elapsed.TotalMilliseconds;
        }
    }

    private struct SelectedMorph
    {
        public int index;
        public float energy;
        public Vector3[] deltaVertices;
    }

    private sealed class MorphSelection
    {
        public readonly List<SelectedMorph> selected =
            new List<SelectedMorph>();
        public byte[] vertexFlags;
        public int priorityVertexCount;
    }

    private struct AttributeLayout
    {
        public float[] values;
        public float[] weights;
        public int attributeCount;
    }

    private static MorphSelection BuildMorphSelection(
        Mesh mesh,
        int vertexCount)
    {
        MorphSelection result =
            new MorphSelection
            {
                vertexFlags =
                    new byte[
                        vertexCount]
            };

        if (
            mesh.blendShapeCount <= 0)
        {
            return result;
        }

        List<SelectedMorph> candidates =
            new List<SelectedMorph>();

        Vector3[] deltaVertices =
            new Vector3[
                vertexCount];

        Vector3[] deltaNormals =
            new Vector3[
                vertexCount];

        Vector3[] deltaTangents =
            new Vector3[
                vertexCount];

        for (
            int shapeIndex = 0;
            shapeIndex < mesh.blendShapeCount;
            shapeIndex++)
        {
            int frames =
                mesh.GetBlendShapeFrameCount(
                    shapeIndex);

            if (frames <= 0)
            {
                continue;
            }

            Array.Clear(
                deltaVertices,
                0,
                deltaVertices.Length);

            Array.Clear(
                deltaNormals,
                0,
                deltaNormals.Length);

            Array.Clear(
                deltaTangents,
                0,
                deltaTangents.Length);

            mesh.GetBlendShapeFrameVertices(
                shapeIndex,
                frames - 1,
                deltaVertices,
                deltaNormals,
                deltaTangents);

            double energy = 0.0;

            Vector3[] copy =
                new Vector3[
                    vertexCount];

            for (int v = 0; v < vertexCount; v++)
            {
                Vector3 d =
                    deltaVertices[v];

                copy[v] =
                    d;

                float sq =
                    d.sqrMagnitude;

                energy +=
                    sq;

                if (sq > 1e-10f)
                {
                    result.vertexFlags[v] |=
                        KiwiMeshOptimizerInterop
                            .SimplifyVertexPriority;
                }
            }

            candidates.Add(
                new SelectedMorph
                {
                    index =
                        shapeIndex,
                    energy =
                        (float)energy,
                    deltaVertices =
                        copy
                });
        }

        candidates.Sort(
            (a, b) =>
                b.energy.CompareTo(
                    a.energy));

        int count =
            Math.Min(
                MaximumMorphAttributes,
                candidates.Count);

        for (int i = 0; i < count; i++)
        {
            result.selected.Add(
                candidates[i]);
        }

        for (
            int i = 0;
            i < result.vertexFlags.Length;
            i++)
        {
            if (
                result.vertexFlags[i] != 0)
            {
                result.priorityVertexCount++;
            }
        }

        return result;
    }

    private static AttributeLayout BuildAttributes(
        Mesh mesh,
        Vector3[] positions,
        Vector3[] normals,
        Vector4[] tangents,
        Color[] colors,
        Vector2[] uv0,
        Vector2[] uv1,
        MorphSelection morphSelection)
    {
        int vertexCount =
            positions.Length;

        bool hasNormals =
            normals != null &&
            normals.Length ==
            vertexCount;

        bool hasTangents =
            tangents != null &&
            tangents.Length ==
            vertexCount;

        bool hasColors =
            colors != null &&
            colors.Length ==
            vertexCount;

        bool hasUv0 =
            uv0 != null &&
            uv0.Length ==
            vertexCount;

        bool hasUv1 =
            uv1 != null &&
            uv1.Length ==
            vertexCount;

        int attributeCount = 0;

        if (hasNormals)
        {
            attributeCount += 3;
        }

        if (hasTangents)
        {
            attributeCount += 3;
        }

        if (hasColors)
        {
            attributeCount += 4;
        }

        if (hasUv0)
        {
            attributeCount += 2;
        }

        if (hasUv1)
        {
            attributeCount += 2;
        }

        int availableMorphFloats =
            Math.Max(
                0,
                MaximumAttributeCount -
                attributeCount);

        int morphCount =
            Math.Min(
                morphSelection.selected.Count,
                availableMorphFloats / 3);

        attributeCount +=
            morphCount * 3;

        if (
            attributeCount <= 0 ||
            attributeCount >
            MaximumAttributeCount)
        {
            throw new InvalidOperationException(
                "Invalid meshoptimizer attribute count: " +
                attributeCount);
        }

        float[] values =
            new float[
                vertexCount *
                attributeCount];

        float[] weights =
            new float[
                attributeCount];

        int weightOffset = 0;

        if (hasNormals)
        {
            for (int k = 0; k < 3; k++)
            {
                weights[weightOffset++] =
                    0.5f;
            }
        }

        if (hasTangents)
        {
            for (int k = 0; k < 3; k++)
            {
                weights[weightOffset++] =
                    0.25f;
            }
        }

        if (hasColors)
        {
            for (int k = 0; k < 4; k++)
            {
                weights[weightOffset++] =
                    0.25f;
            }
        }

        float uvWeight =
            EstimateUvWeight(
                mesh,
                positions,
                uv0);

        if (hasUv0)
        {
            weights[weightOffset++] =
                uvWeight;
            weights[weightOffset++] =
                uvWeight;
        }

        if (hasUv1)
        {
            weights[weightOffset++] =
                Math.Max(
                    1f,
                    uvWeight * 0.25f);
            weights[weightOffset++] =
                Math.Max(
                    1f,
                    uvWeight * 0.25f);
        }

        for (int m = 0; m < morphCount; m++)
        {
            weights[weightOffset++] =
                1.0f;
            weights[weightOffset++] =
                1.0f;
            weights[weightOffset++] =
                1.0f;
        }

        for (int v = 0; v < vertexCount; v++)
        {
            int dst =
                v *
                attributeCount;

            if (hasNormals)
            {
                Vector3 n =
                    normals[v];

                values[dst++] = n.x;
                values[dst++] = n.y;
                values[dst++] = n.z;
            }

            if (hasTangents)
            {
                Vector4 t =
                    tangents[v];

                values[dst++] = t.x;
                values[dst++] = t.y;
                values[dst++] = t.z;
            }

            if (hasColors)
            {
                Color c =
                    colors[v];

                values[dst++] = c.r;
                values[dst++] = c.g;
                values[dst++] = c.b;
                values[dst++] = c.a;
            }

            if (hasUv0)
            {
                Vector2 uv =
                    uv0[v];

                values[dst++] = uv.x;
                values[dst++] = uv.y;
            }

            if (hasUv1)
            {
                Vector2 uv =
                    uv1[v];

                values[dst++] = uv.x;
                values[dst++] = uv.y;
            }

            for (int m = 0; m < morphCount; m++)
            {
                Vector3 d =
                    morphSelection
                        .selected[m]
                        .deltaVertices[v];

                values[dst++] = d.x;
                values[dst++] = d.y;
                values[dst++] = d.z;
            }
        }

        return new AttributeLayout
        {
            values =
                values,
            weights =
                weights,
            attributeCount =
                attributeCount
        };
    }

    private static float EstimateUvWeight(
        Mesh mesh,
        Vector3[] positions,
        Vector2[] uv)
    {
        if (
            uv == null ||
            uv.Length !=
            positions.Length)
        {
            return 1f;
        }

        try
        {
            int[] indices =
                mesh.GetIndices(
                    0,
                    true);

            double uvAreaSum = 0.0;
            int triangleCount = 0;

            for (
                int i = 0;
                i + 2 < indices.Length;
                i += 3)
            {
                int a =
                    indices[i + 0];
                int b =
                    indices[i + 1];
                int c =
                    indices[i + 2];

                Vector2 ua =
                    uv[a];
                Vector2 ub =
                    uv[b];
                Vector2 uc =
                    uv[c];

                double area =
                    Math.Abs(
                        (
                            (ub.x - ua.x) *
                            (uc.y - ua.y)
                        ) -
                        (
                            (ub.y - ua.y) *
                            (uc.x - ua.x)
                        )
                    ) *
                    0.5;

                uvAreaSum +=
                    area;
                triangleCount++;
            }

            if (
                triangleCount <= 0 ||
                uvAreaSum <= 1e-12)
            {
                return 10f;
            }

            double averageArea =
                uvAreaSum /
                triangleCount;

            double weight =
                1.0 /
                Math.Sqrt(
                    averageArea);

            return
                Mathf.Clamp(
                    (float)weight,
                    1f,
                    100f);
        }
        catch
        {
            return 10f;
        }
    }

    private static long CountTriangles(
        Mesh mesh)
    {
        if (mesh == null)
        {
            return 0L;
        }

        long triangles = 0L;

        try
        {
            for (
                int i = 0;
                i < mesh.subMeshCount;
                i++)
            {
                if (
                    mesh.GetTopology(i) !=
                    MeshTopology.Triangles)
                {
                    continue;
                }

                triangles +=
                    (long)mesh.GetIndexCount(i) /
                    3L;
            }
        }
        catch
        {
        }

        return triangles;
    }

    private static void WriteReport(
        OptimizationReport report)
    {
        try
        {
            string directory =
                Path.Combine(
                    Application.persistentDataPath,
                    "KiwiAvatarMeshOptimization");

            Directory.CreateDirectory(
                directory);

            string path =
                Path.Combine(
                    directory,
                    "KiwiAvatarMeshOptimization_" +
                    DateTime.Now.ToString(
                        "yyyyMMdd_HHmmss_fff",
                        Invariant) +
                    ".txt");

            StringBuilder sb =
                new StringBuilder(4096);

            Append(sb, "contract", Contract);
            Append(sb, "context", report.context);
            Append(sb, "rendererPath", report.rendererPath);
            Append(sb, "meshName", report.meshName);
            Append(sb, "vertexCount", report.vertexCount);
            Append(sb, "originalTriangles", report.originalTriangles);
            Append(sb, "targetTriangles", report.targetTriangles);
            Append(sb, "resultTriangles", report.resultTriangles);
            Append(sb, "requestedRatio", report.requestedRatio);
            Append(sb, "actualRatio", report.actualRatio);
            Append(sb, "targetError", report.targetError);
            Append(sb, "resultError", report.resultError);
            Append(sb, "blendShapeCount", report.blendShapeCount);
            Append(sb, "boneCount", report.boneCount);
            Append(sb, "subMeshCount", report.subMeshCount);
            Append(sb, "attributeCount", report.attributeCount);
            Append(sb, "selectedMorphCount", report.selectedMorphCount);
            Append(sb, "priorityVertexCount", report.priorityVertexCount);
            Append(sb, "elapsedMs", report.elapsedMs);
            Append(sb, "message", report.message);
            Append(sb, "vertexDataCompacted", 0);
            Append(sb, "blendShapeOrderPreserved", 1);
            Append(sb, "boneWeightsPreservedBitExactByVertex", 1);
            Append(sb, "fitAuthority", "ORIGINAL_MESH");

            File.WriteAllText(
                path,
                sb.ToString(),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning(
                "[KiwiMeshOpt] report write failed: " +
                ex.Message);
        }
    }

    private static string GetHierarchyPath(
        Transform transform)
    {
        if (transform == null)
        {
            return string.Empty;
        }

        Stack<string> names =
            new Stack<string>();

        Transform current =
            transform;

        while (current != null)
        {
            names.Push(
                current.name);
            current =
                current.parent;
        }

        return string.Join(
            "/",
            names);
    }

    private static bool IsEnabled(
        string name)
    {
        string value =
            Environment.GetEnvironmentVariable(
                name);

        return
            string.Equals(
                value,
                "1",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                value,
                "true",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                value,
                "on",
                StringComparison.OrdinalIgnoreCase);
    }

    private static float ReadFloat(
        string name,
        float fallback,
        float minimum,
        float maximum)
    {
        string value =
            Environment.GetEnvironmentVariable(
                name);

        if (
            float.TryParse(
                value,
                NumberStyles.Float,
                Invariant,
                out float parsed) &&
            !float.IsNaN(parsed) &&
            !float.IsInfinity(parsed))
        {
            return
                Mathf.Clamp(
                    parsed,
                    minimum,
                    maximum);
        }

        return fallback;
    }

    private static int ReadInt(
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        string value =
            Environment.GetEnvironmentVariable(
                name);

        if (
            int.TryParse(
                value,
                NumberStyles.Integer,
                Invariant,
                out int parsed))
        {
            return
                Mathf.Clamp(
                    parsed,
                    minimum,
                    maximum);
        }

        return fallback;
    }

    private static void Append(
        StringBuilder sb,
        string name,
        string value)
    {
        sb.Append(name);
        sb.Append('=');
        sb.AppendLine(
            value ?? string.Empty);
    }

    private static void Append(
        StringBuilder sb,
        string name,
        int value)
    {
        Append(
            sb,
            name,
            value.ToString(Invariant));
    }

    private static void Append(
        StringBuilder sb,
        string name,
        long value)
    {
        Append(
            sb,
            name,
            value.ToString(Invariant));
    }

    private static void Append(
        StringBuilder sb,
        string name,
        float value)
    {
        Append(
            sb,
            name,
            value.ToString("F6", Invariant));
    }

    private static void Append(
        StringBuilder sb,
        string name,
        double value)
    {
        Append(
            sb,
            name,
            value.ToString("F3", Invariant));
    }
}
