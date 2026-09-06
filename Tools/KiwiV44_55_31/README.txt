Kiwi v44.55.30 - observer-only diagnostic, not a Production fix.

Use Windows PowerShell 5.1 (powershell.exe), not pwsh.exe.
Build is executed once by Build-KiwiV44_55_31.ps1 and identity-pinned afterward.

After the supplied build/static report says ready, collect one correlated CONTROL/PROBE:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\KiwiAvatarSystem\Tools\KiwiV44_55_31\Run-KiwiV44_55_31.ps1"

Keep your face visible in the camera throughout both Players. The script launches CONTROL
then PROBE, each with the same 120-second v20 measurement/dependencies and the same v27 observer.
Each Player can take up to 270 seconds including stabilization and draining by callbacks.
Do not manually close the Player; incomplete exit is INCONCLUSIVE.
Only the snapshot flag changes behavior. Evidence-directory/log paths differ for isolation.
One RuntimeEvidence_v44_55_31_*.zip is produced, including failed/incomplete artifacts if possible.
The script runs Validate-Runtime.ps1 against the collected directory after packaging.

The collector invokes independent revalidation automatically; its printed EvidenceRoot is
the exact directory accepted by the validator for later manual revalidation.

Evidence thresholds fixed before Runtime:
Minimum 60 fully valid pairs per arm. Original Actual classification independently recomputed.
CONTROL NEITHER fraction must be at least 0.95 to reproduce the baseline.
Any Actual class fraction change greater than 0.10 absolute is OBSERVER_CONTAMINATED.
Whole-run diagnostic render throughput ratio outside [0.5,2.0] is gross contamination.
These throughput numbers have performanceAuthority=0 and are not Production benchmarks.

The probe only separates coarse output observation endpoints. Storage lifetime, readback
synchronization and native queue ordering are not individually isolated. No fence, wait,
flush, async-disable, repeated inference or lane.input oracle is implemented.

Generated validator fixture directories are SYNTHETIC and never Runtime authority.
Tests can be rerun via Test-Validator.ps1 before build. Editing any pinned source/tool after
build causes collection to fail its identity guard; do not bypass it.
