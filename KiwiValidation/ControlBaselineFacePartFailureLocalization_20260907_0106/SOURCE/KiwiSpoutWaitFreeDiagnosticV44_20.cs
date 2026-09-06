using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// KiwiAvatarSystem v44.20
/// Read-only KlakSpout wait-free inverted NT-handle bridge telemetry.
///
/// This component never touches rendering queues, resource states, tracking,
/// camera capture, or synchronization. It only polls native counters.
/// </summary>
internal sealed class KiwiSpoutWaitFreeDiagnosticV44_20 : MonoBehaviour
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    private const string NativeLibrary = "KlakSpout";

    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Winapi)]
    private static extern int KiwiSpoutDiagGetPluginLoaded();

    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Winapi)]
    private static extern int KiwiSpoutDiagGetState();

    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Winapi)]
    private static extern int KiwiSpoutDiagGetStage();

    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Winapi)]
    private static extern int KiwiSpoutDiagGetLastHResult();

    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Winapi)]
    private static extern ulong KiwiSpoutDiagGetSenderCreateCount();

    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Winapi)]
    private static extern ulong KiwiSpoutDiagGetSubmitCount();

    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Winapi)]
    private static extern ulong KiwiSpoutDiagGetPublishCount();

    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Winapi)]
    private static extern ulong KiwiSpoutDiagGetDiscardCount();

    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Winapi)]
    private static extern ulong KiwiSpoutDiagGetNoFreeSkipCount();
#endif

    private float _nextPoll;
    private bool _nativeUnavailable;
    private int _lastState = int.MinValue;
    private ulong _lastSubmit = ulong.MaxValue;
    private ulong _lastPublish = ulong.MaxValue;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (FindFirstObjectByType<KiwiSpoutWaitFreeDiagnosticV44_20>() != null)
            return;

        var go = new GameObject("__KiwiSpoutWaitFreeDiagnosticV44_20");
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.DontSave;
        go.AddComponent<KiwiSpoutWaitFreeDiagnosticV44_20>();
#endif
    }

    private void Awake()
    {
        _nextPoll = Time.unscaledTime + 1.0f;
    }

    private void Update()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (_nativeUnavailable || Time.unscaledTime < _nextPoll)
            return;

        _nextPoll = Time.unscaledTime + 2.0f;
        PollAndLog(false);
#endif
    }

    private void OnApplicationQuit()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (!_nativeUnavailable)
            PollAndLog(true);
#endif
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    private void PollAndLog(bool final)
    {
        try
        {
            int loaded = KiwiSpoutDiagGetPluginLoaded();
            int state = KiwiSpoutDiagGetState();
            int stage = KiwiSpoutDiagGetStage();
            int hr = KiwiSpoutDiagGetLastHResult();

            ulong senderCreate = KiwiSpoutDiagGetSenderCreateCount();
            ulong submit = KiwiSpoutDiagGetSubmitCount();
            ulong publish = KiwiSpoutDiagGetPublishCount();
            ulong discard = KiwiSpoutDiagGetDiscardCount();
            ulong noFreeSkip = KiwiSpoutDiagGetNoFreeSkipCount();

            bool changed =
                state != _lastState ||
                submit != _lastSubmit ||
                publish != _lastPublish;

            if (changed || final)
            {
                string line =
                    $"[KiwiSpoutV44_20] final={(final ? 1 : 0)} " +
                    $"pluginLoaded={loaded} state={state}({StateName(state)}) " +
                    $"stage={stage}({StageName(stage)}) " +
                    $"hr=0x{unchecked((uint)hr):X8} senderCreate={senderCreate} " +
                    $"submit={submit} publish={publish} discard={discard} " +
                    $"noFreeSkip={noFreeSkip}";

                if (state < 0)
                    Debug.LogError(line);
                else
                    Debug.Log(line);

                _lastState = state;
                _lastSubmit = submit;
                _lastPublish = publish;
            }
        }
        catch (DllNotFoundException ex)
        {
            _nativeUnavailable = true;
            Debug.LogError($"[KiwiSpoutV44_20] DllNotFound: {ex.Message}");
        }
        catch (EntryPointNotFoundException ex)
        {
            _nativeUnavailable = true;
            Debug.LogError($"[KiwiSpoutV44_20] EntryPointNotFound: {ex.Message}");
        }
        catch (Exception ex)
        {
            _nativeUnavailable = true;
            Debug.LogError($"[KiwiSpoutV44_20] telemetry exception: {ex}");
        }
    }

    private static string StateName(int state)
    {
        switch (state)
        {
            case 0: return "UNLOADED_OR_IDLE";
            case 1: return "PLUGIN_LOADED";
            case 10: return "SENDER_CONSTRUCTED";
            case 20: return "BRIDGE_INIT";
            case 30: return "READY";
            case -1: return "FAILED";
            default: return "UNKNOWN";
        }
    }

    private static string StageName(int stage)
    {
        switch (stage)
        {
            case 0: return "NONE";
            case 101: return "BRIDGE_DEVICE_INIT";
            case 106: return "CREATE_COMMAND_ALLOCATOR";
            case 107: return "CREATE_COMMAND_LIST";
            case 108: return "INITIAL_CLOSE_COMMAND_LIST";
            case 109: return "CREATE_D3D11_EVENT_QUERY";
            case 110: return "D3D11_CREATE_NT_TEXTURE";
            case 111: return "QUERY_IDXGIRESOURCE1";
            case 112: return "D3D11_CREATE_NT_HANDLE";
            case 113: return "D3D12_OPEN_D3D11_HANDLE";
            case 114: return "D3D12_IMPORTED_DESC_MISMATCH";
            case 201: return "D3D11_EVENT_GETDATA";
            case 202: return "RESET_COMMAND_ALLOCATOR";
            case 203: return "RESET_COMMAND_LIST";
            case 204: return "CLOSE_COMMAND_LIST";
            case 205: return "UNITY_EXECUTE_COMMAND_LIST";
            case 206: return "SOURCE_COPY_COMPATIBILITY";
            default: return "UNKNOWN";
        }
    }
#endif
}
