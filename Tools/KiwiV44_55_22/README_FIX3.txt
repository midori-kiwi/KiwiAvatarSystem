KiwiAvatarSystem v44.55.22 FIX3
Production Input Readback Transaction Synchronization

Observed invalid run:
- REF-vs-mode2 ge4=0
- Production-vs-REF / Production-vs-mode2 ge4 ~65k-89k
- old observer classified NORMAL_NO_GE4

Root cause isolated to observer contract:
Production queues pendingOutput.ReadbackRequest().
Its normal completion path polls pendingOutput.IsReadbackRequestDone().
The old v44.55.22 requested lane.input readback immediately after pair attach,
before Production output completion established that the async compute chain had completed.

FIX3:
1. Add observer-only early Update probe at execution order -33000.
2. Poll existing Production pendingOutput.IsReadbackRequestDone().
3. Start the single observer lane.input ReadbackAndCloneAsync only after output-ready,
   while the lane identity still matches the measured source transaction.
4. If REF-vs-mode2 ge4=0 but Production input is ge4>0 from both, fail closed:
   RegisterFault, no aggregate/classification, INVALID_OBSERVER.
5. Previous bootstrap and laneChangedBeforeReadbackCompletion validator fixes retained.

No Production / Native / Tracking / ROI / threshold change.
No additional Blit / RenderTexture / Worker / blocking wait.
Observer-owned async input readback remains exactly one callsite per measured pair.
Performance authority=0.

Observer before SHA256:
5FBD9483EF9BBB796FA39403F248EF3B9C514F59FC9A2815764FA5F3E08F12A9

Observer FIX3 SHA256:
BBDE5AFC9AB57F53A55709601DB120E7121E1FFFA11EA929C68E8F1BD5CF3590

Build:
Set-ExecutionPolicy -Scope Process Bypass
& "D:\KiwiAvatarSystem\Tools\KiwiV44_55_22\Build-KiwiV44_55_22.ps1" -ProjectRoot "D:\KiwiAvatarSystem"

Runtime after BUILD PASS:
& "D:\KiwiAvatarSystem\Tools\KiwiV44_55_22\Run-KiwiV44_55_22.ps1" -ProjectRoot "D:\KiwiAvatarSystem"
