#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using UnityEngine;

/// <summary>
/// DIAGNOSTIC_ONLY aggregate observer for the H1 same-build Product gate.
/// It never owns or changes Product output. It observes existing identities,
/// counters, and private retained state only when KIWI_H1_RUNTIME is armed.
/// </summary>
[DefaultExecutionOrder(10000)]
internal sealed class KiwiH1ProductRuntimeObserver : MonoBehaviour
{
    private const string EnableEnvironment = "KIWI_H1_RUNTIME";
    private const string ResultPathEnvironment = "KIWI_H1_RESULT_PATH";
    private const string AutoQuitSecondsEnvironment = "KIWI_H1_AUTOQUIT_SECONDS";
    private const double MinimumAutoQuitSeconds = 10.0;
    private const double MaximumRunSeconds = 180.0;

    private static readonly BindingFlags InstancePrivate =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static bool _installed;

    private FaceLandmarkerRunner _runner;
    private KiwiFacePartRigidSampleFrame _rigid;
    private FieldInfo _hasAppliedField;
    private FieldInfo _lastAppliedFrameField;
    private FieldInfo _lastAppliedCameraField;
    private FieldInfo _lastAppliedSessionField;
    private FieldInfo _lastAppliedProviderField;
    private FieldInfo _lastAppliedModelField;
    private FieldInfo _textureInstanceField;
    private FieldInfo _leftSlotField;
    private FieldInfo _rightSlotField;
    private FieldInfo _mouthSlotField;
    private FieldInfo _serviceField;
    private FieldInfo _trackingInputWidthField;
    private FieldInfo _trackingInputHeightField;

    private string _resultPath;
    private double _started;
    private double _lastWrite;
    private double _autoQuitSeconds;
    private bool _quitting;
    private bool _initialStateCaptured;
    private bool _initialHasApplied;
    private ulong _initialFrameId;
    private bool _previousHasApplied;
    private ulong _previousAppliedFrameId;
    private ulong _lastAppliedFrameId;
    private long _lastAppliedSourceHostTicks;
    private int _lastAppliedCameraGeneration;
    private int _lastAppliedTrackingSessionGeneration;
    private int _lastAppliedProviderGeneration;
    private int _lastAppliedModelGeneration;
    private float _lastAppliedRotationDegrees;
    private int _previousLeftSlot = int.MinValue;
    private int _previousRightSlot = int.MinValue;
    private int _previousMouthSlot = int.MinValue;
    private long _pendingSemanticTimestamp = long.MinValue;
    private bool _pendingSemanticMatched;
    private long _lastObservedCommittedTimestamp = long.MinValue;
    private ulong _lastObservedCommittedCanonicalFrameId;
    private int _textureCommitBaseline;
    private int _textureMissBaseline;
    private int _semanticHoldBaseline;

    private long _pollCount;
    private long _runnerMissingPollCount;
    private long _rigidMissingPollCount;
    private long _bridgeOperationalPollCount;
    private long _bridgeNotOperationalPollCount;
    private long _canonicalSampleCount;
    private long _geometryGatePassCount;
    private long _geometryGateHoldCount;
    private long _textureCommitObservedCount;
    private long _leftEyeAdvanceCount;
    private long _rightEyeAdvanceCount;
    private long _mouthAdvanceCount;
    private long _appliedAdvanceCount;
    private long _appliedExactIdentityObservedCount;
    private long _appliedIdentityUnresolvedCount;
    private long _appliedDuplicateCount;
    private long _appliedOutOfOrderCount;
    private long _appliedGenerationMismatchCount;
    private long _appliedNonFiniteRotationCount;
    private long _resetAfterAppliedCount;
    private long _gateReasonRunnerStateCount;
    private long _gateReasonCanonicalFrameCount;
    private long _gateReasonRigidFrameCount;
    private long _gateReasonProviderSourceFrameIdCount;
    private long _gateReasonServiceSnapshotCount;
    private long _gateReasonFrameIdCount;
    private long _gateReasonSourceHostTicksCount;
    private long _gateReasonCameraGenerationCount;
    private long _gateReasonTrackingSessionGenerationCount;
    private long _gateReasonProviderGenerationCount;
    private long _gateReasonModelGenerationCount;
    private long _gateReasonBackendCount;
    private long _gateReasonDimensionsCount;
    private long _gateReasonUnknownCount;
    private ulong _lastCanonicalRigidFrameId;
    private ulong _lastCanonicalProviderSourceFrameId;
    private long _lastCanonicalRigidSourceHostTicks;
    private ulong _lastGeometrySnapshotFrameId;
    private long _lastGeometrySnapshotSourceHostTicks;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ArmAggregateProducerObserver()
    {
        if (IsEnabled())
        {
            KiwiFaceGeometryP3DDiagnostics.ResetAndEnable();
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_installed || !IsEnabled()) return;
        _installed = true;
        var host = new GameObject("[Kiwi] H1 Product Runtime Observer");
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiH1ProductRuntimeObserver>();
    }

    private static bool IsEnabled()
    {
        string value = Environment.GetEnvironmentVariable(EnableEnvironment);
        return Debug.isDebugBuild &&
            (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    private void Awake()
    {
        Application.runInBackground = true;
        _resultPath = ResolveResultPath();
        _started = Time.realtimeSinceStartupAsDouble;
        _lastWrite = _started;
        _autoQuitSeconds = ParseAutoQuitSeconds();
        CacheReflection();
        WriteResult(false);
        Debug.Log("[KiwiH1] DIAGNOSTIC_ONLY observer armed result=" + _resultPath);
    }

    private void LateUpdate()
    {
        ++_pollCount;
        ResolveRuntimeObjects();
        ObserveBridgeAndCanonical();
        ObserveTextureTransaction();
        ObserveRigidWriter();

        double now = Time.realtimeSinceStartupAsDouble;
        if (now - _lastWrite >= 2.0)
        {
            _lastWrite = now;
            WriteResult(false);
        }

        if (_autoQuitSeconds > 0.0 &&
            now - _started >= _autoQuitSeconds)
        {
            bool ready = HasMinimumRuntimeEvidence();
            Debug.Log("[KiwiH1] AUTOQUIT ready=" + (ready ? "1" : "0"));
            Application.Quit(ready ? 0 : 42);
        }
        else if (now - _started >= MaximumRunSeconds && _autoQuitSeconds > 0.0)
        {
            Application.Quit(43);
        }
    }

    private void OnApplicationQuit()
    {
        _quitting = true;
        FinalizePendingSemantic();
        WriteResult(true);
        KiwiFaceGeometryP3DDiagnostics.Disable();
    }

    private void OnDestroy()
    {
        if (!_quitting)
        {
            FinalizePendingSemantic();
            WriteResult(false);
            KiwiFaceGeometryP3DDiagnostics.Disable();
        }
    }

    private void CacheReflection()
    {
        Type rigidType = typeof(KiwiFacePartRigidSampleFrame);
        _hasAppliedField = rigidType.GetField("_hasAppliedIdentity", InstancePrivate);
        _lastAppliedFrameField = rigidType.GetField("_lastAppliedFrameId", InstancePrivate);
        _lastAppliedCameraField = rigidType.GetField("_lastAppliedCameraGeneration", InstancePrivate);
        _lastAppliedSessionField = rigidType.GetField("_lastAppliedTrackingSessionGeneration", InstancePrivate);
        _lastAppliedProviderField = rigidType.GetField("_lastAppliedProviderGeneration", InstancePrivate);
        _lastAppliedModelField = rigidType.GetField("_lastAppliedModelGeneration", InstancePrivate);

        Type textureType = typeof(KiwiFacePartTextureTransaction);
        _textureInstanceField = textureType.GetField(
            "_instance",
            BindingFlags.Static | BindingFlags.NonPublic);
        _leftSlotField = textureType.GetField("_leftSlot", InstancePrivate);
        _rightSlotField = textureType.GetField("_rightSlot", InstancePrivate);
        _mouthSlotField = textureType.GetField("_mouthSlot", InstancePrivate);

        Type runnerType = typeof(FaceLandmarkerRunner);
        _serviceField = runnerType.GetField(
            "_faceGeometryTransactionService",
            InstancePrivate);
        _trackingInputWidthField = runnerType.GetField(
            "_trackingInputWidth",
            InstancePrivate);
        _trackingInputHeightField = runnerType.GetField(
            "_trackingInputHeight",
            InstancePrivate);
    }

    private void ResolveRuntimeObjects()
    {
        if (_runner == null)
        {
            _runner = FindFirstObjectByType<FaceLandmarkerRunner>(
                FindObjectsInactive.Include);
        }
        if (_rigid == null)
        {
            _rigid = FindFirstObjectByType<KiwiFacePartRigidSampleFrame>(
                FindObjectsInactive.Include);
        }
    }

    private void ObserveBridgeAndCanonical()
    {
        if (_runner == null)
        {
            ++_runnerMissingPollCount;
            return;
        }

        if (_runner.IsFacePartGeometryBridgeOperational)
        {
            ++_bridgeOperationalPollCount;
        }
        else
        {
            ++_bridgeNotOperationalPollCount;
        }

        if (!KiwiCanonicalTrackingFrame.TryGetFrame(out KiwiTrackingFrame frame) ||
            !frame.isValid ||
            !frame.hasSemanticLandmarks ||
            frame.semanticTimestamp < 0L)
        {
            return;
        }

        if (frame.semanticTimestamp != _pendingSemanticTimestamp)
        {
            FinalizePendingSemantic();
            _pendingSemanticTimestamp = frame.semanticTimestamp;
            _pendingSemanticMatched = false;
            ++_canonicalSampleCount;
        }

        if (!_pendingSemanticMatched &&
            _runner.TryGetFacePartGeometrySnapshot(
                frame.semanticTimestamp,
                out _,
                out ulong canonicalFrameId) &&
            canonicalFrameId == frame.canonicalFrameId)
        {
            _pendingSemanticMatched = true;
            ++_geometryGatePassCount;
        }
        else if (!_pendingSemanticMatched)
        {
            RecordGeometryGateReason(frame);
        }
    }

    private void RecordGeometryGateReason(KiwiTrackingFrame frame)
    {
        if (!_runner.IsFacePartGeometryBridgeOperational)
        {
            ++_gateReasonRunnerStateCount;
            return;
        }
        if (!frame.isValid ||
            frame.canonicalFrameId == 0UL ||
            !frame.hasSemanticLandmarks)
        {
            ++_gateReasonCanonicalFrameCount;
            return;
        }
        if (!frame.rigid.isValid ||
            frame.rigid.timestamp != frame.semanticTimestamp ||
            frame.rigid.frameId == 0UL ||
            frame.rigid.backend != KiwiTrackingBackend.InferenceEngine ||
            !frame.rigid.hasMatchedSubmissionTiming ||
            frame.rigid.submissionHostTicks <= 0L)
        {
            ++_gateReasonRigidFrameCount;
            return;
        }

        _lastCanonicalRigidFrameId = frame.rigid.frameId;
        _lastCanonicalProviderSourceFrameId =
            frame.normalization.valid
                ? frame.normalization.providerSourceFrameId
                : 0UL;
        _lastCanonicalRigidSourceHostTicks = frame.rigid.submissionHostTicks;

        if (_lastCanonicalProviderSourceFrameId == 0UL)
        {
            ++_gateReasonProviderSourceFrameIdCount;
            return;
        }

        KiwiFaceGeometryTransactionService service =
            _serviceField != null
                ? _serviceField.GetValue(_runner) as KiwiFaceGeometryTransactionService
                : null;
        if (service == null || !service.TryGetAcceptedSnapshot(out var snapshot))
        {
            ++_gateReasonServiceSnapshotCount;
            return;
        }

        _lastGeometrySnapshotFrameId = snapshot.frameId;
        _lastGeometrySnapshotSourceHostTicks = snapshot.sourceHostTicks;
        if (snapshot.frameId != _lastCanonicalProviderSourceFrameId)
        {
            ++_gateReasonFrameIdCount;
            return;
        }
        if (snapshot.sourceHostTicks != frame.rigid.submissionHostTicks)
        {
            ++_gateReasonSourceHostTicksCount;
            return;
        }
        if (snapshot.cameraGeneration != frame.generation.cameraGeneration)
        {
            ++_gateReasonCameraGenerationCount;
            return;
        }
        if (snapshot.trackingSessionGeneration != frame.generation.trackingSessionGeneration)
        {
            ++_gateReasonTrackingSessionGenerationCount;
            return;
        }
        if (snapshot.providerGeneration != frame.generation.providerGeneration)
        {
            ++_gateReasonProviderGenerationCount;
            return;
        }
        if (snapshot.modelGeneration != frame.generation.modelGeneration)
        {
            ++_gateReasonModelGenerationCount;
            return;
        }
        if (snapshot.backend != frame.rigid.backend)
        {
            ++_gateReasonBackendCount;
            return;
        }

        int width = _trackingInputWidthField != null
            ? (int)_trackingInputWidthField.GetValue(_runner)
            : 0;
        int height = _trackingInputHeightField != null
            ? (int)_trackingInputHeightField.GetValue(_runner)
            : 0;
        if (snapshot.semanticFrameWidth != width ||
            snapshot.semanticFrameHeight != height)
        {
            ++_gateReasonDimensionsCount;
            return;
        }
        ++_gateReasonUnknownCount;
    }

    private void FinalizePendingSemantic()
    {
        if (_pendingSemanticTimestamp != long.MinValue && !_pendingSemanticMatched)
        {
            ++_geometryGateHoldCount;
            _pendingSemanticMatched = true;
        }
    }

    private void ObserveTextureTransaction()
    {
        if (!_initialStateCaptured && KiwiFacePartTextureTransaction.IsOperational)
        {
            _textureCommitBaseline = KiwiFacePartTextureTransaction.TransactionCommitCount;
            _textureMissBaseline = KiwiFacePartTextureTransaction.TransactionMissCount;
            _semanticHoldBaseline = KiwiFacePartTextureTransaction.SemanticHoldCount;
        }

        long committedTimestamp =
            KiwiFacePartTextureTransaction.LastCommittedSemanticTimestamp;
        ulong committedCanonicalFrameId =
            KiwiFacePartTextureTransaction.LastCommittedCanonicalFrameId;
        if (committedTimestamp >= 0L &&
            (committedTimestamp != _lastObservedCommittedTimestamp ||
             committedCanonicalFrameId != _lastObservedCommittedCanonicalFrameId))
        {
            ++_textureCommitObservedCount;
            _lastObservedCommittedTimestamp = committedTimestamp;
            _lastObservedCommittedCanonicalFrameId = committedCanonicalFrameId;
        }

        object texture = _textureInstanceField != null
            ? _textureInstanceField.GetValue(null)
            : null;
        if (texture == null) return;

        ObserveSlot(_leftSlotField, texture, ref _previousLeftSlot, ref _leftEyeAdvanceCount);
        ObserveSlot(_rightSlotField, texture, ref _previousRightSlot, ref _rightEyeAdvanceCount);
        ObserveSlot(_mouthSlotField, texture, ref _previousMouthSlot, ref _mouthAdvanceCount);
    }

    private static void ObserveSlot(
        FieldInfo field,
        object owner,
        ref int previous,
        ref long advances)
    {
        if (field == null) return;
        int current = (int)field.GetValue(owner);
        if (previous == int.MinValue)
        {
            previous = current;
            return;
        }
        if (current != previous)
        {
            if (current >= 0) ++advances;
            previous = current;
        }
    }

    private void ObserveRigidWriter()
    {
        if (_rigid == null)
        {
            ++_rigidMissingPollCount;
            return;
        }
        if (_hasAppliedField == null || _lastAppliedFrameField == null) return;

        bool hasApplied = (bool)_hasAppliedField.GetValue(_rigid);
        ulong frameId = (ulong)_lastAppliedFrameField.GetValue(_rigid);

        if (!_initialStateCaptured)
        {
            _initialStateCaptured = true;
            _initialHasApplied = hasApplied;
            _initialFrameId = frameId;
            _previousHasApplied = hasApplied;
            _previousAppliedFrameId = frameId;
        }

        if (_previousHasApplied && !hasApplied)
        {
            ++_resetAfterAppliedCount;
        }

        if (hasApplied && frameId != _previousAppliedFrameId)
        {
            if (frameId == _previousAppliedFrameId)
            {
                ++_appliedDuplicateCount;
            }
            else if (frameId < _previousAppliedFrameId)
            {
                ++_appliedOutOfOrderCount;
            }
            else
            {
                ++_appliedAdvanceCount;
            }

            _lastAppliedFrameId = frameId;
            _lastAppliedCameraGeneration = ReadInt(_lastAppliedCameraField, _rigid);
            _lastAppliedTrackingSessionGeneration = ReadInt(_lastAppliedSessionField, _rigid);
            _lastAppliedProviderGeneration = ReadInt(_lastAppliedProviderField, _rigid);
            _lastAppliedModelGeneration = ReadInt(_lastAppliedModelField, _rigid);
            _lastAppliedRotationDegrees = KiwiFacePartRigidSampleFrame.AppliedRotationDegrees;

            if (float.IsNaN(_lastAppliedRotationDegrees) ||
                float.IsInfinity(_lastAppliedRotationDegrees))
            {
                ++_appliedNonFiniteRotationCount;
            }

            KiwiRuntimeGenerationContext.Snapshot current =
                KiwiRuntimeGenerationContext.Capture();
            if (current.cameraGeneration != _lastAppliedCameraGeneration ||
                current.trackingSessionGeneration != _lastAppliedTrackingSessionGeneration ||
                current.providerGeneration != _lastAppliedProviderGeneration ||
                current.modelGeneration != _lastAppliedModelGeneration)
            {
                ++_appliedGenerationMismatchCount;
            }

            long committedTimestamp =
                KiwiFacePartTextureTransaction.LastCommittedSemanticTimestamp;
            ulong committedCanonicalFrameId =
                KiwiFacePartTextureTransaction.LastCommittedCanonicalFrameId;
            if (_runner != null &&
                committedTimestamp >= 0L &&
                _runner.TryGetFacePartGeometrySnapshot(
                    committedTimestamp,
                    out KiwiFaceGeometryTransactionService.AcceptedSnapshot snapshot,
                    out ulong canonicalFrameId) &&
                snapshot.frameId == frameId &&
                canonicalFrameId == committedCanonicalFrameId)
            {
                ++_appliedExactIdentityObservedCount;
                _lastAppliedSourceHostTicks = snapshot.sourceHostTicks;
            }
            else
            {
                ++_appliedIdentityUnresolvedCount;
            }
        }

        _previousHasApplied = hasApplied;
        _previousAppliedFrameId = frameId;
    }

    private static int ReadInt(FieldInfo field, object owner)
    {
        return field != null ? (int)field.GetValue(owner) : int.MinValue;
    }

    private bool HasMinimumRuntimeEvidence()
    {
        KiwiFaceGeometryP3DDiagnostics.Snapshot producer =
            KiwiFaceGeometryP3DDiagnostics.Capture();
        return _runner != null &&
            _rigid != null &&
            _bridgeOperationalPollCount > 0L &&
            producer.acceptedCount >= 10L &&
            _geometryGatePassCount >= 5L &&
            _textureCommitObservedCount >= 5L &&
            _appliedAdvanceCount >= 5L &&
            _leftEyeAdvanceCount > 0L &&
            _rightEyeAdvanceCount > 0L &&
            _mouthAdvanceCount > 0L &&
            _appliedOutOfOrderCount == 0L &&
            _appliedGenerationMismatchCount == 0L &&
            _appliedNonFiniteRotationCount == 0L &&
            producer.duplicateAcceptedPublishCount == 0L &&
            producer.outOfOrderAcceptedPublishCount == 0L &&
            producer.staleGenerationAcceptedPublishCount == 0L &&
            producer.mixedEpochAcceptedPublishCount == 0L &&
            producer.oldRunToNewRunPublicationCount == 0L;
    }

    private void WriteResult(bool shutdownObserved)
    {
        if (string.IsNullOrEmpty(_resultPath)) return;
        KiwiFaceGeometryP3DDiagnostics.Snapshot producer =
            KiwiFaceGeometryP3DDiagnostics.Capture();
        string status = HasMinimumRuntimeEvidence() ? "PASS" : "PENDING_OR_FAIL";
        string firstBoundary = DetermineFirstBoundary(producer);
        var text = new StringBuilder();
        text.AppendLine("KIWI_H1_SAME_BUILD_PRODUCT_RUNTIME_EVIDENCE");
        text.AppendLine("UTC=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        text.AppendLine("UNITY_VERSION=" + Application.unityVersion);
        text.AppendLine("DEBUG_BUILD=" + (Debug.isDebugBuild ? "1" : "0"));
        text.AppendLine("GRAPHICS_API=" + SystemInfo.graphicsDeviceType);
        text.AppendLine("SCENE=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
        text.AppendLine("DIAGNOSTIC_ONLY=1");
        text.AppendLine("PERFORMANCE_AUTHORITY=NONE");
        text.AppendLine("HUMAN_VISUAL=PENDING_USER_REVIEW");
        text.AppendLine("PLAYER_CLOSED=" + (shutdownObserved ? "YES" : "NO"));
        text.AppendLine("RUNTIME_AGGREGATE_RESULT=" + status);
        text.AppendLine("FIRST_FAILING_BOUNDARY=" + firstBoundary);
        text.AppendLine("ELAPSED_SECONDS=" + (Time.realtimeSinceStartupAsDouble - _started).ToString("F3", CultureInfo.InvariantCulture));
        text.AppendLine("POLL_COUNT=" + _pollCount);
        text.AppendLine("RUNNER_MISSING_POLLS=" + _runnerMissingPollCount);
        text.AppendLine("RIGID_WRITER_MISSING_POLLS=" + _rigidMissingPollCount);
        text.AppendLine("BRIDGE_OPERATIONAL_POLLS=" + _bridgeOperationalPollCount);
        text.AppendLine("BRIDGE_NOT_OPERATIONAL_POLLS=" + _bridgeNotOperationalPollCount);
        text.AppendLine("CANONICAL_SAMPLE_COUNT=" + _canonicalSampleCount);
        text.AppendLine("GEOMETRY_GATE_PASS_COUNT=" + _geometryGatePassCount);
        text.AppendLine("GEOMETRY_GATE_HOLD_COUNT=" + _geometryGateHoldCount);
        text.AppendLine("TEXTURE_COMMIT_OBSERVED_COUNT=" + _textureCommitObservedCount);
        text.AppendLine("TEXTURE_COMMIT_COUNTER_DELTA=" + Math.Max(0, KiwiFacePartTextureTransaction.TransactionCommitCount - _textureCommitBaseline));
        text.AppendLine("TEXTURE_MISS_COUNTER_DELTA=" + Math.Max(0, KiwiFacePartTextureTransaction.TransactionMissCount - _textureMissBaseline));
        text.AppendLine("SEMANTIC_HOLD_COUNTER_DELTA=" + Math.Max(0, KiwiFacePartTextureTransaction.SemanticHoldCount - _semanticHoldBaseline));
        text.AppendLine("LEFT_EYE_TEXTURE_ADVANCE_COUNT=" + _leftEyeAdvanceCount);
        text.AppendLine("RIGHT_EYE_TEXTURE_ADVANCE_COUNT=" + _rightEyeAdvanceCount);
        text.AppendLine("MOUTH_TEXTURE_ADVANCE_COUNT=" + _mouthAdvanceCount);
        text.AppendLine("APPLIED_ADVANCE_COUNT=" + _appliedAdvanceCount);
        text.AppendLine("APPLIED_EXACT_IDENTITY_OBSERVED_COUNT=" + _appliedExactIdentityObservedCount);
        text.AppendLine("APPLIED_IDENTITY_UNRESOLVED_OBSERVER_COUNT=" + _appliedIdentityUnresolvedCount);
        text.AppendLine("APPLIED_DUPLICATE_COUNT=" + _appliedDuplicateCount);
        text.AppendLine("APPLIED_OUT_OF_ORDER_COUNT=" + _appliedOutOfOrderCount);
        text.AppendLine("APPLIED_GENERATION_MISMATCH_COUNT=" + _appliedGenerationMismatchCount);
        text.AppendLine("APPLIED_NONFINITE_ROTATION_COUNT=" + _appliedNonFiniteRotationCount);
        text.AppendLine("RESET_AFTER_APPLIED_COUNT=" + _resetAfterAppliedCount);
        text.AppendLine("GATE_REASON_RUNNER_STATE_POLLS=" + _gateReasonRunnerStateCount);
        text.AppendLine("GATE_REASON_CANONICAL_FRAME_POLLS=" + _gateReasonCanonicalFrameCount);
        text.AppendLine("GATE_REASON_RIGID_FRAME_POLLS=" + _gateReasonRigidFrameCount);
        text.AppendLine("GATE_REASON_PROVIDER_SOURCE_FRAME_ID_POLLS=" + _gateReasonProviderSourceFrameIdCount);
        text.AppendLine("GATE_REASON_SERVICE_SNAPSHOT_POLLS=" + _gateReasonServiceSnapshotCount);
        text.AppendLine("GATE_REASON_FRAME_ID_POLLS=" + _gateReasonFrameIdCount);
        text.AppendLine("GATE_REASON_SOURCE_HOST_TICKS_POLLS=" + _gateReasonSourceHostTicksCount);
        text.AppendLine("GATE_REASON_CAMERA_GENERATION_POLLS=" + _gateReasonCameraGenerationCount);
        text.AppendLine("GATE_REASON_TRACKING_SESSION_GENERATION_POLLS=" + _gateReasonTrackingSessionGenerationCount);
        text.AppendLine("GATE_REASON_PROVIDER_GENERATION_POLLS=" + _gateReasonProviderGenerationCount);
        text.AppendLine("GATE_REASON_MODEL_GENERATION_POLLS=" + _gateReasonModelGenerationCount);
        text.AppendLine("GATE_REASON_BACKEND_POLLS=" + _gateReasonBackendCount);
        text.AppendLine("GATE_REASON_DIMENSIONS_POLLS=" + _gateReasonDimensionsCount);
        text.AppendLine("GATE_REASON_UNKNOWN_POLLS=" + _gateReasonUnknownCount);
        text.AppendLine("LAST_CANONICAL_RIGID_FRAME_ID=" + _lastCanonicalRigidFrameId);
        text.AppendLine("LAST_CANONICAL_PROVIDER_SOURCE_FRAME_ID=" + _lastCanonicalProviderSourceFrameId);
        text.AppendLine("LAST_CANONICAL_RIGID_SOURCE_HOST_TICKS=" + _lastCanonicalRigidSourceHostTicks);
        text.AppendLine("LAST_GEOMETRY_SNAPSHOT_FRAME_ID=" + _lastGeometrySnapshotFrameId);
        text.AppendLine("LAST_GEOMETRY_SNAPSHOT_SOURCE_HOST_TICKS=" + _lastGeometrySnapshotSourceHostTicks);
        text.AppendLine("INITIAL_HAS_APPLIED_IDENTITY=" + (_initialHasApplied ? "1" : "0"));
        text.AppendLine("INITIAL_APPLIED_FRAME_ID=" + _initialFrameId);
        text.AppendLine("LAST_APPLIED_FRAME_ID=" + _lastAppliedFrameId);
        text.AppendLine("LAST_APPLIED_SOURCE_HOST_TICKS=" + _lastAppliedSourceHostTicks);
        text.AppendLine("LAST_APPLIED_CAMERA_GENERATION=" + _lastAppliedCameraGeneration);
        text.AppendLine("LAST_APPLIED_TRACKING_SESSION_GENERATION=" + _lastAppliedTrackingSessionGeneration);
        text.AppendLine("LAST_APPLIED_PROVIDER_GENERATION=" + _lastAppliedProviderGeneration);
        text.AppendLine("LAST_APPLIED_MODEL_GENERATION=" + _lastAppliedModelGeneration);
        text.AppendLine("LAST_APPLIED_ROTATION_DEGREES=" + _lastAppliedRotationDegrees.ToString("R", CultureInfo.InvariantCulture));
        AppendProducer(text, producer);

        string temporary = _resultPath + ".tmp";
        File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
        if (File.Exists(_resultPath)) File.Delete(_resultPath);
        File.Move(temporary, _resultPath);
    }

    private string DetermineFirstBoundary(
        KiwiFaceGeometryP3DDiagnostics.Snapshot producer)
    {
        if (_runner == null) return "RUNNER_NOT_FOUND";
        if (_rigid == null) return "RIGID_WRITER_NOT_FOUND";
        if (_bridgeOperationalPollCount == 0L) return "PRODUCT_BRIDGE_ACTIVATION";
        if (producer.acceptedCount == 0L) return "LIVE_CAMERA_OR_GEOMETRY_LIVENESS";
        if (_geometryGatePassCount == 0L) return "CANONICAL_TO_GEOMETRY_IDENTITY_GATE";
        if (_textureCommitObservedCount == 0L) return "TEXTURE_TRANSACTION_COMMIT";
        if (_appliedAdvanceCount == 0L) return "FACEPART_ROLL_APPLICATION";
        if (_leftEyeAdvanceCount == 0L || _rightEyeAdvanceCount == 0L || _mouthAdvanceCount == 0L) return "PER_PART_TEXTURE_ADVANCEMENT";
        if (_appliedOutOfOrderCount != 0L) return "APPLIED_OUT_OF_ORDER";
        if (_appliedGenerationMismatchCount != 0L) return "APPLIED_STALE_GENERATION";
        if (_appliedNonFiniteRotationCount != 0L) return "APPLIED_ROTATION_FINITE";
        if (producer.duplicateAcceptedPublishCount != 0L) return "DUPLICATE_ACCEPTED_PUBLISH";
        if (producer.outOfOrderAcceptedPublishCount != 0L) return "OUT_OF_ORDER_ACCEPTED_PUBLISH";
        if (producer.staleGenerationAcceptedPublishCount != 0L) return "STALE_GENERATION_ACCEPTED_PUBLISH";
        if (producer.mixedEpochAcceptedPublishCount != 0L) return "MIXED_EPOCH_ACCEPTED_PUBLISH";
        if (producer.oldRunToNewRunPublicationCount != 0L) return "OLD_RUN_TO_NEW_RUN_PUBLICATION";
        return HasMinimumRuntimeEvidence() ? "NONE" : "MINIMUM_RUNTIME_COVERAGE";
    }

    private static void AppendProducer(
        StringBuilder text,
        KiwiFaceGeometryP3DDiagnostics.Snapshot value)
    {
        text.AppendLine("GEOMETRY_ADMISSION_COUNT=" + value.admissionCount);
        text.AppendLine("GEOMETRY_SUBMITTED_COUNT=" + value.submittedCount);
        text.AppendLine("GEOMETRY_ACCEPTED_COUNT=" + value.acceptedCount);
        text.AppendLine("GEOMETRY_MAX_IN_FLIGHT=" + value.maxInFlight);
        text.AppendLine("GEOMETRY_MAX_PENDING=" + value.maxPending);
        text.AppendLine("GEOMETRY_DUPLICATE_ACCEPTED_PUBLISH=" + value.duplicateAcceptedPublishCount);
        text.AppendLine("GEOMETRY_OUT_OF_ORDER_ACCEPTED_PUBLISH=" + value.outOfOrderAcceptedPublishCount);
        text.AppendLine("GEOMETRY_STALE_GENERATION_ACCEPTED_PUBLISH=" + value.staleGenerationAcceptedPublishCount);
        text.AppendLine("GEOMETRY_MIXED_EPOCH_ACCEPTED_PUBLISH=" + value.mixedEpochAcceptedPublishCount);
        text.AppendLine("GEOMETRY_OLD_RUN_TO_NEW_RUN_PUBLICATION=" + value.oldRunToNewRunPublicationCount);
        text.AppendLine("GEOMETRY_COMPLETION_IDENTITY_MISMATCH=" + value.completionIdentityMismatchCount);
        text.AppendLine("GEOMETRY_FIRST_FRAME_ID=" + (value.firstAccepted.exists ? value.firstAccepted.frameId : 0UL));
        text.AppendLine("GEOMETRY_LAST_FRAME_ID=" + (value.lastAccepted.exists ? value.lastAccepted.frameId : 0UL));
        text.AppendLine("GEOMETRY_LAST_SOURCE_HOST_TICKS=" + (value.lastAccepted.exists ? value.lastAccepted.sourceHostTicks : 0L));
        text.AppendLine("GEOMETRY_LAST_CAMERA_GENERATION=" + (value.lastAccepted.exists ? value.lastAccepted.cameraGeneration : 0));
        text.AppendLine("GEOMETRY_LAST_TRACKING_SESSION_GENERATION=" + (value.lastAccepted.exists ? value.lastAccepted.trackingSessionGeneration : 0));
        text.AppendLine("GEOMETRY_LAST_PROVIDER_GENERATION=" + (value.lastAccepted.exists ? value.lastAccepted.providerGeneration : 0));
        text.AppendLine("GEOMETRY_LAST_MODEL_GENERATION=" + (value.lastAccepted.exists ? value.lastAccepted.modelGeneration : 0));
        text.AppendLine("GEOMETRY_LAST_BACKEND=" + (value.lastAccepted.exists ? value.lastAccepted.backend : "NONE"));
        text.AppendLine("GEOMETRY_LAST_SEMANTIC_WIDTH=" + (value.lastAccepted.exists ? value.lastAccepted.semanticFrameWidth : 0));
        text.AppendLine("GEOMETRY_LAST_SEMANTIC_HEIGHT=" + (value.lastAccepted.exists ? value.lastAccepted.semanticFrameHeight : 0));
    }

    private static string ResolveResultPath()
    {
        string path = Environment.GetEnvironmentVariable(ResultPathEnvironment);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(
                Application.persistentDataPath,
                "KiwiH1_Product_Runtime_Result.txt");
        }
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("H1 result path has no directory.");
        }
        Directory.CreateDirectory(directory);
        return path;
    }

    private static double ParseAutoQuitSeconds()
    {
        string value = Environment.GetEnvironmentVariable(AutoQuitSecondsEnvironment);
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double seconds))
        {
            return 0.0;
        }
        return Math.Max(MinimumAutoQuitSeconds, Math.Min(MaximumRunSeconds, seconds));
    }
}
#endif
