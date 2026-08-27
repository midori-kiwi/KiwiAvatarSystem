using System;
using UnityEngine;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    /// <summary>
    /// Observer-only Phase16.20.10 inference/readback boundary diagnostics.
    ///
    /// This class intentionally does not claim to measure pure GPU inference time.
    /// Unity Inference Engine's public non-blocking tensor readback API exposes the
    /// request and completion-observation boundaries, but not a separate GPU model
    /// completion timestamp and DMA-completion timestamp. Therefore
    /// RequestToDoneObservedMs includes preceding GPU inference, output readback,
    /// and the frame/poll quantization before completion is observed by C#.
    ///
    /// No WaitForCompletion, no synchronous readback, no queue ownership, no
    /// tracking authority, no filtering and no presentation writes live here.
    /// </summary>
    public static class KiwiInferenceReadbackBoundaryDiagnostics
    {
        public const string ContractMarker =
            "KIWI_V5_1_PHASE16_20_10_V22_INFERENCE_READBACK_BOUNDARY_PROFILING";

        public static float PreReadbackSubmitCpuMs { get; private set; }
        public static float ReadbackRequestCpuMs { get; private set; }
        public static float RequestToDoneObservedMs { get; private set; }
        public static int ReadbackObservedFrameDelta { get; private set; }
        public static float PollIntervalMs { get; private set; }
        public static float ReadbackCloneCpuMs { get; private set; }
        public static float DecodeMathCpuMs { get; private set; }
        public static ulong BoundarySampleCount { get; private set; }

        private static long _lastPollHostTicks;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ResetOnLoad()
        {
            PreReadbackSubmitCpuMs = 0f;
            ReadbackRequestCpuMs = 0f;
            RequestToDoneObservedMs = 0f;
            ReadbackObservedFrameDelta = 0;
            PollIntervalMs = 0f;
            ReadbackCloneCpuMs = 0f;
            DecodeMathCpuMs = 0f;
            BoundarySampleCount = 0UL;
            _lastPollHostTicks = 0L;
        }

        public static void RecordPreReadbackSubmitCpu(
            long scheduleBeginHostTicks,
            long readbackRequestBeginHostTicks)
        {
            PreReadbackSubmitCpuMs =
                DeltaMilliseconds(
                    scheduleBeginHostTicks,
                    readbackRequestBeginHostTicks);
        }

        public static void RecordReadbackRequestCpu(
            long readbackRequestBeginHostTicks,
            long readbackRequestEndHostTicks)
        {
            ReadbackRequestCpuMs =
                DeltaMilliseconds(
                    readbackRequestBeginHostTicks,
                    readbackRequestEndHostTicks);
        }

        public static void RecordPollPass(long pollHostTicks)
        {
            if (
                _lastPollHostTicks > 0L &&
                pollHostTicks > _lastPollHostTicks
            )
            {
                PollIntervalMs =
                    DeltaMilliseconds(
                        _lastPollHostTicks,
                        pollHostTicks);
            }

            _lastPollHostTicks = pollHostTicks;
        }

        public static void RecordDoneObserved(
            long readbackRequestHostTicks,
            long doneObservedHostTicks,
            int requestUnityFrame,
            int doneObservedUnityFrame)
        {
            RequestToDoneObservedMs =
                DeltaMilliseconds(
                    readbackRequestHostTicks,
                    doneObservedHostTicks);

            ReadbackObservedFrameDelta =
                requestUnityFrame >= 0 &&
                doneObservedUnityFrame >= requestUnityFrame
                    ? doneObservedUnityFrame - requestUnityFrame
                    : 0;

            BoundarySampleCount++;
        }

        public static void RecordReadbackCloneCpu(
            long cloneBeginHostTicks,
            long cloneEndHostTicks)
        {
            ReadbackCloneCpuMs =
                DeltaMilliseconds(
                    cloneBeginHostTicks,
                    cloneEndHostTicks);
        }

        public static void RecordDecodeMathCpu(
            long decodeBeginHostTicks,
            long decodeEndHostTicks)
        {
            DecodeMathCpuMs =
                DeltaMilliseconds(
                    decodeBeginHostTicks,
                    decodeEndHostTicks);
        }

        private static float DeltaMilliseconds(long start, long end)
        {
            if (start <= 0L || end <= start)
            {
                return 0f;
            }

            double milliseconds =
                (end - start) *
                1000.0 /
                System.Diagnostics.Stopwatch.Frequency;

            if (
                double.IsNaN(milliseconds) ||
                double.IsInfinity(milliseconds) ||
                milliseconds < 0.0
            )
            {
                return 0f;
            }

            return (float)milliseconds;
        }
    }
}
