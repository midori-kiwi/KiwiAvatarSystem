KiwiAvatarSystem v44.55.22 Validator Fixed + Full Preflight Build

Reason for previous build failure:
  The existing KiwiReleaseCandidateBuildGate correctly rejected the build
  because the saved project fingerprint changed after v44.55.22 was added.

Correct handling:
  The build helper now runs the existing Full Preflight first.
  No build-gate bypass is used.

Flow:
  1. KiwiReleaseCandidatePreflight.RunFullPreflight(true)
  2. Export KiwiValidation\KiwiPreflight_v44_55_22.json
  3. Require passed=1 / errors=0 / critical=0
  4. Require HasCurrentPassingStamp()=true
  5. BuildPlayer

Observer SHA256:
  BEB0AA1DEBBC7957A3B1B648E20E1C7148B3A456A41C417B158FCA7042F05D6A

Build script SHA256:
  4B99D9DA48B8702DA74838B14B910B6102A25D9248425EC90DD5FB3E3CFFDE0E

Run:
  Set-ExecutionPolicy -Scope Process Bypass
  & "D:\KiwiAvatarSystem\Tools\KiwiV44_55_22\Build-KiwiV44_55_22.ps1" -ProjectRoot "D:\KiwiAvatarSystem"

No Production / Native / Tracking / ROI / threshold code is changed.
