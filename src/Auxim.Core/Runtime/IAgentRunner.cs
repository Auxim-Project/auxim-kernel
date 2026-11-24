namespace Auxim.Core.Runtime;

public interface IAgentRunner
{
    /// <summary>
    /// Indicates that this runner can execute overlapping context runs on the same instance,
    /// including when a parent awaits a child run.
    /// </summary>
    bool SupportsConcurrentContextExecutions => false;

    Task<AgentResult> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a trusted runner with tools and dependencies bound by the Kernel.</summary>
    Task<AgentResult> RunInContextAsync(string userInput, AuximExecutionContext context) =>
        Task.FromException<AgentResult>(
            new NotSupportedException("This runner does not support context-bound execution."));
}
