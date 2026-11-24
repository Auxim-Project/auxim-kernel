using Auxim.Core.Config;
using Auxim.Core.Logging;
using Auxim.VAFS;

namespace Auxim.Core.Runtime;

internal sealed class ExecutionResourceView : IDisposable
{
    private readonly DirectoryInfo _temporaryDirectory;
    private readonly string _ownershipMarker = $".auxim-owner-{Guid.NewGuid():N}";

    public ExecutionResourceView(
        SandboxConfig sandbox, string homeDirectory, bool followDirectoryLinksInListings,
        Func<string, Stream>? createOwnershipMarker = null)
        : this(tmpPath => VirtualAgentFileSystemOptions.CaptureMountsFromEnvironment(
                sandbox.Workspace, sandbox.Mounts.Select(mount => new VirtualMount(
                    mount.Name, $"/volumes/{mount.Name}", mount.HostPath, mount.ReadOnly)), tmpPath),
            homeDirectory, followDirectoryLinksInListings, createOwnershipMarker)
    {
    }

    internal ExecutionResourceView(ExecutionResourceDomain domain, string homeDirectory)
        : this(domain.CreateViewOptions, homeDirectory, followDirectoryLinksInListings: false, domain: domain)
    {
    }

    private ExecutionResourceView(
        Func<string, VirtualAgentFileSystemOptions> captureOptions,
        string homeDirectory,
        bool followDirectoryLinksInListings,
        Func<string, Stream>? createOwnershipMarker = null,
        ExecutionResourceDomain? domain = null)
    {
        _temporaryDirectory = Directory.CreateTempSubdirectory("auxim-execution-");
        var markerCreated = false;
        try
        {
            var markerPath = Path.Combine(_temporaryDirectory.FullName, _ownershipMarker);
            using (createOwnershipMarker is null
                ? new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write)
                : createOwnershipMarker(markerPath))
            {
                markerCreated = true;
            }

            FileSystem = new VirtualAgentFileSystem(captureOptions(_temporaryDirectory.FullName), followDirectoryLinksInListings);
            Domain = domain ?? new ExecutionResourceDomain(FileSystem);
        }
        catch
        {
            if (markerCreated)
            {
                DisposeAfterSetupFailure(homeDirectory);
            }
            else
            {
                AuximLog.Warning(
                    $"Setup cleanup skipped for '{_temporaryDirectory.FullName}' because ownership could not be established.",
                    homeDirectory);
            }

            throw;
        }
    }

    public VirtualAgentFileSystem FileSystem { get; }

    internal ExecutionResourceDomain Domain { get; }

    public void Dispose()
    {
        try
        {
            DeleteOwnedDirectory();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Execution temporary directory cleanup failed for '{_temporaryDirectory.FullName}': {exception.Message}",
                exception);
        }
    }

    private void DeleteOwnedDirectory()
    {
        var path = _temporaryDirectory.FullName;
        if (GetAttributes(path) is null)
        {
            return;
        }

        if (!IsOwnedDirectory(path))
        {
            throw new IOException("The temporary path no longer refers to the owned directory.");
        }

        var quarantinePath = Path.Combine(_temporaryDirectory.Parent!.FullName, $"auxim-cleanup-{Guid.NewGuid():N}");
        var quarantine = OperatingSystem.IsWindows()
            ? Directory.CreateDirectory(quarantinePath)
            : Directory.CreateDirectory(quarantinePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var candidate = Path.Combine(quarantine.FullName, "tmp");
        try
        {
            // Verify the directory moved into the private location before deleting anything recursively.
            Directory.Move(path, candidate);
            if (!IsOwnedDirectory(candidate))
            {
                if (GetAttributes(path) is null)
                {
                    Directory.Move(candidate, path);
                }

                throw new IOException($"Temporary directory ownership changed during cleanup at '{candidate}'.");
            }

            Directory.Delete(candidate, recursive: true);
        }
        finally
        {
            if (GetAttributes(candidate) is null)
            {
                quarantine.Delete();
            }
        }
    }

    private bool IsOwnedDirectory(string path) =>
        GetAttributes(path) is { } directory
        && (directory & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory
        && GetAttributes(Path.Combine(path, _ownershipMarker)) is { } marker
        && (marker & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;

    private static FileAttributes? GetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void DisposeAfterSetupFailure(string homeDirectory)
    {
        try
        {
            Dispose();
        }
        catch (Exception exception)
        {
            AuximLog.Warning($"Setup cleanup failed: {exception.Message}", homeDirectory);
        }
    }
}

internal sealed class ExecutionResourceDomain
{
    internal ExecutionResourceDomain(VirtualAgentFileSystem fileSystem)
    {
        Mounts = Array.AsReadOnly(fileSystem.ListMounts().Where(mount => mount.VirtualPath != "/tmp").ToArray());
    }

    internal IReadOnlyList<VirtualMount> Mounts { get; }

    internal VirtualAgentFileSystemOptions CreateViewOptions(string tmpPath) => new()
    {
        WorkspaceHostPath = Mounts.Single(mount => mount.VirtualPath == "/workspace").HostPath,
        Mounts = Mounts.Where(mount => mount.VirtualPath != "/workspace").ToArray(),
        TmpHostPath = tmpPath,
        AllowSharedTmpFallback = false,
    };
}
