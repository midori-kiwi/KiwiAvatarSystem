using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Phase 16.8 diagnostic-only transform writer audit.
///
/// The latest dense capture contained visible avatar jumps while the existing
/// Frame Comparison Root delta remained zero. This observer samples the rigid
/// Root and one representative visual descendant at Update, late LateUpdate,
/// Application.onBeforeRender and SRP beginCameraRendering. It never writes a
/// Transform. A visual-relative change with a stable Root local pose identifies
/// a downstream child/animation/presentation writer; a Root change after
/// LateUpdate identifies a late writer/order problem.
/// </summary>
[DefaultExecutionOrder(36400)]
[DisallowMultipleComponent]
public sealed class KiwiCommercialTransformWriterAudit : MonoBehaviour
{
    // KIWI_V5_1_PHASE16_8_TRANSFORM_WRITER_AUDIT
    private const string RuntimeObjectName =
        "[Kiwi] Commercial Transform Writer Audit";

    private static KiwiCommercialTransformWriterAudit _instance;

    private KiwiFaceMotion _motion;
    private Transform _root;
    private Transform _visual;
    private int _boundRootId;

    private Sample _updateSample;
    private Sample _lateSample;
    private int _lateSampleFrame = -1;

    private int _lastCompletedFrame = -1;
    private float _lastRootUpdateToLatePositionDelta;
    private float _lastRootUpdateToLateRotationDelta;
    private float _lastRootUpdateToLateScaleDelta;
    private float _lastRootLateToRenderPositionDelta;
    private float _lastRootLateToRenderRotationDelta;
    private float _lastRootLateToRenderScaleDelta;
    private float _lastVisualRelativeUpdateToLatePositionDelta;
    private float _lastVisualRelativeUpdateToLateRotationDelta;
    private float _lastVisualRelativeUpdateToLateScaleDelta;
    private float _lastVisualRelativeLateToRenderPositionDelta;
    private float _lastVisualRelativeLateToRenderRotationDelta;
    private float _lastVisualRelativeLateToRenderScaleDelta;
    private bool _lastVisualMovedWithoutRoot;
    private bool _lastRootMovedAfterLate;
    private int _visualMovedWithoutRootCount;
    private int _rootMovedAfterLateCount;

    private const float RootPositionEpsilon = 0.00001f;
    private const float RootRotationEpsilonDegrees = 0.005f;
    private const float VisualRelativePositionEpsilon = 0.00005f;
    private const float VisualRelativeRotationEpsilonDegrees = 0.02f;

    private struct Sample
    {
        public bool valid;
        public int unityFrame;
        public Vector3 rootLocalPosition;
        public Quaternion rootLocalRotation;
        public Vector3 rootLocalScale;
        public Vector3 visualRelativePosition;
        public Quaternion visualRelativeRotation;
        public Vector3 visualRelativeScale;
    }

    public static int LastCompletedFrame =>
        _instance != null ? _instance._lastCompletedFrame : -1;

    public static float RootUpdateToLatePositionDelta =>
        _instance != null ? _instance._lastRootUpdateToLatePositionDelta : 0f;

    public static float RootUpdateToLateRotationDelta =>
        _instance != null ? _instance._lastRootUpdateToLateRotationDelta : 0f;

    public static float RootUpdateToLateScaleDelta =>
        _instance != null ? _instance._lastRootUpdateToLateScaleDelta : 0f;

    public static float RootLateToRenderPositionDelta =>
        _instance != null ? _instance._lastRootLateToRenderPositionDelta : 0f;

    public static float RootLateToRenderRotationDelta =>
        _instance != null ? _instance._lastRootLateToRenderRotationDelta : 0f;

    public static float RootLateToRenderScaleDelta =>
        _instance != null ? _instance._lastRootLateToRenderScaleDelta : 0f;

    public static float VisualRelativeUpdateToLatePositionDelta =>
        _instance != null ? _instance._lastVisualRelativeUpdateToLatePositionDelta : 0f;

    public static float VisualRelativeUpdateToLateRotationDelta =>
        _instance != null ? _instance._lastVisualRelativeUpdateToLateRotationDelta : 0f;

    public static float VisualRelativeUpdateToLateScaleDelta =>
        _instance != null ? _instance._lastVisualRelativeUpdateToLateScaleDelta : 0f;

    public static float VisualRelativeLateToRenderPositionDelta =>
        _instance != null ? _instance._lastVisualRelativeLateToRenderPositionDelta : 0f;

    public static float VisualRelativeLateToRenderRotationDelta =>
        _instance != null ? _instance._lastVisualRelativeLateToRenderRotationDelta : 0f;

    public static float VisualRelativeLateToRenderScaleDelta =>
        _instance != null ? _instance._lastVisualRelativeLateToRenderScaleDelta : 0f;

    public static bool VisualMovedWithoutRoot =>
        _instance != null && _instance._lastVisualMovedWithoutRoot;

    public static bool RootMovedAfterLate =>
        _instance != null && _instance._lastRootMovedAfterLate;

    public static int VisualMovedWithoutRootCount =>
        _instance != null ? _instance._visualMovedWithoutRootCount : 0;

    public static int RootMovedAfterLateCount =>
        _instance != null ? _instance._rootMovedAfterLateCount : 0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiCommercialTransformWriterAudit>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiCommercialTransformWriterAudit>();
    }

    private void Awake()
    {
        _instance = this;
        DontDestroyOnLoad(gameObject);
        Application.onBeforeRender -= HandleBeforeRender;
        Application.onBeforeRender += HandleBeforeRender;
        RenderPipelineManager.beginCameraRendering -= HandleBeginCameraRendering;
        RenderPipelineManager.beginCameraRendering += HandleBeginCameraRendering;
        RefreshBinding(true);
    }

    private void OnDestroy()
    {
        Application.onBeforeRender -= HandleBeforeRender;
        RenderPipelineManager.beginCameraRendering -= HandleBeginCameraRendering;
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    private void Update()
    {
        RefreshBinding(false);
        _updateSample = CaptureSample();
    }

    private void LateUpdate()
    {
        RefreshBinding(false);
        _lateSample = CaptureSample();
        _lateSampleFrame = Time.frameCount;

        _lastRootLateToRenderPositionDelta = 0f;
        _lastRootLateToRenderRotationDelta = 0f;
        _lastRootLateToRenderScaleDelta = 0f;
        _lastVisualRelativeLateToRenderPositionDelta = 0f;
        _lastVisualRelativeLateToRenderRotationDelta = 0f;
        _lastVisualRelativeLateToRenderScaleDelta = 0f;
        _lastVisualMovedWithoutRoot = false;
        _lastRootMovedAfterLate = false;

        if (
            _updateSample.valid &&
            _lateSample.valid &&
            _updateSample.unityFrame == _lateSample.unityFrame
        )
        {
            _lastRootUpdateToLatePositionDelta =
                Vector3.Distance(
                    _updateSample.rootLocalPosition,
                    _lateSample.rootLocalPosition);
            _lastRootUpdateToLateRotationDelta =
                Quaternion.Angle(
                    _updateSample.rootLocalRotation,
                    _lateSample.rootLocalRotation);
            _lastRootUpdateToLateScaleDelta =
                Vector3.Distance(
                    _updateSample.rootLocalScale,
                    _lateSample.rootLocalScale);
            _lastVisualRelativeUpdateToLatePositionDelta =
                Vector3.Distance(
                    _updateSample.visualRelativePosition,
                    _lateSample.visualRelativePosition);
            _lastVisualRelativeUpdateToLateRotationDelta =
                Quaternion.Angle(
                    _updateSample.visualRelativeRotation,
                    _lateSample.visualRelativeRotation);
            _lastVisualRelativeUpdateToLateScaleDelta =
                Vector3.Distance(
                    _updateSample.visualRelativeScale,
                    _lateSample.visualRelativeScale);
        }
        else
        {
            _lastRootUpdateToLatePositionDelta = 0f;
            _lastRootUpdateToLateRotationDelta = 0f;
            _lastRootUpdateToLateScaleDelta = 0f;
            _lastVisualRelativeUpdateToLatePositionDelta = 0f;
            _lastVisualRelativeUpdateToLateRotationDelta = 0f;
            _lastVisualRelativeUpdateToLateScaleDelta = 0f;
        }
    }

    private void HandleBeforeRender()
    {
        ObserveRenderBoundary();
    }

    private void HandleBeginCameraRendering(
        ScriptableRenderContext context,
        Camera camera)
    {
        ObserveRenderBoundary();
    }

    private void ObserveRenderBoundary()
    {
        if (
            _lateSampleFrame != Time.frameCount ||
            !_lateSample.valid
        )
        {
            return;
        }

        Sample render = CaptureSample();
        if (!render.valid)
        {
            return;
        }

        float rootPositionDelta =
            Vector3.Distance(
                _lateSample.rootLocalPosition,
                render.rootLocalPosition);
        float rootRotationDelta =
            Quaternion.Angle(
                _lateSample.rootLocalRotation,
                render.rootLocalRotation);
        float rootScaleDelta =
            Vector3.Distance(
                _lateSample.rootLocalScale,
                render.rootLocalScale);
        float visualPositionDelta =
            Vector3.Distance(
                _lateSample.visualRelativePosition,
                render.visualRelativePosition);
        float visualRotationDelta =
            Quaternion.Angle(
                _lateSample.visualRelativeRotation,
                render.visualRelativeRotation);
        float visualScaleDelta =
            Vector3.Distance(
                _lateSample.visualRelativeScale,
                render.visualRelativeScale);

        _lastRootLateToRenderPositionDelta =
            Mathf.Max(
                _lastRootLateToRenderPositionDelta,
                rootPositionDelta);
        _lastRootLateToRenderRotationDelta =
            Mathf.Max(
                _lastRootLateToRenderRotationDelta,
                rootRotationDelta);
        _lastRootLateToRenderScaleDelta =
            Mathf.Max(
                _lastRootLateToRenderScaleDelta,
                rootScaleDelta);
        _lastVisualRelativeLateToRenderPositionDelta =
            Mathf.Max(
                _lastVisualRelativeLateToRenderPositionDelta,
                visualPositionDelta);
        _lastVisualRelativeLateToRenderRotationDelta =
            Mathf.Max(
                _lastVisualRelativeLateToRenderRotationDelta,
                visualRotationDelta);
        _lastVisualRelativeLateToRenderScaleDelta =
            Mathf.Max(
                _lastVisualRelativeLateToRenderScaleDelta,
                visualScaleDelta);

        bool rootMoved =
            rootPositionDelta > RootPositionEpsilon ||
            rootRotationDelta > RootRotationEpsilonDegrees ||
            rootScaleDelta > RootPositionEpsilon;
        bool visualRelativeMoved =
            visualPositionDelta > VisualRelativePositionEpsilon ||
            visualRotationDelta > VisualRelativeRotationEpsilonDegrees ||
            visualScaleDelta > VisualRelativePositionEpsilon;

        if (rootMoved && !_lastRootMovedAfterLate)
        {
            _lastRootMovedAfterLate = true;
            _rootMovedAfterLateCount++;
        }

        if (
            visualRelativeMoved &&
            !rootMoved &&
            !_lastVisualMovedWithoutRoot
        )
        {
            _lastVisualMovedWithoutRoot = true;
            _visualMovedWithoutRootCount++;
        }

        _lastCompletedFrame = Time.frameCount;
    }

    private Sample CaptureSample()
    {
        if (_root == null)
        {
            return default;
        }

        Transform visual =
            _visual != null ? _visual : _root;

        Vector3 relativePosition =
            _root.InverseTransformPoint(visual.position);
        Quaternion relativeRotation =
            Quaternion.Inverse(_root.rotation) * visual.rotation;
        Vector3 rootLossyScale = _root.lossyScale;
        Vector3 visualLossyScale = visual.lossyScale;
        Vector3 relativeScale = new Vector3(
            SafeScaleRatio(visualLossyScale.x, rootLossyScale.x),
            SafeScaleRatio(visualLossyScale.y, rootLossyScale.y),
            SafeScaleRatio(visualLossyScale.z, rootLossyScale.z));

        return new Sample
        {
            valid = true,
            unityFrame = Time.frameCount,
            rootLocalPosition = _root.localPosition,
            rootLocalRotation = _root.localRotation,
            rootLocalScale = _root.localScale,
            visualRelativePosition = relativePosition,
            visualRelativeRotation = relativeRotation,
            visualRelativeScale = relativeScale
        };
    }

    private static float SafeScaleRatio(float numerator, float denominator)
    {
        if (Mathf.Abs(denominator) <= 0.000001f)
        {
            return 1f;
        }

        return numerator / denominator;
    }

    private void RefreshBinding(bool force)
    {
        if (force || _motion == null)
        {
            _motion =
                FindFirstObjectByType<KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }

        Transform resolvedRoot =
            _motion != null ? _motion.kiwiRoot : null;

        int resolvedRootId =
            resolvedRoot != null
                ? resolvedRoot.GetInstanceID()
                : 0;

        if (
            !force &&
            resolvedRootId == _boundRootId &&
            _root == resolvedRoot
        )
        {
            return;
        }

        _root = resolvedRoot;
        _boundRootId = resolvedRootId;
        _visual = null;

        if (_root != null)
        {
            Renderer renderer =
                _root.GetComponentInChildren<Renderer>(true);

            if (renderer != null)
            {
                _visual = renderer.transform;
            }
        }

        _updateSample = default;
        _lateSample = default;
        _lateSampleFrame = -1;
    }
}
