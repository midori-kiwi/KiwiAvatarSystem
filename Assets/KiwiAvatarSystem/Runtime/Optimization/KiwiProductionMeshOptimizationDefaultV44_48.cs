using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// KiwiAvatarSystem v44.48
/// Windows/DX12 Production default policy for the already validated/frozen
/// v44.11 index-only O25 render mesh optimization.
///
/// This class does NOT implement mesh simplification.
/// It only supplies process-local defaults before scene Awake when the user
/// has not explicitly provided a corresponding environment variable.
///
/// Explicit environment variables always win, preserving A/B and rollback:
///   KIWI_AVATAR_MESHOPT_ENABLE
///   KIWI_AVATAR_MESHOPT_RATIO
///   KIWI_AVATAR_MESHOPT_ERROR
/// </summary>
internal static class KiwiProductionMeshOptimizationDefaultV44_48
{
    private const string Contract =
        "KIWI_V44_48_WINDOWS_DX12_PRODUCTION_O25_DEFAULT";

    private const string EnableVariable = "KIWI_AVATAR_MESHOPT_ENABLE";
    private const string RatioVariable = "KIWI_AVATAR_MESHOPT_RATIO";
    private const string ErrorVariable = "KIWI_AVATAR_MESHOPT_ERROR";

    private const string DefaultEnable = "1";
    private const string DefaultRatio = "0.25";
    private const string DefaultError = "0.01";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ApplyProductionDefaults()
    {
        // v44.48 is deliberately scoped to the configuration that has now
        // been measured and validated: Windows Player + Direct3D12.
        if (Application.isEditor)
        {
            return;
        }

        if (Application.platform != RuntimePlatform.WindowsPlayer)
        {
            return;
        }

        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
        {
            return;
        }

        bool enableDefaulted = SetProcessDefaultIfUnset(
            EnableVariable, DefaultEnable);
        bool ratioDefaulted = SetProcessDefaultIfUnset(
            RatioVariable, DefaultRatio);
        bool errorDefaulted = SetProcessDefaultIfUnset(
            ErrorVariable, DefaultError);

        string enable = Environment.GetEnvironmentVariable(EnableVariable) ?? "";
        string ratio = Environment.GetEnvironmentVariable(RatioVariable) ?? "";
        string error = Environment.GetEnvironmentVariable(ErrorVariable) ?? "";

        Debug.Log(
            "[KiwiMeshOptV44_48] " +
            "contract=" + Contract +
            " platform=WindowsPlayer" +
            " graphicsApi=Direct3D12" +
            " enable=" + enable +
            " ratio=" + ratio +
            " targetError=" + error +
            " enableSource=" + (enableDefaulted ? "PRODUCTION_DEFAULT" : "EXPLICIT_ENV") +
            " ratioSource=" + (ratioDefaulted ? "PRODUCTION_DEFAULT" : "EXPLICIT_ENV") +
            " errorSource=" + (errorDefaulted ? "PRODUCTION_DEFAULT" : "EXPLICIT_ENV") +
            " optimizerImplementation=V44_11_FROZEN_UNCHANGED" +
            " trackingChange=0" +
            " inferenceChange=0" +
            " nativeCameraChange=0" +
            " spoutChange=0");
    }

    private static bool SetProcessDefaultIfUnset(string variable, string value)
    {
        string existing = Environment.GetEnvironmentVariable(variable);

        if (!string.IsNullOrWhiteSpace(existing))
        {
            return false;
        }

        Environment.SetEnvironmentVariable(
            variable,
            value,
            EnvironmentVariableTarget.Process);

        return true;
    }
}
