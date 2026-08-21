using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v3.4 side-view visible-eye continuity.
///
/// Mature VTuber trackers commonly avoid letting the poorly-observed far eye
/// destabilize the still-visible near eye. This component operates only on the
/// CanvasRenderer side-view gate. FacePartShapeMask remains the owner of blink
/// and semantic mask opacity.
///
/// If the head is substantially turned and both renderer gates become low, the
/// geometrically nearer eye (or the currently stronger eye when depth is
/// unavailable) is kept above a small renderer-alpha floor.
/// </summary>
[DefaultExecutionOrder(1270)]
[DisallowMultipleComponent]
public sealed class KiwiSideViewEyeContinuity : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Side-View Eye Continuity";

    [Range(20f, 75f)]
    public float sideViewStartYaw = 38f;

    [Range(0.05f, 0.80f)]
    public float minimumNearEyeRendererAlpha = 0.38f;

    [Range(10f, 240f)]
    public float recoveryResponse = 90f;

    [Header("Diagnostics")]
    [SerializeField] private string debugProtectedEye = "-";
    [SerializeField] private float debugYaw;
    [SerializeField] private float debugLeftAlpha = 1f;
    [SerializeField] private float debugRightAlpha = 1f;

    private FacePartCropper _cropper;
    private KiwiFaceMotion _faceMotion;
    private KiwiTrackingProviderHub _trackingHub;
    private FaceLandmarkerRunner _runner;
    private Camera _camera;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiSideViewEyeContinuity>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(
                RuntimeObjectName);

        DontDestroyOnLoad(
            host);

        host.AddComponent<
            KiwiSideViewEyeContinuity>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(
            gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        RefreshReferences(true);
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
        _cropper = null;
        _faceMotion = null;
        _trackingHub = null;
        _runner = null;
        _camera = null;

        RefreshReferences(true);
    }

    private void LateUpdate()
    {
        RefreshReferences(false);

        if (
            _cropper == null ||
            _faceMotion == null ||
            !HasUsableTracking()
        )
        {
            debugProtectedEye =
                "-";

            return;
        }

        float yaw =
            _faceMotion.RenderedYawDegrees;

        debugYaw =
            yaw;

        RawImage left =
            _cropper.leftEyeImage;

        RawImage right =
            _cropper.rightEyeImage;

        if (
            left == null ||
            right == null
        )
        {
            return;
        }

        float leftAlpha =
            left.canvasRenderer
                .GetAlpha();

        float rightAlpha =
            right.canvasRenderer
                .GetAlpha();

        debugLeftAlpha =
            leftAlpha;

        debugRightAlpha =
            rightAlpha;

        if (
            Mathf.Abs(yaw) <
                sideViewStartYaw ||
            Mathf.Max(
                leftAlpha,
                rightAlpha) >=
                minimumNearEyeRendererAlpha
        )
        {
            debugProtectedEye =
                "-";

            return;
        }

        bool protectLeft =
            ResolveNearEyeByDepth(
                left,
                right,
                out bool leftIsNear)
                ? leftIsNear
                : leftAlpha >=
                    rightAlpha;

        RawImage protectedEye =
            protectLeft
                ? left
                : right;

        float current =
            protectedEye.canvasRenderer
                .GetAlpha();

        float dt =
            Mathf.Max(
                0.000001f,
                Time.unscaledDeltaTime);

        float next =
            Mathf.Lerp(
                current,
                minimumNearEyeRendererAlpha,
                1f -
                Mathf.Exp(
                    -recoveryResponse *
                    dt));

        protectedEye.canvasRenderer
            .SetAlpha(
                Mathf.Max(
                    current,
                    next));

        debugProtectedEye =
            protectLeft
                ? "Left"
                : "Right";
    }

    private bool ResolveNearEyeByDepth(
        RawImage leftImage,
        RawImage rightImage,
        out bool leftIsNear)
    {
        leftIsNear =
            false;

        SurfaceFittedRawImage left =
            leftImage
            as SurfaceFittedRawImage;

        SurfaceFittedRawImage right =
            rightImage
            as SurfaceFittedRawImage;

        if (
            left == null ||
            right == null ||
            _camera == null ||
            !TryGetSurfaceCenterWorld(
                left,
                out Vector3 leftWorld) ||
            !TryGetSurfaceCenterWorld(
                right,
                out Vector3 rightWorld)
        )
        {
            return false;
        }

        float leftDepth =
            _camera.transform
                .InverseTransformPoint(
                    leftWorld)
                .z;

        float rightDepth =
            _camera.transform
                .InverseTransformPoint(
                    rightWorld)
                .z;

        if (
            Mathf.Abs(
                leftDepth -
                rightDepth) <
                0.0005f
        )
        {
            return false;
        }

        leftIsNear =
            leftDepth <
            rightDepth;

        return true;
    }

    private static bool TryGetSurfaceCenterWorld(
        SurfaceFittedRawImage part,
        out Vector3 world)
    {
        world =
            Vector3.zero;

        if (
            part == null ||
            !part.TryGetSurfaceLocalPosition(
                new Vector2(
                    0.5f,
                    0.5f),
                out Vector3 local)
        )
        {
            return false;
        }

        world =
            part.rectTransform
                .TransformPoint(
                    local);

        return
            IsFinite(
                world);
    }

    private bool HasUsableTracking()
    {
        FacePrecisionTrackingData data =
            default;

        if (_trackingHub != null)
        {
            if (
                _trackingHub.TryGetLatestFrame(
                    out data,
                    out _)
            )
            {
                return
                    data.isValid &&
                    data.frameId >
                        0UL;
            }

            // Side-view visibility must freeze with the authoritative rigid
            // stream during a short stall. Reading a newer-but-rejected Runner
            // sample here makes the far eye move while the head itself is held.
            return false;
        }

        return
            _runner != null &&
            _runner.TryGetLatestPrecisionTrackingData(
                out data) &&
            data.isValid &&
            data.frameId >
                0UL;
    }

    private void RefreshReferences(
        bool force)
    {
        if (
            force ||
            _cropper == null
        )
        {
            _cropper =
                FindFirstObjectByType<
                    FacePartCropper>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _faceMotion == null
        )
        {
            _faceMotion =
                FindFirstObjectByType<
                    KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _trackingHub == null
        )
        {
            _trackingHub =
                FindFirstObjectByType<
                    KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _runner == null
        )
        {
            _runner =
                FindFirstObjectByType<
                    FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _camera == null
        )
        {
            if (
                _faceMotion != null &&
                _faceMotion.positionReferenceCamera !=
                    null
            )
            {
                _camera =
                    _faceMotion.positionReferenceCamera;
            }
            else
            {
                _camera =
                    Camera.main;
            }
        }
    }

    private static bool IsFinite(
        Vector3 value)
    {
        return
            !float.IsNaN(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsNaN(value.z) &&
            !float.IsInfinity(value.x) &&
            !float.IsInfinity(value.y) &&
            !float.IsInfinity(value.z);
    }
}
