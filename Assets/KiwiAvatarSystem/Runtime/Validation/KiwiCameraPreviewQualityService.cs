using System;
using UnityEngine;

/// <summary>
/// Phase16.20.17 v29 camera preview cost A/B/C diagnostic.
///
/// This service is observer-only.
/// It never changes:
/// - Native camera acquisition/upload;
/// - production presentation texture identity;
/// - tracking input;
/// - inference model/ROI/threshold/decode;
/// - FaceTexture transaction;
/// - FacePart textures;
/// - KiwiFaceMotion.
///
/// Mode is selected once per Unity process through:
///   KIWI_CAMERA_PREVIEW_MODE=DIRECT|SINGLE|STAGED
///
/// DIRECT:
///   LIVE/MATCHED -> GUI source directly
///   Added Graphics.Blit count: 0
///
/// SINGLE:
///   LIVE    1920x1080 -> 768x432 -> GUI
///   MATCHED 1280x720  -> 768x432 -> GUI
///   Added Graphics.Blit count: normally 2 / prepared Unity frame
///
/// STAGED:
///   LIVE    1920x1080 -> 960x540 -> 768x432 -> GUI
///   MATCHED 1280x720  -> 768x432 -> GUI
///   Added Graphics.Blit count: normally 3 / prepared Unity frame
///
/// The diagnostic intentionally changes only preview resampling cost.
/// Preview update-rate throttling is deferred until this A/B/C isolates
/// whether additional preview blits cause the observed Unity cadence loss.
/// </summary>
public static class KiwiCameraPreviewQualityService
{
    public const string ContractMarker =
        "KIWI_V5_1_PHASE16_20_16_V28_CAMERA_PREVIEW_QUALITY";

    public const string V29ContractMarker =
        "KIWI_V5_1_PHASE16_20_17_V29_PREVIEW_COST_ABC";

    public const string V44_29ContractMarker =
        "KIWI_V5_1_PHASE16_20_56_V44_29_LIVE_CAMERA_METADATA_CORRECTION_DIAG";

    public const string V44_31ContractMarker =
        "KIWI_V5_1_PHASE16_20_57_V44_31_SRGB_CORRECTION_RT_DIAG";

    private const string PreviewModeEnvironment =
        "KIWI_CAMERA_PREVIEW_MODE";

    private const string ColorCorrectionEnvironment =
        "KIWI_LIVE_CAMERA_COLOR_CORRECTION";

    private const string NativePresentationTextureName =
        "KiwiNativeCameraPresentation";

    private const string CorrectionShaderResourceName =
        "KiwiLiveCameraMetadataCorrectionV44_29";

    private const string CorrectionShaderFallbackName =
        "Hidden/Kiwi/LiveCameraMetadataCorrectionV44_29";

    private const int PreviewTargetWidth = 768;

    public enum PreviewMode
    {
        Direct = 0,
        Single = 1,
        Staged = 2
    }

    public enum LiveCameraColorCorrectionMode
    {
        Off = 0,
        Metadata = 1
    }

    private sealed class Channel
    {
        public RenderTexture intermediate;
        public RenderTexture output;
        public Texture preparedSource;
        public int blitsLastPrepare;
    }

    private static readonly Channel Live = new Channel();
    private static readonly Channel Matched = new Channel();

    private static readonly PreviewMode ConfiguredMode =
        ResolveConfiguredMode();

    private static readonly LiveCameraColorCorrectionMode
        ConfiguredColorCorrection =
            ResolveColorCorrectionMode();

    private static int _preparedUnityFrame = -1;
    private static bool _modeLogged;
    private static bool _colorModeLogged;
    private static bool _colorShaderErrorLogged;
    private static bool _liveColorCorrectionApplied;

    private static Material _colorCorrectionMaterial;

    public static PreviewMode CurrentMode =>
        ConfiguredMode;

    public static int CurrentModeId =>
        (int)ConfiguredMode;

    public static string CurrentModeName =>
        ConfiguredMode.ToString().ToUpperInvariant();

    public static LiveCameraColorCorrectionMode
        CurrentColorCorrectionMode =>
            ConfiguredColorCorrection;

    public static string CurrentColorCorrectionModeName =>
        ConfiguredColorCorrection.ToString().ToUpperInvariant();

    public static bool LiveColorCorrectionApplied =>
        _liveColorCorrectionApplied;

    public static int LiveBlitsLastPrepare =>
        Live.blitsLastPrepare;

    public static int MatchedBlitsLastPrepare =>
        Matched.blitsLastPrepare;

    public static int TotalBlitsLastPrepare =>
        Live.blitsLastPrepare +
        Matched.blitsLastPrepare;

    public static int LivePreviewWidth =>
        Live.output != null
            ? Live.output.width
            : 0;

    public static int LivePreviewHeight =>
        Live.output != null
            ? Live.output.height
            : 0;

    public static int MatchedPreviewWidth =>
        Matched.output != null
            ? Matched.output.width
            : 0;

    public static int MatchedPreviewHeight =>
        Matched.output != null
            ? Matched.output.height
            : 0;

    public static void PrepareFrame(
        Texture liveSource,
        Texture matchedSource)
    {
        if (_preparedUnityFrame == Time.frameCount)
        {
            return;
        }

        _preparedUnityFrame =
            Time.frameCount;

        LogModeOnce();

        if (!PrepareCorrectedLivePreview(liveSource))
        {
            PrepareChannel(
                Live,
                liveSource,
                "KiwiCameraPreviewQuality_Live");
        }

        PrepareChannel(
            Matched,
            matchedSource,
            "KiwiCameraPreviewQuality_Matched");
    }

    public static Texture GetLivePreviewOrSource(
        Texture liveSource)
    {
        if (
            _liveColorCorrectionApplied &&
            liveSource != null &&
            ReferenceEquals(
                Live.preparedSource,
                liveSource) &&
            Live.output != null &&
            Live.output.IsCreated()
        )
        {
            return Live.output;
        }

        if (ConfiguredMode == PreviewMode.Direct)
        {
            return liveSource;
        }

        if (
            liveSource != null &&
            ReferenceEquals(
                Live.preparedSource,
                liveSource) &&
            Live.output != null &&
            Live.output.IsCreated()
        )
        {
            return Live.output;
        }

        return liveSource;
    }

    public static Texture GetMatchedPreviewOrSource(
        Texture matchedSource)
    {
        if (ConfiguredMode == PreviewMode.Direct)
        {
            return matchedSource;
        }

        if (
            matchedSource != null &&
            ReferenceEquals(
                Matched.preparedSource,
                matchedSource) &&
            Matched.output != null &&
            Matched.output.IsCreated()
        )
        {
            return Matched.output;
        }

        return matchedSource;
    }

    public static void Release()
    {
        ReleaseChannel(
            Live);

        ReleaseChannel(
            Matched);

        if (_colorCorrectionMaterial != null)
        {
            UnityEngine.Object.Destroy(
                _colorCorrectionMaterial);

            _colorCorrectionMaterial =
                null;
        }

        _liveColorCorrectionApplied =
            false;

        _preparedUnityFrame =
            -1;

        _modeLogged =
            false;

        _colorModeLogged =
            false;

        _colorShaderErrorLogged =
            false;
    }

    private static LiveCameraColorCorrectionMode
        ResolveColorCorrectionMode()
    {
        string raw =
            Environment.GetEnvironmentVariable(
                ColorCorrectionEnvironment);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return LiveCameraColorCorrectionMode.Off;
        }

        if (
            string.Equals(
                raw.Trim(),
                "METADATA",
                StringComparison.OrdinalIgnoreCase)
        )
        {
            return LiveCameraColorCorrectionMode.Metadata;
        }

        return LiveCameraColorCorrectionMode.Off;
    }

    private static bool PrepareCorrectedLivePreview(
        Texture source)
    {
        _liveColorCorrectionApplied =
            false;

        bool eligible =
            ConfiguredColorCorrection ==
                LiveCameraColorCorrectionMode.Metadata &&
            source != null &&
            string.Equals(
                source.name,
                NativePresentationTextureName,
                StringComparison.Ordinal);

        if (!eligible)
        {
            LogColorModeOnce(
                source,
                false,
                false);

            return false;
        }

        Material material =
            GetOrCreateColorCorrectionMaterial();

        if (material == null)
        {
            LogColorModeOnce(
                source,
                true,
                false);

            return false;
        }

        Live.preparedSource =
            null;

        Live.blitsLastPrepare =
            0;

        ReleaseRenderTexture(
            ref Live.intermediate);

        EnsureColorCorrectionRenderTexture(
            ref Live.output,
            source.width,
            source.height,
            "KiwiCameraPreviewQuality_Live_MetadataCorrected_sRGB");

        Graphics.Blit(
            source,
            Live.output,
            material);

        Live.blitsLastPrepare =
            1;

        Live.preparedSource =
            source;

        _liveColorCorrectionApplied =
            true;

        LogColorModeOnce(
            source,
            true,
            true);

        return true;
    }

    private static Material
        GetOrCreateColorCorrectionMaterial()
    {
        if (_colorCorrectionMaterial != null)
        {
            return _colorCorrectionMaterial;
        }

        Shader shader =
            Resources.Load<Shader>(
                CorrectionShaderResourceName);

        if (shader == null)
        {
            shader =
                Shader.Find(
                    CorrectionShaderFallbackName);
        }

        if (shader == null)
        {
            if (!_colorShaderErrorLogged)
            {
                _colorShaderErrorLogged =
                    true;

                Debug.LogError(
                    "[KiwiLiveCameraColorV44_29] " +
                    "shaderMissing=1 correctionDisabled=1");
            }

            return null;
        }

        _colorCorrectionMaterial =
            new Material(shader)
            {
                name =
                    "KiwiLiveCameraMetadataCorrectionV44_29_Material",
                hideFlags =
                    HideFlags.HideAndDontSave
            };

        return _colorCorrectionMaterial;
    }

    private static void LogColorModeOnce(
        Texture source,
        bool eligible,
        bool applied)
    {
        if (
            _colorModeLogged ||
            source == null)
        {
            return;
        }

        _colorModeLogged =
            true;

        Debug.Log(
            "[KiwiLiveCameraColorV44_29] " +
            "contract=" +
            V44_29ContractMarker +
            " mode=" +
            CurrentColorCorrectionModeName +
            " source=" +
            source.name +
            " size=" +
            source.width +
            "x" +
            source.height +
            " eligibleNativePresentation=" +
            (eligible ? "1" : "0") +
            " applied=" +
            (applied ? "1" : "0") +
            " addedLivePreviewBlit=" +
            (applied ? "1" : "0") +
            " correctedOutputSrgb=" +
            (
                Live.output != null &&
                Live.output.IsCreated() &&
                Live.output.sRGB
                    ? "1"
                    : "0"
            ) +
            " correctedOutputFormat=" +
            (
                Live.output != null
                    ? Live.output.graphicsFormat.ToString()
                    : "NONE"
            ) +
            " v44_31=" +
            V44_31ContractMarker +
            " trackingInputChanged=0" +
            " nativeDllChanged=0" +
            " spoutChanged=0");
    }

    private static PreviewMode ResolveConfiguredMode()
    {
        string raw =
            Environment.GetEnvironmentVariable(
                PreviewModeEnvironment);

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Diagnostic-safe default: DIRECT restores the pre-v28 preview
            // path and adds no observer blits.
            return PreviewMode.Direct;
        }

        string normalized =
            raw.Trim();

        if (
            string.Equals(
                normalized,
                "SINGLE",
                StringComparison.OrdinalIgnoreCase)
        )
        {
            return PreviewMode.Single;
        }

        if (
            string.Equals(
                normalized,
                "STAGED",
                StringComparison.OrdinalIgnoreCase)
        )
        {
            return PreviewMode.Staged;
        }

        return PreviewMode.Direct;
    }

    private static void LogModeOnce()
    {
        if (_modeLogged)
        {
            return;
        }

        _modeLogged =
            true;

        Debug.Log(
            "[KiwiPreviewCostABC] " +
            "mode=" +
            CurrentModeName +
            " targetWidth=" +
            PreviewTargetWidth +
            " observerOnly=1");
    }

    private static void PrepareChannel(
        Channel channel,
        Texture source,
        string baseName)
    {
        channel.preparedSource =
            null;

        channel.blitsLastPrepare =
            0;

        if (
            source == null ||
            source.width <= 0 ||
            source.height <= 0
        )
        {
            return;
        }

        if (ConfiguredMode == PreviewMode.Direct)
        {
            // No observer RTs and no observer blits in DIRECT.
            ReleaseRenderTexture(
                ref channel.intermediate);

            ReleaseRenderTexture(
                ref channel.output);

            channel.preparedSource =
                source;

            return;
        }

        if (source.width <= PreviewTargetWidth)
        {
            ReleaseRenderTexture(
                ref channel.intermediate);

            ReleaseRenderTexture(
                ref channel.output);

            channel.preparedSource =
                source;

            return;
        }

        int targetWidth =
            Mathf.Min(
                PreviewTargetWidth,
                source.width);

        int targetHeight =
            Mathf.Max(
                1,
                Mathf.RoundToInt(
                    targetWidth *
                    source.height /
                    (float)Mathf.Max(
                        1,
                        source.width)));

        EnsureRenderTexture(
            ref channel.output,
            targetWidth,
            targetHeight,
            source,
            baseName + "_768");

        if (ConfiguredMode == PreviewMode.Single)
        {
            ReleaseRenderTexture(
                ref channel.intermediate);

            Graphics.Blit(
                source,
                channel.output);

            channel.blitsLastPrepare =
                1;

            channel.preparedSource =
                source;

            return;
        }

        // STAGED reproduces the v28/v28.8 observer-quality path:
        // use an intermediate only for sources at least 2x target width.
        bool useIntermediate =
            source.width >=
            PreviewTargetWidth * 2;

        if (useIntermediate)
        {
            int intermediateWidth =
                Mathf.Max(
                    targetWidth,
                    source.width / 2);

            int intermediateHeight =
                Mathf.Max(
                    1,
                    Mathf.RoundToInt(
                        intermediateWidth *
                        source.height /
                        (float)Mathf.Max(
                            1,
                            source.width)));

            EnsureRenderTexture(
                ref channel.intermediate,
                intermediateWidth,
                intermediateHeight,
                source,
                baseName + "_Half");

            Graphics.Blit(
                source,
                channel.intermediate);

            Graphics.Blit(
                channel.intermediate,
                channel.output);

            channel.blitsLastPrepare =
                2;
        }
        else
        {
            ReleaseRenderTexture(
                ref channel.intermediate);

            Graphics.Blit(
                source,
                channel.output);

            channel.blitsLastPrepare =
                1;
        }

        channel.preparedSource =
            source;
    }

    private static void EnsureColorCorrectionRenderTexture(
        ref RenderTexture texture,
        int width,
        int height,
        string name)
    {
        if (
            texture != null &&
            texture.width == width &&
            texture.height == height &&
            texture.sRGB &&
            texture.IsCreated()
        )
        {
            return;
        }

        ReleaseRenderTexture(
            ref texture);

        texture =
            new RenderTexture(
                width,
                height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB)
            {
                name = name,
                filterMode =
                    FilterMode.Bilinear,
                wrapMode =
                    TextureWrapMode.Clamp,
                hideFlags =
                    HideFlags.DontSave,
                useMipMap =
                    false,
                autoGenerateMips =
                    false
            };

        texture.Create();

        if (!texture.sRGB)
        {
            Debug.LogError(
                "[KiwiLiveCameraColorV44_31] " +
                "correctedOutputSrgb=0 " +
                "expected=1 " +
                "correctionRtCreationFailed=1");
        }
    }

    private static void EnsureRenderTexture(
        ref RenderTexture texture,
        int width,
        int height,
        Texture source,
        string name)
    {
        if (
            texture != null &&
            texture.width == width &&
            texture.height == height &&
            texture.graphicsFormat ==
                source.graphicsFormat &&
            texture.IsCreated()
        )
        {
            return;
        }

        ReleaseRenderTexture(
            ref texture);

        RenderTextureDescriptor descriptor =
            new RenderTextureDescriptor(
                width,
                height)
            {
                graphicsFormat =
                    source.graphicsFormat,
                depthBufferBits = 0,
                msaaSamples = 1,
                volumeDepth = 1,
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = false
            };

        texture =
            new RenderTexture(
                descriptor)
            {
                name = name,
                filterMode =
                    FilterMode.Bilinear,
                wrapMode =
                    TextureWrapMode.Clamp,
                hideFlags =
                    HideFlags.DontSave
            };

        texture.Create();
    }

    private static void ReleaseChannel(
        Channel channel)
    {
        if (channel == null)
        {
            return;
        }

        channel.preparedSource =
            null;

        channel.blitsLastPrepare =
            0;

        ReleaseRenderTexture(
            ref channel.intermediate);

        ReleaseRenderTexture(
            ref channel.output);
    }

    private static void ReleaseRenderTexture(
        ref RenderTexture texture)
    {
        if (texture == null)
        {
            return;
        }

        if (texture.IsCreated())
        {
            texture.Release();
        }

        UnityEngine.Object.Destroy(
            texture);

        texture =
            null;
    }
}
