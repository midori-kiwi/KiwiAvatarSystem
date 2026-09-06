KiwiAvatarSystem v44.55.29 authority-gate additions

The package Runtime Validator compares only EXE SHA for its same-build gate.
The Unity Windows launcher stub has the same SHA in v28 and v29 while their
Assembly-CSharp.dll files differ, so EXE SHA is not sufficient build identity.

Run-KiwiV44_55_29.ps1 records and checks before/after every arm:
- EXE and Assembly-CSharp
- Inference Engine and MediaPipe managed assemblies
- inference and MediaPipe models
- built and Production Native DLLs
- protected Tracker, Runner, and FaceMotion sources
- Packages manifest and packages-lock
- installed Inference Engine and MediaPipe package.json files
- v29 source and vendor hotfix ZIPs
- local Run and Runtime Validator scripts

Validate-Runtime-Identity-KiwiV44_55_29.ps1 verifies current SHA, cross-arm
equality, exact package SHA, normal exit, and arm-selection environment.
Validate-Runtime-KiwiV44_55_29.ps1 separately retains the package payload,
observer-integrity, population, classification, and claim-decision gates, with
only the invalid `elif` tokens corrected to PowerShell `elseif`.

No Production C#, observer C#, Native DLL, model, experiment mode, threshold,
tracking, inference, camera, or presentation behavior is changed.
