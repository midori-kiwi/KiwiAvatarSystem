using UnityEngine;

/// <summary>
/// Phase16.20.16 diagnostic-preview-only camera minification service.
///
/// It never changes the production camera texture, Native camera resources,
/// tracking input, FaceTexture transaction, FacePart textures, or model input.
/// It only creates bounded observer copies for the on-screen LIVE CAMERA and
/// MATCHED LANDMARK DEBUG previews.
///
/// 1920x1080 live input:
///   1920x1080 -> 960x540 -> 768x432 -> GUI preview
///
/// 1280x720 matched snapshot:
///   1280x720 -> 768x432 -> GUI preview
///
/// The second observer copy is intentionally larger than the overlay's maximum
/// 560-pixel display width, so GUI never needs to magnify the quality texture.
/// </summary>
public static class KiwiCameraPreviewQualityService
{
    public const string ContractMarker =
        "KIWI_V5_1_PHASE16_20_16_V28_CAMERA_PREVIEW_QUALITY";

    private const int PreviewTargetWidth = 768;

    private sealed class Channel
    {
        public RenderTexture intermediate;
        public RenderTexture output;
        public Texture preparedSource;
    }

    private static readonly Channel Live = new Channel();
    private static readonly Channel Matched = new Channel();

    private static int _preparedUnityFrame = -1;

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
    }

    private static void PrepareChannel(
        Channel channel,
        Texture source,
        string baseName)
    {
        channel.preparedSource =
            null;

        if (
            source == null ||
            source.width <= 0 ||
            source.height <= 0
        )
        {
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

        bool useIntermediate =
            source.width >=
            PreviewTargetWidth * 2;

        int intermediateWidth =
            useIntermediate
                ? Mathf.Max(
                    targetWidth,
                    source.width / 2)
                : targetWidth;

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
            ref channel.output,
            targetWidth,
            targetHeight,
            source,
            baseName + "_768");

        if (useIntermediate)
        {
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
        }
        else
        {
            ReleaseRenderTexture(
                ref channel.intermediate);

            Graphics.Blit(
                source,
                channel.output);
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

        Object.Destroy(
            texture);

        texture =
            null;
    }
}
