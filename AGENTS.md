# KiwiAvatarSystem — Project AGENTS.md

## Scope

This file contains durable KiwiAvatarSystem rules only.
Project root: `D:\KiwiAvatarSystem`.

Do not embed current version numbers, current Git SHA, latest Runtime results, current roadmap step,
temporary hypotheses, or other volatile state here. Resolve current state from local Production,
installed package source, Git history, KiwiValidation reports, build identity, and correlated Runtime evidence.


## Mandatory project context preflight

Treat this `AGENTS.md` as durable operating policy, not as the repository's volatile status database.

The fixed current project-information directory is:

`D:\KiwiAvatarSystem\ProjectContext\Current\`

Files currently present in this directory form the current project information set.
Do not depend on their exact filenames, revision numbers, filename labels such as `latest` or `final`,
or filesystem modification time to decide which project-information document is current.

At the start of every substantive KiwiAvatarSystem technical task, before design, implementation, validation,
evaluation, or a next-action recommendation:

1. resolve current live-local authority first: project root, branch/HEAD/status, relevant Production paths and SHA,
   exact Unity identity, installed package/model identity, and user-owned dirty state as applicable;
2. enumerate every file currently present under `D:\KiwiAvatarSystem\ProjectContext\Current\`;
3. read all current-context documents whose claim scope can materially affect the assigned task;
4. reconcile their Current / Rejected / Superseded / Unknown / Unverified / Do Not Change / Next Action claims;
5. locate and read the newest relevant KiwiValidation / Consolidated REPORT(s) when the current-context documents
   identify or require them;
6. inspect the actually installed package source for package-behavior claims;
7. compare older reports/history only after current authority is established.

Directory membership is the current-context selector.
A document moved out of `ProjectContext\Current\` is not part of the current project-information set unless the task
explicitly requires historical evidence.

The `Current` directory does not override claim-scoped authority.
Current integrated information and project-context documents are coordination/evidence sources, not automatic
Implementation Authority. Current live-local Production source + actual identity remains authoritative for
Implementation claims.

Do not choose between conflicting `Current` documents by:
- filename;
- revision number;
- newest mtime;
- directory order;
- a `latest` / `final` label;
- GitHub `main`;
- an older report's recommendation.

If documents inside `ProjectContext\Current\` conflict, resolve the conflict using claim-scoped Authority,
source identity, report-time SHA/HEAD, same-build Runtime identity, package identity, and claim scope.

If the conflict cannot be resolved:
`CURRENT_CONTEXT_UNRESOLVED`

Stop the affected Gate. Do not silently select one document and continue.

If the directory is missing or contains no usable project-context document:
`CURRENT_CONTEXT_NOT_FOUND`

Do not silently fall back to an older historical source.

If a current-context document references another required file that is missing or ambiguous:
report that dependency as unavailable/unresolved. Do not invent its contents.

Before changing design, implementation, or project direction, explicitly reconcile:
- Current;
- Rejected;
- Superseded;
- Unknown / Unverified;
- Do Not Change;
- Next Action;
- applicable Runtime / Static / Package / Visual authority.

Keep `ProjectContext\Current\` small and intentional.
Place only documents that are meant to define or constrain current project state, architecture, Authority,
roadmap, or active technical decisions there.
Do not use it as a dump for all historical Codex REPORTs, Runtime ZIPs, logs, or superseded evidence.

When a project-context document becomes superseded, remove it from `ProjectContext\Current\` or move it to
historical storage as part of the same context-maintenance task.

## Default Normal Chat / Codex role boundary

Default division of responsibility:

Normal Chat:
- external/public research;
- comparative design and Design Freeze;
- independent re-audit of Codex REPORTs;
- interpretation/adoption decisions for Runtime and Human Visual evidence;
- roadmap/integrated-information decisions;
- final Production Adopt / Reject / Supersede decisions.

Codex:
- current live-local source/dataflow inspection;
- branch/HEAD/status/dirty-state and SHA/provenance collection;
- actually installed package/source inspection;
- exact method/statement/owner/identity/lifecycle/resource tracing;
- whole-file implementation when authorized by the task;
- Windows PowerShell 5.1 validators/tooling;
- compile/build/ABI/artifact verification;
- rollback preparation;
- consolidated evidence REPORT generation.

This is the default boundary, not a reason to refuse an explicitly assigned task.
When Codex is asked to perform supporting external lookup, prefer official/version-matched source and clearly
separate it from live-local facts.

Codex must not promote its own Validator PASS, REPORT conclusion, implementation recommendation,
or report-time SHA/HEAD into final Project/Production adoption authority by itself.
Provide evidence and claim-scoped conclusions for independent project decision.

## Project-wide double full audit

KiwiAvatarSystemに関する、技術的意味を持つすべての調査・設計・指示・実装・検証・評価・判断・成果物を、
原則として2回の完全監査対象とする。

For every substantive technical target:

`FULL_AUDIT_1`
- audit the entire target and the entire agreed scope from start to finish.

`FULL_AUDIT_2`
- independently audit the same entire target and the same entire scope again from start to finish;
- do not treat `FULL_AUDIT_1` conclusions as authority or merely confirm its delta.

Each pass is 100% scope.
Two partial passes whose union equals one complete audit are not two full audits.

Reject:
- first pass = first half, second pass = second half;
- first pass = Source, second pass = Runtime for the same required whole audit;
- second pass = only changed lines/findings;
- checklist partitioning presented as two audits;
- automatic PASS because the first audit passed.

If the two complete audits disagree:
- do not majority-vote or average to PASS;
- return to claim scope, identity, Authority, assumption, observer/validator, or measurement design;
- resolve the conflict or mark the claim `UNRESOLVED` and stop the affected Gate.

The rule applies project-wide, including technical task instructions, source/package/provenance audits,
implementation plans and results, validators/observers/benchmarks, compile/build/ABI results, Runtime,
Performance, Human Visual, lifecycle/resource/security/compatibility work, reuse/retire decisions,
Reject/Supersede/Rollback/Production decisions, reports, roadmaps, handoffs, and release artifacts.

Double full audit does not mean blindly repeating side-effecting operations twice.
Writes, code modification, builds, Runtime capture, installer application, commit, or push are executed only
as many times as technically justified. Audit the intended action before execution and the resulting evidence
after execution with the required two complete audits. Repeated execution is required separately only when
reproducibility, determinism, flakiness, or repeatability is itself the claim.

For a substantive Codex task, the final consolidated REPORT should state:
- `FULL_AUDIT_1=PASS|FAIL|UNRESOLVED`;
- `FULL_AUDIT_2=PASS|FAIL|UNRESOLVED`;
- disagreements found and how they were resolved;
- remaining `UNRESOLVED` claims.

Do not claim two full audits unless both actually covered the full scope independently.

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

Before a non-trivial design or implementation change:
- complete the mandatory project-context preflight, including `ProjectContext\Current\` enumeration and reconciliation;
- inspect current local Production/source identity;
- inspect the actually installed relevant package source;
- reconcile the latest applicable integrated information / roadmap and relevant REPORTs;
- check relevant official source/documentation;
- compare established alternative designs where they could change the decision;
- identify benefits, costs, latency, authority, lifecycle, resource, and regression risks;
- complete `FULL_AUDIT_1` and `FULL_AUDIT_2` for the proposed technical scope;
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
Include project-context sources actually used, report-time live-local identity, changed files/SHA,
validation evidence, rollback state, `FULL_AUDIT_1`, `FULL_AUDIT_2`, unresolved conflicts, and next evidence boundary.
For Runtime evidence, prefer one ZIP when a ZIP is actually needed.
Do not fabricate local Production verification, current-state claims, source identity, or SHA.
