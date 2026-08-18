param(
    [Parameter(Position=0)]
    [string]$MediaPipeTgz
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ManifestPath = Join-Path $ProjectRoot "Packages\manifest.json"

function Select-TgzFile {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = "Select com.github.homuler.mediapipe-0.16.3.tgz"
    $dialog.Filter = "MediaPipe Unity package (*.tgz)|*.tgz|All files (*.*)|*.*"
    $dialog.Multiselect = $false
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        throw "MediaPipe package selection was cancelled."
    }
    return $dialog.FileName
}

if ([string]::IsNullOrWhiteSpace($MediaPipeTgz)) {
    $MediaPipeTgz = Select-TgzFile
}

$MediaPipeTgz = [System.IO.Path]::GetFullPath($MediaPipeTgz)
if (-not (Test-Path -LiteralPath $MediaPipeTgz -PathType Leaf)) {
    throw "File not found: $MediaPipeTgz"
}
if ([System.IO.Path]::GetFileName($MediaPipeTgz) -ne 'com.github.homuler.mediapipe-0.16.3.tgz') {
    throw "Expected com.github.homuler.mediapipe-0.16.3.tgz, but selected: $([System.IO.Path]::GetFileName($MediaPipeTgz))"
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "manifest.json not found: $ManifestPath"
}

# Validate the tarball contents when bsdtar/tar is available.
$tar = Get-Command tar.exe -ErrorAction SilentlyContinue
if ($tar) {
    $listing = & $tar.Source -tzf $MediaPipeTgz 2>$null
    if ($LASTEXITCODE -ne 0 -or -not ($listing -match '^package/package\.json$')) {
        throw "The selected tgz does not look like a valid Unity package tarball."
    }
    $pkgJson = & $tar.Source -xOzf $MediaPipeTgz package/package.json 2>$null | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Could not read package/package.json from tgz." }
    $pkg = $pkgJson | ConvertFrom-Json
    if ($pkg.name -ne 'com.github.homuler.mediapipe' -or $pkg.version -ne '0.16.3') {
        throw "Wrong MediaPipe package. Expected com.github.homuler.mediapipe 0.16.3."
    }
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($null -eq $manifest.dependencies) { throw "dependencies object is missing in manifest.json" }

# Unity local package paths are stored as file:<path>. Forward slashes avoid JSON/backslash issues.
$unityPath = $MediaPipeTgz.Replace('\\','/')
$packageValue = 'file:' + $unityPath
$manifest.dependencies | Add-Member -NotePropertyName 'com.github.homuler.mediapipe' -NotePropertyValue $packageValue -Force

$json = $manifest | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($ManifestPath, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))

$lockPath = Join-Path $ProjectRoot 'Packages\packages-lock.json'
if (Test-Path -LiteralPath $lockPath) { Remove-Item -LiteralPath $lockPath -Force }

Write-Host ''
Write-Host 'MediaPipe v0.16.3 was registered successfully.' -ForegroundColor Green
Write-Host ('Package: ' + $MediaPipeTgz)
Write-Host ('Manifest: ' + $ManifestPath)
Write-Host ''
Write-Host 'You can now add/open this KiwiAvatarSystem folder in Unity Hub.' -ForegroundColor Cyan
