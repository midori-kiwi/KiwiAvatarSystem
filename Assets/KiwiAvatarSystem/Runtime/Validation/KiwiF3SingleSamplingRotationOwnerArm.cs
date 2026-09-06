using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Change-local, opt-in F3 arm and scalar observer.
///
/// With KIWI_F3_SAMPLING_ROTATION_ARM=CONTROL this preserves the current
/// SharedTilt sampling writer. With PROBE it keeps the component enabled and
/// its legacy FacePartAngleLock suppression active, but selects the existing
/// enableSharedTiltLock=false neutral branch. No Scene or Product default is
/// changed when the environment variable is absent.
/// </summary>
[DefaultExecutionOrder(32000)]
[DisallowMultipleComponent]
public sealed class KiwiF3SingleSamplingRotationOwnerArm : MonoBehaviour
{
    private const string ArmEnvironment =
        "KIWI_F3_SAMPLING_ROTATION_ARM";
    private const string EvidenceEnvironment =
        "KIWI_F3_EVIDENCE_DIR";
    private const string RuntimeObjectName =
        "[Kiwi] F3 Single Sampling Rotation Owner Arm";
    private const string ArmControl = "CONTROL";
    private const string ArmProbe = "PROBE";
    private const int MaximumSamples = 1200;
    private const float SamplePeriodSeconds = 0.10f;

    private static readonly int SampleRotationRadId =
        Shader.PropertyToID("_SampleRotationRad");

    private string _arm;
    private KiwiFacePartSharedTiltLock _shared;
    private FacePartCropper _cropper;
    private FacePartAngleLock[] _angleLocks =
        Array.Empty<FacePartAngleLock>();
    private StreamWriter _writer;
    private float _nextSampleAt;
    private int _sampleIndex;
    private float _lastLockRefreshAt;
    private static string _pendingArm;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        string raw =
            Environment.GetEnvironmentVariable(ArmEnvironment);

        if (string.IsNullOrWhiteSpace(raw))
            return;

        string arm = raw.Trim().ToUpperInvariant();

        if (arm != ArmControl && arm != ArmProbe)
        {
            Debug.LogError(
                "[F3_SINGLE_SAMPLING_ROTATION_OWNER] invalid arm='" +
                raw + "'. Expected CONTROL or PROBE; no arm installed.");
            return;
        }

        if (
            FindFirstObjectByType<KiwiF3SingleSamplingRotationOwnerArm>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        _pendingArm = arm;
        GameObject host = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiF3SingleSamplingRotationOwnerArm>();
        _pendingArm = null;
    }

    private void Awake()
    {
        if (string.IsNullOrWhiteSpace(_arm))
            _arm = _pendingArm;

        DontDestroyOnLoad(gameObject);
        RefreshReferences(true);

        if (_shared != null)
        {
            // This is a sampling-contribution arm, not component disable.
            // The existing false branch still suppresses FacePartAngleLock and
            // writes neutral shader state every LateUpdate.
            _shared.enableSharedTiltLock =
                _arm == ArmControl;
        }

        OpenEvidence();

        Debug.Log(
            "[F3_SINGLE_SAMPLING_ROTATION_OWNER] " +
            "ARM_SELECTED=" + _arm +
            " SHARED_COMPONENT_ENABLED=" +
            (_shared != null && _shared.enabled ? "1" : "0") +
            " SHARED_SAMPLING_ENABLED=" +
            (_shared != null && _shared.enableSharedTiltLock ? "1" : "0") +
            " RIGID_UNCHANGED=1" +
            " ANGLE_LOCK_SUPPRESSION_SAME=1" +
            " PRODUCT_DEFAULT_PRESERVED=1");
    }

    private void OnDestroy()
    {
        if (_writer != null)
        {
            _writer.Flush();
            _writer.Dispose();
            _writer = null;
        }
    }

    private void LateUpdate()
    {
        if (_writer == null || _sampleIndex >= MaximumSamples)
            return;

        if (Time.unscaledTime < _nextSampleAt)
            return;

        _nextSampleAt =
            Time.unscaledTime + SamplePeriodSeconds;

        RefreshReferences(false);

        if (Time.unscaledTime - _lastLockRefreshAt >= 1f)
        {
            _angleLocks =
                FindObjectsByType<FacePartAngleLock>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
            _lastLockRefreshAt = Time.unscaledTime;
        }

        WriteSample();
    }

    private void RefreshReferences(bool force)
    {
        if (force || _shared == null)
        {
            _shared =
                FindFirstObjectByType<KiwiFacePartSharedTiltLock>(
                    FindObjectsInactive.Include);

            if (_shared != null)
            {
                _shared.enableSharedTiltLock =
                    _arm == ArmControl;
            }
        }

        if (force || _cropper == null)
        {
            _cropper =
                FindFirstObjectByType<FacePartCropper>(
                    FindObjectsInactive.Include);
        }
    }

    private void OpenEvidence()
    {
        string directory =
            Environment.GetEnvironmentVariable(EvidenceEnvironment);

        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                "F3_SingleSamplingRotationOwner_" +
                _arm + ".csv");
            _writer = new StreamWriter(
                new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read),
                new UTF8Encoding(false));
            _writer.WriteLine(
                "sampleIndex,timeSec,arm,canonicalFrameId," +
                "cameraGeneration,trackingSessionGeneration," +
                "rigidOperational,rigidEyeLineDeg,rigidAppliedDeg," +
                "sharedComponentEnabled,sharedSamplingEnabled," +
                "leftShaderRotationRad,rightShaderRotationRad," +
                "mouthShaderRotationRad,angleLockEnabledCount," +
                "cropperFound,sourceTextureFound");
            _writer.Flush();
            Debug.Log(
                "[F3_SINGLE_SAMPLING_ROTATION_OWNER] EVIDENCE=" +
                path + " MAX_SAMPLES=" + MaximumSamples);
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[F3_SINGLE_SAMPLING_ROTATION_OWNER] evidence open failed: " +
                exception.Message);
        }
    }

    private void WriteSample()
    {
        if (_writer == null)
            return;

        int enabledLocks = 0;
        if (_angleLocks != null)
        {
            for (int i = 0; i < _angleLocks.Length; i++)
            {
                if (_angleLocks[i] != null &&
                    _angleLocks[i].enableAngleLock)
                {
                    enabledLocks++;
                }
            }
        }

        SurfaceFittedRawImage left =
            _cropper != null
                ? _cropper.leftEyeImage as SurfaceFittedRawImage
                : null;
        SurfaceFittedRawImage right =
            _cropper != null
                ? _cropper.rightEyeImage as SurfaceFittedRawImage
                : null;
        SurfaceFittedRawImage mouth =
            _cropper != null
                ? _cropper.mouthImage as SurfaceFittedRawImage
                : null;

        _writer.WriteLine(
            _sampleIndex.ToString(CultureInfo.InvariantCulture) + "," +
            Time.unscaledTime.ToString("F3", CultureInfo.InvariantCulture) + "," +
            _arm + "," +
            KiwiCanonicalTrackingFrame.CanonicalFrameId.ToString(CultureInfo.InvariantCulture) + "," +
            KiwiRuntimeGenerationContext.CameraGeneration.ToString(CultureInfo.InvariantCulture) + "," +
            KiwiRuntimeGenerationContext.TrackingSessionGeneration.ToString(CultureInfo.InvariantCulture) + "," +
            (KiwiFacePartRigidSampleFrame.IsOperational ? "1" : "0") + "," +
            KiwiFacePartRigidSampleFrame.EyeLineAngleDegrees.ToString("F4", CultureInfo.InvariantCulture) + "," +
            KiwiFacePartRigidSampleFrame.AppliedRotationDegrees.ToString("F4", CultureInfo.InvariantCulture) + "," +
            (_shared != null && _shared.enabled ? "1" : "0") + "," +
            (_shared != null && _shared.enableSharedTiltLock ? "1" : "0") + "," +
            ShaderRotation(left) + "," +
            ShaderRotation(right) + "," +
            ShaderRotation(mouth) + "," +
            enabledLocks.ToString(CultureInfo.InvariantCulture) + "," +
            (_cropper != null ? "1" : "0") + "," +
            (_cropper != null && _cropper.sourceImage != null &&
             _cropper.sourceImage.texture != null ? "1" : "0"));
        _writer.Flush();
        _sampleIndex++;
    }

    private static string ShaderRotation(
        SurfaceFittedRawImage image)
    {
        if (image == null || image.material == null ||
            !image.material.HasProperty(SampleRotationRadId))
        {
            return "NaN";
        }

        return image.material.GetFloat(SampleRotationRadId)
            .ToString("F7", CultureInfo.InvariantCulture);
    }
}
