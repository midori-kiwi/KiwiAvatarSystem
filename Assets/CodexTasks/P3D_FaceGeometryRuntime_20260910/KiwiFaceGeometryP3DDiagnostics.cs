#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Aggregate-only P3D evidence sink. It owns no Product output and is inert
/// until the dedicated Development Build controller enables it.
/// </summary>
internal static class KiwiFaceGeometryP3DDiagnostics
{
    internal readonly struct Identity
    {
        internal readonly bool exists;
        internal readonly int serviceId;
        internal readonly int runId;
        internal readonly int streamId;
        internal readonly ulong frameId;
        internal readonly long sourceHostTicks;
        internal readonly int cameraGeneration;
        internal readonly int trackingSessionGeneration;
        internal readonly int providerGeneration;
        internal readonly int modelGeneration;
        internal readonly string backend;
        internal readonly int semanticFrameWidth;
        internal readonly int semanticFrameHeight;

        internal Identity(
            int serviceId,
            int runId,
            int streamId,
            ulong frameId,
            long sourceHostTicks,
            int cameraGeneration,
            int trackingSessionGeneration,
            int providerGeneration,
            int modelGeneration,
            KiwiTrackingBackend backend,
            int semanticFrameWidth,
            int semanticFrameHeight)
        {
            exists = true;
            this.serviceId = serviceId;
            this.runId = runId;
            this.streamId = streamId;
            this.frameId = frameId;
            this.sourceHostTicks = sourceHostTicks;
            this.cameraGeneration = cameraGeneration;
            this.trackingSessionGeneration = trackingSessionGeneration;
            this.providerGeneration = providerGeneration;
            this.modelGeneration = modelGeneration;
            this.backend = backend.ToString();
            this.semanticFrameWidth = semanticFrameWidth;
            this.semanticFrameHeight = semanticFrameHeight;
        }
    }

    internal sealed class Snapshot
    {
        internal bool enabled;
        internal long serviceCreatedCount;
        internal long admissionCount;
        internal long submittedCount;
        internal long pendingSupersedeCount;
        internal int maxInFlight;
        internal int maxPending;
        internal long graphCreatedCount;
        internal long graphRetiredCount;
        internal long graphDestroyStartedCount;
        internal long graphDestroyedCount;
        internal long callbackRootsReleasedCount;
        internal long callbackEnterCount;
        internal long callbackExitCount;
        internal long activeCallbacks;
        internal long maxActiveCallbacks;
        internal long lateCallbackCount;
        internal long callbackHandoffCount;
        internal long callbackHandoffRejectedCount;
        internal long completionIdentityMismatchCount;
        internal long acceptedCount;
        internal long duplicateAcceptedPublishCount;
        internal long outOfOrderAcceptedPublishCount;
        internal long staleGenerationAcceptedPublishCount;
        internal long mixedEpochAcceptedPublishCount;
        internal long oldRunToNewRunPublicationCount;
        internal long sourceAgeSampleCount;
        internal long sourceAgeInvalidCount;
        internal long sourceAgeMinTicks;
        internal long sourceAgeMaxTicks;
        internal long transientFaceGeometryCount;
        internal long allocationSampleCount;
        internal long allocationBytesTotal;
        internal long allocationBytesMax;
        internal long gc0CollectionsDuringCallbacks;
        internal long gc1CollectionsDuringCallbacks;
        internal long gc2CollectionsDuringCallbacks;
        internal long preDestroyRootReleaseCount;
        internal long preDrainRootReleaseCount;
        internal int latestGraphCreatedStreamId;
        internal Identity firstAccepted;
        internal Identity lastAccepted;
    }

    private static readonly object Gate = new object();
    private static bool _enabled;
    private static int _nextServiceId;
    private static Snapshot _state = new Snapshot();

    internal static bool Enabled
    {
        get
        {
            lock (Gate)
            {
                return _enabled;
            }
        }
    }

    internal static void ResetAndEnable()
    {
        lock (Gate)
        {
            _enabled = true;
            _nextServiceId = 0;
            _state = new Snapshot
            {
                enabled = true,
                sourceAgeMinTicks = long.MaxValue
            };
        }
    }

    internal static void Disable()
    {
        lock (Gate)
        {
            _enabled = false;
            _state.enabled = false;
        }
    }

    internal static int RecordServiceCreated()
    {
        lock (Gate)
        {
            int id = ++_nextServiceId;
            if (_enabled)
            {
                ++_state.serviceCreatedCount;
            }
            return id;
        }
    }

    internal static void RecordAdmission(
        bool supersededPending,
        int inFlight,
        int pending)
    {
        lock (Gate)
        {
            if (!_enabled) return;
            ++_state.admissionCount;
            if (supersededPending) ++_state.pendingSupersedeCount;
            RecordBoundsLocked(inFlight, pending);
        }
    }

    internal static void RecordSubmitted(int inFlight, int pending)
    {
        lock (Gate)
        {
            if (!_enabled) return;
            ++_state.submittedCount;
            RecordBoundsLocked(inFlight, pending);
        }
    }

    internal static void RecordState(int inFlight, int pending)
    {
        lock (Gate)
        {
            if (!_enabled) return;
            RecordBoundsLocked(inFlight, pending);
        }
    }

    internal static void RecordGraphCreated(int streamId)
    {
        lock (Gate)
        {
            if (!_enabled) return;
            ++_state.graphCreatedCount;
            if (streamId > _state.latestGraphCreatedStreamId)
            {
                _state.latestGraphCreatedStreamId = streamId;
            }
        }
    }

    internal static void RecordGraphRetired()
    {
        lock (Gate)
        {
            if (_enabled) ++_state.graphRetiredCount;
        }
    }

    internal static void RecordGraphDestroyStarted()
    {
        lock (Gate)
        {
            if (_enabled) ++_state.graphDestroyStartedCount;
        }
    }

    internal static void RecordGraphDestroyed()
    {
        lock (Gate)
        {
            if (_enabled) ++_state.graphDestroyedCount;
        }
    }

    internal static void RecordCallbackRootsReleased(
        bool nativeGraphAlive,
        int activeCallbacks)
    {
        lock (Gate)
        {
            if (!_enabled) return;
            ++_state.callbackRootsReleasedCount;
            if (nativeGraphAlive) ++_state.preDestroyRootReleaseCount;
            if (activeCallbacks != 0) ++_state.preDrainRootReleaseCount;
        }
    }

    internal static void RecordCallbackEnter(bool publicationAccepting)
    {
        lock (Gate)
        {
            if (!_enabled) return;
            ++_state.callbackEnterCount;
            ++_state.activeCallbacks;
            if (_state.activeCallbacks > _state.maxActiveCallbacks)
            {
                _state.maxActiveCallbacks = _state.activeCallbacks;
            }
            if (!publicationAccepting) ++_state.lateCallbackCount;
        }
    }

    internal static void RecordCallbackExit()
    {
        lock (Gate)
        {
            if (!_enabled) return;
            ++_state.callbackExitCount;
            --_state.activeCallbacks;
        }
    }

    internal static void RecordCallbackAllocation(
        bool transientFaceGeometryCreated,
        long allocatedBytes,
        int gc0Delta,
        int gc1Delta,
        int gc2Delta)
    {
        lock (Gate)
        {
            if (!_enabled) return;
            if (transientFaceGeometryCreated)
            {
                ++_state.transientFaceGeometryCount;
            }
            if (allocatedBytes >= 0L)
            {
                ++_state.allocationSampleCount;
                _state.allocationBytesTotal += allocatedBytes;
                if (allocatedBytes > _state.allocationBytesMax)
                {
                    _state.allocationBytesMax = allocatedBytes;
                }
            }
            _state.gc0CollectionsDuringCallbacks += Math.Max(0, gc0Delta);
            _state.gc1CollectionsDuringCallbacks += Math.Max(0, gc1Delta);
            _state.gc2CollectionsDuringCallbacks += Math.Max(0, gc2Delta);
        }
    }

    internal static void RecordCallbackHandoff(bool accepted)
    {
        lock (Gate)
        {
            if (!_enabled) return;
            if (accepted) ++_state.callbackHandoffCount;
            else ++_state.callbackHandoffRejectedCount;
        }
    }

    internal static void RecordCompletionIdentityMismatch()
    {
        lock (Gate)
        {
            if (_enabled) ++_state.completionIdentityMismatchCount;
        }
    }

    internal static void RecordAccepted(
        int serviceId,
        int runId,
        int streamId,
        ulong frameId,
        long sourceHostTicks,
        int cameraGeneration,
        int trackingSessionGeneration,
        int providerGeneration,
        int modelGeneration,
        KiwiTrackingBackend backend,
        int semanticFrameWidth,
        int semanticFrameHeight,
        long acceptedHostTicks)
    {
        lock (Gate)
        {
            if (!_enabled) return;

            var identity = new Identity(
                serviceId,
                runId,
                streamId,
                frameId,
                sourceHostTicks,
                cameraGeneration,
                trackingSessionGeneration,
                providerGeneration,
                modelGeneration,
                backend,
                semanticFrameWidth,
                semanticFrameHeight);

            if (_state.lastAccepted.exists)
            {
                if (frameId == _state.lastAccepted.frameId)
                {
                    ++_state.duplicateAcceptedPublishCount;
                }
                else if (frameId < _state.lastAccepted.frameId)
                {
                    ++_state.outOfOrderAcceptedPublishCount;
                }
            }

            bool stale =
                cameraGeneration != KiwiRuntimeGenerationContext.CameraGeneration ||
                trackingSessionGeneration != KiwiRuntimeGenerationContext.TrackingSessionGeneration ||
                providerGeneration != KiwiRuntimeGenerationContext.ProviderGeneration ||
                modelGeneration != KiwiRuntimeGenerationContext.ModelGeneration;
            if (stale)
            {
                ++_state.staleGenerationAcceptedPublishCount;
                ++_state.mixedEpochAcceptedPublishCount;
            }

            if (streamId < _state.latestGraphCreatedStreamId)
            {
                ++_state.oldRunToNewRunPublicationCount;
            }

            long sourceAgeTicks = acceptedHostTicks - sourceHostTicks;
            ++_state.sourceAgeSampleCount;
            if (sourceAgeTicks < 0L)
            {
                ++_state.sourceAgeInvalidCount;
            }
            else
            {
                if (sourceAgeTicks < _state.sourceAgeMinTicks)
                {
                    _state.sourceAgeMinTicks = sourceAgeTicks;
                }
                if (sourceAgeTicks > _state.sourceAgeMaxTicks)
                {
                    _state.sourceAgeMaxTicks = sourceAgeTicks;
                }
            }

            if (!_state.firstAccepted.exists)
            {
                _state.firstAccepted = identity;
            }
            _state.lastAccepted = identity;
            ++_state.acceptedCount;
        }
    }

    internal static Snapshot Capture()
    {
        lock (Gate)
        {
            return new Snapshot
            {
                enabled = _state.enabled,
                serviceCreatedCount = _state.serviceCreatedCount,
                admissionCount = _state.admissionCount,
                submittedCount = _state.submittedCount,
                pendingSupersedeCount = _state.pendingSupersedeCount,
                maxInFlight = _state.maxInFlight,
                maxPending = _state.maxPending,
                graphCreatedCount = _state.graphCreatedCount,
                graphRetiredCount = _state.graphRetiredCount,
                graphDestroyStartedCount = _state.graphDestroyStartedCount,
                graphDestroyedCount = _state.graphDestroyedCount,
                callbackRootsReleasedCount = _state.callbackRootsReleasedCount,
                callbackEnterCount = _state.callbackEnterCount,
                callbackExitCount = _state.callbackExitCount,
                activeCallbacks = _state.activeCallbacks,
                maxActiveCallbacks = _state.maxActiveCallbacks,
                lateCallbackCount = _state.lateCallbackCount,
                callbackHandoffCount = _state.callbackHandoffCount,
                callbackHandoffRejectedCount = _state.callbackHandoffRejectedCount,
                completionIdentityMismatchCount = _state.completionIdentityMismatchCount,
                acceptedCount = _state.acceptedCount,
                duplicateAcceptedPublishCount = _state.duplicateAcceptedPublishCount,
                outOfOrderAcceptedPublishCount = _state.outOfOrderAcceptedPublishCount,
                staleGenerationAcceptedPublishCount = _state.staleGenerationAcceptedPublishCount,
                mixedEpochAcceptedPublishCount = _state.mixedEpochAcceptedPublishCount,
                oldRunToNewRunPublicationCount = _state.oldRunToNewRunPublicationCount,
                sourceAgeSampleCount = _state.sourceAgeSampleCount,
                sourceAgeInvalidCount = _state.sourceAgeInvalidCount,
                sourceAgeMinTicks = _state.sourceAgeMinTicks == long.MaxValue ? 0L : _state.sourceAgeMinTicks,
                sourceAgeMaxTicks = _state.sourceAgeMaxTicks,
                transientFaceGeometryCount = _state.transientFaceGeometryCount,
                allocationSampleCount = _state.allocationSampleCount,
                allocationBytesTotal = _state.allocationBytesTotal,
                allocationBytesMax = _state.allocationBytesMax,
                gc0CollectionsDuringCallbacks = _state.gc0CollectionsDuringCallbacks,
                gc1CollectionsDuringCallbacks = _state.gc1CollectionsDuringCallbacks,
                gc2CollectionsDuringCallbacks = _state.gc2CollectionsDuringCallbacks,
                preDestroyRootReleaseCount = _state.preDestroyRootReleaseCount,
                preDrainRootReleaseCount = _state.preDrainRootReleaseCount,
                latestGraphCreatedStreamId = _state.latestGraphCreatedStreamId,
                firstAccepted = _state.firstAccepted,
                lastAccepted = _state.lastAccepted
            };
        }
    }

    private static void RecordBoundsLocked(int inFlight, int pending)
    {
        if (inFlight > _state.maxInFlight) _state.maxInFlight = inFlight;
        if (pending > _state.maxPending) _state.maxPending = pending;
    }
}
#endif
