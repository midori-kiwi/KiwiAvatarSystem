param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",
    [int]$TimeoutSeconds = 360
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0
$modulePath = Join-Path $PSScriptRoot "..\KiwiPowerShell\KiwiPsCompat.psm1"
Import-Module -Name $modulePath -Force
$ProjectRoot = Resolve-KiwiProjectRoot -ProjectRoot $ProjectRoot
$exe = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot "Builds\v44_55_26ProductionDecodePayloadTransactionClosure\KiwiAvatarSystem_v44_55_26_PRODUCTION_DECODE_PAYLOAD_TRANSACTION_CLOSURE.exe") -AllowedRoots @($ProjectRoot) -Label "v26 Player"
[void](Assert-KiwiFile -Path $exe -Label "v26 Player")

$settings = @{
    "KIWI_V44_55_20_COMMON_TENSOR_AUDIT" = "1"
    "KIWI_V44_55_20_COMMON_TENSOR_SECONDS" = "120"
    "KIWI_V44_55_20_COMMON_TENSOR_HZ" = "1"
    "KIWI_V44_55_20_STABLE_SECONDS" = "8"
    "KIWI_V44_55_24_PAIR_BOUND_SHADOW_OUTPUT_AUDIT" = "1"
    "KIWI_V44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE" = "1"
    "KIWI_V44_55_26_PRODUCTION_DECODE_PAYLOAD_TRACE" = "1"
    "KIWI_V44_55_21_CROP_SAMPLER_AUDIT" = $null
    "KIWI_V44_55_22_REFERENCE_TRANSACTION_AUDIT" = $null
    "KIWI_V44_55_23_PRODUCTION_OUTPUT_AUTHORITY_AUDIT" = $null
    "KIWI_ORT_DML_SHADOW" = $null
    "KIWI_ORT_DML_ZERO_COPY_SHADOW" = $null
}
foreach ($name in @($settings.Keys)) {
    [Environment]::SetEnvironmentVariable($name, $settings[$name], "Process")
}

$persistent = Join-Path $env:USERPROFILE "AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem"
$playerLog = Join-Path $persistent "Player.log"
$offset = 0L
if (Test-Path -LiteralPath $playerLog -PathType Leaf) {
    $offset = (Get-Item -LiteralPath $playerLog).Length
}
Write-Host "KiwiAvatarSystem v44.55.26 Decode Payload Transaction Closure"
Write-Host "Correctness observer only; keep a stable visible face through COMPLETE."
$process = Start-Process -FilePath $exe -PassThru
$timer = [System.Diagnostics.Stopwatch]::StartNew()
$completeSeen = $false
while (-not $process.WaitForExit(500)) {
    if (Test-Path -LiteralPath $playerLog -PathType Leaf) {
        $stream = New-Object System.IO.FileStream($playerLog, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            if ($offset -gt $stream.Length) { $offset = 0L }
            [void]$stream.Seek($offset, [System.IO.SeekOrigin]::Begin)
            $reader = New-Object System.IO.StreamReader($stream)
            try {
                while (-not $reader.EndOfStream) {
                    $line = $reader.ReadLine()
                    if ($line -match '\[Kiwi v44\.55\.(20|24|25|26).*(GATE_MATCH|MEASURE_START|DEPENDENCY_BOUND|SAMPLE|COMPLETE|OBSERVER_FAULT|MISMATCH)') {
                        Write-Host $line
                    }
                    if ($line -match '\[Kiwi v44\.55\.26 DecodePayload\] COMPLETE') {
                        $completeSeen = $true
                    }
                }
                $offset = $stream.Position
            }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    if ($completeSeen) {
        [void]$process.CloseMainWindow()
    }
    if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
        [void]$process.CloseMainWindow()
        throw "v44.55.26 Runtime timed out after $TimeoutSeconds seconds."
    }
}
$process.Refresh()
if ($process.ExitCode -ne 0) { throw "v44.55.26 Player exited with code $($process.ExitCode)." }
if (-not $completeSeen) { throw "Player exited before v44.55.26 COMPLETE." }
Write-Host "Player exited with code 0 after v44.55.26 COMPLETE."
