using System.Text.Json;
using Auxim.Core.Config;
using Auxim.Core.Tools;
using Auxim.Core.Resources;

namespace Auxim.Tools;

public static class TodoTools
{
    private static readonly object TodoGate = new();

    public static void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "todo.add",
            "todo",
            "Adds a todo item.",
            (args, _) =>
            {
                var text = FileTools.Required(args, "text");
                var todo = UpdateTodos(todos =>
                {
                    var added = new TodoItem(Guid.NewGuid().ToString("N")[..8], text, false, DateTimeOffset.UtcNow);
                    todos.Add(added);
                    return added;
                });
                return Task.FromResult($"Added todo {todo.Id}: {todo.Text}");
            })
        {
            ParametersSchema = FileTools.ObjectSchema([("text", "string", "Todo text.")], ["text"]),
            ResourceAccessResolver = _ =>
                [new ResourceAccess(ResourceAction.Write, new ResourceUri("auxim:todos"))],
        });

        registry.Register(new ToolDefinition(
            "todo.list",
            "todo",
            "Lists todo items.",
            (_, _) =>
            {
                var todos = ReadTodos();
                if (todos.Count == 0)
                {
                    return Task.FromResult("No todos.");
                }

                return Task.FromResult(string.Join(Environment.NewLine, todos.Select(todo =>
                    $"{(todo.Done ? "x" : " ")} {todo.Id} {todo.Text}")));
            })
        {
            ResourceAccessResolver = _ =>
                [new ResourceAccess(ResourceAction.Read, new ResourceUri("auxim:todos"))],
        });

        registry.Register(new ToolDefinition(
            "todo.done",
            "todo",
            "Marks a todo item as done.",
            (args, _) =>
            {
                var id = FileTools.Required(args, "id");
                var todo = UpdateTodos(todos =>
                {
                    var completed = todos.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Todo '{id}' not found.");
                    completed.Done = true;
                    return completed;
                });
                return Task.FromResult($"Marked done: {todo.Id}");
            })
        {
            ParametersSchema = FileTools.ObjectSchema([("id", "string", "Todo id.")], ["id"]),
            ResourceAccessResolver = args =>
                [new ResourceAccess(
                    ResourceAction.Write,
                    ResourceUri.Opaque("auxim-todo", FileTools.Required(args, "id")),
                    RequiresApproval: true)],
        });
    }

    private static List<TodoItem> ReadTodos()
    {
        var path = TodoPath();
        lock (TodoGate)
        {
            return LoadTodos(path);
        }
    }

    private static TodoItem UpdateTodos(Func<List<TodoItem>, TodoItem> update)
    {
        var path = TodoPath();
        lock (TodoGate)
        {
            var todos = LoadTodos(path);
            var result = update(todos);
            SaveTodos(path, todos);
            return result;
        }
    }

    private static List<TodoItem> LoadTodos(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var json = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<List<TodoItem>>(json, JsonOptions()) ?? [];
        }
        catch (JsonException exception)
        {
            // Never rewrite a file that could not be read; that would discard the user's todos.
            throw new InvalidOperationException(
                $"Todo file '{path}' is not valid JSON ({exception.Message}). Repair or remove the file and try again.",
                exception);
        }
    }

    private static void SaveTodos(string path, List<TodoItem> todos)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(todos, JsonOptions()) + Environment.NewLine);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch (IOException) when (File.Exists(path) && File.Exists(tempPath))
        {
            File.Replace(tempPath, path, null);
        }
    }

    private static string TodoPath() => Path.Combine(ConfigLoader.GetAuximHome(), "todos.json");

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed class TodoItem
    {
        public TodoItem(string id, string text, bool done, DateTimeOffset createdAt)
        {
            Id = id;
            Text = text;
            Done = done;
            CreatedAt = createdAt;
        }

        public string Id { get; set; }
        public string Text { get; set; }
        public bool Done { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
