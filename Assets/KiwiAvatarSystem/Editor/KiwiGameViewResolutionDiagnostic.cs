#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// KiwiAvatarSystem Phase16.20.22 v34 Game View Resolution Isolation.
///
/// Editor-only diagnostic helper. It never enters the Player/runtime assembly.
///
/// Enabled only by:
///   KIWI_GAMEVIEW_RESOLUTION_CASE=720P|1080P
///
/// Uses Unity 6 public PlayModeWindow APIs:
/// - SetViewType(GameView)
/// - SetPlayModeFocused(true)
/// - SetCustomRenderingResolution(width,height,baseName)
/// - GetRenderingResolution(out width,out height)
///
/// It performs only one-shot Editor configuration/verification around entering
/// Play Mode. No per-frame callback remains active during the measurement.
/// </summary>
[InitializeOnLoad]
internal static class KiwiGameViewResolutionDiagnostic
{
    internal const string ContractMarker =
        "KIWI_V5_1_PHASE16_20_22_V34_GAMEVIEW_RESOLUTION_ISOLATION";

    private const string CaseEnvironment =
        "KIWI_GAMEVIEW_RESOLUTION_CASE";

    private static bool _enabled;
    private static string _caseName;
    private static uint _targetWidth;
    private static uint _targetHeight;
    private static string _baseName;

    static KiwiGameViewResolutionDiagnostic()
    {
        _enabled =
            TryResolveCase(
                out _caseName,
                out _targetWidth,
                out _targetHeight,
                out _baseName);

        if (!_enabled)
        {
            return;
        }

        EditorApplication.playModeStateChanged -=
            OnPlayModeStateChanged;

        EditorApplication.playModeStateChanged +=
            OnPlayModeStateChanged;

        EditorApplication.delayCall +=
            ApplyAndVerify;
    }

    private static bool TryResolveCase(
        out string caseName,
        out uint width,
        out uint height,
        out string baseName)
    {
        caseName = null;
        width = 0;
        height = 0;
        baseName = null;

        string raw =
            Environment.GetEnvironmentVariable(
                CaseEnvironment);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string normalized =
            raw.Trim().ToUpperInvariant();

        if (normalized == "720P")
        {
            caseName = "720P";
            width = 1280;
            height = 720;
            baseName = "Kiwi v34 1280x720";
            return true;
        }

        if (normalized == "1080P")
        {
            caseName = "1080P";
            width = 1920;
            height = 1080;
            baseName = "Kiwi v34 1920x1080";
            return true;
        }

        Debug.LogError(
            "[KiwiGameViewResolution] Unsupported " +
            CaseEnvironment +
            "=" +
            raw);

        return false;
    }

    private static void OnPlayModeStateChanged(
        PlayModeStateChange state)
    {
        if (!_enabled)
        {
            return;
        }

        if (
            state ==
            PlayModeStateChange.ExitingEditMode)
        {
            ApplyAndVerify();
            return;
        }

        if (
            state ==
            PlayModeStateChange.EnteredPlayMode)
        {
            VerifyAndWrite(
                "EnteredPlayMode");

            EditorApplication.playModeStateChanged -=
                OnPlayModeStateChanged;
        }
    }

    private static void ApplyAndVerify()
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            PlayModeWindow.SetViewType(
                PlayModeWindow.PlayModeViewTypes.GameView);

            // Keep normal/focused Game View rather than maximized.
            PlayModeWindow.SetPlayModeFocused(
                true);

            PlayModeWindow.SetCustomRenderingResolution(
                _targetWidth,
                _targetHeight,
                _baseName);

            VerifyAndWrite(
                "Configured");
        }
        catch (Exception exception)
        {
            WriteMeta(
                "ERROR",
                0,
                0,
                false,
                exception.ToString());

            Debug.LogError(
                "[KiwiGameViewResolution] configuration failed: " +
                exception);
        }
    }

    private static void VerifyAndWrite(
        string phase)
    {
        try
        {
            PlayModeWindow.GetRenderingResolution(
                out uint actualWidth,
                out uint actualHeight);

            bool verified =
                actualWidth == _targetWidth &&
                actualHeight == _targetHeight;

            WriteMeta(
                phase,
                actualWidth,
                actualHeight,
                verified,
                null);

            string message =
                "[KiwiGameViewResolution] case=" +
                _caseName +
                " target=" +
                _targetWidth +
                "x" +
                _targetHeight +
                " actual=" +
                actualWidth +
                "x" +
                actualHeight +
                " verified=" +
                (verified ? "1" : "0");

            if (verified)
            {
                Debug.Log(message);
            }
            else
            {
                Debug.LogError(message);
            }
        }
        catch (Exception exception)
        {
            WriteMeta(
                phase,
                0,
                0,
                false,
                exception.ToString());

            Debug.LogError(
                "[KiwiGameViewResolution] verification failed: " +
                exception);
        }
    }

    private static void WriteMeta(
        string phase,
        uint actualWidth,
        uint actualHeight,
        bool verified,
        string error)
    {
        string projectRoot =
            Directory.GetParent(
                Application.dataPath)?.FullName
            ?? Application.dataPath;

        string path =
            Path.Combine(
                projectRoot,
                "KiwiGameViewResolution_v34_" +
                _caseName +
                ".meta.txt");

        string text =
            "KiwiAvatarSystem v34 Game View Resolution Isolation" +
            Environment.NewLine +
            "contract=" +
            ContractMarker +
            Environment.NewLine +
            "generated=" +
            DateTime.Now.ToString(
                "o",
                CultureInfo.InvariantCulture) +
            Environment.NewLine +
            "phase=" +
            phase +
            Environment.NewLine +
            "case=" +
            _caseName +
            Environment.NewLine +
            "targetWidth=" +
            _targetWidth +
            Environment.NewLine +
            "targetHeight=" +
            _targetHeight +
            Environment.NewLine +
            "actualWidth=" +
            actualWidth +
            Environment.NewLine +
            "actualHeight=" +
            actualHeight +
            Environment.NewLine +
            "verified=" +
            (verified ? "1" : "0") +
            Environment.NewLine +
            "playModeFocused=1" +
            Environment.NewLine +
            "error=" +
            (error ?? string.Empty) +
            Environment.NewLine;

        File.WriteAllText(
            path,
            text);

        AssetDatabase.Refresh(
            ImportAssetOptions.Default);
    }
}
#endif
