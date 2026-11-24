using Auxim.Core.Resources;
using Auxim.VAFS;

namespace Auxim.Core.Runtime;

internal sealed class VafsResourcePermissionPolicy
{
    private const string VafsPrefix = "vafs:";
    private readonly VirtualAgentFileSystem _view;
    private readonly IReadOnlyList<VafsResourcePermission> _permissions;
    private readonly (string VirtualPath, string PhysicalPath, bool ExistingDirectory)[] _mounts;

    public VafsResourcePermissionPolicy(VirtualAgentFileSystem view, IReadOnlyList<VafsResourcePermission> permissions)
    {
        _view = view;
        _permissions = permissions;
        _mounts = view.ListMounts().Select(mount =>
        {
            var exists = Directory.Exists(mount.HostPath);
            var physicalPath = view.ResolveUnaliasedHostPath(mount.VirtualPath);
            return (mount.VirtualPath, physicalPath, exists && Directory.Exists(physicalPath));
        }).ToArray();

        for (var index = 0; index < _mounts.Length; index++)
        {
            var mount = _mounts[index];
            for (var previous = 0; previous < index; previous++)
            {
                var other = _mounts[previous];
                // Existing roots have already been checked for exact names.
                // Unresolved paths and Windows root syntax retain conservative case matching.
                var hostComparison = !OperatingSystem.IsWindows() && mount.ExistingDirectory && other.ExistingDirectory
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                if (PathsOverlap(mount.VirtualPath, other.VirtualPath, StringComparison.Ordinal)
                    || PathsOverlap(ComparableHostPath(mount.PhysicalPath), ComparableHostPath(other.PhysicalPath), hostComparison))
                {
                    throw new ArgumentException(
                        $"ResourcePermissions cannot be used with overlapping mounts '{mount.VirtualPath}' and '{other.VirtualPath}'.",
                        nameof(permissions));
                }
            }
        }

        foreach (var permission in permissions)
        {
            if (permission is null || !IsSupported(permission.Action, permission.VirtualPath, permission.IncludeDescendants))
            {
                throw new ArgumentException(
                    "ResourcePermissions require read or write actions and canonical VAFS paths. Synthetic roots allow only exact reads.",
                    nameof(permissions));
            }

            if (!IsSyntheticRoot(permission.VirtualPath))
            {
                ResolvePinnedPath(permission.VirtualPath, requireWritable: false);
            }
        }
    }

    public void EnsureAllowed(string toolName, IReadOnlyList<ResourceAccess> accesses)
    {
        if (accesses.Count == 0)
        {
            throw new UnauthorizedAccessException($"Tool '{toolName}' requires a VAFS resource declaration.");
        }

        foreach (var access in accesses)
        {
            if (access?.Resource is not { } resource || !resource.Value.StartsWith(VafsPrefix, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException($"Tool '{toolName}' declared an unsupported resource.");
            }

            var path = resource.Value[VafsPrefix.Length..];
            if (!IsSupported(access.Action, path, access.IncludeDescendants))
            {
                throw new UnauthorizedAccessException($"Tool '{toolName}' declared an unsupported VAFS access.");
            }

            if (!_permissions.Any(permission => permission.Action == access.Action
                && (!access.IncludeDescendants || permission.IncludeDescendants)
                && (permission.VirtualPath == path || (permission.IncludeDescendants
                    && IsSameOrBelow(path, permission.VirtualPath, StringComparison.Ordinal)))))
            {
                throw new UnauthorizedAccessException($"Tool '{toolName}' is not allowed to {access.Action} '{path}'.");
            }

            if (!IsSyntheticRoot(path))
            {
                var writable = access.Action == ResourceAction.Write;
                var physicalPath = ResolvePinnedPath(path, writable);
                if (writable && !Directory.Exists(Path.GetDirectoryName(physicalPath)))
                {
                    throw new UnauthorizedAccessException($"Restricted VAFS writes require an existing parent directory for '{path}'.");
                }
            }
        }
    }

    private string ResolvePinnedPath(string path, bool requireWritable)
    {
        var mount = _mounts.FirstOrDefault(mount => IsSameOrBelow(path, mount.VirtualPath, StringComparison.Ordinal));
        if (mount.VirtualPath is null)
        {
            throw new VirtualPathException($"Restricted VAFS path '{path}' has no mount.");
        }

        var relative = path[mount.VirtualPath.Length..].TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var expected = Path.Combine(mount.PhysicalPath, relative);
        var resolved = _view.ResolveUnaliasedHostPath(path, requireWritable);
        if (!string.Equals(expected, resolved, StringComparison.Ordinal))
        {
            throw new VirtualPathException($"The mount for restricted VAFS path '{path}' changed.");
        }

        return resolved;
    }

    private static bool IsSupported(ResourceAction action, string path, bool includeDescendants) =>
        (action == ResourceAction.Read || action == ResourceAction.Write)
        && IsCanonicalPath(path)
        && (!IsSyntheticRoot(path) || (action == ResourceAction.Read && !includeDescendants));

    private static bool IsCanonicalPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Any(character => character == ':' || char.IsControl(character)))
        {
            return false;
        }

        try
        {
            return ResourceUri.Vafs(path).Value == VafsPrefix + path
                && (!OperatingSystem.IsWindows() || !path.Split('/').Skip(1).Any(IsAmbiguousWindowsName));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsAmbiguousWindowsName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        var stem = name.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        return name.EndsWith('.') || char.IsWhiteSpace(name[^1])
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[^1]));
    }

    private static bool IsSyntheticRoot(string path) => path is "/" or "/volumes";

    private static string ComparableHostPath(string path) => path.Replace(Path.DirectorySeparatorChar, '/').Normalize();

    private static bool PathsOverlap(string first, string second, StringComparison comparison) =>
        IsSameOrBelow(first, second, comparison) || IsSameOrBelow(second, first, comparison);

    private static bool IsSameOrBelow(string path, string parent, StringComparison comparison) =>
        string.Equals(path, parent, comparison)
        || (path.Length > parent.Length && path.StartsWith(parent, comparison)
            && (parent.EndsWith('/') || path[parent.Length] == '/'));
}
