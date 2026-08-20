#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class KiwiPrecisionTrackingInstaller
{
    public const string PrecisionVersion = "1.0.0";

    private const string FaceRunnerFileName = "FaceLandmarkerRunner.cs";
    private const string KiwiMotionFileName = "KiwiFaceMotion.cs";

    private const string FaceRunnerTemplatePath =
        "Assets/KiwiAvatarSystem/TrackingTemplates/FaceLandmarkerRunner_v3.5.0.cs.txt";

    private const string KiwiMotionTemplatePath =
        "Assets/KiwiAvatarSystem/TrackingTemplates/KiwiFaceMotion_v3.5.0.cs.txt";

    // Known safe tracking-core sources. Version 1.0.0 may upgrade prior official
    // releases. Unknown custom edits are never auto-overwritten.
    private static readonly string[] FaceRunnerKnownSourceSha256 =
    {
        "3dea1746732fb8e23415d1477b0cd87e0e40fbfcc63a8a1929614e938ac668a5",
        "e6f0f8fea03f0d6fed42e589d58c5c4422b836c5fe05397e4aa05839afae2e3e",
        "d6309fb071f3ade986f3491e37b552c289a793cbd251299a6cad59c2657012f4",
        "3d7f3f9cf05264e858e52d4348ff8f2c3452d329d2e7307532b469f46319d85a",
        "a34d628821bf27510f389e030a86052620b88c062472dcdf019f6b3b3d3b0172",
        "d4bf1f461f01ff462fd45ee6f20ee718c5648d9391d76699db17a8ae89898917",
        "1d30a6f09488a75cb6332e3be37dd66aceb6e79e3f40b0dce4cbd668c70f1372",
        "e221d00f17640dbedd1094f5f90d4d0ef7745eda8ead9f72b34a41d914c24879",
        "471e08e5f3bca9621289faa82d5c1d036ed3664d90768fc6ed83618acc3629f9",
        "701d8b0427953cd2dd1f2dc486d2d71b6dc29aeaf76a4180d945cc25bc9f7c8b",
        "042b544665377e9c3ee6e8f142d9dbb390d74175a06ab1b758231f123f854fc8",
        "5dc06fc136ec750793a35eb4023d9de95ef13b8dfb1431d7c8af47034b62f2ea",
        "c6acabf46c63e3494333b8108d7e6ea7398f0d8579fc17566a74bb41c682aa6b",
        "7c2b73c8ad9f76bf89b6f885a0067afcdab0b36083cf3e7b39cc5bf1f9be4534",
        "976e7d5df272c05c4ff18297ff72f113d699ebc63817c0bdf17356167b16e2d0"
    };

    private static readonly string[] KiwiMotionKnownSourceSha256 =
    {
        "5d8d64a7860d668991594688a33660699e04d42dbf4cfc966eddc77cf5e0dcaa",
        "38dd8f356cfac4de1ba2316bdf08488a3e50405ca24e2c40403373fdb22fd439",
        "8b1176b3d03f9fe160ad8c022c38bcb6ab9defe71dad24b9cabcdd64b0ffad44",
        "edfaad2221480ef0e2cf6a8634b893003eed5d1d81cd6421efba505085413bdc",
        "68e12f0b584be832c81bb490c2a86778571a8a21237f119bd10e8b9d834430b8",
        "6b35d3f6768ed7aa61d5416891b61e141155f50eb934ae5b35521a5bfd0ccf4b",
        "9313c740fe209b521a829b0798daa8cb15d0e30d6c8afb6ffd6a3657420888d6",
        "b8de0d04221b7c6b3e05ed0ce43264470568038b06ba951e21f38dbb2a7ac56d",
        "85df919bc0eacda40dc782c8516903665935196d7298717295856bbd9d8a364d",
        "ac9d703e0976722be260b19ee72ad8172fc19ac35daa4d6f13beb21dbeea1d2c",
        "be98e8d7f61d16d27c8bcaf35f20aa5e8c51ffb430fe13ffec30e5fa91090073",
        "66d3b13fc91c5085c399421720436dc10a07856726bc13bdad06d774cc897a47",
        "14b76b95aa853faafcd8d1d6c2967589f0785c835e0e3bba7ff921bd08d95f78",
        "4fb33dfadc5cf19772634e0a35fbe961aca80e592c16bfe47d39d23870f29c7d",
        "025f28ce156c6c299301f01f39bddf1b91566fbd61a64a52e9dafe389e371df3",
        "720a50985d779480bdcf5ab791b7df7349536956aa3692f164dc85843f392156",
        "ca54eba549e635611dc6013cb410543b981dac40c6847e2978a0ded9631c07cd",
        "3e493fdaac5f5000f4f7b2a6a7e1d02bda367a8308cbe8a5764395b864339665",
        "a2331215d90bb6c4fc362d81dfcd1bc79b7ef0d78316100ae6e4b6525921bea9",
        "32564b1d74baac6b33fe675187444d8b108636829ca4d758a16a392a718ac70c",
        "62cdb9b5bd19dce1813777604b6228112e1e3cf206e9e738f44f475489baac80",
        "faf647e5d2c93b8fa7c1d279623d2c1e0ed6f76a805e31d7ff09a86f5068fc76",
        "bbf82355b0132b8a658fdaf909e02727614661f0cb554ae1f0688470d916e378",
        "7a3de96fab1415485a38a4ceb2554d8bbfac13089c480912f01d042f3309d3d5",
        "91ed1255cf7863e59cd820c37e106118c43a267c0bea4731e577c76c73318110",
        "d4226acda51d298eff222940e4cd808fe1ae61b76ae64a8fd3aa66faf264dd78",
        "f099015878e780114c3de863aa02010e6bfde1dff00a9c20f41780a1735228a5",
        "84e3778b8d310b9f43623af387d2e79b73ba871ab5f7fbc1f08cf43fdaca9a68"
    };

    private const string FaceRunnerTargetSha256 =
        "A682BAE9E08362FAFFADBEA296A1DA7D1CE87480A0B8E866C2A501F860A7908C";

    private const string KiwiMotionTargetSha256 =
        "CE46B434BF031089284182537094E5A0C027D754665ED8CCC9C0B3626EBE3285";

    private const string SessionKey =
        "KiwiPrecisionTrackingInstaller.v1.0.0.AutoChecked";

    private const string BackupRoot =
        "Assets/KiwiAvatarSystem/Backups";

    private enum PatchState
    {
        Missing,
        KnownSource,
        Target,
        Modified
    }

    private sealed class TargetInfo
    {
        public string displayName;
        public string assetPath;
        public string fullPath;
        public string templatePath;
        public string templateFullPath;
        public string[] knownSourceHashes;
        public string targetHash;
        public PatchState state;
        public string currentHash;
    }

    static KiwiPrecisionTrackingInstaller()
    {
        EditorApplication.delayCall += AutoCheckOnce;
    }

    [MenuItem("Kiwi VTuber/Precision Tracking/Apply v1.0.0 Safe Upgrade")]
    public static void ApplySafeUpgradeMenu()
    {
        ApplyKnownSafePatch(true);
    }

    [MenuItem("Kiwi VTuber/Precision Tracking/Validate v1.0.0")]
    public static void ValidateMenu()
    {
        TargetInfo face = BuildTarget(
            FaceRunnerFileName,
            FaceRunnerTemplatePath,
            FaceRunnerKnownSourceSha256,
            FaceRunnerTargetSha256
        );

        TargetInfo motion = BuildTarget(
            KiwiMotionFileName,
            KiwiMotionTemplatePath,
            KiwiMotionKnownSourceSha256,
            KiwiMotionTargetSha256
        );

        string message =
            "Landmarker Primary Hybrid Precision Tracking v" + PrecisionVersion + "\n\n" +
            Describe(face) + "\n" +
            Describe(motion) + "\n\n" +
            "Target means the precision tracking core is installed.";

        EditorUtility.DisplayDialog(
            "Kiwi Precision Tracking",
            message,
            "OK"
        );
    }

    [MenuItem("Kiwi VTuber/Precision Tracking/Force Upgrade With Backup...")]
    public static void ForceUpgradeMenu()
    {
        bool confirmed = EditorUtility.DisplayDialog(
            "Kiwi Precision Tracking",
            "This will replace the currently discovered FaceLandmarkerRunner.cs and KiwiFaceMotion.cs after creating text backups. Use this only if you intentionally want to replace custom edits.",
            "Backup + Replace",
            "Cancel"
        );

        if (!confirmed)
        {
            return;
        }

        ApplyPatchInternal(true, true);
    }

    private static void AutoCheckOnce()
    {
        if (
            Application.isPlaying ||
            EditorApplication.isCompiling
        )
        {
            EditorApplication.delayCall += AutoCheckOnce;
            return;
        }

        if (SessionState.GetBool(SessionKey, false))
        {
            return;
        }

        SessionState.SetBool(SessionKey, true);
        ApplyKnownSafePatch(false);
    }

    private static void ApplyKnownSafePatch(bool showDialog)
    {
        ApplyPatchInternal(false, showDialog);
    }

    private static void ApplyPatchInternal(
        bool force,
        bool showDialog)
    {
        TargetInfo face = BuildTarget(
            FaceRunnerFileName,
            FaceRunnerTemplatePath,
            FaceRunnerKnownSourceSha256,
            FaceRunnerTargetSha256
        );

        TargetInfo motion = BuildTarget(
            KiwiMotionFileName,
            KiwiMotionTemplatePath,
            KiwiMotionKnownSourceSha256,
            KiwiMotionTargetSha256
        );

        if (
            face.state == PatchState.Target &&
            motion.state == PatchState.Target
        )
        {
            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "Kiwi Precision Tracking",
                    "v" + PrecisionVersion + " is already installed.",
                    "OK"
                );
            }

            return;
        }

        if (
            face.state == PatchState.Missing ||
            motion.state == PatchState.Missing
        )
        {
            ReportFailure(
                "Required tracking scripts or packaged templates were not found.\n\n" +
                Describe(face) + "\n" +
                Describe(motion),
                showDialog
            );
            return;
        }

        if (
            !force &&
            (
                !IsKnownSafeState(face.state) ||
                !IsKnownSafeState(motion.state)
            )
        )
        {
            // Never overwrite an unknown/custom tracking core automatically.
            string message =
                "Automatic precision upgrade was skipped because at least one tracking script differs from the known safe source/target. No files were changed.\n\n" +
                Describe(face) + "\n" +
                Describe(motion) + "\n\n" +
                "Use Validate first. Force Upgrade is available only when you intentionally want to replace custom edits.";

            Debug.LogWarning("[KiwiPrecisionTracking] " + message);

            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "Kiwi Precision Tracking",
                    message,
                    "OK"
                );
            }

            return;
        }

        byte[] faceTemplate = File.ReadAllBytes(face.templateFullPath);
        byte[] motionTemplate = File.ReadAllBytes(motion.templateFullPath);

        if (
            !HashBytes(faceTemplate).Equals(
                FaceRunnerTargetSha256,
                StringComparison.OrdinalIgnoreCase
            ) ||
            !HashBytes(motionTemplate).Equals(
                KiwiMotionTargetSha256,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            ReportFailure(
                "Packaged precision templates failed their integrity check. No project files were changed.",
                showDialog
            );
            return;
        }

        string backupFolder = CreateBackupFolder();

        byte[] originalFace = File.ReadAllBytes(face.fullPath);
        byte[] originalMotion = File.ReadAllBytes(motion.fullPath);

        bool writeFace =
            force ||
            face.state == PatchState.KnownSource;

        bool writeMotion =
            force ||
            motion.state == PatchState.KnownSource;

        try
        {
            BackupAsText(face, backupFolder);
            BackupAsText(motion, backupFolder);

            // Known Target + KnownSource is also recoverable: only the source side is written.
            if (writeFace)
            {
                File.WriteAllBytes(face.fullPath, faceTemplate);
            }

            if (writeMotion)
            {
                File.WriteAllBytes(motion.fullPath, motionTemplate);
            }

            // Validate the pair before Unity recompiles either script.
            if (
                !HashFile(face.fullPath).Equals(
                    FaceRunnerTargetSha256,
                    StringComparison.OrdinalIgnoreCase
                ) ||
                !HashFile(motion.fullPath).Equals(
                    KiwiMotionTargetSha256,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new InvalidDataException(
                    "Tracking core verification failed after write."
                );
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            Debug.Log(
                "[KiwiPrecisionTracking] v" + PrecisionVersion +
                " installed. Backups: " + backupFolder
            );

            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "Kiwi Precision Tracking",
                    "Landmarker Primary Hybrid Precision Tracking v" + PrecisionVersion +
                    " was installed.\n\nBackups: " + backupFolder +
                    "\n\nUnity will recompile the updated tracking scripts.",
                    "OK"
                );
            }
        }
        catch (Exception ex)
        {
            string rollbackMessage;

            try
            {
                // Transactional rollback: restore both originals if either write/verify fails.
                File.WriteAllBytes(face.fullPath, originalFace);
                File.WriteAllBytes(motion.fullPath, originalMotion);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                rollbackMessage = "Original tracking scripts were restored.";
            }
            catch (Exception rollbackEx)
            {
                Debug.LogException(rollbackEx);
                rollbackMessage =
                    "Automatic rollback also failed. Restore from: " + backupFolder;
            }

            Debug.LogException(ex);
            ReportFailure(
                "Upgrade failed. " + rollbackMessage +
                "\nBackups: " + backupFolder +
                "\n\n" + ex.Message,
                showDialog
            );
        }
    }

    private static bool IsKnownSafeState(PatchState state)
    {
        return
            state == PatchState.KnownSource ||
            state == PatchState.Target;
    }

    private static TargetInfo BuildTarget(
        string fileName,
        string templatePath,
        string[] knownSourceHashes,
        string targetHash)
    {
        string assetPath = FindUniqueMonoScriptPath(fileName);
        string fullPath = string.IsNullOrEmpty(assetPath)
            ? null
            : AssetPathToFullPath(assetPath);

        string templateFullPath = AssetPathToFullPath(templatePath);

        TargetInfo info = new TargetInfo
        {
            displayName = fileName,
            assetPath = assetPath,
            fullPath = fullPath,
            templatePath = templatePath,
            templateFullPath = templateFullPath,
            knownSourceHashes = knownSourceHashes,
            targetHash = targetHash,
            state = PatchState.Missing,
            currentHash = string.Empty
        };

        if (
            string.IsNullOrEmpty(fullPath) ||
            !File.Exists(fullPath) ||
            !File.Exists(templateFullPath)
        )
        {
            return info;
        }

        info.currentHash = HashFile(fullPath);

        if (info.currentHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase))
        {
            info.state = PatchState.Target;
        }
        else if (ContainsHash(knownSourceHashes, info.currentHash))
        {
            info.state = PatchState.KnownSource;
        }
        else
        {
            info.state = PatchState.Modified;
        }

        return info;
    }

    private static bool ContainsHash(
        string[] hashes,
        string candidate)
    {
        if (
            hashes == null ||
            string.IsNullOrEmpty(candidate)
        )
        {
            return false;
        }

        for (int i = 0; i < hashes.Length; i++)
        {
            if (
                !string.IsNullOrEmpty(hashes[i]) &&
                candidate.Equals(
                    hashes[i],
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return true;
            }
        }

        return false;
    }


    private static string FindUniqueMonoScriptPath(string fileName)
    {
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        string[] guids = AssetDatabase.FindAssets(nameWithoutExtension + " t:MonoScript");
        List<string> matches = new List<string>();
        List<string> semanticMatches = new List<string>();

        string requiredMarker = string.Empty;
        if (string.Equals(fileName, FaceRunnerFileName, StringComparison.OrdinalIgnoreCase))
        {
            requiredMarker = "namespace Mediapipe.Unity.Sample.FaceLandmarkDetection";
        }
        else if (string.Equals(fileName, KiwiMotionFileName, StringComparison.OrdinalIgnoreCase))
        {
            requiredMarker = "public class KiwiFaceMotion";
        }

        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            if (
                string.IsNullOrEmpty(path) ||
                !string.Equals(
                    Path.GetFileName(path),
                    fileName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            matches.Add(path);

            if (string.IsNullOrEmpty(requiredMarker))
            {
                continue;
            }

            try
            {
                string candidateFullPath = AssetPathToFullPath(path);
                if (
                    File.Exists(candidateFullPath) &&
                    File.ReadAllText(candidateFullPath).Contains(requiredMarker)
                )
                {
                    semanticMatches.Add(path);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[KiwiPrecisionTracking] Could not inspect " + path + ": " + ex.Message
                );
            }
        }

        if (semanticMatches.Count == 1)
        {
            return semanticMatches[0];
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            Debug.LogWarning(
                "[KiwiPrecisionTracking] Multiple " + fileName +
                " scripts were found and could not be resolved uniquely. Automatic patching is disabled for safety."
            );
        }

        return string.Empty;
    }

    private static string CreateBackupFolder()
    {
        string backupRootFullPath =
            AssetPathToFullPath(BackupRoot);

        Directory.CreateDirectory(
            backupRootFullPath
        );

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        string assetFolderBase = Path.Combine(
            BackupRoot,
            "PrecisionTracking_v" + PrecisionVersion + "_" + stamp
        ).Replace('\\', '/');

        string assetFolder = assetFolderBase;
        int suffix = 2;
        while (Directory.Exists(AssetPathToFullPath(assetFolder)))
        {
            assetFolder = assetFolderBase + "_" + suffix;
            suffix++;
        }

        Directory.CreateDirectory(
            AssetPathToFullPath(assetFolder)
        );

        AssetDatabase.Refresh();
        return assetFolder;
    }

    private static void BackupAsText(
        TargetInfo info,
        string backupFolder)
    {
        string backupAssetPath = Path.Combine(
            backupFolder,
            info.displayName + ".backup.txt"
        ).Replace('\\', '/');

        File.Copy(
            info.fullPath,
            AssetPathToFullPath(backupAssetPath),
            true
        );
    }

    private static string Describe(TargetInfo info)
    {
        return info.displayName + ": " + info.state +
            (string.IsNullOrEmpty(info.assetPath)
                ? string.Empty
                : "  (" + info.assetPath + ")");
    }

    private static void ReportFailure(
        string message,
        bool showDialog)
    {
        Debug.LogError("[KiwiPrecisionTracking] " + message);

        if (showDialog)
        {
            EditorUtility.DisplayDialog(
                "Kiwi Precision Tracking",
                message,
                "OK"
            );
        }
    }

    private static string AssetPathToFullPath(string assetPath)
    {
        string projectRoot =
            Directory.GetParent(Application.dataPath).FullName;

        return Path.GetFullPath(
            Path.Combine(
                projectRoot,
                assetPath
            )
        );
    }

    private static string HashFile(string path)
    {
        return HashBytes(File.ReadAllBytes(path));
    }

    private static string HashBytes(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash)
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }
    }
}
#endif
