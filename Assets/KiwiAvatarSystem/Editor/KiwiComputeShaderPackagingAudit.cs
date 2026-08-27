#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// KiwiAvatarSystem Phase16.20.27 v39 DX12 Compute Shader Packaging Audit.
///
/// Editor-only / observer-only.
/// Does not modify shader compiler data and does not strip or preserve variants.
///
/// Purpose:
/// - inventory ComputeShader assets visible to AssetDatabase before build;
/// - verify whether required kernels exist in Editor;
/// - observe every compute-shader kernel Unity presents for Player compilation;
/// - record variant counts presented to the build pipeline;
/// - write a post-build report without changing runtime code.
///
/// Required kernels currently diagnosed:
/// - TextureToTensorExact (Unity Inference Engine/Sentis TextureConverter)
/// - Match                (Kiwi FacePart matcher)
/// </summary>
internal sealed class KiwiComputeShaderPackagingAudit :
    IPreprocessBuildWithReport,
    IPreprocessComputeShaders,
    IPostprocessBuildWithReport
{
    internal const string ContractMarker =
        "KIWI_V5_1_PHASE16_20_27_V39_DX12_COMPUTE_SHADER_PACKAGING_AUDIT";

    private const string InferenceKernel =
        "TextureToTensorExact";

    private const string FacePartKernel =
        "Match";

    private static readonly object Sync =
        new object();

    [Serializable]
    private sealed class AssetRecord
    {
        public string path;
        public string name;
        public bool hasTextureToTensorExact;
        public bool hasMatch;
    }

    [Serializable]
    private sealed class CompileRecord
    {
        public string path;
        public string shaderName;
        public string kernelName;
        public int variantCount;
        public bool requiredKernel;
    }

    [Serializable]
    private sealed class AuditReport
    {
        public string contract;
        public string unityVersion;
        public string buildTarget;
        public bool developmentBuild;
        public bool windowsAutoGraphicsApi;
        public string[] windowsGraphicsApis;
        public bool dx12OnlyConfigured;
        public int computeShaderAssetCount;
        public int compileCallbackCount;
        public int requiredKernelCompileCallbackCount;
        public bool editorHasTextureToTensorExact;
        public bool editorHasMatch;
        public bool buildSawTextureToTensorExact;
        public bool buildSawMatch;
        public int textureToTensorExactVariants;
        public int matchVariants;
        public string[] textureToTensorExactAssetPaths;
        public string[] matchAssetPaths;
        public AssetRecord[] assets;
        public CompileRecord[] compileRecords;
    }

    private static readonly List<AssetRecord> Assets =
        new List<AssetRecord>();

    private static readonly List<CompileRecord> Compiles =
        new List<CompileRecord>();

    private static bool _initialized;

    public int callbackOrder =>
        -20000;

    public void OnPreprocessBuild(
        BuildReport report)
    {
        lock (Sync)
        {
            ResetAndInventory(
                report);
        }
    }

    public void OnProcessComputeShader(
        ComputeShader shader,
        string kernelName,
        IList<ShaderCompilerData> data)
    {
        lock (Sync)
        {
            if (!_initialized)
            {
                // Defensive fallback. BuildPipeline normally calls the
                // preprocess-build callback first.
                ResetAndInventory(
                    null);
            }

            string path =
                shader != null
                    ? AssetDatabase.GetAssetPath(shader)
                    : string.Empty;

            int variantCount =
                data != null
                    ? data.Count
                    : 0;

            bool required =
                string.Equals(
                    kernelName,
                    InferenceKernel,
                    StringComparison.Ordinal) ||
                string.Equals(
                    kernelName,
                    FacePartKernel,
                    StringComparison.Ordinal);

            Compiles.Add(
                new CompileRecord
                {
                    path = path ?? string.Empty,
                    shaderName =
                        shader != null
                            ? shader.name
                            : string.Empty,
                    kernelName =
                        kernelName ?? string.Empty,
                    variantCount =
                        variantCount,
                    requiredKernel =
                        required
                });

            if (required)
            {
                Debug.Log(
                    "[KiwiV39ShaderAudit] required kernel compile callback " +
                    "kernel=" +
                    kernelName +
                    " variants=" +
                    variantCount +
                    " path=" +
                    path);
            }

            // OBSERVER ONLY:
            // never remove, add or mutate ShaderCompilerData.
        }
    }

    public void OnPostprocessBuild(
        BuildReport report)
    {
        lock (Sync)
        {
            WriteReport(
                report);
        }
    }

    private static void ResetAndInventory(
        BuildReport report)
    {
        Assets.Clear();
        Compiles.Clear();
        _initialized = true;

        string[] guids =
            AssetDatabase.FindAssets(
                "t:ComputeShader");

        foreach (
            string guid
            in guids
                .OrderBy(
                    x => x,
                    StringComparer.Ordinal))
        {
            string path =
                AssetDatabase.GUIDToAssetPath(
                    guid);

            ComputeShader shader =
                AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    path);

            if (shader == null)
            {
                continue;
            }

            bool hasInference =
                SafeHasKernel(
                    shader,
                    InferenceKernel);

            bool hasMatch =
                SafeHasKernel(
                    shader,
                    FacePartKernel);

            Assets.Add(
                new AssetRecord
                {
                    path =
                        path ?? string.Empty,
                    name =
                        shader.name ?? string.Empty,
                    hasTextureToTensorExact =
                        hasInference,
                    hasMatch =
                        hasMatch
                });

            if (
                hasInference ||
                hasMatch)
            {
                Debug.Log(
                    "[KiwiV39ShaderAudit] Editor asset " +
                    "path=" +
                    path +
                    " TextureToTensorExact=" +
                    (hasInference ? "1" : "0") +
                    " Match=" +
                    (hasMatch ? "1" : "0"));
            }
        }

        Debug.Log(
            "[KiwiV39ShaderAudit] inventory complete assets=" +
            Assets.Count +
            " target=" +
            (
                report != null
                    ? report.summary.platform.ToString()
                    : EditorUserBuildSettings.activeBuildTarget.ToString()
            ));
    }

    private static bool SafeHasKernel(
        ComputeShader shader,
        string kernelName)
    {
        if (
            shader == null ||
            string.IsNullOrEmpty(kernelName))
        {
            return false;
        }

        try
        {
            return shader.HasKernel(
                kernelName);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteReport(
        BuildReport report)
    {
        string outputPath =
            report != null
                ? report.summary.outputPath
                : string.Empty;

        string outputDirectory =
            string.IsNullOrWhiteSpace(
                outputPath)
                ? Directory.GetParent(
                    Application.dataPath)?.FullName
                    ?? Application.dataPath
                : Path.GetDirectoryName(
                    outputPath);

        if (
            string.IsNullOrWhiteSpace(
                outputDirectory))
        {
            outputDirectory =
                Directory.GetParent(
                    Application.dataPath)?.FullName
                ?? Application.dataPath;
        }

        Directory.CreateDirectory(
            outputDirectory);

        AssetRecord[] assets =
            Assets
                .OrderBy(
                    x => x.path,
                    StringComparer.Ordinal)
                .ToArray();

        CompileRecord[] compiles =
            Compiles
                .OrderBy(
                    x => x.path,
                    StringComparer.Ordinal)
                .ThenBy(
                    x => x.kernelName,
                    StringComparer.Ordinal)
                .ToArray();

        string[] inferencePaths =
            assets
                .Where(
                    x => x.hasTextureToTensorExact)
                .Select(
                    x => x.path)
                .Distinct(
                    StringComparer.Ordinal)
                .ToArray();

        string[] matchPaths =
            assets
                .Where(
                    x => x.hasMatch)
                .Select(
                    x => x.path)
                .Distinct(
                    StringComparer.Ordinal)
                .ToArray();

        CompileRecord[] inferenceCompiles =
            compiles
                .Where(
                    x =>
                        string.Equals(
                            x.kernelName,
                            InferenceKernel,
                            StringComparison.Ordinal))
                .ToArray();

        CompileRecord[] matchCompiles =
            compiles
                .Where(
                    x =>
                        string.Equals(
                            x.kernelName,
                            FacePartKernel,
                            StringComparison.Ordinal))
                .ToArray();

        AuditReport audit =
            new AuditReport
            {
                contract =
                    ContractMarker,
                unityVersion =
                    Application.unityVersion,
                buildTarget =
                    report != null
                        ? report.summary.platform.ToString()
                        : EditorUserBuildSettings.activeBuildTarget.ToString(),
                developmentBuild =
                    report != null &&
                    (report.summary.options & BuildOptions.Development) != 0,
                windowsAutoGraphicsApi =
                    PlayerSettings.GetUseDefaultGraphicsAPIs(
                        BuildTarget.StandaloneWindows64),
                windowsGraphicsApis =
                    PlayerSettings.GetGraphicsAPIs(
                        BuildTarget.StandaloneWindows64)
                        .Select(api => api.ToString())
                        .ToArray(),
                dx12OnlyConfigured =
                    !PlayerSettings.GetUseDefaultGraphicsAPIs(
                        BuildTarget.StandaloneWindows64) &&
                    PlayerSettings.GetGraphicsAPIs(
                        BuildTarget.StandaloneWindows64).Length == 1 &&
                    PlayerSettings.GetGraphicsAPIs(
                        BuildTarget.StandaloneWindows64)[0] ==
                        GraphicsDeviceType.Direct3D12,
                computeShaderAssetCount =
                    assets.Length,
                compileCallbackCount =
                    compiles.Length,
                requiredKernelCompileCallbackCount =
                    inferenceCompiles.Length +
                    matchCompiles.Length,
                editorHasTextureToTensorExact =
                    inferencePaths.Length > 0,
                editorHasMatch =
                    matchPaths.Length > 0,
                buildSawTextureToTensorExact =
                    inferenceCompiles.Length > 0,
                buildSawMatch =
                    matchCompiles.Length > 0,
                textureToTensorExactVariants =
                    inferenceCompiles.Sum(
                        x => x.variantCount),
                matchVariants =
                    matchCompiles.Sum(
                        x => x.variantCount),
                textureToTensorExactAssetPaths =
                    inferencePaths,
                matchAssetPaths =
                    matchPaths,
                assets =
                    assets,
                compileRecords =
                    compiles
            };

        string json =
            JsonUtility.ToJson(
                audit,
                true);

        string reportPath =
            Path.Combine(
                outputDirectory,
                "KiwiComputeShaderPackagingAudit_v39.json");

        File.WriteAllText(
            reportPath,
            json);

        string summaryPath =
            Path.Combine(
                outputDirectory,
                "KiwiComputeShaderPackagingAudit_v39.txt");

        File.WriteAllText(
            summaryPath,
            BuildSummaryText(
                audit));

        Debug.Log(
            "[KiwiV39ShaderAudit] report=" +
            reportPath +
            " editorInference=" +
            (audit.editorHasTextureToTensorExact ? "1" : "0") +
            " buildInference=" +
            (audit.buildSawTextureToTensorExact ? "1" : "0") +
            " inferenceVariants=" +
            audit.textureToTensorExactVariants +
            " editorMatch=" +
            (audit.editorHasMatch ? "1" : "0") +
            " buildMatch=" +
            (audit.buildSawMatch ? "1" : "0") +
            " matchVariants=" +
            audit.matchVariants);
    }

    private static string BuildSummaryText(
        AuditReport audit)
    {
        return
            "KiwiAvatarSystem v39 DX12 Compute Shader Packaging Audit" +
            Environment.NewLine +
            "contract=" +
            audit.contract +
            Environment.NewLine +
            "unityVersion=" +
            audit.unityVersion +
            Environment.NewLine +
            "buildTarget=" +
            audit.buildTarget +
            Environment.NewLine +
            "developmentBuild=" +
            (audit.developmentBuild ? "1" : "0") +
            Environment.NewLine +
            "windowsAutoGraphicsApi=" +
            (audit.windowsAutoGraphicsApi ? "1" : "0") +
            Environment.NewLine +
            "windowsGraphicsApis=" +
            string.Join(",", audit.windowsGraphicsApis ?? Array.Empty<string>()) +
            Environment.NewLine +
            "dx12OnlyConfigured=" +
            (audit.dx12OnlyConfigured ? "1" : "0") +
            Environment.NewLine +
            "computeShaderAssetCount=" +
            audit.computeShaderAssetCount +
            Environment.NewLine +
            "compileCallbackCount=" +
            audit.compileCallbackCount +
            Environment.NewLine +
            "editorHasTextureToTensorExact=" +
            (audit.editorHasTextureToTensorExact ? "1" : "0") +
            Environment.NewLine +
            "buildSawTextureToTensorExact=" +
            (audit.buildSawTextureToTensorExact ? "1" : "0") +
            Environment.NewLine +
            "textureToTensorExactVariants=" +
            audit.textureToTensorExactVariants +
            Environment.NewLine +
            "editorHasMatch=" +
            (audit.editorHasMatch ? "1" : "0") +
            Environment.NewLine +
            "buildSawMatch=" +
            (audit.buildSawMatch ? "1" : "0") +
            Environment.NewLine +
            "matchVariants=" +
            audit.matchVariants +
            Environment.NewLine +
            "textureToTensorExactAssetPaths=" +
            string.Join(
                ";",
                audit.textureToTensorExactAssetPaths ?? Array.Empty<string>()) +
            Environment.NewLine +
            "matchAssetPaths=" +
            string.Join(
                ";",
                audit.matchAssetPaths ?? Array.Empty<string>()) +
            Environment.NewLine;
    }
}
#endif
