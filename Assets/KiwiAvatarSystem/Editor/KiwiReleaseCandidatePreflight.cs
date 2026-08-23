#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// v5.1 Phase 15 release-candidate preflight / scene-integrity gate.
///
/// Phase 15 retains the Phase 14 external-local-package policy: selected large
/// packages may be intentionally supplied outside Git. Their source artifact
/// does not have to exist in the repository, but the package must still be
/// resolved by Unity at the exact required version before an RC stamp can pass.
///
/// This editor-only validator does not mutate tracking, presentation, recovery,
/// generations, calibration, runtime policy, or scene object state. It inspects
/// the saved project and release scenes, writes a local pass stamp under Library,
/// and blocks Play Mode when the saved project fingerprint no longer matches a
/// passing full preflight.
/// </summary>
[InitializeOnLoad]
public static class KiwiReleaseCandidatePreflight
{
    public const string PreflightVersion = "5.1.0-phase16.18";
    public const string BaseCommit =
        "0b890a317cc3de64a4eadc955ce87bf21479786b";
    public const string ExpectedUnityVersion = "6000.0.80f1";
    public const string ExpectedPrimarySceneName = "Face Landmark Detection";

    [Serializable]
    public struct PreflightIssue
    {
        public string severity;
        public string code;
        public string message;
        public string assetPath;
        public string objectPath;
    }

    [Serializable]
    public sealed class PreflightReport
    {
        public string version;
        public string baseCommit;
        public string unityVersion;
        public string generatedUtc;
        public string fingerprint;
        public bool passed;
        public bool releaseCandidateReady;
        public int scenesScanned;
        public int objectsScanned;
        public int componentsScanned;
        public int warningCount;
        public int errorCount;
        public int criticalCount;
        public string[] releaseScenes;
        public PreflightIssue[] issues;
    }

    [Serializable]
    private sealed class PassingStamp
    {
        public string version;
        public string fingerprint;
        public string generatedUtc;
    }

    private sealed class SceneScanSummary
    {
        public readonly Dictionary<string, int> roleCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly Dictionary<string, List<MonoBehaviour>> roleInstances =
            new Dictionary<string, List<MonoBehaviour>>(StringComparer.Ordinal);
        public int objects;
        public int components;
    }

    private sealed class MarkerRule
    {
        public string path;
        public string[] markers;

        public MarkerRule(string path, params string[] markers)
        {
            this.path = path;
            this.markers = markers;
        }
    }

    private sealed class PackageRule
    {
        public string name;
        public string expectedVersion;
        public bool versionMustMatch;

        public PackageRule(
            string name,
            string expectedVersion,
            bool versionMustMatch)
        {
            this.name = name;
            this.expectedVersion = expectedVersion;
            this.versionMustMatch = versionMustMatch;
        }
    }

    private const string MenuRoot =
        "Tools/Kiwi Avatar System/Release Candidate Preflight/";
    private const string PlayGateMenu =
        MenuRoot + "Play Gate Enabled";
    private const string GatePrefSuffix =
        ".KiwiAvatarSystem.Phase16_18.PlayGate";
    private const string StampRelativePath =
        "Library/KiwiAvatarSystem/Phase16_18PassingPreflight.json";
    private const string ExportFileName =
        "KiwiPhase16_18ReleaseCandidatePreflight.json";

    private static readonly string[] SingletonRoleNames =
    {
        "FaceLandmarkerRunner",
        "KiwiFaceMotion",
        "FacePartCropper",
        "KiwiTrackingProviderHub",
        "KiwiCanonicalTrackingFrameCoordinator",
        "KiwiRecoveryDomainCoordinator",
        "KiwiFacePartPresentationResolver",
        "KiwiRuntimePolicyResolver",
        "KiwiRuntimeValidationHarness",
        "KiwiFaultInjectionAcceptanceHarness",
        "KiwiMatureTrackingTelemetry",
        "KiwiCommercialQualityGovernor",
        "KiwiLatencyBudgetController"
    };

    private static readonly string[] RequiredCoreRoleNames =
    {
        "FaceLandmarkerRunner",
        "KiwiFaceMotion",
        "FacePartCropper"
    };

    private static readonly MarkerRule[] MarkerRules =
    {
        new MarkerRule(
            "Assets/Script/FaceLandmarkerRunner.cs",
            "KIWI_ASYNC_INFERENCE_MAILBOX_V2_3",
            "KIWI_V5_1_PHASE8_CAMERA_GENERATION_OWNER"),
        new MarkerRule(
            "Assets/Script/KiwiFaceMotion.cs",
            "KIWI_V4_7_COMMERCIAL_RIGID_PHASE_AUTHORITY",
            "KIWI_V5_1_PHASE7_ROOT_CALIBRATION_GENERATION",
            "KIWI_V5_1_PHASE9_PROVIDER_TIMEBASE_GAP",
            "KIWI_V5_1_PHASE16_FRAME_CONTINUITY_GUARD",
            "KIWI_V5_1_PHASE16_2_MEASURED_CONTINUITY_GUARD",
            "KIWI_V5_1_PHASE16_3_CONTINUITY_APPLY_CONFIRMED",
            "KIWI_V5_1_PHASE16_4_PRESENTATION_HOLD_RESAMPLING",
            "KIWI_V5_1_PHASE16_13_STATIC_REST_PRESENTATION",
            "KIWI_V5_1_PHASE16_13_BEFORE_RENDER_REST_DEDUP",
            "KIWI_V5_1_PHASE16_14_RENDER_BOUNDARY_FRESH_ONLY",
            "KIWI_V5_1_PHASE16_15_NO_FRAME_HOLD_RESUME_ENVELOPE",
            "KIWI_V5_1_PHASE16_16_ROOT_SPACE_PROVIDER_BRIDGE",
            "KIWI_V5_1_PHASE16_18_SINGLE_HANDOFF_AUTHORITY"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/TrackingFoundation/" +
            "KiwiTrackingProviderHub.cs",
            "KIWI_V5_1_PHASE16_5_COMMERCIAL_HANDOFF_RELEASE",
            "KIWI_V5_1_PHASE16_6_COMMERCIAL_RIGID_COHERENCE_GUARD",
            "KIWI_V5_1_PHASE16_8_COMMERCIAL_STICKY_RIGID_AUTHORITY",
            "KIWI_V5_1_PHASE16_18_SINGLE_HANDOFF_AUTHORITY",
            "CanonicalHandoffNormalizationEnabled",
            "CanonicalHandoffActive"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
            "KiwiMatureVTuberSupervisor.cs",
            "KIWI_V5_1_PHASE16_5_COMMERCIAL_CADENCE_HEADROOM",
            "KIWI_V5_1_PHASE16_8_COMMERCIAL_TRACKING_QOS_FLOOR",
            "KIWI_V5_1_PHASE16_8_COMMERCIAL_CADENCE_HYSTERESIS",
            "KIWI_V5_1_PHASE16_10_HYBRID_AUXILIARY_BUDGET",
            "KIWI_V5_1_PHASE16_12_PERSISTENT_ROI_POLICY",
            "KIWI_V5_1_PHASE16_13_LATENCY_FIRST_PRESENTATION_POLICY",
            "KIWI_V5_1_PHASE16_14_STABLE_PIPELINE_RENDER_CONTINUITY_POLICY",
            "KIWI_V5_1_PHASE16_15_NO_FRAME_HOLD_RESUME_ENVELOPE_POLICY",
            "KIWI_V5_1_PHASE16_16_ROOT_SPACE_PROVIDER_HANDOFF_POLICY",
            "KIWI_V5_1_PHASE16_17_SINGLE_PRESENTATION_AUTHORITY_POLICY",
            "KIWI_V5_1_PHASE16_18_SINGLE_HANDOFF_AUTHORITY_POLICY"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
            "KiwiCommercialStartupReconciler.cs",
            "KIWI_V5_1_PHASE16_17_SINGLE_PRESENTATION_AUTHORITY_CONTRACT",
            "KIWI_V5_1_PHASE16_18_SINGLE_HANDOFF_AUTHORITY_CONTRACT"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/TrackingFoundation/" +
            "KiwiCanonicalTrackingFrame.cs",
            "KIWI_V5_1_PHASE16_6_COMMERCIAL_CANONICAL_SEMANTIC_COHERENCE",
            "KIWI_V5_1_PHASE16_7_COMMERCIAL_LANDMARK_REFINER"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/TrackingFoundation/" +
            "KiwiCommercialLandmarkRefiner.cs",
            "KIWI_V5_1_PHASE16_7_COMMERCIAL_LANDMARK_REFINER",
            "KIWI_V5_1_PHASE16_8_LANDMARK_REFINER_FALSE_POSITIVE_GUARD",
            "KIWI_V5_1_PHASE16_9_STABLE_ISOLATED_CANDIDATE_SNAPSHOT",
            "KIWI_V5_1_PHASE16_9_STABLE_ISOLATED_CANDIDATE_PASS",
            "KIWI_V5_1_PHASE16_9_APPLY_STABLE_ISOLATED_CANDIDATE"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
            "KiwiCommercialFacePartPolicy.cs",
            "KIWI_V5_1_PHASE16_6_COMMERCIAL_SEMANTIC_TRANSACTION"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
            "KiwiCommercialPresentationTuner.cs",
            "KIWI_V5_1_PHASE16_6_COMMERCIAL_PRESENTATION_PROFILE",
            "KIWI_V5_1_PHASE16_8_PRESENTATION_HEADROOM_FLOOR"),
        new MarkerRule(
            "Assets/Script/KiwiInferenceFaceTracker.cs",
            "KIWI_V5_1_PHASE16_10_COMMERCIAL_FRESH_FRAME_PIPELINE",
            "KIWI_V5_1_PHASE16_10_DESKTOP_BOUNDED_2_3_LANE",
            "KIWI_V5_1_PHASE16_10_DESKTOP_FRESHNESS_HYSTERESIS",
            "KIWI_V5_1_PHASE16_11_FRESHNESS_FIRST_INFERENCE",
            "KIWI_V5_1_PHASE16_11_SOFT_ANCHOR_FUTURE_ONLY",
            "KIWI_V5_1_PHASE16_11_SCHEDULE_BEFORE_CPU_DECODE",
            "KIWI_V5_1_PHASE16_12_PERSISTENT_ROI_AUTHORITY",
            "KIWI_V5_1_PHASE16_12_PRESENCE_RECOVERY_EXPANSION",
            "KIWI_V5_1_PHASE16_12_INFERENCE_STAGE_PROFILING",
            "KIWI_V5_1_PHASE16_13_LATENCY_FIRST_SINGLE_FLIGHT",
            "KIWI_V5_1_PHASE16_14_STABLE_DESKTOP_THREE_LANE"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Validation/" +
            "KiwiFrameComparisonOverlay.cs",
            "KIWI_V5_1_PHASE16_8_COMMERCIAL_GAP_DIAGNOSTICS",
            "KIWI_V5_1_PHASE16_9_COMMERCIAL_PIPELINE_AND_FACEPART_DIAGNOSTICS",
            "KIWI_V5_1_PHASE16_10_INFERENCE_PIPELINE_DIAGNOSTICS",
            "KIWI_V5_1_PHASE16_11_FRESHNESS_AGE_DIAGNOSTICS",
            "KIWI_V5_1_PHASE16_12_PERSISTENT_ROI_DIAGNOSTICS",
            "KIWI_V5_1_PHASE16_13_LATENCY_AND_STATIC_REST_DIAGNOSTICS",
            "KIWI_V5_1_PHASE16_14_STABLE_PIPELINE_RENDER_BOUNDARY_DIAGNOSTICS",
            "KIWI_V5_1_PHASE16_15_ROOT_CONTINUITY_DIAGNOSTICS",
            "KIWI_V5_1_PHASE16_16_ROOT_SPACE_PROVIDER_BRIDGE_DIAGNOSTICS",
            "KIWI_V5_1_PHASE16_17_SINGLE_PRESENTATION_AUTHORITY_DIAGNOSTICS",
            "KIWI_V5_1_PHASE16_18_SINGLE_HANDOFF_AUTHORITY_DIAGNOSTICS",
            "singlePresentationAuthority",
            "singleHandoffAuthority",
            "canonicalHandoffNormalizationEnabled",
            "localRootProviderBridgeActive",
            "handoffAuthorityViolationCount",
            "quality10LegacyWriteViolationCount",
            "faceMotionPredictionDisabled",
            "rootProviderBridgeActive",
            "rootProviderBridgeWeight",
            "rootProviderBridgeAppliedPosDelta",
            "inferenceScheduleDelayMs",
            "inferenceRawPresenceLogit",
            "inferenceRegionRetentionActive",
            "inferenceTrackingHealthy",
            "inferenceGpuReadbackWaitMs",
            "inferenceAcceptedSourceAgeMs",
            "inferenceSoftAnchorUpdateCount",
            "inferenceLaneLimit",
            "inferenceLatencyFirstScheduling",
            "inferenceStableDesktopScheduling",
            "inferenceCompletionIntervalMs",
            "staticRestActive",
            "beforeRenderRestHoldCount",
            "beforeRenderFreshOnlyPolicy",
            "beforeRenderSameSampleSkipCount",
            "beforeRenderAcceptedNewSampleCount",
            "rootContinuityCapApplied",
            "rootAuthoritativeFrameMissing",
            "noFrameHoldCount",
            "sameProviderResumeActive",
            "rootPredictionLeadMs",
            "inferenceDroppedFreshCount",
            "landmarkRefinedPointCount",
            "landmarkIsolatedRejectCount",
            "landmarkIsolatedCandidateCount",
            "landmarkMassRejectBypass",
            "commercialFailoverDeferred",
            "writerAuditRenderBoundaryObserved",
            "faceTextureSemanticTimestamp",
            "runnerSubmissionHz",
            "canonicalAdoptionHz",
            "visualMovedWithoutRoot"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
            "KiwiFaceAttachmentRecalibration.cs",
            "KIWI_V5_1_PHASE16_8_ATTACHMENT_STABILITY"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Validation/" +
            "KiwiCommercialTransformWriterAudit.cs",
            "KIWI_V5_1_PHASE16_8_TRANSFORM_WRITER_AUDIT",
            "KIWI_V5_1_PHASE16_9_WRITER_AUDIT_COMPLETED_FRAME_LATCH"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Validation/" +
            "KiwiPhase16_13PresentationDiagnostics.cs",
            "KIWI_V5_1_PHASE16_13_STATIC_REST_DIAGNOSTICS",
            "KIWI_V5_1_PHASE16_14_RENDER_BOUNDARY_FRESH_ONLY_DIAGNOSTICS"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Validation/" +
            "KiwiPhase16_15RootContinuityDiagnostics.cs",
            "KIWI_V5_1_PHASE16_15_ROOT_CONTINUITY_DIAGNOSTICS",
            "RootContinuityCapApplied",
            "SameProviderResumeActive"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Validation/" +
            "KiwiPhase16_16ProviderBridgeDiagnostics.cs",
            "KIWI_V5_1_PHASE16_16_ROOT_SPACE_PROVIDER_BRIDGE_DIAGNOSTICS",
            "AppliedPositionDelta",
            "MotionProgress"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Validation/" +
            "KiwiPhase16_17PresentationAuthorityDiagnostics.cs",
            "KIWI_V5_1_PHASE16_17_SINGLE_PRESENTATION_AUTHORITY_DIAGNOSTICS",
            "SinglePresentationAuthority",
            "LegacyWriteViolationCount"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Validation/" +
            "KiwiPhase16_18HandoffAuthorityDiagnostics.cs",
            "KIWI_V5_1_PHASE16_18_SINGLE_HANDOFF_AUTHORITY_DIAGNOSTICS",
            "SingleHandoffAuthority",
            "LocalBridgeSuppressedCount",
            "AuthorityViolationCount"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Validation/" +
            "KiwiCommercialCadencePipelineTelemetry.cs",
            "KIWI_V5_1_PHASE16_9_COMMERCIAL_CADENCE_PIPELINE_TELEMETRY",
            "KIWI_V5_1_PHASE16_10_INFERENCE_PIPELINE_TELEMETRY",
            "KIWI_V5_1_PHASE16_11_FRESHNESS_AGE_TELEMETRY",
            "KIWI_V5_1_PHASE16_12_PERSISTENT_ROI_TELEMETRY",
            "KIWI_V5_1_PHASE16_13_LATENCY_FIRST_TELEMETRY",
            "KIWI_V5_1_PHASE16_14_STABLE_PIPELINE_TELEMETRY",
            "InferenceScheduleDelayMs",
            "InferenceRawPresenceLogit",
            "InferenceRegionRetentionActive",
            "InferenceTrackingHealthy",
            "InferenceGpuReadbackWaitMs",
            "InferenceAcceptedSourceAgeMs",
            "InferenceDiscardedStaleAnchorCount",
            "InferenceLaneLimit",
            "InferenceLatencyFirstSchedulingActive",
            "InferenceStableDesktopSchedulingActive",
            "InferenceCompletionIntervalMs",
            "InferenceDropRatio"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Presentation/" +
            "KiwiFacePartTextureTransaction.cs",
            "KIWI_V5_1_PHASE16_9_FACEPART_TEXTURE_TRANSACTION"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Editor/" +
            "KiwiPhase16_13StaticRestPresentationMigration.cs",
            "KIWI_V5_1_PHASE16_13_STATIC_REST_PRESENTATION",
            "KIWI_V5_1_PHASE16_13_BEFORE_RENDER_REST_DEDUP"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Editor/" +
            "KiwiPhase16_14RenderBoundaryContinuityMigration.cs",
            "KIWI_V5_1_PHASE16_14_RENDER_BOUNDARY_FRESH_ONLY"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Editor/" +
            "KiwiPhase16_15RootContinuityMigration.cs",
            "KIWI_V5_1_PHASE16_15_NO_FRAME_HOLD_RESUME_ENVELOPE"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Editor/" +
            "KiwiPhase16_16ProviderBridgeMigration.cs",
            "KIWI_V5_1_PHASE16_16_ROOT_SPACE_PROVIDER_BRIDGE"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Editor/" +
            "KiwiPhase16_17SinglePresentationAuthorityMigration.cs",
            "KIWI_V5_1_PHASE16_17_SINGLE_PRESENTATION_AUTHORITY"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Editor/" +
            "KiwiPhase16_18SingleHandoffAuthorityMigration.cs",
            "KIWI_V5_1_PHASE16_18_SINGLE_HANDOFF_AUTHORITY"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Editor/" +
            "KiwiFacePartTextureTransactionMigration.cs",
            "KIWI_V5_1_PHASE16_9_FACEPART_TEXTURE_TRANSACTION_MIGRATION"),
        new MarkerRule(
            "Assets/Script/FacePartCropper.cs",
            "KIWI_PRESENTATION_FPS_OWNER_V3_7",
            "KIWI_V4_8_SEMANTIC_FRESHNESS_GATE",
            "KIWI_V4_9_PART_TRANSACTION_REPORT",
            "KIWI_V5_1_PHASE5_CANONICAL_CROPPER_FRAME",
            "KIWI_V5_1_PHASE16_9_MATCHED_FACEPART_TEXTURE_WRITER",
            "KIWI_V5_1_PHASE16_9_TEXTURE_SEMANTIC_GATE",
            "KIWI_V5_1_PHASE16_9_TEXTURE_TRANSACTION_COMMIT"),
        new MarkerRule(
            "Assets/Script/FacePartShapeMask.cs",
            "KIWI_V4_8_MASK_SEMANTIC_FRESHNESS_GATE",
            "KIWI_V4_9_PART_TRANSACTION_GATE",
            "KIWI_V5_1_PHASE4_SHAPEMASK_CANVAS_RELEASE",
            "KIWI_V5_1_PHASE5_CANONICAL_MASK_FRAME"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
            "KiwiFacePartQualityCoordinator.cs",
            "KIWI_FACE_PART_VISIBILITY_LATCH_FIX_V3_3",
            "KIWI_V5_1_PHASE4_PRESENTATION_ARBITRATION"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
            "KiwiTrackingQuality10Controller.cs",
            "KIWI_V5_1_RUNTIME_POLICY_SINGLE_WRITER",
            "KIWI_V5_1_PHASE16_17_SINGLE_PRESENTATION_AUTHORITY",
            "phase16_17PolicyOnlyPresentation",
            "ReportSuppressedBeforeRender"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimeManager.cs",
            "KIWI_V5_1_MODEL_GENERATION_COMMIT"),
        new MarkerRule(
            "Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs",
            "KiwiAvatarSystem v4.5.4: compatibility load restored; " +
            "legacy tracking UI retired")
    };

    private static readonly PackageRule[] PackageRules =
    {
        new PackageRule(
            "com.github.homuler.mediapipe",
            "0.16.3",
            true),
        new PackageRule(
            "com.unity.ai.inference",
            "2.4.1",
            true),
        new PackageRule(
            "com.unity.mathematics",
            "1.3.3",
            false),
        new PackageRule(
            "com.unity.ugui",
            "1.0.0",
            false),
        new PackageRule(
            "jp.keijiro.klak.spout",
            "2.0.6",
            false)
    };

    // KIWI_V5_1_PHASE14_EXTERNAL_LOCAL_PACKAGE_POLICY
    // These large package artifacts are intentionally kept outside Git.
    // Their manifest file: source may therefore be absent from a clone/overlay.
    // This NEVER waives package resolution: CheckRequiredPackages still requires
    // Unity Package Manager to have loaded the exact required package/version.
    private static readonly HashSet<string>
        ExternallySuppliedLocalPackages =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "com.github.homuler.mediapipe"
            };

    private static PreflightReport _lastReport;
    private static bool _bypassPlayGateOnce;
    private static bool _runningFullPreflight;

    static KiwiReleaseCandidatePreflight()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    public static PreflightReport LastReport => _lastReport;

    public static bool PlayGateEnabled
    {
        get => EditorPrefs.GetBool(GetPlayGatePrefKey(), false);
        set => EditorPrefs.SetBool(GetPlayGatePrefKey(), value);
    }

    [MenuItem(MenuRoot + "Run Full Preflight", priority = 1)]
    private static void RunFullPreflightFromMenu()
    {
        RunFullPreflight(true);
    }

    [MenuItem(MenuRoot + "Log Last Report", priority = 20)]
    private static void LogLastReport()
    {
        if (_lastReport == null)
        {
            Debug.LogWarning(
                "[KiwiPreflight] No Phase 16.1 preflight has been run in this " +
                "Editor domain yet.");
            return;
        }

        Debug.Log(
            "[KiwiPreflight] Last report:\n" +
            JsonUtility.ToJson(_lastReport, true));
    }

    [MenuItem(MenuRoot + "Export Last Report JSON", priority = 21)]
    private static void ExportLastReport()
    {
        if (_lastReport == null)
        {
            _lastReport = RunFullPreflight(false);
        }

        string path = Path.Combine(
            Application.persistentDataPath,
            ExportFileName);

        File.WriteAllText(
            path,
            JsonUtility.ToJson(_lastReport, true),
            new UTF8Encoding(false));

        Debug.Log(
            "[KiwiPreflight] Report exported: " + path);
    }

    [MenuItem(PlayGateMenu, priority = 40)]
    private static void TogglePlayGate()
    {
        PlayGateEnabled = !PlayGateEnabled;
        Menu.SetChecked(PlayGateMenu, PlayGateEnabled);
        Debug.Log(
            "[KiwiPreflight] Phase 16.1 Strict Play Gate " +
            (PlayGateEnabled
                ? "enabled. Play requires a current passing preflight stamp."
                : "disabled. Diagnostic Play is allowed; RC/Release build gates remain strict."));
    }

    [MenuItem(PlayGateMenu, true)]
    private static bool ValidatePlayGateMenu()
    {
        Menu.SetChecked(PlayGateMenu, PlayGateEnabled);
        return true;
    }

    [MenuItem(MenuRoot + "Bypass Play Gate Once", priority = 41)]
    private static void BypassPlayGateOnce()
    {
        _bypassPlayGateOnce = true;
        Debug.LogWarning(
            "[KiwiPreflight] The Phase 16.1 Strict Play Gate will be bypassed for the " +
            "next Play attempt only. This does not create a passing RC stamp.");
    }

    [MenuItem(MenuRoot + "Clear Passing Stamp", priority = 60)]
    private static void ClearPassingStamp()
    {
        string path = GetStampPath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Debug.Log(
            "[KiwiPreflight] Phase 16.1 passing stamp cleared.");
    }

    public static PreflightReport RunFullPreflight(bool logResult)
    {
        if (_runningFullPreflight)
        {
            return _lastReport;
        }

        _runningFullPreflight = true;

        try
        {
            List<PreflightIssue> issues =
                new List<PreflightIssue>();

            List<string> releaseScenes =
                CollectReleaseScenePaths(issues);

            PreflightReport report =
                new PreflightReport
                {
                    version = PreflightVersion,
                    baseCommit = BaseCommit,
                    unityVersion = Application.unityVersion,
                    generatedUtc = DateTime.UtcNow.ToString("O"),
                    releaseScenes = releaseScenes.ToArray()
                };

            CheckEditorState(issues);
            CheckUnityVersion(issues);
            CheckRequiredPackages(issues);
            CheckManifestPackageSources(issues);
            CheckUniVrm(issues);
            CheckRequiredResources(issues);
            CheckMigrationMarkers(issues);
            CheckLegacyWriterRetirement(issues);
            CheckStaticSingleWriterContracts(issues);
            CheckDeterministicAcceptanceMatrix(issues);
            ScanReleaseScenes(
                releaseScenes,
                issues,
                report);

            report.fingerprint =
                ComputeProjectFingerprint(releaseScenes);

            FinalizeReport(report, issues);
            _lastReport = report;

            if (report.passed)
            {
                SavePassingStamp(report);
            }
            else
            {
                DeletePassingStamp();
            }

            if (logResult)
            {
                LogReportSummary(report);
            }

            return report;
        }
        finally
        {
            _runningFullPreflight = false;
        }
    }

    public static bool HasCurrentPassingStamp(
        out string reason)
    {
        reason = string.Empty;

        List<PreflightIssue> scratch =
            new List<PreflightIssue>();
        List<string> releaseScenes =
            CollectReleaseScenePaths(scratch);

        if (HasDirtyLoadedScene(out string dirtyScene))
        {
            reason =
                "Loaded scene has unsaved changes: " + dirtyScene;
            return false;
        }

        PassingStamp stamp =
            LoadPassingStamp();

        if (
            stamp == null ||
            string.IsNullOrEmpty(stamp.fingerprint)
        )
        {
            reason = "No passing Phase 16.1 full-preflight stamp exists.";
            return false;
        }

        if (!string.Equals(
                stamp.version,
                PreflightVersion,
                StringComparison.Ordinal))
        {
            reason =
                "Passing stamp belongs to " + stamp.version +
                ", not " + PreflightVersion + ".";
            return false;
        }

        string currentFingerprint =
            ComputeProjectFingerprint(releaseScenes);

        if (!string.Equals(
                currentFingerprint,
                stamp.fingerprint,
                StringComparison.Ordinal))
        {
            reason =
                "Saved project/scene/package fingerprint changed since the " +
                "last passing full preflight.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Phase 15 read-only bridge used by the final RC gate. The fingerprint is
    /// returned only when the saved project still matches a current passing
    /// preflight stamp.
    /// </summary>
    public static bool TryGetCurrentPassingFingerprint(
        out string fingerprint,
        out string reason)
    {
        fingerprint = string.Empty;

        if (!HasCurrentPassingStamp(out reason))
        {
            return false;
        }

        PassingStamp stamp = LoadPassingStamp();
        if (stamp == null || string.IsNullOrEmpty(stamp.fingerprint))
        {
            reason = "Passing preflight stamp has no fingerprint.";
            return false;
        }

        fingerprint = stamp.fingerprint;
        reason = string.Empty;
        return true;
    }

    private static void OnPlayModeStateChanged(
        PlayModeStateChange state)
    {
        if (
            state != PlayModeStateChange.ExitingEditMode ||
            !PlayGateEnabled
        )
        {
            return;
        }

        if (_bypassPlayGateOnce)
        {
            _bypassPlayGateOnce = false;
            return;
        }

        if (HasCurrentPassingStamp(out string reason))
        {
            return;
        }

        // Cancel this transition before opening any additional scenes for the
        // full RC scan. The operator presses Play again after a passing scan.
        EditorApplication.isPlaying = false;

        Debug.LogError(
            "[KiwiPreflight] Play Mode blocked by Phase 16.1 Strict RC gate: " +
            reason +
            " A full preflight will run now. Press Play again only after PASS, " +
            "or disable 'Release Candidate Preflight/Play Gate Enabled' for diagnostic camera/Landmark testing. " +
            "Disabling this Play gate does not weaken the RC/Release build gate.");

        EditorApplication.delayCall +=
            () =>
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    RunFullPreflight(true);
                }
            };
    }

    private static void CheckEditorState(
        List<PreflightIssue> issues)
    {
        if (EditorApplication.isCompiling)
        {
            Add(
                issues,
                "Critical",
                "Editor.Compiling",
                "Unity is still compiling scripts. Wait for compilation to " +
                "finish before the RC preflight.");
        }

        if (EditorApplication.isUpdating)
        {
            Add(
                issues,
                "Error",
                "Editor.AssetDatabaseUpdating",
                "AssetDatabase is still updating/importing. Run the preflight " +
                "again after imports finish.");
        }

        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Add(
                issues,
                "Critical",
                "Editor.PlayTransition",
                "Full RC preflight must run from stable Edit Mode.");
        }

        if (HasDirtyLoadedScene(out string dirtyScene))
        {
            Add(
                issues,
                "Error",
                "Scene.UnsavedChanges",
                "Save all loaded scenes before creating an RC passing stamp.",
                dirtyScene);
        }
    }

    private static void CheckUnityVersion(
        List<PreflightIssue> issues)
    {
        if (!string.Equals(
                Application.unityVersion,
                ExpectedUnityVersion,
                StringComparison.Ordinal))
        {
            Add(
                issues,
                "Error",
                "Environment.UnityVersion",
                "Expected Unity " + ExpectedUnityVersion +
                " for this RC baseline, but Editor is " +
                Application.unityVersion + ".");
        }

        if (!SystemInfo.supportsComputeShaders)
        {
            Add(
                issues,
                "Critical",
                "Environment.ComputeShaders",
                "Compute shader support is unavailable. Inference Engine and " +
                "Live2D block matching require compute support on the RC path.");
        }

        if (
            EditorUserBuildSettings.activeBuildTarget !=
            BuildTarget.StandaloneWindows64
        )
        {
            Add(
                issues,
                "Warning",
                "Environment.BuildTarget",
                "Windows is the primary RC target, but the active build target " +
                "is " + EditorUserBuildSettings.activeBuildTarget + ".");
        }
    }

    private static void CheckRequiredPackages(
        List<PreflightIssue> issues)
    {
        UnityEditor.PackageManager.PackageInfo[] packages =
            UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();

        Dictionary<string, UnityEditor.PackageManager.PackageInfo> byName =
            packages
                .Where(p => p != null && !string.IsNullOrEmpty(p.name))
                .GroupBy(p => p.name, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.First(),
                    StringComparer.Ordinal);

        foreach (PackageRule rule in PackageRules)
        {
            if (!byName.TryGetValue(rule.name, out UnityEditor.PackageManager.PackageInfo package))
            {
                Add(
                    issues,
                    "Critical",
                    "Package.Missing",
                    "Required package is not resolved: " + rule.name +
                    " (expected " + rule.expectedVersion + ").",
                    "Packages/manifest.json");
                continue;
            }

            if (
                rule.versionMustMatch &&
                !string.Equals(
                    package.version,
                    rule.expectedVersion,
                    StringComparison.OrdinalIgnoreCase)
            )
            {
                Add(
                    issues,
                    "Error",
                    "Package.VersionMismatch",
                    rule.name + " resolved as " + package.version +
                    ", expected " + rule.expectedVersion + ".",
                    "Packages/manifest.json");
            }
            else if (
                !rule.versionMustMatch &&
                !string.IsNullOrEmpty(rule.expectedVersion) &&
                !string.Equals(
                    package.version,
                    rule.expectedVersion,
                    StringComparison.OrdinalIgnoreCase)
            )
            {
                Add(
                    issues,
                    "Warning",
                    "Package.VersionDrift",
                    rule.name + " resolved as " + package.version +
                    "; current main baseline uses " +
                    rule.expectedVersion + ".",
                    "Packages/manifest.json");
            }
        }
    }

    private static void CheckManifestPackageSources(
        List<PreflightIssue> issues)
    {
        string manifestPath =
            AssetPathToFullPath("Packages/manifest.json");

        if (!File.Exists(manifestPath))
        {
            Add(
                issues,
                "Critical",
                "Package.ManifestMissing",
                "Packages/manifest.json is missing.",
                "Packages/manifest.json");
            return;
        }

        string manifest = File.ReadAllText(manifestPath);
        var matches = System.Text.RegularExpressions.Regex.Matches(
            manifest,
            "\\\"([^\\\"]+)\\\"\\s*:\\s*\\\"(file:[^\\\"]+)\\\"");

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            string packageName = match.Groups[1].Value;
            string source = match.Groups[2].Value;
            string relative = Uri.UnescapeDataString(
                source.Substring("file:".Length));

            if (string.IsNullOrWhiteSpace(relative))
            {
                Add(
                    issues,
                    "Critical",
                    "Package.LocalSourceInvalid",
                    "Local file package source is empty: " + packageName,
                    "Packages/manifest.json");
                continue;
            }

            string projectRoot = GetProjectRoot();
            string normalized = relative.Replace(
                '/',
                Path.DirectorySeparatorChar);

            string candidateProject =
                Path.GetFullPath(
                    Path.IsPathRooted(normalized)
                        ? normalized
                        : Path.Combine(projectRoot, normalized));

            string candidatePackages =
                Path.GetFullPath(
                    Path.IsPathRooted(normalized)
                        ? normalized
                        : Path.Combine(
                            projectRoot,
                            "Packages",
                            normalized));

            if (
                !File.Exists(candidateProject) &&
                !Directory.Exists(candidateProject) &&
                !File.Exists(candidatePackages) &&
                !Directory.Exists(candidatePackages)
            )
            {
                if (ExternallySuppliedLocalPackages.Contains(packageName))
                {
                    // KIWI_V5_1_PHASE14_EXTERNAL_LOCAL_PACKAGE_POLICY
                    // The repository/overlay intentionally does not carry this
                    // large local artifact. Package resolution/version is still
                    // enforced by CheckRequiredPackages above. If the user's
                    // Unity project has not supplied it, compilation/package
                    // registration cannot satisfy the RC gate.
                    Add(
                        issues,
                        "Warning",
                        "Package.ExternalLocalSourceNotBundled",
                        packageName + " uses " + source +
                        ", whose artifact is intentionally not bundled in Git/" +
                        "the cumulative overlay. This is allowed only because " +
                        "Unity has already resolved the required package/version.",
                        "Packages/manifest.json");
                }
                else
                {
                    Add(
                        issues,
                        "Critical",
                        "Package.LocalSourceMissing",
                        packageName + " uses " + source +
                        ", but the referenced local package source does not exist " +
                        "and is not approved as an externally supplied dependency.",
                        "Packages/manifest.json");
                }
            }
        }
    }

    private static void CheckUniVrm(
        List<PreflightIssue> issues)
    {
        var assemblies =
            AppDomain.CurrentDomain.GetAssemblies();

        bool hasUniGltf = assemblies.Any(
            assembly =>
                assembly.GetName().Name.IndexOf(
                    "UniGLTF",
                    StringComparison.OrdinalIgnoreCase) >= 0);

        bool hasVrm = assemblies.Any(
            assembly =>
            {
                string name = assembly.GetName().Name;
                return
                    name.IndexOf(
                        "UniVRM",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(
                        name,
                        "VRM",
                        StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(
                        "VRM.",
                        StringComparison.OrdinalIgnoreCase);
            });

        if (!hasUniGltf || !hasVrm)
        {
            Add(
                issues,
                "Critical",
                "Dependency.UniVRM",
                "UniVRM/UniGLTF assemblies are not loaded. VRM 0.x runtime " +
                "import/model switching cannot be treated as RC-ready.");
            return;
        }

        UnityEditor.PackageManager.PackageInfo[] packages =
            UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();

        UnityEditor.PackageManager.PackageInfo vrmPackage =
            packages.FirstOrDefault(
                package =>
                    package != null &&
                    (
                        (!string.IsNullOrEmpty(package.name) &&
                         package.name.IndexOf(
                             "vrm",
                             StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(package.displayName) &&
                         package.displayName.IndexOf(
                             "UniVRM",
                             StringComparison.OrdinalIgnoreCase) >= 0)
                    ));

        if (vrmPackage != null)
        {
            if (!string.Equals(
                    vrmPackage.version,
                    "0.130.1",
                    StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    issues,
                    "Warning",
                    "Dependency.UniVRMVersion",
                    "UniVRM package version is " + vrmPackage.version +
                    "; the current project baseline is 0.130.1.");
            }
        }
        else
        {
            Add(
                issues,
                "Warning",
                "Dependency.UniVRMVersionUnverified",
                "UniVRM/UniGLTF assemblies are loaded, but PackageManager " +
                "does not expose a UniVRM package version. Confirm 0.130.1 " +
                "during the RC environment check.");
        }
    }

    private static void CheckRequiredResources(
        List<PreflightIssue> issues)
    {
        CheckResource<ComputeShader>(
            issues,
            "KiwiFacePartBlockMatch",
            "Resource.Live2DBlockMatch");

        CheckResource<Shader>(
            issues,
            "KiwiInferenceFaceCrop",
            "Resource.InferenceCropShader");

        CheckResource<UnityEngine.Object>(
            issues,
            "KiwiFaceLandmarkInference",
            "Resource.InferenceModel");
    }

    private static void CheckResource<T>(
        List<PreflightIssue> issues,
        string resourceName,
        string code)
        where T : UnityEngine.Object
    {
        T resource = Resources.Load<T>(resourceName);
        if (resource == null)
        {
            Add(
                issues,
                "Critical",
                code,
                "Resources.Load could not resolve required runtime asset '" +
                resourceName + "'. Check import status, package support, and " +
                "Git LFS/model payloads.");
        }
    }

    private static void CheckMigrationMarkers(
        List<PreflightIssue> issues)
    {
        foreach (MarkerRule rule in MarkerRules)
        {
            string fullPath =
                AssetPathToFullPath(rule.path);

            if (!File.Exists(fullPath))
            {
                Add(
                    issues,
                    "Critical",
                    "Migration.TargetMissing",
                    "Required migration target source is missing.",
                    rule.path);
                continue;
            }

            string source = File.ReadAllText(fullPath);

            foreach (string marker in rule.markers)
            {
                if (source.IndexOf(
                        marker,
                        StringComparison.Ordinal) < 0)
                {
                    Add(
                        issues,
                        "Critical",
                        "Migration.MarkerMissing",
                        "Required final-state migration marker is missing: " +
                        marker,
                        rule.path);
                }
            }
        }
    }

    private static void CheckLegacyWriterRetirement(
        List<PreflightIssue> issues)
    {
        CheckActiveLineAbsent(
            issues,
            "Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs",
            "DrawTrackingControls(buttonHeight);",
            "Legacy.RuntimePanelTrackingWriter",
            "Legacy Tracking controls are still on the runtime call path.");

        CheckActiveLineAbsent(
            issues,
            "Assets/KiwiAvatarSystem/Runtime/KiwiAvatarRuntimePanel.cs",
            "RefreshTrackingDiagnostics();",
            "Legacy.RuntimePanelTrackingDiagnostics",
            "Legacy Tracking diagnostics are still on the runtime call path.");

        string quality10Path =
            "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
            "KiwiTrackingQuality10Controller.cs";

        string[] forbiddenQualityWrites =
        {
            "runner.trackingInputMaxWidth =",
            "runner.sentisMediaPipeRefreshRateHz =",
            "runner.sentisMinimumPresence ="
        };

        foreach (string token in forbiddenQualityWrites)
        {
            CheckActiveLineAbsent(
                issues,
                quality10Path,
                token,
                "Legacy.Quality10DirectWriter",
                "Quality10 still writes a RuntimePolicyResolver-owned field " +
                "directly: " + token);
        }

        CheckActiveLineAbsent(
            issues,
            "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
            "KiwiFacePartLiveMotionBridge.cs",
            "AdvanceCameraGeneration(",
            "Legacy.ConsumerCameraGenerationOwner",
            "Live2D presentation code must not advance global CameraGeneration.");
    }

    private static void CheckStaticSingleWriterContracts(
        List<PreflightIssue> issues)
    {
        List<string> runtimeFiles =
            EnumerateProjectRuntimeCsFiles();

        List<string> alphaWriters =
            new List<string>();

        foreach (string assetPath in runtimeFiles)
        {
            string fullPath = AssetPathToFullPath(assetPath);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(fullPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (
                    trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.Length == 0
                )
                {
                    continue;
                }

                if (
                    trimmed.IndexOf(
                        ".SetAlpha(",
                        StringComparison.Ordinal) >= 0 &&
                    (
                        trimmed.IndexOf(
                            "canvasRenderer",
                            StringComparison.OrdinalIgnoreCase) >= 0 ||
                        trimmed.IndexOf(
                            "CanvasRenderer",
                            StringComparison.Ordinal) >= 0
                    )
                )
                {
                    alphaWriters.Add(
                        assetPath + ":" + (i + 1));
                }
            }
        }

        bool alphaContractOk =
            alphaWriters.Count == 1 &&
            alphaWriters[0].StartsWith(
                "Assets/KiwiAvatarSystem/Runtime/Optimization/" +
                "KiwiFacePartPresentationResolver.cs:",
                StringComparison.Ordinal);

        if (!alphaContractOk)
        {
            Add(
                issues,
                "Critical",
                "Ownership.CanvasAlphaWriter",
                "Runtime CanvasRenderer alpha must have exactly one writer " +
                "(KiwiFacePartPresentationResolver). Found: " +
                (alphaWriters.Count == 0
                    ? "none"
                    : string.Join(", ", alphaWriters)));
        }
    }

    private static void CheckDeterministicAcceptanceMatrix(
        List<PreflightIssue> issues)
    {
        bool passed =
            KiwiFaultInjectionAcceptanceHarness.RunDeterministicMatrix(
                out string report);

        if (!passed)
        {
            Add(
                issues,
                "Critical",
                "Acceptance.DeterministicMatrix",
                "Phase 12 deterministic acceptance matrix failed: " + report);
        }
    }

    private static void ScanReleaseScenes(
        List<string> scenePaths,
        List<PreflightIssue> issues,
        PreflightReport report)
    {
        if (scenePaths.Count == 0)
        {
            Add(
                issues,
                "Critical",
                "Scene.NoReleaseScene",
                "No saved release scene could be resolved from Build Settings " +
                "or the currently loaded scenes.");
            return;
        }

        Dictionary<string, int> aggregateRoles =
            new Dictionary<string, int>(StringComparer.Ordinal);

        Dictionary<string, int> loadedSetRoles =
            new Dictionary<string, int>(StringComparer.Ordinal);

        Scene activeBefore = SceneManager.GetActiveScene();
        string activePathBefore =
            activeBefore.IsValid() ? activeBefore.path : string.Empty;

        foreach (string scenePath in scenePaths)
        {
            if (string.IsNullOrEmpty(scenePath))
            {
                continue;
            }

            if (!File.Exists(AssetPathToFullPath(scenePath)))
            {
                Add(
                    issues,
                    "Critical",
                    "Scene.AssetMissing",
                    "Release scene path does not exist.",
                    scenePath);
                continue;
            }

            Scene scene = SceneManager.GetSceneByPath(scenePath);
            bool wasLoaded = scene.IsValid() && scene.isLoaded;

            try
            {
                if (!wasLoaded)
                {
                    scene = EditorSceneManager.OpenScene(
                        scenePath,
                        OpenSceneMode.Additive);
                }

                SceneScanSummary summary =
                    ScanScene(scene, issues);

                report.scenesScanned++;
                report.objectsScanned += summary.objects;
                report.componentsScanned += summary.components;

                foreach (var pair in summary.roleCounts)
                {
                    aggregateRoles.TryGetValue(
                        pair.Key,
                        out int existing);
                    aggregateRoles[pair.Key] = existing + pair.Value;
                }

                if (wasLoaded)
                {
                    foreach (var pair in summary.roleCounts)
                    {
                        loadedSetRoles.TryGetValue(
                            pair.Key,
                            out int existing);
                        loadedSetRoles[pair.Key] = existing + pair.Value;
                    }
                }

                bool hasAnyCoreRole =
                    RequiredCoreRoleNames.Any(
                        role =>
                        {
                            summary.roleCounts.TryGetValue(role, out int count);
                            return count > 0;
                        });

                bool hasAllCoreRoles =
                    RequiredCoreRoleNames.All(
                        role =>
                        {
                            summary.roleCounts.TryGetValue(role, out int count);
                            return count == 1;
                        });

                if (hasAnyCoreRole && !hasAllCoreRoles)
                {
                    Add(
                        issues,
                        "Error",
                        "Scene.CorePipelineSplit",
                        "A release scene contains only part of the required " +
                        "Runner / FaceMotion / FacePartCropper core pipeline. " +
                        "Keep the authoritative core together in the scene or " +
                        "remove stale partial owners.",
                        scenePath);
                }

                CheckPerSceneRoleDuplicates(
                    scenePath,
                    summary,
                    issues);

                CheckSceneCrossReferences(
                    scenePath,
                    summary,
                    issues);
            }
            catch (Exception exception)
            {
                Add(
                    issues,
                    "Critical",
                    "Scene.ScanException",
                    "Scene scan failed: " + exception.Message,
                    scenePath);
            }
            finally
            {
                if (!wasLoaded && scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        if (!string.IsNullOrEmpty(activePathBefore))
        {
            Scene activeRestore =
                SceneManager.GetSceneByPath(activePathBefore);
            if (activeRestore.IsValid() && activeRestore.isLoaded)
            {
                SceneManager.SetActiveScene(activeRestore);
            }
        }

        foreach (string role in RequiredCoreRoleNames)
        {
            aggregateRoles.TryGetValue(role, out int count);
            if (count == 0)
            {
                Add(
                    issues,
                    "Error",
                    "Scene.RequiredRoleMissing",
                    "No release scene contains required role: " + role);
            }
        }

        foreach (string role in SingletonRoleNames)
        {
            loadedSetRoles.TryGetValue(role, out int loadedCount);
            if (loadedCount > 1)
            {
                Add(
                    issues,
                    "Critical",
                    "Scene.LoadedDuplicateRole",
                    "Currently loaded scene set contains duplicate role owner " +
                    role + "=" + loadedCount + ".");
            }
        }

        bool hasExpectedPrimary =
            scenePaths.Any(
                path =>
                    string.Equals(
                        Path.GetFileNameWithoutExtension(path),
                        ExpectedPrimarySceneName,
                        StringComparison.Ordinal));

        if (!hasExpectedPrimary)
        {
            Add(
                issues,
                "Warning",
                "Scene.PrimaryNameDrift",
                "The current baseline uses scene '" +
                ExpectedPrimarySceneName +
                "', but it is not in the release-scene scan set.");
        }
    }

    private static SceneScanSummary ScanScene(
        Scene scene,
        List<PreflightIssue> issues)
    {
        SceneScanSummary summary =
            new SceneScanSummary();

        if (!scene.IsValid() || !scene.isLoaded)
        {
            return summary;
        }

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            ScanGameObjectRecursive(
                root.transform,
                scene.path,
                summary,
                issues);
        }

        return summary;
    }

    private static void ScanGameObjectRecursive(
        Transform transform,
        string scenePath,
        SceneScanSummary summary,
        List<PreflightIssue> issues)
    {
        if (transform == null)
        {
            return;
        }

        GameObject gameObject = transform.gameObject;
        summary.objects++;

        int missingScriptCount =
            GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(
                gameObject);

        if (missingScriptCount > 0)
        {
            Add(
                issues,
                "Critical",
                "Scene.MissingScript",
                "GameObject contains " + missingScriptCount +
                " missing MonoBehaviour script(s).",
                scenePath,
                GetHierarchyPath(transform));
        }

        MonoBehaviour[] behaviours =
            gameObject.GetComponents<MonoBehaviour>();

        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour = behaviours[i];

            if (behaviour == null)
            {
                continue;
            }

            summary.components++;

            string roleName = behaviour.GetType().Name;
            summary.roleCounts.TryGetValue(roleName, out int roleCount);
            summary.roleCounts[roleName] = roleCount + 1;

            if (!summary.roleInstances.TryGetValue(
                    roleName,
                    out List<MonoBehaviour> roleList))
            {
                roleList = new List<MonoBehaviour>();
                summary.roleInstances[roleName] = roleList;
            }
            roleList.Add(behaviour);

            ScanBrokenSerializedReferences(
                behaviour,
                scenePath,
                transform,
                issues);

            ScanRequiredInspectorReferences(
                behaviour,
                scenePath,
                transform,
                issues);
        }

        for (int child = 0; child < transform.childCount; child++)
        {
            ScanGameObjectRecursive(
                transform.GetChild(child),
                scenePath,
                summary,
                issues);
        }
    }

    private static void ScanBrokenSerializedReferences(
        MonoBehaviour behaviour,
        string scenePath,
        Transform transform,
        List<PreflightIssue> issues)
    {
        try
        {
            SerializedObject serialized =
                new SerializedObject(behaviour);
            SerializedProperty property =
                serialized.GetIterator();

            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;

                if (
                    property.propertyType ==
                        SerializedPropertyType.ObjectReference &&
                    property.objectReferenceValue == null &&
                    property.objectReferenceInstanceIDValue != 0
                )
                {
                    Add(
                        issues,
                        "Critical",
                        "Scene.BrokenObjectReference",
                        behaviour.GetType().Name + "." +
                        property.propertyPath +
                        " points to a missing object/asset reference.",
                        scenePath,
                        GetHierarchyPath(transform));
                }
            }
        }
        catch (Exception exception)
        {
            Add(
                issues,
                "Warning",
                "Scene.SerializedReferenceScan",
                "Could not inspect serialized references on " +
                behaviour.GetType().Name + ": " + exception.Message,
                scenePath,
                GetHierarchyPath(transform));
        }
    }

    private static void ScanRequiredInspectorReferences(
        MonoBehaviour behaviour,
        string scenePath,
        Transform transform,
        List<PreflightIssue> issues)
    {
        string typeName = behaviour.GetType().Name;

        if (typeName == "FaceLandmarkerRunner")
        {
            RequireObjectReference(
                behaviour,
                "_faceLandmarkerResultAnnotationController",
                scenePath,
                transform,
                issues);
        }
        else if (typeName == "KiwiFaceMotion")
        {
            RequireObjectReference(
                behaviour,
                "runner",
                scenePath,
                transform,
                issues);
        }
        else if (typeName == "FacePartCropper")
        {
            RequireObjectReference(
                behaviour,
                "runner",
                scenePath,
                transform,
                issues);
            RequireObjectReference(
                behaviour,
                "sourceImage",
                scenePath,
                transform,
                issues);
            RequireObjectReference(
                behaviour,
                "leftEyeImage",
                scenePath,
                transform,
                issues);
            RequireObjectReference(
                behaviour,
                "rightEyeImage",
                scenePath,
                transform,
                issues);
            RequireObjectReference(
                behaviour,
                "mouthImage",
                scenePath,
                transform,
                issues);
        }
    }

    private static void RequireObjectReference(
        MonoBehaviour behaviour,
        string propertyName,
        string scenePath,
        Transform transform,
        List<PreflightIssue> issues)
    {
        SerializedObject serialized =
            new SerializedObject(behaviour);
        SerializedProperty property =
            serialized.FindProperty(propertyName);

        if (property == null)
        {
            Add(
                issues,
                "Error",
                "Scene.RequiredFieldMissing",
                behaviour.GetType().Name +
                " no longer exposes required serialized field '" +
                propertyName + "'.",
                scenePath,
                GetHierarchyPath(transform));
            return;
        }

        if (
            property.propertyType !=
                SerializedPropertyType.ObjectReference
        )
        {
            Add(
                issues,
                "Error",
                "Scene.RequiredFieldTypeChanged",
                behaviour.GetType().Name + "." + propertyName +
                " is no longer an ObjectReference.",
                scenePath,
                GetHierarchyPath(transform));
            return;
        }

        if (property.objectReferenceValue == null)
        {
            Add(
                issues,
                "Error",
                "Scene.RequiredReferenceNull",
                behaviour.GetType().Name + "." + propertyName +
                " is not assigned.",
                scenePath,
                GetHierarchyPath(transform));
        }
    }

    private static void CheckPerSceneRoleDuplicates(
        string scenePath,
        SceneScanSummary summary,
        List<PreflightIssue> issues)
    {
        foreach (string role in SingletonRoleNames)
        {
            summary.roleCounts.TryGetValue(role, out int count);
            if (count > 1)
            {
                Add(
                    issues,
                    "Critical",
                    "Scene.DuplicateRole",
                    "Scene contains duplicate role owner " +
                    role + "=" + count + ".",
                    scenePath);
            }
        }
    }

    private static void CheckSceneCrossReferences(
        string scenePath,
        SceneScanSummary summary,
        List<PreflightIssue> issues)
    {
        MonoBehaviour runner =
            GetSingleRole(summary, "FaceLandmarkerRunner");
        MonoBehaviour faceMotion =
            GetSingleRole(summary, "KiwiFaceMotion");
        MonoBehaviour cropper =
            GetSingleRole(summary, "FacePartCropper");

        if (runner == null || cropper == null)
        {
            return;
        }

        if (faceMotion != null)
        {
            CheckReferenceIdentity(
                faceMotion,
                "runner",
                runner,
                scenePath,
                issues,
                "FaceMotion and FacePartCropper/Provider stack must share the " +
                "same FaceLandmarkerRunner.");
        }

        CheckReferenceIdentity(
            cropper,
            "runner",
            runner,
            scenePath,
            issues,
            "FacePartCropper must consume the scene's authoritative Runner.");

        SerializedObject cropperSerialized =
            new SerializedObject(cropper);

        string[] outputFields =
        {
            "leftEyeImage",
            "rightEyeImage",
            "mouthImage"
        };

        HashSet<int> outputIds = new HashSet<int>();

        foreach (string outputField in outputFields)
        {
            SerializedProperty property =
                cropperSerialized.FindProperty(outputField);

            if (
                property == null ||
                property.objectReferenceValue == null
            )
            {
                continue;
            }

            UnityEngine.Object output =
                property.objectReferenceValue;

            if (!outputIds.Add(output.GetInstanceID()))
            {
                Add(
                    issues,
                    "Critical",
                    "Scene.FacePartOutputAliased",
                    "FacePartCropper eye/mouth outputs must be three distinct " +
                    "RawImage components.",
                    scenePath,
                    GetHierarchyPath(cropper.transform));
            }

            Component outputComponent = output as Component;
            if (outputComponent != null)
            {
                MonoBehaviour mask =
                    outputComponent.GetComponents<MonoBehaviour>()
                        .FirstOrDefault(
                            component =>
                                component != null &&
                                component.GetType().Name ==
                                    "FacePartShapeMask");

                if (mask == null)
                {
                    Add(
                        issues,
                        "Error",
                        "Scene.FacePartMaskMissing",
                        outputField +
                        " does not have a FacePartShapeMask component.",
                        scenePath,
                        GetHierarchyPath(outputComponent.transform));
                }
            }
        }

        if (summary.roleInstances.TryGetValue(
                "FacePartShapeMask",
                out List<MonoBehaviour> masks))
        {
            foreach (MonoBehaviour mask in masks)
            {
                if (runner != null)
                {
                    CheckReferenceIdentityIfAssigned(
                        mask,
                        "runner",
                        runner,
                        scenePath,
                        issues,
                        "FacePartShapeMask is assigned to a different Runner.");
                }

                if (cropper != null)
                {
                    CheckReferenceIdentityIfAssigned(
                        mask,
                        "cropper",
                        cropper,
                        scenePath,
                        issues,
                        "FacePartShapeMask is assigned to a different Cropper.");
                }

                if (faceMotion != null)
                {
                    CheckReferenceIdentityIfAssigned(
                        mask,
                        "faceMotion",
                        faceMotion,
                        scenePath,
                        issues,
                        "FacePartShapeMask is assigned to a different FaceMotion.");
                }
            }
        }
    }

    private static MonoBehaviour GetSingleRole(
        SceneScanSummary summary,
        string roleName)
    {
        if (
            summary.roleInstances.TryGetValue(
                roleName,
                out List<MonoBehaviour> list) &&
            list.Count == 1
        )
        {
            return list[0];
        }

        return null;
    }

    private static void CheckReferenceIdentity(
        MonoBehaviour owner,
        string propertyName,
        UnityEngine.Object expected,
        string scenePath,
        List<PreflightIssue> issues,
        string message)
    {
        SerializedObject serialized =
            new SerializedObject(owner);
        SerializedProperty property =
            serialized.FindProperty(propertyName);

        if (
            property == null ||
            property.propertyType !=
                SerializedPropertyType.ObjectReference ||
            property.objectReferenceValue != expected
        )
        {
            Add(
                issues,
                "Error",
                "Scene.ReferenceIdentity",
                message,
                scenePath,
                GetHierarchyPath(owner.transform));
        }
    }

    private static void CheckReferenceIdentityIfAssigned(
        MonoBehaviour owner,
        string propertyName,
        UnityEngine.Object expected,
        string scenePath,
        List<PreflightIssue> issues,
        string message)
    {
        SerializedObject serialized =
            new SerializedObject(owner);
        SerializedProperty property =
            serialized.FindProperty(propertyName);

        if (
            property == null ||
            property.propertyType !=
                SerializedPropertyType.ObjectReference ||
            property.objectReferenceValue == null
        )
        {
            // FacePartShapeMask explicitly auto-resolves these three references
            // in Start when left unassigned; only conflicting assignments fail.
            return;
        }

        if (property.objectReferenceValue != expected)
        {
            Add(
                issues,
                "Error",
                "Scene.ReferenceIdentity",
                message,
                scenePath,
                GetHierarchyPath(owner.transform));
        }
    }

    private static List<string> CollectReleaseScenePaths(
        List<PreflightIssue> issues)
    {
        HashSet<string> paths =
            new HashSet<string>(StringComparer.Ordinal);

        int enabledBuildSceneCount = 0;

        foreach (EditorBuildSettingsScene buildScene in EditorBuildSettings.scenes)
        {
            if (!buildScene.enabled || string.IsNullOrEmpty(buildScene.path))
            {
                continue;
            }

            enabledBuildSceneCount++;
            paths.Add(buildScene.path);
        }

        if (enabledBuildSceneCount == 0)
        {
            Add(
                issues,
                "Error",
                "Scene.BuildSettingsEmpty",
                "No enabled scenes exist in Build Settings. RC preflight will " +
                "also scan saved loaded scenes, but the release build list must " +
                "be configured before RC sign-off.");
        }

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (
                scene.IsValid() &&
                !string.IsNullOrEmpty(scene.path)
            )
            {
                paths.Add(scene.path);
            }
        }

        return paths.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    private static bool HasDirtyLoadedScene(
        out string scenePath)
    {
        scenePath = string.Empty;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.IsValid() && scene.isDirty)
            {
                scenePath =
                    string.IsNullOrEmpty(scene.path)
                        ? scene.name + " (unsaved)"
                        : scene.path;
                return true;
            }
        }

        return false;
    }

    private static void CheckActiveLineAbsent(
        List<PreflightIssue> issues,
        string assetPath,
        string token,
        string code,
        string message)
    {
        string fullPath = AssetPathToFullPath(assetPath);
        if (!File.Exists(fullPath))
        {
            Add(
                issues,
                "Critical",
                "Legacy.TargetMissing",
                "Legacy-writer validation target is missing.",
                assetPath);
            return;
        }

        string[] lines = File.ReadAllLines(fullPath);
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (
                trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.Length == 0
            )
            {
                continue;
            }

            if (trimmed.IndexOf(token, StringComparison.Ordinal) >= 0)
            {
                Add(
                    issues,
                    "Critical",
                    code,
                    message,
                    assetPath,
                    "line " + (i + 1));
            }
        }
    }

    private static List<string> EnumerateProjectRuntimeCsFiles()
    {
        List<string> result = new List<string>();
        string[] roots =
        {
            "Assets/KiwiAvatarSystem",
            "Assets/Script"
        };

        foreach (string rootAssetPath in roots)
        {
            string rootFullPath = AssetPathToFullPath(rootAssetPath);
            if (!Directory.Exists(rootFullPath))
            {
                continue;
            }

            foreach (string fullPath in Directory.GetFiles(
                         rootFullPath,
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                string assetPath = FullPathToAssetPath(fullPath);
                if (
                    assetPath.IndexOf(
                        "/Editor/",
                        StringComparison.OrdinalIgnoreCase) >= 0
                )
                {
                    continue;
                }

                result.Add(assetPath);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static string ComputeProjectFingerprint(
        List<string> releaseScenes)
    {
        List<string> inputs = new List<string>();

        inputs.Add("preflight=" + PreflightVersion);
        inputs.Add("unity=" + Application.unityVersion);
        inputs.Add("buildTarget=" + EditorUserBuildSettings.activeBuildTarget);

        EditorBuildSettingsScene[] buildScenes =
            EditorBuildSettings.scenes;
        for (int i = 0; i < buildScenes.Length; i++)
        {
            inputs.Add(
                "buildScene[" + i + "]=" +
                buildScenes[i].enabled + ":" +
                buildScenes[i].path);
        }

        HashSet<string> assetPaths =
            new HashSet<string>(StringComparer.Ordinal);

        foreach (MarkerRule rule in MarkerRules)
        {
            assetPaths.Add(rule.path);
        }

        foreach (string scenePath in releaseScenes)
        {
            assetPaths.Add(scenePath);
        }

        string[] scriptRoots =
        {
            "Assets/KiwiAvatarSystem",
            "Assets/Script"
        };

        foreach (string root in scriptRoots)
        {
            string[] guids = AssetDatabase.FindAssets(
                "t:MonoScript",
                new[] { root });

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                {
                    assetPaths.Add(path);
                }
            }
        }

        string[] resourceNames =
        {
            "KiwiFacePartBlockMatch",
            "KiwiInferenceFaceCrop",
            "KiwiFaceLandmarkInference"
        };

        foreach (string resourceName in resourceNames)
        {
            string[] guids = AssetDatabase.FindAssets(resourceName);
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (
                    !string.IsNullOrEmpty(path) &&
                    path.IndexOf(
                        "/Resources/",
                        StringComparison.OrdinalIgnoreCase) >= 0
                )
                {
                    assetPaths.Add(path);
                }
            }
        }

        foreach (string assetPath in assetPaths.OrderBy(
                     path => path,
                     StringComparer.Ordinal))
        {
            string hash =
                AssetDatabase.GetAssetDependencyHash(assetPath).ToString();
            inputs.Add(assetPath + "=" + hash);
        }

        AddExternalFileFingerprint(
            inputs,
            "Packages/manifest.json");
        AddExternalFileFingerprint(
            inputs,
            "Packages/packages-lock.json");

        UnityEditor.PackageManager.PackageInfo[] packages =
            UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
        foreach (UnityEditor.PackageManager.PackageInfo package in packages
                     .Where(package => package != null)
                     .OrderBy(package => package.name, StringComparer.Ordinal))
        {
            inputs.Add(
                "pkg:" + package.name + "=" + package.version);
        }

        using (SHA256 sha = SHA256.Create())
        {
            byte[] data = Encoding.UTF8.GetBytes(
                string.Join("\n", inputs));
            return BitConverter.ToString(
                    sha.ComputeHash(data))
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }
    }

    private static void AddExternalFileFingerprint(
        List<string> inputs,
        string projectRelativePath)
    {
        string fullPath = AssetPathToFullPath(projectRelativePath);
        if (!File.Exists(fullPath))
        {
            inputs.Add("file:" + projectRelativePath + "=<missing>");
            return;
        }

        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(fullPath))
        {
            string hash = BitConverter.ToString(
                    sha.ComputeHash(stream))
                .Replace("-", string.Empty)
                .ToLowerInvariant();
            inputs.Add("file:" + projectRelativePath + "=" + hash);
        }
    }

    private static void SavePassingStamp(
        PreflightReport report)
    {
        string path = GetStampPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        PassingStamp stamp =
            new PassingStamp
            {
                version = report.version,
                fingerprint = report.fingerprint,
                generatedUtc = report.generatedUtc
            };

        File.WriteAllText(
            path,
            JsonUtility.ToJson(stamp, true),
            new UTF8Encoding(false));
    }

    private static void DeletePassingStamp()
    {
        string path = GetStampPath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static PassingStamp LoadPassingStamp()
    {
        string path = GetStampPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonUtility.FromJson<PassingStamp>(
                File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void FinalizeReport(
        PreflightReport report,
        List<PreflightIssue> issues)
    {
        report.issues = issues.ToArray();

        foreach (PreflightIssue issue in issues)
        {
            if (issue.severity == "Warning")
            {
                report.warningCount++;
            }
            else if (issue.severity == "Error")
            {
                report.errorCount++;
            }
            else if (issue.severity == "Critical")
            {
                report.criticalCount++;
            }
        }

        report.passed =
            report.errorCount == 0 &&
            report.criticalCount == 0;
        report.releaseCandidateReady = report.passed;
    }

    private static void LogReportSummary(
        PreflightReport report)
    {
        if (!report.passed && report.issues != null)
        {
            foreach (PreflightIssue issue in report.issues)
            {
                string location =
                    (!string.IsNullOrEmpty(issue.assetPath) ||
                     !string.IsNullOrEmpty(issue.objectPath))
                        ? " [" + issue.assetPath +
                          (string.IsNullOrEmpty(issue.objectPath)
                              ? string.Empty
                              : " :: " + issue.objectPath) + "]"
                        : string.Empty;

                string detail =
                    "[KiwiPreflight][" + issue.severity + "] " +
                    issue.code + ": " + issue.message + location;

                if (issue.severity == "Warning")
                {
                    Debug.LogWarning(detail);
                }
                else
                {
                    Debug.LogError(detail);
                }
            }
        }

        string summary =
            "[KiwiPreflight] Phase 16.1 " +
            (report.passed ? "PASS" : "FAIL") +
            " scenes=" + report.scenesScanned +
            " objects/components=" +
            report.objectsScanned + "/" +
            report.componentsScanned +
            " warning/error/critical=" +
            report.warningCount + "/" +
            report.errorCount + "/" +
            report.criticalCount +
            " fingerprint=" + report.fingerprint;

        if (report.passed)
        {
            Debug.Log(summary);
        }
        else
        {
            Debug.LogError(
                summary + "\n" +
                JsonUtility.ToJson(report, true));
        }
    }

    private static void Add(
        List<PreflightIssue> issues,
        string severity,
        string code,
        string message,
        string assetPath = "",
        string objectPath = "")
    {
        issues.Add(
            new PreflightIssue
            {
                severity = severity,
                code = code,
                message = message,
                assetPath = assetPath ?? string.Empty,
                objectPath = objectPath ?? string.Empty
            });
    }

    private static string GetHierarchyPath(
        Transform transform)
    {
        if (transform == null)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder(transform.name);
        Transform current = transform.parent;

        while (current != null)
        {
            builder.Insert(0, current.name + "/");
            current = current.parent;
        }

        return builder.ToString();
    }

    private static string GetProjectRoot()
    {
        return Directory.GetParent(Application.dataPath).FullName;
    }

    private static string AssetPathToFullPath(
        string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
        {
            return string.Empty;
        }

        return Path.Combine(
            GetProjectRoot(),
            assetPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string FullPathToAssetPath(
        string fullPath)
    {
        string root = GetProjectRoot()
            .Replace('\\', '/')
            .TrimEnd('/');
        string normalized = fullPath.Replace('\\', '/');

        if (normalized.StartsWith(
                root + "/",
                StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Substring(root.Length + 1);
        }

        return normalized;
    }

    private static string GetStampPath()
    {
        return Path.Combine(
            GetProjectRoot(),
            StampRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string GetPlayGatePrefKey()
    {
        string project = GetProjectRoot().Replace('\\', '/');
        return project + GatePrefSuffix;
    }
}
#endif
