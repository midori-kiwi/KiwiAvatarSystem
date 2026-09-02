KiwiAvatarSystem v44.55.22 Build Helper Fix

Issue:
BuildFailedException namespace was missing in the temporary Unity Editor helper.

Fix:
Added:
  using UnityEditor.Build;

No observer logic changed.
No Production / Native / Tracking / ROI / threshold changes.

Extract into D:\KiwiAvatarSystem and rerun:
  Set-ExecutionPolicy -Scope Process Bypass
  & "D:\KiwiAvatarSystem\Tools\KiwiV44_55_22\Build-KiwiV44_55_22.ps1" -ProjectRoot "D:\KiwiAvatarSystem"
