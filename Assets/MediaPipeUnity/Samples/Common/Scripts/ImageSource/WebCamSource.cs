// Copyright (c) 2021 homuler
//
// Use of this source code is governed by an MIT-style
// license that can be found in the LICENSE file or at
// https://opensource.org/licenses/MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace Mediapipe.Unity
{
  public class WebCamSource : ImageSource
  {
    private readonly int _preferableDefaultWidth = 1280;
    private readonly string[] _preferredDeviceKeywords;
    private readonly int _preferredProfileWidth;
    private readonly int _preferredProfileHeight;
    private readonly int _preferredProfileFrameRate;

    private const string _TAG = nameof(WebCamSource);

    private readonly ResolutionStruct[] _defaultAvailableResolutions;

    public WebCamSource(int preferableDefaultWidth, ResolutionStruct[] defaultAvailableResolutions)
      : this(
          preferableDefaultWidth,
          defaultAvailableResolutions,
          Array.Empty<string>(),
          0,
          0,
          0)
    {
    }

    public WebCamSource(
      int preferableDefaultWidth,
      ResolutionStruct[] defaultAvailableResolutions,
      string[] preferredDeviceKeywords,
      int preferredProfileWidth,
      int preferredProfileHeight,
      int preferredProfileFrameRate)
    {
      _preferableDefaultWidth = preferableDefaultWidth;
      _defaultAvailableResolutions = defaultAvailableResolutions;
      _preferredDeviceKeywords = preferredDeviceKeywords ?? Array.Empty<string>();
      _preferredProfileWidth = preferredProfileWidth;
      _preferredProfileHeight = preferredProfileHeight;
      _preferredProfileFrameRate = preferredProfileFrameRate;
    }

    private static readonly object _PermissionLock = new object();
    private static bool _IsPermitted = false;

    private WebCamTexture _webCamTexture;
    private WebCamTexture webCamTexture
    {
      get => _webCamTexture;
      set
      {
        if (_webCamTexture != null)
        {
          _webCamTexture.Stop();
        }
        _webCamTexture = value;
      }
    }

    public override int textureWidth => !isPrepared ? 0 : webCamTexture.width;
    public override int textureHeight => !isPrepared ? 0 : webCamTexture.height;

    public override bool isVerticallyFlipped => isPrepared && webCamTexture.videoVerticallyMirrored;
    public override bool isFrontFacing => isPrepared && (webCamDevice is WebCamDevice valueOfWebCamDevice) && valueOfWebCamDevice.isFrontFacing;
    public override RotationAngle rotation => !isPrepared ? RotationAngle.Rotation0 : (RotationAngle)webCamTexture.videoRotationAngle;

    private WebCamDevice? _webCamDevice;
    private WebCamDevice? webCamDevice
    {
      get => _webCamDevice;
      set
      {
        if (_webCamDevice is WebCamDevice valueOfWebCamDevice)
        {
          if (value is WebCamDevice valueOfValue && valueOfValue.name == valueOfWebCamDevice.name)
          {
            // not changed
            return;
          }
        }
        else if (value == null)
        {
          // not changed
          return;
        }
        _webCamDevice = value;
        resolution = GetDefaultResolution();
      }
    }
    public override string sourceName => (webCamDevice is WebCamDevice valueOfWebCamDevice) ? valueOfWebCamDevice.name : null;
    public bool usesPreferredProfile => MatchesPreferredDevice(sourceName);

    private WebCamDevice[] _availableSources;
    private WebCamDevice[] availableSources
    {
      get
      {
        if (_availableSources == null)
        {
          _availableSources = WebCamTexture.devices;
        }

        return _availableSources;
      }
      set => _availableSources = value;
    }

    public override string[] sourceCandidateNames => availableSources?.Select(device => device.name).ToArray();

#pragma warning disable IDE0025
    public override ResolutionStruct[] availableResolutions
    {
      get
      {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        if (webCamDevice is WebCamDevice valueOfWebCamDevice) {
          return valueOfWebCamDevice.availableResolutions.Select(resolution => new ResolutionStruct(resolution)).ToArray();
        }
#endif
        return webCamDevice == null ? null : _defaultAvailableResolutions;
      }
    }
#pragma warning restore IDE0025

    public override bool isPrepared => webCamTexture != null;
    public override bool isPlaying => webCamTexture != null && webCamTexture.isPlaying;

    private IEnumerator Initialize()
    {
      yield return GetPermission();

      if (!_IsPermitted)
      {
        yield break;
      }

      if (webCamDevice != null)
      {
        yield break;
      }

      availableSources = WebCamTexture.devices;

      if (availableSources != null && availableSources.Length > 0)
      {
        int preferredSource = FindPreferredSourceIndex(availableSources);
        webCamDevice = availableSources[preferredSource >= 0 ? preferredSource : 0];
      }
    }

    private IEnumerator GetPermission()
    {
      lock (_PermissionLock)
      {
        if (_IsPermitted)
        {
          yield break;
        }

#if UNITY_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
          Permission.RequestUserPermission(Permission.Camera);
          yield return new WaitForSeconds(0.1f);
        }
#elif UNITY_IOS
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam)) {
          yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        }
#endif

#if UNITY_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
          Debug.LogWarning("Not permitted to use Camera");
          yield break;
        }
#elif UNITY_IOS
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam)) {
          Debug.LogWarning("Not permitted to use WebCam");
          yield break;
        }
#endif
        _IsPermitted = true;

        yield return new WaitForEndOfFrame();
      }
    }

    public override void SelectSource(int sourceId)
    {
      if (sourceId < 0 || sourceId >= availableSources.Length)
      {
        throw new ArgumentException($"Invalid source ID: {sourceId}");
      }

      webCamDevice = availableSources[sourceId];
    }

    public override IEnumerator Play()
    {
      yield return Initialize();
      if (!_IsPermitted)
      {
        throw new InvalidOperationException("Not permitted to access cameras");
      }

      InitializeWebCamTexture();
      webCamTexture.Play();
      yield return WaitForWebCamTexture();

      Debug.Log(
        $"[{_TAG}] Camera='{sourceName}', requested={resolution}, " +
        $"actual={webCamTexture.width}x{webCamTexture.height}, " +
        $"preferredProfile={usesPreferredProfile}"
      );
    }

    public override IEnumerator Resume()
    {
      if (!isPrepared)
      {
        throw new InvalidOperationException("WebCamTexture is not prepared yet");
      }
      if (!webCamTexture.isPlaying)
      {
        webCamTexture.Play();
      }
      yield return WaitForWebCamTexture();
    }

    public override void Pause()
    {
      if (isPlaying)
      {
        webCamTexture.Pause();
      }
    }

    public override void Stop()
    {
      if (webCamTexture != null)
      {
        webCamTexture.Stop();
      }
      webCamTexture = null;
    }

    public override Texture GetCurrentTexture() => webCamTexture;

    private ResolutionStruct GetDefaultResolution()
    {
      var resolutions = availableResolutions;
      if (resolutions == null || resolutions.Length == 0)
      {
        return new ResolutionStruct();
      }

      if (usesPreferredProfile)
      {
        return resolutions.OrderBy(
          value => value,
          new ResolutionStructComparer(
            _preferredProfileWidth,
            _preferredProfileHeight,
            _preferredProfileFrameRate
          )
        ).First();
      }

      return resolutions.OrderBy(
        value => value,
        new ResolutionStructComparer(_preferableDefaultWidth)
      ).First();
    }

    public static bool IsCm831DeviceName(string deviceName)
    {
      return !string.IsNullOrWhiteSpace(deviceName) &&
        (deviceName.IndexOf("CM831", StringComparison.OrdinalIgnoreCase) >= 0 ||
         deviceName.IndexOf("UGREEN", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private bool MatchesPreferredDevice(string deviceName)
    {
      if (string.IsNullOrWhiteSpace(deviceName))
      {
        return false;
      }

      for (int i = 0; i < _preferredDeviceKeywords.Length; i++)
      {
        string keyword = _preferredDeviceKeywords[i];
        if (!string.IsNullOrWhiteSpace(keyword) &&
            deviceName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
        {
          return true;
        }
      }

      return false;
    }

    private int FindPreferredSourceIndex(WebCamDevice[] sources)
    {
      for (int i = 0; i < sources.Length; i++)
      {
        if (MatchesPreferredDevice(sources[i].name))
        {
          return i;
        }
      }

      return -1;
    }

    private void InitializeWebCamTexture()
    {
      Stop();
      if (webCamDevice is WebCamDevice valueOfWebCamDevice)
      {
        webCamTexture = new WebCamTexture(valueOfWebCamDevice.name, resolution.width, resolution.height, (int)resolution.frameRate);
        return;
      }
      throw new InvalidOperationException("Cannot initialize WebCamTexture because WebCamDevice is not selected");
    }

    private IEnumerator WaitForWebCamTexture()
    {
      const int timeoutFrame = 2000;
      var count = 0;
      Debug.Log("Waiting for WebCamTexture to start");
      yield return new WaitUntil(() => count++ > timeoutFrame || webCamTexture.width > 16);

      if (webCamTexture.width <= 16)
      {
        throw new TimeoutException("Failed to start WebCam");
      }
    }

    private class ResolutionStructComparer : IComparer<ResolutionStruct>
    {
      private readonly int _preferableDefaultWidth;
      private readonly int _preferableHeight;
      private readonly double _preferableFrameRate;

      public ResolutionStructComparer(int preferableDefaultWidth)
        : this(preferableDefaultWidth, 0, 0)
      {
      }

      public ResolutionStructComparer(
        int preferableDefaultWidth,
        int preferableHeight,
        double preferableFrameRate)
      {
        _preferableDefaultWidth = preferableDefaultWidth;
        _preferableHeight = preferableHeight;
        _preferableFrameRate = preferableFrameRate;
      }

      public int Compare(ResolutionStruct a, ResolutionStruct b)
      {
        var aDiff = Mathf.Abs(a.width - _preferableDefaultWidth);
        var bDiff = Mathf.Abs(b.width - _preferableDefaultWidth);
        if (aDiff != bDiff)
        {
          return aDiff - bDiff;
        }

        if (_preferableHeight > 0)
        {
          var aHeightDiff = Mathf.Abs(a.height - _preferableHeight);
          var bHeightDiff = Mathf.Abs(b.height - _preferableHeight);
          if (aHeightDiff != bHeightDiff)
          {
            return aHeightDiff - bHeightDiff;
          }
        }
        else if (a.height != b.height)
        {
          // prefer smaller height
          return a.height - b.height;
        }

        if (_preferableFrameRate > 0)
        {
          var aRateDiff = Math.Abs(a.frameRate - _preferableFrameRate);
          var bRateDiff = Math.Abs(b.frameRate - _preferableFrameRate);
          int rateComparison = aRateDiff.CompareTo(bRateDiff);
          if (rateComparison != 0)
          {
            return rateComparison;
          }
        }

        // Lower capture intervals reduce motion-to-photon latency. Prefer the
        // highest device-reported frame rate when width and height are equal.
        return b.frameRate.CompareTo(a.frameRate);
      }
    }
  }
}
