using System.Collections.Frozen;
using Auxim.Core.Approval;
using Auxim.Core.Config;
using Auxim.Core.Logging;
using Auxim.Core.Resources;
using Auxim.Core.Tools;
using Auxim.VAFS;

namespace Auxim.Core.Runtime;

public sealed partial class AuximRuntimeService
{
    private readonly object _executionOwner = new();

    /// <summary>
    /// Runs an opted-in, trusted in-process runner in a Kernel-owned scope without a session.
    /// The scope is closed and outstanding bound operations are canceled and drained before this call returns.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The runner has not opted in, or the configured tool service cannot supply a bound catalog.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The runner returned normally with outstanding context operations.
    /// </exception>
    public async Task<AgentResult> RunInContextAsync(
        string userInput,
        AuximRuntimeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        cancellationToken.ThrowIfCancellationRequested();

        var allowedToolNames = CaptureAllowedToolNames(options);
        var resourcePermissions = CaptureResourcePermissions(options);
        var configuration = _configLoader().Snapshot();
        var registry = _tools switch
        {
            RuntimeToolService service => service.CreateRegistry(),
            EmptyRuntimeToolService => new ToolRegistry(),
            _ => throw new NotSupportedException(
                "Context-bound execution requires a RuntimeToolService with a capturable tool catalog."),
        };
        var toolBindings = registry.ListRegistrations()
            .Where(registration => registration.Definition.ResourceViewBinder is not null
                && (allowedToolNames is null || allowedToolNames.Contains(registration.Definition.Name)))
            .Select(registration => (registration.Definition.Name, registration.Definition.ResourceViewBinder!,
                registration.SupportsResourceRebinding));
        var homeDirectory = _homeDirectory();
        var eventSink = CreateEventSink(homeDirectory, options?.EventSink);
        var resources = new ExecutionResourceView(configuration.Sandbox, homeDirectory,
            followDirectoryLinksInListings: resourcePermissions is null);
        return await RunWithResourceViewAsync(userInput, configuration, resources, toolBindings, resourcePermissions,
            homeDirectory, options?.ApprovalHandler ?? NonInteractiveApprovalHandler.Instance, eventSink, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a child of an active context owned by this service. The runner must support overlapping context runs.
    /// Null permission lists inherit the delegable subset and empty lists deny all in their respective category.
    /// The child inherits approval and event routing from the parent. Its private temporary mount receives no grants.
    /// </summary>
    public async Task<AgentResult> RunChildInContextAsync(
        AuximExecutionContext parent,
        string userInput,
        AuximRuntimeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(parent.Owner, _executionOwner))
        {
            throw new ArgumentException("The parent context belongs to a different runtime service.", nameof(parent));
        }

        if (options?.ApprovalHandler is not null || options?.EventSink is not null)
        {
            throw new ArgumentException("Child executions inherit the parent's approval handler and event sink.", nameof(options));
        }

        if (!_agentRunner.SupportsConcurrentContextExecutions)
        {
            throw new NotSupportedException("The runner does not support overlapping context executions.");
        }

        parent.BeginOperation();
        var checkingPermissions = true;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent.CancellationToken, cancellationToken);
            var toolBindings = GetChildToolBindings(parent, CaptureAllowedToolNames(options));
            var resourcePermissions = VafsResourcePermissionPolicy.DeriveChildPermissions(
                parent.ResourceDomain, parent.ResourcePermissions, CaptureResourcePermissions(options));
            checkingPermissions = false;
            var parentResourcePolicy = parent.GetDelegationResourcePolicy();
            var configuration = parent.Configuration;
            parent.CancellationToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            var resources = new ExecutionResourceView(parent.ResourceDomain, parent.HomeDirectory);
            return await RunWithResourceViewAsync(userInput, configuration, resources, toolBindings, resourcePermissions,
                parent.HomeDirectory, parent.ApprovalHandler, parent.UnscopedEventSink, cancellation.Token,
                parentResourcePolicy, parent.ExecutionId)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception) when (checkingPermissions)
        {
            AuximLog.Warning(
                $"Child execution denied for execution {parent.ExecutionId}, run {parent.RunId}: {exception.Message}",
                parent.HomeDirectory);
            throw;
        }
        finally
        {
            parent.EndOperation();
        }
    }

    private static (string Name, Func<VirtualAgentFileSystem, ToolDefinition> Bind, bool CanRebind)[] GetChildToolBindings(
        AuximExecutionContext parent, IReadOnlySet<string>? requested)
    {
        if (requested is not null)
        {
            foreach (var name in requested)
            {
                if (!parent.ToolRebinders.TryGetValue(name, out var bind) || bind is null)
                {
                    throw new UnauthorizedAccessException($"The parent cannot delegate tool '{name}'.");
                }
            }
        }

        return parent.ToolRebinders
            .Where(tool => tool.Value is not null && (requested is null || requested.Contains(tool.Key)))
            .Select(tool => (tool.Key, tool.Value!, true))
            .ToArray();
    }

    private static IReadOnlySet<string>? CaptureAllowedToolNames(AuximRuntimeOptions? options) =>
        options?.AllowedToolNames?.Select(name =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    "AllowedToolNames cannot contain null, empty, or whitespace-only names.", nameof(options));
            }

            return name;
        }).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<VafsResourcePermission>? CaptureResourcePermissions(AuximRuntimeOptions? options) =>
        options?.ResourcePermissions is { } permissions ? Array.AsReadOnly(permissions.ToArray()) : null;

    private async Task<AgentResult> RunWithResourceViewAsync(
        string userInput,
        AuximConfig configuration,
        ExecutionResourceView resources,
        IEnumerable<(string Name, Func<VirtualAgentFileSystem, ToolDefinition> Bind, bool CanRebind)> toolBindings,
        IReadOnlyList<VafsResourcePermission>? resourcePermissions,
        string homeDirectory,
        IApprovalHandler approvalHandler,
        IRuntimeEventSink eventSink,
        CancellationToken cancellationToken,
        VafsResourcePermissionPolicy? parentResourcePolicy = null,
        AuximExecutionId? parentExecutionId = null)
    {
        AuximExecutionContext context;
        try
        {
            context = new AuximExecutionContext(
                _executionOwner, configuration, resources, toolBindings, resourcePermissions, homeDirectory,
                approvalHandler, eventSink, cancellationToken, parentResourcePolicy, parentExecutionId);
        }
        catch
        {
            resources.DisposeAfterSetupFailure(homeDirectory);
            throw;
        }

        // SessionId is empty for this entry point. ExecutionId identifies the scope in all events.
        var startPublished = false;
        try
        {
            AgentResult result;
            Exception? runFailure = null;
            int outstandingOperations;
            try
            {
                await context.EventSink.PublishAsync(
                    new RuntimeRunStartedEvent(
                        RuntimeEventFactory.NewEventId(), RuntimeEventFactory.Now(), context.RunId, ""),
                    context.CancellationToken);
                startPublished = true;
                context.CancellationToken.ThrowIfCancellationRequested();
                var runnerTask = _agentRunner.RunInContextAsync(userInput, context);
                context.TrackRunnerTask(runnerTask);
                result = await runnerTask;
            }
            catch (Exception exception)
            {
                runFailure = exception;
                throw;
            }
            finally
            {
                var drained = context.BeginClose(out outstandingOperations);
                try
                {
                    await context.CloseAsync(homeDirectory, drained);
                }
                catch (Exception exception)
                {
                    AuximLog.Warning(
                        $"Cleanup failed for execution {context.ExecutionId}: {exception.GetType().Name}: {exception.Message}",
                        homeDirectory);
                    if (runFailure is null && outstandingOperations == 0 && !cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (outstandingOperations != 0)
            {
                throw new InvalidOperationException(
                    $"The runner returned with {outstandingOperations} outstanding context operation(s).");
            }

            await context.EventSink.PublishAsync(
                new RuntimeRunCompletedEvent(
                    RuntimeEventFactory.NewEventId(), RuntimeEventFactory.Now(), context.RunId, "", result.FinalResponse),
                cancellationToken);
            return result;
        }
        catch (Exception exception) when (startPublished
            || exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await context.EventSink.PublishAsync(
                new RuntimeRunFailedEvent(
                    RuntimeEventFactory.NewEventId(), RuntimeEventFactory.Now(), context.RunId, "", exception.Message)
                {
                    IsCanceled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested,
                },
                CancellationToken.None);
            throw;
        }
    }
}
