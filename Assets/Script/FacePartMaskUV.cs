using UnityEngine;
using UnityEngine.UI;


[RequireComponent(typeof(Graphic))]
public class FacePartMaskUV : BaseMeshEffect
{
    public override void ModifyMesh(
        VertexHelper vh)
    {
        if (!IsActive())
            return;


        RectTransform rectTransform =
            transform as RectTransform;


        if (rectTransform == null)
            return;


        Rect rect =
            rectTransform.rect;


        if (
            Mathf.Abs(rect.width) < 0.0001f ||
            Mathf.Abs(rect.height) < 0.0001f
        )
        {
            return;
        }


        UIVertex vertex =
            new UIVertex();


        int count =
            vh.currentVertCount;


        for (int i = 0; i < count; i++)
        {
            vh.PopulateUIVertex(
                ref vertex,
                i
            );


            float u =
                Mathf.InverseLerp(
                    rect.xMin,
                    rect.xMax,
                    vertex.position.x
                );


            float v =
                Mathf.InverseLerp(
                    rect.yMin,
                    rect.yMax,
                    vertex.position.y
                );


            vertex.uv1 =
                new Vector2(
                    u,
                    v
                );


            vh.SetUIVertex(
                vertex,
                i
            );
        }
    }
}