#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// v4.3 retired migration shim.
/// KiwiInferenceFaceTracker.cs is now supplied directly with the calibrated
/// presence-logit normalization, so this file intentionally performs no source
/// rewriting. Keeping the same path/class overwrites older auto-rewriting
/// versions when a cumulative overlay is applied.
/// </summary>
public static class KiwiInferencePresenceSigmoidInstaller
{
    [MenuItem("Tools/Kiwi Avatar/Validate Presence Normalization")]
    public static void Validate()
    {
        Debug.Log(
            "[Kiwi v4.3] Presence normalization is integrated directly into KiwiInferenceFaceTracker.cs; no source rewrite is required.");
    }
}
#endif
