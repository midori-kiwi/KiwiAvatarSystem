KiwiAvatarSystem v44.55.12
D3D Fixed-Point Subtexel Sampling Parity Audit
================================================

WHY
---
v44.55.11 identity correction succeeded for all 93 pairs:
- corrected input mean 0.04593 LSB
- p95 0.05104 LSB
- max 0.05375 LSB
- 13/13 Presentation-race pairs corrected

But raw output narrowly missed STRONG:
- landmark 2D p95 0.776 px (gate 0.75)
- within 1px 98.77% (gate 99%)
- presence sigmoid p95 0.0297 (gate 0.01)

OFFICIAL BASIS
--------------
D3D linear sampling:
1. normalized U/V is scaled by texture size;
2. 0.5 is subtracted for linear addressing;
3. texel-space coordinate is converted to at least 16.8 fixed point;
4. ideal/reference float->fixed conversion uses round-to-nearest-even;
5. filter weights are derived from the snapped coordinate.

Unity Graphics.Blit samples source through a shader.
Unity InferenceEngine only applies linear resampling when texture and tensor
spatial sizes differ; frozen crop and tensor are both 192x192 here.

AUDIT CANDIDATES
----------------
Everything except subtexel coordinate precision is frozen:
- frame identity: v44.55.10 target-or-neighbor correction
- matrix: Production lane _Xform
- NV12 color/chroma: unchanged
- Presentation store: UNORM8
- crop destination store: UNORM8

Candidates:
- FULL_FLOAT
- 16.8 fixed-point
- 16.9
- 16.10
- 16.12

The snap occurs on sourceX/sourceY AFTER texture-size scaling and -0.5, not on
normalized UV.

DECISION
--------
SUBTEXEL_PARITY_SUPPORTED:
- one fixed candidate global mean <=0.020 LSB
- pair mean p95 <=0.030 LSB
- >=50% global-mean improvement vs FULL_FLOAT
- API errors 0
- frozen snapshot -> observer tensor remains exact

EXACTISH:
- best global mean <=0.005 LSB

If supported, rerun identity-corrected CPU/GPU raw-output equivalence using the
best fixed-point mode before any Production CPU backend A/B.

SAFETY
------
Production Native path unchanged. Only diagnostic sampler/export added.
No IMFSample retention. No FIFO. No D3D12 queue Wait. No UpdateExternalTexture.
No Production tracker/worker/ROI/threshold changes.

Base v44.55.11 Native SHA256:
367D6407803220DD141587F16B2F8A4B76E908B53A1A558C67882BB6BE285959

v44.55.12 Native SHA256:
636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A

v44.55.12 Interop SHA256:
AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5

v44.55.12 Observer SHA256:
BE98143C691D2980013599135FE7F5083EC446C709006CF5BCFF64A22F10EF58

RUN
---
Set-ExecutionPolicy -Scope Process Bypass

.\Installer\Apply-v44.55.12.ps1 -ProjectRoot "D:\KiwiAvatarSystem"
.\Installer\Validate-v44.55.12.ps1 -ProjectRoot "D:\KiwiAvatarSystem"
.\Launch-v44.55.12-BuildAndAudit.ps1 -ProjectRoot "D:\KiwiAvatarSystem"

Wait for BOTH:
[Kiwi v44.55.12 Subtexel] MEASURE_START
[Kiwi v44.47 Steady Audit] MEASURE_START

Record at least 60 seconds.
