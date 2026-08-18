# KiwiAvatarSystem v4.9.0 end-to-end optimization review

## Audited runtime path

The audit covered camera acquisition, fresh-frame observation, GPU preprocessing,
AsyncGPUReadback, MediaPipe LIVE_STREAM submission, callback conversion, landmark
consumers, avatar pose prediction, eye/mouth crop and shader updates, auto framing,
runtime model controls, VRM switching, and spring-bone behavior.

## Design comparison

| Design | Latency | Precision | Flicker | Runtime load | Total |
|---|---:|---:|---:|---:|---:|
| A. Keep full 1280x720 tracking readback | 2/5 | 5/5 | 3/5 | 2/5 | 12/20 |
| B. Downscale the entire camera texture to 640 width | 5/5 | 2/5 | 5/5 | 5/5 | 17/20 |
| C. Keep face textures full resolution; downscale only detection to 960 width | 5/5 | 5/5 | 5/5 | 4/5 | 19/20 |

Design C is implemented. It reduces 1280x720 tracking input from 921,600 to
518,400 pixels (43.75%) while the eye and mouth RawImages continue sampling the
original camera texture.

## Implemented optimizations

- Persistent 960x540 bilinear RenderTexture for LandMarker input; no temporary
  render texture is allocated per frame.
- Downscale and readback occur only for a fresh-frame submission.
- CPU/CPUAsync frame pool capacity is 2; GPU mode keeps 4 for native ownership.
- Runtime diagnostics include the actual input width and height.
- Tracking and external-model adjustment foldouts start collapsed; diagnostics
  stay visible without laying out every slider each frame.
- MouthDisplaySizeLock caches material and scale values.
- FacePartShapeMask sends visibility only when it changes materially.
- KiwiAutoFraming skips SmoothDamp when both follow axes are disabled by the
  avatar-translation preservation setting.

## Diminishing-return review

1. Full-resolution readback was retained as an Inspector fallback but rejected as default.
2. 640-wide detection was rejected as default because small/distant facial geometry loses detail.
3. 960-wide detection was selected as the precision/performance balance.
4. Downscaling visible eye/mouth textures was rejected because it visibly reduces face quality.
5. Per-frame temporary RenderTextures were rejected because they create allocation and driver churn.
6. A persistent RenderTexture was selected.
7. Multiple simultaneous GPU readbacks were rejected because they increase render contention.
8. Synchronous CPU readback was rejected because it stalls the Windows render thread.
9. A FIFO frame queue was rejected because every queued frame adds unavoidable latency.
10. The generation-counted latest-one mailbox was retained.
11. Pool capacity 1 was rejected for GPU ownership edge cases.
12. Pool capacity 2 CPU / 4 GPU was selected.
13. Disabling the control panel was rejected because model switching must remain easy.
14. Collapsed adjustment sections were selected while keeping diagnostics visible.
15. Replacing IMGUI completely was deferred because it adds broad UI regression risk for limited
    benefit after the expensive sections are collapsed.
16. Redundant shader writes were removed without changing motion timing.
17. Eye/mouth mesh segment counts were retained because lowering them changes surface fit quality.
18. Spring-bone update reduction was rejected because it makes tails and accessories less natural.
19. Face LandMarker blendshape extraction was retained; its fixed category scan is small and
    removing it breaks expression features.
20. Incremental GC, 120 fps target, VSync-off runtime, changed-only landmark copies, and
    onBeforeRender late latching were confirmed already optimized.

Further changes now trade away face precision, natural spring motion, adjustment availability,
or render stability for a smaller expected gain. A new live recording is the next evidence needed.

## Verification

- Unity 2022.3.62f2 compilation succeeded.
- KiwiOptimizationValidator: 19/19 passed.
- Runner and motion source/template pairs are byte-identical.
- v4.8.0 hashes are accepted as safe upgrade sources; installer targets v4.9.0.
- Windows remains primary; Android/iOS source compatibility is retained.
- iOS physical-device verification was not performed, as requested.
