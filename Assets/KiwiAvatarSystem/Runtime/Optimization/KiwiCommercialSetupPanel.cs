using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// v4.4 lightweight in-app commercial setup panel.
///
/// The panel is intentionally configuration-only. It does not run any tracking
/// algorithm and allocates GUI content only while visible.
/// </summary>
[DefaultExecutionOrder(34000)]
[DisallowMultipleComponent]
public sealed class KiwiCommercialSetupPanel : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Commercial Setup Panel";

    private const string SetupVisibleKey =
        "Kiwi.UI.SetupVisible.v1";

    private const string TelemetryVisibleKey =
        "Kiwi.UI.TelemetryVisible.v1";

    [Header("On-Screen Controls")]
    public bool showControlDock = true;

    [Tooltip("Remember Setup / Telemetry visibility between launches.")]
    public bool rememberPanelVisibility = true;

    public bool showPanel = false;

    [Range(320f, 620f)]
    public float panelWidth = 470f;

    [Range(0.80f, 1.60f)]
    public float controlDockScale = 1f;

    private KiwiCommercialProfileController _profile;
    private KiwiCommercialQualityGovernor _quality;
    private KiwiCommercialPathfinder _pathfinder;
    private KiwiMatureTrackingTelemetry _telemetry;

    private GUIStyle _titleStyle;
    private GUIStyle _labelStyle;
    private GUIStyle _buttonStyle;

    private double _nextReferenceRefreshRealtime;

    private bool _requestedTelemetryVisible;
    private bool _telemetryVisibilityApplied;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<
                KiwiCommercialSetupPanel>(
                    FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);

        host.AddComponent<
            KiwiCommercialSetupPanel>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        if (rememberPanelVisibility)
        {
            showPanel =
                PlayerPrefs.GetInt(
                    SetupVisibleKey,
                    0) !=
                0;

            _requestedTelemetryVisible =
                PlayerPrefs.GetInt(
                    TelemetryVisibleKey,
                    0) !=
                0;
        }
        else
        {
            showPanel =
                false;

            _requestedTelemetryVisible =
                false;
        }

        RefreshReferences(true);

        ApplyRequestedTelemetryVisibility();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -=
            HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _profile = null;
        _quality = null;
        _pathfinder = null;
        _telemetry = null;

        _telemetryVisibilityApplied =
            false;

        _nextReferenceRefreshRealtime = 0.0;

        RefreshReferences(true);

        ApplyRequestedTelemetryVisibility();
    }

    private void Update()
    {
        double now =
            Time.realtimeSinceStartupAsDouble;

        if (
            now >=
            _nextReferenceRefreshRealtime
        )
        {
            _nextReferenceRefreshRealtime =
                now + 1.0;

            RefreshReferences(false);

            ApplyRequestedTelemetryVisibility();
        }
    }

    public void SetSetupVisible(
        bool visible)
    {
        showPanel =
            visible;

        PersistPanelVisibility();
    }

    public void ToggleSetupVisible()
    {
        SetSetupVisible(
            !showPanel);
    }

    public void SetTelemetryVisible(
        bool visible)
    {
        _requestedTelemetryVisible =
            visible;

        _telemetryVisibilityApplied =
            false;

        ApplyRequestedTelemetryVisibility();

        PersistPanelVisibility();
    }

    public void ToggleTelemetryVisible()
    {
        bool current =
            _telemetry != null
                ? _telemetry.IsOverlayVisible
                : _requestedTelemetryVisible;

        SetTelemetryVisible(
            !current);
    }

    private void OnGUI()
    {
        EnsureStyles();

        // Keep the compact app control dock visible even when both large
        // panels are hidden. This replaces the old F8/F9 shortcut dependency.
        if (showControlDock)
        {
            DrawControlDock();
        }

        if (!showPanel)
        {
            return;
        }

        Rect panel =
            new Rect(
                18f,
                18f,
                panelWidth,
                612f);

        GUI.Box(
            panel,
            GUIContent.none);

        float x =
            panel.x +
            14f;

        float y =
            panel.y +
            12f;

        float contentWidth =
            panel.width -
            28f;

        GUI.Label(
            new Rect(
                x,
                y,
                contentWidth,
                28f),
            "Kiwi v4.5 Commercial Setup",
            _titleStyle);

        y += 32f;

        string health =
            _pathfinder != null
                ? _pathfinder.StateName +
                    "  score=" +
                    _pathfinder.HealthScore
                        .ToString("F2")
                : "Pathfinder unavailable";

        GUI.Label(
            new Rect(
                x,
                y,
                contentWidth,
                22f),
            health,
            _labelStyle);

        y += 22f;

        if (_pathfinder != null)
        {
            GUI.Label(
                new Rect(
                    x,
                    y,
                    contentWidth,
                    42f),
                _pathfinder.Recommendation,
                _labelStyle);
        }

        y += 46f;

        GUI.Label(
            new Rect(
                x,
                y,
                contentWidth,
                22f),
            "Motion profile",
            _labelStyle);

        y += 24f;

        float third =
            (
                contentWidth -
                12f
            ) /
            3f;

        if (
            GUI.Button(
                new Rect(
                    x,
                    y,
                    third,
                    28f),
                "Responsive",
                _buttonStyle) &&
            _profile != null
        )
        {
            _profile.ApplyStyle(
                KiwiCommercialProfileController
                    .MotionStyle
                    .Responsive);
        }

        if (
            GUI.Button(
                new Rect(
                    x +
                    third +
                    6f,
                    y,
                    third,
                    28f),
                "Balanced",
                _buttonStyle) &&
            _profile != null
        )
        {
            _profile.ApplyStyle(
                KiwiCommercialProfileController
                    .MotionStyle
                    .Balanced);
        }

        if (
            GUI.Button(
                new Rect(
                    x +
                    (
                        third +
                        6f
                    ) *
                    2f,
                    y,
                    third,
                    28f),
                "Stable",
                _buttonStyle) &&
            _profile != null
        )
        {
            _profile.ApplyStyle(
                KiwiCommercialProfileController
                    .MotionStyle
                    .Stable);
        }

        y += 38f;

        GUI.Label(
            new Rect(
                x,
                y,
                contentWidth,
                22f),
            "Runtime quality",
            _labelStyle);

        y += 24f;

        float quarter =
            (
                contentWidth -
                    18f
            ) /
            4f;

        DrawQualityButton(
            x,
            y,
            quarter,
            "Auto",
            KiwiCommercialQualityGovernor
                .QualityMode
                .Auto);

        DrawQualityButton(
            x +
                quarter +
                6f,
            y,
            quarter,
            "Quality",
            KiwiCommercialQualityGovernor
                .QualityMode
                .Quality);

        DrawQualityButton(
            x +
                (
                    quarter +
                    6f
                ) *
                2f,
            y,
            quarter,
            "Balanced",
            KiwiCommercialQualityGovernor
                .QualityMode
                .Balanced);

        DrawQualityButton(
            x +
                (
                    quarter +
                    6f
                ) *
                3f,
            y,
            quarter,
            "Realtime",
            KiwiCommercialQualityGovernor
                .QualityMode
                .Realtime);

        y += 40f;

        string active =
            "Profile: " +
            (
                _profile != null
                    ? _profile.ActiveProfileName +
                        " / " +
                        _profile.CurrentStyleName
                    : "-"
            ) +
            "    Quality: " +
            (
                _quality != null
                    ? _quality.CurrentTierName +
                        "  " +
                        _quality.RenderFps
                            .ToString("F0") +
                        " fps"
                    : "-"
            );

        GUI.Label(
            new Rect(
                x,
                y,
                contentWidth,
                24f),
            active,
            _labelStyle);

        y += 30f;

        bool profileChanged =
            false;

        if (_profile != null)
        {
            profileChanged |=
                DrawProfileSlider(
                    x,
                    ref y,
                    contentWidth,
                    "Move X",
                    ref _profile.screenPositionGainX,
                    0f,
                    2f);

            profileChanged |=
                DrawProfileSlider(
                    x,
                    ref y,
                    contentWidth,
                    "Move Y",
                    ref _profile.screenPositionGainY,
                    0f,
                    2f);

            profileChanged |=
                DrawProfileSlider(
                    x,
                    ref y,
                    contentWidth,
                    "Pitch",
                    ref _profile.pitchGain,
                    0f,
                    2f);

            profileChanged |=
                DrawProfileSlider(
                    x,
                    ref y,
                    contentWidth,
                    "Yaw",
                    ref _profile.yawGain,
                    0f,
                    2f);

            profileChanged |=
                DrawProfileSlider(
                    x,
                    ref y,
                    contentWidth,
                    "Roll",
                    ref _profile.rollGain,
                    0f,
                    2f);

            profileChanged |=
                DrawProfileSlider(
                    x,
                    ref y,
                    contentWidth,
                    "Eye Response",
                    ref _profile.eyeResponseMultiplier,
                    0.65f,
                    1.35f);

            profileChanged |=
                DrawProfileSlider(
                    x,
                    ref y,
                    contentWidth,
                    "Mouth Response",
                    ref _profile.mouthResponseMultiplier,
                    0.65f,
                    1.35f);

            profileChanged |=
                DrawProfileSlider(
                    x,
                    ref y,
                    contentWidth,
                    "Contour Response",
                    ref _profile.contourResponseMultiplier,
                    0.65f,
                    1.35f);
        }

        if (
            profileChanged &&
            _profile != null
        )
        {
            _profile.ApplyCurrentProfile();
        }

        y += 6f;

        float half =
            (
                contentWidth -
                    8f
            ) *
            0.5f;

        if (
            GUI.Button(
                new Rect(
                    x,
                    y,
                    half,
                    30f),
                "Quick Recalibrate",
                _buttonStyle) &&
            _profile != null
        )
        {
            _profile.QuickRecalibrate();
        }

        if (
            GUI.Button(
                new Rect(
                    x +
                        half +
                        8f,
                    y,
                    half,
                    30f),
                "Save Profile",
                _buttonStyle) &&
            _profile != null
        )
        {
            _profile.SaveNow();
        }

        y += 38f;

        GUI.Label(
            new Rect(
                x,
                y,
                contentWidth,
                44f),
            "Use the on-screen Setup / Telemetry buttons to show or hide panels.\n" +
            "Profiles change existing owners; no extra pose filter is added.",
            _labelStyle);
    }

    private void DrawControlDock()
    {
        float automaticScale =
            Mathf.Clamp(
                Screen.height /
                    1080f,
                0.86f,
                1.28f);

        float scale =
            automaticScale *
            Mathf.Clamp(
                controlDockScale,
                0.80f,
                1.60f);

        float buttonWidth =
            128f *
            scale;

        float buttonHeight =
            34f *
            scale;

        float gap =
            8f *
            scale;

        float dockWidth =
            buttonWidth *
                2f +
            gap;

        float x =
            Mathf.Max(
                8f,
                (
                    Screen.width -
                    dockWidth
                ) *
                0.5f);

        float y =
            8f;

        bool telemetryVisible =
            _telemetry != null
                ? _telemetry.IsOverlayVisible
                : _requestedTelemetryVisible;

        string setupLabel =
            showPanel
                ? "Setup : ON"
                : "Setup : OFF";

        string telemetryLabel =
            telemetryVisible
                ? "Telemetry : ON"
                : "Telemetry : OFF";

        if (
            GUI.Button(
                new Rect(
                    x,
                    y,
                    buttonWidth,
                    buttonHeight),
                setupLabel,
                _buttonStyle)
        )
        {
            ToggleSetupVisible();
        }

        if (
            GUI.Button(
                new Rect(
                    x +
                        buttonWidth +
                        gap,
                    y,
                    buttonWidth,
                    buttonHeight),
                telemetryLabel,
                _buttonStyle)
        )
        {
            ToggleTelemetryVisible();
        }
    }


    private void ApplyRequestedTelemetryVisibility()
    {
        if (
            _telemetry == null ||
            _telemetryVisibilityApplied
        )
        {
            return;
        }

        _telemetry.SetOverlayVisible(
            _requestedTelemetryVisible);

        _telemetryVisibilityApplied =
            true;
    }


    private void PersistPanelVisibility()
    {
        if (!rememberPanelVisibility)
        {
            return;
        }

        PlayerPrefs.SetInt(
            SetupVisibleKey,
            showPanel
                ? 1
                : 0);

        bool telemetryVisible =
            _telemetry != null
                ? _telemetry.IsOverlayVisible
                : _requestedTelemetryVisible;

        PlayerPrefs.SetInt(
            TelemetryVisibleKey,
            telemetryVisible
                ? 1
                : 0);

        PlayerPrefs.Save();
    }


    private bool DrawProfileSlider(
        float x,
        ref float y,
        float contentWidth,
        string label,
        ref float value,
        float minimum,
        float maximum)
    {
        const float labelWidth =
            112f;

        const float valueWidth =
            46f;

        GUI.Label(
            new Rect(
                x,
                y,
                labelWidth,
                20f),
            label,
            _labelStyle);

        float sliderX =
            x +
            labelWidth;

        float sliderWidth =
            Mathf.Max(
                80f,
                contentWidth -
                    labelWidth -
                    valueWidth -
                    8f);

        float next =
            GUI.HorizontalSlider(
                new Rect(
                    sliderX,
                    y +
                        4f,
                    sliderWidth,
                    18f),
                value,
                minimum,
                maximum);

        GUI.Label(
            new Rect(
                sliderX +
                    sliderWidth +
                    8f,
                y,
                valueWidth,
                20f),
            next.ToString("F2"),
            _labelStyle);

        y +=
            24f;

        if (
            Mathf.Abs(
                next -
                    value) <
            0.0001f
        )
        {
            return false;
        }

        value =
            next;

        return true;
    }


    private void DrawQualityButton(
        float x,
        float y,
        float width,
        string label,
        KiwiCommercialQualityGovernor
            .QualityMode mode)
    {
        if (
            GUI.Button(
                new Rect(
                    x,
                    y,
                    width,
                    28f),
                label,
                _buttonStyle) &&
            _quality != null
        )
        {
            _quality.SetMode(
                mode);
        }
    }

    private void EnsureStyles()
    {
        if (_titleStyle == null)
        {
            _titleStyle =
                new GUIStyle(
                    GUI.skin.label);

            _titleStyle.fontSize =
                16;

            _titleStyle.fontStyle =
                FontStyle.Bold;
        }

        if (_labelStyle == null)
        {
            _labelStyle =
                new GUIStyle(
                    GUI.skin.label);

            _labelStyle.wordWrap =
                true;
        }

        if (_buttonStyle == null)
        {
            _buttonStyle =
                new GUIStyle(
                    GUI.skin.button);
        }
    }

    private void RefreshReferences(
        bool force)
    {
        if (
            force ||
            _profile == null
        )
        {
            _profile =
                FindFirstObjectByType<
                    KiwiCommercialProfileController>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _quality == null
        )
        {
            _quality =
                FindFirstObjectByType<
                    KiwiCommercialQualityGovernor>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _pathfinder == null
        )
        {
            _pathfinder =
                FindFirstObjectByType<
                    KiwiCommercialPathfinder>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _telemetry == null
        )
        {
            KiwiMatureTrackingTelemetry previous =
                _telemetry;

            _telemetry =
                FindFirstObjectByType<
                    KiwiMatureTrackingTelemetry>(
                    FindObjectsInactive.Include);

            if (
                previous !=
                _telemetry
            )
            {
                _telemetryVisibilityApplied =
                    false;
            }
        }
    }
}
