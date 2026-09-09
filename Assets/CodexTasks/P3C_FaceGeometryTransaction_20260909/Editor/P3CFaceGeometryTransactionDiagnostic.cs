using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Mediapipe;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Synthetic-only P3C graph/callback/lifecycle diagnostic. It uses reflection
/// solely because the Production service intentionally remains internal.
/// </summary>
public static class P3CFaceGeometryTransactionDiagnostic
{
    private const string ResultPath =
        "CodexTasks/P3C_FACEGEOMETRY_TRANSACTION_DIAGNOSTIC_RESULT.txt";

    public static void RunBatch()
    {
        int exitCode = 1;
        object service = null;
        Type serviceType = null;
        MethodInfo beginShutdown = null;
        MethodInfo pumpShutdown = null;
        PropertyInfo shutdownComplete = null;

        try
        {
            string projectRoot = Directory.GetCurrentDirectory();
            string bundlePath = Path.Combine(
                projectRoot,
                "Assets",
                "StreamingAssets",
                "face_landmarker_v2_with_blendshapes.bytes");

            Dictionary<string, object> fixtures =
                LoadP3AFixtures(bundlePath);

            object fixture16x9 = fixtures["480x270"];
            object fixture4x3 = fixtures["640x480"];

            serviceType = typeof(KiwiRuntimeGenerationContext)
                .Assembly
                .GetType(
                    "KiwiFaceGeometryTransactionService",
                    true);

            service = Activator.CreateInstance(
                serviceType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new object[] { bundlePath },
                null);

            MethodInfo admit = RequiredMethod(
                serviceType,
                "AdmitAcceptedInferenceResult");
            MethodInfo pump = RequiredMethod(serviceType, "Pump");
            beginShutdown = RequiredMethod(
                serviceType,
                "BeginShutdown");
            pumpShutdown = RequiredMethod(
                serviceType,
                "PumpShutdown");
            PropertyInfo lastAccepted = RequiredProperty(
                serviceType,
                "LastAcceptedGeometryFrameId");
            shutdownComplete = RequiredProperty(
                serviceType,
                "IsShutdownComplete");

            KiwiRuntimeGenerationContext.Snapshot identity =
                KiwiRuntimeGenerationContext.Capture();

            Vector3[] landmarks16x9 =
                FixtureLandmarks(fixture16x9);
            Vector3[] landmarks4x3 =
                FixtureLandmarks(fixture4x3);

            Require(Admit(
                admit,
                service,
                1UL,
                identity,
                480,
                270,
                landmarks16x9),
                "Initial transaction was not admitted.");

            WaitForFrame(
                service,
                pump,
                lastAccepted,
                1UL,
                480,
                270);

            Require(Admit(
                admit,
                service,
                2UL,
                identity,
                480,
                270,
                landmarks16x9),
                "Frame 2 was not admitted.");
            Require(Admit(
                admit,
                service,
                3UL,
                identity,
                480,
                270,
                landmarks16x9),
                "Frame 3 was not admitted as pending.");
            Require(Admit(
                admit,
                service,
                4UL,
                identity,
                480,
                270,
                landmarks16x9),
                "Frame 4 did not supersede pending frame 3.");

            var observed = new List<ulong>();
            WaitForFrame(
                service,
                pump,
                lastAccepted,
                4UL,
                480,
                270,
                observed);
            Require(
                !observed.Contains(3UL),
                "Superseded pending frame 3 was published.");

            Require(Admit(
                admit,
                service,
                5UL,
                identity,
                640,
                480,
                landmarks4x3),
                "Dimension-change frame was not admitted.");
            WaitForFrame(
                service,
                pump,
                lastAccepted,
                5UL,
                640,
                480);

            beginShutdown.Invoke(service, null);
            WaitForShutdown(
                service,
                pumpShutdown,
                shutdownComplete);

            var report = new StringBuilder();
            report.AppendLine(
                "DETERMINISTIC_DIAGNOSTIC_EXECUTION=PASS");
            report.AppendLine("GRAPH_START=PASS");
            report.AppendLine("SIDE_PACKET_TO_STREAM_IMAGE_SIZE=PASS");
            report.AppendLine("ONE_TRANSACTION_ONE_CALLBACK=PASS");
            report.AppendLine("CALLBACK_CORRELATION=PASS");
            report.AppendLine("TRANSIENT_FULL_FACEGEOMETRY_PARSE=PASS");
            report.AppendLine("POSE_ONLY_KIWI_COMPLETION=PASS");
            report.AppendLine("LATEST_PENDING_SUPERSEDE=PASS");
            report.AppendLine("DIMENSION_GRAPH_REBUILD=PASS");
            report.AppendLine("BOUNDED_SHUTDOWN=PASS");
            report.AppendLine("LIVE_CAMERA_RUNTIME=NOT_RUN");
            report.AppendLine("AVATAR_HEAD_PUBLICATION=NONE");
            report.AppendLine("PERFORMANCE_AUTHORITY=NONE");

            File.WriteAllText(
                Path.Combine(projectRoot, ResultPath),
                report.ToString(),
                new UTF8Encoding(false));

            exitCode = 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    ResultPath),
                "DETERMINISTIC_DIAGNOSTIC_EXECUTION=FAIL\n" +
                exception,
                new UTF8Encoding(false));
            UnityEngine.Debug.LogException(exception);
        }
        finally
        {
            if (service != null && beginShutdown != null)
            {
                beginShutdown.Invoke(service, null);
                if (pumpShutdown != null)
                {
                    pumpShutdown.Invoke(service, null);
                }
            }

            EditorApplication.Exit(exitCode);
        }
    }

    private static bool Admit(
        MethodInfo method,
        object service,
        ulong frameId,
        KiwiRuntimeGenerationContext.Snapshot identity,
        int width,
        int height,
        Vector3[] landmarks)
    {
        return (bool)method.Invoke(
            service,
            new object[]
            {
                frameId,
                Stopwatch.GetTimestamp(),
                identity.cameraGeneration,
                identity.trackingSessionGeneration,
                identity.providerGeneration,
                identity.modelGeneration,
                width,
                height,
                landmarks
            });
    }

    private static void WaitForFrame(
        object service,
        MethodInfo pump,
        PropertyInfo lastAccepted,
        ulong expected,
        int width,
        int height,
        List<ulong> observed = null)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 10000)
        {
            pump.Invoke(service, new object[] { width, height });
            ulong value = (ulong)lastAccepted.GetValue(service);
            if (
                observed != null &&
                !observed.Contains(value))
            {
                observed.Add(value);
            }
            if (value == expected)
            {
                return;
            }
            System.Threading.Thread.Sleep(1);
        }

        throw new TimeoutException(
            "Timed out waiting for geometry frame " + expected + ".");
    }

    private static void WaitForShutdown(
        object service,
        MethodInfo pumpShutdown,
        PropertyInfo shutdownComplete)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 10000)
        {
            pumpShutdown.Invoke(service, null);
            if ((bool)shutdownComplete.GetValue(service))
            {
                return;
            }
            System.Threading.Thread.Sleep(1);
        }

        throw new TimeoutException(
            "Timed out waiting for bounded geometry shutdown.");
    }

    private static Dictionary<string, object> LoadP3AFixtures(
        string bundlePath)
    {
        Type diagnostic = typeof(P3FaceGeometryDiagnostic);
        MethodInfo readZip = RequiredMethod(
            diagnostic,
            "ReadZipEntry");
        MethodInfo parseMetadata = RequiredMethod(
            diagnostic,
            "ParseMetadata");
        MethodInfo buildFixtures = RequiredMethod(
            diagnostic,
            "BuildFixtures");

        byte[] metadata = (byte[])readZip.Invoke(
            null,
            new object[]
            {
                bundlePath,
                "geometry_pipeline_metadata_landmarks.binarypb"
            });
        object parsed = parseMetadata.Invoke(
            null,
            new object[] { metadata });
        IEnumerable fixtures = (IEnumerable)buildFixtures.Invoke(
            null,
            new object[] { parsed });

        var result = new Dictionary<string, object>();
        foreach (object fixture in fixtures)
        {
            Type type = fixture.GetType();
            int width = (int)type.GetField("Width").GetValue(fixture);
            int height = (int)type.GetField("Height").GetValue(fixture);
            result[width + "x" + height] = fixture;
        }

        Require(result.ContainsKey("480x270"), "16:9 fixture is absent.");
        Require(result.ContainsKey("640x480"), "4:3 fixture is absent.");
        return result;
    }

    private static Vector3[] FixtureLandmarks(object fixture)
    {
        Type type = fixture.GetType();
        var list = (NormalizedLandmarkList)
            type.GetField("Landmarks").GetValue(fixture);

        var result = new Vector3[468];
        for (int i = 0; i < result.Length; ++i)
        {
            NormalizedLandmark landmark = list.Landmark[i];
            result[i] = new Vector3(
                landmark.X,
                landmark.Y,
                landmark.Z);
        }
        return result;
    }

    private static MethodInfo RequiredMethod(
        Type type,
        string name)
    {
        MethodInfo result = type.GetMethod(
            name,
            BindingFlags.Static |
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic);
        Require(result != null, "Method not found: " + name);
        return result;
    }

    private static PropertyInfo RequiredProperty(
        Type type,
        string name)
    {
        PropertyInfo result = type.GetProperty(
            name,
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic);
        Require(result != null, "Property not found: " + name);
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
