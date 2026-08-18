using System.Collections;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

[DefaultExecutionOrder(-32000)]
[DisallowMultipleComponent]
public sealed class KiwiMobilePermissionGate : MonoBehaviour
{
    public FaceLandmarkerRunner runner;
    [SerializeField] private string status = "Not required";

    private bool _runnerWasEnabled;
    private bool _runnerStateCaptured;
    private bool _permissionResolved;

    public string Status => status;
    public bool PermissionResolved => _permissionResolved;

    private void Awake()
    {
#if UNITY_ANDROID || UNITY_IOS
        if (Application.isEditor)
        {
            _permissionResolved = true;
            return;
        }

        ResolveRunnerIfNeeded();
#endif
    }

    private IEnumerator Start()
    {
#if UNITY_ANDROID || UNITY_IOS
        if (!Application.isEditor)
        {
            ResolveRunnerIfNeeded();
        }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        if (Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Grant();
            yield break;
        }

        status = "Requesting camera permission...";
        bool finished = false;
        bool granted = false;

        PermissionCallbacks callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += _ => { granted = true; finished = true; };
        callbacks.PermissionDenied += _ => { finished = true; };
        callbacks.PermissionDeniedAndDontAskAgain += _ => { finished = true; };
        Permission.RequestUserPermission(Permission.Camera, callbacks);

        while (!finished)
        {
            yield return null;
        }

        if (granted || Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Grant();
        }
        else
        {
            Deny();
        }
#elif UNITY_IOS && !UNITY_EDITOR
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            status = "Requesting camera permission...";
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        }

        if (Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            Grant();
        }
        else
        {
            Deny();
        }
#else
        _permissionResolved = true;
        yield break;
#endif
    }


    private void OnApplicationFocus(bool hasFocus)
    {
#if UNITY_ANDROID || UNITY_IOS
        if (Application.isEditor || !hasFocus)
        {
            return;
        }

        ResolveRunnerIfNeeded();
        if (runner == null)
        {
            return;
        }

        bool granted = HasCameraPermission();
        if (granted)
        {
            if (!runner.enabled && _runnerWasEnabled)
            {
                runner.enabled = true;
            }
            status = "Camera permission granted";
            _permissionResolved = true;
        }
        else if (_permissionResolved)
        {
            runner.enabled = false;
            status = "Camera permission denied";
        }
#endif
    }

    private void ResolveRunnerIfNeeded()
    {
        if (runner == null)
        {
            runner = FindFirstObjectByType<FaceLandmarkerRunner>();
        }

        if (runner == null || _runnerStateCaptured)
        {
            return;
        }

        _runnerWasEnabled = runner.enabled;
        _runnerStateCaptured = true;

#if UNITY_ANDROID || UNITY_IOS
        if (!Application.isEditor && !HasCameraPermission())
        {
            runner.enabled = false;
        }
#endif
    }


    private bool HasCameraPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return Permission.HasUserAuthorizedPermission(Permission.Camera);
#elif UNITY_IOS && !UNITY_EDITOR
        return Application.HasUserAuthorization(UserAuthorization.WebCam);
#else
        return true;
#endif
    }

    private void Grant()
    {
        ResolveRunnerIfNeeded();
        status = "Camera permission granted";
        _permissionResolved = true;

        if (runner != null && _runnerWasEnabled)
        {
            runner.enabled = true;
        }
    }

    private void Deny()
    {
        ResolveRunnerIfNeeded();
        status = "Camera permission denied";
        _permissionResolved = true;

        if (runner != null)
        {
            runner.enabled = false;
        }

        Debug.LogWarning("[KiwiAvatarSystem] Camera permission was denied. Face tracking remains stopped.");
    }
}
