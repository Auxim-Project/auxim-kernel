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
