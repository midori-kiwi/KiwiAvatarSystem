using UnityEngine;

public class WebCamDisplay : MonoBehaviour
{
    private WebCamTexture webcam;

    void Start()
    {
        webcam = new WebCamTexture();

        Renderer renderer = GetComponent<Renderer>();
        renderer.material.mainTexture = webcam;

        webcam.Play();
    }

    void OnDestroy()
    {
        if (webcam != null && webcam.isPlaying)
        {
            webcam.Stop();
        }
    }
}