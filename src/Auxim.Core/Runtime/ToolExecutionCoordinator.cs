using Auxim.Core.Approval;
using Auxim.Core.Logging;
using Auxim.Core.Resources;
using Auxim.Core.Tools;

namespace Auxim.Core.Runtime;

internal sealed class ToolExecutionCoordinator
{
    private readonly ToolRegistry _tools;
    private readonly ToolApprovalService _approvals;
    private readonly string _homeDirectory;
    private readonly IApprovalHandler _approvalHandler;
    private readonly IRuntimeEventSink _eventSink;
    private readonly VafsResourcePermissionPolicy? _resourcePolicy;

    public ToolExecutionCoordinator(
        ToolRegistry tools,
        string homeDirectory,
        IApprovalHandler approvalHandler,
        IRuntimeEventSink eventSink,
        bool allowRememberedGrants = true,
        VafsResourcePermissionPolicy? resourcePolicy = null)
    {
        _tools = tools;
        _approvals = new ToolApprovalService(homeDirectory, allowRememberedGrants);
        _homeDirectory = homeDirectory;
        _approvalHandler = approvalHandler;
        _eventSink = eventSink;
        _resourcePolicy = resourcePolicy;
    }

    public IReadOnlyList<ResourceAccess> ResolveResourceAccesses(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var accesses = _tools.Get(toolName).ResolveResourceAccesses(arguments);
        return _resourcePolicy is null ? accesses : CheckResourceAccesses(toolName, accesses);
    }

    public async Task<string> ExecuteAsync(
        AuximRunId runId,
        string toolCallId,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var definition = _tools.Get(toolName);
        cancellationToken.ThrowIfCancellationRequested();
        var accesses = CheckResourceAccesses(toolName, definition.ResolveResourceAccesses(arguments));

        ApprovalResponse approval;
        var content = "";
        var outputLength = 0;
        try
        {
            await _eventSink.PublishAsync(
                new RuntimeToolStartedEvent(
                    RuntimeEventFactory.NewEventId(),
                    RuntimeEventFactory.Now(),
                    runId,
                    toolCallId,
                    toolName,
                    accesses),
                cancellationToken);

            approval = await _approvals.ReviewAsync(
                runId, toolName, arguments, accesses, _approvalHandler, _eventSink, cancellationToken);
            if (approval.Approved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _resourcePolicy?.EnsureAllowed(toolName, accesses);
                content = await definition.Handler(arguments, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                outputLength = content.Length;
            }
        }
        catch (Exception exception)
        {
            var outcome = exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? "canceled"
                : "failed";
            try
            {
                await PublishCompletedAsync(runId, toolCallId, toolName, outcome, 0, cancellationToken);
            }
            catch (Exception deliveryFailure)
            {
                AuximLog.Warning(
                    $"Failed to deliver tool.completed for run {runId}, tool call {toolCallId}: {deliveryFailure.Message}",
                    _homeDirectory);
            }

            throw;
        }

        if (!approval.Approved)
        {
            await PublishCompletedAsync(runId, toolCallId, toolName, "denied", 0, cancellationToken);
            throw new ToolApprovalDeniedException(toolName, approval.Reason);
        }

        await PublishCompletedAsync(runId, toolCallId, toolName, "succeeded", outputLength, cancellationToken);
        return content;
    }

    private IReadOnlyList<ResourceAccess> CheckResourceAccesses(string toolName, IReadOnlyList<ResourceAccess> accesses)
    {
        var snapshot = Array.AsReadOnly(accesses.ToArray());
        _resourcePolicy?.EnsureAllowed(toolName, snapshot);
        return snapshot;
    }

    private ValueTask PublishCompletedAsync(
        AuximRunId runId,
        string toolCallId,
        string toolName,
        string outcome,
        int outputLength,
        CancellationToken cancellationToken) =>
        _eventSink.PublishAsync(
            new RuntimeToolCompletedEvent(
                RuntimeEventFactory.NewEventId(),
                RuntimeEventFactory.Now(),
                runId,
                toolCallId,
                toolName,
                outcome,
                outputLength),
            cancellationToken);
}
