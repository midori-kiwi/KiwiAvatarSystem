#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class KiwiSnapshotCopyTestV44_55_31
{
    private static ComputeBuffer input, output;
    private static CommandBuffer cb;
    private static uint[] expected;
    private static volatile bool resolved;
    private static bool passed;
    private static string error;
    private static double deadline;
    public static void Run()
    {
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 ||
            !SystemInfo.supportsAsyncCompute || !SystemInfo.supportsAsyncGPUReadback)
            throw new Exception("DX12 async/readback required");
        var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/KiwiAvatarSystem/Resources/KiwiProducerTailSnapshotV44_55_31.compute");
        int kernel = shader.FindKernel("CopyPackedOutput");
        expected = new uint[2048];
        uint[] special = { 0, 0x80000000, 0x7F800000, 0xFF800000, 0x7F800001, 0x7FC12345, 0xFFFFFFFF, 1 };
        for (int i = 0; i < expected.Length; i++)
            expected[i] = i < special.Length ? special[i] : unchecked((uint)i * 2654435761u);
        input = new ComputeBuffer(2048, 4); input.SetData(expected);
        uint[] sentinel = new uint[1417];
        for (int i = 0; i < sentinel.Length; i++) sentinel[i] = 0xAABBCCDD;
        output = new ComputeBuffer(1417, 4); output.SetData(sentinel);
        cb = new CommandBuffer { name = "v31 copy kernel unit test (NO INFERENCE)" };
        cb.SetExecutionFlags(CommandBufferExecutionFlags.AsyncCompute);
        cb.SetComputeBufferParam(shader, kernel, "_ProductionOutput", input);
        cb.SetComputeBufferParam(shader, kernel, "_ObserverSnapshot", output);
        cb.SetComputeIntParam(shader, "_ObserverToken", 123);
        cb.SetComputeIntParam(shader, "_Lane", 2);
        cb.SetComputeIntParam(shader, "_SourceLow", 456);
        cb.SetComputeIntParam(shader, "_SourceHigh", 789);
        cb.DispatchCompute(shader, kernel, 22, 1, 1);
        Graphics.ExecuteCommandBufferAsync(cb, ComputeQueueType.Default);
        deadline = EditorApplication.timeSinceStartup + 30;
        EditorApplication.update += Poll;
        AsyncGPUReadback.Request(output, 1417 * 4, 0, request =>
        {
            try
            {
                if (!request.done || request.hasError) throw new Exception("Copy test readback failure");
                var data = request.GetData<uint>();
                uint[] header = { 0x4B333150, 31, 123, 2, 1405, 456, 789, 123 ^ 0xA55A31 };
                if (data.Length != 1417) throw new Exception("Copy test size");
                for (int i = 0; i < 8; i++) if (data[i] != header[i]) throw new Exception("Header " + i);
                for (int i = 0; i < 1405; i++) if (data[8+i] != expected[i]) throw new Exception("Payload bits " + i);
                for (int i = 1413; i < 1417; i++) if (data[i] != 0xAABBCCDD) throw new Exception("Out-of-range write");
                passed = true;
            }
            catch (Exception e) { error = e.Message; }
            finally { resolved = request.done; }
        });
    }
    private static void Poll()
    {
        if (!resolved && EditorApplication.timeSinceStartup < deadline) return;
        EditorApplication.update -= Poll;
        if (resolved) { input.Release(); output.Release(); cb.Release(); }
        // If unresolved at timeout, retain all resources until process teardown. No blocking drain.
        if (passed) Debug.Log("[KiwiV31CopyTest] PASS logical=1405 header=8 tailCanary=4 specialFloatBits=preserved inferenceCount=0");
        else Debug.LogError("[KiwiV31CopyTest] FAIL " + error);
        EditorApplication.Exit(passed ? 0 : 30);
    }
}
#endif
