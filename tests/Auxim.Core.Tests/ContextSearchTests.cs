using System.Diagnostics;
using Auxim.Core.Config;
using Auxim.Core.Resources;
using Auxim.Core.Runtime;
using Auxim.Core.Tools;
using Auxim.Tools;
using Auxim.VAFS;
using Xunit;

namespace Auxim.Core.Tests;

[Collection("VAFS environment")]
public sealed class ContextSearchTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("auxim-search-tests-");
    private readonly Dictionary<string, string?> _environment = new();

    public ContextSearchTests()
    {
        SetEnvironment("AUXIM_HOME", _root.CreateSubdirectory("home").FullName);
        SetEnvironment("AUXIM_WORKSPACE", null);
        SetEnvironment("AUXIM_VAFS_MOUNTS", null);
        SetEnvironment("AUXIM_TMP", _root.CreateSubdirectory("legacy-tmp").FullName);
        SetEnvironment("RIPGREP_CONFIG_PATH", null);
    }

    [Fact]
    public async Task ContextSearchKeepsItsCapturedMountsWhileAnotherRunUsesChangedSettings()
    {
        SelectManagedSearch();
        var firstRoot = Workspace("first");
        var secondRoot = Workspace("second");
        await WriteFile(firstRoot, "data.txt", "match first");
        await WriteFile(secondRoot, "data.txt", "match second");
        SetEnvironment("AUXIM_VAFS_MOUNTS", $"data={firstRoot}:ro");
        var entered = NewSignal();
        var continueFirst = NewSignal();
        var runtime = CreateRuntime(firstRoot, async (input, context) =>
        {
            var arguments = Args("match", "/volumes/data/./");
            if (input == "first")
            {
                entered.SetResult();
                await continueFirst.Task.WaitAsync(WaitLimit, context.CancellationToken);
            }

            var access = Assert.Single(context.ResolveToolResourceAccesses("file.search", arguments));
            Assert.Equal(ResourceAction.Read, access.Action);
            Assert.Equal("vafs:/volumes/data", access.Resource.Value);
            Assert.False(access.RequiresApproval);
            return new AgentResult(await context.InvokeToolAsync("file.search", arguments), []);
        });

        var first = runtime.RunInContextAsync("first");
        try
        {
            await entered.Task.WaitAsync(WaitLimit);
            SetEnvironment("AUXIM_VAFS_MOUNTS", $"data={secondRoot}:ro");
            var second = await runtime.RunInContextAsync("second").WaitAsync(WaitLimit);
            Assert.Equal("/volumes/data/data.txt:1:match second", second.FinalResponse);
        }
        finally
        {
            continueFirst.TrySetResult();
        }

        Assert.Equal("/volumes/data/data.txt:1:match first", (await first.WaitAsync(WaitLimit)).FinalResponse);
    }

    [UnixSearchFact]
    public Task ManagedSearchSkipsDiscoveredLinksAndRejectsExplicitEscapes() =>
        CheckSymlinkSearch(useRipgrep: false);

    [RipgrepSearchFact(requiresUnix: true)]
    public Task RipgrepSkipsDiscoveredLinksAndRejectsExplicitEscapes() =>
        CheckSymlinkSearch(useRipgrep: true);

    private async Task CheckSymlinkSearch(bool useRipgrep)
    {
        if (!useRipgrep)
        {
            SelectManagedSearch();
        }

        var workspace = Workspace();
        var outside = Workspace("outside");
        await WriteFile(outside, "secret.txt", "match secret");
        await WriteFile(outside, "ignore.txt", "visible.txt");
        await WriteFile(workspace, "visible.txt", "match visible");
        await WriteFile(Directory.CreateDirectory(Path.Combine(workspace, ".git")).FullName, "hidden.txt", "match git");
        File.CreateSymbolicLink(Path.Combine(workspace, "linked.txt"), Path.Combine(outside, "secret.txt"));
        Directory.CreateSymbolicLink(Path.Combine(workspace, "linked-directory"), outside);
        File.CreateSymbolicLink(Path.Combine(workspace, "chained.txt"), "linked-directory/secret.txt");
        File.CreateSymbolicLink(Path.Combine(workspace, ".rgignore"), Path.Combine(outside, "ignore.txt"));
        File.CreateSymbolicLink(Path.Combine(workspace, "inside.txt"), "visible.txt");

        var runtime = CreateRuntime(workspace, async (_, context) =>
        {
            var result = await context.InvokeToolAsync("file.search", Args("match", "/workspace"));
            Assert.Equal("/workspace/visible.txt:1:match visible", result);
            foreach (var path in new[] { "linked.txt", "linked-directory", "chained.txt" })
            {
                await Assert.ThrowsAsync<VirtualPathException>(() =>
                    context.InvokeToolAsync("file.search", Args("match", path)));
            }

            Assert.Equal("/workspace/inside.txt:1:match visible",
                await context.InvokeToolAsync("file.search", Args("match", "inside.txt")));
            return new AgentResult(result, []);
        });
        await runtime.RunInContextAsync("search");
    }

    [Fact]
    public Task ManagedSearchSupportsSingleFilesAndAppliesGlobalResultLimit() =>
        CheckSingleFileAndLimit(useRipgrep: false);

    [RipgrepSearchFact]
    public Task RipgrepSupportsSingleFilesAndAppliesGlobalResultLimit() =>
        CheckSingleFileAndLimit(useRipgrep: true);

    private async Task CheckSingleFileAndLimit(bool useRipgrep)
    {
        if (!useRipgrep)
        {
            SelectManagedSearch();
        }

        var workspace = Workspace();
        var lines = string.Join('\n', Enumerable.Repeat("match", 300));
        await WriteFile(workspace, "first.txt", lines);
        await WriteFile(workspace, "second.txt", lines);
        var runtime = CreateRuntime(workspace, async (_, context) =>
        {
            Assert.Equal("/workspace/first.txt:1:match",
                await context.InvokeToolAsync("file.search", Args(useRipgrep ? "match" : "MaTcH", "first.txt", 1)));
            Assert.Equal("No matches.", await context.InvokeToolAsync("file.search", Args("absent", "first.txt")));
            var limited = await context.InvokeToolAsync("file.search", Args("match", ".", 3));
            Assert.Equal(3, LineCount(limited));
            var capped = await context.InvokeToolAsync("file.search", Args("match", ".", 900));
            Assert.Equal(500, LineCount(capped));
            Assert.Contains("/workspace/first.txt:", capped);
            Assert.Contains("/workspace/second.txt:", capped);
            return new AgentResult("checked", []);
        });
        await runtime.RunInContextAsync("search");
    }

    [Fact]
    public async Task LegacyToolSearchUsesEnvironmentWorkspaceWithTheExistingSchemaAndReadDeclaration()
    {
        SelectManagedSearch();
        var workspace = Workspace();
        await WriteFile(workspace, "data.txt", "match");
        SetEnvironment("AUXIM_WORKSPACE", workspace);
        var runtime = CreateRuntime(Workspace("configured"), (_, _) => throw new NotSupportedException());
        var tool = Assert.Single(runtime.ListTools());
        Assert.Equal("file.search", tool.Name);
        Assert.Equal("file_search", tool.SchemaName);
        var arguments = Args("match", "data.txt");
        var access = Assert.Single(runtime.ResolveToolResourceAccesses("file.search", arguments));
        Assert.Equal(ResourceAction.Read, access.Action);
        Assert.Equal("vafs:/workspace/data.txt", access.Resource.Value);
        Assert.False(access.RequiresApproval);
        Assert.Equal("/workspace/data.txt:1:match", await runtime.InvokeToolAsync("file.search", arguments));
    }

    [RipgrepSearchFact]
    public async Task RipgrepPreservesDashPrefixedPatternReportsErrorsAndIgnoresAmbientConfig()
    {
        var workspace = Workspace();
        await WriteFile(workspace, "data file.txt", "unmatched\n--dash\n");
        var config = Path.Combine(_root.FullName, "rg.conf");
        await File.WriteAllTextAsync(config, "--invert-match\n");
        SetEnvironment("RIPGREP_CONFIG_PATH", config);
        var runtime = CreateRuntime(workspace, async (_, context) =>
        {
            var result = await context.InvokeToolAsync("file.search", Args("--dash", "data file.txt"));
            Assert.Equal("/workspace/data file.txt:2:--dash", result);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.InvokeToolAsync("file.search", Args("[", "data file.txt")));
            Assert.Contains("ripgrep failed with exit code 2", error.Message);
            Assert.Contains("regular expression", error.Message);
            Assert.DoesNotContain(_root.Name, error.ToString());
            return new AgentResult(result, []);
        });
        await runtime.RunInContextAsync("search");
    }

    [RipgrepSearchFact]
    public async Task RipgrepAppliesRemainingResultLimitAcrossFileBatches()
    {
        var workspace = Workspace();
        for (var index = 0; index < 80; index++)
        {
            await WriteFile(workspace, $"{index:D2}-{new string('a', 160)}.txt", "match");
        }

        var runtime = CreateRuntime(workspace, async (_, context) =>
        {
            var result = await context.InvokeToolAsync("file.search", Args("match", ".", 65));
            Assert.Equal(65, LineCount(result));
            Assert.Equal(65, result.Split('\n').Distinct().Count());
            return new AgentResult(result, []);
        });
        await runtime.RunInContextAsync("search");
    }

    [Fact]
    public async Task ManagedSearchCooperatesWithCancellationWhileReadingAFile()
    {
        SelectManagedSearch();
        var workspace = Workspace();
        await using (var file = File.Create(Path.Combine(workspace, "large.txt")))
        {
            var block = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("no result here\n", 4096)));
            for (var index = 0; index < 256; index++)
            {
                await file.WriteAsync(block);
            }
        }

        using var cancellation = new CancellationTokenSource();
        var runtime = CreateRuntime(workspace, async (_, context) =>
        {
            var search = context.InvokeToolAsync("file.search", Args("unmatched", "large.txt"));
            Assert.False(search.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(WaitLimit));
            return new AgentResult("checked", []);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.RunInContextAsync("search", cancellationToken: cancellation.Token).WaitAsync(WaitLimit));
    }

    [UnixSearchFact]
    public async Task CancellationTerminatesAndReapsRipgrepBeforeReturning()
    {
        var workspace = Workspace();
        await WriteFile(workspace, "data.txt", "match");
        var pidFile = Path.Combine(_root.FullName, "ripgrep.pid");
        SetEnvironment("AUXIM_TEST_RG_PID_FILE", pidFile);
        await InstallRipgrepShim("""
            #!/bin/sh
            printf '%s\n' "$$" > "$AUXIM_TEST_RG_PID_FILE"
            exec /bin/sleep 60
            """);
        using var cancellation = new CancellationTokenSource();
        var runtime = CreateRuntime(workspace, async (_, context) =>
            new AgentResult(await context.InvokeToolAsync("file.search", Args("match", "data.txt")), []));
        var search = runtime.RunInContextAsync("search", cancellationToken: cancellation.Token);
        using var timeout = new CancellationTokenSource(WaitLimit);
        try
        {
            int pid;
            while (!File.Exists(pidFile) || !int.TryParse(await File.ReadAllTextAsync(pidFile, timeout.Token), out pid))
            {
                await Task.Delay(10, timeout.Token);
            }

            using var process = Process.GetProcessById(pid);
            Assert.False(process.HasExited);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(WaitLimit));
            Assert.True(process.HasExited);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [UnixSearchFact]
    public async Task RipgrepDrainsStderrAndReportsFailureEvenAfterReachingResultLimit()
    {
        var workspace = Workspace();
        await WriteFile(workspace, "data.txt", "match");
        var diagnostics = Path.Combine(_root.FullName, "stderr.txt");
        await File.WriteAllTextAsync(diagnostics, $"search failed for {_root.FullName}\n" + new string('a', 128 * 1024));
        SetEnvironment("AUXIM_TEST_RG_DIAGNOSTICS", diagnostics);
        await InstallRipgrepShim("""
            #!/bin/sh
            for path do :; done
            printf '{"type":"match","data":{"path":{"text":"%s"},"lines":{"text":"match"},"line_number":1}}\n' "$path"
            /bin/cat "$AUXIM_TEST_RG_DIAGNOSTICS" >&2
            exit 2
            """);
        var runtime = CreateRuntime(workspace, async (_, context) =>
            new AgentResult(await context.InvokeToolAsync("file.search", Args("match", "data.txt", 1)), []));
        using var timeout = new CancellationTokenSource(WaitLimit);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.RunInContextAsync("search", cancellationToken: timeout.Token));
        Assert.Contains("ripgrep failed with exit code 2", error.Message);
        Assert.DoesNotContain(_root.Name, error.ToString());
    }

    [Fact]
    public async Task MissingRootReportsAnErrorWithoutExposingHostPaths()
    {
        SelectManagedSearch();
        var runtime = CreateRuntime(Workspace(), async (_, context) =>
            new AgentResult(await context.InvokeToolAsync("file.search", Args("match", "/workspace/missing")), []));
        var error = await Assert.ThrowsAsync<IOException>(() => runtime.RunInContextAsync("search"));
        Assert.Contains("/workspace/missing", error.Message);
        Assert.DoesNotContain(_root.FullName, error.ToString());
        Assert.DoesNotContain(new DirectoryInfo(_root.FullName).Name, error.ToString());
    }

    [UnixSearchFact(requiresUnprivilegedUser: true)]
    public Task ManagedTraversalReturnsMatchesAndReportsUnreadableDescendants() =>
        CheckUnreadableDescendants(useRipgrep: false);

    [RipgrepSearchFact(requiresUnix: true, requiresUnprivilegedUser: true)]
    public Task RipgrepTraversalReturnsMatchesAndReportsUnreadableDescendants() =>
        CheckUnreadableDescendants(useRipgrep: true);

    private async Task CheckUnreadableDescendants(bool useRipgrep)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (!useRipgrep)
        {
            SelectManagedSearch();
        }

        var workspace = Workspace();
        await WriteFile(workspace, "visible.txt", "match");
        var denied = Directory.CreateDirectory(Path.Combine(workspace, "denied")).FullName;
        await WriteFile(denied, "hidden.txt", "match");
        var deniedFile = Path.Combine(workspace, "denied.txt");
        await File.WriteAllTextAsync(deniedFile, "match");
        File.SetUnixFileMode(denied, UnixFileMode.None);
        File.SetUnixFileMode(deniedFile, UnixFileMode.None);
        try
        {
            var runtime = CreateRuntime(workspace, async (_, context) =>
            {
                var explicitRoot = await Assert.ThrowsAsync<IOException>(() =>
                    context.InvokeToolAsync("file.search", Args("match", "/workspace/denied")));
                Assert.DoesNotContain(_root.Name, explicitRoot.ToString());
                return new AgentResult(await context.InvokeToolAsync("file.search", Args("match", "/workspace")), []);
            });
            var result = await runtime.RunInContextAsync("search");
            Assert.Contains("/workspace/visible.txt:1:match", result.FinalResponse);
            Assert.Contains("Search incomplete: 2 skipped", result.FinalResponse);
            Assert.Contains("/workspace/denied", result.FinalResponse);
            Assert.DoesNotContain(_root.Name, result.FinalResponse);
        }
        finally
        {
            File.SetUnixFileMode(denied, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(deniedFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [UnixSearchFact]
    public async Task RipgrepStartupFailureIsExplicitAndDoesNotSwitchMatchingEngines()
    {
        var workspace = Workspace();
        await WriteFile(workspace, "data.txt", "match");
        var shim = Workspace("broken-shim");
        await WriteFile(shim, "rg", "not executable");
        SetEnvironment("PATH", shim);
        var runtime = CreateRuntime(workspace, async (_, context) =>
            new AgentResult(await context.InvokeToolAsync("file.search", Args("match", "data.txt")), []));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunInContextAsync("search"));
        Assert.Contains("Failed to start ripgrep", error.Message);
        Assert.DoesNotContain(_root.Name, error.ToString());
    }

    [Fact]
    public async Task RipgrepBudgetIncludesThePatternAndFixedArgumentsBeforeStartup()
    {
        var workspace = Workspace();
        await WriteFile(workspace, "data.txt", "match");
        var shim = Workspace("budget-shim");
        await WriteFile(shim, OperatingSystem.IsWindows() ? "rg.exe" : "rg", "must not be started");
        SetEnvironment("PATH", shim);
        var runtime = CreateRuntime(workspace, async (_, context) =>
            new AgentResult(await context.InvokeToolAsync("file.search", Args(new string('x', 11_950), "data.txt")), []));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunInContextAsync("search"));
        Assert.Contains("command-line size budget", error.Message);
        Assert.DoesNotContain(_root.Name, error.ToString());
    }

    [RipgrepSearchFact(requiresUnix: true)]
    public async Task RipgrepAtMaxDepthZeroDoesNotRecurseWhenAFileBecomesADirectory()
    {
        var workspace = Workspace();
        await WriteFile(workspace, "data.txt", "match before replacement");
        SetEnvironment("AUXIM_TEST_REAL_RG", Path.GetFullPath(RipgrepSearchFactAttribute.FindRipgrep()!));
        await InstallRipgrepShim("""
            #!/bin/sh
            for path do :; done
            /bin/rm "$path"
            /bin/mkdir "$path"
            printf '%s\n' 'match after replacement' > "$path/descendant.txt"
            exec "$AUXIM_TEST_REAL_RG" "$@"
            """);
        var runtime = CreateRuntime(workspace, async (_, context) =>
            new AgentResult(await context.InvokeToolAsync("file.search", Args("match", "data.txt")), []));
        Assert.Equal("No matches.", (await runtime.RunInContextAsync("search")).FinalResponse);
        Assert.True(File.Exists(Path.Combine(workspace, "data.txt", "descendant.txt")));
    }

    [UnixSearchFact]
    public async Task RipgrepExitWithAnInheritedPipeHasABoundedDrain()
    {
        var workspace = Workspace();
        await WriteFile(workspace, "data.txt", "match");
        var pidFile = Path.Combine(_root.FullName, "pipe-holder.pid");
        SetEnvironment("AUXIM_TEST_RG_PID_FILE", pidFile);
        await InstallRipgrepShim("""
            #!/bin/sh
            /bin/sleep 10 &
            printf '%s\n' "$!" > "$AUXIM_TEST_RG_PID_FILE"
            exit 1
            """);
        var runtime = CreateRuntime(workspace, async (_, context) =>
            new AgentResult(await context.InvokeToolAsync("file.search", Args("match", "data.txt")), []));
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.RunInContextAsync("search").WaitAsync(TimeSpan.FromSeconds(7)));
            Assert.Contains("output streams did not close", error.Message);
            Assert.DoesNotContain(_root.Name, error.ToString());
        }
        finally
        {
            if (File.Exists(pidFile) && int.TryParse(await File.ReadAllTextAsync(pidFile), out var pid))
            {
                try
                {
                    using var child = Process.GetProcessById(pid);
                    child.Kill();
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                {
                    // The helper may have already exited.
                }
            }
        }
    }

    [UnixSearchFact]
    public async Task RipgrepRejectsUnexpectedOutputPathsWithoutLeakingOrHanging()
    {
        var workspace = Workspace();
        await WriteFile(workspace, "data.txt", "match");
        SetEnvironment("AUXIM_TEST_RG_UNEXPECTED_PATH", Path.Combine(_root.FullName, "not-a-file-argument"));
        await InstallRipgrepShim("""
            #!/bin/sh
            printf '{"type":"match","data":{"path":{"text":"%s"},"lines":{"text":"match"},"line_number":1}}\n' "$AUXIM_TEST_RG_UNEXPECTED_PATH"
            exec /bin/sleep 20
            """);
        var runtime = CreateRuntime(workspace, async (_, context) =>
            new AgentResult(await context.InvokeToolAsync("file.search", Args("match", "data.txt")), []));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.RunInContextAsync("search").WaitAsync(WaitLimit));
        Assert.Equal("ripgrep returned a path outside its file arguments.", error.Message);
        Assert.DoesNotContain(_root.Name, error.ToString());
    }

    private AuximRuntimeService CreateRuntime(
        string workspace, Func<string, AuximExecutionContext, Task<AgentResult>> run) =>
        new(new Runner(run), new RuntimeToolService(() =>
        {
            var registry = new ToolRegistry();
            SearchTools.Register(registry);
            return registry;
        }), configLoader: () => new AuximConfig { Sandbox = new SandboxConfig { Workspace = workspace } });

    private static IReadOnlyDictionary<string, object?> Args(string pattern, string path, int? maxResults = null) =>
        new Dictionary<string, object?> { ["pattern"] = pattern, ["path"] = path, ["maxResults"] = maxResults };

    private string Workspace(string name = "workspace") => _root.CreateSubdirectory(name).FullName;

    private static Task WriteFile(string directory, string name, string content) =>
        File.WriteAllTextAsync(Path.Combine(directory, name), content);

    private static int LineCount(string output) => output.Split('\n').Length;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SelectManagedSearch() => SetEnvironment("PATH", Workspace("empty-path"));

    private async Task InstallRipgrepShim(string script)
    {
        var directory = Workspace("shim");
        var executable = Path.Combine(directory, "rg");
        await File.WriteAllTextAsync(executable, script + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        SetEnvironment("PATH", directory);
    }

    private void SetEnvironment(string key, string? value)
    {
        _environment.TryAdd(key, Environment.GetEnvironmentVariable(key));
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _environment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _root.Delete(recursive: true);
    }

    private sealed class Runner(Func<string, AuximExecutionContext, Task<AgentResult>> run) : IAgentRunner
    {
        public Task<AgentResult> RunAsync(AgentRunRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<AgentResult> RunInContextAsync(string input, AuximExecutionContext context) => run(input, context);
    }
}

internal sealed class UnixSearchFactAttribute : FactAttribute
{
    public UnixSearchFactAttribute(bool requiresUnprivilegedUser = false)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Requires Unix file permissions, symbolic links, or a POSIX shell.";
        }
        else if (requiresUnprivilegedUser && Environment.UserName == "root")
        {
            Skip = "Requires an unprivileged user to test filesystem permissions.";
        }
    }
}

internal sealed class RipgrepSearchFactAttribute : FactAttribute
{
    public RipgrepSearchFactAttribute(bool requiresUnix = false, bool requiresUnprivilegedUser = false)
    {
        if (requiresUnix)
        {
            Skip = new UnixSearchFactAttribute(requiresUnprivilegedUser).Skip;
        }

        if (Skip is null && FindRipgrep() is null)
        {
            Skip = "Requires an installed ripgrep executable on PATH.";
        }
    }

    public static string? FindRipgrep()
    {
        var executable = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim('"'), executable))
            .FirstOrDefault(File.Exists);
    }
}
