# Auxim Kernel

Auxim is a modular runtime environment for AI agents. Its long-term direction is
to provide reusable foundations for agent applications. The current Early Alpha
includes the Kernel libraries and a default Agent with a terminal CLI.

Auxim Kernel provides runtime services and resource controls for Auxim agents.
It coordinates tool execution, resource approvals, session state, runtime events,
and virtual filesystem and shell access.

## Overview

Kernel defines the runtime boundary; [Auxim Agent](https://github.com/Auxim-Project/auxim-agent)
provides the default agent implementation, model clients, conversation loop, and
CLI. Kernel accepts an agent runner through its `IAgentRunner` contract and does
not depend on the Agent repository.

Use Kernel when building or integrating an agent runtime. To try Auxim from a
terminal, start with the Agent repository linked above.
This repository contains libraries, tests, and a packaging project; it has no CLI
entry point.

## Architecture

The three libraries are distributed together in the `Auxim.Kernel` NuGet package.

| Project | Responsibilities | Direct project dependencies |
| --- | --- | --- |
| [Auxim.Core](https://github.com/Auxim-Project/auxim-kernel/tree/main/src/Auxim.Core) | Runtime and agent runner contracts, application services, tool registry and execution coordination, resource approvals, configuration, sessions, and runtime events. | `Auxim.VAFS` |
| [Auxim.Tools](https://github.com/Auxim-Project/auxim-kernel/tree/main/src/Auxim.Tools) | Default tool registry and built-in tools for files, search, Git, web fetching, restricted shell commands, task lists, time, and echo. | `Auxim.Core`, `Auxim.VAFS` |
| [Auxim.VAFS](https://github.com/Auxim-Project/auxim-kernel/tree/main/src/Auxim.VAFS) | Virtual paths, workspace and volume mappings, read-only mounts, and restricted shell execution. | None |

VAFS exposes `/workspace`, `/tmp`, and `/volumes/<name>` paths. Runtime tool
coordination checks declared resource accesses through the approval service
before invoking tools. VAFS and its shell policy provide application-level
controls, not operating-system process isolation.

### Context-bound execution

`AuximRuntimeService.RunInContextAsync` is an opt-in entry point for trusted
in-process runners. Implement `IAgentRunner.RunInContextAsync` to receive the
Kernel-owned `AuximExecutionContext`. Each invocation has a distinct execution
ID and run ID. It operates without a session and leaves current-session
selection to the chat API.

Use the context to list and invoke tools and publish content deltas. It captures
configuration and tool metadata once and associates tools with the configured
approval and event channels. Scoped events carry an execution ID. The scope
closes when the runner exits, signals cancellation, and waits for admitted
operations to finish. Tool and event callbacks must cooperate with cancellation
for prompt shutdown.

Context tool arguments must be JSON-compatible. Structured values are passed
to resolvers and handlers as immutable JSON, so an approval applies to the
arguments captured for that invocation. Exceptions from nonterminal event
callbacks propagate through the operation. Terminal events are delivered on a
best-effort basis and delivery failures are logged.
If cancellation prevents startup event publication from completing, the Kernel
cleans up and returns cancellation without emitting a terminal run event.

Runners must finish their accepted context operations before returning. Once
the Kernel registers the returned runner task, admission and operation exit
check for task completion under the same lock used to stop admission and
record outstanding work. Operations may still be accepted before the runner
returns its task and the Kernel registers it. The recorded count reflects when
the Kernel first observes completion. Cancellation and cleanup may wait for
the runtime continuation to resume. If operations were outstanding when the
Kernel observed a normal runner return, it requests cancellation, drains the
work, disposes the context's resources, and reports a run failure. A tool
failure the runner handled can still result in a successful run.

The existing chat and direct tool APIs serve legacy callers. The context entry
point requires the Kernel's `RuntimeToolService` (or an empty tool catalog).
Legacy runner and tool service implementations require an explicit upgrade to
participate. Context binding provides managed execution lifetime and tool
routing for trusted code. Hosts must keep configuration and schema input stable
while the Kernel captures them.

The Kernel builds the context's VAFS from the sandbox configuration and
`AUXIM_WORKSPACE` and `AUXIM_VAFS_MOUNTS` values at context creation. As in
legacy VAFS, a host environment mount replaces the configured mount of the same
name, including its read-only setting. The effective mount still governs file
operations and approval requirements. The environment
description and bound tools use that view for the lifetime of the context. The
Kernel creates a unique `/tmp` under the system temporary directory, with no
shared fallback or `AUXIM_TMP` override. It cleans up on setup failure or after
cancellation and admitted operations have finished on close. If marker
creation fails, the Kernel logs the directory path and leaves it in place.
Cleanup verifies directory ownership before deleting recursively. A cleanup
failure is logged and fails an otherwise successful run. A runner failure,
lifecycle violation, or caller cancellation remains the primary outcome. Exceptions from
cancellation callbacks are logged while close continues to drain and clean up.
Workspace and volume directories remain host-owned.

In a context, only definitions that supply a `ToolDefinition.ResourceViewBinder`
are listed and callable. The built-in file read, list, search, write, and patch
tools bind their declarations and I/O to the same view. Writes require a fresh
approval for each call. The context path neither reads nor writes the remembered
approval store. Legacy chat and direct tools continue to use their existing
mount and approval configuration.

Trusted hosts can set `AuximRuntimeOptions.AllowedToolNames` when starting a
context-bound run. The Kernel snapshots the names before binding tools and
uses the resulting catalog for listing, resource declarations, and execution.
Names match registered tool names without regard to case. A null list leaves
the existing context catalog available, and an empty list allows no tools.
Null, empty, and whitespace-only entries are rejected. Valid names absent
from the context catalog grant no access. Legacy chat, direct tool calls, and
external messages reject a non-null list before starting work.

Trusted hosts can also set `AuximRuntimeOptions.ResourcePermissions` to a list
of `VafsResourcePermission` grants. The Kernel snapshots the list for each
context, and checks every declared access during preview, before approval, and
again before execution. Null leaves resource permissions unrestricted by this
option. An empty list grants no resource access. Legacy entry points reject a
non-null list before starting work.

Grants use canonical, case-sensitive virtual paths and separate read from write.
An exact grant covers only its path. Set `IncludeDescendants` to cover a
subtree. Synthetic `/` and `/volumes` allow exact metadata reads only. Listing
a mounted directory and searching require a subtree read grant. Search
declares a subtree even when the supplied target currently resolves to a file.
Patching requires both read and write access, and restricted writes require
an existing parent directory. Writes still go through the existing approval
flow. Missing or unsupported resource declarations are denied.

Restricted views reject overlapping mount roots and links below a mount when
resolving declared targets. Existing path components must use their exact
filesystem names. Virtual mount prefixes are case-sensitive. On Unix, verified
existing host roots can be distinct by case. Unresolved roots and Windows
roots use conservative case-insensitive overlap checks. Restricted directory
listings do not classify links by their targets. These checks apply to trusted
tool declarations and the bound view. They do not establish file identity
across hard links or OS mount aliases, nor do they eliminate races between
validation and I/O.

`AuximRuntimeService.RunChildInContextAsync` starts a child from an active
context owned by that runtime. The runner must explicitly opt in through
`IAgentRunner.SupportsConcurrentContextExecutions` and support a parent
awaiting a child on the same instance. The child inherits the parent's
approval handler and event sink and has its own execution ID and run ID.
Remembered approvals remain disabled.

Child tool and resource requests must fit within the parent's effective
permissions. Null lists inherit delegable permissions and empty lists deny
all in the corresponding category. An unrestricted parent's delegable
resources are its non-temporary mounts, subject to read-only flags and
restricted path checks, plus exact reads of synthetic metadata roots.
Custom binders are excluded from inherited child catalogs. Explicit
requests for unavailable or non-rebindable tools fail. The child uses the
parent's captured workspace and volume mappings and owns a fresh /tmp.
The child receives no temporary resource grants.

The parent tracks the child through its terminal event and cleanup.
Parent cancellation propagates to admitted children. Child cancellation
affects only its own execution and descendants. A parent that returns
with live children follows the same cancellation and drain rules as
other outstanding context operations.

Child events include `ParentExecutionId`. Each execution still has its own
`ExecutionId` and `RunId`. Cancellation uses `run.failed` with
`IsCanceled` set when the run ends in response to its cancellation signal.
Group concurrent events by execution and tool call IDs. A context tool
invocation rejected by the catalog or resource policy reports
`tool.completed` with outcome `denied`. A preflight rejection can arrive
without `tool.started` or an approval event. Resource previews return
errors to their caller. A child request rejected by the permission checks
before a child context is created throws and is logged against the parent.

File search validates explicit paths through VAFS and skips symbolic links and
other reparse points encountered during recursive traversal. It also skips
`.git` entries. With ripgrep installed, the tool passes bounded batches of
validated file paths and disables ripgrep configuration and ignore files.
Without ripgrep, the managed fallback performs case-insensitive literal
matching. If ripgrep is found but fails to start, search reports the failure.
The command-line budget includes the executable, flags, pattern, and file
paths, and the result limit applies across the entire invocation.

Errors opening the requested path fail the search. Unreadable descendants
that can be attributed to an individual path are skipped and reported alongside
any matches. Search diagnostics use virtual paths and omit raw subprocess
diagnostics. Cancellation requests termination of the ripgrep process tree.
The wait for termination and the drain after process exit have time limits.
Unconfirmed cleanup is reported without changing the original search outcome.

VAFS fixes the mount table. Host files and links can still change in place, and
configured mounts can overlap across contexts. This is an application-level
resource view for trusted in-process tools. Ripgrep runs with host process
privileges, and filesystem changes between path validation and file open can
still affect what either search engine reads.

## Getting Started

### Use the published package

The current source version is `0.1.0-alpha.1`. That version is
[available on NuGet.org](https://www.nuget.org/packages/Auxim.Kernel/0.1.0-alpha.1)
and targets .NET 10. From an existing .NET 10 project directory, add it with:

```bash
dotnet add package Auxim.Kernel --version 0.1.0-alpha.1 --source https://api.nuget.org/v3/index.json
```

This adds references to the `Auxim.Core`, `Auxim.Tools`, and `Auxim.VAFS`
assemblies. You do not need a Kernel source checkout or separate packages for
the three libraries.

### Build from source

Install the .NET 10 SDK and Git. Dependency restore requires access to NuGet.org;
building and running the current tests do not require a model service or API key.

```bash
git clone https://github.com/Auxim-Project/auxim-kernel.git
cd auxim-kernel
dotnet restore Auxim.Kernel.sln --source https://api.nuget.org/v3/index.json
dotnet build Auxim.Kernel.sln --no-restore -c Release
dotnet test Auxim.Kernel.sln --no-build --no-restore -c Release
```

Tests are in [tests/Auxim.Core.Tests](https://github.com/Auxim-Project/auxim-kernel/tree/main/tests/Auxim.Core.Tests) and cover runtime
services, tool execution and approvals, the tool registry, VAFS, and shell behavior.

## NuGet Package

After the Release build above, create a local package:

```bash
dotnet pack src/Auxim.Kernel.Package/Auxim.Kernel.Package.csproj -c Release --no-build --no-restore -o artifacts/packages
```

[Auxim.Kernel.Package](https://github.com/Auxim-Project/auxim-kernel/blob/main/src/Auxim.Kernel.Package/Auxim.Kernel.Package.csproj)
bundles the three library assemblies under `lib/net10.0/`, with the README and
license metadata. The libraries are not packed separately. The default output is
`artifacts/packages/Auxim.Kernel.0.1.0-alpha.1.nupkg`; creating this file does not
publish it or update the package on NuGet.org.

The [release workflow](https://github.com/Auxim-Project/auxim-kernel/blob/main/.github/workflows/release.yml) is manually triggered.
Publishing is opt-in and uses NuGet Trusted Publishing.

## Project Status

**Early Alpha.** Runtime contracts, package APIs, and configuration formats may
change. [CI](https://github.com/Auxim-Project/auxim-kernel/blob/main/.github/workflows/ci.yml) is configured for Linux, Windows, and macOS;
external command availability and filesystem behavior depend on the environment.

## License

[Apache License 2.0](https://github.com/Auxim-Project/auxim-kernel/blob/main/LICENSE).
