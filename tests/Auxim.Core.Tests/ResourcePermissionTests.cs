using Auxim.Core.Approval;
using Auxim.Core.Config;
using Auxim.Core.Resources;
using Auxim.Core.Runtime;
using Auxim.Core.Tools;
using Auxim.Tools;
using Auxim.VAFS;
using Xunit;

namespace Auxim.Core.Tests;

[Collection("VAFS environment")]
public sealed class ResourcePermissionTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("auxim-resource-permission-tests-");
    private readonly string? _previousWorkspace = Environment.GetEnvironmentVariable("AUXIM_WORKSPACE");
    private readonly string? _previousMounts = Environment.GetEnvironmentVariable("AUXIM_VAFS_MOUNTS");
    private readonly string _home;
    private readonly string _workspace;

    public ResourcePermissionTests()
    {
        Environment.SetEnvironmentVariable("AUXIM_WORKSPACE", null);
        Environment.SetEnvironmentVariable("AUXIM_VAFS_MOUNTS", null);
        _home = _root.CreateSubdirectory("home").FullName;
        _workspace = _root.CreateSubdirectory("workspace").FullName;
    }

    [Fact]
    public async Task ExactMetadataAndFileGrantsDoNotAuthorizeSubtrees()
    {
        WriteFile("public/note.txt", "allowed");
        WriteFile("public/other.txt", "denied");
        var runtime = CreateRuntime(async (_, context) =>
        {
            var root = Assert.Single(context.ResolveToolResourceAccesses("file.list", Args("/")));
            Assert.False(root.IncludeDescendants);
            Assert.Contains("/workspace/", await context.InvokeToolAsync("file.list", Args("/")));
            Assert.Equal("", await context.InvokeToolAsync("file.list", Args("/volumes")));
            var access = Assert.Single(context.ResolveToolResourceAccesses("file.read", Args("public/./note.txt")));
            Assert.Equal("vafs:/workspace/public/note.txt", access.Resource.Value);
            Assert.Equal("allowed", await context.InvokeToolAsync("file.read", Args("public/./note.txt")));
            await AssertDenied(context, "file.list", Args("/workspace"));
            await AssertDenied(context, "file.search", Args("public/note.txt", ("pattern", "allowed")));
            await AssertDenied(context, "file.read", Args("public/other.txt"));
            return new AgentResult("done", []);
        });

        await runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            ResourcePermissions =
            [
                new(ResourceAction.Read, "/"),
                new(ResourceAction.Read, "/volumes"),
                new(ResourceAction.Read, "/workspace"),
                new(ResourceAction.Read, "/workspace/public/note.txt"),
            ],
        });
    }

    [Fact]
    public async Task SubtreePermissionsMatchWholeComponentsAndSeparateReadFromWrite()
    {
        WriteFile("public/note.txt", "allowed");
        WriteFile("publicity/note.txt", "denied");
        var runtime = CreateRuntime(async (_, context) =>
        {
            var args = Args("public", ("pattern", "allowed"));
            Assert.True(Assert.Single(context.ResolveToolResourceAccesses("file.search", args)).IncludeDescendants);
            Assert.Equal("/workspace/public/note.txt:1:allowed", await context.InvokeToolAsync("file.search", args));
            Assert.Contains("/workspace/public/note.txt", await context.InvokeToolAsync("file.list", Args("public")));
            await AssertDenied(context, "file.read", Args("publicity/note.txt"));
            await AssertDenied(context, "file.read", Args("public/../publicity/note.txt"));
            await AssertDenied(context, "file.read", Args("PUBLIC/note.txt"));
            await AssertDenied(context, "file.write", Args("public/note.txt", ("content", "denied")));
            return new AgentResult("done", []);
        });

        await runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            ResourcePermissions = [new(ResourceAction.Read, "/workspace/public", IncludeDescendants: true)],
        });
    }

    [UnixSearchFact]
    public async Task DirectoryListingPreservesUnrestrictedLinkPresentationAndHidesRestrictedLinkTypes()
    {
        var outside = _root.CreateSubdirectory("outside").FullName;
        Directory.CreateDirectory(HostPath("directory"));
        Directory.CreateSymbolicLink(HostPath("link"), outside);
        var runtime = CreateRuntime(async (_, context) =>
            new AgentResult(await context.InvokeToolAsync("file.list", Args("/workspace")), []));

        var unrestricted = await runtime.RunInContextAsync("unrestricted");
        var restricted = await runtime.RunInContextAsync("restricted", new AuximRuntimeOptions
        {
            ResourcePermissions = [new(ResourceAction.Read, "/workspace", IncludeDescendants: true)],
        });

        Assert.Equal(["/workspace/directory/", "/workspace/link/"],
            unrestricted.FinalResponse.Split(Environment.NewLine));
        Assert.Equal(["/workspace/directory/", "/workspace/link"],
            restricted.FinalResponse.Split(Environment.NewLine));
    }

    [Fact]
    public void RestrictedListingUsesCapturedMetadataForEntriesThatDisappear()
    {
        var directory = Directory.CreateDirectory(HostPath("directory")).FullName;
        WriteFile("note.txt", "original");
        var file = HostPath("note.txt");
        var view = new VirtualAgentFileSystem(new VirtualAgentFileSystemOptions
        {
            WorkspaceHostPath = _workspace,
            TmpHostPath = _root.CreateSubdirectory("tmp").FullName,
        }, followDirectoryLinksInListings: false);
        var entries = new DirectoryInfo(_workspace).EnumerateFileSystemInfos().ToDictionary(entry => entry.Name);

        Directory.Delete(directory);
        File.Delete(file);

        Assert.True(view.IsDirectoryForListing(entries["directory"]));
        Assert.False(view.IsDirectoryForListing(entries["note.txt"]));
    }

    [Fact]
    public async Task SubtreeWriteGrantsAllowExistingDescendantDirectoriesOnly()
    {
        Directory.CreateDirectory(HostPath("writable/nested"));
        Directory.CreateDirectory(HostPath("writable-sibling"));
        var handler = new RecordingApprovalHandler();
        var runtime = CreateRuntime(async (_, context) =>
        {
            await AssertDenied(context, "file.write", Args("writable-sibling/note.txt", ("content", "denied")));
            await AssertDenied(context, "file.write", Args("writable/missing/note.txt", ("content", "denied")));
            Assert.Empty(handler.Requests);

            var args = Args("writable/nested/note.txt", ("content", "allowed"));
            var access = Assert.Single(context.ResolveToolResourceAccesses("file.write", args));
            Assert.Equal(ResourceAction.Write, access.Action);
            await context.InvokeToolAsync("file.write", args);
            await AssertDenied(context, "file.read", args);
            return new AgentResult("done", []);
        });

        await runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            ResourcePermissions = [new(ResourceAction.Write, "/workspace/writable", IncludeDescendants: true)],
            ApprovalHandler = handler,
        });

        Assert.Single(handler.Requests);
        Assert.Equal("allowed", File.ReadAllText(HostPath("writable/nested/note.txt")));
        Assert.False(File.Exists(HostPath("writable-sibling/note.txt")));
        Assert.False(Directory.Exists(HostPath("writable/missing")));
    }

    [Fact]
    public async Task DenialsPrecedeApprovalAndAllowedWritesStillRequireFreshApproval()
    {
        WriteFile("original.txt", "original");
        WriteFile("patchable.txt", "before");
        var handler = new RecordingApprovalHandler();
        var events = new List<RuntimeEvent>();
        var runtime = CreateRuntime(async (_, context) =>
        {
            await AssertDenied(context, "file.read", Args("original.txt"));
            await AssertDenied(context, "file.write", Args("outside.txt", ("content", "denied")));
            await AssertDenied(context, "file.patch", Args("original.txt", ("oldText", "original"), ("newText", "denied")));
            await AssertDenied(context, "file.write", Args("missing/deep.txt", ("content", "denied")));
            Assert.Throws<InvalidOperationException>(() =>
                context.ResolveToolResourceAccesses("file.search", Args("original.txt")));
            Assert.Empty(handler.Requests);
            Assert.Empty(events.OfType<RuntimeToolStartedEvent>());

            var write = Args("original.txt", ("content", "updated"));
            await context.InvokeToolAsync("file.write", write);
            await context.InvokeToolAsync("file.write", write);
            await context.InvokeToolAsync("file.patch", Args("patchable.txt", ("oldText", "before"), ("newText", "after")));
            return new AgentResult("done", []);
        });

        await runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            AllowedToolNames = ["file.read", "file.write", "file.patch"],
            ResourcePermissions =
            [
                new(ResourceAction.Write, "/workspace/original.txt"),
                new(ResourceAction.Write, "/workspace/missing/deep.txt"),
                new(ResourceAction.Read, "/workspace/patchable.txt"),
                new(ResourceAction.Write, "/workspace/patchable.txt"),
            ],
            ApprovalHandler = handler,
            EventSink = new DelegateRuntimeEventSink((value, _) =>
            {
                events.Add(value);
                return ValueTask.CompletedTask;
            }),
        });

        Assert.Equal(["vafs:/workspace/original.txt", "vafs:/workspace/original.txt", "vafs:/workspace/patchable.txt"],
            handler.Requests.Select(request => Assert.Single(request.ResourceAccesses).Resource.Value));
        Assert.All(events.OfType<RuntimeApprovalResolvedEvent>(), value => Assert.False(value.Remembered));
        Assert.Empty(new ToolApprovalService(_home).ListGrants());
        Assert.Equal("updated", File.ReadAllText(HostPath("original.txt")));
        Assert.Equal("after", File.ReadAllText(HostPath("patchable.txt")));
        Assert.False(File.Exists(HostPath("outside.txt")));
        Assert.False(Directory.Exists(HostPath("missing")));
    }

    [Fact]
    public async Task ResourcePermissionsDoNotOverrideReadOnlyMounts()
    {
        var volume = _root.CreateSubdirectory("readonly").FullName;
        var file = Path.Combine(volume, "note.txt");
        File.WriteAllText(file, "original");
        var runtime = CreateRuntime(async (_, context) =>
        {
            var path = Args("/volumes/data/note.txt", ("content", "denied"));
            Assert.Equal("original", await context.InvokeToolAsync("file.read", path));
            Assert.Throws<VirtualPathException>(() => context.ResolveToolResourceAccesses("file.write", path));
            await Assert.ThrowsAsync<VirtualPathException>(() => context.InvokeToolAsync("file.write", path));
            return new AgentResult("done", []);
        }, mounts: [new SandboxMountConfig { Name = "data", HostPath = volume, ReadOnly = true }]);

        await runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            ResourcePermissions =
            [
                new(ResourceAction.Read, "/volumes/data/note.txt"),
                new(ResourceAction.Write, "/volumes/data/note.txt"),
            ],
        });
        Assert.Equal("original", File.ReadAllText(file));
    }

    [Fact]
    public async Task ASubtreeReadDoesNotAcceptAlternateCaseForAnExistingFile()
    {
        WriteFile("public/note.txt", "original");
        var runtime = CreateRuntime(async (_, context) =>
        {
            var path = Args("public/NOTE.txt");
            if (File.Exists(HostPath("public/NOTE.txt")))
            {
                Assert.Throws<VirtualPathException>(() => context.ResolveToolResourceAccesses("file.read", path));
                await Assert.ThrowsAsync<VirtualPathException>(() => context.InvokeToolAsync("file.read", path));
            }
            else
            {
                await Assert.ThrowsAsync<FileNotFoundException>(() => context.InvokeToolAsync("file.read", path));
            }

            return new AgentResult(await context.InvokeToolAsync("file.read", Args("public/note.txt")), []);
        });

        var result = await runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            ResourcePermissions = [new(ResourceAction.Read, "/workspace/public", IncludeDescendants: true)],
        });
        Assert.Equal("original", result.FinalResponse);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("scheme")]
    [InlineData("action")]
    [InlineData("noncanonical")]
    [InlineData("additional")]
    public async Task ActiveRestrictionsRejectMissingOrUnsupportedDeclarations(string kind)
    {
        var allowed = new ResourceAccess(ResourceAction.Read, ResourceUri.Vafs("/workspace/allowed.txt"));
        IReadOnlyList<ResourceAccess> accesses = kind switch
        {
            "missing" => [],
            "scheme" => [allowed with { Resource = new ResourceUri("test:allowed") }],
            "action" => [allowed with { Action = ResourceAction.Execute }],
            "noncanonical" => [allowed with { Resource = new ResourceUri("vafs:/workspace/./allowed.txt") }],
            _ => [allowed, allowed with { Resource = ResourceUri.Vafs("/workspace/denied.txt") }],
        };
        var registry = new ToolRegistry();
        var definition = new ToolDefinition("test.tool", "test", "", (_, _) =>
            throw new InvalidOperationException("The unapproved handler must not run."))
        {
            ResourceAccessResolver = _ => accesses,
        };
        registry.Register(definition with { ResourceViewBinder = _ => definition });
        var runtime = CreateRuntime(async (_, context) =>
        {
            await AssertDenied(context, "test.tool", Args("/workspace/allowed.txt"));
            return new AgentResult("done", []);
        }, registry: registry);

        await runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            ResourcePermissions = [new(ResourceAction.Read, "/workspace/allowed.txt")],
        });
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/volumes")]
    [InlineData("/workspace/dir/../note.txt")]
    [InlineData("/workspace/note.txt:stream")]
    [InlineData("note.txt")]
    public async Task InvalidPermissionPathsAreRejectedAtContextSetup(string path)
    {
        var runtime = CreateRuntime((_, _) => throw new InvalidOperationException("The runner must not start."));

        await Assert.ThrowsAsync<ArgumentException>(() => runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            ResourcePermissions = [new(ResourceAction.Read, path, IncludeDescendants: true)],
        }));
    }

    [Fact]
    public async Task EmptyResourcePermissionsDenyAllAccess()
    {
        WriteFile("note.txt", "original");
        var runtime = CreateRuntime(async (_, context) =>
        {
            await AssertDenied(context, "file.read", Args("note.txt"));
            await AssertDenied(context, "file.list", Args("/"));
            return new AgentResult("done", []);
        });

        await runtime.RunInContextAsync("run", new AuximRuntimeOptions { ResourcePermissions = [] });
    }

    [UnixSearchFact]
    public async Task RestrictedReadsRejectLinksInsideAPermittedSubtree()
    {
        WriteFile("public/visible.txt", "allowed");
        WriteFile("private/secret.txt", "secret");
        File.CreateSymbolicLink(HostPath("public/link.txt"), "../private/secret.txt");
        Directory.CreateSymbolicLink(HostPath("public/link-dir"), "../private");
        var runtime = CreateRuntime(async (_, context) =>
        {
            foreach (var path in new[] { "public/link.txt", "public/link-dir/secret.txt" })
            {
                Assert.Throws<VirtualPathException>(() => context.ResolveToolResourceAccesses("file.read", Args(path)));
                await Assert.ThrowsAsync<VirtualPathException>(() => context.InvokeToolAsync("file.read", Args(path)));
            }

            var output = await context.InvokeToolAsync("file.search", Args("public", ("pattern", "secret")));
            Assert.Equal("No matches.", output);
            return new AgentResult("done", []);
        });

        await runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            ResourcePermissions = [new(ResourceAction.Read, "/workspace/public", IncludeDescendants: true)],
        });
    }

    [UnixSearchFact]
    public async Task ALinkIntroducedDuringApprovalIsRejectedBeforeWriting()
    {
        WriteFile("secret.txt", "secret");
        var handler = new RecordingApprovalHandler(() =>
            File.CreateSymbolicLink(HostPath("target.txt"), "secret.txt"));
        var runtime = CreateRuntime(async (_, context) =>
        {
            var args = Args("target.txt", ("content", "denied"));
            Assert.Single(context.ResolveToolResourceAccesses("file.write", args));
            await Assert.ThrowsAsync<VirtualPathException>(() => context.InvokeToolAsync("file.write", args));
            return new AgentResult("done", []);
        });

        await runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            ResourcePermissions = [new(ResourceAction.Write, "/workspace/target.txt")],
            ApprovalHandler = handler,
        });
        Assert.Single(handler.Requests);
        Assert.Equal("secret", File.ReadAllText(HostPath("secret.txt")));
    }

    [UnixSearchFact]
    public async Task ReplacingAnIntermediateDirectoryDuringApprovalIsRejectedBeforeWriting()
    {
        WriteFile("public/nested/target.txt", "original");
        WriteFile("private/target.txt", "secret");
        var handler = new RecordingApprovalHandler(() =>
        {
            Directory.Move(HostPath("public/nested"), HostPath("public/original"));
            Directory.CreateSymbolicLink(HostPath("public/nested"), "../private");
        });
        var runtime = CreateRuntime(async (_, context) =>
        {
            var args = Args("public/nested/target.txt", ("content", "denied"));
            Assert.Single(context.ResolveToolResourceAccesses("file.write", args));
            await Assert.ThrowsAsync<VirtualPathException>(() => context.InvokeToolAsync("file.write", args));
            return new AgentResult("done", []);
        });

        await runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            ResourcePermissions = [new(ResourceAction.Write, "/workspace/public", IncludeDescendants: true)],
            ApprovalHandler = handler,
        });

        Assert.Single(handler.Requests);
        Assert.Equal("original", File.ReadAllText(HostPath("public/original/target.txt")));
        Assert.Equal("secret", File.ReadAllText(HostPath("private/target.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestrictedViewsRejectEqualAndNestedMountRoots(bool nested)
    {
        var volume = nested ? Directory.CreateDirectory(HostPath("nested")).FullName : _workspace;
        await AssertOverlappingMountsRejected(volume);
    }

    [UnixSearchFact]
    public async Task RestrictedViewsRejectMountRootsAliasedThroughLinks()
    {
        var volume = Path.Combine(_root.FullName, "alias");
        Directory.CreateSymbolicLink(volume, _workspace);
        await AssertOverlappingMountsRejected(volume);
    }

    [Fact]
    public async Task UnrestrictedViewsCanUseOverlappingMountRoots()
    {
        WriteFile("note.txt", "original");
        var runtime = CreateRuntime(async (_, context) =>
        {
            Assert.Equal("original", await context.InvokeToolAsync("file.read", Args("/workspace/note.txt")));
            return new AgentResult(await context.InvokeToolAsync("file.read", Args("/volumes/alias/note.txt")), []);
        }, mounts: [new SandboxMountConfig { Name = "alias", HostPath = _workspace }]);

        Assert.Equal("original", (await runtime.RunInContextAsync("run")).FinalResponse);
    }

    [Fact]
    public async Task CaseVariantHostRootsRequireDistinctVerifiedDirectories()
    {
        var alternateCase = Path.Combine(_root.FullName, "WORKSPACE");
        var rootIsAlias = Directory.Exists(alternateCase);
        var runtime = CreateRuntime(async (_, context) =>
            new AgentResult(await context.InvokeToolAsync("file.read", Args("/volumes/alternate/note.txt")), []),
            mounts: [new SandboxMountConfig { Name = "alternate", HostPath = alternateCase }]);
        var options = new AuximRuntimeOptions
        {
            ResourcePermissions = [new(ResourceAction.Read, "/volumes/alternate/note.txt")],
        };
        if (rootIsAlias)
        {
            var error = await Assert.ThrowsAsync<VirtualPathException>(() => runtime.RunInContextAsync("run", options));
            Assert.Contains("exact filesystem names", error.Message);
            return;
        }

        var overlap = await Assert.ThrowsAsync<ArgumentException>(() => runtime.RunInContextAsync("run", options));
        Assert.Contains("overlapping mounts", overlap.Message);
        Directory.CreateDirectory(alternateCase);
        File.WriteAllText(Path.Combine(alternateCase, "note.txt"), "alternate");
        if (OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<ArgumentException>(() => runtime.RunInContextAsync("run", options));
        }
        else
        {
            Assert.Equal("alternate", (await runtime.RunInContextAsync("run", options)).FinalResponse);
        }
    }

    [Fact]
    public async Task ConcurrentRunsKeepIndependentPermissionSnapshots()
    {
        WriteFile("first.txt", "first");
        WriteFile("second.txt", "second");
        var firstEntered = NewSignal();
        var secondEntered = NewSignal();
        var mayRead = NewSignal();
        var runtime = CreateRuntime(async (input, context) =>
        {
            (input == "first" ? firstEntered : secondEntered).SetResult();
            await mayRead.Task.WaitAsync(TestTimeout, context.CancellationToken);
            var denied = input == "first" ? "second.txt" : "first.txt";
            await AssertDenied(context, "file.read", Args(denied));
            return new AgentResult(await context.InvokeToolAsync("file.read", Args($"{input}.txt")), []);
        });
        var grants = new List<VafsResourcePermission> { new(ResourceAction.Read, "/workspace/first.txt") };
        var options = new AuximRuntimeOptions { ResourcePermissions = grants };
        var first = runtime.RunInContextAsync("first", options);
        Task<AgentResult> second;
        try
        {
            await firstEntered.Task.WaitAsync(TestTimeout);
            grants[0] = new(ResourceAction.Read, "/workspace/second.txt");
            second = runtime.RunInContextAsync("second", options);
            await secondEntered.Task.WaitAsync(TestTimeout);
            grants.Clear();
        }
        finally
        {
            mayRead.TrySetResult();
        }

        var results = await Task.WhenAll(first, second).WaitAsync(TestTimeout);
        Assert.Equal(["first", "second"], results.Select(result => result.FinalResponse));
    }

    private async Task AssertOverlappingMountsRejected(string volume)
    {
        var runtime = CreateRuntime((_, _) => throw new InvalidOperationException("The runner must not start."),
            mounts: [new SandboxMountConfig { Name = "alias", HostPath = volume }]);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => runtime.RunInContextAsync("run", new AuximRuntimeOptions
        {
            ResourcePermissions = [new(ResourceAction.Read, "/workspace", IncludeDescendants: true)],
        }));
        Assert.Contains("overlapping mounts", error.Message);
    }

    private AuximRuntimeService CreateRuntime(
        Func<string, AuximExecutionContext, Task<AgentResult>> run,
        ToolRegistry? registry = null,
        List<SandboxMountConfig>? mounts = null) =>
        new(new Runner(run), new RuntimeToolService(() => registry ?? CreateFileRegistry()),
            configLoader: () => new AuximConfig
            {
                Sandbox = new SandboxConfig { Workspace = _workspace, Mounts = mounts ?? [] },
            },
            homeDirectory: () => _home);

    private static ToolRegistry CreateFileRegistry()
    {
        var registry = new ToolRegistry();
        FileTools.Register(registry);
        SearchTools.Register(registry);
        return registry;
    }

    private static async Task AssertDenied(
        AuximExecutionContext context, string tool, IReadOnlyDictionary<string, object?> args)
    {
        Assert.Throws<UnauthorizedAccessException>(() => context.ResolveToolResourceAccesses(tool, args));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => context.InvokeToolAsync(tool, args));
    }

    private static IReadOnlyDictionary<string, object?> Args(string path, params (string Key, string Value)[] values)
    {
        var arguments = values.ToDictionary(value => value.Key, value => (object?)value.Value);
        arguments.Add("path", path);
        return arguments;
    }

    private string HostPath(string path) => Path.Combine(_workspace, path);

    private void WriteFile(string path, string text)
    {
        var fullPath = HostPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AUXIM_WORKSPACE", _previousWorkspace);
        Environment.SetEnvironmentVariable("AUXIM_VAFS_MOUNTS", _previousMounts);
        _root.Delete(recursive: true);
    }

    private sealed class RecordingApprovalHandler(Action? onRequest = null) : IApprovalHandler
    {
        public List<ApprovalRequest> Requests { get; } = [];

        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            onRequest?.Invoke();
            return Task.FromResult(ApprovalResponse.Allow(remember: true));
        }
    }

    private sealed class Runner(Func<string, AuximExecutionContext, Task<AgentResult>> run) : IAgentRunner
    {
        public Task<AgentResult> RunAsync(AgentRunRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<AgentResult> RunInContextAsync(string input, AuximExecutionContext context) => run(input, context);
    }
}
