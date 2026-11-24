using System.Text;
using System.Text.RegularExpressions;

namespace Auxim.VAFS;

public sealed class VirtualAgentFileSystem
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly IReadOnlyList<VirtualMount> _mounts;

    public VirtualAgentFileSystem(VirtualAgentFileSystemOptions options)
    {
        var tmpMount = NormalizeMount(new VirtualMount(
            "tmp",
            "/tmp",
            options.TmpHostPath,
            ReadOnly: false));
        tmpMount = EnsureWritableTmpMount(tmpMount, options.AllowSharedTmpFallback);

        var mounts = new List<VirtualMount>
        {
            NormalizeMount(new VirtualMount(
                "workspace",
                "/workspace",
                options.WorkspaceHostPath,
                ReadOnly: false)),
            tmpMount,
        };

        foreach (var mount in options.Mounts)
        {
            mounts.Add(NormalizeMount(mount));
        }

        _mounts = Array.AsReadOnly(mounts
            .OrderByDescending(mount => mount.VirtualPath.Length)
            .ToArray());
    }

    public static VirtualAgentFileSystem FromEnvironment() =>
        new(VirtualAgentFileSystemOptions.FromEnvironment());

    public IReadOnlyList<VirtualMount> ListMounts() => _mounts;

    public string ResolveToHostPath(string path, bool requireWritable = false) =>
        ResolvePath(path, requireWritable, out _);

    /// <summary>Returns the canonical virtual path after applying the same mount checks as host-path resolution.</summary>
    public string ResolveVirtualPath(string path, bool requireWritable = false)
    {
        ResolvePath(path, requireWritable, out var virtualPath);
        return virtualPath;
    }

    private string ResolvePath(string path, bool requireWritable, out string virtualPath)
    {
        virtualPath = NormalizeVirtualPath(path);
        var mount = FindMountForVirtualPath(virtualPath)
            ?? throw new VirtualPathException(
                $"Path '{path}' is outside Auxim VAFS. Use /workspace, /tmp, or mounted /volumes paths.");

        if (requireWritable && mount.ReadOnly)
        {
            throw new VirtualPathException($"Virtual mount '{mount.VirtualPath}' is read-only.");
        }

        var relative = RelativeVirtualPath(mount.VirtualPath, virtualPath);
        var hostPath = Path.GetFullPath(Path.Combine(mount.HostPath, relative));
        if (!IsSameOrChildPath(mount.HostPath, hostPath))
        {
            throw new VirtualPathException($"Path '{path}' escapes virtual mount '{mount.VirtualPath}'.");
        }

        EnsurePhysicalPathInsideMount(mount, hostPath, path);

        return hostPath;
    }

    public string ToVirtualPath(string hostPath)
    {
        var fullPath = Path.GetFullPath(hostPath);
        foreach (var mount in _mounts.OrderByDescending(mount => mount.HostPath.Length))
        {
            if (!IsSameOrChildPath(mount.HostPath, fullPath))
            {
                continue;
            }

            var relative = Path.GetRelativePath(mount.HostPath, fullPath);
            if (relative == ".")
            {
                return mount.VirtualPath;
            }

            return mount.VirtualPath.TrimEnd('/') + "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
        }

        throw new VirtualPathException("Host path is outside Auxim VAFS.");
    }

    public string RewriteHostPathsToVirtual(string text)
    {
        var rewritten = text;
        foreach (var mount in _mounts.OrderByDescending(mount => mount.HostPath.Length))
        {
            var pattern = Regex.Escape(mount.HostPath)
                + @"(?=$|[\/\\\s:\)\]\}',""])";
            rewritten = Regex.Replace(
                rewritten,
                pattern,
                mount.VirtualPath,
                PathComparison == StringComparison.OrdinalIgnoreCase
                    ? RegexOptions.IgnoreCase
                    : RegexOptions.None);
        }

        return rewritten;
    }

    public string DescribeForAgent()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Available Virtual Agent File System mounts:");
        foreach (var mount in _mounts.OrderBy(mount => mount.VirtualPath, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {mount.VirtualPath}{(mount.ReadOnly ? " (read-only)" : "")}");
        }

        return builder.ToString().TrimEnd();
    }

    private static VirtualMount NormalizeMount(VirtualMount mount)
    {
        if (string.IsNullOrWhiteSpace(mount.Name))
        {
            throw new VirtualPathException("Virtual mount name is required.");
        }

        if (mount.Name.Contains('/') || mount.Name.Contains('\\') || mount.Name is "." or "..")
        {
            throw new VirtualPathException($"Invalid virtual mount name '{mount.Name}'.");
        }

        var virtualPath = NormalizeVirtualPath(mount.VirtualPath);
        if (virtualPath != "/workspace"
            && virtualPath != "/tmp"
            && !virtualPath.StartsWith("/volumes/", StringComparison.Ordinal))
        {
            throw new VirtualPathException("Virtual mounts must live at /workspace, /tmp, or /volumes/<name>.");
        }

        return mount with
        {
            VirtualPath = virtualPath,
            HostPath = Path.GetFullPath(mount.HostPath),
        };
    }

    private static VirtualMount EnsureWritableTmpMount(VirtualMount mount, bool allowSharedTmpFallback)
    {
        try
        {
            Directory.CreateDirectory(mount.HostPath);
            return mount;
        }
        catch (Exception exception) when (allowSharedTmpFallback
            && (exception is IOException or UnauthorizedAccessException))
        {
            var fallback = Path.Combine(Path.GetTempPath(), "auxim", "vafs-tmp");
            Directory.CreateDirectory(fallback);
            return mount with { HostPath = Path.GetFullPath(fallback) };
        }
    }

    private static string NormalizeVirtualPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            return "/workspace";
        }

        path = path.Replace('\\', '/').Trim();
        if (path.StartsWith('~') || HasWindowsDrivePrefix(path))
        {
            throw new VirtualPathException("Use VAFS paths such as /workspace, /tmp, or /volumes/<name>.");
        }

        var basePath = path.StartsWith('/') ? "" : "/workspace";
        var parts = (basePath + "/" + path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case ".":
                    continue;
                case "..":
                    if (stack.Count == 0)
                    {
                        throw new VirtualPathException("Path escapes Auxim VAFS.");
                    }

                    stack.RemoveAt(stack.Count - 1);
                    break;
                default:
                    stack.Add(part);
                    break;
            }
        }

        return "/" + string.Join('/', stack);
    }

    private VirtualMount? FindMountForVirtualPath(string virtualPath)
    {
        return _mounts.FirstOrDefault(mount =>
            string.Equals(virtualPath, mount.VirtualPath, StringComparison.Ordinal)
            || virtualPath.StartsWith(mount.VirtualPath + "/", StringComparison.Ordinal));
    }

    private static string RelativeVirtualPath(string mountPath, string virtualPath)
    {
        if (string.Equals(mountPath, virtualPath, StringComparison.Ordinal))
        {
            return ".";
        }

        return virtualPath[(mountPath.Length + 1)..].Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool IsSameOrChildPath(string parent, string path)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(parent, path, PathComparison)
            || path.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison)
            || path.StartsWith(parent + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private static void EnsurePhysicalPathInsideMount(
        VirtualMount mount,
        string hostPath,
        string virtualPath)
    {
        var remainingLinks = 40;
        var physicalRoot = ResolvePhysicalPath("", mount.HostPath, ref remainingLinks);
        var relative = Path.GetRelativePath(mount.HostPath, hostPath);
        var current = physicalRoot;
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = ResolvePhysicalPath(current, segment, ref remainingLinks);
            if (!IsSameOrChildPath(physicalRoot, current))
            {
                throw new VirtualPathException(
                    $"Path '{virtualPath}' escapes virtual mount '{mount.VirtualPath}' through a symbolic link.");
            }
        }
    }

    private static string ResolvePhysicalPath(string parent, string path, ref int remainingLinks)
    {
        if (Path.IsPathRooted(path))
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new VirtualPathException("Cannot resolve a partially qualified symbolic link target.");
            }

            parent = Path.GetPathRoot(path)!;
            path = path[parent.Length..];
        }

        foreach (var segment in path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                parent = Path.GetDirectoryName(parent) ?? parent;
                continue;
            }

            var current = Path.Combine(parent, segment);
            var target = ReadLinkTarget(current);
            if (target is not null && --remainingLinks < 0)
            {
                throw new VirtualPathException("Too many symbolic links while resolving a VAFS path.");
            }

            // A target can contain linked parent directories, even when its leaf is missing.
            parent = target is null ? current : ResolvePhysicalPath(parent, target, ref remainingLinks);
        }

        return parent;
    }

    private static string? ReadLinkTarget(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return info.LinkTarget;
    }

    private static bool HasWindowsDrivePrefix(string path) =>
        path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
}
