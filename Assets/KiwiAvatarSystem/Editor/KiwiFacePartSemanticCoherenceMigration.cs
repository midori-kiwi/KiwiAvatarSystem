#if UNITY_EDITOR
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v4.8 targeted migration for the completed FacePartCropper / FacePartShapeMask.
///
/// The supplied recording exposed a presentation-basis mismatch:
/// ShapeMask normalized a new semantic contour against Cropper.sampleRect, but
/// rendered that contour against RawImage.uvRect (the interpolated displayRect).
/// At low / irregular semantic cadence those rectangles can differ enough that
/// ToCropLocal clamps contour points to the crop edges, turning a tight eye mask
/// into a nearly full-ROI mask.
///
/// This migration preserves the established scripts and changes only:
/// - stale semantic result adoption,
/// - the crop-local mask basis,
/// - catastrophic crop-local contour rejection,
/// - bounded face-part prediction lifetime.
/// </summary>
[InitializeOnLoad]
public static class KiwiFacePartSemanticCoherenceMigration
{
    private const string CropperPath =
        "Assets/Script/FacePartCropper.cs";

    private const string ShapeMaskPath =
        "Assets/Script/FacePartShapeMask.cs";

    private const string CropperFreshnessMarker =
        "KIWI_V4_8_SEMANTIC_FRESHNESS_GATE";

    private const string PredictionMarker =
        "KIWI_V4_8_SEMANTIC_PREDICTION_LIFETIME";

    private const string MatchedAgeMarker =
        "KIWI_V4_8_MATCHED_AGE_DIAGNOSTIC_RANGE";

    private const string ShapeFreshnessMarker =
        "KIWI_V4_8_MASK_SEMANTIC_FRESHNESS_GATE";

    private const string MaskBasisMarker =
        "KIWI_V4_8_PRESENTATION_CROP_MASK_BASIS";

    private const string MaskTargetMarker =
        "KIWI_V4_8_TIGHT_MASK_CANDIDATE_GUARD";

    private const string UnclampedLocalMarker =
        "KIWI_V4_8_UNCLAMPED_SEMANTIC_LOCAL";

    static KiwiFacePartSemanticCoherenceMigration()
    {
        EditorApplication.delayCall += ApplyIfNeeded;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v4.8 Face-Part Semantic Coherence")]
    private static void ApplyFromMenu()
    {
        ApplyIfNeeded();
    }

    private static void ApplyIfNeeded()
    {
        bool cropperChanged =
            PatchCropper();

        bool maskChanged =
            PatchShapeMask();

        if (cropperChanged || maskChanged)
        {
            AssetDatabase.Refresh();

            Debug.Log(
                "[KiwiAvatarSystem] v4.8 applied semantic freshness, " +
                "presentation-coherent mask basis and bounded local prediction. " +
                "No avatar-root feedback or new smoothing stage was added.");
        }
    }

    private static bool PatchCropper()
    {
        if (!File.Exists(CropperPath))
        {
            return false;
        }

        string original =
            File.ReadAllText(CropperPath);

        string source =
            original.Replace("\r\n", "\n");

        bool changed = false;
        bool blocked = false;

        if (!source.Contains(CropperFreshnessMarker))
        {
            const string oldGate =
                "        if (\n" +
                "            hasFace &&\n" +
                "            hasNewLandmarks &&\n" +
                "            _landmarkBuffer != null &&\n";

            const string newGate =
                "        // KIWI_V4_8_SEMANTIC_FRESHNESS_GATE\n" +
                "        // A just-arrived ML result can still describe an old\n" +
                "        // camera frame. Hold the previous trusted crop instead\n" +
                "        // of replacing it with source-age-expired geometry.\n" +
                "        bool semanticSampleFresh =\n" +
                "            !hasNewLandmarks ||\n" +
                "            KiwiCommercialFacePartPolicy.IsSemanticSampleAdoptable(\n" +
                "                runner,\n" +
                "                timestamp);\n\n" +
                "        if (\n" +
                "            hasFace &&\n" +
                "            hasNewLandmarks &&\n" +
                "            semanticSampleFresh &&\n" +
                "            _landmarkBuffer != null &&\n";

            if (source.Contains(oldGate))
            {
                source = source.Replace(
                    oldGate,
                    newGate);
                changed = true;
            }
            else
            {
                blocked = true;
            }
        }

        if (!source.Contains(MatchedAgeMarker))
        {
            const string oldMatchedAge =
                "        _matchedFrameAgeSeconds = Mathf.Clamp(\n" +
                "            (float)age,\n" +
                "            0f,\n" +
                "            Mathf.Max(0.005f, maxExtrapolationSeconds)\n" +
                "        );";

            const string newMatchedAge =
                "        // KIWI_V4_8_MATCHED_AGE_DIAGNOSTIC_RANGE\n" +
                "        // Keep the real source age long enough for the\n" +
                "        // prediction lifetime gate to observe a semantic\n" +
                "        // stall. Extrapolation itself remains separately\n" +
                "        // capped by maxExtrapolationSeconds.\n" +
                "        _matchedFrameAgeSeconds = Mathf.Clamp(\n" +
                "            (float)age,\n" +
                "            0f,\n" +
                "            KiwiCommercialFacePartPolicy.\n" +
                "                MaximumSemanticSourceAgeSeconds * 2f\n" +
                "        );";

            if (source.Contains(oldMatchedAge))
            {
                source = source.Replace(
                    oldMatchedAge,
                    newMatchedAge);
                changed = true;
            }
            else
            {
                blocked = true;
            }
        }

        if (!source.Contains(PredictionMarker))
        {
            const string pattern =
                @"public static float CalculatePredictionTime\(\s*bool useMatchedFrameAge,\s*float matchedFrameAgeSeconds,\s*float elapsedSinceResult,\s*float leadSeconds,\s*float maximumSeconds\)\s*\{.*?\n    \}";

            const string replacement =
                "public static float CalculatePredictionTime(\n" +
                "        bool useMatchedFrameAge,\n" +
                "        float matchedFrameAgeSeconds,\n" +
                "        float elapsedSinceResult,\n" +
                "        float leadSeconds,\n" +
                "        float maximumSeconds)\n" +
                "    {\n" +
                "        // KIWI_V4_8_SEMANTIC_PREDICTION_LIFETIME\n" +
                "        // Prediction compensates a fresh result; it is not a\n" +
                "        // pose that may remain extrapolated forever after the\n" +
                "        // semantic stream stalls.\n" +
                "        float elapsed =\n" +
                "            Mathf.Max(0f, elapsedSinceResult);\n\n" +
                "        float age =\n" +
                "            useMatchedFrameAge &&\n" +
                "            IsFinite(matchedFrameAgeSeconds) &&\n" +
                "            matchedFrameAgeSeconds >= 0f\n" +
                "                ? Mathf.Max(matchedFrameAgeSeconds, elapsed)\n" +
                "                : elapsed;\n\n" +
                "        if (\n" +
                "            age >\n" +
                "                KiwiCommercialFacePartPolicy.MaximumSemanticSourceAgeSeconds\n" +
                "        )\n" +
                "        {\n" +
                "            return 0f;\n" +
                "        }\n\n" +
                "        float liveCap =\n" +
                "            Mathf.Min(\n" +
                "                Mathf.Max(0f, maximumSeconds),\n" +
                "                0.050f);\n\n" +
                "        float freshness =\n" +
                "            1f -\n" +
                "            Mathf.InverseLerp(\n" +
                "                0.120f,\n" +
                "                KiwiCommercialFacePartPolicy.MaximumSemanticSourceAgeSeconds,\n" +
                "                age);\n\n" +
                "        float predictionStrength =\n" +
                "            Mathf.Lerp(\n" +
                "                0.35f,\n" +
                "                1f,\n" +
                "                Mathf.Clamp01(freshness));\n\n" +
                "        return\n" +
                "            Mathf.Clamp(\n" +
                "                age + Mathf.Max(0f, leadSeconds),\n" +
                "                0f,\n" +
                "                liveCap) *\n" +
                "            predictionStrength;\n" +
                "    }";

            string patched =
                Regex.Replace(
                    source,
                    pattern,
                    replacement,
                    RegexOptions.Singleline);

            if (patched != source)
            {
                source = patched;
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
                "[KiwiAvatarSystem] v4.8 could not locate one or more " +
                "expected FacePartCropper blocks. Unmatched code was left " +
                "unchanged; no blind rewrite was performed.");
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
        if (!File.Exists(ShapeMaskPath))
        {
            return false;
        }

        string original =
            File.ReadAllText(ShapeMaskPath);

        string source =
            original.Replace("\r\n", "\n");

        bool changed = false;
        bool blocked = false;

        if (!source.Contains(ShapeFreshnessMarker))
        {
            const string oldBlock =
                "        if (!valid)\n" +
                "        {\n" +
                "            RenderFrameState(resolvedPart);\n" +
                "            return;\n" +
                "        }";

            const string newBlock =
                "        if (!valid)\n" +
                "        {\n" +
                "            RenderFrameState(resolvedPart);\n" +
                "            return;\n" +
                "        }\n\n" +
                "        // KIWI_V4_8_MASK_SEMANTIC_FRESHNESS_GATE\n" +
                "        // Cropper and ShapeMask must accept/reject the same\n" +
                "        // semantic timestamp or their coordinate bases diverge.\n" +
                "        if (\n" +
                "            !KiwiCommercialFacePartPolicy.IsSemanticSampleAdoptable(\n" +
                "                runner,\n" +
                "                timestamp)\n" +
                "        )\n" +
                "        {\n" +
                "            RenderFrameState(resolvedPart);\n" +
                "            return;\n" +
                "        }";

            if (source.Contains(oldBlock))
            {
                source = source.Replace(
                    oldBlock,
                    newBlock);
                changed = true;
            }
            else
            {
                blocked = true;
            }
        }

        if (!source.Contains(MaskBasisMarker))
        {
            const string pattern =
                @"Rect contourReferenceRect =\s*uvRect;\s*bool useCropLocalContour =\s*lockContourToMovingCrop &&\s*cropper != null &&\s*cropper\.TryGetSampleRect\(\s*_image,\s*out contourReferenceRect\s*\);";

            const string replacement =
                "// KIWI_V4_8_PRESENTATION_CROP_MASK_BASIS\n" +
                "        // Normalize the semantic contour against the crop that\n" +
                "        // is actually being rendered now. Using the future\n" +
                "        // sampleRect here while decoding against display uvRect\n" +
                "        // made low-cadence masks expand toward the ROI edges.\n" +
                "        Rect contourReferenceRect =\n" +
                "            uvRect;\n\n" +
                "        bool useCropLocalContour =\n" +
                "            lockContourToMovingCrop &&\n" +
                "            cropper != null;";

            string patched =
                Regex.Replace(
                    source,
                    pattern,
                    replacement,
                    RegexOptions.Singleline);

            if (patched != source)
            {
                source = patched;
                changed = true;
            }
            else
            {
                blocked = true;
            }
        }

        if (!source.Contains(MaskTargetMarker))
        {
            const string pattern =
                @"    private void SetContourTarget\(\s*int count,\s*Rect referenceRect,\s*bool cropLocal\)\s*\{.*?\n    \}\n\n\n    private void CopyTargetContourToRendered\(\)";

            const string replacement =
                "    private void SetContourTarget(\n" +
                "        int count,\n" +
                "        Rect referenceRect,\n" +
                "        bool cropLocal)\n" +
                "    {\n" +
                "        // KIWI_V4_8_TIGHT_MASK_CANDIDATE_GUARD\n" +
                "        // Build into the upload scratch buffer first. A bad\n" +
                "        // semantic/crop pairing holds the previous complete\n" +
                "        // contour; it never partially overwrites the target.\n" +
                "        bool coordinateModeChanged =\n" +
                "            _maskPointsAreCropLocal != cropLocal;\n\n" +
                "        int candidateCount =\n" +
                "            Mathf.Clamp(count, 0, MaxPoints);\n\n" +
                "        float localEnvelope =\n" +
                "            Mathf.Clamp(\n" +
                "                0.12f + Mathf.Max(0f, cropLocalSafetyMargin),\n" +
                "                0.12f,\n" +
                "                0.24f);\n\n" +
                "        for (int i = 0; i < candidateCount; i++)\n" +
                "        {\n" +
                "            Vector2 point =\n" +
                "                cropLocal\n" +
                "                    ? KiwiFacePartMaskCoherenceMath.ToCropLocal(\n" +
                "                        _smoothContour[i],\n" +
                "                        referenceRect,\n" +
                "                        cropLocalSafetyMargin)\n" +
                "                    : _smoothContour[i];\n\n" +
                "            bool finite =\n" +
                "                !float.IsNaN(point.x) &&\n" +
                "                !float.IsInfinity(point.x) &&\n" +
                "                !float.IsNaN(point.y) &&\n" +
                "                !float.IsInfinity(point.y);\n\n" +
                "            bool plausible =\n" +
                "                !cropLocal ||\n" +
                "                (\n" +
                "                    point.x >= -localEnvelope &&\n" +
                "                    point.x <= 1f + localEnvelope &&\n" +
                "                    point.y >= -localEnvelope &&\n" +
                "                    point.y <= 1f + localEnvelope\n" +
                "                );\n\n" +
                "            if (!finite || !plausible)\n" +
                "            {\n" +
                "                return;\n" +
                "            }\n\n" +
                "            _uploadShaderPoints[i] =\n" +
                "                new Vector4(point.x, point.y, 0f, 0f);\n" +
                "        }\n\n" +
                "        for (int i = candidateCount; i < MaxPoints; i++)\n" +
                "        {\n" +
                "            _uploadShaderPoints[i] =\n" +
                "                Vector4.zero;\n" +
                "        }\n\n" +
                "        _maskPointsAreCropLocal =\n" +
                "            cropLocal;\n\n" +
                "        _targetPointCount =\n" +
                "            candidateCount;\n\n" +
                "        for (int i = 0; i < MaxPoints; i++)\n" +
                "        {\n" +
                "            _targetShaderPoints[i] =\n" +
                "                _uploadShaderPoints[i];\n" +
                "        }\n\n" +
                "        if (\n" +
                "            strictLandmarkerTracking ||\n" +
                "            !_hasRenderedContour ||\n" +
                "            coordinateModeChanged ||\n" +
                "            _renderedPointCount != _targetPointCount\n" +
                "        )\n" +
                "        {\n" +
                "            CopyTargetContourToRendered();\n" +
                "        }\n" +
                "    }\n\n\n" +
                "    private void CopyTargetContourToRendered()";

            string patched =
                Regex.Replace(
                    source,
                    pattern,
                    replacement,
                    RegexOptions.Singleline);

            if (patched != source)
            {
                source = patched;
                changed = true;
            }
            else
            {
                blocked = true;
            }
        }

        if (!source.Contains(UnclampedLocalMarker))
        {
            const string pattern =
                @"    public static Vector2 ToCropLocal\(\s*Vector2 point,\s*Rect cropRect,\s*float safetyMargin\)\s*\{.*?\n    \}\n\n\n    public static Vector2 FromCropLocal";

            const string replacement =
                "    public static Vector2 ToCropLocal(\n" +
                "        Vector2 point,\n" +
                "        Rect cropRect,\n" +
                "        float safetyMargin)\n" +
                "    {\n" +
                "        // KIWI_V4_8_UNCLAMPED_SEMANTIC_LOCAL\n" +
                "        // Tight semantic contours must remain semantic.\n" +
                "        // Clamping every out-of-crop point to the ROI edge can\n" +
                "        // convert a small eye into an almost full-crop polygon.\n" +
                "        // Catastrophic local coordinates are rejected atomically\n" +
                "        // by SetContourTarget instead.\n" +
                "        float safeWidth =\n" +
                "            Mathf.Max(0.000001f, Mathf.Abs(cropRect.width));\n\n" +
                "        float safeHeight =\n" +
                "            Mathf.Max(0.000001f, Mathf.Abs(cropRect.height));\n\n" +
                "        return new Vector2(\n" +
                "            (point.x - cropRect.xMin) / safeWidth,\n" +
                "            (point.y - cropRect.yMin) / safeHeight\n" +
                "        );\n" +
                "    }\n\n\n" +
                "    public static Vector2 FromCropLocal";

            string patched =
                Regex.Replace(
                    source,
                    pattern,
                    replacement,
                    RegexOptions.Singleline);

            if (patched != source)
            {
                source = patched;
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
                "[KiwiAvatarSystem] v4.8 could not locate one or more " +
                "expected FacePartShapeMask blocks. Unmatched code was left " +
                "unchanged; no blind rewrite was performed.");
        }

        if (!changed)
        {
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
