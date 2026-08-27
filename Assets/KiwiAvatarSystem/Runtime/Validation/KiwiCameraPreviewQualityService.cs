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

    private const string PreviewModeEnvironment =
        "KIWI_CAMERA_PREVIEW_MODE";

    private const int PreviewTargetWidth = 768;

    public enum PreviewMode
    {
        Direct = 0,
        Single = 1,
        Staged = 2
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

    private static int _preparedUnityFrame = -1;
    private static bool _modeLogged;

    public static PreviewMode CurrentMode =>
        ConfiguredMode;

    public static int CurrentModeId =>
        (int)ConfiguredMode;

    public static string CurrentModeName =>
        ConfiguredMode.ToString().ToUpperInvariant();

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

        PrepareChannel(
            Live,
            liveSource,
            "KiwiCameraPreviewQuality_Live");

        PrepareChannel(
            Matched,
            matchedSource,
            "KiwiCameraPreviewQuality_Matched");
    }

    public static Texture GetLivePreviewOrSource(
        Texture liveSource)
    {
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

        _preparedUnityFrame =
            -1;

        _modeLogged =
            false;
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
