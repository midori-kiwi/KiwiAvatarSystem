using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Mediapipe;
using UnityEditor;

/// <summary>
/// Deterministic P3C_1 callback lifetime state and registry stress.
/// It invokes the actual private Production lifecycle owner through reflection
/// so no test hook or per-frame diagnostic branch is added to Production.
/// </summary>
public static class P3C1CallbackLifecycleDiagnostic
{
    private const string ResultPath =
        "CodexTasks/P3C_1_CALLBACK_LIFECYCLE_STRESS_RESULT.txt";

    private static Type _serviceType;
    private static Type _graphRunType;
    private static MethodInfo _registerCallback;
    private static MethodInfo _destroyGraphAndFinalize;
    private static MethodInfo _tryFinalizeDestroyedRun;
    private static MethodInfo _tryReleaseCallbackRoots;
    private static MethodInfo _onNativeOutput;
    private static MethodInfo _tryEnterCallbackLifetime;
    private static MethodInfo _exitCallbackLifetime;
    private static MethodInfo _retireResultPublication;
    private static FieldInfo _callbackRegistry;
    private static FieldInfo _nativeOutputCallback;
    private static FieldInfo _activeCallbacks;
    private static FieldInfo _resultPublicationAccepting;
    private static FieldInfo _nativeGraphAlive;
    private static FieldInfo _noFutureCallbacks;
    private static FieldInfo _callbackRootsReleased;
    private static FieldInfo _completion;

    private static int _nextRunId = 1000000;
    private static int _callbackEnterCount;
    private static int _callbackExitCount;
    private static int _postRetireResultPublicationCount;
    private static int _preDestroyRegistryReleaseCount;
    private static int _preDestroyDelegateReleaseCount;
    private static int _preDrainRegistryReleaseCount;
    private static int _doubleReleaseCount;
    private static int _negativeActiveCallbackCount;
    private static int _invalidStateTransitionCount;
    private static int _graphResourceFinalLeakCount;

    public static void RunBatch()
    {
        int exitCode = 1;

        try
        {
            InitializeReflection();

            object service = CreateService();

            RunCase1(service);
            RunCase2(service);
            RunCase3(service);
            RunCase4(service);
            RunCase5(service);
            RunCase6(service);

            Require(
                _callbackEnterCount == _callbackExitCount,
                "Callback enter/exit counts are not balanced.");
            Require(
                _negativeActiveCallbackCount == 0,
                "Negative active callback count was observed.");
            Require(
                _postRetireResultPublicationCount == 0,
                "A retired callback published a result.");
            Require(
                _preDestroyRegistryReleaseCount == 0,
                "Registry was released before native destruction.");
            Require(
                _preDestroyDelegateReleaseCount == 0,
                "Delegate was released before native destruction.");
            Require(
                _preDrainRegistryReleaseCount == 0,
                "Registry was released before entered callbacks drained.");
            Require(
                _doubleReleaseCount == 0,
                "Callback roots were released twice.");
            Require(
                _invalidStateTransitionCount == 0,
                "Invalid lifecycle state transition was observed.");
            Require(
                _graphResourceFinalLeakCount == 0,
                "Graph or callback registry resource leak was observed.");

            var result = new StringBuilder();
            result.AppendLine("SYNTHETIC_LIFECYCLE_STRESS=PASS");
            result.AppendLine("CASE_1_ACTIVE_RETIRE_EXIT_DESTROY_RELEASE=PASS");
            result.AppendLine("CASE_2_RETIRE_CALLBACK_DISCARD_EXIT_DESTROY=PASS");
            result.AppendLine("CASE_3_DESTROY_THEN_ENTERED_CALLBACK_DRAIN=PASS");
            result.AppendLine("CASE_4_SEQUENTIAL_CALLBACKS_AROUND_RETIRE=PASS");
            result.AppendLine("CASE_5_REPEATED_GRAPH_DISPOSAL=PASS_32_REPEATS");
            result.AppendLine("CASE_6_EXCEPTION_FINALLY_EXIT=PASS");
            result.AppendLine(
                "CALLBACK_ENTER_COUNT=" + _callbackEnterCount);
            result.AppendLine(
                "CALLBACK_EXIT_COUNT=" + _callbackExitCount);
            result.AppendLine("ACTIVE_CALLBACKS_FINAL=0");
            result.AppendLine(
                "POST_RETIRE_RESULT_PUBLICATION_COUNT=" +
                _postRetireResultPublicationCount);
            result.AppendLine(
                "PRE_DESTROY_REGISTRY_RELEASE_COUNT=" +
                _preDestroyRegistryReleaseCount);
            result.AppendLine(
                "PRE_DESTROY_DELEGATE_RELEASE_COUNT=" +
                _preDestroyDelegateReleaseCount);
            result.AppendLine(
                "PRE_DRAIN_REGISTRY_RELEASE_COUNT=" +
                _preDrainRegistryReleaseCount);
            result.AppendLine(
                "DOUBLE_RELEASE_COUNT=" + _doubleReleaseCount);
            result.AppendLine(
                "NEGATIVE_ACTIVE_CALLBACK_COUNT=" +
                _negativeActiveCallbackCount);
            result.AppendLine(
                "INVALID_STATE_TRANSITION_COUNT=" +
                _invalidStateTransitionCount);
            result.AppendLine(
                "GRAPH_RESOURCE_FINAL_LEAK_COUNT=" +
                _graphResourceFinalLeakCount);
            result.AppendLine("LIVE_CAMERA_RUNTIME=NOT_RUN");
            result.AppendLine("P3D_RUNTIME=NOT_RUN");
            result.AppendLine("H1=NOT_AUTHORIZED");

            File.WriteAllText(
                Path.Combine(Directory.GetCurrentDirectory(), ResultPath),
                result.ToString(),
                new UTF8Encoding(false));

            exitCode = 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(
                Path.Combine(Directory.GetCurrentDirectory(), ResultPath),
                "SYNTHETIC_LIFECYCLE_STRESS=FAIL\n" + exception,
                new UTF8Encoding(false));
            UnityEngine.Debug.LogException(exception);
        }
        finally
        {
            EditorApplication.Exit(exitCode);
        }
    }

    private static void RunCase1(object service)
    {
        TestRun run = CreateRegisteredRun(service);
        Enter(run.Value);
        Retire(run.Value);
        Exit(run.Value);
        VerifyPreDestroyRootsHeld(run);
        Destroy(service, run);
        VerifyFinal(service, run);
    }

    private static void RunCase2(object service)
    {
        TestRun run = CreateRegisteredRun(service);
        Retire(run.Value);
        VerifyPreDestroyRootsHeld(run);

        bool completionBefore = CompletionExists(service);
        _onNativeOutput.Invoke(
            null,
            new object[]
            {
                run.GraphPointer,
                run.StreamId,
                IntPtr.Zero
            });
        ++_callbackEnterCount;
        ++_callbackExitCount;

        if (!completionBefore && CompletionExists(service))
        {
            ++_postRetireResultPublicationCount;
        }

        Require(
            Active(run.Value) == 0,
            "Case 2 retired callback did not exit.");
        Destroy(service, run);
        VerifyFinal(service, run);
    }

    private static void RunCase3(object service)
    {
        TestRun run = CreateRegisteredRun(service);
        Retire(run.Value);
        Enter(run.Value);
        VerifyPreDestroyRootsHeld(run);

        Destroy(service, run);

        Require(
            run.Graph.isDisposed,
            "Case 3 native graph was not destroyed.");
        Require(
            RegistryContains(run.StreamId),
            "Case 3 registry was released before callback drain.");
        Require(
            !RootsReleased(run.Value),
            "Case 3 callback roots were marked released before drain.");

        if (!RegistryContains(run.StreamId))
        {
            ++_preDrainRegistryReleaseCount;
        }

        Exit(run.Value);
        _tryFinalizeDestroyedRun.Invoke(
            service,
            new object[] { run.Value });
        VerifyFinal(service, run);
    }

    private static void RunCase4(object service)
    {
        TestRun run = CreateRegisteredRun(service);

        Enter(run.Value);
        Exit(run.Value);
        Enter(run.Value);
        Exit(run.Value);

        Retire(run.Value);

        Enter(run.Value);
        Require(
            !ResultPublicationAccepting(run.Value),
            "Case 4 retirement did not close publication authority.");
        Exit(run.Value);
        Enter(run.Value);
        Exit(run.Value);

        VerifyPreDestroyRootsHeld(run);
        Destroy(service, run);
        VerifyFinal(service, run);
    }

    private static void RunCase5(object service)
    {
        for (int i = 0; i < 32; ++i)
        {
            TestRun run = CreateRegisteredRun(service);
            Retire(run.Value);
            VerifyPreDestroyRootsHeld(run);
            Destroy(service, run);
            VerifyFinal(service, run);
        }
    }

    private static void RunCase6(object service)
    {
        TestRun run = CreateRegisteredRun(service);
        Enter(run.Value);

        try
        {
            throw new InvalidOperationException(
                "Deterministic callback exception fixture.");
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            Exit(run.Value);
        }

        Retire(run.Value);
        VerifyPreDestroyRootsHeld(run);
        Destroy(service, run);
        VerifyFinal(service, run);
    }

    private static void Enter(object run)
    {
        bool entered = (bool)_tryEnterCallbackLifetime.Invoke(run, null);
        Require(entered, "Callback lifetime entry was rejected.");
        ++_callbackEnterCount;
        CheckActiveNonnegative(run);
    }

    private static void Exit(object run)
    {
        _exitCallbackLifetime.Invoke(run, null);
        ++_callbackExitCount;
        CheckActiveNonnegative(run);
    }

    private static void Retire(object run)
    {
        _retireResultPublication.Invoke(run, null);
        Require(
            !ResultPublicationAccepting(run),
            "Result publication authority remained active after retire.");
    }

    private static void VerifyPreDestroyRootsHeld(TestRun run)
    {
        bool releaseReported = (bool)_tryReleaseCallbackRoots.Invoke(
            null,
            new object[] { run.Value });

        if (releaseReported || !RegistryContains(run.StreamId))
        {
            ++_preDestroyRegistryReleaseCount;
        }

        if (_nativeOutputCallback.GetValue(null) == null)
        {
            ++_preDestroyDelegateReleaseCount;
        }

        Require(
            NativeGraphAlive(run.Value),
            "Native graph was not alive before destruction.");
        Require(
            !NoFutureCallbacks(run.Value),
            "No-future-callback boundary was set before destruction.");
    }

    private static void Destroy(object service, TestRun run)
    {
        _destroyGraphAndFinalize.Invoke(
            service,
            new object[] { run.Value });
    }

    private static void VerifyFinal(object service, TestRun run)
    {
        _tryFinalizeDestroyedRun.Invoke(
            service,
            new object[] { run.Value });

        bool finalOk =
            run.Graph.isDisposed &&
            !NativeGraphAlive(run.Value) &&
            NoFutureCallbacks(run.Value) &&
            Active(run.Value) == 0 &&
            RootsReleased(run.Value) &&
            !RegistryContains(run.StreamId);

        if (!finalOk)
        {
            ++_graphResourceFinalLeakCount;
        }

        int registryCountBefore = RegistryCount();
        _tryFinalizeDestroyedRun.Invoke(
            service,
            new object[] { run.Value });
        int registryCountAfter = RegistryCount();
        if (
            registryCountBefore != registryCountAfter ||
            !RootsReleased(run.Value))
        {
            ++_doubleReleaseCount;
        }

        Require(finalOk, "Final graph/callback lifecycle state is not closed.");
    }

    private static TestRun CreateRegisteredRun(object service)
    {
        int id = ++_nextRunId;
        var graph = new CalculatorGraph();
        object run = Activator.CreateInstance(
            _graphRunType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { id, id, 480, 270, graph },
            null);

        _registerCallback.Invoke(
            service,
            new object[] { run });

        Require(
            RegistryContains(id),
            "Callback registry entry was not created.");

        return new TestRun(
            run,
            graph,
            id,
            graph.mpPtr);
    }

    private static object CreateService()
    {
        string bundlePath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Assets",
            "StreamingAssets",
            "face_landmarker_v2_with_blendshapes.bytes");

        return Activator.CreateInstance(
            _serviceType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { bundlePath },
            null);
    }

    private static void InitializeReflection()
    {
        _serviceType = typeof(KiwiRuntimeGenerationContext)
            .Assembly
            .GetType("KiwiFaceGeometryTransactionService", true);
        _graphRunType = _serviceType.GetNestedType(
            "GraphRun",
            BindingFlags.NonPublic);
        Require(_graphRunType != null, "GraphRun type was not found.");

        _registerCallback = RequiredMethod(
            _serviceType,
            "RegisterCallback",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _destroyGraphAndFinalize = RequiredMethod(
            _serviceType,
            "DestroyGraphAndFinalize",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _tryFinalizeDestroyedRun = RequiredMethod(
            _serviceType,
            "TryFinalizeDestroyedRun",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _tryReleaseCallbackRoots = RequiredMethod(
            _serviceType,
            "TryReleaseCallbackRoots",
            BindingFlags.Static | BindingFlags.NonPublic);
        _onNativeOutput = RequiredMethod(
            _serviceType,
            "OnNativeOutput",
            BindingFlags.Static | BindingFlags.NonPublic);

        _tryEnterCallbackLifetime = RequiredMethod(
            _graphRunType,
            "TryEnterCallbackLifetime",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _exitCallbackLifetime = RequiredMethod(
            _graphRunType,
            "ExitCallbackLifetime",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _retireResultPublication = RequiredMethod(
            _graphRunType,
            "RetireResultPublication",
            BindingFlags.Instance | BindingFlags.NonPublic);

        _callbackRegistry = RequiredField(
            _serviceType,
            "CallbackRegistry",
            BindingFlags.Static | BindingFlags.NonPublic);
        _nativeOutputCallback = RequiredField(
            _serviceType,
            "NativeOutputCallback",
            BindingFlags.Static | BindingFlags.NonPublic);
        _completion = RequiredField(
            _serviceType,
            "_completion",
            BindingFlags.Instance | BindingFlags.NonPublic);

        _activeCallbacks = RequiredField(
            _graphRunType,
            "activeCallbacks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _resultPublicationAccepting = RequiredField(
            _graphRunType,
            "resultPublicationAccepting",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _nativeGraphAlive = RequiredField(
            _graphRunType,
            "nativeGraphAlive",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _noFutureCallbacks = RequiredField(
            _graphRunType,
            "noFutureCallbacks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _callbackRootsReleased = RequiredField(
            _graphRunType,
            "callbackRootsReleased",
            BindingFlags.Instance | BindingFlags.NonPublic);
    }

    private static MethodInfo RequiredMethod(
        Type type,
        string name,
        BindingFlags flags)
    {
        MethodInfo result = type.GetMethod(name, flags);
        Require(result != null, "Method not found: " + name);
        return result;
    }

    private static FieldInfo RequiredField(
        Type type,
        string name,
        BindingFlags flags)
    {
        FieldInfo result = type.GetField(name, flags);
        Require(result != null, "Field not found: " + name);
        return result;
    }

    private static bool CompletionExists(object service)
    {
        object completion = _completion.GetValue(service);
        FieldInfo exists = RequiredField(
            completion.GetType(),
            "exists",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return (bool)exists.GetValue(completion);
    }

    private static int Active(object run)
    {
        return (int)_activeCallbacks.GetValue(run);
    }

    private static bool ResultPublicationAccepting(object run)
    {
        return (bool)_resultPublicationAccepting.GetValue(run);
    }

    private static bool NativeGraphAlive(object run)
    {
        return (bool)_nativeGraphAlive.GetValue(run);
    }

    private static bool NoFutureCallbacks(object run)
    {
        return (bool)_noFutureCallbacks.GetValue(run);
    }

    private static bool RootsReleased(object run)
    {
        return (bool)_callbackRootsReleased.GetValue(run);
    }

    private static void CheckActiveNonnegative(object run)
    {
        if (Active(run) < 0)
        {
            ++_negativeActiveCallbackCount;
        }
    }

    private static bool RegistryContains(int streamId)
    {
        var registry = (IDictionary)_callbackRegistry.GetValue(null);
        return registry.Contains(streamId);
    }

    private static int RegistryCount()
    {
        var registry = (IDictionary)_callbackRegistry.GetValue(null);
        return registry.Count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            ++_invalidStateTransitionCount;
            throw new InvalidDataException(message);
        }
    }

    private sealed class TestRun
    {
        internal readonly object Value;
        internal readonly CalculatorGraph Graph;
        internal readonly int StreamId;
        internal readonly IntPtr GraphPointer;

        internal TestRun(
            object value,
            CalculatorGraph graph,
            int streamId,
            IntPtr graphPointer)
        {
            Value = value;
            Graph = graph;
            StreamId = streamId;
            GraphPointer = graphPointer;
        }
    }
}
