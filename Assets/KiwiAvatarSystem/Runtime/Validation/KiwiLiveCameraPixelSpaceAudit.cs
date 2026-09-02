using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// Opt-in, observer-only LIVE CAMERA pixel-space comparator.
///
/// Enable with KIWI_LIVE_CAMERA_PIXEL_SPACE_AUDIT=1. The comparator reads the
/// source already bound to FacePartCropper and the preview Texture already
/// selected by KiwiCameraPreviewQualityService. It does not prepare a frame,
/// create a camera sample, allocate a RenderTexture, Blit, read back, or write
/// Tracking / Camera / Inference / FaceTexture transaction state.
///
/// Both views below use the same local Texture reference in the same OnGUI
/// repaint:
/// A) full-frame, with the current Production overlay's nominal draw aspect;
/// B) a centered crop whose source-texel extent equals its screen-pixel extent.
/// </summary>
[DefaultExecutionOrder(36800)]
[DisallowMultipleComponent]
internal sealed class KiwiLiveCameraPixelSpaceAudit : MonoBehaviour
{
    private const string RuntimeObjectName =
        "__KiwiLiveCameraPixelSpaceAudit";

    private const string EnvironmentVariable =
        "KIWI_LIVE_CAMERA_PIXEL_SPACE_AUDIT";

    private const string Contract =
        "KIWI_LIVE_CAMERA_PRESENTATION_PIXEL_SPACE_AUDIT_V1";

    private const float DefaultPreviewPlanningWidth = 560f;
    private const float MetricsHeight = 176f;
    private const float Margin = 10f;
    private const float PaneGap = 10f;
    private const float PanelPadding = 8f;
    private const float TitleHeight = 24f;
    private const float LogIntervalSeconds = 2f;

    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private static KiwiLiveCameraPixelSpaceAudit _instance;

    private FacePartCropper _cropper;
    private GUIStyle _titleStyle;
    private GUIStyle _metricsStyle;
    private string _metricsText =
        "Waiting for LIVE CAMERA source Texture...";
    private float _nextLogTime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (!IsEnabled(
                Environment.GetEnvironmentVariable(
                    EnvironmentVariable)))
        {
            return;
        }

        KiwiLiveCameraPixelSpaceAudit existing =
            FindFirstObjectByType<KiwiLiveCameraPixelSpaceAudit>();

        if (existing != null)
        {
            _instance = existing;
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName)
            {
                hideFlags = HideFlags.DontSave
            };

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiLiveCameraPixelSpaceAudit>();
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
        _nextLogTime = 0f;

        Debug.Log(
            "[KiwiLiveCameraPixelSpaceAudit] " +
            "READY contract=" + Contract +
            " observerOnly=1 additionalBlit=0 additionalReadback=0 " +
            "trackingWrite=0 cameraWrite=0 inferenceWrite=0 " +
            "faceTextureTransactionWrite=0");
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void OnGUI()
    {
        if (
            Event.current == null ||
            Event.current.type != EventType.Repaint)
        {
            return;
        }

        EnsureStyles();

        if (_cropper == null)
        {
            _cropper = FindFirstObjectByType<FacePartCropper>();
        }

        Texture sourceTexture =
            _cropper != null &&
            _cropper.sourceImage != null
                ? _cropper.sourceImage.texture
                : null;

        Texture comparisonTexture =
            KiwiCameraPreviewQualityService.
                GetLivePreviewOrSource(sourceTexture);

        float sourceAspect =
            sourceTexture != null &&
            sourceTexture.height > 0
                ? sourceTexture.width /
                    (float)sourceTexture.height
                : 16f / 9f;

        KiwiFrameComparisonOverlay overlay =
            KiwiFrameComparisonOverlay.Instance;

        float productionPlanningWidth =
            Mathf.Clamp(
                overlay != null
                    ? overlay.previewWidth
                    : DefaultPreviewPlanningWidth,
                260f,
                560f);

        float productionDesiredHeight =
            Mathf.Clamp(
                Mathf.Round(
                    productionPlanningWidth /
                        Mathf.Max(0.25f, sourceAspect)),
                150f,
                330f);

        // Production reserves one 8px padding lane on each side. On a normal
        // window, the final pixel-aligned content width is planningWidth.
        float productionNominalDrawWidth =
            Mathf.Max(
                1f,
                Mathf.Floor(
                    productionPlanningWidth));

        float productionNominalDrawAspect =
            productionNominalDrawWidth /
            Mathf.Max(1f, productionDesiredHeight);

        float availablePairWidth =
            Mathf.Max(
                2f,
                Screen.width -
                Margin * 2f -
                PanelPadding * 2f -
                PaneGap);

        int drawRectWidth =
            Mathf.Max(
                1,
                Mathf.FloorToInt(
                    Mathf.Min(
                        productionNominalDrawWidth,
                        availablePairWidth * 0.5f)));

        int drawRectHeight =
            Mathf.Max(
                1,
                Mathf.RoundToInt(
                    drawRectWidth /
                    Mathf.Max(
                        0.25f,
                        productionNominalDrawAspect)));

        if (comparisonTexture != null)
        {
            drawRectWidth =
                Mathf.Min(
                    drawRectWidth,
                    Mathf.Max(1, comparisonTexture.width));

            drawRectHeight =
                Mathf.Min(
                    drawRectHeight,
                    Mathf.Max(1, comparisonTexture.height));
        }

        float panelWidth =
            PanelPadding * 2f +
            drawRectWidth * 2f +
            PaneGap;

        float panelHeight =
            PanelPadding * 2f +
            TitleHeight +
            drawRectHeight +
            MetricsHeight;

        float panelX =
            Mathf.Floor(
                (Screen.width - panelWidth) * 0.5f);

        float panelY =
            Mathf.Max(
                Margin,
                Mathf.Floor(
                    Screen.height -
                    panelHeight -
                    Margin));

        Rect panelRect =
            new Rect(
                panelX,
                panelY,
                panelWidth,
                panelHeight);

        Rect fullRect =
            new Rect(
                panelRect.x + PanelPadding,
                panelRect.y +
                    PanelPadding +
                    TitleHeight,
                drawRectWidth,
                drawRectHeight);

        Rect cropRect =
            new Rect(
                fullRect.xMax + PaneGap,
                fullRect.y,
                drawRectWidth,
                drawRectHeight);

        Color oldColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.88f);
        GUI.Box(panelRect, GUIContent.none);
        GUI.color = oldColor;

        GUI.Label(
            new Rect(
                fullRect.x,
                panelRect.y + PanelPadding,
                fullRect.width,
                TitleHeight),
            "A  FULL FRAME (current presentation Texture)",
            _titleStyle);

        GUI.Label(
            new Rect(
                cropRect.x,
                panelRect.y + PanelPadding,
                cropRect.width,
                TitleHeight),
            "B  CENTER 1:1 TEXEL CROP",
            _titleStyle);

        if (comparisonTexture == null)
        {
            GUI.Label(
                fullRect,
                "LIVE CAMERA Texture unavailable",
                _metricsStyle);

            GUI.Label(
                cropRect,
                "LIVE CAMERA Texture unavailable",
                _metricsStyle);
        }
        else
        {
            bool mirrorX =
                _cropper != null &&
                _cropper.mirrorX;

            Rect fullTexCoords =
                mirrorX
                    ? new Rect(1f, 0f, -1f, 1f)
                    : new Rect(0f, 0f, 1f, 1f);

            int cropTexelWidth =
                Mathf.Min(
                    drawRectWidth,
                    comparisonTexture.width);

            int cropTexelHeight =
                Mathf.Min(
                    drawRectHeight,
                    comparisonTexture.height);

            int cropTexelX =
                Mathf.Max(
                    0,
                    (comparisonTexture.width -
                     cropTexelWidth) / 2);

            int cropTexelY =
                Mathf.Max(
                    0,
                    (comparisonTexture.height -
                     cropTexelHeight) / 2);

            Rect cropTexCoords =
                CreateCenterCropTexCoords(
                    comparisonTexture,
                    cropTexelX,
                    cropTexelY,
                    cropTexelWidth,
                    cropTexelHeight,
                    mirrorX);

            // Both draw calls intentionally use the same local Texture object.
            // IMGUI samples it directly; there is no observer RT or Blit.
            GUI.DrawTextureWithTexCoords(
                fullRect,
                comparisonTexture,
                fullTexCoords,
                true);

            GUI.DrawTextureWithTexCoords(
                cropRect,
                comparisonTexture,
                cropTexCoords,
                true);

            RefreshMetricsAndLog(
                sourceTexture,
                comparisonTexture,
                fullRect,
                sourceAspect,
                productionPlanningWidth,
                productionDesiredHeight,
                productionNominalDrawWidth,
                cropTexelX,
                cropTexelY,
                cropTexelWidth,
                cropTexelHeight,
                mirrorX);
        }

        GUI.Label(
            new Rect(
                panelRect.x + PanelPadding,
                fullRect.yMax + 4f,
                panelRect.width -
                    PanelPadding * 2f,
                MetricsHeight - 4f),
            _metricsText,
            _metricsStyle);
    }

    private void RefreshMetricsAndLog(
        Texture sourceTexture,
        Texture comparisonTexture,
        Rect fullRect,
        float sourceAspect,
        float productionPlanningWidth,
        float productionDesiredHeight,
        float productionNominalDrawWidth,
        int cropTexelX,
        int cropTexelY,
        int cropTexelWidth,
        int cropTexelHeight,
        bool mirrorX)
    {
        if (Time.unscaledTime < _nextLogTime)
        {
            return;
        }

        _nextLogTime =
            Time.unscaledTime +
            LogIntervalSeconds;

        int sourceTextureId =
            sourceTexture != null
                ? sourceTexture.GetInstanceID()
                : 0;

        int comparisonTextureId =
            comparisonTexture.GetInstanceID();

        int sourceWidth =
            sourceTexture != null
                ? sourceTexture.width
                : 0;

        int sourceHeight =
            sourceTexture != null
                ? sourceTexture.height
                : 0;

        float drawAspect =
            fullRect.width /
            Mathf.Max(1f, fullRect.height);

        float horizontalScaleRatio =
            sourceWidth > 0
                ? fullRect.width / sourceWidth
                : 0f;

        float verticalScaleRatio =
            sourceHeight > 0
                ? fullRect.height / sourceHeight
                : 0f;

        string relation =
            sourceTexture == null
                ? "SOURCE_UNAVAILABLE"
                : ReferenceEquals(
                    sourceTexture,
                    comparisonTexture)
                    ? "DIRECT_SAME_TEXTURE"
                    : "INTERMEDIATE_TEXTURE";

        string sourceName =
            sourceTexture != null
                ? sourceTexture.name
                : "null";

        string sourceFormat =
            GetTextureFormat(sourceTexture);

        string sourceGraphicsFormat =
            sourceTexture != null
                ? sourceTexture.graphicsFormat.ToString()
                : "NONE";

        string sourceDimension =
            sourceTexture != null
                ? sourceTexture.dimension.ToString()
                : "NONE";

        string sourceColorSpace =
            GetTextureColorSpace(sourceTexture);

        string comparisonFormat =
            GetTextureFormat(comparisonTexture);

        string comparisonColorSpace =
            GetTextureColorSpace(comparisonTexture);

        float comparisonHorizontalScaleRatio =
            fullRect.width /
            Mathf.Max(1, comparisonTexture.width);

        float comparisonVerticalScaleRatio =
            fullRect.height /
            Mathf.Max(1, comparisonTexture.height);

        _metricsText =
            "sourceTextureId=" + sourceTextureId +
            "  sourceName=" + sourceName +
            "  sourceWidth=" + sourceWidth +
            "  sourceHeight=" + sourceHeight +
            "\nformat=" + sourceFormat +
            "  graphicsFormat=" + sourceGraphicsFormat +
            "  dimension=" + sourceDimension +
            "  filterMode=" +
            (sourceTexture != null
                ? sourceTexture.filterMode.ToString()
                : "NONE") +
            "  mipmapCount=" +
            (sourceTexture != null
                ? sourceTexture.mipmapCount.ToString(Invariant)
                : "0") +
            "  wrapMode=" +
            (sourceTexture != null
                ? sourceTexture.wrapMode.ToString()
                : "NONE") +
            "\nactiveTextureColorSpace=" + sourceColorSpace +
            "  QualitySettings.activeColorSpace=" +
            QualitySettings.activeColorSpace +
            "  sourceIsDataSRGB=" +
            (sourceTexture != null && sourceTexture.isDataSRGB
                ? "1"
                : "0") +
            "\ncomparisonTextureId=" + comparisonTextureId +
            "  relation=" + relation +
            "  comparisonSize=" +
            comparisonTexture.width +
            "x" +
            comparisonTexture.height +
            "  comparisonFormat=" + comparisonFormat +
            "  comparisonColorSpace=" + comparisonColorSpace +
            "\ndrawRectWidth=" +
            fullRect.width.ToString("F0", Invariant) +
            "  drawRectHeight=" +
            fullRect.height.ToString("F0", Invariant) +
            "  drawAspect=" +
            drawAspect.ToString("F6", Invariant) +
            "  sourceAspect=" +
            sourceAspect.ToString("F6", Invariant) +
            "\nhorizontalScaleRatio=" +
            horizontalScaleRatio.ToString("F6", Invariant) +
            "  verticalScaleRatio=" +
            verticalScaleRatio.ToString("F6", Invariant) +
            "  comparisonScaleRatio=" +
            comparisonHorizontalScaleRatio.ToString("F6", Invariant) +
            "/" +
            comparisonVerticalScaleRatio.ToString("F6", Invariant) +
            "\nproductionPlanningWidth=" +
            productionPlanningWidth.ToString("F2", Invariant) +
            "  productionDesiredHeight=" +
            productionDesiredHeight.ToString("F2", Invariant) +
            "  productionNominalDrawWidth=" +
            productionNominalDrawWidth.ToString("F2", Invariant) +
            "\ncenterCropTexels=x" + cropTexelX +
            " y" + cropTexelY +
            " w" + cropTexelWidth +
            " h" + cropTexelHeight +
            "  mirrorX=" +
            (mirrorX ? "1" : "0") +
            "  fullCropSameTextureObject=1" +
            "\npreviewMode=" +
            KiwiCameraPreviewQualityService.CurrentModeName +
            "  colorMode=" +
            KiwiCameraPreviewQualityService.
                CurrentColorCorrectionModeName +
            "  liveBlitsLastPrepare=" +
            KiwiCameraPreviewQualityService.
                LiveBlitsLastPrepare +
            "  observerBlit=0 observerReadback=0 frame=" +
            Time.frameCount;

        Debug.Log(
            "[KiwiLiveCameraPixelSpaceAudit] " +
            _metricsText.Replace('\n', ' '));
    }

    private static Rect CreateCenterCropTexCoords(
        Texture texture,
        int cropTexelX,
        int cropTexelY,
        int cropTexelWidth,
        int cropTexelHeight,
        bool mirrorX)
    {
        float textureWidth =
            Mathf.Max(1, texture.width);

        float textureHeight =
            Mathf.Max(1, texture.height);

        float u =
            cropTexelX / textureWidth;

        float v =
            cropTexelY / textureHeight;

        float width =
            cropTexelWidth / textureWidth;

        float height =
            cropTexelHeight / textureHeight;

        return
            mirrorX
                ? new Rect(
                    u + width,
                    v,
                    -width,
                    height)
                : new Rect(
                    u,
                    v,
                    width,
                    height);
    }

    private static string GetTextureFormat(
        Texture texture)
    {
        if (texture == null)
        {
            return "NONE";
        }

        if (texture is RenderTexture renderTexture)
        {
            return renderTexture.format.ToString();
        }

        if (texture is Texture2D texture2D)
        {
            return texture2D.format.ToString();
        }

        return texture.GetType().Name;
    }

    private static string GetTextureColorSpace(
        Texture texture)
    {
        if (texture == null)
        {
            return "NONE";
        }

        return
            GraphicsFormatUtility.IsSRGBFormat(
                texture.graphicsFormat)
                ? "sRGB"
                : "Linear";
    }

    private void EnsureStyles()
    {
        if (_titleStyle == null)
        {
            _titleStyle =
                new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    normal =
                    {
                        textColor = Color.white
                    }
                };
        }

        if (_metricsStyle == null)
        {
            _metricsStyle =
                new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    wordWrap = false,
                    normal =
                    {
                        textColor =
                            new Color(
                                0.84f,
                                0.96f,
                                1f,
                                1f)
                    }
                };
        }
    }

    private static bool IsEnabled(
        string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string value =
            raw.Trim().ToLowerInvariant();

        return
            value == "1" ||
            value == "true" ||
            value == "on" ||
            value == "yes";
    }
}
