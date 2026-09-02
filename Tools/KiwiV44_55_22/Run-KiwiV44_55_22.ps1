param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)

$ExePath = Join-Path $ProjectRoot `
    "Builds\v44_55_22ReferencePixelTransactionIntegrity\KiwiAvatarSystem_v44_55_22_REF_TRANSACTION.exe"

$EvidenceDir = Join-Path $ProjectRoot "KiwiValidation"
$PersistentDir = Join-Path $env:USERPROFILE `
    "AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem"
$BottleneckDir = Join-Path $PersistentDir "KiwiFrameBottleneck"
$PlayerLog = Join-Path $PersistentDir "Player.log"

if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "v44.55.22 Development Player not found: $ExePath"
}

New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null

$existing = Get-Process |
    Where-Object {
        $_.Path -eq $ExePath
    } -ErrorAction SilentlyContinue

if ($null -ne $existing) {
    throw "v44.55.22 Player is already running. Close it before starting another audit."
}

$env:KIWI_V44_55_20_COMMON_TENSOR_AUDIT = "1"
$env:KIWI_V44_55_20_COMMON_TENSOR_SECONDS = "120"
$env:KIWI_V44_55_20_COMMON_TENSOR_HZ = "1"
$env:KIWI_V44_55_20_STABLE_SECONDS = "8"
Remove-Item Env:KIWI_V44_55_21_CROP_SAMPLER_AUDIT -ErrorAction SilentlyContinue
$env:KIWI_V44_55_22_REFERENCE_TRANSACTION_AUDIT = "1"

Write-Host ""
Write-Host "KiwiAvatarSystem v44.55.22 correctness audit"
Write-Host "EXE: $ExePath"
Write-Host "Performance authority: 0"
Write-Host ""
Write-Host "Keep your face near the camera center until MEASURE_START,"
Write-Host "then maintain normal stable tracking until COMPLETE."
Write-Host ""

$startTime = Get-Date
$logOffset = 0L

if (Test-Path -LiteralPath $PlayerLog -PathType Leaf) {
    $logOffset = (Get-Item -LiteralPath $PlayerLog).Length
}

$process = Start-Process `
    -FilePath $ExePath `
    -PassThru

$completeDetected = $false
$lastReminder = Get-Date

while (-not $process.HasExited) {
    Start-Sleep -Milliseconds 500
    $process.Refresh()

    if (Test-Path -LiteralPath $PlayerLog -PathType Leaf) {
        try {
            $stream = New-Object System.IO.FileStream(
                $PlayerLog,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite
            )

            try {
                if ($logOffset -gt $stream.Length) {
                    $logOffset = 0L
                }

                [void]$stream.Seek($logOffset, [System.IO.SeekOrigin]::Begin)

                $reader = New-Object System.IO.StreamReader($stream)

                try {
                    while (-not $reader.EndOfStream) {
                        $line = $reader.ReadLine()

                        if (
                            $line -match "\[Kiwi v44\.55\.20 CommonTensor\].*(GATE_WAIT|GATE_MATCH|READY_FOR_WARMUP|MEASURE_START|COMPLETE|OBSERVER_FAULT)" -or
                            $line -match "\[Kiwi v44\.55\.22 RefTransaction\].*(DEPENDENCY_BOUND|PAIR_ATTACHED|OUTPUT_READY|SNAPSHOT_SUBMITTED|SNAPSHOT_READBACK|SAMPLE|COMPLETE|OBSERVER_FAULT|TRANSACTION_CONTRACT_MISMATCH)"
                        ) {
                            Write-Host $line
                        }

                        if (
                            $line -match "\[Kiwi v44\.55\.22 RefTransaction\] COMPLETE"
                        ) {
                            $completeDetected = $true
                        }
                    }

                    $logOffset = $stream.Position
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
            # Player.log can rotate or be briefly unavailable. Retry on next poll.
        }
    }

    if (
        $completeDetected -and
        ((Get-Date) - $lastReminder).TotalSeconds -ge 5
    ) {
        Write-Host ""
        Write-Host "v44.55.22 COMPLETE detected."
        Write-Host "Close the Player normally to finalize evidence collection."
        Write-Host ""
        $lastReminder = Get-Date
    }
}

$endTime = Get-Date

Write-Host ""
Write-Host "Player exited with code $($process.ExitCode)."
Write-Host "Collecting runtime evidence..."

$patterns = @(
    "KiwiReferencePixelTransactionIntegrity_v44_55_22_*.txt",
    "KiwiReferencePixelTransactionIntegrity_v44_55_22_*.csv",
    "KiwiCommonTensorBackendStageIsolation_v44_55_20_*.txt",
    "KiwiCommonTensorBackendStageIsolation_v44_55_20_*.csv"
)

$selected = @()

foreach ($pattern in $patterns) {
    if (Test-Path -LiteralPath $BottleneckDir -PathType Container) {
        $candidate = Get-ChildItem `
            -LiteralPath $BottleneckDir `
            -Filter $pattern `
            -File `
            -ErrorAction SilentlyContinue |
            Where-Object {
                $_.LastWriteTime -ge $startTime.AddMinutes(-1)
            } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if ($null -ne $candidate) {
            $selected += $candidate
        }
    }
}

if (Test-Path -LiteralPath $PlayerLog -PathType Leaf) {
    $selected += Get-Item -LiteralPath $PlayerLog
}

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$staging = Join-Path $EvidenceDir `
    ("RuntimeEvidence_v44_55_22_" + $stamp)
$zipPath = $staging + ".zip"

if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $staging | Out-Null

foreach ($file in $selected) {
    Copy-Item `
        -LiteralPath $file.FullName `
        -Destination (Join-Path $staging $file.Name) `
        -Force
}

$summaryPath = Join-Path $staging "RUN_SUMMARY.txt"
$summary = @(
    "KiwiAvatarSystem v44.55.22 Runtime Evidence",
    "start=$($startTime.ToString('O'))",
    "end=$($endTime.ToString('O'))",
    "playerExitCode=$($process.ExitCode)",
    "completeDetected=$([int]$completeDetected)",
    "performanceAuthority=0",
    "v44.55.20=enabled",
    "v44.55.21=disabled",
    "v44.55.22=enabled",
    "files:"
)

foreach ($file in $selected) {
    $summary += "  $($file.FullName)"
}

[System.IO.File]::WriteAllLines(
    $summaryPath,
    $summary,
    (New-Object System.Text.UTF8Encoding($false))
)

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $staging,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant()

Write-Host ""
Write-Host "Evidence package:"
Write-Host "  $zipPath"
Write-Host "SHA256:"
Write-Host "  $zipHash"
Write-Host ""
Write-Host "Upload this ZIP after the run."
