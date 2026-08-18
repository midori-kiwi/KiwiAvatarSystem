using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UniGLTF;
using UnityEngine;
using VRM;

[DefaultExecutionOrder(-500)]
[DisallowMultipleComponent]
public sealed class KiwiAvatarRuntimeManager : MonoBehaviour
{
    public const string PackageVersion = "1.0.0";

    [Header("Tracking / Scene References")]
    public KiwiFaceMotion faceMotion;
    public Transform motionRoot;
    public Transform fallbackModel;
    public Transform fallbackHead;
    public Transform faceAnchor;
    public KiwiSurfaceFitter surfaceFitter;

    [Header("Captured Embedded Kiwi Reference")]
    public Vector3 fallbackFaceAnchorHeadLocalPosition;
    public Vector3 fallbackFaceAnchorHeadLocalEulerAngles;
    public Vector3 fallbackFaceAnchorHeadLocalScale = Vector3.one;
    public Vector3 fallbackHeadMotionLocalPosition;
    public Vector3 fallbackHeadMotionLocalEulerAngles;
    public Vector3 fallbackFaceAnchorMotionLocalPosition;
    public Vector3 fallbackFaceAnchorMotionLocalEulerAngles;
    public Vector3 fallbackFaceAnchorRelativeLossyScale = Vector3.one;
    public bool fallbackReferenceCaptured;

    [Header("Runtime Models")]
    public string vtuberLayerName = "VTuberModel1";
    public bool autoLoadLastAvatar = true;
    public bool enableSpringBone = true;
    [Range(16, 1024)] public int maximumRuntimeModelSizeMB = 200;
    [Range(16, 512)] public int mobileMaximumRuntimeModelSizeMB = 128;
    public bool adaptiveMobileMemoryGuard = true;
    [Range(16, 256)] public int lowMemoryDeviceModelLimitMB = 64;

    [Header("Adaptive Head / Face Fit")]
    public bool enableAdaptiveHeadFit = true;
    [Range(0f, 1f)] public float minimumAdaptiveFaceConfidence = 0.52f;
    [Range(2000, 60000)] public int maximumAdaptiveVertexSamples = 24000;
    [Range(0.1f, 1f)] public float minimumAdaptiveScaleRatio = 0.35f;
    [Range(1f, 8f)] public float maximumAdaptiveScaleRatio = 3.5f;

    [Header("Runtime Status")]
    [SerializeField] private string currentAvatarName = "Kiwi (Embedded)";
    [SerializeField] private string status = "Not initialized";
    [SerializeField] private bool busy;

    private const string LastAvatarKey = "KiwiAvatarSystem.LastAvatar.v1";
    private readonly List<string> _modelFiles = new List<string>();
    private RuntimeGltfInstance _activeInstance;
    private Transform _activeModel;
    private Transform _activeHead;
    private KiwiAvatarProfile _activeProfile;
    private string _activeModelPath = string.Empty;
    private KiwiHeadGeometry _fallbackGeometry;
    private KiwiHeadGeometry _activeGeometry;
    private string _activeFaceFitMethod = "Embedded";
    private float _activeFaceFitConfidence = 1f;

    public IReadOnlyList<string> ModelFiles => _modelFiles;
    public string ModelsDirectory => KiwiAvatarStorage.ModelsDirectory;
    public bool IsBusy => busy;
    public bool IsExternalAvatarActive => _activeModel != null;
    public int EffectiveModelSizeLimitMB => CalculateEffectiveModelLimitMB();
    public KiwiAvatarProfile ActiveProfile => _activeProfile;
    public string CurrentAvatarName => currentAvatarName;
    public string Status => status;
    public string ActiveFaceFitMethod => _activeFaceFitMethod;
    public float ActiveFaceFitConfidence => _activeFaceFitConfidence;

    private void Awake()
    {
        KiwiAvatarStorage.EnsureDirectories();
        if (surfaceFitter == null)
        {
            surfaceFitter = FindFirstObjectByType<KiwiSurfaceFitter>(
                FindObjectsInactive.Include
            );
        }
        if (!fallbackReferenceCaptured)
        {
            CaptureFallbackReferencesNow();
        }
        EnsureFallbackGeometry();
    }

    private void Start()
    {
        ScanModels();
        SwitchToFallbackInternal(false);

        if (autoLoadLastAvatar)
        {
            string lastPath = PlayerPrefs.GetString(LastAvatarKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(lastPath) && File.Exists(lastPath))
            {
                SwitchToModel(lastPath);
            }
        }
    }

    public void CaptureFallbackReferencesNow()
    {
        if (motionRoot == null && fallbackModel != null)
        {
            motionRoot = fallbackModel.parent;
        }

        if (fallbackHead == null && fallbackModel != null)
        {
            Animator animator = fallbackModel.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.isHuman)
            {
                fallbackHead = animator.GetBoneTransform(HumanBodyBones.Head);
            }
        }

        if (faceAnchor == null && fallbackModel != null)
        {
            faceAnchor = FindChildByName(fallbackModel, "FaceAnchor");
        }

        if (fallbackHead == null || faceAnchor == null)
        {
            fallbackReferenceCaptured = false;
            return;
        }

        fallbackFaceAnchorHeadLocalPosition = fallbackHead.InverseTransformPoint(faceAnchor.position);
        fallbackFaceAnchorHeadLocalEulerAngles =
            (Quaternion.Inverse(fallbackHead.rotation) * faceAnchor.rotation).eulerAngles;
        fallbackFaceAnchorHeadLocalScale = faceAnchor.localScale;
        fallbackFaceAnchorRelativeLossyScale = DivideScale(faceAnchor.lossyScale, fallbackHead.lossyScale);

        if (motionRoot != null)
        {
            fallbackHeadMotionLocalPosition = motionRoot.InverseTransformPoint(fallbackHead.position);
            fallbackHeadMotionLocalEulerAngles =
                (Quaternion.Inverse(motionRoot.rotation) * fallbackHead.rotation).eulerAngles;
            fallbackFaceAnchorMotionLocalPosition = motionRoot.InverseTransformPoint(faceAnchor.position);
            fallbackFaceAnchorMotionLocalEulerAngles =
                (Quaternion.Inverse(motionRoot.rotation) * faceAnchor.rotation).eulerAngles;
        }

        fallbackReferenceCaptured = true;
        _fallbackGeometry = default;
        EnsureFallbackGeometry();
    }

    public void ScanModels()
    {
        KiwiAvatarStorage.EnsureDirectories();
        KiwiAvatarStorage.NormalizeExistingModelFileNames();
        _modelFiles.Clear();

        foreach (string path in Directory.EnumerateFiles(
            KiwiAvatarStorage.ModelsDirectory,
            "*",
            SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetExtension(path), ".vrm", StringComparison.OrdinalIgnoreCase))
            {
                _modelFiles.Add(Path.GetFullPath(path));
            }
        }

        _modelFiles.Sort(StringComparer.OrdinalIgnoreCase);
        if (!busy)
        {
            status = _modelFiles.Count + " external model(s) found";
        }
    }

    public void ImportVrmFromPicker()
    {
        if (busy)
        {
            return;
        }

        long maximumBytes = (long)EffectiveModelSizeLimitMB * 1024L * 1024L;

        if (KiwiAvatarStorage.IsMobileRuntime)
        {
            KiwiMobileFilePicker.Instance.PickVrm(
                maximumBytes,
                KiwiAvatarStorage.ModelsDirectory,
                OnMobileModelPicked,
                message => status = message
            );
            return;
        }

        if (!KiwiWindowsFilePicker.TryPickVrm(out string selectedPath))
        {
            status = "Model import cancelled";
            return;
        }

        ImportAndSwitch(selectedPath, maximumBytes);
    }

    private void OnMobileModelPicked(string selectedPath)
    {
        long maximumBytes = (long)EffectiveModelSizeLimitMB * 1024L * 1024L;
        try
        {
            ImportAndSwitch(selectedPath, maximumBytes);
        }
        finally
        {
            KiwiMobileFilePicker.CleanupTemporaryResult(selectedPath);
        }
    }

    private void ImportAndSwitch(string selectedPath, long maximumBytes)
    {
        try
        {
            string importedPath = KiwiAvatarStorage.ImportExternalFile(selectedPath, maximumBytes);
            ScanModels();
            SwitchToModel(importedPath);
        }
        catch (Exception exception)
        {
            status = "Import failed: " + exception.Message;
            Debug.LogException(exception, this);
        }
    }

    public void OpenModelsFolder()
    {
        KiwiAvatarStorage.EnsureDirectories();
        Application.OpenURL("file:///" + KiwiAvatarStorage.ModelsDirectory.Replace('\\', '/'));
    }

    public async void SwitchToModel(string path)
    {
        if (busy || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        path = Path.GetFullPath(path);
        if (!File.Exists(path) || !KiwiAvatarStorage.IsPathInsideDirectory(path, KiwiAvatarStorage.ModelsDirectory))
        {
            status = "Model file is unavailable";
            return;
        }

        FileInfo info = new FileInfo(path);
        long maximumBytes = (long)EffectiveModelSizeLimitMB * 1024L * 1024L;
        if (maximumBytes > 0 && info.Length > maximumBytes)
        {
            status = "Model exceeds the " + EffectiveModelSizeLimitMB + " MB runtime limit";
            return;
        }

        busy = true;
        status = "Loading " + Path.GetFileName(path) + "...";
        RuntimeGltfInstance candidate = null;

        try
        {
            candidate = await VrmUtility.LoadAsync(path, new RuntimeOnlyAwaitCaller());
            if (candidate == null || candidate.Root == null)
            {
                throw new InvalidDataException("VRM importer returned no model root.");
            }

            candidate.ShowMeshes();
            candidate.EnableUpdateWhenOffscreen();
            Transform candidateRoot = candidate.Root.transform;
            candidateRoot.SetParent(motionRoot, false);
            candidateRoot.localPosition = Vector3.zero;
            candidateRoot.localRotation = Quaternion.identity;
            candidateRoot.localScale = Vector3.one;
            SetLayerRecursively(candidateRoot.gameObject, ResolveVtuberLayer());

            Animator animator = candidateRoot.GetComponentInChildren<Animator>(true);
            Transform head = animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.Head)
                : FindChildByName(candidateRoot, "head");
            if (head == null)
            {
                throw new InvalidDataException("The VRM has no usable head transform.");
            }

            KiwiAvatarProfile profile = LoadProfile(path);
            RuntimeGltfInstance previousInstance = _activeInstance;

            _activeInstance = candidate;
            _activeModel = candidateRoot;
            _activeHead = head;
            _activeModelPath = path;
            _activeProfile = profile;
            candidate = null;

            ApplyActiveProfile();
            if (fallbackModel != null)
            {
                fallbackModel.gameObject.SetActive(false);
            }

            if (previousInstance != null && previousInstance != _activeInstance)
            {
                Destroy(previousInstance.Root);
            }

            currentAvatarName = string.IsNullOrWhiteSpace(profile.displayName)
                ? Path.GetFileNameWithoutExtension(path)
                : profile.displayName;
            status = "Ready";
            PlayerPrefs.SetString(LastAvatarKey, path);
            PlayerPrefs.Save();
            SaveActiveProfile();
        }
        catch (Exception exception)
        {
            if (candidate != null && candidate.Root != null)
            {
                Destroy(candidate.Root);
            }
            status = "Load failed: " + exception.Message;
            Debug.LogException(exception, this);
        }
        finally
        {
            busy = false;
        }
    }

    public void SwitchToFallback()
    {
        if (!busy)
        {
            SwitchToFallbackInternal(true);
        }
    }

    private void SwitchToFallbackInternal(bool clearLastAvatar)
    {
        if (!fallbackReferenceCaptured)
        {
            CaptureFallbackReferencesNow();
        }

        RestoreFallbackFaceAnchor();
        if (fallbackModel != null)
        {
            fallbackModel.gameObject.SetActive(true);
            SetLayerRecursively(fallbackModel.gameObject, ResolveVtuberLayer());
        }

        ApplySurfaceFit(fallbackModel, fallbackHead, "Embedded");

        if (_activeInstance != null && _activeInstance.Root != null)
        {
            Destroy(_activeInstance.Root);
        }

        _activeInstance = null;
        _activeModel = null;
        _activeHead = null;
        _activeProfile = null;
        _activeModelPath = string.Empty;
        _activeGeometry = default;
        _activeFaceFitMethod = "Embedded";
        _activeFaceFitConfidence = 1f;
        currentAvatarName = "Kiwi (Embedded)";
        status = "Ready";

        if (faceMotion != null && motionRoot != null)
        {
            faceMotion.kiwiRoot = motionRoot;
            faceMotion.RecenterTracking();
        }

        if (clearLastAvatar)
        {
            PlayerPrefs.DeleteKey(LastAvatarKey);
            PlayerPrefs.Save();
        }
    }

    public void NudgeModel(Vector3 localDelta)
    {
        if (_activeModel == null || _activeProfile == null) return;
        _activeModel.localPosition += localDelta;
        _activeProfile.modelLocalPosition = _activeModel.localPosition;
    }

    public void ScaleModel(float multiplier)
    {
        if (_activeModel == null || _activeProfile == null || !IsFinitePositive(multiplier)) return;
        _activeModel.localScale *= multiplier;
        _activeProfile.autoFitMultiplier = Mathf.Clamp(
            _activeProfile.autoFitMultiplier * multiplier,
            0.1f,
            5f
        );
        _activeProfile.modelLocalScale = _activeModel.localScale;
        ReconstructSpringBone();
    }

    public void ResetModelAutoFit()
    {
        if (_activeProfile == null) return;
        _activeProfile.modelFitMode = KiwiAvatarModelFitMode.AdaptiveHead;
        _activeProfile.autoFitMultiplier = 1f;
        _activeProfile.modelLocalPosition = Vector3.zero;
        _activeProfile.modelLocalScale = Vector3.one;
        ApplyActiveProfile();
    }

    public void SetReferenceHeightFit()
    {
        if (_activeProfile == null) return;
        _activeProfile.modelFitMode = KiwiAvatarModelFitMode.ReferenceHeight;
        _activeProfile.autoFitMultiplier = 1f;
        _activeProfile.modelLocalPosition = Vector3.zero;
        _activeProfile.modelLocalScale = Vector3.one;
        ApplyActiveProfile();
    }

    public void NudgeFaceAnchor(Vector3 localDelta)
    {
        if (faceAnchor == null || _activeProfile == null) return;
        faceAnchor.localPosition += localDelta;
        _activeProfile.faceFitMode = KiwiAvatarFaceFitMode.Custom;
        StoreFaceAnchorInProfile();
        _activeFaceFitMethod = "Custom";
    }

    public void ScaleFaceAnchor(float multiplier)
    {
        if (faceAnchor == null || _activeProfile == null || !IsFinitePositive(multiplier)) return;
        faceAnchor.localScale *= multiplier;
        _activeProfile.faceFitMode = KiwiAvatarFaceFitMode.Custom;
        StoreFaceAnchorInProfile();
        _activeFaceFitMethod = "Custom";
    }

    public void ResetActiveFaceAnchorToAuto()
    {
        if (_activeProfile == null) return;
        _activeProfile.faceFitMode = KiwiAvatarFaceFitMode.AdaptiveHead;
        _activeProfile.adaptiveFaceScale = 1f;
        _activeProfile.adaptiveFaceOffsetNormalized = Vector3.zero;
        ApplyFaceAnchorFit();
    }

    public void SetLegacyFaceFit()
    {
        if (_activeProfile == null) return;
        _activeProfile.faceFitMode = KiwiAvatarFaceFitMode.LegacyReference;
        ApplyFaceAnchorFit();
    }

    public void SaveActiveProfile()
    {
        if (_activeProfile == null || string.IsNullOrWhiteSpace(_activeModelPath)) return;
        try
        {
            KiwiAvatarStorage.EnsureDirectories();
            _activeProfile.modelFileName = Path.GetFileName(_activeModelPath);
            string json = JsonUtility.ToJson(_activeProfile, true);
            File.WriteAllText(GetProfilePath(_activeModelPath), json);
        }
        catch (Exception exception)
        {
            status = "Profile save failed: " + exception.Message;
            Debug.LogException(exception, this);
        }
    }

    public void SetActiveSpringBoneEnabled(bool enabled)
    {
        if (_activeProfile == null || _activeModel == null) return;
        _activeProfile.springBoneEnabled = enabled;
        SetSpringComponentsEnabled(_activeModel, enabled && enableSpringBone);
    }

    public void RestoreSpringBoneInitialTransform()
    {
        if (_activeModel == null) return;
        VRMSpringBone[] springs = _activeModel.GetComponentsInChildren<VRMSpringBone>(true);
        for (int i = 0; i < springs.Length; i++)
        {
            springs[i].ReinitializeRotation();
        }
    }

    public void ReconstructSpringBone()
    {
        if (_activeModel == null) return;
        VRMSpringBone[] springs = _activeModel.GetComponentsInChildren<VRMSpringBone>(true);
        for (int i = 0; i < springs.Length; i++)
        {
            springs[i].Setup(true);
        }
    }

    private void ApplyActiveProfile()
    {
        if (_activeModel == null || _activeHead == null || _activeProfile == null) return;

        _activeModel.localPosition = Vector3.zero;
        _activeModel.localRotation = Quaternion.identity;
        _activeModel.localScale = Vector3.one;
        EnsureFallbackGeometry();
        _activeGeometry = AnalyzeGeometry(_activeModel, _activeHead);

        float baseScale = 1f;
        if (_activeProfile.modelFitMode == KiwiAvatarModelFitMode.AdaptiveHead &&
            enableAdaptiveHeadFit && _fallbackGeometry.valid && _activeGeometry.valid)
        {
            float referenceWidth = _fallbackGeometry.GetWorldWidth(fallbackHead);
            float targetWidth = _activeGeometry.GetWorldWidth(_activeHead);
            if (referenceWidth > 0.00001f && targetWidth > 0.00001f)
            {
                baseScale = referenceWidth / targetWidth;
            }
        }
        else if (_activeProfile.modelFitMode == KiwiAvatarModelFitMode.ReferenceHeight)
        {
            baseScale = CalculateReferenceHeightScale();
        }

        baseScale = Mathf.Clamp(baseScale, minimumAdaptiveScaleRatio, maximumAdaptiveScaleRatio);
        baseScale *= Mathf.Clamp(_activeProfile.autoFitMultiplier, 0.1f, 5f);

        if (_activeProfile.modelFitMode == KiwiAvatarModelFitMode.Custom)
        {
            _activeModel.localScale = SanitizeScale(_activeProfile.modelLocalScale);
            _activeModel.localEulerAngles = _activeProfile.modelLocalEulerAngles;
        }
        else
        {
            _activeModel.localScale = Vector3.one * baseScale;
        }
        _activeModel.localPosition = _activeProfile.modelLocalPosition;

        ApplyFaceAnchorFit();
        ApplySurfaceFit(_activeModel, _activeHead, _activeFaceFitMethod);
        SetSpringComponentsEnabled(
            _activeModel,
            enableSpringBone && _activeProfile.springBoneEnabled
        );
        ReconstructSpringBone();

        if (faceMotion != null && motionRoot != null)
        {
            faceMotion.kiwiRoot = motionRoot;
            faceMotion.RecenterTracking();
        }
    }

    private void ApplyFaceAnchorFit()
    {
        if (faceAnchor == null || _activeHead == null || _activeProfile == null) return;
        faceAnchor.SetParent(_activeHead, false);

        if (_activeProfile.faceFitMode == KiwiAvatarFaceFitMode.Custom)
        {
            faceAnchor.localPosition = _activeProfile.faceAnchorLocalPosition;
            faceAnchor.localEulerAngles = _activeProfile.faceAnchorLocalEulerAngles;
            faceAnchor.localScale = SanitizeScale(_activeProfile.faceAnchorLocalScale);
            _activeFaceFitMethod = "Custom";
            _activeFaceFitConfidence = 1f;
            return;
        }

        bool adaptive =
            _activeProfile.faceFitMode == KiwiAvatarFaceFitMode.AdaptiveHead &&
            enableAdaptiveHeadFit &&
            _fallbackGeometry.valid &&
            _activeGeometry.valid &&
            _activeGeometry.confidence >= minimumAdaptiveFaceConfidence;

        if (adaptive)
        {
            faceAnchor.localPosition = KiwiAdaptiveFaceFitter.MapReferenceAnchor(
                _fallbackGeometry,
                fallbackFaceAnchorHeadLocalPosition,
                _activeGeometry,
                _activeProfile.adaptiveFaceOffsetNormalized
            );
            Quaternion referenceRotation = Quaternion.Euler(fallbackFaceAnchorHeadLocalEulerAngles);
            faceAnchor.localRotation =
                _activeGeometry.faceRotationHeadLocal *
                Quaternion.Inverse(_fallbackGeometry.faceRotationHeadLocal) *
                referenceRotation;
            faceAnchor.localScale =
                fallbackFaceAnchorHeadLocalScale *
                Mathf.Clamp(_activeProfile.adaptiveFaceScale, 0.25f, 4f);
            _activeFaceFitMethod = _activeGeometry.hasEyeSemanticReference
                ? "Adaptive Head + Eyes"
                : "Adaptive Head";
            _activeFaceFitConfidence = _activeGeometry.confidence;
        }
        else
        {
            faceAnchor.localPosition = fallbackFaceAnchorHeadLocalPosition;
            faceAnchor.localEulerAngles = fallbackFaceAnchorHeadLocalEulerAngles;
            faceAnchor.localScale = fallbackFaceAnchorHeadLocalScale;
            _activeFaceFitMethod = adaptive ? "Adaptive Head" : "Legacy Reference";
            _activeFaceFitConfidence = adaptive ? _activeGeometry.confidence : 0f;
        }
    }

    private void RestoreFallbackFaceAnchor()
    {
        if (faceAnchor == null || fallbackHead == null) return;
        faceAnchor.SetParent(fallbackHead, false);
        faceAnchor.localPosition = fallbackFaceAnchorHeadLocalPosition;
        faceAnchor.localEulerAngles = fallbackFaceAnchorHeadLocalEulerAngles;
        faceAnchor.localScale = fallbackFaceAnchorHeadLocalScale;
    }

    private void ApplySurfaceFit(Transform model, Transform head, string fallbackMethod)
    {
        if (surfaceFitter == null || model == null || faceAnchor == null)
        {
            return;
        }

        SkinnedMeshRenderer renderer =
            KiwiSurfaceFitter.FindBestFaceRenderer(model, head);
        if (renderer == null)
        {
            _activeFaceFitMethod = fallbackMethod;
            return;
        }

        surfaceFitter.modelRoot = model;
        surfaceFitter.partsRoot = faceAnchor;
        surfaceFitter.targetRenderer = renderer;
        surfaceFitter.FitAllNow();

        if (surfaceFitter.LastFitSucceeded)
        {
            _activeFaceFitMethod = "Raycast + Normal";
            _activeFaceFitConfidence = surfaceFitter.LastSuccessRate;
        }
        else
        {
            _activeFaceFitMethod = fallbackMethod;
        }
    }

    private KiwiAvatarProfile LoadProfile(string modelPath)
    {
        KiwiAvatarProfile profile = null;
        string profilePath = GetProfilePath(modelPath);
        try
        {
            if (File.Exists(profilePath))
            {
                profile = JsonUtility.FromJson<KiwiAvatarProfile>(File.ReadAllText(profilePath));
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[KiwiAvatarSystem] Profile load failed: " + exception.Message, this);
        }

        if (profile == null)
        {
            profile = KiwiAvatarProfile.CreateDefault(
                Path.GetFileName(modelPath),
                Path.GetFileNameWithoutExtension(modelPath)
            );
        }

        profile.MigrateIfNeeded();
        return profile;
    }

    private static string GetProfilePath(string modelPath)
    {
        string safeName = KiwiAvatarStorage.SanitizeFileName(Path.GetFileName(modelPath));
        return Path.Combine(KiwiAvatarStorage.ProfilesDirectory, safeName + ".json");
    }

    private void StoreFaceAnchorInProfile()
    {
        _activeProfile.faceAnchorLocalPosition = faceAnchor.localPosition;
        _activeProfile.faceAnchorLocalEulerAngles = faceAnchor.localEulerAngles;
        _activeProfile.faceAnchorLocalScale = faceAnchor.localScale;
    }

    private void EnsureFallbackGeometry()
    {
        if (_fallbackGeometry.valid || fallbackModel == null || fallbackHead == null) return;
        _fallbackGeometry = AnalyzeGeometry(fallbackModel, fallbackHead);

        Transform leftEye = FindChildByName(fallbackModel, "LeftEye3D");
        Transform rightEye = FindChildByName(fallbackModel, "RightEye3D");
        if (_fallbackGeometry.valid && leftEye != null && rightEye != null)
        {
            _fallbackGeometry.hasEyeSemanticReference = true;
            _fallbackGeometry.eyeCenterHeadLocal = fallbackHead.InverseTransformPoint(
                (leftEye.position + rightEye.position) * 0.5f
            );
            _fallbackGeometry.eyeSpanLocal = Vector3.Distance(
                fallbackHead.InverseTransformPoint(leftEye.position),
                fallbackHead.InverseTransformPoint(rightEye.position)
            );
        }
    }

    private KiwiHeadGeometry AnalyzeGeometry(Transform model, Transform head)
    {
        Animator animator = model != null ? model.GetComponentInChildren<Animator>(true) : null;
        Quaternion fallbackRotation = Quaternion.Euler(fallbackFaceAnchorHeadLocalEulerAngles);
        float outwardSign = fallbackFaceAnchorHeadLocalPosition.z >= 0f ? 1f : -1f;
        return KiwiAdaptiveFaceFitter.Analyze(
            model,
            head,
            animator,
            fallbackRotation,
            outwardSign,
            maximumAdaptiveVertexSamples
        );
    }

    private float CalculateReferenceHeightScale()
    {
        float referenceHeight = CalculateRendererHeight(fallbackModel);
        float targetHeight = CalculateRendererHeight(_activeModel);
        return referenceHeight > 0.0001f && targetHeight > 0.0001f
            ? referenceHeight / targetHeight
            : 1f;
    }

    private static float CalculateRendererHeight(Transform root)
    {
        if (root == null) return 0f;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return 0f;
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        return bounds.size.y;
    }

    private int CalculateEffectiveModelLimitMB()
    {
        if (!KiwiAvatarStorage.IsMobileRuntime) return Mathf.Max(16, maximumRuntimeModelSizeMB);
        int limit = Mathf.Max(16, mobileMaximumRuntimeModelSizeMB);
        if (adaptiveMobileMemoryGuard && SystemInfo.systemMemorySize > 0 && SystemInfo.systemMemorySize <= 3072)
        {
            limit = Mathf.Min(limit, Mathf.Max(16, lowMemoryDeviceModelLimitMB));
        }
        return limit;
    }

    private int ResolveVtuberLayer()
    {
        int layer = LayerMask.NameToLayer(vtuberLayerName);
        return layer >= 0 ? layer : 0;
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        if (root == null) return;
        root.layer = layer;
        Transform transform = root.transform;
        for (int i = 0; i < transform.childCount; i++)
        {
            SetLayerRecursively(transform.GetChild(i).gameObject, layer);
        }
    }

    private static void SetSpringComponentsEnabled(Transform root, bool enabled)
    {
        if (root == null) return;
        VRMSpringBone[] springs = root.GetComponentsInChildren<VRMSpringBone>(true);
        for (int i = 0; i < springs.Length; i++) springs[i].enabled = enabled;
    }

    private static Transform FindChildByName(Transform root, string name)
    {
        if (root == null) return null;
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (string.Equals(all[i].name, name, StringComparison.OrdinalIgnoreCase)) return all[i];
        }
        return null;
    }

    private static Vector3 DivideScale(Vector3 value, Vector3 divisor)
    {
        return new Vector3(
            Mathf.Abs(divisor.x) > 0.000001f ? value.x / divisor.x : 1f,
            Mathf.Abs(divisor.y) > 0.000001f ? value.y / divisor.y : 1f,
            Mathf.Abs(divisor.z) > 0.000001f ? value.z / divisor.z : 1f
        );
    }

    private static Vector3 SanitizeScale(Vector3 value)
    {
        return new Vector3(
            Mathf.Max(0.0001f, Mathf.Abs(value.x)),
            Mathf.Max(0.0001f, Mathf.Abs(value.y)),
            Mathf.Max(0.0001f, Mathf.Abs(value.z))
        );
    }

    private static bool IsFinitePositive(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }
}
