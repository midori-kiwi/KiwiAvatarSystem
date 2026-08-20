#if UNITY_EDITOR
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Narrow idempotent migration for StoreSentisTrackingData.
///
/// The 21:47 recording proves the Inference tracker eventually returns a valid
/// presence (p≈0.45), but the backend remains MediaPipe. That means TryProcess
/// succeeded and StoreSentisTrackingData rejected the geometry.
///
/// This patch keeps the normal geometry-quality gate. Only a zero-quality
/// result may use a continuity fallback, and only when its center/eye span are
/// coherent with the currently published face. The adopted quality is kept low
/// (0.10) so downstream filters know it was a soft recovery.
/// </summary>
[InitializeOnLoad]
public static class KiwiInferenceAdoptionInstaller
{
    private const string RunnerPath =
        "Assets/Script/FaceLandmarkerRunner.cs";

    private const string Marker =
        "KIWI_SENTIS_CONTINUITY_ADOPTION_V2_6";

    static KiwiInferenceAdoptionInstaller()
    {
        EditorApplication.delayCall +=
            EnsureInstalled;
    }

    [MenuItem(
        "Tools/Kiwi Avatar/Install Inference Continuity Adoption")]
    public static void EnsureInstalled()
    {
        if (!File.Exists(RunnerPath))
        {
            Debug.LogWarning(
                "[Kiwi Inference Adoption] Runner not found: " +
                RunnerPath);

            return;
        }

        string text =
            File.ReadAllText(
                RunnerPath);

        if (text.Contains(Marker))
        {
            return;
        }

        Regex gate =
            new Regex(
                @"if\s*\(\s*" +
                @"eyeSpan\s*<=\s*0\.0001f\s*\|\|\s*" +
                @"faceWidth2D\s*<=\s*0\.0001f\s*\|\|\s*" +
                @"geometryQuality\s*<=\s*0f\s*" +
                @"\)\s*\{\s*return\s+false\s*;\s*\}",
                RegexOptions.Multiline);

        MatchCollection matches =
            gate.Matches(text);

        if (matches.Count != 1)
        {
            Debug.LogError(
                "[Kiwi Inference Adoption] Expected exactly one Sentis geometry " +
                "gate but found " +
                matches.Count +
                ". Runner was left unchanged.");

            return;
        }

        string replacement =
@"// KIWI_SENTIS_CONTINUITY_ADOPTION_V2_6
            if (
                eyeSpan <= 0.0001f ||
                faceWidth2D <= 0.0001f ||
                faceHeight2D <= 0.0001f ||
                float.IsNaN(eyeSpan) ||
                float.IsInfinity(eyeSpan) ||
                float.IsNaN(faceWidth2D) ||
                float.IsInfinity(faceWidth2D) ||
                float.IsNaN(faceHeight2D) ||
                float.IsInfinity(faceHeight2D))
            {
                return false;
            }

            if (geometryQuality <= 0f)
            {
                bool coherentWithPublishedFace = false;

                lock (_trackingLock)
                {
                    if (
                        _latestLandmarkCount > 362 &&
                        _latestFaceEyeSpan > 0.0001f)
                    {
                        float spanRatio =
                            eyeSpan /
                            _latestFaceEyeSpan;

                        float centerDistance =
                            Vector2.Distance(
                                center,
                                _latestFaceCenter);

                        float allowedCenterDistance =
                            Mathf.Max(
                                0.10f,
                                _latestFaceEyeSpan * 5.0f);

                        coherentWithPublishedFace =
                            spanRatio >= 0.45f &&
                            spanRatio <= 2.20f &&
                            centerDistance <=
                                allowedCenterDistance &&
                            center.x > -0.20f &&
                            center.x < 1.20f &&
                            center.y > -0.25f &&
                            center.y < 1.35f;
                    }
                }

                if (!coherentWithPublishedFace)
                {
                    return false;
                }

                // Keep downstream quality-aware smoothing conservative.
                geometryQuality = 0.10f;
            }";

        text =
            gate.Replace(
                text,
                replacement,
                1);

        WriteUtf8PreserveBom(
            RunnerPath,
            text);

        AssetDatabase.ImportAsset(
            RunnerPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[Kiwi Inference Adoption] Installed v2.6 continuity-guarded " +
            "Inference Engine geometry adoption.");
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

        UTF8Encoding encoding =
            new UTF8Encoding(
                hasBom);

        File.WriteAllText(
            path,
            text,
            encoding);
    }
}
#endif
