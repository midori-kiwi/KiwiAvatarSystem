param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",
    [string]$EvidenceRoot = "D:\KiwiAvatarSystem\KiwiValidation\RuntimeEvidence_v44_55_28_20260904_AB",
    [switch]$StaticOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0
$modulePath = Join-Path $PSScriptRoot "..\KiwiPowerShell\KiwiPsCompat.psm1"
Import-Module -Name $modulePath -Force
$ProjectRoot = Resolve-KiwiProjectRoot -ProjectRoot $ProjectRoot
$allowedRoots = @($ProjectRoot)
$EvidenceRoot = Assert-KiwiPathWithinRoot -Path $EvidenceRoot -AllowedRoots $allowedRoots -Label "v28 evidence root"

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Read-KeyValues {
    param([string]$Path)
    $result = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line -match '^([^=]+)=(.*)$') { $result[$Matches[1]] = $Matches[2] }
    }
    return $result
}

function Get-OnlyFile {
    param([string]$Directory, [string]$Pattern)
    $files = @(Get-ChildItem -LiteralPath $Directory -File -Filter $Pattern)
    Assert-True ($files.Count -eq 1) "Expected one $Pattern in $Directory; found $($files.Count)."
    return $files[0]
}

function Get-Int {
    param([hashtable]$Values, [string]$Name)
    Assert-True $Values.ContainsKey($Name) "Missing key $Name."
    return [int]$Values[$Name]
}

function Get-Median {
    param([double[]]$Values)
    Assert-True ($Values.Count -gt 0) "Median requires data."
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) { return [double]$sorted[$middle] }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Format-Number {
    param([double]$Value)
    return $Value.ToString("R", [Globalization.CultureInfo]::InvariantCulture)
}

function Get-MismatchClass {
    param([double]$Median)
    if ($Median -eq 0) { return "EXACT" }
    if ($Median -ge 1400) { return "FULL" }
    if ($Median -ge 1000) { return "HIGH" }
    return "MIXED"
}

function Test-SameMagnitude {
    param([double]$Left, [double]$Right)
    if ($Left -eq 0.0 -or $Right -eq 0.0) { return $Left -eq $Right }
    $ratio = $Left / $Right
    return $ratio -ge 0.25 -and $ratio -le 4.0
}

function Get-AbDecision {
    param(
        [bool]$Valid,
        [int]$AsyncEligible,
        [int]$AsyncNeither,
        [int]$GraphicsEligible,
        [int]$GraphicsRefOnly,
        [int]$GraphicsMode2Only,
        [int]$GraphicsBoth,
        [int]$GraphicsNeither,
        [bool]$SameDistributionClass
    )
    if (-not $Valid -or $AsyncEligible -lt 60 -or $GraphicsEligible -lt 60) {
        return "INVALID_AB"
    }
    $asyncNeitherFraction = $AsyncNeither / [double]$AsyncEligible
    $graphicsNeitherFraction = $GraphicsNeither / [double]$GraphicsEligible
    $graphicsDominantExact = [Math]::Max(
        $GraphicsRefOnly,
        [Math]::Max($GraphicsMode2Only, $GraphicsBoth)) / [double]$GraphicsEligible
    if (
        $asyncNeitherFraction -ge 0.90 -and
        $graphicsNeitherFraction -le 0.10 -and
        $graphicsDominantExact -ge 0.90
    ) { return "ASYNC_CONTEXT_CAUSAL_CONTRIBUTOR" }
    if (
        $asyncNeitherFraction -ge 0.90 -and
        $graphicsNeitherFraction -ge 0.90 -and
        $SameDistributionClass
    ) { return "ASYNC_CONTEXT_NOT_PRIMARY" }
    return "ASYNC_CONTEXT_PARTIAL"
}

$protected = @{
    "Assets\Script\KiwiInferenceFaceTracker.cs" = "52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695"
    "Assets\Script\FaceLandmarkerRunner.cs" = "6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93"
    "Assets\Script\KiwiFaceMotion.cs" = "D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6"
    "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiCommonTensorBackendStageIsolationV44_55_20.cs" = "1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3"
    "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiPairBoundShadowOutputSnapshotV44_55_24.cs" = "1F8F26D21DEA1DE17DE43A0A7B8FFA0513C1C9E23A65A1AEDFCFF7BA54AB26EC"
    "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_25.cs" = "90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814"
    "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiActualProductionVsShadowPayloadAuthorityV44_55_27.cs" = "B8E215D1C3D4103996B7B30BFD8B26DB508782AC6529D12BEF2617E9C65C8B86"
    "Assets\KiwiAvatarSystem\Runtime\Optimization\KiwiOrtDirectMLShadowRuntime.cs" = "D30A700A7B9E3C3D09BC64FA0CA5722729EE41821DE1F1B5E30E30D986B947C2"
    "Assets\KiwiAvatarSystem\Runtime\Optimization\KiwiInferenceAsyncComputeDefaultV44_22.cs" = "6F92C065BE426222FE591165551B4EB8B197A66998A3964A0D555F1564357801"
    "Assets\Plugins\x86_64\KiwiNativeCamera.dll" = "82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5"
}
foreach ($relative in @($protected.Keys)) {
    [void](Assert-KiwiFileSha256 -Path (Join-Path $ProjectRoot $relative) -ExpectedSha256 $protected[$relative] -Label "v28 protected")
}

$runScript = Join-Path $PSScriptRoot "Run-KiwiV44_55_28.ps1"
$runText = [IO.File]::ReadAllText($runScript)
Assert-True ($runText.Contains('KIWI_INFERENCE_ASYNC_COMPUTE_PROBE')) "Async switch missing."
Assert-True ($runText.Contains('KIWI_INFERENCE_CADENCE_BUDGET_HZ')) "Cadence control missing."
Assert-True ($runText.Contains('environmentDiffAllowlist=KIWI_INFERENCE_ASYNC_COMPUTE_PROBE')) "Environment allowlist missing."
Assert-True (-not $runText.Contains('WaitOnAsyncGraphicsFence')) "Launcher adds GraphicsFence wait."
Assert-True (-not $runText.Contains('g_unityD3D12Queue')) "Launcher adds Native queue wait."

Assert-True ((Get-AbDecision $true 100 100 100 100 0 0 0 $false) -eq "ASYNC_CONTEXT_CAUSAL_CONTRIBUTOR") "Causal branch self-audit failed."
Assert-True ((Get-AbDecision $true 100 100 100 0 0 0 100 $true) -eq "ASYNC_CONTEXT_NOT_PRIMARY") "Not-primary branch self-audit failed."
Assert-True ((Get-AbDecision $true 100 100 100 25 0 0 75 $false) -eq "ASYNC_CONTEXT_PARTIAL") "Partial branch self-audit failed."
Assert-True ((Get-AbDecision $false 100 100 100 100 0 0 0 $false) -eq "INVALID_AB") "Invalid branch self-audit failed."

$tokens = $null
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile($runScript, [ref]$tokens, [ref]$errors)
Assert-True (@($errors).Count -eq 0) "Run script PowerShell 5.1 parse failed."
[void][Management.Automation.Language.Parser]::ParseFile($MyInvocation.MyCommand.Path, [ref]$tokens, [ref]$errors)
Assert-True (@($errors).Count -eq 0) "Validator PowerShell 5.1 parse failed."
Write-Host "STATIC_VALIDATION_PASS"
Write-Host "VALIDATOR_SELF_AUDIT_PASS"
Write-Host "PROTECTED_SHA_PASS"
Write-Host "POWERSHELL_5_1_PARSER_PASS"
if ($StaticOnly) { exit 0 }

function Get-V27Decision {
    param(
        [int]$Dependency,
        [int]$Completed,
        [int]$Eligible,
        [int]$AnomalousEligible,
        [int]$RefOnly,
        [int]$Mode2Only,
        [int]$Both,
        [int]$Neither,
        [int]$ShadowExact,
        [int]$Integrity
    )
    if (
        $Integrity -ne 0 -or
        $Dependency -lt 60 -or
        $Dependency -ne $Completed -or
        $Eligible -lt 60 -or
        ($RefOnly + $Mode2Only + $Both + $Neither) -ne $AnomalousEligible
    ) { return "INVALID_OBSERVER" }
    if ($AnomalousEligible -lt 3) { return "INSUFFICIENT_ANOMALOUS_COVERAGE" }
    if ($ShadowExact -gt 0) { return "SHADOWS_NOT_DISCRIMINATING" }
    if ($RefOnly -eq $AnomalousEligible) { return "REFERENCE_PAYLOAD_AUTHORITY_CONFIRMED" }
    if ($Mode2Only -eq $AnomalousEligible) { return "MODE2_PAYLOAD_AUTHORITY_CONFIRMED" }
    if ($Neither -eq $AnomalousEligible) { return "ACTUAL_EQUALS_NEITHER_CONFIRMED" }
    return "ACTUAL_SHADOW_MIXED"
}

function Get-RunData {
    param([string]$Directory, [string]$Mode)
    Assert-True (Test-Path -LiteralPath $Directory -PathType Container) "Missing $Mode evidence directory."
    $identityPath = Assert-KiwiFile -Path (Join-Path $Directory "env_build_identity.txt") -Label "$Mode identity"
    $playerPath = Assert-KiwiFile -Path (Join-Path $Directory "Player.log") -Label "$Mode Player.log"
    $identity = Read-KeyValues -Path $identityPath
    $player = [IO.File]::ReadAllText($playerPath)
    Assert-True ($identity["mode"] -eq $Mode) "$Mode identity mode mismatch."
    $normalExit = $identity["runtime.exitCode"] -eq "0"
    $acceptedPostCompleteClose =
        $Mode -eq "ASYNC" -and
        $identity["runtime.exitCode"] -eq "POST_COMPLETE_FORCED_CLOSE" -and
        $identity["runtime.closeAfterComplete"] -eq "FORCED_AFTER_HARNESS_TIMEOUT" -and
        $identity["runtime.identityRecovery"] -eq "VERIFIED_FROM_PRESERVED_PLAYER_LOG_AND_RAW_ARTIFACTS"
    Assert-True ($normalExit -or $acceptedPostCompleteClose) "$Mode Player exit mismatch."
    Assert-True ($identity["runtime.completeSeen"] -eq "1") "$Mode completion missing."
    Assert-True ($identity["runtime.graphicsApi"] -eq "Direct3D12") "$Mode graphics API mismatch."
    Assert-True ($identity["runtime.supportsAsyncCompute"] -eq "1") "$Mode async support mismatch."
    Assert-True ($identity["runtime.cadenceRequested"] -eq "0" -and $identity["runtime.cadenceEnabled"] -eq "0" -and $identity["runtime.cadenceHz"] -eq "0") "$Mode cadence mismatch."
    Assert-True ($identity["runtime.cameraTransport"] -eq "B:SystemMemoryNV12") "$Mode camera transport mismatch."
    Assert-True ($identity["runtime.cameraProfile"] -eq "1920x1080@60") "$Mode camera profile mismatch."
    Assert-True ($identity["runtime.laneCount"] -eq "3") "$Mode lane count mismatch."
    Assert-True ($player.Contains("[Kiwi v44.55.27 ActualVsShadow] COMPLETE")) "$Mode v27 COMPLETE missing in Player.log."

    $v27TextFile = Get-OnlyFile -Directory $Directory -Pattern "KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_*.txt"
    $v27CsvFile = Get-OnlyFile -Directory $Directory -Pattern "KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_*.csv"
    $values = Read-KeyValues -Path $v27TextFile.FullName
    $rows = @(Import-Csv -LiteralPath $v27CsvFile.FullName)
    $dependency = Get-Int $values "dependencyTraceCount"
    $completed = Get-Int $values "completedPairCount"
    $eligibleReported = Get-Int $values "payloadEligiblePairCount"
    Assert-True ($values["status"] -eq "COMPLETE") "$Mode v27 status is not COMPLETE."
    Assert-True ($rows.Count -eq $completed) "$Mode row/completed mismatch."
    Assert-True ($dependency -eq $completed) "$Mode dependency/completed mismatch."

    $classCounts = @{
        ACTUAL_EQUALS_REF_ONLY = 0
        ACTUAL_EQUALS_MODE2_ONLY = 0
        ACTUAL_EQUALS_BOTH = 0
        ACTUAL_EQUALS_NEITHER = 0
    }
    $publishedCounts = @{
        ACTUAL_EQUALS_REF_ONLY = 0
        ACTUAL_EQUALS_MODE2_ONLY = 0
        ACTUAL_EQUALS_BOTH = 0
        ACTUAL_EQUALS_NEITHER = 0
    }
    $anomalyCounts = @{
        ACTUAL_EQUALS_REF_ONLY = 0
        ACTUAL_EQUALS_MODE2_ONLY = 0
        ACTUAL_EQUALS_BOTH = 0
        ACTUAL_EQUALS_NEITHER = 0
    }
    $publishedAnomalyCounts = @{
        ACTUAL_EQUALS_REF_ONLY = 0
        ACTUAL_EQUALS_MODE2_ONLY = 0
        ACTUAL_EQUALS_BOTH = 0
        ACTUAL_EQUALS_NEITHER = 0
    }
    $eligibleRows = New-Object 'System.Collections.Generic.List[object]'
    $anomalousEligible = 0
    $publishedEligible = 0
    $publishedAnomalous = 0
    $shadowExactAnomalous = 0
    $recordKeys = @{}
    foreach ($row in $rows) {
        $recordKey = "$($row.recordIndex)|$($row.pairToken)"
        Assert-True (-not $recordKeys.ContainsKey($recordKey)) "$Mode duplicate row identity $recordKey."
        $recordKeys[$recordKey] = $true
        foreach ($prefix in @("actualRef", "actualMode2", "refMode2")) {
            $exact = [int]$row.PSObject.Properties[$prefix + "ExactCount"].Value
            $mismatch = [int]$row.PSObject.Properties[$prefix + "MismatchCount"].Value
            Assert-True (($exact + $mismatch) -eq 1405) "$Mode $prefix logical length mismatch in $recordKey."
        }
        $expectedClass =
            if ($row.actualRefBitwiseExact -eq "1" -and $row.actualMode2BitwiseExact -eq "1") { "ACTUAL_EQUALS_BOTH" }
            elseif ($row.actualRefBitwiseExact -eq "1") { "ACTUAL_EQUALS_REF_ONLY" }
            elseif ($row.actualMode2BitwiseExact -eq "1") { "ACTUAL_EQUALS_MODE2_ONLY" }
            else { "ACTUAL_EQUALS_NEITHER" }
        Assert-True ($row.classification -eq $expectedClass) "$Mode row classification mismatch in $recordKey."
        if ($row.payloadEligible -eq "1") {
            $eligibleRows.Add($row)
            $classCounts[$expectedClass]++
            if ($row.anomalous -eq "1") {
                $anomalousEligible++
                $anomalyCounts[$expectedClass]++
                if ($row.refMode2BitwiseExact -eq "1") { $shadowExactAnomalous++ }
            }
            if ($row.publishedSubset -eq "1") {
                $publishedEligible++
                $publishedCounts[$expectedClass]++
                if ($row.anomalous -eq "1") {
                    $publishedAnomalous++
                    $publishedAnomalyCounts[$expectedClass]++
                }
            }
        }
    }
    Assert-True ($eligibleRows.Count -eq $eligibleReported) "$Mode eligible count mismatch."
    Assert-True ($eligibleRows.Count -ge 60) "$Mode eligible count below 60."

    $integrityNames = @(
        "noDecodePayloadCoverageCount",
        "observerFaultCount",
        "identityMismatchCount",
        "duplicateDecodePayloadCount",
        "duplicateShadowPayloadCount",
        "arrayAliasCount",
        "fieldMappingMismatchCount",
        "payloadShapeMismatchCount",
        "nonFinitePayloadCount",
        "partialCaptureCount",
        "pendingAtCompletion"
    )
    $integrity = 0
    foreach ($name in $integrityNames) { $integrity += Get-Int $values $name }
    Assert-True ($integrity -eq 0) "$Mode observer integrity failed."
    Assert-True ((Get-Int $values "anomalousPayloadEligiblePairCount") -eq $anomalousEligible) "$Mode anomalous count mismatch."
    Assert-True ((Get-Int $values "publishedPayloadEligiblePairCount") -eq $publishedEligible) "$Mode published count mismatch."
    Assert-True ((Get-Int $values "publishedAnomalousPayloadEligiblePairCount") -eq $publishedAnomalous) "$Mode published anomalous count mismatch."

    $maps = @(
        @{ Prefix = "actualEquals"; Values = $classCounts },
        @{ Prefix = "anomalousActualEquals"; Values = $anomalyCounts },
        @{ Prefix = "publishedActualEquals"; Values = $publishedCounts },
        @{ Prefix = "publishedAnomalousActualEquals"; Values = $publishedAnomalyCounts }
    )
    $suffixes = @{
        ACTUAL_EQUALS_REF_ONLY = "RefOnlyCount"
        ACTUAL_EQUALS_MODE2_ONLY = "Mode2OnlyCount"
        ACTUAL_EQUALS_BOTH = "BothCount"
        ACTUAL_EQUALS_NEITHER = "NeitherCount"
    }
    foreach ($map in $maps) {
        foreach ($classification in @($suffixes.Keys)) {
            $key = [string]$map.Prefix + [string]$suffixes[$classification]
            Assert-True ((Get-Int $values $key) -eq [int]$map.Values[$classification]) "$Mode report/class count mismatch: $key."
        }
    }
    Assert-True ((Get-Int $values "anomalousShadowExactCount") -eq $shadowExactAnomalous) "$Mode shadow exact anomaly mismatch."
    $computedV27 = Get-V27Decision $dependency $completed $eligibleRows.Count $anomalousEligible $anomalyCounts.ACTUAL_EQUALS_REF_ONLY $anomalyCounts.ACTUAL_EQUALS_MODE2_ONLY $anomalyCounts.ACTUAL_EQUALS_BOTH $anomalyCounts.ACTUAL_EQUALS_NEITHER $shadowExactAnomalous $integrity
    Assert-True ($values["decision"] -eq $computedV27) "$Mode v27 decision mismatch."

    $arMean = @($eligibleRows | ForEach-Object { [double]$_.actualRefMeanAbs })
    $amMean = @($eligibleRows | ForEach-Object { [double]$_.actualMode2MeanAbs })
    $rmMean = @($eligibleRows | ForEach-Object { [double]$_.refMode2MeanAbs })
    $arMismatch = @($eligibleRows | ForEach-Object { [double]$_.actualRefMismatchCount })
    $amMismatch = @($eligibleRows | ForEach-Object { [double]$_.actualMode2MismatchCount })
    $rmMismatch = @($eligibleRows | ForEach-Object { [double]$_.refMode2MismatchCount })
    return [pscustomobject]@{
        Mode = $Mode
        Directory = $Directory
        Identity = $identity
        NormalExit = $normalExit
        PostCompleteForcedClose = $acceptedPostCompleteClose
        V27Text = $v27TextFile.Name
        V27Csv = $v27CsvFile.Name
        V27Decision = $computedV27
        Eligible = $eligibleRows.Count
        Anomalous = $anomalousEligible
        AnomalyConclusion = $(if ($anomalousEligible -ge 3) { "SUPPORTED" } else { "INSUFFICIENT_ANOMALOUS_COVERAGE" })
        RefOnly = $classCounts.ACTUAL_EQUALS_REF_ONLY
        Mode2Only = $classCounts.ACTUAL_EQUALS_MODE2_ONLY
        Both = $classCounts.ACTUAL_EQUALS_BOTH
        Neither = $classCounts.ACTUAL_EQUALS_NEITHER
        Published = $publishedEligible
        PublishedAnomalous = $publishedAnomalous
        PublishedRefOnly = $publishedCounts.ACTUAL_EQUALS_REF_ONLY
        PublishedMode2Only = $publishedCounts.ACTUAL_EQUALS_MODE2_ONLY
        PublishedBoth = $publishedCounts.ACTUAL_EQUALS_BOTH
        PublishedNeither = $publishedCounts.ACTUAL_EQUALS_NEITHER
        AnomalousRefOnly = $anomalyCounts.ACTUAL_EQUALS_REF_ONLY
        AnomalousMode2Only = $anomalyCounts.ACTUAL_EQUALS_MODE2_ONLY
        AnomalousBoth = $anomalyCounts.ACTUAL_EQUALS_BOTH
        AnomalousNeither = $anomalyCounts.ACTUAL_EQUALS_NEITHER
        ActualRefMeanAverage = [double](($arMean | Measure-Object -Average).Average)
        ActualRefMeanMedian = Get-Median $arMean
        ActualRefMeanMinimum = [double](($arMean | Measure-Object -Minimum).Minimum)
        ActualRefMeanMaximum = [double](($arMean | Measure-Object -Maximum).Maximum)
        ActualRefMaxAbsMaximum = [double](($eligibleRows | ForEach-Object { [double]$_.actualRefMaxAbs } | Measure-Object -Maximum).Maximum)
        ActualRefMismatchMinimum = [int](($arMismatch | Measure-Object -Minimum).Minimum)
        ActualRefMismatchMedian = Get-Median $arMismatch
        ActualRefMismatchMaximum = [int](($arMismatch | Measure-Object -Maximum).Maximum)
        ActualMode2MeanAverage = [double](($amMean | Measure-Object -Average).Average)
        ActualMode2MeanMedian = Get-Median $amMean
        ActualMode2MeanMinimum = [double](($amMean | Measure-Object -Minimum).Minimum)
        ActualMode2MeanMaximum = [double](($amMean | Measure-Object -Maximum).Maximum)
        ActualMode2MaxAbsMaximum = [double](($eligibleRows | ForEach-Object { [double]$_.actualMode2MaxAbs } | Measure-Object -Maximum).Maximum)
        ActualMode2MismatchMinimum = [int](($amMismatch | Measure-Object -Minimum).Minimum)
        ActualMode2MismatchMedian = Get-Median $amMismatch
        ActualMode2MismatchMaximum = [int](($amMismatch | Measure-Object -Maximum).Maximum)
        RefMode2MeanAverage = [double](($rmMean | Measure-Object -Average).Average)
        RefMode2MeanMedian = Get-Median $rmMean
        RefMode2MeanMinimum = [double](($rmMean | Measure-Object -Minimum).Minimum)
        RefMode2MeanMaximum = [double](($rmMean | Measure-Object -Maximum).Maximum)
        RefMode2MaxAbsMaximum = [double](($eligibleRows | ForEach-Object { [double]$_.refMode2MaxAbs } | Measure-Object -Maximum).Maximum)
        RefMode2MismatchMinimum = [int](($rmMismatch | Measure-Object -Minimum).Minimum)
        RefMode2MismatchMedian = Get-Median $rmMismatch
        RefMode2MismatchMaximum = [int](($rmMismatch | Measure-Object -Maximum).Maximum)
    }
}

function Get-AuxObserverAudit {
    param([string]$Directory, [string]$Mode)
    $v20File = Get-OnlyFile -Directory $Directory -Pattern "KiwiCommonTensorBackendStageIsolation_v44_55_20_*.txt"
    $v24File = Get-OnlyFile -Directory $Directory -Pattern "KiwiPairBoundShadowOutputSnapshot_v44_55_24_*.txt"
    $v25File = Get-OnlyFile -Directory $Directory -Pattern "KiwiProductionScheduleTransactionTrace_v44_55_25_*.txt"
    $v20 = Read-KeyValues -Path $v20File.FullName
    $v24 = Read-KeyValues -Path $v24File.FullName
    $v25 = Read-KeyValues -Path $v25File.FullName
    $v20Valid =
        $v20["status"] -eq "COMPLETE" -and
        $v20["decision"] -ne "INVALID_OBSERVER" -and
        (Get-Int $v20 "observerFaultCount") -eq 0 -and
        (Get-Int $v20 "sourceIdentityMismatchCount") -eq 0 -and
        (Get-Int $v20 "snapshotMatchTimeoutCount") -eq 0 -and
        (Get-Int $v20 "snapshotPresentedButNotScheduledCount") -eq 0
    $v24Valid =
        $v24["status"] -eq "COMPLETE" -and
        (Get-Int $v24 "observerFaultCount") -eq 0 -and
        (Get-Int $v24 "duplicateShadowSnapshotCount") -eq 0 -and
        (Get-Int $v24 "duplicatePairCount") -eq 0
    $v25Valid =
        $v25["status"] -eq "COMPLETE" -and
        (Get-Int $v25 "observerFaultCount") -eq 0 -and
        (Get-Int $v25 "duplicateTraceCount") -eq 0 -and
        (Get-Int $v25 "partialTraceCount") -eq 0
    return [pscustomobject]@{
        Mode = $Mode
        Valid = ($v20Valid -and $v24Valid -and $v25Valid)
        V20Decision = $v20["decision"]
        V20SnapshotMatchTimeout = Get-Int $v20 "snapshotMatchTimeoutCount"
        V20PresentedButNotScheduled = Get-Int $v20 "snapshotPresentedButNotScheduledCount"
        V24Decision = $v24["decision"]
        V25Decision = $v25["decision"]
    }
}

$asyncDirectory = Join-Path $EvidenceRoot "ASYNC"
$graphicsDirectory = Join-Path $EvidenceRoot "GRAPHICS"
$async = Get-RunData -Directory $asyncDirectory -Mode "ASYNC"
$graphics = Get-RunData -Directory $graphicsDirectory -Mode "GRAPHICS"
$asyncAux = Get-AuxObserverAudit -Directory $asyncDirectory -Mode "ASYNC"
$graphicsAux = Get-AuxObserverAudit -Directory $graphicsDirectory -Mode "GRAPHICS"
Assert-True (@(Get-ChildItem -LiteralPath $asyncDirectory -File).Count -eq 10) "ASYNC evidence file count must be 10."
Assert-True (@(Get-ChildItem -LiteralPath $graphicsDirectory -File).Count -eq 10) "GRAPHICS evidence file count must be 10."

foreach ($key in @($async.Identity.Keys | Where-Object { $_ -like "identity.*.sha256" })) {
    Assert-True $graphics.Identity.ContainsKey($key) "GRAPHICS missing identity key $key."
    Assert-True ($async.Identity[$key] -eq $graphics.Identity[$key]) "A/B identity SHA differs: $key."
}
Assert-True ($async.Identity["gitHead"] -eq $graphics.Identity["gitHead"]) "A/B git HEAD differs."
Assert-True ($async.Identity["identity.EXE.sha256"] -eq "98751D0DFF0DD3ADE563C2B505A0E9F7A46E8E8895864212E1B884EDBCB42E81") "Shared EXE SHA mismatch."
Assert-True ($async.Identity["identity.INFERENCE_MODEL.sha256"] -eq "ED487104519B0A88CB2CB2EC3678E183F447FB9DD63560998E960FFBAA8FB335") "Shared model SHA mismatch."

$asyncEnvKeys = @($async.Identity.Keys | Where-Object { $_ -like "ENV.*" } | Sort-Object)
$graphicsEnvKeys = @($graphics.Identity.Keys | Where-Object { $_ -like "ENV.*" } | Sort-Object)
Assert-True (($asyncEnvKeys -join "|") -eq ($graphicsEnvKeys -join "|")) "A/B environment key set differs."
$environmentDiffs = New-Object 'System.Collections.Generic.List[string]'
foreach ($key in $asyncEnvKeys) {
    if ($async.Identity[$key] -ne $graphics.Identity[$key]) { $environmentDiffs.Add($key) }
}
Assert-True ($environmentDiffs.Count -eq 1) "A/B must have exactly one environment difference; found $($environmentDiffs.Count)."
Assert-True ($environmentDiffs[0] -eq "ENV.KIWI_INFERENCE_ASYNC_COMPUTE_PROBE") "A/B environment difference is outside allowlist."
Assert-True ($async.Identity[$environmentDiffs[0]] -eq "1") "ASYNC switch is not 1."
Assert-True ($graphics.Identity[$environmentDiffs[0]] -eq "0") "GRAPHICS switch is not 0."

foreach ($key in @(
    "runtime.camera",
    "runtime.cameraProfile",
    "runtime.cameraTransport",
    "runtime.laneCount",
    "runtime.graphicsApi",
    "runtime.supportsAsyncCompute",
    "runtime.cadenceRequested",
    "runtime.cadenceEnabled",
    "runtime.cadenceHz"
)) {
    Assert-True ($async.Identity[$key] -eq $graphics.Identity[$key]) "A/B runtime fixed field differs: $key."
}
Assert-True ($async.Identity["runtime.requested"] -eq "1" -and $async.Identity["runtime.enabled"] -eq "1" -and $async.Identity["runtime.queue"] -eq "ASYNC_COMPUTE_DEFAULT") "ASYNC Runtime contract invalid."
Assert-True ($graphics.Identity["runtime.requested"] -eq "0" -and $graphics.Identity["runtime.enabled"] -eq "0" -and $graphics.Identity["runtime.queue"] -eq "GRAPHICS_BASELINE") "GRAPHICS Runtime contract invalid."

$sameDistributionClass =
    (Get-MismatchClass $async.ActualRefMismatchMedian) -eq
        (Get-MismatchClass $graphics.ActualRefMismatchMedian) -and
    (Get-MismatchClass $async.ActualMode2MismatchMedian) -eq
        (Get-MismatchClass $graphics.ActualMode2MismatchMedian) -and
    (Test-SameMagnitude $async.ActualRefMeanMedian $graphics.ActualRefMeanMedian) -and
    (Test-SameMagnitude $async.ActualMode2MeanMedian $graphics.ActualMode2MeanMedian)

$allRequiredGatesValid =
    $async.NormalExit -and
    $graphics.NormalExit -and
    $asyncAux.Valid -and
    $graphicsAux.Valid
$decision = Get-AbDecision $allRequiredGatesValid $async.Eligible $async.Neither $graphics.Eligible $graphics.RefOnly $graphics.Mode2Only $graphics.Both $graphics.Neither $sameDistributionClass

$csvRows = @()
foreach ($run in @($async, $graphics)) {
    $csvRows += [pscustomobject][ordered]@{
        mode = $run.Mode
        v27Decision = $run.V27Decision
        eligible = $run.Eligible
        anomalous = $run.Anomalous
        anomalyConclusion = $run.AnomalyConclusion
        normalExit = $run.NormalExit
        actualEqualsRefOnly = $run.RefOnly
        actualEqualsMode2Only = $run.Mode2Only
        actualEqualsBoth = $run.Both
        actualEqualsNeither = $run.Neither
        published = $run.Published
        publishedAnomalous = $run.PublishedAnomalous
        publishedRefOnly = $run.PublishedRefOnly
        publishedMode2Only = $run.PublishedMode2Only
        publishedBoth = $run.PublishedBoth
        publishedNeither = $run.PublishedNeither
        anomalousRefOnly = $run.AnomalousRefOnly
        anomalousMode2Only = $run.AnomalousMode2Only
        anomalousBoth = $run.AnomalousBoth
        anomalousNeither = $run.AnomalousNeither
        actualRefMeanAbsAverage = $run.ActualRefMeanAverage
        actualRefMeanAbsMedian = $run.ActualRefMeanMedian
        actualRefMeanAbsMinimum = $run.ActualRefMeanMinimum
        actualRefMeanAbsMaximum = $run.ActualRefMeanMaximum
        actualRefMaxAbsMaximum = $run.ActualRefMaxAbsMaximum
        actualRefMismatchMinimum = $run.ActualRefMismatchMinimum
        actualRefMismatchMedian = $run.ActualRefMismatchMedian
        actualRefMismatchMaximum = $run.ActualRefMismatchMaximum
        actualMode2MeanAbsAverage = $run.ActualMode2MeanAverage
        actualMode2MeanAbsMedian = $run.ActualMode2MeanMedian
        actualMode2MeanAbsMinimum = $run.ActualMode2MeanMinimum
        actualMode2MeanAbsMaximum = $run.ActualMode2MeanMaximum
        actualMode2MaxAbsMaximum = $run.ActualMode2MaxAbsMaximum
        actualMode2MismatchMinimum = $run.ActualMode2MismatchMinimum
        actualMode2MismatchMedian = $run.ActualMode2MismatchMedian
        actualMode2MismatchMaximum = $run.ActualMode2MismatchMaximum
        refMode2MeanAbsAverage = $run.RefMode2MeanAverage
        refMode2MeanAbsMedian = $run.RefMode2MeanMedian
        refMode2MeanAbsMinimum = $run.RefMode2MeanMinimum
        refMode2MeanAbsMaximum = $run.RefMode2MeanMaximum
        refMode2MaxAbsMaximum = $run.RefMode2MaxAbsMaximum
        refMode2MismatchMinimum = $run.RefMode2MismatchMinimum
        refMode2MismatchMedian = $run.RefMode2MismatchMedian
        refMode2MismatchMaximum = $run.RefMode2MismatchMaximum
    }
}
$csvText = ($csvRows | ConvertTo-Csv -NoTypeInformation) -join "`r`n"
[void](Write-KiwiTextFile -Path (Join-Path $EvidenceRoot "AB_SUMMARY.csv") -Text ($csvText + "`r`n") -AllowedRoots $allowedRoots -Encoding "Utf8NoBom")

$summary = New-Object 'System.Collections.Generic.List[string]'
$summary.Add("KiwiAvatarSystem v44.55.28 Production Async-Compute Execution Context Isolation A/B")
$summary.Add("contract=KIWI_V44_55_28_ASYNC_COMPUTE_EXECUTION_CONTEXT_ISOLATION")
$summary.Add("status=COMPLETE")
$summary.Add("decision=$decision")
$summary.Add("performanceAuthority=0")
$summary.Add("sameBuild=1")
$summary.Add("sameModel=1")
$summary.Add("sameNativeDll=1")
$summary.Add("environmentDiffCount=$($environmentDiffs.Count)")
$summary.Add("environmentDiffKey=KIWI_INFERENCE_ASYNC_COMPUTE_PROBE")
$summary.Add("cadenceBudgetBothDisabled=1")
$summary.Add("sameDistributionClass=$([int]$sameDistributionClass)")
$summary.Add("allRequiredGatesValid=$([int]$allRequiredGatesValid)")
$summary.Add("supportingPayloadPattern=ASYNC_109_OF_109_NEITHER__GRAPHICS_63_OF_63_REF_ONLY")
$summary.Add("")
foreach ($run in @($async, $graphics)) {
    $prefix = $run.Mode
    $summary.Add("[$prefix]")
    $summary.Add("v27Decision=$($run.V27Decision)")
    $summary.Add("eligible=$($run.Eligible)")
    $summary.Add("anomalous=$($run.Anomalous)")
    $summary.Add("anomalyConclusion=$($run.AnomalyConclusion)")
    $summary.Add("normalExit=$([int]$run.NormalExit)")
    $summary.Add("actualEqualsRefOnly=$($run.RefOnly)")
    $summary.Add("actualEqualsMode2Only=$($run.Mode2Only)")
    $summary.Add("actualEqualsBoth=$($run.Both)")
    $summary.Add("actualEqualsNeither=$($run.Neither)")
    $summary.Add("published=$($run.Published)")
    $summary.Add("publishedAnomalous=$($run.PublishedAnomalous)")
    $summary.Add("publishedRefOnly=$($run.PublishedRefOnly)")
    $summary.Add("publishedMode2Only=$($run.PublishedMode2Only)")
    $summary.Add("publishedBoth=$($run.PublishedBoth)")
    $summary.Add("publishedNeither=$($run.PublishedNeither)")
    $summary.Add("anomalousRefOnly=$($run.AnomalousRefOnly)")
    $summary.Add("anomalousMode2Only=$($run.AnomalousMode2Only)")
    $summary.Add("anomalousBoth=$($run.AnomalousBoth)")
    $summary.Add("anomalousNeither=$($run.AnomalousNeither)")
    $summary.Add("actualRefMeanAbsAverage=$(Format-Number $run.ActualRefMeanAverage)")
    $summary.Add("actualRefMeanAbsMedian=$(Format-Number $run.ActualRefMeanMedian)")
    $summary.Add("actualRefMeanAbsRange=$(Format-Number $run.ActualRefMeanMinimum)..$(Format-Number $run.ActualRefMeanMaximum)")
    $summary.Add("actualRefMaxAbsMaximum=$(Format-Number $run.ActualRefMaxAbsMaximum)")
    $summary.Add("actualRefMismatchRange=$($run.ActualRefMismatchMinimum)..$($run.ActualRefMismatchMaximum)")
    $summary.Add("actualMode2MeanAbsAverage=$(Format-Number $run.ActualMode2MeanAverage)")
    $summary.Add("actualMode2MeanAbsMedian=$(Format-Number $run.ActualMode2MeanMedian)")
    $summary.Add("actualMode2MeanAbsRange=$(Format-Number $run.ActualMode2MeanMinimum)..$(Format-Number $run.ActualMode2MeanMaximum)")
    $summary.Add("actualMode2MaxAbsMaximum=$(Format-Number $run.ActualMode2MaxAbsMaximum)")
    $summary.Add("actualMode2MismatchRange=$($run.ActualMode2MismatchMinimum)..$($run.ActualMode2MismatchMaximum)")
    $summary.Add("refMode2MeanAbsAverage=$(Format-Number $run.RefMode2MeanAverage)")
    $summary.Add("refMode2MeanAbsMedian=$(Format-Number $run.RefMode2MeanMedian)")
    $summary.Add("refMode2MeanAbsRange=$(Format-Number $run.RefMode2MeanMinimum)..$(Format-Number $run.RefMode2MeanMaximum)")
    $summary.Add("refMode2MaxAbsMaximum=$(Format-Number $run.RefMode2MaxAbsMaximum)")
    $summary.Add("refMode2MismatchRange=$($run.RefMode2MismatchMinimum)..$($run.RefMode2MismatchMaximum)")
    $summary.Add("")
}
$summary.Add("[AUX_OBSERVER_GATES]")
foreach ($aux in @($asyncAux, $graphicsAux)) {
    $summary.Add("$($aux.Mode).valid=$([int]$aux.Valid)")
    $summary.Add("$($aux.Mode).v20Decision=$($aux.V20Decision)")
    $summary.Add("$($aux.Mode).v20SnapshotMatchTimeout=$($aux.V20SnapshotMatchTimeout)")
    $summary.Add("$($aux.Mode).v20PresentedButNotScheduled=$($aux.V20PresentedButNotScheduled)")
    $summary.Add("$($aux.Mode).v24Decision=$($aux.V24Decision)")
    $summary.Add("$($aux.Mode).v25Decision=$($aux.V25Decision)")
}
$summary.Add("")
$summary.Add("[DECISION_RULE]")
$summary.Add("ASYNC_CONTEXT_CAUSAL_CONTRIBUTOR=ASYNC neither>=90%; GRAPHICS one exact class>=90% and neither<=10%.")
$summary.Add("ASYNC_CONTEXT_NOT_PRIMARY=both neither>=90%; both A-vs-shadow median mismatch classes equal; both meanAbs medians within fixed 0.25x..4x magnitude band.")
$summary.Add("ASYNC_CONTEXT_PARTIAL=valid A/B that changes classification/divergence without strict exact closure or same-class reproduction.")
$summary.Add("INVALID_AB=identity/environment/runtime/observer gate failure or either eligible<60.")
$summary.Add("Anomalous<3 affects only that run's anomalous-specific conclusion.")
[void](Write-KiwiTextFile -Path (Join-Path $EvidenceRoot "AB_SUMMARY.txt") -Text (($summary -join "`r`n") + "`r`n") -AllowedRoots $allowedRoots -Encoding "Utf8NoBom")

if ($decision -eq "INVALID_AB") {
    Write-Host "RUNTIME_VALIDATION_FAIL_CLOSED"
}
else {
    Write-Host "RUNTIME_VALIDATION_PASS"
}
Write-Host "decision=$decision"
Write-Host "asyncEligible=$($async.Eligible)"
Write-Host "asyncNeither=$($async.Neither)"
Write-Host "graphicsEligible=$($graphics.Eligible)"
Write-Host "graphicsNeither=$($graphics.Neither)"
Write-Host "sameDistributionClass=$([int]$sameDistributionClass)"
if ($decision -eq "INVALID_AB") { exit 2 }
exit 0
