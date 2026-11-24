namespace Auxim.Core.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, Registration> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Register(ToolDefinition definition) => Register(definition, supportsResourceRebinding: false);

    internal void RegisterRebindableTool(ToolDefinition definition) =>
        Register(definition, supportsResourceRebinding: true);

    private void Register(ToolDefinition definition, bool supportsResourceRebinding)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new ArgumentException("Tool name is required.", nameof(definition));
        }

        lock (_gate)
        {
            _tools[definition.Name] = new Registration(definition, supportsResourceRebinding);
        }
    }

    public IReadOnlyCollection<ToolDefinition> List()
    {
        lock (_gate)
        {
            return _tools.Values.Select(registration => registration.Definition).ToArray();
        }
    }

    internal Registration[] ListRegistrations()
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
            return _tools.TryGetValue(name, out var registration)
                ? registration.Definition
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

    internal readonly record struct Registration(ToolDefinition Definition, bool SupportsResourceRebinding);
}
