#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v4.5.4 repair migration for KiwiAvatarRuntimePanel.
///
/// v4.5.3 retired the legacy Tracking UI and also disabled the one-time
/// PlayerPrefs compatibility load. The supplied runtime recording showed that
/// this was too broad: previously validated face-part / tracking compatibility
/// settings were no longer restored, while the Commercial Profile only owns a
/// subset of those fields.
///
/// v4.5.4 therefore:
/// - restores the one-time TryLoadTrackingSettings() call;
/// - keeps the legacy Tracking / latency / jitter UI retired;
/// - keeps its old diagnostics refresh retired;
/// - leaves model import / model switching / model adjustments intact.
///
/// A runtime reconciler reapplies the v4.4+ Commercial Profile after the
/// compatibility load so commercial-owned fields remain authoritative.
/// </summary>
[InitializeOnLoad]
public static class KiwiLegacyRuntimePanelTrackingOwnerMigration
{
    private const string TargetPath =
        "Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs";

    private const string OldMarker =
        "KiwiAvatarSystem v4.5.3: legacy tracking owner disabled";

    private const string NewMarker =
        "KiwiAvatarSystem v4.5.4: compatibility load restored; legacy tracking UI retired";

    private const string LegacyLoadCall =
        "        TryLoadTrackingSettings();";

    private const string LegacyDrawCall =
        "        DrawTrackingControls(buttonHeight);";

    private const string LegacyDiagnosticsCall =
        "        RefreshTrackingDiagnostics();";

    private const string OldLoadReplacement =
        "        // " + OldMarker + "\n" +
        "        // Commercial Profile / Latency Budget / Quality Governor own tracking policy.";

    private const string OldDrawReplacement =
        "        // " + OldMarker + "\n" +
        "        // Legacy Tracking / latency / jitter controls intentionally not rendered.";

    private const string OldDiagnosticsReplacement =
        "        // " + OldMarker + "\n" +
        "        // Legacy tracking diagnostics are not refreshed when their UI is retired.";

    private const string NewDrawReplacement =
        "        // " + NewMarker + "\n" +
        "        // Legacy Tracking / latency / jitter controls intentionally not rendered.";

    private const string NewDiagnosticsReplacement =
        "        // " + NewMarker + "\n" +
        "        // Legacy diagnostics are retired with the legacy tracking UI.";

    static KiwiLegacyRuntimePanelTrackingOwnerMigration()
    {
        EditorApplication.delayCall +=
            ApplyIfNeeded;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Repair v4.5.4 Runtime Tracking Compatibility")]
    private static void ApplyFromMenu()
    {
        ApplyIfNeeded();
    }

    private static void ApplyIfNeeded()
    {
        if (!File.Exists(TargetPath))
        {
            return;
        }

        byte[] bytes =
            File.ReadAllBytes(
                TargetPath);

        bool hasBom =
            bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF;

        string source =
            File.ReadAllText(
                TargetPath);

        string original =
            source;

        // Repair a project that already ran the v4.5.3 migration.
        source =
            source.Replace(
                OldLoadReplacement,
                LegacyLoadCall);

        source =
            source.Replace(
                OldDrawReplacement,
                NewDrawReplacement);

        source =
            source.Replace(
                OldDiagnosticsReplacement,
                NewDiagnosticsReplacement);

        // Handle CRLF source modified by the previous migration.
        source =
            source.Replace(
                OldLoadReplacement.Replace("\n", "\r\n"),
                LegacyLoadCall);

        source =
            source.Replace(
                OldDrawReplacement.Replace("\n", "\r\n"),
                NewDrawReplacement);

        source =
            source.Replace(
                OldDiagnosticsReplacement.Replace("\n", "\r\n"),
                NewDiagnosticsReplacement);

        // Fresh v4.5/v4.5.2 source: preserve one-time compatibility loading,
        // but retire only the legacy continuously interactive Tracking UI.
        source =
            ReplaceExactCallOnce(
                source,
                LegacyDrawCall,
                NewDrawReplacement);

        source =
            ReplaceExactCallAll(
                source,
                LegacyDiagnosticsCall,
                NewDiagnosticsReplacement);

        if (source == original)
        {
            return;
        }

        File.WriteAllText(
            TargetPath,
            source,
            new UTF8Encoding(
                hasBom));

        AssetDatabase.ImportAsset(
            TargetPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] v4.5.4 repaired RuntimePanel ownership: " +
            "one-time legacy compatibility settings load is restored, while " +
            "the legacy Tracking UI remains retired. Commercial-owned fields " +
            "are reconciled after startup by KiwiCommercialStartupReconciler.");
    }

    private static string ReplaceExactCallOnce(
        string source,
        string call,
        string replacement)
    {
        int index =
            source.IndexOf(
                call,
                StringComparison.Ordinal);

        if (index < 0)
        {
            return source;
        }

        return
            source.Substring(
                0,
                index) +
            replacement +
            source.Substring(
                index +
                call.Length);
    }

    private static string ReplaceExactCallAll(
        string source,
        string call,
        string replacement)
    {
        return
            source.Replace(
                call,
                replacement);
    }
}
#endif
