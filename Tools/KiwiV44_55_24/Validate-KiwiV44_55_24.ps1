param(
    [string]$ProjectRoot = "D:\KiwiAvatarSystem",
    [string]$EvidenceStamp = "20260902_220948",
    [string]$RuntimeEvidenceDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Sha256 {
    param(
        [string]$Path,
        [string]$Expected
    )

    Assert-True (Test-Path -LiteralPath $Path -PathType Leaf) `
        "Required file missing: $Path"

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()

    Assert-True ($actual -eq $Expected) `
        "SHA256 mismatch: $Path expected=$Expected actual=$actual"
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

$ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)
if ([string]::IsNullOrWhiteSpace($RuntimeEvidenceDirectory)) {
    $RuntimeEvidenceDirectory = Join-Path $env:USERPROFILE `
        "AppData\LocalLow\MidoriKiwi\KiwiAvatarSystem\KiwiFrameBottleneck"
}

$evidenceDirectory = [System.IO.Path]::GetFullPath($RuntimeEvidenceDirectory)
$textPath = Join-Path $evidenceDirectory `
    ("KiwiPairBoundShadowOutputSnapshot_v44_55_24_" + $EvidenceStamp + ".txt")
$csvPath = Join-Path $evidenceDirectory `
    ("KiwiPairBoundShadowOutputSnapshot_v44_55_24_" + $EvidenceStamp + ".csv")

Assert-Sha256 `
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiPairBoundShadowOutputSnapshotV44_55_24.cs") `
    "805E746E453F3785FCAA46EFB07745B0038A428789D4A09BB944854F15477FBA"

Assert-Sha256 `
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\KiwiCommonTensorBackendStageIsolationV44_55_20.cs") `
    "1F5D5BD6018C293529B9E8034B24F2168A377E6F697203A3D1504FFD4C8211B3"

Assert-Sha256 `
    (Join-Path $ProjectRoot "Assets\KiwiAvatarSystem\Runtime\Validation\Resources\KiwiValidation\KiwiOutputSnapshotCopyV44_55_24.compute") `
    "4552D8A2CB4E47465D1BD765CD38D85F7B839A61B73555CD40557937D803CB71"

Assert-True (Test-Path -LiteralPath $textPath -PathType Leaf) `
    "Runtime TXT missing: $textPath"
Assert-True (Test-Path -LiteralPath $csvPath -PathType Leaf) `
    "Runtime CSV missing: $csvPath"

$kv = Read-KeyValues $textPath
$zeroKeys = @(
    "observerFaultCount",
    "attachMissCount",
    "recordCaptureWindowMissCount",
    "shadowOutputCaptureMissCount",
    "shadowIdentityMismatchCount",
    "duplicateShadowSnapshotCount",
    "productionOutputSnapshotFailureCount",
    "referenceOutputSnapshotFailureCount",
    "mode2OutputSnapshotFailureCount",
    "nonFiniteOutputCount",
    "inputArrayIdentityMismatchCount",
    "outputShapeMismatchCount",
    "capturesPending"
)

Assert-True ($kv["status"] -eq "COMPLETE") "Observer status is not COMPLETE."
Assert-True ($kv["dependencyStatus"] -eq "COMPLETE") "Dependency is not COMPLETE."
Assert-True ($kv["decision"] -in @(
        "REFERENCE_TRANSACTION_VALID_CONFIRMED",
        "REFERENCE_TRANSACTION_INVALID_CONFIRMED",
        "MIXED_TRANSACTION",
        "INSUFFICIENT_ANOMALY_REPRODUCTION",
        "INSUFFICIENT_DATA",
        "INVALID_OBSERVER")) `
    "Unknown decision token."
Assert-True ([int]$kv["completedPairCount"] -ge 60) `
    "completedPairCount is below 60."
Assert-True ([int]$kv["completedPairCount"] -eq [int]$kv["dependencyRecordCount"]) `
    "Pair/dependency record counts differ."
Assert-True ([int]$kv["anomalousPairCount"] -ge 3) `
    "anomalousPairCount is below 3."

foreach ($key in $zeroKeys) {
    Assert-True ([int]$kv[$key] -eq 0) "$key is nonzero."
}

$rows = @(Import-Csv -LiteralPath $csvPath)
Assert-True ($rows.Count -eq [int]$kv["completedPairCount"]) `
    "CSV row count does not match completedPairCount."

$seenTokens = @{}
$anomalyCount = 0
$referenceExactCount = 0
$mode2ExactCount = 0
$bothExactCount = 0
$neitherExactCount = 0

foreach ($row in $rows) {
    Assert-True ([int]$row.index -eq [int]$row.shadowSnapshotRecordIndex) `
        "Shadow record index mismatch at row $($row.index)."
    Assert-True ([uint64]$row.sequence -eq [uint64]$row.shadowSnapshotSequence) `
        "Shadow sequence mismatch at row $($row.index)."
    Assert-True ([int]$row.shadowSnapshotPairToken -gt 0) `
        "Invalid pair token at row $($row.index)."
    Assert-True (-not $seenTokens.ContainsKey($row.shadowSnapshotPairToken)) `
        "Duplicate pair token $($row.shadowSnapshotPairToken)."
    $seenTokens[$row.shadowSnapshotPairToken] = $true

    Assert-True ([int]$row.refSnapshotFrame -le [int]$row.recordCompletionFrame) `
        "REF snapshot happened after record completion at row $($row.index)."
    Assert-True ([int]$row.mode2SnapshotFrame -le [int]$row.recordCompletionFrame) `
        "MODE2 snapshot happened after record completion at row $($row.index)."

    if ([int]$row.refMode2Ge4 -gt 0) {
        $anomalyCount++

        $ref = [int]$row.prodRefBitwiseExact -eq 1
        $mode2 = [int]$row.prodMode2BitwiseExact -eq 1

        if ($ref -and -not $mode2) {
            $referenceExactCount++
        }
        elseif ($mode2 -and -not $ref) {
            $mode2ExactCount++
        }
        elseif ($ref -and $mode2) {
            $bothExactCount++
        }
        else {
            $neitherExactCount++
        }
    }
}

Assert-True ($anomalyCount -eq [int]$kv["anomalousPairCount"]) `
    "CSV anomalous count differs from TXT."
Assert-True ($referenceExactCount -eq [int]$kv["referenceExactMatchCount"]) `
    "CSV reference exact count differs from TXT."
Assert-True ($mode2ExactCount -eq [int]$kv["mode2ExactMatchCount"]) `
    "CSV mode2 exact count differs from TXT."
Assert-True ($bothExactCount -eq [int]$kv["bothExactCount"]) `
    "CSV both-exact count differs from TXT."
Assert-True ($neitherExactCount -eq [int]$kv["neitherExactCount"]) `
    "CSV neither-exact count differs from TXT."

Write-Host "VALIDATION PASS"
Write-Host ("decision=" + $kv["decision"])
Write-Host ("completedPairCount=" + $kv["completedPairCount"])
Write-Host ("anomalousPairCount=" + $kv["anomalousPairCount"])
Write-Host ("referenceExactMatchCount=" + $kv["referenceExactMatchCount"])
Write-Host ("neitherExactCount=" + $kv["neitherExactCount"])
