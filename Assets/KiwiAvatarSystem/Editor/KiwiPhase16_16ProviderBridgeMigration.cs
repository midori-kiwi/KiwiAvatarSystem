#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 16.16 Root-space provider normalization.
///
/// Contracts:
/// - tracker/provider data remains authoritative and untouched;
/// - source switches are normalized only in the presentation domain;
/// - the currently rendered Root is the handoff reference, even when the
///   previous canonical sample is older than the provider-handoff age window;
/// - release is driven by fresh accepted provider samples and bounded by
///   Root-space displacement, not render-frame time;
/// - no global low-pass, frame queue, provider threshold change, calibration
///   change, second Root writer, or Eye/Mouth -> Root feedback is introduced.
/// </summary>
[InitializeOnLoad]
public static class KiwiPhase16_16ProviderBridgeMigration
{
    private const string TargetPath = "Assets/Script/KiwiFaceMotion.cs";
    private const string Prerequisite = "KIWI_V5_1_PHASE16_15_NO_FRAME_HOLD_RESUME_ENVELOPE";
    public const string Marker = "KIWI_V5_1_PHASE16_16_ROOT_SPACE_PROVIDER_BRIDGE";
    private static int _retryCount;

    static KiwiPhase16_16ProviderBridgeMigration()
    {
        EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
    }

    [MenuItem("Tools/Kiwi Avatar System/Apply v5.1 Phase 16.16 Root-Space Provider Bridge")]
    private static void ApplyFromMenu()
    {
        if (!TryApplyNow(out string failure))
        {
            Debug.LogError("[KiwiAvatarSystem] Phase 16.16 provider bridge migration failed: " + failure);
        }
    }

    public static bool TryApplyNow(out string failure)
    {
        failure = string.Empty;
        if (!File.Exists(TargetPath))
        {
            failure = "KiwiFaceMotion.cs was not found at " + TargetPath + ".";
            return false;
        }

        string originalDisk = File.ReadAllText(TargetPath);
        string source = NormalizeNewlines(originalDisk);

        if (!source.Contains(Prerequisite))
        {
            failure = "Phase 16.15 Root continuity marker is missing. Refusing to patch an unknown KiwiFaceMotion path.";
            return false;
        }

        if (HasCompletePatch(source)) return true;
        if (source.Contains(Marker))
        {
            failure = "A Phase 16.16 marker exists but the complete provider bridge contract is not present.";
            return false;
        }

        if (!PatchState(ref source, out failure) ||
            !PatchLateUpdate(ref source, out failure) ||
            !PatchBeforeRender(ref source, out failure) ||
            !PatchAdvanceDisplayPose(ref source, out failure) ||
            !PatchResetHook(ref source, out failure) ||
            !PatchMethods(ref source, out failure))
        {
            return false;
        }

        if (!HasCompletePatch(source))
        {
            failure = "Phase 16.16 patch did not satisfy its complete-marker contract.";
            return false;
        }

        WritePreservingFormat(TargetPath, originalDisk, source);
        AssetDatabase.ImportAsset(TargetPath, ImportAssetOptions.ForceUpdate);
        Debug.Log("[KiwiAvatarSystem] Applied v5.1 Phase 16.16 Root-space provider handoff normalization to KiwiFaceMotion.cs.");
        return true;
    }

    private static bool PatchState(ref string source, out string failure)
    {
        failure = string.Empty;
        const string anchor = "    // KIWI_V5_1_PHASE16_15_NO_FRAME_HOLD_RESUME_ENVELOPE\n";
        int anchorIndex = source.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            failure = "Phase 16.15 presentation state anchor was not found.";
            return false;
        }

        const string block =
            "    // KIWI_V5_1_PHASE16_16_ROOT_SPACE_PROVIDER_BRIDGE\n" +
            "    [Header(\"Phase 16.16 Root-Space Provider Bridge\")]\n" +
            "    [Tooltip(\"Normalize an actual provider-generation change to the Root pose already on screen, then release only on fresh accepted provider samples.\")]\n" +
            "    public bool phase16_16EnableRootProviderBridge = true;\n\n" +
            "    [Range(2, 10)] public int phase16_16BridgeReleaseSamples = 5;\n" +
            "    [Range(0.05f, 0.50f)] public float phase16_16BridgeMaxReleasePositionHeights = 0.25f;\n" +
            "    [Range(3f, 30f)] public float phase16_16BridgeMaxReleaseRotationDegrees = 12f;\n" +
            "    [Range(0.02f, 0.20f)] public float phase16_16BridgeMaxReleaseScaleFraction = 0.08f;\n" +
            "    [Range(0.10f, 1.00f)] public float phase16_16BridgeMotionReleasePositionHeights = 0.35f;\n" +
            "    [Range(5f, 45f)] public float phase16_16BridgeMotionReleaseRotationDegrees = 18f;\n" +
            "    [Range(0.05f, 0.40f)] public float phase16_16BridgeMotionReleaseScaleFraction = 0.15f;\n\n" +
            "    private int _phase16_16LastProviderGeneration;\n" +
            "    private KiwiTrackingBackend _phase16_16LastProviderBackend = KiwiTrackingBackend.Unknown;\n" +
            "    private bool _phase16_16ProviderBridgeActive;\n" +
            "    private int _phase16_16BridgeProviderGeneration;\n" +
            "    private KiwiTrackingBackend _phase16_16BridgeBackend = KiwiTrackingBackend.Unknown;\n" +
            "    private int _phase16_16BridgeAcceptedSamples;\n" +
            "    private Vector3 _phase16_16BridgeStartSamplePosition;\n" +
            "    private Quaternion _phase16_16BridgeStartSampleRotation = Quaternion.identity;\n" +
            "    private float _phase16_16BridgeStartSampleScaleFactor = 1f;\n" +
            "    private bool _phase16_16BridgeOffsetsInitialized;\n" +
            "    private Vector3 _phase16_16BridgeReferencePosition;\n" +
            "    private Quaternion _phase16_16BridgeReferenceRotation = Quaternion.identity;\n" +
            "    private float _phase16_16BridgeReferenceScaleFactor = 1f;\n" +
            "    private Vector3 _phase16_16BridgePositionOffset;\n" +
            "    private Quaternion _phase16_16BridgeRotationOffset = Quaternion.identity;\n" +
            "    private float _phase16_16BridgeScaleRatio = 1f;\n" +
            "    private float _phase16_16BridgeWeight;\n" +
            "    private float _phase16_16BridgeTargetWeight;\n" +
            "    private float _phase16_16BridgeReleaseStep;\n" +
            "    private float _phase16_16BridgeMotionProgress;\n\n\n";

        source = source.Insert(anchorIndex, block);
        return true;
    }

    private static bool PatchLateUpdate(ref string source, out string failure)
    {
        failure = string.Empty;
        if (!TryGetMethod(source, "    private void LateUpdate()", out int methodIndex, out int closingBrace, out string method))
        {
            failure = "LateUpdate was not found.";
            return false;
        }

        const string anchor =
            "                HandlePhase16_15AcceptedAuthoritativeFrame(\n" +
            "                    KiwiCommercialRigidMotionPolicy.GetAuthoritativeProviderGeneration(),\n" +
            "                    precisionData.backend);\n";
        if (!method.Contains(anchor))
        {
            failure = "Phase 16.15 accepted-frame anchor was not found in LateUpdate.";
            return false;
        }

        method = method.Replace(anchor, anchor +
            "\n                HandlePhase16_16AcceptedProviderFrame(\n" +
            "                    KiwiCommercialRigidMotionPolicy.GetAuthoritativeProviderGeneration(),\n" +
            "                    precisionData.backend);\n");
        source = source.Substring(0, methodIndex) + method + source.Substring(closingBrace + 1);
        return true;
    }

    private static bool PatchBeforeRender(ref string source, out string failure)
    {
        failure = string.Empty;
        if (!TryGetMethod(source, "    private void OnBeforeRenderPrecision()", out int methodIndex, out int closingBrace, out string method))
        {
            failure = "OnBeforeRenderPrecision was not found.";
            return false;
        }
        const string anchor = "                    phase16_14AcceptedNewRenderSample = true;\n";
        if (!method.Contains(anchor))
        {
            failure = "Phase 16.14 accepted-render-sample anchor was not found.";
            return false;
        }
        method = method.Replace(anchor, anchor +
            "                    HandlePhase16_16AcceptedProviderFrame(\n" +
            "                        KiwiCommercialRigidMotionPolicy.GetAuthoritativeProviderGeneration(),\n" +
            "                        latestData.backend);\n");
        source = source.Substring(0, methodIndex) + method + source.Substring(closingBrace + 1);
        return true;
    }

    private static bool PatchAdvanceDisplayPose(ref string source, out string failure)
    {
        failure = string.Empty;
        if (!TryGetMethod(source, "    private void AdvanceDisplayPose(", out int methodIndex, out int closingBrace, out string method))
        {
            failure = "AdvanceDisplayPose was not found.";
            return false;
        }
        int openingBrace = method.IndexOf('{');
        if (openingBrace < 0)
        {
            failure = "AdvanceDisplayPose opening brace was not found.";
            return false;
        }
        method = method.Insert(openingBrace + 1,
            "\n        ApplyPhase16_16ProviderRootBridge(\n" +
            "            ref targetRotation,\n" +
            "            ref targetPosition,\n" +
            "            ref targetScale);\n");
        source = source.Substring(0, methodIndex) + method + source.Substring(closingBrace + 1);
        return true;
    }

    private static bool PatchResetHook(ref string source, out string failure)
    {
        failure = string.Empty;
        if (!TryGetMethod(source, "    private void ResetPrecisionState()", out int methodIndex, out int closingBrace, out string method))
        {
            failure = "ResetPrecisionState was not found.";
            return false;
        }
        const string anchor = "        ResetPhase16_15RootContinuityState();\n";
        if (!method.Contains(anchor))
        {
            failure = "Phase 16.15 reset hook was not found.";
            return false;
        }
        method = method.Replace(anchor, anchor + "        ResetPhase16_16ProviderBridgeState();\n");
        source = source.Substring(0, methodIndex) + method + source.Substring(closingBrace + 1);
        return true;
    }

    private static bool PatchMethods(ref string source, out string failure)
    {
        failure = string.Empty;
        const string anchor = "    private void BeginPhase16_15NoFrameHold()";
        int anchorIndex = source.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            failure = "Phase 16.15 method insertion anchor was not found.";
            return false;
        }

        const string methods =
            "    private void HandlePhase16_16AcceptedProviderFrame(int providerGeneration, KiwiTrackingBackend backend)\n" +
            "    {\n" +
            "        if (providerGeneration <= 0) return;\n" +
            "        bool providerChanged = _phase16_16LastProviderGeneration > 0 && providerGeneration != _phase16_16LastProviderGeneration;\n" +
            "        if (providerChanged && phase16_16EnableRootProviderBridge)\n" +
            "        {\n" +
            "            BeginPhase16_16ProviderBridge(providerGeneration, backend);\n" +
            "        }\n" +
            "        else if (_phase16_16ProviderBridgeActive && providerGeneration == _phase16_16BridgeProviderGeneration)\n" +
            "        {\n" +
            "            AdvancePhase16_16ProviderBridgeRelease();\n" +
            "        }\n" +
            "        _phase16_16LastProviderGeneration = providerGeneration;\n" +
            "        _phase16_16LastProviderBackend = backend;\n" +
            "        ReportPhase16_16ProviderBridge(false);\n" +
            "    }\n\n" +
            "    private void BeginPhase16_16ProviderBridge(int providerGeneration, KiwiTrackingBackend backend)\n" +
            "    {\n" +
            "        if (kiwiRoot == null || !_displayPoseInitialized)\n" +
            "        {\n" +
            "            ResetPhase16_16ActiveBridgeOnly();\n" +
            "            return;\n" +
            "        }\n" +
            "        Vector3 referencePosition = kiwiRoot.localPosition;\n" +
            "        Quaternion referenceRotation = kiwiRoot.localRotation;\n" +
            "        float referenceScaleFactor = SafeScaleRatio(kiwiRoot.localScale.x, _baseScale.x);\n" +
            "        float incomingScaleFactor = SafeScaleRatio(_sampleScale.x, _baseScale.x);\n" +
            "        if (!IsPhase16_16Finite(referencePosition) || !IsPhase16_16Finite(_samplePosition) || !IsPhase16_16Finite(referenceScaleFactor) || !IsPhase16_16Finite(incomingScaleFactor) || incomingScaleFactor <= 0.0001f)\n" +
            "        {\n" +
            "            ResetPhase16_16ActiveBridgeOnly();\n" +
            "            return;\n" +
            "        }\n" +
            "        _phase16_16ProviderBridgeActive = true;\n" +
            "        _phase16_16BridgeProviderGeneration = providerGeneration;\n" +
            "        _phase16_16BridgeBackend = backend;\n" +
            "        _phase16_16BridgeAcceptedSamples = 0;\n" +
            "        _phase16_16BridgeStartSamplePosition = _samplePosition;\n" +
            "        _phase16_16BridgeStartSampleRotation = _sampleRotation;\n" +
            "        _phase16_16BridgeStartSampleScaleFactor = incomingScaleFactor;\n" +
            "        _phase16_16BridgeOffsetsInitialized = false;\n" +
            "        _phase16_16BridgeReferencePosition = referencePosition;\n" +
            "        _phase16_16BridgeReferenceRotation = referenceRotation;\n" +
            "        _phase16_16BridgeReferenceScaleFactor = referenceScaleFactor;\n" +
            "        _phase16_16BridgePositionOffset = Vector3.zero;\n" +
            "        _phase16_16BridgeRotationOffset = Quaternion.identity;\n" +
            "        _phase16_16BridgeScaleRatio = 1f;\n" +
            "        _phase16_16BridgeWeight = 1f;\n" +
            "        _phase16_16BridgeTargetWeight = 1f;\n" +
            "        _phase16_16BridgeReleaseStep = 0f;\n" +
            "        _phase16_16BridgeMotionProgress = 0f;\n" +
            "        ReportPhase16_16ProviderBridge(true);\n" +
            "    }\n\n" +
            "    private void AdvancePhase16_16ProviderBridgeRelease()\n" +
            "    {\n" +
            "        if (!_phase16_16ProviderBridgeActive || !_phase16_16BridgeOffsetsInitialized) return;\n" +
            "        _phase16_16BridgeAcceptedSamples++;\n" +
            "        float safeHeight = Mathf.Max(_modelHeight, 0.0001f);\n" +
            "        float positionProgress = Vector3.Distance(_samplePosition, _phase16_16BridgeStartSamplePosition) / Mathf.Max(safeHeight * Mathf.Max(0.05f, phase16_16BridgeMotionReleasePositionHeights), 0.0001f);\n" +
            "        float rotationProgress = Quaternion.Angle(_phase16_16BridgeStartSampleRotation, _sampleRotation) / Mathf.Max(1f, phase16_16BridgeMotionReleaseRotationDegrees);\n" +
            "        float currentScaleFactor = SafeScaleRatio(_sampleScale.x, _baseScale.x);\n" +
            "        float scaleProgress = Mathf.Abs(currentScaleFactor - _phase16_16BridgeStartSampleScaleFactor) / Mathf.Max(0.01f, phase16_16BridgeMotionReleaseScaleFraction);\n" +
            "        float sampleProgress = _phase16_16BridgeAcceptedSamples / (float)Mathf.Max(2, phase16_16BridgeReleaseSamples);\n" +
            "        _phase16_16BridgeMotionProgress = Mathf.Clamp01(Mathf.Max(sampleProgress, Mathf.Max(positionProgress, Mathf.Max(rotationProgress, scaleProgress))));\n" +
            "        _phase16_16BridgeTargetWeight = 1f - Phase16_16Smooth01(_phase16_16BridgeMotionProgress);\n" +
            "        _phase16_16BridgeTargetWeight = Mathf.Min(_phase16_16BridgeWeight, _phase16_16BridgeTargetWeight);\n" +
            "        float maximumReleaseWeight = 0.40f;\n" +
            "        float positionOffset = _phase16_16BridgePositionOffset.magnitude;\n" +
            "        if (positionOffset > 0.0001f) maximumReleaseWeight = Mathf.Min(maximumReleaseWeight, safeHeight * Mathf.Max(0.01f, phase16_16BridgeMaxReleasePositionHeights) / positionOffset);\n" +
            "        float rotationOffset = Quaternion.Angle(Quaternion.identity, _phase16_16BridgeRotationOffset);\n" +
            "        if (rotationOffset > 0.001f) maximumReleaseWeight = Mathf.Min(maximumReleaseWeight, Mathf.Max(0.5f, phase16_16BridgeMaxReleaseRotationDegrees) / rotationOffset);\n" +
            "        float scaleOffset = Mathf.Abs(_phase16_16BridgeScaleRatio - 1f);\n" +
            "        if (scaleOffset > 0.0001f) maximumReleaseWeight = Mathf.Min(maximumReleaseWeight, Mathf.Max(0.005f, phase16_16BridgeMaxReleaseScaleFraction) / scaleOffset);\n" +
            "        maximumReleaseWeight = Mathf.Clamp(maximumReleaseWeight, 0.02f, 0.40f);\n" +
            "        float previousWeight = _phase16_16BridgeWeight;\n" +
            "        _phase16_16BridgeWeight = Mathf.MoveTowards(_phase16_16BridgeWeight, _phase16_16BridgeTargetWeight, maximumReleaseWeight);\n" +
            "        _phase16_16BridgeReleaseStep = Mathf.Max(0f, previousWeight - _phase16_16BridgeWeight);\n" +
            "        if (_phase16_16BridgeWeight <= 0.0001f) ResetPhase16_16ActiveBridgeOnly();\n" +
            "    }\n\n" +
            "    private void ApplyPhase16_16ProviderRootBridge(ref Quaternion targetRotation, ref Vector3 targetPosition, ref Vector3 targetScale)\n" +
            "    {\n" +
            "        if (!_phase16_16ProviderBridgeActive || !phase16_16EnableRootProviderBridge || _phase16_16BridgeWeight <= 0.0001f)\n" +
            "        {\n" +
            "            KiwiPhase16_16ProviderBridgeDiagnostics.ReportApplied(0f, 0f, 0f);\n" +
            "            return;\n" +
            "        }\n" +
            "        Vector3 beforePosition = targetPosition;\n" +
            "        Quaternion beforeRotation = targetRotation;\n" +
            "        float beforeScaleFactor = SafeScaleRatio(targetScale.x, _baseScale.x);\n" +
            "        if (!_phase16_16BridgeOffsetsInitialized)\n" +
            "        {\n" +
            "            if (!IsPhase16_16Finite(beforePosition) || !IsPhase16_16Finite(beforeScaleFactor) || beforeScaleFactor <= 0.0001f)\n" +
            "            {\n" +
            "                ResetPhase16_16ActiveBridgeOnly();\n" +
            "                KiwiPhase16_16ProviderBridgeDiagnostics.ReportApplied(0f, 0f, 0f);\n" +
            "                return;\n" +
            "            }\n" +
            "            _phase16_16BridgePositionOffset = _phase16_16BridgeReferencePosition - beforePosition;\n" +
            "            _phase16_16BridgeRotationOffset = _phase16_16BridgeReferenceRotation * Quaternion.Inverse(beforeRotation);\n" +
            "            _phase16_16BridgeScaleRatio = Mathf.Clamp(_phase16_16BridgeReferenceScaleFactor / beforeScaleFactor, 0.50f, 2.00f);\n" +
            "            _phase16_16BridgeOffsetsInitialized = true;\n" +
            "            ReportPhase16_16ProviderBridge(false);\n" +
            "        }\n" +
            "        targetPosition += _phase16_16BridgePositionOffset * _phase16_16BridgeWeight;\n" +
            "        Quaternion appliedRotation = Quaternion.Slerp(Quaternion.identity, _phase16_16BridgeRotationOffset, _phase16_16BridgeWeight);\n" +
            "        targetRotation = appliedRotation * targetRotation;\n" +
            "        float appliedScaleRatio = Mathf.Lerp(1f, _phase16_16BridgeScaleRatio, _phase16_16BridgeWeight);\n" +
            "        float bridgedScaleFactor = beforeScaleFactor * appliedScaleRatio;\n" +
            "        targetScale = _baseScale * bridgedScaleFactor;\n" +
            "        KiwiPhase16_16ProviderBridgeDiagnostics.ReportApplied(Vector3.Distance(beforePosition, targetPosition), Quaternion.Angle(beforeRotation, targetRotation), Mathf.Abs(bridgedScaleFactor - beforeScaleFactor));\n" +
            "    }\n\n" +
            "    private void ReportPhase16_16ProviderBridge(bool started)\n" +
            "    {\n" +
            "        KiwiPhase16_16ProviderBridgeDiagnostics.ReportBridge(_phase16_16ProviderBridgeActive, started, _phase16_16BridgeProviderGeneration, _phase16_16BridgeBackend, _phase16_16BridgeWeight, _phase16_16BridgeTargetWeight, _phase16_16BridgeReleaseStep, _phase16_16BridgeAcceptedSamples, _phase16_16BridgeMotionProgress, _phase16_16BridgePositionOffset.magnitude, Quaternion.Angle(Quaternion.identity, _phase16_16BridgeRotationOffset), _phase16_16BridgeScaleRatio);\n" +
            "    }\n\n" +
            "    private void ResetPhase16_16ActiveBridgeOnly()\n" +
            "    {\n" +
            "        _phase16_16ProviderBridgeActive = false;\n" +
            "        _phase16_16BridgeProviderGeneration = 0;\n" +
            "        _phase16_16BridgeBackend = KiwiTrackingBackend.Unknown;\n" +
            "        _phase16_16BridgeAcceptedSamples = 0;\n" +
            "        _phase16_16BridgeStartSamplePosition = Vector3.zero;\n" +
            "        _phase16_16BridgeStartSampleRotation = Quaternion.identity;\n" +
            "        _phase16_16BridgeStartSampleScaleFactor = 1f;\n" +
            "        _phase16_16BridgeOffsetsInitialized = false;\n" +
            "        _phase16_16BridgeReferencePosition = Vector3.zero;\n" +
            "        _phase16_16BridgeReferenceRotation = Quaternion.identity;\n" +
            "        _phase16_16BridgeReferenceScaleFactor = 1f;\n" +
            "        _phase16_16BridgePositionOffset = Vector3.zero;\n" +
            "        _phase16_16BridgeRotationOffset = Quaternion.identity;\n" +
            "        _phase16_16BridgeScaleRatio = 1f;\n" +
            "        _phase16_16BridgeWeight = 0f;\n" +
            "        _phase16_16BridgeTargetWeight = 0f;\n" +
            "        _phase16_16BridgeReleaseStep = 0f;\n" +
            "        _phase16_16BridgeMotionProgress = 0f;\n" +
            "        ReportPhase16_16ProviderBridge(false);\n" +
            "    }\n\n" +
            "    private void ResetPhase16_16ProviderBridgeState()\n" +
            "    {\n" +
            "        _phase16_16LastProviderGeneration = 0;\n" +
            "        _phase16_16LastProviderBackend = KiwiTrackingBackend.Unknown;\n" +
            "        ResetPhase16_16ActiveBridgeOnly();\n" +
            "    }\n\n" +
            "    private static float Phase16_16Smooth01(float value)\n" +
            "    {\n" +
            "        value = Mathf.Clamp01(value);\n" +
            "        return value * value * (3f - 2f * value);\n" +
            "    }\n\n" +
            "    private static bool IsPhase16_16Finite(float value)\n" +
            "    {\n" +
            "        return !float.IsNaN(value) && !float.IsInfinity(value);\n" +
            "    }\n\n" +
            "    private static bool IsPhase16_16Finite(Vector3 value)\n" +
            "    {\n" +
            "        return IsPhase16_16Finite(value.x) && IsPhase16_16Finite(value.y) && IsPhase16_16Finite(value.z);\n" +
            "    }\n\n\n";

        source = source.Insert(anchorIndex, methods);
        return true;
    }

    private static bool HasCompletePatch(string source)
    {
        return source.Contains(Marker) &&
            source.Contains("HandlePhase16_16AcceptedProviderFrame(") &&
            source.Contains("BeginPhase16_16ProviderBridge(") &&
            source.Contains("AdvancePhase16_16ProviderBridgeRelease()") &&
            source.Contains("ApplyPhase16_16ProviderRootBridge(") &&
            source.Contains("ResetPhase16_16ProviderBridgeState();") &&
            source.Contains("KiwiPhase16_16ProviderBridgeDiagnostics");
    }

    private static bool TryGetMethod(string source, string signature, out int methodIndex, out int closingBrace, out string method)
    {
        methodIndex = source.IndexOf(signature, StringComparison.Ordinal);
        closingBrace = -1;
        method = string.Empty;
        if (methodIndex < 0) return false;
        int openingBrace = source.IndexOf('{', methodIndex + signature.Length);
        closingBrace = FindMatchingBrace(source, openingBrace);
        if (openingBrace < 0 || closingBrace < 0) return false;
        method = source.Substring(methodIndex, closingBrace - methodIndex + 1);
        return true;
    }

    private static int FindMatchingBrace(string source, int openingBrace)
    {
        if (openingBrace < 0 || openingBrace >= source.Length) return -1;
        int depth = 0;
        bool inString = false, inChar = false, inLineComment = false, inBlockComment = false, escape = false;
        for (int i = openingBrace; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (inLineComment) { if (c == '\n') inLineComment = false; continue; }
            if (inBlockComment) { if (c == '*' && next == '/') { inBlockComment = false; i++; } continue; }
            if (inString) { if (escape) { escape = false; continue; } if (c == '\\') { escape = true; continue; } if (c == '"') inString = false; continue; }
            if (inChar) { if (escape) { escape = false; continue; } if (c == '\\') { escape = true; continue; } if (c == '\'') inChar = false; continue; }
            if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
            if (c == '"') { inString = true; continue; }
            if (c == '\'') { inChar = true; continue; }
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    private static string NormalizeNewlines(string source)
    {
        return source.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    private static void WritePreservingFormat(string path, string original, string normalized)
    {
        string output = original.Contains("\r\n") ? normalized.Replace("\n", "\r\n") : normalized;
        File.WriteAllText(path, output);
    }

    private static void ApplyAutomaticallyWhenReady()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
            return;
        }
        if (!File.Exists(TargetPath)) return;
        string source = NormalizeNewlines(File.ReadAllText(TargetPath));
        if (HasCompletePatch(source)) return;
        if (!source.Contains(Prerequisite))
        {
            _retryCount++;
            if (_retryCount <= 24) EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
            else Debug.LogWarning("[KiwiAvatarSystem] Phase 16.16 waited for Phase 16.15 prerequisite marker but it never appeared.");
            return;
        }
        if (!TryApplyNow(out string failure))
        {
            Debug.LogError("[KiwiAvatarSystem] Automatic Phase 16.16 provider bridge migration failed: " + failure);
        }
    }
}
#endif
