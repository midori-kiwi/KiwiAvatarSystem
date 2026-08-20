Kiwi Avatar System v1.0.0
Ultra LandMarker / Adaptive Avatar Complete Edition
===================================================

TARGET
------
Unity 6000.0.80f1
Unity Inference Engine 2.4.1
MediaPipeUnityPlugin v0.16.3
UniVRM 0.130.1 / VRM 0.x
Windows primary / Android + iOS runtime support
(iOS physical-device verification is intentionally out of scope.)

UNITY 6 / INFERENCE ENGINE HYBRID TRACKING
-------------------------------------------
- migrated from Unity 2022.3 / Sentis 1.6 to Unity 6000.0.80f1 and
  com.unity.ai.inference 2.4.1,
- high-rate 468-point face landmarks run through GPUCompute without the former
  full-frame DX11 GPU-to-CPU readback,
- MediaPipe FaceLandmarker remains a 10 Hz asynchronous auxiliary path for ROI
  correction, 52 blendshapes, pose calibration, reacquisition, and fallback,
- synthetic iris points preserve the existing 478-point consumer contract,
- four consecutive inference failures are required before abandoning the GPU
  track, preventing one-frame eye and mouth disappearance,
- version remains fixed at Kiwi Avatar System 1.0.0.

V5.3 LOW-LATENCY EYE / MOUTH STABILITY
---------------------------------------
- the supplied v5.2 recording now renders at 87-104 fps and accepts results at
  16.8-19.2 Hz, but its 640x360 synchronous readback still costs 45-48 ms,
- LandMarker inference now defaults to 480x270 while visible eye/mouth textures
  remain at the full 640x360 camera resolution,
- the persistent downscaled RenderTexture no longer waits for EndOfFrame before
  its required synchronous readback, removing up to one additional render frame,
- crop position/size, mask contour, counter-rotation, and blink visibility are
  resampled at render rate with signal-specific 110-200 response instead of
  jumping at 16-19 Hz (the contour default is the validated 110),
- binary Canvas eye hiding is replaced by short render-rate material fades,
- mouth crop prediction and mouth-height calibration default OFF to prevent
  camera-pixel shimmer, reversal overshoot, and the former downward mouth drift,
- all new latency/flicker values are adjustable and saveable in the runtime panel
  under settings key v12.

V5.2 WINDOWS READBACK BOTTLENECK FIX
------------------------------------
- the supplied 28.16 s v5.1 recording shows Render 50-54 fps but camera source,
  submission, and accepted results only 7.2-7.9 Hz,
- its 960x540 Windows/DX11 AsyncGPUReadback costs 89-96 ms and source-to-result
  age is 99-104 ms; the estimated LandMarker portion is only about 5-10 ms,
- Windows now defaults to a broadly supported 640x360 at 30 fps camera mode and
  a bounded 640-wide LandMarker input (230,400 pixels),
- Windows uses synchronous CPU readback for that bounded input, avoiding the
  long DX11 asynchronous queue measured in the recording,
- Android and iOS retain CPUAsync so the Windows-specific choice does not add a
  mobile render-thread stall,
- diagnostics now show real camera resolution separately from LandMarker input
  resolution and split source-to-result age from estimated model time.

V5.1 COHERENT VELOCITY / CAMERA-AGE COMPENSATION
-------------------------------------------------
- rotation, position, and depth velocity estimates now accelerate from response
  60 toward 180 only when consecutive LandMarker motion is coherent,
- at 60 Hz a coherent first estimate improves from 63.2% to 86.2% of measured
  velocity and exceeds 99% on the next coherent sample,
- inconsistent/noisy motion retains response 60 and its prediction contribution
  is still reduced by the existing consistency gate,
- prediction now includes half of the measured camera source interval because
  WebCamTexture exposes update observation time, not the exposure midpoint,
- this adds 8.33 ms at 60 Hz or 16.67 ms at 30 Hz, capped to 20 ms and bounded
  again by prediction consistency, quality, stale-time, and absolute pose limits,
- adaptive velocity and camera-age values are adjustable, persistent, and the
  active camera-age compensation is visible in runtime diagnostics (settings v11).

V5.0 FACE-SYNCHRONOUS RECOVERY
------------------------------
- the default 1280x720 webcam request is raised from 30 to 60 fps, reducing the
  capture interval when the camera, driver, exposure, and platform support it,
- equal-resolution webcam modes now select the highest reported frame rate with
  a precise floating-point comparison (including 59.94/60 fps modes),
- constant-velocity body movement keeps predictive feed-forward and the stable
  correction response of 45, so no extra smoothing delay is added while moving,
- stops, acceleration changes, and reversals automatically raise position
  correction toward 180 only while measured velocity is inconsistent,
- response 180 has a 5.56 ms correction time constant: at 60 render fps it
  applies 95.02% of a new residual in one frame without globally enabling snap,
- the recovery response is adjustable and persistent in the runtime Tracking
  panel; settings key v10 prevents older values from replacing the new default.

V4.9 END-TO-END RUNTIME OPTIMIZATION
-------------------------------------
- the original camera texture remains full resolution for eye and mouth display,
  while only the Face LandMarker inference input is reduced to at most 960 pixels
  wide (1280x720 becomes 960x540),
- this reduces Windows/DX11 tracking readback pixels and bytes by 43.75% without
  reducing the visual texture resolution of the avatar's face parts,
- the CPU/CPUAsync TextureFrame pool capacity is reduced from 10 to 2 because
  the coroutine performs only one readback at a time; GPU mode retains 4 slots,
- tracking and model-adjustment foldouts start collapsed, while the pipeline
  diagnostics remain visible, cutting immediate-mode GUI layout work during use,
- constant mouth display scale and unchanged mouth-frame visibility are no longer
  resent to shader materials every render frame,
- auto framing bypasses SmoothDamp work when avatar translation preservation makes
  horizontal and vertical camera follow zero,
- the diagnostics now report the actual LandMarker input resolution next to
  source, submission, result, readback, inference, and render-age measurements.

V4.8 CONTINUOUS FRESH-FRAME LIVE_STREAM
----------------------------------------
- the supplied 50.228 s recording renders at roughly 40-48 fps, but accepted
  Face LandMarker results fall to roughly 6-8 Hz although inference is usually
  only several to tens of milliseconds; inference compute is not the bottleneck,
- the cause was the explicit one-request gate being checked before
  WebCamTexture.didUpdateThisFrame, so camera frames arriving during inference
  were discarded before readback,
- camera freshness is now observed every Unity Update, including frames in which
  the processing coroutine is waiting for AsyncGPUReadback,
- a generation-counted one-slot latest-frame mailbox preserves camera updates
  that arrive during readback and never creates an old-frame queue,
- the minimum-latency default submits every genuinely new frame and lets
  MediaPipe LIVE_STREAM perform its intended busy-frame dropping; the optional
  single-in-flight mode remains available when lower GPU/CPU load is preferred,
- diagnostics now separate source Hz, submit Hz, result Hz, readback ms,
  inference ms, and render age so the next recording identifies its bottleneck,
- settings key v9 selects the new non-serial default instead of restoring v4.7.

V4.7 LATEST-FRAME LOW-LATENCY PIPELINE
---------------------------------------
- recording analysis found 45 exact duplicate frames in 134 transitions at
  30 fps: the Unity output was effectively repainting at about 20 fps,
- webcam tracking now starts GPU readback only when WebCamTexture reports a
  genuinely new frame; duplicate 1280x720 readbacks no longer consume render time,
- LIVE_STREAM keeps one request in flight and selects the newest camera frame
  next, preventing flow-limiter backlog and clustered callback corrections,
- source timing is latched before GPU readback, so render prediction compensates
  readback time instead of treating it as if the face frame were newer,
- movement correction response is 45 (previously 70), spreading measurement
  residuals over a few render frames while velocity feed-forward remains immediate,
- settings key v8 prevents the previous stronger correction value being restored.

V4.6 FLICKER-FREE LOW-LATENCY MOVEMENT
---------------------------------------
- Kiwi X/Y translation is advanced continuously at the render boundary from
  the measured position velocity instead of snapping at each LandMarker result,
- a new LandMarker position changes only the correction term, which converges
  exponentially without turning the whole model into a one-frame position jump,
- exact host-time deltas split LateUpdate and onBeforeRender work so movement
  time is never applied twice,
- steady movement remains feed-forward and therefore adds no smoothing delay,
- the runtime panel exposes predictive movement and correction response;
  settings key v7 prevents the prior direct-snap configuration being restored.

V4.5 ZERO-LAG KIWI BODY TRACKING
---------------------------------
- intentional rotation, X/Y movement, and depth motion bypass the final
  display smoother and use the newest render-time predicted pose directly,
- prediction is recalculated at `onBeforeRender`, removing the additional
  LateUpdate-to-render age even when no new LandMarker result arrived,
- genuinely newer LandMarker results received after LateUpdate can affect the
  same rendered frame instead of receiving only a partial 1/240-second step,
- microscopic/rest motion still uses display-rate smoothing, preserving the
  existing anti-jitter behavior,
- zero-lag motion and its three activation-speed thresholds are adjustable in
  the runtime panel; the v6 settings key prevents older slow values overriding
  the new defaults.

V4.4 CAMERA-EDGE FEATURE SAFETY
--------------------------------
- the whole mouth now fades out in 0.04 s when any actual outer-lip landmark
  reaches the camera texture edge, instead of showing a physically incomplete mouth,
- a separate 0.015 UV re-entry margin prevents rapid hide/show flicker at the edge,
- the decision uses the 20 real outer-lip landmarks, so crop padding alone crossing
  the texture boundary does not hide a fully captured mouth,
- both eye crops now preserve signed UV centers at camera edges, eliminating the
  position clamp that could move an entire eye across the avatar surface,
- mouth hide/show thresholds and fade times, plus eye edge-center preservation,
  are adjustable and saveable in the runtime Tracking panel (and Inspector).

V4.3 RAW-RESPONSE TRACKING
-----------------
- the supplied recording reported 8.6 ms inference but 57.7 ms result age;
  v4.3 compensates the complete measured age instead of only one sample interval,
- Ultra prediction strength is 1.00 with a 0.100 s safety ceiling; normal motion
  prediction now begins at 3 degrees/second rather than 12 degrees/second,
- display response is raised to 110 / 220 so correction remains continuous but
  adds only a few milliseconds instead of feeling damped,
- microscopic static locking defaults OFF; the adaptive micro-jitter filter stays ON,
- the 478-point MediaPipe debug annotation overlay defaults OFF for avatar use,
  removing presentation work that is unrelated to tracking,
- the panel now shows accepted tracking-result Hz and exposes full-age compensation,
  compensation ceiling, and the optional slower debug overlay,
- v4 saved tracking settings are not automatically reapplied over v4.3 defaults.

V4.3.2 MOUTH CONTOUR SAFETY
---------------------------
- the mouth crop is centered from all 20 outer-lip landmarks instead of only
  the two mouth corners,
- vertical and horizontal crop clearance expands immediately when the mouth
  contour exceeds the neutral crop, preventing wide-open clipping,
- mouth display scaling and angle correction use the top/bottom lip center as
  their vertical pivot, preventing downward drift while looking down,
- no temporal filter or calibration delay is added to the mouth path.

V4.3.3 CAMERA-EDGE CENTER PRESERVATION
---------------------------------------
- mouth UV rectangles keep their calculated center even when they extend past
  a camera-texture edge,
- signed UV overscan replaces the old position clamp that pushed the mouth to
  the bottom of Mouth3D when UV Rect Y reached zero,
- the mask shader samples valid pixels normally and makes only out-of-texture
  overscan transparent, avoiding repeated/stretched border pixels,
- the Inspector exposes live left/bottom/right/top overscan diagnostics.

V4.3.1 AVATAR-CENTRIC HORIZONTAL MOVEMENT
------------------------------------------
- tracked right movement maps to Kiwi's own right,
- for a front-facing Kiwi, own-right appears on the viewer's left,
- only body X translation is inverted; eye/mouth landmarks and yaw/roll remain unchanged,
- the mapping is configurable in the runtime panel and covered by validation.

V4.2 SMOOTH TRACKING FIX (RETAINED)
-----------------------------------
- accepted LandMarker poses are resampled continuously at the display rate,
  removing the visible 30 fps sample-and-hold staircase at 60/120 fps,
- adaptive exponential response keeps small steps smooth and raises catch-up
  speed for intentional motion without switching back to direct snapping,
- bounded prediction can bridge up to 0.028 s (also capped to 95% of the
  measured accepted-sample interval), compensating most smoothing delay,
- display smoothing and both response values are adjustable and persistent in
  the runtime panel,
- calibration, tracking-loss recovery, late latch, and the current scene all
  explicitly synchronize the new display-pose state.

V4.1 OPTIMIZATION (RETAINED)
----------------------------
- landmark consumers copy the 478-point array only when the accepted
  LandMarker timestamp changes; unchanged render frames use the prior buffer,
- the runtime panel caches stable labels, model names, slider text and
  GUILayout options to reduce IMGUI garbage while the panel is open,
- diagnostics text is refreshed at 5 Hz and does no work while the panel is hidden,
- saved tracking values are finite-checked and clamped before application,
- imported and manually dropped model names normalize '(1)' / 'Copy' suffixes,
- the editor validation menu checks direction invariants, prediction bounds,
  template hashes, naming rules and the changed-only consumer path.

At 120 display fps with 30 LandMarker results per second, changed-only access
can eliminate up to 75% of the previously repeated landmark-array copies. This
is a bounded data-copy improvement, not a claim that total frame time falls 75%.

CORE TRACKING POLICY
--------------------
MediaPipe Face LandMarker remains the only face tracker and source of truth.
No second detector or ML tracking model is added.

v4.3 improves the DISPLAY of accepted LandMarker results rather than claiming
that it can create more ground-truth information than LandMarker itself:
- newest accepted result can be consumed again immediately before render,
- a bounded motion-only prediction compensates part of result age while moving,
- prediction is suppressed at rest and during direction reversals,
- a microscopic adaptive filter and hysteresis rest lock remove visible jitter,
- large / intentional motion uses a fast continuous catch-up response,
- the Runner virtual-neck anchor is used for body X/Y to avoid Roll U-arcs.

DIRECTION INVARIANTS
--------------------
- avatar-right head turn stays avatar-right
- avatar-right tilt stays avatar-right
- combined right + down remains right + down
- Yaw  = -SignedAngle(euler.y)
- Roll = -SignedAngle(euler.z)
- tracked right translation maps to Kiwi-own-right (viewer-left when front-facing)

ULTRA PRESET DEFAULTS
---------------------
Ultra Low Latency Tracking      ON
Latest real sample before render ON
Motion-only render prediction    ON
Microscopic micro-filter         ON
Microscopic static pose lock     OFF
Pure core pose / body extras     ON
Runner position anchor           ON
Virtual neck extension           1.30
Prediction strength              1.00
Prediction max lead              0.100 s full measured-age compensation
Display-rate smoothing           ON
Display smoothing response       110
Fast catch-up response           220
Debug landmark overlay           OFF
Fresh webcam frames only         ON
Latest-frame-only LIVE_STREAM    ON
Movement correction response     45
Stop / reversal recovery          180
Velocity estimate base / steady   60 / 180
Camera interval compensation      0.50 (maximum 0.020 s)

Static lock is hysteretic and FPS-independent: the raw target must remain
inside one fixed microscopic corridor for about 80 ms before lock engages.
It releases once accumulated motion exceeds 2x the configured jitter zone.
Because the candidate point does not move with the output, genuine slow drift
cannot repeatedly re-lock while moving. A locked axis also disables its render
prediction, so prediction cannot recreate rest jitter.

PROTECTED MEDIAPIPE SETTINGS
----------------------------
- Windows uses bounded synchronous CPU readback; Android/iOS retain CPUAsync
- LIVE_STREAM retained
- only genuinely fresh WebCamTexture frames enter readback
- the generation-counted newest-frame mailbox prevents a FIFO backlog
- TextureFramePool capacity = 2 for CPU/CPUAsync and 4 for GPU ownership
- FaceLandmarkerResultAnnotationController.DrawLater(result) retained as an
  optional debug path and disabled by default
- BlendShapes retained
- Facial Transformation Matrixes retained
- no old Graphics.Blit / 360px tracking path
- no second face tracker

EYE / MOUTH TRACKING
--------------------
The eye/mouth landmark pipeline remains independent from body-X/Y stabilization.
v5.3 resamples crop, contour, rotation, and blink visibility at render rate while
the newest accepted Face LandMarker result remains the source of truth.
Mouth visible cap remains 0.50.

RUNTIME MODEL SYSTEM
--------------------
The in-app Kiwi Avatar System panel provides:
- model list and one-click Load
- Windows native VRM picker (no external picker plugin)
- Android / iOS native document picker
- Models folder access on Windows
- automatic transactional runtime VRM hot-swap
- embedded Kiwi fallback and rollback on load failure
- Ctrl+1 = embedded Kiwi, Ctrl+2..9 = external quick switch on desktop

External models are copied into the managed Models folder. Duplicate filenames
use clean suffixes such as Avatar_2.vrm; '(1)' / '(2)' style names are not
created by the runtime importer.

NON-SPHERICAL / STYLIZED MODEL FIT
----------------------------------
New external models default to Adaptive Head fit.
The adaptive analyzer uses Humanoid Head/Neck/Eyes plus sampled head-region mesh
geometry. When eye bones exist on an external model, semantic eye center / eye span are
used before head-bounds fallback. The embedded Kiwi has no Humanoid eye bones,
so v4.3 derives its semantic reference from the actual LeftEye3D/RightEye3D
visual positions. This lets the FaceAnchor containing LeftEye3D, RightEye3D and
Mouth3D adapt to different head widths, heights, proportions and non-spherical
silhouettes.

The panel also exposes:
- Adaptive Head Fit / Whole Height Fit
- Auto Eye/Face Fit / Legacy Face
- model X/Y and scale adjustment
- FaceAnchor X/Y/Z and scale adjustment
- Save Profile

Existing older profiles keep their previous appearance mode; new imports use
Adaptive Head by default. Adaptive geometry cache version 3 stores the eye semantic reference separately
from true Humanoid eye-bone availability and is rebuilt once for older caches.

SPRING / TAIL / HAIR
--------------------
VRM0 SpringBone is reconstructed after model hot-swap and after tracking-scale
changes. The panel provides Spring ON/OFF, Reset and Rebuild. Models that include
VRM SpringBone chains (tail, hair, accessories, etc.) therefore keep their
native natural secondary motion.

IN-APP TRACKING CONTROLS
------------------------
The panel exposes at runtime:
- Ultra tracking toggle
- micro-jitter filter
- microscopic static pose lock
- motion-only render prediction
- full measured-latency compensation
- maximum latency compensation
- source / submission / accepted-result Hz and readback latency
- smoothed Unity render fps
- fresh-camera-frame-only / optional single-in-flight LIVE_STREAM toggles
- optional debug landmark overlay
- adjustable LandMarker inference width
- flicker-free eye/mouth interpolation
- eye/mouth crop, contour and rotation response
- eye blink hide/show fade
- optional calibrated mouth-height lock (OFF by default)
- display-rate smoothing
- display smoothing / fast catch-up response
- latest-real-sample late latch
- pure tracking pose/body extras
- face motion amount
- Pitch / Yaw / Roll amounts
- screen X/Y movement
- avatar-centric X direction toggle
- depth movement
- prediction strength
- rotation / position / depth jitter zones
- Ultra Preset / Direct Raw / Recenter
- Save / Load Tracking settings
- secondary expression/body motion toggles and reaction amount
- live geometry quality, inference latency and render-age readout

PLATFORM NOTES
--------------
Windows:
- primary target
- native Win32 .vrm picker added
- existing VTuberOutput / Spout path is preserved

Android:
- native document picker copies directly into managed app storage
- model-size guard and low-memory behavior retained

Apple iOS:
- native UIDocumentPicker runtime path retained
- app-storage import behavior retained
- feature/static/build configuration is in scope
- physical iOS device verification is not required

SAFE TRACKING INSTALLER
-----------------------
Editor menus:
- Kiwi VTuber > Precision Tracking > Validate v1.0.0
- Kiwi VTuber > Precision Tracking > Apply v1.0.0 Safe Upgrade
- Kiwi VTuber > Precision Tracking > Force Upgrade With Backup...
- Kiwi VTuber > Optimization > Validate v1.0.0

The historical TrackingTemplates filenames are kept to preserve asset paths and
GUID compatibility. Their CONTENT is v1.0.0 and SHA-256 validated. Unknown
custom tracking scripts are never auto-overwritten without Force Upgrade.

IMPORTANT LIMITATION
--------------------
No software layer can reconstruct face information that the LandMarker model did
not detect. v5.3 can reduce visible phase delay during predictable motion and
reduce display jitter compared with presenting raw accepted samples directly,
but final camera-specific tuning still requires real webcam observation.
