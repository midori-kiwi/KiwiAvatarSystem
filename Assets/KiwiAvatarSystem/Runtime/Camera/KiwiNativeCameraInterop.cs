﻿using System;
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
        private static extern ulong KiwiNativeCamera_GetLatestSourceSequence();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern long KiwiNativeCamera_GetLatestSourceHostTicks();

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
        private static extern ulong KiwiNativeCamera_GetSourceFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetSupersededFrameCount();

        // v44.53 observer-only Native CPU NV12 direct shadow crop.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetNativeCpuShadowCropCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetNativeCpuShadowCropFailureCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestNativeCpuShadowCropMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestNativeCpuShadowCropSequence();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_CopyLatestCpuCropNchwFloat(
            [In] float[] samplingMatrix16,
            int outputSize,
            [Out] float[] destination,
            int destinationFloatCount,
            out ulong sequence,
            out long hostTicks,
            out ulong nativeCpuMicroseconds);

        // v44.55.1 observer-only low-rate armed snapshot.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_ArmDiagnosticCpuSnapshot();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_GetDiagnosticCpuSnapshotIdentity(
            out ulong sequence,
            out long hostTicks);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_CopyDiagnosticCpuSnapshotCropNchwFloat(
            long expectedHostTicks,
            [In] float[] samplingMatrix16,
            int outputSize,
            [Out] float[] destination,
            int destinationFloatCount,
            out ulong sequence,
            out long matchedHostTicks,
            out ulong nativeCpuMicroseconds);


        // v44.55.8 observer-only Production UNORM8 parity candidates.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_CopyDiagnosticCpuSnapshotCropNchwFloatQuantized(
            long expectedHostTicks,
            [In] float[] samplingMatrix16,
            int outputSize,
            int quantizationMode,
            [Out] float[] destination,
            int destinationFloatCount,
            out ulong sequence,
            out long matchedHostTicks,
            out ulong nativeCpuMicroseconds);

        // v44.55.10 observer-only Presentation-frame identity burst.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_GetDiagnosticCpuIdentityBurstStatus(
            out int validCandidateCount,
            out int complete);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_GetDiagnosticCpuIdentityCandidate(
            int candidateIndex,
            out ulong sequence,
            out long hostTicks,
            out int role);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_CopyDiagnosticCpuIdentityCandidateCropNchwFloatQuantized(
            int candidateIndex,
            [In] float[] samplingMatrix16,
            int outputSize,
            int quantizationMode,
            [Out] float[] destination,
            int destinationFloatCount,
            out ulong sequence,
            out long hostTicks,
            out int role,
            out ulong nativeCpuMicroseconds);

        // v44.55.12 observer-only D3D fixed-point subtexel candidate crop.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_CopyDiagnosticCpuIdentityCandidateCropNchwFloatSubtexel(
            int candidateIndex,
            [In] float[] samplingMatrix16,
            int outputSize,
            int fractionalBits,
            [Out] float[] destination,
            int destinationFloatCount,
            out ulong sequence,
            out long hostTicks,
            out int role,
            out ulong nativeCpuMicroseconds);

        // v44.55 retrospective exact-slot API retained for diagnostic compatibility.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_CopyCpuCropNchwFloatByHostTicks(
            long expectedHostTicks,
            [In] float[] samplingMatrix16,
            int outputSize,
            [Out] float[] destination,
            int destinationFloatCount,
            out ulong sequence,
            out long matchedHostTicks,
            out ulong nativeCpuMicroseconds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetProcessingFailureFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_IsProcessingWorkerRunning();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetIngestCopyFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetIngestCopyFailureFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestIngestSequence();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern long KiwiNativeCamera_GetLatestIngestHostTicks();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestSourceIntervalMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestArrivalIntervalMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestSourceTimestampIntervalMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestCallbackCpuMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestRequestNextCpuMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestCallbackToRequestNextMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestIngestCopySubmitMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestProcessingCpuMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetIngestCompletedFenceValue();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetIngestConsumerCompletedFenceValue();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_IsCaptureDeviceIsolated();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_IsCaptureD3D11MultithreadProtected();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_IsD3D11MultithreadProtected();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetCaptureGpuWaitCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetProcessingGpuWaitCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetReadyReplacementFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetAllSlotsBusyDropFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetIngestAcceptedFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetReadyUnclaimedCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetOldestReadyAgeMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetIngestProducerFenceLag();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetIngestConsumerFenceLag();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestProcessedSourceAgeMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_GetCaptureTransportId();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KiwiNativeCamera_IsSystemMemoryCaptureEnabled();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestMfSampleResidenceMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestCaptureCopyGpuSubmitMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestCaptureCopyGpuCompletionMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetCaptureCopyGpuOutstandingDepth();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestCpuNv12CopyMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestCpuNv12CopyBytes();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetCpuLatestReplacementCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetCpuAllSlotsBusyDropCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetLatestGpuUploadSubmitMicroseconds();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetGpuUploadCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetCaptureFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetDroppedFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetPresentedFrameCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong KiwiNativeCamera_GetProducerCompletedFenceValue();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern long KiwiNativeCamera_GetQpcNow();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern long KiwiNativeCamera_GetQpcFrequency();

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

        internal static ulong LatestSourceSequence =>
            KiwiNativeCamera_GetLatestSourceSequence();

        internal static long LatestSourceHostTicks =>
            KiwiNativeCamera_GetLatestSourceHostTicks();

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

        internal static ulong SourceFrameCount =>
            KiwiNativeCamera_GetSourceFrameCount();

        internal static ulong SupersededFrameCount =>
            KiwiNativeCamera_GetSupersededFrameCount();


        internal static ulong NativeCpuShadowCropCount =>
            KiwiNativeCamera_GetNativeCpuShadowCropCount();

        internal static ulong NativeCpuShadowCropFailureCount =>
            KiwiNativeCamera_GetNativeCpuShadowCropFailureCount();

        internal static ulong LatestNativeCpuShadowCropMicroseconds =>
            KiwiNativeCamera_GetLatestNativeCpuShadowCropMicroseconds();

        internal static ulong LatestNativeCpuShadowCropSequence =>
            KiwiNativeCamera_GetLatestNativeCpuShadowCropSequence();

        internal static bool TryArmDiagnosticCpuSnapshot()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                return
                    KiwiNativeCamera_ArmDiagnosticCpuSnapshot() != 0;
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

        internal static bool TryGetDiagnosticCpuSnapshotIdentity(
            out ulong sequence,
            out long hostTicks)
        {
            sequence = 0;
            hostTicks = 0;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                return
                    KiwiNativeCamera_GetDiagnosticCpuSnapshotIdentity(
                        out sequence,
                        out hostTicks) != 0 &&
                    sequence > 0 &&
                    hostTicks > 0;
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

        internal static bool TryCopyDiagnosticCpuSnapshotCropNchwFloat(
            long expectedHostTicks,
            float[] samplingMatrix16,
            int outputSize,
            float[] destination,
            out ulong sequence,
            out long matchedHostTicks,
            out ulong nativeCpuMicroseconds)
        {
            sequence = 0;
            matchedHostTicks = 0;
            nativeCpuMicroseconds = 0;

            if (
                expectedHostTicks <= 0 ||
                samplingMatrix16 == null ||
                samplingMatrix16.Length != 16 ||
                outputSize <= 0 ||
                destination == null ||
                destination.Length <
                    outputSize *
                    outputSize *
                    3)
            {
                return false;
            }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                return
                    KiwiNativeCamera_CopyDiagnosticCpuSnapshotCropNchwFloat(
                        expectedHostTicks,
                        samplingMatrix16,
                        outputSize,
                        destination,
                        destination.Length,
                        out sequence,
                        out matchedHostTicks,
                        out nativeCpuMicroseconds) != 0 &&
                    matchedHostTicks ==
                        expectedHostTicks;
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

        internal static bool TryCopyDiagnosticCpuSnapshotCropNchwFloatQuantized(
            long expectedHostTicks,
            float[] samplingMatrix16,
            int outputSize,
            int quantizationMode,
            float[] destination,
            out ulong sequence,
            out long matchedHostTicks,
            out ulong nativeCpuMicroseconds)
        {
            sequence = 0;
            matchedHostTicks = 0;
            nativeCpuMicroseconds = 0;

            if (
                expectedHostTicks <= 0 ||
                samplingMatrix16 == null ||
                samplingMatrix16.Length != 16 ||
                outputSize <= 0 ||
                quantizationMode < 0 ||
                quantizationMode > 2 ||
                destination == null ||
                destination.Length < outputSize * outputSize * 3)
            {
                return false;
            }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                return
                    KiwiNativeCamera_CopyDiagnosticCpuSnapshotCropNchwFloatQuantized(
                        expectedHostTicks,
                        samplingMatrix16,
                        outputSize,
                        quantizationMode,
                        destination,
                        destination.Length,
                        out sequence,
                        out matchedHostTicks,
                        out nativeCpuMicroseconds) != 0 &&
                    matchedHostTicks == expectedHostTicks;
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


        internal static bool TryGetDiagnosticCpuIdentityBurstStatus(
            out int validCandidateCount,
            out bool complete)
        {
            validCandidateCount = 0;
            complete = false;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                int completeValue = 0;

                bool available =
                    KiwiNativeCamera_GetDiagnosticCpuIdentityBurstStatus(
                        out validCandidateCount,
                        out completeValue) != 0;

                complete =
                    completeValue != 0;

                return available;
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

        internal static bool TryGetDiagnosticCpuIdentityCandidate(
            int candidateIndex,
            out ulong sequence,
            out long hostTicks,
            out int role)
        {
            sequence = 0;
            hostTicks = 0;
            role = 0;

            if (
                candidateIndex < 0 ||
                candidateIndex >= 5)
            {
                return false;
            }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                return
                    KiwiNativeCamera_GetDiagnosticCpuIdentityCandidate(
                        candidateIndex,
                        out sequence,
                        out hostTicks,
                        out role) != 0 &&
                    sequence > 0 &&
                    hostTicks > 0;
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

        internal static bool TryCopyDiagnosticCpuIdentityCandidateCropNchwFloatQuantized(
            int candidateIndex,
            float[] samplingMatrix16,
            int outputSize,
            int quantizationMode,
            float[] destination,
            out ulong sequence,
            out long hostTicks,
            out int role,
            out ulong nativeCpuMicroseconds)
        {
            sequence = 0;
            hostTicks = 0;
            role = 0;
            nativeCpuMicroseconds = 0;

            if (
                candidateIndex < 0 ||
                candidateIndex >= 5 ||
                samplingMatrix16 == null ||
                samplingMatrix16.Length != 16 ||
                outputSize <= 0 ||
                quantizationMode < 0 ||
                quantizationMode > 2 ||
                destination == null ||
                destination.Length <
                    outputSize *
                    outputSize *
                    3)
            {
                return false;
            }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                return
                    KiwiNativeCamera_CopyDiagnosticCpuIdentityCandidateCropNchwFloatQuantized(
                        candidateIndex,
                        samplingMatrix16,
                        outputSize,
                        quantizationMode,
                        destination,
                        destination.Length,
                        out sequence,
                        out hostTicks,
                        out role,
                        out nativeCpuMicroseconds) != 0 &&
                    sequence > 0 &&
                    hostTicks > 0;
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


        internal static bool TryCopyDiagnosticCpuIdentityCandidateCropNchwFloatSubtexel(
            int candidateIndex,
            float[] samplingMatrix16,
            int outputSize,
            int fractionalBits,
            float[] destination,
            out ulong sequence,
            out long hostTicks,
            out int role,
            out ulong nativeCpuMicroseconds)
        {
            sequence = 0;
            hostTicks = 0;
            role = 0;
            nativeCpuMicroseconds = 0;

            bool validFractionBits =
                fractionalBits == 0 ||
                fractionalBits == 8 ||
                fractionalBits == 9 ||
                fractionalBits == 10 ||
                fractionalBits == 12;

            if (
                candidateIndex < 0 ||
                candidateIndex >= 5 ||
                samplingMatrix16 == null ||
                samplingMatrix16.Length != 16 ||
                outputSize <= 0 ||
                !validFractionBits ||
                destination == null ||
                destination.Length <
                    outputSize *
                    outputSize *
                    3)
            {
                return false;
            }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                return
                    KiwiNativeCamera_CopyDiagnosticCpuIdentityCandidateCropNchwFloatSubtexel(
                        candidateIndex,
                        samplingMatrix16,
                        outputSize,
                        fractionalBits,
                        destination,
                        destination.Length,
                        out sequence,
                        out hostTicks,
                        out role,
                        out nativeCpuMicroseconds) != 0 &&
                    sequence > 0 &&
                    hostTicks > 0;
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


        internal static bool TryCopyCpuCropNchwFloatByHostTicks(
            long expectedHostTicks,
            float[] samplingMatrix16,
            int outputSize,
            float[] destination,
            out ulong sequence,
            out long matchedHostTicks,
            out ulong nativeCpuMicroseconds)
        {
            sequence = 0;
            matchedHostTicks = 0;
            nativeCpuMicroseconds = 0;

            if (
                expectedHostTicks <= 0 ||
                samplingMatrix16 == null ||
                samplingMatrix16.Length != 16 ||
                outputSize <= 0 ||
                destination == null ||
                destination.Length <
                    outputSize *
                    outputSize *
                    3)
            {
                return false;
            }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                return
                    KiwiNativeCamera_CopyCpuCropNchwFloatByHostTicks(
                        expectedHostTicks,
                        samplingMatrix16,
                        outputSize,
                        destination,
                        destination.Length,
                        out sequence,
                        out matchedHostTicks,
                        out nativeCpuMicroseconds) != 0 &&
                    matchedHostTicks ==
                        expectedHostTicks;
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

        internal static bool TryCopyLatestCpuCropNchwFloat(
            float[] samplingMatrix16,
            int outputSize,
            float[] destination,
            out ulong sequence,
            out long hostTicks,
            out ulong nativeCpuMicroseconds)
        {
            sequence = 0;
            hostTicks = 0;
            nativeCpuMicroseconds = 0;

            if (
                samplingMatrix16 == null ||
                samplingMatrix16.Length != 16 ||
                destination == null ||
                outputSize <= 0 ||
                destination.Length <
                    outputSize *
                    outputSize *
                    3)
            {
                return false;
            }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                return
                    KiwiNativeCamera_CopyLatestCpuCropNchwFloat(
                        samplingMatrix16,
                        outputSize,
                        destination,
                        destination.Length,
                        out sequence,
                        out hostTicks,
                        out nativeCpuMicroseconds) != 0;
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

        internal static ulong ProcessingFailureFrameCount =>
            KiwiNativeCamera_GetProcessingFailureFrameCount();

        internal static bool ProcessingWorkerRunning =>
            KiwiNativeCamera_IsProcessingWorkerRunning() != 0;

        internal static ulong IngestCopyFrameCount =>
            KiwiNativeCamera_GetIngestCopyFrameCount();

        internal static ulong IngestCopyFailureFrameCount =>
            KiwiNativeCamera_GetIngestCopyFailureFrameCount();

        internal static ulong LatestIngestSequence =>
            KiwiNativeCamera_GetLatestIngestSequence();

        internal static long LatestIngestHostTicks =>
            KiwiNativeCamera_GetLatestIngestHostTicks();

        internal static ulong LatestSourceIntervalMicroseconds =>
            KiwiNativeCamera_GetLatestSourceIntervalMicroseconds();

        internal static ulong LatestArrivalIntervalMicroseconds =>
            KiwiNativeCamera_GetLatestArrivalIntervalMicroseconds();

        internal static ulong LatestSourceTimestampIntervalMicroseconds =>
            KiwiNativeCamera_GetLatestSourceTimestampIntervalMicroseconds();

        internal static ulong LatestCallbackCpuMicroseconds =>
            KiwiNativeCamera_GetLatestCallbackCpuMicroseconds();

        internal static ulong LatestRequestNextCpuMicroseconds =>
            KiwiNativeCamera_GetLatestRequestNextCpuMicroseconds();

        internal static ulong LatestCallbackToRequestNextMicroseconds =>
            KiwiNativeCamera_GetLatestCallbackToRequestNextMicroseconds();

        internal static ulong LatestIngestCopySubmitMicroseconds =>
            KiwiNativeCamera_GetLatestIngestCopySubmitMicroseconds();

        internal static ulong LatestProcessingCpuMicroseconds =>
            KiwiNativeCamera_GetLatestProcessingCpuMicroseconds();

        internal static ulong IngestCompletedFenceValue =>
            KiwiNativeCamera_GetIngestCompletedFenceValue();

        internal static ulong IngestConsumerCompletedFenceValue =>
            KiwiNativeCamera_GetIngestConsumerCompletedFenceValue();

        internal static bool CaptureDeviceIsolated =>
            KiwiNativeCamera_IsCaptureDeviceIsolated() != 0;

        internal static bool CaptureD3D11MultithreadProtected =>
            KiwiNativeCamera_IsCaptureD3D11MultithreadProtected() != 0;

        internal static bool D3D11MultithreadProtected =>
            KiwiNativeCamera_IsD3D11MultithreadProtected() != 0;

        internal static ulong CaptureGpuWaitCount =>
            KiwiNativeCamera_GetCaptureGpuWaitCount();

        internal static ulong ProcessingGpuWaitCount =>
            KiwiNativeCamera_GetProcessingGpuWaitCount();

        internal static ulong ReadyReplacementFrameCount =>
            KiwiNativeCamera_GetReadyReplacementFrameCount();

        internal static ulong AllSlotsBusyDropFrameCount =>
            KiwiNativeCamera_GetAllSlotsBusyDropFrameCount();

        internal static ulong IngestAcceptedFrameCount =>
            KiwiNativeCamera_GetIngestAcceptedFrameCount();

        internal static ulong ReadyUnclaimedCount =>
            KiwiNativeCamera_GetReadyUnclaimedCount();

        internal static ulong OldestReadyAgeMicroseconds =>
            KiwiNativeCamera_GetOldestReadyAgeMicroseconds();

        internal static ulong IngestProducerFenceLag =>
            KiwiNativeCamera_GetIngestProducerFenceLag();

        internal static ulong IngestConsumerFenceLag =>
            KiwiNativeCamera_GetIngestConsumerFenceLag();

        internal static ulong LatestProcessedSourceAgeMicroseconds =>
            KiwiNativeCamera_GetLatestProcessedSourceAgeMicroseconds();

        internal static int CaptureTransportId =>
            KiwiNativeCamera_GetCaptureTransportId();

        internal static bool SystemMemoryCaptureEnabled =>
            KiwiNativeCamera_IsSystemMemoryCaptureEnabled() != 0;

        internal static ulong LatestMfSampleResidenceMicroseconds =>
            KiwiNativeCamera_GetLatestMfSampleResidenceMicroseconds();

        internal static ulong LatestCaptureCopyGpuSubmitMicroseconds =>
            KiwiNativeCamera_GetLatestCaptureCopyGpuSubmitMicroseconds();

        internal static ulong LatestCaptureCopyGpuCompletionMicroseconds =>
            KiwiNativeCamera_GetLatestCaptureCopyGpuCompletionMicroseconds();

        internal static ulong CaptureCopyGpuOutstandingDepth =>
            KiwiNativeCamera_GetCaptureCopyGpuOutstandingDepth();

        internal static ulong LatestCpuNv12CopyMicroseconds =>
            KiwiNativeCamera_GetLatestCpuNv12CopyMicroseconds();

        internal static ulong LatestCpuNv12CopyBytes =>
            KiwiNativeCamera_GetLatestCpuNv12CopyBytes();

        internal static ulong CpuLatestReplacementCount =>
            KiwiNativeCamera_GetCpuLatestReplacementCount();

        internal static ulong CpuAllSlotsBusyDropCount =>
            KiwiNativeCamera_GetCpuAllSlotsBusyDropCount();

        internal static ulong LatestGpuUploadSubmitMicroseconds =>
            KiwiNativeCamera_GetLatestGpuUploadSubmitMicroseconds();

        internal static ulong GpuUploadCount =>
            KiwiNativeCamera_GetGpuUploadCount();

        internal static ulong CaptureFrameCount =>
            KiwiNativeCamera_GetCaptureFrameCount();

        internal static ulong DroppedFrameCount =>
            KiwiNativeCamera_GetDroppedFrameCount();

        internal static ulong PresentedFrameCount =>
            KiwiNativeCamera_GetPresentedFrameCount();

        internal static ulong ProducerCompletedFenceValue =>
            KiwiNativeCamera_GetProducerCompletedFenceValue();

        internal static long QpcNow =>
            KiwiNativeCamera_GetQpcNow();

        internal static long QpcFrequency =>
            KiwiNativeCamera_GetQpcFrequency();

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
