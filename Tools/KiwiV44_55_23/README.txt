KiwiAvatarSystem v44.55.23 Production Output Transaction Authority

Why v44.55.22 is retired as authority:
- historical v44.55.6 already showed queue-external Production lane.input readback
  can create a large false mismatch;
- v44.55.22 FIX3/FIX4 reproduced that family of artifact.

v44.55.23 never reads Production lane.input.

It snapshots three completed packed GPU outputs for the same v44.55.20 pair:
1. Production pendingOutput
2. v44.55.20 REF_FROZEN_GPU output
3. v44.55.20 MODE2 B_GPU output

Comparison is bitwise across all 1405 packed floats.
No post-runtime numeric matching threshold is introduced.

Observer SHA256:
AE2CD168171335E20F6793C5F46A12264D86CCAE21DF62FAFF74FCBA1E92E4A7

Shader SHA256:
4552D8A2CB4E47465D1BD765CD38D85F7B839A61B73555CD40557937D803CB71

Install:
Extract into D:\KiwiAvatarSystem

Build:
Set-ExecutionPolicy -Scope Process Bypass
& "D:\KiwiAvatarSystem\Tools\KiwiV44_55_23\Build-KiwiV44_55_23.ps1" -ProjectRoot "D:\KiwiAvatarSystem"

Run after BUILD PASS:
& "D:\KiwiAvatarSystem\Tools\KiwiV44_55_23\Run-KiwiV44_55_23.ps1" -ProjectRoot "D:\KiwiAvatarSystem"

Required env:
v44.55.20 ON
v44.55.21 OFF
v44.55.22 OFF
v44.55.23 ON

No Production / Native / Tracking / ROI / threshold change.
No blocking wait.
Performance authority=0.
