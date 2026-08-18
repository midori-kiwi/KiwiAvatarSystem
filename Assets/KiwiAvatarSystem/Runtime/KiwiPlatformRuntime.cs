using System.Collections;
using UnityEngine;

[DefaultExecutionOrder(-20000)]
[DisallowMultipleComponent]
public sealed class KiwiPlatformRuntime : MonoBehaviour
{
    [Header("Mobile")]
    public Camera vtuberCamera;
    [Range(30, 120)] public int mobileTargetFrameRate = 60;
    public bool keepScreenAwake = true;
    public bool showMobilePreview = true;
    public bool useOptimizedMobileOutput = true;
    public int mobileOutputWidth = 1280;
    public int mobileOutputHeight = 720;

    [Header("Adaptive Mobile Memory")]
    public bool adaptiveLowMemoryOutput = true;
    [Range(1024, 8192)] public int lowMemoryDeviceThresholdMB = 3072;
    public int lowMemoryOutputWidth = 960;
    public int lowMemoryOutputHeight = 540;

    [Header("Windows-only output")]
    public GameObject spoutOutput;

    [Header("Runtime Debug")]
    [SerializeField] private int activeOutputWidth;
    [SerializeField] private int activeOutputHeight;
    [SerializeField] private bool lowMemoryMode;

    private RenderTexture _desktopOrOriginalTarget;
    private RenderTexture _mobileTarget;
    private int _previousVSyncCount;
    private int _previousTargetFrameRate;
    private int _previousSleepTimeout;
    private bool _capturedRuntimeSettings;
    private bool _lowMemorySubscribed;

    private bool IsMobileRuntime
    {
        get
        {
#if UNITY_ANDROID || UNITY_IOS
            return !Application.isEditor;
#else
            return false;
#endif
        }
    }

    private void Awake()
    {
        if (!IsMobileRuntime)
        {
            return;
        }

        ResolveReferences();
        CaptureRuntimeSettings();

        // Spout is a Windows/DirectX output path. Prevent it from starting on mobile.
        if (spoutOutput != null)
        {
            spoutOutput.SetActive(false);
        }

        Application.lowMemory += HandleLowMemory;
        _lowMemorySubscribed = true;

        ApplyMobileRuntimeSettings();
        ConfigureMobileOutput();
        EnsurePreview();
    }

    private IEnumerator Start()
    {
        if (!IsMobileRuntime)
        {
            yield break;
        }

        // KiwiStandaloneRuntime may also set frame-rate settings in Start.
        // Re-apply one frame later so mobile's thermal-friendly target wins
        // without changing the Windows standalone script.
        yield return null;
        ApplyMobileRuntimeSettings();
    }

    private void OnDestroy()
    {
        if (_lowMemorySubscribed)
        {
            Application.lowMemory -= HandleLowMemory;
            _lowMemorySubscribed = false;
        }

        if (_capturedRuntimeSettings)
        {
            QualitySettings.vSyncCount = _previousVSyncCount;
            Application.targetFrameRate = _previousTargetFrameRate;
            Screen.sleepTimeout = _previousSleepTimeout;
        }

        ReleaseMobileTarget(true);
    }

    private void ResolveReferences()
    {
        if (spoutOutput == null)
        {
            spoutOutput = GameObject.Find("SpoutOutput");
        }

        if (vtuberCamera == null)
        {
            GameObject cameraObject = GameObject.Find("VTuberCamera");
            if (cameraObject != null)
            {
                vtuberCamera = cameraObject.GetComponent<Camera>();
            }
        }
    }

    private void CaptureRuntimeSettings()
    {
        _previousVSyncCount = QualitySettings.vSyncCount;
        _previousTargetFrameRate = Application.targetFrameRate;
        _previousSleepTimeout = Screen.sleepTimeout;
        _capturedRuntimeSettings = true;
    }

    private void ApplyMobileRuntimeSettings()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = Mathf.Clamp(mobileTargetFrameRate, 30, 120);

        if (keepScreenAwake)
        {
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }
    }

    private void ConfigureMobileOutput()
    {
        if (!useOptimizedMobileOutput || vtuberCamera == null)
        {
            return;
        }

        _desktopOrOriginalTarget = vtuberCamera.targetTexture;

        bool startLow = adaptiveLowMemoryOutput &&
            SystemInfo.systemMemorySize > 0 &&
            SystemInfo.systemMemorySize <= lowMemoryDeviceThresholdMB;

        int width = startLow ? lowMemoryOutputWidth : mobileOutputWidth;
        int height = startLow ? lowMemoryOutputHeight : mobileOutputHeight;
        ReplaceMobileTarget(width, height, startLow);
    }

    private void HandleLowMemory()
    {
        if (!IsMobileRuntime || !adaptiveLowMemoryOutput || lowMemoryMode)
        {
            return;
        }

        if (!ReplaceMobileTarget(
            lowMemoryOutputWidth,
            lowMemoryOutputHeight,
            true))
        {
            return;
        }

        Debug.LogWarning(
            "[KiwiAvatarSystem] Low-memory signal received. " +
            "Mobile VTuber output was reduced while preserving the 16:9 camera mapping."
        );
    }

    private bool ReplaceMobileTarget(
        int requestedWidth,
        int requestedHeight,
        bool requestedLowMemoryMode)
    {
        if (vtuberCamera == null)
        {
            return false;
        }

        int width = Mathf.Clamp(requestedWidth, 640, 1920);
        int height = Mathf.Clamp(requestedHeight, 360, 1080);

        // Preserve the established 16:9 mapping. Resolution may change, composition may not.
        float aspect = (float)width / Mathf.Max(1, height);
        if (Mathf.Abs(aspect - (16f / 9f)) > 0.02f)
        {
            if (requestedLowMemoryMode)
            {
                width = 960;
                height = 540;
            }
            else
            {
                width = 1280;
                height = 720;
            }
        }

        if (_mobileTarget != null &&
            _mobileTarget.width == width &&
            _mobileTarget.height == height &&
            _mobileTarget.IsCreated())
        {
            lowMemoryMode = requestedLowMemoryMode;
            return true;
        }

        RenderTexture replacement = new RenderTexture(
            width,
            height,
            0,
            RenderTextureFormat.ARGB32)
        {
            name = requestedLowMemoryMode
                ? "KiwiVTuber_MobileOutput_LowMemory"
                : "KiwiVTuber_MobileOutput",
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false,
        };

        replacement.Create();
        if (!replacement.IsCreated())
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Mobile RenderTexture allocation failed. " +
                "Keeping the existing VTuberCamera target."
            );
            Destroy(replacement);
            return false;
        }

        RenderTexture previous = _mobileTarget;
        _mobileTarget = replacement;
        vtuberCamera.targetTexture = _mobileTarget;
        lowMemoryMode = requestedLowMemoryMode;
        activeOutputWidth = width;
        activeOutputHeight = height;

        if (previous != null)
        {
            previous.Release();
            Destroy(previous);
        }

        return true;
    }

    private void ReleaseMobileTarget(bool restoreOriginal)
    {
        if (_mobileTarget == null)
        {
            return;
        }

        if (restoreOriginal && vtuberCamera != null && vtuberCamera.targetTexture == _mobileTarget)
        {
            vtuberCamera.targetTexture = _desktopOrOriginalTarget;
        }

        _mobileTarget.Release();
        Destroy(_mobileTarget);
        _mobileTarget = null;
        activeOutputWidth = 0;
        activeOutputHeight = 0;
    }

    private void EnsurePreview()
    {
        if (!showMobilePreview)
        {
            return;
        }

        KiwiMobilePreview preview = GetComponent<KiwiMobilePreview>();
        if (preview == null)
        {
            preview = gameObject.AddComponent<KiwiMobilePreview>();
        }

        preview.vtuberCamera = vtuberCamera;
    }
}
