KiwiAvatarSystem v44.55.22 Validator Fixed

Observer before SHA256:
8CC69BA22312113AB319836AEDB8B2776F73FC009CC74A2D04B0904E511BD29F

Observer after SHA256:
BEB0AA1DEBBC7957A3B1B648E20E1C7148B3A456A41C417B158FCA7042F05D6A

Fixes:
1. DetermineDecision INVALID_OBSERVER now includes:
   _laneChangedBeforeReadbackCompletionCount > 0

2. TryFinalizeReadyCaptures fails closed before parity aggregate/classification:
   - RegisterFault
   - mark ProductionReadbackFailed
   - exclude the pair from aggregate/classification

No Production / Native / Tracking / ROI / threshold changes.

Copy ZIP contents into D:\KiwiAvatarSystem, close Unity, then run:
  Set-ExecutionPolicy -Scope Process Bypass
  & "D:\KiwiAvatarSystem\Tools\KiwiV44_55_22\Build-KiwiV44_55_22.ps1" -ProjectRoot "D:\KiwiAvatarSystem"
