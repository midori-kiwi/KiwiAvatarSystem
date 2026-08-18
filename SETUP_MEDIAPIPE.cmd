@echo off
setlocal
cd /d "%~dp0"
echo ============================================================
echo KiwiAvatarSystem v3.7.1 - MediaPipe Setup
echo ============================================================
echo.
echo Select com.github.homuler.mediapipe-0.16.3.tgz.
echo The tgz will NOT be copied into this project.
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Set-MediaPipePackage.ps1" "%~1"
if errorlevel 1 (
  echo.
  echo Setup failed. Check the message above.
  pause
  exit /b 1
)
echo.
pause
