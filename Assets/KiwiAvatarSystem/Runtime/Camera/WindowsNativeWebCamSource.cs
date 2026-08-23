using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mediapipe.Unity
{
    /// <summary>
    /// Windows-only low-latency camera source.
    ///
    /// Primary path:
    /// Media Foundation native NV12 1080p60
    /// -> D3D11 GPU copy + NV12-to-RGBA compute
    /// -> 3 fixed shared GPU slots
    /// -> Unity D3D12 queue fence wait
    /// -> one stable Unity Texture2D wrapper.
    ///
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
        private Texture2D _nativeTexture;

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
            if (
                SystemInfo.graphicsDeviceType ==
                    GraphicsDeviceType.Direct3D12 &&
                KiwiNativeCameraInterop.IsSupported())
            {
                yield return TryPlayNative();

                if (_nativeActive)
                {
                    yield break;
                }
            }
#endif

            _fallbackActive = true;

            Debug.LogWarning(
                $"[{Tag}] Native D3D12 camera path unavailable. " +
                "Falling back to WebCamTexture. " +
                KiwiNativeCameraInterop.LastError);

            yield return base.Play();
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

            _renderEventFunc =
                KiwiNativeCameraInterop.RenderEventFunc;

            _presentEventBase =
                KiwiNativeCameraInterop.PresentEventBase;

            const int timeoutFrames = 300;

            for (int i = 0; i < timeoutFrames; ++i)
            {
                if (
                    KiwiNativeCameraInterop
                        .LatestCaptureSequence > 0)
                {
                    break;
                }

                yield return null;
            }

            if (
                KiwiNativeCameraInterop
                    .LatestCaptureSequence == 0)
            {
                Debug.LogWarning(
                    $"[{Tag}] Timed out waiting for first native frame. " +
                    KiwiNativeCameraInterop.LastError);

                KiwiNativeCameraInterop.Stop();
                yield break;
            }

            _nativeActive = true;
            _nativePlaying = true;

            PrepareFrameForUnity();

            if (_nativeTexture == null)
            {
                Debug.LogWarning(
                    $"[{Tag}] First native D3D12 texture was not published. " +
                    KiwiNativeCameraInterop.LastError);

                _nativeActive = false;
                _nativePlaying = false;

                KiwiNativeCameraInterop.Stop();
                yield break;
            }

            Debug.Log(
                $"[{Tag}] ACTIVE camera='{_nativeSourceName}' " +
                $"profile={width}x{height}@{frameRate} " +
                "backend=MediaFoundation/NV12/D3D11->D3D12SharedRGBA");
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

            _nativeActive = false;
            _nativePlaying = false;
            _latestPresentedSequence = 0;
            _latestPresentedHostTicks = 0;
            _lastPrepareUnityFrame = -1;
            _renderEventFunc = IntPtr.Zero;

            if (_nativeTexture != null)
            {
                UnityEngine.Object.Destroy(
                    _nativeTexture);

                _nativeTexture = null;
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
                resource == IntPtr.Zero)
            {
                return;
            }

            if (_nativeTexture == null)
            {
                _nativeTexture =
                    Texture2D.CreateExternalTexture(
                        _preferredWidth,
                        _preferredHeight,
                        TextureFormat.RGBA32,
                        false,
                        false,
                        resource);

                _nativeTexture.name =
                    "KiwiNativeCameraRGBA";
            }
            else
            {
                _nativeTexture
                    .UpdateExternalTexture(
                        resource);
            }

            _latestPresentedSequence =
                sequence;

            _latestPresentedHostTicks =
                hostTicks;

            if (_renderEventFunc != IntPtr.Zero)
            {
                GL.IssuePluginEvent(
                    _renderEventFunc,
                    _presentEventBase +
                        slotIndex);
            }
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
