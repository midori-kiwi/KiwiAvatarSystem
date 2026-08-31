param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot "Assets"))) {
    throw "Unity project not found: $ProjectRoot"
}

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$outRoot = Join-Path $ProjectRoot ("KiwiDiagnostics\v44_SourceAudit_" + $stamp)
$filesRoot = Join-Path $outRoot "Files"
$manifestPath = Join-Path $outRoot "MANIFEST.txt"
$zipPath = $outRoot + ".zip"

New-Item -ItemType Directory -Path $filesRoot -Force | Out-Null

$explicit = @(
    "Assets\KiwiAvatarSystem\Runtime\Camera\WindowsNativeWebCamSource.cs",
    "Assets\KiwiAvatarSystem\Runtime\Camera\KiwiNativeCameraInterop.cs",
    "Assets\KiwiAvatarSystem\Runtime\Camera\KiwiNativeCameraTelemetry.cs",
    "Assets\Script\FaceLandmarkerRunner.cs",
    "Assets\Script\KiwiInferenceFaceTracker.cs",
    "Assets\KiwiAvatarSystem\Runtime\Optimization\KiwiOrtDmlZeroCopyRuntime.cs",
    "Assets\KiwiAvatarSystem\Runtime\Optimization\KiwiInferenceRecoveryBootstrap.cs",
    "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiFrameComparisonOverlay.cs",
    "Assets\KiwiAvatarSystem\Editor\KiwiStandaloneBuildDiagnostic.cs"
)

$patterns = @(
    "KiwiNativeCameraPlugin.cpp",
    "KiwiNativeCameraPlugin.h",
    "KiwiOrtDmlZeroCopyBridge.cpp",
    "KiwiOrtDmlZeroCopyBridge.h",
    "*OrtDml*Bridge*.cpp",
    "*OrtDml*Bridge*.h",
    "KiwiOrtDmlTensorize.compute",
    "KiwiInferenceFaceCrop.shader"
)

$collected = New-Object System.Collections.Generic.List[string]

function Add-SourceFile {
    param([string]$FullPath)

    if ([string]::IsNullOrWhiteSpace($FullPath)) { return }
    if (-not (Test-Path -LiteralPath $FullPath -PathType Leaf)) { return }

    $resolved = (Resolve-Path -LiteralPath $FullPath).Path
    if ($collected.Contains($resolved)) { return }

    $relative = $null
    if ($resolved.StartsWith($ProjectRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative = $resolved.Substring($ProjectRoot.Length).TrimStart('\')
    } else {
        $safe = ($resolved -replace '[:\\\/]', '_')
        $relative = Join-Path "ExternalDiscovered" $safe
    }

    $dest = Join-Path $filesRoot $relative
    $destDir = Split-Path -Parent $dest
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    Copy-Item -LiteralPath $resolved -Destination $dest -Force
    $collected.Add($resolved)
}

foreach ($rel in $explicit) {
    Add-SourceFile (Join-Path $ProjectRoot $rel)
}

foreach ($pattern in $patterns) {
    Get-ChildItem -LiteralPath $ProjectRoot -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
        ForEach-Object { Add-SourceFile $_.FullName }
}

# Also capture current build helper / plugin importer references that mention the relevant DLL names.
Get-ChildItem -LiteralPath (Join-Path $ProjectRoot "Assets") -Recurse -File -Include *.cs,*.ps1,*.bat -ErrorAction SilentlyContinue |
    Where-Object {
        try {
            $t = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction Stop
            $t.Contains("KiwiNativeCamera") -or
            $t.Contains("KiwiOrtDmlZeroCopyBridge") -or
            $t.Contains("KIWI_ORT_DML_ZERO_COPY_SHADOW")
        } catch {
            $false
        }
    } |
    ForEach-Object { Add-SourceFile $_.FullName }

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("KiwiAvatarSystem v44 Source Audit")
$lines.Add("generated=" + (Get-Date).ToString("o"))
$lines.Add("projectRoot=" + $ProjectRoot)
$lines.Add("purpose=read-only source capture before v44 tracking/display cadence separation")
$lines.Add("projectSourceModified=0")
$lines.Add("")

$copiedFiles = Get-ChildItem -LiteralPath $filesRoot -Recurse -File | Sort-Object FullName
foreach ($f in $copiedFiles) {
    $rel = $f.FullName.Substring($filesRoot.Length).TrimStart('\')
    $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines.Add(
        ("{0}`tbytes={1}`tmtimeUtc={2}`tsha256={3}" -f
            $rel,
            $f.Length,
            $f.LastWriteTimeUtc.ToString("o"),
            $hash)
    )
}

[System.IO.File]::WriteAllLines(
    $manifestPath,
    $lines,
    (New-Object System.Text.UTF8Encoding($false))
)

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -LiteralPath $outRoot -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "Kiwi v44 Source Audit completed."
Write-Host "Files    :" $copiedFiles.Count
Write-Host "Folder   :" $outRoot
Write-Host "ZIP      :" $zipPath
Write-Host "ZIP SHA256:" ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant())
Write-Host ""
Write-Host "No project source file was modified."
