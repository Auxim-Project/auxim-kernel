# Auxim Kernel

Auxim Kernel is the trusted runtime and resource-control foundation for Auxim
agents. It owns the runtime boundary, tool execution contracts, approvals,
session state, runtime events, and virtualized filesystem and shell access.

## Assemblies

| Package | Purpose |
| --- | --- |
| `Auxim.Core` | Runtime contracts, application boundary, approvals, sessions, events, and tool coordination. |
| `Auxim.Tools` | Built-in tools and the default tool registry. |
| `Auxim.VAFS` | Virtual agent filesystem and restricted shell primitives. |

These three assemblies are distributed together in the `Auxim.Kernel` NuGet
package. Their project dependency graph is:

```text
Auxim.Tools -> Auxim.Core -> Auxim.VAFS
            -> Auxim.VAFS
```

## Build and Test

.NET 10 SDK is required.

```bash
dotnet restore Auxim.Kernel.sln
dotnet build Auxim.Kernel.sln --no-restore -c Release
dotnet test Auxim.Kernel.sln --no-build -c Release
```

## Package

The default development package version is `0.1.0-alpha.1`. The three internal
projects are not packed separately. The packaging project creates one
`Auxim.Kernel` package containing all three assemblies.

```bash
dotnet pack src/Auxim.Kernel.Package/Auxim.Kernel.Package.csproj -c Release -o artifacts/packages
dotnet pack src/Auxim.Kernel.Package/Auxim.Kernel.Package.csproj -c Release -o artifacts/packages -p:Version=0.1.0-alpha.2
```

The package is not published automatically. The release workflow requires an
explicit manual publish choice and a `NUGET_API_KEY` repository secret.

## License

Licensed under the Apache License 2.0. See [LICENSE](LICENSE).
