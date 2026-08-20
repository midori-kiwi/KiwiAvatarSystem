#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds a short ownership hysteresis to the hybrid backend.
///
/// FaceRig/Animaze-style high-intensity response and VSeeFace-style robust
/// tracking both rely on not letting one marginal observation destabilize the
/// visible avatar. Once Inference Engine is primary, two isolated publish
/// rejects are tolerated. A real tracker-loss branch remains immediate.
/// </summary>
[InitializeOnLoad]
public static class KiwiInferenceBackendHysteresisInstaller
{
    private const string RunnerPath =
        "Assets/Script/FaceLandmarkerRunner.cs";

    private const string AsyncMarker =
        "KIWI_ASYNC_INFERENCE_MAILBOX_V2_3";

    private const string Marker =
        "KIWI_INFERENCE_BACKEND_HYSTERESIS_V2_7";

    static KiwiInferenceBackendHysteresisInstaller()
    {
        EditorApplication.delayCall +=
            EnsureInstalled;
    }

    [MenuItem(
        "Tools/Kiwi Avatar/Install Backend Hysteresis")]
    public static void EnsureInstalled()
    {
        if (!File.Exists(RunnerPath))
        {
            return;
        }

        string text =
            File.ReadAllText(
                RunnerPath);

        if (text.Contains(Marker))
        {
            return;
        }

        // The backend patch must run after the timestamp-preserving async
        // migration. If this is the first import, let that installer run first.
        if (!text.Contains(AsyncMarker))
        {
            EditorApplication.delayCall +=
                EnsureInstalled;

            return;
        }

        const string fieldNeedle =
            "private long _lastSentisAnchorTimestampApplied = -1L;";

        if (!text.Contains(fieldNeedle))
        {
            Debug.LogError(
                "[Kiwi Backend Hysteresis] Expected Runner field was not found.");

            return;
        }

        int fieldIndex =
            text.IndexOf(
                fieldNeedle,
                System.StringComparison.Ordinal);

        if (fieldIndex < 0)
        {
            Debug.LogError(
                "[Kiwi Backend Hysteresis] Expected Runner field was not found.");

            return;
        }

        int duplicateFieldIndex =
            text.IndexOf(
                fieldNeedle,
                fieldIndex +
                fieldNeedle.Length,
                System.StringComparison.Ordinal);

        if (duplicateFieldIndex >= 0)
        {
            Debug.LogError(
                "[Kiwi Backend Hysteresis] Expected exactly one Runner field, " +
                "but multiple matches were found. Runner was left unchanged.");

            return;
        }

        string fieldReplacement =
            fieldNeedle +
            "\n\n        // " + Marker +
            "\n        private int _sentisPublishFailureStreak;" +
            "\n        private const int SentisPublishFailureGraceFrames = 3;";

        text =
            text.Substring(
                0,
                fieldIndex) +
            fieldReplacement +
            text.Substring(
                fieldIndex +
                fieldNeedle.Length);

        const string assignment =
            "_sentisPrimaryActive = StoreSentisTrackingData(";

        int callStart =
            text.IndexOf(
                assignment,
                System.StringComparison.Ordinal);

        if (callStart < 0)
        {
            Debug.LogError(
                "[Kiwi Backend Hysteresis] Inference publish assignment " +
                "was not found. Runner was left unchanged.");

            return;
        }

        int callEnd =
            text.IndexOf(
                ");",
                callStart,
                System.StringComparison.Ordinal);

        if (callEnd < 0)
        {
            Debug.LogError(
                "[Kiwi Backend Hysteresis] Could not locate publish call end.");

            return;
        }

        string call =
            text.Substring(
                callStart,
                callEnd - callStart + 2);

        string convertedCall =
            call.Replace(
                assignment,
                "bool sentisAcceptedForPublish = StoreSentisTrackingData(");

        string hysteresis =
@"

                if (sentisAcceptedForPublish)
                {
                    _sentisPublishFailureStreak = 0;
                    _sentisPrimaryActive = true;
                }
                else if (_sentisPrimaryActive)
                {
                    _sentisPublishFailureStreak++;

                    if (
                        _sentisPublishFailureStreak >=
                        SentisPublishFailureGraceFrames)
                    {
                        _sentisPrimaryActive = false;
                        _hasSentisRotationOffset = false;
                        _sentisPublishFailureStreak = 0;
                    }
                }";

        text =
            text.Substring(
                0,
                callStart) +
            convertedCall +
            hysteresis +
            text.Substring(
                callEnd + 2);

        WriteUtf8PreserveBom(
            RunnerPath,
            text);

        AssetDatabase.ImportAsset(
            RunnerPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[Kiwi Backend Hysteresis] Installed v2.7 ownership hysteresis.");
    }

    private static void WriteUtf8PreserveBom(
        string path,
        string text)
    {
        byte[] bytes =
            File.ReadAllBytes(
                path);

        bool hasBom =
            bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF;

        File.WriteAllText(
            path,
            text,
            new UTF8Encoding(
                hasBom));
    }
}
#endif
