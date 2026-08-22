using System;
using UnityEngine;

/// <summary>
/// Calibration domains that can change independently while sharing one global
/// CalibrationGeneration identity.
///
/// A generation event records the affected scope. Consumers therefore discard
/// only calibration work whose own domain changed; an ActorFace recalibration
/// does not invalidate an in-progress RootPose solve, for example.
/// </summary>
[Flags]
public enum KiwiCalibrationScope
{
    None = 0,
    RootPose = 1 << 0,
    ActorFace = 1 << 1,
    ModelFaceParts = 1 << 2,
    Attachments = 1 << 3,
    All = RootPose | ActorFace | ModelFaceParts | Attachments
}

/// <summary>
/// v5.1 Phase 7 calibration-generation transaction manager.
///
/// KiwiRuntimeGenerationContext remains the canonical monotonic counter owner.
/// This layer adds scope, reason and transaction semantics so one logical
/// recalibration cannot accidentally advance the generation several times just
/// because several participating components expose their own Recalibrate API.
/// </summary>
public static class KiwiCalibrationGeneration
{
    private const int HistoryCapacity = 64;

    private struct GenerationEvent
    {
        public int generation;
        public KiwiCalibrationScope scope;
        public string reason;
        public int unityFrame;
    }

    public sealed class Transaction : IDisposable
    {
        private bool _disposed;

        internal Transaction(int generation)
        {
            Generation = generation;
        }

        public int Generation { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            EndTransaction();
        }
    }

    private static readonly object Sync = new object();
    private static readonly GenerationEvent[] History =
        new GenerationEvent[HistoryCapacity];

    private static int _historyWriteIndex;
    private static int _historyCount;
    private static int _lastManagedGeneration = 1;

    private static int _transactionDepth;
    private static int _transactionGeneration;
    private static KiwiCalibrationScope _transactionScope;
    private static string _transactionReason = string.Empty;

    private static int _eventCount;
    private static int _commitCount;
    private static int _rejectedCommitCount;
    private static string _lastCommitOwner = string.Empty;
    private static int _lastCommitGeneration;

    public static int CurrentGeneration =>
        KiwiRuntimeGenerationContext.CalibrationGeneration;

    public static int EventCount
    {
        get
        {
            lock (Sync)
            {
                SynchronizeExternalAdvanceLocked();
                return _eventCount;
            }
        }
    }

    public static int CommitCount
    {
        get
        {
            lock (Sync)
            {
                return _commitCount;
            }
        }
    }

    public static int RejectedCommitCount
    {
        get
        {
            lock (Sync)
            {
                return _rejectedCommitCount;
            }
        }
    }

    public static string LastReason
    {
        get
        {
            lock (Sync)
            {
                SynchronizeExternalAdvanceLocked();

                if (_historyCount <= 0)
                {
                    return "-";
                }

                int index =
                    (_historyWriteIndex - 1 + HistoryCapacity) %
                    HistoryCapacity;

                return string.IsNullOrEmpty(History[index].reason)
                    ? "-"
                    : History[index].reason;
            }
        }
    }

    public static KiwiCalibrationScope LastScope
    {
        get
        {
            lock (Sync)
            {
                SynchronizeExternalAdvanceLocked();

                if (_historyCount <= 0)
                {
                    return KiwiCalibrationScope.None;
                }

                int index =
                    (_historyWriteIndex - 1 + HistoryCapacity) %
                    HistoryCapacity;

                return History[index].scope;
            }
        }
    }

    public static string LastCommitOwner
    {
        get
        {
            lock (Sync)
            {
                return string.IsNullOrEmpty(_lastCommitOwner)
                    ? "-"
                    : _lastCommitOwner;
            }
        }
    }

    public static int LastCommitGeneration
    {
        get
        {
            lock (Sync)
            {
                return _lastCommitGeneration;
            }
        }
    }

    public static int ActiveTransactionDepth
    {
        get
        {
            lock (Sync)
            {
                return _transactionDepth;
            }
        }
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetRuntimeState()
    {
        lock (Sync)
        {
            Array.Clear(History, 0, History.Length);
            _historyWriteIndex = 0;
            _historyCount = 0;
            // KiwiRuntimeGenerationContext resets every generation counter to 1
            // in BeforeSceneLoad as well. Use the same deterministic baseline so
            // runtime-initialize callback ordering cannot fabricate an external
            // calibration event when Domain Reload is disabled.
            _lastManagedGeneration = 1;

            _transactionDepth = 0;
            _transactionGeneration = 0;
            _transactionScope = KiwiCalibrationScope.None;
            _transactionReason = string.Empty;

            _eventCount = 0;
            _commitCount = 0;
            _rejectedCommitCount = 0;
            _lastCommitOwner = string.Empty;
            _lastCommitGeneration = 0;
        }
    }

    /// <summary>
    /// Starts one logical multi-component calibration transaction. Nested
    /// participants join the same generation and extend its scope.
    /// </summary>
    public static Transaction BeginTransaction(
        KiwiCalibrationScope scope,
        string reason)
    {
        scope = NormalizeScope(scope);
        reason = NormalizeReason(reason);

        lock (Sync)
        {
            SynchronizeExternalAdvanceLocked();

            if (_transactionDepth <= 0)
            {
                _transactionGeneration = AdvanceLocked(scope, reason);
                _transactionScope = scope;
                _transactionReason = reason;
            }
            else
            {
                _transactionScope |= scope;
                _transactionReason = MergeReason(
                    _transactionReason,
                    reason);

                UpdateGenerationEventLocked(
                    _transactionGeneration,
                    _transactionScope,
                    _transactionReason);
            }

            _transactionDepth++;

            return new Transaction(
                _transactionGeneration);
        }
    }

    /// <summary>
    /// Used by individual public Recalibrate APIs. Inside a parent transaction
    /// the caller joins it; otherwise a new generation is started exactly once.
    /// </summary>
    public static int BeginOrJoin(
        KiwiCalibrationScope scope,
        string reason)
    {
        scope = NormalizeScope(scope);
        reason = NormalizeReason(reason);

        lock (Sync)
        {
            SynchronizeExternalAdvanceLocked();

            if (_transactionDepth > 0)
            {
                _transactionScope |= scope;
                _transactionReason = MergeReason(
                    _transactionReason,
                    reason);

                UpdateGenerationEventLocked(
                    _transactionGeneration,
                    _transactionScope,
                    _transactionReason);

                return _transactionGeneration;
            }

            return AdvanceLocked(scope, reason);
        }
    }

    /// <summary>
    /// Returns true only when this consumer's calibration domain changed after
    /// the supplied generation. Unrelated calibration scopes do not poison a
    /// valid collection in progress.
    /// </summary>
    public static bool HasScopeChangedSince(
        int generation,
        KiwiCalibrationScope scope)
    {
        scope = NormalizeScope(scope);

        lock (Sync)
        {
            SynchronizeExternalAdvanceLocked();
            return HasScopeChangedSinceLocked(
                generation,
                scope);
        }
    }

    /// <summary>
    /// Records a completed calibration only if no newer generation affecting the
    /// same scope started while samples were being collected.
    /// </summary>
    public static bool TryRecordCommit(
        string owner,
        int generation,
        KiwiCalibrationScope scope)
    {
        scope = NormalizeScope(scope);

        lock (Sync)
        {
            SynchronizeExternalAdvanceLocked();

            if (HasScopeChangedSinceLocked(
                generation,
                scope))
            {
                _rejectedCommitCount++;
                return false;
            }

            _commitCount++;
            _lastCommitOwner =
                string.IsNullOrEmpty(owner)
                    ? "Unknown"
                    : owner;
            _lastCommitGeneration = generation;
            return true;
        }
    }

    private static void EndTransaction()
    {
        lock (Sync)
        {
            if (_transactionDepth <= 0)
            {
                return;
            }

            _transactionDepth--;

            if (_transactionDepth > 0)
            {
                return;
            }

            _transactionGeneration = 0;
            _transactionScope = KiwiCalibrationScope.None;
            _transactionReason = string.Empty;
        }
    }

    private static int AdvanceLocked(
        KiwiCalibrationScope scope,
        string reason)
    {
        int generation =
            KiwiRuntimeGenerationContext.
                AdvanceCalibrationGeneration();

        _lastManagedGeneration = generation;
        RecordGenerationEventLocked(
            generation,
            scope,
            reason);
        return generation;
    }

    private static void SynchronizeExternalAdvanceLocked()
    {
        int current =
            KiwiRuntimeGenerationContext.CalibrationGeneration;

        if (current == _lastManagedGeneration)
        {
            return;
        }

        if (current < _lastManagedGeneration)
        {
            Array.Clear(History, 0, History.Length);
            _historyWriteIndex = 0;
            _historyCount = 0;
            _transactionDepth = 0;
            _transactionGeneration = 0;
            _transactionScope = KiwiCalibrationScope.None;
            _transactionReason = string.Empty;
        }

        _lastManagedGeneration = current;

        // Unknown direct increments are intentionally conservative. This makes
        // legacy/future code safe until it is migrated to the scoped API. If one
        // occurs during a managed transaction, move the transaction identity to
        // that newer all-scope generation rather than letting nested callers join
        // a stale generation number.
        RecordGenerationEventLocked(
            current,
            KiwiCalibrationScope.All,
            "ExternalAdvance");

        if (_transactionDepth > 0)
        {
            _transactionGeneration = current;
            _transactionScope = KiwiCalibrationScope.All;
            _transactionReason = MergeReason(
                _transactionReason,
                "ExternalAdvance");
        }
    }

    private static bool HasScopeChangedSinceLocked(
        int generation,
        KiwiCalibrationScope scope)
    {
        int current =
            KiwiRuntimeGenerationContext.CalibrationGeneration;

        if (generation <= 0 || current < generation)
        {
            return true;
        }

        if (generation == current)
        {
            return false;
        }

        if (_historyCount <= 0)
        {
            return true;
        }

        int oldestIndex =
            (_historyWriteIndex - _historyCount + HistoryCapacity) %
            HistoryCapacity;

        int oldestGeneration =
            History[oldestIndex].generation;

        if (
            _historyCount >= HistoryCapacity &&
            generation < oldestGeneration)
        {
            return true;
        }

        for (int i = 0; i < _historyCount; i++)
        {
            int index =
                (oldestIndex + i) %
                HistoryCapacity;

            GenerationEvent entry = History[index];

            if (
                entry.generation > generation &&
                (entry.scope & scope) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void RecordGenerationEventLocked(
        int generation,
        KiwiCalibrationScope scope,
        string reason)
    {
        History[_historyWriteIndex] =
            new GenerationEvent
            {
                generation = generation,
                scope = scope,
                reason = reason,
                unityFrame = Time.frameCount
            };

        _historyWriteIndex =
            (_historyWriteIndex + 1) %
            HistoryCapacity;

        _historyCount =
            Mathf.Min(
                HistoryCapacity,
                _historyCount + 1);

        _eventCount++;
    }

    private static void UpdateGenerationEventLocked(
        int generation,
        KiwiCalibrationScope scope,
        string reason)
    {
        if (_historyCount <= 0)
        {
            RecordGenerationEventLocked(
                generation,
                scope,
                reason);
            return;
        }

        int index =
            (_historyWriteIndex - 1 + HistoryCapacity) %
            HistoryCapacity;

        if (History[index].generation != generation)
        {
            RecordGenerationEventLocked(
                generation,
                scope,
                reason);
            return;
        }

        GenerationEvent entry = History[index];
        entry.scope = scope;
        entry.reason = reason;
        History[index] = entry;
    }

    private static KiwiCalibrationScope NormalizeScope(
        KiwiCalibrationScope scope)
    {
        return scope == KiwiCalibrationScope.None
            ? KiwiCalibrationScope.All
            : scope;
    }

    private static string NormalizeReason(
        string reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? "Calibration"
            : reason.Trim();
    }

    private static string MergeReason(
        string existing,
        string incoming)
    {
        if (string.IsNullOrEmpty(existing))
        {
            return incoming;
        }

        if (
            string.IsNullOrEmpty(incoming) ||
            existing.IndexOf(
                incoming,
                StringComparison.Ordinal) >= 0)
        {
            return existing;
        }

        return existing + "+" + incoming;
    }
}
