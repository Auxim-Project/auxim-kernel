using Auxim.Core.Resources;
using Auxim.VAFS;

namespace Auxim.Core.Tools;

public sealed record ToolDefinition(
    string Name,
    string Toolset,
    string Description,
    Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<string>> Handler)
{
    public string SchemaName => Name.Replace('.', '_');

    public IReadOnlyDictionary<string, object?> ParametersSchema { get; init; } =
        new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(),
            ["additionalProperties"] = true,
        };

    /// <summary>
    /// Declares argument-specific resource access for approval and audit. This
    /// declaration does not sandbox the handler or reduce host process rights.
    /// </summary>
    public Func<IReadOnlyDictionary<string, object?>, IReadOnlyList<ResourceAccess>>? ResourceAccessResolver { get; init; }

    /// <summary>
    /// Opts this trusted tool into context execution by supplying a definition whose handler and
    /// resource resolver use the supplied resource view. A missing binder disables the tool in contexts.
    /// </summary>
    public Func<VirtualAgentFileSystem, ToolDefinition>? ResourceViewBinder { get; init; }

    public IReadOnlyList<ResourceAccess> ResolveResourceAccesses(
        IReadOnlyDictionary<string, object?> arguments) =>
        ResourceAccessResolver?.Invoke(arguments) ?? [];
}
