param(
    [Parameter(Mandatory = $false)]
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",

    [Parameter(Mandatory = $false)]
    [string[]]$ScanRoots = @("Tools", "Installer", "Native", "KiwiValidation"),

    [Parameter(Mandatory = $false)]
    [string]$ReportDirectory = "",

    [Parameter(Mandatory = $false)]
    [string]$CurrentVersion = "v44.55.18.2",

    [Parameter(Mandatory = $false)]
    [switch]$FailOnWarning
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$Contract = "KIWI_V44_55_18_2_COMMON_POWERSHELL_51_VALIDATOR"
$ModulePath = Join-Path $ProjectRoot "Tools\KiwiPowerShell\KiwiPsCompat.psm1"

if (-not (Test-Path -LiteralPath $ModulePath -PathType Leaf)) {
    throw "Kiwi PowerShell compatibility module is missing: $ModulePath"
}

Import-Module $ModulePath -Force
$ProjectRoot = Resolve-KiwiProjectRoot -ProjectRoot $ProjectRoot

$ExpectedCriticalHashes = [ordered]@{
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

$KnownLegacyNativeInstallerHashes = @{
    "Native\KiwiNativeCamera\BuildProduction_v44_43.ps1" =
        "48CF3B71EC59759B193B1F335B79E0DB570765CC1516BCDD724A1E32B6971C8D"
}

$Findings = [System.Collections.Generic.List[object]]::new()
$FileResults = [System.Collections.Generic.List[object]]::new()
$WriteCommands = [System.Collections.Generic.List[object]]::new()

function Get-RelativeProjectPath {
    param([string]$Path)

    $canonicalPath = Get-KiwiCanonicalPath -Path $Path
    if ($canonicalPath.StartsWith(
        $ProjectRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        return $canonicalPath.Substring($ProjectRoot.Length + 1)
    }
    return $canonicalPath
}

function Add-Finding {
    param(
        [ValidateSet("ERROR", "WARN", "INFO")]
        [string]$Severity,
        [string]$Code,
        [string]$File,
        [int]$Line,
        [string]$Message
    )

    $Findings.Add([pscustomobject]@{
        severity = $Severity
        code = $Code
        file = $File
        line = $Line
        message = $Message
    })
}

function Get-SafeAstValue {
    param([System.Management.Automation.Language.Ast]$Ast)

    if ($null -eq $Ast) {
        return $null
    }

    if ($Ast -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
        return [string]$Ast.Value
    }

    if ($Ast -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) {
        if ($Ast.NestedExpressions.Count -eq 0) {
            return [string]$Ast.Value
        }
        return $null
    }

    try {
        $value = $Ast.SafeGetValue()
        if ($null -ne $value) {
            return [string]$value
        }
    }
    catch {
    }

    return $null
}

function Get-AssignedPathHint {
    param(
        [System.Management.Automation.Language.Ast]$Ast,
        [string]$VariableName
    )

    if ([string]::IsNullOrWhiteSpace($VariableName)) {
        return $null
    }

    $assignments = @(
        $Ast.FindAll(
            {
                param($candidate)
                if (-not ($candidate -is [System.Management.Automation.Language.AssignmentStatementAst])) {
                    return $false
                }
                if (-not ($candidate.Left -is [System.Management.Automation.Language.VariableExpressionAst])) {
                    return $false
                }
                return [string]::Equals(
                    $candidate.Left.VariablePath.UserPath,
                    $VariableName,
                    [System.StringComparison]::OrdinalIgnoreCase)
            },
            $true)
    )

    foreach ($assignment in $assignments) {
        $extent = $assignment.Right.Extent.Text
        $pathMatch = [regex]::Match(
            $extent,
            '(?i)["'']([^"'']*(?:Assets\\Plugins|Native\\KiwiNativeCamera\\Source|KiwiValidation|Logs|Native\\KiwiNativeCamera\\Build)[^"'']*)["'']')
        if ($pathMatch.Success) {
            return $pathMatch.Groups[1].Value
        }
    }

    return $null
}

function Get-CommandTarget {
    param(
        [System.Management.Automation.Language.CommandAst]$Command,
        [string[]]$ParameterNames,
        [switch]$UseFirstPositional
    )

    $elements = @($Command.CommandElements)
    for ($index = 1; $index -lt $elements.Count; $index++) {
        $element = $elements[$index]
        if ($element -is [System.Management.Automation.Language.CommandParameterAst]) {
            if ($element.ParameterName -in $ParameterNames) {
                if ($index + 1 -lt $elements.Count) {
                    return $elements[$index + 1]
                }
                return $null
            }
        }
    }

    if ($UseFirstPositional) {
        for ($index = 1; $index -lt $elements.Count; $index++) {
            if (-not ($elements[$index] -is [System.Management.Automation.Language.CommandParameterAst])) {
                return $elements[$index]
            }
        }
    }

    return $null
}

function Get-VersionToken {
    param([string]$Text)

    $match = [regex]::Match($Text, '(?i)v(\d+)(?:[._](\d+))(?:[._](\d+))?(?:[._](\d+))?')
    if (-not $match.Success) {
        return $null
    }

    $parts = [System.Collections.Generic.List[string]]::new()
    for ($index = 1; $index -le 4; $index++) {
        if ($match.Groups[$index].Success) {
            $parts.Add($match.Groups[$index].Value)
        }
    }
    return "v" + ($parts.ToArray() -join "_")
}

function Test-VersionPrefixMatch {
    param(
        [string]$Left,
        [string]$Right
    )

    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) {
        return $true
    }

    $leftParts = $Left.TrimStart("v", "V") -split "_"
    $rightParts = $Right.TrimStart("v", "V") -split "_"
    $compareCount = [Math]::Min($leftParts.Count, $rightParts.Count)
    for ($index = 0; $index -lt $compareCount; $index++) {
        if ($leftParts[$index] -ne $rightParts[$index]) {
            return $false
        }
    }
    return $true
}

function Test-ProtectedPathHint {
    param([string]$Hint)

    if ([string]::IsNullOrWhiteSpace($Hint)) {
        return $null
    }

    if ($Hint -match '(?i)Assets[\\/]Plugins(?:[\\/]|$)') {
        return "PRODUCTION_DLL"
    }
    if ($Hint -match '(?i)Native[\\/]KiwiNativeCamera[\\/]Source(?:[\\/]|$)') {
        return "NATIVE_SOURCE"
    }
    return $null
}

function Test-AllowedWritePathHint {
    param([string]$Hint)

    if ([string]::IsNullOrWhiteSpace($Hint)) {
        return $false
    }

    return [bool]($Hint -match '(?i)(^|[\\/])(KiwiValidation|ValidationRuns|Logs|Tools|Installer)([\\/]|$)|Native[\\/]KiwiNativeCamera[\\/]Build([\\/]|$)')
}

function Test-ScriptFile {
    param([string]$Path)

    $relativePath = Get-RelativeProjectPath -Path $Path
    $text = Read-KiwiTextFile -Path $Path -MaximumBytes 33554432
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$parseErrors)

    foreach ($parseError in @($parseErrors)) {
        Add-Finding `
            -Severity "ERROR" `
            -Code "PS51_PARSE" `
            -File $relativePath `
            -Line $parseError.Extent.StartLineNumber `
            -Message $parseError.Message
    }

    foreach ($token in @($tokens)) {
        $kind = [string]$token.Kind
        if ($kind -in @("AndAnd", "OrOr", "QuestionQuestion")) {
            Add-Finding `
                -Severity "ERROR" `
                -Code "FORBIDDEN_SYNTAX" `
                -File $relativePath `
                -Line $token.Extent.StartLineNumber `
                -Message ("PowerShell 7-only operator detected: " + $token.Text)
        }
    }

    $strictModePresent = [bool]($text -match '(?im)^\s*Set-StrictMode\s+-Version\s+2(?:\.0)?\s*$')
    $errorPreferencePresent = [bool]($text -match '(?im)^\s*\$ErrorActionPreference\s*=\s*["'']Stop["'']\s*$')
    $foundationFile = [bool](
        $relativePath -match '(?i)^Installer[\\/]' -or
        $relativePath -match '(?i)^Tools[\\/]KiwiPowerShell[\\/]')

    if (-not $strictModePresent) {
        Add-Finding `
            -Severity $(if ($foundationFile) { "ERROR" } else { "WARN" }) `
            -Code "REQUIRED_STRICT_MODE" `
            -File $relativePath `
            -Line 1 `
            -Message "Set-StrictMode -Version 2.0 is missing."
    }
    if (-not $errorPreferencePresent) {
        Add-Finding `
            -Severity $(if ($foundationFile) { "ERROR" } else { "WARN" }) `
            -Code "REQUIRED_ERROR_PREFERENCE" `
            -File $relativePath `
            -Line 1 `
            -Message 'Explicit $ErrorActionPreference = "Stop" is missing.'
    }

    $genericVariables = [System.Collections.Generic.List[string]]::new()
    foreach ($match in [regex]::Matches(
        $text,
        '(?im)\$([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:\[[^\r\n]*Generic\.List|New-Object\s+[^\r\n]*Generic\.List)')) {
        $genericVariables.Add($match.Groups[1].Value)
    }
    foreach ($variableName in $genericVariables.ToArray()) {
        $dangerousPattern = '@\(\s*\$' + [regex]::Escape($variableName) + '\s*\)'
        $dangerous = [regex]::Match($text, $dangerousPattern)
        if ($dangerous.Success) {
            $line = 1 + ($text.Substring(0, $dangerous.Index).Split("`n").Count - 1)
            Add-Finding `
                -Severity "ERROR" `
                -Code "GENERIC_LIST_ARRAY_COERCION" `
                -File $relativePath `
                -Line $line `
                -Message ("Use $" + $variableName + ".ToArray() instead of @($" + $variableName + ").")
        }
    }

    $placeholderMatches = @([regex]::Matches($text, '__[A-Z][A-Z0-9_]+__'))
    if ($placeholderMatches.Count -gt 0) {
        $placeholderGuarded = [bool](
            $text -match '(?i)Unresolved (?:build )?template placeholder' -and
            $text -match '\.Replace\(')
        $placeholderLine = 1 + ($text.Substring(0, $placeholderMatches[0].Index).Split("`n").Count - 1)
        Add-Finding `
            -Severity $(if ($placeholderGuarded) { "INFO" } else { "WARN" }) `
            -Code $(if ($placeholderGuarded) { "PLACEHOLDER_GUARDED" } else { "UNRESOLVED_PLACEHOLDER" }) `
            -File $relativePath `
            -Line $placeholderLine `
            -Message ("Template placeholder tokens found: " + (@($placeholderMatches | ForEach-Object { $_.Value } | Sort-Object -Unique) -join ","))
    }

    $contractVersion = $null
    $contractMatch = [regex]::Match($text, '(?im)^\s*\$Contract\s*=\s*["'']([^"'']+)["'']')
    if ($contractMatch.Success) {
        $contractVersion = Get-VersionToken -Text $contractMatch.Groups[1].Value
    }

    $fileVersion = Get-VersionToken -Text $relativePath
    $currentVersionToken = Get-VersionToken -Text $CurrentVersion
    $pathStringAsts = @(
        $ast.FindAll(
            {
                param($candidate)
                return (
                    $candidate -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
                    $candidate -is [System.Management.Automation.Language.ExpandableStringExpressionAst]
                )
            },
            $true)
    )
    foreach ($pathStringAst in $pathStringAsts) {
        $pathString = Get-SafeAstValue -Ast $pathStringAst
        if (
            [string]::IsNullOrWhiteSpace($pathString) -or
            $pathString -notmatch '(?i)(KiwiValidation|ValidationRuns|Builds|Installer|Tools)[\\/][^\r\n]*v\d+(?:[._]\d+){1,3}'
        ) {
            continue
        }

        $pathVersion = Get-VersionToken -Text $pathString
        $expectedContextVersion = if (-not [string]::IsNullOrWhiteSpace($contractVersion)) {
            $contractVersion
        }
        elseif (-not [string]::IsNullOrWhiteSpace($fileVersion)) {
            $fileVersion
        }
        else {
            $currentVersionToken
        }

        if (-not (Test-VersionPrefixMatch -Left $expectedContextVersion -Right $pathVersion)) {
            Add-Finding `
                -Severity "WARN" `
                -Code "HARDCODED_OBSOLETE_VERSION_PATH" `
                -File $relativePath `
                -Line $pathStringAst.Extent.StartLineNumber `
                -Message "ContextVersion=$expectedContextVersion PathVersion=$pathVersion Path=$pathString"
        }
    }

    foreach ($reportMatch in [regex]::Matches(
        $text,
        '(?i)Kiwi[A-Za-z0-9_.-]*v\d+(?:_\d+){1,3}[A-Za-z0-9_.-]*\.(?:txt|json|log|csv)')) {
        $reportVersion = Get-VersionToken -Text $reportMatch.Value
        if (-not (Test-VersionPrefixMatch -Left $contractVersion -Right $reportVersion)) {
            $line = 1 + ($text.Substring(0, $reportMatch.Index).Split("`n").Count - 1)
            Add-Finding `
                -Severity "ERROR" `
                -Code "REPORT_VERSION_MISMATCH" `
                -File $relativePath `
                -Line $line `
                -Message "ContractVersion=$contractVersion ReportVersion=$reportVersion FileName=$($reportMatch.Value)"
        }
    }

    $mutatingCommands = @(
        "copy-item", "move-item", "remove-item", "set-content", "add-content",
        "out-file", "export-csv", "new-item", "rename-item"
    )

    $commands = @(
        $ast.FindAll(
            {
                param($candidate)
                return $candidate -is [System.Management.Automation.Language.CommandAst]
            },
            $true)
    )

    foreach ($command in $commands) {
        $commandName = $command.GetCommandName()
        if ([string]::IsNullOrWhiteSpace($commandName)) {
            continue
        }
        $commandName = $commandName.ToLowerInvariant()
        if ($commandName -notin $mutatingCommands) {
            continue
        }

        $targetAst = $null
        switch ($commandName) {
            "copy-item" {
                $targetAst = Get-CommandTarget `
                    -Command $command `
                    -ParameterNames @("Destination")
            }
            "move-item" {
                $targetAst = Get-CommandTarget `
                    -Command $command `
                    -ParameterNames @("Destination")
            }
            "rename-item" {
                $targetAst = Get-CommandTarget `
                    -Command $command `
                    -ParameterNames @("NewName")
            }
            default {
                $targetAst = Get-CommandTarget `
                    -Command $command `
                    -ParameterNames @("LiteralPath", "Path", "FilePath") `
                    -UseFirstPositional
            }
        }

        $targetValue = Get-SafeAstValue -Ast $targetAst
        $targetHint = $targetValue
        if (
            [string]::IsNullOrWhiteSpace($targetHint) -and
            $targetAst -is [System.Management.Automation.Language.VariableExpressionAst]
        ) {
            $targetHint = Get-AssignedPathHint `
                -Ast $ast `
                -VariableName $targetAst.VariablePath.UserPath
        }

        $protectedKind = Test-ProtectedPathHint -Hint $targetHint
        $isKnownLegacyInstaller = $false
        if ($KnownLegacyNativeInstallerHashes.ContainsKey($relativePath)) {
            $knownHash = [string]$KnownLegacyNativeInstallerHashes[$relativePath]
            $actualHash = Get-KiwiSha256 -Path $Path
            $isKnownLegacyInstaller = [bool](
                $actualHash -eq $knownHash -and
                $text -match '(?i)\[switch\]\s*\$Install')
        }

        if ($protectedKind -eq "PRODUCTION_DLL") {
            Add-Finding `
                -Severity $(if ($isKnownLegacyInstaller) { "WARN" } else { "ERROR" }) `
                -Code $(if ($isKnownLegacyInstaller) { "KNOWN_LEGACY_PRODUCTION_INSTALLER" } else { "PRODUCTION_DLL_WRITE" }) `
                -File $relativePath `
                -Line $command.Extent.StartLineNumber `
                -Message ("$commandName can target Assets/Plugins. Hint=$targetHint")
        }
        elseif ($protectedKind -eq "NATIVE_SOURCE") {
            Add-Finding `
                -Severity "ERROR" `
                -Code "NATIVE_SOURCE_WRITE" `
                -File $relativePath `
                -Line $command.Extent.StartLineNumber `
                -Message ("$commandName can target canonical Native source. Hint=$targetHint")
        }
        elseif ([string]::IsNullOrWhiteSpace($targetHint)) {
            Add-Finding `
                -Severity "WARN" `
                -Code "DYNAMIC_WRITE_TARGET" `
                -File $relativePath `
                -Line $command.Extent.StartLineNumber `
                -Message ("$commandName destination could not be resolved statically.")
        }
        elseif (-not (Test-AllowedWritePathHint -Hint $targetHint)) {
            Add-Finding `
                -Severity "WARN" `
                -Code "WRITE_TARGET_OUTSIDE_ALLOWLIST" `
                -File $relativePath `
                -Line $command.Extent.StartLineNumber `
                -Message ("$commandName target is outside the standard write allowlist. Hint=$targetHint")
        }

        $WriteCommands.Add([pscustomobject]@{
            file = $relativePath
            line = $command.Extent.StartLineNumber
            command = $commandName
            targetHint = $targetHint
            protectedKind = $protectedKind
        })
    }

    foreach ($dotNetMatch in [regex]::Matches(
        $text,
        '(?i)\[System\.IO\.(?:File|Directory)\]::(?:WriteAllText|WriteAllLines|Copy|Move|Delete|Replace|CreateDirectory)\s*\(')) {
        $line = 1 + ($text.Substring(0, $dotNetMatch.Index).Split("`n").Count - 1)
        Add-Finding `
            -Severity "INFO" `
            -Code "DOTNET_FILE_MUTATION_REVIEWED" `
            -File $relativePath `
            -Line $line `
            -Message $dotNetMatch.Value.Trim()
    }

    $FileResults.Add([pscustomobject]@{
        path = $relativePath
        sha256 = Get-KiwiSha256 -Path $Path
        length = (Get-Item -LiteralPath $Path).Length
        parseErrorCount = @($parseErrors).Count
        strictMode = [int]$strictModePresent
        errorPreference = [int]$errorPreferencePresent
        writeCommandCount = @($commands | Where-Object {
            $name = $_.GetCommandName()
            -not [string]::IsNullOrWhiteSpace($name) -and
            $name.ToLowerInvariant() -in $mutatingCommands
        }).Count
    })
}

$CriticalBefore = [System.Collections.Generic.List[object]]::new()
foreach ($criticalPath in @($ExpectedCriticalHashes.Keys | Sort-Object)) {
    $CriticalBefore.Add(
        (Assert-KiwiFileSha256 `
            -Path $criticalPath `
            -ExpectedSha256 ([string]$ExpectedCriticalHashes[$criticalPath]) `
            -Label "Production critical before audit"))
}

$isWindowsPowerShell51 = [bool](
    $PSVersionTable.PSEdition -eq "Desktop" -and
    $PSVersionTable.PSVersion.Major -eq 5 -and
    $PSVersionTable.PSVersion.Minor -eq 1)
if (-not $isWindowsPowerShell51) {
    Add-Finding `
        -Severity "ERROR" `
        -Code "VALIDATOR_HOST_NOT_PS51" `
        -File "Installer\Validate-KiwiCommon.ps1" `
        -Line 1 `
        -Message "Run this validator with Windows PowerShell 5.1 for authoritative parse results."
}

$scriptFiles = [System.Collections.Generic.List[string]]::new()
foreach ($scanRoot in $ScanRoots) {
    $scanPath = Get-KiwiCanonicalPath -Path $scanRoot -BasePath $ProjectRoot
    if (-not (Test-KiwiPathWithinRoot -Path $scanPath -Root $ProjectRoot)) {
        Add-Finding `
            -Severity "ERROR" `
            -Code "SCAN_ROOT_OUTSIDE_PROJECT" `
            -File $scanPath `
            -Line 1 `
            -Message "Scan root is outside ProjectRoot."
        continue
    }
    if (-not (Test-Path -LiteralPath $scanPath -PathType Container)) {
        Add-Finding `
            -Severity "WARN" `
            -Code "SCAN_ROOT_MISSING" `
            -File (Get-RelativeProjectPath -Path $scanPath) `
            -Line 1 `
            -Message "Scan root is missing."
        continue
    }

    foreach ($file in @(
        Get-ChildItem -LiteralPath $scanPath -Recurse -File |
        Where-Object { $_.Extension -in @(".ps1", ".psm1") } |
        Sort-Object FullName
    )) {
        $alreadyAdded = $false
        foreach ($existing in $scriptFiles) {
            if ([string]::Equals(
                $existing,
                $file.FullName,
                [System.StringComparison]::OrdinalIgnoreCase)) {
                $alreadyAdded = $true
                break
            }
        }
        if (-not $alreadyAdded) {
            $scriptFiles.Add($file.FullName)
        }
    }
}

foreach ($scriptFile in $scriptFiles.ToArray()) {
    Test-ScriptFile -Path $scriptFile
}

$CriticalAfter = [System.Collections.Generic.List[object]]::new()
foreach ($criticalPath in @($ExpectedCriticalHashes.Keys | Sort-Object)) {
    $CriticalAfter.Add(
        (Assert-KiwiFileSha256 `
            -Path $criticalPath `
            -ExpectedSha256 ([string]$ExpectedCriticalHashes[$criticalPath]) `
            -Label "Production critical after audit"))
}

if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $reportRoot = Join-Path $ProjectRoot "KiwiValidation\PowerShellAudit"
    $ReportDirectory = New-KiwiTimestampedOutputDirectory `
        -Root $reportRoot `
        -Purpose "Common" `
        -AllowedRoots @((Join-Path $ProjectRoot "KiwiValidation"))
}
else {
    $ReportDirectory = Assert-KiwiPathWithinRoot `
        -Path (Get-KiwiCanonicalPath -Path $ReportDirectory -BasePath $ProjectRoot) `
        -AllowedRoots @((Join-Path $ProjectRoot "KiwiValidation")) `
        -Label "Validator report directory"
    if (-not (Test-Path -LiteralPath $ReportDirectory -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($ReportDirectory) | Out-Null
    }
}

$errorCount = @($Findings.ToArray() | Where-Object { $_.severity -eq "ERROR" }).Count
$warningCount = @($Findings.ToArray() | Where-Object { $_.severity -eq "WARN" }).Count
$infoCount = @($Findings.ToArray() | Where-Object { $_.severity -eq "INFO" }).Count
$status = if ($errorCount -gt 0) { "FAIL" } elseif ($warningCount -gt 0) { "WARN" } else { "PASS" }

$result = [ordered]@{
    contract = $Contract
    generated = (Get-Date).ToString("o")
    status = $status
    currentVersion = $CurrentVersion
    projectRoot = $ProjectRoot
    powershellVersion = $PSVersionTable.PSVersion.ToString()
    powershellEdition = $PSVersionTable.PSEdition
    authoritativePs51Host = [int]$isWindowsPowerShell51
    scriptCount = $scriptFiles.Count
    errorCount = $errorCount
    warningCount = $warningCount
    infoCount = $infoCount
    productionCriticalBefore = $CriticalBefore.ToArray()
    productionCriticalAfter = $CriticalAfter.ToArray()
    files = $FileResults.ToArray()
    writeCommands = $WriteCommands.ToArray()
    findings = $Findings.ToArray()
}

$jsonPath = Join-Path $ReportDirectory "KiwiPowerShellAudit.json"
$textPath = Join-Path $ReportDirectory "KiwiPowerShellAudit.txt"
$json = $result | ConvertTo-Json -Depth 12
[void](Write-KiwiTextFile `
    -Path $jsonPath `
    -Text $json `
    -AllowedRoots @((Join-Path $ProjectRoot "KiwiValidation")))

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("KiwiAvatarSystem Common PowerShell 5.1 Audit")
$lines.Add("contract=$Contract")
$lines.Add("status=$status")
$lines.Add("powershell=$($PSVersionTable.PSVersion)|edition=$($PSVersionTable.PSEdition)|authoritativePs51Host=$([int]$isWindowsPowerShell51)")
$lines.Add("scriptCount=$($scriptFiles.Count)")
$lines.Add("errorCount=$errorCount")
$lines.Add("warningCount=$warningCount")
$lines.Add("infoCount=$infoCount")
$lines.Add("")
$lines.Add("[FILES]")
foreach ($fileResult in $FileResults.ToArray()) {
    $lines.Add("path=$($fileResult.path)|sha256=$($fileResult.sha256)|parseErrors=$($fileResult.parseErrorCount)|strictMode=$($fileResult.strictMode)|errorPreference=$($fileResult.errorPreference)|writeCommands=$($fileResult.writeCommandCount)")
}
$lines.Add("")
$lines.Add("[FINDINGS]")
foreach ($finding in $Findings.ToArray()) {
    $lines.Add("severity=$($finding.severity)|code=$($finding.code)|file=$($finding.file)|line=$($finding.line)|message=$($finding.message)")
}
$lines.Add("")
$lines.Add("[PRODUCTION_HASHES]")
foreach ($critical in $CriticalAfter.ToArray()) {
    $lines.Add("path=$(Get-RelativeProjectPath -Path $critical.Path)|sha256=$($critical.Sha256)")
}

[void](Write-KiwiReport `
    -Path $textPath `
    -Lines $lines.ToArray() `
    -AllowedRoots @((Join-Path $ProjectRoot "KiwiValidation")))

Write-Host ""
Write-Host "KIWI POWERSHELL AUDIT $status"
Write-Host "scripts=$($scriptFiles.Count) errors=$errorCount warnings=$warningCount info=$infoCount"
Write-Host "report=$textPath"
Write-Host "json=$jsonPath"
Write-Host ""

if ($errorCount -gt 0) {
    exit 1
}
if ($FailOnWarning -and $warningCount -gt 0) {
    exit 2
}
exit 0
