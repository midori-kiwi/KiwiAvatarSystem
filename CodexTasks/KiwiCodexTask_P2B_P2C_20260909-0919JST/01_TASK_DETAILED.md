# 01_TASK_DETAILED

## GOAL

Complete, in one local task:

P0 live-local Authority Preflight
→ P2B one-boundary Production repair
→ Change-Local Gate
→ exact Unity compile
→ P2C ordered read-only Static re-audit
→ stop at the next first failing boundary
→ one Consolidated REPORT

Do not continue to P3, Runtime, H1, or a second Production repair.

## AUTHORITY

Implementation Authority:
current live D:\KiwiAvatarSystem source + SHA captured by this task.

Reference-only report-time snapshot:
Branch: work/raw-direct-motion
HEAD: 49b43fedad55b084980f5486dc7142fdf2db2e1e
KiwiInferenceFaceTracker.cs:
CF860F4EF686BD5FDAC4140D4F3E73E64B6F390C3BC64204CF346011AC69380D

These identities are NOT allowed to be assumed current.

Package Authority:
actually installed MediaPipeUnityPlugin 0.16.3 / PackageCache.
Version-matched MediaPipe v0.10.22 C++ source only where installed package does not include the underlying calculator C++.

## P0 PREFLIGHT — REQUIRED BEFORE ANY WRITE

1. Resolve Project root exactly:
   D:\KiwiAvatarSystem

2. Read:
   ProjectSettings\ProjectVersion.txt

3. Resolve the exact Unity.exe intended for this project.
   Require Unity 6000.0.80f1.
   If mismatch: FAIL CLOSED. Do not compile or modify Production.

4. Capture:
   git branch
   git HEAD
   git status --short
   relevant tracked/untracked files
   origin tracking relation where available

5. Do NOT:
   reset
   checkout
   clean
   stash user files
   delete untracked files

6. Capture before SHA256 for:
   Assets\Script\KiwiInferenceFaceTracker.cs
   Assets\Script\FaceLandmarkerRunner.cs
   Assets\Script\KiwiFaceMotion.cs
   Packages\manifest.json
   Packages\packages-lock.json
   ProjectSettings\ProjectVersion.txt

7. Inspect live-local ExtractRegionZScale and its DecodeReadableOutput consumer.
   Confirm the still-current first failing statement is functionally:
   projected crop X-axis magnitude
   → Mathf.Clamp(scale, 0.02f, 3.0f)
   → all base landmark Z

If the live-local implementation no longer matches this boundary:
STOP WITHOUT WRITE.
Re-audit the changed boundary and report AUTHORITY_DRIFT.

## P2B DESIGN-FROZEN REPAIR

Target semantic for VALID input domain:
MediaPipe LandmarkProjectionCalculator::CalculateZScale equivalent:
Z scale = Euclidean magnitude of the projected crop X-axis.
No numeric clamp.

Preferred minimal implementation if live local structure remains as audited:

private static float ExtractRegionZScale(
    Matrix4x4 cropMatrix)
{
    Vector3 xAxis =
        cropMatrix.MultiplyVector(
            Vector3.right);

    float scale =
        xAxis.magnitude;

    return
        scale;
}

Important:
- Preserve current formatting conventions where practical.
- Do not refactor surrounding code.
- Do not introduce a new queue, temporal state, cache, smoothing, prediction, or threshold.
- Do not change method signature unless live-local evidence proves it is required.
- Do not add a second validity owner merely for style.
- Existing downstream non-finite rejection may be reused if it is still present and correct.
- If live-local valid-domain invariants do not guarantee a positive finite crop X-axis, document that separately. Do not silently reintroduce a numeric clamp.
- Invalid-input rejection, if truly required, is Kiwi validity policy and must not be described as MediaPipe numerical equivalence.

## STRICT ONE-BOUNDARY WRITE SCOPE

Allowed Production file:
Assets\Script\KiwiInferenceFaceTracker.cs

Allowed semantic change:
ExtractRegionZScale clamp removal only.

Forbidden same-task Production changes:
- UpdateRegionFromLandmarks ROI normalized clamps
- 478-vs-468 recurrence
- FaceGeometry solver/port
- geometry carrier
- H1
- FacePart
- FaceLandmarkerRunner behavior
- KiwiFaceMotion
- Camera
- Native
- thresholds
- smoothing
- prediction
- fences/waits/flush/async-disable
- Package versions
- Scene defaults

Comments may be updated only where they would otherwise become factually false because of the exact Z-scale change.

## CHANGE-LOCAL GATE

After the edit:

1. Capture candidate SHA256 immediately.
2. Verify git diff for KiwiInferenceFaceTracker.cs.
3. Require semantic diff to be only the proven Z-scale boundary.
4. Verify all protected before SHA files except the intended target are unchanged.
5. Verify Packages\packages-lock.json unchanged.
6. Record any unexpected file mutation as FAIL and investigate before continuing.
7. Do not discard user-owned changes to "make the tree clean."

## COMPILE GATE

Use exact Unity 6000.0.80f1.
No Unity 2022 fallback.

Compile the project/assemblies using the repository's current safe established compile path.
Capture:
- exact Unity executable path
- version
- command
- exit code
- relevant compiler errors/warnings
- build/package lock mutations

Compile success is Static compilation evidence only.
It does NOT prove normalized-landmark semantic PASS.

## P2C ORDERED STATIC RE-AUDIT — READ ONLY

After compile, restart the P1-S audit from the beginning against current post-P2B source.

Audit in this exact order:

1. model provenance
2. first468 topology/decode
3. XYZ crop normalization
4. origin/Y basis convention
5. same-sample ROI transform
6. XY source projection
7. Z source projection
8. pixel-square ROI
9. letterbox semantics
10. rotation convention
11. flip/mirror ownership

For each boundary:
- actual current local producer statement
- installed Package / version-matched official source comparator
- equation/dataflow
- PASS / FAIL / UNKNOWN
- exact claim scope

At the FIRST FAIL:
STOP the ordered audit.
Do NOT fix that failure in this task.

Expected possibility from prior evidence:
after Z scale becomes PASS, pixel-square ROI / independent normalized width-height clamp may become the next first failure.
This is a hypothesis/known-followup candidate, not a result to predeclare.

Do not mix:
- P1-S same-sample normalized semantics
with
- P1-R 478-vs-468 next-frame recurrence
or
- P1-I future geometry identity carrier.

P1-R remains classified by P2 as ACCEPTABLE_PROVIDER_SPECIFIC_DIFFERENCE unless new source evidence directly invalidates that design decision.
P1-I is not a reason to fail Z/XY semantics.

## NO RUNTIME

Do not launch live camera Runtime.
Do not create a Runtime ZIP.
Do not infer performance/visual results.

Runtime for the current equation contract remains unnecessary.

## VALIDATOR SELF-AUDIT

Do not use overall validator PASS/FAIL as P1-S Authority.

If an existing validator is run as supporting evidence:
- identify exactly what claim it measures
- identify stale expectations
- do not overwrite Source/Equation conclusions
- do not modernize unrelated validator failures in this task

## OUTPUT

Create one consolidated report, suggested name:

KiwiCodexConsolidatedReport_P2B_ZScaleRepair_P2C_OrderedStaticReaudit_<YYYYMMDD-HHMMSSJST>.txt

Required sections:
- AUTHORITY_PREFLIGHT
- LIVE_LOCAL_IDENTITY
- UNITY_EDITOR_IDENTITY
- BEFORE_SHA256
- P2_DESIGN_FREEZE_APPLIED
- EXACT_PRODUCTION_CHANGE
- CANDIDATE_SHA256
- AFTER_SHA256
- DIFF_SCOPE_AUDIT
- COMPILE_RESULT
- P2C_ORDERED_STATIC_GATE
- FIRST_FAILING_BOUNDARY_AFTER_P2B
- P1_SAME_SAMPLE_NORMALIZED_SEMANTIC_STATUS
- P1_R_RECURRENCE_STATUS
- P1_I_GEOMETRY_CARRIER_STATUS
- OBSERVER_VALIDATOR_SELF_AUDIT
- PROTECTED_FILES_UNCHANGED
- RUNTIME_STATUS
- REJECTED_NOT_EXECUTED
- NEXT_SINGLE_ACTION
- FIXPOINT_REAUDIT
- FINAL_MACHINE_READABLE_BLOCK

Final machine-readable minimum:

P2A_DESIGN_REUSE_FREEZE=PASS
P2B_Z_SCALE_REPAIR=<PASS|FAIL|NOT_APPLIED_AUTHORITY_DRIFT>
P2B_TARGET_FILE_BEFORE_SHA=<actual>
P2B_TARGET_FILE_AFTER_SHA=<actual or NONE>
UNITY_VERSION=<actual>
UNITY_IDENTITY_GATE=<PASS|FAIL>
COMPILE=<PASS|FAIL|NOT_RUN>
P2C_ORDERED_STATIC_REAUDIT=<PASS|FAIL|PARTIAL>
Z_SCALE_EQUIVALENCE=<PASS|FAIL|UNKNOWN>
FIRST_FAILING_BOUNDARY_AFTER_P2B=<exact boundary or NONE>
P1_SAME_SAMPLE_NORMALIZED_SEMANTIC=<PASS|FAIL|UNKNOWN>
P1_R_RECURRENCE_POLICY=ACCEPTABLE_PROVIDER_SPECIFIC_DIFFERENCE
P1_I_GEOMETRY_IDENTITY_CARRIER=NONE
RUNTIME=NOT_RUN
PRODUCTION_FILES_CHANGED=<exact list>
COMMIT_PUSH=NO
NEXT_SINGLE_ACTION=<one action>

## FIXPOINT

Before finalizing, re-audit:
- live-local authority was not replaced by report-time SHA
- exact Unity version
- one-boundary diff only
- protected files unchanged
- packages-lock unchanged
- no second fix
- no Runtime
- no commit/push
- no user-owned file loss
- Source/Static vs Runtime distinction
- next first failure is evidence-derived, not predeclared

If any condition fails, correct the report/task result before declaring PASS.
