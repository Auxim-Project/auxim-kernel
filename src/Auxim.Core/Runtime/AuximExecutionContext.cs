using System.Collections.ObjectModel;
using System.Text.Json;
using Auxim.Core.Approval;
using Auxim.Core.Config;
using Auxim.Core.Logging;
using Auxim.Core.Resources;
using Auxim.Core.Tools;

namespace Auxim.Core.Runtime;

/// <summary>Identifies a Kernel-owned execution scope independently of runs and sessions.</summary>
public readonly record struct AuximExecutionId(Guid Value)
{
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Provides context-bound tools and output to a trusted in-process runner.
/// The Kernel owns its lifetime. Resource isolation requires a separate enforcement boundary.
/// </summary>
public sealed class AuximExecutionContext
{
    private readonly AuximConfig _configuration;
    private readonly ExecutionResourceView _resources;
    private readonly IReadOnlyList<AuximRuntimeTool> _catalog;
    private readonly ToolExecutionCoordinator _tools;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _callerCancellation;
    private readonly object _gate = new();
    private bool _closed;
    private int _activeOperations;
    private TaskCompletionSource? _drained;
    private Task? _cancellation;

    internal AuximExecutionContext(
        AuximConfig configuration,
        ExecutionResourceView resources,
        ToolRegistry registry,
        IReadOnlySet<string>? allowedToolNames,
        IReadOnlyList<VafsResourcePermission>? resourcePermissions,
        string homeDirectory,
        IApprovalHandler approvalHandler,
        IRuntimeEventSink eventSink,
        CancellationToken cancellationToken)
    {
        _configuration = configuration;
        _resources = resources;
        var resourcePolicy = resourcePermissions is null
            ? null
            : new VafsResourcePermissionPolicy(resources.FileSystem, resourcePermissions);
        var snapshot = new ToolRegistry();
        foreach (var definition in registry.List())
        {
            if ((allowedToolNames is not null && !allowedToolNames.Contains(definition.Name))
                || definition.ResourceViewBinder is not { } bind)
            {
                continue;
            }

            var bound = bind(resources.FileSystem);
            if (!string.Equals(definition.Name, bound.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Resource binding changed the name of tool '{definition.Name}'.");
            }

            snapshot.Register(bound with
            {
                ParametersSchema = SnapshotSchema(bound.ParametersSchema),
                ResourceViewBinder = null,
            });
        }

        _catalog = Array.AsReadOnly(RuntimeToolService.DescribeTools(snapshot).ToArray());
        EventSink = new DelegateRuntimeEventSink(async (runtimeEvent, token) =>
        {
            var contextualEvent = runtimeEvent with { ExecutionId = ExecutionId };
            if (runtimeEvent is RuntimeRunCompletedEvent or RuntimeRunFailedEvent or RuntimeToolCompletedEvent)
            {
                try
                {
                    await eventSink.PublishAsync(contextualEvent, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    AuximLog.Warning(
                        $"Failed to deliver {runtimeEvent.Kind} for execution {ExecutionId}: {exception.Message}",
                        homeDirectory);
                }

                return;
            }

            token.ThrowIfCancellationRequested();
            await eventSink.PublishAsync(contextualEvent, token);
        });
        _tools = new ToolExecutionCoordinator(snapshot, homeDirectory, approvalHandler, EventSink,
            allowRememberedGrants: false, resourcePolicy: resourcePolicy);
        _lifetime = new CancellationTokenSource();
        CancellationToken = _lifetime.Token;
        _callerCancellation = cancellationToken.UnsafeRegister(static state =>
        {
            ((AuximExecutionContext)state!).SignalCancellation();
        }, this);
    }

    public AuximExecutionId ExecutionId { get; } = new(Guid.NewGuid());

    public AuximRunId RunId { get; } = AuximRunId.New();

    /// <summary>Returns a defensive copy of the configuration captured at entry.</summary>
    public AuximConfig Configuration => _configuration.Snapshot();

    public string EnvironmentDescription => _resources.FileSystem.DescribeForAgent();

    /// <summary>Signals caller cancellation or the closing of this execution.</summary>
    public CancellationToken CancellationToken { get; }

    internal IRuntimeEventSink EventSink { get; }

    /// <summary>Lists metadata from the bound tool catalog. Schema values are immutable JSON.</summary>
    public IReadOnlyList<AuximRuntimeTool> ListTools()
    {
        lock (_gate)
        {
            EnsureActive();
            return _catalog;
        }
    }

    public IReadOnlyList<ResourceAccess> ResolveToolResourceAccesses(
        string name,
        IReadOnlyDictionary<string, object?> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        BeginOperation();
        try
        {
            return _tools.ResolveResourceAccesses(name, SnapshotArguments(arguments));
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Invokes a bound tool with a snapshot of its JSON-compatible arguments.</summary>
    public async Task<string> InvokeToolAsync(
        string name,
        IReadOnlyDictionary<string, object?> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        BeginOperation();
        try
        {
            return await _tools.ExecuteAsync(
                RunId, Guid.NewGuid().ToString("N"), name, SnapshotArguments(arguments), CancellationToken);
        }
        finally
        {
            EndOperation();
        }
    }

    public async ValueTask PublishContentDeltaAsync(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        BeginOperation();
        try
        {
            await EventSink.PublishAsync(new RuntimeContentDeltaEvent(
                RuntimeEventFactory.NewEventId(), RuntimeEventFactory.Now(), RunId, delta), CancellationToken);
        }
        finally
        {
            EndOperation();
        }
    }

    // Called once by the runtime, including on runner failure or cancellation.
    internal async ValueTask CloseAsync(string homeDirectory)
    {
        Task drained;
        lock (_gate)
        {
            _closed = true;
            if (_activeOperations != 0)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            drained = _drained?.Task ?? Task.CompletedTask;
        }

        try
        {
            await SignalCancellation();
        }
        catch (AggregateException exception)
        {
            AuximLog.Warning(
                $"Cancellation callbacks failed for execution {ExecutionId}: {exception.Flatten().Message}", homeDirectory);
        }
        finally
        {
            await drained;
            try
            {
                await _callerCancellation.DisposeAsync();
            }
            finally
            {
                _lifetime.Dispose();
                _resources.Dispose();
            }
        }
    }

    private Task SignalCancellation()
    {
        lock (_gate)
        {
            return _cancellation ??= _lifetime.CancelAsync();
        }
    }

    private void BeginOperation()
    {
        lock (_gate)
        {
            EnsureActive();
            _activeOperations++;
        }
    }

    private void EndOperation()
    {
        lock (_gate)
        {
            if (--_activeOperations == 0)
            {
                _drained?.SetResult();
            }
        }
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        CancellationToken.ThrowIfCancellationRequested();
    }

    private static IReadOnlyDictionary<string, object?> SnapshotSchema(
        IReadOnlyDictionary<string, object?> schema)
    {
        var properties = JsonSerializer.SerializeToElement(schema).EnumerateObject();
        return new ReadOnlyDictionary<string, object?>(
            properties.ToDictionary(property => property.Name, property => (object?)property.Value));
    }

    private static IReadOnlyDictionary<string, object?> SnapshotArguments(
        IReadOnlyDictionary<string, object?> arguments) =>
        new ReadOnlyDictionary<string, object?>(arguments.ToDictionary(
            argument => argument.Key,
            argument => argument.Value switch
            {
                null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
                    or float or double or decimal => argument.Value,
                JsonElement json => json.Clone(),
                _ => JsonSerializer.SerializeToElement(argument.Value),
            }));
}
