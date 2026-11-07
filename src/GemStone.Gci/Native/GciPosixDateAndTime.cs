using System.Runtime.InteropServices;

namespace GemStone.Gci.Native;

/// <summary>
/// Mirror of the C++ class <c>GciPosixDateAndTime</c> (gcicmn.ht): <c>int64 microseconds; int offset;</c>
/// — 16 bytes with padding.
/// </summary>
/// <remarks>
/// The header documents the epoch inconsistently ("UTC since 1970" on the struct, "since
/// January 1, 1900" on GciNewDateAndTimePosix). Verified against a 3.7.5 stone: both
/// GciNewDateAndTimePosix and GciFetchDateAndTimePosix use the Unix epoch (1970-01-01 UTC);
/// <c>GciDateAndTimeTests</c> guards this via <c>asPosixSeconds</c>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct GciPosixDateAndTime
{
    public static readonly DateTime PosixEpoch = DateTime.UnixEpoch;

    public long Microseconds;
    public int OffsetSeconds;
}
