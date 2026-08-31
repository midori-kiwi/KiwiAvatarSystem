using System;
using System.Runtime.InteropServices;

internal static class KiwiMeshOptimizerInterop
{
    private const string LibraryName = "KiwiMeshOptimizerBridge";

    internal const uint SimplifyRegularizeLight = 1u << 6;
    internal const byte SimplifyVertexPriority = 1 << 2;

    [DllImport(
        LibraryName,
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "KiwiMeshOptimizer_SimplifyWithAttributes")]
    private static extern int SimplifyWithAttributesNative(
        [In] int[] sourceIndices,
        UIntPtr indexCount,
        [In] float[] positionsXYZ,
        UIntPtr vertexCount,
        [In] float[] attributes,
        UIntPtr attributeCount,
        [In] float[] attributeWeights,
        [In] byte[] vertexFlags,
        UIntPtr targetIndexCount,
        float targetError,
        uint options,
        [Out] int[] destinationIndices,
        UIntPtr destinationCapacity,
        out UIntPtr outputIndexCount,
        out float outputError);

    internal static bool TrySimplifyWithAttributes(
        int[] sourceIndices,
        float[] positionsXYZ,
        int vertexCount,
        float[] attributes,
        int attributeCount,
        float[] attributeWeights,
        byte[] vertexFlags,
        int targetIndexCount,
        float targetError,
        uint options,
        int[] destinationIndices,
        out int outputIndexCount,
        out float outputError,
        out string error)
    {
        outputIndexCount = 0;
        outputError = 0f;
        error = string.Empty;

        if (
            sourceIndices == null ||
            positionsXYZ == null ||
            attributes == null ||
            attributeWeights == null ||
            destinationIndices == null ||
            vertexCount <= 0 ||
            attributeCount <= 0 ||
            attributeCount > 32 ||
            positionsXYZ.Length != vertexCount * 3 ||
            attributes.Length != vertexCount * attributeCount ||
            attributeWeights.Length != attributeCount ||
            targetIndexCount < 3 ||
            targetIndexCount > sourceIndices.Length ||
            destinationIndices.Length < sourceIndices.Length)
        {
            error = "Invalid meshoptimizer bridge arguments.";
            return false;
        }

        if (
            vertexFlags != null &&
            vertexFlags.Length != vertexCount)
        {
            error = "Vertex flag count does not match vertex count.";
            return false;
        }

        try
        {
            int code =
                SimplifyWithAttributesNative(
                    sourceIndices,
                    (UIntPtr)(ulong)sourceIndices.Length,
                    positionsXYZ,
                    (UIntPtr)(ulong)vertexCount,
                    attributes,
                    (UIntPtr)(ulong)attributeCount,
                    attributeWeights,
                    vertexFlags,
                    (UIntPtr)(ulong)targetIndexCount,
                    targetError,
                    options,
                    destinationIndices,
                    (UIntPtr)(ulong)destinationIndices.Length,
                    out UIntPtr nativeCount,
                    out outputError);

            ulong count64 =
                nativeCount.ToUInt64();

            if (
                code != 0 ||
                count64 > int.MaxValue)
            {
                error =
                    "Native simplifier failed. code=" +
                    code +
                    " count=" +
                    count64;
                return false;
            }

            outputIndexCount =
                (int)count64;

            if (
                outputIndexCount < 3 ||
                outputIndexCount % 3 != 0 ||
                outputIndexCount > destinationIndices.Length)
            {
                error =
                    "Native simplifier returned invalid index count: " +
                    outputIndexCount;
                return false;
            }

            return true;
        }
        catch (DllNotFoundException ex)
        {
            error =
                "KiwiMeshOptimizerBridge.dll was not found: " +
                ex.Message;
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            error =
                "KiwiMeshOptimizerBridge export mismatch: " +
                ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error =
                ex.GetType().Name +
                ": " +
                ex.Message;
            return false;
        }
    }
}
