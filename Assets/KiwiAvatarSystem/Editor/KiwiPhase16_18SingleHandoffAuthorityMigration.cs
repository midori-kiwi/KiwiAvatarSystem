#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Phase 16.18 commercial handoff-authority consolidation.
///
/// Phase16.16 added a Root-space provider bridge after the canonical ProviderHub
/// had already gained provider/resume normalization. Both mechanisms are useful
/// in isolation, but enabling both creates two release trajectories for one
/// provider switch.
///
/// Commercial default:
/// - ProviderHub owns provider-space / resume coordinate normalization.
/// - KiwiFaceMotion keeps no-frame hold, static rest and the final discontinuity
///   safety envelope.
/// - The Phase16.16 Root-space provider bridge remains compiled as a fallback
///   only when canonical handoff normalization is disabled.
/// </summary>
[InitializeOnLoad]
public static class KiwiPhase16_18SingleHandoffAuthorityMigration
{
    private const string TargetPath =
        "Assets/Script/KiwiFaceMotion.cs";

    private const string Prerequisite16 =
        "KIWI_V5_1_PHASE16_16_ROOT_SPACE_PROVIDER_BRIDGE";

    public const string Marker =
        "KIWI_V5_1_PHASE16_18_SINGLE_HANDOFF_AUTHORITY";

    private static int _retryCount;

    static KiwiPhase16_18SingleHandoffAuthorityMigration()
    {
        EditorApplication.delayCall += ApplyWhenReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 16.18 Single Handoff Authority")]
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

        string originalDisk =
            File.ReadAllText(TargetPath);

        string source =
            NormalizeNewlines(originalDisk);

        if (HasCompletePatch(source))
        {
            return;
        }

        // Phase16.17 patches Quality10, not KiwiFaceMotion, so this migration
        // must not wait for the Phase16.17 marker inside this file. Preflight
        // independently verifies the single-presentation-authority contract.
        if (!source.Contains(Prerequisite16))
        {
            Retry();
            return;
        }

        if (source.Contains(Marker))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase16.18 marker exists but complete " +
                "single-handoff-authority contract is missing.");
            return;
        }

        if (
            !PatchProviderBridgeState(ref source) ||
            !PatchProviderBridgeApplication(ref source) ||
            !PatchFinalEnvelope(ref source)
        )
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase16.18 could not locate expected " +
                "Phase16.15/16 anchors. KiwiFaceMotion was left unchanged.");
            return;
        }

        if (!HasCompletePatch(source))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase16.18 patch failed its final contract check.");
            return;
        }

        WritePreservingFormat(
            TargetPath,
            originalDisk,
            source);

        AssetDatabase.ImportAsset(
            TargetPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] Phase16.18 Single Handoff Authority applied. " +
            "ProviderHub owns canonical handoff normalization; the Phase16.16 " +
            "Root-space bridge is fallback-only.");
    }

    private static void Retry()
    {
        _retryCount++;

        if (_retryCount <= 24)
        {
            EditorApplication.delayCall +=
                ApplyWhenReady;
        }
    }

    private static bool PatchProviderBridgeState(
        ref string source)
    {
        const string signature =
            "    private void UpdatePhase16_16ProviderBridgeState(";

        if (!TryGetMethodRange(
            source,
            signature,
            out int start,
            out int end))
        {
            return false;
        }

        string method =
            source.Substring(start, end - start);

        const string markerLine =
            "        // KIWI_V5_1_PHASE16_18_SINGLE_HANDOFF_AUTHORITY\n";

        if (!method.Contains(markerLine))
        {
            int body =
                method.IndexOf("{\n", StringComparison.Ordinal);

            if (body < 0)
            {
                return false;
            }

            method =
                method.Insert(
                    body + 2,
                    markerLine +
                    "        bool canonicalHandoffOwner =\n" +
                    "            KiwiTrackingProviderHub.\n" +
                    "                CanonicalHandoffNormalizationEnabled;\n\n");
        }

        const string oldBranch =
            "        if (providerChanged && phase16_16EnableRootProviderBridge)\n" +
            "        {\n" +
            "            BeginPhase16_16ProviderBridge(providerGeneration, backend);\n" +
            "        }\n" +
            "        else if (_phase16_16ProviderBridgeActive && providerGeneration == _phase16_16BridgeProviderGeneration)\n";

        const string newBranch =
            "        if (canonicalHandoffOwner)\n" +
            "        {\n" +
            "            if (providerChanged && phase16_16EnableRootProviderBridge)\n" +
            "            {\n" +
            "                KiwiPhase16_18HandoffAuthorityDiagnostics.\n" +
            "                    ReportLocalProviderBridgeSuppressed();\n" +
            "            }\n\n" +
            "            if (_phase16_16ProviderBridgeActive)\n" +
            "            {\n" +
            "                ResetPhase16_16ActiveBridgeOnly();\n" +
            "            }\n" +
            "        }\n" +
            "        else if (providerChanged && phase16_16EnableRootProviderBridge)\n" +
            "        {\n" +
            "            BeginPhase16_16ProviderBridge(providerGeneration, backend);\n" +
            "        }\n" +
            "        else if (_phase16_16ProviderBridgeActive && providerGeneration == _phase16_16BridgeProviderGeneration)\n";

        if (method.Contains(oldBranch))
        {
            method =
                method.Replace(
                    oldBranch,
                    newBranch);
        }
        else if (!method.Contains("canonicalHandoffOwner"))
        {
            return false;
        }

        source =
            source.Substring(0, start) +
            method +
            source.Substring(end);

        return true;
    }

    private static bool PatchProviderBridgeApplication(
        ref string source)
    {
        const string signature =
            "    private void ApplyPhase16_16ProviderRootBridge(";

        if (!TryGetMethodRange(
            source,
            signature,
            out int start,
            out int end))
        {
            return false;
        }

        string method =
            source.Substring(start, end - start);

        const string inserted =
            "        if (KiwiTrackingProviderHub.CanonicalHandoffNormalizationEnabled)\n" +
            "        {\n" +
            "            if (_phase16_16ProviderBridgeActive)\n" +
            "            {\n" +
            "                ResetPhase16_16ActiveBridgeOnly();\n" +
            "            }\n\n" +
            "            KiwiPhase16_16ProviderBridgeDiagnostics.\n" +
            "                ReportApplied(0f, 0f, 0f);\n" +
            "            return;\n" +
            "        }\n\n";

        if (!method.Contains(inserted))
        {
            int body =
                method.IndexOf("{\n", StringComparison.Ordinal);

            if (body < 0)
            {
                return false;
            }

            method =
                method.Insert(
                    body + 2,
                    inserted);
        }

        source =
            source.Substring(0, start) +
            method +
            source.Substring(end);

        return true;
    }

    private static bool PatchFinalEnvelope(
        ref string source)
    {
        const string signature =
            "    private void ApplyPhase16_15RootCorrectionEnvelope()";

        if (!TryGetMethodRange(
            source,
            signature,
            out int start,
            out int end))
        {
            return false;
        }

        string method =
            source.Substring(start, end - start);

        const string oldCondition =
            "                _phase16_15ResumeBridgeActive ||\n" +
            "                KiwiFrameContinuityDiagnostics.DiscontinuityGuardActive\n";

        const string newCondition =
            "                _phase16_15ResumeBridgeActive ||\n" +
            "                KiwiTrackingProviderHub.CanonicalHandoffActive ||\n" +
            "                KiwiFrameContinuityDiagnostics.DiscontinuityGuardActive\n";

        if (method.Contains(oldCondition))
        {
            method =
                method.Replace(
                    oldCondition,
                    newCondition);
        }
        else if (!method.Contains(
            "KiwiTrackingProviderHub.CanonicalHandoffActive"))
        {
            return false;
        }

        const string anchor =
            "        if (finalEnvelopeActive)\n";

        const string report =
            "        KiwiPhase16_18HandoffAuthorityDiagnostics.\n" +
            "            ReportHubHandoffEnvelopeGuard(\n" +
            "                finalEnvelopeActive &&\n" +
            "                KiwiTrackingProviderHub.CanonicalHandoffActive);\n\n";

        if (!method.Contains(
            "ReportHubHandoffEnvelopeGuard("))
        {
            int index =
                method.IndexOf(
                    anchor,
                    StringComparison.Ordinal);

            if (index < 0)
            {
                return false;
            }

            method =
                method.Insert(
                    index,
                    report);
        }

        source =
            source.Substring(0, start) +
            method +
            source.Substring(end);

        return true;
    }

    private static bool HasCompletePatch(
        string source)
    {
        return
            source.Contains(Marker) &&
            source.Contains(
                "CanonicalHandoffNormalizationEnabled") &&
            source.Contains(
                "ReportLocalProviderBridgeSuppressed") &&
            source.Contains(
                "ReportHubHandoffEnvelopeGuard") &&
            source.Contains(
                "KiwiTrackingProviderHub.CanonicalHandoffActive");
    }

    private static bool TryGetMethodRange(
        string source,
        string signature,
        out int start,
        out int end)
    {
        start =
            source.IndexOf(
                signature,
                StringComparison.Ordinal);

        end = -1;

        if (start < 0)
        {
            return false;
        }

        int brace =
            source.IndexOf(
                '{',
                start);

        if (brace < 0)
        {
            return false;
        }

        int depth = 0;
        bool inString = false;
        bool inChar = false;
        bool escape = false;

        for (int i = brace; i < source.Length; i++)
        {
            char c = source[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if ((inString || inChar) && c == '\\')
            {
                escape = true;
                continue;
            }

            if (!inChar && c == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString && c == '\'')
            {
                inChar = !inChar;
                continue;
            }

            if (inString || inChar)
            {
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
                    end = i + 1;

                    if (
                        end < source.Length &&
                        source[end] == '\n')
                    {
                        end++;
                    }

                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeNewlines(
        string value)
    {
        return
            (value ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }

    private static void WritePreservingFormat(
        string path,
        string original,
        string normalized)
    {
        bool crlf =
            original.IndexOf("\r\n", StringComparison.Ordinal) >= 0;

        string output =
            crlf
                ? normalized.Replace("\n", "\r\n")
                : normalized;

        File.WriteAllText(
            path,
            output,
            new System.Text.UTF8Encoding(false));
    }
}
#endif
