# KiwiAvatarSystem — Project AGENTS.md

## Scope

This file contains durable KiwiAvatarSystem rules only.
Project root: `D:\KiwiAvatarSystem`.

Do not embed current version numbers, current Git SHA, latest Runtime results, current roadmap step,
temporary hypotheses, or other volatile state here. Resolve current state from local Production,
installed package source, Git history, KiwiValidation reports, build identity, and correlated Runtime evidence.

## Autonomous local execution

Within an explicitly assigned KiwiAvatarSystem task, proceed without routine confirmation for normal engineering work.

Allowed without extra confirmation:
- read/search/edit files under `D:\KiwiAvatarSystem`;
- run Windows PowerShell 5.1 commands and project scripts;
- run Unity Editor/batchmode/build/compile operations required by the task;
- run C#/C++ build tools, validators, linters, ABI checks, SHA256 checks, and local diagnostics;
- create project-local backups, reports, logs, temporary build artifacts, and Runtime evidence;
- inspect installed package source and local Git history;
- use network/web access for official source/documentation and dependency research;
- repeat a failed local compile/test only when the failure or a changed input justifies the rerun.

Do not stop merely to ask whether routine read/edit/build/test operations are allowed.
Make the smallest justified change, verify it, and continue to task completion.

## Hard human-authorization boundary

The following are never implicit consequences of another task.

### Git publication and destructive Git state changes

Do not run any of the following unless the user explicitly requests the exact operation in the current task:
- `git commit`;
- `git push`;
- creating/publishing tags or releases;
- force push;
- `git reset`;
- `git clean`;
- checkout/restore operations that discard or overwrite user-owned changes;
- destructive history rewriting.

Treat an existing dirty workspace as user-owned unless the task proves otherwise.
Never reset, clean, stash, overwrite, or attribute pre-existing modifications to the current task merely to obtain a clean tree.

### PC-wide or operating-system changes

Do not make PC-wide destructive, persistence, security-boundary, or recovery-impacting changes unless the user explicitly requests that exact system operation.

This includes:
- disk partitioning, formatting, boot/BCD changes, firmware/BIOS actions;
- device-driver install/remove/disable;
- Windows service creation/removal or broad service reconfiguration;
- UAC, Defender, antivirus, firewall, security policy, credential, certificate-store, or account changes;
- machine/user execution-policy changes;
- machine-wide PATH/environment changes not strictly required by an explicitly assigned setup task;
- registry changes unrelated to a narrowly identified Kiwi requirement;
- scheduled-task, startup, autorun, or persistence changes;
- shutdown/restart/logoff commands unless the user specifically requested them;
- recursive deletion or permission takeover outside the project root;
- destructive changes to unrelated repositories, user documents, browser profiles, cloud-sync roots, or system directories.

A process-local PowerShell execution-policy override used by an existing Kiwi runner is not a machine/user policy change.

If one of these system operations becomes necessary, stop at that boundary, state the exact required operation and why,
and wait for explicit user authorization. Do not work around the boundary by weakening Windows security.

## External writes

Reading outside the project is allowed when required for source/package/toolchain/provenance inspection.

Writing outside `D:\KiwiAvatarSystem` is allowed only when directly required by the assigned task, such as:
- a normal toolchain cache/output location;
- Unity/Windows logs;
- a user-selected staging/output path.

Keep such writes narrow and reversible. Never recursively delete or rewrite unrelated external paths.

## Claim-Scoped Authority

Use claim-scoped authority rather than "latest wins".

Implementation:
current local Production source + actual SHA > installed artifact/package > local Git > remote repository.

Runtime:
same-Production / same-build Runtime > correlated evidence > static source.

Package:
actually installed package source > version-matched official source > documentation.

Performance:
clean Production benchmark > same-build telemetry > diagnostic run > theory.

Visual:
same-build Runtime/MP4/human observation > screenshot > static math.

Provenance:
source -> build definition -> toolchain/flags -> artifact -> installed SHA -> Runtime evidence.

Never guess a SHA.
Do not promote a Validator PASS, commercial adoption, benchmark result, or "latest" label to correctness by itself.
Observer, Validator, Benchmark, report, and measurement design are all auditable and can be wrong.

## Engineering objective

Deliver each healthy/newest valid sample to the Avatar with minimum justified latency and high semantic accuracy.
Reject, hold, or recover only locally for invalid samples, authority transitions, broken transactions, or presentation failures.

Do not rebuild a Working Core for cleanup alone.
If new evidence proves a defect, change the smallest responsible boundary.
If an assumption, source/timestamp identity, Observer, proxy, Validator, or measurement design is wrong,
return to that authority/measurement boundary instead of stacking compensating fixes.

Do not hide root causes with strong smoothing, Kalman filtering, permanent buffering, stale prediction,
FIFO accumulation, or unjustified threshold relaxation.

## Tracking / Canonical rails

Face Landmarker is Primary / Source of Truth.

Preserve:
- latest-frame behavior; no latency-growing FIFO or stale accumulation;
- persistent ROI where currently required;
- freshness/liveness/source-age separation;
- tracking cadence separate from display/presentation cadence;
- duplicate/out-of-order/stale/mixed-epoch publication rejection;
- same-sample transaction for texture/crop/mask/FacePart data where that contract applies.

Authority separation:
- Head = one rigid authority;
- Root translation is separate from Head rotation;
- Eye/Blink/Mouth/Expression do not drive Root translation;
- Yaw/Pitch/Roll do not leak into Root translation;
- Provider normalization owner = one;
- Resume is not Provider Switch;
- temporal presentation owner = one per channel;
- prediction owner = one per channel.

Do not change `KiwiFaceMotion.cs` or `KiwiInferenceFaceTracker.cs` as camera/display workarounds.

## Native Camera rails

Keep the evidence-protected Windows Native baseline unless new evidence proves a defect at that boundary.

Preserve the principles:
- latest frame;
- fixed Unity-visible texture identity;
- explicit timestamp and generation/session epoch;
- session epoch is not frame sequence;
- bounded lanes/resources;
- non-blocking normal path.

Do not reintroduce without direct evidence:
- FIFO/stale queues;
- rotating Unity-visible texture identity;
- `UpdateExternalTexture` as a workaround;
- callback/worker-crossing `IMFSample` lifetime retention;
- blocking D3D12 queue waits;
- unsupported historical bridge paths.

Native provenance is not closed by DLL/EXE SHA alone.
Track source -> build definition -> toolchain/flags -> artifact -> installed SHA.
Do not perform a guessed rebuild when the actual build definition is unknown.

## Inference / transaction rails

Prefer the actually installed Inference Engine package source for package behavior claims.

Do not confuse logical tensor size with backing capacity.
Treat worker-owned output lifetime and asynchronous queue/readback lifetime as explicit correctness concerns.
Do not restore lane-input direct readback/copy as a Production input oracle without new evidence.

A backend is not Production-eligible because it is faster or pixel-close.
Close model/canonical semantic correctness first.
Separate old/new in-flight publication across backend epochs.

Trace only transaction fields whose necessity is demonstrated.
After execution-context/semantic closure, prefer deterministic replay / Golden Corpus.
Never auto-update Golden data from current behavior.

## Avatar boundary

Target architecture:
Evidence-Protected Tracking Core
+ Avatar Adapter
+ Model Profile
+ Runtime Import
+ Transactional Hot Swap.

Keep model-specific logic out of Tracking Core.
Do not replace Face Landmarker Primary with an external tracker merely because another product appears mature.

External reuse is selective and evidence-gated.
Prefer reuse only when it removes Kiwi responsibility/bug surface without adding permanent FIFO, hidden smoothing,
extra process/thread hops, serialization, CPU/GPU round trips, readback/upload, previous-frame dependencies,
authority owners, or unacceptable lifecycle/resource risk.

## Runtime evaluation order

Evaluate in this order:
1. Source identity / provenance.
2. Timestamp and transaction identity.
3. Model semantic correctness.
4. Canonical semantic correctness.
5. Authority / ownership safety.
6. Lifecycle / recovery.
7. Resource correctness.
8. Clean Production performance.
9. Same-build visual behavior.
10. Product/release reliability.

Observer-heavy correctness runs are not Performance Authority.
Do not use a proxy alone for a Production decision.
Do not change Tracking Core based only on visual symptoms.

Prefer:
change-local Gate -> contract/replay Gate -> milestone full-product Gate.

Do not multiply diagnostics for the same closed claim.
Retire or freeze diagnostics when their claim/owner is closed or superseded.

## PowerShell / Validator / installer

Windows PowerShell 5.1 compatibility is required for Kiwi PowerShell tooling.

Prefer shared, audited helpers for:
- SHA256;
- safe/root-contained I/O;
- subprocess stdout/stderr/exit-code capture;
- transactional replacement;
- rollback;
- write-target guards.

Reject:
- PS7-only syntax in required PS5.1 tooling;
- ambiguous cardinality;
- unsafe broad Copy/Move/Remove;
- obsolete paths/versions;
- placeholders;
- version/class/contract/output-name mismatches.

For Generic List usage, prefer `::new()` plus `.ToArray()` where applicable.

Validators are evidence producers, not unquestionable authorities.
Audit their schema, representation normalization, identity logic, failure mode, write target, and independence.

For installer/distribution replacement:
exact identity -> backup -> transactional replace -> validate -> rollback path.

## Change discipline

Before a non-trivial design change:
- inspect current local Production/source identity;
- inspect the actually installed relevant package source;
- check relevant official source/documentation;
- compare established alternative designs where they could change the decision;
- identify benefits, costs, latency, authority, lifecycle, and regression risks;
- choose one smallest justified approach.

Do not rewrite healthy code for organization/style alone.
Prefer whole-file output when a file must be replaced and the user needs a copyable implementation.
Do not commit or push unless explicitly requested.

After code changes, verify the affected compile/ABI/authority/lifecycle/SHA/write target as applicable.
Run Unity Runtime only when the claim requires Runtime evidence; do not create heavy observers by default.

## Reporting

Keep conclusions concise and separate:
- Confirmed Facts;
- Runtime;
- Static / Package Source;
- Public Design Principles;
- Inference / Hypothesis;
- Unknown / Unverified;
- Rejected / Superseded where relevant.

For substantial Codex work, prefer one consolidated report.
For Runtime evidence, prefer one ZIP when a ZIP is actually needed.
Do not fabricate local Production verification or SHA.
