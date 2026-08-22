#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Phase 11 convenience commands for the passive runtime validator.
/// Commands never modify tracking configuration or avatar presentation.
/// </summary>
public static class KiwiRuntimeValidationMenu
{
    private const string Root =
        "Tools/Kiwi Avatar System/Runtime Validation/";

    [MenuItem(Root + "Log Current Report")]
    private static void LogCurrentReport()
    {
        KiwiRuntimeValidationHarness harness =
            Object.FindFirstObjectByType<KiwiRuntimeValidationHarness>(
                FindObjectsInactive.Include);

        if (harness == null)
        {
            Debug.LogWarning(
                "[KiwiValidation] Runtime Validation Harness is not active. " +
                "Enter Play Mode first.");
            return;
        }

        Debug.Log(
            "[KiwiValidation] Current runtime report:\n" +
            harness.BuildJsonReport(true),
            harness);
    }

    [MenuItem(Root + "Export JSON Report")]
    private static void ExportJsonReport()
    {
        KiwiRuntimeValidationHarness harness =
            Object.FindFirstObjectByType<KiwiRuntimeValidationHarness>(
                FindObjectsInactive.Include);

        if (harness == null)
        {
            Debug.LogWarning(
                "[KiwiValidation] Runtime Validation Harness is not active. " +
                "Enter Play Mode first.");
            return;
        }

        string path =
            harness.ExportJsonReport();

        Debug.Log(
            "[KiwiValidation] Report exported: " +
            path,
            harness);
    }

    [MenuItem(Root + "Clear Validation History")]
    private static void ClearValidationHistory()
    {
        KiwiRuntimeValidationHarness harness =
            Object.FindFirstObjectByType<KiwiRuntimeValidationHarness>(
                FindObjectsInactive.Include);

        if (harness == null)
        {
            Debug.LogWarning(
                "[KiwiValidation] Runtime Validation Harness is not active. " +
                "Enter Play Mode first.");
            return;
        }

        harness.ClearValidationHistory();
        Debug.Log(
            "[KiwiValidation] Runtime validation history cleared.",
            harness);
    }
}
#endif
