#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

[InitializeOnLoad]
public sealed class KiwiMobileProjectConfigurator : IPreprocessBuildWithReport
{
    private const string FaceModelPath =
        "Assets/StreamingAssets/face_landmarker_v2_with_blendshapes.bytes";

    private const string IOSCameraDescription =
        "Camera access is used for real-time face tracking of the VTuber avatar.";

    public int callbackOrder => -1000;

    static KiwiMobileProjectConfigurator()
    {
        EditorApplication.delayCall += ApplySafeProjectDefaults;
    }

    [MenuItem("Kiwi VTuber/Avatar System/Mobile/Apply Safe Defaults")]
    public static void ApplySafeProjectDefaultsMenu()
    {
        ApplySafeProjectDefaults();
        EditorUtility.DisplayDialog(
            "Kiwi Avatar System - Mobile",
            "Safe mobile defaults were applied where supported.\n\n" +
            "The existing Windows tracking configuration was not changed.",
            "OK"
        );
    }

    [MenuItem("Kiwi VTuber/Avatar System/Mobile/Validate Mobile Readiness")]
    public static void ValidateMobileReadinessMenu()
    {
        string report = BuildReadinessReport();
        EditorUtility.DisplayDialog(
            "Kiwi Avatar System - Mobile Readiness",
            report,
            "OK"
        );
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report == null)
        {
            return;
        }

        BuildTarget target = report.summary.platform;
        if (target != BuildTarget.Android && target != BuildTarget.iOS)
        {
            return;
        }

        ApplySafeProjectDefaults();

        if (!File.Exists(FaceModelPath))
        {
            throw new BuildFailedException(
                "[KiwiAvatarSystem] Required Face Landmarker model is missing: " +
                FaceModelPath +
                ". The mobile build is stopped because MediaPipe cannot start without it."
            );
        }

        if (target == BuildTarget.Android)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Plugins/Android/KiwiMobileBridge.androidlib"))
            {
                throw new BuildFailedException(
                    "[KiwiAvatarSystem] Android VRM picker bridge is missing. Re-import the complete Kiwi Avatar System package."
                );
            }

            if (!HasAssetNamed("mediapipe_android.aar"))
            {
                throw new BuildFailedException(
                    "[KiwiAvatarSystem] mediapipe_android.aar was not detected. " +
                    "Install the MediaPipeUnityPlugin v0.16.3 mobile native libraries before building Android."
                );
            }

            if (!HasAnyAssetNamed("libc++_shared.so", "libstdc++_shared.so"))
            {
                Debug.LogWarning(
                    "[KiwiAvatarSystem] A shared C++ runtime was not found under Assets/Packages. " +
                    "MediaPipeUnityPlugin v0.16.3 requires the Android shared C++ runtime in the final APK. " +
                    "If your MediaPipe sample Gradle setup already copies it at build time, this warning can be ignored."
                );
            }
        }
        else if (target == BuildTarget.iOS)
        {
            if (!File.Exists("Assets/Plugins/iOS/KiwiAvatarSystem/KiwiFilePicker.mm"))
            {
                throw new BuildFailedException(
                    "[KiwiAvatarSystem] iOS VRM picker bridge is missing. Re-import the complete Kiwi Avatar System package."
                );
            }

            if (!HasMediaPipeIOSNativeLibrary())
            {
                throw new BuildFailedException(
                    "[KiwiAvatarSystem] An iOS MediaPipe native library/framework was not detected. " +
                    "Install the MediaPipeUnityPlugin v0.16.3 mobile native libraries before building iOS."
                );
            }
        }
    }

    private static void ApplySafeProjectDefaults()
    {
        TrySetIOSCameraUsageDescription();
        TryEnableAndroidOptimizedFramePacing();
    }

    private static void TrySetIOSCameraUsageDescription()
    {
        try
        {
            Type nested = typeof(PlayerSettings).GetNestedType(
                "iOS",
                BindingFlags.Public | BindingFlags.NonPublic
            );

            if (nested == null)
            {
                return;
            }

            PropertyInfo property = nested.GetProperty(
                "cameraUsageDescription",
                BindingFlags.Public | BindingFlags.Static
            );

            if (property == null || !property.CanRead || !property.CanWrite)
            {
                return;
            }

            string current = property.GetValue(null, null) as string;
            if (string.IsNullOrWhiteSpace(current))
            {
                property.SetValue(null, IOSCameraDescription, null);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Could not set the iOS camera usage description automatically: " +
                ex.Message
            );
        }
    }

    private static void TryEnableAndroidOptimizedFramePacing()
    {
        try
        {
            Type nested = typeof(PlayerSettings).GetNestedType(
                "Android",
                BindingFlags.Public | BindingFlags.NonPublic
            );

            if (nested == null)
            {
                return;
            }

            PropertyInfo property = nested.GetProperty(
                "optimizedFramePacing",
                BindingFlags.Public | BindingFlags.Static
            );

            if (property != null && property.CanWrite)
            {
                property.SetValue(null, true, null);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Could not enable Android optimized frame pacing automatically: " +
                ex.Message
            );
        }
    }

    private static string BuildReadinessReport()
    {
        bool model = File.Exists(FaceModelPath);
        bool androidBridge = AssetDatabase.IsValidFolder(
            "Assets/Plugins/Android/KiwiMobileBridge.androidlib"
        );
        bool iosBridge = File.Exists(
            "Assets/Plugins/iOS/KiwiAvatarSystem/KiwiFilePicker.mm"
        );
        bool androidMediaPipe = HasAssetNamed("mediapipe_android.aar");
        bool androidCpp = HasAnyAssetNamed("libc++_shared.so", "libstdc++_shared.so");
        bool iosMediaPipe = HasMediaPipeIOSNativeLibrary();

        return
            "Common\n" +
            "  Face model in StreamingAssets: " + Mark(model) + "\n" +
            "  Kiwi mobile runtime scripts: OK\n\n" +
            "Android\n" +
            "  Native VRM picker bridge: " + Mark(androidBridge) + "\n" +
            "  mediapipe_android.aar detected: " + Mark(androidMediaPipe) + "\n" +
            "  Shared C++ runtime detected: " + (androidCpp ? "OK" : "CHECK Gradle/Plugins") + "\n\n" +
            "iOS\n" +
            "  Native VRM picker bridge: " + Mark(iosBridge) + "\n" +
            "  MediaPipe iOS native library detected: " + (iosMediaPipe ? "OK" : "CHECK") + "\n\n" +
            "Note: iOS final app compilation/signing requires Xcode on macOS (or a compatible build service).";
    }

    private static string Mark(bool value)
    {
        return value ? "OK" : "MISSING";
    }

    private static bool HasAssetNamed(string fileName)
    {
        return AssetDatabase.GetAllAssetPaths().Any(
            p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static bool HasAnyAssetNamed(params string[] fileNames)
    {
        string[] paths = AssetDatabase.GetAllAssetPaths();
        for (int i = 0; i < paths.Length; i++)
        {
            string name = Path.GetFileName(paths[i]);
            for (int j = 0; j < fileNames.Length; j++)
            {
                if (string.Equals(name, fileNames[j], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasMediaPipeIOSNativeLibrary()
    {
        string[] paths = AssetDatabase.GetAllAssetPaths();
        for (int i = 0; i < paths.Length; i++)
        {
            string p = paths[i];
            string lower = p.ToLowerInvariant();
            if (!lower.Contains("mediapipe"))
            {
                continue;
            }

            if (lower.EndsWith(".framework") ||
                lower.EndsWith(".a") ||
                lower.EndsWith(".dylib") ||
                lower.EndsWith(".xcframework"))
            {
                return true;
            }
        }
        return false;
    }
}
#endif
