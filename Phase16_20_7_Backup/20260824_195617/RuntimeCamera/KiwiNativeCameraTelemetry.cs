using System;

namespace Mediapipe.Unity
{
    /// <summary>
    /// Read-only Production native-camera telemetry bridge.
    ///
    /// Camera/session generation is intentionally separate from frame sequence:
    /// a new frame must never masquerade as a camera/provider epoch change.
    /// Values are published from WindowsNativeWebCamSource on Unity's main thread.
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

        public ulong latestCaptureSequence;
        public long latestCaptureHostTicks;
        public ulong latestPresentedSequence;
        public long latestPresentedHostTicks;

        public ulong captureFrameCount;
        public ulong droppedFrameCount;
        public ulong presentedFrameCount;
        public ulong producerCompletedFenceValue;
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
