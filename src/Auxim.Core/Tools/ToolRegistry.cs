namespace Auxim.Core.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Register(ToolDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new ArgumentException("Tool name is required.", nameof(definition));
        }

        lock (_gate)
        {
            _tools[definition.Name] = definition;
        }
    }

    public IReadOnlyCollection<ToolDefinition> List()
    {
        lock (_gate)
        {
            return _tools.Values.ToArray();
        }
    }

    public ToolDefinition Get(string name)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(name, out var definition)
                ? definition
                : throw new InvalidOperationException($"Tool '{name}' is not registered.");
        }
    }

    public async Task<string> InvokeAsync(
        string name,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        return await Get(name).Handler(arguments, cancellationToken);
    }
}
