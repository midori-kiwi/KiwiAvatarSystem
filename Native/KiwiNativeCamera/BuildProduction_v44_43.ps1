param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",
    [switch]$Install
)

$ErrorActionPreference = "Stop"

$Contract =
    "KIWI_V5_1_PHASE16_20_66_V44_43_PRODUCTION_MODE9_SOURCE_FORMALIZED"

$ExpectedSourceSha =
    "8C28F7A9E22DF9DA66455C37777069E5922037DB62AD7897DBDFEC8931975E03"

$ExpectedAbiSha =
    "42F41F24B75E72A936A328FA79A314EEB59C8DB74BFD9EA1C94D4D55791B31B5"

$ExpectedHlslSha =
    "B12D97A9D96C269670A5EA2C4104BB84528A93CF58023FF085B3594EF000DB71"

$NativeRoot =
    Join-Path $ProjectRoot "Native\KiwiNativeCamera"

$Source =
    Join-Path $NativeRoot "Source\KiwiNativeCameraPlugin.cpp"

$AbiCsv =
    Join-Path $NativeRoot "Validation\ABI_EXPORTS.csv"

$Hlsl =
    Join-Path $NativeRoot "Validation\KiwiNv12ToRgba.hlsl"

$ProbeSource =
    Join-Path $NativeRoot "Validation\KiwiHlslCompileProbe.cpp"

$BuildRoot =
    Join-Path $NativeRoot "Build\x86_64"

$AssetDll =
    Join-Path $ProjectRoot "Assets\Plugins\x86_64\KiwiNativeCamera.dll"

function Get-Sha([string]$Path)
{
    return (
        Get-FileHash `
            -LiteralPath $Path `
            -Algorithm SHA256
    ).Hash
}

function Find-VsDevCmd
{
    $programFilesX86 =
        [Environment]::GetEnvironmentVariable(
            "ProgramFiles(x86)")

    if ([string]::IsNullOrWhiteSpace($programFilesX86))
    {
        return $null
    }

    $vswhere =
        Join-Path `
            -Path $programFilesX86 `
            -ChildPath "Microsoft Visual Studio\Installer\vswhere.exe"

    if (!(Test-Path -LiteralPath $vswhere))
    {
        return $null
    }

    $installation =
        (
            & $vswhere `
                -latest `
                -products * `
                -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
                -property installationPath
        ) |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($installation))
    {
        return $null
    }

    $vsDevCmd =
        Join-Path `
            -Path $installation `
            -ChildPath "Common7\Tools\VsDevCmd.bat"

    if (!(Test-Path -LiteralPath $vsDevCmd))
    {
        return $null
    }

    return [pscustomobject]@{
        Installation = $installation
        VsDevCmd = $vsDevCmd
    }
}

function Find-UnityPluginHeaderDirectory
{
    $headers =
        @(
            Get-ChildItem `
                -LiteralPath $ProjectRoot `
                -Recurse `
                -File `
                -Filter "IUnityGraphicsD3D12.h" `
                -ErrorAction SilentlyContinue
        )

    foreach ($header in $headers)
    {
        $dir =
            $header.Directory.FullName

        if (
            (Test-Path -LiteralPath (Join-Path $dir "IUnityInterface.h")) -and
            (Test-Path -LiteralPath (Join-Path $dir "IUnityGraphics.h"))
        )
        {
            return $dir
        }
    }

    return ""
}

function Parse-DumpbinExports([string]$Path)
{
    $names =
        New-Object System.Collections.Generic.List[string]

    foreach ($line in Get-Content -LiteralPath $Path)
    {
        if (
            $line -match
            '^\s*\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+([A-Za-z_][A-Za-z0-9_]*)\b'
        )
        {
            $names.Add($Matches[1])
        }
    }

    return $names.ToArray()
}

foreach ($item in @(
    @($Source, $ExpectedSourceSha, "source"),
    @($AbiCsv, $ExpectedAbiSha, "ABI CSV"),
    @($Hlsl, $ExpectedHlslSha, "HLSL"),
    @($ProbeSource, "", "HLSL probe source")
))
{
    $path = [string]$item[0]
    $expected = [string]$item[1]
    $label = [string]$item[2]

    if (!(Test-Path -LiteralPath $path))
    {
        throw "$label missing: $path"
    }

    if (![string]::IsNullOrWhiteSpace($expected))
    {
        $actual = Get-Sha $path

        if ($actual -ne $expected)
        {
            throw "$label SHA mismatch. Expected=$expected Actual=$actual"
        }
    }
}

$sourceText =
    Get-Content `
        -LiteralPath $Source `
        -Raw

foreach ($required in @(
    "MF_MT_VIDEO_NOMINAL_RANGE",
    "MF_MT_YUV_MATRIX",
    "GetCurrentMediaType",
    "D3D12_FEATURE_D3D12_OPTIONS4",
    "SharedResourceCompatibilityTier",
    "kProductionMode = 9",
    "CreateShaderResourceView1",
    "GetCompletedValue"
))
{
    if (
        $sourceText.IndexOf(
            $required,
            [StringComparison]::Ordinal
        ) -lt 0
    )
    {
        throw "Required production token missing: $required"
    }
}

foreach ($forbidden in @(
    "UpdateExternalTexture",
    "D3D11On12CreateDevice",
    "CreateWrappedResource",
    "UnwrapUnderlyingResource",
    "ReturnUnderlyingResource",
    "sample->AddRef",
    "std::queue",
    "std::deque",
    "SetEventOnCompletion",
    "WaitForSingleObject"
))
{
    if (
        $sourceText.IndexOf(
            $forbidden,
            [StringComparison]::OrdinalIgnoreCase
        ) -ge 0
    )
    {
        throw "Forbidden frozen-Path-B token found: $forbidden"
    }
}

if (
    [regex]::IsMatch(
        $sourceText,
        'ID3D12CommandQueue[\s\S]*?->\s*Wait\s*\(')
)
{
    throw "Forbidden D3D12 queue Wait detected."
}

if (
    [regex]::IsMatch(
        $sourceText,
        'g_context11_4\s*->\s*Wait\s*\(')
)
{
    throw "Forbidden D3D11 context Wait detected."
}

# Known, deliberate limitation. Do not silently alias diagnostics 1..8 to mode 9.
if (
    $sourceText.IndexOf(
        "Reconstructed candidate implements Production diagnostic mode 9 only",
        [StringComparison]::Ordinal
    ) -lt 0
)
{
    throw "Expected explicit diagnostic mode 1..8 limitation marker is missing."
}

$vs =
    Find-VsDevCmd

if ($null -eq $vs)
{
    throw "Visual Studio C++ x64 build tools not found."
}

$unityHeaders =
    Find-UnityPluginHeaderDirectory

if ([string]::IsNullOrWhiteSpace($unityHeaders))
{
    throw "Unity PluginAPI headers not found under ProjectRoot."
}

Remove-Item `
    -LiteralPath $BuildRoot `
    -Recurse `
    -Force `
    -ErrorAction SilentlyContinue

New-Item `
    -ItemType Directory `
    -Path $BuildRoot `
    -Force |
    Out-Null

$Dll =
    Join-Path $BuildRoot "KiwiNativeCamera.dll"

$Pdb =
    Join-Path $BuildRoot "KiwiNativeCamera.pdb"

$Map =
    Join-Path $BuildRoot "KiwiNativeCamera.map"

$ProbeExe =
    Join-Path $BuildRoot "KiwiHlslCompileProbe.exe"

$ProbeOut =
    Join-Path $BuildRoot "hlsl_compile_probe.txt"

$DumpExports =
    Join-Path $BuildRoot "dumpbin.exports.txt"

$DumpImports =
    Join-Path $BuildRoot "dumpbin.imports.txt"

$Stdout =
    Join-Path $BuildRoot "msvc.stdout.txt"

$Stderr =
    Join-Path $BuildRoot "msvc.stderr.txt"

$Cmd =
    Join-Path $BuildRoot "build.cmd"

$cmdTemplate =
@'
@echo off
setlocal
call "__VSDEVCMD__" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b %errorlevel%

pushd "__BUILDROOT__"
if errorlevel 1 exit /b %errorlevel%

cl.exe /nologo /std:c++17 /EHsc /MD /O2 /W4 /permissive- /Zc:__cplusplus /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /I"__UNITYHEADERS__" /LD "__SOURCE__" /Fe:"KiwiNativeCamera.dll" /link /DEBUG:FULL /PDB:"KiwiNativeCamera.pdb" /MAP:"KiwiNativeCamera.map"
if errorlevel 1 (popd & exit /b %errorlevel%)

cl.exe /nologo /std:c++17 /EHsc /MD /O2 /W4 "__PROBESOURCE__" /Fe:"KiwiHlslCompileProbe.exe"
if errorlevel 1 (popd & exit /b %errorlevel%)

"KiwiHlslCompileProbe.exe" "__HLSL__" > "__PROBEOUT__" 2>&1
if errorlevel 1 (popd & exit /b %errorlevel%)

dumpbin.exe /exports "KiwiNativeCamera.dll" > "__EXPORTS__"
if errorlevel 1 (popd & exit /b %errorlevel%)

dumpbin.exe /imports "KiwiNativeCamera.dll" > "__IMPORTS__"
if errorlevel 1 (popd & exit /b %errorlevel%)

popd
exit /b 0
'@

$cmdText =
    $cmdTemplate

$replacements =
    [ordered]@{
        "__VSDEVCMD__" = $vs.VsDevCmd
        "__BUILDROOT__" = $BuildRoot
        "__UNITYHEADERS__" = $unityHeaders
        "__SOURCE__" = $Source
        "__PROBESOURCE__" = $ProbeSource
        "__HLSL__" = $Hlsl
        "__PROBEOUT__" = $ProbeOut
        "__EXPORTS__" = $DumpExports
        "__IMPORTS__" = $DumpImports
    }

foreach ($entry in $replacements.GetEnumerator())
{
    $cmdText =
        $cmdText.Replace(
            [string]$entry.Key,
            ([string]$entry.Value).Replace('"', '""'))
}

if (
    [regex]::IsMatch(
        $cmdText,
        '__[A-Z0-9_]+__')
)
{
    throw "Unresolved build template placeholder."
}

$cmdText |
    Set-Content `
        -LiteralPath $Cmd `
        -Encoding ASCII

$process =
    Start-Process `
        -FilePath $env:ComSpec `
        -ArgumentList @(
            "/d",
            "/s",
            "/c",
            "`"$Cmd`""
        ) `
        -Wait `
        -PassThru `
        -NoNewWindow `
        -RedirectStandardOutput $Stdout `
        -RedirectStandardError $Stderr

if ($process.ExitCode -ne 0)
{
    Write-Host "===== MSVC STDOUT ====="
    Get-Content -LiteralPath $Stdout -ErrorAction SilentlyContinue

    Write-Host "===== MSVC STDERR ====="
    Get-Content -LiteralPath $Stderr -ErrorAction SilentlyContinue

    throw "Native build failed. ExitCode=$($process.ExitCode)"
}

if (!(Test-Path -LiteralPath $Dll))
{
    throw "Native DLL was not generated."
}

$probeText =
    Get-Content `
        -LiteralPath $ProbeOut `
        -Raw

if (
    $probeText -notmatch 'HRESULT=0x00000000' -or
    $probeText -notmatch 'BytecodeBytes=([1-9][0-9]*)'
)
{
    throw "Embedded HLSL equivalent compile probe failed."
}

$expectedExports =
    @(
        Import-Csv `
            -LiteralPath $AbiCsv |
        Select-Object -ExpandProperty export
    )

$actualExports =
    @(
        Parse-DumpbinExports $DumpExports
    )

if (
    $expectedExports.Count -ne 66 -or
    $actualExports.Count -ne 66
)
{
    throw "Export count mismatch. Expected=$($expectedExports.Count) Actual=$($actualExports.Count)"
}

for ($i = 0; $i -lt 66; $i++)
{
    if ($expectedExports[$i] -ne $actualExports[$i])
    {
        throw "Export mismatch index=$i Expected=$($expectedExports[$i]) Actual=$($actualExports[$i])"
    }
}

$importsText =
    Get-Content `
        -LiteralPath $DumpImports `
        -Raw

if (
    $importsText.IndexOf(
        "D3D11On12CreateDevice",
        [StringComparison]::OrdinalIgnoreCase
    ) -ge 0
)
{
    throw "Forbidden D3D11On12CreateDevice import found."
}

$dllSha =
    Get-Sha $Dll

$buildResult =
    [ordered]@{
        Contract = $Contract
        SourceSHA256 = $ExpectedSourceSha
        AbiSHA256 = $ExpectedAbiSha
        HlslSHA256 = $ExpectedHlslSha
        DllSHA256 = $dllSha
        ExportCount = 66
        DiagnosticModes1To8 = "E_NOTIMPL by design; Production uses mode 9"
        InstallRequested = [bool]$Install
        Installed = $false
    }

if ($Install)
{
    $assetDir =
        Split-Path `
            -Parent `
            $AssetDll

    New-Item `
        -ItemType Directory `
        -Path $assetDir `
        -Force |
        Out-Null

    Copy-Item `
        -LiteralPath $Dll `
        -Destination $AssetDll `
        -Force

    $installedSha =
        Get-Sha $AssetDll

    if ($installedSha -ne $dllSha)
    {
        throw "Installed DLL hash mismatch."
    }

    $buildResult.Installed =
        $true
}

$buildResult |
    ConvertTo-Json -Depth 5 |
    Set-Content `
        -LiteralPath (
            Join-Path $BuildRoot "BUILD_RESULT_v44_43.json"
        ) `
        -Encoding UTF8

Write-Host ""
Write-Host "SourceSHA256=$ExpectedSourceSha"
Write-Host "DllSHA256=$dllSha"
Write-Host "ExportCount=66"
Write-Host "HlslCompile=PASS"
Write-Host "DiagnosticModes1To8=E_NOTIMPL"
Write-Host "Installed=$([int][bool]$buildResult.Installed)"
Write-Host "RESULT=NATIVE_BUILD_PASS"
