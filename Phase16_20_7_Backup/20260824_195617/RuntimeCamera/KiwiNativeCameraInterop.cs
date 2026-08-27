using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Mediapipe.Unity
{
    internal static class KiwiNativeCameraInterop
    {
        private const string DllName = "KiwiNativeCamera";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_IsSupported();

        [DllImport(
            DllName,
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern int KiwiNativeCamera_Start(
            string cameraName,
            int width,
            int height,
            int fps);

        [DllImport(
            DllName,
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern int KiwiNativeCamera_StartDiagnostic(
            string cameraName,
            int width,
            int height,
            int fps,
            int diagnosticMode);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void KiwiNativeCamera_Stop();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_IsRunning();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestCaptureSequence();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern long KiwiNativeCamera_GetLatestCaptureHostTicks();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_RequestLatestPresent(
            out int slotIndex,
            out ulong sequence,
            out long hostTicks,
            out IntPtr d3d12Resource);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr KiwiNativeCamera_GetRenderEventFunc();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_GetPresentEventBase();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetCaptureFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetDroppedFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetPresentedFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetProducerCompletedFenceValue();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_GetSharedResourceCompatibilityTier();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_GetLastErrorCode();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr KiwiNativeCamera_GetLastErrorMessage();

        internal static bool IsSupported()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                return KiwiNativeCamera_IsSupported() != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
#else
            return false;
#endif
        }

        internal static int SharedResourceCompatibilityTier
        {
            get
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                try
                {
                    return KiwiNativeCamera_GetSharedResourceCompatibilityTier();
                }
                catch (DllNotFoundException)
                {
                    return -10;
                }
                catch (EntryPointNotFoundException)
                {
                    return -11;
                }
#else
                return -12;
#endif
            }
        }

        internal static bool Start(
            string cameraName,
            int width,
            int height,
            int fps)
        {
            return KiwiNativeCamera_Start(
                cameraName,
                width,
                height,
                fps) != 0;
        }

        internal static bool StartDiagnostic(
            string cameraName,
            int width,
            int height,
            int fps,
            int diagnosticMode)
        {
            return KiwiNativeCamera_StartDiagnostic(
                cameraName,
                width,
                height,
                fps,
                diagnosticMode) != 0;
        }

        internal static void Stop()
        {
            try
            {
                KiwiNativeCamera_Stop();
            }
            catch (DllNotFoundException)
            {
            }
        }

        internal static bool IsRunning =>
            KiwiNativeCamera_IsRunning() != 0;

        internal static ulong LatestCaptureSequence =>
            KiwiNativeCamera_GetLatestCaptureSequence();

        internal static long LatestCaptureHostTicks =>
            KiwiNativeCamera_GetLatestCaptureHostTicks();

        internal static bool TryRequestLatestPresent(
            out int slotIndex,
            out ulong sequence,
            out long hostTicks,
            out IntPtr resource)
        {
            return KiwiNativeCamera_RequestLatestPresent(
                out slotIndex,
                out sequence,
                out hostTicks,
                out resource) != 0;
        }

        internal static IntPtr RenderEventFunc =>
            KiwiNativeCamera_GetRenderEventFunc();

        internal static int PresentEventBase =>
            KiwiNativeCamera_GetPresentEventBase();

        internal static ulong CaptureFrameCount =>
            KiwiNativeCamera_GetCaptureFrameCount();

        internal static ulong DroppedFrameCount =>
            KiwiNativeCamera_GetDroppedFrameCount();

        internal static ulong PresentedFrameCount =>
            KiwiNativeCamera_GetPresentedFrameCount();

        internal static ulong ProducerCompletedFenceValue =>
            KiwiNativeCamera_GetProducerCompletedFenceValue();

        internal static string LastError
        {
            get
            {
                int code = 0;
                string message = string.Empty;

                try
                {
                    code = KiwiNativeCamera_GetLastErrorCode();

                    IntPtr ptr =
                        KiwiNativeCamera_GetLastErrorMessage();

                    if (ptr != IntPtr.Zero)
                    {
                        message =
                            Marshal.PtrToStringAnsi(ptr) ??
                            string.Empty;
                    }
                }
                catch
                {
                }

                return $"code=0x{code:X8} {message}";
            }
        }
    }
}
