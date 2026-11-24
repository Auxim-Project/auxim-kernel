using System.Collections.Concurrent;
using System.Text.Json;
using Auxim.Core.Approval;
using Auxim.Core.Config;
using Auxim.Core.Resources;
using Auxim.Core.Runtime;
using Auxim.Core.State;
using Auxim.Core.Tools;
using Xunit;

namespace Auxim.Core.Tests;

public sealed class AuximExecutionContextTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly IReadOnlyDictionary<string, object?> NoArguments =
        new Dictionary<string, object?>();
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"auxim-context-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ConcurrentRunsBindIdentityConfigurationCatalogAndChannelsWithoutTouchingSessions()
    {
        var sessions = new SessionStore(_home);
        var current = sessions.NewSession("existing chat");
        var firstEntered = new TaskCompletionSource<AuximExecutionContext>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstMayInvoke = NewSignal();
        var firstToolEntered = NewSignal();
        var secondToolEntered = NewSignal();
        var toolsMayFinish = NewSignal();

        var config = new AuximConfig
        {
            Sandbox = new SandboxConfig
            {
                Mounts = [new SandboxMountConfig { Name = "original", HostPath = _home }],
                Shell = new ShellSandboxConfig { AllowedCommands = ["original"] },
            },
        };
        var schemaValues = new List<string> { "original" };
        var registry = new ToolRegistry();
        RegisterContextTool(registry, ProtectedTool("old", firstToolEntered));
        var factoryCalls = 0;
        var toolService = new RuntimeToolService(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            return registry;
        });
        var runner = new ContextRunner(async (input, context) =>
        {
            if (input == "first")
            {
                firstEntered.SetResult(context);
                await firstMayInvoke.Task.WaitAsync(TestTimeout);
            }

            var snapshot = context.Configuration;
            var expected = input == "first" ? "original" : "new";
            Assert.Equal(expected, Assert.Single(snapshot.Sandbox.Mounts).Name);
            Assert.Equal(expected, Assert.Single(snapshot.Sandbox.Shell.AllowedCommands));
            snapshot.Sandbox.Mounts.Clear();
            snapshot.Sandbox.Shell.AllowedCommands.Clear();
            Assert.Equal(expected, Assert.Single(context.Configuration.Sandbox.Mounts).Name);
            Assert.Equal(expected, Assert.Single(context.Configuration.Sandbox.Shell.AllowedCommands));

            var catalog = context.ListTools();
            Assert.Equal(input == "first" ? ["protected"] : new[] { "new-tool", "protected" },
                catalog.Select(tool => tool.Name));
            var schema = (JsonElement)catalog.Single(tool => tool.Name == "protected").ParametersSchema["enum"]!;
            Assert.Equal(expected, schema[0].GetString());
            Assert.Equal($"test:{(input == "first" ? "old" : "new")}",
                Assert.Single(context.ResolveToolResourceAccesses("protected", NoArguments)).Resource.Value);
            if (input == "first")
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => context.InvokeToolAsync("new-tool", NoArguments));
            }

            await context.PublishContentDeltaAsync(input);
            var response = await context.InvokeToolAsync("protected", NoArguments);
            return new AgentResult(response, []);
        });
        var runtime = CreateRuntime(runner, toolService, () => config);
        var firstEvents = new ConcurrentQueue<RuntimeEvent>();
        var secondEvents = new ConcurrentQueue<RuntimeEvent>();
        var firstApprovals = new RecordingApprovalHandler();
        var secondApprovals = new RecordingApprovalHandler();

        var firstRun = runtime.RunInContextAsync("first", Options(firstEvents, firstApprovals));
        var firstContext = await firstEntered.Task.WaitAsync(TestTimeout);
        config.Sandbox.Mounts[0] = new SandboxMountConfig { Name = "new", HostPath = _home };
        config.Sandbox.Shell.AllowedCommands[0] = "new";
        schemaValues[0] = "new";
        RegisterContextTool(registry, ProtectedTool("new", secondToolEntered));
        RegisterContextTool(registry, new ToolDefinition("new-tool", "test", "", (_, _) => Task.FromResult("new")));

        var secondRun = runtime.RunInContextAsync("second", Options(secondEvents, secondApprovals));
        firstMayInvoke.SetResult();
        await Task.WhenAll(firstToolEntered.Task, secondToolEntered.Task).WaitAsync(TestTimeout);
        Assert.False(firstRun.IsCompleted);
        Assert.False(secondRun.IsCompleted);
        toolsMayFinish.SetResult();
        var results = await Task.WhenAll(firstRun, secondRun).WaitAsync(TestTimeout);

        Assert.Equal(["old", "new"], results.Select(result => result.FinalResponse));
        Assert.Equal(2, factoryCalls);
        var first = Assert.IsType<RuntimeRunStartedEvent>(firstEvents.First());
        var second = Assert.IsType<RuntimeRunStartedEvent>(secondEvents.First());
        Assert.Equal(firstContext.ExecutionId, first.ExecutionId);
        Assert.NotNull(second.ExecutionId);
        Assert.NotEqual(first.ExecutionId, second.ExecutionId);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.NotEqual(firstContext.ExecutionId.ToString(), firstContext.RunId.Value);
        Assert.Empty(first.SessionId);
        Assert.Empty(second.SessionId);
        AssertEvents(firstEvents, first, "first");
        AssertEvents(secondEvents, second, "second");
        Assert.Equal(first.RunId.Value, Assert.Single(firstApprovals.Requests).RunId);
        Assert.Equal(second.RunId.Value, Assert.Single(secondApprovals.Requests).RunId);
        Assert.Equal(current.Id, sessions.GetCurrentSessionId());
        Assert.Equal(current.Id, Assert.Single(sessions.List()).Id);
        Assert.Empty(sessions.TryLoad(current.Id)!.Messages);

        ToolDefinition ProtectedTool(string name, TaskCompletionSource entered) =>
            new("protected", "test", "", async (_, token) =>
            {
                entered.SetResult();
                await toolsMayFinish.Task.WaitAsync(token);
                return name;
            })
            {
                ParametersSchema = new Dictionary<string, object?> { ["enum"] = schemaValues },
                ResourceAccessResolver = _ => [new ResourceAccess(
                    ResourceAction.Write, new ResourceUri($"test:{name}"), RequiresApproval: true)],
            };
    }

    [Fact]
    public async Task CallerCancellationReachesApprovalAndLeavesOtherContextActive()
    {
        var awaitingApproval = NewSignal();
        var approvalCanceled = NewSignal();
        var approvalMayReturn = NewSignal();
        var approvalHandler = new RecordingApprovalHandler(async token =>
        {
            using var registration = token.Register(() => approvalCanceled.TrySetResult());
            awaitingApproval.SetResult();
            await approvalMayReturn.Task.WaitAsync(TestTimeout);
            return ApprovalResponse.Allow(remember: true);
        });
        var protectedHandlerRan = false;
        var registry = new ToolRegistry();
        RegisterContextTool(registry, new ToolDefinition("write", "test", "", (_, _) =>
        {
            protectedHandlerRan = true;
            return Task.FromResult("written");
        })
        {
            ResourceAccessResolver = _ =>
                [new ResourceAccess(ResourceAction.Write, new ResourceUri("test:protected"), RequiresApproval: true)],
        });
        var otherEntered = NewSignal();
        var otherMayFinish = NewSignal();
        var runtime = CreateRuntime(new ContextRunner(async (input, context) =>
        {
            if (input == "cancel")
            {
                await context.InvokeToolAsync("write", NoArguments);
            }
            else
            {
                otherEntered.SetResult();
                await otherMayFinish.Task.WaitAsync(context.CancellationToken);
                Assert.Single(context.ListTools());
            }

            return new AgentResult(input, []);
        }), new RuntimeToolService(() => registry));
        using var cancellation = new CancellationTokenSource();
        var events = new ConcurrentQueue<RuntimeEvent>();
        var canceledRun = runtime.RunInContextAsync("cancel", Options(events, approvalHandler), cancellation.Token);
        var otherRun = runtime.RunInContextAsync("other");
        await Task.WhenAll(awaitingApproval.Task, otherEntered.Task).WaitAsync(TestTimeout);

        cancellation.Cancel();
        await approvalCanceled.Task.WaitAsync(TestTimeout);
        approvalMayReturn.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRun.WaitAsync(TestTimeout));
        Assert.False(protectedHandlerRan);
        Assert.Empty(new ToolApprovalService(_home).ListGrants());
        Assert.Equal("canceled", Assert.Single(events.OfType<RuntimeToolCompletedEvent>()).Outcome);
        Assert.IsType<RuntimeRunFailedEvent>(events.Last());
        AssertEventIdentity(events);
        Assert.False(otherRun.IsCompleted);
        otherMayFinish.SetResult();
        Assert.Equal("other", (await otherRun.WaitAsync(TestTimeout)).FinalResponse);
        Assert.False(Directory.Exists(Path.Combine(_home, "sessions")));
        Assert.False(File.Exists(Path.Combine(_home, "current_session")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingRejectsNewWorkAndDrainsOutstandingToolsOnSuccessOrFailure(bool runnerFails)
    {
        var stopping = NewSignal();
        var toolMayFinish = NewSignal();
        var registry = new ToolRegistry();
        RegisterContextTool(registry, new ToolDefinition("wait", "test", "", async (_, token) =>
        {
            using var registration = token.Register(() => stopping.TrySetResult());
            await toolMayFinish.Task;
            token.ThrowIfCancellationRequested();
            return "done";
        }));
        AuximExecutionContext? capturedContext = null;
        Task<string>? outstandingTool = null;
        var runner = new ContextRunner((_, context) =>
        {
            capturedContext = context;
            outstandingTool = context.InvokeToolAsync("wait", NoArguments);
            return runnerFails
                ? Task.FromException<AgentResult>(new InvalidOperationException("runner failed"))
                : Task.FromResult(new AgentResult("done", []));
        });
        var runtime = CreateRuntime(runner, new RuntimeToolService(() => registry));
        var events = new ConcurrentQueue<RuntimeEvent>();

        var run = runtime.RunInContextAsync("run", Options(events));
        await stopping.Task.WaitAsync(TestTimeout);
        Assert.NotNull(capturedContext);
        Assert.NotNull(outstandingTool);
        Assert.True(capturedContext.CancellationToken.IsCancellationRequested);
        Assert.False(run.IsCompleted);
        Assert.DoesNotContain(events, value => value is RuntimeRunCompletedEvent or RuntimeRunFailedEvent);
        Assert.Throws<ObjectDisposedException>(() => capturedContext.ListTools());
        Assert.Throws<ObjectDisposedException>(() => capturedContext.ResolveToolResourceAccesses("wait", NoArguments));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => capturedContext.InvokeToolAsync("wait", NoArguments));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => capturedContext.PublishContentDeltaAsync("late").AsTask());
        toolMayFinish.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outstandingTool.WaitAsync(TestTimeout));
        if (runnerFails)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TestTimeout));
            Assert.Equal("runner failed", failure.Message);
            Assert.IsType<RuntimeRunFailedEvent>(events.Last());
        }
        else
        {
            Assert.Equal("done", (await run.WaitAsync(TestTimeout)).FinalResponse);
            Assert.IsType<RuntimeRunCompletedEvent>(events.Last());
        }
    }

    [Fact]
    public async Task ClosingWaitsForCancellationAlreadyStartedByCaller()
    {
        var runnerEntered = new TaskCompletionSource<AuximExecutionContext>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runnerMayFinish = NewSignal();
        var callbackEntered = NewSignal();
        var callbackMayFinish = NewSignal();
        var events = new ConcurrentQueue<RuntimeEvent>();
        var runtime = CreateRuntime(new ContextRunner(async (_, context) =>
        {
            // Keep this registration alive while the operation leaves the runner.
            context.CancellationToken.Register(() =>
            {
                callbackEntered.SetResult();
                callbackMayFinish.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
                throw new InvalidOperationException("late cancellation callback");
            });
            runnerEntered.SetResult(context);
            await runnerMayFinish.Task.WaitAsync(TestTimeout);
            return new AgentResult("done", []);
        }));

        using var cancellation = new CancellationTokenSource();
        var run = runtime.RunInContextAsync("run", Options(events), cancellation.Token);
        var context = await runnerEntered.Task.WaitAsync(TestTimeout);
        var cancel = cancellation.CancelAsync();
        try
        {
            await callbackEntered.Task.WaitAsync(TestTimeout);
            runnerMayFinish.SetResult();
            using var closeTimeout = new CancellationTokenSource(TestTimeout);
            await WaitForClose(closeTimeout.Token);
            Assert.False(run.IsCompleted);
            Assert.DoesNotContain(events, value => value is RuntimeRunFailedEvent);
        }
        finally
        {
            callbackMayFinish.TrySetResult();
        }

        await cancel.WaitAsync(TestTimeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TestTimeout));
        Assert.IsType<RuntimeRunFailedEvent>(events.Last());
        Assert.Contains("late cancellation callback", File.ReadAllText(Path.Combine(_home, "logs", "agent.log")));

        async Task WaitForClose(CancellationToken token)
        {
            while (true)
            {
                try
                {
                    context.ListTools();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    await Task.Delay(1, token);
                }
            }
        }
    }

    [Fact]
    public async Task ContextCallsUseApprovalAndProvideNoFallbackToLegacyToolExecution()
    {
        var registry = new ToolRegistry();
        RegisterContextTool(registry, new ToolDefinition("write", "test", "", (_, _) =>
            throw new InvalidOperationException("Denied handler should never run"))
        {
            ResourceAccessResolver = _ =>
                [new ResourceAccess(ResourceAction.Write, new ResourceUri("test:protected"), RequiresApproval: true)],
        });
        var runtime = CreateRuntime(new ContextRunner(async (_, context) =>
        {
            await Assert.ThrowsAsync<ToolApprovalDeniedException>(() => context.InvokeToolAsync("write", NoArguments));
            return new AgentResult("denied", []);
        }), new RuntimeToolService(() => registry));

        Assert.Equal("denied", (await runtime.RunInContextAsync("run")).FinalResponse);
    }

    [Fact]
    public async Task ApprovalAndHandlerUseTheSameDetachedArgumentsAndResourceDeclarations()
    {
        var payload = new Dictionary<string, string> { ["path"] = "original" };
        var arguments = new Dictionary<string, object?> { ["payload"] = payload, ["value"] = 42 };
        var accesses = new List<ResourceAccess>();
        var registry = new ToolRegistry();
        RegisterContextTool(registry, new ToolDefinition("write", "test", "", (args, _) =>
        {
            Assert.Equal(42, Assert.IsType<int>(args["value"]));
            return Task.FromResult(GetPath(args));
        })
        {
            ResourceAccessResolver = args =>
            {
                accesses.Add(Access(GetPath(args)));
                return accesses;
            },
        });
        var runtime = CreateRuntime(new ContextRunner(async (_, context) =>
            new AgentResult(await context.InvokeToolAsync("write", arguments), [])),
            new RuntimeToolService(() => registry));
        var approval = new RecordingApprovalHandler(_ =>
        {
            payload["path"] = "changed";
            arguments["value"] = 0;
            return Task.FromResult(ApprovalResponse.Allow(remember: true));
        });

        var result = await runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            ApprovalHandler = approval,
            EventSink = new DelegateRuntimeEventSink((value, _) =>
            {
                if (value is RuntimeToolStartedEvent)
                {
                    accesses[0] = Access("changed");
                }

                return ValueTask.CompletedTask;
            }),
        });

        Assert.Equal("original", result.FinalResponse);
        var request = Assert.Single(approval.Requests);
        Assert.Equal("original", GetPath(request.Arguments));
        var protectedAccess = Assert.Single(request.ResourceAccesses);
        Assert.Equal("test:original", protectedAccess.Resource.Value);
        Assert.Throws<NotSupportedException>(() => ((IList<ResourceAccess>)request.ResourceAccesses)[0] = Access("changed"));
        Assert.Empty(new ToolApprovalService(_home).ListGrants());

        static string GetPath(IReadOnlyDictionary<string, object?> args) =>
            Assert.IsType<JsonElement>(args["payload"]).GetProperty("path").GetString()!;

        static ResourceAccess Access(string path) =>
            new(ResourceAction.Write, new ResourceUri($"test:{path}"), RequiresApproval: true);
    }

    [Theory]
    [InlineData("runner")]
    [InlineData("handler")]
    [InlineData("approval")]
    [InlineData("run.started")]
    [InlineData("content.delta")]
    [InlineData("tool.started")]
    [InlineData("approval.requested")]
    [InlineData("approval.resolved")]
    public async Task FailuresCloseScopeAndKeepOriginalExceptionWhenTerminalSinkThrows(string source)
    {
        var original = new InvalidOperationException(source);
        var terminalFailure = new InvalidOperationException("terminal sink");
        var events = new ConcurrentQueue<RuntimeEvent>();
        var registry = new ToolRegistry();
        RegisterContextTool(registry, new ToolDefinition("write", "test", "", (_, _) =>
            source == "handler" ? throw original : Task.FromResult("done"))
        {
            ResourceAccessResolver = _ =>
                [new ResourceAccess(ResourceAction.Write, new ResourceUri("test:failure"), RequiresApproval: true)],
        });
        AuximExecutionContext? captured = null;
        var runtime = CreateRuntime(new ContextRunner(async (_, context) =>
        {
            captured = context;
            if (source == "runner")
            {
                // This callback stays registered through teardown.
                context.CancellationToken.Register(() => throw new InvalidOperationException("cleanup"));
                throw original;
            }

            await context.PublishContentDeltaAsync("delta");
            return new AgentResult(await context.InvokeToolAsync("write", NoArguments), []);
        }), new RuntimeToolService(() => registry));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunInContextAsync(
            "run", new AuximRuntimeOptions
            {
                ApprovalHandler = new RecordingApprovalHandler(_ =>
                    source == "approval" ? throw original : Task.FromResult(ApprovalResponse.Allow())),
                EventSink = new DelegateRuntimeEventSink((value, _) =>
                {
                    events.Enqueue(value);
                    if (value.Kind == source)
                    {
                        throw original;
                    }

                    return value is RuntimeToolCompletedEvent or RuntimeRunFailedEvent
                        ? ValueTask.FromException(terminalFailure)
                        : ValueTask.CompletedTask;
                }),
            }));
        Assert.Same(original, failure);
        Assert.IsType<RuntimeRunFailedEvent>(events.Last());
        Assert.Single(events.OfType<RuntimeRunFailedEvent>());
        AssertEventIdentity(events);
        if (source == "run.started")
        {
            Assert.Null(captured);
        }
        else
        {
            Assert.NotNull(captured);
            Assert.True(captured.CancellationToken.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => captured.ListTools());
        }

        if (events.Any(value => value is RuntimeToolStartedEvent))
        {
            Assert.Equal("failed", Assert.Single(events.OfType<RuntimeToolCompletedEvent>()).Outcome);
        }
    }

    [Fact]
    public async Task SuccessfulToolAndRunAreNotReportedAsFailedWhenTerminalSinkThrows()
    {
        var events = new ConcurrentQueue<RuntimeEvent>();
        var registry = new ToolRegistry();
        RegisterContextTool(registry, new ToolDefinition("echo", "test", "", (_, _) => Task.FromResult("done")));
        var runtime = CreateRuntime(new ContextRunner(async (_, context) =>
            new AgentResult(await context.InvokeToolAsync("echo", NoArguments), [])),
            new RuntimeToolService(() => registry));

        var result = await runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            EventSink = new DelegateRuntimeEventSink((value, _) =>
            {
                events.Enqueue(value);
                return value is RuntimeToolCompletedEvent or RuntimeRunCompletedEvent
                    ? ValueTask.FromException(new InvalidOperationException("terminal sink"))
                    : ValueTask.CompletedTask;
            }),
        });

        Assert.Equal("done", result.FinalResponse);
        Assert.Equal(["run.started", "tool.started", "tool.completed", "run.completed"], events.Select(value => value.Kind));
        Assert.Equal("succeeded", Assert.Single(events.OfType<RuntimeToolCompletedEvent>()).Outcome);
        AssertEventIdentity(events);
    }

    [Fact]
    public async Task SerializedEventsIncludeExecutionIdentityOnlyForScopedRuns()
    {
        var events = new ConcurrentQueue<RuntimeEvent>();
        var runtime = CreateRuntime(new ContextRunner((_, _) => Task.FromResult(new AgentResult("done", []))));
        await runtime.RunInContextAsync("run", Options(events));
        var scoped = Assert.IsType<RuntimeRunStartedEvent>(events.First());
        var restored = JsonSerializer.Deserialize<RuntimeRunStartedEvent>(JsonSerializer.Serialize(scoped));
        Assert.NotNull(restored);
        Assert.Equal(scoped.ExecutionId, restored.ExecutionId);

        var legacy = new RuntimeRunStartedEvent("event", DateTimeOffset.UtcNow, AuximRunId.New(), "session");
        Assert.DoesNotContain(nameof(RuntimeEvent.ExecutionId), JsonSerializer.Serialize(legacy));
    }

    [Fact]
    public async Task LegacyRunnerAndToolServiceAreOnlyUsedByLegacyEntries()
    {
        var legacyRunner = new LegacyRunner();
        var runtime = CreateRuntime(legacyRunner);
        await Assert.ThrowsAsync<NotSupportedException>(() => runtime.RunInContextAsync("unsupported"));
        Assert.Equal(0, legacyRunner.Calls);
        Assert.False(Directory.Exists(Path.Combine(_home, "sessions")));

        Assert.Equal("legacy", (await runtime.ChatAsync(new AuximChatRequest("chat"))).FinalResponse);
        Assert.Equal(1, legacyRunner.Calls);

        var customTools = new LegacyToolService();
        runtime = CreateRuntime(new ContextRunner((_, _) =>
            throw new InvalidOperationException("Runner should never run")), customTools);
        await Assert.ThrowsAsync<NotSupportedException>(() => runtime.RunInContextAsync("unsupported"));
        Assert.Equal(0, customTools.Invocations);
        Assert.Equal("legacy tool", await runtime.InvokeToolAsync("tool", NoArguments));
        Assert.Equal(1, customTools.Invocations);
    }

    [Fact]
    public async Task DirectToolKeepsOriginalFailureWhenFailureNotificationAlsoThrows()
    {
        var failure = new InvalidOperationException("tool failed");
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition("fail", "test", "", (_, _) => throw failure));
        var runtime = CreateRuntime(new LegacyRunner(), new RuntimeToolService(() => registry));
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.InvokeToolAsync(
            "fail", NoArguments, new AuximRuntimeOptions
            {
                EventSink = new DelegateRuntimeEventSink((value, _) => value is RuntimeToolCompletedEvent
                    ? ValueTask.FromException(new InvalidOperationException("terminal sink"))
                    : ValueTask.CompletedTask),
            }));
        Assert.Same(failure, actual);
    }

    private AuximRuntimeService CreateRuntime(
        IAgentRunner runner,
        IRuntimeToolService? toolService = null,
        Func<AuximConfig>? configuration = null) =>
        new(runner, toolService, () => new SessionStore(_home),
            configuration ?? (() => new AuximConfig()), () => _home, () => "test environment");

    private static AuximRuntimeOptions Options(
        ConcurrentQueue<RuntimeEvent> events, IApprovalHandler? approvalHandler = null) => new()
    {
        ApprovalHandler = approvalHandler,
        EventSink = new DelegateRuntimeEventSink((value, _) =>
        {
            events.Enqueue(value);
            return ValueTask.CompletedTask;
        }),
    };

    private static void AssertEvents(
        ConcurrentQueue<RuntimeEvent> events, RuntimeRunStartedEvent started, string delta)
    {
        Assert.Equal(
            ["run.started", "content.delta", "tool.started", "approval.requested", "approval.resolved", "tool.completed", "run.completed"],
            events.Select(value => value.Kind));
        Assert.All(events, value =>
        {
            Assert.Equal(started.ExecutionId, value.ExecutionId);
            Assert.Equal(started.RunId, value.RunId);
        });
        Assert.Equal(delta, Assert.Single(events.OfType<RuntimeContentDeltaEvent>()).Delta);
        Assert.Equal(Assert.Single(events.OfType<RuntimeToolStartedEvent>()).ToolCallId,
            Assert.Single(events.OfType<RuntimeToolCompletedEvent>()).ToolCallId);
    }

    private static void AssertEventIdentity(ConcurrentQueue<RuntimeEvent> events)
    {
        var started = Assert.IsType<RuntimeRunStartedEvent>(events.First());
        Assert.NotNull(started.ExecutionId);
        Assert.All(events, value =>
        {
            Assert.Equal(started.ExecutionId, value.ExecutionId);
            Assert.Equal(started.RunId, value.RunId);
        });
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void RegisterContextTool(ToolRegistry registry, ToolDefinition definition) =>
        registry.Register(definition with { ResourceViewBinder = _ => definition });

    private sealed class ContextRunner(Func<string, AuximExecutionContext, Task<AgentResult>> run) : IAgentRunner
    {
        public Task<AgentResult> RunInContextAsync(string userInput, AuximExecutionContext context) =>
            run(userInput, context);

        public Task<AgentResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The legacy runner entry must not be called.");
    }

    private sealed class LegacyRunner : IAgentRunner
    {
        public int Calls { get; private set; }

        public Task<AgentResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AgentResult("legacy", []));
        }
    }

    private sealed class LegacyToolService : IRuntimeToolService
    {
        public int Invocations { get; private set; }

        public IReadOnlyList<AuximRuntimeTool> ListTools() =>
            throw new InvalidOperationException("The legacy catalog must not be used.");

        public IReadOnlyList<ResourceAccess> ResolveResourceAccesses(
            string name, IReadOnlyDictionary<string, object?> arguments) => [];

        public Task<string> InvokeAsync(AuximRunId runId, string toolCallId, string name,
            IReadOnlyDictionary<string, object?> arguments, string homeDirectory,
            IApprovalHandler approvalHandler, IRuntimeEventSink eventSink, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult("legacy tool");
        }
    }

    private sealed class RecordingApprovalHandler(
        Func<CancellationToken, Task<ApprovalResponse>>? respond = null) : IApprovalHandler
    {
        public List<ApprovalRequest> Requests { get; } = [];

        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond?.Invoke(cancellationToken) ?? Task.FromResult(ApprovalResponse.Allow());
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }
}
