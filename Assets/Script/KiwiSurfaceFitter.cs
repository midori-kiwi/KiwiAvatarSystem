using System.Collections;
using UnityEngine;


public class KiwiSurfaceFitter : MonoBehaviour
{
    // =========================================================
    // Target Kiwi
    // =========================================================

    [Header("Kiwi Model")]

    [Tooltip(
        "キウイ本体のSkinnedMeshRenderer。\n" +
        "空欄なら自動検索。"
    )]
    public SkinnedMeshRenderer targetRenderer;


    [Tooltip(
        "自動検索するVRM Root。\n" +
        "空欄なら transform.root。"
    )]
    public Transform modelRoot;


    // =========================================================
    // Face Parts
    // =========================================================

    [Header("Face Parts")]

    [Tooltip(
        "目・口を検索するRoot。\n" +
        "空欄ならこのGameObject以下。"
    )]
    public Transform partsRoot;


    public bool includeInactiveParts = true;


    // =========================================================
    // Automatic Fit
    // =========================================================

    [Header("Automatic Fit")]

    public bool fitOnStart = true;


    [Tooltip(
        "UnityのTransform / Canvasが確定してから" +
        "1フレーム待ってSurface Fitする。"
    )]
    public bool waitOneFrame = true;


    // =========================================================
    // Raycast
    // =========================================================

    [Header("Raycast")]

    [Tooltip(
        "モデル半径に対するRay開始距離。" +
        "通常は変更不要。"
    )]
    [Range(1.2f, 6f)]
    public float rayStartMultiplier = 2.5f;


    [Tooltip(
        "Ray全長の倍率。" +
        "通常は変更不要。"
    )]
    [Range(2f, 12f)]
    public float rayLengthMultiplier = 6f;


    // =========================================================
    // Debug
    // =========================================================

    [Header("Debug")]

    public bool logResult = true;


    // =========================================================
    // Internal
    // =========================================================

    private Mesh _bakedMesh;

    private GameObject _colliderObject;

    private MeshCollider _meshCollider;


    private bool _isFitting = false;


    // =========================================================
    // Start
    // =========================================================

    private IEnumerator Start()
    {
        if (!fitOnStart)
        {
            yield break;
        }


        if (waitOneFrame)
        {
            yield return null;
        }


        Canvas.ForceUpdateCanvases();


        FitAllNow();
    }


    // =========================================================
    // Public Fit
    // =========================================================

    [ContextMenu("Fit All Face Parts Now")]
    public void FitAllNow()
    {
        if (_isFitting)
        {
            return;
        }


        _isFitting =
            true;


        bool oldQueriesHitBackfaces =
            Physics.queriesHitBackfaces;


        try
        {
            Canvas.ForceUpdateCanvases();


            SurfaceFittedRawImage[] parts =
                FindFaceParts();


            if (
                parts == null ||
                parts.Length == 0
            )
            {
                Debug.LogWarning(
                    "[KiwiSurfaceFitter] " +
                    "SurfaceFittedRawImage が見つかりません。"
                );

                return;
            }


            ResolveTargetRenderer(
                parts
            );


            if (targetRenderer == null)
            {
                Debug.LogError(
                    "[KiwiSurfaceFitter] " +
                    "キウイ本体のSkinnedMeshRendererが" +
                    "見つかりません。"
                );

                return;
            }


            // =============================================
            // 前回の一時データを念のため削除
            // =============================================

            CleanupTemporaryObjects();


            // =============================================
            // ★ BakeMeshはここで1回だけ
            // =============================================

            _bakedMesh =
                new Mesh();


            _bakedMesh.name =
                "__KiwiSurfaceBakedMesh";


            targetRenderer.BakeMesh(
                _bakedMesh
            );


            // =============================================
            // ★ MeshColliderも1個だけ
            // =============================================

            _colliderObject =
                new GameObject(
                    "__KiwiSurfaceFitCollider"
                );


            _colliderObject.hideFlags =
                HideFlags.HideAndDontSave;


            _colliderObject.transform.SetParent(
                targetRenderer.transform,
                false
            );


            _colliderObject.transform.localPosition =
                Vector3.zero;


            _colliderObject.transform.localRotation =
                Quaternion.identity;


            _colliderObject.transform.localScale =
                Vector3.one;


            _meshCollider =
                _colliderObject.AddComponent
                <
                    MeshCollider
                >();


            _meshCollider.convex =
                false;


            _meshCollider.sharedMesh =
                _bakedMesh;


            Physics.SyncTransforms();


            // =============================================
            // 表裏どちらからでもRayが通るように
            // Fit中だけ有効化
            // =============================================

            Physics.queriesHitBackfaces =
                true;


            // =============================================
            // Ray距離
            // =============================================

            float modelRadius =
                targetRenderer.bounds
                    .extents
                    .magnitude;


            modelRadius =
                Mathf.Max(
                    0.1f,
                    modelRadius
                );


            float rayStartDistance =
                modelRadius *
                rayStartMultiplier;


            float rayLength =
                modelRadius *
                rayLengthMultiplier;


            // =============================================
            // 全Face Partを同じColliderで処理
            // =============================================

            int totalVertices =
                0;


            int totalHits =
                0;


            foreach (
                SurfaceFittedRawImage part
                in parts
            )
            {
                if (part == null)
                {
                    continue;
                }


                int partHits =
                    FitPart(
                        part,
                        rayStartDistance,
                        rayLength
                    );


                totalVertices +=
                    part.SurfaceVertexCount;


                totalHits +=
                    partHits;
            }


            // =============================================
            // Result
            // =============================================

            if (logResult)
            {
                float successRate =
                    totalVertices > 0
                        ?
                        (
                            totalHits /
                            (float)totalVertices
                        )
                        *
                        100f
                        :
                        0f;


                Debug.Log(
                    "[KiwiSurfaceFitter] " +
                    "Surface Fit 完了  " +
                    totalHits +
                    " / " +
                    totalVertices +
                    " vertices  (" +
                    successRate.ToString("F1") +
                    "%)"
                );
            }
        }
        finally
        {
            Physics.queriesHitBackfaces =
                oldQueriesHitBackfaces;


            // =============================================
            // ★ Fit後は全部破棄
            //
            // 実行中Collider = 0
            // BakeMesh処理    = 0/frame
            // Raycast         = 0/frame
            // =============================================

            CleanupTemporaryObjects();


            _isFitting =
                false;
        }
    }


    // =========================================================
    // Find Face Parts
    // =========================================================

    private SurfaceFittedRawImage[]
        FindFaceParts()
    {
        Transform root =
            partsRoot != null
                ?
                partsRoot
                :
                transform;


        return root.GetComponentsInChildren
        <
            SurfaceFittedRawImage
        >(
            includeInactiveParts
        );
    }


    // =========================================================
    // Resolve Renderer
    // =========================================================

    private void ResolveTargetRenderer(
        SurfaceFittedRawImage[] parts)
    {
        if (targetRenderer != null)
        {
            return;
        }


        // =============================================
        // 旧版SurfaceFittedRawImageに
        // Rendererが設定されていれば流用
        // =============================================

        if (parts != null)
        {
            foreach (
                SurfaceFittedRawImage part
                in parts
            )
            {
                if (
                    part != null &&
                    part.targetRenderer != null
                )
                {
                    targetRenderer =
                        part.targetRenderer;


                    return;
                }
            }
        }


        // =============================================
        // 自動検索
        // =============================================

        Transform root =
            modelRoot != null
                ?
                modelRoot
                :
                transform.root;


        if (root == null)
        {
            return;
        }


        SkinnedMeshRenderer[] renderers =
            root.GetComponentsInChildren
            <
                SkinnedMeshRenderer
            >(
                true
            );


        if (
            renderers == null ||
            renderers.Length == 0
        )
        {
            return;
        }


        // =============================================
        // 一番頂点数の多いRendererを
        // 本体として選択
        // =============================================

        int bestVertexCount =
            -1;


        float bestBoundsSize =
            -1f;


        foreach (
            SkinnedMeshRenderer renderer
            in renderers
        )
        {
            if (
                renderer == null ||
                renderer.sharedMesh == null
            )
            {
                continue;
            }


            int vertexCount =
                renderer.sharedMesh
                    .vertexCount;


            float boundsSize =
                renderer.bounds
                    .size
                    .sqrMagnitude;


            if (
                vertexCount >
                bestVertexCount
                ||
                (
                    vertexCount ==
                    bestVertexCount
                    &&
                    boundsSize >
                    bestBoundsSize
                )
            )
            {
                bestVertexCount =
                    vertexCount;


                bestBoundsSize =
                    boundsSize;


                targetRenderer =
                    renderer;
            }
        }


        if (
            logResult &&
            targetRenderer != null
        )
        {
            Debug.Log(
                "[KiwiSurfaceFitter] " +
                "Auto Renderer = " +
                targetRenderer.name +
                " / Vertices = " +
                targetRenderer.sharedMesh.vertexCount
            );
        }
    }


    // =========================================================
    // Fit One Part
    // =========================================================

    private int FitPart(
        SurfaceFittedRawImage part,
        float rayStartDistance,
        float rayLength)
    {
        int xCount =
            part.XSegments;


        int yCount =
            part.YSegments;


        int vertexCount =
            part.SurfaceVertexCount;


        Vector3[] fittedPositions =
            new Vector3[
                vertexCount
            ];


        // =============================================
        // このパーツの正面方向
        // =============================================

        Vector3 planeNormal =
            part.transform.forward;


        if (
            planeNormal.sqrMagnitude <
            0.000001f
        )
        {
            planeNormal =
                Vector3.forward;
        }


        planeNormal.Normalize();


        // =============================================
        // 中央1点だけ前後から調べて
        // キウイがどちら側にあるか決める
        // =============================================

        Vector3 centerWorld =
            part.transform.TransformPoint(
                part.GetFlatLocalCenter()
            );


        bool hasDirection =
            DetermineProjectionSide(
                centerWorld,
                planeNormal,
                rayStartDistance,
                rayLength,
                out float sideSign
            );


        if (!hasDirection)
        {
            // 中央にヒットしなかった場合、
            // 現在のCanvas正面側を仮採用
            sideSign =
                1f;
        }


        // origin方向
        Vector3 outwardDirection =
            planeNormal *
            sideSign;


        // キウイへ向かうRay
        Vector3 castDirection =
            -outwardDirection;


        int hits =
            0;


        int index =
            0;


        // =============================================
        // 全頂点
        // =============================================

        for (
            int y = 0;
            y <= yCount;
            y++
        )
        {
            for (
                int x = 0;
                x <= xCount;
                x++
            )
            {
                Vector3 flatLocal =
                    part.GetFlatLocalPosition(
                        x,
                        y
                    );


                Vector3 baseWorld =
                    part.transform.TransformPoint(
                        flatLocal
                    );


                bool found =
                    CastFromSurfaceSide(
                        baseWorld,
                        outwardDirection,
                        castDirection,
                        rayStartDistance,
                        rayLength,
                        out RaycastHit hit
                    );


                // =========================================
                // 万一ヒットしなければ
                // 反対方向から1回だけFallback
                // =========================================

                if (!found)
                {
                    found =
                        CastFromSurfaceSide(
                            baseWorld,
                            -outwardDirection,
                            outwardDirection,
                            rayStartDistance,
                            rayLength,
                            out hit
                        );
                }


                if (found)
                {
                    // =====================================
                    // NormalがRay側を向くよう補正
                    // =====================================

                    Vector3 surfaceNormal =
                        hit.normal;


                    Vector3 towardRayOrigin =
                        (
                            hit.point -
                            (
                                baseWorld +
                                outwardDirection *
                                rayStartDistance
                            )
                        );


                    // 実際には -castDirection と
                    // 同じ向きが外側
                    Vector3 expectedOutward =
                        -castDirection;


                    if (
                        Vector3.Dot(
                            surfaceNormal,
                            expectedOutward
                        )
                        <
                        0f
                    )
                    {
                        surfaceNormal =
                            -surfaceNormal;
                    }


                    Vector3 worldPosition =
                        hit.point
                        +
                        surfaceNormal
                        *
                        part.surfaceOffset;


                    fittedPositions[
                        index
                    ] =
                        part.transform
                            .InverseTransformPoint(
                                worldPosition
                            );


                    hits++;
                }
                else
                {
                    // =====================================
                    // どうしても当たらない頂点だけ
                    // 元の平面を維持
                    // =====================================

                    fittedPositions[
                        index
                    ] =
                        flatLocal;
                }


                index++;
            }
        }


        part.ApplySurfaceFit(
            fittedPositions
        );


        if (logResult)
        {
            float percentage =
                vertexCount > 0
                    ?
                    hits /
                    (float)vertexCount
                    *
                    100f
                    :
                    0f;


            Debug.Log(
                "[KiwiSurfaceFitter] "
                +
                part.name
                +
                "  "
                +
                hits
                +
                " / "
                +
                vertexCount
                +
                "  ("
                +
                percentage.ToString("F1")
                +
                "%)"
            );
        }


        return hits;
    }


    // =========================================================
    // Determine Side
    //
    // 中央だけ前後2方向。
    // 近いSurface側を採用。
    // =========================================================

    private bool DetermineProjectionSide(
        Vector3 baseWorldPosition,
        Vector3 normal,
        float startDistance,
        float rayLength,
        out float sideSign)
    {
        sideSign =
            1f;


        // =============================================
        // +Normal側から
        // =============================================

        Vector3 originA =
            baseWorldPosition
            +
            normal *
            startDistance;


        Ray rayA =
            new Ray(
                originA,
                -normal
            );


        bool hitA =
            _meshCollider.Raycast(
                rayA,
                out RaycastHit resultA,
                rayLength
            );


        // =============================================
        // -Normal側から
        // =============================================

        Vector3 originB =
            baseWorldPosition
            -
            normal *
            startDistance;


        Ray rayB =
            new Ray(
                originB,
                normal
            );


        bool hitB =
            _meshCollider.Raycast(
                rayB,
                out RaycastHit resultB,
                rayLength
            );


        // =============================================
        // 両方
        // =============================================

        if (
            hitA &&
            hitB
        )
        {
            float distanceA =
                Vector3.Distance(
                    baseWorldPosition,
                    resultA.point
                );


            float distanceB =
                Vector3.Distance(
                    baseWorldPosition,
                    resultB.point
                );


            sideSign =
                distanceA <= distanceB
                    ?
                    1f
                    :
                    -1f;


            return true;
        }


        // =============================================
        // +Normalのみ
        // =============================================

        if (hitA)
        {
            sideSign =
                1f;


            return true;
        }


        // =============================================
        // -Normalのみ
        // =============================================

        if (hitB)
        {
            sideSign =
                -1f;


            return true;
        }


        return false;
    }


    // =========================================================
    // Cast Vertex
    // =========================================================

    private bool CastFromSurfaceSide(
        Vector3 baseWorldPosition,
        Vector3 outwardDirection,
        Vector3 castDirection,
        float startDistance,
        float rayLength,
        out RaycastHit hit)
    {
        Vector3 origin =
            baseWorldPosition
            +
            outwardDirection
            *
            startDistance;


        Ray ray =
            new Ray(
                origin,
                castDirection.normalized
            );


        return _meshCollider.Raycast(
            ray,
            out hit,
            rayLength
        );
    }


    // =========================================================
    // Cleanup
    // =========================================================

    private void CleanupTemporaryObjects()
    {
        if (_meshCollider != null)
        {
            _meshCollider.enabled =
                false;


            _meshCollider.sharedMesh =
                null;


            _meshCollider =
                null;
        }


        if (_colliderObject != null)
        {
            if (Application.isPlaying)
            {
                Destroy(
                    _colliderObject
                );
            }
            else
            {
                DestroyImmediate(
                    _colliderObject
                );
            }


            _colliderObject =
                null;
        }


        if (_bakedMesh != null)
        {
            if (Application.isPlaying)
            {
                Destroy(
                    _bakedMesh
                );
            }
            else
            {
                DestroyImmediate(
                    _bakedMesh
                );
            }


            _bakedMesh =
                null;
        }
    }


    // =========================================================
    // Destroy
    // =========================================================

    private void OnDestroy()
    {
        CleanupTemporaryObjects();
    }
}