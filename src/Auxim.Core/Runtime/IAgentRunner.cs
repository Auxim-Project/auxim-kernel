namespace Auxim.Core.Runtime;

public interface IAgentRunner
{
    Task<AgentResult> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a trusted runner with tools and dependencies bound by the Kernel.</summary>
    Task<AgentResult> RunInContextAsync(string userInput, AuximExecutionContext context) =>
        Task.FromException<AgentResult>(
            new NotSupportedException("This runner does not support context-bound execution."));
}
