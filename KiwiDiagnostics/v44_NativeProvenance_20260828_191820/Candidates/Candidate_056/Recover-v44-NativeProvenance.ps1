param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",
    [string]$DownloadsRoot = "D:\Users\main\Downloads"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$outRoot = Join-Path $ProjectRoot ("KiwiDiagnostics\v44_NativeProvenance_" + $stamp)
$filesRoot = Join-Path $outRoot "Candidates"
$reportPath = Join-Path $outRoot "REPORT.txt"
$historyPath = Join-Path $outRoot "POWERSHELL_HISTORY_HINTS.txt"
$recentPath = Join-Path $outRoot "RECENT_SHORTCUT_HINTS.txt"
$archiveHintPath = Join-Path $outRoot "ARCHIVE_HINTS.txt"
$zipOut = $outRoot + ".zip"

New-Item -ItemType Directory -Path $filesRoot -Force | Out-Null

$requiredSymbols = @(
    "KiwiNativeCamera_GetCaptureTransportId",
    "KiwiNativeCamera_GetLatestCpuNv12CopyMicroseconds",
    "KiwiNativeCamera_GetCpuLatestReplacementCount",
    "KiwiNativeCamera_GetLatestGpuUploadSubmitMicroseconds"
)

$secondaryMarkers = @(
    "SystemMemoryNV12",
    "KIWI_CAMERA_CAPTURE_TRANSPORT",
    "CpuLatestReplacement",
    "GpuUploadSubmit",
    "MF_SOURCE_READER_D3D_MANAGER",
    "KiwiNativeCamera"
)

$textExtensions = @(
    ".cpp", ".cxx", ".cc", ".h", ".hpp", ".inl",
    ".ps1", ".bat", ".cmd", ".txt", ".md", ".log"
)

$archiveExtensions = @(".zip", ".7z")
$maxTextBytes = 25MB
$maxArchiveBytes = 3GB

$roots = New-Object System.Collections.Generic.List[string]

function Add-Root {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    try {
        $resolved = (Resolve-Path -LiteralPath $Path).Path
        if (-not $roots.Contains($resolved)) {
            $roots.Add($resolved)
        }
    } catch {}
}

Add-Root $ProjectRoot
Add-Root (Join-Path $ProjectRoot "KiwiBackups")
Add-Root (Join-Path $ProjectRoot "KiwiDiagnostics")
Add-Root $DownloadsRoot

$userProfiles = @(
    $env:USERPROFILE,
    "D:\Users\main",
    "C:\Users\main"
)

foreach ($u in $userProfiles) {
    if ([string]::IsNullOrWhiteSpace($u)) { continue }
    Add-Root (Join-Path $u "Downloads")
    Add-Root (Join-Path $u "Desktop")
    Add-Root (Join-Path $u "Documents")
}

Add-Root $env:TEMP
Add-Root $env:TMP
Add-Root (Join-Path $env:LOCALAPPDATA "Temp")
Add-Root "D:\Temp"
Add-Root "C:\Temp"

# Add likely top-level D: work folders without scanning the entire drive.
if (Test-Path -LiteralPath "D:\") {
    Get-ChildItem -LiteralPath "D:\" -Directory -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -match '(?i)kiwi|temp|work|build|source|native|phase|unity'
        } |
        ForEach-Object { Add-Root $_.FullName }
}

$seenFiles = @{}
$candidates = New-Object System.Collections.Generic.List[object]
$archiveHints = New-Object System.Collections.Generic.List[string]

function Get-Score {
    param([string]$Text)

    $score = 0
    $exact = 0
    foreach ($s in $requiredSymbols) {
        if ($Text.Contains($s)) {
            $score += 100
            $exact++
        }
    }

    foreach ($m in $secondaryMarkers) {
        if ($Text.Contains($m)) {
            $score += 10
        }
    }

    if ($Text.Contains("captureTransport=B") -or
        $Text.Contains("B:SystemMemoryNV12")) {
        $score += 40
    }

    if ($Text.Contains("KiwiNativeCameraPlugin")) {
        $score += 20
    }

    return [pscustomobject]@{
        Score = $score
        Exact = $exact
    }
}

function Save-CandidateText {
    param(
        [string]$Source,
        [string]$SuggestedName,
        [string]$Text,
        [int]$Score,
        [int]$Exact
    )

    if ($Score -lt 30) { return }

    $key = $Source
    if ($seenFiles.ContainsKey($key)) { return }
    $seenFiles[$key] = $true

    $idx = $candidates.Count + 1
    $dir = Join-Path $filesRoot ("Candidate_" + $idx.ToString("000"))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null

    $safe = $SuggestedName -replace '[^A-Za-z0-9._-]', '_'
    if ([string]::IsNullOrWhiteSpace($safe)) {
        $safe = "candidate.txt"
    }

    $dest = Join-Path $dir $safe
    [IO.File]::WriteAllText(
        $dest,
        $Text,
        (New-Object Text.UTF8Encoding($false))
    )

    $hash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()

    $meta = @(
        "source=" + $Source,
        "score=" + $Score,
        "exactCurrentPathBSymbols=" + $Exact,
        "sha256=" + $hash
    )
    [IO.File]::WriteAllLines(
        (Join-Path $dir "META.txt"),
        $meta,
        (New-Object Text.UTF8Encoding($false))
    )

    $candidates.Add([pscustomobject]@{
        Index = $idx
        Source = $Source
        Score = $Score
        Exact = $Exact
        File = $dest
        Sha256 = $hash
    })
}

function Inspect-TextFile {
    param([IO.FileInfo]$File)

    try {
        if ($File.Length -gt $maxTextBytes) { return }
        $ext = $File.Extension.ToLowerInvariant()
        if (-not ($textExtensions -contains $ext)) { return }

        $text = [IO.File]::ReadAllText($File.FullName)
        $sc = Get-Score $text

        if ($sc.Score -ge 30) {
            Save-CandidateText $File.FullName $File.Name $text $sc.Score $sc.Exact
        }
    } catch {}
}

# Search plain text/source files.
foreach ($r in $roots) {
    try {
        Get-ChildItem -LiteralPath $r -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $textExtensions -contains $_.Extension.ToLowerInvariant() } |
            ForEach-Object { Inspect-TextFile $_ }
    } catch {}
}

# PowerShell / PSReadLine history. This often contains the exact generated source/package path.
$historyFiles = New-Object System.Collections.Generic.List[string]

foreach ($u in $userProfiles) {
    if ([string]::IsNullOrWhiteSpace($u)) { continue }
    foreach ($h in @(
        (Join-Path $u "AppData\Roaming\Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt"),
        (Join-Path $u "AppData\Roaming\Microsoft\PowerShell\PSReadLine\ConsoleHost_history.txt")
    )) {
        if (Test-Path -LiteralPath $h -PathType Leaf) {
            $historyFiles.Add($h)
        }
    }
}

$historyLines = New-Object System.Collections.Generic.List[string]
foreach ($h in $historyFiles) {
    try {
        $lineNo = 0
        foreach ($line in Get-Content -LiteralPath $h -ErrorAction Stop) {
            $lineNo++
            if ($line -match '(?i)KiwiNativeCamera|Phase16[_\.-]?20[_\.-]?(7|8|9)|v1[89]|cl\.exe|Build.*Native|Native.*Build|SystemMemoryNV12|CaptureTransport') {
                $historyLines.Add(("{0}:{1}: {2}" -f $h, $lineNo, $line))
            }
        }
    } catch {}
}

[IO.File]::WriteAllLines(
    $historyPath,
    $historyLines,
    (New-Object Text.UTF8Encoding($false))
)

# Resolve Windows "Recent" shortcuts for deleted/moved package hints.
$recentLines = New-Object System.Collections.Generic.List[string]
try {
    $recentDir = Join-Path $env:APPDATA "Microsoft\Windows\Recent"
    if (Test-Path -LiteralPath $recentDir) {
        $shell = New-Object -ComObject WScript.Shell
        Get-ChildItem -LiteralPath $recentDir -Filter "*.lnk" -File -ErrorAction SilentlyContinue |
            ForEach-Object {
                try {
                    $s = $shell.CreateShortcut($_.FullName)
                    $target = [string]$s.TargetPath
                    $args = [string]$s.Arguments
                    if (
                        $target -match '(?i)kiwi|native|phase|source|cpp|zip' -or
                        $args -match '(?i)kiwi|native|phase|source|cpp|zip' -or
                        $_.Name -match '(?i)kiwi|native|phase'
                    ) {
                        $recentLines.Add(
                            ("lnk={0}`ntarget={1}`nargs={2}`n" -f
                                $_.FullName, $target, $args)
                        )
                    }
                } catch {}
            }
    }
} catch {}

[IO.File]::WriteAllLines(
    $recentPath,
    $recentLines,
    (New-Object Text.UTF8Encoding($false))
)

# Inspect ZIP archives by content, not only by exact filename.
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Inspect-ZipArchive {
    param([string]$ZipPath)

    try {
        $fi = Get-Item -LiteralPath $ZipPath -ErrorAction Stop
        if ($fi.Length -gt $maxArchiveBytes) { return }

        $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
        try {
            foreach ($entry in $archive.Entries) {
                if ($entry.Length -le 0 -or $entry.Length -gt $maxTextBytes) { continue }

                $ext = [IO.Path]::GetExtension($entry.FullName).ToLowerInvariant()
                if (-not ($textExtensions -contains $ext)) { continue }

                try {
                    $stream = $entry.Open()
                    $reader = New-Object IO.StreamReader($stream)
                    try {
                        $text = $reader.ReadToEnd()
                    } finally {
                        $reader.Dispose()
                        $stream.Dispose()
                    }

                    $sc = Get-Score $text
                    if ($sc.Score -ge 30) {
                        $name = [IO.Path]::GetFileName($entry.FullName)
                        Save-CandidateText ($ZipPath + " :: " + $entry.FullName) $name $text $sc.Score $sc.Exact
                    }
                } catch {}
            }
        } finally {
            $archive.Dispose()
        }
    } catch {}
}

foreach ($r in $roots) {
    try {
        Get-ChildItem -LiteralPath $r -Recurse -File -Filter "*.zip" -ErrorAction SilentlyContinue |
            ForEach-Object { Inspect-ZipArchive $_.FullName }
    } catch {}
}

# 7-Zip archive provenance hints. We deliberately do not extract archives in-place.
$sevenZip = $null
foreach ($p in @(
    "$env:ProgramFiles\7-Zip\7z.exe",
    "${env:ProgramFiles(x86)}\7-Zip\7z.exe",
    "C:\Program Files\7-Zip\7z.exe",
    "D:\Program Files\7-Zip\7z.exe"
)) {
    if ($p -and (Test-Path -LiteralPath $p -PathType Leaf)) {
        $sevenZip = $p
        break
    }
}

foreach ($r in $roots) {
    try {
        Get-ChildItem -LiteralPath $r -Recurse -File -Filter "*.7z" -ErrorAction SilentlyContinue |
            ForEach-Object {
                if ($_.Length -gt $maxArchiveBytes) { return }
                if ($sevenZip) {
                    try {
                        $listing = & $sevenZip l -slt -- $_.FullName 2>$null
                        $joined = $listing -join "`n"
                        if ($joined -match '(?i)KiwiNativeCamera|Phase16|v19|NativeCameraPlugin') {
                            $archiveHints.Add("7z=" + $_.FullName)
                        }
                    } catch {}
                } else {
                    if ($_.Name -match '(?i)kiwi|native|phase|v1[89]') {
                        $archiveHints.Add("7z-uninspected=" + $_.FullName)
                    }
                }
            }
    } catch {}
}

[IO.File]::WriteAllLines(
    $archiveHintPath,
    $archiveHints,
    (New-Object Text.UTF8Encoding($false))
)

# Sort strongest candidate first in report.
$sorted = @($candidates | Sort-Object @{Expression="Exact";Descending=$true}, @{Expression="Score";Descending=$true})

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("KiwiAvatarSystem v44 Native Provenance Recovery")
$lines.Add("generated=" + (Get-Date).ToString("o"))
$lines.Add("projectRoot=" + $ProjectRoot)
$lines.Add("projectSourceModified=0")
$lines.Add("searchRootCount=" + $roots.Count)
$lines.Add("candidateCount=" + $sorted.Count)
$lines.Add("historyHintCount=" + $historyLines.Count)
$lines.Add("recentShortcutHintCount=" + $recentLines.Count)
$lines.Add("archiveHintCount=" + $archiveHints.Count)
$lines.Add("")
$lines.Add("A candidate with exactCurrentPathBSymbols=4 is the preferred current Path B source.")
$lines.Add("Do NOT use an old candidate merely because its filename is KiwiNativeCameraPlugin.cpp.")
$lines.Add("")

foreach ($c in $sorted) {
    $lines.Add(
        ("candidate={0}`nexactCurrentPathBSymbols={1}`nscore={2}`nsource={3}`nfile={4}`nsha256={5}`n" -f
            $c.Index, $c.Exact, $c.Score, $c.Source, $c.File, $c.Sha256)
    )
}

if ($sorted.Count -eq 0) {
    $lines.Add("NO_MATCHING_SOURCE_FOUND")
    $lines.Add("Inspect POWERSHELL_HISTORY_HINTS.txt and RECENT_SHORTCUT_HINTS.txt for the source/package provenance.")
}

[IO.File]::WriteAllLines(
    $reportPath,
    $lines,
    (New-Object Text.UTF8Encoding($false))
)

if (Test-Path -LiteralPath $zipOut) {
    Remove-Item -LiteralPath $zipOut -Force
}

Compress-Archive -LiteralPath $outRoot -DestinationPath $zipOut -CompressionLevel Optimal

Write-Host ""
Write-Host "Kiwi v44 Native Provenance Recovery complete."
Write-Host "Candidates :" $sorted.Count
Write-Host "History    :" $historyLines.Count
Write-Host "Recent LNK :" $recentLines.Count
Write-Host "ZIP        :" $zipOut
Write-Host "ZIP SHA256 :" ((Get-FileHash -LiteralPath $zipOut -Algorithm SHA256).Hash.ToLowerInvariant())
Write-Host ""
Write-Host "No project source, DLL, setting, or asset was modified."
