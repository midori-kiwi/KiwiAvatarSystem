#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Editor-only static gate for the v42.3 automated validation Player.
/// Plugin settings are queried exclusively through PluginImporter APIs.
/// </summary>
public static class KiwiAutomatedValidationEditorEntryPoints
{
    private const string Contract =
        "KIWI_V5_1_PHASE16_20_32_V42_3_AUTOMATED_VALIDATION_STATIC_V1";
    private const string ResultEnvironment =
        "KIWI_VALIDATION_STATIC_RESULT";
    private const string ManifestRelativePath =
        "Phase16_20_32_v42_3_NATIVE_PLUGIN_DEVICE_LIFECYCLE_ROOTFIX.json";
    private const string ModelAssetPath =
        "Assets/KiwiAvatarSystem/Resources/KiwiFaceLandmarkInference.onnx";
    private const string SceneAssetPath =
        "Assets/Scenes/Face Landmark Detection.unity";
    private const string ExpectedModelSha256 =
        "ed487104519b0a88cb2cb2ec3678e183f447fb9dd63560998e960ffbaa8fb335";

    [Serializable]
    private sealed class InstalledManifest
    {
        public string version;
        public string runtimeSha256;
        public string bridgeSha256;
        public string bridgePdbSha256;
    }

    [Serializable]
    private sealed class StaticReceipt
    {
        public string contract;
        public string status;
        public string error;
        public string completedUtc;
        public string unityVersion;
        public string graphicsApis;
        public string installedVersion;
        public string manifestSha256;
        public string modelSha256;
        public string runtimeSha256;
        public string bridgeSha256;
        public string bridgePdbSha256;
        public string pluginImporterState;
        public string buildContract;
    }

    public static void ValidateStaticBatch()
    {
        string resultPath =
            Environment.GetEnvironmentVariable(ResultEnvironment);
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            throw new InvalidOperationException(
                ResultEnvironment + " is not set.");
        }

        StaticReceipt receipt = new StaticReceipt
        {
            contract = Contract,
            status = "FAIL",
            error = string.Empty,
            completedUtc = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            buildContract = KiwiStandaloneBuildDiagnostic.ContractMarker
        };

        try
        {
            Require(
                Application.unityVersion == "6000.0.80f1",
                "Unity version must be 6000.0.80f1.");

            GraphicsDeviceType[] graphicsApis =
                PlayerSettings.GetGraphicsAPIs(
                    BuildTarget.StandaloneWindows64);
            bool automatic =
                PlayerSettings.GetUseDefaultGraphicsAPIs(
                    BuildTarget.StandaloneWindows64);
            receipt.graphicsApis =
                string.Join(",", graphicsApis.Select(x => x.ToString()));
            Require(
                !automatic &&
                graphicsApis.Length == 1 &&
                graphicsApis[0] == GraphicsDeviceType.Direct3D12,
                "Windows Player graphics API must be exact DX12 only.");

            KiwiV42PluginImportPolicy.PolicyState importer =
                KiwiV42PluginImportPolicy.ReadState();
            receipt.pluginImporterState =
                KiwiV42PluginImportPolicy.Describe(importer);
            Require(
                importer.IsValid,
                "Native bridge PluginImporter policy is invalid: " +
                receipt.pluginImporterState);

            string projectRoot =
                Path.GetFullPath(
                    Path.Combine(Application.dataPath, ".."));
            string manifestPath =
                Path.Combine(projectRoot, ManifestRelativePath);
            string modelPath =
                Path.Combine(projectRoot, ModelAssetPath);
            string runtimePath =
                Path.Combine(
                    projectRoot,
                    "Assets/KiwiAvatarSystem/Runtime/Optimization/KiwiOrtDmlZeroCopyRuntime.cs");
            string bridgePath =
                Path.Combine(
                    projectRoot,
                    "Assets/Plugins/KiwiOrtDirectML/x86_64/KiwiOrtDmlZeroCopyBridge.dll");
            string bridgePdbPath =
                Path.ChangeExtension(bridgePath, ".pdb");
            string scenePath = Path.Combine(projectRoot, SceneAssetPath);

            Require(File.Exists(manifestPath), "Installed manifest is missing.");
            Require(File.Exists(modelPath), "Inference model is missing.");
            Require(File.Exists(runtimePath), "onnxruntime.dll is missing.");
            Require(File.Exists(bridgePath), "Native bridge DLL is missing.");
            Require(File.Exists(bridgePdbPath), "Native bridge PDB is missing.");
            Require(File.Exists(scenePath), "Validation scene is missing.");

            InstalledManifest manifest =
                JsonUtility.FromJson<InstalledManifest>(
                    File.ReadAllText(manifestPath));
            Require(manifest != null, "Installed manifest could not be parsed.");
            receipt.installedVersion = manifest.version;
            Require(
                manifest.version == "5.1.0-phase16.20.32-v42.3",
                "Installed manifest version mismatch.");

            receipt.manifestSha256 = ComputeSha256(manifestPath);
            receipt.modelSha256 = ComputeSha256(modelPath);
            receipt.runtimeSha256 = ComputeSha256(runtimePath);
            receipt.bridgeSha256 = ComputeSha256(bridgePath);
            receipt.bridgePdbSha256 = ComputeSha256(bridgePdbPath);
            Require(
                receipt.modelSha256 == ExpectedModelSha256,
                "Inference model SHA-256 mismatch.");
            Require(
                receipt.runtimeSha256 == manifest.runtimeSha256,
                "onnxruntime.dll SHA-256 mismatch.");
            Require(
                receipt.bridgeSha256 == manifest.bridgeSha256,
                "Native bridge SHA-256 mismatch.");
            Require(
                receipt.bridgePdbSha256 == manifest.bridgePdbSha256,
                "Native bridge PDB SHA-256 mismatch.");

            Require(
                AssetDatabase.LoadAssetAtPath<SceneAsset>(SceneAssetPath) != null,
                "Validation scene is not importable as a SceneAsset.");
            Require(
                AssetDatabase.LoadMainAssetAtPath(ModelAssetPath) != null,
                "Inference model is not imported.");

            receipt.status = "PASS";
            receipt.completedUtc = DateTime.UtcNow.ToString("O");
            WriteReceipt(resultPath, receipt);
            Debug.Log(
                "[KiwiValidationStatic] PASS contract=" + Contract +
                " importer=" + receipt.pluginImporterState);
        }
        catch (Exception ex)
        {
            receipt.status = "FAIL";
            receipt.error = ex.ToString();
            receipt.completedUtc = DateTime.UtcNow.ToString("O");
            WriteReceipt(resultPath, receipt);
            throw new BuildFailedException(
                "Kiwi v42.3 automated validation static gate failed: " +
                ex.Message);
        }
    }

    private static void WriteReceipt(
        string path,
        StaticReceipt receipt)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException(
                "Static receipt path has no directory: " + fullPath);
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(
            fullPath,
            JsonUtility.ToJson(receipt, true),
            new UTF8Encoding(false));
    }

    private static string ComputeSha256(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(stream);
            StringBuilder result = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                result.Append(hash[i].ToString("x2"));
            }
            return result.ToString();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
#endif
