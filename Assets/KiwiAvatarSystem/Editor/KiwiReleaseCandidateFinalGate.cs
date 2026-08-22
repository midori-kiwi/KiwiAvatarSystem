#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 15 final RC evidence gate.
///
/// This editor-only coordinator does not mutate tracking, recovery, generations,
/// calibration, presentation, runtime policy, or scene state. It binds three
/// independently-earned proofs to one saved-project fingerprint:
/// 1) Phase 15 static preflight PASS;
/// 2) one Play session covering all eight distinct Phase 12 acceptance scenarios;
/// 3) Phase 11 runtime validation with zero Error/Critical records.
///
/// Only then can an RC1 passing stamp be written. Development builds may still
/// be used while gathering evidence; non-Development builds require the final
/// RC stamp through KiwiReleaseCandidateBuildGate.
/// </summary>
[InitializeOnLoad]
public static class KiwiReleaseCandidateFinalGate
{
    public const string GateVersion = "5.1.0-phase15-rc1";

    private const string MenuRoot =
        "Tools/Kiwi Avatar System/Release Candidate Final Gate/";

    private const string RuntimeEvidenceRelativePath =
        "Library/KiwiAvatarSystem/Phase15RuntimeAcceptanceEvidence.json";

    private const string FinalStampRelativePath =
        "Library/KiwiAvatarSystem/Phase15RC1PassingGate.json";

    private const string FinalReportRelativePath =
        "Library/KiwiAvatarSystem/Phase15RC1FinalReport.json";

    private const int RequiredHealthyFrameStreak = 30;

    [Serializable]
    public sealed class RuntimeEvidence
    {
        public string version;
        public string preflightVersion;
        public string fingerprint;
        public string capturedUtc;
        public string unityVersion;
        public bool deterministicMatrixPassed;
        public string deterministicMatrixReport;
        public int requiredScenarioCount;
        public int uniqueScenarioPassCount;
        public string missingScenarios;
        public int acceptancePassedRecords;
        public int acceptanceFailedRecords;
        public string runtimeValidationHealth;
        public int runtimeValidationWarnings;
        public int runtimeValidationErrors;
        public int runtimeValidationCriticals;
        public int runtimeValidationHealthyFrameStreak;
        public string lastValidationCode;
        public int lastValidationFrame;
        public KiwiFaultInjectionAcceptanceHarness.AcceptanceRecord[] acceptanceRecords;
    }

    [Serializable]
    public sealed class FinalRcReport
    {
        public string version;
        public string preflightVersion;
        public string fingerprint;
        public string generatedUtc;
        public bool passed;
        public string status;
        public RuntimeEvidence runtimeEvidence;
    }

    [Serializable]
    private sealed class FinalStamp
    {
        public string version;
        public string preflightVersion;
        public string fingerprint;
        public string generatedUtc;
    }

    [MenuItem(MenuRoot + "1. Run Static Preflight", priority = 1)]
    private static void RunStaticPreflight()
    {
        KiwiReleaseCandidatePreflight.PreflightReport report =
            KiwiReleaseCandidatePreflight.RunFullPreflight(true);

        if (report != null && report.passed)
        {
            Debug.Log(
                "[KiwiRC] Static preflight PASS. Enter Play Mode and complete " +
                "the eight acceptance scenarios before capturing runtime evidence.");
        }
    }

    [MenuItem(MenuRoot + "2. Capture Runtime Acceptance Evidence", priority = 2)]
    private static void CaptureRuntimeEvidenceMenu()
    {
        if (TryCaptureRuntimeEvidence(out RuntimeEvidence evidence, out string reason))
        {
            SaveRuntimeEvidence(evidence);
            DeleteFinalStamp();
            Debug.Log(
                "[KiwiRC] Runtime acceptance evidence captured: 8/8 unique " +
                "scenarios, validation Error/Critical 0, fingerprint=" +
                evidence.fingerprint + ". Exit Play Mode, then Finalize RC1.");
        }
        else
        {
            Debug.LogError("[KiwiRC] Runtime evidence capture blocked: " + reason);
        }
    }

    [MenuItem(MenuRoot + "2. Capture Runtime Acceptance Evidence", true)]
    private static bool ValidateCaptureRuntimeEvidenceMenu()
    {
        return EditorApplication.isPlaying;
    }

    [MenuItem(MenuRoot + "3. Finalize RC1", priority = 3)]
    private static void FinalizeRc1Menu()
    {
        FinalRcReport report = BuildFinalReport();
        SaveFinalReport(report);

        if (report.passed)
        {
            SaveFinalStamp(report);
            Debug.Log(
                "[KiwiRC] RC1 FINAL PASS. Static preflight, runtime validation, " +
                "and 8/8 acceptance coverage are bound to fingerprint " +
                report.fingerprint + ".");
        }
        else
        {
            DeleteFinalStamp();
            Debug.LogError("[KiwiRC] RC1 finalization FAIL: " + report.status);
        }
    }

    [MenuItem(MenuRoot + "Log RC1 Status", priority = 20)]
    private static void LogStatus()
    {
        FinalRcReport report = BuildFinalReport();
        Debug.Log(
            "[KiwiRC] Current RC1 status:\n" +
            JsonUtility.ToJson(report, true));
    }

    [MenuItem(MenuRoot + "Clear Runtime/Final Evidence", priority = 40)]
    private static void ClearEvidence()
    {
        DeleteIfExists(GetRuntimeEvidencePath());
        DeleteFinalStamp();
        DeleteIfExists(GetFinalReportPath());
        Debug.Log("[KiwiRC] Phase 15 runtime/final RC evidence cleared.");
    }

    public static bool HasCurrentFinalStamp(out string reason)
    {
        reason = string.Empty;

        if (!KiwiReleaseCandidatePreflight.TryGetCurrentPassingFingerprint(
                out string fingerprint,
                out string preflightReason))
        {
            reason = "Static preflight is not current: " + preflightReason;
            return false;
        }

        FinalStamp stamp = LoadFinalStamp();
        if (stamp == null)
        {
            reason = "No Phase 15 RC1 passing stamp exists.";
            return false;
        }

        if (!string.Equals(stamp.version, GateVersion, StringComparison.Ordinal))
        {
            reason = "Final stamp version is " + stamp.version +
                ", expected " + GateVersion + ".";
            return false;
        }

        if (!string.Equals(
                stamp.preflightVersion,
                KiwiReleaseCandidatePreflight.PreflightVersion,
                StringComparison.Ordinal))
        {
            reason = "Final stamp belongs to preflight " +
                stamp.preflightVersion + ".";
            return false;
        }

        if (!string.Equals(stamp.fingerprint, fingerprint, StringComparison.Ordinal))
        {
            reason = "Project fingerprint changed after RC1 evidence was finalized.";
            return false;
        }

        return true;
    }

    public static bool TryCaptureRuntimeEvidence(
        out RuntimeEvidence evidence,
        out string reason)
    {
        evidence = null;
        reason = string.Empty;

        if (!EditorApplication.isPlaying)
        {
            reason = "Runtime evidence must be captured in Play Mode.";
            return false;
        }

        if (!KiwiReleaseCandidatePreflight.TryGetCurrentPassingFingerprint(
                out string fingerprint,
                out string preflightReason))
        {
            reason = "Static preflight is not current: " + preflightReason;
            return false;
        }

        if (!KiwiFaultInjectionAcceptanceHarness.HasRuntimeInstance)
        {
            reason = "Phase 12 Acceptance Harness is not active.";
            return false;
        }

        if (KiwiFaultInjectionAcceptanceHarness.ActiveScenario !=
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.None)
        {
            reason = "An acceptance scenario is still active: " +
                KiwiFaultInjectionAcceptanceHarness.ActiveScenario + ".";
            return false;
        }

        bool matrixPassed =
            KiwiFaultInjectionAcceptanceHarness.RunDeterministicMatrix(
                out string matrixReport);

        bool coveragePassed =
            KiwiFaultInjectionAcceptanceHarness.TryGetRequiredScenarioCoverage(
                out int uniquePassed,
                out string missing);

        if (!KiwiFaultInjectionAcceptanceHarness.TryGetHistorySnapshot(
                out KiwiFaultInjectionAcceptanceHarness.AcceptanceRecord[] records))
        {
            reason = "Acceptance history is unavailable.";
            return false;
        }

        if (!KiwiRuntimeValidationHarness.HasRuntimeInstance)
        {
            reason = "Phase 11 Runtime Validation Harness is not active.";
            return false;
        }

        if (!matrixPassed)
        {
            reason = "Deterministic acceptance matrix failed: " + matrixReport;
            return false;
        }

        if (!coveragePassed)
        {
            reason = "Unique acceptance coverage is " + uniquePassed + "/" +
                KiwiFaultInjectionAcceptanceHarness.RequiredScenarioCount +
                ". Missing: " + missing + ".";
            return false;
        }

        int nonPassRecords = 0;
        for (int i = 0; i < records.Length; i++)
        {
            if (!string.Equals(
                    records[i].state,
                    KiwiFaultInjectionAcceptanceHarness.AcceptanceState.Passed.ToString(),
                    StringComparison.Ordinal))
            {
                nonPassRecords++;
            }
        }

        if (nonPassRecords > 0)
        {
            reason = "This Play session contains " + nonPassRecords +
                " failed/cancelled acceptance result(s). Clear history, rerun the " +
                "campaign cleanly, and capture again.";
            return false;
        }

        if (
            KiwiRuntimeValidationHarness.Health !=
                KiwiRuntimeValidationHarness.ValidationHealth.Healthy ||
            KiwiRuntimeValidationHarness.ErrorCount > 0 ||
            KiwiRuntimeValidationHarness.CriticalCount > 0
        )
        {
            reason = "Runtime validation is not RC-clean: health=" +
                KiwiRuntimeValidationHarness.Health +
                " errors=" + KiwiRuntimeValidationHarness.ErrorCount +
                " criticals=" + KiwiRuntimeValidationHarness.CriticalCount + ".";
            return false;
        }

        if (KiwiRuntimeValidationHarness.HealthyFrameStreak < RequiredHealthyFrameStreak)
        {
            reason = "Runtime validation needs " + RequiredHealthyFrameStreak +
                " consecutive healthy frames after the last transition; current=" +
                KiwiRuntimeValidationHarness.HealthyFrameStreak + ".";
            return false;
        }

        evidence = new RuntimeEvidence
        {
            version = GateVersion,
            preflightVersion = KiwiReleaseCandidatePreflight.PreflightVersion,
            fingerprint = fingerprint,
            capturedUtc = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            deterministicMatrixPassed = true,
            deterministicMatrixReport = matrixReport,
            requiredScenarioCount =
                KiwiFaultInjectionAcceptanceHarness.RequiredScenarioCount,
            uniqueScenarioPassCount = uniquePassed,
            missingScenarios = missing,
            acceptancePassedRecords =
                KiwiFaultInjectionAcceptanceHarness.PassedCount,
            acceptanceFailedRecords = nonPassRecords,
            runtimeValidationHealth =
                KiwiRuntimeValidationHarness.Health.ToString(),
            runtimeValidationWarnings =
                KiwiRuntimeValidationHarness.WarningCount,
            runtimeValidationErrors =
                KiwiRuntimeValidationHarness.ErrorCount,
            runtimeValidationCriticals =
                KiwiRuntimeValidationHarness.CriticalCount,
            runtimeValidationHealthyFrameStreak =
                KiwiRuntimeValidationHarness.HealthyFrameStreak,
            lastValidationCode =
                KiwiRuntimeValidationHarness.LastViolationCode,
            lastValidationFrame =
                KiwiRuntimeValidationHarness.LastViolationFrame,
            acceptanceRecords = records
        };

        return true;
    }

    public static FinalRcReport BuildFinalReport()
    {
        FinalRcReport report = new FinalRcReport
        {
            version = GateVersion,
            preflightVersion = KiwiReleaseCandidatePreflight.PreflightVersion,
            generatedUtc = DateTime.UtcNow.ToString("O"),
            passed = false,
            status = "Unknown"
        };

        if (!KiwiReleaseCandidatePreflight.TryGetCurrentPassingFingerprint(
                out string fingerprint,
                out string preflightReason))
        {
            report.status = "Static preflight is not current: " + preflightReason;
            return report;
        }

        report.fingerprint = fingerprint;

        RuntimeEvidence evidence = LoadRuntimeEvidence();
        report.runtimeEvidence = evidence;

        if (evidence == null)
        {
            report.status = "No Phase 15 runtime acceptance evidence exists.";
            return report;
        }

        if (!string.Equals(evidence.version, GateVersion, StringComparison.Ordinal))
        {
            report.status = "Runtime evidence version is stale: " + evidence.version + ".";
            return report;
        }

        if (!string.Equals(evidence.fingerprint, fingerprint, StringComparison.Ordinal))
        {
            report.status = "Project fingerprint changed after runtime evidence capture.";
            return report;
        }

        if (
            !evidence.deterministicMatrixPassed ||
            evidence.uniqueScenarioPassCount != evidence.requiredScenarioCount ||
            evidence.requiredScenarioCount !=
                KiwiFaultInjectionAcceptanceHarness.RequiredScenarioCount ||
            !string.IsNullOrEmpty(evidence.missingScenarios) ||
            evidence.acceptanceFailedRecords != 0 ||
            evidence.runtimeValidationErrors != 0 ||
            evidence.runtimeValidationCriticals != 0 ||
            !string.Equals(
                evidence.runtimeValidationHealth,
                KiwiRuntimeValidationHarness.ValidationHealth.Healthy.ToString(),
                StringComparison.Ordinal) ||
            evidence.runtimeValidationHealthyFrameStreak < RequiredHealthyFrameStreak
        )
        {
            report.status = "Runtime evidence does not satisfy the Phase 15 RC contract.";
            return report;
        }

        HashSet<string> covered = new HashSet<string>(StringComparer.Ordinal);
        if (evidence.acceptanceRecords != null)
        {
            for (int i = 0; i < evidence.acceptanceRecords.Length; i++)
            {
                KiwiFaultInjectionAcceptanceHarness.AcceptanceRecord record =
                    evidence.acceptanceRecords[i];

                if (string.Equals(
                        record.state,
                        KiwiFaultInjectionAcceptanceHarness.AcceptanceState.Passed.ToString(),
                        StringComparison.Ordinal))
                {
                    covered.Add(record.scenario ?? string.Empty);
                }
            }
        }

        string[] requiredNames =
        {
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.CameraRestart.ToString(),
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.ProviderSwitch.ToString(),
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.SameProviderResume.ToString(),
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.InferenceStall.ToString(),
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.ModelHotSwap.ToString(),
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.MaskFailure.ToString(),
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.AttachmentReacquire.ToString(),
            KiwiFaultInjectionAcceptanceHarness.AcceptanceScenario.TrackingLossReacquire.ToString()
        };

        for (int i = 0; i < requiredNames.Length; i++)
        {
            if (!covered.Contains(requiredNames[i]))
            {
                report.status = "Runtime evidence is missing PASS record for " +
                    requiredNames[i] + ".";
                return report;
            }
        }

        report.passed = true;
        report.status = "PASS: static preflight + 8/8 unique runtime acceptance + " +
            "validation Error/Critical 0 are bound to one fingerprint.";
        return report;
    }

    private static void SaveRuntimeEvidence(RuntimeEvidence evidence)
    {
        WriteJson(GetRuntimeEvidencePath(), evidence);
    }

    private static RuntimeEvidence LoadRuntimeEvidence()
    {
        string path = GetRuntimeEvidencePath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonUtility.FromJson<RuntimeEvidence>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveFinalReport(FinalRcReport report)
    {
        WriteJson(GetFinalReportPath(), report);
    }

    private static void SaveFinalStamp(FinalRcReport report)
    {
        FinalStamp stamp = new FinalStamp
        {
            version = GateVersion,
            preflightVersion = report.preflightVersion,
            fingerprint = report.fingerprint,
            generatedUtc = report.generatedUtc
        };

        WriteJson(GetFinalStampPath(), stamp);
    }

    private static FinalStamp LoadFinalStamp()
    {
        string path = GetFinalStampPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonUtility.FromJson<FinalStamp>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void DeleteFinalStamp()
    {
        DeleteIfExists(GetFinalStampPath());
    }

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(
            path,
            JsonUtility.ToJson(value, true),
            new UTF8Encoding(false));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string GetRuntimeEvidencePath()
    {
        return ProjectPath(RuntimeEvidenceRelativePath);
    }

    private static string GetFinalStampPath()
    {
        return ProjectPath(FinalStampRelativePath);
    }

    private static string GetFinalReportPath()
    {
        return ProjectPath(FinalReportRelativePath);
    }

    private static string ProjectPath(string relative)
    {
        string projectRoot = Path.GetFullPath(
            Path.Combine(Application.dataPath, ".."));
        return Path.Combine(
            projectRoot,
            relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
#endif
