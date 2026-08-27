using System;

namespace Mediapipe.Unity
{
    /// <summary>
    /// Read-only Production native-camera telemetry bridge.
    /// v17 isolates Media Foundation capture on a dedicated D3D11 device and bridges shared NV12 to the processing device with GPU-side shared fences.
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
        // v15 compatibility field: callback-arrival QPC interval.
        public float sourceIntervalMs;
        public float arrivalIntervalMs;
        public float sourceTimestampIntervalMs;
        public float callbackCpuMs;
        public float requestNextCpuMs;
        public float callbackToRequestNextMs;
        public ulong supersededFrameCount;
        public ulong processingFailureFrameCount;
        public bool processingWorkerRunning;

        // Kiwi-owned NV12 ingest boundary. Camera IMFSample lifetime ends before
        // this boundary is handed to the processing worker.
        public ulong latestIngestSequence;
        public long latestIngestHostTicks;
        public ulong ingestCopyFrameCount;
        public ulong ingestCopyFailureFrameCount;
        public float ingestCopySubmitMs;
        public ulong ingestCompletedFenceValue;
        public ulong ingestConsumerCompletedFenceValue;
        public bool captureDeviceIsolated;
        public bool captureD3D11MultithreadProtected;
        public bool d3d11MultithreadProtected;
        public float processingCpuMs;

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
            float sourceIntervalMs,
            float arrivalIntervalMs,
            float sourceTimestampIntervalMs,
            float callbackCpuMs,
            float requestNextCpuMs,
            float callbackToRequestNextMs,
            ulong supersededFrameCount,
            ulong processingFailureFrameCount,
            bool processingWorkerRunning,
            ulong latestIngestSequence,
            long latestIngestHostTicks,
            ulong ingestCopyFrameCount,
            ulong ingestCopyFailureFrameCount,
            float ingestCopySubmitMs,
            ulong ingestCompletedFenceValue,
            ulong ingestConsumerCompletedFenceValue,
            bool captureDeviceIsolated,
            bool captureD3D11MultithreadProtected,
            bool d3d11MultithreadProtected,
            float processingCpuMs,
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
                _latest.sourceIntervalMs = Math.Max(0f, sourceIntervalMs);
                _latest.arrivalIntervalMs = Math.Max(0f, arrivalIntervalMs);
                _latest.sourceTimestampIntervalMs = Math.Max(0f, sourceTimestampIntervalMs);
                _latest.callbackCpuMs = Math.Max(0f, callbackCpuMs);
                _latest.requestNextCpuMs = Math.Max(0f, requestNextCpuMs);
                _latest.callbackToRequestNextMs = Math.Max(0f, callbackToRequestNextMs);
                _latest.supersededFrameCount = supersededFrameCount;
                _latest.processingFailureFrameCount = processingFailureFrameCount;
                _latest.processingWorkerRunning = processingWorkerRunning;
                _latest.latestIngestSequence = latestIngestSequence;
                _latest.latestIngestHostTicks = latestIngestHostTicks;
                _latest.ingestCopyFrameCount = ingestCopyFrameCount;
                _latest.ingestCopyFailureFrameCount = ingestCopyFailureFrameCount;
                _latest.ingestCopySubmitMs = Math.Max(0f, ingestCopySubmitMs);
                _latest.ingestCompletedFenceValue = ingestCompletedFenceValue;
                _latest.ingestConsumerCompletedFenceValue = ingestConsumerCompletedFenceValue;
                _latest.captureDeviceIsolated = captureDeviceIsolated;
                _latest.captureD3D11MultithreadProtected = captureD3D11MultithreadProtected;
                _latest.d3d11MultithreadProtected = d3d11MultithreadProtected;
                _latest.processingCpuMs = Math.Max(0f, processingCpuMs);
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
