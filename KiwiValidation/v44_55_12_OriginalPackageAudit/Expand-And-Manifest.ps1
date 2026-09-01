param(
    [Parameter(Mandatory = $false)]
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",

    [Parameter(Mandatory = $true)]
    [string]$ZipPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$ModulePath = Join-Path $ProjectRoot "Tools\KiwiPowerShell\KiwiPsCompat.psm1"
Import-Module $ModulePath -Force
$ProjectRoot = Resolve-KiwiProjectRoot -ProjectRoot $ProjectRoot
$ZipPath = Assert-KiwiFile -Path $ZipPath -Label "Original package ZIP"

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
        -Label "Protected file before extraction")
}

$ValidationRoot = Join-Path $ProjectRoot "KiwiValidation\v44_55_12_OriginalPackageAudit"
$OutputDirectory = New-KiwiTimestampedOutputDirectory `
    -Root $ValidationRoot `
    -Purpose "OriginalPackage" `
    -AllowedRoots @((Join-Path $ProjectRoot "KiwiValidation"))
$ExtractRoot = Join-Path $OutputDirectory "Extracted"
[void](Assert-KiwiPathWithinRoot -Path $ExtractRoot -AllowedRoots @($OutputDirectory) -Label "Extraction root")
[System.IO.Directory]::CreateDirectory($ExtractRoot) | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
$Archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
$EntryRows = [System.Collections.Generic.List[object]]::new()
$SeenPaths = @{}
$TopLevelNames = @(
    $Archive.Entries |
    ForEach-Object { ($_.FullName -split '/')[0] } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -Unique
)
if ($TopLevelNames.Count -ne 1) {
    throw "ZIP must contain exactly one common top-level directory. Count=$($TopLevelNames.Count)"
}
$PackageRootPrefix = $TopLevelNames[0] + "/"

try {
    foreach ($Entry in $Archive.Entries) {
        if ($Entry.FullName -eq $PackageRootPrefix) {
            $EntryRows.Add([pscustomobject]@{
                Entry = $Entry.FullName
                Type = "Directory"
                Length = $Entry.Length
                CompressedLength = $Entry.CompressedLength
                LastWriteTime = $Entry.LastWriteTime.ToString("o")
            })
            continue
        }
        if (-not $Entry.FullName.StartsWith($PackageRootPrefix, [System.StringComparison]::Ordinal)) {
            throw "ZIP entry is outside the common top-level directory: $($Entry.FullName)"
        }
        $RelativePath = $Entry.FullName.Substring($PackageRootPrefix.Length).Replace(
            '/',
            [System.IO.Path]::DirectorySeparatorChar)
        if ([string]::IsNullOrWhiteSpace($RelativePath)) {
            throw "ZIP contains an empty entry name."
        }

        $DestinationPath = Join-Path $ExtractRoot $RelativePath
        $DestinationPath = Assert-KiwiPathWithinRoot `
            -Path $DestinationPath `
            -AllowedRoots @($ExtractRoot) `
            -Label "ZIP entry target"
        $PathKey = $DestinationPath.ToLowerInvariant()
        if ($SeenPaths.ContainsKey($PathKey)) {
            throw "ZIP contains a duplicate canonical path: $($Entry.FullName)"
        }
        $SeenPaths[$PathKey] = $true

        $IsDirectory = $Entry.FullName.EndsWith("/", [System.StringComparison]::Ordinal)
        $EntryRows.Add([pscustomobject]@{
            Entry = $Entry.FullName
            Type = if ($IsDirectory) { "Directory" } else { "File" }
            Length = $Entry.Length
            CompressedLength = $Entry.CompressedLength
            LastWriteTime = $Entry.LastWriteTime.ToString("o")
        })

        if ($IsDirectory) {
            [System.IO.Directory]::CreateDirectory($DestinationPath) | Out-Null
            continue
        }

        $DestinationDirectory = Split-Path -Parent $DestinationPath
        if (-not (Test-Path -LiteralPath $DestinationDirectory -PathType Container)) {
            [System.IO.Directory]::CreateDirectory($DestinationDirectory) | Out-Null
        }

        $InputStream = $Entry.Open()
        $OutputStream = [System.IO.File]::Open(
            $DestinationPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $InputStream.CopyTo($OutputStream)
        }
        finally {
            $OutputStream.Dispose()
            $InputStream.Dispose()
        }

        try {
            [System.IO.File]::SetLastWriteTimeUtc($DestinationPath, $Entry.LastWriteTime.UtcDateTime)
        }
        catch {
        }
    }
}
finally {
    $Archive.Dispose()
}

$ManifestRows = [System.Collections.Generic.List[object]]::new()
foreach ($File in @(Get-ChildItem -LiteralPath $ExtractRoot -File -Recurse | Sort-Object FullName)) {
    $RelativePath = $File.FullName.Substring($ExtractRoot.Length).TrimStart('\')
    $ManifestRows.Add([pscustomobject]@{
        RelativePath = $RelativePath
        Length = $File.Length
        SHA256 = Get-KiwiSha256 -Path $File.FullName
        LastWriteTimeUtc = $File.LastWriteTimeUtc.ToString("o")
    })
}

$EntryCsv = ($EntryRows.ToArray() | ConvertTo-Csv -NoTypeInformation) -join [Environment]::NewLine
$ManifestCsv = ($ManifestRows.ToArray() | ConvertTo-Csv -NoTypeInformation) -join [Environment]::NewLine
[void](Write-KiwiTextFile `
    -Path (Join-Path $OutputDirectory "zip-entries.csv") `
    -Text $EntryCsv `
    -AllowedRoots @($OutputDirectory))
[void](Write-KiwiTextFile `
    -Path (Join-Path $OutputDirectory "file-manifest.csv") `
    -Text $ManifestCsv `
    -AllowedRoots @($OutputDirectory))

$SummaryLines = @(
    "status=PASS",
    "zip=$ZipPath",
    "zipSha256=$(Get-KiwiSha256 -Path $ZipPath)",
    "zipLength=$((Get-Item -LiteralPath $ZipPath).Length)",
    "entryCount=$($EntryRows.Count)",
    "fileCount=$($ManifestRows.Count)",
    "extractRoot=$ExtractRoot",
    "productionChanged=0",
    "nativeChanged=0",
    "rebuildLaunched=0"
)
[void](Write-KiwiReport `
    -Path (Join-Path $OutputDirectory "extraction-summary.txt") `
    -Lines $SummaryLines `
    -AllowedRoots @($OutputDirectory))

foreach ($File in @(Get-ChildItem -LiteralPath $ExtractRoot -File -Recurse)) {
    [System.IO.File]::SetAttributes(
        $File.FullName,
        ([System.IO.File]::GetAttributes($File.FullName) -bor [System.IO.FileAttributes]::ReadOnly))
}

foreach ($path in @($ExpectedCriticalHashes.Keys | Sort-Object)) {
    [void](Assert-KiwiFileSha256 `
        -Path $path `
        -ExpectedSha256 ([string]$ExpectedCriticalHashes[$path]) `
        -Label "Protected file after extraction")
}

Write-Host "KIWI ORIGINAL PACKAGE EXTRACTION PASS"
Write-Host "outputDirectory=$OutputDirectory"
Write-Host "extractRoot=$ExtractRoot"
