param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",
    [switch]$StaticOnly,
    [string]$EvidenceStamp = "",
    [string]$RuntimeEvidenceDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$modulePath = Join-Path $PSScriptRoot "..\KiwiPowerShell\KiwiPsCompat.psm1"
Import-Module -Name $modulePath -Force

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Read-KeyValues {
    param([string]$Path)

    $values = @{}
    foreach ($line in (Get-Content -LiteralPath $Path)) {
        if ($line -match '^([^=]+)=(.*)$') {
            $values[$matches[1]] = $matches[2]
        }
    }
    return $values
}

function Get-CSharpViolations {
    param([string]$Text)

    $patterns = [ordered]@{
        "input content readback" = '(?<!Is)ReadbackRequest\s*\('
        "tensor download" = 'DownloadToArray\s*\('
        "compute-buffer input oracle" = 'CopyBuffer|CopyCounterValue'
        "texture readback" = 'ReadPixels\s*\(|AsyncGPUReadback\.Request\s*\('
        "new Worker" = 'new\s+Worker\s*\('
        "Worker schedule" = '\.Schedule\s*\('
        "command-buffer worker schedule" = 'ScheduleWorker\s*\('
        "blocking async wait" = 'WaitForCompletion\s*\('
        "graphics fence wait" = 'GraphicsFence|WaitOnAsyncGraphicsFence'
        "thread polling sleep" = 'Thread\.Sleep\s*\('
        "new observer GPU buffer" = 'new\s+ComputeBuffer\s*\('
        "observer GPU execution" = 'Graphics\.ExecuteCommandBuffer'
        "external texture replacement" = 'UpdateExternalTexture\s*\('
        "D3D12 queue wait" = 'g_unityD3D12Queue[^\r\n]*Wait'
    }

    $findings = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @($patterns.Keys)) {
        if ($Text -match [string]$patterns[$name]) {
            $findings.Add([string]$name)
        }
    }
    return $findings.ToArray()
}

function Get-PowerShellViolations {
    param([string]$Text)

    $patterns = [ordered]@{
        "unsafe Copy-Item" = '(?im)^\s*Copy-Item\b'
        "unsafe Move-Item" = '(?im)^\s*Move-Item\b'
        "unsafe Remove-Item" = '(?im)^\s*Remove-Item\b'
        "PowerShell 7 null coalescing" = '\?\?'
        "PowerShell 7 ternary" = '(?m)\?\s*[^\r\n:]+\s*:'
        "PowerShell 7 pipeline chain" = '&&|\|\|'
        "broad recursive delete" = '(?i)(rm|del|rmdir)\s+[^\r\n]*(/s|-r|-recurse)'
    }

    $findings = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @($patterns.Keys)) {
        if ($Text -match [string]$patterns[$name]) {
            $findings.Add([string]$name)
        }
    }
    return $findings.ToArray()
}

function Assert-PowerShellParser {
    param([string]$Path)

    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors)
    Assert-True ($errors.Count -eq 0) `
        ("PowerShell 5.1 parser errors in " + $Path + ": " +
        (($errors | ForEach-Object { $_.Message }) -join " | "))
}

function Assert-ValidatorSelfAudit {
    $csharpCases = @(
        "lane.input.ReadbackRequest();",
        "tensor.DownloadToArray();",
        "var worker = new Worker(model, backend);",
        "worker.Schedule(input);",
        "cb.ScheduleWorker(worker, input);",
        "request.WaitForCompletion();",
        "Thread.Sleep(10);",
        "var buffer = new ComputeBuffer(1, 4);"
    )

    foreach ($case in $csharpCases) {
        $found = @(Get-CSharpViolations -Text $case)
        Assert-True ($found.Count -gt 0) `
            "Validator self-audit missed prohibited C# case: $case"
    }

    $powerShellCases = @(
        "Copy-Item -LiteralPath `$a -Destination `$b",
        "Move-Item -LiteralPath `$a -Destination `$b",
        "Remove-Item -LiteralPath `$a",
        "`$x = `$a ?? `$b"
    )

    foreach ($case in $powerShellCases) {
        $found = @(Get-PowerShellViolations -Text $case)
        Assert-True ($found.Count -gt 0) `
            "Validator self-audit missed unsafe PowerShell case: $case"
    }
}

$ProjectRoot = Resolve-KiwiProjectRoot -ProjectRoot $ProjectRoot
$observer = Join-Path $ProjectRoot `
    "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_25.cs"
$observerMeta = $observer + ".meta"
$buildScript = Join-Path $PSScriptRoot "Build-KiwiV44_55_25.ps1"
$runScript = Join-Path $PSScriptRoot "Run-KiwiV44_55_25.ps1"
$validateScript = Join-Path $PSScriptRoot "Validate-KiwiV44_55_25.ps1"
$readme = Join-Path $PSScriptRoot "README.txt"

$requiredFiles = @(
    $observer,
    $observerMeta,
    $buildScript,
    $runScript,
    $validateScript,
    $readme,
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiCommonTensorBackendStageIsolationV44_55_20.cs"),
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiPairBoundShadowOutputSnapshotV44_55_24.cs"),
    (Join-Path $ProjectRoot "Assets\Script\KiwiInferenceFaceTracker.cs"),
    (Join-Path $ProjectRoot "Assets\Plugins\x86_64\KiwiNativeCamera.dll")
)

foreach ($path in $requiredFiles) {
    [void](Assert-KiwiFile -Path $path -Label "Expected v44.55.25 source/tool")
}

$protectedHashes = @{
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiCommonTensorBackendStageIsolationV44_55_20.cs") = "1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiPairBoundShadowOutputSnapshotV44_55_24.cs") = "805E746E453F3785FCAA46EFB07745B0038A428789D4A09BB944854F15477FBA"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionOutputTransactionAuthorityV44_55_23.cs") = "AE2CD168171335E20F6793C5F46A12264D86CCAE21DF62FAFF74FCBA1E92E4A7"
    (Join-Path $ProjectRoot "Assets\Script\KiwiInferenceFaceTracker.cs") = "52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Tracking\KiwiInferenceFaceTracker.cs") = "EFFCCCF5EE1BF407F065BFA95B491390AC96A55E9A75290A67C5AFAD32AFC1F1"
    (Join-Path $ProjectRoot "Assets\Script\FaceLandmarkerRunner.cs") = "6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93"
    (Join-Path $ProjectRoot "Assets\Script\KiwiFaceMotion.cs") = "D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Camera\KiwiNativeCameraInterop.cs") = "AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5"
    (Join-Path $ProjectRoot "Assets\Plugins\x86_64\KiwiNativeCamera.dll") = "82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5"
    (Join-Path $ProjectRoot "Packages\manifest.json") = "35F00A89D31A8BF7EE50718F38D4BAC90236D8B691F516C5FC14CAB47AFBFEE1"
    (Join-Path $ProjectRoot "Packages\packages-lock.json") = "23845C132DBF51CB53A5CFDE4EF606FA68D10DCB53AE5FF21F4305613A5F9CE1"
}

foreach ($path in @($protectedHashes.Keys)) {
    [void](Assert-KiwiFileSha256 `
        -Path ([string]$path) `
        -ExpectedSha256 ([string]$protectedHashes[$path]) `
        -Label "Protected Production/Native/Tracking file")
}

[void](Assert-KiwiFileSha256 `
    -Path $observer `
    -ExpectedSha256 "90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814" `
    -Label "v44.55.25 observer")
[void](Assert-KiwiFileSha256 `
    -Path $observerMeta `
    -ExpectedSha256 "4CE42873100AF234F45C23C3F5B0D6B8E96B0D34B94FCAEA5E3E5A20EC1A9874" `
    -Label "v44.55.25 observer meta")

$observerText = Get-Content -LiteralPath $observer -Raw
$toolTexts = [System.Collections.Generic.List[string]]::new()
foreach ($path in @($buildScript, $runScript, $readme)) {
    $toolTexts.Add((Get-Content -LiteralPath $path -Raw))
}
$allToolText = $toolTexts.ToArray() -join [Environment]::NewLine

Assert-True ($observerText -match 'v44\.55\.25') `
    "Observer lacks the v44.55.25 version token."
Assert-True ($observerText -notmatch 'v44\.55\.(2[0-46-9]|[0-1][0-9]) Production Schedule Transaction Trace') `
    "Observer contains a wrong version title token."
Assert-True ($allToolText -notmatch 'v44_55_(23|24)[^\r\n]*\.exe') `
    "v44.55.25 tools reference an old v23/v24 output EXE."
Assert-True ($allToolText -notmatch 'KiwiPairBoundShadowOutputSnapshot_v44_55_24_[^\r\n]*\.(txt|csv)') `
    "v44.55.25 tools reference old v24 runtime output filenames."
Assert-True (($observerText + $allToolText) -notmatch '(?i)TODO|TBD|PLACEHOLDER|CHANGEME') `
    "Placeholder token remains in v44.55.25 implementation/tools."
Assert-True ($allToolText -notmatch 'v44_55_23ProductionOutputAuthority|v44_55_24PairBoundShadowOutput') `
    "Obsolete v23/v24 build path remains in v44.55.25 tools."

$csharpViolations = @(Get-CSharpViolations -Text $observerText)
Assert-True ($csharpViolations.Count -eq 0) `
    ("Prohibited observer operation detected: " + ($csharpViolations -join ", "))

$powerShellSurfaceText = $allToolText -replace `
    '(?s)\$editorSource\s*=\s*@''.*?''@', `
    ''
$powerShellViolations = @(Get-PowerShellViolations -Text $powerShellSurfaceText)
Assert-True ($powerShellViolations.Count -eq 0) `
    ("Unsafe/PS7-only PowerShell detected: " +
    ($powerShellViolations -join ", "))

foreach ($path in @($buildScript, $runScript, $validateScript)) {
    Assert-PowerShellParser -Path $path
}

$environmentToken = "KIWI_V44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE"
Assert-True ($observerText.Contains($environmentToken)) `
    "Observer environment-variable contract mismatch."
Assert-True ((Get-Content -LiteralPath $runScript -Raw).Contains($environmentToken)) `
    "Run tool environment-variable contract mismatch."
Assert-True ((Get-Content -LiteralPath $readme -Raw).Contains($environmentToken)) `
    "README environment-variable contract mismatch."

$expectedOutput = "KiwiAvatarSystem_v44_55_25_PRODUCTION_SCHEDULE_TRANSACTION_TRACE.exe"
Assert-True ((Get-Content -LiteralPath $buildScript -Raw).Contains($expectedOutput)) `
    "Build output filename mismatch."
Assert-True ((Get-Content -LiteralPath $runScript -Raw).Contains($expectedOutput)) `
    "Run output filename mismatch."

$expectedRuntimeBase = "KiwiProductionScheduleTransactionTrace_v44_55_25_"
Assert-True ($observerText.Contains($expectedRuntimeBase)) `
    "Observer runtime report filename mismatch."
Assert-True ((Get-Content -LiteralPath $runScript -Raw).Contains($expectedRuntimeBase)) `
    "Run tool runtime report filename mismatch."

$requiredColumns = @(
    "recordIndex",
    "sequence",
    "pairToken",
    "laneIndex",
    "sourceHostTicks",
    "scheduleBeginHostTicks",
    "startedHostTicks",
    "readbackRequestHostTicks",
    "readbackRequestFrame",
    "anchorRevision",
    "externalAnchorEpoch",
    "trackerGeneration",
    "cameraGeneration",
    "trackingSessionGeneration",
    "minimumPresenceBits",
    "cropMatrixBits",
    "laneIdentityToken",
    "workerIdentityToken",
    "inputTensorIdentityToken",
    "pendingOutputIdentityToken",
    "pairAttachFrame",
    "outputReadyFrame",
    "snapshotSubmissionFrame",
    "recordCompletionFrame",
    "canonicalPublicationFrame",
    "canonicalPublicationFrameId",
    "canonicalPublicationTimestamp",
    "canonicalPublicationArrivalHostTicks",
    "scheduleIdentityCoherent",
    "v24BindingCoherent",
    "canonicalPublicationCoherent",
    "classification"
)

foreach ($column in $requiredColumns) {
    Assert-True ($observerText.Contains($column)) `
        "Expected Runtime column absent from observer: $column"
}

$bootstrapMatches = [regex]::Matches(
    $observerText,
    'RuntimeInitializeOnLoadMethod')
$installGuardMatches = [regex]::Matches(
    $observerText,
    'private\s+static\s+bool\s+_installed')
Assert-True ($bootstrapMatches.Count -eq 1) `
    "Duplicate observer bootstrap detected."
Assert-True ($installGuardMatches.Count -eq 1) `
    "Static install guard missing or duplicated."
Assert-True ($observerText -match 'performanceAuthority=0') `
    "Observer incorrectly establishes Production Performance Authority."

Assert-ValidatorSelfAudit

Write-Host "STATIC_VALIDATION_PASS"
Write-Host "VALIDATOR_SELF_AUDIT_PASS"
Write-Host ("OBSERVER_SHA256=" + (Get-KiwiSha256 -Path $observer))
Write-Host "PROTECTED_SHA_PASS"
Write-Host "POWERSHELL_5_1_PARSER_PASS"

if ($StaticOnly) {
    exit 0
}

Assert-True (-not [string]::IsNullOrWhiteSpace($EvidenceStamp)) `
    "EvidenceStamp is required unless -StaticOnly is used."

if ([string]::IsNullOrWhiteSpace($RuntimeEvidenceDirectory)) {
    $RuntimeEvidenceDirectory = Join-Path $env:USERPROFILE `
        "AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem\KiwiFrameBottleneck"
}

$RuntimeEvidenceDirectory = Get-KiwiCanonicalPath `
    -Path $RuntimeEvidenceDirectory
$textPath = Join-Path $RuntimeEvidenceDirectory `
    ($expectedRuntimeBase + $EvidenceStamp + ".txt")
$csvPath = Join-Path $RuntimeEvidenceDirectory `
    ($expectedRuntimeBase + $EvidenceStamp + ".csv")
[void](Assert-KiwiFile -Path $textPath -Label "v44.55.25 Runtime TXT")
[void](Assert-KiwiFile -Path $csvPath -Label "v44.55.25 Runtime CSV")

$kv = Read-KeyValues -Path $textPath
$observerIntegrityZeroKeys = @(
    "observerFaultCount",
    "scheduleAttachMissCount",
    "ambiguousLaneMatchCount",
    "v24PairIdentityMismatchCount",
    "outputReadyObservationMissCount",
    "snapshotBindingMissCount",
    "canonicalPublicationDuplicateCount",
    "duplicateTraceCount",
    "partialTraceCount",
    "tracesPending"
)

$scheduleFindingKeys = @(
    "laneIdentityMismatchCount",
    "workerIdentityMismatchCount",
    "inputTensorIdentityMismatchCount",
    "pendingOutputIdentityMismatchCount",
    "sourceHostTicksMismatchCount",
    "scheduleBeginTicksMismatchCount",
    "startedHostTicksMismatchCount",
    "readbackRequestTicksMismatchCount",
    "readbackRequestFrameMismatchCount",
    "anchorRevisionMismatchCount",
    "externalAnchorEpochMismatchCount",
    "trackerGenerationMismatchCount",
    "cameraGenerationMismatchCount",
    "trackingSessionGenerationMismatchCount",
    "minimumPresenceMismatchCount",
    "cropMatrixMismatchCount"
)

Assert-True ($kv["status"] -eq "COMPLETE") `
    "Runtime observer status is not COMPLETE."
Assert-True ($kv["dependencyStatus"] -eq "COMPLETE") `
    "Runtime dependencyStatus is not COMPLETE."
Assert-True ($kv["decision"] -in @(
        "TRANSACTION_COHERENT",
        "SCHEDULE_TRANSACTION_MISMATCH",
        "PUBLISHED_TRANSACTION_COHERENT_PARTIAL_COVERAGE",
        "INSUFFICIENT_PUBLISHED_ANOMALOUS_DATA",
        "CANONICAL_PUBLICATION_IDENTITY_INVALID")) `
    "Unknown v44.55.25 decision token."
Assert-True ([int]$kv["completedTraceCount"] -ge 60) `
    "completedTraceCount is below 60."
Assert-True (
    [int]$kv["dependencyPairCount"] -eq
    [int]$kv["completedTraceCount"]) `
    "dependencyPairCount differs from completedTraceCount."
Assert-True ([int]$kv["anomalousPairCount"] -ge 3) `
    "anomalousPairCount is below 3."

foreach ($key in $observerIntegrityZeroKeys) {
    Assert-True ($kv.ContainsKey($key)) "Runtime TXT key missing: $key"
    Assert-True ([int]$kv[$key] -eq 0) "$key is nonzero."
}

$scheduleFindingTotal = 0
foreach ($key in $scheduleFindingKeys) {
    Assert-True ($kv.ContainsKey($key)) "Runtime TXT key missing: $key"
    $scheduleFindingTotal += [int]$kv[$key]
}

Assert-True ($kv.ContainsKey("canonicalPublicationMissCount")) `
    "Runtime TXT key missing: canonicalPublicationMissCount"
$canonicalPublicationMissCount =
    [int]$kv["canonicalPublicationMissCount"]
Assert-True ($kv.ContainsKey("canonicalPublicationIdentityInvalidCount")) `
    "Runtime TXT key missing: canonicalPublicationIdentityInvalidCount"
$canonicalPublicationIdentityInvalidCount =
    [int]$kv["canonicalPublicationIdentityInvalidCount"]
Assert-True ($kv.ContainsKey("canonicalPublicationBoundAnomalousCount")) `
    "Runtime TXT key missing: canonicalPublicationBoundAnomalousCount"
$canonicalPublicationBoundAnomalousCount =
    [int]$kv["canonicalPublicationBoundAnomalousCount"]

$rows = @(Import-Csv -LiteralPath $csvPath)
Assert-True ($rows.Count -eq [int]$kv["completedTraceCount"]) `
    "CSV row count differs from completedTraceCount."

$seenTokens = @{}
$scheduleMismatchRowCount = 0
$canonicalInvalidRowCount = 0
$canonicalUnobservedRowCount = 0
foreach ($row in $rows) {
    foreach ($column in $requiredColumns) {
        Assert-True ($null -ne $row.PSObject.Properties[$column]) `
            "Runtime CSV column missing: $column"
    }

    Assert-True ([int]$row.pairToken -gt 0) `
        "Invalid pairToken at record $($row.recordIndex)."
    Assert-True (-not $seenTokens.ContainsKey($row.pairToken)) `
        "Duplicate pairToken $($row.pairToken)."
    $seenTokens[$row.pairToken] = $true
    Assert-True ([int]$row.v24BindingCoherent -eq 1) `
        "v24BindingCoherent is false at record $($row.recordIndex)."

    if ($row.classification -eq "SCHEDULE_TRANSACTION_MISMATCH") {
        $scheduleMismatchRowCount++
    }

    if ($row.classification -eq "CANONICAL_PUBLICATION_IDENTITY_INVALID") {
        $canonicalInvalidRowCount++
    }

    if ($row.classification -eq "NO_CANONICAL_PUBLICATION_OBSERVED") {
        $canonicalUnobservedRowCount++
    }
}

if ($kv["decision"] -eq "TRANSACTION_COHERENT") {
    Assert-True ($scheduleFindingTotal -eq 0) `
        "Coherent decision has schedule mismatch findings."
    Assert-True ($canonicalPublicationMissCount -eq 0) `
        "Coherent decision has canonical publication misses."

    foreach ($row in $rows) {
        Assert-True ([int]$row.scheduleIdentityCoherent -eq 1) `
            "scheduleIdentityCoherent is false at record $($row.recordIndex)."
        Assert-True ([int]$row.canonicalPublicationCoherent -eq 1) `
            "canonicalPublicationCoherent is false at record $($row.recordIndex)."
    }
}
elseif ($kv["decision"] -eq "SCHEDULE_TRANSACTION_MISMATCH") {
    Assert-True ($scheduleFindingTotal -gt 0) `
        "Schedule mismatch decision lacks a schedule finding counter."
    Assert-True ($scheduleMismatchRowCount -gt 0) `
        "Schedule mismatch decision lacks a classified CSV row."
}
elseif ($kv["decision"] -eq "PUBLISHED_TRANSACTION_COHERENT_PARTIAL_COVERAGE") {
    Assert-True ($scheduleFindingTotal -eq 0) `
        "Published-subset decision has schedule findings."
    Assert-True ($canonicalPublicationMissCount -gt 0) `
        "Published-subset decision lacks unobserved coverage."
    Assert-True ($canonicalPublicationIdentityInvalidCount -eq 0) `
        "Published-subset decision has invalid publication identity."
    Assert-True ($canonicalPublicationBoundAnomalousCount -ge 3) `
        "Published-subset decision lacks three anomalous published pairs."
    Assert-True ($canonicalUnobservedRowCount -eq $canonicalPublicationMissCount) `
        "Unobserved row count differs from the TXT coverage count."
}
elseif ($kv["decision"] -eq "INSUFFICIENT_PUBLISHED_ANOMALOUS_DATA") {
    Assert-True ($scheduleFindingTotal -eq 0) `
        "Insufficient-data decision has schedule findings."
    Assert-True ($canonicalPublicationIdentityInvalidCount -eq 0) `
        "Insufficient-data decision has invalid publication identity."
    Assert-True ($canonicalPublicationBoundAnomalousCount -lt 3) `
        "Insufficient-data decision has enough anomalous published pairs."
}
elseif ($kv["decision"] -eq "CANONICAL_PUBLICATION_IDENTITY_INVALID") {
    Assert-True ($canonicalPublicationIdentityInvalidCount -gt 0) `
        "Invalid-publication decision lacks an identity-invalid counter."
    Assert-True ($canonicalInvalidRowCount -gt 0) `
        "Invalid-publication decision lacks a classified CSV row."
}

Write-Host "RUNTIME_VALIDATION_PASS"
Write-Host ("decision=" + $kv["decision"])
Write-Host ("completedTraceCount=" + $kv["completedTraceCount"])
Write-Host ("dependencyPairCount=" + $kv["dependencyPairCount"])
Write-Host ("anomalousPairCount=" + $kv["anomalousPairCount"])
