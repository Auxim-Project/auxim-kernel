using Auxim.Core.Tools;
using Auxim.Core.Resources;
using Auxim.VAFS;

namespace Auxim.Tools;

public static class FileTools
{
    public static void Register(ToolRegistry registry)
    {
        RegisterFileTool(registry, view => new ToolDefinition(
            "file.read",
            "files",
            "Reads a UTF-8 text file under /workspace, /tmp, or mounted /volumes paths.",
            (args, _) =>
            {
                var path = (view ?? Vafs()).ResolveToHostPath(Required(args, "path"));
                return Task.FromResult(File.ReadAllText(path));
            })
        {
            ParametersSchema = ObjectSchema([("path", "string", "Virtual file path to read, such as /workspace/README.md.")], ["path"]),
            ResourceAccessResolver = args =>
                [FileAccess(view, Required(args, "path"), ResourceAction.Read)],
        });

        RegisterFileTool(registry, view => new ToolDefinition(
            "file.write",
            "files",
            "Writes UTF-8 text to a file under writable /workspace, /tmp, or mounted /volumes paths.",
            (args, _) =>
            {
                var vafs = view ?? Vafs();
                var virtualPath = Required(args, "path");
                var path = vafs.ResolveToHostPath(virtualPath, requireWritable: true);
                var content = args.TryGetValue("content", out var value) ? value?.ToString() ?? "" : "";
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path, content);
                return Task.FromResult($"Wrote {content.Length} characters to {vafs.ToVirtualPath(path)}");
            })
        {
            ParametersSchema = ObjectSchema(
                [
                    ("path", "string", "Virtual file path to write, such as /workspace/notes.txt."),
                    ("content", "string", "Text content to write."),
                ],
                ["path", "content"]),
            ResourceAccessResolver = args =>
                [FileAccess(view, Required(args, "path"), ResourceAction.Write)],
        });

        RegisterFileTool(registry, view => new ToolDefinition(
            "file.patch",
            "files",
            "Replaces exact text in a file under writable /workspace, /tmp, or mounted /volumes paths.",
            (args, _) =>
            {
                var vafs = view ?? Vafs();
                var path = vafs.ResolveToHostPath(Required(args, "path"), requireWritable: true);
                var oldText = Required(args, "oldText");
                var newText = args.TryGetValue("newText", out var value) ? value?.ToString() ?? "" : "";
                var content = File.ReadAllText(path);
                if (!content.Contains(oldText, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("oldText was not found exactly once in the target file.");
                }

                var first = content.IndexOf(oldText, StringComparison.Ordinal);
                var second = content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal);
                if (second >= 0)
                {
                    throw new InvalidOperationException("oldText appears more than once; provide a more specific oldText.");
                }

                File.WriteAllText(path, content.Replace(oldText, newText, StringComparison.Ordinal));
                return Task.FromResult($"Patched {vafs.ToVirtualPath(path)}");
            })
        {
            ParametersSchema = ObjectSchema(
                [
                    ("path", "string", "Virtual file path to patch, such as /workspace/README.md."),
                    ("oldText", "string", "Exact text to replace. Must appear exactly once."),
                    ("newText", "string", "Replacement text."),
                ],
                ["path", "oldText", "newText"]),
            ResourceAccessResolver = args =>
            {
                var write = FileAccess(view, Required(args, "path"), ResourceAction.Write);
                return view is null ? [write] : [write with { Action = ResourceAction.Read, RequiresApproval = false }, write];
            },
        });

        RegisterFileTool(registry, view => new ToolDefinition(
            "file.list",
            "files",
            "Lists files and directories under /workspace, /tmp, or mounted /volumes paths.",
            (args, _) =>
            {
                var vafs = view ?? Vafs();
                var requestedPath = ListPath(args);
                if (requestedPath == "/")
                {
                    return Task.FromResult(string.Join(Environment.NewLine, ["/workspace/", "/tmp/", "/volumes/"]));
                }

                if (requestedPath == "/volumes")
                {
                    return Task.FromResult(string.Join(
                        Environment.NewLine,
                        vafs.ListMounts()
                            .Where(mount => mount.VirtualPath.StartsWith("/volumes/", StringComparison.Ordinal))
                            .OrderBy(mount => mount.VirtualPath)
                            .Select(mount => mount.VirtualPath + "/")));
                }

                var path = vafs.ResolveToHostPath(requestedPath);
                var entries = new DirectoryInfo(path).EnumerateFileSystemInfos()
                    .OrderBy(entry => entry.FullName)
                    .Select(entry => vafs.ToVirtualPath(entry.FullName) + (vafs.IsDirectoryForListing(entry) ? "/" : ""));
                return Task.FromResult(string.Join(Environment.NewLine, entries));
            })
        {
            ParametersSchema = ObjectSchema(("path", "string", "Virtual directory path to list. Defaults to /workspace.")),
            ResourceAccessResolver = args =>
            {
                var path = ListPath(args);
                if (path is "/" or "/volumes")
                {
                    return [FileAccess(null, path, ResourceAction.Read)];
                }

                return [FileAccess(view, path, ResourceAction.Read) with { IncludeDescendants = view is not null }];
            },
        });
    }

    internal static void RegisterFileTool(
        ToolRegistry registry, Func<VirtualAgentFileSystem?, ToolDefinition> create) =>
        registry.Register(create(null) with { ResourceViewBinder = view => create(view) });

    internal static ResourceAccess FileAccess(VirtualAgentFileSystem? view, string path, ResourceAction action)
    {
        var writable = action == ResourceAction.Write;
        var resource = view is null
            ? ResourceUri.Vafs(path)
            : new ResourceUri($"vafs:{view.ResolveVirtualPath(path, requireWritable: writable)}");
        return new ResourceAccess(action, resource, RequiresApproval: writable);
    }

    private static string ListPath(IReadOnlyDictionary<string, object?> args) =>
        args.TryGetValue("path", out var value) ? value?.ToString() ?? "/workspace" : "/workspace";

    internal static string Required(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value?.ToString()))
        {
            throw new ArgumentException($"Missing required argument '{key}'.");
        }

        return value.ToString()!;
    }

    internal static IReadOnlyDictionary<string, object?> ObjectSchema(
        params (string Name, string Type, string Description)[] properties)
    {
        return ObjectSchema(properties, []);
    }

    internal static IReadOnlyDictionary<string, object?> ObjectSchema(
        (string Name, string Type, string Description)[] properties,
        string[] required)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties.ToDictionary(
                property => property.Name,
                property => (object?)new Dictionary<string, object?>
                {
                    ["type"] = property.Type,
                    ["description"] = property.Description,
                }),
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    internal static VirtualAgentFileSystem Vafs() => VirtualAgentFileSystem.FromEnvironment();
}
