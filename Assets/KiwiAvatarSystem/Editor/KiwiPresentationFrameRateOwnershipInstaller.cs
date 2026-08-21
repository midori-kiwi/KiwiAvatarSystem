#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v3.7 one-shot ownership cleanup.
///
/// FacePartCropper used to write Application.targetFrameRate from its Start()
/// method even though KiwiTrackingQuality10Controller already owns presentation
/// cadence. Camera/tracker cadence and render cadence are separate concerns in
/// mature VTuber applications, so the cropper is now a consumer of render time,
/// not a global frame-rate owner.
/// </summary>
[InitializeOnLoad]
public static class KiwiPresentationFrameRateOwnershipInstaller
{
    private const string CropperPath =
        "Assets/Script/FacePartCropper.cs";

    private const string Marker =
        "KIWI_PRESENTATION_FPS_OWNER_V3_7";

    private const string StartSignature =
        "    private void Start()";

    private const string NextMethodSignature =
        "    private void LateUpdate()";

    static KiwiPresentationFrameRateOwnershipInstaller()
    {
        EditorApplication.delayCall += EnsureInstalled;
    }

    [MenuItem(
        "Tools/Kiwi Avatar/Install v3.7 Presentation FPS Ownership")]
    public static void EnsureInstalled()
    {
        if (!File.Exists(CropperPath))
        {
            return;
        }

        string text =
            File.ReadAllText(CropperPath);

        if (
            text.IndexOf(
                Marker,
                StringComparison.Ordinal) >= 0
        )
        {
            return;
        }

        int start =
            text.IndexOf(
                StartSignature,
                StringComparison.Ordinal);

        int nextMethod =
            start >= 0
                ? text.IndexOf(
                    NextMethodSignature,
                    start +
                    StartSignature.Length,
                    StringComparison.Ordinal)
                : -1;

        if (
            start < 0 ||
            nextMethod < 0
        )
        {
            Debug.LogWarning(
                "[Kiwi v3.7] FacePartCropper Start() boundary was not found; " +
                "no frame-rate ownership edit was made.");

            return;
        }

        string oldStart =
            text.Substring(
                start,
                nextMethod -
                start);

        if (
            oldStart.IndexOf(
                "Application.targetFrameRate",
                StringComparison.Ordinal) < 0 ||
            oldStart.IndexOf(
                "targetRenderFrameRate",
                StringComparison.Ordinal) < 0
        )
        {
            Debug.LogWarning(
                "[Kiwi v3.7] FacePartCropper Start() no longer owns the global " +
                "frame rate; no edit was needed.");

            return;
        }

        string replacement =
@"    // " + Marker + @"
    // Global presentation cadence is owned by
    // KiwiTrackingQuality10Controller. FacePartCropper follows render time.
    private void Start()
    {
    }


    // =========================================================
    // Update
    // =========================================================

";

        string updated =
            text.Substring(0, start) +
            replacement +
            text.Substring(
                nextMethod);

        byte[] original =
            File.ReadAllBytes(CropperPath);

        bool hasBom =
            original.Length >= 3 &&
            original[0] == 0xEF &&
            original[1] == 0xBB &&
            original[2] == 0xBF;

        File.WriteAllText(
            CropperPath,
            updated,
            new UTF8Encoding(hasBom));

        AssetDatabase.ImportAsset(
            CropperPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[Kiwi v3.7] Presentation FPS ownership centralized.");
    }
}
#endif
