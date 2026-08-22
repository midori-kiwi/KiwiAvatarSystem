#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 5 guarded source migration.
///
/// FacePartCropper and FacePartShapeMask are completed project scripts and are
/// not replaced wholesale. This migration changes only their snapshot read
/// boundary so both consume the display-cycle canonical semantic frame.
/// </summary>
[InitializeOnLoad]
public static class KiwiCanonicalSameFrameMigration
{
    private const string CropperPath =
        "Assets/Script/FacePartCropper.cs";

    private const string ShapeMaskPath =
        "Assets/Script/FacePartShapeMask.cs";

    private const string CropperPrerequisite =
        "KIWI_V4_9_PART_TRANSACTION_REPORT";

    private const string ShapeMaskPrerequisite =
        "KIWI_V5_1_PHASE4_SHAPEMASK_CANVAS_RELEASE";

    private const string CropperMarker =
        "KIWI_V5_1_PHASE5_CANONICAL_CROPPER_FRAME";

    private const string ShapeMaskMarker =
        "KIWI_V5_1_PHASE5_CANONICAL_MASK_FRAME";

    private static int _retryCount;

    static KiwiCanonicalSameFrameMigration()
    {
        EditorApplication.delayCall +=
            ApplyWhenPrerequisitesReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 5 Canonical Same-Frame")]
    private static void ApplyFromMenu()
    {
        _retryCount = 0;
        ApplyWhenPrerequisitesReady();
    }

    private static void ApplyWhenPrerequisitesReady()
    {
        if (
            !File.Exists(CropperPath) ||
            !File.Exists(ShapeMaskPath)
        )
        {
            return;
        }

        string cropper =
            File.ReadAllText(CropperPath);

        string shapeMask =
            File.ReadAllText(ShapeMaskPath);

        bool prerequisitesReady =
            cropper.IndexOf(
                CropperPrerequisite,
                StringComparison.Ordinal) >= 0 &&
            shapeMask.IndexOf(
                ShapeMaskPrerequisite,
                StringComparison.Ordinal) >= 0;

        if (!prerequisitesReady)
        {
            _retryCount++;

            if (_retryCount <= 10)
            {
                EditorApplication.delayCall +=
                    ApplyWhenPrerequisitesReady;
            }
            else
            {
                Debug.LogWarning(
                    "[KiwiAvatarSystem] Phase 5 waited for the v4.9 semantic " +
                    "transaction and Phase 4 ShapeMask ownership migration, " +
                    "but prerequisites were not available. No blind source " +
                    "rewrite was performed.");
            }

            return;
        }

        bool cropperChanged =
            PatchCropper(cropper);

        bool shapeMaskChanged =
            PatchShapeMask(shapeMask);

        if (cropperChanged || shapeMaskChanged)
        {
            AssetDatabase.Refresh();

            Debug.Log(
                "[KiwiAvatarSystem] v5.1 Phase 5 moved Root / Cropper / " +
                "ShapeMask onto one canonical display-cycle tracking frame.");
        }
    }

    private static bool PatchCropper(
        string original)
    {
        if (
            original.IndexOf(
                CropperMarker,
                StringComparison.Ordinal) >= 0
        )
        {
            return false;
        }

        string source =
            NormalizeNewlines(original);

        const string call =
            "runner.TryGetLatestLandmarksIfChanged(\n" +
            "                ref _landmarkBuffer,";

        const string matchedAgeCall =
            "runner.TryGetLatestPrecisionTrackingData(\n" +
            "                out FacePrecisionTrackingData precision";

        if (
            CountOccurrences(source, call) != 1 ||
            CountOccurrences(source, matchedAgeCall) != 1)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 5 could not uniquely locate the " +
                "validated FacePartCropper landmark read. Source was left " +
                "unchanged.");
            return false;
        }

        source =
            source.Replace(
                call,
                "KiwiCanonicalTrackingFrame.TryGetSemanticLandmarksIfChanged(\n" +
                "                runner,\n" +
                "                ref _landmarkBuffer,");

        source =
            source.Replace(
                matchedAgeCall,
                "KiwiCommercialRigidMotionPolicy.TryGetAuthoritativeFrame(\n" +
                "                runner,\n" +
                "                out FacePrecisionTrackingData precision");

        const string markerAnchor =
            "        bool hasNewLandmarks =\n";

        if (CountOccurrences(source, markerAnchor) != 1)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 5 cropper marker anchor changed. " +
                "Source was left unchanged.");
            return false;
        }

        source =
            source.Replace(
                markerAnchor,
                "        // KIWI_V5_1_PHASE5_CANONICAL_CROPPER_FRAME\n" +
                "        // Crop geometry is copied from the same immutable display-cycle\n" +
                "        // snapshot that KiwiFaceMotion consumes for Root.\n" +
                markerAnchor);

        WriteUtf8PreserveBom(
            CropperPath,
            source);

        AssetDatabase.ImportAsset(
            CropperPath,
            ImportAssetOptions.ForceUpdate);

        return true;
    }

    private static bool PatchShapeMask(
        string original)
    {
        if (
            original.IndexOf(
                ShapeMaskMarker,
                StringComparison.Ordinal) >= 0
        )
        {
            return false;
        }

        string source =
            NormalizeNewlines(original);

        const string landmarkCall =
            "runner.TryGetLatestLandmarksIfChanged(\n" +
            "                ref _landmarks,";

        const string expressionCall =
            "runner.TryGetLatestExpressionData(\n" +
            "                out expression,";

        if (
            CountOccurrences(source, landmarkCall) != 1 ||
            CountOccurrences(source, expressionCall) != 1
        )
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 5 could not uniquely locate the " +
                "validated FacePartShapeMask landmark/expression reads. Source " +
                "was left unchanged.");
            return false;
        }

        source =
            source.Replace(
                landmarkCall,
                "KiwiCanonicalTrackingFrame.TryGetSemanticLandmarksIfChanged(\n" +
                "                runner,\n" +
                "                ref _landmarks,");

        source =
            source.Replace(
                expressionCall,
                "KiwiCanonicalTrackingFrame.TryGetExpressionData(\n" +
                "                runner,\n" +
                "                out expression,");

        const string markerAnchor =
            "        bool valid =\n" +
            "            KiwiCanonicalTrackingFrame.TryGetSemanticLandmarksIfChanged(\n";

        if (CountOccurrences(source, markerAnchor) != 1)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 5 ShapeMask marker anchor changed. " +
                "Source was left unchanged.");
            return false;
        }

        int markerIndex =
            source.IndexOf(
                markerAnchor,
                StringComparison.Ordinal);

        source =
            source.Substring(0, markerIndex) +
            "        // KIWI_V5_1_PHASE5_CANONICAL_MASK_FRAME\n" +
            "        // Mask contour and expression are latched from the same display\n" +
            "        // cycle as Cropper/Root; callbacks after the latch wait one cycle.\n" +
            source.Substring(markerIndex);

        WriteUtf8PreserveBom(
            ShapeMaskPath,
            source);

        AssetDatabase.ImportAsset(
            ShapeMaskPath,
            ImportAssetOptions.ForceUpdate);

        return true;
    }

    private static string NormalizeNewlines(
        string value)
    {
        return value
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }

    private static int CountOccurrences(
        string source,
        string value)
    {
        int count = 0;
        int index = 0;

        while (true)
        {
            index =
                source.IndexOf(
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

    private static void WriteUtf8PreserveBom(
        string path,
        string text)
    {
        bool hadBom = false;

        using (
            FileStream stream =
                File.OpenRead(path)
        )
        {
            if (stream.Length >= 3)
            {
                hadBom =
                    stream.ReadByte() == 0xEF &&
                    stream.ReadByte() == 0xBB &&
                    stream.ReadByte() == 0xBF;
            }
        }

        File.WriteAllText(
            path,
            text,
            new UTF8Encoding(hadBom));
    }
}
#endif
