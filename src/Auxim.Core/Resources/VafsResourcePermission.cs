namespace Auxim.Core.Resources;

/// <summary>Allows a VAFS read or write at a canonical virtual path, optionally including its descendants.</summary>
public sealed record VafsResourcePermission(ResourceAction Action, string VirtualPath, bool IncludeDescendants = false);
