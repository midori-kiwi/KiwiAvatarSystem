KiwiAvatarSystem v44.55.22 FIX4
Observer-Owned GPU Snapshot Transaction Isolation

Observed FIX3:
  OUTPUT_READY succeeded, then Production lane was reused before the direct
  lane.input async readback completed:
    OBSERVER_FAULT LANE_CHANGED_BEFORE_READBACK_COMPLETION

Conclusion:
  Waiting for pendingOutput completion solved compute-completion ambiguity, but
  direct readback lifetime still exceeded Production lane ownership.

FIX4:
  1. Early poller waits for existing Production pendingOutput completion.
  2. While lane identity still matches, read Production lane.input ComputeBuffer.
  3. Dispatch one tiny observer compute copy into an observer-owned ComputeBuffer.
  4. In the SAME observer CommandBuffer, request async readback of the snapshot.
  5. Later Production lane reuse is informational only because the readback source
     is the observer-owned snapshot buffer.
  6. Snapshot-submission identity is fail-closed.
  7. Existing REF/mode2/parity thresholds and decision rules are unchanged.
  8. Existing impossible tri-parity contract remains fail-closed.

Added correctness instrumentation per pair:
  - ComputeBuffer snapshot: 1
  - compute copy dispatch: 1
  - async GPU readback: 1
  - blocking wait: 0

Unchanged:
  Production / Native / Tracking / ROI / thresholds / inference worker.

Observer FIX3 SHA256:
  BBDE5AFC9AB57F53A55709601DB120E7121E1FFFA11EA929C68E8F1BD5CF3590

Observer FIX4 SHA256:
  BBCECC1ECDF57A7E6D452A4B5B5D3E2CB0F2165755C00C09F191B0997B4F0B9E

Snapshot shader SHA256:
  D9B0653E0F478359473D42A43FD0321FADC959DF21378A76EF7999B1322DE48D

Build:
  Set-ExecutionPolicy -Scope Process Bypass
  & "D:\KiwiAvatarSystem\Tools\KiwiV44_55_22\Build-KiwiV44_55_22.ps1" -ProjectRoot "D:\KiwiAvatarSystem"

Runtime after BUILD PASS:
  & "D:\KiwiAvatarSystem\Tools\KiwiV44_55_22\Run-KiwiV44_55_22.ps1" -ProjectRoot "D:\KiwiAvatarSystem"

Expected order:
  PAIR_ATTACHED
  OUTPUT_READY
  SNAPSHOT_SUBMITTED
  SNAPSHOT_READBACK
  SAMPLE
