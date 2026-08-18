using UnityEngine;

[DefaultExecutionOrder(-10000)]
public sealed class KiwiStandaloneRuntime : MonoBehaviour
{
    [Header("Standalone Runtime")]

    [SerializeField]
    private bool runInBackground = true;

    [SerializeField]
    private bool disableVSync = true;

    [SerializeField]
    [Range(60, 240)]
    private int targetFrameRate = 120;


    private void Awake()
    {
        ApplySettings();
    }


    private void OnEnable()
    {
        ApplySettings();
    }


    private void ApplySettings()
    {
        Application.runInBackground =
            runInBackground;

        if (disableVSync)
        {
            QualitySettings.vSyncCount = 0;
        }

        Application.targetFrameRate =
            Mathf.Clamp(
                targetFrameRate,
                60,
                240
            );
    }
}