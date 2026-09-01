param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackageRoot = Split-Path -Parent $ScriptRoot
$Source = Join-Path $PackageRoot "Native\KiwiNativeCameraPlugin.cpp"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PackageRoot "Build"
}

if (-not (Test-Path -LiteralPath $Source)) {
    throw "Native source missing: $Source"
}

$VsDevCmd = "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat"
if (-not (Test-Path -LiteralPath $VsDevCmd)) {
    throw "VS2022 Community VsDevCmd not found: $VsDevCmd"
}

$ProjectPluginApi = Join-Path $ProjectRoot "Tools\KlakSpout_v206_diag\Plugin\Unity"
$UnityPluginApi = "C:\Program Files\Unity\Hub\Editor\6000.0.80f1\Editor\Data\PluginAPI"
$PluginApi =
    if (Test-Path (Join-Path $ProjectPluginApi "IUnityGraphicsD3D12.h")) {
        $ProjectPluginApi
    } else {
        $UnityPluginApi
    }

if (-not (Test-Path (Join-Path $PluginApi "IUnityGraphicsD3D12.h"))) {
    throw "Unity PluginAPI headers not found: $PluginApi"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$Dll = Join-Path $OutputDirectory "KiwiNativeCamera.dll"
$Pdb = Join-Path $OutputDirectory "KiwiNativeCamera.pdb"
$Log = Join-Path $OutputDirectory "KiwiNativeCamera_v44_55_12_build.txt"
$Stdout = Join-Path $OutputDirectory "native_stdout.txt"
$Stderr = Join-Path $OutputDirectory "native_stderr.txt"
$CommandFile = Join-Path $OutputDirectory "build_native_v44_55_12.cmd"

$commandLines = @(
    "@echo off"
    "setlocal"
    "call `"$VsDevCmd`" -arch=amd64 -host_arch=amd64"
    "if errorlevel 1 exit /b %errorlevel%"
    "cl.exe /nologo /LD /std:c++17 /O2 /EHsc /MD /utf-8 /diagnostics:caret /DUNICODE /D_UNICODE /DUNITY_WIN=1 /I`"$PluginApi`" `"$Source`" /Fe:`"$Dll`" /Fd:`"$Pdb`" /link mf.lib mfplat.lib mfreadwrite.lib mfuuid.lib ole32.lib d3d11.lib d3d12.lib d3dcompiler.lib dxgi.lib"
    "set `"KIWI_NATIVE_BUILD_EXIT=%ERRORLEVEL%`""
    "endlocal & exit /b %KIWI_NATIVE_BUILD_EXIT%"
)

$commandLines | Set-Content -LiteralPath $CommandFile -Encoding ASCII
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = if ([string]::IsNullOrWhiteSpace($env:ComSpec)) { "cmd.exe" } else { $env:ComSpec }
$psi.Arguments = '/d /s /c ""' + $CommandFile + '""'
$psi.WorkingDirectory = $OutputDirectory
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true

$process = New-Object System.Diagnostics.Process
$process.StartInfo = $psi

if (-not $process.Start()) {
    throw "Failed to start native build."
}

$stdoutText = $process.StandardOutput.ReadToEnd()
$stderrText = $process.StandardError.ReadToEnd()
$process.WaitForExit()
$exitCode = $process.ExitCode

$stdoutText | Set-Content -LiteralPath $Stdout -Encoding UTF8
$stderrText | Set-Content -LiteralPath $Stderr -Encoding UTF8

@(
    "KiwiAvatarSystem v44.55.12 Native Diagnostic Build"
    "Source: $Source"
    "PluginAPI: $PluginApi"
    "ExitCode: $exitCode"
    ""
    "STDOUT:"
    $stdoutText
    ""
    "STDERR:"
    $stderrText
) | Set-Content -LiteralPath $Log -Encoding UTF8

if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $Dll)) {
    if (Test-Path $Log) {
        Get-Content -LiteralPath $Log -Tail 200
    }
    throw "Native plugin build failed. ExitCode=$exitCode"
}

Write-Host "V44_55_12_NATIVE_BUILD_PASS"
Write-Host "DLL: $Dll"
Write-Host "SHA256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $Dll).Hash)"
