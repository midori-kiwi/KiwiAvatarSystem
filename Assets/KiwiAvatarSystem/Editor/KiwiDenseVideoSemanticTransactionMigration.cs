#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v4.9 migration driven by every-encoded-frame analysis of the v4.8 test.
///
/// The recording exposed a single-eye source-crop jump while global 2D quality
/// and mask readiness remained healthy. Cropper already had a conservative
/// mouth outlier guard, but no equivalent one-eye topology guard. It also had
/// no per-part transaction telling ShapeMask that one crop was held.
///
/// This migration adds only a semantic presentation transaction:
/// - catastrophic isolated one-eye crop rejection;
/// - output-side accept/reject reporting for the current semantic timestamp;
/// - matching ShapeMask hold for a rejected part.
///
/// It never writes the avatar root and adds no temporal smoothing stage.
/// </summary>
[InitializeOnLoad]
public static class KiwiDenseVideoSemanticTransactionMigration
{
    private const string CropperPath =
        "Assets/Script/FacePartCropper.cs";

    private const string ShapeMaskPath =
        "Assets/Script/FacePartShapeMask.cs";

    private const string V48CropperPrerequisite =
        "KIWI_V4_8_SEMANTIC_FRESHNESS_GATE";

    private const string V48MaskPrerequisite =
        "KIWI_V4_8_MASK_SEMANTIC_FRESHNESS_GATE";

    private const string EyeGuardMarker =
        "KIWI_V4_9_ISOLATED_EYE_CROP_GUARD";

    private const string TransactionReportMarker =
        "KIWI_V4_9_PART_TRANSACTION_REPORT";

    private const string MaskTransactionMarker =
        "KIWI_V4_9_PART_TRANSACTION_GATE";

    private static int _retryCount;

    static KiwiDenseVideoSemanticTransactionMigration()
    {
        EditorApplication.delayCall +=
            ApplyWhenPrerequisitesReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v4.9 Dense Video Semantic Transaction")]
    private static void ApplyFromMenu()
    {
        _retryCount = 0;
        ApplyWhenPrerequisitesReady();
    }

    private static void ApplyWhenPrerequisitesReady()
    {
        if (!PrerequisitesReady())
        {
            _retryCount++;

            if (_retryCount <= 8)
            {
                EditorApplication.delayCall +=
                    ApplyWhenPrerequisitesReady;
            }
            else
            {
                Debug.LogWarning(
                    "[KiwiAvatarSystem] v4.9 semantic transaction waited for " +
                    "the v4.8 migration markers but they were not available. " +
                    "No blind rewrite was performed. Use the Tools/Kiwi Avatar " +
                    "System menu after compilation if needed.");
            }

            return;
        }

        bool cropperChanged =
            PatchCropper();

        bool maskChanged =
            PatchShapeMask();

        if (cropperChanged || maskChanged)
        {
            AssetDatabase.Refresh();

            Debug.Log(
                "[KiwiAvatarSystem] v4.9 applied isolated-eye crop protection " +
                "and atomic per-part crop/mask semantic transactions.");
        }
    }

    private static bool PrerequisitesReady()
    {
        if (
            !File.Exists(CropperPath) ||
            !File.Exists(ShapeMaskPath)
        )
        {
            return false;
        }

        string cropper =
            File.ReadAllText(CropperPath);

        string mask =
            File.ReadAllText(ShapeMaskPath);

        return
            cropper.Contains(V48CropperPrerequisite) &&
            mask.Contains(V48MaskPrerequisite);
    }

    private static bool PatchCropper()
    {
        string original =
            File.ReadAllText(CropperPath);

        string source =
            original.Replace("\r\n", "\n");

        bool changed = false;
        bool blocked = false;

        if (!source.Contains(EyeGuardMarker))
        {
            const string anchor =
                "        _coherentVerticalApplied = false;\n";

            const string insertion =
                "        // KIWI_V4_9_ISOLATED_EYE_CROP_GUARD\n" +
                "        // The dense v4.8 recording contained a one-eye source\n" +
                "        // crop jump while the companion eye and mouth remained\n" +
                "        // coherent. Reject only that catastrophic isolated eye;\n" +
                "        // shared translation/yaw/roll remains untouched.\n" +
                "        if (\n" +
                "            leftOK &&\n" +
                "            rightOK &&\n" +
                "            mouthOK &&\n" +
                "            _leftEyeState.initialized &&\n" +
                "            _rightEyeState.initialized &&\n" +
                "            _mouthState.initialized\n" +
                "        )\n" +
                "        {\n" +
                "            Rect previousLandmarkLeft =\n" +
                "                swapEyes\n" +
                "                    ? _rightEyeState.sampleRect\n" +
                "                    : _leftEyeState.sampleRect;\n\n" +
                "            Rect previousLandmarkRight =\n" +
                "                swapEyes\n" +
                "                    ? _leftEyeState.sampleRect\n" +
                "                    : _rightEyeState.sampleRect;\n\n" +
                "            KiwiCommercialFacePartPolicy.\n" +
                "                ResolveIsolatedEyeCropOutliers(\n" +
                "                    previousLandmarkLeft,\n" +
                "                    previousLandmarkRight,\n" +
                "                    _mouthState.sampleRect,\n" +
                "                    leftRect,\n" +
                "                    rightRect,\n" +
                "                    mouthRect,\n" +
                "                    ref leftOK,\n" +
                "                    ref rightOK);\n" +
                "        }\n\n" +
                anchor;

            if (source.Contains(anchor))
            {
                source =
                    ReplaceFirst(
                        source,
                        anchor,
                        insertion);

                changed = true;
            }
            else
            {
                blocked = true;
            }
        }

        if (!source.Contains(TransactionReportMarker))
        {
            const string anchor =
                "        if (swapEyes)\n";

            const string insertion =
                "        // KIWI_V4_9_PART_TRANSACTION_REPORT\n" +
                "        // Report decisions in output-image space. ShapeMask\n" +
                "        // runs later and must hold exactly the part whose crop\n" +
                "        // was held for this semantic timestamp.\n" +
                "        bool outputLeftEyeAccepted =\n" +
                "            swapEyes\n" +
                "                ? rightOK\n" +
                "                : leftOK;\n\n" +
                "        bool outputRightEyeAccepted =\n" +
                "            swapEyes\n" +
                "                ? leftOK\n" +
                "                : rightOK;\n\n" +
                "        KiwiCommercialFacePartPolicy.ReportPartSampleDecision(\n" +
                "            timestamp,\n" +
                "            outputLeftEyeAccepted,\n" +
                "            outputRightEyeAccepted,\n" +
                "            mouthOK);\n\n" +
                anchor;

            if (source.Contains(anchor))
            {
                source =
                    ReplaceFirst(
                        source,
                        anchor,
                        insertion);

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
                "[KiwiAvatarSystem] v4.9 could not locate one or more " +
                "FacePartCropper anchors. Unmatched code was left unchanged.");
        }

        if (!changed)
        {
            return false;
        }

        WritePreservingFormat(
            CropperPath,
            original,
            source);

        AssetDatabase.ImportAsset(
            CropperPath,
            ImportAssetOptions.ForceUpdate);

        return true;
    }

    private static bool PatchShapeMask()
    {
        string original =
            File.ReadAllText(ShapeMaskPath);

        string source =
            original.Replace("\r\n", "\n");

        if (source.Contains(MaskTransactionMarker))
        {
            return false;
        }

        const string anchor =
            "        FaceExpressionData expression =\n" +
            "            default;\n";

        const string insertion =
            "        // KIWI_V4_9_PART_TRANSACTION_GATE\n" +
            "        // Crop and tight mask form one semantic transaction. If\n" +
            "        // Cropper rejected only this part, consume the timestamp\n" +
            "        // while retaining the previous complete contour.\n" +
            "        bool hasSemanticPartMapping =\n" +
            "            false;\n\n" +
            "        KiwiCommercialFacePartPolicy.SemanticPart semanticPart =\n" +
            "            KiwiCommercialFacePartPolicy.SemanticPart.Mouth;\n\n" +
            "        if (resolvedPart == FacePartType.Mouth)\n" +
            "        {\n" +
            "            semanticPart =\n" +
            "                KiwiCommercialFacePartPolicy.SemanticPart.Mouth;\n" +
            "            hasSemanticPartMapping = true;\n" +
            "        }\n" +
            "        else if (cropper != null && _image == cropper.leftEyeImage)\n" +
            "        {\n" +
            "            semanticPart =\n" +
            "                KiwiCommercialFacePartPolicy.SemanticPart.LeftEye;\n" +
            "            hasSemanticPartMapping = true;\n" +
            "        }\n" +
            "        else if (cropper != null && _image == cropper.rightEyeImage)\n" +
            "        {\n" +
            "            semanticPart =\n" +
            "                KiwiCommercialFacePartPolicy.SemanticPart.RightEye;\n" +
            "            hasSemanticPartMapping = true;\n" +
            "        }\n\n" +
            "        if (\n" +
            "            hasSemanticPartMapping &&\n" +
            "            !KiwiCommercialFacePartPolicy.IsPartSampleAdoptable(\n" +
            "                timestamp,\n" +
            "                semanticPart)\n" +
            "        )\n" +
            "        {\n" +
            "            _lastTimestamp = timestamp;\n" +
            "            RenderFrameState(resolvedPart);\n" +
            "            return;\n" +
            "        }\n\n" +
            anchor;

        if (!source.Contains(anchor))
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] v4.9 could not locate the " +
                "FacePartShapeMask transaction anchor. No blind rewrite " +
                "was performed.");

            return false;
        }

        source =
            ReplaceFirst(
                source,
                anchor,
                insertion);

        WritePreservingFormat(
            ShapeMaskPath,
            original,
            source);

        AssetDatabase.ImportAsset(
            ShapeMaskPath,
            ImportAssetOptions.ForceUpdate);

        return true;
    }


    private static string ReplaceFirst(
        string source,
        string oldValue,
        string newValue)
    {
        int index =
            source.IndexOf(
                oldValue,
                System.StringComparison.Ordinal);

        if (index < 0)
        {
            return source;
        }

        return
            source.Substring(0, index) +
            newValue +
            source.Substring(
                index + oldValue.Length);
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
