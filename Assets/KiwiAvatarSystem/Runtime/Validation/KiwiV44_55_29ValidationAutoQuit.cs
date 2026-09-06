using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;

internal sealed class KiwiV44_55_29ValidationAutoQuit : MonoBehaviour
{
    private const string EnableEnvironment =
        "KIWI_V44_55_29_AUTO_QUIT";

    private const string TimeoutEnvironment =
        "KIWI_V44_55_29_AUTO_QUIT_TIMEOUT_SEC";

    private const string Contract =
        "KIWI_V44_55_29_VALIDATION_AUTO_QUIT_AFTER_V27_COMPLETE";

    private static bool _installed;
    private DateTime _startedUtc;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed || !IsEnabled())
        {
            return;
        }

        _installed = true;
        GameObject host = new GameObject(
            "[Kiwi] v44.55.29 Validation Auto Quit");
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiV44_55_29ValidationAutoQuit>();
    }

    private static bool IsEnabled()
    {
        string value = Environment.GetEnvironmentVariable(
            EnableEnvironment);

        return Debug.isDebugBuild &&
            (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerator Start()
    {
        _startedUtc = DateTime.UtcNow;
        double timeoutSeconds = ReadTimeoutSeconds();
        string directory = Path.Combine(
            Application.persistentDataPath,
            "KiwiFrameBottleneck");

        Debug.Log(
            "[KiwiV44_55_29AutoQuit] contract=" + Contract +
            " timeoutSeconds=" +
            timeoutSeconds.ToString("F0", CultureInfo.InvariantCulture));

        double startedRealtime = Time.realtimeSinceStartupAsDouble;

        while (
            Time.realtimeSinceStartupAsDouble - startedRealtime <
            timeoutSeconds)
        {
            if (HasFreshCompletedV27Report(directory))
            {
                Debug.Log(
                    "[KiwiV44_55_29AutoQuit] V27_COMPLETE_DETECTED " +
                    "exitCode=0");

                // Allow the report/csv/log flush to settle before a normal quit.
                yield return new WaitForSecondsRealtime(1.0f);
                Application.Quit(0);
                yield break;
            }

            yield return new WaitForSecondsRealtime(0.25f);
        }

        Debug.LogError(
            "[KiwiV44_55_29AutoQuit] TIMEOUT_BEFORE_V27_COMPLETE " +
            "exitCode=29");
        Application.Quit(29);
    }

    private bool HasFreshCompletedV27Report(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(
                directory,
                "KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_*.txt",
                SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return false;
        }

        for (int i = 0; i < files.Length; i++)
        {
            string path = files[i];
            try
            {
                if (File.GetLastWriteTimeUtc(path) < _startedUtc)
                {
                    continue;
                }

                string text = File.ReadAllText(path);
                if (
                    text.IndexOf(
                        "status=COMPLETE",
                        StringComparison.Ordinal) >= 0 ||
                    text.IndexOf(
                        "status=COMPLETE_WITH_COVERAGE",
                        StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            catch
            {
                // The writer may still be flushing this file. Retry next tick.
            }
        }

        return false;
    }

    private static double ReadTimeoutSeconds()
    {
        string value = Environment.GetEnvironmentVariable(
            TimeoutEnvironment);

        double parsed;
        if (
            double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out parsed) &&
            parsed >= 60.0 &&
            parsed <= 600.0)
        {
            return parsed;
        }

        return 240.0;
    }
}
