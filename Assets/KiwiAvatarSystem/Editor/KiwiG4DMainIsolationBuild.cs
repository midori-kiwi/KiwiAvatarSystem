#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

// G4-D source candidate. This entry point is intentionally unavailable until
// the matching Unity-created BuildProfile and empty Product-owned root exist.
// It must never fall back to global PlayerSettings or an existing Player folder.
public static class KiwiG4DMainIsolationBuild
{
    internal const string ProfilePath =
        "Assets/KiwiAvatarSystem/BuildProfiles/KiwiG4DMainOnlyIsolation.asset";
    internal const string Company = "MidoriKiwiG4DMainIsolation";
    internal const string Product = "KiwiAvatarSystemG4DMainIsolation";
    private const string IsolationDefine = "KIWI_G4D_MAIN_ONLY_ISOLATION";
    private const string ExeName = "KiwiG4DMainOnlyIsolation.exe";

    private static string _expectedOutput;

    private static string ProjectRoot =>
        Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

    private static string ReservedRoot => Path.GetFullPath(Path.Combine(
        ProjectRoot, "ProjectArtifacts", "Product", "G4D_MainOnlyIsolation"));

    // A non-building inspection entry point. This does not create folders,
    // build a Player, change ProjectSettings, or open the target HKCU values.
    [MenuItem("Tools/Kiwi Avatar System/Product/G4D Read-only isolation preflight")]
    public static void AuditOnly()
    {
        if (Application.platform != RuntimePlatform.WindowsEditor ||
            EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            throw new BuildFailedException("G4D requires Windows x64 Editor target.");
        VerifyProfile(AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath));
        VerifyPreferenceNamespaceUnused();
        RequireSafeExistingDirectory(ReservedRoot);
        Debug.Log("[Kiwi G4D] STATIC_PREFLIGHT_CONFIG_ONLY: completed; " +
                  "not a Build, Runtime or HKCU isolation PASS.");
    }

    // Not exposed as a GUI menu until the exact Unity profile and local guards
    // have been independently validated. Invoke explicitly only after approval.
    public static void Build()
    {
        if (_expectedOutput != null)
            throw new BuildFailedException("G4D build already in progress.");
        if (Application.platform != RuntimePlatform.WindowsEditor ||
            EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            throw new BuildFailedException("G4D requires Windows Editor with active Windows x64 target.");

        BuildProfile profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath);
        VerifyProfile(profile);
        VerifyPreferenceNamespaceUnused();
        RequireSafeExistingDirectory(ReservedRoot);

        string runDirectory = Path.Combine(ReservedRoot,
            "G4D_" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(runDirectory) || File.Exists(runDirectory))
            throw new BuildFailedException("G4D run destination already exists.");

        // The only new filesystem object before BuildPipeline is a unique run
        // directory within the explicitly preflighted Product-owned root.
        Directory.CreateDirectory(runDirectory);
        RequireSafeExistingDirectory(runDirectory);
        if (Directory.GetFileSystemEntries(runDirectory).Length != 0)
            throw new BuildFailedException("G4D run directory is not empty.");

        string output = Path.GetFullPath(Path.Combine(runDirectory, ExeName));
        _expectedOutput = output;
        try
        {
            BuildReport report = BuildPipeline.BuildPlayer(
                new BuildPlayerWithProfileOptions
                {
                    buildProfile = profile,
                    locationPathName = output,
                    options = BuildOptions.Development | BuildOptions.StrictMode
                });

            if (report == null || report.summary.result != BuildResult.Succeeded)
                throw new BuildFailedException("G4D build did not succeed.");

            RequireSafeExistingDirectory(runDirectory);
            if (!File.Exists(output) || !Directory.Exists(
                    Path.Combine(runDirectory, "KiwiG4DMainOnlyIsolation_Data")))
                throw new BuildFailedException("G4D Player output identity is incomplete.");

            string models = Path.Combine(runDirectory, "Models");
            if (Directory.Exists(models) && Directory.GetFileSystemEntries(models).Length != 0)
                throw new BuildFailedException("G4D Models is unexpectedly populated.");

            VerifyPreferenceNamespaceUnused();
        }
        finally
        {
            _expectedOutput = null;
            // Intentionally preserve incomplete output and evidence; never
            // delete a directory containing potentially foreign files.
        }
    }

    private static void VerifyProfile(BuildProfile profile)
    {
        if (profile == null || AssetDatabase.GetAssetPath(profile) != ProfilePath)
            throw new BuildFailedException("The exact Unity-created G4D BuildProfile is missing.");
        if (profile.scriptingDefines == null ||
            Array.IndexOf(profile.scriptingDefines, IsolationDefine) < 0)
            throw new BuildFailedException("G4D profile compile marker is missing.");

        // Unity 6000.0 does not publicly document a profile GetComponent API.
        // A version-specific, read-only reflection/SerializedObject probe is
        // allowed only when each field is present; otherwise fail closed.
        FieldInfo field = typeof(BuildProfile).GetField(
            "m_PlayerSettings", BindingFlags.Instance | BindingFlags.NonPublic);
        UnityEngine.Object settings = field == null
            ? null : field.GetValue(profile) as UnityEngine.Object;
        if (settings == null)
            throw new BuildFailedException("Cannot prove G4D PlayerSettings override.");

        SerializedObject serialized = new SerializedObject(settings);
        SerializedProperty company = serialized.FindProperty("companyName");
        SerializedProperty product = serialized.FindProperty("productName");
        if (company == null || product == null ||
            company.propertyType != SerializedPropertyType.String ||
            product.propertyType != SerializedPropertyType.String ||
            company.stringValue != Company || product.stringValue != Product)
            throw new BuildFailedException("G4D PlayerSettings identity mismatch.");
    }

    private static void VerifyPreferenceNamespaceUnused()
    {
        // Read-only: do not access any existing values or write the registry.
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
            @"Software\" + Company + @"\" + Product, false))
        {
            if (key != null)
                throw new BuildFailedException("G4D preference namespace is already in use.");
        }
    }

    private static void RequireSafeExistingDirectory(string directory)
    {
        string full = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(full))
            throw new BuildFailedException("G4D expected directory is absent.");
        string cursor = root;
        string remainder = full.Substring(root.Length);
        foreach (string segment in remainder.Split(new[] { Path.DirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, segment);
            FileAttributes attributes = File.GetAttributes(cursor);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                throw new BuildFailedException("G4D directory contains a reparse point.");
        }
    }

    private static bool IsReservedOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        string full = Path.GetFullPath(output);
        string prefix = ReservedRoot.TrimEnd(Path.DirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDedicatedActiveProfile()
    {
        BuildProfile active = BuildProfile.GetActiveBuildProfile();
        return active != null && AssetDatabase.GetAssetPath(active) == ProfilePath;
    }

    internal static bool RequiresIsolation(BuildReport report)
    {
        return _expectedOutput != null ||
               IsReservedOutput(report.summary.outputPath) ||
               IsDedicatedActiveProfile();
    }

    internal static void RequireAuthorizedCallback(BuildReport report)
    {
        string output = Path.GetFullPath(report.summary.outputPath);
        if (_expectedOutput == null ||
            !string.Equals(output, _expectedOutput, StringComparison.OrdinalIgnoreCase) ||
            report.summary.platform != BuildTarget.StandaloneWindows64 ||
            !string.Equals(Path.GetFileName(output), ExeName, StringComparison.OrdinalIgnoreCase))
            throw new BuildFailedException("Unapproved G4D build route. No model copy is allowed.");

        RequireSafeExistingDirectory(Path.GetDirectoryName(output));
    }
}

// Validate before ordinary pre-build callbacks; never infer permission solely
// from an EXE name or a Product folder path.
public sealed class KiwiG4DMainIsolationPreBuildGate : IPreprocessBuildWithReport
{
    public int callbackOrder => -20000;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report == null) throw new BuildFailedException("Missing build report.");
        if (KiwiG4DMainIsolationBuild.RequiresIsolation(report))
            KiwiG4DMainIsolationBuild.RequireAuthorizedCallback(report);
    }
}
#endif
