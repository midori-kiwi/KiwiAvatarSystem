# KiwiAvatarSystem v4.1.0 optimization review

Date: 2026-08-11  
Primary target: Windows / Unity 2022.3.62f2  
Secondary targets: Android and iOS source/build compatibility  
iOS physical-device validation: out of scope

## Outcome

The existing MediaPipe Face LandMarker pipeline remains the source of truth.
The selected design removes redundant work around accepted results without
adding a second tracker, extra smoothing latency, or a new cross-thread lifetime
risk. The package version and safe-upgrade templates are now v4.1.0.

## Three evaluated directions

Scores are 1-10. Total weights latency and stability twice because those are the
primary product requirements.

| Direction | Latency | Stability | Compatibility | Risk | Verifiability | Weighted total |
|---|---:|---:|---:|---:|---:|---:|
| A. Replace or augment Face LandMarker with another tracker | 5 | 5 | 4 | 3 | 5 | 37/70 |
| B. Rebuild the complete pipeline around shared immutable frames | 8 | 8 | 5 | 4 | 7 | 52/70 |
| C. Preserve LandMarker and optimize changed-result access, UI GC, validation and settings safety | 9 | 9 | 9 | 9 | 10 | 64/70 |

Direction C was selected and is the only direction implemented. Direction A
would violate the single-source tracking policy and make direction calibration
harder. Direction B could remove additional copies, but safely retaining native
callback-owned data across multiple consumers would add lifetime and teardown
complexity disproportionate to the current 478-point payload.

## Implemented changes

- Added changed-timestamp landmark access. Four eye/mouth consumers now skip the
  full array copy when the render frame sees the same inference result.
- Kept no-face and tracking-loss behavior distinct from an unchanged valid frame.
- Centralized pitch/yaw/roll direction mapping in testable math while preserving
  right-to-right yaw and roll signs and combined right/down behavior.
- Cached stable IMGUI labels, slider text, model labels and layout options.
- Reduced diagnostics formatting to 5 Hz and disabled it while the panel is hidden.
- Finite-checked and clamped loaded tracking settings to prevent corrupt saved
  values from producing extreme motion or non-finite transforms.
- Normalized duplicate import suffixes, including nested `(1) (2)`, `Copy`,
  `_copy`, and `(0)`. Manually dropped VRM files are normalized during scanning.
- Added a v4.1 editor/batch validator and updated safe-installer hashes while
  retaining v4.0.0 as a recognized safe upgrade source.
- Converted nine legacy CP932 scripts to UTF-8 without changing runtime behavior.

At a 120 Hz render rate and 30 LandMarker results per second, changed-result
access can avoid up to 75% of the old repeated landmark copies. It does not imply
a 75% reduction in total CPU or GPU frame time.

## Ten review passes

1. Baseline Unity compilation: passed before changes.
2. Tracking data flow: confirmed Face LandMarker remains the only source.
3. Callback/thread safety: changed-only reads remain under the existing lock.
4. Consumer semantics: valid-unchanged and no-face states remain distinguishable.
5. Direction signs: avatar-right yaw and avatar-right roll validated.
6. Combined motion: pitch/right yaw/right roll validated together.
7. Prediction: motion extrapolation remains bounded and rest-lock gates remain intact.
8. Runtime UI: stable per-frame string and layout-option allocations removed.
9. Model handling: import, manual folder drop, collision sequence and suffix cleanup reviewed.
10. Upgrade integrity: main scripts, templates, versions and target SHA-256 values aligned.

## Five additional review passes

Further improvement was possible, so five additional passes were completed.

11. Hidden-panel behavior: found and removed an unnecessary 5 Hz formatting path.
12. Manual external-model addition: added normalization during model scanning.
13. Persistence hardening: added finite checks and range clamps for saved controls.
14. Naming edge cases: added zero/nested/copy suffix coverage.
15. Platform and artifact audit: verified Unity-supported APIs, UTF-8 custom sources,
    duplicate-name absence, and batch validation after all corrections.

## Verification result

- Unity batch compilation: passed.
- Automated optimization validation: 12/12 passed.
- Direction-mapping hot loop: 100,000 mappings in 15.65 ms in the final batch editor run.
- Tracking template/main source SHA-256 pairs: identical.
- Duplicate artifact suffix scan: passed.
- Existing warnings: two MediaPipe sample deprecation warnings and four pre-existing
  unused debug-field warnings; no new warning category was introduced.

Real webcam latency, camera-specific jitter, Android hardware performance and iOS
device behavior cannot be established by batch compilation. Those require runtime
capture measurements; iOS physical-device checking remains intentionally excluded.
