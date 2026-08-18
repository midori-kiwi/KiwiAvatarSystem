using UnityEngine;


[DefaultExecutionOrder(20000)]
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public class KiwiAutoFraming : MonoBehaviour
{
    // =========================================================
    // Target
    // =========================================================

    [Header("Target")]

    [Tooltip(
        "キウイ本体のRenderer。\n" +
        "通常は geometry_0 の SkinnedMeshRenderer を指定。"
    )]
    public Renderer targetRenderer;


    [Tooltip(
        "Target Rendererが未指定なら、ここから最大のRendererを自動検索。"
    )]
    public Transform targetRoot;


    // =========================================================
    // Camera FOV
    // =========================================================

    [Header("Camera FOV")]

    [Tooltip(
        "配信用にPerspective歪みを抑えた固定FOVを使用する。"
    )]
    public bool useOptimizedFieldOfView =
        true;


    [Range(15f, 60f)]
    public float optimizedFieldOfView =
        30f;


    // =========================================================
    // Safe Frame
    // =========================================================

    [Header("Safe Frame")]

    [Tooltip(
        "左右に確保する余白。\n" +
        "0.10 = 左右10%ずつ。"
    )]
    [Range(0.02f, 0.25f)]
    public float horizontalMargin =
        0.10f;


    [Tooltip(
        "上下に確保する余白。\n" +
        "0.10 = 上下10%ずつ。"
    )]
    [Range(0.02f, 0.25f)]
    public float verticalMargin =
        0.10f;


    [Header("Landmarker Translation Preservation")]
    [Tooltip("ON: camera X/Y does not chase the model, so Landmarker translation remains visible.")]
    public bool preserveLandmarkerTranslation = true;


    // =========================================================
    // Follow
    // =========================================================

    [Header("Movement Follow")]

    [Tooltip(
        "キウイの横移動をCameraが追従する割合。\n" +
        "0 = Camera固定\n" +
        "1 = 完全追従"
    )]
    [Range(0f, 1f)]
    public float horizontalFollow =
        0.00f;


    [Tooltip(
        "キウイの上下移動をCameraが追従する割合。"
    )]
    [Range(0f, 1f)]
    public float verticalFollow =
        0.00f;


    [Tooltip(
        "小さな動きではCameraを動かさない。\n" +
        "初期モデルサイズに対する割合。"
    )]
    [Range(0f, 0.20f)]
    public float centerDeadZoneFraction =
        0.04f;


    [Tooltip(
        "CameraのX/Y追従速度。\n" +
        "小さいほど速い。"
    )]
    [Range(0.01f, 0.50f)]
    public float followSmoothTime =
        0.08f;


    // =========================================================
    // Auto Zoom
    // =========================================================

    [Header("Auto Zoom Out")]

    [Tooltip(
        "画面から切れそうな時だけCameraを後退させる。"
    )]
    public bool enableAutoZoomOut =
        true;


    [Tooltip(
        "キウイが元のサイズへ戻った時にCameraが戻る速度。"
    )]
    [Range(0.05f, 1f)]
    public float zoomReturnSmoothTime =
        0.28f;


    [Tooltip(
        "Cameraが後退できる最大距離。"
    )]
    [Range(0.1f, 20f)]
    public float maximumExtraDistance =
        5f;


    [Tooltip(
        "Near Clipとの安全距離。"
    )]
    [Range(0.001f, 0.5f)]
    public float nearClipPadding =
        0.05f;


    // =========================================================
    // Skinned Mesh
    // =========================================================

    [Header("Skinned Mesh")]

    [Tooltip(
        "画面外でもSkinnedMeshのBoundsを更新する。\n" +
        "キウイ1体ならON推奨。"
    )]
    public bool updateBoundsWhenOffscreen =
        true;


    // =========================================================
    // Late Latch
    // =========================================================

    [Header("Late Latch")]

    [Tooltip(
        "KiwiFaceMotionの最終更新後にも切れを再チェックする。"
    )]
    public bool useBeforeRenderSafetyCheck =
        true;


    // =========================================================
    // Debug
    // =========================================================

    [Header("Debug")]

    [SerializeField]
    private bool debugInitialized =
        false;


    [SerializeField]
    private float debugExtraDistance =
        0f;


    [SerializeField]
    private float debugRequiredExtraDistance =
        0f;


    [SerializeField]
    private float debugFollowX =
        0f;


    [SerializeField]
    private float debugFollowY =
        0f;


    // =========================================================
    // Runtime
    // =========================================================

    private Camera
        _camera;


    private SkinnedMeshRenderer
        _skinnedRenderer;


    private bool
        _previousUpdateWhenOffscreen;


    private Vector3
        _baseCameraPosition;


    private Quaternion
        _baseCameraRotation;


    private Vector3
        _baseBoundsCenter;


    private float
        _baseHalfWidth;


    private float
        _baseHalfHeight;


    // =========================================================
    // Current lateral movement
    // =========================================================

    private float
        _currentFollowX;


    private float
        _currentFollowY;


    private float
        _followVelocityX;


    private float
        _followVelocityY;


    // =========================================================
    // Zoom
    // =========================================================

    private float
        _extraDistance;


    private float
        _zoomVelocity;


    // =========================================================
    // Bounds corners
    // =========================================================

    private readonly Vector3[]
        _corners =
            new Vector3[8];


    // =========================================================
    // Start
    // =========================================================

    private void Start()
    {
        Initialize();
    }


    // =========================================================
    // Enable
    // =========================================================

    private void OnEnable()
    {
        Application.onBeforeRender +=
            OnBeforeRender;
    }


    // =========================================================
    // Disable
    // =========================================================

    private void OnDisable()
    {
        Application.onBeforeRender -=
            OnBeforeRender;


        RestoreSkinnedRendererSetting();
    }


    // =========================================================
    // Initialize
    // =========================================================

    private void Initialize()
    {
        _camera =
            GetComponent<Camera>();


        if (_camera == null)
        {
            return;
        }


        // =====================================================
        // Perspective only
        // =====================================================

        if (_camera.orthographic)
        {
            Debug.LogError(
                "[KiwiAutoFraming] " +
                "VTuberCamera must be Perspective.",
                this
            );


            enabled =
                false;


            return;
        }


        // =====================================================
        // Find renderer
        // =====================================================

        if (targetRenderer == null)
        {
            targetRenderer =
                FindLargestRenderer();
        }


        if (targetRenderer == null)
        {
            Debug.LogError(
                "[KiwiAutoFraming] " +
                "Target Renderer が見つかりません。",
                this
            );


            enabled =
                false;


            return;
        }


        // =====================================================
        // FOV
        // =====================================================

        if (useOptimizedFieldOfView)
        {
            _camera.fieldOfView =
                optimizedFieldOfView;
        }


        // =====================================================
        // SkinnedMesh bounds
        // =====================================================

        _skinnedRenderer =
            targetRenderer
            as SkinnedMeshRenderer;


        if (_skinnedRenderer != null)
        {
            _previousUpdateWhenOffscreen =
                _skinnedRenderer
                .updateWhenOffscreen;


            if (updateBoundsWhenOffscreen)
            {
                _skinnedRenderer
                    .updateWhenOffscreen =
                    true;
            }
        }


        // =====================================================
        // Capture current composition
        //
        // 今のVTuberCamera位置を
        // 「これ以上近づかない基準位置」とする。
        // =====================================================

        _baseCameraPosition =
            transform.position;


        _baseCameraRotation =
            transform.rotation;


        Bounds bounds =
            targetRenderer.bounds;


        _baseBoundsCenter =
            bounds.center;


        CalculateProjectedHalfSize(
            bounds,
            out _baseHalfWidth,
            out _baseHalfHeight
        );


        _currentFollowX =
            0f;


        _currentFollowY =
            0f;


        _followVelocityX =
            0f;


        _followVelocityY =
            0f;


        _extraDistance =
            0f;


        _zoomVelocity =
            0f;


        debugInitialized =
            true;


        // =====================================================
        // Initial fit
        // =====================================================

        UpdateFraming(
            true
        );
    }


    // =========================================================
    // Late Update
    // =========================================================

    private void LateUpdate()
    {
        if (!debugInitialized)
        {
            return;
        }


        UpdateFraming(
            false
        );
    }


    // =========================================================
    // Before Render
    //
    // KiwiFaceMotion が onBeforeRender で
    // 最後に位置を変更する可能性があるため、
    // 最終的な「切れ防止」だけ再計算。
    //
    // ここではSmooth処理を進めない。
    // =========================================================

    private void OnBeforeRender()
    {
        if (
            !useBeforeRenderSafetyCheck ||
            !debugInitialized ||
            !isActiveAndEnabled
        )
        {
            return;
        }


        EmergencyFitCheck();
    }


    // =========================================================
    // Main Framing
    // =========================================================

    private void UpdateFraming(
        bool instant)
    {
        if (
            targetRenderer == null ||
            _camera == null
        )
        {
            return;
        }


        Bounds bounds =
            targetRenderer.bounds;


        if (
            bounds.extents.sqrMagnitude
            <
            0.0000001f
        )
        {
            return;
        }


        // =====================================================
        // Current model center movement
        // =====================================================

        Vector3 centerDelta =
            bounds.center -
            _baseBoundsCenter;


        Vector3 right =
            _baseCameraRotation *
            Vector3.right;


        Vector3 up =
            _baseCameraRotation *
            Vector3.up;


        Vector3 forward =
            _baseCameraRotation *
            Vector3.forward;


        float rawX =
            Vector3.Dot(
                centerDelta,
                right
            );


        float rawY =
            Vector3.Dot(
                centerDelta,
                up
            );


        // =====================================================
        // Dead Zone
        // =====================================================

        float deadX =
            _baseHalfWidth *
            centerDeadZoneFraction;


        float deadY =
            _baseHalfHeight *
            centerDeadZoneFraction;


        rawX =
            ApplyDeadZone(
                rawX,
                deadX
            );


        rawY =
            ApplyDeadZone(
                rawY,
                deadY
            );


        // =====================================================
        // Partial follow
        //
        // 100%追従させないことで、
        // キウイ自身の移動感を残す。
        // =====================================================

        float effectiveHorizontalFollow =
            preserveLandmarkerTranslation ? 0f : horizontalFollow;

        float effectiveVerticalFollow =
            preserveLandmarkerTranslation ? 0f : verticalFollow;

        float targetFollowX =
            rawX *
            effectiveHorizontalFollow;

        float targetFollowY =
            rawY *
            effectiveVerticalFollow;


        if (instant)
        {
            _currentFollowX =
                targetFollowX;


            _currentFollowY =
                targetFollowY;


            _followVelocityX =
                0f;


            _followVelocityY =
                0f;
        }
        else
        {
            float deltaTime =
                Mathf.Max(
                    0.0001f,
                    Time.unscaledDeltaTime
                );


            _currentFollowX =
                Mathf.SmoothDamp(
                    _currentFollowX,
                    targetFollowX,
                    ref _followVelocityX,
                    followSmoothTime,
                    Mathf.Infinity,
                    deltaTime
                );


            _currentFollowY =
                Mathf.SmoothDamp(
                    _currentFollowY,
                    targetFollowY,
                    ref _followVelocityY,
                    followSmoothTime,
                    Mathf.Infinity,
                    deltaTime
                );
        }


        debugFollowX =
            _currentFollowX;


        debugFollowY =
            _currentFollowY;


        // =====================================================
        // Lateral camera base position
        // =====================================================

        Vector3 lateralPosition =
            _baseCameraPosition
            +
            right *
            _currentFollowX
            +
            up *
            _currentFollowY;


        // =====================================================
        // Required zoom-out
        // =====================================================

        float requiredExtra =
            0f;


        if (enableAutoZoomOut)
        {
            requiredExtra =
                CalculateRequiredExtraDistance(
                    bounds,
                    lateralPosition
                );
        }


        requiredExtra =
            Mathf.Clamp(
                requiredExtra,
                0f,
                maximumExtraDistance
            );


        debugRequiredExtraDistance =
            requiredExtra;


        // =====================================================
        // ★Zoom OUT = immediate
        //
        // 切れそうな時にSmoothすると
        // 一瞬画面外へ出るため、
        // 後退だけは即時。
        //
        // ★Zoom IN = smooth
        //
        // 戻る時だけゆっくり。
        // =====================================================

        if (
            instant ||
            requiredExtra >
            _extraDistance
        )
        {
            _extraDistance =
                requiredExtra;


            _zoomVelocity =
                0f;
        }
        else
        {
            float deltaTime =
                Mathf.Max(
                    0.0001f,
                    Time.unscaledDeltaTime
                );


            _extraDistance =
                Mathf.SmoothDamp(
                    _extraDistance,
                    requiredExtra,
                    ref _zoomVelocity,
                    zoomReturnSmoothTime,
                    Mathf.Infinity,
                    deltaTime
                );
        }


        debugExtraDistance =
            _extraDistance;


        // =====================================================
        // Final Camera Transform
        //
        // FOVは固定。
        // Camera距離だけを変更。
        // =====================================================

        transform.position =
            lateralPosition
            -
            forward *
            _extraDistance;


        transform.rotation =
            _baseCameraRotation;
    }


    // =========================================================
    // Emergency fit
    //
    // BeforeRenderでは切れそうな場合だけ
    // Cameraをさらに後退させる。
    //
    // Cameraを前へ戻す処理はしない。
    // =========================================================

    private void EmergencyFitCheck()
    {
        if (
            targetRenderer == null ||
            _camera == null ||
            !enableAutoZoomOut
        )
        {
            return;
        }


        Bounds bounds =
            targetRenderer.bounds;


        Vector3 right =
            _baseCameraRotation *
            Vector3.right;


        Vector3 up =
            _baseCameraRotation *
            Vector3.up;


        Vector3 forward =
            _baseCameraRotation *
            Vector3.forward;


        Vector3 lateralPosition =
            _baseCameraPosition
            +
            right *
            _currentFollowX
            +
            up *
            _currentFollowY;


        float requiredExtra =
            CalculateRequiredExtraDistance(
                bounds,
                lateralPosition
            );


        requiredExtra =
            Mathf.Clamp(
                requiredExtra,
                0f,
                maximumExtraDistance
            );


        if (
            requiredExtra <=
            _extraDistance
        )
        {
            return;
        }


        // =====================================================
        // Immediate safety zoom-out
        // =====================================================

        _extraDistance =
            requiredExtra;


        _zoomVelocity =
            0f;


        debugExtraDistance =
            _extraDistance;


        debugRequiredExtraDistance =
            requiredExtra;


        transform.position =
            lateralPosition
            -
            forward *
            _extraDistance;


        transform.rotation =
            _baseCameraRotation;
    }


    // =========================================================
    // Calculate Distance Required To Fit Bounds
    //
    // Perspective frustumを逆算して
    // 全8角がSafe Frame内に入る
    // 最小追加距離を求める。
    // =========================================================

    private float CalculateRequiredExtraDistance(
        Bounds bounds,
        Vector3 cameraBasePosition)
    {
        FillBoundsCorners(
            bounds
        );


        // =====================================================
        // Effective aspect
        //
        // RenderTextureがある場合は
        // RenderTextureのAspectを最優先。
        // =====================================================

        float aspect =
            GetOutputAspect();


        float verticalHalfAngle =
            _camera.fieldOfView *
            0.5f *
            Mathf.Deg2Rad;


        float tanVertical =
            Mathf.Tan(
                verticalHalfAngle
            );


        float tanHorizontal =
            tanVertical *
            aspect;


        // =====================================================
        // Safe area
        //
        // Margin 0.10
        //
        // usable half-frustum =
        // 80%
        // =====================================================

        float safeX =
            Mathf.Max(
                0.05f,
                1f -
                horizontalMargin *
                2f
            );


        float safeY =
            Mathf.Max(
                0.05f,
                1f -
                verticalMargin *
                2f
            );


        float safeTanX =
            Mathf.Max(
                0.0001f,
                tanHorizontal *
                safeX
            );


        float safeTanY =
            Mathf.Max(
                0.0001f,
                tanVertical *
                safeY
            );


        Quaternion inverseRotation =
            Quaternion.Inverse(
                _baseCameraRotation
            );


        float requiredExtra =
            0f;


        for (
            int i = 0;
            i < 8;
            i++
        )
        {
            Vector3 local =
                inverseRotation *
                (
                    _corners[i] -
                    cameraBasePosition
                );


            // =================================================
            // Required depth for X
            // =================================================

            float requiredDepthX =
                Mathf.Abs(
                    local.x
                )
                /
                safeTanX;


            // =================================================
            // Required depth for Y
            // =================================================

            float requiredDepthY =
                Mathf.Abs(
                    local.y
                )
                /
                safeTanY;


            // =================================================
            // Near Clip safety
            // =================================================

            float requiredDepthNear =
                _camera.nearClipPlane
                +
                nearClipPadding;


            float requiredDepth =
                Mathf.Max(
                    requiredDepthX,
                    requiredDepthY,
                    requiredDepthNear
                );


            // =================================================
            // Moving camera backward by D
            //
            // local.z becomes:
            //
            // local.z + D
            // =================================================

            float extra =
                requiredDepth -
                local.z;


            if (
                extra >
                requiredExtra
            )
            {
                requiredExtra =
                    extra;
            }
        }


        return Mathf.Max(
            0f,
            requiredExtra
        );
    }


    // =========================================================
    // Output Aspect
    // =========================================================

    private float GetOutputAspect()
    {
        if (
            _camera.targetTexture != null
            &&
            _camera.targetTexture.height >
            0
        )
        {
            return
                (float)
                _camera.targetTexture.width
                /
                _camera.targetTexture.height;
        }


        return Mathf.Max(
            0.01f,
            _camera.aspect
        );
    }


    // =========================================================
    // Dead Zone
    // =========================================================

    private float ApplyDeadZone(
        float value,
        float deadZone)
    {
        float absolute =
            Mathf.Abs(
                value
            );


        if (
            absolute <=
            deadZone
        )
        {
            return 0f;
        }


        return
            Mathf.Sign(
                value
            )
            *
            (
                absolute -
                deadZone
            );
    }


    // =========================================================
    // Initial projected half size
    // =========================================================

    private void CalculateProjectedHalfSize(
        Bounds bounds,
        out float halfWidth,
        out float halfHeight)
    {
        FillBoundsCorners(
            bounds
        );


        Vector3 right =
            _baseCameraRotation *
            Vector3.right;


        Vector3 up =
            _baseCameraRotation *
            Vector3.up;


        float minX =
            float.MaxValue;


        float maxX =
            float.MinValue;


        float minY =
            float.MaxValue;


        float maxY =
            float.MinValue;


        for (
            int i = 0;
            i < 8;
            i++
        )
        {
            Vector3 delta =
                _corners[i] -
                bounds.center;


            float x =
                Vector3.Dot(
                    delta,
                    right
                );


            float y =
                Vector3.Dot(
                    delta,
                    up
                );


            minX =
                Mathf.Min(
                    minX,
                    x
                );


            maxX =
                Mathf.Max(
                    maxX,
                    x
                );


            minY =
                Mathf.Min(
                    minY,
                    y
                );


            maxY =
                Mathf.Max(
                    maxY,
                    y
                );
        }


        halfWidth =
            Mathf.Max(
                0.0001f,
                (
                    maxX -
                    minX
                )
                *
                0.5f
            );


        halfHeight =
            Mathf.Max(
                0.0001f,
                (
                    maxY -
                    minY
                )
                *
                0.5f
            );
    }


    // =========================================================
    // Bounds Corners
    // =========================================================

    private void FillBoundsCorners(
        Bounds bounds)
    {
        Vector3 min =
            bounds.min;


        Vector3 max =
            bounds.max;


        _corners[0] =
            new Vector3(
                min.x,
                min.y,
                min.z
            );


        _corners[1] =
            new Vector3(
                max.x,
                min.y,
                min.z
            );


        _corners[2] =
            new Vector3(
                min.x,
                max.y,
                min.z
            );


        _corners[3] =
            new Vector3(
                max.x,
                max.y,
                min.z
            );


        _corners[4] =
            new Vector3(
                min.x,
                min.y,
                max.z
            );


        _corners[5] =
            new Vector3(
                max.x,
                min.y,
                max.z
            );


        _corners[6] =
            new Vector3(
                min.x,
                max.y,
                max.z
            );


        _corners[7] =
            new Vector3(
                max.x,
                max.y,
                max.z
            );
    }


    // =========================================================
    // Auto Find Renderer
    // =========================================================

    private Renderer FindLargestRenderer()
    {
        if (targetRoot == null)
        {
            return null;
        }


        Renderer[] renderers =
            targetRoot
                .GetComponentsInChildren<
                    Renderer
                >(
                    true
                );


        Renderer best =
            null;


        float bestSize =
            -1f;


        for (
            int i = 0;
            i < renderers.Length;
            i++
        )
        {
            Renderer renderer =
                renderers[i];


            if (renderer == null)
            {
                continue;
            }


            Bounds bounds =
                renderer.bounds;


            float size =
                bounds.size.x
                *
                bounds.size.y
                *
                bounds.size.z;


            if (
                size >
                bestSize
            )
            {
                bestSize =
                    size;


                best =
                    renderer;
            }
        }


        return best;
    }


    // =========================================================
    // Restore
    // =========================================================

    private void RestoreSkinnedRendererSetting()
    {
        if (_skinnedRenderer == null)
        {
            return;
        }


        _skinnedRenderer
            .updateWhenOffscreen =
            _previousUpdateWhenOffscreen;
    }


    // =========================================================
    // Recenter
    //
    // Play中に現在位置を新しい基準にしたい時用。
    // =========================================================

    [ContextMenu("Capture Current Framing As Base")]
    public void CaptureCurrentFramingAsBase()
    {
        if (_camera == null)
        {
            _camera =
                GetComponent<Camera>();
        }


        if (targetRenderer == null)
        {
            targetRenderer =
                FindLargestRenderer();
        }


        if (
            _camera == null ||
            targetRenderer == null
        )
        {
            return;
        }


        // =====================================================
        // 現在のExtra Distanceを除いた位置を
        // 新しいBaseとして保存。
        // =====================================================

        Vector3 forward =
            transform.forward;


        _baseCameraPosition =
            transform.position
            +
            forward *
            _extraDistance;


        _baseCameraRotation =
            transform.rotation;


        Bounds bounds =
            targetRenderer.bounds;


        _baseBoundsCenter =
            bounds.center;


        CalculateProjectedHalfSize(
            bounds,
            out _baseHalfWidth,
            out _baseHalfHeight
        );


        _currentFollowX =
            0f;


        _currentFollowY =
            0f;


        _followVelocityX =
            0f;


        _followVelocityY =
            0f;


        _extraDistance =
            0f;


        _zoomVelocity =
            0f;


        UpdateFraming(
            true
        );
    }
}