namespace Auxim.VAFS;

public sealed class VirtualPathException : InvalidOperationException
{
    public VirtualPathException(string message)
        : this(message, isReadOnlyMount: false)
    {
    }

    internal VirtualPathException(string message, bool isReadOnlyMount)
        : base(message)
    {
        IsReadOnlyMount = isReadOnlyMount;
    }

    internal bool IsReadOnlyMount { get; }
}
