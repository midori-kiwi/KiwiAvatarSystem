using System;
using UnityEngine;

public enum KiwiAvatarModelFitMode
{
    AdaptiveHead = 0,
    ReferenceHeight = 1,
    Custom = 2,
}

public enum KiwiAvatarFaceFitMode
{
    AdaptiveHead = 0,
    LegacyReference = 1,
    Custom = 2,
}

[Serializable]
public sealed class KiwiAvatarProfile
{
    public const int CurrentVersion = 7;
    public const int AdaptiveGeometryCacheVersion = 3;

    public int version = CurrentVersion;
    public string modelFileName = string.Empty;
    public string displayName = string.Empty;

    [Header("Model Fit")]
    public KiwiAvatarModelFitMode modelFitMode = KiwiAvatarModelFitMode.AdaptiveHead;
    [Range(0.1f, 5f)]
    public float autoFitMultiplier = 1.0f;

    [Tooltip("Automatic fit後に加えるユーザー位置オフセット。Custom時は実位置。")]
    public Vector3 modelLocalPosition = Vector3.zero;
    public Vector3 modelLocalEulerAngles = Vector3.zero;

    [Tooltip("Automatic fitの基準Scale。Custom時は最終Scale。")]
    public Vector3 modelLocalScale = Vector3.one;

    [Header("Face Fit")]
    public KiwiAvatarFaceFitMode faceFitMode = KiwiAvatarFaceFitMode.AdaptiveHead;

    [Tooltip("Adaptive Headで頭部幅/高さから決めた顔サイズへの追加倍率。")]
    [Range(0.25f, 4f)]
    public float adaptiveFaceScale = 1.0f;

    [Tooltip("Adaptive Headの追加位置補正。x=頭幅、y=頭高、z=頭奥行きに対する比率。")]
    public Vector3 adaptiveFaceOffsetNormalized = Vector3.zero;

    public Vector3 faceAnchorLocalPosition = Vector3.zero;
    public Vector3 faceAnchorLocalEulerAngles = Vector3.zero;
    public Vector3 faceAnchorLocalScale = Vector3.one;

    [Header("Spring Bone")]
    public bool springBoneEnabled = true;

    [Header("Adaptive Geometry Cache")]
    [Tooltip("モデルファイルが変わっていなければ頭部BakeMesh解析を再利用します。")]
    public bool adaptiveGeometryCacheValid = false;
    public int adaptiveGeometryCacheVersion = 0;
    public long adaptiveGeometryFileSize = 0;
    public long adaptiveGeometryLastWriteUtcTicks = 0;
    public KiwiHeadGeometry adaptiveGeometryCache;

    // v3.x compatibility fields. New code uses the enum modes above.
    [HideInInspector] public bool autoFitHeight = true;
    [HideInInspector] public bool useCustomFaceAnchor = false;

    public static KiwiAvatarProfile CreateDefault(
        string fileName,
        string defaultDisplayName)
    {
        return new KiwiAvatarProfile
        {
            version = CurrentVersion,
            modelFileName = fileName ?? string.Empty,
            displayName = string.IsNullOrWhiteSpace(defaultDisplayName)
                ? System.IO.Path.GetFileNameWithoutExtension(fileName)
                : defaultDisplayName,
            modelFitMode = KiwiAvatarModelFitMode.AdaptiveHead,
            autoFitMultiplier = 1.0f,
            modelLocalPosition = Vector3.zero,
            modelLocalEulerAngles = Vector3.zero,
            modelLocalScale = Vector3.one,
            faceFitMode = KiwiAvatarFaceFitMode.AdaptiveHead,
            adaptiveFaceScale = 1.0f,
            adaptiveFaceOffsetNormalized = Vector3.zero,
            faceAnchorLocalPosition = Vector3.zero,
            faceAnchorLocalEulerAngles = Vector3.zero,
            faceAnchorLocalScale = Vector3.one,
            springBoneEnabled = true,
            adaptiveGeometryCacheValid = false,
            adaptiveGeometryCacheVersion = 0,
            adaptiveGeometryFileSize = 0,
            adaptiveGeometryLastWriteUtcTicks = 0,
            adaptiveGeometryCache = default,
            autoFitHeight = true,
            useCustomFaceAnchor = false,
        };
    }

    public bool MigrateIfNeeded()
    {
        bool changed = version != CurrentVersion;

        if (version < 2 && autoFitMultiplier <= 0.0001f)
        {
            autoFitMultiplier = 1.0f;
            changed = true;
        }

        // Preserve the exact v3.x appearance. New imports use AdaptiveHead,
        // while existing profiles remain on their previous fit behavior.
        if (version < 4)
        {
            modelFitMode = autoFitHeight
                ? KiwiAvatarModelFitMode.ReferenceHeight
                : KiwiAvatarModelFitMode.Custom;

            faceFitMode = useCustomFaceAnchor
                ? KiwiAvatarFaceFitMode.Custom
                : KiwiAvatarFaceFitMode.LegacyReference;

            if (adaptiveFaceScale <= 0.0001f)
            {
                adaptiveFaceScale = 1.0f;
            }

            changed = true;
        }

        // v5 adds a model-content-aware head geometry cache. Older profiles
        // must analyze once before they can safely populate it.
        if (version < 5)
        {
            InvalidateAdaptiveGeometryCache();
            changed = true;
        }

        // v6 extends the cached head geometry with semantic eye center/span.
        // Re-analyze once so automatic eye/mouth FaceAnchor placement can use it.
        if (version < 6)
        {
            InvalidateAdaptiveGeometryCache();
            changed = true;
        }

        // v7 distinguishes true Humanoid eye bones from an eye semantic reference.
        // Target caches rebuild once so models with eye bones can map against the
        // embedded Kiwi's visual LeftEye3D/RightEye3D reference even though the
        // embedded VRM itself has no Humanoid eye bones.
        if (version < 7)
        {
            InvalidateAdaptiveGeometryCache();
            changed = true;
        }

        float clampedAutoFit = Mathf.Clamp(autoFitMultiplier, 0.1f, 5f);
        if (!Mathf.Approximately(clampedAutoFit, autoFitMultiplier))
        {
            autoFitMultiplier = clampedAutoFit;
            changed = true;
        }

        float clampedFaceScale = Mathf.Clamp(adaptiveFaceScale, 0.25f, 4f);
        if (!Mathf.Approximately(clampedFaceScale, adaptiveFaceScale))
        {
            adaptiveFaceScale = clampedFaceScale;
            changed = true;
        }

        if (modelLocalScale.sqrMagnitude <= 0.000001f)
        {
            modelLocalScale = Vector3.one;
            changed = true;
        }

        if (faceAnchorLocalScale.sqrMagnitude <= 0.000001f)
        {
            faceAnchorLocalScale = Vector3.one;
            changed = true;
        }

        bool expectedAutoFit = modelFitMode != KiwiAvatarModelFitMode.Custom;
        if (autoFitHeight != expectedAutoFit)
        {
            autoFitHeight = expectedAutoFit;
            changed = true;
        }

        bool expectedCustomFace = faceFitMode == KiwiAvatarFaceFitMode.Custom;
        if (useCustomFaceAnchor != expectedCustomFace)
        {
            useCustomFaceAnchor = expectedCustomFace;
            changed = true;
        }

        if (adaptiveGeometryCacheValid)
        {
            bool cacheLooksUsable =
                adaptiveGeometryCacheVersion == AdaptiveGeometryCacheVersion &&
                adaptiveGeometryFileSize > 0 &&
                adaptiveGeometryLastWriteUtcTicks > 0 &&
                adaptiveGeometryCache.valid;

            if (!cacheLooksUsable)
            {
                InvalidateAdaptiveGeometryCache();
                changed = true;
            }
        }

        version = CurrentVersion;
        return changed;
    }

    public void InvalidateAdaptiveGeometryCache()
    {
        adaptiveGeometryCacheValid = false;
        adaptiveGeometryCacheVersion = 0;
        adaptiveGeometryFileSize = 0;
        adaptiveGeometryLastWriteUtcTicks = 0;
        adaptiveGeometryCache = default;
    }
}
