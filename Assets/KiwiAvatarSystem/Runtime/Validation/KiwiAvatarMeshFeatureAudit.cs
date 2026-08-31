using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

[DefaultExecutionOrder(-31880)]
[DisallowMultipleComponent]
public sealed class KiwiAvatarMeshFeatureAudit : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Avatar Mesh Feature Audit";

    private const string EnableEnvironment =
        "KIWI_AVATAR_MESH_FEATURE_AUDIT";

    private const string Contract =
        "KIWI_V5_1_PHASE16_20_36_V44_5_AVATAR_MESH_FEATURE_AUDIT";

    private const double AuditDelaySeconds = 8.0;

    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private static KiwiAvatarMeshFeatureAudit _instance;

    private double _createdRealtime;
    private bool _completed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        if (!IsEnabled(EnableEnvironment))
        {
            return;
        }

        if (
            Application.platform != RuntimePlatform.WindowsPlayer ||
            !Debug.isDebugBuild)
        {
            Debug.LogWarning(
                "[KiwiAvatarMeshAudit] ignored: Development Windows Player required.");
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiAvatarMeshFeatureAudit>();
    }

    private static bool IsEnabled(string name)
    {
        string value =
            Environment.GetEnvironmentVariable(name);

        return
            string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);

        _createdRealtime =
            Time.realtimeSinceStartupAsDouble;

        Debug.Log(
            "[KiwiAvatarMeshAudit] contract=" +
            Contract +
            " requested=1 delaySeconds=" +
            AuditDelaySeconds.ToString("F1", Invariant));
    }

    private void Update()
    {
        if (_completed)
        {
            return;
        }

        if (
            Time.realtimeSinceStartupAsDouble -
            _createdRealtime <
            AuditDelaySeconds)
        {
            return;
        }

        try
        {
            string result =
                BuildAudit();

            WriteResult(result);

            Debug.Log(
                "[KiwiAvatarMeshAudit] COMPLETE");
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[KiwiAvatarMeshAudit] failed: " +
                ex);
        }
        finally
        {
            _completed = true;
        }
    }

    private static string BuildAudit()
    {
        StringBuilder sb =
            new StringBuilder(32768);

        sb.AppendLine(
            "KiwiAvatarSystem v44.5 Avatar Mesh Feature Audit");
        Append(sb, "contract", Contract);
        Append(sb, "unityVersion", Application.unityVersion);
        Append(sb, "graphicsApi", SystemInfo.graphicsDeviceType.ToString());
        Append(sb, "graphicsDevice", SystemInfo.graphicsDeviceName);
        Append(
            sb,
            "screen",
            UnityEngine.Screen.width +
            "x" +
            UnityEngine.Screen.height);

        KiwiAvatarRuntimeManager manager =
            FindFirstObjectByType<KiwiAvatarRuntimeManager>(
                FindObjectsInactive.Include);

        KiwiSurfaceFitter fitter =
            FindFirstObjectByType<KiwiSurfaceFitter>(
                FindObjectsInactive.Include);

        sb.AppendLine();
        sb.AppendLine("[RUNTIME_MANAGER]");

        if (manager == null)
        {
            Append(sb, "found", 0);
        }
        else
        {
            Append(sb, "found", 1);
            Append(
                sb,
                "isExternalAvatarActive",
                manager.IsExternalAvatarActive ? 1 : 0);
            Append(
                sb,
                "currentAvatarName",
                manager.CurrentAvatarName);
            Append(
                sb,
                "status",
                manager.Status);
            Append(
                sb,
                "activeFaceFitMethod",
                manager.ActiveFaceFitMethod);
            Append(
                sb,
                "activeFaceFitConfidence",
                manager.ActiveFaceFitConfidence);
            Append(
                sb,
                "effectiveModelSizeLimitMB",
                manager.EffectiveModelSizeLimitMB);
        }

        sb.AppendLine();
        sb.AppendLine("[SURFACE_FITTER]");

        if (fitter == null)
        {
            Append(sb, "found", 0);
        }
        else
        {
            Append(sb, "found", 1);
            Append(
                sb,
                "lastFitSucceeded",
                fitter.LastFitSucceeded ? 1 : 0);
            Append(
                sb,
                "lastSuccessRate",
                fitter.LastSuccessRate);
            Append(
                sb,
                "lastTotalVertices",
                fitter.LastTotalVertices);
            Append(
                sb,
                "lastTotalHits",
                fitter.LastTotalHits);
            Append(
                sb,
                "targetRenderer",
                fitter.targetRenderer != null
                    ? GetHierarchyPath(
                        fitter.targetRenderer.transform)
                    : string.Empty);
        }

        SkinnedMeshRenderer[] renderers =
            FindObjectsByType<SkinnedMeshRenderer>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        List<RendererAudit> audits =
            new List<RendererAudit>();

        for (int i = 0; i < renderers.Length; i++)
        {
            SkinnedMeshRenderer renderer =
                renderers[i];

            if (
                renderer == null ||
                renderer.sharedMesh == null)
            {
                continue;
            }

            audits.Add(
                AuditRenderer(
                    renderer));
        }

        audits.Sort(
            (a, b) =>
                b.triangleCount.CompareTo(
                    a.triangleCount));

        Append(
            sb,
            "skinnedRendererCount",
            audits.Count);

        for (int i = 0; i < audits.Count; i++)
        {
            RendererAudit audit =
                audits[i];

            sb.AppendLine();
            sb.AppendLine(
                "[SKINNED_RENDERER_" +
                i.ToString("D2", Invariant) +
                "]");

            Append(sb, "hierarchyPath", audit.hierarchyPath);
            Append(sb, "gameObjectActive", audit.gameObjectActive);
            Append(sb, "rendererEnabled", audit.rendererEnabled);
            Append(sb, "forceRenderingOff", audit.forceRenderingOff);
            Append(sb, "updateWhenOffscreen", audit.updateWhenOffscreen);
            Append(sb, "meshName", audit.meshName);
            Append(sb, "meshInstanceId", audit.meshInstanceId);
            Append(sb, "meshReadable", audit.meshReadable);
            Append(sb, "vertexCount", audit.vertexCount);
            Append(sb, "triangleCount", audit.triangleCount);
            Append(sb, "subMeshCount", audit.subMeshCount);
            Append(sb, "indexFormat", audit.indexFormat);
            Append(sb, "blendShapeCount", audit.blendShapeCount);
            Append(sb, "boneCount", audit.boneCount);
            Append(sb, "bindPoseCount", audit.bindPoseCount);
            Append(sb, "materialCount", audit.materialCount);
            Append(sb, "rootBone", audit.rootBone);
            Append(sb, "localBounds", audit.localBounds);
            Append(sb, "vertexAttributes", audit.vertexAttributes);
            Append(sb, "subMeshTriangles", audit.subMeshTriangles);
            Append(sb, "materialShaders", audit.materialShaders);
            Append(sb, "blendShapes", audit.blendShapes);
            Append(sb, "legacyBoneWeightReadable", audit.legacyBoneWeightReadable);
            Append(sb, "boneWeight0InfluenceVertices", audit.bw0);
            Append(sb, "boneWeight1InfluenceVertices", audit.bw1);
            Append(sb, "boneWeight2InfluenceVertices", audit.bw2);
            Append(sb, "boneWeight3InfluenceVertices", audit.bw3);
            Append(sb, "boneWeight4InfluenceVertices", audit.bw4);
            Append(sb, "boneWeightInvalidVertices", audit.bwInvalid);
        }

        sb.AppendLine();
        sb.AppendLine("[COMPONENT_BINDING_HINTS]");

        MonoBehaviour[] behaviours =
            FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

        SortedSet<string> bindingTypes =
            new SortedSet<string>(
                StringComparer.Ordinal);

        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour =
                behaviours[i];

            if (behaviour == null)
            {
                continue;
            }

            Type type =
                behaviour.GetType();

            string fullName =
                type.FullName ?? type.Name;

            if (
                fullName.IndexOf(
                    "BlendShape",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                fullName.IndexOf(
                    "Expression",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                fullName.IndexOf(
                    "VRM",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bindingTypes.Add(fullName);
            }
        }

        Append(
            sb,
            "types",
            string.Join(
                ",",
                bindingTypes));

        sb.AppendLine();
        sb.AppendLine("[SAFETY_INTERPRETATION]");
        sb.AppendLine(
            "meshReadable=1 is required by most managed runtime simplifiers.");
        sb.AppendLine(
            "blendShapeCount/order must remain identical because UniVRM 0.x runtime bindings address the same SkinnedMeshRenderer by relative path and blendshape index.");
        sb.AppendLine(
            "bone weights must remain valid; current UniVRM importer uses four joint/weight components per imported vertex.");
        sb.AppendLine(
            "The selected permanent optimization point is after Adaptive/Surface Fit and before transactional model-generation commit; this audit performs no writes.");

        return sb.ToString();
    }

    private struct RendererAudit
    {
        public string hierarchyPath;
        public int gameObjectActive;
        public int rendererEnabled;
        public int forceRenderingOff;
        public int updateWhenOffscreen;
        public string meshName;
        public int meshInstanceId;
        public int meshReadable;
        public int vertexCount;
        public long triangleCount;
        public int subMeshCount;
        public string indexFormat;
        public int blendShapeCount;
        public int boneCount;
        public int bindPoseCount;
        public int materialCount;
        public string rootBone;
        public string localBounds;
        public string vertexAttributes;
        public string subMeshTriangles;
        public string materialShaders;
        public string blendShapes;
        public int legacyBoneWeightReadable;
        public int bw0;
        public int bw1;
        public int bw2;
        public int bw3;
        public int bw4;
        public int bwInvalid;
    }

    private static RendererAudit AuditRenderer(
        SkinnedMeshRenderer renderer)
    {
        Mesh mesh =
            renderer.sharedMesh;

        RendererAudit result =
            new RendererAudit
            {
                hierarchyPath =
                    GetHierarchyPath(
                        renderer.transform),
                gameObjectActive =
                    renderer.gameObject.activeInHierarchy ? 1 : 0,
                rendererEnabled =
                    renderer.enabled ? 1 : 0,
                forceRenderingOff =
                    renderer.forceRenderingOff ? 1 : 0,
                updateWhenOffscreen =
                    renderer.updateWhenOffscreen ? 1 : 0,
                meshName =
                    mesh.name ?? string.Empty,
                meshInstanceId =
                    mesh.GetInstanceID(),
                meshReadable =
                    mesh.isReadable ? 1 : 0,
                vertexCount =
                    mesh.vertexCount,
                triangleCount =
                    CountTriangles(mesh),
                subMeshCount =
                    mesh.subMeshCount,
                indexFormat =
                    mesh.indexFormat.ToString(),
                blendShapeCount =
                    mesh.blendShapeCount,
                boneCount =
                    renderer.bones != null
                        ? renderer.bones.Length
                        : 0,
                bindPoseCount =
                    SafeBindPoseCount(mesh),
                materialCount =
                    renderer.sharedMaterials != null
                        ? renderer.sharedMaterials.Length
                        : 0,
                rootBone =
                    renderer.rootBone != null
                        ? GetHierarchyPath(
                            renderer.rootBone)
                        : string.Empty,
                localBounds =
                    FormatBounds(
                        renderer.localBounds),
                vertexAttributes =
                    DescribeVertexAttributes(mesh),
                subMeshTriangles =
                    DescribeSubMeshes(mesh),
                materialShaders =
                    DescribeMaterials(renderer),
                blendShapes =
                    DescribeBlendShapes(mesh)
            };

        AuditLegacyBoneWeights(
            mesh,
            ref result);

        return result;
    }

    private static long CountTriangles(Mesh mesh)
    {
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

    private static int SafeBindPoseCount(Mesh mesh)
    {
        try
        {
            Matrix4x4[] bindposes =
                mesh.bindposes;

            return
                bindposes != null
                    ? bindposes.Length
                    : 0;
        }
        catch
        {
            return -1;
        }
    }

    private static string DescribeVertexAttributes(
        Mesh mesh)
    {
        try
        {
            VertexAttributeDescriptor[] attributes =
                mesh.GetVertexAttributes();

            if (
                attributes == null ||
                attributes.Length == 0)
            {
                return string.Empty;
            }

            return string.Join(
                ";",
                attributes.Select(
                    x =>
                        x.attribute +
                        ":" +
                        x.format +
                        "x" +
                        x.dimension +
                        "@stream" +
                        x.stream));
        }
        catch (Exception ex)
        {
            return
                "ERROR:" +
                ex.GetType().Name;
        }
    }

    private static string DescribeSubMeshes(
        Mesh mesh)
    {
        List<string> values =
            new List<string>();

        try
        {
            for (
                int i = 0;
                i < mesh.subMeshCount;
                i++)
            {
                MeshTopology topology =
                    mesh.GetTopology(i);

                ulong indices =
                    mesh.GetIndexCount(i);

                long triangles =
                    topology == MeshTopology.Triangles
                        ? (long)(indices / 3UL)
                        : 0L;

                values.Add(
                    i +
                    ":" +
                    topology +
                    ":" +
                    triangles);
            }
        }
        catch (Exception ex)
        {
            values.Add(
                "ERROR:" +
                ex.GetType().Name);
        }

        return string.Join(
            ";",
            values);
    }

    private static string DescribeMaterials(
        SkinnedMeshRenderer renderer)
    {
        Material[] materials =
            renderer.sharedMaterials;

        if (
            materials == null ||
            materials.Length == 0)
        {
            return string.Empty;
        }

        List<string> values =
            new List<string>();

        for (int i = 0; i < materials.Length; i++)
        {
            Material material =
                materials[i];

            if (material == null)
            {
                values.Add(
                    i +
                    ":<null>");
                continue;
            }

            values.Add(
                i +
                ":" +
                material.name +
                "/" +
                (
                    material.shader != null
                        ? material.shader.name
                        : "<no-shader>"
                ));
        }

        return string.Join(
            ";",
            values);
    }

    private static string DescribeBlendShapes(
        Mesh mesh)
    {
        try
        {
            int count =
                mesh.blendShapeCount;

            if (count <= 0)
            {
                return string.Empty;
            }

            int limit =
                Math.Min(
                    count,
                    128);

            List<string> values =
                new List<string>(
                    limit);

            for (int i = 0; i < limit; i++)
            {
                values.Add(
                    i +
                    ":" +
                    mesh.GetBlendShapeName(i) +
                    ":frames=" +
                    mesh.GetBlendShapeFrameCount(i));
            }

            if (count > limit)
            {
                values.Add(
                    "...+" +
                    (count - limit));
            }

            return string.Join(
                ";",
                values);
        }
        catch (Exception ex)
        {
            return
                "ERROR:" +
                ex.GetType().Name;
        }
    }

    private static void AuditLegacyBoneWeights(
        Mesh mesh,
        ref RendererAudit result)
    {
        try
        {
            BoneWeight[] weights =
                mesh.boneWeights;

            if (weights == null)
            {
                result.legacyBoneWeightReadable = 1;
                return;
            }

            result.legacyBoneWeightReadable = 1;

            for (int i = 0; i < weights.Length; i++)
            {
                BoneWeight w =
                    weights[i];

                int influences = 0;

                if (w.weight0 > 0.000001f)
                {
                    influences++;
                }

                if (w.weight1 > 0.000001f)
                {
                    influences++;
                }

                if (w.weight2 > 0.000001f)
                {
                    influences++;
                }

                if (w.weight3 > 0.000001f)
                {
                    influences++;
                }

                float sum =
                    w.weight0 +
                    w.weight1 +
                    w.weight2 +
                    w.weight3;

                if (
                    float.IsNaN(sum) ||
                    float.IsInfinity(sum) ||
                    sum < -0.0001f ||
                    sum > 1.01f)
                {
                    result.bwInvalid++;
                }

                switch (influences)
                {
                    case 0:
                        result.bw0++;
                        break;
                    case 1:
                        result.bw1++;
                        break;
                    case 2:
                        result.bw2++;
                        break;
                    case 3:
                        result.bw3++;
                        break;
                    case 4:
                        result.bw4++;
                        break;
                }
            }
        }
        catch
        {
            result.legacyBoneWeightReadable = 0;
        }
    }

    private static string GetHierarchyPath(
        Transform transform)
    {
        if (transform == null)
        {
            return string.Empty;
        }

        Stack<string> parts =
            new Stack<string>();

        Transform current =
            transform;

        while (current != null)
        {
            parts.Push(
                current.name);
            current =
                current.parent;
        }

        return string.Join(
            "/",
            parts);
    }

    private static string FormatBounds(
        Bounds bounds)
    {
        Vector3 c =
            bounds.center;

        Vector3 s =
            bounds.size;

        return
            "center(" +
            c.x.ToString("F6", Invariant) +
            "," +
            c.y.ToString("F6", Invariant) +
            "," +
            c.z.ToString("F6", Invariant) +
            ") size(" +
            s.x.ToString("F6", Invariant) +
            "," +
            s.y.ToString("F6", Invariant) +
            "," +
            s.z.ToString("F6", Invariant) +
            ")";
    }

    private static void WriteResult(
        string result)
    {
        string directory =
            Path.Combine(
                Application.persistentDataPath,
                "KiwiAvatarMeshFeatureAudit");

        Directory.CreateDirectory(
            directory);

        string path =
            Path.Combine(
                directory,
                "KiwiAvatarMeshFeatureAudit_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss",
                    Invariant) +
                ".txt");

        File.WriteAllText(
            path,
            result,
            new UTF8Encoding(false));

        Debug.Log(
            "[KiwiAvatarMeshAudit] resultPath=" +
            path);
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

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }
}
