using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// KiwiAvatarSystem Phase16.20.9 v21 frame-pacing isolation diagnostic.
///
/// This component is diagnostic policy only:
/// - BASELINE performs no frame-pacing writes.
/// - UNSYNC60 runs after Quality10 and applies one explicit low-latency
///   desktop pacing policy so vSync/OnDemandRendering can be isolated from
///   camera/inference behavior.
///
/// It never reads or writes tracking data, Root transforms, provider authority,
/// ROI state, FaceParts, camera generations, or FaceTexture transactions.
/// </summary>
[DefaultExecutionOrder(34000)]
[DisallowMultipleComponent]
public sealed class KiwiFramePacingDiagnosticController : MonoBehaviour
{
    public const string ContractMarker =
        "KIWI_V5_1_PHASE16_20_9_V21_FRAME_PACING_ISOLATION";

    private const string RuntimeObjectName =
        "[Kiwi] Frame Pacing Diagnostic";

    private const string ModeEnvironment =
        "KIWI_FRAME_PACING_MODE";

    public const int BaselineModeId = 0;
    public const int Unsync60ModeId = 1;

    private static int _modeId = BaselineModeId;
    private static string _modeName = "BASELINE";
    private static bool _modeResolved;
    private static bool _logged;

    public static int ModeId => _modeId;
    public static string ModeName => _modeName;

    public static int VSyncCount =>
        QualitySettings.vSyncCount;

    public static int TargetFrameRate =>
        Application.targetFrameRate;

    public static int RenderFrameInterval =>
        OnDemandRendering.renderFrameInterval;

    public static int EffectiveRenderFrameRate =>
        OnDemandRendering.effectiveRenderFrameRate;

    public static bool WillCurrentFrameRender =>
        OnDemandRendering.willCurrentFrameRender;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        ResolveMode();

        KiwiFramePacingDiagnosticController existing =
            FindFirstObjectByType<KiwiFramePacingDiagnosticController>(
                FindObjectsInactive.Include);

        if (existing != null)
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);

        host.AddComponent<
            KiwiFramePacingDiagnosticController>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        ResolveMode();
        ApplyExplicitDiagnosticPolicy();
        LogStateOnce();
    }

    private void LateUpdate()
    {
        // Default execution order 34000 intentionally runs after Quality10
        // (30000). BASELINE writes nothing. UNSYNC60 is an explicit A/B probe
        // and reasserts only frame-pacing state in case another policy owner
        // writes Application.targetFrameRate earlier in the frame.
        ApplyExplicitDiagnosticPolicy();
    }

    private static void ResolveMode()
    {
        if (_modeResolved)
        {
            return;
        }

        string raw =
            Environment.GetEnvironmentVariable(
                ModeEnvironment) ??
            string.Empty;

        if (
            string.Equals(
                raw,
                "UNSYNC60",
                StringComparison.OrdinalIgnoreCase)
        )
        {
            _modeId = Unsync60ModeId;
            _modeName = "UNSYNC60";
        }
        else
        {
            _modeId = BaselineModeId;
            _modeName = "BASELINE";

            if (
                !string.IsNullOrWhiteSpace(raw) &&
                !string.Equals(
                    raw,
                    "BASELINE",
                    StringComparison.OrdinalIgnoreCase)
            )
            {
                Debug.LogWarning(
                    "[KiwiFramePacing] Unknown " +
                    ModeEnvironment +
                    "='" + raw +
                    "'. Falling back to BASELINE/no-write."
                );
            }
        }

        _modeResolved = true;
    }

    private static void ApplyExplicitDiagnosticPolicy()
    {
        if (_modeId != Unsync60ModeId)
        {
            return;
        }

        if (QualitySettings.vSyncCount != 0)
        {
            QualitySettings.vSyncCount = 0;
        }

        if (Application.targetFrameRate != 60)
        {
            Application.targetFrameRate = 60;
        }

        if (OnDemandRendering.renderFrameInterval != 1)
        {
            OnDemandRendering.renderFrameInterval = 1;
        }
    }

    private static void LogStateOnce()
    {
        if (_logged)
        {
            return;
        }

        _logged = true;

        Debug.Log(
            "[KiwiFramePacing] " +
            ContractMarker +
            " mode=" + _modeName +
            " vSyncCount=" + QualitySettings.vSyncCount +
            " targetFrameRate=" + Application.targetFrameRate +
            " renderFrameInterval=" +
            OnDemandRendering.renderFrameInterval +
            " effectiveRenderFrameRate=" +
            OnDemandRendering.effectiveRenderFrameRate
        );
    }
}
