KiwiAvatarSystem v44.55.22 Validator-Fixed + Runtime Tools

1. Extract into D:\KiwiAvatarSystem.

2. Close Unity Editor.

3. Build:
   Set-ExecutionPolicy -Scope Process Bypass
   & "D:\KiwiAvatarSystem\Tools\KiwiV44_55_22\Build-KiwiV44_55_22.ps1" -ProjectRoot "D:\KiwiAvatarSystem"

4. After BUILD PASS, run:
   & "D:\KiwiAvatarSystem\Tools\KiwiV44_55_22\Run-KiwiV44_55_22.ps1" -ProjectRoot "D:\KiwiAvatarSystem"

The runtime script sets the required environment variables, disables v44.55.21,
launches the Development Player, prints important v44.55.20/v44.55.22 log lines,
and packages the latest runtime evidence after the Player exits.

Correctness-only. Performance authority=0.
