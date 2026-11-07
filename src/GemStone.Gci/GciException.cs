using GemStone.Gci.Native;

namespace GemStone.Gci;

/// <summary>A GemStone error reported through <c>GciErr</c>.</summary>
/// <remarks>
/// <see cref="Context"/>, <see cref="ExceptionObj"/> and <see cref="Args"/> are informational:
/// for non-fatal errors the session has already cleared the stack and released these OOPs, so
/// they must not be used for further GCI calls.
/// </remarks>
public sealed class GciException : Exception
{
    public int Number { get; }
    public bool IsFatal { get; }
    public string Reason { get; }
    public ulong Context { get; }
    public ulong ExceptionObj { get; }
    public IReadOnlyList<ulong> Args { get; }

    public GciException(string message) : base(message)
    {
        Reason = string.Empty;
        Args = Array.Empty<ulong>();
    }

    internal GciException(in GciErrSType err)
        : base($"GemStone error {err.Number}: {err.Message}")
    {
        Number = err.Number;
        IsFatal = err.IsFatal;
        Reason = err.Reason;
        Context = err.Context;
        ExceptionObj = err.ExceptionObj;
        Args = err.GetArgs();
    }
}
