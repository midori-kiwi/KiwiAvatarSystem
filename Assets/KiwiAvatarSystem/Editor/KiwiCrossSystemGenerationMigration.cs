#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 generation-safety migration for the runtime model switcher.
///
/// ModelGeneration advances only after a model transaction has committed, or
/// when an actually-active external avatar is replaced by the embedded model.
/// Failed candidate loads/rollbacks never advance the model identity.
/// </summary>
[InitializeOnLoad]
public static class KiwiCrossSystemGenerationMigration
{
    private const string RuntimeManagerPath =
        "Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimeManager.cs";

    private const string Marker =
        "KIWI_V5_1_MODEL_GENERATION_COMMIT";

    static KiwiCrossSystemGenerationMigration()
    {
        EditorApplication.delayCall += Apply;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Cross-System Generation Safety")]
    private static void ApplyFromMenu()
    {
        Apply();
    }

    private static void Apply()
    {
        if (!File.Exists(RuntimeManagerPath))
        {
            return;
        }

        string original =
            File.ReadAllText(RuntimeManagerPath);

        string source =
            original.Replace("\r\n", "\n");

        if (source.Contains(Marker))
        {
            return;
        }

        const string commitAnchor =
            "            swapCommitted = true;\n" +
            "            candidate = null;\n";

        const string commitReplacement =
            "            swapCommitted = true;\n\n" +
            "            // KIWI_V5_1_MODEL_GENERATION_COMMIT\n" +
            "            // Advance only after the transactional hot-swap has\n" +
            "            // committed. Pending async face-part work from the\n" +
            "            // previous model will then fail generation identity.\n" +
            "            KiwiRuntimeGenerationContext.AdvanceModelGeneration();\n\n" +
            "            candidate = null;\n";

        const string fallbackStartAnchor =
            "    private void SwitchToFallbackInternal(bool clearLastAvatar)\n" +
            "    {\n" +
            "        if (!fallbackReferenceCaptured)\n";

        const string fallbackStartReplacement =
            "    private void SwitchToFallbackInternal(bool clearLastAvatar)\n" +
            "    {\n" +
            "        bool modelIdentityChanged =\n" +
            "            _activeModel != null ||\n" +
            "            _activeInstance != null;\n\n" +
            "        if (!fallbackReferenceCaptured)\n";

        const string fallbackCommitAnchor =
            "        currentAvatarName = \"Kiwi (Embedded)\";\n" +
            "        status = \"Ready\";\n";

        const string fallbackCommitReplacement =
            "        currentAvatarName = \"Kiwi (Embedded)\";\n\n" +
            "        if (modelIdentityChanged)\n" +
            "        {\n" +
            "            KiwiRuntimeGenerationContext.AdvanceModelGeneration();\n" +
            "        }\n\n" +
            "        status = \"Ready\";\n";

        if (
            !source.Contains(commitAnchor) ||
            !source.Contains(fallbackStartAnchor) ||
            !source.Contains(fallbackCommitAnchor)
        )
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] v5.1 model-generation migration could " +
                "not locate the expected transactional switch anchors. No " +
                "blind rewrite was performed.");
            return;
        }

        source =
            source.Replace(
                commitAnchor,
                commitReplacement);

        source =
            source.Replace(
                fallbackStartAnchor,
                fallbackStartReplacement);

        source =
            source.Replace(
                fallbackCommitAnchor,
                fallbackCommitReplacement);

        WritePreservingFormat(
            RuntimeManagerPath,
            original,
            source);

        AssetDatabase.ImportAsset(
            RuntimeManagerPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] v5.1 model generation safety applied.");
    }

    private static void WritePreservingFormat(
        string path,
        string original,
        string normalized)
    {
        byte[] bytes =
            File.ReadAllBytes(path);

        bool hasBom =
            bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF;

        string lineEnding =
            original.Contains("\r\n")
                ? "\r\n"
                : "\n";

        if (lineEnding == "\r\n")
        {
            normalized =
                normalized.Replace("\n", "\r\n");
        }

        File.WriteAllText(
            path,
            normalized,
            new UTF8Encoding(hasBom));
    }
}
#endif
