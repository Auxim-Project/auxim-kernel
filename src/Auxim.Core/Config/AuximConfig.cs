namespace Auxim.Core.Config;

public sealed class AuximConfig
{
    public ModelConfig Model { get; init; } = new();
    public AgentConfig Agent { get; init; } = new();
    public DisplayConfig Display { get; init; } = new();
    public SandboxConfig Sandbox { get; init; } = new();

    internal AuximConfig Snapshot() => new()
    {
        Model = Model,
        Agent = Agent,
        Display = Display,
        Sandbox = new SandboxConfig
        {
            Workspace = Sandbox.Workspace,
            Mounts = [.. Sandbox.Mounts],
            Shell = new ShellSandboxConfig
            {
                Enabled = Sandbox.Shell.Enabled,
                AllowedCommands = [.. Sandbox.Shell.AllowedCommands],
            },
        },
    };
}

public sealed class ModelConfig
{
    public string Provider { get; init; } = "local";
    public string Name { get; init; } = "placeholder";
    public string? BaseUrl { get; init; }
}

public sealed class AgentConfig
{
    public int MaxIterations { get; init; } = 90;
}

public sealed class DisplayConfig
{
    public string Skin { get; init; } = "default";
}

public sealed class SandboxConfig
{
    public string? Workspace { get; init; }
    public List<SandboxMountConfig> Mounts { get; init; } = [];
    public ShellSandboxConfig Shell { get; init; } = new();
}

public sealed class SandboxMountConfig
{
    public string Name { get; init; } = "";
    public string HostPath { get; init; } = "";
    public bool ReadOnly { get; init; }
}

public sealed class ShellSandboxConfig
{
    public bool Enabled { get; init; }
    public List<string> AllowedCommands { get; init; } = [];
}
