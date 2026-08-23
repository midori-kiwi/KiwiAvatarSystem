#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// KiwiAvatarSystem v5.1.0 Phase16.20.3
///
/// Corrects the CM831 / UGREEN preferred WebCam profile authority to
/// 1920x1080 @ 60 fps.
///
/// Why this exists:
/// The current Kiwi AppSettings/WebCamSource implementation has a dedicated
/// preferred-device profile. For CM831/UGREEN cameras that profile bypasses
/// _preferredDefaultWebCamWidth. Phase16.20.2 changed only the generic default
/// width and therefore could still start the preferred camera at 1280x720@60.
///
/// Scope:
/// - Editor-only AppSettings migration.
/// - No FaceLandmarkerRunner changes.
/// - No KiwiFaceMotion changes.
/// - No tracking threshold changes.
/// - No Provider/Root/FaceParts/Presentation changes.
/// - No MediaPipe package source rewrite.
/// </summary>
internal static class KiwiFineCamLite4KProfileMigration
{
    // KIWI_V5_1_PHASE16_20_3_FINECAM_PREFERRED_PROFILE_1080P60
    private const string ContractMarker =
        "KIWI_V5_1_PHASE16_20_3_FINECAM_PREFERRED_PROFILE_1080P60";

    private const string ApplyMenu =
        "Tools/Kiwi Avatar System/Camera/Apply FineCam Lite 4K 1080p60 Profile";
    private const string ValidateMenu =
        "Tools/Kiwi Avatar System/Camera/Validate FineCam Lite 4K Profile";

    private const string PreferredDefaultWidthProperty =
        "_preferredDefaultWebCamWidth";
    private const string PreferredDeviceKeywordsProperty =
        "_preferredWebCamDeviceKeywords";
    private const string PreferredProfileWidthProperty =
        "_preferredWebCamProfileWidth";
    private const string PreferredProfileHeightProperty =
        "_preferredWebCamProfileHeight";
    private const string PreferredProfileFrameRateProperty =
        "_preferredWebCamProfileFrameRate";
    private const string ResolutionArrayProperty =
        "_defaultAvailableWebCamResolutions";

    private const string WidthProperty = "width";
    private const string HeightProperty = "height";
    private const string FrameRateProperty = "frameRate";

    private const int TargetWidth = 1920;
    private const int TargetHeight = 1080;
    private const int TargetFrameRate = 60;
    private const double FrameRateTolerance = 0.05;

    private static readonly string[] RequiredPreferredKeywords =
    {
        "CM831",
        "UGREEN"
    };

    private sealed class Candidate
    {
        public ScriptableObject asset;
        public string path;
    }

    private sealed class Properties
    {
        public SerializedProperty preferredDefaultWidth;
        public SerializedProperty preferredDeviceKeywords;
        public SerializedProperty preferredProfileWidth;
        public SerializedProperty preferredProfileHeight;
        public SerializedProperty preferredProfileFrameRate;
        public SerializedProperty resolutions;
    }

    [MenuItem(ApplyMenu)]
    private static void ApplyProfile()
    {
        if (!CanEditNow())
        {
            return;
        }

        if (!TryFindUniqueAppSettings(out Candidate candidate))
        {
            return;
        }

        if (candidate.path.StartsWith(
                "Packages/",
                StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogError(
                "[KiwiCameraProfile] Refusing to modify a package-owned " +
                "AppSettings asset. path=" + candidate.path);
            return;
        }

        SerializedObject serialized =
            new SerializedObject(candidate.asset);
        serialized.UpdateIfRequiredOrScript();

        if (!TryGetProperties(
                serialized,
                out Properties properties,
                out string propertyError))
        {
            Debug.LogError(
                "[KiwiCameraProfile] " + propertyError +
                " No changes were made. path=" + candidate.path);
            return;
        }

        if (!TryInspectResolutions(
                properties.resolutions,
                out bool has1080p60,
                out string resolutionError))
        {
            Debug.LogError(
                "[KiwiCameraProfile] " + resolutionError +
                " No changes were made. path=" + candidate.path);
            return;
        }

        bool keywordsReady =
            HasRequiredPreferredKeywords(
                properties.preferredDeviceKeywords);

        bool alreadyCorrect =
            properties.preferredDefaultWidth.intValue == TargetWidth &&
            properties.preferredProfileWidth.intValue == TargetWidth &&
            properties.preferredProfileHeight.intValue == TargetHeight &&
            properties.preferredProfileFrameRate.intValue == TargetFrameRate &&
            keywordsReady &&
            has1080p60;

        if (alreadyCorrect)
        {
            Debug.Log(
                "[KiwiCameraProfile] " + ContractMarker +
                " already correct. " +
                BuildProfileSummary(properties, candidate.path) +
                " Actual capture mode still requires Runtime verification.");
            return;
        }

        if (!TryCreateBackup(
                candidate.path,
                candidate.asset,
                out string backupDirectory,
                out string backupError))
        {
            Debug.LogError(
                "[KiwiCameraProfile] Backup failed. No profile changes were " +
                "made. " + backupError);
            return;
        }

        try
        {
            Undo.RecordObject(
                candidate.asset,
                "Apply Kiwi FineCam Lite 4K 1080p60 Preferred Profile");

            // Generic fallback for non-preferred cameras.
            properties.preferredDefaultWidth.intValue = TargetWidth;

            // This is the actual authority for CM831 / UGREEN in the current
            // Kiwi WebCamSource implementation.
            properties.preferredProfileWidth.intValue = TargetWidth;
            properties.preferredProfileHeight.intValue = TargetHeight;
            properties.preferredProfileFrameRate.intValue = TargetFrameRate;

            EnsurePreferredKeywords(
                properties.preferredDeviceKeywords);

            // Preserve all existing modes, including 1920x1080@30.
            // The current preferred-profile comparer explicitly targets 60 fps,
            // so deleting the 30-fps mode is unnecessary and would reduce
            // fallback compatibility.
            if (!has1080p60)
            {
                AppendResolution(
                    properties.resolutions,
                    TargetWidth,
                    TargetHeight,
                    TargetFrameRate);
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(candidate.asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (!TryValidateAsset(
                    candidate.path,
                    out string validationSummary,
                    out string validationError))
            {
                Debug.LogError(
                    "[KiwiCameraProfile] Write completed, but validation FAILED. " +
                    "Restore from backup: " + backupDirectory +
                    " | " + validationError);
                return;
            }

            Debug.Log(
                "[KiwiCameraProfile] " + ContractMarker +
                " PASS. " + validationSummary +
                " Backup=" + backupDirectory +
                " | Expected WebCamSource log on next Play: " +
                "preferredProfile=True requested=1920x1080@60. " +
                "Actual width/height/FPS must still be measured at Runtime.");
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[KiwiCameraProfile] Apply failed after backup creation. " +
                "Restore from: " + backupDirectory + "\n" + ex);
        }
    }

    [MenuItem(ValidateMenu)]
    private static void ValidateProfile()
    {
        if (!TryFindUniqueAppSettings(out Candidate candidate))
        {
            return;
        }

        if (TryValidateAsset(
                candidate.path,
                out string summary,
                out string error))
        {
            Debug.Log(
                "[KiwiCameraProfile] " + ContractMarker +
                " VALIDATION PASS. " + summary +
                " Actual device delivery remains a Runtime measurement.");
        }
        else
        {
            Debug.LogError(
                "[KiwiCameraProfile] " + ContractMarker +
                " VALIDATION FAIL. " + error);
        }
    }

    private static bool CanEditNow()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError(
                "[KiwiCameraProfile] Stop Play Mode before changing the " +
                "camera profile.");
            return false;
        }

        if (BuildPipeline.isBuildingPlayer)
        {
            Debug.LogError(
                "[KiwiCameraProfile] Cannot change the camera profile while " +
                "a Player build is running.");
            return false;
        }

        return true;
    }

    private static bool TryFindUniqueAppSettings(
        out Candidate candidate)
    {
        candidate = null;

        string[] guids =
            AssetDatabase.FindAssets("AppSettings");

        List<Candidate> candidates =
            new List<Candidate>();

        for (int i = 0; i < guids.Length; ++i)
        {
            string path =
                AssetDatabase.GUIDToAssetPath(guids[i]);

            if (string.IsNullOrEmpty(path) ||
                !path.EndsWith(
                    ".asset",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ScriptableObject asset =
                AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);

            if (asset == null)
            {
                continue;
            }

            MonoScript script =
                MonoScript.FromScriptableObject(asset);

            if (script == null ||
                !string.Equals(
                    script.name,
                    "AppSettings",
                    StringComparison.Ordinal))
            {
                continue;
            }

            SerializedObject serialized =
                new SerializedObject(asset);
            serialized.UpdateIfRequiredOrScript();

            if (!TryGetProperties(
                    serialized,
                    out _,
                    out _))
            {
                continue;
            }

            candidates.Add(
                new Candidate
                {
                    asset = asset,
                    path = path
                });
        }

        if (candidates.Count == 0)
        {
            Debug.LogError(
                "[KiwiCameraProfile] Could not find a compatible Kiwi " +
                "MediaPipe AppSettings.asset with preferred-device profile " +
                "fields. No changes were made.");
            return false;
        }

        if (candidates.Count != 1)
        {
            List<string> paths =
                new List<string>();

            for (int i = 0; i < candidates.Count; ++i)
            {
                paths.Add(candidates[i].path);
            }

            Debug.LogError(
                "[KiwiCameraProfile] Multiple compatible AppSettings assets " +
                "were found. Selection is not guessed. No changes were made. " +
                "Candidates: " + string.Join(" | ", paths));
            return false;
        }

        candidate = candidates[0];
        return true;
    }

    private static bool TryGetProperties(
        SerializedObject serialized,
        out Properties properties,
        out string error)
    {
        properties =
            new Properties
            {
                preferredDefaultWidth =
                    serialized.FindProperty(
                        PreferredDefaultWidthProperty),
                preferredDeviceKeywords =
                    serialized.FindProperty(
                        PreferredDeviceKeywordsProperty),
                preferredProfileWidth =
                    serialized.FindProperty(
                        PreferredProfileWidthProperty),
                preferredProfileHeight =
                    serialized.FindProperty(
                        PreferredProfileHeightProperty),
                preferredProfileFrameRate =
                    serialized.FindProperty(
                        PreferredProfileFrameRateProperty),
                resolutions =
                    serialized.FindProperty(
                        ResolutionArrayProperty)
            };

        List<string> missing =
            new List<string>();

        if (properties.preferredDefaultWidth == null)
        {
            missing.Add(PreferredDefaultWidthProperty);
        }

        if (properties.preferredDeviceKeywords == null ||
            !properties.preferredDeviceKeywords.isArray)
        {
            missing.Add(PreferredDeviceKeywordsProperty);
        }

        if (properties.preferredProfileWidth == null)
        {
            missing.Add(PreferredProfileWidthProperty);
        }

        if (properties.preferredProfileHeight == null)
        {
            missing.Add(PreferredProfileHeightProperty);
        }

        if (properties.preferredProfileFrameRate == null)
        {
            missing.Add(PreferredProfileFrameRateProperty);
        }

        if (properties.resolutions == null ||
            !properties.resolutions.isArray)
        {
            missing.Add(ResolutionArrayProperty);
        }

        if (missing.Count != 0)
        {
            error =
                "Required Phase16.20.3 AppSettings fields are missing: " +
                string.Join(", ", missing);
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryInspectResolutions(
        SerializedProperty resolutions,
        out bool has1080p60,
        out string error)
    {
        has1080p60 = false;
        error = string.Empty;

        for (int i = 0; i < resolutions.arraySize; ++i)
        {
            SerializedProperty element =
                resolutions.GetArrayElementAtIndex(i);

            if (!TryReadResolution(
                    element,
                    out int width,
                    out int height,
                    out double frameRate))
            {
                error =
                    "Resolution entry " +
                    i.ToString(CultureInfo.InvariantCulture) +
                    " does not expose width/height/frameRate.";
                return false;
            }

            if (width == TargetWidth &&
                height == TargetHeight &&
                IsTargetFrameRate(frameRate))
            {
                has1080p60 = true;
            }
        }

        return true;
    }

    private static bool TryReadResolution(
        SerializedProperty element,
        out int width,
        out int height,
        out double frameRate)
    {
        width = 0;
        height = 0;
        frameRate = 0.0;

        if (element == null)
        {
            return false;
        }

        SerializedProperty widthProperty =
            element.FindPropertyRelative(WidthProperty);
        SerializedProperty heightProperty =
            element.FindPropertyRelative(HeightProperty);
        SerializedProperty frameRateProperty =
            element.FindPropertyRelative(FrameRateProperty);

        if (widthProperty == null ||
            heightProperty == null ||
            frameRateProperty == null)
        {
            return false;
        }

        width = widthProperty.intValue;
        height = heightProperty.intValue;
        frameRate = frameRateProperty.doubleValue;
        return true;
    }

    private static void AppendResolution(
        SerializedProperty resolutions,
        int width,
        int height,
        double frameRate)
    {
        int index = resolutions.arraySize;
        resolutions.arraySize++;

        SerializedProperty element =
            resolutions.GetArrayElementAtIndex(index);

        SerializedProperty widthProperty =
            element.FindPropertyRelative(WidthProperty);
        SerializedProperty heightProperty =
            element.FindPropertyRelative(HeightProperty);
        SerializedProperty frameRateProperty =
            element.FindPropertyRelative(FrameRateProperty);

        if (widthProperty == null ||
            heightProperty == null ||
            frameRateProperty == null)
        {
            throw new InvalidOperationException(
                "ResolutionStruct schema is not compatible.");
        }

        widthProperty.intValue = width;
        heightProperty.intValue = height;
        frameRateProperty.doubleValue = frameRate;
    }

    private static bool IsTargetFrameRate(
        double frameRate)
    {
        return
            !double.IsNaN(frameRate) &&
            !double.IsInfinity(frameRate) &&
            Math.Abs(frameRate - TargetFrameRate) <=
                FrameRateTolerance;
    }

    private static bool HasRequiredPreferredKeywords(
        SerializedProperty keywords)
    {
        for (int requiredIndex = 0;
             requiredIndex < RequiredPreferredKeywords.Length;
             ++requiredIndex)
        {
            bool found = false;
            string required =
                RequiredPreferredKeywords[requiredIndex];

            for (int i = 0; i < keywords.arraySize; ++i)
            {
                SerializedProperty element =
                    keywords.GetArrayElementAtIndex(i);

                if (element.propertyType ==
                        SerializedPropertyType.String &&
                    string.Equals(
                        element.stringValue,
                        required,
                        StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsurePreferredKeywords(
        SerializedProperty keywords)
    {
        for (int requiredIndex = 0;
             requiredIndex < RequiredPreferredKeywords.Length;
             ++requiredIndex)
        {
            string required =
                RequiredPreferredKeywords[requiredIndex];

            bool found = false;

            for (int i = 0; i < keywords.arraySize; ++i)
            {
                SerializedProperty element =
                    keywords.GetArrayElementAtIndex(i);

                if (element.propertyType ==
                        SerializedPropertyType.String &&
                    string.Equals(
                        element.stringValue,
                        required,
                        StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (found)
            {
                continue;
            }

            int index = keywords.arraySize;
            keywords.arraySize++;

            SerializedProperty newElement =
                keywords.GetArrayElementAtIndex(index);

            newElement.stringValue = required;
        }
    }

    private static bool TryValidateAsset(
        string assetPath,
        out string summary,
        out string error)
    {
        summary = string.Empty;
        error = string.Empty;

        ScriptableObject asset =
            AssetDatabase.LoadAssetAtPath<ScriptableObject>(
                assetPath);

        if (asset == null)
        {
            error =
                "Could not reload AppSettings asset: " +
                assetPath;
            return false;
        }

        SerializedObject serialized =
            new SerializedObject(asset);
        serialized.UpdateIfRequiredOrScript();

        if (!TryGetProperties(
                serialized,
                out Properties properties,
                out error))
        {
            return false;
        }

        if (properties.preferredDefaultWidth.intValue != TargetWidth)
        {
            error =
                PreferredDefaultWidthProperty +
                "=" +
                properties.preferredDefaultWidth.intValue.ToString(
                    CultureInfo.InvariantCulture) +
                ", expected 1920.";
            return false;
        }

        if (properties.preferredProfileWidth.intValue != TargetWidth ||
            properties.preferredProfileHeight.intValue != TargetHeight ||
            properties.preferredProfileFrameRate.intValue != TargetFrameRate)
        {
            error =
                "Preferred-device profile is " +
                properties.preferredProfileWidth.intValue.ToString(
                    CultureInfo.InvariantCulture) +
                "x" +
                properties.preferredProfileHeight.intValue.ToString(
                    CultureInfo.InvariantCulture) +
                "@" +
                properties.preferredProfileFrameRate.intValue.ToString(
                    CultureInfo.InvariantCulture) +
                ", expected 1920x1080@60.";
            return false;
        }

        if (!HasRequiredPreferredKeywords(
                properties.preferredDeviceKeywords))
        {
            error =
                "Preferred WebCam keywords do not contain both CM831 and UGREEN.";
            return false;
        }

        if (!TryInspectResolutions(
                properties.resolutions,
                out bool has1080p60,
                out error))
        {
            return false;
        }

        if (!has1080p60)
        {
            error =
                "Available WebCam resolutions do not contain 1920x1080@60.";
            return false;
        }

        summary =
            BuildProfileSummary(properties, assetPath);

        return true;
    }

    private static string BuildProfileSummary(
        Properties properties,
        string assetPath)
    {
        return
            "AppSettings=" + assetPath +
            " genericPreferredWidth=" +
            properties.preferredDefaultWidth.intValue.ToString(
                CultureInfo.InvariantCulture) +
            " preferredProfile=" +
            properties.preferredProfileWidth.intValue.ToString(
                CultureInfo.InvariantCulture) +
            "x" +
            properties.preferredProfileHeight.intValue.ToString(
                CultureInfo.InvariantCulture) +
            "@" +
            properties.preferredProfileFrameRate.intValue.ToString(
                CultureInfo.InvariantCulture);
    }

    private static bool TryCreateBackup(
        string assetPath,
        ScriptableObject asset,
        out string backupDirectory,
        out string error)
    {
        backupDirectory = string.Empty;
        error = string.Empty;

        try
        {
            DirectoryInfo projectDirectory =
                Directory.GetParent(
                    Application.dataPath);

            if (projectDirectory == null)
            {
                error = "Could not resolve project root.";
                return false;
            }

            string projectRoot =
                projectDirectory.FullName;

            string normalizedRelative =
                assetPath.Replace(
                    '/',
                    Path.DirectorySeparatorChar);

            string sourceAsset =
                Path.GetFullPath(
                    Path.Combine(
                        projectRoot,
                        normalizedRelative));

            if (!File.Exists(sourceAsset))
            {
                error =
                    "AppSettings source file does not exist: " +
                    sourceAsset;
                return false;
            }

            string timestamp =
                DateTime.UtcNow.ToString(
                    "yyyyMMdd_HHmmss_fff",
                    CultureInfo.InvariantCulture);

            backupDirectory =
                Path.Combine(
                    projectRoot,
                    "KiwiBackups",
                    "CameraProfiles",
                    "Phase16_20_3_" + timestamp);

            string backupAsset =
                Path.Combine(
                    backupDirectory,
                    normalizedRelative);

            string backupAssetDirectory =
                Path.GetDirectoryName(
                    backupAsset);

            if (string.IsNullOrEmpty(
                    backupAssetDirectory))
            {
                error =
                    "Could not resolve backup directory.";
                return false;
            }

            Directory.CreateDirectory(
                backupAssetDirectory);

            File.Copy(
                sourceAsset,
                backupAsset,
                false);

            string sourceMeta =
                sourceAsset + ".meta";

            if (File.Exists(sourceMeta))
            {
                File.Copy(
                    sourceMeta,
                    backupAsset + ".meta",
                    false);
            }

            string manifestPath =
                Path.Combine(
                    backupDirectory,
                    "MANIFEST.txt");

            string manifest =
                "KiwiAvatarSystem Phase16.20.3\n" +
                "Contract: " + ContractMarker + "\n" +
                "UTC: " +
                DateTime.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture) +
                "\n" +
                "Source asset: " + assetPath + "\n" +
                "Source instance: " +
                asset.GetInstanceID().ToString(
                    CultureInfo.InvariantCulture) +
                "\n" +
                "Target preferred profile: 1920x1080@60\n" +
                "Note: backup is outside Assets and is not imported by Unity.\n";

            File.WriteAllText(
                manifestPath,
                manifest);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            return false;
        }
    }
}
#endif
