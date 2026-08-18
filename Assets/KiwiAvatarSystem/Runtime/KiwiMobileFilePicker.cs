using System;
using System.Runtime.InteropServices;
using UnityEngine;

[DefaultExecutionOrder(-25000)]
[DisallowMultipleComponent]
public sealed class KiwiMobileFilePicker : MonoBehaviour
{
    private const string ObjectName = "KiwiMobileFilePicker";
    private static KiwiMobileFilePicker _instance;
    private Action<string> _onPicked;
    private Action<string> _onError;
    private bool _busy;

    public static bool IsSupported
    {
        get
        {
#if UNITY_ANDROID || UNITY_IOS
            return !Application.isEditor;
#else
            return false;
#endif
        }
    }

    public static KiwiMobileFilePicker Instance
    {
        get
        {
            if (_instance != null)
            {
                return _instance;
            }

            GameObject existing = GameObject.Find(ObjectName);
            if (existing != null)
            {
                _instance = existing.GetComponent<KiwiMobileFilePicker>();
            }

            if (_instance == null)
            {
                GameObject go = new GameObject(ObjectName);
                _instance = go.AddComponent<KiwiMobileFilePicker>();
            }

            DontDestroyOnLoad(_instance.gameObject);
            return _instance;
        }
    }

    public bool IsBusy => _busy;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        gameObject.name = ObjectName;
        DontDestroyOnLoad(gameObject);
    }

    public void PickVrm(
        long maximumBytes,
        string destinationDirectory,
        Action<string> onPicked,
        Action<string> onError)
    {
        if (_busy)
        {
            onError?.Invoke("The file picker is already open.");
            return;
        }

        if (!IsSupported)
        {
            onError?.Invoke("Native file picker is available only on Android/iOS builds.");
            return;
        }

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            onError?.Invoke("The managed Models directory is unavailable.");
            return;
        }

        _busy = true;
        _onPicked = onPicked;
        _onError = onError;

        try
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (AndroidJavaClass picker = new AndroidJavaClass("com.kiwivtuber.mobile.KiwiFilePicker"))
            {
                picker.CallStatic(
                    "open",
                    gameObject.name,
                    nameof(OnNativeFilePicked),
                    nameof(OnNativeFilePickerError),
                    destinationDirectory,
                    Math.Max(0L, maximumBytes)
                );
            }
#elif UNITY_IOS && !UNITY_EDITOR
            Kiwi_OpenVrmPicker(
                gameObject.name,
                nameof(OnNativeFilePicked),
                nameof(OnNativeFilePickerError),
                destinationDirectory,
                Math.Max(0L, maximumBytes)
            );
#endif
        }
        catch (Exception ex)
        {
            CompleteWithError(ex.Message);
        }
    }

    public void OnNativeFilePicked(string path)
    {
        Action<string> success = _onPicked;
        Action<string> error = _onError;
        ClearCallbacks();

        if (string.IsNullOrWhiteSpace(path))
        {
            error?.Invoke("No file path was returned by the picker.");
            return;
        }

        success?.Invoke(path);
    }

    public void OnNativeFilePickerError(string message)
    {
        CompleteWithError(string.IsNullOrWhiteSpace(message) ? "File selection was cancelled." : message);
    }

    private void CompleteWithError(string message)
    {
        Action<string> callback = _onError;
        ClearCallbacks();
        callback?.Invoke(message);
    }

    private void ClearCallbacks()
    {
        _busy = false;
        _onPicked = null;
        _onError = null;
    }

    public static void CleanupTemporaryResult(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (AndroidJavaClass picker = new AndroidJavaClass("com.kiwivtuber.mobile.KiwiFilePicker"))
            {
                picker.CallStatic("cleanup", path);
            }
#elif UNITY_IOS && !UNITY_EDITOR
            Kiwi_DeleteImportedTemp(path);
#endif
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[KiwiAvatarSystem] Temporary picker cleanup failed: " + ex.Message);
        }
    }

#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern void Kiwi_OpenVrmPicker(
        string gameObjectName,
        string successMethod,
        string errorMethod,
        string destinationDirectory,
        long maximumBytes
    );

    [DllImport("__Internal")]
    private static extern void Kiwi_DeleteImportedTemp(string path);
#endif
}
