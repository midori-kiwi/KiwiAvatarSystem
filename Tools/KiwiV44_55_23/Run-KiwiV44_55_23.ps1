param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)

$ExePath = Join-Path $ProjectRoot `
    "Builds\v44_55_23ProductionOutputAuthority\KiwiAvatarSystem_v44_55_23_OUTPUT_AUTHORITY.exe"

if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "v44.55.23 Development Player missing: $ExePath"
}

$env:KIWI_V44_55_20_COMMON_TENSOR_AUDIT = "1"
$env:KIWI_V44_55_20_COMMON_TENSOR_SECONDS = "120"
$env:KIWI_V44_55_20_COMMON_TENSOR_HZ = "1"
$env:KIWI_V44_55_20_STABLE_SECONDS = "8"

Remove-Item Env:KIWI_V44_55_21_CROP_SAMPLER_AUDIT -ErrorAction SilentlyContinue
Remove-Item Env:KIWI_V44_55_22_REFERENCE_TRANSACTION_AUDIT -ErrorAction SilentlyContinue

$env:KIWI_V44_55_23_PRODUCTION_OUTPUT_AUTHORITY_AUDIT = "1"

Write-Host ""
Write-Host "KiwiAvatarSystem v44.55.23 Production Output Authority"
Write-Host "Performance authority: 0"
Write-Host "Keep stable face tracking through COMPLETE."
Write-Host ""

$process = Start-Process -FilePath $ExePath -PassThru

$PersistentDir = Join-Path $env:USERPROFILE `
    "AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem"
$PlayerLog = Join-Path $PersistentDir "Player.log"

$offset = 0L

if (Test-Path -LiteralPath $PlayerLog -PathType Leaf) {
    $offset = (Get-Item -LiteralPath $PlayerLog).Length
}

while (-not $process.HasExited) {
    Start-Sleep -Milliseconds 500
    $process.Refresh()

    if (-not (Test-Path -LiteralPath $PlayerLog -PathType Leaf)) {
        continue
    }

    try {
        $stream = New-Object System.IO.FileStream(
            $PlayerLog,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite
        )

        try {
            if ($offset -gt $stream.Length) {
                $offset = 0L
            }

            [void]$stream.Seek($offset, [System.IO.SeekOrigin]::Begin)
            $reader = New-Object System.IO.StreamReader($stream)

            try {
                while (-not $reader.EndOfStream) {
                    $line = $reader.ReadLine()

                    if (
                        $line -match "\[Kiwi v44\.55\.20 CommonTensor\].*(GATE_MATCH|READY_FOR_WARMUP|MEASURE_START|COMPLETE|OBSERVER_FAULT)" -or
                        $line -match "\[Kiwi v44\.55\.23 OutputAuthority\].*(DEPENDENCY_BOUND|PAIR_ATTACHED|SNAPSHOT_SUBMITTED|SNAPSHOT_READBACK|V20_OUTPUTS_CAPTURED|SAMPLE|COMPLETE|OBSERVER_FAULT)"
                    ) {
                        Write-Host $line
                    }
                }

                $offset = $stream.Position
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
    }
}

Write-Host ""
Write-Host "Player exited. Collect the v44.55.23 TXT/CSV and same-run v44.55.20 CSV."
