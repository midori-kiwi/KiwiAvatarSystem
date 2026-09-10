param(
    [Parameter(Mandatory = $false)]
    [string]$ProjectRoot = 'D:\KiwiAvatarSystem'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$resultPath = Join-Path $ProjectRoot 'CodexTasks\P3C_1_STATIC_VALIDATION_RESULT.txt'
$servicePath = Join-Path $ProjectRoot 'Assets\KiwiAvatarSystem\Runtime\TrackingFoundation\KiwiFaceGeometryTransactionService.cs'
$runnerPath = Join-Path $ProjectRoot 'Assets\Script\FaceLandmarkerRunner.cs'
$scenePath = Join-Path $ProjectRoot 'Assets\Scenes\Face Landmark Detection.unity'
$stressPath = Join-Path $ProjectRoot 'CodexTasks\P3C_1_CALLBACK_LIFECYCLE_STRESS_RESULT.txt'
$compileLogPath = Join-Path $ProjectRoot 'CodexTasks\P3C_1_CallbackLifecycle_UnityCompile_ExactExit_20260910.log'

$checks = New-Object 'System.Collections.Generic.List[string]'

function Require-True {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if (-not $Condition) {
        throw "Validation failed: $Name"
    }

    $checks.Add("$Name=PASS")
}

function Get-Sha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath
    )

    return (Get-FileHash -Algorithm SHA256 -LiteralPath $LiteralPath).Hash
}

try {
    Require-True ((Test-Path -LiteralPath $servicePath) -eq $true) 'SERVICE_PRESENT'
    Require-True ((Test-Path -LiteralPath $stressPath) -eq $true) 'STRESS_RESULT_PRESENT'
    Require-True ((Test-Path -LiteralPath $compileLogPath) -eq $true) 'COMPILE_LOG_PRESENT'

    $service = [IO.File]::ReadAllText($servicePath)
    $runner = [IO.File]::ReadAllText($runnerPath)
    $scene = [IO.File]::ReadAllText($scenePath)
    $stress = [IO.File]::ReadAllText($stressPath)
    $compileLog = [IO.File]::ReadAllText($compileLogPath)

    foreach ($token in @(
        'resultPublicationAccepting',
        'nativeGraphAlive',
        'nativeGraphDestroyStarted',
        'noFutureCallbacks',
        'activeCallbacks',
        'callbackRootsReleased',
        'TryEnterCallbackLifetime',
        'ExitCallbackLifetime',
        'RetireResultPublication',
        'TryBeginNativeGraphDestroy',
        'MarkNativeGraphDestroyed',
        'TryMarkCallbackRootsReleased',
        'TryReleaseCallbackRoots'
    )) {
        Require-True ($service.IndexOf($token, [StringComparison]::Ordinal) -ge 0) ("STATE_TOKEN_" + $token)
    }

    Require-True ($service.IndexOf('readyToDispose', [StringComparison]::Ordinal) -lt 0) 'OLD_READY_TO_DISPOSE_REMOVED'
    Require-True ($service.IndexOf('acceptingCallbacks', [StringComparison]::Ordinal) -lt 0) 'OLD_COMBINED_ACCEPTANCE_REMOVED'

    $destroyStart = $service.IndexOf('private void DestroyGraphAndFinalize', [StringComparison]::Ordinal)
    $destroyEnd = $service.IndexOf('private void TryFinalizeDestroyedRun', $destroyStart, [StringComparison]::Ordinal)
    Require-True (($destroyStart -ge 0) -and ($destroyEnd -gt $destroyStart)) 'DESTROY_METHOD_BOUNDS'
    $destroyBody = $service.Substring($destroyStart, $destroyEnd - $destroyStart)
    $disposeIndex = $destroyBody.IndexOf('run.graph.Dispose();', [StringComparison]::Ordinal)
    $destroyedIndex = $destroyBody.IndexOf('run.MarkNativeGraphDestroyed();', [StringComparison]::Ordinal)
    $finalizeIndex = $destroyBody.IndexOf('TryFinalizeDestroyedRun(run);', [StringComparison]::Ordinal)
    Require-True (($disposeIndex -ge 0) -and ($disposeIndex -lt $destroyedIndex)) 'DISPOSE_BEFORE_NO_FUTURE_BOUNDARY'
    Require-True (($destroyedIndex -ge 0) -and ($destroyedIndex -lt $finalizeIndex)) 'NO_FUTURE_BEFORE_FINAL_RELEASE_ATTEMPT'

    $callbackStart = $service.IndexOf('private static StatusArgs OnNativeOutput', [StringComparison]::Ordinal)
    $callbackEnd = $service.IndexOf('private void RegisterCallback', $callbackStart, [StringComparison]::Ordinal)
    Require-True (($callbackStart -ge 0) -and ($callbackEnd -gt $callbackStart)) 'CALLBACK_METHOD_BOUNDS'
    $callbackBody = $service.Substring($callbackStart, $callbackEnd - $callbackStart)
    $registryIndex = $callbackBody.IndexOf('lock (CallbackRegistryLock)', [StringComparison]::Ordinal)
    $enterIndex = $callbackBody.IndexOf('TryEnterCallbackLifetime()', [StringComparison]::Ordinal)
    $publicationIndex = $callbackBody.IndexOf('IsResultPublicationAccepting()', [StringComparison]::Ordinal)
    $finallyIndex = $callbackBody.IndexOf('finally', [StringComparison]::Ordinal)
    $exitIndex = $callbackBody.IndexOf('ExitCallbackLifetime()', [StringComparison]::Ordinal)
    Require-True (($registryIndex -ge 0) -and ($registryIndex -lt $enterIndex)) 'REGISTRY_LOOKUP_BEFORE_LIFETIME_ENTER'
    Require-True (($enterIndex -ge 0) -and ($enterIndex -lt $publicationIndex)) 'LIFETIME_ENTER_BEFORE_PUBLICATION_CHECK'
    Require-True (($publicationIndex -ge 0) -and ($publicationIndex -lt $finallyIndex)) 'PUBLICATION_CHECK_BEFORE_FINALLY'
    Require-True (($finallyIndex -ge 0) -and ($finallyIndex -lt $exitIndex)) 'LIFETIME_EXIT_IN_FINALLY'

    $releaseStart = $service.IndexOf('private static bool TryReleaseCallbackRoots', [StringComparison]::Ordinal)
    $releaseEnd = $service.IndexOf('private static int NextStreamId', $releaseStart, [StringComparison]::Ordinal)
    Require-True (($releaseStart -ge 0) -and ($releaseEnd -gt $releaseStart)) 'RELEASE_METHOD_BOUNDS'
    $releaseBody = $service.Substring($releaseStart, $releaseEnd - $releaseStart)
    $releaseGateIndex = $releaseBody.IndexOf('TryMarkCallbackRootsReleased()', [StringComparison]::Ordinal)
    $removeIndex = $releaseBody.IndexOf('CallbackRegistry.Remove', [StringComparison]::Ordinal)
    Require-True (($releaseGateIndex -ge 0) -and ($releaseGateIndex -lt $removeIndex)) 'DRAIN_AND_DESTROY_GATE_BEFORE_REGISTRY_REMOVE'

    foreach ($pattern in @(
        'Thread\.Sleep\s*\(',
        'SpinWait\s*\(',
        'WaitUntilIdle\s*\(',
        'WaitUntilDone\s*\(',
        'OutputStreamPoller',
        'Queue\s*<',
        'GraphicsFence',
        'UpdateExternalTexture\s*\('
    )) {
        Require-True (([regex]::Matches($service, $pattern)).Count -eq 0) ("FORBIDDEN_PATTERN_" + $pattern)
    }

    Require-True ($runner.Contains('private bool enableInferenceFaceGeometryTransactions = false;')) 'PRODUCTION_GATE_DEFAULT_OFF'
    Require-True (-not $scene.Contains('enableInferenceFaceGeometryTransactions')) 'SCENE_NOT_ACTIVATED'

    $expectedHashes = @{
        'Assets\Script\FaceLandmarkerRunner.cs' = '77708DF460584C0FCC041A345CE1736E7E6F4F5950A2D445565926058E662875'
        'Assets\Script\KiwiInferenceFaceTracker.cs' = '17F55087EFA7D42B14EF7453F35B0720EE79A23A64CB239391171C45241E3744'
        'Assets\Script\KiwiFaceMotion.cs' = 'D00D4C86FB79B7F9B9AE3CFE791D7A819449D24D27154B31FFDF45964D8650C6'
        'Assets\KiwiAvatarSystem\Runtime\TrackingFoundation\KiwiRuntimeGenerationContext.cs' = 'E74887450A8EBD881A3AEDE7B4789C5D83E4A7C5B7F5A6D4C427DB134D250C40'
        'Assets\KiwiAvatarSystem\Runtime\TrackingFoundation\KiwiTrackingProviderHub.cs' = 'A5C7DE387FA2E02862BCF64942363AECFB9C7830B52DF5B8953404DF884ECCB1'
        'Assets\KiwiAvatarSystem\Runtime\TrackingFoundation\KiwiCanonicalTrackingFrame.cs' = '458FF33E98E054B766F20EB17CE6BDD242C9AD9FD6E73AF53D60BD3961822AFA'
        'Assets\Scenes\Face Landmark Detection.unity' = '9BAC89F64B45429B38D41C1A2539C63D5E9A442E8EF920EE13B59245BE166735'
        'Packages\manifest.json' = '35F00A89D31A8BF7EE50718F38D4BAC90236D8B691F516C5FC14CAB47AFBFEE1'
        'Packages\packages-lock.json' = '23845C132DBF51CB53A5CFDE4EF606FA68D10DCB53AE5FF21F4305613A5F9CE1'
        'Packages\com.github.homuler.mediapipe-0.16.3.tgz' = 'CC3E77A219E0B99618AE3BE64C31A566197DEEDC69C1E136ACF52D65D7CF2E79'
        'Library\PackageCache\com.github.homuler.mediapipe@66127f8e750d\package.json' = 'EEBBFF4550FA9CA3C2C76AB05BD0EDBDB3A0A4C2800883E244DC9E40D8FF2A2D'
        'Library\PackageCache\com.github.homuler.mediapipe@66127f8e750d\Runtime\Plugins\mediapipe_c.dll' = 'B9B620F2707DEA371BE3FCEF9F2D05468CA1471FAEE3BE6637BBD7DA77E21C54'
    }

    foreach ($relativePath in $expectedHashes.Keys) {
        $actual = Get-Sha256 (Join-Path $ProjectRoot $relativePath)
        Require-True ($actual -eq $expectedHashes[$relativePath]) ("PROTECTED_SHA_" + $relativePath)
    }

    Require-True ($compileLog.Contains('Application will terminate with return code 0')) 'UNITY_COMPILE_LOG_EXIT_ZERO'
    Require-True (([regex]::Matches($compileLog, 'error CS\d+')).Count -eq 0) 'UNITY_COMPILE_ERROR_COUNT_ZERO'

    foreach ($line in @(
        'SYNTHETIC_LIFECYCLE_STRESS=PASS',
        'CALLBACK_ENTER_COUNT=8',
        'CALLBACK_EXIT_COUNT=8',
        'ACTIVE_CALLBACKS_FINAL=0',
        'POST_RETIRE_RESULT_PUBLICATION_COUNT=0',
        'PRE_DESTROY_REGISTRY_RELEASE_COUNT=0',
        'PRE_DESTROY_DELEGATE_RELEASE_COUNT=0',
        'PRE_DRAIN_REGISTRY_RELEASE_COUNT=0',
        'DOUBLE_RELEASE_COUNT=0',
        'NEGATIVE_ACTIVE_CALLBACK_COUNT=0',
        'INVALID_STATE_TRANSITION_COUNT=0',
        'GRAPH_RESOURCE_FINAL_LEAK_COUNT=0',
        'LIVE_CAMERA_RUNTIME=NOT_RUN',
        'P3D_RUNTIME=NOT_RUN',
        'H1=NOT_AUTHORIZED'
    )) {
        Require-True ($stress.Contains($line)) ("STRESS_" + $line)
    }

    $output = New-Object 'System.Collections.Generic.List[string]'
    $output.Add('P3C_1_STATIC_VALIDATION=PASS')
    $output.Add('POWERSHELL_VERSION=' + $PSVersionTable.PSVersion.ToString())
    $output.Add('CHECK_COUNT=' + $checks.Count)
    $output.AddRange($checks.ToArray())
    [IO.File]::WriteAllLines($resultPath, $output.ToArray(), (New-Object Text.UTF8Encoding($false)))
    Write-Output 'P3C_1_STATIC_VALIDATION=PASS'
    Write-Output ('CHECK_COUNT=' + $checks.Count)
    exit 0
}
catch {
    $output = @(
        'P3C_1_STATIC_VALIDATION=FAIL',
        ('ERROR=' + $_.Exception.ToString())
    )
    [IO.File]::WriteAllLines($resultPath, $output, (New-Object Text.UTF8Encoding($false)))
    Write-Error $_
    exit 1
}
