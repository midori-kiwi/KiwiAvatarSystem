param(
    [Parameter(Mandatory = $false)]
    [string]$ProjectRoot = "D:\KiwiAvatarSystem"
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$ModulePath = Join-Path $ProjectRoot "Tools\KiwiPowerShell\KiwiPsCompat.psm1"
Import-Module $ModulePath -Force
$ProjectRoot = Resolve-KiwiProjectRoot -ProjectRoot $ProjectRoot

$ExpectedCriticalHashes = @{
    (Join-Path $ProjectRoot "Assets\Script\KiwiInferenceFaceTracker.cs") =
        "52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695"
    (Join-Path $ProjectRoot "Assets\Script\FaceLandmarkerRunner.cs") =
        "6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Camera\KiwiNativeCameraInterop.cs") =
        "AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5"
    (Join-Path $ProjectRoot "Assets\Plugins\x86_64\KiwiNativeCamera.dll") =
        "82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5"
    (Join-Path $ProjectRoot "Native\KiwiNativeCamera\Source\KiwiNativeCameraPlugin.cpp") =
        "636D76251F9CB3BB785F4497D3D0722033FC0CE36CB4A574B65EDA58B5ABE32A"
}

foreach ($path in @($ExpectedCriticalHashes.Keys | Sort-Object)) {
    [void](Assert-KiwiFileSha256 `
        -Path $path `
        -ExpectedSha256 ([string]$ExpectedCriticalHashes[$path]) `
        -Label "Smoke test protected file before")
}

$ValidationRoot = Join-Path $ProjectRoot "KiwiValidation\PowerShellCompatSmoke"
$OutputDirectory = New-KiwiTimestampedOutputDirectory `
    -Root $ValidationRoot `
    -Purpose "Smoke" `
    -AllowedRoots @((Join-Path $ProjectRoot "KiwiValidation"))

$Results = [System.Collections.Generic.List[object]]::new()

$textPath = Join-Path $OutputDirectory "safe-text.txt"
[void](Write-KiwiTextFile `
    -Path $textPath `
    -Text "KIWI_SAFE_TEXT" `
    -AllowedRoots @($OutputDirectory))
if ((Read-KiwiTextFile -Path $textPath) -ne "KIWI_SAFE_TEXT") {
    throw "Safe text read/write verification failed."
}
$Results.Add([pscustomobject]@{ test = "safeTextReadWrite"; status = "PASS" })

$boundaryRejected = $false
try {
    [void](Assert-KiwiPathWithinRoot `
        -Path (Join-Path $ProjectRoot "Assets\Plugins\x86_64\KiwiNativeCamera.dll") `
        -AllowedRoots @($OutputDirectory) `
        -Label "Expected rejection")
}
catch {
    $boundaryRejected = $true
}
if (-not $boundaryRejected) {
    throw "Path boundary rejection test failed."
}
$Results.Add([pscustomobject]@{ test = "pathBoundary"; status = "PASS" })

$sourcePath = Join-Path $OutputDirectory "transaction-source.txt"
$destinationPath = Join-Path $OutputDirectory "transaction-destination.txt"
$rollbackRoot = Join-Path $OutputDirectory "Rollback"
[void](Write-KiwiTextFile -Path $sourcePath -Text "REPLACEMENT" -AllowedRoots @($OutputDirectory))
[void](Write-KiwiTextFile -Path $destinationPath -Text "ORIGINAL" -AllowedRoots @($OutputDirectory))

$transaction = Copy-KiwiFileTransactional `
    -Source $sourcePath `
    -Destination $destinationPath `
    -AllowedDestinationRoots @($OutputDirectory) `
    -RollbackRoot $rollbackRoot
if ((Read-KiwiTextFile -Path $destinationPath) -ne "REPLACEMENT") {
    throw "Transactional copy verification failed."
}
Undo-KiwiFileTransaction -Transaction $transaction
if ((Read-KiwiTextFile -Path $destinationPath) -ne "ORIGINAL") {
    throw "Transactional rollback verification failed."
}

$transaction = Copy-KiwiFileTransactional `
    -Source $sourcePath `
    -Destination $destinationPath `
    -AllowedDestinationRoots @($OutputDirectory) `
    -RollbackRoot $rollbackRoot
[void](Complete-KiwiFileTransaction -Transaction $transaction)
if ((Read-KiwiTextFile -Path $destinationPath) -ne "REPLACEMENT") {
    throw "Transactional commit verification failed."
}
$Results.Add([pscustomobject]@{ test = "transactionCopyRollbackCommit"; status = "PASS" })

$processResult = Invoke-KiwiProcess `
    -Executable (Join-Path $PSHOME "powershell.exe") `
    -ArgumentList @(
        "-NoProfile",
        "-NonInteractive",
        "-Command",
        'Write-Output "KIWI_PROCESS_OK"; exit 0'
    ) `
    -WorkingDirectory $ProjectRoot `
    -LogDirectory $OutputDirectory `
    -AllowedLogRoots @($OutputDirectory) `
    -LogName "subprocess" `
    -TimeoutSeconds 30 `
    -ProtectedHashes $ExpectedCriticalHashes
if (
    $processResult.ExitCode -ne 0 -or
    $processResult.Stdout.IndexOf("KIWI_PROCESS_OK", [System.StringComparison]::Ordinal) -lt 0
) {
    throw "Subprocess execution verification failed."
}
$Results.Add([pscustomobject]@{ test = "subprocessExitLogAndProtectedHashes"; status = "PASS" })

$nonZeroRejected = $false
try {
    [void](Invoke-KiwiProcess `
        -Executable (Join-Path $PSHOME "powershell.exe") `
        -ArgumentList @(
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            'Write-Error "KIWI_EXPECTED_FAILURE"; exit 7'
        ) `
        -WorkingDirectory $ProjectRoot `
        -LogDirectory $OutputDirectory `
        -AllowedLogRoots @($OutputDirectory) `
        -LogName "subprocess-failure" `
        -TimeoutSeconds 30 `
        -ProtectedHashes $ExpectedCriticalHashes)
}
catch {
    $nonZeroRejected = $true
}
if (
    -not $nonZeroRejected -or
    -not (Test-Path -LiteralPath (Join-Path $OutputDirectory "subprocess-failure.stderr.log") -PathType Leaf)
) {
    throw "Non-zero subprocess rejection/log verification failed."
}
$Results.Add([pscustomobject]@{ test = "subprocessNonZeroRejected"; status = "PASS" })

$buildTools = Get-KiwiNativeBuildTools
if (-not $buildTools.Ready) {
    throw "Native build-tool discovery test failed."
}
$Results.Add([pscustomobject]@{
    test = "nativeBuildToolDiscovery"
    status = "PASS"
    cl = $buildTools.Cl
    link = $buildTools.Link
    dumpbin = $buildTools.Dumpbin
    windowsSdkVersion = $buildTools.WindowsSdkVersion
})

foreach ($path in @($ExpectedCriticalHashes.Keys | Sort-Object)) {
    [void](Assert-KiwiFileSha256 `
        -Path $path `
        -ExpectedSha256 ([string]$ExpectedCriticalHashes[$path]) `
        -Label "Smoke test protected file after")
}

$reportPath = Join-Path $OutputDirectory "KiwiPsCompatSmoke.txt"
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("Kiwi PowerShell 5.1 Compatibility Smoke Test")
$lines.Add("status=PASS")
$lines.Add("powershell=$($PSVersionTable.PSVersion)|edition=$($PSVersionTable.PSEdition)")
foreach ($result in $Results.ToArray()) {
    $lines.Add("test=$($result.test)|status=$($result.status)")
}
[void](Write-KiwiReport `
    -Path $reportPath `
    -Lines $lines.ToArray() `
    -AllowedRoots @($OutputDirectory))

Write-Host "KIWI PS COMPAT SMOKE PASS"
Write-Host "report=$reportPath"
