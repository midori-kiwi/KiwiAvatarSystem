@echo off
setlocal
call "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64
if errorlevel 1 exit /b %errorlevel%
cl.exe /nologo /LD /std:c++17 /O2 /EHsc /MD /utf-8 /diagnostics:caret /DUNICODE /D_UNICODE /DUNITY_WIN=1 /I"D:\KiwiAvatarSystem\Tools\KlakSpout_v206_diag\Plugin\Unity" "D:\Users\main\Downloads\KiwiAvatarSystem_v44_55_12_D3DFixedPointSubtexelSamplingParityAudit\Native\KiwiNativeCameraPlugin.cpp" /Fe:"D:\Users\main\Downloads\KiwiAvatarSystem_v44_55_12_D3DFixedPointSubtexelSamplingParityAudit\Build\KiwiNativeCamera.dll" /Fd:"D:\Users\main\Downloads\KiwiAvatarSystem_v44_55_12_D3DFixedPointSubtexelSamplingParityAudit\Build\KiwiNativeCamera.pdb" /link mf.lib mfplat.lib mfreadwrite.lib mfuuid.lib ole32.lib d3d11.lib d3d12.lib d3dcompiler.lib dxgi.lib
set "KIWI_NATIVE_BUILD_EXIT=%ERRORLEVEL%"
endlocal & exit /b %KIWI_NATIVE_BUILD_EXIT%
