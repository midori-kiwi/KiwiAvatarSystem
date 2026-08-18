using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;


// =============================================================
// FacePartCropper       → 先に通常Crop
// MouthCropGuard  800   → 口位置・固定サイズを確定
// FacePartShapeMask 850 → 確定したUVでマスク
// =============================================================

[DefaultExecutionOrder(800)]
[DisallowMultipleComponent]
[RequireComponent(typeof(SurfaceFittedRawImage))]
public class MouthCropGuard : MonoBehaviour
{
    [Header("Landmarker Direct Tracking")]
    [Tooltip("ON: FacePartCropper remains the only uvRect writer; this guard only enforces mouth visual zoom 1:1.")]
    public bool strictLandmarkerTracking = true;


    // =========================================================
    // References
    // =========================================================

    [Header("References")]

    [Tooltip("Solution の FaceLandmarkerRunner")]
    public FaceLandmarkerRunner runner;


    [Tooltip("Mouth3D。空なら自動取得")]
    public SurfaceFittedRawImage mouthImage;


    // =========================================================
    // Fixed Size
    //
    // 口を開いても閉じてもサイズ変更しない。
    // =========================================================

    [Header("Fixed Mouth Size")]

    [Tooltip(
        "基準Cropに対する横幅倍率。大きいほど口が小さく表示される"
    )]
    [Range(1f, 3f)]
    public float fixedWidthMultiplier = 1.50f;


    [Tooltip(
        "基準Cropに対する縦幅倍率。見切れ防止"
    )]
    [Range(1f, 3f)]
    public float fixedHeightMultiplier = 2.10f;


    // =========================================================
    // Position Lock
    //
    // ★今回の重要部分
    // =========================================================

    [Header("Mouth Position Lock")]

    [Tooltip(
        "正面時の口の高さを維持する"
    )]
    public bool lockMouthHeight = true;


    [Tooltip(
        "正面時の横位置も同じ基準位置へ固定する"
    )]
    public bool lockMouthHorizontalPlacement = true;


    [Tooltip(
        "口角基準点の微小ノイズだけ無視する"
    )]
    [Range(0f, 0.005f)]
    public float anchorDeadZone = 0.00015f;


    // =========================================================
    // Calibration
    // =========================================================

    [Header("Front Calibration")]

    [Tooltip(
        "正面を向いた状態で基準を取得するまでの待ち時間"
    )]
    [Range(0f, 2f)]
    public float captureDelay = 0.40f;


    [Tooltip(
        "正面基準を平均するサンプル数"
    )]
    [Range(1, 20)]
    public int captureSamples = 8;


    [Range(0.0001f, 0.1f)]
    public float minimumValidCropSize = 0.005f;


    // =========================================================
    // Visual Zoom
    // =========================================================

    [Header("Visual Zoom")]

    [Tooltip(
        "口の拡大縮小を完全に禁止"
    )]
    public bool forceVisualZoomOne = true;


    // =========================================================
    // UV Safety
    // =========================================================

    [Header("UV Safety")]

    public bool clampInsideTexture = true;


    // =========================================================
    // Debug
    // =========================================================

    [Header("Debug")]

    public bool logCalibration = false;


    [SerializeField]
    private float debugAnchorU = 0.5f;


    [SerializeField]
    private float debugAnchorV = 0.5f;


    // =========================================================
    // Stable Mouth Anchor
    //
    // 61  = 口角
    // 291 = 反対側の口角
    //
    // 唇20点平均より、
    // 開閉・斜め向きによる上下変化が少ない。
    // =========================================================

    private const int MouthCornerA = 61;
    private const int MouthCornerB = 291;


    // =========================================================
    // Runtime
    // =========================================================

    private Vector2[] _landmarks =
        new Vector2[478];


    private float _enableTime;


    private long _lastTimestamp = -1;


    // =========================================================
    // Orientation
    // =========================================================

    private bool _orientationResolved = false;

    private bool _mirrorX = false;

    private bool _flipY = false;


    // =========================================================
    // Calibration accumulators
    // =========================================================

    private long _lastCaptureTimestamp = -1;

    private int _captureCount = 0;


    private float _widthSum = 0f;

    private float _heightSum = 0f;


    private float _anchorUSum = 0f;

    private float _anchorVSum = 0f;


    // =========================================================
    // Fixed result
    // =========================================================

    private bool _calibrated = false;


    private float _fixedWidth = 0f;

    private float _fixedHeight = 0f;


    // =========================================================
    // Reference position inside Mouth3D
    //
    // 0 = 下/左端
    // 0.5 = 中央
    // 1 = 上/右端
    //
    // 正面時の位置を記憶する。
    // =========================================================

    private float _referenceAnchorU = 0.5f;

    private float _referenceAnchorV = 0.5f;


    // =========================================================
    // Current mouth anchor
    // =========================================================

    private bool _hasAnchor = false;

    private Vector2 _heldAnchor;


    // =========================================================
    // Final Rect
    // =========================================================

    private bool _hasFinalRect = false;

    private Rect _finalRect;


    // =========================================================
    // Enable
    // =========================================================

    private void OnEnable()
    {
        _enableTime =
            Time.unscaledTime;


        ResetInternalState();
    }


    // =========================================================
    // Start
    // =========================================================

    private void Start()
    {
        if (mouthImage == null)
        {
            mouthImage =
                GetComponent<
                    SurfaceFittedRawImage
                >();
        }


        if (runner == null)
        {
            runner =
                FindFirstObjectByType<
                    FaceLandmarkerRunner
                >();
        }


        ForceNoVisualZoom();
    }


    // =========================================================
    // Late Update
    // =========================================================

    private void LateUpdate()
    {
        if (
            runner == null ||
            mouthImage == null
        )
        {
            return;
        }


        // =====================================================
        // 拡大縮小を完全禁止
        // =====================================================

        ForceNoVisualZoom();

        if (strictLandmarkerTracking)
        {
            return;
        }


        int landmarkCount = 0;

        long timestamp = 0;


        bool hasNewLandmarks =
            runner.TryGetLatestLandmarksIfChanged(
                ref _landmarks,
                _lastTimestamp,
                out landmarkCount,
                out timestamp,
                out bool hasFace
            );


        if (
            !hasFace ||
            !ValidateLandmarks(
                landmarkCount
            )
        )
        {
            ApplyFinalRect();

            return;
        }


        // =====================================================
        // 正面基準キャリブレーション
        // =====================================================

        if (!_calibrated)
        {
            CaptureFrontReference(
                timestamp
            );


            return;
        }


        // =====================================================
        // 新しいMediaPipe結果だけ処理
        // =====================================================

        if (hasNewLandmarks)
        {
            UpdateStableAnchor();


            BuildFinalRect();


            _lastTimestamp =
                timestamp;
        }


        // FacePartCropperが毎フレームuvRectを更新しても
        // 最後に固定Rectを再適用。
        ApplyFinalRect();
    }


    // =========================================================
    // Front Reference Capture
    //
    // ★正面を向いた状態で、
    //
    // 口角中点がMouth3D内の
    // どの位置に描画されているかを保存する。
    // =========================================================

    private void CaptureFrontReference(
        long timestamp)
    {
        if (
            Time.unscaledTime -
            _enableTime
            <
            captureDelay
        )
        {
            return;
        }


        if (
            timestamp ==
            _lastCaptureTimestamp
        )
        {
            return;
        }


        Rect sourceRect =
            mouthImage.uvRect;


        float width =
            Mathf.Abs(
                sourceRect.width
            );


        float height =
            Mathf.Abs(
                sourceRect.height
            );


        if (
            width <
            minimumValidCropSize
            ||
            height <
            minimumValidCropSize
        )
        {
            return;
        }


        // =====================================================
        // Texture方向は最初の有効サンプルで一度だけ決定
        // =====================================================

        if (!_orientationResolved)
        {
            ResolveOrientation(
                sourceRect
            );
        }


        Vector2 mouthAnchor =
            GetOrientedMouthAnchor();


        // =====================================================
        // 現在のCrop内で、
        // 口角中点が何%位置にあるか
        // =====================================================

        float anchorU =
            (
                mouthAnchor.x -
                sourceRect.x
            )
            /
            Mathf.Max(
                0.000001f,
                sourceRect.width
            );


        float anchorV =
            (
                mouthAnchor.y -
                sourceRect.y
            )
            /
            Mathf.Max(
                0.000001f,
                sourceRect.height
            );


        anchorU =
            Mathf.Clamp01(
                anchorU
            );


        anchorV =
            Mathf.Clamp01(
                anchorV
            );


        // =====================================================
        // Average
        // =====================================================

        _widthSum +=
            width;


        _heightSum +=
            height;


        _anchorUSum +=
            anchorU;


        _anchorVSum +=
            anchorV;


        _captureCount++;


        _lastCaptureTimestamp =
            timestamp;


        if (
            _captureCount <
            Mathf.Max(
                1,
                captureSamples
            )
        )
        {
            return;
        }


        // =====================================================
        // Fixed Crop Size
        // =====================================================

        float averageWidth =
            _widthSum /
            _captureCount;


        float averageHeight =
            _heightSum /
            _captureCount;


        _fixedWidth =
            averageWidth *
            fixedWidthMultiplier;


        _fixedHeight =
            averageHeight *
            fixedHeightMultiplier;


        _fixedWidth =
            Mathf.Clamp(
                _fixedWidth,
                minimumValidCropSize,
                1f
            );


        _fixedHeight =
            Mathf.Clamp(
                _fixedHeight,
                minimumValidCropSize,
                1f
            );


        // =====================================================
        // ★正面時の表示位置を保存
        // =====================================================

        _referenceAnchorU =
            _anchorUSum /
            _captureCount;


        _referenceAnchorV =
            _anchorVSum /
            _captureCount;


        _referenceAnchorU =
            Mathf.Clamp01(
                _referenceAnchorU
            );


        _referenceAnchorV =
            Mathf.Clamp01(
                _referenceAnchorV
            );


        debugAnchorU =
            _referenceAnchorU;


        debugAnchorV =
            _referenceAnchorV;


        // =====================================================
        // Initial anchor
        // =====================================================

        _heldAnchor =
            GetOrientedMouthAnchor();


        _hasAnchor =
            true;


        _calibrated =
            true;


        BuildFinalRect();


        ApplyFinalRect();


        if (logCalibration)
        {
            Debug.Log(
                "[MouthCropGuard]"
                +
                "\nFront Mouth Reference Ready"
                +
                "\nFixed Width = "
                +
                _fixedWidth
                +
                "\nFixed Height = "
                +
                _fixedHeight
                +
                "\nAnchor U = "
                +
                _referenceAnchorU
                +
                "\nAnchor V = "
                +
                _referenceAnchorV,
                this
            );
        }
    }


    // =========================================================
    // Stable Mouth Anchor
    //
    // ★口の外周20点平均は使わない。
    //
    // 61 / 291 の口角中点だけ使う。
    // =========================================================

    private void UpdateStableAnchor()
    {
        Vector2 target =
            GetOrientedMouthAnchor();


        if (!_hasAnchor)
        {
            _heldAnchor =
                target;


            _hasAnchor =
                true;


            return;
        }


        Vector2 delta =
            target -
            _heldAnchor;


        // =====================================================
        // X / Y 個別DeadZone
        //
        // Lerpは使わない。
        // =====================================================

        if (
            Mathf.Abs(
                delta.x
            )
            >
            anchorDeadZone
        )
        {
            _heldAnchor.x =
                target.x;
        }


        if (
            Mathf.Abs(
                delta.y
            )
            >
            anchorDeadZone
        )
        {
            _heldAnchor.y =
                target.y;
        }
    }


    // =========================================================
    // Build Rect
    //
    // ★今回の核心
    //
    // source mouth anchor
    // ↓
    // 常に正面時と同じU/V位置へマッピング
    //
    // そのため横・斜め・口開閉でも
    // キウイ上の口の高さが変わらない。
    // =========================================================

    private void BuildFinalRect()
    {
        if (
            !_calibrated ||
            !_hasAnchor
        )
        {
            return;
        }


        float anchorU =
            lockMouthHorizontalPlacement
                ?
                _referenceAnchorU
                :
                0.5f;


        float anchorV =
            lockMouthHeight
                ?
                _referenceAnchorV
                :
                0.5f;


        float x =
            _heldAnchor.x
            -
            _fixedWidth *
            anchorU;


        float y =
            _heldAnchor.y
            -
            _fixedHeight *
            anchorV;


        Rect rect =
            new Rect(
                x,
                y,
                _fixedWidth,
                _fixedHeight
            );


        if (clampInsideTexture)
        {
            rect =
                ClampRectInside01(
                    rect
                );
        }


        _finalRect =
            rect;


        _hasFinalRect =
            true;
    }


    // =========================================================
    // Apply
    // =========================================================

    private void ApplyFinalRect()
    {
        if (
            !_hasFinalRect ||
            mouthImage == null
        )
        {
            return;
        }


        mouthImage.uvRect =
            _finalRect;
    }


    // =========================================================
    // Mouth Anchor
    //
    // 口角2点の中点。
    //
    // 口を開いても下唇に引っ張られない。
    // =========================================================

    private Vector2 GetRawMouthAnchor()
    {
        Vector2 cornerA =
            _landmarks[
                MouthCornerA
            ];


        Vector2 cornerB =
            _landmarks[
                MouthCornerB
            ];


        return
            (
                cornerA +
                cornerB
            )
            *
            0.5f;
    }


    private Vector2 GetOrientedMouthAnchor()
    {
        Vector2 p =
            GetRawMouthAnchor();


        if (_mirrorX)
        {
            p.x =
                1f -
                p.x;
        }


        if (_flipY)
        {
            p.y =
                1f -
                p.y;
        }


        return p;
    }


    // =========================================================
    // Orientation
    // =========================================================

    private void ResolveOrientation(
        Rect currentCrop)
    {
        Vector2 rawAnchor =
            GetRawMouthAnchor();


        Vector2 cropCenter =
            new Vector2(
                currentCrop.x
                +
                currentCrop.width *
                0.5f,

                currentCrop.y
                +
                currentCrop.height *
                0.5f
            );


        float bestDistance =
            float.MaxValue;


        bool bestMirror =
            false;


        bool bestFlip =
            false;


        for (
            int mx = 0;
            mx < 2;
            mx++
        )
        {
            for (
                int fy = 0;
                fy < 2;
                fy++
            )
            {
                bool mirror =
                    mx == 1;


                bool flip =
                    fy == 1;


                Vector2 candidate =
                    rawAnchor;


                if (mirror)
                {
                    candidate.x =
                        1f -
                        candidate.x;
                }


                if (flip)
                {
                    candidate.y =
                        1f -
                        candidate.y;
                }


                float distance =
                    Vector2.SqrMagnitude(
                        candidate -
                        cropCenter
                    );


                if (
                    distance <
                    bestDistance
                )
                {
                    bestDistance =
                        distance;


                    bestMirror =
                        mirror;


                    bestFlip =
                        flip;
                }
            }
        }


        _mirrorX =
            bestMirror;


        _flipY =
            bestFlip;


        _orientationResolved =
            true;
    }


    // =========================================================
    // No Visual Zoom
    // =========================================================

    private void ForceNoVisualZoom()
    {
        if (
            !forceVisualZoomOne ||
            mouthImage == null
        )
        {
            return;
        }


        mouthImage.ResetVisualZoom();
    }


    // =========================================================
    // Validate
    // =========================================================

    private bool ValidateLandmarks(
        int landmarkCount)
    {
        return
            MouthCornerA >= 0 &&
            MouthCornerA <
            landmarkCount &&
            MouthCornerB >= 0 &&
            MouthCornerB <
            landmarkCount;
    }


    // =========================================================
    // Clamp
    // =========================================================

    private Rect ClampRectInside01(
        Rect rect)
    {
        float width =
            Mathf.Clamp(
                rect.width,
                minimumValidCropSize,
                1f
            );


        float height =
            Mathf.Clamp(
                rect.height,
                minimumValidCropSize,
                1f
            );


        float x =
            Mathf.Clamp(
                rect.x,
                0f,
                1f -
                width
            );


        float y =
            Mathf.Clamp(
                rect.y,
                0f,
                1f -
                height
            );


        return new Rect(
            x,
            y,
            width,
            height
        );
    }


    // =========================================================
    // Recalibrate
    //
    // 必ず正面を向いて実行。
    // =========================================================

    [ContextMenu("Recalibrate Front Mouth Position")]
    public void RecalibrateFrontMouthPosition()
    {
        _enableTime =
            Time.unscaledTime;


        ResetInternalState();


        ForceNoVisualZoom();
    }


    // =========================================================
    // Reset
    // =========================================================

    private void ResetInternalState()
    {
        _orientationResolved =
            false;


        _mirrorX =
            false;


        _flipY =
            false;


        _lastTimestamp =
            -1;


        _lastCaptureTimestamp =
            -1;


        _captureCount =
            0;


        _widthSum =
            0f;


        _heightSum =
            0f;


        _anchorUSum =
            0f;


        _anchorVSum =
            0f;


        _calibrated =
            false;


        _fixedWidth =
            0f;


        _fixedHeight =
            0f;


        _referenceAnchorU =
            0.5f;


        _referenceAnchorV =
            0.5f;


        _hasAnchor =
            false;


        _heldAnchor =
            Vector2.zero;


        _hasFinalRect =
            false;


        _finalRect =
            default;


        debugAnchorU =
            0.5f;


        debugAnchorV =
            0.5f;
    }
}
