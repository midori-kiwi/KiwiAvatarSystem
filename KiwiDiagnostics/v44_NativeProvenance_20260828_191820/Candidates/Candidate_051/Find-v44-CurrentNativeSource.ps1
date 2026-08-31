param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",
    [string]$DownloadsRoot = "D:\Users\main\Downloads"
)

$ErrorActionPreference = "Stop"

$required = @(
    "KiwiNativeCamera_GetCaptureTransportId",
    "KiwiNativeCamera_GetLatestCpuNv12CopyMicroseconds",
    "KiwiNativeCamera_GetCpuLatestReplacementCount",
    "KiwiNativeCamera_GetLatestGpuUploadSubmitMicroseconds"
)

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$outRoot = Join-Path $ProjectRoot ("KiwiDiagnostics\v44_NativeSource_" + $stamp)
$filesRoot = Join-Path $outRoot "Files"
$report = Join-Path $outRoot "REPORT.txt"
$zipOut = $outRoot + ".zip"

New-Item -ItemType Directory -Path $filesRoot -Force | Out-Null

$roots = New-Object System.Collections.Generic.List[string]
foreach ($r in @(
    $DownloadsRoot,
    (Join-Path $ProjectRoot "KiwiBackups"),
    (Join-Path $ProjectRoot "Installer"),
    (Join-Path $ProjectRoot "Native")
)) {
    if (Test-Path -LiteralPath $r) {
        $roots.Add((Resolve-Path -LiteralPath $r).Path)
    }
}

$found = New-Object System.Collections.Generic.List[object]

function Test-CurrentSourceText {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $false }
    foreach ($token in $required) {
        if (-not $Text.Contains($token)) { return $false }
    }
    return $true
}

function Save-Candidate {
    param(
        [string]$SourceLabel,
        [string]$SuggestedName,
        [string]$Text
    )

    $index = $found.Count + 1
    $safe = ($SuggestedName -replace '[^A-Za-z0-9._-]', '_')
    if ([string]::IsNullOrWhiteSpace($safe)) {
        $safe = "KiwiNativeCameraPlugin.cpp"
    }

    $destDir = Join-Path $filesRoot ("Candidate_" + $index.ToString("00"))
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    $dest = Join-Path $destDir $safe
    [System.IO.File]::WriteAllText(
        $dest,
        $Text,
        (New-Object System.Text.UTF8Encoding($false))
    )

    $hash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()

    $found.Add([pscustomobject]@{
        Index = $index
        Source = $SourceLabel
        File = $dest
        Sha256 = $hash
        Bytes = (Get-Item -LiteralPath $dest).Length
    })
}

# 1) Search loose C++ files.
foreach ($r in $roots) {
    Get-ChildItem -LiteralPath $r -Recurse -File -Filter "KiwiNativeCameraPlugin.cpp" -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                $text = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction Stop
                if (Test-CurrentSourceText $text) {
                    Save-Candidate $_.FullName $_.Name $text
                }
            } catch {
            }
        }
}

# 2) Search ZIP packages without modifying/extracting them in place.
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($r in $roots) {
    Get-ChildItem -LiteralPath $r -Recurse -File -Filter "*.zip" -ErrorAction SilentlyContinue |
        ForEach-Object {
            $zipPath = $_.FullName
            try {
                $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
                try {
                    foreach ($entry in $archive.Entries) {
                        if (-not $entry.FullName.EndsWith("KiwiNativeCameraPlugin.cpp", [System.StringComparison]::OrdinalIgnoreCase)) {
                            continue
                        }

                        $reader = New-Object System.IO.StreamReader($entry.Open())
                        try {
                            $text = $reader.ReadToEnd()
                        } finally {
                            $reader.Dispose()
                        }

                        if (Test-CurrentSourceText $text) {
                            Save-Candidate ($zipPath + " :: " + $entry.FullName) "KiwiNativeCameraPlugin.cpp" $text

                            # Also capture adjacent build/apply/validate scripts from the same archive.
                            $candidateDir = Join-Path $filesRoot ("Candidate_" + $found.Count.ToString("00"))
                            foreach ($s in $archive.Entries) {
                                $n = [System.IO.Path]::GetFileName($s.FullName)
                                if (
                                    $n -match '(?i)(Build|Apply|Validate).*Kiwi.*Camera.*\.ps1$' -or
                                    $n -match '(?i)Kiwi.*Camera.*\.h$'
                                ) {
                                    try {
                                        $dest = Join-Path $candidateDir $n
                                        $stream = $s.Open()
                                        try {
                                            $fs = [System.IO.File]::Create($dest)
                                            try { $stream.CopyTo($fs) } finally { $fs.Dispose() }
                                        } finally { $stream.Dispose() }
                                    } catch {
                                    }
                                }
                            }
                        }
                    }
                } finally {
                    $archive.Dispose()
                }
            } catch {
            }
        }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("KiwiAvatarSystem v44 Native Source Locator")
$lines.Add("generated=" + (Get-Date).ToString("o"))
$lines.Add("projectRoot=" + $ProjectRoot)
$lines.Add("downloadsRoot=" + $DownloadsRoot)
$lines.Add("projectSourceModified=0")
$lines.Add("requiredSymbols=" + ($required -join ","))
$lines.Add("candidateCount=" + $found.Count)
$lines.Add("")

foreach ($c in $found) {
    $lines.Add(
        ("candidate={0}`nsource={1}`nfile={2}`nbytes={3}`nsha256={4}`n" -f
            $c.Index, $c.Source, $c.File, $c.Bytes, $c.Sha256)
    )
}

if ($found.Count -eq 0) {
    $lines.Add("NO_CURRENT_NATIVE_SOURCE_FOUND")
    $lines.Add("The installed DLL is newer than the source available inside the Unity project.")
}

[System.IO.File]::WriteAllLines(
    $report,
    $lines,
    (New-Object System.Text.UTF8Encoding($false))
)

if (Test-Path -LiteralPath $zipOut) {
    Remove-Item -LiteralPath $zipOut -Force
}
Compress-Archive -LiteralPath $outRoot -DestinationPath $zipOut -CompressionLevel Optimal

Write-Host ""
Write-Host "Kiwi v44 Native Source Locator complete."
Write-Host "Candidates :" $found.Count
Write-Host "Report     :" $report
Write-Host "ZIP        :" $zipOut
Write-Host "ZIP SHA256 :" ((Get-FileHash -LiteralPath $zipOut -Algorithm SHA256).Hash.ToLowerInvariant())
Write-Host ""
Write-Host "No project file was modified."
