using System;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    /// <summary>
    /// Phase16.20.13 observer-only failure-stage diagnostics.
    ///
    /// This class never owns scheduling, tracking, ROI, thresholds, presentation,
    /// tensors or camera state. It records which already-existing scheduling API
    /// boundary was executing when a schedule attempt failed.
    /// </summary>
    public static class KiwiInferenceFailureStageDiagnostics
    {
        public const string ContractMarker =
            "KIWI_V5_1_PHASE16_20_13_V25_CPU_FAILURE_STAGE_ISOLATION";

        public const int StageNone = 0;
        public const int StageUpdateSourceDimensions = 1;
        public const int StageBuildCropMatrix = 2;
        public const int StageScheduleModelEntry = 3;
        public const int StageCropBlit = 4;
        public const int StageTextureToTensor = 5;
        public const int StageWorkerSchedule = 6;
        public const int StagePeekOutput = 7;
        public const int StageOutputValidation = 8;
        public const int StageReadbackRequest = 9;
        public const int StageScheduleCommit = 10;

        public const int FailureNone = 0;
        public const int FailureException = 1;
        public const int FailureNullOutput = 2;
        public const int FailureShapeMismatch = 3;

        public static int CurrentStageId { get; private set; }
        public static int LastFailureStageId { get; private set; }
        public static int LastFailureKindId { get; private set; }
        public static int LastExceptionTypeId { get; private set; }
        public static int LastExceptionHResult { get; private set; }

        public static ulong AttemptCount { get; private set; }
        public static ulong SuccessCount { get; private set; }
        public static ulong FailureCount { get; private set; }
        public static ulong ExceptionCount { get; private set; }
        public static ulong NullOutputCount { get; private set; }
        public static ulong ShapeMismatchCount { get; private set; }

        public static void BeginAttempt()
        {
            AttemptCount++;
            CurrentStageId = StageNone;
        }

        public static void RecordStage(int stageId)
        {
            CurrentStageId = stageId;
        }

        public static void RecordNullOutput()
        {
            LastFailureStageId = StageOutputValidation;
            LastFailureKindId = FailureNullOutput;
            LastExceptionTypeId = 0;
            LastExceptionHResult = 0;

            FailureCount++;
            NullOutputCount++;
        }

        public static void RecordShapeMismatch()
        {
            LastFailureStageId = StageOutputValidation;
            LastFailureKindId = FailureShapeMismatch;
            LastExceptionTypeId = 0;
            LastExceptionHResult = 0;

            FailureCount++;
            ShapeMismatchCount++;
        }

        public static void RecordException(Exception exception)
        {
            LastFailureStageId = CurrentStageId;
            LastFailureKindId = FailureException;
            LastExceptionTypeId = ClassifyException(exception);
            LastExceptionHResult =
                exception != null
                    ? exception.HResult
                    : 0;

            FailureCount++;
            ExceptionCount++;
        }

        public static void RecordSuccess()
        {
            CurrentStageId = StageScheduleCommit;
            SuccessCount++;
        }

        private static int ClassifyException(Exception exception)
        {
            if (exception == null)
            {
                return 0;
            }

            if (exception is NotSupportedException)
            {
                return 1;
            }

            if (exception is InvalidOperationException)
            {
                return 2;
            }

            if (exception is ArgumentException)
            {
                return 3;
            }

            if (exception is IndexOutOfRangeException)
            {
                return 4;
            }

            if (exception is NullReferenceException)
            {
                return 5;
            }

            if (exception is ObjectDisposedException)
            {
                return 6;
            }

            return 99;
        }
    }
}
