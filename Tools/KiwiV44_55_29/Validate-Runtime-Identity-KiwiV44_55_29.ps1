param(
    [Parameter(Mandatory=$true)]
    [string]$EvidenceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -lt 1) {
    throw "Windows PowerShell 5.1 is required. Current=$($PSVersionTable.PSVersion)"
}
if (-not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)) {
    throw "EvidenceRoot not found: $EvidenceRoot"
}

function Read-KeyValueReport([string]$Path) {
    $map = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        $index = $line.IndexOf('=')
        if ($index -le 0) { continue }
        $key = $line.Substring(0, $index).Trim()
        $value = $line.Substring($index + 1).Trim()
        if (-not $map.ContainsKey($key)) {
            $map[$key] = $value
        }
    }
    return $map
}

$requiredIdentityNames = @(
    'EXE',
    'ASSEMBLY_CSHARP',
    'INFERENCE_ENGINE_ASSEMBLY',
    'MEDIAPIPE_RUNTIME_ASSEMBLY',
    'INFERENCE_MODEL',
    'MEDIAPIPE_MODEL',
    'NATIVE_BUILD',
    'MEDIAPIPE_NATIVE_BUILD',
    'NATIVE_PRODUCTION',
    'TRACKER',
    'FACE_LANDMARKER_RUNNER',
    'KIWI_FACE_MOTION',
    'PACKAGES_MANIFEST',
    'PACKAGES_LOCK',
    'INFERENCE_PACKAGE_JSON',
    'MEDIAPIPE_PACKAGE_JSON',
    'SOURCE_PACKAGE_ZIP',
    'VENDOR_HOTFIX_PACKAGE_ZIP',
    'RUN_SCRIPT',
    'RUNTIME_VALIDATOR'
)

$identities = @{}
foreach ($arm in @('DIRECT_GRAPHICS','COMMAND_BUFFER_GRAPHICS','COMMAND_BUFFER_ASYNC')) {
    $runPath = Join-Path (Join-Path $EvidenceRoot $arm) 'run_identity.txt'
    if (-not (Test-Path -LiteralPath $runPath -PathType Leaf)) {
        throw "$arm run identity missing: $runPath"
    }
    $identity = Read-KeyValueReport $runPath
    $identities[$arm] = $identity

    if ($identity['sameBuildRequired'] -ne '1' -or $identity['armSelectionDifferenceOnly'] -ne '1') {
        throw "$arm did not assert the same-build/arm-selection-only contract."
    }
    if ([int]$identity['exitCode'] -ne 0) {
        throw "$arm exit code is not zero: $($identity['exitCode'])"
    }

    $expectedAsync = '0'
    $expectedCbGraphics = '0'
    if ($arm -eq 'COMMAND_BUFFER_ASYNC') {
        $expectedAsync = '1'
    }
    elseif ($arm -eq 'COMMAND_BUFFER_GRAPHICS') {
        $expectedCbGraphics = '1'
    }
    if ($identity['ENV.KIWI_INFERENCE_ASYNC_COMPUTE_PROBE'] -ne $expectedAsync -or
        $identity['ENV.KIWI_INFERENCE_COMMAND_BUFFER_GRAPHICS_PROBE'] -ne $expectedCbGraphics) {
        throw "$arm selection environment mismatch."
    }

    foreach ($name in $requiredIdentityNames) {
        $pathKey = 'identity.' + $name + '.path'
        $shaKey = 'identity.' + $name + '.sha256'
        if (-not $identity.ContainsKey($pathKey) -or -not $identity.ContainsKey($shaKey)) {
            throw "$arm identity is missing $name path/SHA."
        }
        $path = [string]$identity[$pathKey]
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "$arm current identity path missing for $name: $path"
        }
        $currentSha = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($currentSha -ne [string]$identity[$shaKey]) {
            throw "$arm current identity drift for $name. recorded=$($identity[$shaKey]) current=$currentSha"
        }
    }
}

foreach ($name in $requiredIdentityNames) {
    $key = 'identity.' + $name + '.sha256'
    $directSha = [string]$identities['DIRECT_GRAPHICS'][$key]
    $cbGraphicsSha = [string]$identities['COMMAND_BUFFER_GRAPHICS'][$key]
    $cbAsyncSha = [string]$identities['COMMAND_BUFFER_ASYNC'][$key]
    if ($directSha -ne $cbGraphicsSha -or $directSha -ne $cbAsyncSha) {
        throw "Same-build/source identity differs across arms for $name."
    }
}

if ($identities['DIRECT_GRAPHICS']['identity.SOURCE_PACKAGE_ZIP.sha256'] -ne
    'D4C5B5001E3730D6E9C03B803209C50E7C20EBF96EB31226C53908A9F0B0E0D7') {
    throw 'Source package identity mismatch.'
}
if ($identities['DIRECT_GRAPHICS']['identity.VENDOR_HOTFIX_PACKAGE_ZIP.sha256'] -ne
    'D4EE1B7C37B202C79A09AADE2A13795A565B9894BE423BE022284A8BFB11A8CB') {
    throw 'Vendor hotfix package identity mismatch.'
}

Write-Host 'KIWI_V44_55_29_RUNTIME_IDENTITY_VALIDATION_PASS' -ForegroundColor Green
Write-Host 'Same EXE, Assembly-CSharp, package assemblies, models, Native DLLs, protected source, package manifests, installed package.json files, and diagnostic harness SHA across all three arms.'
