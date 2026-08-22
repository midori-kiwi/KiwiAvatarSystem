#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 4 migration for current-main face-part presentation ownership.
///
/// - Quality Coordinator keeps depth/surface/yaw/swap calculation and
///   hysteresis, but submits its filtered value as a quality cap.
/// - FacePartShapeMask keeps semantic/blink material opacity, but stops forcing
///   CanvasRenderer alpha to 1.
/// - The final CanvasRenderer writer is KiwiFacePartPresentationResolver.
/// </summary>
[InitializeOnLoad]
public static class KiwiFacePartPresentationResolverMigration
{
    private const string CoordinatorPath =
        "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
        "KiwiFacePartQualityCoordinator.cs";

    private const string ShapeMaskPath =
        "Assets/Script/FacePartShapeMask.cs";

    private const string V33CoordinatorPrerequisite =
        "KIWI_FACE_PART_VISIBILITY_LATCH_FIX_V3_3";

    private const string V49ShapeMaskPrerequisite =
        "KIWI_V4_9_PART_TRANSACTION_GATE";

    private const string CoordinatorMarker =
        "KIWI_V5_1_PHASE4_PRESENTATION_ARBITRATION";

    private const string ShapeMaskMarker =
        "KIWI_V5_1_PHASE4_SHAPEMASK_CANVAS_RELEASE";

    private const string MethodStart =
        "    // KIWI_FACE_PART_VISIBILITY_LATCH_FIX_V3_3";

    private const string NextMethodStart =
        "    private float FilterVisibility(";

    private static int _retryCount;

    static KiwiFacePartPresentationResolverMigration()
    {
        EditorApplication.delayCall +=
            ApplyWhenPrerequisitesReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 4 Presentation Arbitration")]
    private static void ApplyFromMenu()
    {
        _retryCount = 0;
        ApplyWhenPrerequisitesReady();
    }

    private static void ApplyWhenPrerequisitesReady()
    {
        if (
            !File.Exists(CoordinatorPath) ||
            !File.Exists(ShapeMaskPath)
        )
        {
            return;
        }

        string coordinator =
            File.ReadAllText(CoordinatorPath);

        string shapeMask =
            File.ReadAllText(ShapeMaskPath);

        bool prerequisitesReady =
            coordinator.IndexOf(
                V33CoordinatorPrerequisite,
                StringComparison.Ordinal) >= 0 &&
            shapeMask.IndexOf(
                V49ShapeMaskPrerequisite,
                StringComparison.Ordinal) >= 0;

        if (!prerequisitesReady)
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
                    "[KiwiAvatarSystem] Phase 4 waited for its visibility/" +
                    "semantic prerequisites but they were not available. " +
                    "No blind source rewrite was performed.");
            }

            return;
        }

        bool coordinatorChanged =
            PatchCoordinator(coordinator);

        bool shapeMaskChanged =
            PatchShapeMask(shapeMask);

        if (coordinatorChanged || shapeMaskChanged)
        {
            AssetDatabase.Refresh();

            Debug.Log(
                "[KiwiAvatarSystem] v5.1 Phase 4 moved face-part CanvasRenderer " +
                "presentation ownership to the reason-coded resolver.");
        }
    }

    private static bool PatchCoordinator(
        string text)
    {
        if (
            text.IndexOf(
                CoordinatorMarker,
                StringComparison.Ordinal) >= 0
        )
        {
            return false;
        }

        int methodStart =
            text.IndexOf(
                MethodStart,
                StringComparison.Ordinal);

        int nextMethodStart =
            methodStart >= 0
                ? text.IndexOf(
                    NextMethodStart,
                    methodStart +
                        MethodStart.Length,
                    StringComparison.Ordinal)
                : -1;

        if (
            methodStart < 0 ||
            nextMethodStart < 0
        )
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 4 could not locate the known " +
                "FacePartQualityCoordinator visibility method. No blind " +
                "rewrite was performed.");
            return false;
        }

        string currentMethod =
            text.Substring(
                methodStart,
                nextMethodStart -
                    methodStart);

        if (
            currentMethod.IndexOf(
                "canvasRenderer.SetAlpha",
                StringComparison.Ordinal) < 0 ||
            currentMethod.IndexOf(
                "guardVisibility",
                StringComparison.Ordinal) < 0
        )
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 4 coordinator method no longer " +
                "matches the validated v3.3 CanvasRenderer owner. Source was " +
                "left unchanged.");
            return false;
        }

        string replacement =
@"    // KIWI_FACE_PART_VISIBILITY_LATCH_FIX_V3_3
    // KIWI_V5_1_PHASE4_PRESENTATION_ARBITRATION
    // Geometry/depth/yaw/swap still resolve here, but CanvasRenderer alpha has
    // one final writer: KiwiFacePartPresentationResolver.
    private static void ApplyPartVisibility(
        RawImage image,
        float guardVisibility)
    {
        KiwiFacePartPresentationResolver.SubmitQualityCap(
            image,
            guardVisibility);
    }

";

        string updated =
            text.Substring(
                0,
                methodStart) +
            replacement +
            text.Substring(
                nextMethodStart);

        WriteUtf8PreserveBom(
            CoordinatorPath,
            updated);

        AssetDatabase.ImportAsset(
            CoordinatorPath,
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
            original.Replace(
                "\r\n",
                "\n");

        int beforeCount =
            CountOccurrences(
                source,
                "canvasRenderer.SetAlpha");

        if (beforeCount < 3)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 4 expected the validated ShapeMask " +
                "CanvasRenderer reset sites but found only " +
                beforeCount +
                ". Source was left unchanged.");
            return false;
        }

        const string replacement =
            "        // KIWI_V5_1_PHASE4_SHAPEMASK_CANVAS_RELEASE\n" +
            "        // Semantic/blink opacity remains material-owned. Final\n" +
            "        // CanvasRenderer alpha is owned by PresentationResolver.\n";

        source =
            source.Replace(
                "        _image.canvasRenderer.SetAlpha(1f);\n",
                replacement);

        source =
            source.Replace(
                "        _image.canvasRenderer.SetAlpha(\n" +
                "            1f\n" +
                "        );\n",
                replacement);

        source =
            source.Replace(
                "            _image.canvasRenderer.SetAlpha(\n" +
                "                1f\n" +
                "            );\n",
                "            // KIWI_V5_1_PHASE4_SHAPEMASK_CANVAS_RELEASE\n" +
                "            // PresentationResolver owns CanvasRenderer alpha.\n");

        int afterCount =
            CountOccurrences(
                source,
                "canvasRenderer.SetAlpha");

        if (afterCount != 0)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 4 could not safely account for all " +
                "ShapeMask CanvasRenderer writes (before=" +
                beforeCount +
                ", after=" +
                afterCount +
                "). Source was left unchanged.");
            return false;
        }

        WritePreservingFormat(
            ShapeMaskPath,
            original,
            source);

        AssetDatabase.ImportAsset(
            ShapeMaskPath,
            ImportAssetOptions.ForceUpdate);

        return true;
    }

    private static int CountOccurrences(
        string source,
        string value)
    {
        int count = 0;
        int start = 0;

        while (true)
        {
            int index =
                source.IndexOf(
                    value,
                    start,
                    StringComparison.Ordinal);

            if (index < 0)
            {
                return count;
            }

            count++;
            start =
                index +
                value.Length;
        }
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

        if (original.Contains("\r\n"))
        {
            normalized =
                normalized.Replace(
                    "\n",
                    "\r\n");
        }

        File.WriteAllText(
            path,
            normalized,
            new UTF8Encoding(hasBom));
    }

    private static void WriteUtf8PreserveBom(
        string path,
        string text)
    {
        byte[] original =
            File.ReadAllBytes(path);

        bool hasBom =
            original.Length >= 3 &&
            original[0] == 0xEF &&
            original[1] == 0xBB &&
            original[2] == 0xBF;

        File.WriteAllText(
            path,
            text,
            new UTF8Encoding(hasBom));
    }
}
#endif
