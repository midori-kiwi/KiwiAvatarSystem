using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;

[DefaultExecutionOrder(36700)]
internal sealed class KiwiLiveCameraPathDiagnosticV44_23 : MonoBehaviour
{
    private const string Contract =
        "KIWI_V5_1_PHASE16_20_51_V44_23_LIVE_CAMERA_PATH_DIAGNOSTIC";

    private const string EnvironmentVariable =
        "KIWI_PREVIEW_PATH_DIAG";

    private FacePartCropper _cropper;
    private float _nextLogTime;
    private int _lastSourceId = int.MinValue;
    private int _lastPreviewId = int.MinValue;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        string raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!IsEnabled(raw))
        {
            return;
        }

        var go = new GameObject("__KiwiLiveCameraPathDiagnosticV44_23");
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.DontSave;
        go.AddComponent<KiwiLiveCameraPathDiagnosticV44_23>();
#endif
    }

    private void Awake()
    {
        _nextLogTime = 0f;

        Debug.Log(
            $"[KiwiPreviewV44_23] READY contract={Contract} " +
            $"graphicsApi={SystemInfo.graphicsDeviceType} " +
            $"activeColorSpace={QualitySettings.activeColorSpace} " +
            "observerOnly=1 trackingChange=0 cameraChange=0 spoutChange=0");
    }

    private void LateUpdate()
    {
        if (_cropper == null)
        {
            _cropper = FindFirstObjectByType<FacePartCropper>();
        }

        Texture source =
            _cropper != null &&
            _cropper.sourceImage != null
                ? _cropper.sourceImage.texture
                : null;

        Texture preview =
            KiwiCameraPreviewQualityService.GetLivePreviewOrSource(source);

        int sourceId = source != null ? source.GetInstanceID() : 0;
        int previewId = preview != null ? preview.GetInstanceID() : 0;

        bool identityChanged =
            sourceId != _lastSourceId ||
            previewId != _lastPreviewId;

        if (!identityChanged && Time.unscaledTime < _nextLogTime)
        {
            return;
        }

        _lastSourceId = sourceId;
        _lastPreviewId = previewId;
        _nextLogTime = Time.unscaledTime + 2f;

        string relation =
            source == null || preview == null
                ? "UNAVAILABLE"
                : ReferenceEquals(source, preview)
                    ? "DIRECT_SAME_TEXTURE"
                    : "INTERMEDIATE_TEXTURE";

        Debug.Log(
            $"[KiwiPreviewV44_23] relation={relation} " +
            $"source={DescribeTexture(source)} " +
            $"preview={DescribeTexture(preview)}");
    }

    private static string DescribeTexture(Texture texture)
    {
        if (texture == null)
        {
            return "{null}";
        }

        string common =
            $"{{type={texture.GetType().Name}," +
            $"name='{texture.name}'," +
            $"id={texture.GetInstanceID()}," +
            $"size={texture.width}x{texture.height}," +
            $"filter={texture.filterMode}," +
            $"wrap={texture.wrapMode}," +
            $"aniso={texture.anisoLevel}," +
            $"mips={texture.mipmapCount}";

        if (texture is RenderTexture rt)
        {
            RenderTextureDescriptor d = rt.descriptor;

            common +=
                $",rtFormat={rt.format}," +
                $"graphicsFormat={rt.graphicsFormat}," +
                $"descriptorGraphicsFormat={d.graphicsFormat}," +
                $"sRGB={(rt.sRGB ? 1 : 0)}," +
                $"antiAliasing={rt.antiAliasing}," +
                $"useMipMap={(rt.useMipMap ? 1 : 0)}," +
                $"autoGenerateMips={(rt.autoGenerateMips ? 1 : 0)}," +
                $"enableRandomWrite={(rt.enableRandomWrite ? 1 : 0)}," +
                $"dimension={rt.dimension}," +
                $"created={(rt.IsCreated() ? 1 : 0)}";
        }
        else if (texture is Texture2D tex2D)
        {
            common +=
                $",textureFormat={tex2D.format}," +
                $"graphicsFormat={tex2D.graphicsFormat}," +
                $"readable={(tex2D.isReadable ? 1 : 0)}";
        }
        else if (texture is WebCamTexture webCam)
        {
            common +=
                $",requested={webCam.requestedWidth}x{webCam.requestedHeight}@" +
                $"{webCam.requestedFPS:F2}," +
                $"videoRotation={webCam.videoRotationAngle}," +
                $"verticallyMirrored={(webCam.videoVerticallyMirrored ? 1 : 0)}," +
                $"playing={(webCam.isPlaying ? 1 : 0)}";
        }
        else
        {
            common += $",graphicsFormat={texture.graphicsFormat}";
        }

        return common + "}";
    }

    private static bool IsEnabled(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string value = raw.Trim().ToLowerInvariant();
        return
            value == "1" ||
            value == "true" ||
            value == "on" ||
            value == "yes";
    }
}
