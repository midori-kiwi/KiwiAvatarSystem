#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v3.5 narrow migration for FaceLandmarkerRunner's Inference ROI seed.
///
/// MediaPipe's face-landmark ROI policy is:
/// 1) tight bounds around all face landmarks,
/// 2) rotation vector 33 -> 263,
/// 3) scale_x=1.5 / scale_y=1.5,
/// 4) square_long in PIXELS.
///
/// The old Kiwi seed used max(normalizedWidth, normalizedHeight) as both UV
/// width and UV height. On a 16:9 camera that is not a pixel-square crop and
/// severely distorts the 192x192 landmark model input.
/// </summary>
[InitializeOnLoad]
public static class KiwiInferenceRoiParityInstaller
{
    private const string RunnerPath =
        "Assets/Script/FaceLandmarkerRunner.cs";

    private const string Marker =
        "KIWI_MEDIAPIPE_ROI_PARITY_V3_5";

    private const string BlockStart =
        "            UnityEngine.Rect sentisAnchorRegion = default;";

    private const string BlockEnd =
        "            if (arrivalHostTicks <= 0L)";

    static KiwiInferenceRoiParityInstaller()
    {
        EditorApplication.delayCall +=
            EnsureInstalled;
    }

    [MenuItem(
        "Tools/Kiwi Avatar/Install v3.5 MediaPipe ROI Parity")]
    public static void EnsureInstalled()
    {
        if (!File.Exists(RunnerPath))
        {
            Fail(
                "FaceLandmarkerRunner source was not found.");

            return;
        }

        string text =
            File.ReadAllText(
                RunnerPath);

        if (
            text.IndexOf(
                Marker,
                StringComparison.Ordinal) >=
            0
        )
        {
            return;
        }

        int start =
            text.IndexOf(
                BlockStart,
                StringComparison.Ordinal);

        if (start < 0)
        {
            Fail(
                "Inference anchor block start was not found.");

            return;
        }

        int end =
            text.IndexOf(
                BlockEnd,
                start +
                BlockStart.Length,
                StringComparison.Ordinal);

        if (end < 0)
        {
            Fail(
                "Inference anchor block end was not found.");

            return;
        }

        string oldBlock =
            text.Substring(
                start,
                end -
                start);

        if (
            oldBlock.IndexOf(
                "anchorSize",
                StringComparison.Ordinal) <
                0 ||
            oldBlock.IndexOf(
                "faceWidth2D",
                StringComparison.Ordinal) <
                0 ||
            oldBlock.IndexOf(
                "faceHeight2D",
                StringComparison.Ordinal) <
                0
        )
        {
            Fail(
                "Inference anchor block no longer matches the known pre-v3.5 source.");

            return;
        }

        string replacement =
@"            // " + Marker + @"
            // Match MediaPipe FaceLandmarkLandmarksToRoi:
            // full landmark bounds -> 33/263 roll -> 1.5x pixel-square long side.
            UnityEngine.Rect sentisAnchorRegion =
                default;

            float sentisAnchorRollRadians =
                0f;

            bool hasSentisAnchor =
                count >
                    454 &&
                _sourceTextureWidth >
                    0 &&
                _sourceTextureHeight >
                    0;

            if (hasSentisAnchor)
            {
                float minX =
                    float.PositiveInfinity;

                float minY =
                    float.PositiveInfinity;

                float maxX =
                    float.NegativeInfinity;

                float maxY =
                    float.NegativeInfinity;

                for (
                    int i = 0;
                    i < count;
                    i++
                )
                {
                    float x =
                        landmarks[i].x;

                    float y =
                        landmarks[i].y;

                    if (
                        float.IsNaN(x) ||
                        float.IsInfinity(x) ||
                        float.IsNaN(y) ||
                        float.IsInfinity(y)
                    )
                    {
                        hasSentisAnchor =
                            false;

                        break;
                    }

                    minX =
                        Mathf.Min(
                            minX,
                            x);

                    minY =
                        Mathf.Min(
                            minY,
                            y);

                    maxX =
                        Mathf.Max(
                            maxX,
                            x);

                    maxY =
                        Mathf.Max(
                            maxY,
                            y);
                }

                if (hasSentisAnchor)
                {
                    float imageWidth =
                        Mathf.Max(
                            1f,
                            _sourceTextureWidth);

                    float imageHeight =
                        Mathf.Max(
                            1f,
                            _sourceTextureHeight);

                    float boxWidthPixels =
                        (
                            maxX -
                            minX
                        ) *
                        imageWidth;

                    float boxHeightPixels =
                        (
                            maxY -
                            minY
                        ) *
                        imageHeight;

                    float squareSidePixels =
                        Mathf.Max(
                            boxWidthPixels,
                            boxHeightPixels) *
                        1.50f;

                    if (
                        squareSidePixels <=
                            1f
                    )
                    {
                        hasSentisAnchor =
                            false;
                    }
                    else
                    {
                        float anchorWidth =
                            Mathf.Clamp(
                                squareSidePixels /
                                imageWidth,
                                0.04f,
                                2.50f);

                        float anchorHeight =
                            Mathf.Clamp(
                                squareSidePixels /
                                imageHeight,
                                0.04f,
                                2.50f);

                        Vector2 anchorCenter =
                            new Vector2(
                                (
                                    minX +
                                    maxX
                                ) *
                                0.5f,
                                (
                                    minY +
                                    maxY
                                ) *
                                0.5f);

                        sentisAnchorRegion =
                            new UnityEngine.Rect(
                                anchorCenter.x -
                                    anchorWidth *
                                    0.5f,
                                anchorCenter.y -
                                    anchorHeight *
                                    0.5f,
                                anchorWidth,
                                anchorHeight);

                        float eyeDxPixels =
                            (
                                landmarks[263].x -
                                landmarks[33].x
                            ) *
                            imageWidth;

                        float eyeDyPixels =
                            (
                                landmarks[263].y -
                                landmarks[33].y
                            ) *
                            imageHeight;

                        if (
                            eyeDxPixels *
                                eyeDxPixels +
                            eyeDyPixels *
                                eyeDyPixels <=
                                0.000001f
                        )
                        {
                            hasSentisAnchor =
                                false;
                        }
                        else
                        {
                            // Runner landmarks use top-left Y; the tracker crop
                            // transform uses bottom-left Y.
                            sentisAnchorRollRadians =
                                -Mathf.Atan2(
                                    eyeDyPixels,
                                    eyeDxPixels);
                        }
                    }
                }
            }


";

        string updated =
            text.Substring(
                0,
                start) +
            replacement +
            text.Substring(
                end);

        WriteUtf8PreserveBom(
            RunnerPath,
            updated);

        AssetDatabase.ImportAsset(
            RunnerPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[Kiwi v3.5] Installed MediaPipe pixel-square ROI parity.");
    }

    private static void WriteUtf8PreserveBom(
        string path,
        string text)
    {
        byte[] original =
            File.ReadAllBytes(
                path);

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
            path,
            text,
            new UTF8Encoding(
                hasBom));
    }

    private static void Fail(
        string message)
    {
        Debug.LogError(
            "[Kiwi v3.5] " +
            message +
            " Runner was left unchanged.");
    }
}
#endif
