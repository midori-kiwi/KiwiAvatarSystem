#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 16.14 targeted render-boundary continuity repair.
///
/// Phase 16.13 proved that this runtime rarely receives a genuinely newer
/// canonical sample between KiwiFaceMotion.LateUpdate and Application.onBeforeRender.
/// Advancing the display resampler again on the same already-consumed sample
/// therefore adds a second Root presentation write without improving tracking
/// freshness. This migration changes onBeforeRender to fresh-sample-only:
///
/// - a newly accepted authoritative sample may still late-latch and render;
/// - a same/rejected/no-sample render boundary performs no Root write;
/// - Phase 16.13 final static-rest presentation remains untouched.
///
/// No new Transform writer, temporal low-pass, pose buffer, tracker, provider
/// authority, or Eye/Mouth -> Root feedback is introduced.
/// </summary>
[InitializeOnLoad]
public static class KiwiPhase16_14RenderBoundaryContinuityMigration
{
    private const string TargetPath =
        "Assets/Script/KiwiFaceMotion.cs";

    private const string Prerequisite =
        "KIWI_V5_1_PHASE16_13_BEFORE_RENDER_REST_DEDUP";

    public const string Marker =
        "KIWI_V5_1_PHASE16_14_RENDER_BOUNDARY_FRESH_ONLY";

    static KiwiPhase16_14RenderBoundaryContinuityMigration()
    {
        EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 16.14 Render-Boundary Continuity")]
    private static void ApplyFromMenu()
    {
        if (!TryApplyNow(out string failure))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase 16.14 render-boundary migration failed: " +
                failure);
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

        if (!source.Contains(Prerequisite))
        {
            failure =
                "Phase 16.13 render-boundary prerequisite marker is missing. " +
                "Refusing to patch an unknown KiwiFaceMotion path.";
            return false;
        }

        if (HasCompletePatch(source))
        {
            return true;
        }

        if (source.Contains(Marker))
        {
            failure =
                "A Phase 16.14 marker exists but the complete fresh-only " +
                "contract is not present. No partial rewrite was performed.";
            return false;
        }

        if (!PatchBeforeRender(ref source, out failure))
        {
            return false;
        }

        if (!HasCompletePatch(source))
        {
            failure =
                "Phase 16.14 patch did not satisfy its complete-marker contract.";
            return false;
        }

        WritePreservingFormat(TargetPath, originalDisk, source);
        AssetDatabase.ImportAsset(TargetPath, ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] Applied v5.1 Phase 16.14 fresh-only " +
            "render-boundary continuity repair to KiwiFaceMotion.cs.");

        return true;
    }

    private static bool PatchBeforeRender(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string signature =
            "    private void OnBeforeRenderPrecision()";

        int methodIndex = source.IndexOf(signature, StringComparison.Ordinal);
        if (methodIndex < 0)
        {
            failure = "OnBeforeRenderPrecision was not found.";
            return false;
        }

        int openingBrace = source.IndexOf('{', methodIndex + signature.Length);
        int closingBrace = FindMatchingBrace(source, openingBrace);
        if (openingBrace < 0 || closingBrace < 0)
        {
            failure = "OnBeforeRenderPrecision braces could not be resolved.";
            return false;
        }

        string method = source.Substring(
            methodIndex,
            closingBrace - methodIndex + 1);

        const string phase13State =
            "        bool phase16_13ObservedNewRenderFrame = false;\n";

        if (!method.Contains(phase13State))
        {
            failure =
                "Phase 16.13 render-frame state anchor was not found.";
            return false;
        }

        method = method.Replace(
            phase13State,
            phase13State +
            "        // " + Marker + "\n" +
            "        bool phase16_14AcceptedNewRenderSample = false;\n");

        const string acceptedAnchor =
            "                if (accepted)\n" +
            "                {\n" +
            "                    _lastSeenTime = Time.unscaledTime;\n" +
            "                    _trackingWasLost = false;\n" +
            "                }\n";

        if (!method.Contains(acceptedAnchor))
        {
            failure =
                "onBeforeRender accepted-sample anchor was not found.";
            return false;
        }

        method = method.Replace(
            acceptedAnchor,
            "                if (accepted)\n" +
            "                {\n" +
            "                    phase16_14AcceptedNewRenderSample = true;\n" +
            "                    _lastSeenTime = Time.unscaledTime;\n" +
            "                    _trackingWasLost = false;\n" +
            "                }\n");

        const string calibrationGuard =
            "        if (!_calibrated)\n" +
            "        {\n" +
            "            return;\n" +
            "        }\n";

        if (!method.Contains(calibrationGuard))
        {
            failure =
                "onBeforeRender calibration guard was not found.";
            return false;
        }

        string freshOnlyGate =
            "\n" +
            "        // Same/rejected/no-sample boundaries cannot improve the\n" +
            "        // authoritative pose. LateUpdate has already rendered the\n" +
            "        // current display pose, so do not advance or rewrite Root.\n" +
            "        if (!phase16_14AcceptedNewRenderSample)\n" +
            "        {\n" +
            "            KiwiPhase16_13PresentationDiagnostics.RecordBeforeRenderSameSampleSkip();\n" +
            "            return;\n" +
            "        }\n\n" +
            "        KiwiPhase16_13PresentationDiagnostics.RecordBeforeRenderAcceptedNewSample();\n";

        method = method.Replace(
            calibrationGuard,
            calibrationGuard + freshOnlyGate);

        source =
            source.Substring(0, methodIndex) +
            method +
            source.Substring(closingBrace + 1);

        return true;
    }

    private static bool HasCompletePatch(string source)
    {
        return
            source.Contains(Marker) &&
            source.Contains("phase16_14AcceptedNewRenderSample") &&
            source.Contains(
                "RecordBeforeRenderSameSampleSkip();") &&
            source.Contains(
                "RecordBeforeRenderAcceptedNewSample();");
    }

    private static int FindMatchingBrace(
        string source,
        int openingBrace)
    {
        if (openingBrace < 0 || openingBrace >= source.Length)
        {
            return -1;
        }

        int depth = 0;
        bool inString = false;
        bool inChar = false;
        bool inLineComment = false;
        bool inBlockComment = false;
        bool escape = false;

        for (int i = openingBrace; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                }
                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
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

                if (c == '"')
                {
                    inString = false;
                }

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

                if (c == '\'')
                {
                    inChar = false;
                }

                continue;
            }

            if (c == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
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

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;

                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string NormalizeNewlines(string source)
    {
        return source
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }

    private static void WritePreservingFormat(
        string path,
        string original,
        string normalized)
    {
        string output =
            original.Contains("\r\n")
                ? normalized.Replace("\n", "\r\n")
                : normalized;

        File.WriteAllText(path, output);
    }

    private static void ApplyAutomaticallyWhenReady()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
            return;
        }

        if (!File.Exists(TargetPath))
        {
            return;
        }

        string source =
            NormalizeNewlines(
                File.ReadAllText(TargetPath));

        if (HasCompletePatch(source))
        {
            return;
        }

        if (!source.Contains(Prerequisite))
        {
            EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
            return;
        }

        if (!TryApplyNow(out string failure))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Automatic Phase 16.14 render-boundary " +
                "migration failed: " + failure);
        }
    }
}
#endif
