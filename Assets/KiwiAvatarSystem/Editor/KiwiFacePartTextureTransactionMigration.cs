#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Phase 16.9 guarded migration for the completed FacePartCropper.
///
/// It does not rebuild Eye/Mouth scene objects. It only replaces the old live
/// camera texture writer with KiwiFacePartTextureTransaction, gates semantic
/// crop adoption on an available matched camera snapshot, and commits the same
/// per-part accept/reject decision to the texture transaction that Crop/Mask
/// already use.
/// </summary>
[InitializeOnLoad]
public static class KiwiFacePartTextureTransactionMigration
{
    // KIWI_V5_1_PHASE16_9_FACEPART_TEXTURE_TRANSACTION_MIGRATION
    private const string CropperPath =
        "Assets/Script/FacePartCropper.cs";

    private const string PrerequisiteCanonical =
        "KIWI_V5_1_PHASE5_CANONICAL_CROPPER_FRAME";

    private const string PrerequisiteTransaction =
        "KIWI_V4_9_PART_TRANSACTION_REPORT";

    private const string TextureWriterMarker =
        "KIWI_V5_1_PHASE16_9_MATCHED_FACEPART_TEXTURE_WRITER";

    private const string SemanticGateMarker =
        "KIWI_V5_1_PHASE16_9_TEXTURE_SEMANTIC_GATE";

    private const string CommitMarker =
        "KIWI_V5_1_PHASE16_9_TEXTURE_TRANSACTION_COMMIT";

    static KiwiFacePartTextureTransactionMigration()
    {
        EditorApplication.delayCall += ApplyIfReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 16.9 Face-Part Texture Transaction")]
    private static void ApplyFromMenu()
    {
        ApplyIfReady();
    }

    private static void ApplyIfReady()
    {
        if (!File.Exists(CropperPath))
        {
            return;
        }

        string original =
            File.ReadAllText(CropperPath);

        if (
            original.IndexOf(
                PrerequisiteCanonical,
                StringComparison.Ordinal) < 0 ||
            original.IndexOf(
                PrerequisiteTransaction,
                StringComparison.Ordinal) < 0
        )
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 16.9 FacePart transaction requires " +
                "the already-applied Phase 16.8 cumulative project. No blind " +
                "FacePartCropper rewrite was performed.");
            return;
        }

        string source = NormalizeNewlines(original);
        bool changed = false;
        bool blocked = false;

        if (!source.Contains(TextureWriterMarker))
        {
            const string oldWriter =
                "        leftEyeImage.texture =\n" +
                "            sourceImage.texture;\n\n" +
                "        rightEyeImage.texture =\n" +
                "            sourceImage.texture;\n\n" +
                "        mouthImage.texture =\n" +
                "            sourceImage.texture;";

            const string newWriter =
                "        // KIWI_V5_1_PHASE16_9_MATCHED_FACEPART_TEXTURE_WRITER\n" +
                "        // Do not advance Eye/Mouth pixels independently from\n" +
                "        // canonical Crop/Mask geometry. The transaction service\n" +
                "        // keeps each output on its last complete semantic frame.\n" +
                "        KiwiFacePartTextureTransaction.\n" +
                "            ApplyCurrentPresentationTextures(this);";

            if (CountOccurrences(source, oldWriter) == 1)
            {
                source = source.Replace(oldWriter, newWriter);
                changed = true;
            }
            else
            {
                blocked = true;
            }
        }

        if (!source.Contains(SemanticGateMarker))
        {
            const string freshnessBlock =
                "        bool semanticSampleFresh =\n" +
                "            !hasNewLandmarks ||\n" +
                "            KiwiCommercialFacePartPolicy.IsSemanticSampleAdoptable(\n" +
                "                runner,\n" +
                "                timestamp);";

            const string transactionalFreshness =
                "        // KIWI_V5_1_PHASE16_9_TEXTURE_SEMANTIC_GATE\n" +
                "        // A semantic crop may advance only when the camera frame\n" +
                "        // that produced it is still available in the GPU history.\n" +
                "        // On a miss, Texture + Crop + Mask all hold together.\n" +
                "        bool semanticTextureReady =\n" +
                "            !hasNewLandmarks ||\n" +
                "            KiwiFacePartTextureTransaction.\n" +
                "                PrepareSemanticTransaction(\n" +
                "                    this,\n" +
                "                    timestamp);\n\n" +
                "        bool semanticSampleFresh =\n" +
                "            !hasNewLandmarks ||\n" +
                "            (semanticTextureReady &&\n" +
                "             KiwiCommercialFacePartPolicy.IsSemanticSampleAdoptable(\n" +
                "                 runner,\n" +
                "                 timestamp));";

            if (CountOccurrences(source, freshnessBlock) == 1)
            {
                source = source.Replace(
                    freshnessBlock,
                    transactionalFreshness);
                changed = true;
            }
            else
            {
                blocked = true;
            }
        }

        if (!source.Contains(CommitMarker))
        {
            const string reportBlock =
                "        KiwiCommercialFacePartPolicy.ReportPartSampleDecision(\n" +
                "            timestamp,\n" +
                "            outputLeftEyeAccepted,\n" +
                "            outputRightEyeAccepted,\n" +
                "            mouthOK);";

            const string reportAndCommit =
                reportBlock +
                "\n\n" +
                "        // KIWI_V5_1_PHASE16_9_TEXTURE_TRANSACTION_COMMIT\n" +
                "        // Use the exact same output-space accept/reject decision\n" +
                "        // for pixels that ShapeMask uses for contour adoption.\n" +
                "        KiwiFacePartTextureTransaction.CommitPartDecision(\n" +
                "            this,\n" +
                "            timestamp,\n" +
                "            outputLeftEyeAccepted,\n" +
                "            outputRightEyeAccepted,\n" +
                "            mouthOK);";

            if (CountOccurrences(source, reportBlock) == 1)
            {
                source = source.Replace(
                    reportBlock,
                    reportAndCommit);
                changed = true;
            }
            else
            {
                blocked = true;
            }
        }

        if (blocked)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 16.9 could not uniquely locate one " +
                "or more validated FacePartCropper anchors. Unmatched code was " +
                "left unchanged; no blind rewrite was performed.");
        }

        if (!changed)
        {
            return;
        }

        WriteUtf8PreserveBom(
            CropperPath,
            source);

        AssetDatabase.ImportAsset(
            CropperPath,
            ImportAssetOptions.ForceUpdate);

        AssetDatabase.Refresh();

        Debug.Log(
            "[KiwiAvatarSystem] Phase 16.9 applied matched Eye/Mouth camera " +
            "texture transactions without rebuilding scene objects or adding " +
            "any Root feedback path.");
    }

    private static int CountOccurrences(
        string source,
        string value)
    {
        int count = 0;
        int index = 0;

        while (true)
        {
            index = source.IndexOf(
                value,
                index,
                StringComparison.Ordinal);

            if (index < 0)
            {
                return count;
            }

            count++;
            index += value.Length;
        }
    }

    private static string NormalizeNewlines(
        string value)
    {
        return value
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }

    private static void WriteUtf8PreserveBom(
        string path,
        string text)
    {
        bool hadBom = false;

        using (FileStream stream = File.OpenRead(path))
        {
            if (stream.Length >= 3)
            {
                int a = stream.ReadByte();
                int b = stream.ReadByte();
                int c = stream.ReadByte();
                hadBom = a == 0xEF && b == 0xBB && c == 0xBF;
            }
        }

        File.WriteAllText(
            path,
            text,
            new UTF8Encoding(hadBom));
    }
}
#endif
