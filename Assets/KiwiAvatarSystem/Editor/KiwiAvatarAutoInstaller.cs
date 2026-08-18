#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;

[InitializeOnLoad]
public static class KiwiAvatarAutoInstaller
{
    private const string TargetSceneName = "Face Landmark Detection";
    private const string MotionRootName = "AvatarMotionRoot";
    private const string SystemName = "AvatarSystem";
    private const string BackupFolder = "Assets/KiwiAvatarSystem/Backups";

    static KiwiAvatarAutoInstaller()
    {
        EditorApplication.delayCall += TryInstallInActiveScene;
        EditorSceneManager.sceneOpened += OnSceneOpened;
    }

    [MenuItem("Kiwi VTuber/Avatar System/Install or Repair")]
    public static void InstallOrRepairMenu()
    {
        TryInstallInActiveScene(true);
    }

    [MenuItem("Kiwi VTuber/Avatar System/Validate Setup")]
    public static void ValidateSetupMenu()
    {
        Scene scene = SceneManager.GetActiveScene();
        KiwiFaceMotion faceMotion = FindSceneComponent<KiwiFaceMotion>(scene);
        KiwiAvatarRuntimeManager manager = FindSceneComponent<KiwiAvatarRuntimeManager>(scene);
        Transform motionRoot = FindSceneTransform(scene, MotionRootName);

        string message =
            "Scene: " + scene.name + "\n" +
            "KiwiFaceMotion: " + (faceMotion != null ? "OK" : "MISSING") + "\n" +
            "AvatarMotionRoot: " + (motionRoot != null ? "OK" : "MISSING") + "\n" +
            "AvatarSystem: " + (manager != null ? "OK" : "MISSING") + "\n" +
            "KiwiFaceMotion target: " +
            (faceMotion != null && faceMotion.kiwiRoot == motionRoot ? "OK" : "CHECK");

        EditorUtility.DisplayDialog("Kiwi Avatar System", message, "OK");
    }

    [MenuItem("Kiwi VTuber/Avatar System/Open Models Folder")]
    public static void OpenModelsFolderMenu()
    {
        string models = GetEditorModelsDirectory();
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(Path.Combine(models, "Profiles"));
        EditorUtility.RevealInFinder(models);
    }

    [MenuItem("Kiwi VTuber/Avatar System/Open Backup Folder")]
    public static void OpenBackupFolderMenu()
    {
        string full = Path.GetFullPath(BackupFolder);
        Directory.CreateDirectory(full);
        AssetDatabase.Refresh();
        EditorUtility.RevealInFinder(full);
    }

    private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
    {
        EditorApplication.delayCall += TryInstallInActiveScene;
    }

    private static void TryInstallInActiveScene()
    {
        TryInstallInActiveScene(false);
    }

    private static void TryInstallInActiveScene(bool showDialog)
    {
        if (Application.isPlaying || EditorApplication.isCompiling)
        {
            return;
        }

        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded || scene.name != TargetSceneName)
        {
            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "Kiwi Avatar System",
                    "Open the 'Face Landmark Detection' scene, then run Install or Repair again.",
                    "OK"
                );
            }
            return;
        }

        KiwiFaceMotion faceMotion = FindSceneComponent<KiwiFaceMotion>(scene);
        if (faceMotion == null)
        {
            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "Kiwi Avatar System",
                    "KiwiFaceMotion was not found in the active scene. No changes were made.",
                    "OK"
                );
            }
            return;
        }

        KiwiAvatarRuntimeManager existingManager =
            FindSceneComponent<KiwiAvatarRuntimeManager>(scene);
        Transform motionRoot = FindSceneTransform(scene, MotionRootName);

        Transform originalModel = ResolveFallbackModel(scene, faceMotion, motionRoot, existingManager);
        if (originalModel == null)
        {
            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "Kiwi Avatar System",
                    "The embedded Kiwi model could not be identified. No changes were made.",
                    "OK"
                );
            }
            return;
        }

        Transform faceAnchor = existingManager != null && existingManager.faceAnchor != null
            ? existingManager.faceAnchor
            : FindChildByName(originalModel, "FaceAnchor");

        if (faceAnchor == null)
        {
            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "Kiwi Avatar System",
                    "FaceAnchor was not found under the current Kiwi model. No changes were made.",
                    "OK"
                );
            }
            return;
        }

        Transform fallbackHead = faceAnchor.parent;
        if (fallbackHead == null)
        {
            Animator animator = originalModel.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.isHuman)
            {
                fallbackHead = animator.GetBoneTransform(HumanBodyBones.Head);
            }
        }

        if (fallbackHead == null)
        {
            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "Kiwi Avatar System",
                    "The current head transform could not be identified. No changes were made.",
                    "OK"
                );
            }
            return;
        }

        bool runtimeComponentsComplete =
            existingManager != null &&
            existingManager.GetComponent<KiwiAvatarRuntimePanel>() != null &&
            existingManager.GetComponent<KiwiPlatformRuntime>() != null &&
            existingManager.GetComponent<KiwiMobilePermissionGate>() != null;

        bool setupHealthy =
            existingManager != null &&
            motionRoot != null &&
            faceMotion.kiwiRoot == motionRoot &&
            existingManager.faceMotion == faceMotion &&
            existingManager.motionRoot == motionRoot &&
            existingManager.fallbackModel == originalModel &&
            existingManager.fallbackHead == fallbackHead &&
            existingManager.faceAnchor == faceAnchor &&
            runtimeComponentsComplete;

        // Scene-open auto repair should be effectively free once installation is healthy.
        // The explicit menu command still performs a full repair pass.
        if (!showDialog && setupHealthy)
        {
            EnsureModelDirectories();
            return;
        }

        bool structuralInstallNeeded =
            existingManager == null ||
            motionRoot == null ||
            faceMotion.kiwiRoot != motionRoot;

        if (structuralInstallNeeded)
        {
            CreateSceneBackup(scene);
        }

        if (motionRoot == null)
        {
            GameObject rootObject = new GameObject(MotionRootName);
            Undo.RegisterCreatedObjectUndo(rootObject, "Create AvatarMotionRoot");
            motionRoot = rootObject.transform;

            Transform oldParent = originalModel.parent;
            int oldSibling = originalModel.GetSiblingIndex();

            motionRoot.SetParent(oldParent, false);
            motionRoot.SetSiblingIndex(oldSibling);
            motionRoot.localPosition = originalModel.localPosition;
            motionRoot.localRotation = originalModel.localRotation;
            motionRoot.localScale = originalModel.localScale;

            Undo.SetTransformParent(originalModel, motionRoot, "Move Kiwi under AvatarMotionRoot");
            originalModel.localPosition = Vector3.zero;
            originalModel.localRotation = Quaternion.identity;
            originalModel.localScale = Vector3.one;
        }

        if (originalModel.parent != motionRoot)
        {
            Undo.SetTransformParent(originalModel, motionRoot, "Repair Kiwi parent");
        }

        Undo.RecordObject(faceMotion, "Bind KiwiFaceMotion to AvatarMotionRoot");
        faceMotion.kiwiRoot = motionRoot;
        EditorUtility.SetDirty(faceMotion);

        KiwiAvatarRuntimeManager manager = existingManager;
        if (manager == null)
        {
            GameObject systemObject = FindSceneGameObject(scene, SystemName);
            if (systemObject == null)
            {
                systemObject = new GameObject(SystemName);
                Undo.RegisterCreatedObjectUndo(systemObject, "Create AvatarSystem");
            }

            manager = systemObject.GetComponent<KiwiAvatarRuntimeManager>();
            if (manager == null)
            {
                manager = Undo.AddComponent<KiwiAvatarRuntimeManager>(systemObject);
            }
        }

        Undo.RecordObject(manager, "Configure Kiwi Avatar System");
        manager.faceMotion = faceMotion;
        manager.motionRoot = motionRoot;
        manager.fallbackModel = originalModel;
        manager.fallbackHead = fallbackHead;
        manager.faceAnchor = faceAnchor;
        if (string.IsNullOrWhiteSpace(manager.vtuberLayerName))
        {
            manager.vtuberLayerName = "VTuberModel1";
        }
        if (manager.lowMemoryDeviceModelLimitMB <= 0)
        {
            manager.lowMemoryDeviceModelLimitMB = 64;
        }

        if (structuralInstallNeeded || !manager.fallbackReferenceCaptured)
        {
            manager.CaptureFallbackReferencesNow();
        }
        EditorUtility.SetDirty(manager);

        EnsureRuntimePanel(manager);
        EnsurePlatformComponents(manager, scene);
        EnsureModelDirectories();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log(
            "[KiwiAvatarSystem] v" + KiwiAvatarRuntimeManager.PackageVersion +
            " installed/repaired. Existing KiwiFaceMotion tracking logic was not modified."
        );

        if (showDialog)
        {
            EditorUtility.DisplayDialog(
                "Kiwi Avatar System",
                "Installation / repair completed.\n\n" +
                "Existing tracking was preserved.\n" +
                "Windows: press Play and use F8.\n" +
                "Android/iOS: use the on-screen UI button.",
                "OK"
            );
        }
    }

    private static Transform ResolveFallbackModel(
        Scene scene,
        KiwiFaceMotion faceMotion,
        Transform motionRoot,
        KiwiAvatarRuntimeManager manager)
    {
        if (manager != null && manager.fallbackModel != null)
        {
            return manager.fallbackModel;
        }

        if (faceMotion.kiwiRoot != null && faceMotion.kiwiRoot != motionRoot)
        {
            return faceMotion.kiwiRoot;
        }

        Transform named = FindSceneTransform(scene, "uni_kiwi_textured_rigged");
        if (named != null)
        {
            return named;
        }

        if (motionRoot != null && motionRoot.childCount > 0)
        {
            return motionRoot.GetChild(0);
        }

        return null;
    }

    private static void CreateSceneBackup(Scene scene)
    {
        if (string.IsNullOrEmpty(scene.path))
        {
            Debug.LogWarning("[KiwiAvatarSystem] Scene has no asset path. Backup was skipped.");
            return;
        }

        Directory.CreateDirectory(BackupFolder);
        AssetDatabase.Refresh();

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string version = KiwiAvatarRuntimeManager.PackageVersion.Replace('.', '_');
        string backupPath = BackupFolder + "/" +
            SanitizeFileName(scene.name) +
            ".before_KiwiAvatarSystem_v" + version +
            "_" + stamp + ".unity";

        int suffix = 2;
        while (File.Exists(backupPath))
        {
            backupPath = BackupFolder + "/" +
                SanitizeFileName(scene.name) +
                ".before_KiwiAvatarSystem_v" + version +
                "_" + stamp + "_" + suffix + ".unity";
            suffix++;
        }

        bool saved = EditorSceneManager.SaveScene(scene, backupPath, true);
        if (saved)
        {
            Debug.Log("[KiwiAvatarSystem] Scene backup created: " + backupPath);
        }
        else
        {
            Debug.LogWarning("[KiwiAvatarSystem] Scene backup could not be created.");
        }
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = value;
        for (int i = 0; i < invalid.Length; i++)
        {
            result = result.Replace(invalid[i], '_');
        }
        return result;
    }

    private static void EnsureRuntimePanel(KiwiAvatarRuntimeManager manager)
    {
        KiwiAvatarRuntimePanel panel = manager.GetComponent<KiwiAvatarRuntimePanel>();
        if (panel == null)
        {
            panel = Undo.AddComponent<KiwiAvatarRuntimePanel>(manager.gameObject);
        }

        panel.manager = manager;
        panel.visible = true;
        EditorUtility.SetDirty(panel);
    }

    private static void EnsurePlatformComponents(KiwiAvatarRuntimeManager manager, Scene scene)
    {
        KiwiPlatformRuntime platform = manager.GetComponent<KiwiPlatformRuntime>();
        bool platformCreated = platform == null;
        if (platformCreated)
        {
            platform = Undo.AddComponent<KiwiPlatformRuntime>(manager.gameObject);
        }

        GameObject cameraObject = FindSceneGameObject(scene, "VTuberCamera");
        GameObject spoutObject = FindSceneGameObject(scene, "SpoutOutput");
        platform.vtuberCamera = cameraObject != null ? cameraObject.GetComponent<Camera>() : null;
        platform.spoutOutput = spoutObject;

        if (platformCreated)
        {
            platform.mobileTargetFrameRate = 60;
            platform.keepScreenAwake = true;
            platform.showMobilePreview = true;
            platform.useOptimizedMobileOutput = true;
            platform.mobileOutputWidth = 1280;
            platform.mobileOutputHeight = 720;
            platform.adaptiveLowMemoryOutput = true;
            platform.lowMemoryDeviceThresholdMB = 3072;
            platform.lowMemoryOutputWidth = 960;
            platform.lowMemoryOutputHeight = 540;
        }
        else
        {
            if (platform.lowMemoryDeviceThresholdMB <= 0) platform.lowMemoryDeviceThresholdMB = 3072;
            if (platform.lowMemoryOutputWidth <= 0) platform.lowMemoryOutputWidth = 960;
            if (platform.lowMemoryOutputHeight <= 0) platform.lowMemoryOutputHeight = 540;
        }

        EditorUtility.SetDirty(platform);

        KiwiMobilePermissionGate gate = manager.GetComponent<KiwiMobilePermissionGate>();
        if (gate == null)
        {
            gate = Undo.AddComponent<KiwiMobilePermissionGate>(manager.gameObject);
        }

        gate.runner = FindSceneComponent<FaceLandmarkerRunner>(scene);
        EditorUtility.SetDirty(gate);
    }

    private static void EnsureModelDirectories()
    {
        string models = GetEditorModelsDirectory();
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(Path.Combine(models, "Profiles"));
    }

    private static string GetEditorModelsDirectory()
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(root, "Models");
    }

    private static T FindSceneComponent<T>(Scene scene) where T : Component
    {
        T[] all = Resources.FindObjectsOfTypeAll<T>();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].gameObject.scene == scene)
            {
                return all[i];
            }
        }
        return null;
    }

    private static GameObject FindSceneGameObject(Scene scene, string objectName)
    {
        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            Transform found = FindChildByName(roots[i].transform, objectName);
            if (found != null)
            {
                return found.gameObject;
            }
        }
        return null;
    }

    private static Transform FindSceneTransform(Scene scene, string objectName)
    {
        GameObject obj = FindSceneGameObject(scene, objectName);
        return obj != null ? obj.transform : null;
    }

    private static Transform FindChildByName(Transform root, string targetName)
    {
        if (root == null)
        {
            return null;
        }

        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (string.Equals(all[i].name, targetName, StringComparison.Ordinal))
            {
                return all[i];
            }
        }
        return null;
    }
}
#endif
