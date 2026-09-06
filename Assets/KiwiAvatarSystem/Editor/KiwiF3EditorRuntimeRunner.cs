#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Diagnostic-only batch Play runner for the F3 change-local gate. It does not
/// invoke the full Product validator and exits after a bounded observation.
/// </summary>
public static class KiwiF3EditorRuntimeRunner
{
    private static double _stopAt;
    private static bool _stopRequested;

    public static void Run()
    {
        string scene = "Assets/Scenes/Face Landmark Detection.unity";
        if (!System.IO.File.Exists(scene))
            throw new InvalidOperationException("F3 scene missing: " + scene);

        EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
        EditorApplication.ExecuteMenuItem(
            "Tools/Kiwi Avatar System/Release Candidate Preflight/Bypass Play Gate Once");

        double seconds = 20.0;
        string raw = Environment.GetEnvironmentVariable("KIWI_F3_RUN_SECONDS");
        if (double.TryParse(raw, out double parsed) && parsed >= 5.0 && parsed <= 120.0)
            seconds = parsed;

        _stopAt = EditorApplication.timeSinceStartup + seconds;
        _stopRequested = false;
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        EditorApplication.isPlaying = true;
        Debug.Log("[F3_SINGLE_SAMPLING_ROTATION_OWNER] EDITOR_RUN_BEGIN seconds=" + seconds);
    }

    private static void Tick()
    {
        if (!_stopRequested && EditorApplication.timeSinceStartup >= _stopAt)
        {
            _stopRequested = true;
            EditorApplication.isPlaying = false;
            Debug.Log("[F3_SINGLE_SAMPLING_ROTATION_OWNER] EDITOR_RUN_STOP_REQUESTED");
        }

        if (_stopRequested && !EditorApplication.isPlaying)
        {
            EditorApplication.update -= Tick;
            Debug.Log("[F3_SINGLE_SAMPLING_ROTATION_OWNER] EDITOR_RUN_COMPLETE");
            EditorApplication.Exit(0);
        }
    }
}
#endif
