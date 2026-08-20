#if UNITY_EDITOR
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Idempotent local migration for the existing FaceLandmarkerRunner.
///
/// The ~95 KB Runner is not replaced wholesale. Only the live Inference Engine
/// call site is migrated so all accumulated project-specific fixes remain.
///
/// v2.3:
/// - polls async completion every render Update,
/// - keeps the newest unscheduled camera generation as a one-slot mailbox,
/// - marks a generation consumed only when it is actually scheduled,
/// - publishes the host timestamp belonging to the completed inference frame.
/// </summary>
[InitializeOnLoad]
public static class KiwiAsyncInferenceMailboxInstaller
{
    private const string RunnerPath =
        "Assets/Script/FaceLandmarkerRunner.cs";

    private const string InstallMarker =
        "KIWI_ASYNC_INFERENCE_MAILBOX_V2_3";

    static KiwiAsyncInferenceMailboxInstaller()
    {
        EditorApplication.delayCall +=
            EnsureInstalled;
    }

    [MenuItem(
        "Tools/Kiwi Avatar/Install Async Inference Mailbox")]
    public static void EnsureInstalled()
    {
        if (!File.Exists(RunnerPath))
        {
            Debug.LogWarning(
                "[Kiwi Async Mailbox] Runner not found: " +
                RunnerPath);
            return;
        }

        string text =
            File.ReadAllText(RunnerPath);

        if (text.Contains(InstallMarker))
        {
            return;
        }

        string original = text;

        // -----------------------------------------------------
        // 1. Dedicated newest-camera timestamp for Inference.
        // -----------------------------------------------------
        const string fieldNeedle =
            "private int _lastSentisProcessedGeneration = -1;";

        if (!text.Contains(fieldNeedle))
        {
            Fail("Inference generation field was not found.");
            return;
        }

        text = text.Replace(
            fieldNeedle,
            fieldNeedle +
            "\n        private long _latestSentisSourceFrameHostTicks;" +
            "\n        // " + InstallMarker);

        // -----------------------------------------------------
        // 2. Preserve the newest camera timestamp independently
        //    from MediaPipe's pending-frame mailbox.
        // -----------------------------------------------------
        Regex observeRegex =
            new Regex(
                @"(_pendingSourceFrameHostTicks\s*=\s*hostTicks\s*;\s*)" +
                @"(RecordFreshSourceFrame\s*\(\s*hostTicks\s*\)\s*;)",
                RegexOptions.Multiline);

        if (!observeRegex.IsMatch(text))
        {
            Fail("Fresh-frame timestamp publish was not found.");
            return;
        }

        text = observeRegex.Replace(
            text,
            "$1_latestSentisSourceFrameHostTicks = hostTicks;\n            $2",
            1);

        // -----------------------------------------------------
        // 3. Poll the pending readback even on render frames
        //    where the webcam did not produce a new generation.
        // -----------------------------------------------------
        Regex gateRegex =
            new Regex(
                @"if\s*\(\s*" +
                @"!_acceptTrackingResults\s*\|\|\s*" +
                @"_sentisTracker\s*==\s*null\s*\|\|\s*" +
                @"_sentisSourceTexture\s*==\s*null\s*\|\|\s*" +
                @"_freshWebCamGeneration\s*==\s*_lastSentisProcessedGeneration" +
                @"\s*\)\s*\{\s*return\s*;\s*\}\s*" +
                @"_lastSentisProcessedGeneration\s*=\s*_freshWebCamGeneration\s*;",
                RegexOptions.Multiline);

        if (!gateRegex.IsMatch(text))
        {
            Fail("ProcessSentisFreshFrame gate was not found.");
            return;
        }

        string newGate =
@"if (
                !_acceptTrackingResults ||
                _sentisTracker == null ||
                _sentisSourceTexture == null)
            {
                return;
            }

            bool hasFreshSentisSource =
                _freshWebCamGeneration !=
                _lastSentisProcessedGeneration;";

        text = gateRegex.Replace(
            text,
            newGate,
            1);

        // -----------------------------------------------------
        // 4. Dedicated source timestamp.
        // -----------------------------------------------------
        Regex sourceTicksRegex =
            new Regex(
                @"long\s+sourceHostTicks\s*=\s*" +
                @"_pendingSourceFrameHostTicks\s*>\s*0L\s*" +
                @"\?\s*_pendingSourceFrameHostTicks\s*" +
                @":\s*System\.Diagnostics\.Stopwatch\.GetTimestamp\(\)\s*;",
                RegexOptions.Multiline);

        if (!sourceTicksRegex.IsMatch(text))
        {
            Fail("Inference source timestamp was not found.");
            return;
        }

        text = sourceTicksRegex.Replace(
            text,
@"long sourceHostTicks =
                _latestSentisSourceFrameHostTicks > 0L
                    ? _latestSentisSourceFrameHostTicks
                    : System.Diagnostics.Stopwatch.GetTimestamp();",
            1);

        // -----------------------------------------------------
        // 5. Replace only the synchronous if(...) call opening.
        // -----------------------------------------------------
        Regex processIfRegex =
            new Regex(
                @"if\s*\(\s*" +
                @"_sentisTracker\.TryProcess\s*\(\s*" +
                @"_sentisSourceTexture\s*,\s*" +
                @"_sentisFlipHorizontally\s*,\s*" +
                @"_sentisFlipVertically\s*,\s*" +
                @"out\s+Vector3\[\]\s+landmarks\s*,\s*" +
                @"out\s+Quaternion\s+geometricRotation\s*" +
                @"\)\s*\)\s*\{",
                RegexOptions.Multiline);

        if (!processIfRegex.IsMatch(text))
        {
            Fail("Synchronous Inference call was not found.");
            return;
        }

        string asyncOpening =
@"bool hasCompletedSentisResult =
                _sentisTracker.TryProcessAsync(
                    _sentisSourceTexture,
                    _sentisFlipHorizontally,
                    _sentisFlipVertically,
                    sourceHostTicks,
                    hasFreshSentisSource,
                    out bool scheduledSentisSource,
                    out Vector3[] landmarks,
                    out Quaternion geometricRotation,
                    out long completedSourceHostTicks);

            if (scheduledSentisSource)
            {
                _lastSentisProcessedGeneration =
                    _freshWebCamGeneration;
            }

            if (hasCompletedSentisResult)
            {";

        text = processIfRegex.Replace(
            text,
            asyncOpening,
            1);

        // -----------------------------------------------------
        // 6. Pending async readback is not tracking loss.
        // -----------------------------------------------------
        Regex elseIfRegex =
            new Regex(
                @"else\s+if\s*\(\s*!_sentisTracker\.IsTracking\s*\)",
                RegexOptions.Multiline);

        if (!elseIfRegex.IsMatch(text))
        {
            Fail("Inference fallback branch was not found.");
            return;
        }

        text = elseIfRegex.Replace(
            text,
@"else if (
                !_sentisTracker.IsAsyncReadbackPending &&
                !_sentisTracker.IsTracking)",
            1);

        // -----------------------------------------------------
        // 7. Publish the timestamp of the completed source frame.
        // -----------------------------------------------------
        int asyncCallIndex =
            text.IndexOf(
                "bool hasCompletedSentisResult =",
                System.StringComparison.Ordinal);

        int storeIndex =
            text.IndexOf(
                "_sentisPrimaryActive = StoreSentisTrackingData(",
                asyncCallIndex,
                System.StringComparison.Ordinal);

        if (storeIndex < 0)
        {
            Fail("StoreSentisTrackingData call was not found.");
            return;
        }

        int storeEnd =
            text.IndexOf(
                ");",
                storeIndex,
                System.StringComparison.Ordinal);

        if (storeEnd < 0)
        {
            Fail("StoreSentisTrackingData call end was not found.");
            return;
        }

        string storeBlock =
            text.Substring(
                storeIndex,
                storeEnd - storeIndex + 2);

        Regex storeTimingRegex =
            new Regex(
                @"GetCurrentTimestampMillisec\(\)\s*,\s*" +
                @"sourceHostTicks\s*,\s*" +
                @"System\.Diagnostics\.Stopwatch\.GetTimestamp\(\)");

        if (!storeTimingRegex.IsMatch(storeBlock))
        {
            Fail("StoreSentisTrackingData timing arguments were not found.");
            return;
        }

        storeBlock = storeTimingRegex.Replace(
            storeBlock,
@"GetCurrentTimestampMillisec(),
                    completedSourceHostTicks > 0L
                        ? completedSourceHostTicks
                        : sourceHostTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp()",
            1);

        text =
            text.Substring(0, storeIndex) +
            storeBlock +
            text.Substring(storeEnd + 2);

        if (text == original)
        {
            Fail("No source changes were produced.");
            return;
        }

        WriteUtf8PreserveBom(
            RunnerPath,
            text);

        AssetDatabase.ImportAsset(
            RunnerPath,
            ImportAssetOptions.ForceUpdate);

        Debug.Log(
            "[Kiwi Async Mailbox] Installed v2.3 timestamp-preserving " +
            "Inference Engine async readback mailbox.");
    }

    private static void WriteUtf8PreserveBom(
        string path,
        string text)
    {
        byte[] bytes =
            File.ReadAllBytes(path);

        bool hasBom =
            bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF;

        UTF8Encoding encoding =
            new UTF8Encoding(hasBom);

        File.WriteAllText(
            path,
            text,
            encoding);
    }

    private static void Fail(
        string message)
    {
        Debug.LogError(
            "[Kiwi Async Mailbox] " +
            message +
            " Runner was left unchanged.");
    }
}
#endif
