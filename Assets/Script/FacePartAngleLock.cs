using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;


[DefaultExecutionOrder(875)]
[DisallowMultipleComponent]
[RequireComponent(typeof(SurfaceFittedRawImage))]
public class FacePartAngleLock : MonoBehaviour
{
    [Header("Landmarker Direct Tracking")]
    [Tooltip("ON: angle compensation uses each newest Landmarker angle with zero temporal dead-zone.")]
    public bool strictLandmarkerTracking = true;


    // =========================================================
    // Part Type
    // =========================================================

    public enum PartType
    {
        Auto = 0,

        Eye = 1,

        Mouth = 2
    }


    // =========================================================
    // References
    // =========================================================

    [Header("References")]

    [Tooltip(
        "Solution の FaceLandmarkerRunner"
    )]
    public FaceLandmarkerRunner runner;


    // =========================================================
    // Part
    // =========================================================

    [Header("Face Part")]

    public PartType partType =
        PartType.Auto;


    // =========================================================
    // Angle Lock
    // =========================================================

    [Header("Angle Lock")]

    [Tooltip(
        "Camera映像内の目・口の回転を相殺する"
    )]
    public bool enableAngleLock =
        true;


    [Tooltip(
        "1.0 = Camera側の角度変化を100%相殺。\n"
        +
        "3Dキウイ本体の回転だけが残る。"
    )]
    [Range(0f, 1f)]
    public float angleLockStrength =
        1.0f;


    [Tooltip(
        "この角度以下の変化はノイズとして無視"
    )]
    [Range(0f, 3f)]
    public float angleDeadZone =
        0.20f;


    [Tooltip(
        "異常検出時に回りすぎないための最大補正角度"
    )]
    [Range(5f, 90f)]
    public float maximumCorrectionAngle =
        55f;


    // =========================================================
    // Front Calibration
    // =========================================================

    [Header("Front Angle Calibration")]

    [Tooltip(
        "Play開始後、正面を向いて待つ時間"
    )]
    [Range(0f, 2f)]
    public float calibrationDelay =
        0.45f;


    [Tooltip(
        "正面角度を平均するサンプル数"
    )]
    [Range(3, 30)]
    public int calibrationSamples =
        10;


    // =========================================================
    // Eye Mapping
    // =========================================================

    [Header("Eye Mapping")]

    [Tooltip(
        "現在のUV Rectから正しい左右の目を自動選択"
    )]
    public bool automaticEyeMatching =
        true;


    [Tooltip(
        "一度決定した左右対応を固定する。\n"
        +
        "ウインク時の入れ替わり防止。"
    )]
    public bool lockEyeAssignment =
        true;


    // =========================================================
    // Debug
    // =========================================================

    [Header("Debug")]

    [SerializeField]
    private bool debugCalibrated =
        false;


    [SerializeField]
    private string debugTrackedPart =
        "-";


    [SerializeField]
    private float debugReferenceAngle =
        0f;


    [SerializeField]
    private float debugCurrentAngle =
        0f;


    [SerializeField]
    private float debugCorrectionAngle =
        0f;


    [SerializeField]
    private float debugSourceAspect =
        1f;


    public bool logCalibration =
        false;


    // =========================================================
    // MediaPipe landmarks
    // =========================================================

    // 362 / 263 側
    private static readonly int[] EyeAIndices =
    {
        362,
        398,
        384,
        385,
        386,
        387,
        388,
        466,

        263,

        249,
        390,
        373,
        374,
        380,
        381,
        382
    };


    // 33 / 133 側
    private static readonly int[] EyeBIndices =
    {
        33,
        246,
        161,
        160,
        159,
        158,
        157,
        173,

        133,

        155,
        154,
        153,
        145,
        144,
        163,
        7
    };


    private static readonly int[] MouthIndices =
    {
        61,
        185,
        40,
        39,
        37,
        0,
        267,
        269,
        270,
        409,
        291,

        375,
        321,
        405,
        314,
        17,
        84,
        181,
        91,
        146
    };


    // =========================================================
    // Important endpoints
    // =========================================================

    // Eye A
    private const int EyeAStart =
        362;


    private const int EyeAEnd =
        263;


    // Eye B
    private const int EyeBStart =
        33;


    private const int EyeBEnd =
        133;


    // Mouth
    private const int MouthStart =
        61;


    private const int MouthEnd =
        291;


    // =========================================================
    // Runtime
    // =========================================================

    private SurfaceFittedRawImage
        _image;


    private Vector2[] _landmarks =
        new Vector2[478];


    private long _lastTimestamp =
        -1;


    private float _enableTime;


    // =========================================================
    // Orientation
    // =========================================================

    private bool _orientationResolved =
        false;


    private bool _mirrorX =
        false;


    private bool _flipY =
        false;


    // =========================================================
    // Eye assignment
    //
    // -1 = none
    //  0 = Eye A
    //  1 = Eye B
    // =========================================================

    private int _selectedEye =
        -1;


    private bool _eyeAssignmentLocked =
        false;


    // =========================================================
    // Calibration
    //
    // Angle is 180° periodic.
    //
    // Therefore average:
    //
    // sin(2θ)
    // cos(2θ)
    //
    // instead of normal angle average.
    // =========================================================

    private int _calibrationCount =
        0;


    private long _lastCalibrationTimestamp =
        -1;


    private float _sumDoubleAngleSin =
        0f;


    private float _sumDoubleAngleCos =
        0f;


    private bool _calibrated =
        false;


    private float _referenceAngle =
        0f;


    // =========================================================
    // Held correction
    // =========================================================

    private float _heldCorrectionAngle =
        0f;


    // =========================================================
    // Shader IDs
    // =========================================================

    private static readonly int
        SamplePivotId =
            Shader.PropertyToID(
                "_SamplePivot"
            );


    private static readonly int
        SampleRotationRadId =
            Shader.PropertyToID(
                "_SampleRotationRad"
            );


    private static readonly int
        SourceAspectId =
            Shader.PropertyToID(
                "_SourceAspect"
            );


    // =========================================================
    // Enable
    // =========================================================

    private void OnEnable()
    {
        _enableTime =
            Time.unscaledTime;


        ResetAngleLock();
    }


    // =========================================================
    // Start
    // =========================================================

    private void Start()
    {
        _image =
            GetComponent<
                SurfaceFittedRawImage
            >();


        if (runner == null)
        {
            runner =
                FindObjectOfType<
                    FaceLandmarkerRunner
                >();
        }


        ResetShaderRotation();
    }


    // =========================================================
    // Late Update
    // =========================================================

    private void LateUpdate()
    {
        if (
            runner == null ||
            _image == null
        )
        {
            return;
        }


        Material material =
            _image.material;


        if (
            material == null ||
            !material.HasProperty(
                SampleRotationRadId
            )
        )
        {
            return;
        }


        // =====================================================
        // Disable
        // =====================================================

        if (!enableAngleLock)
        {
            SetRotation(
                material,
                Vector2.one *
                0.5f,
                0f
            );


            return;
        }


        // =====================================================
        // Landmarks
        // =====================================================

        int landmarkCount =
            0;


        long timestamp =
            0;


        bool valid =
            runner.TryGetLatestLandmarks(
                ref _landmarks,
                out landmarkCount,
                out timestamp
            );


        if (!valid)
        {
            return;
        }


        if (
            timestamp ==
            _lastTimestamp
        )
        {
            return;
        }


        _lastTimestamp =
            timestamp;


        // =====================================================
        // Texture aspect
        // =====================================================

        float aspect =
            GetSourceAspect();


        debugSourceAspect =
            aspect;


        material.SetFloat(
            SourceAspectId,
            aspect
        );


        // =====================================================
        // Resolve part
        // =====================================================

        PartType resolvedPart =
            ResolvePartType();


        // =====================================================
        // Resolve source feature
        // =====================================================

        int startIndex;

        int endIndex;


        if (
            resolvedPart ==
            PartType.Eye
        )
        {
            ResolveEyeAssignment(
                landmarkCount
            );


            if (_selectedEye == 0)
            {
                startIndex =
                    EyeAStart;


                endIndex =
                    EyeAEnd;


                debugTrackedPart =
                    "Eye A 362/263";
            }
            else
            {
                startIndex =
                    EyeBStart;


                endIndex =
                    EyeBEnd;


                debugTrackedPart =
                    "Eye B 33/133";
            }
        }
        else
        {
            ResolveMouthOrientation(
                landmarkCount
            );


            startIndex =
                MouthStart;


            endIndex =
                MouthEnd;


            debugTrackedPart =
                "Mouth 61/291";
        }


        if (
            startIndex >= landmarkCount ||
            endIndex >= landmarkCount
        )
        {
            return;
        }


        // =====================================================
        // Oriented endpoints
        // =====================================================

        Vector2 start =
            ApplyOrientation(
                _landmarks[startIndex]
            );


        Vector2 end =
            ApplyOrientation(
                _landmarks[endIndex]
            );


        // =====================================================
        // Pivot
        // =====================================================

        Vector2 pivot =
            (
                start +
                end
            )
            *
            0.5f;


        // =====================================================
        // Current feature angle
        //
        // ★Normalized UV angle is NOT enough.
        //
        // X is converted by texture aspect so
        // 16:9 / 4:3 cameras produce correct angles.
        // =====================================================

        float currentAngle =
            CalculateLineAngle(
                start,
                end,
                aspect
            );


        debugCurrentAngle =
            currentAngle;


        // =====================================================
        // Calibration
        // =====================================================

        if (!_calibrated)
        {
            ProcessCalibration(
                material,
                pivot,
                currentAngle,
                timestamp
            );


            return;
        }


        // =====================================================
        // 180-degree periodic delta
        // =====================================================

        float angleDelta =
            DeltaLineAngle(
                _referenceAngle,
                currentAngle
            );


        // =====================================================
        // Dead Zone
        // =====================================================

        float targetCorrection;


        float effectiveAngleDeadZone =
            strictLandmarkerTracking ? 0f : angleDeadZone;


        if (
            Mathf.Abs(
                angleDelta
            )
            <=
            effectiveAngleDeadZone
        )
        {
            targetCorrection =
                0f;
        }
        else
        {
            // =================================================
            // ★Full compensation
            //
            // Camera feature rotates +θ
            //
            // Sampling coordinates rotate +θ
            //
            // Displayed feature rotates -θ
            //
            // Result:
            // Camera-side rotation is cancelled.
            // =================================================

            targetCorrection =
                angleDelta *
                angleLockStrength;
        }


        targetCorrection =
            Mathf.Clamp(
                targetCorrection,
                -maximumCorrectionAngle,
                maximumCorrectionAngle
            );


        // =====================================================
        // ★NO LERP
        //
        // Tracking latencyを増やさない。
        // =====================================================

        _heldCorrectionAngle =
            targetCorrection;


        debugCorrectionAngle =
            _heldCorrectionAngle;


        SetRotation(
            material,
            pivot,
            _heldCorrectionAngle
        );
    }


    // =========================================================
    // Calibration
    // =========================================================

    private void ProcessCalibration(
        Material material,
        Vector2 pivot,
        float angle,
        long timestamp)
    {
        // Compensation off during calibration.
        SetRotation(
            material,
            pivot,
            0f
        );


        if (
            Time.unscaledTime -
            _enableTime
            <
            calibrationDelay
        )
        {
            return;
        }


        if (
            timestamp ==
            _lastCalibrationTimestamp
        )
        {
            return;
        }


        // =====================================================
        // 180° periodic angle averaging
        //
        // θ and θ+180 are the same line orientation.
        //
        // therefore use 2θ.
        // =====================================================

        float doubleRad =
            angle *
            2f *
            Mathf.Deg2Rad;


        _sumDoubleAngleSin +=
            Mathf.Sin(
                doubleRad
            );


        _sumDoubleAngleCos +=
            Mathf.Cos(
                doubleRad
            );


        _calibrationCount++;


        _lastCalibrationTimestamp =
            timestamp;


        if (
            _calibrationCount <
            Mathf.Max(
                1,
                calibrationSamples
            )
        )
        {
            return;
        }


        // =====================================================
        // Reference
        // =====================================================

        float meanDoubleAngle =
            Mathf.Atan2(
                _sumDoubleAngleSin,
                _sumDoubleAngleCos
            )
            *
            Mathf.Rad2Deg;


        _referenceAngle =
            NormalizeLineAngle(
                meanDoubleAngle *
                0.5f
            );


        _calibrated =
            true;


        debugCalibrated =
            true;


        debugReferenceAngle =
            _referenceAngle;


        if (logCalibration)
        {
            Debug.Log(
                "[FacePartAngleLock] "
                +
                gameObject.name
                +
                "\nReference Angle = "
                +
                _referenceAngle
                +
                " deg"
                +
                "\nSamples = "
                +
                _calibrationCount,
                this
            );
        }
    }


    // =========================================================
    // Line angle
    // =========================================================

    private float CalculateLineAngle(
        Vector2 start,
        Vector2 end,
        float aspect)
    {
        Vector2 delta =
            end -
            start;


        // X scale correction for normalized UV.
        delta.x *=
            aspect;


        float angle =
            Mathf.Atan2(
                delta.y,
                delta.x
            )
            *
            Mathf.Rad2Deg;


        return NormalizeLineAngle(
            angle
        );
    }


    // =========================================================
    // Normalize line angle
    //
    // A line does not have a direction.
    //
    // 0° = 180°
    //
    // Result:
    // -90 .. +90
    // =========================================================

    private float NormalizeLineAngle(
        float angle)
    {
        while (
            angle >
            90f
        )
        {
            angle -=
                180f;
        }


        while (
            angle <
            -90f
        )
        {
            angle +=
                180f;
        }


        return angle;
    }


    // =========================================================
    // Line angle delta
    //
    // 180° periodic.
    // =========================================================

    private float DeltaLineAngle(
        float reference,
        float current)
    {
        float delta =
            current -
            reference;


        while (
            delta >
            90f
        )
        {
            delta -=
                180f;
        }


        while (
            delta <
            -90f
        )
        {
            delta +=
                180f;
        }


        return delta;
    }


    // =========================================================
    // Eye Assignment
    // =========================================================

    private void ResolveEyeAssignment(
        int landmarkCount)
    {
        if (
            lockEyeAssignment &&
            _eyeAssignmentLocked &&
            _selectedEye >= 0
        )
        {
            return;
        }


        Rect uvRect =
            _image.uvRect;


        Vector2 cropCenter =
            new Vector2(
                uvRect.x +
                uvRect.width *
                0.5f,

                uvRect.y +
                uvRect.height *
                0.5f
            );


        float bestDistance =
            float.MaxValue;


        int bestEye =
            0;


        bool bestMirror =
            false;


        bool bestFlip =
            false;


        for (
            int eye = 0;
            eye < 2;
            eye++
        )
        {
            int[] indices =
                eye == 0
                ?
                EyeAIndices
                :
                EyeBIndices;


            if (
                !AreIndicesValid(
                    indices,
                    landmarkCount
                )
            )
            {
                continue;
            }


            Vector2 center =
                CalculateCenter(
                    indices
                );


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
                        center;


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


                        bestEye =
                            eye;


                        bestMirror =
                            mirror;


                        bestFlip =
                            flip;
                    }
                }
            }
        }


        _selectedEye =
            bestEye;


        _mirrorX =
            bestMirror;


        _flipY =
            bestFlip;


        _orientationResolved =
            true;


        if (lockEyeAssignment)
        {
            _eyeAssignmentLocked =
                true;
        }
    }


    // =========================================================
    // Mouth Orientation
    // =========================================================

    private void ResolveMouthOrientation(
        int landmarkCount)
    {
        if (_orientationResolved)
        {
            return;
        }


        if (
            !AreIndicesValid(
                MouthIndices,
                landmarkCount
            )
        )
        {
            return;
        }


        Rect uvRect =
            _image.uvRect;


        Vector2 cropCenter =
            new Vector2(
                uvRect.x +
                uvRect.width *
                0.5f,

                uvRect.y +
                uvRect.height *
                0.5f
            );


        Vector2 center =
            CalculateCenter(
                MouthIndices
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
                    center;


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
    // Center
    // =========================================================

    private Vector2 CalculateCenter(
        int[] indices)
    {
        Vector2 center =
            Vector2.zero;


        for (
            int i = 0;
            i < indices.Length;
            i++
        )
        {
            center +=
                _landmarks[
                    indices[i]
                ];
        }


        return
            center /
            Mathf.Max(
                1,
                indices.Length
            );
    }


    // =========================================================
    // Orientation
    // =========================================================

    private Vector2 ApplyOrientation(
        Vector2 point)
    {
        if (_mirrorX)
        {
            point.x =
                1f -
                point.x;
        }


        if (_flipY)
        {
            point.y =
                1f -
                point.y;
        }


        return point;
    }


    // =========================================================
    // Texture aspect
    // =========================================================

    private float GetSourceAspect()
    {
        if (
            _image == null ||
            _image.texture == null
        )
        {
            return 1f;
        }


        int width =
            _image.texture.width;


        int height =
            _image.texture.height;


        if (
            width <= 0 ||
            height <= 0
        )
        {
            return 1f;
        }


        return
            (float)width /
            height;
    }


    // =========================================================
    // Validate
    // =========================================================

    private bool AreIndicesValid(
        int[] indices,
        int landmarkCount)
    {
        if (indices == null)
        {
            return false;
        }


        for (
            int i = 0;
            i < indices.Length;
            i++
        )
        {
            int index =
                indices[i];


            if (
                index < 0 ||
                index >=
                landmarkCount
            )
            {
                return false;
            }
        }


        return true;
    }


    // =========================================================
    // Part Type
    // =========================================================

    private PartType ResolvePartType()
    {
        if (
            partType !=
            PartType.Auto
        )
        {
            return partType;
        }


        string lower =
            gameObject.name
            .ToLowerInvariant();


        if (
            lower.Contains(
                "mouth"
            )
        )
        {
            return PartType.Mouth;
        }


        return PartType.Eye;
    }


    // =========================================================
    // Material Update
    // =========================================================

    private void SetRotation(
        Material material,
        Vector2 pivot,
        float angleDegrees)
    {
        material.SetVector(
            SamplePivotId,
            new Vector4(
                pivot.x,
                pivot.y,
                0f,
                0f
            )
        );


        material.SetFloat(
            SampleRotationRadId,
            angleDegrees *
            Mathf.Deg2Rad
        );
    }


    // =========================================================
    // Recalibrate
    //
    // 正面を向いて実行。
    // =========================================================

    [ContextMenu("Recalibrate Front Angle")]
    public void RecalibrateFrontAngle()
    {
        _enableTime =
            Time.unscaledTime;


        _calibrationCount =
            0;


        _lastCalibrationTimestamp =
            -1;


        _sumDoubleAngleSin =
            0f;


        _sumDoubleAngleCos =
            0f;


        _calibrated =
            false;


        _referenceAngle =
            0f;


        _heldCorrectionAngle =
            0f;


        debugCalibrated =
            false;


        debugReferenceAngle =
            0f;


        debugCurrentAngle =
            0f;


        debugCorrectionAngle =
            0f;


        ResetShaderRotation();
    }


    // =========================================================
    // Full Reset
    // =========================================================

    [ContextMenu("Reset Angle Lock")]
    public void ResetAngleLock()
    {
        _lastTimestamp =
            -1;


        _orientationResolved =
            false;


        _mirrorX =
            false;


        _flipY =
            false;


        _selectedEye =
            -1;


        _eyeAssignmentLocked =
            false;


        _calibrationCount =
            0;


        _lastCalibrationTimestamp =
            -1;


        _sumDoubleAngleSin =
            0f;


        _sumDoubleAngleCos =
            0f;


        _calibrated =
            false;


        _referenceAngle =
            0f;


        _heldCorrectionAngle =
            0f;


        debugCalibrated =
            false;


        debugTrackedPart =
            "-";


        debugReferenceAngle =
            0f;


        debugCurrentAngle =
            0f;


        debugCorrectionAngle =
            0f;


        ResetShaderRotation();
    }


    // =========================================================
    // Reset Shader
    // =========================================================

    private void ResetShaderRotation()
    {
        if (
            _image == null
        )
        {
            _image =
                GetComponent<
                    SurfaceFittedRawImage
                >();
        }


        if (
            _image == null ||
            _image.material == null
        )
        {
            return;
        }


        Material material =
            _image.material;


        if (
            material.HasProperty(
                SampleRotationRadId
            )
        )
        {
            material.SetFloat(
                SampleRotationRadId,
                0f
            );
        }


        if (
            material.HasProperty(
                SamplePivotId
            )
        )
        {
            material.SetVector(
                SamplePivotId,
                new Vector4(
                    0.5f,
                    0.5f,
                    0f,
                    0f
                )
            );
        }


        if (
            material.HasProperty(
                SourceAspectId
            )
        )
        {
            material.SetFloat(
                SourceAspectId,
                1f
            );
        }
    }


    // =========================================================
    // Disable
    // =========================================================

    private void OnDisable()
    {
        ResetShaderRotation();
    }
}