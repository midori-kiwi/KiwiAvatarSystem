#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor controls for the Phase 16 camera / landmark / avatar comparison tool.
/// All actions are diagnostic-only and require Play Mode.
/// </summary>
public static class KiwiFrameComparisonMenu
{
    private const string Root =
        "Tools/Kiwi Avatar System/Frame Comparison/";

    [MenuItem(Root + "Toggle Camera + Landmark Overlay")]
    private static void ToggleOverlay()
    {
        KiwiFrameComparisonOverlay overlay = GetOverlay();
        if (overlay == null)
        {
            return;
        }

        overlay.ToggleOverlay();
        Debug.Log(
            "[KiwiAvatarSystem] Frame comparison: " +
            overlay.BuildStatusLine());
    }

    [MenuItem(Root + "Start CSV Recording")]
    private static void StartCsv()
    {
        KiwiFrameComparisonOverlay overlay = GetOverlay();
        overlay?.StartCsvRecording();
    }

    [MenuItem(Root + "Stop CSV Recording")]
    private static void StopCsv()
    {
        KiwiFrameComparisonOverlay overlay = GetOverlay();
        overlay?.StopCsvRecording();
    }

    [MenuItem(Root + "Log Current Status")]
    private static void LogStatus()
    {
        KiwiFrameComparisonOverlay overlay = GetOverlay();
        if (overlay == null)
        {
            return;
        }

        Debug.Log(
            "[KiwiAvatarSystem] Frame comparison: " +
            overlay.BuildStatusLine() +
            (string.IsNullOrEmpty(overlay.CurrentCsvPath)
                ? string.Empty
                : " path=" + overlay.CurrentCsvPath));
    }

    private static KiwiFrameComparisonOverlay GetOverlay()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Frame Comparison is available in Play Mode.");
            return null;
        }

        return KiwiFrameComparisonOverlay.EnsureInstance();
    }
}
#endif
