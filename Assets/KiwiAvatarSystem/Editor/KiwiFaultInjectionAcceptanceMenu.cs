#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Phase 12 explicit acceptance-test commands.
/// Nothing is executed automatically. Physical lifecycle scenarios are armed
/// first so the operator can perform the real camera/model/tracking action.
/// </summary>
public static class KiwiFaultInjectionAcceptanceMenu
{
    private const string Root =
        "Tools/Kiwi Avatar System/Acceptance Tests/";

    [MenuItem(Root + "Run Deterministic Matrix")]
    private static void RunDeterministicMatrix()
    {
        bool passed =
            KiwiFaultInjectionAcceptanceHarness.RunDeterministicMatrix(
                out string report);

        if (passed)
        {
            Debug.Log("[KiwiAcceptance] " + report);
        }
        else
        {
            Debug.LogError("[KiwiAcceptance] " + report);
        }
    }

    [MenuItem(Root + "Safe Injection/Provider Switch")]
    private static void RunProviderSwitch()
    {
        RunSafe(
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.ProviderSwitch);
    }

    [MenuItem(Root + "Safe Injection/Inference Stall Contract")]
    private static void RunInferenceStall()
    {
        RunSafe(
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.InferenceStall);
    }

    [MenuItem(Root + "Safe Injection/Mask Failure Contract")]
    private static void RunMaskFailure()
    {
        RunSafe(
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.MaskFailure);
    }

    [MenuItem(Root + "Safe Injection/Attachment Reacquire")]
    private static void RunAttachmentReacquire()
    {
        RunSafe(
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.AttachmentReacquire);
    }

    [MenuItem(Root + "Arm Manual/Camera Restart")]
    private static void ArmCameraRestart()
    {
        ArmManual(
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.CameraRestart,
            "Restart the real camera/source now. The harness expects CameraGeneration + CameraSessionChanged without a ProviderGeneration or ModelGeneration change.");
    }

    [MenuItem(Root + "Arm Manual/Model Hot Swap")]
    private static void ArmModelHotSwap()
    {
        ArmManual(
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.ModelHotSwap,
            "Perform the normal transactional model switch now. The harness expects ModelGeneration + CalibrationGeneration + Attachment semantic recovery and no Global recovery.");
    }

    [MenuItem(Root + "Arm Manual/Same Provider Resume")]
    private static void ArmSameProviderResume()
    {
        ArmManual(
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.SameProviderResume,
            "Create a short tracking interruption and let the SAME provider resume. ProviderGeneration must stay unchanged.");
    }

    [MenuItem(Root + "Arm Manual/Tracking Loss + Reacquire")]
    private static void ArmTrackingLossReacquire()
    {
        ArmManual(
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.TrackingLossReacquire,
            "Cause real tracking loss, then reacquire without changing provider identity. Both TrackingLost and TrackingReacquire must be observed.");
    }

    [MenuItem(Root + "Cancel Active Scenario")]
    private static void CancelActiveScenario()
    {
        KiwiFaultInjectionAcceptanceHarness harness = GetHarness();
        if (harness == null)
        {
            return;
        }

        harness.CancelActiveScenario();
        Debug.Log("[KiwiAcceptance] Active acceptance scenario cancelled.", harness);
    }

    [MenuItem(Root + "Log Acceptance Report")]
    private static void LogReport()
    {
        KiwiFaultInjectionAcceptanceHarness harness = GetHarness();
        if (harness == null)
        {
            return;
        }

        Debug.Log(
            "[KiwiAcceptance] Current acceptance report:\n" +
            harness.BuildJsonReport(true),
            harness);
    }

    [MenuItem(Root + "Export Acceptance JSON")]
    private static void ExportReport()
    {
        KiwiFaultInjectionAcceptanceHarness harness = GetHarness();
        if (harness == null)
        {
            return;
        }

        string path = harness.ExportJsonReport();
        Debug.Log("[KiwiAcceptance] Report exported: " + path, harness);
    }

    [MenuItem(Root + "Clear Acceptance History")]
    private static void ClearHistory()
    {
        KiwiFaultInjectionAcceptanceHarness harness = GetHarness();
        if (harness == null)
        {
            return;
        }

        harness.ClearHistory();
        Debug.Log("[KiwiAcceptance] Acceptance history cleared.", harness);
    }

    private static void RunSafe(
        KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario scenario)
    {
        KiwiFaultInjectionAcceptanceHarness harness = GetHarness();
        if (harness == null)
        {
            return;
        }

        if (harness.RunSafeInjection(scenario))
        {
            Debug.Log("[KiwiAcceptance] Running safe injection: " + scenario, harness);
        }
        else
        {
            Debug.LogWarning(
                "[KiwiAcceptance] Could not start " + scenario + ": " +
                KiwiFaultInjectionAcceptanceHarness.LastMessage,
                harness);
        }
    }

    private static void ArmManual(
        KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario scenario,
        string instruction)
    {
        KiwiFaultInjectionAcceptanceHarness harness = GetHarness();
        if (harness == null)
        {
            return;
        }

        if (harness.ArmScenario(scenario))
        {
            Debug.Log(
                "[KiwiAcceptance] Armed " + scenario + ". " + instruction,
                harness);
        }
        else
        {
            Debug.LogWarning(
                "[KiwiAcceptance] Could not arm " + scenario + ": " +
                KiwiFaultInjectionAcceptanceHarness.LastMessage,
                harness);
        }
    }

    private static KiwiFaultInjectionAcceptanceHarness GetHarness()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning(
                "[KiwiAcceptance] Live acceptance scenarios require Play Mode. " +
                "The deterministic matrix can run in Edit Mode.");
            return null;
        }

        KiwiFaultInjectionAcceptanceHarness harness =
            Object.FindFirstObjectByType<KiwiFaultInjectionAcceptanceHarness>(
                FindObjectsInactive.Include);

        if (harness == null)
        {
            Debug.LogWarning(
                "[KiwiAcceptance] Phase 12 harness is not active. " +
                "Wait for the scene to finish loading in Play Mode.");
        }

        return harness;
    }
}
#endif
