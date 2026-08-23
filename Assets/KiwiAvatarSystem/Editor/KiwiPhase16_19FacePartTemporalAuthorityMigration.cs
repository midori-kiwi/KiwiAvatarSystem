#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Phase 16.19 commercial FacePart temporal-authority consolidation.
///
/// Quality10 remains the one policy/QoS preset owner but must not re-enable the
/// pre-transaction FacePart extrapolation paths. Render interpolation and all
/// local eye/mouth shape processing remain untouched.
/// </summary>
[InitializeOnLoad]
public static class KiwiPhase16_19FacePartTemporalAuthorityMigration
{
    private const string TargetPath =
        "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
        "KiwiTrackingQuality10Controller.cs";

    private const string Prerequisite =
        "KIWI_V5_1_PHASE16_17_SINGLE_PRESENTATION_AUTHORITY";

    public const string Marker =
        "KIWI_V5_1_PHASE16_19_STRICT_FACEPART_PRESENTATION_EPOCH";

    private static int _retryCount;

    static KiwiPhase16_19FacePartTemporalAuthorityMigration()
    {
        EditorApplication.delayCall += ApplyWhenReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 16.19 Strict FacePart Presentation Epoch")]
    private static void ApplyFromMenu()
    {
        _retryCount = 0;
        ApplyWhenReady();
    }

    private static void ApplyWhenReady()
    {
        if (!File.Exists(TargetPath))
        {
            Retry();
            return;
        }

        string originalDisk = File.ReadAllText(TargetPath);
        string source = NormalizeNewlines(originalDisk);

        if (HasCompletePatch(source))
        {
            return;
        }

        if (!source.Contains(Prerequisite))
        {
            Retry();
            return;
        }

        if (source.Contains(Marker))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase16.19 marker exists but its complete " +
                "FacePart epoch contract is missing. No partial rewrite was performed.");
            return;
        }

        if (!PatchCropperPreset(ref source) || !HasCompletePatch(source))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase16.19 could not safely locate the " +
                "Quality10 FacePart preset anchors. The file was left unchanged.");
            return;
        }

        WritePreservingFormat(TargetPath, originalDisk, source);
        AssetDatabase.ImportAsset(TargetPath, ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] Phase16.19 Strict FacePart Presentation Epoch " +
            "applied. FacePart render interpolation remains; matched-age " +
            "extrapolation/direct motion are disabled in the strict transaction path.");
    }

    private static void Retry()
    {
        _retryCount++;
        if (_retryCount <= 24)
        {
            EditorApplication.delayCall += ApplyWhenReady;
        }
    }

    private static bool PatchCropperPreset(ref string source)
    {
        const string signature =
            "    private static void ApplyCropperPreset(\n";

        if (!TryGetMethodRange(source, signature, out int start, out int end))
        {
            return false;
        }

        string method = source.Substring(start, end - start);

        if (!ReplaceAssignment(ref method, "cropper.enablePrediction", "false") ||
            !ReplaceAssignment(ref method, "cropper.compensateMatchedFrameAge", "false") ||
            !ReplaceAssignment(ref method, "cropper.directPositionDuringMotion", "false"))
        {
            return false;
        }

        const string insertAnchor =
            "    {\n";
        int body = method.IndexOf(insertAnchor, StringComparison.Ordinal);
        if (body < 0)
        {
            return false;
        }

        const string comment =
            "    {\n" +
            "        // KIWI_V5_1_PHASE16_19_STRICT_FACEPART_PRESENTATION_EPOCH\n" +
            "        // Phase16.9 pins pixels and semantic geometry to one sample.\n" +
            "        // Keep render interpolation, but do not extrapolate crop geometry\n" +
            "        // to a newer time than the pinned texture transaction.\n";

        method = method.Remove(body, insertAnchor.Length)
            .Insert(body, comment);

        source = source.Substring(0, start) + method + source.Substring(end);
        return true;
    }

    private static bool ReplaceAssignment(
        ref string method,
        string left,
        string value)
    {
        int start = method.IndexOf(left + " =", StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        int semicolon = method.IndexOf(';', start);
        if (semicolon < 0)
        {
            return false;
        }

        string replacement = left + " = " + value + ";";
        method = method.Substring(0, start) + replacement + method.Substring(semicolon + 1);
        return true;
    }

    private static bool HasCompletePatch(string source)
    {
        if (!source.Contains(Marker))
        {
            return false;
        }

        const string signature =
            "    private static void ApplyCropperPreset(\n";

        if (!TryGetMethodRange(source, signature, out int start, out int end))
        {
            return false;
        }

        string method = source.Substring(start, end - start);
        return
            method.Contains("cropper.enablePrediction = false;") &&
            method.Contains("cropper.compensateMatchedFrameAge = false;") &&
            method.Contains("cropper.directPositionDuringMotion = false;");
    }

    private static bool TryGetMethodRange(
        string source,
        string signature,
        out int start,
        out int end)
    {
        start = source.IndexOf(signature, StringComparison.Ordinal);
        end = -1;
        if (start < 0)
        {
            return false;
        }

        int open = source.IndexOf('{', start);
        if (open < 0)
        {
            return false;
        }

        int depth = 0;
        bool inString = false;
        bool inChar = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int i = open; i < source.Length; i++)
        {
            char c = source[i];
            char n = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
                continue;
            }
            if (inBlockComment)
            {
                if (c == '*' && n == '/') { inBlockComment = false; i++; }
                continue;
            }
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\') { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }
            if (c == '/' && n == '/') { inLineComment = true; i++; continue; }
            if (c == '/' && n == '*') { inBlockComment = true; i++; continue; }
            if (c == '"') { inString = true; continue; }
            if (c == '\'') { inChar = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    end = i + 1;
                    return true;
                }
            }
        }
        return false;
    }

    private static string NormalizeNewlines(string text)
    {
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    private static void WritePreservingFormat(
        string path,
        string originalDisk,
        string normalized)
    {
        bool crlf = originalDisk.Contains("\r\n");
        string output = crlf ? normalized.Replace("\n", "\r\n") : normalized;
        File.WriteAllText(path, output);
    }
}
#endif
