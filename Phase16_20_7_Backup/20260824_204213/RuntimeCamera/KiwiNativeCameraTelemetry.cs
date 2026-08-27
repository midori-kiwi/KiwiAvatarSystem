using System;

namespace Mediapipe.Unity
{
    /// <summary>
    /// Read-only Production native-camera telemetry bridge.
    /// v14 separates raw MF acquisition, GPU processing/publish, and Unity present.
    /// All *HostTicks exposed here are normalized to the managed Stopwatch domain.
    /// </summary>
    public struct KiwiNativeCameraTelemetrySnapshot
    {
        public bool active;
        public int sessionGeneration;
        public string sourceName;
        public int width;
        public int height;
        public int targetFrameRate;
        public int presentationTextureId;

        public bool timestampCalibrationValid;
        public long nativeQpcFrequency;

        // Raw Media Foundation acquisition cadence.
        public ulong latestSourceSequence;
        public long latestSourceHostTicks;
        public ulong sourceFrameCount;
        public ulong supersededFrameCount;
        public ulong processingFailureFrameCount;
        public bool processingWorkerRunning;

        // GPU-processed/published cadence.
        public ulong latestCaptureSequence;
        public long latestCaptureHostTicks;
        public ulong captureFrameCount;
        public ulong droppedFrameCount;
        public ulong producerCompletedFenceValue;

        // Unity-presented cadence.
        public ulong latestPresentedSequence;
        public long latestPresentedHostTicks;
        public ulong presentedFrameCount;
    }

    public static class KiwiNativeCameraTelemetry
    {
        private static readonly object Gate = new object();

        private static KiwiNativeCameraTelemetrySnapshot _latest;
        private static int _nextSessionGeneration;

        public static bool TryGetSnapshot(
            out KiwiNativeCameraTelemetrySnapshot snapshot)
        {
            lock (Gate)
            {
                snapshot = _latest;
                return snapshot.active;
            }
        }

        public static KiwiNativeCameraTelemetrySnapshot Latest
        {
            get
            {
                lock (Gate)
                {
                    return _latest;
                }
            }
        }

        internal static int BeginSession(
            string sourceName,
            int width,
            int height,
            int targetFrameRate)
        {
            lock (Gate)
            {
                _nextSessionGeneration =
                    _nextSessionGeneration == int.MaxValue
                        ? 1
                        : _nextSessionGeneration + 1;

                _latest =
                    new KiwiNativeCameraTelemetrySnapshot
                    {
                        active = true,
                        sessionGeneration = _nextSessionGeneration,
                        sourceName = sourceName ?? string.Empty,
                        width = Math.Max(0, width),
                        height = Math.Max(0, height),
                        targetFrameRate = Math.Max(0, targetFrameRate),
                        presentationTextureId = 0
                    };

                return _latest.sessionGeneration;
            }
        }

        internal static void Publish(
            int sessionGeneration,
            string sourceName,
            int width,
            int height,
            int targetFrameRate,
            int presentationTextureId,
            bool timestampCalibrationValid,
            long nativeQpcFrequency,
            ulong latestSourceSequence,
            long latestSourceHostTicks,
            ulong sourceFrameCount,
            ulong supersededFrameCount,
            ulong processingFailureFrameCount,
            bool processingWorkerRunning,
            ulong latestCaptureSequence,
            long latestCaptureHostTicks,
            ulong latestPresentedSequence,
            long latestPresentedHostTicks,
            ulong captureFrameCount,
            ulong droppedFrameCount,
            ulong presentedFrameCount,
            ulong producerCompletedFenceValue)
        {
            lock (Gate)
            {
                if (
                    !_latest.active ||
                    sessionGeneration <= 0 ||
                    _latest.sessionGeneration != sessionGeneration)
                {
                    return;
                }

                _latest.sourceName = sourceName ?? string.Empty;
                _latest.width = Math.Max(0, width);
                _latest.height = Math.Max(0, height);
                _latest.targetFrameRate = Math.Max(0, targetFrameRate);
                _latest.presentationTextureId = presentationTextureId;
                _latest.timestampCalibrationValid = timestampCalibrationValid;
                _latest.nativeQpcFrequency = Math.Max(0L, nativeQpcFrequency);
                _latest.latestSourceSequence = latestSourceSequence;
                _latest.latestSourceHostTicks = latestSourceHostTicks;
                _latest.sourceFrameCount = sourceFrameCount;
                _latest.supersededFrameCount = supersededFrameCount;
                _latest.processingFailureFrameCount = processingFailureFrameCount;
                _latest.processingWorkerRunning = processingWorkerRunning;
                _latest.latestCaptureSequence = latestCaptureSequence;
                _latest.latestCaptureHostTicks = latestCaptureHostTicks;
                _latest.latestPresentedSequence = latestPresentedSequence;
                _latest.latestPresentedHostTicks = latestPresentedHostTicks;
                _latest.captureFrameCount = captureFrameCount;
                _latest.droppedFrameCount = droppedFrameCount;
                _latest.presentedFrameCount = presentedFrameCount;
                _latest.producerCompletedFenceValue = producerCompletedFenceValue;
            }
        }

        internal static void EndSession(int sessionGeneration)
        {
            lock (Gate)
            {
                if (
                    sessionGeneration <= 0 ||
                    _latest.sessionGeneration != sessionGeneration)
                {
                    return;
                }

                _latest.active = false;
            }
        }
    }
}
