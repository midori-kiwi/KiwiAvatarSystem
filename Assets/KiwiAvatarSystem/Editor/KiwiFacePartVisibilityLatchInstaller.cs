#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v3.3 root fix for face parts that remain invisible after a side-view hide.
///
/// Pre-v3.3 KiwiFacePartQualityCoordinator writes:
///   _PoseVisibility = Min(existing, guardVisibility)
///
/// Once existing reaches zero, it can never recover because Min(0, 1) is zero.
/// The coordinator should own only the CanvasRenderer side-view gate.
/// FacePartShapeMask owns semantic/blink material visibility.
/// </summary>
[InitializeOnLoad]
public static class KiwiFacePartVisibilityLatchInstaller
{
    private const string CoordinatorPath =
        "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
        "KiwiFacePartQualityCoordinator.cs";

    private const string Marker =
        "KIWI_FACE_PART_VISIBILITY_LATCH_FIX_V3_3";

    private const string MethodStart =
        "    private static void ApplyPartVisibility(";

    private const string NextMethodStart =
        "    private float FilterVisibility(";

    static KiwiFacePartVisibilityLatchInstaller()
    {
        EditorApplication.delayCall +=
            EnsureInstalled;
    }

    [MenuItem(
        "Tools/Kiwi Avatar/Install v3.3 Visibility Latch Fix")]
    public static void EnsureInstalled()
    {
        if (!File.Exists(CoordinatorPath))
        {
            Fail(
                "Coordinator source was not found.");

            return;
        }

        string text =
            File.ReadAllText(
                CoordinatorPath);

        if (
            text.IndexOf(
                Marker,
                StringComparison.Ordinal) >= 0
        )
        {
            return;
        }

        int methodStart =
            text.IndexOf(
                MethodStart,
                StringComparison.Ordinal);

        if (methodStart < 0)
        {
            Fail(
                "ApplyPartVisibility was not found.");

            return;
        }

        int nextMethodStart =
            text.IndexOf(
                NextMethodStart,
                methodStart +
                MethodStart.Length,
                StringComparison.Ordinal);

        if (nextMethodStart < 0)
        {
            Fail(
                "ApplyPartVisibility end boundary was not found.");

            return;
        }

        string currentMethod =
            text.Substring(
                methodStart,
                nextMethodStart -
                methodStart);

        if (
            currentMethod.IndexOf(
                "Mathf.Min(",
                StringComparison.Ordinal) < 0 ||
            currentMethod.IndexOf(
                "PoseVisibilityId",
                StringComparison.Ordinal) < 0 ||
            currentMethod.IndexOf(
                "canvasRenderer.SetAlpha",
                StringComparison.Ordinal) < 0
        )
        {
            Fail(
                "Coordinator no longer matches the known pre-v3.3 method.");

            return;
        }

        string replacement =
@"    // " + Marker + @"
    // Side-view visibility belongs to CanvasRenderer alpha only.
    // Semantic/blink material visibility remains owned by FacePartShapeMask.
    private static void ApplyPartVisibility(
        RawImage image,
        float guardVisibility)
    {
        if (image == null)
        {
            return;
        }

        image.canvasRenderer.SetAlpha(
            Mathf.Clamp01(
                guardVisibility));
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

        Debug.Log(
            "[Kiwi v3.3] Removed irreversible face-part visibility latch.");
    }

    private static void WriteUtf8PreserveBom(
        string path,
        string text)
    {
        byte[] original =
            File.ReadAllBytes(
                path);

        bool hasBom =
            original.Length >= 3 &&
            original[0] == 0xEF &&
            original[1] == 0xBB &&
            original[2] == 0xBF;

        File.WriteAllText(
            path,
            text,
            new UTF8Encoding(
                hasBom));
    }

    private static void Fail(
        string message)
    {
        Debug.LogError(
            "[Kiwi v3.3] " +
            message +
            " Coordinator was left unchanged.");
    }
}
#endif
