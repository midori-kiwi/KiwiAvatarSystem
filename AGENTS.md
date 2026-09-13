# KiwiAvatarSystem — Project AGENTS.md

> Project root: `D:\KiwiAvatarSystem`
>
> Role: durable project operating policy for local Codex / engineering-agent work.
>
> Volatile state such as current SHA, current Runtime result, latest Phase, temporary hypothesis, and Next Action belongs in `ProjectContext\Current\KiwiAvatarSystem_CurrentRoadmap_*.txt`, not here.

---

## 1. Primary objective

KiwiAvatarSystem exists to deliver the newest valid Face Landmarker sample to the Kiwi Avatar with the minimum justified latency, high semantic accuracy, low jitter, fast response, stable stillness, and correct Root / Head / Eye / Blink / Mouth / Expression / FacePart behavior.

Windows / DX12 is the first Production target.

Face Landmarker remains the Primary / Source of Truth.

LIVE CAMERA, Spout / OBS output, Avatar import, recovery, diagnostics, configuration, distribution, and Windows Production Acceptance are part of the product objective.

Validation, diagnostics, phases, FaceGeometry, H1, backends, and architecture work are means, not goals. Do not place work on the Critical Path unless it directly closes a user-visible requirement or an indispensable Correctness / Authority / Transaction / Lifecycle / Resource / Security / Privacy / Performance / Compatibility / Release contract.

Do not redesign a healthy Working Core without evidence.

---

## 2. Mandatory authority preflight

Before any substantive design, implementation, validation, evaluation, or next-action recommendation:

1. Resolve live-local authority as applicable:
   - project root;
   - branch / HEAD / status;
   - user-owned dirty state;
   - exact Unity version and executable;
   - relevant Production paths and SHA256;
   - installed package / model identity;
   - build / artifact identity when the claim requires it.

2. Enumerate every file in:
   `D:\KiwiAvatarSystem\ProjectContext\Current\`

3. Read every current-context document whose claim scope can affect the task.

4. Reconcile:
   - Current;
   - Validated;
   - Rejected;
   - Superseded;
   - Unknown / Unverified;
   - Do Not Change;
   - Next Action;
   - Runtime / Static / Package / Performance / Visual authority.

5. Read the newest relevant KiwiValidation / Consolidated REPORT when referenced or required.

6. For package-behavior claims, inspect the actually installed package source before relying on documentation or a remote repository.

7. Only after current authority is established, use older reports and history as historical evidence.

### Current-context selection

Directory membership in `ProjectContext\Current\` is the selector. Do not choose a current document by filename, revision suffix, mtime, directory order, `latest` / `final` wording, GitHub `main`, or an older report recommendation.

If current-context documents conflict, resolve the conflict with claim-scoped Authority, source identity, report-time SHA / HEAD, same-build Runtime identity, package identity, and claim scope.

If unresolved:
`CURRENT_CONTEXT_UNRESOLVED`

Stop only the affected Gate.

If the directory is missing or has no usable current-context document:
`CURRENT_CONTEXT_NOT_FOUND`

Do not silently substitute an older historical document.

### Fixed AGENTS exception

`D:\KiwiAvatarSystem\AGENTS.md` is the fixed project operating-policy file. It is not selected by `ProjectContext\Current\` membership and does not carry volatile Current state.

---

## 3. Information-source roles

Keep `ProjectContext\Current\` small.

Recommended current set:
- CoreRules: durable project contracts and long-lived design rails.
- CurrentRoadmap: current Authority, Decision Ledger, open issues, single Next Action, and roadmap to acceptance.
- CustomInstructions: compact normal-chat behavior / project context.

Do not place historical Codex reports, Runtime ZIPs, obsolete integrated-information files, old roadmaps, or superseded diagnostics in `ProjectContext\Current\`.

ChatReport and historical integrated-information documents preserve history. They do not automatically become current authority.

When a current-context document is superseded, move it out of `ProjectContext\Current\` during the same context-maintenance task.

---

## 4. Claim-scoped Authority

Use claim-scoped Authority, not “latest wins”.

| Claim | Preferred authority |
|---|---|
| Implementation | current live-local Production source + actual SHA |
| Runtime | same-Production / same-build Runtime |
| Package behavior | actually installed package source |
| Performance | clean Production benchmark |
| Visual | same-build human review |
| Provenance | source → build definition/toolchain/flags → artifact → installed SHA → Runtime evidence |

Rules:
- Never guess a SHA.
- A Codex REPORT SHA / HEAD is a report-time snapshot, not automatic current-live authority.
- GitHub remote/main is not authoritative over newer local Production.
- A GitHub commit is remote implementation evidence, not local Runtime PASS.
- DLL / EXE SHA alone does not prove reproducible build provenance.
- Unknown is neither PASS nor FAIL.
- Observer, Validator, Benchmark, REPORT, capture surface, and measurement design are all auditable.

---

## 5. Project-wide double full audit

All technically meaningful KiwiAvatarSystem research, design, instructions, implementation, validation, evaluation, decisions, and technical artifacts are in scope for:

`FULL_AUDIT_1`
and
`FULL_AUDIT_2`

Each pass must independently cover the same complete agreed scope from start to finish.

Reject:
- split-half audits;
- “source in pass 1, Runtime in pass 2” when the required scope is both;
- second-pass changed-lines-only review;
- automatic confirmation of pass 1;
- majority voting between disagreeing audits.

If the passes disagree, return to claim scope, identity, Authority, assumptions, observer / validator, or measurement design. If the disagreement cannot be resolved, mark the affected claim `UNRESOLVED` and stop that Gate.

Two audits do not imply executing destructive or side-effecting actions twice. Builds, writes, Runtime capture, installer application, commit, or push run only as technically justified. Repeat execution only when reproducibility, determinism, flakiness, or repeatability is itself the claim.

Substantial reports should state:
- `FULL_AUDIT_1=PASS|FAIL|UNRESOLVED`
- `FULL_AUDIT_2=PASS|FAIL|UNRESOLVED`
- disagreements and resolution
- remaining unresolved claims

---

## 6. Normal Chat / GitHub / Codex boundary

### Normal Chat + GitHub is the default development path

Unless the task genuinely requires live-local capability, Normal Chat is the primary owner for:
- public / official research;
- comparative design and Design Freeze;
- GitHub source search and review;
- ordinary source modification on GitHub;
- Static review;
- independent Codex REPORT re-audit;
- Runtime / Human Visual evidence interpretation;
- Adopt / Reject / Supersede decisions;
- roadmap and current-information maintenance;
- commit, when the user has explicitly authorized it.

Do not route ordinary GitHub source analysis or normal code changes to Codex merely because Codex can edit files.

### Codex is primarily for local-only work

Use Codex for work that materially requires `D:\KiwiAvatarSystem` or the local Windows environment, such as:
- local-only files or dirty state;
- live branch / HEAD / status / SHA / provenance;
- safe application and verification of a GitHub commit to local;
- exact Unity compile / build;
- Native build / ABI;
- installed artifact inspection;
- targeted Runtime / stress;
- clean benchmark;
- local hardware / GUI evidence;
- same-build launch and evidence collection for human review;
- rollback / distribution proof;
- necessary consolidated local REPORT.

An explicitly assigned local implementation task may be performed, but Codex must not reopen an already closed design without new evidence.

Human Visual Authority remains human. Codex / Validator may assist with launch, identity, evidence capture, and correlation but cannot replace the human judgment.

---

## 7. Commit / push boundary

A commit is appropriate when one meaningful minimal change has passed Static review and a reproducible baseline should be frozen for the next local verification.

At that point, Normal Chat must explicitly tell the user:
`今がコミット適切時点`

Do not commit unless:
- the user explicitly requests commit in the current task; or
- the user has explicitly granted commit permission for that task.

Do not:
- create premature WIP commits;
- combine independent boundaries into one commit;
- push without an explicit user request;
- treat a pre-local-validation GitHub commit as Production adoption.

Commit notes should state:
- purpose;
- affected boundary;
- Static status;
- local items still unverified.

---

## 8. Local safety

Treat pre-existing dirty state as user-owned unless proved otherwise.

Before code changes, verify as applicable:
- `ProjectVersion.txt`;
- exact Unity executable / version;
- branch / HEAD / status;
- user-owned dirty protection;
- touched-file before SHA;
- candidate SHA;
- after SHA.

Mismatch:
fail closed.

Never use reset / checkout / clean to discard or overwrite user-owned changes.

Windows PowerShell 5.1 compatibility is required for Kiwi PowerShell tooling.

Prefer audited helpers for:
- SHA256;
- root-contained safe I/O;
- subprocess stdout / stderr / exit code;
- transactional replacement;
- rollback;
- write-target guards.

Reject:
- required PS7-only syntax;
- ambiguous cardinality;
- unsafe broad Copy / Move / Remove;
- obsolete path / version assumptions;
- placeholders;
- output / contract / class / version mismatches.

For Generic List usage, prefer `::new()` plus `.ToArray()` where applicable.

---

## 9. Hard human-authorization boundaries

Do not perform without explicit user authorization in the current task:
- `git commit`;
- `git push`;
- tags / releases;
- force push;
- destructive history rewrite;
- `git reset`;
- `git clean`;
- checkout / restore that discards user changes.

Do not make unrelated PC-wide or security-boundary changes without explicit authorization:
- disk / boot / firmware;
- driver install/remove/disable;
- broad Windows service changes;
- UAC / Defender / firewall / security policy;
- credential / account / certificate-store changes;
- machine/user execution-policy changes;
- machine-wide PATH/environment changes;
- unrelated registry changes;
- startup / autorun / scheduled-task persistence;
- shutdown / restart / logoff;
- recursive deletion outside the project;
- permission takeover outside the project.

A process-local PowerShell execution-policy override used by an existing Kiwi runner is not a machine/user policy change.

---

## 10. Engineering objective and latency rails

Deliver the newest healthy sample directly.

Suppress only:
- invalid samples;
- broken identity / transaction;
- stale / duplicate / out-of-order / mixed-epoch publication;
- authority transitions;
- genuine presentation failures.

Preserve:
- latest-frame behavior;
- bounded / nonblocking lanes;
- persistent tracking / ROI where required;
- explicit freshness / liveness / source age;
- explicit dropout / Lost / Reacquire states;
- tracking cadence separate from presentation cadence;
- channel-specific temporal policy with one owner per channel.

Do not hide upstream defects using:
- latency-growing FIFO;
- stale accumulation;
- permanent frame buffering;
- permanent one-frame delay;
- stale prediction;
- strong smoothing / Kalman;
- unjustified threshold relaxation;
- aggressive frame reduction as a first response.

Short dropout must not automatically become Lost. Lost / Reacquire behavior must be governed by explicit age / liveness / quality contracts.

Quality tiers / profiles may tune supported presentation or compute tradeoffs, but must not change authority semantics or conceal correctness failures.

---

## 11. Tracking / Human-head authority

Face Landmarker = Primary / Source of Truth.

Keep Root / Head / Eye / Mouth responsibilities separate.

Head has one rigid authority.

Never leak:
- Yaw / Pitch / Roll into Root translation;
- Blink / Mouth / Eye into Root translation.

Keep:
- Provider normalization owner = 1;
- Resume != Provider Switch;
- Temporal Presentation owner = 1 per channel;
- Prediction owner = 1 per channel;
- duplicate / out-of-order / stale / mixed-epoch publication rejected;
- Texture / Crop / Mask / FacePart same-sample transaction.

Kiwi is a rigid human head:
source rigid pose removal
→ head-local / canonical non-rigid Eye / Mouth appearance
→ existing fixed fitted surface
→ visible Head rigid pose exactly once.

Source de-rigidization reference and visible Head authority are separate responsibilities.

Do not use billboard, 2D facial inverse UV, or model-deformation compensation as the Production solution.

Do not turn `KiwiFaceMotion.cs` or `KiwiInferenceFaceTracker.cs` into camera / display workarounds.

---

## 12. Native Camera / Inference rails

Windows / DX12 first.

Until new evidence proves a defect at that boundary, preserve the Path B SystemMemoryNV12 baseline and:
- latest frame;
- fixed Unity-visible texture identity;
- explicit timestamp;
- explicit session / generation epoch;
- session epoch != frame sequence;
- bounded resources;
- nonblocking normal operation.

Do not reintroduce without direct evidence:
- FIFO / stale queue;
- rotating Unity-visible texture identity;
- `UpdateExternalTexture` workaround;
- callback/worker-crossing `IMFSample` retention;
- blocking D3D12 queue waits;
- unsupported historical bridge paths such as D3D11On12 1080p.

Fence / queue flush / async disable require causal proof before Production use.

Native provenance must trace:
source → build definition/toolchain/flags → artifact → installed SHA → Runtime evidence.

Do not guess a rebuild when the real build definition is unknown.

For Inference Engine:
- distinguish logical tensor size from backing capacity;
- treat worker-owned output lifetime and async queue/readback lifetime as explicit correctness concerns;
- do not restore lane-input direct readback/copy as a Production oracle without new evidence;
- do not choose a backend only because it is faster;
- separate old/new in-flight publication across backend epochs.

---

## 13. Validation / performance / visual

Prefer:
change-local Gate
→ contract / replay Gate
→ milestone full-product Gate.

Do not Runtime-test claims already closed by Static / installed Package Source.

Retire or freeze superseded diagnostics after their claim / owner closes.

Never auto-update Golden data from current behavior. Do not Goldenize bad behavior. Critical Runtime transitions become Golden only after the transition has passed Runtime validation.

Observer-heavy correctness runs are not Performance Authority.

Freeze SLO criteria before final performance comparison when practical. Compare only correctness-qualified candidates.

Final Windows acceptance includes:
- source / provenance;
- transaction identity;
- model semantic;
- canonical semantic;
- authority / ownership;
- lifecycle / recovery;
- resource correctness;
- replay / regression;
- security / privacy;
- clean performance / registered SLO;
- representative support matrix / compatibility;
- Root / Head / Eye / Blink / Mouth / Expression / FacePart;
- LIVE CAMERA / Spout / OBS;
- Human Visual;
- distribution / installer / rollback.

Do not overwrite Human Visual failure with Validator PASS.

---

## 14. Avatar product / security / UX

Target architecture:
Evidence-Protected Core
+ Avatar Adapter
+ Model Profile
+ Runtime Import
+ Transactional Hot Swap.

Keep model-specific behavior outside Tracking Core.

Support model/profile work without making it a new authority owner:
- VRM 0.x / 1.0;
- non-spherical models;
- generic FacePart binding;
- AutoFit;
- Spring / Dangle;
- calibration / profile UI.

Runtime Import must include at the same boundary:
- extension + magic validation;
- path traversal prevention;
- parse isolation;
- resource / memory limits;
- cancellation;
- safe generated storage names;
- atomic failure behavior;
- rollback.

Product completion also includes:
- clear camera / provider state;
- health and diagnostic state;
- actionable error messages;
- recovery paths;
- support bundle where justified;
- privacy / security UX;
- DPI / resize / multi-monitor robustness where applicable;
- keyboard-accessible core UI where applicable;
- nonblocking progress and cancelable import.

Diagnostics must not become a Product authority owner.

---

## 15. External comparison / reuse

External reuse is selective and evidence-gated.

Prefer reusable concepts that reduce Kiwi responsibility and bug surface:
- latest / drop;
- explicit dropout states;
- persistent tracking;
- bounded in-flight control;
- channel-specific temporal policy;
- quality/profile separation;
- task isolation;
- format/import responsibility separation;
- downstream-output separation;
- optional ecosystem adapters.

Reject as default:
- unbounded queues;
- authority-less transport;
- duplicate smoothing owners;
- CPU/OpenCV regression without evidence;
- aggressive frame reduction first;
- hidden commercial implementation guesses;
- external tracker replacement of Face Landmarker Primary;
- adoption based on popularity alone.

When a mature external component is adopted, retire the old Kiwi owner.

Hand / Body, OSC / VMC, telemetry upload, auto-update, and other ecosystem extras are not Windows core-completion blockers. Add them only after need, ownership, latency, privacy/security, and lifecycle evidence justify them.

---

## 16. Communication and implementation style

Prefer concise Japanese for user-facing engineering communication.

Separate:
- confirmed facts;
- Runtime;
- Static / Package;
- public design principles;
- inference / hypothesis;
- unknown / unverified;
- rejected / superseded.

When presenting user-facing pass/fail checks, use Japanese labels first.

Prefer one best justified approach over broad option lists.

Do not ask the user to repeat information already available in the current task or current project context.

When a source file must be replaced for user use, prefer complete whole-file output rather than a fragile partial diff.

Do not require the user to perform a manual “download ZIP → type PowerShell → paste result” loop as the default development workflow. Use GitHub for normal remote development and Codex for genuinely local-only operations. Manual user steps are for human visual judgment, GUI / hardware interaction, privilege boundaries, or unavailable tooling.

---

## 17. Reporting and transfer minimization

Prefer:
- one consolidated Codex REPORT for substantial local work;
- one Runtime ZIP only when Runtime evidence actually needs packaging;
- Visual MP4/images only when visual evidence is required;
- one complete installer/fix archive when distribution is the artifact.

Avoid duplicating large evidence bodies. Record filename / path / timestamp / SHA / purpose / result when that is sufficient.

Permanent chat archives are created by Normal Chat when requested; Codex is not required merely to create a chat archive.

Do not re-promote historical SHA, old Next Action, obsolete versions, or closed diagnostics into Current state.
