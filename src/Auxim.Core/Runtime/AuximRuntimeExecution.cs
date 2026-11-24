using System.Collections.Frozen;
using Auxim.Core.Approval;
using Auxim.Core.Logging;
using Auxim.Core.Tools;

namespace Auxim.Core.Runtime;

public sealed partial class AuximRuntimeService
{
    /// <summary>
    /// Runs an opted-in, trusted in-process runner in a Kernel-owned scope without a session.
    /// The scope is closed and outstanding bound operations are drained before this call returns.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The runner has not opted in, or the configured tool service cannot supply a bound catalog.
    /// </exception>
    public async Task<AgentResult> RunInContextAsync(
        string userInput,
        AuximRuntimeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        cancellationToken.ThrowIfCancellationRequested();

        var allowedToolNames = options?.AllowedToolNames?.Select(name =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    "AllowedToolNames cannot contain null, empty, or whitespace-only names.", nameof(options));
            }

            return name;
        }).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        var resourcePermissions = options?.ResourcePermissions?.ToArray();
        var configuration = _configLoader().Snapshot();
        var registry = _tools switch
        {
            RuntimeToolService service => service.CreateRegistry(),
            EmptyRuntimeToolService => new ToolRegistry(),
            _ => throw new NotSupportedException(
                "Context-bound execution requires a RuntimeToolService with a capturable tool catalog."),
        };
        var homeDirectory = _homeDirectory();
        var resources = new ExecutionResourceView(configuration.Sandbox, homeDirectory,
            followDirectoryLinksInListings: resourcePermissions is null);
        AuximExecutionContext context;
        try
        {
            context = new AuximExecutionContext(
                configuration, resources, registry, allowedToolNames, resourcePermissions, homeDirectory,
                options?.ApprovalHandler ?? NonInteractiveApprovalHandler.Instance,
                CreateEventSink(homeDirectory, options?.EventSink),
                cancellationToken);
        }
        catch
        {
            resources.DisposeAfterSetupFailure(homeDirectory);
            throw;
        }

        // SessionId is empty for this entry point. ExecutionId identifies the scope in all events.
        try
        {
            AgentResult result;
            Exception? runFailure = null;
            try
            {
                await context.EventSink.PublishAsync(
                    new RuntimeRunStartedEvent(
                        RuntimeEventFactory.NewEventId(), RuntimeEventFactory.Now(), context.RunId, ""),
                    context.CancellationToken);
                context.CancellationToken.ThrowIfCancellationRequested();
                result = await _agentRunner.RunInContextAsync(userInput, context);
                context.CancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                runFailure = exception;
                throw;
            }
            finally
            {
                try
                {
                    await context.CloseAsync(homeDirectory);
                }
                catch (Exception exception)
                {
                    AuximLog.Warning(
                        $"Cleanup failed for execution {context.ExecutionId}: {exception.GetType().Name}: {exception.Message}",
                        homeDirectory);
                    if (runFailure is null && !cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.EventSink.PublishAsync(
                new RuntimeRunCompletedEvent(
                    RuntimeEventFactory.NewEventId(), RuntimeEventFactory.Now(), context.RunId, "", result.FinalResponse),
                cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            await context.EventSink.PublishAsync(
                new RuntimeRunFailedEvent(
                    RuntimeEventFactory.NewEventId(), RuntimeEventFactory.Now(), context.RunId, "", exception.Message),
                CancellationToken.None);
            throw;
        }
    }
}
