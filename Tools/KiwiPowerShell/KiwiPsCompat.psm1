Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$script:KiwiUtf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:KiwiUtf8Bom = New-Object System.Text.UTF8Encoding($true)

function Get-KiwiCanonicalPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$BasePath = (Get-Location).Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "Path must not be empty."
    }

    $candidate = $Path
    if (-not [System.IO.Path]::IsPathRooted($candidate)) {
        $candidate = Join-Path $BasePath $candidate
    }

    return [System.IO.Path]::GetFullPath($candidate).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Test-KiwiPathWithinRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $canonicalPath = Get-KiwiCanonicalPath -Path $Path
    $canonicalRoot = Get-KiwiCanonicalPath -Path $Root

    if ([string]::Equals(
        $canonicalPath,
        $canonicalRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $rootPrefix = $canonicalRoot + [System.IO.Path]::DirectorySeparatorChar
    return $canonicalPath.StartsWith(
        $rootPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-KiwiPathWithinRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string[]]$AllowedRoots,

        [string]$Label = "Path"
    )

    $canonicalPath = Get-KiwiCanonicalPath -Path $Path
    foreach ($root in $AllowedRoots) {
        if (Test-KiwiPathWithinRoot -Path $canonicalPath -Root $root) {
            return $canonicalPath
        }
    }

    throw "$Label is outside the allowed roots: $canonicalPath"
}

function Resolve-KiwiProjectRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectRoot
    )

    $root = Get-KiwiCanonicalPath -Path $ProjectRoot
    foreach ($requiredDirectory in @("Assets", "ProjectSettings")) {
        $requiredPath = Join-Path $root $requiredDirectory
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Container)) {
            throw "Unity project marker is missing: $requiredPath"
        }
    }

    return $root
}

function Assert-KiwiFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$Label = "File"
    )

    $canonicalPath = Get-KiwiCanonicalPath -Path $Path
    if (-not (Test-Path -LiteralPath $canonicalPath -PathType Leaf)) {
        throw "$Label is missing: $canonicalPath"
    }

    return $canonicalPath
}

function Get-KiwiSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $canonicalPath = Assert-KiwiFile -Path $Path -Label "SHA256 input"
    $stream = $null
    $algorithm = $null
    try {
        $stream = [System.IO.File]::OpenRead($canonicalPath)
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        $hashBytes = $algorithm.ComputeHash($stream)
        return [System.BitConverter]::ToString($hashBytes).Replace("-", "")
    }
    finally {
        if ($null -ne $algorithm) {
            $algorithm.Dispose()
        }
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Assert-KiwiFileSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[A-Fa-f0-9]{64}$")]
        [string]$ExpectedSha256,

        [string]$Label = "File"
    )

    $canonicalPath = Assert-KiwiFile -Path $Path -Label $Label
    $actualSha256 = Get-KiwiSha256 -Path $canonicalPath
    $expected = $ExpectedSha256.ToUpperInvariant()

    if ($actualSha256 -ne $expected) {
        throw "$Label SHA256 mismatch. Expected=$expected Actual=$actualSha256 Path=$canonicalPath"
    }

    return [pscustomobject]@{
        Label = $Label
        Path = $canonicalPath
        Sha256 = $actualSha256
        Length = (Get-Item -LiteralPath $canonicalPath).Length
    }
}

function Read-KiwiTextFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [ValidateSet("Auto", "Utf8", "Unicode", "Ascii")]
        [string]$Encoding = "Auto",

        [long]$MaximumBytes = 16777216
    )

    $canonicalPath = Assert-KiwiFile -Path $Path -Label "Text file"
    $item = Get-Item -LiteralPath $canonicalPath
    if ($item.Length -gt $MaximumBytes) {
        throw "Text file exceeds MaximumBytes=${MaximumBytes}: $canonicalPath"
    }

    switch ($Encoding) {
        "Utf8" {
            return [System.IO.File]::ReadAllText($canonicalPath, $script:KiwiUtf8NoBom)
        }
        "Unicode" {
            return [System.IO.File]::ReadAllText($canonicalPath, [System.Text.Encoding]::Unicode)
        }
        "Ascii" {
            return [System.IO.File]::ReadAllText($canonicalPath, [System.Text.Encoding]::ASCII)
        }
        default {
            return [System.IO.File]::ReadAllText($canonicalPath)
        }
    }
}

function Write-KiwiTextFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string[]]$AllowedRoots,

        [ValidateSet("Utf8NoBom", "Utf8Bom", "Unicode", "Ascii")]
        [string]$Encoding = "Utf8NoBom"
    )

    $canonicalPath = Assert-KiwiPathWithinRoot `
        -Path $Path `
        -AllowedRoots $AllowedRoots `
        -Label "Text write target"

    $directory = Split-Path -Parent $canonicalPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $fileName = [System.IO.Path]::GetFileName($canonicalPath)
    $token = [guid]::NewGuid().ToString("N")
    $temporaryPath = Join-Path $directory ("." + $fileName + "." + $token + ".tmp")
    $backupPath = Join-Path $directory ("." + $fileName + "." + $token + ".bak")

    switch ($Encoding) {
        "Utf8Bom" { $selectedEncoding = $script:KiwiUtf8Bom }
        "Unicode" { $selectedEncoding = [System.Text.Encoding]::Unicode }
        "Ascii" { $selectedEncoding = [System.Text.Encoding]::ASCII }
        default { $selectedEncoding = $script:KiwiUtf8NoBom }
    }

    try {
        [System.IO.File]::WriteAllText($temporaryPath, $Text, $selectedEncoding)

        if (Test-Path -LiteralPath $canonicalPath -PathType Leaf) {
            [System.IO.File]::Replace($temporaryPath, $canonicalPath, $backupPath, $true)
            if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                [System.IO.File]::Delete($backupPath)
            }
        }
        else {
            [System.IO.File]::Move($temporaryPath, $canonicalPath)
        }
    }
    catch {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            [System.IO.File]::Delete($temporaryPath)
        }

        if (
            -not (Test-Path -LiteralPath $canonicalPath -PathType Leaf) -and
            (Test-Path -LiteralPath $backupPath -PathType Leaf)
        ) {
            [System.IO.File]::Move($backupPath, $canonicalPath)
        }

        throw
    }

    return Get-Item -LiteralPath $canonicalPath
}

function New-KiwiTimestampedOutputDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[A-Za-z0-9._-]+$")]
        [string]$Purpose,

        [string[]]$AllowedRoots = @($Root)
    )

    $canonicalRoot = Assert-KiwiPathWithinRoot `
        -Path $Root `
        -AllowedRoots $AllowedRoots `
        -Label "Output root"

    if (-not (Test-Path -LiteralPath $canonicalRoot -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($canonicalRoot) | Out-Null
    }

    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    for ($index = 0; $index -lt 1000; $index++) {
        $suffix = if ($index -eq 0) { "" } else { "_" + $index.ToString("000") }
        $path = Join-Path $canonicalRoot ($Purpose + "_" + $stamp + $suffix)
        if (-not (Test-Path -LiteralPath $path)) {
            [System.IO.Directory]::CreateDirectory($path) | Out-Null
            return $path
        }
    }

    throw "Unable to allocate a unique timestamped output directory under $canonicalRoot"
}

function Add-KiwiUniqueString {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$List,

        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    foreach ($existing in $List) {
        if ([string]::Equals($existing, $Value, [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }

    $List.Add($Value)
}

function Find-KiwiVisualStudioTool {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("MSBuild.exe", "cl.exe", "link.exe", "dumpbin.exe", "VsDevCmd.bat")]
        [string]$ToolName
    )

    $installations = [System.Collections.Generic.List[string]]::new()
    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $vswhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
            try {
                $foundInstallations = @(
                    & $vswhere `
                        -products * `
                        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
                        -property installationPath `
                        -format value 2>$null
                )
                foreach ($installation in $foundInstallations) {
                    Add-KiwiUniqueString -List $installations -Value ([string]$installation)
                }
            }
            catch {
            }
        }
    }

    foreach ($fallback in @(
        "C:\Program Files\Microsoft Visual Studio\2022\Community",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise",
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools"
    )) {
        if (Test-Path -LiteralPath $fallback -PathType Container) {
            Add-KiwiUniqueString -List $installations -Value $fallback
        }
    }

    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($installation in $installations.ToArray()) {
        switch ($ToolName) {
            "MSBuild.exe" {
                Add-KiwiUniqueString `
                    -List $candidates `
                    -Value (Join-Path $installation "MSBuild\Current\Bin\MSBuild.exe")
            }
            "VsDevCmd.bat" {
                Add-KiwiUniqueString `
                    -List $candidates `
                    -Value (Join-Path $installation "Common7\Tools\VsDevCmd.bat")
            }
            default {
                $toolsRoot = Join-Path $installation "VC\Tools\MSVC"
                if (Test-Path -LiteralPath $toolsRoot -PathType Container) {
                    $versions = @(
                        Get-ChildItem -LiteralPath $toolsRoot -Directory |
                        Sort-Object Name -Descending
                    )
                    foreach ($version in $versions) {
                        Add-KiwiUniqueString `
                            -List $candidates `
                            -Value (Join-Path $version.FullName ("bin\Hostx64\x64\" + $ToolName))
                    }
                }
            }
        }
    }

    foreach ($candidate in $candidates.ToArray()) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Get-KiwiCanonicalPath -Path $candidate)
        }
    }

    return $null
}

function Get-KiwiWindowsSdkInfo {
    [CmdletBinding()]
    param()

    $kitsRoot = $null
    try {
        $installedRoots = Get-ItemProperty `
            -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots" `
            -ErrorAction Stop
        $kitsRoot = [string]$installedRoots.KitsRoot10
    }
    catch {
    }

    if ([string]::IsNullOrWhiteSpace($kitsRoot)) {
        $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
        if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
            $kitsRoot = Join-Path $programFilesX86 "Windows Kits\10"
        }
    }

    $versions = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($kitsRoot)) {
        $libraryRoot = Join-Path $kitsRoot "Lib"
        if (Test-Path -LiteralPath $libraryRoot -PathType Container) {
            foreach ($directory in @(Get-ChildItem -LiteralPath $libraryRoot -Directory)) {
                $umX64 = Join-Path $directory.FullName "um\x64"
                $ucrtX64 = Join-Path $directory.FullName "ucrt\x64"
                if (
                    (Test-Path -LiteralPath $umX64 -PathType Container) -and
                    (Test-Path -LiteralPath $ucrtX64 -PathType Container)
                ) {
                    $versions.Add($directory.Name)
                }
            }
        }
    }

    $selectedVersion = $null
    if ($versions.Count -gt 0) {
        $selectedVersion = @($versions.ToArray() | Sort-Object { [version]$_ } -Descending)[0]
    }

    return [pscustomobject]@{
        KitsRoot = $kitsRoot
        Version = $selectedVersion
    }
}

function Get-KiwiNativeBuildTools {
    [CmdletBinding()]
    param()

    $cl = Find-KiwiVisualStudioTool -ToolName "cl.exe"
    $link = Find-KiwiVisualStudioTool -ToolName "link.exe"
    $dumpbin = Find-KiwiVisualStudioTool -ToolName "dumpbin.exe"
    $msbuild = Find-KiwiVisualStudioTool -ToolName "MSBuild.exe"
    $vsDevCmd = Find-KiwiVisualStudioTool -ToolName "VsDevCmd.bat"
    $sdk = Get-KiwiWindowsSdkInfo

    $clVersion = $null
    if (-not [string]::IsNullOrWhiteSpace($cl)) {
        $clVersion = (Get-Item -LiteralPath $cl).VersionInfo.FileVersion
    }

    return [pscustomobject]@{
        Ready = [bool](
            -not [string]::IsNullOrWhiteSpace($cl) -and
            -not [string]::IsNullOrWhiteSpace($link) -and
            -not [string]::IsNullOrWhiteSpace($dumpbin) -and
            -not [string]::IsNullOrWhiteSpace($vsDevCmd)
        )
        PlatformToolset = "v143"
        Cl = $cl
        ClFileVersion = $clVersion
        Link = $link
        Dumpbin = $dumpbin
        MSBuild = $msbuild
        VsDevCmd = $vsDevCmd
        WindowsSdkRoot = $sdk.KitsRoot
        WindowsSdkVersion = $sdk.Version
    }
}

function ConvertTo-KiwiProcessArgumentString {
    [CmdletBinding()]
    param(
        [string[]]$ArgumentList = @()
    )

    $quoted = [System.Collections.Generic.List[string]]::new()
    foreach ($argumentValue in $ArgumentList) {
        $argument = if ($null -eq $argumentValue) { "" } else { [string]$argumentValue }
        if ($argument.Length -gt 0 -and $argument -notmatch '[\s"]') {
            $quoted.Add($argument)
            continue
        }

        $builder = New-Object System.Text.StringBuilder
        [void]$builder.Append('"')
        $backslashCount = 0

        foreach ($character in $argument.ToCharArray()) {
            if ($character -eq '\') {
                $backslashCount++
                continue
            }

            if ($character -eq '"') {
                [void]$builder.Append(('\' * ($backslashCount * 2 + 1)))
                [void]$builder.Append('"')
                $backslashCount = 0
                continue
            }

            if ($backslashCount -gt 0) {
                [void]$builder.Append(('\' * $backslashCount))
                $backslashCount = 0
            }
            [void]$builder.Append($character)
        }

        if ($backslashCount -gt 0) {
            [void]$builder.Append(('\' * ($backslashCount * 2)))
        }
        [void]$builder.Append('"')
        $quoted.Add($builder.ToString())
    }

    return ($quoted.ToArray() -join " ")
}

function Assert-KiwiHashSet {
    param(
        [hashtable]$ExpectedHashes,
        [string]$Phase
    )

    if ($null -eq $ExpectedHashes) {
        return @()
    }

    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($path in @($ExpectedHashes.Keys | Sort-Object)) {
        $results.Add(
            (Assert-KiwiFileSha256 `
                -Path ([string]$path) `
                -ExpectedSha256 ([string]$ExpectedHashes[$path]) `
                -Label ("$Phase protected file"))
        )
    }

    return $results.ToArray()
}

function Invoke-KiwiProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [string[]]$ArgumentList = @(),

        [string]$WorkingDirectory = (Get-Location).Path,

        [Parameter(Mandatory = $true)]
        [string]$LogDirectory,

        [Parameter(Mandatory = $true)]
        [string[]]$AllowedLogRoots,

        [string]$LogName = "process",

        [int]$TimeoutSeconds = 3600,

        [hashtable]$ProtectedHashes,

        [switch]$AllowNonZeroExitCode
    )

    $executablePath = Assert-KiwiFile -Path $Executable -Label "Executable"
    $workingPath = Get-KiwiCanonicalPath -Path $WorkingDirectory
    if (-not (Test-Path -LiteralPath $workingPath -PathType Container)) {
        throw "Working directory is missing: $workingPath"
    }

    $canonicalLogDirectory = Assert-KiwiPathWithinRoot `
        -Path $LogDirectory `
        -AllowedRoots $AllowedLogRoots `
        -Label "Process log directory"
    if (-not (Test-Path -LiteralPath $canonicalLogDirectory -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($canonicalLogDirectory) | Out-Null
    }

    [void](Assert-KiwiHashSet -ExpectedHashes $ProtectedHashes -Phase "Before process")

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $executablePath
    $startInfo.Arguments = ConvertTo-KiwiProcessArgumentString -ArgumentList $ArgumentList
    $startInfo.WorkingDirectory = $workingPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false

    try {
        if (-not $process.Start()) {
            throw "Process failed to start: $executablePath"
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $waitMilliseconds = [Math]::Max(1, $TimeoutSeconds) * 1000
        if (-not $process.WaitForExit($waitMilliseconds)) {
            $timedOut = $true
            try { $process.Kill() } catch { }
            $process.WaitForExit()
        }

        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
        $exitCode = if ($timedOut) { -1 } else { $process.ExitCode }
    }
    finally {
        $stopwatch.Stop()
        [void](Assert-KiwiHashSet -ExpectedHashes $ProtectedHashes -Phase "After process")
        $process.Dispose()
    }

    $safeLogName = $LogName -replace '[^A-Za-z0-9._-]', '_'
    $stdoutPath = Join-Path $canonicalLogDirectory ($safeLogName + ".stdout.log")
    $stderrPath = Join-Path $canonicalLogDirectory ($safeLogName + ".stderr.log")
    [void](Write-KiwiTextFile `
        -Path $stdoutPath `
        -Text $stdout `
        -AllowedRoots $AllowedLogRoots)
    [void](Write-KiwiTextFile `
        -Path $stderrPath `
        -Text $stderr `
        -AllowedRoots $AllowedLogRoots)

    $result = [pscustomobject]@{
        Executable = $executablePath
        Arguments = $startInfo.Arguments
        WorkingDirectory = $workingPath
        ExitCode = $exitCode
        TimedOut = $timedOut
        DurationMilliseconds = $stopwatch.ElapsedMilliseconds
        StdoutPath = $stdoutPath
        StderrPath = $stderrPath
        Stdout = $stdout
        Stderr = $stderr
    }

    if ($timedOut) {
        throw "Process timed out after $TimeoutSeconds seconds. Executable=$executablePath Stdout=$stdoutPath Stderr=$stderrPath"
    }
    if ($exitCode -ne 0 -and -not $AllowNonZeroExitCode) {
        throw "Process failed. ExitCode=$exitCode Executable=$executablePath Stdout=$stdoutPath Stderr=$stderrPath"
    }

    return $result
}

function Copy-KiwiFileTransactional {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [Parameter(Mandatory = $true)]
        [string[]]$AllowedDestinationRoots,

        [Parameter(Mandatory = $true)]
        [string]$RollbackRoot
    )

    $sourcePath = Assert-KiwiFile -Path $Source -Label "Transactional copy source"
    $destinationPath = Assert-KiwiPathWithinRoot `
        -Path $Destination `
        -AllowedRoots $AllowedDestinationRoots `
        -Label "Transactional copy destination"
    $rollbackPathRoot = Assert-KiwiPathWithinRoot `
        -Path $RollbackRoot `
        -AllowedRoots $AllowedDestinationRoots `
        -Label "Transactional rollback root"

    if (-not (Test-Path -LiteralPath $rollbackPathRoot -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($rollbackPathRoot) | Out-Null
    }
    $destinationDirectory = Split-Path -Parent $destinationPath
    if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
    }

    $token = [guid]::NewGuid().ToString("N")
    $backupPath = Join-Path $rollbackPathRoot (
        [System.IO.Path]::GetFileName($destinationPath) + "." + $token + ".rollback")
    $hadOriginal = Test-Path -LiteralPath $destinationPath -PathType Leaf
    $originalSha256 = $null
    if ($hadOriginal) {
        $originalSha256 = Get-KiwiSha256 -Path $destinationPath
        [System.IO.File]::Copy($destinationPath, $backupPath, $false)
    }

    try {
        [System.IO.File]::Copy($sourcePath, $destinationPath, $true)
        $sourceSha256 = Get-KiwiSha256 -Path $sourcePath
        $destinationSha256 = Get-KiwiSha256 -Path $destinationPath
        if ($sourceSha256 -ne $destinationSha256) {
            throw "Transactional copy SHA256 verification failed."
        }
    }
    catch {
        if ($hadOriginal -and (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
            [System.IO.File]::Copy($backupPath, $destinationPath, $true)
        }
        elseif (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
            [System.IO.File]::Delete($destinationPath)
        }
        throw
    }

    return [pscustomobject]@{
        Source = $sourcePath
        Destination = $destinationPath
        BackupPath = $backupPath
        HadOriginal = $hadOriginal
        OriginalSha256 = $originalSha256
        InstalledSha256 = $destinationSha256
        AllowedDestinationRoots = @($AllowedDestinationRoots)
        Completed = $false
    }
}

function Undo-KiwiFileTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Transaction
    )

    $destinationPath = Assert-KiwiPathWithinRoot `
        -Path ([string]$Transaction.Destination) `
        -AllowedRoots @($Transaction.AllowedDestinationRoots) `
        -Label "Rollback destination"

    if ([bool]$Transaction.HadOriginal) {
        $backupPath = Assert-KiwiFile `
            -Path ([string]$Transaction.BackupPath) `
            -Label "Rollback backup"
        [System.IO.File]::Copy($backupPath, $destinationPath, $true)
        $restoredSha256 = Get-KiwiSha256 -Path $destinationPath
        if ($restoredSha256 -ne [string]$Transaction.OriginalSha256) {
            throw "Rollback SHA256 verification failed: $destinationPath"
        }
    }
    elseif (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
        [System.IO.File]::Delete($destinationPath)
    }

    if (Test-Path -LiteralPath ([string]$Transaction.BackupPath) -PathType Leaf) {
        [System.IO.File]::Delete([string]$Transaction.BackupPath)
    }
}

function Complete-KiwiFileTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Transaction
    )

    $backupPath = [string]$Transaction.BackupPath
    if (-not [string]::IsNullOrWhiteSpace($backupPath)) {
        [void](Assert-KiwiPathWithinRoot `
            -Path $backupPath `
            -AllowedRoots @($Transaction.AllowedDestinationRoots) `
            -Label "Transaction backup")
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
            [System.IO.File]::Delete($backupPath)
        }
    }

    $Transaction.Completed = $true
    return $Transaction
}

function Write-KiwiReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [System.Collections.IEnumerable]$Lines,

        [Parameter(Mandatory = $true)]
        [string[]]$AllowedRoots
    )

    $lineList = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $Lines) {
        $lineList.Add([string]$line)
    }
    $text = ($lineList.ToArray() -join [Environment]::NewLine) + [Environment]::NewLine
    return Write-KiwiTextFile `
        -Path $Path `
        -Text $text `
        -AllowedRoots $AllowedRoots `
        -Encoding "Utf8NoBom"
}

Export-ModuleMember -Function @(
    "Get-KiwiCanonicalPath",
    "Test-KiwiPathWithinRoot",
    "Assert-KiwiPathWithinRoot",
    "Resolve-KiwiProjectRoot",
    "Assert-KiwiFile",
    "Get-KiwiSha256",
    "Assert-KiwiFileSha256",
    "Read-KiwiTextFile",
    "Write-KiwiTextFile",
    "New-KiwiTimestampedOutputDirectory",
    "Find-KiwiVisualStudioTool",
    "Get-KiwiWindowsSdkInfo",
    "Get-KiwiNativeBuildTools",
    "ConvertTo-KiwiProcessArgumentString",
    "Invoke-KiwiProcess",
    "Copy-KiwiFileTransactional",
    "Undo-KiwiFileTransaction",
    "Complete-KiwiFileTransaction",
    "Write-KiwiReport"
)
