using Auxim.Core.Approval;
using Auxim.Core.Logging;
using Auxim.Core.Resources;
using Auxim.Core.Tools;
using Auxim.VAFS;

namespace Auxim.Core.Runtime;

internal sealed class ToolExecutionCoordinator
{
    private readonly ToolRegistry _tools;
    private readonly ToolApprovalService _approvals;
    private readonly string _homeDirectory;
    private readonly IApprovalHandler _approvalHandler;
    private readonly IRuntimeEventSink _eventSink;
    private readonly VafsResourcePermissionPolicy? _resourcePolicy;
    private readonly bool _reportPreflightFailures;

    public ToolExecutionCoordinator(
        ToolRegistry tools,
        string homeDirectory,
        IApprovalHandler approvalHandler,
        IRuntimeEventSink eventSink,
        bool allowRememberedGrants = true,
        VafsResourcePermissionPolicy? resourcePolicy = null,
        bool reportPreflightFailures = false)
    {
        _tools = tools;
        _approvals = new ToolApprovalService(homeDirectory, allowRememberedGrants);
        _homeDirectory = homeDirectory;
        _approvalHandler = approvalHandler;
        _eventSink = eventSink;
        _resourcePolicy = resourcePolicy;
        _reportPreflightFailures = reportPreflightFailures;
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
        ApprovalResponse approval;
        var content = "";
        var outputLength = 0;
        var preflight = true;
        var findingTool = true;
        var checkingPermissions = false;
        try
        {
            var definition = _tools.Get(toolName);
            findingTool = false;
            cancellationToken.ThrowIfCancellationRequested();
            var declared = definition.ResolveResourceAccesses(arguments);
            checkingPermissions = true;
            var accesses = CheckResourceAccesses(toolName, declared);
            checkingPermissions = false;
            preflight = false;

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
                checkingPermissions = true;
                _resourcePolicy?.EnsureAllowed(toolName, accesses);
                checkingPermissions = false;
                content = await definition.Handler(arguments, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                outputLength = content.Length;
            }
        }
        catch (Exception exception) when (!preflight || _reportPreflightFailures)
        {
            var outcome = exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? "canceled"
                : "failed";
            if (_reportPreflightFailures && (findingTool && exception is InvalidOperationException
                || preflight && exception is VirtualPathException { IsReadOnlyMount: true }
                || checkingPermissions && exception is UnauthorizedAccessException or VirtualPathException))
            {
                outcome = "denied";
            }
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
