using System.Text.Json;
using Auxim.Tools;
using Xunit;

namespace Auxim.Core.Tests;

[Collection("VAFS environment")]
public sealed class TodoToolsTests : IDisposable
{
    private readonly string _home;
    private readonly string? _previousHome;

    public TodoToolsTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "auxim-todo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _previousHome = Environment.GetEnvironmentVariable("AUXIM_HOME");
        Environment.SetEnvironmentVariable("AUXIM_HOME", _home);
    }

    [Fact]
    public async Task AddsListsAndCompletesTodosInLegacyFormat()
    {
        var registry = BuiltInTools.CreateDefaultRegistry();

        var added = await registry.InvokeAsync("todo.add", Args(("text", "write docs")));
        var id = IdFrom(added);
        var listed = await registry.InvokeAsync("todo.list", Args());
        var completed = await registry.InvokeAsync("todo.done", Args(("id", id)));
        var relisted = await registry.InvokeAsync("todo.list", Args());

        Assert.Matches("^[0-9a-f]{8}$", id);
        Assert.Equal($"Added todo {id}: write docs", added);
        Assert.Equal($"  {id} write docs", listed);
        Assert.Equal($"Marked done: {id}", completed);
        Assert.Equal($"x {id} write docs", relisted);
    }

    [Fact]
    public async Task PersistsCamelCaseJsonWithoutLeavingTemporaryFiles()
    {
        var registry = BuiltInTools.CreateDefaultRegistry();

        var added = await registry.InvokeAsync("todo.add", Args(("text", "write docs")));
        var id = IdFrom(added);
        var path = Path.Combine(_home, "todos.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entry = Assert.Single(document.RootElement.EnumerateArray());

        Assert.Equal(id, entry.GetProperty("id").GetString());
        Assert.Equal("write docs", entry.GetProperty("text").GetString());
        Assert.False(entry.GetProperty("done").GetBoolean());
        Assert.NotEqual(default, entry.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Empty(Directory.GetFiles(_home, "*.tmp"));

        // Reading through a second invocation proves the persisted document deserializes back into TodoItem.
        Assert.Equal($"  {id} write docs", await registry.InvokeAsync("todo.list", Args()));
    }

    [Fact]
    public async Task ConcurrentAddsKeepEveryEntry()
    {
        const int Count = 25;
        var registry = BuiltInTools.CreateDefaultRegistry();

        await Task.WhenAll(Enumerable.Range(0, Count).Select(index => Task.Run(() =>
            registry.InvokeAsync("todo.add", Args(("text", $"todo-{index}"))))));

        var lines = (await registry.InvokeAsync("todo.list", Args()))
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var listed = string.Join(Environment.NewLine, lines);

        Assert.Equal(Count, lines.Length);
        Assert.All(Enumerable.Range(0, Count), index => Assert.Contains($"todo-{index}", listed));
    }

    [Fact]
    public async Task CorruptedTodoFileIsReportedAndPreserved()
    {
        var registry = BuiltInTools.CreateDefaultRegistry();
        var path = Path.Combine(_home, "todos.json");
        const string Corrupted = "{ \"id\": ";
        File.WriteAllText(path, Corrupted);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.InvokeAsync("todo.add", Args(("text", "write docs"))));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.IsType<JsonException>(exception.InnerException);
        Assert.Equal(Corrupted, File.ReadAllText(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.InvokeAsync("todo.list", Args()));
        Assert.Equal(Corrupted, File.ReadAllText(path));
    }

    [Fact]
    public async Task UnknownTodoIdDoesNotRewriteTheFile()
    {
        var registry = BuiltInTools.CreateDefaultRegistry();
        await registry.InvokeAsync("todo.add", Args(("text", "write docs")));
        var path = Path.Combine(_home, "todos.json");
        var before = File.ReadAllText(path);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.InvokeAsync("todo.done", Args(("id", "00000000"))));

        Assert.Equal("Todo '00000000' not found.", exception.Message);
        Assert.Equal(before, File.ReadAllText(path));
    }

    private static Dictionary<string, object?> Args(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => (object?)entry.Value);

    private static string IdFrom(string added) => added.Split(' ')[2].TrimEnd(':');

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AUXIM_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }
}
