@echo off
setlocal
call "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b %errorlevel%

pushd "D:\KiwiAvatarSystem\Native\KiwiNativeCamera\Build\x86_64"
if errorlevel 1 exit /b %errorlevel%

cl.exe /nologo /std:c++17 /EHsc /MD /O2 /W4 /permissive- /Zc:__cplusplus /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /I"D:\KiwiAvatarSystem\Tools\KlakSpout_v206_diag\Plugin\Unity" /LD "D:\KiwiAvatarSystem\Native\KiwiNativeCamera\Source\KiwiNativeCameraPlugin.cpp" /Fe:"KiwiNativeCamera.dll" /link /DEBUG:FULL /PDB:"KiwiNativeCamera.pdb" /MAP:"KiwiNativeCamera.map"
if errorlevel 1 (popd & exit /b %errorlevel%)

cl.exe /nologo /std:c++17 /EHsc /MD /O2 /W4 "D:\KiwiAvatarSystem\Native\KiwiNativeCamera\Validation\KiwiHlslCompileProbe.cpp" /Fe:"KiwiHlslCompileProbe.exe"
if errorlevel 1 (popd & exit /b %errorlevel%)

"KiwiHlslCompileProbe.exe" "D:\KiwiAvatarSystem\Native\KiwiNativeCamera\Validation\KiwiNv12ToRgba.hlsl" > "D:\KiwiAvatarSystem\Native\KiwiNativeCamera\Build\x86_64\hlsl_compile_probe.txt" 2>&1
if errorlevel 1 (popd & exit /b %errorlevel%)

dumpbin.exe /exports "KiwiNativeCamera.dll" > "D:\KiwiAvatarSystem\Native\KiwiNativeCamera\Build\x86_64\dumpbin.exports.txt"
if errorlevel 1 (popd & exit /b %errorlevel%)

dumpbin.exe /imports "KiwiNativeCamera.dll" > "D:\KiwiAvatarSystem\Native\KiwiNativeCamera\Build\x86_64\dumpbin.imports.txt"
if errorlevel 1 (popd & exit /b %errorlevel%)

popd
exit /b 0
