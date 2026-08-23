@echo off
setlocal

set "UNITY=C:\Program Files\Unity\Hub\Editor\6000.0.80f1\Editor\Unity.exe"
set "PROJECT=D:\KiwiAvatarSystem"

echo Launching KiwiAvatarSystem with Direct3D 12...
echo Unity:   %UNITY%
echo Project: %PROJECT%
echo.

"%UNITY%" -projectPath "%PROJECT%" -force-d3d12

endlocal
