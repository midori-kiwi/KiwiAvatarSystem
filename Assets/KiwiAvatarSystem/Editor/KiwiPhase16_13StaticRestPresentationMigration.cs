#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// v5.1 Phase 16.13 targeted KiwiFaceMotion presentation repair.
///
/// The existing sample-level micro jitter guards remain unchanged. This patch
/// adds a final spatial rest latch inside KiwiFaceMotion's existing display-pose
/// owner so a stationary head cannot keep walking between nearly-identical
/// targets. It also prevents the same already-consumed canonical frame from
/// advancing the display resampler a second time at onBeforeRender while the
/// final presentation rest latch is active.
///
/// No new Transform writer, temporal low-pass, pose buffer, tracker, provider
/// authority or Eye/Mouth -> Root feedback is introduced.
/// </summary>
[InitializeOnLoad]
public static class KiwiPhase16_13StaticRestPresentationMigration
{
    private const string TargetPath =
        "Assets/Script/KiwiFaceMotion.cs";

    private const string Prerequisite =
        "KIWI_V5_1_PHASE16_4_PRESENTATION_HOLD_RESAMPLING";

    public const string Marker =
        "KIWI_V5_1_PHASE16_13_STATIC_REST_PRESENTATION";

    private const string BeforeRenderMarker =
        "KIWI_V5_1_PHASE16_13_BEFORE_RENDER_REST_DEDUP";

    static KiwiPhase16_13StaticRestPresentationMigration()
    {
        EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
    }

    [MenuItem(
        "Tools/Kiwi Avatar System/Apply v5.1 Phase 16.13 Static Rest Presentation")]
    private static void ApplyFromMenu()
    {
        if (!TryApplyNow(out string failure))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Phase 16.13 static-rest migration failed: " +
                failure);
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
            failure =
                "Phase 16.4 prerequisite marker is missing. Refusing to patch " +
                "an unknown KiwiFaceMotion presentation path.";
            return false;
        }

        if (HasCompletePatch(source))
        {
            return true;
        }

        if (source.Contains(Marker) || source.Contains(BeforeRenderMarker))
        {
            failure =
                "A Phase 16.13 marker exists but the complete static-rest " +
                "contract is not present. No partial rewrite was performed.";
            return false;
        }

        if (!PatchStateAndDiagnostics(ref source, out failure) ||
            !PatchBeforeRender(ref source, out failure) ||
            !PatchDisplayAdvance(ref source, out failure) ||
            !PatchRestMethods(ref source, out failure) ||
            !PatchResetHook(ref source, out failure))
        {
            return false;
        }

        if (!HasCompletePatch(source))
        {
            failure =
                "Phase 16.13 patch did not satisfy its complete-marker contract.";
            return false;
        }

        WritePreservingFormat(TargetPath, originalDisk, source);
        AssetDatabase.ImportAsset(TargetPath, ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[KiwiAvatarSystem] Applied v5.1 Phase 16.13 static-rest " +
            "presentation repair to KiwiFaceMotion.cs.");

        return true;
    }

    private static bool PatchStateAndDiagnostics(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string anchor =
            "    // =========================================================\n" +
            "    // Motion Accent\n";

        int anchorIndex = source.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            failure = "Motion Accent state anchor was not found.";
            return false;
        }

        const string block =
            "    // KIWI_V5_1_PHASE16_13_STATIC_REST_PRESENTATION\n" +
            "    // Final display-domain spatial latch. This is deliberately not a\n" +
            "    // temporal low-pass: while the target stays inside the existing\n" +
            "    // microscopic corridors the rendered pose is held exactly; real\n" +
            "    // accumulated displacement or raw-speed release exits immediately.\n" +
            "    private bool _phase16_13PresentationRestLocked;\n" +
            "    private float _phase16_13PresentationRestCandidateSeconds;\n" +
            "    private Quaternion _phase16_13PresentationRestRotation = Quaternion.identity;\n" +
            "    private Vector3 _phase16_13PresentationRestPosition;\n" +
            "    private Vector3 _phase16_13PresentationRestScale = Vector3.one;\n" +
            "    private int _phase16_13PresentationRestLockCount;\n" +
            "    private int _phase16_13PresentationRestReleaseCount;\n" +
            "    private int _phase16_13BeforeRenderRestHoldCount;\n" +
            "    private int _phase16_13BeforeRenderNewSampleCount;\n\n" +
            "    public bool Phase16_13StaticRestActive =>\n" +
            "        _phase16_13PresentationRestLocked;\n\n" +
            "    public float Phase16_13StaticRestCandidateSeconds =>\n" +
            "        _phase16_13PresentationRestCandidateSeconds;\n\n" +
            "    public int Phase16_13StaticRestLockCount =>\n" +
            "        _phase16_13PresentationRestLockCount;\n\n" +
            "    public int Phase16_13StaticRestReleaseCount =>\n" +
            "        _phase16_13PresentationRestReleaseCount;\n\n" +
            "    public int Phase16_13BeforeRenderRestHoldCount =>\n" +
            "        _phase16_13BeforeRenderRestHoldCount;\n\n" +
            "    public int Phase16_13BeforeRenderNewSampleCount =>\n" +
            "        _phase16_13BeforeRenderNewSampleCount;\n\n\n";

        source = source.Insert(anchorIndex, block);
        return true;
    }

    private static bool PatchBeforeRender(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string signature =
            "    private void OnBeforeRenderPrecision()";

        int methodIndex = source.IndexOf(signature, StringComparison.Ordinal);
        if (methodIndex < 0)
        {
            failure = "OnBeforeRenderPrecision was not found.";
            return false;
        }

        int openingBrace = source.IndexOf('{', methodIndex + signature.Length);
        int closingBrace = FindMatchingBrace(source, openingBrace);
        if (openingBrace < 0 || closingBrace < 0)
        {
            failure = "OnBeforeRenderPrecision braces could not be resolved.";
            return false;
        }

        string method = source.Substring(
            methodIndex,
            closingBrace - methodIndex + 1);

        const string guard =
            "        if (!useBeforeRenderLateLatch || kiwiRoot == null)\n" +
            "        {\n" +
            "            return;\n" +
            "        }\n";

        if (!method.Contains(guard))
        {
            failure = "onBeforeRender guard anchor was not found.";
            return false;
        }

        method = method.Replace(
            guard,
            guard +
            "\n" +
            "        // " + BeforeRenderMarker + "\n" +
            "        bool phase16_13ObservedNewRenderFrame = false;\n");

        const string newFrameAnchor =
            "                IsNewPrecisionFrame(latestData))\n" +
            "            {\n";

        if (!method.Contains(newFrameAnchor))
        {
            failure = "onBeforeRender new-frame branch was not found.";
            return false;
        }

        method = method.Replace(
            newFrameAnchor,
            newFrameAnchor +
            "                phase16_13ObservedNewRenderFrame = true;\n" +
            "                _phase16_13BeforeRenderNewSampleCount++;\n" +
            "                KiwiPhase16_13PresentationDiagnostics.RecordBeforeRenderNewSample();\n\n");

        const string advanceAnchor =
            "        if (\n" +
            "            enableUltraLowLatencyTracking &&\n" +
            "            _displayPoseInitialized\n" +
            "        )\n";

        if (!method.Contains(advanceAnchor))
        {
            failure = "onBeforeRender display-advance branch was not found.";
            return false;
        }

        string dedup =
            "        // A stationary already-consumed canonical frame must not\n" +
            "        // advance the display resampler again between LateUpdate and\n" +
            "        // render. A genuinely newer render-boundary sample bypasses\n" +
            "        // this hold so motion remains late-latched.\n" +
            "        if (\n" +
            "            _phase16_13PresentationRestLocked &&\n" +
            "            !phase16_13ObservedNewRenderFrame\n" +
            "        )\n" +
            "        {\n" +
            "            _phase16_13BeforeRenderRestHoldCount++;\n" +
            "            KiwiPhase16_13PresentationDiagnostics.RecordBeforeRenderRestHold();\n" +
            "            RenderDisplayPose();\n" +
            "            return;\n" +
            "        }\n\n";

        method = method.Replace(
            advanceAnchor,
            dedup + advanceAnchor);

        source =
            source.Substring(0, methodIndex) +
            method +
            source.Substring(closingBrace + 1);

        return true;
    }

    private static bool PatchDisplayAdvance(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string signature =
            "    private void AdvanceDisplayPose(";

        int methodIndex = source.IndexOf(signature, StringComparison.Ordinal);
        if (methodIndex < 0)
        {
            failure = "AdvanceDisplayPose was not found.";
            return false;
        }

        int openingBrace = source.IndexOf('{', methodIndex);
        int closingBrace = FindMatchingBrace(source, openingBrace);
        if (openingBrace < 0 || closingBrace < 0)
        {
            failure = "AdvanceDisplayPose braces could not be resolved.";
            return false;
        }

        string method = source.Substring(
            methodIndex,
            closingBrace - methodIndex + 1);

        const string dtAnchor =
            "        dt = Mathf.Clamp(dt, 0f, 0.05f);\n";

        if (!method.Contains(dtAnchor))
        {
            failure = "AdvanceDisplayPose dt anchor was not found.";
            return false;
        }

        const string insert =
            "        if (\n" +
            "            TryApplyPhase16_13StaticRestPresentation(\n" +
            "                targetRotation,\n" +
            "                targetPosition,\n" +
            "                targetScale,\n" +
            "                dt)\n" +
            "        )\n" +
            "        {\n" +
            "            return;\n" +
            "        }\n\n";

        method = method.Replace(dtAnchor, insert + dtAnchor);

        source =
            source.Substring(0, methodIndex) +
            method +
            source.Substring(closingBrace + 1);

        return true;
    }

    private static bool PatchRestMethods(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string anchor =
            "    private bool ApplyZeroLagMotionTarget(";

        int anchorIndex = source.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            failure = "ApplyZeroLagMotionTarget anchor was not found.";
            return false;
        }

        const string methods =
            "    private bool TryApplyPhase16_13StaticRestPresentation(\n" +
            "        Quaternion targetRotation,\n" +
            "        Vector3 targetPosition,\n" +
            "        Vector3 targetScale,\n" +
            "        float dt)\n" +
            "    {\n" +
            "        if (\n" +
            "            !enableUltraLowLatencyTracking ||\n" +
            "            !ultraStaticPoseLock ||\n" +
            "            !_displayPoseInitialized ||\n" +
            "            _trackingWasLost\n" +
            "        )\n" +
            "        {\n" +
            "            ResetPhase16_13PresentationRest();\n" +
            "            return false;\n" +
            "        }\n\n" +
            "        float safeHeight = Mathf.Max(_modelHeight, 0.0001f);\n" +
            "        float positionDeadZone =\n" +
            "            KiwiCommercialRigidMotionPolicy.GetAdaptivePositionDeadZone(\n" +
            "                ultraPositionDeadZone,\n" +
            "                _lastPrecisionQuality);\n\n" +
            "        float rotationCandidate = Mathf.Max(0.0001f, ultraRotationDeadZone * 1.50f);\n" +
            "        float positionCandidate = Mathf.Max(0.000001f, positionDeadZone * 1.50f);\n" +
            "        float scaleCandidate = Mathf.Max(0.000001f, ultraScaleDeadZone * 1.50f);\n" +
            "        float rotationRelease = rotationCandidate * 1.50f;\n" +
            "        float positionRelease = positionCandidate * 1.50f;\n" +
            "        float scaleRelease = scaleCandidate * 1.50f;\n\n" +
            "        bool rawRest =\n" +
            "            _rawAngularSpeed <= ultraRotationStaticReleaseSpeed &&\n" +
            "            _rawPositionSpeed <= ultraPositionStaticReleaseSpeed &&\n" +
            "            _rawScaleSpeed <= ultraScaleStaticReleaseSpeed;\n\n" +
            "        if (_phase16_13PresentationRestLocked)\n" +
            "        {\n" +
            "            float rotationError = Quaternion.Angle(\n" +
            "                _phase16_13PresentationRestRotation,\n" +
            "                targetRotation);\n" +
            "            float positionError = Vector3.Distance(\n" +
            "                _phase16_13PresentationRestPosition,\n" +
            "                targetPosition) / safeHeight;\n" +
            "            float restScaleFactor = SafeScaleRatio(\n" +
            "                _phase16_13PresentationRestScale.x,\n" +
            "                _baseScale.x);\n" +
            "            float targetScaleFactor = SafeScaleRatio(\n" +
            "                targetScale.x,\n" +
            "                _baseScale.x);\n" +
            "            float scaleError = Mathf.Abs(restScaleFactor - targetScaleFactor);\n\n" +
            "            if (\n" +
            "                rawRest &&\n" +
            "                rotationError <= rotationRelease &&\n" +
            "                positionError <= positionRelease &&\n" +
            "                scaleError <= scaleRelease\n" +
            "            )\n" +
            "            {\n" +
            "                _displayRotation = _phase16_13PresentationRestRotation;\n" +
            "                _displayPosition = _phase16_13PresentationRestPosition;\n" +
            "                _displayScale = _phase16_13PresentationRestScale;\n" +
            "                return true;\n" +
            "            }\n\n" +
            "            _phase16_13PresentationRestLocked = false;\n" +
            "            _phase16_13PresentationRestCandidateSeconds = 0f;\n" +
            "            _phase16_13PresentationRestReleaseCount++;\n" +
            "            KiwiPhase16_13PresentationDiagnostics.RecordRestRelease();\n" +
            "            return false;\n" +
            "        }\n\n" +
            "        float candidateRotationError = Quaternion.Angle(\n" +
            "            _displayRotation,\n" +
            "            targetRotation);\n" +
            "        float candidatePositionError = Vector3.Distance(\n" +
            "            _displayPosition,\n" +
            "            targetPosition) / safeHeight;\n" +
            "        float displayScaleFactor = SafeScaleRatio(\n" +
            "            _displayScale.x,\n" +
            "            _baseScale.x);\n" +
            "        float candidateTargetScaleFactor = SafeScaleRatio(\n" +
            "            targetScale.x,\n" +
            "            _baseScale.x);\n" +
            "        float candidateScaleError = Mathf.Abs(\n" +
            "            displayScaleFactor - candidateTargetScaleFactor);\n\n" +
            "        bool candidate =\n" +
            "            rawRest &&\n" +
            "            candidateRotationError <= rotationCandidate &&\n" +
            "            candidatePositionError <= positionCandidate &&\n" +
            "            candidateScaleError <= scaleCandidate;\n\n" +
            "        if (!candidate)\n" +
            "        {\n" +
            "            _phase16_13PresentationRestCandidateSeconds = 0f;\n" +
            "            KiwiPhase16_13PresentationDiagnostics.ReportRestState(false, 0f);\n" +
            "            return false;\n" +
            "        }\n\n" +
            "        _phase16_13PresentationRestCandidateSeconds +=\n" +
            "            Mathf.Clamp(dt, 0f, 0.05f);\n" +
            "        KiwiPhase16_13PresentationDiagnostics.ReportRestState(\n" +
            "            false,\n" +
            "            _phase16_13PresentationRestCandidateSeconds);\n\n" +
            "        if (\n" +
            "            _phase16_13PresentationRestCandidateSeconds <\n" +
            "                Mathf.Max(0.04f, ultraStaticLockSeconds)\n" +
            "        )\n" +
            "        {\n" +
            "            return false;\n" +
            "        }\n\n" +
            "        _phase16_13PresentationRestRotation = _displayRotation;\n" +
            "        _phase16_13PresentationRestPosition = _displayPosition;\n" +
            "        _phase16_13PresentationRestScale = _displayScale;\n" +
            "        _phase16_13PresentationRestLocked = true;\n" +
            "        _phase16_13PresentationRestLockCount++;\n" +
            "        KiwiPhase16_13PresentationDiagnostics.ReportRestState(\n" +
            "            true,\n" +
            "            _phase16_13PresentationRestCandidateSeconds);\n" +
            "        KiwiPhase16_13PresentationDiagnostics.RecordRestLock();\n" +
            "        return true;\n" +
            "    }\n\n" +
            "    private void ResetPhase16_13PresentationRest()\n" +
            "    {\n" +
            "        _phase16_13PresentationRestLocked = false;\n" +
            "        _phase16_13PresentationRestCandidateSeconds = 0f;\n" +
            "        _phase16_13PresentationRestRotation = Quaternion.identity;\n" +
            "        _phase16_13PresentationRestPosition = Vector3.zero;\n" +
            "        _phase16_13PresentationRestScale = Vector3.one;\n" +
            "        KiwiPhase16_13PresentationDiagnostics.ReportRestState(false, 0f);\n" +
            "    }\n\n\n";

        source = source.Insert(anchorIndex, methods);
        return true;
    }

    private static bool PatchResetHook(
        ref string source,
        out string failure)
    {
        failure = string.Empty;

        const string signature =
            "    private void ResetUltraStaticLocks()";

        int methodIndex = source.IndexOf(signature, StringComparison.Ordinal);
        if (methodIndex < 0)
        {
            failure = "ResetUltraStaticLocks was not found.";
            return false;
        }

        int openingBrace = source.IndexOf('{', methodIndex + signature.Length);
        if (openingBrace < 0)
        {
            failure = "ResetUltraStaticLocks opening brace was not found.";
            return false;
        }

        source = source.Insert(
            openingBrace + 1,
            "\n        ResetPhase16_13PresentationRest();\n");

        return true;
    }

    private static bool HasCompletePatch(string source)
    {
        return
            source.Contains(Marker) &&
            source.Contains(BeforeRenderMarker) &&
            source.Contains("TryApplyPhase16_13StaticRestPresentation(") &&
            source.Contains("ResetPhase16_13PresentationRest();") &&
            source.Contains("Phase16_13StaticRestActive") &&
            source.Contains("Phase16_13BeforeRenderRestHoldCount");
    }

    private static int FindMatchingBrace(
        string source,
        int openingBrace)
    {
        if (openingBrace < 0 || openingBrace >= source.Length)
        {
            return -1;
        }

        int depth = 0;
        bool inString = false;
        bool inChar = false;
        bool inLineComment = false;
        bool inBlockComment = false;
        bool escape = false;

        for (int i = openingBrace; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }
                if (c == '\\')
                {
                    escape = true;
                    continue;
                }
                if (c == '"') inString = false;
                continue;
            }

            if (inChar)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }
                if (c == '\\')
                {
                    escape = true;
                    continue;
                }
                if (c == '\'') inChar = false;
                continue;
            }

            if (c == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                continue;
            }
            if (c == '\'')
            {
                inChar = true;
                continue;
            }

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }

    private static string NormalizeNewlines(string value)
    {
        return value.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static void WritePreservingFormat(
        string path,
        string originalDisk,
        string normalizedOutput)
    {
        bool hadCrLf = originalDisk.Contains("\r\n");
        string output = hadCrLf
            ? normalizedOutput.Replace("\n", "\r\n")
            : normalizedOutput;
        File.WriteAllText(path, output);
    }

    private static void ApplyAutomaticallyWhenReady()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
            return;
        }

        if (!File.Exists(TargetPath))
        {
            return;
        }

        string source = NormalizeNewlines(File.ReadAllText(TargetPath));
        if (HasCompletePatch(source))
        {
            return;
        }

        if (!source.Contains(Prerequisite))
        {
            EditorApplication.delayCall += ApplyAutomaticallyWhenReady;
            return;
        }

        if (!TryApplyNow(out string failure))
        {
            Debug.LogError(
                "[KiwiAvatarSystem] Automatic Phase 16.13 static-rest " +
                "migration failed: " + failure);
        }
    }
}
#endif
