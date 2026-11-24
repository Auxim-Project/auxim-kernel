using System.Collections.Concurrent;
using System.Text.Json;
using Auxim.Core.Approval;
using Auxim.Core.Config;
using Auxim.Core.Resources;
using Auxim.Core.Runtime;
using Auxim.Core.Tools;
using Auxim.Tools;
using Auxim.VAFS;
using Xunit;

namespace Auxim.Core.Tests;

[CollectionDefinition("VAFS environment", DisableParallelization = true)]
public sealed class VafsEnvironmentCollection;

[Collection("VAFS environment")]
public sealed class ExecutionResourceViewTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("auxim-resource-tests-");
    private readonly Dictionary<string, string?> _environment = new();
    private readonly string _home;

    public ExecutionResourceViewTests()
    {
        _home = MakeDirectory("home");
        SetEnvironment("AUXIM_HOME", _home);
        SetEnvironment("AUXIM_WORKSPACE", null);
        SetEnvironment("AUXIM_VAFS_MOUNTS", null);
        SetEnvironment("AUXIM_TMP", MakeDirectory("legacy-tmp"));
    }

    [Fact]
    public async Task MappingStaysFixedWhileApprovalWaitsAndNewRunCapturesChangedSettings()
    {
        var oldWorkspace = MakeDirectory("workspace-old");
        var newWorkspace = MakeDirectory("workspace-new");
        var oldVolume = MakeDirectory("volume-old");
        var newVolume = MakeDirectory("volume-new");
        await File.WriteAllTextAsync(Path.Combine(oldWorkspace, "marker.txt"), "old workspace");
        await File.WriteAllTextAsync(Path.Combine(newWorkspace, "marker.txt"), "new workspace");
        await File.WriteAllTextAsync(Path.Combine(newVolume, "note.txt"), "new volume");
        var configPath = Path.Combine(_home, "config.json");
        SaveConfig(oldVolume);
        var awaitingApproval = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approved = NewSignal();
        var runtime = CreateRuntime(async (input, context) =>
        {
            Assert.Contains("/volumes/data", context.EnvironmentDescription);
            Assert.DoesNotContain(_root.FullName, context.EnvironmentDescription);
            if (input == "first")
            {
                var path = PathArgs("/volumes/data/./note.txt");
                path["content"] = "first";
                var declared = Assert.Single(context.ResolveToolResourceAccesses("file.write", path));
                Assert.Equal("vafs:/volumes/data/note.txt", declared.Resource.Value);
                var withSpace = Assert.Single(context.ResolveToolResourceAccesses(
                    "file.write", PathArgs("/volumes/data/space /.")));
                Assert.Equal("vafs:/volumes/data/space ", withSpace.Resource.Value);
                await context.InvokeToolAsync("file.write", path);
                await context.InvokeToolAsync("file.patch", new Dictionary<string, object?>
                {
                    ["path"] = "/volumes/data/note.txt", ["oldText"] = "first", ["newText"] = "patched",
                });
                Assert.Contains("/volumes/data/note.txt",
                    await context.InvokeToolAsync("file.list", PathArgs("/volumes/data")));
                Assert.Contains("/volumes/data/", await context.InvokeToolAsync("file.list", PathArgs("/volumes")));
            }

            var workspace = await context.InvokeToolAsync("file.read", PathArgs("marker.txt"));
            var volume = await context.InvokeToolAsync("file.read", PathArgs("/volumes/data/note.txt"));
            return new AgentResult($"{workspace}, {volume}", []);
        }, () => ConfigLoader.Load(configPath));
        var first = runtime.RunInContextAsync("first", new AuximRuntimeOptions
        {
            ApprovalHandler = new Approval(async (request, token) =>
            {
                awaitingApproval.TrySetResult(request);
                await approved.Task.WaitAsync(WaitLimit, token);
                return ApprovalResponse.Allow();
            }),
        });
        try
        {
            var request = await awaitingApproval.Task.WaitAsync(WaitLimit);
            Assert.Equal("vafs:/volumes/data/note.txt", Assert.Single(request.ResourceAccesses).Resource.Value);
            SaveConfig(newVolume);
            SetEnvironment("AUXIM_WORKSPACE", newWorkspace);
            var second = await runtime.RunInContextAsync("second").WaitAsync(WaitLimit);
            Assert.Equal("new workspace, new volume", second.FinalResponse);
        }
        finally
        {
            approved.TrySetResult();
        }

        Assert.Equal("old workspace, patched", (await first.WaitAsync(WaitLimit)).FinalResponse);
        Assert.Equal("patched", await File.ReadAllTextAsync(Path.Combine(oldVolume, "note.txt")));
        Assert.Equal("new volume", await File.ReadAllTextAsync(Path.Combine(newVolume, "note.txt")));

        void SaveConfig(string volume) => ConfigLoader.Save(new AuximConfig
        {
            Sandbox = new SandboxConfig
            {
                Workspace = oldWorkspace,
                Mounts = [new SandboxMountConfig { Name = "data", HostPath = volume }],
            },
        }, configPath);
    }

    [Fact]
    public async Task PrivateTmpIsIsolatedAndCleanedAfterSuccessFailureAndCancellationDrain()
    {
        var registry = BuiltInTools.CreateDefaultRegistry();
        var entered = new Dictionary<string, TaskCompletionSource<string>>
        {
            ["success"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["cancel"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["failure"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var finish = NewSignal();
        var holdStarted = NewSignal();
        var holdCanceled = NewSignal();
        var releaseHold = NewSignal();
        var handlerFailure = new InvalidOperationException("runner failed");
        Task<string>? outstandingHold = null;
        var probe = new ToolDefinition("test.tmp", "test", "", (_, _) => throw new InvalidOperationException());
        registry.Register(probe with
        {
            ResourceViewBinder = view => probe with
            {
                Handler = (_, _) => Task.FromResult(view.ResolveToHostPath("/tmp")),
            },
        });
        var hold = new ToolDefinition("test.hold", "test", "", async (_, token) =>
        {
            using var registration = token.Register(() => holdCanceled.TrySetResult());
            holdStarted.SetResult();
            await releaseHold.Task.WaitAsync(WaitLimit);
            return "finished";
        });
        registry.Register(hold with { ResourceViewBinder = _ => hold });
        var runtime = CreateRuntime(async (input, context) =>
        {
            var tmp = await context.InvokeToolAsync("test.tmp", PathArgs("/tmp"));
            var args = PathArgs("/tmp/shared.txt");
            args["content"] = input;
            await context.InvokeToolAsync("file.write", args);
            Assert.Equal(input, await context.InvokeToolAsync("file.read", args));
            if (input == "failure")
            {
                entered[input].SetResult(tmp);
                throw handlerFailure;
            }

            if (input == "cancel")
            {
                outstandingHold = context.InvokeToolAsync("test.hold", PathArgs("/tmp"));
                await holdStarted.Task.WaitAsync(WaitLimit);
            }

            entered[input].SetResult(tmp);
            await finish.Task.WaitAsync(WaitLimit, context.CancellationToken);
            return new AgentResult(input, []);
        }, registryFactory: () => registry);
        var options = new AuximRuntimeOptions { ApprovalHandler = AllowOnce() };
        using var cancellation = new CancellationTokenSource();
        var successfulRun = runtime.RunInContextAsync("success", options);
        var canceledRun = runtime.RunInContextAsync("cancel", options, cancellation.Token);
        var tmpPaths = await Task.WhenAll(entered["success"].Task, entered["cancel"].Task).WaitAsync(WaitLimit);
        try
        {
            Assert.NotEqual(tmpPaths[0], tmpPaths[1]);
            Assert.DoesNotContain(Environment.GetEnvironmentVariable("AUXIM_TMP")!, tmpPaths);
            Assert.Equal("success", await File.ReadAllTextAsync(Path.Combine(tmpPaths[0], "shared.txt")));
            Assert.Equal("cancel", await File.ReadAllTextAsync(Path.Combine(tmpPaths[1], "shared.txt")));

            cancellation.Cancel();
            await holdCanceled.Task.WaitAsync(WaitLimit);
            Assert.True(Directory.Exists(tmpPaths[1]));
            Assert.False(canceledRun.IsCompleted);
        }
        finally
        {
            releaseHold.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRun.WaitAsync(WaitLimit));
        Assert.NotNull(outstandingHold);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outstandingHold.WaitAsync(WaitLimit));
        Assert.False(Directory.Exists(tmpPaths[1]));
        Assert.True(Directory.Exists(tmpPaths[0]));
        finish.SetResult();
        await successfulRun.WaitAsync(WaitLimit);
        Assert.False(Directory.Exists(tmpPaths[0]));

        var failedRun = runtime.RunInContextAsync("failure", options);
        var failedTmp = await entered["failure"].Task.WaitAsync(WaitLimit);
        Assert.Same(handlerFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => failedRun.WaitAsync(WaitLimit)));
        Assert.False(Directory.Exists(failedTmp));
        Assert.Empty(Directory.GetFileSystemEntries(Environment.GetEnvironmentVariable("AUXIM_TMP")!));
    }

    [Fact]
    public async Task FileToolsRejectPathsOutsideMountsAndWritesToReadOnlyMounts()
    {
        var root = MakeDirectory("workspace");
        var readOnly = MakeDirectory("volume");
        await File.WriteAllTextAsync(Path.Combine(readOnly, "existing.txt"), "read-only");
        var runtime = CreateRuntime(async (_, context) =>
        {
            Assert.Equal("read-only", await context.InvokeToolAsync("file.read", PathArgs("/volumes/ro/existing.txt")));
            Assert.Throws<VirtualPathException>(() =>
                context.ResolveToolResourceAccesses("file.write", PathArgs("/volumes/ro/new.txt")));
            await Assert.ThrowsAsync<VirtualPathException>(() =>
                context.InvokeToolAsync("file.patch", PathArgs("/volumes/ro/existing.txt")));
            await Assert.ThrowsAsync<VirtualPathException>(() =>
                context.InvokeToolAsync("file.read", PathArgs("/outside.txt")));
            await Assert.ThrowsAsync<VirtualPathException>(() =>
                context.InvokeToolAsync("file.write", PathArgs("../../../outside.txt")));
            return new AgentResult("checked", []);
        }, () => new AuximConfig
        {
            Sandbox = new SandboxConfig
            {
                Workspace = root,
                Mounts = [new SandboxMountConfig { Name = "ro", HostPath = readOnly, ReadOnly = true }],
            },
        });

        await runtime.RunInContextAsync("check", new AuximRuntimeOptions
        {
            ApprovalHandler = new Approval((_, _) => throw new InvalidOperationException("Invalid target reached approval.")),
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FileWriteRevalidatesSymlinksAfterApproval(bool missingTarget, bool throughLinkedParent)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var workspace = MakeDirectory("workspace");
        var outside = Path.Combine(MakeDirectory("outside"), "data.txt");
        if (throughLinkedParent)
        {
            Directory.CreateSymbolicLink(Path.Combine(workspace, "parent-link"), Path.GetDirectoryName(outside)!);
        }
        if (!missingTarget)
        {
            await File.WriteAllTextAsync(outside, "outside");
            File.CreateSymbolicLink(Path.Combine(workspace, "existing-link.txt"), outside);
        }
        var runtime = CreateRuntime(async (_, context) =>
        {
            if (!missingTarget)
            {
                await Assert.ThrowsAsync<VirtualPathException>(() =>
                    context.InvokeToolAsync("file.read", PathArgs("existing-link.txt")));
            }

            await Assert.ThrowsAsync<VirtualPathException>(() =>
                context.InvokeToolAsync("file.write", PathArgs("changed-during-approval.txt")));
            return new AgentResult("checked", []);
        }, () => new AuximConfig { Sandbox = new SandboxConfig { Workspace = workspace } });

        await runtime.RunInContextAsync("check", new AuximRuntimeOptions
        {
            ApprovalHandler = new Approval((_, _) =>
            {
                File.CreateSymbolicLink(Path.Combine(workspace, "changed-during-approval.txt"),
                    throughLinkedParent ? "parent-link/data.txt" : outside);
                return Task.FromResult(ApprovalResponse.Allow());
            }),
        });
        if (missingTarget)
        {
            Assert.False(File.Exists(outside));
        }
        else
        {
            Assert.Equal("outside", await File.ReadAllTextAsync(outside));
        }
    }

    [Fact]
    public void RejectsLinksToSiblingMountThroughMissingTargetsAndLinkedParents()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var workspace = MakeDirectory("workspace");
        var sibling = MakeDirectory("workspace-sibling");
        var missing = Path.Combine(sibling, "missing.txt");
        File.CreateSymbolicLink(Path.Combine(workspace, "direct"), missing);
        File.CreateSymbolicLink(Path.Combine(workspace, "chain"), "direct");
        Directory.CreateSymbolicLink(Path.Combine(workspace, "directory"), sibling);
        File.CreateSymbolicLink(Path.Combine(workspace, "through-parent"), "directory/missing.txt");
        File.CreateSymbolicLink(Path.Combine(workspace, "inside"), "local-missing.txt");
        var vafs = new VirtualAgentFileSystem(new VirtualAgentFileSystemOptions
        {
            WorkspaceHostPath = workspace, TmpHostPath = MakeDirectory("tmp"),
            Mounts = [new VirtualMount("sibling", "/volumes/sibling", sibling, ReadOnly: false)],
        });

        Assert.Equal(missing, vafs.ResolveToHostPath("/volumes/sibling/missing.txt", requireWritable: true));
        Assert.Equal(Path.Combine(workspace, "local-missing.txt"),
            vafs.ResolveToHostPath("/workspace/local-missing.txt", requireWritable: true));
        Assert.Equal(Path.Combine(workspace, "inside"),
            vafs.ResolveToHostPath("/workspace/inside", requireWritable: true));
        Assert.Throws<VirtualPathException>(() => vafs.ResolveToHostPath("/workspace/direct", requireWritable: true));
        Assert.Throws<VirtualPathException>(() => vafs.ResolveToHostPath("/workspace/chain", requireWritable: true));
        Assert.Throws<VirtualPathException>(() => vafs.ResolveToHostPath("/workspace/directory/missing.txt", requireWritable: true));
        Assert.Throws<VirtualPathException>(() => vafs.ResolveVirtualPath("/workspace/through-parent", requireWritable: true));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task CleanupPreservesUnownedReplacementAndReportsFailure(string runnerOutcome)
    {
        var saved = MakeDirectory("saved");
        var moved = Path.Combine(saved, "original-tmp");
        var originalFailure = new InvalidOperationException("runner failed");
        var events = new ConcurrentQueue<RuntimeEvent>();
        using var cancellation = new CancellationTokenSource();
        string? tmp = null;
        var runtime = CreateRuntime(async (_, context) =>
        {
            tmp = await context.InvokeToolAsync("test.tmp", PathArgs("/tmp"));
            Directory.Move(tmp, moved);
            Directory.CreateDirectory(tmp);
            await File.WriteAllTextAsync(Path.Combine(tmp, "foreign.txt"), "foreign");
            if (runnerOutcome == "cancellation")
            {
                cancellation.Cancel();
            }

            return runnerOutcome == "failure" ? throw originalFailure : new AgentResult("done", []);
        }, registryFactory: CreateRegistryWithTmpProbe);

        try
        {
            var run = runtime.RunInContextAsync("run", CaptureEvents(events), cancellation.Token);
            if (runnerOutcome == "failure")
            {
                Assert.Same(originalFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => run));
            }
            else if (runnerOutcome == "cancellation")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => run);
            }

            Assert.NotNull(tmp);
            Assert.Equal("foreign", await File.ReadAllTextAsync(Path.Combine(tmp, "foreign.txt")));
            Assert.True(Directory.Exists(moved));
            var failed = Assert.Single(events.OfType<RuntimeRunFailedEvent>());
            Assert.Empty(events.OfType<RuntimeRunCompletedEvent>());
            Assert.Contains($"execution {failed.ExecutionId}", ReadLog());
            Assert.Contains("Execution temporary directory cleanup failed", ReadLog());
        }
        finally
        {
            if (tmp is not null && Directory.Exists(tmp))
            {
                Directory.Delete(tmp, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupDoesNotFollowDirectoryLinks(bool replaceTmpRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outside = MakeDirectory("outside");
        var foreign = Path.Combine(outside, "foreign.txt");
        await File.WriteAllTextAsync(foreign, "foreign");
        string? tmp = null;
        var runtime = CreateRuntime(async (_, context) =>
        {
            tmp = await context.InvokeToolAsync("test.tmp", PathArgs("/tmp"));
            if (replaceTmpRoot)
            {
                Directory.Move(tmp, Path.Combine(_root.FullName, "original-tmp"));
                Directory.CreateSymbolicLink(tmp, outside);
            }
            else
            {
                Directory.CreateSymbolicLink(Path.Combine(tmp, "linked-directory"), outside);
            }

            return new AgentResult("done", []);
        }, registryFactory: CreateRegistryWithTmpProbe);

        try
        {
            if (replaceTmpRoot)
            {
                await Assert.ThrowsAsync<IOException>(() => runtime.RunInContextAsync("run"));
                Assert.NotNull(tmp);
                Assert.Equal(outside, new DirectoryInfo(tmp).LinkTarget);
            }
            else
            {
                Assert.Equal("done", (await runtime.RunInContextAsync("run")).FinalResponse);
                Assert.False(Directory.Exists(tmp));
            }

            Assert.Equal("foreign", await File.ReadAllTextAsync(foreign));
        }
        finally
        {
            if (replaceTmpRoot && tmp is not null && new DirectoryInfo(tmp).LinkTarget is not null)
            {
                Directory.Delete(tmp);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingCloseCallbackDoesNotChangeTheRunOutcome(bool leaveToolOutstanding)
    {
        var stopping = NewSignal();
        var release = NewSignal();
        var events = new ConcurrentQueue<RuntimeEvent>();
        var registry = CreateRegistryWithTmpProbe();
        var tool = new ToolDefinition("test.hold", "test", "", async (_, _) =>
        {
            await release.Task.WaitAsync(WaitLimit);
            return "done";
        });
        registry.Register(tool with { ResourceViewBinder = _ => tool });
        string? tmp = null;
        CancellationToken contextToken = default;
        Task<string>? inFlight = null;
        var runtime = CreateRuntime(async (input, context) =>
        {
            tmp = await context.InvokeToolAsync("test.tmp", PathArgs("/tmp"));
            if (input == "next")
            {
                return new AgentResult(input, []);
            }

            contextToken = context.CancellationToken;
            context.CancellationToken.Register(() =>
            {
                stopping.SetResult();
                throw new InvalidOperationException("close callback failed");
            });
            if (leaveToolOutstanding)
            {
                inFlight = context.InvokeToolAsync("test.hold", PathArgs("/tmp"));
            }

            return new AgentResult(input, []);
        }, registryFactory: () => registry);

        bool? tmpExistsAtTerminalEvent = null;
        var run = runtime.RunInContextAsync("done", new AuximRuntimeOptions
        {
            EventSink = new DelegateRuntimeEventSink((value, _) =>
            {
                events.Enqueue(value);
                if (value is RuntimeRunCompletedEvent or RuntimeRunFailedEvent)
                {
                    tmpExistsAtTerminalEvent = Directory.Exists(tmp);
                }

                return ValueTask.CompletedTask;
            }),
        });
        try
        {
            await stopping.Task.WaitAsync(WaitLimit);
            if (leaveToolOutstanding)
            {
                Assert.True(Directory.Exists(tmp));
                Assert.False(run.IsCompleted);
                Assert.DoesNotContain(events, value => value is RuntimeRunCompletedEvent or RuntimeRunFailedEvent);
            }
        }
        finally
        {
            release.TrySetResult();
        }

        if (leaveToolOutstanding)
        {
            Assert.NotNull(inFlight);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight.WaitAsync(WaitLimit));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(WaitLimit));
            Assert.Contains("1 outstanding context operation", failure.Message);
            Assert.Equal(failure.Message, Assert.Single(events.OfType<RuntimeRunFailedEvent>()).Error);
            Assert.Empty(events.OfType<RuntimeRunCompletedEvent>());
        }
        else
        {
            Assert.Equal("done", (await run.WaitAsync(WaitLimit)).FinalResponse);
            Assert.Single(events.OfType<RuntimeRunCompletedEvent>());
            Assert.Empty(events.OfType<RuntimeRunFailedEvent>());
        }

        Assert.Equal(false, tmpExistsAtTerminalEvent);
        Assert.False(Directory.Exists(tmp));
        Assert.Throws<ObjectDisposedException>(() => contextToken.WaitHandle);
        Assert.Contains("close callback failed", ReadLog());
        Assert.Equal("next", (await runtime.RunInContextAsync("next")).FinalResponse);
        Assert.False(Directory.Exists(tmp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnvironmentMountOverridesHostConfigurationInBothEntries(bool environmentReadOnly)
    {
        var configured = MakeDirectory("configured");
        var overridden = MakeDirectory("environment");
        await File.WriteAllTextAsync(Path.Combine(overridden, "marker.txt"), "environment");
        ConfigLoader.Save(new AuximConfig
        {
            Sandbox = new SandboxConfig
            {
                Mounts = [new SandboxMountConfig { Name = "data", HostPath = configured, ReadOnly = !environmentReadOnly }],
            },
        });
        SetEnvironment("AUXIM_VAFS_MOUNTS", $"data={overridden}{(environmentReadOnly ? ":ro" : "")}");
        var legacy = VirtualAgentFileSystem.FromEnvironment();
        Assert.Equal(overridden, legacy.ResolveToHostPath("/volumes/data"));
        Assert.Equal(environmentReadOnly, Assert.Single(legacy.ListMounts(), mount => mount.Name == "data").ReadOnly);

        var runtime = CreateRuntime(async (_, context) =>
        {
            Assert.Equal("environment", await context.InvokeToolAsync("file.read", PathArgs("/volumes/data/marker.txt")));
            var write = PathArgs("/volumes/data/new.txt");
            write["content"] = "approved";
            if (environmentReadOnly)
            {
                await Assert.ThrowsAsync<VirtualPathException>(() => context.InvokeToolAsync("file.write", write));
            }
            else
            {
                await Assert.ThrowsAsync<ToolApprovalDeniedException>(() => context.InvokeToolAsync("file.write", write));
            }

            return new AgentResult("checked", []);
        });
        await runtime.RunInContextAsync("check");
        Assert.False(File.Exists(Path.Combine(configured, "new.txt")));
        Assert.False(File.Exists(Path.Combine(overridden, "new.txt")));
    }

    private static ToolRegistry CreateRegistryWithTmpProbe()
    {
        var registry = new ToolRegistry();
        var probe = new ToolDefinition("test.tmp", "test", "", (_, _) => throw new InvalidOperationException());
        registry.Register(probe with
        {
            ResourceViewBinder = view => probe with
            {
                Handler = (_, _) => Task.FromResult(view.ResolveToHostPath("/tmp")),
            },
        });
        return registry;
    }

    private static AuximRuntimeOptions CaptureEvents(ConcurrentQueue<RuntimeEvent> events) => new()
    {
        EventSink = new DelegateRuntimeEventSink((value, _) =>
        {
            events.Enqueue(value);
            return ValueTask.CompletedTask;
        }),
    };

    private string ReadLog() => File.ReadAllText(Path.Combine(_home, "logs", "agent.log"));

    [Fact]
    public async Task ContextNeverConsumesOrPersistsRememberAndOnlyOffersBoundTools()
    {
        var workspace = MakeDirectory("workspace");
        SetEnvironment("AUXIM_WORKSPACE", workspace);
        var approvalsPath = Path.Combine(_home, "approvals.json");
        var existing = JsonSerializer.Serialize(new ApprovalStore
        {
            Grants = [new ApprovalGrant("old", ResourceAction.Write, ResourceUri.Vafs("/workspace/note.txt"), "file.write")],
            AlwaysAllowedTools = ["file.patch"],
        });
        await File.WriteAllTextAsync(approvalsPath, existing);
        var runtime = CreateRuntime(async (input, context) =>
        {
            Assert.Equal(["file.list", "file.patch", "file.read", "file.search", "file.write"],
                context.ListTools().Select(tool => tool.Name));
            foreach (var unsupported in new[] { "echo", "git.status", "shell.run", "todo.add", "web.fetch" })
            {
                Assert.Throws<InvalidOperationException>(() => context.ResolveToolResourceAccesses(unsupported, PathArgs("/")));
                await Assert.ThrowsAsync<InvalidOperationException>(() => context.InvokeToolAsync(unsupported, PathArgs("/")));
            }

            var file = PathArgs("note.txt");
            if (input == "deny")
            {
                await Assert.ThrowsAsync<ToolApprovalDeniedException>(() => context.InvokeToolAsync("file.write", file));
                await Assert.ThrowsAsync<ToolApprovalDeniedException>(() => context.InvokeToolAsync("file.patch", file));
            }
            else
            {
                file["content"] = "allowed once";
                await context.InvokeToolAsync("file.write", file);
                await context.InvokeToolAsync("file.write", file);
            }

            return new AgentResult(input, []);
        });

        await runtime.RunInContextAsync("deny");
        Assert.False(File.Exists(Path.Combine(workspace, "note.txt")));
        var requested = 0;
        var decisions = new ConcurrentQueue<RuntimeApprovalResolvedEvent>();
        await runtime.RunInContextAsync("allow", new AuximRuntimeOptions
        {
            ApprovalHandler = new Approval((_, _) =>
            {
                requested++;
                return Task.FromResult(ApprovalResponse.Allow(remember: true));
            }),
            EventSink = new DelegateRuntimeEventSink((value, _) =>
            {
                if (value is RuntimeApprovalResolvedEvent resolved)
                {
                    decisions.Enqueue(resolved);
                }

                return ValueTask.CompletedTask;
            }),
        });
        Assert.Equal(2, requested);
        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, decision => { Assert.True(decision.Approved); Assert.False(decision.Remembered); });
        Assert.Equal(existing, await File.ReadAllTextAsync(approvalsPath));

        await runtime.InvokeToolAsync("file.write", new Dictionary<string, object?>
        {
            ["path"] = "note.txt", ["content"] = "legacy",
        });
        await runtime.InvokeToolAsync("file.patch", new Dictionary<string, object?>
        {
            ["path"] = "note.txt", ["oldText"] = "legacy", ["newText"] = "still allowed",
        });
        Assert.Equal("still allowed", await File.ReadAllTextAsync(Path.Combine(workspace, "note.txt")));
        Assert.Equal("legacy echo", await runtime.InvokeToolAsync("echo",
            new Dictionary<string, object?> { ["text"] = "legacy echo" }));
    }

    [Fact]
    public async Task SetupFailureDeletesOwnedTmpAndStrictVafsRejectsUnusableTmp()
    {
        var file = Path.Combine(_root.FullName, "not-a-directory");
        await File.WriteAllTextAsync(file, "outside the context");
        Assert.ThrowsAny<IOException>(() => new VirtualAgentFileSystem(
            VirtualAgentFileSystemOptions.CaptureMountsFromEnvironment(_root.FullName, [], file)));

        string? capturedTmp = null;
        var failure = new InvalidOperationException("binding failed");
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition("test.fail", "test", "", (_, _) => throw failure)
        {
            ResourceViewBinder = view =>
            {
                capturedTmp = view.ResolveToHostPath("/tmp");
                Assert.True(Directory.Exists(capturedTmp));
                throw failure;
            },
        });
        var runtime = CreateRuntime((_, _) => throw new InvalidOperationException("Runner should not start."),
            registryFactory: () => registry);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunInContextAsync("fail")));
        Assert.NotNull(capturedTmp);
        Assert.False(Directory.Exists(capturedTmp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnershipMarkerCreationFailurePreservesUnverifiedDirectory(bool replaceDirectory)
    {
        var failure = new UnauthorizedAccessException("could not create ownership marker");
        var moved = Path.Combine(_root.FullName, "original-tmp");
        string? tmp = null;
        try
        {
            var thrown = Assert.Throws<UnauthorizedAccessException>(() => new ExecutionResourceView(
                new SandboxConfig(), _home, followDirectoryLinksInListings: true,
                createOwnershipMarker: markerPath =>
                {
                    tmp = Path.GetDirectoryName(markerPath)!;
                    Assert.True(Directory.Exists(tmp));
                    if (replaceDirectory)
                    {
                        Directory.Move(tmp, moved);
                        Directory.CreateDirectory(tmp);
                    }

                    throw failure;
                }));

            Assert.Same(failure, thrown);
            Assert.NotNull(tmp);
            Assert.True(Directory.Exists(tmp));
            Assert.Empty(Directory.EnumerateFileSystemEntries(tmp));
            Assert.Equal(replaceDirectory, Directory.Exists(moved));
            Assert.Contains($"Setup cleanup skipped for '{tmp}' because ownership could not be established.", ReadLog());
        }
        finally
        {
            if (tmp is not null && Directory.Exists(tmp))
            {
                Directory.Delete(tmp, recursive: true);
            }
        }
    }

    private AuximRuntimeService CreateRuntime(
        Func<string, AuximExecutionContext, Task<AgentResult>> run,
        Func<AuximConfig>? configLoader = null,
        Func<ToolRegistry>? registryFactory = null) =>
        new(new Runner(run), new RuntimeToolService(registryFactory ?? BuiltInTools.CreateDefaultRegistry),
            configLoader: configLoader, homeDirectory: () => _home,
            environmentDescription: () => throw new InvalidOperationException("Context must describe its bound VAFS."));

    private string MakeDirectory(string name) => _root.CreateSubdirectory(name).FullName;

    private void SetEnvironment(string key, string? value)
    {
        _environment.TryAdd(key, Environment.GetEnvironmentVariable(key));
        Environment.SetEnvironmentVariable(key, value);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Dictionary<string, object?> PathArgs(string path) => new() { ["path"] = path };

    private static Approval AllowOnce() => new((_, _) => Task.FromResult(ApprovalResponse.Allow()));

    public void Dispose()
    {
        foreach (var (key, value) in _environment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _root.Delete(recursive: true);
    }

    private sealed class Approval(Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>> review) : IApprovalHandler
    {
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken token) => review(request, token);
    }

    private sealed class Runner(Func<string, AuximExecutionContext, Task<AgentResult>> run) : IAgentRunner
    {
        public Task<AgentResult> RunAsync(AgentRunRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<AgentResult> RunInContextAsync(string input, AuximExecutionContext context) => run(input, context);
    }
}
