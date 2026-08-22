#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 16.4 presentation-hold continuity repair.
///
/// Measured Phase 16.3b CSV showed that continuity/step caps were active, but
/// the display pose was frozen on every Tracking Holding render frame. At the
/// next accepted callback the existing resampler was therefore allowed to
/// consume one bounded 50 ms correction in a single render, creating a visible
/// staircase step. This migration keeps prediction at zero during Holding but
/// lets the existing display resampler continue converging toward the last
/// already-accepted rigid sample.
///
/// No new Root writer, pose history, observation, provider policy, or predictor
/// is introduced. KiwiFaceMotion remains the sole Rigid Pose Authority.
/// </summary>
[InitializeOnLoad]
public static class KiwiFrameContinuityPhase16_4PresentationHoldMigration
{
    private const string TargetPath =
        "Assets/Script/KiwiFaceMotion.cs";

    private const string Phase16_2Prerequisite =
        "KIWI_V5_1_PHASE16_2_MEASURED_CONTINUITY_GUARD";

    public const string Marker =
        "KIWI_V5_1_PHASE16_4_PRESENTATION_HOLD_RESAMPLING";

    static KiwiFrameContinuityPhase16_4PresentationHoldMigration()
    {
        EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 16.4 Presentation Hold Resampling")]
    private static void ApplyFromMenu()
    {
        if (!TryApplyNow(out string failure))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase 16.4 migration failed: " + failure);
        }
    }

    public static bool TryApplyNow(out string failure)
    {
        failure = string.Empty;

        if (!File.Exists(TargetPath))
        {
            failure = "KiwiFaceMotion.cs was not found at " + TargetPath + ".";
            return false;
        }

        string originalDisk = File.ReadAllText(TargetPath);
        string source = NormalizeNewlines(originalDisk);

        if (!source.Contains(Phase16_2Prerequisite))
        {
            failure =
                "Phase 16.2 prerequisite marker is missing. Phase 16.4 will " +
                "not rewrite the Holding presentation path before the measured " +
                "continuity contract exists.";
            return false;
        }

        if (HasCompletePhase16_4(source, out string existingFailure))
        {
            return true;
        }

        if (source.Contains(Marker))
        {
            failure =
                "Phase 16.4 marker exists but the Holding branch is incomplete: " +
                existingFailure;
            return false;
        }

        const string signature = "if (holdRigidPose)";
        int ifIndex = source.IndexOf(signature, StringComparison.Ordinal);
        if (ifIndex < 0)
        {
            failure = "holdRigidPose branch was not found in KiwiFaceMotion.LateUpdate.";
            return false;
        }

        int openingBrace = source.IndexOf('{', ifIndex + signature.Length);
        if (openingBrace < 0)
        {
            failure = "holdRigidPose opening brace was not found.";
            return false;
        }

        int closingBrace = FindMatchingBrace(source, openingBrace);
        if (closingBrace < 0)
        {
            failure = "holdRigidPose closing brace was not found.";
            return false;
        }

        string oldBlock = source.Substring(
            ifIndex,
            closingBrace - ifIndex + 1);

        if (!oldBlock.Contains("ResetPredictionHistory();"))
        {
            failure =
                "holdRigidPose no longer resets prediction history; refusing an " +
                "unknown presentation-path rewrite.";
            return false;
        }

        if (oldBlock.Contains("UpdateAndRenderDisplayPose(dt);"))
        {
            string alreadyPatched = EnsureMarkerInBlock(source, ifIndex);
            if (alreadyPatched == source ||
                !HasCompletePhase16_4(alreadyPatched, out failure))
            {
                if (string.IsNullOrEmpty(failure))
                {
                    failure = "could not place the Phase 16.4 marker.";
                }
                return false;
            }

            WritePreservingFormat(TargetPath, originalDisk, alreadyPatched);
            AssetDatabase.ImportAsset(TargetPath, ImportAssetOptions.ForceUpdate);
            return true;
        }

        if (!oldBlock.Contains("RenderDisplayPose();"))
        {
            failure =
                "holdRigidPose does not contain the expected frozen " +
                "RenderDisplayPose() path; refusing an unknown rewrite.";
            return false;
        }

        string indent = GetLineIndent(source, ifIndex);
        string inner = indent + "    ";

        string newBlock =
            indent + "if (holdRigidPose)\n" +
            indent + "{\n" +
            inner + "// " + Marker + "\n" +
            inner + "// Tracking authority remains held and prediction is reset to zero,\n" +
            inner + "// but presentation must keep converging toward the last already-accepted\n" +
            inner + "// rigid pose. Freezing the display here created sparse-cadence stair steps.\n" +
            inner + "ResetPredictionHistory();\n\n" +
            inner + "// No prediction is possible after ResetPredictionHistory(). This only\n" +
            inner + "// advances the existing Phase 16 display resampler toward _sample.\n" +
            inner + "UpdateAndRenderDisplayPose(dt);\n" +
            inner + "return;\n" +
            indent + "}";

        string patched =
            source.Substring(0, ifIndex) +
            newBlock +
            source.Substring(closingBrace + 1);

        if (!HasCompletePhase16_4(patched, out failure))
        {
            return false;
        }

        WritePreservingFormat(TargetPath, originalDisk, patched);
        AssetDatabase.ImportAsset(TargetPath, ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] v5.1 Phase 16.4 applied Holding presentation " +
            "resampling. Prediction still resets to zero; only the existing " +
            "display resampler continues toward the last accepted rigid pose.");

        return true;
    }

    private static void ApplyAutomaticallyWhenReady()
    {
        if (
            EditorApplication.isCompiling ||
            EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode ||
            !File.Exists(TargetPath)
        )
        {
            return;
        }

        string source = NormalizeNewlines(File.ReadAllText(TargetPath));
        if (
            source.Contains(Marker) ||
            !source.Contains(Phase16_2Prerequisite)
        )
        {
            return;
        }

        if (!TryApplyNow(out string failure))
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Phase 16.4 automatic migration did not " +
                "rewrite KiwiFaceMotion. Exact reason: " + failure);
        }
    }

    public static bool HasCompletePhase16_4(
        string source,
        out string failure)
    {
        failure = string.Empty;

        if (!source.Contains(Marker))
        {
            failure = "marker is missing";
            return false;
        }

        const string signature = "if (holdRigidPose)";
        int ifIndex = source.IndexOf(signature, StringComparison.Ordinal);
        if (ifIndex < 0)
        {
            failure = "holdRigidPose branch is missing";
            return false;
        }

        int openingBrace = source.IndexOf('{', ifIndex + signature.Length);
        int closingBrace = openingBrace >= 0
            ? FindMatchingBrace(source, openingBrace)
            : -1;
        if (closingBrace < 0)
        {
            failure = "holdRigidPose block is structurally invalid";
            return false;
        }

        string block = source.Substring(
            ifIndex,
            closingBrace - ifIndex + 1);

        if (!block.Contains("ResetPredictionHistory();"))
        {
            failure = "Holding no longer zeros prediction";
            return false;
        }

        if (!block.Contains("UpdateAndRenderDisplayPose(dt);"))
        {
            failure = "Holding does not advance the existing display resampler";
            return false;
        }

        if (block.Contains("RenderDisplayPose();"))
        {
            failure = "legacy frozen display-only Holding path is still present";
            return false;
        }

        return true;
    }

    private static string EnsureMarkerInBlock(
        string source,
        int ifIndex)
    {
        if (source.Contains(Marker))
        {
            return source;
        }

        int openingBrace = source.IndexOf('{', ifIndex);
        if (openingBrace < 0)
        {
            return source;
        }

        string indent = GetLineIndent(source, ifIndex) + "    ";
        string markerLine =
            "\n" + indent + "// " + Marker;

        return source.Insert(openingBrace + 1, markerLine);
    }

    private static int FindMatchingBrace(
        string source,
        int openingBrace)
    {
        int depth = 0;
        bool inString = false;
        bool inChar = false;
        bool escape = false;
        bool lineComment = false;
        bool blockComment = false;

        for (int i = openingBrace; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (lineComment)
            {
                if (c == '\n') lineComment = false;
                continue;
            }

            if (blockComment)
            {
                if (c == '*' && next == '/')
                {
                    blockComment = false;
                    i++;
                }
                continue;
            }

            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }
                if (c == '\\')
                {
                    escape = true;
                    continue;
                }
                if (c == '"') inString = false;
                continue;
            }

            if (inChar)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }
                if (c == '\\')
                {
                    escape = true;
                    continue;
                }
                if (c == '\'') inChar = false;
                continue;
            }

            if (c == '/' && next == '/')
            {
                lineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                blockComment = true;
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '\'')
            {
                inChar = true;
                continue;
            }

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }

    private static string GetLineIndent(
        string source,
        int index)
    {
        int lineStart = source.LastIndexOf('\n', Math.Max(0, index - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        int i = lineStart;
        while (i < source.Length &&
               (source[i] == ' ' || source[i] == '\t'))
        {
            i++;
        }

        return source.Substring(lineStart, i - lineStart);
    }

    private static string NormalizeNewlines(string value)
    {
        return value.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static void WritePreservingFormat(
        string path,
        string originalDisk,
        string normalizedOutput)
    {
        string newline = originalDisk.Contains("\r\n") ? "\r\n" : "\n";
        string output = newline == "\n"
            ? normalizedOutput
            : normalizedOutput.Replace("\n", "\r\n");

        File.WriteAllText(
            path,
            output,
            new System.Text.UTF8Encoding(false));
    }
}
#endif
