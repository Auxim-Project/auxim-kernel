using Auxim.Core.Config;
using Auxim.Core.Logging;
using Auxim.VAFS;

namespace Auxim.Core.Runtime;

internal sealed class ExecutionResourceView : IDisposable
{
    private readonly DirectoryInfo _temporaryDirectory;
    private readonly string _ownershipMarker = $".auxim-owner-{Guid.NewGuid():N}";

    public ExecutionResourceView(SandboxConfig sandbox, string homeDirectory)
    {
        _temporaryDirectory = Directory.CreateTempSubdirectory("auxim-execution-");
        try
        {
            using (new FileStream(Path.Combine(_temporaryDirectory.FullName, _ownershipMarker),
                FileMode.CreateNew, FileAccess.Write))
            {
            }

            var mounts = sandbox.Mounts.Select(mount => new VirtualMount(
                mount.Name, $"/volumes/{mount.Name}", mount.HostPath, mount.ReadOnly));
            FileSystem = new VirtualAgentFileSystem(VirtualAgentFileSystemOptions.CaptureMountsFromEnvironment(
                sandbox.Workspace, mounts, _temporaryDirectory.FullName));
        }
        catch
        {
            DisposeAfterSetupFailure(homeDirectory);
            throw;
        }
    }

    public VirtualAgentFileSystem FileSystem { get; }

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
