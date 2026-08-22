#if UNITY_EDITOR
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

/// <summary>
/// Phase 15 build-side companion to the Play/final RC gates.
/// The build callback does not open/modify scenes; it requires the saved project
/// fingerprint to match a passing full preflight. Non-Development builds also require final runtime RC evidence.
/// </summary>
public sealed class KiwiReleaseCandidateBuildGate : IPreprocessBuildWithReport
{
    public int callbackOrder => -10000;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (!KiwiReleaseCandidatePreflight.HasCurrentPassingStamp(
                out string reason))
        {
            throw new BuildFailedException(
                "KiwiAvatarSystem Phase 16.1 preflight gate blocked the build: " +
                reason +
                " Run Tools/Kiwi Avatar System/Release Candidate Preflight/" +
                "Run Full Preflight and fix all Error/Critical findings first.");
        }

        bool developmentBuild =
            (report.summary.options & UnityEditor.BuildOptions.Development) != 0;

        if (
            !developmentBuild &&
            !KiwiReleaseCandidateFinalGate.HasCurrentFinalStamp(
                out string finalReason)
        )
        {
            throw new BuildFailedException(
                "KiwiAvatarSystem Phase 16.1 final RC gate blocked the non-" +
                "Development build: " + finalReason +
                " Complete the 8-scenario Play Mode acceptance campaign, " +
                "capture runtime evidence, and Finalize RC1 first.");
        }
    }
}
#endif
