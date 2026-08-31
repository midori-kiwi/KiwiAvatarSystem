using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

using Mediapipe.Unity;

/// <summary>
/// KiwiAvatarSystem v5.1 Phase 16.20.33 / v43 diagnostic-only.
///
/// Purpose:
/// - isolate Development diagnostic-overlay cost from the production tracking path;
/// - never modify tracking thresholds, ROI math, provider authority, Root,
///   FaceParts, camera capture, inference scheduling, or presentation;
/// - measure only counter deltas for 30 seconds after a 10-second warmup;
/// - write one tiny TXT result instead of the per-render-frame comparison CSV.
///
/// Environment:
///   KIWI_CADENCE_NODIAG_PROBE=1
///   KIWI_CADENCE_NODIAG_DISABLE_OVERLAY=0  -> OVERLAY_ON
///   KIWI_CADENCE_NODIAG_DISABLE_OVERLAY=1  -> OVERLAY_OFF
///
/// The same Development Standalone binary is used for both modes.
/// </summary>
[DefaultExecutionOrder(-31900)]
[DisallowMultipleComponent]
public sealed class KiwiCadenceNoCsvProbe : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Cadence No-CSV Probe";

    private const string EnableEnvironment =
        "KIWI_CADENCE_NODIAG_PROBE";

    private const string DisableOverlayEnvironment =
        "KIWI_CADENCE_NODIAG_DISABLE_OVERLAY";

    private const string Contract =
        "KIWI_V5_1_PHASE16_20_33_V43_DIAGNOSTIC_OVERHEAD_ISOLATION";

    private const double WarmupSeconds = 10.0;
    private const double MeasureSeconds = 30.0;
    private const double SetupTimeoutSeconds = 20.0;

    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private static KiwiCadenceNoCsvProbe _instance;

    private KiwiFrameComparisonOverlay _overlay;
    private bool _disableOverlay;
    private bool _configured;
    private bool _started;
    private bool _completed;
    private double _createdRealtime;
    private double _configuredRealtime;
    private double _measureStartRealtime;
    private int _measureStartFrame;
    private CounterSnapshot _begin;

    private struct CounterSnapshot
    {
        public ulong capture;
        public ulong dropped;
        public ulong presented;

        public long scheduled;
        public long readback;
        public long completed;
        public long presenceRejected;
        public long invalidRejected;
        public long droppedFresh;
        public long discardedStale;
        public long discardedCrossSystem;
        public long canonicalAdoption;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        if (!IsEnabled(EnableEnvironment))
        {
            return;
        }

        if (
            Application.platform != RuntimePlatform.WindowsPlayer ||
            !Debug.isDebugBuild)
        {
            Debug.LogWarning(
                "[KiwiCadenceNoCsvProbe] ignored: " +
                "Development Windows Player is required.");
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiCadenceNoCsvProbe>();
    }

    private static bool IsEnabled(string environmentName)
    {
        string value =
            Environment.GetEnvironmentVariable(
                environmentName);

        return
            string.Equals(
                value,
                "1",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                value,
                "true",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                value,
                "on",
                StringComparison.OrdinalIgnoreCase);
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);

        _disableOverlay =
            IsEnabled(DisableOverlayEnvironment);

        _createdRealtime =
            Time.realtimeSinceStartupAsDouble;

        Debug.Log(
            "[KiwiCadenceNoCsvProbe] contract=" +
            Contract +
            " requested=1 mode=" +
            ModeName +
            " warmupSeconds=" +
            WarmupSeconds.ToString("F1", Invariant) +
            " measureSeconds=" +
            MeasureSeconds.ToString("F1", Invariant));
    }

    private string ModeName =>
        _disableOverlay
            ? "OVERLAY_OFF"
            : "OVERLAY_ON";

    private void Update()
    {
        if (_completed)
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        if (!_configured)
        {
            TryConfigureDiagnosticMode(now);
            return;
        }

        if (!_started)
        {
            if (
                now - _configuredRealtime <
                WarmupSeconds)
            {
                return;
            }

            if (!TryReadCounters(out _begin))
            {
                return;
            }

            _measureStartRealtime = now;
            _measureStartFrame = Time.frameCount;
            _started = true;

            Debug.Log(
                "[KiwiCadenceNoCsvProbe] BEGIN mode=" +
                ModeName +
                " frame=" +
                _measureStartFrame +
                " capture=" +
                _begin.capture +
                " presented=" +
                _begin.presented +
                " scheduled=" +
                _begin.scheduled +
                " canonical=" +
                _begin.canonicalAdoption);

            return;
        }

        if (
            now - _measureStartRealtime <
            MeasureSeconds)
        {
            return;
        }

        if (!TryReadCounters(out CounterSnapshot end))
        {
            return;
        }

        CompleteMeasurement(
            now,
            end);
    }

    private void TryConfigureDiagnosticMode(
        double now)
    {
        _overlay =
            FindFirstObjectByType<KiwiFrameComparisonOverlay>(
                FindObjectsInactive.Include);

        if (_overlay == null)
        {
            if (
                now - _createdRealtime >=
                SetupTimeoutSeconds)
            {
                CompleteSetupFailure(
                    "KiwiFrameComparisonOverlay was not found.");
            }

            return;
        }

        // No large comparison CSV is allowed in this probe.
        if (_overlay.IsCsvRecording)
        {
            _overlay.StopCsvRecording();
        }

        if (_disableOverlay)
        {
            // Disable only the diagnostic MonoBehaviour. This stops its
            // LateUpdate/OnGUI work without destroying it or touching tracking.
            _overlay.enabled = false;
        }
        else
        {
            // Controlled reference condition: normal Development overlay work
            // is active and visible, but F9 CSV remains OFF.
            _overlay.enabled = true;
            _overlay.visible = true;
        }

        _configured = true;
        _configuredRealtime = now;

        Debug.Log(
            "[KiwiCadenceNoCsvProbe] configured mode=" +
            ModeName +
            " overlayFound=1 overlayEnabled=" +
            (_overlay.enabled ? "1" : "0") +
            " overlayVisible=" +
            (_overlay.visible ? "1" : "0") +
            " csv=0");
    }

    private static bool TryReadCounters(
        out CounterSnapshot snapshot)
    {
        snapshot = default;

        if (
            !KiwiNativeCameraTelemetry.TryGetSnapshot(
                out KiwiNativeCameraTelemetrySnapshot camera) ||
            !camera.active)
        {
            return false;
        }

        snapshot.capture =
            camera.captureFrameCount;
        snapshot.dropped =
            camera.droppedFrameCount;
        snapshot.presented =
            camera.presentedFrameCount;

        snapshot.scheduled =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceScheduledCount;
        snapshot.readback =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceReadbackCompletedCount;
        snapshot.completed =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceCompletedCount;
        snapshot.presenceRejected =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceRejectedPresenceCount;
        snapshot.invalidRejected =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceRejectedInvalidCount;
        snapshot.droppedFresh =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceDroppedFreshCount;
        snapshot.discardedStale =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceDiscardedStaleCount;
        snapshot.discardedCrossSystem =
            KiwiCommercialCadencePipelineTelemetry
                .InferenceDiscardedCrossSystemCount;
        snapshot.canonicalAdoption =
            KiwiCommercialCadencePipelineTelemetry
                .AdoptionCount;

        return true;
    }

    private void CompleteMeasurement(
        double now,
        CounterSnapshot end)
    {
        double duration =
            Math.Max(
                0.001,
                now - _measureStartRealtime);

        int renderFrames =
            Math.Max(
                0,
                Time.frameCount -
                _measureStartFrame);

        ulong captureDelta =
            Delta(end.capture, _begin.capture);
        ulong droppedDelta =
            Delta(end.dropped, _begin.dropped);
        ulong presentedDelta =
            Delta(end.presented, _begin.presented);

        long scheduledDelta =
            Delta(end.scheduled, _begin.scheduled);
        long readbackDelta =
            Delta(end.readback, _begin.readback);
        long completedDelta =
            Delta(end.completed, _begin.completed);
        long presenceRejectedDelta =
            Delta(
                end.presenceRejected,
                _begin.presenceRejected);
        long invalidRejectedDelta =
            Delta(
                end.invalidRejected,
                _begin.invalidRejected);
        long droppedFreshDelta =
            Delta(
                end.droppedFresh,
                _begin.droppedFresh);
        long discardedStaleDelta =
            Delta(
                end.discardedStale,
                _begin.discardedStale);
        long discardedCrossSystemDelta =
            Delta(
                end.discardedCrossSystem,
                _begin.discardedCrossSystem);
        long canonicalDelta =
            Delta(
                end.canonicalAdoption,
                _begin.canonicalAdoption);

        double renderHz =
            renderFrames / duration;
        double captureHz =
            captureDelta / duration;
        double presentedHz =
            presentedDelta / duration;
        double scheduledHz =
            scheduledDelta / duration;
        double readbackHz =
            readbackDelta / duration;
        double completedHz =
            completedDelta / duration;
        double canonicalHz =
            canonicalDelta / duration;
        double presenceRejectedHz =
            presenceRejectedDelta / duration;
        double invalidRejectedHz =
            invalidRejectedDelta / duration;

        string result =
            BuildResultText(
                duration,
                renderFrames,
                captureDelta,
                droppedDelta,
                presentedDelta,
                scheduledDelta,
                readbackDelta,
                completedDelta,
                presenceRejectedDelta,
                invalidRejectedDelta,
                droppedFreshDelta,
                discardedStaleDelta,
                discardedCrossSystemDelta,
                canonicalDelta,
                renderHz,
                captureHz,
                presentedHz,
                scheduledHz,
                readbackHz,
                completedHz,
                canonicalHz,
                presenceRejectedHz,
                invalidRejectedHz);

        Debug.Log(
            "[KiwiCadenceNoCsvProbe] RESULT " +
            FlattenResultForLog(result));

        WriteResultFile(
            result);

        _completed = true;
        enabled = false;
    }

    private string BuildResultText(
        double duration,
        int renderFrames,
        ulong captureDelta,
        ulong droppedDelta,
        ulong presentedDelta,
        long scheduledDelta,
        long readbackDelta,
        long completedDelta,
        long presenceRejectedDelta,
        long invalidRejectedDelta,
        long droppedFreshDelta,
        long discardedStaleDelta,
        long discardedCrossSystemDelta,
        long canonicalDelta,
        double renderHz,
        double captureHz,
        double presentedHz,
        double scheduledHz,
        double readbackHz,
        double completedHz,
        double canonicalHz,
        double presenceRejectedHz,
        double invalidRejectedHz)
    {
        StringBuilder sb =
            new StringBuilder(2048);

        sb.AppendLine(
            "KiwiAvatarSystem Cadence No-CSV Probe");
        Append(sb, "contract", Contract);
        Append(sb, "mode", ModeName);
        Append(sb, "durationSeconds", duration);
        Append(sb, "renderFrames", renderFrames);
        Append(sb, "renderHz", renderHz);
        Append(sb, "captureDelta", captureDelta);
        Append(sb, "captureHz", captureHz);
        Append(sb, "cameraDroppedDelta", droppedDelta);
        Append(sb, "presentedDelta", presentedDelta);
        Append(sb, "presentedHz", presentedHz);
        Append(sb, "scheduledDelta", scheduledDelta);
        Append(sb, "scheduledHz", scheduledHz);
        Append(sb, "readbackDelta", readbackDelta);
        Append(sb, "readbackHz", readbackHz);
        Append(sb, "completedDelta", completedDelta);
        Append(sb, "completedHz", completedHz);
        Append(
            sb,
            "presenceRejectedDelta",
            presenceRejectedDelta);
        Append(
            sb,
            "presenceRejectedHz",
            presenceRejectedHz);
        Append(
            sb,
            "invalidRejectedDelta",
            invalidRejectedDelta);
        Append(
            sb,
            "invalidRejectedHz",
            invalidRejectedHz);
        Append(sb, "droppedFreshDelta", droppedFreshDelta);
        Append(
            sb,
            "discardedStaleDelta",
            discardedStaleDelta);
        Append(
            sb,
            "discardedCrossSystemDelta",
            discardedCrossSystemDelta);
        Append(sb, "canonicalDelta", canonicalDelta);
        Append(sb, "canonicalHz", canonicalHz);

        Append(
            sb,
            "captureToPresentedRatio",
            Ratio(
                presentedDelta,
                captureDelta));
        Append(
            sb,
            "presentedToScheduledRatio",
            Ratio(
                scheduledDelta,
                presentedDelta));
        Append(
            sb,
            "readbackToScheduledRatio",
            Ratio(
                readbackDelta,
                scheduledDelta));
        Append(
            sb,
            "canonicalToCompletedRatio",
            Ratio(
                canonicalDelta,
                completedDelta));

        sb.AppendLine(
            "note=observer-only; no tracking/threshold/ROI/authority/camera writes");

        return sb.ToString();
    }

    private static void Append(
        StringBuilder sb,
        string name,
        string value)
    {
        sb.Append(name);
        sb.Append('=');
        sb.AppendLine(
            value ?? string.Empty);
    }

    private static void Append(
        StringBuilder sb,
        string name,
        long value)
    {
        Append(
            sb,
            name,
            value.ToString(Invariant));
    }

    private static void Append(
        StringBuilder sb,
        string name,
        ulong value)
    {
        Append(
            sb,
            name,
            value.ToString(Invariant));
    }

    private static void Append(
        StringBuilder sb,
        string name,
        double value)
    {
        Append(
            sb,
            name,
            value.ToString("F6", Invariant));
    }

    private static ulong Delta(
        ulong end,
        ulong begin)
    {
        return end >= begin
            ? end - begin
            : 0UL;
    }

    private static long Delta(
        long end,
        long begin)
    {
        return end >= begin
            ? end - begin
            : 0L;
    }

    private static double Ratio(
        ulong numerator,
        ulong denominator)
    {
        return denominator > 0UL
            ? numerator / (double)denominator
            : 0.0;
    }

    private static double Ratio(
        long numerator,
        ulong denominator)
    {
        return denominator > 0UL
            ? numerator / (double)denominator
            : 0.0;
    }

    private static double Ratio(
        long numerator,
        long denominator)
    {
        return denominator > 0L
            ? numerator / (double)denominator
            : 0.0;
    }

    private static string FlattenResultForLog(
        string result)
    {
        return result
            .Replace("\r", string.Empty)
            .Replace("\n", " | ")
            .TrimEnd(
                ' ',
                '|');
    }

    private void WriteResultFile(
        string result)
    {
        try
        {
            string directory =
                Path.Combine(
                    Application.persistentDataPath,
                    "KiwiCadenceNoCsvProbe");

            Directory.CreateDirectory(directory);

            string path =
                Path.Combine(
                    directory,
                    "KiwiCadenceNoCsvProbe_" +
                    ModeName +
                    "_" +
                    DateTime.Now.ToString(
                        "yyyyMMdd_HHmmss",
                        Invariant) +
                    ".txt");

            File.WriteAllText(
                path,
                result,
                new UTF8Encoding(false));

            Debug.Log(
                "[KiwiCadenceNoCsvProbe] resultPath=" +
                path);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[KiwiCadenceNoCsvProbe] result write failed: " +
                ex.Message);
        }
    }

    private void CompleteSetupFailure(
        string reason)
    {
        string result =
            "KiwiAvatarSystem Cadence No-CSV Probe\n" +
            "contract=" + Contract + "\n" +
            "mode=" + ModeName + "\n" +
            "result=SETUP_FAIL\n" +
            "reason=" + reason + "\n";

        Debug.LogError(
            "[KiwiCadenceNoCsvProbe] " +
            FlattenResultForLog(result));

        WriteResultFile(
            result);

        _completed = true;
        enabled = false;
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }
}
