#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Command-line armed P3D controller. It toggles only the existing diagnostic
/// FaceGeometry gate, observes two live phases separated by a full service
/// shutdown, writes one aggregate evidence file, and quits the Development
/// Player. It owns no Product tracking or presentation output.
/// </summary>
internal sealed class KiwiP3DFaceGeometryRuntimeController : MonoBehaviour
{
    private const string EnableEnvironment = "KIWI_P3D_RUNTIME";
    private const string ResultPathEnvironment = "KIWI_P3D_RESULT_PATH";
    private const double RunnerTimeoutSeconds = 30.0;
    private const double LiveInputTimeoutSeconds = 120.0;
    private const double ShutdownTimeoutSeconds = 30.0;

    private static bool _installed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed || !IsEnabled()) return;
        _installed = true;
        var host = new GameObject("[Kiwi] P3D FaceGeometry Runtime Controller");
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiP3DFaceGeometryRuntimeController>();
    }

    private static bool IsEnabled()
    {
        string value = Environment.GetEnvironmentVariable(EnableEnvironment);
        return Debug.isDebugBuild &&
            (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerator Start()
    {
        Application.runInBackground = true;
        string resultPath = ResolveResultPath();
        FaceLandmarkerRunner runner = null;
        FieldInfo gateField = null;
        FieldInfo serviceField = null;
        string status = "UNRESOLVED";
        string firstBoundary = "NONE";
        string detail = string.Empty;
        KiwiFaceGeometryP3DDiagnostics.Snapshot phaseOne = null;
        KiwiFaceGeometryP3DDiagnostics.Snapshot final = null;

        KiwiFaceGeometryP3DDiagnostics.ResetAndEnable();
        double started = Time.realtimeSinceStartupAsDouble;
        while (Time.realtimeSinceStartupAsDouble - started < RunnerTimeoutSeconds)
        {
            runner = FindFirstObjectByType<FaceLandmarkerRunner>(
                FindObjectsInactive.Include);
            if (runner != null) break;
            yield return null;
        }

        if (runner == null)
        {
            status = "FAIL";
            firstBoundary = "RUNNER_NOT_FOUND";
            detail = "FaceLandmarkerRunner was not present in the built scene.";
            final = KiwiFaceGeometryP3DDiagnostics.Capture();
            WriteResult(resultPath, status, firstBoundary, detail, phaseOne, final);
            KiwiFaceGeometryP3DDiagnostics.Disable();
            Application.Quit(31);
            yield break;
        }

        Type runnerType = runner.GetType();
        gateField = runnerType.GetField(
            "enableInferenceFaceGeometryTransactions",
            BindingFlags.Instance | BindingFlags.NonPublic);
        serviceField = runnerType.GetField(
            "_faceGeometryTransactionService",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (gateField == null || serviceField == null)
        {
            status = "FAIL";
            firstBoundary = "OBSERVER_IDENTITY";
            detail = "The exact P3D gate/service fields were not found.";
            final = KiwiFaceGeometryP3DDiagnostics.Capture();
            WriteResult(resultPath, status, firstBoundary, detail, phaseOne, final);
            KiwiFaceGeometryP3DDiagnostics.Disable();
            Application.Quit(32);
            yield break;
        }

        gateField.SetValue(runner, true);
        Debug.Log("[KiwiP3D] PHASE_1_GATE_ENABLED");
        started = Time.realtimeSinceStartupAsDouble;
        while (Time.realtimeSinceStartupAsDouble - started < LiveInputTimeoutSeconds)
        {
            phaseOne = KiwiFaceGeometryP3DDiagnostics.Capture();
            if (phaseOne.acceptedCount >= 1L) break;
            yield return null;
        }

        phaseOne = KiwiFaceGeometryP3DDiagnostics.Capture();
        if (phaseOne.acceptedCount < 1L)
        {
            gateField.SetValue(runner, false);
            status = phaseOne.admissionCount == 0L
                ? "BLOCKED_EXTERNAL_RUNTIME_INPUT"
                : "FAIL";
            firstBoundary = phaseOne.admissionCount == 0L
                ? "LIVE_CAMERA_OR_REAL_FACE_INPUT"
                : "NORMAL_GEOMETRY_CALLBACK";
            detail = "No accepted live FaceGeometry result arrived in phase 1.";
            yield return WaitForServiceShutdown(serviceField, runner);
            final = KiwiFaceGeometryP3DDiagnostics.Capture();
            WriteResult(resultPath, status, firstBoundary, detail, phaseOne, final);
            KiwiFaceGeometryP3DDiagnostics.Disable();
            Application.Quit(status == "BLOCKED_EXTERNAL_RUNTIME_INPUT" ? 40 : 33);
            yield break;
        }

        int firstServiceId = phaseOne.lastAccepted.serviceId;
        long firstAcceptedCount = phaseOne.acceptedCount;
        gateField.SetValue(runner, false);
        Debug.Log("[KiwiP3D] PHASE_1_GATE_DISABLED_FOR_FULL_SHUTDOWN");
        yield return WaitForServiceShutdown(serviceField, runner);
        KiwiFaceGeometryP3DDiagnostics.Snapshot afterFirstShutdown =
            KiwiFaceGeometryP3DDiagnostics.Capture();
        if (serviceField.GetValue(runner) != null ||
            afterFirstShutdown.graphDestroyedCount < 1L ||
            afterFirstShutdown.callbackRootsReleasedCount < 1L ||
            afterFirstShutdown.activeCallbacks != 0L)
        {
            status = "FAIL";
            firstBoundary = "GRAPH_SHUTDOWN_DISPOSE";
            detail = "The first live graph did not fully destroy and release callback roots.";
            final = afterFirstShutdown;
            WriteResult(resultPath, status, firstBoundary, detail, phaseOne, final);
            KiwiFaceGeometryP3DDiagnostics.Disable();
            Application.Quit(34);
            yield break;
        }

        gateField.SetValue(runner, true);
        Debug.Log("[KiwiP3D] PHASE_2_GATE_ENABLED_AFTER_RESTART");
        started = Time.realtimeSinceStartupAsDouble;
        while (Time.realtimeSinceStartupAsDouble - started < LiveInputTimeoutSeconds)
        {
            final = KiwiFaceGeometryP3DDiagnostics.Capture();
            if (final.acceptedCount > firstAcceptedCount &&
                final.lastAccepted.serviceId > firstServiceId)
            {
                break;
            }
            yield return null;
        }

        final = KiwiFaceGeometryP3DDiagnostics.Capture();
        if (final.acceptedCount <= firstAcceptedCount ||
            final.lastAccepted.serviceId <= firstServiceId)
        {
            gateField.SetValue(runner, false);
            status = final.admissionCount == phaseOne.admissionCount
                ? "BLOCKED_EXTERNAL_RUNTIME_INPUT"
                : "FAIL";
            firstBoundary = final.admissionCount == phaseOne.admissionCount
                ? "LIVE_CAMERA_OR_REAL_FACE_INPUT_AFTER_RESTART"
                : "NEW_RUN_CALLBACK_AFTER_RESTART";
            detail = "No accepted live FaceGeometry result arrived from a new service after restart.";
            yield return WaitForServiceShutdown(serviceField, runner);
            final = KiwiFaceGeometryP3DDiagnostics.Capture();
            WriteResult(resultPath, status, firstBoundary, detail, phaseOne, final);
            KiwiFaceGeometryP3DDiagnostics.Disable();
            Application.Quit(status == "BLOCKED_EXTERNAL_RUNTIME_INPUT" ? 41 : 35);
            yield break;
        }

        gateField.SetValue(runner, false);
        Debug.Log("[KiwiP3D] PHASE_2_GATE_DISABLED_FOR_FINAL_SHUTDOWN");
        yield return WaitForServiceShutdown(serviceField, runner);
        final = KiwiFaceGeometryP3DDiagnostics.Capture();

        bool pass =
            serviceField.GetValue(runner) == null &&
            final.acceptedCount >= 2L &&
            final.completionIdentityMismatchCount == 0L &&
            final.duplicateAcceptedPublishCount == 0L &&
            final.outOfOrderAcceptedPublishCount == 0L &&
            final.staleGenerationAcceptedPublishCount == 0L &&
            final.mixedEpochAcceptedPublishCount == 0L &&
            final.oldRunToNewRunPublicationCount == 0L &&
            final.maxInFlight <= 1 &&
            final.maxPending <= 1 &&
            final.sourceAgeSampleCount == final.acceptedCount &&
            final.sourceAgeInvalidCount == 0L &&
            final.serviceCreatedCount >= 2L &&
            final.graphCreatedCount >= 2L &&
            final.graphDestroyStartedCount == final.graphCreatedCount &&
            final.graphDestroyedCount == final.graphCreatedCount &&
            final.callbackRootsReleasedCount == final.graphCreatedCount &&
            final.callbackEnterCount == final.callbackExitCount &&
            final.activeCallbacks == 0L &&
            final.preDestroyRootReleaseCount == 0L &&
            final.preDrainRootReleaseCount == 0L &&
            final.transientFaceGeometryCount >= 2L &&
            final.allocationSampleCount >= 2L;

        status = pass ? "PASS" : "FAIL";
        if (!pass)
        {
            firstBoundary = DetermineFirstBoundary(final, serviceField.GetValue(runner));
            detail = "One or more P3D correctness/lifecycle aggregates failed.";
        }
        WriteResult(resultPath, status, firstBoundary, detail, phaseOne, final);
        KiwiFaceGeometryP3DDiagnostics.Disable();
        Application.Quit(pass ? 0 : 36);
    }

    private IEnumerator WaitForServiceShutdown(FieldInfo serviceField, object runner)
    {
        double started = Time.realtimeSinceStartupAsDouble;
        while (Time.realtimeSinceStartupAsDouble - started < ShutdownTimeoutSeconds)
        {
            if (serviceField.GetValue(runner) == null) yield break;
            yield return null;
        }
    }

    private static string DetermineFirstBoundary(
        KiwiFaceGeometryP3DDiagnostics.Snapshot value,
        object activeService)
    {
        if (value.completionIdentityMismatchCount != 0L) return "SAME_SAMPLE_CORRELATION";
        if (value.duplicateAcceptedPublishCount != 0L) return "DUPLICATE_ACCEPTED_PUBLISH";
        if (value.outOfOrderAcceptedPublishCount != 0L) return "OUT_OF_ORDER_ACCEPTED_PUBLISH";
        if (value.staleGenerationAcceptedPublishCount != 0L) return "STALE_GENERATION_ACCEPTED_PUBLISH";
        if (value.mixedEpochAcceptedPublishCount != 0L) return "MIXED_EPOCH_ACCEPTED_PUBLISH";
        if (value.maxInFlight > 1 || value.maxPending > 1) return "BOUNDED_LATEST_TRANSACTION";
        if (value.sourceAgeInvalidCount != 0L || value.sourceAgeSampleCount != value.acceptedCount) return "SOURCE_FRESHNESS_IDENTITY";
        if (value.oldRunToNewRunPublicationCount != 0L) return "OLD_RUN_TO_NEW_RUN_PUBLICATION";
        if (activeService != null || value.graphDestroyedCount != value.graphCreatedCount) return "GRAPH_REBUILD_LIFECYCLE";
        if (value.callbackEnterCount != value.callbackExitCount ||
            value.activeCallbacks != 0L ||
            value.preDestroyRootReleaseCount != 0L ||
            value.preDrainRootReleaseCount != 0L) return "CALLBACK_DESTRUCTION_RESTART";
        if (value.transientFaceGeometryCount < 2L || value.allocationSampleCount < 2L) return "TRANSIENT_RESOURCE_OBSERVATION";
        return "OBSERVER_CLASSIFICATION";
    }

    private static string ResolveResultPath()
    {
        string path = Environment.GetEnvironmentVariable(ResultPathEnvironment);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(
                Application.persistentDataPath,
                "KiwiP3D_FaceGeometry_Runtime_Result.txt");
        }
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("P3D result path has no directory.");
        }
        Directory.CreateDirectory(directory);
        return path;
    }

    private static void WriteResult(
        string path,
        string status,
        string firstBoundary,
        string detail,
        KiwiFaceGeometryP3DDiagnostics.Snapshot phaseOne,
        KiwiFaceGeometryP3DDiagnostics.Snapshot final)
    {
        var text = new StringBuilder();
        text.AppendLine("KIWI_P3D_FACEGEOMETRY_RUNTIME_EVIDENCE");
        text.AppendLine("UTC=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        text.AppendLine("UNITY_VERSION=" + Application.unityVersion);
        text.AppendLine("DEBUG_BUILD=" + (Debug.isDebugBuild ? "1" : "0"));
        text.AppendLine("P3D_RUNTIME_RESULT=" + status);
        text.AppendLine("FIRST_FAILING_BOUNDARY=" + firstBoundary);
        text.AppendLine("DETAIL=" + detail);
        AppendSnapshot(text, "PHASE1", phaseOne);
        AppendSnapshot(text, "FINAL", final);
        string temporary = path + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
        Debug.Log("[KiwiP3D] RESULT_WRITTEN path=" + path + " result=" + status);
    }

    private static void AppendSnapshot(
        StringBuilder text,
        string prefix,
        KiwiFaceGeometryP3DDiagnostics.Snapshot value)
    {
        if (value == null)
        {
            text.AppendLine(prefix + "_SNAPSHOT=NONE");
            return;
        }
        text.AppendLine(prefix + "_SERVICE_CREATED=" + value.serviceCreatedCount);
        text.AppendLine(prefix + "_ADMISSION_COUNT=" + value.admissionCount);
        text.AppendLine(prefix + "_SUBMITTED_COUNT=" + value.submittedCount);
        text.AppendLine(prefix + "_PENDING_SUPERSEDE_COUNT=" + value.pendingSupersedeCount);
        text.AppendLine(prefix + "_MAX_IN_FLIGHT=" + value.maxInFlight);
        text.AppendLine(prefix + "_MAX_PENDING=" + value.maxPending);
        text.AppendLine(prefix + "_GRAPH_CREATED=" + value.graphCreatedCount);
        text.AppendLine(prefix + "_GRAPH_RETIRED=" + value.graphRetiredCount);
        text.AppendLine(prefix + "_GRAPH_DESTROY_STARTED=" + value.graphDestroyStartedCount);
        text.AppendLine(prefix + "_GRAPH_DESTROYED=" + value.graphDestroyedCount);
        text.AppendLine(prefix + "_CALLBACK_ROOTS_RELEASED=" + value.callbackRootsReleasedCount);
        text.AppendLine(prefix + "_CALLBACK_ENTER=" + value.callbackEnterCount);
        text.AppendLine(prefix + "_CALLBACK_EXIT=" + value.callbackExitCount);
        text.AppendLine(prefix + "_ACTIVE_CALLBACKS=" + value.activeCallbacks);
        text.AppendLine(prefix + "_MAX_ACTIVE_CALLBACKS=" + value.maxActiveCallbacks);
        text.AppendLine(prefix + "_LATE_CALLBACK_COUNT=" + value.lateCallbackCount);
        text.AppendLine(prefix + "_CALLBACK_HANDOFF=" + value.callbackHandoffCount);
        text.AppendLine(prefix + "_CALLBACK_HANDOFF_REJECTED=" + value.callbackHandoffRejectedCount);
        text.AppendLine(prefix + "_COMPLETION_IDENTITY_MISMATCH=" + value.completionIdentityMismatchCount);
        text.AppendLine(prefix + "_ACCEPTED_COUNT=" + value.acceptedCount);
        text.AppendLine(prefix + "_DUPLICATE_ACCEPTED_PUBLISH=" + value.duplicateAcceptedPublishCount);
        text.AppendLine(prefix + "_OUT_OF_ORDER_ACCEPTED_PUBLISH=" + value.outOfOrderAcceptedPublishCount);
        text.AppendLine(prefix + "_STALE_GENERATION_ACCEPTED_PUBLISH=" + value.staleGenerationAcceptedPublishCount);
        text.AppendLine(prefix + "_MIXED_EPOCH_ACCEPTED_PUBLISH=" + value.mixedEpochAcceptedPublishCount);
        text.AppendLine(prefix + "_OLD_RUN_TO_NEW_RUN_PUBLICATION=" + value.oldRunToNewRunPublicationCount);
        text.AppendLine(prefix + "_SOURCE_AGE_SAMPLE_COUNT=" + value.sourceAgeSampleCount);
        text.AppendLine(prefix + "_SOURCE_AGE_INVALID_COUNT=" + value.sourceAgeInvalidCount);
        text.AppendLine(prefix + "_SOURCE_AGE_MIN_TICKS=" + value.sourceAgeMinTicks);
        text.AppendLine(prefix + "_SOURCE_AGE_MAX_TICKS=" + value.sourceAgeMaxTicks);
        text.AppendLine(prefix + "_STOPWATCH_FREQUENCY=" + Stopwatch.Frequency);
        text.AppendLine(prefix + "_TRANSIENT_FACEGEOMETRY_COUNT=" + value.transientFaceGeometryCount);
        text.AppendLine(prefix + "_ALLOCATION_SAMPLE_COUNT=" + value.allocationSampleCount);
        text.AppendLine(prefix + "_ALLOCATION_BYTES_TOTAL=" + value.allocationBytesTotal);
        text.AppendLine(prefix + "_ALLOCATION_BYTES_MAX=" + value.allocationBytesMax);
        text.AppendLine(prefix + "_GC0_DURING_CALLBACKS=" + value.gc0CollectionsDuringCallbacks);
        text.AppendLine(prefix + "_GC1_DURING_CALLBACKS=" + value.gc1CollectionsDuringCallbacks);
        text.AppendLine(prefix + "_GC2_DURING_CALLBACKS=" + value.gc2CollectionsDuringCallbacks);
        text.AppendLine(prefix + "_PRE_DESTROY_ROOT_RELEASE=" + value.preDestroyRootReleaseCount);
        text.AppendLine(prefix + "_PRE_DRAIN_ROOT_RELEASE=" + value.preDrainRootReleaseCount);
        AppendIdentity(text, prefix + "_FIRST", value.firstAccepted);
        AppendIdentity(text, prefix + "_LAST", value.lastAccepted);
    }

    private static void AppendIdentity(
        StringBuilder text,
        string prefix,
        KiwiFaceGeometryP3DDiagnostics.Identity value)
    {
        text.AppendLine(prefix + "_EXISTS=" + (value.exists ? "1" : "0"));
        if (!value.exists) return;
        text.AppendLine(prefix + "_SERVICE_ID=" + value.serviceId);
        text.AppendLine(prefix + "_RUN_ID=" + value.runId);
        text.AppendLine(prefix + "_STREAM_ID=" + value.streamId);
        text.AppendLine(prefix + "_FRAME_ID=" + value.frameId);
        text.AppendLine(prefix + "_SOURCE_HOST_TICKS=" + value.sourceHostTicks);
        text.AppendLine(prefix + "_CAMERA_GENERATION=" + value.cameraGeneration);
        text.AppendLine(prefix + "_TRACKING_SESSION_GENERATION=" + value.trackingSessionGeneration);
        text.AppendLine(prefix + "_PROVIDER_GENERATION=" + value.providerGeneration);
        text.AppendLine(prefix + "_MODEL_GENERATION=" + value.modelGeneration);
        text.AppendLine(prefix + "_BACKEND=" + value.backend);
        text.AppendLine(prefix + "_SEMANTIC_FRAME_WIDTH=" + value.semanticFrameWidth);
        text.AppendLine(prefix + "_SEMANTIC_FRAME_HEIGHT=" + value.semanticFrameHeight);
    }
}
#endif
