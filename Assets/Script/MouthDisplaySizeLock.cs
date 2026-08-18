using UnityEngine;


[DefaultExecutionOrder(860)]
[DisallowMultipleComponent]
[RequireComponent(typeof(SurfaceFittedRawImage))]
public class MouthDisplaySizeLock : MonoBehaviour
{
    // =========================================================
    // Purpose
    // =========================================================
    //
    // Landmarker の uvRect / 輪郭 / 位置 / 角度には一切手を加えず、
    // 最終表示だけを一定倍率に縮小する。
    //
    // FacePartSoftMask の _SampleScaleXY は
    // 1 より大きいほど表示結果が小さくなるため、
    // 表示倍率 0.50 -> SampleScale 2.00 とする。
    //
    // 時間平滑化・キャリブレーション・DeadZone は使用しない。
    // そのため Landmarker 追従遅延は増えない。
    // =========================================================


    [Header("Maximum Mouth Display Size")]

    [Tooltip(
        "1.00 = 現在サイズ / 0.50 = 現在の約半分。Landmarkerの追従そのものは変更しません。"
    )]
    [Range(0.25f, 1.00f)]
    public float maximumVisibleScale =
        0.50f;


    [Tooltip(
        "横方向にも最大表示倍率を適用します。"
    )]
    public bool limitWidth =
        true;


    [Tooltip(
        "縦方向にも最大表示倍率を適用します。"
    )]
    public bool limitHeight =
        true;


    // =========================================================
    // Debug
    // =========================================================

    [Header("Debug")]

    [SerializeField]
    private float debugAppliedSampleScaleX =
        2.00f;


    [SerializeField]
    private float debugAppliedSampleScaleY =
        2.00f;


    // =========================================================
    // Runtime
    // =========================================================

    private SurfaceFittedRawImage _image;
    private Material _lastMaterial;


    private static readonly int SampleScaleId =
        Shader.PropertyToID(
            "_SampleScale"
        );


    private static readonly int SampleScaleXYId =
        Shader.PropertyToID(
            "_SampleScaleXY"
        );


    // =========================================================
    // Unity
    // =========================================================

    private void Awake()
    {
        CacheImage();
    }


    private void OnEnable()
    {
        CacheImage();
        ApplyMaximumSize();
    }


    private void Start()
    {
        CacheImage();
        ApplyMaximumSize();
    }


    private void LateUpdate()
    {
        if (_image == null)
        {
            CacheImage();
        }


        if (
            _image == null ||
            _image.material == null
        )
        {
            return;
        }


        // FacePartShapeMask が runtime material を作り直した場合だけでなく、
        // Inspector から倍率を変更した場合も即時反映するため毎 LateUpdate 適用する。
        // SetFloat / SetVector のみなので追従経路への時間フィルタは発生しない。
        ApplyMaximumSize();
    }


    private void OnValidate()
    {
        maximumVisibleScale =
            Mathf.Clamp(
                maximumVisibleScale,
                0.25f,
                1.00f
            );


        if (Application.isPlaying)
        {
            CacheImage();
            ApplyMaximumSize();
        }
    }


    private void OnDisable()
    {
        ResetMaterialScale();
    }


    // =========================================================
    // Cache
    // =========================================================

    private void CacheImage()
    {
        if (_image == null)
        {
            _image =
                GetComponent<
                    SurfaceFittedRawImage
                >();
        }
    }


    // =========================================================
    // Apply
    // =========================================================

    private void ApplyMaximumSize()
    {
        if (
            _image == null ||
            _image.material == null
        )
        {
            return;
        }


        Material material =
            _image.material;


        _lastMaterial =
            material;


        // 0.50 倍表示なら shader sample scale は 2.00。
        // visibleScale = 1 / sampleScale
        float visibleScale =
            Mathf.Clamp(
                maximumVisibleScale,
                0.25f,
                1.00f
            );


        float inverseScale =
            1.00f /
            visibleScale;


        float scaleX =
            limitWidth
                ? inverseScale
                : 1.00f;


        float scaleY =
            limitHeight
                ? inverseScale
                : 1.00f;


        // Legacy uniform scale は常に Neutral。
        if (
            material.HasProperty(
                SampleScaleId
            )
        )
        {
            material.SetFloat(
                SampleScaleId,
                1.00f
            );
        }


        if (
            material.HasProperty(
                SampleScaleXYId
            )
        )
        {
            material.SetVector(
                SampleScaleXYId,
                new Vector4(
                    scaleX,
                    scaleY,
                    0.00f,
                    0.00f
                )
            );
        }


        debugAppliedSampleScaleX =
            scaleX;


        debugAppliedSampleScaleY =
            scaleY;
    }


    // =========================================================
    // Reset
    // =========================================================

    private void ResetMaterialScale()
    {
        Material material =
            _image != null &&
            _image.material != null
                ? _image.material
                : _lastMaterial;


        if (material == null)
        {
            return;
        }


        if (
            material.HasProperty(
                SampleScaleId
            )
        )
        {
            material.SetFloat(
                SampleScaleId,
                1.00f
            );
        }


        if (
            material.HasProperty(
                SampleScaleXYId
            )
        )
        {
            material.SetVector(
                SampleScaleXYId,
                new Vector4(
                    1.00f,
                    1.00f,
                    0.00f,
                    0.00f
                )
            );
        }
    }
}