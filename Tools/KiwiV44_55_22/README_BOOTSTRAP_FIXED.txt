KiwiAvatarSystem v44.55.22 Bootstrap Race Fix

Observed runtime:
  OBSERVER_FAULT DEPENDENCY_BIND_FAIL InvalidOperationException Dependency lanes are unavailable.

Root cause:
  v44.55.20 and v44.55.22 both bootstrap at AfterSceneLoad.
  The v44.55.20 component can exist before its tracker discovery has populated _lanes.

Fix:
  - dependency exists + _lanes null/empty => transient retry, not fault
  - lane[0] null => transient retry, not fault
  - true reflection/schema failures still fail closed
  - readback-completion lane identity validator fix remains unchanged

Observer before SHA256:
  BEB0AA1DEBBC7957A3B1B648E20E1C7148B3A456A41C417B158FCA7042F05D6A

Observer after SHA256:
  5FBD9483EF9BBB796FA39403F248EF3B9C514F59FC9A2815764FA5F3E08F12A9

No Production / Native / Tracking / ROI / threshold changes.
No build gate bypass. Full Preflight remains required.

Build:
  Set-ExecutionPolicy -Scope Process Bypass
  & "D:\KiwiAvatarSystem\Tools\KiwiV44_55_22\Build-KiwiV44_55_22.ps1" -ProjectRoot "D:\KiwiAvatarSystem"

Runtime after BUILD PASS:
  & "D:\KiwiAvatarSystem\Tools\KiwiV44_55_22\Run-KiwiV44_55_22.ps1" -ProjectRoot "D:\KiwiAvatarSystem"
