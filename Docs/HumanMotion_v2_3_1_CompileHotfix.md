# Human Motion v2.3.1 Compile Hotfix

The v2.3 package accidentally contained two source files defining the same
`Mediapipe.Unity.Sample.FaceLandmarkDetection.KiwiInferenceFaceTracker` type.

v2.3.1 uses the original project path as the only canonical implementation:

`Assets/Script/KiwiInferenceFaceTracker.cs`

The accidental duplicate path is overwritten with a comment-only `.cs` file,
so drag-and-drop installation fixes the existing local project without a manual
delete step.

All v2.3 async mailbox and motion fixes are preserved.
