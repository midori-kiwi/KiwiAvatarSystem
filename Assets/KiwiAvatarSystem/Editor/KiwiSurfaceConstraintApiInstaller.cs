#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// v4.3 retired migration shim.
/// SurfaceFittedRawImage.cs is supplied directly with the fitted-surface local
/// constraint API. This file exists only to overwrite the older v4.0 automatic
/// source-rewriter if it is still present in an upgraded project.
/// </summary>
public static class KiwiSurfaceConstraintApiInstaller
{
    [MenuItem("Tools/Kiwi Avatar/Validate Surface Constraint API")]
    public static void Validate()
    {
        Debug.Log(
            "[Kiwi v4.3] Surface constraint API is integrated directly into SurfaceFittedRawImage.cs; no source rewrite is required.");
    }
}
#endif
