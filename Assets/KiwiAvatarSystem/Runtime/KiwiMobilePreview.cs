using UnityEngine;

[DefaultExecutionOrder(19000)]
[DisallowMultipleComponent]
public sealed class KiwiMobilePreview : MonoBehaviour
{
    public Camera vtuberCamera;
    public Color background = Color.black;
    public bool visible = true;

    private Texture _texture;

    private void LateUpdate()
    {
        if (vtuberCamera == null)
        {
            GameObject cameraObject = GameObject.Find("VTuberCamera");
            if (cameraObject != null)
            {
                vtuberCamera = cameraObject.GetComponent<Camera>();
            }
        }

        _texture = vtuberCamera != null ? vtuberCamera.targetTexture : null;
    }

    private void OnGUI()
    {
#if UNITY_ANDROID || UNITY_IOS
        if (Application.isEditor || !visible || _texture == null || Event.current.type != EventType.Repaint)
        {
            return;
        }

        int previousDepth = GUI.depth;
        Color previousColor = GUI.color;
        GUI.depth = 1000;

        GUI.color = background;
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, false);

        GUI.color = Color.white;
        GUI.DrawTexture(
            new Rect(0f, 0f, Screen.width, Screen.height),
            _texture,
            ScaleMode.ScaleToFit,
            true
        );

        GUI.color = previousColor;
        GUI.depth = previousDepth;
#endif
    }
}
