param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$modulePath = Join-Path $PSScriptRoot "..\KiwiPowerShell\KiwiPsCompat.psm1"
Import-Module -Name $modulePath -Force

function Write-NewPlayerLogLines {
    param(
        [string]$Path,
        [ref]$Offset
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return
    }

    try {
        $stream = New-Object System.IO.FileStream(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite
        )

        try {
            if ($Offset.Value -gt $stream.Length) {
                $Offset.Value = 0L
            }

            [void]$stream.Seek($Offset.Value, [System.IO.SeekOrigin]::Begin)
            $reader = New-Object System.IO.StreamReader($stream)

            try {
                while (-not $reader.EndOfStream) {
                    $line = $reader.ReadLine()

                    if (
                        $line -match "\[Kiwi v44\.55\.20 CommonTensor\].*(GATE_MATCH|READY_FOR_WARMUP|MEASURE_START|COMPLETE|OBSERVER_FAULT)" -or
                        $line -match "\[Kiwi v44\.55\.24 PairBoundOutput\].*(DEPENDENCY_BOUND|PAIR_ATTACHED|SNAPSHOT_SUBMITTED|RECORD_BOUND|SAMPLE|COMPLETE|OBSERVER_FAULT)" -or
                        $line -match "\[Kiwi v44\.55\.25 ScheduleTrace\].*(DEPENDENCY_BOUND|PAIR_ATTACHED|OUTPUT_READY_BOUND|RECORD_BOUND|SCHEDULE_MISMATCH|COMPLETE|OBSERVER_FAULT)"
                    ) {
                        Write-Host $line
                    }
                }

                $Offset.Value = $stream.Position
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    catch {
        Write-Verbose $_.Exception.Message
    }
}

$ProjectRoot = Resolve-KiwiProjectRoot -ProjectRoot $ProjectRoot
$exePath = Assert-KiwiPathWithinRoot `
    -Path (Join-Path $ProjectRoot "Builds\v44_55_25ProductionScheduleTransactionTrace\KiwiAvatarSystem_v44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE.exe") `
    -AllowedRoots @($ProjectRoot) `
    -Label "v44.55.25 Development Player"
[void](Assert-KiwiFile -Path $exePath -Label "v44.55.25 Development Player")

[Environment]::SetEnvironmentVariable(
    "KIWI_V44_55_20_COMMON_TENSOR_AUDIT",
    "1",
    "Process")
[Environment]::SetEnvironmentVariable(
    "KIWI_V44_55_20_COMMON_TENSOR_SECONDS",
    "120",
    "Process")
[Environment]::SetEnvironmentVariable(
    "KIWI_V44_55_20_COMMON_TENSOR_HZ",
    "1",
    "Process")
[Environment]::SetEnvironmentVariable(
    "KIWI_V44_55_20_STABLE_SECONDS",
    "8",
    "Process")
[Environment]::SetEnvironmentVariable(
    "KIWI_V44_55_21_CROP_SAMPLER_AUDIT",
    $null,
    "Process")
[Environment]::SetEnvironmentVariable(
    "KIWI_V44_55_22_REFERENCE_TRANSACTION_AUDIT",
    $null,
    "Process")
[Environment]::SetEnvironmentVariable(
    "KIWI_V44_55_23_PRODUCTION_OUTPUT_AUTHORITY_AUDIT",
    $null,
    "Process")
[Environment]::SetEnvironmentVariable(
    "KIWI_V44_55_24_PAIR_BOUND_SHADOW_OUTPUT_AUDIT",
    "1",
    "Process")
[Environment]::SetEnvironmentVariable(
    "KIWI_V44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE",
    "1",
    "Process")

Write-Host "KiwiAvatarSystem v44.55.25 Production Schedule Transaction Trace"
Write-Host "Correctness observer only; performanceAuthority=0"
Write-Host "Keep stable face tracking through COMPLETE."
Write-Host "Runtime outputs: KiwiProductionScheduleTransactionTrace_v44_55_25_<stamp>.txt/.csv"

$persistentDirectory = Join-Path $env:USERPROFILE `
    "AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem"
$playerLog = Join-Path $persistentDirectory "Player.log"
$offset = 0L

if (Test-Path -LiteralPath $playerLog -PathType Leaf) {
    $offset = (Get-Item -LiteralPath $playerLog).Length
}

$process = Start-Process -FilePath $exePath -PassThru

while (-not $process.WaitForExit(500)) {
    Write-NewPlayerLogLines -Path $playerLog -Offset ([ref]$offset)
}

$process.Refresh()
Write-NewPlayerLogLines -Path $playerLog -Offset ([ref]$offset)

if ($process.ExitCode -ne 0) {
    throw "v44.55.25 Player exited with code $($process.ExitCode)."
}

Write-Host "Player exited with code 0."
Write-Host "Run Validate-KiwiV44_55_25.ps1 with the exact EvidenceStamp."
