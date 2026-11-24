using Auxim.Core.Approval;
using Auxim.Core.Resources;

namespace Auxim.Core.Runtime;

public sealed class AuximRuntimeOptions
{
    public IApprovalHandler? ApprovalHandler { get; init; }

    public IRuntimeEventSink? EventSink { get; init; }

    /// <summary>
    /// Restricts context-bound execution to the specified registered tool names, ignoring case.
    /// Null leaves the context catalog unrestricted. An empty collection denies all tools.
    /// Entries must be non-null and contain at least one non-whitespace character.
    /// Legacy chat, direct tool calls, and external messages reject a non-null collection.
    /// </summary>
    public IReadOnlyCollection<string>? AllowedToolNames { get; init; }

    /// <summary>
    /// Restricts context-bound resource access to the supplied VAFS permissions.
    /// Null leaves resource access unrestricted by this option. An empty collection denies all resource access.
    /// Legacy chat, direct tool calls, and external messages reject a non-null collection.
    /// </summary>
    public IReadOnlyCollection<VafsResourcePermission>? ResourcePermissions { get; init; }
}
