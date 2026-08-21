#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Removes the old startup-only 0.32 Inference threshold compensation.
/// After v3.2/v3.5 the model output is a correctly sigmoid-normalized face
/// probability, so the source default should match MediaPipe's 0.5 threshold.
/// </summary>
[InitializeOnLoad]
public static class KiwiInferenceThresholdParityInstaller
{
    private const string PathName =
        "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
        "KiwiTrackingQuality10Controller.cs";

    private const string OldValue =
        "public float inferencePresenceThreshold = 0.32f;";

    private const string NewValue =
        "public float inferencePresenceThreshold = 0.50f;";

    static KiwiInferenceThresholdParityInstaller()
    {
        EditorApplication.delayCall +=
            EnsureInstalled;
    }

    [MenuItem(
        "Tools/Kiwi Avatar/Install v3.5 Inference Threshold Parity")]
    public static void EnsureInstalled()
    {
        if (!File.Exists(PathName))
        {
            return;
        }

        string text =
            File.ReadAllText(
                PathName);

        if (
            text.IndexOf(
                NewValue,
                StringComparison.Ordinal) >=
            0
        )
        {
            return;
        }

        int index =
            text.IndexOf(
                OldValue,
                StringComparison.Ordinal);

        if (index < 0)
        {
            Debug.LogWarning(
                "[Kiwi v3.5] Quality10 Inference threshold source shape " +
                "has changed; no startup threshold edit was made.");

            return;
        }

        string updated =
            text.Substring(
                0,
                index) +
            NewValue +
            text.Substring(
                index +
                OldValue.Length);

        byte[] original =
            File.ReadAllBytes(
                PathName);

        bool hasBom =
            original.Length >=
                3 &&
            original[0] ==
                0xEF &&
            original[1] ==
                0xBB &&
            original[2] ==
                0xBF;

        File.WriteAllText(
            PathName,
            updated,
            new UTF8Encoding(
                hasBom));

        AssetDatabase.ImportAsset(
            PathName,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[Kiwi v3.5] Aligned startup Inference threshold to 0.50.");
    }
}
#endif
