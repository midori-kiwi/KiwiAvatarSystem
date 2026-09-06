KiwiAvatarSystem v44.55.28 Production Async-Compute Execution Context Isolation A/B

Run the exact same v44.55.27 Development Player twice. The only allowed non-empty
environment difference is KIWI_INFERENCE_ASYNC_COMPUTE_PROBE=1 versus 0.

Windows PowerShell 5.1:

  .\Run-KiwiV44_55_28.ps1 -Mode ASYNC
  .\Run-KiwiV44_55_28.ps1 -Mode GRAPHICS

The launcher fails closed on stale evidence directories, protected/build/model/DLL
SHA drift, unexpected KIWI environment variables, cadence activation, queue contract
mismatch, camera Path B/profile mismatch, missing v27 COMPLETE, non-zero Player exit,
or uncorrelated runtime artifacts. Correctness observer only; performance authority=0.

No Production source, observer source, model, Native DLL, Worker, Schedule, readback,
fence, wait, ROI, threshold, smoothing, tracking authority, or camera configuration is
changed by v44.55.28.
