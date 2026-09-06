param(
    [string]$ProjectRoot = 'D:\KiwiAvatarSystem',
    [string]$UnityExe = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$ExpectedTracker = '1DBAAB45A5393D688B2ACE2A4FB5FBA995FE7B09E12843D3DAD536A15D8C539A'
$ExpectedRunner = '6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93'
$ExpectedFaceMotion = 'D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6'
$ExpectedNative = '82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5'

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Assert-Hash([string]$Path, [string]$Expected, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing ${Label}: $Path"
    }
    $actual = Get-Sha256 $Path
    if ($actual -ne $Expected) {
        throw "$Label SHA mismatch. Expected=$Expected Actual=$actual"
    }
}

if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -lt 1) {
    throw "Windows PowerShell 5.1 is required. Current=$($PSVersionTable.PSVersion)"
}

Assert-Hash (Join-Path $ProjectRoot 'Assets\Script\KiwiInferenceFaceTracker.cs') $ExpectedTracker 'Tracker'
Assert-Hash (Join-Path $ProjectRoot 'Assets\Script\FaceLandmarkerRunner.cs') $ExpectedRunner 'FaceLandmarkerRunner'
Assert-Hash (Join-Path $ProjectRoot 'Assets\Script\KiwiFaceMotion.cs') $ExpectedFaceMotion 'KiwiFaceMotion'
$nativePath = Join-Path $ProjectRoot 'Assets\Plugins\x86_64\KiwiNativeCamera.dll'
if (Test-Path -LiteralPath $nativePath -PathType Leaf) {
    Assert-Hash $nativePath $ExpectedNative 'Native DLL'
}

if ([string]::IsNullOrWhiteSpace($UnityExe)) {
    $candidate = 'C:\Program Files\Unity\Hub\Editor\6000.0.80f1\Editor\Unity.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $UnityExe = $candidate
    }
    else {
        throw 'UnityExe was not provided and Unity 6000.0.80f1 was not found at the standard Hub path.'
    }
}

if (-not (Test-Path -LiteralPath $UnityExe -PathType Leaf)) {
    throw "Unity executable not found: $UnityExe"
}

$ValidationDir = Join-Path $ProjectRoot 'KiwiValidation'
New-Item -ItemType Directory -Path $ValidationDir -Force | Out-Null
$LogPath = Join-Path $ValidationDir 'KiwiBuild_v44_55_29.log'

Write-Host '[BUILD] Unity batchmode v44.55.29' -ForegroundColor Cyan
$unityArguments = @(
    '-batchmode',
    '-nographics',
    '-quit',
    '-projectPath',
    $ProjectRoot,
    '-executeMethod',
    'KiwiBuildV44_55_29.Build',
    '-logFile',
    $LogPath
)
$unityProcess = Start-Process `
    -FilePath $UnityExe `
    -ArgumentList $unityArguments `
    -Wait `
    -PassThru `
    -WindowStyle Hidden

$exitCode = $unityProcess.ExitCode
if ($exitCode -ne 0) {
    throw "Unity build failed with exit code $exitCode. Log=$LogPath"
}

$ExePath = Join-Path $ProjectRoot 'Builds\v44_55_29CommandBufferGraphicsExecutionFormIsolation\KiwiAvatarSystem_v44_55_29_COMMAND_BUFFER_GRAPHICS_EXECUTION_FORM_ISOLATION.exe'
$AssemblyPath = Join-Path $ProjectRoot 'Builds\v44_55_29CommandBufferGraphicsExecutionFormIsolation\KiwiAvatarSystem_v44_55_29_COMMAND_BUFFER_GRAPHICS_EXECUTION_FORM_ISOLATION_Data\Managed\Assembly-CSharp.dll'

if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "Expected Player not found: $ExePath"
}
if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) {
    throw "Expected Assembly-CSharp.dll not found: $AssemblyPath"
}

$identityPath = Join-Path $ValidationDir 'KiwiBuildIdentity_v44_55_29.txt'
@(
    'contract=KIWI_V44_55_29_COMMAND_BUFFER_GRAPHICS_EXECUTION_FORM_ISOLATION',
    ('builtUtc=' + [DateTime]::UtcNow.ToString('O')),
    ('EXE.path=' + $ExePath),
    ('EXE.sha256=' + (Get-Sha256 $ExePath)),
    ('ASSEMBLY_CSHARP.path=' + $AssemblyPath),
    ('ASSEMBLY_CSHARP.sha256=' + (Get-Sha256 $AssemblyPath)),
    ('TRACKER.sha256=' + (Get-Sha256 (Join-Path $ProjectRoot 'Assets\Script\KiwiInferenceFaceTracker.cs'))),
    ('NATIVE.sha256=' + $(if (Test-Path -LiteralPath $nativePath) { Get-Sha256 $nativePath } else { '<MISSING>' }))
) | Set-Content -LiteralPath $identityPath -Encoding UTF8

Write-Host ''
Write-Host 'KIWI_V44_55_29_BUILD_PASS' -ForegroundColor Green
Write-Host "EXE: $ExePath"
Write-Host "EXE SHA256: $(Get-Sha256 $ExePath)"
Write-Host "Assembly-CSharp SHA256: $(Get-Sha256 $AssemblyPath)"
Write-Host "Build identity: $identityPath"
Write-Host "Build log: $LogPath"
