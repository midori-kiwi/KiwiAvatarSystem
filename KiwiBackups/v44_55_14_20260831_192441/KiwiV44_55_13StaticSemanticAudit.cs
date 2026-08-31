using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

internal static class KiwiV44_55_13StaticSemanticAudit
{
    private const string Contract =
        "KIWI_V44_55_13_STATIC_SEMANTIC_SOURCE_AUDIT";

    public static void Run()
    {
        var lines = new List<string>();
        int pass = 0;
        int fail = 0;

        lines.Add("contract=" + Contract);
        lines.Add("unityVersion=" + Application.unityVersion);
        lines.Add("mode=EDITOR_BATCHMODE_STATIC_ONLY");

        // FIX1: validate canonical Production paths directly. Same-name files
        // elsewhere under Assets are not Production authority and must not
        // cause false static failures.
        string tracker = FindCanonical(
            "Assets/Script/KiwiInferenceFaceTracker.cs",
            "KiwiInferenceFaceTracker",
            lines,
            ref pass,
            ref fail);

        string motion = FindCanonical(
            "Assets/Script/KiwiFaceMotion.cs",
            "KiwiFaceMotion",
            lines,
            ref pass,
            ref fail);

        string runner = FindCanonical(
            "Assets/Script/FaceLandmarkerRunner.cs",
            "FaceLandmarkerRunner",
            lines,
            ref pass,
            ref fail);

        string observer = FindCanonical(
            "Assets/KiwiAvatarSystem/Runtime/Validation/KiwiStaticFirstCpuSemanticABV44_55_13.cs",
            "KiwiStaticFirstCpuSemanticABV44_55_13",
            lines,
            ref pass,
            ref fail);

        if (tracker != null)
        {
            string s = File.ReadAllText(tracker);
            Check("trackerGpuBackend", s.Contains("BackendType.GPUCompute"), lines, ref pass, ref fail);
            Check("trackerLandmarkOutput", s.Contains("conv2d_20"), lines, ref pass, ref fail);
            Check("trackerPresenceOutput", s.Contains("conv2d_30"), lines, ref pass, ref fail);
            Check("trackerNchw", s.Contains("TensorLayout.NCHW"), lines, ref pass, ref fail);
            Check("trackerTopLeft", s.Contains("CoordOrigin.TopLeft"), lines, ref pass, ref fail);
            Check("trackerAsyncComputePath", s.Contains("ScheduleWorker") || s.Contains("worker.Schedule"), lines, ref pass, ref fail);
            Check("trackerNoBlockingReadback", !s.Contains("CompleteAllPendingOperations"), lines, ref pass, ref fail);
        }

        if (observer != null)
        {
            string s = File.ReadAllText(observer);
            Check("observerCpuWorker", s.Contains("BackendType.CPU"), lines, ref pass, ref fail);
            Check("observerGpuWorker", s.Contains("BackendType.GPUCompute"), lines, ref pass, ref fail);
            Check("observerSameModel", s.Contains("KiwiFaceLandmarkInference"), lines, ref pass, ref fail);
            Check("observerPackedOutputs", s.Contains("conv2d_20") && s.Contains("conv2d_30"), lines, ref pass, ref fail);
            Check("observerIdentityCorrection", s.Contains("PrepareIdentityCorrectedInput"), lines, ref pass, ref fail);
            Check("observerSemanticGeometry", s.Contains("CompareSemanticGeometry"), lines, ref pass, ref fail);
            Check("observerNoSetValue", !s.Contains(".SetValue("), lines, ref pass, ref fail);
            Check("observerNoUpdateExternalTexture", !s.Contains("UpdateExternalTexture"), lines, ref pass, ref fail);
            Check("observerNoBlockingClone", !s.Contains(".ReadbackAndClone()"), lines, ref pass, ref fail);
            Check("observerNoWaitForCompletion", !s.Contains("WaitForCompletion"), lines, ref pass, ref fail);
            Check("observerNoCompleteAllPending", !s.Contains("CompleteAllPendingOperations"), lines, ref pass, ref fail);
            Check("observerGpuAuthorityLog", s.Contains("productionGpuAuthority=1") && s.Contains("cpuAuthority=0"), lines, ref pass, ref fail);
            Check("observerMinimalRuntime", s.Contains("DefaultDurationSeconds = 15f") && s.Contains("DefaultSampleHz = 0.5f"), lines, ref pass, ref fail);
            Check(
                "observerNoLiteralEscapedNewlineInjection",
                !s.Contains(@"\n        ") &&
                !s.Contains(@"\n    {") &&
                !s.Contains(@");\n"),
                lines,
                ref pass,
                ref fail);
        }

        Check("motionPresent", motion != null, lines, ref pass, ref fail);
        Check("runnerPresent", runner != null, lines, ref pass, ref fail);

        lines.Add("pass=" + pass.ToString(CultureInfo.InvariantCulture));
        lines.Add("fail=" + fail.ToString(CultureInfo.InvariantCulture));
        lines.Add("status=" + (fail == 0 ? "PASS" : "FAIL"));

        string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "KiwiValidation");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "KiwiV44_55_13_StaticSemantic.txt");
        File.WriteAllLines(path, lines);

        Debug.Log("[Kiwi v44.55.13 Static] " + (fail == 0 ? "PASS" : "FAIL") + " " + pass + "/" + (pass + fail) + " report=" + path);
        EditorApplication.Exit(fail == 0 ? 0 : 1);
    }

    private static string FindCanonical(
        string projectRelativePath,
        string requiredMarker,
        List<string> lines,
        ref int pass,
        ref int fail)
    {
        string projectRoot =
            Directory.GetParent(Application.dataPath).FullName;

        string normalizedRelative =
            projectRelativePath.Replace('/', Path.DirectorySeparatorChar);

        string path =
            Path.Combine(projectRoot, normalizedRelative);

        bool exists =
            File.Exists(path);

        Check(
            "canonical_" + Path.GetFileName(projectRelativePath),
            exists,
            lines,
            ref pass,
            ref fail);

        if (!exists)
        {
            lines.Add("missingPath=" + projectRelativePath);
            return null;
        }

        string source =
            File.ReadAllText(path);

        bool markerOk =
            source.Contains(requiredMarker);

        Check(
            "marker_" + Path.GetFileName(projectRelativePath),
            markerOk,
            lines,
            ref pass,
            ref fail);

        lines.Add(
            "path_" + Path.GetFileName(projectRelativePath) +
            "=" + path);

        lines.Add(
            "sha256_" + Path.GetFileName(projectRelativePath) +
            "=" + Sha256(path));

        return markerOk ? path : null;
    }

    private static void Check(string name, bool ok, List<string> lines, ref int pass, ref int fail)
    {
        lines.Add((ok ? "PASS " : "FAIL ") + name);
        if (ok) pass++; else fail++;
    }

    private static string Sha256(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
        {
            byte[] hash = sha.ComputeHash(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
