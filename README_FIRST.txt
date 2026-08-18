KiwiAvatarSystem v5.3.0 - Low-Latency Flicker-Free Face Edition
======================================================

IMPORTANT - DO THIS BEFORE OPENING THE PROJECT IN UNITY
-------------------------------------------------------
1. Keep com.github.homuler.mediapipe-0.16.3.tgz anywhere you like outside this project.
2. Double-click SETUP_MEDIAPIPE.cmd.
3. Select com.github.homuler.mediapipe-0.16.3.tgz.
4. The setup writes the selected absolute tgz path into Packages/manifest.json.
5. Then add/open THIS KiwiAvatarSystem folder in Unity Hub using Unity 2022.3.62f2.

No Dependencies folder is used.
The MediaPipe tgz is not copied, unpacked, or embedded into the Unity project.
Do not move/delete the tgz after setup unless you run SETUP_MEDIAPIPE.cmd again and choose its new location.

Other packages are resolved by Unity Package Manager from configured registries:
- com.unity.burst 1.8.29
- com.unity.collections 2.6.7
- com.unity.mathematics 1.3.3
- com.unity.test-framework 1.4.6
- com.unity.ugui 1.0.0
- jp.keijiro.klak.spout 2.0.6

Project-integrated components already remain under Assets:
- KiwiAvatarSystem v5.3.0
- UniGLTF / UniVRM 0.130.1 / VRM 0.x
- Face Landmark Detection assets needed by this project
- face_landmarker_v2_with_blendshapes.bytes
- Kiwi Windows/Android/iOS runtime and bridge sources

If you open Unity before running SETUP_MEDIAPIPE.cmd, MediaPipe compile errors can occur because its package is intentionally not bundled.
