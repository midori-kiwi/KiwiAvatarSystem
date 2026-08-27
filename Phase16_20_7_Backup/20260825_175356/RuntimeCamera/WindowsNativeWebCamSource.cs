using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mediapipe.Unity
{
    /// <summary>
    /// Windows-only low-latency camera source.
    ///
    /// Production path:
    /// Media Foundation native NV12 1080p60 callback
    /// -> one-slot latest-frame mailbox (no stale queue)
    /// -> dedicated D3D11 GPU worker + NV12-to-RGBA compute
    /// -> D3D12-owned Compatibility shared RGBA8 3-slot ring
    /// -> D3D11 reverse-open + CopyResource
    /// -> D3D12-owned shared producer fence
    /// -> non-blocking producer completion polling
    /// -> one fixed Unity Texture2D wrapper per native slot (RGBA32, linear=true)
    /// -> same-frame GPU orientation normalization into one stable Presentation Texture.
    ///
    /// Unity's graphics queue is never made to Wait on the camera producer.
    /// If the native path is unavailable or fails to start, this class falls
    /// back to the existing WebCamSource implementation.
    /// </summary>
    public sealed class WindowsNativeWebCamSource
        : WebCamSource, IKiwiFreshFrameSource
    {
        private const string Tag = nameof(WindowsNativeWebCamSource);

        private readonly string[] _preferredDeviceKeywords;
        private readonly int _preferredWidth;
        private readonly int _preferredHeight;
        private readonly int _preferredFrameRate;

        private bool _nativeActive;
        private bool _fallbackActive;
        private bool _nativePlaying;

        private string _nativeSourceName;
        private Texture _nativeTexture;
        private RenderTexture _nativePresentationTexture;
        private int _nativeTelemetrySessionGeneration;

        // Native QueryPerformanceCounter and managed Stopwatch are treated as
        // separate monotonic domains. v14 calibrates an affine mapping once per
        // camera session instead of assuming their raw tick values are identical.
        private bool _nativeTimestampCalibrationValid;
        private long _nativeQpcAnchorTicks;
        private long _managedStopwatchAnchorTicks;
        private long _nativeQpcFrequency;

        // One persistent Unity wrapper per native ring slot. Never mutate a
        // Texture2D wrapper to point at another D3D12 resource while an older
        // Unity frame may still reference it.
        private readonly Texture2D[] _nativeTextures =
            new Texture2D[3];

        private ulong _latestPresentedSequence;
        private long _latestPresentedHostTicks;
        private int _lastPrepareUnityFrame = -1;

        private IntPtr _renderEventFunc = IntPtr.Zero;
        private int _presentEventBase;

        public WindowsNativeWebCamSource(
            int preferableDefaultWidth,
            ResolutionStruct[] defaultAvailableResolutions,
            string[] preferredDeviceKeywords,
            int preferredProfileWidth,
            int preferredProfileHeight,
            int preferredProfileFrameRate)
            : base(
                preferableDefaultWidth,
                defaultAvailableResolutions,
                preferredDeviceKeywords,
                preferredProfileWidth,
                preferredProfileHeight,
                preferredProfileFrameRate)
        {
            _preferredDeviceKeywords =
                preferredDeviceKeywords ??
                Array.Empty<string>();

            _preferredWidth =
                preferredProfileWidth > 0
                    ? preferredProfileWidth
                    : 1920;

            _preferredHeight =
                preferredProfileHeight > 0
                    ? preferredProfileHeight
                    : 1080;

            _preferredFrameRate =
                preferredProfileFrameRate > 0
                    ? preferredProfileFrameRate
                    : 60;
        }

        public override string sourceName =>
            _nativeActive
                ? _nativeSourceName
                : base.sourceName;

        public override int textureWidth =>
            _nativeActive && _nativeTexture != null
                ? _nativeTexture.width
                : base.textureWidth;

        public override int textureHeight =>
            _nativeActive && _nativeTexture != null
                ? _nativeTexture.height
                : base.textureHeight;

        public override bool isPrepared =>
            _nativeActive
                ? _nativeTexture != null
                : base.isPrepared;

        public override bool isPlaying =>
            _nativeActive
                ? _nativePlaying &&
                    KiwiNativeCameraInterop.IsRunning
                : base.isPlaying;

        public override bool isVerticallyFlipped =>
            _nativeActive
                ? false
                : base.isVerticallyFlipped;

        public override bool isFrontFacing =>
            _nativeActive
                ? false
                : base.isFrontFacing;

        public override RotationAngle rotation =>
            _nativeActive
                ? RotationAngle.Rotation0
                : base.rotation;

        public override IEnumerator Play()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            string mode =
                Environment.GetEnvironmentVariable(
                    "KIWI_NATIVE_CAMERA_MODE"
                ) ?? string.Empty;

            int diagnosticMode =
                ParseDiagnosticMode(mode);

            if (diagnosticMode > 0)
            {
                if (diagnosticMode == 9)
                {
                    Debug.Log(
                        $"[{Tag}] SAFE_DIAGNOSTIC {mode} starting. " +
                        "Reverse-share texture will be consumed by a diagnostic Blit."
                    );

                    yield return RunReverseTextureDiagnostic(
                        mode,
                        diagnosticMode
                    );
                }
                else
                {
                    Debug.Log(
                        $"[{Tag}] SAFE_DIAGNOSTIC {mode} starting. " +
                        "No production tracking consumption will be used."
                    );

                    yield return RunNativeStageDiagnostic(
                        mode,
                        diagnosticMode
                    );
                }

                Debug.Log(
                    $"[{Tag}] SAFE_DIAGNOSTIC {mode} completed. " +
                    "Starting WebCamTexture fallback for normal tracking."
                );

                _fallbackActive = true;
                yield return base.Play();
                yield break;
            }

            if (IsExplicitWebCamFallbackMode(mode))
            {
                Debug.Log(
                    $"[{Tag}] Native camera explicitly disabled by " +
                    $"KIWI_NATIVE_CAMERA_MODE='{mode}'. Using WebCamTexture."
                );

                _fallbackActive = true;
                yield return base.Play();
                yield break;
            }

            if (
                SystemInfo.graphicsDeviceType ==
                    GraphicsDeviceType.Direct3D12 &&
                KiwiNativeCameraInterop.IsSupported()
            )
            {
                Debug.Log(
                    $"[{Tag}] PRODUCTION native camera starting. " +
                    "path=MF-NV12/D3D11->CompatibilityReverseShare/D3D12 " +
                    $"sharedTier={KiwiNativeCameraInterop.SharedResourceCompatibilityTier}"
                );

                yield return TryPlayNative();

                if (_nativeActive)
                {
                    yield break;
                }

                Debug.LogWarning(
                    $"[{Tag}] Production native camera did not become active. " +
                    "Falling back to WebCamTexture. " +
                    KiwiNativeCameraInterop.LastError
                );
            }
            else
            {
                Debug.LogWarning(
                    $"[{Tag}] Production native camera unavailable " +
                    $"graphics={SystemInfo.graphicsDeviceType}. " +
                    "Using WebCamTexture. " +
                    KiwiNativeCameraInterop.LastError
                );
            }
#endif

            _fallbackActive = true;
            yield return base.Play();
        }

        private static bool IsExplicitWebCamFallbackMode(
            string mode)
        {
            return
                string.Equals(
                    mode,
                    "WEBCAM",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    mode,
                    "FALLBACK",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    mode,
                    "DISABLED",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseDiagnosticMode(
            string mode)
        {
            if (string.Equals(
                    mode,
                    "DIAG_RAW",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (string.Equals(
                    mode,
                    "DIAG_COPY",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (string.Equals(
                    mode,
                    "DIAG_CONVERT",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (string.Equals(
                    mode,
                    "DIAG_SHARE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 4;
            }

            if (string.Equals(
                    mode,
                    "DIAG_FENCE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 5;
            }

            if (string.Equals(
                    mode,
                    "DIAG_REVERSE_CREATE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 6;
            }

            if (string.Equals(
                    mode,
                    "DIAG_REVERSE_WRITE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 7;
            }

            if (string.Equals(
                    mode,
                    "DIAG_REVERSE_FENCE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 8;
            }

            if (string.Equals(
                    mode,
                    "DIAG_REVERSE_TEXTURE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 9;
            }

            return 0;
        }

        private IEnumerator RunNativeStageDiagnostic(
            string modeName,
            int diagnosticMode)
        {
            _fallbackActive = false;
            _nativeActive = false;
            _nativePlaying = false;

            if (
                SystemInfo.graphicsDeviceType !=
                    GraphicsDeviceType.Direct3D12
            )
            {
                Debug.LogError(
                    $"[{Tag}] {modeName} requires Direct3D12."
                );

                yield break;
            }

            if (!KiwiNativeCameraInterop.IsSupported())
            {
                Debug.LogError(
                    $"[{Tag}] {modeName} native plugin is not supported. " +
                    KiwiNativeCameraInterop.LastError
                );

                yield break;
            }

            string[] candidates =
                base.sourceCandidateNames ??
                Array.Empty<string>();

            int sourceIndex =
                FindPreferredSourceIndex(candidates);

            if (
                sourceIndex < 0 &&
                candidates.Length > 0
            )
            {
                sourceIndex = 0;
            }

            if (sourceIndex >= 0)
            {
                base.SelectSource(sourceIndex);
                _nativeSourceName =
                    base.sourceName;
            }
            else
            {
                _nativeSourceName =
                    "UGREEN Camera 4K";
            }

            int width = _preferredWidth;
            int height = _preferredHeight;
            int frameRate = _preferredFrameRate;

            Debug.Log(
                $"[{Tag}] {modeName} Start camera='{_nativeSourceName}' " +
                $"profile={width}x{height}@{frameRate} stage={diagnosticMode} " +
                $"sharedTier={KiwiNativeCameraInterop.SharedResourceCompatibilityTier} " +
                "reverseCreate=ID3D12CompatibilityDevice"
            );

            bool started = false;

            try
            {
                started =
                    KiwiNativeCameraInterop.StartDiagnostic(
                        _nativeSourceName,
                        width,
                        height,
                        frameRate,
                        diagnosticMode
                    );
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[{Tag}] {modeName} start exception: " +
                    exception
                );
            }

            if (!started)
            {
                Debug.LogError(
                    $"[{Tag}] {modeName} start failed. " +
                    KiwiNativeCameraInterop.LastError
                );

                KiwiNativeCameraInterop.Stop();
                yield break;
            }

            // No Unity texture consumption in ANY diagnostic stage.
            const int diagnosticFrames = 180;
            const int timeoutUnityFrames = 600;

            int unityFrames = 0;

            while (
                unityFrames < timeoutUnityFrames &&
                KiwiNativeCameraInterop.CaptureFrameCount <
                    diagnosticFrames
            )
            {
                if (unityFrames % 60 == 0)
                {
                    Debug.Log(
                        $"[{Tag}] {modeName} progress " +
                        $"capture={KiwiNativeCameraInterop.CaptureFrameCount} " +
                        $"dropped={KiwiNativeCameraInterop.DroppedFrameCount} " +
                        $"publishedSeq={KiwiNativeCameraInterop.LatestCaptureSequence} " +
                        $"producerFenceCompleted=" +
                        KiwiNativeCameraInterop.ProducerCompletedFenceValue
                    );
                }

                unityFrames++;
                yield return null;
            }

            ulong captureCount =
                KiwiNativeCameraInterop.CaptureFrameCount;

            ulong droppedCount =
                KiwiNativeCameraInterop.DroppedFrameCount;

            ulong sequence =
                KiwiNativeCameraInterop.LatestCaptureSequence;

            ulong completedFence =
                KiwiNativeCameraInterop.ProducerCompletedFenceValue;

            Debug.Log(
                $"[{Tag}] {modeName} RESULT " +
                $"capture={captureCount} " +
                $"dropped={droppedCount} " +
                $"publishedSeq={sequence} " +
                $"producerFenceCompleted={completedFence} " +
                $"unityFrames={unityFrames} " +
                $"status=" +
                (captureCount > 0 ? "PASS_STAGE_ALIVE" : "FAIL_NO_FRAME")
            );

            KiwiNativeCameraInterop.Stop();

            yield return null;
            yield return null;
        }

        private IEnumerator RunReverseTextureDiagnostic(
            string modeName,
            int diagnosticMode)
        {
            _fallbackActive = false;
            _nativeActive = false;
            _nativePlaying = false;

            if (
                SystemInfo.graphicsDeviceType !=
                    GraphicsDeviceType.Direct3D12
            )
            {
                Debug.LogError(
                    $"[{Tag}] {modeName} requires Direct3D12."
                );

                yield break;
            }

            if (!KiwiNativeCameraInterop.IsSupported())
            {
                Debug.LogError(
                    $"[{Tag}] {modeName} native plugin is not supported. " +
                    KiwiNativeCameraInterop.LastError
                );

                yield break;
            }

            string[] candidates =
                base.sourceCandidateNames ??
                Array.Empty<string>();

            int sourceIndex =
                FindPreferredSourceIndex(candidates);

            if (
                sourceIndex < 0 &&
                candidates.Length > 0
            )
            {
                sourceIndex = 0;
            }

            if (sourceIndex >= 0)
            {
                base.SelectSource(sourceIndex);
                _nativeSourceName =
                    base.sourceName;
            }
            else
            {
                _nativeSourceName =
                    "UGREEN Camera 4K";
            }

            int width = _preferredWidth;
            int height = _preferredHeight;
            int frameRate = _preferredFrameRate;

            Debug.Log(
                $"[{Tag}] {modeName} Start camera='{_nativeSourceName}' " +
                $"profile={width}x{height}@{frameRate} stage={diagnosticMode} " +
                $"sharedTier={KiwiNativeCameraInterop.SharedResourceCompatibilityTier}"
            );

            bool started = false;

            try
            {
                started =
                    KiwiNativeCameraInterop.StartDiagnostic(
                        _nativeSourceName,
                        width,
                        height,
                        frameRate,
                        diagnosticMode
                    );
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[{Tag}] {modeName} start exception: " +
                    exception
                );
            }

            if (!started)
            {
                Debug.LogError(
                    $"[{Tag}] {modeName} start failed. " +
                    KiwiNativeCameraInterop.LastError
                );

                KiwiNativeCameraInterop.Stop();
                yield break;
            }

            _renderEventFunc =
                KiwiNativeCameraInterop.RenderEventFunc;

            _presentEventBase =
                KiwiNativeCameraInterop.PresentEventBase;

            RenderTexture diagnosticTarget =
                new RenderTexture(
                    320,
                    180,
                    0,
                    RenderTextureFormat.ARGB32
                )
                {
                    name =
                        "KiwiNativeCameraReverseTextureDiagnostic"
                };

            diagnosticTarget.Create();

            ulong lastSequence = 0;
            int presented = 0;
            int unityFrames = 0;
            bool loggedWrapper = false;
            bool loggedBlit = false;
            bool loggedEvent = false;

            const int targetPresentedFrames = 180;
            const int timeoutUnityFrames = 900;

            try
            {
                while (
                    unityFrames < timeoutUnityFrames &&
                    presented < targetPresentedFrames
                )
                {
                    if (
                        KiwiNativeCameraInterop
                            .TryRequestLatestPresent(
                                out int slotIndex,
                                out ulong sequence,
                                out long hostTicks,
                                out IntPtr resource
                            ) &&
                        sequence > lastSequence &&
                        resource != IntPtr.Zero &&
                        slotIndex >= 0 &&
                        slotIndex < _nativeTextures.Length
                    )
                    {
                        if (_nativeTextures[slotIndex] == null)
                        {
                            _nativeTextures[slotIndex] =
                                Texture2D.CreateExternalTexture(
                                    width,
                                    height,
                                    TextureFormat.RGBA32,
                                    false,
                                    true,
                                    resource
                                );

                            _nativeTextures[slotIndex].name =
                                "KiwiReverseSharedRGBA_" +
                                slotIndex;

                            if (!loggedWrapper)
                            {
                                Debug.Log(
                                    $"[{Tag}] {modeName} first external wrapper created " +
                                    $"slot={slotIndex} seq={sequence} " +
                                    "format=RGBA32 linear=true nativeDXGI=R8G8B8A8_UNORM(28)"
                                );

                                loggedWrapper = true;
                            }
                        }

                        Graphics.Blit(
                            _nativeTextures[slotIndex],
                            diagnosticTarget
                        );

                        if (!loggedBlit)
                        {
                            Debug.Log(
                                $"[{Tag}] {modeName} first Graphics.Blit queued."
                            );

                            loggedBlit = true;
                        }

                        if (_renderEventFunc != IntPtr.Zero)
                        {
                            GL.IssuePluginEvent(
                                _renderEventFunc,
                                _presentEventBase +
                                    slotIndex
                            );

                            if (!loggedEvent)
                            {
                                Debug.Log(
                                    $"[{Tag}] {modeName} first release event queued."
                                );

                                loggedEvent = true;
                            }
                        }

                        lastSequence = sequence;
                        _latestPresentedSequence =
                            sequence;
                        _latestPresentedHostTicks =
                            hostTicks;
                        presented++;
                    }

                    if (unityFrames % 120 == 0)
                    {
                        Debug.Log(
                            $"[{Tag}] {modeName} progress " +
                            $"capture={KiwiNativeCameraInterop.CaptureFrameCount} " +
                            $"presented={presented} " +
                            $"dropped={KiwiNativeCameraInterop.DroppedFrameCount} " +
                            $"producerFenceCompleted=" +
                            KiwiNativeCameraInterop.ProducerCompletedFenceValue
                        );
                    }

                    unityFrames++;
                    yield return null;
                }

                Debug.Log(
                    $"[{Tag}] {modeName} RESULT " +
                    $"capture={KiwiNativeCameraInterop.CaptureFrameCount} " +
                    $"presented={presented} " +
                    $"dropped={KiwiNativeCameraInterop.DroppedFrameCount} " +
                    $"producerFenceCompleted=" +
                    KiwiNativeCameraInterop.ProducerCompletedFenceValue +
                    $" unityFrames={unityFrames} status=" +
                    (
                        presented > 0
                            ? "PASS_TEXTURE_CONSUMPTION_ALIVE"
                            : "FAIL_NO_PRESENT"
                    )
                );
            }
            finally
            {
                KiwiNativeCameraInterop.Stop();

                for (
                    int i = 0;
                    i < _nativeTextures.Length;
                    ++i)
                {
                    if (_nativeTextures[i] != null)
                    {
                        UnityEngine.Object.Destroy(
                            _nativeTextures[i]
                        );

                        _nativeTextures[i] = null;
                    }
                }

                _nativeTexture = null;

                diagnosticTarget.Release();

                UnityEngine.Object.Destroy(
                    diagnosticTarget
                );

                _renderEventFunc =
                    IntPtr.Zero;

                _latestPresentedSequence = 0;
                _latestPresentedHostTicks = 0;
            }

            yield return null;
            yield return null;
        }

        private IEnumerator TryPlayNative()
        {
            _fallbackActive = false;
            _nativeActive = false;
            _nativePlaying = false;

            string[] candidates =
                base.sourceCandidateNames ??
                Array.Empty<string>();

            int sourceIndex =
                FindPreferredSourceIndex(candidates);

            if (
                sourceIndex < 0 &&
                candidates.Length > 0)
            {
                sourceIndex = 0;
            }

            if (sourceIndex >= 0)
            {
                base.SelectSource(sourceIndex);
                _nativeSourceName =
                    base.sourceName;
            }
            else
            {
                _nativeSourceName =
                    "UGREEN Camera 4K";
            }

            int width =
                _preferredWidth;

            int height =
                _preferredHeight;

            int frameRate =
                _preferredFrameRate;

            resolution =
                new ResolutionStruct(
                    width,
                    height,
                    frameRate);

            bool started = false;

            try
            {
                started =
                    KiwiNativeCameraInterop.Start(
                        _nativeSourceName,
                        width,
                        height,
                        frameRate);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[{Tag}] Native start exception: " +
                    exception.Message);
            }

            if (!started)
            {
                KiwiNativeCameraInterop.Stop();
                yield break;
            }

            CalibrateNativeTimestampDomain();

            _renderEventFunc =
                KiwiNativeCameraInterop.RenderEventFunc;

            _presentEventBase =
                KiwiNativeCameraInterop.PresentEventBase;

            if (_renderEventFunc == IntPtr.Zero)
            {
                Debug.LogWarning(
                    $"[{Tag}] Native render event function is unavailable. " +
                    "Slot lifetime cannot be tracked safely."
                );

                KiwiNativeCameraInterop.Stop();
                yield break;
            }

            // A published sequence can exist a little before its D3D11 producer
            // fence is complete. Production must wait across Unity frames for a
            // PRESENTABLE slot instead of treating one failed non-blocking poll
            // as native startup failure. No CPU/GPU blocking wait is used.
            _nativeActive = true;
            _nativePlaying = true;
            _lastPrepareUnityFrame = -1;
            _nativeTelemetrySessionGeneration =
                KiwiNativeCameraTelemetry.BeginSession(
                    _nativeSourceName,
                    width,
                    height,
                    frameRate);

            const int timeoutFrames = 300;

            for (int i = 0; i < timeoutFrames; ++i)
            {
                PrepareFrameForUnity();

                if (_nativeTexture != null)
                {
                    break;
                }

                if (!KiwiNativeCameraInterop.IsRunning)
                {
                    break;
                }

                yield return null;
            }

            if (_nativeTexture == null)
            {
                Debug.LogWarning(
                    $"[{Tag}] Timed out waiting for first producer-completed " +
                    "native D3D12 texture. " +
                    KiwiNativeCameraInterop.LastError
                );

                KiwiNativeCameraTelemetry.EndSession(
                    _nativeTelemetrySessionGeneration);
                _nativeTelemetrySessionGeneration = 0;
                _nativeActive = false;
                _nativePlaying = false;
                _renderEventFunc = IntPtr.Zero;
                ReleaseNativePresentationTexture();
                _nativeTexture = null;

                KiwiNativeCameraInterop.Stop();
                yield break;
            }

            Debug.Log(
                $"[{Tag}] ACTIVE camera='{_nativeSourceName}' " +
                $"profile={width}x{height}@{frameRate} " +
                "backend=MediaFoundation/NV12/D3D11->" +
                "D3D12CompatibilityReverseSharedRGBA " +
                "texture=RGBA32-linear stablePresentation=True " +
                "orientation=ProviderGpuYNormalized " +
                "acquisition=MFAsyncCaptureA->NeverWaitLatestReplace->SharedNV12Ingest3->ProcessingBProducerPoll " +
                "sync=NonBlockingProducerFence+Fixed3SlotTextureRing " +
                $"timestampCalibrated={_nativeTimestampCalibrationValid}"
            );
        }

        public override IEnumerator Resume()
        {
            if (_nativeActive)
            {
                _nativePlaying = true;
                PrepareFrameForUnity();
                yield break;
            }

            yield return base.Resume();
        }

        public override void Pause()
        {
            if (_nativeActive)
            {
                // Keep the native capture/provider resources alive so Resume
                // does not reopen/re-negotiate the camera. Only consumption is
                // paused. This mirrors WebCamSource lifecycle semantics without
                // disturbing GPU/fence ownership.
                _nativePlaying = false;
                return;
            }

            base.Pause();
        }

        public override void Stop()
        {
            if (_nativeActive || _nativePlaying)
            {
                KiwiNativeCameraInterop.Stop();
            }

            KiwiNativeCameraTelemetry.EndSession(
                _nativeTelemetrySessionGeneration);
            _nativeTelemetrySessionGeneration = 0;

            _nativeActive = false;
            _nativePlaying = false;
            _latestPresentedSequence = 0;
            _latestPresentedHostTicks = 0;
            _lastPrepareUnityFrame = -1;
            _renderEventFunc = IntPtr.Zero;

            _nativeTimestampCalibrationValid = false;
            _nativeQpcAnchorTicks = 0L;
            _managedStopwatchAnchorTicks = 0L;
            _nativeQpcFrequency = 0L;

            _nativeTexture = null;
            ReleaseNativePresentationTexture();

            for (
                int i = 0;
                i < _nativeTextures.Length;
                ++i)
            {
                if (_nativeTextures[i] != null)
                {
                    UnityEngine.Object.Destroy(
                        _nativeTextures[i]);

                    _nativeTextures[i] = null;
                }
            }

            if (_fallbackActive)
            {
                base.Stop();
            }

            _fallbackActive = false;
        }

        public override Texture GetCurrentTexture()
        {
            if (_nativeActive)
            {
                PrepareFrameForUnity();
                return _nativeTexture;
            }

            return base.GetCurrentTexture();
        }

        public void PrepareFrameForUnity()
        {
            if (
                !_nativeActive ||
                !_nativePlaying ||
                _lastPrepareUnityFrame ==
                    Time.frameCount)
            {
                return;
            }

            _lastPrepareUnityFrame =
                Time.frameCount;

            // Publish raw producer counters even when this render frame has no
            // newly presentable slot. This separates MF capture cadence from
            // Unity/display consumption cadence.
            PublishNativeTelemetry();

            if (
                !KiwiNativeCameraInterop
                    .TryRequestLatestPresent(
                        out int slotIndex,
                        out ulong sequence,
                        out long hostTicks,
                        out IntPtr resource))
            {
                return;
            }

            if (
                sequence == 0 ||
                sequence <= _latestPresentedSequence ||
                resource == IntPtr.Zero)
            {
                return;
            }

            if (
                slotIndex < 0 ||
                slotIndex >= _nativeTextures.Length)
            {
                Debug.LogError(
                    $"[{Tag}] Native slot index out of range: " +
                    slotIndex);

                return;
            }

            if (_nativeTextures[slotIndex] == null)
            {
                _nativeTextures[slotIndex] =
                    Texture2D.CreateExternalTexture(
                        _preferredWidth,
                        _preferredHeight,
                        TextureFormat.RGBA32,
                        false,
                        true,
                        resource);

                _nativeTextures[slotIndex].name =
                    "KiwiNativeCameraRGBA_" +
                    slotIndex;
            }

            EnsureNativePresentationTexture(
                _preferredWidth,
                _preferredHeight);

            // Provider-boundary canonicalization only. The native slot is the
            // latest completed producer sample; this GPU Blit copies it in the
            // same Unity frame and folds the D3D/Unity Y-origin correction into
            // that copy. It is not a temporal buffer and adds no smoothing.
            Graphics.Blit(
                _nativeTextures[slotIndex],
                _nativePresentationTexture,
                new Vector2(1f, -1f),
                new Vector2(0f, 1f));

            // Downstream systems see one stable Texture identity for the whole
            // camera session. Native slot rotation remains an internal lifetime
            // detail, so frame cadence cannot be mistaken for camera epoch.
            _nativeTexture =
                _nativePresentationTexture;

            _latestPresentedSequence =
                sequence;

            _latestPresentedHostTicks =
                NormalizeNativeHostTicks(hostTicks);

            // Queue the slot release event after the Blit. The native plugin
            // records Unity's next-frame fence for this slot; no graphics-queue
            // Wait is introduced.
            if (_renderEventFunc != IntPtr.Zero)
            {
                GL.IssuePluginEvent(
                    _renderEventFunc,
                    _presentEventBase +
                        slotIndex);
            }

            PublishNativeTelemetry();
        }

        public bool TryGetLatestPresentedFrame(
            out ulong sequence,
            out long hostTicks)
        {
            sequence =
                _latestPresentedSequence;

            hostTicks =
                _latestPresentedHostTicks;

            return
                _nativeActive &&
                sequence > 0;
        }

        private void EnsureNativePresentationTexture(
            int width,
            int height)
        {
            if (
                _nativePresentationTexture != null &&
                _nativePresentationTexture.width == width &&
                _nativePresentationTexture.height == height &&
                _nativePresentationTexture.IsCreated())
            {
                return;
            }

            ReleaseNativePresentationTexture();

            _nativePresentationTexture =
                new RenderTexture(
                    width,
                    height,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear)
                {
                    name = "KiwiNativeCameraPresentation",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false
                };

            _nativePresentationTexture.Create();
        }

        private void ReleaseNativePresentationTexture()
        {
            if (_nativePresentationTexture == null)
            {
                return;
            }

            if (_nativePresentationTexture.IsCreated())
            {
                _nativePresentationTexture.Release();
            }

            UnityEngine.Object.Destroy(
                _nativePresentationTexture);

            _nativePresentationTexture = null;
        }

        private void CalibrateNativeTimestampDomain()
        {
            _nativeTimestampCalibrationValid = false;
            _nativeQpcAnchorTicks = 0L;
            _managedStopwatchAnchorTicks = 0L;
            _nativeQpcFrequency = 0L;

            try
            {
                long frequency =
                    KiwiNativeCameraInterop.QpcFrequency;

                long nativeBefore =
                    KiwiNativeCameraInterop.QpcNow;

                long managedMiddle =
                    System.Diagnostics.Stopwatch.GetTimestamp();

                long nativeAfter =
                    KiwiNativeCameraInterop.QpcNow;

                if (
                    frequency <= 0L ||
                    nativeBefore <= 0L ||
                    nativeAfter < nativeBefore ||
                    managedMiddle <= 0L)
                {
                    Debug.LogWarning(
                        $"[{Tag}] Native timestamp calibration rejected. " +
                        $"qpcBefore={nativeBefore} qpcAfter={nativeAfter} " +
                        $"qpcFrequency={frequency} managed={managedMiddle}");
                    return;
                }

                _nativeQpcFrequency =
                    frequency;

                _nativeQpcAnchorTicks =
                    nativeBefore +
                    ((nativeAfter - nativeBefore) / 2L);

                _managedStopwatchAnchorTicks =
                    managedMiddle;

                _nativeTimestampCalibrationValid = true;

                Debug.Log(
                    $"[{Tag}] Native timestamp calibration ACTIVE " +
                    $"nativeQpcHz={_nativeQpcFrequency} " +
                    $"managedStopwatchHz={System.Diagnostics.Stopwatch.Frequency}");
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[{Tag}] Native timestamp calibration exception: " +
                    exception.Message);
            }
        }

        private long NormalizeNativeHostTicks(long nativeHostTicks)
        {
            if (
                !_nativeTimestampCalibrationValid ||
                nativeHostTicks <= 0L ||
                _nativeQpcFrequency <= 0L)
            {
                return 0L;
            }

            double nativeDelta =
                (double)nativeHostTicks -
                _nativeQpcAnchorTicks;

            double managedDelta =
                nativeDelta *
                System.Diagnostics.Stopwatch.Frequency /
                _nativeQpcFrequency;

            double mapped =
                _managedStopwatchAnchorTicks +
                managedDelta;

            if (
                double.IsNaN(mapped) ||
                double.IsInfinity(mapped) ||
                mapped <= 0.0 ||
                mapped >= long.MaxValue)
            {
                return 0L;
            }

            return (long)Math.Round(mapped);
        }

        private void PublishNativeTelemetry()
        {
            if (
                !_nativeActive ||
                _nativeTelemetrySessionGeneration <= 0)
            {
                return;
            }

            int presentationTextureId =
                _nativePresentationTexture != null
                    ? _nativePresentationTexture.GetInstanceID()
                    : 0;

            long normalizedSourceHostTicks =
                NormalizeNativeHostTicks(
                    KiwiNativeCameraInterop.LatestSourceHostTicks);

            long normalizedIngestHostTicks =
                NormalizeNativeHostTicks(
                    KiwiNativeCameraInterop.LatestIngestHostTicks);

            long normalizedCaptureHostTicks =
                NormalizeNativeHostTicks(
                    KiwiNativeCameraInterop.LatestCaptureHostTicks);

            ulong nativeSourceFrameCount =
                KiwiNativeCameraInterop.SourceFrameCount;

            ulong nativeIngestAcceptedCount =
                KiwiNativeCameraInterop.IngestAcceptedFrameCount;

            float nativeIngestAcceptedRatio =
                nativeSourceFrameCount > 0UL
                    ? Mathf.Clamp01(
                        nativeIngestAcceptedCount /
                        (float)nativeSourceFrameCount)
                    : 0f;

            KiwiNativeCameraTelemetry.Publish(
                _nativeTelemetrySessionGeneration,
                _nativeSourceName,
                _preferredWidth,
                _preferredHeight,
                _preferredFrameRate,
                presentationTextureId,
                _nativeTimestampCalibrationValid,
                _nativeQpcFrequency,
                KiwiNativeCameraInterop.LatestSourceSequence,
                normalizedSourceHostTicks,
                nativeSourceFrameCount,
                KiwiNativeCameraInterop.LatestSourceIntervalMicroseconds / 1000f,
                KiwiNativeCameraInterop.LatestArrivalIntervalMicroseconds / 1000f,
                KiwiNativeCameraInterop.LatestSourceTimestampIntervalMicroseconds / 1000f,
                KiwiNativeCameraInterop.LatestCallbackCpuMicroseconds / 1000f,
                KiwiNativeCameraInterop.LatestRequestNextCpuMicroseconds / 1000f,
                KiwiNativeCameraInterop.LatestCallbackToRequestNextMicroseconds / 1000f,
                KiwiNativeCameraInterop.SupersededFrameCount,
                KiwiNativeCameraInterop.ProcessingFailureFrameCount,
                KiwiNativeCameraInterop.ProcessingWorkerRunning,
                KiwiNativeCameraInterop.LatestIngestSequence,
                normalizedIngestHostTicks,
                KiwiNativeCameraInterop.IngestCopyFrameCount,
                KiwiNativeCameraInterop.IngestCopyFailureFrameCount,
                KiwiNativeCameraInterop.LatestIngestCopySubmitMicroseconds / 1000f,
                KiwiNativeCameraInterop.IngestCompletedFenceValue,
                KiwiNativeCameraInterop.IngestConsumerCompletedFenceValue,
                KiwiNativeCameraInterop.CaptureDeviceIsolated,
                KiwiNativeCameraInterop.CaptureD3D11MultithreadProtected,
                KiwiNativeCameraInterop.D3D11MultithreadProtected,
                KiwiNativeCameraInterop.LatestProcessingCpuMicroseconds / 1000f,
                KiwiNativeCameraInterop.CaptureGpuWaitCount,
                KiwiNativeCameraInterop.ProcessingGpuWaitCount,
                KiwiNativeCameraInterop.ReadyReplacementFrameCount,
                KiwiNativeCameraInterop.AllSlotsBusyDropFrameCount,
                nativeIngestAcceptedCount,
                nativeIngestAcceptedRatio,
                KiwiNativeCameraInterop.ReadyUnclaimedCount,
                KiwiNativeCameraInterop.OldestReadyAgeMicroseconds / 1000f,
                KiwiNativeCameraInterop.IngestProducerFenceLag,
                KiwiNativeCameraInterop.IngestConsumerFenceLag,
                KiwiNativeCameraInterop.LatestProcessedSourceAgeMicroseconds / 1000f,
                KiwiNativeCameraInterop.LatestCaptureSequence,
                normalizedCaptureHostTicks,
                _latestPresentedSequence,
                _latestPresentedHostTicks,
                KiwiNativeCameraInterop.CaptureFrameCount,
                KiwiNativeCameraInterop.DroppedFrameCount,
                KiwiNativeCameraInterop.PresentedFrameCount,
                KiwiNativeCameraInterop.ProducerCompletedFenceValue);
        }

        private int FindPreferredSourceIndex(
            string[] candidates)
        {
            for (
                int keywordIndex = 0;
                keywordIndex <
                    _preferredDeviceKeywords.Length;
                ++keywordIndex)
            {
                string keyword =
                    _preferredDeviceKeywords[
                        keywordIndex];

                if (string.IsNullOrWhiteSpace(
                        keyword))
                {
                    continue;
                }

                for (
                    int sourceIndex = 0;
                    sourceIndex <
                        candidates.Length;
                    ++sourceIndex)
                {
                    string candidate =
                        candidates[sourceIndex];

                    if (
                        !string.IsNullOrWhiteSpace(
                            candidate) &&
                        candidate.IndexOf(
                            keyword,
                            StringComparison
                                .OrdinalIgnoreCase) >= 0)
                    {
                        return sourceIndex;
                    }
                }
            }

            return -1;
        }
    }
}
