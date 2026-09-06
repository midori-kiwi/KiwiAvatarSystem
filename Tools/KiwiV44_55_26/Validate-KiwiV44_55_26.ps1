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

function Get-V26Decision {
    param(
        [int]$DependencyCount,
        [int]$CompletedCount,
        [int]$AnomalousCount,
        [int]$EligibleAnomalousCount,
        [int]$ExactCount,
        [int]$MismatchCount,
        [int]$IntegrityTotal
    )
    if (
        $IntegrityTotal -ne 0 -or
        $DependencyCount -lt 60 -or
        $DependencyCount -ne $CompletedCount
    ) { return "INVALID_OBSERVER" }
    if ($AnomalousCount -eq 0) {
        return "INSUFFICIENT_ANOMALOUS_COVERAGE"
    }
    if ($EligibleAnomalousCount -lt 1) { return "PARTIAL_COVERAGE" }
    if ($MismatchCount -ne 0) { return "DECODE_PAYLOAD_MISMATCH" }
    if ($ExactCount -gt 0) { return "DECODE_PAYLOAD_EXACT" }
    return "PARTIAL_COVERAGE"
}

$observerRel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_26.cs"
$observerMetaRel = $observerRel + ".meta"
$v24Rel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiPairBoundShadowOutputSnapshotV44_55_24.cs"
$v25Rel = "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_25.cs"
$bridgeRel = "Assets\KiwiAvatarSystem\Runtime\Optimization\KiwiOrtDirectMLShadowRuntime.cs"
$observer = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot $observerRel) -AllowedRoots $allowedRoots -Label "v26 observer"
$observerMeta = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot $observerMetaRel) -AllowedRoots $allowedRoots -Label "v26 observer meta"
$v24 = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot $v24Rel) -AllowedRoots $allowedRoots -Label "v24 observer"
$v25 = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot $v25Rel) -AllowedRoots $allowedRoots -Label "v25 observer"
$bridge = Assert-KiwiPathWithinRoot -Path (Join-Path $ProjectRoot $bridgeRel) -AllowedRoots $allowedRoots -Label "readable output bridge"

[void](Assert-KiwiFileSha256 -Path $observer -ExpectedSha256 "A4CDF5A9FF424E56D0ADA15D09AD0BED00B8BEE6B517F59463A2C15E068845DC" -Label "v26 observer")
[void](Assert-KiwiFileSha256 -Path $observerMeta -ExpectedSha256 "82BA94DA1511BE36A4284275A78E41E95030B9034A983CF4ACADEC32072260F6" -Label "v26 observer meta")
[void](Assert-KiwiFileSha256 -Path $v24 -ExpectedSha256 "CC6918E20708DD490AA0E7758CDF9BC5F9D03885A248B4078FC1F0FD70665FDA" -Label "v24 diagnostic seam")
[void](Assert-KiwiFileSha256 -Path $v25 -ExpectedSha256 "90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814" -Label "v25 dependency")
[void](Assert-KiwiFileSha256 -Path $bridge -ExpectedSha256 "B1D0A24F9DE459FA9AE1E7B420B4CE73C15585042B662C2E92DEE4A9165DE436" -Label "decode payload seam")

$protected = @{
    (Join-Path $ProjectRoot "Assets\Script\KiwiInferenceFaceTracker.cs") = "52C046EE44B41A4FF50B85AEF503BC29DD31B57EAF58C0D160CCC33C5D4B7695"
    (Join-Path $ProjectRoot "Assets\Script\FaceLandmarkerRunner.cs") = "6C65C075270F10C791F6B044E3BC04C6024AADF916D65283F0EEFFA3448BBB93"
    (Join-Path $ProjectRoot "Assets\Script\KiwiFaceMotion.cs") = "D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiCommonTensorBackendStageIsolationV44_55_20.cs") = "1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3"
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiProductionScheduleTransactionTraceV44_55_25.cs") = "90CEF88267EC14B5DD67DF35D0AE446C9469E6EC1058D1E8CADCF878BE18C814"
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
Assert-True ($observerText.Contains("PackedOutputLength = 468 * 3 + 1")) "Logical 1405 length marker missing."
Assert-True ($observerText.Contains("MaximumBoundedPairs = 192")) "Bounded capacity missing."
Assert-True ($observerText.Contains("DECODE_PAYLOAD_EXACT")) "Exact decision missing."
Assert-True ($observerText.Contains("DECODE_PAYLOAD_MISMATCH")) "Mismatch decision missing."
Assert-True ($observerText.Contains("PARTIAL_COVERAGE")) "Coverage decision missing."
Assert-True ($observerText.Contains("INVALID_OBSERVER")) "Invalid decision missing."
Assert-True (-not [regex]::IsMatch($observerText, '\.ReadbackAndClone\s*\(')) "Observer adds ReadbackAndClone."
Assert-True (-not [regex]::IsMatch($observerText, '\.ReadbackRequest\s*\(')) "Observer adds ReadbackRequest."
Assert-True (-not [regex]::IsMatch($observerText, '\.Schedule\s*\(')) "Observer schedules inference."
Assert-True (-not [regex]::IsMatch($observerText, 'new\s+Worker')) "Observer creates Worker."
Assert-True (-not [regex]::IsMatch($observerText, 'WaitForCompletion|WaitOnAsyncGraphicsFence|g_unityD3D12Queue')) "Observer adds a blocking wait."
Assert-True (-not [regex]::IsMatch($observerText, 'lane\.input')) "Observer uses lane.input oracle."
Assert-True (([regex]::Matches($v24Text, 'RecordPairBoundSnapshot\s*\(')).Count -eq 1) "v24 callback count invalid."
Assert-True (([regex]::Matches($bridgeText, 'RecordDecodePayload\s*\(')).Count -eq 1) "readable callback count invalid."
Assert-True ($bridgeText.Contains("ReadbackAndClone")) "Existing readable boundary comment missing."

Assert-True ((Get-V26Decision 60 60 1 1 60 0 0) -eq "DECODE_PAYLOAD_EXACT") "Self-audit exact branch failed."
Assert-True ((Get-V26Decision 60 60 1 1 59 1 0) -eq "DECODE_PAYLOAD_MISMATCH") "Self-audit mismatch branch failed."
Assert-True ((Get-V26Decision 60 60 1 0 0 0 0) -eq "PARTIAL_COVERAGE") "Self-audit partial branch failed."
Assert-True ((Get-V26Decision 60 60 0 0 60 0 0) -eq "INSUFFICIENT_ANOMALOUS_COVERAGE") "Self-audit anomaly branch failed."
Assert-True ((Get-V26Decision 60 59 1 1 59 0 1) -eq "INVALID_OBSERVER") "Self-audit invalid branch failed."

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
$textPath = Join-Path $runtimeDirectory ("KiwiProductionDecodePayloadTransactionClosure_v44_55_26_" + $EvidenceStamp + ".txt")
$csvPath = Join-Path $runtimeDirectory ("KiwiProductionDecodePayloadTransactionClosure_v44_55_26_" + $EvidenceStamp + ".csv")
[void](Assert-KiwiFile -Path $textPath -Label "v26 TXT")
[void](Assert-KiwiFile -Path $csvPath -Label "v26 CSV")
$values = Read-KeyValues -Path $textPath
$rows = @(Import-Csv -LiteralPath $csvPath)
Assert-True ($values['status'] -eq 'COMPLETE' -or $values['status'] -eq 'COMPLETE_WITH_COVERAGE') "Runtime did not reach a complete status."
$required = @('recordIndex','sequence','pairToken','sourceHostTicks','laneIndex','scheduleBeginHostTicks','startedHostTicks','readbackRequestHostTicks','readbackRequestFrame','anchorRevision','externalAnchorEpoch','trackerGeneration','cameraGeneration','trackingSessionGeneration','minimumPresenceBits','cropMatrixBits','workerIdentityToken','pendingOutputIdentityToken','canonicalPublicationFrameId','exactCount','bitwiseExact','meanAbs','maxAbs','firstMismatchIndex','mismatchCount','classification')
foreach ($column in $required) {
    Assert-True ($rows.Count -gt 0 -and $rows[0].PSObject.Properties.Name -contains $column) "Missing CSV column: $column"
}
$dependencyCount = Get-IntValue $values 'dependencyTraceCount'
$completedCount = Get-IntValue $values 'completedPairCount'
$anomalousCount = Get-IntValue $values 'anomalousPairCount'
$eligibleAnomalousCount = Get-IntValue $values 'eligibleAnomalousPairCount'
$exactCount = Get-IntValue $values 'decodePayloadExactCount'
$mismatchCount = Get-IntValue $values 'decodePayloadMismatchCount'
$integrityNames = @('observerFaultCount','identityMismatchCount','duplicateDecodePayloadCount','duplicateSnapshotPayloadCount','payloadShapeMismatchCount','nonFinitePayloadCount','partialCaptureCount','pendingAtCompletion')
$integrityTotal = 0
foreach ($name in $integrityNames) { $integrityTotal += Get-IntValue $values $name }
Assert-True ($rows.Count -eq $completedCount) "CSV/completed count mismatch."
Assert-True (@($rows | Group-Object recordIndex | Where-Object Count -ne 1).Count -eq 0) "Duplicate recordIndex."
Assert-True (@($rows | Group-Object pairToken | Where-Object Count -ne 1).Count -eq 0) "Duplicate pairToken."
Assert-True (@($rows | Where-Object { [int]$_.pairToken -le 0 }).Count -eq 0) "Invalid pairToken."
Assert-True (@($rows | Where-Object { $_.snapshotCaptured -ne '1' }).Count -eq 0) "Missing v24 snapshot row."
Assert-True (@($rows | Where-Object { $_.anomalous -eq '1' }).Count -eq $anomalousCount) "Anomalous count mismatch."
Assert-True (@($rows | Where-Object { $_.eligible -eq '1' -and $_.anomalous -eq '1' }).Count -eq $eligibleAnomalousCount) "Eligible anomalous count mismatch."
Assert-True (@($rows | Where-Object classification -eq 'DECODE_PAYLOAD_EXACT').Count -eq $exactCount) "Exact row count mismatch."
Assert-True (@($rows | Where-Object classification -eq 'DECODE_PAYLOAD_MISMATCH').Count -eq $mismatchCount) "Mismatch row count mismatch."
foreach ($row in $rows) {
    $logicalTotal = [int]$row.exactCount + [int]$row.mismatchCount
    if ($row.decodeCaptured -eq '1') { Assert-True ($logicalTotal -eq 1405) "Logical count mismatch in row $($row.recordIndex)." }
    if ($row.classification -eq 'DECODE_PAYLOAD_EXACT') {
        Assert-True ($row.eligible -eq '1' -and $row.v25Classification -eq 'TRANSACTION_COHERENT' -and $row.bitwiseExact -eq '1' -and [int]$row.exactCount -eq 1405 -and [int]$row.mismatchCount -eq 0 -and [int]$row.firstMismatchIndex -eq -1 -and [double]$row.meanAbs -eq 0.0 -and [double]$row.maxAbs -eq 0.0 -and [uint64]$row.canonicalPublicationFrameId -gt 0) "Invalid exact row $($row.recordIndex)."
    }
    elseif ($row.classification -eq 'DECODE_PAYLOAD_MISMATCH') {
        Assert-True ($row.eligible -eq '1' -and $row.v25Classification -eq 'TRANSACTION_COHERENT' -and $row.bitwiseExact -eq '0' -and [int]$row.mismatchCount -gt 0 -and [int]$row.firstMismatchIndex -ge 0) "Invalid mismatch row $($row.recordIndex)."
    }
    elseif ($row.classification -eq 'PARTIAL_COVERAGE') {
        Assert-True ($row.eligible -eq '0') "Partial row is eligible $($row.recordIndex)."
        if ($row.failureReason -eq 'CANONICAL_PUBLICATION_NOT_OBSERVED') {
            Assert-True ($row.v25Classification -eq 'NO_CANONICAL_PUBLICATION_OBSERVED') "Partial row froze non-final v25 disposition $($row.recordIndex)."
        }
    }
    else { throw "Unexpected row classification: $($row.classification)" }
}
$computedDecision = Get-V26Decision $dependencyCount $completedCount $anomalousCount $eligibleAnomalousCount $exactCount $mismatchCount $integrityTotal
Assert-True ($values['decision'] -eq $computedDecision) "Decision/counter mismatch."
Write-Host "RUNTIME_VALIDATION_PASS"
Write-Host "decision=$computedDecision"
Write-Host "completedPairCount=$completedCount"
Write-Host "anomalousPairCount=$anomalousCount"
Write-Host "eligibleAnomalousPairCount=$eligibleAnomalousCount"
exit 0
