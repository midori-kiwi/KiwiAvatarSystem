# KiwiAvatarSystem AGENTS.md

## Scope
This file applies to the KiwiAvatarSystem repository only.
Do not treat it as global guidance for unrelated repositories.

## Astra Single-Agent Policy

Default mode: single-agent execution.

- Do not spawn, delegate to, or invoke any sub-agent unless the user explicitly requests sub-agent use in the current request.
- The default sub-agent count is 0.
- Do not use sub-agents for parallelization, research, implementation, review, testing, validation, or performance optimization.
- Do not infer permission to use sub-agents from task complexity, duration, context size, or potential quality improvements.
- Previous permission to use sub-agents does not carry over to later requests.
- If no explicit current-request permission exists, complete the task entirely in the primary Astra agent.
- Do not recursively delegate.

## Goal
Preserve the working low-latency Production core unless evidence justifies a change.
Prefer deletion, responsibility retirement, and simplification before adding new mechanisms.
`NO_CHANGE` is a valid and preferred result when no change is justified.

## Authority
Use claim-scoped authority.

- Implementation: current local Production source + SHA.
- Runtime: same-build, same-Production correlated evidence.
- Package semantics: actually installed package source.
- Performance: clean Production benchmark only.
- Visual: same-build observed result only.

Do not promote Static findings to Runtime facts.
Do not infer SHA values.
Do not prefer GitHub main when it is older than current local Production.
Treat Observer, Validator, Benchmark, and REPORT conclusions as audit targets, not unquestionable authority.

## Repository safety
Before modifying files:
- inspect git HEAD and git status;
- preserve user-owned dirty files;
- do not reset, checkout, clean, or overwrite unrelated changes;
- record write targets;
- do not commit or push unless explicitly requested.

## Core architecture
Face Landmarker is the Primary / Source of Truth.

Target flow:
Newest valid camera sample
-> Face Landmarker
-> Canonical semantic state
-> Model Profile / model-specific mapping
-> Avatar / FacePart binding
-> Presentation
-> Spout/output

Healthy samples should follow the shortest correct path.
Reject or locally suppress only invalid, duplicate, out-of-order, stale, mixed-epoch, broken-transaction, or presentation/provider-transition failures.

## Camera / Native rails
Windows/DX12 is the primary target.

Preserve unless contrary evidence is established:
- latest-frame behavior;
- fixed Unity-visible texture identity;
- explicit timestamp and generation;
- session epoch distinct from frame sequence;
- nonblocking realtime path;
- bounded resources.

Do not reintroduce without boundary-specific evidence:
- FIFO or stale accumulation;
- `UpdateExternalTexture`;
- IMFSample retention across callback/worker lifetime;
- blocking D3D12 queue Wait;
- rotating Unity-visible texture identity;
- D3D11On12 1080p Production path.

Do not hide camera/inference defects by changing tracking math.

## Tracking / semantic ownership
Keep authority singular and explicit.

- Head = one rigid authority.
- Root, Head, Eye, and Mouth are separate channels.
- Yaw/Pitch/Roll must not drive Root translation.
- Blink/Mouth/Eye must not drive Root translation.
- Provider normalization owner = 1.
- Resume != Provider Switch.
- Temporal Presentation owner = 1 per channel.
- Prediction owner = 1 per channel.
- Do not publish duplicate, out-of-order, stale, or mixed-epoch samples.
- Texture/Crop/Mask/FacePart should remain one same-sample transaction where required.

Do not add strong smoothing, Kalman filtering, stale prediction, permanent frame delay, or threshold relaxation without evidence.

## Inference / GPU
Trace actual installed Inference Engine source before changing scheduling, queue, tensor, or readback behavior.

Keep preprocess, execution, output lifetime, readback, decode, and canonical stages distinguishable.

Do not add Fence, blocking Wait, queue flush, or async-disable as a guessed fix.
A synchronization change requires exact boundary evidence and lifecycle/resource review.

## Diagnostic code
Production and Diagnostic responsibilities must remain separate.

For each diagnostic:
- state the claim it measures;
- state whether the claim is still open;
- keep it opt-in when practical;
- retire superseded diagnostic implementations once their evidence is preserved and no unique dependency remains.

Do not preserve old diagnostics merely because they once produced useful evidence.
Observer-heavy runs are not Performance Authority.

## Avatar / FacePart
Preferred architecture:
Evidence-Protected Core
+ Avatar Adapter
+ Model Profile
+ Runtime Import
+ Transactional Hot Swap

Prefer stateless mapping -> FacePartState -> atomic commit.

External tracker replacement is not the Primary direction.
External reuse is selective and evidence-gated.

Reject reuse that permanently adds unnecessary:
- copies;
- FIFO/queues;
- buffers;
- process/thread hops;
- serialization;
- CPU-GPU roundtrips;
- readback/upload;
- previous-frame dependency;
- hidden smoothing/normalization;
- additional authority owners.

## Change decision
Before changing Production, answer:

`WHY_CHANGE_IS_BETTER_THAN_NO_CHANGE=`

If evidence does not support the answer, do not change Production.

Preferred order:
1. delete unreachable/obsolete/superseded code;
2. retire duplicate responsibility;
3. simplify ownership/state;
4. optimize the remaining hot path;
5. add a new mechanism only when necessary.

Big-Bang rewrite is rejected.

## Validation order
Use:
1. Source identity
2. Timestamp / transaction identity
3. Model semantic correctness
4. Canonical semantic correctness
5. Authority safety
6. Lifecycle / recovery
7. Resource correctness
8. Clean performance
9. Visual result

Validation effort should be proportional to change risk.
Do not repeat already-passing validation unless new evidence, a new change, or an unresolved concern can change the decision.

## Current-state information
Do not hard-code volatile current version numbers, SHAs, or latest Runtime conclusions in this file.
Resolve current state from local source, current build identity, Runtime evidence, and the latest relevant consolidated report.

## Reporting
Keep Confirmed, Runtime, Static, Package Source, External Public Source, Inference, Rejected, Unknown, and Superseded claims distinct.

Final reports should state:
- what changed;
- what did not change;
- why the change is better than `NO_CHANGE`;
- Runtime/Performance/Visual authority separately;
- next single action.

Never fabricate Runtime, Performance, Visual, build, or SHA authority.
