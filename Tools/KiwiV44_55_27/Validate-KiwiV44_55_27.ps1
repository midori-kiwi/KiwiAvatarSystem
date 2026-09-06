param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",
    [string]$EvidenceStamp = "",
    [switch]$StaticOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$modulePath = Join-Path $PSScriptRoot "..\KiwiPowerShell\KiwiPsCompat.psm1"
Import-Module -Name $modulePath -Force
$ProjectRoot = Resolve-KiwiProjectRoot -ProjectRoot $ProjectRoot
$allowedRoots = @($ProjectRoot)

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-Text {
    param([string]$Path)
    return [System.IO.File]::ReadAllText($Path)
}

function Read-KeyValues {
    param([string]$Path)
    $values = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ($line -match '^([^=]+)=(.*)$') {
            $values[$Matches[1]] = $Matches[2]
        }
    }
    return $values
}

function Get-IntValue {
    param([hashtable]$Values, [string]$Name)
    Assert-True -Condition $Values.ContainsKey($Name) -Message "Missing key: $Name"
    return [int]$Values[$Name]
}

function Get-V27Decision {
    param(
        [int]$DependencyCount,
        [int]$CompletedCount,
        [int]$EligibleCount,
        [int]$AnomalousEligibleCount,
        [int]$RefOnlyCount,
        [int]$Mode2OnlyCount,
        [int]$BothCount,
        [int]$NeitherCount,
        [int]$ShadowExactCount,
        [int]$IntegrityTotal
    )
    if (
        $IntegrityTotal -ne 0 -or
        $DependencyCount -lt 60 -or
        $DependencyCount -ne $CompletedCount -or
        $EligibleCount -lt 60 -or
        ($RefOnlyCount + $Mode2OnlyCount + $BothCount + $NeitherCount) -ne
            $AnomalousEligibleCount
    ) { return "INVALID_OBSERVER" }
    if ($AnomalousEligibleCount -lt 3) {
        return "INSUFFICIENT_ANOMALOUS_COVERAGE"
    }
    if ($ShadowExactCount -ne 0) { return "SHADOWS_NOT_DISCRIMINATING" }
    if ($RefOnlyCount -eq $AnomalousEligibleCount) {
        return "REFERENCE_PAYLOAD_AUTHORITY_CONFIRMED"
    }
    if ($Mode2OnlyCount -eq $AnomalousEligibleCount) {
        return "MODE2_PAYLOAD_AUTHORITY_CONFIRMED"
    }
    if ($NeitherCount -eq $AnomalousEligibleCount) {
        return "ACTUAL_EQUALS_NEITHER_CONFIRMED"
    }
    return "ACTUAL_SHADOW_MIXED"
}

function Assert-ParityRow {
    param(
        [psobject]$Row,
        [string]$Prefix
    )
    $exact = [int]$Row.PSObject.Properties[$Prefix + 'ExactCount'].Value
    $mismatch = [int]$Row.PSObject.Properties[$Prefix + 'MismatchCount'].Value
    $bitwise = [int]$Row.PSObject.Properties[$Prefix + 'BitwiseExact'].Value
    $mean = [double]$Row.PSObject.Properties[$Prefix + 'MeanAbs'].Value
    $max = [double]$Row.PSObject.Properties[$Prefix + 'MaxAbs'].Value
    $first = [int]$Row.PSObject.Properties[$Prefix + 'FirstMismatchIndex'].Value
    Assert-True (($exact + $mismatch) -eq 1405) "$Prefix logical count mismatch in row $($Row.recordIndex)."
    if ($mismatch -eq 0) {
        Assert-True ($exact -eq 1405 -and $bitwise -eq 1 -and $mean -eq 0.0 -and $max -eq 0.0 -and $first -eq -1) "$Prefix exact metrics invalid in row $($Row.recordIndex)."
    }
    else {
        Assert-True ($mismatch -gt 0 -and $bitwise -eq 0 -and $first -ge 0 -and $first -lt 1405 -and $mean -ge 0.0 -and $max -ge 0.0) "$Prefix mismatch metrics invalid in row $($Row.recordIndex)."
    }
}

$observerRel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiActualProductionVsShadowPayloadAuthorityV44_55_27.cs"
$observerMetaRel = $observerRel + ".meta"
$v24Rel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiPairBoundShadowOutputSnapshotV44_55_24.cs"
$v25Rel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_25.cs"
$v26Rel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_26.cs"
$bridgeRel = "Assets\KiwiAvatarSystem\Runtime\Optimization\KiwiOrtDirectMLShadowRuntime.cs"
$observer = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot $observerRel) -AllowedRoots $allowedRoots -Label "v27 observer"
$observerMeta = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot $observerMetaRel) -AllowedRoots $allowedRoots -Label "v27 observer meta"
$v24 = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot $v24Rel) -AllowedRoots $allowedRoots -Label "v24 observer"
$v25 = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot $v25Rel) -AllowedRoots $allowedRoots -Label "v25 observer"
$v26 = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot $v26Rel) -AllowedRoots $allowedRoots -Label "v26 observer"
$bridge = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot $bridgeRel) -AllowedRoots $allowedRoots -Label "readable output bridge"

[void](Assert-KiwiFileSha256 -Path $observer -ExpectedSha256 "B8E215D1C3D4103996B7B30BFD8B26DB508782AC6529D12BEF2617E9C65C8B86" -Label "v27 observer")
[void](Assert-KiwiFileSha256 -Path $observerMeta -ExpectedSha256 "3FF3511AAA2EA67E8177C7CCAC7FF4990CE20286B5DBA973303CEE33D7FB809A" -Label "v27 observer meta")
[void](Assert-KiwiFileSha256 -Path $v24 -ExpectedSha256 "1F8F26D21DEA1DE17DE43A0A7B8FFA0513C1C9E23A65A1AEDFCFF7BA54AB26EC" -Label "v24 diagnostic seam")
[void](Assert-KiwiFileSha256 -Path $v25 -ExpectedSha256 "90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814" -Label "v25 dependency")
[void](Assert-KiwiFileSha256 -Path $v26 -ExpectedSha256 "A4CDF5A9FF424E56D0ADA15D09AD0BED00B8BEE6B517F59463A2C15E068845DC" -Label "v26 authority")
[void](Assert-KiwiFileSha256 -Path $bridge -ExpectedSha256 "D30A700A7B9E3C3D09BC64FA0CA5722729EE41821DE1F1B5E30E30D986B947C2" -Label "decode payload seam")

$protected = @{
    (Join-Path $ProjectRoot "Assets\Script\KiwiInferenceFaceTracker.cs") = "52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695"
    (Join-Path $ProjectRoot "Assets\Script\FaceLandmarkerRunner.cs") = "6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93"
    (Join-Path $ProjectRoot "Assets\Script\KiwiFaceMotion.cs") = "D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiCommonTensorBackendStageIsolationV44_55_20.cs") = "1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_25.cs") = "90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_26.cs") = "A4CDF5A9FF424E56D0ADA15D09AD0BED00B8BEE6B517F59463A2C15E068845DC"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Camera\KiwiNativeCameraInterop.cs") = "AC473ADBADE5EDC89726211ECAF03D040B5CC00FCA39BEA4A0D16195FA53E8B5"
    (Join-Path $ProjectRoot "Assets\Plugins\x86_64\KiwiNativeCamera.dll") = "82D1FC2910468056C02E8BAE1C72996D8492173A84BEBBCAE322EBEF435678A5"
    (Join-Path $ProjectRoot "Packages\manifest.json") = "35F00A89D31A8BF7EE50718F38D4BAC90236D8B691F516C5FC14CAB47AFBFEE1"
    (Join-Path $ProjectRoot "Packages\packages-lock.json") = "23845C132DBF51CB53A5CFDE4EF606FA68D10DCB53AE5FF21F4305613A5F9CE1"
}
foreach ($path in @($protected.Keys)) {
    [void](Assert-KiwiFileSha256 -Path ([string]$path) -ExpectedSha256 ([string]$protected[$path]) -Label "protected")
}

$observerText = Get-Text -Path $observer
$v24Text = Get-Text -Path $v24
$bridgeText = Get-Text -Path $bridge
$runText = Get-Text -Path (Join-Path $PSScriptRoot "Run-KiwiV44_55_27.ps1")
Assert-True ($observerText.Contains("PackedOutputLength = 468 * 3 + 1")) "Logical 1405 length marker missing."
Assert-True ($observerText.Contains("MaximumBoundedPairs = 192")) "Bounded capacity missing."
Assert-True ($observerText.Contains("MinimumCompletedPairs = 60")) "Minimum eligible gate missing."
Assert-True ($observerText.Contains("MinimumAnomalousEligiblePairs = 3")) "Minimum anomaly gate missing."
foreach ($decision in @(
    "REFERENCE_PAYLOAD_AUTHORITY_CONFIRMED",
    "MODE2_PAYLOAD_AUTHORITY_CONFIRMED",
    "ACTUAL_SHADOW_MIXED",
    "ACTUAL_EQUALS_NEITHER_CONFIRMED",
    "SHADOWS_NOT_DISCRIMINATING",
    "INSUFFICIENT_ANOMALOUS_COVERAGE",
    "INVALID_OBSERVER"
)) {
    Assert-True ($observerText.Contains($decision)) "Decision missing: $decision"
}
Assert-True ($observerText.Contains("INVALID_OBSERVER")) "Invalid decision missing."
Assert-True (-not [regex]::IsMatch($observerText, '\.ReadbackAndClone\s*\(')) "Observer adds ReadbackAndClone."
Assert-True (-not [regex]::IsMatch($observerText, '\.ReadbackRequest\s*\(')) "Observer adds ReadbackRequest."
Assert-True (-not [regex]::IsMatch($observerText, '\.Schedule\s*\(')) "Observer schedules inference."
Assert-True (-not [regex]::IsMatch($observerText, 'AsyncGPUReadback|CommandBuffer|GraphicsFence')) "Observer adds GPU capture/wait."
Assert-True (-not [regex]::IsMatch($observerText, 'new\s+Worker')) "Observer creates Worker."
Assert-True (-not [regex]::IsMatch($observerText, 'WaitForCompletion|WaitOnAsyncGraphicsFence|g_unityD3D12Queue')) "Observer adds a blocking wait."
Assert-True (-not [regex]::IsMatch($observerText, 'lane\.input')) "Observer uses lane.input oracle."
Assert-True (([regex]::Matches($v24Text, 'RecordPairBoundShadowPayloads\s*\(')).Count -eq 1) "v27 v24 callback count invalid."
Assert-True (([regex]::Matches($bridgeText, 'RecordActualDecodePayload\s*\(')).Count -eq 1) "v27 readable callback count invalid."
Assert-True ($bridgeText.Contains("ReadbackAndClone")) "Existing readable boundary comment missing."
Assert-True ($observerText.Contains("pair.ActualPayload = new float[PackedOutputLength]")) "Actual deep copy allocation missing."
Assert-True ($observerText.Contains("pair.ReferencePayload = new float[PackedOutputLength]")) "REF deep copy allocation missing."
Assert-True ($observerText.Contains("pair.Mode2Payload = new float[PackedOutputLength]")) "MODE2 deep copy allocation missing."
Assert-True (-not $observerText.Contains("rejectedProductionSnapshot[")) "Rejected Production snapshot is read as comparison data."
Assert-True ($v24Text.Contains("state.ReferenceOutput") -and $v24Text.Contains("state.Mode2Output")) "REF/MODE2 field handoff missing."
Assert-True ($runText.Contains('"KIWI_V44_55_26_PRODUCTION_DECODE_PAYLOAD_TRACE" = $null')) "v26 observer is not explicitly disabled."
Assert-True ($runText.Contains('"KIWI_V44_55_27_ACTUAL_PRODUCTION_VS_SHADOW_PAYLOAD_AUDIT" = "1"')) "v27 observer is not enabled."

Assert-True ((Get-V27Decision 60 60 60 3 3 0 0 0 0 0) -eq "REFERENCE_PAYLOAD_AUTHORITY_CONFIRMED") "Self-audit REF branch failed."
Assert-True ((Get-V27Decision 60 60 60 3 0 3 0 0 0 0) -eq "MODE2_PAYLOAD_AUTHORITY_CONFIRMED") "Self-audit MODE2 branch failed."
Assert-True ((Get-V27Decision 60 60 60 3 1 1 0 1 0 0) -eq "ACTUAL_SHADOW_MIXED") "Self-audit mixed branch failed."
Assert-True ((Get-V27Decision 60 60 60 3 0 0 0 3 0 0) -eq "ACTUAL_EQUALS_NEITHER_CONFIRMED") "Self-audit neither branch failed."
Assert-True ((Get-V27Decision 60 60 60 3 0 0 3 0 3 0) -eq "SHADOWS_NOT_DISCRIMINATING") "Self-audit nondiscriminating branch failed."
Assert-True ((Get-V27Decision 60 60 60 2 2 0 0 0 0 0) -eq "INSUFFICIENT_ANOMALOUS_COVERAGE") "Self-audit coverage branch failed."
Assert-True ((Get-V27Decision 60 59 60 3 3 0 0 0 0 1) -eq "INVALID_OBSERVER") "Self-audit invalid branch failed."

$parser = $null
$errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($MyInvocation.MyCommand.Path, [ref]$parser, [ref]$errors)
Assert-True (@($errors).Count -eq 0) "Validator does not parse."
Write-Host "STATIC_VALIDATION_PASS"
Write-Host "VALIDATOR_SELF_AUDIT_PASS"
Write-Host "OBSERVER_SHA256=$(Get-KiwiSha256 -Path $observer)"
Write-Host "PROTECTED_SHA_PASS"
Write-Host "POWERSHELL_5_1_PARSER_PASS"

if ($StaticOnly) { exit 0 }
Assert-True (-not [string]::IsNullOrWhiteSpace($EvidenceStamp)) "EvidenceStamp is required."
Assert-True ($EvidenceStamp -match '^\d{8}_\d{6}$') "EvidenceStamp format invalid."
$runtimeDirectory = Join-Path $env:USERPROFILE "AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem\KiwiFrameBottleneck"
$textPath = Join-Path $runtimeDirectory ("KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_" + $EvidenceStamp + ".txt")
$csvPath = Join-Path $runtimeDirectory ("KiwiActualProductionVsShadowPayloadAuthority_v44_55_27_" + $EvidenceStamp + ".csv")
[void](Assert-KiwiFile -Path $textPath -Label "v27 TXT")
[void](Assert-KiwiFile -Path $csvPath -Label "v27 CSV")
$values = Read-KeyValues -Path $textPath
$rows = @(Import-Csv -LiteralPath $csvPath)
Assert-True ($values['status'] -eq 'COMPLETE' -or $values['status'] -eq 'COMPLETE_WITH_COVERAGE') "Runtime did not reach a complete status."
$required = @(
    'recordIndex','sequence','pairToken','nativeHostTicks','managedHostTicks',
    'sourceHostTicks','laneIndex','laneStartedHostTicks','scheduleBeginHostTicks',
    'startedHostTicks','readbackRequestHostTicks','readbackRequestFrame',
    'anchorRevision','externalAnchorEpoch','trackerGeneration',
    'cameraGeneration','trackingSessionGeneration','minimumPresenceBits',
    'cropMatrixBits','laneIdentityToken','workerIdentityToken',
    'pendingOutputIdentityToken','v24Classification','v25Classification',
    'shadowSnapshotRecordIndex','referenceSnapshotFrame','mode2SnapshotFrame',
    'shadowCaptured','actualCaptured','payloadEligible','publishedSubset',
    'actualRefExactCount','actualRefMismatchCount','actualRefBitwiseExact',
    'actualRefMeanAbs','actualRefMaxAbs','actualRefFirstMismatchIndex',
    'actualMode2ExactCount','actualMode2MismatchCount',
    'actualMode2BitwiseExact','actualMode2MeanAbs','actualMode2MaxAbs',
    'actualMode2FirstMismatchIndex','refMode2ExactCount',
    'refMode2MismatchCount','refMode2BitwiseExact','refMode2MeanAbs',
    'refMode2MaxAbs','refMode2FirstMismatchIndex','classification',
    'failureReason'
)
foreach ($column in $required) {
    Assert-True ($rows.Count -gt 0 -and
        $rows[0].PSObject.Properties.Name -contains $column) "Missing CSV column: $column"
}
$dependencyCount = Get-IntValue $values 'dependencyTraceCount'
$capturedCount = Get-IntValue $values 'capturedPairCount'
$completedCount = Get-IntValue $values 'completedPairCount'
$comparedCount = Get-IntValue $values 'comparedPairCount'
$eligibleCount = Get-IntValue $values 'payloadEligiblePairCount'
$anomalousCount = Get-IntValue $values 'anomalousPairCount'
$eligibleAnomalousCount = Get-IntValue $values 'anomalousPayloadEligiblePairCount'
$refOnlyCount = Get-IntValue $values 'anomalousActualEqualsRefOnlyCount'
$mode2OnlyCount = Get-IntValue $values 'anomalousActualEqualsMode2OnlyCount'
$bothCount = Get-IntValue $values 'anomalousActualEqualsBothCount'
$neitherCount = Get-IntValue $values 'anomalousActualEqualsNeitherCount'
$shadowExactCount = Get-IntValue $values 'anomalousShadowExactCount'
$publishedCount = Get-IntValue $values 'publishedPayloadEligiblePairCount'
$publishedAnomalousCount = Get-IntValue $values 'publishedAnomalousPayloadEligiblePairCount'
$integrityNames = @(
    'observerFaultCount','identityMismatchCount',
    'duplicateDecodePayloadCount','duplicateShadowPayloadCount',
    'arrayAliasCount','fieldMappingMismatchCount',
    'payloadShapeMismatchCount','nonFinitePayloadCount',
    'partialCaptureCount','noDecodePayloadCoverageCount',
    'pendingAtCompletion'
)
$integrityTotal = 0
foreach ($name in $integrityNames) {
    $integrityTotal += Get-IntValue $values $name
}
Assert-True ($rows.Count -eq $completedCount) "CSV/completed count mismatch."
Assert-True ($dependencyCount -eq $capturedCount -and
    $dependencyCount -eq $completedCount -and
    $dependencyCount -eq $comparedCount) "Dependency/capture/compare counts differ."
Assert-True (@($rows | Group-Object recordIndex |
    Where-Object Count -ne 1).Count -eq 0) "Duplicate recordIndex."
Assert-True (@($rows | Group-Object pairToken |
    Where-Object Count -ne 1).Count -eq 0) "Duplicate pairToken."
Assert-True (@($rows | Where-Object {
    [int]$_.pairToken -le 0
}).Count -eq 0) "Invalid pairToken."
Assert-True (@($rows | Where-Object {
    $_.shadowCaptured -ne '1' -or
    $_.actualCaptured -ne '1' -or
    $_.payloadEligible -ne '1'
}).Count -eq 0) "Missing or ineligible payload row."
Assert-True (@($rows | Where-Object {
    [int]$_.shadowSnapshotRecordIndex -ne [int]$_.recordIndex -or
    [int]$_.referenceSnapshotFrame -lt 0 -or
    [int]$_.mode2SnapshotFrame -lt 0
}).Count -eq 0) "Invalid v24 snapshot identity."
Assert-True (@($rows | Where-Object {
    $_.v25Classification -ne 'TRANSACTION_COHERENT' -and
    $_.v25Classification -ne 'NO_CANONICAL_PUBLICATION_OBSERVED'
}).Count -eq 0) "Invalid final v25 classification."
Assert-True (@($rows | Where-Object {
    $_.publishedSubset -eq '1' -and
    $_.v25Classification -ne 'TRANSACTION_COHERENT'
}).Count -eq 0) "Published subset includes non-published row."
Assert-True (@($rows | Where-Object {
    $_.publishedSubset -eq '0' -and
    $_.v25Classification -ne 'NO_CANONICAL_PUBLICATION_OBSERVED'
}).Count -eq 0) "All-decode subset disposition mismatch."
foreach ($row in $rows) {
    Assert-ParityRow -Row $row -Prefix 'actualRef'
    Assert-ParityRow -Row $row -Prefix 'actualMode2'
    Assert-ParityRow -Row $row -Prefix 'refMode2'
    $ar = $row.actualRefBitwiseExact -eq '1'
    $am = $row.actualMode2BitwiseExact -eq '1'
    $rm = $row.refMode2BitwiseExact -eq '1'
    Assert-True ((($ar -and $am) -eq ($ar -and $rm)) -and
        (($ar -and $am) -eq ($am -and $rm))) "Bitwise equality transitivity invalid in row $($row.recordIndex)."
    $expectedClass = if ($ar -and $am) {
        'ACTUAL_EQUALS_BOTH'
    }
    elseif ($ar) {
        'ACTUAL_EQUALS_REF_ONLY'
    }
    elseif ($am) {
        'ACTUAL_EQUALS_MODE2_ONLY'
    }
    else {
        'ACTUAL_EQUALS_NEITHER'
    }
    Assert-True ($row.classification -eq $expectedClass) "Row classification mismatch in row $($row.recordIndex)."
    Assert-True ($row.failureReason -eq '-') "Eligible row has failure reason in row $($row.recordIndex)."
}
$rowAnomalous = @($rows | Where-Object anomalous -eq '1')
$rowEligibleAnomalous = @($rows | Where-Object {
    $_.payloadEligible -eq '1' -and $_.anomalous -eq '1'
})
Assert-True ($rowAnomalous.Count -eq $anomalousCount) "Anomalous count mismatch."
Assert-True ($rowEligibleAnomalous.Count -eq $eligibleAnomalousCount) "Eligible anomalous count mismatch."
Assert-True (@($rowEligibleAnomalous |
    Where-Object classification -eq 'ACTUAL_EQUALS_REF_ONLY').Count -eq
    $refOnlyCount) "Anomalous REF-only count mismatch."
Assert-True (@($rowEligibleAnomalous |
    Where-Object classification -eq 'ACTUAL_EQUALS_MODE2_ONLY').Count -eq
    $mode2OnlyCount) "Anomalous MODE2-only count mismatch."
Assert-True (@($rowEligibleAnomalous |
    Where-Object classification -eq 'ACTUAL_EQUALS_BOTH').Count -eq
    $bothCount) "Anomalous both count mismatch."
Assert-True (@($rowEligibleAnomalous |
    Where-Object classification -eq 'ACTUAL_EQUALS_NEITHER').Count -eq
    $neitherCount) "Anomalous neither count mismatch."
Assert-True (@($rowEligibleAnomalous |
    Where-Object refMode2BitwiseExact -eq '1').Count -eq
    $shadowExactCount) "Anomalous shadow-exact count mismatch."
Assert-True (@($rows | Where-Object publishedSubset -eq '1').Count -eq
    $publishedCount) "Published subset count mismatch."
Assert-True (@($rowEligibleAnomalous |
    Where-Object publishedSubset -eq '1').Count -eq
    $publishedAnomalousCount) "Published anomalous count mismatch."
$summaryMap = @{
    actualEqualsRefOnlyCount = 'ACTUAL_EQUALS_REF_ONLY'
    actualEqualsMode2OnlyCount = 'ACTUAL_EQUALS_MODE2_ONLY'
    actualEqualsBothCount = 'ACTUAL_EQUALS_BOTH'
    actualEqualsNeitherCount = 'ACTUAL_EQUALS_NEITHER'
}
foreach ($name in @($summaryMap.Keys)) {
    $expectedClassification = $summaryMap[$name]
    Assert-True (@($rows | Where-Object {
        $_.classification -eq $expectedClassification
    }).Count -eq (Get-IntValue $values $name)) "All-decode classification count mismatch: $name"
}
$publishedMap = @{
    publishedActualEqualsRefOnlyCount = 'ACTUAL_EQUALS_REF_ONLY'
    publishedActualEqualsMode2OnlyCount = 'ACTUAL_EQUALS_MODE2_ONLY'
    publishedActualEqualsBothCount = 'ACTUAL_EQUALS_BOTH'
    publishedActualEqualsNeitherCount = 'ACTUAL_EQUALS_NEITHER'
}
foreach ($name in @($publishedMap.Keys)) {
    Assert-True (@($rows | Where-Object {
        $_.publishedSubset -eq '1' -and
        $_.classification -eq $publishedMap[$name]
    }).Count -eq (Get-IntValue $values $name)) "Published classification count mismatch: $name"
}
$computedDecision = Get-V27Decision $dependencyCount $completedCount $eligibleCount $eligibleAnomalousCount $refOnlyCount $mode2OnlyCount $bothCount $neitherCount $shadowExactCount $integrityTotal
Assert-True ($values['decision'] -eq $computedDecision) "Decision/counter mismatch."
Write-Host "RUNTIME_VALIDATION_PASS"
Write-Host "decision=$computedDecision"
Write-Host "completedPairCount=$completedCount"
Write-Host "payloadEligiblePairCount=$eligibleCount"
Write-Host "anomalousPayloadEligiblePairCount=$eligibleAnomalousCount"
Write-Host "publishedPayloadEligiblePairCount=$publishedCount"
exit 0
