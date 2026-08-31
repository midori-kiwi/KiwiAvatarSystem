using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Diagnostic-only, fail-closed controller for the v42.3 automated Player runs.
/// It is inert unless KIWI_VALIDATION_TOKEN is explicitly supplied.
/// </summary>
[DefaultExecutionOrder(-31900)]
[DisallowMultipleComponent]
public sealed class KiwiAutomatedValidationController : MonoBehaviour
{
    private const string Contract =
        "KIWI_V5_1_PHASE16_20_32_V42_3_AUTOMATED_VALIDATION_V1";
    private const string RuntimeObjectName =
        "[Kiwi] Automated Validation Controller";
    private const int TargetWidth = 1280;
    private const int TargetHeight = 720;
    private const int CameraWidth = 1920;
    private const int CameraHeight = 1080;
    private const int CameraFps = 60;
    private const double DefaultStartupTimeoutSeconds = 60.0;
    private const double DefaultWarmupSeconds = 5.0;
    private const double DefaultRecordSeconds = 30.0;

    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;
    private static readonly Regex SafeIdentifier =
        new Regex(
            "^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$",
            RegexOptions.CultureInvariant);

    [Serializable]
    private sealed class ArtifactReceipt
    {
        public string path;
        public long bytes;
        public int rows;
        public string sha256;
    }

    [Serializable]
    private sealed class CameraReceipt
    {
        public bool active;
        public int width;
        public int height;
        public int targetFrameRate;
        public bool timestampCalibrationValid;
        public ulong sourceFrameCount;
        public ulong presentedFrameCount;
    }

    [Serializable]
    private sealed class ValidationReceipt
    {
        public string contract;
        public string version;
        public string status;
        public string error;
        public string runId;
        public string caseId;
        public string token;
        public string mode;
        public string purpose;
        public int processId;
        public string graphicsApi;
        public int screenWidth;
        public int screenHeight;
        public string startedUtc;
        public string startupReadyUtc;
        public string recordingStartedUtc;
        public string recordingStoppedUtc;
        public string committedUtc;
        public double warmupSeconds;
        public double requestedRecordSeconds;
        public int runtimeValidationErrors;
        public int runtimeValidationCriticals;
        public CameraReceipt camera;
        public ArtifactReceipt frameComparisonCsv;
        public ArtifactReceipt ortZeroCopyCsv;
        public ArtifactReceipt runtimeValidationJson;
    }

    private struct ZeroCopyStatus
    {
        public bool installed;
        public bool ready;
        public bool disabled;
        public int bridgeReadyAttempts;
        public bool csvRecording;
        public string csvPath;
        public int csvRows;
    }

    private static KiwiAutomatedValidationController _instance;

    private string _runId = string.Empty;
    private string _caseId = string.Empty;
    private string _token = string.Empty;
    private string _mode = string.Empty;
    private string _purpose = string.Empty;
    private string _outputDirectory = string.Empty;
    private string _receiptPath = string.Empty;
    private string _startedUtc = string.Empty;
    private string _startupReadyUtc = string.Empty;
    private string _recordingStartedUtc = string.Empty;
    private string _recordingStoppedUtc = string.Empty;
    private double _warmupSeconds;
    private double _recordSeconds;
    private double _startupTimeoutSeconds;
    private bool _receiptCommitted;
    private ulong _lastObservedCanonicalFrameId;
    private double _lastCanonicalAdvanceRealtime = -1.0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        _instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        string token =
            Environment.GetEnvironmentVariable(
                "KIWI_VALIDATION_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        GameObject host = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiAutomatedValidationController>();
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
        _startedUtc = UtcNow();
    }

    private IEnumerator Start()
    {
        if (!TryReadContract(out string contractError))
        {
            yield return FailAndQuit(contractError);
            yield break;
        }

        Screen.SetResolution(
            TargetWidth,
            TargetHeight,
            FullScreenMode.Windowed);

        Debug.Log(
            "[KiwiValidation] BOOT contract=" + Contract +
            " runId=" + _runId +
            " caseId=" + _caseId +
            " token=" + _token +
            " mode=" + _mode +
            " purpose=" + _purpose);

        double deadline =
            Time.realtimeSinceStartupAsDouble +
            _startupTimeoutSeconds;
        string prerequisiteError = string.Empty;

        while (
            Time.realtimeSinceStartupAsDouble < deadline &&
            !TryValidatePrerequisites(out prerequisiteError))
        {
            yield return null;
        }

        if (!TryValidatePrerequisites(out prerequisiteError))
        {
            yield return FailAndQuit(
                "Startup prerequisites timed out: " + prerequisiteError);
            yield break;
        }

        _startupReadyUtc = UtcNow();
        Debug.Log(
            "[KiwiValidation] STARTUP_READY runId=" + _runId +
            " caseId=" + _caseId +
            " token=" + _token +
            " mode=" + _mode +
            " purpose=" + _purpose);

        double warmupUntil =
            Time.realtimeSinceStartupAsDouble + _warmupSeconds;
        while (Time.realtimeSinceStartupAsDouble < warmupUntil)
        {
            if (!TryValidatePrerequisites(out prerequisiteError))
            {
                yield return FailAndQuit(
                    "Prerequisite lost during warmup: " +
                    prerequisiteError);
                yield break;
            }

            yield return null;
        }

        KiwiFrameComparisonOverlay overlay =
            KiwiFrameComparisonOverlay.EnsureInstance();
        overlay.StartCsvRecording();
        yield return null;
        yield return null;

        if (!overlay.IsCsvRecording)
        {
            yield return FailAndQuit(
                "Frame comparison CSV did not start.");
            yield break;
        }

        if (
            IsZeroCopyMode() &&
            !ReadZeroCopyStatus().csvRecording)
        {
            yield return FailAndQuit(
                "ORT zero-copy CSV did not start with the frame comparison CSV.");
            yield break;
        }

        _recordingStartedUtc = UtcNow();
        Debug.Log(
            "[KiwiValidation] RECORDING_STARTED token=" + _token +
            " durationSec=" +
            _recordSeconds.ToString("R", Invariant));

        double recordUntil =
            Time.realtimeSinceStartupAsDouble + _recordSeconds;
        while (Time.realtimeSinceStartupAsDouble < recordUntil)
        {
            if (!TryValidatePrerequisites(out prerequisiteError))
            {
                overlay.StopCsvRecording();
                yield return FailAndQuit(
                    "Prerequisite lost during recording: " +
                    prerequisiteError);
                yield break;
            }

            yield return null;
        }

        overlay.StopCsvRecording();
        _recordingStoppedUtc = UtcNow();

        double flushDeadline =
            Time.realtimeSinceStartupAsDouble + 5.0;
        while (
            IsZeroCopyMode() &&
            ReadZeroCopyStatus().csvRecording &&
            Time.realtimeSinceStartupAsDouble < flushDeadline)
        {
            yield return null;
        }

        if (
            IsZeroCopyMode() &&
            ReadZeroCopyStatus().csvRecording)
        {
            yield return FailAndQuit(
                "ORT zero-copy CSV did not flush after recording stopped.");
            yield break;
        }

        yield return null;
        yield return null;

        if (!TryCommitSuccessReceipt(overlay, out string receiptError))
        {
            yield return FailAndQuit(receiptError);
            yield break;
        }

        Debug.Log(
            "[KiwiValidation] RECEIPT_COMMITTED path=" +
            _receiptPath);
        Debug.Log("[KiwiValidation] EXIT_REQUESTED code=0");
        yield return null;
        Application.Quit(0);
    }

    private bool TryReadContract(out string error)
    {
        _runId = ReadEnvironment("KIWI_VALIDATION_RUN_ID");
        _caseId = ReadEnvironment("KIWI_VALIDATION_CASE_ID");
        _token = ReadEnvironment("KIWI_VALIDATION_TOKEN");
        _mode = ReadEnvironment("KIWI_VALIDATION_MODE").ToUpperInvariant();
        _purpose = ReadEnvironment("KIWI_VALIDATION_PURPOSE").ToUpperInvariant();

        if (!IsSafeIdentifier(_runId))
        {
            error = "KIWI_VALIDATION_RUN_ID is missing or unsafe.";
            InitializeFallbackReceiptPath();
            return false;
        }

        if (!IsSafeIdentifier(_caseId))
        {
            error = "KIWI_VALIDATION_CASE_ID is missing or unsafe.";
            InitializeFallbackReceiptPath();
            return false;
        }

        if (!IsSafeIdentifier(_token))
        {
            error = "KIWI_VALIDATION_TOKEN is missing or unsafe.";
            InitializeFallbackReceiptPath();
            return false;
        }

        if (_mode != "BASELINE" && _mode != "ZEROCOPY")
        {
            error = "KIWI_VALIDATION_MODE must be BASELINE or ZEROCOPY.";
            InitializeReceiptPath();
            return false;
        }

        if (_purpose != "METRICS" && _purpose != "VISUAL")
        {
            error = "KIWI_VALIDATION_PURPOSE must be METRICS or VISUAL.";
            InitializeReceiptPath();
            return false;
        }

        if (!EnvironmentIsOne("KIWI_AUTO_RECORD"))
        {
            error = "KIWI_AUTO_RECORD=1 is required.";
            InitializeReceiptPath();
            return false;
        }

        if (!EnvironmentIsOne("KIWI_AUTO_QUIT"))
        {
            error = "KIWI_AUTO_QUIT=1 is required.";
            InitializeReceiptPath();
            return false;
        }

        if (
            IsZeroCopyMode() !=
            EnvironmentIsOne("KIWI_ORT_DML_ZERO_COPY_SHADOW"))
        {
            error =
                "KIWI_ORT_DML_ZERO_COPY_SHADOW does not match the requested mode.";
            InitializeReceiptPath();
            return false;
        }

        _warmupSeconds = ReadPositiveSeconds(
            "KIWI_AUTO_RECORD_DELAY_SEC",
            DefaultWarmupSeconds);
        _recordSeconds = ReadPositiveSeconds(
            "KIWI_AUTO_RECORD_DURATION_SEC",
            DefaultRecordSeconds);
        _startupTimeoutSeconds = ReadPositiveSeconds(
            "KIWI_VALIDATION_STARTUP_TIMEOUT_SEC",
            DefaultStartupTimeoutSeconds);

        InitializeReceiptPath();
        if (File.Exists(_receiptPath))
        {
            error =
                "Receipt already exists for this validation token: " +
                _receiptPath;
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool TryValidatePrerequisites(out string error)
    {
        if (
            Application.platform != RuntimePlatform.WindowsPlayer ||
            !Debug.isDebugBuild)
        {
            error = "Windows Development Player is required.";
            return false;
        }

        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
        {
            error =
                "Direct3D12 is required; current=" +
                SystemInfo.graphicsDeviceType;
            return false;
        }

        if (Screen.width != TargetWidth || Screen.height != TargetHeight)
        {
            error =
                "Window must be 1280x720; current=" +
                Screen.width + "x" + Screen.height;
            return false;
        }

        if (!KiwiRuntimeValidationHarness.HasRuntimeInstance)
        {
            error = "Runtime validation harness is not installed.";
            return false;
        }

        if (
            !Mediapipe.Unity.KiwiNativeCameraTelemetry.TryGetSnapshot(
                out Mediapipe.Unity.KiwiNativeCameraTelemetrySnapshot camera))
        {
            error = "Native camera telemetry is not active.";
            return false;
        }

        if (!camera.timestampCalibrationValid)
        {
            error = "Native camera timestamp calibration is not valid.";
            return false;
        }

        if (
            camera.width != CameraWidth ||
            camera.height != CameraHeight ||
            camera.targetFrameRate != CameraFps)
        {
            error =
                "Native camera contract must be 1920x1080@60; current=" +
                camera.width + "x" + camera.height + "@" +
                camera.targetFrameRate;
            return false;
        }

        if (camera.sourceFrameCount == 0 || camera.presentedFrameCount == 0)
        {
            error = "Native camera has not produced and presented a frame.";
            return false;
        }

        ulong canonicalFrameId =
            KiwiCanonicalTrackingFrame.CanonicalFrameId;
        double now = Time.realtimeSinceStartupAsDouble;
        if (canonicalFrameId > _lastObservedCanonicalFrameId)
        {
            _lastObservedCanonicalFrameId = canonicalFrameId;
            _lastCanonicalAdvanceRealtime = now;
        }

        if (canonicalFrameId == 0UL)
        {
            error =
                "Canonical face tracking has not produced a frame.";
            return false;
        }

        if (
            _lastCanonicalAdvanceRealtime < 0.0 ||
            now - _lastCanonicalAdvanceRealtime > 1.0)
        {
            error =
                "Canonical face tracking is stale; frame=" +
                canonicalFrameId +
                " staleSeconds=" +
                (now - _lastCanonicalAdvanceRealtime).ToString(
                    "F3",
                    Invariant);
            return false;
        }

        if (IsZeroCopyMode())
        {
            ZeroCopyStatus zeroCopy = ReadZeroCopyStatus();
            if (!zeroCopy.installed)
            {
                error = "ORT zero-copy runtime is not installed.";
                return false;
            }

            if (zeroCopy.disabled)
            {
                error = "ORT zero-copy runtime disabled itself.";
                return false;
            }

            if (!zeroCopy.ready)
            {
                error =
                    "ORT zero-copy runtime is not ready; attempts=" +
                    zeroCopy.bridgeReadyAttempts;
                return false;
            }
        }
        else if (ReadZeroCopyStatus().installed)
        {
            error = "ORT zero-copy runtime must not exist in BASELINE mode.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool TryCommitSuccessReceipt(
        KiwiFrameComparisonOverlay overlay,
        out string error)
    {
        try
        {
            if (
                KiwiRuntimeValidationHarness.ErrorCount > 0 ||
                KiwiRuntimeValidationHarness.CriticalCount > 0)
            {
                error =
                    "Runtime validation reported error/critical=" +
                    KiwiRuntimeValidationHarness.ErrorCount + "/" +
                    KiwiRuntimeValidationHarness.CriticalCount;
                return false;
            }

            string frameCsv = overlay.CurrentCsvPath;
            if (!File.Exists(frameCsv) || overlay.RecordedFrameCount <= 0)
            {
                error = "Frame comparison CSV is missing or empty: " + frameCsv;
                return false;
            }

            ZeroCopyStatus zeroCopy = ReadZeroCopyStatus();
            string ortCsv =
                IsZeroCopyMode()
                    ? zeroCopy.csvPath
                    : string.Empty;
            if (
                IsZeroCopyMode() &&
                (!File.Exists(ortCsv) || zeroCopy.csvRows <= 0))
            {
                error = "ORT zero-copy CSV is missing or empty: " + ortCsv;
                return false;
            }

            KiwiRuntimeValidationHarness harness =
                FindFirstObjectByType<KiwiRuntimeValidationHarness>();
            if (harness == null)
            {
                error = "Runtime validation instance disappeared before export.";
                return false;
            }

            string runtimeReportPath = Path.Combine(
                _outputDirectory,
                "runtime.json");
            WriteTextAtomically(
                runtimeReportPath,
                harness.BuildJsonReport(true));

            ValidationReceipt receipt = BuildReceipt("OK", string.Empty);
            receipt.frameComparisonCsv =
                DescribeArtifact(frameCsv, overlay.RecordedFrameCount);
            receipt.ortZeroCopyCsv =
                IsZeroCopyMode()
                    ? DescribeArtifact(
                        ortCsv,
                        zeroCopy.csvRows)
                    : null;
            receipt.runtimeValidationJson =
                DescribeArtifact(runtimeReportPath, 0);

            WriteTextAtomically(
                _receiptPath,
                JsonUtility.ToJson(receipt, true));
            _receiptCommitted = true;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = "Receipt commit failed: " + ex;
            return false;
        }
    }

    private IEnumerator FailAndQuit(string error)
    {
        Debug.LogError("[KiwiValidation] FAILED error=" + error);

        KiwiFrameComparisonOverlay overlay =
            KiwiFrameComparisonOverlay.Instance;
        if (overlay != null)
        {
            overlay.StopCsvRecording();
        }

        yield return null;
        yield return null;

        if (!_receiptCommitted)
        {
            try
            {
                if (string.IsNullOrEmpty(_receiptPath))
                {
                    InitializeFallbackReceiptPath();
                }

                ValidationReceipt receipt = BuildReceipt("ERROR", error);
                if (
                    overlay != null &&
                    !string.IsNullOrEmpty(overlay.CurrentCsvPath) &&
                    File.Exists(overlay.CurrentCsvPath))
                {
                    receipt.frameComparisonCsv = DescribeArtifact(
                        overlay.CurrentCsvPath,
                        overlay.RecordedFrameCount);
                }

                ZeroCopyStatus zeroCopy = ReadZeroCopyStatus();
                if (
                    !string.IsNullOrEmpty(zeroCopy.csvPath) &&
                    File.Exists(zeroCopy.csvPath))
                {
                    receipt.ortZeroCopyCsv = DescribeArtifact(
                        zeroCopy.csvPath,
                        zeroCopy.csvRows);
                }

                WriteTextAtomically(
                    _receiptPath,
                    JsonUtility.ToJson(receipt, true));
                _receiptCommitted = true;
                Debug.Log(
                    "[KiwiValidation] RECEIPT_COMMITTED path=" +
                    _receiptPath);
            }
            catch (Exception receiptException)
            {
                Debug.LogError(
                    "[KiwiValidation] ERROR receipt commit failed: " +
                    receiptException);
            }
        }

        Debug.Log("[KiwiValidation] EXIT_REQUESTED code=2");
        yield return null;
        Application.Quit(2);
    }

    private ValidationReceipt BuildReceipt(
        string status,
        string error)
    {
        Mediapipe.Unity.KiwiNativeCameraTelemetrySnapshot camera =
            Mediapipe.Unity.KiwiNativeCameraTelemetry.Latest;

        return new ValidationReceipt
        {
            contract = Contract,
            version = "v42.3",
            status = status,
            error = error ?? string.Empty,
            runId = _runId,
            caseId = _caseId,
            token = _token,
            mode = _mode,
            purpose = _purpose,
            processId =
                System.Diagnostics.Process.GetCurrentProcess().Id,
            graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
            screenWidth = Screen.width,
            screenHeight = Screen.height,
            startedUtc = _startedUtc,
            startupReadyUtc = _startupReadyUtc,
            recordingStartedUtc = _recordingStartedUtc,
            recordingStoppedUtc = _recordingStoppedUtc,
            committedUtc = UtcNow(),
            warmupSeconds = _warmupSeconds,
            requestedRecordSeconds = _recordSeconds,
            runtimeValidationErrors =
                KiwiRuntimeValidationHarness.ErrorCount,
            runtimeValidationCriticals =
                KiwiRuntimeValidationHarness.CriticalCount,
            camera = new CameraReceipt
            {
                active = camera.active,
                width = camera.width,
                height = camera.height,
                targetFrameRate = camera.targetFrameRate,
                timestampCalibrationValid =
                    camera.timestampCalibrationValid,
                sourceFrameCount = camera.sourceFrameCount,
                presentedFrameCount = camera.presentedFrameCount
            }
        };
    }

    private void InitializeReceiptPath()
    {
        _outputDirectory = Path.Combine(
            Application.persistentDataPath,
            "KiwiValidationHarness",
            _token);
        Directory.CreateDirectory(_outputDirectory);
        _receiptPath = Path.Combine(
            _outputDirectory,
            "receipt.json");
    }

    private void InitializeFallbackReceiptPath()
    {
        string fallbackToken =
            IsSafeIdentifier(_token)
                ? _token
                : "invalid_" +
                  DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", Invariant);
        _runId = IsSafeIdentifier(_runId) ? _runId : "invalid-run";
        _caseId = IsSafeIdentifier(_caseId) ? _caseId : "invalid-case";
        _token = fallbackToken;
        InitializeReceiptPath();
    }

    private static ArtifactReceipt DescribeArtifact(
        string path,
        int rows)
    {
        FileInfo file = new FileInfo(path);
        return new ArtifactReceipt
        {
            path = file.FullName,
            bytes = file.Length,
            rows = rows,
            sha256 = ComputeSha256(file.FullName)
        };
    }

    private static string ComputeSha256(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(stream);
            StringBuilder text = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                text.Append(hash[i].ToString("x2", Invariant));
            }

            return text.ToString();
        }
    }

    private static ZeroCopyStatus ReadZeroCopyStatus()
    {
        KiwiOrtDmlZeroCopyRuntime instance =
            FindFirstObjectByType<KiwiOrtDmlZeroCopyRuntime>();
        if (instance == null)
        {
            return default;
        }

        Type type = typeof(KiwiOrtDmlZeroCopyRuntime);
        return new ZeroCopyStatus
        {
            installed = true,
            ready = ReadPrivateField<bool>(type, instance, "_ready"),
            disabled = ReadPrivateField<bool>(type, instance, "_disabled"),
            bridgeReadyAttempts =
                ReadPrivateField<int>(
                    type,
                    instance,
                    "_bridgeReadyAttempts"),
            csvRecording =
                ReadPrivateField<object>(
                    type,
                    instance,
                    "_csvWriter") != null,
            csvPath =
                ReadPrivateField<string>(
                    type,
                    instance,
                    "_csvPath") ?? string.Empty,
            csvRows =
                ReadPrivateField<int>(type, instance, "_csvRows")
        };
    }

    private static T ReadPrivateField<T>(
        Type type,
        object instance,
        string name)
    {
        FieldInfo field = type.GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            throw new MissingFieldException(type.FullName, name);
        }

        object value = field.GetValue(instance);
        return value == null ? default : (T)value;
    }

    private static void WriteTextAtomically(
        string destination,
        string content)
    {
        string directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException(
                "Artifact destination has no directory: " + destination);
        }

        Directory.CreateDirectory(directory);
        string temporary =
            destination + ".tmp." +
            System.Diagnostics.Process.GetCurrentProcess().Id;
        File.WriteAllText(
            temporary,
            content,
            new UTF8Encoding(false));
        File.Move(temporary, destination);
    }

    private bool IsZeroCopyMode()
    {
        return string.Equals(
            _mode,
            "ZEROCOPY",
            StringComparison.Ordinal);
    }

    private static bool IsSafeIdentifier(string value)
    {
        return
            !string.IsNullOrWhiteSpace(value) &&
            SafeIdentifier.IsMatch(value);
    }

    private static string ReadEnvironment(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return value == null ? string.Empty : value.Trim();
    }

    private static bool EnvironmentIsOne(string name)
    {
        return string.Equals(
            ReadEnvironment(name),
            "1",
            StringComparison.Ordinal);
    }

    private static double ReadPositiveSeconds(
        string name,
        double fallback)
    {
        string text = ReadEnvironment(name);
        if (
            double.TryParse(
                text,
                NumberStyles.Float,
                Invariant,
                out double value) &&
            value > 0.0 &&
            value <= 600.0)
        {
            return value;
        }

        return fallback;
    }

    private static string UtcNow()
    {
        return DateTime.UtcNow.ToString("O", Invariant);
    }
}
